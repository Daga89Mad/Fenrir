using Google.Cloud.Firestore;

// ─────────────────────────────────────────────────────────────────────────────
// WarZeroServiceAlianzas.cs  (ARCHIVO NUEVO — añádelo al proyecto Fenrir)
//
// Métodos transaccionales del sistema de alianzas. Es una CONTINUACIÓN de la
// clase WarZeroService (partial), así que comparte el campo privado `_fs`.
//
// ⚠️ REQUISITO: en WarZeroService.cs, cambia la declaración de la clase a:
//        public partial class WarZeroService
//    (basta con añadir la palabra `partial`).
//
// La lógica de negocio (validación y transformación de estado) vive en
// WarZeroAlianzas.cs; aquí solo se orquesta la transacción de Firestore y se
// adjunta el estado resultante + notificaciones best-effort.
// ─────────────────────────────────────────────────────────────────────────────

public partial class WarZeroService
{
    /// POST /warzero/alianza/proponer
    public async Task<AlianzaResponse> ProponerAlianzaAsync(ProponerAlianzaRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.LobbyId) ||
            string.IsNullOrWhiteSpace(req.DeUid) ||
            string.IsNullOrWhiteSpace(req.ParaUid))
            return new AlianzaResponse { Ok = false, Mensaje = "lobbyId, deUid y paraUid son obligatorios" };

        var (ok, msg) = await AplicarAlianzaEnTxAsync(req.LobbyId,
            data => Alianzas.Proponer(data, req.DeUid, req.ParaUid, req.Turnos));

        var resp = new AlianzaResponse { Ok = ok, Mensaje = msg };
        await AdjuntarEstadoAsync(resp, req.LobbyId);

        if (ok)
        {
            try
            {
                await WarZeroNotificaciones.NotificarPropuestaAlianzaAsync(
                    _fs.Db, req.LobbyId, req.DeUid, req.ParaUid);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("[WarZero] notificación propuesta alianza falló: " + ex);
            }
        }
        return resp;
    }

    /// POST /warzero/alianza/responder
    public async Task<AlianzaResponse> ResponderAlianzaAsync(ResponderAlianzaRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.LobbyId) ||
            string.IsNullOrWhiteSpace(req.Uid) ||
            string.IsNullOrWhiteSpace(req.ProponenteUid))
            return new AlianzaResponse { Ok = false, Mensaje = "lobbyId, uid y proponenteUid son obligatorios" };

        var (ok, msg) = await AplicarAlianzaEnTxAsync(req.LobbyId,
            data => Alianzas.Responder(data, req.Uid, req.ProponenteUid, req.Aceptar));

        var resp = new AlianzaResponse { Ok = ok, Mensaje = msg };
        await AdjuntarEstadoAsync(resp, req.LobbyId);
        return resp;
    }

    /// POST /warzero/alianza/traicionar
    public async Task<AlianzaResponse> TraicionarAlianzaAsync(TraicionarAlianzaRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.LobbyId) || string.IsNullOrWhiteSpace(req.Uid))
            return new AlianzaResponse { Ok = false, Mensaje = "lobbyId y uid son obligatorios" };

        var (ok, msg) = await AplicarAlianzaEnTxAsync(req.LobbyId,
            data => Alianzas.Traicionar(data, req.Uid));

        var resp = new AlianzaResponse { Ok = ok, Mensaje = msg };
        await AdjuntarEstadoAsync(resp, req.LobbyId);
        return resp;
    }

    /// POST /warzero/alianza/avisos/limpiar
    /// El cliente lo llama tras mostrar los avisos dirigidos a `uid`.
    public async Task<AlianzaResponse> LimpiarAvisosAlianzaAsync(string lobbyId, string uid)
    {
        if (string.IsNullOrWhiteSpace(lobbyId) || string.IsNullOrWhiteSpace(uid))
            return new AlianzaResponse { Ok = false, Mensaje = "lobbyId y uid son obligatorios" };

        var (ok, msg) = await AplicarAlianzaEnTxAsync(lobbyId,
            data =>
            {
                var nuevo = Alianzas.LimpiarAvisosDe(Alianzas.Leer(data), uid);
                return (true, "Avisos limpiados.", nuevo);
            });

        var resp = new AlianzaResponse { Ok = ok, Mensaje = msg };
        await AdjuntarEstadoAsync(resp, lobbyId);
        return resp;
    }

    // ── Núcleo transaccional compartido ─────────────────────────────────────
    private async Task<(bool ok, string msg)> AplicarAlianzaEnTxAsync(
        string lobbyId,
        Func<Dictionary<string, object?>, (bool ok, string msg, Dictionary<string, object?> nuevo)> fn)
    {
        var db = _fs.Db;
        var lobbyRef = db.Collection("Partidas").Document(lobbyId);

        return await db.RunTransactionAsync(async tx =>
        {
            var snap = await tx.GetSnapshotAsync(lobbyRef);
            if (!snap.Exists) return (false, "La partida no existe");

            var data = M.Map(M.FromFs(snap.ToDictionary()));
            if (M.Str(M.Get(data, "estado")) == "finalizada")
                return (false, "La partida ha terminado");

            var (ok, msg, nuevo) = fn(data);
            if (ok)
                tx.Update(lobbyRef, new Dictionary<string, object>
                {
                    ["alianzas"] = (object)nuevo,
                });
            return (ok, msg);
        });
    }

    private async Task AdjuntarEstadoAsync(AlianzaResponse resp, string lobbyId)
    {
        try { resp.Estado = await LeerEstadoAsync(lobbyId); }
        catch (Exception ex)
        {
            Console.Error.WriteLine("[WarZero] LeerEstado tras operación de alianza falló: " + ex);
        }
    }
}