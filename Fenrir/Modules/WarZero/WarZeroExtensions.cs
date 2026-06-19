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