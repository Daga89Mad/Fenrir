using Tablero = System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<System.Collections.Generic.Dictionary<string, object?>>>;
using EfectosCelda = System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<System.Collections.Generic.Dictionary<string, object?>>>;
using System;
using System.Collections.Generic;
using System.Linq;

// ─────────────────────────────────────────────────────────────────────────────
// LookaheadDosPlies.cs  —  TAREA 2 del lookahead
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
// La puntuación del plan es el PEOR de los dos mundos (mín).
//
// 3 PLIES (Tarea 3): en cada mundo, tras la respuesta del rival, el bot no evalúa
// el tablero directamente, sino que genera su mejor CONTRA desde ahí (mantener,
// consolidar al cuartel, o empujar al cuartel enemigo más cercano), la simula y se
// queda con la mejor. Así ve "si me castigan con Y, recupero con Z", y deja de ser
// tan cauto con jugadas que parecen malas a 2 plies pero son recuperables. Se
// activa con USAR_TRES_PLIES (false = vuelve a 2 plies, para A/B y coste).
//
// EVOLUCIÓN: en el mundo agresivo, cada rival pincha su energía PÚBLICA en
// evolucionar sus cartas evolucionables (las más fuertes primero), así la
// simulación ve la amenaza REAL —tus 100 que se vuelven 200— y el bot deja de
// creer que su cuartel está a salvo. Es lo que le faltaba para querer defender.
//
// LÍMITES v1 (honestos): la mano del rival es OCULTA, así que su respuesta se
// modela solo REPOSICIONANDO (y evolucionando) sus cartas del tablero; no
// despliega refuerzos ni lanza acciones desde la mano. No se modelan alianzas ni terreno para tele
// (se pasan nulos); el farmeo de energía no se simula (EvaluarPosicion puntúa el
// control del mapa sobre el tablero). Todo esto se puede refinar en Tareas 3-4.
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

        // Mundo PASIVO: solo mi plan; el simulador mantiene a los enemigos donde están.
        var resPasivo = SimuladorTurno.Simular(
            tablero, obeliscos, turno, new List<SimuladorTurno.Plan> { miPlan },
            efectos, eliminados, aliadoDe: null, terreno: null, descargasPrev: descargas);
        double sPasivo = Evaluar3(ctx, resPasivo.Tablero, resPasivo.JugadoresEliminados,
                                  resPasivo.EnergiesCombate.GetValueOrDefault(ctx.BotUid));

        // Mundo AGRESIVO: mi plan + los enemigos avanzando hacia mis activos.
        var planesEnemigos = PlanesEnemigos(ctx, tablero, obeliscos, eliminados, agresivo: true);
        var todos = new List<SimuladorTurno.Plan> { miPlan };
        todos.AddRange(planesEnemigos);
        var resAgresivo = SimuladorTurno.Simular(
            tablero, obeliscos, turno, todos,
            efectos, eliminados, aliadoDe: null, terreno: null, descargasPrev: descargas);
        double sAgresivo = Evaluar3(ctx, resAgresivo.Tablero, resAgresivo.JugadoresEliminados,
                                    resAgresivo.EnergiesCombate.GetValueOrDefault(ctx.BotUid));

        return Math.Min(sPasivo, sAgresivo);
    }

    // Evaluación de hoja: a 3 plies (mejor contra del bot) o a 2 (directa).
    // v9: `energia1` = energía ganada por el bot en el turno simulado (combates y
    // conquistas), que la hoja suma como valor; las conquistas se ven por
    // `eliminados1` (JugadoresEliminados del simulador).
    private static double Evaluar3(BotContext ctx, Tablero b1, HashSet<string> eliminados1, int energia1)
        => USAR_TRES_PLIES
            ? MejorContra(ctx, b1, eliminados1, energia1)
            : EvaluadorTablero.EvaluarPosicion(ctx, b1, eliminados1, energia1);

    // Desde el tablero b1 (tras mi jugada + respuesta del rival), el bot prueba
    // varias CONTRAS, simula cada una contra el rival pasivo y devuelve la mejor
    // evaluación. Es el tercer ply: mi recuperación.
    private static double MejorContra(BotContext ctx, Tablero b1, HashSet<string> eliminados1, int energia1)
    {
        var obeliscos = ObeliscosDesde(ctx.Estado);
        var efectos = new EfectosCelda();               // aprox.: efectos de celda ya expirados
        var descargas = DescargasDesde(ctx.Estado);
        int turno = ctx.Turno + 1;

        // Objetivos de contra: MANTENER (null), CONSOLIDAR al cuartel, y EMPUJAR al
        // cuartel enemigo más cercano (punir la sobreextensión del rival).
        var objetivos = new List<string?> { null, ctx.Cuartel };
        string cuartelEnem = CuartelEnemigoMasCercano(ctx, b1, obeliscos, eliminados1);
        if (cuartelEnem != "") objetivos.Add(cuartelEnem);

        double mejor = double.MinValue;
        foreach (var obj in objetivos)
        {
            var contra = PlanBotDesde(ctx, b1, obj);
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

    // Jugada del bot desde b1: cada unidad avanza hacia `objetivo` (terreno-
    // consciente) o se queda si objetivo es null (mantener).
    private static SimuladorTurno.Plan PlanBotDesde(BotContext ctx, Tablero b1, string? objetivo)
    {
        string botUid = ctx.BotUid;
        int filas = ctx.Filas, columnas = ctx.Columnas;
        var celdas = new Tablero();
        foreach (var (coord, cartas) in b1)
            foreach (var c in cartas)
            {
                if (M.Str(M.Get(c, "ownerUid")) != botUid) continue;
                string destino = coord;
                if (objetivo != null && objetivo != "" && coord != objetivo)
                {
                    int mov = M.Int(M.Get(c, "Movimiento", "movimiento"));
                    var (tierra, mar) = TerrenoUtil.ClaseDeTipo(M.Int(M.Get(c, "Tipo", "tipo")));
                    destino = TerrenoUtil.PasoHaciaTerreno(coord, objetivo, mov, tierra, mar, ctx.Terreno, filas, columnas);
                }
                if (!celdas.TryGetValue(destino, out var lst)) { lst = new(); celdas[destino] = lst; }
                lst.Add(c);
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
    private static List<SimuladorTurno.Plan> PlanesEnemigos(
        BotContext ctx, Tablero tablero, Dictionary<string, string> obeliscos,
        HashSet<string> eliminados, bool agresivo)
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

        var planes = new List<SimuladorTurno.Plan>();
        foreach (var (owner, stacks) in porDueno)
        {
            // 1) Avanzar cada stack y recolectar (destino, carta).
            var colocadas = new List<(string destino, Dictionary<string, object?> carta)>();
            foreach (var (coord, cs, mov) in stacks)
            {
                string destino = coord;
                if (agresivo && activos.Count > 0)
                {
                    string mejorObj = ""; int mejorDist = int.MaxValue;
                    foreach (var a in activos)
                    {
                        int dd = Manhattan(coord, a, filas, columnas);
                        if (dd < mejorDist) { mejorDist = dd; mejorObj = a; }
                    }
                    if (mejorObj != "" && mejorDist <= mov + ALCANCE)
                    {
                        bool tierra = cs.Any(x => Tipo(x) is 1 or 2);
                        bool mar = cs.Any(x => Tipo(x) == 3);
                        destino = TerrenoUtil.PasoHaciaTerreno(
                            coord, mejorObj, mov, tierra, mar, ctx.Terreno, filas, columnas);
                    }
                }
                foreach (var c in cs) colocadas.Add((destino, c));
            }

            // 2) EVOLUCIÓN (solo mundo agresivo): el rival pincha su energía pública
            //    en evolucionar sus cartas evolucionables, las MÁS FUERTES primero,
            //    mientras le quede presupuesto. Así la amenaza simulada es la real.
            if (agresivo)
            {
                int presupuesto = EnergiaDe(owner);
                int evosAplicadas = 0;   // v8: tope por rival
                var evolucionables = Enumerable.Range(0, colocadas.Count)
                    .Where(i => Evolucionable(colocadas[i].carta))
                    .OrderByDescending(i => Fuerza(colocadas[i].carta))
                    .ToList();
                foreach (var i in evolucionables)
                {
                    if (evosAplicadas >= MAX_EVOS_POR_RIVAL) break;
                    int coste = CosteEvolucion(colocadas[i].carta);
                    if (coste <= 0 || coste > presupuesto) continue;
                    presupuesto -= coste;
                    colocadas[i] = (colocadas[i].destino, EvolucionarCarta(colocadas[i].carta));
                    evosAplicadas++;
                }
            }

            // 3) Construir las celdas del rival.
            var celdas = new Tablero();
            foreach (var (destino, carta) in colocadas)
            {
                if (!celdas.TryGetValue(destino, out var lst)) { lst = new(); celdas[destino] = lst; }
                lst.Add(carta);
            }
            planes.Add(new SimuladorTurno.Plan(owner, celdas, new List<Dictionary<string, object?>>()));
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
    private const double FACTOR_EVOLUCION = 1.8; // fuerza/defensa evolucionada ≈ ×1,8 (100→200). Tunable.
    private static bool Evolucionable(Dictionary<string, object?> c) =>
        M.Str(M.Get(c, "IdEvolucion", "idEvolucion")) != "";
    private static int CosteEvolucion(Dictionary<string, object?> c) =>
        M.Int(M.Get(c, "Evolucion", "evolucion"));
    private static int Fuerza(Dictionary<string, object?> c) =>
        M.Int(M.Get(c, "Fuerza", "fuerza"));
    private static Dictionary<string, object?> EvolucionarCarta(Dictionary<string, object?> c)
    {
        var copy = new Dictionary<string, object?>(c);
        copy["Fuerza"] = (long)Math.Round(Fuerza(c) * FACTOR_EVOLUCION);
        copy["Defensa"] = (long)Math.Round(M.Int(M.Get(c, "Defensa", "defensa")) * FACTOR_EVOLUCION);
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