using System;
using System.Collections.Generic;
using System.Linq;

// ─────────────────────────────────────────────────────────────────────────────
// EvaluadorTablero.cs
//
// FUNCIÓN DE EVALUACIÓN AISLADA para las jugadas del bot.
//
// Dado un CONTEXTO de turno (BotContext) y un PLAN candidato (BotMove), devuelve
// una PUNTUACIÓN escalar (mayor = mejor PARA EL BOT) del tablero que resultaría
// de aplicar ese plan, teniendo en cuenta la RESPUESTA enemiga plausible.
//
// A diferencia de la v1 (que asumía al rival QUIETO), ahora se evalúan DOS mundos
// y se devuelve el PEOR (pesimista):
//   · PASIVO   — los enemigos no se mueven (farmean / defienden).
//   · AGRESIVO — los enemigos que PUEDEN contestar esta ronda avanzan: hacia el
//                cuartel del bot (para valorar la amenaza a la base) y hacia el
//                activo propio más cercano (para valorar qué piezas caerían).
// El avance está ACOTADO: solo se arrastran al modelo los enemigos que llegan a
// contestar este turno (dist <= mov+1). En turnos sin amenaza inmediata, agresivo
// == pasivo y no hay caución extra: la prudencia es situacional, no un turtling.
//
// Es DELIBERADAMENTE independiente de EstrategaStrategy: solo usa los tipos
// públicos BotContext / BotMove y el helper M. Así se calibra por separado (pesos
// abajo, como constantes con nombre, contra el corpus de EstudioPartidas).
//
// El plan (BotMove.Celdas) contiene SOLO las cartas propias reemitidas. Las
// enemigas se leen del estado actual (ctx.Estado["tablero"]).
//
// LIMITACIONES CONOCIDAS (v2), todas del lado SEGURO (hacen al bot algo más
// cauto, nunca más temerario):
//   · Trata TODA carta no propia como enemiga (con alianzas, el aliado cuenta como
//     rival). Es el mismo punto ciego que la propia EstrategaStrategy.
//   · El avance enemigo es greedy por Manhattan e IGNORA el terreno: en mapas con
//     mucho terreno impasable puede sobreestimar el alcance del rival.
//   · El combate se aproxima como fuerza_enemiga_que_alcanza > (fuerza+defensa)
//     propia en la celda; no resuelve el combate real ni el orden de bajas, y una
//     misma pieza enemiga puede "amenazar" dos celdas a la vez (sobreestima riesgo).
// ─────────────────────────────────────────────────────────────────────────────
public static class EvaluadorTablero
{
    // ── PESOS (tunables). Escala pensada para que el MATERIAL domine, la SEGURIDAD
    //    del cuartel pese mucho (perderlo = perder) y economía / presión / energía
    //    afinen el resto. Calibrar contra EstudioPartidas. ──
    private const double W_MATERIAL = 1.0;  // por punto de (Fuerza+Defensa) propio − enemigo
    private const double W_ECONOMIA = 0.8;  // por punto de farmeo de las celdas que ocupo
    private const double W_ACTIVIDAD = 2.0;  // por unidad propia ACTIVA (fuera de mi cuartel)
    private const double W_DEF_CUARTEL = 4.0;  // por punto de amenaza NO cubierta sobre mi cuartel (penaliza)
    private const double W_PRESION = 1.5;  // por punto de cercanía a cuarteles enemigos
    private const double W_ENERGIA_OCIOSA = 0.5;  // por punto de energía ociosa sobre la reserva (penaliza)

    // NUEVO (v2): penalización por punto de material propio (fuera del cuartel) que
    // la respuesta enemiga CAPTURARÍA esta ronda. Por encima de W_MATERIAL para que
    // "este plan deja una pieza a tiro" pese más que el material que nominalmente suma.
    private const double W_MATERIAL_RIESGO = 1.5;

    // Energía que se considera "reserva razonable"; por encima se penaliza dejarla
    // sin usar (contra la pasividad).
    private const int UMBRAL_RESERVA_ENERGIA = 20;

    // Bono de defensa que un cuartel PROPIO defendido da a su dueño. DEBE coincidir
    // con UmbralCuartel de EstrategaStrategy / Combate.DefensaObelisco.
    private const int BONO_CUARTEL = 40;

    // Radio (Manhattan) desde el que una unidad propia empieza a "presionar" un
    // cuartel enemigo. Cuanto más cerca, más presión.
    private const int RADIO_PRESION = 12;

    // Distancia Manhattan a la que un stack se considera capaz de CONTESTAR (atacar)
    // una celda esta ronda una vez colocado. Adyacencia (incluye estar encima).
    private const int ALCANCE_CONTESTACION = 1;

    /// Puntúa el tablero resultante de aplicar `plan`, tomando el PEOR de la
    /// respuesta enemiga pasiva vs. agresiva acotada (pesimista).
    public static double Evaluar(BotContext ctx, BotMove plan)
    {
        int filas = ctx.Filas, columnas = ctx.Columnas;
        string botUid = ctx.BotUid;

        // ── Cuarteles: coord -> uid dueño; localizar el mío ──
        var obeliscos = M.Map(M.Get(ctx.Estado, "obeliscos"));
        var eliminados = M.List(M.Get(ctx.Estado, "jugadoresEliminados"))
            .Select(M.Str).ToHashSet();

        var cuartelOwner = new Dictionary<string, string>();
        string? miCuartel = ctx.Cuartel != "" ? ctx.Cuartel : null;
        foreach (var (uid, cObj) in obeliscos)
        {
            var c = M.Str(cObj);
            if (c == "") continue;
            cuartelOwner[c] = uid;
            if (uid == botUid && miCuartel == null) miCuartel = c;
        }
        var cuartelesEnemigos = cuartelOwner
            .Where(kv => kv.Value != botUid && !eliminados.Contains(kv.Value))
            .Select(kv => kv.Key)
            .ToList();

        // ── Enemigos en su posición ACTUAL (una entrada por celda ocupada) ──
        var enemigos = new List<(string coord, int f, int d, int mov)>();
        int enemyMat = 0;
        var tablero = M.Map(M.Get(ctx.Estado, "tablero"));
        foreach (var (coord, raw) in tablero)
        {
            int f = 0, d = 0, mov = 0;
            foreach (var cRaw in M.List(raw))
            {
                var card = M.Map(cRaw);
                if (M.Str(M.Get(card, "ownerUid")) == botUid) continue; // propia: no aquí
                f += Fuerza(card); d += Defensa(card); mov = Math.Max(mov, Mov(card));
            }
            if (f > 0 || d > 0) { enemigos.Add((coord, f, d, mov)); enemyMat += f + d; }
        }

        // ── Propias PROYECTADAS (del plan) ──
        int ownMat = 0, unidadesActivas = 0, defensaEnMiCuartel = 0;
        double economia = 0.0;
        var celdasConPropia = new List<string>();
        var misUnidades = new List<(string coord, int f, int d)>(); // NO incluye el cuartel
        foreach (var (coord, cartas) in plan.Celdas)
        {
            int fCelda = 0, dCelda = 0, nPropias = 0;
            foreach (var card in cartas)
            {
                if (M.Str(M.Get(card, "ownerUid")) != botUid) continue; // defensivo
                fCelda += Fuerza(card); dCelda += Defensa(card); nPropias++;
            }
            if (nPropias == 0) continue;

            ownMat += fCelda + dCelda;
            celdasConPropia.Add(coord);

            if (miCuartel != null && coord == miCuartel)
                defensaEnMiCuartel += dCelda;      // guarnición: no es tropa activa
            else
            {
                unidadesActivas += nPropias;       // fuera del cuartel = tropa activa
                misUnidades.Add((coord, fCelda, dCelda));
            }

            economia += FarmValue(coord, ctx, cuartelOwner, botUid);
        }

        // ── Presión sobre cuarteles enemigos (independiente de dónde estén las
        //    UNIDADES enemigas: depende de sus cuarteles, que no se mueven) ──
        double presion = 0.0;
        foreach (var q in cuartelesEnemigos)
        {
            int mejor = int.MaxValue;
            foreach (var c in celdasConPropia)
            {
                int dd = Manhattan(c, q, filas, columnas);
                if (dd < mejor) mejor = dd;
            }
            if (mejor != int.MaxValue) presion += Math.Max(0, RADIO_PRESION - mejor);
        }

        // ── Energía ociosa (contra la pasividad) ──
        int energiaRestante = Math.Max(0, ctx.Energia - plan.EnergiaGastada);
        double penalEnergia = Math.Max(0, energiaRestante - UMBRAL_RESERVA_ENERGIA);

        // ── Términos INDEPENDIENTES de la intención enemiga ──
        double baseScore =
              W_MATERIAL * (ownMat - enemyMat)
            + W_ECONOMIA * economia
            + W_ACTIVIDAD * unidadesActivas
            + W_PRESION * presion
            - W_ENERGIA_OCIOSA * penalEnergia;

        // ── Amenaza al cuartel dada una disposición enemiga ──
        double AmenazaCuartel(List<(string coord, int f, int d, int mov)> disp)
        {
            if (miCuartel == null) return 0.0;
            int amenaza = 0;
            foreach (var e in disp)
                if (Manhattan(e.coord, miCuartel, filas, columnas) <= ALCANCE_CONTESTACION)
                    amenaza += e.f;
            int defensa = defensaEnMiCuartel + BONO_CUARTEL;
            return amenaza > defensa ? amenaza - defensa : 0.0;
        }

        // ── Material propio (no cuartel) que el enemigo captura esta ronda ──
        double Riesgo(List<(string coord, int f, int d, int mov)> disp)
        {
            double enRiesgo = 0.0;
            foreach (var u in misUnidades)
            {
                int fuerzaEnemiga = 0;
                foreach (var e in disp)
                    if (Manhattan(e.coord, u.coord, filas, columnas) <= ALCANCE_CONTESTACION)
                        fuerzaEnemiga += e.f;
                if (fuerzaEnemiga > u.f + u.d) enRiesgo += u.f + u.d; // combate determinista (proxy)
            }
            return enRiesgo;
        }

        double Puntuar(List<(string coord, int f, int d, int mov)> dispCuartel,
                       List<(string coord, int f, int d, int mov)> dispRiesgo)
            => baseScore
               - W_DEF_CUARTEL * AmenazaCuartel(dispCuartel)
               - W_MATERIAL_RIESGO * Riesgo(dispRiesgo);

        // Mundo PASIVO: enemigos quietos.
        double sPasivo = Puntuar(enemigos, enemigos);

        // Mundo AGRESIVO (acotado): los enemigos que alcanzan a contestar avanzan.
        // Amenaza a base → avance hacia el cuartel; riesgo de piezas → avance hacia
        // el activo propio más cercano (cuartel o cualquier unidad).
        var objetivosRiesgo = new List<string>();
        if (miCuartel != null) objetivosRiesgo.Add(miCuartel);
        objetivosRiesgo.AddRange(misUnidades.Select(u => u.coord));

        var dispHaciaCuartel = miCuartel == null
            ? enemigos
            : Avanzar(enemigos, new List<string> { miCuartel }, filas, columnas);
        var dispHaciaCercano = objetivosRiesgo.Count == 0
            ? enemigos
            : Avanzar(enemigos, objetivosRiesgo, filas, columnas);

        double sAgresivo = Puntuar(dispHaciaCuartel, dispHaciaCercano);

        // PESIMISTA: el peor de los mundos plausibles.
        return Math.Min(sPasivo, sAgresivo);
    }

    // Avanza cada stack hasta `mov` pasos hacia su objetivo MÁS CERCANO de la lista,
    // solo si puede llegar a contestar esta ronda (dist <= mov + alcance). Greedy por
    // Manhattan; ignora terreno (proxy). Los que no alcanzan se quedan quietos.
    private static List<(string coord, int f, int d, int mov)> Avanzar(
        List<(string coord, int f, int d, int mov)> stacks, List<string> objetivos,
        int filas, int columnas)
    {
        var res = new List<(string coord, int f, int d, int mov)>(stacks.Count);
        foreach (var e in stacks)
        {
            string mejorObj = ""; int mejorDist = int.MaxValue;
            foreach (var o in objetivos)
            {
                int dd = Manhattan(e.coord, o, filas, columnas);
                if (dd < mejorDist) { mejorDist = dd; mejorObj = o; }
            }
            if (mejorObj == "" || mejorDist == int.MaxValue
                || mejorDist > e.mov + ALCANCE_CONTESTACION)
            {
                res.Add(e); // fuera de alcance o sin objetivo: no es amenaza este turno
                continue;
            }
            res.Add((PasoHacia(e.coord, mejorObj, e.mov, filas, columnas), e.f, e.d, e.mov));
        }
        return res;
    }

    // Da hasta `pasos` pasos desde `desde` reduciendo la distancia Manhattan a
    // `hacia` (un eje por paso, el de mayor delta restante). Clampa al tablero.
    private static string PasoHacia(string desde, string hacia, int pasos, int filas, int columnas)
    {
        var pa = Parse(desde); var pb = Parse(hacia);
        if (pa == null || pb == null) return desde;
        int ri = pa.Value.ri, ci = pa.Value.ci;
        int tri = pb.Value.ri, tci = pb.Value.ci;
        for (int k = 0; k < pasos; k++)
        {
            int dr = tri - ri, dc = tci - ci;
            if (dr == 0 && dc == 0) break;
            if (Math.Abs(dr) >= Math.Abs(dc)) ri += Math.Sign(dr);
            else ci += Math.Sign(dc);
        }
        ri = Math.Clamp(ri, 0, Math.Max(0, filas - 1));
        ci = Math.Clamp(ci, 0, Math.Max(0, columnas - 1));
        return Format(ri, ci);
    }

    // farmValue: energía/turno que daría ocupar esa celda (rayo +10, isla central
    // +7, continente de un rival +5).
    private static double FarmValue(
        string cell, BotContext ctx, Dictionary<string, string> cuartelOwner, string botUid)
    {
        double v = 0;
        if (ctx.Rayos.Contains(cell)) v += 10;
        if (ctx.IslaCentral.Contains(cell)) v += 7;
        foreach (var (obelisco, celdas) in ctx.Continentes)
            if (celdas.Contains(cell))
            {
                var owner = cuartelOwner.GetValueOrDefault(obelisco, "");
                if (owner != "" && owner != botUid) v += 5;
            }
        return v;
    }

    // ── Lectura de stats (réplica local; NO depende de EstrategaStrategy) ──
    private static int Fuerza(Dictionary<string, object?> c) => M.Int(M.Get(c, "Fuerza", "fuerza"));
    private static int Defensa(Dictionary<string, object?> c) => M.Int(M.Get(c, "Defensa", "defensa"));
    private static int Mov(Dictionary<string, object?> c) => M.Int(M.Get(c, "Movimiento", "movimiento"));

    // ── Geometría (formato Letra+Número, p. ej. "B3") ──
    private static (int ri, int ci)? Parse(string coord)
    {
        if (string.IsNullOrEmpty(coord) || coord.Length < 2) return null;
        int ri = char.ToUpperInvariant(coord[0]) - 'A';
        if (!int.TryParse(coord[1..], out int col)) return null;
        return (ri, col - 1);
    }
    private static string Format(int ri, int ci) => $"{(char)('A' + ri)}{ci + 1}";
    private static int Manhattan(string a, string b, int filas, int columnas)
    {
        var pa = Parse(a); var pb = Parse(b);
        if (pa == null || pb == null) return int.MaxValue;
        return Math.Abs(pa.Value.ri - pb.Value.ri) + Math.Abs(pa.Value.ci - pb.Value.ci);
    }
}