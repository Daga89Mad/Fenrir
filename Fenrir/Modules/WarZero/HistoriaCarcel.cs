using System.Security.Cryptography;
using System.Text;

// ─────────────────────────────────────────────────────────────────────────────
// HistoriaCarcel.cs
//
// LA CÁRCEL BIÓNICA de «El hijo del odio · parte 1» (nefilim_1). Catálogo de
// DATOS + lógica pura (sin Firestore), igual que HistoriaTuneles.cs. Lo
// consumen:
//   • WarZeroHistoria.CrearPartidaHistoriaAsync → estado inicial (`carcel`),
//     muros como casillas bloqueadas y mapa por defecto si no existe en
//     Firestore (MapaPorDefecto).
//   • WarZeroHistoriaReglas (paso A) → ODIO de Alexander (+fuerza en el
//     combate de este turno) y validación de puertas: lo que no cumpla las
//     reglas vuelve a su casilla.
//   • WarZeroHistoriaReglas (paso B) → muertes, núcleos, pistas, puertas,
//     caducidad, trile, alarma y refuerzos (ResolverTurno).
//   • WarZeroCarcel.ConstruirJugadaCarcel → la IA de los guardias: todos se
//     quedan quietos salvo el PORTADOR, que huye si Alexander se le acerca.
//   • WarZeroCarcel.DeclararOdioCarcelAsync → POST /warzero/carcel/odio.
//   • EvaluarFinHistoria → fuga (gana el jugador) o alarma máxima (gana el bot).
//
// REGLAS
//   • La cárcel es una cadena de SALAS separadas por MUROS (casillas
//     bloqueadas) y PUERTAS. Cada sala tiene sus guardias biónicos y uno o
//     varios NÚCLEOS (slots). Cada núcleo lo lleva UN guardia de la sala, que
//     nadie sabe cuál es.
//   • Cada guardia muestra su % de llevar el núcleo. Empieza según su PESO
//     (un capitán es más probable que un soldado) y es un posterior honesto:
//     matar a uno que no lo llevaba, una pista o un intercambio lo actualizan.
//   • Matar al PORTADOR da el núcleo a Alexander. Desde ese momento corre el
//     tiempo: tiene `Duracion` turnos para entrar en la puerta (0 = sin
//     límite). Si no llega, el núcleo se APAGA, sube la alarma y otro guardia
//     vivo de la sala recibe uno nuevo (si no queda ninguno, aparece uno).
//   • Matar a uno EQUIVOCADO solo cuesta tiempo: sube la alarma y, en las
//     salas con `Pistas`, su terminal revela una pista VERDADERA sobre el
//     portador (distancia a la puerta, fila, tipo de guardia o distancia a
//     Alexander), elegida para descartar más o menos la mitad.
//   • PUERTAS: solo se puede ENTRAR en la puerta de la sala llevando todos sus
//     núcleos, y entrar termina el movimiento. Si hay refuerzos dentro, hay
//     que vencerlos. Al quedar Alexander vivo en la puerta, se abre para
//     siempre y se consumen los núcleos. La puerta final es la fuga.
//   • HUIDA (`PortadorHuye`): al empezar el turno, si Alexander está a 2
//     casillas o menos (en diagonal también) del portador, este se aleja una
//     casilla. Solo huye el portador: es la pista que se gana mirando.
//   • TRILE (`Intercambios`): al cerrar cada turno de esa sala, N parejas de
//     guardias cercanos (≤ 2 casillas) se INTERCAMBIAN los núcleos. Se publica
//     quién con quién y los % se intercambian igual.
//   • ALARMA: sube cada turno, con cada guardia equivocado y con cada núcleo
//     apagado. Al cruzar cada umbral de `Refuerzos` aparece una carta en la
//     puerta de la sala activa. Con `AlarmaMax` la cárcel se cierra: derrota.
//   • ODIO: una vez por batalla, Alexander suma `OdioFuerza` de fuerza en el
//     combate del turno en que lo desata.
//
// SECRETO: quién lleva cada núcleo NO viaja en claro. Se guarda como índice
// dentro de la lista de guardias del slot sumado (módulo N) a una clave
// derivada con HMAC de la semilla, un contador de versión y el secreto del
// servidor (variable WZ_CARCEL_SECRETO). La versión cambia en CADA
// resolución, así que el valor guardado no revela nada aunque se lea el
// documento de la partida.
// ─────────────────────────────────────────────────────────────────────────────

/// Un guardia de la configuración: carta, casilla de inicio, PESO (cuánto más
/// probable es que lleve el núcleo) y slot de núcleo al que pertenece.
public sealed record GuardiaCarcel(string Coord, string CartaId, int Peso = 1, int Slot = 0);

/// Un NÚCLEO de una sala. `Duracion` = turnos para llegar a la puerta desde
/// que se consigue (0 = sin límite). `CartaRespawn` = guardia que aparece con
/// un núcleo nuevo si se apaga y no queda nadie vivo de ese slot.
public sealed record SlotNucleo(string Nombre, string Icono, int Duracion, string CartaRespawn);

/// Una sala de la cárcel.
public sealed record SalaCarcel(
    string Nombre,
    /// Casillas por las que se puede andar dentro de la sala.
    IReadOnlyList<string> Celdas,
    /// Casilla de la PUERTA que sale de esta sala.
    string Puerta,
    IReadOnlyList<GuardiaCarcel> Guardias,
    IReadOnlyList<SlotNucleo> Slots,
    /// Casilla donde aparece un guardia nuevo si hace falta.
    string Respawn,
    bool Pistas = false,
    bool PortadorHuye = false,
    /// Intercambios de núcleo por turno (trile). 0 = sin trile.
    int Intercambios = 0);

/// Refuerzo que aparece en la puerta activa al alcanzar `Umbral` de alarma.
public sealed record RefuerzoAlarma(int Umbral, string CartaId, string Nombre);

/// Configuración completa de una cárcel.
public sealed record ConfigCarcel(
    /// Mapa por defecto (si el documento de `Mapas` no existe).
    string MapaId,
    int Filas,
    int Columnas,
    /// Casillas impracticables (se publican como `celdasBloqueadas`).
    IReadOnlyList<string> Muros,
    IReadOnlyList<SalaCarcel> Salas,
    /// Casillas del exterior (decorativas: cruzar la puerta final es la fuga).
    IReadOnlyList<string> Salida,
    IReadOnlyList<RefuerzoAlarma> Refuerzos,
    /// Cuarteles NOMINALES (dentro de muros): jugador y bot.
    string CuartelJugador,
    string CuartelBot,
    int AlarmaMax = 40,
    int AlarmaPorTurno = 1,
    int AlarmaPorError = 1,
    int AlarmaPorApagado = 2,
    int OdioFuerza = 40);

// ═════════════════════════════════════════════════════════════════════════════
// ESTADO (campo `carcel` de la partida)
// ═════════════════════════════════════════════════════════════════════════════

public sealed class GuardiaEstado
{
    public string Id = "";
    public int Sala;
    public int Slot;
    public string Nombre = "";
    public string CartaId = "";
    public int Peso = 1;
    public int Tipo = 1;
    public bool Vivo = true;
    /// Probabilidad (0..1) de llevar el núcleo de su slot.
    public double Prob;
    /// Última casilla conocida (para detectar huidas).
    public string Coord = "";
}

public sealed class SlotEstado
{
    /// "guardia" (lo lleva un guardia) | "alexander" | "usado".
    public string Estado = EstadoGuardia;
    /// Turno hasta el que vale el núcleo de Alexander (0 = sin límite).
    public int Caduca;

    public const string EstadoGuardia = "guardia";
    public const string EstadoAlexander = "alexander";
    public const string EstadoUsado = "usado";
}

/// Índice del portador, enmascarado (ver cabecera).
public sealed class SecretoPortador
{
    public int V;
    public int C;
    public int N;
}

public sealed class PistaCarcel
{
    public int Turno;
    public int Sala;
    public int Slot;
    public string Texto = "";
}

public sealed class EventoCarcel
{
    public string Tipo = "";
    public string Texto = "";
    public string Coord = "";
    public bool Malo;
}

public sealed class EstadoCarcel
{
    public string AlexanderId = "";
    public int SalaActual;
    public HashSet<int> PuertasAbiertas { get; } = new();
    public Dictionary<string, GuardiaEstado> Guardias { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, List<string>> Rosters { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, SlotEstado> Slots { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, SecretoPortador> Secretos { get; } = new(StringComparer.Ordinal);
    public List<PistaCarcel> Pistas { get; } = new();
    public int Alarma;
    /// Cuántos refuerzos de `ConfigCarcel.Refuerzos` han aparecido ya.
    public int RefuerzosLanzados;
    public bool OdioUsado;
    /// Turno para el que el jugador ha DECLARADO el odio (0 = ninguno).
    public int OdioTurno;
    /// Turno en que se desató de verdad (0 = aún no).
    public int OdioTurnoUsado;
    /// "" | "escape" (gana el jugador) | "alarma" (gana el bot).
    public string Fin = "";

    // Lo que pasó en la última resolución (para el aviso del cliente).
    public int UltimoTurno;
    public List<EventoCarcel> Eventos { get; } = new();
    public List<(string a, string b, string desde, string hacia)> Intercambios { get; } = new();
    public List<(string desde, string hacia)> Huidas { get; } = new();

    public const string FinEscape = "escape";
    public const string FinAlarma = "alarma";

    public static string Clave(int sala, int slot) => $"s{sala}n{slot}";

    public void IniciarTurno(int turno)
    {
        UltimoTurno = turno;
        Eventos.Clear();
        Intercambios.Clear();
        Huidas.Clear();
    }

    public void Evento(string tipo, string texto, bool malo = false, string coord = "") =>
        Eventos.Add(new EventoCarcel { Tipo = tipo, Texto = texto, Malo = malo, Coord = coord });

    public IEnumerable<GuardiaEstado> VivosDe(string clave) =>
        (Rosters.TryGetValue(clave, out var r) ? r : new List<string>())
            .Where(Guardias.ContainsKey)
            .Select(id => Guardias[id])
            .Where(g => g.Vivo);

    /// True si Alexander lleva todos los núcleos de la sala [sala].
    public bool LlevaNucleosDe(ConfigCarcel cfg, int sala) =>
        sala >= 0 && sala < cfg.Salas.Count
        && Enumerable.Range(0, cfg.Salas[sala].Slots.Count)
            .All(n => Slots.TryGetValue(Clave(sala, n), out var s) && s.Estado == SlotEstado.EstadoAlexander);

    /// Puertas cerradas en las que Alexander puede ENTRAR este turno.
    public HashSet<string> PuertasAbribles(ConfigCarcel cfg)
    {
        var res = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (Fin != "") return res;
        for (int k = 0; k < cfg.Salas.Count; k++)
            if (!PuertasAbiertas.Contains(k) && LlevaNucleosDe(cfg, k))
                res.Add(HistoriaCarcel.N(cfg.Salas[k].Puerta));
        return res;
    }

    public HashSet<string> PuertasCerradas(ConfigCarcel cfg)
    {
        var res = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int k = 0; k < cfg.Salas.Count; k++)
            if (!PuertasAbiertas.Contains(k)) res.Add(HistoriaCarcel.N(cfg.Salas[k].Puerta));
        return res;
    }

    // ── Documento ────────────────────────────────────────────────────────────
    /// Documento a guardar en la partida (`carcel`), válido para el turno
    /// [turnoSiguiente]. Incluye la geometría para que el cliente pinte y
    /// calcule el movimiento sin conocer la config.
    public Dictionary<string, object?> ACampo(ConfigCarcel cfg, int turnoSiguiente)
    {
        var abribles = PuertasAbribles(cfg);
        var salas = new List<object?>();
        var puertas = new List<object?>();
        for (int k = 0; k < cfg.Salas.Count; k++)
        {
            var s = cfg.Salas[k];
            salas.Add(new Dictionary<string, object?>
            {
                ["nombre"] = s.Nombre,
                ["puerta"] = HistoriaCarcel.N(s.Puerta),
                ["celdas"] = s.Celdas.Select(c => (object?)HistoriaCarcel.N(c)).ToList(),
                ["final"] = k == cfg.Salas.Count - 1,
                ["pistas"] = s.Pistas,
                ["huye"] = s.PortadorHuye,
                ["intercambios"] = (long)s.Intercambios,
                ["slots"] = s.Slots.Select(n => (object?)new Dictionary<string, object?>
                {
                    ["nombre"] = n.Nombre,
                    ["icono"] = n.Icono,
                    ["duracion"] = (long)n.Duracion,
                }).ToList(),
            });
            puertas.Add(new Dictionary<string, object?>
            {
                ["coord"] = HistoriaCarcel.N(s.Puerta),
                ["sala"] = (long)k,
                ["abierta"] = PuertasAbiertas.Contains(k),
                ["abrible"] = abribles.Contains(HistoriaCarcel.N(s.Puerta)),
                ["final"] = k == cfg.Salas.Count - 1,
                ["requiere"] = s.Slots.Select(n => (object?)n.Icono).ToList(),
            });
        }

        var nucleos = new List<object?>();
        foreach (var (clave, st) in Slots.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (st.Estado != SlotEstado.EstadoAlexander) continue;
            var (sala, slot) = HistoriaCarcel.ParseClave(clave);
            var def = cfg.Salas[sala].Slots[slot];
            nucleos.Add(new Dictionary<string, object?>
            {
                ["sala"] = (long)sala,
                ["slot"] = (long)slot,
                ["nombre"] = def.Nombre,
                ["icono"] = def.Icono,
                ["caduca"] = (long)st.Caduca,
                // Turnos que le quedan contando el que empieza (0 = sin límite).
                ["restantes"] = (long)(st.Caduca > 0 ? Math.Max(0, st.Caduca - turnoSiguiente + 1) : 0),
            });
        }

        var proximo = RefuerzosLanzados < cfg.Refuerzos.Count ? cfg.Refuerzos[RefuerzosLanzados] : null;

        return new Dictionary<string, object?>
        {
            ["turno"] = (long)turnoSiguiente,
            ["alexanderId"] = AlexanderId,
            ["salaActual"] = (long)SalaActual,
            ["fin"] = Fin,
            ["salas"] = salas,
            ["puertas"] = puertas,
            ["puertasAbiertas"] = PuertasAbiertas.OrderBy(x => x).Select(x => (object?)(long)x).ToList(),
            ["salida"] = cfg.Salida.Select(c => (object?)HistoriaCarcel.N(c)).ToList(),
            ["guardias"] = Guardias.Values.Where(g => g.Vivo).ToDictionary(
                g => g.Id,
                g => (object?)new Dictionary<string, object?>
                {
                    ["sala"] = (long)g.Sala,
                    ["slot"] = (long)g.Slot,
                    ["nombre"] = g.Nombre,
                    ["cartaId"] = g.CartaId,
                    ["peso"] = (long)g.Peso,
                    ["tipo"] = (long)g.Tipo,
                    ["prob"] = Math.Round(g.Prob, 6),
                    ["coord"] = g.Coord,
                }),
            ["muertos"] = Guardias.Values.Where(g => !g.Vivo).ToDictionary(
                g => g.Id,
                g => (object?)new Dictionary<string, object?>
                {
                    ["sala"] = (long)g.Sala,
                    ["slot"] = (long)g.Slot,
                    ["nombre"] = g.Nombre,
                    ["cartaId"] = g.CartaId,
                    ["peso"] = (long)g.Peso,
                    ["tipo"] = (long)g.Tipo,
                    ["coord"] = g.Coord,
                }),
            ["slots"] = Slots.ToDictionary(
                kv => kv.Key,
                kv => (object?)new Dictionary<string, object?>
                {
                    ["estado"] = kv.Value.Estado,
                    ["caduca"] = (long)kv.Value.Caduca,
                }),
            ["nucleos"] = nucleos,
            ["pistas"] = Pistas.Select(p => (object?)new Dictionary<string, object?>
            {
                ["turno"] = (long)p.Turno,
                ["sala"] = (long)p.Sala,
                ["slot"] = (long)p.Slot,
                ["texto"] = p.Texto,
            }).ToList(),
            ["alarma"] = new Dictionary<string, object?>
            {
                ["valor"] = (long)Alarma,
                ["max"] = (long)cfg.AlarmaMax,
                ["lanzados"] = (long)RefuerzosLanzados,
                ["proximoUmbral"] = (long)(proximo?.Umbral ?? 0),
                ["proximoNombre"] = proximo?.Nombre ?? "",
            },
            ["odio"] = new Dictionary<string, object?>
            {
                ["usado"] = OdioUsado,
                ["turno"] = (long)OdioTurno,
                ["turnoUsado"] = (long)OdioTurnoUsado,
                ["fuerza"] = (long)cfg.OdioFuerza,
            },
            // Internos (la lista es pública; el índice va enmascarado).
            ["rosters"] = Rosters.ToDictionary(
                kv => kv.Key, kv => (object?)kv.Value.Cast<object?>().ToList()),
            ["secretos"] = Secretos.ToDictionary(
                kv => kv.Key,
                kv => (object?)new Dictionary<string, object?>
                {
                    ["v"] = (long)kv.Value.V,
                    ["c"] = (long)kv.Value.C,
                    ["n"] = (long)kv.Value.N,
                }),
            ["ultimo"] = new Dictionary<string, object?>
            {
                ["turno"] = (long)UltimoTurno,
                ["eventos"] = Eventos.Select(e => (object?)new Dictionary<string, object?>
                {
                    ["tipo"] = e.Tipo,
                    ["texto"] = e.Texto,
                    ["coord"] = e.Coord,
                    ["malo"] = e.Malo,
                }).ToList(),
                ["intercambios"] = Intercambios.Select(x => (object?)new Dictionary<string, object?>
                {
                    ["a"] = x.a,
                    ["b"] = x.b,
                    ["desde"] = x.desde,
                    ["hacia"] = x.hacia,
                }).ToList(),
                ["huidas"] = Huidas.Select(x => (object?)new Dictionary<string, object?>
                {
                    ["desde"] = x.desde,
                    ["hacia"] = x.hacia,
                }).ToList(),
            },
        };
    }

    /// Lee el campo `carcel` de la partida. null si no hay.
    public static EstadoCarcel? DesdeCampo(object? raw)
    {
        var m = M.Map(raw);
        if (m.Count == 0) return null;
        var e = new EstadoCarcel
        {
            AlexanderId = M.Str(M.Get(m, "alexanderId")),
            SalaActual = M.Int(M.Get(m, "salaActual")),
            Fin = M.Str(M.Get(m, "fin")),
        };
        foreach (var x in M.List(M.Get(m, "puertasAbiertas"))) e.PuertasAbiertas.Add(M.Int(x));

        void LeerGuardias(object? src, bool vivo)
        {
            foreach (var (id, v) in M.Map(src))
            {
                var g = M.Map(v);
                e.Guardias[id] = new GuardiaEstado
                {
                    Id = id,
                    Sala = M.Int(M.Get(g, "sala")),
                    Slot = M.Int(M.Get(g, "slot")),
                    Nombre = M.Str(M.Get(g, "nombre")),
                    CartaId = M.Str(M.Get(g, "cartaId")),
                    Peso = Math.Max(1, M.Int(M.Get(g, "peso"))),
                    Tipo = M.Int(M.Get(g, "tipo")),
                    Vivo = vivo,
                    Prob = vivo ? M.Dbl(M.Get(g, "prob")) : 0,
                    Coord = M.Str(M.Get(g, "coord")),
                };
            }
        }
        LeerGuardias(M.Get(m, "guardias"), true);
        LeerGuardias(M.Get(m, "muertos"), false);

        foreach (var (k, v) in M.Map(M.Get(m, "rosters")))
            e.Rosters[k] = M.List(v).Select(M.Str).Where(s => s != "").ToList();
        foreach (var (k, v) in M.Map(M.Get(m, "slots")))
        {
            var s = M.Map(v);
            e.Slots[k] = new SlotEstado
            {
                Estado = M.Str(M.Get(s, "estado")) is { Length: > 0 } st ? st : SlotEstado.EstadoGuardia,
                Caduca = M.Int(M.Get(s, "caduca")),
            };
        }
        foreach (var (k, v) in M.Map(M.Get(m, "secretos")))
        {
            var s = M.Map(v);
            e.Secretos[k] = new SecretoPortador
            {
                V = M.Int(M.Get(s, "v")),
                C = M.Int(M.Get(s, "c")),
                N = M.Int(M.Get(s, "n")),
            };
        }
        foreach (var x in M.List(M.Get(m, "pistas")))
        {
            var p = M.Map(x);
            e.Pistas.Add(new PistaCarcel
            {
                Turno = M.Int(M.Get(p, "turno")),
                Sala = M.Int(M.Get(p, "sala")),
                Slot = M.Int(M.Get(p, "slot")),
                Texto = M.Str(M.Get(p, "texto")),
            });
        }
        var alarma = M.Map(M.Get(m, "alarma"));
        e.Alarma = M.Int(M.Get(alarma, "valor"));
        e.RefuerzosLanzados = M.Int(M.Get(alarma, "lanzados"));
        var odio = M.Map(M.Get(m, "odio"));
        e.OdioUsado = M.Bool(M.Get(odio, "usado"));
        e.OdioTurno = M.Int(M.Get(odio, "turno"));
        e.OdioTurnoUsado = M.Int(M.Get(odio, "turnoUsado"));
        e.UltimoTurno = M.Int(M.Get(M.Map(M.Get(m, "ultimo")), "turno"));
        return e;
    }
}

/// Lo que la resolución necesita saber tras `ResolverTurno`.
public sealed class ResultadoTurnoCarcel
{
    /// Se han añadido cartas al tablero (refuerzos o guardias nuevos).
    public bool TableroCambiado;
}

// ═════════════════════════════════════════════════════════════════════════════
// CATÁLOGO + LÓGICA
// ═════════════════════════════════════════════════════════════════════════════

public static class HistoriaCarcel
{
    // ── IDS DE CARTA ─────────────────────────────────────────────────────────
    public const string BioTortuga = "nKvmAoQLLynP0L91SqV3";   // Soldado tortuga · F2 D8 M1
    public const string BioZorro = "xYGGl9sWoZfYQwnlaNwu";     // Zorro fantasma · F6 D3 M2
    public const string BioCapitan = "JeI4EaJKLQrQlBiblNLp";   // Capitán de Dunan · F22 D12 M2
    public const string BioLobo = "rpZC7bELsrHFfn4YsCbY";      // Lobo titán · F10 D4 M3
    public const string BioCuervo = "emeeFFwdvz3RKHyYTxaI";    // Cuervo de combate · F8 D2 M5 · volador
    public const string BioQuetzal = "mrfcYa1TZZpdBKa0n85E";   // Quetzal de Chromia · F10 D4 M3 · volador
    public const string BioElefante = "tkw9wEklsteYfmGclaNe";  // Elefante de guerra · F35 D15 M2

    // ── nefilim_1 · El hijo del odio · La cárcel biónica ─────────────────────
    // Mapa CarcelDeDunant (7 filas A-G × 13 columnas). Alexander empieza en A1.
    //
    //        1  2  3  4  5  6  7  8  9 10 11 12 13
    //    A   Al .  .  Zf #  Zf .  .  #  Zf .  Zf .     Sala 1 · A1-C4
    //    B   .  .  T  .  P1 .  Cp .  P2 .  T  .  .     Sala 2 · A6-C8
    //    C   .  T  .  .  #  T  .  T  #  Lt .  .  T     Sala 3 · A10-C13
    //    D   #  #  #  #  #  #  #  #  #  #  P3 #  #
    //    E   ~  ~  ~  #  .  Zf .  .  Q  .  .  Lt .     Sala 4 · E5-G13
    //    F   ~  ~  ~  PF .  .  .  Cv .  .  .  .  .
    //    G   ~  ~  ~  #  .  .  .  .  Cp .  .  Cv .
    //
    //    # muro · P puerta · ~ exterior (fuga) · D1/D13 cuarteles nominales
    //    T Soldado tortuga · Zf Zorro fantasma · Cp Capitán de Dunan ·
    //    Lt Lobo titán · Cv Cuervo de combate · Q Quetzal de Chromia
    private static readonly ConfigCarcel CarcelDeDunant = new(
        MapaId: "CarcelDeDunant",
        Filas: 7,
        Columnas: 13,
        Muros: new[]
        {
            "A5", "C5", "A9", "C9",
            "D1", "D2", "D3", "D4", "D5", "D6", "D7", "D8", "D9", "D10", "D12", "D13",
            "E4", "G4",
        },
        Salas: new[]
        {
            // Sala 1 · tutorial: solo % visibles, sin límite de tiempo.
            new SalaCarcel(
                Nombre: "Las celdas",
                Celdas: Rect("A1", "C4"),
                Puerta: "B5",
                Guardias: new[]
                {
                    new GuardiaCarcel("B3", BioTortuga, Peso: 1),
                    new GuardiaCarcel("C2", BioTortuga, Peso: 1),
                    new GuardiaCarcel("A4", BioZorro, Peso: 2),
                },
                Slots: new[] { new SlotNucleo("Núcleo de las celdas", "🔑", Duracion: 0, CartaRespawn: BioZorro) },
                Respawn: "B3"),

            // Sala 2 · pistas en los cadáveres y el portador huye.
            new SalaCarcel(
                Nombre: "El pasillo de BioZero",
                Celdas: Rect("A6", "C8"),
                Puerta: "B9",
                Guardias: new[]
                {
                    new GuardiaCarcel("A6", BioZorro, Peso: 2),
                    new GuardiaCarcel("C6", BioTortuga, Peso: 1),
                    new GuardiaCarcel("B7", BioCapitan, Peso: 3),
                    new GuardiaCarcel("C8", BioTortuga, Peso: 1),
                },
                Slots: new[] { new SlotNucleo("Núcleo del pasillo", "🔑", Duracion: 3, CartaRespawn: BioZorro) },
                Respawn: "B6",
                Pistas: true,
                PortadorHuye: true),

            // Sala 3 · además, TRILE: un intercambio por turno.
            new SalaCarcel(
                Nombre: "La sala de control",
                Celdas: Rect("A10", "C13"),
                Puerta: "D11",
                Guardias: new[]
                {
                    new GuardiaCarcel("A10", BioZorro, Peso: 2),
                    new GuardiaCarcel("B11", BioTortuga, Peso: 1),
                    new GuardiaCarcel("C10", BioLobo, Peso: 2),
                    new GuardiaCarcel("A12", BioZorro, Peso: 2),
                    new GuardiaCarcel("C13", BioTortuga, Peso: 1),
                },
                Slots: new[] { new SlotNucleo("Núcleo de control", "🔑", Duracion: 3, CartaRespawn: BioZorro) },
                Respawn: "B12",
                Pistas: true,
                PortadorHuye: true,
                Intercambios: 1),

            // Sala 4 · la puerta final pide DOS núcleos: terrestre y volador.
            new SalaCarcel(
                Nombre: "El hangar",
                Celdas: Rect("E5", "G13"),
                Puerta: "F4",
                Guardias: new[]
                {
                    new GuardiaCarcel("E12", BioLobo, Peso: 2, Slot: 0),
                    new GuardiaCarcel("G9", BioCapitan, Peso: 3, Slot: 0),
                    new GuardiaCarcel("E6", BioZorro, Peso: 2, Slot: 0),
                    new GuardiaCarcel("G12", BioCuervo, Peso: 2, Slot: 1),
                    new GuardiaCarcel("F8", BioCuervo, Peso: 2, Slot: 1),
                    new GuardiaCarcel("E9", BioQuetzal, Peso: 1, Slot: 1),
                },
                Slots: new[]
                {
                    new SlotNucleo("Núcleo terrestre", "🔑", Duracion: 6, CartaRespawn: BioZorro),
                    new SlotNucleo("Núcleo volador", "🪶", Duracion: 6, CartaRespawn: BioCuervo),
                },
                Respawn: "F10",
                Pistas: true,
                PortadorHuye: true),
        },
        Salida: Rect("E1", "G3"),
        Refuerzos: new[]
        {
            new RefuerzoAlarma(5, BioTortuga, "Soldado tortuga"),
            new RefuerzoAlarma(10, BioZorro, "Zorro fantasma"),
            new RefuerzoAlarma(15, BioCapitan, "Capitán de Dunan"),
            new RefuerzoAlarma(20, BioLobo, "Lobo titán"),
            new RefuerzoAlarma(25, BioElefante, "Elefante de guerra"),
            new RefuerzoAlarma(30, BioCapitan, "Capitán de Dunan"),
            new RefuerzoAlarma(35, BioElefante, "Elefante de guerra"),
        },
        CuartelJugador: "D1",
        CuartelBot: "D13",
        AlarmaMax: 40,
        AlarmaPorTurno: 1,
        AlarmaPorError: 1,
        AlarmaPorApagado: 2,
        OdioFuerza: 40);

    /// Cárceles registradas, por `HistoriaDef.Id`.
    public static readonly IReadOnlyDictionary<string, ConfigCarcel> Todas =
        new Dictionary<string, ConfigCarcel>
        {
            ["nefilim_1"] = CarcelDeDunant,
        };

    public static ConfigCarcel? Get(string historiaId) =>
        !string.IsNullOrEmpty(historiaId) && Todas.TryGetValue(historiaId, out var c) ? c : null;

    /// Mapa por defecto de una cárcel cuyo documento de `Mapas` no existe:
    /// todo tierra, con sus dimensiones y los dos cuarteles nominales.
    public static (int filas, int columnas, List<string> obeliscos)? MapaPorDefecto(string mapaId)
    {
        var cfg = Todas.Values.FirstOrDefault(c =>
            string.Equals(c.MapaId, mapaId, StringComparison.OrdinalIgnoreCase));
        if (cfg == null) return null;
        return (cfg.Filas, cfg.Columnas, new List<string> { N(cfg.CuartelJugador), N(cfg.CuartelBot) });
    }

    /// Cartas del bot para el `BandoHistoria` de la batalla (cada guardia en
    /// su casilla). Así la config de la cárcel es la única fuente de verdad.
    public static IReadOnlyList<CartaHistoria> CartasBot(ConfigCarcel cfg) =>
        cfg.Salas.SelectMany(s => s.Guardias)
            .Select(g => new CartaHistoria(g.CartaId, 1, Coord: N(g.Coord)))
            .ToList();

    // ── Secreto ──────────────────────────────────────────────────────────────
    private static string Secreto =>
        Environment.GetEnvironmentVariable("WZ_CARCEL_SECRETO")
        ?? "wz-carcel-4c19e8a07d2b5f36-nefilim";

    private static uint Hash(string s)
    {
        using var h = new HMACSHA256(Encoding.UTF8.GetBytes(Secreto));
        var bytes = h.ComputeHash(Encoding.UTF8.GetBytes(s));
        return BitConverter.ToUInt32(bytes, 0);
    }

    private static int Mascara(int semilla, string clave, int v, int n) =>
        n <= 0 ? 0 : (int)(Hash($"{semilla}|carcel|{clave}|{v}") % (uint)n);

    /// Guardia que lleva AHORA el núcleo [clave], o null si no lo lleva nadie.
    public static string? Portador(EstadoCarcel e, int semilla, string clave)
    {
        if (!e.Slots.TryGetValue(clave, out var st) || st.Estado != SlotEstado.EstadoGuardia) return null;
        if (!e.Secretos.TryGetValue(clave, out var sec) || sec.N <= 0) return null;
        if (!e.Rosters.TryGetValue(clave, out var roster) || roster.Count < sec.N) return null;
        int k = Mascara(semilla, clave, sec.V, sec.N);
        int idx = ((sec.C - k) % sec.N + sec.N) % sec.N;
        return roster[idx];
    }

    /// Fija el portador de [clave] (enmascarado con una versión nueva).
    private static void FijarPortador(EstadoCarcel e, int semilla, string clave, string guardiaId)
    {
        var roster = e.Rosters[clave];
        int idx = roster.IndexOf(guardiaId);
        if (idx < 0) return;
        if (!e.Secretos.TryGetValue(clave, out var sec)) e.Secretos[clave] = sec = new SecretoPortador();
        sec.V++;
        sec.N = roster.Count;
        sec.C = (idx + Mascara(semilla, clave, sec.V, sec.N)) % sec.N;
    }

    /// Vuelve a enmascarar TODOS los portadores con una versión nueva: así el
    /// valor guardado cambia cada turno pase lo que pase y no delata nada.
    private static void ReenmascararTodo(EstadoCarcel e, int semilla)
    {
        foreach (var clave in e.Slots.Keys.ToList())
        {
            var p = Portador(e, semilla, clave);
            if (p != null) FijarPortador(e, semilla, clave, p);
        }
    }

    /// Elige con los PESOS de [candidatos] (determinista para la semilla).
    private static string? ElegirPonderado(
        IReadOnlyList<GuardiaEstado> candidatos, int semilla, string etiqueta)
    {
        if (candidatos.Count == 0) return null;
        int total = candidatos.Sum(g => Math.Max(1, g.Peso));
        double u = Hash($"{semilla}|carcel-elige|{etiqueta}") / 4294967296.0;
        double acc = 0;
        foreach (var g in candidatos)
        {
            acc += (double)Math.Max(1, g.Peso) / total;
            if (u < acc) return g.Id;
        }
        return candidatos[^1].Id;
    }

    /// Probabilidades a priori (por peso) de los vivos de [clave].
    private static void ProbsAPriori(EstadoCarcel e, string clave)
    {
        var vivos = e.VivosDe(clave).ToList();
        int total = vivos.Sum(g => Math.Max(1, g.Peso));
        foreach (var g in vivos) g.Prob = total > 0 ? (double)Math.Max(1, g.Peso) / total : 0;
    }

    private static void Normalizar(EstadoCarcel e, string clave)
    {
        var vivos = e.VivosDe(clave).ToList();
        double total = vivos.Sum(g => g.Prob);
        if (total <= 0)
        {
            ProbsAPriori(e, clave);
            return;
        }
        foreach (var g in vivos) g.Prob /= total;
    }

    // ── Estado inicial ───────────────────────────────────────────────────────
    /// Casa los guardias de la config con las cartas ya sembradas en [tablero]
    /// (por casilla y carta) y elige el portador de cada núcleo.
    public static EstadoCarcel Inicial(
        ConfigCarcel cfg,
        Dictionary<string, List<Dictionary<string, object?>>> tablero,
        int semilla, string jugadorUid, string botUid)
    {
        var e = new EstadoCarcel();

        // Alexander: la (única) carta del jugador.
        foreach (var (_, lst) in tablero)
            foreach (var c in lst)
                if (CartaHelper.OwnerUid(c) == jugadorUid && e.AlexanderId == "")
                    e.AlexanderId = M.Str(M.Get(c, "instanceId"));

        var usados = new HashSet<string>(StringComparer.Ordinal);
        for (int k = 0; k < cfg.Salas.Count; k++)
        {
            var sala = cfg.Salas[k];
            for (int n = 0; n < sala.Slots.Count; n++)
            {
                var clave = EstadoCarcel.Clave(k, n);
                e.Rosters[clave] = new List<string>();
                e.Slots[clave] = new SlotEstado();
            }
            foreach (var gd in sala.Guardias)
            {
                var coord = N(gd.Coord);
                if (!tablero.TryGetValue(coord, out var lst)) continue;
                var carta = lst.FirstOrDefault(c =>
                    CartaHelper.OwnerUid(c) == botUid
                    && M.Str(M.Get(c, "id")) == gd.CartaId
                    && !usados.Contains(M.Str(M.Get(c, "instanceId"))));
                if (carta == null)
                {
                    Console.Error.WriteLine($"[WZ.Carcel] no se encontró el guardia {gd.CartaId} en {coord}");
                    continue;
                }
                var iid = M.Str(M.Get(carta, "instanceId"));
                usados.Add(iid);
                int slot = Math.Clamp(gd.Slot, 0, Math.Max(0, sala.Slots.Count - 1));
                e.Guardias[iid] = new GuardiaEstado
                {
                    Id = iid,
                    Sala = k,
                    Slot = slot,
                    Nombre = CartaHelper.Nombre(carta),
                    CartaId = gd.CartaId,
                    Peso = Math.Max(1, gd.Peso),
                    Tipo = CartaHelper.Tipo(carta),
                    Coord = coord,
                };
                e.Rosters[EstadoCarcel.Clave(k, slot)].Add(iid);
            }
            for (int n = 0; n < sala.Slots.Count; n++)
            {
                var clave = EstadoCarcel.Clave(k, n);
                ProbsAPriori(e, clave);
                var elegido = ElegirPonderado(e.VivosDe(clave).ToList(), semilla, $"{clave}|inicio");
                if (elegido != null) FijarPortador(e, semilla, clave, elegido);
            }
        }
        return e;
    }

    // ── Paso A · Odio ────────────────────────────────────────────────────────
    /// Si el jugador declaró el ODIO para [turno], Alexander suma la fuerza del
    /// odio en el combate de esta resolución (potenciación de 1 turno: la
    /// caduca el tick de efectos, que va después del combate).
    public static void AplicarOdio(
        ConfigCarcel cfg, EstadoCarcel e, int turno, string jugadorUid,
        Dictionary<string, List<Dictionary<string, object?>>> merged)
    {
        if (e.OdioUsado || e.OdioTurno != turno || e.AlexanderId == "") return;
        foreach (var (coord, lst) in merged)
            foreach (var c in lst)
            {
                if (M.Str(M.Get(c, "instanceId")) != e.AlexanderId) continue;
                var efectos = M.List(M.Get(c, "Efectos")).ToList();
                efectos.Add(new Dictionary<string, object?>
                {
                    ["tipo"] = "potFuerza",
                    ["turnosRestantes"] = 1L,
                    ["magnitud"] = (long)cfg.OdioFuerza,
                    ["origenUid"] = jugadorUid,
                    ["origen"] = "odioCarcel",
                });
                c["Efectos"] = efectos;
                e.OdioUsado = true;
                e.OdioTurnoUsado = turno;
                e.Evento("odio", $"🔥 ¡Alexander desata el ODIO! +{cfg.OdioFuerza} de fuerza en este combate.",
                    coord: coord);
                return;
            }
    }

    // ── Paso A · Movimiento ──────────────────────────────────────────────────
    /// Casillas a las que puede ir una carta que empieza en [origen] con [mov]
    /// pasos: muros y puertas cerradas son pared, salvo las puertas que se
    /// pueden abrir (Alexander lleva sus núcleos), en las que se puede entrar
    /// pero no atravesar. No incluye el origen. Mismo algoritmo que el cliente
    /// (historia_tunel.dart · CarcelVista).
    public static HashSet<string> Destinos(
        ConfigCarcel cfg, EstadoCarcel e, string origen, int mov, int filas, int columnas)
    {
        var res = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (mov <= 0) return res;
        origen = N(origen);
        var muros = cfg.Muros.Select(N).ToHashSet(StringComparer.OrdinalIgnoreCase);
        muros.Add(N(cfg.CuartelJugador));
        muros.Add(N(cfg.CuartelBot));
        var cerradas = e.PuertasCerradas(cfg);
        var abribles = e.PuertasAbribles(cfg);

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
                if (muros.Contains(n)) continue;
                if (cerradas.Contains(n))
                {
                    if (!abribles.Contains(n)) continue;
                    visto[n] = nd;
                    res.Add(n);           // se entra, pero no se atraviesa
                    continue;
                }
                visto[n] = nd;
                if (n != origen) res.Add(n);
                if (nd < mov) cola.Enqueue((n, nd));
            }
        }
        return res;
    }

    // ── Paso B · Resolución ──────────────────────────────────────────────────
    /// Resuelve el turno [turno] sobre el tablero YA resuelto [tablero]
    /// (muta [e] y, si aparecen refuerzos, [tablero]). [crearCarta] clona una
    /// carta del catálogo para el bot (null si no existe).
    public static ResultadoTurnoCarcel ResolverTurno(
        ConfigCarcel cfg, EstadoCarcel e, int semilla, int turno,
        Dictionary<string, List<Dictionary<string, object?>>> tablero,
        Func<string, Dictionary<string, object?>?> crearCarta)
    {
        var res = new ResultadoTurnoCarcel();
        if (e.UltimoTurno != turno) e.IniciarTurno(turno);
        if (e.Fin != "") return res;

        // Posición de cada carta por instancia.
        var pos = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (coord, lst) in tablero)
            foreach (var c in lst)
            {
                var iid = M.Str(M.Get(c, "instanceId"));
                if (iid != "") pos[iid] = coord;
            }
        pos.TryGetValue(e.AlexanderId, out var celdaAlex);
        bool alexVivo = celdaAlex != null;

        // ── 1) Huidas (guardias que han cambiado de casilla) ────────────────
        foreach (var g in e.Guardias.Values.Where(g => g.Vivo).OrderBy(g => g.Id, StringComparer.Ordinal))
        {
            if (!pos.TryGetValue(g.Id, out var c)) continue;
            if (!string.Equals(c, g.Coord, StringComparison.OrdinalIgnoreCase) && g.Coord != "")
                e.Huidas.Add((g.Coord, c));
            g.Coord = c;
        }

        // ── 2) Muertes: ¿llevaba el núcleo? ─────────────────────────────────
        var muertos = e.Guardias.Values
            .Where(g => g.Vivo && !pos.ContainsKey(g.Id))
            .OrderBy(g => g.Sala).ThenBy(g => g.Slot).ThenBy(g => g.Id, StringComparer.Ordinal)
            .ToList();
        // Los portadores primero: así el núcleo se reparte antes de que las
        // muertes equivocadas del mismo combate generen pistas.
        var portadores = e.Slots.Keys.ToDictionary(k => k, k => Portador(e, semilla, k));
        foreach (var m in muertos.OrderByDescending(m => portadores.Values.Contains(m.Id)))
        {
            m.Vivo = false;
            var clave = EstadoCarcel.Clave(m.Sala, m.Slot);
            var st = e.Slots[clave];
            var slotCfg = cfg.Salas[m.Sala].Slots[m.Slot];
            bool eraPortador = portadores.TryGetValue(clave, out var p) && p == m.Id;

            if (eraPortador)
            {
                m.Prob = 0;
                if (!alexVivo) continue;
                st.Estado = SlotEstado.EstadoAlexander;
                st.Caduca = slotCfg.Duracion > 0 ? turno + slotCfg.Duracion : 0;
                foreach (var g in e.VivosDe(clave)) g.Prob = 0;
                e.Evento("nucleo",
                    $"{slotCfg.Icono} ¡{m.Nombre} llevaba el {slotCfg.Nombre}! " +
                    (slotCfg.Duracion > 0
                        ? $"Tienes {slotCfg.Duracion} turnos para llegar a la puerta {N(cfg.Salas[m.Sala].Puerta)}."
                        : $"Llévalo a la puerta {N(cfg.Salas[m.Sala].Puerta)}."),
                    coord: m.Coord);
                continue;
            }

            m.Prob = 0;
            e.Alarma += cfg.AlarmaPorError;
            bool buscando = st.Estado == SlotEstado.EstadoGuardia;
            if (buscando) Normalizar(e, clave);
            e.Evento("error", $"✖ {m.Nombre} ({m.Coord}) no llevaba el núcleo. La alarma sube.",
                malo: true, coord: m.Coord);

            if (buscando && alexVivo && cfg.Salas[m.Sala].Pistas && p != null)
                GenerarPista(cfg, e, semilla, turno, clave, p, m, celdaAlex!);
        }

        // ── 3) Puerta: Alexander vivo sobre la puerta con sus núcleos ───────
        if (alexVivo)
        {
            for (int k = 0; k < cfg.Salas.Count; k++)
            {
                var sala = cfg.Salas[k];
                if (e.PuertasAbiertas.Contains(k)) continue;
                if (!string.Equals(N(sala.Puerta), celdaAlex, StringComparison.OrdinalIgnoreCase)) continue;
                if (!e.LlevaNucleosDe(cfg, k)) continue;
                e.PuertasAbiertas.Add(k);
                for (int n = 0; n < sala.Slots.Count; n++)
                {
                    var st = e.Slots[EstadoCarcel.Clave(k, n)];
                    st.Estado = SlotEstado.EstadoUsado;
                    st.Caduca = 0;
                }
                if (k == cfg.Salas.Count - 1)
                {
                    e.Fin = EstadoCarcel.FinEscape;
                    e.Evento("fuga", "🌄 ¡La puerta de Dunant cede! Alexander escapa de la cárcel.", coord: celdaAlex!);
                    return res;
                }
                e.SalaActual = Math.Max(e.SalaActual, k + 1);
                e.Evento("puerta",
                    $"🚪 La puerta {N(sala.Puerta)} se abre. Siguiente: {cfg.Salas[k + 1].Nombre}.",
                    coord: celdaAlex!);
            }
        }

        // ── 4) Núcleos que se apagan ────────────────────────────────────────
        foreach (var (clave, st) in e.Slots.OrderBy(kv => kv.Key, StringComparer.Ordinal).ToList())
        {
            if (st.Estado != SlotEstado.EstadoAlexander || st.Caduca <= 0 || turno < st.Caduca) continue;
            var (k, n) = ParseClave(clave);
            var slotCfg = cfg.Salas[k].Slots[n];
            st.Estado = SlotEstado.EstadoGuardia;
            st.Caduca = 0;
            e.Alarma += cfg.AlarmaPorApagado;
            var nuevo = Reasignar(cfg, e, semilla, turno, clave, tablero, celdaAlex, crearCarta, res);
            e.Evento("apagado",
                $"💨 El {slotCfg.Nombre} se ha apagado. " +
                (nuevo != null
                    ? "Otro guardia de la sala lleva ahora uno nuevo: búscalo otra vez."
                    : "No queda nadie que pueda llevar uno nuevo."),
                malo: true);
        }

        // ── 5) Trile: intercambios en la sala activa ───────────────────────
        if (e.SalaActual < cfg.Salas.Count)
        {
            var sala = cfg.Salas[e.SalaActual];
            for (int n = 0; n < sala.Slots.Count && sala.Intercambios > 0; n++)
            {
                var clave = EstadoCarcel.Clave(e.SalaActual, n);
                if (e.Slots[clave].Estado != SlotEstado.EstadoGuardia) continue;
                Trile(e, semilla, turno, clave, sala.Intercambios);
            }
        }

        // ── 6) Alarma: subida del turno, refuerzos y cierre ────────────────
        e.Alarma += cfg.AlarmaPorTurno;
        while (e.RefuerzosLanzados < cfg.Refuerzos.Count
               && e.Alarma >= cfg.Refuerzos[e.RefuerzosLanzados].Umbral)
        {
            var r = cfg.Refuerzos[e.RefuerzosLanzados++];
            if (e.SalaActual >= cfg.Salas.Count) continue;
            var puerta = N(cfg.Salas[e.SalaActual].Puerta);
            var carta = crearCarta(r.CartaId);
            if (carta == null)
            {
                Console.Error.WriteLine($"[WZ.Carcel] refuerzo {r.CartaId} desconocido, se omite");
                continue;
            }
            carta["refuerzoCarcel"] = true;
            if (!tablero.TryGetValue(puerta, out var lst)) tablero[puerta] = lst = new();
            lst.Add(carta);
            res.TableroCambiado = true;
            e.Evento("refuerzo",
                $"🚨 Alarma {e.Alarma}: un {r.Nombre} se aposta en la puerta {puerta}.",
                malo: true, coord: puerta);
        }
        if (e.Alarma >= cfg.AlarmaMax)
        {
            e.Fin = EstadoCarcel.FinAlarma;
            e.Evento("cierre", "🔒 La alarma es total: la cárcel se sella y los guardias te rodean.", malo: true);
        }

        ReenmascararTodo(e, semilla);
        return res;
    }

    /// Da un núcleo NUEVO del slot [clave] a un guardia vivo (por peso). Si no
    /// queda ninguno, aparece uno en la sala. Devuelve su id (o null).
    private static string? Reasignar(
        ConfigCarcel cfg, EstadoCarcel e, int semilla, int turno, string clave,
        Dictionary<string, List<Dictionary<string, object?>>> tablero, string? celdaAlex,
        Func<string, Dictionary<string, object?>?> crearCarta, ResultadoTurnoCarcel res)
    {
        var (k, n) = ParseClave(clave);
        var sala = cfg.Salas[k];
        var vivos = e.VivosDe(clave).ToList();
        if (vivos.Count == 0)
        {
            var slotCfg = sala.Slots[n];
            var carta = crearCarta(slotCfg.CartaRespawn);
            if (carta == null) return null;
            // Casilla libre de la sala más cercana a la de respawn (nunca la
            // de Alexander: no queremos un combate que no ha elegido).
            var ocupadas = tablero.Where(kv => kv.Value.Count > 0).Select(kv => kv.Key)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var celda = sala.Celdas.Select(N)
                .Where(c => !string.Equals(c, celdaAlex, StringComparison.OrdinalIgnoreCase))
                .OrderBy(c => ocupadas.Contains(c) ? 1 : 0)
                .ThenBy(c => Manhattan(c, N(sala.Respawn)))
                .ThenBy(c => c, StringComparer.Ordinal)
                .FirstOrDefault() ?? N(sala.Respawn);
            var iid = M.Str(M.Get(carta, "instanceId"));
            if (!tablero.TryGetValue(celda, out var lst)) tablero[celda] = lst = new();
            lst.Add(carta);
            res.TableroCambiado = true;
            e.Guardias[iid] = new GuardiaEstado
            {
                Id = iid,
                Sala = k,
                Slot = n,
                Nombre = CartaHelper.Nombre(carta),
                CartaId = slotCfg.CartaRespawn,
                Peso = 1,
                Tipo = CartaHelper.Tipo(carta),
                Coord = celda,
            };
            e.Rosters[clave].Add(iid);
            e.Evento("respawn", $"⚠ Un {CartaHelper.Nombre(carta)} nuevo entra en {sala.Nombre} ({celda}).",
                malo: true, coord: celda);
            vivos = e.VivosDe(clave).ToList();
        }
        ProbsAPriori(e, clave);
        var elegido = ElegirPonderado(vivos, semilla, $"{clave}|reasigna|{turno}");
        if (elegido != null) FijarPortador(e, semilla, clave, elegido);
        return elegido;
    }

    /// TRILE: [cuantos] parejas de guardias cercanos (≤ 2 casillas) se
    /// intercambian los núcleos. Se publica quién con quién y los % se
    /// intercambian igual, así que el posterior sigue siendo exacto.
    private static void Trile(EstadoCarcel e, int semilla, int turno, string clave, int cuantos)
    {
        var portador = Portador(e, semilla, clave);
        var vivos = e.VivosDe(clave).Where(g => g.Coord != "")
            .OrderBy(g => g.Id, StringComparer.Ordinal).ToList();
        // Semilla estable (string.GetHashCode cambia entre procesos).
        var rng = new Random((int)(Hash($"{semilla}|carcel-trile|{clave}|{turno}") & 0x7FFFFFFF));
        var usados = new HashSet<string>(StringComparer.Ordinal);
        for (int t = 0; t < cuantos; t++)
        {
            var parejas = new List<(GuardiaEstado a, GuardiaEstado b)>();
            for (int i = 0; i < vivos.Count; i++)
                for (int j = i + 1; j < vivos.Count; j++)
                {
                    var a = vivos[i]; var b = vivos[j];
                    if (usados.Contains(a.Id) || usados.Contains(b.Id)) continue;
                    if (string.Equals(a.Coord, b.Coord, StringComparison.OrdinalIgnoreCase)) continue;
                    if (Chebyshev(a.Coord, b.Coord) > 2) continue;
                    parejas.Add((a, b));
                }
            if (parejas.Count == 0) break;
            var (x, y) = parejas[rng.Next(parejas.Count)];
            usados.Add(x.Id);
            usados.Add(y.Id);
            (x.Prob, y.Prob) = (y.Prob, x.Prob);
            if (portador == x.Id) portador = y.Id;
            else if (portador == y.Id) portador = x.Id;
            e.Intercambios.Add((x.Id, y.Id, x.Coord, y.Coord));
        }
        if (portador != null) FijarPortador(e, semilla, clave, portador);
        if (e.Intercambios.Count > 0)
            e.Evento("trile",
                "🔀 Los guardias se pasan los núcleos: " +
                string.Join(" · ", e.Intercambios.Select(i => $"{i.desde} ⇄ {i.hacia}")) + ".");
    }

    // ── Pistas ───────────────────────────────────────────────────────────────
    private sealed record Candidata(string Texto, Func<GuardiaEstado, bool> Cumple);

    /// Pista VERDADERA sobre el portador [portadorId] de [clave], que deja el
    /// terminal de [muerto]. Se elige la que descarta más o menos la mitad de
    /// los candidatos (nunca a todos los que no son: siempre queda duda si la
    /// había). Actualiza los % al momento.
    private static void GenerarPista(
        ConfigCarcel cfg, EstadoCarcel e, int semilla, int turno, string clave,
        string portadorId, GuardiaEstado muerto, string celdaAlex)
    {
        var (k, n) = ParseClave(clave);
        var cand = e.VivosDe(clave).Where(g => g.Prob > 0 && g.Coord.Length >= 2).ToList();
        if (cand.Count < 2 || !e.Guardias.TryGetValue(portadorId, out var portador)) return;
        var puerta = N(cfg.Salas[k].Puerta);

        var opciones = new List<Candidata>();
        foreach (var d in cand.Select(g => Manhattan(g.Coord, puerta)).Distinct().OrderBy(x => x))
        {
            int dd = d;
            opciones.Add(new($"está a {dd} {Casillas(dd)} o menos de la puerta {puerta}",
                g => Manhattan(g.Coord, puerta) <= dd));
            opciones.Add(new($"está a más de {dd} {Casillas(dd)} de la puerta {puerta}",
                g => Manhattan(g.Coord, puerta) > dd));
        }
        foreach (var fila in cand.Select(g => g.Coord[..1]).Distinct())
        {
            var f = fila;
            opciones.Add(new($"está en la fila {f}", g => g.Coord.StartsWith(f, StringComparison.OrdinalIgnoreCase)));
            opciones.Add(new($"no está en la fila {f}", g => !g.Coord.StartsWith(f, StringComparison.OrdinalIgnoreCase)));
        }
        foreach (var nombre in cand.Select(g => g.Nombre).Distinct())
        {
            var nm = nombre;
            opciones.Add(new($"es un {nm}", g => g.Nombre == nm));
            opciones.Add(new($"no es un {nm}", g => g.Nombre != nm));
        }
        foreach (var d in cand.Select(g => Manhattan(g.Coord, celdaAlex)).Distinct().OrderBy(x => x))
        {
            int dd = d;
            opciones.Add(new($"está a {dd} {Casillas(dd)} o menos de ti", g => Manhattan(g.Coord, celdaAlex) <= dd));
            opciones.Add(new($"está a más de {dd} {Casillas(dd)} de ti", g => Manhattan(g.Coord, celdaAlex) > dd));
        }

        // Solo pistas ciertas que descartan a alguien. Puntuación: lo cerca que
        // deja el nº de candidatos de la mitad.
        double objetivo = cand.Count / 2.0;
        var validas = opciones
            .Where(o => o.Cumple(portador))
            .Select(o => (o, quedan: cand.Count(o.Cumple)))
            .Where(x => x.quedan < cand.Count)
            .ToList();
        if (validas.Count == 0)
        {
            e.Evento("pista", $"🔎 El terminal de {muerto.Nombre} está destrozado: no revela nada.");
            return;
        }
        var mejor = validas.Min(x => Math.Abs(x.quedan - objetivo));
        var empatadas = validas.Where(x => Math.Abs(Math.Abs(x.quedan - objetivo) - mejor) < 1e-9)
            .OrderBy(x => x.o.Texto, StringComparer.Ordinal).ToList();
        var elegida = empatadas[(int)(Hash($"{semilla}|carcel-pista|{clave}|{turno}|{muerto.Id}") % (uint)empatadas.Count)].o;

        foreach (var g in cand)
            if (!elegida.Cumple(g)) g.Prob = 0;
        Normalizar(e, clave);

        var slotCfg = cfg.Salas[k].Slots[n];
        // "Ahora mismo": la huida y el trile pueden cambiarlo después (los %
        // ya lo tienen en cuenta).
        var texto = $"Ahora mismo, el portador del {slotCfg.Nombre} {elegida.Texto}.";
        e.Pistas.Add(new PistaCarcel { Turno = turno, Sala = k, Slot = n, Texto = texto });
        e.Evento("pista", $"🔎 Terminal de {muerto.Nombre}: {texto}");
    }

    private static string Casillas(int n) => n == 1 ? "casilla" : "casillas";

    // ── IA de los guardias: la HUIDA del portador ────────────────────────────
    /// Guardias que huyen este turno (id → casilla destino). [tablero] es el
    /// tablero al EMPEZAR el turno. Solo huye el portador de un núcleo de la
    /// sala activa con `PortadorHuye`, si Alexander está a 2 casillas o menos
    /// (en diagonal también) y hay una casilla de la sala más lejos de él.
    public static Dictionary<string, string> PlanHuida(
        ConfigCarcel cfg, EstadoCarcel e, int semilla,
        Dictionary<string, List<Dictionary<string, object?>>> tablero)
    {
        var res = new Dictionary<string, string>(StringComparer.Ordinal);
        if (e.Fin != "" || e.SalaActual >= cfg.Salas.Count) return res;
        var sala = cfg.Salas[e.SalaActual];
        if (!sala.PortadorHuye) return res;

        string? celdaAlex = null;
        var posGuardia = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (coord, lst) in tablero)
            foreach (var c in lst)
            {
                var iid = M.Str(M.Get(c, "instanceId"));
                if (iid == e.AlexanderId) celdaAlex = coord;
                else if (iid != "") posGuardia[iid] = coord;
            }
        if (celdaAlex == null) return res;
        var celdasSala = sala.Celdas.Select(N).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!celdasSala.Contains(celdaAlex)) return res;

        for (int n = 0; n < sala.Slots.Count; n++)
        {
            var p = Portador(e, semilla, EstadoCarcel.Clave(e.SalaActual, n));
            if (p == null || !posGuardia.TryGetValue(p, out var desde)) continue;
            var destino = DestinoHuida(cfg, e.SalaActual, desde, celdaAlex);
            if (destino != null) res[p] = destino;
        }
        return res;
    }

    /// Casilla a la que huiría un guardia de la sala [sala] que está en
    /// [desde] si Alexander está en [celdaAlex], o null si no huye: solo huye
    /// con Alexander a 2 casillas o menos (en diagonal también) y hacia la
    /// casilla vecina de la sala que más le aleja (en empate, la de menor
    /// coordenada). Es determinista: quien observe con atención puede
    /// anticiparla.
    public static string? DestinoHuida(ConfigCarcel cfg, int sala, string desde, string celdaAlex)
    {
        if (Chebyshev(desde, celdaAlex) > 2) return null;
        var celdasSala = cfg.Salas[sala].Celdas.Select(N).ToHashSet(StringComparer.OrdinalIgnoreCase);
        int dActual = Manhattan(desde, celdaAlex);
        return Vecinos(desde)
            .Where(celdasSala.Contains)
            .Where(c => !string.Equals(c, celdaAlex, StringComparison.OrdinalIgnoreCase))
            .Select(c => (c, d: Manhattan(c, celdaAlex), cheb: Chebyshev(c, celdaAlex)))
            .Where(x => x.d > dActual)
            .OrderByDescending(x => x.d)
            .ThenByDescending(x => x.cheb)
            .ThenBy(x => x.c, StringComparer.Ordinal)
            .Select(x => x.c)
            .FirstOrDefault();
    }

    // ── Utilidades ───────────────────────────────────────────────────────────
    public static string N(string coord) => (coord ?? "").Trim().ToUpperInvariant();

    public static (int sala, int slot) ParseClave(string clave)
    {
        // "s{sala}n{slot}"
        var i = clave.IndexOf('n');
        int sala = int.TryParse(clave[1..i], out var s) ? s : 0;
        int slot = int.TryParse(clave[(i + 1)..], out var n) ? n : 0;
        return (sala, slot);
    }

    /// Todas las casillas del rectángulo [desde]..[hasta] (inclusive).
    public static List<string> Rect(string desde, string hasta)
    {
        var a = Parse(N(desde))!.Value;
        var b = Parse(N(hasta))!.Value;
        var res = new List<string>();
        for (int r = Math.Min(a.r, b.r); r <= Math.Max(a.r, b.r); r++)
            for (int c = Math.Min(a.c, b.c); c <= Math.Max(a.c, b.c); c++)
                res.Add(Format(r, c));
        return res;
    }

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

    public static int Manhattan(string a, string b)
    {
        var pa = Parse(a); var pb = Parse(b);
        if (pa == null || pb == null) return 99;
        return Math.Abs(pa.Value.r - pb.Value.r) + Math.Abs(pa.Value.c - pb.Value.c);
    }

    public static int Chebyshev(string a, string b)
    {
        var pa = Parse(a); var pb = Parse(b);
        if (pa == null || pb == null) return 99;
        return Math.Max(Math.Abs(pa.Value.r - pb.Value.r), Math.Abs(pa.Value.c - pb.Value.c));
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
