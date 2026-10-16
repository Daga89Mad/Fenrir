using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Google.Cloud.Firestore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;

using Tablero = System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<System.Collections.Generic.Dictionary<string, object?>>>;
using EfectosCelda = System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<System.Collections.Generic.Dictionary<string, object?>>>;

// ─────────────────────────────────────────────────────────────────────────────
// ArenaOffline.cs  —  ARENA DE PARTIDAS SIMULADAS (v12)
//
// Juega partidas COMPLETAS en memoria, sin Firestore (salvo leer el catálogo
// de cartas y el mapa una vez), enfrentando bots (cualquier dificultad/estilo)
// y "humanos simulados" (EstrategiaHumanoSimulado), con el MISMO motor de
// resolución que el servidor (SimuladorTurno = puerto fiel de la resolución
// real + Farmeo.Calcular + reparto + eliminaciones). Sirve para medir una
// versión de los bots en cientos de partidas en minutos, en vez de esperar
// partidas reales.
//
// REST (protegido por X-Admin-Secret = WARZERO_ARENA_SECRET o WARZERO_ADMIN_SECRET):
//   POST   /warzero/arena/partidas          lanza un trabajo (202 + jobId)
//   GET    /warzero/arena/partidas          lista trabajos
//   GET    /warzero/arena/partidas/{id}     estado, progreso y RESUMEN (por jugador y por partida)
//   GET    /warzero/arena/partidas/{id}/partida/{n}   partida completa nº n en formato EstudioPartidas
//   DELETE /warzero/arena/partidas/{id}     cancela / borra
//
// Cuerpo de POST (ejemplo):
// {
//   "partidas": 100, "mapaId": "Clasica4J", "maxTurnos": 80, "semilla": 42,
//   "guardarPartidas": 3, "persistirEstudio": false,
//   "jugadores": [
//     { "uid": "H", "alias": "pescador", "tipo": "humano", "perfil": "pescador", "ejercitoId": 2, "mazo": "pescador" },
//     { "uid": "bot_0", "alias": "Itachi", "tipo": "bot", "dificultad": "maestro", "estilo": "defensivo", "ejercitoId": 4, "mazo": "maestro" },
//     { "uid": "bot_1", "alias": "Pain",   "tipo": "bot", "dificultad": "alto",    "estilo": "equilibrado", "ejercitoId": 1 },
//     { "uid": "bot_10","alias": "Madara", "tipo": "bot", "dificultad": "alto",    "estilo": "agresivo", "ejercitoId": 2 }
//   ]
// }
// `mazo`: "defecto" (8 PorDefecto del ejército al azar, como el servidor),
// "maestro" (MazosMaestro), "pescador"/"soyloki" (el mazo real de esos
// jugadores en las partidas de estudio) o una lista de NOMBRES de carta.
//
// Las partidas se juegan en SERIE en un hilo de fondo (la instancia de Render
// es pequeña); el progreso se consulta por GET. Los resultados viven en memoria
// del proceso (y el resumen se guarda en Firestore `Arena/{jobId}` al acabar).
// Con `guardarPartidas` N, las N primeras partidas se conservan completas en
// memoria (formato EstudioPartidas) y, con `persistirEstudio`, se escriben
// además en EstudioPartidas/arena_{jobId}_{n} para exportarlas como siempre.
// ─────────────────────────────────────────────────────────────────────────────

public sealed class ArenaJugadorRequest
{
    public string Uid { get; set; } = "";
    public string? Alias { get; set; }
    /// "bot" | "humano"
    public string Tipo { get; set; } = "bot";
    public string? Dificultad { get; set; } = "alto";
    public string? Estilo { get; set; } = "equilibrado";
    /// Perfil del humano simulado: "pescador" | "soyloki".
    public string? Perfil { get; set; }
    public int EjercitoId { get; set; } = 1;
    /// "defecto" | "maestro" | "pescador" | "soyloki" | ["Nombre de carta", ...]
    public JsonElement? Mazo { get; set; }
}

public sealed class ArenaRequest
{
    public int Partidas { get; set; } = 10;
    public string? MapaId { get; set; } = "Clasica4J";
    public int MaxTurnos { get; set; } = 80;
    public int? Semilla { get; set; }
    public int GuardarPartidas { get; set; } = 2;
    public bool PersistirEstudio { get; set; } = false;
    /// Rota los cuarteles entre partidas (si no, la asignación depende de la semilla).
    public bool RotarCuarteles { get; set; } = true;
    public List<ArenaJugadorRequest> Jugadores { get; set; } = new();
}

public sealed class ArenaJob
{
    public string Id { get; init; } = "";
    public string Estado { get; set; } = "pendiente";   // pendiente | en_curso | terminado | error | cancelado
    public int Completadas { get; set; }
    public int Total { get; set; }
    public DateTime CreadoUtc { get; init; } = DateTime.UtcNow;
    public DateTime? TerminadoUtc { get; set; }
    public string? Error { get; set; }
    public ArenaRequest Request { get; init; } = new();
    public List<Dictionary<string, object?>> Partidas { get; } = new();       // una fila por partida
    public List<object> PartidasCompletas { get; } = new();                   // formato EstudioPartidas (JSON)
    public Dictionary<string, object?>? Resumen { get; set; }
    [JsonIgnore] public System.Threading.CancellationTokenSource Cts { get; } = new();
}

// ─────────────────────────────────────────────────────────────────────────────
// SERVICIO (cola de trabajos en memoria)
// ─────────────────────────────────────────────────────────────────────────────
public sealed class ArenaService
{
    private readonly WarZeroFirestore _fs;
    private readonly ConcurrentDictionary<string, ArenaJob> _jobs = new();
    private readonly System.Threading.SemaphoreSlim _uno = new(1, 1);

    private Dictionary<string, Dictionary<string, object?>>? _catalogo;
    private DateTime _catalogoEn = DateTime.MinValue;
    private readonly ConcurrentDictionary<string, MotorArena.Mapa> _mapas = new();

    public ArenaService(WarZeroFirestore fs) { _fs = fs; }

    public IEnumerable<ArenaJob> Jobs => _jobs.Values.OrderByDescending(j => j.CreadoUtc);
    public ArenaJob? Get(string id) => _jobs.TryGetValue(id, out var j) ? j : null;

    public bool Cancelar(string id)
    {
        if (!_jobs.TryGetValue(id, out var j)) return false;
        if (j.Estado is "pendiente" or "en_curso") { j.Cts.Cancel(); j.Estado = "cancelado"; }
        else _jobs.TryRemove(id, out _);
        return true;
    }

    public ArenaJob Lanzar(ArenaRequest req)
    {
        var job = new ArenaJob
        {
            Id = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss") + "_" + Guid.NewGuid().ToString("N")[..6],
            Request = req,
            Total = Math.Clamp(req.Partidas, 1, 2000),
        };
        _jobs[job.Id] = job;
        _ = System.Threading.Tasks.Task.Run(() => ProcesarAsync(job));
        return job;
    }

    private async System.Threading.Tasks.Task ProcesarAsync(ArenaJob job)
    {
        await _uno.WaitAsync();
        try
        {
            if (job.Cts.IsCancellationRequested) return;
            job.Estado = "en_curso";
            var req = job.Request;
            var catalogo = await CatalogoAsync();
            var mapa = await MapaAsync(req.MapaId ?? "", req.Jugadores.Count);
            var defs = req.Jugadores.Select(j => MotorArena.Definir(j, catalogo)).ToList();
            var rng = new Random(req.Semilla ?? Environment.TickCount);

            for (int n = 0; n < job.Total; n++)
            {
                job.Cts.Token.ThrowIfCancellationRequested();
                int semillaPartida = rng.Next();
                bool guardar = n < req.GuardarPartidas;
                var res = MotorArena.JugarPartida(
                    defs, mapa, catalogo, semillaPartida, Math.Clamp(req.MaxTurnos, 5, 300),
                    guardar, req.RotarCuarteles ? n : 0, job.Cts.Token);
                res.Fila["n"] = n + 1;
                res.Fila["semilla"] = semillaPartida;
                job.Partidas.Add(res.Fila);
                if (guardar && res.Estudio != null)
                {
                    job.PartidasCompletas.Add(res.Estudio);
                    if (req.PersistirEstudio) await PersistirEstudioAsync(job.Id, n + 1, res);
                }
                job.Completadas = n + 1;
                job.Resumen = MotorArena.Resumir(job.Partidas, defs);
            }
            job.Estado = "terminado";
            job.TerminadoUtc = DateTime.UtcNow;
            await GuardarResumenAsync(job);
        }
        catch (OperationCanceledException) { job.Estado = "cancelado"; job.TerminadoUtc = DateTime.UtcNow; }
        catch (Exception ex)
        {
            job.Estado = "error"; job.Error = ex.ToString(); job.TerminadoUtc = DateTime.UtcNow;
            Console.Error.WriteLine($"[WZ][arena] trabajo {job.Id} falló: {ex}");
        }
        finally { _uno.Release(); }
    }

    private async System.Threading.Tasks.Task<Dictionary<string, Dictionary<string, object?>>> CatalogoAsync()
    {
        if (_catalogo != null && (DateTime.UtcNow - _catalogoEn) < TimeSpan.FromMinutes(10)) return _catalogo;
        var snap = await _fs.Db.Collection("Cartas").GetSnapshotAsync();
        var cat = new Dictionary<string, Dictionary<string, object?>>();
        foreach (var d in snap.Documents)
        {
            var map = M.Map(M.FromFs(d.ToDictionary()));
            map["id"] = d.Id;
            map.Remove("Descripcion"); map.Remove("Imagen");   // peso muerto para la IA
            cat[d.Id] = map;
        }
        _catalogo = cat; _catalogoEn = DateTime.UtcNow;
        return cat;
    }

    private async System.Threading.Tasks.Task<MotorArena.Mapa> MapaAsync(string mapaId, int jugadores)
    {
        var key = mapaId + "|" + jugadores;
        if (_mapas.TryGetValue(key, out var m)) return m;
        var mapa = MotorArena.Mapa.Preset(jugadores);
        if (mapaId != "")
        {
            var snap = await _fs.Db.Collection("Mapas").Document(mapaId).GetSnapshotAsync();
            if (!snap.Exists) throw new InvalidOperationException($"El mapa '{mapaId}' no existe en Mapas.");
            mapa = MotorArena.Mapa.Desde(mapaId, M.Map(M.FromFs(snap.ToDictionary())), jugadores);
        }
        _mapas[key] = mapa;
        return mapa;
    }

    private async System.Threading.Tasks.Task PersistirEstudioAsync(string jobId, int n, MotorArena.ResultadoPartida res)
    {
        try
        {
            var lobbyId = $"arena_{jobId}_{n}";
            foreach (var estado in res.EstadosPorTurno)
                await WarZeroEstudio.RegistrarTurnoSiHayBotAsync(_fs.Db, lobbyId, estado);
        }
        catch (Exception ex) { Console.Error.WriteLine($"[WZ][arena] persistir estudio falló: {ex.Message}"); }
    }

    private async System.Threading.Tasks.Task GuardarResumenAsync(ArenaJob job)
    {
        try
        {
            var doc = new Dictionary<string, object>
            {
                ["estado"] = job.Estado,
                ["creado"] = Timestamp.FromDateTime(DateTime.SpecifyKind(job.CreadoUtc, DateTimeKind.Utc)),
                ["terminado"] = Timestamp.FromDateTime(DateTime.UtcNow),
                ["partidas"] = job.Total,
                ["request"] = MotorArena.Plain(JsonSerializer.SerializeToElement(job.Request)) ?? new Dictionary<string, object?>(),
                ["resumen"] = (object?)job.Resumen ?? new Dictionary<string, object?>(),
            };
            await _fs.Db.Collection("Arena").Document(job.Id).SetAsync(doc, SetOptions.MergeAll);
        }
        catch (Exception ex) { Console.Error.WriteLine($"[WZ][arena] guardar resumen falló: {ex.Message}"); }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// MOTOR: una partida completa en memoria
// ─────────────────────────────────────────────────────────────────────────────
public static class MotorArena
{
    public const int EnergiasIniciales = 15;
    public const int TamanioManoInicial = 5;
    public const int TamanioMazo = 8;

    public sealed class Mapa
    {
        public string Id = "";
        public int Filas, Columnas;
        public Dictionary<string, string> Terreno = new();
        public HashSet<string> IslaCentral = new();
        public Dictionary<string, List<string>> Continentes = new();
        public List<string> Obeliscos = new();
        public List<string> Celdas = new();
        public bool ConFarmeo => IslaCentral.Count > 0 || Continentes.Count > 0;

        public static Mapa Preset(int jugadores)
        {
            var (f, c) = jugadores switch { 2 => (6, 10), 6 => (10, 16), 8 => (12, 18), _ => (8, 14) };
            var m = new Mapa { Filas = f, Columnas = c, Obeliscos = Coords.ObeliscosFallback(jugadores) };
            m.Celdas = Todas(f, c);
            return m;
        }

        public static Mapa Desde(string id, Dictionary<string, object?> data, int jugadores)
        {
            var m = Preset(jugadores);
            m.Id = id;
            int f = M.Int(M.Get(data, "filas")), c = M.Int(M.Get(data, "columnas"));
            if (f > 0 && c > 0) { m.Filas = f; m.Columnas = c; m.Celdas = Todas(f, c); m.Obeliscos = Coords.ObeliscosFallback(jugadores, f, c); }
            foreach (var (coord, val) in M.Map(M.Get(data, "terreno"))) m.Terreno[coord] = M.Str(val);
            foreach (var x in M.List(M.Get(data, "islaCentral")).Select(M.Str)) if (x != "") m.IslaCentral.Add(x);
            foreach (var (k, v) in M.Map(M.Get(data, "continentes")))
                m.Continentes[k] = M.List(v).Select(M.Str).Where(s => s != "").ToList();
            var ob = M.List(M.Get(data, "obeliscos")).Select(M.Str).Where(s => s != "").ToList();
            if (ob.Count == 0) ob = m.Continentes.Keys.Where(k => k != "").ToList();
            if (ob.Count > 0) m.Obeliscos = ob;
            return m;
        }

        private static List<string> Todas(int f, int c)
        {
            var l = new List<string>(f * c);
            for (int r = 0; r < f; r++) for (int cc = 1; cc <= c; cc++) l.Add($"{(char)('A' + r)}{cc}");
            return l;
        }
    }

    public sealed class Def
    {
        public string Uid = "", Alias = "", Tipo = "bot";
        public PerfilBot Perfil = PerfilBot.PorDefecto;
        public PerfilHumanoSimulado PerfilHumano = PerfilHumanoSimulado.Pescador;
        public int EjercitoId = 1;
        public List<string> Pool = new();
        public string Etiqueta => Tipo == "humano"
            ? $"humano:{PerfilHumano.Nombre}"
            : $"bot:{Perfil.Dificultad.ToString().ToLowerInvariant()}/{Perfil.Estilo.ToString().ToLowerInvariant()}";
    }

    public sealed class ResultadoPartida
    {
        public Dictionary<string, object?> Fila = new();
        public object? Estudio;
        public List<Dictionary<string, object?>> EstadosPorTurno = new();
    }

    // ── Mazos conocidos de los humanos de las partidas de estudio ──
    private static readonly Dictionary<string, string[]> MazosHumanos = new()
    {
        ["pescador"] = new[] { "Soldado carpa", "Aliento de dragon", "Tiburón de hierro", "Capitan de Dunan", "Cuervo de combate", "Lobo titán", "Zorro fantasma" },
        ["soyloki"] = new[] { "Protección de Azazel", "Leviatan", "Bestia del abismo", "Ala rota de Samael", "Modo caza", "Ogro de Azazel", "Belial", "Apocalipsis" },
    };

    public static Def Definir(ArenaJugadorRequest j, Dictionary<string, Dictionary<string, object?>> catalogo)
    {
        var d = new Def
        {
            Uid = string.IsNullOrWhiteSpace(j.Uid) ? Guid.NewGuid().ToString("N")[..8] : j.Uid.Trim(),
            Tipo = (j.Tipo ?? "bot").Trim().ToLowerInvariant() == "humano" ? "humano" : "bot",
            EjercitoId = j.EjercitoId <= 0 ? 1 : j.EjercitoId,
            Perfil = PerfilBot.Parse(j.Dificultad, j.Estilo),
            PerfilHumano = PerfilHumanoSimulado.Parse(j.Perfil),
        };
        d.Alias = string.IsNullOrWhiteSpace(j.Alias) ? d.Uid : j.Alias!.Trim();

        // Mazo
        string modo = "defecto";
        List<string>? nombres = null;
        if (j.Mazo is JsonElement m)
        {
            if (m.ValueKind == JsonValueKind.String) modo = (m.GetString() ?? "defecto").Trim().ToLowerInvariant();
            else if (m.ValueKind == JsonValueKind.Array)
                nombres = m.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString() ?? "").ToList();
        }
        else if (d.Tipo == "humano") modo = d.PerfilHumano.Nombre;
        else if (d.Perfil.EsMaestro) modo = "maestro";

        if (nombres == null)
        {
            if (modo == "maestro" && MazosMaestro.PorEjercito.TryGetValue(d.EjercitoId, out var mm)) nombres = mm.ToList();
            else if (MazosHumanos.TryGetValue(modo, out var mh)) nombres = mh.ToList();
        }
        if (nombres != null)
            d.Pool = ResolverNombres(catalogo, nombres);
        if (d.Pool.Count == 0)
        {
            // Mazo por defecto (como RepartirManoAsync): 8 PorDefecto del ejército al azar.
            var rnd = new Random(d.Uid.GetHashCode());
            var basicas = catalogo.Where(kv => M.Int(M.Get(kv.Value, "Condicion")) is not (1 or 5)).ToList();
            var del = basicas.Where(kv => M.Int(M.Get(kv.Value, "Ejercito")) == d.EjercitoId).ToList();
            if (del.Count == 0) del = basicas;
            var marc = del.Where(kv => M.Get(kv.Value, "PorDefecto") is bool b && b).ToList();
            var fuente = marc.Count > 0 ? marc : del;
            d.Pool = fuente.OrderBy(_ => rnd.Next()).Take(TamanioMazo).Select(kv => kv.Key).ToList();
        }
        return d;
    }

    /// Nombres → ids (cualquier ejército; sin evoluciones ni generales).
    private static List<string> ResolverNombres(Dictionary<string, Dictionary<string, object?>> catalogo, List<string> nombres)
    {
        var porNombre = new Dictionary<string, string>();
        foreach (var (id, c) in catalogo)
        {
            int cond = M.Int(M.Get(c, "Condicion"));
            if (cond == 1 || cond == 5) continue;
            var n = Normalizar(M.Str(M.Get(c, "Nombre")));
            if (n != "" && !porNombre.ContainsKey(n)) porNombre[n] = id;
        }
        var res = new List<string>();
        foreach (var n in nombres) if (porNombre.TryGetValue(Normalizar(n), out var id)) res.Add(id);
        return res;
    }

    private static string Normalizar(string s)
    {
        var d = s.Trim().ToLowerInvariant().Normalize(System.Text.NormalizationForm.FormD);
        var sb = new System.Text.StringBuilder(d.Length);
        foreach (var ch in d)
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch) != System.Globalization.UnicodeCategory.NonSpacingMark)
                sb.Append(ch);
        return sb.ToString();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // PARTIDA
    // ─────────────────────────────────────────────────────────────────────────
    private sealed class Jugador
    {
        public Def Def = new();
        public IBotStrategy Estrategia = null!;
        public Metricas Met = new();
    }

    private sealed class Metricas
    {
        public int TurnosVivo, Eliminado; public string Motivo = "";
        public double Farm, FarmRayo, FarmIsla, FarmCont, SumE, SumMano, SumN, SumPoder, SumCeldas, SumShare, SumCuN, SumCuP;
        public int TurnosConUnidades, PoderMax, EvoMax, Gens, Combates, CombGanados, CartasPerdidas, Conquistas, Acciones, Kills, MuertasHab, Adyacente, Disparos, Robos, Sacrificios;
        public int FarmT6, ET6, NT6;
        /// PC ganados con disparos (acción o trampa), dentro del PC total.
        public int PcDisparos;
    }

    public static ResultadoPartida JugarPartida(
        List<Def> defs, Mapa mapa, Dictionary<string, Dictionary<string, object?>> catalogo,
        int semilla, int maxTurnos, bool guardar, int rotacion, System.Threading.CancellationToken ct)
    {
        var rng = new Random(semilla);
        var jugadores = defs.Select(d => new Jugador
        {
            Def = d,
            Estrategia = d.Tipo == "humano"
                ? new EstrategiaHumanoSimulado(d.PerfilHumano, rng.Next())
                : (d.Perfil.EsMaestro
                    ? new MaestroStrategy(new WarZeroBotOptions(), d.Perfil)
                    : new EstrategaSoftmaxStrategy(new WarZeroBotOptions(), d.Perfil)),
        }).ToList();
        var porUid = jugadores.ToDictionary(j => j.Def.Uid);

        // ── Estado inicial (mismo shape que el doc de Partidas) ──
        var obeliscos = new Dictionary<string, string>();
        var candidatos = mapa.Obeliscos.Take(Math.Max(jugadores.Count, 1)).ToList();
        if (candidatos.Count < jugadores.Count) candidatos = Coords.ObeliscosFallback(jugadores.Count, mapa.Filas, mapa.Columnas);
        for (int i = 0; i < jugadores.Count && i < candidatos.Count; i++)
            obeliscos[jugadores[i].Def.Uid] = candidatos[(i + rotacion) % candidatos.Count];

        var stats = new Dictionary<string, Dictionary<string, object?>>();
        foreach (var j in jugadores)
        {
            var pool = j.Def.Pool.OrderBy(_ => rng.Next()).ToList();
            stats[j.Def.Uid] = new Dictionary<string, object?>
            {
                ["energies"] = EnergiasIniciales, ["pc"] = 0, ["victorias"] = 0, ["derrotas"] = 0,
                ["descargaUsada"] = false,
                ["mano"] = pool.Take(TamanioManoInicial).Cast<object?>().ToList(),
                ["mazoRestante"] = pool.Skip(TamanioManoInicial).Cast<object?>().ToList(),
                ["mazoPool"] = j.Def.Pool.Cast<object?>().ToList(),
                ["especialesCompradas"] = new List<object?>(),
                ["robosComprados"] = 0,
                ["modoBot"] = j.Def.Tipo == "bot" ? "libre" : "humano",
            };
        }
        var tablero = new Tablero();
        var efectos = new EfectosCelda();
        var eliminados = new HashSet<string>();
        var ruinas = new HashSet<string>();
        var descargas = new Dictionary<string, int>();
        var rayos = new List<Dictionary<string, object?>>();
        var historial = new List<Dictionary<string, object?>>();
        var botsUids = jugadores.Where(j => j.Def.Tipo == "bot").Select(j => j.Def.Uid).ToList();
        if (botsUids.Count == 0) botsUids.Add(jugadores[0].Def.Uid);   // el estudio exige algún bot
        string? ganador = null;
        var estadosGuardados = new List<Dictionary<string, object?>>();
        var turnosEstudio = new List<object>();
        int turno = 1;

        Dictionary<string, object?> EstadoActual(int t, string estadoPartida) => new()
        {
            ["mapaId"] = mapa.Id == "" ? null : mapa.Id,
            ["turnoActual"] = t,
            ["estado"] = estadoPartida,
            ["ganadorUid"] = ganador,
            ["jugadores"] = jugadores.Select(j => (object?)new Dictionary<string, object?>
            { ["uid"] = j.Def.Uid, ["alias"] = j.Def.Alias, ["ejercitoId"] = j.Def.EjercitoId, ["listo"] = true }).ToList(),
            ["botsUids"] = botsUids.Cast<object?>().ToList(),
            ["participantes"] = jugadores.Select(j => (object?)j.Def.Uid).ToList(),
            ["obeliscos"] = obeliscos.ToDictionary(kv => kv.Key, kv => (object?)kv.Value),
            ["statsPartida"] = stats.ToDictionary(kv => kv.Key, kv => (object?)kv.Value),
            ["tablero"] = tablero.ToDictionary(kv => kv.Key, kv => (object?)kv.Value.Cast<object?>().ToList()),
            ["efectosCelda"] = efectos.ToDictionary(kv => kv.Key, kv => (object?)kv.Value.Cast<object?>().ToList()),
            ["jugadoresEliminados"] = eliminados.Cast<object?>().ToList(),
            ["cuartelesDestruidos"] = ruinas.Select(c => (object?)new Dictionary<string, object?> { ["coord"] = c }).ToList(),
            ["descargasCuartel"] = descargas.ToDictionary(kv => kv.Key, kv => (object?)kv.Value),
            ["rayos"] = rayos.Cast<object?>().ToList(),
            ["historialCombates"] = historial.Cast<object?>().ToList(),
            ["cerradoPor"] = new List<object?>(),
            ["modoTurno"] = "rapido",
        };

        for (; turno <= maxTurnos; turno++)
        {
            ct.ThrowIfCancellationRequested();
            var estado = EstadoActual(turno, "en_curso");
            var activos = jugadores.Where(j => !eliminados.Contains(j.Def.Uid)).ToList();

            // ── 1. Decisiones ──
            var planes = new List<SimuladorTurno.Plan>();
            var movimientos = new List<object?>();
            var costeAcciones = new Dictionary<string, int>();
            foreach (var j in activos)
            {
                string uid = j.Def.Uid;
                var st = stats[uid];
                var ctx = ConstruirContexto(estado, j, mapa, catalogo, turno, obeliscos.GetValueOrDefault(uid, ""));
                BotMove jugada;
                try { jugada = j.Estrategia.DecidirJugada(ctx); }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[WZ][arena] {uid} T{turno} estrategia falló: {ex.Message}");
                    jugada = new BotMove { Celdas = Arrastrar(tablero, uid), ManoResultante = ctx.Mano };
                }
                var celdas = Sanear(jugada.Celdas, tablero, uid);

                // Prepago (despliegues/evoluciones/generales; negativo = sacrificio).
                int energia = M.Int(st["energies"]);
                if (jugada.EnergiaGastada > energia)
                {
                    Console.Error.WriteLine($"[WZ][arena] {uid} T{turno} gasto {jugada.EnergiaGastada} > energía {energia}: jugada rechazada, arrastre");
                    celdas = Arrastrar(tablero, uid);
                    jugada = new BotMove { Celdas = celdas, ManoResultante = ctx.Mano };
                }
                if (jugada.EnergiaGastada < 0) j.Met.Sacrificios++;
                energia -= jugada.EnergiaGastada;
                st["mano"] = jugada.ManoResultante.Cast<object?>().ToList();
                if (!string.IsNullOrEmpty(jugada.EspecialComprada))
                {
                    var esp = M.List(st["especialesCompradas"]).ToList();
                    esp.Add(jugada.EspecialComprada); st["especialesCompradas"] = esp; j.Met.Gens++;
                }
                if (jugada.RobosComprados > 0) { st["robosComprados"] = M.Int(st["robosComprados"]) + jugada.RobosComprados; j.Met.Robos += jugada.RobosComprados; }

                // Acciones: se cobran en la resolución, por orden, mientras haya energía.
                var acciones = new List<Dictionary<string, object?>>();
                bool descUsada = M.Bool(st["descargaUsada"]);
                foreach (var a in jugada.Acciones)
                {
                    int coste;
                    if (AccionesTacticas.EsDescarga(a))
                    {
                        if (descUsada || M.Str(M.Get(a, "origen")) != obeliscos.GetValueOrDefault(uid, "")) continue;
                        coste = AccionesTacticas.CosteDescarga;
                    }
                    else coste = Math.Max(0, M.Int(M.Get(a, "costePagado")));
                    if (coste > energia) continue;
                    energia -= coste; costeAcciones[uid] = costeAcciones.GetValueOrDefault(uid) + coste;
                    if (AccionesTacticas.EsDescarga(a)) { descUsada = true; st["descargaUsada"] = true; }
                    acciones.Add(a);
                    j.Met.Acciones++;
                    if (AccionesTacticas.Catalogo.TryGetValue(M.Int(M.Get(a, "habilidadId")), out var h) && h.Efecto == AccionesTacticas.Efecto.Disparo) j.Met.Disparos++;
                }
                st["energies"] = Math.Max(0, energia);
                if (j.Estrategia is EstrategaSoftmaxStrategy s) st["modoBot"] = s.UltimoModo;
                else if (j.Estrategia is MaestroStrategy ms) st["modoBot"] = ms.UltimoModo;
                else if (j.Estrategia is EstrategiaHumanoSimulado hs) st["modoBot"] = hs.UltimoModo;

                planes.Add(new SimuladorTurno.Plan(uid, celdas, acciones));
                movimientos.Add(new Dictionary<string, object?>
                {
                    ["uid"] = uid, ["zona"] = ctx.Zona,
                    ["celdas"] = celdas.ToDictionary(kv => kv.Key, kv => (object?)kv.Value.Cast<object?>().ToList()),
                });
            }

            // ── 2. Resolución (motor del servidor) ──
            var previo = tablero;
            var res = SimuladorTurno.Simular(
                tablero, obeliscos, turno, planes, efectos, eliminados,
                null, mapa.Terreno, descargas, mapa.Celdas.ToHashSet());
            var reso = res.Combates!;
            Trampas.ActualizarTurnosEnCelda(res.Tablero, previo);
            tablero = res.Tablero;
            efectos = res.EfectosFinales ?? new EfectosCelda();
            descargas = res.DescargasTurno ?? descargas;

            // ── 3. Farmeo ──
            FarmeoResultado? farmeo = null;
            if (mapa.ConFarmeo)
            {
                int numRayos = jugadores.Count >= 7 ? 3 : (jugadores.Count >= 4 ? 2 : 1);
                farmeo = Farmeo.Calcular(tablero, obeliscos, mapa.Continentes, mapa.IslaCentral.ToList(),
                    rayos, mapa.Celdas, numRayos, rng, ruinas);
                rayos = farmeo.NuevosRayos;
            }

            // ── 4. Stats: combate, conquistas, disparos, farmeo, suerte del perdedor ──
            var perdedores = reso.ObeliscosConquistados.Select(c => c.PerdedorUid).ToHashSet();
            var pcAcciones = Combate.PcPorAcciones(res.LogAcciones ?? new List<Dictionary<string, object?>>(), null);
            var farmeoLog = new List<object?>();
            foreach (var j in activos)
            {
                string uid = j.Def.Uid; var st = stats[uid];
                int e = M.Int(st["energies"]);
                int ganado = reso.EnergiesPorJugador.GetValueOrDefault(uid) + (farmeo?.EnergiesPorJugador.GetValueOrDefault(uid) ?? 0);
                e += ganado;
                st["pc"] = M.Int(st["pc"]) + reso.PcPorJugador.GetValueOrDefault(uid)
                           + pcAcciones.GetValueOrDefault(uid);
                j.Met.PcDisparos += pcAcciones.GetValueOrDefault(uid);
                if (!perdedores.Contains(uid) && ganado == 0)
                {
                    e += 3;
                    farmeoLog.Add(new Dictionary<string, object?> { ["uid"] = uid, ["zona"] = "", ["totalEnergies"] = 3, ["detalle"] = new Dictionary<string, object?> { ["suerteDelPerdedor"] = 3 } });
                }
                st["energies"] = e;
            }
            if (farmeo != null) foreach (var f in farmeo.FarmeoLog) farmeoLog.Add(f);

            foreach (var oc in reso.ObeliscosConquistados)
            {
                eliminados.Add(oc.PerdedorUid);
                obeliscos.Remove(oc.PerdedorUid);
                ruinas.Add(oc.Coord);
                if (porUid.TryGetValue(oc.PerdedorUid, out var jp)) { jp.Met.Eliminado = turno; jp.Met.Motivo = $"conquista por {oc.ConquistadorUid} en {oc.Coord}"; }
                if (porUid.TryGetValue(oc.ConquistadorUid, out var jc)) jc.Met.Conquistas++;
            }

            // ── 5. Reparto de carta (con repetición desde el pool) ──
            var repartoLog = new List<object?>();
            foreach (var j in activos)
            {
                string uid = j.Def.Uid;
                if (eliminados.Contains(uid)) continue;
                var st = stats[uid];
                st["robosComprados"] = 0;
                var pool = M.List(st["mazoPool"]).Select(M.Str).Where(s => s != "").ToList();
                if (pool.Count == 0) continue;
                var mano = M.List(st["mano"]).ToList();
                var id = pool[rng.Next(pool.Count)];
                mano.Add(id); st["mano"] = mano;
                repartoLog.Add(new Dictionary<string, object?> { ["uid"] = uid, ["cartaId"] = id });
            }

            // ── 6. Historial del turno (mismo shape que el servidor) ──
            var accionesLog = res.LogAcciones ?? new List<Dictionary<string, object?>>();
            var entrada = new Dictionary<string, object?>
            {
                ["turno"] = turno,
                ["combateLog"] = reso.Resultados.Select(r => (object?)r.ToLogMap()).ToList(),
                ["conquistasLog"] = reso.ObeliscosConquistados.Select(c => (object?)c.ToLogMap()).ToList(),
                ["movimientosLog"] = movimientos,
                ["farmeoLog"] = farmeoLog,
                ["repartoLog"] = repartoLog,
                ["accionesLog"] = accionesLog.Cast<object?>().ToList(),
                ["rayoCoords"] = rayos.Select(r => M.Get(r, "coord")).ToList(),
            };
            historial.Add(entrada);
            if (historial.Count > 5) historial.RemoveAt(0);

            // ── 7. Métricas del turno ──
            Medir(jugadores, activos, stats, tablero, obeliscos, eliminados, reso, accionesLog, farmeoLog, turno);

            // ── 8. Fin de partida ──
            var vivos = jugadores.Where(j => !eliminados.Contains(j.Def.Uid)).ToList();
            bool fin = vivos.Count <= 1;
            if (fin && vivos.Count == 1) ganador = vivos[0].Def.Uid;

            if (guardar)
            {
                // Copia PROFUNDA (vía JSON): las stats y el tablero vivos se mutan
                // en los turnos siguientes y la foto debe quedar congelada.
                var fotoViva = EstadoActual(turno + 1, fin ? "finalizada" : "en_curso");
                var foto = (Dictionary<string, object?>)Plain(JsonSerializer.SerializeToElement(fotoViva))!;
                estadosGuardados.Add(foto);
                turnosEstudio.Add(new Dictionary<string, object?>
                {
                    ["id"] = turno.ToString("D4"),
                    ["datos"] = new Dictionary<string, object?>
                    {
                        ["turno"] = turno,
                        ["registradoEn"] = DateTime.UtcNow.ToString("o"),
                        ["tablero"] = foto["tablero"], ["efectosCelda"] = foto["efectosCelda"],
                        ["statsPartida"] = foto["statsPartida"], ["obeliscos"] = foto["obeliscos"],
                        ["jugadores"] = foto["jugadores"], ["jugadoresEliminados"] = foto["jugadoresEliminados"],
                        ["botsUids"] = foto["botsUids"], ["estado"] = foto["estado"], ["ganadorUid"] = foto["ganadorUid"],
                        ["logsTurno"] = entrada,
                    },
                });
            }
            if (fin) break;
        }
        int turnosJugados = Math.Min(turno, maxTurnos);

        // ── Resultado ──
        var r = new ResultadoPartida();
        r.Fila["ganador"] = ganador;
        r.Fila["turnos"] = turnosJugados;
        r.Fila["terminada"] = ganador != null;
        var filasJ = new Dictionary<string, object?>();
        foreach (var j in jugadores)
        {
            var m = j.Met; int tv = Math.Max(1, m.TurnosVivo);
            filasJ[j.Def.Uid] = new Dictionary<string, object?>
            {
                ["alias"] = j.Def.Alias, ["tipo"] = j.Def.Etiqueta, ["ejercito"] = j.Def.EjercitoId,
                ["gana"] = j.Def.Uid == ganador, ["turnosVivo"] = m.TurnosVivo, ["eliminadoT"] = m.Eliminado, ["motivo"] = m.Motivo,
                ["farmT"] = Math.Round(m.Farm / tv, 1), ["farmRayoT"] = Math.Round(m.FarmRayo / tv, 1), ["farmIslaT"] = Math.Round(m.FarmIsla / tv, 1), ["farmContT"] = Math.Round(m.FarmCont / tv, 1),
                ["farmT1_6"] = m.FarmT6, ["E_T6"] = m.ET6, ["unidades_T6"] = m.NT6,
                ["E"] = Math.Round(m.SumE / tv, 1), ["mano"] = Math.Round(m.SumMano / tv, 1), ["unidades"] = Math.Round(m.SumN / tv, 1),
                ["poder"] = Math.Round(m.SumPoder / tv, 1), ["poderMax"] = m.PoderMax, ["poderPorUnidad"] = Math.Round(m.SumPoder / Math.Max(1, m.SumN), 1),
                ["celdas"] = Math.Round(m.SumCeldas / tv, 1), ["shareStack"] = Math.Round(m.SumShare / Math.Max(1, m.TurnosConUnidades), 2),
                ["cuartelN"] = Math.Round(m.SumCuN / tv, 1), ["cuartelPoder"] = Math.Round(m.SumCuP / tv, 1),
                ["evoMax"] = m.EvoMax, ["generales"] = m.Gens, ["robos"] = m.Robos, ["sacrificios"] = m.Sacrificios,
                ["combates"] = m.Combates, ["combatesGanados"] = m.CombGanados, ["cartasPerdidas"] = m.CartasPerdidas,
                ["conquistas"] = m.Conquistas, ["acciones"] = m.Acciones, ["disparos"] = m.Disparos, ["kills"] = m.Kills, ["muertasPorHabilidad"] = m.MuertasHab,
                ["turnosAdyacenteCuartel"] = m.Adyacente,
                ["pc"] = M.Int(M.Get(stats[j.Def.Uid], "pc")), ["pcDisparos"] = m.PcDisparos,
            };
        }
        r.Fila["jugadores"] = filasJ;

        if (guardar)
        {
            r.EstadosPorTurno = estadosGuardados;
            r.Estudio = new Dictionary<string, object?>
            {
                ["lobbyId"] = $"arena_{semilla}",
                ["meta"] = new Dictionary<string, object?>
                {
                    ["lobbyId"] = $"arena_{semilla}", ["mapaId"] = mapa.Id == "" ? null : mapa.Id,
                    ["botsUids"] = botsUids, ["estado"] = ganador != null ? "finalizada" : "en_curso",
                    ["ganadorUid"] = ganador, ["ultimoTurno"] = turnosJugados,
                    ["jugadores"] = jugadores.Select(j => (object?)new Dictionary<string, object?>
                    { ["uid"] = j.Def.Uid, ["alias"] = j.Def.Alias, ["ejercitoId"] = j.Def.EjercitoId, ["listo"] = true, ["tipo"] = j.Def.Etiqueta }).ToList(),
                },
                ["turnos"] = turnosEstudio,
            };
        }
        return r;
    }

    /// JsonElement → objetos planos (Dictionary / List / string / long / double / bool / null).
    public static object? Plain(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Object => e.EnumerateObject().ToDictionary(p => p.Name, p => Plain(p.Value)),
        JsonValueKind.Array => e.EnumerateArray().Select(Plain).ToList(),
        JsonValueKind.String => e.GetString(),
        JsonValueKind.Number => e.TryGetInt64(out var l) ? l : e.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null,
    };

    // ── Contexto del jugador (equivalente a WarZeroBot.JugarTurnoAsync) ──
    private static BotContext ConstruirContexto(
        Dictionary<string, object?> estado, Jugador j, Mapa mapa,
        Dictionary<string, Dictionary<string, object?>> catalogo, int turno, string cuartel)
    {
        string uid = j.Def.Uid;
        var st = M.Map(M.Get(M.Map(M.Get(estado, "statsPartida")), uid));
        var mano = M.List(M.Get(st, "mano")).Select(M.Str).Where(s => s != "").ToList();
        var catalogoMano = new Dictionary<string, Dictionary<string, object?>>();
        foreach (var id in mano.Distinct())
            if (catalogo.TryGetValue(id, out var c)) catalogoMano[id] = new Dictionary<string, object?>(c);

        var idsEvo = new HashSet<string>();
        foreach (var celda in M.Map(M.Get(estado, "tablero")).Values)
            foreach (var c in M.List(celda))
            {
                var cm = M.Map(c);
                var ie = M.Str(M.Get(cm, "IdEvolucion", "idEvolucion"));
                if (ie != "" && M.Int(M.Get(cm, "Evolucion", "evolucion")) > 0) idsEvo.Add(ie);
            }
        var evoluciones = new Dictionary<string, Dictionary<string, object?>>();
        foreach (var id in idsEvo) if (catalogo.TryGetValue(id, out var c)) evoluciones[id] = new Dictionary<string, object?>(c);

        var compradas = M.List(M.Get(st, "especialesCompradas")).Select(M.Str).ToHashSet();
        // Mismo cuartel que en partida real: máx. 3 generales.
        var generales = CuartelGenerales.Disponibles(
            catalogo.Select(kv => (kv.Key, kv.Value)), j.Def.EjercitoId, compradas);

        var rayosSet = new HashSet<string>(); var rayosTurnos = new Dictionary<string, int>();
        foreach (var r in M.List(M.Get(estado, "rayos")))
        {
            var rm = M.Map(r); var c = M.Str(M.Get(rm, "coord"));
            if (c == "") continue;
            rayosSet.Add(c); int t = M.Int(M.Get(rm, "turnosRestantes")); rayosTurnos[c] = t > 0 ? t : 1;
        }

        return new BotContext
        {
            Estado = estado, BotUid = uid, Turno = turno, Cuartel = cuartel,
            Energia = M.Int(M.Get(st, "energies")), Mano = mano, CatalogoMano = catalogoMano,
            Zona = Zona(estado, uid, cuartel, mapa.Filas, mapa.Columnas),
            Terreno = mapa.Terreno, Filas = mapa.Filas, Columnas = mapa.Columnas,
            IslaCentral = mapa.IslaCentral, Continentes = mapa.Continentes,
            Rayos = rayosSet, RayosTurnos = rayosTurnos,
            EjercitoId = j.Def.EjercitoId, Evoluciones = evoluciones, GeneralesDisponibles = generales,
        };
    }

    private static string Zona(Dictionary<string, object?> estado, string uid, string cuartel, int filas, int columnas)
    {
        foreach (var raw in M.Map(M.Get(estado, "tablero")).Values)
            foreach (var cRaw in M.List(raw))
            {
                var carta = M.Map(cRaw);
                if (M.Str(M.Get(carta, "ownerUid")) == uid) { var z = M.Str(M.Get(carta, "ownerZone")); if (z != "") return z; }
            }
        var pos = ReglasEntrada.Parse(cuartel);
        if (pos == null) return "";
        var (ri, ci) = pos.Value;
        bool n = ri <= 2, s = ri >= filas - 3, w = ci <= 2, e = ci >= columnas - 3;
        if (n && e) return "ne"; if (n && w) return "nw"; if (s && e) return "se"; if (s && w) return "sw";
        if (n) return "north"; if (s) return "south"; if (w) return "west"; if (e) return "east";
        return "";
    }

    /// Cierre seguro: las cartas propias se quedan donde están.
    private static Tablero Arrastrar(Tablero tablero, string uid)
    {
        var t = new Tablero();
        foreach (var (coord, cartas) in tablero)
        {
            var mias = cartas.Where(c => M.Str(M.Get(c, "ownerUid")) == uid).Select(c => new Dictionary<string, object?>(c)).ToList();
            if (mias.Count > 0) t[coord] = mias;
        }
        return t;
    }

    /// Reglas que impone el servidor: solo cartas propias, y las ESTÁTICAS
    /// terminan exactamente donde estaban.
    private static Tablero Sanear(Tablero plan, Tablero previo, string uid)
    {
        var estaticas = new Dictionary<string, string>();   // instanceId -> coord
        foreach (var (coord, cartas) in previo)
            foreach (var c in cartas)
                if (M.Str(M.Get(c, "ownerUid")) == uid && ReglasEntrada.EsEstatica(c))
                {
                    var iid = M.Str(M.Get(c, "instanceId"));
                    if (iid != "") estaticas[iid] = coord;
                }
        var t = new Tablero();
        void Add(string coord, Dictionary<string, object?> c)
        {
            if (!t.TryGetValue(coord, out var l)) { l = new(); t[coord] = l; }
            l.Add(c);
        }
        foreach (var (coord, cartas) in plan)
            foreach (var c in cartas)
            {
                if (M.Str(M.Get(c, "ownerUid")) != uid) continue;
                var iid = M.Str(M.Get(c, "instanceId"));
                if (iid != "" && estaticas.TryGetValue(iid, out var orig) && orig != coord) Add(orig, c);
                else Add(coord, c);
            }
        return t;
    }

    private static void Medir(
        List<Jugador> jugadores, List<Jugador> activos, Dictionary<string, Dictionary<string, object?>> stats,
        Tablero tablero, Dictionary<string, string> obeliscos, HashSet<string> eliminados,
        ResolucionCombates reso, List<Dictionary<string, object?>> accionesLog, List<object?> farmeoLog, int turno)
    {
        var porUid = jugadores.ToDictionary(j => j.Def.Uid);
        foreach (var j in activos)
        {
            string uid = j.Def.Uid; var m = j.Met; var st = stats[uid];
            bool vivoAlFinal = !eliminados.Contains(uid) || m.Eliminado == turno;
            if (!vivoAlFinal) continue;
            m.TurnosVivo++;
            m.SumE += M.Int(st["energies"]); m.SumMano += M.List(st["mano"]).Count;
            var mias = tablero.SelectMany(kv => kv.Value.Where(c => M.Str(M.Get(c, "ownerUid")) == uid).Select(c => (kv.Key, c))).ToList();
            int n = mias.Count, poder = mias.Sum(x => ReglasEntrada.Fuerza(x.c) + ReglasEntrada.Defensa(x.c));
            m.SumN += n; m.SumPoder += poder; m.PoderMax = Math.Max(m.PoderMax, poder);
            var celdas = mias.GroupBy(x => x.Key).ToList();
            m.SumCeldas += celdas.Count;
            if (n > 0)
            {
                m.TurnosConUnidades++;
                int maxStack = celdas.Max(g => g.Sum(x => ReglasEntrada.Fuerza(x.c) + ReglasEntrada.Defensa(x.c)));
                m.SumShare += maxStack / (double)Math.Max(1, poder);
            }
            m.EvoMax = Math.Max(m.EvoMax, mias.Count(x => M.Get(x.c, "PorDefecto") is bool b && !b && ReglasEntrada.Condicion(x.c) is not (5 or 3)));
            if (obeliscos.TryGetValue(uid, out var cu))
            {
                var enCasa = mias.Where(x => x.Key == cu).ToList();
                m.SumCuN += enCasa.Count; m.SumCuP += enCasa.Sum(x => ReglasEntrada.Fuerza(x.c) + ReglasEntrada.Defensa(x.c));
            }
            var cuartelesEnemigos = obeliscos.Where(kv => kv.Key != uid).Select(kv => kv.Value).ToList();
            if (celdas.Any(g => cuartelesEnemigos.Any(q => ReglasEntrada.Manhattan(g.Key, q) == 1))) m.Adyacente++;
            if (turno == 6) { m.ET6 = M.Int(st["energies"]); m.NT6 = n; }
        }
        foreach (var fRaw in farmeoLog)
        {
            var f = M.Map(fRaw); var uid = M.Str(M.Get(f, "uid"));
            if (!porUid.TryGetValue(uid, out var j)) continue;
            int tot = M.Int(M.Get(f, "totalEnergies")); j.Met.Farm += tot;
            if (turno <= 6) j.Met.FarmT6 += tot;
            var det = M.Map(M.Get(f, "detalle"));
            j.Met.FarmRayo += M.Int(M.Get(det, "rayo")); j.Met.FarmIsla += M.Int(M.Get(det, "islaCentral")); j.Met.FarmCont += M.Int(M.Get(det, "continenteEnemigo"));
        }
        foreach (var rc in reso.Resultados)
        {
            if (rc.EsConquistaObelisco) continue;
            var log = rc.ToLogMap();
            var ganadorUid = M.Str(M.Get(log, "ganadorUid"));
            foreach (var dtRaw in M.List(M.Get(log, "detalle")))
            {
                var dt = M.Map(dtRaw); var o = M.Str(M.Get(dt, "ownerUid"));
                if (!porUid.TryGetValue(o, out var j)) continue;
                j.Met.Combates++;
                if (o == ganadorUid) j.Met.CombGanados++;
                else j.Met.CartasPerdidas += M.Int(M.Get(dt, "numCartas"));
            }
        }
        foreach (var a in accionesLog)
        {
            var uid = M.Str(M.Get(a, "uid"));
            var destruidas = M.List(M.Get(a, "cartasDestruidas"));
            if (porUid.TryGetValue(uid, out var j)) j.Met.Kills += destruidas.Count;
            foreach (var cd in destruidas)
            {
                var o = M.Str(M.Get(M.Map(cd), "ownerUid"));
                if (porUid.TryGetValue(o, out var jv)) jv.Met.MuertasHab++;
            }
        }
    }

    // ── Resumen agregado por jugador ──
    public static Dictionary<string, object?> Resumir(List<Dictionary<string, object?>> partidas, List<Def> defs)
    {
        var res = new Dictionary<string, object?>();
        int total = partidas.Count;
        res["partidas"] = total;
        res["terminadas"] = partidas.Count(p => p["terminada"] is true);
        res["turnosMedia"] = total == 0 ? 0 : Math.Round(partidas.Average(p => Convert.ToDouble(p["turnos"])), 1);
        var porJugador = new Dictionary<string, object?>();
        string[] medias = { "farmT", "farmRayoT", "farmIslaT", "farmContT", "farmT1_6", "E_T6", "unidades_T6", "E", "mano", "unidades", "poder", "poderMax", "poderPorUnidad", "celdas", "shareStack", "cuartelN", "cuartelPoder", "evoMax", "generales", "robos", "sacrificios", "combates", "combatesGanados", "cartasPerdidas", "conquistas", "acciones", "disparos", "kills", "muertasPorHabilidad", "turnosAdyacenteCuartel", "turnosVivo", "pc", "pcDisparos" };
        foreach (var d in defs)
        {
            var filas = partidas.Select(p => M.Map(M.Get(M.Map(p["jugadores"]), d.Uid))).Where(f => f.Count > 0).ToList();
            if (filas.Count == 0) continue;
            var fila = new Dictionary<string, object?>
            {
                ["alias"] = d.Alias, ["tipo"] = d.Etiqueta, ["ejercito"] = d.EjercitoId,
                ["victorias"] = filas.Count(f => f["gana"] is true),
                ["pctVictorias"] = Math.Round(100.0 * filas.Count(f => f["gana"] is true) / filas.Count, 1),
                ["eliminadoTmedia"] = Math.Round(filas.Where(f => M.Int(f["eliminadoT"]) > 0).Select(f => (double)M.Int(f["eliminadoT"])).DefaultIfEmpty(0).Average(), 1),
            };
            foreach (var k in medias)
                fila[k] = Math.Round(filas.Select(f => Convert.ToDouble(f[k] ?? 0)).Average(), 2);
            porJugador[d.Uid] = fila;
        }
        res["porJugador"] = porJugador;
        return res;
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// ENDPOINTS
// ─────────────────────────────────────────────────────────────────────────────
public static class WarZeroArenaExtensions
{
    public static WebApplication MapWarZeroArenaEndpoints(this WebApplication app)
    {
        static IResult? Autorizar(HttpContext ctx, IConfiguration cfg)
        {
            var secreto = cfg["WARZERO_ARENA_SECRET"];
            if (string.IsNullOrWhiteSpace(secreto)) secreto = cfg["WARZERO_ADMIN_SECRET"];
            if (string.IsNullOrWhiteSpace(secreto)) return Results.NotFound();
            var recibido = ctx.Request.Headers["X-Admin-Secret"].ToString();
            var a = System.Text.Encoding.UTF8.GetBytes(recibido);
            var b = System.Text.Encoding.UTF8.GetBytes(secreto);
            bool ok = a.Length == b.Length && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(a, b);
            return ok ? null : Results.Unauthorized();
        }

        static object Vista(ArenaJob j, bool conPartidas) => new
        {
            jobId = j.Id,
            estado = j.Estado,
            progreso = new { completadas = j.Completadas, total = j.Total },
            creadoUtc = j.CreadoUtc,
            terminadoUtc = j.TerminadoUtc,
            error = j.Error,
            request = j.Request,
            resumen = j.Resumen,
            partidasGuardadas = j.PartidasCompletas.Count,
            partidas = conPartidas ? j.Partidas : null,
        };

        app.MapPost("/warzero/arena/partidas", (HttpContext ctx, IConfiguration cfg, ArenaService arena, ArenaRequest req) =>
        {
            var auth = Autorizar(ctx, cfg); if (auth != null) return auth;
            if (req.Jugadores == null || req.Jugadores.Count < 2 || req.Jugadores.Count > 8)
                return Results.BadRequest(new { error = "Hacen falta entre 2 y 8 jugadores." });
            if (req.Jugadores.Select(j => j.Uid).Distinct().Count() != req.Jugadores.Count)
                return Results.BadRequest(new { error = "Los uid de los jugadores deben ser distintos." });
            var job = arena.Lanzar(req);
            return Results.Accepted($"/warzero/arena/partidas/{job.Id}", Vista(job, false));
        });

        app.MapGet("/warzero/arena/partidas", (HttpContext ctx, IConfiguration cfg, ArenaService arena) =>
        {
            var auth = Autorizar(ctx, cfg); if (auth != null) return auth;
            return Results.Ok(arena.Jobs.Select(j => Vista(j, false)).ToList());
        });

        app.MapGet("/warzero/arena/partidas/{id}", (HttpContext ctx, IConfiguration cfg, ArenaService arena, string id, bool? filas) =>
        {
            var auth = Autorizar(ctx, cfg); if (auth != null) return auth;
            var job = arena.Get(id);
            return job == null ? Results.NotFound() : Results.Ok(Vista(job, filas ?? true));
        });

        app.MapGet("/warzero/arena/partidas/{id}/partida/{n:int}", (HttpContext ctx, IConfiguration cfg, ArenaService arena, string id, int n) =>
        {
            var auth = Autorizar(ctx, cfg); if (auth != null) return auth;
            var job = arena.Get(id);
            if (job == null) return Results.NotFound();
            if (n < 1 || n > job.PartidasCompletas.Count)
                return Results.NotFound(new { error = $"Solo hay {job.PartidasCompletas.Count} partidas completas guardadas." });
            return Results.Ok(job.PartidasCompletas[n - 1]);
        });

        app.MapDelete("/warzero/arena/partidas/{id}", (HttpContext ctx, IConfiguration cfg, ArenaService arena, string id) =>
        {
            var auth = Autorizar(ctx, cfg); if (auth != null) return auth;
            return arena.Cancelar(id) ? Results.Ok(new { ok = true }) : Results.NotFound();
        });

        return app;
    }
}
