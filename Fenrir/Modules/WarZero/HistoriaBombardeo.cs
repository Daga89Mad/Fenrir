using System;
using System.Collections.Generic;
using System.Linq;

// ─────────────────────────────────────────────────────────────────────────────
// HistoriaBombardeo.cs
//
// BOMBARDEO POR FILAS de algunas batallas de historia (p. ej. "humanos_1" ·
// Los hermanos del alba). Aquí vive:
//   • `GuionBombardeo`: los DATOS de cada batalla (disparos por fila y turno,
//     pesos, castigo por agrupación y casillas desactivadoras).
//   • `PlanificadorBombardeo`: el MOTOR puro que, a partir del tablero, prepara
//     el plan de un turno (`PlanBombardeo`) y lo sortea. No toca Firestore.
//
// CÓMO FUNCIONA UN TURNO
//   1. Al EMPEZAR el turno N (al crear la partida para el turno 1, o al resolver
//      el turno N−1) el servidor prepara el `PlanBombardeo` de N y lo guarda en
//      la partida (campo `bombardeo`). El cliente pinta en cada celda el % de
//      que le caiga un disparo y marca las casillas desactivadoras.
//   2. Al CERRAR el turno N, el bot sortea ese mismo plan (`Sortear`) y lanza
//      un disparo lejano en cada celda elegida.
//
// DISPAROS POR FILA
//   En CADA fila del tablero (A, B, C…) caen EXACTAMENTE
//     disparosPorFila(N) = máx(0, base(N) − reducción)
//   disparos, en celdas distintas de esa fila.
//   base(N) sale de `Tramos` (humanos_1: 3 en turnos 1-2, 4 en 3-4, 5 desde el 5).
//   reducción = casillas desactivadoras ocupadas por el jugador al cerrar N−1,
//   × `ReduccionPorCasilla` (se resta en TODAS las filas).
//   Si una fila tiene menos celdas válidas que disparos, caen tantos como celdas.
//
// PROBABILIDAD DE CADA CELDA
//   Dentro de su fila, cada celda válida tiene un PESO:
//     peso = PesoBase
//          + PesoAcoso   · (RadioAcoso + 1 − d) / (RadioAcoso + 1)
//                        (d = distancia a la carta del jugador más cercana, si d ≤ RadioAcoso)
//          + Σ PesoCastigo · zonas(grupo) · (RadioCastigo + 1 − d)
//                        (por cada grupo de más de `Umbral` cartas a distancia d ≤ RadioCastigo)
//   y su probabilidad es proporcional a ese peso, repartida para que la fila
//   sume exactamente sus disparos (una celda nunca pasa del 100 %):
//     p(celda) = disparos · peso / Σ pesos de la fila   (con tope 1 y reparto
//                del sobrante entre el resto).
//   El sorteo (muestreo sistemático) elige EXACTAMENTE `disparos` celdas de la
//   fila y cada celda sale con probabilidad p(celda): el % pintado es el real.
//   Nunca tienen peso: el cuartel del bot, las celdas de mar (el jugador no
//   puede estar ahí) ni las celdas con un escudo del jugador.
//
// NUNCA DONDE ESTÁ EL BOT
//   Si un disparo sorteado cae donde TERMINA una carta del bot, se REDIRIGE a
//   otra celda libre de la MISMA fila (según sus probabilidades), para que la
//   fila reciba igualmente sus disparos. Si la fila no tiene otra celda libre,
//   ese disparo se pierde.
//
// CASILLAS DESACTIVADORAS
//   Dos casillas: una en el CENTRO del tablero y otra AL AZAR. Cambian de sitio
//   cada `DuracionTurnos` turnos. Si al cerrar un turno el jugador tiene una
//   carta en una desactivadora:
//     • el turno siguiente cae 1 disparo menos EN CADA FILA por cada
//       desactivadora ocupada;
//     • se forma sobre ella un ESCUDO del jugador de `EscudoTurnos` turnos (el
//       mismo efecto de escudo del juego: el enemigo no puede entrar en la celda
//       ni lanzar acciones sobre ella), si no tenía ya uno.
//
// Todo el azar usa semillas ESTABLES (semilla de la partida + turno): si la
// transacción del cierre se reintenta, el resultado es el mismo.
//
// AÑADIR UN BOMBARDEO NUEVO:
//   1. Escribe un `GuionBombardeo` con sus tramos, pesos, castigo y desactivadoras.
//   2. Añádelo a `HistoriaBombardeos.Todas` con la Id de la `HistoriaDef`.
// ─────────────────────────────────────────────────────────────────────────────

/// Desde el turno `DesdeTurno` (inclusive) caen `Disparos` disparos EN CADA
/// FILA por turno, hasta que empiece el tramo siguiente.
public sealed record TramoDisparos(int DesdeTurno, int Disparos);

/// Pesos con los que se reparten los disparos DENTRO de cada fila. Con todo a
/// 0 salvo `PesoBase`, los disparos de la fila caen uniformemente al azar.
public sealed record ConfigPesos(
    /// Peso de cualquier celda válida.
    double PesoBase = 1.0,
    /// Peso extra de las celdas cerca de las cartas del jugador (0 = sin acoso).
    double PesoAcoso = 0.0,
    /// Distancia (Manhattan) hasta la que llega el acoso.
    int RadioAcoso = 3,
    /// Peso extra, por zona atraída, junto a un grupo grande del jugador.
    double PesoCastigo = 2.0,
    /// Distancia (Manhattan) hasta la que llega el castigo de un grupo.
    int RadioCastigo = 2);

/// Castigo por agrupación. `ZonasAtraidas(n)` es la fórmula.
public sealed record CastigoAgrupacion(
    /// Cartas que pueden ir juntas sin castigo.
    int Umbral,
    /// Cada tantas cartas por encima del umbral, el castigo sube un escalón.
    int CartasPorZona)
{
    /// Escalones de castigo que atrae un grupo de [cartas] cartas.
    public int ZonasAtraidas(int cartas) =>
        cartas <= Umbral ? 0 : (cartas - Umbral + CartasPorZona - 1) / Math.Max(1, CartasPorZona);
}

/// Casillas desactivadoras.
public sealed record ConfigDesactivadoras(
    /// Una de ellas se coloca siempre en las celdas del centro del tablero.
    bool UnaEnElCentro = true,
    /// Cuántas más se colocan al azar.
    int AlAzar = 1,
    /// Turnos que se mantienen en su sitio antes de cambiar.
    int DuracionTurnos = 2,
    /// Disparos menos POR FILA el turno siguiente por cada desactivadora ocupada.
    int ReduccionPorCasilla = 1,
    /// Duración del escudo que se forma al ocuparla.
    int EscudoTurnos = 3,
    /// Magnitud del escudo (mismo valor que las cartas de escudo).
    int EscudoMagnitud = 3,
    /// Distancia mínima de la desactivadora al azar a cualquier cuartel.
    int DistanciaMinimaCuarteles = 3);

/// Guion completo de bombardeo de una batalla.
public sealed class GuionBombardeo
{
    /// Id de la carta de acción (disparo lejano) con la que se lanzan los
    /// disparos. Debe existir en el catálogo (normalmente una carta EXCLUSIVA
    /// de historia, ver HistoriaCatalogo.CartasExclusivas).
    public string CartaArtilleriaId { get; }
    public IReadOnlyList<TramoDisparos> Tramos { get; }
    public ConfigPesos Pesos { get; }
    public CastigoAgrupacion? Castigo { get; }
    public ConfigDesactivadoras? Desactivadoras { get; }

    public GuionBombardeo(
        string cartaArtilleriaId,
        IReadOnlyList<TramoDisparos> tramos,
        ConfigPesos? pesos = null,
        CastigoAgrupacion? castigo = null,
        ConfigDesactivadoras? desactivadoras = null)
    {
        CartaArtilleriaId = cartaArtilleriaId;
        Tramos = tramos ?? new List<TramoDisparos>();
        Pesos = pesos ?? new ConfigPesos();
        Castigo = castigo;
        Desactivadoras = desactivadoras;
    }

    /// Disparos BASE por fila del turno (antes de restar las desactivadoras).
    public int DisparosBase(int turno) =>
        Tramos.Where(t => t.DesdeTurno <= turno)
              .OrderByDescending(t => t.DesdeTurno)
              .Select(t => Math.Max(0, t.Disparos))
              .FirstOrDefault();
}

/// Plan de UNA fila: caen exactamente `Disparos` disparos en celdas distintas
/// y cada celda sale con la probabilidad de `Prob` (suman `Disparos`, cada
/// una ≤ 1).
public sealed record FilaBombardeo(
    string Fila,
    int Disparos,
    IReadOnlyDictionary<string, double> Prob);

/// Una casilla desactivadora activa hasta el turno `HastaTurno` (inclusive).
public sealed record Desactivadora(string Coord, int HastaTurno, bool Centro);

/// Plan de bombardeo de UN turno (lo que se publica y luego se sortea).
public sealed record PlanBombardeo(
    int Turno,
    int DisparosBase,
    int Reduccion,
    IReadOnlyList<FilaBombardeo> Filas,
    IReadOnlyList<Desactivadora> Desactivadoras)
{
    /// Disparos por fila de este turno (base − reducción).
    public int DisparosPorFila => Math.Max(0, DisparosBase - Reduccion);

    /// Disparos totales del turno (suma de todas las filas).
    public int Disparos => Filas.Sum(f => f.Disparos);

    /// Probabilidad (0..1) de que caiga un disparo en cada celda. Las filas no
    /// se solapan, así que es directamente la de su fila.
    public Dictionary<string, double> Probabilidades()
    {
        var res = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in Filas)
            foreach (var kv in f.Prob)
                res[kv.Key] = kv.Value;
        return res;
    }

    /// Documento para guardar en la partida (campo `bombardeo`). `prob` va en %
    /// entero para que el cliente lo pinte sin cálculos; `filas` guarda las
    /// probabilidades exactas que usa el sorteo.
    public Dictionary<string, object?> ACampo() => new()
    {
        ["turno"] = (long)Turno,
        ["disparosPorFila"] = (long)DisparosPorFila,
        ["disparos"] = (long)Disparos,
        ["disparosBase"] = (long)DisparosBase,
        ["reduccion"] = (long)Reduccion,
        ["filas"] = Filas.ToDictionary(
            f => f.Fila,
            f => (object?)new Dictionary<string, object?>
            {
                ["disparos"] = (long)f.Disparos,
                ["prob"] = f.Prob.ToDictionary(kv => kv.Key, kv => (object?)kv.Value),
            }),
        ["prob"] = Probabilidades()
            .Where(kv => kv.Value > 0)
            .ToDictionary(kv => kv.Key, kv => (object?)(long)Math.Clamp(Math.Round(kv.Value * 100), 1, 100)),
        ["desactivadoras"] = Desactivadoras.Select(d => (object?)new Dictionary<string, object?>
        {
            ["coord"] = d.Coord,
            ["hastaTurno"] = (long)d.HastaTurno,
            ["centro"] = d.Centro,
        }).ToList(),
    };
}

/// Resultado de sortear un plan.
public sealed record SorteoBombardeo(
    /// Celdas donde caen los disparos (ya redirigidos), fila a fila.
    IReadOnlyList<string> Impactos,
    /// Disparos que caían sobre una carta del bot y se movieron a otra celda.
    int Redirigidos,
    /// Disparos que caían sobre el bot y no tenían otra celda libre en su fila.
    int Perdidos);

/// Catálogo estático de bombardeos, indexado por `HistoriaDef.Id`.
public static class HistoriaBombardeos
{
    // ═════════════════════════════════════════════════════════════════════════
    // "humanos_1" · Los hermanos del alba · Parte 1 (MonolitoNefilim, 12×15)
    // ═════════════════════════════════════════════════════════════════════════
    //   • Disparos POR FILA: 3 en los turnos 1-2, 4 en los turnos 3-4, 5 desde
    //     el 5 (12 filas → 36 / 48 / 60 disparos por turno en total).
    //     (Antes 4 / 5 / 6: se bajó 1 por fila para suavizar la batalla.)
    //   • Dentro de cada fila caen al azar (peso base 1, sin acoso).
    //   • Castigo: un grupo de 4-5 cartas suma +2·(3 − d) de peso a las celdas a
    //     distancia d ≤ 2; de 6-7, el doble; de 8-9, el triple…
    //   • Desactivadoras: 1 en el centro + 1 al azar, se mantienen 5 turnos
    //     en su sitio y luego cambian;
    //     −1 disparo EN CADA FILA por cada una ocupada y escudo de 3 turnos.
    private static readonly GuionBombardeo HermanosDelAlba1 = new(
        cartaArtilleriaId: HistoriaCatalogo.NefAndanada,
        tramos: new[]
        {
            new TramoDisparos(DesdeTurno: 1, Disparos: 3),
            new TramoDisparos(DesdeTurno: 3, Disparos: 4),
            new TramoDisparos(DesdeTurno: 5, Disparos: 5),
        },
        pesos: new ConfigPesos(PesoBase: 1.0, PesoAcoso: 0.0, RadioAcoso: 3,
                               PesoCastigo: 2.0, RadioCastigo: 2),
        castigo: new CastigoAgrupacion(Umbral: 3, CartasPorZona: 2),
        desactivadoras: new ConfigDesactivadoras(
            UnaEnElCentro: true, AlAzar: 1, DuracionTurnos: 5,
            ReduccionPorCasilla: 1, EscudoTurnos: 3, EscudoMagnitud: 3,
            DistanciaMinimaCuarteles: 3));

    /// Todos los bombardeos registrados, indexados por `HistoriaDef.Id`. Una
    /// historia sin entrada aquí no tiene bombardeo.
    public static readonly IReadOnlyDictionary<string, GuionBombardeo> Todas =
        new Dictionary<string, GuionBombardeo>
        {
            ["humanos_1"] = HermanosDelAlba1,
        };

    /// Bombardeo de `historiaId`, o null si esa historia no tiene.
    public static GuionBombardeo? Get(string historiaId) =>
        !string.IsNullOrEmpty(historiaId) && Todas.TryGetValue(historiaId, out var g) ? g : null;
}

/// Motor PURO del bombardeo: prepara y sortea planes. No conoce Firestore ni
/// el formato de las cartas; recibe el tablero ya resumido.
public static class PlanificadorBombardeo
{
    /// Lo que el planificador necesita saber del tablero al empezar el turno.
    public sealed record Entrada(
        int Turno,
        int Semilla,
        int Filas,
        int Columnas,
        /// True si una carta del jugador puede estar en la celda (terreno).
        Func<string, bool> Transitable,
        string CuartelJugador,
        string CuartelBot,
        /// Celda → nº de cartas VISIBLES del jugador (incluye su cuartel).
        IReadOnlyDictionary<string, int> CartasJugador,
        /// Celdas con un escudo activo del jugador.
        IReadOnlySet<string> EscudosJugador,
        /// Disparos menos POR FILA por las desactivadoras ocupadas el turno anterior.
        int Reduccion);

    /// Prepara el plan del turno `e.Turno`.
    public static PlanBombardeo Preparar(GuionBombardeo g, Entrada e)
    {
        int baseDisparos = g.DisparosBase(e.Turno);
        int reduccion = Math.Max(0, e.Reduccion);
        int porFila = Math.Max(0, baseDisparos - reduccion);
        var p = g.Pesos;

        bool Valida(string c) =>
            !Igual(c, e.CuartelBot) && !e.EscudosJugador.Contains(c) && e.Transitable(c);

        // Grupos castigados: fuera de los cuarteles, más de `Umbral` cartas.
        var grupos = g.Castigo == null
            ? new List<(string coord, int zonas)>()
            : e.CartasJugador
                .Where(kv => !Igual(kv.Key, e.CuartelJugador) && !Igual(kv.Key, e.CuartelBot))
                .Select(kv => (coord: kv.Key, zonas: g.Castigo.ZonasAtraidas(kv.Value)))
                .Where(x => x.zonas > 0)
                .ToList();
        var cartas = e.CartasJugador.Where(kv => kv.Value > 0).Select(kv => kv.Key).ToList();

        double Peso(string c)
        {
            double w = Math.Max(0, p.PesoBase);
            if (p.PesoAcoso > 0 && cartas.Count > 0)
            {
                int d = cartas.Min(j => Distancia(c, j));
                if (d <= p.RadioAcoso)
                    w += p.PesoAcoso * (p.RadioAcoso + 1 - d) / (double)(p.RadioAcoso + 1);
            }
            foreach (var (coord, zonas) in grupos)
            {
                int d = Distancia(c, coord);
                if (d <= p.RadioCastigo) w += p.PesoCastigo * zonas * (p.RadioCastigo + 1 - d);
            }
            return w;
        }

        var filas = new List<FilaBombardeo>();
        for (int f = 0; f < e.Filas; f++)
        {
            var pesos = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            for (int col = 1; col <= e.Columnas; col++)
            {
                var c = Coord(f, col);
                if (!Valida(c)) continue;
                double w = Peso(c);
                if (w > 0) pesos[c] = w;
            }
            int n = Math.Min(porFila, pesos.Count);
            var prob = n > 0 ? Repartir(pesos, n) : new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            filas.Add(new FilaBombardeo(((char)('A' + f)).ToString(), n, prob));
        }

        // Desactivadoras del turno.
        var excluidas = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { e.CuartelJugador, e.CuartelBot };
        var desact = g.Desactivadoras == null
            ? new List<Desactivadora>()
            : Desactivadoras(g.Desactivadoras, e.Turno, e.Semilla, e.Filas, e.Columnas, e.Transitable, excluidas);

        return new PlanBombardeo(e.Turno, baseDisparos, reduccion, filas, desact);
    }

    /// Reparte [n] disparos entre las celdas de una fila en proporción a su
    /// peso, sin que ninguna pase de 1: las que llegan al tope se fijan a 1 y
    /// el resto se reparte entre las demás. Las probabilidades suman [n].
    public static Dictionary<string, double> Repartir(IReadOnlyDictionary<string, double> pesos, int n)
    {
        var res = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var libres = pesos.Where(kv => kv.Value > 0).Select(kv => kv.Key).ToList();
        double resto = Math.Min(n, libres.Count);
        while (libres.Count > 0 && resto > 1e-12)
        {
            double suma = libres.Sum(c => pesos[c]);
            var topadas = libres.Where(c => resto * pesos[c] / suma >= 1.0).ToList();
            if (topadas.Count == 0)
            {
                foreach (var c in libres) res[c] = resto * pesos[c] / suma;
                break;
            }
            foreach (var c in topadas) { res[c] = 1.0; libres.Remove(c); resto -= 1.0; }
        }
        return res;
    }

    /// Posición de las desactivadoras en `turno`. Cambian cada
    /// `DuracionTurnos`: todos los turnos del mismo periodo dan el mismo
    /// resultado (semilla = partida + periodo).
    public static List<Desactivadora> Desactivadoras(
        ConfigDesactivadoras cfg, int turno, int semilla, int filas, int columnas,
        Func<string, bool> transitable, ISet<string> excluidas)
    {
        var res = new List<Desactivadora>();
        if (filas <= 0 || columnas <= 0) return res;
        int dur = Math.Max(1, cfg.DuracionTurnos);
        int periodo = (Math.Max(1, turno) - 1) / dur;
        int hasta = (periodo + 1) * dur;
        var rng = new Random(Semilla(semilla, "desactivadoras", periodo));

        bool Libre(string c) =>
            transitable(c) && !excluidas.Contains(c) && !res.Any(d => Igual(d.Coord, c));

        if (cfg.UnaEnElCentro)
        {
            var centro = CeldasCentro(filas, columnas).Where(Libre).OrderBy(c => c, StringComparer.Ordinal).ToList();
            if (centro.Count > 0) res.Add(new Desactivadora(centro[rng.Next(centro.Count)], hasta, true));
        }

        for (int k = 0; k < cfg.AlAzar; k++)
        {
            var candidatas = TodasLasCeldas(filas, columnas)
                .Where(Libre)
                .Where(c => excluidas.All(x => Distancia(c, x) >= cfg.DistanciaMinimaCuarteles))
                .Where(c => res.All(d => Distancia(c, d.Coord) >= 2))
                .OrderBy(c => c, StringComparer.Ordinal)
                .ToList();
            if (candidatas.Count == 0) break;
            res.Add(new Desactivadora(candidatas[rng.Next(candidatas.Count)], hasta, false));
        }
        return res;
    }

    /// Sortea el plan fila a fila. En cada fila se eligen EXACTAMENTE sus
    /// disparos con muestreo sistemático (orden aleatorio + un único número al
    /// azar): cada celda sale con la probabilidad publicada.
    ///
    /// Los impactos que caen en [prohibidas] (celdas donde termina una carta
    /// del bot) se REDIRIGEN a otra celda de la misma fila que no esté elegida
    /// ni prohibida, eligiéndola según sus probabilidades. Si no queda
    /// ninguna, ese disparo se pierde.
    public static SorteoBombardeo Sortear(PlanBombardeo plan, int semilla, ISet<string>? prohibidas = null)
    {
        var rng = new Random(Semilla(semilla, "sorteo", plan.Turno));
        var impactos = new List<string>();
        int redirigidos = 0, perdidos = 0;

        foreach (var fila in plan.Filas.OrderBy(f => f.Fila, StringComparer.Ordinal))
        {
            if (fila.Disparos <= 0 || fila.Prob.Count == 0) continue;

            // Orden aleatorio de la fila (Fisher-Yates sobre un orden estable).
            var celdas = fila.Prob.Keys.OrderBy(c => c, StringComparer.Ordinal).ToList();
            for (int i = celdas.Count - 1; i > 0; i--)
            {
                int j = rng.Next(i + 1);
                (celdas[i], celdas[j]) = (celdas[j], celdas[i]);
            }

            // Muestreo sistemático: puntos u, u+1, …, u+n−1 sobre la suma acumulada.
            double u = rng.NextDouble();
            var elegidas = new List<string>();
            double acc = 0;
            int k = 0;
            foreach (var c in celdas)
            {
                acc += fila.Prob[c];
                if (k < fila.Disparos && u + k < acc - 1e-12)
                {
                    elegidas.Add(c);
                    k++;
                    while (k < fila.Disparos && u + k < acc - 1e-12) k++; // p ≤ 1: no ocurre
                }
            }
            // Redondeos: si faltan, las de mayor probabilidad aún libres.
            foreach (var c in celdas.OrderByDescending(c => fila.Prob[c]).ThenBy(c => c, StringComparer.Ordinal))
            {
                if (elegidas.Count >= fila.Disparos) break;
                if (!elegidas.Contains(c, StringComparer.OrdinalIgnoreCase)) elegidas.Add(c);
            }

            // Redirección de los que caen sobre el bot.
            if (prohibidas != null && prohibidas.Count > 0)
            {
                for (int i = 0; i < elegidas.Count; i++)
                {
                    if (!prohibidas.Contains(elegidas[i])) continue;
                    var alternativas = celdas
                        .Where(c => !prohibidas.Contains(c) && !elegidas.Contains(c, StringComparer.OrdinalIgnoreCase))
                        .OrderBy(c => c, StringComparer.Ordinal)
                        .ToList();
                    if (alternativas.Count == 0)
                    {
                        elegidas.RemoveAt(i--);
                        perdidos++;
                        continue;
                    }
                    double total = alternativas.Sum(c => fila.Prob[c]);
                    string nueva = alternativas[^1];
                    if (total > 0)
                    {
                        double r = rng.NextDouble() * total, a = 0;
                        foreach (var c in alternativas)
                        {
                            a += fila.Prob[c];
                            if (r < a) { nueva = c; break; }
                        }
                    }
                    else nueva = alternativas[rng.Next(alternativas.Count)];
                    elegidas[i] = nueva;
                    redirigidos++;
                }
            }

            impactos.AddRange(elegidas);
        }
        return new SorteoBombardeo(impactos, redirigidos, perdidos);
    }

    // ── Utilidades ───────────────────────────────────────────────────────────

    /// Celdas del CENTRO del tablero: las 2 filas centrales (1 si son impares)
    /// × las 3 columnas centrales (2 si son pares).
    public static List<string> CeldasCentro(int filas, int columnas)
    {
        var fs = new[] { (filas - 1) / 2, filas / 2 }.Distinct();
        var cs = columnas % 2 == 1
            ? new[] { columnas / 2, columnas / 2 + 1, columnas / 2 + 2 }
            : new[] { columnas / 2, columnas / 2 + 1 };
        return fs.SelectMany(f => cs.Where(c => c >= 1 && c <= columnas).Select(c => Coord(f, c))).ToList();
    }

    public static IEnumerable<string> TodasLasCeldas(int filas, int columnas)
    {
        for (int f = 0; f < filas; f++)
            for (int c = 1; c <= columnas; c++)
                yield return Coord(f, c);
    }

    public static int Distancia(string a, string b)
    {
        var pa = Parse(a);
        var pb = Parse(b);
        if (pa == null || pb == null) return int.MaxValue;
        return Math.Abs(pa.Value.r - pb.Value.r) + Math.Abs(pa.Value.c - pb.Value.c);
    }

    /// Semilla estable (FNV-1a) a partir de varias partes. string.GetHashCode
    /// no sirve: cambia entre procesos.
    public static int Semilla(params object[] partes)
    {
        unchecked
        {
            uint h = 2166136261;
            foreach (var ch in string.Join("|", partes)) { h ^= ch; h *= 16777619; }
            return (int)h;
        }
    }

    private static bool Igual(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static string Coord(int f, int c) => $"{(char)('A' + f)}{c}";

    private static (int r, int c)? Parse(string? coord)
    {
        if (string.IsNullOrEmpty(coord) || coord.Length < 2) return null;
        int r = char.ToUpperInvariant(coord[0]) - 'A';
        if (r < 0 || !int.TryParse(coord[1..], out int c)) return null;
        return (r, c);
    }
}