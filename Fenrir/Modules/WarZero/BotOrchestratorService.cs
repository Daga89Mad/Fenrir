using Google.Cloud.Firestore;

// ─────────────────────────────────────────────────────────────────────────────
// BotOrchestratorService.cs
//
// BackgroundService que rellena salas con bots. En cada barrido:
//   0. RECUPERA partidas ya EN CURSO cuyos bots participantes se quedaron sin
//      runner (p. ej. tras un reinicio del contenedor en Render). Sin esto, una
//      partida en curso queda huérfana: sus turnos no vuelven a cerrarse jamás.
//   1. Lee los bots ACTIVOS de la colección `Bots` (activo == true), por `orden`.
//   2. Descarta los que ya están ocupados en una partida.
//   3. Busca las PARTIDAS PÚBLICAS en espera, MÁS ANTIGUAS primero (creadoEn asc).
//   4. Reparte: llena la sala más vieja (según sus huecos libres) y, si sobran
//      bots, desborda a la siguiente. Por cada asignación lanza un WarZeroBot que
//      juega esa partida de principio a fin.
//
// POR QUÉ HACE FALTA LA RECUPERACIÓN (paso 0):
// Cada WarZeroBot corre como un Task.Run en memoria de ESTE proceso. Si el
// contenedor se reinicia (spin-down, redeploy, OOM), todos esos runners mueren
// y `_ocupados` se vacía, pero las partidas siguen `en_curso` en Firestore. El
// orquestador antiguo solo miraba salas `esperando`, así que nunca retomaba esas
// partidas. Ahora, al arrancar, detecta cada partida en curso con un bot activo
// que ya es participante y le lanza un runner en modo REANUDAR.
//
// El panel Flutter (EdicionBotsScreen) es quien pone/quita `activo`. Desactivar
// un bot solo evita que se le asignen salas NUEVAS y que se le reanuden partidas:
// las partidas ya en curso las termina el runner que esté vivo.
//
// La colección `Bots` la siembra el panel (bot_0…bot_6). Aquí solo se lee.
// ─────────────────────────────────────────────────────────────────────────────

public class BotOrchestratorOptions
{
    /// Cada cuánto se re-escanean salas y bots.
    public TimeSpan ScanInterval { get; set; } = TimeSpan.FromSeconds(15);

    /// Tope de salas públicas a considerar por barrido.
    public int MaxSalasPorBarrido { get; set; } = 100;

    /// Tope de partidas EN CURSO a inspeccionar por barrido para recuperación.
    public int MaxPartidasEnCurso { get; set; } = 200;
}

public class BotOrchestratorService : BackgroundService
{
    private readonly WarZeroFirestore _fs;
    private readonly WarZeroService _svc;
    private readonly BotOrchestratorOptions _opt;
    private readonly WarZeroBotOptions _botOpt;
    private readonly ILogger<BotOrchestratorService> _log;

    // Bots ocupados AHORA en una partida: uid -> lobbyId. Evita reasignar un bot
    // que ya está jugando y sirve para descontar huecos en el reparto.
    private readonly Dictionary<string, string> _ocupados = new();
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

    // ── Un barrido: recuperar en curso → repartir bots ociosos ─────────────────
    private async Task BarridoAsync(CancellationToken ct)
    {
        // 1) Bots activos, por orden.
        var activos = await LeerBotsActivosAsync(ct);
        if (activos.Count == 0) return;

        // 0) RECUPERACIÓN: retomar partidas EN CURSO cuyos bots se quedaron sin
        //    runner (tras un reinicio del proceso). Marca esos bots como ocupados.
        await RecuperarPartidasEnCursoAsync(activos, ct);

        // 2) Solo los ociosos (no ocupados en otra partida ni recién reanudados).
        List<BotDef> ociosos;
        lock (_lock)
            ociosos = activos.Where(b => !_ocupados.ContainsKey(b.Uid)).ToList();
        if (ociosos.Count == 0) return;

        // 3) Salas públicas en espera, más antiguas primero.
        var salas = await LeerSalasPublicasAsync(ct);
        if (salas.Count == 0) return;

        // 4) Reparto: llenar la sala más vieja; desbordar a la siguiente.
        int idx = 0;
        foreach (var sala in salas)
        {
            if (idx >= ociosos.Count) break;

            int libres = Math.Max(0, sala.Max - sala.Ocupadas);
            for (int k = 0; k < libres && idx < ociosos.Count; k++)
            {
                var bot = ociosos[idx++];
                Lanzar(bot, sala.Id, reanudar: false, ct);
            }
        }
    }

    // ── Recuperación de partidas en curso ──────────────────────────────────────
    // Para cada partida `en_curso`, cualquier bot ACTIVO que sea participante,
    // no esté eliminado y no tenga runner vivo (no está en _ocupados) recibe un
    // runner en modo REANUDAR. Es idempotente: si ya está corriendo, no hace nada.
    private async Task RecuperarPartidasEnCursoAsync(List<BotDef> activos, CancellationToken ct)
    {
        var porUid = new Dictionary<string, BotDef>();
        foreach (var b in activos) porUid[b.Uid] = b;

        var enCurso = await LeerPartidasEnCursoAsync(ct);
        foreach (var p in enCurso)
        {
            foreach (var uid in p.Participantes)
            {
                if (!porUid.TryGetValue(uid, out var bot)) continue; // no es bot activo
                if (p.Eliminados.Contains(uid)) continue;            // ya eliminado

                bool yaCorriendo;
                lock (_lock) yaCorriendo = _ocupados.ContainsKey(uid);
                if (yaCorriendo) continue;                           // runner vivo

                _log.LogInformation(
                    "[WZ][orquestador] recuperando {alias} en partida en curso {lobby}",
                    bot.Alias, p.Id);
                Lanzar(bot, p.Id, reanudar: true, ct);
            }
        }
    }

    // ── Lanzar un bot en una sala (tarea de fondo) ─────────────────────────────
    // reanudar == true  → partida ya en curso; el runner salta unirse/arranque.
    // reanudar == false → sala en espera; flujo normal (unirse → arrancar → jugar).
    private void Lanzar(BotDef bot, string lobbyId, bool reanudar, CancellationToken ct)
    {
        lock (_lock)
        {
            if (_ocupados.ContainsKey(bot.Uid)) return; // carrera: ya asignado
            _ocupados[bot.Uid] = lobbyId;
        }

        if (!reanudar)
            _log.LogInformation("[WZ][orquestador] {alias} → sala {lobby}",
                bot.Alias, lobbyId);

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
                lock (_lock) _ocupados.Remove(bot.Uid);
            }
        }, ct);
    }

    // ── Lecturas de Firestore ──────────────────────────────────────────────────

    /// Bots con activo == true, ordenados por `orden` (menor entra antes).
    private async Task<List<BotDef>> LeerBotsActivosAsync(CancellationToken ct)
    {
        var db = _fs.Db;
        var snap = await db.Collection("Bots")
            .WhereEqualTo("activo", true)
            .GetSnapshotAsync(ct);

        var list = new List<BotDef>();
        foreach (var doc in snap.Documents)
        {
            var data = M.Map(M.FromFs(doc.ToDictionary()));
            list.Add(new BotDef(
                Uid: doc.Id,
                Alias: M.Str(M.Get(data, "alias")) is var a && a != "" ? a : doc.Id,
                Orden: M.Int(M.Get(data, "orden"))));
        }
        list.Sort((x, y) => x.Orden.CompareTo(y.Orden));
        return list;
    }

    /// Partidas EN CURSO (para recuperación). Lee `participantes` y
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

            var participantes = M.List(M.Get(data, "participantes"))
                .Select(M.Str).Where(s => s != "").ToHashSet();
            if (participantes.Count == 0) continue;

            var eliminados = M.List(M.Get(data, "jugadoresEliminados"))
                .Select(M.Str).Where(s => s != "").ToHashSet();

            res.Add(new PartidaEnCurso(doc.Id, participantes, eliminados));
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
    private record BotDef(string Uid, string Alias, int Orden);
    private record SalaDef(string Id, int Max, int Ocupadas, DateTime Creado);
    private record PartidaEnCurso(string Id, HashSet<string> Participantes, HashSet<string> Eliminados);
}