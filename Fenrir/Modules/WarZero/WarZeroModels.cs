using System.Text.Json;

// ─────────────────────────────────────────────────────────────────────────────
// WarZeroModels.cs
// ─────────────────────────────────────────────────────────────────────────────

public record GameStatus(string Server, int Players);

/// Cuerpo de POST /warzero/turno/cerrar.
/// `Celdas` y `Acciones` se reciben como JSON crudo y se convierten a CLR en el
/// servicio (mismo formato que MovimientoTurno.toMap / AccionPendiente.toMap).
public class CerrarTurnoRequest
{
    public string LobbyId { get; set; } = "";
    public string Uid { get; set; } = "";
    public int Turno { get; set; }

    /// Map coord -> lista de cartas (Map<String, List<Map<String,dynamic>>> en Dart).
    public JsonElement Celdas { get; set; }

    /// Lista de AccionPendiente.toMap().
    public JsonElement Acciones { get; set; }
}

/// Respuesta de POST /warzero/turno/cerrar.
public class CerrarTurnoResponse
{
    /// True si esta llamada cerró al último jugador y resolvió el turno.
    public bool Resuelto { get; set; }

    /// Turno vigente tras la operación (incrementado si Resuelto = true).
    public int TurnoActual { get; set; }

    /// Nº de jugadores que han cerrado el turno actual.
    public int CerradoPor { get; set; }

    /// Nº de jugadores activos (no eliminados).
    public int JugadoresActivos { get; set; }

    /// Jugadores que faltan por cerrar.
    public int Faltan { get; set; }

    public bool Finalizada { get; set; }
    public string? GanadorUid { get; set; }

    /// Conquistas de cuartel ocurridas en esta resolución (logs).
    public List<Dictionary<string, object?>> Conquistas { get; set; } = new();

    /// Energies ganadas por jugador en esta resolución (combate + farmeo).
    public Dictionary<string, int> EnergiesPorJugador { get; set; } = new();

    public string Mensaje { get; set; } = "";

    /// Estado completo de la partida tras la operación (mismo shape que el doc
    /// de Firestore: tablero, efectosCelda, statsPartida, obeliscos, cerradoPor,
    /// historialCombates, ultimoCombateLog, ultimosMovimientos, etc.), ya
    /// serializado JSON-safe. Permite al cliente avanzar SIN leer Firestore.
    public Dictionary<string, object?>? Estado { get; set; }
}

/// Respuesta de GET /warzero/estado: estado completo de la partida por HTTP,
/// para que un jugador que espera pueda sondear sin depender de Firestore.
public class EstadoResponse
{
    public bool Existe { get; set; }
    public int TurnoActual { get; set; }
    public Dictionary<string, object?>? Estado { get; set; }
}

/// Cuerpo de POST /warzero/entrar.
public class EntrarRequest
{
    public string LobbyId { get; set; } = "";
    public string Uid { get; set; } = "";
}

/// Respuesta de POST /warzero/entrar: inicializa (si hace falta) las energías
/// de inicio y el obelisco del jugador, y devuelve el estado completo.
public class EntrarResponse
{
    public bool Existe { get; set; }
    public int TurnoActual { get; set; }

    /// Energías iniciales asignadas en esta entrada (null si ya las tenía).
    public int? EnergiasAsignadas { get; set; }

    /// Obelisco asignado en esta entrada (null si ya tenía o no había libre).
    public string? ObeliscoAsignado { get; set; }

    public Dictionary<string, object?>? Estado { get; set; }
}