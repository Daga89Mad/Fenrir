// ─────────────────────────────────────────────────────────────────────────────
// WarZeroInactividad.cs
//
// EXPULSIÓN POR INACTIVIDAD. Un jugador que deja pasar 3 turnos SEGUIDOS sin
// pulsar "fin de turno" (su turno lo acaba resolviendo la fecha límite,
// ForzarResolucionSiProcedeAsync) queda EXPULSADO de la partida, igual que si
// le hubieran conquistado el cuartel:
//
//   · Entra en `jugadoresEliminados` (y además en `jugadoresExpulsados`, que es
//     lo que permite a la app pintar "EXPULSADO" en lugar de "ELIMINADO").
//   · Su cuartel pasa a ruinas y sus cartas desaparecen del tablero.
//   · Suma +1 Derrota (por partida y global) y +1 Expulsión en sus estadísticas
//     (Jugadores/{uid}/Estadisticas/Resultados.Expulsiones y el espejo
//     `expulsiones` en Jugadores/{uid}, que es lo que lee el perfil).
//   · Cobra su anticipo como cualquier eliminado (WarZeroRecompensas).
//   · El botín que daría su cuartel a un conquistador (100 energías + 100 PC,
//     Combate.EnergiesConquista / Combate.PcConquista) se REPARTE a partes
//     iguales entre todos los jugadores que siguen en la partida.
//
// Contador: `turnosSinCerrar` en el doc de la partida (uid → turnos seguidos sin
// cerrar). Se actualiza en CADA resolución: quien cerró vuelve a 0 y quien no,
// suma 1. No aplica a historia (allí nunca hay resolución forzosa) ni a los
// bots (uid "bot_…" / "historia_…"): un fallo de su runner no debe regalar la
// partida.
// ─────────────────────────────────────────────────────────────────────────────

public partial class WarZeroService
{
    /// Turnos SEGUIDOS sin cerrar que provocan la expulsión.
    public const int TurnosInactividadExpulsion = 3;

    /// Campo del doc de partida: uid → turnos seguidos sin cerrar.
    public const string CampoTurnosSinCerrar = "turnosSinCerrar";

    /// Campo del doc de partida: uids expulsados por inactividad (en orden).
    public const string CampoJugadoresExpulsados = "jugadoresExpulsados";

    /// Resultado de evaluar la inactividad al resolver un turno.
    private sealed class ResultadoInactividad
    {
        /// False si la regla no aplica a esta partida (historia): entonces no se
        /// toca el campo `turnosSinCerrar`.
        public bool Aplica;

        /// Contadores que quedan tras este turno (solo los > 0).
        public Dictionary<string, int> Contadores = new();

        /// Jugadores que alcanzan el límite en esta resolución.
        public List<string> Expulsados = new();
    }

    /// ¿El uid es un bot (de sala, de reto o de historia)? Los bots no se
    /// expulsan por inactividad.
    private static bool EsBotInactividad(string uid) =>
        uid.StartsWith("bot_", StringComparison.Ordinal) ||
        uid.StartsWith("historia_", StringComparison.Ordinal);

    /// Calcula los contadores de inactividad tras este turno y quién queda
    /// expulsado. `cerraronTurno` son los uids que pulsaron "fin de turno" en el
    /// turno que se resuelve; si es null se asume que cerraron todos.
    private static ResultadoInactividad EvaluarInactividad(
        Dictionary<string, object?> data,
        List<string> activos,
        HashSet<string>? cerraronTurno)
    {
        var res = new ResultadoInactividad();
        if (M.Bool(M.Get(data, "esHistoria"))) return res;
        res.Aplica = true;

        var previos = M.Map(M.Get(data, CampoTurnosSinCerrar));
        foreach (var uid in activos)
        {
            if (uid == "" || EsBotInactividad(uid)) continue;

            var cerro = cerraronTurno == null || cerraronTurno.Contains(uid);
            if (cerro) continue; // vuelve a 0 (no se guarda)

            var cuenta = M.Int(M.Get(previos, uid)) + 1;
            if (cuenta >= TurnosInactividadExpulsion)
                res.Expulsados.Add(uid);
            else
                res.Contadores[uid] = cuenta;
        }

        // Si todos los activos quedan expulsados a la vez, la partida termina
        // sin ganador (estaba abandonada): lo resuelve el paso 8 del resolver.
        return res;
    }

    /// Reparte `total` a partes iguales entre `receptores`. El resto de la
    /// división se da de 1 en 1 a los primeros (orden estable de la partida).
    private static Dictionary<string, int> RepartirAPartesIguales(
        int total, List<string> receptores)
    {
        var reparto = new Dictionary<string, int>();
        if (total <= 0 || receptores.Count == 0) return reparto;
        var cuota = total / receptores.Count;
        var resto = total % receptores.Count;
        for (int i = 0; i < receptores.Count; i++)
        {
            var parte = cuota + (i < resto ? 1 : 0);
            if (parte > 0) reparto[receptores[i]] = parte;
        }
        return reparto;
    }
}
