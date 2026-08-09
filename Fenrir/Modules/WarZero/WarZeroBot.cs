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

// ─────────────────────────────────────────────────────────────────────────────
// PERFIL DE BOT (dificultad + estilo)
//
// Dos ejes ORTOGONALES que se configuran por bot desde el panel de Flutter
// (colección `Bots`, campos `dificultad` y `estilo`) y viajan hasta la estrategia:
//
//   · DIFICULTAD — cuán FUERTE juega (no cuán temerario):
//       Medio → los valores actuales (jugador medio, ya competente).
//       Alto  → más recursos por turno (despliegues, habilidades, evoluciones),
//               menos energía ociosa en reserva, predice también unidades rápidas,
//               lanza más asaltos coordinados y concentra la fuerza para rematar
//               antes. NO es "suicida": los ataques individuales siguen exigiendo
//               ganar el combate; lo que sube es la PRESIÓN y el aprovechamiento.
//
//   · ESTILO — DÓNDE invierte esos recursos:
//       Equilibrado → como hasta ahora (farmea, caza, conquista con criterio).
//       Defensivo   → guarnición más densa, más reserva de energía, prioriza
//                     farmear/defender su territorio y solo pelea cuando gana claro.
//       Agresivo    → empuja hacia los cuarteles enemigos, compromete grupos antes,
//                     gasta más energía, deja el cuartel más ligero y remata
//                     cuarteles aunque esté siendo amenazado (si de verdad los gana).
//
// Un bot MEDIO + EQUILIBRADO reproduce EXACTAMENTE el comportamiento anterior, de
// modo que el cambio es retrocompatible con los bots ya sembrados en Firestore.
// ─────────────────────────────────────────────────────────────────────────────
public enum DificultadBot { Medio, Alto }
public enum EstiloBot { Equilibrado, Defensivo, Agresivo }

public sealed class PerfilBot
{
    public DificultadBot Dificultad { get; init; } = DificultadBot.Medio;
    public EstiloBot Estilo { get; init; } = EstiloBot.Equilibrado;

    /// Perfil neutro: nivel medio, estilo equilibrado (= comportamiento clásico).
    public static readonly PerfilBot PorDefecto = new();

    /// Construye un perfil a partir de las cadenas guardadas en el documento del
    /// bot. Tolera nulos, mayúsculas y espacios; cualquier valor desconocido cae a
    /// los valores por defecto (medio / equilibrado).
    public static PerfilBot Parse(string? dificultad, string? estilo) => new()
    {
        Dificultad = (dificultad ?? "").Trim().ToLowerInvariant() switch
        {
            "alto" => DificultadBot.Alto,
            _ => DificultadBot.Medio,
        },
        Estilo = (estilo ?? "").Trim().ToLowerInvariant() switch
        {
            "defensivo" => EstiloBot.Defensivo,
            "agresivo" => EstiloBot.Agresivo,
            _ => EstiloBot.Equilibrado,
        },
    };

    public override string ToString() => $"{Dificultad}/{Estilo}";
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
                var cond = M.Int(M.Get(cartaBase, "Condicion", "condicion"));
                if (cond == 4) continue; // acción: no se despliega desde aquí
                // Estática (Condicion==3): NO puede desplegarse en el cuartel.
                // Aquí no se coloca; se hace en la FASE ESTÁTICAS de más abajo,
                // sobre una celda propia YA mantenida (anclaje válido).
                if (cond == 3) continue;
                int coste = M.Int(M.Get(cartaBase, "Coste", "coste"));
                if (coste > energia) continue;
                var celda = new Dictionary<string, object?>(cartaBase)
                { ["id"] = id, ["ownerUid"] = ctx.BotUid, ["ownerZone"] = zona, ["instanceId"] = Guid.NewGuid().ToString("N") };
                if (!celdas.TryGetValue(ctx.Cuartel, out var lst)) { lst = new(); celdas[ctx.Cuartel] = lst; }
                lst.Add(celda);
                energia -= coste; gastado += coste; desplegadas++; mano.Remove(id);
            }
        // ── FASE ESTÁTICAS: fortificar posiciones mantenidas ───────────────────
        // En Recluta las unidades propias no se mueven (se recolocan en su misma
        // celda), así que TODA celda propia que no sea el cuartel es un anclaje
        // válido para una estática. Se prioriza defender el farmeo (rayo / isla)
        // y, en su defecto, cualquier posición retenida. Una por turno (estrategia
        // de baja intensidad). El servidor revalida el anclaje por si acaso.
        var estaticasMano = mano
            .Where(id => ctx.CatalogoMano.TryGetValue(id, out var b)
                         && M.Int(M.Get(b, "Condicion", "condicion")) == 3)
            .ToList();
        if (estaticasMano.Count > 0)
        {
            bool PuedeAterrizar(string coord, int tipo)
            {
                var terr = ctx.Terreno.TryGetValue(coord, out var v) ? v : "land";
                return tipo switch
                {
                    1 or 2 => terr is "land" or "amphibious",
                    3 => terr is "sea" or "deepSea" or "amphibious",
                    _ => true,
                };
            }
            // Anclas = celdas propias (≠ cuartel), priorizando celdas de farmeo.
            var anclas = celdas.Keys
                .Where(c => c != ctx.Cuartel && celdas[c].Count > 0)
                .OrderByDescending(c =>
                    (ctx.Rayos.Contains(c) ? 8 : 0) + (ctx.IslaCentral.Contains(c) ? 5 : 0))
                .ToList();
            foreach (var coord in anclas)
            {
                var id = estaticasMano.FirstOrDefault(sid =>
                {
                    var b = ctx.CatalogoMano[sid];
                    int tipo = M.Int(M.Get(b, "Tipo", "tipo")); if (tipo <= 0) tipo = 1;
                    int coste = M.Int(M.Get(b, "Coste", "coste"));
                    return PuedeAterrizar(coord, tipo) && coste <= energia;
                });
                if (id == null) continue;
                var baseCard = ctx.CatalogoMano[id];
                int coste = M.Int(M.Get(baseCard, "Coste", "coste"));
                var est = new Dictionary<string, object?>(baseCard)
                {
                    ["id"] = id,
                    ["ownerUid"] = ctx.BotUid,
                    ["ownerZone"] = zona,
                    ["instanceId"] = Guid.NewGuid().ToString("N"),
                };
                celdas[coord].Add(est);
                energia -= coste; gastado += coste; mano.Remove(id);
                Console.WriteLine($"[WZ][bot {ctx.BotUid}] ESTÁTICA (recluta) en {coord}");
                break; // una estática por turno
            }
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

    // ── Parámetros derivados del PERFIL (dificultad + estilo) ──
    private readonly PerfilBot _perfil;
    /// % de energía que se guarda sin gastar cuando NO hay urgencia. Menos reserva
    /// = juega más su energía (dificultad alta / estilo agresivo).
    private readonly int _reservaPct;
    /// Valentía en las lecturas de combate DE GRUPO (asalto coordinado / contención
    /// de intrusos). Se suma a la defensa propia efectiva: >0 compromete grupos
    /// algo antes; <0 exige ventaja más clara. NUNCA se aplica a la toma de un
    /// cuartel (eso siempre exige ganar de verdad).
    private readonly int _sesgoAtaque;
    /// Empuje hacia el frente enemigo frente a desviarse a por energía. >0 (agresivo)
    /// exige que la energía esté MUCHO más cerca para ir a por ella; <0 (defensivo)
    /// se desvía a farmear con más facilidad.
    private readonly int _sesgoFrente;
    /// Nº de asaltos coordinados (caza en grupo) por turno.
    private readonly int _maxCazasGrupo;
    /// Nº de stacks enemigos que se evalúan como objetivo de caza en grupo.
    private readonly int _topObjetivosGrupo;
    /// Si asalta cuarteles enemigos AUNQUE su propio cuartel esté amenazado (siempre
    /// que el grupo realmente gane la toma). Estilo agresivo.
    private readonly bool _asaltoBajoAmenaza;

    /// Retrocompatible: perfil neutro (medio / equilibrado) = comportamiento clásico.
    public EstrategaStrategy(WarZeroBotOptions opt) : this(opt, PerfilBot.PorDefecto) { }

    public EstrategaStrategy(WarZeroBotOptions opt, PerfilBot? perfil)
    {
        _perfil = perfil ?? PerfilBot.PorDefecto;
        bool alto = _perfil.Dificultad == DificultadBot.Alto;

        // Base = opciones globales (equivale a MEDIO / EQUILIBRADO).
        int maxDeploys = opt.MaxDeploysPorTurno;
        int maxAcc = opt.MaxAcciones;
        int maxAccCarta = opt.MaxAccionesCarta;
        int maxEvo = opt.MaxEvolucionesPorTurno;
        int movPred = opt.MovMaxPredecible;
        int defensores = opt.MaxDefensoresCuartel;
        int reservaPct = 10; // MENOS reserva ociosa: convertir energía en tablero
        int cazasGrupo = 2, topGrupo = 3, sesgoAtaque = 0, sesgoFrente = 0;
        bool asaltoBajoAmenaza = false;

        // ── DIFICULTAD ALTO: más recursos por turno, mejor lectura, más presión ──
        //    (más fuerte, no más temerario: los ataques siguen exigiendo ganar).
        if (alto)
        {
            maxDeploys += 1;   // saca más tropa al tablero
            maxAcc += 2;   // usa más habilidades desde tablero
            maxAccCarta += 1;   // juega más cartas de acción
            maxEvo += 1;   // evoluciona más unidades
            movPred = Math.Max(movPred, 9); // predice también unidades rápidas
            reservaPct = 10;  // deja menos energía ociosa
            cazasGrupo = 3;   // más asaltos coordinados por turno
            topGrupo = 5;   // evalúa más stacks enemigos para cazar en grupo
            sesgoAtaque += 6;   // concentra fuerza y remata algo antes
        }

        // ── ESTILO: sesga DÓNDE invierte los recursos (ortogonal a la dificultad) ──
        switch (_perfil.Estilo)
        {
            case EstiloBot.Defensivo:
                defensores += 2;   // guarnición más densa
                reservaPct += 10;  // más colchón de energía para reaccionar
                sesgoFrente -= 4;   // prioriza farmear / defender su territorio
                sesgoAtaque -= 6;   // solo pelea en grupo cuando gana con claridad
                break;
            case EstiloBot.Agresivo:
                defensores = Math.Max(1, defensores - 1); // cuartel más ligero
                reservaPct = Math.Max(0, reservaPct - 8); // gasta para presionar
                sesgoFrente += 6;   // empuja hacia los cuarteles enemigos
                sesgoAtaque += 8;   // compromete grupos antes
                asaltoBajoAmenaza = true; // remata cuarteles aunque le amenacen
                maxDeploys += 1;   // más presencia en el frente
                break;
            default: break;         // Equilibrado: sin cambios
        }

        _maxDeploys = Math.Max(0, maxDeploys);
        _maxAcciones = Math.Max(0, maxAcc);
        _maxAccionesCarta = Math.Max(0, maxAccCarta);
        _maxDefensoresCuartel = Math.Max(1, defensores);
        _probPrediccion = Math.Clamp(opt.ProbCazaPredictiva, 0.0, 1.0);
        _movMaxPredecible = Math.Max(0, movPred);
        _maxEvoluciones = Math.Max(0, maxEvo);
        _comprarGenerales = opt.ComprarGenerales;
        _reservaPct = Math.Clamp(reservaPct, 0, 90);
        _maxCazasGrupo = Math.Max(1, cazasGrupo);
        _topObjetivosGrupo = Math.Max(1, topGrupo);
        _sesgoAtaque = sesgoAtaque;
        _sesgoFrente = sesgoFrente;
        _asaltoBajoAmenaza = asaltoBajoAmenaza;
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
            int reserva = (amenazado || continenteInvadido || remontada) ? 0 : energia * _reservaPct / 100;

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
                .Where(id => ctx.CatalogoMano.ContainsKey(id)
                             && !EsAccion(ctx.CatalogoMano[id])
                             && !EsEstatica(ctx.CatalogoMano[id]))
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

        // Cuarteles enemigos que el asalto NO pudo tomar este turno (defensor
        // demasiado apilado). Candidatos a romperse con un DISPARO LEJANO en la
        // fase de cartas de acción (limpia a los defensores; se entra al turno
        // siguiente sobre el cuartel ya vacío).
        var cuartelesAtrincherados = new HashSet<string>();

        // (a) ASALTO EN MANADA a un cuartel enemigo (DEFENDIDO o no). Reúne el grupo
        //     MÍNIMO (más fuertes primero) que, sumando fuerzas, GANA el combate del
        //     cuartel (el defensor suma +UmbralCuartel). Prioriza el más cercano.
        //     Por defecto solo si NO estamos amenazados (si nos atacan, defender es
        //     prioritario). El estilo AGRESIVO (_asaltoBajoAmenaza) remata cuarteles
        //     aunque le amenacen, pero SIEMPRE exigiendo que el grupo gane la toma.
        if (!amenazado || _asaltoBajoAmenaza)
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
                {
                    // Ni todas juntas rematan el cuartel: no malgastar el asalto,
                    // pero marcarlo para intentar romperlo con un disparo lejano.
                    cuartelesAtrincherados.Add(cuartelObj);
                    continue;
                }

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
                              intruso, enemyByCoord, enemyCuarteles, cuartelOwner, botUid, _sesgoAtaque)) break;
            }
            if (!GanaGrupo(grupo.Sum(g => Fuerza(g.card)), grupo.Sum(g => Defensa(g.card)),
                           intruso, enemyByCoord, enemyCuarteles, cuartelOwner, botUid, _sesgoAtaque))
                continue; // aún no lo batimos; el movimiento individual convergerá a él

            foreach (var u in grupo) { destino[u.inst] = intruso; asignada.Add(u.inst); }
            Console.WriteLine($"[WZ][bot {botUid}] CONTIENE INTRUSO: {grupo.Count} → {intruso}");
        }

        // (a2) CAZA EN GRUPO de stacks enemigos valiosos (fuera del cuartel y que no
        //      sean ya intrusos ni cuarteles). Varias unidades que en solitario
        //      perderían pueden ganar JUNTAS: se concentra el mínimo que gana.
        if (!amenazado || _asaltoBajoAmenaza)
        {
            var objetivosGrupo = enemyByCoord.Keys
                .Where(c => !enemyCuarteles.Contains(c) && !intrusos.Contains(c))
                .OrderByDescending(c => enemyByCoord[c].Sum(Coste))
                .Take(_topObjetivosGrupo);
            int cazasLanzadas = 0;
            foreach (var celdaObj in objetivosGrupo)
            {
                if (cazasLanzadas >= _maxCazasGrupo) break; // tope de asaltos coordinados/turno
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
                                  celdaObj, enemyByCoord, enemyCuarteles, cuartelOwner, botUid, _sesgoAtaque)) break;
                }
                if (!GanaGrupo(grupo.Sum(g => Fuerza(g.card)), grupo.Sum(g => Defensa(g.card)),
                               celdaObj, enemyByCoord, enemyCuarteles, cuartelOwner, botUid, _sesgoAtaque))
                    continue; // ni todas juntas ganan: no suicidarse

                foreach (var u in grupo) { destino[u.inst] = celdaObj; asignada.Add(u.inst); }
                cazasLanzadas++;
                Console.WriteLine($"[WZ][bot {botUid}] CAZA EN GRUPO: {grupo.Count} unidades → {celdaObj}");
            }
        }

        // (b) DEFENSA PROPORCIONAL A LA AMENAZA: contra un asalto grande NO nos
        //     limitamos al tope anti-AoE (perder el cuartel de golpe es mucho peor
        //     que arriesgar un AoE). Reunimos, entre las unidades que YA están en el
        //     cuartel o que PUEDEN replegarse a él este turno, las suficientes para
        //     GANAR la defensa: el cuartel aporta +UmbralCuartel y cada unidad su
        //     defensa. Anclamos por orden de más fuertes hasta superar la fuerza
        //     entrante (con un mínimo de piezas); si ni con todas se gana, se anclan
        //     todas igualmente (mejor caer peleando en masa que regalar el cuartel).
        //     Fue el fallo clave observado: un cuartel caía con 1 solo defensor.
        if (amenazado && miCuartel != null)
        {
            int amenazaF = MaxAtaqueEntrante(miCuartel, enemyByCoord, terreno, filas, columnas);
            var defensoras = ownUnits
                .Where(u => !asignada.Contains(u.inst))
                .Where(u => u.coord == miCuartel ||
                            Alcanzables(u.coord, Mov(u.card), Tipo(u.card), terreno, filas, columnas).Contains(miCuartel))
                .OrderByDescending(u => Defensa(u.card) + Fuerza(u.card))
                .ToList();

            int sumaD = UmbralCuartel;                              // bono del cuartel propio
            int minPiezas = Math.Min(_maxDefensoresCuartel, defensoras.Count);
            int ancladas = 0;
            foreach (var u in defensoras)
            {
                destino[u.inst] = miCuartel; asignada.Add(u.inst);
                sumaD += Defensa(u.card); ancladas++;
                // Basta con superar la fuerza entrante y anclar un mínimo de piezas.
                if (sumaD > amenazaF && ancladas >= minPiezas) break;
            }

            // REFUERZO DE EMERGENCIA: si con las unidades ya ancladas NO se supera
            // la fuerza entrante, DESPLEGAR cartas nuevas (las de más defensa)
            // directamente sobre el cuartel, SIN el tope de despliegue ni el de
            // defensores. Perder el cuartel es mucho peor que un AoE; y con energía
            // acumulada, defenderlo con el número adecuado al ataque es trivial.
            // (Fallo observado: el cuartel caía con 1-2 defensores teniendo energía
            // de sobra en el banco.)
            if (sumaD <= amenazaF && ctx.Cuartel != "")
            {
                var refuerzos = mano
                    .Where(id => ctx.CatalogoMano.ContainsKey(id)
                                 && !EsAccion(ctx.CatalogoMano[id])
                                 && !EsEstatica(ctx.CatalogoMano[id]))
                    .OrderByDescending(id => Defensa(ctx.CatalogoMano[id]) + Fuerza(ctx.CatalogoMano[id]))
                    .ToList();
                foreach (var id in refuerzos)
                {
                    if (sumaD > amenazaF) break;
                    var baseCard = ctx.CatalogoMano[id];
                    int coste = M.Int(M.Get(baseCard, "Coste", "coste"));
                    if (coste > energia) continue;
                    DesplegarUnidad(baseCard, id);
                    var instNuevo = ownUnits[^1].inst;      // la unidad recién añadida
                    destino[instNuevo] = miCuartel; asignada.Add(instNuevo);
                    energia -= coste; gastado += coste; desplegadas++;
                    mano.Remove(id);
                    sumaD += Defensa(baseCard); ancladas++;
                }
            }

            if (ancladas > 0)
                Console.WriteLine($"[WZ][bot {botUid}] DEFIENDE CUARTEL {miCuartel}: {ancladas} unidades " +
                    $"(defensa {sumaD} vs fuerza entrante {amenazaF})");
        }

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
        //     mi cuartel (un AoE a distancia las barrería a todas). SOLO cuando NO
        //     estamos amenazados: si nos asaltan, la defensa proporcional (b) manda y
        //     apilar es preferible a perder el cuartel.
        if (miCuartel != null && !amenazado)
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

        // ── FASE ESTÁTICAS: fortificar posiciones propias ──────────────────────
        // Las estáticas (Condicion==3) NO se mueven y NO pueden ir al cuartel:
        // solo pueden colocarse sobre una celda propia donde ya había una carta
        // que NO se mueve este turno (mismo anclaje que valida el cliente y el
        // servidor). Son piezas DEFENSIVAS clave: refuerzan y "clavan" una
        // posición. El bot las coloca en sus celdas más valiosas de defender:
        // las amenazadas por el enemigo, los pasos de acceso a su cuartel y las
        // celdas de farmeo (rayo / isla central).
        var estaticasMano = mano
            .Where(id => ctx.CatalogoMano.ContainsKey(id) && EsEstatica(ctx.CatalogoMano[id]))
            .ToList();
        if (estaticasMano.Count > 0 && miCuartel != null)
        {
            // Anclas válidas: celdas (≠ cuartel) donde una unidad PROPIA que ya
            // estaba en tablero se QUEDA este turno (destino == su propia celda).
            var anclas = ownUnits
                .Where(u => !recienInst.Contains(u.inst)
                            && destino[u.inst] == u.coord
                            && u.coord != miCuartel)
                .Select(u => u.coord)
                .Distinct()
                .ToList();

            // Valor defensivo de fortificar una celda:
            //   · amenaza entrante (fuerza enemiga que puede caer ahí) → lo más
            //     importante que defender;
            //   · cercanía al cuartel → anillo de contención de acceso;
            //   · celdas de farmeo (rayo / isla) → retener economía.
            int ValorDefensa(string coord)
            {
                int v = MaxAtaqueEntrante(coord, enemyByCoord, terreno, filas, columnas) * 3;
                v += Math.Max(0, 20 - Manhattan(coord, miCuartel!, filas, columnas) * 2);
                if (ctx.Rayos.Contains(coord)) v += 8;
                if (ctx.IslaCentral.Contains(coord)) v += 5;
                return v;
            }
            anclas.Sort((a, b) => ValorDefensa(b).CompareTo(ValorDefensa(a)));

            // Reserva de energía salvo urgencia; más estáticas si hay presión.
            int reservaEst = (amenazado || continenteInvadido) ? 0 : energia * _reservaPct / 100;
            int maxEstaticas = (amenazado || continenteInvadido) ? 2 : 1;
            int colocadas = 0;
            foreach (var coord in anclas)
            {
                if (colocadas >= maxEstaticas) break;
                // Sin urgencia, no malgastar en celdas de poco valor (ordenadas
                // desc.: la primera por debajo del umbral corta el resto).
                if (!amenazado && !continenteInvadido && ValorDefensa(coord) < 6) break;
                // Una sola estática por celda: un AoE a distancia barrería juntas
                // varias torretas apiladas.
                string? elegido = null;
                foreach (var id in estaticasMano)
                {
                    var bc = ctx.CatalogoMano[id];
                    if (!CanLand(coord, Tipo(bc), terreno)) continue;   // terreno
                    if (energia - Coste(bc) < reservaEst) continue;      // presupuesto
                    elegido = id; break;
                }
                if (elegido == null) continue;
                var baseCard = ctx.CatalogoMano[elegido];
                Place(coord, NuevaUnidad(baseCard, elegido, botUid, zona));
                int coste = Coste(baseCard);
                energia -= coste; gastado += coste;
                mano.Remove(elegido); estaticasMano.Remove(elegido);
                colocadas++;
                Console.WriteLine($"[WZ][bot {botUid}] ESTÁTICA defensiva en {coord} (valor {ValorDefensa(coord)})");
            }
        }

        // ── FASE 2: CARTAS DE ACCIÓN jugadas desde la mano ─────────────────────
        JugarCartasAccion(ctx, miCuartel, zona, amenazado, enemyByCoord, enemyCuarteles, misCoords,
            ref energia, ref gastado, mano, acciones, cuartelesAtrincherados);

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

    // ── Cartas ESTÁTICAS (Condicion == 3) ───────────────────────────────────────
    // No se despliegan en el cuartel. El bot solo sabe desplegar en su cuartel,
    // así que las excluye del despliegue (misma regla que el cliente humano).
    private static bool EsEstatica(Dictionary<string, object?> baseCard)
        => M.Int(M.Get(baseCard, "Condicion", "condicion")) == 3;

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
        List<string> mano, List<Dictionary<string, object?>> acciones,
        HashSet<string>? cuartelesAtrincherados = null)
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
                // ROMPER ATRINCHERAMIENTO: si es un DISPARO y algún cuartel enemigo
                // que el asalto no pudo tomar está en rango, dispararlo AHÍ primero
                // (limpia a los defensores; se entra a conquistar al turno siguiente
                // sobre el cuartel ya vacío). ElegirObjetivos ya incluye cuarteles
                // (ExcluyeCG=false); aquí solo forzamos su prioridad.
                if (hab.Efecto == Efe.Disparo && cuartelesAtrincherados != null
                    && cuartelesAtrincherados.Count > 0)
                {
                    var prioritarios = objetivos.Where(cuartelesAtrincherados.Contains).ToList();
                    if (prioritarios.Count > 0)
                    {
                        objetivos = prioritarios
                            .Concat(objetivos.Where(o => !prioritarios.Contains(o)))
                            .ToList();
                        Console.WriteLine($"[WZ][bot {ctx.BotUid}] DISPARO para romper cuartel atrincherado {prioritarios[0]}");
                    }
                }
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

    // Fuerza TOTAL que puede impactar mi cuartel este turno (suma de la fuerza de
    // todo enemigo que lo alcanza o está a distancia <=2). Sirve para dimensionar
    // cuánta defensa hay que anclar para NO perder el cuartel de golpe.
    private static int MaxAtaqueEntrante(
        string cuartel, Dictionary<string, List<Dictionary<string, object?>>> enemyByCoord,
        Dictionary<string, string> terreno, int filas, int columnas)
    {
        int suma = 0;
        foreach (var (coord, cartas) in enemyByCoord)
            if (Manhattan(coord, cuartel, filas, columnas) <= 2 ||
                cartas.Any(e => Alcanzables(coord, Mov(e), Tipo(e), terreno, filas, columnas).Contains(cuartel)))
                suma += cartas.Sum(Fuerza);
        return suma;
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
        // ANTI-SALLY (amenaza por ALCANCE): una celda está EXPUESTA si un stack
        // enemigo puede ALCANZARLA el próximo turno y batirnos allí. Cubre el caso
        // que la predicción por vector NO ve: un rival ATRINCHERADO y quieto (p. ej.
        // apilado en su cuartel) que SALE a batir a las unidades que se acercan a
        // farmear. Suma la fuerza de TODAS las cartas enemigas que alcanzan `c`; si
        // supera nuestra defensa allí, `c` es una trampa (perderíamos las tropas).
        bool ExpuestoASalida(string c)
        {
            if (cuartelCoords.Contains(c) && cuartelOwner.GetValueOrDefault(c) == botUid)
                return false; // mi propio cuartel: su defensa se gestiona aparte
            int fuerzaEntrante = 0;
            foreach (var (ecoord, ecartas) in enemyByCoord)
            {
                if (ecoord == c) continue; // combate directo ya lo cubre Segura
                if (ecartas.Any(ec => Alcanzables(ecoord, Mov(ec), Tipo(ec), terreno, filas, columnas).Contains(c)))
                    fuerzaEntrante += ecartas.Sum(Fuerza);
            }
            return fuerzaEntrante > myD;
        }
        var sinPeligro = seguras.Where(c => !CaeEnemigoQueMeGana(c) && !ExpuestoASalida(c)).ToList();
        if (sinPeligro.Count > 0) seguras = sinPeligro;

        // NO ROMPER EL ASEDIO: si esta unidad está pegada a un cuartel enemigo
        // DEFENDIDO (forma parte del cerco), no la mandamos a farmear/cazar lejos:
        // se restringe a celdas que sigan pegadas a ese cuartel (mantener el anillo)
        // hasta que se pueda tomar (por asalto en masa o tras un disparo lejano).
        var cuartelCercado = enemyCuarteles.FirstOrDefault(q =>
            Manhattan(coord, q, filas, columnas) == 1
            && CuartelDefendido(q, cuartelOwner, botUid, enemyByCoord));
        if (cuartelCercado != null)
        {
            var mantieneCerco = seguras
                .Where(c => Manhattan(c, cuartelCercado, filas, columnas) <= 1)
                .ToList();
            if (mantieneCerco.Count > 0) seguras = mantieneCerco;
        }

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
        //    IMPORTANTE (economía): se incluye la celda ACTUAL como candidata si ya
        //    farmea y es segura, para NO abandonar un rayo (+10) / isla (+7) que ya
        //    ocupamos por otra celda de igual o menor valor. La energía es la
        //    condición de victoria: mantener el farmeo premium es prioritario.
        var conFarm = seguras.Where(c => Farm(c) > 0).ToList();
        if (Farm(coord) > 0 && !CaeEnemigoQueMeGana(coord)) conFarm.Add(coord);
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

        // Equilibrio, sesgado por estilo: _sesgoFrente>0 (agresivo) exige que la
        // energía esté MUCHO más cerca para desviarse a por ella; <0 (defensivo) se
        // desvía a farmear con más facilidad. =0 reproduce el criterio clásico.
        if (mejorEnergia != null && dEnergia + _sesgoFrente < dEnemigo) return mejorEnergia;
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
        HashSet<string> enemyCuarteles, Dictionary<string, string> cuartelOwner, string botUid,
        int sesgo = 0)
        => GanaGrupo(myF, myD, coord, enemyByCoord, enemyCuarteles, cuartelOwner, botUid, sesgo);

    // ¿Gana un GRUPO propio (suma de fuerza/defensa) atacando esa celda? El
    // combate del juego suma las cartas por celda, así que apilar unidades es la
    // forma legítima de ganar peleas que en solitario se pierden.
    private bool GanaGrupo(
        int sumF, int sumD, string coord,
        Dictionary<string, List<Dictionary<string, object?>>> enemyByCoord,
        HashSet<string> enemyCuarteles, Dictionary<string, string> cuartelOwner, string botUid,
        int sesgo = 0)
    {
        // SEGURIDAD: la toma de un cuartel NUNCA se sesga. Creerse capaz de tomar
        // un cuartel que no se gana equivale a regalar la partida, así que ahí la
        // lectura es siempre estricta aunque el perfil sea agresivo.
        if (enemyCuarteles.Contains(coord)) sesgo = 0;

        if (!enemyByCoord.TryGetValue(coord, out var enemigos) || enemigos.Count == 0)
            return !enemyCuarteles.Contains(coord) || sumF > UmbralCuartel;
        int fe = enemigos.Sum(Fuerza), de = enemigos.Sum(Defensa);
        if (enemyCuarteles.Contains(coord) && CuartelDefendido(coord, cuartelOwner, botUid, enemyByCoord))
            de += UmbralCuartel; // el cuartel defendido suma +UmbralCuartel de defensa al dueño
        // Poder neto propio estrictamente mayor. `sesgo`>0 baja el listón (más
        // valiente al comprometer un GRUPO); solo se aplica fuera de cuarteles.
        return (sumF - de) > (fe - sumD - sesgo);
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

    // ── Catálogo de cartas COMPARTIDO entre TODOS los runners de bots ──────────
    // Las definiciones de cartas son estáticas durante las partidas (y cambian
    // muy raramente). Antes cada bot releía de Firestore cada carta de su mano
    // —una a una— y consultaba las especiales en CADA turno, lo que multiplicaba
    // las lecturas. Ahora la colección `Cartas` se lee UNA vez y se sirve de
    // memoria para todos los bots, refrescándose solo si supera el TTL.
    private static volatile Dictionary<string, Dictionary<string, object?>>? _catalogo;
    private static DateTime _catalogoCargado = DateTime.MinValue;
    private static readonly TimeSpan _catalogoTtl = TimeSpan.FromMinutes(10);
    private static Task<Dictionary<string, Dictionary<string, object?>>>? _catalogoCargando;
    private static readonly object _catGate = new();

    public WarZeroBot(
        WarZeroFirestore fs, WarZeroService svc,
        WarZeroBotOptions? options = null, IBotStrategy? strategy = null,
        PerfilBot? perfil = null)
    {
        _fs = fs; _svc = svc;
        _opt = options ?? new WarZeroBotOptions();
        // Si no se inyecta una estrategia explícita, se construye la Estratega con
        // el PERFIL del bot (dificultad + estilo). Sin perfil → medio/equilibrado.
        _strategy = strategy ?? new EstrategaStrategy(_opt, perfil ?? PerfilBot.PorDefecto);
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
            // Cadencia de sondeo según el modo: en diario/turno12h el turno tarda
            // HORAS en resolverse, así que sondear cada 60 s solo malgasta lecturas.
            await Task.Delay(DelayPollBucle(estado), ct);
        }
    }

    /// Intervalo de sondeo del bucle de partida del bot según el modo de turno.
    private TimeSpan DelayPollBucle(Dictionary<string, object?> estado)
    {
        var modo = M.Str(M.Get(estado, "modoTurno"));
        return modo switch
        {
            "diario" or "turno12h" => TimeSpan.FromMinutes(3),
            _ => _opt.PollInterval, // rápida u otros: valor configurado (60 s)
        };
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
    /// bot que aún no figuran en `especialesCompradas` de esta partida. Se filtra
    /// del catálogo EN MEMORIA (antes era una consulta a Firestore cada turno).
    private async Task<List<Dictionary<string, object?>>> CargarGeneralesAsync(
        int ejercitoId, HashSet<string> yaCompradas, CancellationToken ct)
    {
        var res = new List<Dictionary<string, object?>>();
        try
        {
            var cat = await ObtenerCatalogoAsync(ct);
            foreach (var kv in cat)
            {
                var map = kv.Value;
                if (M.Int(M.Get(map, "Condicion", "condicion")) != 5) continue;
                if (yaCompradas.Contains(kv.Key)) continue;
                if (ejercitoId > 0 && M.Int(M.Get(map, "Ejercito", "ejercito")) != ejercitoId) continue;
                // Copia superficial: no exponer las entradas del caché compartido.
                res.Add(new Dictionary<string, object?>(map));
            }
        }
        catch (Exception ex) { Console.Error.WriteLine($"[WZ][bot] filtrar generales falló: {ex}"); }
        return res;
    }

    private async Task<Dictionary<string, Dictionary<string, object?>>> CargarCartasAsync(List<string> ids, CancellationToken ct)
    {
        var res = new Dictionary<string, Dictionary<string, object?>>();
        try
        {
            var cat = await ObtenerCatalogoAsync(ct);
            foreach (var id in ids.Distinct())
            {
                if (cat.TryGetValue(id, out var map))
                    // Copia superficial: el llamante puede escribir claves de nivel
                    // superior sin corromper el caché compartido.
                    res[id] = new Dictionary<string, object?>(map);
            }
        }
        catch (Exception ex) { Console.Error.WriteLine($"[WZ][bot] leer cartas de caché falló: {ex}"); }
        return res;
    }

    // ── Catálogo compartido: carga perezosa con TTL y deduplicación ────────────
    // Devuelve el catálogo de `Cartas` en memoria. Si está fresco (< TTL) lo sirve
    // sin tocar Firestore. Si caducó, dispara UNA recarga compartida (aunque
    // varios bots la pidan a la vez) y, si esa recarga falla, sigue sirviendo el
    // catálogo anterior para no romper el turno.
    private async Task<Dictionary<string, Dictionary<string, object?>>> ObtenerCatalogoAsync(CancellationToken ct)
    {
        var cache = _catalogo;
        if (cache != null && (DateTime.UtcNow - _catalogoCargado) < _catalogoTtl)
            return cache;

        Task<Dictionary<string, Dictionary<string, object?>>> carga;
        lock (_catGate)
        {
            if (_catalogo != null && (DateTime.UtcNow - _catalogoCargado) < _catalogoTtl)
                return _catalogo;
            // Reutiliza una recarga en curso para no lanzar N lecturas simultáneas.
            _catalogoCargando ??= CargarCatalogoAsync();
            carga = _catalogoCargando;
        }

        try { return await carga; }
        catch
        {
            // Recarga fallida: si teníamos catálogo previo, seguimos con él.
            return _catalogo ?? new Dictionary<string, Dictionary<string, object?>>();
        }
    }

    private async Task<Dictionary<string, Dictionary<string, object?>>> CargarCatalogoAsync()
    {
        try
        {
            var nuevo = new Dictionary<string, Dictionary<string, object?>>();
            // Sin token por partida: es una carga compartida; que una partida se
            // cancele no debe abortar la recarga del resto. Es una lectura de toda
            // la colección UNA vez cada 10 min, compartida por todos los bots.
            var snap = await _fs.Db.Collection("Cartas").GetSnapshotAsync(CancellationToken.None);
            foreach (var d in snap.Documents)
            {
                var map = M.Map(M.FromFs(d.ToDictionary()));
                map["id"] = d.Id;
                nuevo[d.Id] = map;
            }
            _catalogo = nuevo;
            _catalogoCargado = DateTime.UtcNow;
            return nuevo;
        }
        finally
        {
            lock (_catGate) { _catalogoCargando = null; }
        }
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