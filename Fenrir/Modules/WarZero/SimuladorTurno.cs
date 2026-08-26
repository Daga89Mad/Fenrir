using Tablero = System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<System.Collections.Generic.Dictionary<string, object?>>>;
using EfectosCelda = System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<System.Collections.Generic.Dictionary<string, object?>>>;
using System.Linq;

// ─────────────────────────────────────────────────────────────────────────────
// SimuladorTurno.cs  —  TAREA 1 del lookahead
//
// Función PURA y determinista: dado el tablero actual y un conjunto de PLANES
// (las cartas reemitidas + acciones de cada jugador que se modela), devuelve el
// tablero RESUELTO de ese turno, la energía de combate por jugador y qué
// cuarteles cayeron. Es el motor sobre el que se construirá la búsqueda a varios
// plies (Tareas 2-4).
//
// Es un PUERTO FIEL de la secuencia que afecta al tablero en
// WarZeroService.ResolverTurnoCoreEnTx, reutilizando los MISMOS helpers puros
// (Habilidades.AplicarAcciones, Trampas.Procesar, Combate.Resolver,
// Habilidades.TickEfectos). Por eso el resultado coincide con la resolución real
// (se valida con el hook de sombra; ver más abajo). Lo que NO se replica, por no
// afectar al TABLERO, es el farmeo de energía (el evaluador ya puntúa el control
// del mapa sobre el tablero resultante), el reparto de carta y las escrituras a
// Firestore. La energía de FARMEO se puede añadir en una v2 si la búsqueda la
// necesita para varios turnos.
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
        System.Collections.Generic.HashSet<string> JugadoresEliminados);    // uids eliminados tras el turno

    /// Simula un turno de forma determinista.
    ///   tableroActual      : tablero de partida (no se muta).
    ///   obeliscos          : uid -> coord del cuartel.
    ///   turno              : nº de turno (para la recuperación de descargas).
    ///   planes             : jugada de cada jugador modelado.
    ///   efectosPrevios     : efectos de celda persistidos (veneno/escudo/parálisis).
    ///   eliminadosPrevios  : uids ya eliminados antes del turno.
    ///   aliadoDe           : uid -> uid aliado (o null si no hay alianzas activas).
    ///   terreno            : coord -> tipo, solo necesario si hay teletransportes.
    ///   descargasPrev      : coord -> turnoDescarga previo (recuperación de defensa).
    public static Resultado Simular(
        Tablero tableroActual,
        System.Collections.Generic.Dictionary<string, string> obeliscos,
        int turno,
        System.Collections.Generic.IReadOnlyList<Plan> planes,
        EfectosCelda efectosPrevios,
        System.Collections.Generic.HashSet<string> eliminadosPrevios,
        System.Collections.Generic.Dictionary<string, string>? aliadoDe = null,
        System.Collections.Generic.Dictionary<string, string>? terreno = null,
        System.Collections.Generic.Dictionary<string, int>? descargasPrev = null)
    {
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
            acciones.AddRange(plan.Acciones);
        }
        // Jugadores sin plan: mantienen sus cartas actuales.
        foreach (var kv in tableroActual)
            foreach (var c in kv.Value)
            {
                if (uidsConPlan.Contains(CartaHelper.OwnerUid(c))) continue;
                if (!merged.TryGetValue(kv.Key, out var lst)) { lst = new(); merged[kv.Key] = lst; }
                lst.Add(CopiarCarta(c));
            }

        // Tablero previo (copia): base para revertir parálisis y para las trampas.
        var tableroPrevio = new Tablero();
        foreach (var kv in tableroActual)
        {
            var lst = new System.Collections.Generic.List<System.Collections.Generic.Dictionary<string, object?>>();
            foreach (var c in kv.Value) lst.Add(CopiarCarta(c));
            tableroPrevio[kv.Key] = lst;
        }

        // ── 2. Parálisis: una carta paralizada no puede moverse (misma regla que
        //      el servidor: se revierte a su celda del turno anterior) ──
        var previoPorInst = new System.Collections.Generic.Dictionary<string, (string coord, System.Collections.Generic.Dictionary<string, object?> card)>();
        foreach (var (coord, lst) in tableroPrevio)
            foreach (var c in lst)
            {
                var iid = M.Str(M.Get(c, "instanceId"));
                if (iid != "") previoPorInst[iid] = (coord, c);
            }
        var paralizadas = new System.Collections.Generic.HashSet<string>();
        foreach (var (iid, pc) in previoPorInst)
            if (CartaHelper.EstaParalizada(pc.card)) paralizadas.Add(iid);
        if (paralizadas.Count > 0)
        {
            foreach (var lst in merged.Values)
                lst.RemoveAll(c => paralizadas.Contains(M.Str(M.Get(c, "instanceId"))));
            foreach (var iid in paralizadas)
            {
                var (coord, card) = previoPorInst[iid];
                if (!merged.TryGetValue(coord, out var lst)) { lst = new(); merged[coord] = lst; }
                if (!lst.Any(c => M.Str(M.Get(c, "instanceId")) == iid)) lst.Add(CopiarCarta(card));
            }
            foreach (var k in merged.Keys.Where(k => merged[k].Count == 0).ToList())
                merged.Remove(k);
        }

        // ── 3. Acciones (tele → disparo → veneno → escudo) ──
        var acc = Habilidades.AplicarAcciones(
            merged, acciones, efectosPrevios, obeliscos, tableroPrevio, terreno);

        // ── 4. Trampas (acciones estáticas) ──
        Trampas.Procesar(acc.Tablero, acc.EfectosCelda, acc.Log,
            acciones, tableroPrevio, obeliscos,
            aliadoDe ?? new System.Collections.Generic.Dictionary<string, string>());

        // ── 5. Descarga de cuartel (ANTES del combate): mata todo en la celda y
        //      baja su defensa; recupera +25%/turno ──
        var descargaTurno = new System.Collections.Generic.Dictionary<string, int>();
        if (descargasPrev != null)
            foreach (var kv in descargasPrev)
                if (kv.Key != "" && kv.Value > 0) descargaTurno[kv.Key] = kv.Value;

        foreach (var a in acciones)
        {
            if (!(M.Get(a, "esDescarga") is bool ed && ed)) continue;
            var duid = M.Str(M.Get(a, "uid"));
            var dcoord = M.Str(M.Get(a, "origen"));
            if (dcoord == "") dcoord = M.Str(M.List(M.Get(a, "objetivos")).FirstOrDefault());
            if (dcoord == "" || !obeliscos.TryGetValue(duid, out var micg) || micg != dcoord) continue;
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

        // ── 6. Combate determinista ──
        var reso = Combate.Resolver(
            acc.Tablero, obeliscos,
            aliadoDe != null && aliadoDe.Count > 0 ? aliadoDe : null,
            defensaObeliscoPorCoord.Count > 0 ? defensaObeliscoPorCoord : null);

        // ── 7. Tick de efectos ──
        var tick = Habilidades.TickEfectos(reso.Tablero, acc.EfectosCelda);
        var tableroFinal = tick.Tablero;

        // ── 8. Limpiar cartas de jugadores eliminados (previos + conquistados) ──
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
            eliminadosTotal);
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