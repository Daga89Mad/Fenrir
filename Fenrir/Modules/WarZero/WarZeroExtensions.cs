public static class WarZeroExtensions
{
    public static WebApplication MapWarZeroEndpoints(this WebApplication app)
    {
        app.MapGet("/warzero/status", (WarZeroService svc) => Results.Ok(svc.GetStatus()));
        return app;
    }
}