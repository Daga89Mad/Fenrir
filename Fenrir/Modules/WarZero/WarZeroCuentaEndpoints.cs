// ─────────────────────────────────────────────────────────────────────────────
// WarZeroCuentaEndpoints.cs  — VERSIÓN PUENTE (autónoma)
//
// Borrado de la PROPIA cuenta del jugador:
//
//   POST /warzero/cuenta/eliminar   { confirmacion: "ELIMINAR" }
//   Authorization: Bearer <ID token de Firebase>   (lo añade ApiAuthClient)
//
// Requisito de Apple (Guideline 5.1.1(v)) y derecho de supresión del RGPD.
//
// Esta versión NO depende de FirebaseAuthHandler ni de FiltroPropiedadUid:
// valida el ID token por sí misma (firma, caducidad y revocación) igual que
// WarZeroAdminCuentas.cs, así que se puede desplegar sobre la API actual sin
// cambiar nada más. Cuando se despliegue la revisión de seguridad completa,
// SUSTITUYE este fichero por la versión definitiva de WarZeroCuentaEndpoints.cs.
//
// Qué borra:
//   1. Jugadores/{uid} y TODAS sus subcolecciones (Coleccion, Mazos/*/Cartas,
//      Estadisticas…). Los tokens FCM viven en ese documento y caen con él.
//   2. El usuario de Firebase Authentication.
// Qué NO borra:
//   · Referencias al uid dentro de partidas ya jugadas (Partidas/*), que son
//     datos de juego de otros jugadores. Queda como identificador seudónimo.
//   · Una entrada mínima en AuditoriaCuentas (uid + fecha).
//
// Seguridad: el uid se toma SIEMPRE del token verificado, nunca del cuerpo, y
// se exige un inicio de sesión reciente (auth_time < 10 min): el cliente
// reautentica con la contraseña justo antes de llamar.
// ─────────────────────────────────────────────────────────────────────────────

using FirebaseAdmin.Auth;
using Google.Cloud.Firestore;

public static class WarZeroCuentaExtensions
{
    private const string TextoConfirmacion = "ELIMINAR";
    private static readonly TimeSpan MaximaAntiguedadLogin = TimeSpan.FromMinutes(10);

    public static WebApplication MapWarZeroCuentaEndpoints(this WebApplication app)
    {
        app.MapPost("/warzero/cuenta/eliminar", async (
            HttpContext ctx, WarZeroFirestore fs, EliminarCuentaRequest req, ILoggerFactory lf) =>
        {
            var log = lf.CreateLogger("WarZero.CuentaEliminar");

            // ── Verificación del ID token ────────────────────────────────────
            var header = ctx.Request.Headers.Authorization.ToString();
            if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                return Results.Unauthorized();

            var token = header["Bearer ".Length..].Trim();
            if (token.Length == 0 || token.Length > 8192)
                return Results.Unauthorized();

            FirebaseToken decodificado;
            try
            {
                decodificado = await FirebaseAuth.DefaultInstance
                    .VerifyIdTokenAsync(token, checkRevoked: true, ctx.RequestAborted);
            }
            catch (FirebaseAuthException)
            {
                return Results.Unauthorized();
            }
            catch (ArgumentException)
            {
                return Results.Unauthorized();
            }

            var uid = decodificado.Uid;

            if (!string.Equals(req.Confirmacion, TextoConfirmacion, StringComparison.Ordinal))
                return Results.BadRequest(new { error = "Falta la confirmación del borrado." });

            // ── Inicio de sesión reciente ────────────────────────────────────
            if (!LoginReciente(decodificado))
            {
                return Results.Json(
                    new { error = "Por seguridad, vuelve a introducir tu contraseña.", codigo = "requiere-login-reciente" },
                    statusCode: StatusCodes.Status401Unauthorized);
            }

            try
            {
                var db = fs.Db;
                var jugadorRef = db.Collection("Jugadores").Document(uid);

                // 1) Firestore primero: si falla, la cuenta sigue existiendo y
                //    el usuario puede reintentar.
                var referencias = new List<DocumentReference>();
                await RecogerRecursivoAsync(jugadorRef, referencias, ctx.RequestAborted);
                await BorrarEnLotesAsync(db, referencias, ctx.RequestAborted);

                // 2) Registro mínimo de auditoría.
                try
                {
                    await db.Collection("AuditoriaCuentas").AddAsync(new Dictionary<string, object?>
                    {
                        ["accion"] = "eliminar-cuenta-propia",
                        ["uidObjetivo"] = uid,
                        ["documentosBorrados"] = referencias.Count,
                        ["fecha"] = Timestamp.GetCurrentTimestamp(),
                    });
                }
                catch (Exception exAud)
                {
                    log.LogError(exAud, "Auditoría NO guardada del borrado de {Uid}", uid);
                }

                // 3) Usuario de Authentication.
                try
                {
                    await FirebaseAuth.DefaultInstance.DeleteUserAsync(uid);
                }
                catch (FirebaseAuthException ex) when (ex.AuthErrorCode == AuthErrorCode.UserNotFound)
                {
                    // Ya no existía: el resultado es el mismo.
                }

                log.LogInformation("Cuenta {Uid} eliminada por su titular ({Docs} documentos)",
                    uid, referencias.Count);
                return Results.Ok(new { ok = true });
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Error eliminando la cuenta {Uid}", uid);
                return Results.Problem(title: "No se pudo eliminar la cuenta. Inténtalo de nuevo.",
                    statusCode: 500);
            }
        });

        return app;
    }

    /// auth_time = segundos UNIX del último inicio de sesión o reautenticación.
    private static bool LoginReciente(FirebaseToken token)
    {
        if (!token.Claims.TryGetValue("auth_time", out var raw) || raw == null)
            return false;
        if (!long.TryParse(raw.ToString(), out var segundos))
            return false;
        var authTime = DateTimeOffset.FromUnixTimeSeconds(segundos);
        return DateTimeOffset.UtcNow - authTime <= MaximaAntiguedadLogin;
    }

    /// Recoge el documento y, en profundidad, todos los de sus subcolecciones.
    /// ListDocumentsAsync incluye documentos "fantasma" (sin campos pero con
    /// subcolecciones), que una consulta normal no devolvería.
    private static async Task RecogerRecursivoAsync(
        DocumentReference doc, List<DocumentReference> acumulado, CancellationToken ct)
    {
        await foreach (var coleccion in doc.ListCollectionsAsync().WithCancellation(ct))
        {
            var hijos = new List<DocumentReference>();
            await foreach (var hijo in coleccion.ListDocumentsAsync().WithCancellation(ct))
                hijos.Add(hijo);

            foreach (var hijo in hijos)
                await RecogerRecursivoAsync(hijo, acumulado, ct);
        }
        acumulado.Add(doc);
    }

    private static async Task BorrarEnLotesAsync(
        FirestoreDb db, List<DocumentReference> referencias, CancellationToken ct)
    {
        const int TamanoLote = 400; // Firestore admite 500 escrituras por lote
        for (var i = 0; i < referencias.Count; i += TamanoLote)
        {
            var lote = db.StartBatch();
            foreach (var r in referencias.Skip(i).Take(TamanoLote))
                lote.Delete(r);
            await lote.CommitAsync(ct);
        }
    }
}

/// Cuerpo de POST /warzero/cuenta/eliminar. El uid NO viaja en el cuerpo: se
/// toma siempre del token verificado.
public class EliminarCuentaRequest
{
    public string Confirmacion { get; set; } = "";
}