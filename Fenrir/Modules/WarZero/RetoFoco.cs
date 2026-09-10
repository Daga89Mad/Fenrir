using System.Collections.Generic;
using System.Linq;

// ─────────────────────────────────────────────────────────────────────────────
// RetoFoco.cs  —  "todos los bots a por el jugador, esquivándose entre ellos"
//
// PROBLEMA: en el reto Resistencia demoníaca los 3 bots deben ir a por el humano
// y NO pelearse entre ellos. La noción de "enemigo" no vive en un sitio: la usan
// EstrategaStrategy, PlanificadorCaceria, PlanificadorDefensivo,
// PlanificadorFarmeo, ReglasEntrada, EvaluadorTablero y LookaheadDosPlies, y
// TODAS la derivan de lo mismo: `ownerUid != BotUid` sobre `estado.tablero` y las
// entradas de `estado.obeliscos`.
//
// SOLUCIÓN: en vez de tocar esos siete sitios, se filtra la FOTO del turno que
// recibe la estrategia. Se devuelve un BotContext idéntico salvo por tres cosas:
//
//   1. `tablero`   → sin las cartas de los OTROS bots. Para el planificador no
//                    son enemigos: no son presa de la cacería, no cuentan como
//                    amenaza al cuartel y no entran en el combate simulado.
//   2. `obeliscos` → sin los cuarteles de los otros bots. No hay más cuartel
//                    conquistable que el del jugador.
//   3. `Terreno`   → las celdas donde HAY otro bot (o donde está su cuartel) se
//                    marcan como INTRANSITABLES. Esto es lo que hace que se
//                    ESQUIVEN: `ReglasEntrada.CanLand/CanTraverse`,
//                    `Alcanzables`, `HayCamino`, `PasoSeguro`,
//                    `AvanceCohesionado` y `TerrenoUtil.Compatible` leen todas
//                    el terreno del contexto, así que ninguna unidad planifica
//                    aterrizar ni cruzar por encima de un compañero: rodean.
//
// O sea: los bots SE VEN (los tratan como obstáculo del mapa, igual que el mar
// para un terrestre) pero NO se atacan. El servidor sigue resolviendo el combate
// con las reglas de siempre: si aun así acaban en la misma celda —porque dos
// planifican el mismo destino a la vez— se combaten y pueden destruirse. Lo que
// se elimina es que se busquen y que se estorben por descuido.
//
// EXCEPCIONES al bloqueo (importantes, para no dispararse en el pie):
//   · Mi propio cuartel NUNCA se bloquea: es donde despliego.
//   · Una celda donde YA tengo cartas propias tampoco: sus unidades tienen que
//     poder reemitirse ahí; ese choque ya está en curso y lo resuelve el
//     servidor este mismo turno.
//
// Este filtro NO altera el estado real de la partida: se copia el diccionario de
// nivel superior y se sustituyen dos claves; las cartas se comparten por
// referencia (solo se leen). El `estado` original, que WarZeroBot usa para el
// cierre seguro, queda intacto.
//
// En una partida SIN campo `reto` (todas las normales, historia incluida)
// devuelve el mismo contexto que recibe: coste cero y cero riesgo.
// ─────────────────────────────────────────────────────────────────────────────
public static class RetoFoco
{
    /// Clave del doc de partida donde vive la config del reto.
    public const string CampoReto = "reto";

    /// Valor de terreno sintético para las celdas ocupadas por otro bot. No
    /// coincide con ningún terreno real (land / sea / deepSea / amphibious), así
    /// que CanLand y CanTraverse lo rechazan para cartas de tierra y de mar.
    public const string TerrenoOcupado = "bot_aliado";

    /// Devuelve el contexto que debe ver el bot. Si la partida no es un reto con
    /// foco (o este bot no es uno de los bots del reto), devuelve `ctx` tal cual.
    public static BotContext Aplicar(BotContext ctx)
    {
        var reto = M.Map(M.Get(ctx.Estado, CampoReto));
        if (reto.Count == 0) return ctx;

        // Jugador al que TODOS los bots deben atacar. Solo se escribe cuando el
        // reto es de modo "todos contra el jugador".
        var foco = M.Str(M.Get(reto, "focoUid"));
        if (foco == "" || foco == ctx.BotUid) return ctx;

        // La lente solo se aplica a los bots declarados en el reto. Si el humano
        // (o cualquier otro participante) llegara aquí, juega con la vista real.
        var bots = M.List(M.Get(reto, "botsUids")).Select(M.Str).Where(u => u != "").ToHashSet();
        if (bots.Count > 0 && !bots.Contains(ctx.BotUid)) return ctx;

        // ── Tablero: me quedo conmigo y con el objetivo; anoto dónde están los
        //    demás bots para convertirlos en obstáculo ──────────────────────────
        var tablero = new Dictionary<string, object?>();
        var celdasPropias = new HashSet<string>();
        var celdasOtrosBots = new HashSet<string>();

        foreach (var (coord, lista) in M.Map(M.Get(ctx.Estado, "tablero")))
        {
            List<object?>? visibles = null;
            foreach (var raw in M.List(lista))
            {
                var owner = M.Str(M.Get(M.Map(raw), "ownerUid"));
                if (owner == ctx.BotUid)
                {
                    celdasPropias.Add(coord);
                    visibles ??= new List<object?>();
                    visibles.Add(raw);          // misma referencia: solo se lee
                }
                else if (owner == foco)
                {
                    visibles ??= new List<object?>();
                    visibles.Add(raw);
                }
                else if (owner != "")
                {
                    celdasOtrosBots.Add(coord); // compañero de reto: obstáculo
                }
            }
            if (visibles != null) tablero[coord] = visibles;
        }

        // ── Cuarteles: el mío y el del objetivo. Los de los demás bots pasan a
        //    ser obstáculo, para que nadie los conquiste "de paso" ─────────────
        var obeliscos = new Dictionary<string, object?>();
        string miCuartel = ctx.Cuartel;
        foreach (var (uid, coordObj) in M.Map(M.Get(ctx.Estado, "obeliscos")))
        {
            var coord = M.Str(coordObj);
            if (uid == ctx.BotUid)
            {
                obeliscos[uid] = coordObj;
                if (miCuartel == "") miCuartel = coord;
            }
            else if (uid == foco) obeliscos[uid] = coordObj;
            else if (coord != "") celdasOtrosBots.Add(coord);
        }

        // ── Terreno con los compañeros marcados como intransitables ──────────
        // Nunca mi cuartel (despliego ahí) ni una celda en la que ya tengo
        // cartas (tienen que poder reemitirse donde están).
        var terreno = ctx.Terreno;
        var bloqueadas = celdasOtrosBots
            .Where(c => c != miCuartel && !celdasPropias.Contains(c))
            .ToList();

        if (bloqueadas.Count > 0)
        {
            terreno = new Dictionary<string, string>(ctx.Terreno);
            foreach (var c in bloqueadas) terreno[c] = TerrenoOcupado;
            System.Console.WriteLine(
                $"[WZ][reto {ctx.BotUid}] foco en {foco}; esquivo {bloqueadas.Count} celda(s) " +
                $"de compañeros: {string.Join(", ", bloqueadas.OrderBy(c => c))}");
        }

        var estado = new Dictionary<string, object?>(ctx.Estado)
        {
            ["tablero"] = tablero,
            ["obeliscos"] = obeliscos,
        };

        return new BotContext
        {
            Estado = estado,
            BotUid = ctx.BotUid,
            Turno = ctx.Turno,
            Cuartel = ctx.Cuartel,
            Energia = ctx.Energia,
            Mano = ctx.Mano,
            CatalogoMano = ctx.CatalogoMano,
            Zona = ctx.Zona,
            Terreno = terreno,
            Filas = ctx.Filas,
            Columnas = ctx.Columnas,
            IslaCentral = ctx.IslaCentral,
            Continentes = ctx.Continentes,
            Rayos = ctx.Rayos,
            RayosTurnos = ctx.RayosTurnos,
            EjercitoId = ctx.EjercitoId,
            Evoluciones = ctx.Evoluciones,
            GeneralesDisponibles = ctx.GeneralesDisponibles,
        };
    }
}