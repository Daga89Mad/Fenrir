using System;
using System.Collections.Generic;
using System.Linq;

// ─────────────────────────────────────────────────────────────────────────────
// WarZeroHistoriaMulti.cs
//
// Batallas de historia de VARIOS EJÉRCITOS (HistoriaDef.EsMultiBando), p. ej.
// «El hijo del odio · La batalla de Nar-Khal» (nefilim_3): el jugador, un
// ALIADO controlado por la máquina y uno o más ENEMIGOS, todos en partida
// normal (mano, mazo y robo).
//
//   • Uids sintéticos: `historia_bot` (Bot, el enemigo principal),
//     `historia_aliado` (Aliado) y `historia_bot_2`, `historia_bot_3`…
//     (EnemigosExtra). Ninguno existe en `Bots` ni en `Jugadores`.
//   • Cuarteles (ResolverCuartelesMulti): los del mapa. El jugador, el más al
//     sur; su aliado, el más cercano a él; los enemigos, los demás (primero el
//     principal, el más cercano al jugador).
//   • Alianza PERMANENTE jugador ⇄ aliado (`alianzas.activas` con
//     `permanente: true`, WarZeroAlianzas.cs): no caduca ni se puede
//     traicionar. Comparten casilla y suman fuerza (WarZeroLogic.Combate).
//   • Ninguno puede entrar en el cuartel del otro
//     (RevertirEntradasEnCuartelAliado, paso A de la resolución): en Combate
//     el dueño de un cuartel no se fusiona con su aliado, así que el aliado
//     podría conquistárselo.
//   • Cada bot juega con la IA de los bots (ConstruirJugadaBotHistoriaNormal).
//     Esa IA no sabe de alianzas: el aliado juega sobre una VISTA de la partida
//     (VistaBotHistoria) en la que el jugador no existe (ni sus cartas ni su
//     cuartel), así que nunca lo ataca.
//   • Fin (EvaluarFinHistoria): gana el jugador cuando han caído TODOS los
//     enemigos; pierde si cae su cuartel o una carta clave. Que caiga el
//     aliado no es derrota.
// ─────────────────────────────────────────────────────────────────────────────

public partial class WarZeroService
{
    /// Uid sintético del ALIADO del jugador en una batalla multi-bando.
    public const string HistoriaAliadoUid = "historia_aliado";

    /// Uid sintético del enemigo extra nº [i] (0-based): historia_bot_2, _3…
    public static string HistoriaEnemigoExtraUid(int i) => $"{HistoriaBotUid}_{i + 2}";

    /// Zona (color) del aliado y de los enemigos extra, en orden.
    private const string ZonaHistoriaAliado = "west";
    private static readonly string[] ZonasHistoriaExtra = { "east", "ne", "nw", "se", "sw" };

    /// Turnos con los que nace la alianza permanente (solo informativo: una
    /// alianza `permanente` no descuenta turnos).
    private const int TurnosAlianzaPermanente = 9999;

    // ── Lectura de la config multi-bando guardada en `historia` ──────────────

    private static List<string> UidsDeHistoria(Dictionary<string, object?> hist, string campo) =>
        M.List(M.Get(hist, campo)).Select(M.Str).Where(s => s != "").ToList();

    /// Uids de los ALIADOS del jugador (bots).
    internal static List<string> AliadosHistoria(Dictionary<string, object?> hist) =>
        UidsDeHistoria(hist, "aliadosUids");

    /// Uids de TODOS los enemigos del jugador: el bot principal y los extra.
    internal static List<string> EnemigosHistoria(Dictionary<string, object?> hist)
    {
        var lista = UidsDeHistoria(hist, "enemigosUids");
        var bot = M.Str(M.Get(hist, "botUid"));
        if (bot != "" && !lista.Contains(bot)) lista.Insert(0, bot);
        return lista;
    }

    /// Uids de todos los bots que la máquina tiene que mover: enemigos y aliados.
    internal static List<string> BotsHistoria(Dictionary<string, object?> hist) =>
        EnemigosHistoria(hist).Concat(AliadosHistoria(hist)).Distinct().ToList();

    /// True si la batalla tiene más de un bot (aliado o enemigos extra).
    internal static bool EsHistoriaMulti(Dictionary<string, object?> hist) =>
        M.Bool(M.Get(hist, "multiBando"));

    /// Zona (cosmética) de [uid] en la batalla: la guardada en `historia.zonas`;
    /// si no hay, la del bot de siempre.
    internal static string ZonaHistoriaDe(Dictionary<string, object?> hist, string uid)
    {
        var z = M.Str(M.Get(M.Map(M.Get(hist, "zonas")), uid));
        if (z != "") return z;
        return uid == M.Str(M.Get(hist, "jugadorUid")) ? ZonaHistoriaJugador : ZonaHistoriaBot;
    }

    /// Primer enemigo del jugador que sigue en pie (el principal si vive), o
    /// null si han caído todos.
    internal static string? EnemigoVivoHistoria(Dictionary<string, object?> hist, ISet<string> eliminados) =>
        EnemigosHistoria(hist).FirstOrDefault(u => !eliminados.Contains(u));

    // ── Cuarteles ────────────────────────────────────────────────────────────

    /// Reparte los cuarteles del mapa entre los bandos de una batalla
    /// multi-bando. Devuelve { rol → coord } con los roles "jugador", "bot",
    /// "aliado" y "extra0", "extra1"…; null si el mapa no tiene cuarteles
    /// suficientes. Un `BandoHistoria.Cuartel` fijo manda sobre el reparto.
    ///   1) Jugador: el cuartel más al SUR (y, a igualdad, el más al oeste).
    ///   2) Aliado: el más cercano al jugador.
    ///   3) Enemigo principal y extras: los restantes, del más cercano al
    ///      jugador al más lejano.
    internal static Dictionary<string, string>? ResolverCuartelesMulti(
        HistoriaDef def, List<string> obeliscosMapa, int filas, int columnas)
    {
        var extras = def.EnemigosExtra ?? Array.Empty<BandoHistoria>();
        int n = 2 + (def.Aliado != null ? 1 : 0) + extras.Count;

        static string Norm(string c) => c.Trim().ToUpperInvariant();
        var pool = obeliscosMapa.Select(Norm).Where(c => c != "").Distinct().ToList();
        if (pool.Count < n)
        {
            Console.Error.WriteLine(
                $"[WZ.Historia] {def.Id}: el mapa {def.MapaId} tiene {pool.Count} cuartel(es) para {n} ejércitos; " +
                "se usan los de por defecto");
            pool = Coords.ObeliscosFallback(n, filas, columnas).Select(Norm).Distinct().ToList();
        }
        pool = pool.OrderBy(c => c, StringComparer.Ordinal).ToList();

        var res = new Dictionary<string, string>();
        // Los fijados en la definición salen del reparto.
        void Fijar(string rol, string? coord)
        {
            if (string.IsNullOrWhiteSpace(coord)) return;
            var c = Norm(coord);
            res[rol] = c;
            pool.Remove(c);
        }
        Fijar("jugador", def.Jugador.Cuartel);
        Fijar("bot", def.Bot.Cuartel);
        if (def.Aliado != null) Fijar("aliado", def.Aliado.Cuartel);
        for (int i = 0; i < extras.Count; i++) Fijar($"extra{i}", extras[i].Cuartel);

        (int r, int c) Pos(string coord) => ParseCoord(coord) ?? (0, 0);
        int Dist(string a, string b)
        {
            var (ra, ca) = Pos(a);
            var (rb, cb) = Pos(b);
            return Math.Abs(ra - rb) + Math.Abs(ca - cb);
        }

        if (!res.ContainsKey("jugador"))
        {
            if (pool.Count == 0) return null;
            var j = pool.OrderByDescending(c => Pos(c).r).ThenBy(c => Pos(c).c).First();
            res["jugador"] = j;
            pool.Remove(j);
        }
        var cuartelJugador = res["jugador"];

        string? Siguiente()
        {
            if (pool.Count == 0) return null;
            var c = pool.OrderBy(x => Dist(x, cuartelJugador)).ThenBy(x => x, StringComparer.Ordinal).First();
            pool.Remove(c);
            return c;
        }

        if (def.Aliado != null && !res.ContainsKey("aliado"))
        {
            var a = Siguiente();
            if (a == null) return null;
            res["aliado"] = a;
        }
        if (!res.ContainsKey("bot"))
        {
            var b = Siguiente();
            if (b == null) return null;
            res["bot"] = b;
        }
        for (int i = 0; i < extras.Count; i++)
        {
            if (res.ContainsKey($"extra{i}")) continue;
            var e = Siguiente();
            if (e == null) return null;
            res[$"extra{i}"] = e;
        }

        // Todos distintos.
        if (res.Values.Distinct(StringComparer.OrdinalIgnoreCase).Count() != res.Count) return null;
        return res;
    }

    /// Mapa por defecto de una batalla multi-bando cuyo mapa aún no existe en
    /// el editor (todo tierra, un cuartel en cada esquina).
    public static (int filas, int columnas, List<string> obeliscos)? MapaMultiPorDefecto(string mapaId)
    {
        var def = HistoriaCatalogo.Todas.FirstOrDefault(h => h.EsMultiBando
            && string.Equals(h.MapaId, mapaId, StringComparison.OrdinalIgnoreCase));
        if (def == null) return null;
        int n = 2 + (def.Aliado != null ? 1 : 0) + (def.EnemigosExtra?.Count ?? 0);
        const int filas = 10, columnas = 14;
        return (filas, columnas, Coords.ObeliscosFallback(n, filas, columnas));
    }

    // ── Alianza permanente ───────────────────────────────────────────────────

    /// Campo `alianzas` inicial de la partida: el jugador y cada aliado, con
    /// una alianza PERMANENTE (no caduca, no se puede traicionar).
    internal static Dictionary<string, object?> AlianzasPermanentesHistoria(
        string jugadorUid, IEnumerable<string> aliados) => new()
    {
        ["propuestas"] = new List<object?>(),
        ["activas"] = aliados.Select(a => (object?)new Dictionary<string, object?>
        {
            ["uidA"] = jugadorUid,
            ["uidB"] = a,
            ["turnosRestantes"] = (long)TurnosAlianzaPermanente,
            [Alianzas.CampoPermanente] = true,
        }).ToList(),
        ["traiciones"] = new List<object?>(),
        ["avisos"] = new List<object?>(),
    };

    // ── Vista de la partida para la IA de un bot ─────────────────────────────

    /// La IA de los bots no sabe de alianzas: trata como enemigo a todo el que
    /// no es ella. Para un ALIADO del jugador se le da una vista en la que el
    /// jugador no existe (sus cartas no están en el tablero y figura como
    /// eliminado, así que su cuartel no es objetivo). Para los enemigos, la
    /// partida tal cual. `statsPartida` es la MISMA instancia que en [data]
    /// (la IA descuenta ahí la energía pre-pagada y fija la mano).
    internal static Dictionary<string, object?> VistaBotHistoria(
        Dictionary<string, object?> data, string botUid)
    {
        var hist = M.Map(M.Get(data, "historia"));
        if (!AliadosHistoria(hist).Contains(botUid)) return data;
        var jugadorUid = M.Str(M.Get(hist, "jugadorUid"));
        if (jugadorUid == "") return data;

        // Los "amigos" del bot: el jugador y los demás aliados del jugador.
        var amigos = AliadosHistoria(hist).Where(u => u != botUid).Append(jugadorUid).ToHashSet();

        var vista = new Dictionary<string, object?>(data);
        var tablero = new Dictionary<string, object?>();
        foreach (var kv in M.Map(M.Get(data, "tablero")))
        {
            var quedan = M.List(kv.Value)
                .Where(c => !amigos.Contains(CartaHelper.OwnerUid(M.Map(c))))
                .ToList();
            if (quedan.Count > 0) tablero[kv.Key] = quedan;
        }
        vista["tablero"] = tablero;
        vista["jugadoresEliminados"] = M.List(M.Get(data, "jugadoresEliminados"))
            .Select(M.Str).Where(s => s != "")
            .Concat(amigos).Distinct()
            .Cast<object?>().ToList();
        vista["statsPartida"] = M.Map(M.Get(data, "statsPartida"));
        return vista;
    }

    // ── Paso A: nadie entra en el cuartel de un aliado ──────────────────────

    /// Revierte a su celda del turno anterior cualquier carta que termine el
    /// movimiento en el cuartel de un ALIADO suyo (alianza permanente de
    /// historia). Muta [merged]. Las cartas recién colocadas (sin celda previa)
    /// no se tocan: solo se colocan en el cuartel propio.
    internal static void RevertirEntradasEnCuartelAliado(
        Dictionary<string, object?> data,
        Dictionary<string, List<Dictionary<string, object?>>> merged,
        Dictionary<string, List<Dictionary<string, object?>>> tableroPrevio,
        int turno)
    {
        var hist = M.Map(M.Get(data, "historia"));
        if (!EsHistoriaMulti(hist)) return;
        var aliadoDe = new Dictionary<string, HashSet<string>>();
        foreach (var par in Alianzas.Activas(Alianzas.Leer(data)))
        {
            if (!M.Bool(M.Get(par, Alianzas.CampoPermanente))) continue;
            var a = M.Str(M.Get(par, "uidA"));
            var b = M.Str(M.Get(par, "uidB"));
            if (a == "" || b == "") continue;
            if (!aliadoDe.TryGetValue(a, out var sa)) aliadoDe[a] = sa = new();
            if (!aliadoDe.TryGetValue(b, out var sb)) aliadoDe[b] = sb = new();
            sa.Add(b);
            sb.Add(a);
        }
        if (aliadoDe.Count == 0) return;

        var duenoCuartel = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in M.Map(M.Get(data, "obeliscos")))
        {
            var c = M.Str(kv.Value);
            if (c != "") duenoCuartel[c] = kv.Key;
        }

        var previo = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (coord, lst) in tableroPrevio)
            foreach (var c in lst)
            {
                var iid = M.Str(M.Get(c, "instanceId"));
                if (iid != "") previo[iid] = coord;
            }

        var revertir = new List<(string desde, string hacia, Dictionary<string, object?> carta)>();
        foreach (var (coord, lst) in merged)
        {
            if (!duenoCuartel.TryGetValue(coord, out var dueno)) continue;
            foreach (var c in lst)
            {
                var owner = CartaHelper.OwnerUid(c);
                if (owner == dueno || !aliadoDe.TryGetValue(owner, out var amigos) || !amigos.Contains(dueno))
                    continue;
                var iid = M.Str(M.Get(c, "instanceId"));
                if (iid == "" || !previo.TryGetValue(iid, out var antes)) continue;
                if (string.Equals(antes, coord, StringComparison.OrdinalIgnoreCase)) continue;
                revertir.Add((coord, antes, c));
            }
        }

        foreach (var (desde, hacia, carta) in revertir)
        {
            if (merged.TryGetValue(desde, out var lst)) lst.Remove(carta);
            if (!merged.TryGetValue(hacia, out var dst)) { dst = new(); merged[hacia] = dst; }
            dst.Add(carta);
            Console.WriteLine(
                $"[WZ.Historia] {M.Str(M.Get(hist, "id"))} turno {turno}: " +
                $"{M.Str(M.Get(carta, "Nombre", "nombre"))} {hacia}→{desde} revertido (cuartel aliado)");
        }
        foreach (var k in merged.Keys.Where(k => merged[k].Count == 0).ToList()) merged.Remove(k);
    }
}
