using Tablero = System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<System.Collections.Generic.Dictionary<string, object?>>>;
using EfectosCelda = System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<System.Collections.Generic.Dictionary<string, object?>>>;
using System.Linq;

// ─────────────────────────────────────────────────────────────────────────────
// SimuladorTurno.cs  —  TAREA 1 del lookahead
//
// Función PURA y determinista: dado el tablero actual y un conjunto de PLANES
// (las cartas reemitidas + acciones de cada jugador que se modela), devuelve el
// tablero RESUELTO de ese turno, la energía de combate por jugador y qué
// cuarteles cayeron. Es el motor de la búsqueda del bot (LookaheadDosPlies) y
// de la arena offline (ArenaOffline).
//
// Es un PUERTO FIEL de la secuencia que afecta al tablero en
// WarZeroService.ResolverTurnoCoreEnTx, reutilizando los MISMOS helpers puros.
// Orden (igual que el servidor):
//   fusionar → parálisis/confusión (bloqueo) → supervivencia (re-sellar) →
//   estáticas (no se mueven) → ValidarClones → muros (revertir) →
//   MoverConfundidas → reglas de acciones (escudo en cuartel propio) →
//   AplicarAcciones → trampas → descarga → ExtraerClones → Combate →
//   ReinsertarClones → TickEfectos → limpiar eliminados.
//
// v13 (revisión de habilidades, 14/10/2026): antes faltaban la confusión, la
// supervivencia, las estáticas, la validación de clones, los muros, el
// movimiento de las confundidas y la retirada de los clones antes del combate.
// Consecuencias en la arena y en el lookahead: un CLON combatía como una carta
// real (con la fuerza de la original), los MUROS no bloqueaban a nadie y una
// carta CONFUNDIDA obedecía a su dueño. Ahora coincide con la resolución real.
//
// Lo que NO se replica, por no afectar al TABLERO o por depender de Firestore:
// el farmeo de energía, el reparto de carta, el cobro de energía (el llamante
// solo debe pasar acciones que pueda pagar), la recarga de habilidades y las
// reglas de movimiento del modo historia.
//
// Jugadores NO incluidos en `planes` conservan sus cartas actuales (se arrastran
// del tablero de partida), igual que el modelo "reemitir y fusionar" del juego:
// así el bot puede modelar solo su jugada + la del rival y dejar quietos a los
// demás.
// ─────────────────────────────────────────────────────────────────────────────
public static class SimuladorTurno
{
    /// La jugada de un jugador para un turno: sus celdas (coord -> cartas) y sus
    /// acciones (disparos, escudos, parálisis, teletransportes, descargas…).
    public sealed record Plan(
        string Uid,
        Tablero Celdas,
        System.Collections.Generic.List<System.Collections.Generic.Dictionary<string, object?>> Acciones);

    /// Resultado de simular un turno.
    public sealed record Resultado(
        Tablero Tablero,                                        // tablero resuelto
        System.Collections.Generic.Dictionary<string, int> EnergiesCombate, // energía de combate por jugador
        System.Collections.Generic.HashSet<string> CuartelesDestruidos,     // coords de cuarteles caídos
        System.Collections.Generic.HashSet<string> JugadoresEliminados,     // uids eliminados tras el turno
        // ── v12 (arena offline): detalle completo de la resolución. Opcionales
        //    para no tocar a los llamantes del lookahead. ──
        ResolucionCombates? Combates = null,                    // combates, PC, conquistas (con conquistador)
        EfectosCelda? EfectosFinales = null,                    // efectos de celda tras el tick (para el turno siguiente)
        System.Collections.Generic.List<System.Collections.Generic.Dictionary<string, object?>>? LogAcciones = null, // accionesLog del turno
        System.Collections.Generic.Dictionary<string, int>? DescargasTurno = null); // coord -> turno de descarga (previas + nuevas)

    /// Semilla por defecto: el simulador debe ser reproducible.
    public const int SemillaPorDefecto = 12345;

    /// Simula un turno de forma determinista.
    ///   tableroActual      : tablero de partida (no se muta).
    ///   obeliscos          : uid -> coord del cuartel.
    ///   turno              : nº de turno (para la recuperación de descargas).
    ///   planes             : jugada de cada jugador modelado.
    ///   efectosPrevios     : efectos de celda persistidos (veneno/escudo/parálisis/muro/trampa).
    ///   eliminadosPrevios  : uids ya eliminados antes del turno.
    ///   aliadoDe           : uid -> uid aliado (o null si no hay alianzas activas).
    ///   terreno            : coord -> tipo (teletransporte, fractura, clon, muros,
    ///                        confusión y huida por supervivencia lo usan).
    ///   descargasPrev      : coord -> turnoDescarga previo (recuperación de defensa).
    ///   celdasValidas      : TODAS las celdas del mapa. Acota la huida por
    ///                        SUPERVIVENCIA, el rodeo de los muros y el
    ///                        movimiento de las confundidas. Opcional para no
    ///                        romper a los llamantes antiguos, pero pásala
    ///                        SIEMPRE que la tengas.
    ///   rng                : azar de la huida y de las confundidas. Si es null
    ///                        se usa SemillaPorDefecto (reproducible).
    public static Resultado Simular(
        Tablero tableroActual,
        System.Collections.Generic.Dictionary<string, string> obeliscos,
        int turno,
        System.Collections.Generic.IReadOnlyList<Plan> planes,
        EfectosCelda efectosPrevios,
        System.Collections.Generic.HashSet<string> eliminadosPrevios,
        System.Collections.Generic.Dictionary<string, string>? aliadoDe = null,
        System.Collections.Generic.Dictionary<string, string>? terreno = null,
        System.Collections.Generic.Dictionary<string, int>? descargasPrev = null,
        // celdasValidas / rng: ver la cabecera del método (un /// aquí es CS1587).
        System.Collections.Generic.HashSet<string>? celdasValidas = null,
        System.Random? rng = null)
    {
        var azar = rng ?? new System.Random(SemillaPorDefecto);

        // ── 1. Fusionar: jugadas de los modelados + arrastre de los NO modelados ──
        var merged = new Tablero();
        var uidsConPlan = new System.Collections.Generic.HashSet<string>();
        var acciones = new System.Collections.Generic.List<System.Collections.Generic.Dictionary<string, object?>>();

        foreach (var plan in planes)
        {
            uidsConPlan.Add(plan.Uid);
            foreach (var ce in plan.Celdas)
            {
                if (!merged.TryGetValue(ce.Key, out var lst)) { lst = new(); merged[ce.Key] = lst; }
                foreach (var c in ce.Value) lst.Add(CopiarCarta(c)); // copia profunda: no mutar la del llamante
            }
            // Como el servidor: el uid autoritativo de una acción es el del plan.
            foreach (var a in plan.Acciones)
            {
                var copia = new System.Collections.Generic.Dictionary<string, object?>(a)
                {
                    ["uid"] = plan.Uid,
                    ["turno"] = turno,
                };
                acciones.Add(copia);
            }
        }
        // Jugadores sin plan: mantienen sus cartas actuales.
        foreach (var kv in tableroActual)
            foreach (var c in kv.Value)
            {
                if (uidsConPlan.Contains(CartaHelper.OwnerUid(c))) continue;
                if (!merged.TryGetValue(kv.Key, out var lst)) { lst = new(); merged[kv.Key] = lst; }
                lst.Add(CopiarCarta(c));
            }

        // Tablero previo (copia): base para revertir parálisis/escudos y para las trampas.
        var tableroPrevio = new Tablero();
        foreach (var kv in tableroActual)
        {
            var lst = new System.Collections.Generic.List<System.Collections.Generic.Dictionary<string, object?>>();
            foreach (var c in kv.Value) lst.Add(CopiarCarta(c));
            tableroPrevio[kv.Key] = lst;
        }
        var previoPorInst = new System.Collections.Generic.Dictionary<string, (string coord, System.Collections.Generic.Dictionary<string, object?> card)>();
        foreach (var (coord, lst) in tableroPrevio)
            foreach (var c in lst)
            {
                var iid = M.Str(M.Get(c, "instanceId"));
                if (iid != "") previoPorInst[iid] = (coord, c);
            }

        // ── 2. Parálisis y CONFUSIÓN: su dueño no puede moverlas (se revierten a
        //      su celda del turno anterior, igual que en el servidor) ──
        var bloqueadas = new System.Collections.Generic.HashSet<string>();
        foreach (var (iid, pc) in previoPorInst)
            if (CartaHelper.EstaParalizada(pc.card) || CartaHelper.EstaConfundida(pc.card)) bloqueadas.Add(iid);
        if (bloqueadas.Count > 0)
        {
            foreach (var lst in merged.Values)
                lst.RemoveAll(c => bloqueadas.Contains(M.Str(M.Get(c, "instanceId"))));
            foreach (var iid in bloqueadas)
            {
                var (coord, card) = previoPorInst[iid];
                if (!merged.TryGetValue(coord, out var lst)) { lst = new(); merged[coord] = lst; }
                if (!lst.Any(c => M.Str(M.Get(c, "instanceId")) == iid)) lst.Add(CopiarCarta(card));
            }
            QuitarVacias(merged);
        }

        // ── 3. SUPERVIVENCIA: la penalización de fuerza es permanente (se
        //      re-sella desde el tablero anterior quedándose con el mayor) ──
        foreach (var lst in merged.Values)
            foreach (var carta in lst)
            {
                var iid = CartaHelper.InstanceId(carta);
                if (iid == "" || !previoPorInst.TryGetValue(iid, out var prev)) continue;
                var antes = CartaHelper.SupervivenciaPenalizacion(prev.card);
                if (antes <= CartaHelper.SupervivenciaPenalizacion(carta)) continue;
                carta[CartaHelper.CampoSupervivencia] = (long)antes;
                carta.Remove("supervivenciaPenalizacion");
            }

        // ── 4. ESTÁTICAS: una carta estática no se mueve nunca ──
        var estaticasPrevias = previoPorInst
            .Where(kv => CartaHelper.EsEstatica(kv.Value.card))
            .ToDictionary(kv => kv.Key, kv => kv.Value.coord);
        if (estaticasPrevias.Count > 0)
        {
            var aRecolocar = new System.Collections.Generic.List<(string casa, System.Collections.Generic.Dictionary<string, object?> card)>();
            foreach (var (coord, lst) in merged)
                for (int i = lst.Count - 1; i >= 0; i--)
                {
                    var iid = M.Str(M.Get(lst[i], "instanceId"));
                    if (iid == "" || !estaticasPrevias.TryGetValue(iid, out var casa) || casa == coord) continue;
                    aRecolocar.Add((casa, lst[i]));
                    lst.RemoveAt(i);
                }
            foreach (var (casa, card) in aRecolocar)
            {
                if (!merged.TryGetValue(casa, out var lst)) { lst = new(); merged[casa] = lst; }
                var iid = M.Str(M.Get(card, "instanceId"));
                if (!lst.Any(c => M.Str(M.Get(c, "instanceId")) == iid)) lst.Add(card);
            }
            QuitarVacias(merged);
        }

        // ── 5. CLONES: el servidor decide qué carta es un clon ──
        AccionesDistorsion.ValidarClones(merged, tableroPrevio);

        // ── 6. MUROS (revertir) y CONFUSIÓN (mover al azar) ──
        var logsDistorsion = new System.Collections.Generic.List<System.Collections.Generic.Dictionary<string, object?>>();
        AccionesDistorsion.RevertirMovimientosPorMuros(
            merged, tableroPrevio, efectosPrevios, terreno, celdasValidas, logsDistorsion, obeliscos);
        AccionesDistorsion.MoverConfundidas(
            merged, efectosPrevios, obeliscos, terreno, celdasValidas, azar, logsDistorsion);

        // ── 7. Reglas autoritativas de las acciones que afectan al tablero ──
        //      (el cobro y la recarga son cosa del llamante).
        var logsRechazadas = new System.Collections.Generic.List<System.Collections.Generic.Dictionary<string, object?>>();
        acciones = FiltrarAcciones(acciones, obeliscos, logsRechazadas);

        // ── 8. Acciones (escudo/potenciación → muro → tele → fractura → clon →
        //      disparo → veneno → parálisis → confusión → invisibilidad/supervivencia) ──
        var acc = Habilidades.AplicarAcciones(
            merged, acciones, efectosPrevios, obeliscos, tableroPrevio, terreno, celdasValidas);
        if (logsDistorsion.Count > 0) acc.Log.InsertRange(0, logsDistorsion);
        if (logsRechazadas.Count > 0) acc.Log.InsertRange(0, logsRechazadas);

        // ── 9. Trampas (acciones estáticas) ──
        Trampas.Procesar(acc.Tablero, acc.EfectosCelda, acc.Log,
            acciones, tableroPrevio, obeliscos,
            aliadoDe ?? new System.Collections.Generic.Dictionary<string, string>());

        // ── 10. Descarga de cuartel (ANTES del combate): mata todo en la celda y
        //       baja su defensa; recupera +25%/turno ──
        var descargaTurno = new System.Collections.Generic.Dictionary<string, int>();
        if (descargasPrev != null)
            foreach (var kv in descargasPrev)
                if (kv.Key != "" && kv.Value > 0) descargaTurno[kv.Key] = kv.Value;

        var descargadosEsteTurno = new System.Collections.Generic.HashSet<string>();
        foreach (var a in acciones)
        {
            if (!AccionesTacticas.EsDescarga(a)) continue;
            var duid = M.Str(M.Get(a, "uid"));
            var dcoord = M.Str(M.Get(a, "origen"));
            if (dcoord == "") dcoord = M.Str(M.List(M.Get(a, "objetivos")).FirstOrDefault());
            if (dcoord == "" || !obeliscos.TryGetValue(duid, out var micg) || micg != dcoord) continue;
            if (!descargadosEsteTurno.Add(duid)) continue;   // una por jugador y resolución
            var destruidas = new System.Collections.Generic.List<object?>();
            if (acc.Tablero.TryGetValue(dcoord, out var muertas))
                destruidas = muertas.Select(c => (object?)new System.Collections.Generic.Dictionary<string, object?>
                {
                    ["Nombre"] = CartaHelper.Nombre(c),
                    ["ownerUid"] = CartaHelper.OwnerUid(c),
                    ["ownerZone"] = CartaHelper.OwnerZone(c),
                }).ToList();
            acc.Log.Add(new System.Collections.Generic.Dictionary<string, object?>
            {
                ["tipo"] = "descarga",
                ["uid"] = duid,
                ["zona"] = M.Str(M.Get(a, "zona")),
                ["coord"] = dcoord,
                ["defensaCuartel"] = 0L,
                ["cartasDestruidas"] = destruidas,
            });
            acc.Tablero.Remove(dcoord);
            descargaTurno[dcoord] = turno;
        }
        var defensaObeliscoPorCoord = new System.Collections.Generic.Dictionary<string, int>();
        foreach (var kv in descargaTurno)
        {
            var diff = turno - kv.Value;
            if (diff < 0) diff = 0;
            if (diff >= 4) continue;
            defensaObeliscoPorCoord[kv.Key] = Combate.DefensaObelisco * diff / 4;
        }

        // ── 11. CLONES fuera del combate (los que coinciden con un enemigo se
        //       disipan) → Combate determinista → clones de vuelta ──
        var aliados = aliadoDe != null && aliadoDe.Count > 0 ? aliadoDe : null;
        var clonesSupervivientes = AccionesDistorsion.ExtraerClones(acc.Tablero, obeliscos, aliados);
        var celdasMuroSim = AccionesDistorsion.CeldasConMuro(acc.EfectosCelda);
        var protegidasSim = Habilidades.CeldasProtegidas(acc.EfectosCelda);
        var reso = Combate.Resolver(
            acc.Tablero, obeliscos,
            aliados,
            defensaObeliscoPorCoord.Count > 0 ? defensaObeliscoPorCoord : null,
            terreno,
            celdasValidas,
            celdasMuroSim.Count > 0 ? celdasMuroSim : null,
            protegidasSim.Count > 0 ? protegidasSim : null,
            azar);
        AccionesDistorsion.ReinsertarClones(reso.Tablero, clonesSupervivientes, aliados);
        if (reso.Huidas.Count > 0) acc.Log.AddRange(reso.Huidas);

        // ── 12. Tick de efectos (incluye la caducidad de los clones) ──
        var tick = Habilidades.TickEfectos(reso.Tablero, acc.EfectosCelda);
        var tableroFinal = tick.Tablero;

        // ── 13. Limpiar cartas de jugadores eliminados (previos + conquistados) ──
        var eliminadosTotal = new System.Collections.Generic.HashSet<string>(eliminadosPrevios);
        foreach (var oc in reso.ObeliscosConquistados) eliminadosTotal.Add(oc.PerdedorUid);
        if (eliminadosTotal.Count > 0)
        {
            var limpio = new Tablero();
            foreach (var kv in tableroFinal)
            {
                var quedan = kv.Value
                    .Where(c => !eliminadosTotal.Contains(CartaHelper.OwnerUid(c)))
                    .ToList();
                if (quedan.Count > 0) limpio[kv.Key] = quedan;
            }
            tableroFinal = limpio;
        }

        return new Resultado(
            tableroFinal,
            new System.Collections.Generic.Dictionary<string, int>(reso.EnergiesPorJugador),
            reso.ObeliscosConquistados.Select(c => c.Coord).ToHashSet(),
            eliminadosTotal,
            reso,
            tick.EfectosCelda,
            acc.Log,
            descargaTurno);
    }

    /// Reglas del servidor sobre las acciones que cambian el tablero y no
    /// dependen del catálogo ni de la energía (fase "coste-acciones" de
    /// WarZeroService): nadie puede escudar su PROPIO cuartel. El resto (coste,
    /// recarga, carta lanzadora válida) lo decide el llamante.
    public static System.Collections.Generic.List<System.Collections.Generic.Dictionary<string, object?>> FiltrarAcciones(
        System.Collections.Generic.List<System.Collections.Generic.Dictionary<string, object?>> acciones,
        System.Collections.Generic.Dictionary<string, string> obeliscos,
        System.Collections.Generic.List<System.Collections.Generic.Dictionary<string, object?>> logRechazadas)
    {
        var res = new System.Collections.Generic.List<System.Collections.Generic.Dictionary<string, object?>>(acciones.Count);
        foreach (var a in acciones)
        {
            var uid = M.Str(M.Get(a, "uid"));
            var h = CatalogoHabilidades.Get(M.Int(M.Get(a, "habilidadId")));
            if (h != null && h.Efecto == EfectoTipo.Escudo
                && !(M.Get(a, "esTrampa") is bool et && et)
                && obeliscos.TryGetValue(uid, out var cuartel) && cuartel != ""
                && M.List(M.Get(a, "objetivos")).Select(M.Str).Contains(cuartel))
            {
                logRechazadas.Add(new System.Collections.Generic.Dictionary<string, object?>
                {
                    ["tipo"] = "fallida",
                    ["uid"] = uid,
                    ["habilidadId"] = h.Id,
                    ["origen"] = M.Str(M.Get(a, "origen")),
                    ["motivo"] = "No se puede escudar el propio cuartel",
                });
                continue;
            }
            res.Add(a);
        }
        return res;
    }

    private static void QuitarVacias(Tablero t)
    {
        foreach (var k in t.Keys.Where(k => t[k].Count == 0).ToList()) t.Remove(k);
    }

    // Copia PROFUNDA de una carta (top-level + lista Efectos + cada efecto), para
    // que las mutaciones del combate/tick NO toquen el tablero del llamante. M.Map
    // devuelve la MISMA referencia si ya es diccionario, así que no vale para copiar.
    private static System.Collections.Generic.Dictionary<string, object?> CopiarCarta(
        System.Collections.Generic.Dictionary<string, object?> c)
    {
        var copy = new System.Collections.Generic.Dictionary<string, object?>(c);
        if (c.TryGetValue("Efectos", out var ef) && ef is not null)
            copy["Efectos"] = M.List(ef)
                .Select(m => (object?)new System.Collections.Generic.Dictionary<string, object?>(M.Map(m)))
                .ToList();
        return copy;
    }
}
