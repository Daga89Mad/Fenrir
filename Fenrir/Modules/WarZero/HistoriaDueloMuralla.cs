// ─────────────────────────────────────────────────────────────────────────────
// HistoriaDueloMuralla.cs
//
// MODO MURALLA del duelo de generales (`ConfigDuelo.Modo = Muralla`,
// nefilim_2 · «El hijo del odio · La guardiana del último avance»). Alexander
// (rol "cazador", del jugador) contra la General Izanami (rol "jefe", del bot),
// en lo alto de la muralla de Dunant. Es parte del motor de duelos
// (HistoriaDuelo.cs): comparte vidas, eventos, creación, fin de batalla y
// marcas del cliente. Aquí solo vive lo propio de este modo.
//
// REGLAS (con los valores por defecto de AjustesDueloIzanami)
//   • Vidas: Izanami 3 NÚCLEOS · Alexander 3 vidas. Sin combate normal.
//   • ESCUDO FRONTAL: Izanami siempre mira hacia una dirección (N/S/E/O).
//     Lo que decide un golpe es desde qué casilla venía Alexander al entrar
//     en la suya: desde DELANTE (cono de 90°) → Alexander pierde una vida;
//     desde un LATERAL o la ESPALDA → Izanami pierde un núcleo.
//   • NUNCA RETROCEDE. Cada turno hace UNA cosa, que se publica al empezar el
//     turno (plan):
//       – AVANZAR: si Alexander está en su cono frontal, carga en línea recta
//         (sin girar) hasta la altura a la que estaba Alexander, como mucho
//         `AlcanceCarga` casillas. Si Alexander termina en su carril, lo
//         arrolla (golpe frontal) y se detiene ahí.
//       – BARRIDO: si Alexander empieza el turno pegado a ella (también en
//         diagonal) y no está en su cono, barre con el escudo las 8 casillas
//         de alrededor: si termina en una de ellas o en la suya, pierde una
//         vida y sale despedido.
//       – GIRAR 90° hacia Alexander sin moverse, en cualquier otro caso. Ese
//         es el momento de golpearla por el flanco (hay que empezar el turno
//         a 2 casillas para llegar a la suya).
//   • LÍNEA BIÓNICA: tras ella avanza una fila de soldados que gana una fila
//     cada `LineaCada` turnos. Nadie más que Izanami puede pisar la línea ni
//     lo que queda detrás. Si la línea alcanza a Alexander, pierde una vida y
//     retrocede; solo si no queda ninguna casilla libre por delante de la
//     línea, cae de la muralla. Es el RELOJ del duelo.
//   • GRIETAS: cada vez que Alexander termina un turno en una casilla, la
//     agrieta un poco más (⚡ → ⚡⚡ GRIETA PROFUNDA). Si Izanami TERMINA una
//     carga sobre una grieta profunda, el suelo se hunde: queda ATURDIDA el
//     turno siguiente (cualquier golpe le quita un núcleo) y la casilla se
//     convierte en un HUECO impracticable cuando sale de ella. Una grieta que
//     no se pisa en `GrietaDuracion` turnos se asienta y desaparece.
//   • ODIO: cada vida que pierde Alexander le da 1 punto de odio (máx. 2).
//     Con 2 puntos puede lanzar la EMBESTIDA DEL ODIO (POST /warzero/duelo/
//     odio, desde la ficha de Alexander): ese turno, un choque de frente le
//     quita un núcleo a ella en lugar de una vida a él. Si no llega a tocarla,
//     conserva el odio.
//   • FASES por núcleos:
//       – Con 2: su carga llega más lejos, barre un carril de 3 casillas de
//         ancho, carga contra todo lo que tenga delante y su ESCUDO DOBLE
//         cubre también el lateral hacia el que gira: solo la espalda (o una
//         grieta, o el odio) la hiere.
//       – Con 1: además GIRA Y CARGA en el mismo turno (ya no hay turnos de
//         giro quieta) y la línea avanza cada turno.
//   • Alexander no puede ATRAVESAR la casilla de Izanami: solo terminar en ella.
//   • Tras cada golpe a Izanami, Alexander sale despedido delante de ella y
//     ella se vuelve hacia él: el turno siguiente carga contra él.
// ─────────────────────────────────────────────────────────────────────────────

// ═════════════════════════════════════════════════════════════════════════════
// AJUSTES DE "EL HIJO DEL ODIO · PARTE 2" (nefilim_2) — EDITA AQUÍ
// ═════════════════════════════════════════════════════════════════════════════
public static class AjustesDueloIzanami
{
    // ── Vidas ────────────────────────────────────────────────────────────────
    public const int NucleosIzanami = 3;
    public const int VidasAlexander = 3;

    // ── Carga ────────────────────────────────────────────────────────────────
    /// Casillas que avanza al cargar (normal y desde la fase 2).
    public const int AlcanceCarga = 3;
    public const int AlcanceCargaFase2 = 4;
    /// Ancho del carril de la carga (1 o 3), normal y desde la fase 2.
    public const int AnchoCarga = 1;
    public const int AnchoCargaFase2 = 3;

    // ── Línea biónica ────────────────────────────────────────────────────────
    /// Fila en la que empieza la línea (0 = A) y turno de su primer avance.
    public const int LineaFilaInicial = 0;
    public const int LineaPrimerAvance = 2;
    /// Cada cuántos turnos avanza una fila (y en la fase 3). Simulado con un
    /// Alexander que lee bien los planes pero no prepara trampas a más de un
    /// turno vista: 3 y 2 → ~90 % de victorias; 3 y 1 → ~60 %.
    public const int LineaCada = 3;
    public const int LineaCadaFase3 = 2;

    // ── Odio ─────────────────────────────────────────────────────────────────
    public const int OdioMax = 2;
    public const int OdioCoste = 2;

    // ── Grietas ──────────────────────────────────────────────────────────────
    /// Turnos que dura una grieta sin volver a pisarla (0 = no se asienta).
    public const int GrietaDuracion = 6;
    /// Veces que hay que terminar el turno en una casilla para que se hunda.
    public const int GrietaPisadas = 2;
    /// Turnos que queda aturdida al hundirse.
    public const int TurnosAturdida = 1;

    // ── Mapa (MurallaDeDunant, 12 filas A-L × 8 columnas) ───────────────────
    public const string MapaId = "MurallaDeDunant";
    public const int Filas = 12;
    public const int Columnas = 8;
    public const string CeldaIzanami = "C4";
    public const string OrientacionInicial = "S";
    public const string CeldaAlexander = "J5";
    /// Cuarteles NOMINALES (bajo la línea desde el turno 1).
    public const string CuartelJugador = "A1";
    public const string CuartelBot = "A8";
    public static readonly string[] CeldasBloqueadas = { CuartelJugador, CuartelBot };
}

/// Reglas propias del modo muralla (ver la cabecera del fichero).
public sealed record ConfigMuralla(
    string OrientacionInicial = "S",
    int AlcanceCarga = 3,
    int AlcanceCargaFase2 = 4,
    int AnchoCarga = 1,
    int AnchoCargaFase2 = 3,
    int LineaFilaInicial = 0,
    int LineaPrimerAvance = 2,
    int LineaCada = 3,
    int LineaCadaFase3 = 2,
    int OdioMax = 2,
    int OdioCoste = 2,
    int GrietaDuracion = 6,
    int GrietaPisadas = 2,
    int TurnosAturdida = 1,
    /// Vidas de Izanami con las que empieza cada fase (fase 2 y fase 3).
    int VidasFase2 = 2,
    int VidasFase3 = 1,
    /// Fase desde la que el escudo cubre también el lateral hacia el que gira.
    int FaseEscudoDoble = 2,
    /// Fase desde la que gira y carga en el mismo turno (0 = nunca).
    int FaseGiraYCarga = 3);

public static partial class HistoriaDuelos
{
    private const string AlexanderNef = "VKM1uUkqO9GqDI6tTr47"; // General Alexander · F40 D35 M2
    public const string IzanamiId = "43RxCheHL2R4JP2IzIGi";    // General Izanami · F45 D28 M3

    /// «El hijo del odio» · parte 2: Alexander contra Izanami en la muralla.
    private static ConfigDuelo DueloIzanami => new(
        JefeId: IzanamiId,
        CazadorId: AlexanderNef,
        VerdugoId: "",                   // Alexander está solo
        VidasJefe: AjustesDueloIzanami.NucleosIzanami,
        VidasCazador: AjustesDueloIzanami.VidasAlexander,
        VidasVerdugo: 0,
        TurnoLimite: 0,                  // el reloj es la línea biónica
        MovimientoJefe: AjustesDueloIzanami.AlcanceCarga,
        CeldasBloqueadas: AjustesDueloIzanami.CeldasBloqueadas,
        TurnosParalisis: AjustesDueloIzanami.TurnosAturdida,
        LluviaCadaTurnos: 0,
        JefeCanalizaEnLluvia: false,
        RompeCadaTurnos: 0,
        JefeGanaAlLimite: true,
        Modo: ModoDuelo.Muralla,
        Muralla: new ConfigMuralla(
            OrientacionInicial: AjustesDueloIzanami.OrientacionInicial,
            AlcanceCarga: AjustesDueloIzanami.AlcanceCarga,
            AlcanceCargaFase2: AjustesDueloIzanami.AlcanceCargaFase2,
            AnchoCarga: AjustesDueloIzanami.AnchoCarga,
            AnchoCargaFase2: AjustesDueloIzanami.AnchoCargaFase2,
            LineaFilaInicial: AjustesDueloIzanami.LineaFilaInicial,
            LineaPrimerAvance: AjustesDueloIzanami.LineaPrimerAvance,
            LineaCada: AjustesDueloIzanami.LineaCada,
            LineaCadaFase3: AjustesDueloIzanami.LineaCadaFase3,
            OdioMax: AjustesDueloIzanami.OdioMax,
            OdioCoste: AjustesDueloIzanami.OdioCoste,
            GrietaDuracion: AjustesDueloIzanami.GrietaDuracion,
            GrietaPisadas: AjustesDueloIzanami.GrietaPisadas,
            TurnosAturdida: AjustesDueloIzanami.TurnosAturdida));

    /// Mapa por defecto de un duelo cuyo documento de `Mapas` no existe
    /// (todo tierra, con su rejilla y los dos cuarteles nominales).
    public static (int filas, int columnas, List<string> obeliscos)? MapaPorDefecto(string mapaId) =>
        string.Equals(mapaId, AjustesDueloIzanami.MapaId, StringComparison.OrdinalIgnoreCase)
            ? (AjustesDueloIzanami.Filas, AjustesDueloIzanami.Columnas,
               new List<string> { AjustesDueloIzanami.CuartelJugador, AjustesDueloIzanami.CuartelBot })
            : null;
}

// ═════════════════════════════════════════════════════════════════════════════
// ESTADO
// ═════════════════════════════════════════════════════════════════════════════
public sealed partial class EstadoDuelo
{
    public const string AccionAvanzar = "avanzar";
    public const string AccionGirar = "girar";
    public const string AccionAturdida = "aturdida";
    public const string AccionBarrido = "barrido";
    public const string AccionGiraYCarga = "giraYCarga";

    /// Hacia dónde mira el jefe ("N" | "S" | "E" | "O").
    public string Orientacion { get; set; } = "S";
    /// Casilla del general al EMPEZAR el turno (para saber desde dónde entra).
    public string CeldaGeneralInicio { get; set; } = "";
    /// Fila (0 = A) que ocupa la línea biónica y turno de su próximo avance.
    public int LineaFila { get; set; }
    public int LineaProximoAvance { get; set; }
    /// Grietas (casilla → veces que se ha pisado), turno de la última pisada y
    /// huecos impracticables.
    public Dictionary<string, int> Grietas { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, int> GrietasTurno { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> Huecos { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// Casilla hundida bajo el jefe aturdido (se vuelve hueco cuando sale).
    public string Hundida { get; set; } = "";
    /// Puntos de odio del general y turno para el que ha declarado la
    /// embestida del odio (0 = ninguno; `duelo.odioTurno`).
    public int OdioPuntos { get; set; }
    public int OdioTurno { get; set; }

    /// Plan PUBLICADO del jefe para `PlanTurno`.
    public int PlanTurno { get; set; }
    public string PlanAccion { get; set; } = "";
    public string PlanDir { get; set; } = "";
    public List<string> PlanCarril { get; } = new();
    /// Casillas laterales que barre la carga (carril de 3 de ancho).
    public List<string> PlanLaterales { get; } = new();
    public string PlanFin { get; set; } = "";
    public bool PlanHundira { get; set; }

    public void LimpiarPlan()
    {
        PlanTurno = 0;
        PlanAccion = PlanDir = PlanFin = "";
        PlanCarril.Clear();
        PlanLaterales.Clear();
        PlanHundira = false;
    }

    /// True si [c] es una grieta profunda (Izanami se hunde si termina ahí).
    public bool GrietaProfunda(ConfigMuralla m, string c) =>
        Grietas.TryGetValue(c, out var n) && n >= Math.Max(1, m.GrietaPisadas);

    public void QuitarGrieta(string c)
    {
        Grietas.Remove(c);
        GrietasTurno.Remove(c);
    }

    /// Fase del jefe según sus vidas (1, 2 o 3).
    public int FaseMuralla(ConfigMuralla m)
    {
        int v = VidasDe(RolJefe);
        return v <= m.VidasFase3 ? 3 : v <= m.VidasFase2 ? 2 : 1;
    }

    private Dictionary<string, object?> CampoMuralla(ConfigDuelo cfg, int turnoSiguiente)
    {
        var m = cfg.Muralla!;
        int fase = FaseMuralla(m);
        return new Dictionary<string, object?>
        {
            ["orientacion"] = Orientacion,
            ["fase"] = (long)fase,
            ["conoEstrecho"] = fase == 1,
            ["escudoDoble"] = fase >= m.FaseEscudoDoble,
            ["giraYCarga"] = m.FaseGiraYCarga > 0 && fase >= m.FaseGiraYCarga,
            ["alcanceCarga"] = (long)(fase >= 2 ? m.AlcanceCargaFase2 : m.AlcanceCarga),
            ["anchoCarga"] = (long)(fase >= 2 ? m.AnchoCargaFase2 : m.AnchoCarga),
            ["celdaGeneral"] = CeldaGeneralInicio,
            ["plan"] = new Dictionary<string, object?>
            {
                ["turno"] = (long)PlanTurno,
                ["accion"] = PlanAccion,
                ["dir"] = PlanDir,
                ["carril"] = PlanCarril.Cast<object?>().ToList(),
                ["laterales"] = PlanLaterales.Cast<object?>().ToList(),
                ["fin"] = PlanFin,
                ["hundira"] = PlanHundira,
            },
            ["linea"] = new Dictionary<string, object?>
            {
                ["fila"] = (long)LineaFila,
                ["letra"] = ((char)('A' + Math.Max(0, LineaFila))).ToString(),
                ["proximoAvance"] = (long)LineaProximoAvance,
                ["cada"] = (long)(fase == 3 ? m.LineaCadaFase3 : m.LineaCada),
            },
            ["grietas"] = Grietas.OrderBy(kv => kv.Key, StringComparer.Ordinal).ToDictionary(
                kv => kv.Key, kv => (object?)(long)kv.Value),
            ["grietasTurno"] = GrietasTurno.OrderBy(kv => kv.Key, StringComparer.Ordinal).ToDictionary(
                kv => kv.Key, kv => (object?)(long)kv.Value),
            ["grietaDuracion"] = (long)m.GrietaDuracion,
            ["grietaPisadas"] = (long)m.GrietaPisadas,
            ["huecos"] = Huecos.OrderBy(c => c, StringComparer.Ordinal).Cast<object?>().ToList(),
            ["hundida"] = Hundida,
            ["odio"] = new Dictionary<string, object?>
            {
                ["puntos"] = (long)OdioPuntos,
                ["max"] = (long)m.OdioMax,
                ["coste"] = (long)m.OdioCoste,
            },
        };
    }

    internal void LeerMuralla(Dictionary<string, object?> mur, int odioTurno)
    {
        Orientacion = M.Str(M.Get(mur, "orientacion")) is { Length: > 0 } o ? o : "S";
        CeldaGeneralInicio = M.Str(M.Get(mur, "celdaGeneral"));
        var plan = M.Map(M.Get(mur, "plan"));
        PlanTurno = M.Int(M.Get(plan, "turno"));
        PlanAccion = M.Str(M.Get(plan, "accion"));
        PlanDir = M.Str(M.Get(plan, "dir"));
        PlanFin = M.Str(M.Get(plan, "fin"));
        PlanHundira = M.Bool(M.Get(plan, "hundira"));
        foreach (var c in M.List(M.Get(plan, "carril"))) { var s = M.Str(c); if (s != "") PlanCarril.Add(s); }
        foreach (var c in M.List(M.Get(plan, "laterales"))) { var s = M.Str(c); if (s != "") PlanLaterales.Add(s); }
        var linea = M.Map(M.Get(mur, "linea"));
        LineaFila = M.Int(M.Get(linea, "fila"));
        LineaProximoAvance = M.Int(M.Get(linea, "proximoAvance"));
        foreach (var kv in M.Map(M.Get(mur, "grietas"))) Grietas[kv.Key] = M.Int(kv.Value);
        foreach (var kv in M.Map(M.Get(mur, "grietasTurno"))) GrietasTurno[kv.Key] = M.Int(kv.Value);
        foreach (var c in M.List(M.Get(mur, "huecos"))) { var s = M.Str(c); if (s != "") Huecos.Add(s); }
        Hundida = M.Str(M.Get(mur, "hundida"));
        OdioPuntos = M.Int(M.Get(M.Map(M.Get(mur, "odio")), "puntos"));
        OdioTurno = odioTurno;
    }
}

// ═════════════════════════════════════════════════════════════════════════════
// LÓGICA
// ═════════════════════════════════════════════════════════════════════════════
public static partial class MotorDuelo
{
    // ── Direcciones ──────────────────────────────────────────────────────────
    private static (int dr, int dc) Vec(string dir) => dir switch
    {
        "N" => (-1, 0),
        "S" => (1, 0),
        "E" => (0, 1),
        "O" => (0, -1),
        _ => (1, 0),
    };

    private static string Derecha(string dir) => dir switch { "N" => "E", "E" => "S", "S" => "O", _ => "N" };
    private static string Izquierda(string dir) => dir switch { "N" => "O", "O" => "S", "S" => "E", _ => "N" };

    /// Posición de [x] vista desde [p] mirando a [dir]: (adelante, a la derecha).
    public static (int fwd, int lat)? Relativa(string p, string dir, string x)
    {
        if (!Parse(p, out int pr, out int pc) || !Parse(x, out int xr, out int xc)) return null;
        var (fr, fc) = Vec(dir);
        int dr = xr - pr, dc = xc - pc;
        int fwd = dr * fr + dc * fc;
        int lat = dr * fc - dc * fr;     // derecha de (fr, fc) = (fc, -fr)
        return (fwd, lat);
    }

    /// True si [x] está en el cono FRONTAL de 90° (el que para los golpes).
    public static bool EnFrente(string p, string dir, string x) =>
        Relativa(p, dir, x) is { } r && r.fwd > 0 && Math.Abs(r.lat) <= r.fwd;

    private static string? Desplazar(string coord, string dir, int n, int filas, int columnas)
    {
        if (!Parse(coord, out int r, out int c)) return null;
        var (dr, dc) = Vec(dir);
        r += dr * n; c += dc * n;
        if (r < 0 || r >= filas || c < 1 || c > columnas) return null;
        return $"{(char)('A' + r)}{c}";
    }

    private static int Fila(string coord) => Parse(coord, out int r, out _) ? r : -1;
    private static int Col(string coord) => Parse(coord, out _, out int c) ? c : -1;

    // ── Casillas que solo puede pisar Izanami ────────────────────────────────
    /// Casillas BLOQUEADAS dinámicas de la muralla para el general: la línea
    /// y todo lo que queda detrás, y los huecos. Nunca la casilla del jefe
    /// (los soldados le abren paso y el hueco no existe mientras esté encima).
    public static HashSet<string> BloqueadasMuralla(ConfigDuelo cfg, EstadoDuelo e, string? celdaJefe, int filas, int columnas)
    {
        var res = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!cfg.EsMuralla) return res;
        for (int r = 0; r <= Math.Min(e.LineaFila, filas - 1); r++)
            for (int c = 1; c <= columnas; c++)
                res.Add($"{(char)('A' + r)}{c}");
        res.UnionWith(e.Huecos);
        if (celdaJefe != null) res.Remove(celdaJefe);
        return res;
    }

    /// ¿Puede el general pisar [c]? (estática + dinámica).
    private static Func<string, bool> PasaGeneral(
        ConfigDuelo cfg, EstadoDuelo e, string? celdaJefe, Func<string, bool> transitable, int filas, int columnas)
    {
        var est = Bloqueadas(cfg);
        var din = BloqueadasMuralla(cfg, e, celdaJefe, filas, columnas);
        return c => transitable(c) && !est.Contains(c) && !din.Contains(c);
    }

    /// Casillas a las que puede ir el general este turno (sin el origen). La
    /// casilla del jefe se puede alcanzar pero no atravesar. Lo usa la
    /// validación del paso A para revertir lo que no cumpla (y el cliente
    /// calcula lo mismo).
    public static HashSet<string> DestinosGeneralMuralla(
        ConfigDuelo cfg, EstadoDuelo e, string origen, int mov, string? celdaJefe,
        Func<string, bool> transitable, int filas, int columnas)
    {
        var pasa = PasaGeneral(cfg, e, celdaJefe, transitable, filas, columnas);
        var visto = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { [origen] = 0 };
        var cola = new Queue<string>();
        cola.Enqueue(origen);
        while (cola.Count > 0)
        {
            var c = cola.Dequeue();
            if (visto[c] >= mov) continue;
            if (celdaJefe != null && string.Equals(c, celdaJefe, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(c, origen, StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var n in Vecinos(c, filas, columnas))
            {
                if (visto.ContainsKey(n) || !pasa(n)) continue;
                visto[n] = visto[c] + 1;
                cola.Enqueue(n);
            }
        }
        visto.Remove(origen);
        return visto.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    // ── Estado inicial ───────────────────────────────────────────────────────
    private static void InicialMuralla(
        ConfigDuelo cfg, EstadoDuelo e, Dictionary<string, List<Dictionary<string, object?>>> tablero)
    {
        var m = cfg.Muralla!;
        e.Orientacion = m.OrientacionInicial;
        e.LineaFila = m.LineaFilaInicial;
        e.LineaProximoAvance = m.LineaPrimerAvance;
        e.CeldaGeneralInicio = CeldaDe(tablero, e.IdDe(EstadoDuelo.RolCazador)) ?? "";
    }

    // ── Plan del turno ───────────────────────────────────────────────────────
    /// Carril de una carga desde [origen] hacia [dir]: hasta [alcance]
    /// casillas, parando antes del borde, de una casilla bloqueada, de un
    /// hueco o de la línea.
    private static List<string> CarrilMuralla(
        ConfigDuelo cfg, EstadoDuelo e, string origen, string dir, int alcance,
        Func<string, bool> transitable, int filas, int columnas)
    {
        var est = Bloqueadas(cfg);
        var res = new List<string>();
        for (int k = 1; k <= alcance; k++)
        {
            var c = Desplazar(origen, dir, k, filas, columnas);
            if (c == null || !transitable(c) || est.Contains(c) || e.Huecos.Contains(c)) break;
            if (Fila(c) <= e.LineaFila) break;
            res.Add(c);
        }
        return res;
    }

    /// Lo que hará el jefe en [jefe] mirando a [e.Orientacion] si el general
    /// está en [general]: avanzar (en su cono de carga) o girar hacia él.
    private static (string accion, string dir) DecidirAccionMuralla(
        ConfigDuelo cfg, EstadoDuelo e, string jefe, string general,
        Func<string, bool> transitable, int filas, int columnas)
    {
        var m = cfg.Muralla!;
        var rel = Relativa(jefe, e.Orientacion, general) ?? (1, 0);
        bool estrecho = e.FaseMuralla(m) == 1;
        bool enCono = rel.fwd > 0 && (!estrecho || Math.Abs(rel.lat) <= rel.fwd);
        if (enCono) return (EstadoDuelo.AccionAvanzar, e.Orientacion);
        if (Math.Abs(rel.fwd) <= 1 && Math.Abs(rel.lat) <= 1) return (EstadoDuelo.AccionBarrido, e.Orientacion);
        var giro = m.FaseGiraYCarga > 0 && e.FaseMuralla(m) >= m.FaseGiraYCarga
            ? EstadoDuelo.AccionGiraYCarga
            : EstadoDuelo.AccionGirar;
        if (rel.lat > 0) return (giro, Derecha(e.Orientacion));
        if (rel.lat < 0) return (giro, Izquierda(e.Orientacion));
        // Justo detrás: hacia el lado con más espacio (en empate, la derecha).
        var der = Derecha(e.Orientacion);
        var izq = Izquierda(e.Orientacion);
        int Espacio(string d) => CarrilMuralla(cfg, e, jefe, d, 99, transitable, filas, columnas).Count;
        return (giro, Espacio(izq) > Espacio(der) ? izq : der);
    }

    /// Laterales que barre una carga de 3 de ancho: los de cada casilla del
    /// carril y los de la de salida (no los de una casilla bloqueada, un
    /// hueco o la línea). Vacío si el ancho de su fase es 1.
    private static List<string> LateralesCarga(
        ConfigDuelo cfg, EstadoDuelo e, string origen, string dir, IReadOnlyList<string> carril,
        Func<string, bool> transitable, int filas, int columnas)
    {
        var m = cfg.Muralla!;
        int ancho = e.FaseMuralla(m) >= 2 ? m.AnchoCargaFase2 : m.AnchoCarga;
        var res = new List<string>();
        if (ancho < 3 || carril.Count == 0) return res;
        var est = Bloqueadas(cfg);
        foreach (var c in new[] { origen }.Concat(carril))
            foreach (var l in new[] { Desplazar(c, Derecha(dir), 1, filas, columnas), Desplazar(c, Izquierda(dir), 1, filas, columnas) })
                if (l != null && transitable(l) && !est.Contains(l) && !e.Huecos.Contains(l)
                    && Fila(l) > e.LineaFila && !res.Contains(l, StringComparer.OrdinalIgnoreCase))
                    res.Add(l);
        return res;
    }

    /// Casillas que carga: hasta la altura del general (sin pasarse), como
    /// mucho el alcance de su fase.
    private static int AlcancePlan(ConfigDuelo cfg, EstadoDuelo e, string jefe, string general, string? dir = null)
    {
        var m = cfg.Muralla!;
        int max = e.FaseMuralla(m) >= 2 ? m.AlcanceCargaFase2 : m.AlcanceCarga;
        int fwd = Relativa(jefe, dir ?? e.Orientacion, general)?.fwd ?? max;
        return Math.Clamp(fwd, 1, max);
    }

    /// Publica en [e] el plan del jefe para [turno] según las posiciones del
    /// tablero AL EMPEZAR ese turno.
    private static void PrepararPlanMuralla(
        ConfigDuelo cfg, EstadoDuelo e, int turno,
        Dictionary<string, List<Dictionary<string, object?>>> tablero,
        Func<string, bool> transitable, int filas, int columnas)
    {
        e.LimpiarPlan();
        var jefe = CeldaDe(tablero, e.IdDe(EstadoDuelo.RolJefe));
        var general = CeldaDe(tablero, e.IdDe(EstadoDuelo.RolCazador));
        if (jefe == null || general == null) return;
        e.PlanTurno = turno;
        if (e.Paralizado(turno))
        {
            e.PlanAccion = EstadoDuelo.AccionAturdida;
            e.PlanFin = jefe;
            return;
        }
        var (accion, dir) = DecidirAccionMuralla(cfg, e, jefe, general, transitable, filas, columnas);
        e.PlanAccion = accion;
        e.PlanDir = dir;
        if (accion == EstadoDuelo.AccionAvanzar || accion == EstadoDuelo.AccionGiraYCarga)
        {
            var m = cfg.Muralla!;
            e.PlanCarril.AddRange(CarrilMuralla(cfg, e, jefe, dir, AlcancePlan(cfg, e, jefe, general, dir),
                transitable, filas, columnas));
            e.PlanFin = e.PlanCarril.Count > 0 ? e.PlanCarril[^1] : jefe;
            e.PlanHundira = e.PlanCarril.Count > 0 && e.GrietaProfunda(m, e.PlanFin);
            e.PlanLaterales.AddRange(LateralesCarga(cfg, e, jefe, dir, e.PlanCarril, transitable, filas, columnas));
        }
        else
        {
            e.PlanFin = jefe;
        }
    }

    // ── Resolución ───────────────────────────────────────────────────────────
    private static List<(string coord, Dictionary<string, object?> carta)> ResolverMuralla(
        ConfigDuelo cfg, EstadoDuelo e, int turno, int semilla,
        Dictionary<string, List<Dictionary<string, object?>>> t,
        Func<string, bool> transitable, int filas, int columnas)
    {
        var m = cfg.Muralla!;
        e.Eventos.Clear();
        e.UltimoTurno = turno;
        var apartadas = new List<(string, Dictionary<string, object?>)>();
        bool Igual(string? a, string? b) => a != null && b != null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        var rng = new Random(PlanificadorBombardeo.Semilla(semilla, "duelo-muralla", turno));

        string? idJ = e.IdDe(EstadoDuelo.RolJefe);
        string? idA = e.IdDe(EstadoDuelo.RolCazador);
        string Nombre(string? id) => id != null && e.Nombres.TryGetValue(id, out var n) && n != "" ? n : "Carta";
        string jefeN = Nombre(idJ).Replace("General ", "");
        string genN = Nombre(idA).Replace("General ", "");
        int vidasJefeAntes = e.VidasDe(EstadoDuelo.RolJefe);
        int faseAntes = e.FaseMuralla(m);

        var J0 = CeldaDe(t, idJ);
        var A = CeldaDe(t, idA);
        var A0 = e.CeldaGeneralInicio != "" ? e.CeldaGeneralInicio : A;
        bool vivoA = A != null && idA != null && e.Vidas.GetValueOrDefault(idA) > 0;
        bool odioListo = e.OdioTurno == turno && e.OdioPuntos >= m.OdioCoste;
        bool odioGastado = false, contacto = false, heridoA = false;
        var est = Bloqueadas(cfg);

        // Casilla libre para el general (pisable, sin el jefe, sin cartas).
        bool LibreGeneral(string? c)
        {
            if (c == null || !transitable(c) || est.Contains(c) || e.Huecos.Contains(c)) return false;
            if (Fila(c) <= e.LineaFila) return false;
            var j = CeldaDe(t, idJ);
            if (Igual(c, j)) return false;
            return !t.TryGetValue(c, out var l) || l.Count == 0 || l.All(x => M.Str(M.Get(x, "instanceId")) == idA);
        }

        // Saca al general de la casilla del jefe: [preferidas] en orden y, si
        // no, cualquier vecina libre. Devuelve dónde acaba.
        string? Despedir(string desde, IEnumerable<string?> preferidas)
        {
            var destino = preferidas.FirstOrDefault(LibreGeneral)
                          ?? Vecinos(desde, filas, columnas).Where(LibreGeneral).OrderBy(_ => rng.Next()).FirstOrDefault();
            if (destino == null || idA == null) return null;
            Mover(t, idA, destino);
            return destino;
        }

        void HerirGeneral(string motivo, string coord)
        {
            if (heridoA || idA == null || e.Vidas.GetValueOrDefault(idA) <= 0) return;
            heridoA = true;
            e.Vidas[idA]--;
            e.Eventos.Add(new("herida", $"{motivo}: {genN} pierde una vida (le quedan {e.Vidas[idA]})", coord, true));
        }

        // El general arrollado de frente sale despedido hacia delante de ella.
        void Arrollado(string celdaJefe, string dirJefe, string motivo)
        {
            contacto = true;
            HerirGeneral(motivo, celdaJefe);
            var d = Despedir(celdaJefe, new[]
            {
                Desplazar(celdaJefe, dirJefe, 1, filas, columnas),
                Desplazar(celdaJefe, Derecha(dirJefe), 1, filas, columnas),
                Desplazar(celdaJefe, Izquierda(dirJefe), 1, filas, columnas),
            });
            if (d != null) e.Eventos.Add(new("empuje", $"💨 {genN} sale despedido a {d}", d, true));
        }

        // El general hiere al jefe: pierde un núcleo, el general sale
        // despedido delante de ella y ella se vuelve hacia él.
        void GolpearJefe(string celdaJefe, string motivo)
        {
            if (idJ == null) return;
            contacto = true;
            e.Vidas[idJ]--;
            e.ParalizadoHasta = 0;
            e.Eventos.Add(new("golpe",
                $"⚔ {motivo}: {jefeN} pierde un núcleo de BioZero (le quedan {e.Vidas[idJ]})", celdaJefe));
            if (e.Vidas[idJ] <= 0) return;
            var d = Despedir(celdaJefe, new[]
            {
                Desplazar(celdaJefe, e.Orientacion, 1, filas, columnas),
                Desplazar(celdaJefe, Derecha(e.Orientacion), 1, filas, columnas),
                Desplazar(celdaJefe, Izquierda(e.Orientacion), 1, filas, columnas),
            });
            if (d != null)
            {
                var nueva = DireccionCarga(celdaJefe, d);
                if (nueva != null) e.Orientacion = nueva;
                e.Eventos.Add(new("empuje", $"💨 {genN} sale despedido a {d} y {jefeN} se vuelve hacia él", d));
            }
        }

        // Choque de FRENTE: con el odio listo, la herida es para ella.
        void ChoqueFrontal(string celdaJefe, string dirJefe, string motivo)
        {
            if (odioListo && !odioGastado)
            {
                odioGastado = true;
                e.OdioPuntos -= m.OdioCoste;
                e.Eventos.Add(new("odio", $"🔥 ¡EMBESTIDA DEL ODIO! {genN} atraviesa el escudo de {jefeN}", celdaJefe));
                GolpearJefe(celdaJefe, "El odio rompe su guardia");
            }
            else Arrollado(celdaJefe, dirJefe, motivo);
        }

        // ── 1) Acción de Izanami ─────────────────────────────────────────────
        if (J0 != null && idJ != null && vivoA)
        {
            // Plan publicado (o, si falta, recalculado desde el inicio del turno).
            if (e.PlanTurno != turno && A0 != null)
            {
                var tInicio = new Dictionary<string, List<Dictionary<string, object?>>>(StringComparer.OrdinalIgnoreCase);
                tInicio[J0] = new() { new() { ["instanceId"] = idJ } };
                if (!tInicio.TryGetValue(A0, out var l0)) tInicio[A0] = l0 = new();
                l0.Add(new() { ["instanceId"] = idA });
                PrepararPlanMuralla(cfg, e, turno, tInicio, transitable, filas, columnas);
            }

            if (e.Paralizado(turno))
            {
                if (Igual(A, J0)) GolpearJefe(J0, $"{genN} golpea a {jefeN} hundida en la grieta");
                else e.Eventos.Add(new("recupera", $"😤 {jefeN} sale de la grieta: {genN} no llegó a tiempo", J0, true));
            }
            else if (e.PlanAccion == EstadoDuelo.AccionGirar)
            {
                var nueva = e.PlanDir;
                if (Igual(A, J0) && A0 != null)
                {
                    // El golpe se mide contra hacia dónde miraba (con el escudo
                    // doble de la fase 3, contra hacia dónde se gira).
                    bool doble = e.FaseMuralla(m) >= m.FaseEscudoDoble;
                    var mira = doble ? nueva : e.Orientacion;
                    e.Orientacion = nueva;
                    if (EnFrente(J0, mira, A0))
                        ChoqueFrontal(J0, mira, doble
                            ? $"🛡 El escudo doble de {jefeN} te recibe"
                            : $"🛡 {genN} choca contra el escudo de {jefeN}");
                    else
                    {
                        var rel = Relativa(J0, mira, A0);
                        GolpearJefe(J0, rel is { } r && r.fwd < 0
                            ? $"{genN} la golpea por la espalda"
                            : $"{genN} la golpea por el flanco mientras se gira");
                    }
                }
                else
                {
                    e.Orientacion = nueva;
                    e.Eventos.Add(new("gira", $"↻ {jefeN} se gira hacia el {NombreDir(nueva)}", J0));
                }
            }
            else if (e.PlanAccion == EstadoDuelo.AccionBarrido)
            {
                // Barrido de escudo: su casilla y las 8 de alrededor.
                bool cerca = A != null && Math.Max(Math.Abs(Fila(A) - Fila(J0)), Math.Abs(Col(A) - Col(J0))) <= 1;
                if (Igual(A, J0) && odioListo)
                    ChoqueFrontal(J0, e.Orientacion, "");
                else if (cerca && A != null)
                {
                    contacto = true;
                    HerirGeneral($"🛡 El barrido del escudo de {jefeN} te alcanza", A);
                    // Despedido 2 casillas en la dirección en que estaba (o 1).
                    var desde = Igual(A, J0) ? (A0 ?? A) : A;
                    var dir = DireccionCarga(J0, desde) ?? e.Orientacion;
                    var d = Despedir(A, new[]
                    {
                        Desplazar(J0, dir, 2, filas, columnas),
                        Desplazar(J0, dir, 1, filas, columnas),
                    });
                    if (d != null) e.Eventos.Add(new("empuje", $"💨 {genN} sale despedido a {d}", d, true));
                }
                else
                    e.Eventos.Add(new("barrido", $"🛡 {jefeN} barre con el escudo a su alrededor, pero no te alcanza", J0));
            }
            else if (e.PlanAccion == EstadoDuelo.AccionAvanzar || e.PlanAccion == EstadoDuelo.AccionGiraYCarga)
            {
                if (e.PlanAccion == EstadoDuelo.AccionGiraYCarga && e.PlanDir != "")
                {
                    e.Orientacion = e.PlanDir;
                    e.Eventos.Add(new("gira", $"↻ {jefeN} se gira hacia el {NombreDir(e.PlanDir)} y carga", J0));
                }
                var dir = e.Orientacion;
                // El carril PUBLICADO (lo que ha visto el jugador). Si faltara,
                // se recalcula hasta la altura a la que estaba el general.
                var carril = e.PlanCarril.Count > 0 || e.PlanFin != ""
                    ? e.PlanCarril.ToList()
                    : CarrilMuralla(cfg, e, J0, dir, A0 != null ? AlcancePlan(cfg, e, J0, A0, dir) : m.AlcanceCarga,
                        transitable, filas, columnas);
                int k = A == null ? -1 : carril.FindIndex(c => Igual(c, A));
                // Carril de 3 de ancho: alcanzado en un lateral → se detiene a su altura.
                if (k < 0 && A != null)
                {
                    var lat = e.PlanCarril.Count > 0 || e.PlanFin != ""
                        ? e.PlanLaterales.ToList()
                        : LateralesCarga(cfg, e, J0, dir, carril, transitable, filas, columnas);
                    if (lat.Any(c => Igual(c, A)))
                    {
                        int altura = Relativa(J0, dir, A)?.fwd ?? 0;
                        if (altura <= 0) k = -2;     // a la salida: la roza sin moverse
                        else k = Math.Min(altura, carril.Count) - 1;
                    }
                }
                if (Igual(A, J0) || k == -2)
                {
                    // Se ha metido en su casilla (o a su lado) mientras cargaba.
                    ChoqueFrontal(J0, dir, $"🛡 {genN} choca de frente contra {jefeN}");
                }
                else if (k >= 0)
                {
                    var choque = carril[k];
                    Mover(t, idJ, choque);
                    ChoqueFrontal(choque, dir, Igual(choque, A)
                        ? $"🐂 {jefeN} carga y arrolla a {genN} en {choque}"
                        : $"🐂 La carga de {jefeN} barre a {genN} en {A}");
                }
                else if (carril.Count > 0)
                {
                    Mover(t, idJ, carril[^1]);
                    e.Eventos.Add(new("carga", $"🐂 {jefeN} avanza hasta {carril[^1]}", carril[^1]));
                }
                else
                {
                    e.Eventos.Add(new("planta", $"🛡 {jefeN} no puede avanzar y se planta", J0));
                }

                // ¿Ha terminado la carga sobre una grieta?
                var fin = CeldaDe(t, idJ);
                if (fin != null && !Igual(fin, J0) && e.GrietaProfunda(m, fin) && e.Vidas.GetValueOrDefault(idJ) > 0)
                {
                    e.QuitarGrieta(fin);
                    e.Hundida = fin;
                    e.ParalizadoHasta = turno + Math.Max(1, m.TurnosAturdida);
                    e.Eventos.Add(new("hundida",
                        $"🕳 ¡El suelo se hunde bajo {jefeN} en {fin}! Queda ATURDIDA: entra en su casilla y golpéala", fin));
                }
            }
        }

        // ── 2) Odio que no llegó a usarse ───────────────────────────────────
        if (odioListo && !odioGastado)
            e.Eventos.Add(new("odio", $"🔥 {genN} no llegó a chocar con {jefeN}: conserva su odio", A ?? ""));
        e.OdioTurno = 0;

        // ── 3) La casilla hundida se vuelve hueco cuando ella sale ──────────
        var jefeAhora = CeldaDe(t, idJ);
        if (e.Hundida != "" && !Igual(e.Hundida, jefeAhora) && !e.Paralizado(turno + 1))
        {
            e.Huecos.Add(e.Hundida);
            e.Eventos.Add(new("hueco", $"🕳 {e.Hundida} se derrumba: ahora es un hueco", e.Hundida));
            e.Hundida = "";
        }

        // ── 4) Grietas: donde ha terminado Alexander ────────────────────────
        if (m.GrietaDuracion > 0)
            foreach (var g in e.GrietasTurno.Where(kv => turno - kv.Value >= m.GrietaDuracion).Select(kv => kv.Key).ToList())
                e.QuitarGrieta(g);
        if (vivoA && !contacto && A != null && !Igual(A, jefeAhora) && !e.Huecos.Contains(A))
        {
            int nivel = Math.Min(Math.Max(1, m.GrietaPisadas), e.Grietas.GetValueOrDefault(A) + 1);
            e.Grietas[A] = nivel;
            e.GrietasTurno[A] = turno;
            if (nivel == Math.Max(1, m.GrietaPisadas) && nivel > 1)
                e.Eventos.Add(new("grieta", $"⚡⚡ {A} es ya una GRIETA PROFUNDA: si {jefeN} termina su carga ahí, se hundirá", A));
        }

        // ── 5) La línea biónica avanza ──────────────────────────────────────
        if (turno >= e.LineaProximoAvance && e.LineaFila < filas - 1)
        {
            e.LineaFila++;
            e.LineaProximoAvance = turno + Math.Max(1, e.FaseMuralla(m) == 3 ? m.LineaCadaFase3 : m.LineaCada);
            foreach (var g in e.Grietas.Keys.Where(c => Fila(c) <= e.LineaFila).ToList()) e.QuitarGrieta(g);
            e.Huecos.RemoveWhere(c => Fila(c) <= e.LineaFila);
            var letra = (char)('A' + e.LineaFila);
            e.Eventos.Add(new("linea", $"🛡 La línea biónica avanza hasta la fila {letra}", ""));
            var g2 = CeldaDe(t, idA);
            if (g2 != null && idA != null && e.Vidas.GetValueOrDefault(idA) > 0 && Fila(g2) <= e.LineaFila)
            {
                HerirGeneral("🛡 La línea biónica te alcanza", g2);
                // Retrocede: hacia el sur y, si no puede, a la casilla libre más
                // cercana por delante de la línea. Solo cae si no queda ninguna.
                var atras = Desplazar(g2, "S", 1, filas, columnas);
                var destino = new[]
                    {
                        atras,
                        atras == null ? null : Desplazar(atras, "E", 1, filas, columnas),
                        atras == null ? null : Desplazar(atras, "O", 1, filas, columnas),
                    }
                    .FirstOrDefault(LibreGeneral)
                    ?? TodasLasCeldas(filas, columnas)
                        .Where(LibreGeneral)
                        .OrderBy(c => Math.Abs(Fila(c) - Fila(g2)) + Math.Abs(Col(c) - Col(g2)))
                        .ThenBy(c => c, StringComparer.Ordinal)
                        .FirstOrDefault();
                if (destino != null)
                {
                    Mover(t, idA, destino);
                    e.Eventos.Add(new("empuje", $"💨 {genN} retrocede a {destino}", destino, true));
                }
                else if (e.Vidas[idA] > 0)
                {
                    e.Vidas[idA] = 0;
                    e.Eventos.Add(new("caida", $"🌑 {genN} no tiene a dónde retroceder y cae de la muralla", g2, true));
                }
            }
            // Si la línea alcanza a Izanami, sus soldados la empujan hacia delante.
            var j2 = CeldaDe(t, idJ);
            if (j2 != null && Fila(j2) <= e.LineaFila)
            {
                var delante = Desplazar(j2, "S", 1, filas, columnas);
                if (delante != null && transitable(delante) && !est.Contains(delante) && !e.Huecos.Contains(delante)
                    && !t.ContainsKey(delante))
                    Mover(t, idJ, delante);
            }
        }

        // ── 6) Odio por las heridas ─────────────────────────────────────────
        if (heridoA && idA != null && e.Vidas.GetValueOrDefault(idA) > 0 && e.OdioPuntos < m.OdioMax)
        {
            e.OdioPuntos++;
            e.Eventos.Add(new("odio", e.OdioPuntos >= m.OdioCoste
                ? $"🔥 El odio de {genN} arde ({e.OdioPuntos}/{m.OdioMax}): ya puede lanzar la EMBESTIDA DEL ODIO"
                : $"🔥 El odio de {genN} crece ({e.OdioPuntos}/{m.OdioMax})", ""));
        }

        // ── 7) Cambios de fase ──────────────────────────────────────────────
        int vidasJefe = e.VidasDe(EstadoDuelo.RolJefe);
        int fase = e.FaseMuralla(m);
        if (vidasJefe > 0 && vidasJefe < vidasJefeAntes && fase != faseAntes)
        {
            if (fase == 2)
                e.Eventos.Add(new("fase",
                    $"⚡ {jefeN} alza el ESCUDO DOBLE: cubre también el lado hacia el que gira (solo la espalda la hiere) " +
                    $"y carga {m.AlcanceCargaFase2} casillas contra todo lo que tenga delante", "", true));
            else if (fase == 3)
                e.Eventos.Add(new("fase",
                    $"⚡ Último núcleo: {jefeN} GIRA Y CARGA en el mismo turno y la línea avanza cada turno. " +
                    "Usa tus grietas o tu odio", "", true));
        }

        // ── 8) Muertes ──────────────────────────────────────────────────────
        foreach (var (id, rol) in e.Roles.ToList())
        {
            if (e.Vidas.GetValueOrDefault(id) > 0) continue;
            var c = CeldaDe(t, id);
            if (c == null) continue;
            Quitar(t, id);
            bool malo = rol != EstadoDuelo.RolJefe;
            e.Eventos.Add(new("muerte", malo ? $"💀 {Nombre(id)} ha caído" : $"🏆 ¡{Nombre(id)} ha caído!", c, malo));
        }

        // ── 9) Sin combate normal y posición de partida del turno siguiente ─
        var cjFinal = CeldaDe(t, idJ);
        if (cjFinal != null && t.TryGetValue(cjFinal, out var lstJ) && lstJ.Count > 1)
        {
            var carta = lstJ.First(c => M.Str(M.Get(c, "instanceId")) == idJ);
            lstJ.Remove(carta);
            apartadas.Add((cjFinal, carta));
        }
        foreach (var k in t.Keys.Where(k => t[k].Count == 0).ToList()) t.Remove(k);
        e.CeldaGeneralInicio = CeldaDe(t, idA) ?? "";
        e.LimpiarPlan();
        return apartadas;
    }

    private static string NombreDir(string dir) => dir switch
    {
        "N" => "norte",
        "S" => "sur",
        "E" => "este",
        "O" => "oeste",
        _ => dir,
    };
}
