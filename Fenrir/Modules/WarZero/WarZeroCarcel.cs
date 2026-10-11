using Google.Cloud.Firestore;

// ─────────────────────────────────────────────────────────────────────────────
// WarZeroCarcel.cs
//
// Piezas de servicio de LA CÁRCEL BIÓNICA (HistoriaCarcel.cs, nefilim_1):
//
//   • ConstruirJugadaCarcel: jugada del bot. Los guardias no se mueven,
//     salvo el PORTADOR de un núcleo de la sala activa, que se aleja una
//     casilla si Alexander está a 2 o menos (HistoriaCarcel.PlanHuida). Los
//     refuerzos de la alarma se quedan en su puerta.
//
//   • DeclararOdioCarcelAsync: el ODIO de Alexander. El jugador lo desata
//     desde la ficha de Alexander (botón de habilidad, sin coste) ANTES de
//     cerrar el turno; se guarda en `carcel.odio.turno` y lo consume la
//     resolución del turno (HistoriaCarcel.AplicarOdio, paso A). Se puede
//     anular mientras el jugador no haya cerrado su turno. Una sola vez por
//     batalla. Va por su propio endpoint (POST /warzero/carcel/odio) para no
//     tocar el formato de las jugadas, igual que la lluvia del duelo.
// ─────────────────────────────────────────────────────────────────────────────

public partial class WarZeroService
{
    /// Jugada del bot en la cárcel: re-emite todas sus cartas donde están,
    /// salvo los portadores que huyen.
    private static Dictionary<string, object?> ConstruirJugadaCarcel(
        Dictionary<string, object?> data, string botUid, int turno, ConfigCarcel cfg)
    {
        var hist = M.Map(M.Get(data, "historia"));
        var tablero = M.Map(M.Get(data, "tablero"))
            .ToDictionary(kv => kv.Key, kv => M.List(kv.Value).Select(M.Map).ToList());

        var estado = EstadoCarcel.DesdeCampo(M.Get(data, "carcel"));
        var huida = estado != null
            ? HistoriaCarcel.PlanHuida(cfg, estado, SemillaPartida(hist), tablero)
            : new Dictionary<string, string>();

        var celdas = new Dictionary<string, object?>();
        void Colocar(string coord, object? carta)
        {
            if (celdas.TryGetValue(coord, out var lst) && lst is List<object?> l) l.Add(carta);
            else celdas[coord] = new List<object?> { carta };
        }

        foreach (var (coord, lst) in tablero)
            foreach (var carta in lst)
            {
                if (CartaHelper.OwnerUid(carta) != botUid) continue;
                var iid = M.Str(M.Get(carta, "instanceId"));
                var destino = huida.TryGetValue(iid, out var d) ? d : coord;
                Colocar(destino, carta);
            }

        foreach (var (iid, destino) in huida)
            Console.WriteLine($"[WZ.Carcel] turno {turno}: el portador {iid} huye a {destino}");

        return new Dictionary<string, object?>
        {
            ["uid"] = botUid,
            ["turno"] = turno,
            ["celdas"] = celdas,
            ["timestamp"] = Timestamp.FromDateTime(DateTime.UtcNow),
            ["acciones"] = new List<object?>(),
        };
    }

    /// Declara (o anula, con `Activar = false`) el ODIO de Alexander para el
    /// turno en curso. Devuelve {ok, turno, activo} o {ok:false, error}.
    public async Task<Dictionary<string, object?>> DeclararOdioCarcelAsync(DeclararOdioRequest req)
    {
        Dictionary<string, object?> Error(string msg) => new() { ["ok"] = false, ["error"] = msg };
        if (string.IsNullOrWhiteSpace(req.Uid) || string.IsNullOrWhiteSpace(req.LobbyId))
            return Error("uid y lobbyId son obligatorios");

        var lobbyRef = _fs.Db.Collection("Partidas").Document(req.LobbyId);

        return await _fs.Db.RunTransactionAsync(async tx =>
        {
            var snap = await tx.GetSnapshotAsync(lobbyRef);
            if (!snap.Exists) return Error("la partida no existe");
            var data = M.Map(M.FromFs(snap.ToDictionary()));

            if (M.Str(M.Get(data, "estado")) != "en_curso") return Error("la partida no está en curso");
            if (!M.Bool(M.Get(data, "esHistoria"))) return Error("no es una batalla de historia");

            var hist = M.Map(M.Get(data, "historia"));
            if (M.Str(M.Get(hist, "jugadorUid")) != req.Uid) return Error("no eres el jugador de esta batalla");

            var cfg = HistoriaCarcel.Get(M.Str(M.Get(hist, "id")));
            var estado = cfg != null ? EstadoCarcel.DesdeCampo(M.Get(data, "carcel")) : null;
            if (cfg == null || estado == null) return Error("esta batalla no tiene odio");
            if (estado.Fin != "") return Error("la fuga ya ha terminado");
            if (estado.OdioUsado) return Error("Alexander ya ha desatado su odio en esta batalla");

            int turno = M.Int(M.Get(data, "turnoActual"));
            if (req.Turno > 0 && req.Turno != turno) return Error($"el turno en curso es el {turno}");

            var cerrado = M.List(M.Get(data, "cerradoPor")).Select(M.Str).Contains(req.Uid);
            if (cerrado) return Error("ya has cerrado el turno");

            bool vivo = M.Map(M.Get(data, "tablero")).Values
                .SelectMany(v => M.List(v).Select(M.Map))
                .Any(c => M.Str(M.Get(c, "instanceId")) == estado.AlexanderId);
            if (!vivo) return Error("Alexander ha caído");

            tx.Update(lobbyRef, new Dictionary<string, object>
            {
                ["carcel.odio.turno"] = req.Activar ? (long)turno : 0L,
            });

            Console.WriteLine(req.Activar
                ? $"[WZ.Carcel] {req.LobbyId} turno {turno}: odio declarado"
                : $"[WZ.Carcel] {req.LobbyId} turno {turno}: odio anulado");
            return new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["turno"] = (long)turno,
                ["activo"] = req.Activar,
            };
        });
    }
}

/// Cuerpo de POST /warzero/carcel/odio. `Activar = false` = anular.
public class DeclararOdioRequest
{
    public string Uid { get; set; } = "";
    public string LobbyId { get; set; } = "";
    public int Turno { get; set; }
    public bool Activar { get; set; } = true;
}
