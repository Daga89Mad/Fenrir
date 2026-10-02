using System;
using System.Collections.Generic;
using System.Linq;

// ─────────────────────────────────────────────────────────────────────────────
// WarZeroHistoriaReglas.cs
//
// Reglas de MOVIMIENTO propias de algunas batallas de historia (p. ej.
// humanos_2) y las MARCAS que el cliente pinta sobre el tablero. Se enganchan
// en la resolución del turno (WarZeroService.ResolverTurnoCoreEnTx):
//
//   PASO A · AplicarReglasMovimientoHistoria (tras muros y confusión, sobre el
//   tablero fusionado). Autoritativo: un cliente desincronizado o manipulado
//   no puede saltárselas. Revierte a su celda del turno anterior cualquier
//   carta del JUGADOR que:
//     • sea de GUARNICIÓN (`historia.guarnicionJugador`) y se haya movido;
//     • entre en el cuartel NOMINAL del bot (`historia.cuartelBotInaccesible`);
//     • incumpla las reglas del TÚNEL (HistoriaTuneles.cs): entrar o salir por
//       una pared, moverse más de 2 dentro, pasar por una casilla sin explorar
//       o inundada, o atravesar el túnel desde fuera.
//   Después resuelve la INUNDACIÓN del turno (casillas sin explorar ocupadas).
//
//   PASO B · CamposHistoriaTrasResolver (junto al bombardeo, paso 7b): campos
//   a guardar en la partida: `tunel` (estado público del túnel) y
//   `marcasHistoria` (papeles del bot, cartas clave, guarnición, celdas
//   prohibidas y asalto general) para el cliente.
//
// En una batalla sin túnel, sin guarnición del jugador y sin cuartel nominal,
// el paso A no hace nada; el paso B solo publica marcas en las batallas con
// comportamiento "cazar" o con cartas clave.
// ─────────────────────────────────────────────────────────────────────────────

/// Resultado del paso A, que el paso B necesita para publicar el túnel.
public sealed class ResultadoReglasHistoria
{
    public ConfigTunel? Config { get; init; }
    public EstadoTunel? Tunel { get; init; }
}

public partial class WarZeroService
{
    /// PASO A (ver cabecera). Muta [merged]. Devuelve null si la partida no
    /// es de historia o la batalla no tiene reglas de movimiento propias.
    internal static ResultadoReglasHistoria? AplicarReglasMovimientoHistoria(
        Dictionary<string, object?> data,
        Dictionary<string, List<Dictionary<string, object?>>> merged,
        Dictionary<string, List<Dictionary<string, object?>>> tableroPrevio,
        int turno)
    {
        if (!M.Bool(M.Get(data, "esHistoria"))) return null;
        var hist = M.Map(M.Get(data, "historia"));
        var historiaId = M.Str(M.Get(hist, "id"));
        var jugadorUid = M.Str(M.Get(hist, "jugadorUid"));
        var botUid = M.Str(M.Get(hist, "botUid"));
        if (jugadorUid == "") return null;

        var guarnicion = M.List(M.Get(hist, "guarnicionJugador")).Select(M.Str)
            .Where(s => s != "").ToHashSet(StringComparer.Ordinal);
        bool inaccesible = M.Bool(M.Get(hist, "cuartelBotInaccesible"));
        var cuartelBot = M.Str(M.Get(M.Map(M.Get(data, "obeliscos")), botUid));

        var cfg = HistoriaTuneles.Get(historiaId);
        int semilla = SemillaPartida(hist);
        EstadoTunel? tunel = null;
        if (cfg != null)
            tunel = EstadoTunel.DesdeCampo(M.Get(data, "tunel")) ?? HistoriaTuneles.Inicial(cfg, semilla);

        if (cfg == null && guarnicion.Count == 0 && !(inaccesible && cuartelBot != "")) return null;

        // Terreno y rejilla cacheados en la creación de la partida.
        var mapaH = M.Map(M.Get(hist, "mapa"));
        int filas = M.Int(M.Get(mapaH, "filas"));
        int columnas = M.Int(M.Get(mapaH, "columnas"));
        if (filas <= 0) filas = 26;
        if (columnas <= 0) columnas = 99;
        var terreno = new Dictionary<string, string>();
        foreach (var kv in M.Map(M.Get(mapaH, "terreno"))) terreno[kv.Key] = M.Str(kv.Value);
        var celdasTunel = cfg != null
            ? HistoriaTuneles.TodasLasCeldas(cfg).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Posición del turno anterior de cada carta del jugador (por instancia).
        var previo = new Dictionary<string, (string coord, Dictionary<string, object?> carta)>();
        foreach (var (coord, lst) in tableroPrevio)
            foreach (var c in lst)
            {
                if (CartaHelper.OwnerUid(c) != jugadorUid) continue;
                var iid = M.Str(M.Get(c, "instanceId"));
                if (iid != "") previo[iid] = (coord, c);
            }

        // ── 1) Movimientos ilegales → vuelven a su celda ────────────────────
        var revertir = new List<(string desde, string hacia, Dictionary<string, object?> carta, string motivo)>();
        foreach (var (coord, lst) in merged)
            foreach (var c in lst)
            {
                if (CartaHelper.OwnerUid(c) != jugadorUid) continue;
                var iid = M.Str(M.Get(c, "instanceId"));
                if (iid == "" || !previo.TryGetValue(iid, out var prev)) continue;
                if (string.Equals(prev.coord, coord, StringComparison.OrdinalIgnoreCase)) continue;

                string? motivo = null;
                if (guarnicion.Contains(iid))
                    motivo = "la guarnición no sale del cuartel";
                else if (inaccesible && cuartelBot != ""
                         && string.Equals(coord, cuartelBot, StringComparison.OrdinalIgnoreCase))
                    motivo = "el cuartel enemigo es inaccesible";
                else if (cfg != null && tunel != null)
                {
                    int movPropio = CartaHelper.MovimientoEfectivo(prev.carta);
                    var (t, m) = TerrenoUtil.ClaseDeTipo(M.Int(M.Get(prev.carta, "Tipo", "tipo")));
                    bool Transitable(string x) => TerrenoUtil.Compatible(x, t, m, terreno);
                    bool implicaTunel = celdasTunel.Contains(prev.coord) || celdasTunel.Contains(coord);
                    var conTunel = HistoriaTuneles.Destinos(
                        cfg, tunel, prev.coord, movPropio, Transitable, filas, columnas);
                    if (implicaTunel)
                    {
                        if (!conTunel.Contains(coord)) motivo = "movimiento no permitido en el túnel";
                    }
                    else if (!conTunel.Contains(coord)
                             && HistoriaTuneles.DestinosSinTunel(prev.coord, movPropio, Transitable, filas, columnas)
                                 .Contains(coord))
                    {
                        motivo = "no se puede atravesar el túnel";
                    }
                }
                if (motivo != null) revertir.Add((coord, prev.coord, c, motivo));
            }

        foreach (var (desde, hacia, carta, motivo) in revertir)
        {
            if (merged.TryGetValue(desde, out var lst)) lst.Remove(carta);
            if (!merged.TryGetValue(hacia, out var dst)) { dst = new(); merged[hacia] = dst; }
            dst.Add(carta);
            tunel?.UltimoRevertidas.Add((desde, hacia, M.Str(M.Get(carta, "Nombre", "nombre"))));
            Console.WriteLine(
                $"[WZ.Historia] {historiaId} turno {turno}: {M.Str(M.Get(carta, "Nombre", "nombre"))} " +
                $"{hacia}→{desde} revertido ({motivo})");
        }
        foreach (var k in merged.Keys.Where(k => merged[k].Count == 0).ToList()) merged.Remove(k);

        // ── 2) Inundación del túnel ─────────────────────────────────────────
        if (cfg != null && tunel != null)
        {
            tunel.UltimoTurno = turno;
            var ahogadas = HistoriaTuneles.ResolverInundacion(cfg, tunel, semilla, merged);
            foreach (var (coord, carta) in ahogadas)
                tunel.UltimoAhogadas.Add((coord, M.Str(M.Get(carta, "Nombre", "nombre"))));
            if (tunel.UltimoInundadas.Count > 0 || tunel.UltimoLimpias.Count > 0)
                Console.WriteLine(
                    $"[WZ.Historia] {historiaId} turno {turno}: túnel · limpias [{string.Join(",", tunel.UltimoLimpias)}] " +
                    $"· inundadas [{string.Join(",", tunel.UltimoInundadas)}] · {ahogadas.Count} carta(s) ahogada(s)");
        }

        return new ResultadoReglasHistoria { Config = cfg, Tunel = tunel };
    }

    /// PASO B (ver cabecera): campos a añadir al update de la resolución.
    internal static Dictionary<string, object> CamposHistoriaTrasResolver(
        Dictionary<string, object?> data,
        Dictionary<string, List<Dictionary<string, object?>>> tableroFinal,
        int turno,
        ResultadoReglasHistoria? reglas)
    {
        var campos = new Dictionary<string, object>();
        if (!M.Bool(M.Get(data, "esHistoria"))) return campos;
        var hist = M.Map(M.Get(data, "historia"));

        if (reglas?.Config != null && reglas.Tunel != null)
            campos["tunel"] = reglas.Tunel.ACampo(reglas.Config);

        var marcas = ConstruirMarcasHistoria(hist, M.Map(M.Get(data, "obeliscos")), tableroFinal, turno + 1);
        if (marcas != null) campos["marcasHistoria"] = marcas;
        return campos;
    }

    /// Marcas que pinta el cliente (campo `marcasHistoria`), calculadas sobre
    /// [tablero] para el turno [turnoSiguiente]:
    ///   rolesBot   · coord → { cazadores, asalto } (cartas del bot por papel)
    ///   vip        · instanceIds de las cartas clave del jugador (👑)
    ///   guarnicion · instanceIds de la guarnición del jugador (no se mueven)
    ///   prohibidas · celdas en las que el jugador no puede entrar
    ///   turnoAsalto / asaltoGeneral / reunion · asalto general del bot
    /// null si la batalla no usa nada de esto.
    internal static Dictionary<string, object?>? ConstruirMarcasHistoria(
        Dictionary<string, object?> hist,
        Dictionary<string, object?> obeliscos,
        Dictionary<string, List<Dictionary<string, object?>>> tablero,
        int turnoSiguiente)
    {
        var botUid = M.Str(M.Get(hist, "botUid"));
        bool caza = M.Str(M.Get(hist, "botComportamiento")) == "cazar";
        var vip = M.List(M.Get(hist, "vipIds")).Select(M.Str).Where(s => s != "").ToList();
        var guarnicion = M.List(M.Get(hist, "guarnicionJugador")).Select(M.Str).Where(s => s != "").ToList();
        var prohibidas = new List<string>();
        var cuartelBot = M.Str(M.Get(obeliscos, botUid));
        if (M.Bool(M.Get(hist, "cuartelBotInaccesible")) && cuartelBot != "") prohibidas.Add(cuartelBot);

        if (!caza && vip.Count == 0 && guarnicion.Count == 0 && prohibidas.Count == 0) return null;

        int turnoAsalto = M.Int(M.Get(hist, "turnoAsalto"));
        bool asaltoGeneral = turnoAsalto > 0 && turnoSiguiente >= turnoAsalto;

        var roles = new Dictionary<string, object?>();
        if (caza)
        {
            foreach (var (coord, lst) in tablero.OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                int cazadores = 0, asalto = 0;
                foreach (var c in lst)
                {
                    if (CartaHelper.OwnerUid(c) != botUid) continue;
                    if (EsGuarnicion(c)) continue;
                    if (M.List(M.Get(c, CampoRuta)).Count > 0) continue;
                    if (asaltoGeneral || M.Str(M.Get(c, CampoRol)) == RolAsalto) asalto++;
                    else cazadores++;
                }
                if (cazadores + asalto == 0) continue;
                roles[coord] = new Dictionary<string, object?>
                {
                    ["cazadores"] = (long)cazadores,
                    ["asalto"] = (long)asalto,
                };
            }
        }

        return new Dictionary<string, object?>
        {
            ["rolesBot"] = roles,
            ["vip"] = vip.Cast<object?>().ToList(),
            ["guarnicion"] = guarnicion.Cast<object?>().ToList(),
            ["prohibidas"] = prohibidas.Cast<object?>().ToList(),
            ["turnoAsalto"] = (long)turnoAsalto,
            ["asaltoGeneral"] = asaltoGeneral,
            ["reunion"] = M.Str(M.Get(hist, "reunionAsalto")),
        };
    }
}