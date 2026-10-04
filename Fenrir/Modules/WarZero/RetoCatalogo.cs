// ─────────────────────────────────────────────────────────────────────────────
// RetoCatalogo.cs
//
// Catálogo de RETOS. Hay dos clases de reto:
//
// 1) RETO DE PARTIDA NORMAL (el de siempre). Una partida normal (mismo
//    tablero, mismas reglas y misma pantalla de juego que el PvP) que el
//    servidor monta ya hecha:
//      · mapa fijo,
//      · ejército fijo para el jugador,
//      · rivales fijos (bots CONCRETOS de la colección `Bots`, por uid),
//      · una REGLA DE ENFRENTAMIENTO propia (ver `RetoModoBots`),
//      · y, opcionalmente, TROFEOS que se otorgan al ganarlo. Se asignan desde
//        la app (Edición · Trofeos → "al completar un reto"); `TrofeoId` es el
//        sistema anterior y solo se conserva por compatibilidad.
//    No se siembran cartas ni se usa un bot sintético: los rivales son los
//    mismos runners de bot que rellenan salas públicas (WarZeroBot.cs), con su
//    perfil de dificultad y estilo. Lo único que cambia es a QUIÉN miran, y de
//    eso se encarga RetoFoco.cs.
//
// 2) RETO SOBRE EL MOTOR DE HISTORIA (`HistoriaId`). La partida la monta el
//    modo historia (WarZeroHistoria.CrearPartidaHistoriaAsync) a partir de
//    una batalla de HistoriaCatalogo marcada con `RetoId`: tablero sembrado a
//    mano, bot sintético y reglas propias (p. ej. el duelo de generales). El
//    trofeo se otorga igual que en un reto normal.
//
// Para añadir un reto nuevo basta con registrarlo aquí y darle un hueco en la
// lista de la pantalla de retos del cliente (reto_service.dart →
// kRetosDisponibles).
// ─────────────────────────────────────────────────────────────────────────────

/// Cómo se comportan los bots entre ellos dentro del reto.
public enum RetoModoBots
{
    /// Comportamiento normal: cada bot juega contra todos (guerra de todos).
    Libre,

    /// Todos los bots tienen UN solo objetivo: el jugador humano. No se buscan
    /// entre ellos ni se disputan sus cuarteles (ver RetoFoco.cs). Si dos bots
    /// coinciden en la misma celda, el combate lo resuelve el servidor como
    /// siempre: pueden destruirse, pero nunca porque se hayan buscado.
    TodosContraElJugador,
}

/// Definición de un reto del catálogo.
public sealed class RetoDef
{
    /// Id estable del reto (viaja del cliente al servidor y al doc de partida).
    public required string Id { get; init; }

    /// Número de orden en la lista de retos de la pantalla (1..N).
    public required int Orden { get; init; }

    public required string Titulo { get; init; }
    public string Descripcion { get; init; } = "";

    /// Texto con el que se abre la VENTANA EXPLICATIVA del reto (la que sale
    /// antes de empezar, como en el modo historia). Vacío = la descripción.
    /// En los retos sobre el motor de historia manda la explicación de la
    /// batalla (HistoriaDef.Introduccion / SeccionesExplicacion).
    public string Introduccion { get; init; } = "";

    /// Consejos para la ventana explicativa (una línea por consejo).
    public IReadOnlyList<string> Consejos { get; init; } = Array.Empty<string>();

    /// Id del documento del mapa en la colección `Mapas`.
    public required string MapaId { get; init; }

    /// Nombre del mapa (campo `nombre` del documento). Solo se usa como
    /// FALLBACK: si no existe `Mapas/{MapaId}`, se busca el mapa cuyo `nombre`
    /// coincida con este texto. Así el reto no se rompe si el documento del
    /// mapa se creó con un id autogenerado en vez de con su nombre.
    public string MapaNombre { get; init; } = "";

    /// Ejército (1..4) con el que juega el humano.
    public required int EjercitoJugador { get; init; }

    /// Uids de los bots rivales, en la colección `Bots`. Vacía en los retos
    /// sobre el motor de historia (allí el rival es el bot de historia).
    public List<string> Bots { get; init; } = new();

    /// Regla de enfrentamiento entre los bots.
    public RetoModoBots ModoBots { get; init; } = RetoModoBots.TodosContraElJugador;

    /// Modo de turno de la partida ("rapida" | "turno12h" | "diario").
    public string ModoTurno { get; init; } = "rapida";

    /// Si no es vacío, el reto se juega sobre el MOTOR DE HISTORIA con esta
    /// batalla de HistoriaCatalogo (que debe llevar `RetoId` = este Id). En ese
    /// caso `Bots`, `ModoBots` y `ModoTurno` no se usan.
    public string HistoriaId { get; init; } = "";

    /// True si el reto se monta con el motor de historia.
    public bool EsDeHistoria => !string.IsNullOrWhiteSpace(HistoriaId);

    /// SISTEMA ANTERIOR (se mantiene por compatibilidad; déjalo en "").
    ///
    /// Los trofeos de un reto se asignan ahora desde la app, en Edición ·
    /// Trofeos → "Cómo se consigue: al completar un reto", que guarda en el
    /// trofeo `Origen = "reto"` y `OrigenId = <Id de este reto>`. No hace falta
    /// tocar código ni redesplegar.
    ///
    /// Si aun así se rellena, este trofeo también se otorga al GANAR el reto
    /// (`WarZeroService.OtorgarTrofeoRetoSiProcedeAsync`), de forma idempotente.
    public string TrofeoId { get; init; } = "";

    /// Nº de jugadores de la partida (humano + bots; 2 en los de historia).
    public int MaxJugadores => EsDeHistoria ? 2 : 1 + Bots.Count;
}

public static class RetoCatalogo
{
    public const string ResistenciaDemoniaca = "resistencia_demoniaca";
    public const string ResistenciaHumana8 = "resistencia_humana_8";
    public const string DueloAlexander = "duelo_alexander";

    private static readonly Dictionary<string, RetoDef> _defs = new()
    {
        // ── Reto 1 · Resistencia demoníaca ───────────────────────────────────
        // 4 jugadores en el mapa clásico de 4: el humano con Demonios contra
        // bot_20, bot_21 y bot_22, que van TODOS a por él.
        [ResistenciaDemoniaca] = new RetoDef
        {
            Id = ResistenciaDemoniaca,
            Orden = 1,
            Titulo = "Resistencia demoníaca",
            Descripcion = "Tres ejércitos, un solo objetivo: tú. Aguanta con los Demonios.",
            Introduccion = "Tres ejércitos han firmado una tregua con un único fin: borrar a los " +
                           "Demonios del mapa. No se atacarán entre ellos mientras tú sigas en pie.",
            Consejos = new[]
            {
                "Refuerza tu cuartel antes de salir a farmear: van a llegar por varios lados a la vez.",
                "Los bots se esquivan entre ellos: aprovecha los pasillos que dejan libres.",
                "Un cuartel enemigo conquistado es un rival menos (y sus energías para ti).",
            },
            MapaId = "Clasica4J",
            MapaNombre = "Clasica4J",
            EjercitoJugador = 3,                 // Demonios
            Bots = new List<string> { "bot_20", "bot_21", "bot_22" },
            ModoBots = RetoModoBots.TodosContraElJugador,
            ModoTurno = "rapida",
            // Trofeos: se asignan desde Edición · Trofeos (ver RetoDef.TrofeoId).
            TrofeoId = "",
        },

        // ── Reto 2 · Resistencia humana (8 jugadores) ────────────────────────
        // El mismo reto, a lo grande: 8 jugadores en Mapa8J, el humano con los
        // Humanos contra 7 bots que van TODOS a por él (RetoFoco).
        // Para 8 jugadores hacen falta 7 bots: bot_42 … bot_48. Si alguno no
        // existe en `Bots`, juega igual con el perfil por defecto y un
        // ejército distinto del tuyo (LeerBotsRetoAsync).
        [ResistenciaHumana8] = new RetoDef
        {
            Id = ResistenciaHumana8,
            Orden = 2,
            Titulo = "Resistencia humana",
            Descripcion = "Siete ejércitos, un solo objetivo: tú. Aguanta con los Humanos.",
            Introduccion = "Siete ejércitos se han aliado contra la Humanidad. Ocho cuarteles, un solo " +
                           "enemigo común: tú. No se atacarán entre ellos mientras sigas en pie.",
            Consejos = new[]
            {
                "Con siete rivales no puedes defender todos los frentes: elige uno y ábrete paso.",
                "Refuerza tu cuartel antes de salir: llegarán por varios lados a la vez.",
                "Los bots se esquivan entre ellos: aprovecha los pasillos que dejan libres.",
                "Cada cuartel que conquistas es un rival menos.",
            },
            MapaId = "Mapa8J",
            MapaNombre = "Mapa8J",
            EjercitoJugador = 1,                 // Humanos
            Bots = new List<string>
            {
                "bot_42", "bot_43", "bot_44", "bot_45", "bot_46", "bot_47", "bot_48",
            },
            ModoBots = RetoModoBots.TodosContraElJugador,
            ModoTurno = "rapida",
            TrofeoId = "",
        },

        // ── Reto 3 · El duelo de Alexander ───────────────────────────────────
        // La parte 3 de «Los hermanos del alba» al revés: tú eres Alexander y
        // la máquina lleva a Alvaroth y Albariel. Se juega con el motor de
        // historia (duelo de generales invertido, HistoriaDuelo.cs). Vidas,
        // turnos y probabilidades: AjustesRetoAlexander.
        [DueloAlexander] = new RetoDef
        {
            Id = DueloAlexander,
            Orden = 3,
            Titulo = "El duelo de Alexander",
            Descripcion = "Eres Alexander. Haz caer la lluvia de rocas sobre Alvaroth y Albariel y derriba a uno.",
            MapaId = "MonolitoNefilim3",
            MapaNombre = "MonolitoNefilim3",
            EjercitoJugador = 4,                 // Nefilim
            HistoriaId = HistoriaDuelos.IdRetoAlexander,
            TrofeoId = "",
        },
    };

    /// Definición de un reto por id, o null si no existe.
    public static RetoDef? Get(string? id)
        => id != null && _defs.TryGetValue(id, out var d) ? d : null;

    /// Todos los retos publicados, por orden.
    public static IReadOnlyList<RetoDef> Todos =>
        _defs.Values.OrderBy(d => d.Orden).ToList();
}