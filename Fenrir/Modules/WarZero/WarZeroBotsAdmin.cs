// ─────────────────────────────────────────────────────────────────────────────
// WarZeroBotsAdmin.cs
//
// Endpoints de ADMINISTRACIÓN DE BOTS (solo editores).
//
// SEGURIDAD
//   · Cada petición debe llevar  Authorization: Bearer <ID token de Firebase>.
//   · El usuario debe tener el CUSTOM CLAIM  editor = true (mismo claim que
//     WarZeroAdminCuentas / WarZeroTorneos).
//
// ENDPOINTS
//   POST /admin/bots/enviar   { botUid, lobbyId }
//     Manda un bot concreto a una partida PÚBLICA en espera para que ocupe un
//     hueco. Lo usa EdicionBotsScreen (botón "Enviar a partida" de cada bot).
//     El bot entra esté activo o no y sin respetar su tope de partidas: es una
//     orden explícita del editor. Ver BotOrchestratorService.EnviarBotASalaAsync.
//
// Se registra en Program.cs con:  app.MapWarZeroBotsAdminEndpoints();
// ─────────────────────────────────────────────────────────────────────────────

using FirebaseAdmin.Auth;

public static class WarZeroBotsAdminExtensions
{
    public static WebApplication MapWarZeroBotsAdminEndpoints(this WebApplication app)
    {
        // ── Enviar un bot a una partida pública ──────────────────────────────
        // POST /admin/bots/enviar   { botUid, lobbyId }
        app.MapPost("/admin/bots/enviar", async (
            HttpContext ctx, EnviarBotRequest req, ILoggerFactory lf) =>
        {
            var log = lf.CreateLogger("WarZero.AdminBots");

            var ed = await VerificarEditorAsync(ctx);
            if (ed.Error != null) return ed.Error;

            if (string.IsNullOrWhiteSpace(req.BotUid) || string.IsNullOrWhiteSpace(req.LobbyId))
                return Results.BadRequest(new { error = "botUid y lobbyId son obligatorios." });

            var orq = BotOrchestratorService.Instancia;
            if (orq == null)
                return Results.Problem(title: "El orquestador de bots no está en marcha",
                    statusCode: 503);

            try
            {
                var res = await orq.EnviarBotASalaAsync(req.BotUid.Trim(), req.LobbyId.Trim(),
                    ctx.RequestAborted);
                if (!res.Ok)
                    return Results.BadRequest(new { error = res.Error });

                log.LogInformation("Editor {Uid} envía {Bot} a la sala {Lobby}",
                    ed.Uid, req.BotUid, req.LobbyId);

                return Results.Ok(new
                {
                    ok = true,
                    alias = res.Alias,
                    partidasActivas = res.PartidasActivas,
                    maxPartidas = res.MaxPartidas,
                });
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Error al enviar el bot {Bot} a la sala {Lobby}",
                    req.BotUid, req.LobbyId);
                return Results.Problem(title: "Error al enviar el bot",
                    detail: ex.Message, statusCode: 500);
            }
        });

        return app;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // VERIFICACIÓN DEL EDITOR
    // ─────────────────────────────────────────────────────────────────────────

    private sealed record EditorVerificado(string Uid, IResult? Error);

    /// Valida el ID token de Firebase y exige el claim editor=true.
    private static async Task<EditorVerificado> VerificarEditorAsync(HttpContext ctx)
    {
        var header = ctx.Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return new("", Results.Unauthorized());

        var token = header["Bearer ".Length..].Trim();
        if (token.Length == 0) return new("", Results.Unauthorized());

        FirebaseToken dec;
        try { dec = await FirebaseAuth.DefaultInstance.VerifyIdTokenAsync(token, checkRevoked: true); }
        catch (FirebaseAuthException) { return new("", Results.Unauthorized()); }

        var esEditor = dec.Claims.TryGetValue("editor", out var v)
                       && string.Equals(v?.ToString(), "true", StringComparison.OrdinalIgnoreCase);
        if (!esEditor) return new(dec.Uid, Results.StatusCode(StatusCodes.Status403Forbidden));

        return new(dec.Uid, null);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// DTOs
// ─────────────────────────────────────────────────────────────────────────────

public sealed class EnviarBotRequest
{
    public string? BotUid { get; set; }
    public string? LobbyId { get; set; }
}
