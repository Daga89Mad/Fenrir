using System.Collections.Generic;
using System.Linq;

// ─────────────────────────────────────────────────────────────────────────────
// HistoriaGuionOleadas.cs
//
// Guiones de IA "por oleadas" para batallas de historia CONCRETAS. Es un
// catálogo de DATOS puro (mismo espíritu que HistoriaCatalogo.cs): no toca
// Firestore ni conoce el tablero, solo describe QUÉ hace el bot en cada turno:
//
//   • OLEADAS  → qué cartas REAPARECEN (refuerzos nuevos, clonados del
//     catálogo), en qué turno y en qué coordenada de salida, y qué RUTA sigue
//     cada grupo desde ahí.
//   • EVOLUCIONES → cuántas copias de una carta que el bot YA TIENE sobre el
//     tablero suben a su evolución ese turno (p. ej. tanqueta → tanque).
//   • RUTAS DE TURNO → restricciones globales de ese turno: celdas donde
//     NINGUNA carta del bot puede quedarse y puntos de paso para las unidades
//     que no llevan ruta propia.
//
// TODO lo de este fichero es OPCIONAL y vive SOLO dentro del modo historia: una
// batalla sin guion (y cualquier partida PvP) se comporta exactamente igual que
// antes.
//
// La lógica que CONSUME este catálogo vive en WarZeroHistoria.cs
// (ConstruirJugadaBotHistoria):
//   • Si la historia en curso NO tiene guion aquí → comportamiento de siempre:
//     avance frontal puro de todo lo que el bot ya tiene en el tablero.
//   • Si lo tiene, en el turno exacto `Oleada.TurnoInicio` se clonan del
//     catálogo de cartas las unidades de cada `GrupoOleada` y se colocan en su
//     `Coordenada` de salida, SUMÁNDOSE a lo que ya hubiera en el tablero (las
//     oleadas anteriores que sobrevivieron no se tocan: siguen avanzando por
//     su propia ruta, o frontalmente si no tenían). En los turnos SIN oleada no
//     se clona nada nuevo.
//
// AÑADIR UN GUION NUEVO:
//   1. Da de alta las constantes de CartaId que necesites.
//   2. Escribe un `GuionOleadas` con su lista de `Oleada` (turno + grupos) y,
//      si hace falta, sus `EvolucionEnTurno` y sus `RutaBot`.
//   3. Añádelo a `Todas` con la Id de la `HistoriaDef` correspondiente.
// ─────────────────────────────────────────────────────────────────────────────

/// Coordenadas SIMBÓLICAS admitidas tanto en `GrupoOleada.Coordenada` como en
/// los pasos de `GrupoOleada.Ruta`. Se resuelven en tiempo de partida contra el
/// mapa de obeliscos, así que el guion no depende de dónde caiga cada cuartel.
public static class CoordHistoria
{
    /// Cuartel del BOT (su propia base; de ahí salen los refuerzos "desde casa").
    public const string CuartelBot = "CUARTEL_BOT";

    /// Cuartel del JUGADOR: el objetivo del asedio.
    public const string CuartelRival = "CUARTEL_RIVAL";
}

/// Un grupo de refuerzos dentro de una oleada: `Cantidad` copias NUEVAS de
/// `CartaId` (clonadas del catálogo `Cartas`) aparecen en `Coordenada`.
///
/// `CantidadEvolucionada` (0 por defecto) indica cuántas de esas copias nacen
/// YA EVOLUCIONADAS: en vez de clonar `CartaId`, se clona la carta a la que
/// apunta su campo `IdEvolucion` en el catálogo. Nacen con las estadísticas de
/// la evolución y, como no tienen que gastar un turno evolucionando, pueden
/// avanzar desde su primer turno de movimiento. Las `Cantidad -
/// CantidadEvolucionada` restantes nacen normales. Si la carta no tiene
/// evolución (o esa evolución no existe en el catálogo) se avisa por consola y
/// el grupo entero sale sin evolucionar (nunca se rompe la oleada).
///
/// `Ruta` (null = avance frontal de siempre) es el itinerario del grupo: una
/// lista ORDENADA de coordenadas (literales como "F4" o simbólicas de
/// `CoordHistoria`). Las cartas nacen con esa ruta grabada y la conservan turno
/// a turno:
///   • Cada turno avanzan hacia el paso actual con su `Movimiento` normal (si
///     no les da para llegar, siguen hacia el mismo paso el turno siguiente).
///   • Al empezar un turno YA ENCIMA del paso actual, lo dan por cumplido y
///     pasan al siguiente. Repetir una coord en la lista es por tanto la forma
///     de decir "quédate ahí un turno más" (p. ej. ["A4", "A4", …]).
///   • Cuando se acaba la lista, entran a por el cuartel del jugador con el
///     avance normal. Termina la ruta con `CoordHistoria.CuartelRival` si
///     quieres que el último tramo sea explícito.
/// Las celdas vetadas de la `RutaBot` del turno siguen aplicando por encima.
public sealed record GrupoOleada(
    string CartaId,
    int Cantidad,
    string Coordenada,
    int CantidadEvolucionada = 0,
    IReadOnlyList<string>? Ruta = null);

/// Una oleada de refuerzo: en el turno `TurnoInicio` aparecen todos sus
/// `Grupos` a la vez. No se repite sola; si debe reaparecer en otro turno,
/// se declara como otra `Oleada` distinta con esos mismos grupos.
public sealed record Oleada(int TurnoInicio, IReadOnlyList<GrupoOleada> Grupos);

/// EVOLUCIÓN EN TABLERO: en el turno `Turno`, `Cantidad` copias de `CartaId`
/// que el bot YA TIENE desplegadas suben a su evolución (`IdEvolucion` del
/// catálogo `Cartas`). No es lo mismo que `GrupoOleada.CantidadEvolucionada`:
/// aquello hace NACER refuerzos nuevos ya evolucionados, esto MEJORA unidades
/// que llevan turnos peleando, sin añadir cartas al tablero.
///
/// Se eligen las copias más ADELANTADAS (las más cercanas al cuartel objetivo);
/// si hay empate se desempata por coordenada, así que el resultado es
/// determinista. Si el bot tiene menos copias de las pedidas se evolucionan las
/// que haya y se avisa por consola.
///
/// `AvanzaTrasEvolucionar` (false por defecto) replica la regla del juego: la
/// carta gasta el turno evolucionando y NO se mueve ese turno.
public sealed record EvolucionEnTurno(
    int Turno,
    string CartaId,
    int Cantidad,
    bool AvanzaTrasEvolucionar = false);

/// RUTA GLOBAL del bot en un turno: se aplica a todas sus cartas.
///
///   • `Turno`: turno al que aplica. Si es null, es la ruta POR DEFECTO.
///   • `CeldasVetadas`: celdas donde ninguna carta del bot puede TERMINAR el
///     turno. Manda sobre todo lo demás, incluidas las rutas de grupo.
///   • `PuntosDePaso`: waypoints para las unidades SIN ruta propia. Una carta
///     con `GrupoOleada.Ruta` sigue la suya y los ignora.
public sealed record RutaBot(
    int? Turno = null,
    IReadOnlyList<string>? CeldasVetadas = null,
    IReadOnlyList<string>? PuntosDePaso = null);

/// Guion completo de una batalla: la secuencia de oleadas a lo largo de la
/// partida, más las evoluciones y rutas de cada turno.
public sealed class GuionOleadas
{
    public IReadOnlyList<Oleada> Oleadas { get; }
    public IReadOnlyList<EvolucionEnTurno> Evoluciones { get; }
    public IReadOnlyList<RutaBot> Rutas { get; }

    public GuionOleadas(
        IReadOnlyList<Oleada> oleadas,
        IReadOnlyList<EvolucionEnTurno>? evoluciones = null,
        IReadOnlyList<RutaBot>? rutas = null)
    {
        Oleadas = oleadas ?? new List<Oleada>();
        Evoluciones = evoluciones ?? new List<EvolucionEnTurno>();
        Rutas = rutas ?? new List<RutaBot>();
    }

    /// La oleada que debe REAPARECER justo en `turno`, o null si `turno` no es
    /// un turno de reposición.
    public Oleada? OleadaEnTurno(int turno) => Oleadas.FirstOrDefault(o => o.TurnoInicio == turno);

    /// Evoluciones de tablero declaradas para `turno` (lista vacía si ninguna).
    public IReadOnlyList<EvolucionEnTurno> EvolucionesEnTurno(int turno) =>
        Evoluciones.Where(e => e.Turno == turno).ToList();

    /// Ruta global aplicable a `turno`: la específica de ese turno si existe; si
    /// no, la ruta por defecto (`Turno == null`); si no hay ninguna, null.
    public RutaBot? RutaEnTurno(int turno) =>
        Rutas.FirstOrDefault(r => r.Turno == turno) ?? Rutas.FirstOrDefault(r => r.Turno == null);
}

/// Catálogo estático de guiones de oleadas, indexado por `HistoriaDef.Id`.
public static class HistoriaGuiones
{
    // ── IDS DE CARTA (bando BOT = Humanos en la campaña de Demonios) ─────────
    // Mismos ids que HistoriaCatalogo.HumA/HumB/HumC — se repiten aquí como
    // constantes propias para que este fichero no dependa de la visibilidad
    // interna de HistoriaCatalogo.
    private const string HumA = "xPcw2Adpdfdb8TMp4Uiy";
    private const string HumB = "8KZtDtblcypCtFfDSF08"; // TANQUETA (→ tanque)
    private const string HumC = "kKJl1PyTsfIytyfOkfiS";

    // Atajos de coordenada simbólica, para que las rutas se lean de un vistazo.
    private const string CuartelBot = CoordHistoria.CuartelBot;
    private const string CuartelRival = CoordHistoria.CuartelRival;

    // ── "demonios_1" · El asedio de Diente de Invierno ───────────────────────
    // 3 oleadas de refuerzo hasta el turno de supervivencia (6):
    //
    //   Turnos 1-2 · Oleada 1 (4 grupos) — asalto inicial en cuatro frentes:
    //     · A2: las 8 copias de HumA.
    //     · F1 (cuartel del bot): 2 de las 3 copias de HumB (tanquetas).
    //     · F6: la copia restante de HumB.
    //     · A6: las 2 copias de HumC ("el resto" de la plantilla).
    //   El turno 2 NO clona nada nuevo: es la misma oleada, que ya avanza sola.
    //
    //   Turno 3 · Oleada 2 (3 grupos) — se repone TODO lo de A2/F1/F6. De las 8
    //   copias de HumA que salen por A2, 3 nacen YA EVOLUCIONADAS.
    //
    //   Turno 3 · además (y SOLO este turno):
    //     · 2 TANQUETAS (HumB) que ya estaban sobre el tablero EVOLUCIONAN A
    //       TANQUE. Se eligen las 2 más adelantadas y gastan la acción
    //       evolucionando: ese turno no avanzan.
    //     · RUTA propia: ninguna carta del bot puede quedarse en D3, y todas
    //       pasan antes por D4 antes de tirar hacia el cuartel del jugador.
    //   Los turnos 1, 2, 4, 5 y 6 se mueven exactamente como antes.
    //
    //   Turno 4 · sin oleada nueva: lo desplegado sigue avanzando.
    //   Turno 5 · Oleada 3 (2 grupos) — F1 y F6 sacan 2 HumB cada uno, todas
    //   evolucionadas de salida.
    //   Turno 6 · sin oleada nueva.
    private static readonly GuionOleadas DienteDeInvierno1 = new(
        oleadas: new List<Oleada>
        {
            new Oleada(TurnoInicio: 1, Grupos: new List<GrupoOleada>
            {
                new GrupoOleada(HumA, 8, "A2"),
                new GrupoOleada(HumB, 2, "F1"),
                new GrupoOleada(HumB, 1, "F6"),
                new GrupoOleada(HumC, 2, "A6"),
            }),
            new Oleada(TurnoInicio: 3, Grupos: new List<GrupoOleada>
            {
                new GrupoOleada(HumA, 8, "A2", CantidadEvolucionada: 3),
                new GrupoOleada(HumB, 2, "F1"),
                new GrupoOleada(HumB, 1, "F6"),
            }),
            new Oleada(TurnoInicio: 5, Grupos: new List<GrupoOleada>
            {
                new GrupoOleada(HumB, 2, "F1", CantidadEvolucionada: 2),
                new GrupoOleada(HumB, 2, "F6", CantidadEvolucionada: 2),
            }),
        },
        evoluciones: new List<EvolucionEnTurno>
        {
            // SOLO turno 3: dos tanquetas YA DESPLEGADAS suben a tanque.
            new EvolucionEnTurno(Turno: 3, CartaId: HumB, Cantidad: 2),
        },
        rutas: new List<RutaBot>
        {
            // SOLO turno 3: D3 prohibida y D4 como punto de paso.
            new RutaBot(
                Turno: 3,
                CeldasVetadas: new[] { "D3" },
                PuntosDePaso: new[] { "D4" }),
        });

    // ═════════════════════════════════════════════════════════════════════════
    // "demonios_2" · Diente de Invierno · La segunda embestida
    // ═════════════════════════════════════════════════════════════════════════
    // Cuatro COLUMNAS de asalto, cada una con su propia composición, su punto de
    // salida y su itinerario. Las columnas reaparecen en los turnos 1, 3 y 5;
    // en los turnos 2, 4 y 6 no entra nada nuevo y lo desplegado sigue su ruta.
    //
    //   GRUPO 1 · La guardia del cuartel — 2 HumC + 1 HumB evolucionada +
    //     1 HumA evolucionada. Sale del CUARTEL DEL BOT.
    //   GRUPO 2 · La columna del norte — 2 HumB (1 evolucionada) + 5 HumA
    //     (1 evolucionada). Sale por C1 y avanza como quiera el bot.
    //   GRUPO 3 · El flanco largo — 2 HumB evolucionadas + 5 HumA
    //     (2 evolucionadas en la 1.ª oleada, 3 en la 2.ª). Sale por A6.
    //   GRUPO 4 · El destacamento del sur — 1 HumA evolucionada + 1 HumB
    //     evolucionada + 2 HumC. Sale por F6.
    //
    // Itinerarios (ver `GrupoOleada.Ruta`):
    //   · Turno 1 · Grupo 1  → F4 → E4 → cuartel del jugador.
    //   · Turno 1 · Grupo 3  → A4 y, desde ahí, las HumA SE QUEDAN un turno en
    //     A4 (por eso "A4" aparece dos veces en su ruta) mientras las HumB se
    //     desvían a B6; al turno siguiente entran todas al cuartel.
    //   · Turno 3 · Grupos 1 y 4 (los dos salen ya del cuartel del bot) → D2 →
    //     cuartel del jugador.
    //   · Turno 3 · Grupo 3 → sin ruta: desde A6 van directos al cuartel.
    //   · Turno 5 · Grupos 1, 2 y 4 salen del cuartel del bot y el grupo 3 por
    //     F6, todos SIN ruta: el bot elige el avance.
    //
    // No hay evoluciones de tablero ni celdas vetadas en esta batalla: todo lo
    // que sale evolucionado nace ya evolucionado.

    // Rutas reutilizadas por los grupos (una instancia por itinerario).
    private static readonly string[] RutaGuardiaT1 = { "F4", "E4", CuartelRival };
    private static readonly string[] RutaAsaltoD2 = { "D2", CuartelRival };
    private static readonly string[] RutaFlancoHumA = { "A4", "A4", CuartelRival };
    private static readonly string[] RutaFlancoHumB = { "A4", "B6", CuartelRival };

    private static readonly GuionOleadas DienteDeInvierno2 = new(
        oleadas: new List<Oleada>
        {
            // ── TURNO 1 ─────────────────────────────────────────────────────
            new Oleada(TurnoInicio: 1, Grupos: new List<GrupoOleada>
            {
                // Grupo 1 · del cuartel del bot, por F4 y E4.
                new GrupoOleada(HumC, 2, CuartelBot, Ruta: RutaGuardiaT1),
                new GrupoOleada(HumB, 1, CuartelBot, CantidadEvolucionada: 1, Ruta: RutaGuardiaT1),
                new GrupoOleada(HumA, 1, CuartelBot, CantidadEvolucionada: 1, Ruta: RutaGuardiaT1),

                // Grupo 2 · C1, avance libre.
                new GrupoOleada(HumB, 2, "C1", CantidadEvolucionada: 1),
                new GrupoOleada(HumA, 5, "C1", CantidadEvolucionada: 1),

                // Grupo 3 · A6. Las HumA esperan un turno en A4; las HumB rodean
                // por B6. Después entran todas al cuartel.
                new GrupoOleada(HumB, 2, "A6", CantidadEvolucionada: 2, Ruta: RutaFlancoHumB),
                new GrupoOleada(HumA, 5, "A6", CantidadEvolucionada: 2, Ruta: RutaFlancoHumA),

                // Grupo 4 · F6, avance libre.
                new GrupoOleada(HumA, 1, "F6", CantidadEvolucionada: 1),
                new GrupoOleada(HumB, 1, "F6", CantidadEvolucionada: 1),
                new GrupoOleada(HumC, 2, "F6"),
            }),

            // ── TURNO 3 ─────────────────────────────────────────────────────
            new Oleada(TurnoInicio: 3, Grupos: new List<GrupoOleada>
            {
                // Grupos 1 y 4 · esta vez los DOS salen del cuartel del bot y
                // atacan por D2.
                new GrupoOleada(HumC, 2, CuartelBot, Ruta: RutaAsaltoD2),
                new GrupoOleada(HumB, 1, CuartelBot, CantidadEvolucionada: 1, Ruta: RutaAsaltoD2),
                new GrupoOleada(HumA, 1, CuartelBot, CantidadEvolucionada: 1, Ruta: RutaAsaltoD2),
                new GrupoOleada(HumA, 1, CuartelBot, CantidadEvolucionada: 1, Ruta: RutaAsaltoD2),
                new GrupoOleada(HumB, 1, CuartelBot, CantidadEvolucionada: 1, Ruta: RutaAsaltoD2),
                new GrupoOleada(HumC, 2, CuartelBot, Ruta: RutaAsaltoD2),

                // Grupo 2 · igual que en el turno 1.
                new GrupoOleada(HumB, 2, "C1", CantidadEvolucionada: 1),
                new GrupoOleada(HumA, 5, "C1", CantidadEvolucionada: 1),

                // Grupo 3 · más fuerte (3 HumA evolucionadas) y sin rodeo: desde
                // A6 entran todos juntos al cuartel.
                new GrupoOleada(HumB, 2, "A6", CantidadEvolucionada: 2),
                new GrupoOleada(HumA, 5, "A6", CantidadEvolucionada: 3),
            }),

            // ── TURNO 5 ─────────────────────────────────────────────────────
            new Oleada(TurnoInicio: 5, Grupos: new List<GrupoOleada>
            {
                // Grupos 1, 2 y 4 · todos desde el cuartel del bot, sin ruta.
                new GrupoOleada(HumC, 2, CuartelBot),
                new GrupoOleada(HumB, 1, CuartelBot, CantidadEvolucionada: 1),
                new GrupoOleada(HumA, 1, CuartelBot, CantidadEvolucionada: 1),

                new GrupoOleada(HumB, 2, CuartelBot, CantidadEvolucionada: 1),
                new GrupoOleada(HumA, 5, CuartelBot, CantidadEvolucionada: 1),

                new GrupoOleada(HumA, 1, CuartelBot, CantidadEvolucionada: 1),
                new GrupoOleada(HumB, 1, CuartelBot, CantidadEvolucionada: 1),
                new GrupoOleada(HumC, 2, CuartelBot),

                // Grupo 3 · esta vez sale por F6, sin ruta.
                new GrupoOleada(HumB, 2, "F6", CantidadEvolucionada: 2),
                new GrupoOleada(HumA, 5, "F6", CantidadEvolucionada: 3),
            }),
        });

    /// Todos los guiones registrados, indexados por `HistoriaDef.Id`. Una
    /// historia sin entrada aquí usa el avance frontal genérico de siempre.
    public static readonly IReadOnlyDictionary<string, GuionOleadas> Todas =
        new Dictionary<string, GuionOleadas>
        {
            ["demonios_1"] = DienteDeInvierno1,
            ["demonios_2"] = DienteDeInvierno2,
        };

    /// Guion de `historiaId`, o null si esa historia no tiene oleadas
    /// scriptadas (usa el avance frontal genérico).
    public static GuionOleadas? Get(string historiaId) =>
        !string.IsNullOrEmpty(historiaId) && Todas.TryGetValue(historiaId, out var g) ? g : null;
}