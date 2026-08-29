using System;
using System.Collections.Generic;
using System.Linq;

using Tablero = System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<System.Collections.Generic.Dictionary<string, object?>>>;

// ─────────────────────────────────────────────────────────────────────────────
// PlanificadorFarmeo.cs  —  MODO FARMEO (planificador de apertura)
//
// Genera el plan que le FALTABA al arranque: en vez de repartir cartas débiles o
// picar cuarteles, empuja a DOMINAR EL CENTRO. Es un candidato más; el lookahead
// lo elige cuando es mejor que las variantes (que es casi siempre al principio).
//
// DISPARADOR (tu diseño): poca energía (<25), pocas tropas (<6 en juego) y sin
// amenaza seria al cuartel. Es la situación de apertura, así que los bots empiezan
// las partidas en farmeo.
//
// COMPORTAMIENTO: distribuye las unidades hacia los objetivos de farmeo más
// cercanos (terreno-consciente): la ISLA CENTRAL (dominar el centro) y las celdas
// de RAYO (energía Zero, +10, las más ricas). El tope anti-área (≤3) se aplica
// SOLO a los rayos —ahí van los disparos lejanos—; en la isla y el resto se puede
// juntar lo que haga falta para ganar un combate. La guarnición del cuartel se queda.
//
// LÍMITES v1 (para las siguientes iteraciones, todo de tu diseño): aún no farmea
// celdas de energía cero del propio continente ni de continentes ajenos lejos de
// cuarteles, ni hace la evasión de la carta solitaria (moverse a otra celda si el
// rival la alcanza para hacerle adivinar). El centro primero es lo esencial; el
// resto se añade encima.
// ─────────────────────────────────────────────────────────────────────────────
public static class PlanificadorFarmeo
{
    private const int UMBRAL_ENERGIA = 30;         // "poca energía"
    private const int UMBRAL_TROPAS = 10;          // "pocas tropas en juego"
    private const int RADIO_AMENAZA_CUARTEL = 3;   // amenaza "cerca" del cuartel
    private const int MAX_POR_RAYO = 3;            // tope anti-área SOLO en celdas de rayo (energía Zero)
    private const int BONO_CUARTEL = 40;

    public static BotMove? Generar(BotContext ctx)
    {
        var tablero = TableroDesde(ctx.Estado);
        string botUid = ctx.BotUid;
        int filas = ctx.Filas, columnas = ctx.Columnas;

        // Disparador: poca energía Y pocas tropas, y sin amenaza seria al cuartel.
        int misTropas = tablero.Sum(kv => kv.Value.Count(c => EsMio(c, botUid)));
        bool aperturaEconomica = ctx.Energia < UMBRAL_ENERGIA && misTropas < UMBRAL_TROPAS;
        if (!aperturaEconomica) return null;
        if (HayAmenazaSeria(ctx, tablero)) return null;   // hay amenaza: que decida el modo defensa

        // Objetivos de farmeo: la ISLA CENTRAL (dominar el centro) y las celdas de
        // RAYO (energía Zero, +10). El tope anti-área (≤3) se aplica SOLO a los rayos.
        var objetivos = new List<string>(ctx.IslaCentral);
        foreach (var r in ctx.Rayos) if (!ctx.IslaCentral.Contains(r)) objetivos.Add(r);
        if (objetivos.Count == 0) return null;

        string miCuartel = ctx.Cuartel;
        var celdas = new Tablero();
        // Ocupación SOLO de las celdas de rayo (las únicas con tope).
        var ocupacionRayo = new Dictionary<string, int>();
        foreach (var r in ctx.Rayos)
            ocupacionRayo[r] = tablero.TryGetValue(r, out var lr) ? lr.Count(c => EsMio(c, botUid)) : 0;

        // Recolectar unidades (la guarnición del cuartel se queda; el resto se asigna).
        var unidades = new List<Dictionary<string, object?>>();
        foreach (var (coord, cartas) in tablero)
            foreach (var c in cartas)
            {
                if (!EsMio(c, botUid)) continue;
                if (coord == miCuartel) Add(celdas, coord, c);
                else unidades.Add(c);
            }

        // Asignar cada unidad al objetivo compatible más cercano. Un RAYO lleno
        // (≥3) se descarta como destino; la isla nunca se descarta por tope.
        foreach (var c in unidades)
        {
            string coordActual = tablero.First(kv => kv.Value.Contains(c)).Key;
            int mov = M.Int(M.Get(c, "Movimiento", "movimiento"));
            var (tierra, mar) = TerrenoUtil.ClaseDeTipo(M.Int(M.Get(c, "Tipo", "tipo")));

            string objetivo = ""; int mejor = int.MaxValue;
            foreach (var o in objetivos)
            {
                if (ctx.Rayos.Contains(o) && ocupacionRayo.GetValueOrDefault(o, 0) >= MAX_POR_RAYO) continue; // tope solo en rayos
                if (!TerrenoUtil.Compatible(o, tierra, mar, ctx.Terreno)) continue;
                int dd = Manhattan(coordActual, o, filas, columnas);
                if (dd < mejor) { mejor = dd; objetivo = o; }
            }

            string destino = coordActual;
            if (objetivo != "")
            {
                destino = TerrenoUtil.PasoHaciaTerreno(coordActual, objetivo, mov, tierra, mar, ctx.Terreno, filas, columnas);
                if (destino == objetivo && ctx.Rayos.Contains(objetivo))
                    ocupacionRayo[objetivo] = ocupacionRayo.GetValueOrDefault(objetivo, 0) + 1;
            }
            Add(celdas, destino, c);
        }

        return new BotMove
        {
            Celdas = celdas,
            Acciones = new List<Dictionary<string, object?>>(),
            ManoResultante = new List<string>(ctx.Mano),
            EnergiaGastada = 0,
        };
    }

    // Amenaza seria: fuerza enemiga cerca del cuartel que supera su defensa.
    private static bool HayAmenazaSeria(BotContext ctx, Tablero tablero)
    {
        string cuartel = ctx.Cuartel;
        if (cuartel == "") return false;
        int filas = ctx.Filas, columnas = ctx.Columnas;
        int fuerzaEnemiga = 0, defensaPropia = BONO_CUARTEL;
        foreach (var (coord, cartas) in tablero)
        {
            int dd = Manhattan(coord, cuartel, filas, columnas);
            foreach (var c in cartas)
            {
                if (EsMio(c, ctx.BotUid))
                {
                    if (coord == cuartel) defensaPropia += Defensa(c);
                }
                else if (EsEnemigo(c, ctx.BotUid) && dd <= RADIO_AMENAZA_CUARTEL)
                    fuerzaEnemiga += Fuerza(c);
            }
        }
        return fuerzaEnemiga > defensaPropia;
    }

    private static void Add(Tablero t, string coord, Dictionary<string, object?> c)
    {
        if (!t.TryGetValue(coord, out var lst)) { lst = new(); t[coord] = lst; }
        lst.Add(c);
    }
    private static bool EsMio(Dictionary<string, object?> c, string botUid) =>
        M.Str(M.Get(c, "ownerUid")) == botUid;
    private static bool EsEnemigo(Dictionary<string, object?> c, string botUid)
    { var o = M.Str(M.Get(c, "ownerUid")); return o != "" && o != botUid; }
    private static int Fuerza(Dictionary<string, object?> c) => M.Int(M.Get(c, "Fuerza", "fuerza"));
    private static int Defensa(Dictionary<string, object?> c) => M.Int(M.Get(c, "Defensa", "defensa"));

    private static Tablero TableroDesde(Dictionary<string, object?> estado)
    {
        var t = new Tablero();
        foreach (var kv in M.Map(M.Get(estado, "tablero")))
            t[kv.Key] = M.List(kv.Value).Select(M.Map).ToList();
        return t;
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