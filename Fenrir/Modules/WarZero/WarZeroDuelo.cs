using Google.Cloud.Firestore;

// ─────────────────────────────────────────────────────────────────────────────
// WarZeroDuelo.cs
//
// LLUVIA DE ROCAS MANUAL del duelo invertido (HistoriaDuelo.cs, reto «El duelo
// de Alexander»). El jugador, que lleva al jefe, la lanza desde la carta de
// Alexander (botón de habilidad, sin coste) cuando no está recargando, y elige
// TODAS las casillas donde cae una roca: como mucho `RocasPorFila` por fila y
// `MaxFilasLluvia` filas distintas (más con la furia). El turno en que la lanza
// no se mueve, y después necesita `LluviaRecarga` turnos.
//
// El cliente la declara con POST /warzero/duelo/lluvia (WarZeroRetosEndpoints)
// ANTES de cerrar el turno; se guarda en `duelo.lluviaJugador = {turno,
// coords}` y la consume la resolución del turno (MotorDuelo.Resolver). Se
// puede cambiar o anular (coords vacía) mientras el jugador no haya cerrado
// su turno. La IA de los generales no lee este campo.
//
// Va por su propio endpoint (y no dentro de la jugada) para no tocar el
// formato de las jugadas ni el cierre de turno: la resolución ya lee el campo
// `duelo` de la partida.
// ─────────────────────────────────────────────────────────────────────────────

public partial class WarZeroService
{
    /// Declara (o anula, con coords vacía) la lluvia de rocas del jugador para
    /// el turno en curso. Devuelve {ok, turno, coords} o {ok:false, error}.
    public async Task<Dictionary<string, object?>> DeclararLluviaDueloAsync(DeclararLluviaRequest req)
    {
        Dictionary<string, object?> Error(string msg) => new() { ["ok"] = false, ["error"] = msg };
        if (string.IsNullOrWhiteSpace(req.Uid) || string.IsNullOrWhiteSpace(req.LobbyId))
            return Error("uid y lobbyId son obligatorios");

        var coords = (req.Coords ?? new List<string>())
            .Select(c => (c ?? "").Trim().ToUpperInvariant())
            .Where(c => c != "")
            .ToList();
        var lobbyRef = _fs.Db.Collection("Partidas").Document(req.LobbyId);

        return await _fs.Db.RunTransactionAsync(async tx =>
        {
            var snap = await tx.GetSnapshotAsync(lobbyRef);
            if (!snap.Exists) return Error("la partida no existe");
            var data = M.Map(M.FromFs(snap.ToDictionary()));

            if (M.Str(M.Get(data, "estado")) != "en_curso") return Error("la partida no está en curso");
            if (!M.Bool(M.Get(data, "esHistoria"))) return Error("no es una batalla de historia");

            var hist = M.Map(M.Get(data, "historia"));
            var jugadorUid = M.Str(M.Get(hist, "jugadorUid"));
            if (jugadorUid != req.Uid) return Error("no eres el jugador de esta batalla");

            var cfg = HistoriaDuelos.Get(M.Str(M.Get(hist, "id")));
            if (cfg == null || !cfg.JugadorEsJefe || !cfg.LluviaManual)
                return Error("esta batalla no tiene lluvia de rocas manual");

            int turno = M.Int(M.Get(data, "turnoActual"));
            if (req.Turno > 0 && req.Turno != turno) return Error($"el turno en curso es el {turno}");

            var cerrado = M.List(M.Get(data, "cerradoPor")).Select(M.Str).Contains(req.Uid);
            if (cerrado) return Error("ya has cerrado el turno");

            var duelo = EstadoDuelo.DesdeCampo(M.Get(data, "duelo"));
            if (duelo == null) return Error("la batalla no tiene duelo");
            MotorDuelo.CompletarDuenos(cfg, duelo, jugadorUid, M.Str(M.Get(hist, "botUid")));
            if (duelo.VidasDe(EstadoDuelo.RolJefe) <= 0) return Error("Alexander ha caído");

            if (coords.Count > 0)
            {
                if (!duelo.LluviaDisponible(cfg, turno))
                {
                    int lista = duelo.LluviaUltimoTurno + Math.Max(0, cfg.LluviaRecarga) + 1;
                    return Error($"la lluvia de rocas se está recargando (lista en el turno {lista})");
                }
                var mapaH = M.Map(M.Get(hist, "mapa"));
                int filas = M.Int(M.Get(mapaH, "filas"));
                int columnas = M.Int(M.Get(mapaH, "columnas"));
                var tablero = M.Map(M.Get(data, "tablero"))
                    .ToDictionary(kv => kv.Key, kv => M.List(kv.Value).Select(M.Map).ToList());
                var celdaJefe = MotorDuelo.CeldaDe(tablero, duelo.IdDe(EstadoDuelo.RolJefe));
                var error = MotorDuelo.ValidarLluvia(cfg, duelo, coords, celdaJefe, filas, columnas);
                if (error != null) return Error(error);
            }

            tx.Update(lobbyRef, new Dictionary<string, object>
            {
                ["duelo.lluviaJugador"] = new Dictionary<string, object>
                {
                    ["turno"] = (long)turno,
                    ["coords"] = coords.Cast<object>().ToList(),
                },
            });

            Console.WriteLine(coords.Count == 0
                ? $"[WZ.Duelo] {req.LobbyId} turno {turno}: lluvia de rocas anulada"
                : $"[WZ.Duelo] {req.LobbyId} turno {turno}: lluvia de rocas en {string.Join(", ", coords)}");
            return new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["turno"] = (long)turno,
                ["coords"] = coords.Cast<object?>().ToList(),
            };
        });
    }
}

/// Cuerpo de POST /warzero/duelo/lluvia. `Coords` vacía = anular.
public class DeclararLluviaRequest
{
    public string Uid { get; set; } = "";
    public string LobbyId { get; set; } = "";
    public int Turno { get; set; }
    public List<string>? Coords { get; set; }
}