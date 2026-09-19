// ─────────────────────────────────────────────────────────────────────────────
// WarZeroAdminCuentas.cs
//
// Endpoints de ADMINISTRACIÓN DE CUENTAS (solo editores).
//
// SEGURIDAD
//   · Cada petición debe llevar  Authorization: Bearer <ID token de Firebase>
//     (en Flutter: FirebaseAuth.instance.currentUser!.getIdToken()).
//   · El token se valida con el Admin SDK (firma, caducidad y revocación).
//   · El usuario debe tener el CUSTOM CLAIM  editor = true. La lista de
//     permisos.dart en Flutter solo decide qué pantallas se VEN; quien decide
//     qué se PUEDE HACER es este claim, que solo el servidor puede asignar.
//
// ASIGNAR EL CLAIM (una vez por editor)
//   Define en Render la variable de entorno  WARZERO_ADMIN_SECRET  con un
//   valor largo y aleatorio, y llama a:
//
//     curl -X POST https://fenrirv2.onrender.com/admin/editores/claim \
//       -H "Content-Type: application/json" \
//       -H "X-Admin-Secret: <tu secreto>" \
//       -d '{"email":"dagahh89@gmail.com","editor":true}'
//
//   Sin la variable definida, ese endpoint queda desactivado.
//   El editor debe cerrar y abrir sesión (o esperar ~1 h) para que su token
//   incluya el claim; el cliente Flutter fuerza la renovación si recibe 403.
//
// ENDPOINTS
//   POST /admin/editores/claim            (X-Admin-Secret)  asignar/quitar editor
//   GET  /admin/cuentas/buscar?q=...      (editor)          email, UID o alias
//   POST /admin/cuentas/cambiar-email     (editor)          cambia el correo
//   POST /admin/cartas/enviar             (editor)          envía una carta a un jugador
//
// AUDITORÍA
//   Cada cambio se registra en la colección Firestore `AuditoriaCuentas`.
//   Recomendado en las reglas de Firestore (el Admin SDK no las necesita):
//     match /AuditoriaCuentas/{doc} { allow read, write: if false; }
//
// Se registra en Program.cs con:  app.MapWarZeroAdminEndpoints();
// ─────────────────────────────────────────────────────────────────────────────

using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using FirebaseAdmin.Auth;
using Google.Cloud.Firestore;

public static class WarZeroAdminCuentasExtensions
{
    private const string ClaimEditor = "editor";
    private const string ColeccionAuditoria = "AuditoriaCuentas";

    public static WebApplication MapWarZeroAdminEndpoints(this WebApplication app)
    {
        // ── Asignar / quitar el claim de editor ──────────────────────────────
        // POST /admin/editores/claim   { email, editor }
        // Protegido por secreto de servidor, NO por token: sirve para dar de
        // alta al primer editor, cuando aún nadie tiene el claim.
        app.MapPost("/admin/editores/claim", async (
            HttpContext ctx, IConfiguration cfg, AsignarEditorRequest req, ILoggerFactory lf) =>
        {
            var log = lf.CreateLogger("WarZero.AdminClaim");

            var secreto = cfg["WARZERO_ADMIN_SECRET"];
            if (string.IsNullOrWhiteSpace(secreto))
                return Results.NotFound();

            var recibido = ctx.Request.Headers["X-Admin-Secret"].ToString();
            if (!SecretoValido(recibido, secreto))
            {
                log.LogWarning("Intento de asignar claim con secreto inválido desde {Ip}",
                    ctx.Connection.RemoteIpAddress);
                return Results.Unauthorized();
            }

            if (string.IsNullOrWhiteSpace(req.Email))
                return Results.BadRequest(new { error = "Falta el email." });

            try
            {
                var auth = FirebaseAuth.DefaultInstance;
                var user = await auth.GetUserByEmailAsync(req.Email.Trim());

                // SetCustomUserClaimsAsync REEMPLAZA todos los claims: se
                // conservan los que ya tuviera y solo se toca "editor".
                var claims = new Dictionary<string, object>(
                    user.CustomClaims ?? new Dictionary<string, object>());
                if (req.Editor) claims[ClaimEditor] = true;
                else claims.Remove(ClaimEditor);

                await auth.SetCustomUserClaimsAsync(user.Uid, claims);
                log.LogInformation("Claim editor={Editor} para {Uid} ({Email})",
                    req.Editor, user.Uid, user.Email);

                return Results.Ok(new { ok = true, uid = user.Uid, email = user.Email, editor = req.Editor });
            }
            catch (FirebaseAuthException ex) when (ex.AuthErrorCode == AuthErrorCode.UserNotFound)
            {
                return Results.NotFound(new { error = "No existe una cuenta con ese correo." });
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Error asignando claim a {Email}", req.Email);
                return Results.Problem(title: "Error asignando el claim", detail: Describe(ex), statusCode: 500);
            }
        });

        // ── Buscar cuentas ───────────────────────────────────────────────────
        // GET /admin/cuentas/buscar?q=...
        //   · contiene '@'  → búsqueda por correo exacto
        //   · si no         → se prueba como UID y como alias exacto
        app.MapGet("/admin/cuentas/buscar", async (
            HttpContext ctx, WarZeroFirestore fs, string? q, ILoggerFactory lf) =>
        {
            var log = lf.CreateLogger("WarZero.AdminBuscar");

            var editor = await VerificarEditorAsync(ctx);
            if (editor.Error != null) return editor.Error;

            var texto = (q ?? "").Trim();
            if (texto.Length < 3)
                return Results.BadRequest(new { error = "Escribe al menos 3 caracteres." });

            try
            {
                var auth = FirebaseAuth.DefaultInstance;
                var encontrados = new Dictionary<string, UserRecord>();

                if (texto.Contains('@'))
                {
                    var u = await BuscarPorEmailAsync(auth, texto);
                    if (u != null) encontrados[u.Uid] = u;
                }
                else
                {
                    var porUid = await BuscarPorUidAsync(auth, texto);
                    if (porUid != null) encontrados[porUid.Uid] = porUid;

                    var snap = await fs.Db.Collection("Jugadores")
                        .WhereEqualTo("alias", texto)
                        .Limit(10)
                        .GetSnapshotAsync();
                    foreach (var doc in snap.Documents)
                    {
                        if (encontrados.ContainsKey(doc.Id)) continue;
                        var u = await BuscarPorUidAsync(auth, doc.Id);
                        if (u != null) encontrados[u.Uid] = u;
                    }
                }

                // Alias de cada cuenta encontrada (desde Jugadores/{uid}).
                var cuentas = new List<object>();
                foreach (var u in encontrados.Values)
                {
                    string alias = "";
                    try
                    {
                        var d = await fs.Db.Collection("Jugadores").Document(u.Uid).GetSnapshotAsync();
                        if (d.Exists && d.TryGetValue<string>("alias", out var a)) alias = a ?? "";
                    }
                    catch { /* alias opcional */ }

                    cuentas.Add(new
                    {
                        uid = u.Uid,
                        email = u.Email,
                        alias,
                        emailVerificado = u.EmailVerified,
                        deshabilitada = u.Disabled,
                        esEditor = EsEditor(u.CustomClaims),
                        creada = u.UserMetaData?.CreationTimestamp,
                        ultimoAcceso = u.UserMetaData?.LastSignInTimestamp,
                    });
                }

                return Results.Ok(new { cuentas });
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Error buscando cuentas q={Q}", texto);
                return Results.Problem(title: "Error al buscar cuentas", detail: Describe(ex), statusCode: 500);
            }
        });

        // ── Cambiar el correo de otra cuenta ─────────────────────────────────
        // POST /admin/cuentas/cambiar-email
        //   { uid, nuevoEmail, motivo, cerrarSesiones }
        //
        // El cambio es INMEDIATO y sin verificación del correo nuevo (lo hace el
        // Admin SDK). Por eso se exige motivo, se audita, y el cliente envía
        // después un correo de restablecer contraseña a la dirección nueva.
        app.MapPost("/admin/cuentas/cambiar-email", async (
            HttpContext ctx, WarZeroFirestore fs, CambiarEmailAdminRequest req, ILoggerFactory lf) =>
        {
            var log = lf.CreateLogger("WarZero.AdminCambiarEmail");

            var editor = await VerificarEditorAsync(ctx);
            if (editor.Error != null) return editor.Error;

            var uid = (req.Uid ?? "").Trim();
            var nuevo = (req.NuevoEmail ?? "").Trim().ToLowerInvariant();
            var motivo = (req.Motivo ?? "").Trim();

            if (uid.Length == 0)
                return Results.BadRequest(new { error = "Falta el UID de la cuenta." });
            if (!EmailValido(nuevo))
                return Results.BadRequest(new { error = "El correo nuevo no es válido." });
            if (motivo.Length < 5)
                return Results.BadRequest(new { error = "Indica el motivo del cambio (mínimo 5 caracteres)." });
            if (uid == editor.Uid)
                return Results.BadRequest(new { error = "Para cambiar tu propio correo usa tu perfil." });

            try
            {
                var auth = FirebaseAuth.DefaultInstance;

                var objetivo = await BuscarPorUidAsync(auth, uid);
                if (objetivo == null)
                    return Results.NotFound(new { error = "La cuenta ya no existe." });

                var anterior = objetivo.Email ?? "";
                if (string.Equals(anterior, nuevo, StringComparison.OrdinalIgnoreCase))
                    return Results.BadRequest(new { error = "El correo nuevo es igual al actual." });

                var ocupado = await BuscarPorEmailAsync(auth, nuevo);
                if (ocupado != null)
                    return Results.Conflict(new { error = "Ese correo ya pertenece a otra cuenta." });

                await auth.UpdateUserAsync(new UserRecordArgs
                {
                    Uid = uid,
                    Email = nuevo,
                    EmailVerified = false,
                });

                var sesionesCerradas = false;
                if (req.CerrarSesiones)
                {
                    await auth.RevokeRefreshTokensAsync(uid);
                    sesionesCerradas = true;
                }

                // Auditoría (si falla, el cambio ya está hecho: se registra en log).
                try
                {
                    await fs.Db.Collection(ColeccionAuditoria).AddAsync(new Dictionary<string, object?>
                    {
                        ["accion"] = "cambiar-email",
                        ["uidObjetivo"] = uid,
                        ["emailAnterior"] = anterior,
                        ["emailNuevo"] = nuevo,
                        ["motivo"] = motivo,
                        ["sesionesCerradas"] = sesionesCerradas,
                        ["editorUid"] = editor.Uid,
                        ["editorEmail"] = editor.Email,
                        ["fecha"] = Timestamp.GetCurrentTimestamp(),
                    });
                }
                catch (Exception exAud)
                {
                    log.LogError(exAud, "Auditoría NO guardada: {Editor} cambió {Uid} {Anterior} -> {Nuevo}",
                        editor.Email, uid, anterior, nuevo);
                }

                log.LogInformation("Editor {Editor} cambió el correo de {Uid}: {Anterior} -> {Nuevo}",
                    editor.Email, uid, anterior, nuevo);

                return Results.Ok(new
                {
                    ok = true,
                    uid,
                    emailAnterior = anterior,
                    emailNuevo = nuevo,
                    sesionesCerradas,
                });
            }
            catch (FirebaseAuthException ex) when (ex.AuthErrorCode == AuthErrorCode.EmailAlreadyExists)
            {
                return Results.Conflict(new { error = "Ese correo ya pertenece a otra cuenta." });
            }
            catch (FirebaseAuthException ex) when (ex.AuthErrorCode == AuthErrorCode.UserNotFound)
            {
                return Results.NotFound(new { error = "La cuenta ya no existe." });
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Error cambiando email de {Uid}", uid);
                return Results.Problem(title: "Error al cambiar el correo", detail: Describe(ex), statusCode: 500);
            }
        });

        // ── Enviar una carta a UN jugador (por correo) ───────────────────────
        // POST /admin/cartas/enviar  { email, cartaId, cantidad?, motivo? }
        //
        // Misma escritura que al abrir un sobre: si el jugador ya tiene la
        // carta, suma `cantidad` copias; si no, crea la entrada. Se hace en
        // transacción leyendo la cantidad actual, porque hay entradas antiguas
        // con `cantidad` guardada como texto ("1") y FieldValue.Increment sobre
        // un texto la sobrescribiría en vez de sumar.
        app.MapPost("/admin/cartas/enviar", async (
            HttpContext ctx, WarZeroFirestore fs, EnviarCartaJugadorRequest req, ILoggerFactory lf) =>
        {
            var log = lf.CreateLogger("WarZero.AdminEnviarCarta");

            var editor = await VerificarEditorAsync(ctx);
            if (editor.Error != null) return editor.Error;

            var email = (req.Email ?? "").Trim().ToLowerInvariant();
            var cartaId = (req.CartaId ?? "").Trim();
            var cantidad = req.Cantidad <= 0 ? 1 : req.Cantidad;
            var motivo = (req.Motivo ?? "").Trim();

            if (!EmailValido(email))
                return Results.BadRequest(new { error = "El correo no es válido." });
            if (cartaId.Length == 0)
                return Results.BadRequest(new { error = "Falta la carta." });
            if (cantidad > MaxCopiasPorEnvio)
                return Results.BadRequest(new { error = $"Máximo {MaxCopiasPorEnvio} copias por envío." });

            try
            {
                var auth = FirebaseAuth.DefaultInstance;
                var db = fs.Db;

                var user = await BuscarPorEmailAsync(auth, email);
                if (user == null)
                    return Results.NotFound(new { error = "No existe ningún jugador con ese correo." });

                var jugadorSnap = await db.Collection("Jugadores").Document(user.Uid).GetSnapshotAsync();
                if (!jugadorSnap.Exists)
                    return Results.NotFound(new { error = "La cuenta existe pero no tiene perfil de jugador." });
                var alias = jugadorSnap.TryGetValue<string>("alias", out var a) ? a ?? "" : "";

                var cartaSnap = await db.Collection("Cartas").Document(cartaId).GetSnapshotAsync();
                if (!cartaSnap.Exists)
                    return Results.NotFound(new { error = "La carta no existe en el catálogo." });
                var nombreCarta = M.Str(M.Get(M.Map(cartaSnap.ToDictionary()), "Nombre", "nombre"));

                var colRef = db.Collection("Jugadores").Document(user.Uid)
                               .Collection("Coleccion").Document(cartaId);

                var (anterior, nueva, esNueva) = await db.RunTransactionAsync(async tx =>
                {
                    var snap = await tx.GetSnapshotAsync(colRef);
                    var actual = 0;
                    var nuevaEntrada = true;
                    var eraPlaceholder = false;

                    if (snap.Exists)
                    {
                        var d = M.Map(snap.ToDictionary());
                        eraPlaceholder = M.Bool(M.Get(d, "placeholder"));
                        if (!eraPlaceholder)
                        {
                            nuevaEntrada = false;
                            var raw = M.Get(d, "cantidad");
                            // Cantidad ausente = 1 (mismo criterio que la colección).
                            actual = raw == null ? 1 : Math.Max(1, M.Int(raw));
                        }
                    }

                    var data = new Dictionary<string, object?>
                    {
                        ["cantidad"] = actual + cantidad,
                        ["vecesObtenida"] = FieldValue.Increment(cantidad),
                        ["fechaObtenida"] = FieldValue.ServerTimestamp,
                    };
                    if (!snap.Exists) data["skinsDesbloqueadas"] = new List<object?>();
                    if (eraPlaceholder) data["placeholder"] = FieldValue.Delete;

                    tx.Set(colRef, data, SetOptions.MergeAll);
                    return (actual, actual + cantidad, nuevaEntrada);
                });

                try
                {
                    await db.Collection(ColeccionAuditoria).AddAsync(new Dictionary<string, object?>
                    {
                        ["accion"] = "enviar-carta",
                        ["uidObjetivo"] = user.Uid,
                        ["emailObjetivo"] = user.Email,
                        ["cartaId"] = cartaId,
                        ["nombreCarta"] = nombreCarta,
                        ["copias"] = cantidad,
                        ["cantidadAnterior"] = anterior,
                        ["cantidadNueva"] = nueva,
                        ["motivo"] = motivo,
                        ["editorUid"] = editor.Uid,
                        ["editorEmail"] = editor.Email,
                        ["fecha"] = Timestamp.GetCurrentTimestamp(),
                    });
                }
                catch (Exception exAud)
                {
                    log.LogError(exAud, "Auditoría NO guardada: {Editor} envió {Carta} x{N} a {Uid}",
                        editor.Email, cartaId, cantidad, user.Uid);
                }

                log.LogInformation("Editor {Editor} envió {Carta} x{N} a {Uid} ({Email}): {Ant} -> {Nue}",
                    editor.Email, cartaId, cantidad, user.Uid, user.Email, anterior, nueva);

                return Results.Ok(new
                {
                    ok = true,
                    uid = user.Uid,
                    email = user.Email,
                    alias,
                    cartaId,
                    nombreCarta,
                    nueva = esNueva,
                    cantidadAnterior = anterior,
                    cantidadNueva = nueva,
                });
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Error enviando carta {Carta} a {Email}", cartaId, email);
                return Results.Problem(title: "Error al enviar la carta", detail: Describe(ex), statusCode: 500);
            }
        });

        return app;
    }

    /// Límite de copias por envío, para evitar errores de dedo.
    private const int MaxCopiasPorEnvio = 50;

    // ─────────────────────────────────────────────────────────────────────────
    // VERIFICACIÓN DEL EDITOR
    // ─────────────────────────────────────────────────────────────────────────

    private sealed record EditorVerificado(string Uid, string Email, IResult? Error);

    /// Valida el ID token de Firebase del header Authorization y exige el
    /// claim editor=true. Devuelve Error != null si hay que cortar la petición.
    private static async Task<EditorVerificado> VerificarEditorAsync(HttpContext ctx)
    {
        var header = ctx.Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return new("", "", Results.Unauthorized());

        var token = header["Bearer ".Length..].Trim();
        if (token.Length == 0)
            return new("", "", Results.Unauthorized());

        FirebaseToken decodificado;
        try
        {
            // checkRevoked: true → rechaza tokens de sesiones revocadas.
            decodificado = await FirebaseAuth.DefaultInstance.VerifyIdTokenAsync(token, checkRevoked: true);
        }
        catch (FirebaseAuthException)
        {
            return new("", "", Results.Unauthorized());
        }

        if (!EsEditor(decodificado.Claims))
            return new(decodificado.Uid, "", Results.StatusCode(StatusCodes.Status403Forbidden));

        var email = decodificado.Claims.TryGetValue("email", out var e) ? e?.ToString() ?? "" : "";
        return new(decodificado.Uid, email, null);
    }

    /// El valor puede llegar como bool o como token JSON según de dónde se
    /// lea (token decodificado o UserRecord), así que se compara como texto.
    private static bool EsEditor(IReadOnlyDictionary<string, object>? claims) =>
        claims != null
        && claims.TryGetValue(ClaimEditor, out var v)
        && string.Equals(v?.ToString(), "true", StringComparison.OrdinalIgnoreCase);

    // ─────────────────────────────────────────────────────────────────────────
    // HELPERS
    // ─────────────────────────────────────────────────────────────────────────

    private static async Task<UserRecord?> BuscarPorEmailAsync(FirebaseAuth auth, string email)
    {
        try { return await auth.GetUserByEmailAsync(email); }
        catch (FirebaseAuthException ex) when (ex.AuthErrorCode == AuthErrorCode.UserNotFound) { return null; }
        catch (ArgumentException) { return null; }
    }

    private static async Task<UserRecord?> BuscarPorUidAsync(FirebaseAuth auth, string uid)
    {
        if (uid.Length == 0 || uid.Length > 128) return null;
        try { return await auth.GetUserAsync(uid); }
        catch (FirebaseAuthException ex) when (ex.AuthErrorCode == AuthErrorCode.UserNotFound) { return null; }
        catch (ArgumentException) { return null; }
    }

    private static bool EmailValido(string email)
    {
        if (email.Length < 5 || email.Length > 254 || !email.Contains('@')) return false;
        try
        {
            var m = new MailAddress(email);
            return m.Address.Equals(email, StringComparison.OrdinalIgnoreCase)
                   && m.Host.Contains('.');
        }
        catch { return false; }
    }

    /// Comparación en tiempo constante para no filtrar el secreto por tiempos.
    private static bool SecretoValido(string recibido, string esperado)
    {
        var a = Encoding.UTF8.GetBytes(recibido ?? "");
        var b = Encoding.UTF8.GetBytes(esperado);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }

    private static string Describe(Exception ex)
    {
        var partes = new List<string>();
        for (Exception? e = ex; e != null; e = e.InnerException)
            partes.Add($"{e.GetType().Name}: {e.Message}");
        return string.Join(" <- ", partes);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// DTOs
// ─────────────────────────────────────────────────────────────────────────────

public record AsignarEditorRequest(string Email, bool Editor = true);

public record EnviarCartaJugadorRequest(
    string Email,
    string CartaId,
    int Cantidad = 1,
    string? Motivo = null);

public record CambiarEmailAdminRequest(
    string Uid,
    string NuevoEmail,
    string? Motivo,
    bool CerrarSesiones = true);