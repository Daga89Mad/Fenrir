// ─────────────────────────────────────────────────────────────────────────────
// WarZeroAlianzas.cs  (ARCHIVO NUEVO — añádelo al proyecto Fenrir)
//
// Lógica PURA (sin Firestore) del sistema de alianzas para partidas de 4+
// jugadores. Trabaja sobre mapas "Dart-like" (Dictionary<string, object?>) con
// los helpers M.* del proyecto, igual que WarZeroLogic.cs.
//
// Estado persistido en el doc de partida bajo la clave `alianzas`:
//
//   alianzas: {
//     propuestas: [ { deUid, paraUid, turnos, turnoPropuesta } ],
//     activas:    [ { uidA, uidB, turnosRestantes } ],
//     traiciones: [ { traidorUid, victimaUid } ],   // pendientes de aplicar
//     avisos:     [ { paraUid, tipo, deUid, turno } ] // in-app; se limpian al leerlos
//   }
//
//   tipo de aviso: "traicionado" | "alianza_terminada" | "aceptada" | "rechazada"
//
// Reglas:
//   • Solo en partidas de 4+ jugadores.
//   • Una alianza activa y una propuesta saliente por jugador como máximo.
//   • Aliados: suman fuerza y comparten casilla (la fusión de combate la hace
//     WarZeroLogic.Combate); su PC se divide /2 (floor) en cada resolución.
//   • Traición: se marca durante el turno; en la próxima resolución el par deja
//     de ser aliado (el traidor puede atacar) y la víctima recibe el aviso
//     DESPUÉS de resolver.
//   • Expiración: turnosRestantes baja 1 por resolución; al llegar a 0 se disuelve.
// ─────────────────────────────────────────────────────────────────────────────

public static class Alianzas
{
    public const int MinJugadores = 4;
    public const int MaxTurnos = 20;

    // ── Lectura ─────────────────────────────────────────────────────────────
    public static Dictionary<string, object?> Leer(Dictionary<string, object?> data)
        => M.Map(M.Get(data, "alianzas"));

    public static List<Dictionary<string, object?>> Propuestas(Dictionary<string, object?> a)
        => M.List(M.Get(a, "propuestas")).Select(M.Map).ToList();
    public static List<Dictionary<string, object?>> Activas(Dictionary<string, object?> a)
        => M.List(M.Get(a, "activas")).Select(M.Map).ToList();
    public static List<Dictionary<string, object?>> Traiciones(Dictionary<string, object?> a)
        => M.List(M.Get(a, "traiciones")).Select(M.Map).ToList();
    public static List<Dictionary<string, object?>> Avisos(Dictionary<string, object?> a)
        => M.List(M.Get(a, "avisos")).Select(M.Map).ToList();

    private static bool ParContiene(Dictionary<string, object?> par, string uid, out string otro)
    {
        var a = M.Str(M.Get(par, "uidA"));
        var b = M.Str(M.Get(par, "uidB"));
        if (uid == a) { otro = b; return true; }
        if (uid == b) { otro = a; return true; }
        otro = "";
        return false;
    }

    /// Aliado ACTIVO de `uid` (o null si no tiene alianza activa).
    public static string? AliadoActivoDe(Dictionary<string, object?> alianzas, string uid)
    {
        foreach (var p in Activas(alianzas))
            if (ParContiene(p, uid, out var o) && o != "") return o;
        return null;
    }

    /// Copia normalizada (con las 4 claves siempre presentes) para editar seguro.
    private static Dictionary<string, object?> ClonarConDefaults(Dictionary<string, object?> a) => new()
    {
        ["propuestas"] = Propuestas(a).Cast<object?>().ToList(),
        ["activas"] = Activas(a).Cast<object?>().ToList(),
        ["traiciones"] = Traiciones(a).Cast<object?>().ToList(),
        ["avisos"] = Avisos(a).Cast<object?>().ToList(),
    };

    // ── Resolución de turno ─────────────────────────────────────────────────

    /// Mapa simétrico uid -> aliadoUid con las alianzas ACTIVAS que siguen en
    /// pie ESTA resolución (se excluyen los pares con traición pendiente: esos
    /// combaten como enemigos y su PC NO se divide). Se pasa a Combate.Resolver.
    public static Dictionary<string, string> AliadoDeParaResolucion(Dictionary<string, object?> alianzas)
    {
        var betrayed = UidsConTraicionPendiente(alianzas);
        var map = new Dictionary<string, string>();
        foreach (var p in Activas(alianzas))
        {
            var a = M.Str(M.Get(p, "uidA"));
            var b = M.Str(M.Get(p, "uidB"));
            if (a == "" || b == "") continue;
            if (betrayed.Contains(a) || betrayed.Contains(b)) continue; // traicionada
            map[a] = b;
            map[b] = a;
        }
        return map;
    }

    private static HashSet<string> UidsConTraicionPendiente(Dictionary<string, object?> alianzas)
    {
        var set = new HashSet<string>();
        foreach (var t in Traiciones(alianzas))
        {
            var tr = M.Str(M.Get(t, "traidorUid"));
            var vi = M.Str(M.Get(t, "victimaUid"));
            if (tr != "") set.Add(tr);
            if (vi != "") set.Add(vi);
        }
        return set;
    }

    private static (string traidor, string victima) TraidorYVictimaDelPar(
        List<Dictionary<string, object?>> traiciones, string a, string b)
    {
        foreach (var t in traiciones)
        {
            var tr = M.Str(M.Get(t, "traidorUid"));
            var vi = M.Str(M.Get(t, "victimaUid"));
            if ((tr == a && vi == b) || (tr == b && vi == a)) return (tr, vi);
        }
        foreach (var t in traiciones)
        {
            var tr = M.Str(M.Get(t, "traidorUid"));
            if (tr == a) return (a, b);
            if (tr == b) return (b, a);
        }
        return ("", "");
    }

    /// Aplica el fin de turno a las alianzas: decrementa turnos, expira las de 0,
    /// aplica las traiciones (elimina el par y avisa a la víctima) y limpia la
    /// lista de traiciones. Devuelve el NUEVO mapa `alianzas` para persistir, o
    /// null si queda completamente vacío (el caller borra el campo).
    ///
    /// `nuevosAvisosTraicion` (out): avisos de tipo "traicionado" generados en
    /// esta resolución (para enviar push a las víctimas fuera de la transacción).
    public static Dictionary<string, object?>? AplicarFinDeTurno(
        Dictionary<string, object?> alianzas, int turnoResuelto,
        out List<Dictionary<string, object?>> nuevosAvisosTraicion)
    {
        nuevosAvisosTraicion = new();
        var traiciones = Traiciones(alianzas);
        var betrayed = UidsConTraicionPendiente(alianzas);

        var nuevasActivas = new List<Dictionary<string, object?>>();
        var avisos = Avisos(alianzas); // se conservan los previos no leídos

        foreach (var p in Activas(alianzas))
        {
            var a = M.Str(M.Get(p, "uidA"));
            var b = M.Str(M.Get(p, "uidB"));
            if (a == "" || b == "") continue;

            if (betrayed.Contains(a) || betrayed.Contains(b))
            {
                // Par traicionado → se disuelve y la víctima se entera ahora.
                var (traidor, victima) = TraidorYVictimaDelPar(traiciones, a, b);
                if (victima != "")
                {
                    var aviso = new Dictionary<string, object?>
                    {
                        ["paraUid"] = victima,
                        ["tipo"] = "traicionado",
                        ["deUid"] = traidor,
                        ["turno"] = turnoResuelto,
                    };
                    avisos.Add(aviso);
                    nuevosAvisosTraicion.Add(aviso);
                }
                continue; // no se re-añade → vuelven a ser enemigos
            }

            var restantes = M.Int(M.Get(p, "turnosRestantes")) - 1;
            if (restantes > 0)
            {
                nuevasActivas.Add(new Dictionary<string, object?>
                {
                    ["uidA"] = a,
                    ["uidB"] = b,
                    ["turnosRestantes"] = restantes,
                });
            }
            else
            {
                // Expiró: vuelven a ser enemigos. Aviso in-app a ambos.
                avisos.Add(new Dictionary<string, object?>
                { ["paraUid"] = a, ["tipo"] = "alianza_terminada", ["deUid"] = b, ["turno"] = turnoResuelto });
                avisos.Add(new Dictionary<string, object?>
                { ["paraUid"] = b, ["tipo"] = "alianza_terminada", ["deUid"] = a, ["turno"] = turnoResuelto });
            }
        }

        var propuestas = Propuestas(alianzas); // se conservan
        var nuevo = new Dictionary<string, object?>
        {
            ["propuestas"] = propuestas.Cast<object?>().ToList(),
            ["activas"] = nuevasActivas.Cast<object?>().ToList(),
            ["traiciones"] = new List<object?>(), // limpiadas
            ["avisos"] = avisos.Cast<object?>().ToList(),
        };

        if (propuestas.Count == 0 && nuevasActivas.Count == 0 && avisos.Count == 0)
            return null;
        return nuevo;
    }

    // ── Transiciones de estado (validadas) ──────────────────────────────────
    // Cada una devuelve (ok, mensaje, nuevoAlianzas). Si ok == false, el mapa
    // devuelto es el estado sin cambios (no se debe persistir).

    public static (bool ok, string mensaje, Dictionary<string, object?> nuevo) Proponer(
        Dictionary<string, object?> data, string deUid, string paraUid, int turnos)
    {
        var alianzas = ClonarConDefaults(Leer(data));
        var jugadores = JugadoresDe(data);
        var eliminados = EliminadosDe(data);

        if (jugadores.Count < MinJugadores)
            return (false, "Las alianzas solo están disponibles en partidas de 4 o más jugadores.", alianzas);
        if (deUid == "" || paraUid == "" || deUid == paraUid)
            return (false, "Destinatario no válido.", alianzas);
        if (!jugadores.Contains(deUid) || !jugadores.Contains(paraUid))
            return (false, "Ambos deben ser jugadores de la partida.", alianzas);
        if (eliminados.Contains(deUid) || eliminados.Contains(paraUid))
            return (false, "No se puede aliar con un jugador eliminado.", alianzas);
        if (AliadoActivoDe(alianzas, deUid) != null)
            return (false, "Ya tienes una alianza activa (solo una a la vez).", alianzas);
        if (AliadoActivoDe(alianzas, paraUid) != null)
            return (false, "Ese jugador ya tiene una alianza activa.", alianzas);

        var propuestas = Propuestas(alianzas);
        propuestas.RemoveAll(p => M.Str(M.Get(p, "deUid")) == deUid); // una saliente por jugador
        var t = Math.Clamp(turnos, 1, MaxTurnos);
        propuestas.Add(new Dictionary<string, object?>
        {
            ["deUid"] = deUid,
            ["paraUid"] = paraUid,
            ["turnos"] = t,
            ["turnoPropuesta"] = M.Int(M.Get(data, "turnoActual")),
        });
        alianzas["propuestas"] = propuestas.Cast<object?>().ToList();
        return (true, "Propuesta de alianza enviada.", alianzas);
    }

    public static (bool ok, string mensaje, Dictionary<string, object?> nuevo) Responder(
        Dictionary<string, object?> data, string uid, string proponenteUid, bool aceptar)
    {
        var alianzas = ClonarConDefaults(Leer(data));
        var propuestas = Propuestas(alianzas);
        var idx = propuestas.FindIndex(p =>
            M.Str(M.Get(p, "deUid")) == proponenteUid && M.Str(M.Get(p, "paraUid")) == uid);
        if (idx < 0)
            return (false, "La propuesta ya no existe.", alianzas);

        var prop = propuestas[idx];
        propuestas.RemoveAt(idx);
        var turnoActual = M.Int(M.Get(data, "turnoActual"));

        if (!aceptar)
        {
            alianzas["propuestas"] = propuestas.Cast<object?>().ToList();
            var av = Avisos(alianzas);
            av.Add(new Dictionary<string, object?>
            { ["paraUid"] = proponenteUid, ["tipo"] = "rechazada", ["deUid"] = uid, ["turno"] = turnoActual });
            alianzas["avisos"] = av.Cast<object?>().ToList();
            return (true, "Propuesta rechazada.", alianzas);
        }

        // Aceptar: revalidar que ninguno esté ya aliado (posible carrera).
        if (AliadoActivoDe(alianzas, uid) != null || AliadoActivoDe(alianzas, proponenteUid) != null)
        {
            alianzas["propuestas"] = propuestas.Cast<object?>().ToList();
            return (false, "Uno de los dos ya tiene una alianza activa.", alianzas);
        }

        // Eliminar cualquier otra propuesta que implique a estos dos jugadores.
        propuestas.RemoveAll(p =>
        {
            var d = M.Str(M.Get(p, "deUid"));
            var pa = M.Str(M.Get(p, "paraUid"));
            return d == uid || pa == uid || d == proponenteUid || pa == proponenteUid;
        });

        var activas = Activas(alianzas);
        var turnos = Math.Clamp(M.Int(M.Get(prop, "turnos")), 1, MaxTurnos);
        activas.Add(new Dictionary<string, object?>
        {
            ["uidA"] = proponenteUid,
            ["uidB"] = uid,
            ["turnosRestantes"] = turnos,
        });

        var avisos = Avisos(alianzas);
        avisos.Add(new Dictionary<string, object?>
        { ["paraUid"] = proponenteUid, ["tipo"] = "aceptada", ["deUid"] = uid, ["turno"] = turnoActual });

        alianzas["propuestas"] = propuestas.Cast<object?>().ToList();
        alianzas["activas"] = activas.Cast<object?>().ToList();
        alianzas["avisos"] = avisos.Cast<object?>().ToList();
        return (true, "Alianza aceptada.", alianzas);
    }

    public static (bool ok, string mensaje, Dictionary<string, object?> nuevo) Traicionar(
        Dictionary<string, object?> data, string uid)
    {
        var alianzas = ClonarConDefaults(Leer(data));
        var otro = AliadoActivoDe(alianzas, uid);
        if (otro == null)
            return (false, "No tienes ninguna alianza activa.", alianzas);

        var traiciones = Traiciones(alianzas);
        if (traiciones.Any(t => M.Str(M.Get(t, "traidorUid")) == uid))
            return (false, "Ya has marcado la traición para este turno.", alianzas);

        traiciones.Add(new Dictionary<string, object?>
        { ["traidorUid"] = uid, ["victimaUid"] = otro });
        alianzas["traiciones"] = traiciones.Cast<object?>().ToList();
        return (true, "Traición marcada. Se hará efectiva al resolver el turno.", alianzas);
    }

    /// Elimina los avisos dirigidos a `uid` (el cliente los ha mostrado).
    public static Dictionary<string, object?> LimpiarAvisosDe(
        Dictionary<string, object?> alianzas, string uid)
    {
        var a = ClonarConDefaults(alianzas);
        var avisos = Avisos(a);
        avisos.RemoveAll(x => M.Str(M.Get(x, "paraUid")) == uid);
        a["avisos"] = avisos.Cast<object?>().ToList();
        return a;
    }

    // ── Helpers ─────────────────────────────────────────────────────────────
    private static List<string> JugadoresDe(Dictionary<string, object?> data) =>
        M.List(M.Get(data, "jugadores"))
            .Select(j => M.Str(M.Get(M.Map(j), "uid")))
            .Where(u => u != "").ToList();

    private static HashSet<string> EliminadosDe(Dictionary<string, object?> data) =>
        M.List(M.Get(data, "jugadoresEliminados")).Select(M.Str).Where(u => u != "").ToHashSet();
}