// ─────────────────────────────────────────────────────────────────────────────
// HistoriaDuelo.cs
//
// MODO DUELO DE GENERALES. Un JEFE (bot) contra dos generales del jugador:
//   • el CAZADOR (Alvaroth) lo paraliza al caer en su casilla;
//   • el VERDUGO (Albariel) le quita una vida si entra con el jefe paralizado.
// No hay combate normal entre ellos (F + D): cada carta tiene VIDAS y las
// reglas de abajo deciden qué pasa cuando coinciden. El jefe tiene dos
// habilidades: LLUVIA DE ROCAS (disparos por fila con % publicado, mientras
// canaliza sin moverse) y ROMPE ESCUDOS (contra el cazador).
//
// Es un modo GENÉRICO, pensado también para retos: todo se configura con un
// `ConfigDuelo` registrado por id (`HistoriaDuelos.Todas`). Lógica pura, sin
// Firestore. La consumen:
//   • WarZeroHistoria.CrearPartidaHistoriaAsync → estado inicial (`duelo`).
//   • WarZeroHistoria.ConstruirJugadaBotHistoria → IA del jefe
//     (MotorDuelo.DecidirJefe).
//   • WarZeroHistoriaReglas (paso A de la resolución) → MotorDuelo.Resolver:
//     rocas, rompe escudos, parálisis, golpes, separación y muertes. Aparta
//     al jefe del combate normal y el paso B lo devuelve al tablero.
//
// REGLAS (con los valores por defecto de humanos_3)
//   • Vidas: jefe 3 · cazador 2 · verdugo 2. Si cae un general, pierdes; si
//     cae el jefe, ganas. Límite de turnos: si el jefe sigue vivo, pierdes.
//   • Turnos de LLUVIA (2, 4, 6…): el jefe CANALIZA (no se mueve). Al empezar
//     el turno se publica el % de cada casilla; llueve en N filas al azar (más
//     con la furia de la última vida) y en cada una cae 1 roca. Cada roca
//     quita 1 vida a cada general de la casilla. Nunca caen sobre el jefe.
//   • Turnos de ROMPE ESCUDOS (3, 7, 11…): se publica un % sobre todas las
//     casillas a las que puede ir el cazador (más alto junto al jefe); una de
//     ellas recibe el golpe. Si el cazador termina ahí, queda SIN ESCUDO ese
//     turno y los N siguientes.
//   • Encuentros (posiciones al terminar el movimiento):
//       – jefe PARALIZADO + verdugo en su casilla → el jefe pierde 1 vida, se
//         libera y salta a la casilla más alejada de los dos generales;
//       – jefe libre + cazador y verdugo juntos en su casilla → SEPARACIÓN: el
//         cazador sale despedido a la casilla más lejana y el verdugo a una
//         contigua. Sin daño;
//       – jefe libre + cazador CON escudo → el jefe queda PARALIZADO el turno
//         siguiente (el cazador lo sujeta);
//       – jefe libre + cazador SIN escudo → el cazador pierde 1 vida y es
//         empujado a una casilla contigua;
//       – jefe libre + verdugo solo → el verdugo pierde 1 vida y es empujado.
//
// DUELO INVERTIDO (`ConfigDuelo.JugadorEsJefe`, reto «El duelo de Alexander»):
// el JUGADOR controla al jefe y el bot a los dos generales
// (MotorDuelo.DecidirGenerales). Mismas reglas, con estos cambios:
//   • LLUVIA MANUAL (`LluviaManual`): la lluvia de rocas la lanza el jugador
//     desde la carta de Alexander (botón de habilidad, sin coste). Elige él
//     TODAS las casillas donde cae una roca, con el límite de siempre por
//     fila: como mucho `DisparosPorFila` rocas por fila y `FilasConLluvia`
//     filas (más con la furia). Ese turno el jefe CANALIZA (no se mueve) y la
//     habilidad necesita `LluviaRecarga` turnos para volver a estar lista. Lo
//     declara POST /warzero/duelo/lluvia (WarZeroDuelo.cs) en
//     `duelo.lluviaJugador = {turno, coords}`; la IA de los generales NO lo
//     lee (no sabe dónde caerán).
//   • El ROMPE ESCUDOS es automático, como en humanos_3: % publicado sobre el
//     alcance del cazador; los generales lo ven y lo esquivan.
//   • El jefe del jugador no puede moverse paralizado ni el turno en que
//     lanza la lluvia (el servidor revierte el movimiento:
//     WarZeroHistoriaReglas).
// El fin de la partida lo decide EvaluarFinHistoria con las mismas reglas para
// los dos sentidos: si cae un general gana el dueño del jefe; si cae el jefe
// gana el dueño de los generales; al cerrar el turno límite gana el del jefe
// (o el de los generales, con `JefeGanaAlLimite = false`).
// ─────────────────────────────────────────────────────────────────────────────

// ═════════════════════════════════════════════════════════════════════════════
// AJUSTES DE "LOS HERMANOS DEL ALBA · PARTE 3" (humanos_3) — EDITA AQUÍ
// ═════════════════════════════════════════════════════════════════════════════
public static class AjustesDueloAlexander
{
    // ── Vidas ────────────────────────────────────────────────────────────────
    public const int VidasAlexander = 3;
    public const int VidasAlvaroth = 2;
    public const int VidasAlbariel = 2;

    /// Si al cerrar este turno Alexander sigue vivo, escapa y pierdes.
    public const int TurnoLimite = 25;

    /// Movimiento de Alexander en sus turnos de movimiento.
    public const int MovimientoAlexander = 2;

    /// Turnos que dura la parálisis de Alvaroth sobre Alexander.
    public const int TurnosParalisis = 1;

    // ── Lluvia de rocas ──────────────────────────────────────────────────────
    /// Primer turno de lluvia y cada cuántos turnos se repite (2 y 2 = turnos
    /// pares). En esos turnos Alexander canaliza y NO se mueve.
    public const int LluviaPrimerTurno = 2;
    public const int LluviaCadaTurnos = 2;
    /// Filas en las que LLUEVE cada turno de lluvia (elegidas al azar; en el
    /// resto, 0 %). 0 = en todas las filas.
    public const int FilasConLluvia = 3;
    /// Filas con lluvia cuando a Alexander le queda la ÚLTIMA vida (furia).
    public const int FilasConLluviaFuria = 5;
    /// Disparos que caen en CADA FILA con lluvia.
    public const int DisparosPorFila = 1;
    /// Disparos por fila con lluvia durante la furia.
    public const int DisparosPorFilaFuria = 1;
    /// Cuánto se concentran las rocas cerca de tus generales (0 = uniforme).
    public const double LluviaAcoso = 1.0;

    // ── Rompe escudos ────────────────────────────────────────────────────────
    /// Primer turno y cada cuántos turnos (3 y 4 = turnos 3, 7, 11…).
    public const int RompeEscudosPrimerTurno = 3;
    public const int RompeEscudosCadaTurnos = 4;
    /// Turnos SIN escudo tras el golpe (además del propio turno del golpe).
    public const int TurnosSinEscudo = 2;
    /// Cuánto más probable es el golpe en las casillas junto a Alexander.
    public const double RompeEscudosSesgoJefe = 4.0;

    // ── IA de Alexander ──────────────────────────────────────────────────────
    /// "Torpeza" al huir: 0.5 = casi perfecto (muy difícil), 1.0 = normal,
    /// 1.5 = se equivoca a menudo (fácil).
    public const double TorpezaAlexander = 1.0;

    // ── Mapa ─────────────────────────────────────────────────────────────────
    /// Casillas bloqueadas (pilares). D1 y D7 alojan los cuarteles nominales.
    public static readonly string[] CeldasBloqueadas = { "C3", "C5", "E3", "E5", "D1", "D7" };
}

// ═════════════════════════════════════════════════════════════════════════════
// AJUSTES DEL RETO «EL DUELO DE ALEXANDER» (tú eres Alexander) — EDITA AQUÍ
// ═════════════════════════════════════════════════════════════════════════════
public static class AjustesRetoAlexander
{
    // ── Vidas ────────────────────────────────────────────────────────────────
    public const int VidasAlexander = 2;
    public const int VidasAlvaroth = 2;
    public const int VidasAlbariel = 2;

    /// Turno límite del duelo.
    public const int TurnoLimite = 15;

    /// ¿Qué pasa si al cerrar el turno límite nadie ha caído?
    ///   false = PIERDES: tienes que derribar a Alvaroth o a Albariel antes.
    ///   true  = GANAS: basta con sobrevivir. OJO: con la lluvia manual
    ///           Alexander puede moverse todos los turnos y, huyendo sin parar,
    ///           Alvaroth (que mueve lo mismo) casi nunca lo alcanza: el reto
    ///           se gana solo con correr.
    public const bool GanasSiSobrevives = false;

    /// Movimiento de Alexander.
    public const int MovimientoAlexander = 2;

    /// Turnos que Alvaroth te deja paralizado al caer en tu casilla.
    public const int TurnosParalisis = 1;

    // ── Lluvia de rocas (la lanzas tú desde la carta de Alexander) ──────────
    /// Turnos de RECARGA tras lanzarla (2 = si la lanzas en el turno 5, vuelve
    /// a estar lista en el 8). El turno en que la lanzas no te mueves.
    public const int LluviaRecarga = 2;
    /// Máximo de FILAS en las que puedes hacer caer rocas, y con tu última
    /// vida (furia). 0 = en todas.
    public const int FilasConLluvia = 3;
    public const int FilasConLluviaFuria = 5;
    /// Máximo de rocas en CADA fila (normal y furia).
    public const int DisparosPorFila = 1;
    public const int DisparosPorFilaFuria = 1;
    /// Con tu lluvia lista, los generales se mueven más al azar para que no
    /// les adivines la casilla (1 = igual que siempre, 3 = muy imprevisibles).
    public const double LluviaImprevisibilidad = 2.5;

    // ── Rompe escudos (automático, contra Alvaroth) ─────────────────────────
    /// Primer turno y cada cuántos turnos (3 y 4 = turnos 3, 7, 11…). Se ve un
    /// % sobre las casillas a las que puede ir Alvaroth; una recibe el golpe.
    public const int RompeEscudosPrimerTurno = 3;
    public const int RompeEscudosCadaTurnos = 4;
    /// Turnos SIN escudo tras el golpe (además del propio turno del golpe).
    public const int TurnosSinEscudo = 2;
    /// Cuánto más probable es el golpe en las casillas junto a Alexander.
    public const double RompeEscudosSesgoJefe = 4.0;

    // ── IA de Alvaroth y Albariel ────────────────────────────────────────────
    /// "Torpeza" de los generales: 0.5 = casi perfectos (muy difícil),
    /// 1.0 = normal, 1.5 = se equivocan a menudo (fácil).
    public const double TorpezaGenerales = 1.0;

    // ── Mapa (el mismo pilar central que la parte 3) ────────────────────────
    public static readonly string[] CeldasBloqueadas = { "C3", "C5", "E3", "E5", "D1", "D7" };
}

/// Configuración de un duelo.
public sealed record ConfigDuelo(
    /// Id de catálogo del JEFE (bot).
    string JefeId,
    /// Id de catálogo del CAZADOR (paraliza) y del VERDUGO (quita vidas).
    string CazadorId,
    string VerdugoId,
    int VidasJefe,
    int VidasCazador,
    int VidasVerdugo,
    /// 0 = sin límite.
    int TurnoLimite,
    int MovimientoJefe,
    IReadOnlyList<string> CeldasBloqueadas,
    int TurnosParalisis = 1,
    int LluviaPrimerTurno = 2,
    int LluviaCadaTurnos = 2,
    int DisparosPorFila = 1,
    int DisparosPorFilaFuria = 2,
    /// Filas con lluvia por turno de lluvia (0 = todas) y en la furia.
    int FilasConLluvia = 0,
    int FilasConLluviaFuria = 0,
    double LluviaAcoso = 1.0,
    /// En los turnos de lluvia el jefe no se mueve.
    bool JefeCanalizaEnLluvia = true,
    int RompePrimerTurno = 3,
    /// 0 = sin rompe escudos.
    int RompeCadaTurnos = 4,
    int TurnosSinEscudo = 2,
    double RompeSesgoJefe = 4.0,
    double TorpezaJefe = 1.0,
    /// true = el JUGADOR controla al jefe y el bot a los generales.
    bool JugadorEsJefe = false,
    /// Torpeza de la IA de los generales (duelo invertido).
    double TorpezaGenerales = 1.0,
    /// true = la lluvia de rocas la lanza el JUGADOR (que lleva al jefe) y
    /// elige las casillas; false = cae sola en los turnos de lluvia, con el %
    /// publicado. Con lluvia manual, `FilasConLluvia(Furia)` y
    /// `DisparosPorFila(Furia)` son los LÍMITES de su elección.
    bool LluviaManual = false,
    /// Turnos de recarga de la lluvia manual tras lanzarla.
    int LluviaRecarga = 2,
    /// Cuánto más al azar se mueven los generales cuando el jugador tiene la
    /// lluvia manual lista (para que no se les adivine la casilla).
    double LluviaImprevisibilidad = 1.0,
    /// Al cerrar el turno límite con el jefe vivo, ¿quién gana? true = el
    /// dueño del jefe (humanos_3: Alexander escapa); false = el dueño de los
    /// generales (el jefe tenía que derribar a uno antes).
    bool JefeGanaAlLimite = true)
{
    /// Máximo de filas con rocas (0 = todas) y de rocas por fila.
    public int MaxFilasLluvia(bool furia) => furia ? FilasConLluviaFuria : FilasConLluvia;
    public int RocasPorFila(bool furia) => Math.Max(1, furia ? DisparosPorFilaFuria : DisparosPorFila);

    public bool EsTurnoLluvia(int turno) =>
        LluviaCadaTurnos > 0 && turno >= LluviaPrimerTurno
        && (turno - LluviaPrimerTurno) % LluviaCadaTurnos == 0;

    public bool EsTurnoRompe(int turno) =>
        RompeCadaTurnos > 0 && turno >= RompePrimerTurno
        && (turno - RompePrimerTurno) % RompeCadaTurnos == 0;

    /// Guion de bombardeo equivalente a la lluvia (para reutilizar el
    /// planificador y el sorteo por filas de HistoriaBombardeo.cs).
    public GuionBombardeo GuionLluvia(bool furia) => new(
        cartaArtilleriaId: "",
        tramos: new[] { new TramoDisparos(1, furia ? DisparosPorFilaFuria : DisparosPorFila) },
        pesos: new ConfigPesos(PesoBase: 1.0, PesoAcoso: LluviaAcoso, RadioAcoso: 2));
}

/// Duelos registrados (por id de historia o, en el futuro, de reto).
public static class HistoriaDuelos
{
    private const string Alexander = "VKM1uUkqO9GqDI6tTr47"; // General Alexander · F40 D35 M2
    private const string Alvaroth = "uOfCRljbVXoRNWXwv6gL";  // General Alvaroth · F48 D30 M2
    private const string Albariel = "wWGvqQWEjZRZZcyab6yU";  // General Albariel · F55 D20 M3

    private static ConfigDuelo DueloAlexander => new(
        JefeId: Alexander,
        CazadorId: Alvaroth,
        VerdugoId: Albariel,
        VidasJefe: AjustesDueloAlexander.VidasAlexander,
        VidasCazador: AjustesDueloAlexander.VidasAlvaroth,
        VidasVerdugo: AjustesDueloAlexander.VidasAlbariel,
        TurnoLimite: AjustesDueloAlexander.TurnoLimite,
        MovimientoJefe: AjustesDueloAlexander.MovimientoAlexander,
        CeldasBloqueadas: AjustesDueloAlexander.CeldasBloqueadas,
        TurnosParalisis: AjustesDueloAlexander.TurnosParalisis,
        LluviaPrimerTurno: AjustesDueloAlexander.LluviaPrimerTurno,
        LluviaCadaTurnos: AjustesDueloAlexander.LluviaCadaTurnos,
        DisparosPorFila: AjustesDueloAlexander.DisparosPorFila,
        DisparosPorFilaFuria: AjustesDueloAlexander.DisparosPorFilaFuria,
        FilasConLluvia: AjustesDueloAlexander.FilasConLluvia,
        FilasConLluviaFuria: AjustesDueloAlexander.FilasConLluviaFuria,
        LluviaAcoso: AjustesDueloAlexander.LluviaAcoso,
        JefeCanalizaEnLluvia: true,
        RompePrimerTurno: AjustesDueloAlexander.RompeEscudosPrimerTurno,
        RompeCadaTurnos: AjustesDueloAlexander.RompeEscudosCadaTurnos,
        TurnosSinEscudo: AjustesDueloAlexander.TurnosSinEscudo,
        RompeSesgoJefe: AjustesDueloAlexander.RompeEscudosSesgoJefe,
        TorpezaJefe: AjustesDueloAlexander.TorpezaAlexander);

    /// Reto «El duelo de Alexander»: el jugador es Alexander.
    private static ConfigDuelo RetoAlexander => new(
        JefeId: Alexander,
        CazadorId: Alvaroth,
        VerdugoId: Albariel,
        VidasJefe: AjustesRetoAlexander.VidasAlexander,
        VidasCazador: AjustesRetoAlexander.VidasAlvaroth,
        VidasVerdugo: AjustesRetoAlexander.VidasAlbariel,
        TurnoLimite: AjustesRetoAlexander.TurnoLimite,
        MovimientoJefe: AjustesRetoAlexander.MovimientoAlexander,
        CeldasBloqueadas: AjustesRetoAlexander.CeldasBloqueadas,
        TurnosParalisis: AjustesRetoAlexander.TurnosParalisis,
        DisparosPorFila: AjustesRetoAlexander.DisparosPorFila,
        DisparosPorFilaFuria: AjustesRetoAlexander.DisparosPorFilaFuria,
        FilasConLluvia: AjustesRetoAlexander.FilasConLluvia,
        FilasConLluviaFuria: AjustesRetoAlexander.FilasConLluviaFuria,
        JefeCanalizaEnLluvia: true,
        RompePrimerTurno: AjustesRetoAlexander.RompeEscudosPrimerTurno,
        RompeCadaTurnos: AjustesRetoAlexander.RompeEscudosCadaTurnos,
        TurnosSinEscudo: AjustesRetoAlexander.TurnosSinEscudo,
        RompeSesgoJefe: AjustesRetoAlexander.RompeEscudosSesgoJefe,
        JugadorEsJefe: true,
        TorpezaGenerales: AjustesRetoAlexander.TorpezaGenerales,
        LluviaManual: true,
        LluviaRecarga: AjustesRetoAlexander.LluviaRecarga,
        LluviaImprevisibilidad: AjustesRetoAlexander.LluviaImprevisibilidad,
        JefeGanaAlLimite: AjustesRetoAlexander.GanasSiSobrevives);

    /// Id de la batalla (HistoriaCatalogo) del reto «El duelo de Alexander».
    public const string IdRetoAlexander = "reto_duelo_alexander";

    /// Propiedad (no campo) para leer siempre los ajustes actuales.
    public static IReadOnlyDictionary<string, ConfigDuelo> Todas =>
        new Dictionary<string, ConfigDuelo>
        {
            ["humanos_3"] = DueloAlexander,
            [IdRetoAlexander] = RetoAlexander,
        };

    public static ConfigDuelo? Get(string id) =>
        !string.IsNullOrEmpty(id) && Todas.TryGetValue(id, out var c) ? c : null;
}

/// Un suceso del duelo en la última resolución (aviso del cliente). [Malo] es
/// desde el punto de vista del JUGADOR (le hiere a él, le paraliza…).
public readonly record struct EventoDuelo(string tipo, string texto, string coord, bool malo = false);

/// Estado PÚBLICO del duelo (campo `duelo` de la partida).
public sealed class EstadoDuelo
{
    public const string RolJefe = "jefe";
    public const string RolCazador = "cazador";
    public const string RolVerdugo = "verdugo";

    /// instanceId → rol.
    public Dictionary<string, string> Roles { get; } = new();
    public Dictionary<string, int> Vidas { get; } = new();
    public Dictionary<string, int> VidasMax { get; } = new();
    public Dictionary<string, string> Nombres { get; } = new();

    /// Dueño del jefe y dueño de los dos generales.
    public string JefeUid { get; set; } = "";
    public string GeneralesUid { get; set; } = "";

    /// Último turno (inclusive) en que el jefe está paralizado. 0 = libre.
    public int ParalizadoHasta { get; set; }
    /// Último turno (inclusive) en que el cazador está sin escudo. 0 = con escudo.
    public int SinEscudoHasta { get; set; }

    /// Rompe escudos publicado para el turno `RompeTurno` (coord → 0..1).
    public int RompeTurno { get; set; }
    public Dictionary<string, double> RompeProb { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// Lluvia MANUAL declarada por el jugador (`duelo.lluviaJugador`): turno
    /// para el que vale y casillas donde caen las rocas.
    public int LluviaJugadorTurno { get; set; }
    public List<string> LluviaJugadorCoords { get; } = new();
    /// Último turno en que el jugador lanzó la lluvia manual (0 = nunca).
    public int LluviaUltimoTurno { get; set; }

    // Lo que pasó en la última resolución (aviso del cliente).
    public int UltimoTurno { get; set; }
    public List<EventoDuelo> Eventos { get; } = new();

    public string? IdDe(string rol) => Roles.FirstOrDefault(kv => kv.Value == rol).Key;
    public int VidasDe(string rol) { var id = IdDe(rol); return id != null && Vidas.TryGetValue(id, out var v) ? v : 0; }

    /// Dueño de la carta con ese rol.
    public string DuenoDe(string rol) => rol == RolJefe ? JefeUid : GeneralesUid;

    public bool Paralizado(int turno) => turno <= ParalizadoHasta;
    public bool SinEscudo(int turno) => turno <= SinEscudoHasta;

    /// True si el jefe NO puede moverse en [turno] (paralizado o canalizando
    /// la lluvia: la del calendario o, con lluvia manual, la que ha lanzado).
    public bool JefeInmovil(ConfigDuelo cfg, int turno) =>
        Paralizado(turno) || (cfg.JefeCanalizaEnLluvia && (cfg.LluviaManual
            ? LluviaDeclarada(turno)
            : cfg.EsTurnoLluvia(turno)));

    /// True si el jugador ha declarado la lluvia manual para [turno].
    public bool LluviaDeclarada(int turno) =>
        LluviaJugadorTurno == turno && LluviaJugadorCoords.Count > 0;

    /// True si la lluvia manual está lista (sin recarga) en [turno].
    public bool LluviaDisponible(ConfigDuelo cfg, int turno) =>
        cfg.LluviaManual && (LluviaUltimoTurno <= 0 || turno - LluviaUltimoTurno > Math.Max(0, cfg.LluviaRecarga));

    /// True si el jefe está en su última vida (furia).
    public bool Furia => VidasDe(RolJefe) == 1;

    public Dictionary<string, object?> ACampo(ConfigDuelo cfg, int turnoSiguiente) => new()
    {
        ["roles"] = Roles.ToDictionary(kv => kv.Key, kv => (object?)kv.Value),
        ["vidas"] = Vidas.ToDictionary(kv => kv.Key, kv => (object?)(long)kv.Value),
        ["vidasMax"] = VidasMax.ToDictionary(kv => kv.Key, kv => (object?)(long)kv.Value),
        ["nombres"] = Nombres.ToDictionary(kv => kv.Key, kv => (object?)kv.Value),
        ["jefeUid"] = JefeUid,
        ["generalesUid"] = GeneralesUid,
        ["jugadorEsJefe"] = cfg.JugadorEsJefe,
        ["movimientoJefe"] = (long)cfg.MovimientoJefe,
        ["paralizadoHasta"] = (long)ParalizadoHasta,
        ["sinEscudoHasta"] = (long)SinEscudoHasta,
        ["rompe"] = new Dictionary<string, object?>
        {
            ["turno"] = (long)RompeTurno,
            ["prob"] = RompeProb.ToDictionary(
                kv => kv.Key, kv => (object?)(long)Math.Clamp(Math.Round(kv.Value * 100), 1, 100)),
            ["exacta"] = RompeProb.ToDictionary(kv => kv.Key, kv => (object?)kv.Value),
            // Rompe escudos MANUAL: lo lanza el jugador (botón de la carta del
            // jefe) en los turnos de rompe; el golpe cubre la casilla elegida
            // y las que estén a `radio` casillas o menos.
        },
        // Lluvia MANUAL (la lanza el jugador desde la carta del jefe): si está
        // lista, recarga y límites de su elección. `lluviaJugador` NO se
        // publica: se consume en la resolución.
        ["lluvia"] = new Dictionary<string, object?>
        {
            ["manual"] = cfg.LluviaManual,
            ["recarga"] = (long)Math.Max(0, cfg.LluviaRecarga),
            ["ultimoTurno"] = (long)LluviaUltimoTurno,
            ["disponible"] = LluviaDisponible(cfg, turnoSiguiente),
            ["maxFilas"] = (long)cfg.MaxFilasLluvia(Furia),
            ["porFila"] = (long)cfg.RocasPorFila(Furia),
        },
        // Calendario (para la leyenda del cliente).
        ["turnoLimite"] = (long)cfg.TurnoLimite,
        ["lluviaEsteTurno"] = cfg.LluviaManual
            ? LluviaDisponible(cfg, turnoSiguiente)
            : cfg.EsTurnoLluvia(turnoSiguiente),
        ["rompeEsteTurno"] = cfg.EsTurnoRompe(turnoSiguiente),
        ["canaliza"] = !cfg.LluviaManual && cfg.JefeCanalizaEnLluvia && cfg.EsTurnoLluvia(turnoSiguiente),
        ["turno"] = (long)turnoSiguiente,
        ["ultimo"] = new Dictionary<string, object?>
        {
            ["turno"] = (long)UltimoTurno,
            ["eventos"] = Eventos.Select(e => (object?)new Dictionary<string, object?>
            {
                ["tipo"] = e.tipo,
                ["texto"] = e.texto,
                ["coord"] = e.coord,
                ["malo"] = e.malo,
            }).ToList(),
        },
    };

    public static EstadoDuelo? DesdeCampo(object? raw)
    {
        var m = M.Map(raw);
        if (m.Count == 0) return null;
        var e = new EstadoDuelo();
        foreach (var kv in M.Map(M.Get(m, "roles"))) e.Roles[kv.Key] = M.Str(kv.Value);
        foreach (var kv in M.Map(M.Get(m, "vidas"))) e.Vidas[kv.Key] = M.Int(kv.Value);
        foreach (var kv in M.Map(M.Get(m, "vidasMax"))) e.VidasMax[kv.Key] = M.Int(kv.Value);
        foreach (var kv in M.Map(M.Get(m, "nombres"))) e.Nombres[kv.Key] = M.Str(kv.Value);
        e.JefeUid = M.Str(M.Get(m, "jefeUid"));
        e.GeneralesUid = M.Str(M.Get(m, "generalesUid"));
        e.ParalizadoHasta = M.Int(M.Get(m, "paralizadoHasta"));
        e.SinEscudoHasta = M.Int(M.Get(m, "sinEscudoHasta"));
        var rompe = M.Map(M.Get(m, "rompe"));
        e.RompeTurno = M.Int(M.Get(rompe, "turno"));
        foreach (var kv in M.Map(M.Get(rompe, "exacta"))) e.RompeProb[kv.Key] = M.Dbl(kv.Value);
        e.LluviaUltimoTurno = M.Int(M.Get(M.Map(M.Get(m, "lluvia")), "ultimoTurno"));
        var lj = M.Map(M.Get(m, "lluviaJugador"));
        e.LluviaJugadorTurno = M.Int(M.Get(lj, "turno"));
        foreach (var c in M.List(M.Get(lj, "coords")))
        {
            var coord = M.Str(c).Trim().ToUpperInvariant();
            if (coord != "" && !e.LluviaJugadorCoords.Contains(coord)) e.LluviaJugadorCoords.Add(coord);
        }
        return e;
    }
}

/// Lógica del duelo (sin Firestore).
public static class MotorDuelo
{
    /// Estado inicial: localiza en el tablero sembrado al jefe, al cazador y
    /// al verdugo (por id de catálogo y dueño) y les da sus vidas. El jefe es
    /// del bot y los generales del jugador, salvo en el duelo invertido
    /// (`JugadorEsJefe`).
    public static EstadoDuelo Inicial(
        ConfigDuelo cfg, Dictionary<string, List<Dictionary<string, object?>>> tablero,
        string jugadorUid, string botUid)
    {
        var e = new EstadoDuelo();
        CompletarDuenos(cfg, e, jugadorUid, botUid);
        void Registrar(string cartaId, string owner, string rol, int vidas)
        {
            var c = tablero.Values.SelectMany(l => l).FirstOrDefault(x =>
                M.Str(M.Get(x, "id")) == cartaId && M.Str(M.Get(x, "ownerUid")) == owner);
            if (c == null)
            {
                Console.Error.WriteLine($"[WZ.Duelo] no encuentro la carta {cartaId} ({rol}) en el tablero");
                return;
            }
            var iid = M.Str(M.Get(c, "instanceId"));
            e.Roles[iid] = rol;
            e.Vidas[iid] = Math.Max(1, vidas);
            e.VidasMax[iid] = Math.Max(1, vidas);
            e.Nombres[iid] = M.Str(M.Get(c, "Nombre", "nombre"));
        }
        Registrar(cfg.JefeId, e.JefeUid, EstadoDuelo.RolJefe, cfg.VidasJefe);
        Registrar(cfg.CazadorId, e.GeneralesUid, EstadoDuelo.RolCazador, cfg.VidasCazador);
        Registrar(cfg.VerdugoId, e.GeneralesUid, EstadoDuelo.RolVerdugo, cfg.VidasVerdugo);
        return e;
    }

    /// Rellena los dueños del jefe y de los generales si faltan (partidas
    /// creadas antes de que existieran estos campos).
    public static void CompletarDuenos(ConfigDuelo cfg, EstadoDuelo e, string jugadorUid, string botUid)
    {
        if (e.JefeUid == "") e.JefeUid = cfg.JugadorEsJefe ? jugadorUid : botUid;
        if (e.GeneralesUid == "") e.GeneralesUid = cfg.JugadorEsJefe ? botUid : jugadorUid;
    }

    // ── Planes publicados al EMPEZAR un turno ────────────────────────────────

    /// Plan de la LLUVIA de [turno] (null si ese turno no llueve). Cae sobre
    /// los GENERALES (cartas de `GeneralesUid`); nunca sobre el jefe ni sobre
    /// casillas bloqueadas o intransitables.
    public static PlanBombardeo? PlanLluvia(
        ConfigDuelo cfg, EstadoDuelo e, int turno, int semilla,
        Dictionary<string, List<Dictionary<string, object?>>> tablero,
        Func<string, bool> transitable, int filas, int columnas)
    {
        if (cfg.LluviaManual || !cfg.EsTurnoLluvia(turno)) return null;
        var jefe = CeldaDe(tablero, e.IdDe(EstadoDuelo.RolJefe));
        bool furia = e.VidasDe(EstadoDuelo.RolJefe) == 1;
        var bloqueadas = Bloqueadas(cfg);
        var generales = e.GeneralesUid;

        // Filas con lluvia este turno (al azar, estables para la partida y el
        // turno). En las demás no cae nada (0 %).
        int nFilas = furia ? cfg.FilasConLluviaFuria : cfg.FilasConLluvia;
        HashSet<char>? filasLluvia = null;
        if (nFilas > 0 && nFilas < filas)
        {
            var rng = new Random(PlanificadorBombardeo.Semilla(semilla, "duelo-filas", turno));
            filasLluvia = Enumerable.Range(0, filas).OrderBy(_ => rng.Next()).Take(nFilas)
                .Select(i => (char)('A' + i)).ToHashSet();
        }
        bool EnFilaConLluvia(string c) => filasLluvia == null || filasLluvia.Contains(char.ToUpperInvariant(c[0]));

        var cartas = tablero
            .Where(kv => kv.Value.Any(c => M.Str(M.Get(c, "ownerUid")) == generales))
            .ToDictionary(kv => kv.Key, kv => kv.Value.Count(c => M.Str(M.Get(c, "ownerUid")) == generales),
                StringComparer.OrdinalIgnoreCase);
        return PlanificadorBombardeo.Preparar(cfg.GuionLluvia(furia), new PlanificadorBombardeo.Entrada(
            Turno: turno,
            Semilla: semilla,
            Filas: filas,
            Columnas: columnas,
            Transitable: c => transitable(c) && !bloqueadas.Contains(c) && EnFilaConLluvia(c),
            CuartelJugador: "",
            CuartelBot: jefe ?? "",
            CartasJugador: cartas,
            EscudosJugador: new HashSet<string>(),
            Reduccion: 0));
    }

    /// Prepara (en [e]) el ROMPE ESCUDOS de [turno]: % sobre todas las
    /// casillas a las que puede llegar el cazador (incluida la suya), más
    /// alto junto al jefe. Suman 100 %: una de ellas recibe el golpe.
    public static void PrepararRompe(
        ConfigDuelo cfg, EstadoDuelo e, int turno,
        Dictionary<string, List<Dictionary<string, object?>>> tablero,
        Func<string, bool> transitable, int filas, int columnas)
    {
        e.RompeProb.Clear();
        e.RompeTurno = 0;
        if (!cfg.EsTurnoRompe(turno)) return;
        var cazId = e.IdDe(EstadoDuelo.RolCazador);
        var caz = CeldaDe(tablero, cazId);
        var jefe = CeldaDe(tablero, e.IdDe(EstadoDuelo.RolJefe));
        if (caz == null) return;
        var carta = tablero[caz].First(c => M.Str(M.Get(c, "instanceId")) == cazId);
        int mov = Math.Max(1, M.Int(M.Get(carta, "Movimiento", "movimiento")));
        var bloqueadas = Bloqueadas(cfg);
        bool Pasa(string c) => transitable(c) && !bloqueadas.Contains(c);
        var alcance = Alcance(caz, mov, Pasa, filas, columnas);
        alcance.Add(caz);
        var pesos = alcance.ToDictionary(c => c, c =>
        {
            int d = jefe == null ? 9 : Distancia(c, jefe, Pasa, filas, columnas);
            return 1.0 + cfg.RompeSesgoJefe / (1 + Math.Min(d, 9));
        }, StringComparer.OrdinalIgnoreCase);
        double total = pesos.Values.Sum();
        foreach (var kv in pesos) e.RompeProb[kv.Key] = kv.Value / total;
        e.RompeTurno = turno;
    }

    /// Valida la LLUVIA MANUAL que quiere lanzar el jugador: casillas dentro
    /// del tablero, sin bloquear, distintas de la del jefe, como mucho
    /// `RocasPorFila` por fila y `MaxFilasLluvia` filas distintas (0 = todas).
    /// Devuelve null si vale o el motivo si no.
    public static string? ValidarLluvia(
        ConfigDuelo cfg, EstadoDuelo e, IReadOnlyCollection<string> coords,
        string? celdaJefe, int filas, int columnas)
    {
        if (coords.Count == 0) return null;   // anular
        var bloqueadas = Bloqueadas(cfg);
        var porFila = new Dictionary<char, int>();
        var vistas = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in coords)
        {
            var c = (raw ?? "").Trim().ToUpperInvariant();
            if (!Parse(c, out int r, out int col) || r >= filas || col < 1 || col > columnas)
                return $"casilla no válida: {c}";
            if (bloqueadas.Contains(c)) return $"{c} está bloqueada";
            if (celdaJefe != null && string.Equals(c, celdaJefe, StringComparison.OrdinalIgnoreCase))
                return "las rocas no pueden caer sobre ti";
            if (!vistas.Add(c)) return $"{c} está repetida";
            porFila[c[0]] = porFila.GetValueOrDefault(c[0]) + 1;
        }
        int max = cfg.RocasPorFila(e.Furia);
        var llena = porFila.FirstOrDefault(kv => kv.Value > max);
        if (llena.Value > 0) return $"como mucho {max} roca{(max == 1 ? "" : "s")} por fila (fila {llena.Key})";
        int maxFilas = cfg.MaxFilasLluvia(e.Furia);
        if (maxFilas > 0 && porFila.Count > maxFilas) return $"como mucho {maxFilas} filas";
        return null;
    }

    private static bool Parse(string coord, out int fila, out int col)
    {
        fila = -1; col = -1;
        if (string.IsNullOrWhiteSpace(coord) || coord.Length < 2) return false;
        coord = coord.Trim().ToUpperInvariant();
        fila = coord[0] - 'A';
        return fila >= 0 && int.TryParse(coord[1..], out col);
    }

    // ── IA del jefe ──────────────────────────────────────────────────────────

    /// Casilla a la que mueve el jefe este turno.
    ///   • Paralizado o canalizando la lluvia → se queda.
    ///   • Cazador SIN escudo → va a por él (a su casilla si llega).
    ///   • Si no, HUYE: prefiere casillas lejos de todo lo que el cazador puede
    ///     alcanzar este turno (así no lo atrapan, tampoco el turno siguiente
    ///     si toca canalizar) y con espacio para moverse. Elige con algo de
    ///     azar (`TorpezaJefe`): no es perfecto, se le puede leer y acorralar.
    public static string DecidirJefe(
        ConfigDuelo cfg, EstadoDuelo e, int turno, int semilla,
        Dictionary<string, List<Dictionary<string, object?>>> tablero,
        Func<string, bool> transitable, int filas, int columnas)
    {
        var jefe = CeldaDe(tablero, e.IdDe(EstadoDuelo.RolJefe));
        if (jefe == null) return "";
        if (e.Paralizado(turno)) return jefe;
        if (cfg.JefeCanalizaEnLluvia && cfg.EsTurnoLluvia(turno)) return jefe;

        var bloqueadas = Bloqueadas(cfg);
        bool Pasa(string c) => transitable(c) && !bloqueadas.Contains(c);
        var opciones = Alcance(jefe, cfg.MovimientoJefe, Pasa, filas, columnas);
        opciones.Add(jefe);

        var cazId = e.IdDe(EstadoDuelo.RolCazador);
        var caz = CeldaDe(tablero, cazId);
        var rng = new Random(PlanificadorBombardeo.Semilla(semilla, "duelo-jefe", turno));
        if (caz == null) return opciones.OrderBy(_ => rng.Next()).First();

        int movCaz = 2;
        var cartaCaz = tablero[caz].FirstOrDefault(c => M.Str(M.Get(c, "instanceId")) == cazId);
        if (cartaCaz != null) movCaz = Math.Max(1, M.Int(M.Get(cartaCaz, "Movimiento", "movimiento")));

        // Cazador sin escudo: a por él.
        if (e.SinEscudo(turno))
        {
            if (opciones.Contains(caz)) return caz;
            return opciones
                .OrderBy(c => Distancia(c, caz, Pasa, filas, columnas))
                .ThenBy(_ => rng.Next())
                .First();
        }

        // Huida con softmax.
        var alcanceCaz = Alcance(caz, movCaz, Pasa, filas, columnas);
        alcanceCaz.Add(caz);
        double temp = Math.Max(0.05, cfg.TorpezaJefe);
        var lista = opciones.OrderBy(c => c, StringComparer.Ordinal).ToList();
        var pesos = lista.Select(c =>
        {
            int d = alcanceCaz.Min(x => Distancia(c, x, Pasa, filas, columnas));
            double libertad = Alcance(c, cfg.MovimientoJefe, Pasa, filas, columnas).Count;
            double score = 0.8 * Math.Min(d, 5) + 0.05 * libertad;
            return Math.Exp(score / temp);
        }).ToList();
        double r = rng.NextDouble() * pesos.Sum(), acc = 0;
        for (int i = 0; i < lista.Count; i++)
        {
            acc += pesos[i];
            if (r < acc) return lista[i];
        }
        return lista[^1];
    }

    // ── IA de los generales (duelo invertido) ────────────────────────────────

    /// Destinos de los GENERALES del bot este turno (instanceId → casilla) en
    /// el duelo invertido. Ven el % de rocas publicado; no saben dónde caerá el
    /// rompe escudos del jugador ni a dónde moverá al jefe.
    ///   • CAZADOR con escudo: si el jefe está quieto (canaliza) y llega, cae
    ///     en su casilla (lo paraliza). Si se mueve, va a la casilla donde es
    ///     más probable que termine (lo predice como huiría la IA del jefe).
    ///     Si ya está paralizado, espera cerca.
    ///   • CAZADOR sin escudo: huye del alcance del jefe.
    ///   • VERDUGO: entra si el jefe está paralizado y llega; si no, se queda
    ///     fuera del alcance del jefe (o junto al cazador: así el jefe los
    ///     separa en vez de herirle) y lo bastante cerca para rematar.
    ///   • Los dos evitan las casillas con % de roca.
    /// `TorpezaGenerales` controla cuánto se equivocan (elección softmax).
    public static Dictionary<string, string> DecidirGenerales(
        ConfigDuelo cfg, EstadoDuelo e, int turno, int semilla,
        Dictionary<string, List<Dictionary<string, object?>>> tablero,
        PlanBombardeo? lluvia,
        Func<string, bool> transitable, int filas, int columnas)
    {
        var res = new Dictionary<string, string>();
        var idJ = e.IdDe(EstadoDuelo.RolJefe);
        var idC = e.IdDe(EstadoDuelo.RolCazador);
        var idV = e.IdDe(EstadoDuelo.RolVerdugo);
        var J = CeldaDe(tablero, idJ);
        var C = CeldaDe(tablero, idC);
        var V = CeldaDe(tablero, idV);
        if (J == null) return res;

        var bloqueadas = Bloqueadas(cfg);
        bool Pasa(string c) => transitable(c) && !bloqueadas.Contains(c);
        int Dist(string a, string b) => Distancia(a, b, Pasa, filas, columnas);
        bool Igual(string? a, string? b) => a != null && b != null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        var rng = new Random(PlanificadorBombardeo.Semilla(semilla, "duelo-generales", turno));
        double temp = Math.Max(0.05, cfg.TorpezaGenerales);
        // Con la lluvia manual lista, más al azar: si no, se les adivina.
        if (cfg.LluviaManual && e.LluviaDisponible(cfg, turno))
            temp *= Math.Max(1.0, cfg.LluviaImprevisibilidad);

        var prob = lluvia != null && lluvia.Turno == turno
            ? lluvia.Probabilidades()
            : new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        double Roca(string c) => prob.TryGetValue(c, out var p) ? p : 0;
        // % de rompe escudos publicado este turno (solo afecta al cazador).
        double Rompe(string c) => e.RompeTurno == turno && e.RompeProb.TryGetValue(c, out var p) ? p : 0;

        int MovDe(string? id, string? celda, int porDefecto)
        {
            if (id == null || celda == null || !tablero.TryGetValue(celda, out var l)) return porDefecto;
            var carta = l.FirstOrDefault(x => M.Str(M.Get(x, "instanceId")) == id);
            return carta == null ? porDefecto : Math.Max(1, M.Int(M.Get(carta, "Movimiento", "movimiento")));
        }
        int movC = MovDe(idC, C, 2), movV = MovDe(idV, V, 3);
        bool vivoC = C != null && idC != null && e.Vidas.GetValueOrDefault(idC) > 0;
        bool vivoV = V != null && idV != null && e.Vidas.GetValueOrDefault(idV) > 0;

        bool paralizado = e.Paralizado(turno);
        // Ojo: con lluvia MANUAL no se mira si el jugador la ha declarado
        // (`lluviaJugador`): la IA no sabe si se va a quedar quieto.
        bool quieto = paralizado
            || (!cfg.LluviaManual && cfg.JefeCanalizaEnLluvia && cfg.EsTurnoLluvia(turno));
        bool puedeLlover = cfg.LluviaManual && e.LluviaDisponible(cfg, turno);
        bool sinEscudo = e.SinEscudo(turno);

        // Dónde puede acabar el jefe este turno y con qué probabilidad. Modelo:
        // huye del alcance del cazador (como la IA del jefe) y, si tiene a su
        // alcance al verdugo o al cazador sin escudo, puede ir a por ellos.
        var pJ = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        if (quieto) pJ[J] = 1.0;
        else
        {
            var opJ = Alcance(J, cfg.MovimientoJefe, Pasa, filas, columnas);
            opJ.Add(J);
            var alcC = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (vivoC && !sinEscudo)
            {
                alcC = Alcance(C!, movC, Pasa, filas, columnas);
                alcC.Add(C!);
            }
            foreach (var x in opJ)
            {
                double sc = 0.05 * Alcance(x, cfg.MovimientoJefe, Pasa, filas, columnas).Count;
                sc += alcC.Count > 0 ? 0.8 * Math.Min(alcC.Min(y => Dist(x, y)), 5) : 2.0;
                if (vivoV && Igual(x, V)) sc += 1.0;                 // presa
                if (sinEscudo && vivoC && Igual(x, C)) sc += 1.0;    // presa
                if (puedeLlover && Igual(x, J)) sc += 1.0;           // se queda a lanzar la lluvia
                pJ[x] = Math.Exp(sc);
            }
            double tot = pJ.Values.Sum();
            foreach (var k in pJ.Keys.ToList()) pJ[k] /= tot;
        }
        double PJ(string c) => pJ.TryGetValue(c, out var p) ? p : 0;
        bool AlAlcanceJefe(string c) => pJ.ContainsKey(c);

        string Elegir(IEnumerable<string> opciones, Func<string, double> score, double t)
        {
            var lista = opciones.OrderBy(c => c, StringComparer.Ordinal).ToList();
            var s = lista.Select(score).ToList();
            double max = s.Max();
            var pesos = s.Select(x => Math.Exp((x - max) / t)).ToList();
            double r = rng.NextDouble() * pesos.Sum(), acc = 0;
            for (int i = 0; i < lista.Count; i++) { acc += pesos[i]; if (r < acc) return lista[i]; }
            return lista[^1];
        }

        // ── CAZADOR ──
        string? destC = C;
        double tempC = temp;
        if (vivoC)
        {
            var opc = Alcance(C!, movC, Pasa, filas, columnas);
            opc.Add(C!);
            if (sinEscudo)
                destC = Elegir(opc, c =>
                    -8.0 * PJ(c) * (quieto ? 1 : 3)
                    - (AlAlcanceJefe(c) && !quieto ? 2.0 : 0)
                    + 0.3 * Math.Min(Dist(c, J), 4)
                    - 15.0 * Roca(c) - 12.0 * Rompe(c), tempC);
            else if (paralizado)
                destC = Elegir(opc, c =>
                    -0.8 * Math.Abs(Dist(c, J) - 1)
                    - (Igual(c, J) ? 1.0 : 0)
                    - 15.0 * Roca(c) - 12.0 * Rompe(c), tempC);
            else if (quieto && opc.Contains(J))
                destC = J;   // canaliza y llego: a por él
            else if (quieto)
                // Canaliza pero no llego: acercarme todo lo posible.
                destC = Elegir(opc, c =>
                    -1.5 * Dist(c, J)
                    - 15.0 * Roca(c) - 12.0 * Rompe(c), tempC);
            else
            {
                // Coincidir con él este turno y, si no, ACORRALARLO: quedar a
                // tiro de donde acabe, sobre todo si el turno siguiente va a
                // canalizar (no podrá huir).
                double pesoTiro = !cfg.LluviaManual && cfg.JefeCanalizaEnLluvia && cfg.EsTurnoLluvia(turno + 1)
                    ? 4.0
                    : 1.5;
                destC = Elegir(opc, c =>
                    6.0 * PJ(c)
                    + pesoTiro * pJ.Sum(kv => Dist(c, kv.Key) <= movC ? kv.Value : 0)
                    - 0.8 * Dist(c, J)
                    - 15.0 * Roca(c) - 12.0 * Rompe(c), tempC);
            }
            res[idC!] = destC!;
        }

        // ── VERDUGO ──
        if (vivoV)
        {
            var opv = Alcance(V!, movV, Pasa, filas, columnas);
            opv.Add(V!);
            if (paralizado && opv.Contains(J))
                res[idV!] = J;
            else
            {
                double Riesgo(string c)
                {
                    if (Igual(c, J)) return 1.0;                          // jefe libre: golpe
                    if (quieto) return 0;
                    if (vivoC && Igual(c, destC)) return 0;               // juntos: separación
                    return AlAlcanceJefe(c) ? Math.Max(PJ(c), 0.25) : 0;
                }
                res[idV!] = Elegir(opv, c =>
                    -14.0 * Riesgo(c)
                    - 15.0 * Roca(c)
                    - (quieto ? 2.0 : 1.0) * Math.Max(0, Dist(c, J) - movV)
                    - 0.5 * Math.Abs(Dist(c, J) - (cfg.MovimientoJefe + 1))
                    - (vivoC && Igual(c, destC) ? 0.8 : 0), temp);
            }
        }
        return res;
    }

    // ── Resolución ───────────────────────────────────────────────────────────

    /// Resuelve el duelo sobre el tablero fusionado [t] (posiciones al terminar
    /// el movimiento) y muta [e]. Devuelve las cartas APARTADAS del combate
    /// normal (el jefe cuando comparte casilla con un general), que hay que
    /// devolver al tablero después del combate.
    public static List<(string coord, Dictionary<string, object?> carta)> Resolver(
        ConfigDuelo cfg, EstadoDuelo e, int turno, int semilla,
        Dictionary<string, List<Dictionary<string, object?>>> t,
        PlanBombardeo? lluvia,
        Func<string, bool> transitable, int filas, int columnas)
    {
        e.Eventos.Clear();
        e.UltimoTurno = turno;
        var apartadas = new List<(string, Dictionary<string, object?>)>();
        var bloqueadas = Bloqueadas(cfg);
        bool Pasa(string c) => transitable(c) && !bloqueadas.Contains(c);
        var rng = new Random(PlanificadorBombardeo.Semilla(semilla, "duelo-resolver", turno));

        string? idJefe = e.IdDe(EstadoDuelo.RolJefe);
        string? idCaz = e.IdDe(EstadoDuelo.RolCazador);
        string? idVer = e.IdDe(EstadoDuelo.RolVerdugo);
        string Nombre(string? id) => id != null && e.Nombres.TryGetValue(id, out var n) && n != "" ? n : "Carta";
        string? Celda(string? id) => CeldaDe(t, id);
        // ¿Le perjudica al JUGADOR lo que le pasa a la carta de este rol?
        bool MaloPara(string? id) => id != null && e.Roles.TryGetValue(id, out var r)
                                     && (r == EstadoDuelo.RolJefe) == cfg.JugadorEsJefe;
        // Los generales vistos desde el jugador: "os" si son suyos, "los" si no.
        string os = cfg.JugadorEsJefe ? "los" : "os";

        void Herir(string? id, string motivo, string coord)
        {
            if (id == null || !e.Vidas.ContainsKey(id) || e.Vidas[id] <= 0) return;
            e.Vidas[id]--;
            e.Eventos.Add(new("herida", $"{motivo}: {Nombre(id)} pierde una vida (le quedan {e.Vidas[id]})", coord, MaloPara(id)));
        }

        // 1) LLUVIA DE ROCAS.
        if (cfg.LluviaManual)
        {
            // La lanzó el jugador (POST /warzero/duelo/lluvia): una roca en
            // cada casilla elegida (nunca sobre el jefe).
            if (e.LluviaDeclarada(turno))
            {
                var cj = Celda(idJefe);
                var impactos = e.LluviaJugadorCoords
                    .Where(c => cj == null || !string.Equals(c, cj, StringComparison.OrdinalIgnoreCase))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                e.LluviaUltimoTurno = turno;
                e.Eventos.Add(new("lluvia",
                    $"🪨 {Nombre(idJefe)} lanza la lluvia de rocas sobre {string.Join(", ", impactos.OrderBy(c => c, StringComparer.Ordinal))}", ""));
                foreach (var id in new[] { idCaz, idVer })
                {
                    var c = Celda(id);
                    if (c != null && impactos.Contains(c)) Herir(id, $"🪨 Una roca cae en {c}", c);
                }
            }
        }
        else if (lluvia != null && lluvia.Turno == turno)
        {
            var prohibidas = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var cj = Celda(idJefe);
            if (cj != null) prohibidas.Add(cj);
            var sorteo = PlanificadorBombardeo.Sortear(lluvia, semilla, prohibidas);
            var impactos = sorteo.Impactos.ToHashSet(StringComparer.OrdinalIgnoreCase);
            e.Eventos.Add(new("lluvia", $"🪨 Lluvia de rocas: {impactos.Count} impacto(s)", ""));
            foreach (var id in new[] { idCaz, idVer })
            {
                var c = Celda(id);
                if (c != null && impactos.Contains(c)) Herir(id, $"🪨 Una roca cae en {c}", c);
            }
        }

        // 2) ROMPE ESCUDOS.
        void RomperEscudo(string golpe, string zonaTxt)
        {
            e.SinEscudoHasta = turno + Math.Max(0, cfg.TurnosSinEscudo);
            e.Eventos.Add(new("rompe",
                $"🛡 Rompe escudos alcanza a {Nombre(idCaz)} en {zonaTxt}: sin escudo hasta el turno {e.SinEscudoHasta}",
                golpe, MaloPara(idCaz)));
        }
        if (e.RompeTurno == turno && e.RompeProb.Count > 0)
        {
            var celdas = e.RompeProb.Keys.OrderBy(c => c, StringComparer.Ordinal).ToList();
            var rr = new Random(PlanificadorBombardeo.Semilla(semilla, "duelo-rompe", turno));
            double x = rr.NextDouble(), acc = 0;
            string golpe = celdas[^1];
            foreach (var c in celdas) { acc += e.RompeProb[c]; if (x < acc) { golpe = c; break; } }
            var cc = Celda(idCaz);
            if (cc != null && string.Equals(cc, golpe, StringComparison.OrdinalIgnoreCase))
                RomperEscudo(golpe, golpe);
            else
                e.Eventos.Add(new("rompe", $"🛡 Rompe escudos golpea {golpe}: {Nombre(idCaz)} lo esquiva", golpe, !MaloPara(idCaz)));
        }
        e.LluviaJugadorTurno = 0;
        e.LluviaJugadorCoords.Clear();

        // 3) ENCUENTROS.
        var J = Celda(idJefe);
        var C = Celda(idCaz);
        var V = Celda(idVer);
        bool vivoC = idCaz != null && e.Vidas.GetValueOrDefault(idCaz) > 0;
        bool vivoV = idVer != null && e.Vidas.GetValueOrDefault(idVer) > 0;
        bool enJ(string? c) => J != null && c != null && string.Equals(c, J, StringComparison.OrdinalIgnoreCase);

        if (J != null && idJefe != null)
        {
            if (e.Paralizado(turno))
            {
                if (vivoV && enJ(V))
                {
                    e.Vidas[idJefe]--;
                    e.ParalizadoHasta = 0;
                    e.Eventos.Add(new("golpe", $"⚔ {Nombre(idVer)} hiere a {Nombre(idJefe)} (le quedan {e.Vidas[idJefe]})", J, MaloPara(idJefe)));
                    if (e.Vidas[idJefe] > 0)
                    {
                        var lejos = MasLejana(J, new[] { C, V }, t, Pasa, filas, columnas, rng);
                        if (lejos != null)
                        {
                            Mover(t, idJefe, lejos);
                            e.Eventos.Add(new("salto", $"💨 {Nombre(idJefe)} se libera y salta a {lejos}", lejos));
                        }
                        if (e.Vidas[idJefe] == 1)
                            e.Eventos.Add(new("furia", $"🔥 {Nombre(idJefe)} entra en FURIA: la lluvia será más densa", "", !MaloPara(idJefe)));
                    }
                }
            }
            else
            {
                bool cAqui = vivoC && enJ(C), vAqui = vivoV && enJ(V);
                if (cAqui && vAqui)
                {
                    // SEPARACIÓN: el cazador, lejos; el verdugo, a una contigua.
                    var lejos = MasLejana(J, new[] { J }, t, Pasa, filas, columnas, rng);
                    if (lejos != null) Mover(t, idCaz!, lejos);
                    var contigua = Contigua(J, t, Pasa, filas, columnas, rng);
                    if (contigua != null) Mover(t, idVer!, contigua);
                    e.Eventos.Add(new("separa",
                        $"💥 {Nombre(idJefe)} {os} separa: {Nombre(idCaz)} sale despedido a {lejos ?? C} y {Nombre(idVer)} a {contigua ?? V}", J));
                }
                else if (cAqui)
                {
                    if (!e.SinEscudo(turno))
                    {
                        e.ParalizadoHasta = turno + Math.Max(1, cfg.TurnosParalisis);
                        e.Eventos.Add(new("paraliza", cfg.JugadorEsJefe
                            ? $"🔗 {Nombre(idCaz)} paraliza a {Nombre(idJefe)} en {J}: el próximo turno no puede moverse y {Nombre(idVer)} intentará herirlo"
                            : $"🔗 {Nombre(idCaz)} paraliza a {Nombre(idJefe)} en {J}: ¡que entre {Nombre(idVer)}!", J, MaloPara(idJefe)));
                    }
                    else
                    {
                        Herir(idCaz, $"⚔ Sin escudo, {Nombre(idJefe)} golpea", J);
                        var contigua = Contigua(J, t, Pasa, filas, columnas, rng);
                        if (contigua != null) Mover(t, idCaz!, contigua);
                    }
                }
                else if (vAqui)
                {
                    Herir(idVer, $"⚔ {Nombre(idJefe)} está libre y golpea", J);
                    var contigua = Contigua(J, t, Pasa, filas, columnas, rng);
                    if (contigua != null) Mover(t, idVer!, contigua);
                }
            }
        }

        // 4) MUERTES.
        foreach (var (id, rol) in e.Roles.ToList())
        {
            if (e.Vidas.GetValueOrDefault(id) > 0) continue;
            var c = Celda(id);
            if (c == null) continue;
            Quitar(t, id);
            e.Eventos.Add(new("muerte", MaloPara(id)
                ? $"💀 {Nombre(id)} ha caído"
                : $"🏆 ¡{Nombre(id)} ha caído!", c, MaloPara(id)));
        }

        // 5) Nada de combate normal entre generales: si el jefe comparte casilla
        //    con alguien, se aparta y se devuelve tras el combate.
        var cjFinal = Celda(idJefe);
        if (cjFinal != null && t.TryGetValue(cjFinal, out var lstJ) && lstJ.Count > 1)
        {
            var carta = lstJ.First(c => M.Str(M.Get(c, "instanceId")) == idJefe);
            lstJ.Remove(carta);
            apartadas.Add((cjFinal, carta));
        }
        foreach (var k in t.Keys.Where(k => t[k].Count == 0).ToList()) t.Remove(k);
        return apartadas;
    }

    // ── Utilidades de tablero ────────────────────────────────────────────────
    public static HashSet<string> Bloqueadas(ConfigDuelo cfg) =>
        cfg.CeldasBloqueadas.Select(c => c.Trim().ToUpperInvariant()).ToHashSet(StringComparer.OrdinalIgnoreCase);

    public static string? CeldaDe(Dictionary<string, List<Dictionary<string, object?>>> t, string? instanceId)
    {
        if (string.IsNullOrEmpty(instanceId)) return null;
        foreach (var (coord, lst) in t)
            if (lst.Any(c => M.Str(M.Get(c, "instanceId")) == instanceId)) return coord;
        return null;
    }

    private static void Mover(Dictionary<string, List<Dictionary<string, object?>>> t, string instanceId, string destino)
    {
        var desde = CeldaDe(t, instanceId);
        if (desde == null || string.Equals(desde, destino, StringComparison.OrdinalIgnoreCase)) return;
        var carta = t[desde].First(c => M.Str(M.Get(c, "instanceId")) == instanceId);
        t[desde].Remove(carta);
        if (t[desde].Count == 0) t.Remove(desde);
        if (!t.TryGetValue(destino, out var lst)) t[destino] = lst = new();
        lst.Add(carta);
    }

    private static void Quitar(Dictionary<string, List<Dictionary<string, object?>>> t, string instanceId)
    {
        var desde = CeldaDe(t, instanceId);
        if (desde == null) return;
        t[desde].RemoveAll(c => M.Str(M.Get(c, "instanceId")) == instanceId);
        if (t[desde].Count == 0) t.Remove(desde);
    }

    /// Casilla libre (sin cartas) más alejada de las celdas [de], por camino.
    private static string? MasLejana(
        string origen, IEnumerable<string?> de, Dictionary<string, List<Dictionary<string, object?>>> t,
        Func<string, bool> pasa, int filas, int columnas, Random rng)
    {
        var refs = de.Where(x => x != null).Cast<string>().ToList();
        if (refs.Count == 0) refs.Add(origen);
        var candidatas = TodasLasCeldas(filas, columnas)
            .Where(c => pasa(c) && !t.ContainsKey(c) && !string.Equals(c, origen, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (candidatas.Count == 0) return null;
        return candidatas
            .Select(c => (c, d: refs.Min(r => Distancia(c, r, pasa, filas, columnas)), azar: rng.Next()))
            .Where(x => x.d < 999)
            .OrderByDescending(x => x.d).ThenBy(x => x.azar)
            .Select(x => x.c)
            .FirstOrDefault();
    }

    /// Casilla vecina libre de [origen] (al azar), o null.
    private static string? Contigua(
        string origen, Dictionary<string, List<Dictionary<string, object?>>> t,
        Func<string, bool> pasa, int filas, int columnas, Random rng)
    {
        var vecinas = Vecinos(origen, filas, columnas)
            .Where(c => pasa(c) && !t.ContainsKey(c))
            .OrderBy(_ => rng.Next())
            .ToList();
        return vecinas.FirstOrDefault();
    }

    /// Celdas alcanzables en ≤ [mov] pasos ortogonales (sin incluir el origen).
    public static HashSet<string> Alcance(string origen, int mov, Func<string, bool> pasa, int filas, int columnas)
    {
        var visto = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { [origen] = 0 };
        var cola = new Queue<string>();
        cola.Enqueue(origen);
        while (cola.Count > 0)
        {
            var c = cola.Dequeue();
            if (visto[c] >= mov) continue;
            foreach (var n in Vecinos(c, filas, columnas))
            {
                if (visto.ContainsKey(n) || !pasa(n)) continue;
                visto[n] = visto[c] + 1;
                cola.Enqueue(n);
            }
        }
        visto.Remove(origen);
        return visto.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// Pasos del camino más corto (999 si no hay).
    public static int Distancia(string a, string b, Func<string, bool> pasa, int filas, int columnas)
    {
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return 0;
        var visto = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { [a] = 0 };
        var cola = new Queue<string>();
        cola.Enqueue(a);
        while (cola.Count > 0)
        {
            var c = cola.Dequeue();
            foreach (var n in Vecinos(c, filas, columnas))
            {
                if (visto.ContainsKey(n)) continue;
                bool meta = string.Equals(n, b, StringComparison.OrdinalIgnoreCase);
                if (!meta && !pasa(n)) continue;
                visto[n] = visto[c] + 1;
                if (meta) return visto[n];
                cola.Enqueue(n);
            }
        }
        return 999;
    }

    private static IEnumerable<string> TodasLasCeldas(int filas, int columnas)
    {
        for (int r = 0; r < filas; r++)
            for (int c = 1; c <= columnas; c++)
                yield return $"{(char)('A' + r)}{c}";
    }

    private static IEnumerable<string> Vecinos(string coord, int filas, int columnas)
    {
        if (string.IsNullOrEmpty(coord) || coord.Length < 2) yield break;
        int r = char.ToUpperInvariant(coord[0]) - 'A';
        if (!int.TryParse(coord[1..], out int c)) yield break;
        if (r > 0) yield return $"{(char)('A' + r - 1)}{c}";
        if (r < filas - 1) yield return $"{(char)('A' + r + 1)}{c}";
        if (c > 1) yield return $"{(char)('A' + r)}{c - 1}";
        if (c < columnas) yield return $"{(char)('A' + r)}{c + 1}";
    }
}