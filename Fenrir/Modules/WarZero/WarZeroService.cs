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

        return await db.RunTransactionAsync(async tx =>
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