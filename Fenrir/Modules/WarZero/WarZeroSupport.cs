using System.Collections;
using System.Text.Json;
using Google.Apis.Auth.OAuth2;
using Google.Cloud.Firestore;

// ─────────────────────────────────────────────────────────────────────────────
// WarZeroSupport.cs
//
// Utilidades transversales del módulo WarZero:
//   • M        → acceso tipado y conversión de Map/List "estilo Dart".
//   • Coords   → genera todas las celdas válidas según el nº de jugadores
//                (equivalente a GameConfig.forPlayerCount(...).allCells).
//   • WarZeroFirestore → provee un FirestoreDb reutilizable.
//
// Toda la lógica de juego trabaja con Dictionary<string, object?> y
// List<object?> (primitivas CLR: string, long, double, bool, null), igual que
// el código Dart trabaja con Map<String, dynamic>. Esto evita el infierno de
// JsonElement y encaja directamente con lo que Firestore lee/escribe.
// ─────────────────────────────────────────────────────────────────────────────

public static class M
{
    /// Convierte un valor a Dictionary<string, object?> (mapa "Dart-like").
    public static Dictionary<string, object?> Map(object? o)
    {
        if (o is Dictionary<string, object?> d) return d;
        if (o is IDictionary<string, object> d2)
        {
            var r = new Dictionary<string, object?>();
            foreach (var kv in d2) r[kv.Key] = kv.Value;
            return r;
        }
        return new Dictionary<string, object?>();
    }

    /// Convierte un valor a List<object?>.
    public static List<object?> List(object? o)
    {
        if (o is List<object?> l) return l;
        if (o is string) return new List<object?>();
        if (o is IEnumerable e)
        {
            var r = new List<object?>();
            foreach (var x in e) r.Add(x);
            return r;
        }
        return new List<object?>();
    }

    public static long Long(object? o) => o switch
    {
        long l => l,
        int i => i,
        double d => (long)d,
        float f => (long)f,
        bool b => b ? 1 : 0,
        string s when long.TryParse(s, out var v) => v,
        _ => 0L,
    };

    public static int Int(object? o) => (int)Long(o);

    /// Interpreta un valor como booleano. Acepta bool, "true"/"false"
    /// (case-insensitive) y números (0 = false, resto = true). Cualquier otra
    /// cosa (incluido null / campo ausente) es false.
    public static bool Bool(object? o) => o switch
    {
        bool b => b,
        string s => s.Trim().ToLowerInvariant() == "true" || s == "1",
        int i => i != 0,
        long l => l != 0,
        double d => d != 0,
        float f => f != 0,
        _ => false,
    };

    public static string Str(object? o) => o as string ?? (o?.ToString() ?? "");

    /// Primer valor presente entre varias claves candidatas (PascalCase/snake_case).
    public static object? Get(Dictionary<string, object?> m, params string[] keys)
    {
        foreach (var k in keys)
            if (m.TryGetValue(k, out var v)) return v;
        return null;
    }

    /// Normaliza recursivamente lo que devuelve Firestore (Dictionary<string,object>,
    /// List<object>, long, double, string, bool, Timestamp...) a la representación
    /// "Dart-like" con Dictionary<string,object?> / List<object?>.
    public static object? FromFs(object? o)
    {
        switch (o)
        {
            case IDictionary<string, object> d:
                var rd = new Dictionary<string, object?>();
                foreach (var kv in d) rd[kv.Key] = FromFs(kv.Value);
                return rd;
            case string s:
                return s;
            case IEnumerable e:
                var rl = new List<object?>();
                foreach (var x in e) rl.Add(FromFs(x));
                return rl;
            default:
                return o;
        }
    }

    /// Convierte recursivamente un valor (incluido lo que devuelve Firestore) a
    /// tipos serializables por System.Text.Json: Timestamp/DateTime → epoch
    /// millis (long); diccionarios y listas se recorren; primitivas se dejan.
    public static object? ToJsonSafe(object? o)
    {
        switch (o)
        {
            case null:
                return null;
            case Timestamp ts:
                return (long)ts.ToDateTime()
                    .Subtract(DateTime.UnixEpoch).TotalMilliseconds;
            case DateTime dt:
                return (long)dt.ToUniversalTime().Subtract(DateTime.UnixEpoch).TotalMilliseconds;
            case string s:
                return s;
            case bool or long or int or double or float:
                return o;
            case IDictionary<string, object> d:
                var rd = new Dictionary<string, object?>();
                foreach (var kv in d) rd[kv.Key] = ToJsonSafe(kv.Value);
                return rd;
            case IEnumerable e:
                var rl = new List<object?>();
                foreach (var x in e) rl.Add(ToJsonSafe(x));
                return rl;
            default:
                return o?.ToString();
        }
    }

    /// Convierte un JsonElement (cuerpo de la request) a primitivas CLR.
    public static object? FromJson(JsonElement el)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Object:
                var map = new Dictionary<string, object?>();
                foreach (var p in el.EnumerateObject()) map[p.Name] = FromJson(p.Value);
                return map;
            case JsonValueKind.Array:
                var list = new List<object?>();
                foreach (var item in el.EnumerateArray()) list.Add(FromJson(item));
                return list;
            case JsonValueKind.String:
                return el.GetString();
            case JsonValueKind.Number:
                return el.TryGetInt64(out var l) ? l : el.GetDouble();
            case JsonValueKind.True: return true;
            case JsonValueKind.False: return false;
            default: return null;
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────

public static class Coords
{
    // Etiquetas de fila/columna por nº de jugadores (idéntico a GameConfig.dart).
    private static (string[] rows, int[] cols) Layout(int playerCount)
    {
        switch (playerCount)
        {
            case 2:
                return (new[] { "A", "B", "C", "D", "E", "F" },
                        Range(1, 10));
            case 6:
                return (new[] { "A", "B", "C", "D", "E", "F", "G", "H", "I", "J" },
                        Range(1, 16));
            case 8:
                return (new[] { "A", "B", "C", "D", "E", "F", "G", "H", "I", "J", "K", "L" },
                        Range(1, 18));
            default: // 4 jugadores
                return (new[] { "A", "B", "C", "D", "E", "F", "G", "H" },
                        Range(1, 14));
        }
    }

    private static int[] Range(int from, int to)
    {
        var r = new int[to - from + 1];
        for (int i = 0; i < r.Length; i++) r[i] = from + i;
        return r;
    }

    /// Todas las coordenadas válidas del tablero para ese nº de jugadores.
    public static List<string> AllCells(int playerCount)
    {
        var (rows, cols) = Layout(playerCount);
        var result = new List<string>(rows.Length * cols.Length);
        foreach (var row in rows)
            foreach (var col in cols)
                result.Add($"{row}{col}");
        return result;
    }
}

// ─────────────────────────────────────────────────────────────────────────────

public class WarZeroFirestore
{
    private readonly Lazy<FirestoreDb> _db;

    // Acceso al cliente. La construcción es PEREZOSA: si Build() falla, la
    // excepción se lanza aquí (dentro del try/catch del endpoint) y no durante
    // la resolución de dependencias, que daría un 500 crudo sin detalle.
    public FirestoreDb Db => _db.Value;

    public WarZeroFirestore(IConfiguration cfg)
    {
        var projectId = cfg["Firebase:ProjectId"] ?? "warzero-6fe4d";
        var credentialsPath =
            cfg["Firebase:CredentialsPath"] ?? "Firebase/firebase-key.json";

        _db = new Lazy<FirestoreDb>(() =>
        {
            var builder = new FirestoreDbBuilder { ProjectId = projectId };

            // Resolver el fichero de credencial probando rutas candidatas, para
            // no depender del directorio de trabajo del proceso. Se carga con el
            // MISMO mecanismo que FirebaseApp.Create (GoogleCredential.FromFile),
            // que ya funciona al arrancar.
            var candidatos = new[]
            {
                credentialsPath,
                Path.Combine(AppContext.BaseDirectory, credentialsPath),
                Path.Combine(Directory.GetCurrentDirectory(), credentialsPath),
            };
            var ruta = candidatos.FirstOrDefault(File.Exists);

            if (ruta != null)
            {
                builder.Credential = GoogleCredential.FromFile(ruta);
            }
            else
            {
                // Como último recurso, reutiliza la credencial ya cargada por
                // FirebaseAdmin en el arranque (misma cuenta de servicio).
                var fbApp = FirebaseAdmin.FirebaseApp.DefaultInstance;
                if (fbApp?.Options?.Credential != null)
                    builder.Credential = fbApp.Options.Credential;
            }

            return builder.Build();
        });
    }
}