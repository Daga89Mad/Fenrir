// ─────────────────────────────────────────────────────────────────────────────
// ArenaDuelos.cs
//
// ARENA OFFLINE del modo Duelos: juega partidas completas bot contra bot en
// memoria (DuelosMotor + DuelosBot) y resume el equilibrio: duración, tablas,
// % de victorias por ejército, general y pareja, cómo muere el rey, si cae
// antes el señuelo y cuánto se usa cada habilidad.
//
// Lógica pura: se puede llamar desde un endpoint de administración o desde
// una consola local (así se han hecho las primeras simulaciones).
// ─────────────────────────────────────────────────────────────────────────────

using System.Collections.Concurrent;
using System.Text;

public sealed class ArenaDuelosOpciones
{
    public int Partidas = 1000;
    public int Semilla = 1;
    /// 0 = tantos hilos como núcleos.
    public int Hilos = 0;
    /// Ejército fijo de cada lado (null = al azar en cada partida).
    public DuelosEjercito? Ejercito0;
    public DuelosEjercito? Ejercito1;
    public int PlanesPropios = 14;
    public int PlanesRival = 5;
    public double PesoAmenazaRey = 8;
    public double PesoPresion = 4;
    public double PesoDistancia = 3;
}

public sealed class ArenaDuelosPartida
{
    public int Semilla;
    public DuelosEjercito[] Ejercitos = new DuelosEjercito[2];
    public string[][] Generales = { new string[2], new string[2] };
    public int[] ReyIndice = new int[2];
    /// 0 / 1 / 2 (tablas).
    public int Ganador;
    public int Turnos;
    public string Motivo = "";
    /// Por jugador: "rey", "senuelo" o "" según qué general cayó primero.
    public string[] PrimeraBaja = { "", "" };
    /// Por jugador: veces que se ha usado cada habilidad / disparo.
    public Dictionary<string, int>[] Usos = { new(), new() };
    /// Por jugador: turno en que usó la habilidad de ejército (0 = no la usó).
    public int[] TurnoEjercito = new int[2];
    /// Por jugador, al acabar: vidas de generales que le quedan, generales
    /// muertos y soldados vivos.
    public int[] VidasFinal = new int[2];
    public int[] GeneralesMuertos = new int[2];
    public int[] SoldadosFinal = new int[2];
}

public static class ArenaDuelos
{
    public static List<ArenaDuelosPartida> Ejecutar(ArenaDuelosOpciones o, Action<int>? progreso = null)
    {
        var res = new ConcurrentBag<ArenaDuelosPartida>();
        int hechas = 0;
        var po = new ParallelOptions { MaxDegreeOfParallelism = o.Hilos > 0 ? o.Hilos : Environment.ProcessorCount };
        Parallel.For(0, o.Partidas, po, i =>
        {
            res.Add(JugarUna(o.Semilla * 100003 + i, o));
            int n = Interlocked.Increment(ref hechas);
            progreso?.Invoke(n);
        });
        return res.OrderBy(p => p.Semilla).ToList();
    }

    public static ArenaDuelosPartida JugarUna(int semilla, ArenaDuelosOpciones o)
    {
        var rng = new Random(semilla);
        var r = new ArenaDuelosPartida { Semilla = semilla };
        var todos = Enum.GetValues<DuelosEjercito>();
        var cfg = new DuelosConfigJugador[2];
        for (int j = 0; j < 2; j++)
        {
            var ej = (j == 0 ? o.Ejercito0 : o.Ejercito1) ?? todos[rng.Next(todos.Length)];
            var gens = DuelosCatalogo.Ejercitos[ej].Generales.OrderBy(_ => rng.Next()).Take(2).Select(g => g.Nombre).ToArray();
            int rey = rng.Next(2);
            cfg[j] = new DuelosConfigJugador
            {
                Ejercito = ej,
                Generales = gens,
                ReyIndice = rey,
                Despliegue = DuelosBot.ConRey(DuelosBot.Desplegar(j, rng), rey),
            };
            r.Ejercitos[j] = ej;
            r.Generales[j] = gens;
            r.ReyIndice[j] = rey;
        }

        var e = DuelosMotor.Crear(cfg[0], cfg[1], rng);
        var bot = new DuelosBot
        {
            PlanesPropios = o.PlanesPropios,
            PlanesRival = o.PlanesRival,
            PesoAmenazaRey = o.PesoAmenazaRey,
            PesoPresion = o.PesoPresion,
            PesoDistancia = o.PesoDistancia,
        };
        var reyId = new[] { e.Jugadores[0].ReyId, e.Jugadores[1].ReyId };

        while (!e.Terminada && e.Turno <= DuelosAjustes.TurnoLimite + 1)
        {
            var j0 = bot.Decidir(e, 0, rng);
            var j1 = bot.Decidir(e, 1, rng);
            DuelosMotor.Resolver(e, j0, j1, rng);
            foreach (var ev in e.Eventos)
            {
                if (ev.Jugador is < 0 or > 1) continue;
                if (ev.Tipo.StartsWith("habilidad:") || ev.Tipo.StartsWith("ejercito:") || ev.Tipo == "disparo")
                {
                    var u = r.Usos[ev.Jugador];
                    u[ev.Tipo] = u.GetValueOrDefault(ev.Tipo) + 1;
                    if (ev.Tipo.StartsWith("ejercito:")) r.TurnoEjercito[ev.Jugador] = e.Turno - 1;
                }
            }
            // Primera baja de general de cada jugador (vida perdida, no muerte).
            for (int j = 0; j < 2; j++)
            {
                if (r.PrimeraBaja[j] != "") continue;
                var heridos = e.Cartas.Where(c => c.Dueno == j && c.EsGeneral && !c.EsClon && c.Vidas < DuelosAjustes.VidasGeneral).ToList();
                if (heridos.Count == 0) continue;
                r.PrimeraBaja[j] = heridos.Any(c => c.Id == reyId[j]) ? "rey" : "senuelo";
            }
        }
        for (int j = 0; j < 2; j++)
        {
            r.VidasFinal[j] = e.Cartas.Where(c => c.Dueno == j && c.Viva && c.EsGeneral && !c.EsClon).Sum(c => c.Vidas);
            r.GeneralesMuertos[j] = e.Cartas.Count(c => c.Dueno == j && !c.Viva && c.EsGeneral && !c.EsClon);
            r.SoldadosFinal[j] = e.Cartas.Count(c => c.Dueno == j && c.Viva && !c.EsGeneral);
        }
        r.Ganador = e.Ganador < 0 ? 2 : e.Ganador;
        r.Turnos = e.Turno - 1;
        r.Motivo = e.Ganador < 0 ? "sin terminar" : e.Motivo;
        return r;
    }

    // ═════════════════════════════════════════════════════════════════════════
    // INFORME
    // ═════════════════════════════════════════════════════════════════════════

    /// Puntuación de un lado: victoria 1, tablas 0,5, derrota 0.
    private static double Punt(ArenaDuelosPartida p, int j) => p.Ganador == j ? 1 : p.Ganador == 2 ? 0.5 : 0;

    public static string Informe(List<ArenaDuelosPartida> ps, string titulo = "Arena de Duelos")
    {
        var sb = new StringBuilder();
        int n = ps.Count;
        sb.AppendLine($"# {titulo}");
        sb.AppendLine();
        sb.AppendLine($"Partidas: {n}. Turnos de media: {ps.Average(p => p.Turnos):0.0} (mediana {Mediana(ps.Select(p => (double)p.Turnos)):0}).");
        sb.AppendLine($"Gana el jugador 0 (filas A-B): {Pct(ps.Count(p => p.Ganador == 0), n)} · jugador 1: {Pct(ps.Count(p => p.Ganador == 1), n)} · tablas: {Pct(ps.Count(p => p.Ganador == 2), n)}.");
        sb.AppendLine();

        sb.AppendLine($"Al acabar, por jugador: {ps.Average(p => (p.VidasFinal[0] + p.VidasFinal[1]) / 2.0):0.00} vidas de general de {2 * DuelosAjustes.VidasGeneral}, " +
                      $"{ps.Average(p => (p.GeneralesMuertos[0] + p.GeneralesMuertos[1]) / 2.0):0.00} generales muertos y {ps.Average(p => (p.SoldadosFinal[0] + p.SoldadosFinal[1]) / 2.0):0.00} soldados vivos de 4.");
        var lim = ps.Where(p => p.Motivo == "limite").ToList();
        if (lim.Count > 0)
            sb.AppendLine($"En las que llegan al límite, diferencia media de vidas: {lim.Average(p => Math.Abs(p.VidasFinal[0] - p.VidasFinal[1])):0.00}; se deciden por vidas {Pct(lim.Count(p => p.VidasFinal[0] != p.VidasFinal[1]), lim.Count)}.");
        sb.AppendLine();
        sb.AppendLine("## Cómo acaba");
        sb.AppendLine("| Motivo | Partidas | % |");
        sb.AppendLine("|---|---|---|");
        foreach (var g in ps.GroupBy(p => p.Motivo).OrderByDescending(g => g.Count()))
            sb.AppendLine($"| {g.Key} | {g.Count()} | {Pct(g.Count(), n)} |");
        sb.AppendLine();

        // ── Ejércitos ────────────────────────────────────────────────────────
        var ejs = Enum.GetValues<DuelosEjercito>();
        sb.AppendLine("## Ejércitos (sin espejos)");
        sb.AppendLine("| Ejército | Partidas | Puntuación |");
        sb.AppendLine("|---|---|---|");
        foreach (var ej in ejs)
        {
            var lados = ps.Where(p => p.Ejercitos[0] != p.Ejercitos[1])
                .SelectMany(p => new[] { 0, 1 }.Where(j => p.Ejercitos[j] == ej).Select(j => Punt(p, j))).ToList();
            sb.AppendLine($"| {ej} | {lados.Count} | {Pct(lados.Sum(), lados.Count)} |");
        }
        sb.AppendLine();
        sb.AppendLine("Cruces (puntuación de la fila contra la columna):");
        sb.AppendLine();
        sb.AppendLine("| | " + string.Join(" | ", ejs) + " |");
        sb.AppendLine("|---|" + string.Concat(ejs.Select(_ => "---|")));
        foreach (var a in ejs)
        {
            var celdas = ejs.Select(b =>
            {
                var l = ps.SelectMany(p => new[] { 0, 1 }
                        .Where(j => p.Ejercitos[j] == a && p.Ejercitos[1 - j] == b)
                        .Select(j => Punt(p, j))).ToList();
                return a == b ? "—" : l.Count == 0 ? "" : $"{Pct(l.Sum(), l.Count)} ({l.Count})";
            });
            sb.AppendLine($"| {a} | " + string.Join(" | ", celdas) + " |");
        }
        sb.AppendLine();

        // ── Generales ────────────────────────────────────────────────────────
        sb.AppendLine("## Generales (todas las partidas en que juegan)");
        sb.AppendLine("| Ejército | General | Habilidad | Partidas | Puntuación | Usos/partida | Rey: puntuación |");
        sb.AppendLine("|---|---|---|---|---|---|---|");
        foreach (var ej in ejs)
            foreach (var g in DuelosCatalogo.Ejercitos[ej].Generales)
            {
                var lados = ps.SelectMany(p => new[] { 0, 1 }
                    .Where(j => p.Ejercitos[j] == ej && p.Generales[j].Contains(g.Nombre))
                    .Select(j => (p, j))).ToList();
                if (lados.Count == 0) continue;
                double usos = lados.Average(x => (double)x.p.Usos[x.j].GetValueOrDefault("habilidad:" + g.Habilidad));
                var comoRey = lados.Where(x => x.p.Generales[x.j][x.p.ReyIndice[x.j]] == g.Nombre).ToList();
                sb.AppendLine($"| {ej} | {g.Nombre} | {g.Habilidad} | {lados.Count} | {Pct(lados.Sum(x => Punt(x.p, x.j)), lados.Count)} | {usos:0.00} | {Pct(comoRey.Sum(x => Punt(x.p, x.j)), comoRey.Count)} |");
            }
        sb.AppendLine();

        // ── Parejas ──────────────────────────────────────────────────────────
        sb.AppendLine("## Parejas");
        sb.AppendLine("| Ejército | Pareja | Partidas | Puntuación |");
        sb.AppendLine("|---|---|---|---|");
        var parejas = ps.SelectMany(p => new[] { 0, 1 }.Select(j => (
                clave: (p.Ejercitos[j], string.Join(" + ", p.Generales[j].OrderBy(x => x))),
                punt: Punt(p, j))))
            .GroupBy(x => x.clave)
            .Select(g => (g.Key, n: g.Count(), s: g.Sum(x => x.punt) / g.Count()))
            .OrderByDescending(x => x.s);
        foreach (var x in parejas)
            sb.AppendLine($"| {x.Key.Item1} | {x.Key.Item2} | {x.n} | {x.s * 100:0}% |");
        sb.AppendLine();

        // ── Rey y señuelo ────────────────────────────────────────────────────
        var decididas = ps.Where(p => p.Ganador is 0 or 1).ToList();
        var lados2 = ps.SelectMany(p => new[] { 0, 1 }.Select(j => p.PrimeraBaja[j])).Where(s => s != "").ToList();
        sb.AppendLine("## Rey y señuelo");
        sb.AppendLine($"Primer general herido de cada jugador: señuelo {Pct(lados2.Count(s => s == "senuelo"), lados2.Count)} · rey {Pct(lados2.Count(s => s == "rey"), lados2.Count)}.");
        sb.AppendLine();

        // ── Habilidades de ejército y disparos ───────────────────────────────
        sb.AppendLine("## Recursos de un solo uso");
        sb.AppendLine("| Ejército | Habilidad | La usa | Turno medio | Puntuación si la usa | Si no |");
        sb.AppendLine("|---|---|---|---|---|---|");
        foreach (var ej in ejs)
        {
            var lados = ps.SelectMany(p => new[] { 0, 1 }.Where(j => p.Ejercitos[j] == ej).Select(j => (p, j))).ToList();
            var usan = lados.Where(x => x.p.TurnoEjercito[x.j] > 0).ToList();
            var no = lados.Where(x => x.p.TurnoEjercito[x.j] == 0).ToList();
            sb.AppendLine($"| {ej} | {DuelosCatalogo.Ejercitos[ej].HabilidadEjercito} | {Pct(usan.Count, lados.Count)} | {(usan.Count > 0 ? usan.Average(x => x.p.TurnoEjercito[x.j]) : 0):0.0} | {Pct(usan.Sum(x => Punt(x.p, x.j)), usan.Count)} | {Pct(no.Sum(x => Punt(x.p, x.j)), no.Count)} |");
        }
        double disparos = ps.Average(p => (p.Usos[0].GetValueOrDefault("disparo") + p.Usos[1].GetValueOrDefault("disparo")) / 2.0);
        sb.AppendLine();
        sb.AppendLine($"Disparos lejanos usados por jugador y partida: {disparos:0.00} de {DuelosAjustes.DisparosLejanos}.");
        return sb.ToString();
    }

    private static string Pct(double a, double b) => b <= 0 ? "—" : $"{100.0 * a / b:0}%";

    private static double Mediana(IEnumerable<double> xs)
    {
        var l = xs.OrderBy(x => x).ToList();
        return l.Count == 0 ? 0 : l[l.Count / 2];
    }
}
