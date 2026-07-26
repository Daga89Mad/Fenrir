using System.Collections.Concurrent;
using System.Text.Json;
using Google.Cloud.Firestore;

// ─────────────────────────────────────────────────────────────────────────────
// WarZeroBot.cs
//
// Bot server-side para RELLENAR SALAS. Corre dentro de Fenrir y reutiliza
// WarZeroService (EntrarAsync / CerrarTurnoAsync / LeerEstadoAsync /
// ActualizarStatsAsync) y WarZeroFirestore, así que juega contra la misma
// lógica autoritativa que un humano; no toca la resolución del turno.
//
// REGLA CLAVE (verificada en _serializarTablero del cliente): al cerrar turno
// cada jugador reenvía SOLO sus propias cartas y el servidor fusiona las de
// todos. Por eso la estrategia SIEMPRE reemite todas las unidades propias
// (posición actual, movida o desplegada) o el ejército desaparecería.
//
// Estrategias disponibles:
//   · ReclutaStrategy   — v1: arrastra ejército + despliega en el cuartel.
//   · EstrategaStrategy — v2 (por defecto): despliega, MUEVE hacia el cuartel
//     enemigo, ATACA solo cuando gana, CONQUISTA coordinando fuerza > 80, y usa
//     HABILIDADES ofensivas/control (disparo, veneno, parálisis, escudo).
// ─────────────────────────────────────────────────────────────────────────────

public class WarZeroBotOptions
{
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(5);
    public TimeSpan ThinkDelay { get; set; } = TimeSpan.FromSeconds(3);
    public TimeSpan MaxWaitStart { get; set; } = TimeSpan.FromMinutes(15);
    public int MaxDeploysPorTurno { get; set; } = 2;
}

/// Contexto que recibe la estrategia para decidir la jugada del turno.
public class BotContext
{
    public required Dictionary<string, object?> Estado { get; init; }
    public required string BotUid { get; init; }
    public required int Turno { get; init; }
    public required string Cuartel { get; init; }
    public required int Energia { get; init; }
    public required List<string> Mano { get; init; }
    public required Dictionary<string, Dictionary<string, object?>> CatalogoMano { get; init; }
    public required string Zona { get; init; }

    // Terreno del mapa: coord -> "land"|"sea"|"deepSea"|"amphibious". Ausente = land.
    public required Dictionary<string, string> Terreno { get; init; }
    public required int Filas { get; init; }
    public required int Columnas { get; init; }
}

/// Jugada resuelta: celdas propias, acciones (habilidades) y estado de mano/energía.
public class BotMove
{
    public Dictionary<string, List<Dictionary<string, object?>>> Celdas { get; init; } = new();
    public List<Dictionary<string, object?>> Acciones { get; init; } = new();
    public List<string> ManoResultante { get; init; } = new();
    public int EnergiaGastada { get; init; }
}

public interface IBotStrategy
{
    BotMove DecidirJugada(BotContext ctx);
}

// ─────────────────────────────────────────────────────────────────────────────
// v1 — Recluta: arrastra ejército + despliega en el cuartel. Nunca bloquea.
// ─────────────────────────────────────────────────────────────────────────────
public class ReclutaStrategy : IBotStrategy
{
    private readonly int _maxDeploys;
    public ReclutaStrategy(int maxDeploysPorTurno = 2) => _maxDeploys = Math.Max(0, maxDeploysPorTurno);

    public BotMove DecidirJugada(BotContext ctx)
    {
        var celdas = new Dictionary<string, List<Dictionary<string, object?>>>();
        var tablero = M.Map(M.Get(ctx.Estado, "tablero"));
        var zona = ctx.Zona;

        foreach (var (coord, cartasRaw) in tablero)
            foreach (var cRaw in M.List(cartasRaw))
            {
                var carta = M.Map(cRaw);
                if (M.Str(M.Get(carta, "ownerUid")) != ctx.BotUid) continue;
                if (!celdas.TryGetValue(coord, out var lst)) { lst = new(); celdas[coord] = lst; }
                lst.Add(new Dictionary<string, object?>(carta));
                if (zona == "") zona = M.Str(M.Get(carta, "ownerZone"));
            }

        var mano = new List<string>(ctx.Mano);
        int energia = ctx.Energia, gastado = 0, desplegadas = 0;

        if (ctx.Cuartel != "")
            foreach (var id in ctx.Mano)
            {
                if (desplegadas >= _maxDeploys) break;
                if (!ctx.CatalogoMano.TryGetValue(id, out var cartaBase)) continue;
                int coste = M.Int(M.Get(cartaBase, "Coste", "coste"));
                if (coste > energia) continue;
                var celda = new Dictionary<string, object?>(cartaBase)
                { ["id"] = id, ["ownerUid"] = ctx.BotUid, ["ownerZone"] = zona, ["instanceId"] = Guid.NewGuid().ToString("N") };
                if (!celdas.TryGetValue(ctx.Cuartel, out var lst)) { lst = new(); celdas[ctx.Cuartel] = lst; }
                lst.Add(celda);
                energia -= coste; gastado += coste; desplegadas++; mano.Remove(id);
            }

        return new BotMove { Celdas = celdas, ManoResultante = mano, EnergiaGastada = gastado };
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// v2 — Estratega (equilibrado): mover + atacar + habilidades.
// ─────────────────────────────────────────────────────────────────────────────
public class EstrategaStrategy : IBotStrategy
{
    private readonly int _maxDeploys;
    private readonly int _maxAcciones;
    private const int DefensaObelisco = 80;

    public EstrategaStrategy(int maxDeploysPorTurno = 2, int maxAcciones = 3)
    {
        _maxDeploys = Math.Max(0, maxDeploysPorTurno);
        _maxAcciones = Math.Max(0, maxAcciones);
    }

    // Efecto/rango de las habilidades que usa el bot (subset del catálogo).
    private enum Efe { Disparo, Veneno, Paralisis, Escudo }
    private enum Rng { Frontera, Radio7, Cualquiera, Propia }
    private readonly record struct Hab(Efe Efecto, Rng Rango, int NumObjetivos, bool ExcluyeCG);

    private static readonly Dictionary<int, Hab> Cat = new()
    {
        [1] = new(Efe.Disparo, Rng.Frontera, 1, false),
        [2] = new(Efe.Disparo, Rng.Radio7, 1, false),
        [3] = new(Efe.Disparo, Rng.Cualquiera, 1, false),
        [6] = new(Efe.Veneno, Rng.Frontera, 2, false),
        [7] = new(Efe.Veneno, Rng.Radio7, 1, true),
        [8] = new(Efe.Veneno, Rng.Cualquiera, 1, false),
        [9] = new(Efe.Paralisis, Rng.Frontera, 1, false),
        [10] = new(Efe.Paralisis, Rng.Radio7, 1, true),
        [11] = new(Efe.Paralisis, Rng.Cualquiera, 1, false),
        [12] = new(Efe.Escudo, Rng.Propia, 1, false),
        [13] = new(Efe.Escudo, Rng.Frontera, 1, false),
        [14] = new(Efe.Escudo, Rng.Cualquiera, 1, false),
    };

    public BotMove DecidirJugada(BotContext ctx)
    {
        var estado = ctx.Estado;
        var botUid = ctx.BotUid;
        int filas = ctx.Filas, columnas = ctx.Columnas;
        var terreno = ctx.Terreno;

        var tablero = M.Map(M.Get(estado, "tablero"));
        var obeliscos = M.Map(M.Get(estado, "obeliscos"));
        var eliminados = M.List(M.Get(estado, "jugadoresEliminados")).Select(M.Str).ToHashSet();

        // ── Parse tablero: unidades propias y celdas con enemigos ──
        var ownUnits = new List<(string coord, Dictionary<string, object?> card, string inst)>();
        var enemyByCoord = new Dictionary<string, List<Dictionary<string, object?>>>();
        foreach (var (coord, raw) in tablero)
            foreach (var cRaw in M.List(raw))
            {
                var card = M.Map(cRaw);
                if (M.Str(M.Get(card, "ownerUid")) == botUid)
                    ownUnits.Add((coord, card, M.Str(M.Get(card, "instanceId"))));
                else
                {
                    if (!enemyByCoord.TryGetValue(coord, out var l)) { l = new(); enemyByCoord[coord] = l; }
                    l.Add(card);
                }
            }

        // ── Cuarteles enemigos vivos ──
        var enemyCuarteles = new HashSet<string>();
        foreach (var (uid, coordObj) in obeliscos)
        {
            if (uid == botUid || eliminados.Contains(uid)) continue;
            var c = M.Str(coordObj);
            if (c != "") enemyCuarteles.Add(c);
        }

        string? miCuartel = ctx.Cuartel != "" ? ctx.Cuartel : null;
        string? target = MasCercano(miCuartel, enemyCuarteles, filas, columnas)
                         ?? MasCercano(miCuartel, enemyByCoord.Keys, filas, columnas);

        var zona = ctx.Zona;
        if (zona == "")
            foreach (var u in ownUnits) { var z = M.Str(M.Get(u.card, "ownerZone")); if (z != "") { zona = z; break; } }

        var celdas = new Dictionary<string, List<Dictionary<string, object?>>>();
        void Place(string coord, Dictionary<string, object?> card)
        {
            if (!celdas.TryGetValue(coord, out var l)) { l = new(); celdas[coord] = l; }
            l.Add(card);
        }

        var mano = new List<string>(ctx.Mano);
        int energia = ctx.Energia, gastado = 0, desplegadas = 0;

        // ── Despliegue (reserva ~40% de energía para ataque/habilidades) ──
        int reserva = energia * 4 / 10;
        if (ctx.Cuartel != "")
            foreach (var id in ctx.Mano.ToList())
            {
                if (desplegadas >= _maxDeploys) break;
                if (!ctx.CatalogoMano.TryGetValue(id, out var baseCard)) continue;
                int coste = M.Int(M.Get(baseCard, "Coste", "coste"));
                if (energia - coste < reserva) continue;
                var celda = new Dictionary<string, object?>(baseCard)
                { ["id"] = id, ["ownerUid"] = botUid, ["ownerZone"] = zona, ["instanceId"] = Guid.NewGuid().ToString("N") };
                Place(ctx.Cuartel, celda);
                energia -= coste; gastado += coste; desplegadas++; mano.Remove(id);
            }

        // ── Decidir destino de cada unidad propia ──
        var destino = new Dictionary<string, string>();   // inst -> coord final
        var origen = new Dictionary<string, string>();    // inst -> coord actual
        foreach (var u in ownUnits) origen[u.inst] = u.coord;

        // Conquista coordinada: cuartel enemigo sin defensor al que varias
        // unidades llegan sumando fuerza > 80.
        var yaAsignada = new HashSet<string>();
        if (target != null && enemyCuarteles.Contains(target) &&
            !CuartelDefendido(target, obeliscos, botUid, enemyByCoord))
        {
            var llegan = ownUnits
                .Where(u => Alcanzables(u.coord, Mov(u.card), Tipo(u.card), terreno, filas, columnas).Contains(target))
                .ToList();
            if (llegan.Sum(u => Fuerza(u.card)) > DefensaObelisco)
                foreach (var u in llegan) { destino[u.inst] = target; yaAsignada.Add(u.inst); }
        }

        // Movimiento/ataque individual del resto.
        foreach (var u in ownUnits)
        {
            if (yaAsignada.Contains(u.inst)) continue;
            destino[u.inst] = DecidirMovimiento(u.coord, u.card, target, terreno, filas, columnas,
                                                enemyByCoord, enemyCuarteles, obeliscos, botUid);
        }

        // Colocar todas las unidades propias en su destino.
        foreach (var u in ownUnits)
            Place(destino[u.inst], new Dictionary<string, object?>(u.card));

        // ── Habilidades (solo unidades que NO se movieron; origen = su celda) ──
        var acciones = new List<Dictionary<string, object?>>();
        foreach (var u in ownUnits)
        {
            if (acciones.Count >= _maxAcciones) break;
            if (destino[u.inst] != u.coord) continue; // se movió: no castea

            int habId = M.Int(M.Get(u.card, "IdHabilidad", "idHabilidad"));
            if (!Cat.TryGetValue(habId, out var hab)) continue;
            int coste = M.Int(M.Get(u.card, "CosteHabilidad", "costeHabilidad"));
            if (coste > energia) continue;
            if (EnEnfriamiento(u.card, ctx.Turno)) continue;

            var objetivos = ElegirObjetivos(hab, u.coord, filas, columnas, enemyByCoord, enemyCuarteles, miCuartel);
            if (objetivos.Count < hab.NumObjetivos) continue;
            objetivos = objetivos.Take(hab.NumObjetivos).ToList();

            acciones.Add(new Dictionary<string, object?>
            {
                ["habilidadId"] = habId,
                ["uid"] = botUid,
                ["zona"] = zona,
                ["origen"] = u.coord,
                ["objetivos"] = objetivos,
                ["turno"] = ctx.Turno,
                ["costePagado"] = coste,
            });
            energia -= coste; gastado += coste;
        }

        return new BotMove
        {
            Celdas = celdas,
            Acciones = acciones,
            ManoResultante = mano,
            EnergiaGastada = gastado,
        };
    }

    // ── Decisión de movimiento de una unidad ──
    private string DecidirMovimiento(
        string coord, Dictionary<string, object?> card, string? target,
        Dictionary<string, string> terreno, int filas, int columnas,
        Dictionary<string, List<Dictionary<string, object?>>> enemyByCoord,
        HashSet<string> enemyCuarteles, Dictionary<string, object?> obeliscos, string botUid)
    {
        int mov = Mov(card), tipo = Tipo(card);
        int myF = Fuerza(card), myD = Defensa(card);
        var reach = Alcanzables(coord, mov, tipo, terreno, filas, columnas);
        if (reach.Count == 0) return coord;

        // 1) Ataque que se gana: celda con enemigos donde vencemos.
        string? mejorAtaque = null; int mejorValor = -1;
        foreach (var c in reach)
        {
            if (!enemyByCoord.ContainsKey(c)) continue;
            if (!GanoAtacando(myF, myD, c, enemyByCoord, enemyCuarteles, obeliscos, botUid)) continue;
            int valor = enemyByCoord[c].Sum(Coste);
            if (valor > mejorValor) { mejorValor = valor; mejorAtaque = c; }
        }
        if (mejorAtaque != null) return mejorAtaque;

        // 2) Conquista en solitario de un cuartel sin defensor (fuerza > 80).
        foreach (var c in reach)
            if (enemyCuarteles.Contains(c) && !CuartelDefendido(c, obeliscos, botUid, enemyByCoord) && myF > DefensaObelisco)
                return c;

        // 3) Avanzar hacia el objetivo por la casilla más cercana que no sea
        //    una pelea perdida.
        if (target == null) return coord;
        int distActual = Manhattan(coord, target, filas, columnas);
        string mejor = coord; int mejorDist = distActual;
        foreach (var c in reach)
        {
            // Evitar meterse en una celda con enemigos si no la ganamos.
            if (enemyByCoord.ContainsKey(c) &&
                !GanoAtacando(myF, myD, c, enemyByCoord, enemyCuarteles, obeliscos, botUid)) continue;
            // Evitar cuartel enemigo que no podemos tomar.
            if (enemyCuarteles.Contains(c) && CuartelDefendido(c, obeliscos, botUid, enemyByCoord) &&
                !GanoAtacando(myF, myD, c, enemyByCoord, enemyCuarteles, obeliscos, botUid)) continue;

            int d = Manhattan(c, target, filas, columnas);
            if (d < mejorDist) { mejorDist = d; mejor = c; }
        }
        return mejor;
    }

    // ── Combate ──
    private bool GanoAtacando(
        int myF, int myD, string coord,
        Dictionary<string, List<Dictionary<string, object?>>> enemyByCoord,
        HashSet<string> enemyCuarteles, Dictionary<string, object?> obeliscos, string botUid)
    {
        if (!enemyByCoord.TryGetValue(coord, out var enemigos) || enemigos.Count == 0)
            return enemyCuarteles.Contains(coord) ? myF > DefensaObelisco : true; // cuartel vacío
        int fe = enemigos.Sum(Fuerza), de = enemigos.Sum(Defensa);
        // Bonus de +80 defensa si es un cuartel enemigo defendido.
        if (enemyCuarteles.Contains(coord) && CuartelDefendido(coord, obeliscos, botUid, enemyByCoord))
            de += DefensaObelisco;
        // Poder neto: gano si el mío supera estrictamente al enemigo.
        return (myF - de) > (fe - myD);
    }

    private static bool CuartelDefendido(
        string coord, Dictionary<string, object?> obeliscos, string botUid,
        Dictionary<string, List<Dictionary<string, object?>>> enemyByCoord)
    {
        // Dueño del cuartel.
        string dueno = "";
        foreach (var (uid, cObj) in obeliscos) if (M.Str(cObj) == coord) { dueno = uid; break; }
        if (dueno == "" || dueno == botUid) return false;
        // ¿Tiene el dueño cartas propias en esa celda?
        if (!enemyByCoord.TryGetValue(coord, out var cartas)) return false;
        return cartas.Any(c => M.Str(M.Get(c, "ownerUid")) == dueno);
    }

    // ── Objetivos de habilidad (por celdas con enemigos en rango) ──
    private List<string> ElegirObjetivos(
        Hab hab, string origen, int filas, int columnas,
        Dictionary<string, List<Dictionary<string, object?>>> enemyByCoord,
        HashSet<string> enemyCuarteles, string? miCuartel)
    {
        // Escudo: defensivo, sobre celda propia.
        if (hab.Efecto == Efe.Escudo)
        {
            // Solo si hay un enemigo adyacente al origen (amenaza real).
            bool amenaza = Vecinas(origen, filas, columnas).Any(enemyByCoord.ContainsKey);
            if (!amenaza) return new();
            var objetivo = hab.Rango == Rng.Propia ? origen : (miCuartel ?? origen);
            return new() { objetivo };
        }

        // Ofensivas/control: celdas con enemigos dentro de rango.
        bool EnRango(string c) => hab.Rango switch
        {
            Rng.Frontera => Manhattan(origen, c, filas, columnas) == 1,
            Rng.Radio7 => Manhattan(origen, c, filas, columnas) <= 7,
            Rng.Cualquiera => c != origen,
            Rng.Propia => c == origen,
            _ => false,
        };

        var candidatas = enemyByCoord.Keys
            .Where(EnRango)
            .Where(c => !hab.ExcluyeCG || !enemyCuarteles.Contains(c))
            .OrderByDescending(c => enemyByCoord[c].Sum(Coste))   // pega al grupo más valioso
            .ToList();
        return candidatas;
    }

    // ── Movimiento (BFS ortogonal, réplica del cliente) ──
    private static HashSet<string> Alcanzables(
        string from, int mov, int tipo, Dictionary<string, string> terreno, int filas, int columnas)
    {
        var res = new HashSet<string>();
        if (mov <= 0) return res;
        var p0 = Parse(from);
        if (p0 == null) return res;
        var visited = new Dictionary<string, int> { [from] = 0 };
        var queue = new Queue<(string coord, int steps)>();
        queue.Enqueue((from, 0));
        var deltas = new (int dr, int dc)[] { (-1, 0), (1, 0), (0, -1), (0, 1) };

        while (queue.Count > 0)
        {
            var (coord, steps) = queue.Dequeue();
            if (steps >= mov) continue;
            var pos = Parse(coord);
            if (pos == null) continue;
            var (ri, ci) = pos.Value;
            foreach (var (dr, dc) in deltas)
            {
                int nr = ri + dr, nc = ci + dc;
                if (nr < 0 || nr >= filas || nc < 0 || nc >= columnas) continue;
                var nCoord = Label(nr, nc);
                int newSteps = steps + 1;
                if (visited.GetValueOrDefault(nCoord, 999) <= newSteps) continue;
                if (!CanTraverse(nCoord, tipo, terreno)) continue;
                visited[nCoord] = newSteps;
                if (nCoord != from && CanLand(nCoord, tipo, terreno)) res.Add(nCoord);
                if (newSteps < mov) queue.Enqueue((nCoord, newSteps));
            }
        }
        return res;
    }

    // ── Geometría / terreno ──
    private static (int ri, int ci)? Parse(string coord)
    {
        if (string.IsNullOrEmpty(coord) || coord.Length < 2) return null;
        int ri = char.ToUpperInvariant(coord[0]) - 'A';
        if (!int.TryParse(coord[1..], out int col)) return null;
        return (ri, col - 1);
    }

    private static string Label(int ri, int ci) => $"{(char)('A' + ri)}{ci + 1}";

    private static string Terr(string coord, Dictionary<string, string> terreno)
        => terreno.TryGetValue(coord, out var t) ? t : "land";

    private static bool CanTraverse(string coord, int tipo, Dictionary<string, string> terreno)
    {
        var t = Terr(coord, terreno);
        return tipo switch
        {
            1 => t == "land" || t == "amphibious",
            3 => t == "sea" || t == "deepSea" || t == "amphibious",
            _ => true, // tipo 2 vuela
        };
    }

    private static bool CanLand(string coord, int tipo, Dictionary<string, string> terreno)
    {
        var t = Terr(coord, terreno);
        return tipo switch
        {
            1 or 2 => t == "land" || t == "amphibious",
            3 => t == "sea" || t == "deepSea" || t == "amphibious",
            _ => true,
        };
    }

    private static int Manhattan(string a, string b, int filas, int columnas)
    {
        var pa = Parse(a); var pb = Parse(b);
        if (pa == null || pb == null) return int.MaxValue;
        return Math.Abs(pa.Value.ri - pb.Value.ri) + Math.Abs(pa.Value.ci - pb.Value.ci);
    }

    private static IEnumerable<string> Vecinas(string coord, int filas, int columnas)
    {
        var p = Parse(coord);
        if (p == null) yield break;
        var (ri, ci) = p.Value;
        foreach (var (dr, dc) in new[] { (-1, 0), (1, 0), (0, -1), (0, 1) })
        {
            int nr = ri + dr, nc = ci + dc;
            if (nr < 0 || nr >= filas || nc < 0 || nc >= columnas) continue;
            yield return Label(nr, nc);
        }
    }

    private static string? MasCercano(string? from, IEnumerable<string> cands, int filas, int columnas)
    {
        if (from == null) return cands.FirstOrDefault();
        string? mejor = null; int mejorD = int.MaxValue;
        foreach (var c in cands)
        {
            int d = Manhattan(from, c, filas, columnas);
            if (d < mejorD) { mejorD = d; mejor = c; }
        }
        return mejor;
    }

    // ── Stats de carta ──
    private static int Fuerza(Dictionary<string, object?> c) => M.Int(M.Get(c, "Fuerza", "fuerza"));
    private static int Defensa(Dictionary<string, object?> c) => M.Int(M.Get(c, "Defensa", "defensa"));
    private static int Coste(Dictionary<string, object?> c) => M.Int(M.Get(c, "Coste", "coste"));
    private static int Mov(Dictionary<string, object?> c) => M.Int(M.Get(c, "Movimiento", "movimiento"));
    private static int Tipo(Dictionary<string, object?> c) { int t = M.Int(M.Get(c, "Tipo", "tipo")); return t <= 0 ? 1 : t; }

    private static bool EnEnfriamiento(Dictionary<string, object?> c, int turno)
    {
        int enf = M.Int(M.Get(c, "EnfriamientoHabilidad", "enfriamientoHabilidad"));
        if (enf <= 0) return false;
        var ultimoObj = M.Get(c, "UltimoUsoHabilidad", "ultimoUsoHabilidad");
        if (ultimoObj == null) return false;
        int ultimo = M.Int(ultimoObj);
        return (turno - ultimo) < enf;
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Orquestador del bot para UNA partida.
// ─────────────────────────────────────────────────────────────────────────────
public class WarZeroBot
{
    private readonly WarZeroFirestore _fs;
    private readonly WarZeroService _svc;
    private readonly WarZeroBotOptions _opt;
    private readonly IBotStrategy _strategy;

    // Cache de terreno/dimensiones por mapa (no cambia durante la partida).
    private readonly ConcurrentDictionary<string, (Dictionary<string, string> terreno, int filas, int columnas)> _mapas = new();

    public WarZeroBot(
        WarZeroFirestore fs, WarZeroService svc,
        WarZeroBotOptions? options = null, IBotStrategy? strategy = null)
    {
        _fs = fs;
        _svc = svc;
        _opt = options ?? new WarZeroBotOptions();
        _strategy = strategy ?? new EstrategaStrategy(_opt.MaxDeploysPorTurno);
    }

    public async Task RunForLobbyAsync(
        string lobbyId, string botUid, string botAlias, CancellationToken ct = default)
    {
        try
        {
            Log(botUid, $"entrando a rellenar la sala {lobbyId}");
            if (!await UnirseYMarcarListoAsync(lobbyId, botUid, botAlias, ct))
            { Log(botUid, "no pude unirme a la sala"); return; }
            if (!await EsperarArranqueAsync(lobbyId, ct))
            { Log(botUid, "la sala no arrancó a tiempo; me retiro"); return; }

            await _svc.EntrarAsync(new EntrarRequest { LobbyId = lobbyId, Uid = botUid });
            Log(botUid, "dentro de la partida; empiezo a jugar");
            await BuclePartidaAsync(lobbyId, botUid, ct);
            Log(botUid, "partida terminada");
        }
        catch (OperationCanceledException) { Log(botUid, "cancelado"); }
        catch (Exception ex) { Console.Error.WriteLine($"[WZ][bot {botUid}] error fatal: {ex}"); }
    }

    // ── Unirse ──
    private async Task<bool> UnirseYMarcarListoAsync(
        string lobbyId, string botUid, string botAlias, CancellationToken ct)
    {
        var lobbyRef = _fs.Db.Collection("Partidas").Document(lobbyId);
        return await _fs.Db.RunTransactionAsync(async tx =>
        {
            var snap = await tx.GetSnapshotAsync(lobbyRef, ct);
            if (!snap.Exists) return false;
            var data = M.Map(M.FromFs(snap.ToDictionary()));
            if (M.Str(M.Get(data, "estado")) != "esperando") return false;

            var jugadores = M.List(M.Get(data, "jugadores")).Select(M.Map).ToList();
            int max = M.Int(M.Get(data, "maxJugadores"));
            bool yaEstoy = jugadores.Any(j => M.Str(M.Get(j, "uid")) == botUid);
            if (!yaEstoy)
            {
                if (max > 0 && jugadores.Count >= max) return false;
                jugadores.Add(new Dictionary<string, object?> { ["uid"] = botUid, ["alias"] = botAlias, ["listo"] = true });
            }
            else foreach (var j in jugadores) if (M.Str(M.Get(j, "uid")) == botUid) j["listo"] = true;

            tx.Update(lobbyRef, new Dictionary<FieldPath, object>
            {
                [new FieldPath("jugadores")] = jugadores,
                [new FieldPath("participantes")] = FieldValue.ArrayUnion(botUid),
            });
            return true;
        }, cancellationToken: ct);
    }

    // ── Esperar arranque ──
    private async Task<bool> EsperarArranqueAsync(string lobbyId, CancellationToken ct)
    {
        var lobbyRef = _fs.Db.Collection("Partidas").Document(lobbyId);
        var limite = DateTime.UtcNow + _opt.MaxWaitStart;
        while (DateTime.UtcNow < limite)
        {
            ct.ThrowIfCancellationRequested();
            var snap = await lobbyRef.GetSnapshotAsync(ct);
            if (!snap.Exists) return false;
            var estado = M.Str(M.Get(M.Map(M.FromFs(snap.ToDictionary())), "estado"));
            if (estado == "en_curso") return true;
            if (estado == "finalizada") return false;
            await Task.Delay(_opt.PollInterval, ct);
        }
        return false;
    }

    // ── Bucle de partida ──
    private async Task BuclePartidaAsync(string lobbyId, string botUid, CancellationToken ct)
    {
        int ultimoTurnoJugado = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var estado = await _svc.LeerEstadoAsync(lobbyId);
            if (estado == null) return;
            if (M.Str(M.Get(estado, "estado")) == "finalizada") return;

            var turno = M.Int(M.Get(estado, "turnoActual"));
            if (M.List(M.Get(estado, "jugadoresEliminados")).Select(M.Str).Contains(botUid)) return;

            bool yaCerre = M.List(M.Get(estado, "cerradoPor")).Select(M.Str).Contains(botUid);
            if (turno > ultimoTurnoJugado && !yaCerre)
            {
                await Task.Delay(_opt.ThinkDelay, ct);
                var fresco = await _svc.LeerEstadoAsync(lobbyId) ?? estado;
                if (M.Str(M.Get(fresco, "estado")) == "finalizada") return;
                int turnoFresco = M.Int(M.Get(fresco, "turnoActual"));
                var cerradoFresco = M.List(M.Get(fresco, "cerradoPor")).Select(M.Str).ToHashSet();
                if (turnoFresco == turno && !cerradoFresco.Contains(botUid))
                {
                    await JugarTurnoAsync(lobbyId, botUid, turno, fresco, ct);
                    ultimoTurnoJugado = turno;
                }
            }
            await Task.Delay(_opt.PollInterval, ct);
        }
    }

    // ── Jugar un turno ──
    private async Task JugarTurnoAsync(
        string lobbyId, string botUid, int turno, Dictionary<string, object?> estado, CancellationToken ct)
    {
        var obeliscos = M.Map(M.Get(estado, "obeliscos"));
        var cuartel = M.Str(M.Get(obeliscos, botUid));
        var stats = M.Map(M.Get(estado, "statsPartida"));
        var miStat = M.Map(M.Get(stats, botUid));
        int energia = M.Int(M.Get(miStat, "energies"));
        var mano = M.List(M.Get(miStat, "mano")).Select(M.Str).Where(s => s != "").ToList();

        var (terreno, filas, columnas) = await CargarMapaAsync(estado, ct);
        var zona = ZonaDe(estado, botUid, cuartel, filas, columnas);
        var catalogo = await CargarCartasAsync(mano, ct);

        var ctx = new BotContext
        {
            Estado = estado,
            BotUid = botUid,
            Turno = turno,
            Cuartel = cuartel,
            Energia = energia,
            Mano = mano,
            CatalogoMano = catalogo,
            Zona = zona,
            Terreno = terreno,
            Filas = filas,
            Columnas = columnas,
        };

        BotMove jugada;
        try { jugada = _strategy.DecidirJugada(ctx); }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[WZ][bot {botUid}] estrategia falló, cierre seguro: {ex}");
            jugada = new BotMove { Celdas = ArrastrarEjercito(estado, botUid), ManoResultante = mano };
        }

        // Persistir energía/mano antes de cerrar (igual que el cliente).
        if (jugada.EnergiaGastada != 0 || !mano.SequenceEqual(jugada.ManoResultante))
        {
            try
            {
                await _svc.ActualizarStatsAsync(new StatsRequest
                {
                    LobbyId = lobbyId,
                    Uid = botUid,
                    EnergiesDelta = -jugada.EnergiaGastada,
                    Mano = jugada.ManoResultante,
                });
            }
            catch (Exception ex) { Console.Error.WriteLine($"[WZ][bot {botUid}] actualizarStats falló: {ex}"); }
        }

        var req = new CerrarTurnoRequest
        {
            LobbyId = lobbyId,
            Uid = botUid,
            Turno = turno,
            Celdas = JsonSerializer.SerializeToElement(jugada.Celdas),
            Acciones = JsonSerializer.SerializeToElement(jugada.Acciones),
        };
        var resp = await _svc.CerrarTurnoAsync(req);
        Log(botUid, $"turno {turno} cerrado (celdas={jugada.Celdas.Values.Sum(l => l.Count)}, " +
                    $"acciones={jugada.Acciones.Count}, resuelto={resp.Resuelto})");
    }

    // ── Helpers ──
    private static Dictionary<string, List<Dictionary<string, object?>>> ArrastrarEjercito(
        Dictionary<string, object?> estado, string botUid)
    {
        var celdas = new Dictionary<string, List<Dictionary<string, object?>>>();
        foreach (var (coord, raw) in M.Map(M.Get(estado, "tablero")))
            foreach (var cRaw in M.List(raw))
            {
                var carta = M.Map(cRaw);
                if (M.Str(M.Get(carta, "ownerUid")) != botUid) continue;
                if (!celdas.TryGetValue(coord, out var l)) { l = new(); celdas[coord] = l; }
                l.Add(new Dictionary<string, object?>(carta));
            }
        return celdas;
    }

    private async Task<Dictionary<string, Dictionary<string, object?>>> CargarCartasAsync(
        List<string> ids, CancellationToken ct)
    {
        var res = new Dictionary<string, Dictionary<string, object?>>();
        foreach (var id in ids.Distinct())
        {
            try
            {
                var snap = await _fs.Db.Collection("Cartas").Document(id).GetSnapshotAsync(ct);
                if (!snap.Exists) continue;
                var map = M.Map(M.FromFs(snap.ToDictionary()));
                map["id"] = id;
                res[id] = map;
            }
            catch (Exception ex) { Console.Error.WriteLine($"[WZ][bot] leer carta {id} falló: {ex}"); }
        }
        return res;
    }

    /// Terreno + dimensiones del mapa de la partida (cacheado por mapaId).
    private async Task<(Dictionary<string, string> terreno, int filas, int columnas)> CargarMapaAsync(
        Dictionary<string, object?> estado, CancellationToken ct)
    {
        var mapaId = M.Str(M.Get(estado, "mapaId"));
        int jugadores = M.List(M.Get(estado, "jugadores")).Count;
        var (filasDef, columnasDef) = DimensionesPreset(jugadores);

        if (mapaId == "") return (new(), filasDef, columnasDef);
        if (_mapas.TryGetValue(mapaId, out var cached)) return cached;

        var terreno = new Dictionary<string, string>();
        int filas = filasDef, columnas = columnasDef;
        try
        {
            var snap = await _fs.Db.Collection("Mapas").Document(mapaId).GetSnapshotAsync(ct);
            if (snap.Exists)
            {
                var data = M.Map(M.FromFs(snap.ToDictionary()));
                foreach (var (coord, val) in M.Map(M.Get(data, "terreno")))
                    terreno[coord] = M.Str(val);
                int f = M.Int(M.Get(data, "filas"));
                int c = M.Int(M.Get(data, "columnas"));
                if (f > 0) filas = f;
                if (c > 0) columnas = c;
            }
        }
        catch (Exception ex) { Console.Error.WriteLine($"[WZ][bot] leer mapa {mapaId} falló: {ex}"); }

        var result = (terreno, filas, columnas);
        _mapas[mapaId] = result;
        return result;
    }

    /// Zona del bot: de una unidad propia si la hay; si no, derivada del cuartel.
    private static string ZonaDe(
        Dictionary<string, object?> estado, string botUid, string cuartel, int filas, int columnas)
    {
        foreach (var (_, raw) in M.Map(M.Get(estado, "tablero")))
            foreach (var cRaw in M.List(raw))
            {
                var carta = M.Map(cRaw);
                if (M.Str(M.Get(carta, "ownerUid")) == botUid)
                {
                    var z = M.Str(M.Get(carta, "ownerZone"));
                    if (z != "") return z;
                }
            }
        if (cuartel.Length < 2) return "";
        int ri = char.ToUpperInvariant(cuartel[0]) - 'A';
        if (!int.TryParse(cuartel[1..], out int col)) return "";
        int ci = col - 1;
        bool n = ri <= 2, s = ri >= filas - 3, w = ci <= 2, e = ci >= columnas - 3;
        if (n && e) return "ne"; if (n && w) return "nw"; if (s && e) return "se"; if (s && w) return "sw";
        if (n) return "north"; if (s) return "south"; if (w) return "west"; if (e) return "east";
        return "";
    }

    private static (int filas, int columnas) DimensionesPreset(int jugadores) => jugadores switch
    {
        2 => (6, 10),
        6 => (10, 16),
        8 => (12, 18),
        _ => (8, 14),
    };

    private static void Log(string botUid, string msg) => Console.WriteLine($"[WZ][bot {botUid}] {msg}");
}