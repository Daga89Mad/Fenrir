public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this WebApplication app)
    {
        app.MapPost("/auth/login", async (
            FirebaseAuthService firebase,
            ITokenService tokenService,
            LoginRequest req) =>
        {
            var uid = await firebase.LoginAsync(req.Email, req.Password);

            if (uid == null)
                return Results.Unauthorized();

            // Generas tu token interno usando tu TokenService
            var token = tokenService.GenerateToken(uid);

            return Results.Ok(new { token });
        });
    }
}

public record LoginRequest(string Email, string Password);
