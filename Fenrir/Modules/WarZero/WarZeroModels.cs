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

/// Cuerpo de POST /warzero/stats. Actualiza los stats de partida de un jugador
/// (energías, mano/mazo, compras) sin que el cliente toque Firestore. Todos los
/// campos salvo lobbyId/uid son opcionales; solo se aplican los presentes.
public class StatsRequest
{
    public string LobbyId { get; set; } = "";
    public string Uid { get; set; } = "";

    /// Incremento de energías (negativo = gasto). Se aplica con FieldValue.Increment.
    public int? EnergiesDelta { get; set; }

    /// Id de carta especial recién comprada (arrayUnion en especialesCompradas).
    public string? EspecialComprada { get; set; }

    /// Incremento del contador de cartas robadas en el cuartel (para el precio
    /// creciente del robo). Se aplica con FieldValue.Increment sobre
    /// statsPartida.{uid}.robosComprados.
    public int? RobosDelta { get; set; }

    /// Mano actual (lista de ids) a persistir, si se envía.
    public List<string>? Mano { get; set; }

    /// Mazo restante (lista de ids) a persistir, si se envía.
    public List<string>? MazoRestante { get; set; }
}

/// Cuerpo de POST /warzero/turno/deshacer. Revierte los gastos NO consolidados
/// del turno en curso (bug QAS #2): devuelve la energía revertible gastada este
/// turno (despliegues/compras/evoluciones), desmarca las especiales compradas
/// este turno y borra cualquier borrador. NO cierra el turno.
public class DeshacerTurnoRequest
{
    public string LobbyId { get; set; } = "";
    public string Uid { get; set; } = "";
    public int Turno { get; set; }

    /// Energía a DEVOLVER (positiva). Se aplica con FieldValue.Increment.
    public int EnergiesDelta { get; set; }

    /// Ids de especiales compradas este turno a desmarcar (arrayRemove).
    public List<string> EspecialesQuitar { get; set; } = new();
}

/// Cuerpo de POST /warzero/historia/desbloquear. Marca una historia como
/// conseguida por el jugador (arrayUnion en historiasDesbloqueadas).
public class DesbloquearHistoriaRequest
{
    public string Uid { get; set; } = "";
    public string HistoriaId { get; set; } = "";
}

/// Cuerpo de POST /warzero/skin/seleccionar. Fija (o limpia, si SkinId es null)
/// la skin elegida del jugador para una carta de su colección.
public class SeleccionarSkinRequest
{
    public string Uid { get; set; } = "";
    public string CartaId { get; set; } = "";

    /// Id de la skin a aplicar. null/vacío → volver al diseño original.
    public string? SkinId { get; set; }
}

/// Cuerpo de POST /warzero/carta/repartir-todos. [Solo editores] Reparte la
/// carta indicada a la colección de TODOS los usuarios que aún no la tengan.
public class RepartirCartaRequest
{
    public string CartaId { get; set; } = "";
}

/// Cuerpo de POST /warzero/skin/repartir-todos. [Solo editores] Desbloquea la
/// skin indicada para TODOS los usuarios en la carta asociada.
public class RepartirSkinRequest
{
    public string SkinId { get; set; } = "";
}

/// Cuerpo de POST /warzero/sobre/abrir. Abre un sobre del ejército indicado:
/// otorga una carta al azar (ponderada por Probabilidad), incrementa su
/// contador y da Zero del ejército.
public class AbrirSobreRequest
{
    public string Uid { get; set; } = "";
    public int EjercitoId { get; set; }
}

/// Cuerpo de POST /warzero/skin/comprar. Compra una skin gastando Zero del
/// ejército de su carta (requiere haber obtenido la carta ≥ numeroCompra veces).
public class ComprarSkinRequest
{
    public string Uid { get; set; } = "";
    public string SkinId { get; set; } = "";
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

/// Cuerpo de POST /warzero/fcm/registrar. Registra el token FCM del dispositivo
/// del jugador para poder enviarle notificaciones push (iOS/Android) cuando se
/// resuelve un turno. `Platform` es informativo ("ios"/"android").
public class RegistrarFcmTokenRequest
{
    public string Uid { get; set; } = "";
    public string Token { get; set; } = "";
    public string? Platform { get; set; }
}

/// Cuerpo de POST /warzero/fcm/eliminar. Quita un token (p. ej. al cerrar sesión).
public class EliminarFcmTokenRequest
{
    public string Uid { get; set; } = "";
    public string Token { get; set; } = "";
}

// ─────────────────────────────────────────────────────────────────────────────
// ALIANZAS (partidas de 4+ jugadores)
// ─────────────────────────────────────────────────────────────────────────────
//
// Estado persistido en el doc de partida bajo la clave `alianzas`:
//
//   alianzas: {
//     propuestas: [
//       { deUid, paraUid, turnos, turnoPropuesta }
//     ],
//     activas: [
//       // Par simétrico. `turnosRestantes` baja 1 en cada resolución de turno;
//       // al llegar a 0 la alianza se elimina y vuelven a ser enemigos.
//       { uidA, uidB, turnosRestantes }
//     ],
//     traiciones: [
//       // Traición PENDIENTE marcada por `traidorUid`. En la próxima resolución
//       // el par deja de estar aliado (el traidor puede atacar en esa misma
//       // resolución) y la víctima recibe el aviso DESPUÉS de resolver.
//       { traidorUid, victimaUid }
//     ]
//   }
//
// Reglas de negocio (validadas en el servicio):
//   • Solo se permite en partidas de 4+ jugadores.
//   • Un jugador solo puede tener UNA alianza activa a la vez (y una propuesta
//     saliente a la vez).
//   • Al aceptar, ambos quedan aliados con `turnos` = turnos propuestos.
//   • Mientras dure la alianza, el PC ganado por cada aliado se divide /2 (floor)
//     en cada resolución, participen ambos o no.
//   • En combate, las cartas de aliados suman fuerza y comparten casilla (no se
//     combaten entre sí) EXCEPTO sobre el cuartel del aliado, que SÍ es
//     conquistable.

/// Cuerpo de POST /warzero/alianza/proponer.
public class ProponerAlianzaRequest
{
    public string LobbyId { get; set; } = "";

    /// Jugador que propone la alianza.
    public string DeUid { get; set; } = "";

    /// Jugador al que se le propone.
    public string ParaUid { get; set; } = "";

    /// Duración de la alianza en turnos (>= 1).
    public int Turnos { get; set; } = 1;
}

/// Cuerpo de POST /warzero/alianza/responder. La responde el jugador destinatario
/// de una propuesta pendiente.
public class ResponderAlianzaRequest
{
    public string LobbyId { get; set; } = "";

    /// Jugador que responde (destinatario de la propuesta).
    public string Uid { get; set; } = "";

    /// Jugador que propuso la alianza.
    public string ProponenteUid { get; set; } = "";

    /// true = aceptar, false = rechazar.
    public bool Aceptar { get; set; }
}

/// Cuerpo de POST /warzero/alianza/traicionar. Marca una traición pendiente:
/// el jugador `Uid` dejará de ser aliado en la próxima resolución del turno.
public class TraicionarAlianzaRequest
{
    public string LobbyId { get; set; } = "";
    public string Uid { get; set; } = "";
}

/// Respuesta genérica de las operaciones de alianza. Devuelve el estado completo
/// de la partida para que el cliente refresque sin leer Firestore.
public class AlianzaResponse
{
    public bool Ok { get; set; }
    public string Mensaje { get; set; } = "";
    public Dictionary<string, object?>? Estado { get; set; }
}