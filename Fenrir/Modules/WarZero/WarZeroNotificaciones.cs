using FirebaseAdmin;
using FirebaseAdmin.Messaging;
using Google.Apis.Auth.OAuth2;
using Google.Cloud.Firestore;

// ─────────────────────────────────────────────────────────────────────────────
// WarZeroNotificaciones.cs
//
// Notificaciones push (Firebase Cloud Messaging) para iOS y Android.
//
// Se dispara cuando un turno se RESUELVE, ya sea porque:
//   • ha cerrado el último jugador activo (WarZeroService.CerrarTurnoAsync), o
//   • ha vencido la hora límite (WarZeroService.ForzarResolucionSiProcedeAsync).
//
// En ambos casos, tras confirmarse la resolución (fuera de la transacción de
// Firestore, porque enviar por red dentro de una transacción es peligroso y la
// transacción puede reintentarse), se avisa a los jugadores activos de que ya
// pueden jugar el nuevo turno.
//
// Tokens de dispositivo:
//   Cada jugador guarda sus tokens FCM en `Jugadores/{uid}.fcmTokens` (array de
//   strings). El cliente los registra vía POST /warzero/fcm/registrar. Un mismo
//   jugador puede tener varios (móvil + tablet). Los tokens caducados/erróneos
//   se podan automáticamente al enviar.
//
// El mismo mensaje sirve para iOS (APNs) y Android: FCM traduce a cada
// plataforma. Requiere que FirebaseApp esté inicializado (lo está desde el
// arranque para FirebaseAdmin.Auth); por si acaso, EnsureFirebaseApp() lo crea
// perezosamente con la misma credencial que usa Firestore.
// ─────────────────────────────────────────────────────────────────────────────

public static class WarZeroNotificaciones
{
    // Debe coincidir con el channelId del cliente (notificaciones_service.dart).
    private const string CanalAndroid = "turnos_warzero";

    // ── Registro de token FCM de un jugador ─────────────────────────────────
    /// Añade (idempotente) el token FCM del dispositivo al array
    /// `Jugadores/{uid}.fcmTokens`. `platform` ("ios"/"android") se guarda como
    /// metadato informativo. Llamado por POST /warzero/fcm/registrar.
    public static async Task RegistrarTokenAsync(
        FirestoreDb db, string uid, string token, string? platform)
    {
        if (string.IsNullOrWhiteSpace(uid) || string.IsNullOrWhiteSpace(token))
            return;

        var jugRef = db.Collection("Jugadores").Document(uid);
        await jugRef.SetAsync(new Dictionary<string, object>
        {
            ["fcmTokens"] = FieldValue.ArrayUnion(token),
            ["fcmTokensMeta"] = new Dictionary<string, object>
            {
                [token] = new Dictionary<string, object>
                {
                    ["platform"] = platform ?? "desconocida",
                    ["actualizado"] = Timestamp.FromDateTime(DateTime.UtcNow),
                },
            },
        }, SetOptions.MergeAll);
    }

    /// Elimina un token concreto de un jugador (p. ej. al cerrar sesión).
    public static async Task EliminarTokenAsync(
        FirestoreDb db, string uid, string token)
    {
        if (string.IsNullOrWhiteSpace(uid) || string.IsNullOrWhiteSpace(token))
            return;
        var jugRef = db.Collection("Jugadores").Document(uid);
        await jugRef.UpdateAsync("fcmTokens", FieldValue.ArrayRemove(token));
    }

    // ── Aviso de turno resuelto ─────────────────────────────────────────────
    /// Lee el estado actual de la partida y notifica a los jugadores activos de
    /// que el turno se ha resuelto y ya pueden jugar. Si la partida terminó,
    /// envía en su lugar el aviso de fin de partida. Best-effort: cualquier
    /// fallo se registra pero NUNCA propaga (no debe romper el cierre de turno).
    public static async Task NotificarTurnoResueltoAsync(
        FirestoreDb db, string lobbyId, string? excluirUid = null)
    {
        try
        {
            if (!EnsureFirebaseApp()) return; // sin credencial no se puede enviar

            var snap = await db.Collection("Partidas").Document(lobbyId)
                .GetSnapshotAsync();
            if (!snap.Exists) return;

            var data = snap.ToDictionary();

            var estado = Str(Get(data, "estado"));
            var finalizada = estado == "finalizada";
            var turnoNuevo = Int(Get(data, "turnoActual"));
            var nombrePartida = Str(Get(data, "nombre"));
            if (string.IsNullOrWhiteSpace(nombrePartida)) nombrePartida = "WarZero";

            // Jugadores activos (no eliminados) = los que pueden jugar el turno.
            var eliminados = ListStr(Get(data, "jugadoresEliminados"));
            var jugadores = new List<string>();
            if (Get(data, "jugadores") is IEnumerable<object> js)
                foreach (var j in js)
                    if (j is IDictionary<string, object> jm && jm.TryGetValue("uid", out var u))
                    {
                        var uid = u?.ToString() ?? "";
                        if (uid != "") jugadores.Add(uid);
                    }

            // Al terminar la partida se avisa a TODOS los participantes
            // (incluidos los recién eliminados). Al avanzar el turno, solo a los
            // que siguen en juego, y se excluye al jugador que acaba de cerrar
            // (está mirando la partida y no necesita el aviso).
            var destinatarios = finalizada
                ? jugadores
                : jugadores
                    .Where(u => !eliminados.Contains(u) && u != excluirUid)
                    .ToList();

            if (destinatarios.Count == 0) return;

            var titulo = finalizada ? "Partida terminada" : "¡Es tu turno!";
            var cuerpo = finalizada
                ? $"La partida \"{nombrePartida}\" ha terminado. Entra para ver el resultado."
                : $"El turno se ha resuelto en \"{nombrePartida}\". Ya puedes jugar.";

            var tipo = finalizada ? "partida_finalizada" : "turno_resuelto";

            foreach (var uid in destinatarios)
            {
                var tokens = await LeerTokensAsync(db, uid);
                if (tokens.Count == 0) continue;

                var invalidos = new List<string>();
                foreach (var token in tokens)
                {
                    try
                    {
                        await FirebaseMessaging.DefaultInstance.SendAsync(
                            ConstruirMensaje(token, titulo, cuerpo, lobbyId, turnoNuevo, tipo));
                    }
                    catch (FirebaseMessagingException fex)
                    {
                        // Token caducado / desinstalado / inválido → podar.
                        if (fex.MessagingErrorCode == MessagingErrorCode.Unregistered ||
                            fex.MessagingErrorCode == MessagingErrorCode.InvalidArgument ||
                            fex.MessagingErrorCode == MessagingErrorCode.SenderIdMismatch)
                        {
                            invalidos.Add(token);
                        }
                        else
                        {
                            Console.Error.WriteLine(
                                $"[WarZero][FCM] envío falló uid={uid}: {fex.MessagingErrorCode} {fex.Message}");
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"[WarZero][FCM] envío falló uid={uid}: {ex.Message}");
                    }
                }

                if (invalidos.Count > 0)
                {
                    try
                    {
                        await db.Collection("Jugadores").Document(uid).UpdateAsync(
                            "fcmTokens",
                            FieldValue.ArrayRemove(invalidos.Cast<object>().ToArray()));
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"[WarZero][FCM] poda de tokens uid={uid} falló: {ex.Message}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            // Nunca romper el cierre de turno por un fallo de notificación.
            Console.Error.WriteLine($"[WarZero][FCM] NotificarTurnoResuelto lobby={lobbyId} falló: {ex}");
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static Message ConstruirMensaje(
        string token, string titulo, string cuerpo,
        string lobbyId, int turno, string tipo) => new Message
        {
            Token = token,
            Notification = new Notification { Title = titulo, Body = cuerpo },
            // El payload de datos permite al cliente navegar directo a la partida
            // al pulsar la notificación.
            Data = new Dictionary<string, string>
            {
                ["tipo"] = tipo,
                ["lobbyId"] = lobbyId,
                ["turno"] = turno.ToString(),
            },
            Android = new AndroidConfig
            {
                Priority = Priority.High,
                Notification = new AndroidNotification
                {
                    ChannelId = CanalAndroid,
                    Sound = "default",
                },
            },
            Apns = new ApnsConfig
            {
                // iOS 13+ EXIGE el header apns-push-type. Para una alerta visible
                // debe ser "alert" con prioridad 10. Antes se enviaba con
                // ContentAvailable=true (content-available:1), lo que hacía que
                // iOS clasificara el push como SILENCIOSO/background: no mostraba
                // el banner y lo throttleaba. Por eso no llegaban las
                // notificaciones en iOS.
                Headers = new Dictionary<string, string>
                {
                    ["apns-push-type"] = "alert",
                    ["apns-priority"] = "10",
                },
                Aps = new Aps
                {
                    Sound = "default",
                    // Sin ContentAvailable: es una alerta visible (title/body), no
                    // un push silencioso de datos.
                },
            },
        };

    private static async Task<List<string>> LeerTokensAsync(FirestoreDb db, string uid)
    {
        try
        {
            var snap = await db.Collection("Jugadores").Document(uid).GetSnapshotAsync();
            if (!snap.Exists) return new List<string>();
            var data = snap.ToDictionary();
            return ListStr(Get(data, "fcmTokens"))
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Distinct()
                .ToList();
        }
        catch
        {
            return new List<string>();
        }
    }

    /// Garantiza que exista una FirebaseApp por defecto (la usa FirebaseMessaging).
    /// Normalmente ya está creada al arrancar para FirebaseAdmin.Auth; si no,
    /// intenta crearla con la misma credencial que Firestore. Devuelve false si
    /// no hay forma de obtener credencial (no se puede enviar).
    private static bool EnsureFirebaseApp()
    {
        if (FirebaseApp.DefaultInstance != null) return true;

        var candidatos = new[]
        {
            Environment.GetEnvironmentVariable("Firebase__CredentialsPath"),
            "Firebase/firebase-key.json",
            Path.Combine(AppContext.BaseDirectory, "Firebase/firebase-key.json"),
            Path.Combine(Directory.GetCurrentDirectory(), "Firebase/firebase-key.json"),
        };
        var ruta = candidatos.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p) && File.Exists(p));

        try
        {
            if (ruta != null)
            {
                FirebaseApp.Create(new AppOptions
                {
                    Credential = GoogleCredential.FromFile(ruta),
                });
                return true;
            }
            // Último recurso: credencial por defecto del entorno (GOOGLE_APPLICATION_CREDENTIALS).
            FirebaseApp.Create(new AppOptions
            {
                Credential = GoogleCredential.GetApplicationDefault(),
            });
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("[WarZero][FCM] no se pudo inicializar FirebaseApp: " + ex.Message);
            return false;
        }
    }

    // Lectores tolerantes (Firestore devuelve object?).
    private static object? Get(IDictionary<string, object> d, string k) =>
        d != null && d.TryGetValue(k, out var v) ? v : null;

    private static string Str(object? v) => v?.ToString() ?? "";

    private static int Int(object? v) => v switch
    {
        long l => (int)l,
        int i => i,
        double db => (int)db,
        _ => int.TryParse(v?.ToString(), out var n) ? n : 0,
    };

    private static List<string> ListStr(object? v) =>
        v is IEnumerable<object> e
            ? e.Select(x => x?.ToString() ?? "").Where(s => s != "").ToList()
            : new List<string>();
}