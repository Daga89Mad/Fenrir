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
    double TorpezaJefe = 1.0)
{
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

    /// Propiedad (no campo) para leer siempre los ajustes actuales.
    public static IReadOnlyDictionary<string, ConfigDuelo> Todas =>
        new Dictionary<string, ConfigDuelo>
        {
            ["humanos_3"] = DueloAlexander,
        };

    public static ConfigDuelo? Get(string id) =>
        !string.IsNullOrEmpty(id) && Todas.TryGetValue(id, out var c) ? c : null;
}

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

    /// Último turno (inclusive) en que el jefe está paralizado. 0 = libre.
    public int ParalizadoHasta { get; set; }
    /// Último turno (inclusive) en que el cazador está sin escudo. 0 = con escudo.
    public int SinEscudoHasta { get; set; }

    /// Rompe escudos publicado para el turno `RompeTurno` (coord → 0..1).
    public int RompeTurno { get; set; }
    public Dictionary<string, double> RompeProb { get; } = new(StringComparer.OrdinalIgnoreCase);

    // Lo que pasó en la última resolución (aviso del cliente).
    public int UltimoTurno { get; set; }
    public List<(string tipo, string texto, string coord)> Eventos { get; } = new();

    public string? IdDe(string rol) => Roles.FirstOrDefault(kv => kv.Value == rol).Key;
    public int VidasDe(string rol) { var id = IdDe(rol); return id != null && Vidas.TryGetValue(id, out var v) ? v : 0; }

    public bool Paralizado(int turno) => turno <= ParalizadoHasta;
    public bool SinEscudo(int turno) => turno <= SinEscudoHasta;

    public Dictionary<string, object?> ACampo(ConfigDuelo cfg, int turnoSiguiente) => new()
    {
        ["roles"] = Roles.ToDictionary(kv => kv.Key, kv => (object?)kv.Value),
        ["vidas"] = Vidas.ToDictionary(kv => kv.Key, kv => (object?)(long)kv.Value),
        ["vidasMax"] = VidasMax.ToDictionary(kv => kv.Key, kv => (object?)(long)kv.Value),
        ["nombres"] = Nombres.ToDictionary(kv => kv.Key, kv => (object?)kv.Value),
        ["paralizadoHasta"] = (long)ParalizadoHasta,
        ["sinEscudoHasta"] = (long)SinEscudoHasta,
        ["rompe"] = new Dictionary<string, object?>
        {
            ["turno"] = (long)RompeTurno,
            ["prob"] = RompeProb.ToDictionary(
                kv => kv.Key, kv => (object?)(long)Math.Clamp(Math.Round(kv.Value * 100), 1, 100)),
            ["exacta"] = RompeProb.ToDictionary(kv => kv.Key, kv => (object?)kv.Value),
        },
        // Calendario (para la leyenda del cliente).
        ["turnoLimite"] = (long)cfg.TurnoLimite,
        ["lluviaEsteTurno"] = cfg.EsTurnoLluvia(turnoSiguiente),
        ["rompeEsteTurno"] = cfg.EsTurnoRompe(turnoSiguiente),
        ["canaliza"] = cfg.JefeCanalizaEnLluvia && cfg.EsTurnoLluvia(turnoSiguiente),
        ["turno"] = (long)turnoSiguiente,
        ["ultimo"] = new Dictionary<string, object?>
        {
            ["turno"] = (long)UltimoTurno,
            ["eventos"] = Eventos.Select(e => (object?)new Dictionary<string, object?>
            {
                ["tipo"] = e.tipo,
                ["texto"] = e.texto,
                ["coord"] = e.coord,
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
        e.ParalizadoHasta = M.Int(M.Get(m, "paralizadoHasta"));
        e.SinEscudoHasta = M.Int(M.Get(m, "sinEscudoHasta"));
        var rompe = M.Map(M.Get(m, "rompe"));
        e.RompeTurno = M.Int(M.Get(rompe, "turno"));
        foreach (var kv in M.Map(M.Get(rompe, "exacta"))) e.RompeProb[kv.Key] = M.Dbl(kv.Value);
        return e;
    }
}

/// Lógica del duelo (sin Firestore).
public static class MotorDuelo
{
    /// Estado inicial: localiza en el tablero sembrado al jefe, al cazador y
    /// al verdugo (por id de catálogo y dueño) y les da sus vidas.
    public static EstadoDuelo Inicial(
        ConfigDuelo cfg, Dictionary<string, List<Dictionary<string, object?>>> tablero,
        string jugadorUid, string botUid)
    {
        var e = new EstadoDuelo();
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
        Registrar(cfg.JefeId, botUid, EstadoDuelo.RolJefe, cfg.VidasJefe);
        Registrar(cfg.CazadorId, jugadorUid, EstadoDuelo.RolCazador, cfg.VidasCazador);
        Registrar(cfg.VerdugoId, jugadorUid, EstadoDuelo.RolVerdugo, cfg.VidasVerdugo);
        return e;
    }

    // ── Planes publicados al EMPEZAR un turno ────────────────────────────────

    /// Plan de la LLUVIA de [turno] (null si ese turno no llueve). Nunca cae
    /// sobre el jefe ni sobre casillas bloqueadas o intransitables.
    public static PlanBombardeo? PlanLluvia(
        ConfigDuelo cfg, EstadoDuelo e, int turno, int semilla,
        Dictionary<string, List<Dictionary<string, object?>>> tablero,
        string jugadorUid, Func<string, bool> transitable, int filas, int columnas)
    {
        if (!cfg.EsTurnoLluvia(turno)) return null;
        var jefe = CeldaDe(tablero, e.IdDe(EstadoDuelo.RolJefe));
        bool furia = e.VidasDe(EstadoDuelo.RolJefe) == 1;
        var bloqueadas = Bloqueadas(cfg);

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
            .Where(kv => kv.Value.Any(c => M.Str(M.Get(c, "ownerUid")) == jugadorUid))
            .ToDictionary(kv => kv.Key, kv => kv.Value.Count(c => M.Str(M.Get(c, "ownerUid")) == jugadorUid),
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

        void Herir(string? id, string motivo, string coord)
        {
            if (id == null || !e.Vidas.ContainsKey(id) || e.Vidas[id] <= 0) return;
            e.Vidas[id]--;
            e.Eventos.Add(("herida", $"{motivo}: {Nombre(id)} pierde una vida (le quedan {e.Vidas[id]})", coord));
        }

        // 1) LLUVIA DE ROCAS.
        if (lluvia != null && lluvia.Turno == turno)
        {
            var prohibidas = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var cj = Celda(idJefe);
            if (cj != null) prohibidas.Add(cj);
            var sorteo = PlanificadorBombardeo.Sortear(lluvia, semilla, prohibidas);
            var impactos = sorteo.Impactos.ToHashSet(StringComparer.OrdinalIgnoreCase);
            e.Eventos.Add(("lluvia", $"🪨 Lluvia de rocas: {impactos.Count} impacto(s)", ""));
            foreach (var id in new[] { idCaz, idVer })
            {
                var c = Celda(id);
                if (c != null && impactos.Contains(c)) Herir(id, $"🪨 Una roca cae en {c}", c);
            }
        }

        // 2) ROMPE ESCUDOS.
        if (e.RompeTurno == turno && e.RompeProb.Count > 0)
        {
            var celdas = e.RompeProb.Keys.OrderBy(c => c, StringComparer.Ordinal).ToList();
            var rr = new Random(PlanificadorBombardeo.Semilla(semilla, "duelo-rompe", turno));
            double x = rr.NextDouble(), acc = 0;
            string golpe = celdas[^1];
            foreach (var c in celdas) { acc += e.RompeProb[c]; if (x < acc) { golpe = c; break; } }
            var cc = Celda(idCaz);
            if (cc != null && string.Equals(cc, golpe, StringComparison.OrdinalIgnoreCase))
            {
                e.SinEscudoHasta = turno + Math.Max(0, cfg.TurnosSinEscudo);
                e.Eventos.Add(("rompe", $"🛡 Rompe escudos alcanza a {Nombre(idCaz)} en {golpe}: sin escudo hasta el turno {e.SinEscudoHasta}", golpe));
            }
            else
                e.Eventos.Add(("rompe", $"🛡 Rompe escudos golpea {golpe}: {Nombre(idCaz)} lo esquiva", golpe));
        }

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
                    e.Eventos.Add(("golpe", $"⚔ {Nombre(idVer)} hiere a {Nombre(idJefe)} (le quedan {e.Vidas[idJefe]})", J));
                    if (e.Vidas[idJefe] > 0)
                    {
                        var lejos = MasLejana(J, new[] { C, V }, t, Pasa, filas, columnas, rng);
                        if (lejos != null)
                        {
                            Mover(t, idJefe, lejos);
                            e.Eventos.Add(("salto", $"💨 {Nombre(idJefe)} se libera y salta a {lejos}", lejos));
                        }
                        if (e.Vidas[idJefe] == 1)
                            e.Eventos.Add(("furia", $"🔥 {Nombre(idJefe)} entra en FURIA: la lluvia será más densa", ""));
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
                    e.Eventos.Add(("separa",
                        $"💥 {Nombre(idJefe)} os separa: {Nombre(idCaz)} sale despedido a {lejos ?? C} y {Nombre(idVer)} a {contigua ?? V}", J));
                }
                else if (cAqui)
                {
                    if (!e.SinEscudo(turno))
                    {
                        e.ParalizadoHasta = turno + Math.Max(1, cfg.TurnosParalisis);
                        e.Eventos.Add(("paraliza", $"🔗 {Nombre(idCaz)} paraliza a {Nombre(idJefe)} en {J}: ¡que entre {Nombre(idVer)}!", J));
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
            e.Eventos.Add(("muerte", rol == EstadoDuelo.RolJefe
                ? $"🏆 ¡{Nombre(id)} ha caído!"
                : $"💀 {Nombre(id)} ha caído", c));
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