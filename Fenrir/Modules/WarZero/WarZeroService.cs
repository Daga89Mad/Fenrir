using System.Text.Json;
using Google.Cloud.Firestore;

// Alias de tipo (ámbito de fichero): mismos que en WarZeroLogic.cs. Los alias
// `using` no se heredan entre archivos, por eso hay que repetirlos aquí.
using Tablero = System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<System.Collections.Generic.Dictionary<string, object?>>>;
using EfectosCelda = System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<System.Collections.Generic.Dictionary<string, object?>>>;

// ─────────────────────────────────────────────────────────────────────────────
// WarZeroService.cs
//
// Orquesta el cierre de turno contra Firestore. Toda la operación (registrar el
// cierre de un jugador y, si cerraron todos, resolver el turno) ocurre dentro de
// UNA transacción, lo que elimina la carrera de "quién resuelve" que había en el
// cliente.
//
// Documento Partidas/{lobbyId}:
//   turnoActual, cerradoPor[], movimientosTurno{uid}, tablero{}, statsPartida{},
//   obeliscos{uid->coord}, jugadores[], jugadoresEliminados[], efectosCelda{},
//   rayo{}, mapaId, historialCombates[], estado, ganadorUid
// ─────────────────────────────────────────────────────────────────────────────

public partial class WarZeroService
{
    private readonly WarZeroFirestore _fs;

    public WarZeroService(WarZeroFirestore fs) => _fs = fs;

    public GameStatus GetStatus() => new("EU-1", 42);

    public async Task<CerrarTurnoResponse> CerrarTurnoAsync(CerrarTurnoRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.LobbyId) || string.IsNullOrWhiteSpace(req.Uid))
            return new CerrarTurnoResponse { Mensaje = "lobbyId y uid son obligatorios" };

        // Convertir el JSON entrante a CLR (mismo formato que en Dart).
        var celdasClr = req.Celdas.ValueKind == JsonValueKind.Object
            ? M.Map(M.FromJson(req.Celdas))
            : new Dictionary<string, object?>();
        var accionesIncoming = req.Acciones.ValueKind == JsonValueKind.Array
            ? M.List(M.FromJson(req.Acciones)).Select(M.Map).ToList()
            : new List<Dictionary<string, object?>>();

        var db = _fs.Db;
        var lobbyRef = db.Collection("Partidas").Document(req.LobbyId);

        var resp = await db.RunTransactionAsync(async tx =>
        {
            var snap = await tx.GetSnapshotAsync(lobbyRef);
            if (!snap.Exists)
                return new CerrarTurnoResponse { Mensaje = "La partida no existe" };

            var data = M.Map(M.FromFs(snap.ToDictionary()));
            var turnoDb = M.Int(M.Get(data, "turnoActual"));

            // Turno desincronizado: otro proceso ya avanzó. No escribimos.
            if (turnoDb != req.Turno)
                return new CerrarTurnoResponse
                {
                    Resuelto = false,
                    TurnoActual = turnoDb,
                    Mensaje = "El turno ya había avanzado",
                };

            // ── Guard autoritativo de cartas ESTÁTICAS ────────────────────────
            // Una estática (Condicion == 3) solo puede colocarse en una celda
            // donde ESTE jugador ya tenía una carta el turno anterior (mismo
            // criterio que el cliente en _tryPlaceFromHand). Aquí es la fuente de
            // la verdad, y la comprobación es por `ownerUid`: es CORRECTA aunque
            // haya ejércitos repetidos (dos jugadores con el mismo ejército no
            // comparten uid, así que uno no puede anclar su estática en la celda
            // del otro). Se descartan las estáticas colocadas en celdas no
            // poseídas, para que un cliente desincronizado o manipulado no pueda
            // saltarse la regla. Las cartas normales/evoluciones/acciones no se
            // tocan.
            var tableroPrev = M.Map(M.Get(data, "tablero"));
            var miCuartel = M.Str(M.Get(M.Map(M.Get(data, "obeliscos")), req.Uid));
            bool TeniaCartaPropiaEn(string coord)
            {
                if (!tableroPrev.TryGetValue(coord, out var lst)) return false;
                return M.List(lst).Select(M.Map)
                    .Any(c => M.Str(M.Get(c, "ownerUid")) == req.Uid);
            }
            // Una estática es válida si NO está en el cuartel propio y el jugador
            // ya tenía una carta propia en esa celda el turno anterior.
            bool EstaticaValida(string coord) =>
                (miCuartel == "" || coord != miCuartel) && TeniaCartaPropiaEn(coord);
            foreach (var coord in celdasClr.Keys.ToList())
            {
                var cartas = M.List(celdasClr[coord]).Select(M.Map).ToList();
                var validas = cartas
                    .Where(c => M.Int(M.Get(c, "Condicion")) != 3
                                || EstaticaValida(coord))
                    .Cast<object?>()
                    .ToList();
                if (validas.Count == 0) celdasClr.Remove(coord);
                else if (validas.Count != cartas.Count) celdasClr[coord] = validas;
            }

            // ── Movimiento del jugador que cierra ─────────────────────────────
            var movData = new Dictionary<string, object?>
            {
                ["uid"] = req.Uid,
                ["turno"] = req.Turno,
                ["celdas"] = celdasClr,
                ["timestamp"] = Timestamp.FromDateTime(DateTime.UtcNow),
                ["acciones"] = accionesIncoming.Cast<object?>().ToList(),
            };

            // movimientosTurno con este jugador ya incluido (para mergear / contar).
            var movTurno = M.Map(M.Get(data, "movimientosTurno"));
            movTurno[req.Uid] = movData;

            // cerradoPor con este jugador.
            var cerrado = M.List(M.Get(data, "cerradoPor")).Select(M.Str).Where(s => s != "").ToHashSet();
            cerrado.Add(req.Uid);

            // Jugadores activos.
            var eliminados = M.List(M.Get(data, "jugadoresEliminados")).Select(M.Str).ToHashSet();
            var jugadores = M.List(M.Get(data, "jugadores"))
                .Select(j => M.Str(M.Get(M.Map(j), "uid")))
                .Where(u => u != "").ToList();
            var activos = jugadores.Where(u => !eliminados.Contains(u)).ToList();

            var todosCerraron = activos.Count > 0 && activos.All(u => cerrado.Contains(u));

            // ── Caso 1: aún faltan jugadores → solo registrar el cierre ───────
            if (!todosCerraron)
            {
                tx.Update(lobbyRef, new Dictionary<FieldPath, object>
                {
                    [new FieldPath("movimientosTurno", req.Uid)] = movData,
                    [new FieldPath("cerradoPor")] = FieldValue.ArrayUnion(req.Uid),
                });

                return new CerrarTurnoResponse
                {
                    Resuelto = false,
                    TurnoActual = turnoDb,
                    CerradoPor = cerrado.Count,
                    JugadoresActivos = activos.Count,
                    Faltan = Math.Max(0, activos.Count - cerrado.Count),
                    Mensaje = "Turno cerrado. Esperando a los demás.",
                };
            }

            // ── Caso 2: cerraron todos → RESOLVER el turno ────────────────────
            // La lógica vive en ResolverTurnoCoreEnTx (compartida con la
            // resolución forzosa por fecha límite).
            return await ResolverTurnoCoreEnTx(
                tx, lobbyRef, snap, data, movTurno, req.Turno,
                jugadores, eliminados, activos, cerrado.Count);
        });

        // Tras commit, adjunta el estado completo de la partida para que el
        // cliente avance SIN leer Firestore (camino HTTP puro).
        try
        {
            resp.Estado = await LeerEstadoAsync(req.LobbyId);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("[WarZero] LeerEstado tras cerrar falló: " + ex);
        }
        // Si esta resolución terminó la partida, reparte recompensas
        if (resp.Finalizada)
        {
            try { await WarZeroRecompensas.RepartirSiFinalizadaAsync(_fs.Db, req.LobbyId); }
            catch (Exception ex)
            {
                Console.Error.WriteLine("[WarZero] recompensas tras cerrar falló: " + ex);
            }
        }

        // Si el turno se resolvió (cerró el último jugador), avisar por push a
        // los jugadores activos de que ya pueden jugar el nuevo turno. Fuera de
        // la transacción y best-effort: nunca rompe el cierre.
        if (resp.Resuelto)
        {
            try { await WarZeroNotificaciones.NotificarTurnoResueltoAsync(_fs.Db, req.LobbyId, excluirUid: req.Uid); }
            catch (Exception ex)
            {
                Console.Error.WriteLine("[WarZero] notificación tras cerrar falló: " + ex);
            }
        }
        if (resp.Resuelto)
        {
            try { await WarZeroNotificaciones.NotificarTraicionesAsync(_fs.Db, req.LobbyId, resp.TurnoActual - 1); }
            catch (Exception ex)
            {
                Console.Error.WriteLine("[WarZero] notificación traición tras cerrar falló: " + ex);
            }
        }
        // Estudio best-effort: si la partida tiene bot y este cierre resolvió el
        // turno, guarda la foto completa en EstudioPartidas (aparte del informe que
        // ven los jugadores). Nunca rompe el cierre.
        if (resp.Resuelto)
        {
            try { await WarZeroEstudio.RegistrarTurnoSiHayBotAsync(_fs.Db, req.LobbyId, resp.Estado); }
            catch (Exception ex)
            {
                Console.Error.WriteLine("[WarZero] estudio tras cerrar falló: " + ex);
            }
        }
        return resp;
    }

    // Núcleo de resolución de turno (compartido por el cierre normal y la
    // resolución forzosa por fecha límite). Debe llamarse DENTRO de una
    // transacción; hace las lecturas (Mapas) antes de la escritura y llama a
    // tx.Update. El tablero se construye a partir de movTurno (cada jugador
    // activo debe tener su entrada: en el cierre normal por haber cerrado; en
    // la resolución forzosa se rellena con sus cartas del tablero previo).
    private async Task<CerrarTurnoResponse> ResolverTurnoCoreEnTx(
        Transaction tx, DocumentReference lobbyRef, DocumentSnapshot snap,
        Dictionary<string, object?> data, Dictionary<string, object?> movTurno,
        int turno, List<string> jugadores, HashSet<string> eliminados,
        List<string> activos, int cerradoCount)
    {
        var db = _fs.Db;
        var fase = "obeliscos";
        try
        {
            var obeliscos = M.Map(M.Get(data, "obeliscos"))
                .ToDictionary(k => k.Key, v => M.Str(v.Value));

            // Tablero fusionado a partir de los movimientos de ESTE turno.
            fase = "merge-tablero";
            var merged = new Dictionary<string, List<Dictionary<string, object?>>>();
            var acciones = new List<Dictionary<string, object?>>();
            foreach (var kv in movTurno)
            {
                var mov = M.Map(kv.Value);
                if (M.Int(M.Get(mov, "turno")) != turno) continue;
                foreach (var ce in M.Map(M.Get(mov, "celdas")))
                {
                    if (!merged.TryGetValue(ce.Key, out var lst)) { lst = new(); merged[ce.Key] = lst; }
                    foreach (var c in M.List(ce.Value)) lst.Add(M.Map(c));
                }
                acciones.AddRange(M.List(M.Get(mov, "acciones")).Select(M.Map));
            }

            // Efectos de celda previos.
            fase = "efectos-previos";
            var efectosPrevios = ParseEfectosCelda(M.Get(data, "efectosCelda"));

            // Tablero del turno anterior (para revertir a su posición las
            // cartas enemigas que se muevan a una celda escudada este turno).
            fase = "tablero-previo";
            var tableroPrevio = new Dictionary<string, List<Dictionary<string, object?>>>();
            foreach (var kv in M.Map(M.Get(data, "tablero")))
            {
                var lst = new List<Dictionary<string, object?>>();
                foreach (var c in M.List(kv.Value)) lst.Add(M.Map(c));
                tableroPrevio[kv.Key] = lst;
            }

            // ── PARÁLISIS: una carta paralizada NO puede moverse. Se revierte a su
            //    celda del turno anterior (mismo patrón que el revert por escudo).
            //    Autoritativo server-side: da igual quién envíe el movimiento (humano
            //    o bot). La duración la gobierna TickEfectos.
            fase = "paralisis-enforce";
            var previoPorInst = new Dictionary<string, (string coord, Dictionary<string, object?> card)>();
            foreach (var (coord, lst) in tableroPrevio)
                foreach (var c in lst)
                {
                    var iid = M.Str(M.Get(c, "instanceId"));
                    if (iid != "") previoPorInst[iid] = (coord, c);
                }
            var paralizadas = previoPorInst
                .Where(kv => CartaHelper.EstaParalizada(kv.Value.card))
                .Select(kv => kv.Key)
                .ToHashSet();
            if (paralizadas.Count > 0)
            {
                // Quitar las paralizadas de donde el jugador intentó colocarlas.
                foreach (var lst in merged.Values)
                    lst.RemoveAll(c => paralizadas.Contains(M.Str(M.Get(c, "instanceId"))));
                // Reponerlas en su celda anterior (con su estado previo, que conserva
                // el efecto para que TickEfectos lo decremente este turno).
                foreach (var iid in paralizadas)
                {
                    var (coord, card) = previoPorInst[iid];
                    if (!merged.TryGetValue(coord, out var lst)) { lst = new(); merged[coord] = lst; }
                    if (!lst.Any(c => M.Str(M.Get(c, "instanceId")) == iid)) lst.Add(card);
                }
                foreach (var k in merged.Keys.Where(k => merged[k].Count == 0).ToList())
                    merged.Remove(k);
            }

            // 1. Acciones (tele → disparo → veneno).
            fase = "acciones";
            // Terreno del mapa: solo se carga si hay teletransportes que
            // validar, para impedir que una carta aterrice en una celda
            // incompatible (p. ej. una unidad de aire en una celda de agua).
            Dictionary<string, string>? terreno = null;
            bool hayTele = acciones.Any(a =>
                CatalogoHabilidades.Get(M.Int(M.Get(a, "habilidadId")))?.Efecto
                    == EfectoTipo.Teletransporte);
            if (hayTele)
            {
                var mapaIdAcc = M.Str(M.Get(data, "mapaId"));
                if (mapaIdAcc != "")
                {
                    var mapaSnapAcc = await tx.GetSnapshotAsync(
                        db.Collection("Mapas").Document(mapaIdAcc));
                    if (mapaSnapAcc.Exists)
                    {
                        var mapDataAcc = M.Map(M.FromFs(mapaSnapAcc.ToDictionary()));
                        terreno = M.Map(M.Get(mapDataAcc, "terreno"))
                            .ToDictionary(k => k.Key, v => M.Str(v.Value));
                    }
                }
            }
            var acc = Habilidades.AplicarAcciones(
                merged, acciones, efectosPrevios, obeliscos, tableroPrevio, terreno);
            // 2. Combates (con alianzas activas: los aliados fusionan fuerza y
            //    su PC se divide /2; los pares con traición pendiente NO cuentan
            //    como aliados esta resolución).
            // 2. Trampas (acciones estáticas) + Combates.
            //    - Se construyen las alianzas activas de esta resolución.
            //    - Se disparan/colocan las trampas sobre acc.Tablero / acc.EfectosCelda.
            //    - Se resuelve el combate ya con las trampas aplicadas y las alianzas.
            fase = "combate";
            var alianzasData = Alianzas.Leer(data);
            var aliadoDe = Alianzas.AliadoDeParaResolucion(alianzasData);
            Trampas.Procesar(acc.Tablero, acc.EfectosCelda, acc.Log,
                acciones, tableroPrevio, obeliscos, aliadoDe);

            // ── DESCARGA de cuartel (ANTES del combate) ────────────────────
            // Si un jugador activó "descarga" sobre su PROPIO cuartel, todo lo
            // que haya en esa celda (amigos y enemigos) muere: así un invasor no
            // lo conquista, muere por la descarga. La defensa del cuartel cae a 0
            // y se recupera +25%/turno (0→25→50→75→100% en 4 turnos).
            fase = "descarga";
            var descargasPrev = M.Map(M.Get(data, "descargasCuartel")); // coord -> turnoDescarga
            var descargaTurno = new Dictionary<string, int>();
            foreach (var kv in descargasPrev)
            {
                var td = M.Int(kv.Value);
                if (kv.Key != "" && td > 0) descargaTurno[kv.Key] = td;
            }
            foreach (var a in acciones)
            {
                if (!(M.Get(a, "esDescarga") is bool ed && ed)) continue;
                var duid = M.Str(M.Get(a, "uid"));
                var dcoord = M.Str(M.Get(a, "origen"));
                if (dcoord == "")
                    dcoord = M.Str(M.List(M.Get(a, "objetivos")).FirstOrDefault());
                // Solo el cuartel PROPIO del jugador que la declaró.
                if (dcoord == "" || !obeliscos.TryGetValue(duid, out var micg) || micg != dcoord)
                    continue;

                if (acc.Tablero.TryGetValue(dcoord, out var muertas) && muertas.Count > 0)
                {
                    acc.Log.Add(new Dictionary<string, object?>
                    {
                        ["tipo"] = "descarga",
                        ["uid"] = duid,
                        ["zona"] = M.Str(M.Get(a, "zona")),
                        ["coord"] = dcoord,
                        ["cartasDestruidas"] = muertas.Select(c => (object?)new Dictionary<string, object?>
                        {
                            ["Nombre"] = CartaHelper.Nombre(c),
                            ["ownerUid"] = CartaHelper.OwnerUid(c),
                            ["ownerZone"] = CartaHelper.OwnerZone(c),
                        }).ToList(),
                    });
                }
                acc.Tablero.Remove(dcoord);       // muere todo lo de la celda
                descargaTurno[dcoord] = turno;    // defensa 0 este turno; recupera después
            }

            // Defensa efectiva de cada cuartel con descarga reciente:
            //   diff = turno - turnoDescarga → 0,1,2,3 = 0%,25%,50%,75%.
            //   diff >= 4 → recuperado del todo (sin override → defensa base).
            var defensaObeliscoPorCoord = new Dictionary<string, int>();
            foreach (var kv in descargaTurno)
            {
                var diff = turno - kv.Value;
                if (diff < 0) diff = 0;
                if (diff >= 4) continue;
                defensaObeliscoPorCoord[kv.Key] = Combate.DefensaObelisco * diff / 4;
            }

            var reso = Combate.Resolver(
                acc.Tablero, obeliscos, aliadoDe.Count > 0 ? aliadoDe : null,
                defensaObeliscoPorCoord.Count > 0 ? defensaObeliscoPorCoord : null);
            // 3. Tick de efectos.
            fase = "tick-efectos";
            var tick = Habilidades.TickEfectos(reso.Tablero, acc.EfectosCelda);
            var tableroFinal = tick.Tablero;
            var efectosFinal = tick.EfectosCelda;

            // ── Conquistas de este turno ──────────────────────────────────
            var perdedoresConquista = reso.ObeliscosConquistados
                .Select(c => c.PerdedorUid).ToHashSet();
            var eliminadosTotal = new HashSet<string>(eliminados);
            eliminadosTotal.UnionWith(perdedoresConquista);

            // Issue #4: las cartas sueltas de jugadores eliminados desaparecen
            // YA (no al turno siguiente).
            if (eliminadosTotal.Count > 0)
            {
                var limpio = new Dictionary<string, List<Dictionary<string, object?>>>();
                foreach (var kv in tableroFinal)
                {
                    var quedan = kv.Value
                        .Where(c => !eliminadosTotal.Contains(CartaHelper.OwnerUid(c)))
                        .ToList();
                    if (quedan.Count > 0) limpio[kv.Key] = quedan;
                }
                tableroFinal = limpio;
            }

            // ── VALIDACIÓN del simulador (Tarea 1): con ValidarSimulador=true,
            //    ejecuta SimuladorTurno con los MISMOS planes/estado de este turno
            //    y compara su tablero con el real. Debe salir [SIM][OK] siempre; un
            //    [SIM][MISMATCH] señala una divergencia a corregir. Off en prod.
            if (ValidarSimulador)
            {
                try
                {
                    var planesSim = new List<SimuladorTurno.Plan>();
                    foreach (var kv in movTurno)
                    {
                        var mov = M.Map(kv.Value);
                        if (M.Int(M.Get(mov, "turno")) != turno) continue;
                        var celdasSim = new Dictionary<string, List<Dictionary<string, object?>>>();
                        foreach (var ce in M.Map(M.Get(mov, "celdas")))
                            celdasSim[ce.Key] = M.List(ce.Value).Select(M.Map).ToList();
                        planesSim.Add(new SimuladorTurno.Plan(
                            M.Str(M.Get(mov, "uid")), celdasSim,
                            M.List(M.Get(mov, "acciones")).Select(M.Map).ToList()));
                    }
                    var descPrev = new Dictionary<string, int>();
                    foreach (var kv in M.Map(M.Get(data, "descargasCuartel")))
                    {
                        var td = M.Int(kv.Value);
                        if (kv.Key != "" && td > 0) descPrev[kv.Key] = td;
                    }
                    var sim = SimuladorTurno.Simular(
                        tableroPrevio, obeliscos, turno, planesSim, efectosPrevios,
                        eliminados, aliadoDe.Count > 0 ? aliadoDe : null, terreno, descPrev);
                    var igual = FirmaTablero(sim.Tablero) == FirmaTablero(tableroFinal);
                    Console.WriteLine(igual
                        ? $"[SIM][OK] turno={turno}"
                        : $"[SIM][MISMATCH] turno={turno}\n  sim ={FirmaTablero(sim.Tablero)}\n  real={FirmaTablero(tableroFinal)}");
                }
                catch (Exception ex) { Console.Error.WriteLine("[SIM][ERROR] " + ex); }
            }

            // ── Trampas: actualizar cuántos turnos lleva cada carta en su celda
            //    (una acción estática solo puede armarse sobre una carta propia
            //    asentada ≥ 2 turnos). Se hace con el tablero YA final.
            fase = "trampas-turnos";
            Trampas.ActualizarTurnosEnCelda(tableroFinal, tableroPrevio);

            // Coords de cuarteles destruidos (persistidos + nuevos) para el farmeo.
            var cuartelesDestruidosCoords = reso.ObeliscosConquistados
                .Select(c => c.Coord).ToHashSet();
            foreach (var it in M.List(M.Get(data, "cuartelesDestruidos")))
            {
                var cc = M.Str(M.Get(M.Map(it), "coord"));
                if (cc != "") cuartelesDestruidosCoords.Add(cc);
            }

            // Estado de recuperación de descarga para el SIGUIENTE turno: se
            // conservan los cuarteles cuya defensa aún no llegará al 100% el turno
            // que viene ((turno+1) - turnoDescarga < 4) y que no han sido
            // conquistados/destruidos.
            var descargasNext = new Dictionary<string, object?>();
            foreach (var kv in descargaTurno)
            {
                if (cuartelesDestruidosCoords.Contains(kv.Key)) continue;
                if ((turno + 1) - kv.Value < 4)
                    descargasNext[kv.Key] = (long)kv.Value;
            }

            // 4. Farmeo (solo si el mapa aporta continentes/isla central).
            fase = "farmeo";
            FarmeoResultado? farmeo = null;
            var mapaId = M.Str(M.Get(data, "mapaId"));
            if (mapaId != "")
            {
                var mapaSnap = await tx.GetSnapshotAsync(db.Collection("Mapas").Document(mapaId));
                if (mapaSnap.Exists)
                {
                    var mapData = M.Map(M.FromFs(mapaSnap.ToDictionary()));
                    var continentes = M.Map(M.Get(mapData, "continentes"))
                        .ToDictionary(k => k.Key, v => M.List(v.Value).Select(M.Str).ToList());
                    var islaCentral = M.List(M.Get(mapData, "islaCentral")).Select(M.Str).ToList();
                    if (continentes.Count > 0 || islaCentral.Count > 0)
                    {
                        // Celdas VÁLIDAS del mapa a partir de sus dimensiones
                        // (filas = letras A.., columnas = números 1..). El rayo
                        // debe colocarse SOLO en celdas que existen en el mapa;
                        // antes se usaba un grid por nº de jugadores que no
                        // coincidía con el mapa (bug: rayo en G11 en un 10x6).
                        var columnas = M.Int(M.Get(mapData, "columnas"));
                        var filas = M.Int(M.Get(mapData, "filas"));
                        List<string> celdasMapa;
                        if (columnas > 0 && filas > 0)
                        {
                            celdasMapa = new List<string>(columnas * filas);
                            for (var r = 0; r < filas; r++)
                                for (var c = 1; c <= columnas; c++)
                                    celdasMapa.Add($"{(char)('A' + r)}{c}");
                        }
                        else
                        {
                            // Fallback para mapas antiguos sin columnas/filas.
                            celdasMapa = Coords.AllCells(jugadores.Count);
                        }

                        // Rayos activos (lista). Retrocompat: si la partida
                        // aún guarda el campo antiguo `rayo` (único), se envuelve.
                        var rayosActuales = new List<Dictionary<string, object?>>();
                        var rayosRaw = M.Get(data, "rayos");
                        if (rayosRaw is System.Collections.IEnumerable en && rayosRaw is not string)
                            foreach (var r in en)
                            {
                                var rm = M.Map(r);
                                if (M.Str(M.Get(rm, "coord")) != "") rayosActuales.Add(rm);
                            }
                        else if (snap.ContainsField("rayo"))
                        {
                            var uno = M.Map(M.Get(data, "rayo"));
                            if (M.Str(M.Get(uno, "coord")) != "") rayosActuales.Add(uno);
                        }
                        // Nº de casillas de rayo simultáneas por nº de jugadores:
                        // 2-3 → 1, 4-6 → 2, 7-8 → 3.
                        var nJug = jugadores.Count;
                        var numRayos = nJug >= 7 ? 3 : (nJug >= 4 ? 2 : 1);
                        farmeo = Farmeo.Calcular(
                            tableroFinal, obeliscos, continentes, islaCentral,
                            rayosActuales, celdasMapa, numRayos,
                            new Random(), cuartelesDestruidosCoords);
                    }
                }
            }

            // 5. Acumular stats.
            fase = "stats";
            var stats = new Dictionary<string, Dictionary<string, object?>>();
            foreach (var kv in M.Map(M.Get(data, "statsPartida")))
            {
                var m = M.Map(kv.Value);
                var entry = new Dictionary<string, object?>
                {
                    ["energies"] = M.Int(M.Get(m, "energies")),
                    ["pc"] = M.Int(M.Get(m, "pc")),
                    ["victorias"] = M.Int(M.Get(m, "victorias")),
                    ["derrotas"] = M.Int(M.Get(m, "derrotas")),
                };
                // BUG QAS #2: preservar mano / mazoRestante / especialesCompradas.
                // Antes se reescribía statsPartida SOLO con energies+pc, así que
                // cada turno se borraban la mano, el mazo restante y las especiales
                // compradas (el cliente tenía que repoblar la mano robando en el
                // stream, y las especiales dejaban de estar deshabilitadas). Ahora
                // se conservan y el reparto de fin de turno se hace aquí (paso 5c).
                if (m.ContainsKey("mano"))
                    entry["mano"] = M.List(M.Get(m, "mano")).Select(M.Str)
                        .Where(s => s != "").Cast<object?>().ToList();
                if (m.ContainsKey("mazoRestante"))
                    entry["mazoRestante"] = M.List(M.Get(m, "mazoRestante")).Select(M.Str)
                        .Where(s => s != "").Cast<object?>().ToList();
                if (m.ContainsKey("especialesCompradas"))
                    entry["especialesCompradas"] = M.List(M.Get(m, "especialesCompradas"))
                        .Select(M.Str).Where(s => s != "").Cast<object?>().ToList();
                // mazoPool = mazo completo del jugador (IDs, con repetición por
                // cantidad). Es el pool del que se roba al final de cada turno,
                // CON repetición y SIN agotarse (igual que el robo del cliente).
                if (m.ContainsKey("mazoPool"))
                    entry["mazoPool"] = M.List(M.Get(m, "mazoPool"))
                        .Select(M.Str).Where(s => s != "").Cast<object?>().ToList();
                stats[kv.Key] = entry;
            }
            void EnsureStat(string uid)
            {
                if (!stats.ContainsKey(uid))
                    stats[uid] = new() { ["energies"] = 0, ["pc"] = 0, ["victorias"] = 0, ["derrotas"] = 0 };
            }
            foreach (var kv in reso.EnergiesPorJugador)
            {
                EnsureStat(kv.Key);
                stats[kv.Key]["energies"] = M.Int(stats[kv.Key]["energies"]) + kv.Value;
            }
            foreach (var kv in reso.PcPorJugador)
            {
                EnsureStat(kv.Key);
                stats[kv.Key]["pc"] = M.Int(stats[kv.Key]["pc"]) + kv.Value;
            }
            // ── V/D por combate: se calcula UNA vez y se acumula tanto POR
            // PARTIDA (en statsPartida, aquí) como en las estadísticas GLOBALES
            // del jugador (más abajo, tras el tx.Update). El mismo `statDelta`
            // sirve para ambos; NO se vuelve a declarar después.
            //   · Combate con ganador → +1 Victoria al ganador y +1 Derrota a
            //     cada derrotado (incluye conquistas de cuartel).
            //   · Empate en cabeza (sin ganador) → a los grupos empatados NO se
            //     les suma nada; solo los grupos destruidos (DerrotadosUid).
            var statDelta = new Dictionary<string, (int vic, int der)>();
            foreach (var r in reso.Resultados)
            {
                if (!string.IsNullOrEmpty(r.GanadorUid))
                {
                    var cur = statDelta.GetValueOrDefault(r.GanadorUid!);
                    statDelta[r.GanadorUid!] = (cur.vic + 1, cur.der);
                }
                foreach (var perdedor in r.DerrotadosUid)
                {
                    if (string.IsNullOrEmpty(perdedor)) continue;
                    var cur = statDelta.GetValueOrDefault(perdedor);
                    statDelta[perdedor] = (cur.vic, cur.der + 1);
                }
            }
            foreach (var kv in statDelta)
            {
                EnsureStat(kv.Key);
                stats[kv.Key]["victorias"] = M.Int(stats[kv.Key]["victorias"]) + kv.Value.vic;
                stats[kv.Key]["derrotas"] = M.Int(stats[kv.Key]["derrotas"]) + kv.Value.der;
            }
            if (farmeo != null)
                foreach (var kv in farmeo.EnergiesPorJugador)
                {
                    EnsureStat(kv.Key);
                    stats[kv.Key]["energies"] = M.Int(stats[kv.Key]["energies"]) + kv.Value;
                }

            // 5b. Suerte del perdedor: si un jugador que sigue en partida NO
            // gana energías ESTE turno (ni combate ni farmeo), recibe +3.
            // Se mira lo ganado EN EL TURNO, no el total acumulado.
            fase = "suerte-perdedor";
            var perdedoresEsteTurno = reso.ObeliscosConquistados
                .Select(c => c.PerdedorUid).ToHashSet();
            var suerteLog = new List<Dictionary<string, object?>>();
            foreach (var uid in activos)
            {
                if (perdedoresEsteTurno.Contains(uid)) continue;
                var ganadoTurno = reso.EnergiesPorJugador.GetValueOrDefault(uid)
                    + (farmeo?.EnergiesPorJugador.GetValueOrDefault(uid) ?? 0);
                if (ganadoTurno != 0) continue;
                EnsureStat(uid);
                stats[uid]["energies"] = M.Int(stats[uid]["energies"]) + 3;
                suerteLog.Add(new Dictionary<string, object?>
                {
                    ["uid"] = uid,
                    ["zona"] = "",
                    ["totalEnergies"] = 3L,
                    ["detalle"] = new Dictionary<string, object?> { ["suerteDelPerdedor"] = 3L },
                });
            }

            // farmeoLog final = farmeo del mapa + suerte del perdedor, para
            // que el concepto sea visible en el informe (pestaña ENERGIES).
            var farmeoLogFinal = new List<object?>();
            if (farmeo != null) farmeoLogFinal.AddRange(farmeo.FarmeoLog.Cast<object?>());
            farmeoLogFinal.AddRange(suerteLog.Cast<object?>());

            // 5c. Reparto de fin de turno (server-side). Cada jugador activo
            // que NO quede eliminado roba 1 carta de su mazo completo a su mano.
            // BUG QAS #2: antes esto lo hacía el CLIENTE al ver avanzar el turno
            // en el stream; si el jugador no estaba presente cuando el turno
            // resolvía, nunca robaba ni se persistía → la carta se perdía y no
            // aparecía en el informe. Ahora es autoritativo en el servidor y se
            // registra en repartoLog para que el informe lo muestre siempre.
            fase = "reparto";
            var elimTrasTurno = new HashSet<string>(eliminados);
            foreach (var oc in reso.ObeliscosConquistados) elimTrasTurno.Add(oc.PerdedorUid);
            var repartoLog = new List<Dictionary<string, object?>>();
            var rngReparto = new Random();
            foreach (var uid in activos)
            {
                if (elimTrasTurno.Contains(uid)) continue;
                if (!stats.TryGetValue(uid, out var st)) continue;
                // El precio de robar se reinicia cada turno: al empezar un turno
                // nuevo vuelve a valer 100 (el cliente lo calcula desde
                // robosComprados). Dentro del turno sube 100→200→400 por robo.
                st["robosComprados"] = 0L;
                // Pool de robo = mazoPool (mazo completo). Fallback a
                // mazoRestante para partidas antiguas sin mazoPool. El robo es
                // CON repetición y NO agota el pool (idéntico al robo del
                // cliente: "la misma carta puede salir otro turno").
                var pool = M.List(M.Get(st, "mazoPool")).Select(M.Str)
                    .Where(s => s != "").ToList();
                if (pool.Count == 0)
                    pool = M.List(M.Get(st, "mazoRestante")).Select(M.Str)
                        .Where(s => s != "").ToList();
                if (pool.Count == 0) continue;
                var manoUid = M.List(M.Get(st, "mano")).Select(M.Str)
                    .Where(s => s != "").ToList();
                var cartaId = pool[rngReparto.Next(pool.Count)];
                manoUid.Add(cartaId);
                st["mano"] = manoUid.Cast<object?>().ToList();
                repartoLog.Add(new Dictionary<string, object?>
                {
                    ["uid"] = uid,
                    ["cartaId"] = cartaId,
                });
            }

            // 6. Logs + entrada de historial.
            fase = "logs-historial";
            var combateLog = reso.Resultados.Select(r => (object?)r.ToLogMap()).ToList();
            var conquistasLog = reso.ObeliscosConquistados.Select(c => (object?)c.ToLogMap()).ToList();
            // Cartas invisibles de esta resolución (tablero pre-combate) para
            // ocultarlas del informe de movimientos.
            var invisiblesMov = InstanceIdsInvisibles(acc.Tablero);
            var movimientosLog = BuildMovimientosLog(movTurno, turno, obeliscos, invisiblesMov);

            // Coordenadas de TODAS las casillas de rayo tras resolver (lista).
            var rayoCoordsFinal = (farmeo?.NuevosRayos ?? new List<Dictionary<string, object?>>())
                .Select(r => M.Get(r, "coord")).Where(c => c != null).ToList();

            var entradaHistorial = new Dictionary<string, object?>
            {
                ["turno"] = turno,
                ["combateLog"] = combateLog,
                ["conquistasLog"] = conquistasLog,
                ["movimientosLog"] = movimientosLog,
                ["farmeoLog"] = farmeoLogFinal,
                ["repartoLog"] = repartoLog.Cast<object?>().ToList(),
                ["accionesLog"] = acc.Log.Cast<object?>().ToList(),
                ["rayoCoords"] = rayoCoordsFinal,
            };

            var historial = M.List(M.Get(data, "historialCombates")).ToList();
            historial.Add(entradaHistorial);
            if (historial.Count > 5) historial.RemoveRange(0, historial.Count - 5);

            // 7. Construir el update.
            fase = "build-update";
            // Fecha de resolución obligatoria del SIGUIENTE turno, según el modo:
            // 00:00 UTC (diario/rapida) o ahora + 12 h (turno12h).
            long _limitePrevMs = M.Long(M.Get(data, "fechaResolucion"));
            string _modoTurno = M.Str(M.Get(data, "modoTurno"));
            long _limiteSiguienteMs = FechaResolucionSiguienteMillis(
                _modoTurno, _limitePrevMs > 0 ? _limitePrevMs : (long?)null);
            var update = new Dictionary<string, object>
            {
                ["turnoActual"] = turno + 1,
                ["fechaResolucion"] = _limiteSiguienteMs,
                ["cerradoPor"] = new List<object>(),
                ["movimientosTurno"] = new Dictionary<string, object>(),
                ["tablero"] = ToFsTablero(tableroFinal),
                ["statsPartida"] = stats.ToDictionary(k => k.Key, v => (object)v.Value),
                ["ultimoCombateLog"] = combateLog,
                ["ultimoFarmeoLog"] = farmeoLogFinal,
                ["ultimoRepartoLog"] = repartoLog.Cast<object?>().ToList(),
                ["ultimoAccionesLog"] = acc.Log.Cast<object?>().ToList(),
                ["ultimosMovimientos"] = movimientosLog,
                ["historialCombates"] = historial,
                ["efectosCelda"] = efectosFinal.Count == 0 ? FieldValue.Delete : ToFsEfectos(efectosFinal),
                ["descargasCuartel"] = descargasNext.Count == 0
                    ? (object)FieldValue.Delete
                    : descargasNext.ToDictionary(k => k.Key, v => (object)v.Value!),
            };
            if (farmeo != null)
            {
                // Guardar la LISTA de rayos activos y borrar el campo antiguo
                // `rayo` (único) para no dejar estado obsoleto.
                update["rayos"] = farmeo.NuevosRayos.Count > 0
                    ? (object)farmeo.NuevosRayos.Cast<object>().ToList()
                    : FieldValue.Delete;
                update["rayo"] = FieldValue.Delete;
            }

            // ── Cerrar los cuarteles conquistados (issues #1, #2, #3) ──────
            if (reso.ObeliscosConquistados.Count > 0)
            {
                // El perdedor deja de tener cuartel: se reescribe `obeliscos`
                // sin él → no se re-conquista cada turno (issue #3) y la UI lo
                // trata como celda normal (issue #1).
                var obeliscosRestantes = obeliscos
                    .Where(kv => !perdedoresConquista.Contains(kv.Key))
                    .ToDictionary(kv => kv.Key, kv => (object?)kv.Value);
                update["obeliscos"] = obeliscosRestantes;

                // Registro de ruinas para el marcador visual (issue #2).
                var destruidosLista = M.List(M.Get(data, "cuartelesDestruidos")).ToList();
                foreach (var oc in reso.ObeliscosConquistados)
                    destruidosLista.Add(new Dictionary<string, object?>
                    {
                        ["coord"] = oc.Coord,
                        ["conquistadorUid"] = oc.ConquistadorUid,
                        ["perdedorUid"] = oc.PerdedorUid,
                        ["turno"] = turno,
                    });
                update["cuartelesDestruidos"] = destruidosLista;
            }

            // 8. Eliminaciones / fin de partida.
            var nuevosEliminados = reso.ObeliscosConquistados.Select(c => c.PerdedorUid).Distinct().ToList();
            string? ganadorUid = null;
            var finalizada = false;
            if (nuevosEliminados.Count > 0)
            {
                update["jugadoresEliminados"] = FieldValue.ArrayUnion(nuevosEliminados.Cast<object>().ToArray());
                var totalElim = new HashSet<string>(eliminados);
                totalElim.UnionWith(nuevosEliminados);
                var siguenActivos = jugadores.Where(u => !totalElim.Contains(u)).ToList();
                if (siguenActivos.Count <= 1)
                {
                    finalizada = true;
                    update["estado"] = "finalizada";
                    if (siguenActivos.Count > 0)
                    {
                        ganadorUid = siguenActivos[0];
                        update["ganadorUid"] = ganadorUid;
                    }
                }
            }

            // ── Alianzas: fin de turno (decrementa turnos, expira las de 0 y
            //    aplica las traiciones pendientes; añade avisos in-app). Se usa
            //    el mismo `alianzasData` leído en la fase de combate.
            fase = "alianzas";
            var alianzasNuevo = Alianzas.AplicarFinDeTurno(alianzasData, turno, out _);
            update["alianzas"] = alianzasNuevo == null
                ? (object)FieldValue.Delete
                : (object)alianzasNuevo;

            fase = "write";
            tx.Update(lobbyRef, update);

            // ── Victorias / Derrotas POR COMBATE (globales del jugador) ────────
            // Reutiliza el `statDelta` ya calculado arriba (mismo commit atómico
            // que la resolución del turno). NO se recalcula ni se redeclara.
            //   · Combate con ganador → Victoria al ganador y Derrota a cada
            //     derrotado (incluye conquistas de cuartel).
            //   · Empate en cabeza (sin ganador) → a los grupos empatados NO se
            //     les suma nada; solo los grupos claramente destruidos
            //     (DerrotadosUid) cuentan Derrota. Cuando el standoff se rompa en
            //     un turno posterior, ese combate ya se contará como normal.
            fase = "stats-combate";
            foreach (var kv in statDelta)
            {
                if (kv.Value.vic == 0 && kv.Value.der == 0) continue;
                var jugRef = db.Collection("Jugadores").Document(kv.Key);

                // Fuente canónica: subcolección Estadisticas/Resultados.
                var campos = new Dictionary<string, object>();
                if (kv.Value.vic > 0) campos["Victorias"] = FieldValue.Increment(kv.Value.vic);
                if (kv.Value.der > 0) campos["Derrotas"] = FieldValue.Increment(kv.Value.der);
                tx.Set(jugRef.Collection("Estadisticas").Document("Resultados"),
                    campos, SetOptions.MergeAll);

                // Espejo en el doc del jugador (Firestore no ordena por
                // subcolecciones: para desempatar el ranking por victorias/derrotas
                // esos campos DEBEN vivir en el propio documento).
                var espejo = new Dictionary<string, object>();
                if (kv.Value.vic > 0) espejo["victorias"] = FieldValue.Increment(kv.Value.vic);
                if (kv.Value.der > 0) espejo["derrotas"] = FieldValue.Increment(kv.Value.der);
                tx.Set(jugRef, espejo, SetOptions.MergeAll);
            }

            // ── PC de combate → Cristales Zero del ejército ────────────────────
            // La conversión ya NO se hace por turno. Ahora se reparte una sola vez
            // al FINALIZAR la partida (5 PC = 1 Zero + 1 por no abandonar) en
            // WarZeroRecompensas.RepartirSiFinalizadaAsync. El PC sigue
            // acumulándose en statsPartida[uid].pc, que es lo que usa el reparto.

            var energiesTotales = new Dictionary<string, int>(reso.EnergiesPorJugador);
            return new CerrarTurnoResponse
            {
                Resuelto = true,
                TurnoActual = turno + 1,
                CerradoPor = cerradoCount,
                JugadoresActivos = activos.Count,
                Faltan = 0,
                Finalizada = finalizada,
                GanadorUid = ganadorUid,
                Conquistas = reso.ObeliscosConquistados.Select(c => c.ToLogMap()).ToList(),
                EnergiesPorJugador = energiesTotales,
                Mensaje = "Turno resuelto.",
            };
        }
        catch (Exception ex)
        {
            // Re-lanza añadiendo la fase para diagnóstico preciso.
            throw new InvalidOperationException(
                $"[fase={fase}] {ex.GetType().Name}: {ex.Message}", ex);
        }
    }

    // ── Fecha de resolución obligatoria (00:00 UTC) ─────────────────────────
    private static DateTime MedianocheUtcHoy()
    {
        var n = DateTime.UtcNow;
        return new DateTime(n.Year, n.Month, n.Day, 0, 0, 0, DateTimeKind.Utc);
    }

    private static long ToMillisUtc(DateTime dtUtc) =>
        (long)dtUtc.ToUniversalTime().Subtract(DateTime.UnixEpoch).TotalMilliseconds;

    // Siguiente límite = min(limiteActual + 1 día, medianoche_hoy + 2 días),
    // con suelo en medianoche_hoy + 1 día (mañana). Si no hay límite previo,
    // devuelve mañana 00:00 UTC. Todo a 00:00 UTC.
    private static long SiguienteLimiteMillis(long? limiteActualMs)
    {
        var medianoche = MedianocheUtcHoy();
        var low = medianoche.AddDays(1);
        var high = medianoche.AddDays(2);
        DateTime candidato;
        if (limiteActualMs is long ms && ms > 0)
            candidato = DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime.AddDays(1);
        else
            candidato = low;
        var next = candidato < low ? low : (candidato > high ? high : candidato);
        return ToMillisUtc(next);
    }

    // Fecha de resolución del SIGUIENTE turno según el MODO de la partida:
    //   · "turno12h" → hora actual UTC + 12 h (el turno se cierra solo al llegar).
    //   · "diario"   → próxima medianoche 00:00 UTC (comportamiento clásico).
    //   · otro (rapida) → medianoche 00:00 UTC como red de seguridad.
    // El cierre efectivo lo aplica ForzarResolucionSiProcedeAsync (perezoso) y el
    // barrido del orquestador (para partidas sin nadie con la app abierta).
    private static long FechaResolucionSiguienteMillis(string? modoTurno, long? limiteActualMs)
    {
        if (modoTurno == "turno12h")
            return ToMillisUtc(DateTime.UtcNow.AddHours(12));
        return SiguienteLimiteMillis(limiteActualMs);
    }

    // Resolución FORZOSA por fecha límite (00:00 UTC). Comprobación perezosa: se
    // llama al entrar / leer la partida. Si el límite venció, resuelve el turno
    // con lo que haya (rellenando los jugadores ausentes con sus cartas del
    // tablero previo para que no desaparezcan). Devuelve true si resolvió.
    public async Task<bool> ForzarResolucionSiProcedeAsync(
        string lobbyId, DocumentSnapshot? preSnap = null)
    {
        if (string.IsNullOrWhiteSpace(lobbyId)) return false;
        var db = _fs.Db;
        var lobbyRef = db.Collection("Partidas").Document(lobbyId);
        try
        {
            // Pre-comprobación barata (sin transacción). Si el llamante ya leyó el
            // documento (p. ej. LeerEstadoAsync), reutilizamos ESE snapshot para no
            // gastar una lectura extra en cada sondeo. Si no, lo leemos aquí.
            var pre = preSnap ?? await lobbyRef.GetSnapshotAsync();
            if (!pre.Exists) return false;
            var preData = M.Map(M.FromFs(pre.ToDictionary()));
            if (M.Str(M.Get(preData, "estado")) == "finalizada") return false;
            long limiteMs = M.Long(M.Get(preData, "fechaResolucion"));
            if (limiteMs <= 0)
            {
                // Sin límite todavía → inicializarlo según el modo (no resuelve):
                // 00:00 UTC (diario/rapida) o ahora + 12 h (turno12h).
                var modoInit = M.Str(M.Get(preData, "modoTurno"));
                await lobbyRef.UpdateAsync("fechaResolucion",
                    FechaResolucionSiguienteMillis(modoInit, null));
                return false;
            }
            if (ToMillisUtc(DateTime.UtcNow) < limiteMs) return false; // aún no vence

            // Venció → resolver dentro de una transacción (re-comprobando).
            var resuelto = await db.RunTransactionAsync(async tx =>
            {
                var snap = await tx.GetSnapshotAsync(lobbyRef);
                if (!snap.Exists) return false;
                var data = M.Map(M.FromFs(snap.ToDictionary()));
                if (M.Str(M.Get(data, "estado")) == "finalizada") return false;
                long lim = M.Long(M.Get(data, "fechaResolucion"));
                if (lim <= 0 || ToMillisUtc(DateTime.UtcNow) < lim) return false;

                var turno = M.Int(M.Get(data, "turnoActual"));
                var eliminados = M.List(M.Get(data, "jugadoresEliminados")).Select(M.Str).ToHashSet();
                var jugadores = M.List(M.Get(data, "jugadores"))
                    .Select(j => M.Str(M.Get(M.Map(j), "uid"))).Where(u => u != "").ToList();
                var activos = jugadores.Where(u => !eliminados.Contains(u)).ToList();
                if (activos.Count == 0) return false;

                var movTurno = M.Map(M.Get(data, "movimientosTurno"));
                var cerrado = M.List(M.Get(data, "cerradoPor")).Select(M.Str)
                    .Where(s => s != "").ToHashSet();

                // uids que YA enviaron movimiento de ESTE turno (cerraron).
                var conMov = movTurno
                    .Where(kv => M.Int(M.Get(M.Map(kv.Value), "turno")) == turno)
                    .Select(kv => kv.Key).ToHashSet();

                // Rellenar el movimiento de los AUSENTES con sus cartas del tablero
                // previo, para que no se pierdan al recomponer el tablero.
                var tablero = M.Map(M.Get(data, "tablero"));
                foreach (var uid in activos)
                {
                    if (conMov.Contains(uid)) continue;
                    var celdas = new Dictionary<string, object?>();
                    foreach (var kv in tablero)
                    {
                        var mias = M.List(kv.Value).Select(M.Map)
                            .Where(c => M.Str(M.Get(c, "ownerUid")) == uid)
                            .Cast<object?>().ToList();
                        if (mias.Count > 0) celdas[kv.Key] = mias;
                    }
                    movTurno[uid] = new Dictionary<string, object?>
                    {
                        ["uid"] = uid,
                        ["turno"] = turno,
                        ["celdas"] = celdas,
                        ["acciones"] = new List<object?>(),
                    };
                }

                await ResolverTurnoCoreEnTx(tx, lobbyRef, snap, data, movTurno, turno,
                    jugadores, eliminados, activos, cerrado.Count);
                return true;
            });

            // Si la resolución forzosa terminó la partida, reparte recompensas
            // (experiencia/dinero/nivel por posición final). Es idempotente.
            if (resuelto)
            {
                try { await WarZeroRecompensas.RepartirSiFinalizadaAsync(db, lobbyId); }
                catch (Exception ex)
                {
                    Console.Error.WriteLine("[WarZero] recompensas tras forzar falló: " + ex);
                }

                // Turno resuelto por HORA LÍMITE → avisar por push a los
                // jugadores activos de que ya pueden jugar. Best-effort.
                try { await WarZeroNotificaciones.NotificarTurnoResueltoAsync(db, lobbyId); }
                catch (Exception ex)
                {
                    Console.Error.WriteLine("[WarZero] notificación tras forzar falló: " + ex);
                }

                // Push a las víctimas de traición resueltas en este turno.
                try
                {
                    var s2 = await lobbyRef.GetSnapshotAsync();
                    if (s2.Exists)
                    {
                        var d2 = M.Map(M.FromFs(s2.ToDictionary()));
                        var tActual = M.Int(M.Get(d2, "turnoActual"));
                        await WarZeroNotificaciones.NotificarTraicionesAsync(db, lobbyId, tActual - 1);
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine("[WarZero] notificación traición tras forzar falló: " + ex);
                }
            }
            return resuelto;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("[WarZero] ForzarResolucion falló lobby=" + lobbyId + ": " + ex);
            return false;
        }
    }

    // ── Arranque automático de sala llena ───────────────────────────────────
    /// Arranca automáticamente una sala EN ESPERA cuando ya está LLENA
    /// (jugadores >= maxJugadores), sin depender de que el host tenga la app
    /// abierta. Idempotente y seguro: si ya está en curso o aún no está llena, no
    /// hace nada. Devuelve true SOLO si esta llamada la arrancó (para notificar
    /// una única vez). Deja turno 1 y fija la fecha de resolución inicial según el
    /// modo (00:00 UTC en diario/rapida, ahora + 12 h en turno12h).
    // Todos los jugadores presentes en la sala han elegido ejército (listo).
    // La partida solo arranca (sola al llenarse, o tras rellenar con bots) cuando
    // NADIE queda por elegir. Los bots se añaden ya con listo == true.
    private static bool TodosHanElegidoEjercito(object? jugadoresRaw)
    {
        var jugadores = M.List(jugadoresRaw).Select(M.Map).ToList();
        if (jugadores.Count == 0) return false;
        return jugadores.All(j => M.Bool(M.Get(j, "listo")));
    }

    public async Task<bool> IntentarAutoIniciarAsync(string lobbyId)
    {
        if (string.IsNullOrWhiteSpace(lobbyId)) return false;
        var db = _fs.Db;
        var lobbyRef = db.Collection("Partidas").Document(lobbyId);
        try
        {
            // Pre-comprobación barata (sin transacción) para no encarecer cada barrido.
            var pre = await lobbyRef.GetSnapshotAsync();
            if (!pre.Exists) return false;
            var preData = M.Map(M.FromFs(pre.ToDictionary()));
            if (M.Str(M.Get(preData, "estado")) != "esperando") return false;
            int maxPre = M.Int(M.Get(preData, "maxJugadores"));
            int njugPre = M.List(M.Get(preData, "jugadores")).Count;
            if (maxPre <= 0 || njugPre < maxPre) return false; // aún no está llena
            // Nuevo criterio: además de llena, TODOS deben haber elegido
            // ejército (listo). Así una sala llena de humanos que aún no eligieron
            // NO arranca sola.
            if (!TodosHanElegidoEjercito(M.Get(preData, "jugadores"))) return false;

            var arrancada = await db.RunTransactionAsync(async tx =>
            {
                var snap = await tx.GetSnapshotAsync(lobbyRef);
                if (!snap.Exists) return false;
                var data = M.Map(M.FromFs(snap.ToDictionary()));
                if (M.Str(M.Get(data, "estado")) != "esperando") return false; // otro la arrancó
                int max = M.Int(M.Get(data, "maxJugadores"));
                int njug = M.List(M.Get(data, "jugadores")).Count;
                if (max <= 0 || njug < max) return false;
                if (!TodosHanElegidoEjercito(M.Get(data, "jugadores"))) return false;

                var modo = M.Str(M.Get(data, "modoTurno"));
                int turno = M.Int(M.Get(data, "turnoActual"));
                if (turno <= 0) turno = 1;

                tx.Update(lobbyRef, new Dictionary<string, object>
                {
                    ["estado"] = "en_curso",
                    ["turnoActual"] = turno,
                    ["cerradoPor"] = new List<object>(),
                    ["fechaResolucion"] = FechaResolucionSiguienteMillis(modo, null),
                });
                return true;
            });

            if (arrancada)
                Console.WriteLine("[WarZero] AUTO-INICIO sala llena lobby=" + lobbyId);
            return arrancada;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("[WarZero] IntentarAutoIniciar lobby=" + lobbyId + " falló: " + ex);
            return false;
        }
    }

    /// Revierte los gastos NO consolidados del turno en curso (bug QAS #2): al
    /// salir a mitad de turno (o pulsar "deshacer"), devuelve la energía
    /// revertible gastada este turno (despliegues/compras/evoluciones), desmarca
    /// las especiales compradas este turno y borra cualquier borrador previo. El
    /// tablero NO se persiste a mitad de turno, así que revierte solo al reentrar.
    ///
    /// NO toca `cerradoPor` ni resuelve. Si el jugador ya cerró o el turno ya
    /// avanzó, se ignora (no hay nada que revertir).
    public async Task<CerrarTurnoResponse> DeshacerTurnoAsync(DeshacerTurnoRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.LobbyId) || string.IsNullOrWhiteSpace(req.Uid))
            return new CerrarTurnoResponse { Mensaje = "lobbyId y uid son obligatorios" };

        var db = _fs.Db;
        var lobbyRef = db.Collection("Partidas").Document(req.LobbyId);

        return await db.RunTransactionAsync(async tx =>
        {
            var snap = await tx.GetSnapshotAsync(lobbyRef);
            if (!snap.Exists)
                return new CerrarTurnoResponse { Mensaje = "La partida no existe" };

            var data = M.Map(M.FromFs(snap.ToDictionary()));
            var turnoDb = M.Int(M.Get(data, "turnoActual"));
            var cerrado = M.List(M.Get(data, "cerradoPor")).Select(M.Str).ToHashSet();

            if (turnoDb != req.Turno || cerrado.Contains(req.Uid))
                return new CerrarTurnoResponse
                {
                    Resuelto = false,
                    TurnoActual = turnoDb,
                    Mensaje = "Deshacer ignorado (turno avanzado o ya cerrado)",
                };

            var updates = new Dictionary<FieldPath, object>();

            // Devolver la energía revertible gastada este turno.
            if (req.EnergiesDelta != 0)
                updates[new FieldPath("statsPartida", req.Uid, "energies")] =
                    FieldValue.Increment(req.EnergiesDelta);

            // Desmarcar las especiales compradas este turno (permite recomprarlas).
            var quitar = (req.EspecialesQuitar ?? new List<string>())
                .Where(s => !string.IsNullOrEmpty(s)).Distinct().ToList();
            if (quitar.Count > 0)
                updates[new FieldPath("statsPartida", req.Uid, "especialesCompradas")] =
                    FieldValue.ArrayRemove(quitar.Cast<object>().ToArray());

            // Borrar cualquier borrador de turno que hubiera quedado.
            updates[new FieldPath("movimientosTurno", req.Uid)] = FieldValue.Delete;

            tx.Update(lobbyRef, updates);

            return new CerrarTurnoResponse
            {
                Resuelto = false,
                TurnoActual = turnoDb,
                Mensaje = "Turno deshecho",
            };
        });
    }
    public async Task<Dictionary<string, object?>?> LeerEstadoAsync(string lobbyId)
    {
        // OPTIMIZACIÓN DE LECTURAS: antes esto costaba 2 lecturas del documento
        // Partida en CADA llamada (una en ForzarResolucionSiProcedeAsync para el
        // pre-check y otra aquí). Como este endpoint lo sondea cada cliente/bot
        // constantemente, esa doble lectura era una de las causas del consumo
        // desproporcionado de Firestore. Ahora leemos la Partida UNA sola vez y
        // reutilizamos ese snapshot para la resolución perezosa. Solo si de
        // verdad se resuelve un turno (frontera de turno, poco frecuente) hace
        // falta releer para devolver el estado ya avanzado.
        var lobbyRef = _fs.Db.Collection("Partidas").Document(lobbyId);
        var snap = await lobbyRef.GetSnapshotAsync();     // 1 lectura (la única en el caso común)
        if (!snap.Exists) return null;

        // Resolución forzosa perezosa: si el límite (00:00 UTC / turno12h) venció,
        // resuelve antes de devolver el estado. Le pasamos el snapshot ya leído
        // para que NO vuelva a leer el documento en el pre-check.
        var resuelto = await ForzarResolucionSiProcedeAsync(lobbyId, snap);
        if (resuelto)
        {
            // Se avanzó el turno dentro de una transacción → releemos para que el
            // cliente vea el estado nuevo. Esto solo ocurre en la frontera de un
            // turno, no en el sondeo normal.
            snap = await lobbyRef.GetSnapshotAsync();
            if (!snap.Exists) return null;
        }

        var safe = M.ToJsonSafe(snap.ToDictionary()) as Dictionary<string, object?>
                   ?? new Dictionary<string, object?>();

        // Si la resolución perezosa avanzó un turno (frontera de turno, poco
        // frecuente), registra la foto de estudio si hay bot. Best-effort.
        if (resuelto)
        {
            try { await WarZeroEstudio.RegistrarTurnoSiHayBotAsync(_fs.Db, lobbyId, safe); }
            catch (Exception ex)
            {
                Console.Error.WriteLine("[WarZero] estudio tras resolución perezosa falló: " + ex);
            }
        }

        // Resolver la skin ACTUAL del PROPIETARIO de cada carta del tablero, de
        // modo que TODOS los jugadores (y bots) vean las cartas de X con la skin
        // elegida por X. Esto es AUTORITATIVO al servir y sustituye a depender de
        // la imagen "horneada" que subió el cliente: funciona aunque X cambie la
        // skin a mitad de partida o la carta se hubiera desplegado con la imagen
        // base. Es puramente cosmético, va cacheado por TTL (no castiga el
        // presupuesto de lecturas en el sondeo) y es tolerante a fallos: si algo
        // va mal, el tablero se devuelve tal cual estaba.
        try { await AplicarSkinsPropietarioAlEstadoAsync(safe); }
        catch (Exception ex)
        {
            Console.Error.WriteLine("[WarZero] resolución de skins por propietario falló: " + ex);
        }

        return safe;
    }

    /// Colección personal del jugador (catálogo + cartas poseídas + stats +
    /// skins resueltas) en una sola llamada, para que el cliente NO tenga que
    /// leer Firestore directamente. Usado por GET /warzero/coleccion.
    // ── Catálogo de cartas cacheado (compartido por TODOS los endpoints) ───────
    // Varias pantallas (colección, mazo, mis mazos) y el reparto de mano leían la
    // colección `Cartas` COMPLETA de Firestore en cada llamada. Con testers
    // abriendo esas pantallas y arrancando partidas, eran cientos de lecturas
    // repetidas de datos ESTÁTICOS. Ahora `Cartas` se lee una vez cada 10 min y
    // se comparte en memoria; cada llamante recibe una COPIA (para poder mutarla,
    // p. ej. aplicar skins, sin tocar el caché).
    private static volatile Dictionary<string, Dictionary<string, object?>>? _catCartas;
    private static DateTime _catCartasCargado = DateTime.MinValue;
    private static Task<Dictionary<string, Dictionary<string, object?>>>? _catCartasCargando;
    private static readonly object _catCartasGate = new();
    private static readonly TimeSpan _catCartasTtl = TimeSpan.FromMinutes(10);

    /// Copia del catálogo (id -> campos con `id` inyectado). Mutable por el
    /// llamante sin afectar al caché compartido.
    private async Task<Dictionary<string, Dictionary<string, object?>>> ObtenerCatalogoCartasAsync()
    {
        var baseCat = await CatalogoCartasBaseAsync();
        var copia = new Dictionary<string, Dictionary<string, object?>>(baseCat.Count);
        foreach (var kv in baseCat)
            copia[kv.Key] = new Dictionary<string, object?>(kv.Value); // copia superficial
        return copia;
    }

    private async Task<Dictionary<string, Dictionary<string, object?>>> CatalogoCartasBaseAsync()
    {
        var cache = _catCartas;
        if (cache != null && (DateTime.UtcNow - _catCartasCargado) < _catCartasTtl)
            return cache;

        Task<Dictionary<string, Dictionary<string, object?>>> carga;
        lock (_catCartasGate)
        {
            if (_catCartas != null && (DateTime.UtcNow - _catCartasCargado) < _catCartasTtl)
                return _catCartas;
            _catCartasCargando ??= CargarCatalogoCartasAsync();
            carga = _catCartasCargando;
        }
        try { return await carga; }
        catch { return _catCartas ?? new Dictionary<string, Dictionary<string, object?>>(); }
    }

    private async Task<Dictionary<string, Dictionary<string, object?>>> CargarCatalogoCartasAsync()
    {
        try
        {
            var snap = await _fs.Db.Collection("Cartas").GetSnapshotAsync();
            var nuevo = new Dictionary<string, Dictionary<string, object?>>(snap.Count);
            foreach (var doc in snap.Documents)
            {
                var m = M.Map(M.ToJsonSafe(doc.ToDictionary()));
                m["id"] = doc.Id;
                nuevo[doc.Id] = m;
            }
            _catCartas = nuevo;
            _catCartasCargado = DateTime.UtcNow;
            return nuevo;
        }
        finally { lock (_catCartasGate) { _catCartasCargando = null; } }
    }

    /// Invalida el caché del catálogo de cartas. Llamar tras editar/crear/borrar
    /// cartas o repartir cartas a jugadores, para que los cambios se vean ya.
    public static void InvalidarCatalogoCartas()
    {
        _catCartas = null;
        _catCartasCargado = DateTime.MinValue;
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // COLECCIONISMO: monedas Zero + porcentaje de completado por ejército.
    // ─────────────────────────────────────────────────────────────────────────────

    /// Ids de los 4 ejércitos coleccionables (1 Humanos, 2 Biónicos, 3 Demonios,
    /// 4 Nefilim).
    private static readonly int[] _ejercitosColeccion = { 1, 2, 3, 4 };

    /// Extrae las 5 monedas Zero del doc del jugador (mapa "Dart-like"). Acepta
    /// clave lowercase (nueva) y PascalCase (legado).
    private static Dictionary<string, object?> ZerosDeJugador(Dictionary<string, object?> d) =>
        new()
        {
            ["zeroCeleste"] = M.Int(M.Get(d, "zeroCeleste", "ZeroCeleste")),
            ["zeroEscarlata"] = M.Int(M.Get(d, "zeroEscarlata", "ZeroEscarlata")),
            ["zeroFuego"] = M.Int(M.Get(d, "zeroFuego", "ZeroFuego")),
            ["zeroNatural"] = M.Int(M.Get(d, "zeroNatural", "ZeroNatural")),
            ["zeroPuro"] = M.Int(M.Get(d, "zeroPuro", "ZeroPuro")),
        };

    /// Devuelve el catálogo de SKINS agrupado por carta: cartaId → lista de
    /// {id, rareza}. Lee la colección `Skins` (una lectura). No se cachea para
    /// que los cambios del editor se reflejen de inmediato.
    private async Task<Dictionary<string, List<Dictionary<string, object?>>>>
        ObtenerSkinsPorCartaAsync()
    {
        var snap = await _fs.Db.Collection("Skins").GetSnapshotAsync();
        var porCarta = new Dictionary<string, List<Dictionary<string, object?>>>();
        foreach (var doc in snap.Documents)
        {
            var sd = M.Map(M.ToJsonSafe(doc.ToDictionary()));
            var cartaId = M.Str(M.Get(sd, "cartaId", "CartaId"));
            if (string.IsNullOrEmpty(cartaId)) continue;
            if (!porCarta.TryGetValue(cartaId, out var lista))
            {
                lista = new List<Dictionary<string, object?>>();
                porCarta[cartaId] = lista;
            }
            lista.Add(new Dictionary<string, object?>
            {
                ["id"] = doc.Id,
                ["rareza"] = M.Str(M.Get(sd, "rareza", "Rareza")),
                ["numeroCompra"] = M.Int(M.Get(sd, "numeroCompra", "NumeroCompra")),
            });
        }
        return porCarta;
    }

    /// Calcula el % de completado por ejército. Cada unidad coleccionable pesa
    /// igual: poseer la carta cuenta como su skin "por defecto" (1) y cada skin
    /// EXTRA existente suma 1 más.
    ///
    ///   total_ejercito   = Σ (1 + nº de skins de la carta)  para cada carta
    ///                      NUMERADA y no-evolución del ejército.
    ///   conseguidas      = Σ (poseída ? 1 : 0) + nº de skins extra desbloqueadas
    ///                      (intersección con las que existen realmente).
    ///
    /// Devuelve una lista de mapas {ejercito, conseguidas, total, porcentaje}.
    private static List<object?> CalcularPorcentajesColeccion(
        Dictionary<string, Dictionary<string, object?>> catalogo,
        HashSet<string> poseidas,
        Dictionary<string, HashSet<string>> skinsDesbloqueadasPorCarta,
        Dictionary<string, List<Dictionary<string, object?>>> skinsPorCarta)
    {
        var acumulado = _ejercitosColeccion.ToDictionary(
            e => e, _ => (conseguidas: 0, total: 0));

        foreach (var kv in catalogo)
        {
            var c = kv.Value;
            var ejercito = M.Int(M.Get(c, "Ejercito", "ejercito"));
            var numero = M.Int(M.Get(c, "Numero", "numero"));
            var condicion = M.Int(M.Get(c, "Condicion", "condicion"));

            // Solo cuentan cartas numeradas, no-evolución (Condicion 1), de un
            // ejército coleccionable.
            if (numero <= 0) continue;
            if (condicion == 1) continue;
            if (!acumulado.ContainsKey(ejercito)) continue;

            var cartaId = M.Str(M.Get(c, "id"));
            var skinsExistentes = skinsPorCarta.TryGetValue(cartaId, out var lst)
                ? lst : new List<Dictionary<string, object?>>();
            var idsExistentes = skinsExistentes
                .Select(s => M.Str(M.Get(s, "id")))
                .Where(s => !string.IsNullOrEmpty(s))
                .ToHashSet();

            var unidadesTotal = 1 + idsExistentes.Count; // default + skins extra
            var poseida = poseidas.Contains(cartaId);
            var skinsUnlocked = skinsDesbloqueadasPorCarta.TryGetValue(cartaId, out var us)
                ? us : new HashSet<string>();
            var skinsConseguidas = skinsUnlocked.Count(id => idsExistentes.Contains(id));
            var unidadesConseguidas = (poseida ? 1 : 0) + skinsConseguidas;

            var actual = acumulado[ejercito];
            acumulado[ejercito] = (
                actual.conseguidas + unidadesConseguidas,
                actual.total + unidadesTotal);
        }

        var res = new List<object?>();
        foreach (var e in _ejercitosColeccion)
        {
            var (conseguidas, total) = acumulado[e];
            var pct = total > 0
                ? (int)Math.Round(conseguidas * 100.0 / total)
                : 0;
            res.Add(new Dictionary<string, object?>
            {
                ["ejercito"] = e,
                ["conseguidas"] = conseguidas,
                ["total"] = total,
                ["porcentaje"] = pct,
            });
        }
        return res;
    }

    // REEMPLAZA el método ColeccionAsync EXISTENTE en WarZeroService.cs por este.
    //
    // Cambios funcionales respecto al original:
    //   • Resuelve la RAREZA de la skin seleccionada (skinRareza).
    //   • Expone "evolucionesPoseidas": IDs de cartas de evolución (Condicion==1)
    //     que el jugador TIENE en su colección. Regla de juego: poseer la carta
    //     base NO implica poseer su evolución; solo se puede evolucionar en el
    //     tablero si la evolución está en esta lista.
    // ─────────────────────────────────────────────────────────────────────────────

    public async Task<Dictionary<string, object?>> ColeccionAsync(string uid)
    {
        var db = _fs.Db;

        // Lecturas en paralelo: jugador y su subcolección Coleccion. El catálogo
        // de cartas viene del caché compartido (no relee `Cartas` en cada llamada).
        var jugadorTask = db.Collection("Jugadores").Document(uid).GetSnapshotAsync();
        var coleccionTask = db.Collection("Jugadores").Document(uid)
            .Collection("Coleccion").GetSnapshotAsync();
        var catalogoTask = ObtenerCatalogoCartasAsync();
        var skinsPorCartaTask = ObtenerSkinsPorCartaAsync();
        await Task.WhenAll(jugadorTask, coleccionTask, catalogoTask, skinsPorCartaTask);

        var jugadorSnap = jugadorTask.Result;
        var coleccionSnap = coleccionTask.Result;

        // Catálogo global: docId -> campos (con el id inyectado, como en el cliente).
        var catalogo = catalogoTask.Result;

        // Catálogo de skins agrupado por carta (para el % de completado).
        var skinsPorCarta = skinsPorCartaTask.Result;

        // Stats del jugador.
        Dictionary<string, object?>? jugador = null;
        if (jugadorSnap.Exists)
        {
            var d = M.Map(M.ToJsonSafe(jugadorSnap.ToDictionary()));
            jugador = new Dictionary<string, object?>
            {
                ["alias"] = M.Str(M.Get(d, "alias")),
                ["nivel"] = M.Int(M.Get(d, "nivel")),
                ["experiencia"] = M.Int(M.Get(d, "experiencia")),
                ["dinero"] = M.Int(M.Get(d, "dinero")),
                ["imagenPerfil"] = M.Str(M.Get(d, "imagenPerfil")),
            };
            // Monedas Zero (coleccionismo / tiendas por ejército).
            foreach (var z in ZerosDeJugador(d)) jugador[z.Key] = z.Value;
        }

        // Entradas de colección + skins seleccionadas a resolver.
        var skinIds = new HashSet<string>();
        var entradas = new List<Dictionary<string, object?>>();
        foreach (var doc in coleccionSnap.Documents)
        {
            var d = M.Map(M.ToJsonSafe(doc.ToDictionary()));
            var cant = M.Int(M.Get(d, "cantidad"));
            if (cant <= 0) cant = 1;
            var skinSel = M.Get(d, "skinSeleccionada") as string;
            if (!string.IsNullOrEmpty(skinSel)) skinIds.Add(skinSel!);
            entradas.Add(new Dictionary<string, object?>
            {
                ["cartaId"] = doc.Id,
                ["cantidad"] = cant,
                ["skinSeleccionada"] = skinSel,
                ["skinsDesbloqueadas"] =
                    M.List(M.Get(d, "skinsDesbloqueadas")).Select(M.Str).Cast<object?>().ToList(),
                ["fechaObtenida"] = M.Get(d, "fechaObtenida"),
            });
        }

        // Conjuntos para el cálculo de % de completado:
        //   poseidas                    → cartaIds que el jugador tiene.
        //   skinsDesbloqueadasPorCarta  → cartaId → set de skinIds desbloqueadas.
        var poseidas = new HashSet<string>();
        var skinsDesbloqueadasPorCarta = new Dictionary<string, HashSet<string>>();
        foreach (var e in entradas)
        {
            var cid = M.Str(e["cartaId"]);
            if (string.IsNullOrEmpty(cid)) continue;
            poseidas.Add(cid);
            skinsDesbloqueadasPorCarta[cid] = M.List(e["skinsDesbloqueadas"])
                .Select(M.Str)
                .Where(s => !string.IsNullOrEmpty(s))
                .ToHashSet();
        }

        // Evoluciones que el jugador POSEE realmente (Condicion==1 en el catálogo).
        // Fuente de verdad de la regla "solo evolucionas si tienes la evolución".
        var evolucionesPoseidas = poseidas
            .Where(id => catalogo.TryGetValue(id, out var c)
                         && M.Int(M.Get(c, "Condicion", "condicion")) == 1)
            .Cast<object?>()
            .ToList();

        // Imágenes y RAREZA de las skins seleccionadas (en paralelo).
        var skinUrls = new Dictionary<string, string>();
        var skinRarezas = new Dictionary<string, string>();
        if (skinIds.Count > 0)
        {
            var skinTasks = skinIds.ToDictionary(
                id => id,
                id => db.Collection("Skins").Document(id).GetSnapshotAsync());
            await Task.WhenAll(skinTasks.Values);
            foreach (var kv in skinTasks)
            {
                var s = kv.Value.Result;
                if (!s.Exists) continue;
                var sd = M.Map(M.ToJsonSafe(s.ToDictionary()));
                var url = M.Str(M.Get(sd, "imagen"));
                if (!string.IsNullOrEmpty(url)) skinUrls[kv.Key] = url;
                var rar = M.Str(M.Get(sd, "rareza"));
                if (!string.IsNullOrEmpty(rar)) skinRarezas[kv.Key] = rar;
            }
        }

        // Cartas poseídas = catálogo + datos de colección + url/rareza de skin.
        // Y recoger las evoluciones referenciadas para incluirlas también.
        var cartas = new List<Dictionary<string, object?>>();
        var evolucionIds = new HashSet<string>();
        foreach (var e in entradas)
        {
            var cartaId = M.Str(e["cartaId"]);
            if (!catalogo.TryGetValue(cartaId, out var cat)) continue;

            var merged = new Dictionary<string, object?>(cat)
            {
                ["cantidad"] = e["cantidad"],
                ["skinSeleccionada"] = e["skinSeleccionada"],
                ["skinsDesbloqueadas"] = e["skinsDesbloqueadas"],
                ["fechaObtenida"] = e["fechaObtenida"],
            };
            // Ids de las skins EXTRA existentes para esta carta (en orden), para
            // que el cliente pinte los números 1..X bajo la carta y sepa cuáles
            // están desbloqueadas (∩ con skinsDesbloqueadas). El nº 1 es el
            // diseño por defecto (se posee al tener la carta).
            var skinsExtra = skinsPorCarta.TryGetValue(cartaId, out var lstSk)
                ? lstSk : new List<Dictionary<string, object?>>();
            merged["skinsExtraIds"] = skinsExtra
                .Select(s => (object?)M.Str(M.Get(s, "id")))
                .Where(s => !string.IsNullOrEmpty((string)s!))
                .ToList();
            if (e["skinSeleccionada"] is string sel)
            {
                if (skinUrls.TryGetValue(sel, out var url))
                    merged["skinImagen"] = url;
                if (skinRarezas.TryGetValue(sel, out var rar))
                    merged["skinRareza"] = rar;
            }
            cartas.Add(merged);

            var idEvo = M.Str(M.Get(cat, "IdEvolucion"));
            if (!string.IsNullOrEmpty(idEvo)) evolucionIds.Add(idEvo);
        }

        var evoluciones = evolucionIds
            .Where(catalogo.ContainsKey)
            .Select(id => (object?)catalogo[id])
            .ToList();

        // Porcentaje de completado por ejército (cartas + skins, mismo peso).
        var porcentajes = CalcularPorcentajesColeccion(
            catalogo, poseidas, skinsDesbloqueadasPorCarta, skinsPorCarta);

        // Catálogo NUMERADO por ejército: para poder pintar como "bloqueadas"
        // las cartas que el jugador no posee, SIN revelar imagen ni datos. Solo
        // se envía {cartaId, ejercito, numero, poseida}; los datos completos de
        // las poseídas ya viajan en `cartas`.
        var catalogoNumerado = catalogo.Values
            .Where(c =>
            {
                var numero = M.Int(M.Get(c, "Numero", "numero"));
                var condicion = M.Int(M.Get(c, "Condicion", "condicion"));
                var ejercito = M.Int(M.Get(c, "Ejercito", "ejercito"));
                return numero > 0 && condicion != 1 &&
                       _ejercitosColeccion.Contains(ejercito);
            })
            .Select(c =>
            {
                var cartaId = M.Str(M.Get(c, "id"));
                return (object?)new Dictionary<string, object?>
                {
                    ["cartaId"] = cartaId,
                    ["ejercito"] = M.Int(M.Get(c, "Ejercito", "ejercito")),
                    ["numero"] = M.Int(M.Get(c, "Numero", "numero")),
                    ["poseida"] = poseidas.Contains(cartaId),
                    ["idEvolucion"] = M.Str(M.Get(c, "IdEvolucion", "idEvolucion")),
                };
            })
            .OrderBy(o => M.Int(M.Get(M.Map(o), "ejercito")))
            .ThenBy(o => M.Int(M.Get(M.Map(o), "numero")))
            .ToList();

        return new Dictionary<string, object?>
        {
            ["jugador"] = jugador,
            ["cartas"] = cartas.Cast<object?>().ToList(),
            ["evoluciones"] = evoluciones,
            ["evolucionesPoseidas"] = evolucionesPoseidas,
            ["porcentajes"] = porcentajes,
            ["catalogoNumerado"] = catalogoNumerado,
        };
    }

    /// Versión ligera para el PERFIL: devuelve solo el % de completado por
    /// ejército y las monedas Zero del jugador, sin expandir la colección.
    /// Usado por GET /warzero/porcentajes.
    public async Task<Dictionary<string, object?>> PorcentajesColeccionAsync(string uid)
    {
        var db = _fs.Db;

        var jugadorTask = db.Collection("Jugadores").Document(uid).GetSnapshotAsync();
        var coleccionTask = db.Collection("Jugadores").Document(uid)
            .Collection("Coleccion").GetSnapshotAsync();
        var catalogoTask = ObtenerCatalogoCartasAsync();
        var skinsPorCartaTask = ObtenerSkinsPorCartaAsync();
        await Task.WhenAll(jugadorTask, coleccionTask, catalogoTask, skinsPorCartaTask);

        var catalogo = catalogoTask.Result;
        var skinsPorCarta = skinsPorCartaTask.Result;

        var poseidas = new HashSet<string>();
        var skinsDesbloqueadasPorCarta = new Dictionary<string, HashSet<string>>();
        foreach (var doc in coleccionTask.Result.Documents)
        {
            var d = M.Map(M.ToJsonSafe(doc.ToDictionary()));
            poseidas.Add(doc.Id);
            skinsDesbloqueadasPorCarta[doc.Id] = M.List(M.Get(d, "skinsDesbloqueadas"))
                .Select(M.Str)
                .Where(s => !string.IsNullOrEmpty(s))
                .ToHashSet();
        }

        var porcentajes = CalcularPorcentajesColeccion(
            catalogo, poseidas, skinsDesbloqueadasPorCarta, skinsPorCarta);

        var zeros = jugadorTask.Result.Exists
            ? ZerosDeJugador(M.Map(M.ToJsonSafe(jugadorTask.Result.ToDictionary())))
            : ZerosDeJugador(new Dictionary<string, object?>());

        return new Dictionary<string, object?>
        {
            ["porcentajes"] = porcentajes,
            ["zeros"] = zeros,
        };
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // MECÁNICA: abrir sobres (RNG ponderado) y comprar skins con monedas Zero.
    //
    // Economía (AJUSTABLE):
    //   • Sobres: se pagan con Cristales Zero del ejército + Cristales Zero Puro.
    //   • Skins: NO cuestan moneda; se canjean juntando copias de la carta
    //     (contador vecesObtenida ≥ numeroCompra), consumiéndolas.
    //   • Energía pura: +10 Cristales Zero Puro cada 12 h.
    //   • PC de combate → Cristales Zero del ejército (1:1) al resolver turno.
    // ─────────────────────────────────────────────────────────────────────────────

    /// Cristales Zero Puro que otorga la recarga cada _horasEnergiaPura horas.
    private const int _energiaPuraPorRecarga = 10;

    /// Horas entre recargas de energía pura.
    private const int _horasEnergiaPura = 12;

    /// Probabilidad de que una carta del sobre suelte además una skin LEGENDARIA
    /// de esa carta (las legendarias solo se consiguen así, no se compran).
    private const double _probSkinLegendaria = 0.03;

    /// Clave (lowercase) de la moneda Cristales Zero propia de un ejército.
    private static string MonedaKeyDeEjercito(int ejercitoId) => ejercitoId switch
    {
        1 => "zeroCeleste",
        2 => "zeroEscarlata",
        3 => "zeroFuego",
        4 => "zeroNatural",
        _ => "zeroPuro",
    };

    /// Versión PascalCase (legado) de una clave de moneda, para lectura robusta.
    private static string PascalKey(string k) =>
        string.IsNullOrEmpty(k) ? k : char.ToUpperInvariant(k[0]) + k[1..];

    /// Configuración de sobre según el tipo: (coste en Energía Zero, nº de cartas).
    ///   normal   → 10 energías, 4 cartas
    ///   especial → 15 energías, 6 cartas
    ///   doble    → 18 energías, 8 cartas (dos sobres normales con descuento)
    private static (int coste, int cantidad) _configSobre(string tipo) => tipo switch
    {
        "especial" => (15, 6),
        "doble" => (18, 8),
        _ => (10, 4), // normal
    };

    /// Abre un sobre del ejército indicado. Cuesta Energía Zero (según el tipo)
    /// y entrega varias cartas al azar (ponderadas por su Probabilidad). Cada
    /// carta incrementa su contador; los duplicados otorgan Cristales Zero del
    /// ejército y, con baja probabilidad, cada carta puede soltar una skin
    /// legendaria suya.
    public async Task<Dictionary<string, object?>> AbrirSobreAsync(
        string uid, int ejercitoId, string tipo)
    {
        var db = _fs.Db;
        var (coste, cantidad) = _configSobre(tipo);

        var catalogo = await ObtenerCatalogoCartasAsync();

        // Pool de cartas que pueden salir en el sobre. Se admiten TODAS las
        // condiciones (básica, evolución, estática, acción, especial y acción
        // estática): el diseñador decide qué cartas entran por carta, dándoles
        // Numero > 0 y Probabilidad > 0. Una carta aparece en sobres si, y solo
        // si, cumple ambas cosas (además de ser del ejército del sobre).
        var pool = catalogo.Values.Where(c =>
            M.Int(M.Get(c, "Ejercito", "ejercito")) == ejercitoId &&
            M.Int(M.Get(c, "Numero", "numero")) > 0 &&
            M.Dbl(M.Get(c, "Probabilidad", "probabilidad")) > 0).ToList();

        if (pool.Count == 0)
            throw new InvalidOperationException(
                "No hay cartas con probabilidad configurada para este ejército.");

        var jugRef = db.Collection("Jugadores").Document(uid);
        var monedaKey = MonedaKeyDeEjercito(ejercitoId);
        const string puroKey = "zeroPuro";

        // Cobro con Cristales Zero: primero del ejército, y Puro cubre el resto.
        var (pagadoEjercito, pagadoPuro) = await db.RunTransactionAsync(async tx =>
        {
            var snap = await tx.GetSnapshotAsync(jugRef);
            var jd = snap.Exists
                ? M.Map(M.ToJsonSafe(snap.ToDictionary()))
                : new Dictionary<string, object?>();
            var saldoEjercito = M.Int(M.Get(jd, monedaKey, PascalKey(monedaKey)));
            var saldoPuro = M.Int(M.Get(jd, puroKey, PascalKey(puroKey)));

            if (saldoEjercito + saldoPuro < coste)
                throw new InvalidOperationException(
                    $"Cristales Zero insuficientes: necesitas {coste}, tienes " +
                    $"{saldoEjercito} del ejército + {saldoPuro} puro.");

            var delEjercito = Math.Min(saldoEjercito, coste);
            var delPuro = coste - delEjercito;
            var updates = new Dictionary<string, object>();
            if (delEjercito > 0)
                updates[monedaKey] = FieldValue.Increment(-delEjercito);
            if (delPuro > 0)
                updates[puroKey] = FieldValue.Increment(-delPuro);
            if (updates.Count > 0) tx.Update(jugRef, updates);
            return (delEjercito, delPuro);
        });

        // Selección ponderada por Probabilidad.
        var totalPeso = pool.Sum(c => M.Dbl(M.Get(c, "Probabilidad", "probabilidad")));
        Dictionary<string, object?> ElegirCarta()
        {
            var r = Random.Shared.NextDouble() * totalPeso;
            double acc = 0;
            foreach (var c in pool)
            {
                acc += M.Dbl(M.Get(c, "Probabilidad", "probabilidad"));
                if (r <= acc) return c;
            }
            return pool[^1];
        }

        var cartas = new List<object?>();

        for (var n = 0; n < cantidad; n++)
        {
            var elegida = ElegirCarta();
            var cartaId = M.Str(M.Get(elegida, "id"));
            var colRef = jugRef.Collection("Coleccion").Document(cartaId);

            var colSnap = await colRef.GetSnapshotAsync();
            var nueva = !colSnap.Exists;
            var vecesPrev = 0;
            var desbloqueadas = new HashSet<string>();
            if (colSnap.Exists)
            {
                var cd = M.Map(M.ToJsonSafe(colSnap.ToDictionary()));
                vecesPrev = M.Int(M.Get(cd, "vecesObtenida"));
                desbloqueadas = M.List(M.Get(cd, "skinsDesbloqueadas"))
                    .Select(M.Str).Where(s => !string.IsNullOrEmpty(s)).ToHashSet();
            }

            // ¿Sale una skin legendaria de esta carta?
            string? skinLegendaria = null;
            var legSnap = await db.Collection("Skins")
                .WhereEqualTo("cartaId", cartaId).GetSnapshotAsync();
            var candidatas = legSnap.Documents
                .Where(d => M.Str(M.Get(M.Map(M.ToJsonSafe(d.ToDictionary())),
                    "rareza", "Rareza")) == "legendaria")
                .Select(d => d.Id)
                .Where(id => !desbloqueadas.Contains(id))
                .ToList();
            if (candidatas.Count > 0 &&
                Random.Shared.NextDouble() < _probSkinLegendaria)
                skinLegendaria = candidatas[Random.Shared.Next(candidatas.Count)];

            var colData = new Dictionary<string, object?>
            {
                ["cantidad"] = FieldValue.Increment(1),
                ["vecesObtenida"] = FieldValue.Increment(1),
                ["fechaObtenida"] = FieldValue.ServerTimestamp,
            };
            if (skinLegendaria != null)
                colData["skinsDesbloqueadas"] = FieldValue.ArrayUnion(skinLegendaria);
            await colRef.SetAsync(colData, SetOptions.MergeAll);

            cartas.Add(new Dictionary<string, object?>
            {
                ["cartaId"] = cartaId,
                ["nombre"] = M.Str(M.Get(elegida, "Nombre", "nombre")),
                ["imagen"] = M.Str(M.Get(elegida, "Imagen", "imagen")),
                ["nueva"] = nueva,
                ["vecesObtenida"] = vecesPrev + 1,
                ["skinLegendaria"] = skinLegendaria,
            });
        }

        return new Dictionary<string, object?>
        {
            ["ok"] = true,
            ["tipo"] = tipo,
            ["coste"] = coste,
            ["cantidad"] = cantidad,
            ["moneda"] = monedaKey,
            ["pagadoEjercito"] = pagadoEjercito,
            ["pagadoPuro"] = pagadoPuro,
            ["cartas"] = cartas,
        };
    }

    /// Recarga de energía pura: si han pasado ≥ _horasEnergiaPura desde la
    /// última (o nunca), otorga +_energiaPuraPorRecarga Cristales Zero Puro y
    /// registra el momento. Devuelve {concedido, cantidad?, zeroPuro, proximaMs}.
    public async Task<Dictionary<string, object?>> ReclamarEnergiaPuraAsync(string uid)
    {
        var db = _fs.Db;
        var jugRef = db.Collection("Jugadores").Document(uid);
        var intervalo = _horasEnergiaPura * 3600L * 1000L;

        return await db.RunTransactionAsync(async tx =>
        {
            var snap = await tx.GetSnapshotAsync(jugRef);
            var jd = snap.Exists
                ? M.Map(M.ToJsonSafe(snap.ToDictionary()))
                : new Dictionary<string, object?>();
            var ahora = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var ultima = M.Long(M.Get(jd, "ultimaEnergiaPura", "UltimaEnergiaPura"));
            var puro = M.Int(M.Get(jd, "zeroPuro", "ZeroPuro"));

            if (ultima != 0 && ahora < ultima + intervalo)
            {
                return new Dictionary<string, object?>
                {
                    ["concedido"] = false,
                    ["zeroPuro"] = puro,
                    ["proximaMs"] = ultima + intervalo - ahora,
                };
            }

            tx.Set(jugRef, new Dictionary<string, object>
            {
                ["zeroPuro"] = FieldValue.Increment(_energiaPuraPorRecarga),
                ["ultimaEnergiaPura"] = ahora,
            }, SetOptions.MergeAll);

            return new Dictionary<string, object?>
            {
                ["concedido"] = true,
                ["cantidad"] = _energiaPuraPorRecarga,
                ["zeroPuro"] = puro + _energiaPuraPorRecarga,
                ["proximaMs"] = intervalo,
            };
        });
    }

    /// Canjea una skin CONSUMIENDO copias de la carta (no cuesta moneda).
    /// Requisitos:
    ///   • la skin no es legendaria (esas no se canjean, solo caen en sobres);
    ///   • tiene numeroCompra > 0;
    ///   • el jugador ha obtenido la carta ≥ numeroCompra veces (contador);
    ///   • no la tenía ya desbloqueada.
    /// Al canjear se descuentan `numeroCompra` del contador `vecesObtenida`.
    public async Task<Dictionary<string, object?>> ComprarSkinAsync(string uid, string skinId)
    {
        var db = _fs.Db;

        var skinSnap = await db.Collection("Skins").Document(skinId).GetSnapshotAsync();
        if (!skinSnap.Exists)
            throw new InvalidOperationException("La skin no existe.");

        var sd = M.Map(M.ToJsonSafe(skinSnap.ToDictionary()));
        var cartaId = M.Str(M.Get(sd, "cartaId", "CartaId"));
        var rareza = M.Str(M.Get(sd, "rareza", "Rareza"));
        var numeroCompra = M.Int(M.Get(sd, "numeroCompra", "NumeroCompra"));

        if (rareza == "legendaria")
            throw new InvalidOperationException("Las skins legendarias no se canjean.");
        if (numeroCompra <= 0)
            throw new InvalidOperationException("Esta skin no está disponible para canje.");
        if (string.IsNullOrEmpty(cartaId))
            throw new InvalidOperationException("La skin no tiene carta asociada.");

        var colRef = db.Collection("Jugadores").Document(uid)
            .Collection("Coleccion").Document(cartaId);

        var vecesRestantes = await db.RunTransactionAsync(async tx =>
        {
            var colSnap = await tx.GetSnapshotAsync(colRef);
            if (!colSnap.Exists)
                throw new InvalidOperationException("Aún no tienes esa carta.");

            var cd = M.Map(M.ToJsonSafe(colSnap.ToDictionary()));
            var veces = M.Int(M.Get(cd, "vecesObtenida"));
            var desbloqueadas = M.List(M.Get(cd, "skinsDesbloqueadas"))
                .Select(M.Str).ToHashSet();

            if (desbloqueadas.Contains(skinId))
                throw new InvalidOperationException("Ya tienes esa skin.");
            if (veces < numeroCompra)
                throw new InvalidOperationException(
                    $"Necesitas {numeroCompra} copias de la carta (tienes {veces}).");

            tx.Update(colRef, new Dictionary<string, object>
            {
                ["vecesObtenida"] = FieldValue.Increment(-numeroCompra),
                ["skinsDesbloqueadas"] = FieldValue.ArrayUnion(skinId),
            });

            return veces - numeroCompra;
        });

        return new Dictionary<string, object?>
        {
            ["ok"] = true,
            ["skinId"] = skinId,
            ["cartaId"] = cartaId,
            ["copiasUsadas"] = numeroCompra,
            ["vecesRestantes"] = vecesRestantes,
        };
    }

    /// Devuelve TODAS las skins de una carta con su estado para el selector:
    /// {id, nombre, imagen, rareza, numeroCompra, desbloqueada}. Incluye también
    /// el contador `vecesObtenida` del jugador, la `moneda` del ejército y su
    /// saldo `zeroDisponible`, para poder mostrar el botón de compra.
    public async Task<Dictionary<string, object?>> SkinsDeCartaAsync(string uid, string cartaId)
    {
        var db = _fs.Db;

        var catalogo = await ObtenerCatalogoCartasAsync();
        catalogo.TryGetValue(cartaId, out var cat);
        var ejercito = cat != null ? M.Int(M.Get(cat, "Ejercito", "ejercito")) : 0;
        var monedaKey = MonedaKeyDeEjercito(ejercito);

        var colSnap = await db.Collection("Jugadores").Document(uid)
            .Collection("Coleccion").Document(cartaId).GetSnapshotAsync();
        var veces = 0;
        var desbloqueadas = new HashSet<string>();
        if (colSnap.Exists)
        {
            var cd = M.Map(M.ToJsonSafe(colSnap.ToDictionary()));
            veces = M.Int(M.Get(cd, "vecesObtenida"));
            desbloqueadas = M.List(M.Get(cd, "skinsDesbloqueadas"))
                .Select(M.Str).Where(s => !string.IsNullOrEmpty(s)).ToHashSet();
        }

        var jugSnap = await db.Collection("Jugadores").Document(uid).GetSnapshotAsync();
        var zero = jugSnap.Exists
            ? M.Int(M.Get(M.Map(M.ToJsonSafe(jugSnap.ToDictionary())),
                monedaKey, PascalKey(monedaKey)))
            : 0;

        var skinsSnap = await db.Collection("Skins")
            .WhereEqualTo("cartaId", cartaId).GetSnapshotAsync();
        var skins = new List<object?>();
        foreach (var doc in skinsSnap.Documents)
        {
            var sd = M.Map(M.ToJsonSafe(doc.ToDictionary()));
            skins.Add(new Dictionary<string, object?>
            {
                ["id"] = doc.Id,
                ["nombre"] = M.Str(M.Get(sd, "nombre", "Nombre")),
                ["imagen"] = M.Str(M.Get(sd, "imagen", "Imagen")),
                ["rareza"] = M.Str(M.Get(sd, "rareza", "Rareza")),
                ["numeroCompra"] = M.Int(M.Get(sd, "numeroCompra", "NumeroCompra")),
                ["desbloqueada"] = desbloqueadas.Contains(doc.Id),
            });
        }

        return new Dictionary<string, object?>
        {
            ["cartaId"] = cartaId,
            ["vecesObtenida"] = veces,
            ["moneda"] = monedaKey,
            ["zeroDisponible"] = zero,
            ["skins"] = skins,
        };
    }

    /// Skins DESBLOQUEADAS por el jugador para una carta concreta. Lee la
    /// subcolección Coleccion para saber qué skins tiene desbloqueadas y devuelve
    /// sus datos desde la colección Skins. Usado por GET /warzero/skins.
    public async Task<List<Dictionary<string, object?>>> SkinsDisponiblesAsync(
        string uid, string cartaId)
    {
        var db = _fs.Db;

        var entry = await db.Collection("Jugadores").Document(uid)
            .Collection("Coleccion").Document(cartaId).GetSnapshotAsync();
        if (!entry.Exists) return new();

        var d = M.Map(M.ToJsonSafe(entry.ToDictionary()));
        var desbloqueadas = M.List(M.Get(d, "skinsDesbloqueadas"))
            .Select(M.Str)
            .Where(s => !string.IsNullOrEmpty(s))
            .Distinct()
            .Take(30) // Firestore whereIn admite hasta 30 (igual que el cliente).
            .ToList();
        if (desbloqueadas.Count == 0) return new();

        var snap = await db.Collection("Skins")
            .WhereIn(FieldPath.DocumentId, desbloqueadas)
            .WhereEqualTo("cartaId", cartaId)
            .GetSnapshotAsync();

        return snap.Documents.Select(doc =>
        {
            var sd = M.Map(M.ToJsonSafe(doc.ToDictionary()));
            return new Dictionary<string, object?>
            {
                ["id"] = doc.Id,
                ["nombre"] = M.Str(M.Get(sd, "nombre")),
                ["imagen"] = M.Str(M.Get(sd, "imagen")),
                ["rareza"] = M.Str(M.Get(sd, "rareza")),
            };
        }).ToList();
    }

    /// Fija (o limpia, si skinId es null/vacío) la skin elegida del jugador para
    /// una carta en Jugadores/{uid}/Coleccion/{cartaId}.skinSeleccionada y
    /// devuelve la URL de la imagen resultante. Usado por POST /warzero/skin/seleccionar.
    public async Task<Dictionary<string, object?>> SeleccionarSkinAsync(
        string uid, string cartaId, string? skinId)
    {
        var db = _fs.Db;
        var docRef = db.Collection("Jugadores").Document(uid)
            .Collection("Coleccion").Document(cartaId);

        if (string.IsNullOrEmpty(skinId))
            await docRef.UpdateAsync("skinSeleccionada", FieldValue.Delete);
        else
            await docRef.UpdateAsync("skinSeleccionada", skinId);

        // Invalidar la caché de selección de skins de este jugador para que el
        // cambio se propague de inmediato al tablero que ven los demás (en su
        // siguiente sondeo de estado), sin esperar a que expire el TTL.
        InvalidarSkinSelCache(uid);

        // Resolver la imagen de la skin elegida para que el cliente la pinte.
        string? imagen = null;
        if (!string.IsNullOrEmpty(skinId))
        {
            var s = await db.Collection("Skins").Document(skinId).GetSnapshotAsync();
            if (s.Exists)
            {
                var sd = M.Map(M.ToJsonSafe(s.ToDictionary()));
                var url = M.Str(M.Get(sd, "imagen"));
                if (!string.IsNullOrEmpty(url)) imagen = url;
            }
        }

        return new Dictionary<string, object?>
        {
            ["ok"] = true,
            ["skinId"] = skinId,
            ["imagen"] = imagen,
        };
    }

    /// [Solo editores] Reparte una carta a la colección de TODOS los usuarios.
    /// Crea la entrada Jugadores/{uid}/Coleccion/{cartaId} (cantidad 1) para
    /// quien no la tuviera; a quien ya la posee NO se le toca la cantidad.
    /// Procesa por lotes: lee la existencia en paralelo y escribe en batch.
    /// Usado por POST /warzero/carta/repartir-todos.
    public async Task<Dictionary<string, object?>> RepartirCartaATodosAsync(string cartaId)
    {
        if (string.IsNullOrWhiteSpace(cartaId))
            throw new InvalidOperationException("cartaId es obligatorio.");

        var db = _fs.Db;

        // La carta debe existir en el catálogo global.
        var cartaSnap = await db.Collection("Cartas").Document(cartaId).GetSnapshotAsync();
        if (!cartaSnap.Exists)
            throw new InvalidOperationException($"La carta {cartaId} no existe en el catálogo.");

        // Todos los jugadores (solo necesitamos sus ids).
        var jugadoresSnap = await db.Collection("Jugadores").GetSnapshotAsync();
        var uids = jugadoresSnap.Documents.Select(d => d.Id).ToList();

        int otorgadas = 0;
        int yaTenian = 0;
        const int chunk = 200; // batch de Firestore admite hasta 500 escrituras.

        for (int i = 0; i < uids.Count; i += chunk)
        {
            var slice = uids.Skip(i).Take(chunk).ToList();

            // Leer en paralelo la entrada de colección de cada jugador.
            var getTasks = slice.ToDictionary(
                uid => uid,
                uid => db.Collection("Jugadores").Document(uid)
                         .Collection("Coleccion").Document(cartaId).GetSnapshotAsync());
            await Task.WhenAll(getTasks.Values);

            var batch = db.StartBatch();
            int enBatch = 0;
            foreach (var uid in slice)
            {
                if (getTasks[uid].Result.Exists) { yaTenian++; continue; }

                var docRef = db.Collection("Jugadores").Document(uid)
                               .Collection("Coleccion").Document(cartaId);
                batch.Set(docRef, new Dictionary<string, object?>
                {
                    ["cantidad"] = 1,
                    ["skinsDesbloqueadas"] = new List<object?>(),
                    ["fechaObtenida"] = FieldValue.ServerTimestamp,
                }, SetOptions.MergeAll);
                otorgadas++;
                enBatch++;
            }
            if (enBatch > 0) await batch.CommitAsync();
        }

        return new Dictionary<string, object?>
        {
            ["ok"] = true,
            ["cartaId"] = cartaId,
            ["jugadores"] = uids.Count,
            ["otorgadas"] = otorgadas,
            ["yaTenian"] = yaTenian,
        };
    }

    /// [Solo editores] Reparte una skin a TODOS los usuarios: la añade (arrayUnion)
    /// a `skinsDesbloqueadas` de la carta asociada en la colección de cada jugador.
    /// Si el jugador no tenía la carta, se crea la entrada con la skin desbloqueada
    /// (la cantidad ausente se interpreta como 1 al leer la colección).
    /// Usado por POST /warzero/skin/repartir-todos.
    public async Task<Dictionary<string, object?>> RepartirSkinATodosAsync(string skinId)
    {
        if (string.IsNullOrWhiteSpace(skinId))
            throw new InvalidOperationException("skinId es obligatorio.");

        var db = _fs.Db;

        // La skin debe existir y tener carta asociada.
        var skinSnap = await db.Collection("Skins").Document(skinId).GetSnapshotAsync();
        if (!skinSnap.Exists)
            throw new InvalidOperationException($"La skin {skinId} no existe.");

        var sd = M.Map(M.ToJsonSafe(skinSnap.ToDictionary()));
        var cartaId = M.Str(M.Get(sd, "cartaId", "CartaId"));
        if (string.IsNullOrEmpty(cartaId))
            throw new InvalidOperationException("La skin no tiene carta asociada (cartaId).");

        // Todos los jugadores.
        var jugadoresSnap = await db.Collection("Jugadores").GetSnapshotAsync();
        var uids = jugadoresSnap.Documents.Select(d => d.Id).ToList();

        int otorgadas = 0;
        const int chunk = 300;

        for (int i = 0; i < uids.Count; i += chunk)
        {
            var slice = uids.Skip(i).Take(chunk).ToList();

            var batch = db.StartBatch();
            foreach (var uid in slice)
            {
                var docRef = db.Collection("Jugadores").Document(uid)
                               .Collection("Coleccion").Document(cartaId);
                // MergeAll + ArrayUnion crea el doc si no existe y añade la skin
                // sin duplicar ni tocar la cantidad de quien ya la tuviera.
                batch.Set(docRef, new Dictionary<string, object?>
                {
                    ["skinsDesbloqueadas"] = FieldValue.ArrayUnion(skinId),
                }, SetOptions.MergeAll);
                otorgadas++;
            }
            await batch.CommitAsync();
        }

        return new Dictionary<string, object?>
        {
            ["ok"] = true,
            ["skinId"] = skinId,
            ["cartaId"] = cartaId,
            ["jugadores"] = uids.Count,
            ["otorgadas"] = otorgadas,
        };
    }

    /// Actualiza los stats de partida de un jugador (energías, mano/mazo, compras)
    /// de forma atómica. Devuelve las energías resultantes. Usado por POST
    /// /warzero/stats para que el cliente NO escriba en Firestore en partida.
    public async Task<Dictionary<string, object?>> ActualizarStatsAsync(StatsRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.LobbyId) || string.IsNullOrWhiteSpace(req.Uid))
            return new() { ["ok"] = false, ["error"] = "lobbyId y uid son obligatorios" };

        var db = _fs.Db;
        var lobbyRef = db.Collection("Partidas").Document(req.LobbyId);
        var updates = new Dictionary<FieldPath, object>();

        if (req.EnergiesDelta is int delta && delta != 0)
            updates[new FieldPath("statsPartida", req.Uid, "energies")] =
                FieldValue.Increment(delta);

        if (!string.IsNullOrEmpty(req.EspecialComprada))
            updates[new FieldPath("statsPartida", req.Uid, "especialesCompradas")] =
                FieldValue.ArrayUnion(req.EspecialComprada);

        if (req.RobosDelta is int robos && robos != 0)
            updates[new FieldPath("statsPartida", req.Uid, "robosComprados")] =
                FieldValue.Increment(robos);

        if (req.Mano != null)
            updates[new FieldPath("statsPartida", req.Uid, "mano")] = req.Mano;

        if (req.MazoRestante != null)
            updates[new FieldPath("statsPartida", req.Uid, "mazoRestante")] =
                req.MazoRestante;

        if (!string.IsNullOrEmpty(req.ModoBot))
            updates[new FieldPath("statsPartida", req.Uid, "modoBot")] = req.ModoBot;

        if (updates.Count > 0)
            await lobbyRef.UpdateAsync(updates);

        // Devolver las energías resultantes para que el cliente pueda reconciliar.
        int? energies = null;
        try
        {
            var snap = await lobbyRef.GetSnapshotAsync();
            if (snap.Exists)
            {
                var data = M.Map(M.FromFs(snap.ToDictionary()));
                var stats = M.Map(M.Get(data, "statsPartida"));
                if (stats.TryGetValue(req.Uid, out var s))
                    energies = M.Int(M.Get(M.Map(s), "energies"));
            }
        }
        catch { /* el valor de retorno es informativo; no rompemos por esto */ }

        return new Dictionary<string, object?>
        {
            ["ok"] = true,
            ["energies"] = energies,
        };
    }

    /// Sobrescribe "Imagen" en las entradas de [catalogoPorId] cuyo id tenga
    /// una skin seleccionada por [uid] en su colección, con la URL de esa
    /// skin. Muta los mapas in-place (misma instancia que ya vive en
    /// [catalogoPorId]), así que cualquier copia posterior por cantidad
    /// arrastra la imagen correcta sin más trabajo. Usado por
    /// MazoDelJugadorAsync para que el diseño elegido en "Mis cartas"
    /// también se vea en partida (mano, mazo restante, tablero).
    private async Task AplicarSkinsAsync(
        string uid, Dictionary<string, Dictionary<string, object?>> catalogoPorId)
    {
        var db = _fs.Db;
        var coleccionSnap = await db.Collection("Jugadores").Document(uid)
            .Collection("Coleccion").GetSnapshotAsync();

        var skinSelPorCarta = new Dictionary<string, string>();
        foreach (var doc in coleccionSnap.Documents)
        {
            if (!catalogoPorId.ContainsKey(doc.Id)) continue;
            var d = M.Map(M.ToJsonSafe(doc.ToDictionary()));
            var sel = M.Get(d, "skinSeleccionada") as string;
            if (!string.IsNullOrEmpty(sel)) skinSelPorCarta[doc.Id] = sel!;
        }
        if (skinSelPorCarta.Count == 0) return;

        var skinIds = skinSelPorCarta.Values.Distinct().ToList();
        var skinTasks = skinIds.ToDictionary(
            id => id,
            id => db.Collection("Skins").Document(id).GetSnapshotAsync());
        await Task.WhenAll(skinTasks.Values);

        foreach (var kv in skinSelPorCarta)
        {
            var snap = skinTasks[kv.Value].Result;
            if (!snap.Exists) continue;
            var sd = M.Map(M.ToJsonSafe(snap.ToDictionary()));
            var url = M.Str(M.Get(sd, "imagen"));
            if (!string.IsNullOrEmpty(url) && catalogoPorId.TryGetValue(kv.Key, out var cm))
                cm["Imagen"] = url;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // SKINS POR PROPIETARIO AL SERVIR EL TABLERO (con caché TTL)
    //
    // Problema que resuelve: cuando el jugador X cambia la skin de una carta,
    // el resto de jugadores (Z, P) debe ver esa carta de X con la skin de X.
    // Antes esto dependía de la imagen que X "horneaba" en el tablero al
    // desplegar, lo que fallaba con cartas resueltas por fallback (sin skin) y
    // se quedaba obsoleto si X cambiaba de skin a mitad de partida.
    //
    // Ahora se resuelve de forma AUTORITATIVA en LeerEstadoAsync (único punto por
    // el que el estado sale hacia TODOS los clientes: sondeo y entrar), leyendo
    // la skin seleccionada por el DUEÑO de cada carta. Como ese endpoint se
    // sondea sin parar, se cachea con TTL para no leer Firestore en cada poll:
    //   • _skinSelCache : uid    -> (cartaId -> skinId seleccionada)   [TTL corto]
    //   • _skinUrlCache : skinId -> url de la imagen                   [TTL largo]
    // Las selecciones cambian de vez en cuando (TTL corto + invalidación al
    // seleccionar); las URLs de skins son assets casi estáticos (TTL largo).
    // ─────────────────────────────────────────────────────────────────────────

    private sealed class SkinSelCacheEntry
    {
        public DateTime CargadoUtc;
        public Dictionary<string, string> PorCarta = new(); // cartaId -> skinId
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SkinSelCacheEntry>
        _skinSelCache = new();
    private static readonly TimeSpan _skinSelTtl = TimeSpan.FromSeconds(60);

    private sealed class SkinUrlCacheEntry
    {
        public DateTime CargadoUtc;
        public string? Url;
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SkinUrlCacheEntry>
        _skinUrlCache = new();
    private static readonly TimeSpan _skinUrlTtl = TimeSpan.FromMinutes(10);

    /// Invalida la caché de selección de skins de un jugador. Se llama cuando el
    /// jugador cambia su skin seleccionada, para que el cambio se propague ya sin
    /// esperar al TTL.
    private static void InvalidarSkinSelCache(string uid)
    {
        if (!string.IsNullOrEmpty(uid)) _skinSelCache.TryRemove(uid, out _);
    }

    /// Mapa cartaId -> skinId seleccionada del jugador, cacheado por TTL corto.
    /// Lee Jugadores/{uid}/Coleccion como máximo una vez por ventana de TTL. Ante
    /// un fallo de lectura devuelve lo último cacheado (o vacío): un problema de
    /// red NUNCA debe romper el estado por algo cosmético.
    private async Task<Dictionary<string, string>> ObtenerSeleccionSkinsAsync(string uid)
    {
        if (_skinSelCache.TryGetValue(uid, out var cached) &&
            (DateTime.UtcNow - cached.CargadoUtc) < _skinSelTtl)
            return cached.PorCarta;

        var porCarta = new Dictionary<string, string>();
        try
        {
            var snap = await _fs.Db.Collection("Jugadores").Document(uid)
                .Collection("Coleccion").GetSnapshotAsync();
            foreach (var doc in snap.Documents)
            {
                var d = M.Map(M.ToJsonSafe(doc.ToDictionary()));
                var sel = M.Get(d, "skinSeleccionada") as string;
                if (!string.IsNullOrEmpty(sel)) porCarta[doc.Id] = sel!;
            }
        }
        catch
        {
            if (cached != null) return cached.PorCarta;
        }

        _skinSelCache[uid] = new SkinSelCacheEntry
        {
            CargadoUtc = DateTime.UtcNow,
            PorCarta = porCarta,
        };
        return porCarta;
    }

    /// Resuelve (y cachea) la URL de imagen de un conjunto de skinIds. Solo lee de
    /// Firestore las que no estén cacheadas y frescas. Cachea también las que no
    /// existen o no tienen imagen (Url = null) para no re-leerlas en cada poll.
    private async Task<Dictionary<string, string>> ResolverUrlsSkinsAsync(
        IEnumerable<string> skinIds)
    {
        var res = new Dictionary<string, string>();
        var faltan = new List<string>();
        foreach (var id in skinIds.Distinct())
        {
            if (string.IsNullOrEmpty(id)) continue;
            if (_skinUrlCache.TryGetValue(id, out var e) &&
                (DateTime.UtcNow - e.CargadoUtc) < _skinUrlTtl)
            {
                if (!string.IsNullOrEmpty(e.Url)) res[id] = e.Url!;
            }
            else faltan.Add(id);
        }

        if (faltan.Count > 0)
        {
            var tasks = faltan.ToDictionary(
                id => id,
                id => _fs.Db.Collection("Skins").Document(id).GetSnapshotAsync());
            await Task.WhenAll(tasks.Values);
            foreach (var kv in tasks)
            {
                string? url = null;
                var s = kv.Value.Result;
                if (s.Exists)
                {
                    var sd = M.Map(M.ToJsonSafe(s.ToDictionary()));
                    var u = M.Str(M.Get(sd, "imagen", "Imagen"));
                    if (!string.IsNullOrEmpty(u)) url = u;
                }
                _skinUrlCache[kv.Key] = new SkinUrlCacheEntry
                {
                    CargadoUtc = DateTime.UtcNow,
                    Url = url,
                };
                if (!string.IsNullOrEmpty(url)) res[kv.Key] = url!;
            }
        }
        return res;
    }

    /// Sobrescribe la `Imagen` de cada carta del tablero de [estado] con la skin
    /// ACTUAL de su PROPIETARIO. Muta [estado] in-place (las cartas del tablero
    /// son las mismas instancias que se devolverán al cliente). Solo toca cartas
    /// cuyo dueño tenga una skin seleccionada que resuelva a una URL válida: si
    /// no hay selección, deja la imagen como estaba (comportamiento anterior).
    private async Task AplicarSkinsPropietarioAlEstadoAsync(
        Dictionary<string, object?> estado)
    {
        var tablero = M.Get(estado, "tablero");
        if (tablero is not Dictionary<string, object?> celdas || celdas.Count == 0)
            return;

        // 1. Recoger las cartas del tablero (referencias vivas) y los uids dueños.
        var cartas = new List<Dictionary<string, object?>>();
        var owners = new HashSet<string>();
        foreach (var celda in celdas.Values)
        {
            foreach (var c in M.List(celda))
            {
                var cm = M.Map(c);
                var owner = M.Str(M.Get(cm, "ownerUid"));
                if (string.IsNullOrEmpty(owner)) continue;
                cartas.Add(cm);
                owners.Add(owner);
            }
        }
        if (owners.Count == 0) return;

        // 2. Selección de skins de cada propietario (cacheada por TTL).
        var selPorOwner = new Dictionary<string, Dictionary<string, string>>();
        foreach (var owner in owners)
            selPorOwner[owner] = await ObtenerSeleccionSkinsAsync(owner);

        // 3. Resolver a URL todas las skins seleccionadas necesarias (cacheadas).
        var urls = await ResolverUrlsSkinsAsync(
            selPorOwner.Values.SelectMany(m => m.Values));
        if (urls.Count == 0) return;

        // 4. Sobrescribir Imagen por (ownerUid, cartaId).
        foreach (var cm in cartas)
        {
            var owner = M.Str(M.Get(cm, "ownerUid"));
            var cartaId = M.Str(M.Get(cm, "id", "Id"));
            if (string.IsNullOrEmpty(cartaId)) continue;
            if (selPorOwner.TryGetValue(owner, out var sel) &&
                sel.TryGetValue(cartaId, out var skinId) &&
                urls.TryGetValue(skinId, out var url) &&
                !string.IsNullOrEmpty(url))
            {
                cm["Imagen"] = url;
            }
        }
    }

    /// Mazo del jugador (cartas completas, expandidas por Cantidad, filtradas por
    /// ejército preservando si el filtro lo vacía), portando la lógica de
    /// MazoService.obtenerMazoParaJuego del cliente. Usado por GET /warzero/mazo.
    public async Task<List<Dictionary<string, object?>>> MazoDelJugadorAsync(
        string uid, int? ejercitoId)
    {
        var db = _fs.Db;
        var rnd = new Random();

        // Catálogo completo desde el caché compartido (copia mutable: se le
        // aplican skins abajo sin afectar al caché).
        var catalogo = await ObtenerCatalogoCartasAsync();

        // BUG reportado: el diseño (skin) elegido en "Mis cartas" no se veía
        // en partida (mano/mazo/tablero), porque este endpoint devolvía
        // siempre "Imagen" del catálogo base, sin mirar la skin seleccionada
        // del jugador. Se sobrescribe aquí, ANTES de expandir por cantidad,
        // así todas las copias de esa carta arrastran ya la imagen correcta.
        await AplicarSkinsAsync(uid, catalogo);

        int Cond(Dictionary<string, object?> m) => M.Int(M.Get(m, "Condicion"));
        int Ejer(Dictionary<string, object?> m) => M.Int(M.Get(m, "Ejercito"));
        bool EsPorDefecto(Dictionary<string, object?> m) =>
            M.Get(m, "PorDefecto") is bool b && b;

        // Esquema nuevo: array plano `cartaIds` en el doc del mazo, eligiendo el
        // mazo por ejército/principal (retro-compatible con la subcolección
        // `Cartas` + `Cantidad`). Antes se leía siempre `.Limit(1)` + subcolección
        // `Cartas`, que el editor ya no escribe, así que el mazo creado por el
        // jugador salía vacío y caía al mazo por defecto (incidencia #3).
        var mazoIds = await SeleccionarMazoIdsAsync(uid, ejercitoId);

        var resultado = new List<Dictionary<string, object?>>();

        if (mazoIds.Count > 0)
        {
            // (id, cantidad) agrupando los ids ya expandidos del mazo.
            var entradas = mazoIds.GroupBy(id => id)
                .Select(g => (id: g.Key, cant: g.Count())).ToList();

            // Expande por cantidad; NO excluye evolución/especial (igual que
            // resolverMazo del cliente: el filtrado lo hace game_screen).
            List<Dictionary<string, object?>> Construir(bool conFiltro)
            {
                var res = new List<Dictionary<string, object?>>();
                foreach (var (id, cant) in entradas)
                {
                    if (!catalogo.TryGetValue(id, out var cm)) continue;
                    if (conFiltro && ejercitoId != null && Ejer(cm) != ejercitoId) continue;
                    for (int q = 0; q < cant; q++) res.Add(cm);
                }
                return res;
            }

            resultado = Construir(true);
            if (resultado.Count == 0) resultado = Construir(false); // preservar mazo
        }

        // Sin mazo guardado utilizable (o resolvió vacío tras el filtro): mazo
        // por defecto, para no dejar al jugador sin cartas.
        if (resultado.Count == 0)
        {
            // Mazo por defecto: catálogo sin evoluciones ni especiales.
            var basicas = catalogo.Values
                .Where(m => Cond(m) != 1 && Cond(m) != 5)
                .ToList();
            var filtradas = ejercitoId != null
                ? basicas.Where(m => Ejer(m) == ejercitoId).ToList()
                : basicas;
            if (filtradas.Count == 0) filtradas = basicas;

            var marcadas = filtradas.Where(EsPorDefecto).ToList();
            var fuente = marcadas.Count > 0 ? marcadas : filtradas;

            resultado = fuente.OrderBy(_ => rnd.Next())
                .Take(TamanioMazoDefecto).ToList();
        }

        return resultado;
    }

    /// Cartas del catálogo por sus IDs (con id inyectado), para resolver
    /// evoluciones y mano/mazo en el cliente. Usado por GET /warzero/cartas.
    public async Task<List<Dictionary<string, object?>>> CartasPorIdsAsync(
        IEnumerable<string> ids)
    {
        var db = _fs.Db;
        var distinct = ids.Where(s => !string.IsNullOrEmpty(s)).Distinct().ToList();
        if (distinct.Count == 0) return new();

        var tasks = distinct.ToDictionary(
            id => id,
            id => db.Collection("Cartas").Document(id).GetSnapshotAsync());
        await Task.WhenAll(tasks.Values);

        var res = new List<Dictionary<string, object?>>();
        foreach (var kv in tasks)
        {
            var snap = kv.Value.Result;
            if (!snap.Exists) continue;
            var m = M.Map(M.ToJsonSafe(snap.ToDictionary()));
            m["id"] = snap.Id;
            res.Add(m);
        }
        return res;
    }

    /// Terreno de un mapa: { coord: "sea"|"deepSea"|"amphibious"|"land" }.
    /// Devuelve null si el mapa no existe. Usado por GET /warzero/mapa.
    public async Task<Dictionary<string, object?>?> MapaTerrenoAsync(string mapaId)
    {
        var db = _fs.Db;
        var snap = await db.Collection("Mapas").Document(mapaId).GetSnapshotAsync();
        if (!snap.Exists) return null;

        var data = M.Map(M.ToJsonSafe(snap.ToDictionary()));
        var terreno = M.Map(M.Get(data, "terreno"));
        // filas/columnas: para que el cliente dibuje la rejilla REAL del mapa,
        // que puede ser mayor que el preset del nº de jugadores (p. ej. 12×20 en
        // una partida de 8, cuyo preset es 12×18). Sin esto, las columnas/filas
        // extra no existen en juego y los obeliscos/continentes que caen ahí se
        // salen del tablero.
        return new Dictionary<string, object?>
        {
            ["terreno"] = terreno,
            ["filas"] = M.Int(M.Get(data, "filas")),
            ["columnas"] = M.Int(M.Get(data, "columnas")),
            // imagen de fondo del tablero (ruta de asset o URL). Sin esto, la
            // partida usaba la imagen por defecto (map_background.png) en vez de
            // la del mapa, y quedaba desalineada sobre la rejilla real.
            ["imagen"] = M.Str(M.Get(data, "imagen")),
        };
    }

    /// Historias del jugador: catálogo `Historias` + estado de desbloqueo del
    /// jugador (campo historiasDesbloqueadas). Las bloqueadas se devuelven SIN
    /// título ni páginas (para no destripar el contenido). Usado por
    /// GET /warzero/historias.
    public async Task<List<Dictionary<string, object?>>> HistoriasAsync(string uid)
    {
        var db = _fs.Db;

        var jugadorTask = db.Collection("Jugadores").Document(uid).GetSnapshotAsync();
        var historiasTask = db.Collection("Historias").GetSnapshotAsync();
        await Task.WhenAll(jugadorTask, historiasTask);

        var desbloqueadas = new HashSet<string>();
        if (jugadorTask.Result.Exists)
        {
            var jd = M.Map(M.ToJsonSafe(jugadorTask.Result.ToDictionary()));
            foreach (var s in M.List(M.Get(jd, "historiasDesbloqueadas")))
            {
                var id = M.Str(s);
                if (!string.IsNullOrEmpty(id)) desbloqueadas.Add(id);
            }
        }

        var res = new List<Dictionary<string, object?>>();
        foreach (var doc in historiasTask.Result.Documents)
        {
            var d = M.Map(M.ToJsonSafe(doc.ToDictionary()));

            // Una historia marcada `PorDefecto` está desbloqueada para TODOS los
            // jugadores sin necesidad de conseguirla ni de tocar el documento de
            // cada jugador: son las historias "de bienvenida" o de tutorial que
            // el editor decide dejar abiertas para todo el mundo.
            var porDefecto = M.Bool(M.Get(d, "PorDefecto"));
            var abierta = porDefecto || desbloqueadas.Contains(doc.Id);

            var item = new Dictionary<string, object?>
            {
                ["id"] = doc.Id,
                ["ejercito"] = M.Int(M.Get(d, "Ejercito")),
                ["orden"] = M.Int(M.Get(d, "Orden")),
                ["desbloqueada"] = abierta,
                ["porDefecto"] = porDefecto,
            };

            // Solo se envía el contenido si está desbloqueada.
            if (abierta)
            {
                item["titulo"] = M.Str(M.Get(d, "Titulo"));
                item["paginas"] = M.List(M.Get(d, "Paginas")).Select(p =>
                {
                    var pm = M.Map(p);
                    return (object?)new Dictionary<string, object?>
                    {
                        ["imagen"] = M.Str(M.Get(pm, "imagen") ?? M.Get(pm, "Imagen")),
                        ["descripcion"] =
                            M.Str(M.Get(pm, "descripcion") ?? M.Get(pm, "Descripcion")),
                        ["orden"] = M.Int(M.Get(pm, "orden") ?? M.Get(pm, "Orden")),
                    };
                }).ToList();
            }

            res.Add(item);
        }
        return res;
    }

    /// Marca una historia como conseguida por el jugador (arrayUnion). Crea el
    /// doc/campo si no existieran. Usado por POST /warzero/historia/desbloquear.
    public async Task<Dictionary<string, object?>> DesbloquearHistoriaAsync(
        string uid, string historiaId)
    {
        if (string.IsNullOrWhiteSpace(uid) || string.IsNullOrWhiteSpace(historiaId))
            return new() { ["ok"] = false, ["error"] = "uid e historiaId son obligatorios" };

        var db = _fs.Db;
        await db.Collection("Jugadores").Document(uid).SetAsync(
            new Dictionary<string, object>
            {
                ["historiasDesbloqueadas"] = FieldValue.ArrayUnion(historiaId),
            },
            SetOptions.MergeAll);

        return new Dictionary<string, object?> { ["ok"] = true };
    }

    private const int EnergiasIniciales = 15;
    private const int TamanioManoInicial = 5;
    private const int TamanioMazoDefecto = 8;

    /// Entrada a la partida: inicializa de forma atómica las energías de inicio,
    /// el obelisco y la mano/mazo del jugador si aún no los tiene, y devuelve el
    /// estado completo.
    public async Task<EntrarResponse> EntrarAsync(EntrarRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.LobbyId) || string.IsNullOrWhiteSpace(req.Uid))
            return new EntrarResponse { Existe = false };

        var db = _fs.Db;
        var lobbyRef = db.Collection("Partidas").Document(req.LobbyId);

        // Resolución forzosa perezosa: si el límite (00:00 UTC) venció, se resuelve
        // el turno pendiente antes de que este jugador entre.
        await ForzarResolucionSiProcedeAsync(req.LobbyId);

        // ── Pre-lectura (fuera de la transacción) para decidir si hay que ──────
        // repartir mano. El reparto lee colecciones (Mazos, Cartas) que no
        // conviene leer dentro de la transacción.
        var pre = await lobbyRef.GetSnapshotAsync();
        if (!pre.Exists) return new EntrarResponse { Existe = false };
        var preData = M.Map(M.FromFs(pre.ToDictionary()));

        var preStats = M.Map(M.Get(preData, "statsPartida"));
        var preMiStat = preStats.TryGetValue(req.Uid, out var ps) ? M.Map(ps) : null;
        var yaTieneMano = preMiStat != null && preMiStat.ContainsKey("mano");

        // Candidatos de obelisco/cuartel: PRIMERO los definidos en el mapa
        // (herramienta de diseño → campo `obeliscos`), y si el mapa no los
        // define, se usa el fallback hardcodeado (esquinas de un 6x10).
        var playerCount = M.List(M.Get(preData, "jugadores")).Count;
        List<string> obeliscoCandidatos = Coords.ObeliscosFallback(playerCount);
        var mapaIdPre = M.Str(M.Get(preData, "mapaId"));
        if (mapaIdPre != "")
        {
            try
            {
                var mapaSnapPre = await db.Collection("Mapas").Document(mapaIdPre)
                    .GetSnapshotAsync();
                if (mapaSnapPre.Exists)
                {
                    var mapDataPre = M.Map(M.FromFs(mapaSnapPre.ToDictionary()));
                    var obDef = M.List(M.Get(mapDataPre, "obeliscos")).Select(M.Str)
                        .Where(s => s != "").ToList();
                    if (obDef.Count == 0)
                    {
                        // Fallback: las claves de `continentes` SON los obeliscos.
                        obDef = M.Map(M.Get(mapDataPre, "continentes")).Keys
                            .Where(k => k != "").ToList();
                    }
                    if (obDef.Count > 0) obeliscoCandidatos = obDef;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(
                    "[WarZero.Entrar] leer obeliscos del mapa falló: " + ex);
            }
        }

        List<string>? manoIds = null;
        List<string>? mazoRestanteIds = null;
        List<string>? mazoPoolIds = null;
        if (!yaTieneMano)
        {
            try
            {
                var ejercitoId = EjercitoDeJugador(preData, req.Uid);
                var enTablero = CartasEnTableroDe(preData, req.Uid);
                var (mano, resto, pool) =
                    await RepartirManoAsync(req.Uid, ejercitoId, enTablero);
                manoIds = mano;
                mazoRestanteIds = resto;
                mazoPoolIds = pool;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("[WarZero.Entrar] reparto mano falló: " + ex);
            }
        }

        var resp = await db.RunTransactionAsync(async tx =>
        {
            var snap = await tx.GetSnapshotAsync(lobbyRef);
            if (!snap.Exists) return new EntrarResponse { Existe = false };

            var data = M.Map(M.FromFs(snap.ToDictionary()));
            var updates = new Dictionary<FieldPath, object>();
            int? energiasAsignadas = null;
            string? obeliscoAsignado = null;

            // Si la partida ya está finalizada, este jugador está ENTRANDO a ver
            // el resultado (mensaje de victoria / fin). Lo marcamos como que ya lo
            // ha visto para que MisPartidasAsync deje de mostrarle la partida a
            // partir de ahora (no debe desaparecer ANTES de entrar, pero sí
            // después). Con ArrayUnion es idempotente y a prueba de concurrencia.
            if (M.Str(M.Get(data, "estado")) == "finalizada")
            {
                updates[new FieldPath("resultadoVistoPor")] =
                    FieldValue.ArrayUnion(req.Uid);
            }

            var stats = M.Map(M.Get(data, "statsPartida"));
            var miStat = stats.TryGetValue(req.Uid, out var s) ? M.Map(s) : null;

            // 1) Energías de inicio.
            if (miStat == null || !miStat.ContainsKey("energies"))
            {
                updates[new FieldPath("statsPartida", req.Uid, "energies")] =
                    EnergiasIniciales;
                energiasAsignadas = EnergiasIniciales;
            }

            // 2) Obeliscos. Se asignan de una vez a TODOS los jugadores que aún
            // no tengan cuartel (no solo al que entra): así, en la PRIMERA
            // entrada a una partida recién creada el mapa de obeliscos ya está
            // completo y el tablero pinta las posiciones correctas. (Antes se
            // asignaban perezosamente, de ahí que hubiera que reentrar.)
            var obeliscos = M.Map(M.Get(data, "obeliscos"));
            var jugadoresUids = M.List(M.Get(data, "jugadores"))
                .Select(j => M.Str(M.Get(M.Map(j), "uid")))
                .Where(u => u != "")
                .ToList();

            var ocupadas = obeliscos.Values.Select(M.Str).ToHashSet();
            var libres = obeliscoCandidatos.Where(c => !ocupadas.Contains(c)).ToList();

            // Mezcla determinista y estable por lobby (no depende de Random dentro
            // de la transacción, que puede reintentarse).
            int seed = 0;
            foreach (var ch in req.LobbyId) seed = unchecked(seed * 31 + ch);
            var rnd = new Random(seed);
            libres = libres.OrderBy(_ => rnd.Next()).ToList();

            int idxLibre = 0;
            foreach (var uid in jugadoresUids)
            {
                if (obeliscos.ContainsKey(uid)) continue;   // ya tiene cuartel
                if (idxLibre >= libres.Count) break;         // sin candidatos libres
                var elegido = libres[idxLibre++];
                updates[new FieldPath("obeliscos", uid)] = elegido;
                if (uid == req.Uid) obeliscoAsignado = elegido;
            }

            // 3) Mano/mazo (solo si sigue sin tenerla y la pudimos repartir).
            var tieneMano = miStat != null && miStat.ContainsKey("mano");
            if (!tieneMano && manoIds != null && mazoRestanteIds != null)
            {
                updates[new FieldPath("statsPartida", req.Uid, "mano")] = manoIds;
                updates[new FieldPath("statsPartida", req.Uid, "mazoRestante")] =
                    mazoRestanteIds;
                // mazoPool = mazo completo (pool de robo de fin de turno, bug QAS #2).
                if (mazoPoolIds != null)
                    updates[new FieldPath("statsPartida", req.Uid, "mazoPool")] =
                        mazoPoolIds;
            }

            if (updates.Count > 0) tx.Update(lobbyRef, updates);

            return new EntrarResponse
            {
                Existe = true,
                TurnoActual = M.Int(M.Get(data, "turnoActual")),
                EnergiasAsignadas = energiasAsignadas,
                ObeliscoAsignado = obeliscoAsignado,
            };
        });

        // Tras commit, adjunta el estado completo (ya con la init aplicada).
        if (resp.Existe)
        {
            try { resp.Estado = await LeerEstadoAsync(req.LobbyId); }
            catch (Exception ex)
            {
                Console.Error.WriteLine("[WarZero] LeerEstado tras entrar falló: " + ex);
            }
            if (resp.Estado != null)
                resp.TurnoActual = resp.Estado.TryGetValue("turnoActual", out var t) && t is long l
                    ? (int)l : resp.TurnoActual;
        }
        return resp;
    }

    /// Partidas en las que el jugador es participante (no finalizadas y donde
    /// sigue presente). Mismo criterio que LobbyService.misPartidasStream del
    /// cliente, pero por HTTP para no depender del realtime de Firestore.
    /// Devuelve cada doc serializado JSON-safe (mismo shape que Firestore) con su
    /// id inyectado; el cliente lo convierte con LobbyModel.fromMap.
    public async Task<List<Dictionary<string, object?>>> MisPartidasAsync(string uid)
    {
        // OPTIMIZACIÓN DE LECTURAS (crítica): el método antiguo consultaba
        // `Partidas WhereArrayContains("participantes", uid)` SIN filtrar por
        // estado, así que leía TODAS las partidas en las que el jugador había
        // participado alguna vez —incluidas TODAS las finalizadas—. Ese conjunto
        // crece sin límite según se acumulan partidas y el cliente lo relee en
        // cada refresco de "mis partidas": era un drenaje enorme e independiente
        // de que jugaran o no los bots. Ahora se consultan SOLO las activas y las
        // ganadas-no-vistas (acotadas). Si faltan los índices compuestos, se cae
        // al método antiguo para no romper la pantalla hasta desplegarlos.
        try { return await MisPartidasFiltradaAsync(uid); }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                "[WarZero] MisPartidas filtrada falló (¿faltan índices?), uso legacy: " + ex);
            return await MisPartidasLegacyAsync(uid);
        }
    }

    /// Consulta ACOTADA de "mis partidas": activas del jugador (esperando/en_curso)
    /// y, aparte, las GANADAS por él y aún no vistas (para mostrar la victoria).
    /// Requiere índices compuestos (ver firestore.indexes.json):
    ///   Partidas: participantes(array-contains) + estado(asc)
    ///   Partidas: ganadorUid(asc) + estado(asc)
    private async Task<List<Dictionary<string, object?>>> MisPartidasFiltradaAsync(string uid)
    {
        var col = _fs.Db.Collection("Partidas");

        var esperandoTask = col
            .WhereArrayContains("participantes", uid)
            .WhereEqualTo("estado", "esperando")
            .GetSnapshotAsync();
        var enCursoTask = col
            .WhereArrayContains("participantes", uid)
            .WhereEqualTo("estado", "en_curso")
            .GetSnapshotAsync();
        // Ganadas por el jugador y aún no vistas. Acotado: se ganan pocas, y el
        // límite evita cualquier crecimiento.
        var ganadasTask = col
            .WhereEqualTo("ganadorUid", uid)
            .WhereEqualTo("estado", "finalizada")
            .Limit(10)
            .GetSnapshotAsync();

        await Task.WhenAll(esperandoTask, enCursoTask, ganadasTask);

        var result = new List<Dictionary<string, object?>>();
        var vistos = new HashSet<string>();

        // Partidas ACTIVAS (esperando / en curso) en las que el jugador sigue.
        foreach (var snap in new[] { esperandoTask.Result, enCursoTask.Result })
        {
            foreach (var doc in snap.Documents)
            {
                if (!vistos.Add(doc.Id)) continue;
                var data = M.Map(M.ToJsonSafe(doc.ToDictionary()));
                var sigue = M.List(M.Get(data, "jugadores"))
                    .Select(j => M.Str(M.Get(M.Map(j), "uid")))
                    .Any(u => u == uid);
                if (!sigue) continue;
                data["id"] = doc.Id;
                result.Add(data);
            }
        }

        // GANADAS no vistas: mismas reglas que antes (solo el ganador, y solo
        // hasta que entra a ver el resultado → resultadoVistoPor lo contiene).
        foreach (var doc in ganadasTask.Result.Documents)
        {
            if (!vistos.Add(doc.Id)) continue;
            var data = M.Map(M.ToJsonSafe(doc.ToDictionary()));
            var sigue = M.List(M.Get(data, "jugadores"))
                .Select(j => M.Str(M.Get(M.Map(j), "uid")))
                .Any(u => u == uid);
            if (!sigue) continue;
            var vistoPor = M.List(M.Get(data, "resultadoVistoPor")).Select(M.Str).ToHashSet();
            if (vistoPor.Contains(uid)) continue;
            data["id"] = doc.Id;
            result.Add(data);
        }

        return result;
    }

    /// Método antiguo (fallback): lee TODAS las partidas del jugador. Solo se usa
    /// si la consulta filtrada falla (p. ej. índices aún no desplegados).
    private async Task<List<Dictionary<string, object?>>> MisPartidasLegacyAsync(string uid)
    {
        var db = _fs.Db;
        var snap = await db.Collection("Partidas")
            .WhereArrayContains("participantes", uid)
            .GetSnapshotAsync();

        var result = new List<Dictionary<string, object?>>();
        foreach (var doc in snap.Documents)
        {
            var data = M.Map(M.ToJsonSafe(doc.ToDictionary()));
            var estado = M.Str(M.Get(data, "estado"));

            // El jugador sigue presente en jugadores[].
            var sigue = M.List(M.Get(data, "jugadores"))
                .Select(j => M.Str(M.Get(M.Map(j), "uid")))
                .Any(u => u == uid);
            if (!sigue) continue;

            if (estado == "finalizada")
            {
                // Se mantiene visible SOLO para el ganador y SOLO hasta que ha
                // entrado a ver el resultado (EntrarAsync lo añade a
                // `resultadoVistoPor`). El resto de jugadores ya vieron su aviso.
                var ganador = M.Str(M.Get(data, "ganadorUid"));
                if (ganador == "" || ganador != uid) continue;
                var vistoPor = M.List(M.Get(data, "resultadoVistoPor"))
                    .Select(M.Str).ToHashSet();
                if (vistoPor.Contains(uid)) continue;
            }

            data["id"] = doc.Id;
            result.Add(data);
        }
        return result;
    }

    /// Partidas públicas en espera (pestaña PÚBLICAS). Filtra por estado en el
    /// servidor (índice de campo único) y descarta privadas; cada doc va
    /// serializado JSON-safe con su id inyectado.
    public async Task<List<Dictionary<string, object?>>> PublicasAsync()
    {
        var db = _fs.Db;
        var snap = await db.Collection("Partidas")
            .WhereEqualTo("estado", "esperando")
            .Limit(50)
            .GetSnapshotAsync();

        var result = new List<Dictionary<string, object?>>();
        foreach (var doc in snap.Documents)
        {
            var data = M.Map(M.ToJsonSafe(doc.ToDictionary()));
            var esPrivada = M.Get(data, "esPrivada") is bool b && b;
            if (esPrivada) continue;
            data["id"] = doc.Id;
            result.Add(data);
        }
        return result;
    }

    /// Datos de la pantalla MIS MAZOS (sin Firestore en el cliente): ejércitos,
    /// catálogo de cartas y perfiles de mazo del jugador. Equivale a las tres
    /// lecturas que hacía el cliente (EjercitoService + MazoService +
    /// Jugadores/{uid}/Mazos). Usado por GET /warzero/mismazos.
    public async Task<Dictionary<string, object?>> MisMazosAsync(string uid)
    {
        var db = _fs.Db;

        // Lecturas en paralelo. El catálogo de cartas viene del caché compartido
        // (Ejercitos es pequeño y estático; se deja como lectura directa).
        var ejercitosTask = db.Collection("Ejercitos").GetSnapshotAsync();
        var catalogoTask = ObtenerCatalogoCartasAsync();
        var mazosTask = db.Collection("Jugadores").Document(uid)
            .Collection("Mazos").GetSnapshotAsync();
        await Task.WhenAll(ejercitosTask, catalogoTask, mazosTask);

        // Ejércitos: docId numérico → { id, nombre, descripcion, icono }.
        var ejercitos = new List<Dictionary<string, object?>>();
        foreach (var doc in ejercitosTask.Result.Documents)
        {
            var d = M.Map(M.ToJsonSafe(doc.ToDictionary()));
            ejercitos.Add(new Dictionary<string, object?>
            {
                ["id"] = int.TryParse(doc.Id, out var idn) ? idn : 0,
                ["nombre"] = M.Str(M.Get(d, "Nombre")),
                ["descripcion"] = M.Str(M.Get(d, "Descripcion")),
                ["icono"] = M.Str(M.Get(d, "Icono")),
            });
        }
        ejercitos.Sort((a, b) => M.Int(a["id"]).CompareTo(M.Int(b["id"])));

        // Catálogo de cartas completo (id inyectado), mismo shape que /warzero/mazo.
        var cartas = catalogoTask.Result.Values
            .Select(m => new Dictionary<string, object?>(m))
            .ToList();

        // Perfiles de mazo del jugador.
        var mazos = new List<Dictionary<string, object?>>();
        foreach (var doc in mazosTask.Result.Documents)
        {
            var d = M.Map(M.ToJsonSafe(doc.ToDictionary()));
            mazos.Add(new Dictionary<string, object?>
            {
                ["id"] = doc.Id,
                ["nombre"] = M.Str(M.Get(d, "nombre")),
                ["ejercitoId"] = M.Int(M.Get(d, "ejercitoId")),
                ["esPrincipal"] = M.Get(d, "esPrincipal") is bool eb && eb,
                ["cartaIds"] = M.List(M.Get(d, "cartaIds")).Select(M.Str).Cast<object?>().ToList(),
                ["total"] = M.Int(M.Get(d, "total")),
            });
        }

        return new Dictionary<string, object?>
        {
            ["ejercitos"] = ejercitos.Cast<object?>().ToList(),
            ["cartas"] = cartas.Cast<object?>().ToList(),
            ["mazos"] = mazos.Cast<object?>().ToList(),
        };
    }

    /// Ejército elegido por el jugador en la sala (de `jugadores[].ejercitoId`).
    private static int? EjercitoDeJugador(Dictionary<string, object?> data, string uid)
    {
        foreach (var j in M.List(M.Get(data, "jugadores")))
        {
            var jm = M.Map(j);
            if (M.Str(M.Get(jm, "uid")) == uid)
            {
                var e = M.Get(jm, "ejercitoId");
                return e == null ? (int?)null : M.Int(e);
            }
        }
        return null;
    }

    /// IDs de cartas que el jugador ya tiene colocadas en el tablero.
    private static HashSet<string> CartasEnTableroDe(
        Dictionary<string, object?> data, string uid)
    {
        var ids = new HashSet<string>();
        foreach (var celda in M.Map(M.Get(data, "tablero")).Values)
        {
            foreach (var c in M.List(celda))
            {
                var cm = M.Map(c);
                if (M.Str(M.Get(cm, "ownerUid")) == uid)
                {
                    var id = M.Str(M.Get(cm, "id"));
                    if (!string.IsNullOrEmpty(id)) ids.Add(id);
                }
            }
        }
        return ids;
    }

    /// Devuelve los IDs de carta (ya expandidos por cantidad) del mazo elegido
    /// del jugador para el [ejercitoId] indicado. Lee el ESQUEMA NUEVO que
    /// escribe el editor de mazos (mazo_screen.dart): un array plano `cartaIds`
    /// en el propio documento del mazo. Es retro-compatible con el esquema
    /// antiguo (subcolección `Cartas` con campo `Cantidad`) por si quedan mazos
    /// viejos. Devuelve lista vacía si el jugador no tiene ningún mazo guardado.
    ///
    /// Elección del mazo (igual que MazoService._elegirMazo del cliente, ya que
    /// `esPrincipal` es por ejército):
    ///   1) principal del ejército  2) cualquiera del ejército
    ///   3) principal global        4) el primero
    private async Task<List<string>> SeleccionarMazoIdsAsync(string uid, int? ejercitoId)
    {
        var db = _fs.Db;
        var mazosSnap = await db.Collection("Jugadores").Document(uid)
            .Collection("Mazos").GetSnapshotAsync();
        if (mazosSnap.Count == 0) return new List<string>();

        var docs = mazosSnap.Documents.ToList();

        int? EjerDe(DocumentSnapshot d)
        {
            var v = M.Get(M.Map(d.ToDictionary()), "ejercitoId");
            return v is null ? (int?)null : M.Int(v);
        }
        bool PrincipalDe(DocumentSnapshot d) =>
            M.Bool(M.Get(M.Map(d.ToDictionary()), "esPrincipal"));

        DocumentSnapshot? elegido = null;
        if (ejercitoId != null)
        {
            var delEjercito = docs.Where(d => EjerDe(d) == ejercitoId).ToList();
            if (delEjercito.Count > 0)
                elegido = delEjercito.FirstOrDefault(PrincipalDe) ?? delEjercito[0];
        }
        elegido ??= docs.FirstOrDefault(PrincipalDe) ?? docs[0];

        // Esquema nuevo: array plano `cartaIds` (ya expandido por cantidad).
        var cartaIds = M.List(M.Get(M.Map(elegido.ToDictionary()), "cartaIds"))
            .Select(M.Str).Where(s => s != "").ToList();
        // Tope del mazo del jugador: si tiene mazo elegido, se usan como mucho
        // sus TamanioMazoDefecto (8) primeras cartas en el orden guardado. El
        // editor limita a 8, pero ese tope no era autoritativo (mazos antiguos o
        // editados fuera del editor podían tener más), así que se recorta aquí.
        if (cartaIds.Count > 0) return cartaIds.Take(TamanioMazoDefecto).ToList();

        // Retro-compatibilidad: esquema antiguo (subcolección `Cartas` + `Cantidad`).
        var deckCartasSnap = await elegido.Reference.Collection("Cartas").GetSnapshotAsync();
        var ids = new List<string>();
        foreach (var c in deckCartasSnap.Documents)
        {
            var cant = M.Int(M.Map(c.ToDictionary()).GetValueOrDefault("Cantidad"));
            if (cant <= 0) cant = 1;
            for (int q = 0; q < cant; q++) ids.Add(c.Id);
        }
        // Mismo tope para el esquema antiguo expandido por Cantidad.
        return ids.Take(TamanioMazoDefecto).ToList();
    }

    /// Reparte la mano inicial y el mazo restante del jugador (listas de IDs de
    /// carta), portando la lógica de MazoService del cliente: usa el mazo del
    /// jugador (elegido por ejército/principal, expandido por cantidad) o un mazo
    /// por defecto si no tiene; excluye evoluciones; filtra por ejército
    /// (preservando el mazo si el filtro lo vacía); excluye cartas ya colocadas
    /// en el tablero; y baraja.
    private async Task<(List<string> mano, List<string> resto, List<string> pool)>
        RepartirManoAsync(
        string uid, int? ejercitoId, HashSet<string> cartasEnTablero)
    {
        var db = _fs.Db;
        var rnd = new Random();
        var poolIds = new List<string>();

        // Esquema nuevo: array plano `cartaIds` en el doc del mazo elegido por
        // ejército/principal (retro-compatible con la subcolección `Cartas`).
        // Antes se leía `.Limit(1)` + subcolección `Cartas`, que el editor ya no
        // escribe, así que el mazo creado por el jugador salía vacío (incidencia #3).
        var mazoIds = await SeleccionarMazoIdsAsync(uid, ejercitoId);

        // Catálogo cacheado (compartido): evita releer `Cartas` aquí en cada
        // arranque de partida, tanto para los metadatos como para el mazo por
        // defecto de más abajo.
        var catalogo = await ObtenerCatalogoCartasAsync();

        if (mazoIds.Count > 0)
        {
            // (idCarta, cantidad) agrupando los ids ya expandidos del mazo.
            var entradas = mazoIds.GroupBy(id => id)
                .Select(g => (id: g.Key, cant: g.Count())).ToList();

            // Condicion + Ejercito de cada carta distinta, desde el catálogo en
            // memoria (antes: una lectura de Firestore por carta distinta).
            var metas = new Dictionary<string, (int cond, int ejer)>();
            foreach (var (id, _) in entradas)
            {
                if (metas.ContainsKey(id)) continue;
                if (!catalogo.TryGetValue(id, out var cd)) { metas[id] = (-1, -1); continue; }
                metas[id] = (M.Int(M.Get(cd, "Condicion")), M.Int(M.Get(cd, "Ejercito")));
            }

            List<string> Construir(bool conFiltro)
            {
                var res = new List<string>();
                foreach (var (id, cant) in entradas)
                {
                    if (!metas.TryGetValue(id, out var m) || m.cond < 0) continue;
                    if (m.cond == 1 || m.cond == 5) continue; // evolución/especial: no se reparten
                    if (conFiltro && ejercitoId != null && m.ejer != ejercitoId) continue;
                    for (int q = 0; q < cant; q++) res.Add(id);
                }
                return res;
            }

            poolIds = Construir(true);
            if (poolIds.Count == 0) poolIds = Construir(false); // preservar mazo
        }

        // Sin mazo guardado utilizable (o resolvió vacío tras el filtro): mazo
        // por defecto, para no dejar al jugador sin cartas.
        if (poolIds.Count == 0)
        {
            // Mazo por defecto: catálogo completo (cacheado), sin evoluciones ni
            // especiales.
            var basicas = catalogo
                .Select(kv => (id: kv.Key, cd: kv.Value))
                .Where(x => M.Int(M.Get(x.cd, "Condicion")) != 1
                    && M.Int(M.Get(x.cd, "Condicion")) != 5)
                .ToList();
            var filtradas = ejercitoId != null
                ? basicas.Where(x => M.Int(M.Get(x.cd, "Ejercito")) == ejercitoId).ToList()
                : basicas;
            if (filtradas.Count == 0) filtradas = basicas;

            // Preferir las cartas marcadas como "mazo por defecto" (PorDefecto).
            var marcadas = filtradas
                .Where(x => M.Get(x.cd, "PorDefecto") is bool b && b)
                .ToList();
            var fuente = marcadas.Count > 0 ? marcadas : filtradas;

            poolIds = fuente.OrderBy(_ => rnd.Next())
                .Take(TamanioMazoDefecto).Select(x => x.id).ToList();
        }

        // Excluir cartas ya en el tablero (por id) y barajar.
        var pool = poolIds.Where(id => !cartasEnTablero.Contains(id))
            .OrderBy(_ => rnd.Next()).ToList();

        var mano = pool.Take(TamanioManoInicial).ToList();
        var resto = pool.Skip(TamanioManoInicial).ToList();
        // `poolIds` es el mazo COMPLETO del jugador (expandido por cantidad, sin
        // evoluciones/especiales y SIN excluir on-board): es el pool de robo de
        // fin de turno (con repetición). Coincide con `_mazoCompleto` del cliente.
        return (mano, resto, poolIds);
    }

    // ── Helpers de serialización ──────────────────────────────────────────────

    private static EfectosCelda ParseEfectosCelda(object? raw)
    {
        var result = new EfectosCelda();
        foreach (var kv in M.Map(raw))
        {
            var lista = M.List(kv.Value).Select(M.Map).ToList();
            if (lista.Count > 0) result[kv.Key] = lista;
        }
        return result;
    }
    // Interruptor de la validación del simulador (Tarea 1 del lookahead). Con
    // true, cada resolución compara el tablero real con el de SimuladorTurno y lo
    // registra ([SIM][OK] / [SIM][MISMATCH]). Dejar en false en producción.
    private static readonly bool ValidarSimulador = false;

    // Firma estructural de un tablero: coord -> instanceIds ordenados. Ignora
    // campos incidentales (turnosEnCelda, etc.); compara qué cartas quedan y dónde.
    private static string FirmaTablero(Tablero t) => string.Join("|",
        t.Where(kv => kv.Value.Count > 0)
         .OrderBy(kv => kv.Key)
         .Select(kv => kv.Key + ":" + string.Join(",",
             kv.Value.Select(c => M.Str(M.Get(c, "instanceId"))).OrderBy(s => s))));
    private static Dictionary<string, object> ToFsTablero(Tablero t)
    {
        var o = new Dictionary<string, object>();
        foreach (var kv in t) o[kv.Key] = kv.Value.Cast<object>().ToList();
        return o;
    }

    private static Dictionary<string, object> ToFsEfectos(EfectosCelda e)
    {
        var o = new Dictionary<string, object>();
        foreach (var kv in e) o[kv.Key] = kv.Value.Cast<object>().ToList();
        return o;
    }

    private static List<object?> BuildMovimientosLog(
        Dictionary<string, object?> movTurno, int turno,
        Dictionary<string, string> obeliscos,
        HashSet<string>? invisibles = null)
    {
        var ocultas = invisibles ?? new HashSet<string>();
        var log = new List<object?>();
        foreach (var kv in movTurno)
        {
            var mov = M.Map(kv.Value);
            if (M.Int(M.Get(mov, "turno")) != turno) continue;
            var uid = M.Str(M.Get(mov, "uid"));
            var miCuartel = obeliscos.GetValueOrDefault(uid, "");
            var celdasSrc = M.Map(M.Get(mov, "celdas"));

            // Issue #5: las cartas jugadas al PROPIO cuartel no se muestran en el
            // informe (misterio sobre qué hay dentro). Se descarta esa celda.
            var celdas = new Dictionary<string, object?>();
            foreach (var ce in celdasSrc)
            {
                if (miCuartel != "" && ce.Key == miCuartel) continue;
                // Ocultar las cartas INVISIBLES del informe de movimientos: no
                // deben delatar su posición ni a rivales ni en la revisión de
                // turno. Se filtran por instanceId (capturado del tablero de esta
                // resolución). Si la celda queda vacía, se omite.
                var visibles = M.List(ce.Value)
                    .Where(c =>
                    {
                        var iid = M.Str(M.Get(M.Map(c), "instanceId"));
                        return iid == "" || !ocultas.Contains(iid);
                    })
                    .ToList();
                if (visibles.Count == 0) continue;
                celdas[ce.Key] = visibles;
            }

            var zona = "";
            foreach (var ce in celdas)
            {
                var cartas = M.List(ce.Value);
                if (cartas.Count > 0)
                {
                    zona = M.Str(M.Get(M.Map(cartas[0]), "ownerZone"));
                    if (zona != "") break;
                }
            }
            // Si tras filtrar (cuartel propio + cartas invisibles) no queda
            // ninguna celda con cartas, no se registra la entrada: así un rival
            // que solo movió cartas invisibles no aparece en el informe.
            if (celdas.Count == 0) continue;
            log.Add(new Dictionary<string, object?>
            {
                ["uid"] = uid,
                ["zona"] = zona,
                ["celdas"] = celdas,
            });
        }
        return log;
    }

    /// InstanceIds de las cartas que tienen una invisibilidad activa en [tablero]
    /// (se usa para ocultarlas del informe de movimientos). Se toma el tablero de
    /// ANTES de combate para que también cubra cartas invisibles que luego mueran
    /// o se revelen al combatir (mejor ocultar de más que delatar su movimiento).
    private static HashSet<string> InstanceIdsInvisibles(
        Dictionary<string, List<Dictionary<string, object?>>> tablero)
    {
        var set = new HashSet<string>();
        foreach (var cartas in tablero.Values)
            foreach (var c in cartas)
            {
                var iid = M.Str(M.Get(c, "instanceId"));
                if (iid == "") continue;
                foreach (var ef in CartaHelper.Efectos(c))
                    if (M.Str(M.Get(ef, "tipo")) == "invisibilidad" &&
                        M.Int(M.Get(ef, "turnosRestantes")) > 0)
                    {
                        set.Add(iid);
                        break;
                    }
            }
        return set;
    }
    /// Ranking global, BAJO DEMANDA. Orden: experiencia↓, victorias↓, derrotas↑,
    /// alias↑. Vecinos/top10 con cursores sobre el doc del jugador (respetan todo
    /// el orden). Posición exacta = 1 + Σ de 4 Count() disjuntos (una rama por
    /// nivel de desempate). Requiere que Jugadores/{uid} tenga victorias/derrotas
    /// (espejo) y alias. Usado por GET /warzero/ranking.
    public async Task<Dictionary<string, object?>> RankingAsync(string uid, string ordenarPor)
    {
        var db = _fs.Db;
        var jugadores = db.Collection("Jugadores");

        var porVictorias = ordenarPor == "victorias";
        var miSnap = await jugadores.Document(uid).GetSnapshotAsync();
        long miXp = 0, miVic = 0, miDer = 0;
        string miAlias = "";
        if (miSnap.Exists)
        {
            var d = M.Map(M.FromFs(miSnap.ToDictionary()));
            miXp = M.Long(M.Get(d, "experiencia"));
            miVic = M.Long(M.Get(d, "victorias"));
            miDer = M.Long(M.Get(d, "derrotas"));
            miAlias = M.Str(M.Get(d, "alias"));
        }

        // Orden compuesto reutilizable.
        Query Ordenado(Query q) => porVictorias
             ? q.OrderByDescending("victorias")
                .OrderByDescending("experiencia")
                .OrderBy("derrotas")
                .OrderBy("alias")
             : q.OrderByDescending("experiencia")
                .OrderByDescending("victorias")
                .OrderBy("derrotas")
                .OrderBy("alias");

        async Task<long> Contar(Query q)
        {
            try { return (await q.Count().GetSnapshotAsync()).Count ?? 0; }
            catch (Exception ex)
            {
                Console.Error.WriteLine("[WZ][ranking] count falló: " + ex);
                return 0;
            }
        }

        // Posición exacta = nº de jugadores estrictamente por encima + 1.
        // Un jugador X está por encima de mí si:
        //   exp>mi.exp  ·  ó (exp==·vic>mi.vic)  ·  ó (exp==·vic==·der<mi.der)
        //                ·  ó (exp==·vic==·der==·alias<mi.alias)
        long porEncima = 0;
        if (miSnap.Exists)
        {
            if (porVictorias)
            {
                porEncima += await Contar(jugadores.WhereGreaterThan("victorias", miVic));
                porEncima += await Contar(jugadores
                    .WhereEqualTo("victorias", miVic)
                    .WhereGreaterThan("experiencia", miXp));
                porEncima += await Contar(jugadores
                    .WhereEqualTo("victorias", miVic).WhereEqualTo("experiencia", miXp)
                    .WhereLessThan("derrotas", miDer));
                porEncima += await Contar(jugadores
                    .WhereEqualTo("victorias", miVic).WhereEqualTo("experiencia", miXp)
                    .WhereEqualTo("derrotas", miDer).WhereLessThan("alias", miAlias));
            }
            else
            {
                porEncima += await Contar(jugadores.WhereGreaterThan("experiencia", miXp));
                porEncima += await Contar(jugadores
                    .WhereEqualTo("experiencia", miXp)
                    .WhereGreaterThan("victorias", miVic));
                porEncima += await Contar(jugadores
                    .WhereEqualTo("experiencia", miXp).WhereEqualTo("victorias", miVic)
                    .WhereLessThan("derrotas", miDer));
                porEncima += await Contar(jugadores
                    .WhereEqualTo("experiencia", miXp).WhereEqualTo("victorias", miVic)
                    .WhereEqualTo("derrotas", miDer).WhereLessThan("alias", miAlias));
            }
        }
        var miPosicion = porEncima + 1;

        var topTask = Ordenado(jugadores).Limit(10).GetSnapshotAsync();
        Task<QuerySnapshot>? arribaTask = null, abajoTask = null;
        if (miSnap.Exists)
        {
            var cursor = porVictorias
                ? new object[] { miVic, miXp, miDer, miAlias }
                : new object[] { miXp, miVic, miDer, miAlias };
            arribaTask = Ordenado(jugadores).EndBefore(cursor).LimitToLast(5).GetSnapshotAsync();
            abajoTask = Ordenado(jugadores).StartAfter(cursor).Limit(5).GetSnapshotAsync();
        }
        var tareas = new List<Task> { topTask };
        if (arribaTask != null) tareas.Add(arribaTask);
        if (abajoTask != null) tareas.Add(abajoTask);
        await Task.WhenAll(tareas);

        Dictionary<string, object?> Fila(DocumentSnapshot doc, long pos)
        {
            var d = M.Map(M.FromFs(doc.ToDictionary()));
            return new()
            {
                ["uid"] = doc.Id,
                ["alias"] = M.Str(M.Get(d, "alias")),
                ["imagenPerfil"] = M.Str(M.Get(d, "imagenPerfil")),
                ["experiencia"] = M.Long(M.Get(d, "experiencia")),
                ["nivel"] = Math.Max(1, M.Int(M.Get(d, "nivel"))),
                ["victorias"] = M.Long(M.Get(d, "victorias")),
                ["derrotas"] = M.Long(M.Get(d, "derrotas")),
                ["posicion"] = pos,
                ["esYo"] = doc.Id == uid,
            };
        }

        // "arriba" viene best-first (el inmediatamente superior es el último) →
        // posiciones miPosicion-N … miPosicion-1.
        var arriba = new List<Dictionary<string, object?>>();
        if (arribaTask != null)
        {
            var docs = arribaTask.Result.Documents.ToList();
            for (int i = 0; i < docs.Count; i++)
                arriba.Add(Fila(docs[i], miPosicion - (docs.Count - i)));
        }

        var abajo = new List<Dictionary<string, object?>>();
        if (abajoTask != null)
        {
            var docs = abajoTask.Result.Documents.ToList();
            for (int i = 0; i < docs.Count; i++)
                abajo.Add(Fila(docs[i], miPosicion + 1 + i));
        }

        Dictionary<string, object?>? miEntrada =
            miSnap.Exists ? Fila(miSnap, miPosicion) : null;

        var alrededor = new List<Dictionary<string, object?>>();
        alrededor.AddRange(arriba);
        if (miEntrada != null) alrededor.Add(miEntrada);
        alrededor.AddRange(abajo);

        var topDiez = new List<Dictionary<string, object?>>();
        var topDocs = topTask.Result.Documents.ToList();
        for (int i = 0; i < topDocs.Count; i++)
            topDiez.Add(Fila(topDocs[i], i + 1));

        return new Dictionary<string, object?>
        {
            ["miPosicion"] = miPosicion,
            ["miEntrada"] = miEntrada,
            ["alrededor"] = alrededor.Cast<object?>().ToList(),
            ["topDiez"] = topDiez.Cast<object?>().ToList(),
        };
    }
    /// Rellena victorias/derrotas (espejo) en los docs de Jugadores que no los
    /// tengan, leyéndolos de su subcolección Estadisticas/Resultados. Ejecutar
    /// UNA vez tras desplegar el ranking. Idempotente (salta los ya migrados).
    public async Task<Dictionary<string, object?>> BackfillRankingFieldsAsync()
    {
        var db = _fs.Db;
        var snap = await db.Collection("Jugadores").GetSnapshotAsync();
        int actualizados = 0;
        foreach (var doc in snap.Documents)
        {
            var d = M.Map(M.FromFs(doc.ToDictionary()));
            if (d.ContainsKey("victorias") && d.ContainsKey("derrotas")) continue;

            long vic = 0, der = 0;
            try
            {
                var res = await doc.Reference.Collection("Estadisticas")
                    .Document("Resultados").GetSnapshotAsync();
                if (res.Exists)
                {
                    var rd = M.Map(M.FromFs(res.ToDictionary()));
                    vic = M.Long(M.Get(rd, "Victorias"));
                    der = M.Long(M.Get(rd, "Derrotas"));
                }
            }
            catch { /* si falla, quedan a 0 */ }

            await doc.Reference.SetAsync(new Dictionary<string, object>
            {
                ["victorias"] = d.ContainsKey("victorias") ? M.Long(M.Get(d, "victorias")) : vic,
                ["derrotas"] = d.ContainsKey("derrotas") ? M.Long(M.Get(d, "derrotas")) : der,
            }, SetOptions.MergeAll);
            actualizados++;
        }
        return new Dictionary<string, object?> { ["ok"] = true, ["actualizados"] = actualizados };
    }
}