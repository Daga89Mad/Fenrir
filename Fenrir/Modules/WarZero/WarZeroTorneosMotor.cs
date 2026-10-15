// ─────────────────────────────────────────────────────────────────────────────
// WarZeroTorneosMotor.cs
//
// MODO TORNEO · FASE 2: el motor que hace avanzar un torneo en curso.
//
//   1) SALAS. Llegado `inicioTorneo`, cada grupo de la ronda en curso recibe su
//      sala real, ya con sus jugadores dentro:
//        · partida → Partidas/{salaId}  (privada, contraseña aleatoria,
//                    rellenarBots si faltan jugadores, torneoId/torneoGrupoId)
//        · duelo   → Duelos/{salaId}    (privada, los dos jugadores dentro)
//      El jugador entra desde la pantalla del torneo, elige ejército y la sala
//      arranca sola (auto-inicio del orquestador al estar llena y todos listos).
//
//   2) PLAZO DE ARRANQUE (`plazoArranqueHoras`, 24 h por defecto). Si la sala
//      sigue en espera pasado el plazo:
//        · partida → se quita a quien no eligió ejército (0 PC) y los bots
//                    rellenan; si no queda ningún humano listo, 0 PC para todos.
//        · duelo   → si los dos están listos se inicia; si no, 0 PC a ambos.
//
//   3) RESULTADOS. Al terminar la sala se guardan los PC de cada jugador en el
//      grupo (`resultados`). Lo dispara el reparto de recompensas de la partida
//      (WarZeroRecompensas) o del duelo (WarZeroDuelos) y, como respaldo, el
//      propio motor al revisar la sala. Idempotente: un grupo solo se cierra una
//      vez.
//
//   4) CIERRE DE RONDA. Con todos los grupos cerrados se suma el PC de cada
//      jugador en la ronda (quien "descansa" en un duelo impar recibe la media
//      de PC de esa partida), se ordena (PC de la ronda → PC acumulado → azar),
//      se guarda en Torneos/{id}/Clasificacion/{ronda} y:
//        · pasan los `clasifican` primeros → sorteo de la ronda siguiente;
//        · en la última ronda (o si pasan menos de 2) → clasificación final y
//          reparto de premios (EntregarPremiosAsync).
//      El editor puede FORZAR el cierre: los grupos sin terminar cuentan con el
//      PC que lleven en ese momento.
//
// Disparadores: barrido caro del orquestador (BotOrchestratorService), consulta
// del torneo vigente, cierre de cada sala y botones del editor.
// ─────────────────────────────────────────────────────────────────────────────

using System.Collections.Concurrent;
using Google.Cloud.Firestore;
using Microsoft.Extensions.Logging.Abstractions;

public static class TorneoMotor
{
    private const string Col = WarZeroTorneosExtensions.Coleccion;
    private const string SubGrupos = WarZeroTorneosExtensions.SubGrupos;
    private const string SubInscritos = WarZeroTorneosExtensions.SubInscritos;
    private const string SubClasificacion = "Clasificacion";

    // Un solo procesado a la vez por torneo; si llega otro mientras tanto se
    // marca "sucio" y se repite al terminar.
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();
    private static readonly ConcurrentDictionary<string, bool> _sucio = new();

    // ═════════════════════════════════════════════════════════════════════════
    // MODELO DE GRUPO
    // ═════════════════════════════════════════════════════════════════════════

    internal sealed record GrupoT(
        string Id, int Ronda, int Partida, int Grupo, string Tipo, string Modo, int Capacidad,
        List<string> Jugadores, List<string> Alias, bool Descansa, string Estado,
        string SalaId, DateTime SalaCreada, Dictionary<string, int> Resultados);

    internal static GrupoT LeerGrupo(DocumentSnapshot d)
    {
        var m = d.ToDictionary();
        int I(string k) => m.TryGetValue(k, out var v) && v != null ? Convert.ToInt32(v) : 0;
        string S(string k) => m.TryGetValue(k, out var v) ? v?.ToString() ?? "" : "";
        List<string> L(string k) => m.TryGetValue(k, out var v) && v is IEnumerable<object> l
            ? l.Select(x => x?.ToString() ?? "").ToList() : new List<string>();

        var res = new Dictionary<string, int>();
        if (m.TryGetValue("resultados", out var rv) && rv is IDictionary<string, object> rd)
            foreach (var kv in rd) res[kv.Key] = kv.Value == null ? 0 : Convert.ToInt32(kv.Value);

        return new GrupoT(d.Id, I("ronda"), I("partida"), I("grupo"),
            S("tipo") == "" ? "partida" : S("tipo"), S("modo") == "" ? "normal" : S("modo"),
            I("capacidad"), L("jugadores"), L("alias"),
            m.TryGetValue("descansa", out var de) && de is bool b && b,
            S("estado"), S("salaId"),
            m.TryGetValue("salaCreada", out var sc) && sc is Timestamp ts ? ts.ToDateTime() : DateTime.MinValue,
            res);
    }

    private static DocumentReference GrupoRef(FirestoreDb db, string torneoId, string grupoId) =>
        db.Collection(Col).Document(torneoId).Collection(SubGrupos).Document(grupoId);

    private static DocumentReference SalaRef(FirestoreDb db, GrupoT g) =>
        db.Collection(g.Tipo == "duelo" ? "Duelos" : "Partidas").Document(g.SalaId);

    // ═════════════════════════════════════════════════════════════════════════
    // PROCESADO
    // ═════════════════════════════════════════════════════════════════════════

    /// Procesa todos los torneos en curso (lo llama el orquestador).
    public static async Task ProcesarTodosAsync(FirestoreDb db, CancellationToken ct = default)
    {
        var snap = await db.Collection(Col).WhereEqualTo("estado", "en_curso").GetSnapshotAsync(ct);
        foreach (var d in snap.Documents)
        {
            ct.ThrowIfCancellationRequested();
            await ProcesarTorneoAsync(db, d.Id);
        }
    }

    /// Hace avanzar un torneo: crea salas, revisa su estado, aplica el plazo de
    /// arranque y cierra la ronda cuando todos sus grupos han terminado.
    public static async Task ProcesarTorneoAsync(FirestoreDb db, string torneoId)
    {
        if (string.IsNullOrWhiteSpace(torneoId)) return;
        var sem = _locks.GetOrAdd(torneoId, _ => new SemaphoreSlim(1, 1));
        if (!await sem.WaitAsync(0))
        {
            _sucio[torneoId] = true; // lo repetirá quien tiene el turno
            return;
        }
        try
        {
            // Hasta 4 pasadas: cerrar una ronda sortea la siguiente y hay que
            // crear sus salas; además repite si alguien pidió procesar mientras.
            for (var pasada = 0; pasada < 4; pasada++)
            {
                _sucio.TryRemove(torneoId, out _);
                bool avanzo;
                try { avanzo = await ProcesarCoreAsync(db, torneoId); }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[WZ][torneo] procesar {torneoId} falló: {ex}");
                    break;
                }
                if (!avanzo && !_sucio.ContainsKey(torneoId)) break;
            }
        }
        finally { sem.Release(); }
    }

    /// Una pasada. Devuelve true si se cerró una ronda (conviene repetir).
    private static async Task<bool> ProcesarCoreAsync(FirestoreDb db, string torneoId)
    {
        var tRef = db.Collection(Col).Document(torneoId);
        var snap = await tRef.GetSnapshotAsync();
        if (!snap.Exists) return false;
        var t = WarZeroTorneosExtensions.LeerTorneo(snap);
        if (t.Estado != "en_curso" || t.RondaActual <= 0 || t.PremiosEntregados) return false;

        var r = t.RondaActual;
        var gs = await tRef.Collection(SubGrupos).WhereEqualTo("ronda", r).GetSnapshotAsync();
        if (gs.Count == 0) return false;

        var empezado = DateTime.UtcNow >= t.InicioTorneo;
        var todosTerminados = true;

        foreach (var d in gs.Documents)
        {
            var g = LeerGrupo(d);
            if (g.Descansa || g.Estado == "terminada") continue;
            if (!empezado) { todosTerminados = false; continue; }

            bool terminado;
            try { terminado = await AvanzarGrupoAsync(db, t, g); }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[WZ][torneo] grupo {torneoId}/{g.Id} falló: {ex.Message}");
                terminado = false;
            }
            if (!terminado) todosTerminados = false;
        }

        if (!todosTerminados) return false;
        return await CerrarRondaAsync(db, torneoId, r, forzar: false);
    }

    /// Avanza un grupo. Devuelve true si el grupo ha quedado terminado.
    private static async Task<bool> AvanzarGrupoAsync(FirestoreDb db, WarZeroTorneosExtensions.TorneoT t, GrupoT g)
    {
        var gRef = GrupoRef(db, t.Id, g.Id);

        // 1) Sin sala: reservar id y crearla.
        if (g.SalaId == "")
        {
            var salaId = await db.RunTransactionAsync(async tx =>
            {
                var s = await tx.GetSnapshotAsync(gRef);
                var actual = s.TryGetValue<string>("salaId", out var sid) ? sid ?? "" : "";
                if (actual != "") return "";
                var nuevo = db.Collection(g.Tipo == "duelo" ? "Duelos" : "Partidas").Document().Id;
                tx.Update(gRef, new Dictionary<string, object>
                {
                    ["salaId"] = nuevo,
                    ["salaCreada"] = Timestamp.GetCurrentTimestamp(),
                });
                return nuevo;
            });
            if (salaId == "") return false; // otro la reservó
            await CrearSalaAsync(db, t, g with { SalaId = salaId });
            return false;
        }

        // 2) Con sala: revisar su estado.
        var salaRef = SalaRef(db, g);
        var salaSnap = await salaRef.GetSnapshotAsync();
        if (!salaSnap.Exists)
        {
            if (g.Estado == "pendiente")
            {
                // La creación falló o se borró antes de empezar: se recrea.
                await CrearSalaAsync(db, t, g);
                return false;
            }
            // Desapareció ya empezada: se cierra con lo que haya (0 PC).
            return await RegistrarResultadoAsync(db, t.Id, g.Id, new(), procesar: false);
        }

        var sala = g.Tipo == "duelo"
            ? DuelosSalas.Normalizar(salaSnap.ToDictionary())
            : M.Map(M.FromFs(salaSnap.ToDictionary()));
        var estado = M.Str(M.Get(sala, "estado"));

        if (estado == "finalizada")
            return await RegistrarResultadoAsync(db, t.Id, g.Id, PcDeSala(g, sala), procesar: false);

        if (estado == "en_curso")
        {
            if (g.Estado != "jugando")
                await gRef.UpdateAsync("estado", "jugando");
            return false;
        }

        // En espera: plazo de arranque.
        var plazo = TimeSpan.FromHours(Math.Max(1, t.PlazoArranqueHoras));
        var desde = g.SalaCreada > t.InicioTorneo ? g.SalaCreada : t.InicioTorneo;
        if (DateTime.UtcNow > desde + plazo)
            return await ForzarArranqueAsync(db, t, g, salaRef);
        return false;
    }

    // ═════════════════════════════════════════════════════════════════════════
    // SALAS
    // ═════════════════════════════════════════════════════════════════════════

    private static async Task CrearSalaAsync(FirestoreDb db, WarZeroTorneosExtensions.TorneoT t, GrupoT g)
    {
        var pass = Guid.NewGuid().ToString("N")[..12];
        var nombre = $"🏆 {t.Nombre} · R{g.Ronda} P{g.Partida}" + (g.Grupo > 0 ? $" G{g.Grupo}" : "");
        string Alias(int i) => i < g.Alias.Count && g.Alias[i] != "" ? g.Alias[i] : "Jugador";

        if (g.Tipo == "duelo")
        {
            if (g.Jugadores.Count < 2) return;
            var (mapaId, mapaNombre) = await MapaDueloAsync(db);
            if (mapaId == null)
            {
                Console.Error.WriteLine($"[WZ][torneo] {t.Id}/{g.Id}: no hay ningún mapa válido para duelos");
                return;
            }
            var doc = DuelosSalas.NuevaSala(g.Jugadores[0], Alias(0), nombre, true, pass,
                mapaId, mapaNombre, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), t.ModoTurno);
            DuelosSalas.Unirse(doc, g.Jugadores[1], Alias(1), pass);
            doc["torneoId"] = t.Id;
            doc["torneoGrupoId"] = g.Id;
            doc["torneoNombre"] = t.Nombre;
            await db.Collection("Duelos").Document(g.SalaId).SetAsync(doc);
        }
        else
        {
            var cap = Math.Max(g.Capacidad, g.Jugadores.Count);
            var jugadores = g.Jugadores.Select((u, i) => (object)new Dictionary<string, object?>
            {
                ["uid"] = u,
                ["alias"] = Alias(i),
                ["ejercitoId"] = null,
                ["listo"] = false,
            }).ToList();

            var data = new Dictionary<string, object?>
            {
                ["nombre"] = nombre,
                ["hostUid"] = g.Jugadores[0],
                ["esPrivada"] = true,
                ["contrasena"] = pass,
                ["maxJugadores"] = cap,
                ["jugadores"] = jugadores,
                ["participantes"] = g.Jugadores.Cast<object>().ToList(),
                ["estado"] = "esperando",
                ["creadoEn"] = Timestamp.GetCurrentTimestamp(),
                ["modoTurno"] = t.ModoTurno,
                ["modoPartida"] = g.Modo,
                ["turnoActual"] = 1,
                ["cerradoPor"] = new List<object>(),
                ["statsPartida"] = new Dictionary<string, object>(),
                ["jugadoresEliminados"] = new List<object>(),
                // Si faltan jugadores, los bots rellenan los huecos ya.
                ["rellenarBots"] = g.Jugadores.Count < cap,
                ["torneoId"] = t.Id,
                ["torneoGrupoId"] = g.Id,
                ["torneoNombre"] = t.Nombre,
            };
            await db.Collection("Partidas").Document(g.SalaId).SetAsync(data);
        }

        Console.WriteLine($"[WZ][torneo] {t.Id}: sala {g.SalaId} ({g.Tipo}) creada para {g.Id}");
    }

    private static (string? id, string nombre) _mapaDuelo;

    /// Primer mapa válido para duelos (se cachea).
    private static async Task<(string? id, string nombre)> MapaDueloAsync(FirestoreDb db)
    {
        if (_mapaDuelo.id != null) return _mapaDuelo;
        var snap = await db.Collection("Mapas").GetSnapshotAsync();
        foreach (var d in snap.Documents.OrderBy(x => x.Id))
        {
            var mapa = DuelosSalas.Normalizar(d.ToDictionary());
            if (M.Get(mapa, "seleccionable") is bool sel && !sel) continue;
            if (DuelosSalas.BloqueadasDesdeMapa(mapa, out _) == null) continue;
            var nombre = M.Str(M.Get(mapa, "nombre"));
            _mapaDuelo = (d.Id, nombre == "" ? d.Id : nombre);
            return _mapaDuelo;
        }
        return (null, "");
    }

    /// PC de cada jugador del grupo según el estado actual de la sala.
    private static Dictionary<string, int> PcDeSala(GrupoT g, Dictionary<string, object?> sala)
    {
        var res = new Dictionary<string, int>();
        if (g.Tipo == "duelo")
        {
            var juego = M.Get(sala, "juego");
            if (juego == null) return res;
            var e = DuelosSerializacion.DesdeMapa(juego);
            var jugadores = DuelosSalas.Jugadores(sala);
            for (var k = 0; k < jugadores.Count && k < e.Jugadores.Length; k++)
                res[M.Str(M.Get(jugadores[k], "uid"))] = e.Jugadores[k].Pc;
            return res;
        }
        var stats = M.Map(M.Get(sala, "statsPartida"));
        foreach (var uid in g.Jugadores)
            res[uid] = stats.TryGetValue(uid, out var s) ? M.Int(M.Get(M.Map(s), "pc")) : 0;
        return res;
    }

    /// Plazo vencido con la sala en espera. Devuelve true si el grupo termina.
    private static async Task<bool> ForzarArranqueAsync(
        FirestoreDb db, WarZeroTorneosExtensions.TorneoT t, GrupoT g, DocumentReference salaRef)
    {
        if (g.Tipo == "duelo")
        {
            var snap = await salaRef.GetSnapshotAsync();
            if (!snap.Exists) return false;
            var doc = DuelosSalas.Normalizar(snap.ToDictionary());
            if (M.Str(M.Get(doc, "estado")) != "esperando") return false;
            var jugadores = DuelosSalas.Jugadores(doc);
            var listos = jugadores.Count == 2 && jugadores.All(j => M.Bool(M.Get(j, "listo")));
            if (listos)
            {
                bool[]? bloq = null;
                var mapaId = M.Str(M.Get(doc, "mapaId"));
                if (mapaId != "")
                {
                    var mapa = await db.Collection("Mapas").Document(mapaId).GetSnapshotAsync();
                    if (mapa.Exists) bloq = DuelosSalas.BloqueadasDesdeMapa(DuelosSalas.Normalizar(mapa.ToDictionary()), out _);
                }
                var error = DuelosSalas.Iniciar(doc, M.Str(M.Get(doc, "hostUid")), bloq, new Random(),
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                if (error == null)
                {
                    await salaRef.SetAsync(doc);
                    await GrupoRef(db, t.Id, g.Id).UpdateAsync("estado", "jugando");
                    Console.WriteLine($"[WZ][torneo] {t.Id}/{g.Id}: duelo iniciado por plazo");
                    return false;
                }
                Console.Error.WriteLine($"[WZ][torneo] {t.Id}/{g.Id}: no se pudo iniciar el duelo: {error}");
            }
            // Incomparecencia: el duelo no se juega (0 PC para los dos).
            await salaRef.DeleteAsync();
            Console.WriteLine($"[WZ][torneo] {t.Id}/{g.Id}: duelo anulado por incomparecencia");
            return await RegistrarResultadoAsync(db, t.Id, g.Id, new(), procesar: false);
        }

        // Partida: fuera quien no eligió ejército, los bots rellenan.
        var resultado = await db.RunTransactionAsync(async tx =>
        {
            var s = await tx.GetSnapshotAsync(salaRef);
            if (!s.Exists) return "nada";
            var d = M.Map(M.FromFs(s.ToDictionary()));
            if (M.Str(M.Get(d, "estado")) != "esperando") return "nada";

            var bots = M.List(M.Get(d, "botsUids")).Select(M.Str).ToHashSet();
            bool EsBot(string u) => bots.Contains(u) || u.StartsWith("bot_");
            var jugadores = M.List(M.Get(d, "jugadores")).Select(M.Map).ToList();
            var quedan = jugadores.Where(j => EsBot(M.Str(M.Get(j, "uid"))) || M.Bool(M.Get(j, "listo"))).ToList();
            var humanos = quedan.Where(j => !EsBot(M.Str(M.Get(j, "uid")))).ToList();
            if (humanos.Count == 0)
            {
                tx.Delete(salaRef);
                return "vacia";
            }
            var fuera = jugadores.Except(quedan).Select(j => M.Str(M.Get(j, "uid"))).ToHashSet();
            var participantes = M.List(M.Get(d, "participantes")).Select(M.Str).Where(u => !fuera.Contains(u)).ToList();
            var host = M.Str(M.Get(d, "hostUid"));
            if (fuera.Contains(host)) host = M.Str(M.Get(humanos[0], "uid"));

            tx.Update(salaRef, new Dictionary<string, object>
            {
                ["jugadores"] = quedan.Cast<object>().ToList(),
                ["participantes"] = participantes.Cast<object>().ToList(),
                ["hostUid"] = host,
                ["rellenarBots"] = true,
            });
            return fuera.Count > 0 ? "recortada" : "rellenar";
        });

        Console.WriteLine($"[WZ][torneo] {t.Id}/{g.Id}: plazo de arranque vencido → {resultado}");
        if (resultado == "vacia")
            return await RegistrarResultadoAsync(db, t.Id, g.Id, new(), procesar: false);
        return false;
    }

    // ═════════════════════════════════════════════════════════════════════════
    // RESULTADOS
    // ═════════════════════════════════════════════════════════════════════════

    /// Cierra un grupo con los PC de cada jugador (0 a quien falte).
    /// Devuelve true si este llamada lo cerró (o ya estaba cerrado).
    public static async Task<bool> RegistrarResultadoAsync(
        FirestoreDb db, string torneoId, string grupoId, Dictionary<string, int> pcs, bool procesar = true)
    {
        var gRef = GrupoRef(db, torneoId, grupoId);
        var cerrado = await db.RunTransactionAsync(async tx =>
        {
            var s = await tx.GetSnapshotAsync(gRef);
            if (!s.Exists) return false;
            var g = LeerGrupo(s);
            if (g.Estado == "terminada") return false;
            var res = g.Jugadores.ToDictionary(u => u, u => (object)(pcs.TryGetValue(u, out var v) ? v : 0));
            tx.Update(gRef, new Dictionary<string, object>
            {
                ["resultados"] = res,
                ["estado"] = "terminada",
                ["terminado"] = Timestamp.GetCurrentTimestamp(),
            });
            return true;
        });

        if (cerrado)
            Console.WriteLine($"[WZ][torneo] {torneoId}/{grupoId}: resultados " +
                              string.Join(", ", pcs.Select(kv => $"{kv.Key}={kv.Value}")));
        if (cerrado && procesar) await ProcesarTorneoAsync(db, torneoId);
        return true;
    }

    /// Gancho: una partida de torneo ha terminado (WarZeroRecompensas).
    public static async Task RegistrarDesdePartidaAsync(FirestoreDb db, Dictionary<string, object?> datosLobby)
    {
        var torneoId = M.Str(M.Get(datosLobby, "torneoId"));
        var grupoId = M.Str(M.Get(datosLobby, "torneoGrupoId"));
        if (torneoId == "" || grupoId == "") return;
        var stats = M.Map(M.Get(datosLobby, "statsPartida"));
        var pcs = stats.ToDictionary(kv => kv.Key, kv => M.Int(M.Get(M.Map(kv.Value), "pc")));
        await RegistrarResultadoAsync(db, torneoId, grupoId, pcs);
    }

    /// Gancho: un duelo de torneo ha terminado (WarZeroDuelos).
    public static async Task RegistrarDesdeDueloAsync(FirestoreDb db, string dueloId, IEnumerable<DuelosRecompensa> lista)
    {
        var snap = await db.Collection("Duelos").Document(dueloId).GetSnapshotAsync();
        if (!snap.Exists) return;
        var doc = DuelosSalas.Normalizar(snap.ToDictionary());
        var torneoId = M.Str(M.Get(doc, "torneoId"));
        var grupoId = M.Str(M.Get(doc, "torneoGrupoId"));
        if (torneoId == "" || grupoId == "") return;
        await RegistrarResultadoAsync(db, torneoId, grupoId, lista.ToDictionary(r => r.Uid, r => r.Pc));
    }

    // ═════════════════════════════════════════════════════════════════════════
    // CLASIFICACIÓN DE RONDA
    // ═════════════════════════════════════════════════════════════════════════

    internal sealed record FilaT(string Uid, string Alias, int Pc, int Jugadas, int Total);

    /// Suma de PC de la ronda por jugador. Solo cuentan grupos terminados.
    /// Quien descansa recibe la media de PC de esa partida (si ya hay datos).
    internal static List<FilaT> TablaRonda(IEnumerable<GrupoT> grupos)
    {
        var lista = grupos.ToList();
        var filas = new Dictionary<string, (string alias, int pc, int jugadas, int total)>();
        void Sumar(string uid, string alias, int pc, bool jugada)
        {
            filas.TryGetValue(uid, out var f);
            if (string.IsNullOrEmpty(f.alias)) f.alias = alias;
            f.total++;
            if (jugada) { f.pc += pc; f.jugadas++; }
            filas[uid] = f;
        }

        var medias = lista.Where(g => !g.Descansa && g.Estado == "terminada")
            .GroupBy(g => g.Partida)
            .ToDictionary(x => x.Key, x =>
            {
                var v = x.SelectMany(g => g.Resultados.Values).ToList();
                return v.Count == 0 ? 0 : (int)Math.Round(v.Average());
            });

        foreach (var g in lista)
        {
            for (var i = 0; i < g.Jugadores.Count; i++)
            {
                var uid = g.Jugadores[i];
                var alias = i < g.Alias.Count ? g.Alias[i] : "";
                if (g.Descansa)
                    Sumar(uid, alias, medias.TryGetValue(g.Partida, out var m) ? m : 0, medias.ContainsKey(g.Partida));
                else
                    Sumar(uid, alias, g.Resultados.TryGetValue(uid, out var pc) ? pc : 0, g.Estado == "terminada");
            }
        }
        return filas.Select(kv => new FilaT(kv.Key, kv.Value.alias, kv.Value.pc, kv.Value.jugadas, kv.Value.total)).ToList();
    }

    /// Cierra la ronda [r]: clasificación, sorteo de la siguiente o final con
    /// premios. Con [forzar] cierra antes los grupos sin terminar con su PC
    /// actual. Devuelve true si la ronda se cerró en esta llamada.
    public static async Task<bool> CerrarRondaAsync(FirestoreDb db, string torneoId, int r, bool forzar)
    {
        var tRef = db.Collection(Col).Document(torneoId);
        var tSnap = await tRef.GetSnapshotAsync();
        if (!tSnap.Exists) return false;
        var t = WarZeroTorneosExtensions.LeerTorneo(tSnap);
        if (t.RondaActual != r || t.Estado != "en_curso") return false;

        async Task<List<GrupoT>> Grupos() =>
            (await tRef.Collection(SubGrupos).WhereEqualTo("ronda", r).GetSnapshotAsync())
            .Documents.Select(LeerGrupo).ToList();

        var grupos = await Grupos();
        var pendientes = grupos.Where(g => !g.Descansa && g.Estado != "terminada").ToList();
        if (pendientes.Count > 0)
        {
            if (!forzar) return false;
            foreach (var g in pendientes)
            {
                var pcs = new Dictionary<string, int>();
                if (g.SalaId != "")
                {
                    try
                    {
                        var s = await SalaRef(db, g).GetSnapshotAsync();
                        if (s.Exists)
                            pcs = PcDeSala(g, g.Tipo == "duelo"
                                ? DuelosSalas.Normalizar(s.ToDictionary())
                                : M.Map(M.FromFs(s.ToDictionary())));
                    }
                    catch { /* 0 PC */ }
                }
                await RegistrarResultadoAsync(db, torneoId, g.Id, pcs, procesar: false);
            }
            grupos = await Grupos();
        }

        // Reserva atómica del cierre.
        var reservado = await db.RunTransactionAsync(async tx =>
        {
            var s = await tx.GetSnapshotAsync(tRef);
            var tt = WarZeroTorneosExtensions.LeerTorneo(s);
            if (tt.RondaActual != r || tt.Estado != "en_curso") return false;
            if (s.TryGetValue<List<object>>("rondasCerradas", out var rc) && rc.Any(x => Convert.ToInt32(x) == r))
                return false;
            tx.Update(tRef, new Dictionary<string, object>
            {
                ["rondasCerradas"] = FieldValue.ArrayUnion(r),
                ["actualizado"] = Timestamp.GetCurrentTimestamp(),
            });
            return true;
        });
        if (!reservado) return false;

        // Tabla y desempates (PC de la ronda → PC acumulado → azar).
        var insSnap = await tRef.Collection(SubInscritos).GetSnapshotAsync();
        var acumulado = insSnap.Documents.ToDictionary(d => d.Id,
            d => d.TryGetValue<long>("pcTotal", out var v) ? v : 0L);
        var azar = new Random();
        var tabla = TablaRonda(grupos)
            .Select(f => (f, desempate: azar.Next()))
            .OrderByDescending(x => x.f.Pc)
            .ThenByDescending(x => acumulado.TryGetValue(x.f.Uid, out var a) ? a : 0)
            .ThenBy(x => x.desempate)
            .Select(x => x.f)
            .ToList();

        var ronda = t.Rondas[r - 1];
        var ultima = r >= t.Rondas.Count;
        var pasan = ultima ? 0 : Math.Min(ronda.Clasifican, tabla.Count);

        var batch = db.StartBatch();
        batch.Set(tRef.Collection(SubClasificacion).Document(r.ToString()), new Dictionary<string, object>
        {
            ["ronda"] = r,
            ["clasifican"] = pasan,
            ["final"] = ultima || pasan < 2,
            ["cerrada"] = Timestamp.GetCurrentTimestamp(),
            ["forzada"] = forzar && pendientes.Count > 0,
            ["filas"] = tabla.Select((f, i) => (object)new Dictionary<string, object>
            {
                ["puesto"] = i + 1,
                ["uid"] = f.Uid,
                ["alias"] = f.Alias,
                ["pc"] = f.Pc,
                ["jugadas"] = f.Jugadas,
                ["total"] = f.Total,
                ["clasificado"] = i < pasan,
            }).ToList(),
        });
        for (var i = 0; i < tabla.Count; i++)
        {
            var f = tabla[i];
            var campos = new Dictionary<string, object>
            {
                ["pcTotal"] = FieldValue.Increment(f.Pc),
                ["pcRonda"] = new Dictionary<string, object> { [$"r{r}"] = f.Pc },
            };
            if (!ultima && i >= pasan) campos["eliminadoEnRonda"] = r;
            batch.Set(tRef.Collection(SubInscritos).Document(f.Uid), campos, SetOptions.MergeAll);
        }
        await batch.CommitAsync();
        Console.WriteLine($"[WZ][torneo] {torneoId}: ronda {r} cerrada ({tabla.Count} jugadores, pasan {pasan})");

        if (!ultima && pasan >= 2)
        {
            var clasificados = tabla.Take(pasan).Select(f => (f.Uid, f.Alias)).ToList();
            await SortearRondaAsync(db, t, r + 1, clasificados);
            return true;
        }

        // FINAL: esta ronda primero y luego los eliminados, de la ronda más
        // reciente a la más antigua, en su orden de clasificación.
        var orden = tabla.Select(f => f.Uid).ToList();
        for (var k = r - 1; k >= 1; k--)
        {
            var c = await tRef.Collection(SubClasificacion).Document(k.ToString()).GetSnapshotAsync();
            if (!c.Exists || !c.TryGetValue<List<object>>("filas", out var filas)) continue;
            foreach (var fo in filas.OfType<IDictionary<string, object>>())
            {
                var uid = fo.TryGetValue("uid", out var u) ? u?.ToString() ?? "" : "";
                if (uid != "" && !orden.Contains(uid)) orden.Add(uid);
            }
        }
        await WarZeroTorneosExtensions.EntregarPremiosAsync(db, torneoId, orden, NullLogger.Instance);
        Console.WriteLine($"[WZ][torneo] {torneoId}: FINALIZADO · campeón {orden.FirstOrDefault()}");
        return true;
    }

    // ═════════════════════════════════════════════════════════════════════════
    // SORTEO
    // ═════════════════════════════════════════════════════════════════════════

    /// Sortea los grupos de la ronda [numRonda] con [jugadores] y la marca como
    /// ronda en curso. Sustituye cualquier sorteo previo de esa ronda.
    internal static async Task<int> SortearRondaAsync(
        FirestoreDb db, WarZeroTorneosExtensions.TorneoT t, int numRonda, List<(string uid, string alias)> jugadores)
    {
        var tRef = db.Collection(Col).Document(t.Id);
        var previos = await tRef.Collection(SubGrupos).WhereEqualTo("ronda", numRonda).GetSnapshotAsync();
        foreach (var d in previos.Documents) await d.Reference.DeleteAsync();

        var ronda = t.Rondas[numRonda - 1];
        var batch = db.StartBatch();
        var total = 0;

        for (var p = 0; p < ronda.Partidas.Count; p++)
        {
            var partida = ronda.Partidas[p];
            var duelo = partida.Tipo == "duelo";
            var mezcla = jugadores.OrderBy(_ => Random.Shared.Next()).ToList();
            var (grupos, descansa) = WarZeroTorneosExtensions.Repartir(mezcla, duelo ? 2 : partida.Jugadores, duelo);

            for (var g = 0; g < grupos.Count; g++)
            {
                batch.Set(tRef.Collection(SubGrupos).Document($"{numRonda}_{p + 1}_{g + 1}"), new Dictionary<string, object>
                {
                    ["ronda"] = numRonda,
                    ["partida"] = p + 1,
                    ["grupo"] = g + 1,
                    ["tipo"] = partida.Tipo,
                    ["modo"] = partida.Modo,
                    ["capacidad"] = duelo ? 2 : partida.Jugadores,
                    ["jugadores"] = grupos[g].Select(x => x.uid).ToList(),
                    ["alias"] = grupos[g].Select(x => x.alias).ToList(),
                    ["descansa"] = false,
                    ["estado"] = "pendiente",
                    ["salaId"] = "",
                });
                total++;
            }
            if (descansa != null)
            {
                batch.Set(tRef.Collection(SubGrupos).Document($"{numRonda}_{p + 1}_descansa"), new Dictionary<string, object>
                {
                    ["ronda"] = numRonda,
                    ["partida"] = p + 1,
                    ["grupo"] = 0,
                    ["tipo"] = partida.Tipo,
                    ["modo"] = partida.Modo,
                    ["capacidad"] = 2,
                    ["jugadores"] = new List<string> { descansa.Value.uid },
                    ["alias"] = new List<string> { descansa.Value.alias },
                    ["descansa"] = true,
                    ["estado"] = "descansa",
                    ["salaId"] = "",
                });
            }
        }

        batch.Update(tRef, new Dictionary<string, object>
        {
            ["rondaActual"] = numRonda,
            ["estado"] = "en_curso",
            ["actualizado"] = Timestamp.GetCurrentTimestamp(),
        });
        await batch.CommitAsync();
        Console.WriteLine($"[WZ][torneo] {t.Id}: ronda {numRonda} sorteada ({total} grupos, {jugadores.Count} jugadores)");
        return total;
    }
}
