using System.Text.Json;
using Google.Cloud.Firestore;

// ─────────────────────────────────────────────────────────────────────────────
// WarZeroBot.cs
//
// Bot server-side para RELLENAR SALAS. Corre dentro de Fenrir y reutiliza
// WarZeroService (EntrarAsync / CerrarTurnoAsync / LeerEstadoAsync /
// ActualizarStatsAsync) y WarZeroFirestore, así que juega exactamente contra la
// misma lógica autoritativa que un cliente humano; no toca la resolución.
//
// Ciclo de vida (RunForLobbyAsync):
//   1. Se añade a `jugadores[]` del doc Partidas/{id} como {uid, alias, listo:true}
//      (+ `participantes`), ocupando un hueco de la sala.
//   2. Espera a que el host arranque (estado == "en_curso").
//   3. Entra (EntrarAsync): recibe energías iniciales, cuartel/obelisco y mano.
//   4. Bucle por turno: sondea el estado y, cuando es un turno nuevo en el que el
//      bot sigue activo y NO ha cerrado, decide una jugada legal y cierra.
//
// REGLA CLAVE (verificada en _serializarTablero del cliente): al cerrar turno
// cada jugador reenvía SOLO sus propias cartas, y el servidor fusiona las de
// todos. Por tanto el bot DEBE reenviar todas sus unidades cada turno
// (posiciones actuales + despliegues + movimientos) o su ejército desaparece.
// De eso se encarga la estrategia (IBotStrategy): siempre arrastra su ejército.
// ─────────────────────────────────────────────────────────────────────────────

/// Ajustes de comportamiento del bot.
public class WarZeroBotOptions
{
    /// Cada cuánto sondea el estado de la partida.
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// Pausa antes de cerrar el turno, para no resolver de forma instantánea
    /// (da sensación de que "piensa" y evita cerrar antes que los humanos vean).
    public TimeSpan ThinkDelay { get; set; } = TimeSpan.FromSeconds(3);

    /// Tiempo máximo esperando a que la sala arranque antes de rendirse.
    public TimeSpan MaxWaitStart { get; set; } = TimeSpan.FromMinutes(15);

    /// Máximo de cartas que despliega por turno (aunque tenga energía/mano de sobra).
    public int MaxDeploysPorTurno { get; set; } = 3;
}

/// Contexto que recibe la estrategia para decidir la jugada del turno.
public class BotContext
{
    /// Estado completo de la partida (mismo shape que el doc de Firestore).
    public required Dictionary<string, object?> Estado { get; init; }

    public required string BotUid { get; init; }
    public required int Turno { get; init; }

    /// Coordenada del cuartel/obelisco del bot (donde puede desplegar). Puede ser
    /// "" si aún no tiene cuartel asignado.
    public required string Cuartel { get; init; }

    /// Energía disponible del bot.
    public required int Energia { get; init; }

    /// Mano actual del bot (lista de ids de carta).
    public required List<string> Mano { get; init; }

    /// Catálogo de las cartas de la mano (id -> mapa completo de la carta, con las
    /// claves Nombre/Fuerza/Defensa/Coste/Movimiento/Tipo…). Lo precarga el
    /// orquestador para que la estrategia no tenga que leer Firestore.
    public required Dictionary<string, Dictionary<string, object?>> CatalogoMano { get; init; }

    /// Zona del bot ("north"/"south"/… o "" si no se pudo determinar). Se usa como
    /// ownerZone al desplegar en el cuartel.
    public required string Zona { get; init; }
}

/// Jugada resuelta: qué celdas propias enviar y cómo queda la mano/energía.
public class BotMove
{
    /// coord -> lista de cartas propias (incluye ejército arrastrado + despliegues).
    public Dictionary<string, List<Dictionary<string, object?>>> Celdas { get; init; } = new();

    /// Mano tras la jugada (sin las cartas desplegadas).
    public List<string> ManoResultante { get; init; } = new();

    /// Energía total gastada este turno (se persiste como delta negativo).
    public int EnergiaGastada { get; init; }
}

/// Estrategia de decisión. Implementaciones distintas dan bots más o menos listos.
public interface IBotStrategy
{
    BotMove DecidirJugada(BotContext ctx);
}

/// Estrategia por defecto: conserva SIEMPRE su ejército (lo reenvía intacto) y
/// despliega en su cuartel las cartas de la mano que pueda pagar. No mueve ni
/// ataca todavía: su objetivo es rellenar la sala sin bloquearla nunca.
public class ReclutaStrategy : IBotStrategy
{
    private readonly int _maxDeploys;
    public ReclutaStrategy(int maxDeploysPorTurno = 3) => _maxDeploys = Math.Max(0, maxDeploysPorTurno);

    public BotMove DecidirJugada(BotContext ctx)
    {
        // 1) Arrastrar el ejército actual: todas las cartas propias del tablero,
        //    tal cual (conservan coord/ownerUid/ownerZone/instanceId).
        var celdas = new Dictionary<string, List<Dictionary<string, object?>>>();
        var tablero = M.Map(M.Get(ctx.Estado, "tablero"));
        var zona = ctx.Zona;

        foreach (var (coord, cartasRaw) in tablero)
        {
            foreach (var cRaw in M.List(cartasRaw))
            {
                var carta = M.Map(cRaw);
                if (M.Str(M.Get(carta, "ownerUid")) != ctx.BotUid) continue;

                if (!celdas.TryGetValue(coord, out var lst)) { lst = new(); celdas[coord] = lst; }
                lst.Add(CopiarCarta(carta));

                // Aprovecha para fijar la zona si aún no la teníamos.
                if (zona == "") zona = M.Str(M.Get(carta, "ownerZone"));
            }
        }

        // 2) Desplegar desde la mano en el cuartel, mientras alcance la energía.
        var mano = new List<string>(ctx.Mano);
        int energia = ctx.Energia;
        int gastado = 0;
        int desplegadas = 0;

        if (ctx.Cuartel != "")
        {
            // Recorremos una copia: vamos quitando de `mano` las que desplegamos.
            foreach (var id in ctx.Mano)
            {
                if (desplegadas >= _maxDeploys) break;
                if (!ctx.CatalogoMano.TryGetValue(id, out var cartaBase)) continue;

                int coste = M.Int(M.Get(cartaBase, "Coste", "coste"));
                if (coste > energia) continue; // no la puede pagar: prueba la siguiente

                var celda = CopiarCarta(cartaBase);
                celda["id"] = id;                        // asegura el id (doc id del catálogo)
                celda["ownerUid"] = ctx.BotUid;
                celda["ownerZone"] = zona;
                celda["instanceId"] = Guid.NewGuid().ToString("N");

                if (!celdas.TryGetValue(ctx.Cuartel, out var lst)) { lst = new(); celdas[ctx.Cuartel] = lst; }
                lst.Add(celda);

                energia -= coste;
                gastado += coste;
                desplegadas++;
                mano.Remove(id); // quita la primera aparición de ese id
            }
        }

        return new BotMove
        {
            Celdas = celdas,
            ManoResultante = mano,
            EnergiaGastada = gastado,
        };
    }

    /// Copia superficial suficiente para el payload (los valores son escalares o
    /// listas/mapas que no mutamos). Evita reutilizar la referencia del estado.
    private static Dictionary<string, object?> CopiarCarta(Dictionary<string, object?> c)
        => new(c);
}

/// Orquestador del bot para UNA partida. Instancia uno por sala a rellenar.
public class WarZeroBot
{
    private readonly WarZeroFirestore _fs;
    private readonly WarZeroService _svc;
    private readonly WarZeroBotOptions _opt;
    private readonly IBotStrategy _strategy;

    public WarZeroBot(
        WarZeroFirestore fs,
        WarZeroService svc,
        WarZeroBotOptions? options = null,
        IBotStrategy? strategy = null)
    {
        _fs = fs;
        _svc = svc;
        _opt = options ?? new WarZeroBotOptions();
        _strategy = strategy ?? new ReclutaStrategy(_opt.MaxDeploysPorTurno);
    }

    /// Ejecuta el ciclo de vida completo del bot en la sala [lobbyId] con la
    /// identidad [botUid]/[botAlias]. Devuelve al terminar la partida, al agotar
    /// la espera de arranque o al cancelarse. Nunca lanza: registra y sale.
    public async Task RunForLobbyAsync(
        string lobbyId, string botUid, string botAlias, CancellationToken ct = default)
    {
        try
        {
            Log(botUid, $"entrando a rellenar la sala {lobbyId}");

            if (!await UnirseYMarcarListoAsync(lobbyId, botUid, botAlias, ct))
            {
                Log(botUid, "no pude unirme a la sala (llena / iniciada / inexistente)");
                return;
            }

            if (!await EsperarArranqueAsync(lobbyId, ct))
            {
                Log(botUid, "la sala no arrancó a tiempo; me retiro");
                return;
            }

            // Inicializa energías/obelisco/mano.
            await _svc.EntrarAsync(new EntrarRequest { LobbyId = lobbyId, Uid = botUid });
            Log(botUid, "dentro de la partida; empiezo a jugar");

            await BuclePartidaAsync(lobbyId, botUid, ct);
            Log(botUid, "partida terminada");
        }
        catch (OperationCanceledException)
        {
            Log(botUid, "cancelado");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[WZ][bot {botUid}] error fatal: {ex}");
        }
    }

    // ── 1) Unirse a la sala ────────────────────────────────────────────────────
    private async Task<bool> UnirseYMarcarListoAsync(
        string lobbyId, string botUid, string botAlias, CancellationToken ct)
    {
        var db = _fs.Db;
        var lobbyRef = db.Collection("Partidas").Document(lobbyId);

        return await db.RunTransactionAsync(async tx =>
        {
            var snap = await tx.GetSnapshotAsync(lobbyRef, ct);
            if (!snap.Exists) return false;

            var data = M.Map(M.FromFs(snap.ToDictionary()));
            var estado = M.Str(M.Get(data, "estado"));
            if (estado != "esperando") return false; // ya arrancó o terminó

            var jugadores = M.List(M.Get(data, "jugadores")).Select(M.Map).ToList();
            int max = M.Int(M.Get(data, "maxJugadores"));
            bool yaEstoy = jugadores.Any(j => M.Str(M.Get(j, "uid")) == botUid);

            if (!yaEstoy)
            {
                if (max > 0 && jugadores.Count >= max) return false; // sala llena
                jugadores.Add(new Dictionary<string, object?>
                {
                    ["uid"] = botUid,
                    ["alias"] = botAlias,
                    ["ejercitoId"] = 0,
                    ["listo"] = true,
                });
            }
            else
            {
                // Ya estaba: solo asegura `listo:true`.
                foreach (var j in jugadores)
                    if (M.Str(M.Get(j, "uid")) == botUid) j["listo"] = true;
            }

            tx.Update(lobbyRef, new Dictionary<FieldPath, object>
            {
                // Reescribimos el array completo (arrayUnion no vale para modificar
                // el `listo` de una entrada existente).
                [new FieldPath("jugadores")] = jugadores,
                [new FieldPath("participantes")] = FieldValue.ArrayUnion(botUid),
            });
            return true;
        }, cancellationToken: ct);
    }

    // ── 2) Esperar arranque ────────────────────────────────────────────────────
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

    // ── 3) Bucle de partida ────────────────────────────────────────────────────
    private async Task BuclePartidaAsync(string lobbyId, string botUid, CancellationToken ct)
    {
        int ultimoTurnoJugado = 0;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var estado = await _svc.LeerEstadoAsync(lobbyId);
            if (estado == null) return;

            var estadoStr = M.Str(M.Get(estado, "estado"));
            if (estadoStr == "finalizada") return;

            var turno = M.Int(M.Get(estado, "turnoActual"));

            // ¿Sigo activo? Si me eliminaron, no tengo que cerrar más.
            var eliminados = M.List(M.Get(estado, "jugadoresEliminados")).Select(M.Str).ToHashSet();
            if (eliminados.Contains(botUid)) return;

            // ¿Ya cerré este turno? Entonces solo espero a que avance.
            var cerradoPor = M.List(M.Get(estado, "cerradoPor")).Select(M.Str).ToHashSet();
            bool yaCerre = cerradoPor.Contains(botUid);

            if (turno > ultimoTurnoJugado && !yaCerre)
            {
                // "Pensar" un poco antes de cerrar.
                await Task.Delay(_opt.ThinkDelay, ct);

                // Releer por si algo cambió durante la pausa (otro jugador cerró y
                // avanzó el turno): evita cerrar un turno viejo.
                var fresco = await _svc.LeerEstadoAsync(lobbyId) ?? estado;
                if (M.Str(M.Get(fresco, "estado")) == "finalizada") return;
                var turnoFresco = M.Int(M.Get(fresco, "turnoActual"));
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

    // ── 4) Jugar un turno ──────────────────────────────────────────────────────
    private async Task JugarTurnoAsync(
        string lobbyId, string botUid, int turno,
        Dictionary<string, object?> estado, CancellationToken ct)
    {
        // Contexto del bot a partir del estado.
        var obeliscos = M.Map(M.Get(estado, "obeliscos"));
        var cuartel = M.Str(M.Get(obeliscos, botUid));

        var stats = M.Map(M.Get(estado, "statsPartida"));
        var miStat = M.Map(M.Get(stats, botUid));
        int energia = M.Int(M.Get(miStat, "energies"));
        var mano = M.List(M.Get(miStat, "mano")).Select(M.Str).Where(s => s != "").ToList();

        var zona = DeterminarZona(estado, botUid, cuartel);
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
        };

        BotMove jugada;
        try { jugada = _strategy.DecidirJugada(ctx); }
        catch (Exception ex)
        {
            // Si la estrategia falla, cerramos con el ejército intacto para no
            // bloquear la sala (arrastre puro, sin despliegues).
            Console.Error.WriteLine($"[WZ][bot {botUid}] estrategia falló, cierro seguro: {ex}");
            jugada = new BotMove
            {
                Celdas = ArrastrarEjercito(estado, botUid),
                ManoResultante = mano,
                EnergiaGastada = 0,
            };
        }

        // Persistir energía/mano ANTES de cerrar (igual que el cliente): así el
        // reparto de fin de turno del servidor opera sobre la mano correcta.
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
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[WZ][bot {botUid}] actualizarStats falló (sigo): {ex}");
            }
        }

        // Cerrar el turno con las celdas propias. `acciones` vacío de momento.
        var req = new CerrarTurnoRequest
        {
            LobbyId = lobbyId,
            Uid = botUid,
            Turno = turno,
            Celdas = JsonSerializer.SerializeToElement(jugada.Celdas),
            Acciones = JsonSerializer.SerializeToElement(Array.Empty<object>()),
        };

        var resp = await _svc.CerrarTurnoAsync(req);
        Log(botUid, $"turno {turno} cerrado (desplegadas={jugada.Celdas.Values.Sum(l => l.Count)} " +
                    $"celdas, resuelto={resp.Resuelto})");
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    /// Sólo el arrastre del ejército propio (sin despliegues). Red de seguridad.
    private static Dictionary<string, List<Dictionary<string, object?>>> ArrastrarEjercito(
        Dictionary<string, object?> estado, string botUid)
    {
        var celdas = new Dictionary<string, List<Dictionary<string, object?>>>();
        var tablero = M.Map(M.Get(estado, "tablero"));
        foreach (var (coord, cartasRaw) in tablero)
        {
            foreach (var cRaw in M.List(cartasRaw))
            {
                var carta = M.Map(cRaw);
                if (M.Str(M.Get(carta, "ownerUid")) != botUid) continue;
                if (!celdas.TryGetValue(coord, out var lst)) { lst = new(); celdas[coord] = lst; }
                lst.Add(new Dictionary<string, object?>(carta));
            }
        }
        return celdas;
    }

    /// Carga los mapas completos de las cartas de la mano desde la colección Cartas.
    private async Task<Dictionary<string, Dictionary<string, object?>>> CargarCartasAsync(
        List<string> ids, CancellationToken ct)
    {
        var db = _fs.Db;
        var res = new Dictionary<string, Dictionary<string, object?>>();
        foreach (var id in ids.Distinct())
        {
            try
            {
                var snap = await db.Collection("Cartas").Document(id).GetSnapshotAsync(ct);
                if (!snap.Exists) continue;
                var map = M.Map(M.FromFs(snap.ToDictionary()));
                map["id"] = id; // el doc id ES el id de carta
                res[id] = map;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[WZ][bot] leer carta {id} falló: {ex}");
            }
        }
        return res;
    }

    /// Determina la zona del bot: reutiliza la de una unidad propia si la hay; si
    /// no, la deriva de la coord del cuartel y del tamaño de la rejilla. Devuelve
    /// "" si no se puede determinar (el servidor no exige zona: el combate es por
    /// ownerUid).
    private string DeterminarZona(Dictionary<string, object?> estado, string botUid, string cuartel)
    {
        // 1) Reutilizar zona de una unidad propia ya en tablero.
        var tablero = M.Map(M.Get(estado, "tablero"));
        foreach (var (_, cartasRaw) in tablero)
            foreach (var cRaw in M.List(cartasRaw))
            {
                var carta = M.Map(cRaw);
                if (M.Str(M.Get(carta, "ownerUid")) == botUid)
                {
                    var z = M.Str(M.Get(carta, "ownerZone"));
                    if (z != "") return z;
                }
            }

        // 2) Derivar de la coord del cuartel (e.g. "B5") + dimensiones de rejilla.
        if (cuartel.Length < 2) return "";
        int ri = char.ToUpperInvariant(cuartel[0]) - 'A';
        if (!int.TryParse(cuartel[1..], out int col1)) return "";
        int ci = col1 - 1;

        int jugadores = M.List(M.Get(estado, "jugadores")).Count;
        var (filas, columnas) = DimensionesPreset(jugadores);

        bool north = ri <= 2, south = ri >= filas - 3, west = ci <= 2, east = ci >= columnas - 3;
        if (north && east) return "ne";
        if (north && west) return "nw";
        if (south && east) return "se";
        if (south && west) return "sw";
        if (north) return "north";
        if (south) return "south";
        if (west) return "west";
        if (east) return "east";
        return "";
    }

    /// Dimensiones de rejilla por nº de jugadores (espejo de GameConfig del cliente).
    private static (int filas, int columnas) DimensionesPreset(int jugadores) => jugadores switch
    {
        2 => (6, 10),
        6 => (10, 16),
        8 => (12, 18),
        _ => (8, 14), // 4 jugadores (por defecto)
    };

    private static void Log(string botUid, string msg)
        => Console.WriteLine($"[WZ][bot {botUid}] {msg}");
}