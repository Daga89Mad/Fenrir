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
// PARTIDA NORMAL (HistoriaDef.Modo == PartidaNormal, p. ej. demonios_3): la
// siembra es la misma, pero además cada bando recibe mano inicial, mazo
// restante y mazoPool desde su MAZO FIJO de historia (historia.conMano = true),
// y el bot se mueve con la IA real de los bots (ConstruirJugadaBotHistoriaNormal)
// en lugar del avance scriptado del asedio. Las cartas EXCLUSIVAS de historia
// (HistoriaCatalogo.CartasExclusivas) se resuelven con
// ObtenerCatalogoCartasConHistoriaAsync.
//
// COMPORTAMIENTO DEL BOT, GUARNICIÓN Y BOMBARDEO (p. ej. humanos_1):
//   • `historia.botComportamiento` ("avanzar" / "defender" / "cazar") decide
//     cómo mueve el bot sus cartas (ConstruirJugadaBotHistoria, PlanCaza).
//   • Las cartas sembradas con `CartaHistoria.Guarnicion` llevan la marca
//     `guarnicionHistoria` y no salen nunca de su cuartel.
//   • Si la historia tiene un GuionBombardeo (HistoriaBombardeo.cs), cada
//     turno tiene un PLAN de bombardeo publicado en la partida (campo
//     `bombardeo`: disparos por fila, % por celda y casillas desactivadoras).
//     Lo prepara
//     la creación de la partida (turno 1) y la resolución de cada turno para
//     el siguiente (PrepararBombardeoTrasResolver, llamado desde
//     WarZeroService, paso 7b). Al cerrar el turno, el bot lo SORTEA y lanza un
//     DISPARO LEJANO por impacto (ConstruirBombardeo), con la carta de acción
//     exclusiva del guion.
//   • Con `historia.derrotaSinCartas`, el jugador pierde en cuanto se queda sin
//     cartas: lo decide EvaluarFinHistoria, que llama la resolución del turno
//     (WarZeroService, paso 8b).
//
// CAZA CON CARTAS CLAVE, ASALTO GENERAL Y TÚNEL (p. ej. humanos_2):
//   • Las cartas pueden nacer fuera del cuartel (`CartaHistoria.Coord`).
//   • Las cartas CLAVE del jugador (`historia.vipIds`) son la prioridad de los
//     cazadores, y si muere una el jugador pierde (EvaluarFinHistoria).
//   • Cada carta del bot tiene papel (`rolHistoria`: cazador / asalto). Las de
//     asalto esperan en `historia.reunionAsalto`; desde `historia.turnoAsalto`
//     todas se reúnen allí y entran JUNTAS en el cuartel del jugador
//     (PlanAsalto). Los cazadores empiezan a marchar justo a tiempo.
//   • El túnel inundable (HistoriaTuneles.cs) está vetado para el bot y oculta
//     lo que hay dentro; sus reglas de movimiento e inundación las aplica
//     WarZeroHistoriaReglas.cs durante la resolución.
//   • `historia.victoriaSinEnemigos`: el jugador gana al acabar con todo el
//     ejército del bot (y sus oleadas).
//
// VENTANA EXPLICATIVA (todas las historias): `historia.explicacion` lleva las
// secciones de la ventana que el cliente muestra antes de empezar la batalla
// (WarZeroHistoriaExplicacion.cs).
//
// ABANDONO (todas las historias): salir de la batalla la BORRA
// (POST /warzero/historia/abandonar → AbandonarHistoriaAsync). No se puede
// retomar: hay que empezar la historia de nuevo. Al crear una batalla también
// se borran las que el jugador hubiera dejado abiertas (app cerrada a la
// fuerza), para que no queden partidas colgadas.
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

    /// Habilidad con la que se lanza cada impacto del bombardeo (Disparo lejano).
    private const int HabilidadBombardeo = 3;

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
        // Incluye las cartas EXCLUSIVAS de historia (HistoriaCatalogo), que no
        // existen en Firestore pero pueden estar en el tablero o en los mazos.
        var catalogo = await ObtenerCatalogoCartasConHistoriaAsync();

        var tablero = new Dictionary<string, object?>();
        var vipIds = new List<string>();
        var guarnicionJugador = new List<string>();
        SembrarBando(tablero, cuartelJugador, req.Uid, ZonaHistoriaJugador, def.Jugador, catalogo,
            vipIds, guarnicionJugador);
        SembrarBando(tablero, cuartelBot, HistoriaBotUid, ZonaHistoriaBot, def.Bot, catalogo,
            null, null);

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
                ["alias"] = string.IsNullOrWhiteSpace(def.Bot.Alias)
                    ? NombreEjercito(def.Bot.Ejercito)
                    : def.Bot.Alias,
                ["ejercitoId"] = (long)def.Bot.Ejercito,
                ["listo"] = true,
            },
        };

        // ── statsPartida ─────────────────────────────────────────────────────
        // ASEDIO: energías + mano/mazo VACÍOS (claves presentes → EntrarAsync
        // no reparte nada).
        // PARTIDA NORMAL: mano inicial + mazo restante + mazoPool sacados del
        // MAZO FIJO del bando (HistoriaDef). Las claves también existen, así que
        // EntrarAsync no vuelve a repartir desde el mazo personal del jugador.
        var rngReparto = new Random();
        Dictionary<string, object?> Stats(BandoHistoria bando)
        {
            var (mano, resto, pool) = def.EsPartidaNormal
                ? RepartirMazoHistoria(bando, catalogo, rngReparto)
                : (new List<string>(), new List<string>(), new List<string>());
            return new()
            {
                ["energies"] = (long)bando.EnergiaInicial,
                ["pc"] = 0L,
                ["victorias"] = 0L,
                ["derrotas"] = 0L,
                ["mano"] = mano.Cast<object?>().ToList(),
                ["mazoRestante"] = resto.Cast<object?>().ToList(),
                ["mazoPool"] = pool.Cast<object?>().ToList(),
            };
        }

        var statsPartida = new Dictionary<string, object?>
        {
            [req.Uid] = Stats(def.Jugador),
            [HistoriaBotUid] = Stats(def.Bot),
        };

        var obeliscos = new Dictionary<string, object?>
        {
            [req.Uid] = cuartelJugador,
            [HistoriaBotUid] = cuartelBot,
        };

        // Semilla de la partida: todo el azar del bombardeo sale de aquí, así
        // que es estable entre reintentos de la transacción y cambia en cada
        // intento de la batalla.
        var semilla = new Random().Next();

        // ── Config de historia (la consumen las fases 3/4 y el cliente) ──────
        var historia = new Dictionary<string, object?>
        {
            ["id"] = def.Id,
            ["semilla"] = (long)semilla,
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
            // Derrota del jugador al quedarse sin cartas (EvaluarFinHistoria).
            ["derrotaSinCartas"] = def.DerrotaJugadorSinCartas,
            // Cartas CLAVE del jugador (instanceId): si muere una, pierde.
            ["vipIds"] = vipIds.Cast<object?>().ToList(),
            // Guarnición del JUGADOR (instanceId): no se mueve nunca. Se guarda
            // por instanceId porque el cliente no reenvía marcas desconocidas
            // en sus cartas.
            ["guarnicionJugador"] = guarnicionJugador.Cast<object?>().ToList(),
            // Victoria por aniquilación: sin cartas del bot y sin oleadas
            // pendientes (la última llega en `ultimaOleada`).
            ["victoriaSinEnemigos"] = def.VictoriaSinEnemigos,
            ["ultimaOleada"] = (long)(HistoriaGuiones.Get(def.Id)?.UltimoTurnoOleada ?? 0),
            // Asalto general del bot y su punto de reunión (PlanAsalto).
            ["turnoAsalto"] = (long)def.TurnoAsaltoGeneral,
            ["reunionAsalto"] = def.ReunionAsalto ?? "",
            ["esperaMaxReunion"] = (long)Math.Max(0, def.EsperaMaxReunion),
            // Los cazadores suponen que tus grupos huyen hacia tu cuartel.
            ["presasHuyenACasa"] = def.PresasHuyenACasa,
            // Cuartel del bot solo nominal: el jugador no puede entrar.
            ["cuartelBotInaccesible"] = def.CuartelBotInaccesible,
            // Duelo de generales (HistoriaDuelo.cs): casillas bloqueadas para
            // todos y turno límite (si el jefe sigue vivo al cerrarlo, gana el bot).
            ["celdasBloqueadas"] = (HistoriaDuelos.Get(def.Id)?.CeldasBloqueadas ?? Array.Empty<string>())
                .Select(c => (object?)c.Trim().ToUpperInvariant()).ToList(),
            ["turnoLimite"] = (long)(HistoriaDuelos.Get(def.Id)?.TurnoLimite ?? 0),
            // Cómo mueve el bot sus cartas en el asedio: avanzar | defender | cazar.
            ["botComportamiento"] = def.ComportamientoBotEfectivo.ToString().ToLowerInvariant(),
            // Informativo para el cliente: esta batalla tiene bombardeo
            // (HistoriaBombardeo.cs). El patrón lo aplica el servidor.
            ["bombardeo"] = HistoriaBombardeos.Get(def.Id) != null,
            // Perfil de la IA (fase 4).
            ["botDificultad"] = def.BotDificultad,
            ["botEstilo"] = def.BotEstilo,
            // Historia (colección `Historias`) a desbloquear al ganar la última parte.
            ["desbloqueaId"] = def.DesbloqueaHistoriaId,
            // Generales que el JUGADOR puede comprar en su cuartel (lista fija
            // de ids de `Cartas`). Vacía = cuartel normal de su ejército. La lee
            // el cliente (CuartelScreen.especialesFijasIds).
            ["especialesCuartel"] = (def.Jugador.EspecialesCuartel ?? Array.Empty<string>())
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Cast<object?>().ToList(),
            // Parte 1 de la historia (para reiniciar tras perder). Si no se define,
            // esta misma batalla es la parte 1.
            ["primeraParteId"] = def.PrimeraParteId ?? def.Id,
            // Ventana explicativa que el cliente muestra antes de empezar
            // (WarZeroHistoriaExplicacion.cs): [{icono, titulo, texto}, …].
            ["explicacion"] = ConstruirExplicacionHistoria(def, catalogo, cuartelJugador, cuartelBot),
            // Modo de juego. `conMano` = partida normal (mano, mazo y robo):
            // lo leen CerrarTurno (qué IA mueve al bot), ActualizarStats (si se
            // permite ampliar la mano) y el cliente (si pinta mano y robo).
            ["modo"] = def.EsPartidaNormal ? "normal" : "asedio",
            ["conMano"] = def.EsPartidaNormal,
            // RETO montado sobre el motor de historia (p. ej. «El duelo de
            // Alexander»): no pertenece a ninguna campaña, no desbloquea
            // historias y el fin de partida ofrece reintentar el reto.
            ["esReto"] = !string.IsNullOrWhiteSpace(def.RetoId),
            ["retoId"] = def.RetoId ?? "",
            // Texto del cartel de objetivo (HUD); vacío = el de siempre.
            ["hudObjetivo"] = def.HudObjetivo ?? "",
            // Textos del cartel de fin (vacío = los genéricos del cliente).
            ["textoVictoria"] = def.TextoVictoria ?? "",
            ["textoDerrota"] = def.TextoDerrota ?? "",
            // Mapa cacheado para la IA del bot (fase 4): mueve sin releer Firestore.
            ["mapa"] = new Dictionary<string, object?>
            {
                ["filas"] = (long)mapa.filas,
                ["columnas"] = (long)mapa.columnas,
                ["terreno"] = mapa.terreno.ToDictionary(kv => kv.Key, kv => (object?)kv.Value),
                // Isla central y continentes: los necesita la IA real de los
                // bots (partida normal) para valorar el farmeo.
                ["islaCentral"] = mapa.islaCentral.Cast<object?>().ToList(),
                ["continentes"] = mapa.continentes.ToDictionary(
                    kv => kv.Key, kv => (object?)kv.Value.Cast<object?>().ToList()),
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

        // ── RETO sobre el motor de historia ──────────────────────────────────
        // Mismas marcas que un reto normal (WarZeroRetos.cs): con ellas el
        // cierre de turno otorga el trofeo del reto al ganar
        // (OtorgarTrofeoRetoSiProcedeAsync). Al ser `esHistoria`, no reparte
        // recompensas de PvP.
        if (!string.IsNullOrWhiteSpace(def.RetoId))
        {
            var retoDef = RetoCatalogo.Get(def.RetoId);
            doc["esReto"] = true;
            doc[RetoFoco.CampoReto] = new Dictionary<string, object?>
            {
                ["id"] = def.RetoId,
                ["orden"] = (long)(retoDef?.Orden ?? 0),
                ["titulo"] = retoDef?.Titulo ?? def.Titulo,
                ["descripcion"] = retoDef?.Descripcion ?? "",
                ["mapaId"] = def.MapaId,
                ["jugadorUid"] = req.Uid,
                ["ejercitoJugador"] = (long)def.Jugador.Ejercito,
                ["botsUids"] = new List<object?>(),
                ["modoBots"] = "historia",
            };
        }

        // ── Bombardeo del turno 1 (si la historia lo tiene) ──────────────────
        // El cliente pinta desde el primer turno el % de cada celda y las
        // casillas desactivadoras.
        var guionBombardeo = HistoriaBombardeos.Get(def.Id);
        if (guionBombardeo != null)
        {
            var dataInicial = new Dictionary<string, object?>
            {
                ["obeliscos"] = obeliscos,
                ["historia"] = historia,
            };
            var tableroInicial = tablero.ToDictionary(
                kv => kv.Key, kv => M.List(kv.Value).Select(M.Map).ToList());
            var plan1 = PrepararPlanBombardeo(
                dataInicial, historia, guionBombardeo, 1, semilla, tableroInicial,
                new Dictionary<string, List<Dictionary<string, object?>>>(), reduccion: 0);
            doc["bombardeo"] = plan1.ACampo();
        }

        // ── Túnel inundable (si la historia lo tiene) ────────────────────────
        // Los tramos con agua se eligen ahora (distintos en cada partida). La
        // casilla segura de cada tramo NO se guarda: se deriva de la semilla
        // con el secreto del servidor (HistoriaTuneles.CasillaSegura).
        var cfgTunel = HistoriaTuneles.Get(def.Id);
        if (cfgTunel != null)
            doc["tunel"] = HistoriaTuneles.Inicial(cfgTunel, semilla).ACampo(cfgTunel);

        // ── Marcas para el cliente: papeles del bot, cartas clave… ──────────
        var tableroMarcas = tablero.ToDictionary(
            kv => kv.Key, kv => M.List(kv.Value).Select(M.Map).ToList());

        // ── Duelo de generales (si la historia lo tiene, HistoriaDuelo.cs) ───
        // Vidas de cada general, planes del turno 1 (lluvia / rompe escudos) y
        // casillas bloqueadas del mapa.
        var cfgDuelo = HistoriaDuelos.Get(def.Id);
        if (cfgDuelo != null)
        {
            var estadoDuelo = MotorDuelo.Inicial(cfgDuelo, tableroMarcas, req.Uid, HistoriaBotUid);
            bool Transitable(string c) => TerrenoUtil.Compatible(c, true, false, mapa.terreno);
            MotorDuelo.PrepararRompe(cfgDuelo, estadoDuelo, 1, tableroMarcas, Transitable, mapa.filas, mapa.columnas);
            var lluvia1 = MotorDuelo.PlanLluvia(cfgDuelo, estadoDuelo, 1, semilla, tableroMarcas,
                Transitable, mapa.filas, mapa.columnas);
            if (lluvia1 != null) doc["bombardeo"] = lluvia1.ACampo();
            doc["duelo"] = estadoDuelo.ACampo(cfgDuelo, 1);
        }

        var marcas = ConstruirMarcasHistoria(historia, obeliscos, tableroMarcas, turnoSiguiente: 1);
        if (marcas != null) doc["marcasHistoria"] = marcas;

        var docId = $"hist_{req.Uid}_{def.Id}";
        var lobbyRef = db.Collection("Partidas").Document(docId);

        // Empezar una batalla borra las demás que el jugador tuviera abiertas
        // (p. ej. cerró la app a la fuerza en mitad de otra parte).
        await BorrarHistoriasAbiertasAsync(req.Uid, exceptoDocId: docId);

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
    // (o en la `Coord` de cada entrada, si la tiene). [vipIds] y
    // [guarnicionIds], si no son null, reciben los instanceId de las cartas
    // CLAVE y de GUARNICIÓN sembradas (solo se usan para el jugador).
    private static void SembrarBando(
        Dictionary<string, object?> tablero,
        string cuartel,
        string ownerUid,
        string ownerZone,
        BandoHistoria bando,
        Dictionary<string, Dictionary<string, object?>> catalogo,
        List<string>? vipIds,
        List<string>? guarnicionIds)
    {
        var pilas = new Dictionary<string, List<object?>>(StringComparer.OrdinalIgnoreCase);
        List<object?> PilaDe(string coord)
        {
            if (pilas.TryGetValue(coord, out var p)) return p;
            p = tablero.TryGetValue(coord, out var lst) ? M.List(lst) : new List<object?>();
            pilas[coord] = p;
            return p;
        }

        foreach (var c in bando.Cartas)
        {
            if (!catalogo.TryGetValue(c.CartaId, out var cd))
            {
                Console.Error.WriteLine($"[WZ.Historia] carta desconocida {c.CartaId}, se omite");
                continue;
            }
            var cant = Math.Max(1, c.Cantidad);

            // Copias que nacen YA EVOLUCIONADAS (`CartaHistoria.Evolucionadas`):
            // se siembra la carta de su `IdEvolucion`, con sus estadísticas.
            int evolucionadas = Math.Clamp(c.Evolucionadas, 0, cant);
            var idEvo = evolucionadas > 0 ? IdEvolucionDe(catalogo, c.CartaId) : "";
            if (evolucionadas > 0 && idEvo == "")
            {
                Console.Error.WriteLine(
                    $"[WZ.Historia] {c.CartaId} no tiene evolución en el catálogo: se siembra sin evolucionar");
                evolucionadas = 0;
            }

            // Copias de GUARNICIÓN (`CartaHistoria.Guarnicion`): las ÚLTIMAS de
            // la entrada, es decir, primero las que nacen sin evolucionar.
            int guarnicion = Math.Clamp(c.Guarnicion, 0, cant);

            // Celda de nacimiento: la de la entrada o, si no tiene, el cuartel.
            var celda = string.IsNullOrWhiteSpace(c.Coord) ? cuartel : c.Coord!.Trim().ToUpperInvariant();
            var pila = PilaDe(celda);

            for (int q = 0; q < cant; q++)
            {
                bool evo = q < evolucionadas;
                var carta = evo
                    ? ClonarCartaParaTablero(catalogo[idEvo], idEvo, ownerUid, ownerZone)
                    : ClonarCartaParaTablero(cd, c.CartaId, ownerUid, ownerZone);
                var iid = M.Str(M.Get(carta, "instanceId"));
                if (q >= cant - guarnicion)
                {
                    carta[CampoGuarnicion] = true;
                    guarnicionIds?.Add(iid);
                }
                if (c.Vip)
                {
                    carta[CampoVip] = true;
                    vipIds?.Add(iid);
                }
                if (c.Rol != RolBotHistoria.Auto)
                    carta[CampoRol] = c.Rol == RolBotHistoria.Asalto ? RolAsalto : RolCazador;
                pila.Add(carta);
            }
        }

        foreach (var kv in pilas)
            if (kv.Value.Count > 0) tablero[kv.Key] = kv.Value;
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
            // Identidad de instancia desde el nacimiento: la necesitan las
            // reglas que casan una carta entre turnos (estáticas que no se
            // mueven, parálisis, habilidades con su lanzador).
            ["instanceId"] = "srv-" + Guid.NewGuid().ToString("N").Substring(0, 16),
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
    private async Task<(List<string> obeliscos, int filas, int columnas, Dictionary<string, string> terreno,
            List<string> islaCentral, Dictionary<string, List<string>> continentes)>
        LeerMapaAsync(string mapaId)
    {
        var obeliscos = new List<string>();
        int filas = 0, columnas = 0;
        var terreno = new Dictionary<string, string>();
        var islaCentral = new List<string>();
        var continentes = new Dictionary<string, List<string>>();
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
                islaCentral = M.List(M.Get(d, "islaCentral")).Select(M.Str)
                    .Where(s => s != "").ToList();
                foreach (var kv in M.Map(M.Get(d, "continentes")))
                    continentes[kv.Key] = M.List(kv.Value).Select(M.Str)
                        .Where(s => s != "").ToList();
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

        return (obeliscos, filas, columnas, terreno, islaCentral, continentes);
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
    // El COMPORTAMIENTO del bot (`historia.botComportamiento`, ver
    // HistoriaCatalogo.ComportamientoBotHistoria) decide cómo mueve sus cartas:
    //
    //   • "avanzar" (Diente de Invierno): cada carta AVANZA `Movimiento` pasos
    //     hacia el cuartel del jugador, respetando terreno y tipo de carta
    //     (TerrenoUtil, la misma primitiva que usa el bot real). Las cartas que
    //     ya están sobre el cuartel se quedan (siguen combatiendo cada turno).
    //   • "defender": las cartas mantienen su celda.
    //   • "cazar" (humanos_1): ver PlanCaza. Los cazadores PREDICEN a qué celda
    //     moverá el jugador cada grupo (la de menor % de bombardeo que alcanza, o
    //     una casilla desactivadora libre) y van a por ella; solo entran si el
    //     grupo que llega le gana. Los que sobran vigilan las desactivadoras.
    //
    // En cualquier comportamiento, las cartas de GUARNICIÓN (campo
    // `guarnicionHistoria`, sembrado desde `CartaHistoria.Guarnicion`) no se
    // mueven nunca, y una carta con RUTA DE GRUPO sigue su ruta.
    //
    // Al re-emitir TODAS sus cartas (movidas o no) se garantiza que persisten:
    // el tablero se reconstruye cada turno a partir de los cierres. El
    // co-emplazamiento con las cartas del jugador dispara el combate/asalto en
    // ResolverTurnoCoreEnTx (no hay que "declarar" ataque).
    //
    // BOMBARDEO (opcional, HistoriaBombardeo.cs): el plan del turno (disparos
    // por fila y %) ya está publicado en la partida (`bombardeo`, lo preparó la
    // resolución del turno anterior o la creación de la partida). Aquí se
    // SORTEA fila a fila y se convierte en disparos lejanos del bot; los que
    // caen donde termina una carta del bot se redirigen a otra celda de su fila
    // (ConstruirBombardeo).
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
    //   • RUTA DE GRUPO (`GrupoOleada.Ruta`): itinerario propio de una columna
    //     de asalto. Se GRABA en cada carta al nacer (campos `rutaHistoria` y
    //     `rutaPaso`), así que la unidad lo sigue turno a turno sin que haya que
    //     recordar nada fuera del tablero; manda sobre los puntos de paso
    //     globales y cede solo ante las celdas vetadas.
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

        // DUELO DE GENERALES (HistoriaDuelo.cs): el bot es solo el jefe y lo
        // mueve su propia IA; las habilidades las resuelve el servidor.
        var cfgDuelo = HistoriaDuelos.Get(historiaId);
        if (cfgDuelo != null)
            return ConstruirJugadaDuelo(data, botUid, turno, cfgDuelo);

        // Comportamiento del bot. Las partidas creadas antes de que existiera
        // `botComportamiento` lo deducen de su objetivo, como hasta ahora.
        var comportamiento = M.Str(M.Get(hist, "botComportamiento"));
        if (comportamiento == "")
            comportamiento = M.Str(M.Get(hist, "botObjetivo")) == "sobrevivir" ? "defender" : "avanzar";
        bool botDefiende = comportamiento == "defender";
        bool botCaza = comportamiento == "cazar";

        // Objetivo del asedio: el cuartel del jugador. Si ya no existe
        // (conquistado), el bot se queda quieto (la partida ya habrá terminado).
        var obeliscos = M.Map(M.Get(data, "obeliscos"));
        var objetivo = M.Str(M.Get(obeliscos, jugadorUid));
        var cuartelBot = M.Str(M.Get(obeliscos, botUid));

        // Resuelve una coord del guion: los tokens simbólicos de CoordHistoria
        // se traducen a la coord real de cada cuartel; el resto se usa tal cual.
        string Resolver(string coord)
        {
            if (string.IsNullOrWhiteSpace(coord)) return "";
            var c = coord.Trim();
            if (string.Equals(c, CoordHistoria.CuartelBot, StringComparison.OrdinalIgnoreCase))
                return cuartelBot;
            if (string.Equals(c, CoordHistoria.CuartelRival, StringComparison.OrdinalIgnoreCase))
                return objetivo;
            return c.ToUpperInvariant();
        }

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

        // TÚNEL (HistoriaTuneles.cs): el bot no puede pisarlo ni atravesarlo,
        // y lo que hay dentro es invisible para sus cazadores.
        var celdasTunel = CeldasTunelDe(data);
        vetadas.UnionWith(celdasTunel);

        // ASALTO GENERAL: desde `turnoAsalto` TODAS las cartas del bot dejan de
        // cazar y van a por el cuartel del jugador, reuniéndose antes en
        // `reunionAsalto` para entrar juntas (PlanAsalto).
        int turnoAsalto = M.Int(M.Get(hist, "turnoAsalto"));
        bool asaltoGeneral = turnoAsalto > 0 && turno >= turnoAsalto;
        var reunion = Resolver(M.Str(M.Get(hist, "reunionAsalto")));
        int esperaMax = M.Int(M.Get(hist, "esperaMaxReunion"));
        bool presasHuyenACasa = M.Bool(M.Get(hist, "presasHuyenACasa"));
        var vipIds = M.List(M.Get(hist, "vipIds")).Select(M.Str).Where(s => s != "")
            .ToHashSet(StringComparer.Ordinal);

        // ── 1) Inventario de lo que el bot tiene en el tablero ───────────────
        // Se recoge primero (en vez de mover sobre la marcha) porque las
        // evoluciones de tablero necesitan comparar TODAS las copias entre sí
        // para quedarse con las más adelantadas, y la caza necesita planificar
        // a todo el grupo a la vez.
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

        // ── 3a) Carta FINAL de cada unidad (tras evolucionar) y si puede mover ─
        // Una copia que evoluciona se sustituye por un clon de la carta
        // evolucionada (entra con las stats de la evolución) y, por defecto,
        // gasta el turno evolucionando: no se mueve. La ruta de grupo y la marca
        // de guarnición se copian al clon, y conserva su identidad de instancia
        // (la casan parálisis/estáticas entre turnos).
        var finales = new List<(Dictionary<string, object?> carta, bool avanza)>(unidades.Count);
        for (int i = 0; i < unidades.Count; i++)
        {
            var carta = unidades[i].carta;
            var cartaFinal = carta;
            bool avanza = true;
            if (evoPorIndice.TryGetValue(i, out var evo))
            {
                cartaFinal = ClonarCartaParaTablero(evo.cd, evo.idEvo, botUid, ZonaHistoriaBot);
                CopiarRutaGrupo(carta, cartaFinal);
                if (EsGuarnicion(carta)) cartaFinal[CampoGuarnicion] = true;
                var iidPrevio = M.Str(M.Get(carta, "instanceId"));
                if (iidPrevio != "") cartaFinal["instanceId"] = iidPrevio;
                avanza = evo.avanza;
            }
            // Misma regla que en cualquier partida: una ESTÁTICA (o acción /
            // trampa) no se mueve nunca (ReglasEntrada.Mov == 0). La guarnición
            // tampoco sale nunca del cuartel.
            if (ReglasEntrada.EsInmovil(cartaFinal) || EsGuarnicion(cartaFinal)) avanza = false;
            finales.Add((cartaFinal, avanza));
        }

        // ── 3b) Plan de bombardeo de ESTE turno (el que ve el jugador) ────────
        var bombardeo = HistoriaBombardeos.Get(historiaId);
        int semilla = SemillaPartida(hist);
        var plan = bombardeo == null ? null : PlanBombardeoDeTurno(data, bombardeo, turno, semilla);

        // ── 3c) Plan de CAZA y de ASALTO (solo comportamiento "cazar") ───────
        Dictionary<int, string>? planCaza = null;
        if (botCaza)
        {
            var escudosJugador = EscudosDe(M.Get(data, "efectosCelda"), jugadorUid);
            var noPisables = new HashSet<string>(vetadas, StringComparer.OrdinalIgnoreCase);
            noPisables.UnionWith(escudosJugador);   // un escudo rival revierte la entrada

            bool EsAsalto(int i) =>
                asaltoGeneral || M.Str(M.Get(finales[i].carta, CampoRol)) == RolAsalto;

            var libresDeRuta = Enumerable.Range(0, unidades.Count)
                .Where(i => finales[i].avanza)
                .Where(i => M.List(M.Get(finales[i].carta, CampoRuta)).Count == 0)
                .ToList();

            // En los últimos `VentanaReunion` turnos antes del asalto general,
            // cada cazador deja de cazar JUSTO a tiempo de llegar al punto de
            // reunión (según su movimiento y el camino real, rodeando el
            // túnel). Pasa a ser de asalto para siempre (la marca viaja en la
            // carta). Antes de esa ventana todos cazan.
            if (!asaltoGeneral && turnoAsalto > 0 && reunion != "" && filas > 0 && columnas > 0
                && turnoAsalto - turno <= VentanaReunion)
            {
                foreach (var i in libresDeRuta)
                {
                    if (EsAsalto(i)) continue;
                    var carta = finales[i].carta;
                    var (t, m) = TerrenoUtil.ClaseDeTipo(M.Int(M.Get(carta, "Tipo", "tipo")));
                    int mov = Math.Max(1, M.Int(M.Get(carta, "Movimiento", "movimiento")));
                    int dist = DistanciaBfs(unidades[i].coord, reunion, t, m, terreno, filas, columnas, vetadas);
                    if (dist == int.MaxValue) continue;
                    int turnosHastaReunion = (dist + mov - 1) / mov;
                    // Llega al cerrar el turno anterior al asalto (+1 de margen).
                    if (turno + turnosHastaReunion + 1 >= turnoAsalto)
                        carta[CampoRol] = RolAsalto;
                }
            }

            var cazadores = libresDeRuta
                .Where(i => !EsAsalto(i))
                .Select(i => (idx: i, coord: unidades[i].coord, carta: finales[i].carta))
                .ToList();

            var celdasBot = new HashSet<string>(unidades.Select(u => u.coord), StringComparer.OrdinalIgnoreCase);
            var presas = PredecirPresas(
                PilasJugador(data, jugadorUid, objetivo, cuartelBot, celdasTunel), plan,
                celdasBot, noPisables, objetivo, cuartelBot, terreno, filas, columnas,
                huyeHacia: presasHuyenACasa ? objetivo : "",
                vipIds: vipIds,
                bloqueadas: celdasTunel);

            var desactivadoras = plan?.Desactivadoras.Select(d => d.Coord).ToList() ?? new List<string>();

            planCaza = PlanCaza(
                cazadores, presas, desactivadoras, noPisables,
                objetivo, cuartelBot, terreno, filas, columnas, celdasTunel);

            // Asalto: las cartas de asalto (o todas, en el asalto general).
            var asaltantes = libresDeRuta
                .Where(EsAsalto)
                .Select(i => (idx: i, coord: unidades[i].coord, carta: finales[i].carta))
                .ToList();
            if (asaltantes.Count > 0)
            {
                var planAsalto = PlanAsalto(
                    asaltantes, asaltoGeneral, turno, turnoAsalto, esperaMax,
                    reunion, objetivo, noPisables, terreno, filas, columnas);
                foreach (var kv in planAsalto) planCaza[kv.Key] = kv.Value;
            }
        }

        // ── 3d) Colocar TODO lo que el bot ya tiene en el tablero ─────────────
        // (unidades nacidas en la siembra inicial + refuerzos de oleadas
        // anteriores que ya se comprometieron en turnos previos).
        for (int i = 0; i < unidades.Count; i++)
        {
            var coordActual = unidades[i].coord;
            var (cartaFinal, avanza) = finales[i];

            int tipo = M.Int(M.Get(cartaFinal, "Tipo", "tipo"));
            var (tierra, mar) = TerrenoUtil.ClaseDeTipo(tipo);

            var destino = coordActual;
            if (!avanza)
            {
                // Guarnición, estática o evolucionando: se queda.
            }
            else if (planCaza != null && planCaza.TryGetValue(i, out var destinoCaza))
            {
                destino = destinoCaza;
            }
            else
            {
                // Meta de ESTE turno. Prioridad:
                //   1) el paso pendiente de la ruta de grupo que la carta lleva
                //      grabada desde que nació (columnas de asalto scriptadas),
                //   2) BOT DEFENSOR: su propia celda (no avanza),
                //   3) los puntos de paso globales del turno, para las que no
                //      llevan ruta propia,
                //   4) el cuartel del jugador.
                // Un bot defensor que TERMINA su ruta se queda en el último paso.
                var meta = MetaDeRutaGrupo(
                               cartaFinal, coordActual, objetivo, Resolver,
                               alTerminar: botDefiende ? coordActual : objetivo)
                           ?? (botDefiende
                               ? coordActual
                               : MetaConPuntosDePaso(coordActual, objetivo, puntosDePaso));

                if (meta != "" && meta != coordActual && filas > 0 && columnas > 0)
                {
                    int mov = Math.Max(1, M.Int(M.Get(cartaFinal, "Movimiento", "movimiento")));
                    destino = PasoHaciaEvitando(
                        coordActual, meta, mov, tierra, mar, terreno, filas, columnas, vetadas);
                }
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

                        // Coord de salida: admite los tokens simbólicos
                        // (CUARTEL_BOT / CUARTEL_RIVAL) además de coords literales.
                        var coordSalida = Resolver(grupo.Coordenada);
                        if (coordSalida == "")
                        {
                            Console.Error.WriteLine(
                                $"[WZ.Historia] guion {historiaId} turno {turno}: coord de salida '{grupo.Coordenada}' no se pudo resolver, se omite el grupo");
                            break;
                        }

                        // Una oleada nunca puede sacar refuerzos en una celda
                        // vetada de este turno: se desvían a la adyacente
                        // compatible más cercana al objetivo.
                        if (vetadas.Contains(coordSalida))
                        {
                            var (t, m) = TerrenoUtil.ClaseDeTipo(M.Int(M.Get(cdUso, "Tipo", "tipo")));
                            var alternativa = EsquivarCeldaVetada(
                                coordSalida, objetivo, t, m, terreno, filas, columnas, vetadas);
                            Console.Error.WriteLine(
                                $"[WZ.Historia] guion {historiaId} turno {turno}: salida {coordSalida} está vetada, {idUso} sale en {alternativa}");
                            coordSalida = alternativa;
                        }

                        var nueva = ClonarCartaParaTablero(cdUso, idUso, botUid, ZonaHistoriaBot);
                        // La ruta del grupo viaja GRABADA en la carta, así que
                        // en los turnos siguientes cada unidad sabe por dónde le
                        // toca ir sin que el guion tenga que recordarlo.
                        GrabarRutaGrupo(nueva, grupo.Ruta);
                        // Papel en la caza (cazador / asalto), si el grupo lo fija.
                        if (grupo.Rol != RolBotHistoria.Auto)
                            nueva[CampoRol] = grupo.Rol == RolBotHistoria.Asalto ? RolAsalto : RolCazador;
                        Colocar(coordSalida, nueva);
                    }
                }
            }
        }

        // ── 5) Disparos: se sortea el plan publicado. Van al final porque los
        // disparos que caen donde TERMINAN las cartas del bot se redirigen.
        var acciones = new List<object?>();
        if (bombardeo != null && plan != null)
            acciones.AddRange(ConstruirBombardeo(bombardeo, plan, semilla, cuartelBot, celdas.Keys, botUid));

        return new Dictionary<string, object?>
        {
            ["uid"] = botUid,
            ["turno"] = turno,
            ["celdas"] = celdas,
            ["timestamp"] = Timestamp.FromDateTime(DateTime.UtcNow),
            ["acciones"] = acciones,
        };
    }

    // ── DUELO: jugada del bot ────────────────────────────────────────────────
    /// Jugada del bot en un DUELO de generales.
    ///   • Si el bot es el JEFE (humanos_3): solo mueve al jefe (paralizado o
    ///     canalizando se queda; si no, huye o caza según MotorDuelo.DecidirJefe).
    ///   • Si el bot lleva a los GENERALES (duelo invertido): los mueve con
    ///     MotorDuelo.DecidirGenerales.
    /// Re-emite el resto de sus cartas (si las hubiera) donde están.
    private static Dictionary<string, object?> ConstruirJugadaDuelo(
        Dictionary<string, object?> data, string botUid, int turno, ConfigDuelo cfg)
    {
        var hist = M.Map(M.Get(data, "historia"));
        var mapaH = M.Map(M.Get(hist, "mapa"));
        int filas = M.Int(M.Get(mapaH, "filas"));
        int columnas = M.Int(M.Get(mapaH, "columnas"));
        var terreno = new Dictionary<string, string>();
        foreach (var kv in M.Map(M.Get(mapaH, "terreno"))) terreno[kv.Key] = M.Str(kv.Value);
        bool Transitable(string c) => TerrenoUtil.Compatible(c, true, false, terreno);

        var tablero = M.Map(M.Get(data, "tablero"))
            .ToDictionary(kv => kv.Key, kv => M.List(kv.Value).Select(M.Map).ToList());
        var estado = EstadoDuelo.DesdeCampo(M.Get(data, "duelo"));
        var destinos = new Dictionary<string, string>();
        if (estado != null)
        {
            MotorDuelo.CompletarDuenos(cfg, estado, M.Str(M.Get(hist, "jugadorUid")), botUid);
            if (estado.JefeUid == botUid)
            {
                var idJefe = estado.IdDe(EstadoDuelo.RolJefe);
                var d = MotorDuelo.DecidirJefe(cfg, estado, turno, SemillaPartida(hist), tablero, Transitable, filas, columnas);
                if (idJefe != null && d != "") destinos[idJefe] = d;
            }
            else
            {
                var lluvia = LeerPlanBombardeo(M.Get(data, "bombardeo"));
                destinos = MotorDuelo.DecidirGenerales(cfg, estado, turno, SemillaPartida(hist), tablero,
                    lluvia, Transitable, filas, columnas);
            }
        }

        var celdas = new Dictionary<string, object?>();
        foreach (var (coord, lst) in tablero)
            foreach (var carta in lst)
            {
                if (M.Str(M.Get(carta, "ownerUid")) != botUid) continue;
                var destino = destinos.TryGetValue(M.Str(M.Get(carta, "instanceId")), out var d2) && d2 != ""
                    ? d2
                    : coord;
                if (!celdas.TryGetValue(destino, out var l) || l is not List<object?> lista)
                    celdas[destino] = lista = new List<object?>();
                lista.Add(carta);
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

    // ── GUARNICIÓN ───────────────────────────────────────────────────────────
    // Marca que llevan en el tablero las cartas del bot que no salen nunca de su
    // celda (sembradas desde `CartaHistoria.Guarnicion`).
    private const string CampoGuarnicion = "guarnicionHistoria";

    private static bool EsGuarnicion(Dictionary<string, object?> carta) =>
        M.Bool(M.Get(carta, CampoGuarnicion));

    // ── CARTAS CLAVE y PAPELES ───────────────────────────────────────────────
    // Marca informativa de las cartas CLAVE del jugador (`CartaHistoria.Vip`).
    // La autoridad es `historia.vipIds` (por instanceId), porque el cliente no
    // reenvía campos desconocidos de sus cartas.
    private const string CampoVip = "vipHistoria";

    // Papel de una carta del bot (`CartaHistoria.Rol` / `GrupoOleada.Rol`).
    private const string CampoRol = "rolHistoria";
    private const string RolCazador = "cazador";
    private const string RolAsalto = "asalto";

    /// Turnos ANTES del asalto general en los que los cazadores empiezan a
    /// marchar hacia el punto de reunión (los que no llegarían a tiempo).
    private const int VentanaReunion = 3;

    /// Celdas del túnel de la partida (campo `tunel.celdas`), vacío si no hay.
    private static HashSet<string> CeldasTunelDe(Dictionary<string, object?> data) =>
        M.List(M.Get(M.Map(M.Get(data, "tunel")), "celdas"))
            .Select(M.Str).Where(s => s != "")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    // ── CAZA ─────────────────────────────────────────────────────────────────
    // Margen de poder (Fuerza + Defensa) con el que el cazador acepta entrar en
    // la celda de una presa: entra solo si Σ poder de los cazadores que LLEGAN
    // este turno > poder de la presa × margen. Es la misma vara con la que el
    // servidor resuelve un combate fuera de cuartel (gana el de mayor F + D).
    private const double MargenCaza = 1.0;

    // Al REPARTIR a los cazadores entre las presas, cada presa recibe cazadores
    // hasta superar su poder × este factor (un colchón sobre el margen, porque
    // no todos llegarán el mismo turno). El resto sigue a la siguiente presa.
    private const double RefuerzoReparto = 1.3;

    // Sin presas ni desactivadoras, los cazadores cercan el cuartel del jugador
    // sin acercarse a menos de esta distancia.
    private const int DistanciaSitioCuartelJugador = 2;

    /// Una presa del cazador: el grupo del jugador que está en [Origen], la
    /// celda a la que se PREVÉ que irá este turno ([Destino]), su poder y si
    /// lleva una carta CLAVE del jugador ([EsVip]: los cazadores van primero a
    /// por ella).
    private readonly record struct Presa(string Origen, string Destino, int Poder, bool EsVip = false);

    /// Para cada grupo visible del jugador (fuera de los cuarteles), la celda a
    /// la que es MÁS PROBABLE que lo mueva este turno. Se supone un jugador
    /// prudente que ve el % de bombardeo:
    ///   1. si alcanza una casilla DESACTIVADORA libre (sin cartas del bot), va a
    ///      ella (es lo mejor que puede hacer: quita disparos y le da escudo);
    ///   2. si no, a la celda alcanzable con MENOS % de bombardeo;
    ///   3. a igual %, la que más le acerca al cuartel del bot (quiere avanzar)
    ///      o, si se indica [huyeHacia], la que más le acerca a esa celda (el
    ///      jugador huye hacia su cuartel, humanos_2).
    /// Se consideran las celdas que el grupo alcanza moviéndose junto (su carta
    /// más lenta), sin entrar en celdas con cartas del bot ni en su cuartel, ni
    /// atravesar [bloqueadas] (el túnel).
    private static List<Presa> PredecirPresas(
        Dictionary<string, List<Dictionary<string, object?>>> pilas,
        PlanBombardeo? plan, HashSet<string> celdasBot, HashSet<string> noPisablesBot,
        string cuartelJugador, string cuartelBot,
        Dictionary<string, string> terreno, int filas, int columnas,
        string huyeHacia = "",
        ISet<string>? vipIds = null,
        ISet<string>? bloqueadas = null)
    {
        var metaHuida = string.IsNullOrWhiteSpace(huyeHacia) ? cuartelBot : huyeHacia;
        var prob = plan?.Probabilidades() ?? new Dictionary<string, double>();
        var desact = new HashSet<string>(
            plan?.Desactivadoras.Select(d => d.Coord) ?? Enumerable.Empty<string>(),
            StringComparer.OrdinalIgnoreCase);

        var res = new List<Presa>();
        foreach (var kv in pilas.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            int mov = kv.Value.Min(c => ReglasEntrada.EsInmovil(c)
                ? 0
                : Math.Max(1, M.Int(M.Get(c, "Movimiento", "movimiento"))));
            var (t, m) = TerrenoUtil.ClaseDeTipo(M.Int(M.Get(kv.Value[0], "Tipo", "tipo")));
            var opciones = Alcanzables(kv.Key, mov, t, m, terreno, filas, columnas, bloqueadas)
                .Where(c => !celdasBot.Contains(c)
                            && !string.Equals(c, cuartelBot, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (opciones.Count == 0) opciones.Add(kv.Key);

            var destino = opciones
                .OrderBy(c => desact.Contains(c) ? 0 : 1)
                .ThenBy(c => prob.TryGetValue(c, out var p) ? p : 0.0)
                .ThenBy(c => DistanciaCoord(c, metaHuida))
                .ThenBy(c => c, StringComparer.Ordinal)
                .First();
            bool esVip = vipIds != null && vipIds.Count > 0
                && kv.Value.Any(c => vipIds.Contains(M.Str(M.Get(c, "instanceId"))));
            res.Add(new Presa(kv.Key, destino, kv.Value.Sum(Poder), esVip));
        }
        return res;
    }

    /// Plan de movimiento del bot CAZADOR: índice de unidad → celda destino.
    ///
    /// Reglas, en orden:
    ///   1. Cada cazador solo puede terminar en celdas que alcanza este turno
    ///      (BFS por terreno), fuera de `noPisables` (celdas vetadas y celdas con
    ///      escudo del jugador) y de los dos cuarteles. Si no tiene ninguna, se
    ///      queda donde está. (El bombardeo no le preocupa: nunca cae donde
    ///      termina una carta del bot.)
    ///   2. Los cazadores se REPARTEN entre las presas (ver PredecirPresas):
    ///      empezando por la presa cuyo destino tienen más cerca, cada una recibe
    ///      a los cazadores más próximos hasta superar su poder ×
    ///      `RefuerzoReparto`; los que sobran pasan a la siguiente.
    ///   3. Si los cazadores de una presa que LLEGAN a su destino previsto le
    ///      ganan en poder (F + D), entran. Si no, acechan en la celda segura
    ///      común más cercana a ese destino, sin entrar (a igual distancia, la
    ///      más "en diagonal", que corta más salidas).
    ///   4. Los cazadores SOBRANTES (o todos, si no hay presas fuera de los
    ///      cuarteles) vigilan la casilla desactivadora más cercana, sin pisarla.
    ///      Sin desactivadoras, cercan el cuartel del jugador a distancia ≥ 2.
    ///
    /// CARTAS CLAVE (humanos_2): las presas con una carta clave del jugador
    /// (`Presa.EsVip`) se reparten PRIMERO, así que los cazadores más cercanos
    /// van a por ella; los que sobran se comen el cebo que puedan ganar
    /// (cazadores oportunistas). [bloqueadas] (el túnel) no se atraviesa.
    private static Dictionary<int, string> PlanCaza(
        IReadOnlyList<(int idx, string coord, Dictionary<string, object?> carta)> cazadores,
        IReadOnlyList<Presa> presas, IReadOnlyList<string> desactivadoras,
        HashSet<string> noPisables, string cuartelJugador, string cuartelBot,
        Dictionary<string, string> terreno, int filas, int columnas,
        ISet<string>? bloqueadas = null)
    {
        var plan = new Dictionary<int, string>();
        if (cazadores.Count == 0 || filas <= 0 || columnas <= 0) return plan;

        // 1) Opciones de cada cazador.
        var opciones = new Dictionary<int, HashSet<string>>();
        foreach (var u in cazadores)
        {
            var (t, m) = TerrenoUtil.ClaseDeTipo(M.Int(M.Get(u.carta, "Tipo", "tipo")));
            int mov = Math.Max(1, M.Int(M.Get(u.carta, "Movimiento", "movimiento")));
            opciones[u.idx] = Alcanzables(u.coord, mov, t, m, terreno, filas, columnas, bloqueadas)
                .Where(c => !noPisables.Contains(c)
                            && !string.Equals(c, cuartelJugador, StringComparison.OrdinalIgnoreCase)
                            && !string.Equals(c, cuartelBot, StringComparison.OrdinalIgnoreCase))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        // Celda de [candidatas] más cercana a [hacia] (sin entrar en ella si
        // [sinEntrar]) y a distancia ≥ [distMin]. "" si no hay ninguna.
        static string MasCercana(IEnumerable<string> candidatas, string hacia, bool sinEntrar, int distMin = 0) =>
            candidatas
                .Where(c => !(sinEntrar && string.Equals(c, hacia, StringComparison.OrdinalIgnoreCase)))
                .Where(c => DistanciaCoord(c, hacia) >= distMin)
                .OrderBy(c => DistanciaCoord(c, hacia))
                .ThenBy(c => DistanciaChebyshev(c, hacia))
                .ThenBy(c => c, StringComparer.Ordinal)
                .FirstOrDefault() ?? "";

        void Fijar(int idx, string destino, string actual) =>
            plan[idx] = destino != "" ? destino : actual;

        // Vigilancia de desactivadoras (o cerco del cuartel si no hay).
        void Vigilar(IEnumerable<(int idx, string coord, Dictionary<string, object?> carta)> us)
        {
            foreach (var u in us)
            {
                var d = desactivadoras
                    .OrderBy(x => DistanciaCoord(u.coord, x))
                    .ThenBy(x => x, StringComparer.Ordinal)
                    .FirstOrDefault();
                Fijar(u.idx,
                      d != null
                          ? MasCercana(opciones[u.idx], d, sinEntrar: true)
                          : MasCercana(opciones[u.idx], cuartelJugador, sinEntrar: true,
                                       distMin: DistanciaSitioCuartelJugador),
                      u.coord);
            }
        }

        if (presas.Count == 0)
        {
            Vigilar(cazadores);
            return plan;
        }

        // 2) Reparto de cazadores entre las presas.
        var libres = cazadores.ToList();
        var asignados = new List<(Presa presa, List<(int idx, string coord, Dictionary<string, object?> carta)> miembros)>();
        var ordenPresas = presas
            .OrderBy(p => p.EsVip ? 0 : 1)
            .ThenBy(p => libres.Min(u => DistanciaCoord(u.coord, p.Destino)))
            .ThenBy(p => p.Poder)
            .ThenBy(p => p.Origen, StringComparer.Ordinal)
            .ToList();
        foreach (var presa in ordenPresas)
        {
            if (libres.Count == 0) break;
            var elegidos = new List<(int idx, string coord, Dictionary<string, object?> carta)>();
            int acumulado = 0;
            foreach (var u in libres
                         .OrderBy(u => DistanciaCoord(u.coord, presa.Destino))
                         .ThenByDescending(u => Poder(u.carta))
                         .ThenBy(u => u.idx))
            {
                elegidos.Add(u);
                acumulado += Poder(u.carta);
                if (acumulado > presa.Poder * RefuerzoReparto) break;
            }
            asignados.Add((presa, elegidos));
            libres = libres.Where(u => !elegidos.Any(e => e.idx == u.idx)).ToList();
        }

        // 3) Cada grupo: atacar si gana, si no acechar.
        foreach (var (presa, miembros) in asignados)
        {
            var destino = presa.Destino;
            var llegan = miembros.Where(u => opciones[u.idx].Contains(destino)).ToList();
            int poderLlegan = llegan.Sum(u => Poder(u.carta));
            var restantes = miembros;
            if (llegan.Count > 0 && poderLlegan > presa.Poder * MargenCaza)
            {
                foreach (var u in llegan) plan[u.idx] = destino;
                restantes = miembros.Where(u => !plan.ContainsKey(u.idx)).ToList();
                Console.WriteLine(
                    $"[WZ.Historia] caza: {llegan.Count} cazador(es) (poder {poderLlegan}) van a {destino} " +
                    $"a por el grupo de {presa.Origen} (poder {presa.Poder})");
            }
            if (restantes.Count == 0) continue;

            var comunes = restantes
                .Select(u => (IEnumerable<string>)opciones[u.idx])
                .Aggregate((a, b) => a.Intersect(b, StringComparer.OrdinalIgnoreCase).ToList());
            var celdaComun = MasCercana(comunes, destino, sinEntrar: true);
            foreach (var u in restantes)
                Fijar(u.idx,
                      celdaComun != "" ? celdaComun : MasCercana(opciones[u.idx], destino, sinEntrar: true),
                      u.coord);
        }

        // 4) Sobrantes: vigilan las desactivadoras.
        Vigilar(libres);
        return plan;
    }

    /// Distancia de Chebyshev (máx. de |Δfila|, |Δcolumna|).
    private static int DistanciaChebyshev(string a, string b)
    {
        var pa = ParseCoord(a);
        var pb = ParseCoord(b);
        if (pa == null || pb == null) return int.MaxValue;
        return Math.Max(Math.Abs(pa.Value.r - pb.Value.r), Math.Abs(pa.Value.c - pb.Value.c));
    }

    /// Poder de combate de una carta: Fuerza + Defensa.
    private static int Poder(Dictionary<string, object?> c) =>
        M.Int(M.Get(c, "Fuerza", "fuerza")) + M.Int(M.Get(c, "Defensa", "defensa"));

    /// Grupos VISIBLES del jugador por celda, fuera de los dos cuarteles. No
    /// cuentan los clones (son señuelos) ni las cartas invisibles (el bot no
    /// las ve, igual que un rival humano).
    private static Dictionary<string, List<Dictionary<string, object?>>> PilasJugador(
        Dictionary<string, object?> data, string jugadorUid, string cuartelJugador, string cuartelBot,
        ISet<string>? ocultas = null)
    {
        var res = new Dictionary<string, List<Dictionary<string, object?>>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (coord, c) in CartasVisiblesJugador(M.Map(M.Get(data, "tablero"))
                     .Select(kv => (kv.Key, M.List(kv.Value).Select(M.Map))), jugadorUid))
        {
            if (string.Equals(coord, cuartelJugador, StringComparison.OrdinalIgnoreCase)) continue;
            if (string.Equals(coord, cuartelBot, StringComparison.OrdinalIgnoreCase)) continue;
            // Dentro del túnel no se ven (humanos_2).
            if (ocultas != null && ocultas.Contains(coord)) continue;
            if (!res.TryGetValue(coord, out var lst)) res[coord] = lst = new();
            lst.Add(c);
        }
        return res;
    }

    /// Cartas del jugador que el bot VE (ni clones ni invisibles), con su celda.
    private static IEnumerable<(string coord, Dictionary<string, object?> carta)> CartasVisiblesJugador(
        IEnumerable<(string coord, IEnumerable<Dictionary<string, object?>> cartas)> tablero, string jugadorUid)
    {
        foreach (var (coord, cartas) in tablero)
            foreach (var c in cartas)
            {
                if (M.Str(M.Get(c, "ownerUid")) != jugadorUid) continue;
                if (CartaHelper.EsClon(c) || EsInvisible(c)) continue;
                yield return (coord, c);
            }
    }

    private static bool EsInvisible(Dictionary<string, object?> c) =>
        CartaHelper.Efectos(c).Any(ef =>
            M.Str(M.Get(ef, "tipo")) == "invisibilidad" && M.Int(M.Get(ef, "turnosRestantes")) > 0);

    /// Celdas con un escudo ACTIVO de [uid] en un mapa de efectos de celda
    /// (formato de `efectosCelda`: coord → lista de efectos).
    private static HashSet<string> EscudosDe(object? efectosCelda, string uid)
    {
        var res = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in M.Map(efectosCelda))
            if (M.List(kv.Value).Select(M.Map).Any(ef => EsEscudoDe(ef, uid)))
                res.Add(kv.Key);
        return res;
    }

    private static bool EsEscudoDe(Dictionary<string, object?> ef, string uid) =>
        M.Str(M.Get(ef, "tipo")) == "escudo"
        && M.Int(M.Get(ef, "turnosRestantes")) > 0
        && M.Str(M.Get(ef, "origenUid")) == uid;

    /// Celdas alcanzables desde [desde] en ≤ [pasos] pasos ortogonales por
    /// terreno compatible (incluye la propia celda de partida), sin pasar por
    /// [bloqueadas] (p. ej. el túnel de humanos_2).
    private static HashSet<string> Alcanzables(
        string desde, int pasos, bool tierra, bool mar,
        Dictionary<string, string> terreno, int filas, int columnas,
        ISet<string>? bloqueadas = null)
    {
        var res = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { desde };
        var p = ParseCoord(desde);
        if (p == null || pasos <= 0) return res;

        var cola = new Queue<(int r, int c, int d)>();
        cola.Enqueue((p.Value.r, p.Value.c, 0));
        var deltas = new (int dr, int dc)[] { (-1, 0), (1, 0), (0, -1), (0, 1) };
        while (cola.Count > 0)
        {
            var (r, c, d) = cola.Dequeue();
            if (d >= pasos) continue;
            foreach (var (dr, dc) in deltas)
            {
                int nr = r + dr, nc = c + dc;
                if (nr < 0 || nr >= filas || nc < 1 || nc > columnas) continue;
                var cand = FormatCoord(nr, nc);
                if (res.Contains(cand)) continue;
                if (bloqueadas != null && bloqueadas.Contains(cand)) continue;
                if (!TerrenoUtil.Compatible(cand, tierra, mar, terreno)) continue;
                res.Add(cand);
                cola.Enqueue((nr, nc, d + 1));
            }
        }
        return res;
    }

    // ── ASALTO ───────────────────────────────────────────────────────────────
    /// Plan de las cartas de ASALTO (rol "asalto" o todas desde el asalto
    /// general): índice de unidad → celda destino.
    ///
    ///   • Antes del asalto general: van al punto de REUNIÓN y esperan allí.
    ///   • En el asalto general: si TODAS pueden entrar este turno en el
    ///     cuartel del jugador (o ya se agotó la espera, o no hay reunión),
    ///     entran todas a la vez —sumando su fuerza en la misma resolución— y
    ///     las que no llegan siguen hacia él. Si no, se siguen reuniendo.
    /// Nunca pisan ni atraviesan [noPisables] (túnel, escudos del jugador).
    private static Dictionary<int, string> PlanAsalto(
        IReadOnlyList<(int idx, string coord, Dictionary<string, object?> carta)> asaltantes,
        bool asaltoGeneral, int turno, int turnoAsalto, int esperaMax,
        string reunion, string objetivo, HashSet<string> noPisables,
        Dictionary<string, string> terreno, int filas, int columnas)
    {
        var plan = new Dictionary<int, string>();
        if (asaltantes.Count == 0) return plan;
        if (objetivo == "" || filas <= 0 || columnas <= 0)
        {
            foreach (var u in asaltantes) plan[u.idx] = u.coord;
            return plan;
        }

        (bool t, bool m, int mov) Datos(Dictionary<string, object?> carta)
        {
            var (t, m) = TerrenoUtil.ClaseDeTipo(M.Int(M.Get(carta, "Tipo", "tipo")));
            return (t, m, Math.Max(1, M.Int(M.Get(carta, "Movimiento", "movimiento"))));
        }

        string Hacia((int idx, string coord, Dictionary<string, object?> carta) u, string meta)
        {
            if (meta == "" || string.Equals(u.coord, meta, StringComparison.OrdinalIgnoreCase)) return u.coord;
            var (t, m, mov) = Datos(u.carta);
            return PasoBfs(u.coord, meta, mov, t, m, terreno, filas, columnas, noPisables);
        }

        if (!asaltoGeneral)
        {
            foreach (var u in asaltantes)
                plan[u.idx] = reunion != "" ? Hacia(u, reunion) : u.coord;
            return plan;
        }

        var llegan = asaltantes.Where(u =>
        {
            var (t, m, mov) = Datos(u.carta);
            return Alcanzables(u.coord, mov, t, m, terreno, filas, columnas, noPisables).Contains(objetivo);
        }).Select(u => u.idx).ToHashSet();

        bool lanzar = reunion == ""
                      || llegan.Count == asaltantes.Count
                      || turno >= turnoAsalto + Math.Max(0, esperaMax);
        foreach (var u in asaltantes)
            plan[u.idx] = lanzar
                ? (llegan.Contains(u.idx) ? objetivo : Hacia(u, objetivo))
                : Hacia(u, reunion);

        Console.WriteLine(
            $"[WZ.Historia] asalto turno {turno}: {asaltantes.Count} carta(s), {llegan.Count} llegan al cuartel " +
            (lanzar ? "→ ¡ASALTO!" : $"→ se reúnen en {reunion}"));
        return plan;
    }

    /// Pasos del camino más corto de [desde] a [hacia] sin pisar [bloqueadas]
    /// (salvo la meta), por terreno compatible. int.MaxValue si no hay camino.
    private static int DistanciaBfs(
        string desde, string hacia, bool tierra, bool mar,
        Dictionary<string, string> terreno, int filas, int columnas,
        ISet<string> bloqueadas)
    {
        if (string.Equals(desde, hacia, StringComparison.OrdinalIgnoreCase)) return 0;
        if (ParseCoord(desde) == null || ParseCoord(hacia) == null) return int.MaxValue;
        var dist = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { [desde] = 0 };
        var cola = new Queue<string>();
        cola.Enqueue(desde);
        var deltas = new (int dr, int dc)[] { (-1, 0), (1, 0), (0, -1), (0, 1) };
        while (cola.Count > 0)
        {
            var cur = cola.Dequeue();
            var p = ParseCoord(cur)!.Value;
            foreach (var (dr, dc) in deltas)
            {
                int nr = p.r + dr, nc = p.c + dc;
                if (nr < 0 || nr >= filas || nc < 1 || nc > columnas) continue;
                var n = FormatCoord(nr, nc);
                if (dist.ContainsKey(n)) continue;
                bool esMeta = string.Equals(n, hacia, StringComparison.OrdinalIgnoreCase);
                if (!esMeta && bloqueadas.Contains(n)) continue;
                if (!TerrenoUtil.Compatible(n, tierra, mar, terreno)) continue;
                dist[n] = dist[cur] + 1;
                if (esMeta) return dist[n];
                cola.Enqueue(n);
            }
        }
        return int.MaxValue;
    }

    /// Un paso de hasta [pasos] celdas por el CAMINO MÁS CORTO (BFS) hacia
    /// [hacia], sin pisar [bloqueadas] (salvo la propia meta). Si la meta no es
    /// alcanzable, va hacia la celda alcanzable más cercana a ella. Rodea
    /// obstáculos (p. ej. el túnel) donde el avance voraz se quedaría clavado.
    private static string PasoBfs(
        string desde, string hacia, int pasos, bool tierra, bool mar,
        Dictionary<string, string> terreno, int filas, int columnas,
        ISet<string> bloqueadas)
    {
        var p0 = ParseCoord(desde);
        if (p0 == null || pasos <= 0) return desde;

        var padre = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [desde] = "" };
        var cola = new Queue<string>();
        cola.Enqueue(desde);
        var deltas = new (int dr, int dc)[] { (-1, 0), (1, 0), (0, -1), (0, 1) };
        while (cola.Count > 0)
        {
            var cur = cola.Dequeue();
            if (string.Equals(cur, hacia, StringComparison.OrdinalIgnoreCase)) break;
            var p = ParseCoord(cur)!.Value;
            foreach (var (dr, dc) in deltas)
            {
                int nr = p.r + dr, nc = p.c + dc;
                if (nr < 0 || nr >= filas || nc < 1 || nc > columnas) continue;
                var n = FormatCoord(nr, nc);
                if (padre.ContainsKey(n)) continue;
                bool esMeta = string.Equals(n, hacia, StringComparison.OrdinalIgnoreCase);
                if (!esMeta && bloqueadas.Contains(n)) continue;
                if (!TerrenoUtil.Compatible(n, tierra, mar, terreno)) continue;
                padre[n] = cur;
                cola.Enqueue(n);
            }
        }

        // Meta efectiva: la propia meta o la celda alcanzada más cercana a ella.
        var meta = padre.ContainsKey(hacia)
            ? hacia
            : padre.Keys
                .OrderBy(c => DistanciaCoord(c, hacia))
                .ThenBy(c => c, StringComparer.Ordinal)
                .First();

        var camino = new List<string>();
        for (var c = meta; c != ""; c = padre[c]) camino.Add(c);
        camino.Reverse();                     // [desde, …, meta]
        int idx = Math.Min(pasos, camino.Count - 1);
        // No terminar en una celda bloqueada (la meta bloqueada solo se pisa al llegar).
        while (idx > 0 && bloqueadas.Contains(camino[idx])
               && !string.Equals(camino[idx], hacia, StringComparison.OrdinalIgnoreCase)) idx--;
        return camino[idx];
    }

    // ═════════════════════════════════════════════════════════════════════
    // BOMBARDEO (HistoriaBombardeo.cs)
    // ═════════════════════════════════════════════════════════════════════

    /// Semilla de la partida (`historia.semilla`, fijada al crearla). Las
    /// partidas anteriores a este campo usan una derivada del jugador y la
    /// historia.
    private static int SemillaPartida(Dictionary<string, object?> hist)
    {
        var s = M.Get(hist, "semilla");
        return s != null
            ? M.Int(s)
            : PlanificadorBombardeo.Semilla(M.Str(M.Get(hist, "jugadorUid")), M.Str(M.Get(hist, "id")));
    }

    /// Plan de bombardeo del turno [turno]: el PUBLICADO en la partida (campo
    /// `bombardeo`) si es de ese turno; si no (partida antigua, o un turno que
    /// no pasó por la resolución normal), se prepara ahora sin reducción.
    private static PlanBombardeo? PlanBombardeoDeTurno(
        Dictionary<string, object?> data, GuionBombardeo guion, int turno, int semilla)
    {
        var publicado = LeerPlanBombardeo(M.Get(data, "bombardeo"));
        if (publicado != null && publicado.Turno == turno) return publicado;

        var hist = M.Map(M.Get(data, "historia"));
        var tablero = M.Map(M.Get(data, "tablero"))
            .ToDictionary(kv => kv.Key, kv => M.List(kv.Value).Select(M.Map).ToList());
        var efectos = M.Map(M.Get(data, "efectosCelda"))
            .ToDictionary(kv => kv.Key, kv => M.List(kv.Value).Select(M.Map).ToList());
        Console.Error.WriteLine($"[WZ.Historia] turno {turno}: no hay plan de bombardeo publicado, se prepara ahora");
        return PrepararPlanBombardeo(data, hist, guion, turno, semilla, tablero, efectos, reduccion: 0);
    }

    /// Prepara el plan de bombardeo de [turno] a partir de un tablero y unos
    /// efectos de celda (los del INICIO de ese turno).
    private static PlanBombardeo PrepararPlanBombardeo(
        Dictionary<string, object?> data, Dictionary<string, object?> hist,
        GuionBombardeo guion, int turno, int semilla,
        Dictionary<string, List<Dictionary<string, object?>>> tablero,
        Dictionary<string, List<Dictionary<string, object?>>> efectos,
        int reduccion)
    {
        var jugadorUid = M.Str(M.Get(hist, "jugadorUid"));
        var botUid = M.Str(M.Get(hist, "botUid"));
        var obeliscos = M.Map(M.Get(data, "obeliscos"));
        var mapaH = M.Map(M.Get(hist, "mapa"));
        var terreno = new Dictionary<string, string>();
        foreach (var kv in M.Map(M.Get(mapaH, "terreno"))) terreno[kv.Key] = M.Str(kv.Value);

        var cartasJugador = CartasVisiblesJugador(
                tablero.Select(kv => (kv.Key, (IEnumerable<Dictionary<string, object?>>)kv.Value)), jugadorUid)
            .GroupBy(x => x.coord, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

        var escudos = EscudosDe(efectos.ToDictionary(kv => kv.Key, kv => (object?)kv.Value.Cast<object?>().ToList()), jugadorUid);

        return PlanificadorBombardeo.Preparar(guion, new PlanificadorBombardeo.Entrada(
            Turno: turno,
            Semilla: semilla,
            Filas: M.Int(M.Get(mapaH, "filas")),
            Columnas: M.Int(M.Get(mapaH, "columnas")),
            Transitable: c => TerrenoUtil.Compatible(c, true, false, terreno),
            CuartelJugador: M.Str(M.Get(obeliscos, jugadorUid)),
            CuartelBot: M.Str(M.Get(obeliscos, botUid)),
            CartasJugador: cartasJugador,
            EscudosJugador: escudos,
            Reduccion: reduccion));
    }

    /// Lee el campo `bombardeo` de la partida (el que escribe `PlanBombardeo.ACampo`).
    /// Devuelve null si no hay plan o si es del formato antiguo (por zonas, sin
    /// `filas`): en ese caso se prepara uno nuevo.
    private static PlanBombardeo? LeerPlanBombardeo(object? raw)
    {
        var m = M.Map(raw);
        if (m.Count == 0) return null;
        if (M.Get(m, "filas") == null) return null;
        var filas = M.Map(M.Get(m, "filas"))
            .Select(kv =>
            {
                var f = M.Map(kv.Value);
                return new FilaBombardeo(
                    kv.Key,
                    M.Int(M.Get(f, "disparos")),
                    M.Map(M.Get(f, "prob")).ToDictionary(
                        p => p.Key, p => M.Dbl(p.Value), StringComparer.OrdinalIgnoreCase));
            })
            .OrderBy(f => f.Fila, StringComparer.Ordinal)
            .ToList();
        var desact = M.List(M.Get(m, "desactivadoras")).Select(M.Map).Select(d => new Desactivadora(
                M.Str(M.Get(d, "coord")), M.Int(M.Get(d, "hastaTurno")), M.Bool(M.Get(d, "centro"))))
            .Where(d => d.Coord != "")
            .ToList();
        return new PlanBombardeo(
            M.Int(M.Get(m, "turno")), M.Int(M.Get(m, "disparosBase")), M.Int(M.Get(m, "reduccion")),
            filas, desact);
    }

    // ── Bombardeo: plan sorteado → disparos lejanos del bot ──────────────────
    // Sortea el plan del turno fila a fila (en cada fila caen exactamente sus
    // disparos) y convierte cada impacto en una acción de disparo lejano del
    // bot, con el mismo shape que una carta de acción jugada desde la mano
    // (`cartaAccionId`). El servidor la valida contra el catálogo (la carta
    // exclusiva cuesta 0) y la resuelve como cualquier disparo: tras el
    // movimiento, matando todo lo que haya en la celda.
    //
    // Los impactos que caen donde TERMINA una carta del bot este turno se
    // REDIRIGEN a otra celda libre de la misma fila. El cuartel del bot y los
    // escudos del jugador ya tienen probabilidad 0 en el plan (y, si no, el
    // propio escudo bloquearía el disparo).
    private static List<object?> ConstruirBombardeo(
        GuionBombardeo guion, PlanBombardeo plan, int semilla,
        string cuartelBot, IEnumerable<string> celdasBot, string botUid)
    {
        var acciones = new List<object?>();
        if (string.IsNullOrWhiteSpace(guion.CartaArtilleriaId)) return acciones;

        var prohibidas = new HashSet<string>(celdasBot, StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(cuartelBot)) prohibidas.Add(cuartelBot);
        var sorteo = PlanificadorBombardeo.Sortear(plan, semilla, prohibidas);

        foreach (var coord in sorteo.Impactos)
        {
            acciones.Add(new Dictionary<string, object?>
            {
                ["habilidadId"] = HabilidadBombardeo,
                ["uid"] = botUid,
                ["zona"] = ZonaHistoriaBot,
                ["origen"] = cuartelBot,
                ["objetivos"] = new List<object?> { coord },
                ["turno"] = plan.Turno,
                ["costePagado"] = 0,
                ["cartaAccionId"] = guion.CartaArtilleriaId,
            });
        }

        Console.WriteLine(
            $"[WZ.Historia] bombardeo turno {plan.Turno}: {plan.DisparosPorFila} por fila " +
            $"(base {plan.DisparosBase} − {plan.Reduccion}) · {sorteo.Impactos.Count} impacto(s), " +
            $"{sorteo.Redirigidos} redirigido(s) por el bot, {sorteo.Perdidos} perdido(s) · " +
            $"[{string.Join(",", sorteo.Impactos)}]");
        return acciones;
    }

    /// Tras RESOLVER el turno [turno] (lo llama WarZeroService, paso 7b):
    ///   1. Cuenta las casillas desactivadoras de ese turno que han quedado
    ///      ocupadas por el jugador y, en las que no tenían ya un escudo suyo,
    ///      FORMA un escudo del jugador (se añade a [efectosFinal]).
    ///   2. Prepara el plan de bombardeo del turno SIGUIENTE, con 1 disparo
    ///      menos EN CADA FILA por cada desactivadora ocupada.
    /// Devuelve el documento a guardar en `bombardeo`, o null si la historia no
    /// tiene bombardeo.
    internal static Dictionary<string, object?>? PrepararBombardeoTrasResolver(
        Dictionary<string, object?> data,
        Dictionary<string, List<Dictionary<string, object?>>> tableroFinal,
        Dictionary<string, List<Dictionary<string, object?>>> efectosFinal,
        int turno)
    {
        var hist = M.Map(M.Get(data, "historia"));
        var historiaId = M.Str(M.Get(hist, "id"));
        var guion = HistoriaBombardeos.Get(historiaId);
        if (guion == null) return null;

        var jugadorUid = M.Str(M.Get(hist, "jugadorUid"));
        int semilla = SemillaPartida(hist);
        var planActual = PlanBombardeoDeTurno(data, guion, turno, semilla);

        int ocupadas = 0;
        var cfg = guion.Desactivadoras;
        if (cfg != null && planActual != null)
        {
            foreach (var d in planActual.Desactivadoras)
            {
                if (!tableroFinal.TryGetValue(d.Coord, out var cartas)) continue;
                if (!cartas.Any(c => CartaHelper.OwnerUid(c) == jugadorUid && !CartaHelper.EsClon(c))) continue;
                ocupadas++;

                if (!efectosFinal.TryGetValue(d.Coord, out var efs))
                    efectosFinal[d.Coord] = efs = new List<Dictionary<string, object?>>();
                if (efs.Any(ef => EsEscudoDe(ef, jugadorUid))) continue;
                efs.Add(new Dictionary<string, object?>
                {
                    ["tipo"] = "escudo",
                    ["turnosRestantes"] = (long)cfg.EscudoTurnos,
                    ["magnitud"] = (long)cfg.EscudoMagnitud,
                    ["origenUid"] = jugadorUid,
                });
                Console.WriteLine(
                    $"[WZ.Historia] {historiaId} turno {turno}: desactivadora {d.Coord} ocupada → escudo de {cfg.EscudoTurnos} turnos");
            }
        }

        int reduccion = cfg == null ? 0 : ocupadas * cfg.ReduccionPorCasilla;
        var siguiente = PrepararPlanBombardeo(
            data, hist, guion, turno + 1, semilla, tableroFinal, efectosFinal, reduccion);
        Console.WriteLine(
            $"[WZ.Historia] {historiaId}: plan del turno {turno + 1} → {siguiente.DisparosPorFila} disparo(s) por fila, " +
            $"{siguiente.Disparos} en total (base {siguiente.DisparosBase} − {reduccion} por {ocupadas} desactivadora(s)) · " +
            $"desactivadoras [{string.Join(",", siguiente.Desactivadoras.Select(d => d.Coord))}]");
        return siguiente.ACampo();
    }

    // ── Fin de batalla por las reglas PROPIAS de la historia ─────────────────
    // Lo llama la resolución del turno (WarZeroService.ResolverTurnoCoreEnTx,
    // paso 8b) cuando la partida aún no ha terminado por conquista. Devuelve el
    // uid del GANADOR si la batalla termina en esta resolución, o null.
    //
    //   1) SUPERVIVENCIA (de siempre): si hay `turnosSupervivencia` y el jugador
    //      sigue vivo al cerrar ese turno, GANA el jugador.
    //   2) ANIQUILACIÓN (`historia.derrotaSinCartas`): si al jugador no le queda
    //      ninguna carta en el tablero (los clones no cuentan) —ni en la mano,
    //      en partida normal—, GANA el bot.
    //
    // [tableroFinal] es el tablero YA resuelto (tras disparos, combate y la
    // limpieza de eliminados). [eliminadosTotal] incluye los eliminados de esta
    // misma resolución.
    internal static string? EvaluarFinHistoria(
        Dictionary<string, object?> data,
        Dictionary<string, List<Dictionary<string, object?>>> tableroFinal,
        int turno,
        ISet<string> eliminadosTotal)
    {
        var hist = M.Map(M.Get(data, "historia"));
        var jugadorUid = M.Str(M.Get(hist, "jugadorUid"));
        var botUid = M.Str(M.Get(hist, "botUid"));
        if (jugadorUid == "" || eliminadosTotal.Contains(jugadorUid)) return null;

        // 00) DUELO DE GENERALES (HistoriaDuelo.cs). Mismas reglas en los dos
        //     sentidos (el jugador puede llevar a los generales o al jefe):
        //       · cae un general (no está en el tablero) → gana el dueño del jefe;
        //       · cae el jefe → gana el dueño de los generales;
        //       · se cierra el turno límite con el jefe vivo → gana el del jefe
        //         (o el de los generales si `JefeGanaAlLimite` es false).
        //     El duelo decide solo: el resto de reglas no se evalúan.
        var cfgDuelo = HistoriaDuelos.Get(M.Str(M.Get(hist, "id")));
        var duelo = cfgDuelo != null ? EstadoDuelo.DesdeCampo(M.Get(data, "duelo")) : null;
        if (cfgDuelo != null && duelo != null && botUid != "")
        {
            MotorDuelo.CompletarDuenos(cfgDuelo, duelo, jugadorUid, botUid);
            var vivas = tableroFinal.Values.SelectMany(l => l)
                .Select(c => M.Str(M.Get(c, "instanceId")))
                .ToHashSet(StringComparer.Ordinal);
            bool Vivo(string rol) { var id = duelo.IdDe(rol); return id == null || vivas.Contains(id); }
            string idBatalla = M.Str(M.Get(hist, "id"));
            if (!Vivo(EstadoDuelo.RolCazador) || !Vivo(EstadoDuelo.RolVerdugo))
            {
                Console.WriteLine($"[WZ.Historia] {idBatalla} turno {turno}: ha caído un general → gana {duelo.JefeUid}");
                return duelo.JefeUid;
            }
            if (!Vivo(EstadoDuelo.RolJefe))
            {
                Console.WriteLine($"[WZ.Historia] {idBatalla} turno {turno}: ha caído el jefe → gana {duelo.GeneralesUid}");
                return duelo.GeneralesUid;
            }
            if (cfgDuelo.TurnoLimite > 0 && turno >= cfgDuelo.TurnoLimite)
            {
                var ganador = cfgDuelo.JefeGanaAlLimite ? duelo.JefeUid : duelo.GeneralesUid;
                Console.WriteLine($"[WZ.Historia] {idBatalla} turno {turno}: turno límite sin caídos → gana {ganador}");
                return ganador;
            }
            return null;
        }

        // 0) CARTAS CLAVE (`historia.vipIds`): si ha muerto alguna, gana el bot.
        //    Va primero: perder a Alvaroth o a Soren es derrota aunque ese
        //    mismo turno caiga la última carta enemiga.
        var vipIds = M.List(M.Get(hist, "vipIds")).Select(M.Str).Where(s => s != "").ToList();
        if (vipIds.Count > 0 && botUid != "" && !eliminadosTotal.Contains(botUid))
        {
            var vivas = tableroFinal.Values
                .SelectMany(l => l)
                .Where(c => CartaHelper.OwnerUid(c) == jugadorUid && !CartaHelper.EsClon(c))
                .Select(c => M.Str(M.Get(c, "instanceId")))
                .ToHashSet(StringComparer.Ordinal);
            var caida = vipIds.FirstOrDefault(id => !vivas.Contains(id));
            if (caida != null)
            {
                Console.WriteLine(
                    $"[WZ.Historia] {M.Str(M.Get(hist, "id"))} turno {turno}: ha caído una carta clave ({caida}) → gana el bot");
                return botUid;
            }
        }

        // 1) Supervivencia.
        var turnosSup = M.Int(M.Get(hist, "turnosSupervivencia"));
        if (turnosSup > 0 && turno >= turnosSup) return jugadorUid;

        // 1b) ANIQUILACIÓN DEL BOT (`historia.victoriaSinEnemigos`): no queda
        //     ninguna carta del bot y ya no le quedan oleadas por llegar.
        if (M.Bool(M.Get(hist, "victoriaSinEnemigos")) && botUid != "")
        {
            int ultimaOleada = M.Int(M.Get(hist, "ultimaOleada"));
            bool quedanBot = tableroFinal.Values.Any(lst => lst.Any(c => CartaHelper.OwnerUid(c) == botUid));
            if (!quedanBot && turno >= ultimaOleada)
            {
                Console.WriteLine(
                    $"[WZ.Historia] {M.Str(M.Get(hist, "id"))} turno {turno}: no quedan tropas enemigas → gana el jugador");
                return jugadorUid;
            }
        }

        // 1c) TURNO LÍMITE (`historia.turnoLimite`, duelo): si el jugador no ha
        //     ganado al cerrar ese turno, gana el bot.
        int turnoLimite = M.Int(M.Get(hist, "turnoLimite"));
        if (turnoLimite > 0 && turno >= turnoLimite && botUid != "")
        {
            Console.WriteLine(
                $"[WZ.Historia] {M.Str(M.Get(hist, "id"))} turno {turno}: se acabó el tiempo → gana el bot");
            return botUid;
        }

        // 2) Aniquilación.
        if (M.Bool(M.Get(hist, "derrotaSinCartas"))
            && botUid != "" && !eliminadosTotal.Contains(botUid))
        {
            bool quedanEnTablero = tableroFinal.Values.Any(lst => lst.Any(c =>
                CartaHelper.OwnerUid(c) == jugadorUid && !CartaHelper.EsClon(c)));
            if (quedanEnTablero) return null;

            if (M.Bool(M.Get(hist, "conMano")))
            {
                var stats = M.Map(M.Get(data, "statsPartida"));
                var mano = M.List(M.Get(M.Map(M.Get(stats, jugadorUid)), "mano"))
                    .Select(M.Str).Where(s => s != "").ToList();
                if (mano.Count > 0) return null;
            }

            Console.WriteLine(
                $"[WZ.Historia] {M.Str(M.Get(hist, "id"))} turno {turno}: el jugador se ha quedado sin cartas → gana el bot");
            return botUid;
        }

        return null;
    }

    // ── Ruta de GRUPO (itinerario grabado en la carta) ───────────────────────
    // Campos que cada refuerzo scriptado lleva encima dentro del tablero:
    //   · `rutaHistoria` → lista ordenada de coords (literales o simbólicas).
    //   · `rutaPaso`     → índice del paso que la unidad tiene pendiente.
    // Van en el propio documento de la carta (y no en una tabla aparte) para que
    // el itinerario sobreviva a la resolución del turno igual que los efectos,
    // sin necesidad de identificar grupos ni de recordar nada entre turnos.
    private const string CampoRuta = "rutaHistoria";
    private const string CampoRutaPaso = "rutaPaso";

    /// Graba el itinerario de un grupo en una carta recién clonada. Sin ruta
    /// (null o vacía) no escribe nada: la carta avanza como siempre.
    private static void GrabarRutaGrupo(
        Dictionary<string, object?> carta, IReadOnlyList<string>? ruta)
    {
        if (ruta == null || ruta.Count == 0) return;
        carta[CampoRuta] = ruta
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => (object?)p.Trim())
            .ToList();
        carta[CampoRutaPaso] = 0L;
    }

    /// Copia la ruta (y su progreso) de una carta a otra. Se usa al evolucionar
    /// en tablero, donde la unidad se sustituye por un clon de su evolución.
    private static void CopiarRutaGrupo(
        Dictionary<string, object?> desde, Dictionary<string, object?> hacia)
    {
        if (desde.TryGetValue(CampoRuta, out var r) && r != null) hacia[CampoRuta] = r;
        if (desde.TryGetValue(CampoRutaPaso, out var p) && p != null) hacia[CampoRutaPaso] = p;
    }

    /// Meta de este turno según la ruta grabada en [carta], o null si la carta
    /// no lleva ruta (entonces manda el avance normal / los puntos de paso).
    ///
    /// Regla de avance del itinerario: si la unidad EMPIEZA el turno encima del
    /// paso pendiente, ese paso se da por cumplido y se pasa al siguiente. Por
    /// eso repetir una coord en la ruta ("A4", "A4") significa "quédate ahí un
    /// turno más", y por eso una unidad lenta que no llegó sigue yendo al mismo
    /// paso el turno siguiente en vez de saltárselo. Agotada la lista, la meta
    /// pasa a ser [alTerminar]: el cuartel del jugador en un bot atacante, o la
    /// celda actual (quedarse) en un bot defensor.
    private static string? MetaDeRutaGrupo(
        Dictionary<string, object?> carta, string coordActual, string objetivo,
        Func<string, string> resolver, string alTerminar)
    {
        var pasos = M.List(M.Get(carta, CampoRuta)).Select(M.Str)
            .Where(s => s != "").ToList();
        if (pasos.Count == 0) return null;

        int paso = Math.Max(0, M.Int(M.Get(carta, CampoRutaPaso)));
        if (paso < pasos.Count && resolver(pasos[paso]) == coordActual)
        {
            paso++;
            carta[CampoRutaPaso] = (long)paso;
        }

        if (paso >= pasos.Count)
        {
            // Itinerario terminado: se limpia el rastro para no arrastrar campos
            // muertos en el documento de la partida.
            carta.Remove(CampoRuta);
            carta.Remove(CampoRutaPaso);
            return alTerminar;
        }

        var meta = resolver(pasos[paso]);
        return meta != "" ? meta : objetivo;
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

    // ═════════════════════════════════════════════════════════════════════
    // PARTIDA NORMAL DE HISTORIA (ModoHistoria.PartidaNormal)
    // ═════════════════════════════════════════════════════════════════════

    /// Catálogo de `Cartas` (caché compartido) + cartas EXCLUSIVAS de historia.
    /// Usar SOLO donde se resuelven cartas por id (crear partida de historia,
    /// cerrar turno, validar acciones, GET /warzero/cartas): así las exclusivas
    /// nunca aparecen en tienda, sobres, colección ni mazos por defecto. Los ids
    /// exclusivos llevan prefijo `hist_excl_` y no pueden pisar uno real.
    internal async Task<Dictionary<string, Dictionary<string, object?>>> ObtenerCatalogoCartasConHistoriaAsync()
    {
        var catalogo = await ObtenerCatalogoCartasAsync();   // ya es una copia mutable
        foreach (var kv in HistoriaCatalogo.CatalogoExclusivas())
            catalogo[kv.Key] = kv.Value;
        return catalogo;
    }

    /// Reparte la mano inicial de un bando a partir de su MAZO FIJO de historia.
    /// Devuelve (mano, mazoRestante, mazoPool) como ids, con la misma semántica
    /// que RepartirManoAsync en PvP: el pool es el mazo completo expandido por
    /// cantidad (pool de robo de fin de turno, CON repetición) y la mano son las
    /// primeras `TamanioManoInicial` cartas del pool barajado. A diferencia del
    /// PvP NO se filtra por ejército (el mazo puede mezclar Humanos y Demonios);
    /// sí se excluyen evoluciones/especiales y cartas que no estén en catálogo.
    private static (List<string> mano, List<string> resto, List<string> pool)
        RepartirMazoHistoria(
            BandoHistoria bando,
            Dictionary<string, Dictionary<string, object?>> catalogo,
            Random rnd)
    {
        var pool = new List<string>();
        foreach (var c in bando.Mazo ?? Array.Empty<CartaHistoria>())
        {
            if (!catalogo.TryGetValue(c.CartaId, out var cd))
            {
                Console.Error.WriteLine($"[WZ.Historia] carta de mazo desconocida {c.CartaId}, se omite");
                continue;
            }
            int cond = M.Int(M.Get(cd, "Condicion", "condicion"));
            if (cond == 1 || cond == 5)
            {
                Console.Error.WriteLine($"[WZ.Historia] {c.CartaId} es evolución/especial, no puede ir en el mazo");
                continue;
            }
            for (int q = 0; q < Math.Max(1, c.Cantidad); q++) pool.Add(c.CartaId);
        }

        var barajado = pool.OrderBy(_ => rnd.Next()).ToList();
        var mano = barajado.Take(TamanioManoInicial).ToList();
        var resto = barajado.Skip(TamanioManoInicial).ToList();
        return (mano, resto, pool);
    }

    // ── IA del bot en PARTIDA NORMAL de historia ─────────────────────────────
    // En vez del avance ciego del asedio, el bot juega como un bot real de PvP:
    // se monta su BotContext a partir del documento de la partida (sin leer
    // Firestore: mapa cacheado en `historia.mapa`, catálogo precargado) y se le
    // pide la jugada a EstrategaSoftmaxStrategy con el perfil de la historia.
    //
    // La jugada se aplica igual que hace WarZeroBot.JugarTurnoAsync, pero
    // DENTRO de la transacción del cierre: se descuenta la energía pre-pagada
    // (despliegues/evoluciones; las acciones las cobra la resolución) y se
    // sustituye la mano por la resultante directamente en `data.statsPartida`.
    // ResolverTurnoCoreEnTx reconstruye statsPartida a partir de `data`, así
    // que esos cambios se persisten en el mismo commit que la resolución, y el
    // robo de fin de turno (paso 5c) se hace ya sobre la mano correcta.
    //
    // Si la estrategia falla, cierre seguro: el ejército del bot se queda donde
    // está (mejor perder un turno que dejar la partida colgada).
    internal static Dictionary<string, object?> ConstruirJugadaBotHistoriaNormal(
        Dictionary<string, object?> data, string botUid, int turno,
        Dictionary<string, Dictionary<string, object?>>? catalogoCartas)
    {
        var catalogo = catalogoCartas ?? new Dictionary<string, Dictionary<string, object?>>();
        var hist = M.Map(M.Get(data, "historia"));

        var statsPartida = M.Map(M.Get(data, "statsPartida"));
        var miStat = M.Map(M.Get(statsPartida, botUid));
        int energia = M.Int(M.Get(miStat, "energies"));
        var mano = M.List(M.Get(miStat, "mano")).Select(M.Str).Where(s => s != "").ToList();

        var cuartel = M.Str(M.Get(M.Map(M.Get(data, "obeliscos")), botUid));

        Dictionary<string, object?> Jugada(Dictionary<string, object?> celdas, List<object?> acciones) => new()
        {
            ["uid"] = botUid,
            ["turno"] = turno,
            ["celdas"] = celdas,
            ["timestamp"] = Timestamp.FromDateTime(DateTime.UtcNow),
            ["acciones"] = acciones,
        };

        // Sin cuartel (ya conquistado): el bot no juega nada.
        if (cuartel == "")
            return Jugada(ArrastrarEjercitoHistoria(data, botUid), new List<object?>());

        // ── Mapa cacheado en la creación de la partida ───────────────────────
        var mapaH = M.Map(M.Get(hist, "mapa"));
        int filas = M.Int(M.Get(mapaH, "filas"));
        int columnas = M.Int(M.Get(mapaH, "columnas"));
        var terreno = new Dictionary<string, string>();
        foreach (var kv in M.Map(M.Get(mapaH, "terreno"))) terreno[kv.Key] = M.Str(kv.Value);
        var isla = M.List(M.Get(mapaH, "islaCentral")).Select(M.Str)
            .Where(c => c != "").ToHashSet();
        var continentes = new Dictionary<string, List<string>>();
        foreach (var kv in M.Map(M.Get(mapaH, "continentes")))
            continentes[kv.Key] = M.List(kv.Value).Select(M.Str).Where(c => c != "").ToList();

        // ── Cartas de la mano y evoluciones referenciadas en el tablero ──────
        var catalogoMano = new Dictionary<string, Dictionary<string, object?>>();
        foreach (var id in mano.Distinct())
            if (catalogo.TryGetValue(id, out var cd))
                catalogoMano[id] = new Dictionary<string, object?>(cd);

        var evoluciones = new Dictionary<string, Dictionary<string, object?>>();
        foreach (var celda in M.Map(M.Get(data, "tablero")).Values)
            foreach (var c in M.List(celda))
            {
                var cm = M.Map(c);
                var ie = M.Str(M.Get(cm, "IdEvolucion", "idEvolucion"));
                if (ie == "" || M.Int(M.Get(cm, "Evolucion", "evolucion")) <= 0) continue;
                if (!evoluciones.ContainsKey(ie) && catalogo.TryGetValue(ie, out var evo))
                    evoluciones[ie] = new Dictionary<string, object?>(evo);
            }

        var (rayos, rayosTurnos) = LeerRayosHistoria(data);

        int ejercitoId = 0;
        foreach (var j in M.List(M.Get(data, "jugadores")))
        {
            var jm = M.Map(j);
            if (M.Str(M.Get(jm, "uid")) == botUid) { ejercitoId = M.Int(M.Get(jm, "ejercitoId")); break; }
        }

        var ctx = new BotContext
        {
            EjercitoId = ejercitoId,
            Evoluciones = evoluciones,
            // El bot de historia solo juega su mazo: no compra generales.
            GeneralesDisponibles = new List<Dictionary<string, object?>>(),
            Estado = data,
            BotUid = botUid,
            Turno = turno,
            Cuartel = cuartel,
            Energia = energia,
            Mano = mano,
            CatalogoMano = catalogoMano,
            Zona = ZonaHistoriaBot,
            Terreno = terreno,
            Filas = filas,
            Columnas = columnas,
            IslaCentral = isla,
            Continentes = continentes,
            Rayos = rayos,
            RayosTurnos = rayosTurnos,
        };

        var perfil = PerfilBot.Parse(
            M.Str(M.Get(hist, "botDificultad")), M.Str(M.Get(hist, "botEstilo")));
        var estrategia = new EstrategaSoftmaxStrategy(
            new WarZeroBotOptions { ComprarGenerales = false }, perfil);

        BotMove jugada;
        try
        {
            jugada = estrategia.DecidirJugada(ctx);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[WZ.Historia] IA del bot falló en turno {turno}, cierre seguro: {ex}");
            return Jugada(ArrastrarEjercitoHistoria(data, botUid), new List<object?>());
        }

        // ── Aplicar el pre-pago y la mano resultante en `data` ───────────────
        // (negativo si sacrificó: le devuelve energía, igual que en PvP).
        miStat["energies"] = (long)Math.Max(0, energia - jugada.EnergiaGastada);
        miStat["mano"] = jugada.ManoResultante.Cast<object?>().ToList();
        miStat["modoBot"] = estrategia.UltimoModo;
        if (!string.IsNullOrEmpty(jugada.EspecialComprada))
        {
            var compradas = M.List(M.Get(miStat, "especialesCompradas"))
                .Select(M.Str).Where(x => x != "").ToList();
            if (!compradas.Contains(jugada.EspecialComprada))
                compradas.Add(jugada.EspecialComprada);
            miStat["especialesCompradas"] = compradas.Cast<object?>().ToList();
        }
        statsPartida[botUid] = miStat;
        data["statsPartida"] = statsPartida;

        Console.WriteLine(
            $"[WZ.Historia] bot turno {turno}: celdas={jugada.Celdas.Values.Sum(l => l.Count)} " +
            $"acciones={jugada.Acciones.Count} prepagado={jugada.EnergiaGastada} modo={estrategia.UltimoModo}");

        // Normalizar a los tipos que espera la resolución (Dictionary<string,
        // object?> / List<object?>): M.Map/M.List no reconocen los genéricos
        // tipados de BotMove y los tratarían como vacíos.
        var celdasOut = M.Map(NormalizarHistoria(jugada.Celdas));
        var accionesOut = M.List(NormalizarHistoria(jugada.Acciones));
        return Jugada(celdasOut, accionesOut);
    }

    /// Cierre seguro: todas las cartas del bot se quedan donde están.
    private static Dictionary<string, object?> ArrastrarEjercitoHistoria(
        Dictionary<string, object?> data, string botUid)
    {
        var celdas = new Dictionary<string, object?>();
        foreach (var kv in M.Map(M.Get(data, "tablero")))
            foreach (var raw in M.List(kv.Value))
            {
                var carta = M.Map(raw);
                if (M.Str(M.Get(carta, "ownerUid")) != botUid) continue;
                List<object?> lst;
                if (celdas.TryGetValue(kv.Key, out var l) && l is List<object?> existente)
                    lst = existente;
                else
                {
                    lst = new List<object?>();
                    celdas[kv.Key] = lst;
                }
                lst.Add(new Dictionary<string, object?>(carta));
            }
        return celdas;
    }

    /// Rayos activos (coord) y sus turnos restantes, igual que WarZeroBot.
    private static (HashSet<string> rayos, Dictionary<string, int> turnos)
        LeerRayosHistoria(Dictionary<string, object?> data)
    {
        var turnos = new Dictionary<string, int>();
        var raw = M.Get(data, "rayos");
        if (raw is System.Collections.IEnumerable en && raw is not string)
            foreach (var r in en)
            {
                var m = M.Map(r);
                var c = M.Str(M.Get(m, "coord"));
                if (c == "") continue;
                int t = M.Int(M.Get(m, "turnosRestantes"));
                turnos[c] = t > 0 ? t : 1;
            }
        if (turnos.Count == 0)
        {
            var c = M.Str(M.Get(M.Map(M.Get(data, "rayo")), "coord"));
            if (c != "") turnos[c] = 1;
        }
        return (turnos.Keys.ToHashSet(), turnos);
    }

    /// Convierte recursivamente diccionarios/listas tipados a los tipos
    /// "Dart-like" del servidor (Dictionary<string, object?> y List<object?>).
    private static object? NormalizarHistoria(object? o)
    {
        switch (o)
        {
            case null:
                return null;
            case string:
                return o;
            case System.Collections.IDictionary dict:
                {
                    var res = new Dictionary<string, object?>();
                    foreach (System.Collections.DictionaryEntry e in dict)
                        res[e.Key?.ToString() ?? ""] = NormalizarHistoria(e.Value);
                    return res;
                }
            case System.Collections.IEnumerable en:
                {
                    var res = new List<object?>();
                    foreach (var x in en) res.Add(NormalizarHistoria(x));
                    return res;
                }
            default:
                return o;
        }
    }

    // ── ABANDONO ─────────────────────────────────────────────────────────────
    // Salir de una batalla de historia la BORRA: no se puede retomar (tampoco
    // sale en «mis partidas») y hay que empezar la historia de nuevo. Lo llama
    // el cliente al salir de la partida (POST /warzero/historia/abandonar).

    /// Borra la batalla de historia [req.LobbyId] de [req.Uid] si sigue en
    /// curso. Idempotente: si ya no existe o ya terminó, no hace nada (una
    /// batalla terminada la gestiona el cartel de fin: siguiente parte,
    /// reintentar o volver).
    public async Task<Dictionary<string, object?>> AbandonarHistoriaAsync(AbandonarHistoriaRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.LobbyId) || string.IsNullOrWhiteSpace(req.Uid))
            return new() { ["ok"] = false, ["error"] = "lobbyId y uid son obligatorios" };

        var db = _fs.Db;
        var lobbyRef = db.Collection("Partidas").Document(req.LobbyId);
        var borrada = await db.RunTransactionAsync(async tx =>
        {
            var snap = await tx.GetSnapshotAsync(lobbyRef);
            if (!snap.Exists) return false;
            var data = M.Map(M.FromFs(snap.ToDictionary()));
            if (!EsHistoriaAbiertaDe(data, req.Uid)) return false;
            tx.Delete(lobbyRef);
            return true;
        });

        if (borrada)
            Console.WriteLine($"[WZ.Historia] {req.LobbyId}: abandonada por {req.Uid} → borrada");
        return new() { ["ok"] = true, ["abandonada"] = borrada };
    }

    /// Borra las batallas de historia de [uid] que sigan EN CURSO (salvo
    /// [exceptoDocId]). Las partidas de historia tienen docId determinista
    /// `hist_{uid}_{historiaId}`, así que basta leer una por batalla del
    /// catálogo: sin consultas ni índices. Best-effort: un fallo aquí no debe
    /// impedir empezar la batalla nueva.
    private async Task BorrarHistoriasAbiertasAsync(string uid, string exceptoDocId)
    {
        try
        {
            var db = _fs.Db;
            var refs = HistoriaCatalogo.Todas
                .Select(h => $"hist_{uid}_{h.Id}")
                .Where(id => id != exceptoDocId)
                .Select(id => db.Collection("Partidas").Document(id))
                .ToList();
            var snaps = await Task.WhenAll(refs.Select(r => r.GetSnapshotAsync()));
            for (int i = 0; i < refs.Count; i++)
            {
                if (!snaps[i].Exists) continue;
                var data = M.Map(M.FromFs(snaps[i].ToDictionary()));
                if (!EsHistoriaAbiertaDe(data, uid)) continue;
                await refs[i].DeleteAsync();
                Console.WriteLine($"[WZ.Historia] {refs[i].Id}: borrada al empezar otra batalla");
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("[WZ.Historia] borrar batallas abiertas falló: " + ex);
        }
    }

    /// True si [data] es una batalla de historia de [uid] que no ha terminado.
    private static bool EsHistoriaAbiertaDe(Dictionary<string, object?> data, string uid) =>
        M.Bool(M.Get(data, "esHistoria"))
        && M.Str(M.Get(data, "estado")) != "finalizada"
        && M.Str(M.Get(M.Map(M.Get(data, "historia")), "jugadorUid")) == uid;

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
        if (M.Bool(M.Get(hist, "esReto"))) return;                  // un reto no desbloquea historias

        // Id del documento de la colección `Historias` a marcar como desbloqueada:
        //   1. `DesbloqueaHistoriaId` del catálogo, si lo define (forzado a mano).
        //   2. Si no, el documento que el editor creó para ese ejército y orden
        //      (campos `Ejercito` + `Orden`), que es el que lista la pantalla de
        //      Historias del jugador. Así no hay que copiar ids a mano.
        //   3. Como último recurso, el id de la batalla (p. ej. "humanos_3").
        var desbloqueaId = M.Str(M.Get(hist, "desbloqueaId"));
        if (desbloqueaId == "")
            desbloqueaId = await BuscarDocHistoriaAsync(
                M.Int(M.Get(hist, "ejercitoCampana")), M.Int(M.Get(hist, "orden")));
        if (desbloqueaId == "") desbloqueaId = M.Str(M.Get(hist, "id"));
        if (desbloqueaId == "") return;

        Console.WriteLine("[WZ.Historia] " + M.Str(M.Get(hist, "id")) + " ganada por " +
                          jugadorUid + " → desbloquea Historias/" + desbloqueaId);
        await DesbloquearHistoriaAsync(jugadorUid, desbloqueaId);
    }

    /// Id del documento de `Historias` con ese ejército y orden ("" si no hay).
    /// La colección es pequeña (10 por ejército): se lee entera y se filtra en
    /// memoria, aceptando `Ejercito`/`ejercito` y `Orden`/`orden` y números
    /// guardados como int o double (igual que el editor y HistoriasAsync).
    private async Task<string> BuscarDocHistoriaAsync(int ejercito, int orden)
    {
        if (ejercito <= 0 || orden <= 0) return "";
        try
        {
            var snap = await _fs.Db.Collection("Historias").GetSnapshotAsync();
            foreach (var doc in snap.Documents)
            {
                var d = M.Map(M.ToJsonSafe(doc.ToDictionary()));
                if (M.Int(M.Get(d, "Ejercito", "ejercito")) == ejercito &&
                    M.Int(M.Get(d, "Orden", "orden")) == orden)
                    return doc.Id;
            }
            Console.Error.WriteLine(
                $"[WZ.Historia] no hay documento en Historias con Ejercito={ejercito} Orden={orden}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("[WZ.Historia] buscar doc de Historias falló: " + ex);
        }
        return "";
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
        5 => "Trans-Universales",
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

/// Cuerpo de POST /warzero/historia/abandonar.
public class AbandonarHistoriaRequest
{
    public string Uid { get; set; } = "";
    public string LobbyId { get; set; } = "";
}