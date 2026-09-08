using System.Linq;

// ─────────────────────────────────────────────────────────────────────────────
// HistoriaGuionOleadas.cs
//
// Guiones de IA "por oleadas" para batallas de historia CONCRETAS. Es un
// catálogo de DATOS puro (mismo espíritu que HistoriaCatalogo.cs): no toca
// Firestore ni conoce el tablero, solo describe QUÉ cartas del bot deben
// REAPARECER (como refuerzos nuevos, clonados del catálogo) y EN QUÉ turno y
// coordenada de salida.
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
//   2. Escribe un `GuionOleadas` con su lista de `Oleada` (turno + grupos).
//   3. Añádelo a `Todas` con la Id de la `HistoriaDef` correspondiente.
// Una historia sin entrada aquí sigue funcionando exactamente igual que antes.
// ─────────────────────────────────────────────────────────────────────────────

/// Un grupo de refuerzos dentro de una oleada: `Cantidad` copias NUEVAS de
/// `CartaId` (clonadas del catálogo `Cartas`) aparecen en `Coordenada`.
public sealed record GrupoOleada(string CartaId, int Cantidad, string Coordenada);

/// Una oleada de refuerzo: en el turno `TurnoInicio` aparecen todos sus
/// `Grupos` a la vez. No se repite sola; si debe reaparecer en otro turno,
/// se declara como otra `Oleada` distinta con esos mismos grupos.
public sealed record Oleada(int TurnoInicio, System.Collections.Generic.IReadOnlyList<GrupoOleada> Grupos);

/// Guion completo de una batalla: la secuencia de oleadas a lo largo de la
/// partida, ordenadas por turno.
public sealed class GuionOleadas
{
    public System.Collections.Generic.IReadOnlyList<Oleada> Oleadas { get; }

    public GuionOleadas(System.Collections.Generic.IReadOnlyList<Oleada> oleadas) => Oleadas = oleadas;

    /// La oleada que debe REAPARECER justo en `turno`, o null si `turno` no es
    /// un turno de reposición (las unidades ya desplegadas de oleadas
    /// anteriores simplemente siguen su avance genérico ese turno).
    public Oleada? OleadaEnTurno(int turno) => Oleadas.FirstOrDefault(o => o.TurnoInicio == turno);
}

/// Catálogo estático de guiones de oleadas, indexado por `HistoriaDef.Id`.
public static class HistoriaGuiones
{
    // ── IDS DE CARTA (bando BOT = Humanos en "demonios_1") ───────────────────
    // Mismos ids que HistoriaCatalogo.HumA/HumB/HumC — se repiten aquí como
    // constantes propias para que este fichero no dependa de la visibilidad
    // interna de HistoriaCatalogo.
    private const string DienteInviernoHumA = "xPcw2Adpdfdb8TMp4Uiy"; // ×8 en el reparto inicial
    private const string DienteInviernoHumB = "8KZtDtblcypCtFfDSF08"; // ×3 en el reparto inicial
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
    //   cantidades que la oleada 1); ya no hay grupo en A6/HumC.
    //
    //   Turno 4 · sin oleada nueva ("mantiene esas cartas"): lo desplegado en
    //   el turno 3 sigue avanzando, sin refuerzos adicionales.
    //
    //   Turno 5 · Oleada 3 (2 grupos) — última reposición, solo por el centro:
    //   F1 y F6 vuelven a sacar sus copias de HumB.
    //
    //   Turno 6 · sin oleada nueva: último turno de supervivencia, lo que haya
    //   en el tablero sigue avanzando sin más refuerzos.
    //
    // NOTA: la carta HumB tiene 3 copias en total y no se divide en dos mitades
    // exactas; se reparte 2 en F1 (el cuartel) y 1 en F6. Si el diseño quiere
    // la proporción inversa, basta con intercambiar las Cantidades abajo.
    private static readonly GuionOleadas DienteDeInvierno1 = new(
        new System.Collections.Generic.List<Oleada>
        {
            new Oleada(TurnoInicio: 1, Grupos: new System.Collections.Generic.List<GrupoOleada>
            {
                new GrupoOleada(DienteInviernoHumA, 8, "A2"),
                new GrupoOleada(DienteInviernoHumB, 2, "F1"),
                new GrupoOleada(DienteInviernoHumB, 1, "F6"),
                new GrupoOleada(DienteInviernoHumC, 2, "A6"),
            }),
            new Oleada(TurnoInicio: 3, Grupos: new System.Collections.Generic.List<GrupoOleada>
            {
                new GrupoOleada(DienteInviernoHumA, 8, "A2"),
                new GrupoOleada(DienteInviernoHumB, 2, "F1"),
                new GrupoOleada(DienteInviernoHumB, 1, "F6"),
            }),
            new Oleada(TurnoInicio: 5, Grupos: new System.Collections.Generic.List<GrupoOleada>
            {
                new GrupoOleada(DienteInviernoHumB, 2, "F1"),
                new GrupoOleada(DienteInviernoHumB, 1, "F6"),
            }),
        });

    /// Todos los guiones registrados, indexados por `HistoriaDef.Id`. Una
    /// historia sin entrada aquí usa el avance frontal genérico de siempre.
    public static readonly System.Collections.Generic.IReadOnlyDictionary<string, GuionOleadas> Todas =
        new System.Collections.Generic.Dictionary<string, GuionOleadas>
        {
            ["demonios_1"] = DienteDeInvierno1,
        };

    /// Guion de `historiaId`, o null si esa historia no tiene oleadas
    /// scriptadas (usa el avance frontal genérico).
    public static GuionOleadas? Get(string historiaId) =>
        !string.IsNullOrEmpty(historiaId) && Todas.TryGetValue(historiaId, out var g) ? g : null;
}