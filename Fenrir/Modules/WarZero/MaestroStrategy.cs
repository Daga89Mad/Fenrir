using System;
using System.Collections.Generic;
using System.Linq;

using Tablero = System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<System.Collections.Generic.Dictionary<string, object?>>>;

// ─────────────────────────────────────────────────────────────────────────────
// MaestroStrategy.cs  —  DIFICULTAD MAESTRO (v12)
//
// Nace del estudio de 11 partidas (09oP, QroL, dqDY, 6eAj, NM94, SnO7, 1h2q,
// 6vnT, SSC4 y los dos retos "resistencia demoníaca"). En el mapa clásico los
// bots pierden TODAS las partidas contra el humano "pescador"; ganan solo en
// mapas sin farmeo o contra un humano que no farmea. Lo que separa al ganador:
//
//   · ECONOMÍA: 43-75 de ingreso por turno (bots que pierden: 4-15). El farmeo
//     es POR CARTA (+10 rayo, +7 isla, +5 continente ajeno) y el humano abre
//     sacrificando su acción de 100 (+50) para tener 4-7 unidades en el turno
//     1-6, con 3-4 farmeando. Los bots guardaban misiles de 100 y escudos de 50
//     en la mano diez turnos con 2-10 de energía.
//   · CONVERSIÓN: el humano juega con 1-2 cartas en mano, 9-19 evolucionadas y
//     3 generales. En la 8J dos bots murieron con 1.650 y 2.840 de energía.
//   · MASA Y REMATE: el humano mueve 1-2 stacks de 150-500 de poder; los bots
//     reparten 15-19 cartas en 5-6 celdas (decisión carta a carta) y se
//     quedan 8-19 turnos pegados a un cuartel enemigo sin entrar (reglas de
//     entrada pesimistas + stacks individuales que no las superan).
//   · CUARTEL: 7 de 9 cuarteles de bot cayeron con UNA pieza de 10-30 de
//     poder dentro (tope anti-misil) ante un stack de 160 que llevaba 2-3
//     turnos a la vista.
//
// MaestroStrategy ENVUELVE la EstrategaSoftmaxStrategy (que ya contiene las
// variantes de estilo, la defensa, la cacería y el farmeo) y añade, por turno:
//
//   1. APERTURA (contexto): sacrificio de acciones caras impagables mientras
//      la economía no está montada (lo hace también la variante "libre", pero
//      aquí se aplica ANTES de generar candidatos: así vale para cualquier
//      plan que gane la softmax, incluido el farmeo de apertura).
//   2. ASEDIO EN MASA (plan propio, determinista): cuando la masa móvil del
//      bot supera con margen la defensa estimada de un cuartel enemigo, TODA
//      la masa marcha en bola hacia él (ReglasEntrada.AvanceCohesionado: el
//      subgrupo que llega entra solo si gana junto). Si la guarnición rival
//      vale un disparo y el bot lo puede pagar, primero dispara al cuartel y
//      entra al turno siguiente (el patrón con el que el humano gana). El
//      asedio se suspende si el propio cuartel no aguanta una entrada.
//   3. EXCEDENTE (post-plan): con más de TopeOcioso de energía sin asignar, se
//      compra el mejor general, se despliegan más unidades (que salen del
//      cuartel ese mismo turno hacia el farmeo o el asedio, sin fabricar cebo)
//      y, con la mano sin unidades, se ROBA una carta (100).
//
// Todo lo demás (defensa del cuartel, caza, farmeo, habilidades) sigue siendo
// la softmax con el perfil Maestro de EstrategaStrategy (que ya incluye Alto).
// ─────────────────────────────────────────────────────────────────────────────
public class MaestroStrategy : IBotStrategy
{
    /// Unidades MÓVILES a partir de las cuales se considera montada la economía
    /// (fin de la apertura: ya se puede ahorrar para evolucionar).
    public const int UnidadesEconomia = 6;

    /// Energía de apertura a la que se aspira antes de dejar de sacrificar.
    public const int EnergiaObjetivoApertura = 60;

    /// Coste mínimo de una carta de acción para plantearse su sacrificio.
    public const int CosteMinSacrificio = 40;

    /// Energía ociosa máxima tras el plan del turno: por encima se convierte
    /// (general → despliegues → robo). El humano ganador promedia 170-360 pero
    /// gasta todo lo que entra; los bots que perdieron con 500-800 de media no.
    public const int TopeOcioso = 100;

    /// Turnos de apertura durante los que se sacrifican acciones impagables.
    public const int TurnosApertura = 8;

    private readonly WarZeroBotOptions _opt;
    private readonly PerfilBot _perfil;
    private readonly EstrategaSoftmaxStrategy _base;
    private readonly EstadoAsedioMaestro _asedio = new();
    private readonly Random _rng = new();

    /// Modo del plan elegido en la última decisión (para EstudioPartidas):
    /// el de la softmax, o "maestro:asedio" / "maestro:asedio+disparo".
    public string UltimoModo { get; private set; } = "libre";

    public MaestroStrategy(WarZeroBotOptions opt, PerfilBot? perfil)
    {
        _opt = opt;
        _perfil = perfil ?? new PerfilBot { Dificultad = DificultadBot.Maestro };
        _base = new EstrategaSoftmaxStrategy(opt, _perfil);
    }

    public BotMove DecidirJugada(BotContext ctxOriginal)
    {
        // Lente de reto (otros bots no son enemigos). Idéntico a la softmax.
        var ctx = RetoFoco.Aplicar(ctxOriginal);

        // 1) APERTURA: sacrificio de acciones caras impagables → contexto con
        //    más energía y menos mano. Cualquier plan posterior parte de aquí.
        var (ctxJuego, retornoSacrificio, sacrificadas) = SacrificioApertura(ctx);
        if (sacrificadas.Count > 0)
            Console.WriteLine($"[WZ][maestro {ctx.BotUid}] apertura: sacrifica {sacrificadas.Count} carta(s) (+{retornoSacrificio})");

        // 2) ASEDIO EN MASA, si procede; si no, la softmax de siempre.
        BotMove? plan = null;
        string modo = "libre";
        try
        {
            var asedio = PlanificadorAsedioMaestro.Generar(ctxJuego, _asedio);
            if (asedio.HasValue)
            {
                // Misma red final que cualquier candidato de la softmax.
                plan = ReglasEntrada.SanearPlan(ctxJuego, asedio.Value.plan, out int arreglos);
                if (arreglos > 0)
                    Console.WriteLine($"[WZ][maestro {ctx.BotUid}] asedio: {arreglos} entrada(s) corregida(s)");
                modo = asedio.Value.modo;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[WZ][maestro {ctx.BotUid}] planificador de asedio falló ({ex.GetType().Name}: {ex.Message}); softmax");
            plan = null;
        }

        if (plan == null)
        {
            plan = _base.DecidirJugada(ctxJuego);
            modo = _base.UltimoModo;
        }

        // 3) EXCEDENTE: convertir la energía ociosa en tablero.
        try { plan = GastarExcedente(ctxJuego, plan, _asedio.Objetivo); }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[WZ][maestro {ctx.BotUid}] gastar excedente falló ({ex.GetType().Name}: {ex.Message}); plan sin excedente");
        }

        // 4) Contabilizar el sacrificio (energía GANADA ⇒ gastado negativo). La
        //    mano del plan ya nace sin las sacrificadas (todo candidato partió
        //    de ctxJuego.Mano), así que solo se ajusta la energía.
        if (retornoSacrificio > 0)
            plan = new BotMove
            {
                Celdas = plan.Celdas,
                Acciones = plan.Acciones,
                ManoResultante = plan.ManoResultante,
                EnergiaGastada = plan.EnergiaGastada - retornoSacrificio,
                EnergiaAcciones = plan.EnergiaAcciones,
                EspecialComprada = plan.EspecialComprada,
                RobosComprados = plan.RobosComprados,
            };

        UltimoModo = modo;
        return plan;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 1) SACRIFICIO DE APERTURA (sobre el contexto)
    // ─────────────────────────────────────────────────────────────────────────
    private static (BotContext ctx, int retorno, List<string> sacrificadas) SacrificioApertura(BotContext ctx)
    {
        var sacrificadas = new List<string>();
        int unidadesMoviles = 0;
        foreach (var (_, cartas) in ReglasEntrada.TableroDesde(ctx.Estado))
            foreach (var c in cartas)
                if (M.Str(M.Get(c, "ownerUid")) == ctx.BotUid && ReglasEntrada.Mov(c) > 0) unidadesMoviles++;

        bool apertura = ctx.Turno <= TurnosApertura || unidadesMoviles < UnidadesEconomia;
        if (!apertura || ctx.Energia >= EnergiaObjetivoApertura) return (ctx, 0, sacrificadas);

        int energia = ctx.Energia, retorno = 0;
        var mano = new List<string>(ctx.Mano);
        var caras = mano
            .Where(id => ctx.CatalogoMano.TryGetValue(id, out var b)
                         && AccionesTacticas.EsCartaAccion(b)
                         && AccionesTacticas.Coste(b) >= CosteMinSacrificio
                         && AccionesTacticas.Coste(b) > energia)
            .OrderByDescending(id => AccionesTacticas.Coste(ctx.CatalogoMano[id]))
            .ToList();
        int disparos = caras.Count(id => EsDisparo(ctx.CatalogoMano[id]));
        foreach (var id in caras)
        {
            if (energia >= EnergiaObjetivoApertura) break;
            var b = ctx.CatalogoMano[id];
            if (EsDisparo(b))
            {
                // El disparo es el arma que rompe cuarteles: solo se sacrifica
                // en el arranque puro (T≤4, +50 ahora vale más) o si hay otro.
                if (ctx.Turno > 4 && disparos <= 1) continue;
                disparos--;
            }
            int r = AccionesTacticas.Coste(b) / 2;
            energia += r; retorno += r;
            mano.Remove(id);
            sacrificadas.Add(id);
        }
        if (retorno == 0) return (ctx, 0, sacrificadas);

        var nuevo = new BotContext
        {
            Estado = ctx.Estado,
            BotUid = ctx.BotUid,
            Turno = ctx.Turno,
            Cuartel = ctx.Cuartel,
            Energia = energia,
            Mano = mano,
            CatalogoMano = ctx.CatalogoMano,
            Zona = ctx.Zona,
            Terreno = ctx.Terreno,
            Filas = ctx.Filas,
            Columnas = ctx.Columnas,
            IslaCentral = ctx.IslaCentral,
            Continentes = ctx.Continentes,
            Rayos = ctx.Rayos,
            RayosTurnos = ctx.RayosTurnos,
            EjercitoId = ctx.EjercitoId,
            Evoluciones = ctx.Evoluciones,
            GeneralesDisponibles = ctx.GeneralesDisponibles,
        };
        return (nuevo, retorno, sacrificadas);
    }

    private static bool EsDisparo(Dictionary<string, object?> b)
        => AccionesTacticas.Catalogo.TryGetValue(M.Int(M.Get(b, "IdHabilidad", "idHabilidad")), out var h)
           && h.Efecto == AccionesTacticas.Efecto.Disparo;

    // ─────────────────────────────────────────────────────────────────────────
    // 3) EXCEDENTE (sobre el plan elegido)
    // ─────────────────────────────────────────────────────────────────────────
    private BotMove GastarExcedente(BotContext ctx, BotMove plan, string? objetivoAsedio)
    {
        string miCuartel = ctx.Cuartel;
        if (string.IsNullOrEmpty(miCuartel)) return plan;

        int restante = ctx.Energia - plan.EnergiaGastada - plan.EnergiaAcciones;
        // Reserva para un disparo en mano pagable pronto (no se "gasta" el misil).
        int reservaDisparo = ctx.Mano
            .Where(id => ctx.CatalogoMano.TryGetValue(id, out var b) && AccionesTacticas.EsCartaAccion(b) && EsDisparo(b))
            .Select(id => AccionesTacticas.Coste(ctx.CatalogoMano[id]))
            .DefaultIfEmpty(0).Min();
        int tope = Math.Max(TopeOcioso, reservaDisparo + 20);
        if (restante <= tope) return plan;

        var k = ReglasEntrada.Crear(ctx);
        var celdas = new Tablero();
        foreach (var (coord, lista) in plan.Celdas) celdas[coord] = new List<Dictionary<string, object?>>(lista);
        var mano = new List<string>(plan.ManoResultante);
        int gastado = plan.EnergiaGastada;
        string? especial = plan.EspecialComprada;
        int robos = plan.RobosComprados;
        int cambios = 0;

        // Poder propio por celda en el plan (para que PasoSeguro vea el grupo).
        var sums = new Dictionary<string, (int f, int d)>();
        foreach (var (coord, lista) in celdas)
        {
            int f = 0, d = 0;
            foreach (var c in lista)
                if (M.Str(M.Get(c, "ownerUid")) == ctx.BotUid) { f += ReglasEntrada.Fuerza(c); d += ReglasEntrada.Defensa(c); }
            sums[coord] = (f, d);
        }
        (int f, int d) Extra(string c) => sums.TryGetValue(c, out var s) ? s : (0, 0);
        void Coloca(string coord, Dictionary<string, object?> card)
        {
            if (!celdas.TryGetValue(coord, out var l)) { l = new(); celdas[coord] = l; }
            l.Add(card);
            var s = Extra(coord);
            sums[coord] = (s.f + ReglasEntrada.Fuerza(card), s.d + ReglasEntrada.Defensa(card));
        }

        bool rivalConMisil = AccionesTacticas.RivalesConMisil(ctx).Count > 0;
        int CosteEnCasa() => celdas.TryGetValue(miCuartel, out var l)
            ? l.Where(c => M.Str(M.Get(c, "ownerUid")) == ctx.BotUid).Sum(ReglasEntrada.Coste) : 0;

        // Destino de una unidad recién desplegada: hacia el asedio si lo hay,
        // si no hacia la celda de farmeo más cercana; nunca se queda en el
        // cuartel si eso fabrica un cebo de misil.
        string DestinoNueva(Dictionary<string, object?> card)
        {
            string? objetivo = objetivoAsedio;
            if (objetivo == null)
            {
                int mejorD = int.MaxValue;
                foreach (var c in k.Rayos.Concat(k.IslaCentral))
                {
                    int d = ReglasEntrada.Manhattan(miCuartel, c);
                    if (d < mejorD) { mejorD = d; objetivo = c; }
                }
            }
            string paso = ReglasEntrada.PasoSeguro(k, miCuartel, card, objetivo, Extra);
            if (paso == miCuartel && rivalConMisil && CosteEnCasa() + ReglasEntrada.Coste(card) > AccionesTacticas.UmbralCosteCebo)
            {
                // Cualquier celda alcanzable y permitida fuera del cuartel.
                foreach (var c in k.Alcance(miCuartel, ReglasEntrada.Mov(card), ReglasEntrada.Tipo(card)))
                    if (ReglasEntrada.EntradaPermitida(k, c, ReglasEntrada.Fuerza(card) + Extra(c).f, ReglasEntrada.Defensa(card) + Extra(c).d))
                        return c;
            }
            return paso;
        }

        // (a) GENERAL: la mejor compra del juego (75-115 de poder por 45-150).
        if (especial == null && ctx.GeneralesDisponibles.Count > 0)
        {
            var g = ctx.GeneralesDisponibles
                .Where(x => AccionesTacticas.Coste(x) > 0 && AccionesTacticas.Coste(x) <= restante - reservaDisparo)
                .OrderByDescending(x => ReglasEntrada.Fuerza(x) + ReglasEntrada.Defensa(x))
                .FirstOrDefault();
            if (g != null)
            {
                var gid = M.Str(M.Get(g, "id"));
                var nu = NuevaUnidad(g, gid, ctx.BotUid, ctx.Zona);
                Coloca(DestinoNueva(nu), nu);
                int coste = AccionesTacticas.Coste(g);
                restante -= coste; gastado += coste; especial = gid; cambios++;
                Console.WriteLine($"[WZ][maestro {ctx.BotUid}] excedente: compra {M.Str(M.Get(g, "Nombre", "nombre"))} ({coste})");
            }
        }

        // (b) DESPLIEGUES EXTRA: unidades móviles de la mano, mejor poder por
        //     energía primero, mientras sobre energía por encima del tope.
        var desplegables = mano
            .Where(id => ctx.CatalogoMano.TryGetValue(id, out var b)
                         && !AccionesTacticas.EsCartaAccion(b) && !ReglasEntrada.EsEstatica(b)
                         && ReglasEntrada.Mov(b) > 0
                         && ReglasEntrada.CanLand(miCuartel, ReglasEntrada.Tipo(b), ctx.Terreno))
            .OrderByDescending(id =>
            {
                var b = ctx.CatalogoMano[id];
                return (ReglasEntrada.Fuerza(b) + ReglasEntrada.Defensa(b)) / (double)Math.Max(1, ReglasEntrada.Coste(b));
            })
            .ToList();
        int extra = 0;
        foreach (var id in desplegables)
        {
            if (restante <= tope) break;
            if (extra >= _opt.MaxDeploysPorTurno + 3) break;
            var b = ctx.CatalogoMano[id];
            int coste = ReglasEntrada.Coste(b);
            if (coste <= 0 || restante - coste < reservaDisparo) continue;
            var nu = NuevaUnidad(b, id, ctx.BotUid, ctx.Zona);
            Coloca(DestinoNueva(nu), nu);
            mano.Remove(id);
            restante -= coste; gastado += coste; extra++; cambios++;
        }
        if (extra > 0) Console.WriteLine($"[WZ][maestro {ctx.BotUid}] excedente: {extra} despliegue(s) extra");

        // (c) ROBO: sin unidades desplegables en mano y energía de sobra, se
        //     roba una carta (precio 100·2^n, se reinicia cada turno). Las
        //     cartas robadas se juegan el turno siguiente.
        bool quedanUnidades = mano.Any(id => ctx.CatalogoMano.TryGetValue(id, out var b)
                                             && !AccionesTacticas.EsCartaAccion(b) && !ReglasEntrada.EsEstatica(b));
        if (!quedanUnidades)
        {
            var pool = M.List(M.Get(M.Map(M.Get(M.Map(M.Get(ctx.Estado, "statsPartida")), ctx.BotUid)), "mazoPool"))
                .Select(M.Str).Where(s => s != "").ToList();
            int robosPrevios = M.Int(M.Get(M.Map(M.Get(M.Map(M.Get(ctx.Estado, "statsPartida")), ctx.BotUid)), "robosComprados"));
            while (pool.Count > 0 && robos < 2)
            {
                int precio = 100 * (1 << (robosPrevios + robos));
                if (restante - precio < tope) break;
                var id = pool[_rng.Next(pool.Count)];
                mano.Add(id);
                restante -= precio; gastado += precio; robos++; cambios++;
                Console.WriteLine($"[WZ][maestro {ctx.BotUid}] excedente: roba carta ({precio})");
            }
        }

        if (cambios == 0) return plan;
        return new BotMove
        {
            Celdas = celdas,
            Acciones = plan.Acciones,
            ManoResultante = mano,
            EnergiaGastada = gastado,
            EnergiaAcciones = plan.EnergiaAcciones,
            EspecialComprada = especial,
            RobosComprados = robos,
        };
    }

    internal static Dictionary<string, object?> NuevaUnidad(
        Dictionary<string, object?> baseCard, string id, string uid, string zona)
        => new(baseCard)
        {
            ["id"] = id,
            ["ownerUid"] = uid,
            ["ownerZone"] = zona,
            ["instanceId"] = Guid.NewGuid().ToString("N"),
        };
}

// ─────────────────────────────────────────────────────────────────────────────
// ESTADO DEL ASEDIO (vive toda la partida en la estrategia)
// ─────────────────────────────────────────────────────────────────────────────
public sealed class EstadoAsedioMaestro
{
    /// Cuartel enemigo objetivo (histéresis entre turnos). Null = sin asedio.
    public string? Objetivo { get; set; }
    /// Turnos seguidos en asedio sobre `Objetivo`.
    public int Turnos { get; set; }
    /// Último turno en que se disparó al objetivo (para entrar al siguiente).
    public int TurnoDisparo { get; set; } = -1;
}

// ─────────────────────────────────────────────────────────────────────────────
// PLANIFICADOR DE ASEDIO EN MASA (Maestro)
//
// Decide por GRUPO, no por carta. Si la masa móvil del bot supera con margen la
// defensa estimada de un cuartel enemigo (guarnición pesimista + refuerzo que
// su dueño puede desplegar + bono real del cuartel), TODA la masa marcha en
// bola hacia él (AvanceCohesionado). Mientras marcha sigue farmeando lo que
// pise (PasoSeguro prefiere celdas con energía a igual distancia).
//
// Combo del humano: si la guarnición rival vale ≥ UmbralCosteCebo y el bot
// tiene un disparo pagable, DISPARA al cuartel (la guarnición es lo único que
// no se mueve: el misil siempre acierta) y nadie entra ese turno; al
// siguiente, el cuartel está vacío o casi y la masa entra.
//
// El asedio NO se declara (devuelve null → softmax) si el propio cuartel no
// aguanta una entrada inminente con la guarnición que se deja + la trampa de
// mano: defender primero. Tampoco con menos de MinUnidades móviles.
// ─────────────────────────────────────────────────────────────────────────────
public static class PlanificadorAsedioMaestro
{
    /// Masa móvil / defensa estimada para DECLARAR el asedio.
    public const double MargenDeclarar = 1.5;
    /// Masa móvil / defensa estimada para MANTENER un asedio ya declarado.
    public const double MargenMantener = 1.15;
    public const int MinUnidades = 3;
    public const int MaxGuarnicion = 2;
    public const int MaxDespliegues = 6;
    /// Turnos máximos en asedio sin progreso antes de soltar el objetivo
    /// (evita el cerco eterno que se vio en SnO7: 19 turnos pegado).
    public const int TurnosMaxSinProgreso = 10;

    public static (BotMove plan, string modo)? Generar(BotContext ctx, EstadoAsedioMaestro estado)
    {
        var k = ReglasEntrada.Crear(ctx);
        if (k.CuartelesEnemigos.Count == 0) { Reset(estado); return null; }
        string botUid = ctx.BotUid;
        string miCuartel = ctx.Cuartel;
        var tablero = ReglasEntrada.TableroDesde(ctx.Estado);

        // ── Mis unidades ──
        var propias = new List<(string coord, Dictionary<string, object?> card)>();
        foreach (var (coord, cartas) in tablero)
            foreach (var c in cartas)
                if (M.Str(M.Get(c, "ownerUid")) == botUid) propias.Add((coord, c));

        // ── Guarnición: hasta MaxGuarnicion piezas de mejor perfil defensivo
        //    que ya estén en casa (lentas y baratas; nunca un general).
        var guarnicion = new List<Dictionary<string, object?>>();
        if (miCuartel != "")
            guarnicion = propias.Where(u => u.coord == miCuartel && Mov(u.card) > 0)
                .OrderByDescending(u => PerfilGuarnicion(u.card))
                .Take(MaxGuarnicion).Select(u => u.card).ToList();

        var moviles = propias
            .Where(u => Mov(u.card) > 0 && !guarnicion.Any(g => ReferenceEquals(g, u.card)))
            .ToList();

        // ── ¿Aguanta mi cuartel sin la masa? Si no, que decida la defensa. ──
        if (miCuartel != "")
        {
            int amenaza = ReglasEntrada.AmenazaSobre(k, miCuartel);
            if (amenaza > 0)
            {
                int bono = AccionesTacticas.DefensaCuartelActual(ctx.Estado, miCuartel, ctx.Turno);
                int enCasa = guarnicion.Sum(g => Fuerza(g) + Defensa(g))
                             + propias.Where(u => u.coord == miCuartel && Mov(u.card) <= 0).Sum(u => Fuerza(u.card) + Defensa(u.card));
                // Trampa de mano: las dos mejores unidades desplegables pagables.
                int trampa = ctx.Mano
                    .Where(id => ctx.CatalogoMano.TryGetValue(id, out var b)
                                 && !AccionesTacticas.EsCartaAccion(b) && !ReglasEntrada.EsEstatica(b)
                                 && ReglasEntrada.Coste(b) <= ctx.Energia)
                    .Select(id => Fuerza(ctx.CatalogoMano[id]) + Defensa(ctx.CatalogoMano[id]))
                    .OrderByDescending(p => p).Take(2).Sum();
                if (enCasa + bono + trampa < amenaza)
                {
                    Console.WriteLine($"[WZ][maestro {botUid}] asedio suspendido: cuartel amenazado ({amenaza} vs {enCasa}+{bono}+{trampa})");
                    return null;
                }
            }
        }

        // ── Despliegue hacia el asedio: unidades móviles de la mano (poder por
        //    energía), reservando el disparo más barato si lo hay y es pagable.
        var mano = new List<string>(ctx.Mano);
        int energia = ctx.Energia, gastado = 0;
        var disparo = mano
            .Where(id => ctx.CatalogoMano.TryGetValue(id, out var b) && AccionesTacticas.EsCartaAccion(b) && EsDisparoLejano(b))
            .OrderBy(id => ReglasEntrada.Coste(ctx.CatalogoMano[id]))
            .FirstOrDefault();
        int reservaDisparo = disparo != null && ReglasEntrada.Coste(ctx.CatalogoMano[disparo]) <= energia
            ? ReglasEntrada.Coste(ctx.CatalogoMano[disparo]) : 0;

        var desplegadas = new List<Dictionary<string, object?>>();
        if (miCuartel != "")
        {
            var candidatas = mano
                .Where(id => ctx.CatalogoMano.TryGetValue(id, out var b)
                             && !AccionesTacticas.EsCartaAccion(b) && !ReglasEntrada.EsEstatica(b)
                             && Mov(b) > 0
                             && ReglasEntrada.CanLand(miCuartel, Tipo(b), ctx.Terreno))
                .OrderByDescending(id => { var c = ctx.CatalogoMano[id]; return (Fuerza(c) + Defensa(c)) / (double)Math.Max(1, ReglasEntrada.Coste(c)); })
                .ToList();
            foreach (var id in candidatas)
            {
                if (desplegadas.Count >= MaxDespliegues) break;
                var b = ctx.CatalogoMano[id];
                int coste = ReglasEntrada.Coste(b);
                if (coste <= 0 || energia - coste < reservaDisparo) continue;
                var nu = MaestroStrategy.NuevaUnidad(b, id, botUid, ctx.Zona);
                desplegadas.Add(nu);
                energia -= coste; gastado += coste;
                mano.Remove(id);
            }
        }
        foreach (var nu in desplegadas) moviles.Add((miCuartel, nu));
        if (moviles.Count < MinUnidades) { Reset(estado); return null; }

        // ── Objetivo: defensa estimada de cada cuartel enemigo y masa que puede
        //    llegar (camino real). Preferencia: el objetivo previo (histéresis),
        //    luego el humano (uid que no es bot), luego el más barato y cercano.
        var candidatos = new List<(string coord, int defensa, int masa, double dist, bool humano)>();
        foreach (var q in k.CuartelesEnemigos)
        {
            string dueno = k.CuartelOwner.GetValueOrDefault(q, "");
            int guarn = k.EnemigosPorCelda.TryGetValue(q, out var gc) ? ReglasEntrada.PoderPesimista(k, gc) : 0;
            var (rf, rd) = ReglasEntrada.RefuerzoCuartel(k, dueno, ReglasEntrada.RefuerzoMaxEntrada);
            int bono = AccionesTacticas.DefensaCuartelActual(ctx.Estado, q, ctx.Turno);
            int defensa = guarn + rf + rd + bono;
            int masa = 0, n = 0; double dist = 0;
            foreach (var (uc, card) in moviles)
            {
                if (!ReglasEntrada.HayCamino(uc, q, Tipo(card), ctx.Terreno, ctx.Filas, ctx.Columnas)) continue;
                masa += Fuerza(card) + Defensa(card); n++;
                dist += ReglasEntrada.Manhattan(uc, q);
            }
            if (n < MinUnidades) continue;
            candidatos.Add((q, Math.Max(1, defensa), masa, dist / n, !dueno.StartsWith("bot_", StringComparison.Ordinal)));
        }
        if (candidatos.Count == 0) { Reset(estado); return null; }

        string? objetivo = null;
        string? agotado = null;   // objetivo previo sin progreso: se descarta este turno
        if (estado.Objetivo != null)
        {
            if (estado.Turnos >= TurnosMaxSinProgreso)
            {
                agotado = estado.Objetivo;
                Console.WriteLine($"[WZ][maestro {botUid}] asedio a {agotado} sin progreso en {estado.Turnos} turnos: se suelta");
                Reset(estado);
            }
            else if (candidatos.Any(c => c.coord == estado.Objetivo))
            {
                var prev = candidatos.First(c => c.coord == estado.Objetivo);
                if (prev.masa >= prev.defensa * MargenMantener) objetivo = prev.coord;
            }
        }
        if (objetivo == null)
        {
            var elegibles = candidatos
                .Where(c => c.coord != agotado && c.masa >= c.defensa * MargenDeclarar)
                .OrderByDescending(c => c.humano)
                .ThenBy(c => c.defensa + 6 * c.dist)
                .ToList();
            if (elegibles.Count == 0) { Reset(estado); return null; }
            objetivo = elegibles[0].coord;
            if (objetivo != estado.Objetivo) { estado.Objetivo = objetivo; estado.Turnos = 0; estado.TurnoDisparo = -1; }
        }
        estado.Turnos++;
        var obj = candidatos.First(c => c.coord == objetivo);
        Console.WriteLine($"[WZ][maestro {botUid}] ASEDIO {objetivo}: masa {obj.masa} vs defensa {obj.defensa} (turno {estado.Turnos} del asedio)");

        // ── Plan: guarnición y estáticas se quedan; las móviles avanzan en bola.
        var celdas = new Tablero();
        void Add(string coord, Dictionary<string, object?> c)
        {
            if (!celdas.TryGetValue(coord, out var lst)) { lst = new(); celdas[coord] = lst; }
            lst.Add(c);
        }
        var ocupacion = new Dictionary<string, (int f, int d)>();
        void Ocupa(string coord, Dictionary<string, object?> c)
        {
            var s = ocupacion.TryGetValue(coord, out var v) ? v : (0, 0);
            ocupacion[coord] = (s.Item1 + Fuerza(c), s.Item2 + Defensa(c));
        }
        foreach (var u in propias)
        {
            if (moviles.Any(m => ReferenceEquals(m.card, u.card))) continue;
            Add(u.coord, u.card);
            Ocupa(u.coord, u.card);
        }

        // ── Combo DISPARO → ENTRADA: guarnición rival que vale un misil y
        //    disparo pagable ⇒ este turno se dispara al cuartel y nadie entra.
        var acciones = new List<Dictionary<string, object?>>();
        int energiaAcciones = 0;
        string modo = "maestro:asedio";
        int costeGuarnicion = k.EnemigosPorCelda.TryGetValue(objetivo, out var cartasObj) ? cartasObj.Sum(ReglasEntrada.Coste) : 0;
        bool dispara = disparo != null && reservaDisparo > 0 && energia >= reservaDisparo
                       && costeGuarnicion >= AccionesTacticas.UmbralCosteCebo
                       && estado.TurnoDisparo != ctx.Turno
                       && miCuartel != "";
        if (dispara)
        {
            var b = ctx.CatalogoMano[disparo!];
            int habId = M.Int(M.Get(b, "IdHabilidad", "idHabilidad"));
            acciones.Add(AccionesTacticas.CrearAccion(
                habId, botUid, ctx.Zona, miCuartel, new List<string> { objetivo }, ctx.Turno, reservaDisparo, disparo));
            energiaAcciones += reservaDisparo;
            mano.Remove(disparo!);
            estado.TurnoDisparo = ctx.Turno;
            modo = "maestro:asedio+disparo";
            Console.WriteLine($"[WZ][maestro {botUid}] DISPARA a {objetivo} (guarnición de {costeGuarnicion} de coste) y espera para entrar");
        }

        List<string> destinos;
        if (dispara)
        {
            // Nadie termina en la celda del disparo: marcha en bola a su alrededor.
            destinos = AvanceSinEntrar(k, moviles, objetivo, ocupacion);
        }
        else
        {
            destinos = ReglasEntrada.AvanceCohesionado(k, moviles, objetivo, ocupacion);
        }
        int entran = 0;
        for (int i = 0; i < moviles.Count; i++)
        {
            Add(destinos[i], moviles[i].card);
            if (destinos[i] == objetivo) entran++;
        }
        if (entran > 0) Console.WriteLine($"[WZ][maestro {botUid}] ENTRAN {entran} en {objetivo}; {moviles.Count - entran} se acercan");

        return (new BotMove
        {
            Celdas = celdas,
            Acciones = acciones,
            ManoResultante = mano,
            EnergiaGastada = gastado,
            EnergiaAcciones = energiaAcciones,
        }, modo);
    }

    /// Avance en bola hacia `objetivo` SIN entrar en él (el disparo lo barre
    /// tras el movimiento). Cada unidad da el paso seguro más cercano al
    /// objetivo excluyendo la propia celda objetivo.
    private static List<string> AvanceSinEntrar(
        ReglasEntrada.Contexto k, List<(string coord, Dictionary<string, object?> card)> unidades,
        string objetivo, Dictionary<string, (int f, int d)> ocupacion)
    {
        var dest = new string[unidades.Count];
        var sums = new Dictionary<string, (int f, int d)>(ocupacion);
        (int f, int d) Extra(string c) => sums.TryGetValue(c, out var s) ? s : (0, 0);
        void Suma(string c, int f, int d) { var s = Extra(c); sums[c] = (s.f + f, s.d + d); }

        var orden = Enumerable.Range(0, unidades.Count)
            .OrderByDescending(i => Fuerza(unidades[i].card) + Defensa(unidades[i].card)).ToList();
        foreach (var i in orden)
        {
            var (desde, card) = unidades[i];
            int f = Fuerza(card), d = Defensa(card);
            var cand = new List<string>(k.Alcance(desde, Mov(card), Tipo(card))) { desde };
            string? mejor = null; (int, int, int, int) mejorKey = default;
            foreach (var c in cand)
            {
                if (c == objetivo) continue;
                var (ef, ed) = Extra(c);
                if (c != desde && !ReglasEntrada.EntradaPermitida(k, c, f + ef, d + ed)) continue;
                int poderCelda = f + d + ef + ed + (k.MiCuartel == c ? ReglasEntrada.BonoCuartel : 0);
                int expuesta = ReglasEntrada.Expuesta(k, c, poderCelda) ? 1 : 0;
                var key = (expuesta, ReglasEntrada.Manhattan(c, objetivo), -ReglasEntrada.ValorFarmeo(k, c), c == desde ? 0 : 1);
                if (mejor == null || key.CompareTo(mejorKey) < 0) { mejor = c; mejorKey = key; }
            }
            dest[i] = mejor ?? desde;
            Suma(dest[i], f, d);
        }
        return dest.ToList();
    }

    private static void Reset(EstadoAsedioMaestro e) { e.Objetivo = null; e.Turnos = 0; e.TurnoDisparo = -1; }

    private static bool EsDisparoLejano(Dictionary<string, object?> b)
        => AccionesTacticas.Catalogo.TryGetValue(M.Int(M.Get(b, "IdHabilidad", "idHabilidad")), out var h)
           && h.Efecto == AccionesTacticas.Efecto.Disparo && h.Rango == AccionesTacticas.Rango.Cualquiera;

    private static int PerfilGuarnicion(Dictionary<string, object?> c)
    {
        int poder = Fuerza(c) + Defensa(c);
        int mov = Mov(c);
        bool general = ReglasEntrada.Condicion(c) == 5;
        return 2 * poder - 15 * mov - (general ? 200 : 0);   // misma fórmula que EstrategaStrategy
    }

    private static int Fuerza(Dictionary<string, object?> c) => ReglasEntrada.Fuerza(c);
    private static int Defensa(Dictionary<string, object?> c) => ReglasEntrada.Defensa(c);
    /// Movimiento efectivo: 0 para estáticas/acciones y para paralizadas o confundidas.
    private static int Mov(Dictionary<string, object?> c)
    {
        if (CartaHelper.EstaParalizada(c) || CartaHelper.EstaConfundida(c)) return 0;
        return ReglasEntrada.Mov(c);
    }
    private static int Tipo(Dictionary<string, object?> c) => ReglasEntrada.Tipo(c);
}

// ─────────────────────────────────────────────────────────────────────────────
// MAZOS MAESTRO (uno por ejército, 8 cartas, por NOMBRE de catálogo)
//
// Criterio (medido): 4 farmeadores baratos y móviles (cada carta sobre un rayo
// vale +10/turno: una carta de 1-8 se paga en un turno), 2 piezas de choque
// con buen poder por energía, 1 disparo lejano (sacrificio de apertura, +50; o
// el arma que vacía un cuartel más adelante) y 1 estática fuerte para
// fortificar el farmeo o el anillo del cuartel. El reparto de fin de turno es
// CON repetición desde el pool, así que la mitad de los robos son farmeadores.
//
// Los nombres se resuelven contra la colección `Cartas` (normalizados: sin
// acentos ni mayúsculas). Si un nombre no existe en el catálogo se omite; si
// quedan menos de MinCartas el mazo no se escribe y el bot juega el reparto
// por defecto.
// ─────────────────────────────────────────────────────────────────────────────
public static class MazosMaestro
{
    public const int MinCartas = 6;

    public static string DocId(int ejercitoId) => $"maestro_ej{ejercitoId}";

    public static readonly IReadOnlyDictionary<int, string[]> PorEjercito = new Dictionary<int, string[]>
    {
        // 1 · Humanos (Ciudad Celeste)
        [1] = new[]
        {
            "Eclipse Negro",              // 8 · 7/3 · mov 5 (aéreo): farmeador rápido
            "Manta escarlata",            // 8 · 10/3 · mov 3 (naval)
            "Tanqueta ónix",              // 8 · 10/5 · mov 3 (evoluciona a Tanque celeste 25/14)
            "Dron",                       // 2 · 1/1 · mov 4 (aéreo): +10/turno en un rayo por 2
            "Capitán Soren",              // 40 · 28/10 · mov 4
            "Capitán Esmeralda",          // 35 · 18/10 · mov 4 (aéreo)
            "Misil de energía Celeste",   // 100 · disparo lejano
            "Cañon de fuego celeste",     // 28 · 40/20 · estática
        },
        // 2 · Biónicos (Dunant)
        [2] = new[]
        {
            "Soldado carpa",              // 1 · 1/1 · mov 4 (naval): el mejor farmeador del juego
            "Tiburón de hierro",          // 6 · 8/2 · mov 4 (naval)
            "Cuervo de combate",          // 8 · 8/2 · mov 5 (aéreo)
            "Lobo titán",                 // 7 · 10/4 · mov 3 (evoluciona a Elefante 35/15)
            "Capitan de Dunan",           // 30 · 22/12 · mov 2
            "Capitan distrito Chromia",   // 30 · 18/8 · mov 3 (aéreo)
            "Aliento de dragon",          // 100 · disparo lejano
            "Escorpión de acero",         // 25 · 38/20 · estática
        },
        // 3 · Demonios (Gehena)
        [3] = new[]
        {
            "Diablillo",                  // 1 · 2/1 · mov 3 (aéreo)
            "Leviatan",                   // 7 · 6/3 · mov 4 (naval; evoluciona a Kraken 35/20)
            "Ala rota de Samael",         // 7 · 7/2 · mov 4 (aéreo; evoluciona a Llama del Sheol 32/20)
            "Sacerdote del castigo",      // 6 · 3/3 · mov 3
            "Astaroth",                   // 35 · 25/10 · mov 3
            "Belial",                     // 20 · 20/10 · mov 1
            "Apocalipsis",                // 100 · disparo lejano
            "Devora almas",               // 28 · 38/25 · estática
        },
        // 4 · Nefilim
        [4] = new[]
        {
            "Luz de la soberbia",         // 10 · 8/3 · mov 6 (aéreo, parálisis cercana)
            "Fuerza de la desesperanza",  // 7 · 7/7 · mov 3 (naval)
            "Destructor de la envidia",   // 8 · 7/3 · mov 4 (naval)
            "Lanzarocas de la avaricia",  // 6 · 5/5 · mov 2
            "Capitan Anac",               // 30 · 35/18 · mov 3
            "Capitan Sarieloth",          // 25 · 25/25 · mov 2
            "Ira de Sodoma",              // 100 · disparo lejano
            "Bosque de la gula",          // 25 · 25/38 · estática
        },
    };

    /// Resuelve los nombres a ids del catálogo (cartas base, estáticas o de
    /// acción del ejército indicado). Conserva el orden; omite lo que no exista.
    public static List<string> Resolver(
        Dictionary<string, Dictionary<string, object?>> catalogo, int ejercitoId, IEnumerable<string> nombres)
    {
        var porNombre = new Dictionary<string, string>();
        foreach (var (id, c) in catalogo)
        {
            int cond = M.Int(M.Get(c, "Condicion", "condicion"));
            if (cond == 1 || cond == 5) continue;                        // evoluciones y generales no se reparten
            if (M.Int(M.Get(c, "Ejercito", "ejercito")) != ejercitoId) continue;
            var n = Normalizar(M.Str(M.Get(c, "Nombre", "nombre")));
            if (n != "" && !porNombre.ContainsKey(n)) porNombre[n] = id;
        }
        var res = new List<string>();
        foreach (var nombre in nombres)
            if (porNombre.TryGetValue(Normalizar(nombre), out var id)) res.Add(id);
        return res;
    }

    private static string Normalizar(string s)
    {
        var d = s.Trim().ToLowerInvariant().Normalize(System.Text.NormalizationForm.FormD);
        var sb = new System.Text.StringBuilder(d.Length);
        foreach (var ch in d)
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch) != System.Globalization.UnicodeCategory.NonSpacingMark)
                sb.Append(ch);
        return sb.ToString().Replace("  ", " ");
    }
}
