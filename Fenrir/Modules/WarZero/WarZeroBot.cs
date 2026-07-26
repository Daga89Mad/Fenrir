using System.Collections.Concurrent;
using System.Text.Json;
using Google.Cloud.Firestore;

// ─────────────────────────────────────────────────────────────────────────────
// WarZeroBot.cs
//
// Bot server-side para RELLENAR SALAS. Reutiliza WarZeroService y WarZeroFirestore;
// juega contra la misma lógica autoritativa que un humano.
//
// REGLA CLAVE: al cerrar turno cada jugador reemite SOLO sus propias cartas y el
// servidor fusiona las de todos. La estrategia SIEMPRE reemite todas las unidades
// propias (posición actual, movida o desplegada) o el ejército desaparecería.
//
// Estrategias:
//   · ReclutaStrategy   — arrastra ejército + despliega en el cuartel.
//   · EstrategaStrategy — (por defecto) despliega, FARMEA energía, CAZA unidades
//     enemigas que puede batir, ataca solo cuando gana, CONQUISTA coordinando
//     fuerza > umbral, y usa HABILIDADES (disparo/veneno/parálisis/escudo).
// ─────────────────────────────────────────────────────────────────────────────

public class WarZeroBotOptions
{
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(5);
    public TimeSpan ThinkDelay { get; set; } = TimeSpan.FromSeconds(3);
    public TimeSpan MaxWaitStart { get; set; } = TimeSpan.FromMinutes(15);
    public int MaxDeploysPorTurno { get; set; } = 2;
}

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

    // Mapa / farmeo
    public required Dictionary<string, string> Terreno { get; init; }   // coord -> land|sea|deepSea|amphibious
    public required int Filas { get; init; }
    public required int Columnas { get; init; }
    public required HashSet<string> IslaCentral { get; init; }          // celdas isla central (+7)
    public required Dictionary<string, List<string>> Continentes { get; init; } // obeliscoCoord -> celdas
    public required HashSet<string> Rayos { get; init; }                // celdas de rayo activas (+10)
}

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
// Recluta — arrastra ejército + despliega en el cuartel. Nunca bloquea.
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
// Estratega (equilibrado): farmear + cazar + atacar + conquistar + habilidades.
// ─────────────────────────────────────────────────────────────────────────────
public class EstrategaStrategy : IBotStrategy
{
    private readonly int _maxDeploys;
    private readonly int _maxAcciones;

    // Umbral de fuerza para conquistar un cuartel SIN defensor. Debe coincidir
    // con `Combate.DefensaObelisco` de WarZeroLogic.cs (allí es 80). Si en tu
    // build lo cambiaste, ajústalo también AQUÍ (único sitio).
    private const int UmbralCuartel = 80;

    public EstrategaStrategy(int maxDeploysPorTurno = 2, int maxAcciones = 3)
    {
        _maxDeploys = Math.Max(0, maxDeploysPorTurno);
        _maxAcciones = Math.Max(0, maxAcciones);
    }

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

        // coord -> uid dueño del cuartel
        var cuartelOwner = new Dictionary<string, string>();
        foreach (var (uid, cObj) in obeliscos) { var c = M.Str(cObj); if (c != "") cuartelOwner[c] = uid; }
        var cuartelCoords = cuartelOwner.Keys.ToHashSet();

        // Unidades propias y celdas con enemigos
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

        var enemyCuarteles = cuartelOwner
            .Where(kv => kv.Value != botUid && !eliminados.Contains(kv.Value))
            .Select(kv => kv.Key).ToHashSet();

        string? miCuartel = ctx.Cuartel != "" ? ctx.Cuartel : null;

        // farmValue(cell): energía por turno que daría pararse ahí.
        int Farm(string cell)
        {
            if (cuartelCoords.Contains(cell)) return 0;   // cuartel no farmea
            int v = 0;
            if (ctx.Rayos.Contains(cell)) v += 10;
            if (ctx.IslaCentral.Contains(cell)) v += 7;
            foreach (var (k, cells) in ctx.Continentes)
                if (cells.Contains(cell))
                {
                    var owner = cuartelOwner.GetValueOrDefault(k, "");
                    if (owner != "" && owner != botUid) v += 5;
                }
            return v;
        }

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

        // Despliegue (reserva ~35% para pelea/habilidades).
        int reserva = energia * 35 / 100;
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

        // ── Conquista coordinada de un cuartel sin defensor (suma fuerza > umbral) ──
        var destino = new Dictionary<string, string>();
        foreach (var u in ownUnits) destino[u.inst] = u.coord; // por defecto, quieto
        var asignada = new HashSet<string>();

        string? targetCuartel = MasCercano(miCuartel, enemyCuarteles, filas, columnas);
        if (targetCuartel != null && !CuartelDefendido(targetCuartel, cuartelOwner, botUid, enemyByCoord))
        {
            var llegan = ownUnits
                .Where(u => Alcanzables(u.coord, Mov(u.card), Tipo(u.card), terreno, filas, columnas).Contains(targetCuartel))
                .ToList();
            if (llegan.Sum(u => Fuerza(u.card)) > UmbralCuartel)
                foreach (var u in llegan) { destino[u.inst] = targetCuartel; asignada.Add(u.inst); }
        }

        // ── Movimiento individual del resto ──
        // Objetivos globales de caza/farmeo (celdas), para orientar el avance.
        foreach (var u in ownUnits)
        {
            if (asignada.Contains(u.inst)) continue;
            destino[u.inst] = DecidirMovimiento(
                u.coord, u.card, terreno, filas, columnas,
                enemyByCoord, enemyCuarteles, cuartelOwner, cuartelCoords, botUid, Farm);
        }

        // Colocar todas las unidades propias.
        foreach (var u in ownUnits)
            Place(destino[u.inst], new Dictionary<string, object?>(u.card));

        // ── Habilidades (solo unidades que NO se movieron) ──
        var acciones = new List<Dictionary<string, object?>>();
        foreach (var u in ownUnits)
        {
            if (acciones.Count >= _maxAcciones) break;
            if (destino[u.inst] != u.coord) continue;

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

        return new BotMove { Celdas = celdas, Acciones = acciones, ManoResultante = mano, EnergiaGastada = gastado };
    }

    // ── Decisión de movimiento de una unidad ──
    private string DecidirMovimiento(
        string coord, Dictionary<string, object?> card,
        Dictionary<string, string> terreno, int filas, int columnas,
        Dictionary<string, List<Dictionary<string, object?>>> enemyByCoord,
        HashSet<string> enemyCuarteles, Dictionary<string, string> cuartelOwner,
        HashSet<string> cuartelCoords, string botUid, Func<string, int> Farm)
    {
        int mov = Mov(card), tipo = Tipo(card);
        int myF = Fuerza(card), myD = Defensa(card);
        var reach = Alcanzables(coord, mov, tipo, terreno, filas, columnas);
        if (reach.Count == 0) return coord;

        // 1) ATACAR/CAZAR: mejor celda alcanzable con enemigos que ganamos, o
        //    cuartel sin defensor conquistable.
        string? mejorAtaque = null; int mejorValor = -1;
        foreach (var c in reach)
        {
            bool esCuartel = enemyCuarteles.Contains(c);
            if (enemyByCoord.ContainsKey(c))
            {
                if (!GanoAtacando(myF, myD, c, enemyByCoord, enemyCuarteles, cuartelOwner, botUid)) continue;
                int valor = enemyByCoord[c].Sum(Coste) + (esCuartel ? 1000 : 0);
                if (valor > mejorValor) { mejorValor = valor; mejorAtaque = c; }
            }
            else if (esCuartel && myF > UmbralCuartel) // cuartel enemigo vacío conquistable
            {
                if (1000 > mejorValor) { mejorValor = 1000; mejorAtaque = c; }
            }
        }
        if (mejorAtaque != null) return mejorAtaque;

        // Celdas a las que es seguro/legal moverse (sin pelea perdida, sin pisar
        // un cuartel que no podemos tomar).
        bool Segura(string c)
        {
            if (enemyCuarteles.Contains(c))
            {
                bool defend = CuartelDefendido(c, cuartelOwner, botUid, enemyByCoord);
                if (!defend) return myF > UmbralCuartel;               // vacío: solo si conquistamos
                return GanoAtacando(myF, myD, c, enemyByCoord, enemyCuarteles, cuartelOwner, botUid);
            }
            if (cuartelCoords.Contains(c) && cuartelOwner.GetValueOrDefault(c) == botUid) return true; // mi cuartel
            if (enemyByCoord.ContainsKey(c))
                return GanoAtacando(myF, myD, c, enemyByCoord, enemyCuarteles, cuartelOwner, botUid);
            return true;
        }
        var seguras = reach.Where(Segura).ToList();
        if (seguras.Count == 0) return coord;

        // 2) FARMEAR: si alguna celda segura da energía, ir a la de mayor farmeo.
        var conFarm = seguras.Where(c => Farm(c) > 0).ToList();
        if (conFarm.Count > 0)
            return conFarm.OrderByDescending(Farm)
                          .ThenBy(c => DistObjetivo(c, coord, enemyByCoord, enemyCuarteles, filas, columnas, myF, myD, cuartelOwner, botUid))
                          .First();

        // 3) Sin farmeo a mano: moverse hacia un OBJETIVO (caza > farmeo lejano > cuartel).
        string? objetivo = ObjetivoGlobal(coord, enemyByCoord, enemyCuarteles, filas, columnas, myF, myD, cuartelOwner, botUid, Farm);
        if (objetivo == null) return coord;
        int distActual = Manhattan(coord, objetivo, filas, columnas);
        string mejor = coord; int mejorDist = distActual;
        foreach (var c in seguras)
        {
            int d = Manhattan(c, objetivo, filas, columnas);
            if (d < mejorDist) { mejorDist = d; mejor = c; }
        }
        return mejor;
    }

    // Objetivo hacia el que orientarse: enemigo batible más cercano; si no, mejor
    // región de farmeo; si no, cuartel enemigo más cercano.
    private string? ObjetivoGlobal(
        string from, Dictionary<string, List<Dictionary<string, object?>>> enemyByCoord,
        HashSet<string> enemyCuarteles, int filas, int columnas, int myF, int myD,
        Dictionary<string, string> cuartelOwner, string botUid, Func<string, int> Farm)
    {
        // a) enemigo batible más cercano (caza)
        string? mejorEnemigo = null; int mejorD = int.MaxValue;
        foreach (var c in enemyByCoord.Keys)
        {
            if (!GanoAtacando(myF, myD, c, enemyByCoord, enemyCuarteles, cuartelOwner, botUid)) continue;
            int d = Manhattan(from, c, filas, columnas);
            if (d < mejorD) { mejorD = d; mejorEnemigo = c; }
        }
        if (mejorEnemigo != null) return mejorEnemigo;

        // b) mejor celda de farmeo del tablero (más cercana con farmeo alto)
        // (recorremos continentes/isla/rayos ya reflejados en Farm; muestreamos
        //  las celdas enemigas y cuarteles como anclas no sirve, así que usamos
        //  las celdas de farmeo conocidas vía enemyCuarteles-adyacentes no; en su
        //  lugar orientamos al cuartel enemigo, que suele estar rodeado de su
        //  continente farmeable.)
        var cuartel = MasCercano(from, enemyCuarteles, filas, columnas);
        return cuartel;
    }

    private int DistObjetivo(
        string c, string from, Dictionary<string, List<Dictionary<string, object?>>> enemyByCoord,
        HashSet<string> enemyCuarteles, int filas, int columnas, int myF, int myD,
        Dictionary<string, string> cuartelOwner, string botUid)
    {
        var obj = MasCercano(from, enemyCuarteles, filas, columnas);
        return obj == null ? 0 : Manhattan(c, obj, filas, columnas);
    }

    // ── Combate ──
    private bool GanoAtacando(
        int myF, int myD, string coord,
        Dictionary<string, List<Dictionary<string, object?>>> enemyByCoord,
        HashSet<string> enemyCuarteles, Dictionary<string, string> cuartelOwner, string botUid)
    {
        if (!enemyByCoord.TryGetValue(coord, out var enemigos) || enemigos.Count == 0)
            return !enemyCuarteles.Contains(coord) || myF > UmbralCuartel;
        int fe = enemigos.Sum(Fuerza), de = enemigos.Sum(Defensa);
        if (enemyCuarteles.Contains(coord) && CuartelDefendido(coord, cuartelOwner, botUid, enemyByCoord))
            de += UmbralCuartel; // el cuartel defendido suma +80 de defensa al dueño
        return (myF - de) > (fe - myD); // poder neto estrictamente mayor
    }

    private static bool CuartelDefendido(
        string coord, Dictionary<string, string> cuartelOwner, string botUid,
        Dictionary<string, List<Dictionary<string, object?>>> enemyByCoord)
    {
        var dueno = cuartelOwner.GetValueOrDefault(coord, "");
        if (dueno == "" || dueno == botUid) return false;
        if (!enemyByCoord.TryGetValue(coord, out var cartas)) return false;
        return cartas.Any(c => M.Str(M.Get(c, "ownerUid")) == dueno);
    }

    // ── Objetivos de habilidad ──
    private List<string> ElegirObjetivos(
        Hab hab, string origen, int filas, int columnas,
        Dictionary<string, List<Dictionary<string, object?>>> enemyByCoord,
        HashSet<string> enemyCuarteles, string? miCuartel)
    {
        if (hab.Efecto == Efe.Escudo)
        {
            bool amenaza = Vecinas(origen, filas, columnas).Any(enemyByCoord.ContainsKey);
            if (!amenaza) return new();
            return new() { hab.Rango == Rng.Propia ? origen : (miCuartel ?? origen) };
        }

        bool EnRango(string c) => hab.Rango switch
        {
            Rng.Frontera => Manhattan(origen, c, filas, columnas) == 1,
            Rng.Radio7 => Manhattan(origen, c, filas, columnas) <= 7,
            Rng.Cualquiera => c != origen,
            Rng.Propia => c == origen,
            _ => false,
        };

        return enemyByCoord.Keys
            .Where(EnRango)
            .Where(c => !hab.ExcluyeCG || !enemyCuarteles.Contains(c))
            .OrderByDescending(c => enemyByCoord[c].Sum(Coste))
            .ToList();
    }

    // ── Movimiento (BFS ortogonal, réplica del cliente) ──
    private static HashSet<string> Alcanzables(
        string from, int mov, int tipo, Dictionary<string, string> terreno, int filas, int columnas)
    {
        var res = new HashSet<string>();
        if (mov <= 0 || Parse(from) == null) return res;
        var visited = new Dictionary<string, int> { [from] = 0 };
        var queue = new Queue<(string, int)>();
        queue.Enqueue((from, 0));
        var deltas = new (int, int)[] { (-1, 0), (1, 0), (0, -1), (0, 1) };
        while (queue.Count > 0)
        {
            var (coord, steps) = queue.Dequeue();
            if (steps >= mov) continue;
            var pos = Parse(coord); if (pos == null) continue;
            var (ri, ci) = pos.Value;
            foreach (var (dr, dc) in deltas)
            {
                int nr = ri + dr, nc = ci + dc;
                if (nr < 0 || nr >= filas || nc < 0 || nc >= columnas) continue;
                var nCoord = Label(nr, nc);
                int ns = steps + 1;
                if (visited.GetValueOrDefault(nCoord, 999) <= ns) continue;
                if (!CanTraverse(nCoord, tipo, terreno)) continue;
                visited[nCoord] = ns;
                if (nCoord != from && CanLand(nCoord, tipo, terreno)) res.Add(nCoord);
                if (ns < mov) queue.Enqueue((nCoord, ns));
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
    private static string Terr(string coord, Dictionary<string, string> t) => t.TryGetValue(coord, out var v) ? v : "land";
    private static bool CanTraverse(string coord, int tipo, Dictionary<string, string> t) => tipo switch
    {
        1 => Terr(coord, t) is "land" or "amphibious",
        3 => Terr(coord, t) is "sea" or "deepSea" or "amphibious",
        _ => true,
    };
    private static bool CanLand(string coord, int tipo, Dictionary<string, string> t) => tipo switch
    {
        1 or 2 => Terr(coord, t) is "land" or "amphibious",
        3 => Terr(coord, t) is "sea" or "deepSea" or "amphibious",
        _ => true,
    };
    private static int Manhattan(string a, string b, int filas, int columnas)
    {
        var pa = Parse(a); var pb = Parse(b);
        if (pa == null || pb == null) return int.MaxValue;
        return Math.Abs(pa.Value.ri - pb.Value.ri) + Math.Abs(pa.Value.ci - pb.Value.ci);
    }
    private static IEnumerable<string> Vecinas(string coord, int filas, int columnas)
    {
        var p = Parse(coord); if (p == null) yield break;
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
        foreach (var c in cands) { int d = Manhattan(from, c, filas, columnas); if (d < mejorD) { mejorD = d; mejor = c; } }
        return mejor;
    }

    // ── Stats ──
    private static int Fuerza(Dictionary<string, object?> c) => M.Int(M.Get(c, "Fuerza", "fuerza"));
    private static int Defensa(Dictionary<string, object?> c) => M.Int(M.Get(c, "Defensa", "defensa"));
    private static int Coste(Dictionary<string, object?> c) => M.Int(M.Get(c, "Coste", "coste"));
    private static int Mov(Dictionary<string, object?> c) => M.Int(M.Get(c, "Movimiento", "movimiento"));
    private static int Tipo(Dictionary<string, object?> c) { int t = M.Int(M.Get(c, "Tipo", "tipo")); return t <= 0 ? 1 : t; }
    private static bool EnEnfriamiento(Dictionary<string, object?> c, int turno)
    {
        int enf = M.Int(M.Get(c, "EnfriamientoHabilidad", "enfriamientoHabilidad"));
        if (enf <= 0) return false;
        var ultimo = M.Get(c, "UltimoUsoHabilidad", "ultimoUsoHabilidad");
        if (ultimo == null) return false;
        return (turno - M.Int(ultimo)) < enf;
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

    private readonly record struct MapaInfo(
        Dictionary<string, string> Terreno, int Filas, int Columnas,
        HashSet<string> IslaCentral, Dictionary<string, List<string>> Continentes);

    private readonly ConcurrentDictionary<string, MapaInfo> _mapas = new();

    public WarZeroBot(
        WarZeroFirestore fs, WarZeroService svc,
        WarZeroBotOptions? options = null, IBotStrategy? strategy = null)
    {
        _fs = fs; _svc = svc;
        _opt = options ?? new WarZeroBotOptions();
        _strategy = strategy ?? new EstrategaStrategy(_opt.MaxDeploysPorTurno);
    }

    public async Task RunForLobbyAsync(string lobbyId, string botUid, string botAlias, CancellationToken ct = default)
    {
        try
        {
            Log(botUid, $"entrando a rellenar la sala {lobbyId}");
            if (!await UnirseYMarcarListoAsync(lobbyId, botUid, botAlias, ct)) { Log(botUid, "no pude unirme"); return; }
            if (!await EsperarArranqueAsync(lobbyId, ct)) { Log(botUid, "la sala no arrancó; me retiro"); return; }
            await _svc.EntrarAsync(new EntrarRequest { LobbyId = lobbyId, Uid = botUid });
            Log(botUid, "dentro de la partida; empiezo a jugar");
            await BuclePartidaAsync(lobbyId, botUid, ct);
            Log(botUid, "partida terminada");
        }
        catch (OperationCanceledException) { Log(botUid, "cancelado"); }
        catch (Exception ex) { Console.Error.WriteLine($"[WZ][bot {botUid}] error fatal: {ex}"); }
    }

    private async Task<bool> UnirseYMarcarListoAsync(string lobbyId, string botUid, string botAlias, CancellationToken ct)
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

    private async Task JugarTurnoAsync(string lobbyId, string botUid, int turno, Dictionary<string, object?> estado, CancellationToken ct)
    {
        var obeliscos = M.Map(M.Get(estado, "obeliscos"));
        var cuartel = M.Str(M.Get(obeliscos, botUid));
        var miStat = M.Map(M.Get(M.Map(M.Get(estado, "statsPartida")), botUid));
        int energia = M.Int(M.Get(miStat, "energies"));
        var mano = M.List(M.Get(miStat, "mano")).Select(M.Str).Where(s => s != "").ToList();

        var mapa = await CargarMapaAsync(estado, ct);
        var rayos = LeerRayos(estado);
        var zona = ZonaDe(estado, botUid, cuartel, mapa.Filas, mapa.Columnas);
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
            Terreno = mapa.Terreno,
            Filas = mapa.Filas,
            Columnas = mapa.Columnas,
            IslaCentral = mapa.IslaCentral,
            Continentes = mapa.Continentes,
            Rayos = rayos,
        };

        BotMove jugada;
        try { jugada = _strategy.DecidirJugada(ctx); }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[WZ][bot {botUid}] estrategia falló, cierre seguro: {ex}");
            jugada = new BotMove { Celdas = ArrastrarEjercito(estado, botUid), ManoResultante = mano };
        }

        if (jugada.EnergiaGastada != 0 || !mano.SequenceEqual(jugada.ManoResultante))
        {
            try
            {
                await _svc.ActualizarStatsAsync(new StatsRequest
                { LobbyId = lobbyId, Uid = botUid, EnergiesDelta = -jugada.EnergiaGastada, Mano = jugada.ManoResultante });
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
        Log(botUid, $"turno {turno} cerrado (celdas={jugada.Celdas.Values.Sum(l => l.Count)}, acciones={jugada.Acciones.Count}, resuelto={resp.Resuelto})");
    }

    private static HashSet<string> LeerRayos(Dictionary<string, object?> estado)
    {
        var res = new HashSet<string>();
        var rayosRaw = M.Get(estado, "rayos");
        if (rayosRaw is System.Collections.IEnumerable en && rayosRaw is not string)
            foreach (var r in en)
            {
                var c = M.Str(M.Get(M.Map(r), "coord"));
                if (c != "") res.Add(c);
            }
        if (res.Count == 0)
        {
            var uno = M.Map(M.Get(estado, "rayo"));
            var c = M.Str(M.Get(uno, "coord"));
            if (c != "") res.Add(c);
        }
        return res;
    }

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

    private async Task<Dictionary<string, Dictionary<string, object?>>> CargarCartasAsync(List<string> ids, CancellationToken ct)
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

    private async Task<MapaInfo> CargarMapaAsync(Dictionary<string, object?> estado, CancellationToken ct)
    {
        var mapaId = M.Str(M.Get(estado, "mapaId"));
        int jugadores = M.List(M.Get(estado, "jugadores")).Count;
        var (filasDef, columnasDef) = DimensionesPreset(jugadores);
        if (mapaId == "")
            return new MapaInfo(new(), filasDef, columnasDef, new(), new());
        if (_mapas.TryGetValue(mapaId, out var cached)) return cached;

        var terreno = new Dictionary<string, string>();
        var isla = new HashSet<string>();
        var continentes = new Dictionary<string, List<string>>();
        int filas = filasDef, columnas = columnasDef;
        try
        {
            var snap = await _fs.Db.Collection("Mapas").Document(mapaId).GetSnapshotAsync(ct);
            if (snap.Exists)
            {
                var data = M.Map(M.FromFs(snap.ToDictionary()));
                foreach (var (coord, val) in M.Map(M.Get(data, "terreno"))) terreno[coord] = M.Str(val);
                foreach (var c in M.List(M.Get(data, "islaCentral")).Select(M.Str)) if (c != "") isla.Add(c);
                foreach (var (k, v) in M.Map(M.Get(data, "continentes")))
                    continentes[k] = M.List(v).Select(M.Str).Where(s => s != "").ToList();
                int f = M.Int(M.Get(data, "filas")), c2 = M.Int(M.Get(data, "columnas"));
                if (f > 0) filas = f;
                if (c2 > 0) columnas = c2;
            }
        }
        catch (Exception ex) { Console.Error.WriteLine($"[WZ][bot] leer mapa {mapaId} falló: {ex}"); }

        var info = new MapaInfo(terreno, filas, columnas, isla, continentes);
        _mapas[mapaId] = info;
        return info;
    }

    private static string ZonaDe(Dictionary<string, object?> estado, string botUid, string cuartel, int filas, int columnas)
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