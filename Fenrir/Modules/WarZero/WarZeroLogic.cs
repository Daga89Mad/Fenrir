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
//
// v3 — ACCIONES DE DISTORSIÓN (ids 27-38, lógica en AccionesDistorsion.cs):
//   • Clon      (27/28/29) — señuelo: copia de una carta propia que no combate.
//   • Muro      (30/31/32) — 3 celdas encadenadas que nadie puede pisar ni cruzar.
//   • Confusión (33/34/35) — las cartas afectadas se mueven solas y luchan como
//                            bando propio (también contra su dueño).
//   • Fractura  (36/37/38) — desplaza las cartas de una celda a otra (≤ 4).
//   Aquí solo cambian: catálogo, despacho en AplicarAcciones, agrupación de
//   confundidas en Combate, caducidad de clones en TickEfectos y farmeo sin clones.
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
    public static string Imagen(Dictionary<string, object?> c) => M.Str(M.Get(c, "Imagen", "imagen"));

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

    /// Suma de magnitud de escudos activos (turnosRestantes > 0).
    public static int DefensaExtraPorEfectos(Dictionary<string, object?> c)
    {
        var raw = M.Get(c, "Efectos");
        if (raw is null) return 0;
        int total = 0;
        foreach (var item in M.List(raw))
        {
            var mm = M.Map(item);
            if (M.Int(M.Get(mm, "turnosRestantes")) <= 0) continue;
            var tipo = M.Str(M.Get(mm, "tipo"));
            if (tipo == "potDefensa")
                total += M.Int(M.Get(mm, "magnitud"));
        }
        return total;
    }

    /// Fuerza extra que aportan las potenciaciones de fuerza activas.
    public static int FuerzaExtraPorEfectos(Dictionary<string, object?> c)
    {
        var raw = M.Get(c, "Efectos");
        if (raw is null) return 0;
        int total = 0;
        foreach (var item in M.List(raw))
        {
            var mm = M.Map(item);
            if (M.Int(M.Get(mm, "turnosRestantes")) <= 0) continue;
            if (M.Str(M.Get(mm, "tipo")) == "potFuerza")
                total += M.Int(M.Get(mm, "magnitud"));
        }
        return total;
    }

    public static bool EstaParalizada(Dictionary<string, object?> c)
    {
        foreach (var item in M.List(M.Get(c, "Efectos")))
        {
            var mm = M.Map(item);
            if (M.Str(M.Get(mm, "tipo")) == "paralisis"
                && M.Int(M.Get(mm, "turnosRestantes")) > 0)
                return true;
        }
        return false;
    }

    /// True si la carta arrastra una CONFUSIÓN activa: su dueño no puede moverla
    /// (se mueve sola cada turno) y en combate forma un bando propio.
    public static bool EstaConfundida(Dictionary<string, object?> c)
    {
        foreach (var item in M.List(M.Get(c, "Efectos")))
        {
            var mm = M.Map(item);
            if (M.Str(M.Get(mm, "tipo")) == "confusion"
                && M.Int(M.Get(mm, "turnosRestantes")) > 0)
                return true;
        }
        return false;
    }

    /// True si la carta arrastra una invisibilidad activa.
    public static bool EsInvisible(Dictionary<string, object?> c)
    {
        foreach (var item in M.List(M.Get(c, "Efectos")))
        {
            var mm = M.Map(item);
            if (M.Str(M.Get(mm, "tipo")) == "invisibilidad"
                && M.Int(M.Get(mm, "turnosRestantes")) > 0)
                return true;
        }
        return false;
    }

    // ── SUPERVIVENCIA (ids 39/40/41) ─────────────────────────────────────────

    /// Nombre del campo (contador) donde se acumula la penalización permanente.
    public const string CampoSupervivencia = "SupervivenciaPenalizacion";

    /// Tipo del efecto tal y como se serializa en `Efectos` (espejo de
    /// `EfectoTipoEstado.supervivencia` del cliente).
    public const string TipoSupervivencia = "supervivencia";

    /// True si la carta arrastra una SUPERVIVENCIA activa: al perder un combate
    /// huye a una celda colindante en vez de morir (si hay alguna válida).
    public static bool TieneSupervivencia(Dictionary<string, object?> c)
    {
        foreach (var item in M.List(M.Get(c, "Efectos")))
        {
            var mm = M.Map(item);
            if (M.Str(M.Get(mm, "tipo")) == TipoSupervivencia
                && M.Int(M.Get(mm, "turnosRestantes")) > 0)
                return true;
        }
        return false;
    }

    /// Veces que esta carta se ha salvado de morir gracias a la supervivencia.
    ///
    /// NO es un efecto con duración: es un contador permanente que sobrevive a
    /// TickEfectos, a que expire la supervivencia y a que la carta se mueva. Es
    /// autoritativo del servidor (lo incrementa `Combate.Resolver` y lo re-sella
    /// `WarZeroService` cada turno desde el tablero anterior).
    public static int SupervivenciaPenalizacion(Dictionary<string, object?> c)
        => Math.Max(0, M.Int(M.Get(c, CampoSupervivencia, "supervivenciaPenalizacion")));

    /// Aplica [veces] penalizaciones del `SupervivenciaPerdidaFuerzaPct` % a
    /// [fuerzaBase] de forma ACUMULATIVA y con aritmética ENTERA, para dar
    /// exactamente el mismo número que el cliente (`fuerzaTrasSupervivencia`
    /// de `habilidad_model.dart`, que hace `(f * 75) ~/ 100`).
    ///
    /// Con 2 huidas una carta de 10 queda en 5 (10→7→5), no en 5 por resta
    /// lineal: importa que las dos plataformas coincidan al entero.
    public static int FuerzaTrasSupervivencia(int fuerzaBase, int veces)
    {
        var f = fuerzaBase;
        var n = veces < 0 ? 0 : veces;
        for (var i = 0; i < n; i++)
            f = f * (100 - CatalogoHabilidades.SupervivenciaPerdidaFuerzaPct) / 100;
        return f < 0 ? 0 : f;
    }

    /// Fuerza BASE real de la carta: la impresa ya mermada por las huidas.
    /// Sin huidas coincide exactamente con [Fuerza].
    public static int FuerzaBase(Dictionary<string, object?> c)
        => FuerzaTrasSupervivencia(Fuerza(c), SupervivenciaPenalizacion(c));

    /// Suma UNA huida al contador de la carta (la carta acaba de salvarse).
    /// Escribe siempre en la clave PascalCase y borra la variante lowercase para
    /// que no queden dos contadores desincronizados en el mismo documento.
    public static void SumarPenalizacionSupervivencia(Dictionary<string, object?> c)
    {
        var n = SupervivenciaPenalizacion(c) + 1;
        c[CampoSupervivencia] = (long)n;
        c.Remove("supervivenciaPenalizacion");
    }

    /// True si la carta es un CLON (señuelo de la acción Clon): no combate, no
    /// farmea, no lanza habilidades y desaparece al coincidir con un enemigo.
    public static bool EsClon(Dictionary<string, object?> c) => M.Bool(M.Get(c, "esClon"));

    /// Turnos de vida que le quedan a un clon.
    public static int ClonTurnos(Dictionary<string, object?> c) => M.Int(M.Get(c, "clonTurnos"));

    /// Cartas estáticas (Condicion == 3): no se mueven nunca.
    public static bool EsEstatica(Dictionary<string, object?> c)
        => M.Int(M.Get(c, "Condicion", "condicion")) == 3;

    /// Tipo de unidad (1 tierra, 2 aire, 3 mar). Sin dato → tierra.
    public static int Tipo(Dictionary<string, object?> c)
    {
        var t = M.Int(M.Get(c, "Tipo", "tipo"));
        return t <= 0 ? 1 : t;
    }

    public static string InstanceId(Dictionary<string, object?> c) => M.Str(M.Get(c, "instanceId"));

    /// Movimiento extra de las potenciaciones de movimiento activas.
    public static int MovimientoExtraPorEfectos(Dictionary<string, object?> c)
    {
        int total = 0;
        foreach (var item in M.List(M.Get(c, "Efectos")))
        {
            var mm = M.Map(item);
            if (M.Int(M.Get(mm, "turnosRestantes")) <= 0) continue;
            if (M.Str(M.Get(mm, "tipo")) == "potMovimiento")
                total += M.Int(M.Get(mm, "magnitud"));
        }
        return total;
    }

    /// Movimiento efectivo (base + potenciación), nunca negativo.
    public static int MovimientoEfectivo(Dictionary<string, object?> c)
        => Math.Max(0, M.Int(M.Get(c, "Movimiento", "movimiento")) + MovimientoExtraPorEfectos(c));

    /// Fuerza efectiva: base MERMADA por las huidas + potenciación de fuerza.
    /// La penalización se aplica ANTES del buff (el buff no se recorta), igual
    /// que en el cliente (`CartaEnCelda.fuerzaEfectiva`).
    public static int FuerzaEfectiva(Dictionary<string, object?> c)
        => FuerzaBase(c) + FuerzaExtraPorEfectos(c);

    public static int DefensaEfectiva(Dictionary<string, object?> c)
    {
        var ef = DefensaBase(c) - DefensaReducidaPorEfectos(c) + DefensaExtraPorEfectos(c);
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

    /// True si el grupo ganador eran cartas CONFUNDIDAS (sin control de su
    /// dueño): no suman victoria ni botín.
    public bool GanadorConfuso;

    public Dictionary<string, object?> ToLogMap() => new()
    {
        ["coord"] = Coord,
        ["ganadorUid"] = GanadorUid,
        ["ganadorZone"] = GanadorZone,
        ["ganadorConfuso"] = GanadorConfuso,
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

    /// Entradas de log de las cartas que se han salvado (o no) por SUPERVIVENCIA
    /// en esta resolución. El llamante las vuelca en el log del turno para que
    /// aparezcan en el Informe de Batalla. Tipos: "supervivenciaHuida" y
    /// "supervivenciaFallida".
    public List<Dictionary<string, object?>> Huidas = new();
}

internal class Grupo
{
    public string OwnerUid = "";
    public string OwnerZone = "";
    public List<Dictionary<string, object?>> Cartas = new();
    public int DefensaBonus;

    /// Grupo de cartas CONFUNDIDAS de un jugador: bando propio que lucha contra
    /// todos (incluido su dueño) y no genera botín.
    public bool Confuso;

    public int TotalFuerza => Cartas.Sum(CartaHelper.FuerzaEfectiva);
    public int TotalFuerzaBase => Cartas.Sum(CartaHelper.FuerzaBase);
    public int TotalBonusFuerza => Cartas.Sum(CartaHelper.FuerzaExtraPorEfectos);
    public int TotalDefensa => Cartas.Sum(CartaHelper.DefensaEfectiva) + DefensaBonus;
    public int TotalDefensaBase => Cartas.Sum(CartaHelper.DefensaBase) + DefensaBonus;
    public int TotalReduccionVeneno => Cartas.Sum(CartaHelper.DefensaReducidaPorEfectos);
    public int TotalBonusEscudo => Cartas.Sum(CartaHelper.DefensaExtraPorEfectos);
    public int TotalCoste => Cartas.Sum(CartaHelper.Coste);
    public int NumCartas => Cartas.Count;

    /// Uids reales distintos de las cartas del grupo. En un grupo NO fusionado
    /// es un único uid (== OwnerUid); en un grupo de aliados puede haber dos.
    public IEnumerable<string> UidsReales =>
        Cartas.Select(CartaHelper.OwnerUid).Where(u => u != "").Distinct();
}

public static class Combate
{
    public const int DefensaObelisco = 40;
    public const int EnergiesConquista = 100;
    public const int PcConquista = 100;

    /// Una carta que ha perdido su combate pero lleva SUPERVIVENCIA activa: se
    /// aparta aquí y se recoloca cuando ya están resueltas todas las celdas.
    private sealed class Huida
    {
        public string Origen = "";
        public Dictionary<string, object?> Carta = new();
    }

    /// True si la carta puede intentar huir en vez de morir.
    /// Se excluyen los clones (son señuelos y además se extraen antes del
    /// combate) y las cartas estáticas (Condicion 3 no se mueve nunca, así que
    /// tampoco huye).
    private static bool PuedeHuir(Dictionary<string, object?> c)
        => CartaHelper.TieneSupervivencia(c)
           && !CartaHelper.EsClon(c)
           && !CartaHelper.EsEstatica(c);

    /// Resuelve todos los combates del tablero.
    ///
    /// `aliadoDe` (opcional): mapa simétrico uid -> aliadoUid con las alianzas
    /// ACTIVAS y efectivas en ESTA resolución. Si es null o vacío, se comporta
    /// exactamente como antes (sin alianzas).
    ///
    /// Parámetros de SUPERVIVENCIA (todos opcionales, pero conviene pasarlos
    /// todos para que la huida respete las reglas del tablero):
    ///   `terreno`          coord -> "land|sea|deepSea|amphibious".
    ///   `celdasValidas`    todas las celdas del mapa. Sin esto, una carta en el
    ///                      borde podría huir fuera del tablero.
    ///   `celdasMuro`       celdas con muro activo: nadie puede estar en ellas.
    ///   `celdasProtegidas` coord -> uid del escudo: no se huye al escudo ajeno.
    ///   `rng`              para elegir la colindante. Si es null se crea uno.
    public static ResolucionCombates Resolver(
        Tablero tablero,
        Dictionary<string, string> obeliscosPorJugador,
        Dictionary<string, string>? aliadoDe = null,
        Dictionary<string, int>? defensaObeliscoPorCoord = null,
        Dictionary<string, string>? terreno = null,
        HashSet<string>? celdasValidas = null,
        HashSet<string>? celdasMuro = null,
        Dictionary<string, string>? celdasProtegidas = null,
        Random? rng = null)
    {
        // Defensa efectiva de un cuartel en [coord]: la base (DefensaObelisco)
        // salvo que haya un override por descarga reciente (0/25/50/75%).
        int DefObelisco(string coord) =>
            defensaObeliscoPorCoord != null &&
            defensaObeliscoPorCoord.TryGetValue(coord, out var d)
                ? d
                : DefensaObelisco;

        // Invertir: coord -> uid propietario del obelisco
        var obeliscoOwnerByCoord = new Dictionary<string, string>();
        foreach (var kv in obeliscosPorJugador) obeliscoOwnerByCoord[kv.Value] = kv.Key;

        var tableroResultante = new Tablero();
        var resultados = new List<ResultadoCombate>();
        var energiesPorJugador = new Dictionary<string, int>();
        var pcPorJugador = new Dictionary<string, int>();
        var conquistas = new List<ObeliscoConquista>();

        // Cartas que han perdido pero llevan supervivencia: se recolocan al
        // final, cuando ya no queda ningún combate por resolver.
        var huidas = new List<Huida>();

        void AddEnergies(string uid, int v) => energiesPorJugador[uid] = energiesPorJugador.GetValueOrDefault(uid) + v;
        void AddPc(string uid, int v) => pcPorJugador[uid] = pcPorJugador.GetValueOrDefault(uid) + v;

        foreach (var coord in tablero.Keys.ToList())
        {
            var cartas = tablero[coord];
            if (cartas.Count == 0) continue;

            var esObeliscoCoord = obeliscoOwnerByCoord.ContainsKey(coord);
            string? obeliscoPropietarioUid = esObeliscoCoord ? obeliscoOwnerByCoord[coord] : null;

            // El propietario del obelisco NUNCA se fusiona con su aliado en su
            // propia celda de cuartel: así el aliado puede conquistarlo.
            var grupos = Agrupar(cartas, aliadoDe, obeliscoPropietarioUid);

            // ── Obelisco sin defensor (solo atacantes) ──────────────────────────
            if (esObeliscoCoord && obeliscoPropietarioUid != null && !grupos.ContainsKey(obeliscoPropietarioUid))
            {
                var fuerzaTotal = grupos.Values.Sum(g => g.TotalFuerza);
                if (fuerzaTotal > DefObelisco(coord))
                {
                    var gConq = grupos.Values.Aggregate((a, b) => a.TotalFuerza >= b.TotalFuerza ? a : b);
                    var conquistadorUid = gConq.OwnerUid;
                    conquistas.Add(new ObeliscoConquista { Coord = coord, ConquistadorUid = conquistadorUid, PerdedorUid = obeliscoPropietarioUid });
                    AddEnergies(conquistadorUid, EnergiesConquista);
                    AddPc(conquistadorUid, PcConquista);

                    // Los atacantes han entrado en el cuartel enemigo → se revelan.
                    RevelarInvisibles(cartas);
                    tableroResultante[coord] = cartas;
                    resultados.Add(new ResultadoCombate
                    {
                        Coord = coord,
                        GanadorUid = conquistadorUid,
                        GanadorZone = gConq.OwnerZone,
                        DerrotadosUid = new List<string> { obeliscoPropietarioUid },
                        EnergiesGanadas = new() { [conquistadorUid] = EnergiesConquista },
                        PcGanados = new() { [conquistadorUid] = PcConquista },
                        Detalle = new(),
                        EsConquistaObelisco = true,
                    });
                }
                else
                {
                    // Aun sin conquistar, han entrado en el cuartel enemigo (combate
                    // contra su defensa base) → se revelan.
                    RevelarInvisibles(cartas);
                    tableroResultante[coord] = cartas;
                }
                continue;
            }

            // ── Sin combate (1 solo grupo) ──────────────────────────────────────
            // Con alianzas, dos aliados que comparten casilla forman UN grupo, así
            // que aquí no hay combate entre ellos.
            if (grupos.Count <= 1)
            {
                tableroResultante[coord] = cartas;
                continue;
            }

            // ── Obelisco con defensor: +40 de defensa al propietario ────────────
            if (esObeliscoCoord && obeliscoPropietarioUid != null && grupos.ContainsKey(obeliscoPropietarioUid))
                grupos[obeliscoPropietarioUid].DefensaBonus = DefObelisco(coord);

            // ── Poder neto (por CLAVE de grupo) ─────────────────────────────────
            var poderNeto = new Dictionary<string, int>();
            foreach (var key in grupos.Keys)
            {
                var defensaEnemigos = grupos.Where(e => e.Key != key).Sum(e => e.Value.TotalDefensa);
                poderNeto[key] = grupos[key].TotalFuerza - defensaEnemigos;
            }

            var maxPoder = poderNeto.Values.Max();
            var ganadorasKeys = poderNeto.Where(e => e.Value == maxPoder).Select(e => e.Key).ToList();

            // ── Detalle ─────────────────────────────────────────────────────────
            var detalle = grupos.Select(e => new Dictionary<string, object?>
            {
                ["ownerUid"] = e.Value.OwnerUid,
                ["ownerZone"] = e.Value.OwnerZone,
                ["aliados"] = e.Value.UidsReales.Cast<object?>().ToList(),
                ["confuso"] = e.Value.Confuso,
                ["totalFuerza"] = e.Value.TotalFuerza,
                ["totalDefensa"] = e.Value.TotalDefensa,
                ["totalDefensaBase"] = e.Value.TotalDefensaBase,
                ["reduccionVeneno"] = e.Value.TotalReduccionVeneno,
                ["bonusEscudo"] = e.Value.TotalBonusEscudo,
                ["bonusFuerza"] = e.Value.TotalBonusFuerza,
                ["defensaBonus"] = e.Value.DefensaBonus,
                ["poderNeto"] = poderNeto[e.Key],
                ["numCartas"] = e.Value.NumCartas,
                ["cartas"] = e.Value.Cartas.Select(c =>
                {
                    var b = CartaHelper.DefensaBase(c);
                    var red = CartaHelper.DefensaReducidaPorEfectos(c);
                    var esc = CartaHelper.DefensaExtraPorEfectos(c);
                    var efe = b - red + esc;
                    // `fuerza` es la fuerza BASE REAL (ya mermada por huidas
                    // anteriores), no la impresa en la carta: es la que ha
                    // entrado en el cálculo del combate.
                    var fb = CartaHelper.FuerzaBase(c);
                    var fbonus = CartaHelper.FuerzaExtraPorEfectos(c);
                    return (object?)new Dictionary<string, object?>
                    {
                        ["nombre"] = CartaHelper.Nombre(c),
                        ["fuerza"] = fb,
                        ["fuerzaEfectiva"] = fb + fbonus,
                        ["bonusFuerza"] = fbonus,
                        ["defensa"] = b,
                        ["defensaEfectiva"] = efe > 0 ? efe : 0,
                        ["reduccionVeneno"] = red,
                        ["bonusEscudo"] = esc,
                        ["coste"] = CartaHelper.Coste(c),
                        ["imagen"] = CartaHelper.Imagen(c),
                        // Supervivencia: para que el informe de batalla pueda
                        // avisar de que esta carta puede escapar y de cuánta
                        // fuerza lleva perdida.
                        ["puedeSobrevivir"] = CartaHelper.TieneSupervivencia(c),
                        ["huidasPrevias"] = CartaHelper.SupervivenciaPenalizacion(c),
                        ["fuerzaImpresa"] = CartaHelper.Fuerza(c),
                    };
                }).ToList(),
            }).ToList();

            string? ganadorUid;
            string? ganadorZone;
            List<string> derrotadosUid;
            List<Dictionary<string, object?>> supervivientes;
            bool esConquista = false;
            bool ganadorConfuso = false;

            if (ganadorasKeys.Count == 1)
            {
                var gk = ganadorasKeys[0];
                var gGan = grupos[gk];
                ganadorUid = gGan.OwnerUid;
                ganadorZone = gGan.OwnerZone;
                ganadorConfuso = gGan.Confuso;
                var ganadorUidLocal = ganadorUid;

                // Derrotados: uids reales de TODOS los grupos perdedores. Con
                // CONFUSIÓN un jugador puede perder cartas contra sus propias
                // cartas confundidas (o al revés): eso no cuenta como derrota
                // suya, así que su uid nunca figura como derrotado de su propio
                // grupo ganador.
                //
                // OJO: una carta que ESCAPA por supervivencia NO saca a su dueño
                // de esta lista. Perdió el combate igual, así que la conquista de
                // cuartel y las estadísticas siguen funcionando como siempre; lo
                // único que cambia es que la carta no se destruye.
                derrotadosUid = grupos
                    .Where(e => e.Key != gk)
                    .SelectMany(e => e.Value.UidsReales)
                    .Where(u => u != ganadorUidLocal)
                    .Distinct()
                    .ToList();

                supervivientes = gGan.Cartas;

                foreach (var e in grupos.Where(e => e.Key != gk))
                {
                    var grupo = e.Value;

                    // Separar las cartas que se salvan de las que mueren. Las que
                    // huyen se apartan SIEMPRE (incluso en fuego amigo por
                    // confusión): la supervivencia no depende de quién las mató.
                    var mueren = new List<Dictionary<string, object?>>();
                    foreach (var c in grupo.Cartas)
                    {
                        if (PuedeHuir(c)) huidas.Add(new Huida { Origen = coord, Carta = c });
                        else mueren.Add(c);
                    }

                    // Las cartas confundidas no generan botín para nadie, y matar
                    // cartas propias (fuego amigo por confusión) tampoco.
                    if (gGan.Confuso) continue;
                    if (grupo.UidsReales.All(u => u == ganadorUidLocal)) continue;
                    // El botín cuenta solo lo DESTRUIDO: una carta que escapa no
                    // da Ø ni PC (antes era grupo.TotalCoste / grupo.NumCartas).
                    if (mueren.Count == 0) continue;
                    AddEnergies(ganadorUid, mueren.Sum(CartaHelper.Coste));
                    AddPc(ganadorUid, 3 * mueren.Count);
                }

                if (!gGan.Confuso && esObeliscoCoord && obeliscoPropietarioUid != null && derrotadosUid.Contains(obeliscoPropietarioUid))
                {
                    esConquista = true;
                    conquistas.Add(new ObeliscoConquista { Coord = coord, ConquistadorUid = ganadorUid, PerdedorUid = obeliscoPropietarioUid });
                    AddEnergies(ganadorUid, EnergiesConquista);
                    AddPc(ganadorUid, PcConquista);
                }
            }
            else
            {
                // Empate EN CABEZA: el combate NO se resuelve. Los grupos
                // empatados al máximo poderNeto permanecen en la celda (standoff)
                // a la espera de que alguien rompa el empate. Los grupos con MENOS
                // poder (perdedores claros) SÍ son destruidos —salvo los que se
                // salven por supervivencia—. El cuartel NO se conquista mientras
                // dure el empate.
                ganadorUid = null;
                ganadorZone = null;

                var empatadas = ganadorasKeys.ToHashSet();
                derrotadosUid = grupos
                    .Where(e => !empatadas.Contains(e.Key))
                    .SelectMany(e => e.Value.UidsReales)
                    .Distinct()
                    .ToList();
                supervivientes = grupos
                    .Where(e => empatadas.Contains(e.Key))
                    .SelectMany(e => e.Value.Cartas)
                    .ToList();

                foreach (var e in grupos.Where(e => !empatadas.Contains(e.Key)))
                    foreach (var c in e.Value.Cartas)
                        if (PuedeHuir(c)) huidas.Add(new Huida { Origen = coord, Carta = c });
            }

            // Los supervivientes entraron en combate → pierden la invisibilidad.
            RevelarInvisibles(supervivientes);
            if (supervivientes.Count > 0)
                tableroResultante[coord] = supervivientes;

            resultados.Add(new ResultadoCombate
            {
                Coord = coord,
                GanadorUid = ganadorUid,
                GanadorZone = ganadorZone,
                DerrotadosUid = derrotadosUid,
                EnergiesGanadas = ganadorUid != null && !ganadorConfuso ? new() { [ganadorUid] = energiesPorJugador.GetValueOrDefault(ganadorUid) } : new(),
                PcGanados = ganadorUid != null && !ganadorConfuso ? new() { [ganadorUid] = pcPorJugador.GetValueOrDefault(ganadorUid) } : new(),
                Detalle = detalle,
                EsConquistaObelisco = esConquista,
                GanadorConfuso = ganadorConfuso,
            });
        }

        // ── SUPERVIVENCIA: recolocar a los que se han salvado ────────────────
        //    Se hace AQUÍ, con todas las celdas ya resueltas, para que nadie
        //    caiga en una celda cuyo combate estaba pendiente.
        var logHuidas = ReubicarHuidos(
            huidas, tableroResultante, obeliscoOwnerByCoord, aliadoDe,
            terreno, celdasValidas, celdasMuro, celdasProtegidas, rng);

        // ── Penalización de alianza: el PC de cada aliado se divide /2 (floor),
        //    participe o no en cada batalla. Las energías NO se tocan.
        if (aliadoDe != null && aliadoDe.Count > 0)
        {
            foreach (var uid in aliadoDe.Keys.ToList())
            {
                if (pcPorJugador.TryGetValue(uid, out var pc) && pc > 0)
                    pcPorJugador[uid] = pc / 2;
            }
        }

        return new ResolucionCombates
        {
            Tablero = tableroResultante,
            Resultados = resultados,
            EnergiesPorJugador = energiesPorJugador,
            PcPorJugador = pcPorJugador,
            ObeliscosConquistados = conquistas,
            Huidas = logHuidas,
        };
    }

    /// Coloca cada carta salvada en una celda ORTOGONALMENTE colindante a la que
    /// perdió, elegida al azar entre las válidas. Si no hay ninguna válida, la
    /// carta muere (no se reinserta) y queda un log "supervivenciaFallida".
    ///
    /// Una celda destino es válida si:
    ///   · está en el tablero (`celdasValidas`; sin ese dato solo se descartan
    ///     los índices negativos, así que pásalo siempre),
    ///   · no es un cuartel,
    ///   · no tiene un muro activo,
    ///   · no está escudada por OTRO jugador,
    ///   · admite el TIPO de la carta según el terreno (`TeleCanLand`: una
    ///     unidad de tierra no huye al mar),
    ///   · y no contiene cartas enemigas — si no, la huida sería un suicidio y
    ///     además dejaría un combate sin resolver en el tablero ya cerrado.
    ///
    /// El contador `SupervivenciaPenalizacion` se incrementa SOLO si la huida se
    /// consuma: una carta que muere no paga el 25 %.
    private static List<Dictionary<string, object?>> ReubicarHuidos(
        List<Huida> huidas,
        Tablero t,
        Dictionary<string, string> obeliscoOwnerByCoord,
        Dictionary<string, string>? aliadoDe,
        Dictionary<string, string>? terreno,
        HashSet<string>? celdasValidas,
        HashSet<string>? celdasMuro,
        Dictionary<string, string>? protegidas,
        Random? rng)
    {
        var log = new List<Dictionary<string, object?>>();
        if (huidas.Count == 0) return log;

        var azar = rng ?? new Random();
        var terr = terreno ?? new Dictionary<string, string>();

        // Orden determinista (celda, instancia): con la misma semilla y el mismo
        // estado, el resultado es reproducible.
        foreach (var h in huidas
            .OrderBy(x => x.Origen, StringComparer.Ordinal)
            .ThenBy(x => CartaHelper.InstanceId(x.Carta), StringComparer.Ordinal))
        {
            var carta = h.Carta;
            var owner = CartaHelper.OwnerUid(carta);
            var tipo = CartaHelper.Tipo(carta);

            bool EsAmiga(Dictionary<string, object?> x)
            {
                var u = CartaHelper.OwnerUid(x);
                if (u == owner) return true;
                return aliadoDe != null
                       && aliadoDe.TryGetValue(owner, out var ally)
                       && !string.IsNullOrEmpty(ally)
                       && ally == u;
            }

            var destinos = Vecinas(h.Origen)
                .Where(d => celdasValidas == null || celdasValidas.Contains(d))
                .Where(d => !obeliscoOwnerByCoord.ContainsKey(d))
                .Where(d => celdasMuro == null || !celdasMuro.Contains(d))
                .Where(d => protegidas == null
                            || !(protegidas.TryGetValue(d, out var s) && s != owner))
                .Where(d => Habilidades.TeleCanLand(d, tipo, terr))
                .Where(d => !t.TryGetValue(d, out var ocup)
                            || ocup.Count == 0
                            || ocup.All(EsAmiga))
                .OrderBy(d => d, StringComparer.Ordinal)
                .ToList();

            if (destinos.Count == 0)
            {
                log.Add(new Dictionary<string, object?>
                {
                    ["tipo"] = "supervivenciaFallida",
                    ["uid"] = owner,
                    ["zona"] = CartaHelper.OwnerZone(carta),
                    ["origen"] = h.Origen,
                    ["cartaNombre"] = CartaHelper.Nombre(carta),
                    ["motivo"] = "Sin celda colindante válida para huir",
                });
                continue; // muere como siempre
            }

            var destino = destinos[azar.Next(destinos.Count)];

            var fuerzaAntes = CartaHelper.FuerzaBase(carta);
            CartaHelper.SumarPenalizacionSupervivencia(carta);
            var fuerzaDespues = CartaHelper.FuerzaBase(carta);

            // Ha estado en combate: pierde la invisibilidad, como cualquier
            // superviviente. La SUPERVIVENCIA en cambio se conserva: le quedan
            // los turnos que le queden y puede volver a salvarla (pagando otro
            // 25 %); es TickEfectos quien la caduca.
            RevelarInvisibles(new List<Dictionary<string, object?>> { carta });

            if (!t.TryGetValue(destino, out var lst)) { lst = new(); t[destino] = lst; }
            lst.Add(carta);

            log.Add(new Dictionary<string, object?>
            {
                ["tipo"] = "supervivenciaHuida",
                ["uid"] = owner,
                ["zona"] = CartaHelper.OwnerZone(carta),
                ["origen"] = h.Origen,
                ["destino"] = destino,
                ["cartaNombre"] = CartaHelper.Nombre(carta),
                ["fuerzaAntes"] = fuerzaAntes,
                ["fuerzaDespues"] = fuerzaDespues,
                ["perdidaPct"] = CatalogoHabilidades.SupervivenciaPerdidaFuerzaPct,
                ["huidasTotales"] = CartaHelper.SupervivenciaPenalizacion(carta),
            });
        }

        return log;
    }

    /// Las 4 celdas ortogonalmente colindantes a [coord] ("B3" → A3, C3, B2, B4).
    /// Solo descarta los índices negativos; el límite superior lo pone
    /// `celdasValidas` en el llamante.
    private static IEnumerable<string> Vecinas(string coord)
    {
        if (string.IsNullOrEmpty(coord) || coord.Length < 2) yield break;
        var ri = char.ToUpperInvariant(coord[0]) - 'A';
        if (!int.TryParse(coord[1..], out var col)) yield break;
        var ci = col - 1;
        if (ri < 0 || ci < 0) yield break;

        foreach (var (dr, dc) in new[] { (-1, 0), (1, 0), (0, -1), (0, 1) })
        {
            var nr = ri + dr;
            var nc = ci + dc;
            if (nr < 0 || nc < 0) continue;
            yield return $"{(char)('A' + nr)}{nc + 1}";
        }
    }

    /// Rompe la invisibilidad de las cartas indicadas (al entrar en combate).
    /// Elimina cualquier efecto "invisibilidad" de su lista `Efectos`. Las cartas
    /// que mueren en el combate no llegan aquí (no son supervivientes), así que
    /// "morir por una acción" queda cubierto de forma natural.
    private static void RevelarInvisibles(List<Dictionary<string, object?>> cartas)
    {
        foreach (var c in cartas)
        {
            var raw = M.Get(c, "Efectos");
            if (raw is null) continue;
            var lista = M.List(raw);
            if (lista.Count == 0) continue;
            var nuevos = lista.Select(M.Map)
                .Where(m => M.Str(M.Get(m, "tipo")) != "invisibilidad")
                .Select(m => (object?)m).ToList();
            if (nuevos.Count == lista.Count) continue;
            if (nuevos.Count == 0) c.Remove("Efectos");
            else c["Efectos"] = nuevos;
        }
    }

    /// Agrupa las cartas de una celda.
    ///
    /// Sin `aliadoDe`, agrupa por uid (comportamiento clásico). Con `aliadoDe`,
    /// fusiona las cartas de dos aliados en un mismo grupo (suman fuerza y
    /// comparten casilla) EXCEPTO al `obeliscoOwnerUid` de esta celda, que se
    /// mantiene como grupo propio para que su aliado pueda conquistarle el cuartel.
    ///
    /// La CLAVE del diccionario es un identificador interno de grupo (puede ser
    /// compuesto para aliados). El `OwnerUid`/`OwnerZone` de cada grupo es el
    /// representante (el aliado con más fuerza) y es quien recibe energies/PC.
    ///
    /// CONFUSIÓN: fuera de las celdas de cuartel, las cartas confundidas de cada
    /// jugador forman un grupo PROPIO (`confusion|uid`) que no se fusiona con su
    /// dueño ni con sus aliados: luchan contra todos. En una celda de cuartel se
    /// agrupan normal (así nadie puede "conquistarse" su propio cuartel).
    public const string PrefijoGrupoConfuso = "confusion|";

    private static Dictionary<string, Grupo> Agrupar(
        List<Dictionary<string, object?>> cartas,
        Dictionary<string, string>? aliadoDe,
        string? obeliscoOwnerUid)
    {
        string ClaveGrupo(string uid)
        {
            if (aliadoDe == null) return uid;
            // El dueño del obelisco de esta celda nunca se fusiona.
            if (obeliscoOwnerUid != null && uid == obeliscoOwnerUid) return uid;
            if (!aliadoDe.TryGetValue(uid, out var ally) || string.IsNullOrEmpty(ally)) return uid;
            // Tampoco fusionar con el aliado si el aliado ES el dueño del obelisco
            // aquí: así puedo atacar/conquistar su cuartel.
            if (obeliscoOwnerUid != null && ally == obeliscoOwnerUid) return uid;
            return string.CompareOrdinal(uid, ally) <= 0 ? $"{uid}|{ally}" : $"{ally}|{uid}";
        }

        var grupos = new Dictionary<string, Grupo>();
        foreach (var carta in cartas)
        {
            var uid = CartaHelper.OwnerUid(carta);
            var zone = CartaHelper.OwnerZone(carta);
            var confusa = obeliscoOwnerUid == null && CartaHelper.EstaConfundida(carta);
            var key = confusa ? PrefijoGrupoConfuso + uid : ClaveGrupo(uid);
            if (grupos.TryGetValue(key, out var g)) g.Cartas.Add(carta);
            else grupos[key] = new Grupo { OwnerUid = uid, OwnerZone = zone, Cartas = new() { carta }, Confuso = confusa };
        }

        // Representante de cada grupo fusionado: el aliado con más fuerza (define
        // ganadorZone y quién recibe energies/PC). En grupos no fusionados es el
        // único uid.
        foreach (var g in grupos.Values)
        {
            var rep = g.Cartas
                .GroupBy(CartaHelper.OwnerUid)
                .Select(gr => new
                {
                    Uid = gr.Key,
                    Zone = CartaHelper.OwnerZone(gr.First()),
                    Fuerza = gr.Sum(CartaHelper.FuerzaEfectiva),
                })
                .OrderByDescending(x => x.Fuerza)
                .ThenBy(x => x.Uid, StringComparer.Ordinal)
                .First();
            g.OwnerUid = rep.Uid;
            g.OwnerZone = rep.Zone;
        }

        return grupos;
    }
}

// ═════════════════════════════════════════════════════════════════════════════
// HABILIDADES  (port de habilidad_service.dart) — aplicar acciones + tick
// ═════════════════════════════════════════════════════════════════════════════

public enum EfectoTipo { Disparo, Teletransporte, Veneno, Paralisis, Escudo, PotFuerza, PotDefensa, PotMovimiento, Invisibilidad, Clon, Muro, Confusion, Fractura, Supervivencia }

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
        [7] = new(7, "Veneno medio", EfectoTipo.Veneno, false, 3, 3),
        [8] = new(8, "Veneno lejano", EfectoTipo.Veneno, false, 3, 3),
        [9] = new(9, "Parálisis cercana", EfectoTipo.Paralisis, false, 3, 0),
        [10] = new(10, "Parálisis media", EfectoTipo.Paralisis, false, 3, 0),
        [11] = new(11, "Parálisis lejana", EfectoTipo.Paralisis, false, 3, 0),
        [12] = new(12, "Escudo cercano", EfectoTipo.Escudo, false, 3, 3),
        [13] = new(13, "Escudo medio", EfectoTipo.Escudo, false, 3, 3),
        [14] = new(14, "Escudo lejano", EfectoTipo.Escudo, false, 3, 3),
        // Potenciaciones (buff aliado). Magnitud/duración configurables aquí
        // (espejo de las constantes kPot* del cliente).
        [15] = new(15, "Potenciar fuerza cercano", EfectoTipo.PotFuerza, false, PotDuracion, PotFuerzaMag),
        [16] = new(16, "Potenciar fuerza medio", EfectoTipo.PotFuerza, false, PotDuracion, PotFuerzaMag),
        [17] = new(17, "Potenciar fuerza lejano", EfectoTipo.PotFuerza, false, PotDuracion, PotFuerzaMag),
        [18] = new(18, "Potenciar defensa cercano", EfectoTipo.PotDefensa, false, PotDuracion, PotDefensaMag),
        [19] = new(19, "Potenciar defensa medio", EfectoTipo.PotDefensa, false, PotDuracion, PotDefensaMag),
        [20] = new(20, "Potenciar defensa lejano", EfectoTipo.PotDefensa, false, PotDuracion, PotDefensaMag),
        [21] = new(21, "Potenciar movimiento cercano", EfectoTipo.PotMovimiento, false, PotDuracion, PotMovimientoMag),
        [22] = new(22, "Potenciar movimiento medio", EfectoTipo.PotMovimiento, false, PotDuracion, PotMovimientoMag),
        [23] = new(23, "Potenciar movimiento lejano", EfectoTipo.PotMovimiento, false, PotDuracion, PotMovimientoMag),
        // Invisibilidad (solo cartas propias). El rango lo valida el cliente; el
        // servidor solo aplica el efecto a la carta seleccionada. Duración
        // configurable con InvisibilidadDuracion.
        [24] = new(24, "Invisibilidad cercana", EfectoTipo.Invisibilidad, false, InvisibilidadDuracion, 0),
        [25] = new(25, "Invisibilidad media", EfectoTipo.Invisibilidad, false, InvisibilidadDuracion, 0),
        [26] = new(26, "Invisibilidad lejana", EfectoTipo.Invisibilidad, false, InvisibilidadDuracion, 0),
        // ── ACCIONES DE DISTORSIÓN (AccionesDistorsion.cs) ──────────────────
        // Rangos (los valida el cliente): cercano = adyacente, medio = radio 7,
        // lejano = cualquiera. Todas excluyen cuarteles como objetivo.
        // Clon: señuelo de una carta propia en la celda objetivo.
        [27] = new(27, "Clon cercano", EfectoTipo.Clon, true, ClonDuracion, 0),
        [28] = new(28, "Clon medio", EfectoTipo.Clon, true, ClonDuracion, 0),
        [29] = new(29, "Clon lejano", EfectoTipo.Clon, true, ClonDuracion, 0),
        // Muro: 1 celda en rango + 2 colindantes encadenadas (MuroNumCeldas).
        [30] = new(30, "Muro cercano", EfectoTipo.Muro, true, MuroDuracion, 0),
        [31] = new(31, "Muro medio", EfectoTipo.Muro, true, MuroDuracion, 0),
        [32] = new(32, "Muro lejano", EfectoTipo.Muro, true, MuroDuracion, 0),
        // Confusión: las cartas enemigas de la celda se descontrolan.
        [33] = new(33, "Confusión cercana", EfectoTipo.Confusion, true, ConfusionDuracion, 0),
        [34] = new(34, "Confusión media", EfectoTipo.Confusion, true, ConfusionDuracion, 0),
        [35] = new(35, "Confusión lejana", EfectoTipo.Confusion, true, ConfusionDuracion, 0),
        // Fractura: objetivos = [celda origen, celda destino (≤ FracturaDistanciaMax)].
        [36] = new(36, "Fractura cercana", EfectoTipo.Fractura, true, 0, 0),
        [37] = new(37, "Fractura media", EfectoTipo.Fractura, true, 0, 0),
        [38] = new(38, "Fractura lejana", EfectoTipo.Fractura, true, 0, 0),
        // Supervivencia: se ancla a UNA carta PROPIA (como la invisibilidad), así
        // que NO excluye cuarteles: el objetivo es la celda que contiene la
        // carta, y el rango lo valida el cliente. `DefensaReducida` se reutiliza
        // como MAGNITUD = % de fuerza que cuesta cada huida, igual que en
        // `EfectoHabilidad.supervivencia` del cliente.
        [39] = new(39, "Supervivencia cercana", EfectoTipo.Supervivencia, false, SupervivenciaDuracion, SupervivenciaPerdidaFuerzaPct),
        [40] = new(40, "Supervivencia media", EfectoTipo.Supervivencia, false, SupervivenciaDuracion, SupervivenciaPerdidaFuerzaPct),
        [41] = new(41, "Supervivencia lejana", EfectoTipo.Supervivencia, false, SupervivenciaDuracion, SupervivenciaPerdidaFuerzaPct),
    };

    // Magnitudes/duración configurables de las potenciaciones.
    public const int PotFuerzaMag = 5;
    public const int PotDefensaMag = 5;
    public const int PotMovimientoMag = 2;
    public const int PotDuracion = 3;

    // Duración configurable de la invisibilidad (espejo de kInvisibilidadDuracionTurnos).
    public const int InvisibilidadDuracion = 3;

    // Acciones de distorsión (espejo de las constantes k* de habilidad_model.dart).
    public const int ClonDuracion = 3;
    public const int MuroDuracion = 3;
    public const int MuroNumCeldas = 3;
    public const int ConfusionDuracion = 3;
    public const int FracturaDistanciaMax = 4;

    // ── SUPERVIVENCIA (espejo de kSupervivencia* de habilidad_model.dart) ────
    /// Turnos que la supervivencia permanece activa sobre la carta.
    public const int SupervivenciaDuracion = 8;
    /// % de FUERZA que la carta pierde para TODA LA PARTIDA por cada huida.
    public const int SupervivenciaPerdidaFuerzaPct = 25;

    // ── ENFRIAMIENTO DE HABILIDAD (recarga) ─────────────────────────────────
    /// Turnos MÍNIMOS entre dos lanzamientos de la habilidad de una MISMA carta,
    /// sea lo que diga su campo `EnfriamientoHabilidad` (el editor deja muchas a
    /// 0, lo que permitía relanzarla cada turno).
    ///
    /// Es la fuente autoritativa: la usan la validación de `CosteCanonicoAccion`
    /// y los planificadores del bot. Espejo del cliente:
    /// `kEnfriamientoHabilidadMinimo` de `habilidad_model.dart`.
    public const int EnfriamientoMinimo = 5;

    /// Enfriamiento REAL de una carta: el mayor entre el suyo y el mínimo global.
    public static int EnfriamientoEfectivo(int enfriamientoCarta)
        => enfriamientoCarta > EnfriamientoMinimo ? enfriamientoCarta : EnfriamientoMinimo;

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
    Dictionary<string, string> obeliscosPorJugador,
    Tablero? tableroPrevio = null,
    Dictionary<string, string>? terreno = null)
    {
        var t = CartaHelper.Copy(tableroIn);
        var e = CopiarEfectos(efectosCeldaIn);
        var log = new List<Dictionary<string, object?>>();

        var teles = new List<Dictionary<string, object?>>();
        var disparos = new List<Dictionary<string, object?>>();
        var venenos = new List<Dictionary<string, object?>>();
        var paralisis = new List<Dictionary<string, object?>>();
        var escudos = new List<Dictionary<string, object?>>();
        var potenciaciones = new List<Dictionary<string, object?>>();
        var invisibilidades = new List<Dictionary<string, object?>>();
        var supervivencias = new List<Dictionary<string, object?>>();
        var muros = new List<Dictionary<string, object?>>();
        var fracturas = new List<Dictionary<string, object?>>();
        var clones = new List<Dictionary<string, object?>>();
        var confusiones = new List<Dictionary<string, object?>>();

        foreach (var a in acciones)
        {
            // Las colocaciones de trampa (acción estática) NO se procesan aquí;
            // las gestiona Trampas.Procesar tras resolver los movimientos.
            if (M.Get(a, "esTrampa") is bool _et && _et) continue;
            var h = CatalogoHabilidades.Get(M.Int(M.Get(a, "habilidadId")));
            if (h == null) continue;
            switch (h.Efecto)
            {
                case EfectoTipo.Teletransporte: teles.Add(a); break;
                case EfectoTipo.Disparo: disparos.Add(a); break;
                case EfectoTipo.Veneno: venenos.Add(a); break;
                case EfectoTipo.Paralisis: paralisis.Add(a); break;
                case EfectoTipo.Escudo: escudos.Add(a); break;
                case EfectoTipo.PotFuerza:
                case EfectoTipo.PotDefensa:
                case EfectoTipo.PotMovimiento: potenciaciones.Add(a); break;
                case EfectoTipo.Invisibilidad: invisibilidades.Add(a); break;
                case EfectoTipo.Supervivencia: supervivencias.Add(a); break;
                case EfectoTipo.Muro: muros.Add(a); break;
                case EfectoTipo.Fractura: fracturas.Add(a); break;
                case EfectoTipo.Clon: clones.Add(a); break;
                case EfectoTipo.Confusion: confusiones.Add(a); break;
            }
        }

        // 1) Escudos y potenciaciones PRIMERO: los escudos establecen la
        //    protección de celda antes de resolver acciones ofensivas.
        foreach (var a in escudos) AplicarEscudo(a, t, e, log, obeliscosPorJugador);
        foreach (var a in potenciaciones) AplicarPotenciacion(a, t, e, log, obeliscosPorJugador);

        // 2) Celdas protegidas: coord → uid del jugador que las escuda.
        var protegidas = CeldasProtegidas(e);

        // 2b) MUROS: se levantan antes de cualquier desplazamiento, para que ni
        //     el teletransporte, ni la fractura, ni un clon aterricen en ellos.
        foreach (var a in muros) AccionesDistorsion.AplicarMuro(a, t, e, log, obeliscosPorJugador, protegidas);
        var celdasMuro = AccionesDistorsion.CeldasConMuro(e);

        // 3) Acciones ofensivas: se ignoran si la celda objetivo está protegida
        //    por OTRO jugador (≠ lanzador).
        foreach (var a in teles) AplicarTeletransporte(a, t, log, obeliscosPorJugador, protegidas, terreno, celdasMuro);
        // Fractura y clon van DESPUÉS del teletransporte y ANTES de los disparos:
        // así se puede desplazar un stack a una celda y dispararle el mismo turno.
        foreach (var a in fracturas) AccionesDistorsion.AplicarFractura(a, t, log, obeliscosPorJugador, protegidas, celdasMuro, terreno);
        foreach (var a in clones) AccionesDistorsion.AplicarClon(a, t, log, obeliscosPorJugador, protegidas, celdasMuro, terreno);
        foreach (var a in disparos) AplicarDisparo(a, t, log, obeliscosPorJugador, protegidas);
        foreach (var a in venenos) AplicarVeneno(a, t, e, log, obeliscosPorJugador, protegidas);
        foreach (var a in paralisis) AplicarParalisis(a, t, e, log, obeliscosPorJugador, protegidas);
        // Confusión: se ancla a las cartas enemigas presentes en la celda (no es
        // efecto de zona). Su movimiento aleatorio empieza el turno siguiente.
        foreach (var a in confusiones) AccionesDistorsion.AplicarConfusion(a, t, log, obeliscosPorJugador, protegidas);

        // Invisibilidad y SUPERVIVENCIA: se anclan a UNA carta propia (no son
        // efectos de celda, no se propagan). Van después de los desplazamientos
        // para que, si la carta se teletransporta o la desplaza una fractura el
        // mismo turno, se siga localizando por id en su celda de destino.
        //
        // La supervivencia va DESPUÉS de los disparos a propósito: no protege de
        // un disparo (eso mata la celda entera sin combate), solo de perder un
        // COMBATE, que se resuelve más tarde en Combate.Resolver.
        foreach (var a in invisibilidades) AplicarInvisibilidad(a, t, log);
        foreach (var a in supervivencias) AplicarSupervivencia(a, t, log);

        PropagarEfectosACeldas(t, e);

        // 4) Revertir cartas enemigas que se hayan MOVIDO a una celda protegida
        //    (vuelven a su posición del turno anterior).
        if (tableroPrevio != null)
            RevertirMovimientosAProtegidas(t, tableroPrevio, protegidas, log);

        return new ResultadoAplicarAcciones { Tablero = t, EfectosCelda = e, Log = log };
    }

    /// coord → uid del escudo activo (el primero encontrado por celda).
    internal static Dictionary<string, string> CeldasProtegidas(EfectosCelda e)
    {
        var m = new Dictionary<string, string>();
        foreach (var kv in e)
        {
            foreach (var ef in kv.Value)
            {
                if (M.Int(M.Get(ef, "turnosRestantes")) <= 0) continue;
                // SOLO el efecto "escudo" define una celda protegida. (Antes esta
                // condición estaba INVERTIDA: se saltaba el escudo — copiada por
                // error de PropagarEfectosACeldas, donde sí hay que ignorarlo para
                // no darlo como defensa a las cartas —, con lo que NINGUNA celda
                // escudada entraba en `protegidas`: no se bloqueaban las acciones
                // ofensivas ni se revertían los movimientos enemigos, así que el
                // escudo NO rechazaba la invasión.)
                var _tipoEf = M.Str(M.Get(ef, "tipo"));
                if (_tipoEf != "escudo") continue;
                {
                    m[kv.Key] = M.Str(M.Get(ef, "origenUid"));
                    break;
                }
            }
        }
        return m;
    }

    /// True si la celda objetivo está protegida por un jugador distinto de [uid].
    internal static bool BloqueadaPorEscudo(Dictionary<string, string> protegidas, string coord, string uid)
        => protegidas.TryGetValue(coord, out var s) && s != uid;

    /// Devuelve las cartas enemigas que se movieron a una celda protegida a su
    /// posición del turno anterior. No toca las cartas del propio escudo ni las
    /// que ya estaban en la celda.
    private static void RevertirMovimientosAProtegidas(
        Tablero t, Tablero previo, Dictionary<string, string> protegidas,
        List<Dictionary<string, object?>> log)
    {
        // Posiciones del turno ANTERIOR indexadas por (ownerUid|id) → LISTA de
        // celdas. Antes se indexaba SOLO por `id`, sin dueño y sin lista: con
        // cartas repetidas (el mismo id en varias copias o en varios jugadores)
        // el índice se machacaba y una carta enemiga que ENTRABA en la celda
        // escudada podía no revertirse (se quedaba y mataba a las cartas
        // escudadas). Con owner+id y lista, cada instancia se resuelve bien.
        var posAnterior = new Dictionary<string, List<string>>();
        foreach (var kv in previo)
            foreach (var c in kv.Value)
            {
                var key = CartaHelper.OwnerUid(c) + "|" + M.Str(M.Get(c, "id", "Id"));
                if (!posAnterior.TryGetValue(key, out var lst)) { lst = new(); posAnterior[key] = lst; }
                lst.Add(kv.Key);
            }

        foreach (var kv in protegidas)
        {
            var coord = kv.Key;
            var shielder = kv.Value;
            if (!t.TryGetValue(coord, out var cartas) || cartas.Count == 0) continue;

            var quedan = new List<Dictionary<string, object?>>();
            foreach (var c in cartas)
            {
                // Las cartas del DUEÑO del escudo nunca se revierten.
                if (CartaHelper.OwnerUid(c) == shielder) { quedan.Add(c); continue; }

                var key = CartaHelper.OwnerUid(c) + "|" + M.Str(M.Get(c, "id", "Id"));
                string? prev = null;
                if (posAnterior.TryGetValue(key, out var prevs) && prevs.Count > 0)
                {
                    // Preferir una celda anterior DISTINTA de la protegida: es la
                    // casilla desde la que la carta entró, y se la devolvemos.
                    var idx = prevs.FindIndex(p => p != coord);
                    if (idx >= 0) { prev = prevs[idx]; prevs.RemoveAt(idx); }
                    else { prevs.RemoveAt(0); } // ya estaba aquí al inicio del turno
                }

                if (prev != null)
                {
                    if (!t.TryGetValue(prev, out var destino)) { destino = new(); t[prev] = destino; }
                    destino.Add(c);
                    log.Add(new Dictionary<string, object?>
                    {
                        ["tipo"] = "escudoBloqueoMovimiento",
                        ["objetivo"] = coord,
                        ["origen"] = prev,
                        ["uid"] = CartaHelper.OwnerUid(c),
                    });
                }
                else
                {
                    quedan.Add(c); // ya estaba aquí o sin posición anterior
                }
            }
            t[coord] = quedan;
        }
    }

    /// Terreno: ¿puede una carta de tipo [tipo] aterrizar en [coord]?
    /// tipo 1 (terrestre) y 2 (aire) → land / amphibious.
    /// tipo 3 (marina) → sea / deepSea / amphibious.
    internal static bool TeleCanLand(string coord, int tipo, Dictionary<string, string> terreno)
    {
        var terr = terreno.TryGetValue(coord, out var v) ? v : "land";
        return tipo switch
        {
            1 or 2 => terr is "land" or "amphibious",
            3 => terr is "sea" or "deepSea" or "amphibious",
            _ => true,
        };
    }

    private static void AplicarTeletransporte(Dictionary<string, object?> a, Tablero t, List<Dictionary<string, object?>> log, Dictionary<string, string> obeliscos, Dictionary<string, string>? protegidas = null, Dictionary<string, string>? terreno = null, HashSet<string>? celdasMuro = null)
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
        // No se puede teletransportar a una celda protegida por un rival.
        if (protegidas != null && BloqueadaPorEscudo(protegidas, destino, uid))
        {
            log.Add(LogFallo(a, h, "Celda destino protegida por un escudo"));
            return;
        }
        // Ni a una celda con MURO (nadie puede estar en ella).
        if (celdasMuro != null && celdasMuro.Contains(destino))
        {
            log.Add(LogFallo(a, h, "Celda destino bloqueada por un muro"));
            return;
        }

        if (!t.TryGetValue(fromCoord, out var cartasOrigen) || cartasOrigen.Count == 0)
        {
            log.Add(LogFallo(a, h, "La carta origen ya no existe"));
            return;
        }

        // Preferir localizar la carta por ID: es robusto ante cambios de índice
        // (p. ej. si se coloca otra carta en la misma celda antes de resolver).
        var cartaId = M.Str(M.Get(a, "cartaOrigenId"));
        if (cartaId != "")
        {
            var byId = cartasOrigen.FindIndex(c => M.Str(M.Get(c, "id", "Id")) == cartaId);
            if (byId >= 0) fromIdx = byId;
        }
        if (fromIdx < 0 || fromIdx >= cartasOrigen.Count)
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
        // Las cartas estáticas (Condicion == 3) no pueden teletransportarse.
        if (M.Int(M.Get(carta, "Condicion")) == 3)
        {
            log.Add(LogFallo(a, h, "No se puede teletransportar una carta estática"));
            return;
        }
        // Terreno: la carta no puede aterrizar en un destino incompatible
        // (p. ej. una unidad de aire a una celda de agua).
        if (terreno != null)
        {
            int tipo = M.Int(M.Get(carta, "Tipo", "tipo"));
            if (tipo <= 0) tipo = 1;
            if (!TeleCanLand(destino, tipo, terreno))
            {
                log.Add(LogFallo(a, h, "Terreno incompatible para el destino"));
                return;
            }
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

    private static void AplicarDisparo(Dictionary<string, object?> a, Tablero t, List<Dictionary<string, object?>> log, Dictionary<string, string> obeliscos, Dictionary<string, string>? protegidas = null)
    {
        var h = CatalogoHabilidades.Get(M.Int(M.Get(a, "habilidadId")));
        if (h == null) return;
        var uid = M.Str(M.Get(a, "uid"));

        foreach (var obj in M.List(M.Get(a, "objetivos")).Select(M.Str))
        {
            if (h.ExcluyeCG && obeliscos.Values.Contains(obj)) continue;
            // Celda protegida por un rival: el disparo no la afecta.
            if (protegidas != null && BloqueadaPorEscudo(protegidas, obj, uid)) continue;

            var cartas = t.TryGetValue(obj, out var lst) ? lst : new();
            var destruidas = cartas.Select(c => (object?)new Dictionary<string, object?>
            {
                ["id"] = M.Get(c, "id", "Id") ?? "",
                ["Nombre"] = CartaHelper.Nombre(c),
                ["Fuerza"] = CartaHelper.Fuerza(c),
                ["Defensa"] = CartaHelper.DefensaBase(c),
                ["Imagen"] = CartaHelper.Imagen(c),
                ["ownerUid"] = CartaHelper.OwnerUid(c),
                ["ownerZone"] = CartaHelper.OwnerZone(c),
            }).ToList();
            t.Remove(obj);

            log.Add(new Dictionary<string, object?>
            {
                ["tipo"] = "disparo",
                ["habilidadId"] = h.Id,
                ["habilidadNombre"] = h.Nombre,
                ["uid"] = uid,
                ["zona"] = M.Str(M.Get(a, "zona")),
                ["origen"] = M.Str(M.Get(a, "origen")),
                ["objetivo"] = obj,
                ["cartasDestruidas"] = destruidas,
            });
        }
    }

    private static void AplicarVeneno(Dictionary<string, object?> a, Tablero t, EfectosCelda e, List<Dictionary<string, object?>> log, Dictionary<string, string> obeliscos, Dictionary<string, string>? protegidas = null)
    {
        var h = CatalogoHabilidades.Get(M.Int(M.Get(a, "habilidadId")));
        if (h == null) return;
        var uid = M.Str(M.Get(a, "uid"));

        foreach (var obj in M.List(M.Get(a, "objetivos")).Select(M.Str))
        {
            if (h.ExcluyeCG && obeliscos.Values.Contains(obj)) continue;
            if (protegidas != null && BloqueadaPorEscudo(protegidas, obj, uid)) continue;

            var efecto = new Dictionary<string, object?>
            {
                ["tipo"] = "veneno",
                ["turnosRestantes"] = h.DuracionTurnos,
                ["magnitud"] = h.DefensaReducida,
                ["origenUid"] = uid,
            };
            AgregarOFusionarEfectoCelda(e, obj, efecto);

            // El veneno solo afecta a cartas ENEMIGAS (no a las del lanzador),
            // si no, en un combate 1v1 restaría a ambos y no cambiaría nada.
            if (t.TryGetValue(obj, out var cartas))
                foreach (var c in cartas)
                    if (CartaHelper.OwnerUid(c) != uid)
                        AgregarOFusionarEfectoCarta(c, efecto);

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

    private static void AplicarParalisis(Dictionary<string, object?> a, Tablero t, EfectosCelda e, List<Dictionary<string, object?>> log, Dictionary<string, string> obeliscos, Dictionary<string, string>? protegidas = null)
    {
        var h = CatalogoHabilidades.Get(M.Int(M.Get(a, "habilidadId")));
        if (h == null) return;
        var uid = M.Str(M.Get(a, "uid"));

        foreach (var obj in M.List(M.Get(a, "objetivos")).Select(M.Str))
        {
            if (h.ExcluyeCG && obeliscos.Values.Contains(obj)) continue;
            if (protegidas != null && BloqueadaPorEscudo(protegidas, obj, uid)) continue;

            var efecto = new Dictionary<string, object?>
            {
                ["tipo"] = "paralisis",
                ["turnosRestantes"] = h.DuracionTurnos,
                ["magnitud"] = 0,
                ["origenUid"] = uid,
            };
            AgregarOFusionarEfectoCelda(e, obj, efecto);

            // La parálisis solo congela cartas ENEMIGAS, no las del lanzador.
            if (t.TryGetValue(obj, out var cartas))
                foreach (var c in cartas)
                    if (CartaHelper.OwnerUid(c) != uid)
                        AgregarOFusionarEfectoCarta(c, efecto);

            log.Add(new Dictionary<string, object?>
            {
                ["tipo"] = "paralisis",
                ["habilidadId"] = h.Id,
                ["habilidadNombre"] = h.Nombre,
                ["uid"] = uid,
                ["zona"] = M.Str(M.Get(a, "zona")),
                ["origen"] = M.Str(M.Get(a, "origen")),
                ["objetivo"] = obj,
                ["turnosRestantes"] = h.DuracionTurnos,
            });
        }
    }

    private static void AplicarEscudo(Dictionary<string, object?> a, Tablero t, EfectosCelda e, List<Dictionary<string, object?>> log, Dictionary<string, string> obeliscos)
    {
        var h = CatalogoHabilidades.Get(M.Int(M.Get(a, "habilidadId")));
        if (h == null) return;
        var uid = M.Str(M.Get(a, "uid"));

        foreach (var obj in M.List(M.Get(a, "objetivos")).Select(M.Str))
        {
            if (h.ExcluyeCG && obeliscos.Values.Contains(obj)) continue;

            var efecto = new Dictionary<string, object?>
            {
                ["tipo"] = "escudo",
                ["turnosRestantes"] = h.DuracionTurnos,
                ["magnitud"] = h.DefensaReducida,
                ["origenUid"] = uid,
            };
            AgregarOFusionarEfectoCelda(e, obj, efecto);

            // El escudo es SOLO protección de celda: no se aplica a las cartas
            // (no da defensa). CeldasProtegidas lo lee del efecto de celda.

            log.Add(new Dictionary<string, object?>
            {
                ["tipo"] = "escudo",
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

    private static string TipoEstadoPotenciacion(EfectoTipo t) => t switch
    {
        EfectoTipo.PotFuerza => "potFuerza",
        EfectoTipo.PotDefensa => "potDefensa",
        EfectoTipo.PotMovimiento => "potMovimiento",
        _ => "potFuerza",
    };

    private static void AplicarPotenciacion(Dictionary<string, object?> a, Tablero t, EfectosCelda e, List<Dictionary<string, object?>> log, Dictionary<string, string> obeliscos)
    {
        var h = CatalogoHabilidades.Get(M.Int(M.Get(a, "habilidadId")));
        if (h == null) return;
        var uid = M.Str(M.Get(a, "uid"));
        var tipo = TipoEstadoPotenciacion(h.Efecto);

        foreach (var obj in M.List(M.Get(a, "objetivos")).Select(M.Str))
        {
            if (h.ExcluyeCG && obeliscos.Values.Contains(obj)) continue;

            var efecto = new Dictionary<string, object?>
            {
                ["tipo"] = tipo,
                ["turnosRestantes"] = h.DuracionTurnos,
                ["magnitud"] = h.DefensaReducida,
                ["origenUid"] = uid,
            };
            AgregarOFusionarEfectoCelda(e, obj, efecto);

            // Solo potencia a las cartas del LANZADOR.
            if (t.TryGetValue(obj, out var cartas))
                foreach (var c in cartas)
                    if (CartaHelper.OwnerUid(c) == uid)
                        AgregarOFusionarEfectoCarta(c, efecto);

            log.Add(new Dictionary<string, object?>
            {
                ["tipo"] = tipo,
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

    /// Invisibilidad: ancla el efecto a UNA carta PROPIA identificada por
    /// cartaOrigenCoord + cartaOrigenId/cartaOrigenIndice (igual que el
    /// teletransporte). NO crea efecto de celda: solo la carta seleccionada
    /// queda invisible. Se rompe al expirar los turnos (TickEfectos), al entrar
    /// en combate o al morir (ver Combate.RevelarInvisibles).
    private static void AplicarInvisibilidad(Dictionary<string, object?> a, Tablero t, List<Dictionary<string, object?>> log)
    {
        var h = CatalogoHabilidades.Get(M.Int(M.Get(a, "habilidadId")));
        if (h == null) return;
        var uid = M.Str(M.Get(a, "uid"));

        var fromCoord = M.Get(a, "cartaOrigenCoord") as string;
        if (fromCoord == null || !t.TryGetValue(fromCoord, out var cartas) || cartas.Count == 0)
        {
            log.Add(LogFallo(a, h, "La carta objetivo ya no existe"));
            return;
        }

        // Preferir localizar por id (robusto ante cambios de índice).
        int idx = M.Get(a, "cartaOrigenIndice") is null ? -1 : M.Int(M.Get(a, "cartaOrigenIndice"));
        var cartaId = M.Str(M.Get(a, "cartaOrigenId"));
        if (cartaId != "")
        {
            var byId = cartas.FindIndex(c => M.Str(M.Get(c, "id", "Id")) == cartaId);
            if (byId >= 0) idx = byId;
        }
        if (idx < 0 || idx >= cartas.Count)
        {
            log.Add(LogFallo(a, h, "La carta objetivo ya no existe"));
            return;
        }

        var carta = cartas[idx];
        if (CartaHelper.OwnerUid(carta) != uid)
        {
            log.Add(LogFallo(a, h, "La carta objetivo no es propia"));
            return;
        }

        var efecto = new Dictionary<string, object?>
        {
            ["tipo"] = "invisibilidad",
            ["turnosRestantes"] = h.DuracionTurnos,
            ["magnitud"] = 0,
            ["origenUid"] = uid,
        };
        AgregarOFusionarEfectoCarta(carta, efecto);

        log.Add(new Dictionary<string, object?>
        {
            ["tipo"] = "invisibilidad",
            ["habilidadId"] = h.Id,
            ["habilidadNombre"] = h.Nombre,
            ["uid"] = uid,
            ["zona"] = M.Str(M.Get(a, "zona")),
            ["origen"] = M.Str(M.Get(a, "origen")),
            ["objetivo"] = fromCoord,
            ["turnosRestantes"] = h.DuracionTurnos,
            ["cartaNombre"] = CartaHelper.Nombre(carta),
        });
    }
    /// Supervivencia: ancla el efecto a UNA carta PROPIA identificada por
    /// cartaOrigenCoord + cartaOrigenId/cartaOrigenIndice, exactamente igual que
    /// la invisibilidad. NO crea efecto de celda: solo la carta seleccionada
    /// podrá huir en vez de morir.
    ///
    /// La huida en sí la resuelve `Combate.Resolver` (la carta perdedora con este
    /// efecto se recoloca en una colindante y paga el % de fuerza). Aquí solo se
    /// marca la carta. La caducidad la gobierna `TickEfectos` como cualquier otro
    /// efecto de N turnos.
    private static void AplicarSupervivencia(Dictionary<string, object?> a, Tablero t, List<Dictionary<string, object?>> log)
    {
        var h = CatalogoHabilidades.Get(M.Int(M.Get(a, "habilidadId")));
        if (h == null) return;
        var uid = M.Str(M.Get(a, "uid"));

        var fromCoord = M.Get(a, "cartaOrigenCoord") as string;
        if (fromCoord == null || !t.TryGetValue(fromCoord, out var cartas) || cartas.Count == 0)
        {
            log.Add(LogFallo(a, h, "La carta objetivo ya no existe"));
            return;
        }

        // Preferir localizar por id (robusto ante cambios de índice).
        int idx = M.Get(a, "cartaOrigenIndice") is null ? -1 : M.Int(M.Get(a, "cartaOrigenIndice"));
        var cartaId = M.Str(M.Get(a, "cartaOrigenId"));
        if (cartaId != "")
        {
            var byId = cartas.FindIndex(c => M.Str(M.Get(c, "id", "Id")) == cartaId);
            if (byId >= 0) idx = byId;
        }
        if (idx < 0 || idx >= cartas.Count)
        {
            log.Add(LogFallo(a, h, "La carta objetivo ya no existe"));
            return;
        }

        var carta = cartas[idx];
        if (CartaHelper.OwnerUid(carta) != uid)
        {
            log.Add(LogFallo(a, h, "La carta objetivo no es propia"));
            return;
        }
        // Un CLON es un señuelo: no tiene sentido protegerlo (desaparece solo).
        if (CartaHelper.EsClon(carta))
        {
            log.Add(LogFallo(a, h, "Un clon no puede recibir supervivencia"));
            return;
        }
        // Una carta ESTÁTICA no se mueve nunca, así que nunca podría huir.
        if (CartaHelper.EsEstatica(carta))
        {
            log.Add(LogFallo(a, h, "Una carta estática no puede huir"));
            return;
        }

        var efecto = new Dictionary<string, object?>
        {
            ["tipo"] = CartaHelper.TipoSupervivencia,
            ["turnosRestantes"] = h.DuracionTurnos,
            // Magnitud = % de fuerza que cuesta cada huida (para el badge).
            ["magnitud"] = h.DefensaReducida,
            ["origenUid"] = uid,
        };
        AgregarOFusionarEfectoCarta(carta, efecto);

        log.Add(new Dictionary<string, object?>
        {
            ["tipo"] = CartaHelper.TipoSupervivencia,
            ["habilidadId"] = h.Id,
            ["habilidadNombre"] = h.Nombre,
            ["uid"] = uid,
            ["zona"] = M.Str(M.Get(a, "zona")),
            ["origen"] = M.Str(M.Get(a, "origen")),
            ["objetivo"] = fromCoord,
            ["turnosRestantes"] = h.DuracionTurnos,
            ["magnitud"] = h.DefensaReducida,
            ["cartaNombre"] = CartaHelper.Nombre(carta),
        });
    }

    /// True si alguna carta del tablero arrastra una SUPERVIVENCIA activa.
    ///
    /// Lo consulta `WarZeroService` para decidir si hay que cargar el TERRENO del
    /// mapa en esta resolución: la huida respeta el terreno, así que sin el mapa
    /// una unidad de tierra podría acabar en el mar.
    public static bool HaySupervivencia(Tablero t)
    {
        foreach (var lst in t.Values)
            foreach (var c in lst)
                if (CartaHelper.TieneSupervivencia(c)) return true;
        return false;
    }
    private static readonly HashSet<string> _buffs = new()
    { "potFuerza", "potDefensa", "potMovimiento" };

    private static void PropagarEfectosACeldas(Tablero t, EfectosCelda e)
    {
        foreach (var kv in e)
        {
            if (!t.TryGetValue(kv.Key, out var cartas) || cartas.Count == 0) continue;
            foreach (var ef in kv.Value)
            {
                if (M.Int(M.Get(ef, "turnosRestantes")) <= 0) continue;
                // El escudo y el muro son solo efectos de celda: nunca se aplican
                // a las cartas.
                var tipoEf = M.Str(M.Get(ef, "tipo"));
                if (tipoEf == "escudo" || tipoEf == AccionesDistorsion.TipoMuro) continue;
                var origen = M.Str(M.Get(ef, "origenUid"));
                var esBuff = _buffs.Contains(M.Str(M.Get(ef, "tipo")));
                foreach (var c in cartas)
                {
                    var esPropia = CartaHelper.OwnerUid(c) == origen;
                    if (esBuff ? !esPropia : esPropia) continue;
                    AgregarOFusionarEfectoCarta(c, ef);
                }
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

        // Clones: pierden 1 turno de vida por resolución y desaparecen al
        // llegar a 0 (misma semántica que el resto de efectos de 3 turnos).
        AccionesDistorsion.TickClones(t);

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
    public List<Dictionary<string, object?>> NuevosRayos = new();
}

public static class Farmeo
{
    public static FarmeoResultado Calcular(
        Tablero tablero,
        Dictionary<string, string> obeliscosPorJugador,
        Dictionary<string, List<string>> continentes,
        List<string> islaCentral,
        List<Dictionary<string, object?>> rayosActuales,
        List<string> todasLasCeldas,
        int numRayos,
        Random rng,
        HashSet<string>? cuartelesDestruidos = null)
    {
        var propietarioDeObelisco = new Dictionary<string, string>();
        foreach (var kv in obeliscosPorJugador) propietarioDeObelisco[kv.Value] = kv.Key;

        // Coordenadas de TODOS los rayos activos (varias casillas simultáneas).
        var rayoCoords = rayosActuales
            .Select(r => M.Str(M.Get(r, "coord")))
            .Where(c => c != "").ToHashSet();

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

                // Un CLON es un señuelo: no extrae energía.
                if (CartaHelper.EsClon(carta)) continue;

                zonaMap[uid] = zona;
                if (!detalleMap.ContainsKey(uid))
                    detalleMap[uid] = new() { ["continenteEnemigo"] = 0, ["islaCentral"] = 0, ["rayo"] = 0 };

                // BUG QAS #1: una carta situada sobre CUALQUIER celda de
                // cuartel/obelisco NO farmea nada (ni continente enemigo, ni isla
                // central, ni rayo). El cuartel es una base, no una zona de
                // extracción. Antes solo se bloqueaba el continente del cuartel
                // PROPIO (esMiCuartel), y seguían apareciendo Zeros extraídos en
                // celdas de cuartel: p. ej. una carta parada sobre un cuartel
                // enemigo que no llegó a conquistarlo, o la especial comprada
                // sobre el cuartel propio cuando su celda entra en otro cómputo.
                var esCeldaCuartel = propietarioDeObelisco.ContainsKey(coord);
                if (esCeldaCuartel) continue;

                // Un cuartel DESTRUIDO es ruina: no farmea nada (issue #3), así
                // el conquistador que se queda encima no suma sin parar.
                if (cuartelesDestruidos != null && cuartelesDestruidos.Contains(coord)) continue;

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

                if (rayoCoords.Contains(coord))
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

        // ── Ciclo de vida de los rayos ──────────────────────────────────────
        // 1) Cada rayo activo baja 1 turno; se mantienen los que aún duran.
        var nuevosRayos = new List<Dictionary<string, object?>>();
        foreach (var r in rayosActuales)
        {
            var turnosRestantes = M.Int(M.Get(r, "turnosRestantes")) - 1;
            if (turnosRestantes > 0)
                nuevosRayos.Add(new()
                {
                    ["coord"] = M.Get(r, "coord"),
                    ["turnosRestantes"] = turnosRestantes,
                });
        }

        // 2) Rellenar hasta numRayos con celdas nuevas (libres: sin cartas, sin
        // obeliscos y sin ser ya un rayo). Cada rayo nuevo dura 3 turnos.
        var objetivo = numRayos < 1 ? 1 : numRayos;
        if (nuevosRayos.Count < objetivo)
        {
            var ocupadas = nuevosRayos
                .Select(r => M.Str(M.Get(r, "coord"))).Where(c => c != "").ToHashSet();
            var conCartas = tablero.Keys.ToHashSet();
            var obeliscos = obeliscosPorJugador.Values.ToHashSet();
            var disponibles = todasLasCeldas
                .Where(c => !conCartas.Contains(c) && !obeliscos.Contains(c)
                            && !ocupadas.Contains(c)
                            && (cuartelesDestruidos == null || !cuartelesDestruidos.Contains(c)))
                .ToList();
            while (nuevosRayos.Count < objetivo && disponibles.Count > 0)
            {
                var idx = rng.Next(disponibles.Count);
                var pick = disponibles[idx];
                disponibles.RemoveAt(idx); // sin repetir celda
                nuevosRayos.Add(new() { ["coord"] = pick, ["turnosRestantes"] = 3 });
            }
        }

        return new FarmeoResultado
        {
            EnergiesPorJugador = energies,
            FarmeoLog = farmeoLog,
            NuevosRayos = nuevosRayos,
        };
    }
}