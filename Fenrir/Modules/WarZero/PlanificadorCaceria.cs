using System;
using System.Collections.Generic;
using System.Linq;

using Tablero = System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<System.Collections.Generic.Dictionary<string, object?>>>;

// ─────────────────────────────────────────────────────────────────────────────
// PlanificadorCaceria.cs  —  MODO CACERÍA (planificador)  v3
//
// Genera UN plan candidato de CAZA: concentrar la fuerza del bot sobre una presa
// vulnerable para rematarla. Igual que el defensivo, es un candidato MÁS que la
// softmax evalúa; el lookahead lo elige solo cuando la caza de verdad sale.
//
// Cambios v3 (partidas de estudio EennDB / N0DGsq / ThEozf / atpcCJ):
//   · FUERA EL UMBRAL ABSOLUTO DE ENERGÍA (100). Los bots superaron 100 en 42 de
//     389 turnos y aun así la cacería se eligió 0 veces: el filtro "presa
//     alcanzable" exigía llegar ESTE turno y el ejército estaba lejos. Ahora
//     la presa cuenta si la fuerza propia la alcanza en HORIZONTE turnos
//     (3 para un cuartel, que no se mueve; 1 para un general, que sí). El
//     plan ya era de aproximación (cada unidad da un paso hacia la presa), así
//     que basta con dejarlo proponerse; el lookahead (con término de conquista
//     v9) es quien lo elige o lo descarta.
//   · ATACABILIDAD POR PODER (F+D), como resuelve el combate el servidor, y no
//     solo por fuerza; con un MARGEN mínimo para no marchar a un empate.
//   · La presa más BARATA gana entre cuarteles (mayor margen): el humano
//     eliminó primero a los dos bots más débiles, y eso es lo que da energía
//     (100 por conquista + PC) para el siguiente.
//
// Cambios v2 (partidas de estudio XnIl/GG6):
//   · FUERA EL CANDADO DominaCentro. El disparador antiguo exigía energía ≥100 Y
//     dominar la isla central, pero NINGÚN candidato peleaba el centro, así que
//     la cacería no se proponía JAMÁS (candado circular). Se observó un bot con
//     125 de energía y la mano llena sin proponer caza nunca. Ahora basta la
//     energía: la presa sigue exigiendo ser ATACABLE de verdad y, sobre todo, el
//     LOOKAHEAD simula el plan y lo descarta si es malo — ese es el filtro real.
//   · DESPLIEGUE BÁSICO: con energía de sobra, cazar solo con lo que hay en
//     tablero desaprovecha el banco. Se despliegan hasta 2 unidades potentes
//     (caen en el cuartel y avanzan hacia la presa ese mismo turno, la regla del
//     juego lo permite), actualizando mano y energía gastada.
//
// PRESA: la mejor pieza enemiga ATACABLE (fuerza propia alcanzable > su defensa),
// por valor: un CUARTEL enemigo (conquistarlo elimina a un rival) por encima de un
// GENERAL aislado (pieza irreemplazable). Si ninguna es atacable, no hay plan.
//
// LÍMITES: el alcance de la fuerza propia se estima por Manhattan (el
// movimiento del plan sí es terreno-consciente); si sobrestima, el lookahead lo
// rechaza al simular. La guarnición del cuartel se queda; el resto se concentra.
// ─────────────────────────────────────────────────────────────────────────────
public static class PlanificadorCaceria
{
    private const int HORIZONTE_CUARTEL = 3;         // turnos de aproximación a un cuartel (v3)
    private const int HORIZONTE_GENERAL = 1;         // un general se mueve: solo si llego ya
    private const double MARGEN_PODER = 1.15;        // poder propio mínimo / poder defensor (v3)
    private const int MIN_UNIDADES = 2;              // nunca una carta suelta (v3)
    private const int BONO_CUARTEL = 40;
    private const int VALOR_CUARTEL = 10000;         // conquistar > matar general
    private const int VALOR_GENERAL = 1000;
    private const int COND_GENERAL = 5;
    private const int ALCANCE = 1;
    private const int MAX_DESPLIEGUE_CAZA = 2;       // refuerzos nuevos hacia la presa

    /// Genera el plan de caza, o null si el disparador no se cumple o no hay presa.
    public static BotMove? Generar(BotContext ctx)
    {
        // v3: sin disparador de energía. El candado "dominar el centro" (v1) y el
        // umbral absoluto de energía (v2) impedían proponer la caza justo cuando
        // más falta hacía. El filtro real es la presa ATACABLE + el lookahead.
        var tablero = TableroDesde(ctx.Estado);
        var obeliscos = ObeliscosDesde(ctx.Estado);
        var eliminados = M.List(M.Get(ctx.Estado, "jugadoresEliminados")).Select(M.Str).ToHashSet();
        string botUid = ctx.BotUid;
        int filas = ctx.Filas, columnas = ctx.Columnas;

        // Poder propio (F+D, como resuelve el combate) por celda + su alcance.
        var misUnidades = new List<(string coord, int poder, int mov, int n)>();
        foreach (var (coord, cartas) in tablero)
        {
            int p = 0, mov = 0, n = 0;
            foreach (var c in cartas)
                if (M.Str(M.Get(c, "ownerUid")) == botUid)
                { n++; p += Fuerza(c) + Defensa(c); mov = Math.Max(mov, Mov(c)); }
            if (n > 0) misUnidades.Add((coord, p, mov, n));
        }

        // ── DESPLIEGUE (v2): hasta MAX_DESPLIEGUE_CAZA unidades potentes de la
        //    mano. Caen en el cuartel y avanzan hacia la presa este mismo turno,
        //    así que su fuerza también cuenta para "alcanzable" desde el cuartel.
        var mano = new List<string>(ctx.Mano);
        int energia = ctx.Energia, gastado = 0;
        var desplegadas = new List<Dictionary<string, object?>>();
        string miCuartel = ctx.Cuartel;
        if (miCuartel != "")
        {
            var candidatas = mano
                .Where(id => ctx.CatalogoMano.TryGetValue(id, out var b)
                             && !AccionesTacticas.EsCartaAccion(b) && !EsEstatica(b))
                .OrderByDescending(id => { var c = ctx.CatalogoMano[id]; return Fuerza(c) + Defensa(c); })
                .ToList();
            foreach (var id in candidatas)
            {
                if (desplegadas.Count >= MAX_DESPLIEGUE_CAZA) break;
                var baseCard = ctx.CatalogoMano[id];
                int coste = M.Int(M.Get(baseCard, "Coste", "coste"));
                if (coste > energia) continue;
                var nu = NuevaUnidad(baseCard, id, botUid, ctx.Zona);
                desplegadas.Add(nu);
                energia -= coste; gastado += coste;
                mano.Remove(id);
            }
            if (desplegadas.Count > 0)
            {
                int p = desplegadas.Sum(c => Fuerza(c) + Defensa(c));
                int mov = desplegadas.Max(Mov);
                misUnidades.Add((miCuartel, p, mov, desplegadas.Count));
            }
        }

        // Presas candidatas: cuarteles enemigos + generales enemigos, con el PODER
        // (F+D) que hay que superar y el horizonte de aproximación (v3).
        var presas = new List<(string coord, int valor, int defensa, int horizonte)>();
        foreach (var (uid, coord) in obeliscos)
            if (uid != botUid && !eliminados.Contains(uid) && coord != "")
                presas.Add((coord, VALOR_CUARTEL, BONO_CUARTEL + FuerzaMasDefensaEnemigaEn(tablero, coord, botUid), HORIZONTE_CUARTEL));
        foreach (var (coord, cartas) in tablero)
            foreach (var c in cartas)
                if (EsEnemigo(c, botUid) && M.Int(M.Get(c, "Condicion", "condicion")) == COND_GENERAL)
                    presas.Add((coord, VALOR_GENERAL + Fuerza(c), FuerzaMasDefensaEnemigaEn(tablero, coord, botUid), HORIZONTE_GENERAL));

        // Elegir la mejor presa ATACABLE (poder que llega en `horizonte` turnos
        // supera con margen al defensor), por valor y luego por margen: entre
        // cuarteles gana el más BARATO de tomar.
        string? mejorPresa = null; int mejorValor = int.MinValue, mejorMargen = int.MinValue;
        int mejorPoder = 0, mejorNecesario = 0;
        foreach (var (coord, valor, defensa, horizonte) in presas)
        {
            int alcanzable = 0, unidades = 0;
            foreach (var u in misUnidades)
                if (u.mov > 0 && Manhattan(u.coord, coord, filas, columnas) <= u.mov * horizonte + ALCANCE)
                { alcanzable += u.poder; unidades += u.n; }
            int necesario = (int)Math.Ceiling(defensa * MARGEN_PODER);
            if (unidades < MIN_UNIDADES || alcanzable < necesario) continue;   // no gano: no es presa
            int margen = alcanzable - defensa;
            if (valor > mejorValor || (valor == mejorValor && margen > mejorMargen))
            { mejorValor = valor; mejorMargen = margen; mejorPresa = coord; mejorPoder = alcanzable; mejorNecesario = necesario; }
        }
        if (mejorPresa == null) return null;
        Console.WriteLine($"[WZ][bot {botUid}] CACERÍA propuesta: presa {mejorPresa} (poder alcanzable {mejorPoder} vs necesario {mejorNecesario}, {desplegadas.Count} refuerzos)");

        // Plan: concentrar fuerza sobre la presa (terreno-consciente). La guarnición
        // del cuartel se queda; lo que ya está en la presa, también.
        var celdas = new Tablero();
        void Add(string coord, Dictionary<string, object?> c)
        {
            if (!celdas.TryGetValue(coord, out var lst)) { lst = new(); celdas[coord] = lst; }
            lst.Add(c);
        }
        foreach (var (coord, cartas) in tablero)
            foreach (var c in cartas)
            {
                if (!EsMio(c, botUid)) continue;
                string destino = coord;
                if (coord != miCuartel && coord != mejorPresa)
                {
                    int mov = M.Int(M.Get(c, "Movimiento", "movimiento"));
                    var (tierra, mar) = TerrenoUtil.ClaseDeTipo(M.Int(M.Get(c, "Tipo", "tipo")));
                    destino = TerrenoUtil.PasoHaciaTerreno(coord, mejorPresa, mov, tierra, mar, ctx.Terreno, filas, columnas);
                }
                Add(destino, c);
            }
        // Las recién desplegadas caen en el cuartel y AVANZAN hacia la presa.
        foreach (var nu in desplegadas)
        {
            int mov = M.Int(M.Get(nu, "Movimiento", "movimiento"));
            var (tierra, mar) = TerrenoUtil.ClaseDeTipo(M.Int(M.Get(nu, "Tipo", "tipo")));
            string destino = TerrenoUtil.PasoHaciaTerreno(miCuartel, mejorPresa, mov, tierra, mar, ctx.Terreno, filas, columnas);
            Add(destino, nu);
        }

        return new BotMove
        {
            Celdas = celdas,
            Acciones = new List<Dictionary<string, object?>>(),
            ManoResultante = mano,
            EnergiaGastada = gastado,
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

    private static bool EsEstatica(Dictionary<string, object?> baseCard)
        => M.Int(M.Get(baseCard, "Condicion", "condicion")) == 3;

    private static bool EsMio(Dictionary<string, object?> c, string botUid) =>
        M.Str(M.Get(c, "ownerUid")) == botUid;
    private static bool EsEnemigo(Dictionary<string, object?> c, string botUid)
    {
        var o = M.Str(M.Get(c, "ownerUid"));
        return o != "" && o != botUid;
    }
    private static int FuerzaEnemigaEn(Tablero t, string coord, string botUid) =>
        t.TryGetValue(coord, out var l) ? l.Where(c => EsEnemigo(c, botUid)).Sum(Fuerza) : 0;
    private static int FuerzaMasDefensaEnemigaEn(Tablero t, string coord, string botUid) =>
        t.TryGetValue(coord, out var l)
            ? l.Where(c => EsEnemigo(c, botUid)).Sum(c => Fuerza(c) + Defensa(c)) : 0;

    private static int Fuerza(Dictionary<string, object?> c) => M.Int(M.Get(c, "Fuerza", "fuerza"));
    private static int Defensa(Dictionary<string, object?> c) => M.Int(M.Get(c, "Defensa", "defensa"));
    private static int Mov(Dictionary<string, object?> c) => M.Int(M.Get(c, "Movimiento", "movimiento"));

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
    private static int Manhattan(string a, string b, int filas, int columnas)
    {
        var pa = Parse(a); var pb = Parse(b);
        if (pa == null || pb == null) return int.MaxValue;
        return Math.Abs(pa.Value.ri - pb.Value.ri) + Math.Abs(pa.Value.ci - pb.Value.ci);
    }
    private static (int ri, int ci)? Parse(string coord)
    {
        if (string.IsNullOrEmpty(coord) || coord.Length < 2) return null;
        int ri = char.ToUpperInvariant(coord[0]) - 'A';
        if (!int.TryParse(coord[1..], out int col)) return null;
        return (ri, col - 1);
    }
}