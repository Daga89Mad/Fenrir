// ─────────────────────────────────────────────────────────────────────────────
// DuelosReglas.cs
//
// MODO DUELOS — tipos, ajustes y catálogo. Lógica pura (sin Firestore): la
// usan el motor (DuelosMotor.cs), el bot (DuelosBot.cs) y la arena offline
// (ArenaDuelos.cs). Documento de diseño: proyecto warZero,
// `claude/duelos_diseno.md`.
//
// Resumen de reglas:
//   • Cada jugador: ejército, 2 generales de ese ejército (uno es el REY, en
//     secreto) y una guarnición FIJA de 4 soldados (2 básicas, 1 media, 1 alta).
//   • Las cartas solo aportan nombre e imagen: ataque y movimiento son fijos
//     por papel (básica 3, media 7, alta 12, general 17; movimiento 2). Solo
//     existe el ataque. Cada general tiene 2 vidas.
//   • Tablero 7×7 con pilares. Movimiento simultáneo, combate en la casilla
//     final: gana el grupo con más ataque; el perdedor pierde sus soldados y
//     cada general pierde 1 vida y sale empujado. Empate: pierden los dos.
//   • Tope de 3 cartas propias por casilla, 2 disparos lejanos por partida,
//     límite de 35 turnos. El tablero se hunde: 1 casilla por turno desde el
//     3, 2 desde el 10 y 3 desde el 20.
//   • Cada general tiene una habilidad fija (recarga 4) y cada ejército una
//     habilidad de un solo uso.
//   • Si muere tu rey, pierdes. Si mueren los dos reyes a la vez, tablas.
// ─────────────────────────────────────────────────────────────────────────────

// ═════════════════════════════════════════════════════════════════════════════
// AJUSTES — EDITA AQUÍ
// ═════════════════════════════════════════════════════════════════════════════
public static class DuelosAjustes
{
    // ── Tablero ──────────────────────────────────────────────────────────────
    public const int Filas = 7;
    public const int Columnas = 7;
    public const int Celdas = Filas * Columnas;
    /// Casillas bloqueadas desde el inicio.
    public static readonly string[] Pilares = { "C3", "C5", "E3", "E5" };
    /// Filas traseras donde despliega cada jugador (el 0 desde la A, el 1
    /// desde la G).
    public const int FilasDespliegue = 2;

    // ── Cartas ───────────────────────────────────────────────────────────────
    public const int AtaqueBasica = 3;
    public const int AtaqueMedia = 7;
    public const int AtaqueAlta = 12;
    public const int AtaqueGeneral = 17;
    public const int Movimiento = 2;
    public const int VidasGeneral = 2;
    public const int TopeCartasCasilla = 3;

    // ── Recursos comunes ─────────────────────────────────────────────────────
    public const int DisparosLejanos = 2;
    public const int DisparoLejanoPrimerTurno = 3;

    // ── Tablero que se hunde ─────────────────────────────────────────────────
    /// Primer turno en el que se HUNDE una casilla (se anuncia al empezarlo).
    public const int HundimientoPrimerTurno = 3;
    /// Desde este turno se hunden 2 casillas por turno (0 = nunca).
    public const int HundimientoDobleDesdeTurno = 10;
    /// Desde este turno se hunden 3 casillas por turno (0 = nunca).
    public const int HundimientoTripleDesdeTurno = 20;

    /// Casillas que se hunden en [turno] (0 antes de HundimientoPrimerTurno).
    public static int CasillasHundidasEnTurno(int turno) =>
        turno < HundimientoPrimerTurno ? 0
        : HundimientoTripleDesdeTurno > 0 && turno >= HundimientoTripleDesdeTurno ? 3
        : HundimientoDobleDesdeTurno > 0 && turno >= HundimientoDobleDesdeTurno ? 2
        : 1;
    /// Peso extra por anillo hacia el borde (0 = uniforme). Con 3, una casilla
    /// del borde pesa 10 y la central 1.
    public const double HundimientoPesoBorde = 3.0;

    // ── Fin ──────────────────────────────────────────────────────────────────
    public const int TurnoLimite = 35;

    // ── Puntos de combate (PC) ───────────────────────────────────────────────
    // Como en las partidas normales: 3 PC por carta enemiga destruida, en
    // combate y también con disparos y habilidades (no por hundimiento).
    // Derrotar a un general enemigo da 50 (los dos, 100).
    public const int PcSoldado = 3;
    public const int PcGeneral = 50;

    // ── Habilidades de general ───────────────────────────────────────────────
    public const int HabilidadPrimerTurno = 2;
    /// Turnos de recarga: usada en el turno T, vuelve a estar lista en T+5.
    public const int RecargaHabilidad = 4;
    /// Alcance (pasos ortogonales) de parálisis, veneno y la 1.ª casilla del muro.
    public const int AlcanceMedio = 3;
    public const int TeletransporteDistancia = 3;
    public const int MasMovimientoMagnitud = 1;
    public const int MasMovimientoTurnos = 2;
    public const int EscudoTurnos = 2;
    public const int ClonTurnos = 3;
    public const int VenenoMagnitud = 3;
    public const int VenenoTurnos = 3;
    public const int MasAtaqueMagnitud = 3;
    public const int MasAtaqueTurnos = 2;
    public const int SupervivenciaTurnos = 3;
    public const int MuroCeldas = 3;
    public const int MuroTurnos = 2;

    // ── Habilidades de ejército ──────────────────────────────────────────────
    public const int HabilidadEjercitoPrimerTurno = 2;
    public const int MovilizacionMagnitud = 2;
    public const int MovilizacionTurnos = 2;
    /// Vidas que recupera el general con Reparación.
    public const int ReparacionVidas = 1;
    /// Si es true, el general reparado no puede moverse ese turno.
    public static readonly bool ReparacionInmoviliza = true;
    public const int FuriaInfernalMagnitud = 4;
    public const int FortalezaTurnos = 2;
}

// ═════════════════════════════════════════════════════════════════════════════
// TIPOS
// ═════════════════════════════════════════════════════════════════════════════
public enum DuelosEjercito { Humanos = 1, Bionicos = 2, Demonios = 3, Nefilim = 4 }

public enum DuelosPapel { Basica, Media, Alta, General }

public enum DuelosHabilidad
{
    Ninguna,
    DisparoCercano,
    Teletransporte,
    MasMovimiento,
    Escudo,
    Paralisis,
    Clon,
    Veneno,
    MasAtaque,
    Confusion,
    Supervivencia,
    Muro,
}

public enum DuelosHabilidadEjercito { Movilizacion, Reparacion, FuriaInfernal, Fortaleza }

/// Carta del catálogo usada en Duelos (id de `Cartas`, nombre, papel y, si es
/// general, su habilidad fija).
public sealed record DuelosDefCarta(
    string CartaId,
    string Nombre,
    DuelosPapel Papel,
    DuelosHabilidad Habilidad = DuelosHabilidad.Ninguna);

public sealed record DuelosDefEjercito(
    DuelosEjercito Ejercito,
    string Nombre,
    DuelosHabilidadEjercito HabilidadEjercito,
    /// Guarnición fija, en orden: básica, básica, media, alta.
    IReadOnlyList<DuelosDefCarta> Guarnicion,
    IReadOnlyList<DuelosDefCarta> Generales);

// ═════════════════════════════════════════════════════════════════════════════
// CATÁLOGO
// ═════════════════════════════════════════════════════════════════════════════
public static class DuelosCatalogo
{
    private static DuelosDefCarta B(string id, string n) => new(id, n, DuelosPapel.Basica);
    private static DuelosDefCarta Me(string id, string n) => new(id, n, DuelosPapel.Media);
    private static DuelosDefCarta Al(string id, string n) => new(id, n, DuelosPapel.Alta);
    private static DuelosDefCarta G(string id, string n, DuelosHabilidad h) => new(id, n, DuelosPapel.General, h);

    public static readonly IReadOnlyDictionary<DuelosEjercito, DuelosDefEjercito> Ejercitos =
        new Dictionary<DuelosEjercito, DuelosDefEjercito>
        {
            [DuelosEjercito.Humanos] = new(
                DuelosEjercito.Humanos, "Humanos", DuelosHabilidadEjercito.Movilizacion,
                new[]
                {
                    B("xPcw2Adpdfdb8TMp4Uiy", "Soldado celeste"),
                    B("xPcw2Adpdfdb8TMp4Uiy", "Soldado celeste"),
                    Me("8KZtDtblcypCtFfDSF08", "Tanqueta ónix"),
                    Al("kKJl1PyTsfIytyfOkfiS", "Capitán celeste"),
                },
                new[]
                {
                    G("uOfCRljbVXoRNWXwv6gL", "General Alvaroth", DuelosHabilidad.Escudo),
                    G("wWGvqQWEjZRZZcyab6yU", "General Albariel", DuelosHabilidad.DisparoCercano),
                    G("BvoBCmLhU52aSGmB1nQE", "Reina Cristina", DuelosHabilidad.MasMovimiento),
                    G("kgM5Wh5wppusK4zTCBRV", "Noavelya", DuelosHabilidad.Teletransporte),
                }),

            [DuelosEjercito.Bionicos] = new(
                DuelosEjercito.Bionicos, "Biónicos", DuelosHabilidadEjercito.Reparacion,
                new[]
                {
                    B("xYGGl9sWoZfYQwnlaNwu", "Zorro fantasma"),
                    B("xYGGl9sWoZfYQwnlaNwu", "Zorro fantasma"),
                    Me("rpZC7bELsrHFfn4YsCbY", "Lobo titán"),
                    Al("JeI4EaJKLQrQlBiblNLp", "Capitán de Dunan"),
                },
                new[]
                {
                    G("LpAM8celKg1gvQsp5okR", "General Izanagi", DuelosHabilidad.Paralisis),
                    G("43RxCheHL2R4JP2IzIGi", "General Izanami", DuelosHabilidad.Escudo),
                    G("3wqhMnJ1DQjNlmV3EGL6", "Anjeru ryu", DuelosHabilidad.DisparoCercano),
                    G("S6QaKrY6pF6jSxY0tG7z", "Musashi", DuelosHabilidad.Clon),
                }),

            [DuelosEjercito.Demonios] = new(
                DuelosEjercito.Demonios, "Demonios", DuelosHabilidadEjercito.FuriaInfernal,
                new[]
                {
                    B("jFpE0EY9dJQdME2iM2y9", "Hijo de Gehena"),
                    B("jFpE0EY9dJQdME2iM2y9", "Hijo de Gehena"),
                    Me("stF3jOzQyQvVJguGblKj", "Ogro de Azazel"),
                    Al("uoxviGggKKUDlkEwHn6O", "Belial"),
                },
                new[]
                {
                    G("qrc2GYYSEhjLoISdxQch", "General Martynara", DuelosHabilidad.Supervivencia),
                    G("xi9ys2LqcHNVfFPNRsef", "Azazel", DuelosHabilidad.MasAtaque),
                    G("yEwMBTHhiVqgL1OIZsUI", "Los 8 colmillos de Gehena", DuelosHabilidad.DisparoCercano),
                    G("JFyZ4iYEA9BfsFSh3VLa", "Bestia escarlata", DuelosHabilidad.Veneno),
                    G("O3kpPY3hHwOWyXOPZgeM", "Lilith", DuelosHabilidad.Confusion),
                }),

            [DuelosEjercito.Nefilim] = new(
                DuelosEjercito.Nefilim, "Nefilim", DuelosHabilidadEjercito.Fortaleza,
                new[]
                {
                    B("qeygP8oT7WYKqmq1kvHI", "Soldado de la ira"),
                    B("qeygP8oT7WYKqmq1kvHI", "Soldado de la ira"),
                    Me("iju32O3HHqU27hmoVhhn", "Lanzarocas de la avaricia"),
                    Al("VmhD1AghdOBjmRfpSzk4", "Capitán Anac"),
                },
                new[]
                {
                    G("VKM1uUkqO9GqDI6tTr47", "General Alexander", DuelosHabilidad.DisparoCercano),
                    G("i1TxqqV691IQIR3xZpo2", "General Goliat", DuelosHabilidad.Paralisis),
                    G("hMuq8tSUrQZffQNx0wxz", "Oyah", DuelosHabilidad.Escudo),
                    G("tN02xfbpJePdykktgJq9", "Abu", DuelosHabilidad.Muro),
                }),
        };

    public static int Ataque(DuelosPapel p) => p switch
    {
        DuelosPapel.Basica => DuelosAjustes.AtaqueBasica,
        DuelosPapel.Media => DuelosAjustes.AtaqueMedia,
        DuelosPapel.Alta => DuelosAjustes.AtaqueAlta,
        _ => DuelosAjustes.AtaqueGeneral,
    };

    /// General por nombre (sin distinguir mayúsculas) dentro de un ejército.
    public static DuelosDefCarta? General(DuelosEjercito e, string nombre) =>
        Ejercitos[e].Generales.FirstOrDefault(g =>
            string.Equals(g.Nombre, nombre, StringComparison.OrdinalIgnoreCase));

    // ── Textos para la app (catálogo de GET /warzero/duelos/catalogo) ───────

    public static string NombreHabilidad(DuelosHabilidad h) => h switch
    {
        DuelosHabilidad.DisparoCercano => "Disparo cercano",
        DuelosHabilidad.Teletransporte => "Teletransporte",
        DuelosHabilidad.MasMovimiento => "Marcha forzada",
        DuelosHabilidad.Escudo => "Escudo",
        DuelosHabilidad.Paralisis => "Parálisis",
        DuelosHabilidad.Clon => "Clon",
        DuelosHabilidad.Veneno => "Veneno",
        DuelosHabilidad.MasAtaque => "Furia",
        DuelosHabilidad.Confusion => "Confusión",
        DuelosHabilidad.Supervivencia => "Supervivencia",
        DuelosHabilidad.Muro => "Muro",
        _ => "",
    };

    public static string DescripcionHabilidad(DuelosHabilidad h) => h switch
    {
        DuelosHabilidad.DisparoCercano =>
            "Dispara a una casilla contigua: mata a los soldados que haya y quita 1 vida a cada general.",
        DuelosHabilidad.Teletransporte =>
            $"Una carta propia salta hasta {DuelosAjustes.TeletransporteDistancia} casillas, ignorando obstáculos. Puede caer sobre enemigos.",
        DuelosHabilidad.MasMovimiento =>
            $"+{DuelosAjustes.MasMovimientoMagnitud} de movimiento a las cartas de su casilla durante {DuelosAjustes.MasMovimientoTurnos} turnos.",
        DuelosHabilidad.Escudo =>
            $"Escuda su casilla o una contigua durante {DuelosAjustes.EscudoTurnos} turnos: ningún enemigo puede entrar ni dispararle.",
        DuelosHabilidad.Paralisis =>
            $"Una casilla a {DuelosAjustes.AlcanceMedio} o menos: sus cartas no pueden moverse el turno siguiente.",
        DuelosHabilidad.Clon =>
            $"Crea en una casilla contigua un clon de un general propio durante {DuelosAjustes.ClonTurnos} turnos. Tu rival no sabe cuál es el verdadero.",
        DuelosHabilidad.Veneno =>
            $"Una casilla a {DuelosAjustes.AlcanceMedio} o menos: sus cartas pierden {DuelosAjustes.VenenoMagnitud} de ataque durante {DuelosAjustes.VenenoTurnos} turnos.",
        DuelosHabilidad.MasAtaque =>
            $"Tus cartas que terminen en su casilla ganan +{DuelosAjustes.MasAtaqueMagnitud} de ataque durante {DuelosAjustes.MasAtaqueTurnos} turnos.",
        DuelosHabilidad.Confusion =>
            "Las cartas enemigas de una casilla contigua se mueven al azar el turno siguiente.",
        DuelosHabilidad.Supervivencia =>
            $"Una carta propia de su casilla o una contigua, durante {DuelosAjustes.SupervivenciaTurnos} turnos: si pierde un combate huye en vez de morir.",
        DuelosHabilidad.Muro =>
            $"Levanta un muro de {DuelosAjustes.MuroCeldas} casillas vacías encadenadas durante {DuelosAjustes.MuroTurnos} turnos: nadie puede cruzarlo.",
        _ => "",
    };

    public static string NombreHabilidadEjercito(DuelosHabilidadEjercito h) => h switch
    {
        DuelosHabilidadEjercito.Movilizacion => "Movilización",
        DuelosHabilidadEjercito.Reparacion => "Reparación",
        DuelosHabilidadEjercito.FuriaInfernal => "Furia infernal",
        _ => "Fortaleza",
    };

    public static string DescripcionHabilidadEjercito(DuelosHabilidadEjercito h) => h switch
    {
        DuelosHabilidadEjercito.Movilizacion =>
            $"Una vez por partida: todas tus cartas tienen +{DuelosAjustes.MovilizacionMagnitud} de movimiento durante {DuelosAjustes.MovilizacionTurnos} turnos y cruzan pilares y casillas hundidas.",
        DuelosHabilidadEjercito.Reparacion =>
            "Una vez por partida: un general propio recupera 1 vida, pero no puede moverse ese turno.",
        DuelosHabilidadEjercito.FuriaInfernal =>
            $"Una vez por partida: este turno todas tus cartas suman +{DuelosAjustes.FuriaInfernalMagnitud} de ataque.",
        _ =>
            $"Una vez por partida, durante {DuelosAjustes.FortalezaTurnos} turnos: ningún enemigo puede entrar en tus casillas y tus cartas no reciben disparos ni habilidades.",
    };
}

// ═════════════════════════════════════════════════════════════════════════════
// TABLERO (índices 0..48: fila * 7 + columna; "A1" = 0, "G7" = 48)
// ═════════════════════════════════════════════════════════════════════════════
public static class DuelosTablero
{
    public static int Fila(int c) => c / DuelosAjustes.Columnas;
    public static int Col(int c) => c % DuelosAjustes.Columnas;
    public static int Idx(int fila, int col) => fila * DuelosAjustes.Columnas + col;

    public static bool Dentro(int fila, int col) =>
        fila >= 0 && col >= 0 && fila < DuelosAjustes.Filas && col < DuelosAjustes.Columnas;

    public static string Coord(int c) => $"{(char)('A' + Fila(c))}{Col(c) + 1}";

    public static int Parse(string coord)
    {
        coord = (coord ?? "").Trim().ToUpperInvariant();
        if (coord.Length < 2) return -1;
        int f = coord[0] - 'A';
        if (!int.TryParse(coord[1..], out var col)) return -1;
        return Dentro(f, col - 1) ? Idx(f, col - 1) : -1;
    }

    public static int Distancia(int a, int b) =>
        Math.Abs(Fila(a) - Fila(b)) + Math.Abs(Col(a) - Col(b));

    /// Anillo desde el centro (0 = centro, 3 = borde).
    public static int Anillo(int c) =>
        Math.Max(Math.Abs(Fila(c) - DuelosAjustes.Filas / 2), Math.Abs(Col(c) - DuelosAjustes.Columnas / 2));

    /// Vecinas ortogonales (precalculadas).
    public static readonly int[][] Vecinas = Enumerable.Range(0, DuelosAjustes.Celdas).Select(c =>
    {
        var l = new List<int>(4);
        foreach (var (df, dc) in new[] { (-1, 0), (1, 0), (0, -1), (0, 1) })
            if (Dentro(Fila(c) + df, Col(c) + dc)) l.Add(Idx(Fila(c) + df, Col(c) + dc));
        return l.ToArray();
    }).ToArray();

    /// Fila trasera de despliegue del jugador ([j] 0 → filas A-B, 1 → F-G).
    public static bool EnZonaDespliegue(int jugador, int c) =>
        jugador == 0
            ? Fila(c) < DuelosAjustes.FilasDespliegue
            : Fila(c) >= DuelosAjustes.Filas - DuelosAjustes.FilasDespliegue;
}

// ═════════════════════════════════════════════════════════════════════════════
// ESTADO
// ═════════════════════════════════════════════════════════════════════════════
public sealed class DuelosCarta
{
    public int Id;
    public int Dueno;
    public DuelosDefCarta Def = null!;
    public int Pos;
    /// Posición al empezar la última resolución y si se movió en ella
    /// (información pública: sirve para leer al rival).
    public int PosInicioTurno;
    public bool SeMovio;
    public bool Viva = true;
    /// Solo generales (y sus clones, que copian las del original).
    public int Vidas;

    // ── Clon (Musashi) ───────────────────────────────────────────────────────
    public bool EsClon;
    public int ClonDe = -1;
    public int ClonHasta;
    /// Turno en que se creó (no se mueve ese turno).
    public int CreadaTurno;

    // ── Habilidad ────────────────────────────────────────────────────────────
    /// Turno a partir del cual la habilidad vuelve a estar lista.
    public int RecargaLista = DuelosAjustes.HabilidadPrimerTurno;

    // ── Efectos (turno hasta el que duran, inclusive; 0 = sin efecto) ────────
    /// En este turno no puede moverse.
    public int ParalizadaTurno;
    /// En este turno se mueve al azar.
    public int ConfusaTurno;
    public int VenenoHasta;
    public int MasAtaqueHasta;
    public int MasMovHasta;
    public int SupervivenciaHasta;

    /// Causa del último daño (para el informe de la arena).
    public string UltimoDano = "";

    public bool EsGeneral => Def.Papel == DuelosPapel.General;
    public DuelosHabilidad Habilidad => EsClon ? DuelosHabilidad.Ninguna : Def.Habilidad;
    public DuelosCarta Clonar() => (DuelosCarta)MemberwiseClone();
}

public sealed class DuelosJugador
{
    public DuelosEjercito Ejercito;
    /// Id de la carta del rey. En la vista de un jugador, el rey RIVAL es una
    /// suposición (DuelosVista).
    public int ReyId = -1;
    public int DisparosRestantes = DuelosAjustes.DisparosLejanos;
    public bool HabilidadEjercitoUsada;
    public int HabilidadEjercitoTurno;
    public int FortalezaHasta;
    public int FuriaTurno;
    /// Turno hasta el que dura la Movilización (inclusive).
    public int MovilizacionHasta;
    /// Puntos de combate ganados en la partida (DuelosAjustes.PcSoldado por
    /// soldado enemigo destruido y DuelosAjustes.PcGeneral por general
    /// enemigo derrotado, en combate, con disparos o con habilidades).
    public int Pc;

    public DuelosHabilidadEjercito HabilidadEjercito => DuelosCatalogo.Ejercitos[Ejercito].HabilidadEjercito;
    public DuelosJugador Clonar() => (DuelosJugador)MemberwiseClone();
}

public readonly record struct DuelosEvento(string Tipo, int Jugador, int Celda, string Texto);

public sealed class DuelosEstado
{
    public int Turno = 1;
    public List<DuelosCarta> Cartas = new();
    public DuelosJugador[] Jugadores = { new(), new() };

    public bool[] Pilar = new bool[DuelosAjustes.Celdas];
    public bool[] Hundida = new bool[DuelosAjustes.Celdas];
    /// Turno hasta el que hay muro (inclusive); 0 = sin muro.
    public int[] MuroHasta = new int[DuelosAjustes.Celdas];
    public int[] EscudoHasta = new int[DuelosAjustes.Celdas];
    public int[] EscudoDueno = new int[DuelosAjustes.Celdas];

    /// Casillas que se hundirán al resolver el turno en curso.
    public List<int> Hundimientos = new();

    public bool SeHunde(int c) => Hundimientos.Contains(c);

    /// -1 en curso, 0 / 1 = gana ese jugador, 2 = tablas.
    public int Ganador = -1;
    /// Cómo acabó: "combate", "disparo lejano", "disparo cercano",
    /// "hundimiento", "atrapado", "limite", "tablas"…
    public string Motivo = "";
    public int SiguienteId;

    /// Eventos de la última resolución (para el cliente y la arena).
    public List<DuelosEvento> Eventos = new();
    public bool RegistrarEventos = true;

    public bool Terminada => Ganador >= 0;

    public DuelosCarta? Carta(int id)
    {
        foreach (var c in Cartas) if (c.Id == id) return c;
        return null;
    }

    public DuelosCarta? Rey(int jugador) => Carta(Jugadores[jugador].ReyId);

    public bool MuroActivo(int c) => MuroHasta[c] >= Turno;

    /// True si la casilla tiene un escudo activo de un jugador distinto de [jugador].
    public bool EscudoContra(int c, int jugador) => EscudoHasta[c] >= Turno && EscudoDueno[c] != jugador;

    public bool FortalezaActiva(int jugador) => Jugadores[jugador].FortalezaHasta >= Turno;

    public DuelosEstado Clonar()
    {
        var e = (DuelosEstado)MemberwiseClone();
        e.Cartas = new List<DuelosCarta>(Cartas.Count);
        foreach (var c in Cartas) e.Cartas.Add(c.Clonar());
        e.Jugadores = new[] { Jugadores[0].Clonar(), Jugadores[1].Clonar() };
        e.Pilar = (bool[])Pilar.Clone();
        e.Hundida = (bool[])Hundida.Clone();
        e.MuroHasta = (int[])MuroHasta.Clone();
        e.EscudoHasta = (int[])EscudoHasta.Clone();
        e.EscudoDueno = (int[])EscudoDueno.Clone();
        e.Hundimientos = new List<int>(Hundimientos);
        e.Eventos = new List<DuelosEvento>();
        return e;
    }
}

// ═════════════════════════════════════════════════════════════════════════════
// JUGADA (lo que declara un jugador en su turno)
// ═════════════════════════════════════════════════════════════════════════════
public sealed class DuelosUsoHabilidad
{
    /// General que lanza la habilidad.
    public int GeneralId;
    /// Casillas objetivo (muro: 3 encadenadas; resto: 1). Teletransporte:
    /// destino. Supervivencia: casilla de la carta. Clon: casilla del clon.
    public int[] Celdas = Array.Empty<int>();
    /// Carta objetivo (teletransporte, supervivencia: carta propia; clon:
    /// general propio a copiar).
    public int CartaId = -1;
}

/// Objetivos válidos de una habilidad de general (DuelosMotor.OpcionesHabilidad).
///   Modo "" (no disponible), "inmediata" (sin objetivo), "celda" (una casilla
///   de [Celdas]), "carta" (una carta propia de [Cartas]), "carta_celda" (una
///   carta de [Cartas] y uno de sus destinos) o "muro" ([NumCeldas] casillas
///   encadenadas de [Libres], la primera de [Celdas]).
public sealed class DuelosOpcionesHabilidad
{
    public string Modo = "";
    public List<int> Celdas = new();
    public Dictionary<int, List<int>> Cartas = new();
    public List<int> Libres = new();
    public int NumCeldas = 1;
}

public sealed class DuelosJugada
{
    /// cartaId → casilla destino. Las cartas que no aparecen se quedan quietas.
    public Dictionary<int, int> Movimientos = new();
    public List<DuelosUsoHabilidad> Habilidades = new();
    /// Casillas de los disparos lejanos de este turno.
    public List<int> Disparos = new();
    public bool UsarHabilidadEjercito;
    /// Reparación: general a curar.
    public int HabilidadEjercitoCartaId = -1;

    public static DuelosJugada Vacia() => new();
}

/// Configuración inicial de un jugador.
public sealed class DuelosConfigJugador
{
    public DuelosEjercito Ejercito;
    /// Los 2 generales elegidos (nombres del catálogo).
    public string[] Generales = new string[2];
    /// 0 o 1: cuál de los dos generales es el rey.
    public int ReyIndice;
    /// Casillas de despliegue en orden: general 1, general 2, básica, básica,
    /// media, alta. Si es null, despliegue automático.
    public int[]? Despliegue;
}
