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
//
// ── ORIGEN DEL TROFEO (cómo se consigue) ────────────────────────────────────
// Cada trofeo declara en su documento `Origen` + `OrigenId`:
//   · "metrica"  (o campo ausente, trofeos antiguos) → Metrica/Operador/Objetivo.
//   · "reto"     → se consigue al GANAR el reto `OrigenId` (id de RetoCatalogo).
//   · "historia" → se consigue al COMPLETAR la historia `OrigenId` del MODO
//                  HISTORIA (id de su parte 1 en HistoriaCatalogo, p. ej.
//                  "demonios_1"), es decir, al ganar su última parte.
//                  NO es el id de un documento de la colección `Historias`:
//                  esa colección es el lore (historias del juego para leer) y
//                  se puede abrir por otros caminos (PorDefecto, desbloqueo
//                  manual), así que no sirve para saber quién ganó la campaña.
// `OrigenNombre` es solo una etiqueta legible que escribe el editor.
//
// Los de reto/historia se evalúan contra lo que el jugador YA ha completado,
// guardado en su propio doc:
//   · Jugadores/{uid}.retosCompletados       (lo escribe WarZeroRetos al ganar)
//   · Jugadores/{uid}.modoHistoriaCompletada (lo escribe
//     WarZeroService.CompletarModoHistoriaAsync al ganar la última parte)
// Por eso `Cumple` sirve para TODOS los orígenes y el reparto es RETROACTIVO
// sin código extra: si un editor asigna hoy un trofeo a una historia, quien ya
// la tenía completada lo recibe en la siguiente evaluación (turno resuelto o
// consulta del perfil), con su pop-up.
//
// Además, al completar un reto/historia se otorgan en el acto
// (`OtorgarPorOrigenAsync`) para que el pop-up salga en la misma respuesta.
// ─────────────────────────────────────────────────────────────────────────────

public static class WarZeroTrofeos
{
    /// Nombre del campo (array de ids) donde el jugador acumula sus trofeos.
    public const string CampoConseguidos = "trofeosConseguidos";

    /// Campo (array de ids) con los trofeos conseguidos que AÚN NO se le han
    /// mostrado al jugador: es la cola del pop-up "¡Trofeo conseguido!".
    ///
    /// Se escribe SIEMPRE en el mismo `SetAsync` que `CampoConseguidos`, así que
    /// no puede quedar un trofeo otorgado sin su aviso (ni un aviso sin trofeo)
    /// si el proceso se cae entre dos escrituras. Lo drena
    /// `DrenarPendientesAsync`, que es quien decide que el jugador ya lo ha
    /// visto.
    public const string CampoPendientes = "trofeosPendientesAviso";

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

    // ── Origen del trofeo ────────────────────────────────────────────────────

    public const string OrigenMetrica = "metrica";
    public const string OrigenReto = "reto";
    public const string OrigenHistoria = "historia";

    /// Campo (array de ids de reto) con los retos que el jugador ha GANADO.
    public const string CampoRetosCompletados = "retosCompletados";

    /// Campo (array de ids de HistoriaCatalogo, el de la parte 1 de cada
    /// historia) con las historias del MODO HISTORIA que el jugador ha
    /// completado (ganada su última parte). Es lo que miran los trofeos de
    /// origen "historia".
    public const string CampoModoHistoriaCompletada = "modoHistoriaCompletada";

    /// Campo (array de ids de doc de `Historias`) con las historias de LORE que
    /// el jugador tiene abiertas para leer. NO cuenta para los trofeos.
    public const string CampoHistoriasDesbloqueadas = "historiasDesbloqueadas";

    /// Origen del trofeo. Ausente, vacío o desconocido = métrica (legado).
    public static string Origen(Dictionary<string, object?> t)
    {
        var o = M.Str(M.Get(t, "Origen", "origen")).Trim().ToLowerInvariant();
        return o == OrigenReto || o == OrigenHistoria ? o : OrigenMetrica;
    }

    /// Id del reto o de la historia (vacío en los de métrica).
    public static string OrigenId(Dictionary<string, object?> t)
        => M.Str(M.Get(t, "OrigenId", "origenId")).Trim();

    /// ¿El trofeo `t` se consigue completando ese reto/historia concreto?
    public static bool EsDeOrigen(Dictionary<string, object?> t, string origen, string origenId)
        => !string.IsNullOrWhiteSpace(origenId)
           && Origen(t) == origen
           && OrigenId(t) == origenId;

    /// Ids que el jugador tiene en un campo array de su doc (sin vacíos).
    private static HashSet<string> IdsDe(Dictionary<string, object?> jd, string campo)
        => M.List(M.Get(jd, campo)).Select(M.Str)
            .Where(s => !string.IsNullOrEmpty(s)).ToHashSet();

    // ── Migración de las historias completadas ANTES de `modoHistoriaCompletada` ──
    //
    // Antes de existir ese campo, completar el modo historia solo dejaba rastro
    // en `historiasDesbloqueadas` (el lore que se abre al ganar la última parte).
    // Para que esos jugadores no pierdan los trofeos de historia, la PRIMERA vez
    // que se consultan sus trofeos se traduce cada lore desbloqueado a su
    // historia del modo historia (mismo Ejercito + Orden) y se apunta en
    // `modoHistoriaCompletada`. Se marca con `CampoModoHistoriaMigrado` para no
    // volver a leer la colección `Historias` nunca más para ese jugador.
    //
    // Es fiable porque `historiasDesbloqueadas` solo se escribe al ganar la
    // última parte (o por el endpoint manual de desbloqueo, que la app no usa);
    // las historias `PorDefecto` no se guardan en el jugador.

    /// Marca de que la migración anterior ya se hizo para el jugador.
    public const string CampoModoHistoriaMigrado = "modoHistoriaMigrado";

    /// Si hace falta, migra las historias completadas de `uid` (doc `jd`) al
    /// campo `modoHistoriaCompletada`. Actualiza `jd` EN MEMORIA para que la
    /// evaluación que venga a continuación ya las tenga en cuenta. Best-effort:
    /// nunca lanza; si falla, se reintenta en la siguiente consulta.
    public static async Task MigrarModoHistoriaSiProcedeAsync(
        FirestoreDb db, string uid, Dictionary<string, object?> jd)
    {
        if (db == null || string.IsNullOrWhiteSpace(uid)) return;
        if (M.Bool(M.Get(jd, CampoModoHistoriaMigrado))) return;
        try
        {
            var lore = IdsDe(jd, CampoHistoriasDesbloqueadas);
            var completadas = IdsDe(jd, CampoModoHistoriaCompletada);
            var nuevas = new HashSet<string>();

            if (lore.Count > 0)
            {
                var snap = await db.Collection("Historias").GetSnapshotAsync();
                var porDoc = new Dictionary<string, string>();
                foreach (var doc in snap.Documents)
                {
                    var d = M.Map(M.ToJsonSafe(doc.ToDictionary()));
                    var campana = HistoriaCatalogo.CampanaIdDe(
                        M.Int(M.Get(d, "Ejercito", "ejercito")),
                        M.Int(M.Get(d, "Orden", "orden")));
                    if (campana != "") porDoc[doc.Id] = campana;
                }

                foreach (var id in lore)
                {
                    // Lore del editor → su historia jugable. Si no había doc de
                    // lore, el servidor apuntaba el id de la batalla (p. ej.
                    // "humanos_3"): se resuelve con el catálogo.
                    var campana = porDoc.TryGetValue(id, out var c)
                        ? c
                        : HistoriaCatalogo.Get(id)?.CampanaId ?? "";
                    if (campana != "" && !completadas.Contains(campana)) nuevas.Add(campana);
                }
            }

            var datos = new Dictionary<string, object>
            {
                [CampoModoHistoriaMigrado] = true,
            };
            if (nuevas.Count > 0)
                datos[CampoModoHistoriaCompletada] =
                    FieldValue.ArrayUnion(nuevas.Cast<object>().ToArray());

            await db.Collection("Jugadores").Document(uid).SetAsync(datos, SetOptions.MergeAll);

            // Reflejo en memoria para la evaluación de esta misma petición.
            jd[CampoModoHistoriaMigrado] = true;
            if (nuevas.Count > 0)
            {
                jd[CampoModoHistoriaCompletada] =
                    completadas.Concat(nuevas).Cast<object?>().ToList();
                Console.WriteLine(
                    "[WarZero] modo historia migrado uid=" + uid + " → " +
                    string.Join(",", nuevas));
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("[WarZero] MigrarModoHistoria falló uid=" + uid + ": " + ex);
        }
    }

    /// ¿El jugador (por su doc `jd`) cumple la condición del trofeo `t`?
    ///
    /// Reto/historia: la tiene si ese reto/historia figura entre los
    /// completados del jugador (la métrica se ignora aunque el doc la conserve
    /// de cuando el trofeo era de métrica). Métrica: comparación de siempre.
    public static bool Cumple(Dictionary<string, object?> t, Dictionary<string, object?> jd)
    {
        switch (Origen(t))
        {
            case OrigenReto:
                {
                    var id = OrigenId(t);
                    return id != "" && IdsDe(jd, CampoRetosCompletados).Contains(id);
                }
            case OrigenHistoria:
                {
                    var id = OrigenId(t);
                    return id != "" && IdsDe(jd, CampoModoHistoriaCompletada).Contains(id);
                }
        }

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
    /// Devuelve uid → ids recién otorgados (solo los jugadores con alguno). Los
    /// avisos quedan además en la cola `CampoPendientes` de cada jugador, que es
    /// de donde los saca el pop-up.
    ///
    /// `estadoPre`: estado de la partida YA leído por el llamante (p. ej.
    /// `resp.Estado` de CerrarTurnoAsync). Si se pasa, NO se relee el doc de la
    /// partida (ahorra una lectura por turno). Si es null, se lee como antes.
    public static async Task<Dictionary<string, List<string>>> EvaluarTrasTurnoAsync(
        FirestoreDb db, string lobbyId, Dictionary<string, object?>? estadoPre = null)
    {
        var resultado = new Dictionary<string, List<string>>();
        if (db == null || string.IsNullOrWhiteSpace(lobbyId)) return resultado;
        try
        {
            // 1) Catálogo de trofeos activos (de caché: sin lectura en el caso común).
            var activos = await ObtenerActivosAsync(db);
            if (activos.Count == 0) return resultado;

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
                if (!lobby.Exists) return resultado;
                data = M.Map(M.FromFs(lobby.ToDictionary()));
            }

            // Se evalúa a TODOS (incluidos los recién eliminados: pueden haber
            // logrado su última victoria/partida).
            var uids = M.List(M.Get(data, "jugadores"))
                .Select(j => M.Str(M.Get(M.Map(j), "uid")))
                .Where(u => !string.IsNullOrEmpty(u))
                .Distinct()
                .ToList();
            if (uids.Count == 0) return resultado;

            // 3) Por jugador: leer su doc, calcular los trofeos recién cumplidos y
            //    registrarlos. Se hace en paralelo; cada uno es independiente.
            var tareas = uids
                .Select(uid => (uid, task: OtorgarNuevosAsync(db, uid, activos)))
                .ToList();
            await Task.WhenAll(tareas.Select(t => t.task));

            foreach (var (uid, task) in tareas)
            {
                var nuevos = task.Result;
                if (nuevos.Count == 0) continue;
                resultado[uid] = nuevos;
                Console.WriteLine(
                    "[WarZero] trofeos nuevos uid=" + uid +
                    " lobby=" + lobbyId + " → " + string.Join(",", nuevos));
            }
            return resultado;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("[WarZero] EvaluarTrofeosTrasTurno falló lobby=" + lobbyId + ": " + ex);
            return resultado;
        }
    }
    /// Otorga un trofeo CONCRETO a `uid` sin evaluar ninguna condición.
    ///
    /// Hoy solo lo usa el sistema ANTERIOR de asignación (el `TrofeoId` escrito
    /// en el doc de la historia o en `RetoDef`), que se mantiene por
    /// compatibilidad. Lo nuevo es declarar el origen en el propio trofeo
    /// (`Origen`/`OrigenId`) y otorgarlo con `OtorgarPorOrigenAsync`.
    ///
    /// Idempotente: si el jugador ya lo tenía no escribe y devuelve false, así el
    /// llamante sabe si ha habido algo nuevo. Best-effort: nunca lanza.
    public static async Task<bool> OtorgarManualAsync(
        FirestoreDb db, string uid, string trofeoId)
    {
        if (db == null || string.IsNullOrWhiteSpace(uid) || string.IsNullOrWhiteSpace(trofeoId))
            return false;
        try
        {
            var jugRef = db.Collection("Jugadores").Document(uid);
            var snap = await jugRef.GetSnapshotAsync();
            if (!snap.Exists) return false;

            var jd = M.Map(M.ToJsonSafe(snap.ToDictionary()));
            var yaTiene = M.List(M.Get(jd, CampoConseguidos)).Select(M.Str).ToHashSet();
            if (yaTiene.Contains(trofeoId)) return false;

            // El trofeo debe EXISTIR y estar activo. Si un editor lo borró o lo
            // desactivó no se otorga: si no, quedaría un id fantasma en el perfil
            // que ninguna pantalla sabría pintar (y el pop-up saldría vacío).
            var todos = await ObtenerTodosAsync(db);
            var def = todos.FirstOrDefault(x => x.id == trofeoId);
            if (def.id == null)
            {
                Console.Error.WriteLine(
                    "[WarZero] OtorgarManual: el trofeo '" + trofeoId +
                    "' no existe en el catálogo (uid=" + uid + ")");
                return false;
            }
            if (!EstaActivo(def.d))
            {
                Console.WriteLine(
                    "[WarZero] OtorgarManual: el trofeo '" + trofeoId +
                    "' está desactivado; no se otorga (uid=" + uid + ")");
                return false;
            }

            await jugRef.SetAsync(new Dictionary<string, object>
            {
                [CampoConseguidos] = FieldValue.ArrayUnion(trofeoId),
                [CampoPendientes] = FieldValue.ArrayUnion(trofeoId),
            }, SetOptions.MergeAll);

            Console.WriteLine(
                "[WarZero] trofeo '" + trofeoId + "' otorgado a mano a uid=" + uid);
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                "[WarZero] OtorgarManual falló uid=" + uid +
                " trofeo=" + trofeoId + ": " + ex);
            return false;
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

        // Historias completadas antes de existir `modoHistoriaCompletada`
        // (una sola vez por jugador, y solo si hay trofeos de historia en juego).
        if (catalogo.Any(x => Origen(x.d) == OrigenHistoria && !yaTiene.Contains(x.id)))
            await MigrarModoHistoriaSiProcedeAsync(db, uid, jd);

        foreach (var (id, t) in catalogo)
        {
            if (yaTiene.Contains(id)) continue;
            if (Cumple(t, jd)) otorgados.Add(id);
        }

        if (otorgados.Count > 0)
        {
            var arr = otorgados.Cast<object>().ToArray();
            await jugRef.SetAsync(new Dictionary<string, object>
            {
                [CampoConseguidos] = FieldValue.ArrayUnion(arr),
                // Cola del pop-up, en el MISMO write que el trofeo.
                [CampoPendientes] = FieldValue.ArrayUnion(arr),
            }, SetOptions.MergeAll);
        }
        return otorgados;
    }

    /// Ids de los trofeos ACTIVOS que se consiguen completando ese reto o esa
    /// historia (`origen` = OrigenReto | OrigenHistoria). De caché: sin lecturas.
    public static async Task<List<string>> IdsPorOrigenAsync(
        FirestoreDb db, string origen, string origenId)
    {
        if (db == null || string.IsNullOrWhiteSpace(origenId)) return new List<string>();
        var activos = await ObtenerActivosAsync(db);
        return activos
            .Where(x => EsDeOrigen(x.d, origen, origenId))
            .Select(x => x.id)
            .ToList();
    }

    /// Otorga a `uid` los trofeos activos asociados a ese reto/historia.
    ///
    /// PRECONDICIÓN: el llamante ya ha registrado el reto/historia como
    /// completado en el doc del jugador (`retosCompletados` /
    /// `modoHistoriaCompletada`). Aquí se reevalúa con `Cumple`, así que si ese
    /// registro faltara no se otorga nada (nunca se regala un trofeo).
    ///
    /// Una lectura del jugador y, si hay algo nuevo, una escritura que añade el
    /// trofeo y su aviso de pop-up. Idempotente. Best-effort: nunca lanza.
    public static async Task<List<string>> OtorgarPorOrigenAsync(
        FirestoreDb db, string uid, string origen, string origenId)
    {
        if (db == null || string.IsNullOrWhiteSpace(uid) || string.IsNullOrWhiteSpace(origenId))
            return new List<string>();
        try
        {
            var activos = await ObtenerActivosAsync(db);
            var delOrigen = activos.Where(x => EsDeOrigen(x.d, origen, origenId)).ToList();
            if (delOrigen.Count == 0) return new List<string>();

            var nuevos = await OtorgarNuevosAsync(db, uid, delOrigen);
            if (nuevos.Count > 0)
                Console.WriteLine(
                    "[WarZero] " + origen + " '" + origenId + "' completado por " + uid +
                    " → trofeos " + string.Join(",", nuevos));
            return nuevos;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                "[WarZero] OtorgarPorOrigen falló uid=" + uid +
                " " + origen + "=" + origenId + ": " + ex);
            return new List<string>();
        }
    }

    /// Datos de presentación (id, nombre, descripción, icono) de una lista de
    /// ids, servidos de la caché del catálogo. Es lo que necesita el pop-up: con
    /// el id a secas no se puede pintar nada.
    ///
    /// Respeta el orden de `ids` y descarta los que ya no existan en el catálogo.
    public static async Task<List<Dictionary<string, object?>>> InfoDeAsync(
        FirestoreDb db, IEnumerable<string>? ids)
    {
        var res = new List<Dictionary<string, object?>>();
        var lista = ids?.Where(s => !string.IsNullOrEmpty(s)).Distinct().ToList();
        if (db == null || lista == null || lista.Count == 0) return res;

        var todos = await ObtenerTodosAsync(db);
        var porId = new Dictionary<string, Dictionary<string, object?>>();
        foreach (var (id, d) in todos) porId[id] = d;

        foreach (var id in lista)
        {
            if (!porId.TryGetValue(id, out var d)) continue;
            var icono = M.Str(M.Get(d, "Icono", "icono"));
            res.Add(new Dictionary<string, object?>
            {
                ["id"] = id,
                ["nombre"] = M.Str(M.Get(d, "Nombre", "nombre")),
                ["descripcion"] = M.Str(M.Get(d, "Descripcion", "descripcion")),
                ["icono"] = string.IsNullOrWhiteSpace(icono) ? "🏆" : icono,
                // El pop-up solo muestra trofeos YA conseguidos.
                ["conseguido"] = true,
            });
        }
        return res;
    }
    /// Devuelve los avisos de trofeo pendientes de `uid` (con nombre e icono) y
    /// los saca de la cola, para que el pop-up no se repita.
    ///
    /// Detalles que importan:
    ///   · Se resuelve la info ANTES de limpiar: si la lectura del catálogo
    ///     fallara, la cola se queda intacta y se reintenta la próxima vez.
    ///   · Se borran EXACTAMENTE los ids leídos (arrayRemove), no el campo
    ///     entero: si el jugador gana otro trofeo mientras esta petición está en
    ///     vuelo, ese aviso nuevo sobrevive y saldrá después.
    ///   · Se limpian también los ids HUÉRFANOS (trofeo borrado o desactivado
    ///     por un editor). Si no, se quedarían atascados reintentándose en cada
    ///     petición para siempre.
    ///
    /// Best-effort: nunca lanza; ante un fallo devuelve lista vacía.
    public static async Task<List<Dictionary<string, object?>>> DrenarPendientesAsync(
        FirestoreDb db, string uid)
    {
        var vacio = new List<Dictionary<string, object?>>();
        if (db == null || string.IsNullOrWhiteSpace(uid)) return vacio;

        try
        {
            var jugRef = db.Collection("Jugadores").Document(uid);
            var snap = await jugRef.GetSnapshotAsync();
            if (!snap.Exists) return vacio;

            var jd = M.Map(M.ToJsonSafe(snap.ToDictionary()));
            var pendientes = M.List(M.Get(jd, CampoPendientes)).Select(M.Str)
                .Where(s => !string.IsNullOrEmpty(s)).Distinct().ToList();
            if (pendientes.Count == 0) return vacio;

            // Solo se avisa de lo que el jugador REALMENTE tiene conseguido.
            var conseguidos = M.List(M.Get(jd, CampoConseguidos)).Select(M.Str).ToHashSet();
            var avisar = pendientes.Where(conseguidos.Contains).ToList();

            // Info primero, limpieza después (ver nota del doc).
            var info = await InfoDeAsync(db, avisar);

            try
            {
                await jugRef.SetAsync(new Dictionary<string, object>
                {
                    [CampoPendientes] =
                        FieldValue.ArrayRemove(pendientes.Cast<object>().ToArray()),
                }, SetOptions.MergeAll);
            }
            catch (Exception ex)
            {
                // Si la limpieza falla, el pop-up podría repetirse una vez. Es
                // preferible a perder el aviso, así que no se propaga.
                Console.Error.WriteLine(
                    "[WarZero] DrenarPendientes: limpiar la cola falló uid=" + uid + ": " + ex);
            }

            return info;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("[WarZero] DrenarPendientes falló uid=" + uid + ": " + ex);
            return vacio;
        }
    }
}

public partial class WarZeroService
{
    /// Trofeos del jugador para el perfil y la pantalla de trofeos: catálogo de
    /// trofeos ACTIVOS + estado de conseguido de `uid` + porcentaje. De paso,
    /// registra (arrayUnion) cualquier trofeo ya cumplido pero aún no guardado,
    /// como red de seguridad por si la evaluación de resolver-turno se perdió.
    /// Esa misma red es la que reparte de forma RETROACTIVA los trofeos de
    /// reto/historia asignados después de que el jugador los completara.
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

        // Historias del modo historia completadas antes de existir
        // `modoHistoriaCompletada`: se migran una sola vez por jugador, antes
        // de evaluar, para que reciban ya aquí sus trofeos de historia.
        if (jugadorSnap.Exists
            && activos.Any(x => WarZeroTrofeos.Origen(x.d) == WarZeroTrofeos.OrigenHistoria
                                && !conseguidos.Contains(x.id)))
        {
            await WarZeroTrofeos.MigrarModoHistoriaSiProcedeAsync(db, uid, jd);
        }

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
                var arrNuevos = nuevos.Cast<object>().ToArray();
                await db.Collection("Jugadores").Document(uid).SetAsync(new Dictionary<string, object>
                {
                    [WarZeroTrofeos.CampoConseguidos] = FieldValue.ArrayUnion(arrNuevos),
                    // La red de seguridad también encola el aviso: si el trofeo
                    // se detecta aquí (porque la evaluación de resolver-turno se
                    // perdió), el pop-up debe salir igual la próxima vez que el
                    // cliente drene la cola.
                    [WarZeroTrofeos.CampoPendientes] = FieldValue.ArrayUnion(arrNuevos),
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
                    // Cómo se consigue: "metrica" | "reto" | "historia". Con
                    // `origenNombre` el cliente pinta "Completa el reto X".
                    ["origen"] = WarZeroTrofeos.Origen(d),
                    ["origenId"] = WarZeroTrofeos.OrigenId(d),
                    ["origenNombre"] = M.Str(M.Get(d, "OrigenNombre", "origenNombre")),
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