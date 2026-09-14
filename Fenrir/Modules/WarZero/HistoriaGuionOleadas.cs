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
//     catálogo) y en qué turno y coordenada de salida.
//   • EVOLUCIONES → cuántas copias de una carta que el bot YA TIENE sobre el
//     tablero suben a su evolución ese turno (p. ej. tanqueta → tanque).
//   • RUTAS → restricciones de movimiento de ese turno: celdas donde NINGUNA
//     carta del bot puede quedarse (`CeldasVetadas`) y puntos de paso
//     obligatorios antes de ir a por el cuartel (`PuntosDePaso`).
//
// TODO lo de este fichero es OPCIONAL y vive SOLO dentro del modo historia: una
// batalla sin guion (y cualquier partida PvP) se comporta exactamente igual que
// antes. Evoluciones y rutas se declaran POR TURNO, así que afectan únicamente
// al turno que las declara.
//
// La lógica que CONSUME este catálogo vive en WarZeroHistoria.cs
// (ConstruirJugadaBotHistoria):
//   • Si la historia en curso NO tiene guion aquí → comportamiento de siempre:
//     avance frontal puro de todo lo que el bot ya tiene en el tablero.
//   • Si lo tiene, en el turno exacto `Oleada.TurnoInicio` se clonan del
//     catálogo de cartas las unidades de cada `GrupoOleada` y se colocan en su
//     `Coordenada` de salida, SUMÁNDOSE a lo que ya hubiera en el tablero (las
//     oleadas anteriores que sobrevivieron no se tocan: siguen avanzando por
//     la lógica genérica). En los turnos SIN oleada (no listados) no se clona
//     nada nuevo: las unidades ya desplegadas simplemente continúan avanzando.
//
// AÑADIR UN GUION NUEVO:
//   1. Da de alta las constantes de CartaId que necesites (o reutiliza las de
//      HistoriaCatalogo si son las mismas cartas del bando bot).
//   2. Escribe un `GuionOleadas` con su lista de `Oleada` (turno + grupos) y,
//      si hace falta, sus `EvolucionEnTurno` y sus `RutaBot`.
//   3. Añádelo a `Todas` con la Id de la `HistoriaDef` correspondiente.
// Una historia sin entrada aquí sigue funcionando exactamente igual que antes.
// ─────────────────────────────────────────────────────────────────────────────

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
public sealed record GrupoOleada(
    string CartaId,
    int Cantidad,
    string Coordenada,
    int CantidadEvolucionada = 0);

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
/// carta gasta el turno evolucionando y NO se mueve ese turno. Ponlo a true si
/// quieres que además avance el mismo turno.
public sealed record EvolucionEnTurno(
    int Turno,
    string CartaId,
    int Cantidad,
    bool AvanzaTrasEvolucionar = false);

/// RUTA del bot en un turno: restricciones de movimiento que se aplican a TODAS
/// sus cartas (las que ya estaban en el tablero y las que entran por oleada).
///
///   • `Turno`: turno al que aplica. Si es null, la ruta es la POR DEFECTO y se
///     usa en todos los turnos que no tengan una ruta propia.
///   • `CeldasVetadas`: celdas donde ninguna carta del bot puede TERMINAR el
///     turno. El avance las esquiva; si una unidad ya estaba dentro (venía del
///     turno anterior), se la desplaza a la celda adyacente compatible más
///     cercana al objetivo.
///   • `PuntosDePaso`: waypoints, en orden. Mientras una carta no haya llegado
///     a un punto de paso (y no lo haya dejado atrás, es decir, mientras siga
///     más lejos del cuartel que ese punto), avanza HACIA ÉL en vez de hacia el
///     cuartel. Sirve para canalizar el asalto por un pasillo concreto en vez
///     de que cada unidad entre por donde le pille.
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
    /// un turno de reposición (las unidades ya desplegadas de oleadas
    /// anteriores simplemente siguen su avance genérico ese turno).
    public Oleada? OleadaEnTurno(int turno) => Oleadas.FirstOrDefault(o => o.TurnoInicio == turno);

    /// Evoluciones de tablero declaradas para `turno` (lista vacía si ninguna).
    public IReadOnlyList<EvolucionEnTurno> EvolucionesEnTurno(int turno) =>
        Evoluciones.Where(e => e.Turno == turno).ToList();

    /// Ruta aplicable a `turno`: la específica de ese turno si existe; si no, la
    /// ruta por defecto (`Turno == null`); si no hay ninguna, null (avance libre).
    public RutaBot? RutaEnTurno(int turno) =>
        Rutas.FirstOrDefault(r => r.Turno == turno) ?? Rutas.FirstOrDefault(r => r.Turno == null);
}

/// Catálogo estático de guiones de oleadas, indexado por `HistoriaDef.Id`.
public static class HistoriaGuiones
{
    // ── IDS DE CARTA (bando BOT = Humanos en "demonios_1") ───────────────────
    // Mismos ids que HistoriaCatalogo.HumA/HumB/HumC — se repiten aquí como
    // constantes propias para que este fichero no dependa de la visibilidad
    // interna de HistoriaCatalogo.
    private const string DienteInviernoHumA = "xPcw2Adpdfdb8TMp4Uiy"; // ×8 en el reparto inicial
    private const string DienteInviernoHumB = "8KZtDtblcypCtFfDSF08"; // TANQUETA (→ tanque) · ×3 en el reparto inicial
    private const string DienteInviernoHumC = "kKJl1PyTsfIytyfOkfiS"; // ×2 en el reparto inicial

    // ── "demonios_1" · El asedio de Diente de Invierno ───────────────────────
    // 3 oleadas de refuerzo hasta el turno de supervivencia (6):
    //
    //   Turnos 1-2 · Oleada 1 (4 grupos) — asalto inicial en cuatro frentes:
    //     · A2: las 8 copias de HumA.
    //     · F1 (cuartel del bot): 2 de las 3 copias de HumB.
    //     · F6: la copia restante de HumB.
    //     · A6: las 2 copias de HumC ("el resto" de la plantilla).
    //   El turno 2 NO clona nada nuevo: es la misma oleada, que ya avanza sola
    //   con la lógica genérica de abajo.
    //
    //   Turno 3 · Oleada 2 (3 grupos) — se repone TODO lo de A2/F1/F6 (mismas
    //   cantidades que la oleada 1); ya no hay grupo en A6/HumC. De las 8 copias
    //   de HumA que salen por A2, 3 nacen YA EVOLUCIONADAS (no gastan turno
    //   evolucionando: pueden moverse desde su primer turno).
    //
    //   Turno 3 · además (y SOLO este turno):
    //     · 2 TANQUETAS (HumB) que ya estaban sobre el tablero EVOLUCIONAN A
    //       TANQUE. Se eligen las 2 más adelantadas y, como manda la regla del
    //       juego, gastan la acción evolucionando: ese turno no avanzan.
    //     · RUTA propia: ninguna carta del bot puede quedarse en D3, y todas
    //       pasan antes por D4 antes de tirar hacia el cuartel del jugador.
    //   Los turnos 1, 2, 4, 5 y 6 se mueven exactamente como antes.
    //
    //   Turno 4 · sin oleada nueva ("mantiene esas cartas"): lo desplegado en
    //   el turno 3 sigue avanzando, sin refuerzos adicionales.
    //
    //   Turno 5 · Oleada 3 (2 grupos) — última reposición, solo por el centro:
    //   F1 y F6 sacan 2 copias de HumB cada uno (4 cartas en total) y TODAS
    //   nacen ya evolucionadas.
    //
    //   Turno 6 · sin oleada nueva: último turno de supervivencia, lo que haya
    //   en el tablero sigue avanzando sin más refuerzos.
    //
    // NOTA: en la oleada 1 la carta HumB tiene 3 copias en total y no se divide
    // en dos mitades exactas; se reparte 2 en F1 (el cuartel) y 1 en F6. Si el
    // diseño quiere la proporción inversa, basta con intercambiar las Cantidades
    // abajo. Las oleadas de refuerzo NO están limitadas por el reparto inicial:
    // son clones frescos del catálogo, así que el turno 5 puede sacar 2+2.
    private static readonly GuionOleadas DienteDeInvierno1 = new(
        oleadas: new List<Oleada>
        {
            new Oleada(TurnoInicio: 1, Grupos: new List<GrupoOleada>
            {
                new GrupoOleada(DienteInviernoHumA, 8, "A2"),
                new GrupoOleada(DienteInviernoHumB, 2, "F1"),
                new GrupoOleada(DienteInviernoHumB, 1, "F6"),
                new GrupoOleada(DienteInviernoHumC, 2, "A6"),
            }),
            // Turno 3: de las 8 copias de HumA en A2, 3 salen ya evolucionadas.
            new Oleada(TurnoInicio: 3, Grupos: new List<GrupoOleada>
            {
                new GrupoOleada(DienteInviernoHumA, 8, "A2", CantidadEvolucionada: 3),
                new GrupoOleada(DienteInviernoHumB, 2, "F1"),
                new GrupoOleada(DienteInviernoHumB, 1, "F6"),
            }),
            // Turno 5: 2 grupos · 4 cartas, TODAS evolucionadas de salida.
            new Oleada(TurnoInicio: 5, Grupos: new List<GrupoOleada>
            {
                new GrupoOleada(DienteInviernoHumB, 2, "F1", CantidadEvolucionada: 2),
                new GrupoOleada(DienteInviernoHumB, 2, "F6", CantidadEvolucionada: 2),
            }),
        },
        evoluciones: new List<EvolucionEnTurno>
        {
            // SOLO turno 3: dos tanquetas (HumB) YA DESPLEGADAS suben a tanque —
            // las dos más cercanas al cuartel del jugador. Gastan el turno
            // evolucionando: no avanzan (pon AvanzaTrasEvolucionar: true si
            // quieres que sí). Las tanquetas que entran ese mismo turno por la
            // oleada no cuentan: solo evolucionan las que ya estaban en juego.
            new EvolucionEnTurno(Turno: 3, CartaId: DienteInviernoHumB, Cantidad: 2),
        },
        rutas: new List<RutaBot>
        {
            // Turno 3: D3 queda prohibida (nadie termina el turno ahí) y D4 es
            // punto de paso obligatorio antes de ir a por el cuartel.
            // Para que la ruta valga en TODOS los turnos, cambia `Turno: 3` por
            // `Turno: null` (ruta por defecto) o duplica la entrada por turno.
            new RutaBot(
                Turno: 3,
                CeldasVetadas: new[] { "D3" },
                PuntosDePaso: new[] { "D4" }),
        });

    // ── "demonios_2" · Diente de Invierno · La segunda embestida ─────────────
    // Mismo esqueleto que la parte 1 (oleadas en 1, 3 y 5 sobre A2/F1/F6, turno
    // de supervivencia 6) con tres diferencias:
    //
    //   1. Las 3 copias evolucionadas de HumA salen ya DESDE EL TURNO 1 (en la
    //      parte 1 no aparecían evolucionadas hasta el turno 3).
    //   2. GRUPO NUEVO por A6: 3 HumA + 2 HumB (las 5 cartas de refuerzo del
    //      bot en esta parte) que REAPARECE en los turnos 1, 3 y 5. Salen sin
    //      evolucionar; para que salieran evolucionadas basta con añadirles
    //      `CantidadEvolucionada`.
    //   3. En el turno 1, ese grupo nuevo comparte la coordenada A6 con las 2
    //      copias de HumC de la parte 1: A6 se convierte en el segundo frente
    //      fuerte del asedio.
    //
    // El resto (F1 con 2 HumB, F6 con 1 HumB, turno 5 con 2 grupos de 2 HumB
    // evolucionadas, turnos 2/4/6 sin refuerzos) es idéntico a la parte 1. NO
    // tiene evoluciones de tablero ni rutas: la parte 2 se juega exactamente
    // como antes de este cambio. Si algún día quieres replicar aquí lo del turno
    // 3 de la parte 1, copia los bloques `evoluciones:` y `rutas:`.
    private static readonly GuionOleadas DienteDeInvierno2 = new(
        oleadas: new List<Oleada>
        {
            new Oleada(TurnoInicio: 1, Grupos: new List<GrupoOleada>
            {
                new GrupoOleada(DienteInviernoHumA, 8, "A2", CantidadEvolucionada: 3),
                new GrupoOleada(DienteInviernoHumB, 2, "F1"),
                new GrupoOleada(DienteInviernoHumB, 1, "F6"),
                new GrupoOleada(DienteInviernoHumC, 2, "A6"),
                // Grupo nuevo de la parte 2 (turnos 1, 3 y 5).
                new GrupoOleada(DienteInviernoHumA, 3, "A6"),
                new GrupoOleada(DienteInviernoHumB, 2, "A6"),
            }),
            new Oleada(TurnoInicio: 3, Grupos: new List<GrupoOleada>
            {
                new GrupoOleada(DienteInviernoHumA, 8, "A2", CantidadEvolucionada: 3),
                new GrupoOleada(DienteInviernoHumB, 2, "F1"),
                new GrupoOleada(DienteInviernoHumB, 1, "F6"),
                new GrupoOleada(DienteInviernoHumA, 3, "A6"),
                new GrupoOleada(DienteInviernoHumB, 2, "A6"),
            }),
            new Oleada(TurnoInicio: 5, Grupos: new List<GrupoOleada>
            {
                new GrupoOleada(DienteInviernoHumB, 2, "F1", CantidadEvolucionada: 2),
                new GrupoOleada(DienteInviernoHumB, 2, "F6", CantidadEvolucionada: 2),
                new GrupoOleada(DienteInviernoHumA, 3, "A6"),
                new GrupoOleada(DienteInviernoHumB, 2, "A6"),
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