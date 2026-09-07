using System;
using System.Collections.Generic;
using System.Linq;

using Tablero = System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<System.Collections.Generic.Dictionary<string, object?>>>;

// ─────────────────────────────────────────────────────────────────────────────
// ReglasEntrada.cs  —  FUENTE ÚNICA de "¿puede este grupo ENTRAR en esta celda?"
//
// Nace del caso 9UCNdoqXJ2mMaur6ssuF / turno 11: PlanificadorCaceria mandó una
// Manta escarlata (10/3) SOLA al cuartel humano F1 (vacío en ese momento). Una
// F10 no conquista un cuartel (hace falta F>40), un cuartel no farmea y el
// rival SIEMPRE puede desplegar en su cuartel (tenía 82 de energía): desplegó
// una Llama del Sheol y la Manta murió regalando 8 de energía y 3 PC. Ningún
// planificador comprobaba que el grupo que ENTRA ese turno gana; el
// movimiento greedy (TerrenoUtil.PasoHaciaTerreno) aterriza sobre la presa
// en cuanto está a tiro.
//
// Aquí vive, compartida por TODOS los planificadores y por el lookahead:
//   · Alcance BFS por terreno (réplica del cliente/servidor).
//   · Regla EXACTA de combate del servidor (WarZeroLogic.Combate.Resolver):
//       - cuartel sin defensor: se conquista si Σ Fuerza atacante > 40;
//         si no, la carta se queda dentro sin efecto (y se revela).
//       - con defensor: poderNeto = F − Σ D rivales (+40 al dueño del cuartel);
//         gana el neto ESTRICTAMENTE mayor; empate = standoff.
//   · Pesimismo REAL, no genérico:
//       - EVOLUCIÓN RIVAL PAGABLE: si una carta enemiga tiene evolución y su
//         dueño puede pagarla, cuenta con sus estadísticas evolucionadas
//         (9UCN T9: un Capitán 8/7 atacó una Bestia del abismo 10/5 y el
//         humano la evolucionó a Bestia del cielo 55/18 en ese mismo turno).
//       - REFUERZO DE CUARTEL: al entrar en un cuartel rival se asume que su
//         dueño despliega (hasta RefuerzoMaxEntrada de energía) desde la mano.
//   · Amenaza realista sobre una celda: el MAYOR stack que la alcanza más la
//     mitad del resto (convergencia parcial), en PODER (F+D), que es la vara
//     con la que el juego resuelve el combate.
//   · PasoSeguro: mejor celda alcanzable hacia un objetivo sin entrar donde se
//     pierde ni quedarse donde te barren.
//   · AvanceCohesionado: el grupo que alcanza el objetivo entra SOLO si gana
//     junto; el resto se acerca en bola. Nunca goteo de cartas sueltas.
//   · SanearPlan: red final. Cualquier plan (de cualquier planificador) pasa
//     por aquí antes de puntuarse: las cartas que ENTRAN en una celda donde el
//     grupo no gana se recolocan en la celda segura más cercana al objetivo.
// ─────────────────────────────────────────────────────────────────────────────
public static class ReglasEntrada
{
    /// = WarZeroLogic.Combate.DefensaObelisco.
    public const int BonoCuartel = 40;

    /// Evoluciones rivales que se asumen como mucho por rival y turno (techo
    /// realista; el propio bot tiene MaxEvolucionesPorTurno = 2).
    public const int MaxEvosPorRival = 2;

    /// Si la carta evolucionada no está en el catálogo cargado, se estima ×1,8.
    public const double FactorEvolucionDesconocida = 1.8;

    /// Energía rival que se asume DESPLEGABLE en su cuartel al decidir ENTRAR en
    /// él (aprox. "una carta que seguro puede soltar"). Tunable. Más alto = el
    /// bot exige más masa para asaltar; más bajo = más picoteo.
    public const int RefuerzoMaxEntrada = 40;

    /// Ídem para el LOOKAHEAD (mundo agresivo): el rival refuerza su cuartel con
    /// más energía porque ahí se pondera contra el valor de la conquista.
    public const int RefuerzoMaxLookahead = 60;

    /// Conversión energía → poder de las cartas BASE desplegables (Manta 10/3 por
    /// 8, Belial 20/10 por 20, Capitán de Dunan 22/12 por 30, generales 55/20
    /// por 50): ≈ 0,8-1,2 de fuerza y ≈ 0,35-0,5 de defensa por energía.
    public const double RefuerzoFuerzaPorEnergia = 0.8;
    public const double RefuerzoDefensaPorEnergia = 0.35;

    /// Por debajo de esta energía el rival no tiene ni para la carta más barata.
    public const int EnergiaMinimaRefuerzo = 5;

    public enum Veredicto
    {
        SinCombate,    // no hay nadie: entrar es libre
        Gana,          // el grupo gana el combate (o conquista el cuartel)
        Empata,        // standoff: nadie gana, todos se quedan
        Pierde,        // el grupo muere
        SinConquista,  // cuartel vacío pero Σ Fuerza ≤ 40: se entra sin efecto
    }

    // ─────────────────────────────────────────────────────────────────────────
    // CONTEXTO: foto del turno vista por el bot (tablero real o simulado).
    // ─────────────────────────────────────────────────────────────────────────
    public sealed class Contexto
    {
        public string BotUid { get; }
        public int Filas { get; }
        public int Columnas { get; }
        public Dictionary<string, string> Terreno { get; }
        public string? MiCuartel { get; }
        /// coord -> uid del dueño (solo cuarteles de jugadores VIVOS).
        public Dictionary<string, string> CuartelOwner { get; } = new();
        public HashSet<string> CuartelesEnemigos { get; } = new();
        /// coord -> cartas enemigas (cualquier dueño ≠ bot y vivo).
        public Dictionary<string, List<Dictionary<string, object?>>> EnemigosPorCelda { get; } = new();
        /// coord -> cartas propias (según el tablero del contexto).
        public Dictionary<string, List<Dictionary<string, object?>>> PropiasPorCelda { get; } = new();
        public Dictionary<string, int> EnergiaPublica { get; } = new();
        public Dictionary<string, int> ManoPublica { get; } = new();
        public Dictionary<string, Dictionary<string, object?>> Evoluciones { get; }
        public HashSet<string> IslaCentral { get; }
        public HashSet<string> Rayos { get; }
        public Dictionary<string, List<string>> Continentes { get; }

        private readonly Dictionary<(string, int, int), HashSet<string>> _alcance = new();
        private readonly Dictionary<string, List<(string uid, int f, int d)>> _gruposPesimista = new();
        private readonly Dictionary<string, int> _amenaza = new();

        internal Contexto(BotContext ctx, Tablero tablero, HashSet<string> eliminados)
        {
            BotUid = ctx.BotUid;
            Filas = ctx.Filas; Columnas = ctx.Columnas;
            Terreno = ctx.Terreno ?? new Dictionary<string, string>();
            Evoluciones = ctx.Evoluciones ?? new Dictionary<string, Dictionary<string, object?>>();
            IslaCentral = ctx.IslaCentral ?? new HashSet<string>();
            Rayos = ctx.Rayos ?? new HashSet<string>();
            Continentes = ctx.Continentes ?? new Dictionary<string, List<string>>();

            var obeliscos = M.Map(M.Get(ctx.Estado, "obeliscos"));
            string? mio = ctx.Cuartel != "" ? ctx.Cuartel : null;
            foreach (var (uid, cObj) in obeliscos)
            {
                var c = M.Str(cObj);
                if (c == "" || eliminados.Contains(uid)) continue;
                CuartelOwner[c] = uid;
                if (uid == BotUid) mio ??= c;
                else CuartelesEnemigos.Add(c);
            }
            MiCuartel = mio;

            var stats = M.Map(M.Get(ctx.Estado, "statsPartida"));
            foreach (var (uid, sObj) in stats)
            {
                var s = M.Map(sObj);
                EnergiaPublica[uid] = M.Int(M.Get(s, "energies"));
                ManoPublica[uid] = M.List(M.Get(s, "mano")).Count;
            }

            foreach (var (coord, cartas) in tablero)
                foreach (var c in cartas)
                {
                    var owner = M.Str(M.Get(c, "ownerUid"));
                    if (owner == BotUid)
                    {
                        if (!PropiasPorCelda.TryGetValue(coord, out var lp)) { lp = new(); PropiasPorCelda[coord] = lp; }
                        lp.Add(c);
                    }
                    else if (owner != "" && !eliminados.Contains(owner))
                    {
                        if (!EnemigosPorCelda.TryGetValue(coord, out var le)) { le = new(); EnemigosPorCelda[coord] = le; }
                        le.Add(c);
                    }
                }
        }

        /// Celdas alcanzables (BFS por terreno) desde `from`, con caché.
        public HashSet<string> Alcance(string from, int mov, int tipo)
        {
            var key = (from, mov, tipo);
            if (_alcance.TryGetValue(key, out var r)) return r;
            r = Alcanzables(from, mov, tipo, Terreno, Filas, Columnas);
            _alcance[key] = r;
            return r;
        }

        internal List<(string uid, int f, int d)> GruposPesimistaEn(string coord)
        {
            if (_gruposPesimista.TryGetValue(coord, out var g)) return g;
            g = EnemigosPorCelda.TryGetValue(coord, out var cartas)
                ? GruposPorDueno(this, cartas, pesimista: true)
                : new List<(string uid, int f, int d)>();
            _gruposPesimista[coord] = g;
            return g;
        }

        internal int AmenazaCache(string coord, Func<int> calc)
        {
            if (_amenaza.TryGetValue(coord, out var a)) return a;
            a = calc();
            _amenaza[coord] = a;
            return a;
        }
    }

    /// Contexto sobre el tablero REAL del estado de la partida.
    public static Contexto Crear(BotContext ctx)
    {
        var tablero = TableroDesde(ctx.Estado);
        var eliminados = M.List(M.Get(ctx.Estado, "jugadoresEliminados")).Select(M.Str).ToHashSet();
        return new Contexto(ctx, tablero, eliminados);
    }

    /// Contexto sobre un tablero SIMULADO (lookahead), con sus eliminados.
    public static Contexto Crear(BotContext ctx, Tablero tablero, HashSet<string>? eliminados)
    {
        var elim = new HashSet<string>(M.List(M.Get(ctx.Estado, "jugadoresEliminados")).Select(M.Str));
        if (eliminados != null) elim.UnionWith(eliminados);
        return new Contexto(ctx, tablero, elim);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // COMBATE
    // ─────────────────────────────────────────────────────────────────────────

    /// Grupos por dueño de una lista de cartas enemigas. Con `pesimista`, cada
    /// dueño evoluciona (hasta MaxEvosPorRival) las cartas con evolución que
    /// puede pagar con su energía PÚBLICA, mejores ganancias primero.
    public static List<(string uid, int f, int d)> GruposPorDueno(
        Contexto k, IEnumerable<Dictionary<string, object?>> cartas, bool pesimista)
    {
        var res = new Dictionary<string, (int f, int d)>();
        var porDueno = new Dictionary<string, List<Dictionary<string, object?>>>();
        foreach (var c in cartas)
        {
            var o = M.Str(M.Get(c, "ownerUid"));
            if (o == "") continue;
            if (!porDueno.TryGetValue(o, out var l)) { l = new(); porDueno[o] = l; }
            l.Add(c);
        }
        foreach (var (uid, lista) in porDueno)
        {
            int f = 0, d = 0;
            foreach (var c in lista) { f += Fuerza(c); d += Defensa(c); }
            if (pesimista)
            {
                var (extraF, extraD) = PotencialEvolucion(k, uid, lista);
                f += extraF; d += extraD;
            }
            res[uid] = (f, d);
        }
        return res.Select(kv => (kv.Key, kv.Value.f, kv.Value.d)).ToList();
    }

    /// Fuerza/defensa EXTRA que ganaría `uid` evolucionando (lo que pueda pagar)
    /// las cartas de `lista`. Usa las estadísticas reales de la carta
    /// evolucionada si está en `Evoluciones`; si no, estima ×1,8.
    public static (int f, int d) PotencialEvolucion(Contexto k, string uid, IEnumerable<Dictionary<string, object?>> lista)
    {
        int presupuesto = k.EnergiaPublica.GetValueOrDefault(uid, 0);
        if (presupuesto <= 0) return (0, 0);
        var cand = new List<(int coste, int gf, int gd)>();
        foreach (var c in lista)
        {
            var idEvo = M.Str(M.Get(c, "IdEvolucion", "idEvolucion"));
            int coste = M.Int(M.Get(c, "Evolucion", "evolucion"));
            if (idEvo == "" || coste <= 0) continue;
            int f = Fuerza(c), d = Defensa(c), gf, gd;
            if (k.Evoluciones.TryGetValue(idEvo, out var evo))
            {
                gf = Math.Max(0, Fuerza(evo) - f);
                gd = Math.Max(0, Defensa(evo) - d);
            }
            else
            {
                gf = (int)Math.Round(f * (FactorEvolucionDesconocida - 1.0));
                gd = (int)Math.Round(d * (FactorEvolucionDesconocida - 1.0));
            }
            if (gf + gd <= 0) continue;
            cand.Add((coste, gf, gd));
        }
        int tf = 0, td = 0, evos = 0;
        foreach (var (coste, gf, gd) in cand.OrderByDescending(x => x.gf + x.gd))
        {
            if (evos >= MaxEvosPorRival) break;
            if (coste > presupuesto) continue;
            presupuesto -= coste; tf += gf; td += gd; evos++;
        }
        return (tf, td);
    }

    /// Poder (F+D) pesimista de una lista de cartas enemigas (evolución pagable).
    public static int PoderPesimista(Contexto k, IEnumerable<Dictionary<string, object?>> cartas)
        => GruposPorDueno(k, cartas, pesimista: true).Sum(g => g.f + g.d);

    /// Refuerzo (F, D) que el dueño de un cuartel puede desplegar en él.
    public static (int f, int d) RefuerzoCuartel(Contexto k, string duenoUid, int topeEnergia)
    {
        int energia = k.EnergiaPublica.GetValueOrDefault(duenoUid, 0);
        int mano = k.ManoPublica.GetValueOrDefault(duenoUid, 0);
        if (mano <= 0 || energia < EnergiaMinimaRefuerzo) return (0, 0);
        int e = Math.Min(energia, topeEnergia);
        return ((int)Math.Round(e * RefuerzoFuerzaPorEnergia), (int)Math.Round(e * RefuerzoDefensaPorEnergia));
    }

    /// Veredicto de un grupo propio (sumF, sumD) que ENTRA en `coord` este turno.
    /// `contarRefuerzo`: si `coord` es un cuartel rival, añade el despliegue que
    /// su dueño puede hacer. `sesgo` (>0) hace al bot más valiente FUERA de los
    /// cuarteles y nunca contra un stack con evolución pagable (ahí la lectura
    /// es estricta: es exactamente la trampa observada).
    public static Veredicto Evaluar(Contexto k, string coord, int sumF, int sumD, bool contarRefuerzo, int sesgo = 0)
    {
        bool esCuartelEnemigo = k.CuartelesEnemigos.Contains(coord);
        string dueno = k.CuartelOwner.GetValueOrDefault(coord, "");

        var grupos = k.GruposPesimistaEn(coord).Select(g => (g.uid, g.f, g.d)).ToList();
        bool evolucionable = false;
        if (k.EnemigosPorCelda.TryGetValue(coord, out var cartasEne))
            evolucionable = cartasEne.Any(c =>
                M.Str(M.Get(c, "IdEvolucion", "idEvolucion")) != ""
                && M.Int(M.Get(c, "Evolucion", "evolucion")) > 0
                && M.Int(M.Get(c, "Evolucion", "evolucion")) <= k.EnergiaPublica.GetValueOrDefault(M.Str(M.Get(c, "ownerUid")), 0));

        // Cuartel rival VACÍO ahora mismo: si el dueño NO despliega, la regla es
        // Σ Fuerza > 40 o no hay conquista. Con F≤40 no hay conquista posible en
        // ningún mundo, aunque el grupo "ganara" al refuerzo estimado por defensa.
        if (esCuartelEnemigo && grupos.Count == 0 && sumF <= BonoCuartel) return Veredicto.SinConquista;

        if (esCuartelEnemigo && contarRefuerzo && dueno != "")
        {
            var (rf, rd) = RefuerzoCuartel(k, dueno, RefuerzoMaxEntrada);
            if (rf + rd > 0)
            {
                int idx = grupos.FindIndex(g => g.uid == dueno);
                if (idx >= 0) grupos[idx] = (dueno, grupos[idx].f + rf, grupos[idx].d + rd);
                else grupos.Add((dueno, rf, rd));
            }
        }

        if (grupos.Count == 0)
        {
            if (esCuartelEnemigo) return sumF > BonoCuartel ? Veredicto.Gana : Veredicto.SinConquista;
            return Veredicto.SinCombate;
        }

        // Bono del cuartel al dueño defendiendo (regla del servidor).
        if (esCuartelEnemigo && dueno != "")
            for (int i = 0; i < grupos.Count; i++)
                if (grupos[i].uid == dueno) grupos[i] = (dueno, grupos[i].f, grupos[i].d + BonoCuartel);

        if (esCuartelEnemigo || evolucionable) sesgo = 0;

        int miNeto = sumF - grupos.Sum(g => g.d);
        int mejorRival = int.MinValue;
        foreach (var g in grupos)
        {
            int neto = g.f - (sumD + grupos.Where(h => h.uid != g.uid).Sum(h => h.d));
            if (neto > mejorRival) mejorRival = neto;
        }
        if (miNeto + sesgo > mejorRival) return Veredicto.Gana;
        if (miNeto + sesgo == mejorRival) return Veredicto.Empata;
        return Veredicto.Pierde;
    }

    /// ¿Gana el grupo (o entra sin combate)? Atajo sin refuerzo de cuartel.
    public static bool GanaGrupo(Contexto k, string coord, int sumF, int sumD, int sesgo = 0)
    {
        var v = Evaluar(k, coord, sumF, sumD, contarRefuerzo: false, sesgo);
        return v is Veredicto.Gana or Veredicto.SinCombate;
    }

    /// POLÍTICA de entrada del bot: mi cuartel siempre; una celda enemiga solo si
    /// el grupo GANA (nunca empate: standoff inútil); un cuartel rival solo si
    /// lo CONQUISTA contando el refuerzo que su dueño puede desplegar.
    public static bool EntradaPermitida(Contexto k, string coord, int sumF, int sumD)
    {
        if (k.MiCuartel != null && coord == k.MiCuartel) return true;
        var v = Evaluar(k, coord, sumF, sumD, contarRefuerzo: k.CuartelesEnemigos.Contains(coord), 0);
        return v is Veredicto.Gana or Veredicto.SinCombate;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // AMENAZA
    // ─────────────────────────────────────────────────────────────────────────

    /// Poder (F+D, pesimista) que puede caer sobre `coord` el próximo turno: el
    /// MAYOR stack que la alcanza más la MITAD del resto. Excluye lo que ya está
    /// en la celda (el combate directo lo cubre Evaluar).
    public static int AmenazaSobre(Contexto k, string coord)
        => k.AmenazaCache(coord, () =>
        {
            int mayor = 0, resto = 0;
            foreach (var (ecoord, cartas) in k.EnemigosPorCelda)
            {
                if (ecoord == coord) continue;
                var llegan = cartas.Where(c => k.Alcance(ecoord, Mov(c), Tipo(c)).Contains(coord)).ToList();
                if (llegan.Count == 0) continue;
                int poder = PoderPesimista(k, llegan);
                if (poder > mayor) { resto += mayor; mayor = poder; }
                else resto += poder;
            }
            return mayor + resto / 2;
        });

    /// Mayor stack enemigo (poder pesimista) que alcanza `coord` el próximo turno.
    public static int MayorStackQueAlcanza(Contexto k, string coord)
    {
        int mayor = 0;
        foreach (var (ecoord, cartas) in k.EnemigosPorCelda)
        {
            if (ecoord == coord) continue;
            var llegan = cartas.Where(c => k.Alcance(ecoord, Mov(c), Tipo(c)).Contains(coord)).ToList();
            if (llegan.Count == 0) continue;
            mayor = Math.Max(mayor, PoderPesimista(k, llegan));
        }
        return mayor;
    }

    /// ¿Barren a un grupo de poder `poderPropio` que termine en `coord`?
    public static bool Expuesta(Contexto k, string coord, int poderPropio)
    {
        if (k.MiCuartel != null && coord == k.MiCuartel) return false;   // la defensa del cuartel va aparte
        return AmenazaSobre(k, coord) > poderPropio;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // MOVIMIENTO
    // ─────────────────────────────────────────────────────────────────────────

    /// Energía por turno que da una celda (regla del servidor: por carta).
    public static int ValorFarmeo(Contexto k, string coord)
    {
        if (k.CuartelOwner.ContainsKey(coord)) return 0;
        int v = 0;
        if (k.Rayos.Contains(coord)) v += 10;
        if (k.IslaCentral.Contains(coord)) v += 7;
        foreach (var (obelisco, celdas) in k.Continentes)
            if (celdas.Contains(coord))
            {
                var owner = k.CuartelOwner.GetValueOrDefault(obelisco, "");
                if (owner != "" && owner != k.BotUid) v += 5;
            }
        return v;
    }

    /// Mejor celda para `card` (en `desde`) hacia `objetivo`: alcanzable (o la
    /// propia), PERMITIDA con el grupo que ya haya allí (`extra`) y, si es
    /// posible, no expuesta; a igualdad, la más cercana al objetivo y la que
    /// más farmea. Quedarse siempre está permitido (no hay nada mejor).
    public static string PasoSeguro(
        Contexto k, string desde, Dictionary<string, object?> card, string? objetivo,
        Func<string, (int f, int d)>? extra = null)
    {
        int f = Fuerza(card), d = Defensa(card);
        var cand = new List<string>(k.Alcance(desde, Mov(card), Tipo(card))) { desde };
        string? mejor = null;
        (int, int, int, int) mejorKey = default;
        foreach (var c in cand)
        {
            var (ef, ed) = extra?.Invoke(c) ?? (0, 0);
            if (c != desde && !EntradaPermitida(k, c, f + ef, d + ed)) continue;
            int poderCelda = f + d + ef + ed + (k.MiCuartel == c ? BonoCuartel : 0);
            int expuesta = Expuesta(k, c, poderCelda) ? 1 : 0;
            int dist = objetivo == null ? 0 : Manhattan(c, objetivo);
            var key = (expuesta, dist, -ValorFarmeo(k, c), c == desde ? 0 : 1);
            if (mejor == null || key.CompareTo(mejorKey) < 0) { mejor = c; mejorKey = key; }
        }
        return mejor ?? desde;
    }

    /// Avance EN BOLA de `unidades` hacia `objetivo`. El subgrupo que ALCANZA el
    /// objetivo este turno entra SOLO si gana junto (con refuerzo si es un
    /// cuartel rival); si no, nadie entra y todos se acercan con PasoSeguro.
    /// Devuelve los destinos alineados con la lista de entrada.
    public static List<string> AvanceCohesionado(
        Contexto k, List<(string coord, Dictionary<string, object?> card)> unidades, string objetivo,
        Dictionary<string, (int f, int d)>? ocupacionInicial = null)
    {
        var dest = new string[unidades.Count];
        var sums = ocupacionInicial != null
            ? new Dictionary<string, (int f, int d)>(ocupacionInicial)
            : new Dictionary<string, (int f, int d)>();
        (int f, int d) Extra(string c) => sums.TryGetValue(c, out var s) ? s : (0, 0);
        void Suma(string c, int f, int d) { var s = Extra(c); sums[c] = (s.f + f, s.d + d); }

        var asalto = new List<int>();
        for (int i = 0; i < unidades.Count; i++)
        {
            var (coord, card) = unidades[i];
            if (coord == objetivo || k.Alcance(coord, Mov(card), Tipo(card)).Contains(objetivo)) asalto.Add(i);
        }
        bool entra = false;
        if (asalto.Count > 0)
        {
            int sf = Extra(objetivo).f + asalto.Sum(i => Fuerza(unidades[i].card));
            int sd = Extra(objetivo).d + asalto.Sum(i => Defensa(unidades[i].card));
            var v = Evaluar(k, objetivo, sf, sd, contarRefuerzo: k.CuartelesEnemigos.Contains(objetivo), 0);
            entra = v is Veredicto.Gana or Veredicto.SinCombate;
            // Sin combate (celda libre) también exige no quedar expuesto en bola.
            if (entra && v == Veredicto.SinCombate && Expuesta(k, objetivo, sf + sd)) entra = false;
        }
        var restantes = new List<int>();
        for (int i = 0; i < unidades.Count; i++)
        {
            if (entra && asalto.Contains(i))
            {
                dest[i] = objetivo;
                Suma(objetivo, Fuerza(unidades[i].card), Defensa(unidades[i].card));
            }
            else restantes.Add(i);
        }
        foreach (var i in restantes.OrderByDescending(i => Fuerza(unidades[i].card) + Defensa(unidades[i].card)))
        {
            var (coord, card) = unidades[i];
            string paso = PasoSeguro(k, coord, card, objetivo, Extra);
            dest[i] = paso;
            Suma(paso, Fuerza(card), Defensa(card));
        }
        return dest.ToList();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // SANEADO DE PLANES (red final)
    // ─────────────────────────────────────────────────────────────────────────

    /// Recoloca las cartas propias que ENTRAN en una celda donde el grupo no
    /// gana (o en un cuartel rival que no conquista). Las que ya estaban allí
    /// se quedan (salir es otra decisión). Devuelve el plan corregido y cuántas
    /// cartas se han recolocado.
    public static BotMove SanearPlan(BotContext ctx, BotMove plan, out int arreglos)
    {
        arreglos = 0;
        var k = Crear(ctx);
        string botUid = ctx.BotUid;

        // Origen de cada carta propia del tablero real.
        var origenPorInst = new Dictionary<string, string>();
        var tablero = TableroDesde(ctx.Estado);
        foreach (var (coord, cartas) in tablero)
            foreach (var c in cartas)
            {
                if (M.Str(M.Get(c, "ownerUid")) != botUid) continue;
                var iid = M.Str(M.Get(c, "instanceId"));
                if (iid != "") origenPorInst[iid] = coord;
            }
        var instEnPlan = plan.Celdas.Values.SelectMany(l => l)
            .Select(c => M.Str(M.Get(c, "instanceId"))).Where(s => s != "").ToHashSet();

        // Copia del plan (listas nuevas; las cartas se comparten).
        var celdas = new Tablero();
        foreach (var (coord, lista) in plan.Celdas) celdas[coord] = new List<Dictionary<string, object?>>(lista);

        string OrigenDe(string coord, Dictionary<string, object?> card)
        {
            var iid = M.Str(M.Get(card, "instanceId"));
            if (iid != "" && origenPorInst.TryGetValue(iid, out var o)) return o;
            // Evolución: había una carta propia en esta celda cuyo IdEvolucion es
            // esta carta y que ya no figura en el plan (la sustituye).
            var id = M.Str(M.Get(card, "id"));
            if (id != "" && tablero.TryGetValue(coord, out var antes) &&
                antes.Any(a => M.Str(M.Get(a, "ownerUid")) == botUid
                               && M.Str(M.Get(a, "IdEvolucion", "idEvolucion")) == id
                               && !instEnPlan.Contains(M.Str(M.Get(a, "instanceId")))))
                return coord;
            if (M.Int(M.Get(card, "Condicion", "condicion")) == 3) return coord;   // estática: se coloca, no se mueve
            return k.MiCuartel ?? coord;                                           // recién desplegada
        }

        var sums = new Dictionary<string, (int f, int d)>();
        foreach (var (coord, lista) in celdas)
        {
            int f = 0, d = 0;
            foreach (var c in lista) if (M.Str(M.Get(c, "ownerUid")) == botUid) { f += Fuerza(c); d += Defensa(c); }
            sums[coord] = (f, d);
        }
        (int f, int d) Extra(string c) => sums.TryGetValue(c, out var s) ? s : (0, 0);

        var prohibidas = celdas.Keys
            .Where(c => !(k.MiCuartel != null && c == k.MiCuartel))
            .Where(c => sums[c].f + sums[c].d > 0 && !EntradaPermitida(k, c, sums[c].f, sums[c].d))
            .ToList();

        foreach (var coord in prohibidas)
        {
            var entrantes = celdas[coord]
                .Where(c => M.Str(M.Get(c, "ownerUid")) == botUid && OrigenDe(coord, c) != coord)
                .OrderByDescending(c => Fuerza(c) + Defensa(c))
                .ToList();
            if (entrantes.Count == 0) continue;
            foreach (var card in entrantes)
            {
                string origen = OrigenDe(coord, card);
                celdas[coord].Remove(card);
                sums[coord] = (sums[coord].f - Fuerza(card), sums[coord].d - Defensa(card));

                // Celda segura más cercana al objetivo que quería (se queda en el cerco).
                string destino = PasoSeguro(k, origen, card, coord, Extra);
                if (destino == coord) destino = origen;   // por construcción no debería ocurrir
                if (!celdas.TryGetValue(destino, out var l)) { l = new(); celdas[destino] = l; }
                l.Add(card);
                sums[destino] = (Extra(destino).f + Fuerza(card), Extra(destino).d + Defensa(card));
                arreglos++;
                Console.WriteLine($"[WZ][reglas {botUid}] carta {M.Str(M.Get(card, "Nombre", "nombre"))} " +
                                  $"({Fuerza(card)}/{Defensa(card)}) NO entra en {coord}: recolocada en {destino}");
            }
            if (celdas[coord].Count == 0) celdas.Remove(coord);
        }

        if (arreglos == 0) return plan;
        return new BotMove
        {
            Celdas = celdas,
            Acciones = plan.Acciones,
            ManoResultante = plan.ManoResultante,
            EnergiaGastada = plan.EnergiaGastada,
            EnergiaAcciones = plan.EnergiaAcciones,
            EspecialComprada = plan.EspecialComprada,
        };
    }

    // ─────────────────────────────────────────────────────────────────────────
    // GEOMETRÍA / TERRENO (réplica exacta de cliente y servidor)
    // ─────────────────────────────────────────────────────────────────────────

    public static HashSet<string> Alcanzables(
        string from, int mov, int tipo, Dictionary<string, string> terreno, int filas, int columnas)
    {
        var res = new HashSet<string>();
        if (mov <= 0 || Parse(from) == null) return res;
        var visited = new Dictionary<string, int> { [from] = 0 };
        var queue = new Queue<(string, int)>();
        queue.Enqueue((from, 0));
        var deltas = new (int, int)[] { (-1, 0), (1, 0), (0, -1), (0, 1) };
        while (queue.Count > 0)
        {
            var (coord, steps) = queue.Dequeue();
            if (steps >= mov) continue;
            var pos = Parse(coord); if (pos == null) continue;
            var (ri, ci) = pos.Value;
            foreach (var (dr, dc) in deltas)
            {
                int nr = ri + dr, nc = ci + dc;
                if (nr < 0 || nr >= filas || nc < 0 || nc >= columnas) continue;
                var nCoord = Label(nr, nc);
                int ns = steps + 1;
                if (visited.GetValueOrDefault(nCoord, 999) <= ns) continue;
                if (!CanTraverse(nCoord, tipo, terreno)) continue;
                visited[nCoord] = ns;
                if (nCoord != from && CanLand(nCoord, tipo, terreno)) res.Add(nCoord);
                if (ns < mov) queue.Enqueue((nCoord, ns));
            }
        }
        return res;
    }

    /// ¿Existe un camino (en cualquier nº de turnos) de `from` a `to` para una
    /// carta de ese tipo? (Un naval no cruza tierra; un terrestre no cruza mar.)
    public static bool HayCamino(string from, string to, int tipo, Dictionary<string, string> terreno, int filas, int columnas)
    {
        if (from == to) return true;
        if (!CanLand(to, tipo, terreno)) return false;
        var visited = new HashSet<string> { from };
        var queue = new Queue<string>();
        queue.Enqueue(from);
        var deltas = new (int, int)[] { (-1, 0), (1, 0), (0, -1), (0, 1) };
        while (queue.Count > 0)
        {
            var coord = queue.Dequeue();
            var pos = Parse(coord); if (pos == null) continue;
            foreach (var (dr, dc) in deltas)
            {
                int nr = pos.Value.ri + dr, nc = pos.Value.ci + dc;
                if (nr < 0 || nr >= filas || nc < 0 || nc >= columnas) continue;
                var n = Label(nr, nc);
                if (visited.Contains(n) || !CanTraverse(n, tipo, terreno)) continue;
                if (n == to) return true;
                visited.Add(n);
                queue.Enqueue(n);
            }
        }
        return false;
    }

    public static string Terr(string coord, Dictionary<string, string> t) => t.TryGetValue(coord, out var v) ? v : "land";
    public static bool CanTraverse(string coord, int tipo, Dictionary<string, string> t) => tipo switch
    {
        1 => Terr(coord, t) is "land" or "amphibious",
        3 => Terr(coord, t) is "sea" or "deepSea" or "amphibious",
        _ => true,
    };
    public static bool CanLand(string coord, int tipo, Dictionary<string, string> t) => tipo switch
    {
        1 or 2 => Terr(coord, t) is "land" or "amphibious",
        3 => Terr(coord, t) is "sea" or "deepSea" or "amphibious",
        _ => true,
    };

    public static (int ri, int ci)? Parse(string coord)
    {
        if (string.IsNullOrEmpty(coord) || coord.Length < 2) return null;
        int ri = char.ToUpperInvariant(coord[0]) - 'A';
        if (!int.TryParse(coord[1..], out int col)) return null;
        return (ri, col - 1);
    }
    public static string Label(int ri, int ci) => $"{(char)('A' + ri)}{ci + 1}";
    public static int Manhattan(string a, string b)
    {
        var pa = Parse(a); var pb = Parse(b);
        if (pa == null || pb == null) return int.MaxValue / 4;
        return Math.Abs(pa.Value.ri - pb.Value.ri) + Math.Abs(pa.Value.ci - pb.Value.ci);
    }

    public static Tablero TableroDesde(Dictionary<string, object?> estado)
    {
        var t = new Tablero();
        foreach (var kv in M.Map(M.Get(estado, "tablero")))
            t[kv.Key] = M.List(kv.Value).Select(M.Map).ToList();
        return t;
    }

    // ── Stats de carta ──
    public static int Fuerza(Dictionary<string, object?> c) => M.Int(M.Get(c, "Fuerza", "fuerza"));
    public static int Defensa(Dictionary<string, object?> c) => M.Int(M.Get(c, "Defensa", "defensa"));
    public static int Coste(Dictionary<string, object?> c) => M.Int(M.Get(c, "Coste", "coste"));
    public static int Mov(Dictionary<string, object?> c) => M.Int(M.Get(c, "Movimiento", "movimiento"));
    public static int Tipo(Dictionary<string, object?> c) { int t = M.Int(M.Get(c, "Tipo", "tipo")); return t <= 0 ? 1 : t; }
}