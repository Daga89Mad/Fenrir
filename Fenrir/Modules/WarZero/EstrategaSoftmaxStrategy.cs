using System;
using System.Collections.Generic;
using System.Linq;

// ─────────────────────────────────────────────────────────────────────────────
// EstrategaSoftmaxStrategy.cs  (v11)
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
//
// ── v11: CANDIDATOS DEFENSIVOS MÚLTIPLES + BLINDAJE DE REGLAS ─────────────────
// PlanificadorDefensivo v7 ya no devuelve UN plan sino una lista de variantes
// ("defensa" = guarnición acotada + anillo + caza + misil predictivo;
// "defensa+descarga" = cuartel vacío + descarga + caza/anillo; "defensa+trampa"
// = despliegue desde la mano en el cuartel el turno de la entrada). Cada una
// entra en la softmax con su propio modo y el lookahead (v2, con mundo "misil")
// decide cuál gana: la elección descarga/trampa/base es emergente.
//
// Además, TODO candidato pasa por Blindar() antes de puntuarse (misma filosofía
// que el saneado v10): se eliminan las acciones que violan reglas del juego que
// el servidor no impone pero que el bot debe respetar:
//   · ESCUDO sobre el PROPIO cuartel (regla: nadie puede escudar su cuartel; el
//     Estratega lo hacía por un fallo de ElegirObjetivos).
//   · DESCARGA si ya se usó en la partida, si el plan lleva más de una, o si no
//     apunta al cuartel propio.
// Si la acción eliminada consumía una carta de la mano, la carta VUELVE a
// ManoResultante (la mano la escribe el bot, no el servidor) y el presupuesto de
// acciones se recalcula.
//
// ── BLINDAJE v8 (root cause del "bot congelado" en EstudioPartidas) ──────────
// El fallo: `Anadir(PlanificadorDefensivo.Generar(ctx), "defensa")` se llamaba
// SIN comprobar null. PlanificadorDefensivo.Generar devuelve null SIEMPRE que no
// hay amenaza real al cuartel — es decir, en la inmensa mayoría de los turnos —
// y ese null llegaba a LookaheadDosPlies.Puntuar, que hacía `plan.Celdas` y
// lanzaba NullReferenceException. La excepción abortaba la decisión ENTERA y
// JugarTurnoAsync caía al cierre seguro (arrastre, gasto 0): los bots quedaban
// paralizados todos los turnos EXCEPTO cuando el cuartel estaba de verdad
// amenazado (única situación en la que el defensivo devolvía plan y no había
// null en la lista). Reproducido turno a turno con las partidas de estudio
// XnIlRbHEXCr4nuZMFxnj y GG6zp3j7k85JvwtLVHVn: los 23 turnos "congelados" de la
// fase de código nuevo crashean exactamente ahí, y todos los turnos con
// actividad completan la decisión.
//
// ── v10: SANEADO DE CANDIDATOS (caso 9UCNdoqXJ2mMaur6ssuF, turno 11) ────────
// Un plan de cacería mandó una Manta escarlata (10/3) SOLA al cuartel humano
// F1: ni conquista (F≤40) ni farmea, y el rival siempre puede desplegar en su
// cuartel. Ahora CADA candidato (variantes, defensa, cacería, farmeo) pasa por
// ReglasEntrada.SanearPlan antes de puntuarse: las cartas que entran en una
// celda donde el grupo no gana se recolocan en la celda segura más cercana.
//
// Arreglos v8:
//   1. Null-check SIMÉTRICO para los tres planificadores (defensa incluida).
//   2. Cada candidato (generación + puntuación) va en su propio try/catch: un
//      candidato que falle se DESCARTA con log, en vez de abortar la decisión.
//   3. Si el lookahead falla para un plan, se reintenta con el evaluador plano
//      (1 ply) antes de descartar el candidato.
//   4. Si TODO fallara (imposible salvo catástrofe), se devuelve el plan crudo
//      de la primera variante; solo si ni eso existe se relanza y el cierre
//      seguro de JugarTurnoAsync hace de última red.
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
    // "libre" (variante de estilo), "defensa", "defensa+descarga",
    // "defensa+trampa", "caceria" o "farmeo".
    public string UltimoModo { get; private set; } = "libre";

    public BotMove DecidirJugada(BotContext ctx)
    {
        // ── LENTE DE RETO (Resistencia demoníaca) ────────────────────────────
        // En una partida de RETO todos los bots comparten un ÚNICO objetivo: el
        // jugador humano. RetoFoco devuelve un contexto en el que los DEMÁS bots
        // no son enemigos (fuera del tablero y de los cuarteles rivales) pero SÍ
        // son obstáculo: sus celdas quedan marcadas como intransitables en el
        // terreno, así que todo lo que viene después (variantes, planificadores,
        // ReglasEntrada, lookahead) los rodea en vez de pisarlos. Ni se buscan
        // ni se estorban; si aun así coinciden en una celda, el servidor resuelve
        // el combate como siempre.
        // En una partida normal esto no hace NADA (devuelve el mismo ctx).
        ctx = RetoFoco.Aplicar(ctx);

        var planes = new List<BotMove>();
        var scores = new List<double>();
        var modos = new List<string>();

        // Primer plan de variante que se haya podido GENERAR (aunque su
        // puntuación fallara): red de seguridad si ningún candidato puntúa.
        BotMove? planEmergencia = null;

        // BLINDAJE: puntúa y añade el candidato de forma aislada. Un candidato
        // roto (null o que lance al puntuar) se descarta con log; el resto de la
        // decisión sigue. Si el lookahead falla, se reintenta con el evaluador
        // plano antes de descartar.
        void Anadir(BotMove? plan, string modo)
        {
            if (plan == null) return;   // planificador sin plan este turno (p. ej. defensa sin amenaza)

            // ── REGLAS QUE EL SERVIDOR NO IMPONE (v11) ─────────────────────
            // Escudo al cuartel propio y descargas inválidas se eliminan del
            // plan ANTES de puntuar: así ningún candidato "gana" la softmax
            // gracias a una acción que no se puede jugar.
            try
            {
                plan = Blindar(ctx, plan, out int quitadas);
                if (quitadas > 0)
                    Console.WriteLine($"[WZ][softmax {ctx.BotUid}] candidato '{modo}': {quitadas} acción(es) ilegal(es) eliminada(s)");
            }
            catch (Exception exBl)
            {
                Console.Error.WriteLine(
                    $"[WZ][softmax {ctx.BotUid}] blindar candidato '{modo}' falló ({exBl.GetType().Name}: {exBl.Message}); se puntúa sin blindar");
            }

            // ── RED FINAL (v10, caso 9UCN T11) ──────────────────────────────
            // TODO candidato pasa por ReglasEntrada.SanearPlan antes de
            // puntuarse: ninguna carta propia puede ENTRAR en una celda donde
            // el grupo que entra no gana (ni en un cuartel rival que no
            // conquista contando el refuerzo que su dueño puede desplegar).
            // Las cartas infractoras se recolocan en la celda segura más
            // cercana a su objetivo. Así la garantía no depende de que cada
            // planificador la respete por su cuenta.
            try
            {
                var saneado = ReglasEntrada.SanearPlan(ctx, plan, out int arreglos);
                if (arreglos > 0)
                    Console.WriteLine($"[WZ][softmax {ctx.BotUid}] candidato '{modo}': {arreglos} entrada(s) suicida(s) corregida(s)");
                plan = saneado;
            }
            catch (Exception exSan)
            {
                Console.Error.WriteLine(
                    $"[WZ][softmax {ctx.BotUid}] sanear candidato '{modo}' falló ({exSan.GetType().Name}: {exSan.Message}); se puntúa sin sanear");
            }

            planEmergencia ??= plan;
            double score;
            try
            {
                score = _usarLookahead
                    ? LookaheadDosPlies.Puntuar(ctx, plan)   // 2-3 plies: simula la respuesta enemiga
                    : EvaluadorTablero.Evaluar(ctx, plan);   // 1 ply: proxy heurístico
            }
            catch (Exception exLook)
            {
                Console.Error.WriteLine(
                    $"[WZ][softmax {ctx.BotUid}] puntuar candidato '{modo}' falló ({exLook.GetType().Name}: {exLook.Message}); reintento con evaluador plano");
                try { score = EvaluadorTablero.Evaluar(ctx, plan); }
                catch (Exception exEval)
                {
                    Console.Error.WriteLine(
                        $"[WZ][softmax {ctx.BotUid}] candidato '{modo}' descartado ({exEval.GetType().Name}: {exEval.Message})");
                    return;   // candidato fuera; la decisión continúa con el resto
                }
            }
            planes.Add(plan);
            scores.Add(score);
            modos.Add(modo);
        }

        // Genera un candidato de forma aislada: si el GENERADOR lanza, se
        // descarta solo ese candidato (con log), nunca la decisión entera.
        void Candidato(Func<BotMove?> generar, string modo)
        {
            BotMove? plan;
            try { plan = generar(); }
            catch (Exception ex)
            {
                Console.Error.WriteLine(
                    $"[WZ][softmax {ctx.BotUid}] generar candidato '{modo}' falló ({ex.GetType().Name}: {ex.Message}); descartado");
                return;
            }
            Anadir(plan, modo);
        }

        // Variantes de estilo (modo "libre").
        foreach (var v in _variantes) Candidato(() => v.DecidirJugada(ctx), "libre");

        // Modos como candidatos EXTRA; el lookahead elige cuál gana. El cambio de
        // modo es emergente de la simulación, no una regla que haya que acertar.
        // Los planificadores pueden devolver null / lista vacía (sin plan este
        // turno) y el null se filtra SIEMPRE.
        //
        // v11: la defensa aporta VARIAS variantes (base, descarga, trampa), cada
        // una con su modo. Si el generador entero falla, se descarta solo la
        // defensa; si falla un candidato al puntuar, solo ese candidato.
        List<(BotMove plan, string modo)> defensivos;
        try { defensivos = PlanificadorDefensivo.GenerarCandidatos(ctx); }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"[WZ][softmax {ctx.BotUid}] generar candidatos 'defensa' falló ({ex.GetType().Name}: {ex.Message}); descartados");
            defensivos = new List<(BotMove plan, string modo)>();
        }
        foreach (var (plan, modo) in defensivos) Anadir(plan, modo);       // guarnición acotada, anillo, caza, descarga, trampa

        Candidato(() => PlanificadorCaceria.Generar(ctx), "caceria");     // concentrar sobre una presa
        Candidato(() => PlanificadorFarmeo.Generar(ctx), "farmeo");       // apertura: dominar el centro

        if (planes.Count == 0)
        {
            // Ningún candidato puntuable. Si al menos una variante generó plan,
            // se juega ese plan sin puntuar (mucho mejor que un turno en blanco).
            if (planEmergencia != null)
            {
                Console.Error.WriteLine($"[WZ][softmax {ctx.BotUid}] sin candidatos puntuables; juego el plan de emergencia");
                UltimoModo = "libre";
                return planEmergencia;
            }
            // Ni siquiera hay plan crudo: que el cierre seguro de JugarTurnoAsync
            // haga de última red (arrastre del ejército).
            throw new InvalidOperationException("EstrategaSoftmaxStrategy: ningún candidato disponible");
        }

        int sel = Seleccionar(scores);
        UltimoModo = modos[sel];
        return planes[sel];
    }

    // ─────────────────────────────────────────────────────────────────────────
    // v11: BLINDAJE DE REGLAS sobre las acciones del plan
    // ─────────────────────────────────────────────────────────────────────────
    // Devuelve el mismo plan si no hay nada que quitar; si no, una copia sin las
    // acciones ilegales, con la mano restaurada (cartas de acción que se iban a
    // descartar) y el presupuesto de acciones recalculado.
    private static BotMove Blindar(BotContext ctx, BotMove plan, out int quitadas)
    {
        quitadas = 0;
        if (plan.Acciones.Count == 0) return plan;

        string miCuartel = ctx.Cuartel;
        bool descargaUsada = AccionesTacticas.DescargaUsada(ctx.Estado, ctx.BotUid);
        bool descargaEnPlan = false;

        var acciones = new List<Dictionary<string, object?>>();
        var devolver = new List<string>();   // cartas de mano de acciones eliminadas
        int energiaAcciones = 0;

        foreach (var a in plan.Acciones)
        {
            bool ilegal = false;

            if (AccionesTacticas.EsDescarga(a))
            {
                var objetivo = M.List(M.Get(a, "objetivos")).Select(M.Str).FirstOrDefault() ?? "";
                if (descargaUsada || descargaEnPlan || miCuartel == "" || objetivo != miCuartel
                    || M.Str(M.Get(a, "origen")) != miCuartel)
                    ilegal = true;
                else
                    descargaEnPlan = true;
            }
            else if (miCuartel != ""
                     && AccionesTacticas.Catalogo.TryGetValue(M.Int(M.Get(a, "habilidadId")), out var h)
                     && h.Efecto == AccionesTacticas.Efecto.Escudo)
            {
                // Regla del juego: NADIE puede escudar su propio cuartel (el
                // servidor no lo comprueba; el bot lo respeta).
                var objetivos = M.List(M.Get(a, "objetivos")).Select(M.Str).ToList();
                if (objetivos.Contains(miCuartel)) ilegal = true;
            }

            if (ilegal)
            {
                quitadas++;
                var cartaId = M.Str(M.Get(a, "cartaAccionId"));
                if (cartaId != "") devolver.Add(cartaId);
                continue;
            }

            acciones.Add(a);
            energiaAcciones += M.Int(M.Get(a, "costePagado"));
        }

        if (quitadas == 0) return plan;

        var mano = new List<string>(plan.ManoResultante);
        foreach (var id in devolver)
            if (!mano.Contains(id) && ctx.Mano.Contains(id)) mano.Add(id);

        return new BotMove
        {
            Celdas = plan.Celdas,
            Acciones = acciones,
            ManoResultante = mano,
            EnergiaGastada = plan.EnergiaGastada,
            EnergiaAcciones = energiaAcciones,
            EspecialComprada = plan.EspecialComprada,
        };
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