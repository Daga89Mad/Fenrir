// ─────────────────────────────────────────────────────────────────────────────
// HistoriaCatalogo.cs
//
// Catálogo ESTÁTICO de las batallas del MODO HISTORIA (Opción B: partidas reales
// de Firestore con configuración especial). Cada `HistoriaDef` describe por
// completo una batalla scriptada: mapa, energía inicial, cartas ya colocadas de
// ambos bandos, objetivo del jugador y del bot, y el encadenamiento de partes
// (1..N) de esa historia.
//
// El creador de partidas (WarZeroService.CrearPartidaHistoriaAsync — fase 2) lee
// de aquí y siembra el documento Partidas/{id} ya EN CURSO. En estas partidas:
//   • NO hay reparto de mano ni robo de fin de turno: TODAS las cartas nacen ya
//     sobre el tablero (apiladas en el cuartel de su bando).
//   • La energía inicial la fija cada bando (por defecto 40).
//   • Cada turno, quien NO farmea Ø recibe `SuerteDelPerdedor` Ø de regalo.
//   • El jugador GANA si sobrevive (su cuartel no es conquistado) hasta cerrar el
//     turno `TurnosSupervivencia`. El bot GANA si conquista el cuartel del jugador
//     antes de ese turno.
//
// Al completar la ÚLTIMA parte (`SiguienteId == null`) se desbloquea la historia
// en el perfil del jugador. Perder obliga a reempezar desde la parte 1.
//
// AÑADIR UNA HISTORIA NUEVA:
//   1. Da de alta sus ids de carta como constantes (sección IDS DE CARTA).
//   2. Escribe un método `static HistoriaDef XxxN()` con su configuración.
//   3. Añádelo a la lista `Todas`.
// Nada más: el resto del pipeline (creación de partida, resolución de turno,
// UI de la campaña) es genérico y no necesita tocarse por historia.
// ─────────────────────────────────────────────────────────────────────────────

/// Objetivo de un bando dentro de una batalla de historia.
public enum ObjetivoHistoria
{
    /// Gana si su cuartel NO es conquistado hasta cerrar el turno de supervivencia.
    Sobrevivir,

    /// Gana si conquista el cuartel del rival antes del turno de supervivencia.
    Conquistar,
}

/// Una carta y su cantidad dentro del despliegue inicial de un bando. Las
/// `Cantidad` copias nacen APILADAS en la celda del cuartel de ese bando.
public record CartaHistoria(string CartaId, int Cantidad);

/// Configuración de uno de los dos bandos de la batalla.
public record BandoHistoria(
    /// ejercitoId del bando (1 Humanos · 2 Biónicos · 3 Demonios · 4 Nefilim).
    int Ejercito,
    /// Qué tiene que lograr este bando para ganar.
    ObjetivoHistoria Objetivo,
    /// Cartas que nacen apiladas en el cuartel de este bando (ids de `Cartas`).
    IReadOnlyList<CartaHistoria> Cartas,
    /// Coord FIJA del cuartel de este bando (p. ej. "A1"). Debe ser una de las
    /// coords de obelisco definidas en el mapa. Si es null, el creador elige una
    /// de forma determinista entre las candidatas del mapa (fase 2).
    string? Cuartel = null,
    /// Energía Ø con la que empieza el bando.
    int EnergiaInicial = 40);

/// Definición completa de una batalla (una parte de una historia).
public record HistoriaDef(
    /// Id único de la batalla, p. ej. "demonios_1". También es el docId de la
    /// partida-plantilla y lo que se desbloquea al terminar la última parte.
    string Id,
    /// Ejército de la CAMPAÑA a la que pertenece (pestaña del modo historia).
    /// 1 Humanos · 2 Biónicos · 3 Demonios · 4 Nefilim.
    int EjercitoCampana,
    /// Nº de historia dentro de la campaña (1..10) — el "slot" de la lista.
    int Orden,
    /// Parte actual dentro de la historia (1..Partes).
    int Parte,
    /// Nº total de partes de esta historia.
    int Partes,
    /// Id de la parte SIGUIENTE, o null si esta es la última (al ganarla se
    /// marca la historia como desbloqueada/completada).
    string? SiguienteId,
    string Titulo,
    /// docId del mapa en la colección `Mapas` (rejilla, terreno y obeliscos).
    string MapaId,
    /// El jugador gana si sobrevive hasta cerrar ESTE turno (inclusive).
    int TurnosSupervivencia,
    /// Ø regalados cada turno a un bando que no farmeó nada ("suerte del perdedor").
    int SuerteDelPerdedor,
    /// Bando controlado por el jugador humano.
    BandoHistoria Jugador,
    /// Bando controlado por la máquina.
    BandoHistoria Bot,
    /// Perfil de la IA del bot para esta batalla (afinado por historia).
    /// Dificultad: "medio" | "alto".  Estilo: "equilibrado" | "defensivo" | "agresivo".
    string BotDificultad = "medio",
    string BotEstilo = "agresivo",
    /// docId de la colección `Historias` que se marca como DESBLOQUEADA en el
    /// perfil del jugador al GANAR la ÚLTIMA parte. Solo se usa si esta es la
    /// última parte (SiguienteId == null). Déjalo en null en las partes
    /// intermedias (no desbloquean nada; el cliente encadena a la siguiente).
    string? DesbloqueaHistoriaId = null,
    /// Id de la PRIMERA parte de la historia (para reiniciar tras perder: "si
    /// pierdes debes volver a empezar"). En la parte 1 déjalo en null (se asume
    /// que es ella misma); en las partes 2..N apunta a la parte 1.
    string? PrimeraParteId = null)
{
    /// True si es la última parte de la historia (al ganarla se desbloquea).
    public bool EsUltimaParte => string.IsNullOrEmpty(SiguienteId);
}

/// Catálogo estático de todas las batallas del modo historia.
public static class HistoriaCatalogo
{
    // ── IDS DE CARTA (colección `Cartas`) ────────────────────────────────────
    // DEMONIOS (bando del jugador en demonios_1)
    private const string DemA = "jFpE0EY9dJQdME2iM2y9"; // ×6 en la parte 1 · ×8 en la parte 2
    private const string DemB = "yEwMBTHhiVqgL1OIZsUI"; // ×1 8   COLMILLOS DE GEHENA
    private const string DemC = "stF3jOzQyQvVJguGblKj"; // ×1 OGRO
    private const string DemD = "qrc2GYYSEhjLoISdxQch"; // ×1 · Martynara
    private const string DemE = "cPgRW24Te3ic6sN8EPoR"; // ×2 · Sombras de Belial

    // HUMANOS (bando del bot en demonios_1)
    private const string HumA = "xPcw2Adpdfdb8TMp4Uiy"; // ×8 en la parte 1 · ×11 en la parte 2
    private const string HumB = "8KZtDtblcypCtFfDSF08"; // ×3 en la parte 1 · ×5 en la parte 2
    private const string HumC = "kKJl1PyTsfIytyfOkfiS"; // ×2

    /// Todas las batallas registradas. El orden no importa (se indexan por Id).
    public static readonly IReadOnlyList<HistoriaDef> Todas = new List<HistoriaDef>
    {
        Demonios1(),
        Demonios2(),
        // … aquí irán las ~40 batallas restantes (demonios_3, humanos_1, …)
    };

    private static readonly Dictionary<string, HistoriaDef> _porId =
        Todas.ToDictionary(h => h.Id);

    /// Devuelve la definición de una batalla por su Id, o null si no existe.
    public static HistoriaDef? Get(string id) =>
        id != null && _porId.TryGetValue(id, out var d) ? d : null;

    /// Batallas de una campaña (ejército), ordenadas por `Orden` y `Parte`.
    /// Útil para construir la lista del modo historia en el cliente.
    public static IEnumerable<HistoriaDef> DeCampana(int ejercitoCampana) =>
        Todas.Where(h => h.EjercitoCampana == ejercitoCampana)
             .OrderBy(h => h.Orden).ThenBy(h => h.Parte);

    /// True si `id` es una batalla de historia conocida.
    public static bool Existe(string id) => Get(id) != null;

    // ── DEFINICIONES ─────────────────────────────────────────────────────────

    /// DEMONIOS · Historia 1 · Parte 1 de 3.
    /// El jugador (Demonios) debe AGUANTAR 6 turnos sin que le conquisten el
    /// cuartel. El bot (Humanos) intenta conquistarlo. Todas las cartas nacen ya
    /// colocadas: las del jugador en su cuartel; las del bot en el suyo. Energía
    /// 40 para ambos, sin reparto de cartas, y "suerte del perdedor" de +3 Ø.
    private static HistoriaDef Demonios1() => new(
        Id: "demonios_1",
        EjercitoCampana: 3,              // Demonios
        Orden: 1,
        Parte: 1,
        Partes: 3,
        SiguienteId: "demonios_2",       // Demonios2() (más abajo)
        Titulo: "El asedio de Diente de Invierno",
        MapaId: "diente_invierno",
        TurnosSupervivencia: 6,
        SuerteDelPerdedor: 3,
        Jugador: new BandoHistoria(
            Ejercito: 3,                 // Demonios
            Objetivo: ObjetivoHistoria.Sobrevivir,
            // TODO(fase 2): fijar la coord exacta del cuartel del jugador en
            // `diente_invierno`. Si se deja null, el creador la elige de forma
            // determinista entre las coords de obelisco del mapa.
            Cuartel: null,
            Cartas: new[]
            {
                new CartaHistoria(DemA, 7),
                new CartaHistoria(DemB, 1),
                new CartaHistoria(DemC, 1),
                new CartaHistoria(DemE, 2),
            },
            EnergiaInicial: 40),
        Bot: new BandoHistoria(
            Ejercito: 1,                 // Humanos
            Objetivo: ObjetivoHistoria.Conquistar,
            // TODO(fase 2): fijar la coord del cuartel del bot en `diente_invierno`.
            Cuartel: null,
            Cartas: new[]
            {
                new CartaHistoria(HumA, 8),
                new CartaHistoria(HumB, 3),
                new CartaHistoria(HumC, 2),
            },
            EnergiaInicial: 40),
        BotDificultad: "medio",
        BotEstilo: "agresivo");

    /// DEMONIOS · Historia 1 · Parte 2 de 3.
    /// Mismo asedio que la parte 1 (mismo mapa, mismos cuarteles, 6 turnos de
    /// supervivencia y 40 Ø por bando), pero con AMBOS bandos reforzados:
    ///
    ///   • Jugador (Demonios): además de lo de la parte 1 recibe en su cuartel
    ///     1 copia de DemD y 5 copias EXTRA de DemA (3 + 5 = 8).
    ///   • Bot (Humanos): 3 copias EXTRA de HumA (8 + 3 = 11) y 2 EXTRA de HumB
    ///     (3 + 2 = 5). Esas 5 cartas nuevas forman el GRUPO DE A6 del guion de
    ///     oleadas (HistoriaGuionOleadas.cs · DienteDeInvierno2), que reaparece
    ///     en los turnos 1, 3 y 5. Además, las 3 copias de HumA evolucionadas
    ///     ya salen así DESDE EL TURNO 1 (en la parte 1 no lo hacían hasta el 3).
    ///
    /// Perder obliga a reempezar por la parte 1 (`PrimeraParteId`).
    private static HistoriaDef Demonios2() => new(
        Id: "demonios_2",
        EjercitoCampana: 3,              // Demonios
        Orden: 1,
        Parte: 2,
        Partes: 3,
        SiguienteId: "demonios_3",       // aún por definir (fase posterior)
        Titulo: "Diente de Invierno · La segunda embestida",
        MapaId: "diente_invierno",
        TurnosSupervivencia: 6,
        SuerteDelPerdedor: 3,
        Jugador: new BandoHistoria(
            Ejercito: 3,                 // Demonios
            Objetivo: ObjetivoHistoria.Sobrevivir,
            Cuartel: null,               // igual que la parte 1: lo elige el mapa
            Cartas: new[]
            {
                new CartaHistoria(DemA, 8),   // 3 de la parte 1 + 5 de refuerzo
                new CartaHistoria(DemB, 1),
                new CartaHistoria(DemC, 1),
                new CartaHistoria(DemD, 1),   // carta nueva de la parte 2
            },
            EnergiaInicial: 40),
        Bot: new BandoHistoria(
            Ejercito: 1,                 // Humanos
            Objetivo: ObjetivoHistoria.Conquistar,
            Cuartel: null,
            Cartas: new[]
            {
                new CartaHistoria(HumA, 11),  // 8 de la parte 1 + 3 de refuerzo
                new CartaHistoria(HumB, 5),   // 3 de la parte 1 + 2 de refuerzo
                new CartaHistoria(HumC, 2),
            },
            EnergiaInicial: 40),
        BotDificultad: "medio",
        BotEstilo: "agresivo",
        // Parte intermedia: no desbloquea nada; el cliente encadena con
        // `SiguienteId`. Al perder se vuelve a la parte 1.
        DesbloqueaHistoriaId: null,
        PrimeraParteId: "demonios_1");
}