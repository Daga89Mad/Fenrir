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

    public EstrategaSoftmaxStrategy(
        WarZeroBotOptions opt, PerfilBot? perfil,
        double temperatura = 18.0, double deltaCorte = 25.0)
    {
        var p = perfil ?? PerfilBot.PorDefecto;
        _temperatura = temperatura <= 0 ? 1.0 : temperatura;
        _deltaCorte = deltaCorte < 0 ? 0.0 : deltaCorte;

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

    public BotMove DecidirJugada(BotContext ctx)
    {
        // 1) un plan por variante (todas ven el MISMO contexto) + su puntuación.
        var planes = new List<BotMove>(_variantes.Count);
        var scores = new List<double>(_variantes.Count);
        foreach (var v in _variantes)
        {
            var plan = v.DecidirJugada(ctx);
            planes.Add(plan);
            scores.Add(EvaluadorTablero.Evaluar(ctx, plan));
        }

        if (planes.Count == 1) return planes[0];

        double max = scores.Max();

        // 2) CORTE: solo siguen elegibles los planes dentro de delta del mejor.
        var candidatos = new List<int>();
        for (int i = 0; i < scores.Count; i++)
            if (scores[i] >= max - _deltaCorte) candidatos.Add(i);

        // Si solo el mejor sobrevive (delta pequeño o resto claramente peor), se juega él.
        if (candidatos.Count <= 1) return planes[scores.IndexOf(max)];

        // 3) softmax numéricamente estable SOBRE LOS SUPERVIVIENTES.
        var pesos = new double[candidatos.Count];
        double suma = 0.0;
        for (int j = 0; j < candidatos.Count; j++)
        {
            double w = Math.Exp((scores[candidatos[j]] - max) / _temperatura);
            pesos[j] = w;
            suma += w;
        }
        if (suma <= 0 || double.IsNaN(suma))
            return planes[scores.IndexOf(max)]; // degenerado: cae al mejor

        // 4) muestreo por ruleta entre los supervivientes.
        double r = _rng.NextDouble() * suma;
        double acum = 0.0;
        for (int j = 0; j < pesos.Length; j++)
        {
            acum += pesos[j];
            if (r <= acum) return planes[candidatos[j]];
        }
        return planes[candidatos[^1]];
    }
}