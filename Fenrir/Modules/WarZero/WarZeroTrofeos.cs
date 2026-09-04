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
// Documento Trofeos/{id} (lo escribe la pantalla EdicionTrofeosScreen):
//   Nombre       (string)   nombre visible del trofeo
//   Descripcion  (string)   texto de ayuda / condición explicada
//   Icono        (string)   emoji opcional (por defecto 🏆)
//   Metrica      (string)   clave de la métrica (ver MetricasValidas)
//   Operador     (string)   ">=" (por defecto), ">", "==", "<=", "<"
//   Objetivo     (long)     valor a alcanzar
//   Orden        (int)      orden de presentación
//   Activo       (bool)     si está en juego (ausente = activo)
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
    // EVALUACIÓN TRAS RESOLVER UN TURNO
    // ─────────────────────────────────────────────────────────────────────────

    /// Comprueba, para todos los jugadores de la partida, si han conseguido algún
    /// trofeo nuevo tras resolverse el turno, y lo registra en su perfil
    /// (arrayUnion, idempotente). Best-effort: nunca lanza. Se llama SIEMPRE tras
    /// resolver un turno (cierre normal o resolución forzosa por fecha límite).
    public static async Task EvaluarTrasTurnoAsync(FirestoreDb db, string lobbyId)
    {
        if (db == null || string.IsNullOrWhiteSpace(lobbyId)) return;
        try
        {
            // 1) Catálogo de trofeos activos (una sola lectura).
            var trofeosSnap = await db.Collection("Trofeos").GetSnapshotAsync();
            var activos = new List<(string id, Dictionary<string, object?> d)>();
            foreach (var doc in trofeosSnap.Documents)
            {
                var d = M.Map(M.ToJsonSafe(doc.ToDictionary()));
                if (EstaActivo(d)) activos.Add((doc.Id, d));
            }
            if (activos.Count == 0) return;

            // 2) Jugadores de la partida (mismo shape que en la resolución: lista
            //    de mapas con clave `uid`). Se evalúa a TODOS (incluidos los recién
            //    eliminados: pueden haber logrado su última victoria/partida).
            var lobby = await db.Collection("Partidas").Document(lobbyId).GetSnapshotAsync();
            if (!lobby.Exists) return;
            var data = M.Map(M.FromFs(lobby.ToDictionary()));
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
    public async Task<Dictionary<string, object?>> TrofeosAsync(string uid)
    {
        var db = _fs.Db;

        var jugadorTask = db.Collection("Jugadores").Document(uid).GetSnapshotAsync();
        var trofeosTask = db.Collection("Trofeos").GetSnapshotAsync();
        await Task.WhenAll(jugadorTask, trofeosTask);

        var jd = jugadorTask.Result.Exists
            ? M.Map(M.ToJsonSafe(jugadorTask.Result.ToDictionary()))
            : new Dictionary<string, object?>();

        var conseguidos = M.List(M.Get(jd, WarZeroTrofeos.CampoConseguidos))
            .Select(M.Str).Where(s => !string.IsNullOrEmpty(s)).ToHashSet();

        // Catálogo activo.
        var activos = new List<(string id, Dictionary<string, object?> d)>();
        foreach (var doc in trofeosTask.Result.Documents)
        {
            var d = M.Map(M.ToJsonSafe(doc.ToDictionary()));
            if (WarZeroTrofeos.EstaActivo(d)) activos.Add((doc.Id, d));
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

        return new Dictionary<string, object?>
        {
            ["trofeos"] = lista,
            ["total"] = total,
            ["conseguidos"] = logrados,
            ["porcentaje"] = porcentaje,
        };
    }
}