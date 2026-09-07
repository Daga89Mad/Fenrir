using System;
using System.Collections.Generic;
using System.Linq;

using Tablero = System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<System.Collections.Generic.Dictionary<string, object?>>>;

// ─────────────────────────────────────────────────────────────────────────────
// PlanificadorCaceria.cs  —  MODO CACERÍA (planificador)  v4
//
// Genera UN plan candidato de CAZA: concentrar la fuerza del bot sobre una presa
// vulnerable para rematarla. Es un candidato MÁS que la softmax evalúa; el
// lookahead lo elige solo cuando la caza de verdad sale.
//
// Cambios v4 (partida de estudio 9UCNdoqXJ2mMaur6ssuF, turno 11):
//   · FUERA EL PASO GREEDY SOBRE LA PRESA. v3 movía CADA unidad con
//     TerrenoUtil.PasoHaciaTerreno hacia la presa, y la primera que estaba a
//     tiro aterrizaba SOLA encima: una Manta escarlata (10/3) entró en el
//     cuartel humano F1 (vacío), el humano desplegó una Llama del Sheol y la
//     Manta murió regalando energía y PC. Ahora el movimiento es
//     ReglasEntrada.AvanceCohesionado: el subgrupo que ALCANZA la presa este
//     turno entra SOLO si gana junto (con el bono del cuartel, la evolución
//     rival pagable y el refuerzo que el dueño puede desplegar); si no, nadie
//     entra y todos se acercan en bola a celdas seguras.
//   · La DEFENSA de la presa se estima con la misma regla exacta que usará
//     el asalto (poder pesimista + 40 + refuerzo), no solo con F+D actual.
//   · FUERA LA GUARNICIÓN "TODO LO QUE ESTÉ EN EL CUARTEL". En TrTl el General
//     Izanagi (80 de poder) pasó 6 turnos en casa porque los planes dejaban
//     al cuartel lo que ya estaba dentro. Ahora se queda como mucho UNA
//     unidad de guarnición (la de mejor perfil defensivo: lenta y barata) y
//     solo si hay enemigos en juego; el resto caza.
//   · Los navales SÍ pueden cazar cuarteles (los cuarteles son anfibios en los
//     mapas clásicos: la Manta entró en F1 y un Megalodón humano conquistó
//     A10 en wlDM); se exige camino REAL por terreno (HayCamino), no el tipo.
//   · El coste de las cartas de ACCIÓN nunca se suma a EnergiaGastada (el
//     servidor las cobra en la resolución; sumarlas era un doble cobro).
//     Este planificador no lanza acciones, pero deja el campo a 0 explícito.
//
// Cambios v3: sin umbral absoluto de energía; presa alcanzable en HORIZONTE
// turnos; atacabilidad por poder (F+D) con margen; la presa más barata gana.
// Cambios v2: fuera el candado DominaCentro; despliegue básico hacia la presa.
//
// PRESA: la mejor pieza enemiga ATACABLE (poder propio que llega en el horizonte
// > su defensa con margen), por valor: un CUARTEL enemigo (conquistarlo elimina
// a un rival) por encima de un GENERAL aislado. Si ninguna es atacable, no hay
// plan.
// ─────────────────────────────────────────────────────────────────────────────
public static class PlanificadorCaceria
{
    private const int HORIZONTE_CUARTEL = 3;         // turnos de aproximación a un cuartel
    private const int HORIZONTE_GENERAL = 1;         // un general se mueve: solo si llego ya
    private const double MARGEN_PODER = 1.15;        // poder propio mínimo / poder defensor
    private const int MIN_UNIDADES = 2;              // nunca una carta suelta
    private const int VALOR_CUARTEL = 10000;         // conquistar > matar general
    private const int VALOR_GENERAL = 1000;
    private const int COND_GENERAL = 5;
    private const int ALCANCE = 1;
    private const int MAX_DESPLIEGUE_CAZA = 2;       // refuerzos nuevos hacia la presa
    private const int MAX_GUARNICION = 1;            // v4: como mucho una en casa

    /// Genera el plan de caza, o null si no hay presa atacable.
    public static BotMove? Generar(BotContext ctx)
    {
        var k = ReglasEntrada.Crear(ctx);
        var tablero = ReglasEntrada.TableroDesde(ctx.Estado);
        string botUid = ctx.BotUid;
        int filas = ctx.Filas, columnas = ctx.Columnas;
        string miCuartel = ctx.Cuartel;
        bool hayEnemigos = k.EnemigosPorCelda.Count > 0;

        // ── Mis unidades ──
        var propias = new List<(string coord, Dictionary<string, object?> card)>();
        foreach (var (coord, cartas) in tablero)
            foreach (var c in cartas)
                if (EsMio(c, botUid)) propias.Add((coord, c));

        // ── DESPLIEGUE (v2): hasta MAX_DESPLIEGUE_CAZA unidades potentes de la
        //    mano. Caen en el cuartel y avanzan hacia la presa este mismo turno.
        var mano = new List<string>(ctx.Mano);
        int energia = ctx.Energia, gastado = 0;
        var desplegadas = new List<Dictionary<string, object?>>();
        if (miCuartel != "")
        {
            var candidatas = mano
                .Where(id => ctx.CatalogoMano.TryGetValue(id, out var b)
                             && !AccionesTacticas.EsCartaAccion(b) && !EsEstatica(b))
                .OrderByDescending(id => { var c = ctx.CatalogoMano[id]; return Fuerza(c) + Defensa(c); })
                .ToList();
            foreach (var id in candidatas)
            {
                if (desplegadas.Count >= MAX_DESPLIEGUE_CAZA) break;
                var baseCard = ctx.CatalogoMano[id];
                int coste = M.Int(M.Get(baseCard, "Coste", "coste"));
                if (coste > energia) continue;
                if (!ReglasEntrada.CanLand(miCuartel, Tipo(baseCard), ctx.Terreno)) continue;
                var nu = NuevaUnidad(baseCard, id, botUid, ctx.Zona);
                desplegadas.Add(nu);
                energia -= coste; gastado += coste;
                mano.Remove(id);
            }
        }

        // ── GUARNICIÓN (v4): como mucho UNA unidad, la de mejor perfil defensivo
        //    (lenta y barata; nunca un general), y solo si hay enemigos en juego.
        var guarnicion = new List<Dictionary<string, object?>>();
        if (hayEnemigos && miCuartel != "")
        {
            var enCasa = propias.Where(u => u.coord == miCuartel && Mov(u.card) > 0)
                .OrderByDescending(u => PerfilGuarnicion(u.card))
                .Take(MAX_GUARNICION)
                .Select(u => u.card)
                .ToList();
            guarnicion.AddRange(enCasa);
        }

        // Unidades MÓVILES para la caza (estáticas y guarnición se quedan).
        var moviles = new List<(string coord, Dictionary<string, object?> card)>();
        foreach (var u in propias)
        {
            if (Mov(u.card) <= 0 || guarnicion.Contains(u.card)) continue;
            moviles.Add(u);
        }
        foreach (var nu in desplegadas) moviles.Add((miCuartel, nu));
        if (moviles.Count == 0) return null;

        // ── PRESAS: cuarteles enemigos + generales enemigos, con la defensa que
        //    hay que superar (misma regla que el asalto: poder pesimista de la
        //    guarnición + 40 + refuerzo desplegable) y horizonte de aproximación.
        var presas = new List<(string coord, int valor, int defensa, int horizonte)>();
        foreach (var q in k.CuartelesEnemigos)
        {
            string dueno = k.CuartelOwner.GetValueOrDefault(q, "");
            int guarn = k.EnemigosPorCelda.TryGetValue(q, out var gc) ? ReglasEntrada.PoderPesimista(k, gc) : 0;
            var (rf, rd) = ReglasEntrada.RefuerzoCuartel(k, dueno, ReglasEntrada.RefuerzoMaxEntrada);
            presas.Add((q, VALOR_CUARTEL, guarn + rf + rd + ReglasEntrada.BonoCuartel, HORIZONTE_CUARTEL));
        }
        foreach (var (coord, cartas) in k.EnemigosPorCelda)
        {
            if (k.CuartelesEnemigos.Contains(coord)) continue;
            var general = cartas.FirstOrDefault(c => M.Int(M.Get(c, "Condicion", "condicion")) == COND_GENERAL);
            if (general == null) continue;
            presas.Add((coord, VALOR_GENERAL + Fuerza(general), ReglasEntrada.PoderPesimista(k, cartas), HORIZONTE_GENERAL));
        }
        if (presas.Count == 0) return null;

        // ── Elegir la mejor presa ATACABLE: poder que llega en `horizonte`
        //    turnos (con camino real por terreno) supera con margen la defensa.
        string? mejorPresa = null; int mejorValor = int.MinValue, mejorMargen = int.MinValue;
        int mejorPoder = 0, mejorNecesario = 0;
        foreach (var (coord, valor, defensa, horizonte) in presas)
        {
            int alcanzable = 0, unidades = 0;
            foreach (var (uc, card) in moviles)
            {
                int mov = Mov(card);
                if (mov <= 0) continue;
                if (ReglasEntrada.Manhattan(uc, coord) > mov * horizonte + ALCANCE) continue;
                if (!ReglasEntrada.HayCamino(uc, coord, Tipo(card), ctx.Terreno, filas, columnas)) continue;
                alcanzable += Fuerza(card) + Defensa(card); unidades++;
            }
            int necesario = (int)Math.Ceiling(defensa * MARGEN_PODER);
            if (unidades < MIN_UNIDADES || alcanzable <= necesario) continue;   // no gano: no es presa
            int margen = alcanzable - defensa;
            if (valor > mejorValor || (valor == mejorValor && margen > mejorMargen))
            { mejorValor = valor; mejorMargen = margen; mejorPresa = coord; mejorPoder = alcanzable; mejorNecesario = necesario; }
        }
        if (mejorPresa == null) return null;
        Console.WriteLine($"[WZ][bot {botUid}] CACERÍA propuesta: presa {mejorPresa} (poder alcanzable {mejorPoder} vs necesario {mejorNecesario}, {desplegadas.Count} refuerzos)");

        // ── PLAN: guarnición y estáticas se quedan; las móviles avanzan EN BOLA.
        var celdas = new Tablero();
        void Add(string coord, Dictionary<string, object?> c)
        {
            if (!celdas.TryGetValue(coord, out var lst)) { lst = new(); celdas[coord] = lst; }
            lst.Add(c);
        }
        var ocupacion = new Dictionary<string, (int f, int d)>();
        void Ocupa(string coord, Dictionary<string, object?> c)
        {
            var s = ocupacion.TryGetValue(coord, out var v) ? v : (0, 0);
            ocupacion[coord] = (s.Item1 + Fuerza(c), s.Item2 + Defensa(c));
        }
        foreach (var u in propias)
        {
            if (moviles.Any(m => ReferenceEquals(m.card, u.card))) continue;
            Add(u.coord, u.card);   // estática o guarnición: se queda
            Ocupa(u.coord, u.card);
        }

        var destinos = ReglasEntrada.AvanceCohesionado(k, moviles, mejorPresa, ocupacion);
        int entran = 0;
        for (int i = 0; i < moviles.Count; i++)
        {
            Add(destinos[i], moviles[i].card);
            if (destinos[i] == mejorPresa) entran++;
        }
        Console.WriteLine($"[WZ][bot {botUid}] CACERÍA: {entran} entran en {mejorPresa}, {moviles.Count - entran} se acercan en bola");

        return new BotMove
        {
            Celdas = celdas,
            Acciones = new List<Dictionary<string, object?>>(),
            ManoResultante = mano,
            EnergiaGastada = gastado,   // solo despliegues: las acciones las cobra el servidor
            EnergiaAcciones = 0,
        };
    }

    /// Perfil defensivo de una carta como guarnición: cuanto más ALTO, mejor se
    /// queda en casa (poder útil, pero lenta y sin valor ofensivo especial).
    private static int PerfilGuarnicion(Dictionary<string, object?> c)
    {
        int poder = Fuerza(c) + Defensa(c);
        int mov = Mov(c);
        bool general = M.Int(M.Get(c, "Condicion", "condicion")) == COND_GENERAL;
        return 2 * poder - 15 * mov - (general ? 200 : 0);   // misma fórmula que EstrategaStrategy
    }

    private static Dictionary<string, object?> NuevaUnidad(
        Dictionary<string, object?> baseCard, string id, string uid, string zona)
        => new(baseCard)
        {
            ["id"] = id,
            ["ownerUid"] = uid,
            ["ownerZone"] = zona,
            ["instanceId"] = Guid.NewGuid().ToString("N"),
        };

    private static bool EsEstatica(Dictionary<string, object?> baseCard)
        => M.Int(M.Get(baseCard, "Condicion", "condicion")) == 3;

    private static bool EsMio(Dictionary<string, object?> c, string botUid) =>
        M.Str(M.Get(c, "ownerUid")) == botUid;

    private static int Fuerza(Dictionary<string, object?> c) => ReglasEntrada.Fuerza(c);
    private static int Defensa(Dictionary<string, object?> c) => ReglasEntrada.Defensa(c);
    private static int Mov(Dictionary<string, object?> c) => ReglasEntrada.Mov(c);
    private static int Tipo(Dictionary<string, object?> c) => ReglasEntrada.Tipo(c);
}