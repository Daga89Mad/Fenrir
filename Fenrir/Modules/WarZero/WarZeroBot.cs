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
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(60);
    public TimeSpan ThinkDelay { get; set; } = TimeSpan.FromSeconds(3);
    public TimeSpan MaxWaitStart { get; set; } = TimeSpan.FromMinutes(15);
    public int MaxDeploysPorTurno { get; set; } = 3;

    /// Nº máximo de acciones de HABILIDAD (de unidades en tablero) por turno.
    public int MaxAcciones { get; set; } = 4;

    /// Nº máximo de CARTAS DE ACCIÓN (jugadas desde la mano) por turno.
    public int MaxAccionesCarta { get; set; } = 2;

    /// Nº máximo de unidades propias que el bot mantiene sobre su cuartel.
    /// Pocas a propósito: un AoE a distancia puede barrer un cuartel apilado.
    public int MaxDefensoresCuartel { get; set; } = 2;

    /// Probabilidad [0..1] de intentar INTERCEPTAR (adivinar el avance rival)
    /// en vez de ir directo a la casilla actual de la carta enemiga. 1.0 = siempre
    /// que exista una intercepción mejor; valores bajos = más pasivo.
    public double ProbCazaPredictiva { get; set; } = 1.0;

    /// Si la carta enemiga tiene movimiento MAYOR que esto, su avance es
    /// demasiado impredecible: se persigue como siempre (sin interceptar).
    public int MovMaxPredecible { get; set; } = 5;

    /// Nº máximo de EVOLUCIONES por turno. Evolucionar cuesta energía y la carta
    /// evolucionada no puede moverse ese turno (y una que se movió no evoluciona).
    public int MaxEvolucionesPorTurno { get; set; } = 2;

    /// Si el bot compra GENERALES (cartas especiales) en su cuartel. Cada general
    /// solo puede comprarse UNA vez por partida; si muere, no vuelve.
    public bool ComprarGenerales { get; set; } = true;

    /// Ejército del bot (1..4) si no está definido en su documento de `Bots`.
    /// 0 = derivarlo de forma estable a partir del uid del bot.
    public int EjercitoPorDefecto { get; set; } = 0;
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

    // ── Evoluciones y generales ──
    /// Ejército del bot (1..4). Determina qué generales puede comprar.
    public int EjercitoId { get; init; }

    /// Cartas de EVOLUCIÓN referenciadas por las unidades propias en tablero:
    /// idEvolucion -> datos de la carta resultante.
    public Dictionary<string, Dictionary<string, object?>> Evoluciones { get; init; } = new();

    /// GENERALES (cartas especiales, Condicion==5) del ejército del bot que
    /// AÚN no ha comprado esta partida.
    public List<Dictionary<string, object?>> GeneralesDisponibles { get; init; } = new();
}

public class BotMove
{
    public Dictionary<string, List<Dictionary<string, object?>>> Celdas { get; init; } = new();
    public List<Dictionary<string, object?>> Acciones { get; init; } = new();
    public List<string> ManoResultante { get; init; } = new();
    public int EnergiaGastada { get; init; }

    /// Id del general (carta especial) comprado este turno, si lo hubo. Se
    /// persiste con arrayUnion en `especialesCompradas`: por eso cada general
    /// solo puede comprarse una vez por partida (si muere, no vuelve).
    public string? EspecialComprada { get; init; }
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
                if (M.Int(M.Get(cartaBase, "Condicion", "condicion")) == 4) continue; // acción: no se despliega
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
    private readonly int _maxAccionesCarta;
    private readonly int _maxDefensoresCuartel;
    private readonly double _probPrediccion;
    private readonly int _movMaxPredecible;
    private readonly Random _rng = new();

    // Memoria entre turnos: instanceId de cada carta enemiga -> su coord el turno
    // ANTERIOR. Permite estimar su vector de avance (dead reckoning) y predecir a
    // dónde irá el próximo turno. La estrategia vive toda la partida, así que este
    // estado persiste entre llamadas a DecidirJugada.
    private Dictionary<string, string> _ultimaPosEnemigo = new();

    // Umbral de fuerza para conquistar un cuartel SIN defensor y bono de defensa
    // que un cuartel DEFENDIDO otorga a su dueño. DEBE coincidir con
    // `Combate.DefensaObelisco` de WarZeroLogic.cs (allí es 40). Antes estaba a 80
    // (el doble): eso hacía que el bot creyera los cuarteles inconquistables y no
    // se atreviera a rematar partidas. Corregido a 40 para que ataque cuando toca.
    private const int UmbralCuartel = 40;

    private readonly int _maxEvoluciones;
    private readonly bool _comprarGenerales;

    public EstrategaStrategy(WarZeroBotOptions opt)
        : this(opt.MaxDeploysPorTurno, opt.MaxAcciones, opt.MaxAccionesCarta,
               opt.MaxDefensoresCuartel, opt.ProbCazaPredictiva, opt.MovMaxPredecible,
               opt.MaxEvolucionesPorTurno, opt.ComprarGenerales)
    { }

    public EstrategaStrategy(
        int maxDeploysPorTurno = 3, int maxAcciones = 4, int maxAccionesCarta = 2,
        int maxDefensoresCuartel = 2, double probCazaPredictiva = 1.0, int movMaxPredecible = 5,
        int maxEvolucionesPorTurno = 2, bool comprarGenerales = true)
    {
        _maxEvoluciones = Math.Max(0, maxEvolucionesPorTurno);
        _comprarGenerales = comprarGenerales;
        _maxDeploys = Math.Max(0, maxDeploysPorTurno);
        _maxAcciones = Math.Max(0, maxAcciones);
        _maxAccionesCarta = Math.Max(0, maxAccionesCarta);
        _maxDefensoresCuartel = Math.Max(1, maxDefensoresCuartel);
        _probPrediccion = Math.Clamp(probCazaPredictiva, 0.0, 1.0);
        _movMaxPredecible = Math.Max(0, movMaxPredecible);
    }

    private enum Efe { Disparo, Veneno, Paralisis, Escudo, Potenciacion }
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
        // Potenciaciones (buff a una unidad PROPIA): fuerza 15-17, defensa 18-20,
        // movimiento 21-23, en rango cercano/medio/lejano.
        [15] = new(Efe.Potenciacion, Rng.Frontera, 1, false),
        [16] = new(Efe.Potenciacion, Rng.Radio7, 1, false),
        [17] = new(Efe.Potenciacion, Rng.Cualquiera, 1, false),
        [18] = new(Efe.Potenciacion, Rng.Frontera, 1, false),
        [19] = new(Efe.Potenciacion, Rng.Radio7, 1, false),
        [20] = new(Efe.Potenciacion, Rng.Cualquiera, 1, false),
        [21] = new(Efe.Potenciacion, Rng.Frontera, 1, false),
        [22] = new(Efe.Potenciacion, Rng.Radio7, 1, false),
        [23] = new(Efe.Potenciacion, Rng.Cualquiera, 1, false),
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

        // Mi cuartel: el declarado en ctx o, si no, el que figure en `obeliscos`.
        string? miCuartel = ctx.Cuartel != "" ? ctx.Cuartel
            : cuartelOwner.FirstOrDefault(kv => kv.Value == botUid).Key;
        if (string.IsNullOrEmpty(miCuartel)) miCuartel = null;

        // Celdas de MI continente (para detectar intrusos y defender el territorio).
        var miContinente = new HashSet<string>();
        if (miCuartel != null && ctx.Continentes.TryGetValue(miCuartel, out var celdasCont))
            miContinente = celdasCont.ToHashSet();

        // ── PREDICCIÓN DE AVANCE ENEMIGO (por celda) ───────────────────────────
        var misCoords = ownUnits.Select(u => u.coord).ToList();
        var atractoresEnergia = new HashSet<string>(ctx.Rayos);
        atractoresEnergia.UnionWith(ctx.IslaCentral);
        var predEnemigo = new Dictionary<string, string>();
        var posActualEnemigo = new Dictionary<string, string>();
        foreach (var (coord, cartas) in enemyByCoord)
        {
            foreach (var e in cartas)
            {
                var iid = M.Str(M.Get(e, "instanceId"));
                if (iid != "") posActualEnemigo[iid] = coord;
            }
            var rep = cartas.OrderByDescending(Mov).First();
            if (Mov(rep) > _movMaxPredecible) continue;
            var repIid = M.Str(M.Get(rep, "instanceId"));
            string? prev = repIid != "" && _ultimaPosEnemigo.TryGetValue(repIid, out var pv) ? pv : null;
            predEnemigo[coord] = PredecirAvance(coord, rep, prev, terreno, filas, columnas,
                miCuartel, misCoords, atractoresEnergia);
        }
        _ultimaPosEnemigo = posActualEnemigo;

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
        var acciones = new List<Dictionary<string, object?>>();

        // ¿Hay enemigos que amenazan mi cuartel este turno o el siguiente?
        bool amenazado = miCuartel != null &&
            CuartelAmenazado(miCuartel, enemyByCoord, terreno, filas, columnas);

        // INTRUSOS: celdas con enemigos DENTRO de mi continente (excluye mi cuartel).
        // Si hay intrusos, hay que CONTENERLOS (defensa de territorio, punto 3).
        var intrusos = enemyByCoord.Keys
            .Where(c => miContinente.Contains(c) && c != miCuartel)
            .ToHashSet();
        bool continenteInvadido = intrusos.Count > 0;

        // ── FASE 0: DESPLIEGUE (ANTES de mover) ────────────────────────────────
        // CLAVE: una carta desplegada CAE en el cuartel pero PUEDE moverse el mismo
        // turno. Por eso se despliega ANTES de decidir movimientos y cada unidad
        // nueva se añade al pool `ownUnits` (con coord = mi cuartel): así sale a
        // jugar ya (farmear, cazar, asaltar) en vez de quedarse parada en casa.
        var recienInst = new HashSet<string>();
        string? especialComprada = null;
        bool remontada = ownUnits.Count <= 1; // casi sin tablero: recuperar presencia ya

        // Añade una unidad recién desplegada al pool movible (en el cuartel).
        void DesplegarUnidad(Dictionary<string, object?> baseCard, string id)
        {
            var nu = NuevaUnidad(baseCard, id, botUid, zona);
            var inst = M.Str(M.Get(nu, "instanceId"));
            ownUnits.Add((miCuartel!, nu, inst));
            recienInst.Add(inst);
        }

        if (miCuartel != null && ctx.Cuartel != "")
        {
            // Reserva de energía para habilidades / cartas de acción. Se relaja a 0
            // si hay urgencia (amenaza, invasión de continente o remontada).
            int reserva = (amenazado || continenteInvadido || remontada) ? 0 : energia * 20 / 100;

            // (0a) COMPRA DE GENERAL (carta especial): fuerte atacando y defendiendo.
            //      Uno por partida (si muere no vuelve) → como mucho uno por turno.
            if (_comprarGenerales && ctx.GeneralesDisponibles.Count > 0)
            {
                var candidato = ctx.GeneralesDisponibles
                    .Where(g => Coste(g) <= energia)
                    .OrderByDescending(g => Fuerza(g) + Defensa(g))
                    .FirstOrDefault();
                // Sin urgencia, exigir cierto colchón para no vaciar la energía.
                bool permite = candidato != null &&
                    (amenazado || continenteInvadido || energia >= Coste(candidato) * 3 / 2);
                if (candidato != null && permite)
                {
                    int coste = Coste(candidato);
                    var gid = M.Str(M.Get(candidato, "id"));
                    DesplegarUnidad(candidato, gid);
                    energia -= coste; gastado += coste; especialComprada = gid;
                    Console.WriteLine($"[WZ][bot {botUid}] COMPRA GENERAL {M.Str(M.Get(candidato, "Nombre", "nombre"))} ({gid}) por {coste}");
                }
            }

            // (0b) DESPLIEGUE de unidades: las MÁS POTENTES primero, hasta _maxDeploys.
            //      Ya NO se limita por defensores del cuartel: las cartas salen a
            //      jugar en la Fase 1. Se conserva una reserva salvo urgencia.
            var ordenadas = mano
                .Where(id => ctx.CatalogoMano.ContainsKey(id) && !EsAccion(ctx.CatalogoMano[id]))
                .OrderByDescending(id =>
                {
                    var c = ctx.CatalogoMano[id];
                    return Fuerza(c) + Defensa(c);
                })
                .ToList();
            foreach (var id in ordenadas)
            {
                if (desplegadas >= _maxDeploys) break;
                var baseCard = ctx.CatalogoMano[id];
                int coste = M.Int(M.Get(baseCard, "Coste", "coste"));
                if (energia - coste < reserva) continue;
                DesplegarUnidad(baseCard, id);
                energia -= coste; gastado += coste; desplegadas++;
                mano.Remove(id);
            }
        }

        // Mis coords tras desplegar (para las cartas de acción de potenciación).
        misCoords = ownUnits.Select(u => u.coord).ToList();

        // ── FASE 1: destinos de movimiento (sobre TODAS mis unidades) ──────────
        var destino = new Dictionary<string, string>();
        foreach (var u in ownUnits) destino[u.inst] = u.coord; // por defecto, quieto
        var asignada = new HashSet<string>();

        // (a) ASALTO EN MANADA a un cuartel enemigo (DEFENDIDO o no). Reúne el grupo
        //     MÍNIMO (más fuertes primero) que, sumando fuerzas, GANA el combate del
        //     cuartel (el defensor suma +UmbralCuartel). Prioriza el más cercano.
        //     Solo si NO estamos amenazados (si nos atacan, defender es prioritario).
        if (!amenazado)
            foreach (var cuartelObj in enemyCuarteles
                        .OrderBy(c => miCuartel == null ? 0 : Manhattan(miCuartel, c, filas, columnas)))
            {
                var llegan = ownUnits
                    .Where(u => !asignada.Contains(u.inst))
                    .Where(u => Alcanzables(u.coord, Mov(u.card), Tipo(u.card), terreno, filas, columnas).Contains(cuartelObj))
                    .OrderByDescending(u => Fuerza(u.card) + Defensa(u.card))
                    .ToList();
                if (llegan.Count == 0) continue;

                var grupo = new List<(string coord, Dictionary<string, object?> card, string inst)>();
                foreach (var u in llegan)
                {
                    grupo.Add(u);
                    if (GanaGrupo(grupo.Sum(g => Fuerza(g.card)), grupo.Sum(g => Defensa(g.card)),
                                  cuartelObj, enemyByCoord, enemyCuarteles, cuartelOwner, botUid)) break;
                }
                if (!GanaGrupo(grupo.Sum(g => Fuerza(g.card)), grupo.Sum(g => Defensa(g.card)),
                               cuartelObj, enemyByCoord, enemyCuarteles, cuartelOwner, botUid))
                    continue; // ni todas juntas rematan el cuartel: no malgastar el ataque

                foreach (var u in grupo) { destino[u.inst] = cuartelObj; asignada.Add(u.inst); }
                Console.WriteLine($"[WZ][bot {botUid}] ASALTO CUARTEL en manada: {grupo.Count} → {cuartelObj}");
            }

        // (aDef) CONTENER INTRUSOS: enemigos dentro de MI continente. Reúne el grupo
        //        mínimo que los bate y los envía. Es la respuesta defensiva que
        //        faltaba: cuando un rival entra con varias cartas, se le planta cara.
        foreach (var intruso in intrusos
                    .OrderByDescending(c => enemyByCoord[c].Sum(Coste)))
        {
            var llegan = ownUnits
                .Where(u => !asignada.Contains(u.inst))
                .Where(u => Alcanzables(u.coord, Mov(u.card), Tipo(u.card), terreno, filas, columnas).Contains(intruso))
                .OrderByDescending(u => Fuerza(u.card) + Defensa(u.card))
                .ToList();
            if (llegan.Count == 0) continue;

            var grupo = new List<(string coord, Dictionary<string, object?> card, string inst)>();
            foreach (var u in llegan)
            {
                grupo.Add(u);
                if (GanaGrupo(grupo.Sum(g => Fuerza(g.card)), grupo.Sum(g => Defensa(g.card)),
                              intruso, enemyByCoord, enemyCuarteles, cuartelOwner, botUid)) break;
            }
            if (!GanaGrupo(grupo.Sum(g => Fuerza(g.card)), grupo.Sum(g => Defensa(g.card)),
                           intruso, enemyByCoord, enemyCuarteles, cuartelOwner, botUid))
                continue; // aún no lo batimos; el movimiento individual convergerá a él

            foreach (var u in grupo) { destino[u.inst] = intruso; asignada.Add(u.inst); }
            Console.WriteLine($"[WZ][bot {botUid}] CONTIENE INTRUSO: {grupo.Count} → {intruso}");
        }

        // (a2) CAZA EN GRUPO de stacks enemigos valiosos (fuera del cuartel y que no
        //      sean ya intrusos ni cuarteles). Varias unidades que en solitario
        //      perderían pueden ganar JUNTAS: se concentra el mínimo que gana.
        if (!amenazado)
        {
            var objetivosGrupo = enemyByCoord.Keys
                .Where(c => !enemyCuarteles.Contains(c) && !intrusos.Contains(c))
                .OrderByDescending(c => enemyByCoord[c].Sum(Coste))
                .Take(3);
            int cazasLanzadas = 0;
            foreach (var celdaObj in objetivosGrupo)
            {
                if (cazasLanzadas >= 2) break; // máx. 2 asaltos coordinados por turno
                var candidatas = ownUnits
                    .Where(u => !asignada.Contains(u.inst))
                    .Where(u => Alcanzables(u.coord, Mov(u.card), Tipo(u.card), terreno, filas, columnas).Contains(celdaObj))
                    .OrderByDescending(u => Fuerza(u.card) + Defensa(u.card))
                    .ToList();
                if (candidatas.Count < 2) continue; // en solitario ya lo cubre DecidirMovimiento

                var grupo = new List<(string coord, Dictionary<string, object?> card, string inst)>();
                foreach (var u in candidatas)
                {
                    grupo.Add(u);
                    if (GanaGrupo(grupo.Sum(g => Fuerza(g.card)), grupo.Sum(g => Defensa(g.card)),
                                  celdaObj, enemyByCoord, enemyCuarteles, cuartelOwner, botUid)) break;
                }
                if (!GanaGrupo(grupo.Sum(g => Fuerza(g.card)), grupo.Sum(g => Defensa(g.card)),
                               celdaObj, enemyByCoord, enemyCuarteles, cuartelOwner, botUid))
                    continue; // ni todas juntas ganan: no suicidarse

                foreach (var u in grupo) { destino[u.inst] = celdaObj; asignada.Add(u.inst); }
                cazasLanzadas++;
                Console.WriteLine($"[WZ][bot {botUid}] CAZA EN GRUPO: {grupo.Count} unidades → {celdaObj}");
            }
        }

        // (b) DEFENSA DEL CUARTEL: si está amenazado, ancla a los defensores más
        //     fuertes que ya están en él (incluidas cartas recién desplegadas),
        //     hasta el tope.
        if (amenazado && miCuartel != null)
            foreach (var u in ownUnits
                        .Where(u => u.coord == miCuartel && !asignada.Contains(u.inst))
                        .OrderByDescending(u => Fuerza(u.card) + Defensa(u.card))
                        .Take(_maxDefensoresCuartel))
            { destino[u.inst] = miCuartel; asignada.Add(u.inst); }

        // (c) MOVIMIENTO INDIVIDUAL del resto (caza / farmeo / avance, con caza
        //     predictiva). Las cartas recién desplegadas SALEN aquí hacia energía o
        //     frente. Si mi continente está invadido, las unidades de casa
        //     convergen hacia el intruso más cercano para contenerlo.
        foreach (var u in ownUnits)
        {
            if (asignada.Contains(u.inst)) continue;
            destino[u.inst] = DecidirMovimiento(
                u.coord, u.card, terreno, filas, columnas,
                enemyByCoord, enemyCuarteles, cuartelOwner, cuartelCoords, botUid, Farm, miCuartel,
                predEnemigo, atractoresEnergia, intrusos, miContinente);
        }

        // (d) ANTI-APILAMIENTO: no dejar más de _maxDefensoresCuartel unidades sobre
        //     mi cuartel (un AoE a distancia las barrería a todas).
        if (miCuartel != null)
        {
            var enMiCuartel = ownUnits
                .Where(u => destino[u.inst] == miCuartel)
                .OrderByDescending(u => Fuerza(u.card) + Defensa(u.card))
                .ToList();
            foreach (var u in enMiCuartel.Skip(_maxDefensoresCuartel))
                destino[u.inst] = ReubicarFueraDeCuartel(
                    u.coord, u.card, miCuartel, terreno, filas, columnas,
                    enemyByCoord, enemyCuarteles, cuartelOwner, botUid);
        }

        // (e) GUARNICIÓN MÍNIMA: si tras mover el cuartel quedaría vacío y aún hay
        //     enemigos en juego, retén en casa a la unidad más defensiva que ya
        //     estuviera allí (evita regalar el cuartel a un rush del rival).
        if (miCuartel != null && enemyByCoord.Count > 0 &&
            !ownUnits.Any(u => destino[u.inst] == miCuartel))
        {
            var guard = ownUnits
                .Where(u => u.coord == miCuartel)
                .OrderByDescending(u => Defensa(u.card) + Fuerza(u.card))
                .FirstOrDefault();
            if (guard.card != null) destino[guard.inst] = miCuartel;
        }

        // ── COLOCACIÓN + EVOLUCIONES ───────────────────────────────────────────
        // Una carta que se MOVIÓ no evoluciona y una recién desplegada tampoco
        // (acaba de entrar). Solo evolucionan las que se quedan en su celda.
        var evolucionadas = new HashSet<string>();
        int evos = 0;
        int reservaEvo = amenazado ? energia * 40 / 100 : 0;

        foreach (var u in ownUnits)
        {
            var destinoU = destino[u.inst];
            Dictionary<string, object?> aColocar = new(u.card);

            if (evos < _maxEvoluciones && destinoU == u.coord && !recienInst.Contains(u.inst))
            {
                var idEvo = M.Str(M.Get(u.card, "IdEvolucion", "idEvolucion"));
                int costeEvo = M.Int(M.Get(u.card, "Evolucion", "evolucion"));
                if (idEvo != "" && costeEvo > 0 && energia - costeEvo >= reservaEvo
                    && ctx.Evoluciones.TryGetValue(idEvo, out var evoCard)
                    && CanLand(u.coord, Tipo(evoCard), terreno)
                    && (Fuerza(evoCard) + Defensa(evoCard)) > (Fuerza(u.card) + Defensa(u.card)))
                {
                    var zonaU = M.Str(M.Get(u.card, "ownerZone"));
                    if (zonaU == "") zonaU = zona;
                    aColocar = NuevaUnidad(evoCard, idEvo, botUid, zonaU);
                    energia -= costeEvo; gastado += costeEvo; evos++;
                    evolucionadas.Add(u.inst);
                    Console.WriteLine($"[WZ][bot {botUid}] EVOLUCIONA en {u.coord}: " +
                        $"{M.Str(M.Get(u.card, "Nombre", "nombre"))} → {M.Str(M.Get(evoCard, "Nombre", "nombre"))} (-{costeEvo})");
                }
            }

            Place(destinoU, aColocar);
        }

        // ── FASE 2: CARTAS DE ACCIÓN jugadas desde la mano ─────────────────────
        JugarCartasAccion(ctx, miCuartel, zona, amenazado, enemyByCoord, enemyCuarteles, misCoords,
            ref energia, ref gastado, mano, acciones);

        // ── FASE 3: HABILIDADES de unidades en tablero (solo las que no se movieron
        //    ni acaban de desplegarse). ────────────────────────────────────────
        int accHab = 0;
        foreach (var u in ownUnits)
        {
            if (accHab >= _maxAcciones) break;
            if (destino[u.inst] != u.coord) continue;
            if (evolucionadas.Contains(u.inst)) continue; // ya no es la misma carta
            if (recienInst.Contains(u.inst)) continue;    // recién desplegada

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
            energia -= coste; gastado += coste; accHab++;
        }

        return new BotMove
        {
            Celdas = celdas,
            Acciones = acciones,
            ManoResultante = mano,
            EnergiaGastada = gastado,
            EspecialComprada = especialComprada,
        };
    }

    // ── Cartas de acción (Condicion == 4) ──────────────────────────────────────
    // Una carta de acción se JUEGA desde la mano (no se despliega en el tablero):
    // lanza la habilidad `IdHabilidad` con origen = el cuartel del jugador, cuesta
    // su `Coste` normal (el que se ve en la mano) y se descarta tras usarse. El
    // servidor la aplica por habilidadId + objetivos; `cartaAccionId` marca la
    // carta a descartar de la mano.
    private static bool EsAccion(Dictionary<string, object?> baseCard)
        => M.Int(M.Get(baseCard, "Condicion", "condicion")) == 4;

    private static Dictionary<string, object?> NuevaUnidad(
        Dictionary<string, object?> baseCard, string id, string botUid, string zona)
        => new(baseCard)
        {
            ["id"] = id,
            ["ownerUid"] = botUid,
            ["ownerZone"] = zona,
            ["instanceId"] = Guid.NewGuid().ToString("N"),
        };

    // Juega hasta _maxAccionesCarta cartas de acción de la mano:
    //   · Ofensivas (disparo/veneno/parálisis) → al mejor grupo enemigo en rango.
    //   · Escudo → SOLO si el cuartel está amenazado, para protegerlo.
    //   · Potenciación → a la unidad propia más adelantada en rango.
    // Teletransporte (mover una carta propia) se deja en mano por ahora.
    private void JugarCartasAccion(
        BotContext ctx, string? miCuartel, string zona, bool amenazado,
        Dictionary<string, List<Dictionary<string, object?>>> enemyByCoord,
        HashSet<string> enemyCuarteles, List<string> misCoords,
        ref int energia, ref int gastado,
        List<string> mano, List<Dictionary<string, object?>> acciones)
    {
        // DIAGNÓSTICO: cuántas cartas de acción hay en mano (para ver si el mazo
        // del bot siquiera las incluye). Si esto sale 0 turno tras turno, es un
        // problema de DATOS (marcar las cartas de acción como PorDefecto o dar a
        // los bots un mazo que las contenga), no del bot.
        var enMano = mano.Where(id =>
            ctx.CatalogoMano.TryGetValue(id, out var b) && EsAccion(b)).ToList();
        if (enMano.Count > 0)
            Console.WriteLine($"[WZ][bot {ctx.BotUid}] cartas de accion en mano: " +
                string.Join(",", enMano.Select(id =>
                    $"{id}(hab{M.Int(M.Get(ctx.CatalogoMano[id], "IdHabilidad", "idHabilidad"))})")));

        if (miCuartel == null) return;
        int jugadas = 0;
        foreach (var id in enMano)
        {
            if (jugadas >= _maxAccionesCarta) break;
            var baseCard = ctx.CatalogoMano[id];
            int habId = M.Int(M.Get(baseCard, "IdHabilidad", "idHabilidad"));
            if (!Cat.TryGetValue(habId, out var hab))
            {
                Console.WriteLine($"[WZ][bot {ctx.BotUid}] accion {id}: habilidad {habId} no modelada (teletransporte u otra) → en mano");
                continue;
            }
            int coste = M.Int(M.Get(baseCard, "Coste", "coste")); // carta de acción: coste = Coste normal
            if (coste > energia)
            {
                Console.WriteLine($"[WZ][bot {ctx.BotUid}] accion {id}: sin energia ({coste}>{energia})");
                continue;
            }

            List<string> objetivos;
            if (hab.Efecto == Efe.Escudo)
            {
                if (!amenazado) continue;                         // escudo solo para defender el cuartel
                objetivos = new() { miCuartel };
            }
            else if (hab.Efecto == Efe.Potenciacion)
            {
                // Buff a una unidad PROPIA en rango: la más adelantada (más cerca
                // de un enemigo) para que rente el potenciador.
                var objetivo = ElegirUnidadAPotenciar(hab, miCuartel, misCoords,
                    enemyByCoord, ctx.Filas, ctx.Columnas);
                if (objetivo == null)
                {
                    Console.WriteLine($"[WZ][bot {ctx.BotUid}] accion {id}: sin unidad propia en rango para potenciar");
                    continue;
                }
                objetivos = new() { objetivo };
            }
            else
            {
                objetivos = ElegirObjetivos(hab, miCuartel, ctx.Filas, ctx.Columnas,
                    enemyByCoord, enemyCuarteles, miCuartel);
                if (objetivos.Count < hab.NumObjetivos)
                {
                    Console.WriteLine($"[WZ][bot {ctx.BotUid}] accion {id}: sin objetivos enemigos en rango");
                    continue;
                }
                objetivos = objetivos.Take(hab.NumObjetivos).ToList();
            }

            acciones.Add(new Dictionary<string, object?>
            {
                ["habilidadId"] = habId,
                ["uid"] = ctx.BotUid,
                ["zona"] = zona,
                ["origen"] = miCuartel,
                ["objetivos"] = objetivos,
                ["turno"] = ctx.Turno,
                ["costePagado"] = coste,
                ["cartaAccionId"] = id,   // el servidor/cliente descarta esta carta de la mano
            });
            energia -= coste; gastado += coste; jugadas++;
            mano.Remove(id);
            Console.WriteLine($"[WZ][bot {ctx.BotUid}] LANZA accion {id} (hab{habId}) sobre [{string.Join(",", objetivos)}]");
        }
    }

    // Elige la unidad PROPIA en rango (desde el cuartel) a la que aplicar una
    // potenciación: la más adelantada, es decir la más cercana a un enemigo.
    private static string? ElegirUnidadAPotenciar(
        Hab hab, string origen, List<string> misCoords,
        Dictionary<string, List<Dictionary<string, object?>>> enemyByCoord,
        int filas, int columnas)
    {
        bool EnRango(string c) => hab.Rango switch
        {
            Rng.Frontera => Manhattan(origen, c, filas, columnas) == 1,
            Rng.Radio7 => Manhattan(origen, c, filas, columnas) <= 7,
            Rng.Cualquiera => true,
            _ => true,
        };

        var candidatas = misCoords.Where(EnRango).ToList();
        if (candidatas.Count == 0) return null;
        if (enemyByCoord.Count == 0)
            return candidatas.First();

        // La más cercana a cualquier enemigo (frontal).
        return candidatas
            .OrderBy(c => enemyByCoord.Keys.Min(e => Manhattan(c, e, filas, columnas)))
            .First();
    }

    // ¿Algún enemigo puede alcanzar mi cuartel este turno o el siguiente?
    private static bool CuartelAmenazado(
        string cuartel, Dictionary<string, List<Dictionary<string, object?>>> enemyByCoord,
        Dictionary<string, string> terreno, int filas, int columnas)
    {
        foreach (var (coord, cartas) in enemyByCoord)
        {
            if (Manhattan(coord, cuartel, filas, columnas) <= 2) return true;
            foreach (var e in cartas)
                if (Alcanzables(coord, Mov(e), Tipo(e), terreno, filas, columnas).Contains(cuartel))
                    return true;
        }
        return false;
    }

    // Mueve una unidad excedente FUERA del cuartel a una celda segura (evita
    // apilar demasiadas cartas juntas). Si no hay alternativa, se queda donde está.
    private string ReubicarFueraDeCuartel(
        string coordOriginal, Dictionary<string, object?> card, string miCuartel,
        Dictionary<string, string> terreno, int filas, int columnas,
        Dictionary<string, List<Dictionary<string, object?>>> enemyByCoord,
        HashSet<string> enemyCuarteles, Dictionary<string, string> cuartelOwner, string botUid)
    {
        int myF = Fuerza(card), myD = Defensa(card);
        var reach = Alcanzables(coordOriginal, Mov(card), Tipo(card), terreno, filas, columnas);
        string? mejor = null; int mejorSep = -1;
        foreach (var c in reach)
        {
            if (c == miCuartel) continue;
            if (enemyCuarteles.Contains(c)) continue;
            if (enemyByCoord.ContainsKey(c) &&
                !GanoAtacando(myF, myD, c, enemyByCoord, enemyCuarteles, cuartelOwner, botUid)) continue;
            int sep = Manhattan(c, miCuartel, filas, columnas); // dispersar: alejarse del cuartel
            if (sep > mejorSep) { mejorSep = sep; mejor = c; }
        }
        return mejor ?? coordOriginal;
    }

    // Intento de INTERCEPTACIÓN: elige el enemigo batible y predecible más cercano,
    // toma su celda PREDICHA (a dónde irá el próximo turno) y se mueve a la celda
    // segura propia que más se acerque a esa predicción — idealmente cayendo encima
    // o a distancia 1 para golpear en cuanto llegue. Devuelve null si no hay presa
    // predecible o si ninguna celda segura mejora respecto a quedarse.
    private string? Interceptar(
        string from, Dictionary<string, object?> card,
        int filas, int columnas,
        Dictionary<string, List<Dictionary<string, object?>>> enemyByCoord,
        HashSet<string> enemyCuarteles, Dictionary<string, string> cuartelOwner,
        string botUid, List<string> seguras, Dictionary<string, string> predEnemigo)
    {
        int myF = Fuerza(card), myD = Defensa(card);

        // Presa: enemigo batible, con predicción disponible, más cercano a nosotros.
        string? objCoord = null, pred = null; int mejorD = int.MaxValue;
        foreach (var (coord, cartas) in enemyByCoord)
        {
            if (enemyCuarteles.Contains(coord)) continue;
            if (!predEnemigo.TryGetValue(coord, out var p)) continue;   // muy móvil / no predecible
            if (!GanoAtacando(myF, myD, coord, enemyByCoord, enemyCuarteles, cuartelOwner, botUid)) continue;
            int d = Manhattan(from, coord, filas, columnas);
            if (d < mejorD) { mejorD = d; objCoord = coord; pred = p; }
        }
        if (objCoord == null || pred == null) return null;

        // Cortarle el paso: la celda segura que MINIMIZA la distancia a la celda
        // predicha (0 = caemos donde irá; 1 = adyacente para golpear al llegar).
        string mejor = from; int mejorDist = int.MaxValue;
        foreach (var c in seguras)
        {
            int dPred = Manhattan(c, pred, filas, columnas);
            if (dPred < mejorDist) { mejorDist = dPred; mejor = c; }
        }
        // Solo si mejora respecto a quedarse quieto (si no, que siga el flujo normal).
        if (mejor == from || mejorDist >= Manhattan(from, pred, filas, columnas)) return null;

        Console.WriteLine($"[WZ][bot {botUid}] predice {objCoord}->{pred}; intercepta {from}->{mejor}");
        return mejor;
    }

    // Predice a qué celda irá una carta enemiga el próximo turno.
    //  (1) Si la vimos moverse (prevCoord), extrapola su vector un paso más y lo
    //      ajusta a la celda alcanzable (respeta movimiento y tipo de terreno).
    //  (2) Si no hay historial, asume que avanza hacia su objetivo probable: la
    //      unidad mía más cercana o mi cuartel.
    private static string PredecirAvance(
        string coord, Dictionary<string, object?> enemyCard, string? prevCoord,
        Dictionary<string, string> terreno, int filas, int columnas,
        string? miCuartel, List<string> misUnidades, HashSet<string> atractoresEnergia)
    {
        int mov = Mov(enemyCard), tipo = Tipo(enemyCard);
        var reach = Alcanzables(coord, mov, tipo, terreno, filas, columnas);
        reach.Add(coord);

        var pc = Parse(coord);
        if (pc != null && prevCoord != null)
        {
            var pp = Parse(prevCoord);
            if (pp != null && (pp.Value.ri != pc.Value.ri || pp.Value.ci != pc.Value.ci))
            {
                int dr = pc.Value.ri - pp.Value.ri, dc = pc.Value.ci - pp.Value.ci;
                string objetivo = ClampLabel(pc.Value.ri + dr, pc.Value.ci + dc, filas, columnas);
                return reach
                    .OrderBy(c => Manhattan(c, objetivo, filas, columnas))
                    .ThenByDescending(c => Manhattan(c, coord, filas, columnas)) // que avance, no que se quede
                    .First();
            }
        }

        // Sin vector: la mayoría de jugadores van a por ENERGÍA (rayos / isla
        // central) o a por una presa cercana; asumimos que avanza hacia el atractor
        // más cercano entre {energía, mis unidades, mi cuartel}.
        var metas = new List<string>(atractoresEnergia);
        metas.AddRange(misUnidades);
        if (miCuartel != null) metas.Add(miCuartel);
        var meta = MasCercano(coord, metas, filas, columnas);
        if (meta == null) return coord;
        return reach.OrderBy(c => Manhattan(c, meta, filas, columnas)).First();
    }

    // Etiqueta de celda recortada a los límites del tablero.
    private static string ClampLabel(int ri, int ci, int filas, int columnas)
    {
        if (ri < 0) ri = 0; else if (ri >= filas) ri = filas - 1;
        if (ci < 0) ci = 0; else if (ci >= columnas) ci = columnas - 1;
        return Label(ri, ci);
    }

    // ── Decisión de movimiento de una unidad ──
    private string DecidirMovimiento(
        string coord, Dictionary<string, object?> card,
        Dictionary<string, string> terreno, int filas, int columnas,
        Dictionary<string, List<Dictionary<string, object?>>> enemyByCoord,
        HashSet<string> enemyCuarteles, Dictionary<string, string> cuartelOwner,
        HashSet<string> cuartelCoords, string botUid, Func<string, int> Farm,
        string? miCuartel, Dictionary<string, string> predEnemigo,
        HashSet<string> atractoresEnergia,
        HashSet<string> intrusos, HashSet<string> miContinente)
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

        // Evitar CAMINAR HACIA LA MUERTE: descartar (si hay alternativa) las
        // celdas donde la predicción dice que caerá un stack enemigo que nos gana.
        bool CaeEnemigoQueMeGana(string c)
        {
            foreach (var (src, land) in predEnemigo)
            {
                if (land != c) continue;
                if (!GanoAtacando(myF, myD, src, enemyByCoord, enemyCuarteles, cuartelOwner, botUid))
                    return true; // ahí aterriza alguien contra quien perdemos
            }
            return false;
        }
        var sinPeligro = seguras.Where(c => !CaeEnemigoQueMeGana(c)).ToList();
        if (sinPeligro.Count > 0) seguras = sinPeligro;

        // 1.5) CAZA PREDICTIVA: en vez de ir a la casilla ACTUAL del enemigo,
        //      adivinar hacia dónde se moverá (por su vector de avance) y cortarle
        //      el paso. Solo contra enemigos poco móviles y batibles. Con
        //      _probPrediccion>=1 lo intenta siempre; si no, con esa probabilidad.
        if (predEnemigo.Count > 0 && (_probPrediccion >= 1.0 || _rng.NextDouble() < _probPrediccion))
        {
            var intercept = Interceptar(coord, card, filas, columnas,
                enemyByCoord, enemyCuarteles, cuartelOwner, botUid, seguras, predEnemigo);
            if (intercept != null) return intercept;
        }

        // 1.6) DEFENSA DEL TERRITORIO: si un rival ha entrado en MI continente y
        //      esta unidad está en casa, converge hacia el intruso más cercano
        //      para contenerlo entre varias (aunque en solitario no lo bata; el
        //      asalto en grupo lo remata). Prioriza defender antes que farmear.
        if (intrusos.Count > 0 && miContinente.Contains(coord))
        {
            var objIntruso = MasCercano(coord, intrusos, filas, columnas);
            if (objIntruso != null)
            {
                string mejorC = coord; int mejorDI = Manhattan(coord, objIntruso, filas, columnas);
                foreach (var c in seguras)
                {
                    int d = Manhattan(c, objIntruso, filas, columnas);
                    if (d < mejorDI) { mejorDI = d; mejorC = c; }
                }
                return mejorC;
            }
        }

        // 2) FARMEAR: si alguna celda segura da energía, ir a la de mayor farmeo.
        var conFarm = seguras.Where(c => Farm(c) > 0).ToList();
        if (conFarm.Count > 0)
            return conFarm.OrderByDescending(Farm)
                          .ThenBy(c => DistObjetivo(c, coord, enemyByCoord, enemyCuarteles, filas, columnas, myF, myD, cuartelOwner, botUid))
                          .First();

        // 3) Sin farmeo a mano: moverse hacia un OBJETIVO (equilibra caza y energía).
        string? objetivo = ObjetivoGlobal(coord, enemyByCoord, enemyCuarteles, filas, columnas, myF, myD, cuartelOwner, botUid, Farm, atractoresEnergia);
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

    // Objetivo hacia el que orientarse, EQUILIBRANDO caza y energía: si hay una
    // celda de energía (rayo / isla) estrictamente más cerca que el enemigo
    // batible más próximo, va primero a por la energía (la recoge de camino); si
    // no, caza. En último término, se orienta al cuartel enemigo más cercano.
    private string? ObjetivoGlobal(
        string from, Dictionary<string, List<Dictionary<string, object?>>> enemyByCoord,
        HashSet<string> enemyCuarteles, int filas, int columnas, int myF, int myD,
        Dictionary<string, string> cuartelOwner, string botUid, Func<string, int> Farm,
        HashSet<string> atractoresEnergia)
    {
        // a) enemigo batible más cercano (caza)
        string? mejorEnemigo = null; int dEnemigo = int.MaxValue;
        foreach (var c in enemyByCoord.Keys)
        {
            if (!GanoAtacando(myF, myD, c, enemyByCoord, enemyCuarteles, cuartelOwner, botUid)) continue;
            int d = Manhattan(from, c, filas, columnas);
            if (d < dEnemigo) { dEnemigo = d; mejorEnemigo = c; }
        }

        // b) energía (rayo / isla) más cercana
        string? mejorEnergia = null; int dEnergia = int.MaxValue;
        foreach (var c in atractoresEnergia)
        {
            int d = Manhattan(from, c, filas, columnas);
            if (d < dEnergia) { dEnergia = d; mejorEnergia = c; }
        }

        // Equilibrio: energía si está más cerca; si no, caza.
        if (mejorEnergia != null && dEnergia < dEnemigo) return mejorEnergia;
        if (mejorEnemigo != null) return mejorEnemigo;
        if (mejorEnergia != null) return mejorEnergia;

        // c) cuartel enemigo más cercano
        return MasCercano(from, enemyCuarteles, filas, columnas);
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
        => GanaGrupo(myF, myD, coord, enemyByCoord, enemyCuarteles, cuartelOwner, botUid);

    // ¿Gana un GRUPO propio (suma de fuerza/defensa) atacando esa celda? El
    // combate del juego suma las cartas por celda, así que apilar unidades es la
    // forma legítima de ganar peleas que en solitario se pierden.
    private bool GanaGrupo(
        int sumF, int sumD, string coord,
        Dictionary<string, List<Dictionary<string, object?>>> enemyByCoord,
        HashSet<string> enemyCuarteles, Dictionary<string, string> cuartelOwner, string botUid)
    {
        if (!enemyByCoord.TryGetValue(coord, out var enemigos) || enemigos.Count == 0)
            return !enemyCuarteles.Contains(coord) || sumF > UmbralCuartel;
        int fe = enemigos.Sum(Fuerza), de = enemigos.Sum(Defensa);
        if (enemyCuarteles.Contains(coord) && CuartelDefendido(coord, cuartelOwner, botUid, enemyByCoord))
            de += UmbralCuartel; // el cuartel defendido suma +80 de defensa al dueño
        return (sumF - de) > (fe - sumD); // poder neto estrictamente mayor
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

    // Ejército resuelto por bot (estable durante la vida del proceso) y lista de
    // ejércitos existentes, para no releerlos en cada turno.
    private readonly ConcurrentDictionary<string, int> _ejercitoCache = new();
    private List<int>? _ejercitoIds;

    public WarZeroBot(
        WarZeroFirestore fs, WarZeroService svc,
        WarZeroBotOptions? options = null, IBotStrategy? strategy = null)
    {
        _fs = fs; _svc = svc;
        _opt = options ?? new WarZeroBotOptions();
        _strategy = strategy ?? new EstrategaStrategy(_opt);
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

    // ── Reanudar una partida YA EN CURSO (recuperación tras reinicio) ──────────
    // La invoca el orquestador cuando encuentra una partida `en_curso` con este
    // bot como participante pero sin runner vivo. NO se une ni espera arranque:
    // la partida ya arrancó y el bot ya es participante. EntrarAsync es
    // idempotente (si ya tiene mano no la reparte otra vez), así que solo
    // re-adjunta el estado antes de retomar el bucle de juego.
    public async Task ResumeForLobbyAsync(string lobbyId, string botUid, string botAlias, CancellationToken ct = default)
    {
        try
        {
            Log(botUid, $"reanudando la partida {lobbyId} (recuperada tras reinicio)");
            await _svc.EntrarAsync(new EntrarRequest { LobbyId = lobbyId, Uid = botUid });
            await BuclePartidaAsync(lobbyId, botUid, ct);
            Log(botUid, "partida terminada");
        }
        catch (OperationCanceledException) { Log(botUid, "cancelado"); }
        catch (Exception ex) { Console.Error.WriteLine($"[WZ][bot {botUid}] error fatal (reanudar): {ex}"); }
    }

    private async Task<bool> UnirseYMarcarListoAsync(string lobbyId, string botUid, string botAlias, CancellationToken ct)
    {
        // EJÉRCITO: sin `ejercitoId`, el servidor reparte al bot un mazo por
        // defecto tomado del CATÁLOGO COMPLETO, mezclando cartas de todos los
        // ejércitos. Se resuelve antes de la transacción y se guarda en la entrada
        // del jugador, igual que hace un humano al elegir ejército en la sala.
        int ejercitoId = await EjercitoDeBotAsync(botUid, ct);

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
                jugadores.Add(new Dictionary<string, object?>
                {
                    ["uid"] = botUid,
                    ["alias"] = botAlias,
                    ["listo"] = true,
                    ["ejercitoId"] = ejercitoId,
                });
            }
            else foreach (var j in jugadores)
                if (M.Str(M.Get(j, "uid")) == botUid)
                {
                    j["listo"] = true;
                    if (M.Get(j, "ejercitoId") == null) j["ejercitoId"] = ejercitoId;
                }
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

        // ── Ejército del bot (para no mezclar cartas de ejércitos distintos y
        //    saber qué generales puede comprar) ──
        int ejercitoId = EjercitoDeJugador(estado, botUid) ?? await EjercitoDeBotAsync(botUid, ct);

        // ── Evoluciones referenciadas por mis cartas en tablero ──
        var idsEvo = new HashSet<string>();
        foreach (var celda in M.Map(M.Get(estado, "tablero")).Values)
            foreach (var c in M.List(celda))
            {
                var cm = M.Map(c);
                if (M.Str(M.Get(cm, "ownerUid")) != botUid) continue;
                var ie = M.Str(M.Get(cm, "IdEvolucion", "idEvolucion"));
                if (ie != "" && M.Int(M.Get(cm, "Evolucion", "evolucion")) > 0) idsEvo.Add(ie);
            }
        var evoluciones = idsEvo.Count > 0
            ? await CargarCartasAsync(idsEvo.ToList(), ct)
            : new Dictionary<string, Dictionary<string, object?>>();

        // ── Generales (especiales) de mi ejército aún NO comprados ──
        var compradas = M.List(M.Get(miStat, "especialesCompradas")).Select(M.Str).ToHashSet();
        var generales = _opt.ComprarGenerales
            ? await CargarGeneralesAsync(ejercitoId, compradas, ct)
            : new List<Dictionary<string, object?>>();

        var ctx = new BotContext
        {
            EjercitoId = ejercitoId,
            Evoluciones = evoluciones,
            GeneralesDisponibles = generales,
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

        if (jugada.EnergiaGastada != 0 || !mano.SequenceEqual(jugada.ManoResultante)
            || !string.IsNullOrEmpty(jugada.EspecialComprada))
        {
            try
            {
                await _svc.ActualizarStatsAsync(new StatsRequest
                {
                    LobbyId = lobbyId,
                    Uid = botUid,
                    EnergiesDelta = -jugada.EnergiaGastada,
                    Mano = jugada.ManoResultante,
                    // arrayUnion en `especialesCompradas`: el general queda marcado
                    // como comprado para toda la partida (si muere, no se reinvoca).
                    EspecialComprada = string.IsNullOrEmpty(jugada.EspecialComprada)
                        ? null : jugada.EspecialComprada,
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

    /// Ejército elegido por el jugador en la sala (`jugadores[].ejercitoId`),
    /// igual que lo lee WarZeroService para repartir la mano. null si no lo tiene.
    private static int? EjercitoDeJugador(Dictionary<string, object?> estado, string uid)
    {
        foreach (var j in M.List(M.Get(estado, "jugadores")))
        {
            var jm = M.Map(j);
            if (M.Str(M.Get(jm, "uid")) != uid) continue;
            var e = M.Get(jm, "ejercitoId");
            return e == null ? (int?)null : M.Int(e);
        }
        return null;
    }

    /// Ejército del bot: el de su documento en `Bots` (campo `ejercitoId`), el de
    /// las opciones, o uno derivado de forma ESTABLE del uid. Estable importa:
    /// así un bot siempre juega el mismo ejército y no mezcla cartas.
    private async Task<int> EjercitoDeBotAsync(string botUid, CancellationToken ct)
    {
        if (_ejercitoCache.TryGetValue(botUid, out var cache)) return cache;

        int elegido = 0;
        try
        {
            var snap = await _fs.Db.Collection("Bots").Document(botUid).GetSnapshotAsync(ct);
            if (snap.Exists)
                elegido = M.Int(M.Get(M.Map(M.FromFs(snap.ToDictionary())), "ejercitoId"));
        }
        catch (Exception ex) { Console.Error.WriteLine($"[WZ][bot {botUid}] leer ejercito falló: {ex}"); }

        if (elegido <= 0) elegido = _opt.EjercitoPorDefecto;
        if (elegido <= 0)
        {
            // Derivación estable por uid entre los ejércitos existentes (1..N).
            var ids = await CargarEjercitoIdsAsync(ct);
            if (ids.Count > 0)
            {
                int h = 0;
                foreach (var c in botUid) h = (h * 31 + c) & 0x7FFFFFFF;
                elegido = ids[h % ids.Count];
            }
            else elegido = 1;
        }

        _ejercitoCache[botUid] = elegido;
        return elegido;
    }

    /// Ids numéricos de la colección `Ejercitos` (el id del doc es el número).
    private async Task<List<int>> CargarEjercitoIdsAsync(CancellationToken ct)
    {
        if (_ejercitoIds != null) return _ejercitoIds;
        var ids = new List<int>();
        try
        {
            var snap = await _fs.Db.Collection("Ejercitos").GetSnapshotAsync(ct);
            foreach (var d in snap.Documents)
                if (int.TryParse(d.Id, out var n) && n > 0) ids.Add(n);
        }
        catch (Exception ex) { Console.Error.WriteLine($"[WZ][bot] leer Ejercitos falló: {ex}"); }
        ids.Sort();
        _ejercitoIds = ids;
        return ids;
    }

    /// GENERALES comprables: cartas especiales (Condicion == 5) del ejército del
    /// bot que aún no figuran en `especialesCompradas` de esta partida.
    private async Task<List<Dictionary<string, object?>>> CargarGeneralesAsync(
        int ejercitoId, HashSet<string> yaCompradas, CancellationToken ct)
    {
        var res = new List<Dictionary<string, object?>>();
        try
        {
            var snap = await _fs.Db.Collection("Cartas")
                .WhereEqualTo("Condicion", 5)
                .GetSnapshotAsync(ct);
            foreach (var d in snap.Documents)
            {
                if (yaCompradas.Contains(d.Id)) continue;
                var map = M.Map(M.FromFs(d.ToDictionary()));
                if (ejercitoId > 0 && M.Int(M.Get(map, "Ejercito", "ejercito")) != ejercitoId) continue;
                map["id"] = d.Id;
                res.Add(map);
            }
        }
        catch (Exception ex) { Console.Error.WriteLine($"[WZ][bot] leer generales falló: {ex}"); }
        return res;
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