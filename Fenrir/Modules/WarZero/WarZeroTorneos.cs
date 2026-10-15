// ─────────────────────────────────────────────────────────────────────────────
// WarZeroTorneos.cs
//
// MODO TORNEO · FASE 1: configuración (editores), inscripción (jugadores) y
// sorteo de grupos aleatorios de una ronda.
//
// MODELO (Firestore)
//   Torneos/{id}
//     nombre, descripcion
//     inicioInscripcion, finInscripcion, inicioTorneo   (Timestamp, UTC)
//     estado        "borrador" | "publicado" | "en_curso" | "finalizado"
//     rondas        [ { numero, clasifican, partidas: [ { tipo, jugadores, modo } ] } ]
//                     tipo      "partida" | "duelo"   (duelo ⇒ jugadores = 2)
//                     jugadores 2 | 4 | 6 | 8
//                     modo      "normal" | "meteoritos" | "apocalipsis"
//                     clasifican  nº de jugadores que pasan a la siguiente ronda
//                                 por suma de PC (0 en la última ronda)
//     rondaActual   0 = aún no sorteado; N = ronda con grupos sorteados
//     premios       [ { desde, hasta, xp, dinero, zero, zeroTipo, cartaId,
//                       cartaNombre, cartaCantidad, externo } ]
//                     Rango de puestos (1–1, 2–2, 4–8…). Todo se entrega solo al
//                     finalizar salvo `externo` (camiseta, metálico…), que queda
//                     registrado como pendiente para entregarlo a mano.
//     premiosEntregados  true tras el reparto (el reparto es idempotente)
//     creadoPor, actualizado
//   Torneos/{id}/Inscritos/{uid}   uid, alias, fecha
//   Torneos/{id}/Grupos/{r}_{p}_{g} ronda, partida, grupo, tipo, modo,
//                                   jugadores [uid], alias [alias], descansa
//   Torneos/{id}/Premiados/{uid}    puesto, alias, premio entregado, externo,
//                                   externoEntregado
//
// SEGURIDAD
//   · Todas las rutas exigen  Authorization: Bearer <ID token de Firebase>.
//   · /admin/torneos/* además exige el custom claim editor=true.
//   · La ventana de inscripción la decide la hora del SERVIDOR (UTC).
//
// ENDPOINTS (jugador)
//   GET  /warzero/torneos/vigente
//   POST /warzero/torneos/{id}/inscribir
//   POST /warzero/torneos/{id}/desinscribir
//   GET  /warzero/torneos/{id}/grupos?ronda=N
//   GET  /warzero/torneos/{id}/clasificacion?ronda=N   (cerrada o provisional)
// ENDPOINTS (editor)
//   GET    /admin/torneos
//   POST   /admin/torneos                 crear / actualizar (id opcional)
//   DELETE /admin/torneos/{id}
//   GET    /admin/torneos/{id}/inscritos
//   POST   /admin/torneos/{id}/sortear    { ronda, forzar }
//   POST   /admin/torneos/{id}/procesar   pasada del motor (salas, plazos, cierre)
//   POST   /admin/torneos/{id}/cerrar-ronda  cierra ya la ronda en curso
//   POST   /admin/torneos/{id}/finalizar  { clasificacion: [uid, …] }  (manual)
//
// FASE 2 (salas, resultados, rondas y final): WarZeroTorneosMotor.cs
//   GET    /admin/torneos/{id}/premiados
//   POST   /admin/torneos/{id}/premiados/{uid}/externo  { entregado }
//
// Se registra en Program.cs con:  app.MapWarZeroTorneosEndpoints();
// ─────────────────────────────────────────────────────────────────────────────

using FirebaseAdmin.Auth;
using Google.Cloud.Firestore;

public static class WarZeroTorneosExtensions
{
    internal const string Coleccion = "Torneos";
    internal const string SubInscritos = "Inscritos";
    internal const string SubGrupos = "Grupos";
    private const string SubPremiados = "Premiados";

    private static readonly HashSet<string> EstadosVigentes = new() { "publicado", "en_curso" };
    private static readonly HashSet<string> EstadosValidos = new() { "borrador", "publicado", "en_curso", "finalizado" };
    private static readonly HashSet<string> ModosValidos = new() { "normal", "meteoritos", "apocalipsis" };
    private static readonly HashSet<int> JugadoresValidos = new() { 2, 4, 6, 8 };
    private static readonly HashSet<string> ModosTurno = new() { "rapida", "diario", "turno12h" };
    private static readonly HashSet<string> ZeroValidos = new() { "zeroCeleste", "zeroEscarlata", "zeroFuego", "zeroNatural", "zeroPuro" };

    public static WebApplication MapWarZeroTorneosEndpoints(this WebApplication app)
    {
        // ═════════════════════════════════════════════════════════════════════
        // JUGADOR
        // ═════════════════════════════════════════════════════════════════════

        // Torneo vigente (publicado o en curso) más próximo, con el estado de
        // la inscripción calculado con la hora del servidor.
        app.MapGet("/warzero/torneos/vigente", async (HttpContext ctx, WarZeroFirestore fs, ILoggerFactory lf) =>
        {
            var log = lf.CreateLogger("WarZero.Torneos");
            var user = await VerificarUsuarioAsync(ctx, exigirEditor: false);
            if (user.Error != null) return user.Error;

            try
            {
                var snap = await fs.Db.Collection(Coleccion)
                    .WhereIn("estado", EstadosVigentes.Cast<object>())
                    .GetSnapshotAsync();

                var torneos = snap.Documents.Select(LeerTorneo)
                    .OrderBy(t => t.InicioTorneo)
                    .ToList();
                if (torneos.Count == 0)
                    return Results.Ok(new { torneo = (object?)null, ahora = DateTime.UtcNow });

                var t = torneos[0];
                var docRef = fs.Db.Collection(Coleccion).Document(t.Id);

                // Avance perezoso: crea salas / cierra rondas pendientes.
                if (t.Estado == "en_curso" && DateTime.UtcNow >= t.InicioTorneo)
                {
                    try
                    {
                        await TorneoMotor.ProcesarTorneoAsync(fs.Db, t.Id);
                        t = LeerTorneo(await docRef.GetSnapshotAsync());
                    }
                    catch (Exception ex) { log.LogWarning(ex, "Procesar torneo {Id} falló", t.Id); }
                }
                var inscritos = await docRef.Collection(SubInscritos).GetSnapshotAsync();
                var inscrito = inscritos.Documents.Any(d => d.Id == user.Uid);

                return Results.Ok(new
                {
                    torneo = TorneoDto(t),
                    inscritos = inscritos.Count,
                    inscrito,
                    estadoInscripcion = EstadoInscripcion(t, DateTime.UtcNow),
                    ahora = DateTime.UtcNow,
                });
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Error leyendo torneo vigente");
                return Results.Problem("No se pudo leer el torneo.");
            }
        });

        app.MapPost("/warzero/torneos/{id}/inscribir", async (string id, HttpContext ctx, WarZeroFirestore fs, ILoggerFactory lf) =>
        {
            var log = lf.CreateLogger("WarZero.Torneos");
            var user = await VerificarUsuarioAsync(ctx, exigirEditor: false);
            if (user.Error != null) return user.Error;

            try
            {
                var docRef = fs.Db.Collection(Coleccion).Document(id);
                var doc = await docRef.GetSnapshotAsync();
                if (!doc.Exists) return Results.NotFound(new { error = "El torneo no existe." });

                var t = LeerTorneo(doc);
                if (!EstadosVigentes.Contains(t.Estado))
                    return Results.BadRequest(new { error = "El torneo no está publicado." });
                if (EstadoInscripcion(t, DateTime.UtcNow) != "abierta")
                    return Results.BadRequest(new { error = "La inscripción no está abierta." });

                string alias = "";
                try
                {
                    var j = await fs.Db.Collection("Jugadores").Document(user.Uid).GetSnapshotAsync();
                    if (j.Exists && j.TryGetValue<string>("alias", out var a)) alias = a ?? "";
                }
                catch { /* alias opcional */ }

                await docRef.Collection(SubInscritos).Document(user.Uid).SetAsync(new Dictionary<string, object>
                {
                    ["uid"] = user.Uid,
                    ["alias"] = alias,
                    ["fecha"] = Timestamp.GetCurrentTimestamp(),
                });

                var total = (await docRef.Collection(SubInscritos).GetSnapshotAsync()).Count;
                return Results.Ok(new { ok = true, inscritos = total });
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Error inscribiendo en torneo {Id}", id);
                return Results.Problem("No se pudo completar la inscripción.");
            }
        });

        app.MapPost("/warzero/torneos/{id}/desinscribir", async (string id, HttpContext ctx, WarZeroFirestore fs, ILoggerFactory lf) =>
        {
            var log = lf.CreateLogger("WarZero.Torneos");
            var user = await VerificarUsuarioAsync(ctx, exigirEditor: false);
            if (user.Error != null) return user.Error;

            try
            {
                var docRef = fs.Db.Collection(Coleccion).Document(id);
                var doc = await docRef.GetSnapshotAsync();
                if (!doc.Exists) return Results.NotFound(new { error = "El torneo no existe." });

                var t = LeerTorneo(doc);
                if (EstadoInscripcion(t, DateTime.UtcNow) != "abierta")
                    return Results.BadRequest(new { error = "La inscripción ya está cerrada." });

                await docRef.Collection(SubInscritos).Document(user.Uid).DeleteAsync();
                var total = (await docRef.Collection(SubInscritos).GetSnapshotAsync()).Count;
                return Results.Ok(new { ok = true, inscritos = total });
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Error desinscribiendo de torneo {Id}", id);
                return Results.Problem("No se pudo anular la inscripción.");
            }
        });

        app.MapGet("/warzero/torneos/{id}/grupos", async (string id, int? ronda, HttpContext ctx, WarZeroFirestore fs, ILoggerFactory lf) =>
        {
            var log = lf.CreateLogger("WarZero.Torneos");
            var user = await VerificarUsuarioAsync(ctx, exigirEditor: false);
            if (user.Error != null) return user.Error;

            try
            {
                return Results.Ok(new { grupos = await LeerGruposAsync(fs.Db, id, ronda) });
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Error leyendo grupos de {Id}", id);
                return Results.Problem("No se pudieron leer los grupos.");
            }
        });

        // ═════════════════════════════════════════════════════════════════════
        // EDITOR
        // ═════════════════════════════════════════════════════════════════════

        app.MapGet("/admin/torneos", async (HttpContext ctx, WarZeroFirestore fs, ILoggerFactory lf) =>
        {
            var log = lf.CreateLogger("WarZero.TorneosAdmin");
            var ed = await VerificarUsuarioAsync(ctx, exigirEditor: true);
            if (ed.Error != null) return ed.Error;

            try
            {
                var snap = await fs.Db.Collection(Coleccion).GetSnapshotAsync();
                var lista = new List<object>();
                foreach (var d in snap.Documents)
                {
                    var t = LeerTorneo(d);
                    var n = (await d.Reference.Collection(SubInscritos).GetSnapshotAsync()).Count;
                    lista.Add(new { torneo = TorneoDto(t), inscritos = n, estadoInscripcion = EstadoInscripcion(t, DateTime.UtcNow) });
                }
                return Results.Ok(new { torneos = lista });
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Error listando torneos");
                return Results.Problem("No se pudieron listar los torneos.");
            }
        });

        app.MapPost("/admin/torneos", async (HttpContext ctx, WarZeroFirestore fs, TorneoRequest req, ILoggerFactory lf) =>
        {
            var log = lf.CreateLogger("WarZero.TorneosAdmin");
            var ed = await VerificarUsuarioAsync(ctx, exigirEditor: true);
            if (ed.Error != null) return ed.Error;

            var error = Validar(req);
            if (error != null) return Results.BadRequest(new { error });

            try
            {
                var col = fs.Db.Collection(Coleccion);
                var docRef = string.IsNullOrWhiteSpace(req.Id) ? col.Document() : col.Document(req.Id!.Trim());

                // Premios: se comprueba que las cartas existen y se guarda su nombre.
                var premios = new List<object>();
                foreach (var p in (req.Premios ?? new()).OrderBy(p => p.Desde))
                {
                    var cartaId = (p.CartaId ?? "").Trim();
                    var cartaNombre = "";
                    if (cartaId.Length > 0)
                    {
                        var c = await fs.Db.Collection("Cartas").Document(cartaId).GetSnapshotAsync();
                        if (!c.Exists)
                            return Results.BadRequest(new { error = $"La carta del premio {p.Desde}º no existe." });
                        cartaNombre = M.Str(M.Get(M.Map(c.ToDictionary()), "Nombre", "nombre"));
                    }
                    premios.Add(new Dictionary<string, object>
                    {
                        ["desde"] = p.Desde,
                        ["hasta"] = Math.Max(p.Desde, p.Hasta),
                        ["xp"] = Math.Max(0, p.Xp),
                        ["dinero"] = Math.Max(0, p.Dinero),
                        ["zero"] = Math.Max(0, p.Zero),
                        ["zeroTipo"] = string.IsNullOrWhiteSpace(p.ZeroTipo) ? "zeroPuro" : p.ZeroTipo!,
                        ["cartaId"] = cartaId,
                        ["cartaNombre"] = cartaNombre,
                        ["cartaCantidad"] = cartaId.Length > 0 ? Math.Max(1, p.CartaCantidad) : 0,
                        ["externo"] = (p.Externo ?? "").Trim(),
                    });
                }

                var rondas = req.Rondas!.Select((r, i) => new Dictionary<string, object>
                {
                    ["numero"] = i + 1,
                    ["clasifican"] = i == req.Rondas!.Count - 1 ? 0 : r.Clasifican,
                    ["partidas"] = r.Partidas!.Select(p =>
                    {
                        var duelo = p.Tipo == "duelo";
                        return (object)new Dictionary<string, object>
                        {
                            ["tipo"] = duelo ? "duelo" : "partida",
                            ["jugadores"] = duelo ? 2 : p.Jugadores,
                            ["modo"] = duelo ? "normal" : (p.Modo ?? "normal"),
                        };
                    }).ToList(),
                }).Cast<object>().ToList();

                var datos = new Dictionary<string, object>
                {
                    ["nombre"] = req.Nombre!.Trim(),
                    ["descripcion"] = (req.Descripcion ?? "").Trim(),
                    ["inicioInscripcion"] = Timestamp.FromDateTime(req.InicioInscripcion!.Value.ToUniversalTime()),
                    ["finInscripcion"] = Timestamp.FromDateTime(req.FinInscripcion!.Value.ToUniversalTime()),
                    ["inicioTorneo"] = Timestamp.FromDateTime(req.InicioTorneo!.Value.ToUniversalTime()),
                    ["estado"] = string.IsNullOrWhiteSpace(req.Estado) ? "borrador" : req.Estado!,
                    ["rondas"] = rondas,
                    ["premios"] = premios,
                    ["modoTurno"] = string.IsNullOrWhiteSpace(req.ModoTurno) ? "diario" : req.ModoTurno!,
                    ["plazoArranqueHoras"] = req.PlazoArranqueHoras <= 0 ? 24 : req.PlazoArranqueHoras,
                    ["actualizado"] = Timestamp.GetCurrentTimestamp(),
                    ["actualizadoPor"] = ed.Email,
                };

                var existente = await docRef.GetSnapshotAsync();
                if (!existente.Exists)
                {
                    datos["creadoPor"] = ed.Email;
                    datos["creado"] = Timestamp.GetCurrentTimestamp();
                    datos["rondaActual"] = 0;
                }
                await docRef.SetAsync(datos, SetOptions.MergeAll);

                var t = LeerTorneo(await docRef.GetSnapshotAsync());
                log.LogInformation("Torneo {Id} guardado por {Email}", docRef.Id, ed.Email);
                return Results.Ok(new { torneo = TorneoDto(t) });
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Error guardando torneo");
                return Results.Problem("No se pudo guardar el torneo.");
            }
        });

        app.MapDelete("/admin/torneos/{id}", async (string id, HttpContext ctx, WarZeroFirestore fs, ILoggerFactory lf) =>
        {
            var log = lf.CreateLogger("WarZero.TorneosAdmin");
            var ed = await VerificarUsuarioAsync(ctx, exigirEditor: true);
            if (ed.Error != null) return ed.Error;

            try
            {
                var docRef = fs.Db.Collection(Coleccion).Document(id);
                foreach (var sub in new[] { SubInscritos, SubGrupos })
                {
                    var s = await docRef.Collection(sub).GetSnapshotAsync();
                    foreach (var d in s.Documents) await d.Reference.DeleteAsync();
                }
                await docRef.DeleteAsync();
                log.LogInformation("Torneo {Id} borrado por {Email}", id, ed.Email);
                return Results.Ok(new { ok = true });
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Error borrando torneo {Id}", id);
                return Results.Problem("No se pudo borrar el torneo.");
            }
        });

        app.MapGet("/admin/torneos/{id}/inscritos", async (string id, HttpContext ctx, WarZeroFirestore fs, ILoggerFactory lf) =>
        {
            var log = lf.CreateLogger("WarZero.TorneosAdmin");
            var ed = await VerificarUsuarioAsync(ctx, exigirEditor: true);
            if (ed.Error != null) return ed.Error;

            try
            {
                var s = await fs.Db.Collection(Coleccion).Document(id).Collection(SubInscritos).GetSnapshotAsync();
                var lista = s.Documents.Select(d => new
                {
                    uid = d.Id,
                    alias = d.TryGetValue<string>("alias", out var a) ? a ?? "" : "",
                    fecha = d.TryGetValue<Timestamp>("fecha", out var f) ? f.ToDateTime() : (DateTime?)null,
                }).OrderBy(x => x.fecha).ToList();
                return Results.Ok(new { inscritos = lista });
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Error leyendo inscritos de {Id}", id);
                return Results.Problem("No se pudieron leer los inscritos.");
            }
        });

        // Sorteo aleatorio de grupos para una ronda. En la ronda 1 entran todos
        // los inscritos; las rondas siguientes (clasificados por PC) llegan en
        // la fase 2. Se rehace desde cero si se vuelve a sortear.
        app.MapPost("/admin/torneos/{id}/sortear", async (string id, SortearRequest req, HttpContext ctx, WarZeroFirestore fs, ILoggerFactory lf) =>
        {
            var log = lf.CreateLogger("WarZero.TorneosAdmin");
            var ed = await VerificarUsuarioAsync(ctx, exigirEditor: true);
            if (ed.Error != null) return ed.Error;

            try
            {
                var docRef = fs.Db.Collection(Coleccion).Document(id);
                var doc = await docRef.GetSnapshotAsync();
                if (!doc.Exists) return Results.NotFound(new { error = "El torneo no existe." });
                var t = LeerTorneo(doc);

                var numRonda = req.Ronda <= 0 ? 1 : req.Ronda;
                if (numRonda != 1)
                    return Results.BadRequest(new { error = "Solo se sortea a mano la ronda 1; las siguientes se sortean solas al cerrar cada ronda." });
                if (t.Rondas.Count == 0)
                    return Results.BadRequest(new { error = "El torneo no tiene rondas." });
                if (t.RondaActual > 1)
                    return Results.BadRequest(new { error = "El torneo ya va por una ronda posterior." });
                if (!req.Forzar && EstadoInscripcion(t, DateTime.UtcNow) != "cerrada")
                    return Results.BadRequest(new { error = "La inscripción aún no ha terminado. Usa 'forzar' para sortear igualmente." });

                var previos = await docRef.Collection(SubGrupos).WhereEqualTo("ronda", 1).GetSnapshotAsync();
                if (previos.Documents.Any(d => d.TryGetValue<string>("salaId", out var sid) && !string.IsNullOrEmpty(sid)))
                    return Results.BadRequest(new { error = "Las salas de la ronda 1 ya están creadas: no se puede volver a sortear." });

                var insSnap = await docRef.Collection(SubInscritos).GetSnapshotAsync();
                var jugadores = insSnap.Documents.Select(d => (uid: d.Id,
                    alias: d.TryGetValue<string>("alias", out var a) ? a ?? "" : "")).ToList();
                if (jugadores.Count < 2)
                    return Results.BadRequest(new { error = "Hacen falta al menos 2 inscritos." });

                var totalGrupos = await TorneoMotor.SortearRondaAsync(fs.Db, t, 1, jugadores);
                // Si el torneo ya ha empezado, las salas se crean al momento.
                _ = Task.Run(() => TorneoMotor.ProcesarTorneoAsync(fs.Db, id));

                log.LogInformation("Torneo {Id}: ronda {R} sorteada ({G} grupos, {J} jugadores) por {Email}",
                    id, numRonda, totalGrupos, jugadores.Count, ed.Email);
                return Results.Ok(new { ok = true, grupos = await LeerGruposAsync(fs.Db, id, numRonda) });
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Error sorteando grupos de {Id}", id);
                return Results.Problem("No se pudieron sortear los grupos.");
            }
        });

        // Clasificación de una ronda (cerrada) o la PROVISIONAL de la ronda en
        // curso, calculada con los grupos ya terminados.
        app.MapGet("/warzero/torneos/{id}/clasificacion", async (string id, int? ronda, HttpContext ctx, WarZeroFirestore fs, ILoggerFactory lf) =>
        {
            var user = await VerificarUsuarioAsync(ctx, exigirEditor: false);
            if (user.Error != null) return user.Error;
            try
            {
                var docRef = fs.Db.Collection(Coleccion).Document(id);
                var doc = await docRef.GetSnapshotAsync();
                if (!doc.Exists) return Results.NotFound(new { error = "El torneo no existe." });
                var t = LeerTorneo(doc);
                var r = ronda is > 0 ? ronda.Value : Math.Max(1, t.RondaActual);

                var c = await docRef.Collection("Clasificacion").Document(r.ToString()).GetSnapshotAsync();
                if (c.Exists)
                {
                    var m = c.ToDictionary();
                    var filas = m.TryGetValue("filas", out var fv) && fv is IEnumerable<object> fl
                        ? fl.OfType<IDictionary<string, object>>().Select(f => (object)f.ToDictionary(kv => kv.Key, kv => kv.Value)).ToList()
                        : new List<object>();
                    return Results.Ok(new
                    {
                        ronda = r,
                        cerrada = true,
                        clasifican = m.TryGetValue("clasifican", out var cl) && cl != null ? Convert.ToInt32(cl) : 0,
                        final = m.TryGetValue("final", out var fi) && fi is bool fb && fb,
                        filas,
                    });
                }

                var gs = await docRef.Collection(SubGrupos).WhereEqualTo("ronda", r).GetSnapshotAsync();
                var tabla = TorneoMotor.TablaRonda(gs.Documents.Select(TorneoMotor.LeerGrupo))
                    .OrderByDescending(f => f.Pc).ThenBy(f => f.Alias).ToList();
                var pasan = r <= t.Rondas.Count && r < t.Rondas.Count ? t.Rondas[r - 1].Clasifican : 0;
                return Results.Ok(new
                {
                    ronda = r,
                    cerrada = false,
                    clasifican = pasan,
                    final = r >= t.Rondas.Count,
                    filas = tabla.Select((f, i) => new
                    {
                        puesto = i + 1,
                        uid = f.Uid,
                        alias = f.Alias,
                        pc = f.Pc,
                        jugadas = f.Jugadas,
                        total = f.Total,
                        clasificado = i < pasan,
                    }),
                });
            }
            catch (Exception ex)
            {
                lf.CreateLogger("WarZero.Torneos").LogError(ex, "Error leyendo clasificación de {Id}", id);
                return Results.Problem("No se pudo leer la clasificación.");
            }
        });

        // Fuerza una pasada del motor (crear salas, revisar plazos, cerrar ronda).
        app.MapPost("/admin/torneos/{id}/procesar", async (string id, HttpContext ctx, WarZeroFirestore fs, ILoggerFactory lf) =>
        {
            var ed = await VerificarUsuarioAsync(ctx, exigirEditor: true);
            if (ed.Error != null) return ed.Error;
            try
            {
                await TorneoMotor.ProcesarTorneoAsync(fs.Db, id);
                return Results.Ok(new { ok = true, grupos = await LeerGruposAsync(fs.Db, id, null) });
            }
            catch (Exception ex)
            {
                lf.CreateLogger("WarZero.TorneosAdmin").LogError(ex, "Error procesando {Id}", id);
                return Results.Problem("No se pudo procesar el torneo.");
            }
        });

        // Cierra la ronda en curso YA: las partidas sin terminar cuentan con el
        // PC que lleven. Sortea la siguiente o finaliza con premios.
        app.MapPost("/admin/torneos/{id}/cerrar-ronda", async (string id, HttpContext ctx, WarZeroFirestore fs, ILoggerFactory lf) =>
        {
            var log = lf.CreateLogger("WarZero.TorneosAdmin");
            var ed = await VerificarUsuarioAsync(ctx, exigirEditor: true);
            if (ed.Error != null) return ed.Error;
            try
            {
                var doc = await fs.Db.Collection(Coleccion).Document(id).GetSnapshotAsync();
                if (!doc.Exists) return Results.NotFound(new { error = "El torneo no existe." });
                var t = LeerTorneo(doc);
                if (t.Estado != "en_curso" || t.RondaActual <= 0)
                    return Results.BadRequest(new { error = "No hay ninguna ronda en curso." });
                var cerrada = await TorneoMotor.CerrarRondaAsync(fs.Db, id, t.RondaActual, forzar: true);
                if (cerrada) await TorneoMotor.ProcesarTorneoAsync(fs.Db, id);
                log.LogInformation("Torneo {Id}: ronda {R} cerrada a mano por {Email}", id, t.RondaActual, ed.Email);
                var nuevo = LeerTorneo(await fs.Db.Collection(Coleccion).Document(id).GetSnapshotAsync());
                return Results.Ok(new { ok = cerrada, torneo = TorneoDto(nuevo) });
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Error cerrando ronda de {Id}", id);
                return Results.Problem("No se pudo cerrar la ronda.");
            }
        });

        // Finaliza el torneo con la clasificación final (uids en orden) y
        // reparte los premios. En la fase 2 la clasificación la calculará el
        // propio servidor con la suma de PC y llamará a EntregarPremiosAsync.
        app.MapPost("/admin/torneos/{id}/finalizar", async (string id, FinalizarRequest req, HttpContext ctx, WarZeroFirestore fs, ILoggerFactory lf) =>
        {
            var log = lf.CreateLogger("WarZero.TorneosAdmin");
            var ed = await VerificarUsuarioAsync(ctx, exigirEditor: true);
            if (ed.Error != null) return ed.Error;

            var clasif = (req.Clasificacion ?? new()).Where(u => !string.IsNullOrWhiteSpace(u)).Distinct().ToList();
            if (clasif.Count == 0) return Results.BadRequest(new { error = "La clasificación está vacía." });

            try
            {
                var r = await EntregarPremiosAsync(fs.Db, id, clasif, log);
                if (r.Error != null) return Results.BadRequest(new { error = r.Error });
                return Results.Ok(new { ok = true, yaEntregados = r.YaEntregados, premiados = await LeerPremiadosAsync(fs.Db, id) });
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Error finalizando torneo {Id}", id);
                return Results.Problem("No se pudieron repartir los premios.");
            }
        });

        app.MapGet("/admin/torneos/{id}/premiados", async (string id, HttpContext ctx, WarZeroFirestore fs, ILoggerFactory lf) =>
        {
            var ed = await VerificarUsuarioAsync(ctx, exigirEditor: true);
            if (ed.Error != null) return ed.Error;
            try { return Results.Ok(new { premiados = await LeerPremiadosAsync(fs.Db, id) }); }
            catch (Exception ex)
            {
                lf.CreateLogger("WarZero.TorneosAdmin").LogError(ex, "Error leyendo premiados de {Id}", id);
                return Results.Problem("No se pudieron leer los premiados.");
            }
        });

        // Marca como entregado (o no) el premio de fuera de la app de un jugador.
        app.MapPost("/admin/torneos/{id}/premiados/{uid}/externo", async (string id, string uid, ExternoRequest req, HttpContext ctx, WarZeroFirestore fs, ILoggerFactory lf) =>
        {
            var ed = await VerificarUsuarioAsync(ctx, exigirEditor: true);
            if (ed.Error != null) return ed.Error;
            try
            {
                var refP = fs.Db.Collection(Coleccion).Document(id).Collection(SubPremiados).Document(uid);
                if (!(await refP.GetSnapshotAsync()).Exists) return Results.NotFound(new { error = "Ese jugador no tiene premio." });
                await refP.UpdateAsync(new Dictionary<string, object>
                {
                    ["externoEntregado"] = req.Entregado,
                    ["externoEntregadoPor"] = ed.Email,
                    ["externoFecha"] = Timestamp.GetCurrentTimestamp(),
                });
                return Results.Ok(new { ok = true });
            }
            catch (Exception ex)
            {
                lf.CreateLogger("WarZero.TorneosAdmin").LogError(ex, "Error marcando externo {Id}/{Uid}", id, uid);
                return Results.Problem("No se pudo actualizar.");
            }
        });

        return app;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // PREMIOS
    // ─────────────────────────────────────────────────────────────────────────

    public sealed record ResultadoPremios(bool YaEntregados, string? Error);

    /// Reparte los premios según [clasificacion] (uids, 1º primero) y marca el
    /// torneo como finalizado. IDEMPOTENTE: si ya se entregaron no repite nada.
    /// XP (recalcula el nivel), dinero, Cristales Zero y carta van directos a la
    /// cuenta; el premio externo queda en Premiados/{uid} como pendiente.
    public static async Task<ResultadoPremios> EntregarPremiosAsync(
        FirestoreDb db, string torneoId, List<string> clasificacion, ILogger log)
    {
        var tRef = db.Collection(Coleccion).Document(torneoId);

        // Reserva atómica del reparto para que dos llamadas no paguen dos veces.
        var reservado = await db.RunTransactionAsync(async tx =>
        {
            var s = await tx.GetSnapshotAsync(tRef);
            if (!s.Exists) return (ok: false, error: "El torneo no existe.", ya: false);
            if (s.TryGetValue<bool>("premiosEntregados", out var ya) && ya) return (ok: false, error: (string?)null, ya: true);
            tx.Update(tRef, new Dictionary<string, object>
            {
                ["premiosEntregados"] = true,
                ["estado"] = "finalizado",
                ["clasificacionFinal"] = clasificacion,
                ["finalizado"] = Timestamp.GetCurrentTimestamp(),
            });
            return (ok: true, error: (string?)null, ya: false);
        });
        if (reservado.ya) return new(true, null);
        if (!reservado.ok) return new(false, reservado.error);

        var t = LeerTorneo(await tRef.GetSnapshotAsync());

        for (var i = 0; i < clasificacion.Count; i++)
        {
            var puesto = i + 1;
            var uid = clasificacion[i];
            var premio = t.Premios.FirstOrDefault(p => puesto >= p.Desde && puesto <= p.Hasta);
            if (premio == null) continue;

            var jRef = db.Collection("Jugadores").Document(uid);
            var jSnap = await jRef.GetSnapshotAsync();
            var alias = jSnap.Exists && jSnap.TryGetValue<string>("alias", out var a) ? a ?? "" : "";

            try
            {
                if (premio.Xp > 0 || premio.Dinero > 0 || premio.Zero > 0)
                {
                    long xpActual = jSnap.Exists ? M.Long(M.Get(M.Map(M.FromFs(jSnap.ToDictionary())), "experiencia")) : 0;
                    var campos = new Dictionary<string, object>();
                    if (premio.Xp > 0)
                    {
                        campos["experiencia"] = FieldValue.Increment(premio.Xp);
                        campos["nivel"] = WarZeroRecompensas.NivelDesdeExperiencia(xpActual + premio.Xp);
                    }
                    if (premio.Dinero > 0) campos["dinero"] = FieldValue.Increment(premio.Dinero);
                    if (premio.Zero > 0) campos[premio.ZeroTipo] = FieldValue.Increment(premio.Zero);
                    await jRef.SetAsync(campos, SetOptions.MergeAll);
                }
                if (premio.CartaId.Length > 0 && premio.CartaCantidad > 0)
                    await DarCartaAsync(db, uid, premio.CartaId, premio.CartaCantidad);
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Torneo {Id}: error entregando premio a {Uid}", torneoId, uid);
            }

            await tRef.Collection(SubPremiados).Document(uid).SetAsync(new Dictionary<string, object>
            {
                ["uid"] = uid,
                ["alias"] = alias,
                ["puesto"] = puesto,
                ["xp"] = premio.Xp,
                ["dinero"] = premio.Dinero,
                ["zero"] = premio.Zero,
                ["zeroTipo"] = premio.ZeroTipo,
                ["cartaId"] = premio.CartaId,
                ["cartaNombre"] = premio.CartaNombre,
                ["cartaCantidad"] = premio.CartaCantidad,
                ["externo"] = premio.Externo,
                ["externoEntregado"] = premio.Externo.Length == 0,
                ["fecha"] = Timestamp.GetCurrentTimestamp(),
            });
        }

        log.LogInformation("Torneo {Id}: premios repartidos a {N} jugadores", torneoId, clasificacion.Count);
        return new(false, null);
    }

    /// Suma [cantidad] copias de una carta a la colección (mismo criterio que
    /// el envío de cartas de administración).
    private static async Task DarCartaAsync(FirestoreDb db, string uid, string cartaId, int cantidad)
    {
        var colRef = db.Collection("Jugadores").Document(uid).Collection("Coleccion").Document(cartaId);
        await db.RunTransactionAsync(async tx =>
        {
            var snap = await tx.GetSnapshotAsync(colRef);
            var actual = 0;
            var eraPlaceholder = false;
            if (snap.Exists)
            {
                var d = M.Map(snap.ToDictionary());
                eraPlaceholder = M.Bool(M.Get(d, "placeholder"));
                if (!eraPlaceholder)
                {
                    var raw = M.Get(d, "cantidad");
                    actual = raw == null ? 1 : Math.Max(1, M.Int(raw));
                }
            }
            var data = new Dictionary<string, object?>
            {
                ["cantidad"] = actual + cantidad,
                ["vecesObtenida"] = FieldValue.Increment(cantidad),
                ["fechaObtenida"] = FieldValue.ServerTimestamp,
            };
            if (!snap.Exists) data["skinsDesbloqueadas"] = new List<object?>();
            if (eraPlaceholder) data["placeholder"] = FieldValue.Delete;
            tx.Set(colRef, data, SetOptions.MergeAll);
            return true;
        });
    }

    private static async Task<List<object>> LeerPremiadosAsync(FirestoreDb db, string id)
    {
        var s = await db.Collection(Coleccion).Document(id).Collection(SubPremiados).GetSnapshotAsync();
        return s.Documents.Select(d =>
        {
            var m = d.ToDictionary();
            string S(string k) => m.TryGetValue(k, out var v) ? v?.ToString() ?? "" : "";
            int I(string k) => m.TryGetValue(k, out var v) && v != null ? Convert.ToInt32(v) : 0;
            return new
            {
                uid = d.Id,
                alias = S("alias"),
                puesto = I("puesto"),
                xp = I("xp"),
                dinero = I("dinero"),
                zero = I("zero"),
                zeroTipo = S("zeroTipo"),
                cartaId = S("cartaId"),
                cartaNombre = S("cartaNombre"),
                cartaCantidad = I("cartaCantidad"),
                externo = S("externo"),
                externoEntregado = m.TryGetValue("externoEntregado", out var e) && e is bool b && b,
            };
        }).OrderBy(x => x.puesto).Cast<object>().ToList();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // REPARTO
    // ─────────────────────────────────────────────────────────────────────────

    /// Reparte jugadores ya barajados en grupos de capacidad máx. [tam].
    /// · Duelo: parejas; si sobra uno, descansa.
    /// · Partida: nº de grupos = ceil(n / tam) con tamaños equilibrados
    ///   (difieren en 1 como mucho), así nunca queda un grupo de 1.
    internal static (List<List<(string uid, string alias)>> grupos, (string uid, string alias)? descansa)
        Repartir(List<(string uid, string alias)> js, int tam, bool duelo)
    {
        var grupos = new List<List<(string, string)>>();
        if (duelo)
        {
            (string, string)? sobra = null;
            var lista = js;
            if (js.Count % 2 == 1) { sobra = js[^1]; lista = js.Take(js.Count - 1).ToList(); }
            for (var i = 0; i < lista.Count; i += 2) grupos.Add(lista.Skip(i).Take(2).ToList());
            return (grupos, sobra);
        }

        var nGrupos = Math.Max(1, (int)Math.Ceiling(js.Count / (double)tam));
        for (var i = 0; i < nGrupos; i++) grupos.Add(new());
        for (var i = 0; i < js.Count; i++) grupos[i % nGrupos].Add(js[i]);
        return (grupos, null);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // LECTURA / DTO
    // ─────────────────────────────────────────────────────────────────────────

    internal sealed record PartidaT(string Tipo, int Jugadores, string Modo);
    internal sealed record RondaT(int Numero, int Clasifican, List<PartidaT> Partidas);
    internal sealed record PremioT(int Desde, int Hasta, int Xp, int Dinero, int Zero, string ZeroTipo,
        string CartaId, string CartaNombre, int CartaCantidad, string Externo);
    internal sealed record TorneoT(string Id, string Nombre, string Descripcion, DateTime InicioInscripcion,
        DateTime FinInscripcion, DateTime InicioTorneo, string Estado, int RondaActual, List<RondaT> Rondas,
        List<PremioT> Premios, bool PremiosEntregados, string ModoTurno, int PlazoArranqueHoras);

    internal static TorneoT LeerTorneo(DocumentSnapshot d)
    {
        var dic = d.ToDictionary();
        string S(string k) => dic.TryGetValue(k, out var v) ? v?.ToString() ?? "" : "";
        DateTime T(string k) => dic.TryGetValue(k, out var v) && v is Timestamp ts ? ts.ToDateTime() : DateTime.MinValue;
        int I(IDictionary<string, object> m, string k) => m.TryGetValue(k, out var v) && v != null ? Convert.ToInt32(v) : 0;

        var rondas = new List<RondaT>();
        if (dic.TryGetValue("rondas", out var rv) && rv is IEnumerable<object> rl)
        {
            foreach (var ro in rl.OfType<IDictionary<string, object>>())
            {
                var partidas = new List<PartidaT>();
                if (ro.TryGetValue("partidas", out var pv) && pv is IEnumerable<object> pl)
                    foreach (var po in pl.OfType<IDictionary<string, object>>())
                        partidas.Add(new PartidaT(
                            po.TryGetValue("tipo", out var tp) ? tp?.ToString() ?? "partida" : "partida",
                            I(po, "jugadores"),
                            po.TryGetValue("modo", out var md) ? md?.ToString() ?? "normal" : "normal"));
                rondas.Add(new RondaT(I(ro, "numero"), I(ro, "clasifican"), partidas));
            }
        }

        var premios = new List<PremioT>();
        if (dic.TryGetValue("premios", out var prv) && prv is IEnumerable<object> prl)
        {
            foreach (var po in prl.OfType<IDictionary<string, object>>())
            {
                string PS(string k) => po.TryGetValue(k, out var v) ? v?.ToString() ?? "" : "";
                premios.Add(new PremioT(I(po, "desde"), I(po, "hasta"), I(po, "xp"), I(po, "dinero"), I(po, "zero"),
                    PS("zeroTipo") == "" ? "zeroPuro" : PS("zeroTipo"), PS("cartaId"), PS("cartaNombre"),
                    I(po, "cartaCantidad"), PS("externo")));
            }
        }

        return new TorneoT(d.Id, S("nombre"), S("descripcion"), T("inicioInscripcion"), T("finInscripcion"),
            T("inicioTorneo"), string.IsNullOrEmpty(S("estado")) ? "borrador" : S("estado"),
            dic.TryGetValue("rondaActual", out var ra) && ra != null ? Convert.ToInt32(ra) : 0,
            rondas.OrderBy(r => r.Numero).ToList(),
            premios.OrderBy(p => p.Desde).ToList(),
            dic.TryGetValue("premiosEntregados", out var pe) && pe is bool peb && peb,
            ModosTurno.Contains(S("modoTurno")) ? S("modoTurno") : "diario",
            dic.TryGetValue("plazoArranqueHoras", out var ph) && ph != null ? Math.Max(1, Convert.ToInt32(ph)) : 24);
    }

    private static object TorneoDto(TorneoT t) => new
    {
        id = t.Id,
        nombre = t.Nombre,
        descripcion = t.Descripcion,
        inicioInscripcion = t.InicioInscripcion,
        finInscripcion = t.FinInscripcion,
        inicioTorneo = t.InicioTorneo,
        estado = t.Estado,
        rondaActual = t.RondaActual,
        rondas = t.Rondas.Select(r => new
        {
            numero = r.Numero,
            clasifican = r.Clasifican,
            partidas = r.Partidas.Select(p => new { tipo = p.Tipo, jugadores = p.Jugadores, modo = p.Modo }),
        }),
        premios = t.Premios.Select(p => new
        {
            desde = p.Desde,
            hasta = p.Hasta,
            xp = p.Xp,
            dinero = p.Dinero,
            zero = p.Zero,
            zeroTipo = p.ZeroTipo,
            cartaId = p.CartaId,
            cartaNombre = p.CartaNombre,
            cartaCantidad = p.CartaCantidad,
            externo = p.Externo,
        }),
        premiosEntregados = t.PremiosEntregados,
        modoTurno = t.ModoTurno,
        plazoArranqueHoras = t.PlazoArranqueHoras,
    };

    /// "pendiente" (aún no abre) · "abierta" · "cerrada" (ya terminó).
    internal static string EstadoInscripcion(TorneoT t, DateTime ahoraUtc)
    {
        if (ahoraUtc < t.InicioInscripcion) return "pendiente";
        if (ahoraUtc <= t.FinInscripcion) return "abierta";
        return "cerrada";
    }

    private static async Task<List<object>> LeerGruposAsync(FirestoreDb db, string id, int? ronda)
    {
        Query q = db.Collection(Coleccion).Document(id).Collection(SubGrupos);
        if (ronda is > 0) q = q.WhereEqualTo("ronda", ronda.Value);
        var s = await q.GetSnapshotAsync();

        return s.Documents.Select(d =>
        {
            var m = d.ToDictionary();
            int I(string k) => m.TryGetValue(k, out var v) && v != null ? Convert.ToInt32(v) : 0;
            List<string> L(string k) => m.TryGetValue(k, out var v) && v is IEnumerable<object> l
                ? l.Select(x => x?.ToString() ?? "").ToList() : new List<string>();
            return new
            {
                id = d.Id,
                ronda = I("ronda"),
                partida = I("partida"),
                grupo = I("grupo"),
                tipo = m.TryGetValue("tipo", out var tp) ? tp?.ToString() ?? "partida" : "partida",
                modo = m.TryGetValue("modo", out var md) ? md?.ToString() ?? "normal" : "normal",
                capacidad = I("capacidad"),
                jugadores = L("jugadores"),
                alias = L("alias"),
                descansa = m.TryGetValue("descansa", out var de) && de is bool b && b,
                estado = m.TryGetValue("estado", out var es) ? es?.ToString() ?? "" : "",
                salaId = m.TryGetValue("salaId", out var sid) ? sid?.ToString() ?? "" : "",
                resultados = m.TryGetValue("resultados", out var rv) && rv is IDictionary<string, object> rd
                    ? rd.ToDictionary(kv => kv.Key, kv => kv.Value == null ? 0 : Convert.ToInt32(kv.Value))
                    : new Dictionary<string, int>(),
            };
        })
        .OrderBy(g => g.ronda).ThenBy(g => g.partida).ThenBy(g => g.descansa).ThenBy(g => g.grupo)
        .Cast<object>().ToList();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // VALIDACIÓN
    // ─────────────────────────────────────────────────────────────────────────

    private static string? Validar(TorneoRequest r)
    {
        if (string.IsNullOrWhiteSpace(r.Nombre)) return "Falta el nombre.";
        if (r.InicioInscripcion == null || r.FinInscripcion == null || r.InicioTorneo == null)
            return "Faltan fechas.";
        if (r.FinInscripcion <= r.InicioInscripcion)
            return "El fin de inscripción debe ser posterior a su inicio.";
        if (r.InicioTorneo < r.FinInscripcion)
            return "El torneo no puede empezar antes de que cierre la inscripción.";
        if (!string.IsNullOrWhiteSpace(r.Estado) && !EstadosValidos.Contains(r.Estado!))
            return "Estado no válido.";
        if (r.Rondas == null || r.Rondas.Count == 0) return "Añade al menos una ronda.";
        if (!string.IsNullOrWhiteSpace(r.ModoTurno) && !ModosTurno.Contains(r.ModoTurno!)) return "Modo de turno no válido.";
        if (r.PlazoArranqueHoras < 0 || r.PlazoArranqueHoras > 168) return "El plazo de arranque debe estar entre 1 y 168 horas.";

        for (var i = 0; i < r.Rondas.Count; i++)
        {
            var ro = r.Rondas[i];
            if (ro.Partidas == null || ro.Partidas.Count == 0)
                return $"La ronda {i + 1} no tiene partidas.";
            var ultima = i == r.Rondas.Count - 1;
            if (!ultima && ro.Clasifican < 1)
                return $"Indica cuántos clasifican en la ronda {i + 1}.";
            if (i > 0 && r.Rondas[i - 1].Clasifican > 0 && ro.Clasifican >= r.Rondas[i - 1].Clasifican && !ultima)
                return $"En la ronda {i + 1} deben clasificar menos jugadores que en la anterior.";
            foreach (var p in ro.Partidas)
            {
                if (p.Tipo != "partida" && p.Tipo != "duelo") return $"Tipo de partida no válido en la ronda {i + 1}.";
                if (p.Tipo == "partida" && !JugadoresValidos.Contains(p.Jugadores))
                    return $"Nº de jugadores no válido en la ronda {i + 1}.";
                if (p.Tipo == "partida" && !ModosValidos.Contains(p.Modo ?? "normal"))
                    return $"Modo no válido en la ronda {i + 1}.";
            }
        }

        var ocupados = new HashSet<int>();
        foreach (var p in r.Premios ?? new())
        {
            if (p.Desde < 1 || p.Hasta < p.Desde) return "Rango de puestos de premio no válido.";
            if (p.Hasta - p.Desde > 1000) return "Rango de puestos demasiado grande.";
            for (var k = p.Desde; k <= p.Hasta; k++)
                if (!ocupados.Add(k)) return $"El puesto {k}º tiene más de un premio.";
            if (p.Xp < 0 || p.Dinero < 0 || p.Zero < 0 || p.CartaCantidad < 0) return "Los premios no pueden ser negativos.";
            if (p.Zero > 0 && !ZeroValidos.Contains(p.ZeroTipo ?? "zeroPuro")) return "Tipo de Cristal Zero no válido.";
            var vacio = p.Xp == 0 && p.Dinero == 0 && p.Zero == 0
                        && string.IsNullOrWhiteSpace(p.CartaId) && string.IsNullOrWhiteSpace(p.Externo);
            if (vacio) return $"El premio del puesto {p.Desde}º está vacío.";
        }
        return null;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // AUTH
    // ─────────────────────────────────────────────────────────────────────────

    private sealed record UsuarioVerificado(string Uid, string Email, IResult? Error);

    private static async Task<UsuarioVerificado> VerificarUsuarioAsync(HttpContext ctx, bool exigirEditor)
    {
        var header = ctx.Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return new("", "", Results.Unauthorized());
        var token = header["Bearer ".Length..].Trim();
        if (token.Length == 0) return new("", "", Results.Unauthorized());

        FirebaseToken dec;
        try { dec = await FirebaseAuth.DefaultInstance.VerifyIdTokenAsync(token, checkRevoked: true); }
        catch (FirebaseAuthException) { return new("", "", Results.Unauthorized()); }

        if (exigirEditor)
        {
            var esEditor = dec.Claims.TryGetValue("editor", out var v)
                           && string.Equals(v?.ToString(), "true", StringComparison.OrdinalIgnoreCase);
            if (!esEditor) return new(dec.Uid, "", Results.StatusCode(StatusCodes.Status403Forbidden));
        }

        var email = dec.Claims.TryGetValue("email", out var e) ? e?.ToString() ?? "" : "";
        return new(dec.Uid, email, null);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// DTOs de entrada
// ─────────────────────────────────────────────────────────────────────────────

public sealed class TorneoRequest
{
    public string? Id { get; set; }
    public string? Nombre { get; set; }
    public string? Descripcion { get; set; }
    public DateTime? InicioInscripcion { get; set; }
    public DateTime? FinInscripcion { get; set; }
    public DateTime? InicioTorneo { get; set; }
    public string? Estado { get; set; }
    public List<RondaTorneoRequest>? Rondas { get; set; }
    public List<PremioTorneoRequest>? Premios { get; set; }
    public string? ModoTurno { get; set; } = "diario";
    public int PlazoArranqueHoras { get; set; } = 24;
}

public sealed class PremioTorneoRequest
{
    public int Desde { get; set; } = 1;
    public int Hasta { get; set; } = 1;
    public int Xp { get; set; }
    public int Dinero { get; set; }
    public int Zero { get; set; }
    public string? ZeroTipo { get; set; } = "zeroPuro";
    public string? CartaId { get; set; }
    public int CartaCantidad { get; set; } = 1;
    public string? Externo { get; set; }
}

public sealed class FinalizarRequest
{
    public List<string>? Clasificacion { get; set; }
}

public sealed class ExternoRequest
{
    public bool Entregado { get; set; } = true;
}

public sealed class RondaTorneoRequest
{
    public int Clasifican { get; set; }
    public List<PartidaTorneoRequest>? Partidas { get; set; }
}

public sealed class PartidaTorneoRequest
{
    public string Tipo { get; set; } = "partida";
    public int Jugadores { get; set; } = 4;
    public string? Modo { get; set; } = "normal";
}

public sealed class SortearRequest
{
    public int Ronda { get; set; } = 1;
    public bool Forzar { get; set; }
}
