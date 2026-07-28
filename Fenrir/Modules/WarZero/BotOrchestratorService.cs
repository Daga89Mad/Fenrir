using Google.Cloud.Firestore;

// ─────────────────────────────────────────────────────────────────────────────
// BotOrchestratorService.cs
//
// BackgroundService que rellena salas con bots. En cada barrido:
//   0. RECUPERA partidas ya EN CURSO cuyos bots participantes se quedaron sin
//      runner (p. ej. tras un reinicio del contenedor en Render). Sin esto, una
//      partida en curso queda huérfana: sus turnos no vuelven a cerrarse jamás.
//   1. Lee los bots ACTIVOS de la colección `Bots` (activo == true), por `orden`.
//      Cada bot puede jugar hasta `maxPartidas` partidas SIMULTÁNEAS.
//   2. Busca las PARTIDAS PÚBLICAS en espera, MÁS ANTIGUAS primero (creadoEn asc).
//   3. Reparte: llena la sala más vieja con bots que aún tengan CAPACIDAD (menos
//      partidas activas que su `maxPartidas`) y que no estén ya en esa sala. Por
//      cada asignación lanza un WarZeroBot que juega esa partida de principio a
//      fin.
//
// SIMULTANEIDAD (mejora #1):
//   `_ocupados` ya no es uid -> una sala, sino uid -> CONJUNTO de salas. Un bot
//   está disponible para una sala nueva si nº de salas activas < maxPartidas y no
//   está ya dentro de esa sala. Cada runner corre como un Task.Run independiente.
//
// RECUPERACIÓN (paso 0):
//   Cada runner vive en memoria de ESTE proceso. Si el contenedor se reinicia,
//   los runners mueren y `_ocupados` se vacía, pero las partidas siguen
//   `en_curso` en Firestore. Al arrancar, el orquestador detecta cada partida en
//   curso con un bot activo que ya es participante y le lanza un runner en modo
//   REANUDAR (sin límite de capacidad: esas partidas YA existen).
//
// El panel Flutter (EdicionBotsScreen) es quien siembra los bots (bot_0…bot_N) y
// pone/quita `activo`, `orden`, `alias` y `maxPartidas`. Aquí solo se lee.
// ─────────────────────────────────────────────────────────────────────────────

public class BotOrchestratorOptions
{
    /// Cada cuánto se re-escanean salas y bots.
    public TimeSpan ScanInterval { get; set; } = TimeSpan.FromSeconds(15);

    /// Tope de salas públicas a considerar por barrido.
    public int MaxSalasPorBarrido { get; set; } = 100;

    /// Tope de partidas EN CURSO a inspeccionar por barrido para recuperación.
    public int MaxPartidasEnCurso { get; set; } = 300;

    /// Valor por defecto de partidas simultáneas si un bot no define `maxPartidas`.
    public int MaxPartidasPorBotDefecto { get; set; } = 1;
}

public class BotOrchestratorService : BackgroundService
{
    private readonly WarZeroFirestore _fs;
    private readonly WarZeroService _svc;
    private readonly BotOrchestratorOptions _opt;
    private readonly WarZeroBotOptions _botOpt;
    private readonly ILogger<BotOrchestratorService> _log;

    // Bots ocupados AHORA: uid -> conjunto de lobbyIds que está jugando. Permite
    // partidas simultáneas y evita reasignar un bot a una sala en la que ya está.
    private readonly Dictionary<string, HashSet<string>> _ocupados = new();
    private readonly object _lock = new();

    public BotOrchestratorService(
        WarZeroFirestore fs,
        WarZeroService svc,
        ILogger<BotOrchestratorService> log,
        BotOrchestratorOptions? options = null,
        WarZeroBotOptions? botOptions = null)
    {
        _fs = fs;
        _svc = svc;
        _log = log;
        _opt = options ?? new BotOrchestratorOptions();
        _botOpt = botOptions ?? new WarZeroBotOptions();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation("[WZ][orquestador] iniciado (cada {s}s)",
            _opt.ScanInterval.TotalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await BarridoAsync(stoppingToken);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _log.LogError(ex, "[WZ][orquestador] barrido falló");
            }

            try { await Task.Delay(_opt.ScanInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }

        _log.LogInformation("[WZ][orquestador] detenido");
    }

    // ── Un barrido: recuperar en curso → repartir bots con capacidad ───────────
    private async Task BarridoAsync(CancellationToken ct)
    {
        var activos = await LeerBotsActivosAsync(ct);   // para rellenar salas nuevas
        var todos = await LeerTodosLosBotsAsync(ct);    // para recuperar (ignora activo)
        var enCurso = await LeerPartidasEnCursoAsync(ct);

        // HEARTBEAT: prueba inequívoca de que este build ejecuta la recuperación.
        _log.LogInformation(
            "[WZ][orquestador] barrido: {act} activos, {tot} bots totales [{uids}], {c} partidas en curso",
            activos.Count, todos.Count,
            string.Join(",", todos.Select(x => x.Uid)),
            enCurso.Count);

        // 0) RECUPERACIÓN SIEMPRE (aunque no haya bots activos): un bot atascado en
        //    una partida debe seguir cerrando sus turnos hasta que termine.
        RecuperarPartidasEnCurso(todos, enCurso, ct);

        if (activos.Count == 0)
        {
            _log.LogInformation("[WZ][orquestador] sin bots activos: no se rellenan salas nuevas " +
                                "(la recuperación de partidas en curso sí se ejecuta)");
            return;
        }

        // ¿Queda algún bot activo con capacidad libre?
        bool hayCapacidad = activos.Any(b => Cuenta(b.Uid) < b.MaxPartidas);
        if (!hayCapacidad) return;

        // 2) Salas públicas en espera, más antiguas primero.
        var salas = await LeerSalasPublicasAsync(ct);
        if (salas.Count == 0) return;

        // 3) Reparto: llenar la sala más vieja con bots que tengan capacidad y no
        //    estén ya en ella; desbordar a la siguiente.
        foreach (var sala in salas)
        {
            int libres = Math.Max(0, sala.Max - sala.Ocupadas);
            for (int k = 0; k < libres; k++)
            {
                // Siguiente bot (por orden) con capacidad y que no esté ya en esta sala.
                BotDef? elegido = null;
                foreach (var b in activos)
                {
                    if (EstaEn(b.Uid, sala.Id)) continue;
                    if (Cuenta(b.Uid) >= b.MaxPartidas) continue;
                    elegido = b;
                    break;
                }
                if (elegido == null) break; // nadie disponible para esta sala
                Lanzar(elegido, sala.Id, reanudar: false, ct);
            }
        }
    }

    // ── Recuperación de partidas en curso ──────────────────────────────────────
    // Para cada partida `en_curso`, cualquier bot que sea jugador, no esté
    // eliminado y no tenga runner vivo para ESA sala recibe un runner en modo
    // REANUDAR — esté marcado activo o no. Es idempotente: si ya está corriendo
    // esa sala, no hace nada. No aplica límite de capacidad (la partida ya existe).
    private void RecuperarPartidasEnCurso(List<BotDef> bots, List<PartidaEnCurso> enCurso, CancellationToken ct)
    {
        var porUid = new Dictionary<string, BotDef>();
        foreach (var b in bots) porUid[b.Uid] = b;

        foreach (var p in enCurso)
        {
            // Bots (de la colección Bots) que son jugadores de esta partida.
            var botsEnPartida = p.Jugadores.Where(porUid.ContainsKey).ToList();
            if (botsEnPartida.Count == 0)
            {
                _log.LogInformation(
                    "[WZ][orquestador] partida {lobby}: jugadores=[{jug}] — ningún bot entre ellos",
                    p.Id, string.Join(",", p.Jugadores));
                continue;
            }

            foreach (var uid in botsEnPartida)
            {
                var bot = porUid[uid];
                if (p.Eliminados.Contains(uid))
                {
                    _log.LogInformation("[WZ][orquestador] {alias} en {lobby}: eliminado, no se recupera",
                        bot.Alias, p.Id);
                    continue;
                }
                if (EstaEn(uid, p.Id))
                {
                    _log.LogInformation("[WZ][orquestador] {alias} en {lobby}: runner ya vivo",
                        bot.Alias, p.Id);
                    continue;
                }

                _log.LogInformation(
                    "[WZ][orquestador] RECUPERANDO {alias} ({uid}) en partida en curso {lobby}",
                    bot.Alias, uid, p.Id);
                Lanzar(bot, p.Id, reanudar: true, ct);
            }
        }
    }

    // ── Lanzar un bot en una sala (tarea de fondo) ─────────────────────────────
    // reanudar == true  → partida ya en curso; el runner salta unirse/arranque y
    //                     no se aplica el tope de capacidad (la partida ya existe).
    // reanudar == false → sala en espera; flujo normal (unirse → arrancar → jugar)
    //                     respetando maxPartidas del bot.
    private void Lanzar(BotDef bot, string lobbyId, bool reanudar, CancellationToken ct)
    {
        lock (_lock)
        {
            if (!_ocupados.TryGetValue(bot.Uid, out var set)) { set = new(); _ocupados[bot.Uid] = set; }
            if (set.Contains(lobbyId)) return;                             // ya en esa sala
            if (!reanudar && set.Count >= bot.MaxPartidas) return;         // sin capacidad
            set.Add(lobbyId);
        }

        if (!reanudar)
            _log.LogInformation("[WZ][orquestador] {alias} → sala {lobby} ({n}/{max})",
                bot.Alias, lobbyId, Cuenta(bot.Uid), bot.MaxPartidas);

        _ = Task.Run(async () =>
        {
            try
            {
                var runner = new WarZeroBot(_fs, _svc, _botOpt);
                if (reanudar)
                    await runner.ResumeForLobbyAsync(lobbyId, bot.Uid, bot.Alias, ct);
                else
                    await runner.RunForLobbyAsync(lobbyId, bot.Uid, bot.Alias, ct);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "[WZ][orquestador] runner de {alias} falló", bot.Alias);
            }
            finally
            {
                lock (_lock)
                {
                    if (_ocupados.TryGetValue(bot.Uid, out var set))
                    {
                        set.Remove(lobbyId);
                        if (set.Count == 0) _ocupados.Remove(bot.Uid);
                    }
                }
            }
        }, ct);
    }

    // ── Estado de ocupación (bajo lock) ────────────────────────────────────────
    private int Cuenta(string uid)
    {
        lock (_lock) return _ocupados.TryGetValue(uid, out var s) ? s.Count : 0;
    }

    private bool EstaEn(string uid, string lobbyId)
    {
        lock (_lock) return _ocupados.TryGetValue(uid, out var s) && s.Contains(lobbyId);
    }

    // ── Lecturas de Firestore ──────────────────────────────────────────────────

    private BotDef ParseBot(DocumentSnapshot doc)
    {
        var data = M.Map(M.FromFs(doc.ToDictionary()));
        int max = M.Int(M.Get(data, "maxPartidas", "partidasSimultaneas"));
        if (max <= 0) max = _opt.MaxPartidasPorBotDefecto;
        return new BotDef(
            Uid: doc.Id,
            Alias: M.Str(M.Get(data, "alias")) is var a && a != "" ? a : doc.Id,
            Orden: M.Int(M.Get(data, "orden")),
            MaxPartidas: Math.Max(1, max));
    }

    /// Bots con activo == true, ordenados por `orden` (menor entra antes).
    /// Se usan para RELLENAR salas nuevas.
    private async Task<List<BotDef>> LeerBotsActivosAsync(CancellationToken ct)
    {
        var snap = await _fs.Db.Collection("Bots")
            .WhereEqualTo("activo", true)
            .GetSnapshotAsync(ct);
        var list = snap.Documents.Select(ParseBot).ToList();
        list.Sort((x, y) => x.Orden.CompareTo(y.Orden));
        return list;
    }

    /// TODOS los bots (ignora `activo`). Se usan para RECUPERAR partidas ya en
    /// curso: un bot atascado en una partida debe seguir cerrando sus turnos
    /// aunque esté marcado inactivo (inactivo = "no entres a salas nuevas", no
    /// "abandona las partidas en las que ya estás").
    private async Task<List<BotDef>> LeerTodosLosBotsAsync(CancellationToken ct)
    {
        var snap = await _fs.Db.Collection("Bots").GetSnapshotAsync(ct);
        return snap.Documents.Select(ParseBot).ToList();
    }

    /// Partidas EN CURSO (para recuperación). Lee `jugadores` y
    /// `jugadoresEliminados`. Se filtra por estado en la query; el resto en memoria.
    private async Task<List<PartidaEnCurso>> LeerPartidasEnCursoAsync(CancellationToken ct)
    {
        var db = _fs.Db;
        var snap = await db.Collection("Partidas")
            .WhereEqualTo("estado", "en_curso")
            .Limit(_opt.MaxPartidasEnCurso)
            .GetSnapshotAsync(ct);

        var res = new List<PartidaEnCurso>();
        foreach (var doc in snap.Documents)
        {
            var data = M.Map(M.FromFs(doc.ToDictionary()));

            // OJO: `jugadores` es una lista de MAPAS, cada uno con un campo `uid`
            // (igual que lo lee WarZeroService.CerrarTurnoAsync). NO son strings.
            var jugadores = M.List(M.Get(data, "jugadores"))
                .Select(j => M.Str(M.Get(M.Map(j), "uid")))
                .Where(u => u != "").ToHashSet();
            if (jugadores.Count == 0) continue;

            // `jugadoresEliminados` sí es una lista de uids (strings).
            var eliminados = M.List(M.Get(data, "jugadoresEliminados"))
                .Select(M.Str).Where(s => s != "").ToHashSet();

            res.Add(new PartidaEnCurso(doc.Id, jugadores, eliminados));
        }
        return res;
    }

    /// Salas públicas en espera con huecos, más antiguas primero. Se filtra
    /// `esPrivada` en memoria y se ordena por `creadoEn` para no exigir un índice
    /// compuesto en Firestore (mismo criterio que WarZeroService.PublicasAsync).
    private async Task<List<SalaDef>> LeerSalasPublicasAsync(CancellationToken ct)
    {
        var db = _fs.Db;
        var snap = await db.Collection("Partidas")
            .WhereEqualTo("estado", "esperando")
            .Limit(_opt.MaxSalasPorBarrido)
            .GetSnapshotAsync(ct);

        var salas = new List<SalaDef>();
        foreach (var doc in snap.Documents)
        {
            var data = M.Map(M.FromFs(doc.ToDictionary()));
            if (M.Bool(M.Get(data, "esPrivada"))) continue;

            int max = M.Int(M.Get(data, "maxJugadores"));
            int ocupadas = M.List(M.Get(data, "jugadores")).Count;
            if (max > 0 && ocupadas >= max) continue; // ya está llena

            // creadoEn como Timestamp para ordenar de forma fiable.
            DateTime creado = DateTime.UtcNow;
            if (doc.TryGetValue<Timestamp>("creadoEn", out var ts))
                creado = ts.ToDateTime();

            salas.Add(new SalaDef(Id: doc.Id, Max: max, Ocupadas: ocupadas, Creado: creado));
        }

        salas.Sort((x, y) => x.Creado.CompareTo(y.Creado)); // más antigua primero
        return salas;
    }

    // ── Tipos internos ─────────────────────────────────────────────────────────
    private record BotDef(string Uid, string Alias, int Orden, int MaxPartidas);
    private record SalaDef(string Id, int Max, int Ocupadas, DateTime Creado);
    private record PartidaEnCurso(string Id, HashSet<string> Jugadores, HashSet<string> Eliminados);
}