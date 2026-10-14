// ─────────────────────────────────────────────────────────────────────────────
// DuelosSerializacion.cs
//
// MODO DUELOS — paso del estado del motor (DuelosEstado) a mapas planos y
// vuelta, y VISTA de la partida para cada jugador. Lógica pura (sin
// Firestore): los mapas usan long, string, bool, listas y diccionarios, que
// Firestore guarda tal cual y que el endpoint serializa a JSON.
//
//   • ToMapa / DesdeMapa: estado COMPLETO (incluye cuál es el comandante de
//     cada jugador). Solo lo lee el servidor: el campo `juego` del documento
//     Duelos/{id} nunca se devuelve tal cual al cliente.
//   • Vista: lo que ve un jugador. El comandante RIVAL no aparece, los clones
//     rivales parecen generales de verdad y los avisos de jugadas no válidas
//     del rival se omiten.
//   • JugadaDesdeMapa: la jugada que manda el cliente (coordenadas "C4").
// ─────────────────────────────────────────────────────────────────────────────

public static class DuelosSerializacion
{
    // ═════════════════════════════════════════════════════════════════════════
    // ESTADO COMPLETO
    // ═════════════════════════════════════════════════════════════════════════

    public static Dictionary<string, object?> ToMapa(DuelosEstado e)
    {
        var muros = new Dictionary<string, object?>();
        var escudos = new List<object?>();
        var pilares = new List<object?>();
        var hundidas = new List<object?>();
        for (int c = 0; c < DuelosAjustes.Celdas; c++)
        {
            var coord = DuelosTablero.Coord(c);
            if (e.Pilar[c]) pilares.Add(coord);
            if (e.Hundida[c]) hundidas.Add(coord);
            if (e.MuroHasta[c] > 0) muros[coord] = (long)e.MuroHasta[c];
            if (e.EscudoHasta[c] > 0)
                escudos.Add(new Dictionary<string, object?>
                {
                    ["celda"] = coord,
                    ["hasta"] = (long)e.EscudoHasta[c],
                    ["dueno"] = (long)e.EscudoDueno[c],
                });
        }

        return new Dictionary<string, object?>
        {
            ["version"] = 1L,
            ["turno"] = (long)e.Turno,
            ["siguienteId"] = (long)e.SiguienteId,
            ["ganador"] = (long)e.Ganador,
            ["motivo"] = e.Motivo,
            ["pilares"] = pilares,
            ["hundidas"] = hundidas,
            ["hundimientos"] = e.Hundimientos.Select(c => (object?)DuelosTablero.Coord(c)).ToList(),
            ["muros"] = muros,
            ["escudos"] = escudos,
            ["jugadores"] = e.Jugadores.Select(j => (object?)new Dictionary<string, object?>
            {
                ["ejercito"] = (long)(int)j.Ejercito,
                ["comandanteId"] = (long)j.ReyId,
                ["disparos"] = (long)j.DisparosRestantes,
                ["habilidadEjercitoUsada"] = j.HabilidadEjercitoUsada,
                ["habilidadEjercitoTurno"] = (long)j.HabilidadEjercitoTurno,
                ["fortalezaHasta"] = (long)j.FortalezaHasta,
                ["furiaTurno"] = (long)j.FuriaTurno,
                ["movilizacionHasta"] = (long)j.MovilizacionHasta,
                ["pc"] = (long)j.Pc,
            }).ToList(),
            ["cartas"] = e.Cartas.Select(c => (object?)new Dictionary<string, object?>
            {
                ["id"] = (long)c.Id,
                ["dueno"] = (long)c.Dueno,
                ["cartaId"] = c.Def.CartaId,
                ["nombre"] = c.Def.Nombre,
                ["papel"] = c.Def.Papel.ToString(),
                ["pos"] = DuelosTablero.Coord(c.Pos),
                ["posInicio"] = DuelosTablero.Coord(c.PosInicioTurno),
                ["seMovio"] = c.SeMovio,
                ["viva"] = c.Viva,
                ["vidas"] = (long)c.Vidas,
                ["esClon"] = c.EsClon,
                ["clonDe"] = (long)c.ClonDe,
                ["clonHasta"] = (long)c.ClonHasta,
                ["creada"] = (long)c.CreadaTurno,
                ["recarga"] = (long)c.RecargaLista,
                ["paralizada"] = (long)c.ParalizadaTurno,
                ["confusa"] = (long)c.ConfusaTurno,
                ["veneno"] = (long)c.VenenoHasta,
                ["masAtaque"] = (long)c.MasAtaqueHasta,
                ["masMov"] = (long)c.MasMovHasta,
                ["supervivencia"] = (long)c.SupervivenciaHasta,
                ["ultimoDano"] = c.UltimoDano,
            }).ToList(),
            ["eventos"] = e.Eventos.Select(v => (object?)new Dictionary<string, object?>
            {
                ["tipo"] = v.Tipo,
                ["jugador"] = (long)v.Jugador,
                ["celda"] = v.Celda >= 0 ? DuelosTablero.Coord(v.Celda) : "",
                ["texto"] = v.Texto,
            }).ToList(),
        };
    }

    public static DuelosEstado DesdeMapa(object? raw)
    {
        var d = Dic(raw);
        var e = new DuelosEstado
        {
            Turno = Int(d, "turno", 1),
            SiguienteId = Int(d, "siguienteId"),
            Ganador = Int(d, "ganador", -1),
            Motivo = Str(d, "motivo"),
        };
        foreach (var c in Lst(d, "pilares")) Marcar(e.Pilar, c);
        foreach (var c in Lst(d, "hundidas")) Marcar(e.Hundida, c);
        foreach (var c in Lst(d, "hundimientos"))
        {
            int i = DuelosTablero.Parse(c?.ToString() ?? "");
            if (i >= 0) e.Hundimientos.Add(i);
        }
        foreach (var kv in Dic(Get(d, "muros")))
        {
            int i = DuelosTablero.Parse(kv.Key);
            if (i >= 0) e.MuroHasta[i] = ToInt(kv.Value);
        }
        foreach (var x in Lst(d, "escudos"))
        {
            var m = Dic(x);
            int i = DuelosTablero.Parse(Str(m, "celda"));
            if (i < 0) continue;
            e.EscudoHasta[i] = Int(m, "hasta");
            e.EscudoDueno[i] = Int(m, "dueno");
        }

        var js = Lst(d, "jugadores");
        for (int j = 0; j < 2 && j < js.Count; j++)
        {
            var m = Dic(js[j]);
            e.Jugadores[j] = new DuelosJugador
            {
                Ejercito = (DuelosEjercito)Int(m, "ejercito", 1),
                ReyId = Int(m, "comandanteId", -1),
                DisparosRestantes = Int(m, "disparos"),
                HabilidadEjercitoUsada = Bool(m, "habilidadEjercitoUsada"),
                HabilidadEjercitoTurno = Int(m, "habilidadEjercitoTurno"),
                FortalezaHasta = Int(m, "fortalezaHasta"),
                FuriaTurno = Int(m, "furiaTurno"),
                MovilizacionHasta = Int(m, "movilizacionHasta"),
                Pc = Int(m, "pc"),
            };
        }

        foreach (var x in Lst(d, "cartas"))
        {
            var m = Dic(x);
            int dueno = Int(m, "dueno");
            var def = BuscarDef(e.Jugadores[dueno].Ejercito, Str(m, "nombre"), Str(m, "papel"));
            if (def == null) continue;
            e.Cartas.Add(new DuelosCarta
            {
                Id = Int(m, "id"),
                Dueno = dueno,
                Def = def,
                Pos = DuelosTablero.Parse(Str(m, "pos")),
                PosInicioTurno = DuelosTablero.Parse(Str(m, "posInicio")),
                SeMovio = Bool(m, "seMovio"),
                Viva = Bool(m, "viva", true),
                Vidas = Int(m, "vidas"),
                EsClon = Bool(m, "esClon"),
                ClonDe = Int(m, "clonDe", -1),
                ClonHasta = Int(m, "clonHasta"),
                CreadaTurno = Int(m, "creada"),
                RecargaLista = Int(m, "recarga", DuelosAjustes.HabilidadPrimerTurno),
                ParalizadaTurno = Int(m, "paralizada"),
                ConfusaTurno = Int(m, "confusa"),
                VenenoHasta = Int(m, "veneno"),
                MasAtaqueHasta = Int(m, "masAtaque"),
                MasMovHasta = Int(m, "masMov"),
                SupervivenciaHasta = Int(m, "supervivencia"),
                UltimoDano = Str(m, "ultimoDano"),
            });
        }

        foreach (var x in Lst(d, "eventos"))
        {
            var m = Dic(x);
            var celda = Str(m, "celda");
            e.Eventos.Add(new DuelosEvento(Str(m, "tipo"), Int(m, "jugador", -1),
                celda == "" ? -1 : DuelosTablero.Parse(celda), Str(m, "texto")));
        }
        return e;
    }

    private static DuelosDefCarta? BuscarDef(DuelosEjercito ej, string nombre, string papel)
    {
        if (!DuelosCatalogo.Ejercitos.TryGetValue(ej, out var def)) return null;
        return def.Generales.Concat(def.Guarnicion)
            .FirstOrDefault(c => c.Nombre == nombre && c.Papel.ToString() == papel);
    }

    private static void Marcar(bool[] arr, object? coord)
    {
        int i = DuelosTablero.Parse(coord?.ToString() ?? "");
        if (i >= 0) arr[i] = true;
    }

    // ═════════════════════════════════════════════════════════════════════════
    // VISTA DE UN JUGADOR
    // ═════════════════════════════════════════════════════════════════════════

    /// Lo que ve el jugador [yo] (0 o 1). Nunca incluye el comandante rival.
    public static Dictionary<string, object?> Vista(DuelosEstado e, int yo)
    {
        int rival = 1 - yo;
        var muros = new List<object?>();
        var escudos = new List<object?>();
        var bloqueadas = new List<object?>();
        var hundidas = new List<object?>();
        for (int c = 0; c < DuelosAjustes.Celdas; c++)
        {
            var coord = DuelosTablero.Coord(c);
            if (e.Pilar[c]) bloqueadas.Add(coord);
            if (e.Hundida[c]) hundidas.Add(coord);
            if (e.MuroActivo(c))
                muros.Add(new Dictionary<string, object?> { ["celda"] = coord, ["hasta"] = (long)e.MuroHasta[c] });
            if (e.EscudoHasta[c] >= e.Turno)
                escudos.Add(new Dictionary<string, object?>
                {
                    ["celda"] = coord,
                    ["hasta"] = (long)e.EscudoHasta[c],
                    ["propio"] = e.EscudoDueno[c] == yo,
                });
        }

        var jugadores = new List<object?>();
        for (int j = 0; j < 2; j++)
        {
            var jp = e.Jugadores[j];
            var defEj = DuelosCatalogo.Ejercitos[jp.Ejercito];
            var habEj = new Dictionary<string, object?>
            {
                ["tipo"] = jp.HabilidadEjercito.ToString(),
                ["nombre"] = DuelosCatalogo.NombreHabilidadEjercito(jp.HabilidadEjercito),
                ["descripcion"] = DuelosCatalogo.DescripcionHabilidadEjercito(jp.HabilidadEjercito),
                ["usada"] = jp.HabilidadEjercitoUsada,
                ["lista"] = DuelosMotor.HabilidadEjercitoLista(e, j),
            };
            if (j == yo)
            {
                // Órdenes: la Reparación necesita un general herido.
                bool requiereCarta = jp.HabilidadEjercito == DuelosHabilidadEjercito.Reparacion;
                habEj["requiereCarta"] = requiereCarta;
                habEj["objetivos"] = requiereCarta
                    ? e.Cartas.Where(c => c.Viva && !c.EsClon && c.EsGeneral && c.Dueno == yo
                                          && c.Vidas < DuelosAjustes.VidasGeneral)
                              .Select(c => (object?)(long)c.Id).ToList()
                    : new List<object?>();
            }
            jugadores.Add(new Dictionary<string, object?>
            {
                ["indice"] = (long)j,
                ["propio"] = j == yo,
                ["ejercitoId"] = (long)(int)jp.Ejercito,
                ["ejercito"] = defEj.Nombre,
                ["disparosRestantes"] = (long)jp.DisparosRestantes,
                ["pc"] = (long)jp.Pc,
                ["puedeDisparar"] = DuelosMotor.PuedeDispararLejos(e, j),
                ["habilidadEjercito"] = habEj,
                ["fortalezaActiva"] = jp.FortalezaHasta >= e.Turno,
                ["furiaActiva"] = jp.FuriaTurno == e.Turno,
                ["movilizacionActiva"] = jp.MovilizacionHasta >= e.Turno,
            });
        }

        var cartas = new List<object?>();
        foreach (var c in e.Cartas)
        {
            if (!c.Viva) continue;
            bool propia = c.Dueno == yo;
            // Un clon rival se presenta como el general que copia.
            bool clonVisible = propia && c.EsClon;
            var h = c.Def.Habilidad;
            var carta = new Dictionary<string, object?>
            {
                ["id"] = (long)c.Id,
                ["propia"] = propia,
                ["dueno"] = (long)c.Dueno,
                ["nombre"] = c.Def.Nombre,
                ["cartaId"] = c.Def.CartaId,
                ["papel"] = c.Def.Papel.ToString(),
                ["celda"] = DuelosTablero.Coord(c.Pos),
                ["ataque"] = (long)(c.EsClon && !propia ? DuelosCatalogo.Ataque(c.Def.Papel) : DuelosMotor.AtaqueEfectivo(e, c)),
                ["ataqueBase"] = (long)DuelosCatalogo.Ataque(c.Def.Papel),
                ["movimiento"] = (long)DuelosMotor.Movimiento(e, c),
                ["esGeneral"] = c.EsGeneral,
                ["vidas"] = (long)c.Vidas,
                ["vidasMax"] = (long)(c.EsGeneral ? DuelosAjustes.VidasGeneral : 0),
                ["seMovio"] = c.SeMovio,
                ["efectos"] = new Dictionary<string, object?>
                {
                    ["paralizada"] = c.ParalizadaTurno == e.Turno,
                    ["confusa"] = c.ConfusaTurno == e.Turno,
                    ["veneno"] = c.VenenoHasta >= e.Turno,
                    ["masAtaque"] = c.MasAtaqueHasta >= e.Turno,
                    ["masMovimiento"] = c.MasMovHasta >= e.Turno,
                    ["supervivencia"] = propia && c.SupervivenciaHasta >= e.Turno,
                },
            };
            if (propia)
            {
                carta["esComandante"] = c.Id == e.Jugadores[yo].ReyId;
                carta["esClon"] = clonVisible;
                if (clonVisible) carta["clonHasta"] = (long)c.ClonHasta;
                // Órdenes: casillas a las que puede ir este turno (una carta
                // confusa se mueve sola y no obedece).
                AnadirAlcanzables(e, c, yo, carta);
            }
            if (c.EsGeneral && h != DuelosHabilidad.Ninguna)
            {
                // Un clon rival muestra el estado de la habilidad del original.
                var refHab = c;
                if (c.EsClon && !propia && e.Carta(c.ClonDe) is { Viva: true } orig) refHab = orig;
                var hab = new Dictionary<string, object?>
                {
                    ["tipo"] = h.ToString(),
                    ["nombre"] = DuelosCatalogo.NombreHabilidad(h),
                    ["descripcion"] = DuelosCatalogo.DescripcionHabilidad(h),
                    ["lista"] = c.EsClon && propia ? false : DuelosMotor.HabilidadLista(e, refHab),
                    ["recargaLista"] = (long)refHab.RecargaLista,
                };
                if (propia && !c.EsClon)
                {
                    // Órdenes: objetivos válidos de la habilidad este turno.
                    var op = DuelosMotor.OpcionesHabilidad(e, c);
                    hab["modo"] = op.Modo;
                    hab["objetivos"] = op.Celdas.Select(x => (object?)DuelosTablero.Coord(x)).ToList();
                    hab["cartas"] = op.Cartas.ToDictionary(
                        kv => kv.Key.ToString(),
                        kv => (object?)kv.Value.Select(x => (object?)DuelosTablero.Coord(x)).ToList());
                    hab["libres"] = op.Libres.Select(x => (object?)DuelosTablero.Coord(x)).ToList();
                    hab["numCeldas"] = (long)op.NumCeldas;
                }
                carta["habilidad"] = hab;
            }
            cartas.Add(carta);
        }

        // Avisos de la última resolución: los de jugadas no válidas, bloqueos y
        // tope solo al jugador al que afectan (revelarían sus intenciones).
        var privados = new HashSet<string> { "invalida", "bloqueo", "tope", "huida" };
        var eventos = e.Eventos
            .Where(v => !privados.Contains(v.Tipo) || v.Jugador == yo)
            .Select(v => (object?)new Dictionary<string, object?>
            {
                ["tipo"] = v.Tipo,
                ["propio"] = v.Jugador == yo,
                ["rival"] = v.Jugador == rival,
                ["celda"] = v.Celda >= 0 ? DuelosTablero.Coord(v.Celda) : "",
                ["texto"] = v.Texto,
            }).ToList();

        string resultado = e.Ganador switch
        {
            -1 => "",
            2 => "tablas",
            _ => e.Ganador == yo ? "victoria" : "derrota",
        };

        return new Dictionary<string, object?>
        {
            ["turno"] = (long)e.Turno,
            ["turnoLimite"] = (long)DuelosAjustes.TurnoLimite,
            ["filas"] = (long)DuelosAjustes.Filas,
            ["columnas"] = (long)DuelosAjustes.Columnas,
            ["yo"] = (long)yo,
            ["resultado"] = resultado,
            ["motivo"] = e.Motivo,
            ["bloqueadas"] = bloqueadas,
            ["hundidas"] = hundidas,
            ["hundimientos"] = e.Hundimientos.Select(c => (object?)DuelosTablero.Coord(c)).ToList(),
            ["muros"] = muros,
            ["escudos"] = escudos,
            ["jugadores"] = jugadores,
            ["cartas"] = cartas,
            ["eventos"] = eventos,
        };
    }

    /// Casillas a las que puede ir [c] este turno: `alcanzables` (sin nada
    /// más) y, si este mismo turno se puede activar algo que da movimiento
    /// (Más movimiento de un general en su casilla o la Movilización del
    /// ejército, que se aplican ANTES de mover), `alcanzablesExtra` con cada
    /// caso: { "masMovimiento", "movilizacion", "ambos" } y `masMovimientoDe`
    /// (general que da el +movimiento).
    private static void AnadirAlcanzables(DuelosEstado e, DuelosCarta c, int yo, Dictionary<string, object?> carta)
    {
        int t = e.Turno;
        List<object?> Lista(bool[] alc) => Enumerable.Range(0, DuelosAjustes.Celdas)
            .Where(i => alc[i] && i != c.Pos)
            .Select(i => (object?)DuelosTablero.Coord(i)).ToList();

        if (c.ConfusaTurno == t || c.ParalizadaTurno == t)
        {
            carta["alcanzables"] = new List<object?>();
            return;
        }
        carta["alcanzables"] = Lista(DuelosMotor.Alcanzables(e, c));

        var jp = e.Jugadores[yo];
        // General propio en su casilla con Más movimiento listo (si ya lo
        // tiene activo, el alcance normal ya lo incluye).
        var dador = c.MasMovHasta >= t ? null : e.Cartas.FirstOrDefault(g =>
            g.Viva && !g.EsClon && g.Dueno == yo && g.Pos == c.Pos
            && g.Def.Habilidad == DuelosHabilidad.MasMovimiento && DuelosMotor.HabilidadLista(e, g));
        bool movil = jp.HabilidadEjercito == DuelosHabilidadEjercito.Movilizacion
                     && jp.MovilizacionHasta < t && DuelosMotor.HabilidadEjercitoLista(e, yo);
        if (dador == null && !movil) return;

        int masMovPrevio = c.MasMovHasta, movilPrevio = jp.MovilizacionHasta;
        var extra = new Dictionary<string, object?>();
        try
        {
            if (dador != null)
            {
                c.MasMovHasta = t;
                extra["masMovimiento"] = Lista(DuelosMotor.Alcanzables(e, c));
                c.MasMovHasta = masMovPrevio;
                carta["masMovimientoDe"] = (long)dador.Id;
            }
            if (movil)
            {
                jp.MovilizacionHasta = t;
                extra["movilizacion"] = Lista(DuelosMotor.Alcanzables(e, c));
                if (dador != null)
                {
                    c.MasMovHasta = t;
                    extra["ambos"] = Lista(DuelosMotor.Alcanzables(e, c));
                }
            }
        }
        finally
        {
            c.MasMovHasta = masMovPrevio;
            jp.MovilizacionHasta = movilPrevio;
        }
        carta["alcanzablesExtra"] = extra;
    }

    // ═════════════════════════════════════════════════════════════════════════
    // JUGADA DEL CLIENTE
    // ═════════════════════════════════════════════════════════════════════════

    /// Convierte la jugada que manda el cliente:
    ///   { movimientos: { "<cartaId>": "C4" }, habilidades: [ { generalId,
    ///     celdas: ["C4"], cartaId } ], disparos: ["C4"],
    ///     usarHabilidadEjercito, habilidadEjercitoCartaId }
    public static DuelosJugada JugadaDesdeMapa(object? raw)
    {
        var d = Dic(raw);
        var j = new DuelosJugada
        {
            UsarHabilidadEjercito = Bool(d, "usarHabilidadEjercito"),
            HabilidadEjercitoCartaId = Int(d, "habilidadEjercitoCartaId", -1),
        };
        foreach (var kv in Dic(Get(d, "movimientos")))
        {
            if (!int.TryParse(kv.Key, out var id)) continue;
            int celda = DuelosTablero.Parse(kv.Value?.ToString() ?? "");
            if (celda >= 0) j.Movimientos[id] = celda;
        }
        foreach (var x in Lst(d, "habilidades"))
        {
            var m = Dic(x);
            j.Habilidades.Add(new DuelosUsoHabilidad
            {
                GeneralId = Int(m, "generalId", -1),
                CartaId = Int(m, "cartaId", -1),
                Celdas = Lst(m, "celdas").Select(c => DuelosTablero.Parse(c?.ToString() ?? "")).Where(c => c >= 0).ToArray(),
            });
        }
        foreach (var c in Lst(d, "disparos"))
        {
            int i = DuelosTablero.Parse(c?.ToString() ?? "");
            if (i >= 0) j.Disparos.Add(i);
        }
        return j;
    }

    // ═════════════════════════════════════════════════════════════════════════
    // AYUDAS DE LECTURA (tolerantes con los tipos que devuelve Firestore)
    // ═════════════════════════════════════════════════════════════════════════

    private static object? Get(Dictionary<string, object?> d, string k) => d.TryGetValue(k, out var v) ? v : null;

    private static Dictionary<string, object?> Dic(object? o)
    {
        if (o is Dictionary<string, object?> d) return d;
        var r = new Dictionary<string, object?>();
        if (o is System.Collections.IDictionary id)
            foreach (System.Collections.DictionaryEntry kv in id) r[kv.Key.ToString() ?? ""] = kv.Value;
        return r;
    }

    private static List<object?> Lst(Dictionary<string, object?> d, string k)
    {
        var o = Get(d, k);
        if (o is string || o is not System.Collections.IEnumerable en) return new List<object?>();
        var l = new List<object?>();
        foreach (var x in en) l.Add(x);
        return l;
    }

    private static int ToInt(object? o) => o switch
    {
        null => 0,
        int i => i,
        long l => (int)l,
        double db => (int)db,
        string s when int.TryParse(s, out var p) => p,
        _ => Convert.ToInt32(o),
    };

    private static int Int(Dictionary<string, object?> d, string k, int def = 0) =>
        d.TryGetValue(k, out var v) && v != null ? ToInt(v) : def;

    private static string Str(Dictionary<string, object?> d, string k) =>
        d.TryGetValue(k, out var v) && v != null ? v.ToString() ?? "" : "";

    private static bool Bool(Dictionary<string, object?> d, string k, bool def = false) =>
        d.TryGetValue(k, out var v) && v is bool b ? b : def;
}
