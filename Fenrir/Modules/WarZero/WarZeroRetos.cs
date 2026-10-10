using Google.Cloud.Firestore;

// ─────────────────────────────────────────────────────────────────────────────
// WarZeroRetos.cs
//
// RETOS: partidas preparadas de un toque. Hay dos clases (RetoCatalogo.cs):
//
// A) RETO DE PARTIDA NORMAL. A diferencia del MODO HISTORIA (que siembra un
//    tablero a mano y mueve un bot sintético), es una partida NORMAL, con los
//    mismos bots que rellenan salas públicas:
//
//   1. Se crea `Partidas/{reto_{uid}_{retoId}}` ya EN CURSO, con todos los
//      jugadores (humano + bots del reto) marcados como listos y su ejército
//      fijado. El tablero, las energías, los cuarteles y las manos NO se
//      escriben aquí: los reparte `EntrarAsync` cuando cada participante entra,
//      exactamente igual que en una partida normal.
//   2. Se lanzan los runners de los bots del reto (modo REANUDAR del runner:
//      la partida ya existe) a través del orquestador
//      (BotOrchestratorService.LanzarBotsEnPartidaAsync), para que empiecen a
//      jugar en segundos y sin duplicar runners.
//   3. Se guarda la config del reto en el campo `reto` del documento. De ahí la
//      lee RetoFoco.cs para que, en los retos de modo "todos contra el
//      jugador", cada bot solo vea al humano como enemigo.
//
//   SALIR = ABANDONAR: igual que en el modo historia, salir de un reto lo
//     da por PERDIDO. El cliente llama a POST /warzero/reto/abandonar
//     (AbandonarRetoAsync): se paran los runners de sus bots y se BORRA la
//     partida. Por eso un reto nunca aparece en «mis partidas» (Sala de
//     Guerra): MisPartidasAsync descarta los documentos con `esReto`.
//
//   REENTRADA: el id del documento es determinista (`reto_{uid}_{retoId}`) y
//     cada intento empieza SIEMPRE de cero, sobrescribiendo el documento
//     anterior (los retos no se acumulan en Firestore). Si quedara un intento
//     abierto (el aviso de abandono no llegó: app cerrada sin red, etc.), se
//     detienen antes sus runners para que no sigan jugando sobre el tablero
//     recién reiniciado.
//
// B) RETO SOBRE EL MOTOR DE HISTORIA (`RetoDef.HistoriaId`, p. ej. «El duelo
//    de Alexander»). Se delega en CrearPartidaHistoriaAsync con la batalla del
//    reto: el documento nace con `esHistoria` (bot sintético, sin recompensas
//    de PvP) y con `esReto` + `reto` (trofeo al ganar). Siempre empieza de
//    cero, como una batalla de historia, y salir de él la borra
//    (AbandonarHistoriaAsync; AbandonarRetoAsync también lo acepta).
// ─────────────────────────────────────────────────────────────────────────────

public partial class WarZeroService
{
    /// Prefijo de los documentos de partida de reto.
    private const string RetoDocPrefijo = "reto";

    /// Crea la partida de un reto para [req.Uid] (siempre de cero).
    public async Task<CrearRetoResponse> CrearPartidaRetoAsync(CrearRetoRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Uid) || string.IsNullOrWhiteSpace(req.RetoId))
            return new CrearRetoResponse { Ok = false, Error = "uid y retoId son obligatorios" };

        var def = RetoCatalogo.Get(req.RetoId);
        if (def == null)
            return new CrearRetoResponse { Ok = false, Error = $"reto desconocido: {req.RetoId}" };

        // ── Reto sobre el motor de historia ──────────────────────────────────
        if (def.EsDeHistoria)
            return await CrearRetoDeHistoriaAsync(req, def);

        if (def.Bots.Count == 0)
            return new CrearRetoResponse { Ok = false, Error = $"el reto {def.Id} no define bots" };

        var db = _fs.Db;
        var docId = $"{RetoDocPrefijo}_{req.Uid}_{def.Id}";
        var lobbyRef = db.Collection("Partidas").Document(docId);

        // ── ¿Queda un intento abierto? ───────────────────────────────────────
        // Salir de un reto lo abandona (AbandonarRetoAsync), así que aquí no se
        // reanuda nada: cada intento empieza de cero. Si aun así queda uno EN
        // CURSO (el aviso de abandono no llegó), se paran antes sus runners:
        // seguirían vivos jugando sobre el tablero recién reiniciado (creerían
        // haber jugado ya esos turnos y se quedarían parados) y bloquearían el
        // lanzamiento de los nuevos.
        try
        {
            var previo = await lobbyRef.GetSnapshotAsync();
            if (previo.Exists)
            {
                var dataPrevia = M.Map(M.FromFs(previo.ToDictionary()));
                if (M.Str(M.Get(dataPrevia, "estado")) != "finalizada")
                {
                    DetenerBotsReto(docId);
                    Console.WriteLine($"[WZ.Reto] {docId}: había un intento abierto; se descarta y empieza de cero");
                }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("[WZ.Reto] leer intento previo falló (se crea de cero): " + ex);
        }

        // ── Alias del jugador ────────────────────────────────────────────────
        var aliasJugador = "Jugador";
        try
        {
            var jugSnap = await db.Collection("Jugadores").Document(req.Uid).GetSnapshotAsync();
            if (jugSnap.Exists)
            {
                var a = M.Str(M.Get(M.Map(M.FromFs(jugSnap.ToDictionary())), "alias"));
                if (a != "") aliasJugador = a;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("[WZ.Reto] leer alias falló: " + ex);
        }

        // ── Mapa y bots ──────────────────────────────────────────────────────
        var mapaId = await ResolverMapaRetoAsync(def);
        var bots = await LeerBotsRetoAsync(def);

        // ── Jugadores: humano + bots, todos listos y con ejército fijado ─────
        var jugadores = new List<object?>
        {
            new Dictionary<string, object?>
            {
                ["uid"] = req.Uid,
                ["alias"] = aliasJugador,
                ["ejercitoId"] = (long)def.EjercitoJugador,
                ["listo"] = true,
            },
        };
        foreach (var b in bots)
        {
            jugadores.Add(new Dictionary<string, object?>
            {
                ["uid"] = b.Uid,
                ["alias"] = b.Alias,
                ["ejercitoId"] = (long)b.Ejercito,
                ["listo"] = true,
            });
        }

        // ── Config del reto (la leen RetoFoco y el cliente) ──────────────────
        var reto = new Dictionary<string, object?>
        {
            ["id"] = def.Id,
            ["orden"] = (long)def.Orden,
            ["titulo"] = def.Titulo,
            ["descripcion"] = def.Descripcion,
            ["mapaId"] = mapaId,
            ["jugadorUid"] = req.Uid,
            ["ejercitoJugador"] = (long)def.EjercitoJugador,
            ["botsUids"] = def.Bots.Cast<object?>().ToList(),
            ["modoBots"] = def.ModoBots == RetoModoBots.TodosContraElJugador
                ? "todos_contra_jugador"
                : "libre",
        };
        // `focoUid` SOLO existe en los retos en los que los bots comparten
        // objetivo. Es la única señal que mira RetoFoco: sin ella, los bots
        // juegan la guerra de todos contra todos de siempre.
        if (def.ModoBots == RetoModoBots.TodosContraElJugador)
            reto["focoUid"] = req.Uid;

        // Ventana explicativa que el cliente muestra antes de empezar (igual
        // que en el modo historia): [{icono, titulo, texto}, …].
        // ¿El reto da premio? Trofeos asignados en el editor (de caché, sin
        // lecturas) o el TrofeoId del sistema anterior.
        bool hayPremio = !string.IsNullOrWhiteSpace(def.TrofeoId);
        if (!hayPremio)
        {
            try
            {
                hayPremio = (await WarZeroTrofeos.IdsPorOrigenAsync(
                    db, WarZeroTrofeos.OrigenReto, def.Id)).Count > 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("[WZ.Reto] consultar trofeos del reto falló: " + ex);
            }
        }
        reto["explicacion"] = ConstruirExplicacionReto(def, bots, hayPremio);

        // ── Documento de partida (nace EN CURSO, vacío como una partida nueva) ─
        var doc = new Dictionary<string, object>
        {
            ["nombre"] = def.Titulo,
            ["hostUid"] = req.Uid,
            ["esPrivada"] = true,          // fuera de listados públicos y del relleno de bots
            ["contrasena"] = "",
            ["maxJugadores"] = (long)def.MaxJugadores,
            ["jugadores"] = jugadores,
            ["participantes"] = new List<object?> { req.Uid },   // los bots no listan partidas
            ["botsUids"] = def.Bots.Cast<object?>().ToList(),
            ["estado"] = "en_curso",
            ["creadoEn"] = Timestamp.FromDateTime(DateTime.UtcNow),
            ["modoTurno"] = def.ModoTurno,
            ["turnoActual"] = 1L,
            ["cerradoPor"] = new List<object?>(),
            ["jugadoresEliminados"] = new List<object?>(),
            // Vacíos a propósito: EntrarAsync reparte energías, cuarteles y manos.
            ["statsPartida"] = new Dictionary<string, object?>(),
            ["obeliscos"] = new Dictionary<string, object?>(),
            ["tablero"] = new Dictionary<string, object?>(),
            ["ultimoCombateLog"] = new List<object?>(),
            ["mapaId"] = mapaId,
            // Marcas de modo reto.
            ["esReto"] = true,
            [RetoFoco.CampoReto] = reto,
        };

        try
        {
            // SetAsync sin merge: un reto que ya terminó se sustituye por uno
            // fresco, sin arrastrar tablero ni stats del intento anterior.
            await lobbyRef.SetAsync(doc);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("[WZ.Reto] crear partida falló: " + ex);
            return new CrearRetoResponse { Ok = false, Error = "no se pudo crear la partida del reto" };
        }

        Console.WriteLine(
            $"[WZ.Reto] {def.Id} creado para {req.Uid} en {docId} " +
            $"(mapa {mapaId}, {def.MaxJugadores} jugadores, bots: {string.Join(", ", def.Bots)}, modo {def.ModoBots})");

        // Runners de los bots: a jugar ya, sin esperar al barrido del orquestador.
        await LanzarBotsRetoAsync(docId, def.Bots);

        var res = new CrearRetoResponse
        {
            Ok = true,
            LobbyId = docId,
            Reanudada = false,
            MaxJugadores = def.MaxJugadores,
        };
        try { res.Estado = await LeerEstadoAsync(docId); }
        catch (Exception ex)
        {
            Console.Error.WriteLine("[WZ.Reto] LeerEstado tras crear falló: " + ex);
        }
        return res;
    }

    // ── Ventana explicativa del reto (partida normal) ───────────────────────
    // Mismo formato que `historia.explicacion` (WarZeroHistoriaExplicacion.cs):
    // lista de {icono, titulo, texto}; las líneas que empiezan por "• " se
    // pintan como viñetas. El cliente añade las reglas generales del reto.
    private static List<object?> ConstruirExplicacionReto(
        RetoDef def, List<BotReto> bots, bool hayPremio)
    {
        var secciones = new List<object?>();
        void Seccion(string icono, string titulo, string texto)
        {
            if (string.IsNullOrWhiteSpace(texto)) return;
            secciones.Add(new Dictionary<string, object?>
            {
                ["icono"] = icono,
                ["titulo"] = titulo,
                ["texto"] = texto.Trim(),
            });
        }

        int rivales = bots.Count;
        Seccion("📜", "El reto", def.Introduccion != "" ? def.Introduccion : def.Descripcion);

        Seccion("🎯", "Objetivo",
            $"Sé el último comandante en pie: conquista los cuarteles de tus {rivales} rivales.");
        Seccion("💀", "Derrota", "• Si conquistan tu cuartel.");

        Seccion("⚔", "Tu ejército",
            $"Juegas con {NombreEjercitoConArticulo(def.EjercitoJugador)}. Despliegas desde tu cuartel, " +
            "robas cada turno y compras en el cuartel como en una partida normal.");

        var lineas = new List<string> { $"{def.MaxJugadores} jugadores: tú y {rivales} bots." };
        lineas.AddRange(bots.Select(b => $"• {b.Alias} · {NombreEjercito(b.Ejercito)}"));
        Seccion("👹", "Rivales", string.Join("\n", lineas));

        if (def.ModoBots == RetoModoBots.TodosContraElJugador)
            Seccion("🤝", "Todos contra ti",
                $"Los {rivales} bots tienen UN solo objetivo: tú. No se buscan entre ellos ni se disputan " +
                "sus cuarteles, y se apartan para no estorbarse. Si dos coinciden por casualidad en una " +
                "casilla, combaten como siempre.");

        if (def.Consejos.Count > 0)
            Seccion("🧭", "Consejos", string.Join("\n", def.Consejos.Select(c => "• " + c)));

        if (hayPremio)
            Seccion("🏆", "Premio", "Al superar el reto consigues un trofeo exclusivo.");

        return secciones;
    }

    private static string NombreEjercitoConArticulo(int ejercito) => ejercito switch
    {
        1 => "los Humanos",
        2 => "los Biónicos",
        3 => "los Demonios",
        4 => "los Nefilim",
        _ => "tu ejército",
    };

    // ── Reto sobre el motor de historia ──────────────────────────────────────
    // La batalla (HistoriaCatalogo, con `RetoId` = este reto) ya trae todo:
    // tablero sembrado, bot de historia, reglas propias y las marcas `esReto` +
    // `reto` para el trofeo. Siempre empieza de cero.
    private async Task<CrearRetoResponse> CrearRetoDeHistoriaAsync(CrearRetoRequest req, RetoDef def)
    {
        var batalla = HistoriaCatalogo.Get(def.HistoriaId);
        if (batalla == null)
            return new CrearRetoResponse { Ok = false, Error = $"el reto {def.Id} apunta a una batalla que no existe: {def.HistoriaId}" };
        if (batalla.RetoId != def.Id)
            Console.Error.WriteLine(
                $"[WZ.Reto] aviso: la batalla {batalla.Id} no tiene RetoId = {def.Id}: no se otorgará el trofeo del reto");

        var r = await CrearPartidaHistoriaAsync(new CrearHistoriaRequest
        {
            Uid = req.Uid,
            HistoriaId = def.HistoriaId,
        });
        if (!r.Ok)
            return new CrearRetoResponse { Ok = false, Error = r.Error ?? "no se pudo crear la partida del reto" };

        Console.WriteLine($"[WZ.Reto] {def.Id} creado para {req.Uid} en {r.LobbyId} (motor de historia: {def.HistoriaId})");
        return new CrearRetoResponse
        {
            Ok = true,
            LobbyId = r.LobbyId,
            Reanudada = false,
            MaxJugadores = def.MaxJugadores,
            Estado = r.Estado,
        };
    }

    // ── Abandono ─────────────────────────────────────────────────────────────
    // Salir de un reto (menú, botón atrás, «SALIR» en la ventana explicativa o
    // cerrar la app) lo da por PERDIDO: se paran los runners de sus bots y se
    // BORRA la partida. Así no queda «en curso» en la Sala de Guerra ni sigue
    // jugándose sola en segundo plano. Lo llama el cliente con
    // POST /warzero/reto/abandonar.

    /// Abandona el reto [req.LobbyId] de [req.Uid] si sigue abierto.
    /// Idempotente: si ya no existe, ya terminó o no es un reto de ese jugador,
    /// no hace nada. Los retos sobre el motor de historia se delegan en
    /// AbandonarHistoriaAsync (mismo efecto: la batalla se borra).
    public async Task<Dictionary<string, object?>> AbandonarRetoAsync(AbandonarRetoRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.LobbyId) || string.IsNullOrWhiteSpace(req.Uid))
            return new() { ["ok"] = false, ["error"] = "lobbyId y uid son obligatorios" };

        var db = _fs.Db;
        var lobbyRef = db.Collection("Partidas").Document(req.LobbyId);

        // Lectura previa (sin transacción) para decidir el camino y parar los
        // bots ANTES de borrar: un runner a mitad de turno no debe seguir
        // escribiendo sobre la partida que se está eliminando.
        var pre = await lobbyRef.GetSnapshotAsync();
        if (!pre.Exists) return new() { ["ok"] = true, ["abandonada"] = false };
        var preData = M.Map(M.FromFs(pre.ToDictionary()));

        if (M.Bool(M.Get(preData, "esHistoria")))
        {
            return await AbandonarHistoriaAsync(new AbandonarHistoriaRequest
            {
                Uid = req.Uid,
                LobbyId = req.LobbyId,
            });
        }

        if (!EsRetoAbiertoDe(preData, req.Uid))
            return new() { ["ok"] = true, ["abandonada"] = false };

        DetenerBotsReto(req.LobbyId);

        var borrada = await db.RunTransactionAsync(async tx =>
        {
            var snap = await tx.GetSnapshotAsync(lobbyRef);
            if (!snap.Exists) return false;
            var data = M.Map(M.FromFs(snap.ToDictionary()));
            if (!EsRetoAbiertoDe(data, req.Uid)) return false;
            tx.Delete(lobbyRef);
            return true;
        });

        if (borrada)
            Console.WriteLine($"[WZ.Reto] {req.LobbyId}: abandonado por {req.Uid} → borrado");
        return new() { ["ok"] = true, ["abandonada"] = borrada };
    }

    /// True si [data] es un reto (partida normal) de [uid] que no ha terminado.
    private static bool EsRetoAbiertoDe(Dictionary<string, object?> data, string uid) =>
        M.Bool(M.Get(data, "esReto"))
        && !M.Bool(M.Get(data, "esHistoria"))
        && M.Str(M.Get(data, "estado")) != "finalizada"
        && M.Str(M.Get(M.Map(M.Get(data, RetoFoco.CampoReto)), "jugadorUid")) == uid;

    /// Para al instante los runners de los bots de la partida [lobbyId]. Si el
    /// orquestador no está vivo no hay runners en este proceso que parar: los
    /// que hubiera en otro sitio salen solos al ver la partida borrada.
    private static void DetenerBotsReto(string lobbyId)
    {
        try
        {
            BotOrchestratorService.Instancia?.DetenerBotsEnPartida(lobbyId);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("[WZ.Reto] detener bots falló: " + ex);
        }
    }

    // ── Reto completado: registro + trofeos ──────────────────────────────────
    // Se llama tras el commit del cierre de turno (y tras la resolución forzosa).
    // Solo actúa si la partida es un RETO, ha terminado, y el ganador es el
    // HUMANO del reto (no un bot). Entonces:
    //
    //   1. Apunta el reto en Jugadores/{uid}.retosCompletados (arrayUnion). Se
    //      hace SIEMPRE, tenga trofeo o no: es lo que permite que un trofeo
    //      asignado más tarde al reto se reparta de forma retroactiva.
    //   2. Otorga los trofeos que en el editor declaran Origen = "reto" y
    //      OrigenId = este reto.
    //   3. Compatibilidad: si `RetoDef.TrofeoId` (sistema anterior) tiene valor,
    //      también lo otorga.
    //
    // No hace falta gestionar el aviso del pop-up: las dos vías encolan el
    // trofeo en `trofeosPendientesAviso`, y `CerrarTurnoAsync` drena esa cola
    // un poco más adelante en el mismo método, así que el trofeo viaja en la
    // MISMA respuesta que cierra la partida.
    internal async Task OtorgarTrofeoRetoSiProcedeAsync(
        Dictionary<string, object?> estado, bool finalizada, string? ganadorUid)
    {
        if (!finalizada) return;
        if (!M.Bool(M.Get(estado, "esReto"))) return;

        var reto = M.Map(M.Get(estado, RetoFoco.CampoReto));
        var jugadorUid = M.Str(M.Get(reto, "jugadorUid"));
        if (jugadorUid == "") return;

        // Ganó un bot (o la partida acabó sin ganador): ni registro ni trofeo.
        if (ganadorUid != jugadorUid) return;

        var retoId = M.Str(M.Get(reto, "id"));
        if (retoId == "") return;
        var db = _fs.Db;

        // 1) Registro del reto completado. Si falla no se sigue: el paso 2
        //    reevalúa contra este registro y no otorgaría nada.
        try
        {
            await db.Collection("Jugadores").Document(jugadorUid).SetAsync(
                new Dictionary<string, object>
                {
                    [WarZeroTrofeos.CampoRetosCompletados] = FieldValue.ArrayUnion(retoId),
                },
                SetOptions.MergeAll);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                "[WZ.Reto] registrar reto completado falló uid=" + jugadorUid +
                " reto=" + retoId + ": " + ex);
            return;
        }

        // 2) Trofeos asignados al reto desde el editor de trofeos.
        var nuevos = await WarZeroTrofeos.OtorgarPorOrigenAsync(
            db, jugadorUid, WarZeroTrofeos.OrigenReto, retoId);

        // 3) Trofeo del sistema anterior (RetoDef.TrofeoId), si lo hubiera.
        var def = RetoCatalogo.Get(retoId);
        if (def != null && !string.IsNullOrWhiteSpace(def.TrofeoId))
        {
            if (await WarZeroTrofeos.OtorgarManualAsync(db, jugadorUid, def.TrofeoId))
                nuevos.Add(def.TrofeoId);
        }

        Console.WriteLine(
            "[WZ.Reto] " + retoId + " ganado por " + jugadorUid +
            (nuevos.Count > 0
                ? " → trofeos otorgados: " + string.Join(",", nuevos)
                : " → sin trofeos nuevos"));
    }

    // ── Lanzamiento de los bots del reto ─────────────────────────────────────
    // Pasa SIEMPRE por el orquestador: es quien lleva la cuenta de qué bot tiene
    // runner en qué sala, así que ni se duplican runners ahora ni los duplica
    // luego su barrido de recuperación. Si el orquestador no estuviera vivo
    // (configuración sin el servicio hospedado), la partida queda creada y sus
    // bots arrancarán en el siguiente barrido de recuperación.
    private static async Task LanzarBotsRetoAsync(string lobbyId, List<string> uids)
    {
        var orquestador = BotOrchestratorService.Instancia;
        if (orquestador == null)
        {
            Console.Error.WriteLine(
                $"[WZ.Reto] orquestador no disponible: los bots de {lobbyId} arrancarán en la recuperación");
            return;
        }
        try
        {
            int n = await orquestador.LanzarBotsEnPartidaAsync(lobbyId, uids);
            Console.WriteLine($"[WZ.Reto] {n} runner(s) de bot lanzados en {lobbyId}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("[WZ.Reto] lanzar bots falló: " + ex);
        }
    }

    // ── Resolución del mapa del reto ─────────────────────────────────────────
    // Devuelve el id REAL del documento del mapa. Primero se prueba el id del
    // catálogo; si no existe, se busca por `nombre` (el editor de mapas puede
    // haber creado el documento con un id autogenerado). Si tampoco aparece, se
    // devuelve el id del catálogo y EntrarAsync usará el fallback de
    // coordenadas de cuartel (Coords.ObeliscosFallback), de modo que el reto se
    // puede jugar igualmente aunque el mapa no esté sembrado.
    private async Task<string> ResolverMapaRetoAsync(RetoDef def)
    {
        var db = _fs.Db;
        try
        {
            var snap = await db.Collection("Mapas").Document(def.MapaId).GetSnapshotAsync();
            if (snap.Exists) return def.MapaId;

            var nombre = def.MapaNombre != "" ? def.MapaNombre : def.MapaId;
            var todos = await db.Collection("Mapas").GetSnapshotAsync();
            foreach (var d in todos.Documents)
            {
                var n = M.Str(M.Get(M.Map(M.FromFs(d.ToDictionary())), "nombre"));
                if (string.Equals(n, nombre, StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine(
                        $"[WZ.Reto] mapa '{def.MapaId}' no existe; uso '{d.Id}' (nombre '{n}')");
                    return d.Id;
                }
            }
            Console.Error.WriteLine(
                $"[WZ.Reto] no encuentro el mapa '{def.MapaId}' ni por nombre '{nombre}': " +
                "se jugará con cuarteles por defecto");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("[WZ.Reto] resolver mapa falló: " + ex);
        }
        return def.MapaId;
    }

    // ── Alias y ejército de los bots del reto ────────────────────────────────
    // Se leen de la colección `Bots` (los mismos documentos que usa el panel de
    // edición de bots). Si un bot no tiene documento o no define `ejercitoId`,
    // se le asigna un ejército DISTINTO al del jugador, rotando, para que los
    // rivales no jueguen todos el mismo mazo.
    private async Task<List<BotReto>> LeerBotsRetoAsync(RetoDef def)
    {
        var db = _fs.Db;
        var res = new List<BotReto>();

        // Ejércitos de repuesto: todos menos el del jugador.
        var repuesto = new List<int> { 1, 2, 3, 4 }
            .Where(e => e != def.EjercitoJugador).ToList();
        int siguiente = 0;

        foreach (var uid in def.Bots)
        {
            string alias = uid;
            int ejercito = 0;
            try
            {
                var snap = await db.Collection("Bots").Document(uid).GetSnapshotAsync();
                if (snap.Exists)
                {
                    var d = M.Map(M.FromFs(snap.ToDictionary()));
                    var a = M.Str(M.Get(d, "alias"));
                    if (a != "") alias = a;
                    ejercito = M.Int(M.Get(d, "ejercitoId"));
                }
                else
                {
                    Console.Error.WriteLine(
                        $"[WZ.Reto] el bot {uid} no existe en `Bots`: juega con perfil por defecto");
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[WZ.Reto] leer bot {uid} falló: " + ex);
            }

            if (ejercito <= 0 && repuesto.Count > 0)
                ejercito = repuesto[siguiente++ % repuesto.Count];
            if (ejercito <= 0) ejercito = 1;

            res.Add(new BotReto(uid, alias, ejercito));
        }
        return res;
    }

    /// Datos mínimos de un bot rival dentro de un reto.
    private readonly record struct BotReto(string Uid, string Alias, int Ejercito);
}

// ─────────────────────────────────────────────────────────────────────────────
// DTOs de POST /warzero/reto/crear
// ─────────────────────────────────────────────────────────────────────────────

/// Cuerpo de POST /warzero/reto/crear.
public class CrearRetoRequest
{
    public string Uid { get; set; } = "";
    public string RetoId { get; set; } = "";
}

/// Cuerpo de POST /warzero/reto/abandonar.
public class AbandonarRetoRequest
{
    public string Uid { get; set; } = "";
    public string LobbyId { get; set; } = "";
}

/// Respuesta de POST /warzero/reto/crear. `LobbyId` es el id de la partida (para
/// navegar al juego), `MaxJugadores` el nº de puestos (humano + bots) y `Estado`
/// el estado completo ya montado. `Reanudada` se mantiene por compatibilidad
/// y hoy es siempre false: cada intento de reto empieza de cero. En los retos sobre
/// el motor de historia, `Estado.esHistoria` es true y `Estado.historia` lleva
/// la config que el cliente pasa a la pantalla de juego.
public class CrearRetoResponse
{
    public bool Ok { get; set; }
    public string? LobbyId { get; set; }
    public string? Error { get; set; }
    public bool Reanudada { get; set; }
    public int MaxJugadores { get; set; }
    public Dictionary<string, object?>? Estado { get; set; }
}