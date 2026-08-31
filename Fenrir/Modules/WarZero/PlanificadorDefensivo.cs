using System;
using System.Collections.Generic;
using System.Linq;

using Tablero = System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<System.Collections.Generic.Dictionary<string, object?>>>;

// ─────────────────────────────────────────────────────────────────────────────
// PlanificadorDefensivo.cs  —  MODO DEFENSA (planificador)  v6
//
// Candidato de la softmax para cuando el CUARTEL está amenazado. Devuelve null si
// no hay amenaza. El lookahead elige el plan solo si defender supera a farmear/atacar.
// (La softmax v8 ya filtra el null: devolver null aquí es seguro y NO congela nada.)
//
// ── Regla de combate (WarZeroLogic.Combate) ──────────────────────────────────
//   · Cuartel SIN defensor: cae si Σ Fuerza atacante > 40 (DefensaObelisco).
//   · Cuartel CON defensor: poder neto, +40 al defensor. Para tomarlo, el atacante
//     necesita en la práctica Fuerza > Fuerza_def + Defensa_def + 40.
//
// ── AMENAZA consciente de movimiento y evolución ─────────────────────────────
//   Amenaza = Σ Fuerza de stacks enemigos que PUEDEN LLEGAR (Manhattan ≤ mov+1) +
//   potencial de evolución del rival según su energía (FACTOR_EVO_ENEMIGO).
//   v6: el potencial de evolución se ACOTA a MAX_EVO_AMENAZA cartas por stack —
//   antes se evolucionaba TODO lo evolucionable con la energía pública y la
//   amenaza estimada salía apocalíptica, distorsionando el disparador.
//
// ── DISPARADOR v6 (partidas de estudio XnIl/GG6) ─────────────────────────────
//   Antes: hay amenaza si amenaza > 40 + MI MATERIAL TOTAL. Con el ejército
//   desplegado LEJOS (farmear el centro, asediar…) ese material contaba como si
//   defendiera el cuartel, y el modo defensa no saltaba aunque la base estuviera
//   sola frente a un asalto. Ahora la vara es el MATERIAL DEFENDIBLE: solo las
//   unidades que YA están en el cuartel o que PUEDEN llegar a él este turno
//   (Manhattan ≤ su movimiento). Lo que no llega a casa no defiende.
//
// ── DEFENSA ACTIVA (commit 3) ────────────────────────────────────────────────
//   1) HABILIDADES combinadas contra el stack principal (AccionesTacticas):
//      PARÁLISIS (congela 3 turnos) + VENENO (−3 def) + DISPARO (daño), lo que quepa
//      en energía. Fuentes: cartas de acción de la mano y unidades en el cuartel.
//   2) REFUERZO del cuartel con tus propios recursos: EVOLUCIONA la guarnición que se
//      queda (si mejora y hay energía) y DESPLIEGA defensores de la mano, hasta que
//      40 + material de guarnición cubra la amenaza (o agotes recursos/topes). Esto
//      es "tu 40 + defensa contando evolución/energía/mano". Topes para NO apilar
//      todo en el cuartel (un disparo de área barre lo apilado).
//   3) MOVIMIENTO que no se encierra: interceptores cubriendo las 2-3 CELDAS DE
//      APROXIMACIÓN críticas (las que el rival puede pisar para atacar el cuartel),
//      separando amenazas de MAR y de TIERRA por terreno. Guarnición se queda.
//
// ── Pendiente ────────────────────────────────────────────────────────────────
//   Afinado fino de pathfinding (rodear obstáculos) queda como límite conocido:
//   el paso es greedy.
// ─────────────────────────────────────────────────────────────────────────────
public static class PlanificadorDefensivo
{
    private const int BONO_CUARTEL = 40;            // = WarZeroLogic.Combate.DefensaObelisco
    private const int ALCANCE = 1;                  // alcance de combate sobre el movimiento
    private const double FACTOR_EVO_ENEMIGO = 1.6;  // subida estimada de fuerza si el rival evoluciona (tunable)
    private const int MAX_EVO_AMENAZA = 2;          // v6: cartas evolucionadas como mucho por stack enemigo
    private const int MAX_CELDAS_APROXIMACION = 3;  // celdas de acceso a cubrir con interceptores
    private const int RADIO_APROXIMACION = 2;       // distancia máx al cuartel de una celda de acceso
    private const int MAX_DESPLIEGUE_DEFENSA = 2;   // defensores nuevos en el cuartel (anti-apilado)
    private const int MAX_EVO_DEFENSA = 2;          // evoluciones de guarnición por turno

    public static BotMove? Generar(BotContext ctx)
    {
        string miCuartel = ctx.Cuartel;
        if (miCuartel == "") return null;
        int filas = ctx.Filas, columnas = ctx.Columnas;
        string botUid = ctx.BotUid;
        var terreno = ctx.Terreno;
        var tablero = TableroDesde(ctx.Estado);

        var stats = M.Map(M.Get(ctx.Estado, "statsPartida"));
        int EnergiaDe(string uid) => M.Int(M.Get(M.Map(M.Get(stats, uid)), "energies"));

        var obeliscos = M.Map(M.Get(ctx.Estado, "obeliscos"));
        var eliminados = M.List(M.Get(ctx.Estado, "jugadoresEliminados")).Select(M.Str).ToHashSet();
        var cuartelesEnemigos = new HashSet<string>();
        foreach (var (uid, cObj) in obeliscos)
        {
            var c = M.Str(cObj);
            if (c != "" && uid != botUid && !eliminados.Contains(uid)) cuartelesEnemigos.Add(c);
        }

        var enemyByCoord = new Dictionary<string, List<Dictionary<string, object?>>>();
        foreach (var (coord, cartas) in tablero)
        {
            var ene = cartas.Where(c => EsEnemigo(c, botUid)).ToList();
            if (ene.Count > 0) enemyByCoord[coord] = ene;
        }

        // ── Mis unidades + MATERIAL DEFENDIBLE (v6) ──
        // Solo cuenta como defensa lo que puede LLEGAR al cuartel este turno.
        var misUnidades = new List<(string coord, Dictionary<string, object?> card, int f, int d, int mov, int tipo)>();
        int materialDefendible = 0;
        foreach (var (coord, cartas) in tablero)
            foreach (var c in cartas)
            {
                if (!EsMio(c, botUid)) continue;
                int f = Fuerza(c), d = Defensa(c);
                int mov = Mov(c);
                misUnidades.Add((coord, c, f, d, mov, Tipo(c)));
                if (coord == miCuartel || Manhattan(coord, miCuartel, filas, columnas) <= mov)
                    materialDefendible += f + d;
            }

        // ── Amenaza (mov + evolución acotada). Por celda: fuerza, mov, tipo del más fuerte. ──
        var energiaGastableEnemigo = new Dictionary<string, int>();
        var stacksAmenaza = new List<(string coord, int fuerza, int mov, int tipoRep)>();
        double amenaza = 0.0;
        string? amenazaPrincipal = null; double fuerzaPrincipal = 0.0;

        foreach (var (coord, cartas) in tablero)
        {
            int stackFuerza = 0, movMax = 0, tipoRep = 1, fMax = -1; string dueno = "";
            var evolucionables = new List<(int baseF, int costeEvo)>();
            foreach (var c in cartas)
            {
                if (!EsEnemigo(c, botUid)) continue;
                int f = Fuerza(c);
                stackFuerza += f; movMax = Math.Max(movMax, Mov(c));
                if (f > fMax) { fMax = f; tipoRep = Tipo(c); }   // tipo del más fuerte del stack
                if (dueno == "") dueno = M.Str(M.Get(c, "ownerUid"));
                string idEvo = M.Str(M.Get(c, "IdEvolucion", "idEvolucion"));
                int costeEvo = M.Int(M.Get(c, "Evolucion", "evolucion"));
                if (idEvo != "" && costeEvo > 0) evolucionables.Add((f, costeEvo));
            }
            if (stackFuerza <= 0) continue;
            if (Manhattan(coord, miCuartel, filas, columnas) > movMax + ALCANCE) continue;

            double fuerzaStack = stackFuerza;
            if (!energiaGastableEnemigo.TryGetValue(dueno, out int presupuesto))
                presupuesto = energiaGastableEnemigo[dueno] = EnergiaDe(dueno);
            int evosStack = 0;   // v6: tope de evoluciones estimadas por stack
            foreach (var (baseF, costeEvo) in evolucionables.OrderBy(e => e.costeEvo))
            {
                if (evosStack >= MAX_EVO_AMENAZA) break;
                if (presupuesto < costeEvo) continue;
                presupuesto -= costeEvo;
                fuerzaStack += baseF * (FACTOR_EVO_ENEMIGO - 1.0);
                evosStack++;
            }
            energiaGastableEnemigo[dueno] = presupuesto;

            amenaza += fuerzaStack;
            stacksAmenaza.Add((coord, stackFuerza, movMax, tipoRep));
            if (fuerzaStack > fuerzaPrincipal) { fuerzaPrincipal = fuerzaStack; amenazaPrincipal = coord; }
        }

        // Disparador v6: la amenaza se compara contra el 40 del cuartel MÁS el
        // material que de verdad puede defenderlo este turno.
        if (amenaza <= BONO_CUARTEL + materialDefendible || amenazaPrincipal == null) return null;

        // ── Estado mutable del plan ──
        var celdas = new Tablero();
        void Add(string coord, Dictionary<string, object?> c)
        {
            if (!celdas.TryGetValue(coord, out var lst)) { lst = new(); celdas[coord] = lst; }
            lst.Add(c);
        }
        var acciones = new List<Dictionary<string, object?>>();
        int energia = ctx.Energia, gastado = 0;
        var mano = new List<string>(ctx.Mano);

        // ── 1) HABILIDADES combinadas contra el stack principal ──
        var fuentes = new List<AccionesTacticas.Fuente>();
        foreach (var id in ctx.Mano)
            if (ctx.CatalogoMano.TryGetValue(id, out var baseCard) && AccionesTacticas.EsCartaAccion(baseCard))
                fuentes.Add(new AccionesTacticas.Fuente(
                    miCuartel,
                    M.Int(M.Get(baseCard, "IdHabilidad", "idHabilidad")),
                    M.Int(M.Get(baseCard, "Coste", "coste")),
                    id));
        foreach (var u in misUnidades)
            if (u.coord == miCuartel)   // unidades que se quedan → pueden usar habilidad
            {
                int habId = M.Int(M.Get(u.card, "IdHabilidad", "idHabilidad"));
                if (habId <= 0 || EnEnfriamiento(u.card, ctx.Turno)) continue;
                fuentes.Add(new AccionesTacticas.Fuente(
                    miCuartel, habId, M.Int(M.Get(u.card, "CosteHabilidad", "costeHabilidad")), null));
            }

        foreach (var (accion, coste, cartaId) in AccionesTacticas.ElegirAccionesDefensivas(
                     amenazaPrincipal, fuentes, energia, cuartelesEnemigos,
                     botUid, ctx.Zona, ctx.Turno, filas, columnas))
        {
            acciones.Add(accion);
            energia -= coste; gastado += coste;
            if (cartaId != null) mano.Remove(cartaId);
        }

        // ── 2) REFUERZO del cuartel (evolución de guarnición + despliegue) ──
        // Material de guarnición = unidades que YA están en el cuartel (se quedan).
        // Se evoluciona/despliega hasta cubrir la amenaza o agotar recursos/topes.
        var enCuartel = misUnidades.Where(u => u.coord == miCuartel).ToList();
        int materialGuarnicion = enCuartel.Sum(u => u.f + u.d);

        // 2a) EVOLUCIÓN de la guarnición que se queda (reemplaza la carta si mejora).
        var cartaGuarnicionFinal = new List<(int f, int d, Dictionary<string, object?> card)>();
        int evos = 0;
        foreach (var u in enCuartel)
        {
            var card = u.card; int f = u.f, d = u.d;
            if (evos < MAX_EVO_DEFENSA)
            {
                string idEvo = M.Str(M.Get(u.card, "IdEvolucion", "idEvolucion"));
                int costeEvo = M.Int(M.Get(u.card, "Evolucion", "evolucion"));
                if (idEvo != "" && costeEvo > 0 && energia >= costeEvo
                    && ctx.Evoluciones.TryGetValue(idEvo, out var evoCard)
                    && CanLand(miCuartel, Tipo(evoCard), terreno)
                    && (Fuerza(evoCard) + Defensa(evoCard)) > (f + d))
                {
                    string zonaU = M.Str(M.Get(u.card, "ownerZone")); if (zonaU == "") zonaU = ctx.Zona;
                    card = NuevaUnidad(evoCard, idEvo, botUid, zonaU);
                    energia -= costeEvo; gastado += costeEvo; evos++;
                    materialGuarnicion += (Fuerza(card) + Defensa(card)) - (f + d);
                    f = Fuerza(card); d = Defensa(card);
                }
            }
            cartaGuarnicionFinal.Add((f, d, card));
        }

        // 2b) DESPLIEGUE de defensores de la mano al cuartel (los más potentes),
        //     con tope anti-apilado, hasta que 40 + guarnición cubra la amenaza.
        var desplegadas = new List<Dictionary<string, object?>>();
        int nDesp = 0;
        var candidatas = mano
            .Where(id => ctx.CatalogoMano.TryGetValue(id, out var b) && !AccionesTacticas.EsCartaAccion(b) && !EsEstatica(b))
            .OrderByDescending(id => { var c = ctx.CatalogoMano[id]; return Fuerza(c) + Defensa(c); })
            .ToList();
        foreach (var id in candidatas)
        {
            if (nDesp >= MAX_DESPLIEGUE_DEFENSA) break;
            if (BONO_CUARTEL + materialGuarnicion >= amenaza) break;   // ya cubierto
            var baseCard = ctx.CatalogoMano[id];
            int coste = M.Int(M.Get(baseCard, "Coste", "coste"));
            if (coste > energia) continue;
            if (!CanLand(miCuartel, Tipo(baseCard), terreno)) continue;   // tipo incompatible con el cuartel
            var nu = NuevaUnidad(baseCard, id, botUid, ctx.Zona);
            desplegadas.Add(nu);
            energia -= coste; gastado += coste; nDesp++;
            mano.Remove(id);
            materialGuarnicion += Fuerza(baseCard) + Defensa(baseCard);
        }

        // ── 3) MOVIMIENTO: guarnición + interceptores por celdas de aproximación ──
        // Celdas de acceso: a ≤ RADIO del cuartel, que ALGÚN stack enemigo pueda pisar
        // (mov + terreno de ese enemigo). Separa mar/tierra por CanLand del tipo del
        // enemigo. Peso = fuerza enemiga que puede converger en esa celda.
        var incoming = new Dictionary<string, int>();
        foreach (var v in CeldasCerca(miCuartel, RADIO_APROXIMACION, filas, columnas))
            foreach (var s in stacksAmenaza)
                if (Manhattan(s.coord, v, filas, columnas) <= s.mov && CanLand(v, s.tipoRep, terreno))
                    incoming[v] = incoming.GetValueOrDefault(v) + s.fuerza;

        var celdasAcceso = incoming.Count > 0
            ? incoming.OrderByDescending(kv => kv.Value).Select(kv => kv.Key).Take(MAX_CELDAS_APROXIMACION).ToList()
            : new List<string> { amenazaPrincipal };

        // Interceptores = los más fuertes hasta batir el stack principal, dejando ≥1
        // en el cuartel. (Con parálisis lanzada, el interceptado ya no ataca; rematar
        // se vuelve seguro.)
        var ordenadas = misUnidades.Where(u => u.coord != miCuartel)
            .OrderByDescending(u => u.f).ToList();
        var interceptoras = new List<(string coord, Dictionary<string, object?> card, int f, int d, int mov, int tipo)>();
        double fuerzaInt = 0.0;
        foreach (var u in ordenadas)
        {
            if (fuerzaInt > fuerzaPrincipal) break;
            interceptoras.Add(u); fuerzaInt += u.f;
        }
        var setInter = new HashSet<Dictionary<string, object?>>(interceptoras.Select(u => u.card));

        // Coloca la guarnición evolucionada + desplegados en el cuartel.
        foreach (var g in cartaGuarnicionFinal) Add(miCuartel, g.card);
        foreach (var nu in desplegadas) Add(miCuartel, nu);

        // Coloca las unidades que NO están en el cuartel: interceptores a celdas de
        // acceso (compatibles con su terreno), el resto repliega al cuartel.
        int k = 0;
        foreach (var u in misUnidades)
        {
            if (u.coord == miCuartel) continue;        // ya colocada arriba
            if (setInter.Contains(u.card))
            {
                string objetivo = ElegirCeldaAcceso(u, celdasAcceso, amenazaPrincipal!, terreno, ref k);
                Add(PasoTerreno(u, objetivo, terreno, filas, columnas), u.card);
            }
            else
            {
                Add(PasoTerreno(u, miCuartel, terreno, filas, columnas), u.card);
            }
        }

        return new BotMove
        {
            Celdas = celdas,
            Acciones = acciones,
            ManoResultante = mano,
            EnergiaGastada = gastado,
        };
    }

    // Elige, para un interceptor, la celda de acceso que pueda PISAR (terreno),
    // repartiendo en round-robin; si ninguna le sirve, va hacia el stack principal.
    private static string ElegirCeldaAcceso(
        (string coord, Dictionary<string, object?> card, int f, int d, int mov, int tipo) u,
        List<string> celdasAcceso, string principal, Dictionary<string, string> terreno, ref int k)
    {
        for (int t = 0; t < celdasAcceso.Count; t++)
        {
            var cand = celdasAcceso[(k + t) % celdasAcceso.Count];
            if (CanLand(cand, u.tipo, terreno)) { k = (k + t + 1) % Math.Max(1, celdasAcceso.Count); return cand; }
        }
        return principal;
    }

    private static string PasoTerreno(
        (string coord, Dictionary<string, object?> card, int f, int d, int mov, int tipo) u,
        string objetivo, Dictionary<string, string> terreno, int filas, int columnas)
    {
        if (u.coord == objetivo) return u.coord;
        var (tierra, mar) = TerrenoUtil.ClaseDeTipo(u.tipo);
        return TerrenoUtil.PasoHaciaTerreno(u.coord, objetivo, u.mov, tierra, mar, terreno, filas, columnas);
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

    // ── Terreno (réplica de la lógica del cliente/estratega) ──
    private static string Terr(string coord, Dictionary<string, string> t) => t.TryGetValue(coord, out var v) ? v : "land";
    private static bool CanLand(string coord, int tipo, Dictionary<string, string> t) => tipo switch
    {
        1 or 2 => Terr(coord, t) is "land" or "amphibious",
        3 => Terr(coord, t) is "sea" or "deepSea" or "amphibious",
        _ => true,
    };

    private static IEnumerable<string> CeldasCerca(string centro, int radio, int filas, int columnas)
    {
        var p = Parse(centro); if (p == null) yield break;
        var (ri, ci) = p.Value;
        for (int dr = -radio; dr <= radio; dr++)
            for (int dc = -radio; dc <= radio; dc++)
            {
                if (dr == 0 && dc == 0) continue;
                if (Math.Abs(dr) + Math.Abs(dc) > radio) continue;   // Manhattan ≤ radio
                int nr = ri + dr, nc = ci + dc;
                if (nr < 0 || nr >= filas || nc < 0 || nc >= columnas) continue;
                yield return $"{(char)('A' + nr)}{nc + 1}";
            }
    }

    // ── Utilidades ──
    private static bool EsMio(Dictionary<string, object?> c, string botUid) =>
        M.Str(M.Get(c, "ownerUid")) == botUid;
    private static bool EsEnemigo(Dictionary<string, object?> c, string botUid)
    { var o = M.Str(M.Get(c, "ownerUid")); return o != "" && o != botUid; }
    private static bool EsEstatica(Dictionary<string, object?> baseCard)
        => M.Int(M.Get(baseCard, "Condicion", "condicion")) == 3;
    private static int Fuerza(Dictionary<string, object?> c) => M.Int(M.Get(c, "Fuerza", "fuerza"));
    private static int Defensa(Dictionary<string, object?> c) => M.Int(M.Get(c, "Defensa", "defensa"));
    private static int Mov(Dictionary<string, object?> c) => M.Int(M.Get(c, "Movimiento", "movimiento"));
    private static int Tipo(Dictionary<string, object?> c) { int t = M.Int(M.Get(c, "Tipo", "tipo")); return t <= 0 ? 1 : t; }

    private static bool EnEnfriamiento(Dictionary<string, object?> c, int turno)
    {
        int enf = M.Int(M.Get(c, "EnfriamientoHabilidad", "enfriamientoHabilidad"));
        if (enf <= 0) return false;
        var ultimo = M.Get(c, "UltimoUsoHabilidad", "ultimoUsoHabilidad");
        if (ultimo == null) return false;
        return (turno - M.Int(ultimo)) < enf;
    }

    private static Tablero TableroDesde(Dictionary<string, object?> estado)
    {
        var t = new Tablero();
        foreach (var kv in M.Map(M.Get(estado, "tablero")))
            t[kv.Key] = M.List(kv.Value).Select(M.Map).ToList();
        return t;
    }
    private static int Manhattan(string a, string b, int filas, int columnas)
    {
        var pa = Parse(a); var pb = Parse(b);
        if (pa == null || pb == null) return int.MaxValue;
        return Math.Abs(pa.Value.ri - pb.Value.ri) + Math.Abs(pa.Value.ci - pb.Value.ci);
    }
    private static (int ri, int ci)? Parse(string coord)
    {
        if (string.IsNullOrEmpty(coord) || coord.Length < 2) return null;
        int ri = char.ToUpperInvariant(coord[0]) - 'A';
        if (!int.TryParse(coord[1..], out int col)) return null;
        return (ri, col - 1);
    }
}