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
//   · Disparo   (1/2/3)  — destruye TODAS las cartas de la celda objetivo (las
//                          propias también). Se resuelve DESPUÉS del movimiento:
//                          solo acierta sobre lo que NO se mueve (cuarteles,
//                          estáticas, stacks aparcados).
//   · Veneno    (6/7/8)  — DefensaReducida = 3 durante 3 turnos (ablanda al stack).
//   · Parálisis (9/10/11)— congela 3 turnos (DuracionTurnos = 3, DefensaReducida 0).
//   · Escudo    (12/13/14)— protege la celda 3 turnos: bloquea acciones y ENTRADA
//                          enemigas. REGLA DEL JUEGO: nunca sobre el cuartel propio.
//   · Potenciación (15-23)— buff a unidad propia.
//   · DESCARGA (acción especial, sin habilidadId): mata todo lo que haya en el
//     cuartel PROPIO antes del combate (amigos y enemigos), una vez por partida,
//     coste fijo CosteDescarga; deja el cuartel con defensa 0 y recupera
//     +10/turno hasta 40.
// Rangos: cercano = adyacente (Manhattan 1), medio = radio 7, lejano = cualquiera.
//
// Dict de acción: habilidadId, uid, zona, origen, objetivos, turno, costePagado,
// [cartaAccionId]. `origen` = celda de la unidad, o el cuartel si es CARTA de acción
// de la mano (Condicion == 4); `cartaAccionId` marca la carta a descartar.
//
// v2 (palancas defensivas, partidas de estudio iPKy / reto resistencia demoníaca):
//   · Constantes compartidas del "misil": coste estimado del disparo lejano
//     rival y UMBRAL DE CEBO (coste total de cartas que NUNCA debe quedarse
//     parado en una misma celda —cuartel incluido— cuando un rival puede pagar
//     un disparo: por debajo, el misil no le compensa).
//   · CrearDescarga / EsDescarga: la descarga como acción del bot.
//   · RivalPuedeDisparar / CosteDisparoEstimado: ¿algún rival puede pagar un
//     disparo lejano este turno? (energía pública ≥ coste y cartas en mano).
//   · ElegirAccionesDefensivas acepta un objetivo DISTINTO para el disparo
//     (celda predicha a la que puede moverse el rival) y una lista de efectos.
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

    // ── v2: constantes compartidas del juego del misil ──────────────────────
    /// Coste fijo de la DESCARGA (= WarZeroService.CosteDescarga / kDescargaCoste).
    public const int CosteDescarga = 20;

    /// Coste ESTIMADO de un disparo lejano rival cuando no se conoce su carta.
    /// Observado en las partidas de estudio: ~75 (ejército 3) – ~90 (humanos).
    /// Se toma el más bajo (del lado seguro: el rival puede pagarlo antes).
    public const int CosteDisparoLejanoEstimado = 75;

    /// UMBRAL DE CEBO: coste total de cartas propias que puede quedarse PARADO
    /// en una misma celda (cuartel incluido) sin que a un rival le compense
    /// gastar un disparo lejano en ella. Por encima, la celda es un cebo.
    public const int UmbralCosteCebo = 55;

    /// Turnos seguidos en la misma celda a partir de los cuales un stack rival
    /// se considera APARCADO (predecible: probablemente sigue ahí el turno que
    /// viene). El servidor mantiene `turnosEnCelda` en cada carta.
    public const int TurnosParaAparcado = 2;

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
        => ElegirAccionesDefensivas(objetivoCoord, fuentes, energiaDisponible, cuartelesEnemigos,
                                    uid, zona, turno, filas, columnas,
                                    new[] { Efecto.Paralisis, Efecto.Veneno, Efecto.Disparo }, null);

    /// v2: misma selección, pero con la lista de EFECTOS a intentar (en orden de
    /// prioridad) y, opcionalmente, un objetivo DISTINTO para el DISPARO
    /// (`objetivoDisparo`): la celda a la que el rival puede moverse. Un disparo
    /// se resuelve tras el movimiento, así que disparar a donde el stack ESTÁ
    /// solo sirve si va a quedarse; parálisis y veneno sí van a la celda actual.
    public static List<(Dictionary<string, object?> accion, int coste, string? cartaId)> ElegirAccionesDefensivas(
        string objetivoCoord,
        IReadOnlyList<Fuente> fuentes,
        int energiaDisponible,
        HashSet<string> cuartelesEnemigos,
        string uid, string zona, int turno,
        int filas, int columnas,
        IEnumerable<Efecto> efectos,
        string? objetivoDisparo)
    {
        var res = new List<(Dictionary<string, object?> accion, int coste, string? cartaId)>();
        int presupuesto = energiaDisponible;
        var usados = new HashSet<int>();

        foreach (var efecto in efectos)
        {
            string objetivo = efecto == Efecto.Disparo && objetivoDisparo != null ? objetivoDisparo : objetivoCoord;
            int mejor = -1, mejorCoste = int.MaxValue;
            for (int i = 0; i < fuentes.Count; i++)
            {
                if (usados.Contains(i)) continue;
                var f = fuentes[i];
                if (f.Coste > presupuesto) continue;
                if (!Catalogo.TryGetValue(f.HabId, out var h)) continue;
                if (h.Efecto != efecto) continue;
                if (h.ExcluyeCG && cuartelesEnemigos.Contains(objetivo)) continue;
                if (!EnRango(h.Rango, f.Origen, objetivo, filas, columnas)) continue;
                if (f.Coste < mejorCoste) { mejorCoste = f.Coste; mejor = i; }
            }
            if (mejor < 0) continue;

            var fu = fuentes[mejor];
            var accion = CrearAccion(fu.HabId, uid, zona, fu.Origen,
                new List<string> { objetivo }, turno, fu.Coste, fu.CartaId);
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

    /// v2: ¿es un objetivo PREDECIBLE para un disparo? Un disparo se resuelve tras
    /// el movimiento, así que solo acierta sobre lo que no se mueve: un cuartel
    /// (su guarnición no se mueve), cartas estáticas (mov 0) o un stack aparcado
    /// (todas sus cartas llevan ≥ TurnosParaAparcado turnos en la celda).
    public static bool ObjetivoPredecible(
        string coord, List<Dictionary<string, object?>> cartas, ISet<string> cuarteles)
    {
        if (cuarteles.Contains(coord)) return true;
        if (cartas.Count == 0) return false;
        return cartas.All(c => M.Int(M.Get(c, "Movimiento", "movimiento")) <= 0
                               || TurnosEnCelda(c) >= TurnosParaAparcado);
    }

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

    /// v2: DESCARGA del cuartel propio, con el MISMO shape que envía el cliente
    /// (game_screen._toggleDescarga): habilidadId 0, esDescarga true, origen y
    /// objetivo = el cuartel. El servidor la valida (una por partida, cuartel
    /// propio, energía ≥ CosteDescarga) y la cobra al resolver.
    public static Dictionary<string, object?> CrearDescarga(string uid, string zona, string cuartel, int turno)
        => new()
        {
            ["habilidadId"] = 0,
            ["uid"] = uid,
            ["zona"] = zona,
            ["origen"] = cuartel,
            ["objetivos"] = new List<string> { cuartel },
            ["turno"] = turno,
            ["costePagado"] = CosteDescarga,
            ["esDescarga"] = true,
        };

    public static bool EsDescarga(Dictionary<string, object?> accion)
    {
        var v = M.Get(accion, "esDescarga");
        return (v is bool b && b) || (v is string s && s == "true");
    }

    /// ¿Ya ha usado `uid` su descarga en esta partida? (statsPartida.descargaUsada)
    public static bool DescargaUsada(Dictionary<string, object?> estado, string uid)
    {
        var stats = M.Map(M.Get(estado, "statsPartida"));
        if (!stats.TryGetValue(uid, out var raw)) return false;
        return M.Bool(M.Get(M.Map(raw), "descargaUsada"));
    }

    /// Defensa EFECTIVA del cuartel `coord` en la resolución del turno `turno`,
    /// contando la recuperación tras una descarga (0/10/20/30 → 40). Lee
    /// `descargasCuartel` del estado (coord → turno de la descarga).
    public static int DefensaCuartelActual(Dictionary<string, object?> estado, string coord, int turno)
    {
        foreach (var kv in M.Map(M.Get(estado, "descargasCuartel")))
        {
            if (kv.Key != coord) continue;
            int td = M.Int(kv.Value);
            if (td <= 0) continue;
            int diff = Math.Max(0, turno - td);
            if (diff >= 4) return Combate.DefensaObelisco;
            return Combate.DefensaObelisco * diff / 4;
        }
        return Combate.DefensaObelisco;
    }

    /// Coste estimado de un disparo lejano RIVAL. Si el bot tiene en su propio
    /// catálogo un disparo lejano, usa su coste (misma escala de precios del
    /// juego); si no, la constante. Siempre del lado seguro (el menor).
    public static int CosteDisparoEstimado(BotContext? ctx)
    {
        if (ctx == null) return CosteDisparoLejanoEstimado;
        int mejor = int.MaxValue;
        foreach (var kv in ctx.CatalogoMano)
        {
            var b = kv.Value;
            if (!EsCartaAccion(b)) continue;
            if (!Catalogo.TryGetValue(M.Int(M.Get(b, "IdHabilidad", "idHabilidad")), out var h)) continue;
            if (h.Efecto != Efecto.Disparo || h.Rango != Rango.Cualquiera) continue;
            int c = Coste(b);
            if (c > 0 && c < mejor) mejor = c;
        }
        return mejor == int.MaxValue ? CosteDisparoLejanoEstimado : Math.Min(mejor, CosteDisparoLejanoEstimado);
    }

    /// ¿Puede el rival `uid` pagar un disparo lejano este turno? Su energía y el
    /// tamaño de su mano son públicos; el contenido de la mano no. Del lado
    /// seguro: con energía suficiente y alguna carta en mano, se asume que sí.
    public static bool RivalPuedeDisparar(Dictionary<string, object?> estado, string uid, int costeEstimado)
    {
        var stats = M.Map(M.Get(estado, "statsPartida"));
        if (!stats.TryGetValue(uid, out var raw)) return false;
        var s = M.Map(raw);
        return M.Int(M.Get(s, "energies")) >= costeEstimado && M.List(M.Get(s, "mano")).Count > 0;
    }

    /// ¿Ha disparado ya `uid` en esta partida? (señal fuerte de que tiene y usa
    /// disparos; se lee del historial de combates que arrastra el estado).
    public static bool RivalHaDisparado(Dictionary<string, object?> estado, string uid)
    {
        foreach (var t in M.List(M.Get(estado, "historialCombates")))
            foreach (var a in M.List(M.Get(M.Map(t), "accionesLog")))
            {
                var am = M.Map(a);
                if (M.Str(M.Get(am, "tipo")) == "disparo" && M.Str(M.Get(am, "uid")) == uid) return true;
            }
        return false;
    }

    /// Rivales VIVOS que pueden pagar un disparo lejano este turno.
    public static List<string> RivalesConMisil(BotContext ctx)
    {
        int coste = CosteDisparoEstimado(ctx);
        var eliminados = M.List(M.Get(ctx.Estado, "jugadoresEliminados")).Select(M.Str).ToHashSet();
        var res = new List<string>();
        foreach (var (uid, _) in M.Map(M.Get(ctx.Estado, "obeliscos")))
        {
            if (uid == ctx.BotUid || eliminados.Contains(uid)) continue;
            if (RivalPuedeDisparar(ctx.Estado, uid, coste)) res.Add(uid);
        }
        return res;
    }

    public static int TurnosEnCelda(Dictionary<string, object?> c) => M.Int(M.Get(c, "turnosEnCelda"));

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

    public static int Coste(Dictionary<string, object?> c) => M.Int(M.Get(c, "Coste", "coste"));

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