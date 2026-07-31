using FirebaseAdmin.Messaging;
using Google.Cloud.Firestore;

// ─────────────────────────────────────────────────────────────────────────────
// WarZeroNotificacionesAlianza.cs  (ARCHIVO NUEVO — añádelo al proyecto Fenrir)
//
// Notificaciones push del sistema de alianzas. Es una CONTINUACIÓN de la clase
// WarZeroNotificaciones (partial), así que reutiliza sus helpers privados
// (ConstruirMensaje, LeerTokensAsync, EnsureFirebaseApp, Get/Str/Int/ListStr).
//
// ⚠️ REQUISITO: en WarZeroNotificaciones.cs, cambia la declaración de la clase a:
//        public static partial class WarZeroNotificaciones
//    (basta con añadir la palabra `partial`).
//
// Todo es best-effort: cualquier fallo se registra pero NUNCA propaga.
// ─────────────────────────────────────────────────────────────────────────────

public static partial class WarZeroNotificaciones
{
    /// Avisa al jugador `paraUid` de que `deUid` le ha propuesto una alianza.
    /// El aviso in-app ya está en el estado (alianzas.propuestas); este push es
    /// solo para que le llegue aunque no tenga la app abierta.
    public static async Task NotificarPropuestaAlianzaAsync(
        FirestoreDb db, string lobbyId, string deUid, string paraUid)
    {
        try
        {
            if (!EnsureFirebaseApp()) return;
            if (string.IsNullOrWhiteSpace(paraUid)) return;

            var lobbySnap = await db.Collection("Partidas").Document(lobbyId).GetSnapshotAsync();
            if (!lobbySnap.Exists) return;
            var lobby = lobbySnap.ToDictionary();
            var turno = Int(Get(lobby, "turnoActual"));
            var nombrePartida = Str(Get(lobby, "nombre"));
            if (string.IsNullOrWhiteSpace(nombrePartida)) nombrePartida = "WarZero";

            var aliasProponente = await LeerAliasAsync(db, deUid);
            var titulo = "Propuesta de alianza";
            var cuerpo = string.IsNullOrWhiteSpace(aliasProponente)
                ? $"Un jugador te propone una alianza en \"{nombrePartida}\"."
                : $"{aliasProponente} te propone una alianza en \"{nombrePartida}\".";

            await EnviarAUidAsync(db, paraUid, titulo, cuerpo, lobbyId, turno, "alianza_propuesta");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[WarZero][FCM] NotificarPropuestaAlianza lobby={lobbyId} falló: {ex}");
        }
    }

    /// Avisa a las víctimas de las traiciones resueltas en `turnoResuelto`. Lee
    /// los avisos de tipo "traicionado" del estado y envía push a cada víctima.
    /// Se llama DESPUÉS de resolver el turno (fuera de la transacción).
    public static async Task NotificarTraicionesAsync(
        FirestoreDb db, string lobbyId, int turnoResuelto)
    {
        try
        {
            if (!EnsureFirebaseApp()) return;

            var snap = await db.Collection("Partidas").Document(lobbyId).GetSnapshotAsync();
            if (!snap.Exists) return;
            var data = snap.ToDictionary();
            var nombrePartida = Str(Get(data, "nombre"));
            if (string.IsNullOrWhiteSpace(nombrePartida)) nombrePartida = "WarZero";
            var turno = Int(Get(data, "turnoActual"));

            // alianzas.avisos → filtrar tipo "traicionado" del turno resuelto.
            if (Get(data, "alianzas") is not IDictionary<string, object> alianzas) return;
            if (Get(alianzas, "avisos") is not IEnumerable<object> avisos) return;

            foreach (var av in avisos)
            {
                if (av is not IDictionary<string, object> am) continue;
                if (Str(Get(am, "tipo")) != "traicionado") continue;
                if (Int(Get(am, "turno")) != turnoResuelto) continue;
                var paraUid = Str(Get(am, "paraUid"));
                if (paraUid == "") continue;

                var titulo = "Te han traicionado";
                var cuerpo = $"Tu aliado ha roto la alianza en \"{nombrePartida}\". Ya sois enemigos.";
                await EnviarAUidAsync(db, paraUid, titulo, cuerpo, lobbyId, turno, "alianza_traicion");
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[WarZero][FCM] NotificarTraiciones lobby={lobbyId} falló: {ex}");
        }
    }

    // ── Helpers privados de este archivo ────────────────────────────────────

    private static async Task EnviarAUidAsync(
        FirestoreDb db, string uid, string titulo, string cuerpo,
        string lobbyId, int turno, string tipo)
    {
        var tokens = await LeerTokensAsync(db, uid);
        if (tokens.Count == 0) return;

        var invalidos = new List<string>();
        foreach (var token in tokens)
        {
            try
            {
                await FirebaseMessaging.DefaultInstance.SendAsync(
                    ConstruirMensaje(token, titulo, cuerpo, lobbyId, turno, tipo));
            }
            catch (FirebaseMessagingException fex)
            {
                if (fex.MessagingErrorCode == MessagingErrorCode.Unregistered ||
                    fex.MessagingErrorCode == MessagingErrorCode.InvalidArgument ||
                    fex.MessagingErrorCode == MessagingErrorCode.SenderIdMismatch)
                    invalidos.Add(token);
                else
                    Console.Error.WriteLine(
                        $"[WarZero][FCM] alianza envío falló uid={uid}: {fex.MessagingErrorCode} {fex.Message}");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[WarZero][FCM] alianza envío falló uid={uid}: {ex.Message}");
            }
        }

        if (invalidos.Count > 0)
        {
            try
            {
                await db.Collection("Jugadores").Document(uid).UpdateAsync(
                    "fcmTokens", FieldValue.ArrayRemove(invalidos.Cast<object>().ToArray()));
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[WarZero][FCM] alianza poda tokens uid={uid} falló: {ex.Message}");
            }
        }
    }

    private static async Task<string> LeerAliasAsync(FirestoreDb db, string uid)
    {
        try
        {
            var snap = await db.Collection("Jugadores").Document(uid).GetSnapshotAsync();
            if (!snap.Exists) return "";
            return Str(Get(snap.ToDictionary(), "alias"));
        }
        catch { return ""; }
    }
}