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
// de aquí y siembra el documento Partidas/{id} ya EN CURSO.
//
// Hay DOS modos de batalla (`ModoHistoria`):
//
//   • ASEDIO (el de siempre, partes 1 y 2 de Diente de Invierno):
//       – NO hay reparto de mano ni robo de fin de turno: TODAS las cartas nacen
//         ya sobre el tablero (apiladas en el cuartel de su bando).
//       – La energía inicial la fija cada bando (por defecto 40).
//       – Cada turno, quien NO farmea Ø recibe `SuerteDelPerdedor` Ø de regalo.
//       – El jugador GANA si sobrevive (su cuartel no es conquistado) hasta
//         cerrar el turno `TurnosSupervivencia`. El bot GANA si conquista el
//         cuartel del jugador antes de ese turno.
//       – El bot solo AVANZA lo que tiene (guion de oleadas opcional).
//       – Variante con los papeles CAMBIADOS (humanos_1): el jugador ATACA
//         (Objetivo Conquistar) y el bot gana si el jugador se queda sin cartas
//         (`DerrotaJugadorSinCartas`). Cómo mueve el bot lo decide
//         `ComportamientoBot` (avanzar / defender / CAZAR), las copias marcadas
//         con `CartaHistoria.Guarnicion` no salen nunca de su cuartel y el bot
//         puede además bombardear el tablero cada turno (HistoriaBombardeo.cs).
//
//   • PARTIDA NORMAL (nuevo, parte 3 de Diente de Invierno):
//       – Funciona como una partida PvP de 2 jugadores: cada bando tiene un
//         MAZO FIJO (`BandoHistoria.Mazo`), recibe una mano inicial, roba 1
//         carta al final de cada turno y puede robar en el cuartel.
//       – Las cartas de `BandoHistoria.Cartas` nacen igualmente en su cuartel.
//       – Gana quien conquiste el cuartel rival (TurnosSupervivencia = 0 desactiva
//         la victoria por supervivencia).
//       – El bot juega con la IA REAL de los bots (EstrategaSoftmaxStrategy):
//         despliega desde la mano, evoluciona, lanza cartas de acción…
//       – El mazo del jugador puede MEZCLAR ejércitos (p. ej. Humanos + Demonios):
//         es una lista cerrada de ids que no pasa por el filtro de ejército.
//
// CARTAS EXCLUSIVAS DE HISTORIA (`CartasExclusivas`): cartas que NO existen en la
// colección `Cartas` de Firestore y que solo aparecen dentro de una batalla. El
// servidor las inyecta en el catálogo únicamente en los puntos que resuelven
// cartas por id (creación de la partida, cierre de turno, validación de
// acciones y GET /warzero/cartas), de modo que NUNCA aparecen en tienda, sobres,
// colección, mazos por defecto ni generales comprables. Sus ids llevan el
// prefijo `hist_excl_` para que no puedan colisionar con un docId de Firestore.
//
// Al completar la ÚLTIMA parte (`SiguienteId == null`) se desbloquea la historia
// en el perfil del jugador. Perder obliga a reempezar desde la parte 1.
//
// AÑADIR UNA HISTORIA NUEVA:
//   1. Da de alta sus ids de carta como constantes (sección IDS DE CARTA). Si
//      necesita cartas propias, defínelas en CARTAS EXCLUSIVAS.
//   2. Escribe un método `static HistoriaDef XxxN()` con su configuración.
//   3. Añádelo a la lista `Todas`.
// Nada más: el resto del pipeline (creación de partida, resolución de turno,
// UI de la campaña) es genérico y no necesita tocarse por historia.
// ─────────────────────────────────────────────────────────────────────────────

/// Objetivo de un bando dentro de una batalla de historia.
public enum ObjetivoHistoria
{
    /// Gana si su cuartel NO es conquistado hasta cerrar el turno de supervivencia.
    /// En el BOT, si no se indica otro `ComportamientoBot`, significa además que
    /// DEFIENDE: sus cartas no avanzan.
    Sobrevivir,

    /// Gana si conquista el cuartel del rival antes del turno de supervivencia.
    Conquistar,
}

/// Cómo MUEVE sus cartas el bot de un asedio (en partida normal lo decide la
/// IA real de los bots y esto se ignora).
public enum ComportamientoBotHistoria
{
    /// Se deduce del objetivo del bot: Conquistar → Avanzar, Sobrevivir → Defender.
    Auto,

    /// Avance frontal hacia el cuartel del jugador (asedios de Diente de Invierno).
    Avanzar,

    /// Las cartas mantienen su posición.
    Defender,

    /// CAZADOR: cada carta que no sea de guarnición termina el turno en una
    /// celda donde NO cae el bombardeo y persigue a las cartas del jugador;
    /// solo entra en su celda si el grupo que llega le gana. Ver
    /// WarZeroHistoria.PlanCaza.
    Cazar,
}

/// Cómo se juega una batalla de historia (ver cabecera del fichero).
public enum ModoHistoria
{
    /// Todo nace en el tablero, sin mano ni robo. El bot avanza, defiende o
    /// caza según `HistoriaDef.ComportamientoBot`.
    Asedio,

    /// Partida normal de 2 jugadores: mazo fijo, mano, robo y bot con IA real.
    PartidaNormal,
}

/// Una carta y su cantidad dentro del despliegue inicial o del mazo de un
/// bando. En `Cartas` las `Cantidad` copias nacen APILADAS en la celda del
/// cuartel; en `Mazo` son las copias de esa carta dentro del mazo.
///
/// `Evolucionadas` (solo en `Cartas`, 0 por defecto): cuántas de esas copias
/// nacen YA EVOLUCIONADAS. En vez de la carta base se siembra la carta de su
/// `IdEvolucion` del catálogo, con sus estadísticas, desde el turno 1. Si la
/// carta no tiene evolución, se siembran sin evolucionar (y se avisa por
/// consola). En `Mazo` se ignora.
///
/// `Guarnicion` (solo en `Cartas` del BOT, 0 por defecto): cuántas de esas
/// copias forman la GUARNICIÓN del cuartel: nacen marcadas y no se mueven
/// nunca, sea cual sea el `ComportamientoBot`. Se marcan primero las copias
/// SIN evolucionar (las últimas de la entrada).
public record CartaHistoria(string CartaId, int Cantidad, int Evolucionadas = 0, int Guarnicion = 0);

/// Configuración de uno de los dos bandos de la batalla.
public record BandoHistoria(
    /// ejercitoId del bando (1 Humanos · 2 Biónicos · 3 Demonios · 4 Nefilim ·
    /// 5 Trans-Universales, solo historia).
    int Ejercito,
    /// Qué tiene que lograr este bando para ganar.
    ObjetivoHistoria Objetivo,
    /// Cartas que nacen apiladas en el cuartel de este bando (ids de `Cartas`
    /// o de `HistoriaCatalogo.CartasExclusivas`).
    IReadOnlyList<CartaHistoria> Cartas,
    /// Coord FIJA del cuartel de este bando (p. ej. "A1"). Debe ser una de las
    /// coords de obelisco definidas en el mapa. Si es null, el creador elige una
    /// de forma determinista entre las candidatas del mapa (fase 2).
    string? Cuartel = null,
    /// Energía Ø con la que empieza el bando.
    int EnergiaInicial = 40,
    /// SOLO en `ModoHistoria.PartidaNormal`: mazo FIJO del bando. Funciona
    /// igual que un mazo de PvP: de aquí sale la mano inicial y, cada turno,
    /// 1 carta al azar (el mazo no se agota). Lo normal es 1 de cada carta
    /// (`Cantidad` = 1); poner 2 equivale a tener esa carta repetida en el
    /// mazo. Puede mezclar ejércitos. En modo Asedio se ignora.
    IReadOnlyList<CartaHistoria>? Mazo = null,
    /// Nombre visible del bando (alias en la partida). Si es null se usa el
    /// nombre del ejército.
    string? Alias = null,
    /// Cartas ESPECIALES (generales) que este bando puede COMPRAR en su
    /// cuartel durante la batalla. Si se define, el cuartel del cliente muestra
    /// EXACTAMENTE estas cartas (ids de `Cartas`), sin mirar el mazo personal
    /// del jugador ni el filtro de ejército. Cada una se compra una sola vez
    /// por partida, como en PvP. null = cuartel normal del ejército.
    IReadOnlyList<string>? EspecialesCuartel = null);

/// Una sección EXTRA de la ventana explicativa que se muestra antes de empezar
/// la batalla (ver `HistoriaDef.SeccionesExplicacion`). El resto de secciones
/// (objetivo, derrota, tropas, enemigo, bombardeo…) se generan solas a partir
/// de la definición en WarZeroHistoriaExplicacion.cs.
public record SeccionExplicacion(
    /// Emoji o símbolo corto que encabeza la sección (p. ej. "📜").
    string Icono,
    string Titulo,
    /// Texto de la sección. Admite saltos de línea; las líneas que empiezan
    /// por "• " se pintan como viñetas.
    string Texto);

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
    /// 0 = sin victoria por supervivencia (solo se gana conquistando).
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
    string? PrimeraParteId = null,
    /// Modo de juego de la batalla (ver `ModoHistoria`).
    ModoHistoria Modo = ModoHistoria.Asedio,
    /// Si true, el JUGADOR PIERDE en cuanto, tras resolver un turno, no le
    /// queda ninguna carta en el tablero (ni en la mano, en partida normal).
    /// Lo evalúa la resolución del turno (WarZeroService, paso 8b, vía
    /// `EvaluarFinHistoria`). false = comportamiento de siempre.
    bool DerrotaJugadorSinCartas = false,
    /// Cómo mueve el bot sus cartas en un asedio (ver
    /// `ComportamientoBotHistoria`). Auto = según su objetivo.
    ComportamientoBotHistoria ComportamientoBot = ComportamientoBotHistoria.Auto,
    /// Texto narrativo con el que se abre la ventana explicativa de la batalla
    /// (antes de empezar a jugar). null = sin introducción.
    string? Introduccion = null,
    /// Secciones EXTRA de la ventana explicativa, que se añaden después de las
    /// generadas automáticamente (objetivo, derrota, tropas, enemigo,
    /// bombardeo, desactivadoras). Para reglas propias de la batalla que no se
    /// deducen de la definición.
    IReadOnlyList<SeccionExplicacion>? SeccionesExplicacion = null)
{
    /// Comportamiento EFECTIVO del bot (resuelve Auto a partir del objetivo).
    public ComportamientoBotHistoria ComportamientoBotEfectivo =>
        ComportamientoBot != ComportamientoBotHistoria.Auto
            ? ComportamientoBot
            : Bot.Objetivo == ObjetivoHistoria.Sobrevivir
                ? ComportamientoBotHistoria.Defender
                : ComportamientoBotHistoria.Avanzar;

    /// True si es la última parte de la historia (al ganarla se desbloquea).
    public bool EsUltimaParte => string.IsNullOrEmpty(SiguienteId);

    /// True si la batalla se juega como partida normal (mano, mazo y robo).
    public bool EsPartidaNormal => Modo == ModoHistoria.PartidaNormal;
}

/// Carta EXCLUSIVA del modo historia. No existe en la colección `Cartas`: se
/// define aquí y el servidor la inyecta en el catálogo solo donde hace falta
/// resolverla por id (ver cabecera). `ACatalogo()` produce el mismo shape que
/// un documento de `Cartas` (mismos nombres de campo que escribe el editor de
/// cartas del cliente), así que el resto del juego la trata como una más.
public record CartaExclusivaHistoria(
    string Id,
    string Nombre,
    /// Texto de la carta (campo `Descripcion`).
    string Descripcion,
    /// URL de la ilustración (campo `Imagen`). Vacío = placeholder.
    string Imagen,
    int Ejercito,
    int Fuerza,
    int Defensa,
    int Movimiento,
    int Coste,
    /// 1 Terrestre · 2 Volador · 3 Marino.
    int Tipo = 1,
    /// 0 Básica · 1 Evolución · 3 Estática · 4 Acción · 5 Especial.
    int Condicion = 0,
    int IdHabilidad = 0,
    int CosteHabilidad = 0)
{
    /// Documento de catálogo equivalente (claves de `Cartas` + `id`).
    public Dictionary<string, object?> ACatalogo() => new()
    {
        ["id"] = Id,
        ["Nombre"] = Nombre,
        ["Descripcion"] = Descripcion,
        ["Imagen"] = Imagen,
        ["Ejercito"] = (long)Ejercito,
        ["Fuerza"] = (long)Fuerza,
        ["Defensa"] = (long)Defensa,
        ["Movimiento"] = (long)Movimiento,
        ["Coste"] = (long)Coste,
        ["Tipo"] = (long)Tipo,
        ["Condicion"] = (long)Condicion,
        ["IdHabilidad"] = (long)IdHabilidad,
        ["CosteHabilidad"] = (long)CosteHabilidad,
        ["IdEvolucion"] = "",
        ["Evolucion"] = 0L,
        ["PorDefecto"] = false,
        ["Numero"] = 0L,
        // Marca informativa: carta que solo existe dentro del modo historia.
        ["ExclusivaHistoria"] = true,
    };
}

/// Catálogo estático de todas las batallas del modo historia.
public static class HistoriaCatalogo
{
    // ── EJÉRCITOS EXCLUSIVOS DE HISTORIA ─────────────────────────────────────
    /// Ejército de las cartas Trans-Universales. No existe en la colección
    /// `Ejercitos`: solo lo usan las cartas exclusivas y el bot de demonios_3.
    public const int EjercitoTransUniversal = 5;

    // ── IDS DE CARTA (colección `Cartas`) ────────────────────────────────────
    // DEMONIOS
    private const string DemA = "jFpE0EY9dJQdME2iM2y9"; // ×6 en la parte 1 · ×8 en la parte 2
    private const string DemB = "yEwMBTHhiVqgL1OIZsUI"; // ×1 8   COLMILLOS DE GEHENA
    private const string DemC = "stF3jOzQyQvVJguGblKj"; // ×1 OGRO
    private const string DemD = "qrc2GYYSEhjLoISdxQch"; // ×1 · Martynara
    private const string DemE = "cPgRW24Te3ic6sN8EPoR"; // ×2 · Sombras de Belial
    private const string DemF = "xi9ys2LqcHNVfFPNRsef"; // cuartel en la parte 3
    private const string DemG = "piqdzNbXTy1xt2ibg8Oi"; // mazo en la parte 3
    private const string DemH = "0qGtiuXP9aqAmxEwVzhc"; // mazo en la parte 3

    // HUMANOS
    private const string HumA = "xPcw2Adpdfdb8TMp4Uiy"; // Soldado celeste · ×9 en la parte 1 · ×11 en la parte 2 · ×10 en humanos_1
    private const string HumB = "8KZtDtblcypCtFfDSF08"; // ×4 en la parte 1 · ×5 en la parte 2
    private const string HumC = "kKJl1PyTsfIytyfOkfiS"; // ×3
    private const string HumD = "wWGvqQWEjZRZZcyab6yU"; // General Albariel · humanos_1
    private const string HumE = "IUvkpR1o49FMwY9XyMdW"; // Capitán esmeralda · humanos_1

    // NEFILIM (bot de humanos_1)
    private const string NefA = "VmhD1AghdOBjmRfpSzk4"; // Capitán Anac
    private const string NefB = "iju32O3HHqU27hmoVhhn"; // Lanzarocas
    private const string NefC = "qeygP8oT7WYKqmq1kvHI"; // Soldado de la ira
    private const string NefD = "tSWupmUokszJJfLRJaFC"; // Luz de la soberbia

    // ── CARTAS EXCLUSIVAS: TRANS-UNIVERSALES (solo demonios_3) ───────────────
    // Ids con prefijo `hist_excl_` (nunca colisionan con un docId de Firestore).
    public const string TuGeneral = "hist_excl_tu_general";
    public const string TuSoldado = "hist_excl_tu_soldado";
    public const string TuCriatura = "hist_excl_tu_criatura";
    public const string TuProteccion = "hist_excl_tu_proteccion";
    public const string TuAnimal = "hist_excl_tu_animal";

    // ── CARTAS EXCLUSIVAS: ARTILLERÍA NEFILIM (solo humanos_1) ───────────────
    /// Carta de ACCIÓN con la que el bot de humanos_1 lanza el bombardeo
    /// (HistoriaBombardeo.cs). Disparo lejano (habilidad 3) y coste 0: el bot
    /// no paga nada por bombardear. Solo la juega el bot.
    public const string NefAndanada = "hist_excl_nef_andanada";

    // IMAGEN y DESCRIPCIÓN de cada carta exclusiva. Edita estas variables para
    // cambiar el arte (URL, igual que el campo `Imagen` de `Cartas`) o el texto.
    // Una imagen vacía muestra el placeholder de carta sin ilustración.
    public static string ImagenGeneralTransUniversal = "";
    public static string DescripcionGeneralTransUniversal =
        "Comandante de un ejército que ha cruzado el velo entre universos. " +
        "Donde él pisa, la realidad se pliega a su voluntad.";

    public static string ImagenSoldadoTransUniversal = "";
    public static string DescripcionSoldadoTransUniversal =
        "Infantería forjada en un universo en guerra perpetua. " +
        "Avanza en formación sin conocer el miedo.";

    public static string ImagenCriaturaTransUniversal = "";
    public static string DescripcionCriaturaTransUniversal =
        "Bestia de una dimensión desconocida, arrastrada a este mundo " +
        "por la grieta de Diente de Invierno.";

    public static string ImagenProteccionEspacioTemporal = "";
    public static string DescripcionProteccionEspacioTemporal =
        "Dobla el espacio y el tiempo alrededor de una región lejana: " +
        "nada del enemigo puede entrar ni actuar sobre ella durante 3 turnos.";

    public static string ImagenAnimalCombateTransUniversal = "";
    public static string DescripcionAnimalCombateTransUniversal =
        "Criatura de guerra domesticada más allá de las estrellas. " +
        "Barata, rápida de desplegar y siempre hambrienta.";

    public static string ImagenAndanadaMonolito = "";
    public static string DescripcionAndanadaMonolito =
        "Desde lo alto del Monolito, los Lanzarocas nefilim barren el campo " +
        "fila a fila. Nada de lo que esté en una celda alcanzada sobrevive.";

    /// Todas las cartas exclusivas de historia, por id. Propiedad (no campo)
    /// para que los cambios de imagen/descripción de arriba se lean siempre al
    /// día, sin depender del orden de inicialización estática.
    public static IReadOnlyDictionary<string, CartaExclusivaHistoria> CartasExclusivas =>
        new Dictionary<string, CartaExclusivaHistoria>
        {
            // a. General Trans-Universal · F78 D35 M4 · Coste 25
            [TuGeneral] = new(
                Id: TuGeneral,
                Nombre: "General Trans-Universal",
                Descripcion: DescripcionGeneralTransUniversal,
                Imagen: ImagenGeneralTransUniversal,
                Ejercito: EjercitoTransUniversal,
                Fuerza: 78, Defensa: 35, Movimiento: 4, Coste: 25,
                Tipo: 1),
            // b. Soldado Trans-Universal · F25 D10 M3 · Coste 15
            [TuSoldado] = new(
                Id: TuSoldado,
                Nombre: "Soldado Trans-Universal",
                Descripcion: DescripcionSoldadoTransUniversal,
                Imagen: ImagenSoldadoTransUniversal,
                Ejercito: EjercitoTransUniversal,
                Fuerza: 25, Defensa: 10, Movimiento: 3, Coste: 15,
                Tipo: 1),
            // c. Criatura Trans-Universal · F40 D25 M4 · Coste 20
            [TuCriatura] = new(
                Id: TuCriatura,
                Nombre: "Criatura Trans-Universal",
                Descripcion: DescripcionCriaturaTransUniversal,
                Imagen: ImagenCriaturaTransUniversal,
                Ejercito: EjercitoTransUniversal,
                Fuerza: 40, Defensa: 25, Movimiento: 4, Coste: 20,
                Tipo: 1),
            // d. Protección Espacio-Temporal · carta de ACCIÓN (Condicion 4)
            //    con habilidad 14 = Escudo lejano · Coste 50 · resto a 0.
            [TuProteccion] = new(
                Id: TuProteccion,
                Nombre: "Protección Espacio-Temporal",
                Descripcion: DescripcionProteccionEspacioTemporal,
                Imagen: ImagenProteccionEspacioTemporal,
                Ejercito: EjercitoTransUniversal,
                Fuerza: 0, Defensa: 0, Movimiento: 0, Coste: 50,
                Tipo: 1,
                Condicion: 4,
                IdHabilidad: 14,
                CosteHabilidad: 0),
            // e. Animal de combate Trans-Universal · F10 D5 M2 · Coste 3
            [TuAnimal] = new(
                Id: TuAnimal,
                Nombre: "Animal de combate Trans-Universal",
                Descripcion: DescripcionAnimalCombateTransUniversal,
                Imagen: ImagenAnimalCombateTransUniversal,
                Ejercito: EjercitoTransUniversal,
                Fuerza: 10, Defensa: 5, Movimiento: 2, Coste: 3,
                Tipo: 1),
            // f. Andanada del Monolito · carta de ACCIÓN (Condicion 4) con
            //    habilidad 3 = Disparo lejano · Coste 0 · resto a 0. La usa el
            //    bot de humanos_1 para el bombardeo (HistoriaBombardeo.cs).
            [NefAndanada] = new(
                Id: NefAndanada,
                Nombre: "Andanada del Monolito",
                Descripcion: DescripcionAndanadaMonolito,
                Imagen: ImagenAndanadaMonolito,
                Ejercito: 4,
                Fuerza: 0, Defensa: 0, Movimiento: 0, Coste: 0,
                Tipo: 1,
                Condicion: 4,
                IdHabilidad: 3,
                CosteHabilidad: 0),
        };

    /// True si [cartaId] es una carta exclusiva de historia.
    public static bool EsCartaExclusiva(string? cartaId) =>
        !string.IsNullOrEmpty(cartaId) && CartasExclusivas.ContainsKey(cartaId);

    /// Documentos de catálogo de todas las cartas exclusivas (id → campos).
    public static Dictionary<string, Dictionary<string, object?>> CatalogoExclusivas() =>
        CartasExclusivas.ToDictionary(kv => kv.Key, kv => kv.Value.ACatalogo());

    /// Todas las batallas registradas. El orden no importa (se indexan por Id).
    public static readonly IReadOnlyList<HistoriaDef> Todas = new List<HistoriaDef>
    {
        Demonios1(),
        Demonios2(),
        Demonios3(),
        Humanos1(),
        // … aquí irán las batallas restantes (humanos_2, humanos_3, …)
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
                new CartaHistoria(DemA, 5),
                new CartaHistoria(DemB, 1),
                new CartaHistoria(DemC, 1),
                //new CartaHistoria(DemE, 1),
            },
            EnergiaInicial: 40),
        Bot: new BandoHistoria(
            Ejercito: 1,                 // Humanos
            Objetivo: ObjetivoHistoria.Conquistar,
            // TODO(fase 2): fijar la coord del cuartel del bot en `diente_invierno`.
            Cuartel: null,
            Cartas: new[]
            {
                new CartaHistoria(HumA, 9),
                //new CartaHistoria(HumB, 3),
                // Refuerzo: 2 copias MÁS de HumB (8KZtDtblcypCtFfDSF08) que
                // nacen YA EVOLUCIONADAS desde el turno 1.
                new CartaHistoria(HumB, 4, Evolucionadas: 4),
                new CartaHistoria(HumC, 3),
            },
            EnergiaInicial: 40),
        BotDificultad: "alta",
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
        SiguienteId: "demonios_3",       // Demonios3() (más abajo)
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
                new CartaHistoria(HumA, 13),  // 8 de la parte 1 + 3 de refuerzo 1
                new CartaHistoria(HumB, 6),   // 3 de la parte 1 + 2 de refuerzo 1
                // Refuerzo: 2 copias MÁS de HumB (8KZtDtblcypCtFfDSF08) que
                // nacen YA EVOLUCIONADAS desde el turno 1 (igual que en la parte 1).
                new CartaHistoria(HumB, 3, Evolucionadas: 3),
                new CartaHistoria(HumC, 4), //2
            },
            EnergiaInicial: 40),
        BotDificultad: "medio",
        BotEstilo: "agresivo",
        // Parte intermedia: no desbloquea nada; el cliente encadena con
        // `SiguienteId`. Al perder se vuelve a la parte 1.
        DesbloqueaHistoriaId: null,
        PrimeraParteId: "demonios_1");

    /// DEMONIOS · Historia 1 · Parte 3 de 3 (ÚLTIMA).
    /// Cambio de modo: ya no es un asedio sino una PARTIDA NORMAL de 2 jugadores
    /// en `diente_invierno_Campos_De_Batalla`, contra un ejército venido de otro
    /// universo (cartas exclusivas Trans-Universales).
    ///
    ///   • Jugador: por primera vez un mazo que MEZCLA dos ejércitos, Humanos y
    ///     Demonios (los supervivientes del asedio se alían). Su base empieza
    ///     VACÍA: los 3 generales demonios (Colmillos de Gehena, Martynara y
    ///     xi9ys2LqcHNVfFPNRsef) se COMPRAN en el cuartel durante la partida
    ///     (`EspecialesCuartel`). Roba del mazo de 7 cartas de abajo.
    ///   • Bot: mazo de las 5 cartas Trans-Universales. Nace sin nada en el
    ///     tablero y lo despliega todo desde su mano con la IA real de los bots.
    ///   • Gana quien conquiste el cuartel rival (sin límite de turnos).
    ///
    /// Energía inicial 15 por bando, la misma que en una partida normal.
    /// Perder obliga a reempezar por la parte 1 (`PrimeraParteId`).
    private static HistoriaDef Demonios3() => new(
        Id: "demonios_3",
        EjercitoCampana: 3,              // Demonios
        Orden: 1,
        Parte: 3,
        Partes: 3,
        SiguienteId: null,               // ÚLTIMA parte: al ganarla se desbloquea
        Titulo: "Diente de Invierno · Campos de batalla",
        MapaId: "diente_invierno_Campos_De_Batalla",
        TurnosSupervivencia: 0,          // sin victoria por supervivencia
        SuerteDelPerdedor: 3,
        Jugador: new BandoHistoria(
            Ejercito: 3,                 // Demonios (campaña); el mazo es mixto
            Objetivo: ObjetivoHistoria.Conquistar,
            Cuartel: null,
            // La base empieza VACÍA: los generales demonios ya no nacen en el
            // tablero, se COMPRAN en el cuartel durante la partida.
            Cartas: Array.Empty<CartaHistoria>(),
            // Generales disponibles en el cuartel (una compra de cada uno).
            EspecialesCuartel: new[]
            {
                DemB,   // yEwMBTHhiVqgL1OIZsUI · Colmillos de Gehena
                DemD,   // qrc2GYYSEhjLoISdxQch · Martynara
                DemF,   // xi9ys2LqcHNVfFPNRsef
            },
            // Mazo MIXTO Humanos + Demonios (mano inicial + robo por turno).
            Mazo: new[]
            {
                new CartaHistoria(HumA, 1),   // xPcw2Adpdfdb8TMp4Uiy
                new CartaHistoria(HumB, 1),   // 8KZtDtblcypCtFfDSF08
                new CartaHistoria(HumC, 1),   // kKJl1PyTsfIytyfOkfiS
                new CartaHistoria(DemA, 1),   // jFpE0EY9dJQdME2iM2y9
                new CartaHistoria(DemC, 1),   // stF3jOzQyQvVJguGblKj · Ogro
                new CartaHistoria(DemG, 1),   // piqdzNbXTy1xt2ibg8Oi
                new CartaHistoria(DemH, 1),   // 0qGtiuXP9aqAmxEwVzhc
            },
            EnergiaInicial: 15),
        Bot: new BandoHistoria(
            Ejercito: EjercitoTransUniversal,
            Objetivo: ObjetivoHistoria.Conquistar,
            Cuartel: null,
            Cartas: Array.Empty<CartaHistoria>(),   // lo despliega todo desde la mano
                                                    // Mazo Trans-Universal: las 5 cartas, una de cada. Igual que en
                                                    // PvP, cada turno se roba 1 al azar de estas 5 (sin límite).
            Mazo: new[]
            {
                new CartaHistoria(TuGeneral, 1),
                new CartaHistoria(TuSoldado, 1),
                new CartaHistoria(TuCriatura, 1),
                new CartaHistoria(TuProteccion, 1),
                new CartaHistoria(TuAnimal, 1),
            },
            EnergiaInicial: 15,
            Alias: "Trans-Universales"),
        BotDificultad: "medio",
        BotEstilo: "agresivo",
        // TODO: pon aquí el docId de la colección `Historias` que debe quedar
        // desbloqueado al completar la historia. Con null se usa "demonios_3".
        DesbloqueaHistoriaId: null,
        PrimeraParteId: "demonios_1",
        Modo: ModoHistoria.PartidaNormal);

    /// HUMANOS · Historia 1 "Los hermanos del alba" · Parte 1 de 3.
    /// Asedio con los papeles CAMBIADOS en `MonolitoNefilim` (12×15, un
    /// cuartel en cada esquina):
    ///
    ///   • Jugador (Humanos): ATACA. Gana si conquista el cuartel nefilim. No
    ///     hay límite de turnos.
    ///   • Bot (Nefilim): gana cuando el jugador se queda sin cartas en el
    ///     tablero (`DerrotaJugadorSinCartas`).
    ///       – GUARNICIÓN: el Capitán Anac y 3 Soldados de la ira SIN evolucionar
    ///         no salen nunca del cuartel.
    ///       – CAZADORES: el resto sale a cazar. El bombardeo nunca cae donde
    ///         terminan (se redirige en su fila) y persiguen a las cartas del jugador;
    ///         solo entran en su celda si el grupo que llega le gana.
    ///   • BOMBARDEO POR FILAS (HistoriaBombardeo.cs · "humanos_1"): en CADA
    ///     fila caen 4 disparos en los turnos 1-2, 5 en los 3-4 y 6 desde el 5,
    ///     en celdas al azar de la fila. Cada turno se publica el % de cada
    ///     celda; los grupos de más de 3 cartas suben el % de su zona.
    ///   • CASILLAS DESACTIVADORAS: una en el centro y otra al azar, cambian
    ///     cada 5 turnos. Ocuparlas quita 1 disparo EN CADA FILA el turno
    ///     siguiente y forma un escudo del jugador de 3 turnos sobre ellas.
    ///   Todo se calcula con el tamaño real del mapa (pensado para 12×15).
    ///
    /// Energía 40 por bando y "suerte del perdedor" de +3 Ø por turno.
    private static HistoriaDef Humanos1() => new(
        Id: "humanos_1",
        EjercitoCampana: 1,              // Humanos
        Orden: 1,
        Parte: 1,
        Partes: 3,
        // OJO: la parte 2 aún no existe. Hasta que se añada `Humanos2()`, ganar
        // esta parte mostrará "historia desconocida: humanos_2" al encadenar.
        SiguienteId: "humanos_2",
        Titulo: "Los hermanos del alba · La lluvia del Monolito",
        MapaId: "MonolitoNefilim",
        TurnosSupervivencia: 0,          // sin límite de turnos
        SuerteDelPerdedor: 3,
        Jugador: new BandoHistoria(
            Ejercito: 1,                 // Humanos
            Objetivo: ObjetivoHistoria.Conquistar,
            // Cuartel FIJO del jugador: K14 (continente 2 de MonolitoNefilim).
            // Sin fijarlo, el reparto automático le daba el primer obelisco
            // por orden alfabético y los cuarteles salían al revés.
            Cuartel: "K14",
            Cartas: new[]
            {
                new CartaHistoria(HumA, 10, Evolucionadas: 2),   // Soldado celeste
                new CartaHistoria(HumD, 1),                      // General Albariel
                new CartaHistoria(HumE, 1),                      // Capitán esmeralda
            },
            EnergiaInicial: 40),
        Bot: new BandoHistoria(
            Ejercito: 4,                 // Nefilim
            Objetivo: ObjetivoHistoria.Sobrevivir,
            Cuartel: null,               // el otro obelisco del mapa (≠ K14)
            Cartas: new[]
            {
                // Guarnición: Capitán Anac (no sale nunca del cuartel).
                new CartaHistoria(NefA, 1, Guarnicion: 1),
                new CartaHistoria(NefB, 8, Evolucionadas: 3),    // Lanzarocas
                // Soldado de la ira: 4 evolucionadas + 6 sin evolucionar, de
                // las que 3 se quedan de guarnición y el resto sale a cazar.
                new CartaHistoria(NefC, 10, Evolucionadas: 4, Guarnicion: 3),
                new CartaHistoria(NefD, 2),                      // Luz de la soberbia
            },
            EnergiaInicial: 40),
        BotDificultad: "medio",
        BotEstilo: "agresivo",
        DesbloqueaHistoriaId: null,      // parte intermedia
        PrimeraParteId: null,            // es la parte 1
        Modo: ModoHistoria.Asedio,
        DerrotaJugadorSinCartas: true,
        ComportamientoBot: ComportamientoBotHistoria.Cazar,
 Introduccion:
     "Archivo Militar de Ciudad Celeste\r\nOperación: Cielo Quebrado\r\nAño 118 d.s. " +
     "Fuerzas desplegadas:\r\n\r\nBatallón 86— Guardia del Alba\r\nComandante: General Alvaroth, Escudo de la Humanidad." +
     "Batallón 19 — Tormenta Celeste\r\nComandante: General Albariel, Filo de la Humanidad. " +
     "La batalla comenzó en el lugar donde los dos generales habían nacido.De la antigua colonia apenas quedaba nada." +
     "Los campos que durante seis años alimentaron a cientos de familias habían desaparecido bajo extensiones de piedra negra. " +
     "Los ríos fueron desviados hacia profundas grietas abiertas en la montaña. Las casas permanecían sepultadas bajo murallas Nefilim.");
}