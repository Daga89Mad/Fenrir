using System;
using System.Collections.Generic;

// ─────────────────────────────────────────────────────────────────────────────
// TerrenoUtil.cs  —  compatibilidad carta↔terreno y movimiento terreno-consciente
//
// Réplica EXACTA de la regla del juego (WarZeroLogic.TeleCanLand): según el campo
// Tipo de la carta, tipo 1/2 = TIERRA (puede pisar land/amphibious), tipo 3 = MAR
// (sea/deepSea/amphibious), cualquier otro = sin restricción. Un conjunto con
// cartas de tierra Y de mar solo cabe en amphibious.
//
// Sirve para dos cosas del modo defensa: replegar unidades sin pisar terreno
// inválido, y —lo importante— ACOTAR las opciones del rival en el modelo enemigo:
// un stack de mar no puede cruzar tierra para llegar a tu cuartel, así que no lo
// amenaza y el bot no malgasta defensa donde el rival no puede entrar.
// ─────────────────────────────────────────────────────────────────────────────
public static class TerrenoUtil
{
    /// (tierra, mar) que exige una carta según su Tipo.
    public static (bool tierra, bool mar) ClaseDeTipo(int tipo) => tipo switch
    {
        1 or 2 => (true, false),
        3 => (false, true),
        _ => (false, false),
    };

    /// ¿Puede una carta/stack (con estas exigencias) estar en `coord`?
    public static bool Compatible(
        string coord, bool tieneTierra, bool tieneMar, Dictionary<string, string> terreno)
    {
        var terr = terreno.TryGetValue(coord, out var v) ? v : "land";
        if (tieneTierra && !(terr is "land" or "amphibious")) return false;
        if (tieneMar && !(terr is "sea" or "deepSea" or "amphibious")) return false;
        return true;
    }

    /// Da hasta `pasos` pasos desde `desde` hacia `hacia` RESPETANDO el terreno:
    /// en cada paso intenta el eje de mayor delta y, si esa celda es incompatible,
    /// el otro eje; si ninguna vale, se detiene (bloqueado por el terreno). Greedy
    /// (no rodea obstáculos por amphibious; eso sería pathfinding completo, v2).
    public static string PasoHaciaTerreno(
        string desde, string hacia, int pasos, bool tieneTierra, bool tieneMar,
        Dictionary<string, string> terreno, int filas, int columnas)
    {
        var pa = Parse(desde); var pb = Parse(hacia);
        if (pa == null || pb == null) return desde;
        int ri = pa.Value.ri, ci = pa.Value.ci;
        int tri = pb.Value.ri, tci = pb.Value.ci;
        for (int k = 0; k < pasos; k++)
        {
            int dr = tri - ri, dc = tci - ci;
            if (dr == 0 && dc == 0) break;
            var opciones = Math.Abs(dr) >= Math.Abs(dc)
                ? new[] { (ri + Math.Sign(dr), ci), (ri, ci + Math.Sign(dc)) }
                : new[] { (ri, ci + Math.Sign(dc)), (ri + Math.Sign(dr), ci) };
            bool movido = false;
            foreach (var (nr, nc) in opciones)
            {
                int cr = Math.Clamp(nr, 0, Math.Max(0, filas - 1));
                int cc = Math.Clamp(nc, 0, Math.Max(0, columnas - 1));
                if (cr == ri && cc == ci) continue;                 // sin movimiento efectivo
                string cand = Format(cr, cc);
                if (Compatible(cand, tieneTierra, tieneMar, terreno))
                {
                    ri = cr; ci = cc; movido = true; break;
                }
            }
            if (!movido) break;   // bloqueado por terreno: se queda
        }
        return Format(ri, ci);
    }

    private static (int ri, int ci)? Parse(string coord)
    {
        if (string.IsNullOrEmpty(coord) || coord.Length < 2) return null;
        int ri = char.ToUpperInvariant(coord[0]) - 'A';
        if (!int.TryParse(coord[1..], out int col)) return null;
        return (ri, col - 1);
    }
    private static string Format(int ri, int ci) => $"{(char)('A' + ri)}{ci + 1}";
}