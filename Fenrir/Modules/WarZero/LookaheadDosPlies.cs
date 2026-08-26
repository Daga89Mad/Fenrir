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
//                      unidad). Los que no alcanzan se quedan.
// La puntuación del plan es el PEOR de los dos mundos (mín).
//
// LÍMITES v1 (honestos): la mano del rival es OCULTA, así que su respuesta se
// modela solo REPOSICIONANDO sus cartas del tablero (no despliega refuerzos ni
// lanza acciones desde la mano). No se modelan alianzas ni terreno para tele
// (se pasan nulos); el farmeo de energía no se simula (EvaluarPosicion puntúa el
// control del mapa sobre el tablero). Todo esto se puede refinar en Tareas 3-4.
// ─────────────────────────────────────────────────────────────────────────────
public static class LookaheadDosPlies
{
    private const int ALCANCE = 1; // adyacencia para "poder contestar" esta ronda

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
        double sPasivo = EvaluadorTablero.EvaluarPosicion(ctx, resPasivo.Tablero);

        // Mundo AGRESIVO: mi plan + los enemigos avanzando hacia mis activos.
        var planesEnemigos = PlanesEnemigos(ctx, tablero, obeliscos, eliminados, agresivo: true);
        var todos = new List<SimuladorTurno.Plan> { miPlan };
        todos.AddRange(planesEnemigos);
        var resAgresivo = SimuladorTurno.Simular(
            tablero, obeliscos, turno, todos,
            efectos, eliminados, aliadoDe: null, terreno: null, descargasPrev: descargas);
        double sAgresivo = EvaluadorTablero.EvaluarPosicion(ctx, resAgresivo.Tablero);

        return Math.Min(sPasivo, sAgresivo);
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

        var planes = new List<SimuladorTurno.Plan>();
        foreach (var (owner, stacks) in porDueno)
        {
            var celdas = new Tablero();
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
                        destino = PasoHacia(coord, mejorObj, mov, filas, columnas);
                }
                if (!celdas.TryGetValue(destino, out var lst)) { lst = new(); celdas[destino] = lst; }
                lst.AddRange(cs);
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
}