public static class WarZeroExtensions
{
    public static WebApplication MapWarZeroEndpoints(this WebApplication app)
    {
        app.MapGet("/warzero/status", (WarZeroService svc) => Results.Ok(svc.GetStatus()));

        // Cierre de turno gestionado íntegramente por el servidor.
        // Registra el cierre del jugador y, si cerraron todos, resuelve el turno.
        app.MapPost("/warzero/turno/cerrar", async (WarZeroService svc, CerrarTurnoRequest req) =>
        {
            try
            {
                var res = await svc.CerrarTurnoAsync(req);
                return Results.Ok(res);
            }
            catch (Exception ex)
            {
                return Results.Problem(
                    title: "Error al cerrar el turno",
                    detail: ex.Message,
                    statusCode: 500);
            }
        });
        // .RequireAuthorization();  // ← activar cuando el cliente envíe el JWT

        return app;
    }
}