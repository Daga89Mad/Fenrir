using System;
using System.Collections.Generic;
using System.Linq;

// ─────────────────────────────────────────────────────────────────────────────
// EstrategaSoftmaxStrategy.cs
//
// Envuelve varias EstrategaStrategy (una por VARIANTE de estilo, ancladas en el
// perfil real del bot) y, cada turno:
//   1. pide a cada variante su PLAN (BotMove) para el mismo contexto,
//   2. puntúa el tablero proyectado de cada plan con EvaluadorTablero (que ya
//      considera la respuesta enemiga pesimista),
//   3. DESCARTA los planes claramente peores que el mejor (corte por delta), y
//   4. elige uno por SOFTMAX entre los supervivientes.
//
// El paso 3 (el "corte") es lo que da fuerza sin sacrificar variedad: garantiza
// que el bot NUNCA juega un error por variar — solo mezcla entre jugadas que son
// casi igual de buenas. Con esto:
//   · delta pequeño  → más fuerte, menos variado (en el límite, siempre el mejor).
//   · delta grande   → más variado (en el límite, el softmax puro de antes).
//   · temperatura    → cómo se reparte la probabilidad ENTRE los supervivientes.
//
// Ante el mismo tablero el bot ya no juega SIEMPRE lo mismo (mata la
// predictibilidad) pero, cuando la posición es forzada, las variantes colapsan al
// mismo plan y la elección vuelve a ser contundente. Respeta el ESTILO: un bot
// defensivo nunca recibe una variante temeraria.
//
// Es un IBotStrategy: entra donde antes iba EstrategaStrategy. Las variantes se
// construyen UNA vez y viven toda la partida (su memoria de predicción interna se
// mantiene coherente, porque TODAS se consultan cada turno con el mismo contexto).
// Solo se APLICA el plan elegido; los descartados no tienen efecto.
// ─────────────────────────────────────────────────────────────────────────────
public class EstrategaSoftmaxStrategy : IBotStrategy
{
    private readonly List<EstrategaStrategy> _variantes;
    private readonly Random _rng = new();

    // TEMPERATURA del softmax entre los planes que sobreviven al corte. Más alta =
    // reparte más parejo; más baja = tiende al mejor de los supervivientes. Tunable.
    private readonly double _temperatura;

    // CORTE (delta): margen máximo por debajo del mejor plan para seguir siendo
    // elegible. Planes peores que (mejor − delta) se descartan antes del softmax.
    // Es el mando fuerza↔variedad. Ajustado a la escala de EvaluadorTablero: ~25
    // deja mezclar planes que difieren en un par de piezas menores, pero excluye
    // cualquiera que conceda un cuartel o una pieza real. Tunable.
    private readonly double _deltaCorte;

    // Puntuación a 2 PLIES (Tarea 2): si true, cada plan se puntúa SIMULANDO el
    // turno (combate exacto) contra la respuesta enemiga y evaluando el tablero
    // resultante (LookaheadDosPlies). Si false, cae al evaluador plano de 1 ply.
    private readonly bool _usarLookahead;

    public EstrategaSoftmaxStrategy(
        WarZeroBotOptions opt, PerfilBot? perfil,
        double temperatura = 18.0, double deltaCorte = 25.0, bool usarLookahead = true)
    {
        var p = perfil ?? PerfilBot.PorDefecto;
        _temperatura = temperatura <= 0 ? 1.0 : temperatura;
        _deltaCorte = deltaCorte < 0 ? 0.0 : deltaCorte;
        _usarLookahead = usarLookahead;

        // VARIANTES de estilo ancladas en el perfil real (misma DIFICULTAD). Se
        // respeta la identidad del bot: solo el Equilibrado explora los tres
        // estilos; Agresivo / Defensivo solo se relajan hacia Equilibrado.
        var estilos = p.Estilo switch
        {
            EstiloBot.Agresivo => new[] { EstiloBot.Agresivo, EstiloBot.Equilibrado },
            EstiloBot.Defensivo => new[] { EstiloBot.Defensivo, EstiloBot.Equilibrado },
            _ => new[] { EstiloBot.Equilibrado, EstiloBot.Agresivo, EstiloBot.Defensivo },
        };

        _variantes = estilos
            .Distinct()
            .Select(e => new EstrategaStrategy(
                opt, new PerfilBot { Dificultad = p.Dificultad, Estilo = e }))
            .ToList();
    }

    // Modo del plan elegido en la última decisión (para registro/medición):
    // "libre" (variante de estilo), "defensa", "caceria" o "farmeo".
    public string UltimoModo { get; private set; } = "libre";

    public BotMove DecidirJugada(BotContext ctx)
    {
        var planes = new List<BotMove>();
        var scores = new List<double>();
        var modos = new List<string>();

        void Anadir(BotMove plan, string modo)
        {
            planes.Add(plan);
            scores.Add(_usarLookahead
                ? LookaheadDosPlies.Puntuar(ctx, plan)   // 2-3 plies: simula la respuesta enemiga
                : EvaluadorTablero.Evaluar(ctx, plan));  // 1 ply: proxy heurístico
            modos.Add(modo);
        }

        // Variantes de estilo (modo "libre").
        foreach (var v in _variantes) Anadir(v.DecidirJugada(ctx), "libre");

        // Modos como candidatos EXTRA; el lookahead elige cuál gana. El cambio de
        // modo es emergente de la simulación, no una regla que haya que acertar.
        Anadir(PlanificadorDefensivo.Generar(ctx), "defensa");           // replegar y guarnecer
        var planCaza = PlanificadorCaceria.Generar(ctx);                 // concentrar sobre una presa
        if (planCaza != null) Anadir(planCaza, "caceria");
        var planFarmeo = PlanificadorFarmeo.Generar(ctx);                // apertura: dominar el centro
        if (planFarmeo != null) Anadir(planFarmeo, "farmeo");

        int sel = Seleccionar(scores);
        UltimoModo = modos[sel];
        return planes[sel];
    }

    // Índice del plan elegido: CORTE por delta + SOFTMAX entre supervivientes.
    private int Seleccionar(List<double> scores)
    {
        if (scores.Count == 1) return 0;
        double max = scores.Max();
        int idxMax = scores.IndexOf(max);

        var candidatos = new List<int>();
        for (int i = 0; i < scores.Count; i++)
            if (scores[i] >= max - _deltaCorte) candidatos.Add(i);
        if (candidatos.Count <= 1) return idxMax;   // solo el mejor sobrevive

        var pesos = new double[candidatos.Count];
        double suma = 0.0;
        for (int j = 0; j < candidatos.Count; j++)
        {
            double w = Math.Exp((scores[candidatos[j]] - max) / _temperatura);
            pesos[j] = w; suma += w;
        }
        if (suma <= 0 || double.IsNaN(suma)) return idxMax;   // degenerado: el mejor

        double r = _rng.NextDouble() * suma, acum = 0.0;
        for (int j = 0; j < pesos.Length; j++)
        {
            acum += pesos[j];
            if (r <= acum) return candidatos[j];
        }
        return candidatos[^1];
    }
}