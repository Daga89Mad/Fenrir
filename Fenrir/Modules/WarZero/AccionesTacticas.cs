using System;
using System.Collections.Generic;
using System.Linq;

// ─────────────────────────────────────────────────────────────────────────────
// AccionesTacticas.cs  —  HELPER COMPARTIDO de habilidades/acciones (Opción A)
//
// Casa única de la lógica de habilidades del bot: catálogo (semántica REAL del
// servidor, WarZeroLogic.CatalogoHabilidades), rango, selección de objetivos y
// construcción del dict de acción que consume CerrarTurno. Lo usan
// PlanificadorDefensivo y EstrategaStrategy (vía alias Efe/Rng/Hab), para no
// duplicar la lógica.
//
// Semántica real relevante (de WarZeroLogic):
//   · Disparo   (1/2/3)  — daño directo, instantáneo.
//   · Veneno    (6/7/8)  — DefensaReducida = 3 durante 3 turnos (ablanda al stack).
//   · Parálisis (9/10/11)— congela 3 turnos (DuracionTurnos = 3, DefensaReducida 0).
//   · Escudo    (12/13/14)— +3 defensa 3 turnos.
//   · Potenciación (15-23)— buff a unidad propia.
// Rangos: cercano = adyacente (Manhattan 1), medio = radio 7, lejano = cualquiera.
//
// Dict de acción: habilidadId, uid, zona, origen, objetivos, turno, costePagado,
// [cartaAccionId]. `origen` = celda de la unidad, o el cuartel si es CARTA de acción
// de la mano (Condicion == 4); `cartaAccionId` marca la carta a descartar.
// ─────────────────────────────────────────────────────────────────────────────
public static class AccionesTacticas
{
    public enum Efecto { Disparo, Veneno, Paralisis, Escudo, Potenciacion, Otro }
    public enum Rango { Frontera, Radio7, Cualquiera, Propia }

    public readonly record struct Hab(
        int Id, Efecto Efecto, Rango Rango, int NumObjetivos, bool ExcluyeCG,
        int DuracionTurnos, int DefensaReducida);

    // Catálogo alineado con WarZeroLogic.CatalogoHabilidades (duración/defensa reales).
    public static readonly IReadOnlyDictionary<int, Hab> Catalogo = new Dictionary<int, Hab>
    {
        [1] = new(1, Efecto.Disparo, Rango.Frontera, 1, false, 0, 0),
        [2] = new(2, Efecto.Disparo, Rango.Radio7, 1, false, 0, 0),
        [3] = new(3, Efecto.Disparo, Rango.Cualquiera, 1, false, 0, 0),
        [6] = new(6, Efecto.Veneno, Rango.Frontera, 2, false, 3, 3),
        [7] = new(7, Efecto.Veneno, Rango.Radio7, 1, true, 3, 3),
        [8] = new(8, Efecto.Veneno, Rango.Cualquiera, 1, false, 3, 3),
        [9] = new(9, Efecto.Paralisis, Rango.Frontera, 1, false, 3, 0),
        [10] = new(10, Efecto.Paralisis, Rango.Radio7, 1, true, 3, 0),
        [11] = new(11, Efecto.Paralisis, Rango.Cualquiera, 1, false, 3, 0),
        [12] = new(12, Efecto.Escudo, Rango.Propia, 1, false, 3, 3),
        [13] = new(13, Efecto.Escudo, Rango.Frontera, 1, false, 3, 3),
        [14] = new(14, Efecto.Escudo, Rango.Cualquiera, 1, false, 3, 3),
        [15] = new(15, Efecto.Potenciacion, Rango.Frontera, 1, false, 3, 0),
        [16] = new(16, Efecto.Potenciacion, Rango.Radio7, 1, false, 3, 0),
        [17] = new(17, Efecto.Potenciacion, Rango.Cualquiera, 1, false, 3, 0),
        [18] = new(18, Efecto.Potenciacion, Rango.Frontera, 1, false, 3, 0),
        [19] = new(19, Efecto.Potenciacion, Rango.Radio7, 1, false, 3, 0),
        [20] = new(20, Efecto.Potenciacion, Rango.Cualquiera, 1, false, 3, 0),
        [21] = new(21, Efecto.Potenciacion, Rango.Frontera, 1, false, 3, 0),
        [22] = new(22, Efecto.Potenciacion, Rango.Radio7, 1, false, 3, 0),
        [23] = new(23, Efecto.Potenciacion, Rango.Cualquiera, 1, false, 3, 0),
    };

    public const int COND_CARTA_ACCION = 4;   // Condicion de una CARTA de acción (se juega desde la mano)

    /// Una FUENTE de acción: de dónde sale la habilidad. Si CartaId != null es una
    /// carta de acción de la mano (origen = cuartel, se descarta al usarse); si es
    /// null, es una unidad en tablero que usa su habilidad desde su celda.
    public readonly record struct Fuente(string Origen, int HabId, int Coste, string? CartaId);

    /// Elige VARIAS acciones defensivas contra el stack en `objetivoCoord`, para
    /// COMBINARLAS el mismo turno dentro del presupuesto de energía. Prioridad:
    ///   PARÁLISIS (congela 3 turnos) → VENENO (−3 def, lo ablanda) → DISPARO (daño).
    /// Toma como mucho UNA por efecto, la más barata que alcance el objetivo, sin
    /// reutilizar una fuente. Así "paralizas y ablandas y luego rematas". Devuelve la
    /// lista (0..3) de (acción lista, coste, carta a descartar).
    public static List<(Dictionary<string, object?> accion, int coste, string? cartaId)> ElegirAccionesDefensivas(
        string objetivoCoord,
        IReadOnlyList<Fuente> fuentes,
        int energiaDisponible,
        HashSet<string> cuartelesEnemigos,
        string uid, string zona, int turno,
        int filas, int columnas)
    {
        var res = new List<(Dictionary<string, object?> accion, int coste, string? cartaId)>();
        int presupuesto = energiaDisponible;
        var usados = new HashSet<int>();

        // Orden de prioridad de efectos para la defensa.
        foreach (var efecto in new[] { Efecto.Paralisis, Efecto.Veneno, Efecto.Disparo })
        {
            int mejor = -1, mejorCoste = int.MaxValue;
            for (int i = 0; i < fuentes.Count; i++)
            {
                if (usados.Contains(i)) continue;
                var f = fuentes[i];
                if (f.Coste > presupuesto) continue;
                if (!Catalogo.TryGetValue(f.HabId, out var h)) continue;
                if (h.Efecto != efecto) continue;
                if (h.ExcluyeCG && cuartelesEnemigos.Contains(objetivoCoord)) continue;
                if (!EnRango(h.Rango, f.Origen, objetivoCoord, filas, columnas)) continue;
                if (f.Coste < mejorCoste) { mejorCoste = f.Coste; mejor = i; }
            }
            if (mejor < 0) continue;

            var fu = fuentes[mejor];
            var accion = CrearAccion(fu.HabId, uid, zona, fu.Origen,
                new List<string> { objetivoCoord }, turno, fu.Coste, fu.CartaId);
            res.Add((accion, fu.Coste, fu.CartaId));
            presupuesto -= fu.Coste;
            usados.Add(mejor);
        }
        return res;
    }

    /// Objetivos enemigos en rango desde `origen`, ordenados por valor (coste total
    /// del stack), excluyendo cuarteles si la habilidad los excluye.
    public static List<string> MejoresObjetivos(
        Hab hab, string origen,
        Dictionary<string, List<Dictionary<string, object?>>> enemyByCoord,
        HashSet<string> cuartelesEnemigos, int filas, int columnas)
        => enemyByCoord.Keys
            .Where(c => EnRango(hab.Rango, origen, c, filas, columnas))
            .Where(c => !hab.ExcluyeCG || !cuartelesEnemigos.Contains(c))
            .OrderByDescending(c => enemyByCoord[c].Sum(Coste))
            .ToList();

    /// Construye el dict de acción tal cual lo consume CerrarTurno.
    public static Dictionary<string, object?> CrearAccion(
        int habId, string uid, string zona, string origen,
        List<string> objetivos, int turno, int coste, string? cartaId = null)
    {
        var a = new Dictionary<string, object?>
        {
            ["habilidadId"] = habId,
            ["uid"] = uid,
            ["zona"] = zona,
            ["origen"] = origen,
            ["objetivos"] = objetivos,
            ["turno"] = turno,
            ["costePagado"] = coste,
        };
        if (cartaId != null) a["cartaAccionId"] = cartaId;   // carta de mano a descartar
        return a;
    }

    public static bool EnRango(Rango rango, string origen, string coord, int filas, int columnas) => rango switch
    {
        Rango.Frontera => Manhattan(origen, coord, filas, columnas) == 1,
        Rango.Radio7 => Manhattan(origen, coord, filas, columnas) <= 7,
        Rango.Cualquiera => coord != origen,
        Rango.Propia => coord == origen,
        _ => false,
    };

    public static bool EsCartaAccion(Dictionary<string, object?> baseCard)
        => M.Int(M.Get(baseCard, "Condicion", "condicion")) == COND_CARTA_ACCION;

    private static int Coste(Dictionary<string, object?> c) => M.Int(M.Get(c, "Coste", "coste"));

    public static int Manhattan(string a, string b, int filas, int columnas)
    {
        var pa = Parse(a); var pb = Parse(b);
        if (pa == null || pb == null) return int.MaxValue;
        return Math.Abs(pa.Value.ri - pb.Value.ri) + Math.Abs(pa.Value.ci - pb.Value.ci);
    }
    public static (int ri, int ci)? Parse(string coord)
    {
        if (string.IsNullOrEmpty(coord) || coord.Length < 2) return null;
        int ri = char.ToUpperInvariant(coord[0]) - 'A';
        if (!int.TryParse(coord[1..], out int col)) return null;
        return (ri, col - 1);
    }
}