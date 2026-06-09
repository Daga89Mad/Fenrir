// ─────────────────────────────────────────────────────────────────────────────
// WarZeroLogic.cs
//
// Port a C# de la lógica de resolución de turno de la app Flutter:
//   • Combate         (combate_service.dart)
//   • Habilidades     (habilidad_service.dart) — aplicar acciones + tick efectos
//   • Farmeo          (farmeo_service.dart)
//
// El tablero se representa como:  coord -> List<carta>  donde carta es un
// Dictionary<string, object?> con las mismas claves que en Firestore
// (Fuerza/Defensa/Coste/Nombre/ownerUid/ownerZone/Efectos...).
//
// Para RESOLVER no se necesita el cálculo de rango/BFS de habilidades: las
// acciones ya traen sus celdas objetivo (a.objetivos). Ese cálculo se queda en
// el cliente para la UI de selección.
// ─────────────────────────────────────────────────────────────────────────────

using Tablero = System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<System.Collections.Generic.Dictionary<string, object?>>>;
using EfectosCelda = System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<System.Collections.Generic.Dictionary<string, object?>>>;

// ═════════════════════════════════════════════════════════════════════════════
// HELPERS DE CARTA / TABLERO
// ═════════════════════════════════════════════════════════════════════════════

public static class CartaHelper
{
    public static int Fuerza(Dictionary<string, object?> c) => M.Int(M.Get(c, "Fuerza", "fuerza"));
    public static int DefensaBase(Dictionary<string, object?> c) => M.Int(M.Get(c, "Defensa", "defensa"));
    public static int Coste(Dictionary<string, object?> c) => M.Int(M.Get(c, "Coste", "coste"));
    public static string Nombre(Dictionary<string, object?> c) => M.Str(M.Get(c, "Nombre", "nombre"));
    public static string OwnerUid(Dictionary<string, object?> c) => M.Str(M.Get(c, "ownerUid"));
    public static string OwnerZone(Dictionary<string, object?> c) => M.Str(M.Get(c, "ownerZone"));

    public static List<Dictionary<string, object?>> Efectos(Dictionary<string, object?> c)
        => M.List(M.Get(c, "Efectos")).Select(M.Map).ToList();

    /// Suma de magnitud de venenos activos (turnosRestantes > 0).
    public static int DefensaReducidaPorEfectos(Dictionary<string, object?> c)
    {
        var raw = M.Get(c, "Efectos");
        if (raw is null) return 0;
        int total = 0;
        foreach (var item in M.List(raw))
        {
            var mm = M.Map(item);
            if (M.Int(M.Get(mm, "turnosRestantes")) <= 0) continue;
            if (M.Str(M.Get(mm, "tipo")) == "veneno")
                total += M.Int(M.Get(mm, "magnitud"));
        }
        return total;
    }

    public static int DefensaEfectiva(Dictionary<string, object?> c)
    {
        var ef = DefensaBase(c) - DefensaReducidaPorEfectos(c);
        return ef > 0 ? ef : 0;
    }

    /// Construye un tablero tipado desde un mapa "Dart-like" (coord -> lista de cartas).
    public static Tablero FromRaw(object? raw)
    {
        var t = new Tablero();
        foreach (var kv in M.Map(raw))
        {
            var cartas = M.List(kv.Value).Select(M.Map).ToList();
            t[kv.Key] = cartas;
        }
        return t;
    }

    /// Copia del tablero con copia de las cartas y de sus listas de Efectos.
    public static Tablero Copy(Tablero src)
    {
        var t = new Tablero();
        foreach (var kv in src)
        {
            var lista = new List<Dictionary<string, object?>>();
            foreach (var c in kv.Value)
            {
                var copy = new Dictionary<string, object?>(c);
                if (c.TryGetValue("Efectos", out var ef) && ef is not null)
                    copy["Efectos"] = M.List(ef).Select(m => (object?)M.Map(m)).ToList();
                lista.Add(copy);
            }
            t[kv.Key] = lista;
        }
        return t;
    }
}

// ═════════════════════════════════════════════════════════════════════════════
// COMBATE  (port de combate_service.dart)
// ═════════════════════════════════════════════════════════════════════════════

public class ObeliscoConquista
{
    public string Coord = "";
    public string ConquistadorUid = "";
    public string PerdedorUid = "";

    public Dictionary<string, object?> ToLogMap() => new()
    {
        ["coord"] = Coord,
        ["conquistadorUid"] = ConquistadorUid,
        ["perdedorUid"] = PerdedorUid,
        ["tipo"] = "conquista_cuartel",
    };
}

public class ResultadoCombate
{
    public string Coord = "";
    public string? GanadorUid;
    public string? GanadorZone;
    public List<string> DerrotadosUid = new();
    public Dictionary<string, int> EnergiesGanadas = new();
    public Dictionary<string, int> PcGanados = new();
    public List<Dictionary<string, object?>> Detalle = new();
    public bool EsConquistaObelisco;

    public Dictionary<string, object?> ToLogMap() => new()
    {
        ["coord"] = Coord,
        ["ganadorUid"] = GanadorUid,
        ["ganadorZone"] = GanadorZone,
        ["derrotadosUid"] = DerrotadosUid.Cast<object?>().ToList(),
        ["energiesGanadas"] = EnergiesGanadas.ToDictionary(k => k.Key, v => (object?)(long)v.Value),
        ["pcGanados"] = PcGanados.ToDictionary(k => k.Key, v => (object?)(long)v.Value),
        ["detalle"] = Detalle.Cast<object?>().ToList(),
        ["esConquistaObelisco"] = EsConquistaObelisco,
    };
}

public class ResolucionCombates
{
    public Tablero Tablero = new();
    public List<ResultadoCombate> Resultados = new();
    public Dictionary<string, int> EnergiesPorJugador = new();
    public Dictionary<string, int> PcPorJugador = new();
    public List<ObeliscoConquista> ObeliscosConquistados = new();
}

internal class Grupo
{
    public string OwnerUid = "";
    public string OwnerZone = "";
    public List<Dictionary<string, object?>> Cartas = new();
    public int DefensaBonus;

    public int TotalFuerza => Cartas.Sum(CartaHelper.Fuerza);
    public int TotalDefensa => Cartas.Sum(CartaHelper.DefensaEfectiva) + DefensaBonus;
    public int TotalDefensaBase => Cartas.Sum(CartaHelper.DefensaBase) + DefensaBonus;
    public int TotalReduccionVeneno => Cartas.Sum(CartaHelper.DefensaReducidaPorEfectos);
    public int TotalCoste => Cartas.Sum(CartaHelper.Coste);
    public int NumCartas => Cartas.Count;
}

public static class Combate
{
    public const int DefensaObelisco = 80;
    public const int EnergiesConquista = 100;
    public const int PcConquista = 100;

    public static ResolucionCombates Resolver(Tablero tablero, Dictionary<string, string> obeliscosPorJugador)
    {
        // Invertir: coord -> uid propietario del obelisco
        var obeliscoOwnerByCoord = new Dictionary<string, string>();
        foreach (var kv in obeliscosPorJugador) obeliscoOwnerByCoord[kv.Value] = kv.Key;

        var tableroResultante = new Tablero();
        var resultados = new List<ResultadoCombate>();
        var energiesPorJugador = new Dictionary<string, int>();
        var pcPorJugador = new Dictionary<string, int>();
        var conquistas = new List<ObeliscoConquista>();

        void AddEnergies(string uid, int v) => energiesPorJugador[uid] = energiesPorJugador.GetValueOrDefault(uid) + v;
        void AddPc(string uid, int v) => pcPorJugador[uid] = pcPorJugador.GetValueOrDefault(uid) + v;

        foreach (var coord in tablero.Keys.ToList())
        {
            var cartas = tablero[coord];
            if (cartas.Count == 0) continue;

            var esObeliscoCoord = obeliscoOwnerByCoord.ContainsKey(coord);
            string? obeliscoPropietarioUid = esObeliscoCoord ? obeliscoOwnerByCoord[coord] : null;

            var grupos = Agrupar(cartas);

            // ── Obelisco sin defensor (solo atacantes) ──────────────────────────
            if (esObeliscoCoord && obeliscoPropietarioUid != null && !grupos.ContainsKey(obeliscoPropietarioUid))
            {
                var fuerzaTotal = grupos.Values.Sum(g => g.TotalFuerza);
                if (fuerzaTotal > DefensaObelisco)
                {
                    var conquistadorUid = grupos.Aggregate((a, b) => a.Value.TotalFuerza >= b.Value.TotalFuerza ? a : b).Key;
                    conquistas.Add(new ObeliscoConquista { Coord = coord, ConquistadorUid = conquistadorUid, PerdedorUid = obeliscoPropietarioUid });
                    AddEnergies(conquistadorUid, EnergiesConquista);
                    AddPc(conquistadorUid, PcConquista);

                    tableroResultante[coord] = cartas;
                    resultados.Add(new ResultadoCombate
                    {
                        Coord = coord,
                        GanadorUid = conquistadorUid,
                        GanadorZone = grupos[conquistadorUid].OwnerZone,
                        DerrotadosUid = new List<string> { obeliscoPropietarioUid },
                        EnergiesGanadas = new() { [conquistadorUid] = EnergiesConquista },
                        PcGanados = new() { [conquistadorUid] = PcConquista },
                        Detalle = new(),
                        EsConquistaObelisco = true,
                    });
                }
                else
                {
                    tableroResultante[coord] = cartas;
                }
                continue;
            }

            // ── Sin combate (1 solo propietario) ────────────────────────────────
            if (grupos.Count <= 1)
            {
                tableroResultante[coord] = cartas;
                continue;
            }

            // ── Obelisco con defensor: +80 de defensa al propietario ────────────
            if (esObeliscoCoord && obeliscoPropietarioUid != null && grupos.ContainsKey(obeliscoPropietarioUid))
                grupos[obeliscoPropietarioUid].DefensaBonus = DefensaObelisco;

            // ── Poder neto ──────────────────────────────────────────────────────
            var poderNeto = new Dictionary<string, int>();
            foreach (var uid in grupos.Keys)
            {
                var defensaEnemigos = grupos.Where(e => e.Key != uid).Sum(e => e.Value.TotalDefensa);
                poderNeto[uid] = grupos[uid].TotalFuerza - defensaEnemigos;
            }

            var maxPoder = poderNeto.Values.Max();
            var ganadoresUid = poderNeto.Where(e => e.Value == maxPoder).Select(e => e.Key).ToList();

            // ── Detalle ─────────────────────────────────────────────────────────
            var detalle = grupos.Select(e => new Dictionary<string, object?>
            {
                ["ownerUid"] = e.Key,
                ["ownerZone"] = e.Value.OwnerZone,
                ["totalFuerza"] = e.Value.TotalFuerza,
                ["totalDefensa"] = e.Value.TotalDefensa,
                ["totalDefensaBase"] = e.Value.TotalDefensaBase,
                ["reduccionVeneno"] = e.Value.TotalReduccionVeneno,
                ["defensaBonus"] = e.Value.DefensaBonus,
                ["poderNeto"] = poderNeto[e.Key],
                ["numCartas"] = e.Value.NumCartas,
                ["cartas"] = e.Value.Cartas.Select(c =>
                {
                    var b = CartaHelper.DefensaBase(c);
                    var red = CartaHelper.DefensaReducidaPorEfectos(c);
                    return (object?)new Dictionary<string, object?>
                    {
                        ["nombre"] = CartaHelper.Nombre(c),
                        ["fuerza"] = CartaHelper.Fuerza(c),
                        ["defensa"] = b,
                        ["defensaEfectiva"] = b - red > 0 ? b - red : 0,
                        ["reduccionVeneno"] = red,
                        ["coste"] = CartaHelper.Coste(c),
                    };
                }).ToList(),
            }).ToList();

            string? ganadorUid;
            string? ganadorZone;
            List<string> derrotadosUid;
            List<Dictionary<string, object?>> supervivientes;
            bool esConquista = false;

            if (ganadoresUid.Count == 1)
            {
                ganadorUid = ganadoresUid[0];
                ganadorZone = grupos[ganadorUid].OwnerZone;
                derrotadosUid = grupos.Keys.Where(uid => uid != ganadorUid).ToList();
                supervivientes = grupos[ganadorUid].Cartas;

                foreach (var derrotadoUid in derrotadosUid)
                {
                    var grupo = grupos[derrotadoUid];
                    AddEnergies(ganadorUid, grupo.TotalCoste);
                    AddPc(ganadorUid, 3 * grupo.NumCartas);
                }

                if (esObeliscoCoord && obeliscoPropietarioUid != null && derrotadosUid.Contains(obeliscoPropietarioUid))
                {
                    esConquista = true;
                    conquistas.Add(new ObeliscoConquista { Coord = coord, ConquistadorUid = ganadorUid, PerdedorUid = obeliscoPropietarioUid });
                    AddEnergies(ganadorUid, EnergiesConquista);
                    AddPc(ganadorUid, PcConquista);
                }
            }
            else
            {
                // Empate: todos destruidos.
                ganadorUid = null;
                ganadorZone = null;
                derrotadosUid = grupos.Keys.ToList();
                supervivientes = new();
            }

            if (supervivientes.Count > 0)
                tableroResultante[coord] = supervivientes;

            resultados.Add(new ResultadoCombate
            {
                Coord = coord,
                GanadorUid = ganadorUid,
                GanadorZone = ganadorZone,
                DerrotadosUid = derrotadosUid,
                EnergiesGanadas = ganadorUid != null ? new() { [ganadorUid] = energiesPorJugador.GetValueOrDefault(ganadorUid) } : new(),
                PcGanados = ganadorUid != null ? new() { [ganadorUid] = pcPorJugador.GetValueOrDefault(ganadorUid) } : new(),
                Detalle = detalle,
                EsConquistaObelisco = esConquista,
            });
        }

        return new ResolucionCombates
        {
            Tablero = tableroResultante,
            Resultados = resultados,
            EnergiesPorJugador = energiesPorJugador,
            PcPorJugador = pcPorJugador,
            ObeliscosConquistados = conquistas,
        };
    }

    private static Dictionary<string, Grupo> Agrupar(List<Dictionary<string, object?>> cartas)
    {
        var grupos = new Dictionary<string, Grupo>();
        foreach (var carta in cartas)
        {
            var uid = CartaHelper.OwnerUid(carta);
            var zone = CartaHelper.OwnerZone(carta);
            if (grupos.TryGetValue(uid, out var g)) g.Cartas.Add(carta);
            else grupos[uid] = new Grupo { OwnerUid = uid, OwnerZone = zone, Cartas = new() { carta } };
        }
        return grupos;
    }
}

// ═════════════════════════════════════════════════════════════════════════════
// HABILIDADES  (port de habilidad_service.dart) — aplicar acciones + tick
// ═════════════════════════════════════════════════════════════════════════════

public enum EfectoTipo { Disparo, Teletransporte, Veneno }

public record Habilidad(int Id, string Nombre, EfectoTipo Efecto, bool ExcluyeCG, int DuracionTurnos, int DefensaReducida);

public static class CatalogoHabilidades
{
    private static readonly Dictionary<int, Habilidad> Catalogo = new()
    {
        [1] = new(1, "Disparo cercano", EfectoTipo.Disparo, false, 0, 0),
        [2] = new(2, "Disparo medio", EfectoTipo.Disparo, false, 0, 0),
        [3] = new(3, "Disparo lejano", EfectoTipo.Disparo, false, 0, 0),
        [4] = new(4, "Teletransporte medio", EfectoTipo.Teletransporte, true, 0, 0),
        [5] = new(5, "Teletransporte lejano", EfectoTipo.Teletransporte, true, 0, 0),
        [6] = new(6, "Veneno cercano", EfectoTipo.Veneno, false, 3, 3),
        [7] = new(7, "Veneno medio", EfectoTipo.Veneno, true, 3, 3),
        [8] = new(8, "Veneno lejano", EfectoTipo.Veneno, false, 3, 3),
    };

    public static Habilidad? Get(int id) => Catalogo.TryGetValue(id, out var h) ? h : null;
}

public class ResultadoAplicarAcciones
{
    public Tablero Tablero = new();
    public EfectosCelda EfectosCelda = new();
    public List<Dictionary<string, object?>> Log = new();
}

public class ResultadoTickEfectos
{
    public Tablero Tablero = new();
    public EfectosCelda EfectosCelda = new();
}

public static class Habilidades
{
    public static ResultadoAplicarAcciones AplicarAcciones(
        Tablero tableroIn,
        List<Dictionary<string, object?>> acciones,
        EfectosCelda efectosCeldaIn,
        Dictionary<string, string> obeliscosPorJugador)
    {
        var t = CartaHelper.Copy(tableroIn);
        var e = CopiarEfectos(efectosCeldaIn);
        var log = new List<Dictionary<string, object?>>();

        var teles = new List<Dictionary<string, object?>>();
        var disparos = new List<Dictionary<string, object?>>();
        var venenos = new List<Dictionary<string, object?>>();

        foreach (var a in acciones)
        {
            var h = CatalogoHabilidades.Get(M.Int(M.Get(a, "habilidadId")));
            if (h == null) continue;
            switch (h.Efecto)
            {
                case EfectoTipo.Teletransporte: teles.Add(a); break;
                case EfectoTipo.Disparo: disparos.Add(a); break;
                case EfectoTipo.Veneno: venenos.Add(a); break;
            }
        }

        foreach (var a in teles) AplicarTeletransporte(a, t, log, obeliscosPorJugador);
        foreach (var a in disparos) AplicarDisparo(a, t, log, obeliscosPorJugador);
        foreach (var a in venenos) AplicarVeneno(a, t, e, log, obeliscosPorJugador);

        PropagarVenenoACeldas(t, e);

        return new ResultadoAplicarAcciones { Tablero = t, EfectosCelda = e, Log = log };
    }

    private static readonly HashSet<string> _ = new();

    private static void AplicarTeletransporte(Dictionary<string, object?> a, Tablero t, List<Dictionary<string, object?>> log, Dictionary<string, string> obeliscos)
    {
        var h = CatalogoHabilidades.Get(M.Int(M.Get(a, "habilidadId")));
        if (h == null) return;

        var objetivos = M.List(M.Get(a, "objetivos")).Select(M.Str).ToList();
        var fromCoord = M.Get(a, "cartaOrigenCoord") as string;
        var fromIdxObj = M.Get(a, "cartaOrigenIndice");
        var uid = M.Str(M.Get(a, "uid"));

        if (fromCoord == null || fromIdxObj == null || objetivos.Count == 0)
        {
            log.Add(LogFallo(a, h, "Datos de teletransporte incompletos"));
            return;
        }
        var fromIdx = M.Int(fromIdxObj);
        var destino = objetivos[0];

        if (h.ExcluyeCG && obeliscos.Values.Contains(destino))
        {
            log.Add(LogFallo(a, h, "No se puede teletransportar a un cuartel"));
            return;
        }

        if (!t.TryGetValue(fromCoord, out var cartasOrigen) || fromIdx < 0 || fromIdx >= cartasOrigen.Count)
        {
            log.Add(LogFallo(a, h, "La carta origen ya no existe"));
            return;
        }

        var carta = cartasOrigen[fromIdx];
        if (CartaHelper.OwnerUid(carta) != uid)
        {
            log.Add(LogFallo(a, h, "La carta origen no pertenece al jugador"));
            return;
        }

        cartasOrigen.RemoveAt(fromIdx);
        if (cartasOrigen.Count == 0) t.Remove(fromCoord);
        if (!t.TryGetValue(destino, out var destList)) { destList = new(); t[destino] = destList; }
        destList.Add(carta);

        log.Add(new Dictionary<string, object?>
        {
            ["tipo"] = "teletransporte",
            ["habilidadId"] = h.Id,
            ["habilidadNombre"] = h.Nombre,
            ["uid"] = uid,
            ["zona"] = M.Str(M.Get(a, "zona")),
            ["origen"] = M.Str(M.Get(a, "origen")),
            ["cartaOrigenCoord"] = fromCoord,
            ["destino"] = destino,
            ["cartaNombre"] = CartaHelper.Nombre(carta),
        });
    }

    private static void AplicarDisparo(Dictionary<string, object?> a, Tablero t, List<Dictionary<string, object?>> log, Dictionary<string, string> obeliscos)
    {
        var h = CatalogoHabilidades.Get(M.Int(M.Get(a, "habilidadId")));
        if (h == null) return;

        foreach (var obj in M.List(M.Get(a, "objetivos")).Select(M.Str))
        {
            if (h.ExcluyeCG && obeliscos.Values.Contains(obj)) continue;

            var cartas = t.TryGetValue(obj, out var lst) ? lst : new();
            var destruidas = cartas.Select(c => (object?)new Dictionary<string, object?>
            {
                ["id"] = M.Get(c, "id", "Id") ?? "",
                ["Nombre"] = CartaHelper.Nombre(c),
                ["ownerUid"] = CartaHelper.OwnerUid(c),
                ["ownerZone"] = CartaHelper.OwnerZone(c),
            }).ToList();
            t.Remove(obj);

            log.Add(new Dictionary<string, object?>
            {
                ["tipo"] = "disparo",
                ["habilidadId"] = h.Id,
                ["habilidadNombre"] = h.Nombre,
                ["uid"] = M.Str(M.Get(a, "uid")),
                ["zona"] = M.Str(M.Get(a, "zona")),
                ["origen"] = M.Str(M.Get(a, "origen")),
                ["objetivo"] = obj,
                ["cartasDestruidas"] = destruidas,
            });
        }
    }

    private static void AplicarVeneno(Dictionary<string, object?> a, Tablero t, EfectosCelda e, List<Dictionary<string, object?>> log, Dictionary<string, string> obeliscos)
    {
        var h = CatalogoHabilidades.Get(M.Int(M.Get(a, "habilidadId")));
        if (h == null) return;
        var uid = M.Str(M.Get(a, "uid"));

        foreach (var obj in M.List(M.Get(a, "objetivos")).Select(M.Str))
        {
            if (h.ExcluyeCG && obeliscos.Values.Contains(obj)) continue;

            var efecto = new Dictionary<string, object?>
            {
                ["tipo"] = "veneno",
                ["turnosRestantes"] = h.DuracionTurnos,
                ["magnitud"] = h.DefensaReducida,
                ["origenUid"] = uid,
            };
            AgregarOFusionarEfectoCelda(e, obj, efecto);

            if (t.TryGetValue(obj, out var cartas))
                foreach (var c in cartas) AgregarOFusionarEfectoCarta(c, efecto);

            log.Add(new Dictionary<string, object?>
            {
                ["tipo"] = "veneno",
                ["habilidadId"] = h.Id,
                ["habilidadNombre"] = h.Nombre,
                ["uid"] = uid,
                ["zona"] = M.Str(M.Get(a, "zona")),
                ["origen"] = M.Str(M.Get(a, "origen")),
                ["objetivo"] = obj,
                ["turnosRestantes"] = h.DuracionTurnos,
                ["magnitud"] = h.DefensaReducida,
            });
        }
    }

    private static void PropagarVenenoACeldas(Tablero t, EfectosCelda e)
    {
        foreach (var kv in e)
        {
            if (!t.TryGetValue(kv.Key, out var cartas) || cartas.Count == 0) continue;
            foreach (var ef in kv.Value)
            {
                if (M.Str(M.Get(ef, "tipo")) != "veneno") continue;
                foreach (var c in cartas) AgregarOFusionarEfectoCarta(c, ef);
            }
        }
    }

    public static ResultadoTickEfectos TickEfectos(Tablero tableroIn, EfectosCelda efectosCeldaIn)
    {
        var t = CartaHelper.Copy(tableroIn);
        var e = new EfectosCelda();

        foreach (var kv in efectosCeldaIn)
        {
            var nuevos = kv.Value
                .Select(Decrementar)
                .Where(ef => M.Int(M.Get(ef, "turnosRestantes")) > 0)
                .ToList();
            if (nuevos.Count > 0) e[kv.Key] = nuevos;
        }

        foreach (var cartas in t.Values)
        {
            foreach (var c in cartas)
            {
                var raw = M.Get(c, "Efectos");
                if (raw is null) continue;
                var lista = M.List(raw);
                if (lista.Count == 0) continue;
                var nuevos = lista
                    .Select(m => Decrementar(M.Map(m)))
                    .Where(ef => M.Int(M.Get(ef, "turnosRestantes")) > 0)
                    .Select(ef => (object?)ef)
                    .ToList();
                if (nuevos.Count == 0) c.Remove("Efectos");
                else c["Efectos"] = nuevos;
            }
        }

        return new ResultadoTickEfectos { Tablero = t, EfectosCelda = e };
    }

    // ── Internos ─────────────────────────────────────────────────────────────

    private static Dictionary<string, object?> Decrementar(Dictionary<string, object?> ef)
    {
        var copy = new Dictionary<string, object?>(ef);
        copy["turnosRestantes"] = M.Int(M.Get(ef, "turnosRestantes")) - 1;
        return copy;
    }

    private static EfectosCelda CopiarEfectos(EfectosCelda src)
    {
        var o = new EfectosCelda();
        foreach (var kv in src)
            o[kv.Key] = kv.Value.Select(m => new Dictionary<string, object?>(m)).ToList();
        return o;
    }

    private static void AgregarOFusionarEfectoCelda(EfectosCelda efectos, string coord, Dictionary<string, object?> nuevo)
    {
        if (!efectos.TryGetValue(coord, out var lista)) { lista = new(); efectos[coord] = lista; }
        var idx = lista.FindIndex(ef =>
            M.Str(M.Get(ef, "tipo")) == M.Str(M.Get(nuevo, "tipo")) &&
            M.Str(M.Get(ef, "origenUid")) == M.Str(M.Get(nuevo, "origenUid")));
        if (idx == -1) lista.Add(new Dictionary<string, object?>(nuevo));
        else if (M.Int(M.Get(nuevo, "turnosRestantes")) > M.Int(M.Get(lista[idx], "turnosRestantes")))
            lista[idx] = new Dictionary<string, object?>(nuevo);
    }

    private static void AgregarOFusionarEfectoCarta(Dictionary<string, object?> carta, Dictionary<string, object?> nuevo)
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

    private static Dictionary<string, object?> LogFallo(Dictionary<string, object?> a, Habilidad h, string motivo) => new()
    {
        ["tipo"] = "fallida",
        ["habilidadId"] = h.Id,
        ["habilidadNombre"] = h.Nombre,
        ["uid"] = M.Str(M.Get(a, "uid")),
        ["zona"] = M.Str(M.Get(a, "zona")),
        ["origen"] = M.Str(M.Get(a, "origen")),
        ["motivo"] = motivo,
    };
}

// ═════════════════════════════════════════════════════════════════════════════
// FARMEO  (port de farmeo_service.dart)
// ═════════════════════════════════════════════════════════════════════════════

public class FarmeoResultado
{
    public Dictionary<string, int> EnergiesPorJugador = new();
    public List<Dictionary<string, object?>> FarmeoLog = new();
    public Dictionary<string, object?>? NuevoRayo;
}

public static class Farmeo
{
    public static FarmeoResultado Calcular(
        Tablero tablero,
        Dictionary<string, string> obeliscosPorJugador,
        Dictionary<string, List<string>> continentes,
        List<string> islaCentral,
        Dictionary<string, object?>? rayoActual,
        List<string> todasLasCeldas,
        Random rng)
    {
        var propietarioDeObelisco = new Dictionary<string, string>();
        foreach (var kv in obeliscosPorJugador) propietarioDeObelisco[kv.Value] = kv.Key;

        var rayoCoord = rayoActual != null ? M.Str(M.Get(rayoActual, "coord")) : null;

        var energies = new Dictionary<string, int>();
        var detalleMap = new Dictionary<string, Dictionary<string, int>>();
        var zonaMap = new Dictionary<string, string>();

        foreach (var kv in tablero)
        {
            var coord = kv.Key;
            foreach (var carta in kv.Value)
            {
                var uid = CartaHelper.OwnerUid(carta);
                var zona = CartaHelper.OwnerZone(carta);
                if (uid == "") continue;

                zonaMap[uid] = zona;
                if (!detalleMap.ContainsKey(uid))
                    detalleMap[uid] = new() { ["continenteEnemigo"] = 0, ["islaCentral"] = 0, ["rayo"] = 0 };

                foreach (var c in continentes)
                {
                    if (!c.Value.Contains(coord)) continue;
                    var propietarioUid = propietarioDeObelisco.GetValueOrDefault(c.Key);
                    if (!string.IsNullOrEmpty(propietarioUid) && propietarioUid != uid)
                    {
                        energies[uid] = energies.GetValueOrDefault(uid) + 5;
                        detalleMap[uid]["continenteEnemigo"] += 5;
                    }
                }

                if (islaCentral.Contains(coord))
                {
                    energies[uid] = energies.GetValueOrDefault(uid) + 7;
                    detalleMap[uid]["islaCentral"] += 7;
                }

                if (rayoCoord != null && coord == rayoCoord)
                {
                    energies[uid] = energies.GetValueOrDefault(uid) + 10;
                    detalleMap[uid]["rayo"] += 10;
                }
            }
        }

        var farmeoLog = detalleMap
            .Where(e => energies.GetValueOrDefault(e.Key) > 0)
            .Select(e => new Dictionary<string, object?>
            {
                ["uid"] = e.Key,
                ["zona"] = zonaMap.GetValueOrDefault(e.Key, ""),
                ["totalEnergies"] = energies.GetValueOrDefault(e.Key),
                ["detalle"] = e.Value.ToDictionary(k => k.Key, v => (object?)(long)v.Value),
            })
            .ToList();

        Dictionary<string, object?>? nuevoRayo = null;
        if (rayoActual != null)
        {
            var turnosRestantes = M.Int(M.Get(rayoActual, "turnosRestantes")) - 1;
            if (turnosRestantes > 0)
                nuevoRayo = new() { ["coord"] = M.Get(rayoActual, "coord"), ["turnosRestantes"] = turnosRestantes };
        }

        if (nuevoRayo == null)
        {
            var conCartas = tablero.Keys.ToHashSet();
            var obeliscos = obeliscosPorJugador.Values.ToHashSet();
            var disponibles = todasLasCeldas.Where(c => !conCartas.Contains(c) && !obeliscos.Contains(c)).ToList();
            if (disponibles.Count > 0)
            {
                var pick = disponibles[rng.Next(disponibles.Count)];
                nuevoRayo = new() { ["coord"] = pick, ["turnosRestantes"] = 3 };
            }
        }

        return new FarmeoResultado { EnergiesPorJugador = energies, FarmeoLog = farmeoLog, NuevoRayo = nuevoRayo };
    }
}