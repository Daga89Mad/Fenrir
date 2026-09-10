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

    // ── ÉLITE: HumB EVOLUCIONADA desde el turno 1 ────────────────────────────
    // Copias de DienteInviernoHumB que salen YA EVOLUCIONADAS en cada oleada de
    // refuerzo (turnos 1, 3 y 5) de las dos partes de Diente de Invierno. Van en
    // un grupo propio, aparte de los refuerzos normales de HumB, así que subir o
    // bajar la dificultad de esa presión es tocar solo estos dos números.
    //   Parte 1 → 1 carta por oleada.
    //   Parte 2 → 3 cartas por oleada.
    private const int EliteHumBParte1 = 1;
    private const int EliteHumBParte2 = 3;

    // ── "demonios_1" · El asedio de Diente de Invierno ───────────────────────
    // 3 oleadas de refuerzo hasta el turno de supervivencia (6):
    //
    //   Turnos 1-2 · Oleada 1 (5 grupos) — asalto inicial en cuatro frentes:
    //     · A2: las 8 copias de HumA.
    //     · F1 (cuartel del bot): 2 de las 3 copias de HumB.
    //     · F6: la copia restante de HumB.
    //     · A6: las 2 copias de HumC ("el resto" de la plantilla).
    //     · F1: la HumB de ÉLITE, ya evolucionada (ver EliteHumBParte1).
    //   El turno 2 NO clona nada nuevo: es la misma oleada, que ya avanza sola
    //   con la lógica genérica de abajo.
    //
    //   Turno 3 · Oleada 2 (4 grupos) — se repone TODO lo de A2/F1/F6 (mismas
    //   cantidades que la oleada 1); ya no hay grupo en A6/HumC. De las 8 copias
    //   de HumA que salen por A2, 3 nacen YA EVOLUCIONADAS (no gastan turno
    //   evolucionando: pueden moverse desde su primer turno). Y vuelve a salir
    //   la HumB de élite.
    //
    //   Turno 4 · sin oleada nueva ("mantiene esas cartas"): lo desplegado en
    //   el turno 3 sigue avanzando, sin refuerzos adicionales.
    //
    //   Turno 5 · Oleada 3 (3 grupos) — última reposición, solo por el centro:
    //   F1 y F6 sacan 2 copias de HumB cada uno (4 cartas en total) y TODAS
    //   nacen ya evolucionadas, más la HumB de élite por F1.
    //
    //   Turno 6 · sin oleada nueva: último turno de supervivencia, lo que haya
    //   en el tablero sigue avanzando sin más refuerzos.
    //
    // NOTA: en la oleada 1 la carta HumB tiene 3 copias en total y no se divide
    // en dos mitades exactas; se reparte 2 en F1 (el cuartel) y 1 en F6. Si el
    // diseño quiere la proporción inversa, basta con intercambiar las Cantidades
    // abajo. Las oleadas de refuerzo NO están limitadas por el reparto inicial:
    // son clones frescos del catálogo, así que el turno 5 puede sacar 2+2.
    //
    // ÉLITE HumB (turnos 1, 3 y 5): grupo propio y separado, para que haya una
    // HumB EVOLUCIONADA presionando DESDE EL PRIMER TURNO y no solo a partir
    // del 3. Se saca aparte —en vez de subir el `CantidadEvolucionada` de los
    // grupos de F1/F6 ya existentes— para poder cambiar su cantidad, su turno o
    // su frente de salida sin tocar el equilibrio del resto de la oleada. Sale
    // por F1 (el cuartel del bot); para moverla de frente basta con poner "F6".
    private static readonly GuionOleadas DienteDeInvierno1 = new(
        new System.Collections.Generic.List<Oleada>
        {
            new Oleada(TurnoInicio: 1, Grupos: new System.Collections.Generic.List<GrupoOleada>
            {
                new GrupoOleada(DienteInviernoHumA, 8, "A2"),
                new GrupoOleada(DienteInviernoHumB, 2, "F1"),
                new GrupoOleada(DienteInviernoHumB, 1, "F6"),
                new GrupoOleada(DienteInviernoHumC, 2, "A6"),
                // Élite: HumB evolucionada ya en el turno 1.
                new GrupoOleada(DienteInviernoHumB, EliteHumBParte1, "F1",
                    CantidadEvolucionada: EliteHumBParte1),
            }),
            // Turno 3: de las 8 copias de HumA en A2, 3 salen ya evolucionadas.
            new Oleada(TurnoInicio: 3, Grupos: new System.Collections.Generic.List<GrupoOleada>
            {
                new GrupoOleada(DienteInviernoHumA, 8, "A2", CantidadEvolucionada: 3),
                new GrupoOleada(DienteInviernoHumB, 2, "F1"),
                new GrupoOleada(DienteInviernoHumB, 1, "F6"),
                new GrupoOleada(DienteInviernoHumB, EliteHumBParte1, "F1",
                    CantidadEvolucionada: EliteHumBParte1),
            }),
            // Turno 5: 3 grupos · 5 cartas, TODAS evolucionadas de salida.
            new Oleada(TurnoInicio: 5, Grupos: new System.Collections.Generic.List<GrupoOleada>
            {
                new GrupoOleada(DienteInviernoHumB, 2, "F1", CantidadEvolucionada: 2),
                new GrupoOleada(DienteInviernoHumB, 2, "F6", CantidadEvolucionada: 2),
                new GrupoOleada(DienteInviernoHumB, EliteHumBParte1, "F1",
                    CantidadEvolucionada: EliteHumBParte1),
            }),
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
    //   4. La ÉLITE de HumB evolucionadas de los turnos 1, 3 y 5 es de 3 cartas
    //      (en la parte 1 es de 1): cada oleada trae el triple de presión ya
    //      evolucionada por el cuartel.
    //
    // El resto (F1 con 2 HumB, F6 con 1 HumB, turno 5 con 2 grupos de 2 HumB
    // evolucionadas, turnos 2/4/6 sin refuerzos) es idéntico a la parte 1.
    private static readonly GuionOleadas DienteDeInvierno2 = new(
        new System.Collections.Generic.List<Oleada>
        {
            new Oleada(TurnoInicio: 1, Grupos: new System.Collections.Generic.List<GrupoOleada>
            {
                new GrupoOleada(DienteInviernoHumA, 8, "A2", CantidadEvolucionada: 3),
                new GrupoOleada(DienteInviernoHumB, 2, "F1"),
                new GrupoOleada(DienteInviernoHumB, 1, "F6"),
                new GrupoOleada(DienteInviernoHumC, 2, "A6"),
                // Grupo nuevo de la parte 2 (turnos 1, 3 y 5).
                new GrupoOleada(DienteInviernoHumA, 3, "A6"),
                new GrupoOleada(DienteInviernoHumB, 2, "A6"),
                // Élite: 3 HumB evolucionadas ya en el turno 1.
                new GrupoOleada(DienteInviernoHumB, EliteHumBParte2, "F1",
                    CantidadEvolucionada: EliteHumBParte2),
            }),
            new Oleada(TurnoInicio: 3, Grupos: new System.Collections.Generic.List<GrupoOleada>
            {
                new GrupoOleada(DienteInviernoHumA, 8, "A2", CantidadEvolucionada: 3),
                new GrupoOleada(DienteInviernoHumB, 2, "F1"),
                new GrupoOleada(DienteInviernoHumB, 1, "F6"),
                new GrupoOleada(DienteInviernoHumA, 3, "A6"),
                new GrupoOleada(DienteInviernoHumB, 2, "A6"),
                new GrupoOleada(DienteInviernoHumB, EliteHumBParte2, "F1",
                    CantidadEvolucionada: EliteHumBParte2),
            }),
            new Oleada(TurnoInicio: 5, Grupos: new System.Collections.Generic.List<GrupoOleada>
            {
                new GrupoOleada(DienteInviernoHumB, 2, "F1", CantidadEvolucionada: 2),
                new GrupoOleada(DienteInviernoHumB, 2, "F6", CantidadEvolucionada: 2),
                new GrupoOleada(DienteInviernoHumA, 3, "A6"),
                new GrupoOleada(DienteInviernoHumB, 2, "A6"),
                new GrupoOleada(DienteInviernoHumB, EliteHumBParte2, "F1",
                    CantidadEvolucionada: EliteHumBParte2),
            }),
        });

    /// Todos los guiones registrados, indexados por `HistoriaDef.Id`. Una
    /// historia sin entrada aquí usa el avance frontal genérico de siempre.
    public static readonly System.Collections.Generic.IReadOnlyDictionary<string, GuionOleadas> Todas =
        new System.Collections.Generic.Dictionary<string, GuionOleadas>
        {
            ["demonios_1"] = DienteDeInvierno1,
            ["demonios_2"] = DienteDeInvierno2,
        };

    /// Guion de `historiaId`, o null si esa historia no tiene oleadas
    /// scriptadas (usa el avance frontal genérico).
    public static GuionOleadas? Get(string historiaId) =>
        !string.IsNullOrEmpty(historiaId) && Todas.TryGetValue(historiaId, out var g) ? g : null;
}