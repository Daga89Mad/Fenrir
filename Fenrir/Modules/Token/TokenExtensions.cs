// TokenExtensions.cs
public static class TokenExtensions
{
    public static WebApplication MapTokenEndpoints(this WebApplication app)
    {
        app.MapPost("/token", (ITokenService svc, TokenRequest req) =>
          Results.Ok(new TokenResponse(svc.GenerateToken(req.Username))));
        return app;
    }
}
