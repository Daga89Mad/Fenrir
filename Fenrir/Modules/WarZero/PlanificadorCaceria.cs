using System;
using System.Collections.Generic;
using System.Linq;

using Tablero = System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<System.Collections.Generic.Dictionary<string, object?>>>;

// ─────────────────────────────────────────────────────────────────────────────
// PlanificadorCaceria.cs  —  MODO CACERÍA (planificador)  v2
//
// Genera UN plan candidato de CAZA: concentrar la fuerza del bot sobre una presa
// vulnerable para rematarla. Igual que el defensivo, es un candidato MÁS que la
// softmax evalúa; el lookahead lo elige solo cuando la caza de verdad sale.
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
    private const int UMBRAL_ENERGIA_CACERIA = 100;  // "energía de sobra" (tunable)
    private const int BONO_CUARTEL = 40;
    private const int VALOR_CUARTEL = 10000;         // conquistar > matar general
    private const int VALOR_GENERAL = 1000;
    private const int COND_GENERAL = 5;
    private const int ALCANCE = 1;
    private const int MAX_DESPLIEGUE_CAZA = 2;       // refuerzos nuevos hacia la presa

    /// Genera el plan de caza, o null si el disparador no se cumple o no hay presa.
    public static BotMove? Generar(BotContext ctx)
    {
        // Disparador: energía de sobra. (El candado "dominar el centro" se retiró
        // en v2: era circular — sin centro nunca había caza y sin caza el bot solo
        // acumulaba energía. El lookahead ya filtra las cazas malas al simularlas.)
        if (ctx.Energia < UMBRAL_ENERGIA_CACERIA) return null;

        var tablero = TableroDesde(ctx.Estado);
        var obeliscos = ObeliscosDesde(ctx.Estado);
        var eliminados = M.List(M.Get(ctx.Estado, "jugadoresEliminados")).Select(M.Str).ToHashSet();
        string botUid = ctx.BotUid;
        int filas = ctx.Filas, columnas = ctx.Columnas;

        // Fuerza propia por celda + su alcance.
        var misUnidades = new List<(string coord, int fuerza, int mov)>();
        foreach (var (coord, cartas) in tablero)
        {
            int f = 0, mov = 0; bool mia = false;
            foreach (var c in cartas)
                if (M.Str(M.Get(c, "ownerUid")) == botUid)
                { mia = true; f += Fuerza(c); mov = Math.Max(mov, Mov(c)); }
            if (mia) misUnidades.Add((coord, f, mov));
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
                int f = desplegadas.Sum(Fuerza);
                int mov = desplegadas.Max(Mov);
                misUnidades.Add((miCuartel, f, mov));
            }
        }

        // Presas candidatas: cuarteles enemigos + generales enemigos, con su defensa.
        var presas = new List<(string coord, int valor, int defensa)>();
        foreach (var (uid, coord) in obeliscos)
            if (uid != botUid && !eliminados.Contains(uid) && coord != "")
                presas.Add((coord, VALOR_CUARTEL, BONO_CUARTEL + FuerzaEnemigaEn(tablero, coord, botUid)));
        foreach (var (coord, cartas) in tablero)
            foreach (var c in cartas)
                if (EsEnemigo(c, botUid) && M.Int(M.Get(c, "Condicion", "condicion")) == COND_GENERAL)
                    presas.Add((coord, VALOR_GENERAL + Fuerza(c), FuerzaMasDefensaEnemigaEn(tablero, coord, botUid)));

        // Elegir la mejor presa ATACABLE (fuerza alcanzable > defensa), por valor.
        string? mejorPresa = null; int mejorValor = int.MinValue, mejorMargen = int.MinValue;
        foreach (var (coord, valor, defensa) in presas)
        {
            int alcanzable = 0;
            foreach (var u in misUnidades)
                if (Manhattan(u.coord, coord, filas, columnas) <= u.mov + ALCANCE)
                    alcanzable += u.fuerza;
            if (alcanzable <= defensa) continue;   // no gano el combate: no es presa
            int margen = alcanzable - defensa;
            if (valor > mejorValor || (valor == mejorValor && margen > mejorMargen))
            { mejorValor = valor; mejorMargen = margen; mejorPresa = coord; }
        }
        if (mejorPresa == null) return null;

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