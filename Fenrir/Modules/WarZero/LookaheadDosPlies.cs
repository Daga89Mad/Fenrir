using Tablero = System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<System.Collections.Generic.Dictionary<string, object?>>>;
using EfectosCelda = System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<System.Collections.Generic.Dictionary<string, object?>>>;
using System;
using System.Collections.Generic;
using System.Linq;

// ─────────────────────────────────────────────────────────────────────────────
// LookaheadDosPlies.cs  —  TAREA 2 del lookahead  (v2: mundo "misil")
//
// Puntúa un plan del bot a DOS PLIES: en vez de puntuar con el proxy heurístico
// del evaluador, SIMULA el turno (Tarea 1, combate exacto) contra la respuesta
// enemiga y evalúa el tablero RESULTANTE con EvaluadorTablero.EvaluarPosicion.
//
// Pesimista y acotado (misma filosofía que el evaluador, pero exacta):
//   · Mundo PASIVO   — los enemigos se quedan quietos (el simulador los arrastra).
//   · Mundo AGRESIVO — cada stack enemigo que ALCANZA a contestar esta ronda
//                      avanza hacia el activo propio más cercano (cuartel o
//                      unidad), RESPETANDO EL TERRENO: un stack de mar no cruza
//                      tierra, así que no amenaza celdas donde no puede entrar.
//                      Los que no alcanzan (o están bloqueados) se quedan.
//   · Mundo MISIL (v2) — si algún rival puede pagar un disparo lejano (energía
//                      pública ≥ coste estimado y cartas en mano), DISPARA a la
//                      celda propia más valiosa que NO se mueve (el cuartel si
//                      tiene guarnición; si no, el stack propio aparcado más
//                      caro por encima del umbral de cebo) y avanza como en el
//                      agresivo pero SIN entrar en mi cuartel ese turno (el que
//                      dispara al cuartel entra al siguiente: un disparo mata
//                      también lo que entra). Es el patrón exacto con el que los
//                      humanos vencían a los bots (reto T25: 6 cartas en F1
//                      muertas de un disparo, conquista en T26) y lo que hacía
//                      que apilar en el cuartel pareciera "sólido".
// La puntuación del plan es el PEOR de los mundos (mín).
//
// 3 PLIES (Tarea 3): en cada mundo, tras la respuesta del rival, el bot no evalúa
// el tablero directamente, sino que genera su mejor CONTRA desde ahí (mantener,
// consolidar al cuartel, o empujar al cuartel enemigo más cercano), la simula y se
// queda con la mejor. Así ve "si me castigan con Y, recupero con Z", y deja de ser
// tan cauto con jugadas que parecen malas a 2 plies pero son recuperables. Se
// activa con USAR_TRES_PLIES (false = vuelve a 2 plies, para A/B y coste). En el
// mundo misil la contra "consolidar al cuartel" es justo lo que hace un ANILLO:
// recubrir el cuartel con lo que esperaba fuera.
//
// EVOLUCIÓN: en el mundo agresivo, cada rival pincha su energía PÚBLICA en
// evolucionar sus cartas evolucionables (las más fuertes primero), así la
// simulación ve la amenaza REAL —tus 100 que se vuelven 200— y el bot deja de
// creer que su cuartel está a salvo. Es lo que le faltaba para querer defender.
//
// v10 (partidas de estudio 9UCN / TrTl / wlDM):
//   · REFUERZO DE CUARTEL en el mundo agresivo: cada rival despliega en su
//     cuartel una carta virtual con el poder que compra su energía pública
//     (acotada a RefuerzoMaxLookahead y a que tenga mano). Sin esto, un cuartel
//     vacío parecía gratis y el bot metía cartas sueltas que morían contra el
//     despliegue rival (9UCN T11).
//   · EVOLUCIÓN REAL: se usan las estadísticas de la carta evolucionada del
//     catálogo (ctx.Evoluciones incluye ahora las de todo el tablero); ×1,8 es
//     solo el fallback.
//   · La CONTRA del bot (3er ply) avanza de forma cohesionada y segura
//     (ReglasEntrada.AvanceCohesionado) en vez de con el paso greedy.
//
// v2 además:
//   · La ENERGÍA DE ACCIONES del plan (disparos, escudos, descarga) resta
//     W_ENERGIA_ACCIONES por punto: la hoja no ve el coste de una acción, y sin
//     esto un misil "salía gratis" y se disparaba a stacks que se mueven (4 de
//     los 5 disparos de los bots en el reto cayeron en celdas vacías).
//   · La DESCARGA lleva además una penalización fija (es una por partida y deja
//     el cuartel a 0/10/20/30 tres turnos): solo se elige si la simulación la
//     paga con creces (el rival entra y muere), nunca "por si acaso".
//   · La descarga del plan se arrastra al 3er ply (defensa reducida ya visible).
//
// LÍMITES (honestos): la mano del rival es OCULTA, así que su refuerzo es una
// estimación por energía y su disparo, una hipótesis por energía. No se modelan
// alianzas ni terreno para tele (se pasan nulos); el farmeo de energía no se
// simula (EvaluarPosicion puntúa el control del mapa sobre el tablero).
// ─────────────────────────────────────────────────────────────────────────────
public static class LookaheadDosPlies
{
    private const int ALCANCE = 1;            // adyacencia para "poder contestar" esta ronda
    private const bool USAR_TRES_PLIES = true; // Tarea 3: contra tras la respuesta del rival

    // v8: tope de EVOLUCIONES simuladas por rival en el mundo agresivo. Antes se
    // evolucionaba TODO lo evolucionable con la energía pública: con 3-4 rivales
    // el mundo agresivo salía apocalíptico para CUALQUIER plan, los scores se
    // aplastaban y el lookahead dejaba de distinguir jugadas buenas de malas
    // (otra pata del "los bots no hacen nada"). Dos evoluciones por rival y
    // turno es el techo realista de la propia estrategia del bot (MaxEvoluciones).
    private const int MAX_EVOS_POR_RIVAL = 2;

    // v2: coste de las acciones del plan en la escala del evaluador (1 punto de
    // material ≈ 1 de energía; una acción sin retorno simulado no es gratis).
    private const double W_ENERGIA_ACCIONES = 0.5;
    // v2: penalización fija por usar la DESCARGA (una por partida + 3 turnos con
    // el cuartel a media defensa). El plan con descarga debe superar al mismo
    // plan sin ella en al menos esto (≈ una carta media) en el peor mundo.
    private const double PENALIZACION_DESCARGA = 40.0;
    // v2: valor extra que el rival ve en disparar al CUARTEL (abre la conquista)
    // frente a un stack de igual coste fuera de él.
    private const int VALOR_EXTRA_MISIL_CUARTEL = 60;

    /// Puntuación a 2 plies del plan del bot (mayor = mejor).
    public static double Puntuar(BotContext ctx, BotMove plan)
    {
        var tablero = TableroDesde(ctx.Estado);
        var obeliscos = ObeliscosDesde(ctx.Estado);
        var efectos = EfectosDesde(ctx.Estado);
        var eliminados = M.List(M.Get(ctx.Estado, "jugadoresEliminados")).Select(M.Str).ToHashSet();
        var descargas = DescargasDesde(ctx.Estado);
        int turno = ctx.Turno;

        var miPlan = new SimuladorTurno.Plan(ctx.BotUid, plan.Celdas, plan.Acciones);

        // v2: la descarga del plan también cuenta en el 3er ply (defensa 10 al
        // turno siguiente), no solo en el turno simulado.
        bool tieneDescarga = plan.Acciones.Any(AccionesTacticas.EsDescarga);
        var descargasTrasPly1 = new Dictionary<string, int>(descargas);
        if (tieneDescarga && ctx.Cuartel != "") descargasTrasPly1[ctx.Cuartel] = turno;

        // Mundo PASIVO: solo mi plan; el simulador mantiene a los enemigos donde están.
        var resPasivo = SimuladorTurno.Simular(
            tablero, obeliscos, turno, new List<SimuladorTurno.Plan> { miPlan },
            efectos, eliminados, aliadoDe: null, terreno: null, descargasPrev: descargas);
        double sPasivo = Evaluar3(ctx, resPasivo.Tablero, resPasivo.JugadoresEliminados,
                                  resPasivo.EnergiesCombate.GetValueOrDefault(ctx.BotUid), descargasTrasPly1);

        // Mundo AGRESIVO: mi plan + los enemigos avanzando hacia mis activos.
        var planesEnemigos = PlanesEnemigos(ctx, tablero, obeliscos, eliminados, agresivo: true, entrarMiCuartel: true);
        var todos = new List<SimuladorTurno.Plan> { miPlan };
        todos.AddRange(planesEnemigos);
        var resAgresivo = SimuladorTurno.Simular(
            tablero, obeliscos, turno, todos,
            efectos, eliminados, aliadoDe: null, terreno: null, descargasPrev: descargas);
        double sAgresivo = Evaluar3(ctx, resAgresivo.Tablero, resAgresivo.JugadoresEliminados,
                                    resAgresivo.EnergiesCombate.GetValueOrDefault(ctx.BotUid), descargasTrasPly1);

        double score = Math.Min(sPasivo, sAgresivo);

        // Mundo MISIL (v2): un rival con energía dispara a mi celda parada más
        // valiosa y avanza sin entrar en mi cuartel este turno.
        var misil = ObjetivoMisilRival(ctx, plan, tablero, obeliscos, eliminados);
        double? sMisilDebug = null;
        if (misil != null)
        {
            var (tirador, cuartelTirador, objetivo, coste) = misil.Value;
            var planesMisil = PlanesEnemigos(ctx, tablero, obeliscos, eliminados, agresivo: true, entrarMiCuartel: false);
            var disparo = AccionesTacticas.CrearAccion(3, tirador, "", cuartelTirador,
                new List<string> { objetivo }, turno, coste);
            int idx = planesMisil.FindIndex(p => p.Uid == tirador);
            if (idx >= 0)
            {
                var p = planesMisil[idx];
                var acc = new List<Dictionary<string, object?>>(p.Acciones) { disparo };
                planesMisil[idx] = new SimuladorTurno.Plan(p.Uid, p.Celdas, acc);
            }
            else
            {
                planesMisil.Add(new SimuladorTurno.Plan(tirador, new Tablero(),
                    new List<Dictionary<string, object?>> { disparo }));
            }
            var todosMisil = new List<SimuladorTurno.Plan> { miPlan };
            todosMisil.AddRange(planesMisil);
            var resMisil = SimuladorTurno.Simular(
                tablero, obeliscos, turno, todosMisil,
                efectos, eliminados, aliadoDe: null, terreno: null, descargasPrev: descargas);
            double sMisil = Evaluar3(ctx, resMisil.Tablero, resMisil.JugadoresEliminados,
                                     resMisil.EnergiesCombate.GetValueOrDefault(ctx.BotUid), descargasTrasPly1);
            score = Math.Min(score, sMisil);
            sMisilDebug = sMisil;
        }

        // v2: las acciones no son gratis; la descarga, menos.
        score -= W_ENERGIA_ACCIONES * Math.Max(0, plan.EnergiaAcciones);
        if (tieneDescarga) score -= PENALIZACION_DESCARGA;
        if (DEBUG)
            Console.WriteLine($"[WZ][lookahead {ctx.BotUid}] pasivo={sPasivo:F1} agresivo={sAgresivo:F1} " +
                $"misil={(sMisilDebug.HasValue ? sMisilDebug.Value.ToString("F1") : "-")}" +
                $"{(misil != null ? $" (dispara {misil.Value.tirador} a {misil.Value.objetivo})" : "")} " +
                $"acciones={plan.EnergiaAcciones} descarga={tieneDescarga} → {score:F1}");
        return score;
    }

    // Diagnóstico por mundo (WZ_LOOKAHEAD_DEBUG=1 en el entorno del runner).
    private static readonly bool DEBUG = Environment.GetEnvironmentVariable("WZ_LOOKAHEAD_DEBUG") == "1";

    // v2: ¿a qué celda propia dispararía un rival con misil este turno?
    // Candidatas: mi cuartel (si mi plan deja cartas mías en él) y cualquier
    // celda con cartas mías que NO se mueven (mismas instancias que ahora) con
    // coste ≥ umbral de cebo. Vale más el cuartel (abre la conquista).
    // Devuelve (uid tirador, su cuartel, celda objetivo, coste) o null.
    private static (string tirador, string cuartelTirador, string objetivo, int coste)? ObjetivoMisilRival(
        BotContext ctx, BotMove plan, Tablero tablero, Dictionary<string, string> obeliscos, HashSet<string> eliminados)
    {
        var rivales = AccionesTacticas.RivalesConMisil(ctx);
        if (rivales.Count == 0) return null;
        string botUid = ctx.BotUid;
        string miCuartel = ctx.Cuartel != "" ? ctx.Cuartel : obeliscos.GetValueOrDefault(botUid, "");
        if (miCuartel == "") return null;

        // Instancias propias por celda AHORA.
        var instAhora = new Dictionary<string, HashSet<string>>();
        foreach (var (coord, cartas) in tablero)
            foreach (var c in cartas)
            {
                if (M.Str(M.Get(c, "ownerUid")) != botUid) continue;
                if (!instAhora.TryGetValue(coord, out var s)) { s = new(); instAhora[coord] = s; }
                s.Add(M.Str(M.Get(c, "instanceId")));
            }

        string? mejor = null; int mejorValor = 0;
        foreach (var (coord, cartas) in plan.Celdas)
        {
            int coste = 0, n = 0; bool parada = true;
            foreach (var c in cartas)
            {
                if (M.Str(M.Get(c, "ownerUid")) != botUid) continue;
                n++; coste += M.Int(M.Get(c, "Coste", "coste"));
                var iid = M.Str(M.Get(c, "instanceId"));
                if (coord != miCuartel && (iid == "" || !instAhora.TryGetValue(coord, out var s) || !s.Contains(iid)))
                    parada = false;   // llega este turno: no es predecible
            }
            if (n == 0) continue;
            int valor;
            if (coord == miCuartel) valor = coste + VALOR_EXTRA_MISIL_CUARTEL;   // la guarnición nunca se mueve
            else if (parada && coste >= AccionesTacticas.UmbralCosteCebo) valor = coste;
            else continue;
            if (valor > mejorValor) { mejorValor = valor; mejor = coord; }
        }
        if (mejor == null) return null;

        // Tirador: el rival con misil más rico. Su cuartel es el origen de la
        // acción (carta de acción jugada desde la mano).
        var stats = M.Map(M.Get(ctx.Estado, "statsPartida"));
        string tirador = rivales
            .OrderByDescending(u => M.Int(M.Get(M.Map(M.Get(stats, u)), "energies")))
            .First();
        string cuartelTirador = obeliscos.GetValueOrDefault(tirador, "");
        return (tirador, cuartelTirador, mejor, AccionesTacticas.CosteDisparoEstimado(ctx));
    }

    // Evaluación de hoja: a 3 plies (mejor contra del bot) o a 2 (directa).
    // v9: `energia1` = energía ganada por el bot en el turno simulado (combates y
    // conquistas), que la hoja suma como valor; las conquistas se ven por
    // `eliminados1` (JugadoresEliminados del simulador).
    private static double Evaluar3(BotContext ctx, Tablero b1, HashSet<string> eliminados1, int energia1,
                                   Dictionary<string, int> descargasTrasPly1)
        => USAR_TRES_PLIES
            ? MejorContra(ctx, b1, eliminados1, energia1, descargasTrasPly1)
            : EvaluadorTablero.EvaluarPosicion(ctx, b1, eliminados1, energia1);

    // Desde el tablero b1 (tras mi jugada + respuesta del rival), el bot prueba
    // varias CONTRAS, simula cada una contra el rival pasivo y devuelve la mejor
    // evaluación. Es el tercer ply: mi recuperación.
    private static double MejorContra(BotContext ctx, Tablero b1, HashSet<string> eliminados1, int energia1,
                                      Dictionary<string, int> descargas)
    {
        var obeliscos = ObeliscosDesde(ctx.Estado);
        var efectos = new EfectosCelda();               // aprox.: efectos de celda ya expirados
        int turno = ctx.Turno + 1;

        // Objetivos de contra: MANTENER (null), CONSOLIDAR al cuartel, y EMPUJAR al
        // cuartel enemigo más cercano (punir la sobreextensión del rival).
        var objetivos = new List<string?> { null, ctx.Cuartel };
        string cuartelEnem = CuartelEnemigoMasCercano(ctx, b1, obeliscos, eliminados1);
        if (cuartelEnem != "") objetivos.Add(cuartelEnem);

        double mejor = double.MinValue;
        foreach (var obj in objetivos)
        {
            var contra = PlanBotDesde(ctx, b1, eliminados1, obj);
            var res = SimuladorTurno.Simular(
                b1, obeliscos, turno, new List<SimuladorTurno.Plan> { contra },
                efectos, eliminados1, aliadoDe: null, terreno: null, descargasPrev: descargas);
            // v9: la hoja recibe los eliminados y la energía ganada tras la contra
            // (conquista / combates a 3 plies, acumulados con los del turno 1).
            double v = EvaluadorTablero.EvaluarPosicion(
                ctx, res.Tablero, res.JugadoresEliminados,
                energia1 + res.EnergiesCombate.GetValueOrDefault(ctx.BotUid));
            if (v > mejor) mejor = v;
        }
        return mejor == double.MinValue
            ? EvaluadorTablero.EvaluarPosicion(ctx, b1, eliminados1, energia1)
            : mejor;
    }

    // Jugada del bot desde b1: cada unidad avanza hacia `objetivo` o se queda si
    // objetivo es null (mantener). v10: el avance es COHESIONADO y SEGURO
    // (ReglasEntrada sobre el tablero simulado): el subgrupo que alcanza el
    // objetivo entra solo si gana junto; el resto se acerca sin pisar celdas
    // donde pierde. Antes el paso greedy hacía que la "contra" metiera cartas
    // sueltas en stacks y cuarteles rivales, y el tercer ply valoraba
    // recuperaciones que en realidad eran suicidios.
    private static SimuladorTurno.Plan PlanBotDesde(BotContext ctx, Tablero b1, HashSet<string> eliminados1, string? objetivo)
    {
        string botUid = ctx.BotUid;
        var celdas = new Tablero();
        void Add(string coord, Dictionary<string, object?> c)
        {
            if (!celdas.TryGetValue(coord, out var lst)) { lst = new(); celdas[coord] = lst; }
            lst.Add(c);
        }

        if (objetivo == null || objetivo == "")
        {
            foreach (var (coord, cartas) in b1)
                foreach (var c in cartas)
                    if (M.Str(M.Get(c, "ownerUid")) == botUid) Add(coord, c);
            return new SimuladorTurno.Plan(botUid, celdas, new List<Dictionary<string, object?>>());
        }

        var k = ReglasEntrada.Crear(ctx, b1, eliminados1);
        var moviles = new List<(string coord, Dictionary<string, object?> card)>();
        var ocupacion = new Dictionary<string, (int f, int d)>();
        foreach (var (coord, cartas) in b1)
            foreach (var c in cartas)
            {
                if (M.Str(M.Get(c, "ownerUid")) != botUid) continue;
                if (M.Int(M.Get(c, "Movimiento", "movimiento")) <= 0 || coord == objetivo)
                {
                    Add(coord, c);
                    var s = ocupacion.TryGetValue(coord, out var v) ? v : (0, 0);
                    ocupacion[coord] = (s.Item1 + Fuerza(c), s.Item2 + M.Int(M.Get(c, "Defensa", "defensa")));
                    continue;
                }
                moviles.Add((coord, c));
            }
        if (moviles.Count > 0)
        {
            var destinos = ReglasEntrada.AvanceCohesionado(k, moviles, objetivo, ocupacion);
            for (int i = 0; i < moviles.Count; i++) Add(destinos[i], moviles[i].card);
        }
        return new SimuladorTurno.Plan(botUid, celdas, new List<Dictionary<string, object?>>());
    }

    // Cuartel enemigo vivo más cercano a alguna unidad del bot en b1 (o "").
    private static string CuartelEnemigoMasCercano(
        BotContext ctx, Tablero b1, Dictionary<string, string> obeliscos, HashSet<string> eliminados)
    {
        string botUid = ctx.BotUid;
        int filas = ctx.Filas, columnas = ctx.Columnas;
        var misCoords = b1.Where(kv => kv.Value.Any(c => M.Str(M.Get(c, "ownerUid")) == botUid))
                          .Select(kv => kv.Key).ToList();
        if (misCoords.Count == 0) return "";
        string mejor = ""; int mejorDist = int.MaxValue;
        foreach (var (uid, coord) in obeliscos)
        {
            if (uid == botUid || eliminados.Contains(uid) || coord == "") continue;
            foreach (var mc in misCoords)
            {
                int dd = Manhattan(mc, coord, filas, columnas);
                if (dd < mejorDist) { mejorDist = dd; mejor = coord; }
            }
        }
        return mejor;
    }

    // Construye una jugada por cada jugador enemigo. Si `agresivo`, cada stack que
    // alcanza a contestar esta ronda avanza hacia el activo propio más cercano
    // (cuartel o unidad); el resto se queda. Reemite TODAS las cartas del enemigo.
    // v2: con `entrarMiCuartel = false` (mundo misil) ningún stack entra en mi
    // cuartel este turno: el que va a dispararlo se queda donde está.
    private static List<SimuladorTurno.Plan> PlanesEnemigos(
        BotContext ctx, Tablero tablero, Dictionary<string, string> obeliscos,
        HashSet<string> eliminados, bool agresivo, bool entrarMiCuartel = true)
    {
        string botUid = ctx.BotUid;
        int filas = ctx.Filas, columnas = ctx.Columnas;

        // Activos del bot como objetivos del avance: cuartel + celdas con unidades.
        string miCuartel = obeliscos.GetValueOrDefault(botUid, "");
        var activos = new List<string>();
        if (miCuartel != "") activos.Add(miCuartel);
        foreach (var (coord, cartas) in tablero)
            if (coord != miCuartel && cartas.Any(c => M.Str(M.Get(c, "ownerUid")) == botUid))
                activos.Add(coord);

        // Stacks enemigos por dueño: (celda, cartas de ese dueño, mov máx).
        var porDueno = new Dictionary<string, List<(string coord, List<Dictionary<string, object?>> cartas, int mov)>>();
        foreach (var (coord, cartas) in tablero)
        {
            var porOwner = new Dictionary<string, List<Dictionary<string, object?>>>();
            foreach (var card in cartas)
            {
                var owner = M.Str(M.Get(card, "ownerUid"));
                if (owner == "" || owner == botUid || eliminados.Contains(owner)) continue;
                if (!porOwner.TryGetValue(owner, out var l)) { l = new(); porOwner[owner] = l; }
                l.Add(card);
            }
            foreach (var (owner, cs) in porOwner)
            {
                int mov = cs.Max(Mov);
                if (!porDueno.TryGetValue(owner, out var st)) { st = new(); porDueno[owner] = st; }
                st.Add((coord, cs, mov));
            }
        }

        var stats = M.Map(M.Get(ctx.Estado, "statsPartida"));
        int EnergiaDe(string uid) => M.Int(M.Get(M.Map(M.Get(stats, uid)), "energies"));

        // v2b: CUARTEL PRIMERO. Poder de la guarnición que el rival VE AHORA
        // (tablero, no el plan: los movimientos son simultáneos) más la defensa
        // real del cuartel (0/10/20/30 tras una descarga).
        int poderGuarnicionActual = 0;
        if (miCuartel != "" && tablero.TryGetValue(miCuartel, out var enCasa))
            poderGuarnicionActual = enCasa.Where(c => M.Str(M.Get(c, "ownerUid")) == botUid).Sum(c => Fuerza(c) + Defensa(c));
        int defensaCuartel = miCuartel != ""
            ? AccionesTacticas.DefensaCuartelActual(ctx.Estado, miCuartel, ctx.Turno) : 0;

        var planes = new List<SimuladorTurno.Plan>();
        foreach (var (owner, stacks) in porDueno)
        {
            // Poder pesimista de un stack: el actual más lo que compra su energía
            // pública en evoluciones (hasta MAX_EVOS_POR_RIVAL, mayor ganancia primero).
            int PoderPesimista(List<Dictionary<string, object?>> cs)
            {
                int poder = cs.Sum(c => Fuerza(c) + Defensa(c));
                int presupuestoEvo = EnergiaDe(owner), evos = 0;
                foreach (var c in cs.Where(Evolucionable).OrderByDescending(c => GananciaEvolucion(ctx, c)))
                {
                    if (evos >= MAX_EVOS_POR_RIVAL) break;
                    int coste = CosteEvolucion(c);
                    if (coste <= 0 || coste > presupuestoEvo) continue;
                    presupuestoEvo -= coste; poder += GananciaEvolucion(ctx, c); evos++;
                }
                return poder;
            }

            // 1) Avanzar cada stack y recolectar (destino, carta).
            var colocadas = new List<(string destino, Dictionary<string, object?> carta)>();
            foreach (var (coord, cs, mov) in stacks)
            {
                string destino = coord;
                if (agresivo && activos.Count > 0)
                {
                    bool tierra = cs.Any(x => Tipo(x) is 1 or 2);
                    bool mar = cs.Any(x => Tipo(x) == 3);

                    // v2b: si el stack ALCANZA mi cuartel este turno y GANA contra
                    // la guarnición que ve (poder pesimista > guarnición + defensa),
                    // ENTRA. Es el peor caso para el bot y lo que hace un rival que
                    // sabe jugar. Antes el stack iba al activo propio más cercano
                    // por Manhattan, y una carta suelta a una casilla de él
                    // "desviaba" la entrada: el lookahead no veía la conquista y
                    // premiaba apilar en el cuartel (reto T25).
                    string? entrada = null;
                    if (entrarMiCuartel && miCuartel != "" && coord != miCuartel
                        && Manhattan(coord, miCuartel, filas, columnas) <= mov)
                    {
                        string paso = TerrenoUtil.PasoHaciaTerreno(
                            coord, miCuartel, mov, tierra, mar, ctx.Terreno, filas, columnas);
                        if (paso == miCuartel && PoderPesimista(cs) > poderGuarnicionActual + defensaCuartel)
                            entrada = miCuartel;
                    }

                    if (entrada != null) destino = entrada;
                    else
                    {
                        string mejorObj = ""; int mejorDist = int.MaxValue;
                        foreach (var a in activos)
                        {
                            int dd = Manhattan(coord, a, filas, columnas);
                            if (dd < mejorDist) { mejorDist = dd; mejorObj = a; }
                        }
                        if (mejorObj != "" && mejorDist <= mov + ALCANCE)
                        {
                            destino = TerrenoUtil.PasoHaciaTerreno(
                                coord, mejorObj, mov, tierra, mar, ctx.Terreno, filas, columnas);
                            // v2 (mundo misil): este turno nadie entra en mi cuartel.
                            if (!entrarMiCuartel && miCuartel != "" && destino == miCuartel) destino = coord;
                        }
                    }
                }
                foreach (var c in cs) colocadas.Add((destino, c));
            }

            // 2) EVOLUCIÓN (solo mundo agresivo): el rival pincha su energía pública
            //    en evolucionar sus cartas evolucionables, las de MÁS GANANCIA
            //    primero, mientras le quede presupuesto. v10: con las
            //    estadísticas REALES de la carta evolucionada cuando está en
            //    ctx.Evoluciones (el bot ya carga las de todo el tablero); el
            //    factor ×1,8 solo es el fallback. Una Bestia del abismo (10/5)
            //    pasa a Bestia del cielo (55/18), no a 18/9.
            int presupuesto = EnergiaDe(owner);
            if (agresivo)
            {
                int evosAplicadas = 0;   // v8: tope por rival
                var evolucionables = Enumerable.Range(0, colocadas.Count)
                    .Where(i => Evolucionable(colocadas[i].carta))
                    .OrderByDescending(i => GananciaEvolucion(ctx, colocadas[i].carta))
                    .ToList();
                foreach (var i in evolucionables)
                {
                    if (evosAplicadas >= MAX_EVOS_POR_RIVAL) break;
                    int coste = CosteEvolucion(colocadas[i].carta);
                    if (coste <= 0 || coste > presupuesto) continue;
                    presupuesto -= coste;
                    colocadas[i] = (colocadas[i].destino, EvolucionarCarta(ctx, colocadas[i].carta));
                    evosAplicadas++;
                }
            }

            // 3) REFUERZO DE CUARTEL (solo mundo agresivo, v10). La mano del
            //    rival es oculta, pero su energía y el tamaño de su mano son
            //    públicos, y SIEMPRE puede desplegar en su cuartel. Se añade una
            //    carta virtual (sin coste: si cae no regala energía) con el poder
            //    que esa energía compra en cartas base. Es lo que faltaba para
            //    que el lookahead viera que entrar en un cuartel "vacío" con una
            //    carta suelta no es gratis (9UCN T11: F1 vacío, 82 de energía
            //    rival → Llama del Sheol desplegada encima de la Manta).
            if (agresivo && obeliscos.TryGetValue(owner, out var cuartelRival) && cuartelRival != "")
            {
                int mano = M.List(M.Get(M.Map(M.Get(stats, owner)), "mano")).Count;
                if (mano > 0 && presupuesto >= ReglasEntrada.EnergiaMinimaRefuerzo)
                {
                    int e = Math.Min(presupuesto, ReglasEntrada.RefuerzoMaxLookahead);
                    var virtualCard = new Dictionary<string, object?>
                    {
                        ["id"] = "virtual-refuerzo",
                        ["Nombre"] = "(refuerzo estimado)",
                        ["Fuerza"] = (long)Math.Round(e * ReglasEntrada.RefuerzoFuerzaPorEnergia),
                        ["Defensa"] = (long)Math.Round(e * ReglasEntrada.RefuerzoDefensaPorEnergia),
                        ["Coste"] = 0L,
                        ["Movimiento"] = 0L,
                        ["Tipo"] = 1L,
                        ["Condicion"] = 0L,
                        ["ownerUid"] = owner,
                        ["ownerZone"] = "",
                        ["instanceId"] = "virt-" + owner,
                    };
                    colocadas.Add((cuartelRival, virtualCard));
                }
            }

            // 4) Construir las celdas del rival.
            var celdas = new Tablero();
            foreach (var (destino, carta) in colocadas)
            {
                if (!celdas.TryGetValue(destino, out var lst)) { lst = new(); celdas[destino] = lst; }
                lst.Add(carta);
            }
            planes.Add(new SimuladorTurno.Plan(owner, celdas, new List<Dictionary<string, object?>>()));
        }

        // 5) Rivales VIVOS sin ninguna carta en el tablero (v10): también pueden
        //    desplegar en su cuartel. Sin esto, un cuartel "vacío del todo"
        //    parecía gratis para cualquier carta suelta.
        if (agresivo)
            foreach (var (uid, cuartelRival) in obeliscos)
            {
                if (uid == botUid || uid == "" || cuartelRival == "" || eliminados.Contains(uid)) continue;
                if (porDueno.ContainsKey(uid)) continue;
                int presupuesto = EnergiaDe(uid);
                int mano = M.List(M.Get(M.Map(M.Get(stats, uid)), "mano")).Count;
                if (mano <= 0 || presupuesto < ReglasEntrada.EnergiaMinimaRefuerzo) continue;
                int e = Math.Min(presupuesto, ReglasEntrada.RefuerzoMaxLookahead);
                var celdas = new Tablero
                {
                    [cuartelRival] = new List<Dictionary<string, object?>>
                    {
                        new Dictionary<string, object?>
                        {
                            ["id"] = "virtual-refuerzo",
                            ["Nombre"] = "(refuerzo estimado)",
                            ["Fuerza"] = (long)Math.Round(e * ReglasEntrada.RefuerzoFuerzaPorEnergia),
                            ["Defensa"] = (long)Math.Round(e * ReglasEntrada.RefuerzoDefensaPorEnergia),
                            ["Coste"] = 0L,
                            ["Movimiento"] = 0L,
                            ["Tipo"] = 1L,
                            ["Condicion"] = 0L,
                            ["ownerUid"] = uid,
                            ["ownerZone"] = "",
                            ["instanceId"] = "virt-" + uid,
                        }
                    }
                };
                planes.Add(new SimuladorTurno.Plan(uid, celdas, new List<Dictionary<string, object?>>()));
            }
        return planes;
    }

    // ── Parseo de ctx.Estado a los tipos del simulador ──
    private static Tablero TableroDesde(Dictionary<string, object?> estado)
    {
        var t = new Tablero();
        foreach (var kv in M.Map(M.Get(estado, "tablero")))
            t[kv.Key] = M.List(kv.Value).Select(M.Map).ToList();
        return t;
    }

    private static Dictionary<string, string> ObeliscosDesde(Dictionary<string, object?> estado)
    {
        var o = new Dictionary<string, string>();
        foreach (var kv in M.Map(M.Get(estado, "obeliscos")))
        {
            var c = M.Str(kv.Value);
            if (c != "") o[kv.Key] = c;
        }
        return o;
    }

    private static EfectosCelda EfectosDesde(Dictionary<string, object?> estado)
    {
        var e = new EfectosCelda();
        foreach (var kv in M.Map(M.Get(estado, "efectosCelda")))
        {
            var lista = M.List(kv.Value).Select(M.Map).ToList();
            if (lista.Count > 0) e[kv.Key] = lista;
        }
        return e;
    }

    private static Dictionary<string, int> DescargasDesde(Dictionary<string, object?> estado)
    {
        var d = new Dictionary<string, int>();
        foreach (var kv in M.Map(M.Get(estado, "descargasCuartel")))
        {
            var td = M.Int(kv.Value);
            if (kv.Key != "" && td > 0) d[kv.Key] = td;
        }
        return d;
    }

    // ── Geometría (formato Letra+Número, p. ej. "B3") ──
    // ── Evolución (para el modelo enemigo pesimista) ──
    // v10: estadísticas REALES si la carta evolucionada está en ctx.Evoluciones
    // (WarZeroBot carga las evoluciones de TODAS las cartas del tablero, no
    // solo las propias); ×1,8 es solo el fallback para cartas desconocidas.
    private const double FACTOR_EVOLUCION = ReglasEntrada.FactorEvolucionDesconocida;
    private static bool Evolucionable(Dictionary<string, object?> c) =>
        M.Str(M.Get(c, "IdEvolucion", "idEvolucion")) != "" && CosteEvolucion(c) > 0;
    private static int CosteEvolucion(Dictionary<string, object?> c) =>
        M.Int(M.Get(c, "Evolucion", "evolucion"));
    private static int Fuerza(Dictionary<string, object?> c) =>
        M.Int(M.Get(c, "Fuerza", "fuerza"));
    private static int Defensa(Dictionary<string, object?> c) =>
        M.Int(M.Get(c, "Defensa", "defensa"));

    /// Poder (F+D) que gana la carta al evolucionar (real o estimado).
    private static int GananciaEvolucion(BotContext ctx, Dictionary<string, object?> c)
    {
        var idEvo = M.Str(M.Get(c, "IdEvolucion", "idEvolucion"));
        if (idEvo != "" && ctx.Evoluciones.TryGetValue(idEvo, out var evo))
            return Math.Max(0, Fuerza(evo) + Defensa(evo) - Fuerza(c) - Defensa(c));
        return (int)Math.Round((Fuerza(c) + Defensa(c)) * (FACTOR_EVOLUCION - 1.0));
    }

    private static Dictionary<string, object?> EvolucionarCarta(BotContext ctx, Dictionary<string, object?> c)
    {
        var copy = new Dictionary<string, object?>(c);
        var idEvo = M.Str(M.Get(c, "IdEvolucion", "idEvolucion"));
        if (idEvo != "" && ctx.Evoluciones.TryGetValue(idEvo, out var evo))
        {
            copy["Fuerza"] = (long)Fuerza(evo);
            copy["Defensa"] = (long)Defensa(evo);
            copy["Coste"] = (long)M.Int(M.Get(evo, "Coste", "coste"));
            copy["Nombre"] = M.Str(M.Get(evo, "Nombre", "nombre"));
            copy["Condicion"] = (long)M.Int(M.Get(evo, "Condicion", "condicion"));
            copy["IdEvolucion"] = "";
            copy["Evolucion"] = 0L;
            return copy;
        }
        copy["Fuerza"] = (long)Math.Round(Fuerza(c) * FACTOR_EVOLUCION);
        copy["Defensa"] = (long)Math.Round(Defensa(c) * FACTOR_EVOLUCION);
        return copy;
    }

    private static int Mov(Dictionary<string, object?> c) => M.Int(M.Get(c, "Movimiento", "movimiento"));
    private static int Tipo(Dictionary<string, object?> c) => M.Int(M.Get(c, "Tipo", "tipo"));

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