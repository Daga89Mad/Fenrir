using System.Security.Cryptography;
using System.Text;

// ─────────────────────────────────────────────────────────────────────────────
// HistoriaTuneles.cs
//
// TÚNELES INUNDABLES de las batallas de historia (p. ej. humanos_2 · el túnel
// de Soren). Catálogo de DATOS + lógica pura (sin Firestore), igual que
// HistoriaBombardeo.cs. Lo consumen:
//   • WarZeroHistoria.CrearPartidaHistoriaAsync → estado inicial (`tunel`).
//   • WarZeroHistoria.AplicarReglasMovimientoHistoria (WarZeroService, tras
//     los muros) → valida los movimientos del jugador y resuelve las
//     inundaciones del turno.
//   • La IA del bot → las celdas del túnel están VETADAS para el bot y lo que
//     hay dentro es invisible para sus cazadores.
//
// REGLAS
//   • El túnel es una lista ORDENADA de TRAMOS (de la boca de entrada a la de
//     salida), cada uno de 3 casillas en paralelo, más celdas secas sueltas
//     (p. ej. el recodo). Solo se entra y se sale por las BOCAS: cada casilla
//     de boca está unida a sus celdas exteriores; el resto del túnel tiene
//     paredes.
//   • Dentro del túnel TODAS las cartas mueven `Movimiento` (2) casillas, solo
//     por casillas del túnel no inundadas (o saliendo por una boca).
//   • En cada partida se eligen al azar `TramosConAgua` tramos con agua. En
//     cada uno solo UNA de sus 3 casillas es segura. Entrar en una casilla sin
//     explorar TERMINA el movimiento; al resolver el turno:
//       – si era la segura → queda LIMPIA (cualquiera puede pasar) y el resto
//         del tramo se inunda;
//       – si no → se INUNDA: muere todo lo que haya en ella y nadie puede
//         volver a entrar.
//   • Los tramos con agua nunca son contiguos y siempre existe un camino de
//     la entrada a la salida (se verifica al generar).
//   • El jugador NO ve qué casilla es la segura: la casilla segura de cada
//     tramo se deriva de la semilla de la partida con un SECRETO del servidor
//     (HMAC), así que no viaja en el documento de la partida.
// ─────────────────────────────────────────────────────────────────────────────

/// Configuración de un túnel.
public sealed record ConfigTunel(
    /// Tramos ordenados de la boca de entrada a la de salida. Cada tramo son
    /// casillas en paralelo (normalmente 3) entre las que se elige el paso.
    IReadOnlyList<IReadOnlyList<string>> Tramos,
    /// Casillas del túnel que no pertenecen a ningún tramo (siempre secas),
    /// p. ej. el recodo donde el túnel gira.
    IReadOnlyList<string> CeldasSecas,
    /// Bocas: casilla del túnel → celdas EXTERIORES por las que se entra/sale.
    IReadOnlyDictionary<string, IReadOnlyList<string>> Bocas,
    /// Movimiento de TODAS las cartas dentro del túnel.
    int Movimiento = 2,
    /// Nº de tramos con agua por partida.
    int TramosConAgua = 4,
    /// Índices de `Tramos` que pueden tener agua (null = todos).
    IReadOnlyList<int>? TramosCandidatos = null);

/// Estado PÚBLICO del túnel en una partida (campo `tunel`).
public sealed class EstadoTunel
{
    /// Índices de los tramos con agua de esta partida.
    public List<int> TramosAgua { get; } = new();
    /// Casillas de tramos con agua aún sin explorar.
    public HashSet<string> SinExplorar { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// Casillas de tramos con agua ya exploradas y seguras.
    public HashSet<string> Limpias { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// Casillas inundadas (impracticables para siempre).
    public HashSet<string> Inundadas { get; } = new(StringComparer.OrdinalIgnoreCase);

    // Lo que pasó al resolver el último turno (para el aviso del cliente).
    public int UltimoTurno { get; set; }
    public List<string> UltimoInundadas { get; } = new();
    public List<string> UltimoLimpias { get; } = new();
    public List<(string coord, string nombre)> UltimoAhogadas { get; } = new();
    public List<(string desde, string hacia, string nombre)> UltimoRevertidas { get; } = new();

    /// Documento a guardar en la partida (`tunel`). Incluye la geometría para
    /// que el cliente pinte y calcule el movimiento sin conocer la config.
    public Dictionary<string, object?> ACampo(ConfigTunel cfg)
    {
        var celdas = HistoriaTuneles.TodasLasCeldas(cfg);
        return new Dictionary<string, object?>
        {
            ["celdas"] = celdas.Cast<object?>().ToList(),
            ["bocas"] = cfg.Bocas.ToDictionary(
                kv => kv.Key, kv => (object?)kv.Value.Cast<object?>().ToList()),
            ["movimiento"] = (long)cfg.Movimiento,
            ["tramosAgua"] = TramosAgua.Select(i => (object?)(long)i).ToList(),
            ["sinExplorar"] = SinExplorar.OrderBy(c => c, StringComparer.Ordinal).Cast<object?>().ToList(),
            ["limpias"] = Limpias.OrderBy(c => c, StringComparer.Ordinal).Cast<object?>().ToList(),
            ["inundadas"] = Inundadas.OrderBy(c => c, StringComparer.Ordinal).Cast<object?>().ToList(),
            ["ultimo"] = new Dictionary<string, object?>
            {
                ["turno"] = (long)UltimoTurno,
                ["inundadas"] = UltimoInundadas.Cast<object?>().ToList(),
                ["limpias"] = UltimoLimpias.Cast<object?>().ToList(),
                ["ahogadas"] = UltimoAhogadas.Select(a => (object?)new Dictionary<string, object?>
                {
                    ["coord"] = a.coord,
                    ["nombre"] = a.nombre,
                }).ToList(),
                ["revertidas"] = UltimoRevertidas.Select(a => (object?)new Dictionary<string, object?>
                {
                    ["desde"] = a.desde,
                    ["hacia"] = a.hacia,
                    ["nombre"] = a.nombre,
                }).ToList(),
            },
        };
    }

    /// Lee el campo `tunel` de la partida. null si no hay.
    public static EstadoTunel? DesdeCampo(object? raw)
    {
        var m = M.Map(raw);
        if (m.Count == 0) return null;
        var e = new EstadoTunel();
        e.TramosAgua.AddRange(M.List(M.Get(m, "tramosAgua")).Select(M.Int));
        foreach (var c in M.List(M.Get(m, "sinExplorar")).Select(M.Str)) if (c != "") e.SinExplorar.Add(c);
        foreach (var c in M.List(M.Get(m, "limpias")).Select(M.Str)) if (c != "") e.Limpias.Add(c);
        foreach (var c in M.List(M.Get(m, "inundadas")).Select(M.Str)) if (c != "") e.Inundadas.Add(c);
        return e;
    }
}

public static class HistoriaTuneles
{
    // ── humanos_2 · Los hermanos del alba · El túnel de Soren ────────────────
    // Mapa MonolitoNefilim2 (10×16). El túnel entra por la columna 13 (filas
    // E-G, junto a G14 donde empieza Soren), recorre las filas E, F y G hacia el
    // oeste hasta la columna 3 y sube por las columnas 3-5 hasta la fila C, que
    // sale a la fila B junto al cuartel del jugador (A2).
    //
    //        3  4  5  6  7  8  9 10 11 12 13
    //    C   ▲  ▲  ▲                            ← boca de salida (a B3/B4/B5)
    //    D   ▓  ▓  ▓
    //    E   ░  ░  ░  ▓  ▓  ▓  ▓  ▓  ▓  ▓  ◄   ← boca de entrada (desde E14/F14/G14)
    //    F   ░  ░  ░  ▓  ▓  ▓  ▓  ▓  ▓  ▓  ◄
    //    G   ░  ░  ░  ▓  ▓  ▓  ▓  ▓  ▓  ▓  ◄
    //    ░ = recodo (siempre seco)
    //
    // Tramos: 0 = columna 13 (entrada, siempre seca), 1..7 = columnas 12..6,
    // 8 = fila D, 9 = fila C (salida). 4 de los tramos 1..9 llevan agua.
    private static readonly ConfigTunel TunelSoren = new(
        Tramos: new List<IReadOnlyList<string>>
        {
            new[] { "E13", "F13", "G13" },   // 0 · boca de entrada
            new[] { "E12", "F12", "G12" },   // 1
            new[] { "E11", "F11", "G11" },   // 2
            new[] { "E10", "F10", "G10" },   // 3
            new[] { "E9", "F9", "G9" },      // 4
            new[] { "E8", "F8", "G8" },      // 5
            new[] { "E7", "F7", "G7" },      // 6
            new[] { "E6", "F6", "G6" },      // 7
            new[] { "D3", "D4", "D5" },      // 8
            new[] { "C3", "C4", "C5" },      // 9 · boca de salida
        },
        CeldasSecas: new[] { "E3", "E4", "E5", "F3", "F4", "F5", "G3", "G4", "G5" },
        Bocas: new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["E13"] = new[] { "E14" },
            ["F13"] = new[] { "F14" },
            ["G13"] = new[] { "G14" },
            ["C3"] = new[] { "B3" },
            ["C4"] = new[] { "B4" },
            ["C5"] = new[] { "B5" },
        },
        Movimiento: 2,
        TramosConAgua: 4,
        TramosCandidatos: new[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 });

    /// Túneles registrados, por `HistoriaDef.Id`.
    public static readonly IReadOnlyDictionary<string, ConfigTunel> Todas =
        new Dictionary<string, ConfigTunel>
        {
            ["humanos_2"] = TunelSoren,
        };

    public static ConfigTunel? Get(string historiaId) =>
        !string.IsNullOrEmpty(historiaId) && Todas.TryGetValue(historiaId, out var t) ? t : null;

    /// Todas las casillas del túnel (tramos + secas), sin repetir.
    public static List<string> TodasLasCeldas(ConfigTunel cfg) =>
        cfg.Tramos.SelectMany(t => t).Concat(cfg.CeldasSecas)
            .Select(c => c.Trim().ToUpperInvariant())
            .Distinct()
            .ToList();

    // ── Secreto ──────────────────────────────────────────────────────────────
    // La casilla segura de cada tramo NO se guarda en la partida: se deriva de
    // la semilla con este secreto (variable de entorno WZ_TUNEL_SECRETO en el
    // servidor; si no existe se usa el valor por defecto). El cliente nunca lo
    // ve, así que leer el documento de la partida no revela el camino.
    private static string Secreto =>
        Environment.GetEnvironmentVariable("WZ_TUNEL_SECRETO")
        ?? "wz-tunel-7f3a9c1e5b2d4086-humanos";

    /// Casilla SEGURA del tramo [indice] en la partida de [semilla].
    public static string CasillaSegura(ConfigTunel cfg, int semilla, int indice, int intento = 0)
    {
        var tramo = cfg.Tramos[indice];
        using var h = new HMACSHA256(Encoding.UTF8.GetBytes(Secreto));
        var bytes = h.ComputeHash(Encoding.UTF8.GetBytes($"{semilla}|{indice}|{intento}"));
        uint v = BitConverter.ToUInt32(bytes, 0);
        return tramo[(int)(v % (uint)tramo.Count)].Trim().ToUpperInvariant();
    }

    // ── Estado inicial ───────────────────────────────────────────────────────
    /// Elige los tramos con agua de esta partida (distintos cada partida) y
    /// comprueba que exista un camino de la entrada a la salida.
    public static EstadoTunel Inicial(ConfigTunel cfg, int semilla)
    {
        var candidatos = (cfg.TramosCandidatos ?? Enumerable.Range(0, cfg.Tramos.Count).ToList())
            .Where(i => i >= 0 && i < cfg.Tramos.Count)
            .Distinct()
            .ToList();

        for (int intento = 0; intento < 50; intento++)
        {
            var rng = new Random(unchecked(semilla * 31 + 7919 + intento));
            var orden = candidatos.OrderBy(_ => rng.Next()).ToList();
            var elegidos = new List<int>();
            foreach (var i in orden)
            {
                if (elegidos.Count >= cfg.TramosConAgua) break;
                if (elegidos.Any(j => TramosContiguos(cfg.Tramos[i], cfg.Tramos[j]))) continue;
                elegidos.Add(i);
            }
            if (elegidos.Count < Math.Min(cfg.TramosConAgua, candidatos.Count) && intento < 49) continue;
            elegidos.Sort();

            if (!HayCamino(cfg, elegidos, semilla)) continue;

            var e = new EstadoTunel();
            e.TramosAgua.AddRange(elegidos);
            foreach (var i in elegidos)
                foreach (var c in cfg.Tramos[i]) e.SinExplorar.Add(c.Trim().ToUpperInvariant());
            return e;
        }

        // Imposible (config rara): túnel sin agua antes que una partida rota.
        Console.Error.WriteLine("[WZ.Tunel] no se pudo generar un túnel con camino; se deja seco");
        return new EstadoTunel();
    }

    /// True si alguna casilla de [a] es ortogonalmente adyacente a una de [b].
    private static bool TramosContiguos(IReadOnlyList<string> a, IReadOnlyList<string> b) =>
        a.Any(x => b.Any(y => Manhattan(x, y) == 1));

    /// Hay camino de cualquier casilla de la boca de entrada (tramo 0) a
    /// cualquiera de la de salida (último tramo) pisando solo casillas secas o
    /// las seguras de los tramos con agua.
    private static bool HayCamino(ConfigTunel cfg, List<int> conAgua, int semilla)
    {
        var celdas = TodasLasCeldas(cfg).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var i in conAgua)
        {
            var segura = CasillaSegura(cfg, semilla, i);
            foreach (var c in cfg.Tramos[i])
                if (!string.Equals(c, segura, StringComparison.OrdinalIgnoreCase))
                    celdas.Remove(c.Trim().ToUpperInvariant());
        }
        var inicio = cfg.Tramos[0].Select(c => c.Trim().ToUpperInvariant()).Where(celdas.Contains).ToList();
        var fin = cfg.Tramos[^1].Select(c => c.Trim().ToUpperInvariant()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var vistos = new HashSet<string>(inicio, StringComparer.OrdinalIgnoreCase);
        var cola = new Queue<string>(inicio);
        while (cola.Count > 0)
        {
            var c = cola.Dequeue();
            if (fin.Contains(c)) return true;
            foreach (var n in Vecinos(c))
                if (celdas.Contains(n) && vistos.Add(n)) cola.Enqueue(n);
        }
        return false;
    }

    // ── Movimiento ───────────────────────────────────────────────────────────
    /// Casillas a las que puede llegar una carta que empieza el turno en
    /// [origen] con las reglas del túnel. [movPropio] es su movimiento normal
    /// (se usa si empieza FUERA del túnel). [transitable] dice si una celda
    /// EXTERIOR se puede pisar con su tipo. No incluye el origen.
    ///
    ///   • Empezando FUERA: movimiento normal por el exterior; las casillas del
    ///     túnel son pared salvo entrar en una BOCA desde su celda exterior, y
    ///     entrar termina el movimiento.
    ///   • Empezando DENTRO: `cfg.Movimiento` pasos por casillas del túnel no
    ///     inundadas, o saliendo por una boca a su celda exterior (y seguir
    ///     fuera con los pasos que queden). Entrar en una casilla SIN EXPLORAR
    ///     termina el movimiento.
    /// Mismo algoritmo que el cliente (historia_tunel.dart · destinosConTunel).
    public static HashSet<string> Destinos(
        ConfigTunel cfg, EstadoTunel estado, string origen, int movPropio,
        Func<string, bool> transitable, int filas, int columnas)
    {
        var tunel = TodasLasCeldas(cfg).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var res = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        origen = origen.Trim().ToUpperInvariant();
        bool empiezaDentro = tunel.Contains(origen);
        int mov = empiezaDentro ? cfg.Movimiento : movPropio;
        if (mov <= 0) return res;

        bool EsBocaDe(string interior, string exterior) =>
            cfg.Bocas.TryGetValue(interior, out var ext)
            && ext.Any(e => string.Equals(e, exterior, StringComparison.OrdinalIgnoreCase));

        var visto = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { [origen] = 0 };
        var cola = new Queue<(string c, int d)>();
        cola.Enqueue((origen, 0));
        while (cola.Count > 0)
        {
            var (cur, d) = cola.Dequeue();
            if (d >= mov) continue;
            bool curDentro = tunel.Contains(cur);
            foreach (var n in Vecinos(cur))
            {
                var p = Parse(n);
                if (p == null || p.Value.r >= filas || p.Value.c > columnas) continue;
                int nd = d + 1;
                if (visto.TryGetValue(n, out var vd) && vd <= nd) continue;
                bool nDentro = tunel.Contains(n);

                if (nDentro)
                {
                    if (estado.Inundadas.Contains(n)) continue;
                    if (!curDentro)
                    {
                        // Entrar desde fuera: solo por la boca, y se acaba el paso.
                        if (!EsBocaDe(n, cur)) continue;
                        visto[n] = nd;
                        if (n != origen) res.Add(n);
                        continue;
                    }
                    visto[n] = nd;
                    if (n != origen) res.Add(n);
                    if (estado.SinExplorar.Contains(n)) continue;   // termina aquí
                    if (nd < mov) cola.Enqueue((n, nd));
                }
                else
                {
                    if (curDentro && !EsBocaDe(cur, n)) continue;   // pared del túnel
                    if (!transitable(n)) continue;
                    visto[n] = nd;
                    if (n != origen) res.Add(n);
                    if (nd < mov) cola.Enqueue((n, nd));
                }
            }
        }
        return res;
    }

    /// Alcance SIN las reglas del túnel (el túnel es terreno normal): sirve
    /// para saber si un movimiento entre dos celdas exteriores solo era posible
    /// atravesando el túnel.
    public static HashSet<string> DestinosSinTunel(
        string origen, int mov, Func<string, bool> transitable, int filas, int columnas)
    {
        var res = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (mov <= 0) return res;
        origen = origen.Trim().ToUpperInvariant();
        var visto = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { [origen] = 0 };
        var cola = new Queue<(string c, int d)>();
        cola.Enqueue((origen, 0));
        while (cola.Count > 0)
        {
            var (cur, d) = cola.Dequeue();
            if (d >= mov) continue;
            foreach (var n in Vecinos(cur))
            {
                var p = Parse(n);
                if (p == null || p.Value.r >= filas || p.Value.c > columnas) continue;
                int nd = d + 1;
                if (visto.TryGetValue(n, out var vd) && vd <= nd) continue;
                if (!transitable(n)) continue;
                visto[n] = nd;
                if (n != origen) res.Add(n);
                if (nd < mov) cola.Enqueue((n, nd));
            }
        }
        return res;
    }

    // ── Inundación ───────────────────────────────────────────────────────────
    /// Resuelve las casillas SIN EXPLORAR ocupadas este turno (mutando
    /// [tablero] y [estado]). Devuelve las cartas ahogadas (coord, carta).
    public static List<(string coord, Dictionary<string, object?> carta)> ResolverInundacion(
        ConfigTunel cfg, EstadoTunel estado, int semilla,
        Dictionary<string, List<Dictionary<string, object?>>> tablero)
    {
        var ahogadas = new List<(string, Dictionary<string, object?>)>();
        bool Ocupada(string c) => tablero.TryGetValue(c, out var l) && l.Count > 0;

        void Inundar(string c)
        {
            estado.SinExplorar.Remove(c);
            estado.Limpias.Remove(c);
            if (estado.Inundadas.Add(c)) estado.UltimoInundadas.Add(c);
            if (tablero.TryGetValue(c, out var lst))
            {
                foreach (var carta in lst) ahogadas.Add((c, carta));
                tablero.Remove(c);
            }
        }

        foreach (var i in estado.TramosAgua)
        {
            if (i < 0 || i >= cfg.Tramos.Count) continue;
            var tramo = cfg.Tramos[i].Select(c => c.Trim().ToUpperInvariant()).ToList();
            var pendientes = tramo.Where(estado.SinExplorar.Contains).ToList();
            if (pendientes.Count == 0) continue;
            var ocupadas = pendientes.Where(Ocupada).ToList();
            if (ocupadas.Count == 0) continue;

            var segura = CasillaSegura(cfg, semilla, i);
            if (ocupadas.Contains(segura))
            {
                // Paso encontrado: la segura queda limpia y el resto del tramo
                // se inunda (con lo que hubiera entrado en ella).
                estado.SinExplorar.Remove(segura);
                if (estado.Limpias.Add(segura)) estado.UltimoLimpias.Add(segura);
                foreach (var c in pendientes.Where(c => c != segura)) Inundar(c);
            }
            else
            {
                foreach (var c in ocupadas) Inundar(c);
            }
        }
        return ahogadas;
    }

    // ── Utilidades ───────────────────────────────────────────────────────────
    private static IEnumerable<string> Vecinos(string coord)
    {
        var p = Parse(coord);
        if (p == null) yield break;
        var (r, c) = p.Value;
        if (r > 0) yield return Format(r - 1, c);
        yield return Format(r + 1, c);
        if (c > 1) yield return Format(r, c - 1);
        yield return Format(r, c + 1);
    }

    private static int Manhattan(string a, string b)
    {
        var pa = Parse(a); var pb = Parse(b);
        if (pa == null || pb == null) return int.MaxValue;
        return Math.Abs(pa.Value.r - pb.Value.r) + Math.Abs(pa.Value.c - pb.Value.c);
    }

    /// "B5" → (fila 0-based, columna 1-based).
    private static (int r, int c)? Parse(string coord)
    {
        if (string.IsNullOrEmpty(coord) || coord.Length < 2) return null;
        int r = char.ToUpperInvariant(coord[0]) - 'A';
        if (r < 0 || r >= 26 || !int.TryParse(coord[1..], out int c) || c < 1) return null;
        return (r, c);
    }

    private static string Format(int r, int c) => $"{(char)('A' + r)}{c}";
}