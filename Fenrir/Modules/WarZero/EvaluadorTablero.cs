using System;
using System.Collections.Generic;
using System.Linq;

// ─────────────────────────────────────────────────────────────────────────────
// EvaluadorTablero.cs  (v7)
//
// Función de evaluación aislada. Puntúa (mayor = mejor PARA EL BOT) el tablero
// resultante de un plan, con respuesta enemiga pesimista (dos mundos, el peor).
//
// Cambios v7:
//   C. CASTIGO AL PICOTEO SUICIDA. El gate de fuerza de la v5 quitaba el PREMIO de
//      presión a una unidad débil junto a un cuartel enemigo, pero no la penalizaba.
//      Además, FarmValue premia estar en el continente enemigo (+5), lo que ATRAÍA
//      unidades hacia los cuarteles rivales, donde una carta débil solo se suicida
//      contra el +40 de defensa. Ahora, cada unidad propia demasiado débil para
//      amenazar (Fuerza < UMBRAL_FUERZA_PRESION) que quede adyacente a un cuartel
//      enemigo RESTA su material (f+d) con peso W_SUICIDIO. Simétrico al gate:
//      fuerte cerca del cuartel = presión; débil = suicidio penalizado.
//   D. EL CENTRO VALE MÁS Y NO SE ABANDONA. W_CENTRO sube (4 → 8) y se añade
//      W_CENTRO_ENEMIGO: cada celda de la isla central en manos enemigas penaliza.
//      Así ceder el centro es doblemente caro (pierdes tu bonus y el rival gana el
//      suyo), y mantenerlo compite de sobra contra el farmeo de continente enemigo.
//
// Cambios v5:
//   A. CENTRO como objetivo estratégico. La isla central no solo da energía: es el
//      único paso terrestre entre continentes (cuello de botella). Se le añade un
//      bonus por celda propia en la isla, POR ENCIMA de su farmeo, para que los
//      bots peleen por el centro y —sobre todo— NO lo abandonen. El bonus es
//      estratégico, así que se mantiene aunque el modo victoria baje la economía.
//   B. PRESIÓN con gate de fuerza. Antes, acercar CUALQUIER unidad a un cuartel
//      enemigo daba premio de presión, así que el bot mandaba cartas débiles y
//      rápidas a picar junto a un cuartel (+40 de defensa) donde solo se suicidaban
//      —el movimiento errático que se veía en las partidas—. Ahora solo cuentan
//      como amenaza las celdas con fuerza suficiente para inquietar de verdad a un
//      cuartel; una unidad débil junto al cuartel ya no da premio, solo penaliza
//      por el riesgo de morir.
//
// (v4: pesimismo acotado a la amenaza al cuartel, anti-congelación, modo victoria.)
//
// LIMITACIONES (del lado seguro): trata toda carta no propia como enemiga; el
// avance enemigo es greedy por Manhattan (ignora terreno); el combate se aproxima
// como fuerza_enemiga_que_alcanza > (fuerza+defensa) propia.
// ─────────────────────────────────────────────────────────────────────────────
public static class EvaluadorTablero
{
    // ── PESOS BASE (tunables) ──
    private const double W_MATERIAL = 1.0;
    private const double W_ECONOMIA = 1.5;
    private const double W_ACTIVIDAD = 2.0;
    private const double W_DEF_CUARTEL = 4.0;
    private const double W_PRESION = 1.5;
    private const double W_ENERGIA_OCIOSA = 0.5;
    private const double W_MATERIAL_RIESGO = 1.5;

    // Bonus estratégico por celda propia en la ISLA CENTRAL (energía + cuello de
    // botella). Es lo que hace que el centro se pelee y no se abandone. v7: sube de
    // 4 a 8 para que mantener el centro pese de verdad frente al farmeo de
    // continente enemigo (+5) y la actividad.
    private const double W_CENTRO = 8.0;

    // Castigo por cada celda de la ISLA CENTRAL en manos ENEMIGAS (v7). Ceder el
    // centro deja de ser gratis: no solo pierdes tu bonus, el rival gana el suyo.
    private const double W_CENTRO_ENEMIGO = 4.0;

    // Castigo por cada unidad propia DÉBIL (Fuerza < UMBRAL_FUERZA_PRESION) que
    // quede adyacente a un cuartel enemigo (v7). Se resta su material (f+d) con
    // este peso: acercar una carta débil a un cuartel es tirarla contra el +40 de
    // defensa. Simétrico al gate de presión: fuerte = amenaza, débil = suicidio.
    private const double W_SUICIDIO = 2.0;

    private const int UMBRAL_RESERVA_ENERGIA = 20;
    private const int BONO_CUARTEL = 40;
    private const int RADIO_PRESION = 12;
    private const int ALCANCE_CONTESTACION = 1;

    // Fuerza mínima de una celda propia para contar como AMENAZA a un cuartel
    // enemigo. Por debajo, acercarse solo es picar en balde (no da presión) y
    // ADEMÁS penaliza como suicidio (v7).
    private const int UMBRAL_FUERZA_PRESION = 20;

    // Energía pública → refuerzo del rival. SOLO se usa en la amenaza al cuartel.
    private const int ENERGIA_POR_REFUERZO = 60;
    private const double REFUERZO_MAX = 0.6;

    private const int BONO_GENERAL_RIESGO = 60;
    private const int COND_GENERAL = 5;

    // ── MODO VICTORIA (anti-acumulación) ──
    private const int UMBRAL_VICTORIA = 400;
    private const double FACTOR_ECO_VICTORIA = 0.2;
    private const double FACTOR_PRESION_VICTORIA = 2.0;

    // ── ANTI-CONGELACIÓN (bot rezagado) ──
    private const int UMBRAL_PRESENCIA_MINIMA = 1;
    private const double FACTOR_RIESGO_SIN_PRESENCIA = 0.3;

    public static double Evaluar(BotContext ctx, BotMove plan)
    {
        int filas = ctx.Filas, columnas = ctx.Columnas;
        string botUid = ctx.BotUid;

        var stats = M.Map(M.Get(ctx.Estado, "statsPartida"));
        int EnergiaDe(string uid) => M.Int(M.Get(M.Map(M.Get(stats, uid)), "energies"));

        // ── Cuarteles ──
        var obeliscos = M.Map(M.Get(ctx.Estado, "obeliscos"));
        var eliminados = M.List(M.Get(ctx.Estado, "jugadoresEliminados")).Select(M.Str).ToHashSet();

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
            .Select(kv => kv.Key).ToList();

        // ── Enemigos (con energía del dueño de cada stack) ──
        var enemigos = new List<(string coord, int f, int d, int mov, int energia)>();
        int enemyMat = 0, centroEnemigo = 0;
        var tablero = M.Map(M.Get(ctx.Estado, "tablero"));
        foreach (var (coord, raw) in tablero)
        {
            int f = 0, d = 0, mov = 0, enerDueno = 0;
            foreach (var cRaw in M.List(raw))
            {
                var card = M.Map(cRaw);
                var owner = M.Str(M.Get(card, "ownerUid"));
                if (owner == botUid) continue;
                f += Fuerza(card); d += Defensa(card); mov = Math.Max(mov, Mov(card));
                enerDueno = Math.Max(enerDueno, EnergiaDe(owner));
            }
            if (f > 0 || d > 0)
            {
                enemigos.Add((coord, f, d, mov, enerDueno)); enemyMat += f + d;
                if (ctx.IslaCentral.Contains(coord)) centroEnemigo++;   // centro cedido al rival
            }
        }

        // ── Propias PROYECTADAS ──
        int ownMat = 0, unidadesActivas = 0, defensaEnMiCuartel = 0, celdasCentro = 0;
        double economia = 0.0;
        var misUnidades = new List<(string coord, int f, int d, bool general)>(); // sin cuartel
        foreach (var (coord, cartas) in plan.Celdas)
        {
            int fCelda = 0, dCelda = 0, nPropias = 0; bool hayGeneral = false;
            foreach (var card in cartas)
            {
                if (M.Str(M.Get(card, "ownerUid")) != botUid) continue;
                fCelda += Fuerza(card); dCelda += Defensa(card); nPropias++;
                if (M.Int(M.Get(card, "Condicion", "condicion")) == COND_GENERAL) hayGeneral = true;
            }
            if (nPropias == 0) continue;

            ownMat += fCelda + dCelda;
            if (ctx.IslaCentral.Contains(coord)) celdasCentro++;   // control del centro

            if (miCuartel != null && coord == miCuartel)
                defensaEnMiCuartel += dCelda;
            else
            {
                unidadesActivas += nPropias;
                misUnidades.Add((coord, fCelda, dCelda, hayGeneral));
            }
            economia += FarmValue(coord, ctx, cuartelOwner, botUid);
        }

        // ── MODOS según la situación económica del bot ──
        bool modoVictoria = ctx.Energia >= UMBRAL_VICTORIA;
        bool presenciaMinima = unidadesActivas <= UMBRAL_PRESENCIA_MINIMA;

        double wEconomia = W_ECONOMIA * (modoVictoria ? FACTOR_ECO_VICTORIA : 1.0);
        double wPresion = W_PRESION * (modoVictoria ? FACTOR_PRESION_VICTORIA : 1.0);
        double wRiesgo = W_MATERIAL_RIESGO * (presenciaMinima ? FACTOR_RIESGO_SIN_PRESENCIA : 1.0);

        // ── Presión sobre cuarteles enemigos (SOLO desde celdas con fuerza real) y
        //    CASTIGO al picoteo suicida (unidad débil adyacente a un cuartel) ──
        double presion = 0.0, suicidioDebil = 0.0;
        foreach (var q in cuartelesEnemigos)
        {
            int mejor = int.MaxValue;
            foreach (var u in misUnidades)
            {
                int dd = Manhattan(u.coord, q, filas, columnas);
                if (u.f < UMBRAL_FUERZA_PRESION)
                {
                    // Débil junto a un cuartel = picar en balde: no da presión y,
                    // si está adyacente, penaliza como suicidio (va a morir).
                    if (dd <= ALCANCE_CONTESTACION) suicidioDebil += u.f + u.d;
                    continue;
                }
                if (dd < mejor) mejor = dd;
            }
            if (mejor != int.MaxValue) presion += Math.Max(0, RADIO_PRESION - mejor);
        }

        int energiaRestante = Math.Max(0, ctx.Energia - plan.EnergiaGastada);
        double penalEnergia = Math.Max(0, energiaRestante - UMBRAL_RESERVA_ENERGIA);

        // ── Términos independientes de la intención enemiga ──
        double baseScore =
              W_MATERIAL * (ownMat - enemyMat)
            + wEconomia * economia
            + W_ACTIVIDAD * unidadesActivas
            + W_CENTRO * celdasCentro          // control del centro (estratégico)
            - W_CENTRO_ENEMIGO * centroEnemigo // centro cedido al rival (v7)
            + wPresion * presion
            - W_SUICIDIO * suicidioDebil       // picoteo suicida (v7)
            - W_ENERGIA_OCIOSA * penalEnergia;

        // Factor de refuerzo por energía pública (SOLO amenaza al cuartel).
        static double Refuerzo(int energiaDueno)
            => 1.0 + Math.Min(REFUERZO_MAX, energiaDueno / (double)ENERGIA_POR_REFUERZO);

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

        double Riesgo(List<(string coord, int f, int d, int mov, int energia)> disp)
        {
            double enRiesgo = 0.0;
            foreach (var u in misUnidades)
            {
                double fuerzaEnemiga = 0.0;
                foreach (var e in disp)
                    if (Manhattan(e.coord, u.coord, filas, columnas) <= ALCANCE_CONTESTACION)
                        fuerzaEnemiga += e.f;
                if (fuerzaEnemiga > u.f + u.d)
                    enRiesgo += (u.f + u.d) + (u.general ? BONO_GENERAL_RIESGO : 0);
            }
            return enRiesgo;
        }

        double Puntuar(List<(string coord, int f, int d, int mov, int energia)> dispCuartel,
                       List<(string coord, int f, int d, int mov, int energia)> dispRiesgo)
            => baseScore
               - W_DEF_CUARTEL * AmenazaCuartel(dispCuartel)
               - wRiesgo * Riesgo(dispRiesgo);

        double sPasivo = Puntuar(enemigos, enemigos);

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

        return Math.Min(sPasivo, sAgresivo);
    }


    // ─────────────────────────────────────────────────────────────────────────
    // EvaluarPosicion: puntúa un TABLERO YA RESUELTO para el bot. A diferencia de
    // Evaluar (que proyecta un plan y modela la respuesta enemiga con un proxy),
    // aquí NO hay pesimismo interno: el lookahead (Tarea 2) simula la respuesta
    // del rival de verdad y llama a esto sobre CADA tablero resultante, quedándose
    // con el peor. Por eso tampoco hay término de "material en riesgo": las bajas
    // ya están resueltas en el tablero. Es la función de evaluación de HOJA.
    // ─────────────────────────────────────────────────────────────────────────
    public static double EvaluarPosicion(
        BotContext ctx, Dictionary<string, List<Dictionary<string, object?>>> tablero)
    {
        int filas = ctx.Filas, columnas = ctx.Columnas;
        string botUid = ctx.BotUid;

        var obeliscos = M.Map(M.Get(ctx.Estado, "obeliscos"));
        var eliminados = M.List(M.Get(ctx.Estado, "jugadoresEliminados")).Select(M.Str).ToHashSet();
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
            .Select(kv => kv.Key).ToList();

        int ownMat = 0, enemyMat = 0, unidadesActivas = 0, defensaEnMiCuartel = 0;
        int celdasCentro = 0, centroEnemigo = 0;
        double economia = 0.0;
        var misUnidades = new List<(string coord, int f, int d)>();   // celdas propias activas (no cuartel)
        var enemigos = new List<(string coord, int f)>();             // celdas enemigas
        foreach (var (coord, cartas) in tablero)
        {
            int fMia = 0, dMia = 0, nMias = 0, fEne = 0, dEne = 0;
            foreach (var card in cartas)
            {
                var owner = M.Str(M.Get(card, "ownerUid"));
                if (owner == botUid) { fMia += Fuerza(card); dMia += Defensa(card); nMias++; }
                else if (owner != "") { fEne += Fuerza(card); dEne += Defensa(card); }
            }
            if (nMias > 0)
            {
                ownMat += fMia + dMia;
                if (ctx.IslaCentral.Contains(coord)) celdasCentro++;
                if (miCuartel != null && coord == miCuartel) defensaEnMiCuartel += dMia;
                else { unidadesActivas += nMias; misUnidades.Add((coord, fMia, dMia)); }
                economia += FarmValue(coord, ctx, cuartelOwner, botUid);
            }
            if (fEne > 0 || dEne > 0)
            {
                enemigos.Add((coord, fEne)); enemyMat += fEne + dEne;
                if (ctx.IslaCentral.Contains(coord)) centroEnemigo++;   // centro cedido al rival
            }
        }

        bool modoVictoria = ctx.Energia >= UMBRAL_VICTORIA;
        double wEconomia = W_ECONOMIA * (modoVictoria ? FACTOR_ECO_VICTORIA : 1.0);
        double wPresion = W_PRESION * (modoVictoria ? FACTOR_PRESION_VICTORIA : 1.0);

        // Presión (solo unidades fuertes) y CASTIGO al picoteo suicida (unidad débil
        // adyacente a un cuartel enemigo, que solo va a morir contra el +40).
        double presion = 0.0, suicidioDebil = 0.0;
        foreach (var q in cuartelesEnemigos)
        {
            int mejor = int.MaxValue;
            foreach (var u in misUnidades)
            {
                int dd = Manhattan(u.coord, q, filas, columnas);
                if (u.f < UMBRAL_FUERZA_PRESION)
                {
                    if (dd <= ALCANCE_CONTESTACION) suicidioDebil += u.f + u.d;
                    continue;
                }
                if (dd < mejor) mejor = dd;
            }
            if (mejor != int.MaxValue) presion += Math.Max(0, RADIO_PRESION - mejor);
        }

        double amenazaCuartel = 0.0;
        if (miCuartel != null)
        {
            double amenaza = 0.0;
            foreach (var e in enemigos)
                if (Manhattan(e.coord, miCuartel, filas, columnas) <= ALCANCE_CONTESTACION)
                    amenaza += e.f;
            double defensa = defensaEnMiCuartel + BONO_CUARTEL;
            amenazaCuartel = amenaza > defensa ? amenaza - defensa : 0.0;
        }

        return W_MATERIAL * (ownMat - enemyMat)
             + wEconomia * economia
             + W_ACTIVIDAD * unidadesActivas
             + W_CENTRO * celdasCentro
             - W_CENTRO_ENEMIGO * centroEnemigo   // centro cedido al rival (v7)
             + wPresion * presion
             - W_SUICIDIO * suicidioDebil         // picoteo suicida (v7)
             - W_DEF_CUARTEL * amenazaCuartel;
    }

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
            if (mejorObj == "" || mejorDist == int.MaxValue || mejorDist > e.mov + ALCANCE_CONTESTACION)
            {
                res.Add(e);
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

    private static int Fuerza(Dictionary<string, object?> c) => M.Int(M.Get(c, "Fuerza", "fuerza"));
    private static int Defensa(Dictionary<string, object?> c) => M.Int(M.Get(c, "Defensa", "defensa"));
    private static int Mov(Dictionary<string, object?> c) => M.Int(M.Get(c, "Movimiento", "movimiento"));

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