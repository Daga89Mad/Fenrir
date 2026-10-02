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

    /// DUELO DE GENERALES (humanos_3): el bot es un jefe con vidas y
    /// habilidades, sin combate normal. Ver HistoriaDuelo.cs.
    Duelo,
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

/// PAPEL de una carta del bot en un asedio con `ComportamientoBot` = Cazar
/// (p. ej. humanos_2). El cliente lo pinta sobre el tablero (🎯 / ⚔).
public enum RolBotHistoria
{
    /// Según el comportamiento del bot (en Cazar = cazador).
    Auto,

    /// Sale a CAZAR las cartas del jugador (PlanCaza).
    Cazador,

    /// No caza: se agrupa en el punto de reunión (`HistoriaDef.ReunionAsalto`)
    /// y asalta el cuartel del jugador en el turno del asalto general
    /// (`HistoriaDef.TurnoAsaltoGeneral`).
    Asalto,
}

/// Una carta y su cantidad dentro del despliegue inicial o del mazo de un
/// bando. En `Cartas` las `Cantidad` copias nacen APILADAS en la celda del
/// cuartel (o en `Coord`, si se indica); en `Mazo` son las copias de esa carta
/// dentro del mazo.
///
/// `Evolucionadas` (solo en `Cartas`, 0 por defecto): cuántas de esas copias
/// nacen YA EVOLUCIONADAS. En vez de la carta base se siembra la carta de su
/// `IdEvolucion` del catálogo, con sus estadísticas, desde el turno 1. Si la
/// carta no tiene evolución, se siembran sin evolucionar (y se avisa por
/// consola). En `Mazo` se ignora.
///
/// `Guarnicion` (solo en `Cartas`, 0 por defecto): cuántas de esas copias
/// forman la GUARNICIÓN del cuartel: nacen marcadas y no se mueven nunca. En
/// el BOT, sea cual sea el `ComportamientoBot`; en el JUGADOR, el servidor
/// devuelve a su celda cualquier movimiento suyo (y el cliente no deja
/// moverlas). Se marcan primero las copias SIN evolucionar (las últimas de la
/// entrada).
///
/// `Coord` (solo en `Cartas`): celda donde nacen estas copias. null = el
/// cuartel del bando (comportamiento de siempre).
///
/// `Vip` (solo en `Cartas` del JUGADOR): carta CLAVE. Si muere, el jugador
/// pierde la batalla (EvaluarFinHistoria). El cliente la marca con 👑.
///
/// `Rol` (solo en `Cartas` del BOT): papel de estas copias (ver
/// `RolBotHistoria`).
public record CartaHistoria(
    string CartaId,
    int Cantidad,
    int Evolucionadas = 0,
    int Guarnicion = 0,
    string? Coord = null,
    bool Vip = false,
    RolBotHistoria Rol = RolBotHistoria.Auto);

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
    /// En la última parte, null = se busca AUTOMÁTICAMENTE el documento de
    /// `Historias` con Ejercito == EjercitoCampana y Orden == Orden (el que
    /// creó el editor). Ponlo solo para forzar otro documento.
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
    IReadOnlyList<SeccionExplicacion>? SeccionesExplicacion = null,
    /// Si true, el JUGADOR GANA en cuanto, tras resolver un turno, no queda
    /// ninguna carta del bot en el tablero y ya no le quedan oleadas por
    /// llegar (humanos_2).
    bool VictoriaSinEnemigos = false,
    /// Turno a partir del cual TODAS las cartas del bot dejan de cazar y van a
    /// por el cuartel del jugador (asalto general). 0 = nunca.
    int TurnoAsaltoGeneral = 0,
    /// Punto de REUNIÓN del asalto: las cartas de asalto esperan aquí y, en el
    /// asalto general, se reúnen aquí y entran juntas en el cuartel del jugador
    /// (todas a la vez, para sumar su fuerza). Debe estar a una distancia del
    /// cuartel del jugador que TODAS las cartas del bot cubran en un turno.
    /// null = sin reunión (van directas).
    string? ReunionAsalto = null,
    /// Turnos máximos que el asalto general espera a los rezagados en el punto
    /// de reunión antes de lanzarse con lo que haya.
    int EsperaMaxReunion = 2,
    /// Si true, los cazadores suponen que tus grupos HUYEN hacia tu cuartel
    /// (en vez de avanzar hacia el del bot) al predecir a dónde los mueves.
    bool PresasHuyenACasa = false,
    /// Si true, el cuartel del bot es solo NOMINAL: el jugador no puede entrar
    /// en él (el servidor revierte el movimiento) y no hay victoria por
    /// conquista (humanos_2: el bot no tiene base que defender).
    bool CuartelBotInaccesible = false)
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
    private const string HumAlvaroth = "uOfCRljbVXoRNWXwv6gL"; // General Alvaroth · F48 D30 M2 · humanos_2
    private const string HumSoren = "DJNK0WTY8k55sTPZGEsZ";    // Capitán Soren · F28 D10 M4 · humanos_2
    private const string HumDefensor = "2nSmuTkVPutvQV9B9CO3"; // Defensor celeste · F1 D8 M1 (→ Regimiento defensor celeste F3 D13 M2)
    private const string HumSoldado = "nkl4wi3EY2th4xjLTSC3";  // Soldado azul · F2 D2 M1 (→ Regimiento de asalto azul F9 D4 M2)
    private const string NefAlexander = "VKM1uUkqO9GqDI6tTr47"; // General Alexander · F40 D35 M2 · humanos_3

    // NEFILIM (bot de humanos_1)
    private const string NefA = "VmhD1AghdOBjmRfpSzk4"; // Capitán Anac
    private const string NefB = "iju32O3HHqU27hmoVhhn"; // Lanzarocas
    private const string NefC = "qeygP8oT7WYKqmq1kvHI"; // Soldado de la ira
    private const string NefD = "tSWupmUokszJJfLRJaFC"; // Luz de la soberbia · F8 D3 M6 aire (→ Oscuridad de la soberbia F25 D10 M5)
    private const string NefTemplanza = "t6UG89p61rrus0ZO3TUN"; // Soldado de la templanza · F3 D4 M2 (→ Regimiento de la templanza F15 D10 M3)

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
        Humanos2(),
        Humanos3(),
        // … aquí irán las batallas restantes
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
        // null → se desbloquea el documento de `Historias` con Ejercito = 3
        // (Demonios) y Orden = 1, sea cual sea su docId.
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
        SiguienteId: "humanos_2",        // Humanos2() (más abajo)
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
            "Los hermanos del alba marchan hacia el Monolito Nefilim. Desde lo " +
            "alto, sus andanadas barren el campo fila a fila y sus tropas salen " +
            "a cazar a quien avance. Solo quien sepa dispersarse llegará vivo " +
            "a sus puertas.");

    /// HUMANOS · Historia 1 "Los hermanos del alba" · Parte 2 de 3.
    /// `MonolitoNefilim2` (10×16). El jugador DEFIENDE su cuartel (A2) y tiene
    /// que proteger a dos cartas clave:
    ///
    ///   • A11 · General Alvaroth (👑, si muere pierdes) con 4 Defensores
    ///     celestes y 4 Soldados azules: lentos, se quedan de cebo.
    ///   • G14 · Capitán Soren (👑) con 3 Defensores y 3 Soldados (2 y 2 ya
    ///     evolucionados). Su camino es el TÚNEL INUNDABLE (HistoriaTuneles.cs ·
    ///     "humanos_2"), que sale junto a A2.
    ///   • A2 · cuartel con 3 Tanquetas ónix (1 evolucionada) de GUARNICIÓN:
    ///     no se mueven nunca.
    ///
    /// Bot (Nefilim), en A13 y sin base que defender (J16 es solo nominal, el
    /// jugador no puede entrar):
    ///   • 10 Soldados de la templanza (3 ya Regimientos, Mov 3) CAZADORES:
    ///     priorizan a Alvaroth y se comen el cebo que puedan.
    ///   • 1 Luz de la soberbia de ASALTO: espera en C2.
    ///   • Turno 3: 3 Luces + 1 Oscuridad de la soberbia (asalto).
    ///   • Turno 7: 2 Regimientos de la templanza (asalto).
    ///     (HistoriaGuionOleadas.cs · "humanos_2")
    ///   • Desde el turno 8 (ASALTO GENERAL) todo el ejército se reúne en C2 y
    ///     entra junto en A2 cuando están todos (espera hasta 4 turnos a los
    ///     rezagados: en la práctica golpea hacia el turno 11-12).
    ///
    /// El jugador GANA cuando no queda ninguna carta enemiga (y ya han llegado
    /// todos los refuerzos). PIERDE si muere Alvaroth o Soren o si conquistan
    /// A2. Equilibrio (gana el bando con más F + D en la casilla): pila de
    /// A11 130 · asalto intacto ≈ 253 contra A2 + Alvaroth 227 → sin Soren el
    /// cuartel cae; con Soren (≈ 85-109) aguanta.
    private static HistoriaDef Humanos2() => new(
        Id: "humanos_2",
        EjercitoCampana: 1,              // Humanos
        Orden: 1,
        Parte: 2,
        Partes: 3,
        SiguienteId: "humanos_3",        // Humanos3() (más abajo)
        Titulo: "Los hermanos del alba · El túnel de Soren",
        MapaId: "MonolitoNefilim2",
        TurnosSupervivencia: 0,          // sin victoria por supervivencia
        SuerteDelPerdedor: 3,
        Jugador: new BandoHistoria(
            Ejercito: 1,                 // Humanos
            Objetivo: ObjetivoHistoria.Sobrevivir,
            Cuartel: "A2",
            Cartas: new[]
            {
                // ── Grupo de Alvaroth · A11 ──────────────────────────────
                new CartaHistoria(HumAlvaroth, 1, Coord: "A11", Vip: true),
                new CartaHistoria(HumDefensor, 4, Coord: "A11"),
                new CartaHistoria(HumSoldado, 4, Coord: "A11"),
                // ── Grupo de Soren · G14 (junto a la boca del túnel) ─────
                new CartaHistoria(HumSoren, 1, Coord: "G14", Vip: true),
                new CartaHistoria(HumDefensor, 3, Evolucionadas: 2, Coord: "G14"),
                new CartaHistoria(HumSoldado, 3, Evolucionadas: 2, Coord: "G14"),
                // ── Cuartel A2 · guarnición fija ─────────────────────────
                new CartaHistoria(HumB, 3, Evolucionadas: 1, Guarnicion: 3),
            },
            EnergiaInicial: 40),
        Bot: new BandoHistoria(
            Ejercito: 4,                 // Nefilim
            Objetivo: ObjetivoHistoria.Conquistar,
            // Cuartel NOMINAL (el motor necesita uno): no tiene cartas y el
            // jugador no puede entrar (`CuartelBotInaccesible`).
            Cuartel: "J16",
            Cartas: new[]
            {
                // 3 Regimientos (Mov 3) + 7 Soldados de la templanza: cazadores.
                new CartaHistoria(NefTemplanza, 10, Evolucionadas: 3, Coord: "A13",
                                  Rol: RolBotHistoria.Cazador),
                // 1 Luz de la soberbia: asalto (espera en el punto de reunión).
                new CartaHistoria(NefD, 1, Coord: "A13", Rol: RolBotHistoria.Asalto),
            },
            EnergiaInicial: 40),
        BotDificultad: "medio",
        BotEstilo: "agresivo",
        DesbloqueaHistoriaId: null,      // parte intermedia
        PrimeraParteId: "humanos_1",
        Modo: ModoHistoria.Asedio,
        DerrotaJugadorSinCartas: false,  // la derrota la marcan las cartas clave
        ComportamientoBot: ComportamientoBotHistoria.Cazar,
        Introduccion:
            "El Monolito ha caído, pero la retirada no ha terminado. El General " +
            "Alvaroth vuelve al cuartel con los suyos mientras los regimientos " +
            "de la templanza le pisan los talones. Al otro lado del valle, el " +
            "Capitán Soren solo tiene un camino: el viejo túnel bajo las rocas, " +
            "que el agua reclama paso a paso.",
        SeccionesExplicacion: new[]
        {
            new SeccionExplicacion("🧭", "Consejos",
                "• Alvaroth (Mov 2) es tan rápido como los Soldados de la templanza, " +
                "pero los Regimientos (Mov 3) le alcanzan. Solo, 3 Regimientos no " +
                "pueden con él; con un Soldado más, sí.\n" +
                "• Tus Defensores y Soldados (Mov 1) no pueden huir: úsalos de cebo " +
                "o de escolta.\n" +
                "• Gana la casilla el bando que suma más FUERZA + DEFENSA en ella. " +
                "Tu cuartel suma +80.\n" +
                "• Sin Soren, el cuartel no aguantará el asalto general: llévalo " +
                "a casa por el túnel."),
        },
        VictoriaSinEnemigos: true,
        TurnoAsaltoGeneral: 8,
        ReunionAsalto: "C2",
        EsperaMaxReunion: 4,
        PresasHuyenACasa: true,
        CuartelBotInaccesible: true);

    /// HUMANOS · Historia 1 "Los hermanos del alba" · Parte 3 de 3 (ÚLTIMA).
    /// DUELO DE GENERALES en `MonolitoNefilim3` (7×7): Alvaroth y Albariel
    /// contra el General Alexander. Sin combate normal: vidas y habilidades
    /// (HistoriaDuelo.cs · todos los números en `AjustesDueloAlexander`).
    ///
    ///   • Alvaroth (A1) lo PARALIZA al caer en su casilla; Albariel (A7) le
    ///     quita una vida si entra con él paralizado. 3 vidas y es tuyo.
    ///   • Alexander (G4): en los turnos pares CANALIZA la Lluvia de rocas (no
    ///     se mueve); cada 4 turnos lanza Rompe escudos contra Alvaroth.
    ///   • Alvaroth y Albariel tienen 2 vidas: si cae uno, pierdes. También
    ///     pierdes si Alexander sigue vivo al cerrar el turno límite.
    ///   • Pilares (bloqueadas): C3, C5, E3, E5, D1 y D7. D1 y D7 alojan los
    ///     cuarteles NOMINALES (nadie entra; no hay conquista).
    private static HistoriaDef Humanos3() => new(
        Id: "humanos_3",
        EjercitoCampana: 1,              // Humanos
        Orden: 1,
        Parte: 3,
        Partes: 3,
        SiguienteId: null,               // ÚLTIMA parte: al ganarla se desbloquea
        Titulo: "Los hermanos del alba · El duelo del Monolito",
        MapaId: "MonolitoNefilim3",
        TurnosSupervivencia: 0,
        SuerteDelPerdedor: 0,
        Jugador: new BandoHistoria(
            Ejercito: 1,                 // Humanos
            Objetivo: ObjetivoHistoria.Conquistar,
            Cuartel: "D1",               // nominal (pilar)
            Cartas: new[]
            {
                new CartaHistoria(HumAlvaroth, 1, Coord: "A1", Vip: true),
                new CartaHistoria(HumD, 1, Coord: "A7", Vip: true),   // General Albariel
            },
            EnergiaInicial: 0),
        Bot: new BandoHistoria(
            Ejercito: 4,                 // Nefilim
            Objetivo: ObjetivoHistoria.Sobrevivir,
            Cuartel: "D7",               // nominal (pilar)
            Cartas: new[]
            {
                new CartaHistoria(NefAlexander, 1, Coord: "G4"),
            },
            EnergiaInicial: 0),
        BotDificultad: "medio",
        BotEstilo: "agresivo",
        // null → se desbloquea el documento de `Historias` con Ejercito = 1
        // (Humanos) y Orden = 1, sea cual sea su docId.
        DesbloqueaHistoriaId: null,
        PrimeraParteId: "humanos_1",
        Modo: ModoHistoria.Asedio,
        DerrotaJugadorSinCartas: false,  // la derrota la marcan las vidas
        ComportamientoBot: ComportamientoBotHistoria.Duelo,
        Introduccion:
            "En la cima del Monolito espera el General Alexander. Ningún ejército " +
            "puede ayudaros aquí: solo Alvaroth, que sabe inmovilizarlo, y " +
            "Albariel, cuya espada puede herirlo. Tres veces habrá que alcanzarle " +
            "antes de que la lluvia de rocas os entierre.",
        SeccionesExplicacion: new[]
        {
            new SeccionExplicacion("🧭", "Consejos",
                "• Alexander se queda quieto cuando invoca la lluvia: es tu momento " +
                "para caer sobre él con Alvaroth.\n" +
                "• No entréis los dos a la vez en su casilla si está libre: os separa.\n" +
                "• Albariel mueve 3: déjalo cerca, pero sin pisar a Alexander si no " +
                "está paralizado.\n" +
                "• Esquiva las casillas con más % de rocas y, en los turnos de Rompe " +
                "escudos, piensa bien dónde termina Alvaroth."),
        },
        VictoriaSinEnemigos: true,       // cae Alexander → no quedan enemigos
        CuartelBotInaccesible: true);
}