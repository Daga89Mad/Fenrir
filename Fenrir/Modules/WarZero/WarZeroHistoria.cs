using Google.Cloud.Firestore;

// ─────────────────────────────────────────────────────────────────────────────
// WarZeroHistoria.cs  (Opción B · fases 2-4)
//
// Modo historia: creación de la partida (fase 2), cierre del bot y desbloqueo
// (fase 3) y la IA que mueve al bot (fase 4). A diferencia de una partida normal
// (que nace en `esperando`, se rellena de bots y reparte manos), una batalla de
// historia se SIEMBRA aquí ya EN CURSO y completamente montada a partir de su
// `HistoriaDef` (ver HistoriaCatalogo.cs):
//
//   • 2 participantes: el jugador humano y un BOT de historia (uid sintético,
//     no está en la colección `Bots`, así que el orquestador NO lo toca; lo
//     moverá la IA ligera de la fase 4).
//   • Mapa de la historia (p. ej. `diente_invierno`), cuarteles fijos.
//   • Cartas de ambos bandos YA colocadas y apiladas en su cuartel. No hay mano
//     ni robo de fin de turno (statsPartida.{uid}.mano = [] para que EntrarAsync
//     no reparta nada).
//   • Energía inicial por bando (40 por defecto).
//   • Config de historia (turnos de supervivencia, suerte del perdedor, objetivos)
//     bajo el campo `historia`, y `esHistoria = true` para filtrar rápido.
//
// El documento se guarda en Partidas/{docId} con docId determinista
// `hist_{uid}_{historiaId}`, de modo que reintentar (tras perder) SOBRESCRIBE la
// partida con un tablero fresco.
//
// El no-reparto y la "suerte del perdedor" (+3) los cubre ya la resolución normal
// del turno (statsPartida con mano/mazo vacíos + regla existente). Aquí viven,
// además: el cierre del bot en el mismo turno que el jugador
// (ConstruirJugadaBotHistoria) y el desbloqueo al ganar la última parte
// (DesbloquearHistoriaSiProcedeAsync). La victoria por supervivencia y el
// bloqueo de recompensas PvP se enganchan en WarZeroService.cs.
// ─────────────────────────────────────────────────────────────────────────────

public partial class WarZeroService
{
    /// Uid sintético del bot de historia. No existe en `Bots` ni en `Jugadores`:
    /// es un participante local de la partida, controlado por la IA de historia.
    public const string HistoriaBotUid = "historia_bot";

    // Zonas cosméticas (color/HUD) de cada bando dentro de la batalla.
    private const string ZonaHistoriaJugador = "south";
    private const string ZonaHistoriaBot = "north";

    /// Crea (o reinicia) la partida de una batalla de historia para [uid] y
    /// devuelve su id y estado completo, listo para que el cliente entre.
    public async Task<CrearHistoriaResponse> CrearPartidaHistoriaAsync(CrearHistoriaRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Uid) || string.IsNullOrWhiteSpace(req.HistoriaId))
            return new CrearHistoriaResponse { Ok = false, Error = "uid e historiaId son obligatorios" };

        var def = HistoriaCatalogo.Get(req.HistoriaId);
        if (def == null)
            return new CrearHistoriaResponse { Ok = false, Error = $"historia desconocida: {req.HistoriaId}" };

        var db = _fs.Db;

        // ── Alias del jugador (para la entrada de `jugadores`) ────────────────
        var aliasJugador = "Jugador";
        try
        {
            var jugSnap = await db.Collection("Jugadores").Document(req.Uid).GetSnapshotAsync();
            if (jugSnap.Exists)
            {
                var a = M.Str(M.Get(M.Map(M.FromFs(jugSnap.ToDictionary())), "alias"));
                if (a != "") aliasJugador = a;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("[WZ.Historia] leer alias falló: " + ex);
        }

        // ── Mapa (una sola lectura): cuarteles + terreno + dimensiones ───────
        var mapa = await LeerMapaAsync(def.MapaId);
        var (cuartelJugador, cuartelBot) = ResolverCuarteles(def, mapa.obeliscos);
        if (cuartelJugador == "" || cuartelBot == "" || cuartelJugador == cuartelBot)
            return new CrearHistoriaResponse
            {
                Ok = false,
                Error = $"no se pudieron asignar cuarteles distintos en el mapa {def.MapaId}",
            };

        // ── Catálogo de cartas (cacheado) para sembrar el tablero ────────────
        var catalogo = await ObtenerCatalogoCartasAsync();

        var tablero = new Dictionary<string, object?>();
        SembrarBando(tablero, cuartelJugador, req.Uid, ZonaHistoriaJugador, def.Jugador, catalogo);
        SembrarBando(tablero, cuartelBot, HistoriaBotUid, ZonaHistoriaBot, def.Bot, catalogo);

        // ── Jugadores (humano + bot de historia), ambos ya "listos" ──────────
        var jugadores = new List<object?>
        {
            new Dictionary<string, object?>
            {
                ["uid"] = req.Uid,
                ["alias"] = aliasJugador,
                ["ejercitoId"] = (long)def.Jugador.Ejercito,
                ["listo"] = true,
            },
            new Dictionary<string, object?>
            {
                ["uid"] = HistoriaBotUid,
                ["alias"] = NombreEjercito(def.Bot.Ejercito),
                ["ejercitoId"] = (long)def.Bot.Ejercito,
                ["listo"] = true,
            },
        };

        // ── statsPartida: energías + mano/mazo VACÍOS (bloquea el reparto) ───
        Dictionary<string, object?> Stats(int energia) => new()
        {
            ["energies"] = (long)energia,
            ["pc"] = 0L,
            ["victorias"] = 0L,
            ["derrotas"] = 0L,
            // Claves presentes y vacías → EntrarAsync no reparte mano ni mazo.
            ["mano"] = new List<object?>(),
            ["mazoRestante"] = new List<object?>(),
            ["mazoPool"] = new List<object?>(),
        };

        var statsPartida = new Dictionary<string, object?>
        {
            [req.Uid] = Stats(def.Jugador.EnergiaInicial),
            [HistoriaBotUid] = Stats(def.Bot.EnergiaInicial),
        };

        var obeliscos = new Dictionary<string, object?>
        {
            [req.Uid] = cuartelJugador,
            [HistoriaBotUid] = cuartelBot,
        };

        // ── Config de historia (la consumen las fases 3/4 y el cliente) ──────
        var historia = new Dictionary<string, object?>
        {
            ["id"] = def.Id,
            ["ejercitoCampana"] = (long)def.EjercitoCampana,
            ["orden"] = (long)def.Orden,
            ["parte"] = (long)def.Parte,
            ["partes"] = (long)def.Partes,
            ["siguienteId"] = def.SiguienteId,
            ["esUltimaParte"] = def.EsUltimaParte,
            ["titulo"] = def.Titulo,
            ["mapaId"] = def.MapaId,
            ["turnosSupervivencia"] = (long)def.TurnosSupervivencia,
            ["suerteDelPerdedor"] = (long)def.SuerteDelPerdedor,
            // Roles/objetivos por uid.
            ["jugadorUid"] = req.Uid,
            ["botUid"] = HistoriaBotUid,
            ["jugadorObjetivo"] = ObjetivoStr(def.Jugador.Objetivo),
            ["botObjetivo"] = ObjetivoStr(def.Bot.Objetivo),
            // Perfil de la IA (fase 4).
            ["botDificultad"] = def.BotDificultad,
            ["botEstilo"] = def.BotEstilo,
            // Historia (colección `Historias`) a desbloquear al ganar la última parte.
            ["desbloqueaId"] = def.DesbloqueaHistoriaId,
            // Parte 1 de la historia (para reiniciar tras perder). Si no se define,
            // esta misma batalla es la parte 1.
            ["primeraParteId"] = def.PrimeraParteId ?? def.Id,
            // Mapa cacheado para la IA del bot (fase 4): mueve sin releer Firestore.
            ["mapa"] = new Dictionary<string, object?>
            {
                ["filas"] = (long)mapa.filas,
                ["columnas"] = (long)mapa.columnas,
                ["terreno"] = mapa.terreno.ToDictionary(kv => kv.Key, kv => (object?)kv.Value),
            },
        };

        // ── Documento completo de la partida (nace EN CURSO) ─────────────────
        var doc = new Dictionary<string, object>
        {
            ["nombre"] = def.Titulo,
            ["hostUid"] = req.Uid,
            ["esPrivada"] = true,        // fuera de listados públicos / rellenar-bots
            ["contrasena"] = "",
            ["maxJugadores"] = 2L,
            ["jugadores"] = jugadores,
            ["participantes"] = new List<object?> { req.Uid }, // el bot no cuenta
            ["estado"] = "en_curso",
            ["creadoEn"] = Timestamp.FromDateTime(DateTime.UtcNow),
            ["modoTurno"] = "rapida",
            ["turnoActual"] = 1L,
            ["cerradoPor"] = new List<object?>(),
            ["jugadoresEliminados"] = new List<object?>(),
            ["statsPartida"] = statsPartida,
            ["obeliscos"] = obeliscos,
            ["tablero"] = tablero,
            ["ultimoCombateLog"] = new List<object?>(),
            ["mapaId"] = def.MapaId,
            // Marcas de modo historia.
            ["esHistoria"] = true,
            ["historia"] = historia,
        };

        var docId = $"hist_{req.Uid}_{def.Id}";
        var lobbyRef = db.Collection("Partidas").Document(docId);

        try
        {
            // SetAsync (sin merge) SOBRESCRIBE: reintentar tras perder deja un
            // tablero fresco sin arrastrar el estado de la partida anterior.
            await lobbyRef.SetAsync(doc);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("[WZ.Historia] crear partida falló: " + ex);
            return new CrearHistoriaResponse { Ok = false, Error = "no se pudo crear la partida" };
        }

        var resp = new CrearHistoriaResponse { Ok = true, LobbyId = docId };
        try { resp.Estado = await LeerEstadoAsync(docId); }
        catch (Exception ex)
        {
            Console.Error.WriteLine("[WZ.Historia] LeerEstado tras crear falló: " + ex);
        }
        return resp;
    }

    // ── Siembra las cartas de un bando, apiladas en su cuartel ───────────────
    private static void SembrarBando(
        Dictionary<string, object?> tablero,
        string cuartel,
        string ownerUid,
        string ownerZone,
        BandoHistoria bando,
        Dictionary<string, Dictionary<string, object?>> catalogo)
    {
        var pila = tablero.TryGetValue(cuartel, out var lst)
            ? M.List(lst)
            : new List<object?>();

        foreach (var c in bando.Cartas)
        {
            if (!catalogo.TryGetValue(c.CartaId, out var cd))
            {
                Console.Error.WriteLine($"[WZ.Historia] carta desconocida {c.CartaId}, se omite");
                continue;
            }
            var cant = Math.Max(1, c.Cantidad);
            for (int q = 0; q < cant; q++)
                pila.Add(ClonarCartaParaTablero(cd, c.CartaId, ownerUid, ownerZone));
        }

        if (pila.Count > 0) tablero[cuartel] = pila;
    }

    // ── Clona una carta del catálogo para colocarla en el tablero ────────────
    // Copia todos los campos del catálogo (Nombre, Fuerza, Defensa, Coste,
    // Condicion, Tipo, Movimiento, Imagen, habilidades…) e inyecta id + owner.
    private static Dictionary<string, object?> ClonarCartaParaTablero(
        Dictionary<string, object?> cd, string cartaId, string ownerUid, string ownerZone)
    {
        var carta = new Dictionary<string, object?>(cd)
        {
            ["id"] = cartaId,
            ["ownerUid"] = ownerUid,
            ["ownerZone"] = ownerZone,
        };
        return carta;
    }

    // ── Resuelve la EVOLUCIÓN de una carta dentro del catálogo ───────────────
    // Devuelve el id de la carta a la que evoluciona `cartaId` (campo
    // `IdEvolucion` del catálogo `Cartas`), o "" si esa carta no evoluciona o si
    // la evolución referenciada no existe en el catálogo. Lo usan las oleadas
    // scriptadas para hacer nacer refuerzos YA evolucionados (ver
    // `GrupoOleada.CantidadEvolucionada` en HistoriaGuionOleadas.cs) y las
    // evoluciones de tablero (`EvolucionEnTurno`).
    private static string IdEvolucionDe(
        Dictionary<string, Dictionary<string, object?>> catalogo, string cartaId)
    {
        if (!catalogo.TryGetValue(cartaId, out var cd)) return "";
        var idEvo = M.Str(M.Get(cd, "IdEvolucion", "idEvolucion"));
        return idEvo != "" && catalogo.ContainsKey(idEvo) ? idEvo : "";
    }

    // ── Mapa: SIEMPRE desde la colección `Mapas` de Firebase ─────────────────
    // Una sola lectura que devuelve: coords de cuartel (campo `obeliscos` o, si
    // no existe, claves de `continentes`), dimensiones de la rejilla y el mapa de
    // terreno. Si el mapa no declara `filas`/`columnas`, se derivan del mayor
    // índice de fila/columna presente en el terreno y los obeliscos.
    private async Task<(List<string> obeliscos, int filas, int columnas, Dictionary<string, string> terreno)>
        LeerMapaAsync(string mapaId)
    {
        var obeliscos = new List<string>();
        int filas = 0, columnas = 0;
        var terreno = new Dictionary<string, string>();
        try
        {
            var snap = await _fs.Db.Collection("Mapas").Document(mapaId).GetSnapshotAsync();
            if (snap.Exists)
            {
                var d = M.Map(M.FromFs(snap.ToDictionary()));
                obeliscos = M.List(M.Get(d, "obeliscos")).Select(M.Str).Where(s => s != "").ToList();
                if (obeliscos.Count == 0)
                    obeliscos = M.Map(M.Get(d, "continentes")).Keys.Where(k => k != "").ToList();
                filas = M.Int(M.Get(d, "filas"));
                columnas = M.Int(M.Get(d, "columnas"));
                foreach (var kv in M.Map(M.Get(d, "terreno")))
                    terreno[kv.Key] = M.Str(kv.Value);
            }
            else
            {
                Console.Error.WriteLine($"[WZ.Historia] mapa {mapaId} no existe en Firebase");
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("[WZ.Historia] leer mapa falló: " + ex);
        }

        // Derivar dimensiones si el mapa no las declara (mapas antiguos).
        if (filas <= 0 || columnas <= 0)
        {
            int maxR = 0, maxC = 0;
            foreach (var coord in terreno.Keys.Concat(obeliscos))
            {
                var p = ParseCoord(coord);
                if (p == null) continue;
                if (p.Value.r + 1 > maxR) maxR = p.Value.r + 1;
                if (p.Value.c > maxC) maxC = p.Value.c;
            }
            if (filas <= 0) filas = maxR > 0 ? maxR : 6;
            if (columnas <= 0) columnas = maxC > 0 ? maxC : 10;
        }

        return (obeliscos, filas, columnas, terreno);
    }

    // Asigna los dos cuarteles de forma determinista a partir de las coords del
    // mapa. El HistoriaDef puede fijar una coord concreta como override opcional.
    private static (string jugador, string bot) ResolverCuarteles(HistoriaDef def, List<string> obeliscos)
    {
        var candidatos = (obeliscos.Count > 0 ? obeliscos : Coords.ObeliscosFallback(2))
            .Distinct().OrderBy(c => c, StringComparer.Ordinal).ToList();

        // Jugador = primer cuartel del mapa; Bot = último distinto del jugador.
        var jugador = def.Jugador.Cuartel ?? (candidatos.Count > 0 ? candidatos[0] : "");
        var bot = def.Bot.Cuartel
                  ?? candidatos.LastOrDefault(c => c != jugador)
                  ?? "";
        return (jugador, bot);
    }

    // "B5" → (fila 0-based, columna 1-based). null si no es una coord válida.
    private static (int r, int c)? ParseCoord(string coord)
    {
        if (string.IsNullOrEmpty(coord) || coord.Length < 2) return null;
        int r = char.ToUpperInvariant(coord[0]) - 'A';
        if (r < 0 || !int.TryParse(coord[1..], out int c)) return null;
        return (r, c);
    }

    // ── IA del bot de historia (cierre B2) ───────────────────────────────────
    // Cada carta del bot AVANZA `Movimiento` pasos hacia el cuartel del jugador,
    // respetando terreno y tipo de carta (TerrenoUtil, la misma primitiva que usa
    // el bot real). Las cartas que ya están sobre el cuartel se quedan (siguen
    // combatiendo cada turno). Al re-emitir TODAS sus cartas (movidas o no) se
    // garantiza que persisten: el tablero se reconstruye cada turno a partir de
    // los cierres. El co-emplazamiento con las defensas del jugador dispara el
    // combate/asalto al cuartel en ResolverTurnoCoreEnTx (no hay que "declarar"
    // ataque).
    //
    // Es un avance frontal (perfil "agresivo"): suficiente para el asedio. La
    // dificultad/estilo (`historia.botDificultad`/`botEstilo`) quedan disponibles
    // para modular el avance en el futuro (agrupar, esperar, replegar…).
    //
    // GUION POR OLEADAS (opcional, HistoriaGuionOleadas.cs): algunas historias
    // (p. ej. "demonios_1" · Diente de Invierno) definen un `GuionOleadas` que,
    // en turnos concretos, hace aparecer NUEVOS refuerzos (clones frescos del
    // catálogo `Cartas`) en coordenadas de salida fijas, simulando asaltos
    // coordinados en varios frentes en vez de un único avance homogéneo desde
    // el cuartel. Esos refuerzos se SUMAN a lo que ya hay en el tablero: las
    // oleadas anteriores que sobrevivieron siguen avanzando por la lógica
    // genérica de abajo, sin reiniciarse. Un grupo puede además declarar cuántas
    // de sus copias nacen YA EVOLUCIONADAS (`GrupoOleada.CantidadEvolucionada`):
    // en ese caso se clona la carta de `IdEvolucion` en lugar de la base, de
    // forma que el refuerzo entra con las stats de la evolución y no pierde un
    // turno evolucionando. Si la historia no tiene guion, o si
    // `catalogoCartas` no se pasa (null/vacío), el comportamiento es EXACTAMENTE
    // el de siempre: avance frontal puro, sin oleadas — cero riesgo de romper
    // el resto de historias del catálogo.
    //
    // El guion puede además declarar, por turno:
    //   • EVOLUCIONES DE TABLERO (`EvolucionEnTurno`): N copias de una carta que
    //     el bot YA tiene desplegadas suben a su `IdEvolucion`. Se eligen las más
    //     adelantadas (menor distancia al cuartel objetivo) y, salvo que se pida
    //     lo contrario, gastan el turno evolucionando: no se mueven.
    //   • RUTA (`RutaBot`): celdas VETADAS donde ninguna carta del bot puede
    //     terminar el turno (el avance las esquiva y, si alguna venía ya dentro,
    //     se la desplaza fuera) y PUNTOS DE PASO por los que hay que pasar antes
    //     de ir a por el cuartel (embudo de asalto).
    // Ambas cosas son de la HISTORIA y del TURNO que las declara: sin guion (o
    // en un turno sin ruta/evolución) esta función hace exactamente lo mismo que
    // antes, y ninguna primitiva compartida con el juego normal cambia de
    // comportamiento — el avance con veto es local a este fichero.
    internal static Dictionary<string, object?> ConstruirJugadaBotHistoria(
        Dictionary<string, object?> data, string botUid, int turno,
        Dictionary<string, Dictionary<string, object?>>? catalogoCartas = null)
    {
        var hist = M.Map(M.Get(data, "historia"));
        var jugadorUid = M.Str(M.Get(hist, "jugadorUid"));
        var historiaId = M.Str(M.Get(hist, "id"));

        // Objetivo del asedio: el cuartel del jugador. Si ya no existe
        // (conquistado), el bot se queda quieto (la partida ya habrá terminado).
        var objetivo = M.Str(M.Get(M.Map(M.Get(data, "obeliscos")), jugadorUid));

        // Terreno + dimensiones cacheados en la creación (sin releer Firestore).
        var mapaH = M.Map(M.Get(hist, "mapa"));
        int filas = M.Int(M.Get(mapaH, "filas"));
        int columnas = M.Int(M.Get(mapaH, "columnas"));
        var terreno = new Dictionary<string, string>();
        foreach (var kv in M.Map(M.Get(mapaH, "terreno")))
            terreno[kv.Key] = M.Str(kv.Value);

        var celdas = new Dictionary<string, object?>();
        void Colocar(string coord, object? carta)
        {
            if (celdas.TryGetValue(coord, out var lst) && lst is List<object?> l)
                l.Add(carta);
            else
                celdas[coord] = new List<object?> { carta };
        }

        // ── 0) Guion de esta historia: ruta y evoluciones de ESTE turno ───────
        var guion = HistoriaGuiones.Get(historiaId);
        var ruta = guion?.RutaEnTurno(turno);

        var vetadas = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in ruta?.CeldasVetadas ?? (IReadOnlyList<string>)Array.Empty<string>())
            if (!string.IsNullOrWhiteSpace(c)) vetadas.Add(c.Trim().ToUpperInvariant());

        var puntosDePaso = (ruta?.PuntosDePaso ?? (IReadOnlyList<string>)Array.Empty<string>())
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p.Trim().ToUpperInvariant())
            .ToList();

        // ── 1) Inventario de lo que el bot tiene en el tablero ───────────────
        // Se recoge primero (en vez de mover sobre la marcha) porque las
        // evoluciones de tablero necesitan comparar TODAS las copias entre sí
        // para quedarse con las más adelantadas.
        var unidades = new List<(string coord, Dictionary<string, object?> carta)>();
        foreach (var kv in M.Map(M.Get(data, "tablero")))
        {
            var coordActual = kv.Key;
            foreach (var raw in M.List(kv.Value))
            {
                var carta = M.Map(raw);
                if (M.Str(M.Get(carta, "ownerUid")) != botUid) continue;
                unidades.Add((coordActual, carta));
            }
        }

        // ── 2) Evoluciones de tablero de ESTE turno ──────────────────────────
        // Mapa índice-de-unidad → evolución a aplicar. Se eligen las copias más
        // cercanas al cuartel objetivo (vanguardia) y se desempata por coord,
        // así que el resultado es estable entre ejecuciones.
        var evoPorIndice = new Dictionary<int, (string idEvo, Dictionary<string, object?> cd, bool avanza)>();
        var evoluciones = guion?.EvolucionesEnTurno(turno);
        if (evoluciones != null && evoluciones.Count > 0)
        {
            if (catalogoCartas == null || catalogoCartas.Count == 0)
            {
                Console.Error.WriteLine(
                    $"[WZ.Historia] guion {historiaId} turno {turno}: sin catálogo de cartas, no se aplican evoluciones de tablero");
            }
            else
            {
                foreach (var ev in evoluciones)
                {
                    var idEvo = IdEvolucionDe(catalogoCartas, ev.CartaId);
                    if (idEvo == "")
                    {
                        Console.Error.WriteLine(
                            $"[WZ.Historia] guion {historiaId} turno {turno}: {ev.CartaId} no tiene evolución en el catálogo, no evoluciona");
                        continue;
                    }
                    var cdEvo = catalogoCartas[idEvo];
                    int cantidad = Math.Max(0, ev.Cantidad);

                    var elegidas = Enumerable.Range(0, unidades.Count)
                        .Where(i => !evoPorIndice.ContainsKey(i))
                        .Where(i => M.Str(M.Get(unidades[i].carta, "id")) == ev.CartaId)
                        .OrderBy(i => DistanciaCoord(unidades[i].coord, objetivo))
                        .ThenBy(i => unidades[i].coord, StringComparer.Ordinal)
                        .Take(cantidad)
                        .ToList();

                    if (elegidas.Count < cantidad)
                        Console.Error.WriteLine(
                            $"[WZ.Historia] guion {historiaId} turno {turno}: solo hay {elegidas.Count} de {cantidad} copias de {ev.CartaId} en el tablero para evolucionar");

                    foreach (var i in elegidas)
                        evoPorIndice[i] = (idEvo, cdEvo, ev.AvanzaTrasEvolucionar);
                }
            }
        }

        // ── 3) Avance genérico de TODO lo que el bot ya tiene en el tablero ───
        // (unidades nacidas en la siembra inicial + refuerzos de oleadas
        // anteriores que ya se comprometieron en turnos previos).
        for (int i = 0; i < unidades.Count; i++)
        {
            var (coordActual, carta) = unidades[i];
            var cartaFinal = carta;
            bool avanza = true;

            // ¿Esta copia evoluciona este turno? Se sustituye por un clon de la
            // carta evolucionada (entra con las stats de la evolución) y, por
            // defecto, gasta el turno evolucionando: no se mueve.
            if (evoPorIndice.TryGetValue(i, out var evo))
            {
                cartaFinal = ClonarCartaParaTablero(evo.cd, evo.idEvo, botUid, ZonaHistoriaBot);
                avanza = evo.avanza;
            }

            int tipo = M.Int(M.Get(cartaFinal, "Tipo", "tipo"));
            var (tierra, mar) = TerrenoUtil.ClaseDeTipo(tipo);

            var destino = coordActual;
            if (avanza && objetivo != "" && objetivo != coordActual && filas > 0 && columnas > 0)
            {
                int mov = Math.Max(1, M.Int(M.Get(cartaFinal, "Movimiento", "movimiento")));
                // Con puntos de paso, la meta inmediata puede ser un waypoint en
                // vez del cuartel (embudo de asalto).
                var meta = MetaConPuntosDePaso(coordActual, objetivo, puntosDePaso);
                destino = PasoHaciaEvitando(
                    coordActual, meta, mov, tierra, mar, terreno, filas, columnas, vetadas);
            }

            // Nadie puede QUEDARSE en una celda vetada: ni la unidad que se
            // quedó clavada por terreno ni la que ya estaba dentro desde el
            // turno anterior o porque acaba de evolucionar.
            if (vetadas.Contains(destino))
                destino = EsquivarCeldaVetada(
                    destino, objetivo, tierra, mar, terreno, filas, columnas, vetadas);

            Colocar(destino, cartaFinal);
        }

        // ── 4) Refuerzos scriptados de esta historia en ESTE turno (si los
        // tiene). Se AÑADEN a lo anterior: no sustituyen ni reinician el avance
        // de las unidades ya desplegadas, solo introducen unidades NUEVAS en
        // sus coordenadas de salida, tal cual "nacen" (sin avanzar todavía este
        // mismo turno; empezarán a avanzar la próxima vez que se les llame,
        // igual que cualquier otra carta del tablero).
        if (catalogoCartas != null && catalogoCartas.Count > 0)
        {
            var oleada = guion?.OleadaEnTurno(turno);
            if (oleada != null)
            {
                foreach (var grupo in oleada.Grupos)
                {
                    if (!catalogoCartas.TryGetValue(grupo.CartaId, out var cd))
                    {
                        Console.Error.WriteLine(
                            $"[WZ.Historia] guion {historiaId} turno {turno}: carta {grupo.CartaId} desconocida, se omite");
                        continue;
                    }
                    var cantidad = Math.Max(1, grupo.Cantidad);

                    // Copias que nacen YA EVOLUCIONADAS (0 = comportamiento de
                    // siempre). Se clona directamente la carta destino de
                    // `IdEvolucion`, así que entran al tablero con las stats de la
                    // evolución y SIN gastar un turno evolucionando: pueden
                    // avanzar en su primer turno de movimiento.
                    int evolucionadas = Math.Clamp(grupo.CantidadEvolucionada, 0, cantidad);
                    var idEvo = evolucionadas > 0 ? IdEvolucionDe(catalogoCartas, grupo.CartaId) : "";
                    if (evolucionadas > 0 && idEvo == "")
                    {
                        Console.Error.WriteLine(
                            $"[WZ.Historia] guion {historiaId} turno {turno}: {grupo.CartaId} no tiene evolución en el catálogo, sale sin evolucionar");
                        evolucionadas = 0;
                    }
                    var cdEvo = idEvo != "" ? catalogoCartas[idEvo] : null;

                    for (int q = 0; q < cantidad; q++)
                    {
                        bool evo = q < evolucionadas;
                        var cdUso = evo ? cdEvo! : cd;
                        var idUso = evo ? idEvo : grupo.CartaId;

                        // Una oleada nunca puede sacar refuerzos en una celda
                        // vetada de este turno: se desvían a la adyacente
                        // compatible más cercana al objetivo.
                        var coordSalida = grupo.Coordenada;
                        if (vetadas.Contains(coordSalida))
                        {
                            var (t, m) = TerrenoUtil.ClaseDeTipo(M.Int(M.Get(cdUso, "Tipo", "tipo")));
                            var alternativa = EsquivarCeldaVetada(
                                coordSalida, objetivo, t, m, terreno, filas, columnas, vetadas);
                            Console.Error.WriteLine(
                                $"[WZ.Historia] guion {historiaId} turno {turno}: salida {coordSalida} está vetada, {idUso} sale en {alternativa}");
                            coordSalida = alternativa;
                        }

                        Colocar(coordSalida, ClonarCartaParaTablero(
                            cdUso, idUso, botUid, ZonaHistoriaBot));
                    }
                }
            }
        }

        return new Dictionary<string, object?>
        {
            ["uid"] = botUid,
            ["turno"] = turno,
            ["celdas"] = celdas,
            ["timestamp"] = Timestamp.FromDateTime(DateTime.UtcNow),
            ["acciones"] = new List<object?>(),
        };
    }

    // ── Ruta: avance esquivando las celdas vetadas ───────────────────────────
    // Si el turno NO tiene celdas vetadas (el caso normal, y el de cualquier
    // batalla sin ruta en el guion) delega TAL CUAL en TerrenoUtil: el avance es
    // byte a byte el de siempre. Solo cuando hay veto se usa la variante local,
    // que es el mismo algoritmo greedy de TerrenoUtil.PasoHaciaTerreno pero
    // descartando además las celdas prohibidas. Se hace aquí, y no tocando
    // TerrenoUtil, para que el modo historia no pueda alterar el movimiento del
    // juego normal (bots de PvP, repliegues, modelo enemigo…).
    private static string PasoHaciaEvitando(
        string desde, string hacia, int pasos, bool tierra, bool mar,
        Dictionary<string, string> terreno, int filas, int columnas,
        HashSet<string> vetadas)
    {
        if (vetadas.Count == 0)
            return TerrenoUtil.PasoHaciaTerreno(
                desde, hacia, pasos, tierra, mar, terreno, filas, columnas);

        var pa = ParseCoord(desde);
        var pb = ParseCoord(hacia);
        if (pa == null || pb == null) return desde;

        int ri = pa.Value.r, ci = pa.Value.c;      // ci = columna 1-based
        int tri = pb.Value.r, tci = pb.Value.c;

        for (int k = 0; k < pasos; k++)
        {
            int dr = tri - ri, dc = tci - ci;
            if (dr == 0 && dc == 0) break;

            // Mismo criterio que TerrenoUtil: primero el eje de mayor delta.
            var opciones = Math.Abs(dr) >= Math.Abs(dc)
                ? new[] { (ri + Math.Sign(dr), ci), (ri, ci + Math.Sign(dc)) }
                : new[] { (ri, ci + Math.Sign(dc)), (ri + Math.Sign(dr), ci) };

            bool movido = false;
            foreach (var (nr, nc) in opciones)
            {
                int cr = Math.Clamp(nr, 0, Math.Max(0, filas - 1));
                int cc = Math.Clamp(nc, 1, Math.Max(1, columnas));
                if (cr == ri && cc == ci) continue;              // sin movimiento efectivo

                var cand = FormatCoord(cr, cc);
                if (vetadas.Contains(cand)) continue;            // celda prohibida este turno
                if (!TerrenoUtil.Compatible(cand, tierra, mar, terreno)) continue;

                ri = cr; ci = cc; movido = true; break;
            }
            if (!movido) break;   // bloqueado por terreno o por veto: se queda
        }

        return FormatCoord(ri, ci);
    }

    // (fila 0-based, columna 1-based) → "D4".
    private static string FormatCoord(int r, int c) => $"{(char)('A' + r)}{c}";

    // ── Ruta: meta inmediata según los puntos de paso ────────────────────────
    // Devuelve el primer punto de paso PENDIENTE para una unidad que está en
    // `desde`, o el `objetivo` final si ya no queda ninguno. Un punto de paso se
    // considera cumplido si la unidad ya está en él o si ya está MÁS CERCA del
    // objetivo que ese punto (así una unidad adelantada nunca retrocede para
    // "fichar" en el waypoint).
    private static string MetaConPuntosDePaso(
        string desde, string objetivo, IReadOnlyList<string> puntos)
    {
        if (objetivo == "" || puntos == null || puntos.Count == 0) return objetivo;

        int distObjetivo = DistanciaCoord(desde, objetivo);
        foreach (var wp in puntos)
        {
            if (string.IsNullOrEmpty(wp) || wp == desde) continue;   // ya está en él
            int distWp = DistanciaCoord(wp, objetivo);
            if (distWp == int.MaxValue) continue;                    // coord inválida
            if (distObjetivo != int.MaxValue && distObjetivo <= distWp) continue; // ya lo dejó atrás
            return wp;
        }
        return objetivo;
    }

    // ── Ruta: sacar una unidad de una celda vetada ───────────────────────────
    // Busca la celda ADYACENTE (ortogonal) compatible con la carta, dentro de la
    // rejilla y no vetada, que quede más cerca del objetivo. Si no hay ninguna,
    // devuelve la celda original (mejor dejarla ahí que perder la carta).
    private static string EsquivarCeldaVetada(
        string desde, string objetivo, bool tierra, bool mar,
        Dictionary<string, string> terreno, int filas, int columnas,
        HashSet<string> vetadas)
    {
        var p = ParseCoord(desde);
        if (p == null) return desde;

        var mejor = "";
        int mejorDist = int.MaxValue;
        var deltas = new (int dr, int dc)[] { (-1, 0), (1, 0), (0, -1), (0, 1) };

        foreach (var (dr, dc) in deltas)
        {
            int r = p.Value.r + dr;
            int c = p.Value.c + dc;                       // columna 1-based
            if (r < 0 || (filas > 0 && r >= filas)) continue;
            if (c < 1 || (columnas > 0 && c > columnas)) continue;

            var cand = FormatCoord(r, c);
            if (vetadas.Contains(cand)) continue;
            if (!TerrenoUtil.Compatible(cand, tierra, mar, terreno)) continue;

            int d = DistanciaCoord(cand, objetivo);
            if (mejor == "" || d < mejorDist ||
                (d == mejorDist && string.CompareOrdinal(cand, mejor) < 0))
            {
                mejor = cand;
                mejorDist = d;
            }
        }

        if (mejor == "")
            Console.Error.WriteLine(
                $"[WZ.Historia] no hay salida desde la celda vetada {desde}, la carta se queda ahí");

        return mejor != "" ? mejor : desde;
    }

    // Distancia Manhattan entre dos coords ("D3" → fila/columna). int.MaxValue si
    // alguna no es válida (p. ej. objetivo vacío tras conquistar el cuartel).
    private static int DistanciaCoord(string a, string b)
    {
        var pa = ParseCoord(a);
        var pb = ParseCoord(b);
        if (pa == null || pb == null) return int.MaxValue;
        return Math.Abs(pa.Value.r - pb.Value.r) + Math.Abs(pa.Value.c - pb.Value.c);
    }

    // ── Desbloqueo al ganar la última parte ──────────────────────────────────
    // Se llama tras el commit del cierre. Solo desbloquea si el JUGADOR ganó y
    // esta era la ÚLTIMA parte de la historia. En partes intermedias no hace
    // nada (el cliente encadenará a la parte siguiente con `historia.siguienteId`).
    internal async Task DesbloquearHistoriaSiProcedeAsync(
        Dictionary<string, object?> estado, bool finalizada, string? ganadorUid)
    {
        if (!finalizada) return;
        var hist = M.Map(M.Get(estado, "historia"));
        var jugadorUid = M.Str(M.Get(hist, "jugadorUid"));
        if (jugadorUid == "" || ganadorUid != jugadorUid) return;   // no ganó el jugador
        if (!M.Bool(M.Get(hist, "esUltimaParte"))) return;          // aún quedan partes

        // Id de la historia (colección `Historias`) a marcar como desbloqueada.
        // Si el catálogo no define uno, se usa el id de la batalla como fallback.
        var desbloqueaId = M.Str(M.Get(hist, "desbloqueaId"));
        if (desbloqueaId == "") desbloqueaId = M.Str(M.Get(hist, "id"));
        if (desbloqueaId == "") return;

        await DesbloquearHistoriaAsync(jugadorUid, desbloqueaId);
    }

    // ── Utilidades ───────────────────────────────────────────────────────────
    private static string ObjetivoStr(ObjetivoHistoria o) =>
        o == ObjetivoHistoria.Conquistar ? "conquistar" : "sobrevivir";

    private static string NombreEjercito(int ejercitoId) => ejercitoId switch
    {
        1 => "Humanos",
        2 => "Biónicos",
        3 => "Demonios",
        4 => "Nefilim",
        _ => "Enemigo",
    };
}

// ─────────────────────────────────────────────────────────────────────────────
// DTOs de POST /warzero/historia/crear
// ─────────────────────────────────────────────────────────────────────────────

/// Cuerpo de POST /warzero/historia/crear.
public class CrearHistoriaRequest
{
    public string Uid { get; set; } = "";
    public string HistoriaId { get; set; } = "";
}

/// Respuesta de POST /warzero/historia/crear. `LobbyId` es el id de la partida
/// creada (para navegar al juego) y `Estado` su estado completo ya montado.
public class CrearHistoriaResponse
{
    public bool Ok { get; set; }
    public string? LobbyId { get; set; }
    public string? Error { get; set; }
    public Dictionary<string, object?>? Estado { get; set; }
}