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
    /// Cada cuánto se re-escanean salas y bots (reparto proactivo de bots a
    /// salas en espera). Antes 15 s; subido a 30 s para reducir a la mitad las
    /// lecturas de barrido sin afectar de forma perceptible al llenado de salas.
    public TimeSpan ScanInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// Cada cuántos barridos se ejecutan las tareas CARAS que leen todas las
    /// partidas EN CURSO (recuperación de runners y cierre por hora límite). No
    /// hacen falta en cada barrido: recuperar un bot caído o resolver un turno
    /// diario/12h con ~2 min de margen es irrelevante para el juego, pero leer
    /// hasta 300 partidas cada 30 s sí dispara el consumo. Con 4 → cada ~2 min.
    public int BarridosPorRecuperacion { get; set; } = 4;

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

    // Último valor de `partidasActivas` escrito en Firestore por bot, para no
    // reescribir en cada barrido cuando no ha cambiado nada.
    private readonly Dictionary<string, int> _publicado = new();

    // Contador de barridos, para espaciar las tareas caras (recuperación de
    // partidas en curso y cierre por hora límite) según BarridosPorRecuperacion.
    private long _numBarrido = 0;

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
        // ¿Toca ejecutar en ESTE barrido las tareas caras (leer todas las
        // partidas en curso)? El primer barrido (0) siempre las hace.
        var tareasCaras = (_numBarrido % Math.Max(1, _opt.BarridosPorRecuperacion)) == 0;
        _numBarrido++;

        // Leer las salas EN ESPERA UNA sola vez (antes se leían dos veces: una
        // para rellenar con bots y otra para auto-iniciar las llenas). Incluye
        // tanto las que tienen hueco como las llenas.
        var salas = await LeerSalasEnEsperaAsync(ct);
        var salasConHueco = salas.Where(s => s.Max <= 0 || s.Ocupadas < s.Max).ToList();
        var salasLlenas = salas
            .Where(s => s.Max > 0 && s.Ocupadas >= s.Max && s.TodosListos).ToList();

        // Leer la colección `Bots` SOLO si hace falta: hay salas con hueco que
        // rellenar, o toca recuperación. En periodos idle (sin salas y sin
        // recuperación) NO se lee `Bots` en absoluto → el orquestador deja de
        // gastar lecturas de Firestore en vacío, que era el mayor drenaje 24/7
        // (antes leía toda la colección Bots DOS veces en CADA barrido, jugaran
        // o no los bots). Además, los activos se derivan en memoria del mismo
        // resultado en vez de con una segunda consulta.
        var necesitaBots = salasConHueco.Count > 0 || tareasCaras;
        var todos = necesitaBots
            ? await LeerTodosLosBotsAsync(ct)
            : new List<BotDef>();
        var activos = todos.Where(b => b.Activo).OrderBy(b => b.Orden).ToList();

        try
        {
            // RECUPERACIÓN (cara): leer las partidas en curso y re-enganchar bots.
            if (tareasCaras)
            {
                var enCurso = await LeerPartidasEnCursoAsync(ct);
                _log.LogInformation(
                    "[WZ][orquestador] barrido(recup): {act} activos, {tot} bots, {c} en curso, {s} salas espera",
                    activos.Count, todos.Count, enCurso.Count, salas.Count);
                RecuperarPartidasEnCurso(todos, enCurso, ct);
            }

            // Repartir bots a las salas con hueco (sin lecturas: ya las tenemos).
            RellenarSalas(salasConHueco, todos, activos, ct);

            // Auto-iniciar las salas llenas y listas (reutiliza `salasLlenas`, ya
            // leídas: no vuelve a consultar `esperando`).
            await AutoIniciarSalasLlenasAsync(salasLlenas, ct);

            // Cerrar turnos vencidos (diario / turno12h): tarea cara, solo cada
            // BarridosPorRecuperacion.
            if (tareasCaras) await ResolverDeadlinesAsync(ct);
        }
        finally
        {
            // Publicar la ocupación solo si en este barrido leímos bots. En los
            // barridos idle no hay bots que leer (ni cambios reales de ocupación),
            // así que evitamos incluso ese bucle. `partidasActivas` es un dato de
            // panel; su refresco puede esperar al siguiente barrido con bots.
            if (todos.Count > 0) await PublicarOcupacionAsync(todos, ct);
        }
    }

    // Reparte bots a las salas con hueco. Ya NO lee Firestore: recibe las salas y
    // los pools de bots ya leídos por el barrido.
    private void RellenarSalas(
        List<SalaDef> salasConHueco, List<BotDef> todos, List<BotDef> activos,
        CancellationToken ct)
    {
        foreach (var sala in salasConHueco)
        {
            // Relleno PROACTIVO (sala en espera normal): NO entra en salas
            // privadas. Solo el relleno FORZADO (host pulsó Iniciar en su sala,
            // marca rellenarBots) puede meter bots en una privada.
            if (!sala.Forzada && sala.EsPrivada) continue;

            // Pool de bots según el flujo:
            //  • Forzada: CUALQUIER bot (incluidos los desactivados) para poder
            //    arrancar la partida aunque no haya bots activos disponibles.
            //  • Proactiva: solo bots ACTIVOS, que se meten automáticamente en
            //    cuanto hay una sala con huecos.
            var pool = sala.Forzada ? todos : activos;
            if (pool.Count == 0) continue;

            // Tiempo que lleva la sala esperando (desde su creación). En el relleno
            // proactivo cada bot solo entra si la sala lleva esperando al menos su
            // `esperaSegundos` configurado (para dar margen a jugadores humanos).
            var esperaSala = DateTime.UtcNow - sala.Creado;

            int libres = Math.Max(0, sala.Max - sala.Ocupadas);
            for (int k = 0; k < libres; k++)
            {
                // Siguiente bot (por orden) con capacidad, que no esté ya en esta
                // sala y —si es proactivo— cuya espera ya se haya cumplido.
                BotDef? elegido = null;
                foreach (var b in pool)
                {
                    if (EstaEn(b.Uid, sala.Id)) continue;
                    if (Cuenta(b.Uid) >= b.MaxPartidas) continue;
                    if (!sala.Forzada &&
                        esperaSala < TimeSpan.FromSeconds(b.EsperaSegundos)) continue;
                    elegido = b;
                    break;
                }
                if (elegido == null) break; // nadie disponible para esta sala
                Lanzar(elegido, sala.Id, reanudar: false, ct);
            }
        }
    }

    // ── Arranque automático de salas llenas ────────────────────────────────────
    // Arranca las salas ya LLENAS y con todos listos (públicas y privadas), sin
    // depender de que el host tenga la app abierta. Recibe la lista ya calculada
    // por el barrido (no vuelve a leer `esperando`). El servidor decide el
    // arranque de forma transaccional (WarZeroService.IntentarAutoIniciar) y, si
    // arranca, se avisa por push a los jugadores.
    private async Task AutoIniciarSalasLlenasAsync(List<SalaDef> salasLlenas, CancellationToken ct)
    {
        foreach (var sala in salasLlenas)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (await _svc.IntentarAutoIniciarAsync(sala.Id))
                {
                    _log.LogInformation("[WZ][orquestador] AUTO-INICIO sala {lobby} (llena)", sala.Id);
                    try { await WarZeroNotificaciones.NotificarPartidaIniciadaAsync(_fs.Db, sala.Id); }
                    catch (Exception ex)
                    {
                        _log.LogWarning(ex, "[WZ][orquestador] notif inicio {lobby} falló", sala.Id);
                    }
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "[WZ][orquestador] auto-inicio {lobby} falló", sala.Id);
            }
        }
    }

    // ── Cierre automático por hora límite (diario / turno12h) ───────────────────
    // Recorre las partidas EN CURSO y fuerza la resolución de las que ya vencieron
    // su `fechaResolucion`. La comprobación es perezosa y barata: si aún no vence,
    // no hace nada. Esto hace que el modo turno12h (y diario) se cierre solo aunque
    // NINGÚN jugador tenga la app abierta.
    private async Task ResolverDeadlinesAsync(CancellationToken ct)
    {
        List<DocumentSnapshot> docs = new();
        try
        {
            var snap = await _fs.Db.Collection("Partidas")
                .WhereEqualTo("estado", "en_curso")
                .Limit(_opt.MaxPartidasEnCurso)
                .GetSnapshotAsync(ct);
            docs = snap.Documents.Cast<DocumentSnapshot>().ToList();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[WZ][orquestador] leer en curso para deadlines falló");
            return;
        }

        foreach (var doc in docs)
        {
            ct.ThrowIfCancellationRequested();
            // Reutilizamos el snapshot ya leído: el pre-check de ForzarResolucion no
            // vuelve a leer el documento (la resolución real, si procede, usa su
            // propia transacción con lectura fresca).
            try { await _svc.ForzarResolucionSiProcedeAsync(doc.Id, doc); }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "[WZ][orquestador] resolver deadline {lobby} falló", doc.Id);
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
                // PERFIL del bot (dificultad + estilo) → estrategia configurada.
                var perfil = PerfilBot.Parse(bot.Dificultad, bot.Estilo);
                var runner = new WarZeroBot(_fs, _svc, _botOpt, perfil: perfil);
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

    // ── Publicar ocupación en la colección Bots ────────────────────────────────
    // Escribe `partidasActivas` (nº de partidas que el bot está jugando AHORA) en
    // su documento, para que el panel de Flutter lo muestre sin tener que
    // descargar las partidas enteras (que incluyen tablero e historial). Solo
    // escribe cuando el valor CAMBIA, así el coste es mínimo.
    private async Task PublicarOcupacionAsync(List<BotDef> bots, CancellationToken ct)
    {
        foreach (var b in bots)
        {
            int n = Cuenta(b.Uid);
            if (_publicado.TryGetValue(b.Uid, out var prev) && prev == n) continue;
            try
            {
                await _fs.Db.Collection("Bots").Document(b.Uid).SetAsync(
                    new Dictionary<string, object> { ["partidasActivas"] = n },
                    SetOptions.MergeAll, ct);
                _publicado[b.Uid] = n;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "[WZ][orquestador] no se pudo publicar ocupacion de {uid}", b.Uid);
            }
        }
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
            MaxPartidas: Math.Max(1, max),
            // Perfil del bot: dificultad (medio|alto) y estilo (equilibrado|
            // defensivo|agresivo). Vacío/desconocido → medio/equilibrado.
            Dificultad: M.Str(M.Get(data, "dificultad")),
            Estilo: M.Str(M.Get(data, "estilo")),
            // Segundos que debe llevar una sala en espera (proactiva) antes de que
            // ESTE bot entre. 0 = entra de inmediato. Solo aplica al relleno
            // proactivo; en el forzado (host pulsó Iniciar) el bot entra ya.
            EsperaSegundos: Math.Max(0, M.Int(M.Get(data, "esperaSegundos"))),
            // `activo`: si el bot entra proactivamente a salas nuevas. Los activos
            // se derivan en memoria de la lectura única de Bots (antes era una
            // segunda consulta WhereEqualTo("activo", true) en cada barrido).
            Activo: M.Bool(M.Get(data, "activo")));
    }

    /// TODOS los bots (ignora `activo`). Se usan para RECUPERAR partidas ya en
    /// curso: un bot atascado en una partida debe seguir cerrando sus turnos
    /// aunque esté marcado inactivo (inactivo = "no entres a salas nuevas", no
    /// "abandona las partidas en las que ya estás"). Los ACTIVOS se derivan en
    /// memoria de esta misma lista (b.Activo), sin una segunda lectura.
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
    /// TODAS las salas EN ESPERA (con hueco y llenas) en UNA sola lectura. Antes
    /// se leía la colección `esperando` dos veces por barrido (una para rellenar
    /// con bots y otra para auto-iniciar las llenas). El barrido clasifica el
    /// resultado en memoria. Incluye `TodosListos` para decidir el auto-inicio.
    private async Task<List<SalaDef>> LeerSalasEnEsperaAsync(CancellationToken ct)
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

            int max = M.Int(M.Get(data, "maxJugadores"));
            var jugs = M.List(M.Get(data, "jugadores")).Select(M.Map).ToList();
            int ocupadas = jugs.Count;

            // Todos los jugadores presentes han elegido ejército (listo). Necesario
            // para el auto-inicio: una sala llena de humanos que aún no eligieron
            // NO arranca sola.
            bool todosListos = ocupadas > 0 && jugs.All(j => M.Bool(M.Get(j, "listo")));

            // Dos flujos de relleno:
            //  • Forzada: el host pulsó "Iniciar batalla" con huecos → `rellenarBots`.
            //  • Proactiva (rellenarBots == false): sala en espera normal.
            bool forzada = M.Bool(M.Get(data, "rellenarBots"));
            bool esPrivada = M.Bool(M.Get(data, "esPrivada"));

            DateTime creado = DateTime.UtcNow;
            if (doc.TryGetValue<Timestamp>("creadoEn", out var ts))
                creado = ts.ToDateTime();

            salas.Add(new SalaDef(
                Id: doc.Id, Max: max, Ocupadas: ocupadas, Creado: creado,
                Forzada: forzada, EsPrivada: esPrivada, TodosListos: todosListos));
        }

        salas.Sort((x, y) => x.Creado.CompareTo(y.Creado)); // más antigua primero
        return salas;
    }

    // ── Tipos internos ─────────────────────────────────────────────────────────
    private record BotDef(string Uid, string Alias, int Orden, int MaxPartidas, string Dificultad, string Estilo, int EsperaSegundos, bool Activo);
    private record SalaDef(string Id, int Max, int Ocupadas, DateTime Creado, bool Forzada, bool EsPrivada, bool TodosListos);
    private record PartidaEnCurso(string Id, HashSet<string> Jugadores, HashSet<string> Eliminados);
}