using Google.Cloud.Firestore;

// ─────────────────────────────────────────────────────────────────────────────
// WarZeroTrofeos.cs
//
// SISTEMA DE TROFEOS (coleccionables de perfil).
//
// Un trofeo es un logro que el jugador consigue A LO LARGO DE LAS PARTIDAS al
// alcanzar cierto valor en una MÉTRICA acumulada (victorias de combate, partidas
// ganadas, nivel, etc.). Los editores los definen en la colección Firestore
// `Trofeos`; el jugador acumula los conseguidos en Jugadores/{uid}.trofeosConseguidos
// (array de ids de trofeo).
//
// EVALUACIÓN: se hace en DOS momentos, ambos idempotentes (arrayUnion):
//   1) Tras RESOLVER un turno  → EvaluarTrasTurnoAsync (lo pide el requisito de
//      "añadirlo a resolver turno"). Best-effort, fuera de la transacción, como
//      WarZeroRecompensas: si falla no rompe el cierre de turno.
//   2) Al consultar el perfil   → WarZeroService.TrofeosAsync persiste de paso
//      cualquier trofeo ya cumplido pero aún no registrado (red de seguridad).
//
// Las métricas se leen del propio doc del jugador (ya se actualizan durante y al
// final de las partidas), así que NO hace falta tocar la transacción de
// resolución de turno.
//
// ── OPTIMIZACIÓN DE LECTURAS (catálogo cacheado) ────────────────────────────
// El catálogo de `Trofeos` (las DEFINICIONES) apenas cambia: solo cuando un
// editor toca EdicionTrofeosScreen. Sin embargo, antes se leía la colección
// ENTERA en CADA turno resuelto de CADA partida (y por partida de bots, que
// cierran turnos muy rápido, eso disparaba las lecturas de Firestore).
//
// Ahora las definiciones se sirven de una CACHÉ compartida y estática con TTL
// (mismo patrón que el catálogo de `Cartas` de WarZeroBot):
//   · Estática  → una sola copia para TODAS las partidas del proceso.
//   · TTL 5 min → los cambios del editor aparecen como muy tarde en 5 minutos.
//   · Dedup     → si varias partidas piden el catálogo caducado a la vez, solo
//                 se dispara UNA recarga; el resto reutiliza esa Task.
//   · Tolerante → si la recarga falla, se sigue sirviendo el catálogo anterior.
// Los datos del JUGADOR (que sí cambian a cada turno) se siguen leyendo frescos.
// ─────────────────────────────────────────────────────────────────────────────

public static class WarZeroTrofeos
{
    /// Nombre del campo (array de ids) donde el jugador acumula sus trofeos.
    public const string CampoConseguidos = "trofeosConseguidos";

    /// Claves de métrica admitidas y cómo se leen del doc del jugador. Debe
    /// mantenerse en sincronía con la lista del editor Flutter (edicion_trofeos_screen.dart).
    public static readonly IReadOnlyList<string> MetricasValidas = new[]
    {
        "victoriasCombate",
        "derrotasCombate",
        "partidasGanadas",
        "victoriasSinBots2",
        "victoriasSinBots4",
        "victoriasSinBots6",
        "victoriasSinBots8",
        "cuartelesConquistados",
        "nivel",
        "experiencia",
        "dinero",
    };

    /// Valor actual de una métrica para un jugador (a partir de su doc). Lee tanto
    /// la clave lowercase (nueva) como PascalCase (legado), por robustez.
    public static long MetricaValor(Dictionary<string, object?> jd, string metrica) => metrica switch
    {
        "victoriasCombate" => M.Long(M.Get(jd, "victorias", "Victorias")),
        "derrotasCombate" => M.Long(M.Get(jd, "derrotas", "Derrotas")),
        "partidasGanadas" => M.Long(M.Get(jd, "victorias2", "Victorias2"))
                           + M.Long(M.Get(jd, "victorias4", "Victorias4"))
                           + M.Long(M.Get(jd, "victorias6", "Victorias6"))
                           + M.Long(M.Get(jd, "victorias8", "Victorias8")),
        // Victorias "limpias" (partida SIN bots) por tamaño de sala. El
        // servidor las cuenta en WarZeroRecompensas al finalizar la partida.
        "victoriasSinBots2" => M.Long(M.Get(jd, "victoriasSinBots2", "VictoriasSinBots2")),
        "victoriasSinBots4" => M.Long(M.Get(jd, "victoriasSinBots4", "VictoriasSinBots4")),
        "victoriasSinBots6" => M.Long(M.Get(jd, "victoriasSinBots6", "VictoriasSinBots6")),
        "victoriasSinBots8" => M.Long(M.Get(jd, "victoriasSinBots8", "VictoriasSinBots8")),
        "cuartelesConquistados" => M.Long(M.Get(jd, "cuartelesConquistados", "CuartelesConquistados")),
        "nivel" => M.Long(M.Get(jd, "nivel", "Nivel")),
        "experiencia" => M.Long(M.Get(jd, "experiencia", "Experiencia")),
        "dinero" => M.Long(M.Get(jd, "dinero", "Dinero")),
        _ => 0,
    };

    /// ¿El jugador (por su doc `jd`) cumple la condición del trofeo `t`?
    public static bool Cumple(Dictionary<string, object?> t, Dictionary<string, object?> jd)
    {
        var metrica = M.Str(M.Get(t, "Metrica", "metrica"));
        if (string.IsNullOrEmpty(metrica)) return false;
        var objetivo = M.Long(M.Get(t, "Objetivo", "objetivo"));
        var op = M.Str(M.Get(t, "Operador", "operador"));
        var val = MetricaValor(jd, metrica);
        return op switch
        {
            "==" => val == objetivo,
            ">" => val > objetivo,
            "<" => val < objetivo,
            "<=" => val <= objetivo,
            _ => val >= objetivo, // ">=" por defecto
        };
    }

    /// ¿El trofeo está activo? Ausencia del campo = activo (para no exigir que el
    /// editor lo marque en trofeos antiguos).
    public static bool EstaActivo(Dictionary<string, object?> t)
    {
        var raw = M.Get(t, "Activo", "activo");
        return raw == null || M.Bool(raw);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // CACHÉ DEL CATÁLOGO DE TROFEOS (estático, compartido, con TTL)
    //
    // Guarda TODAS las definiciones de trofeo (activas e inactivas) ya parseadas,
    // para que cada llamante filtre lo que necesite en memoria sin releer la
    // colección. Mismo patrón que el catálogo de `Cartas` en WarZeroBot.
    // ─────────────────────────────────────────────────────────────────────────
    private static volatile List<(string id, Dictionary<string, object?> d)>? _catalogo;
    private static DateTime _catalogoCargado = DateTime.MinValue;
    private static readonly TimeSpan _catalogoTtl = TimeSpan.FromMinutes(5);
    private static Task<List<(string id, Dictionary<string, object?> d)>>? _catalogoCargando;
    private static readonly object _catGate = new();

    /// Fuerza que la próxima consulta recargue el catálogo desde Firestore. Útil
    /// si en el futuro el editor de trofeos avisa al backend tras guardar (para no
    /// esperar al TTL). Hoy no es necesario llamarlo: el TTL lo cubre.
    public static void InvalidarCatalogo() => _catalogoCargado = DateTime.MinValue;

    /// TODAS las definiciones de trofeo (activas + inactivas), servidas de caché.
    /// Si está fresca (< TTL) NO toca Firestore. Si caducó, dispara UNA recarga
    /// compartida; si esa recarga falla, sigue sirviendo la copia anterior.
    public static async Task<List<(string id, Dictionary<string, object?> d)>>
        ObtenerTodosAsync(FirestoreDb db)
    {
        var cache = _catalogo;
        if (cache != null && (DateTime.UtcNow - _catalogoCargado) < _catalogoTtl)
            return cache;

        Task<List<(string id, Dictionary<string, object?> d)>> carga;
        lock (_catGate)
        {
            if (_catalogo != null && (DateTime.UtcNow - _catalogoCargado) < _catalogoTtl)
                return _catalogo;
            // Reutiliza una recarga en curso para no lanzar N lecturas simultáneas.
            _catalogoCargando ??= CargarTodosAsync(db);
            carga = _catalogoCargando;
        }

        try { return await carga; }
        catch
        {
            // Recarga fallida: si teníamos catálogo previo, seguimos con él.
            return _catalogo ?? new List<(string, Dictionary<string, object?>)>();
        }
    }

    private static async Task<List<(string id, Dictionary<string, object?> d)>>
        CargarTodosAsync(FirestoreDb db)
    {
        try
        {
            var snap = await db.Collection("Trofeos").GetSnapshotAsync();
            var lista = new List<(string, Dictionary<string, object?>)>();
            foreach (var doc in snap.Documents)
            {
                var d = M.Map(M.ToJsonSafe(doc.ToDictionary()));
                lista.Add((doc.Id, d));
            }
            _catalogo = lista;
            _catalogoCargado = DateTime.UtcNow;
            return lista;
        }
        finally
        {
            lock (_catGate) { _catalogoCargando = null; }
        }
    }

    /// Solo las definiciones ACTIVAS (derivado en memoria de la caché).
    public static async Task<List<(string id, Dictionary<string, object?> d)>>
        ObtenerActivosAsync(FirestoreDb db)
    {
        var todos = await ObtenerTodosAsync(db);
        return todos.Where(x => EstaActivo(x.d)).ToList();
    }

    /// Carga el catálogo de trofeos ACTIVOS como mapa id → (icono, nombre). Ahora
    /// se sirve de la caché (antes: una lectura de la colección por cada llamada).
    /// Se reutiliza para resolver el trofeo destacado de varios jugadores (ranking,
    /// sala de espera).
    public static async Task<Dictionary<string, (string icono, string nombre)>>
        CargarCatalogoActivoAsync(FirestoreDb db)
    {
        var map = new Dictionary<string, (string icono, string nombre)>();
        foreach (var (id, d) in await ObtenerActivosAsync(db))
        {
            map[id] = (
                M.Str(M.Get(d, "Icono", "icono")),
                M.Str(M.Get(d, "Nombre", "nombre")));
        }
        return map;
    }

    /// Resuelve el trofeo DESTACADO (icono + nombre) de un jugador a partir de su
    /// doc y del catálogo activo. Devuelve ("","","") si no lo tiene, si el trofeo
    /// ya no está activo, o si el jugador aún no lo ha conseguido.
    public static (string id, string icono, string nombre) ResolverDestacado(
        Dictionary<string, object?> jd,
        Dictionary<string, (string icono, string nombre)> catalogo)
    {
        var destId = M.Str(M.Get(jd, "trofeoDestacado", "TrofeoDestacado"));
        if (string.IsNullOrEmpty(destId)) return ("", "", "");
        if (!catalogo.TryGetValue(destId, out var info)) return ("", "", "");
        var conseguidos = M.List(M.Get(jd, CampoConseguidos)).Select(M.Str);
        if (!conseguidos.Contains(destId)) return ("", "", "");
        return (destId, info.icono, info.nombre);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // EVALUACIÓN TRAS RESOLVER UN TURNO
    // ─────────────────────────────────────────────────────────────────────────

    /// Comprueba, para todos los jugadores de la partida, si han conseguido algún
    /// trofeo nuevo tras resolverse el turno, y lo registra en su perfil
    /// (arrayUnion, idempotente). Best-effort: nunca lanza. Se llama SIEMPRE tras
    /// resolver un turno (cierre normal o resolución forzosa por fecha límite).
    ///
    /// `estadoPre`: estado de la partida YA leído por el llamante (p. ej.
    /// `resp.Estado` de CerrarTurnoAsync). Si se pasa, NO se relee el doc de la
    /// partida (ahorra una lectura por turno). Si es null, se lee como antes.
    public static async Task EvaluarTrasTurnoAsync(
        FirestoreDb db, string lobbyId, Dictionary<string, object?>? estadoPre = null)
    {
        if (db == null || string.IsNullOrWhiteSpace(lobbyId)) return;
        try
        {
            // 1) Catálogo de trofeos activos (de caché: sin lectura en el caso común).
            var activos = await ObtenerActivosAsync(db);
            if (activos.Count == 0) return;

            // 2) Jugadores de la partida. Se reutiliza el estado que el llamante ya
            //    tiene en memoria si lo pasa; si no, se lee el doc de la partida.
            Dictionary<string, object?> data;
            if (estadoPre != null)
            {
                data = estadoPre;
            }
            else
            {
                var lobby = await db.Collection("Partidas").Document(lobbyId).GetSnapshotAsync();
                if (!lobby.Exists) return;
                data = M.Map(M.FromFs(lobby.ToDictionary()));
            }

            // Se evalúa a TODOS (incluidos los recién eliminados: pueden haber
            // logrado su última victoria/partida).
            var uids = M.List(M.Get(data, "jugadores"))
                .Select(j => M.Str(M.Get(M.Map(j), "uid")))
                .Where(u => !string.IsNullOrEmpty(u))
                .Distinct()
                .ToList();
            if (uids.Count == 0) return;

            // 3) Por jugador: leer su doc, calcular los trofeos recién cumplidos y
            //    registrarlos. Se hace en paralelo; cada uno es independiente.
            var tareas = uids.Select(uid => OtorgarNuevosAsync(db, uid, activos));
            await Task.WhenAll(tareas);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("[WarZero] EvaluarTrofeosTrasTurno falló lobby=" + lobbyId + ": " + ex);
        }
    }

    /// Registra en el perfil de `uid` los trofeos de `catalogo` que ya cumple y
    /// aún no tenía. Devuelve los ids recién otorgados. Best-effort.
    public static async Task<List<string>> OtorgarNuevosAsync(
        FirestoreDb db, string uid,
        IReadOnlyList<(string id, Dictionary<string, object?> d)> catalogo)
    {
        var otorgados = new List<string>();
        if (db == null || string.IsNullOrWhiteSpace(uid) || catalogo.Count == 0) return otorgados;

        var jugRef = db.Collection("Jugadores").Document(uid);
        var snap = await jugRef.GetSnapshotAsync();
        if (!snap.Exists) return otorgados;

        var jd = M.Map(M.ToJsonSafe(snap.ToDictionary()));
        var yaTiene = M.List(M.Get(jd, CampoConseguidos)).Select(M.Str)
            .Where(s => !string.IsNullOrEmpty(s)).ToHashSet();

        foreach (var (id, t) in catalogo)
        {
            if (yaTiene.Contains(id)) continue;
            if (Cumple(t, jd)) otorgados.Add(id);
        }

        if (otorgados.Count > 0)
        {
            await jugRef.SetAsync(new Dictionary<string, object>
            {
                [CampoConseguidos] = FieldValue.ArrayUnion(otorgados.Cast<object>().ToArray()),
            }, SetOptions.MergeAll);
        }
        return otorgados;
    }
}

public partial class WarZeroService
{
    /// Trofeos del jugador para el perfil y la pantalla de trofeos: catálogo de
    /// trofeos ACTIVOS + estado de conseguido de `uid` + porcentaje. De paso,
    /// registra (arrayUnion) cualquier trofeo ya cumplido pero aún no guardado,
    /// como red de seguridad por si la evaluación de resolver-turno se perdió.
    /// Usado por GET /warzero/trofeos.
    ///
    /// El catálogo de trofeos se sirve de la caché compartida (sin lectura de la
    /// colección en el caso común); solo se lee fresco el doc del JUGADOR.
    public async Task<Dictionary<string, object?>> TrofeosAsync(string uid)
    {
        var db = _fs.Db;

        // Doc del jugador (fresco) + catálogo activo (cacheado) en paralelo.
        var jugadorTask = db.Collection("Jugadores").Document(uid).GetSnapshotAsync();
        var activos = await WarZeroTrofeos.ObtenerActivosAsync(db);
        var jugadorSnap = await jugadorTask;

        var jd = jugadorSnap.Exists
            ? M.Map(M.ToJsonSafe(jugadorSnap.ToDictionary()))
            : new Dictionary<string, object?>();

        var conseguidos = M.List(M.Get(jd, WarZeroTrofeos.CampoConseguidos))
            .Select(M.Str).Where(s => !string.IsNullOrEmpty(s)).ToHashSet();

        // Red de seguridad: otorgar los cumplidos-no-registrados (persistencia
        // best-effort; no bloquea la respuesta si falla).
        var nuevos = new List<string>();
        foreach (var (id, t) in activos)
        {
            if (conseguidos.Contains(id)) continue;
            if (WarZeroTrofeos.Cumple(t, jd)) nuevos.Add(id);
        }
        if (nuevos.Count > 0)
        {
            foreach (var n in nuevos) conseguidos.Add(n);
            try
            {
                await db.Collection("Jugadores").Document(uid).SetAsync(new Dictionary<string, object>
                {
                    [WarZeroTrofeos.CampoConseguidos] = FieldValue.ArrayUnion(nuevos.Cast<object>().ToArray()),
                }, SetOptions.MergeAll);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("[WarZero] TrofeosAsync persistir nuevos falló uid=" + uid + ": " + ex);
            }
        }

        // Lista ordenada para la UI.
        var lista = activos
            .Select(x =>
            {
                var d = x.d;
                var conseguido = conseguidos.Contains(x.id);
                return new Dictionary<string, object?>
                {
                    ["id"] = x.id,
                    ["nombre"] = M.Str(M.Get(d, "Nombre", "nombre")),
                    ["descripcion"] = M.Str(M.Get(d, "Descripcion", "descripcion")),
                    ["icono"] = M.Str(M.Get(d, "Icono", "icono")),
                    ["metrica"] = M.Str(M.Get(d, "Metrica", "metrica")),
                    ["operador"] = M.Str(M.Get(d, "Operador", "operador")),
                    ["objetivo"] = M.Long(M.Get(d, "Objetivo", "objetivo")),
                    ["orden"] = M.Int(M.Get(d, "Orden", "orden")),
                    ["conseguido"] = conseguido,
                };
            })
            .OrderBy(m => M.Int(m["orden"]))
            .ThenBy(m => M.Str(m["nombre"]))
            .ToList();

        var total = lista.Count;
        var logrados = lista.Count(m => M.Bool(m["conseguido"]));
        var porcentaje = total == 0 ? 0 : (int)Math.Round(100.0 * logrados / total);

        // Trofeo DESTACADO que el jugador ha elegido mostrar junto a su alias.
        // Solo es válido si sigue activo y lo tiene conseguido; si no, se ignora
        // (devolvemos "") para no mostrar un trofeo borrado o no logrado.
        var destacadoRaw = M.Str(M.Get(jd, "trofeoDestacado", "TrofeoDestacado"));
        var destacado = "";
        if (!string.IsNullOrEmpty(destacadoRaw)
            && conseguidos.Contains(destacadoRaw)
            && activos.Any(x => x.id == destacadoRaw))
        {
            destacado = destacadoRaw;
        }

        return new Dictionary<string, object?>
        {
            ["trofeos"] = lista,
            ["total"] = total,
            ["conseguidos"] = logrados,
            ["porcentaje"] = porcentaje,
            ["destacado"] = destacado,
        };
    }

    /// Trofeo destacado (icono + nombre) de VARIOS jugadores a la vez. Para el
    /// ranking (embebido en RankingAsync) y la sala de espera, donde hay que
    /// resolver el destacado de todos los participantes sin una petición por uid.
    /// Devuelve un mapa uid → { id, icono, nombre } solo para los que tengan un
    /// destacado válido (activo y conseguido). El catálogo va de caché; se leen N
    /// docs de jugador.
    public async Task<Dictionary<string, object?>> TrofeosDestacadosAsync(List<string> uids)
    {
        var res = new Dictionary<string, object?>();
        if (uids == null || uids.Count == 0) return res;

        var db = _fs.Db;
        var catalogo = await WarZeroTrofeos.CargarCatalogoActivoAsync(db);
        if (catalogo.Count == 0) return res;

        var unicos = uids.Where(u => !string.IsNullOrEmpty(u)).Distinct().ToList();
        var tareas = unicos
            .Select(u => (uid: u, task: db.Collection("Jugadores").Document(u).GetSnapshotAsync()))
            .ToList();
        await Task.WhenAll(tareas.Select(t => t.task));

        foreach (var (uid, task) in tareas)
        {
            var snap = task.Result;
            if (!snap.Exists) continue;
            var jd = M.Map(M.ToJsonSafe(snap.ToDictionary()));
            var (id, icono, nombre) = WarZeroTrofeos.ResolverDestacado(jd, catalogo);
            if (id == "") continue;
            res[uid] = new Dictionary<string, object?>
            {
                ["id"] = id,
                ["icono"] = icono,
                ["nombre"] = nombre,
            };
        }
        return res;
    }
}