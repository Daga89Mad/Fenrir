// ─────────────────────────────────────────────────────────────────────────────
// DuelosBot.cs
//
// BOT HEURÍSTICO del modo Duelos (DuelosMotor.cs). Juega con información
// oculta: no sabe cuál es el rey rival ni distingue los clones (DuelosVista).
//
// Cómo decide (un turno):
//   1. Saca varias VISTAS de la partida, cada una con una suposición distinta
//      del rey rival, y en cada una genera una jugada probable del RIVAL.
//   2. Genera N jugadas propias con estilos distintos (defensivo, ofensivo,
//      caza, azar): movimientos, habilidades, disparos y habilidad de ejército
//      según reglas heurísticas.
//   3. Resuelve cada jugada propia contra cada jugada rival con el MOTOR REAL
//      y puntúa el resultado (Evaluar). Se queda con la mejor media.
// Así las reglas del bot no tienen que prever cada combinación: el motor dice
// qué pasa y la evaluación decide.
// ─────────────────────────────────────────────────────────────────────────────

public sealed class DuelosBot
{
    public int PlanesPropios = 14;
    public int PlanesRival = 5;

    // ── Pesos de la evaluación (perfil del bot) ──────────────────────────────
    /// Castigo por cada punto de ataque rival que puede caer sobre el rey.
    public double PesoAmenazaRey = 8;
    /// Premio por cada punto de ventaja que se puede reunir sobre un general rival.
    public double PesoPresion = 4;
    /// Castigo por cada casilla de distancia al general rival más cercano.
    public double PesoDistancia = 3;

    private enum Estilo { Defensivo, Ofensivo, Caza, Azar }

    // ═════════════════════════════════════════════════════════════════════════
    // DESPLIEGUE
    // ═════════════════════════════════════════════════════════════════════════

    /// Despliegue del bot: el rey en la fila trasera, con o sin escolta.
    /// [bloqueadas]: casillas intransitables del mapa (null = sin bloqueos en
    /// las filas de despliegue, como con los pilares por defecto).
    public static int[] Desplegar(int jugador, Random rng, bool[]? bloqueadas = null)
    {
        int filaTrasera = jugador == 0 ? 0 : DuelosAjustes.Filas - 1;
        int filaDelantera = jugador == 0 ? 1 : DuelosAjustes.Filas - 2;
        var cuenta = new int[DuelosAjustes.Celdas];
        var pos = new int[6];
        bool Libre(int c) => (bloqueadas == null || !bloqueadas[c]) && cuenta[c] < DuelosAjustes.TopeCartasCasilla;
        int Poner(int c) { cuenta[c]++; return c; }
        List<int> LibresEn(int fila) => Enumerable.Range(0, DuelosAjustes.Columnas)
            .Select(k => DuelosTablero.Idx(fila, k)).Where(Libre).ToList();
        int EnFila(int fila)
        {
            var l = LibresEn(fila);
            if (l.Count == 0) l = LibresEn(fila == filaTrasera ? filaDelantera : filaTrasera);
            if (l.Count == 0) throw new InvalidOperationException("no queda sitio para desplegar");
            return l[rng.Next(l.Count)];
        }
        int FilaAlAzar() => rng.Next(2) == 0 ? filaTrasera : filaDelantera;

        // Orden de las cartas: general 1, general 2, básica, básica, media, alta.
        // Aquí el rey es siempre el general 1; ConRey lo recoloca según el
        // índice de rey de la configuración.
        int reyCelda = Poner(EnFila(filaTrasera));
        pos[0] = reyCelda;
        bool escoltado = rng.NextDouble() < 0.6;
        pos[5] = Poner(escoltado && Libre(reyCelda) ? reyCelda : EnFila(filaDelantera)); // la alta escolta al rey
        pos[1] = Poner(EnFila(filaDelantera));      // el otro general, delante
        pos[4] = Poner(Libre(pos[1]) && rng.NextDouble() < 0.5 ? pos[1] : EnFila(filaDelantera));
        pos[2] = Poner(escoltado && Libre(reyCelda) && rng.NextDouble() < 0.5 ? reyCelda : EnFila(FilaAlAzar()));
        pos[3] = Poner(EnFila(FilaAlAzar()));
        return pos;
    }

    /// Reordena un despliegue de Desplegar (donde el rey va en la posición 0)
    /// para que el rey quede en el índice de general [reyIndice].
    public static int[] ConRey(int[] pos, int reyIndice)
    {
        var r = (int[])pos.Clone();
        if (reyIndice == 1) (r[0], r[1]) = (r[1], r[0]);
        return r;
    }

    // ═════════════════════════════════════════════════════════════════════════
    // DECISIÓN
    // ═════════════════════════════════════════════════════════════════════════

    public DuelosJugada Decidir(DuelosEstado real, int yo, Random rng)
    {
        if (real.Terminada) return DuelosJugada.Vacia();
        int rival = 1 - yo;

        var vistas = new DuelosEstado[PlanesRival];
        var rivales = new DuelosJugada[PlanesRival];
        for (int k = 0; k < PlanesRival; k++)
        {
            vistas[k] = DuelosVista.Para(real, yo, rng);
            rivales[k] = GenerarPlan(vistas[k], rival, rng, EstiloAlAzar(rng));
        }

        var candidatos = new List<DuelosJugada> { GenerarPlan(vistas[0], yo, rng, Estilo.Defensivo) };
        for (int i = 1; i < PlanesPropios; i++)
            candidatos.Add(GenerarPlan(vistas[0], yo, rng, EstiloAlAzar(rng)));

        DuelosJugada mejor = candidatos[0];
        double mejorPunt = double.NegativeInfinity;
        foreach (var plan in candidatos)
        {
            double s = 0;
            for (int k = 0; k < PlanesRival; k++)
            {
                var sim = vistas[k].Clonar();
                sim.RegistrarEventos = false;
                if (yo == 0) DuelosMotor.Resolver(sim, plan, rivales[k], rng);
                else DuelosMotor.Resolver(sim, rivales[k], plan, rng);
                s += Evaluar(sim, yo);
            }
            s /= PlanesRival;
            s += rng.NextDouble() * 2; // desempate con algo de variedad
            if (s > mejorPunt) { mejorPunt = s; mejor = plan; }
        }
        return mejor;
    }

    private static Estilo EstiloAlAzar(Random rng)
    {
        double r = rng.NextDouble();
        return r < 0.30 ? Estilo.Defensivo : r < 0.60 ? Estilo.Ofensivo : r < 0.88 ? Estilo.Caza : Estilo.Azar;
    }

    // ═════════════════════════════════════════════════════════════════════════
    // EVALUACIÓN (desde el punto de vista de [yo], con el rey rival supuesto)
    // ═════════════════════════════════════════════════════════════════════════

    private double Evaluar(DuelosEstado s, int yo)
    {
        if (s.Terminada)
            return s.Ganador == yo ? 100000 : s.Ganador == 2 ? -2000 : -100000;
        int rival = 1 - yo;
        double p = 0;
        var rey = s.Rey(yo);
        int genRivales = s.Cartas.Count(c => c.Viva && c.Dueno == rival && c.EsGeneral && !c.EsClon);

        foreach (var c in s.Cartas)
        {
            if (!c.Viva || c.EsClon) continue;
            double v;
            if (c.EsGeneral)
                v = c.Dueno == yo
                    ? c.Vidas * (rey != null && c.Id == rey.Id ? 500 : 150)
                    : c.Vidas * (genRivales <= 1 ? 500 : 260);
            else v = DuelosCatalogo.Ataque(c.Def.Papel) * 5;
            p += c.Dueno == yo ? v : -v;
            if (c.Dueno == yo && c.EsGeneral && DuelosMotor.HabilidadLista(s, c)) p += 6;
        }
        p += (s.Jugadores[yo].DisparosRestantes - s.Jugadores[rival].DisparosRestantes) * 30;
        p += (s.Jugadores[yo].HabilidadEjercitoUsada ? 0 : 40) - (s.Jugadores[rival].HabilidadEjercitoUsada ? 0 : 40);

        // Seguridad del rey.
        if (rey != null && rey.Viva)
        {
            int amenaza = AtaqueAlAlcance(s, rey.Pos, rival);
            int propio = s.Cartas.Where(c => c.Viva && !c.EsClon && c.Dueno == yo && c.Pos == rey.Pos)
                .Sum(c => DuelosMotor.AtaqueEfectivo(s, c));
            if (amenaza > propio) p -= (amenaza - propio) * PesoAmenazaRey;
            if (!rey.SeMovio && s.Jugadores[rival].DisparosRestantes > 0 && s.Turno >= DuelosAjustes.DisparoLejanoPrimerTurno)
                p -= 40;
            if (s.SeHunde(rey.Pos)) p -= 60;
            int salidas = DuelosTablero.Vecinas[rey.Pos].Count(v => DuelosMotor.PuedeTerminar(s, v, yo));
            if (salidas < 2) p -= 25;
        }

        // Presión sobre los generales rivales.
        var mios = s.Cartas.Where(c => c.Viva && !c.EsClon && c.Dueno == yo && (rey == null || c.Id != rey.Id)).ToList();
        foreach (var g in s.Cartas.Where(c => c.Viva && c.Dueno == rival && c.EsGeneral))
        {
            double peso = genRivales <= 1 ? 1.0 : 0.6;
            int mio = AtaqueAlAlcance(s, g.Pos, yo);
            int suyo = s.Cartas.Where(c => c.Viva && !c.EsClon && c.Dueno == rival && c.Pos == g.Pos)
                .Sum(c => DuelosMotor.AtaqueEfectivo(s, c));
            if (mio > suyo) p += (mio - suyo) * PesoPresion * peso;
            if (mios.Count > 0) p -= mios.Min(c => DuelosTablero.Distancia(c.Pos, g.Pos)) * PesoDistancia * peso;
        }
        return p;
    }

    /// Ataque máximo (las 3 mejores cartas) que [jugador] puede reunir en
    /// [celda] el turno que viene (distancia ortogonal, sin obstáculos).
    private static int AtaqueAlAlcance(DuelosEstado s, int celda, int jugador)
    {
        int a1 = 0, a2 = 0, a3 = 0;
        foreach (var c in s.Cartas)
        {
            if (!c.Viva || c.EsClon || c.Dueno != jugador) continue;
            if (c.ParalizadaTurno == s.Turno && c.Pos != celda) continue;
            if (DuelosTablero.Distancia(c.Pos, celda) > DuelosMotor.Movimiento(s, c)) continue;
            int a = DuelosMotor.AtaqueEfectivo(s, c);
            if (a > a1) { a3 = a2; a2 = a1; a1 = a; }
            else if (a > a2) { a3 = a2; a2 = a; }
            else if (a > a3) a3 = a;
        }
        return a1 + a2 + a3;
    }

    // ═════════════════════════════════════════════════════════════════════════
    // GENERACIÓN DE JUGADAS
    // ═════════════════════════════════════════════════════════════════════════

    private static DuelosJugada GenerarPlan(DuelosEstado v0, int p, Random rng, Estilo est)
    {
        var j = new DuelosJugada();
        int rival = 1 - p;
        // Copia de trabajo: aquí se aplican los efectos de movimiento que el
        // plan decide usar (más movimiento, movilización) para calcular el
        // alcance real.
        var v = v0.Clonar();
        v.RegistrarEventos = false;
        bool agresivo = est is Estilo.Ofensivo or Estilo.Caza;

        var mias = v.Cartas.Where(c => c.Viva && c.Dueno == p).ToList();
        var enem = v.Cartas.Where(c => c.Viva && c.Dueno == rival).ToList();
        var enemGen = enem.Where(c => c.EsGeneral).ToList();
        var rey = v.Rey(p);
        if (rey != null && !rey.Viva) rey = null;

        // Amenaza enemiga por casilla.
        var amenaza = new int[DuelosAjustes.Celdas];
        for (int c = 0; c < DuelosAjustes.Celdas; c++) amenaza[c] = AtaqueAlAlcance(v, c, rival);

        // ── Habilidad de ejército ────────────────────────────────────────────
        if (DuelosMotor.HabilidadEjercitoLista(v, p))
        {
            var jp = v.Jugadores[p];
            switch (jp.HabilidadEjercito)
            {
                case DuelosHabilidadEjercito.Movilizacion:
                    if (agresivo && rng.NextDouble() < 0.25)
                    {
                        j.UsarHabilidadEjercito = true;
                        jp.MovilizacionHasta = v.Turno + DuelosAjustes.MovilizacionTurnos - 1;
                    }
                    break;
                case DuelosHabilidadEjercito.FuriaInfernal:
                    if (agresivo && rng.NextDouble() < 0.3) j.UsarHabilidadEjercito = true;
                    break;
                case DuelosHabilidadEjercito.Fortaleza:
                    if (rey != null && amenaza[rey.Pos] > 0 && rng.NextDouble() < 0.35) j.UsarHabilidadEjercito = true;
                    break;
                case DuelosHabilidadEjercito.Reparacion:
                {
                    var herido = mias.Where(c => c.EsGeneral && !c.EsClon && c.Vidas < DuelosAjustes.VidasGeneral)
                        .OrderByDescending(c => rey != null && c.Id == rey.Id).FirstOrDefault();
                    if (herido != null && rng.NextDouble() < 0.8)
                    {
                        j.UsarHabilidadEjercito = true;
                        j.HabilidadEjercitoCartaId = herido.Id;
                    }
                    break;
                }
            }
        }

        // ── Habilidades previas que cambian el movimiento ────────────────────
        foreach (var g in mias.Where(g => g.Def.Habilidad == DuelosHabilidad.MasMovimiento && DuelosMotor.HabilidadLista(v, g)))
        {
            int juntos = mias.Count(c => c.Pos == g.Pos);
            if (agresivo && juntos >= 2 && rng.NextDouble() < 0.6)
            {
                j.Habilidades.Add(new DuelosUsoHabilidad { GeneralId = g.Id });
                foreach (var c in mias.Where(c => c.Pos == g.Pos))
                    c.MasMovHasta = v.Turno + DuelosAjustes.MasMovimientoTurnos - 1;
            }
        }

        // ── Movimientos ──────────────────────────────────────────────────────
        var cuenta = new int[DuelosAjustes.Celdas];
        var destino = new Dictionary<int, int>();
        double Ruido() => (rng.NextDouble() - 0.5) * 6;

        List<int> Alc(DuelosCarta c)
        {
            var a = DuelosMotor.Alcanzables(v, c);
            var l = new List<int>();
            for (int i = 0; i < DuelosAjustes.Celdas; i++) if (a[i]) l.Add(i);
            return l;
        }

        int Elegir(DuelosCarta c, Func<int, double> puntuar)
        {
            int mejor = c.Pos;
            double mp = double.NegativeInfinity;
            foreach (var d in Alc(c))
            {
                if (!c.EsClon && cuenta[d] >= DuelosAjustes.TopeCartasCasilla) continue;
                double s = puntuar(d) + Ruido();
                if (v.SeHunde(d)) s -= 300;
                if (s > mp) { mp = s; mejor = d; }
            }
            return mejor;
        }

        void Asignar(DuelosCarta c, int d)
        {
            destino[c.Id] = d;
            if (!c.EsClon) cuenta[d]++;
        }

        // Objetivo de ataque: el general rival más débil (o uno al azar).
        DuelosCarta? objetivo = null;
        if (enemGen.Count > 0)
            objetivo = est == Estilo.Caza
                ? enemGen.OrderBy(g => enem.Where(e => e.Pos == g.Pos).Sum(e => DuelosMotor.AtaqueEfectivo(v, e))).First()
                : enemGen[rng.Next(enemGen.Count)];

        int NearestEnemyGen(int c) =>
            enemGen.Count == 0 ? 0 : enemGen.Min(g => DuelosTablero.Distancia(c, g.Pos));

        // 1) Rey.
        int reyDest = -1;
        if (rey != null)
        {
            var amigos = mias.Where(c => c != rey && !c.EsClon).ToList();
            reyDest = est == Estilo.Azar
                ? Alc(rey)[rng.Next(Alc(rey).Count)]
                : Elegir(rey, d =>
                    -amenaza[d] * 2.5
                    + amigos.Count(a => DuelosTablero.Distancia(a.Pos, d) <= 2) * 3
                    + Math.Min(NearestEnemyGen(d), 5) * 1.5
                    - (DuelosTablero.Anillo(d) == 3 && v.Turno > 15 ? 6 : 0)
                    + (d != rey.Pos ? 4 : 0)); // moverse dificulta el disparo lejano
            Asignar(rey, reyDest);
        }

        // 2) Resto, de más fuerte a más débil.
        var resto = mias.Where(c => c != rey)
            .OrderByDescending(c => c.EsClon ? 0 : DuelosCatalogo.Ataque(c.Def.Papel)).ToList();
        int escoltas = est switch { Estilo.Defensivo => 2, Estilo.Caza => 1, Estilo.Ofensivo => 0, _ => rng.Next(0, 3) };
        int i0 = 0;
        foreach (var c in resto)
        {
            if (c.ParalizadaTurno == v.Turno) { Asignar(c, c.Pos); continue; }
            if (est == Estilo.Azar)
            {
                var l = Alc(c).Where(d => c.EsClon || cuenta[d] < DuelosAjustes.TopeCartasCasilla).ToList();
                Asignar(c, l.Count > 0 ? l[rng.Next(l.Count)] : c.Pos);
                continue;
            }
            bool escolta = reyDest >= 0 && !c.EsGeneral && i0 >= resto.Count - escoltas;
            i0++;
            int d;
            if (escolta)
            {
                d = Elegir(c, x => -DuelosTablero.Distancia(x, reyDest) * 6 + (x == reyDest ? 8 : 0));
            }
            else if (objetivo != null)
            {
                int ataqueC = c.EsClon ? 0 : DuelosMotor.AtaqueEfectivo(v, c);
                d = Elegir(c, x =>
                {
                    int dist = DuelosTablero.Distancia(x, objetivo.Pos);
                    double s = -dist * 5;
                    // Apilarse con otras cartas propias que van al mismo sitio.
                    s += cuenta[x] * 3;
                    // No meterse solo donde la amenaza le supera claramente.
                    if (!agresivo && amenaza[x] > ataqueC * (1 + cuenta[x])) s -= 6;
                    return s;
                });
            }
            else d = Elegir(c, _ => 0);
            Asignar(c, d);
        }

        // ── Habilidades de general ───────────────────────────────────────────
        int ValorEnemigo(int celda)
        {
            int s = 0;
            foreach (var e in enem)
                if (e.Pos == celda) s += e.EsGeneral ? 40 * Math.Max(1, e.Vidas) : DuelosCatalogo.Ataque(e.Def.Papel) * 2;
            return s;
        }
        bool HayPropias(int celda) => mias.Any(c => !c.EsClon && (c.Pos == celda || destino.GetValueOrDefault(c.Id, c.Pos) == celda));

        foreach (var g in mias.Where(g => g.EsGeneral && !g.EsClon && DuelosMotor.HabilidadLista(v, g)))
        {
            var h = g.Def.Habilidad;
            if (h == DuelosHabilidad.MasMovimiento) continue; // ya decidida
            var uso = new DuelosUsoHabilidad { GeneralId = g.Id };
            bool usar = false;
            switch (h)
            {
                case DuelosHabilidad.DisparoCercano:
                case DuelosHabilidad.Confusion:
                {
                    var mejor = DuelosTablero.Vecinas[g.Pos].Where(c => !HayPropias(c) || h == DuelosHabilidad.Confusion)
                        .OrderByDescending(ValorEnemigo).FirstOrDefault(-1);
                    if (mejor >= 0 && ValorEnemigo(mejor) > 0 && rng.NextDouble() < 0.75)
                    { uso.Celdas = new[] { mejor }; usar = true; }
                    break;
                }
                case DuelosHabilidad.Paralisis:
                case DuelosHabilidad.Veneno:
                {
                    var mejor = enem.Select(e => e.Pos).Distinct()
                        .Where(c => DuelosTablero.Distancia(g.Pos, c) <= DuelosAjustes.AlcanceMedio && !HayPropias(c))
                        .OrderByDescending(ValorEnemigo).FirstOrDefault(-1);
                    if (mejor >= 0 && rng.NextDouble() < 0.65) { uso.Celdas = new[] { mejor }; usar = true; }
                    break;
                }
                case DuelosHabilidad.Escudo:
                {
                    if (reyDest >= 0 && DuelosTablero.Distancia(g.Pos, reyDest) <= 1
                        && rng.NextDouble() < (amenaza[reyDest] > 0 ? 0.8 : 0.1))
                    { uso.Celdas = new[] { reyDest }; usar = true; }
                    break;
                }
                case DuelosHabilidad.MasAtaque:
                    if (agresivo && rng.NextDouble() < 0.5) usar = true;
                    break;
                case DuelosHabilidad.Supervivencia:
                {
                    var objetivoS = rey != null && DuelosTablero.Distancia(g.Pos, rey.Pos) <= 1
                        ? rey
                        : mias.Where(c => !c.EsClon && DuelosTablero.Distancia(g.Pos, c.Pos) <= 1)
                            .OrderByDescending(c => c.EsGeneral ? 100 : DuelosCatalogo.Ataque(c.Def.Papel)).FirstOrDefault();
                    if (objetivoS != null && rng.NextDouble() < 0.6)
                    { uso.CartaId = objetivoS.Id; uso.Celdas = new[] { objetivoS.Pos }; usar = true; }
                    break;
                }
                case DuelosHabilidad.Clon:
                {
                    var copia = rey ?? g;
                    var celda = DuelosTablero.Vecinas[g.Pos]
                        .Where(c => DuelosMotor.PuedeTerminar(v, c, p) && !enem.Any(e => e.Pos == c))
                        .OrderBy(_ => rng.Next()).FirstOrDefault(-1);
                    if (celda >= 0 && rng.NextDouble() < 0.55)
                    { uso.CartaId = copia.Id; uso.Celdas = new[] { celda }; usar = true; }
                    break;
                }
                case DuelosHabilidad.Teletransporte:
                {
                    // a) El rey huye si está amenazado.
                    if (rey != null && amenaza[rey.Pos] > 0 && rey.ParalizadaTurno != v.Turno && rng.NextDouble() < 0.5)
                    {
                        int mejor = Enumerable.Range(0, DuelosAjustes.Celdas)
                            .Where(c => c != rey.Pos && DuelosTablero.Distancia(rey.Pos, c) <= DuelosAjustes.TeletransporteDistancia
                                        && DuelosMotor.PuedeTerminar(v, c, p) && !v.SeHunde(c) && cuenta[c] < DuelosAjustes.TopeCartasCasilla)
                            .OrderBy(c => amenaza[c] + rng.NextDouble()).FirstOrDefault(-1);
                        if (mejor >= 0)
                        {
                            uso.CartaId = rey.Id; uso.Celdas = new[] { mejor }; usar = true;
                            cuenta[destino[rey.Id]]--; destino[rey.Id] = mejor; cuenta[mejor]++;
                        }
                    }
                    // b) Una carta fuerte cae sobre un grupo rival que puede ganar.
                    if (!usar && rng.NextDouble() < 0.6)
                    {
                        foreach (var c in mias.Where(c => !c.EsClon && (rey == null || c.Id != rey.Id) && c.ParalizadaTurno != v.Turno)
                                     .OrderByDescending(c => DuelosMotor.AtaqueEfectivo(v, c)))
                        {
                            int a = DuelosMotor.AtaqueEfectivo(v, c);
                            int presa = enem.Select(e => e.Pos).Distinct()
                                .Where(x => DuelosTablero.Distancia(c.Pos, x) <= DuelosAjustes.TeletransporteDistancia
                                            && DuelosMotor.PuedeTerminar(v, x, p)
                                            && enem.Where(e => e.Pos == x).Sum(e => DuelosMotor.AtaqueEfectivo(v, e)) < a)
                                .OrderByDescending(ValorEnemigo).FirstOrDefault(-1);
                            if (presa < 0) continue;
                            uso.CartaId = c.Id; uso.Celdas = new[] { presa }; usar = true;
                            if (destino.TryGetValue(c.Id, out var dAnt)) cuenta[dAnt]--;
                            destino[c.Id] = presa; cuenta[presa]++;
                            break;
                        }
                    }
                    break;
                }
                case DuelosHabilidad.Muro:
                {
                    if (reyDest < 0 || amenaza[reyDest] == 0 || rng.NextDouble() > 0.5) break;
                    var cerca = enem.OrderBy(e => DuelosTablero.Distancia(e.Pos, reyDest)).First().Pos;
                    bool Libre(int c) => !v.Pilar[c] && !v.Hundida[c] && !v.MuroActivo(c) && v.EscudoHasta[c] < v.Turno
                                         && !v.Cartas.Any(x => x.Viva && x.Pos == c) && !destino.ContainsValue(c);
                    int c0 = Enumerable.Range(0, DuelosAjustes.Celdas)
                        .Where(c => Libre(c) && DuelosTablero.Distancia(g.Pos, c) <= DuelosAjustes.AlcanceMedio)
                        .OrderBy(c => DuelosTablero.Distancia(c, reyDest) + DuelosTablero.Distancia(c, cerca) + rng.NextDouble())
                        .FirstOrDefault(-1);
                    if (c0 < 0) break;
                    var muro = new List<int> { c0 };
                    while (muro.Count < DuelosAjustes.MuroCeldas)
                    {
                        int sig = muro.SelectMany(m => DuelosTablero.Vecinas[m]).Distinct()
                            .Where(c => !muro.Contains(c) && Libre(c))
                            .OrderBy(c => DuelosTablero.Distancia(c, cerca) + rng.NextDouble()).FirstOrDefault(-1);
                        if (sig < 0) break;
                        muro.Add(sig);
                    }
                    if (muro.Count == DuelosAjustes.MuroCeldas) { uso.Celdas = muro.ToArray(); usar = true; }
                    break;
                }
            }
            if (usar) j.Habilidades.Add(uso);
        }

        // ── Disparo lejano ───────────────────────────────────────────────────
        if (DuelosMotor.PuedeDispararLejos(v, p))
        {
            int mejor = -1;
            double mv = 0;
            foreach (var celda in enem.Select(e => e.Pos).Distinct())
            {
                if (HayPropias(celda)) continue;
                bool paralizados = enem.Where(e => e.Pos == celda).All(e => e.ParalizadaTurno == v.Turno);
                bool quietos = enem.Where(e => e.Pos == celda).All(e => !e.SeMovio);
                double certeza = paralizados ? 1.0 : quietos ? 0.5 : 0.2;
                double val = ValorEnemigo(celda) * certeza;
                if (val > mv) { mv = val; mejor = celda; }
            }
            if (mejor >= 0 && (mv >= 30 ? rng.NextDouble() < 0.7 : rng.NextDouble() < 0.1))
                j.Disparos.Add(mejor);
        }

        foreach (var (id, d) in destino) j.Movimientos[id] = d;
        return j;
    }
}
