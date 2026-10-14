// ─────────────────────────────────────────────────────────────────────────────
// DuelosMotor.cs
//
// MODO DUELOS — creación de la partida, validación y RESOLUCIÓN del turno.
// Lógica pura (sin Firestore). Tipos y ajustes en DuelosReglas.cs.
//
// Orden de resolución (los dos jugadores declaran a la vez):
//   1. Habilidades de ejército (Movilización, Reparación, Furia, Fortaleza).
//   2. Habilidades previas al movimiento: escudo, muro, más movimiento,
//      teletransporte, clon y supervivencia. Se validan desde la posición del
//      general AL EMPEZAR el turno.
//   3. Movimiento. La carta vuelve a su casilla si el destino no es
//      alcanzable, si es pilar, hundida, muro o escudo rival, si la Fortaleza
//      rival lo impide o si supera el tope de 3 cartas propias.
//   4. Disparos (cercanos y lejanos, simultáneos) y habilidades sobre casillas
//      tras el movimiento: veneno, parálisis, confusión y más ataque.
//   5. Los clones que tocan a un enemigo desaparecen.
//   6. Combate en cada casilla con cartas de los dos jugadores.
//   7. Hundimiento de la casilla anunciada.
//   8. Fin de partida (rey muerto o límite de turnos) y anuncio del siguiente
//      hundimiento.
// ─────────────────────────────────────────────────────────────────────────────

public static class DuelosMotor
{
    // ═════════════════════════════════════════════════════════════════════════
    // CREACIÓN
    // ═════════════════════════════════════════════════════════════════════════

    /// Crea la partida. Lanza ArgumentException si la configuración no es válida.
    /// [bloqueadas]: casillas intransitables del mapa elegido (49, por índice);
    /// si es null se usan los pilares por defecto (DuelosAjustes.Pilares).
    public static DuelosEstado Crear(DuelosConfigJugador j0, DuelosConfigJugador j1, Random rng,
                                     bool[]? bloqueadas = null)
    {
        var e = new DuelosEstado();
        if (bloqueadas != null)
        {
            if (bloqueadas.Length != DuelosAjustes.Celdas)
                throw new ArgumentException($"el mapa debe tener {DuelosAjustes.Celdas} casillas");
            e.Pilar = (bool[])bloqueadas.Clone();
        }
        else
        {
            foreach (var p in DuelosAjustes.Pilares) e.Pilar[DuelosTablero.Parse(p)] = true;
        }
        AnadirJugador(e, 0, j0, rng);
        AnadirJugador(e, 1, j1, rng);
        return e;
    }

    private static void AnadirJugador(DuelosEstado e, int j, DuelosConfigJugador cfg, Random rng)
    {
        var defEj = DuelosCatalogo.Ejercitos[cfg.Ejercito];
        if (cfg.Generales == null || cfg.Generales.Length != 2)
            throw new ArgumentException("hay que elegir exactamente 2 generales");
        if (string.Equals(cfg.Generales[0], cfg.Generales[1], StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("los dos generales deben ser distintos");
        if (cfg.ReyIndice is < 0 or > 1) throw new ArgumentException("ReyIndice debe ser 0 o 1");

        var defs = new List<DuelosDefCarta>();
        foreach (var n in cfg.Generales)
            defs.Add(DuelosCatalogo.General(cfg.Ejercito, n)
                     ?? throw new ArgumentException($"{n} no es un general de {defEj.Nombre}"));
        defs.AddRange(defEj.Guarnicion);

        var pos = cfg.Despliegue ?? DespliegueAutomatico(e, j, rng);
        var error = ValidarDespliegue(e, j, pos);
        if (error != null) throw new ArgumentException(error);

        e.Jugadores[j] = new DuelosJugador { Ejercito = cfg.Ejercito };
        for (int i = 0; i < defs.Count; i++)
        {
            var c = new DuelosCarta
            {
                Id = e.SiguienteId++,
                Dueno = j,
                Def = defs[i],
                Pos = pos[i],
                PosInicioTurno = pos[i],
                Vidas = defs[i].Papel == DuelosPapel.General ? DuelosAjustes.VidasGeneral : 0,
            };
            e.Cartas.Add(c);
            if (i == cfg.ReyIndice) e.Jugadores[j].ReyId = c.Id;
        }
    }

    /// Valida un despliegue (6 casillas: general 1, general 2, básica, básica,
    /// media, alta). Devuelve el error o null.
    public static string? ValidarDespliegue(DuelosEstado e, int jugador, int[] pos)
    {
        if (pos == null || pos.Length != 6) return "el despliegue debe tener 6 casillas";
        var cuenta = new int[DuelosAjustes.Celdas];
        foreach (var c in pos)
        {
            if (c < 0 || c >= DuelosAjustes.Celdas) return "casilla fuera del tablero";
            if (!DuelosTablero.EnZonaDespliegue(jugador, c)) return $"{DuelosTablero.Coord(c)} no está en tus filas de despliegue";
            if (e.Pilar[c]) return $"{DuelosTablero.Coord(c)} es un pilar";
            if (++cuenta[c] > DuelosAjustes.TopeCartasCasilla)
                return $"más de {DuelosAjustes.TopeCartasCasilla} cartas en {DuelosTablero.Coord(c)}";
        }
        return null;
    }

    /// Despliegue al azar válido: reparte las 6 cartas en 2-4 casillas de la
    /// zona propia.
    public static int[] DespliegueAutomatico(DuelosEstado e, int jugador, Random rng)
    {
        var zona = Enumerable.Range(0, DuelosAjustes.Celdas)
            .Where(c => DuelosTablero.EnZonaDespliegue(jugador, c) && !e.Pilar[c])
            .OrderBy(_ => rng.Next()).ToList();
        int grupos = rng.Next(2, 5);
        var celdas = zona.Take(grupos).ToList();
        var cuenta = new int[DuelosAjustes.Celdas];
        var pos = new int[6];
        for (int i = 0; i < 6; i++)
        {
            int c;
            do c = celdas[rng.Next(celdas.Count)]; while (cuenta[c] >= DuelosAjustes.TopeCartasCasilla);
            cuenta[c]++;
            pos[i] = c;
        }
        return pos;
    }

    // ═════════════════════════════════════════════════════════════════════════
    // CONSULTAS (las usan el bot y el cliente)
    // ═════════════════════════════════════════════════════════════════════════

    public static int Movimiento(DuelosEstado e, DuelosCarta c)
    {
        int m = DuelosAjustes.Movimiento;
        if (c.MasMovHasta >= e.Turno) m += DuelosAjustes.MasMovimientoMagnitud;
        if (e.Jugadores[c.Dueno].MovilizacionHasta >= e.Turno) m += DuelosAjustes.MovilizacionMagnitud;
        return m;
    }

    /// True si una carta de [jugador] puede TERMINAR su movimiento en [c].
    public static bool PuedeTerminar(DuelosEstado e, int c, int jugador) =>
        !e.Pilar[c] && !e.Hundida[c] && !e.MuroActivo(c) && !e.EscudoContra(c, jugador);

    /// True si una carta de [jugador] puede ATRAVESAR [c] (con Movilización
    /// cruza pilares y casillas hundidas; nunca muros ni escudos rivales).
    private static bool PuedePasar(DuelosEstado e, int c, int jugador, bool movilizacion) =>
        !e.MuroActivo(c) && !e.EscudoContra(c, jugador) && (movilizacion || (!e.Pilar[c] && !e.Hundida[c]));

    /// Casillas donde [carta] puede terminar este turno (incluye la suya).
    public static bool[] Alcanzables(DuelosEstado e, DuelosCarta carta)
    {
        var res = new bool[DuelosAjustes.Celdas];
        res[carta.Pos] = true;
        if (carta.ParalizadaTurno == e.Turno) return res;
        int mov = Movimiento(e, carta);
        bool movil = e.Jugadores[carta.Dueno].MovilizacionHasta >= e.Turno;
        Span<int> dist = stackalloc int[DuelosAjustes.Celdas];
        dist.Fill(-1);
        Span<int> cola = stackalloc int[DuelosAjustes.Celdas];
        int ini = 0, fin = 0;
        dist[carta.Pos] = 0;
        cola[fin++] = carta.Pos;
        while (ini < fin)
        {
            int c = cola[ini++];
            if (dist[c] >= mov) continue;
            foreach (var v in DuelosTablero.Vecinas[c])
            {
                if (dist[v] >= 0 || !PuedePasar(e, v, carta.Dueno, movil)) continue;
                dist[v] = dist[c] + 1;
                cola[fin++] = v;
                if (PuedeTerminar(e, v, carta.Dueno)) res[v] = true;
            }
        }
        return res;
    }

    public static int AtaqueEfectivo(DuelosEstado e, DuelosCarta c)
    {
        if (c.EsClon || !c.Viva) return 0;
        int a = DuelosCatalogo.Ataque(c.Def.Papel);
        if (c.VenenoHasta >= e.Turno) a -= DuelosAjustes.VenenoMagnitud;
        if (c.MasAtaqueHasta >= e.Turno) a += DuelosAjustes.MasAtaqueMagnitud;
        if (e.Jugadores[c.Dueno].FuriaTurno == e.Turno) a += DuelosAjustes.FuriaInfernalMagnitud;
        return Math.Max(0, a);
    }

    public static bool HabilidadLista(DuelosEstado e, DuelosCarta g) =>
        g.Viva && !g.EsClon && g.EsGeneral && g.Def.Habilidad != DuelosHabilidad.Ninguna
        && e.Turno >= DuelosAjustes.HabilidadPrimerTurno && g.RecargaLista <= e.Turno;

    public static bool HabilidadEjercitoLista(DuelosEstado e, int jugador) =>
        !e.Jugadores[jugador].HabilidadEjercitoUsada && e.Turno >= DuelosAjustes.HabilidadEjercitoPrimerTurno;

    public static bool PuedeDispararLejos(DuelosEstado e, int jugador) =>
        e.Jugadores[jugador].DisparosRestantes > 0 && e.Turno >= DuelosAjustes.DisparoLejanoPrimerTurno;

    private static int CartasPropiasEn(DuelosEstado e, int celda, int jugador, DuelosCarta? excepto = null)
    {
        int n = 0;
        foreach (var c in e.Cartas)
            if (c.Viva && !c.EsClon && c.Dueno == jugador && c.Pos == celda && c != excepto) n++;
        return n;
    }

    private static bool HayEnemigos(DuelosEstado e, int celda, int jugador, bool contarClones = false)
    {
        foreach (var c in e.Cartas)
            if (c.Viva && c.Dueno != jugador && c.Pos == celda && (contarClones || !c.EsClon)) return true;
        return false;
    }

    private static bool HayCartas(DuelosEstado e, int celda)
    {
        foreach (var c in e.Cartas) if (c.Viva && c.Pos == celda) return true;
        return false;
    }

    /// Valida el uso de una habilidad de general de [jugador] con el estado AL
    /// EMPEZAR el turno. Devuelve el error o null.
    public static string? ValidarHabilidad(DuelosEstado e, int jugador, DuelosUsoHabilidad u)
    {
        var g = e.Carta(u.GeneralId);
        if (g == null || g.Dueno != jugador) return "general no válido";
        if (!HabilidadLista(e, g)) return $"{g.Def.Nombre}: habilidad no disponible";
        int t0 = u.Celdas.Length > 0 ? u.Celdas[0] : -1;
        bool CeldaOk(int c) => c >= 0 && c < DuelosAjustes.Celdas;
        switch (g.Def.Habilidad)
        {
            case DuelosHabilidad.DisparoCercano:
            case DuelosHabilidad.Confusion:
                if (!CeldaOk(t0) || DuelosTablero.Distancia(g.Pos, t0) != 1) return "objetivo: una casilla contigua";
                return null;
            case DuelosHabilidad.Paralisis:
            case DuelosHabilidad.Veneno:
                if (!CeldaOk(t0) || DuelosTablero.Distancia(g.Pos, t0) > DuelosAjustes.AlcanceMedio)
                    return $"objetivo: una casilla a {DuelosAjustes.AlcanceMedio} o menos";
                return null;
            case DuelosHabilidad.Escudo:
                if (!CeldaOk(t0) || DuelosTablero.Distancia(g.Pos, t0) > 1 || e.Pilar[t0] || e.Hundida[t0])
                    return "objetivo: su casilla o una contigua";
                return null;
            case DuelosHabilidad.MasMovimiento:
            case DuelosHabilidad.MasAtaque:
                return null;
            case DuelosHabilidad.Teletransporte:
            {
                var c = e.Carta(u.CartaId);
                if (c == null || !c.Viva || c.EsClon || c.Dueno != jugador) return "carta propia no válida";
                if (c.ParalizadaTurno == e.Turno) return "la carta está paralizada";
                if (!CeldaOk(t0) || t0 == c.Pos || DuelosTablero.Distancia(c.Pos, t0) > DuelosAjustes.TeletransporteDistancia)
                    return $"destino: a {DuelosAjustes.TeletransporteDistancia} casillas o menos de la carta";
                if (!PuedeTerminar(e, t0, jugador)) return "destino bloqueado";
                return null;
            }
            case DuelosHabilidad.Clon:
            {
                var c = e.Carta(u.CartaId);
                if (c == null || !c.Viva || c.EsClon || !c.EsGeneral || c.Dueno != jugador) return "general propio no válido";
                if (!CeldaOk(t0) || DuelosTablero.Distancia(g.Pos, t0) != 1 || !PuedeTerminar(e, t0, jugador)
                    || HayEnemigos(e, t0, jugador, true))
                    return "objetivo: una casilla contigua libre de enemigos";
                return null;
            }
            case DuelosHabilidad.Supervivencia:
            {
                var c = e.Carta(u.CartaId);
                if (c == null || !c.Viva || c.EsClon || c.Dueno != jugador) return "carta propia no válida";
                if (DuelosTablero.Distancia(g.Pos, c.Pos) > 1) return "la carta debe estar en su casilla o en una contigua";
                return null;
            }
            case DuelosHabilidad.Muro:
            {
                if (u.Celdas.Length != DuelosAjustes.MuroCeldas || u.Celdas.Any(c => !CeldaOk(c)) || u.Celdas.Distinct().Count() != u.Celdas.Length)
                    return $"el muro necesita {DuelosAjustes.MuroCeldas} casillas distintas";
                if (DuelosTablero.Distancia(g.Pos, u.Celdas[0]) > DuelosAjustes.AlcanceMedio)
                    return $"la primera casilla del muro debe estar a {DuelosAjustes.AlcanceMedio} o menos";
                for (int i = 1; i < u.Celdas.Length; i++)
                {
                    bool encadenada = false;
                    for (int k = 0; k < i; k++)
                        if (DuelosTablero.Distancia(u.Celdas[k], u.Celdas[i]) == 1) encadenada = true;
                    if (!encadenada) return "las casillas del muro deben ir encadenadas";
                }
                foreach (var c in u.Celdas)
                    if (e.Pilar[c] || e.Hundida[c] || e.MuroActivo(c) || e.EscudoHasta[c] >= e.Turno || HayCartas(e, c))
                        return $"{DuelosTablero.Coord(c)} no está libre";
                return null;
            }
        }
        return "habilidad desconocida";
    }

    // ═════════════════════════════════════════════════════════════════════════
    // RESOLUCIÓN
    // ═════════════════════════════════════════════════════════════════════════

    /// Resuelve el turno en curso con las jugadas de los dos jugadores y avanza
    /// al siguiente. Las declaraciones no válidas se ignoran (con evento).
    public static void Resolver(DuelosEstado e, DuelosJugada? jugada0, DuelosJugada? jugada1, Random rng)
    {
        if (e.Terminada) return;
        int t = e.Turno;
        e.Eventos.Clear();
        var jug = new[] { jugada0 ?? DuelosJugada.Vacia(), jugada1 ?? DuelosJugada.Vacia() };

        foreach (var c in e.Cartas)
        {
            c.PosInicioTurno = c.Pos;
            c.SeMovio = false;
        }

        // ── 1. Habilidades de ejército ───────────────────────────────────────
        for (int p = 0; p < 2; p++)
        {
            if (!jug[p].UsarHabilidadEjercito || !HabilidadEjercitoLista(e, p)) continue;
            var jp = e.Jugadores[p];
            bool ok = true;
            switch (jp.HabilidadEjercito)
            {
                case DuelosHabilidadEjercito.Movilizacion: jp.MovilizacionHasta = t + DuelosAjustes.MovilizacionTurnos - 1; break;
                case DuelosHabilidadEjercito.FuriaInfernal: jp.FuriaTurno = t; break;
                case DuelosHabilidadEjercito.Fortaleza: jp.FortalezaHasta = t + DuelosAjustes.FortalezaTurnos - 1; break;
                case DuelosHabilidadEjercito.Reparacion:
                {
                    var g = e.Carta(jug[p].HabilidadEjercitoCartaId);
                    if (g == null || g.Dueno != p || !g.Viva || g.EsClon || !g.EsGeneral || g.Vidas >= DuelosAjustes.VidasGeneral)
                        ok = false;
                    else
                    {
                        g.Vidas = Math.Min(DuelosAjustes.VidasGeneral, g.Vidas + DuelosAjustes.ReparacionVidas);
                        if (DuelosAjustes.ReparacionInmoviliza) g.ParalizadaTurno = t;
                    }
                    break;
                }
            }
            if (!ok) { Ev(e, "invalida", p, -1, "Reparación sin objetivo válido"); continue; }
            jp.HabilidadEjercitoUsada = true;
            jp.HabilidadEjercitoTurno = t;
            Ev(e, "ejercito:" + jp.HabilidadEjercito, p, -1, jp.HabilidadEjercito.ToString());
        }

        // ── 2. Validación de habilidades de general y efectos previos ───────
        var usos = new List<(int p, DuelosCarta g, DuelosUsoHabilidad u)>();
        var teletransportes = new Dictionary<int, int>(); // cartaId → destino
        var creadas = new HashSet<int>();
        for (int p = 0; p < 2; p++)
        {
            var usados = new HashSet<int>();
            foreach (var u in jug[p].Habilidades)
            {
                if (!usados.Add(u.GeneralId)) continue;
                var err = ValidarHabilidad(e, p, u);
                if (err != null) { Ev(e, "invalida", p, -1, err); continue; }
                var g = e.Carta(u.GeneralId)!;
                g.RecargaLista = t + DuelosAjustes.RecargaHabilidad + 1;
                usos.Add((p, g, u));
                Ev(e, "habilidad:" + g.Def.Habilidad, p, u.Celdas.Length > 0 ? u.Celdas[0] : g.Pos, g.Def.Nombre);
            }
        }
        foreach (var (p, g, u) in usos)
        {
            switch (g.Def.Habilidad)
            {
                case DuelosHabilidad.Escudo:
                    e.EscudoHasta[u.Celdas[0]] = t + DuelosAjustes.EscudoTurnos - 1;
                    e.EscudoDueno[u.Celdas[0]] = p;
                    break;
                case DuelosHabilidad.Muro:
                    foreach (var c in u.Celdas) e.MuroHasta[c] = t + DuelosAjustes.MuroTurnos - 1;
                    break;
                case DuelosHabilidad.MasMovimiento:
                    foreach (var c in e.Cartas)
                        if (c.Viva && c.Dueno == p && c.Pos == g.Pos)
                            c.MasMovHasta = t + DuelosAjustes.MasMovimientoTurnos - 1;
                    break;
                case DuelosHabilidad.Teletransporte:
                    teletransportes[u.CartaId] = u.Celdas[0];
                    break;
                case DuelosHabilidad.Supervivencia:
                    e.Carta(u.CartaId)!.SupervivenciaHasta = t + DuelosAjustes.SupervivenciaTurnos - 1;
                    break;
                case DuelosHabilidad.Clon:
                {
                    var orig = e.Carta(u.CartaId)!;
                    var clon = new DuelosCarta
                    {
                        Id = e.SiguienteId++,
                        Dueno = p,
                        Def = orig.Def,
                        Pos = u.Celdas[0],
                        PosInicioTurno = u.Celdas[0],
                        Vidas = orig.Vidas,
                        EsClon = true,
                        ClonDe = orig.Id,
                        ClonHasta = t + DuelosAjustes.ClonTurnos - 1,
                        CreadaTurno = t,
                        RecargaLista = orig.RecargaLista,
                    };
                    e.Cartas.Add(clon);
                    creadas.Add(clon.Id);
                    break;
                }
            }
        }

        // ── 3. Movimiento ────────────────────────────────────────────────────
        // Destinos con el estado tras los escudos y muros de este turno.
        var destino = new Dictionary<DuelosCarta, int>();
        foreach (var c in e.Cartas)
        {
            if (!c.Viva || creadas.Contains(c.Id)) continue;
            if (c.ParalizadaTurno == t) continue;
            if (c.ConfusaTurno == t)
            {
                var alc = Alcanzables(e, c);
                var opciones = Enumerable.Range(0, DuelosAjustes.Celdas).Where(i => alc[i]).ToList();
                destino[c] = opciones[rng.Next(opciones.Count)];
                continue;
            }
            if (teletransportes.TryGetValue(c.Id, out var tele)) { destino[c] = tele; continue; }
            if (!jug[c.Dueno].Movimientos.TryGetValue(c.Id, out var d) || d == c.Pos) continue;
            if (d < 0 || d >= DuelosAjustes.Celdas || !Alcanzables(e, c)[d])
            {
                Ev(e, "invalida", c.Dueno, d, $"{c.Def.Nombre}: destino no alcanzable");
                continue;
            }
            destino[c] = d;
        }
        foreach (var (c, d) in destino) c.Pos = d;

        // Reversiones: bloqueos (escudo, muro, fortaleza) y tope por casilla.
        for (int iter = 0; iter < 8; iter++)
        {
            bool cambio = false;
            foreach (var c in e.Cartas)
            {
                if (!c.Viva || c.Pos == c.PosInicioTurno) continue;
                int rival = 1 - c.Dueno;
                bool bloqueada = !PuedeTerminar(e, c.Pos, c.Dueno)
                                 || (!c.EsClon && e.FortalezaActiva(rival) && CartasPropiasEn(e, c.Pos, rival) > 0);
                if (bloqueada)
                {
                    Ev(e, "bloqueo", c.Dueno, c.Pos, c.Def.Nombre);
                    c.Pos = c.PosInicioTurno;
                    cambio = true;
                }
            }
            for (int p = 0; p < 2; p++)
            {
                var porCelda = e.Cartas.Where(c => c.Viva && !c.EsClon && c.Dueno == p).GroupBy(c => c.Pos);
                foreach (var gr in porCelda)
                {
                    int exceso = gr.Count() - DuelosAjustes.TopeCartasCasilla;
                    if (exceso <= 0) continue;
                    foreach (var c in gr.Where(c => c.PosInicioTurno != c.Pos).OrderByDescending(c => c.Id).Take(exceso).ToList())
                    {
                        Ev(e, "tope", p, c.Pos, c.Def.Nombre);
                        c.Pos = c.PosInicioTurno;
                        cambio = true;
                    }
                }
            }
            if (!cambio) break;
        }
        foreach (var c in e.Cartas) c.SeMovio = c.Viva && c.Pos != c.PosInicioTurno;

        // ── 4. Disparos y habilidades sobre casillas ─────────────────────────
        var disparos = new List<(int p, int celda, string causa)>();
        for (int p = 0; p < 2; p++)
        {
            foreach (var d in jug[p].Disparos)
            {
                if (!PuedeDispararLejos(e, p) || d < 0 || d >= DuelosAjustes.Celdas) continue;
                e.Jugadores[p].DisparosRestantes--;
                disparos.Add((p, d, "disparo lejano"));
                Ev(e, "disparo", p, d, "disparo lejano");
            }
        }
        foreach (var (p, g, u) in usos)
            if (g.Def.Habilidad == DuelosHabilidad.DisparoCercano)
                disparos.Add((p, u.Celdas[0], "disparo cercano"));

        foreach (var (p, celda, causa) in disparos)
        {
            if (e.EscudoContra(celda, p)) { Ev(e, "escudo", 1 - p, celda, "el escudo detiene el disparo"); continue; }
            foreach (var c in e.Cartas.Where(c => c.Viva && c.Pos == celda).ToList())
            {
                if (c.Dueno != p && e.FortalezaActiva(c.Dueno)) continue;
                if (c.EsClon) { Desaparecer(e, c, "el clon recibe un disparo"); continue; }
                if (c.EsGeneral) QuitarVida(e, c, causa);
                else Morir(e, c, causa);
            }
        }

        foreach (var (p, g, u) in usos)
        {
            var h = g.Def.Habilidad;
            if (h is not (DuelosHabilidad.Veneno or DuelosHabilidad.Paralisis or DuelosHabilidad.Confusion or DuelosHabilidad.MasAtaque))
                continue;
            if (h == DuelosHabilidad.MasAtaque)
            {
                if (!g.Viva) continue;
                foreach (var c in e.Cartas)
                    if (c.Viva && !c.EsClon && c.Dueno == p && c.Pos == g.Pos)
                        c.MasAtaqueHasta = t + DuelosAjustes.MasAtaqueTurnos - 1;
                continue;
            }
            int celda = u.Celdas[0];
            if (e.EscudoContra(celda, p)) { Ev(e, "escudo", 1 - p, celda, "el escudo detiene la habilidad"); continue; }
            foreach (var c in e.Cartas)
            {
                if (!c.Viva || c.EsClon || c.Pos != celda) continue;
                if (c.Dueno != p && e.FortalezaActiva(c.Dueno)) continue;
                switch (h)
                {
                    case DuelosHabilidad.Veneno: c.VenenoHasta = t + DuelosAjustes.VenenoTurnos - 1; break;
                    case DuelosHabilidad.Paralisis: c.ParalizadaTurno = t + 1; break;
                    case DuelosHabilidad.Confusion:
                        if (c.Dueno != p) c.ConfusaTurno = t + 1;
                        break;
                }
            }
        }

        // ── 5. Clones que tocan a un enemigo ─────────────────────────────────
        foreach (var c in e.Cartas.Where(c => c.Viva && c.EsClon).ToList())
            if (HayEnemigos(e, c.Pos, c.Dueno, true)) Desaparecer(e, c, "el clon se desvanece al contacto");

        // ── 6. Combate ───────────────────────────────────────────────────────
        var celdasCombate = e.Cartas.Where(c => c.Viva && !c.EsClon)
            .GroupBy(c => c.Pos)
            .Where(g => g.Any(c => c.Dueno == 0) && g.Any(c => c.Dueno == 1))
            .Select(g => (celda: g.Key, cartas: g.ToList()))
            .ToList();
        foreach (var (celda, cartas) in celdasCombate)
        {
            int a0 = cartas.Where(c => c.Dueno == 0).Sum(c => AtaqueEfectivo(e, c));
            int a1 = cartas.Where(c => c.Dueno == 1).Sum(c => AtaqueEfectivo(e, c));
            int ganador = a0 > a1 ? 0 : a1 > a0 ? 1 : -1;
            Ev(e, "combate", ganador, celda, $"{a0} contra {a1}");
            foreach (var c in cartas)
                if (ganador == -1 || c.Dueno != ganador)
                    Derrota(e, c, celda, "combate", rng);
        }

        // ── 7. Hundimiento ───────────────────────────────────────────────────
        foreach (var h0 in e.Hundimientos)
        {
            foreach (var c in e.Cartas.Where(c => c.Viva && c.Pos == h0).ToList())
            {
                if (c.EsClon) { Desaparecer(e, c, "el clon cae al vacío"); continue; }
                if (!c.EsGeneral) { Morir(e, c, "hundimiento"); continue; }
                QuitarVida(e, c, "hundimiento");
                if (c.Viva) Empujar(e, c, h0, rng, atrapadoCuesta: true);
            }
            e.Hundida[h0] = true;
            e.MuroHasta[h0] = 0;
            e.EscudoHasta[h0] = 0;
            Ev(e, "hundimiento", -1, h0, DuelosTablero.Coord(h0));
        }
        e.Hundimientos.Clear();

        // ── 8. Clones caducados y fin de partida ─────────────────────────────
        foreach (var c in e.Cartas.Where(c => c.Viva && c.EsClon && c.ClonHasta <= t).ToList())
            Desaparecer(e, c, "el clon se desvanece");
        // Los clones copian las vidas y la recarga del original para no delatarse.
        foreach (var c in e.Cartas.Where(c => c.Viva && c.EsClon))
        {
            var orig = e.Carta(c.ClonDe);
            if (orig == null || !orig.Viva) continue;
            c.Vidas = orig.Vidas;
            c.RecargaLista = orig.RecargaLista;
        }

        var rey0 = e.Rey(0);
        var rey1 = e.Rey(1);
        bool m0 = rey0 == null || !rey0.Viva;
        bool m1 = rey1 == null || !rey1.Viva;
        if (m0 && m1) Fin(e, 2, "ambos reyes");
        else if (m0) Fin(e, 1, rey0?.UltimoDano ?? "");
        else if (m1) Fin(e, 0, rey1?.UltimoDano ?? "");
        else if (t >= DuelosAjustes.TurnoLimite) Desempate(e);

        // Limpieza de cadáveres y clones desaparecidos.
        e.Cartas.RemoveAll(c => !c.Viva && (c.EsClon || !c.EsGeneral));

        e.Turno = t + 1;
        if (!e.Terminada && e.Turno >= DuelosAjustes.HundimientoPrimerTurno)
            AnunciarHundimiento(e, rng);
    }

    // ── Daño y desplazamientos ───────────────────────────────────────────────

    private static void Derrota(DuelosEstado e, DuelosCarta c, int celda, string causa, Random rng)
    {
        if (!c.Viva) return;
        bool superv = c.SupervivenciaHasta >= e.Turno;
        if (!c.EsGeneral)
        {
            if (superv && Empujar(e, c, celda, rng, atrapadoCuesta: false)) { Ev(e, "huida", c.Dueno, c.Pos, c.Def.Nombre); return; }
            Morir(e, c, causa);
            return;
        }
        if (superv) Ev(e, "huida", c.Dueno, celda, c.Def.Nombre);
        else QuitarVida(e, c, causa);
        if (c.Viva) Empujar(e, c, celda, rng, atrapadoCuesta: true);
    }

    /// Empuja [c] a una casilla contigua libre (sin enemigos, con sitio). Si no
    /// hay ninguna y [atrapadoCuesta], un general pierde 1 vida más.
    private static bool Empujar(DuelosEstado e, DuelosCarta c, int desde, Random rng, bool atrapadoCuesta)
    {
        var opciones = DuelosTablero.Vecinas[desde]
            .Where(v => PuedeTerminar(e, v, c.Dueno) && !HayEnemigos(e, v, c.Dueno)
                        && CartasPropiasEn(e, v, c.Dueno, c) < DuelosAjustes.TopeCartasCasilla)
            .ToList();
        if (opciones.Count > 1)
        {
            var seguras = opciones.Where(v => !e.SeHunde(v)).ToList();
            if (seguras.Count > 0) opciones = seguras;
        }
        if (opciones.Count == 0)
        {
            if (atrapadoCuesta && c.EsGeneral)
            {
                Ev(e, "atrapado", c.Dueno, desde, c.Def.Nombre);
                QuitarVida(e, c, "atrapado");
            }
            return false;
        }
        c.Pos = opciones[rng.Next(opciones.Count)];
        return true;
    }

    private static void QuitarVida(DuelosEstado e, DuelosCarta c, string causa)
    {
        if (!c.Viva) return;
        c.Vidas--;
        c.UltimoDano = causa;
        Ev(e, "vida", c.Dueno, c.Pos, $"{c.Def.Nombre} pierde una vida ({causa})");
        if (c.Vidas <= 0) Morir(e, c, causa);
    }

    private static void Morir(DuelosEstado e, DuelosCarta c, string causa)
    {
        if (!c.Viva) return;
        c.Viva = false;
        c.UltimoDano = causa;
        Ev(e, c.EsGeneral ? "muere_general" : "muere", c.Dueno, c.Pos, $"{c.Def.Nombre} ({causa})");
    }

    private static void Desaparecer(DuelosEstado e, DuelosCarta c, string texto)
    {
        if (!c.Viva) return;
        c.Viva = false;
        Ev(e, "clon", c.Dueno, c.Pos, texto);
    }

    // ── Fin ──────────────────────────────────────────────────────────────────

    private static void Fin(DuelosEstado e, int ganador, string motivo)
    {
        e.Ganador = ganador;
        e.Motivo = motivo;
        Ev(e, "fin", ganador, -1, motivo);
    }

    /// Límite de turnos: más vidas de generales; si empatan, más ataque en el
    /// tablero; si no, tablas.
    private static void Desempate(DuelosEstado e)
    {
        int Vidas(int p) => e.Cartas.Where(c => c.Viva && !c.EsClon && c.EsGeneral && c.Dueno == p).Sum(c => c.Vidas);
        int Ataque(int p) => e.Cartas.Where(c => c.Viva && !c.EsClon && c.Dueno == p).Sum(c => DuelosCatalogo.Ataque(c.Def.Papel));
        int v0 = Vidas(0), v1 = Vidas(1);
        if (v0 != v1) { Fin(e, v0 > v1 ? 0 : 1, "limite"); return; }
        int a0 = Ataque(0), a1 = Ataque(1);
        if (a0 != a1) { Fin(e, a0 > a1 ? 0 : 1, "limite"); return; }
        Fin(e, 2, "limite");
    }

    // ── Hundimiento ──────────────────────────────────────────────────────────

    /// Anuncia las casillas que se hundirán al resolver el turno en curso:
    /// vacías, sin pilar ni hundidas, y que no partan el tablero. Más peso en
    /// el borde. Cuántas: DuelosAjustes.CasillasHundidasEnTurno (1, 2 o 3).
    public static void AnunciarHundimiento(DuelosEstado e, Random rng)
    {
        e.Hundimientos.Clear();
        int n = DuelosAjustes.CasillasHundidasEnTurno(e.Turno);
        for (int k = 0; k < n; k++)
        {
            var candidatas = new List<(int c, double w)>();
            double total = 0;
            for (int c = 0; c < DuelosAjustes.Celdas; c++)
            {
                if (e.Pilar[c] || e.Hundida[c] || e.SeHunde(c) || HayCartas(e, c)) continue;
                if (!SigueConectado(e, c)) continue;
                double w = 1 + DuelosAjustes.HundimientoPesoBorde * DuelosTablero.Anillo(c);
                candidatas.Add((c, w));
                total += w;
            }
            if (candidatas.Count == 0) return;
            double r = rng.NextDouble() * total;
            int elegida = candidatas[^1].c;
            foreach (var (c, w) in candidatas)
            {
                r -= w;
                if (r <= 0) { elegida = c; break; }
            }
            e.Hundimientos.Add(elegida);
        }
    }

    /// True si, quitando [quitar], las casillas transitables siguen conectadas.
    private static bool SigueConectado(DuelosEstado e, int quitar)
    {
        Span<bool> visto = stackalloc bool[DuelosAjustes.Celdas];
        Span<int> cola = stackalloc int[DuelosAjustes.Celdas];
        int total = 0, inicio = -1;
        for (int c = 0; c < DuelosAjustes.Celdas; c++)
            if (c != quitar && !e.Pilar[c] && !e.Hundida[c] && !e.SeHunde(c)) { total++; if (inicio < 0) inicio = c; }
        if (total == 0) return true;
        int ini = 0, fin = 0, vistos = 1;
        visto[inicio] = true;
        cola[fin++] = inicio;
        while (ini < fin)
        {
            int c = cola[ini++];
            foreach (var v in DuelosTablero.Vecinas[c])
            {
                if (visto[v] || v == quitar || e.Pilar[v] || e.Hundida[v] || e.SeHunde(v)) continue;
                visto[v] = true;
                vistos++;
                cola[fin++] = v;
            }
        }
        return vistos == total;
    }

    private static void Ev(DuelosEstado e, string tipo, int jugador, int celda, string texto)
    {
        if (e.RegistrarEventos) e.Eventos.Add(new DuelosEvento(tipo, jugador, celda, texto));
    }
}

// ═════════════════════════════════════════════════════════════════════════════
// VISTA DE UN JUGADOR (información oculta)
// ═════════════════════════════════════════════════════════════════════════════
public static class DuelosVista
{
    /// Copia del estado tal como la ve [observador]: los clones rivales parecen
    /// generales de verdad y el rey rival es una SUPOSICIÓN al azar entre los
    /// generales rivales visibles. La usan el bot (y servirá para el cliente).
    public static DuelosEstado Para(DuelosEstado real, int observador, Random rng)
    {
        var v = real.Clonar();
        v.RegistrarEventos = false;
        int rival = 1 - observador;
        var candidatos = new List<DuelosCarta>();
        foreach (var c in v.Cartas)
        {
            if (c.Dueno != rival || !c.Viva) continue;
            if (c.EsClon) c.EsClon = false;
            if (c.EsGeneral) candidatos.Add(c);
        }
        v.Jugadores[rival].ReyId = candidatos.Count > 0 ? candidatos[rng.Next(candidatos.Count)].Id : -1;
        return v;
    }
}
