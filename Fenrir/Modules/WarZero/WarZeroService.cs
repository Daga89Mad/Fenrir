using System.Text.Json;
using Google.Cloud.Firestore;

// Alias de tipo (ámbito de fichero): mismos que en WarZeroLogic.cs. Los alias
// `using` no se heredan entre archivos, por eso hay que repetirlos aquí.
using Tablero = System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<System.Collections.Generic.Dictionary<string, object?>>>;
using EfectosCelda = System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<System.Collections.Generic.Dictionary<string, object?>>>;

// ─────────────────────────────────────────────────────────────────────────────
// WarZeroService.cs
//
// Orquesta el cierre de turno contra Firestore. Toda la operación (registrar el
// cierre de un jugador y, si cerraron todos, resolver el turno) ocurre dentro de
// UNA transacción, lo que elimina la carrera de "quién resuelve" que había en el
// cliente.
//
// Documento Partidas/{lobbyId}:
//   turnoActual, cerradoPor[], movimientosTurno{uid}, tablero{}, statsPartida{},
//   obeliscos{uid->coord}, jugadores[], jugadoresEliminados[], efectosCelda{},
//   rayo{}, mapaId, historialCombates[], estado, ganadorUid
// ─────────────────────────────────────────────────────────────────────────────

public class WarZeroService
{
    private readonly WarZeroFirestore _fs;

    public WarZeroService(WarZeroFirestore fs) => _fs = fs;

    public GameStatus GetStatus() => new("EU-1", 42);

    public async Task<CerrarTurnoResponse> CerrarTurnoAsync(CerrarTurnoRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.LobbyId) || string.IsNullOrWhiteSpace(req.Uid))
            return new CerrarTurnoResponse { Mensaje = "lobbyId y uid son obligatorios" };

        // Convertir el JSON entrante a CLR (mismo formato que en Dart).
        var celdasClr = req.Celdas.ValueKind == JsonValueKind.Object
            ? M.Map(M.FromJson(req.Celdas))
            : new Dictionary<string, object?>();
        var accionesIncoming = req.Acciones.ValueKind == JsonValueKind.Array
            ? M.List(M.FromJson(req.Acciones)).Select(M.Map).ToList()
            : new List<Dictionary<string, object?>>();

        var db = _fs.Db;
        var lobbyRef = db.Collection("Partidas").Document(req.LobbyId);

        var resp = await db.RunTransactionAsync(async tx =>
        {
            var snap = await tx.GetSnapshotAsync(lobbyRef);
            if (!snap.Exists)
                return new CerrarTurnoResponse { Mensaje = "La partida no existe" };

            var data = M.Map(M.FromFs(snap.ToDictionary()));
            var turnoDb = M.Int(M.Get(data, "turnoActual"));

            // Turno desincronizado: otro proceso ya avanzó. No escribimos.
            if (turnoDb != req.Turno)
                return new CerrarTurnoResponse
                {
                    Resuelto = false,
                    TurnoActual = turnoDb,
                    Mensaje = "El turno ya había avanzado",
                };

            // ── Movimiento del jugador que cierra ─────────────────────────────
            var movData = new Dictionary<string, object?>
            {
                ["uid"] = req.Uid,
                ["turno"] = req.Turno,
                ["celdas"] = celdasClr,
                ["timestamp"] = Timestamp.FromDateTime(DateTime.UtcNow),
                ["acciones"] = accionesIncoming.Cast<object?>().ToList(),
            };

            // movimientosTurno con este jugador ya incluido (para mergear / contar).
            var movTurno = M.Map(M.Get(data, "movimientosTurno"));
            movTurno[req.Uid] = movData;

            // cerradoPor con este jugador.
            var cerrado = M.List(M.Get(data, "cerradoPor")).Select(M.Str).Where(s => s != "").ToHashSet();
            cerrado.Add(req.Uid);

            // Jugadores activos.
            var eliminados = M.List(M.Get(data, "jugadoresEliminados")).Select(M.Str).ToHashSet();
            var jugadores = M.List(M.Get(data, "jugadores"))
                .Select(j => M.Str(M.Get(M.Map(j), "uid")))
                .Where(u => u != "").ToList();
            var activos = jugadores.Where(u => !eliminados.Contains(u)).ToList();

            var todosCerraron = activos.Count > 0 && activos.All(u => cerrado.Contains(u));

            // ── Caso 1: aún faltan jugadores → solo registrar el cierre ───────
            if (!todosCerraron)
            {
                tx.Update(lobbyRef, new Dictionary<FieldPath, object>
                {
                    [new FieldPath("movimientosTurno", req.Uid)] = movData,
                    [new FieldPath("cerradoPor")] = FieldValue.ArrayUnion(req.Uid),
                });

                return new CerrarTurnoResponse
                {
                    Resuelto = false,
                    TurnoActual = turnoDb,
                    CerradoPor = cerrado.Count,
                    JugadoresActivos = activos.Count,
                    Faltan = Math.Max(0, activos.Count - cerrado.Count),
                    Mensaje = "Turno cerrado. Esperando a los demás.",
                };
            }

            // ── Caso 2: cerraron todos → RESOLVER el turno ────────────────────
            // Etiqueta de fase: si algo lanza, el catch la añade al mensaje para
            // saber exactamente en qué paso de la resolución ocurrió.
            var fase = "obeliscos";
            try
            {
                var obeliscos = M.Map(M.Get(data, "obeliscos"))
                    .ToDictionary(k => k.Key, v => M.Str(v.Value));

                // Tablero fusionado a partir de los movimientos de ESTE turno.
                fase = "merge-tablero";
                var merged = new Dictionary<string, List<Dictionary<string, object?>>>();
                var acciones = new List<Dictionary<string, object?>>();
                foreach (var kv in movTurno)
                {
                    var mov = M.Map(kv.Value);
                    if (M.Int(M.Get(mov, "turno")) != req.Turno) continue;
                    foreach (var ce in M.Map(M.Get(mov, "celdas")))
                    {
                        if (!merged.TryGetValue(ce.Key, out var lst)) { lst = new(); merged[ce.Key] = lst; }
                        foreach (var c in M.List(ce.Value)) lst.Add(M.Map(c));
                    }
                    acciones.AddRange(M.List(M.Get(mov, "acciones")).Select(M.Map));
                }

                // Efectos de celda previos.
                fase = "efectos-previos";
                var efectosPrevios = ParseEfectosCelda(M.Get(data, "efectosCelda"));

                // 1. Acciones (tele → disparo → veneno).
                fase = "acciones";
                var acc = Habilidades.AplicarAcciones(merged, acciones, efectosPrevios, obeliscos);
                // 2. Combates.
                fase = "combate";
                var reso = Combate.Resolver(acc.Tablero, obeliscos);
                // 3. Tick de efectos.
                fase = "tick-efectos";
                var tick = Habilidades.TickEfectos(reso.Tablero, acc.EfectosCelda);
                var tableroFinal = tick.Tablero;
                var efectosFinal = tick.EfectosCelda;

                // 4. Farmeo (solo si el mapa aporta continentes/isla central).
                fase = "farmeo";
                FarmeoResultado? farmeo = null;
                var mapaId = M.Str(M.Get(data, "mapaId"));
                if (mapaId != "")
                {
                    var mapaSnap = await tx.GetSnapshotAsync(db.Collection("Mapas").Document(mapaId));
                    if (mapaSnap.Exists)
                    {
                        var mapData = M.Map(M.FromFs(mapaSnap.ToDictionary()));
                        var continentes = M.Map(M.Get(mapData, "continentes"))
                            .ToDictionary(k => k.Key, v => M.List(v.Value).Select(M.Str).ToList());
                        var islaCentral = M.List(M.Get(mapData, "islaCentral")).Select(M.Str).ToList();
                        if (continentes.Count > 0 || islaCentral.Count > 0)
                        {
                            var rayoActual = snap.ContainsField("rayo") ? M.Map(M.Get(data, "rayo")) : null;
                            farmeo = Farmeo.Calcular(
                                tableroFinal, obeliscos, continentes, islaCentral,
                                rayoActual, Coords.AllCells(jugadores.Count), new Random());
                        }
                    }
                }

                // 5. Acumular stats.
                fase = "stats";
                var stats = new Dictionary<string, Dictionary<string, object?>>();
                foreach (var kv in M.Map(M.Get(data, "statsPartida")))
                {
                    var m = M.Map(kv.Value);
                    stats[kv.Key] = new Dictionary<string, object?>
                    {
                        ["energies"] = M.Int(M.Get(m, "energies")),
                        ["pc"] = M.Int(M.Get(m, "pc")),
                    };
                }
                void EnsureStat(string uid)
                {
                    if (!stats.ContainsKey(uid)) stats[uid] = new() { ["energies"] = 0, ["pc"] = 0 };
                }
                foreach (var kv in reso.EnergiesPorJugador)
                {
                    EnsureStat(kv.Key);
                    stats[kv.Key]["energies"] = M.Int(stats[kv.Key]["energies"]) + kv.Value;
                }
                foreach (var kv in reso.PcPorJugador)
                {
                    EnsureStat(kv.Key);
                    stats[kv.Key]["pc"] = M.Int(stats[kv.Key]["pc"]) + kv.Value;
                }
                if (farmeo != null)
                    foreach (var kv in farmeo.EnergiesPorJugador)
                    {
                        EnsureStat(kv.Key);
                        stats[kv.Key]["energies"] = M.Int(stats[kv.Key]["energies"]) + kv.Value;
                    }

                // 6. Logs + entrada de historial.
                fase = "logs-historial";
                var combateLog = reso.Resultados.Select(r => (object?)r.ToLogMap()).ToList();
                var conquistasLog = reso.ObeliscosConquistados.Select(c => (object?)c.ToLogMap()).ToList();
                var movimientosLog = BuildMovimientosLog(movTurno, req.Turno);

                var entradaHistorial = new Dictionary<string, object?>
                {
                    ["turno"] = req.Turno,
                    ["combateLog"] = combateLog,
                    ["conquistasLog"] = conquistasLog,
                    ["movimientosLog"] = movimientosLog,
                    ["farmeoLog"] = farmeo?.FarmeoLog.Cast<object?>().ToList() ?? new List<object?>(),
                    ["accionesLog"] = acc.Log.Cast<object?>().ToList(),
                    ["rayoCoord"] = farmeo?.NuevoRayo != null ? M.Get(farmeo.NuevoRayo, "coord") : null,
                    ["rayoTurnosRestantes"] = farmeo?.NuevoRayo != null ? M.Get(farmeo.NuevoRayo, "turnosRestantes") : null,
                };

                var historial = M.List(M.Get(data, "historialCombates")).ToList();
                historial.Add(entradaHistorial);
                if (historial.Count > 3) historial.RemoveRange(0, historial.Count - 3);

                // 7. Construir el update.
                fase = "build-update";
                var update = new Dictionary<string, object>
                {
                    ["turnoActual"] = req.Turno + 1,
                    ["cerradoPor"] = new List<object>(),
                    ["movimientosTurno"] = new Dictionary<string, object>(),
                    ["tablero"] = ToFsTablero(tableroFinal),
                    ["statsPartida"] = stats.ToDictionary(k => k.Key, v => (object)v.Value),
                    ["ultimoCombateLog"] = combateLog,
                    ["ultimoFarmeoLog"] = farmeo?.FarmeoLog.Cast<object?>().ToList() ?? new List<object?>(),
                    ["ultimoAccionesLog"] = acc.Log.Cast<object?>().ToList(),
                    ["ultimosMovimientos"] = movimientosLog,
                    ["historialCombates"] = historial,
                    ["efectosCelda"] = efectosFinal.Count == 0 ? FieldValue.Delete : ToFsEfectos(efectosFinal),
                };
                if (farmeo != null)
                    update["rayo"] = farmeo.NuevoRayo != null ? (object)farmeo.NuevoRayo : FieldValue.Delete;

                // 8. Eliminaciones / fin de partida.
                var nuevosEliminados = reso.ObeliscosConquistados.Select(c => c.PerdedorUid).Distinct().ToList();
                string? ganadorUid = null;
                var finalizada = false;
                if (nuevosEliminados.Count > 0)
                {
                    update["jugadoresEliminados"] = FieldValue.ArrayUnion(nuevosEliminados.Cast<object>().ToArray());
                    var totalElim = new HashSet<string>(eliminados);
                    totalElim.UnionWith(nuevosEliminados);
                    var siguenActivos = jugadores.Where(u => !totalElim.Contains(u)).ToList();
                    if (siguenActivos.Count <= 1)
                    {
                        finalizada = true;
                        update["estado"] = "finalizada";
                        if (siguenActivos.Count > 0)
                        {
                            ganadorUid = siguenActivos[0];
                            update["ganadorUid"] = ganadorUid;
                        }
                    }
                }

                fase = "write";
                tx.Update(lobbyRef, update);

                var energiesTotales = new Dictionary<string, int>(reso.EnergiesPorJugador);
                if (farmeo != null)
                    foreach (var kv in farmeo.EnergiesPorJugador)
                        energiesTotales[kv.Key] = energiesTotales.GetValueOrDefault(kv.Key) + kv.Value;

                return new CerrarTurnoResponse
                {
                    Resuelto = true,
                    TurnoActual = req.Turno + 1,
                    CerradoPor = activos.Count,
                    JugadoresActivos = activos.Count,
                    Faltan = 0,
                    Finalizada = finalizada,
                    GanadorUid = ganadorUid,
                    Conquistas = reso.ObeliscosConquistados.Select(c => c.ToLogMap()).ToList(),
                    EnergiesPorJugador = energiesTotales,
                    Mensaje = "Turno resuelto.",
                };
            }
            catch (Exception ex)
            {
                // Re-lanza añadiendo la fase para diagnóstico preciso.
                throw new InvalidOperationException(
                    $"[fase={fase}] {ex.GetType().Name}: {ex.Message}", ex);
            }
        });

        // Tras commit, adjunta el estado completo de la partida para que el
        // cliente avance SIN leer Firestore (camino HTTP puro).
        try
        {
            resp.Estado = await LeerEstadoAsync(req.LobbyId);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("[WarZero] LeerEstado tras cerrar falló: " + ex);
        }
        return resp;
    }

    /// Lee el doc de la partida y lo devuelve serializado JSON-safe (mismo shape
    /// que Firestore). Usado por el cierre y por GET /warzero/estado.
    public async Task<Dictionary<string, object?>?> LeerEstadoAsync(string lobbyId)
    {
        var snap = await _fs.Db.Collection("Partidas").Document(lobbyId)
            .GetSnapshotAsync();
        if (!snap.Exists) return null;
        var safe = M.ToJsonSafe(snap.ToDictionary());
        return safe as Dictionary<string, object?> ?? new Dictionary<string, object?>();
    }

    /// Esquinas candidatas para el cuartel/obelisco (igual que kObeliscoCoords).
    private static readonly string[] ObeliscoCoords = { "F1", "A1", "A10", "F10" };

    private const int EnergiasIniciales = 15;
    private const int TamanioManoInicial = 5;
    private const int TamanioMazoDefecto = 20;

    /// Entrada a la partida: inicializa de forma atómica las energías de inicio,
    /// el obelisco y la mano/mazo del jugador si aún no los tiene, y devuelve el
    /// estado completo.
    public async Task<EntrarResponse> EntrarAsync(EntrarRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.LobbyId) || string.IsNullOrWhiteSpace(req.Uid))
            return new EntrarResponse { Existe = false };

        var db = _fs.Db;
        var lobbyRef = db.Collection("Partidas").Document(req.LobbyId);

        // ── Pre-lectura (fuera de la transacción) para decidir si hay que ──────
        // repartir mano. El reparto lee colecciones (Mazos, Cartas) que no
        // conviene leer dentro de la transacción.
        var pre = await lobbyRef.GetSnapshotAsync();
        if (!pre.Exists) return new EntrarResponse { Existe = false };
        var preData = M.Map(M.FromFs(pre.ToDictionary()));

        var preStats = M.Map(M.Get(preData, "statsPartida"));
        var preMiStat = preStats.TryGetValue(req.Uid, out var ps) ? M.Map(ps) : null;
        var yaTieneMano = preMiStat != null && preMiStat.ContainsKey("mano");

        List<string>? manoIds = null;
        List<string>? mazoRestanteIds = null;
        if (!yaTieneMano)
        {
            try
            {
                var ejercitoId = EjercitoDeJugador(preData, req.Uid);
                var enTablero = CartasEnTableroDe(preData, req.Uid);
                var (mano, resto) =
                    await RepartirManoAsync(req.Uid, ejercitoId, enTablero);
                manoIds = mano;
                mazoRestanteIds = resto;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("[WarZero.Entrar] reparto mano falló: " + ex);
            }
        }

        var resp = await db.RunTransactionAsync(async tx =>
        {
            var snap = await tx.GetSnapshotAsync(lobbyRef);
            if (!snap.Exists) return new EntrarResponse { Existe = false };

            var data = M.Map(M.FromFs(snap.ToDictionary()));
            var updates = new Dictionary<FieldPath, object>();
            int? energiasAsignadas = null;
            string? obeliscoAsignado = null;

            var stats = M.Map(M.Get(data, "statsPartida"));
            var miStat = stats.TryGetValue(req.Uid, out var s) ? M.Map(s) : null;

            // 1) Energías de inicio.
            if (miStat == null || !miStat.ContainsKey("energies"))
            {
                updates[new FieldPath("statsPartida", req.Uid, "energies")] =
                    EnergiasIniciales;
                energiasAsignadas = EnergiasIniciales;
            }

            // 2) Obelisco.
            var obeliscos = M.Map(M.Get(data, "obeliscos"));
            if (!obeliscos.ContainsKey(req.Uid))
            {
                var ocupadas = obeliscos.Values.Select(M.Str).ToHashSet();
                var libres = ObeliscoCoords.Where(c => !ocupadas.Contains(c)).ToList();
                if (libres.Count > 0)
                {
                    var elegido = libres[new Random().Next(libres.Count)];
                    updates[new FieldPath("obeliscos", req.Uid)] = elegido;
                    obeliscoAsignado = elegido;
                }
            }

            // 3) Mano/mazo (solo si sigue sin tenerla y la pudimos repartir).
            var tieneMano = miStat != null && miStat.ContainsKey("mano");
            if (!tieneMano && manoIds != null && mazoRestanteIds != null)
            {
                updates[new FieldPath("statsPartida", req.Uid, "mano")] = manoIds;
                updates[new FieldPath("statsPartida", req.Uid, "mazoRestante")] =
                    mazoRestanteIds;
            }

            if (updates.Count > 0) tx.Update(lobbyRef, updates);

            return new EntrarResponse
            {
                Existe = true,
                TurnoActual = M.Int(M.Get(data, "turnoActual")),
                EnergiasAsignadas = energiasAsignadas,
                ObeliscoAsignado = obeliscoAsignado,
            };
        });

        // Tras commit, adjunta el estado completo (ya con la init aplicada).
        if (resp.Existe)
        {
            try { resp.Estado = await LeerEstadoAsync(req.LobbyId); }
            catch (Exception ex)
            {
                Console.Error.WriteLine("[WarZero] LeerEstado tras entrar falló: " + ex);
            }
            if (resp.Estado != null)
                resp.TurnoActual = resp.Estado.TryGetValue("turnoActual", out var t) && t is long l
                    ? (int)l : resp.TurnoActual;
        }
        return resp;
    }

    /// Ejército elegido por el jugador en la sala (de `jugadores[].ejercitoId`).
    private static int? EjercitoDeJugador(Dictionary<string, object?> data, string uid)
    {
        foreach (var j in M.List(M.Get(data, "jugadores")))
        {
            var jm = M.Map(j);
            if (M.Str(M.Get(jm, "uid")) == uid)
            {
                var e = M.Get(jm, "ejercitoId");
                return e == null ? (int?)null : M.Int(e);
            }
        }
        return null;
    }

    /// IDs de cartas que el jugador ya tiene colocadas en el tablero.
    private static HashSet<string> CartasEnTableroDe(
        Dictionary<string, object?> data, string uid)
    {
        var ids = new HashSet<string>();
        foreach (var celda in M.Map(M.Get(data, "tablero")).Values)
        {
            foreach (var c in M.List(celda))
            {
                var cm = M.Map(c);
                if (M.Str(M.Get(cm, "ownerUid")) == uid)
                {
                    var id = M.Str(M.Get(cm, "id"));
                    if (!string.IsNullOrEmpty(id)) ids.Add(id);
                }
            }
        }
        return ids;
    }

    /// Reparte la mano inicial y el mazo restante del jugador (listas de IDs de
    /// carta), portando la lógica de MazoService del cliente: usa el primer mazo
    /// guardado del jugador (expandido por Cantidad) o un mazo por defecto si no
    /// tiene; excluye evoluciones; filtra por ejército (preservando el mazo si el
    /// filtro lo vacía); excluye cartas ya colocadas en el tablero; y baraja.
    private async Task<(List<string> mano, List<string> resto)> RepartirManoAsync(
        string uid, int? ejercitoId, HashSet<string> cartasEnTablero)
    {
        var db = _fs.Db;
        var rnd = new Random();
        var poolIds = new List<string>();

        var mazosSnap = await db.Collection("Jugadores").Document(uid)
            .Collection("Mazos").Limit(1).GetSnapshotAsync();

        if (mazosSnap.Count > 0)
        {
            var cartasSnap = await mazosSnap.Documents[0].Reference
                .Collection("Cartas").GetSnapshotAsync();

            // (idCarta, cantidad) del mazo del jugador.
            var entradas = cartasSnap.Documents.Select(d =>
            {
                var cd = d.ToDictionary();
                var cant = M.Int(cd.GetValueOrDefault("Cantidad"));
                return (id: d.Id, cant: cant <= 0 ? 1 : cant);
            }).ToList();

            // Lee del catálogo Condicion + Ejercito de cada carta distinta.
            var metas = new Dictionary<string, (int cond, int ejer)>();
            foreach (var (id, _) in entradas)
            {
                if (metas.ContainsKey(id)) continue;
                var csnap = await db.Collection("Cartas").Document(id).GetSnapshotAsync();
                if (!csnap.Exists) { metas[id] = (-1, -1); continue; }
                var cd = csnap.ToDictionary();
                metas[id] = (M.Int(cd.GetValueOrDefault("Condicion")),
                             M.Int(cd.GetValueOrDefault("Ejercito")));
            }

            List<string> Construir(bool conFiltro)
            {
                var res = new List<string>();
                foreach (var (id, cant) in entradas)
                {
                    if (!metas.TryGetValue(id, out var m) || m.cond < 0) continue;
                    if (m.cond == 1) continue; // evolución: nunca se reparte
                    if (conFiltro && ejercitoId != null && m.ejer != ejercitoId) continue;
                    for (int q = 0; q < cant; q++) res.Add(id);
                }
                return res;
            }

            poolIds = Construir(true);
            if (poolIds.Count == 0) poolIds = Construir(false); // preservar mazo
        }
        else
        {
            // Mazo por defecto: catálogo completo, sin evoluciones, filtrado.
            var allSnap = await db.Collection("Cartas").GetSnapshotAsync();
            var basicas = allSnap.Documents
                .Select(d => (id: d.Id, cd: d.ToDictionary()))
                .Where(x => M.Int(x.cd.GetValueOrDefault("Condicion")) != 1)
                .ToList();
            var filtradas = ejercitoId != null
                ? basicas.Where(x => M.Int(x.cd.GetValueOrDefault("Ejercito")) == ejercitoId).ToList()
                : basicas;
            if (filtradas.Count == 0) filtradas = basicas;
            poolIds = filtradas.OrderBy(_ => rnd.Next())
                .Take(TamanioMazoDefecto).Select(x => x.id).ToList();
        }

        // Excluir cartas ya en el tablero (por id) y barajar.
        var pool = poolIds.Where(id => !cartasEnTablero.Contains(id))
            .OrderBy(_ => rnd.Next()).ToList();

        var mano = pool.Take(TamanioManoInicial).ToList();
        var resto = pool.Skip(TamanioManoInicial).ToList();
        return (mano, resto);
    }

    // ── Helpers de serialización ──────────────────────────────────────────────

    private static EfectosCelda ParseEfectosCelda(object? raw)
    {
        var result = new EfectosCelda();
        foreach (var kv in M.Map(raw))
        {
            var lista = M.List(kv.Value).Select(M.Map).ToList();
            if (lista.Count > 0) result[kv.Key] = lista;
        }
        return result;
    }

    private static Dictionary<string, object> ToFsTablero(Tablero t)
    {
        var o = new Dictionary<string, object>();
        foreach (var kv in t) o[kv.Key] = kv.Value.Cast<object>().ToList();
        return o;
    }

    private static Dictionary<string, object> ToFsEfectos(EfectosCelda e)
    {
        var o = new Dictionary<string, object>();
        foreach (var kv in e) o[kv.Key] = kv.Value.Cast<object>().ToList();
        return o;
    }

    private static List<object?> BuildMovimientosLog(Dictionary<string, object?> movTurno, int turno)
    {
        var log = new List<object?>();
        foreach (var kv in movTurno)
        {
            var mov = M.Map(kv.Value);
            if (M.Int(M.Get(mov, "turno")) != turno) continue;
            var celdas = M.Map(M.Get(mov, "celdas"));
            var zona = "";
            foreach (var ce in celdas)
            {
                var cartas = M.List(ce.Value);
                if (cartas.Count > 0)
                {
                    zona = M.Str(M.Get(M.Map(cartas[0]), "ownerZone"));
                    if (zona != "") break;
                }
            }
            log.Add(new Dictionary<string, object?>
            {
                ["uid"] = M.Str(M.Get(mov, "uid")),
                ["zona"] = zona,
                ["celdas"] = celdas,
            });
        }
        return log;
    }
}