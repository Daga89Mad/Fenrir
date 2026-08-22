using Google.Cloud.Firestore;

public static class WarZeroExtensions
{
    public static WebApplication MapWarZeroEndpoints(this WebApplication app)
    {
        app.MapGet("/warzero/status", (WarZeroService svc) => Results.Ok(svc.GetStatus()));

        // ── Diagnóstico: prueba SOLO la conexión a Firestore ──────────────────
        // GET /warzero/firestore/ping?lobbyId=XXXX
        // Aísla si el 500 viene de Firestore (auth/projectId/credencial) o de la
        // lógica de resolución. Devuelve la excepción completa si falla.
        app.MapGet("/warzero/firestore/ping", async (WarZeroFirestore fs, string? lobbyId) =>
        {
            try
            {
                var db = fs.Db;
                if (string.IsNullOrWhiteSpace(lobbyId))
                {
                    // Lectura mínima: lista 1 documento de Partidas.
                    var q = await db.Collection("Partidas").Limit(1).GetSnapshotAsync();
                    return Results.Ok(new
                    {
                        ok = true,
                        projectId = db.ProjectId,
                        partidasLeidas = q.Count,
                        mensaje = "Conexión a Firestore correcta",
                    });
                }

                var snap = await db.Collection("Partidas").Document(lobbyId).GetSnapshotAsync();
                return Results.Ok(new
                {
                    ok = true,
                    projectId = db.ProjectId,
                    existe = snap.Exists,
                    turnoActual = snap.Exists && snap.ContainsField("turnoActual")
                        ? snap.GetValue<long>("turnoActual") : (long?)null,
                });
            }
            catch (Exception ex)
            {
                return Results.Problem(
                    title: "Fallo de conexión a Firestore",
                    detail: Describe(ex),
                    statusCode: 500);
            }
        });

        // ── Estado completo de la partida por HTTP (para el que espera) ───────
        // GET /warzero/estado?lobbyId=XXXX
        app.MapGet("/warzero/estado", async (WarZeroService svc, string lobbyId, ILoggerFactory lf) =>
        {
            var log = lf.CreateLogger("WarZero.Estado");
            try
            {
                if (string.IsNullOrWhiteSpace(lobbyId))
                    return Results.BadRequest(new { error = "lobbyId es obligatorio" });

                var estado = await svc.LeerEstadoAsync(lobbyId);
                if (estado == null)
                    return Results.Ok(new EstadoResponse { Existe = false });

                var turno = estado.TryGetValue("turnoActual", out var t) && t is long l
                    ? (int)l
                    : (estado.TryGetValue("turnoActual", out var t2) && t2 is int i ? i : 0);

                return Results.Ok(new EstadoResponse
                {
                    Existe = true,
                    TurnoActual = turno,
                    Estado = estado,
                });
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Error al leer estado lobby={LobbyId}", lobbyId);
                return Results.Problem(title: "Error al leer estado",
                    detail: ex.Message, statusCode: 500);
            }
        });

        // ── Colección personal del jugador (sin Firestore en el cliente) ─────
        // GET /warzero/coleccion?uid=XXXX
        app.MapGet("/warzero/coleccion", async (WarZeroService svc, string uid, ILoggerFactory lf) =>
        {
            var log = lf.CreateLogger("WarZero.Coleccion");
            try
            {
                if (string.IsNullOrWhiteSpace(uid))
                    return Results.BadRequest(new { error = "uid es obligatorio" });

                var data = await svc.ColeccionAsync(uid);
                return Results.Ok(new
                {
                    existe = true,
                    jugador = data["jugador"],
                    cartas = data["cartas"],
                    evoluciones = data["evoluciones"],
                    porcentajes = data["porcentajes"],
                    catalogoNumerado = data["catalogoNumerado"],
                });
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Error al leer colección uid={Uid}", uid);
                return Results.Problem(title: "Error al leer la colección",
                    detail: Describe(ex), statusCode: 500);
            }
        });

        // ── Porcentaje de completado por ejército + monedas Zero (perfil) ────
        // GET /warzero/porcentajes?uid=XXXX
        app.MapGet("/warzero/porcentajes", async (WarZeroService svc, string uid, ILoggerFactory lf) =>
        {
            var log = lf.CreateLogger("WarZero.Porcentajes");
            try
            {
                if (string.IsNullOrWhiteSpace(uid))
                    return Results.BadRequest(new { error = "uid es obligatorio" });

                var data = await svc.PorcentajesColeccionAsync(uid);
                return Results.Ok(new
                {
                    existe = true,
                    porcentajes = data["porcentajes"],
                    zeros = data["zeros"],
                });
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Error al leer porcentajes uid={Uid}", uid);
                return Results.Problem(title: "Error al leer porcentajes",
                    detail: Describe(ex), statusCode: 500);
            }
        });

        // ── Abrir un sobre (RNG ponderado por Probabilidad) ──────────────────
        // POST /warzero/sobre/abrir  { uid, ejercitoId }
        app.MapPost("/warzero/sobre/abrir", async (WarZeroService svc, AbrirSobreRequest req, ILoggerFactory lf) =>
        {
            var log = lf.CreateLogger("WarZero.Sobre");
            try
            {
                if (string.IsNullOrWhiteSpace(req.Uid) || req.EjercitoId <= 0)
                    return Results.BadRequest(new { error = "uid y ejercitoId son obligatorios" });

                var data = await svc.AbrirSobreAsync(req.Uid, req.EjercitoId);
                return Results.Ok(data);
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { ok = false, error = ex.Message });
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Error al abrir sobre uid={Uid} ejercito={Ej}", req.Uid, req.EjercitoId);
                return Results.Problem(title: "Error al abrir el sobre",
                    detail: Describe(ex), statusCode: 500);
            }
        });

        // ── Comprar una skin con monedas Zero ────────────────────────────────
        // POST /warzero/skin/comprar  { uid, skinId }
        app.MapPost("/warzero/skin/comprar", async (WarZeroService svc, ComprarSkinRequest req, ILoggerFactory lf) =>
        {
            var log = lf.CreateLogger("WarZero.ComprarSkin");
            try
            {
                if (string.IsNullOrWhiteSpace(req.Uid) || string.IsNullOrWhiteSpace(req.SkinId))
                    return Results.BadRequest(new { error = "uid y skinId son obligatorios" });

                var data = await svc.ComprarSkinAsync(req.Uid, req.SkinId);
                return Results.Ok(data);
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { ok = false, error = ex.Message });
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Error al comprar skin uid={Uid} skin={Skin}", req.Uid, req.SkinId);
                return Results.Problem(title: "Error al comprar la skin",
                    detail: Describe(ex), statusCode: 500);
            }
        });

        // ── Skins de una carta (con estado de compra) ────────────────────────
        // GET /warzero/skins-carta?uid=XXXX&cartaId=YYYY
        app.MapGet("/warzero/skins-carta", async (WarZeroService svc, string uid, string cartaId, ILoggerFactory lf) =>
        {
            var log = lf.CreateLogger("WarZero.SkinsCarta");
            try
            {
                if (string.IsNullOrWhiteSpace(uid) || string.IsNullOrWhiteSpace(cartaId))
                    return Results.BadRequest(new { error = "uid y cartaId son obligatorios" });

                var data = await svc.SkinsDeCartaAsync(uid, cartaId);
                return Results.Ok(data);
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Error al leer skins carta uid={Uid} carta={Carta}", uid, cartaId);
                return Results.Problem(title: "Error al leer las skins de la carta",
                    detail: Describe(ex), statusCode: 500);
            }
        });

        // ── Skins desbloqueadas de una carta (sin Firestore en el cliente) ───
        // GET /warzero/skins?uid=XXXX&cartaId=YYYY
        app.MapGet("/warzero/skins", async (WarZeroService svc, string uid, string cartaId, ILoggerFactory lf) =>
        {
            var log = lf.CreateLogger("WarZero.Skins");
            try
            {
                if (string.IsNullOrWhiteSpace(uid) || string.IsNullOrWhiteSpace(cartaId))
                    return Results.BadRequest(new { error = "uid y cartaId son obligatorios" });

                var skins = await svc.SkinsDisponiblesAsync(uid, cartaId);
                return Results.Ok(new { existe = true, skins });
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Error al leer skins uid={Uid} carta={CartaId}", uid, cartaId);
                return Results.Problem(title: "Error al leer skins",
                    detail: Describe(ex), statusCode: 500);
            }
        });

        // ── Fijar/limpiar la skin elegida de una carta ───────────────────────
        // POST /warzero/skin/seleccionar  { uid, cartaId, skinId? }
        app.MapPost("/warzero/skin/seleccionar", async (WarZeroService svc, SeleccionarSkinRequest req, ILoggerFactory lf) =>
        {
            var log = lf.CreateLogger("WarZero.SkinSeleccionar");
            try
            {
                if (string.IsNullOrWhiteSpace(req.Uid) || string.IsNullOrWhiteSpace(req.CartaId))
                    return Results.BadRequest(new { error = "uid y cartaId son obligatorios" });

                var res = await svc.SeleccionarSkinAsync(req.Uid, req.CartaId, req.SkinId);
                return Results.Ok(res);
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Error al seleccionar skin uid={Uid} carta={CartaId}",
                    req.Uid, req.CartaId);
                return Results.Problem(title: "Error al guardar la skin",
                    detail: Describe(ex), statusCode: 500);
            }
        });

        // ── [Editores] Repartir una carta a TODOS los usuarios ───────────────
        // POST /warzero/carta/repartir-todos  { cartaId }
        app.MapPost("/warzero/carta/repartir-todos", async (WarZeroService svc, RepartirCartaRequest req, ILoggerFactory lf) =>
        {
            var log = lf.CreateLogger("WarZero.RepartirCarta");
            try
            {
                if (string.IsNullOrWhiteSpace(req.CartaId))
                    return Results.BadRequest(new { error = "cartaId es obligatorio" });

                var res = await svc.RepartirCartaATodosAsync(req.CartaId);
                return Results.Ok(res);
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Error al repartir carta {CartaId} a todos", req.CartaId);
                return Results.Problem(title: "Error al repartir la carta",
                    detail: Describe(ex), statusCode: 500);
            }
        });

        // ── [Editores] Repartir una skin a TODOS los usuarios ────────────────
        // POST /warzero/skin/repartir-todos  { skinId }
        app.MapPost("/warzero/skin/repartir-todos", async (WarZeroService svc, RepartirSkinRequest req, ILoggerFactory lf) =>
        {
            var log = lf.CreateLogger("WarZero.RepartirSkin");
            try
            {
                if (string.IsNullOrWhiteSpace(req.SkinId))
                    return Results.BadRequest(new { error = "skinId es obligatorio" });

                var res = await svc.RepartirSkinATodosAsync(req.SkinId);
                return Results.Ok(res);
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Error al repartir skin {SkinId} a todos", req.SkinId);
                return Results.Problem(title: "Error al repartir la skin",
                    detail: Describe(ex), statusCode: 500);
            }
        });

        // ── Actualizar stats de partida (energías/mano/mazo/compras) ─────────
        // POST /warzero/stats  { lobbyId, uid, energiesDelta?, especialComprada?, mano?, mazoRestante? }
        app.MapPost("/warzero/stats", async (WarZeroService svc, StatsRequest req, ILoggerFactory lf) =>
        {
            var log = lf.CreateLogger("WarZero.Stats");
            try
            {
                var res = await svc.ActualizarStatsAsync(req);
                return Results.Ok(res);
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Error al actualizar stats lobby={LobbyId} uid={Uid}",
                    req.LobbyId, req.Uid);
                return Results.Problem(title: "Error al actualizar stats",
                    detail: Describe(ex), statusCode: 500);
            }
        });

        // ── Cartas del catálogo por IDs (sin Firestore en el cliente) ────────
        // GET /warzero/cartas?ids=a,b,c
        app.MapGet("/warzero/cartas", async (WarZeroService svc, string? ids, ILoggerFactory lf) =>
        {
            var log = lf.CreateLogger("WarZero.Cartas");
            try
            {
                var lista = (ids ?? "").Split(',',
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var cartas = await svc.CartasPorIdsAsync(lista);
                return Results.Ok(new { cartas });
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Error al leer cartas ids={Ids}", ids);
                return Results.Problem(title: "Error al leer cartas",
                    detail: Describe(ex), statusCode: 500);
            }
        });

        // ── Mazo del jugador (expandido + filtrado por ejército) ─────────────
        // GET /warzero/mazo?uid=XXXX&ejercitoId=N
        app.MapGet("/warzero/mazo", async (WarZeroService svc, string uid, int? ejercitoId, ILoggerFactory lf) =>
        {
            var log = lf.CreateLogger("WarZero.Mazo");
            try
            {
                if (string.IsNullOrWhiteSpace(uid))
                    return Results.BadRequest(new { error = "uid es obligatorio" });

                var cartas = await svc.MazoDelJugadorAsync(uid, ejercitoId);
                return Results.Ok(new { existe = true, cartas });
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Error al leer mazo uid={Uid} ejercito={Ejercito}", uid, ejercitoId);
                return Results.Problem(title: "Error al leer el mazo",
                    detail: Describe(ex), statusCode: 500);
            }
        });

        // ── Terreno de un mapa (sin Firestore en el cliente) ─────────────────
        // GET /warzero/mapa?mapaId=XXXX
        app.MapGet("/warzero/mapa", async (WarZeroService svc, string mapaId, ILoggerFactory lf) =>
        {
            var log = lf.CreateLogger("WarZero.Mapa");
            try
            {
                if (string.IsNullOrWhiteSpace(mapaId))
                    return Results.BadRequest(new { error = "mapaId es obligatorio" });

                var data = await svc.MapaTerrenoAsync(mapaId);
                if (data == null)
                    return Results.Ok(new { existe = false });
                return Results.Ok(new { existe = true, terreno = data["terreno"], filas = data["filas"], columnas = data["columnas"], imagen = data["imagen"] });
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Error al leer mapa mapaId={MapaId}", mapaId);
                return Results.Problem(title: "Error al leer el mapa",
                    detail: Describe(ex), statusCode: 500);
            }
        });

        // ── Historias del jugador (catálogo + desbloqueo) ────────────────────
        // GET /warzero/historias?uid=XXXX
        app.MapGet("/warzero/historias", async (WarZeroService svc, string uid, ILoggerFactory lf) =>
        {
            var log = lf.CreateLogger("WarZero.Historias");
            try
            {
                if (string.IsNullOrWhiteSpace(uid))
                    return Results.BadRequest(new { error = "uid es obligatorio" });

                var historias = await svc.HistoriasAsync(uid);
                return Results.Ok(new { existe = true, historias });
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Error al leer historias uid={Uid}", uid);
                return Results.Problem(title: "Error al leer las historias",
                    detail: Describe(ex), statusCode: 500);
            }
        });

        // ── Desbloquear una historia (conseguida en el juego) ────────────────
        // POST /warzero/historia/desbloquear  { uid, historiaId }
        app.MapPost("/warzero/historia/desbloquear", async (WarZeroService svc, DesbloquearHistoriaRequest req, ILoggerFactory lf) =>
        {
            var log = lf.CreateLogger("WarZero.HistoriaDesbloquear");
            try
            {
                var res = await svc.DesbloquearHistoriaAsync(req.Uid, req.HistoriaId);
                return Results.Ok(res);
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Error al desbloquear historia uid={Uid} id={Id}",
                    req.Uid, req.HistoriaId);
                return Results.Problem(title: "Error al desbloquear la historia",
                    detail: Describe(ex), statusCode: 500);
            }
        });

        // ── Entrada a la partida (init atómica energías + obelisco) ──────────
        app.MapPost("/warzero/entrar", async (WarZeroService svc, EntrarRequest req, ILoggerFactory lf) =>
        {
            var log = lf.CreateLogger("WarZero.Entrar");
            try
            {
                var res = await svc.EntrarAsync(req);
                if (!res.Existe)
                    return Results.NotFound(new { error = "La partida no existe" });
                return Results.Ok(res);
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Error al entrar lobby={LobbyId} uid={Uid}",
                    req.LobbyId, req.Uid);
                Console.Error.WriteLine("[WarZero.Entrar] " + ex);
                return Results.Problem(title: "Error al entrar a la partida",
                    detail: ex.Message, statusCode: 500);
            }
        });

        // ── Cierre de turno gestionado íntegramente por el servidor ───────────
        app.MapPost("/warzero/turno/cerrar", async (WarZeroService svc, CerrarTurnoRequest req, ILoggerFactory lf) =>
        {
            var log = lf.CreateLogger("WarZero.CerrarTurno");
            try
            {
                var res = await svc.CerrarTurnoAsync(req);
                return Results.Ok(res);
            }
            catch (Exception ex)
            {
                // Traza completa en los logs de Render.
                log.LogError(ex, "Error al cerrar turno lobby={LobbyId} uid={Uid} turno={Turno}",
                    req.LobbyId, req.Uid, req.Turno);
                Console.Error.WriteLine("[WarZero.CerrarTurno] " + ex);

                return Results.Problem(
                    title: "Error al cerrar el turno",
                    detail: Describe(ex),
                    statusCode: 500);
            }
        });
        // .RequireAuthorization();  // ← activar cuando el cliente envíe el JWT

        // ── Deshacer los gastos del turno en curso (sin cerrar) ──────────────
        // POST /warzero/turno/deshacer. Devuelve la energía revertible gastada
        // este turno, desmarca las especiales compradas este turno y borra el
        // borrador. No cierra el turno ni resuelve (bug QAS #2).
        app.MapPost("/warzero/turno/deshacer", async (WarZeroService svc, DeshacerTurnoRequest req, ILoggerFactory lf) =>
        {
            var log = lf.CreateLogger("WarZero.DeshacerTurno");
            try
            {
                var res = await svc.DeshacerTurnoAsync(req);
                return Results.Ok(res);
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Error al deshacer turno lobby={LobbyId} uid={Uid} turno={Turno}",
                    req.LobbyId, req.Uid, req.Turno);
                Console.Error.WriteLine("[WarZero.DeshacerTurno] " + ex);
                return Results.Problem(
                    title: "Error al deshacer el turno",
                    detail: Describe(ex),
                    statusCode: 500);
            }
        });

        // ── Alianzas (partidas de 4+ jugadores) ──────────────────────────────
        // POST /warzero/alianza/proponer  { lobbyId, deUid, paraUid, turnos }
        app.MapPost("/warzero/alianza/proponer", async (WarZeroService svc, ProponerAlianzaRequest req, ILoggerFactory lf) =>
        {
            var log = lf.CreateLogger("WarZero.AlianzaProponer");
            try
            {
                var res = await svc.ProponerAlianzaAsync(req);
                return Results.Ok(res);
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Error al proponer alianza lobby={LobbyId} de={De} para={Para}",
                    req.LobbyId, req.DeUid, req.ParaUid);
                Console.Error.WriteLine("[WarZero.AlianzaProponer] " + ex);
                return Results.Problem(title: "Error al proponer la alianza",
                    detail: Describe(ex), statusCode: 500);
            }
        });

        // POST /warzero/alianza/responder  { lobbyId, uid, proponenteUid, aceptar }
        app.MapPost("/warzero/alianza/responder", async (WarZeroService svc, ResponderAlianzaRequest req, ILoggerFactory lf) =>
        {
            var log = lf.CreateLogger("WarZero.AlianzaResponder");
            try
            {
                var res = await svc.ResponderAlianzaAsync(req);
                return Results.Ok(res);
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Error al responder alianza lobby={LobbyId} uid={Uid} de={De}",
                    req.LobbyId, req.Uid, req.ProponenteUid);
                Console.Error.WriteLine("[WarZero.AlianzaResponder] " + ex);
                return Results.Problem(title: "Error al responder la alianza",
                    detail: Describe(ex), statusCode: 500);
            }
        });

        // POST /warzero/alianza/traicionar  { lobbyId, uid }
        app.MapPost("/warzero/alianza/traicionar", async (WarZeroService svc, TraicionarAlianzaRequest req, ILoggerFactory lf) =>
        {
            var log = lf.CreateLogger("WarZero.AlianzaTraicionar");
            try
            {
                var res = await svc.TraicionarAlianzaAsync(req);
                return Results.Ok(res);
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Error al traicionar alianza lobby={LobbyId} uid={Uid}",
                    req.LobbyId, req.Uid);
                Console.Error.WriteLine("[WarZero.AlianzaTraicionar] " + ex);
                return Results.Problem(title: "Error al traicionar la alianza",
                    detail: Describe(ex), statusCode: 500);
            }
        });

        // POST /warzero/alianza/avisos/limpiar?lobbyId=XXXX&uid=YYYY
        app.MapPost("/warzero/alianza/avisos/limpiar", async (WarZeroService svc, string lobbyId, string uid, ILoggerFactory lf) =>
        {
            var log = lf.CreateLogger("WarZero.AlianzaAvisosLimpiar");
            try
            {
                var res = await svc.LimpiarAvisosAlianzaAsync(lobbyId, uid);
                return Results.Ok(res);
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Error al limpiar avisos alianza lobby={LobbyId} uid={Uid}", lobbyId, uid);
                Console.Error.WriteLine("[WarZero.AlianzaAvisosLimpiar] " + ex);
                return Results.Problem(title: "Error al limpiar los avisos de alianza",
                    detail: Describe(ex), statusCode: 500);
            }
        });

        // GET /warzero/mispartidas?uid=XXXX
        app.MapGet("/warzero/mispartidas", async (WarZeroService svc, string uid, ILoggerFactory lf) =>
        {
            var log = lf.CreateLogger("WarZero.MisPartidas");
            try
            {
                if (string.IsNullOrWhiteSpace(uid))
                    return Results.BadRequest(new { error = "uid es obligatorio" });

                var partidas = await svc.MisPartidasAsync(uid);
                return Results.Ok(new { existe = true, partidas });
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Error al leer mis partidas uid={Uid}", uid);
                return Results.Problem(title: "Error al leer las partidas",
                    detail: Describe(ex), statusCode: 500);
            }
        });

        // ── Partidas públicas en espera vía HTTP ─────────────────────────────
        // GET /warzero/publicas
        app.MapGet("/warzero/publicas", async (WarZeroService svc, ILoggerFactory lf) =>
        {
            var log = lf.CreateLogger("WarZero.Publicas");
            try
            {
                var partidas = await svc.PublicasAsync();
                return Results.Ok(new { existe = true, partidas });
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Error al leer partidas públicas");
                return Results.Problem(title: "Error al leer las partidas públicas",
                    detail: Describe(ex), statusCode: 500);
            }
        });

        // ── Mis mazos (ejércitos + catálogo + perfiles) vía HTTP ─────────────
        // GET /warzero/mismazos?uid=XXXX
        app.MapGet("/warzero/mismazos", async (WarZeroService svc, string uid, ILoggerFactory lf) =>
        {
            var log = lf.CreateLogger("WarZero.MisMazos");
            try
            {
                if (string.IsNullOrWhiteSpace(uid))
                    return Results.BadRequest(new { error = "uid es obligatorio" });

                var data = await svc.MisMazosAsync(uid);
                return Results.Ok(data);
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Error al leer mis mazos uid={Uid}", uid);
                return Results.Problem(title: "Error al leer los mazos",
                    detail: Describe(ex), statusCode: 500);
            }
        });

        // GET /warzero/ranking?uid=XXXX
        app.MapGet("/warzero/ranking", async (WarZeroService svc, string uid, string? orden, ILoggerFactory lf) =>
        {
            var log = lf.CreateLogger("WarZero.Ranking");
            try
            {
                if (string.IsNullOrWhiteSpace(uid))
                    return Results.BadRequest(new { error = "uid es obligatorio" });
                var data = await svc.RankingAsync(uid, orden ?? "experiencia");
                return Results.Ok(new
                {
                    ok = true,
                    miPosicion = data["miPosicion"],
                    miEntrada = data["miEntrada"],
                    alrededor = data["alrededor"],
                    topDiez = data["topDiez"],
                });
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Error al leer ranking uid={Uid}", uid);
                return Results.Problem(title: "Error al leer el ranking",
                    detail: Describe(ex), statusCode: 500);
            }
        });

        // ── Registro del token FCM del dispositivo (notificaciones push) ─────
        // POST /warzero/fcm/registrar  { uid, token, platform }
        app.MapPost("/warzero/fcm/registrar", async (WarZeroFirestore fs, RegistrarFcmTokenRequest req, ILoggerFactory lf) =>
        {
            var log = lf.CreateLogger("WarZero.FcmRegistrar");
            try
            {
                if (string.IsNullOrWhiteSpace(req.Uid) || string.IsNullOrWhiteSpace(req.Token))
                    return Results.BadRequest(new { error = "uid y token son obligatorios" });

                await WarZeroNotificaciones.RegistrarTokenAsync(
                    fs.Db, req.Uid, req.Token, req.Platform);
                return Results.Ok(new { ok = true });
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Error al registrar token FCM uid={Uid}", req.Uid);
                return Results.Problem(title: "Error al registrar el token",
                    detail: Describe(ex), statusCode: 500);
            }
        });

        // ── Baja del token FCM (al cerrar sesión) ────────────────────────────
        // POST /warzero/fcm/eliminar  { uid, token }
        app.MapPost("/warzero/fcm/eliminar", async (WarZeroFirestore fs, EliminarFcmTokenRequest req, ILoggerFactory lf) =>
        {
            var log = lf.CreateLogger("WarZero.FcmEliminar");
            try
            {
                if (string.IsNullOrWhiteSpace(req.Uid) || string.IsNullOrWhiteSpace(req.Token))
                    return Results.BadRequest(new { error = "uid y token son obligatorios" });

                await WarZeroNotificaciones.EliminarTokenAsync(fs.Db, req.Uid, req.Token);
                return Results.Ok(new { ok = true });
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Error al eliminar token FCM uid={Uid}", req.Uid);
                return Results.Problem(title: "Error al eliminar el token",
                    detail: Describe(ex), statusCode: 500);
            }
        });

        return app;
    }

    /// Aplana la cadena de excepciones (mensaje + tipo + inner) para devolverla
    /// al cliente y dejarla legible en los logs.
    private static string Describe(Exception ex)
    {
        var parts = new List<string>();
        for (Exception? e = ex; e != null; e = e.InnerException)
            parts.Add($"{e.GetType().Name}: {e.Message}");
        return string.Join(" → ", parts);
    }
}