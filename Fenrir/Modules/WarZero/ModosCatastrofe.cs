using System;
using System.Collections.Generic;
using System.Linq;

// Alias de tipo (ámbito de fichero): los mismos que en WarZeroLogic.cs.
using Tablero = System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<System.Collections.Generic.Dictionary<string, object?>>>;
using EfectosCelda = System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<System.Collections.Generic.Dictionary<string, object?>>>;

// ─────────────────────────────────────────────────────────────────────────────
// ModosCatastrofe.cs
//
// MODOS DE PARTIDA con catástrofes (desplegable "MODO DE PARTIDA" de CREAR
// SALA; el cliente lo guarda en la partida en el campo `modoPartida`):
//
//   "normal"       → partida clásica. Este archivo no hace nada.
//   "meteoritos"   → LLUVIA DE METEORITOS. A partir del turno 10 y después cada
//                    5 turnos (10, 15, 20…) las casillas del CENTRO del tablero
//                    se marcan con el % de que les caiga un DISPARO LEJANO; al
//                    cerrar ese turno caen 3 disparos en 3 casillas distintas
//                    del centro.
//   "apocalipsis"  → APOCALIPSIS. Misma cadencia, pero en TODO el tablero: en
//                    CADA FILA caen 2 disparos lejanos y 1 parálisis lejana, en
//                    casillas distintas. El % de cada casilla es la probabilidad
//                    de que le caiga una de las tres.
//
// CÓMO FUNCIONA (mismo esquema que el bombardeo de historia, HistoriaBombardeo.cs)
//   1. Al cerrar el turno N−1 (ResolverTurnoCoreEnTx, paso 7c), si N es turno de
//      impacto, se prepara el PLAN de N con el tablero YA resuelto y se publica
//      en la partida (campo `bombardeo`, el mismo que usa la historia: el
//      cliente lo pinta con bombardeo_overlay.dart). Si N no es turno de
//      impacto, el campo se borra y el cliente muestra el aviso de "próxima
//      lluvia en el turno X".
//   2. Al cerrar el turno N (fase "catastrofe", justo antes de aplicar las
//      acciones), se SORTEA ese mismo plan y cada impacto se convierte en una
//      acción (disparo lejano = habilidad 3, parálisis lejana = habilidad 11)
//      firmada por un "jugador" neutro, `UidEvento`. Las acciones entran en el
//      motor normal (Habilidades.AplicarAcciones) DESPUÉS de la validación de
//      coste, así que no necesitan carta ni energía, y se resuelven tras el
//      movimiento: el disparo mata todo lo que TERMINE en la casilla (de quien
//      sea) y la parálisis congela a todas las cartas que haya.
//
// REGLAS
//   • Los CUARTELES nunca reciben impactos (ni entran en el plan ni, por
//     seguridad, se construye una acción sobre ellos).
//   • Una casilla con ESCUDO activo al preparar el plan tiene probabilidad 0.
//     Un escudo que se lance en el propio turno de impacto también lo bloquea:
//     el motor rechaza las acciones ofensivas sobre celdas escudadas por otro
//     jugador, y el evento nunca es el dueño del escudo.
//   • Las casillas con MURO activo (nadie puede estar en ellas) quedan fuera.
//   • Dentro de la zona, todas las casillas válidas tienen la MISMA
//     probabilidad: p = impactos / casillas válidas (de la fila, o del centro).
//     El sorteo (PlanificadorBombardeo.Sortear, muestreo sistemático) elige
//     EXACTAMENTE ese número de casillas y cada una sale con la probabilidad
//     publicada: el % que ve el jugador es el real.
//   • Los impactos NO dan PC a nadie (Combate.PcPorAcciones ignora `UidEvento`)
//     ni cuentan como victoria/derrota.
//   • Todo el azar usa semillas ESTABLES (id de la partida + turno): si la
//     transacción del cierre se reintenta, el resultado es el mismo.
//
// CENTRO DEL TABLERO (lluvia de meteoritos): las `filas/3` filas centrales (al
// menos 2) × las `columnas/3` columnas centrales (al menos 2), redondeando
// hacia arriba. Tablero 6×10 → 2×4 = 8 casillas (37 % cada una con 3 disparos);
// 8×14 → 3×5 = 15 (20 %); 12×18 → 4×6 = 24 (12 %).
//
// El cliente tiene su espejo en models/modo_partida.dart (constantes, nombres,
// `kUidEvento`) y widgets/bombardeo_overlay.dart (campos `modo`,
// `paralisisPorFila`, `paralisis` del plan publicado).
// ─────────────────────────────────────────────────────────────────────────────

/// Modo de partida (campo `modoPartida` de la partida).
public enum ModoCatastrofe { Normal, Meteoritos, Apocalipsis }

/// Plan de impactos de UN turno de un modo catástrofe. Envuelve un
/// `PlanBombardeo` (una "fila" por fila del tablero en apocalipsis; una única
/// pseudo-fila `FilaCentro` en la lluvia de meteoritos) con el reparto entre
/// disparos y parálisis.
public sealed record PlanCatastrofe(
    ModoCatastrofe Modo,
    int Turno,
    /// Disparos POR FILA (apocalipsis) o TOTALES (meteoritos).
    int DisparosPorFila,
    /// Parálisis POR FILA (apocalipsis; 0 en meteoritos).
    int ParalisisPorFila,
    PlanBombardeo Plan)
{
    /// Impactos (disparos + parálisis) que caen en una fila con [n] casillas
    /// sorteadas: primero se cubren las parálisis y el resto son disparos.
    public (int disparos, int paralisis) Reparto(int n)
    {
        int par = Math.Min(Math.Max(0, ParalisisPorFila), Math.Max(0, n));
        return (Math.Max(0, n - par), par);
    }

    /// Disparos totales del turno.
    public int Disparos => Plan.Filas.Sum(f => Reparto(f.Disparos).disparos);

    /// Parálisis totales del turno.
    public int Paralisis => Plan.Filas.Sum(f => Reparto(f.Disparos).paralisis);

    /// Documento para guardar en la partida (campo `bombardeo`). Mismo formato
    /// que `PlanBombardeo.ACampo` (lo lee bombardeo_overlay.dart) más `modo`,
    /// `paralisisPorFila` y `paralisis`. `prob` va en % entero; `filas` guarda
    /// las probabilidades exactas con las que se sortea.
    public Dictionary<string, object?> ACampo()
    {
        var campo = Plan.ACampo();
        campo["modo"] = ModosCatastrofe.Id(Modo);
        campo["disparosPorFila"] = (long)DisparosPorFila;
        campo["disparos"] = (long)Disparos;
        campo["disparosBase"] = (long)(DisparosPorFila + ParalisisPorFila);
        campo["reduccion"] = 0L;
        campo["paralisisPorFila"] = (long)ParalisisPorFila;
        campo["paralisis"] = (long)Paralisis;
        return campo;
    }
}

/// Resultado del sorteo de un turno de impacto: las acciones a inyectar en el
/// motor y un resumen para el log del servidor.
public sealed record ImpactosCatastrofe(
    List<Dictionary<string, object?>> Acciones,
    IReadOnlyList<string> Disparos,
    IReadOnlyList<string> Paralisis);

public static class ModosCatastrofe
{
    // ── Constantes (espejo de modo_partida.dart) ─────────────────────────────

    /// UID neutro con el que se firman los impactos. El cliente lo traduce al
    /// nombre del modo en los informes (`aliasEvento`).
    public const string UidEvento = "__evento__";

    /// Primer turno de impacto y cadencia (10, 15, 20…).
    public const int PrimerTurno = 10;
    public const int CadaTurnos = 5;

    /// Lluvia de meteoritos: disparos TOTALES por turno de impacto (en el centro).
    public const int MeteoritosDisparos = 3;

    /// Apocalipsis: disparos y parálisis POR FILA en cada turno de impacto.
    public const int ApocalipsisDisparosPorFila = 2;
    public const int ApocalipsisParalisisPorFila = 1;

    /// Habilidades con las que se lanzan los impactos (CatalogoHabilidades).
    public const int HabilidadDisparo = 3;     // Disparo lejano
    public const int HabilidadParalisis = 11;  // Parálisis lejana

    /// Fracción del tablero que ocupa el centro en cada eje (1/3) y tamaño
    /// mínimo del centro en cada eje.
    public const int CentroFraccion = 3;
    public const int CentroMinimo = 2;

    /// Nombre de la pseudo-fila del plan de la lluvia de meteoritos.
    public const string FilaCentro = "centro";

    // ── Modo ─────────────────────────────────────────────────────────────────

    public static string Id(ModoCatastrofe m) => m switch
    {
        ModoCatastrofe.Meteoritos => "meteoritos",
        ModoCatastrofe.Apocalipsis => "apocalipsis",
        _ => "normal",
    };

    public static ModoCatastrofe Parse(string? s) => (s ?? "").Trim().ToLowerInvariant() switch
    {
        "meteoritos" => ModoCatastrofe.Meteoritos,
        "apocalipsis" => ModoCatastrofe.Apocalipsis,
        _ => ModoCatastrofe.Normal,
    };

    /// Modo de la partida. Las batallas de HISTORIA nunca tienen catástrofes
    /// (su campo `bombardeo` lo gobierna HistoriaBombardeo.cs).
    public static ModoCatastrofe ModoDe(Dictionary<string, object?> data)
    {
        if (M.Bool(M.Get(data, "esHistoria"))) return ModoCatastrofe.Normal;
        return Parse(M.Str(M.Get(data, "modoPartida")));
    }

    /// True si en [turno] caen impactos (10, 15, 20…).
    public static bool EsTurnoDeImpacto(ModoCatastrofe modo, int turno)
    {
        if (modo == ModoCatastrofe.Normal) return false;
        if (turno < PrimerTurno) return false;
        return (turno - PrimerTurno) % CadaTurnos == 0;
    }

    /// True si la resolución del turno [turno] necesita el tablero (mapa):
    /// porque se sortea en él o porque hay que preparar el plan del siguiente.
    public static bool NecesitaTablero(ModoCatastrofe modo, int turno) =>
        EsTurnoDeImpacto(modo, turno) || EsTurnoDeImpacto(modo, turno + 1);

    /// Semilla estable de la partida para todo el azar de las catástrofes.
    public static int Semilla(string partidaId) =>
        PlanificadorBombardeo.Semilla("catastrofe", partidaId ?? "");

    // ── Plan ─────────────────────────────────────────────────────────────────

    /// Prepara el plan del turno [turno] (si es turno de impacto; si no, null)
    /// a partir del tablero con el que EMPIEZA ese turno.
    ///
    ///   [filas]/[columnas]: rejilla del mapa.
    ///   [cuarteles]: coords de los cuarteles vivos (nunca reciben impactos).
    ///   [efectos]: efectos de celda al empezar el turno (escudos y muros
    ///              activos quedan fuera del plan).
    public static PlanCatastrofe? Preparar(
        ModoCatastrofe modo, int turno, int semilla, int filas, int columnas,
        IEnumerable<string> cuarteles, EfectosCelda? efectos)
    {
        if (!EsTurnoDeImpacto(modo, turno)) return null;
        if (filas <= 0 || columnas <= 0) return null;

        var excluidas = new HashSet<string>(cuarteles.Where(c => !string.IsNullOrEmpty(c)),
                                            StringComparer.OrdinalIgnoreCase);
        excluidas.UnionWith(CeldasEscudadas(efectos));
        excluidas.UnionWith(AccionesDistorsion.CeldasConMuro(efectos));

        bool Valida(string c) => !excluidas.Contains(c);

        var filasPlan = new List<FilaBombardeo>();
        int disparosPorFila, paralisisPorFila;

        if (modo == ModoCatastrofe.Meteoritos)
        {
            disparosPorFila = MeteoritosDisparos;
            paralisisPorFila = 0;
            var pesos = CeldasCentro(filas, columnas)
                .Where(Valida)
                .ToDictionary(c => c, _ => 1.0, StringComparer.OrdinalIgnoreCase);
            int n = Math.Min(MeteoritosDisparos, pesos.Count);
            var prob = n > 0
                ? PlanificadorBombardeo.Repartir(pesos, n)
                : new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            filasPlan.Add(new FilaBombardeo(FilaCentro, n, prob));
        }
        else
        {
            disparosPorFila = ApocalipsisDisparosPorFila;
            paralisisPorFila = ApocalipsisParalisisPorFila;
            int porFila = disparosPorFila + paralisisPorFila;
            for (int f = 0; f < filas; f++)
            {
                var pesos = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                for (int col = 1; col <= columnas; col++)
                {
                    var c = Coord(f, col);
                    if (Valida(c)) pesos[c] = 1.0;
                }
                int n = Math.Min(porFila, pesos.Count);
                var prob = n > 0
                    ? PlanificadorBombardeo.Repartir(pesos, n)
                    : new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                filasPlan.Add(new FilaBombardeo(((char)('A' + f)).ToString(), n, prob));
            }
        }

        var plan = new PlanBombardeo(
            Turno: turno,
            DisparosBase: disparosPorFila + paralisisPorFila,
            Reduccion: 0,
            Filas: filasPlan,
            Desactivadoras: new List<Desactivadora>());
        return new PlanCatastrofe(modo, turno, disparosPorFila, paralisisPorFila, plan);
    }

    /// Lee el plan publicado en la partida (campo `bombardeo`) si es de este
    /// [modo] y de este [turno]; si no, null (y el llamador lo recalcula).
    public static PlanCatastrofe? LeerPlan(object? raw, ModoCatastrofe modo, int turno)
    {
        var m = M.Map(raw);
        if (m.Count == 0) return null;
        if (Parse(M.Str(M.Get(m, "modo"))) != modo) return null;
        if (M.Int(M.Get(m, "turno")) != turno) return null;
        if (M.Get(m, "filas") == null) return null;

        var filas = M.Map(M.Get(m, "filas"))
            .Select(kv =>
            {
                var f = M.Map(kv.Value);
                return new FilaBombardeo(
                    kv.Key,
                    M.Int(M.Get(f, "disparos")),
                    M.Map(M.Get(f, "prob")).ToDictionary(
                        p => p.Key, p => M.Dbl(p.Value), StringComparer.OrdinalIgnoreCase));
            })
            .OrderBy(f => f.Fila, StringComparer.Ordinal)
            .ToList();

        int disparosPorFila = M.Int(M.Get(m, "disparosPorFila"));
        int paralisisPorFila = M.Int(M.Get(m, "paralisisPorFila"));
        var plan = new PlanBombardeo(
            turno, disparosPorFila + paralisisPorFila, 0, filas, new List<Desactivadora>());
        return new PlanCatastrofe(modo, turno, disparosPorFila, paralisisPorFila, plan);
    }

    // ── Sorteo → acciones del evento ─────────────────────────────────────────

    /// Sortea el plan y construye las acciones del jugador neutro: un DISPARO
    /// LEJANO por casilla sorteada y, en apocalipsis, una PARÁLISIS LEJANA en
    /// una de las casillas de cada fila. Los cuarteles ([cuarteles]) se
    /// descartan por seguridad aunque el plan ya los excluya.
    public static ImpactosCatastrofe Sortear(
        PlanCatastrofe plan, int semilla, IEnumerable<string> cuarteles)
    {
        var prohibidas = new HashSet<string>(cuarteles.Where(c => !string.IsNullOrEmpty(c)),
                                             StringComparer.OrdinalIgnoreCase);
        var sorteo = PlanificadorBombardeo.Sortear(plan.Plan, semilla, prohibidas);

        // Agrupar los impactos por fila del plan (en la lluvia de meteoritos
        // todos pertenecen a la pseudo-fila del centro).
        var porFila = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var f in plan.Plan.Filas) porFila[f.Fila] = new List<string>();
        foreach (var coord in sorteo.Impactos)
        {
            if (prohibidas.Contains(coord)) continue;
            var fila = plan.Modo == ModoCatastrofe.Meteoritos
                ? FilaCentro
                : coord.Substring(0, 1).ToUpperInvariant();
            if (!porFila.TryGetValue(fila, out var lst)) { lst = new List<string>(); porFila[fila] = lst; }
            lst.Add(coord);
        }

        var rng = new Random(PlanificadorBombardeo.Semilla(semilla, "paralisis", plan.Turno));
        var disparos = new List<string>();
        var paralisis = new List<string>();
        foreach (var fila in porFila.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            var celdas = porFila[fila].OrderBy(c => c, StringComparer.Ordinal).ToList();
            if (celdas.Count == 0) continue;
            // Orden aleatorio estable dentro de la fila.
            for (int i = celdas.Count - 1; i > 0; i--)
            {
                int j = rng.Next(i + 1);
                (celdas[i], celdas[j]) = (celdas[j], celdas[i]);
            }
            var (_, nPar) = plan.Reparto(celdas.Count);
            paralisis.AddRange(celdas.Take(nPar));
            disparos.AddRange(celdas.Skip(nPar));
        }

        var acciones = new List<Dictionary<string, object?>>();
        foreach (var coord in disparos) acciones.Add(Accion(HabilidadDisparo, coord, plan.Turno));
        foreach (var coord in paralisis) acciones.Add(Accion(HabilidadParalisis, coord, plan.Turno));

        Console.WriteLine(
            $"[WZ.Catastrofe] {Id(plan.Modo)} turno {plan.Turno}: " +
            $"{disparos.Count} disparo(s) [{string.Join(",", disparos)}] · " +
            $"{paralisis.Count} parálisis [{string.Join(",", paralisis)}] · " +
            $"{sorteo.Redirigidos} redirigido(s), {sorteo.Perdidos} perdido(s)");

        return new ImpactosCatastrofe(acciones, disparos, paralisis);
    }

    /// Acción con el mismo shape que una habilidad lanzada por un jugador. No
    /// lleva carta ni coste: se inyecta DESPUÉS de la validación de
    /// `CosteCanonicoAccion` y el motor solo necesita `habilidadId`, `uid` y
    /// `objetivos`.
    private static Dictionary<string, object?> Accion(int habilidadId, string coord, int turno) => new()
    {
        ["habilidadId"] = habilidadId,
        ["uid"] = UidEvento,
        ["zona"] = "",
        ["origen"] = "",
        ["objetivos"] = new List<object?> { coord },
        ["turno"] = turno,
        ["costePagado"] = 0,
        ["costeServidor"] = 0,
        ["esEvento"] = true,
    };

    // ── Orquestación (la llama WarZeroService.ResolverTurnoCoreEnTx) ─────────

    /// Impactos del turno [turno] que se está resolviendo: usa el plan
    /// publicado en la partida si es de este turno y, si no (partida anterior
    /// a esta función, o un turno que no pasó por el cierre normal), lo prepara
    /// ahora con el tablero del INICIO del turno. Devuelve null si en este
    /// turno no cae nada.
    public static ImpactosCatastrofe? ImpactosDelTurno(
        ModoCatastrofe modo, int turno, int semilla,
        Dictionary<string, object?> data,
        EfectosCelda? efectosPrevios,
        IEnumerable<string> cuarteles,
        int filas, int columnas)
    {
        if (!EsTurnoDeImpacto(modo, turno)) return null;
        var cuartelesLista = cuarteles.ToList();

        var plan = LeerPlan(M.Get(data, "bombardeo"), modo, turno);
        if (plan == null)
        {
            Console.Error.WriteLine(
                $"[WZ.Catastrofe] turno {turno}: no hay plan publicado de {Id(modo)}, se prepara ahora");
            plan = Preparar(modo, turno, semilla, filas, columnas, cuartelesLista, efectosPrevios);
            if (plan == null) return null;
        }
        return Sortear(plan, semilla, cuartelesLista);
    }

    /// Valor del campo `bombardeo` para el turno SIGUIENTE al que se acaba de
    /// resolver: el plan (documento) si es turno de impacto, o null si hay que
    /// BORRAR el campo (el cliente muestra entonces el aviso de la próxima
    /// catástrofe).
    public static Dictionary<string, object?>? PlanSiguienteTrasResolver(
        ModoCatastrofe modo, int turnoResuelto, int semilla,
        IEnumerable<string> cuarteles, EfectosCelda? efectosFinal,
        int filas, int columnas)
    {
        var plan = Preparar(modo, turnoResuelto + 1, semilla, filas, columnas, cuarteles, efectosFinal);
        if (plan == null) return null;
        Console.WriteLine(
            $"[WZ.Catastrofe] {Id(modo)}: plan del turno {plan.Turno} → " +
            $"{plan.Disparos} disparo(s) y {plan.Paralisis} parálisis sobre " +
            $"{plan.Plan.Filas.Sum(f => f.Prob.Count)} casilla(s)");
        return plan.ACampo();
    }

    // ── Utilidades ───────────────────────────────────────────────────────────

    /// Celdas del CENTRO del tablero para la lluvia de meteoritos: las
    /// `filas/CentroFraccion` filas centrales × las `columnas/CentroFraccion`
    /// columnas centrales (redondeo hacia arriba, mínimo `CentroMinimo`).
    public static List<string> CeldasCentro(int filas, int columnas)
    {
        var res = new List<string>();
        if (filas <= 0 || columnas <= 0) return res;
        int nf = Math.Min(filas, Math.Max(CentroMinimo, (filas + CentroFraccion - 1) / CentroFraccion));
        int nc = Math.Min(columnas, Math.Max(CentroMinimo, (columnas + CentroFraccion - 1) / CentroFraccion));
        int f0 = (filas - nf) / 2;
        int c0 = (columnas - nc) / 2 + 1;
        for (int f = f0; f < f0 + nf; f++)
            for (int c = c0; c < c0 + nc; c++)
                res.Add(Coord(f, c));
        return res;
    }

    /// Dimensiones (filas, columnas) de una rejilla dada por sus coordenadas
    /// ("A1", "B7"…). (0, 0) si no hay celdas.
    public static (int filas, int columnas) Dimensiones(IEnumerable<string>? celdas)
    {
        int filas = 0, columnas = 0;
        if (celdas == null) return (0, 0);
        foreach (var c in celdas)
        {
            if (string.IsNullOrEmpty(c) || c.Length < 2) continue;
            int r = char.ToUpperInvariant(c[0]) - 'A';
            if (r < 0 || !int.TryParse(c[1..], out int col)) continue;
            filas = Math.Max(filas, r + 1);
            columnas = Math.Max(columnas, col);
        }
        return (filas, columnas);
    }

    /// Celdas con un escudo ACTIVO (de cualquier jugador).
    private static HashSet<string> CeldasEscudadas(EfectosCelda? efectos)
    {
        var res = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (efectos == null) return res;
        foreach (var kv in efectos)
            if (kv.Value.Any(ef =>
                    M.Str(M.Get(ef, "tipo")) == "escudo" &&
                    M.Int(M.Get(ef, "turnosRestantes")) > 0))
                res.Add(kv.Key);
        return res;
    }

    private static string Coord(int f, int c) => $"{(char)('A' + f)}{c}";
}
