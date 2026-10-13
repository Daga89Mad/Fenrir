using System;
using System.Collections.Generic;
using System.Linq;

using Tablero = System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<System.Collections.Generic.Dictionary<string, object?>>>;

// ─────────────────────────────────────────────────────────────────────────────
// EstrategiaHumanoSimulado.cs  —  "humano de guion" para la ARENA OFFLINE (v12)
//
// Reproduce las jugadas MÁS REPETIDAS de los dos humanos de las partidas de
// estudio (pescador y SoyLoki) como una IBotStrategy, para poder medir contra
// ellas en la arena sin esperar a partidas reales y para comprobar qué
// patrones suyos son de verdad efectivos (y copiarlos en Maestro).
//
// Patrones observados (11 partidas):
//   PESCADOR (gana las 6 del mapa clásico)
//     · T1: sacrifica su acción de 100 (+50) y abre con capitán de 30 + 3 baratas.
//     · Juega TODA la mano cada turno (1-2 cartas en mano de media).
//     · Cada carta va a la mejor celda de energía alcanzable (rayo 10 > isla 7 >
//       continente ajeno 5), apilando sin miedo: 47 de ingreso/turno.
//     · Evoluciona lo que está parado farmeando (9-19 evolucionadas) y compra
//       los 3 generales en cuanto puede.
//     · Mantiene 1-2 stacks de asalto de 150-500 de poder; entra en un cuartel
//       cuando la fuerza del stack supera guarnición + 40 (+ lo que el dueño
//       pueda desplegar); antes dispara al cuartel si la guarnición vale ≥ 55.
//     · Casa casi vacía (0,3-1,9 piezas): defiende desplegando desde la mano
//       el turno de la entrada y con la descarga.
//   SOYLOKI (gana el reto 1 vs 3)
//     · Pocas unidades (4-8) pero grandes (Kraken×2 + Llama = 162), mano de
//       11-14 cartas (no despliega todo), aparca el stack a 1-3 del cuartel
//       objetivo y espera la ventana; dispara al cuartel; usa escudo de
//       bloqueo de movimiento.
//
// Es una aproximación deliberada (no un clon): lo que importa es que la arena
// ofrezca un rival con la economía y la masa del humano real.
// ─────────────────────────────────────────────────────────────────────────────
public sealed class PerfilHumanoSimulado
{
    public string Nombre { get; init; } = "pescador";
    /// Sacrifica la acción más cara (≥50) en los dos primeros turnos.
    public bool SacrificaAccionApertura { get; init; } = true;
    /// Despliega todo lo pagable cada turno (pescador) o como mucho UNA unidad
    /// por turno, la más potente (SoyLoki).
    public bool JuegaTodaLaMano { get; init; } = true;
    /// Tope de unidades en tablero.
    public int MaxUnidades { get; init; } = 99;
    /// Evoluciona unidades que se quedan quietas (hasta 3 por turno).
    public bool Evoluciona { get; init; } = true;
    public int MaxEvolucionesPorTurno { get; init; } = 3;
    public bool CompraGenerales { get; init; } = true;
    /// Poder (F+D) móvil a partir del cual se forma el stack de asalto.
    public int PoderStackAsalto { get; init; } = 150;
    /// Dispara a un cuartel enemigo cuya guarnición cueste al menos esto.
    public bool DisparaAlCuartel { get; init; } = true;
    public int UmbralCosteDisparo { get; init; } = 55;
    /// Dispara también a stacks rivales APARCADOS (≥2 turnos quietos) caros.
    public int UmbralCosteDisparoStack { get; init; } = 90;
    /// Farmea el continente enemigo (+5 por carta).
    public bool FarmeaContinenteEnemigo { get; init; } = true;
    /// Usa la descarga del cuartel contra una entrada que no aguanta.
    public bool UsaDescarga { get; init; } = true;
    /// Energía que guarda sin gastar en despliegues (SoyLoki ahorra).
    public int Reserva { get; init; } = 0;

    public static PerfilHumanoSimulado Pescador => new() { Nombre = "pescador" };

    public static PerfilHumanoSimulado SoyLoki => new()
    {
        Nombre = "soyloki",
        JuegaTodaLaMano = false,
        MaxUnidades = 8,
        Reserva = 40,
        PoderStackAsalto = 140,
        MaxEvolucionesPorTurno = 2,
    };

    public static PerfilHumanoSimulado Parse(string? nombre) => (nombre ?? "").Trim().ToLowerInvariant() switch
    {
        "soyloki" => SoyLoki,
        _ => Pescador,
    };
}

public sealed class EstrategiaHumanoSimulado : IBotStrategy
{
    private readonly PerfilHumanoSimulado _p;
    private readonly Random _rng;
    public string UltimoModo { get; private set; } = "humano";

    public EstrategiaHumanoSimulado(PerfilHumanoSimulado? perfil = null, int? semilla = null)
    {
        _p = perfil ?? PerfilHumanoSimulado.Pescador;
        _rng = semilla.HasValue ? new Random(semilla.Value) : new Random();
    }

    public BotMove DecidirJugada(BotContext ctx)
    {
        var k = ReglasEntrada.Crear(ctx);
        var tablero = ReglasEntrada.TableroDesde(ctx.Estado);
        string uid = ctx.BotUid, miCuartel = ctx.Cuartel, zona = ctx.Zona;
        int energia = ctx.Energia, gastado = 0, energiaAcciones = 0;
        var mano = new List<string>(ctx.Mano);
        var acciones = new List<Dictionary<string, object?>>();
        string? especial = null;
        bool descargaUsada = AccionesTacticas.DescargaUsada(ctx.Estado, uid);

        // ── Mis unidades ──
        var propias = new List<(string coord, Dictionary<string, object?> card)>();
        foreach (var (coord, cartas) in tablero)
            foreach (var c in cartas)
                if (M.Str(M.Get(c, "ownerUid")) == uid) propias.Add((coord, c));

        Dictionary<string, object?> Cat(string id) => ctx.CatalogoMano[id];
        bool EsUnidad(string id) => ctx.CatalogoMano.TryGetValue(id, out var b)
                                    && !AccionesTacticas.EsCartaAccion(b) && !ReglasEntrada.EsEstatica(b);
        bool EsDisparoLejano(Dictionary<string, object?> b)
            => AccionesTacticas.EsCartaAccion(b)
               && AccionesTacticas.Catalogo.TryGetValue(M.Int(M.Get(b, "IdHabilidad", "idHabilidad")), out var h)
               && h.Efecto == AccionesTacticas.Efecto.Disparo && h.Rango == AccionesTacticas.Rango.Cualquiera;

        // ── 1. SACRIFICIO DE APERTURA: la acción más cara impagable → +coste/2 ──
        if (_p.SacrificaAccionApertura && ctx.Turno <= 2)
        {
            var cara = mano
                .Where(id => ctx.CatalogoMano.TryGetValue(id, out var b) && AccionesTacticas.EsCartaAccion(b)
                             && AccionesTacticas.Coste(b) >= 50 && AccionesTacticas.Coste(b) > energia)
                .OrderByDescending(id => AccionesTacticas.Coste(Cat(id)))
                .FirstOrDefault();
            if (cara != null)
            {
                int r = AccionesTacticas.Coste(Cat(cara)) / 2;
                energia += r; gastado -= r; mano.Remove(cara);
            }
        }

        // ── 2. GENERAL: en cuanto se puede pagar ──
        var nuevas = new List<Dictionary<string, object?>>();
        if (_p.CompraGenerales && miCuartel != "" && ctx.GeneralesDisponibles.Count > 0)
        {
            var g = ctx.GeneralesDisponibles
                .Where(x => AccionesTacticas.Coste(x) > 0 && AccionesTacticas.Coste(x) <= energia - _p.Reserva)
                .OrderByDescending(x => Fuerza(x) + Defensa(x)).FirstOrDefault();
            if (g != null)
            {
                var gid = M.Str(M.Get(g, "id"));
                nuevas.Add(Nueva(g, gid, uid, zona));
                energia -= AccionesTacticas.Coste(g); gastado += AccionesTacticas.Coste(g); especial = gid;
            }
        }

        // ── 3. DESPLIEGUE: todo lo pagable (pescador) o la mejor pieza (SoyLoki),
        //      reservando el coste de un disparo lejano si está en mano. ──
        int reservaDisparo = mano.Where(id => ctx.CatalogoMano.TryGetValue(id, out var b) && EsDisparoLejano(b))
            .Select(id => AccionesTacticas.Coste(Cat(id))).DefaultIfEmpty(0).Min();
        if (miCuartel != "")
        {
            var cand = mano.Where(EsUnidad)
                .Where(id => ReglasEntrada.CanLand(miCuartel, ReglasEntrada.Tipo(Cat(id)), ctx.Terreno))
                .OrderByDescending(id => Fuerza(Cat(id)) + Defensa(Cat(id))).ToList();
            int tope = _p.JuegaTodaLaMano ? 99 : 1;
            int enCampo = propias.Count + nuevas.Count;
            foreach (var id in cand)
            {
                if (nuevas.Count >= tope + (especial != null ? 1 : 0) || enCampo >= _p.MaxUnidades) break;
                int coste = AccionesTacticas.Coste(Cat(id));
                if (coste <= 0 || energia - coste < _p.Reserva) continue;
                // El misil se guarda si queda a tiro de un turno de ingreso.
                if (reservaDisparo > 0 && energia - coste < reservaDisparo && energia >= reservaDisparo) continue;
                nuevas.Add(Nueva(Cat(id), id, uid, zona));
                mano.Remove(id); energia -= coste; gastado += coste; enCampo++;
            }
        }

        // Unidades MÓVILES (las recién desplegadas salen del cuartel este turno).
        var moviles = propias.Where(u => Mov(u.card) > 0).ToList();
        foreach (var nu in nuevas) moviles.Add((miCuartel, nu));
        var inmoviles = propias.Where(u => Mov(u.card) <= 0).ToList();

        // ── 4. DEFENSA DE CASA ──
        var destino = new Dictionary<Dictionary<string, object?>, string>(ReferenceEqualityComparer.Instance);
        var asignadas = new HashSet<Dictionary<string, object?>>(ReferenceEqualityComparer.Instance);
        if (miCuartel != "")
        {
            int amenaza = ReglasEntrada.AmenazaSobre(k, miCuartel);
            int bono = AccionesTacticas.DefensaCuartelActual(ctx.Estado, miCuartel, ctx.Turno);
            if (amenaza > 0)
            {
                int enCasa = inmoviles.Where(u => u.coord == miCuartel).Sum(u => Fuerza(u.card) + Defensa(u.card));
                // Descarga: entrada que no se aguanta, casa casi vacía y descarga sin usar.
                bool entraGrande = amenaza > bono + enCasa + 40;
                if (_p.UsaDescarga && !descargaUsada && entraGrande && energia >= AccionesTacticas.CosteDescarga
                    && moviles.Count(u => u.coord == miCuartel) <= 1)
                {
                    acciones.Add(AccionesTacticas.CrearDescarga(uid, zona, miCuartel, ctx.Turno));
                    energiaAcciones += AccionesTacticas.CosteDescarga; descargaUsada = true;
                    // Nadie se queda en casa: la descarga mata todo lo que haya.
                    foreach (var u in moviles.Where(u => u.coord == miCuartel).ToList())
                    {
                        string fuera = k.Alcance(miCuartel, Mov(u.card), ReglasEntrada.Tipo(u.card))
                            .FirstOrDefault(c => ReglasEntrada.EntradaPermitida(k, c, Fuerza(u.card), Defensa(u.card))) ?? miCuartel;
                        destino[u.card] = fuera; asignadas.Add(u.card);
                    }
                }
                else
                {
                    // Guarnición: lo que haga falta de lo que llega, más barato primero.
                    int poderCasa = bono + enCasa;
                    foreach (var u in moviles
                        .Where(u => u.coord == miCuartel || k.Alcance(u.coord, Mov(u.card), ReglasEntrada.Tipo(u.card)).Contains(miCuartel))
                        .OrderBy(u => Fuerza(u.card) + Defensa(u.card)))
                    {
                        if (poderCasa >= amenaza) break;
                        destino[u.card] = miCuartel; asignadas.Add(u.card);
                        poderCasa += Fuerza(u.card) + Defensa(u.card);
                    }
                }
            }
        }

        // ── 5. OFENSIVA: stack de asalto ──
        var libres = moviles.Where(u => !asignadas.Contains(u.card)).ToList();
        string? objetivo = null;
        if (k.CuartelesEnemigos.Count > 0 && libres.Count > 0)
        {
            int masa = libres.Sum(u => Fuerza(u.card) + Defensa(u.card));
            if (masa >= _p.PoderStackAsalto)
            {
                // Objetivo: cuartel enemigo con menor defensa (guarnición pesimista + refuerzo + bono), ponderado por distancia.
                objetivo = k.CuartelesEnemigos
                    .Select(q =>
                    {
                        string dueno = k.CuartelOwner.GetValueOrDefault(q, "");
                        int guarn = k.EnemigosPorCelda.TryGetValue(q, out var gc) ? ReglasEntrada.PoderPesimista(k, gc) : 0;
                        var (rf, rd) = ReglasEntrada.RefuerzoCuartel(k, dueno, ReglasEntrada.RefuerzoMaxEntrada);
                        int bonoQ = AccionesTacticas.DefensaCuartelActual(ctx.Estado, q, ctx.Turno);
                        double dist = libres.Average(u => ReglasEntrada.Manhattan(u.coord, q));
                        return (q, score: guarn + rf + rd + bonoQ + 6 * dist);
                    })
                    .OrderBy(x => x.score).First().q;
            }
        }

        // Disparo lejano: al cuartel objetivo con guarnición cara, o a un stack aparcado caro.
        bool disparado = false;
        var disparoId = mano.Where(id => ctx.CatalogoMano.TryGetValue(id, out var b) && EsDisparoLejano(b))
            .OrderBy(id => AccionesTacticas.Coste(Cat(id))).FirstOrDefault();
        if (_p.DisparaAlCuartel && disparoId != null && miCuartel != "" && AccionesTacticas.Coste(Cat(disparoId)) <= energia)
        {
            string? blanco = null;
            if (objetivo != null && k.EnemigosPorCelda.TryGetValue(objetivo, out var cObj)
                && cObj.Sum(ReglasEntrada.Coste) >= _p.UmbralCosteDisparo)
                blanco = objetivo;
            if (blanco == null)
                blanco = k.EnemigosPorCelda
                    .Where(kv => !k.CuartelesEnemigos.Contains(kv.Key)
                                 && kv.Value.Sum(ReglasEntrada.Coste) >= _p.UmbralCosteDisparoStack
                                 && AccionesTacticas.ObjetivoPredecible(kv.Key, kv.Value, k.CuartelesEnemigos))
                    .OrderByDescending(kv => kv.Value.Sum(ReglasEntrada.Coste))
                    .Select(kv => kv.Key).FirstOrDefault();
            if (blanco != null)
            {
                var b = Cat(disparoId);
                int coste = AccionesTacticas.Coste(b);
                acciones.Add(AccionesTacticas.CrearAccion(M.Int(M.Get(b, "IdHabilidad", "idHabilidad")), uid, zona, miCuartel,
                    new List<string> { blanco }, ctx.Turno, coste, disparoId));
                energiaAcciones += coste; mano.Remove(disparoId); disparado = true;
                if (blanco == objetivo) objetivo = null;   // este turno no se entra: el misil barre la celda
            }
        }

        // ── 6. MOVIMIENTO ──
        var ocupacion = new Dictionary<string, (int f, int d)>();
        void Ocupa(string c, Dictionary<string, object?> card)
        {
            var s = ocupacion.TryGetValue(c, out var v) ? v : (0, 0);
            ocupacion[c] = (s.Item1 + Fuerza(card), s.Item2 + Defensa(card));
        }
        foreach (var u in inmoviles) { destino[u.card] = u.coord; Ocupa(u.coord, u.card); }
        foreach (var u in moviles.Where(u => asignadas.Contains(u.card))) Ocupa(destino[u.card], u.card);

        if (objetivo != null)
        {
            // El stack de asalto = las unidades libres de poder ≥ 15 (las baratas siguen farmeando).
            var asalto = libres.Where(u => Fuerza(u.card) + Defensa(u.card) >= 15).ToList();
            var farmers = libres.Where(u => !asalto.Any(a => ReferenceEquals(a.card, u.card))).ToList();
            var dest = ReglasEntrada.AvanceCohesionado(k, asalto, objetivo, ocupacion);
            for (int i = 0; i < asalto.Count; i++) { destino[asalto[i].card] = dest[i]; Ocupa(dest[i], asalto[i].card); }
            libres = farmers;
            UltimoModo = disparado ? "humano:asedio+disparo" : "humano:asedio";
        }
        else UltimoModo = disparado ? "humano:farmeo+disparo" : "humano:farmeo";

        // Caza oportunista y farmeo para el resto, de más a menos poder.
        (int f, int d) Extra(string c) => ocupacion.TryGetValue(c, out var s) ? s : (0, 0);
        foreach (var u in libres.OrderByDescending(u => Fuerza(u.card) + Defensa(u.card)))
        {
            int f = Fuerza(u.card), d = Defensa(u.card);
            var alcance = k.Alcance(u.coord, Mov(u.card), ReglasEntrada.Tipo(u.card));
            // a) presa batible al alcance (cuartel vacío conquistable o stack que gano con lo que ya va allí)
            string? presa = null; int mejorValor = 0;
            foreach (var c in alcance)
            {
                bool esCuartel = k.CuartelesEnemigos.Contains(c);
                bool hayEnemigo = k.EnemigosPorCelda.ContainsKey(c);
                if (!esCuartel && !hayEnemigo) continue;
                var (ef, ed) = Extra(c);
                var v = ReglasEntrada.Evaluar(k, c, f + ef, d + ed, contarRefuerzo: esCuartel, 0);
                if (v != ReglasEntrada.Veredicto.Gana) continue;
                int valor = (esCuartel ? 1000 : 0) + (hayEnemigo ? k.EnemigosPorCelda[c].Sum(ReglasEntrada.Coste) : 0);
                if (valor > mejorValor) { mejorValor = valor; presa = c; }
            }
            if (presa != null) { destino[u.card] = presa; Ocupa(presa, u.card); continue; }

            // b) mejor celda de energía alcanzable (incluida la actual), sin exponerse
            string mejor = u.coord; int mejorFarm = ReglasEntrada.ValorFarmeo(k, u.coord);
            bool actualSegura = !ReglasEntrada.Expuesta(k, u.coord, f + d + Extra(u.coord).f + Extra(u.coord).d);
            if (!actualSegura) mejorFarm = -1;
            foreach (var c in alcance)
            {
                int vf = ReglasEntrada.ValorFarmeo(k, c);
                if (!_p.FarmeaContinenteEnemigo && !k.Rayos.Contains(c) && !k.IslaCentral.Contains(c)) vf = 0;
                if (vf <= mejorFarm) continue;
                var (ef, ed) = Extra(c);
                if (!ReglasEntrada.EntradaPermitida(k, c, f + ef, d + ed)) continue;
                if (ReglasEntrada.Expuesta(k, c, f + d + ef + ed)) continue;
                mejor = c; mejorFarm = vf;
            }
            if (mejorFarm <= 0)
            {
                // c) sin energía a tiro: acercarse a la celda de energía más cercana
                string? objEnergia = k.Rayos.Concat(k.IslaCentral)
                    .OrderBy(c => ReglasEntrada.Manhattan(u.coord, c)).FirstOrDefault();
                mejor = ReglasEntrada.PasoSeguro(k, u.coord, u.card, objEnergia, Extra);
            }
            destino[u.card] = mejor; Ocupa(mejor, u.card);
        }

        // ── 7. EVOLUCIONES: unidades que se quedan quietas, mejor ganancia por energía ──
        var celdas = new Tablero();
        void Add(string c, Dictionary<string, object?> card)
        {
            if (!celdas.TryGetValue(c, out var l)) { l = new(); celdas[c] = l; }
            l.Add(card);
        }
        int evos = 0;
        var todas = propias.Select(u => (coord: u.coord, card: u.card, nueva: false))
            .Concat(nuevas.Select(n => (coord: miCuartel, card: n, nueva: true))).ToList();
        foreach (var (coord, card, nueva) in todas.OrderByDescending(x => GananciaEvo(ctx, x.card)))
        {
            string dst = destino.TryGetValue(card, out var dd) ? dd : coord;
            var colocar = card;
            if (_p.Evoluciona && !nueva && evos < _p.MaxEvolucionesPorTurno && dst == coord)
            {
                var idEvo = M.Str(M.Get(card, "IdEvolucion", "idEvolucion"));
                int costeEvo = M.Int(M.Get(card, "Evolucion", "evolucion"));
                if (idEvo != "" && costeEvo > 0 && energia - costeEvo >= _p.Reserva
                    && ctx.Evoluciones.TryGetValue(idEvo, out var evo)
                    && ReglasEntrada.CanLand(coord, ReglasEntrada.Tipo(evo), ctx.Terreno)
                    && Fuerza(evo) + Defensa(evo) > Fuerza(card) + Defensa(card))
                {
                    var z = M.Str(M.Get(card, "ownerZone")); if (z == "") z = zona;
                    colocar = Nueva(evo, idEvo, uid, z);
                    energia -= costeEvo; gastado += costeEvo; evos++;
                }
            }
            Add(dst, colocar);
        }

        return new BotMove
        {
            Celdas = celdas,
            Acciones = acciones,
            ManoResultante = mano,
            EnergiaGastada = gastado,
            EnergiaAcciones = energiaAcciones,
            EspecialComprada = especial,
        };
    }

    private static double GananciaEvo(BotContext ctx, Dictionary<string, object?> card)
    {
        var idEvo = M.Str(M.Get(card, "IdEvolucion", "idEvolucion"));
        int coste = M.Int(M.Get(card, "Evolucion", "evolucion"));
        if (idEvo == "" || coste <= 0 || !ctx.Evoluciones.TryGetValue(idEvo, out var evo)) return -1;
        return (Fuerza(evo) + Defensa(evo) - Fuerza(card) - Defensa(card)) / (double)coste;
    }

    private static Dictionary<string, object?> Nueva(Dictionary<string, object?> baseCard, string id, string uid, string zona)
        => new(baseCard)
        {
            ["id"] = id,
            ["ownerUid"] = uid,
            ["ownerZone"] = zona,
            ["instanceId"] = Guid.NewGuid().ToString("N"),
        };

    private static int Fuerza(Dictionary<string, object?> c) => ReglasEntrada.Fuerza(c);
    private static int Defensa(Dictionary<string, object?> c) => ReglasEntrada.Defensa(c);
    private static int Mov(Dictionary<string, object?> c)
    {
        if (CartaHelper.EstaParalizada(c) || CartaHelper.EstaConfundida(c)) return 0;
        return ReglasEntrada.Mov(c);
    }
}
