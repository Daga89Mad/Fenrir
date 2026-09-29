// ─────────────────────────────────────────────────────────────────────────────
// RetoCatalogo.cs
//
// Catálogo de RETOS. Un reto es una partida normal (mismo tablero, mismas reglas
// y misma pantalla de juego que el PvP) que el servidor monta ya hecha:
//   · mapa fijo,
//   · ejército fijo para el jugador,
//   · rivales fijos (bots CONCRETOS de la colección `Bots`, por uid),
//   · una REGLA DE ENFRENTAMIENTO propia (ver `RetoModoBots`),
//   · y, opcionalmente, un TROFEO que se otorga al ganarlo (`TrofeoId`).
//
// A diferencia del MODO HISTORIA (WarZeroHistoria.cs), aquí NO se siembran
// cartas ni se usa un bot sintético: los rivales son los mismos runners de bot
// que rellenan salas públicas (WarZeroBot.cs), con su perfil de dificultad y
// estilo. Lo único que cambia es a QUIÉN miran, y de eso se encarga RetoFoco.cs.
//
// Para añadir un reto nuevo basta con registrarlo aquí y darle un hueco en la
// lista de la pantalla de retos del cliente (retos_screen.dart).
// ─────────────────────────────────────────────────────────────────────────────

/// Cómo se comportan los bots entre ellos dentro del reto.
public enum RetoModoBots
{
    /// Comportamiento normal: cada bot juega contra todos (guerra de 4).
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

    /// Id del documento del mapa en la colección `Mapas`.
    public required string MapaId { get; init; }

    /// Nombre del mapa (campo `nombre` del documento). Solo se usa como
    /// FALLBACK: si no existe `Mapas/{MapaId}`, se busca el mapa cuyo `nombre`
    /// coincida con este texto. Así el reto no se rompe si el documento del
    /// mapa se creó con un id autogenerado en vez de con su nombre.
    public string MapaNombre { get; init; } = "";

    /// Ejército (1..4) con el que juega el humano.
    public required int EjercitoJugador { get; init; }

    /// Uids de los bots rivales, en la colección `Bots`.
    public required List<string> Bots { get; init; }

    /// Regla de enfrentamiento entre los bots.
    public RetoModoBots ModoBots { get; init; } = RetoModoBots.TodosContraElJugador;

    /// Modo de turno de la partida ("rapida" | "turno12h" | "diario").
    public string ModoTurno { get; init; } = "rapida";

    /// Id del documento de la colección `Trofeos` que se otorga al GANAR el
    /// reto. Vacío = el reto no da trofeo.
    ///
    /// El trofeo debe existir, estar activo y —importante— NO tener `Metrica`:
    /// `WarZeroTrofeos.Cumple` devuelve false con métrica vacía, así que la
    /// evaluación automática por acumulación nunca lo regalará. La única forma
    /// de conseguirlo es ganar este reto, vía
    /// `WarZeroService.OtorgarTrofeoRetoSiProcedeAsync`.
    ///
    /// Es idempotente: repetir el reto no vuelve a otorgarlo (ni repite el
    /// pop-up).
    public string TrofeoId { get; init; } = "";

    /// Nº de jugadores de la partida (humano + bots).
    public int MaxJugadores => 1 + Bots.Count;
}

public static class RetoCatalogo
{
    public const string ResistenciaDemoniaca = "resistencia_demoniaca";

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
            MapaId = "Clasica4J",
            MapaNombre = "Clasica4J",
            EjercitoJugador = 3,                 // Demonios
            Bots = new List<string> { "bot_20", "bot_21", "bot_22" },
            ModoBots = RetoModoBots.TodosContraElJugador,
            ModoTurno = "rapida",
            // Crea este documento en la colección `Trofeos` (SIN métrica) y pon
            // aquí su id. Déjalo en "" mientras no exista: el reto funciona
            // igual, simplemente no otorga nada.
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