using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Google.Cloud.Firestore;

// ─────────────────────────────────────────────────────────────────────────────
// WarZeroEstudio.cs
//
// Registro de PARTIDAS COMPLETAS solo para ESTUDIO (mejorar los bots). Es
// ADICIONAL al informe que ven los jugadores (historialCombates, que sigue
// guardando los últimos turnos): esto guarda, turno a turno, una foto completa
// del estado en una colección aparte (`EstudioPartidas`) que el cliente NUNCA
// lee. Solo se activa si en la partida hay al menos un BOT (campo `botsUids`,
// que el runner del bot marca al unirse).
//
// Se llama SIEMPRE fuera de la transacción de resolución y best-effort: si algo
// falla aquí, NO afecta a la partida.
//
// Estructura en Firestore:
//   EstudioPartidas/{lobbyId}                     → doc meta (se mergea cada turno)
//   EstudioPartidas/{lobbyId}/Turnos/{turno:D4}   → foto por turno resuelto
//
// Se usa subcolección por turno (no un array creciente) para no chocar con el
// límite de 1 MB por documento en partidas largas, y para poder leer turnos
// sueltos al analizarlos.
// ─────────────────────────────────────────────────────────────────────────────
public static class WarZeroEstudio
{
    /// Registra el turno recién resuelto SI la partida incluye algún bot.
    /// `estado` es el estado COMPLETO de la partida tras resolver (el mismo shape
    /// del doc de Partidas, tal cual lo devuelve LeerEstadoAsync). No lanza:
    /// cualquier fallo se traga y se registra por consola.
    public static async Task RegistrarTurnoSiHayBotAsync(
        FirestoreDb db, string lobbyId, Dictionary<string, object?>? estado)
    {
        if (db == null || string.IsNullOrWhiteSpace(lobbyId) || estado == null) return;

        // Solo se estudian partidas CON bot.
        var botsUids = M.List(M.Get(estado, "botsUids"))
            .Select(M.Str).Where(s => s != "").Distinct().ToList();
        if (botsUids.Count == 0) return;

        try
        {
            // Turno recién resuelto = turnoActual − 1 (turnoActual ya apunta al
            // siguiente tras la resolución).
            int turnoResuelto = Math.Max(0, M.Int(M.Get(estado, "turnoActual")) - 1);

            // Logs SOLO de este turno (última entrada del historial), para no
            // duplicar en cada foto los últimos turnos que arrastra el historial.
            var historial = M.List(M.Get(estado, "historialCombates"));
            object? logsTurno = historial.Count > 0 ? historial[^1] : null;

            var estadoPartida = M.Str(M.Get(estado, "estado"));
            object? ganadorUid = M.Get(estado, "ganadorUid");
            object? mapaId = M.Get(estado, "mapaId");
            var botsUidsFs = botsUids.Cast<object?>().ToList();

            var raizRef = db.Collection("EstudioPartidas").Document(lobbyId);

            // ── Foto del turno ──
            var foto = new Dictionary<string, object?>
            {
                ["turno"] = turnoResuelto,
                ["registradoEn"] = Timestamp.FromDateTime(DateTime.UtcNow),
                ["tablero"] = M.Get(estado, "tablero"),
                // statsPartida incluye energía y MANO de cada jugador: es la señal
                // más rica para estudiar qué pudo hacer el bot en cada turno.
                ["statsPartida"] = M.Get(estado, "statsPartida"),
                ["obeliscos"] = M.Get(estado, "obeliscos"),
                ["jugadores"] = M.Get(estado, "jugadores"),
                ["jugadoresEliminados"] = M.Get(estado, "jugadoresEliminados"),
                ["botsUids"] = botsUidsFs,
                ["estado"] = estadoPartida,
                ["ganadorUid"] = ganadorUid,
                ["logsTurno"] = logsTurno,
            };
            await raizRef.Collection("Turnos")
                .Document(turnoResuelto.ToString("D4"))
                .SetAsync(foto);

            // ── Doc meta (para listar / filtrar el corpus de estudio) ──
            var meta = new Dictionary<string, object?>
            {
                ["lobbyId"] = lobbyId,
                ["botsUids"] = botsUidsFs,
                ["jugadores"] = M.Get(estado, "jugadores"),
                ["mapaId"] = mapaId,
                ["ultimoTurno"] = turnoResuelto,
                ["estado"] = estadoPartida,
                ["ganadorUid"] = ganadorUid,
                ["actualizado"] = Timestamp.FromDateTime(DateTime.UtcNow),
            };
            await raizRef.SetAsync(meta, SetOptions.MergeAll);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[WZ][estudio] registrar turno de {lobbyId} falló: {ex}");
        }
    }
}