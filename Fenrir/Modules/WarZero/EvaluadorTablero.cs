using System;
using System.Collections.Generic;
using System.Linq;

// ─────────────────────────────────────────────────────────────────────────────
// EvaluadorTablero.cs  (v3)
//
// FUNCIÓN DE EVALUACIÓN AISLADA para las jugadas del bot. Dado el CONTEXTO de
// turno (BotContext) y un PLAN candidato (BotMove), devuelve una PUNTUACIÓN
// (mayor = mejor PARA EL BOT) del tablero resultante, teniendo en cuenta la
// respuesta enemiga plausible.
//
// Evalúa DOS mundos y se queda con el PEOR (pesimista):
//   · PASIVO   — los enemigos no se mueven (farmean / defienden).
//   · AGRESIVO — los enemigos que pueden contestar esta ronda avanzan (hacia el
//                cuartel del bot y hacia su activo más cercano).
//
// Novedades v3:
//   1. ECONOMÍA con más peso: ocupar celdas de energía renta CADA turno, así que
//      su valor se pondera al alza como proxy del ingreso futuro (el evaluador es
//      de 1 ply y no ve el interés compuesto de sentarse en un rayo).
//   2. ENERGÍA PÚBLICA del rival en el pesimismo: la energía de cada jugador es
//      información pública. Un enemigo rico puede DESPLEGAR refuerzos (de una mano
//      que no ves) para atacar o defender; uno arruinado no. Por eso la fuerza de
//      contestación de cada stack enemigo se escala por la energía de su dueño.
//   3. GENERAL como pieza única: no se puede recomprar tras morir, así que dejar
//      un general propio a tiro penaliza MUCHO más que su fuerza+defensa en bruto.
//
// LIMITACIONES CONOCIDAS (todas del lado seguro: hacen al bot algo más cauto):
//   · Trata toda carta no propia como enemiga (con alianzas, el aliado cuenta).
//   · El avance enemigo es greedy por Manhattan e ignora el terreno.
//   · El combate se aproxima como fuerza_enemiga_que_alcanza > (fuerza+defensa)
//     propia; no resuelve el combate real, y una pieza puede figurar amenazando
//     dos celdas a la vez (sobreestima el riesgo).
// ─────────────────────────────────────────────────────────────────────────────
public static class EvaluadorTablero
{
    // ── PESOS (tunables) ──
    private const double W_MATERIAL = 1.0;  // por punto de (Fuerza+Defensa) propio − enemigo
    private const double W_ECONOMIA = 1.5;  // por punto de farmeo ocupado (subido: proxy de ingreso futuro)
    private const double W_ACTIVIDAD = 2.0;  // por unidad propia ACTIVA (fuera de mi cuartel)
    private const double W_DEF_CUARTEL = 4.0;  // por punto de amenaza NO cubierta sobre mi cuartel (penaliza)
    private const double W_PRESION = 1.5;  // por punto de cercanía a cuarteles enemigos
    private const double W_ENERGIA_OCIOSA = 0.5;  // por punto de energía ociosa sobre la reserva (penaliza)
    private const double W_MATERIAL_RIESGO = 1.5;  // por punto de material propio que el rival capturaría (penaliza)

    private const int UMBRAL_RESERVA_ENERGIA = 20;
    private const int BONO_CUARTEL = 40;   // debe coincidir con UmbralCuartel / Combate.DefensaObelisco
    private const int RADIO_PRESION = 12;
    private const int ALCANCE_CONTESTACION = 1;   // adyacencia Manhattan para "poder atacar" esta ronda

    // Energía pública → capacidad de refuerzo del rival. La fuerza de contestación
    // de un stack enemigo se multiplica por 1 + min(REFUERZO_MAX, energía/ENERGIA_POR_REFUERZO):
    // enemigo sin energía = ×1 (no puede reforzar), enemigo rico = hasta ×2.
    private const int ENERGIA_POR_REFUERZO = 60;
    private const double REFUERZO_MAX = 1.0;

    // Penalización EXTRA por dejar un general propio (no cuartel) a tiro, por encima
    // de su fuerza+defensa: refleja que es irreemplazable (no se recompra al morir).
    private const int BONO_GENERAL_RIESGO = 60;
    private const int COND_GENERAL = 5;

    public static double Evaluar(BotContext ctx, BotMove plan)
    {
        int filas = ctx.Filas, columnas = ctx.Columnas;
        string botUid = ctx.BotUid;

        // Energía pública por jugador (para el factor de refuerzo).
        var stats = M.Map(M.Get(ctx.Estado, "statsPartida"));
        int EnergiaDe(string uid) => M.Int(M.Get(M.Map(M.Get(stats, uid)), "energies"));

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
        //    Se captura la energía del DUEÑO del stack (la mayor si hubiera mezcla).
        var enemigos = new List<(string coord, int f, int d, int mov, int energia)>();
        int enemyMat = 0;
        var tablero = M.Map(M.Get(ctx.Estado, "tablero"));
        foreach (var (coord, raw) in tablero)
        {
            int f = 0, d = 0, mov = 0, enerDueno = 0;
            foreach (var cRaw in M.List(raw))
            {
                var card = M.Map(cRaw);
                var owner = M.Str(M.Get(card, "ownerUid"));
                if (owner == botUid) continue; // propia: no aquí
                f += Fuerza(card); d += Defensa(card); mov = Math.Max(mov, Mov(card));
                enerDueno = Math.Max(enerDueno, EnergiaDe(owner));
            }
            if (f > 0 || d > 0) { enemigos.Add((coord, f, d, mov, enerDueno)); enemyMat += f + d; }
        }

        // ── Propias PROYECTADAS (del plan) ──
        int ownMat = 0, unidadesActivas = 0, defensaEnMiCuartel = 0;
        double economia = 0.0;
        var celdasConPropia = new List<string>();
        var misUnidades = new List<(string coord, int f, int d, bool general)>(); // sin el cuartel
        foreach (var (coord, cartas) in plan.Celdas)
        {
            int fCelda = 0, dCelda = 0, nPropias = 0; bool hayGeneral = false;
            foreach (var card in cartas)
            {
                if (M.Str(M.Get(card, "ownerUid")) != botUid) continue; // defensivo
                fCelda += Fuerza(card); dCelda += Defensa(card); nPropias++;
                if (M.Int(M.Get(card, "Condicion", "condicion")) == COND_GENERAL) hayGeneral = true;
            }
            if (nPropias == 0) continue;

            ownMat += fCelda + dCelda;
            celdasConPropia.Add(coord);

            if (miCuartel != null && coord == miCuartel)
                defensaEnMiCuartel += dCelda;      // guarnición: no es tropa activa
            else
            {
                unidadesActivas += nPropias;
                misUnidades.Add((coord, fCelda, dCelda, hayGeneral));
            }

            economia += FarmValue(coord, ctx, cuartelOwner, botUid);
        }

        // ── Presión sobre cuarteles enemigos (no depende de las UNIDADES enemigas) ──
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

        // Factor de refuerzo por energía pública del dueño del stack.
        static double Refuerzo(int energiaDueno)
            => 1.0 + Math.Min(REFUERZO_MAX, energiaDueno / (double)ENERGIA_POR_REFUERZO);

        // ── Amenaza al cuartel dada una disposición enemiga ──
        double AmenazaCuartel(List<(string coord, int f, int d, int mov, int energia)> disp)
        {
            if (miCuartel == null) return 0.0;
            double amenaza = 0.0;
            foreach (var e in disp)
                if (Manhattan(e.coord, miCuartel, filas, columnas) <= ALCANCE_CONTESTACION)
                    amenaza += e.f * Refuerzo(e.energia);
            double defensa = defensaEnMiCuartel + BONO_CUARTEL;
            return amenaza > defensa ? amenaza - defensa : 0.0;
        }

        // ── Material propio (no cuartel) que el enemigo captura esta ronda ──
        //    Un general a tiro penaliza además por su irremplazabilidad.
        double Riesgo(List<(string coord, int f, int d, int mov, int energia)> disp)
        {
            double enRiesgo = 0.0;
            foreach (var u in misUnidades)
            {
                double fuerzaEnemiga = 0.0;
                foreach (var e in disp)
                    if (Manhattan(e.coord, u.coord, filas, columnas) <= ALCANCE_CONTESTACION)
                        fuerzaEnemiga += e.f * Refuerzo(e.energia);
                if (fuerzaEnemiga > u.f + u.d)
                    enRiesgo += (u.f + u.d) + (u.general ? BONO_GENERAL_RIESGO : 0);
            }
            return enRiesgo;
        }

        double Puntuar(List<(string coord, int f, int d, int mov, int energia)> dispCuartel,
                       List<(string coord, int f, int d, int mov, int energia)> dispRiesgo)
            => baseScore
               - W_DEF_CUARTEL * AmenazaCuartel(dispCuartel)
               - W_MATERIAL_RIESGO * Riesgo(dispRiesgo);

        // Mundo PASIVO: enemigos quietos.
        double sPasivo = Puntuar(enemigos, enemigos);

        // Mundo AGRESIVO (acotado): los enemigos que alcanzan a contestar avanzan.
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
    // Manhattan; ignora terreno (proxy). Preserva la energía del dueño.
    private static List<(string coord, int f, int d, int mov, int energia)> Avanzar(
        List<(string coord, int f, int d, int mov, int energia)> stacks, List<string> objetivos,
        int filas, int columnas)
    {
        var res = new List<(string coord, int f, int d, int mov, int energia)>(stacks.Count);
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
            res.Add((PasoHacia(e.coord, mejorObj, e.mov, filas, columnas), e.f, e.d, e.mov, e.energia));
        }
        return res;
    }

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