using System;
using System.Collections.Generic;
using System.Linq;

using Tablero = System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<System.Collections.Generic.Dictionary<string, object?>>>;

// ─────────────────────────────────────────────────────────────────────────────
// PlanificadorDefensivo.cs  —  MODO DEFENSA (planificador)  v7 "anti-misil"
//
// Candidatos de la softmax para cuando el CUARTEL está amenazado (un stack rival
// puede ENTRAR este turno o está APARCADO a un turno de distancia). Devuelve una
// lista vacía si no hay amenaza. El lookahead elige el plan solo si defender
// supera a farmear/atacar.
//
// ── Regla de combate (WarZeroLogic.Combate) ──────────────────────────────────
//   · Cuartel SIN defensor: cae si Σ Fuerza atacante > 40 (DefensaObelisco).
//   · Cuartel CON defensor: el dueño AGUANTA si F_g + D_g + 40 > F_a + D_a.
//   · Un DISPARO destruye todas las cartas de una celda y se resuelve DESPUÉS
//     del movimiento: solo acierta sobre lo que no se mueve.
//   · La DESCARGA (20, una por partida) mata todo lo que haya en el cuartel
//     propio antes del combate y deja su defensa a 0 (recupera +10/turno).
//
// ── Lo que enseñan las partidas de estudio (iPKy, reto resistencia demoníaca) ──
//   El patrón humano contra el bot es siempre el mismo: aparcar un stack a
//   mov+1 del cuartel, DISPARAR al cuartel (la guarnición es la única celda
//   cuyo contenido no se mueve → el misil siempre acierta) y ENTRAR al turno
//   siguiente sobre el cuartel vacío. El bot respondía metiendo tropas en el
//   cuartel (v6: "REFUERZO"; Estratega: "DEFENSA PROPORCIONAL"), es decir,
//   fabricando el cebo: 6 cartas (Noavelya, General Albariel…) muertas por un
//   solo disparo en el reto T25.
//
// ── Palancas v7 ──────────────────────────────────────────────────────────────
//   A. GUARNICIÓN "NO RENTABLE DE DISPARAR": en el cuartel nunca más de
//      UMBRAL_COSTE_GUARNICION de coste (55) ni MAX_CARTAS_GUARNICION piezas,
//      elegidas por perfil defensivo (poder por coste, lentas, nunca generales).
//      Solo se apila POR ENCIMA del umbral cuando el bot ha decidido que el
//      rival VA A ENTRAR este turno (variante "trampa", ver F).
//   B. DEFENSA EN ANILLO: el resto del material espera FUERA, en grupos de
//      ≤ UMBRAL de coste, en las celdas de acceso al cuartel (a ≤ RADIO_ANILLO),
//      desde las que puede volver a casa el turno que viene. Un misil solo se
//      lleva un grupo; el cuartel se recubre igual.
//   C. CAZA: si un grupo propio que alcanza la celda del stack principal le
//      GANA (ReglasEntrada, evolución rival pagable), va a por él. Si el rival
//      se queda, muere; si entra en el cuartel, la descarga/trampa lo espera.
//   D. MISIL PREDICTIVO: las acciones se resuelven TRAS el movimiento, así que
//      el disparo propio va a donde el stack VA A ESTAR: a su celda si puede
//      entrar ya, está aparcado (turnosEnCelda ≥ 2) o paralizado (si no entra
//      es que se queda a disparar), y si aún no llega, a la CELDA DE
//      APROXIMACIÓN (la que pisaría para entrar al turno siguiente) mientras
//      la parálisis cubre la otra salida (esperar). NUNCA a la celda a la que
//      va la caza (mataría a las dos). Solo si la presa vale ≥
//      UMBRAL_COSTE_PRESA_MISIL.
//   E. DESCARGA COMBINADA (variante "descarga", solo con stack que puede entrar
//      YA y guarnición que no aguanta): el cuartel se VACÍA (nada propio muere),
//      se arma la descarga y el resto caza/anilla: si entra, muere; si no entra,
//      la caza le golpea donde está o el misil donde iba a moverse. Nunca se
//      arma si el cuartel quedaría "vendido" (nadie puede recubrirlo al turno
//      siguiente ni hay reserva de mano para hacerlo).
//   F. TRAMPA DE MANO (variante "trampa", solo con stack que puede entrar YA):
//      desplegar desde la mano, EN el cuartel y en el mismo turno de la entrada,
//      lo justo para aguantar (F_g + D_g + defensa > F_a + D_a). La mano es
//      invisible: el rival entra contra 1 carta y se encuentra 3. Gana su coste.
//   G. RESERVA DE CONTRA-ENTRADA: con un stack aparcado (no entra aún), el bot
//      no gasta en despliegues la energía de su mejor defensor de mano: es la
//      trampa del turno que viene. Las acciones (misil/parálisis) sí la usan.
//   H. ESCUDO al grupo de anillo más valioso (nunca al cuartel: regla del juego)
//      cuando un rival puede pagar un misil.
//
// El lookahead (LookaheadDosPlies v2) puntúa cada variante contra tres mundos
// (pasivo, agresivo y "misil"), así que la elección descarga/trampa/base es
// emergente de la simulación, no una regla que haya que acertar.
// ─────────────────────────────────────────────────────────────────────────────
public static class PlanificadorDefensivo
{
    private const int ALCANCE = 1;                    // "aparcado" = a ≤ mov+ALCANCE del cuartel
    private const int UMBRAL_COSTE_GUARNICION = AccionesTacticas.UmbralCosteCebo;   // 55
    private const int MAX_CARTAS_GUARNICION = 2;
    private const int RADIO_ANILLO = 2;               // celdas de anillo a ≤ 2 del cuartel
    private const int UMBRAL_COSTE_GRUPO_ANILLO = AccionesTacticas.UmbralCosteCebo;  // por celda
    private const int UMBRAL_COSTE_PRESA_MISIL = 55;  // no gastar un misil en menos
    private const int UMBRAL_COSTE_ESCUDO = 40;       // grupo que merece escudo
    private const int MAX_DESPLIEGUE_DEFENSA = 2;     // despliegues por turno en modo defensa
    private const int MAX_EVO_DEFENSA = 2;
    private const int COND_GENERAL = 5;

    // ── Modelo interno ──
    private sealed class Unidad
    {
        public string Coord = "";
        public Dictionary<string, object?> Card = new();
        public string Inst = "";
        public int F, D, Mov, Tipo, Coste;
        public bool Nueva;                 // desplegada este turno (no evoluciona)
        public int Poder => F + D;
        public bool General => M.Int(M.Get(Card, "Condicion", "condicion")) == COND_GENERAL;
    }

    private sealed class Stack
    {
        public string Coord = "";
        public List<Dictionary<string, object?>> Cartas = new();
        public int F, D, Coste, MovMax, TipoRep, TurnosEnCelda;
        public int PoderPesimista;
        public bool EntraAhora;            // alguna carta alcanza el cuartel este turno
        public int EntranteF, EntranteD;   // poder pesimista de las que alcanzan
        public bool Aparcado;              // no entra aún, pero está a ≤ mov+1
        public bool Dentro;                // ya está DENTRO de mi cuartel
        public string Dueno = "";
        public bool Paralizado;
        public HashSet<string> Alcance = new();   // celdas que puede pisar este turno
        public int Poder => F + D;
    }

    private sealed class Escenario
    {
        public BotContext Ctx = null!;
        public ReglasEntrada.Contexto Reglas = null!;
        public string Cuartel = "";
        public string Zona = "";
        public Tablero Tablero = new();
        public List<Unidad> Unidades = new();
        public List<Stack> Amenazas = new();
        public Stack Principal = null!;
        public int AmenazaAhora;           // poder que puede caer sobre el cuartel este turno
        public int DefensaCuartel;         // 40, o menos si hay descarga reciente
        public bool DescargaUsada;
        public int CosteMisilRival;
        public bool RivalConMisil;         // el dueño del principal puede pagar un disparo
        public List<string> CeldasAnillo = new();
        public Dictionary<string, int> PesoAcceso = new();
        public HashSet<string> CuartelesEnemigos = new();
        public HashSet<string> Cuarteles = new();
    }

    private sealed class Variante
    {
        public string Modo = "defensa";
        public bool Descarga;
        public bool Trampa;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // API
    // ─────────────────────────────────────────────────────────────────────────

    /// Compatibilidad: el primer candidato (variante base) o null sin amenaza.
    public static BotMove? Generar(BotContext ctx)
        => GenerarCandidatos(ctx).Select(c => c.plan).FirstOrDefault();

    /// Candidatos defensivos para la softmax: (plan, modo). Vacío si el cuartel
    /// no está amenazado. Modos: "defensa", "defensa+descarga", "defensa+trampa".
    public static List<(BotMove plan, string modo)> GenerarCandidatos(BotContext ctx)
    {
        var res = new List<(BotMove plan, string modo)>();
        var e = Analizar(ctx);
        if (e == null) return res;

        var baseVar = new Variante { Modo = "defensa" };
        var planBase = Construir(e, baseVar);
        if (planBase != null) res.Add((planBase, baseVar.Modo));

        if (e.Principal.EntraAhora || e.Principal.Dentro)
        {
            // La guarnición base, ¿aguanta la entrada? Si aguanta, el rival que
            // entre muere contra ella y no hace falta ni descarga ni trampa.
            bool baseAguanta = planBase != null && Aguanta(e, planBase);

            if (!baseAguanta && !e.DescargaUsada && e.Ctx.Energia >= AccionesTacticas.CosteDescarga)
            {
                var v = new Variante { Modo = "defensa+descarga", Descarga = true };
                var p = Construir(e, v);
                if (p != null) res.Add((p, v.Modo));
            }
            if (!baseAguanta)
            {
                var v = new Variante { Modo = "defensa+trampa", Trampa = true };
                var p = Construir(e, v);
                if (p != null && Aguanta(e, p)) res.Add((p, v.Modo));
            }
        }
        return res;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ANÁLISIS de la amenaza
    // ─────────────────────────────────────────────────────────────────────────
    private static Escenario? Analizar(BotContext ctx)
    {
        string miCuartel = ctx.Cuartel;
        if (miCuartel == "") return null;
        var estado = ctx.Estado;
        var reglas = ReglasEntrada.Crear(ctx);
        var tablero = ReglasEntrada.TableroDesde(estado);
        int turno = ctx.Turno;

        var e = new Escenario
        {
            Ctx = ctx,
            Reglas = reglas,
            Cuartel = miCuartel,
            Tablero = tablero,
            DescargaUsada = AccionesTacticas.DescargaUsada(estado, ctx.BotUid),
            DefensaCuartel = AccionesTacticas.DefensaCuartelActual(estado, miCuartel, turno),
            CosteMisilRival = AccionesTacticas.CosteDisparoEstimado(ctx),
            CuartelesEnemigos = new HashSet<string>(reglas.CuartelesEnemigos),
            Cuarteles = new HashSet<string>(reglas.CuartelOwner.Keys),
        };
        e.Zona = ctx.Zona;

        // ── Mis unidades ──
        foreach (var (coord, cartas) in tablero)
            foreach (var c in cartas)
            {
                if (M.Str(M.Get(c, "ownerUid")) != ctx.BotUid) continue;
                e.Unidades.Add(new Unidad
                {
                    Coord = coord,
                    Card = c,
                    Inst = M.Str(M.Get(c, "instanceId")),
                    F = Fuerza(c),
                    D = Defensa(c),
                    Mov = Mov(c),
                    Tipo = Tipo(c),
                    Coste = Coste(c),
                });
                if (e.Zona == "") e.Zona = M.Str(M.Get(c, "ownerZone"));
            }

        // ── Stacks enemigos que amenazan el cuartel (entran ya / aparcados / dentro) ──
        foreach (var (coord, cartas) in reglas.EnemigosPorCelda)
        {
            if (cartas.Count == 0) continue;
            var s = new Stack { Coord = coord, Cartas = cartas };
            int fMax = -1;
            foreach (var c in cartas)
            {
                int f = Fuerza(c);
                s.F += f; s.D += Defensa(c); s.Coste += Coste(c);
                s.MovMax = Math.Max(s.MovMax, Mov(c));
                if (f > fMax) { fMax = f; s.TipoRep = Tipo(c); s.Dueno = M.Str(M.Get(c, "ownerUid")); }
                if (CartaHelper.EstaParalizada(c)) s.Paralizado = true;
                foreach (var a in reglas.Alcance(coord, Mov(c), Tipo(c))) s.Alcance.Add(a);
            }
            s.Alcance.Add(coord);
            s.TurnosEnCelda = cartas.Min(AccionesTacticas.TurnosEnCelda);
            s.PoderPesimista = ReglasEntrada.PoderPesimista(reglas, cartas);
            s.Dentro = coord == miCuartel;

            var entrantes = s.Dentro
                ? cartas
                : cartas.Where(c => !CartaHelper.EstaParalizada(c)
                                    && reglas.Alcance(coord, Mov(c), Tipo(c)).Contains(miCuartel)).ToList();
            if (entrantes.Count > 0)
            {
                s.EntraAhora = true;
                foreach (var g in ReglasEntrada.GruposPorDueno(reglas, entrantes, pesimista: true))
                { s.EntranteF += g.f; s.EntranteD += g.d; }
            }
            int dist = ReglasEntrada.Manhattan(coord, miCuartel);
            s.Aparcado = !s.EntraAhora && !s.Paralizado && s.MovMax > 0 && dist <= s.MovMax + ALCANCE;

            if (s.EntraAhora || s.Aparcado) e.Amenazas.Add(s);
        }
        if (e.Amenazas.Count == 0) return null;

        // Sin fuerza para conquistar ni siquiera un cuartel vacío (F ≤ 40), no hay
        // amenaza real: el planificador de defensa no compite.
        int poderMax = e.Amenazas.Max(s => s.PoderPesimista);
        if (poderMax <= Combate.DefensaObelisco) return null;

        // Principal: el que puede entrar ya (o ya está dentro) con más poder; si
        // ninguno entra aún, el aparcado más fuerte.
        e.Principal = e.Amenazas
            .OrderByDescending(s => s.Dentro ? 2 : (s.EntraAhora ? 1 : 0))
            .ThenByDescending(s => s.PoderPesimista)
            .First();

        // Poder que puede caer sobre el cuartel ESTE turno: mayor entrante + la
        // mitad del resto (convergencia parcial, misma vara que ReglasEntrada).
        var entrantesPoder = e.Amenazas.Where(s => s.EntraAhora || s.Dentro)
            .Select(s => s.EntranteF + s.EntranteD).OrderByDescending(p => p).ToList();
        if (entrantesPoder.Count > 0)
            e.AmenazaAhora = entrantesPoder[0] + entrantesPoder.Skip(1).Sum() / 2;

        e.RivalConMisil = e.Principal.Dueno != "" &&
            AccionesTacticas.RivalPuedeDisparar(estado, e.Principal.Dueno, e.CosteMisilRival);

        // ── Celdas de anillo: a ≤ RADIO del cuartel, sin cuarteles ni enemigos ──
        foreach (var v in CeldasCerca(miCuartel, RADIO_ANILLO, ctx.Filas, ctx.Columnas))
        {
            if (e.Cuarteles.Contains(v)) continue;
            if (reglas.EnemigosPorCelda.ContainsKey(v)) continue;
            e.CeldasAnillo.Add(v);
            int peso = 0;
            foreach (var s in e.Amenazas) if (s.Alcance.Contains(v)) peso += s.F;
            e.PesoAcceso[v] = peso;
        }
        e.CeldasAnillo = e.CeldasAnillo
            .OrderBy(v => ReglasEntrada.Manhattan(v, miCuartel))
            .ThenByDescending(v => e.PesoAcceso[v])
            .ThenByDescending(v => ReglasEntrada.ValorFarmeo(reglas, v))
            .ToList();

        Console.WriteLine($"[WZ][defensa {ctx.BotUid}] amenaza: {e.Amenazas.Count} stack(s); principal {e.Principal.Coord} " +
            $"poder {e.Principal.PoderPesimista} coste {e.Principal.Coste} " +
            $"({(e.Principal.Dentro ? "DENTRO" : e.Principal.EntraAhora ? "entra ya" : "aparcado")}, {e.Principal.TurnosEnCelda} turnos en celda); " +
            $"cae este turno {e.AmenazaAhora}; defensa cuartel {e.DefensaCuartel}; rival con misil {e.RivalConMisil}");
        return e;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // CONSTRUCCIÓN del plan de una variante
    // ─────────────────────────────────────────────────────────────────────────
    private static BotMove? Construir(Escenario e, Variante v)
    {
        var ctx = e.Ctx; var reglas = e.Reglas; var terreno = ctx.Terreno;
        string miCuartel = e.Cuartel, botUid = ctx.BotUid;
        int filas = ctx.Filas, columnas = ctx.Columnas;
        int energia = ctx.Energia, gastado = 0, gastadoAcciones = 0;
        var mano = new List<string>(ctx.Mano);
        var acciones = new List<Dictionary<string, object?>>();
        var destino = new Dictionary<Unidad, string>();
        var unidades = e.Unidades.Select(u => u).ToList();   // copia (se añaden despliegues)

        // Coste ya comprometido en una celda (cebo) por unidades PROPIAS.
        var costeCelda = new Dictionary<string, int>();
        var poderCelda = new Dictionary<string, int>();
        void Reservar(Unidad u, string c)
        {
            destino[u] = c;
            costeCelda[c] = costeCelda.GetValueOrDefault(c) + u.Coste;
            poderCelda[c] = poderCelda.GetValueOrDefault(c) + u.Poder;
        }

        // ── 0) Presupuesto de acciones y descarga (van antes que los despliegues) ──
        if (v.Descarga)
        {
            acciones.Add(AccionesTacticas.CrearDescarga(botUid, e.Zona, miCuartel, ctx.Turno));
            energia -= AccionesTacticas.CosteDescarga; gastadoAcciones += AccionesTacticas.CosteDescarga;
        }

        // ── 1) GUARNICIÓN acotada (A). Con descarga, NADIE se queda dentro. ──
        var guarnicion = new List<Unidad>();
        if (!v.Descarga)
        {
            int costeG = 0;
            foreach (var u in unidades
                         .Where(u => u.Mov > 0)
                         .Where(u => u.Coord == miCuartel || reglas.Alcance(u.Coord, u.Mov, u.Tipo).Contains(miCuartel))
                         .OrderByDescending(u => PerfilGuarnicion(u, miCuartel)))
            {
                if (guarnicion.Count >= MAX_CARTAS_GUARNICION) break;
                if (costeG + u.Coste > UMBRAL_COSTE_GUARNICION) continue;
                guarnicion.Add(u); costeG += u.Coste;
            }
            foreach (var g in guarnicion) Reservar(g, miCuartel);
        }

        // ── 2) CAZA del stack principal (C): grupo mínimo que le gana en su celda ──
        var caza = new List<Unidad>();
        if (!e.Principal.Dentro)
        {
            int sf = 0, sd = 0;
            foreach (var u in unidades
                         .Where(u => u.Mov > 0 && !destino.ContainsKey(u))
                         .Where(u => reglas.Alcance(u.Coord, u.Mov, u.Tipo).Contains(e.Principal.Coord))
                         .OrderByDescending(u => u.Poder))
            {
                caza.Add(u); sf += u.F; sd += u.D;
                if (ReglasEntrada.GanaGrupo(reglas, e.Principal.Coord, sf, sd)) break;
            }
            if (caza.Count == 0 || !ReglasEntrada.GanaGrupo(reglas, e.Principal.Coord, sf, sd)) caza.Clear();
            foreach (var u in caza) Reservar(u, e.Principal.Coord);
        }

        // ── 3) TRAMPA (F): con entrada inminente, lo que haga falta DENTRO ──
        //      Primero material del tablero que llega (gratis), luego mano.
        if (v.Trampa)
        {
            foreach (var u in unidades
                         .Where(u => u.Mov > 0 && !destino.ContainsKey(u))
                         .Where(u => u.Coord == miCuartel || reglas.Alcance(u.Coord, u.Mov, u.Tipo).Contains(miCuartel))
                         .OrderByDescending(u => u.Poder))
            {
                if (AguantaCon(e, guarnicion)) break;
                guarnicion.Add(u); Reservar(u, miCuartel);
            }
            int nDesp = 0;
            foreach (var id in CartasDesplegables(ctx, mano)
                         .OrderByDescending(id => Fuerza(ctx.CatalogoMano[id]) + Defensa(ctx.CatalogoMano[id])))
            {
                if (AguantaCon(e, guarnicion)) break;
                if (nDesp >= MAX_DESPLIEGUE_DEFENSA + 1) break;
                var b = ctx.CatalogoMano[id];
                int coste = Coste(b);
                if (coste > energia) continue;
                if (!ReglasEntrada.CanLand(miCuartel, Tipo(b), terreno)) continue;
                var nu = NuevaUnidadDesde(b, id, botUid, e.Zona, miCuartel);
                unidades.Add(nu); guarnicion.Add(nu); Reservar(nu, miCuartel);
                energia -= coste; gastado += coste; nDesp++;
                mano.Remove(id);
            }
            if (!AguantaCon(e, guarnicion)) return null;   // la trampa no aguanta: variante sin sentido
        }

        // ── 4) ANILLO (B): el resto, en grupos ≤ umbral en las celdas de acceso ──
        var ocupantesAnillo = new Dictionary<string, List<Unidad>>();
        foreach (var u in unidades.Where(u => !destino.ContainsKey(u)).OrderByDescending(u => u.Poder))
        {
            if (u.Mov <= 0) { destino[u] = u.Coord; continue; }   // estática: no se mueve
            string c = ElegirCeldaAnillo(e, u, costeCelda, poderCelda, reglas);
            Reservar(u, c);
            if (!ocupantesAnillo.TryGetValue(c, out var l)) { l = new(); ocupantesAnillo[c] = l; }
            l.Add(u);
        }

        // ── 5) DESPLIEGUE de refuerzo (solo si el material defensivo no cubre la
        //      amenaza) respetando la RESERVA DE CONTRA-ENTRADA (G). ──
        int reservaContraEntrada = 0;
        if (!v.Trampa && !e.Principal.EntraAhora && !e.Principal.Dentro)
        {
            // El mejor defensor de la mano se guarda para el turno de la entrada.
            var mejor = CartasDesplegables(ctx, mano)
                .Select(id => ctx.CatalogoMano[id])
                .OrderByDescending(b => Fuerza(b) + Defensa(b))
                .FirstOrDefault();
            if (mejor != null) reservaContraEntrada = Math.Min(Coste(mejor), energia);
        }
        int poderDefensivo = guarnicion.Sum(u => u.Poder) + e.DefensaCuartel
                             + caza.Sum(u => u.Poder) + ocupantesAnillo.Values.Sum(l => l.Sum(u => u.Poder));
        int amenazaRef = Math.Max(e.AmenazaAhora, e.Principal.PoderPesimista);
        if (!v.Trampa && poderDefensivo < amenazaRef)
        {
            int nDesp = 0;
            foreach (var id in CartasDesplegables(ctx, mano)
                         .OrderByDescending(id => (Fuerza(ctx.CatalogoMano[id]) + Defensa(ctx.CatalogoMano[id])) / (double)Math.Max(1, Coste(ctx.CatalogoMano[id]))))
            {
                if (nDesp >= MAX_DESPLIEGUE_DEFENSA || poderDefensivo >= amenazaRef) break;
                var b = ctx.CatalogoMano[id];
                int coste = Coste(b);
                if (energia - coste < reservaContraEntrada) continue;
                if (!ReglasEntrada.CanLand(miCuartel, Tipo(b), terreno)) continue;
                var nu = NuevaUnidadDesde(b, id, botUid, e.Zona, miCuartel);
                unidades.Add(nu);
                // Si cabe en la guarnición (umbral), se queda; si no, sale al anillo
                // en el mismo turno (una carta desplegada puede moverse).
                bool cabe = !v.Descarga && guarnicion.Count < MAX_CARTAS_GUARNICION
                            && costeCelda.GetValueOrDefault(miCuartel) + nu.Coste <= UMBRAL_COSTE_GUARNICION;
                if (cabe) { guarnicion.Add(nu); Reservar(nu, miCuartel); }
                else
                {
                    string c = ElegirCeldaAnillo(e, nu, costeCelda, poderCelda, reglas);
                    if (c == miCuartel) { if (v.Descarga) continue; guarnicion.Add(nu); }
                    Reservar(nu, c);
                }
                energia -= coste; gastado += coste; nDesp++;
                poderDefensivo += nu.Poder;
                mano.Remove(id);
            }
        }

        // Con descarga el cuartel debe quedar VACÍO de cartas propias: si alguna
        // no ha podido salir (sin celda de anillo), la variante no es válida.
        if (v.Descarga && destino.Any(kv => kv.Value == miCuartel)) return null;
        // Y no puede quedar "vendido": alguien tiene que poder recubrirlo el
        // turno que viene, o haber mano+energía para desplegar entonces.
        if (v.Descarga)
        {
            bool recubre = destino.Any(kv => kv.Key.Mov > 0 && kv.Value != miCuartel
                                             && ReglasEntrada.Manhattan(kv.Value, miCuartel) <= kv.Key.Mov);
            bool reservaMano = CartasDesplegables(ctx, mano).Any(id => Coste(ctx.CatalogoMano[id]) <= energia);
            if (!recubre && !reservaMano) return null;
        }

        // ── 6) EVOLUCIONES de lo que se queda quieto (guarnición/anillo), sin
        //      romper el umbral de cebo de su celda ni la reserva. ──
        var evolucionadas = new Dictionary<Unidad, Dictionary<string, object?>>();
        int evos = 0;
        foreach (var u in unidades.OrderByDescending(u => u.Poder))
        {
            if (evos >= MAX_EVO_DEFENSA) break;
            if (u.Nueva || u.Mov <= 0 || !destino.TryGetValue(u, out var d) || d != u.Coord) continue;
            string idEvo = M.Str(M.Get(u.Card, "IdEvolucion", "idEvolucion"));
            int costeEvo = M.Int(M.Get(u.Card, "Evolucion", "evolucion"));
            if (idEvo == "" || costeEvo <= 0 || energia - costeEvo < reservaContraEntrada) continue;
            if (!ctx.Evoluciones.TryGetValue(idEvo, out var evoCard)) continue;
            if (!ReglasEntrada.CanLand(u.Coord, Tipo(evoCard), terreno)) continue;
            if (Fuerza(evoCard) + Defensa(evoCard) <= u.Poder) continue;
            int costeNuevo = Coste(evoCard);
            bool celdaCebo = u.Coord == miCuartel || e.CeldasAnillo.Contains(u.Coord);
            if (celdaCebo && costeCelda.GetValueOrDefault(u.Coord) - u.Coste + costeNuevo > UMBRAL_COSTE_GRUPO_ANILLO) continue;
            string zonaU = M.Str(M.Get(u.Card, "ownerZone")); if (zonaU == "") zonaU = e.Zona;
            evolucionadas[u] = NuevaUnidad(evoCard, idEvo, botUid, zonaU);
            costeCelda[u.Coord] = costeCelda.GetValueOrDefault(u.Coord) - u.Coste + costeNuevo;
            energia -= costeEvo; gastado += costeEvo; evos++;
        }

        // ── 7) ACCIONES: parálisis + veneno sobre el principal, MISIL predictivo
        //      (D), escudo al grupo de anillo más valioso (H). ──
        //
        // ORDEN REAL DEL SERVIDOR: movimiento → acciones (parálisis, veneno,
        // disparo) → combate → descarga. Es decir, TODA acción de este turno cae
        // sobre la celda tal y como queda TRAS el movimiento del rival: una
        // parálisis lanzada hoy no le impide moverse hoy (solo mañana) y un
        // disparo a "donde está" solo acierta si se queda. Por eso todas las
        // acciones apuntan a donde el stack VA A ESTAR:
        //   · Puede ENTRAR ya, o está aparcado (≥ 2 turnos) o paralizado → se
        //     queda (el patrón humano es "disparo al cuartel y entro mañana",
        //     desde donde está). Disparo a su celda; si el disparo se paga, la
        //     parálisis/veneno ahí mismo sobran (el disparo lo mata todo).
        //   · Si aún no llega → o espera o se ACERCA: parálisis/veneno a su
        //     celda (si espera, queda congelado 3 turnos) y disparo a la CELDA
        //     DE APROXIMACIÓN (si se acerca, muere). Dos salidas cubiertas.
        //   · Nunca a la celda a la que va la caza propia.
        var fuentes = new List<AccionesTacticas.Fuente>();
        foreach (var id in mano)
            if (ctx.CatalogoMano.TryGetValue(id, out var baseCard) && AccionesTacticas.EsCartaAccion(baseCard))
                fuentes.Add(new AccionesTacticas.Fuente(
                    miCuartel, M.Int(M.Get(baseCard, "IdHabilidad", "idHabilidad")), Coste(baseCard), id));
        foreach (var u in unidades)
        {
            if (u.Nueva || evolucionadas.ContainsKey(u)) continue;
            if (!destino.TryGetValue(u, out var d) || d != u.Coord) continue;   // solo las que no se mueven
            int habId = M.Int(M.Get(u.Card, "IdHabilidad", "idHabilidad"));
            if (habId <= 0 || EnEnfriamiento(u.Card, ctx.Turno)) continue;
            fuentes.Add(new AccionesTacticas.Fuente(
                u.Coord, habId, M.Int(M.Get(u.Card, "CosteHabilidad", "costeHabilidad")), null));
        }

        var celdasPropiasPlan = new HashSet<string>(destino.Values);
        if (!e.Principal.Dentro)
        {
            var s = e.Principal;
            string? objetivoMisil = s.Coste >= UMBRAL_COSTE_PRESA_MISIL
                ? ObjetivoMisil(e, caza.Count > 0, celdasPropiasPlan) : null;
            bool disparoACeldaActual = objetivoMisil == s.Coord;
            bool disparoLanzado = false;

            void Lanzar(IEnumerable<AccionesTacticas.Efecto> efectos, string? objetivoDisparo)
            {
                foreach (var (accion, coste, cartaId) in AccionesTacticas.ElegirAccionesDefensivas(
                             s.Coord, fuentes, energia, e.CuartelesEnemigos,
                             botUid, e.Zona, ctx.Turno, filas, columnas, efectos, objetivoDisparo))
                {
                    acciones.Add(accion);
                    energia -= coste; gastadoAcciones += coste;
                    if (cartaId != null) { mano.Remove(cartaId); fuentes.RemoveAll(f => f.CartaId == cartaId); }
                    if (M.Int(M.Get(accion, "habilidadId")) is 1 or 2 or 3)
                    {
                        disparoLanzado = true;
                        Console.WriteLine($"[WZ][defensa {botUid}] MISIL a {objetivoDisparo} (stack en {s.Coord}, " +
                            $"{(disparoACeldaActual ? "se queda" : "celda de aproximación")}; caza={(caza.Count > 0 ? s.Coord : "-")})");
                    }
                }
            }

            if (disparoACeldaActual)
            {
                // El disparo decide; parálisis/veneno en la misma celda solo si
                // el disparo no se pudo pagar (o no hay).
                Lanzar(new[] { AccionesTacticas.Efecto.Disparo }, objetivoMisil);
                if (!disparoLanzado)
                    Lanzar(new[] { AccionesTacticas.Efecto.Paralisis, AccionesTacticas.Efecto.Veneno }, null);
            }
            else
            {
                // Se acerca o espera: parálisis/veneno donde está (más baratas y
                // congelan 3 turnos si espera), disparo a donde iría si se acerca.
                Lanzar(new[] { AccionesTacticas.Efecto.Paralisis, AccionesTacticas.Efecto.Veneno }, null);
                if (objetivoMisil != null)
                    Lanzar(new[] { AccionesTacticas.Efecto.Disparo }, objetivoMisil);
            }
        }

        // Escudo (H) al grupo de anillo más valioso. NUNCA al cuartel (regla).
        if (e.RivalConMisil)
        {
            var grupoValioso = ocupantesAnillo
                .Where(kv => kv.Key != miCuartel && costeCelda.GetValueOrDefault(kv.Key) >= UMBRAL_COSTE_ESCUDO)
                .OrderByDescending(kv => costeCelda.GetValueOrDefault(kv.Key))
                .Select(kv => kv.Key)
                .FirstOrDefault();
            if (grupoValioso != null)
            {
                int mejor = -1, mejorCoste = int.MaxValue;
                for (int i = 0; i < fuentes.Count; i++)
                {
                    var f = fuentes[i];
                    if (f.Coste > energia) continue;
                    if (!AccionesTacticas.Catalogo.TryGetValue(f.HabId, out var h)) continue;
                    if (h.Efecto != AccionesTacticas.Efecto.Escudo) continue;
                    if (!AccionesTacticas.EnRango(h.Rango, f.Origen, grupoValioso, filas, columnas)) continue;
                    if (f.Coste < mejorCoste) { mejorCoste = f.Coste; mejor = i; }
                }
                if (mejor >= 0)
                {
                    var f = fuentes[mejor];
                    acciones.Add(AccionesTacticas.CrearAccion(f.HabId, botUid, e.Zona, f.Origen,
                        new List<string> { grupoValioso }, ctx.Turno, f.Coste, f.CartaId));
                    energia -= f.Coste; gastadoAcciones += f.Coste;
                    if (f.CartaId != null) mano.Remove(f.CartaId);
                    fuentes.RemoveAt(mejor);
                }
            }
        }

        // ── 8) Colocación ──
        var celdas = new Tablero();
        void Add(string coord, Dictionary<string, object?> c)
        {
            if (!celdas.TryGetValue(coord, out var lst)) { lst = new(); celdas[coord] = lst; }
            lst.Add(c);
        }
        foreach (var u in unidades)
        {
            string d = destino.TryGetValue(u, out var dd) ? dd : u.Coord;
            Add(d, evolucionadas.TryGetValue(u, out var evo) ? evo : u.Card);
        }

        Console.WriteLine($"[WZ][defensa {botUid}] {v.Modo}: guarnición {guarnicion.Count} ({costeCelda.GetValueOrDefault(miCuartel)} coste), " +
            $"caza {caza.Count}, anillo {ocupantesAnillo.Count} celda(s), acciones {acciones.Count}, gasto {gastado}+{gastadoAcciones}");

        return new BotMove
        {
            Celdas = celdas,
            Acciones = acciones,
            ManoResultante = mano,
            EnergiaGastada = gastado,          // despliegues + evoluciones (prepagado)
            EnergiaAcciones = gastadoAcciones, // acciones + descarga (las cobra el servidor)
        };
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Piezas
    // ─────────────────────────────────────────────────────────────────────────

    /// ¿Aguanta la guarnición DEL PLAN la entrada de este turno? Regla exacta del
    /// servidor: F_g + D_g + defensa > F_a + D_a (poder pesimista del que entra).
    private static bool Aguanta(Escenario e, BotMove plan)
    {
        int f = 0, d = 0;
        if (plan.Celdas.TryGetValue(e.Cuartel, out var lst))
            foreach (var c in lst)
                if (M.Str(M.Get(c, "ownerUid")) == e.Ctx.BotUid) { f += Fuerza(c); d += Defensa(c); }
        return f + d + e.DefensaCuartel > e.AmenazaAhora;
    }

    private static bool AguantaCon(Escenario e, List<Unidad> guarnicion)
        => guarnicion.Sum(u => u.Poder) + e.DefensaCuartel > e.AmenazaAhora;

    /// Perfil como guarnición: poder por coste, lentas antes, nunca generales ni
    /// piezas que por sí solas ya superan el umbral. Las que ya están en casa
    /// tienen un pequeño plus (no hay que moverlas).
    private static double PerfilGuarnicion(Unidad u, string miCuartel)
    {
        double perfil = u.Poder * 10.0 / Math.Max(1, u.Coste) - u.Mov;
        if (u.General) perfil -= 1000;
        if (u.Coste > UMBRAL_COSTE_GUARNICION) perfil -= 1000;
        if (u.Coord == miCuartel) perfil += 3;
        return perfil;
    }

    /// Celda de anillo para `u`: alcanzable, con terreno compatible, donde el
    /// grupo no supere el umbral de cebo, desde la que pueda volver al cuartel el
    /// turno que viene; primero las adyacentes y de más acceso, repartiendo
    /// (menos coste acumulado antes). Si no alcanza ninguna, se acerca.
    private static string ElegirCeldaAnillo(Escenario e, Unidad u, Dictionary<string, int> costeCelda, Dictionary<string, int> poderCelda, ReglasEntrada.Contexto reglas)
    {
        var reach = new HashSet<string>(reglas.Alcance(u.Coord, u.Mov, u.Tipo));
        if (u.Coord != e.Cuartel && !reglas.EnemigosPorCelda.ContainsKey(u.Coord)) reach.Add(u.Coord);

        string? mejor = null; (int, int, int, int, int) mejorKey = default;
        foreach (var c in e.CeldasAnillo)
        {
            if (!reach.Contains(c)) continue;
            if (!ReglasEntrada.CanLand(c, u.Tipo, e.Ctx.Terreno)) continue;
            int acumulado = costeCelda.GetValueOrDefault(c);
            if (acumulado > 0 && acumulado + u.Coste > UMBRAL_COSTE_GRUPO_ANILLO) continue;
            int vuelve = ReglasEntrada.Manhattan(c, e.Cuartel) <= u.Mov ? 0 : 1;
            int dist = ReglasEntrada.Manhattan(c, e.Cuartel);
            var key = (vuelve, dist, -e.PesoAcceso.GetValueOrDefault(c), acumulado, -ReglasEntrada.ValorFarmeo(reglas, c));
            if (mejor == null || key.CompareTo(mejorKey) < 0) { mejor = c; mejorKey = key; }
        }
        if (mejor != null) return mejor;

        // Sin celda de anillo a su alcance: acercarse al cuartel sin entrar en
        // celdas enemigas ni en cuarteles (la guarnición ya está decidida),
        // SIN formar otro cebo (> umbral de coste por celda) y, si se puede, sin
        // pararse donde el stack principal barrería al grupo.
        string? paso = null; (int, int, int, int) pasoKey = default;
        foreach (var c in reach)
        {
            if (e.Cuarteles.Contains(c) || reglas.EnemigosPorCelda.ContainsKey(c)) continue;
            if (!ReglasEntrada.CanLand(c, u.Tipo, e.Ctx.Terreno)) continue;
            int acumulado = costeCelda.GetValueOrDefault(c);
            int cebo = acumulado > 0 && acumulado + u.Coste > UMBRAL_COSTE_GRUPO_ANILLO ? 1 : 0;
            int poderGrupo = u.Poder + poderCelda.GetValueOrDefault(c);
            int barrido = ReglasEntrada.Expuesta(reglas, c, poderGrupo) ? 1 : 0;
            var key = (barrido, cebo, ReglasEntrada.Manhattan(c, e.Cuartel), acumulado);
            if (paso == null || key.CompareTo(pasoKey) < 0) { paso = c; pasoKey = key; }
        }
        return paso ?? u.Coord;
    }

    /// Objetivo del MISIL propio contra el stack principal (D): a donde VA A
    /// ESTAR tras su movimiento (el disparo se resuelve después de mover).
    ///   · Puede ENTRAR ya, o está aparcado (≥ TurnosParaAparcado en la celda)
    ///     o paralizado desde el turno anterior → se queda: si entra lo espera
    ///     la descarga/trampa y si no entra es porque se ha quedado a disparar
    ///     (patrón humano). Su celda actual, salvo que vaya ahí la caza.
    ///   · Si no → la CELDA DE APROXIMACIÓN: la que puede pisar este turno más
    ///     cercana a mi cuartel (donde se pone para entrar al turno siguiente),
    ///     que no sea mi cuartel ni una celda donde vaya a haber cartas mías.
    private static string? ObjetivoMisil(Escenario e, bool hayCaza, HashSet<string> celdasPropiasPlan)
    {
        var s = e.Principal;
        bool seQueda = s.EntraAhora || s.Paralizado || s.TurnosEnCelda >= AccionesTacticas.TurnosParaAparcado;
        if (seQueda && !hayCaza && !celdasPropiasPlan.Contains(s.Coord)) return s.Coord;

        string? mejor = null; (int, int) mejorKey = default;
        foreach (var c in s.Alcance)
        {
            if (c == s.Coord || c == e.Cuartel || e.Cuarteles.Contains(c)) continue;
            if (celdasPropiasPlan.Contains(c)) continue;
            if (!ReglasEntrada.CanLand(c, s.TipoRep, e.Ctx.Terreno)) continue;
            var key = (ReglasEntrada.Manhattan(c, e.Cuartel), ReglasEntrada.Manhattan(c, s.Coord));
            if (mejor == null || key.CompareTo(mejorKey) < 0) { mejor = c; mejorKey = key; }
        }
        // Si ya está pegado al cuartel su única alternativa a entrar es quedarse:
        // solo dispararle donde está si no va la caza.
        if (mejor != null && ReglasEntrada.Manhattan(s.Coord, e.Cuartel) <= 1 && !hayCaza && !celdasPropiasPlan.Contains(s.Coord))
            return s.Coord;
        if (mejor == null && !hayCaza && !celdasPropiasPlan.Contains(s.Coord)) return s.Coord;
        return mejor;
    }

    private static IEnumerable<string> CartasDesplegables(BotContext ctx, List<string> mano)
        => mano.Where(id => ctx.CatalogoMano.TryGetValue(id, out var b)
                            && !AccionesTacticas.EsCartaAccion(b) && !EsEstatica(b) && Mov(b) > 0);

    private static Unidad NuevaUnidadDesde(Dictionary<string, object?> baseCard, string id, string uid, string zona, string coord)
    {
        var card = NuevaUnidad(baseCard, id, uid, zona);
        return new Unidad
        {
            Coord = coord,
            Card = card,
            Inst = M.Str(M.Get(card, "instanceId")),
            F = Fuerza(card),
            D = Defensa(card),
            Mov = Mov(card),
            Tipo = Tipo(card),
            Coste = Coste(card),
            Nueva = true,
        };
    }

    private static Dictionary<string, object?> NuevaUnidad(
        Dictionary<string, object?> baseCard, string id, string uid, string zona)
        => new(baseCard)
        {
            ["id"] = id,
            ["ownerUid"] = uid,
            ["ownerZone"] = zona,
            ["instanceId"] = Guid.NewGuid().ToString("N"),
        };

    private static IEnumerable<string> CeldasCerca(string centro, int radio, int filas, int columnas)
    {
        var p = ReglasEntrada.Parse(centro); if (p == null) yield break;
        var (ri, ci) = p.Value;
        for (int dr = -radio; dr <= radio; dr++)
            for (int dc = -radio; dc <= radio; dc++)
            {
                if (dr == 0 && dc == 0) continue;
                if (Math.Abs(dr) + Math.Abs(dc) > radio) continue;   // Manhattan ≤ radio
                int nr = ri + dr, nc = ci + dc;
                if (nr < 0 || nr >= filas || nc < 0 || nc >= columnas) continue;
                yield return ReglasEntrada.Label(nr, nc);
            }
    }

    // ── Utilidades ──
    private static bool EsEstatica(Dictionary<string, object?> baseCard)
        => M.Int(M.Get(baseCard, "Condicion", "condicion")) == 3;
    private static int Fuerza(Dictionary<string, object?> c) => M.Int(M.Get(c, "Fuerza", "fuerza"));
    private static int Defensa(Dictionary<string, object?> c) => M.Int(M.Get(c, "Defensa", "defensa"));
    private static int Mov(Dictionary<string, object?> c) => M.Int(M.Get(c, "Movimiento", "movimiento"));
    private static int Coste(Dictionary<string, object?> c) => M.Int(M.Get(c, "Coste", "coste"));
    private static int Tipo(Dictionary<string, object?> c) { int t = M.Int(M.Get(c, "Tipo", "tipo")); return t <= 0 ? 1 : t; }

    private static bool EnEnfriamiento(Dictionary<string, object?> c, int turno)
    {
        int enf = M.Int(M.Get(c, "EnfriamientoHabilidad", "enfriamientoHabilidad"));
        if (enf <= 0) return false;
        var ultimo = M.Get(c, "UltimoUsoHabilidad", "ultimoUsoHabilidad");
        if (ultimo == null) return false;
        return (turno - M.Int(ultimo)) < enf;
    }
}