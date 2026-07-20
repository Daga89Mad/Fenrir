using System.Text.Json;
using Google.Cloud.Firestore;

// Alias de tipo (ámbito de fichero): mismos que en WarZeroLogic.cs. Los alias
// `using` no se heredan entre archivos, por eso hay que repetirlos aquí.
using Tablero = System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<System.Collections.Generic.Dictionary<string, object?>>>;
using EfectosCelda = System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<System.Collections.Generic.Dictionary<string, object?>>>;

// ─────────────────────────────────────────────────────────────────────────────
// WarZeroService.cs
//
// Orquesta el cierre de turno contra Firestore. Toda la operación (registrar el
// cierre de un jugador y, si cerraron todos, resolver el turno) ocurre dentro de
// UNA transacción, lo que elimina la carrera de "quién resuelve" que había en el
// cliente.
//
// Documento Partidas/{lobbyId}:
//   turnoActual, cerradoPor[], movimientosTurno{uid}, tablero{}, statsPartida{},
//   obeliscos{uid->coord}, jugadores[], jugadoresEliminados[], efectosCelda{},
//   rayo{}, mapaId, historialCombates[], estado, ganadorUid
// ─────────────────────────────────────────────────────────────────────────────

public class WarZeroService
{
    private readonly WarZeroFirestore _fs;

    public WarZeroService(WarZeroFirestore fs) => _fs = fs;

    public GameStatus GetStatus() => new("EU-1", 42);

    public async Task<CerrarTurnoResponse> CerrarTurnoAsync(CerrarTurnoRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.LobbyId) || string.IsNullOrWhiteSpace(req.Uid))
            return new CerrarTurnoResponse { Mensaje = "lobbyId y uid son obligatorios" };

        // Convertir el JSON entrante a CLR (mismo formato que en Dart).
        var celdasClr = req.Celdas.ValueKind == JsonValueKind.Object
            ? M.Map(M.FromJson(req.Celdas))
            : new Dictionary<string, object?>();
        var accionesIncoming = req.Acciones.ValueKind == JsonValueKind.Array
            ? M.List(M.FromJson(req.Acciones)).Select(M.Map).ToList()
            : new List<Dictionary<string, object?>>();

        var db = _fs.Db;
        var lobbyRef = db.Collection("Partidas").Document(req.LobbyId);

        var resp = await db.RunTransactionAsync(async tx =>
        {
            var snap = await tx.GetSnapshotAsync(lobbyRef);
            if (!snap.Exists)
                return new CerrarTurnoResponse { Mensaje = "La partida no existe" };

            var data = M.Map(M.FromFs(snap.ToDictionary()));
            var turnoDb = M.Int(M.Get(data, "turnoActual"));

            // Turno desincronizado: otro proceso ya avanzó. No escribimos.
            if (turnoDb != req.Turno)
                return new CerrarTurnoResponse
                {
                    Resuelto = false,
                    TurnoActual = turnoDb,
                    Mensaje = "El turno ya había avanzado",
                };

            // ── Movimiento del jugador que cierra ─────────────────────────────
            var movData = new Dictionary<string, object?>
            {
                ["uid"] = req.Uid,
                ["turno"] = req.Turno,
                ["celdas"] = celdasClr,
                ["timestamp"] = Timestamp.FromDateTime(DateTime.UtcNow),
                ["acciones"] = accionesIncoming.Cast<object?>().ToList(),
            };

            // movimientosTurno con este jugador ya incluido (para mergear / contar).
            var movTurno = M.Map(M.Get(data, "movimientosTurno"));
            movTurno[req.Uid] = movData;

            // cerradoPor con este jugador.
            var cerrado = M.List(M.Get(data, "cerradoPor")).Select(M.Str).Where(s => s != "").ToHashSet();
            cerrado.Add(req.Uid);

            // Jugadores activos.
            var eliminados = M.List(M.Get(data, "jugadoresEliminados")).Select(M.Str).ToHashSet();
            var jugadores = M.List(M.Get(data, "jugadores"))
                .Select(j => M.Str(M.Get(M.Map(j), "uid")))
                .Where(u => u != "").ToList();
            var activos = jugadores.Where(u => !eliminados.Contains(u)).ToList();

            var todosCerraron = activos.Count > 0 && activos.All(u => cerrado.Contains(u));

            // ── Caso 1: aún faltan jugadores → solo registrar el cierre ───────
            if (!todosCerraron)
            {
                tx.Update(lobbyRef, new Dictionary<FieldPath, object>
                {
                    [new FieldPath("movimientosTurno", req.Uid)] = movData,
                    [new FieldPath("cerradoPor")] = FieldValue.ArrayUnion(req.Uid),
                });

                return new CerrarTurnoResponse
                {
                    Resuelto = false,
                    TurnoActual = turnoDb,
                    CerradoPor = cerrado.Count,
                    JugadoresActivos = activos.Count,
                    Faltan = Math.Max(0, activos.Count - cerrado.Count),
                    Mensaje = "Turno cerrado. Esperando a los demás.",
                };
            }

            // ── Caso 2: cerraron todos → RESOLVER el turno ────────────────────
            // Etiqueta de fase: si algo lanza, el catch la añade al mensaje para
            // saber exactamente en qué paso de la resolución ocurrió.
            var fase = "obeliscos";
            try
            {
                var obeliscos = M.Map(M.Get(data, "obeliscos"))
                    .ToDictionary(k => k.Key, v => M.Str(v.Value));

                // Tablero fusionado a partir de los movimientos de ESTE turno.
                fase = "merge-tablero";
                var merged = new Dictionary<string, List<Dictionary<string, object?>>>();
                var acciones = new List<Dictionary<string, object?>>();
                foreach (var kv in movTurno)
                {
                    var mov = M.Map(kv.Value);
                    if (M.Int(M.Get(mov, "turno")) != req.Turno) continue;
                    foreach (var ce in M.Map(M.Get(mov, "celdas")))
                    {
                        if (!merged.TryGetValue(ce.Key, out var lst)) { lst = new(); merged[ce.Key] = lst; }
                        foreach (var c in M.List(ce.Value)) lst.Add(M.Map(c));
                    }
                    acciones.AddRange(M.List(M.Get(mov, "acciones")).Select(M.Map));
                }

                // Efectos de celda previos.
                fase = "efectos-previos";
                var efectosPrevios = ParseEfectosCelda(M.Get(data, "efectosCelda"));

                // Tablero del turno anterior (para revertir a su posición las
                // cartas enemigas que se muevan a una celda escudada este turno).
                fase = "tablero-previo";
                var tableroPrevio = new Dictionary<string, List<Dictionary<string, object?>>>();
                foreach (var kv in M.Map(M.Get(data, "tablero")))
                {
                    var lst = new List<Dictionary<string, object?>>();
                    foreach (var c in M.List(kv.Value)) lst.Add(M.Map(c));
                    tableroPrevio[kv.Key] = lst;
                }

                // 1. Acciones (tele → disparo → veneno).
                fase = "acciones";
                var acc = Habilidades.AplicarAcciones(
                    merged, acciones, efectosPrevios, obeliscos, tableroPrevio);
                // 2. Combates.
                fase = "combate";
                var reso = Combate.Resolver(acc.Tablero, obeliscos);
                // 3. Tick de efectos.
                fase = "tick-efectos";
                var tick = Habilidades.TickEfectos(reso.Tablero, acc.EfectosCelda);
                var tableroFinal = tick.Tablero;
                var efectosFinal = tick.EfectosCelda;

                // 4. Farmeo (solo si el mapa aporta continentes/isla central).
                fase = "farmeo";
                FarmeoResultado? farmeo = null;
                var mapaId = M.Str(M.Get(data, "mapaId"));
                if (mapaId != "")
                {
                    var mapaSnap = await tx.GetSnapshotAsync(db.Collection("Mapas").Document(mapaId));
                    if (mapaSnap.Exists)
                    {
                        var mapData = M.Map(M.FromFs(mapaSnap.ToDictionary()));
                        var continentes = M.Map(M.Get(mapData, "continentes"))
                            .ToDictionary(k => k.Key, v => M.List(v.Value).Select(M.Str).ToList());
                        var islaCentral = M.List(M.Get(mapData, "islaCentral")).Select(M.Str).ToList();
                        if (continentes.Count > 0 || islaCentral.Count > 0)
                        {
                            var rayoActual = snap.ContainsField("rayo") ? M.Map(M.Get(data, "rayo")) : null;
                            farmeo = Farmeo.Calcular(
                                tableroFinal, obeliscos, continentes, islaCentral,
                                rayoActual, Coords.AllCells(jugadores.Count), new Random());
                        }
                    }
                }

                // 5. Acumular stats.
                fase = "stats";
                var stats = new Dictionary<string, Dictionary<string, object?>>();
                foreach (var kv in M.Map(M.Get(data, "statsPartida")))
                {
                    var m = M.Map(kv.Value);
                    var entry = new Dictionary<string, object?>
                    {
                        ["energies"] = M.Int(M.Get(m, "energies")),
                        ["pc"] = M.Int(M.Get(m, "pc")),
                    };
                    // BUG QAS #2: preservar mano / mazoRestante / especialesCompradas.
                    // Antes se reescribía statsPartida SOLO con energies+pc, así que
                    // cada turno se borraban la mano, el mazo restante y las especiales
                    // compradas (el cliente tenía que repoblar la mano robando en el
                    // stream, y las especiales dejaban de estar deshabilitadas). Ahora
                    // se conservan y el reparto de fin de turno se hace aquí (paso 5c).
                    if (m.ContainsKey("mano"))
                        entry["mano"] = M.List(M.Get(m, "mano")).Select(M.Str)
                            .Where(s => s != "").Cast<object?>().ToList();
                    if (m.ContainsKey("mazoRestante"))
                        entry["mazoRestante"] = M.List(M.Get(m, "mazoRestante")).Select(M.Str)
                            .Where(s => s != "").Cast<object?>().ToList();
                    if (m.ContainsKey("especialesCompradas"))
                        entry["especialesCompradas"] = M.List(M.Get(m, "especialesCompradas"))
                            .Select(M.Str).Where(s => s != "").Cast<object?>().ToList();
                    // mazoPool = mazo completo del jugador (IDs, con repetición por
                    // cantidad). Es el pool del que se roba al final de cada turno,
                    // CON repetición y SIN agotarse (igual que el robo del cliente).
                    if (m.ContainsKey("mazoPool"))
                        entry["mazoPool"] = M.List(M.Get(m, "mazoPool"))
                            .Select(M.Str).Where(s => s != "").Cast<object?>().ToList();
                    stats[kv.Key] = entry;
                }
                void EnsureStat(string uid)
                {
                    if (!stats.ContainsKey(uid)) stats[uid] = new() { ["energies"] = 0, ["pc"] = 0 };
                }
                foreach (var kv in reso.EnergiesPorJugador)
                {
                    EnsureStat(kv.Key);
                    stats[kv.Key]["energies"] = M.Int(stats[kv.Key]["energies"]) + kv.Value;
                }
                foreach (var kv in reso.PcPorJugador)
                {
                    EnsureStat(kv.Key);
                    stats[kv.Key]["pc"] = M.Int(stats[kv.Key]["pc"]) + kv.Value;
                }
                if (farmeo != null)
                    foreach (var kv in farmeo.EnergiesPorJugador)
                    {
                        EnsureStat(kv.Key);
                        stats[kv.Key]["energies"] = M.Int(stats[kv.Key]["energies"]) + kv.Value;
                    }

                // 5b. Suerte del perdedor: si un jugador que sigue en partida NO
                // gana energías ESTE turno (ni combate ni farmeo), recibe +3.
                // Se mira lo ganado EN EL TURNO, no el total acumulado.
                fase = "suerte-perdedor";
                var perdedoresEsteTurno = reso.ObeliscosConquistados
                    .Select(c => c.PerdedorUid).ToHashSet();
                var suerteLog = new List<Dictionary<string, object?>>();
                foreach (var uid in activos)
                {
                    if (perdedoresEsteTurno.Contains(uid)) continue;
                    var ganadoTurno = reso.EnergiesPorJugador.GetValueOrDefault(uid)
                        + (farmeo?.EnergiesPorJugador.GetValueOrDefault(uid) ?? 0);
                    if (ganadoTurno != 0) continue;
                    EnsureStat(uid);
                    stats[uid]["energies"] = M.Int(stats[uid]["energies"]) + 3;
                    suerteLog.Add(new Dictionary<string, object?>
                    {
                        ["uid"] = uid,
                        ["zona"] = "",
                        ["totalEnergies"] = 3L,
                        ["detalle"] = new Dictionary<string, object?> { ["suerteDelPerdedor"] = 3L },
                    });
                }

                // farmeoLog final = farmeo del mapa + suerte del perdedor, para
                // que el concepto sea visible en el informe (pestaña ENERGIES).
                var farmeoLogFinal = new List<object?>();
                if (farmeo != null) farmeoLogFinal.AddRange(farmeo.FarmeoLog.Cast<object?>());
                farmeoLogFinal.AddRange(suerteLog.Cast<object?>());

                // 5c. Reparto de fin de turno (server-side). Cada jugador activo
                // que NO quede eliminado roba 1 carta de su mazo completo a su mano.
                // BUG QAS #2: antes esto lo hacía el CLIENTE al ver avanzar el turno
                // en el stream; si el jugador no estaba presente cuando el turno
                // resolvía, nunca robaba ni se persistía → la carta se perdía y no
                // aparecía en el informe. Ahora es autoritativo en el servidor y se
                // registra en repartoLog para que el informe lo muestre siempre.
                fase = "reparto";
                var elimTrasTurno = new HashSet<string>(eliminados);
                foreach (var oc in reso.ObeliscosConquistados) elimTrasTurno.Add(oc.PerdedorUid);
                var repartoLog = new List<Dictionary<string, object?>>();
                var rngReparto = new Random();
                foreach (var uid in activos)
                {
                    if (elimTrasTurno.Contains(uid)) continue;
                    if (!stats.TryGetValue(uid, out var st)) continue;
                    // Pool de robo = mazoPool (mazo completo). Fallback a
                    // mazoRestante para partidas antiguas sin mazoPool. El robo es
                    // CON repetición y NO agota el pool (idéntico al robo del
                    // cliente: "la misma carta puede salir otro turno").
                    var pool = M.List(M.Get(st, "mazoPool")).Select(M.Str)
                        .Where(s => s != "").ToList();
                    if (pool.Count == 0)
                        pool = M.List(M.Get(st, "mazoRestante")).Select(M.Str)
                            .Where(s => s != "").ToList();
                    if (pool.Count == 0) continue;
                    var manoUid = M.List(M.Get(st, "mano")).Select(M.Str)
                        .Where(s => s != "").ToList();
                    var cartaId = pool[rngReparto.Next(pool.Count)];
                    manoUid.Add(cartaId);
                    st["mano"] = manoUid.Cast<object?>().ToList();
                    repartoLog.Add(new Dictionary<string, object?>
                    {
                        ["uid"] = uid,
                        ["cartaId"] = cartaId,
                    });
                }

                // 6. Logs + entrada de historial.
                fase = "logs-historial";
                var combateLog = reso.Resultados.Select(r => (object?)r.ToLogMap()).ToList();
                var conquistasLog = reso.ObeliscosConquistados.Select(c => (object?)c.ToLogMap()).ToList();
                var movimientosLog = BuildMovimientosLog(movTurno, req.Turno);

                var entradaHistorial = new Dictionary<string, object?>
                {
                    ["turno"] = req.Turno,
                    ["combateLog"] = combateLog,
                    ["conquistasLog"] = conquistasLog,
                    ["movimientosLog"] = movimientosLog,
                    ["farmeoLog"] = farmeoLogFinal,
                    ["repartoLog"] = repartoLog.Cast<object?>().ToList(),
                    ["accionesLog"] = acc.Log.Cast<object?>().ToList(),
                    ["rayoCoord"] = farmeo?.NuevoRayo != null ? M.Get(farmeo.NuevoRayo, "coord") : null,
                    ["rayoTurnosRestantes"] = farmeo?.NuevoRayo != null ? M.Get(farmeo.NuevoRayo, "turnosRestantes") : null,
                };

                var historial = M.List(M.Get(data, "historialCombates")).ToList();
                historial.Add(entradaHistorial);
                if (historial.Count > 3) historial.RemoveRange(0, historial.Count - 3);

                // 7. Construir el update.
                fase = "build-update";
                var update = new Dictionary<string, object>
                {
                    ["turnoActual"] = req.Turno + 1,
                    ["cerradoPor"] = new List<object>(),
                    ["movimientosTurno"] = new Dictionary<string, object>(),
                    ["tablero"] = ToFsTablero(tableroFinal),
                    ["statsPartida"] = stats.ToDictionary(k => k.Key, v => (object)v.Value),
                    ["ultimoCombateLog"] = combateLog,
                    ["ultimoFarmeoLog"] = farmeoLogFinal,
                    ["ultimoRepartoLog"] = repartoLog.Cast<object?>().ToList(),
                    ["ultimoAccionesLog"] = acc.Log.Cast<object?>().ToList(),
                    ["ultimosMovimientos"] = movimientosLog,
                    ["historialCombates"] = historial,
                    ["efectosCelda"] = efectosFinal.Count == 0 ? FieldValue.Delete : ToFsEfectos(efectosFinal),
                };
                if (farmeo != null)
                    update["rayo"] = farmeo.NuevoRayo != null ? (object)farmeo.NuevoRayo : FieldValue.Delete;

                // 8. Eliminaciones / fin de partida.
                var nuevosEliminados = reso.ObeliscosConquistados.Select(c => c.PerdedorUid).Distinct().ToList();
                string? ganadorUid = null;
                var finalizada = false;
                if (nuevosEliminados.Count > 0)
                {
                    update["jugadoresEliminados"] = FieldValue.ArrayUnion(nuevosEliminados.Cast<object>().ToArray());
                    var totalElim = new HashSet<string>(eliminados);
                    totalElim.UnionWith(nuevosEliminados);
                    var siguenActivos = jugadores.Where(u => !totalElim.Contains(u)).ToList();
                    if (siguenActivos.Count <= 1)
                    {
                        finalizada = true;
                        update["estado"] = "finalizada";
                        if (siguenActivos.Count > 0)
                        {
                            ganadorUid = siguenActivos[0];
                            update["ganadorUid"] = ganadorUid;
                        }
                    }
                }

                fase = "write";
                tx.Update(lobbyRef, update);

                var energiesTotales = new Dictionary<string, int>(reso.EnergiesPorJugador);
                if (farmeo != null)
                    foreach (var kv in farmeo.EnergiesPorJugador)
                        energiesTotales[kv.Key] = energiesTotales.GetValueOrDefault(kv.Key) + kv.Value;

                return new CerrarTurnoResponse
                {
                    Resuelto = true,
                    TurnoActual = req.Turno + 1,
                    CerradoPor = activos.Count,
                    JugadoresActivos = activos.Count,
                    Faltan = 0,
                    Finalizada = finalizada,
                    GanadorUid = ganadorUid,
                    Conquistas = reso.ObeliscosConquistados.Select(c => c.ToLogMap()).ToList(),
                    EnergiesPorJugador = energiesTotales,
                    Mensaje = "Turno resuelto.",
                };
            }
            catch (Exception ex)
            {
                // Re-lanza añadiendo la fase para diagnóstico preciso.
                throw new InvalidOperationException(
                    $"[fase={fase}] {ex.GetType().Name}: {ex.Message}", ex);
            }
        });

        // Tras commit, adjunta el estado completo de la partida para que el
        // cliente avance SIN leer Firestore (camino HTTP puro).
        try
        {
            resp.Estado = await LeerEstadoAsync(req.LobbyId);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("[WarZero] LeerEstado tras cerrar falló: " + ex);
        }
        return resp;
    }

    /// Revierte los gastos NO consolidados del turno en curso (bug QAS #2): al
    /// salir a mitad de turno (o pulsar "deshacer"), devuelve la energía
    /// revertible gastada este turno (despliegues/compras/evoluciones), desmarca
    /// las especiales compradas este turno y borra cualquier borrador previo. El
    /// tablero NO se persiste a mitad de turno, así que revierte solo al reentrar.
    ///
    /// NO toca `cerradoPor` ni resuelve. Si el jugador ya cerró o el turno ya
    /// avanzó, se ignora (no hay nada que revertir).
    public async Task<CerrarTurnoResponse> DeshacerTurnoAsync(DeshacerTurnoRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.LobbyId) || string.IsNullOrWhiteSpace(req.Uid))
            return new CerrarTurnoResponse { Mensaje = "lobbyId y uid son obligatorios" };

        var db = _fs.Db;
        var lobbyRef = db.Collection("Partidas").Document(req.LobbyId);

        return await db.RunTransactionAsync(async tx =>
        {
            var snap = await tx.GetSnapshotAsync(lobbyRef);
            if (!snap.Exists)
                return new CerrarTurnoResponse { Mensaje = "La partida no existe" };

            var data = M.Map(M.FromFs(snap.ToDictionary()));
            var turnoDb = M.Int(M.Get(data, "turnoActual"));
            var cerrado = M.List(M.Get(data, "cerradoPor")).Select(M.Str).ToHashSet();

            if (turnoDb != req.Turno || cerrado.Contains(req.Uid))
                return new CerrarTurnoResponse
                {
                    Resuelto = false,
                    TurnoActual = turnoDb,
                    Mensaje = "Deshacer ignorado (turno avanzado o ya cerrado)",
                };

            var updates = new Dictionary<FieldPath, object>();

            // Devolver la energía revertible gastada este turno.
            if (req.EnergiesDelta != 0)
                updates[new FieldPath("statsPartida", req.Uid, "energies")] =
                    FieldValue.Increment(req.EnergiesDelta);

            // Desmarcar las especiales compradas este turno (permite recomprarlas).
            var quitar = (req.EspecialesQuitar ?? new List<string>())
                .Where(s => !string.IsNullOrEmpty(s)).Distinct().ToList();
            if (quitar.Count > 0)
                updates[new FieldPath("statsPartida", req.Uid, "especialesCompradas")] =
                    FieldValue.ArrayRemove(quitar.Cast<object>().ToArray());

            // Borrar cualquier borrador de turno que hubiera quedado.
            updates[new FieldPath("movimientosTurno", req.Uid)] = FieldValue.Delete;

            tx.Update(lobbyRef, updates);

            return new CerrarTurnoResponse
            {
                Resuelto = false,
                TurnoActual = turnoDb,
                Mensaje = "Turno deshecho",
            };
        });
    }
    public async Task<Dictionary<string, object?>?> LeerEstadoAsync(string lobbyId)
    {
        var snap = await _fs.Db.Collection("Partidas").Document(lobbyId)
            .GetSnapshotAsync();
        if (!snap.Exists) return null;
        var safe = M.ToJsonSafe(snap.ToDictionary());
        return safe as Dictionary<string, object?> ?? new Dictionary<string, object?>();
    }

    /// Colección personal del jugador (catálogo + cartas poseídas + stats +
    /// skins resueltas) en una sola llamada, para que el cliente NO tenga que
    /// leer Firestore directamente. Usado por GET /warzero/coleccion.
    public async Task<Dictionary<string, object?>> ColeccionAsync(string uid)
    {
        var db = _fs.Db;

        // Lecturas en paralelo: jugador, su subcolección Coleccion y el catálogo.
        var jugadorTask = db.Collection("Jugadores").Document(uid).GetSnapshotAsync();
        var coleccionTask = db.Collection("Jugadores").Document(uid)
            .Collection("Coleccion").GetSnapshotAsync();
        var cartasTask = db.Collection("Cartas").GetSnapshotAsync();
        await Task.WhenAll(jugadorTask, coleccionTask, cartasTask);

        var jugadorSnap = jugadorTask.Result;
        var coleccionSnap = coleccionTask.Result;
        var cartasSnap = cartasTask.Result;

        // Catálogo global: docId -> campos (con el id inyectado, como en el cliente).
        var catalogo = new Dictionary<string, Dictionary<string, object?>>();
        foreach (var doc in cartasSnap.Documents)
        {
            var m = M.Map(M.ToJsonSafe(doc.ToDictionary()));
            m["id"] = doc.Id;
            catalogo[doc.Id] = m;
        }

        // Stats del jugador.
        Dictionary<string, object?>? jugador = null;
        if (jugadorSnap.Exists)
        {
            var d = M.Map(M.ToJsonSafe(jugadorSnap.ToDictionary()));
            jugador = new Dictionary<string, object?>
            {
                ["alias"] = M.Str(M.Get(d, "alias")),
                ["nivel"] = M.Int(M.Get(d, "nivel")),
                ["experiencia"] = M.Int(M.Get(d, "experiencia")),
                ["dinero"] = M.Int(M.Get(d, "dinero")),
                ["imagenPerfil"] = M.Str(M.Get(d, "imagenPerfil")),
            };
        }

        // Entradas de colección + skins seleccionadas a resolver.
        var skinIds = new HashSet<string>();
        var entradas = new List<Dictionary<string, object?>>();
        foreach (var doc in coleccionSnap.Documents)
        {
            var d = M.Map(M.ToJsonSafe(doc.ToDictionary()));
            var cant = M.Int(M.Get(d, "cantidad"));
            if (cant <= 0) cant = 1;
            var skinSel = M.Get(d, "skinSeleccionada") as string;
            if (!string.IsNullOrEmpty(skinSel)) skinIds.Add(skinSel!);
            entradas.Add(new Dictionary<string, object?>
            {
                ["cartaId"] = doc.Id,
                ["cantidad"] = cant,
                ["skinSeleccionada"] = skinSel,
                ["skinsDesbloqueadas"] =
                    M.List(M.Get(d, "skinsDesbloqueadas")).Select(M.Str).Cast<object?>().ToList(),
                ["fechaObtenida"] = M.Get(d, "fechaObtenida"),
            });
        }

        // Imágenes de las skins seleccionadas (en paralelo).
        var skinUrls = new Dictionary<string, string>();
        if (skinIds.Count > 0)
        {
            var skinTasks = skinIds.ToDictionary(
                id => id,
                id => db.Collection("Skins").Document(id).GetSnapshotAsync());
            await Task.WhenAll(skinTasks.Values);
            foreach (var kv in skinTasks)
            {
                var s = kv.Value.Result;
                if (!s.Exists) continue;
                var sd = M.Map(M.ToJsonSafe(s.ToDictionary()));
                var url = M.Str(M.Get(sd, "imagen"));
                if (!string.IsNullOrEmpty(url)) skinUrls[kv.Key] = url;
            }
        }

        // Cartas poseídas = catálogo + datos de colección + url de skin.
        // Y recoger las evoluciones referenciadas para incluirlas también.
        var cartas = new List<Dictionary<string, object?>>();
        var evolucionIds = new HashSet<string>();
        foreach (var e in entradas)
        {
            var cartaId = M.Str(e["cartaId"]);
            if (!catalogo.TryGetValue(cartaId, out var cat)) continue;

            var merged = new Dictionary<string, object?>(cat)
            {
                ["cantidad"] = e["cantidad"],
                ["skinSeleccionada"] = e["skinSeleccionada"],
                ["skinsDesbloqueadas"] = e["skinsDesbloqueadas"],
                ["fechaObtenida"] = e["fechaObtenida"],
            };
            if (e["skinSeleccionada"] is string sel && skinUrls.TryGetValue(sel, out var url))
                merged["skinImagen"] = url;
            cartas.Add(merged);

            var idEvo = M.Str(M.Get(cat, "IdEvolucion"));
            if (!string.IsNullOrEmpty(idEvo)) evolucionIds.Add(idEvo);
        }

        var evoluciones = evolucionIds
            .Where(catalogo.ContainsKey)
            .Select(id => (object?)catalogo[id])
            .ToList();

        return new Dictionary<string, object?>
        {
            ["jugador"] = jugador,
            ["cartas"] = cartas.Cast<object?>().ToList(),
            ["evoluciones"] = evoluciones,
        };
    }

    /// Skins DESBLOQUEADAS por el jugador para una carta concreta. Lee la
    /// subcolección Coleccion para saber qué skins tiene desbloqueadas y devuelve
    /// sus datos desde la colección Skins. Usado por GET /warzero/skins.
    public async Task<List<Dictionary<string, object?>>> SkinsDisponiblesAsync(
        string uid, string cartaId)
    {
        var db = _fs.Db;

        var entry = await db.Collection("Jugadores").Document(uid)
            .Collection("Coleccion").Document(cartaId).GetSnapshotAsync();
        if (!entry.Exists) return new();

        var d = M.Map(M.ToJsonSafe(entry.ToDictionary()));
        var desbloqueadas = M.List(M.Get(d, "skinsDesbloqueadas"))
            .Select(M.Str)
            .Where(s => !string.IsNullOrEmpty(s))
            .Distinct()
            .Take(30) // Firestore whereIn admite hasta 30 (igual que el cliente).
            .ToList();
        if (desbloqueadas.Count == 0) return new();

        var snap = await db.Collection("Skins")
            .WhereIn(FieldPath.DocumentId, desbloqueadas)
            .WhereEqualTo("cartaId", cartaId)
            .GetSnapshotAsync();

        return snap.Documents.Select(doc =>
        {
            var sd = M.Map(M.ToJsonSafe(doc.ToDictionary()));
            return new Dictionary<string, object?>
            {
                ["id"] = doc.Id,
                ["nombre"] = M.Str(M.Get(sd, "nombre")),
                ["imagen"] = M.Str(M.Get(sd, "imagen")),
                ["rareza"] = M.Str(M.Get(sd, "rareza")),
            };
        }).ToList();
    }

    /// Fija (o limpia, si skinId es null/vacío) la skin elegida del jugador para
    /// una carta en Jugadores/{uid}/Coleccion/{cartaId}.skinSeleccionada y
    /// devuelve la URL de la imagen resultante. Usado por POST /warzero/skin/seleccionar.
    public async Task<Dictionary<string, object?>> SeleccionarSkinAsync(
        string uid, string cartaId, string? skinId)
    {
        var db = _fs.Db;
        var docRef = db.Collection("Jugadores").Document(uid)
            .Collection("Coleccion").Document(cartaId);

        if (string.IsNullOrEmpty(skinId))
            await docRef.UpdateAsync("skinSeleccionada", FieldValue.Delete);
        else
            await docRef.UpdateAsync("skinSeleccionada", skinId);

        // Resolver la imagen de la skin elegida para que el cliente la pinte.
        string? imagen = null;
        if (!string.IsNullOrEmpty(skinId))
        {
            var s = await db.Collection("Skins").Document(skinId).GetSnapshotAsync();
            if (s.Exists)
            {
                var sd = M.Map(M.ToJsonSafe(s.ToDictionary()));
                var url = M.Str(M.Get(sd, "imagen"));
                if (!string.IsNullOrEmpty(url)) imagen = url;
            }
        }

        return new Dictionary<string, object?>
        {
            ["ok"] = true,
            ["skinId"] = skinId,
            ["imagen"] = imagen,
        };
    }

    /// Actualiza los stats de partida de un jugador (energías, mano/mazo, compras)
    /// de forma atómica. Devuelve las energías resultantes. Usado por POST
    /// /warzero/stats para que el cliente NO escriba en Firestore en partida.
    public async Task<Dictionary<string, object?>> ActualizarStatsAsync(StatsRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.LobbyId) || string.IsNullOrWhiteSpace(req.Uid))
            return new() { ["ok"] = false, ["error"] = "lobbyId y uid son obligatorios" };

        var db = _fs.Db;
        var lobbyRef = db.Collection("Partidas").Document(req.LobbyId);
        var updates = new Dictionary<FieldPath, object>();

        if (req.EnergiesDelta is int delta && delta != 0)
            updates[new FieldPath("statsPartida", req.Uid, "energies")] =
                FieldValue.Increment(delta);

        if (!string.IsNullOrEmpty(req.EspecialComprada))
            updates[new FieldPath("statsPartida", req.Uid, "especialesCompradas")] =
                FieldValue.ArrayUnion(req.EspecialComprada);

        if (req.Mano != null)
            updates[new FieldPath("statsPartida", req.Uid, "mano")] = req.Mano;

        if (req.MazoRestante != null)
            updates[new FieldPath("statsPartida", req.Uid, "mazoRestante")] =
                req.MazoRestante;

        if (updates.Count > 0)
            await lobbyRef.UpdateAsync(updates);

        // Devolver las energías resultantes para que el cliente pueda reconciliar.
        int? energies = null;
        try
        {
            var snap = await lobbyRef.GetSnapshotAsync();
            if (snap.Exists)
            {
                var data = M.Map(M.FromFs(snap.ToDictionary()));
                var stats = M.Map(M.Get(data, "statsPartida"));
                if (stats.TryGetValue(req.Uid, out var s))
                    energies = M.Int(M.Get(M.Map(s), "energies"));
            }
        }
        catch { /* el valor de retorno es informativo; no rompemos por esto */ }

        return new Dictionary<string, object?>
        {
            ["ok"] = true,
            ["energies"] = energies,
        };
    }

    /// Sobrescribe "Imagen" en las entradas de [catalogoPorId] cuyo id tenga
    /// una skin seleccionada por [uid] en su colección, con la URL de esa
    /// skin. Muta los mapas in-place (misma instancia que ya vive en
    /// [catalogoPorId]), así que cualquier copia posterior por cantidad
    /// arrastra la imagen correcta sin más trabajo. Usado por
    /// MazoDelJugadorAsync para que el diseño elegido en "Mis cartas"
    /// también se vea en partida (mano, mazo restante, tablero).
    private async Task AplicarSkinsAsync(
        string uid, Dictionary<string, Dictionary<string, object?>> catalogoPorId)
    {
        var db = _fs.Db;
        var coleccionSnap = await db.Collection("Jugadores").Document(uid)
            .Collection("Coleccion").GetSnapshotAsync();

        var skinSelPorCarta = new Dictionary<string, string>();
        foreach (var doc in coleccionSnap.Documents)
        {
            if (!catalogoPorId.ContainsKey(doc.Id)) continue;
            var d = M.Map(M.ToJsonSafe(doc.ToDictionary()));
            var sel = M.Get(d, "skinSeleccionada") as string;
            if (!string.IsNullOrEmpty(sel)) skinSelPorCarta[doc.Id] = sel!;
        }
        if (skinSelPorCarta.Count == 0) return;

        var skinIds = skinSelPorCarta.Values.Distinct().ToList();
        var skinTasks = skinIds.ToDictionary(
            id => id,
            id => db.Collection("Skins").Document(id).GetSnapshotAsync());
        await Task.WhenAll(skinTasks.Values);

        foreach (var kv in skinSelPorCarta)
        {
            var snap = skinTasks[kv.Value].Result;
            if (!snap.Exists) continue;
            var sd = M.Map(M.ToJsonSafe(snap.ToDictionary()));
            var url = M.Str(M.Get(sd, "imagen"));
            if (!string.IsNullOrEmpty(url) && catalogoPorId.TryGetValue(kv.Key, out var cm))
                cm["Imagen"] = url;
        }
    }

    /// Mazo del jugador (cartas completas, expandidas por Cantidad, filtradas por
    /// ejército preservando si el filtro lo vacía), portando la lógica de
    /// MazoService.obtenerMazoParaJuego del cliente. Usado por GET /warzero/mazo.
    public async Task<List<Dictionary<string, object?>>> MazoDelJugadorAsync(
        string uid, int? ejercitoId)
    {
        var db = _fs.Db;
        var rnd = new Random();

        // Catálogo completo una vez (id -> map con id inyectado).
        var cartasSnap = await db.Collection("Cartas").GetSnapshotAsync();
        var catalogo = new Dictionary<string, Dictionary<string, object?>>();
        foreach (var doc in cartasSnap.Documents)
        {
            var m = M.Map(M.ToJsonSafe(doc.ToDictionary()));
            m["id"] = doc.Id;
            catalogo[doc.Id] = m;
        }

        // BUG reportado: el diseño (skin) elegido en "Mis cartas" no se veía
        // en partida (mano/mazo/tablero), porque este endpoint devolvía
        // siempre "Imagen" del catálogo base, sin mirar la skin seleccionada
        // del jugador. Se sobrescribe aquí, ANTES de expandir por cantidad,
        // así todas las copias de esa carta arrastran ya la imagen correcta.
        await AplicarSkinsAsync(uid, catalogo);

        int Cond(Dictionary<string, object?> m) => M.Int(M.Get(m, "Condicion"));
        int Ejer(Dictionary<string, object?> m) => M.Int(M.Get(m, "Ejercito"));
        bool EsPorDefecto(Dictionary<string, object?> m) =>
            M.Get(m, "PorDefecto") is bool b && b;

        var mazosSnap = await db.Collection("Jugadores").Document(uid)
            .Collection("Mazos").Limit(1).GetSnapshotAsync();

        var resultado = new List<Dictionary<string, object?>>();

        if (mazosSnap.Count > 0)
        {
            var deckCartasSnap = await mazosSnap.Documents[0].Reference
                .Collection("Cartas").GetSnapshotAsync();

            // (id, cantidad) del mazo guardado.
            var entradas = deckCartasSnap.Documents.Select(d =>
            {
                var cd = d.ToDictionary();
                var cant = M.Int(cd.GetValueOrDefault("Cantidad"));
                return (id: d.Id, cant: cant <= 0 ? 1 : cant);
            }).ToList();

            // Expande por cantidad; NO excluye evolución/especial (igual que
            // resolverMazo del cliente: el filtrado lo hace game_screen).
            List<Dictionary<string, object?>> Construir(bool conFiltro)
            {
                var res = new List<Dictionary<string, object?>>();
                foreach (var (id, cant) in entradas)
                {
                    if (!catalogo.TryGetValue(id, out var cm)) continue;
                    if (conFiltro && ejercitoId != null && Ejer(cm) != ejercitoId) continue;
                    for (int q = 0; q < cant; q++) res.Add(cm);
                }
                return res;
            }

            resultado = Construir(true);
            if (resultado.Count == 0) resultado = Construir(false); // preservar mazo
        }
        else
        {
            // Mazo por defecto: catálogo sin evoluciones ni especiales.
            var basicas = catalogo.Values
                .Where(m => Cond(m) != 1 && Cond(m) != 5)
                .ToList();
            var filtradas = ejercitoId != null
                ? basicas.Where(m => Ejer(m) == ejercitoId).ToList()
                : basicas;
            if (filtradas.Count == 0) filtradas = basicas;

            var marcadas = filtradas.Where(EsPorDefecto).ToList();
            var fuente = marcadas.Count > 0 ? marcadas : filtradas;

            resultado = fuente.OrderBy(_ => rnd.Next())
                .Take(TamanioMazoDefecto).ToList();
        }

        return resultado;
    }

    /// Cartas del catálogo por sus IDs (con id inyectado), para resolver
    /// evoluciones y mano/mazo en el cliente. Usado por GET /warzero/cartas.
    public async Task<List<Dictionary<string, object?>>> CartasPorIdsAsync(
        IEnumerable<string> ids)
    {
        var db = _fs.Db;
        var distinct = ids.Where(s => !string.IsNullOrEmpty(s)).Distinct().ToList();
        if (distinct.Count == 0) return new();

        var tasks = distinct.ToDictionary(
            id => id,
            id => db.Collection("Cartas").Document(id).GetSnapshotAsync());
        await Task.WhenAll(tasks.Values);

        var res = new List<Dictionary<string, object?>>();
        foreach (var kv in tasks)
        {
            var snap = kv.Value.Result;
            if (!snap.Exists) continue;
            var m = M.Map(M.ToJsonSafe(snap.ToDictionary()));
            m["id"] = snap.Id;
            res.Add(m);
        }
        return res;
    }

    /// Terreno de un mapa: { coord: "sea"|"deepSea"|"amphibious"|"land" }.
    /// Devuelve null si el mapa no existe. Usado por GET /warzero/mapa.
    public async Task<Dictionary<string, object?>?> MapaTerrenoAsync(string mapaId)
    {
        var db = _fs.Db;
        var snap = await db.Collection("Mapas").Document(mapaId).GetSnapshotAsync();
        if (!snap.Exists) return null;

        var data = M.Map(M.ToJsonSafe(snap.ToDictionary()));
        var terreno = M.Map(M.Get(data, "terreno"));
        return new Dictionary<string, object?> { ["terreno"] = terreno };
    }

    /// Historias del jugador: catálogo `Historias` + estado de desbloqueo del
    /// jugador (campo historiasDesbloqueadas). Las bloqueadas se devuelven SIN
    /// título ni páginas (para no destripar el contenido). Usado por
    /// GET /warzero/historias.
    public async Task<List<Dictionary<string, object?>>> HistoriasAsync(string uid)
    {
        var db = _fs.Db;

        var jugadorTask = db.Collection("Jugadores").Document(uid).GetSnapshotAsync();
        var historiasTask = db.Collection("Historias").GetSnapshotAsync();
        await Task.WhenAll(jugadorTask, historiasTask);

        var desbloqueadas = new HashSet<string>();
        if (jugadorTask.Result.Exists)
        {
            var jd = M.Map(M.ToJsonSafe(jugadorTask.Result.ToDictionary()));
            foreach (var s in M.List(M.Get(jd, "historiasDesbloqueadas")))
            {
                var id = M.Str(s);
                if (!string.IsNullOrEmpty(id)) desbloqueadas.Add(id);
            }
        }

        var res = new List<Dictionary<string, object?>>();
        foreach (var doc in historiasTask.Result.Documents)
        {
            var d = M.Map(M.ToJsonSafe(doc.ToDictionary()));

            // Una historia marcada `PorDefecto` está desbloqueada para TODOS los
            // jugadores sin necesidad de conseguirla ni de tocar el documento de
            // cada jugador: son las historias "de bienvenida" o de tutorial que
            // el editor decide dejar abiertas para todo el mundo.
            var porDefecto = M.Bool(M.Get(d, "PorDefecto"));
            var abierta = porDefecto || desbloqueadas.Contains(doc.Id);

            var item = new Dictionary<string, object?>
            {
                ["id"] = doc.Id,
                ["ejercito"] = M.Int(M.Get(d, "Ejercito")),
                ["orden"] = M.Int(M.Get(d, "Orden")),
                ["desbloqueada"] = abierta,
                ["porDefecto"] = porDefecto,
            };

            // Solo se envía el contenido si está desbloqueada.
            if (abierta)
            {
                item["titulo"] = M.Str(M.Get(d, "Titulo"));
                item["paginas"] = M.List(M.Get(d, "Paginas")).Select(p =>
                {
                    var pm = M.Map(p);
                    return (object?)new Dictionary<string, object?>
                    {
                        ["imagen"] = M.Str(M.Get(pm, "imagen") ?? M.Get(pm, "Imagen")),
                        ["descripcion"] =
                            M.Str(M.Get(pm, "descripcion") ?? M.Get(pm, "Descripcion")),
                        ["orden"] = M.Int(M.Get(pm, "orden") ?? M.Get(pm, "Orden")),
                    };
                }).ToList();
            }

            res.Add(item);
        }
        return res;
    }

    /// Marca una historia como conseguida por el jugador (arrayUnion). Crea el
    /// doc/campo si no existieran. Usado por POST /warzero/historia/desbloquear.
    public async Task<Dictionary<string, object?>> DesbloquearHistoriaAsync(
        string uid, string historiaId)
    {
        if (string.IsNullOrWhiteSpace(uid) || string.IsNullOrWhiteSpace(historiaId))
            return new() { ["ok"] = false, ["error"] = "uid e historiaId son obligatorios" };

        var db = _fs.Db;
        await db.Collection("Jugadores").Document(uid).SetAsync(
            new Dictionary<string, object>
            {
                ["historiasDesbloqueadas"] = FieldValue.ArrayUnion(historiaId),
            },
            SetOptions.MergeAll);

        return new Dictionary<string, object?> { ["ok"] = true };
    }

    /// Esquinas candidatas para el cuartel/obelisco (igual que kObeliscoCoords).
    private static readonly string[] ObeliscoCoords = { "F1", "A1", "A10", "F10" };

    private const int EnergiasIniciales = 15;
    private const int TamanioManoInicial = 5;
    private const int TamanioMazoDefecto = 8;

    /// Entrada a la partida: inicializa de forma atómica las energías de inicio,
    /// el obelisco y la mano/mazo del jugador si aún no los tiene, y devuelve el
    /// estado completo.
    public async Task<EntrarResponse> EntrarAsync(EntrarRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.LobbyId) || string.IsNullOrWhiteSpace(req.Uid))
            return new EntrarResponse { Existe = false };

        var db = _fs.Db;
        var lobbyRef = db.Collection("Partidas").Document(req.LobbyId);

        // ── Pre-lectura (fuera de la transacción) para decidir si hay que ──────
        // repartir mano. El reparto lee colecciones (Mazos, Cartas) que no
        // conviene leer dentro de la transacción.
        var pre = await lobbyRef.GetSnapshotAsync();
        if (!pre.Exists) return new EntrarResponse { Existe = false };
        var preData = M.Map(M.FromFs(pre.ToDictionary()));

        var preStats = M.Map(M.Get(preData, "statsPartida"));
        var preMiStat = preStats.TryGetValue(req.Uid, out var ps) ? M.Map(ps) : null;
        var yaTieneMano = preMiStat != null && preMiStat.ContainsKey("mano");

        List<string>? manoIds = null;
        List<string>? mazoRestanteIds = null;
        List<string>? mazoPoolIds = null;
        if (!yaTieneMano)
        {
            try
            {
                var ejercitoId = EjercitoDeJugador(preData, req.Uid);
                var enTablero = CartasEnTableroDe(preData, req.Uid);
                var (mano, resto, pool) =
                    await RepartirManoAsync(req.Uid, ejercitoId, enTablero);
                manoIds = mano;
                mazoRestanteIds = resto;
                mazoPoolIds = pool;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("[WarZero.Entrar] reparto mano falló: " + ex);
            }
        }

        var resp = await db.RunTransactionAsync(async tx =>
        {
            var snap = await tx.GetSnapshotAsync(lobbyRef);
            if (!snap.Exists) return new EntrarResponse { Existe = false };

            var data = M.Map(M.FromFs(snap.ToDictionary()));
            var updates = new Dictionary<FieldPath, object>();
            int? energiasAsignadas = null;
            string? obeliscoAsignado = null;

            var stats = M.Map(M.Get(data, "statsPartida"));
            var miStat = stats.TryGetValue(req.Uid, out var s) ? M.Map(s) : null;

            // 1) Energías de inicio.
            if (miStat == null || !miStat.ContainsKey("energies"))
            {
                updates[new FieldPath("statsPartida", req.Uid, "energies")] =
                    EnergiasIniciales;
                energiasAsignadas = EnergiasIniciales;
            }

            // 2) Obelisco.
            var obeliscos = M.Map(M.Get(data, "obeliscos"));
            if (!obeliscos.ContainsKey(req.Uid))
            {
                var ocupadas = obeliscos.Values.Select(M.Str).ToHashSet();
                var libres = ObeliscoCoords.Where(c => !ocupadas.Contains(c)).ToList();
                if (libres.Count > 0)
                {
                    var elegido = libres[new Random().Next(libres.Count)];
                    updates[new FieldPath("obeliscos", req.Uid)] = elegido;
                    obeliscoAsignado = elegido;
                }
            }

            // 3) Mano/mazo (solo si sigue sin tenerla y la pudimos repartir).
            var tieneMano = miStat != null && miStat.ContainsKey("mano");
            if (!tieneMano && manoIds != null && mazoRestanteIds != null)
            {
                updates[new FieldPath("statsPartida", req.Uid, "mano")] = manoIds;
                updates[new FieldPath("statsPartida", req.Uid, "mazoRestante")] =
                    mazoRestanteIds;
                // mazoPool = mazo completo (pool de robo de fin de turno, bug QAS #2).
                if (mazoPoolIds != null)
                    updates[new FieldPath("statsPartida", req.Uid, "mazoPool")] =
                        mazoPoolIds;
            }

            if (updates.Count > 0) tx.Update(lobbyRef, updates);

            return new EntrarResponse
            {
                Existe = true,
                TurnoActual = M.Int(M.Get(data, "turnoActual")),
                EnergiasAsignadas = energiasAsignadas,
                ObeliscoAsignado = obeliscoAsignado,
            };
        });

        // Tras commit, adjunta el estado completo (ya con la init aplicada).
        if (resp.Existe)
        {
            try { resp.Estado = await LeerEstadoAsync(req.LobbyId); }
            catch (Exception ex)
            {
                Console.Error.WriteLine("[WarZero] LeerEstado tras entrar falló: " + ex);
            }
            if (resp.Estado != null)
                resp.TurnoActual = resp.Estado.TryGetValue("turnoActual", out var t) && t is long l
                    ? (int)l : resp.TurnoActual;
        }
        return resp;
    }

    /// Partidas en las que el jugador es participante (no finalizadas y donde
    /// sigue presente). Mismo criterio que LobbyService.misPartidasStream del
    /// cliente, pero por HTTP para no depender del realtime de Firestore.
    /// Devuelve cada doc serializado JSON-safe (mismo shape que Firestore) con su
    /// id inyectado; el cliente lo convierte con LobbyModel.fromMap.
    public async Task<List<Dictionary<string, object?>>> MisPartidasAsync(string uid)
    {
        var db = _fs.Db;
        var snap = await db.Collection("Partidas")
            .WhereArrayContains("participantes", uid)
            .GetSnapshotAsync();

        var result = new List<Dictionary<string, object?>>();
        foreach (var doc in snap.Documents)
        {
            var data = M.Map(M.ToJsonSafe(doc.ToDictionary()));
            var estado = M.Str(M.Get(data, "estado"));
            if (estado == "finalizada") continue;

            // El jugador sigue presente en jugadores[].
            var sigue = M.List(M.Get(data, "jugadores"))
                .Select(j => M.Str(M.Get(M.Map(j), "uid")))
                .Any(u => u == uid);
            if (!sigue) continue;

            data["id"] = doc.Id;
            result.Add(data);
        }
        return result;
    }

    /// Partidas públicas en espera (pestaña PÚBLICAS). Filtra por estado en el
    /// servidor (índice de campo único) y descarta privadas; cada doc va
    /// serializado JSON-safe con su id inyectado.
    public async Task<List<Dictionary<string, object?>>> PublicasAsync()
    {
        var db = _fs.Db;
        var snap = await db.Collection("Partidas")
            .WhereEqualTo("estado", "esperando")
            .Limit(50)
            .GetSnapshotAsync();

        var result = new List<Dictionary<string, object?>>();
        foreach (var doc in snap.Documents)
        {
            var data = M.Map(M.ToJsonSafe(doc.ToDictionary()));
            var esPrivada = M.Get(data, "esPrivada") is bool b && b;
            if (esPrivada) continue;
            data["id"] = doc.Id;
            result.Add(data);
        }
        return result;
    }

    /// Datos de la pantalla MIS MAZOS (sin Firestore en el cliente): ejércitos,
    /// catálogo de cartas y perfiles de mazo del jugador. Equivale a las tres
    /// lecturas que hacía el cliente (EjercitoService + MazoService +
    /// Jugadores/{uid}/Mazos). Usado por GET /warzero/mismazos.
    public async Task<Dictionary<string, object?>> MisMazosAsync(string uid)
    {
        var db = _fs.Db;

        // Lecturas en paralelo.
        var ejercitosTask = db.Collection("Ejercitos").GetSnapshotAsync();
        var cartasTask = db.Collection("Cartas").GetSnapshotAsync();
        var mazosTask = db.Collection("Jugadores").Document(uid)
            .Collection("Mazos").GetSnapshotAsync();
        await Task.WhenAll(ejercitosTask, cartasTask, mazosTask);

        // Ejércitos: docId numérico → { id, nombre, descripcion, icono }.
        var ejercitos = new List<Dictionary<string, object?>>();
        foreach (var doc in ejercitosTask.Result.Documents)
        {
            var d = M.Map(M.ToJsonSafe(doc.ToDictionary()));
            ejercitos.Add(new Dictionary<string, object?>
            {
                ["id"] = int.TryParse(doc.Id, out var idn) ? idn : 0,
                ["nombre"] = M.Str(M.Get(d, "Nombre")),
                ["descripcion"] = M.Str(M.Get(d, "Descripcion")),
                ["icono"] = M.Str(M.Get(d, "Icono")),
            });
        }
        ejercitos.Sort((a, b) => M.Int(a["id"]).CompareTo(M.Int(b["id"])));

        // Catálogo de cartas completo (id inyectado), mismo shape que /warzero/mazo.
        var cartas = new List<Dictionary<string, object?>>();
        foreach (var doc in cartasTask.Result.Documents)
        {
            var m = M.Map(M.ToJsonSafe(doc.ToDictionary()));
            m["id"] = doc.Id;
            cartas.Add(m);
        }

        // Perfiles de mazo del jugador.
        var mazos = new List<Dictionary<string, object?>>();
        foreach (var doc in mazosTask.Result.Documents)
        {
            var d = M.Map(M.ToJsonSafe(doc.ToDictionary()));
            mazos.Add(new Dictionary<string, object?>
            {
                ["id"] = doc.Id,
                ["nombre"] = M.Str(M.Get(d, "nombre")),
                ["ejercitoId"] = M.Int(M.Get(d, "ejercitoId")),
                ["esPrincipal"] = M.Get(d, "esPrincipal") is bool eb && eb,
                ["cartaIds"] = M.List(M.Get(d, "cartaIds")).Select(M.Str).Cast<object?>().ToList(),
                ["total"] = M.Int(M.Get(d, "total")),
            });
        }

        return new Dictionary<string, object?>
        {
            ["ejercitos"] = ejercitos.Cast<object?>().ToList(),
            ["cartas"] = cartas.Cast<object?>().ToList(),
            ["mazos"] = mazos.Cast<object?>().ToList(),
        };
    }

    /// Ejército elegido por el jugador en la sala (de `jugadores[].ejercitoId`).
    private static int? EjercitoDeJugador(Dictionary<string, object?> data, string uid)
    {
        foreach (var j in M.List(M.Get(data, "jugadores")))
        {
            var jm = M.Map(j);
            if (M.Str(M.Get(jm, "uid")) == uid)
            {
                var e = M.Get(jm, "ejercitoId");
                return e == null ? (int?)null : M.Int(e);
            }
        }
        return null;
    }

    /// IDs de cartas que el jugador ya tiene colocadas en el tablero.
    private static HashSet<string> CartasEnTableroDe(
        Dictionary<string, object?> data, string uid)
    {
        var ids = new HashSet<string>();
        foreach (var celda in M.Map(M.Get(data, "tablero")).Values)
        {
            foreach (var c in M.List(celda))
            {
                var cm = M.Map(c);
                if (M.Str(M.Get(cm, "ownerUid")) == uid)
                {
                    var id = M.Str(M.Get(cm, "id"));
                    if (!string.IsNullOrEmpty(id)) ids.Add(id);
                }
            }
        }
        return ids;
    }

    /// Reparte la mano inicial y el mazo restante del jugador (listas de IDs de
    /// carta), portando la lógica de MazoService del cliente: usa el primer mazo
    /// guardado del jugador (expandido por Cantidad) o un mazo por defecto si no
    /// tiene; excluye evoluciones; filtra por ejército (preservando el mazo si el
    /// filtro lo vacía); excluye cartas ya colocadas en el tablero; y baraja.
    private async Task<(List<string> mano, List<string> resto, List<string> pool)>
        RepartirManoAsync(
        string uid, int? ejercitoId, HashSet<string> cartasEnTablero)
    {
        var db = _fs.Db;
        var rnd = new Random();
        var poolIds = new List<string>();

        var mazosSnap = await db.Collection("Jugadores").Document(uid)
            .Collection("Mazos").Limit(1).GetSnapshotAsync();

        if (mazosSnap.Count > 0)
        {
            var cartasSnap = await mazosSnap.Documents[0].Reference
                .Collection("Cartas").GetSnapshotAsync();

            // (idCarta, cantidad) del mazo del jugador.
            var entradas = cartasSnap.Documents.Select(d =>
            {
                var cd = d.ToDictionary();
                var cant = M.Int(cd.GetValueOrDefault("Cantidad"));
                return (id: d.Id, cant: cant <= 0 ? 1 : cant);
            }).ToList();

            // Lee del catálogo Condicion + Ejercito de cada carta distinta.
            var metas = new Dictionary<string, (int cond, int ejer)>();
            foreach (var (id, _) in entradas)
            {
                if (metas.ContainsKey(id)) continue;
                var csnap = await db.Collection("Cartas").Document(id).GetSnapshotAsync();
                if (!csnap.Exists) { metas[id] = (-1, -1); continue; }
                var cd = csnap.ToDictionary();
                metas[id] = (M.Int(cd.GetValueOrDefault("Condicion")),
                             M.Int(cd.GetValueOrDefault("Ejercito")));
            }

            List<string> Construir(bool conFiltro)
            {
                var res = new List<string>();
                foreach (var (id, cant) in entradas)
                {
                    if (!metas.TryGetValue(id, out var m) || m.cond < 0) continue;
                    if (m.cond == 1 || m.cond == 5) continue; // evolución/especial: no se reparten
                    if (conFiltro && ejercitoId != null && m.ejer != ejercitoId) continue;
                    for (int q = 0; q < cant; q++) res.Add(id);
                }
                return res;
            }

            poolIds = Construir(true);
            if (poolIds.Count == 0) poolIds = Construir(false); // preservar mazo
        }
        else
        {
            // Mazo por defecto: catálogo completo, sin evoluciones ni especiales.
            var allSnap = await db.Collection("Cartas").GetSnapshotAsync();
            var basicas = allSnap.Documents
                .Select(d => (id: d.Id, cd: d.ToDictionary()))
                .Where(x => M.Int(x.cd.GetValueOrDefault("Condicion")) != 1
                    && M.Int(x.cd.GetValueOrDefault("Condicion")) != 5)
                .ToList();
            var filtradas = ejercitoId != null
                ? basicas.Where(x => M.Int(x.cd.GetValueOrDefault("Ejercito")) == ejercitoId).ToList()
                : basicas;
            if (filtradas.Count == 0) filtradas = basicas;

            // Preferir las cartas marcadas como "mazo por defecto" (PorDefecto).
            var marcadas = filtradas
                .Where(x => x.cd.GetValueOrDefault("PorDefecto") is bool b && b)
                .ToList();
            var fuente = marcadas.Count > 0 ? marcadas : filtradas;

            poolIds = fuente.OrderBy(_ => rnd.Next())
                .Take(TamanioMazoDefecto).Select(x => x.id).ToList();
        }

        // Excluir cartas ya en el tablero (por id) y barajar.
        var pool = poolIds.Where(id => !cartasEnTablero.Contains(id))
            .OrderBy(_ => rnd.Next()).ToList();

        var mano = pool.Take(TamanioManoInicial).ToList();
        var resto = pool.Skip(TamanioManoInicial).ToList();
        // `poolIds` es el mazo COMPLETO del jugador (expandido por cantidad, sin
        // evoluciones/especiales y SIN excluir on-board): es el pool de robo de
        // fin de turno (con repetición). Coincide con `_mazoCompleto` del cliente.
        return (mano, resto, poolIds);
    }

    // ── Helpers de serialización ──────────────────────────────────────────────

    private static EfectosCelda ParseEfectosCelda(object? raw)
    {
        var result = new EfectosCelda();
        foreach (var kv in M.Map(raw))
        {
            var lista = M.List(kv.Value).Select(M.Map).ToList();
            if (lista.Count > 0) result[kv.Key] = lista;
        }
        return result;
    }

    private static Dictionary<string, object> ToFsTablero(Tablero t)
    {
        var o = new Dictionary<string, object>();
        foreach (var kv in t) o[kv.Key] = kv.Value.Cast<object>().ToList();
        return o;
    }

    private static Dictionary<string, object> ToFsEfectos(EfectosCelda e)
    {
        var o = new Dictionary<string, object>();
        foreach (var kv in e) o[kv.Key] = kv.Value.Cast<object>().ToList();
        return o;
    }

    private static List<object?> BuildMovimientosLog(Dictionary<string, object?> movTurno, int turno)
    {
        var log = new List<object?>();
        foreach (var kv in movTurno)
        {
            var mov = M.Map(kv.Value);
            if (M.Int(M.Get(mov, "turno")) != turno) continue;
            var celdas = M.Map(M.Get(mov, "celdas"));
            var zona = "";
            foreach (var ce in celdas)
            {
                var cartas = M.List(ce.Value);
                if (cartas.Count > 0)
                {
                    zona = M.Str(M.Get(M.Map(cartas[0]), "ownerZone"));
                    if (zona != "") break;
                }
            }
            log.Add(new Dictionary<string, object?>
            {
                ["uid"] = M.Str(M.Get(mov, "uid")),
                ["zona"] = zona,
                ["celdas"] = celdas,
            });
        }
        return log;
    }
}