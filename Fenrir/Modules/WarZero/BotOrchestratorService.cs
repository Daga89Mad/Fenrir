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
// ══ ECONOMÍA DE LECTURAS DE FIRESTORE (por qué este fichero es como es) ══════
//
// Firestore cobra UNA lectura por CADA documento que devuelve una consulta. Un
// barrido que lee "todas las partidas en espera" y "todas las partidas en curso"
// cuesta tantas lecturas como documentos haya en esos estados, y esas
// colecciones CRECEN solas con salas y partidas abandonadas que nadie limpia.
//
// La versión anterior, cada 2 minutos, leía `esperando` CUATRO veces (una por
// barrido de 30 s), `en_curso` DOS veces (una consulta para recuperación y otra
// distinta para deadlines) y `Bots` en casi todos los barridos. Con las
// colecciones cerca de sus topes (100 en espera, 300 en curso) eso eran ~33k
// lecturas/hora las 24 h del día, jugara alguien o no, y subiendo cada día.
//
// Ahora:
//   · BARRIDO LIGERO (cada 60 s): SOLO la consulta `esperando`. Y como las salas
//     abandonadas se BORRAN (ver EdadMaxSalaEspera / EdadMaxSalaSinHumanos),
//     esa colección se mantiene pequeña: unos pocos documentos por barrido.
//   · BARRIDO CARO (cada 30 min, y siempre en el primer barrido tras arrancar):
//     UNA sola consulta `en_curso` cuyo snapshot se reutiliza para las DOS tareas
//     (recuperar runners y resolver deadlines). De 2 × N cada 2 min pasa a
//     1 × N cada 30 min: 30 veces menos.
//   · `Bots` se cachea (CacheBots). El panel cambia bots muy de vez en cuando.
//   · ENFRIAMIENTO por sala: cuando un runner se retira de una sala que no
//     arrancó, no se vuelve a meter un bot en ESA sala hasta pasados 30 min.
//     Antes se reinsertaba a los 30 s (la memoria de ocupación se vaciaba al
//     morir el runner), lo que encadenaba transacción + 15 min de sondeo + otra
//     transacción... 24/7 por cada sala atascada.
//   · LOG por barrido con el nº de documentos leídos, para poder correlacionar
//     el consumo con la gráfica de Firebase sin adivinar.
//
// ══ SALAS SIN HUMANOS ═══════════════════════════════════════════════════════
//
// Cuando el último humano sale de una sala (salirDeLobby en Flutter) la sala NO
// se borra si quedan bots: el host pasa a ser un bot y queda una sala fantasma.
// Sin estas reglas el orquestador la rellenaba con otro bot y la auto-iniciaba
// como partida de solo bots, sin ningún humano, quemando lecturas y escrituras
// para nadie. Reglas:
//   · Un bot NUNCA entra en una sala sin humanos (ni proactivo ni forzado).
//   · Una sala sin humanos NUNCA se auto-inicia.
//   · Una sala sin humanos se borra a la hora (EdadMaxSalaSinHumanos): quien
//     venga después crea la suya y los bots entran en segundos.
// "Humano" = jugador de la sala cuyo uid no está en `botsUids` (campo que el
// bot escribe al unirse) ni en la colección Bots (si está cacheada). Si una sala
// antigua no tuviera `botsUids`, todos parecen humanos y se aplica el
// comportamiento clásico (nunca se borra nada por error).
//
// ¿Y los deadlines (turno12h / diario) con 30 min de latencia? Son una red de
// seguridad: cualquier bot que sondee la partida (LeerEstadoAsync) o cualquier
// cliente que la abra ya fuerza la resolución vencida al instante. El barrido
// caro solo cubre partidas de humanos con nadie conectado, y ahí 30 min sobre un
// turno de 12-24 h es irrelevante. Ajustable en `IntervaloTareasCaras`.
//
// SIMULTANEIDAD:
//   `_ocupados` es uid -> CONJUNTO de salas. Un bot está disponible para una
//   sala nueva si nº de salas activas < maxPartidas y no está ya dentro de esa
//   sala. Cada runner corre como un Task.Run independiente.
//
// RECUPERACIÓN (paso 0):
//   Cada runner vive en memoria de ESTE proceso. Si el contenedor se reinicia,
//   los runners mueren y `_ocupados` se vacía, pero las partidas siguen
//   `en_curso` en Firestore. Al arrancar, el orquestador detecta cada partida en
//   curso con un bot que ya es participante y le lanza un runner en modo
//   REANUDAR (sin límite de capacidad: esas partidas YA existen).
//
// El panel Flutter (EdicionBotsScreen) es quien siembra los bots (bot_0…bot_N) y
// pone/quita `activo`, `orden`, `alias` y `maxPartidas`. Aquí solo se lee.
// ─────────────────────────────────────────────────────────────────────────────

public class BotOrchestratorOptions
{
    /// Cada cuánto corre el barrido LIGERO (solo salas en espera). Con la
    /// limpieza de salas abandonadas la consulta devuelve pocos documentos, así
    /// que 60 s es barato y suficiente para rellenar salas nuevas.
    public TimeSpan ScanInterval { get; set; } = TimeSpan.FromSeconds(60);

    /// Cada cuánto corre el barrido CARO (una lectura de TODAS las partidas en
    /// curso, compartida por recuperación de runners y resolución de deadlines).
    /// El primer barrido tras arrancar lo ejecuta siempre.
    public TimeSpan IntervaloTareasCaras { get; set; } = TimeSpan.FromMinutes(30);

    /// Si el barrido caro FALLA (Firestore caído, etc.), cuánto esperar antes
    /// de reintentarlo, en vez de esperar el intervalo completo.
    public TimeSpan ReintentoTareasCaras { get; set; } = TimeSpan.FromMinutes(5);

    /// Tiempo de vida de la caché de la colección `Bots`. Un cambio en el panel
    /// (activar/desactivar un bot) tarda como mucho esto en tener efecto.
    public TimeSpan CacheBots { get; set; } = TimeSpan.FromMinutes(5);

    /// Una sala EN ESPERA con más edad que esto (desde `creadoEn`) se considera
    /// abandonada y se BORRA de Firestore: nadie la va a jugar y cada barrido la
    /// pagaría para siempre. El cliente, si aún la tiene abierta, simplemente
    /// vuelve al menú (room_screen trata un doc inexistente como "salir").
    /// TimeSpan.Zero desactiva la limpieza por edad.
    public TimeSpan EdadMaxSalaEspera { get; set; } = TimeSpan.FromHours(24);

    /// Una sala EN ESPERA en la que NO queda ningún humano (solo bots) se borra
    /// al superar esta edad, sin esperar a EdadMaxSalaEspera. Si el humano se
    /// fue, la sala está muerta. TimeSpan.Zero = usar EdadMaxSalaEspera.
    public TimeSpan EdadMaxSalaSinHumanos { get; set; } = TimeSpan.FromHours(1);

    /// Tope de borrados por barrido, para repartir la limpieza inicial (si hay
    /// cientos de salas viejas acumuladas) en varios barridos.
    public int MaxBorradosPorBarrido { get; set; } = 50;

    /// Tras retirarse un runner de una sala (p. ej. porque no arrancó en
    /// MaxWaitStart), cuánto esperar antes de volver a meter un bot en ESA sala.
    public TimeSpan EnfriamientoSala { get; set; } = TimeSpan.FromMinutes(30);

    /// Si IntentarAutoIniciar devuelve false para una sala llena, cuánto esperar
    /// antes de reintentarlo (evita pagar 2 lecturas por barrido en una sala
    /// llena que por lo que sea no arranca).
    public TimeSpan EnfriamientoAutoInicio { get; set; } = TimeSpan.FromMinutes(5);

    /// Tope de salas en espera a considerar por barrido.
    public int MaxSalasPorBarrido { get; set; } = 100;

    /// Tope de partidas EN CURSO a inspeccionar en el barrido caro.
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

    // Caché de la colección Bots (ver CacheBots).
    private List<BotDef>? _botsCache;
    private DateTime _botsCacheEn = DateTime.MinValue;

    // Cuándo se ejecutó por última vez el barrido caro (lectura de en_curso).
    private DateTime _ultimoBarridoCaro = DateTime.MinValue;

    // Enfriamientos: lobbyId -> instante hasta el que NO se vuelve a tocar.
    //   _enfriamientoSala       → no meter otro bot en esa sala.
    //   _enfriamientoAutoInicio → no reintentar el auto-inicio de esa sala.
    private readonly Dictionary<string, DateTime> _enfriamientoSala = new();
    private readonly Dictionary<string, DateTime> _enfriamientoAutoInicio = new();

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
        _log.LogInformation(
            "[WZ][orquestador] iniciado (ligero cada {s}s, caro cada {m}min, expira salas >{h}h, sin humanos >{sh}h)",
            _opt.ScanInterval.TotalSeconds, _opt.IntervaloTareasCaras.TotalMinutes,
            _opt.EdadMaxSalaEspera.TotalHours, EdadSinHumanos().TotalHours);

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

    // ── Un barrido ─────────────────────────────────────────────────────────────
    private async Task BarridoAsync(CancellationToken ct)
    {
        var ahora = DateTime.UtcNow;
        var tareasCaras = (ahora - _ultimoBarridoCaro) >= _opt.IntervaloTareasCaras;

        PurgarEnfriamientos(ahora);

        // 1) Salas EN ESPERA (única consulta del barrido ligero).
        var salas = await LeerSalasEnEsperaAsync(ct);
        int leidasEspera = salas.Count;

        // Uids de bots conocidos (de la caché, si la hay) para clasificar
        // humanos sin gastar una lectura. El campo `botsUids` del doc ya cubre
        // el caso común; esto es un refuerzo.
        var botsConocidos = BotsConocidos();

        // 2) Limpieza: borrar las abandonadas para que dejen de costar lecturas.
        int borradas = await ExpirarSalasViejasAsync(salas, botsConocidos, ahora, ct);

        // Solo salas CON humanos son candidatas a recibir bots o a arrancar.
        var salasConHueco = salas
            .Where(s => (s.Max <= 0 || s.Ocupadas < s.Max)
                        && TieneHumano(s, botsConocidos)
                        && !EnEnfriamiento(_enfriamientoSala, s.Id, ahora))
            .ToList();
        var salasLlenas = salas
            .Where(s => s.Max > 0 && s.Ocupadas >= s.Max && s.TodosListos
                        && TieneHumano(s, botsConocidos)
                        && !EnEnfriamiento(_enfriamientoAutoInicio, s.Id, ahora))
            .ToList();

        // 3) Bots (cacheados) SOLO si hacen falta: hay salas que rellenar o toca
        //    recuperación. En barridos idle no se tocan.
        var necesitaBots = salasConHueco.Count > 0 || tareasCaras;
        var todos = new List<BotDef>();
        bool botsDeCache = true;
        if (necesitaBots) (todos, botsDeCache) = await ObtenerBotsAsync(ct);
        var activos = todos.Where(b => b.Activo).OrderBy(b => b.Orden).ToList();

        int leidasEnCurso = 0;
        try
        {
            // 4) BARRIDO CARO: una sola lectura de en_curso, reutilizada.
            if (tareasCaras)
            {
                _ultimoBarridoCaro = ahora;
                try
                {
                    var docs = await LeerPartidasEnCursoAsync(ct);
                    leidasEnCurso = docs.Count;
                    RecuperarPartidasEnCurso(todos, docs, ct);
                    await ResolverDeadlinesAsync(docs, ct);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    // Reintentar antes del intervalo completo, pero sin martillear.
                    _ultimoBarridoCaro = ahora - _opt.IntervaloTareasCaras + _opt.ReintentoTareasCaras;
                    _log.LogWarning(ex, "[WZ][orquestador] barrido caro falló; reintento en {m} min",
                        _opt.ReintentoTareasCaras.TotalMinutes);
                }
            }

            // 5) Repartir bots a las salas con hueco (sin lecturas).
            RellenarSalas(salasConHueco, todos, activos, ct);

            // 6) Auto-iniciar las salas llenas y listas.
            await AutoIniciarSalasLlenasAsync(salasLlenas, ahora, ct);
        }
        finally
        {
            if (todos.Count > 0) await PublicarOcupacionAsync(todos, ct);
        }

        // Coste del barrido en LECTURAS de Firestore (aprox.): cada documento
        // devuelto por una consulta es una lectura. Esto es lo que hay que ver
        // bajar en la gráfica de Firebase.
        _log.LogInformation(
            "[WZ][orquestador] barrido{caro}: lecturas≈{tot} (espera={e}, bots={b}, en_curso={c}) · borradas={x} · runners={r}",
            tareasCaras ? "(caro)" : "", leidasEspera + (botsDeCache ? 0 : todos.Count) + leidasEnCurso,
            leidasEspera, botsDeCache ? "caché" : todos.Count.ToString(), leidasEnCurso,
            borradas, TotalRunners());
    }

    // ── Humanos en una sala ────────────────────────────────────────────────────
    private TimeSpan EdadSinHumanos() =>
        _opt.EdadMaxSalaSinHumanos > TimeSpan.Zero ? _opt.EdadMaxSalaSinHumanos : _opt.EdadMaxSalaEspera;

    private HashSet<string> BotsConocidos()
    {
        var cache = _botsCache;
        return cache == null ? new HashSet<string>() : cache.Select(b => b.Uid).ToHashSet();
    }

    /// ¿Queda al menos un jugador que NO sea bot? Un bot es todo uid presente en
    /// `botsUids` del doc o en la colección Bots (si está cacheada).
    private static bool TieneHumano(SalaDef s, HashSet<string> botsConocidos) =>
        s.Jugadores.Any(u => !s.BotsEnSala.Contains(u) && !botsConocidos.Contains(u));

    // ── Limpieza de salas en espera abandonadas ────────────────────────────────
    // Borra de Firestore, y quita de la lista en memoria:
    //   · salas más viejas que EdadMaxSalaEspera (nadie las va a jugar), y
    //   · salas SIN humanos más viejas que EdadMaxSalaSinHumanos (el humano se
    //     fue; los bots que queden en ellas se liberan solos: sus runners ven
    //     que el doc ya no existe y se retiran).
    private async Task<int> ExpirarSalasViejasAsync(
        List<SalaDef> salas, HashSet<string> botsConocidos, DateTime ahora, CancellationToken ct)
    {
        var edadMax = _opt.EdadMaxSalaEspera;
        var edadSinHumanos = EdadSinHumanos();
        if (edadMax <= TimeSpan.Zero && edadSinHumanos <= TimeSpan.Zero) return 0;

        var aBorrar = new List<(SalaDef sala, string motivo)>();
        foreach (var s in salas)
        {
            var edad = ahora - s.Creado;
            bool conHumano = TieneHumano(s, botsConocidos);
            if (!conHumano && edadSinHumanos > TimeSpan.Zero && edad > edadSinHumanos)
                aBorrar.Add((s, "sin humanos"));
            else if (edadMax > TimeSpan.Zero && edad > edadMax)
                aBorrar.Add((s, "abandono"));
        }
        if (aBorrar.Count == 0) return 0;

        int borradas = 0;
        foreach (var (s, motivo) in aBorrar.Take(Math.Max(1, _opt.MaxBorradosPorBarrido)))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await _fs.Db.Collection("Partidas").Document(s.Id).DeleteAsync(cancellationToken: ct);
                salas.Remove(s);
                borradas++;
                _log.LogInformation(
                    "[WZ][orquestador] sala {lobby} borrada por {motivo} ({h:F1} h en espera, {n}/{max} jugadores, {bots} bots{priv})",
                    s.Id, motivo, (ahora - s.Creado).TotalHours, s.Ocupadas, s.Max,
                    s.Jugadores.Count(u => s.BotsEnSala.Contains(u) || botsConocidos.Contains(u)),
                    s.EsPrivada ? ", privada" : "");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "[WZ][orquestador] no se pudo borrar la sala {lobby}", s.Id);
            }
        }
        return borradas;
    }

    // ── Reparto de bots a salas con hueco (sin lecturas) ───────────────────────
    // Recibe SOLO salas con humanos, con hueco y fuera de enfriamiento.
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
            //  • Proactiva: solo bots ACTIVOS.
            var pool = sala.Forzada ? todos : activos;
            if (pool.Count == 0) continue;

            // Tiempo que lleva la sala esperando. En el relleno proactivo cada bot
            // solo entra si la sala lleva al menos su `esperaSegundos` (margen
            // para que entren humanos).
            var esperaSala = DateTime.UtcNow - sala.Creado;

            int libres = Math.Max(0, sala.Max - sala.Ocupadas);
            for (int k = 0; k < libres; k++)
            {
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
    // Arranca las salas ya LLENAS, con todos listos y CON al menos un humano
    // (públicas y privadas), sin depender de que el host tenga la app abierta.
    // El servidor decide el arranque de forma transaccional
    // (WarZeroService.IntentarAutoIniciar) y, si arranca, se avisa por push. Si
    // NO arranca, la sala entra en enfriamiento para no pagar sus 2 lecturas en
    // cada barrido.
    private async Task AutoIniciarSalasLlenasAsync(List<SalaDef> salasLlenas, DateTime ahora, CancellationToken ct)
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
                else
                {
                    lock (_lock) _enfriamientoAutoInicio[sala.Id] = ahora + _opt.EnfriamientoAutoInicio;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                lock (_lock) _enfriamientoAutoInicio[sala.Id] = ahora + _opt.EnfriamientoAutoInicio;
                _log.LogWarning(ex, "[WZ][orquestador] auto-inicio {lobby} falló", sala.Id);
            }
        }
    }

    // ── Cierre automático por hora límite (diario / turno12h) ───────────────────
    // Recorre las partidas EN CURSO ya leídas por el barrido caro y fuerza la
    // resolución de las que vencieron su `fechaResolucion`. Reutiliza el
    // snapshot: el pre-check de ForzarResolucion NO vuelve a leer el documento
    // (la resolución real, si procede, usa su propia transacción).
    private async Task ResolverDeadlinesAsync(List<DocumentSnapshot> docs, CancellationToken ct)
    {
        foreach (var doc in docs)
        {
            ct.ThrowIfCancellationRequested();
            try { await _svc.ForzarResolucionSiProcedeAsync(doc.Id, doc); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "[WZ][orquestador] resolver deadline {lobby} falló", doc.Id);
            }
        }
    }

    // ── Recuperación de partidas en curso ──────────────────────────────────────
    // Para cada partida `en_curso`, cualquier bot que sea jugador, no esté
    // eliminado y no tenga runner vivo para ESA sala recibe un runner en modo
    // REANUDAR — esté marcado activo o no. Idempotente. Sin tope de capacidad.
    private void RecuperarPartidasEnCurso(List<BotDef> bots, List<DocumentSnapshot> docs, CancellationToken ct)
    {
        var porUid = new Dictionary<string, BotDef>();
        foreach (var b in bots) porUid[b.Uid] = b;
        if (porUid.Count == 0) return;

        int recuperados = 0;
        foreach (var doc in docs)
        {
            var p = ParsePartidaEnCurso(doc);
            if (p == null) continue;

            var botsEnPartida = p.Jugadores.Where(porUid.ContainsKey).ToList();
            if (botsEnPartida.Count == 0) continue;

            foreach (var uid in botsEnPartida)
            {
                if (p.Eliminados.Contains(uid)) continue;
                if (EstaEn(uid, p.Id)) continue;

                var bot = porUid[uid];
                _log.LogInformation(
                    "[WZ][orquestador] RECUPERANDO {alias} ({uid}) en partida en curso {lobby}",
                    bot.Alias, uid, p.Id);
                Lanzar(bot, p.Id, reanudar: true, ct);
                recuperados++;
            }
        }
        if (recuperados > 0)
            _log.LogInformation("[WZ][orquestador] recuperación: {n} runners relanzados", recuperados);
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
                    // El runner se ha ido de esta sala. Si la sala sigue en espera
                    // (no arrancó), no volver a meter un bot hasta pasado el
                    // enfriamiento. Si la partida terminó, el enfriamiento es
                    // irrelevante (la sala ya no está `esperando`).
                    _enfriamientoSala[lobbyId] = DateTime.UtcNow + _opt.EnfriamientoSala;
                }
            }
        }, ct);
    }

    // ── Publicar ocupación en la colección Bots ────────────────────────────────
    // Escribe `partidasActivas` en el doc del bot, para que el panel de Flutter
    // lo muestre sin descargar las partidas. Solo cuando el valor CAMBIA.
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

    // ── Estado en memoria (bajo lock) ──────────────────────────────────────────
    private int Cuenta(string uid)
    {
        lock (_lock) return _ocupados.TryGetValue(uid, out var s) ? s.Count : 0;
    }

    private bool EstaEn(string uid, string lobbyId)
    {
        lock (_lock) return _ocupados.TryGetValue(uid, out var s) && s.Contains(lobbyId);
    }

    private int TotalRunners()
    {
        lock (_lock) return _ocupados.Values.Sum(s => s.Count);
    }

    private bool EnEnfriamiento(Dictionary<string, DateTime> tabla, string lobbyId, DateTime ahora)
    {
        lock (_lock) return tabla.TryGetValue(lobbyId, out var hasta) && hasta > ahora;
    }

    private void PurgarEnfriamientos(DateTime ahora)
    {
        lock (_lock)
        {
            foreach (var k in _enfriamientoSala.Where(kv => kv.Value <= ahora).Select(kv => kv.Key).ToList())
                _enfriamientoSala.Remove(k);
            foreach (var k in _enfriamientoAutoInicio.Where(kv => kv.Value <= ahora).Select(kv => kv.Key).ToList())
                _enfriamientoAutoInicio.Remove(k);
        }
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
            Dificultad: M.Str(M.Get(data, "dificultad")),
            Estilo: M.Str(M.Get(data, "estilo")),
            EsperaSegundos: Math.Max(0, M.Int(M.Get(data, "esperaSegundos"))),
            Activo: M.Bool(M.Get(data, "activo")));
    }

    /// TODOS los bots (ignora `activo`), servidos de caché si está fresca.
    /// Devuelve también si vinieron de caché (para el log de coste).
    private async Task<(List<BotDef> bots, bool deCache)> ObtenerBotsAsync(CancellationToken ct)
    {
        if (_botsCache != null && (DateTime.UtcNow - _botsCacheEn) < _opt.CacheBots)
            return (_botsCache, true);

        try
        {
            var snap = await _fs.Db.Collection("Bots").GetSnapshotAsync(ct);
            _botsCache = snap.Documents.Select(ParseBot).ToList();
            _botsCacheEn = DateTime.UtcNow;
            return (_botsCache, false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[WZ][orquestador] leer Bots falló; uso la caché anterior");
            return (_botsCache ?? new List<BotDef>(), true);
        }
    }

    /// UNA lectura de las partidas EN CURSO (snapshots crudos). El barrido caro
    /// reutiliza esta lista para recuperación Y deadlines.
    private async Task<List<DocumentSnapshot>> LeerPartidasEnCursoAsync(CancellationToken ct)
    {
        var snap = await _fs.Db.Collection("Partidas")
            .WhereEqualTo("estado", "en_curso")
            .Limit(_opt.MaxPartidasEnCurso)
            .GetSnapshotAsync(ct);
        return snap.Documents.Cast<DocumentSnapshot>().ToList();
    }

    /// Jugadores (lista de MAPAS con `uid`) y eliminados (lista de uids) de una
    /// partida en curso. null si no tiene jugadores.
    private static PartidaEnCurso? ParsePartidaEnCurso(DocumentSnapshot doc)
    {
        var data = M.Map(M.FromFs(doc.ToDictionary()));
        var jugadores = M.List(M.Get(data, "jugadores"))
            .Select(j => M.Str(M.Get(M.Map(j), "uid")))
            .Where(u => u != "").ToHashSet();
        if (jugadores.Count == 0) return null;
        var eliminados = M.List(M.Get(data, "jugadoresEliminados"))
            .Select(M.Str).Where(s => s != "").ToHashSet();
        return new PartidaEnCurso(doc.Id, jugadores, eliminados);
    }

    /// TODAS las salas EN ESPERA (con hueco y llenas) en UNA sola lectura. Se
    /// filtra `esPrivada` en memoria y se ordena por `creadoEn` para no exigir un
    /// índice compuesto en Firestore.
    private async Task<List<SalaDef>> LeerSalasEnEsperaAsync(CancellationToken ct)
    {
        var snap = await _fs.Db.Collection("Partidas")
            .WhereEqualTo("estado", "esperando")
            .Limit(_opt.MaxSalasPorBarrido)
            .GetSnapshotAsync(ct);

        var salas = new List<SalaDef>();
        foreach (var doc in snap.Documents)
        {
            var data = M.Map(M.FromFs(doc.ToDictionary()));

            int max = M.Int(M.Get(data, "maxJugadores"));
            var jugs = M.List(M.Get(data, "jugadores")).Select(M.Map).ToList();
            var uids = jugs.Select(j => M.Str(M.Get(j, "uid"))).Where(u => u != "").ToList();
            int ocupadas = jugs.Count;
            bool todosListos = ocupadas > 0 && jugs.All(j => M.Bool(M.Get(j, "listo")));
            bool forzada = M.Bool(M.Get(data, "rellenarBots"));
            bool esPrivada = M.Bool(M.Get(data, "esPrivada"));

            // Bots que se unieron a esta sala (lo escribe el bot al entrar).
            var botsEnSala = M.List(M.Get(data, "botsUids"))
                .Select(M.Str).Where(u => u != "").ToHashSet();

            // Sin `creadoEn` la sala se trata como recién creada: nunca se expira
            // ni se considera "vieja" para el relleno.
            DateTime creado = DateTime.UtcNow;
            if (doc.TryGetValue<Timestamp>("creadoEn", out var ts))
                creado = ts.ToDateTime();

            salas.Add(new SalaDef(
                Id: doc.Id, Max: max, Ocupadas: ocupadas, Creado: creado,
                Forzada: forzada, EsPrivada: esPrivada, TodosListos: todosListos,
                Jugadores: uids, BotsEnSala: botsEnSala));
        }

        salas.Sort((x, y) => x.Creado.CompareTo(y.Creado)); // más antigua primero
        return salas;
    }

    // ── Tipos internos ─────────────────────────────────────────────────────────
    private record BotDef(string Uid, string Alias, int Orden, int MaxPartidas, string Dificultad, string Estilo, int EsperaSegundos, bool Activo);
    private record SalaDef(
        string Id, int Max, int Ocupadas, DateTime Creado, bool Forzada, bool EsPrivada, bool TodosListos,
        List<string> Jugadores, HashSet<string> BotsEnSala);
    private record PartidaEnCurso(string Id, HashSet<string> Jugadores, HashSet<string> Eliminados);
}