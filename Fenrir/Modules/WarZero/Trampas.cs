using Tablero = System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<System.Collections.Generic.Dictionary<string, object?>>>;
using EfectosCelda = System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<System.Collections.Generic.Dictionary<string, object?>>>;

// ─────────────────────────────────────────────────────────────────────────────
// Trampas.cs  (ARCHIVO NUEVO — añádelo al proyecto Fenrir)
//
// Acción estática = TRAMPA invisible (CondicionCarta.accionEstatica, valor 6).
//
// Reglas:
//   • Se juega desde la mano como una acción, pero SOLO puede colocarse sobre
//     una celda donde el jugador tiene una carta propia que lleva >= 2 turnos
//     asentada (campo `turnosEnCelda`, mantenido por ActualizarTurnosEnCelda).
//   • Al colocarse queda OCULTA (oculta:true): nadie la ve en el tablero.
//   • Cuando una carta ENEMIGA cae en su celda, dispara la habilidad configurada
//     (`habilidadId`) SOLO sobre los enemigos de la celda y se REVELA para todos
//     (oculta:false) con su icono y los turnos que le quedan.
//   • Tiene vida finita: `turnosRestantes` baja 1 por resolución (TickEfectos) y
//     desaparece al llegar a 0.
//
// La trampa vive dentro de `efectosCelda` como un efecto más:
//   { tipo:"trampa", oculta:bool, turnosRestantes:int, habilidadId:int,
//     origenUid:string, icono:string, cartaNombre:string, cartaId:string }
//
// El cliente coloca la trampa enviando en `acciones` un objeto con `esTrampa:true`:
//   { esTrampa:true, uid, zona, origen, habilidadId, objetivos:["<coord>"],
//     duracion, cartaId, cartaNombre, icono }
//
// IMPORTANTE (visibilidad): el estado de la partida es un único documento
// compartido, así que las trampas ocultas viajan en él. El OCULTAMIENTO real es
// del lado del cliente (Fase 4): solo se pintan las trampas con oculta:false, y
// las oculta:true únicamente a su dueño. No es un secreto criptográfico, pero es
// coherente con cómo esta arquitectura sirve el estado (documento completo).
// ─────────────────────────────────────────────────────────────────────────────

public static class Trampas
{
    public const string Tipo = "trampa";
    public const int DuracionDefecto = 5;

    /// True si la acción entrante es una colocación de trampa.
    private static bool EsAccionTrampa(Dictionary<string, object?> a)
    {
        var v = M.Get(a, "esTrampa");
        return (v is bool b && b) || (v is string s && s == "true");
    }

    /// Procesa las trampas de una resolución: primero DISPARA las trampas ocultas
    /// ya existentes si hay un enemigo en su celda, luego COLOCA las trampas
    /// nuevas de este turno. Muta `t`, `e` y `log` en el sitio.
    ///
    /// Debe llamarse DESPUÉS de Habilidades.AplicarAcciones (cuando las cartas ya
    /// están en su posición de este turno) y ANTES de Combate.Resolver.
    public static void Procesar(
        Tablero t,
        EfectosCelda e,
        List<Dictionary<string, object?>> log,
        List<Dictionary<string, object?>> acciones,
        Tablero? tableroPrevio,
        Dictionary<string, string> obeliscos,
        Dictionary<string, string>? aliadoDe = null)
    {
        // 1) Disparar trampas ocultas existentes.
        DispararTrampasExistentes(t, e, log, aliadoDe);

        // 2) Colocar trampas nuevas (validando el asentamiento ≥ 2 turnos).
        foreach (var a in acciones)
        {
            if (!EsAccionTrampa(a)) continue;
            ColocarTrampa(a, e, log, tableroPrevio, obeliscos);
        }
    }

    /// Mantiene el contador `turnosEnCelda` de cada carta del tablero resuelto:
    ///   • Si la carta sigue en la MISMA celda que el turno anterior → +1.
    ///   • Si se movió o es nueva en esa celda → 1.
    /// Debe llamarse en la resolución con el tablero YA final (post-combate,
    /// post-tick y post-limpieza de eliminados) y el tablero del turno anterior.
    public static void ActualizarTurnosEnCelda(Tablero tableroFinal, Tablero? tableroPrevio)
    {
        // Índice del turno anterior: (coord|owner|id) → cola de turnosEnCelda
        // (cola para soportar copias repetidas de la misma carta en la celda).
        var prev = new Dictionary<string, Queue<int>>();
        if (tableroPrevio != null)
        {
            foreach (var kv in tableroPrevio)
                foreach (var c in kv.Value)
                {
                    var key = Clave(kv.Key, c);
                    if (!prev.TryGetValue(key, out var q)) { q = new(); prev[key] = q; }
                    q.Enqueue(M.Int(M.Get(c, "turnosEnCelda")));
                }
        }

        foreach (var kv in tableroFinal)
            foreach (var c in kv.Value)
            {
                var key = Clave(kv.Key, c);
                int nuevo = 1;
                if (prev.TryGetValue(key, out var q) && q.Count > 0)
                    nuevo = q.Dequeue() + 1; // seguía en la MISMA celda
                c["turnosEnCelda"] = nuevo;
            }
    }

    // ── Internos ─────────────────────────────────────────────────────────────

    private static string Clave(string coord, Dictionary<string, object?> c)
        => coord + "|" + CartaHelper.OwnerUid(c) + "|" + M.Str(M.Get(c, "id", "Id"));

    private static bool EsAliado(Dictionary<string, string>? aliadoDe, string a, string b)
        => aliadoDe != null && aliadoDe.TryGetValue(a, out var x) && x == b;

    private static void DispararTrampasExistentes(
        Tablero t, EfectosCelda e, List<Dictionary<string, object?>> log,
        Dictionary<string, string>? aliadoDe)
    {
        foreach (var kv in e)
        {
            var coord = kv.Key;
            if (!t.TryGetValue(coord, out var cartas) || cartas.Count == 0) continue;

            foreach (var trap in kv.Value)
            {
                if (M.Str(M.Get(trap, "tipo")) != Tipo) continue;
                if (M.Get(trap, "oculta") is not true) continue;              // ya revelada
                if (M.Int(M.Get(trap, "turnosRestantes")) <= 0) continue;     // caducada
                var origen = M.Str(M.Get(trap, "origenUid"));

                // ¿Hay algún enemigo (no dueño y no aliado del dueño) en la celda?
                var hayEnemigo = cartas.Any(c =>
                {
                    var o = CartaHelper.OwnerUid(c);
                    return o != "" && o != origen && !EsAliado(aliadoDe, origen, o);
                });
                if (!hayEnemigo) continue;

                DispararEfecto(trap, coord, t, aliadoDe);
                trap["oculta"] = false; // revelada para todos

                log.Add(new Dictionary<string, object?>
                {
                    ["tipo"] = "trampaActivada",
                    ["objetivo"] = coord,
                    ["uid"] = origen,
                    ["habilidadId"] = M.Int(M.Get(trap, "habilidadId")),
                    ["turnosRestantes"] = M.Int(M.Get(trap, "turnosRestantes")),
                });
            }
        }
    }

    /// Aplica el efecto configurado de la trampa SOLO sobre los enemigos de la
    /// celda. Disparo destruye enemigos; veneno/parálisis/potenciación se aplican
    /// como estado de carta; teletransporte/escudo no tienen sentido como trampa
    /// (solo revela, sin efecto).
    private static void DispararEfecto(
        Dictionary<string, object?> trap, string coord, Tablero t,
        Dictionary<string, string>? aliadoDe)
    {
        var origen = M.Str(M.Get(trap, "origenUid"));
        var h = CatalogoHabilidades.Get(M.Int(M.Get(trap, "habilidadId")));
        if (h == null) return;
        if (!t.TryGetValue(coord, out var cartas) || cartas.Count == 0) return;

        bool EsEnemigo(Dictionary<string, object?> c)
        {
            var o = CartaHelper.OwnerUid(c);
            return o != "" && o != origen && !EsAliado(aliadoDe, origen, o);
        }

        switch (h.Efecto)
        {
            case EfectoTipo.Disparo:
                // Destruye únicamente las cartas enemigas de la celda.
                var quedan = cartas.Where(c => !EsEnemigo(c)).ToList();
                if (quedan.Count > 0) t[coord] = quedan; else t.Remove(coord);
                break;

            case EfectoTipo.Veneno:
            case EfectoTipo.Paralisis:
            case EfectoTipo.PotFuerza:
            case EfectoTipo.PotDefensa:
            case EfectoTipo.PotMovimiento:
                var tipoEstado = h.Efecto switch
                {
                    EfectoTipo.Veneno => "veneno",
                    EfectoTipo.Paralisis => "paralisis",
                    EfectoTipo.PotFuerza => "potFuerza",
                    EfectoTipo.PotDefensa => "potDefensa",
                    _ => "potMovimiento",
                };
                var estado = new Dictionary<string, object?>
                {
                    ["tipo"] = tipoEstado,
                    ["turnosRestantes"] = h.DuracionTurnos,
                    ["magnitud"] = h.DefensaReducida,
                    ["origenUid"] = origen,
                };
                foreach (var c in cartas)
                    if (EsEnemigo(c)) AgregarEstadoCarta(c, estado);
                break;

            default:
                // Teletransporte / Escudo: no aplican como trampa; solo se revela.
                break;
        }
    }

    private static void ColocarTrampa(
        Dictionary<string, object?> a, EfectosCelda e,
        List<Dictionary<string, object?>> log, Tablero? tableroPrevio,
        Dictionary<string, string> obeliscos)
    {
        var uid = M.Str(M.Get(a, "uid"));
        var coord = M.List(M.Get(a, "objetivos")).Select(M.Str).FirstOrDefault(s => s != "") ?? "";
        if (uid == "" || coord == "")
        {
            log.Add(Fallo(a, "Datos de trampa incompletos"));
            return;
        }

        // No se colocan trampas sobre un cuartel.
        if (obeliscos.Values.Contains(coord))
        {
            log.Add(Fallo(a, "No se puede colocar una trampa en un cuartel"));
            return;
        }

        // Asentamiento: en el tablero del turno anterior debe existir una carta
        // del jugador en `coord` con turnosEnCelda >= 2.
        if (!CumpleAsentamiento(tableroPrevio, coord, uid))
        {
            log.Add(Fallo(a, "Necesitas una carta propia asentada 2 turnos en esa casilla"));
            return;
        }

        var duracion = M.Int(M.Get(a, "duracion"));
        if (duracion <= 0) duracion = DuracionDefecto;
        var habilidadId = M.Int(M.Get(a, "habilidadId"));

        var trap = new Dictionary<string, object?>
        {
            ["tipo"] = Tipo,
            ["oculta"] = true,
            ["turnosRestantes"] = duracion,
            ["habilidadId"] = habilidadId,
            ["origenUid"] = uid,
            ["icono"] = M.Str(M.Get(a, "icono")),
            ["cartaNombre"] = M.Str(M.Get(a, "cartaNombre")),
            ["cartaId"] = M.Str(M.Get(a, "cartaId")),
        };

        if (!e.TryGetValue(coord, out var lst)) { lst = new(); e[coord] = lst; }
        lst.Add(trap);

        log.Add(new Dictionary<string, object?>
        {
            ["tipo"] = "trampaColocada",
            ["uid"] = uid,
            ["objetivo"] = coord,
            ["habilidadId"] = habilidadId,
            ["turnosRestantes"] = duracion,
        });
    }

    private static bool CumpleAsentamiento(Tablero? previo, string coord, string uid)
    {
        if (previo == null) return false;
        if (!previo.TryGetValue(coord, out var cartas)) return false;
        return cartas.Any(c =>
            CartaHelper.OwnerUid(c) == uid && M.Int(M.Get(c, "turnosEnCelda")) >= 2);
    }

    private static void AgregarEstadoCarta(Dictionary<string, object?> carta, Dictionary<string, object?> nuevo)
    {
        var raw = M.List(M.Get(carta, "Efectos")).Select(M.Map).ToList();
        var idx = raw.FindIndex(m =>
            M.Str(M.Get(m, "tipo")) == M.Str(M.Get(nuevo, "tipo")) &&
            M.Str(M.Get(m, "origenUid")) == M.Str(M.Get(nuevo, "origenUid")));
        if (idx == -1) raw.Add(new Dictionary<string, object?>(nuevo));
        else if (M.Int(M.Get(nuevo, "turnosRestantes")) > M.Int(M.Get(raw[idx], "turnosRestantes")))
            raw[idx] = new Dictionary<string, object?>(nuevo);
        carta["Efectos"] = raw.Select(m => (object?)m).ToList();
    }

    private static Dictionary<string, object?> Fallo(Dictionary<string, object?> a, string motivo) => new()
    {
        ["tipo"] = "trampaFallida",
        ["uid"] = M.Str(M.Get(a, "uid")),
        ["objetivo"] = M.List(M.Get(a, "objetivos")).Select(M.Str).FirstOrDefault() ?? "",
        ["motivo"] = motivo,
    };
}