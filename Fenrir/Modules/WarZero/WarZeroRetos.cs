using Google.Cloud.Firestore;

// ─────────────────────────────────────────────────────────────────────────────
// WarZeroRetos.cs
//
// RETOS: partidas preparadas de un toque. A diferencia del MODO HISTORIA (que
// siembra un tablero a mano y mueve un bot sintético), un reto es una partida
// NORMAL, con los mismos bots que rellenan salas públicas:
//
//   1. Se crea `Partidas/{reto_{uid}_{retoId}}` ya EN CURSO, con los 4
//      jugadores (humano + bots del reto) marcados como listos y su ejército
//      fijado. El tablero, las energías, los cuarteles y las manos NO se
//      escriben aquí: los reparte `EntrarAsync` cuando cada participante entra,
//      exactamente igual que en una partida normal.
//   2. Se lanzan los runners de los bots del reto en modo REANUDAR a través del
//      orquestador (BotOrchestratorService.LanzarBotsEnPartidaAsync), para que
//      empiecen a jugar en segundos y sin duplicar runners.
//   3. Se guarda la config del reto en el campo `reto` del documento. De ahí la
//      lee RetoFoco.cs para que, en los retos de modo "todos contra el
//      jugador", cada bot solo vea al humano como enemigo.
//
// REENTRADA: el id del documento es determinista (`reto_{uid}_{retoId}`).
//   · Si el reto sigue EN CURSO se REANUDA (se devuelve esa misma partida y se
//     relanzan los runners que falten). No se pisa el tablero a medias.
//   · Si no existe o ya terminó, se crea de cero SOBRESCRIBIENDO el documento
//     anterior: los retos no se acumulan en Firestore.
// ─────────────────────────────────────────────────────────────────────────────

public partial class WarZeroService
{
    /// Prefijo de los documentos de partida de reto.
    private const string RetoDocPrefijo = "reto";

    /// Crea (o reanuda) la partida de un reto para [req.Uid].
    public async Task<CrearRetoResponse> CrearPartidaRetoAsync(CrearRetoRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Uid) || string.IsNullOrWhiteSpace(req.RetoId))
            return new CrearRetoResponse { Ok = false, Error = "uid y retoId son obligatorios" };

        var def = RetoCatalogo.Get(req.RetoId);
        if (def == null)
            return new CrearRetoResponse { Ok = false, Error = $"reto desconocido: {req.RetoId}" };
        if (def.Bots.Count == 0)
            return new CrearRetoResponse { Ok = false, Error = $"el reto {def.Id} no define bots" };

        var db = _fs.Db;
        var docId = $"{RetoDocPrefijo}_{req.Uid}_{def.Id}";
        var lobbyRef = db.Collection("Partidas").Document(docId);

        // ── ¿Hay un intento a medias? ────────────────────────────────────────
        // Un reto EN CURSO se reanuda tal cual: pisarlo dejaría runners vivos
        // jugando sobre un tablero recién reiniciado (creerían haber jugado ya
        // los primeros turnos y se quedarían parados).
        try
        {
            var previo = await lobbyRef.GetSnapshotAsync();
            if (previo.Exists)
            {
                var dataPrevia = M.Map(M.FromFs(previo.ToDictionary()));
                if (M.Str(M.Get(dataPrevia, "estado")) == "en_curso")
                {
                    await LanzarBotsRetoAsync(docId, def.Bots);
                    var resReanuda = new CrearRetoResponse
                    {
                        Ok = true,
                        LobbyId = docId,
                        Reanudada = true,
                        MaxJugadores = M.Int(M.Get(dataPrevia, "maxJugadores")),
                    };
                    try { resReanuda.Estado = await LeerEstadoAsync(docId); }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine("[WZ.Reto] LeerEstado al reanudar falló: " + ex);
                    }
                    return resReanuda;
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
            $"(mapa {mapaId}, bots: {string.Join(", ", def.Bots)}, modo {def.ModoBots})");

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
    // tres rivales no jueguen todos el mismo mazo.
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

/// Respuesta de POST /warzero/reto/crear. `LobbyId` es el id de la partida (para
/// navegar al juego), `MaxJugadores` el nº de puestos (humano + bots) y `Estado`
/// el estado completo ya montado. `Reanudada` indica que se ha devuelto un
/// intento que seguía en curso en vez de crear uno nuevo.
public class CrearRetoResponse
{
    public bool Ok { get; set; }
    public string? LobbyId { get; set; }
    public string? Error { get; set; }
    public bool Reanudada { get; set; }
    public int MaxJugadores { get; set; }
    public Dictionary<string, object?>? Estado { get; set; }
}