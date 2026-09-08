using System.Collections.Concurrent;
using System.Text.Json;
using Google.Cloud.Firestore;
// Opción A: catálogo, enums y modelo de habilidad viven SOLO en AccionesTacticas.
// Se aliasan para que el resto del fichero siga usando Efe/Rng/Hab sin cambios.
using Efe = AccionesTacticas.Efecto;
using Rng = AccionesTacticas.Rango;
using Hab = AccionesTacticas.Hab;
// ─────────────────────────────────────────────────────────────────────────────
// WarZeroBot.cs
//
// Bot server-side para RELLENAR SALAS. Reutiliza WarZeroService y WarZeroFirestore;
// juega contra la misma lógica autoritativa que un humano.
//
// REGLA CLAVE: al cerrar turno cada jugador reemite SOLO sus propias cartas y el
// servidor fusiona las de todos. La estrategia SIEMPRE reemite todas las unidades
// propias (posición actual, movida o desplegada) o el ejército desaparecería.
//
// Estrategias:
//   · ReclutaStrategy   — arrastra ejército + despliega en el cuartel.
//   · EstrategaStrategy — (por defecto) despliega, FARMEA energía, CAZA unidades
//     enemigas que puede batir, ataca solo cuando gana, CONQUISTA coordinando
//     fuerza > umbral, y usa HABILIDADES (disparo/veneno/parálisis/escudo).
//
// ── v10 (partidas de estudio 9UCNdoqX / TrTlKcoJ / WPnCnMHH / wlDMcZEj) ─────
//   · REGLAS DE ENTRADA compartidas (ReglasEntrada.cs): combate exacto con +40,
//     evolución rival pagable con estadísticas reales, refuerzo que el dueño
//     de un cuartel puede desplegar. GanaGrupo / Segura delegan ahí.
//   · AMENAZA REALISTA al cuartel: el MAYOR stack que llega + mitad del resto,
//     en poder (F+D). Antes se sumaba la fuerza de TODO lo que alcanzaba y se
//     comparaba solo con la defensa: en wlDM un bot ancló 8 cartas en casa 12
//     turnos (89 % de sus unidad-turnos), ingresó 3/turno y murió de hambre.
//   · DEFENSA PROPORCIONAL Y BARATA, decidida ANTES de la ofensiva: se ancla
//     la guarnición mínima que aguanta (lentas y baratas; nunca un general) y
//     el resto sale a jugar. Solo si ni con todo se aguanta (amenaza GRAVE) se
//     bloquea la ofensiva y se recluta de emergencia.
//   · LA PIEZA FUERTE SALE: en TrTl el General Izanagi (80 de poder) pasó 6 de
//     7 turnos de guarnición; un Elefante 8 de 11. La guarnición se elige por
//     perfil defensivo, no por potencia, y la evolución ya no se apaga al
//     estar amenazado (evolucionar al defensor ES defender).
//   · SIN DOBLE COBRO de acciones (ver BotMove.EnergiaGastada).
//   · Objetivo de asedio con histéresis, marcha sin excluir navales (los
//     cuarteles son anfibios) y con camino real por terreno.
//   · Evoluciones de TODO el tablero en el contexto (para leer al rival).
// ─────────────────────────────────────────────────────────────────────────────

public class WarZeroBotOptions
{
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(60);
    public TimeSpan ThinkDelay { get; set; } = TimeSpan.FromSeconds(3);
    public TimeSpan MaxWaitStart { get; set; } = TimeSpan.FromMinutes(15);
    public int MaxDeploysPorTurno { get; set; } = 3;

    /// Nº máximo de acciones de HABILIDAD (de unidades en tablero) por turno.
    public int MaxAcciones { get; set; } = 4;

    /// Nº máximo de CARTAS DE ACCIÓN (jugadas desde la mano) por turno.
    public int MaxAccionesCarta { get; set; } = 2;

    /// Nº máximo de unidades propias que el bot mantiene sobre su cuartel.
    /// Pocas a propósito: un AoE a distancia puede barrer un cuartel apilado.
    public int MaxDefensoresCuartel { get; set; } = 2;

    /// Probabilidad [0..1] de intentar INTERCEPTAR (adivinar el avance rival)
    /// en vez de ir directo a la casilla actual de la carta enemiga. 1.0 = siempre
    /// que exista una intercepción mejor; valores bajos = más pasivo.
    public double ProbCazaPredictiva { get; set; } = 1.0;

    /// Si la carta enemiga tiene movimiento MAYOR que esto, su avance es
    /// demasiado impredecible: se persigue como siempre (sin interceptar).
    public int MovMaxPredecible { get; set; } = 5;

    /// Nº máximo de EVOLUCIONES por turno. Evolucionar cuesta energía y la carta
    /// evolucionada no puede moverse ese turno (y una que se movió no evoluciona).
    public int MaxEvolucionesPorTurno { get; set; } = 2;

    /// Si el bot compra GENERALES (cartas especiales) en su cuartel. Cada general
    /// solo puede comprarse UNA vez por partida; si muere, no vuelve.
    public bool ComprarGenerales { get; set; } = true;

    /// Ejército del bot (1..4) si no está definido en su documento de `Bots`.
    /// 0 = derivarlo de forma estable a partir del uid del bot.
    public int EjercitoPorDefecto { get; set; } = 0;
}

// ─────────────────────────────────────────────────────────────────────────────
// PERFIL DE BOT (dificultad + estilo)
//
// Dos ejes ORTOGONALES que se configuran por bot desde el panel de Flutter
// (colección `Bots`, campos `dificultad` y `estilo`) y viajan hasta la estrategia:
//
//   · DIFICULTAD — cuán FUERTE juega (no cuán temerario):
//       Medio → los valores actuales (jugador medio, ya competente).
//       Alto  → más recursos por turno (despliegues, habilidades, evoluciones),
//               menos energía ociosa en reserva, predice también unidades rápidas,
//               lanza más asaltos coordinados y concentra la fuerza para rematar
//               antes. NO es "suicida": los ataques individuales siguen exigiendo
//               ganar el combate; lo que sube es la PRESIÓN y el aprovechamiento.
//
//   · ESTILO — DÓNDE invierte esos recursos:
//       Equilibrado → como hasta ahora (farmea, caza, conquista con criterio).
//       Defensivo   → guarnición más densa, más reserva de energía, prioriza
//                     farmear/defender su territorio y solo pelea cuando gana claro.
//       Agresivo    → empuja hacia los cuarteles enemigos, compromete grupos antes,
//                     gasta más energía, deja el cuartel más ligero y remata
//                     cuarteles aunque esté siendo amenazado (si de verdad los gana).
//
// Un bot MEDIO + EQUILIBRADO reproduce EXACTAMENTE el comportamiento anterior, de
// modo que el cambio es retrocompatible con los bots ya sembrados en Firestore.
// ─────────────────────────────────────────────────────────────────────────────
public enum DificultadBot { Medio, Alto }
public enum EstiloBot { Equilibrado, Defensivo, Agresivo }

public sealed class PerfilBot
{
    public DificultadBot Dificultad { get; init; } = DificultadBot.Medio;
    public EstiloBot Estilo { get; init; } = EstiloBot.Equilibrado;

    /// Perfil neutro: nivel medio, estilo equilibrado (= comportamiento clásico).
    public static readonly PerfilBot PorDefecto = new();

    /// Construye un perfil a partir de las cadenas guardadas en el documento del
    /// bot. Tolera nulos, mayúsculas y espacios; cualquier valor desconocido cae a
    /// los valores por defecto (medio / equilibrado).
    public static PerfilBot Parse(string? dificultad, string? estilo) => new()
    {
        Dificultad = (dificultad ?? "").Trim().ToLowerInvariant() switch
        {
            "alto" => DificultadBot.Alto,
            _ => DificultadBot.Medio,
        },
        Estilo = (estilo ?? "").Trim().ToLowerInvariant() switch
        {
            "defensivo" => EstiloBot.Defensivo,
            "agresivo" => EstiloBot.Agresivo,
            _ => EstiloBot.Equilibrado,
        },
    };

    public override string ToString() => $"{Dificultad}/{Estilo}";
}

public class BotContext
{
    public required Dictionary<string, object?> Estado { get; init; }
    public required string BotUid { get; init; }
    public required int Turno { get; init; }
    public required string Cuartel { get; init; }
    public required int Energia { get; init; }
    public required List<string> Mano { get; init; }
    public required Dictionary<string, Dictionary<string, object?>> CatalogoMano { get; init; }
    public required string Zona { get; init; }

    // Mapa / farmeo
    public required Dictionary<string, string> Terreno { get; init; }   // coord -> land|sea|deepSea|amphibious
    public required int Filas { get; init; }
    public required int Columnas { get; init; }
    public required HashSet<string> IslaCentral { get; init; }          // celdas isla central (+7)
    public required Dictionary<string, List<string>> Continentes { get; init; } // obeliscoCoord -> celdas
    public required HashSet<string> Rayos { get; init; }                // celdas de rayo activas (+10)

    /// v9b: coord de rayo -> TURNOS que le quedan de vida (incluido el actual;
    /// el servidor los crea con 3 y descuenta 1 por turno TRAS pagar el farmeo).
    /// Sin esto el bot emprendía marchas de 3-4 turnos hacia rayos que morían
    /// antes de que llegara nadie (medido: rayos por turno del humano hasta 1,0;
    /// de los bots 0,0-0,4). Vacío = comportamiento antiguo (todos "eternos").
    public Dictionary<string, int> RayosTurnos { get; init; } = new();

    // ── Evoluciones y generales ──
    /// Ejército del bot (1..4). Determina qué generales puede comprar.
    public int EjercitoId { get; init; }

    /// Cartas de EVOLUCIÓN referenciadas por las unidades propias en tablero:
    /// idEvolucion -> datos de la carta resultante.
    public Dictionary<string, Dictionary<string, object?>> Evoluciones { get; init; } = new();

    /// GENERALES (cartas especiales, Condicion==5) del ejército del bot que
    /// AÚN no ha comprado esta partida.
    public List<Dictionary<string, object?>> GeneralesDisponibles { get; init; } = new();
}

public class BotMove
{
    public Dictionary<string, List<Dictionary<string, object?>>> Celdas { get; init; } = new();
    public List<Dictionary<string, object?>> Acciones { get; init; } = new();
    public List<string> ManoResultante { get; init; } = new();

    /// Energía que el bot PAGA POR ADELANTADO vía ActualizarStats (despliegues,
    /// evoluciones, generales, robos; negativa si sacrifica). v10: NO incluye el
    /// coste de las acciones/habilidades: el servidor las cobra él mismo en la
    /// resolución (fase "coste-acciones" de WarZeroService). Sumarlas aquí era
    /// un DOBLE COBRO: TrTl T14, escudo lejano de 50 rechazado por "energías
    /// insuficientes" con 9 disponibles, porque el bot ya se había cobrado 50.
    public int EnergiaGastada { get; init; }

    /// Coste de las acciones incluidas en el plan (solo informativo / presupuesto
    /// local). Lo cobra el servidor al resolver el turno.
    public int EnergiaAcciones { get; init; }

    /// Id del general (carta especial) comprado este turno, si lo hubo. Se
    /// persiste con arrayUnion en `especialesCompradas`: por eso cada general
    /// solo puede comprarse una vez por partida (si muere, no vuelve).
    public string? EspecialComprada { get; init; }
}

public interface IBotStrategy
{
    BotMove DecidirJugada(BotContext ctx);
}

// ─────────────────────────────────────────────────────────────────────────────
// Recluta — arrastra ejército + despliega en el cuartel. Nunca bloquea.
// ─────────────────────────────────────────────────────────────────────────────
public class ReclutaStrategy : IBotStrategy
{
    private readonly int _maxDeploys;
    public ReclutaStrategy(int maxDeploysPorTurno = 2) => _maxDeploys = Math.Max(0, maxDeploysPorTurno);

    public BotMove DecidirJugada(BotContext ctx)
    {
        var celdas = new Dictionary<string, List<Dictionary<string, object?>>>();
        var tablero = M.Map(M.Get(ctx.Estado, "tablero"));
        var zona = ctx.Zona;
        foreach (var (coord, cartasRaw) in tablero)
            foreach (var cRaw in M.List(cartasRaw))
            {
                var carta = M.Map(cRaw);
                if (M.Str(M.Get(carta, "ownerUid")) != ctx.BotUid) continue;
                if (!celdas.TryGetValue(coord, out var lst)) { lst = new(); celdas[coord] = lst; }
                lst.Add(new Dictionary<string, object?>(carta));
                if (zona == "") zona = M.Str(M.Get(carta, "ownerZone"));
            }

        var mano = new List<string>(ctx.Mano);
        int energia = ctx.Energia, gastado = 0, desplegadas = 0;
        if (ctx.Cuartel != "")
            foreach (var id in ctx.Mano)
            {
                if (desplegadas >= _maxDeploys) break;
                if (!ctx.CatalogoMano.TryGetValue(id, out var cartaBase)) continue;
                var cond = M.Int(M.Get(cartaBase, "Condicion", "condicion"));
                if (cond == 4) continue; // acción: no se despliega desde aquí
                // Estática (Condicion==3): NO puede desplegarse en el cuartel.
                // Aquí no se coloca; se hace en la FASE ESTÁTICAS de más abajo,
                // sobre una celda propia YA mantenida (anclaje válido).
                if (cond == 3) continue;
                int coste = M.Int(M.Get(cartaBase, "Coste", "coste"));
                if (coste > energia) continue;
                var celda = new Dictionary<string, object?>(cartaBase)
                { ["id"] = id, ["ownerUid"] = ctx.BotUid, ["ownerZone"] = zona, ["instanceId"] = Guid.NewGuid().ToString("N") };
                if (!celdas.TryGetValue(ctx.Cuartel, out var lst)) { lst = new(); celdas[ctx.Cuartel] = lst; }
                lst.Add(celda);
                energia -= coste; gastado += coste; desplegadas++; mano.Remove(id);
            }
        // ── FASE ESTÁTICAS: fortificar posiciones mantenidas ───────────────────
        // En Recluta las unidades propias no se mueven (se recolocan en su misma
        // celda), así que TODA celda propia que no sea el cuartel es un anclaje
        // válido para una estática. Se prioriza defender el farmeo (rayo / isla)
        // y, en su defecto, cualquier posición retenida. Una por turno (estrategia
        // de baja intensidad). El servidor revalida el anclaje por si acaso.
        var estaticasMano = mano
            .Where(id => ctx.CatalogoMano.TryGetValue(id, out var b)
                         && M.Int(M.Get(b, "Condicion", "condicion")) == 3)
            .ToList();
        if (estaticasMano.Count > 0)
        {
            bool PuedeAterrizar(string coord, int tipo)
            {
                var terr = ctx.Terreno.TryGetValue(coord, out var v) ? v : "land";
                return tipo switch
                {
                    1 or 2 => terr is "land" or "amphibious",
                    3 => terr is "sea" or "deepSea" or "amphibious",
                    _ => true,
                };
            }
            // Anclas = celdas propias (≠ cuartel), priorizando celdas de farmeo.
            var anclas = celdas.Keys
                .Where(c => c != ctx.Cuartel && celdas[c].Count > 0)
                .OrderByDescending(c =>
                    (ctx.Rayos.Contains(c) ? 8 : 0) + (ctx.IslaCentral.Contains(c) ? 5 : 0))
                .ToList();
            foreach (var coord in anclas)
            {
                var id = estaticasMano.FirstOrDefault(sid =>
                {
                    var b = ctx.CatalogoMano[sid];
                    int tipo = M.Int(M.Get(b, "Tipo", "tipo")); if (tipo <= 0) tipo = 1;
                    int coste = M.Int(M.Get(b, "Coste", "coste"));
                    return PuedeAterrizar(coord, tipo) && coste <= energia;
                });
                if (id == null) continue;
                var baseCard = ctx.CatalogoMano[id];
                int coste = M.Int(M.Get(baseCard, "Coste", "coste"));
                var est = new Dictionary<string, object?>(baseCard)
                {
                    ["id"] = id,
                    ["ownerUid"] = ctx.BotUid,
                    ["ownerZone"] = zona,
                    ["instanceId"] = Guid.NewGuid().ToString("N"),
                };
                celdas[coord].Add(est);
                energia -= coste; gastado += coste; mano.Remove(id);
                Console.WriteLine($"[WZ][bot {ctx.BotUid}] ESTÁTICA (recluta) en {coord}");
                break; // una estática por turno
            }
        }

        return new BotMove { Celdas = celdas, ManoResultante = mano, EnergiaGastada = gastado };
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Estratega (equilibrado): farmear + cazar + atacar + conquistar + habilidades.
// ─────────────────────────────────────────────────────────────────────────────
public class EstrategaStrategy : IBotStrategy
{
    private readonly int _maxDeploys;
    private readonly int _maxAcciones;
    private readonly int _maxAccionesCarta;
    private readonly int _maxDefensoresCuartel;
    private readonly double _probPrediccion;
    private readonly int _movMaxPredecible;
    private readonly Random _rng = new();

    // Memoria entre turnos: instanceId de cada carta enemiga -> su coord el turno
    // ANTERIOR. Permite estimar su vector de avance (dead reckoning) y predecir a
    // dónde irá el próximo turno. La estrategia vive toda la partida, así que este
    // estado persiste entre llamadas a DecidirJugada.
    private Dictionary<string, string> _ultimaPosEnemigo = new();

    // Umbral de fuerza para conquistar un cuartel SIN defensor y bono de defensa
    // que un cuartel DEFENDIDO otorga a su dueño. DEBE coincidir con
    // `Combate.DefensaObelisco` de WarZeroLogic.cs (allí es 40). Antes estaba a 80
    // (el doble): eso hacía que el bot creyera los cuarteles inconquistables y no
    // se atreviera a rematar partidas. Corregido a 40 para que ataque cuando toca.
    private const int UmbralCuartel = 40;

    /// Energía a partir de la cual el bot entra en EMPUJE FINAL: deja de priorizar
    /// el farmeo individual y todo marcha a cazar/asediar. DEBE ir a la par de
    /// EvaluadorTablero.UMBRAL_VICTORIA (modo victoria del evaluador), que baja la
    /// economía y sube la presión con el mismo umbral. Fallo observado: bots con
    /// cientos de energía aparcados en celdas de farmeo sin rematar la partida.
    private const int UmbralEmpujeFinal = 400;

    // ── v9: ASEDIO (partidas de estudio EennDB / N0DGsq / ThEozf / atpcCJ) ──
    // Ningún bot conquistó un solo obelisco en 4 partidas (71 turnos): el punto
    // de reunión v8 era la celda propia MÁS FUERTE (casi siempre en casa) y el
    // 89% de las cartas son "débiles" (F<=40), así que el ejército entero
    // convergía hacia atrás (distancia media al cuartel rival: bots 5,4 → 10,0;
    // humano 8,0 → 2,0). El asalto en manada exigía ALCANZAR el cuartel ese
    // mismo turno, y con el ejército replegado nunca llegaba nadie. v9:
    //   · OBJETIVO DE ASEDIO: el cuartel enemigo más barato (guarnición + bono,
    //     ponderado por distancia).
    //   · PUNTO DE REUNIÓN OFENSIVO: la masa se forma en el frente, no en casa.
    //   · MARCHA DE ASEDIO: si el grupo que GANA la toma no llega hoy, marcha en
    //     bola hacia el objetivo (paso del más lento, sin pisar peleas perdidas).
    /// Margen de poder (F+D) del grupo sobre (guarnición + bono) para marchar.
    private const double MargenAsedio = 1.25;
    /// Cada casilla de distancia al cuartel objetivo equivale a este poder de
    /// guarnición al elegir objetivo (cercano y débil > lejano y débil).
    private const int PesoDistanciaAsedio = 6;
    /// Unidades mínimas del grupo de marcha: nunca una carta suelta.
    private const int MinGrupoAsedio = 2;
    /// Unidades móviles a partir de las cuales el bot AHORRA para evolucionar
    /// en vez de seguir sacando cartas base (v9, prioridad de evolución).
    /// v10: 6 → 3. Con el candado en 6 los bots pequeños nunca ahorraban y
    /// diluían la energía en cartas base (poder medio por unidad: bots 15-19,
    /// humanos 45-50 en las partidas de estudio).
    private const int UnidadesParaAhorrarEvolucion = 3;

    /// Cuartel enemigo objetivo del turno (se fija en DecidirJugada; lo usa el
    /// último recurso de ObjetivoGlobal para orientar a las piezas fuertes).
    private string? _objetivoAsedioTurno;

    /// v10: objetivo de asedio del turno ANTERIOR (histéresis). El objetivo se
    /// elegía cada turno por distancia media del ejército, así que cambiaba al
    /// moverse las piezas y la marcha nunca cuajaba. Se mantiene el anterior
    /// mientras no sea claramente más caro que el mejor nuevo.
    private string? _objetivoAsedioPrevio;
    private const double HisteresisAsedio = 1.3;

    /// v10: reglas de entrada/amenaza del turno (ReglasEntrada), construidas al
    /// principio de DecidirJugada y compartidas por toda la decisión.
    private ReglasEntrada.Contexto? _reglas;

    private readonly int _maxEvoluciones;
    private readonly bool _comprarGenerales;

    // ── Parámetros derivados del PERFIL (dificultad + estilo) ──
    private readonly PerfilBot _perfil;
    /// % de energía que se guarda sin gastar cuando NO hay urgencia. Menos reserva
    /// = juega más su energía (dificultad alta / estilo agresivo).
    private readonly int _reservaPct;
    /// Valentía en las lecturas de combate DE GRUPO (asalto coordinado / contención
    /// de intrusos). Se suma a la defensa propia efectiva: >0 compromete grupos
    /// algo antes; <0 exige ventaja más clara. NUNCA se aplica a la toma de un
    /// cuartel (eso siempre exige ganar de verdad).
    private readonly int _sesgoAtaque;
    /// Empuje hacia el frente enemigo frente a desviarse a por energía. >0 (agresivo)
    /// exige que la energía esté MUCHO más cerca para ir a por ella; <0 (defensivo)
    /// se desvía a farmear con más facilidad.
    private readonly int _sesgoFrente;
    /// Nº de asaltos coordinados (caza en grupo) por turno.
    private readonly int _maxCazasGrupo;
    /// Nº de stacks enemigos que se evalúan como objetivo de caza en grupo.
    private readonly int _topObjetivosGrupo;
    /// Si asalta cuarteles enemigos AUNQUE su propio cuartel esté amenazado (siempre
    /// que el grupo realmente gane la toma). Estilo agresivo.
    private readonly bool _asaltoBajoAmenaza;

    /// Retrocompatible: perfil neutro (medio / equilibrado) = comportamiento clásico.
    public EstrategaStrategy(WarZeroBotOptions opt) : this(opt, PerfilBot.PorDefecto) { }

    public EstrategaStrategy(WarZeroBotOptions opt, PerfilBot? perfil)
    {
        _perfil = perfil ?? PerfilBot.PorDefecto;
        bool alto = _perfil.Dificultad == DificultadBot.Alto;

        // Base = opciones globales (equivale a MEDIO / EQUILIBRADO).
        int maxDeploys = opt.MaxDeploysPorTurno;
        int maxAcc = opt.MaxAcciones;
        int maxAccCarta = opt.MaxAccionesCarta;
        int maxEvo = opt.MaxEvolucionesPorTurno;
        int movPred = opt.MovMaxPredecible;
        int defensores = opt.MaxDefensoresCuartel;
        int reservaPct = 10; // MENOS reserva ociosa: convertir energía en tablero
        int cazasGrupo = 2, topGrupo = 3, sesgoAtaque = 0, sesgoFrente = 0;
        bool asaltoBajoAmenaza = false;

        // ── DIFICULTAD ALTO: más recursos por turno, mejor lectura, más presión ──
        //    (más fuerte, no más temerario: los ataques siguen exigiendo ganar).
        if (alto)
        {
            maxDeploys += 1;   // saca más tropa al tablero
            maxAcc += 2;   // usa más habilidades desde tablero
            maxAccCarta += 1;   // juega más cartas de acción
            maxEvo += 1;   // evoluciona más unidades
            movPred = Math.Max(movPred, 9); // predice también unidades rápidas
            reservaPct = 10;  // deja menos energía ociosa
            cazasGrupo = 3;   // más asaltos coordinados por turno
            topGrupo = 5;   // evalúa más stacks enemigos para cazar en grupo
            sesgoAtaque += 6;   // concentra fuerza y remata algo antes
        }

        // ── ESTILO: sesga DÓNDE invierte los recursos (ortogonal a la dificultad) ──
        switch (_perfil.Estilo)
        {
            case EstiloBot.Defensivo:
                defensores += 2;   // guarnición más densa
                reservaPct += 10;  // más colchón de energía para reaccionar
                sesgoFrente -= 4;   // prioriza farmear / defender su territorio
                sesgoAtaque -= 6;   // solo pelea en grupo cuando gana con claridad
                break;
            case EstiloBot.Agresivo:
                defensores = Math.Max(1, defensores - 1); // cuartel más ligero
                reservaPct = Math.Max(0, reservaPct - 8); // gasta para presionar
                sesgoFrente += 6;   // empuja hacia los cuarteles enemigos
                sesgoAtaque += 8;   // compromete grupos antes
                asaltoBajoAmenaza = true; // remata cuarteles aunque le amenacen
                maxDeploys += 1;   // más presencia en el frente
                break;
            default: break;         // Equilibrado: sin cambios
        }

        _maxDeploys = Math.Max(0, maxDeploys);
        _maxAcciones = Math.Max(0, maxAcc);
        _maxAccionesCarta = Math.Max(0, maxAccCarta);
        _maxDefensoresCuartel = Math.Max(1, defensores);
        _probPrediccion = Math.Clamp(opt.ProbCazaPredictiva, 0.0, 1.0);
        _movMaxPredecible = Math.Max(0, movPred);
        _maxEvoluciones = Math.Max(0, maxEvo);
        _comprarGenerales = opt.ComprarGenerales;
        _reservaPct = Math.Clamp(reservaPct, 0, 90);
        _maxCazasGrupo = Math.Max(1, cazasGrupo);
        _topObjetivosGrupo = Math.Max(1, topGrupo);
        _sesgoAtaque = sesgoAtaque;
        _sesgoFrente = sesgoFrente;
        _asaltoBajoAmenaza = asaltoBajoAmenaza;
    }



    public BotMove DecidirJugada(BotContext ctx)
    {
        var estado = ctx.Estado;
        var botUid = ctx.BotUid;
        int filas = ctx.Filas, columnas = ctx.Columnas;
        var terreno = ctx.Terreno;

        // v10: reglas de entrada y amenaza del turno (combate exacto, evolución
        // rival pagable, refuerzo de cuartel). Todo GanaGrupo/Segura pasa por aquí.
        ReglasEntrada.Contexto reglas = ReglasEntrada.Crear(ctx);
        _reglas = reglas;

        var tablero = M.Map(M.Get(estado, "tablero"));
        var obeliscos = M.Map(M.Get(estado, "obeliscos"));
        var eliminados = M.List(M.Get(estado, "jugadoresEliminados")).Select(M.Str).ToHashSet();

        // coord -> uid dueño del cuartel
        var cuartelOwner = new Dictionary<string, string>();
        foreach (var (uid, cObj) in obeliscos) { var c = M.Str(cObj); if (c != "") cuartelOwner[c] = uid; }
        var cuartelCoords = cuartelOwner.Keys.ToHashSet();

        // Unidades propias y celdas con enemigos
        var ownUnits = new List<(string coord, Dictionary<string, object?> card, string inst)>();
        var enemyByCoord = new Dictionary<string, List<Dictionary<string, object?>>>();
        foreach (var (coord, raw) in tablero)
            foreach (var cRaw in M.List(raw))
            {
                var card = M.Map(cRaw);
                if (M.Str(M.Get(card, "ownerUid")) == botUid)
                    ownUnits.Add((coord, card, M.Str(M.Get(card, "instanceId"))));
                else
                {
                    if (!enemyByCoord.TryGetValue(coord, out var l)) { l = new(); enemyByCoord[coord] = l; }
                    l.Add(card);
                }
            }

        var enemyCuarteles = cuartelOwner
            .Where(kv => kv.Value != botUid && !eliminados.Contains(kv.Value))
            .Select(kv => kv.Key).ToHashSet();

        // Mi cuartel: el declarado en ctx o, si no, el que figure en `obeliscos`.
        string? miCuartel = ctx.Cuartel != "" ? ctx.Cuartel
            : cuartelOwner.FirstOrDefault(kv => kv.Value == botUid).Key;
        if (string.IsNullOrEmpty(miCuartel)) miCuartel = null;

        // Celdas de MI continente (para detectar intrusos y defender el territorio).
        var miContinente = new HashSet<string>();
        if (miCuartel != null && ctx.Continentes.TryGetValue(miCuartel, out var celdasCont))
            miContinente = celdasCont.ToHashSet();

        // ── PREDICCIÓN DE AVANCE ENEMIGO (por celda) ───────────────────────────
        var misCoords = ownUnits.Select(u => u.coord).ToList();
        var atractoresEnergia = new HashSet<string>(ctx.Rayos);
        atractoresEnergia.UnionWith(ctx.IslaCentral);
        var predEnemigo = new Dictionary<string, string>();
        var posActualEnemigo = new Dictionary<string, string>();
        foreach (var (coord, cartas) in enemyByCoord)
        {
            foreach (var e in cartas)
            {
                var iid = M.Str(M.Get(e, "instanceId"));
                if (iid != "") posActualEnemigo[iid] = coord;
            }
            var rep = cartas.OrderByDescending(Mov).First();
            if (Mov(rep) > _movMaxPredecible) continue;
            var repIid = M.Str(M.Get(rep, "instanceId"));
            string? prev = repIid != "" && _ultimaPosEnemigo.TryGetValue(repIid, out var pv) ? pv : null;
            predEnemigo[coord] = PredecirAvance(coord, rep, prev, terreno, filas, columnas,
                miCuartel, misCoords, atractoresEnergia);
        }
        _ultimaPosEnemigo = posActualEnemigo;

        // ── RIVAL LÍDER (v9b, FFA) ─────────────────────────────────────────────
        // En la 8J hubo 42 combates bot-contra-bot y solo 19 contra el humano: los
        // bots se desangraban entre ellos mientras el líder (1.720 de energía, 938
        // PC) engordaba sin oposición. `lider` = el rival vivo con más energía+PC
        // si dobla la mediana de los demás; se usa para PRIORIZAR su cuartel como
        // objetivo de asedio (si está a tiro) y sus stacks como presa de caza.
        string? liderRival = null;
        {
            var statsTodos = M.Map(M.Get(estado, "statsPartida"));
            var puntuacion = new Dictionary<string, int>();
            foreach (var (uid, cObj) in obeliscos)
            {
                if (uid == botUid || eliminados.Contains(uid)) continue;
                var st = M.Map(M.Get(statsTodos, uid));
                puntuacion[uid] = M.Int(M.Get(st, "energies")) + M.Int(M.Get(st, "pc"));
            }
            if (puntuacion.Count >= 2)
            {
                var orden = puntuacion.OrderByDescending(kv => kv.Value).ToList();
                var resto = orden.Skip(1).Select(kv => kv.Value).OrderBy(v => v).ToList();
                int mediana = resto.Count == 0 ? 0 : resto[resto.Count / 2];
                if (orden[0].Value >= Math.Max(1, mediana) * 2) liderRival = orden[0].Key;
            }
            if (liderRival != null)
                Console.WriteLine($"[WZ][bot {botUid}] líder detectado: {liderRival} ({puntuacion[liderRival]} energía+pc)");
        }
        string? cuartelLider = liderRival != null ? M.Str(M.Get(obeliscos, liderRival)) : null;
        if (cuartelLider == "") cuartelLider = null;

        // farmValue(cell): energía por turno que daría pararse ahí.
        int Farm(string cell)
        {
            if (cuartelCoords.Contains(cell)) return 0;   // cuartel no farmea
            int v = 0;
            if (ctx.Rayos.Contains(cell)) v += 10;
            if (ctx.IslaCentral.Contains(cell)) v += 7;
            foreach (var (k, cells) in ctx.Continentes)
                if (cells.Contains(cell))
                {
                    var owner = cuartelOwner.GetValueOrDefault(k, "");
                    if (owner != "" && owner != botUid) v += 5;
                }
            return v;
        }

        // Turnos de vida que le quedan a la celda si es un rayo (0 si no lo es).
        int TurnosRayo(string cell)
        {
            if (ctx.RayosTurnos.TryGetValue(cell, out var t)) return t > 0 ? t : 1;
            return ctx.Rayos.Contains(cell) ? 1 : 0;
        }

        var zona = ctx.Zona;
        if (zona == "")
            foreach (var u in ownUnits) { var z = M.Str(M.Get(u.card, "ownerZone")); if (z != "") { zona = z; break; } }

        var celdas = new Dictionary<string, List<Dictionary<string, object?>>>();
        void Place(string coord, Dictionary<string, object?> card)
        {
            if (!celdas.TryGetValue(coord, out var l)) { l = new(); celdas[coord] = l; }
            l.Add(card);
        }

        var mano = new List<string>(ctx.Mano);
        int energia = ctx.Energia, gastado = 0, desplegadas = 0;
        // v10: coste de las acciones (presupuesto local; lo cobra el servidor).
        int gastadoAcciones = 0;
        var acciones = new List<Dictionary<string, object?>>();

        // ¿Hay enemigos que amenazan mi cuartel este turno o el siguiente?
        bool amenazado = miCuartel != null &&
            CuartelAmenazado(miCuartel, enemyByCoord, terreno, filas, columnas);

        // ── AMENAZA REALISTA (v10) ─────────────────────────────────────────────
        // Poder (F+D, con evolución rival pagable) que puede CAER sobre mi cuartel
        // el próximo turno: el MAYOR stack que llega más la MITAD del resto. Antes
        // se sumaba la FUERZA de todo lo que alcanzaba (o estaba a ≤2) y se
        // comparaba solo con la DEFENSA anclada: con 15 cartas rivales por el
        // mapa la suma era enorme y el bot anclaba el ejército entero (wlDM:
        // bot_1, 8 cartas en casa durante 12 turnos, 3 de ingreso por turno).
        // La regla del juego es poder contra poder: el cuartel aguanta si
        // (F+D de la guarnición) + 40 ≥ (F+D del asalto).
        int amenazaPoder = miCuartel != null ? ReglasEntrada.AmenazaSobre(reglas, miCuartel) : 0;
        // Poder que PUEDE llegar a casa este turno (lo que ya está + lo que llega).
        int poderDefendible = 0;
        if (miCuartel != null)
            foreach (var u in ownUnits)
                if (u.coord == miCuartel ||
                    Alcanzables(u.coord, Mov(u.card), Tipo(u.card), terreno, filas, columnas).Contains(miCuartel))
                    poderDefendible += Fuerza(u.card) + Defensa(u.card);
        // GRAVE: ni con todo lo que llega a casa se aguanta. Solo entonces la
        // ofensiva se bloquea (salvo estilo agresivo) y se recluta de emergencia.
        // CONTENIDA: basta una guarnición proporcional; el resto sale a jugar.
        bool amenazaGrave = amenazado && amenazaPoder > 0 && (UmbralCuartel + poderDefendible) < amenazaPoder;
        if (amenazado)
            Console.WriteLine($"[WZ][bot {botUid}] cuartel amenazado: entra {amenazaPoder} de poder, defendible {poderDefendible}+{UmbralCuartel} → {(amenazaGrave ? "GRAVE" : "contenida")}");

        // INTRUSOS: celdas con enemigos DENTRO de mi continente (excluye mi cuartel).
        // Si hay intrusos, hay que CONTENERLOS (defensa de territorio, punto 3).
        var intrusos = enemyByCoord.Keys
            .Where(c => miContinente.Contains(c) && c != miCuartel)
            .ToHashSet();
        bool continenteInvadido = intrusos.Count > 0;

        // ── FASE 0-pre: SACRIFICIO de cartas inservibles ───────────────────────
        // Sacrificar una carta devuelve floor(coste/2) de energía. Útil para
        // convertir en energía cartas de acción caras que el bot no puede pagar
        // (p. ej. un misil de 100 con un bot que apenas junta 40): en vez de morir
        // en la mano, se cambian por energía. Conservador: solo acciones inpagables
        // ahora y caras; nunca una carta que podría usar.
        {
            const int UMBRAL_CARA = 60; // "carta cara" (tunable)
            int gMin = (_comprarGenerales && ctx.GeneralesDisponibles.Count > 0)
                ? ctx.GeneralesDisponibles.Select(Coste).Where(c => c > 0).DefaultIfEmpty(0).Min()
                : 0;
            // v9b: un DISPARO nunca se sacrifica. Destruye TODAS las cartas de la
            // celda objetivo (WarZeroLogic.AplicarDisparo hace t.Remove): sobre un
            // stack rival evolucionado borra cientos de energía de inversión, y es
            // la llave para romper un cuartel atrincherado. El humano disparó antes
            // de 5 de sus 8 conquistas en la 8J; los bots lo vendían a mitad de
            // precio por no poder pagarlo ese turno.
            var sacrificables = mano
                .Where(id => ctx.CatalogoMano.TryGetValue(id, out var b)
                             && EsAccion(b) && Coste(b) > energia && Coste(b) >= UMBRAL_CARA
                             && !EsDisparo(b))
                .OrderByDescending(id => Coste(ctx.CatalogoMano[id]))
                .ToList();
            foreach (var id in sacrificables)
            {
                int retorno = Coste(ctx.CatalogoMano[id]) / 2;
                bool acercaGeneral = gMin > 0 && energia < gMin && energia + retorno >= gMin;
                bool impagableCronica = Coste(ctx.CatalogoMano[id]) > energia * 2; // ni de lejos la pagará
                if (!acercaGeneral && !impagableCronica) continue;
                energia += retorno; gastado -= retorno;   // gastado negativo ⇒ energía ganada
                mano.Remove(id);
                Console.WriteLine($"[WZ][bot {botUid}] SACRIFICA {M.Str(M.Get(ctx.CatalogoMano[id], "Nombre", "nombre"))} (+{retorno})");
                if (acercaGeneral) break;   // ya alcanza el general
            }
        }
        // ── FASE 0: DESPLIEGUE (ANTES de mover) ────────────────────────────────
        // CLAVE: una carta desplegada CAE en el cuartel pero PUEDE moverse el mismo
        // turno. Por eso se despliega ANTES de decidir movimientos y cada unidad
        // nueva se añade al pool `ownUnits` (con coord = mi cuartel): así sale a
        // jugar ya (farmear, cazar, asaltar) en vez de quedarse parada en casa.
        var recienInst = new HashSet<string>();
        string? especialComprada = null;
        bool remontada = ownUnits.Count <= 1; // casi sin tablero: recuperar presencia ya

        // Añade una unidad recién desplegada al pool movible (en el cuartel).
        void DesplegarUnidad(Dictionary<string, object?> baseCard, string id)
        {
            var nu = NuevaUnidad(baseCard, id, botUid, zona);
            var inst = M.Str(M.Get(nu, "instanceId"));
            ownUnits.Add((miCuartel!, nu, inst));
            recienInst.Add(inst);
        }

        // ── RESERVA DE EVOLUCIÓN (v9) ──────────────────────────────────────────
        // Antes la evolución se pagaba con lo que SOBRABA tras desplegar: con
        // ~15 de ingreso, dos cartas base (14) lo dejaban a cero y el bot casi
        // nunca evolucionaba (partidas de estudio: humano 66% de cartas
        // evolucionadas y 55 de poder por carta; bots 0-32% y 11-35). Ahora,
        // ANTES de desplegar, se elige qué unidades del tablero evolucionan este
        // turno (mejor ganancia de poder por energía) y se reserva su coste; el
        // despliegue gasta el resto. Con ejército ya formado y sin urgencia, si
        // no llega para ninguna, AHORRA (no diluye la energía en cartas base)
        // cuando ya tiene al menos la mitad del coste de la más rentable.
        // v10: la evolución YA NO se apaga por estar amenazado (evolucionar al
        // defensor es la mejor defensa; PlanificadorDefensivo hace justo eso).
        // Solo se apaga con amenaza GRAVE, donde la energía va a reclutar.
        var planEvolucion = new List<(string inst, string coord, int coste, int poderEvo)>();
        int reservaEvolucion = 0;
        if (!amenazaGrave && !remontada && _maxEvoluciones > 0)
        {
            var candidatasEvo = new List<(string inst, string coord, int coste, int poderEvo, double ganancia)>();
            foreach (var u in ownUnits)
            {
                if (Mov(u.card) <= 0) continue;   // estáticas: no se reserva para ellas
                var idEvoU = M.Str(M.Get(u.card, "IdEvolucion", "idEvolucion"));
                int costeEvoU = M.Int(M.Get(u.card, "Evolucion", "evolucion"));
                if (idEvoU == "" || costeEvoU <= 0) continue;
                if (!ctx.Evoluciones.TryGetValue(idEvoU, out var evoCardU)) continue;
                if (!CanLand(u.coord, Tipo(evoCardU), terreno)) continue;
                int poderEvoU = Fuerza(evoCardU) + Defensa(evoCardU);
                int gananciaU = poderEvoU - (Fuerza(u.card) + Defensa(u.card));
                if (gananciaU <= 0) continue;
                candidatasEvo.Add((u.inst, u.coord, costeEvoU, poderEvoU, gananciaU / (double)costeEvoU));
            }
            foreach (var c in candidatasEvo.OrderByDescending(ce => ce.ganancia).ThenByDescending(ce => ce.poderEvo))
            {
                if (planEvolucion.Count >= _maxEvoluciones) break;
                if (reservaEvolucion + c.coste > energia) continue;
                planEvolucion.Add((c.inst, c.coord, c.coste, c.poderEvo));
                reservaEvolucion += c.coste;
            }
            if (planEvolucion.Count == 0 && candidatasEvo.Count > 0
                && ownUnits.Count(u => Mov(u.card) > 0) >= UnidadesParaAhorrarEvolucion)
            {
                var objetivoEvo = candidatasEvo.OrderByDescending(ce => ce.ganancia).First();
                if (energia * 2 >= objetivoEvo.coste) reservaEvolucion = energia;   // ahorra este turno
            }
            if (reservaEvolucion > 0)
                Console.WriteLine($"[WZ][bot {botUid}] RESERVA EVOLUCIÓN {reservaEvolucion} ({planEvolucion.Count} planificadas)");
        }

        // ── RESERVA DE ACCIÓN (v9b, corregida en v10) ─────────────────────────
        // El servidor cobra las acciones EN LA RESOLUCIÓN sobre la energía que
        // queda tras los despliegues/evoluciones (pre-pagados vía ActualizarStats).
        // Hasta v9b el bot ADEMÁS sumaba el coste de la acción a EnergiaGastada:
        // doble cobro, y la acción caía por "energías insuficientes" (TrTl T14).
        // Eso ya no ocurre (ver BotMove.EnergiaGastada), pero la reserva sigue
        // teniendo sentido: si el despliegue vacía la energía, la acción no se
        // puede pagar. Se reserva el coste de la carta de acción que el bot va
        // a poder jugar (la más barata pagable; con preferencia por un DISPARO
        // si hay presa), acotada a la mitad de la energía.
        int reservaAccion = 0;
        if (enemyByCoord.Count > 0 && !amenazaGrave && !continenteInvadido && !remontada)
        {
            var jugables = mano
                .Where(id => ctx.CatalogoMano.TryGetValue(id, out var b) && EsAccion(b) && Coste(b) <= energia)
                .Select(id => ctx.CatalogoMano[id])
                .ToList();
            if (jugables.Count > 0)
            {
                var disparos = jugables.Where(EsDisparo).ToList();
                int coste = (disparos.Count > 0 ? disparos : jugables).Min(Coste);
                reservaAccion = Math.Min(coste, Math.Max(0, energia / 2));
                if (reservaAccion > 0)
                    Console.WriteLine($"[WZ][bot {botUid}] RESERVA ACCIÓN {reservaAccion}");
            }
        }

        if (miCuartel != null && ctx.Cuartel != "")
        {
            // Reserva de energía para habilidades / cartas de acción. Se relaja a 0
            // si hay urgencia (amenaza GRAVE, invasión de continente o remontada).
            int reserva = (amenazaGrave || continenteInvadido || remontada) ? 0 : energia * _reservaPct / 100;
            reserva += reservaEvolucion;   // v9: la evolución se paga ANTES que el despliegue
            reserva += reservaAccion;      // v9b: y la acción ANTES que el despliegue (orden real de cobro)
            // Tope: las reservas nunca dejan al bot sin desplegar del todo.
            reserva = Math.Min(reserva, energia * 70 / 100);

            // (0a) COMPRA DE GENERAL (carta especial): fuerte atacando y defendiendo.
            //      Uno por partida (si muere no vuelve) → como mucho uno por turno.
            if (_comprarGenerales && ctx.GeneralesDisponibles.Count > 0)
            {
                var candidato = ctx.GeneralesDisponibles
                    .Where(g => Coste(g) <= energia)
                    .OrderByDescending(g => Fuerza(g) + Defensa(g))
                    .FirstOrDefault();
                // Sin urgencia, exigir cierto colchón para no vaciar la energía.
                // v10: colchón 1,5× → 1,2×. Un general (75-80 de poder por 45-50)
                // es la mejor compra del juego y los bots tardaban 10-16 turnos
                // en juntar el 1,5× (TrTl: Izanagi en T10, Izanami en T16).
                bool permite = candidato != null &&
                    (amenazado || continenteInvadido || energia >= Coste(candidato) * 6 / 5) &&
                    (amenazado || continenteInvadido || energia - Coste(candidato) >= reservaEvolucion);
                if (candidato != null && permite)
                {
                    int coste = Coste(candidato);
                    var gid = M.Str(M.Get(candidato, "id"));
                    DesplegarUnidad(candidato, gid);
                    energia -= coste; gastado += coste; especialComprada = gid;
                    Console.WriteLine($"[WZ][bot {botUid}] COMPRA GENERAL {M.Str(M.Get(candidato, "Nombre", "nombre"))} ({gid}) por {coste}");
                }
            }

            // (0b) DESPLIEGUE de unidades: las MÁS POTENTES primero, hasta _maxDeploys.
            //      Ya NO se limita por defensores del cuartel: las cartas salen a
            //      jugar en la Fase 1. Se conserva una reserva salvo urgencia.
            // En el ARRANQUE prioriza MOVIMIENTO para llegar antes a las celdas de
            // energía (isla central, rayos, continente ajeno); superado el arranque,
            // vuelve a priorizar potencia. Empatados por movimiento, desempata la potencia.
            const int arranqueTurnos = 4;                 // tunable
            bool arranque = ctx.Turno <= arranqueTurnos;
            var ordenadas = mano
                .Where(id => ctx.CatalogoMano.ContainsKey(id)
                             && !EsAccion(ctx.CatalogoMano[id])
                             && !EsEstatica(ctx.CatalogoMano[id]))
                .OrderByDescending(id =>
                {
                    var c = ctx.CatalogoMano[id];
                    int potencia = Fuerza(c) + Defensa(c);
                    return arranque ? Mov(c) * 100 + potencia : potencia;
                })
                .ToList();
            foreach (var id in ordenadas)
            {
                if (desplegadas >= _maxDeploys) break;
                var baseCard = ctx.CatalogoMano[id];
                int coste = M.Int(M.Get(baseCard, "Coste", "coste"));
                if (energia - coste < reserva) continue;
                DesplegarUnidad(baseCard, id);
                energia -= coste; gastado += coste; desplegadas++;
                mano.Remove(id);
            }
        }

        // Mis coords tras desplegar (para las cartas de acción de potenciación).
        misCoords = ownUnits.Select(u => u.coord).ToList();

        // ── FASE 1: destinos de movimiento (sobre TODAS mis unidades) ──────────
        var destino = new Dictionary<string, string>();
        foreach (var u in ownUnits) destino[u.inst] = u.coord; // por defecto, quieto
        var asignada = new HashSet<string>();

        // ── Intención GLOBAL del turno (v8) ────────────────────────────────────
        // EMPUJE FINAL: con energía masiva, seguir farmeando es acumular en balde.
        bool empujeFinal = ctx.Energia >= UmbralEmpujeFinal;
        // OBJETIVO DE ASEDIO (v9): el cuartel enemigo más barato de tomar; con
        // preferencia por el del LÍDER si no sale mucho más caro (v9b).
        string? objetivoAsedio = ElegirObjetivoAsedio(
            ownUnits, miCuartel, enemyByCoord, enemyCuarteles, filas, columnas, cuartelLider,
            _objetivoAsedioPrevio);
        _objetivoAsedioTurno = objetivoAsedio;
        _objetivoAsedioPrevio = objetivoAsedio;
        // PUNTO DE REUNIÓN OFENSIVO (v9): las piezas sin presa siguen formando
        // masa (no gotean solas a un cuartel), pero la masa se forma en la celda
        // propia más fuerte DEL FRENTE (las más cercanas al objetivo de asedio),
        // no en la más fuerte del mapa, que casi siempre estaba en casa y tiraba
        // del ejército hacia atrás.
        string? puntoReunion = PuntoReunionOfensivo(ownUnits, miCuartel, objetivoAsedio, filas, columnas);
        if (objetivoAsedio != null)
            Console.WriteLine($"[WZ][bot {botUid}] objetivo de asedio {objetivoAsedio}, reunión en {puntoReunion ?? "-"}");

        // Cuarteles enemigos que el asalto NO pudo tomar este turno (defensor
        // demasiado apilado). Candidatos a romperse con un DISPARO LEJANO en la
        // fase de cartas de acción (limpia a los defensores; se entra al turno
        // siguiente sobre el cuartel ya vacío).
        var cuartelesAtrincherados = new HashSet<string>();

        // Mayor stack enemigo que puede caer sobre `c` el próximo turno, en PODER
        // (F+D, con evolución rival pagable; v10 vía ReglasEntrada). Misma
        // filosofía de amenaza realista que ExpuestoASalida: el stack mayor
        // decide, no la suma de todo el mapa.
        int MayorStackEnemigoQueAlcanza(string c) => ReglasEntrada.MayorStackQueAlcanza(reglas, c);

        // (b) DEFENSA PROPORCIONAL, BARATA Y ANTES DE LA OFENSIVA (v10).
        //     Regla del juego: el cuartel aguanta si (F+D de la guarnición) + 40
        //     ≥ (F+D del asalto). Se ancla la guarnición MÍNIMA que aguanta la
        //     amenaza realista (`amenazaPoder`: mayor stack que llega + mitad
        //     del resto, con evolución rival pagable), eligiendo las piezas de
        //     mejor PERFIL DEFENSIVO (lentas y baratas; un general o una
        //     evolucionada rápida valen mucho más fuera), y el RESTO sale a
        //     farmear / cazar / asediar. Antes este bloque iba DESPUÉS de la
        //     ofensiva, comparaba la fuerza entrante TOTAL contra la defensa
        //     anclada y ordenaba por potencia: anclaba el ejército entero (y al
        //     general) turno tras turno, con el ingreso en 3 (wlDM, TrTl).
        //     Si la amenaza es GRAVE (ni con todo lo que llega se aguanta) se
        //     ancla todo lo que llega y se recluta de emergencia, como antes.
        if (amenazado && miCuartel != null)
        {
            string casa = miCuartel;   // no-nulo dentro del bloque (lambdas)
            var defensoras = ownUnits
                .Where(u => !asignada.Contains(u.inst))
                .Where(u => u.coord == casa ||
                            Alcanzables(u.coord, Mov(u.card), Tipo(u.card), terreno, filas, columnas).Contains(casa))
                .OrderByDescending(u => PerfilGuarnicion(u.card))
                .ToList();

            int poderCasa = UmbralCuartel;                       // bono del cuartel propio
            int minPiezas = Math.Min(1, defensoras.Count);       // con amenaza, alguien se queda
            int ancladas = 0;
            foreach (var u in defensoras)
            {
                bool cubierto = poderCasa >= amenazaPoder && ancladas >= minPiezas;
                if (cubierto) break;
                destino[u.inst] = miCuartel; asignada.Add(u.inst);
                poderCasa += Fuerza(u.card) + Defensa(u.card); ancladas++;
            }

            // REFUERZO DE EMERGENCIA (amenaza GRAVE): si con todo lo que llega a
            // casa NO se aguanta, DESPLEGAR cartas nuevas directamente sobre el
            // cuartel, SIN el tope de despliegue. Perder el cuartel es perder la
            // partida. Se prefiere el poder más BARATO (más defensa por energía).
            if (poderCasa < amenazaPoder && ctx.Cuartel != "")
            {
                var refuerzos = mano
                    .Where(id => ctx.CatalogoMano.ContainsKey(id)
                                 && !EsAccion(ctx.CatalogoMano[id])
                                 && !EsEstatica(ctx.CatalogoMano[id])
                                 && CanLand(casa, Tipo(ctx.CatalogoMano[id]), terreno))
                    .OrderByDescending(id =>
                    {
                        var c = ctx.CatalogoMano[id];
                        return (Fuerza(c) + Defensa(c)) / (double)Math.Max(1, Coste(c));
                    })
                    .ThenByDescending(id => Fuerza(ctx.CatalogoMano[id]) + Defensa(ctx.CatalogoMano[id]))
                    .ToList();
                foreach (var id in refuerzos)
                {
                    if (poderCasa >= amenazaPoder) break;
                    var baseCard = ctx.CatalogoMano[id];
                    int coste = M.Int(M.Get(baseCard, "Coste", "coste"));
                    if (coste > energia) continue;
                    DesplegarUnidad(baseCard, id);
                    var instNuevo = ownUnits[^1].inst;      // la unidad recién añadida
                    destino[instNuevo] = miCuartel; asignada.Add(instNuevo);
                    energia -= coste; gastado += coste; desplegadas++;
                    mano.Remove(id);
                    poderCasa += Fuerza(baseCard) + Defensa(baseCard); ancladas++;
                }
            }

            if (ancladas > 0)
                Console.WriteLine($"[WZ][bot {botUid}] DEFIENDE CUARTEL {miCuartel}: {ancladas} unidades " +
                    $"(poder en casa {poderCasa} vs entrante {amenazaPoder}{(amenazaGrave ? ", GRAVE" : "")})");
        }

        // (a) ASALTO EN MANADA a un cuartel enemigo (DEFENDIDO o no). Reúne el grupo
        //     MÍNIMO (más fuertes primero) que, sumando fuerzas, GANA el combate del
        //     cuartel (el defensor suma +UmbralCuartel). Prioriza el más cercano.
        //     Por defecto solo si NO estamos amenazados (si nos atacan, defender es
        //     prioritario). El estilo AGRESIVO (_asaltoBajoAmenaza) remata cuarteles
        //     aunque le amenacen, pero SIEMPRE exigiendo que el grupo gane la toma.
        if (!amenazaGrave || _asaltoBajoAmenaza)
            foreach (var cuartelObj in enemyCuarteles
                        .OrderBy(c => miCuartel == null ? 0 : Manhattan(miCuartel, c, filas, columnas)))
            {
                var llegan = ownUnits
                    .Where(u => !asignada.Contains(u.inst))
                    .Where(u => Alcanzables(u.coord, Mov(u.card), Tipo(u.card), terreno, filas, columnas).Contains(cuartelObj))
                    .OrderByDescending(u => Fuerza(u.card) + Defensa(u.card))
                    .ToList();
                if (llegan.Count == 0) continue;

                var grupo = new List<(string coord, Dictionary<string, object?> card, string inst)>();
                foreach (var u in llegan)
                {
                    grupo.Add(u);
                    if (GanaGrupo(grupo.Sum(g => Fuerza(g.card)), grupo.Sum(g => Defensa(g.card)),
                                  cuartelObj, enemyByCoord, enemyCuarteles, cuartelOwner, botUid)) break;
                }
                if (!GanaGrupo(grupo.Sum(g => Fuerza(g.card)), grupo.Sum(g => Defensa(g.card)),
                               cuartelObj, enemyByCoord, enemyCuarteles, cuartelOwner, botUid))
                {
                    // Ni todas juntas rematan el cuartel: no malgastar el asalto,
                    // pero marcarlo para intentar romperlo con un disparo lejano.
                    cuartelesAtrincherados.Add(cuartelObj);
                    continue;
                }

                foreach (var u in grupo) { destino[u.inst] = cuartelObj; asignada.Add(u.inst); }
                Console.WriteLine($"[WZ][bot {botUid}] ASALTO CUARTEL en manada: {grupo.Count} → {cuartelObj}");
            }

        // (aDef) CONTENER INTRUSOS: enemigos dentro de MI continente. Reúne el grupo
        //        mínimo que los bate y los envía. Es la respuesta defensiva que
        //        faltaba: cuando un rival entra con varias cartas, se le planta cara.
        foreach (var intruso in intrusos
                    .OrderByDescending(c => enemyByCoord[c].Sum(Coste)))
        {
            var llegan = ownUnits
                .Where(u => !asignada.Contains(u.inst))
                .Where(u => Alcanzables(u.coord, Mov(u.card), Tipo(u.card), terreno, filas, columnas).Contains(intruso))
                .OrderByDescending(u => Fuerza(u.card) + Defensa(u.card))
                .ToList();
            if (llegan.Count == 0) continue;

            var grupo = new List<(string coord, Dictionary<string, object?> card, string inst)>();
            foreach (var u in llegan)
            {
                grupo.Add(u);
                if (GanaGrupo(grupo.Sum(g => Fuerza(g.card)), grupo.Sum(g => Defensa(g.card)),
                              intruso, enemyByCoord, enemyCuarteles, cuartelOwner, botUid, _sesgoAtaque)) break;
            }
            if (!GanaGrupo(grupo.Sum(g => Fuerza(g.card)), grupo.Sum(g => Defensa(g.card)),
                           intruso, enemyByCoord, enemyCuarteles, cuartelOwner, botUid, _sesgoAtaque))
                continue; // aún no lo batimos; el movimiento individual convergerá a él

            foreach (var u in grupo) { destino[u.inst] = intruso; asignada.Add(u.inst); }
            Console.WriteLine($"[WZ][bot {botUid}] CONTIENE INTRUSO: {grupo.Count} → {intruso}");
        }

        // (a2) CAZA EN GRUPO de stacks enemigos valiosos (fuera del cuartel y que no
        //      sean ya intrusos ni cuarteles). Varias unidades que en solitario
        //      perderían pueden ganar JUNTAS: se concentra el mínimo que gana.
        if (!amenazaGrave || _asaltoBajoAmenaza)
        {
            // v9b: el material del LÍDER vale más (se le frena a él, no al que ya
            // va último): su coste cuenta doble al ordenar las presas.
            var objetivosGrupo = enemyByCoord.Keys
                .Where(c => !enemyCuarteles.Contains(c) && !intrusos.Contains(c))
                .OrderByDescending(c => enemyByCoord[c].Sum(Coste)
                    * (liderRival != null && enemyByCoord[c].Any(e => M.Str(M.Get(e, "ownerUid")) == liderRival) ? 2 : 1))
                .Take(_topObjetivosGrupo);
            int cazasLanzadas = 0;
            foreach (var celdaObj in objetivosGrupo)
            {
                if (cazasLanzadas >= _maxCazasGrupo) break; // tope de asaltos coordinados/turno
                var candidatas = ownUnits
                    .Where(u => !asignada.Contains(u.inst))
                    .Where(u => Alcanzables(u.coord, Mov(u.card), Tipo(u.card), terreno, filas, columnas).Contains(celdaObj))
                    .OrderByDescending(u => Fuerza(u.card) + Defensa(u.card))
                    .ToList();
                if (candidatas.Count < 2) continue; // en solitario ya lo cubre DecidirMovimiento

                var grupo = new List<(string coord, Dictionary<string, object?> card, string inst)>();
                foreach (var u in candidatas)
                {
                    grupo.Add(u);
                    if (GanaGrupo(grupo.Sum(g => Fuerza(g.card)), grupo.Sum(g => Defensa(g.card)),
                                  celdaObj, enemyByCoord, enemyCuarteles, cuartelOwner, botUid, _sesgoAtaque)) break;
                }
                if (!GanaGrupo(grupo.Sum(g => Fuerza(g.card)), grupo.Sum(g => Defensa(g.card)),
                               celdaObj, enemyByCoord, enemyCuarteles, cuartelOwner, botUid, _sesgoAtaque))
                    continue; // ni todas juntas ganan: no suicidarse

                foreach (var u in grupo) { destino[u.inst] = celdaObj; asignada.Add(u.inst); }
                cazasLanzadas++;
                Console.WriteLine($"[WZ][bot {botUid}] CAZA EN GRUPO: {grupo.Count} unidades → {celdaObj}");
            }
        }

        // (a1) MARCHA DE ASEDIO (v9). El asalto (a) solo actúa con unidades que
        //      ALCANZAN el cuartel este turno; con el ejército lejos no llegaba
        //      nadie y el cuartel se marcaba "atrincherado" sin hacer nada más
        //      (0 conquistas de bot en 4 partidas de estudio). Aquí, si el grupo
        //      móvil reúne poder para GANAR la toma del cuartel objetivo, marcha
        //      hacia él EN BOLA: todos hacia la misma etapa (paso del más lento
        //      desde el punto de reunión), sin pisar celdas enemigas ni celdas
        //      donde un stack mayor que el grupo pueda caer. Las unidades que ya
        //      farmean entran las últimas (solo si hacen falta para el poder).
        if (objetivoAsedio != null && (!amenazaGrave || _asaltoBajoAmenaza)
            && !ownUnits.Any(u => asignada.Contains(u.inst) && destino[u.inst] == objetivoAsedio!))
        {
            string objAsedio = objetivoAsedio;   // no-nulo dentro del bloque (lambdas)
            // v10: guarnición en poder PESIMISTA (evolución rival pagable) más el
            // refuerzo que el dueño puede desplegar: la misma vara que usará el
            // asalto (a), para no emprender marchas con masa insuficiente.
            int guarnicion = enemyByCoord.TryGetValue(objAsedio, out var gCartas)
                ? ReglasEntrada.PoderPesimista(reglas, gCartas) : 0;
            var (refF, refD) = ReglasEntrada.RefuerzoCuartel(
                reglas, cuartelOwner.GetValueOrDefault(objAsedio, ""), ReglasEntrada.RefuerzoMaxEntrada);
            int necesario = (int)Math.Ceiling((guarnicion + refF + refD + UmbralCuartel) * MargenAsedio);

            // v10: los NAVALES también asedian (los cuarteles son anfibios en los
            // mapas clásicos: una Manta entró en F1 y un Megalodón humano tomó
            // A10 en wlDM). Lo que se exige es CAMINO REAL por terreno hasta el
            // objetivo, no el tipo de la carta. En TrTl los dos Megalodones
            // (82 de poder) quedaban fuera de la marcha por esta exclusión.
            var candidatas = ownUnits
                .Where(u => !asignada.Contains(u.inst) && Mov(u.card) > 0)
                .Where(u => ReglasEntrada.HayCamino(u.coord, objAsedio, Tipo(u.card), terreno, filas, columnas))
                .OrderBy(u => Farm(u.coord) > 0 ? 1 : 0)                     // las que no farmean, primero
                .ThenByDescending(u => Fuerza(u.card) + Defensa(u.card))
                .ToList();

            var grupo = new List<(string coord, Dictionary<string, object?> card, string inst)>();
            int poderGrupo = 0;
            foreach (var u in candidatas)
            {
                grupo.Add(u); poderGrupo += Fuerza(u.card) + Defensa(u.card);
                if (poderGrupo >= necesario && grupo.Count >= MinGrupoAsedio) break;
            }

            if (poderGrupo < necesario || grupo.Count < MinGrupoAsedio)
            {
                Console.WriteLine($"[WZ][bot {botUid}] asedio {objetivoAsedio}: poder móvil {poderGrupo} < necesario {necesario}, sin marcha");
            }
            else
            {
                string cabeza = puntoReunion
                    ?? grupo.OrderBy(u => Manhattan(u.coord, objAsedio, filas, columnas)).First().coord;
                // Paso de la bola: la MEDIANA de movimiento del grupo (acotada a 1-3).
                // Con el mínimo, un solo lento (mov 1) frenaba a toda la bola; con
                // la mediana los rápidos avanzan y los lentos se reincorporan por
                // el punto de reunión (siempre la celda fuerte más adelantada).
                var movs = grupo.Select(u => Mov(u.card)).OrderBy(m => m).ToList();
                int pasoBola = Math.Clamp(movs[movs.Count / 2], 1, 3);
                string etapa = TerrenoUtil.PasoHaciaTerreno(cabeza, objAsedio, pasoBola, true, false, terreno, filas, columnas);
                // La etapa nunca es una celda enemiga ni un cuartel: la toma la hace (a).
                if (enemyCuarteles.Contains(etapa) || enemyByCoord.ContainsKey(etapa) || cuartelCoords.Contains(etapa))
                    etapa = cabeza;

                int marchan = 0;
                // Sin etapa fuera de mi cuartel no hay marcha (evita "avanzar" hacia casa).
                if (etapa != miCuartel)
                    foreach (var u in grupo)
                    {
                        var reach = Alcanzables(u.coord, Mov(u.card), Tipo(u.card), terreno, filas, columnas);
                        reach.Add(u.coord);
                        var paso = reach
                            .Where(c => !enemyByCoord.ContainsKey(c))
                            .Where(c => !cuartelCoords.Contains(c) || cuartelOwner.GetValueOrDefault(c) == botUid)
                            .Where(c => MayorStackEnemigoQueAlcanza(c) <= poderGrupo)
                            .OrderBy(c => Manhattan(c, etapa, filas, columnas))
                            .ThenBy(c => Manhattan(c, objAsedio, filas, columnas))
                            .FirstOrDefault();
                        if (paso == null) continue;   // sin paso seguro: que decida el flujo normal
                        destino[u.inst] = paso; asignada.Add(u.inst); marchan++;
                    }
                if (marchan > 0)
                    Console.WriteLine($"[WZ][bot {botUid}] MARCHA DE ASEDIO: {marchan}/{grupo.Count} unidades (poder {poderGrupo} vs {necesario}) → etapa {etapa} hacia {objAsedio}");
            }
        }

        // (a3) EL CENTRO SE TOMA Y SE SOSTIENE EN GRUPO (v8). La isla central es
        //      energía + el único paso terrestre entre continentes y el evaluador
        //      ya la premia (W_CENTRO), pero NINGÚN candidato la disputaba: el
        //      movimiento individual la evitaba (celdas "expuestas") y el modo
        //      farmeo muere pasada la apertura. Resultado observado en las
        //      partidas de estudio: los bots no luchan por el centro. Aquí:
        //      1) se ANCLA a las unidades propias que YA están en la isla y
        //         pueden sostenerla (dejar de regalar el centro), y
        //      2) si el rival controla tantas o más celdas de isla que nosotros,
        //         se envían hasta 2 GRUPOS mínimos que GANAN a las mejores celdas
        //         (libres o batibles), siempre con apoyo mutuo: a una celda libre
        //         se va como mínimo en pareja para no regalar una carta suelta.
        if ((!amenazaGrave || _asaltoBajoAmenaza) && ctx.IslaCentral.Count > 0)
        {
            // Mayor stack enemigo que puede caer sobre `c` el próximo turno
            // (poder pesimista, v10 vía ReglasEntrada).
            int MayorStackQueAlcanza(string c) => ReglasEntrada.MayorStackQueAlcanza(reglas, c);

            // 1) Anclar el centro que ya tenemos (si la pieza puede sostenerlo).
            int centroAnclado = 0;
            foreach (var u in ownUnits)
            {
                if (asignada.Contains(u.inst) || !ctx.IslaCentral.Contains(u.coord)) continue;
                bool peleaPerdida = enemyByCoord.ContainsKey(u.coord) &&
                    !GanoAtacando(Fuerza(u.card), Defensa(u.card), u.coord, enemyByCoord, enemyCuarteles, cuartelOwner, botUid);
                if (peleaPerdida) continue;
                if (MayorStackQueAlcanza(u.coord) > Fuerza(u.card) + Defensa(u.card))
                    continue; // la barrerían: que decida el flujo normal (retirada)
                destino[u.inst] = u.coord; asignada.Add(u.inst); centroAnclado++;
            }

            // 2) ¿Hace falta disputar más centro?
            int centroEnemigo = enemyByCoord.Keys.Count(ctx.IslaCentral.Contains);
            if (centroAnclado == 0 || centroAnclado < centroEnemigo)
            {
                var celdasObjetivo = ctx.IslaCentral
                    .Where(c => !cuartelCoords.Contains(c))
                    .Where(c => !ownUnits.Any(u => destino[u.inst] == c))      // sin presencia nuestra ya asignada
                    .OrderBy(c => enemyByCoord.ContainsKey(c) ? 1 : 0)        // libres primero
                    .ThenBy(c => ownUnits.Where(u => !asignada.Contains(u.inst))
                                         .Select(u => Manhattan(u.coord, c, filas, columnas))
                                         .DefaultIfEmpty(int.MaxValue).Min())
                    .ToList();

                int enviados = 0;
                foreach (var celdaC in celdasObjetivo)
                {
                    if (enviados >= 2) break;                                 // tope de empujes al centro/turno
                    var candidatas = ownUnits
                        .Where(u => !asignada.Contains(u.inst))
                        .Where(u => Alcanzables(u.coord, Mov(u.card), Tipo(u.card), terreno, filas, columnas).Contains(celdaC))
                        .OrderByDescending(u => Fuerza(u.card) + Defensa(u.card))
                        .ToList();
                    if (candidatas.Count == 0) continue;

                    var grupo = new List<(string coord, Dictionary<string, object?> card, string inst)>();
                    foreach (var u in candidatas)
                    {
                        grupo.Add(u);
                        bool gana = GanaGrupo(grupo.Sum(g => Fuerza(g.card)), grupo.Sum(g => Defensa(g.card)),
                                              celdaC, enemyByCoord, enemyCuarteles, cuartelOwner, botUid, _sesgoAtaque);
                        if (gana && (enemyByCoord.ContainsKey(celdaC) || grupo.Count >= 2)) break;
                    }
                    bool ganaFinal = GanaGrupo(grupo.Sum(g => Fuerza(g.card)), grupo.Sum(g => Defensa(g.card)),
                                               celdaC, enemyByCoord, enemyCuarteles, cuartelOwner, botUid, _sesgoAtaque);
                    if (!ganaFinal) continue;
                    if (!enemyByCoord.ContainsKey(celdaC) && grupo.Count < 2)
                        continue; // sin pareja no se manda una carta suelta al centro
                    if (MayorStackQueAlcanza(celdaC) > grupo.Sum(g => Fuerza(g.card) + Defensa(g.card)))
                        continue; // caerían al contragolpe: mejor esperar más masa

                    foreach (var u in grupo) { destino[u.inst] = celdaC; asignada.Add(u.inst); }
                    enviados++;
                    Console.WriteLine($"[WZ][bot {botUid}] TOMA DEL CENTRO: {grupo.Count} unidades → {celdaC}");
                }
            }
        }

        // (c) MOVIMIENTO INDIVIDUAL del resto (caza / farmeo / avance, con caza
        //     predictiva). Las cartas recién desplegadas SALEN aquí hacia energía o
        //     frente. Si mi continente está invadido, las unidades de casa
        //     convergen hacia el intruso más cercano para contenerlo.
        foreach (var u in ownUnits)
        {
            if (asignada.Contains(u.inst)) continue;
            destino[u.inst] = DecidirMovimiento(
                u.coord, u.card, terreno, filas, columnas,
                enemyByCoord, enemyCuarteles, cuartelOwner, cuartelCoords, botUid, Farm, miCuartel,
                predEnemigo, atractoresEnergia, intrusos, miContinente,
                puntoReunion, empujeFinal, TurnosRayo);
        }

        // (d) ANTI-APILAMIENTO: no dejar más de _maxDefensoresCuartel unidades sobre
        //     mi cuartel (un AoE a distancia las barrería a todas). Solo se salta
        //     con amenaza GRAVE (ahí apilar es preferible a perder el cuartel).
        //     v10: las que se QUEDAN son las de mejor PERFIL DEFENSIVO (lentas y
        //     baratas), nunca las más potentes: el general Izanagi (80 de poder)
        //     pasó 6 de 7 turnos de guarnición en TrTl por la regla anterior
        //     ("las veteranas más potentes se quedan"). Las ancladas por la
        //     defensa (b) no se tocan y cuentan para el tope; las recién
        //     desplegadas siguen siendo las primeras en salir.
        if (miCuartel != null && !amenazaGrave)
        {
            int ancladasDefensa = ownUnits.Count(u => asignada.Contains(u.inst) && destino[u.inst] == miCuartel);
            int huecos = Math.Max(0, _maxDefensoresCuartel - ancladasDefensa);
            var enMiCuartel = ownUnits
                .Where(u => destino[u.inst] == miCuartel && !asignada.Contains(u.inst))
                .OrderByDescending(u => recienInst.Contains(u.inst) ? 0 : 1)
                .ThenByDescending(u => PerfilGuarnicion(u.card))
                .ToList();
            foreach (var u in enMiCuartel.Skip(huecos))
                destino[u.inst] = ReubicarFueraDeCuartel(
                    u.coord, u.card, miCuartel, terreno, filas, columnas,
                    enemyByCoord, enemyCuarteles, cuartelOwner, botUid,
                    objetivoAsedio, puntoReunion, Farm);
        }

        // (e) GUARNICIÓN MÍNIMA: si tras mover el cuartel quedaría vacío y aún hay
        //     enemigos en juego, retén en casa a la unidad de mejor perfil
        //     defensivo que ya estuviera allí (v10: antes era la más potente, y
        //     se quedaba el Ala Esmeralda / el Elefante mientras las Mantas
        //     salían). Evita regalar el cuartel a un rush del rival.
        if (miCuartel != null && enemyByCoord.Count > 0 &&
            !ownUnits.Any(u => destino[u.inst] == miCuartel))
        {
            var guard = ownUnits
                .Where(u => u.coord == miCuartel && Mov(u.card) > 0)
                .OrderByDescending(u => PerfilGuarnicion(u.card))
                .FirstOrDefault();
            if (guard.card != null) destino[guard.inst] = miCuartel;
        }

        // (f) ANCLAJE PARA EVOLUCIONAR (v9): las unidades elegidas en la reserva
        //     se quedan en su celda (regla del juego: la que se mueve no
        //     evoluciona), salvo que estén comprometidas en un asalto/defensa/
        //     marcha (manda eso) o que quedarse quietas sea morir.
        var evolucionPlanificada = new HashSet<string>();
        foreach (var (instEvo, coordEvo, costeEvoP, poderEvoP) in planEvolucion)
        {
            if (asignada.Contains(instEvo)) continue;
            if (!destino.TryGetValue(instEvo, out var dPlan)) continue;
            if (dPlan != coordEvo)
            {
                if (MayorStackEnemigoQueAlcanza(coordEvo) > poderEvoP) continue;   // la barrerían
                destino[instEvo] = coordEvo;
            }
            evolucionPlanificada.Add(instEvo);
        }

        // ── COLOCACIÓN + EVOLUCIONES ───────────────────────────────────────────
        // Una carta que se MOVIÓ no evoluciona y una recién desplegada tampoco
        // (acaba de entrar). Solo evolucionan las que se quedan en su celda.
        var evolucionadas = new HashSet<string>();
        int evos = 0;
        // v10: la única reserva que frena una evolución es la de la carta de
        // acción ya planificada (orden real de cobro). Antes, con amenaza, se
        // guardaba el 40 % de la energía y el defensor no evolucionaba justo
        // cuando más falta hacía.
        int reservaEvo = reservaAccion;

        // v9: las evoluciones planificadas (reserva) van primero en el orden.
        foreach (var u in ownUnits.OrderByDescending(x => evolucionPlanificada.Contains(x.inst) ? 1 : 0).ToList())
        {
            var destinoU = destino[u.inst];
            Dictionary<string, object?> aColocar = new(u.card);

            if (evos < _maxEvoluciones && destinoU == u.coord && !recienInst.Contains(u.inst))
            {
                var idEvo = M.Str(M.Get(u.card, "IdEvolucion", "idEvolucion"));
                int costeEvo = M.Int(M.Get(u.card, "Evolucion", "evolucion"));
                if (idEvo != "" && costeEvo > 0 && energia - costeEvo >= reservaEvo
                    && ctx.Evoluciones.TryGetValue(idEvo, out var evoCard)
                    && CanLand(u.coord, Tipo(evoCard), terreno)
                    && (Fuerza(evoCard) + Defensa(evoCard)) > (Fuerza(u.card) + Defensa(u.card)))
                {
                    var zonaU = M.Str(M.Get(u.card, "ownerZone"));
                    if (zonaU == "") zonaU = zona;
                    aColocar = NuevaUnidad(evoCard, idEvo, botUid, zonaU);
                    energia -= costeEvo; gastado += costeEvo; evos++;
                    evolucionadas.Add(u.inst);
                    Console.WriteLine($"[WZ][bot {botUid}] EVOLUCIONA en {u.coord}: " +
                        $"{M.Str(M.Get(u.card, "Nombre", "nombre"))} → {M.Str(M.Get(evoCard, "Nombre", "nombre"))} (-{costeEvo})");
                }
            }

            Place(destinoU, aColocar);
        }

        // ── FASE ESTÁTICAS: fortificar posiciones propias ──────────────────────
        // Las estáticas (Condicion==3) NO se mueven y NO pueden ir al cuartel:
        // solo pueden colocarse sobre una celda propia donde ya había una carta
        // que NO se mueve este turno (mismo anclaje que valida el cliente y el
        // servidor). Son piezas DEFENSIVAS clave: refuerzan y "clavan" una
        // posición. El bot las coloca en sus celdas más valiosas de defender:
        // las amenazadas por el enemigo, los pasos de acceso a su cuartel y las
        // celdas de farmeo (rayo / isla central).
        var estaticasMano = mano
            .Where(id => ctx.CatalogoMano.ContainsKey(id) && EsEstatica(ctx.CatalogoMano[id]))
            .ToList();
        if (estaticasMano.Count > 0 && miCuartel != null)
        {
            // Anclas válidas: celdas (≠ cuartel) donde una unidad PROPIA que ya
            // estaba en tablero se QUEDA este turno (destino == su propia celda).
            var anclas = ownUnits
                .Where(u => !recienInst.Contains(u.inst)
                            && destino[u.inst] == u.coord
                            && u.coord != miCuartel)
                .Select(u => u.coord)
                .Distinct()
                .ToList();

            // Valor defensivo de fortificar una celda:
            //   · amenaza entrante (fuerza enemiga que puede caer ahí) → lo más
            //     importante que defender;
            //   · cercanía al cuartel → anillo de contención de acceso;
            //   · celdas de farmeo (rayo / isla) → retener economía.
            int ValorDefensa(string coord)
            {
                int v = MaxAtaqueEntrante(coord, enemyByCoord, terreno, filas, columnas) * 3;
                v += Math.Max(0, 20 - Manhattan(coord, miCuartel!, filas, columnas) * 2);
                if (ctx.Rayos.Contains(coord)) v += 8;
                if (ctx.IslaCentral.Contains(coord)) v += 5;
                return v;
            }
            anclas.Sort((a, b) => ValorDefensa(b).CompareTo(ValorDefensa(a)));

            // Reserva de energía salvo urgencia; más estáticas si hay presión.
            int reservaEst = (amenazado || continenteInvadido) ? 0 : energia * _reservaPct / 100;
            // v9: si este turno se AHORRA para evolucionar, las torretas no se lo comen.
            if (!amenazado && !continenteInvadido && planEvolucion.Count == 0) reservaEst += reservaEvolucion;
            int maxEstaticas = (amenazado || continenteInvadido) ? 2 : 1;
            int colocadas = 0;
            foreach (var coord in anclas)
            {
                if (colocadas >= maxEstaticas) break;
                // Sin urgencia, una torreta solo vale en la ISLA CENTRAL o donde de
                // verdad entra fuerza enemiga (v9). Antes bastaba estar a <=7 del
                // cuartel: un bot gastó el 44% de su energía en 14 torretas que
                // hicieron 3 combates en 44 turnos; otro, 112 de energía en
                // torretas con 0 combates.
                if (!amenazado && !continenteInvadido)
                {
                    bool valeLaPena = ctx.IslaCentral.Contains(coord)
                        || MaxAtaqueEntrante(coord, enemyByCoord, terreno, filas, columnas) > 0;
                    if (!valeLaPena) continue;
                }
                // Una sola estática por celda: un AoE a distancia barrería juntas
                // varias torretas apiladas.
                string? elegido = null;
                foreach (var id in estaticasMano)
                {
                    var bc = ctx.CatalogoMano[id];
                    if (!CanLand(coord, Tipo(bc), terreno)) continue;   // terreno
                    if (energia - Coste(bc) < reservaEst) continue;      // presupuesto
                    // v9: sin urgencia, una torreta nunca se lleva más de la mitad
                    // de la energía (queda para evolucionar / desplegar).
                    if (!amenazado && !continenteInvadido && Coste(bc) * 2 > energia) continue;
                    elegido = id; break;
                }
                if (elegido == null) continue;
                var baseCard = ctx.CatalogoMano[elegido];
                Place(coord, NuevaUnidad(baseCard, elegido, botUid, zona));
                int coste = Coste(baseCard);
                energia -= coste; gastado += coste;
                mano.Remove(elegido); estaticasMano.Remove(elegido);
                colocadas++;
                Console.WriteLine($"[WZ][bot {botUid}] ESTÁTICA defensiva en {coord} (valor {ValorDefensa(coord)})");
            }
        }
        // Mejor celda propia (NUNCA el cuartel: escudarlo está prohibido) para un
        // escudo: una posición rentable/adelantada que quieras conservar y que esté
        // amenazada. El escudo bloquea acciones y movimiento en esa celda.
        string? celdaEscudo = null;
        {
            int mejor = int.MinValue;
            foreach (var (coord, cartasCelda) in celdas)
            {
                if (coord == miCuartel) continue;                 // prohibido escudar el cuartel
                int mias = cartasCelda.Count(c => M.Str(M.Get(c, "ownerUid")) == botUid);
                if (mias == 0) continue;
                int farm = Farm(coord);
                bool amenazada = enemyByCoord.Any(kv =>
                    Manhattan(kv.Key, coord, filas, columnas) <= Math.Max(1, kv.Value.Max(Mov)));
                // no malgastar un escudo caro: exige stack real, o celda muy rentable, o amenaza
                if (mias < 2 && farm < 7 && !amenazada) continue;
                int score = mias * 3 + farm + (amenazada ? 8 : 0);
                if (score > mejor) { mejor = score; celdaEscudo = coord; }
            }
        }
        // ── FASE 2: CARTAS DE ACCIÓN jugadas desde la mano ─────────────────────
        JugarCartasAccion(ctx, miCuartel, zona, amenazado, enemyByCoord, enemyCuarteles, misCoords,
                   ref energia, ref gastadoAcciones, mano, acciones, cuartelesAtrincherados, celdaEscudo);

        // ── FASE 3: HABILIDADES de unidades en tablero (solo las que no se movieron
        //    ni acaban de desplegarse). ────────────────────────────────────────
        int accHab = 0;
        foreach (var u in ownUnits)
        {
            if (accHab >= _maxAcciones) break;
            if (destino[u.inst] != u.coord) continue;
            if (evolucionadas.Contains(u.inst)) continue; // ya no es la misma carta
            if (recienInst.Contains(u.inst)) continue;    // recién desplegada

            int habId = M.Int(M.Get(u.card, "IdHabilidad", "idHabilidad"));
            if (!AccionesTacticas.Catalogo.TryGetValue(habId, out var hab)) continue;
            int coste = M.Int(M.Get(u.card, "CosteHabilidad", "costeHabilidad"));
            if (coste > energia) continue;
            if (EnEnfriamiento(u.card, ctx.Turno)) continue;

            var objetivos = ElegirObjetivos(hab, u.coord, filas, columnas, enemyByCoord, enemyCuarteles, miCuartel);
            if (objetivos.Count < hab.NumObjetivos) continue;
            objetivos = objetivos.Take(hab.NumObjetivos).ToList();

            acciones.Add(new Dictionary<string, object?>
            {
                ["habilidadId"] = habId,
                ["uid"] = botUid,
                ["zona"] = zona,
                ["origen"] = u.coord,
                ["objetivos"] = objetivos,
                ["turno"] = ctx.Turno,
                ["costePagado"] = coste,
            });
            energia -= coste; gastadoAcciones += coste; accHab++;
        }

        if (gastadoAcciones > 0)
            Console.WriteLine($"[WZ][bot {botUid}] acciones por {gastadoAcciones} (las cobra el servidor al resolver)");

        return new BotMove
        {
            Celdas = celdas,
            Acciones = acciones,
            ManoResultante = mano,
            EnergiaGastada = gastado,          // despliegues / evoluciones / generales / sacrificios
            EnergiaAcciones = gastadoAcciones, // NO se pre-paga: el servidor lo cobra (v10, sin doble cobro)
            EspecialComprada = especialComprada,
        };
    }

    // ── Cartas de acción (Condicion == 4) ──────────────────────────────────────
    // Una carta de acción se JUEGA desde la mano (no se despliega en el tablero):
    // lanza la habilidad `IdHabilidad` con origen = el cuartel del jugador, cuesta
    // su `Coste` normal (el que se ve en la mano) y se descarta tras usarse. El
    // servidor la aplica por habilidadId + objetivos; `cartaAccionId` marca la
    // carta a descartar de la mano.
    private static bool EsAccion(Dictionary<string, object?> baseCard)
        => AccionesTacticas.EsCartaAccion(baseCard);   // Opción A: fuente única

    /// v9b: ¿la carta lanza un DISPARO (destruye todas las cartas de una celda)?
    private static bool EsDisparo(Dictionary<string, object?> baseCard)
        => AccionesTacticas.Catalogo.TryGetValue(
               M.Int(M.Get(baseCard, "IdHabilidad", "idHabilidad")), out var h)
           && h.Efecto == Efe.Disparo;

    // ── Cartas ESTÁTICAS (Condicion == 3) ───────────────────────────────────────
    // No se despliegan en el cuartel. El bot solo sabe desplegar en su cuartel,
    // así que las excluye del despliegue (misma regla que el cliente humano).
    private static bool EsEstatica(Dictionary<string, object?> baseCard)
        => M.Int(M.Get(baseCard, "Condicion", "condicion")) == 3;

    private static Dictionary<string, object?> NuevaUnidad(
        Dictionary<string, object?> baseCard, string id, string botUid, string zona)
        => new(baseCard)
        {
            ["id"] = id,
            ["ownerUid"] = botUid,
            ["ownerZone"] = zona,
            ["instanceId"] = Guid.NewGuid().ToString("N"),
        };

    // Juega hasta _maxAccionesCarta cartas de acción de la mano:
    //   · Ofensivas (disparo/veneno/parálisis) → al mejor grupo enemigo en rango.
    //   · Escudo → SOLO si el cuartel está amenazado, para protegerlo.
    //   · Potenciación → a la unidad propia más adelantada en rango.
    // Teletransporte (mover una carta propia) se deja en mano por ahora.
    // v10: `gastadoAcciones` acumula el coste de las acciones SOLO para el
    // presupuesto local; NO se reporta en EnergiaGastada (el servidor cobra las
    // acciones en la resolución del turno; sumarlas era un doble cobro).
    private void JugarCartasAccion(
        BotContext ctx, string? miCuartel, string zona, bool amenazado,
        Dictionary<string, List<Dictionary<string, object?>>> enemyByCoord,
        HashSet<string> enemyCuarteles, List<string> misCoords,
        ref int energia, ref int gastadoAcciones,
        List<string> mano, List<Dictionary<string, object?>> acciones,
       HashSet<string>? cuartelesAtrincherados = null, string? celdaEscudo = null)
    {
        // DIAGNÓSTICO: cuántas cartas de acción hay en mano (para ver si el mazo
        // del bot siquiera las incluye). Si esto sale 0 turno tras turno, es un
        // problema de DATOS (marcar las cartas de acción como PorDefecto o dar a
        // los bots un mazo que las contenga), no del bot.
        var enMano = mano.Where(id =>
            ctx.CatalogoMano.TryGetValue(id, out var b) && EsAccion(b)).ToList();
        if (enMano.Count > 0)
            Console.WriteLine($"[WZ][bot {ctx.BotUid}] cartas de accion en mano: " +
                string.Join(",", enMano.Select(id =>
                    $"{id}(hab{M.Int(M.Get(ctx.CatalogoMano[id], "IdHabilidad", "idHabilidad"))})")));

        if (miCuartel == null) return;
        int jugadas = 0;
        foreach (var id in enMano)
        {
            if (jugadas >= _maxAccionesCarta) break;
            var baseCard = ctx.CatalogoMano[id];
            int habId = M.Int(M.Get(baseCard, "IdHabilidad", "idHabilidad"));
            if (!AccionesTacticas.Catalogo.TryGetValue(habId, out var hab))
            {
                Console.WriteLine($"[WZ][bot {ctx.BotUid}] accion {id}: habilidad {habId} no modelada (teletransporte u otra) → en mano");
                continue;
            }
            int coste = M.Int(M.Get(baseCard, "Coste", "coste")); // carta de acción: coste = Coste normal
            if (coste > energia)
            {
                Console.WriteLine($"[WZ][bot {ctx.BotUid}] accion {id}: sin energia ({coste}>{energia})");
                continue;
            }

            List<string> objetivos;
            if (hab.Efecto == Efe.Escudo)
            {
                // El escudo NO puede ir al cuartel (regla del juego). Se usa para
                // blindar una posición rentable/adelantada que quieras conservar.
                // Sin una celda válida, no se juega (se guarda en mano).
                if (celdaEscudo == null) continue;
                objetivos = new() { celdaEscudo };
            }
            else if (hab.Efecto == Efe.Potenciacion)
            {
                // Buff a una unidad PROPIA en rango: la más adelantada (más cerca
                // de un enemigo) para que rente el potenciador.
                var objetivo = ElegirUnidadAPotenciar(hab, miCuartel, misCoords,
                    enemyByCoord, ctx.Filas, ctx.Columnas);
                if (objetivo == null)
                {
                    Console.WriteLine($"[WZ][bot {ctx.BotUid}] accion {id}: sin unidad propia en rango para potenciar");
                    continue;
                }
                objetivos = new() { objetivo };
            }
            else
            {
                objetivos = ElegirObjetivos(hab, miCuartel, ctx.Filas, ctx.Columnas,
                    enemyByCoord, enemyCuarteles, miCuartel);
                // ROMPER ATRINCHERAMIENTO: si es un DISPARO y algún cuartel enemigo
                // que el asalto no pudo tomar está en rango, dispararlo AHÍ primero
                // (limpia a los defensores; se entra a conquistar al turno siguiente
                // sobre el cuartel ya vacío). ElegirObjetivos ya incluye cuarteles
                // (ExcluyeCG=false); aquí solo forzamos su prioridad.
                if (hab.Efecto == Efe.Disparo && cuartelesAtrincherados != null
                    && cuartelesAtrincherados.Count > 0)
                {
                    var prioritarios = objetivos.Where(cuartelesAtrincherados.Contains).ToList();
                    if (prioritarios.Count > 0)
                    {
                        objetivos = prioritarios
                            .Concat(objetivos.Where(o => !prioritarios.Contains(o)))
                            .ToList();
                        Console.WriteLine($"[WZ][bot {ctx.BotUid}] DISPARO para romper cuartel atrincherado {prioritarios[0]}");
                    }
                }
                if (objetivos.Count < hab.NumObjetivos)
                {
                    Console.WriteLine($"[WZ][bot {ctx.BotUid}] accion {id}: sin objetivos enemigos en rango");
                    continue;
                }
                objetivos = objetivos.Take(hab.NumObjetivos).ToList();
            }

            acciones.Add(new Dictionary<string, object?>
            {
                ["habilidadId"] = habId,
                ["uid"] = ctx.BotUid,
                ["zona"] = zona,
                ["origen"] = miCuartel,
                ["objetivos"] = objetivos,
                ["turno"] = ctx.Turno,
                ["costePagado"] = coste,
                ["cartaAccionId"] = id,   // el servidor/cliente descarta esta carta de la mano
            });
            energia -= coste; gastadoAcciones += coste; jugadas++;
            mano.Remove(id);
            Console.WriteLine($"[WZ][bot {ctx.BotUid}] LANZA accion {id} (hab{habId}) sobre [{string.Join(",", objetivos)}]");
        }
    }

    // Elige la unidad PROPIA en rango (desde el cuartel) a la que aplicar una
    // potenciación: la más adelantada, es decir la más cercana a un enemigo.
    private static string? ElegirUnidadAPotenciar(
        Hab hab, string origen, List<string> misCoords,
        Dictionary<string, List<Dictionary<string, object?>>> enemyByCoord,
        int filas, int columnas)
    {
        bool EnRango(string c) => hab.Rango switch
        {
            Rng.Frontera => Manhattan(origen, c, filas, columnas) == 1,
            Rng.Radio7 => Manhattan(origen, c, filas, columnas) <= 7,
            Rng.Cualquiera => true,
            _ => true,
        };

        var candidatas = misCoords.Where(EnRango).ToList();
        if (candidatas.Count == 0) return null;
        if (enemyByCoord.Count == 0)
            return candidatas.First();

        // La más cercana a cualquier enemigo (frontal).
        return candidatas
            .OrderBy(c => enemyByCoord.Keys.Min(e => Manhattan(c, e, filas, columnas)))
            .First();
    }

    // ¿Algún enemigo puede alcanzar mi cuartel este turno o el siguiente?
    private static bool CuartelAmenazado(
        string cuartel, Dictionary<string, List<Dictionary<string, object?>>> enemyByCoord,
        Dictionary<string, string> terreno, int filas, int columnas)
    {
        foreach (var (coord, cartas) in enemyByCoord)
        {
            if (Manhattan(coord, cuartel, filas, columnas) <= 2) return true;
            foreach (var e in cartas)
                if (Alcanzables(coord, Mov(e), Tipo(e), terreno, filas, columnas).Contains(cuartel))
                    return true;
        }
        return false;
    }

    // Fuerza TOTAL que puede impactar mi cuartel este turno (suma de la fuerza de
    // todo enemigo que lo alcanza o está a distancia <=2). Sirve para dimensionar
    // cuánta defensa hay que anclar para NO perder el cuartel de golpe.
    private static int MaxAtaqueEntrante(
        string cuartel, Dictionary<string, List<Dictionary<string, object?>>> enemyByCoord,
        Dictionary<string, string> terreno, int filas, int columnas)
    {
        int suma = 0;
        foreach (var (coord, cartas) in enemyByCoord)
            if (Manhattan(coord, cuartel, filas, columnas) <= 2 ||
                cartas.Any(e => Alcanzables(coord, Mov(e), Tipo(e), terreno, filas, columnas).Contains(cuartel)))
                suma += cartas.Sum(Fuerza);
        return suma;
    }

    // Mueve una unidad excedente FUERA del cuartel a una celda segura (evita
    // apilar demasiadas cartas juntas). Si no hay alternativa, se queda donde está.
    // v10: antes elegía la celda alcanzable MÁS LEJANA del cuartel ("dispersar"),
    // que era literalmente esparcir el ejército. Ahora elige la celda segura
    // que más FARMEA y, a igualdad, la más cercana al objetivo de asedio / punto
    // de reunión, sin quedar expuesta ni entrar donde se pierde.
    private string ReubicarFueraDeCuartel(
        string coordOriginal, Dictionary<string, object?> card, string miCuartel,
        Dictionary<string, string> terreno, int filas, int columnas,
        Dictionary<string, List<Dictionary<string, object?>>> enemyByCoord,
        HashSet<string> enemyCuarteles, Dictionary<string, string> cuartelOwner, string botUid,
        string? objetivo = null, string? puntoReunion = null, Func<string, int>? Farm = null)
    {
        int myF = Fuerza(card), myD = Defensa(card);
        var reach = Alcanzables(coordOriginal, Mov(card), Tipo(card), terreno, filas, columnas);
        string meta = objetivo ?? puntoReunion ?? MasCercano(coordOriginal, enemyCuarteles, filas, columnas) ?? coordOriginal;
        string? mejor = null; double mejorScore = double.MinValue;
        foreach (var c in reach)
        {
            if (c == miCuartel) continue;
            if (_reglas != null)
            {
                if (!ReglasEntrada.EntradaPermitida(_reglas, c, myF, myD)) continue;
            }
            else
            {
                if (enemyCuarteles.Contains(c)) continue;
                if (enemyByCoord.ContainsKey(c) &&
                    !GanoAtacando(myF, myD, c, enemyByCoord, enemyCuarteles, cuartelOwner, botUid)) continue;
            }
            bool expuesta = _reglas != null && ReglasEntrada.Expuesta(_reglas, c, myF + myD);
            double score = (Farm?.Invoke(c) ?? 0) * 3.0
                           - Manhattan(c, meta, filas, columnas)
                           - (expuesta ? 50.0 : 0.0);
            if (score > mejorScore) { mejorScore = score; mejor = c; }
        }
        return mejor ?? coordOriginal;
    }

    // Intento de INTERCEPTACIÓN: elige el enemigo batible y predecible más cercano,
    // toma su celda PREDICHA (a dónde irá el próximo turno) y se mueve a la celda
    // segura propia que más se acerque a esa predicción — idealmente cayendo encima
    // o a distancia 1 para golpear en cuanto llegue. Devuelve null si no hay presa
    // predecible o si ninguna celda segura mejora respecto a quedarse.
    private string? Interceptar(
        string from, Dictionary<string, object?> card,
        int filas, int columnas,
        Dictionary<string, List<Dictionary<string, object?>>> enemyByCoord,
        HashSet<string> enemyCuarteles, Dictionary<string, string> cuartelOwner,
        string botUid, List<string> seguras, Dictionary<string, string> predEnemigo)
    {
        int myF = Fuerza(card), myD = Defensa(card);

        // Presa: enemigo batible, con predicción disponible, más cercano a nosotros.
        string? objCoord = null, pred = null; int mejorD = int.MaxValue;
        foreach (var (coord, cartas) in enemyByCoord)
        {
            if (enemyCuarteles.Contains(coord)) continue;
            if (!predEnemigo.TryGetValue(coord, out var p)) continue;   // muy móvil / no predecible
            if (!GanoAtacando(myF, myD, coord, enemyByCoord, enemyCuarteles, cuartelOwner, botUid)) continue;
            int d = Manhattan(from, coord, filas, columnas);
            if (d < mejorD) { mejorD = d; objCoord = coord; pred = p; }
        }
        if (objCoord == null || pred == null) return null;

        // Cortarle el paso: la celda segura que MINIMIZA la distancia a la celda
        // predicha (0 = caemos donde irá; 1 = adyacente para golpear al llegar).
        string mejor = from; int mejorDist = int.MaxValue;
        foreach (var c in seguras)
        {
            int dPred = Manhattan(c, pred, filas, columnas);
            if (dPred < mejorDist) { mejorDist = dPred; mejor = c; }
        }
        // Solo si mejora respecto a quedarse quieto (si no, que siga el flujo normal).
        if (mejor == from || mejorDist >= Manhattan(from, pred, filas, columnas)) return null;

        Console.WriteLine($"[WZ][bot {botUid}] predice {objCoord}->{pred}; intercepta {from}->{mejor}");
        return mejor;
    }

    // Predice a qué celda irá una carta enemiga el próximo turno.
    //  (1) Si la vimos moverse (prevCoord), extrapola su vector un paso más y lo
    //      ajusta a la celda alcanzable (respeta movimiento y tipo de terreno).
    //  (2) Si no hay historial, asume que avanza hacia su objetivo probable: la
    //      unidad mía más cercana o mi cuartel.
    private static string PredecirAvance(
        string coord, Dictionary<string, object?> enemyCard, string? prevCoord,
        Dictionary<string, string> terreno, int filas, int columnas,
        string? miCuartel, List<string> misUnidades, HashSet<string> atractoresEnergia)
    {
        int mov = Mov(enemyCard), tipo = Tipo(enemyCard);
        var reach = Alcanzables(coord, mov, tipo, terreno, filas, columnas);
        reach.Add(coord);

        var pc = Parse(coord);
        if (pc != null && prevCoord != null)
        {
            var pp = Parse(prevCoord);
            if (pp != null && (pp.Value.ri != pc.Value.ri || pp.Value.ci != pc.Value.ci))
            {
                int dr = pc.Value.ri - pp.Value.ri, dc = pc.Value.ci - pp.Value.ci;
                string objetivo = ClampLabel(pc.Value.ri + dr, pc.Value.ci + dc, filas, columnas);
                return reach
                    .OrderBy(c => Manhattan(c, objetivo, filas, columnas))
                    .ThenByDescending(c => Manhattan(c, coord, filas, columnas)) // que avance, no que se quede
                    .First();
            }
        }

        // Sin vector: la mayoría de jugadores van a por ENERGÍA (rayos / isla
        // central) o a por una presa cercana; asumimos que avanza hacia el atractor
        // más cercano entre {energía, mis unidades, mi cuartel}.
        var metas = new List<string>(atractoresEnergia);
        metas.AddRange(misUnidades);
        if (miCuartel != null) metas.Add(miCuartel);
        var meta = MasCercano(coord, metas, filas, columnas);
        if (meta == null) return coord;
        return reach.OrderBy(c => Manhattan(c, meta, filas, columnas)).First();
    }

    // Etiqueta de celda recortada a los límites del tablero.
    private static string ClampLabel(int ri, int ci, int filas, int columnas)
    {
        if (ri < 0) ri = 0; else if (ri >= filas) ri = filas - 1;
        if (ci < 0) ci = 0; else if (ci >= columnas) ci = columnas - 1;
        return Label(ri, ci);
    }

    // ── Decisión de movimiento de una unidad ──
    private string DecidirMovimiento(
        string coord, Dictionary<string, object?> card,
        Dictionary<string, string> terreno, int filas, int columnas,
        Dictionary<string, List<Dictionary<string, object?>>> enemyByCoord,
        HashSet<string> enemyCuarteles, Dictionary<string, string> cuartelOwner,
        HashSet<string> cuartelCoords, string botUid, Func<string, int> Farm,
        string? miCuartel, Dictionary<string, string> predEnemigo,
        HashSet<string> atractoresEnergia,
        HashSet<string> intrusos, HashSet<string> miContinente,
        string? puntoReunion, bool empujeFinal, Func<string, int> TurnosRayo)
    {
        int mov = Mov(card), tipo = Tipo(card);
        int myF = Fuerza(card), myD = Defensa(card);
        var reach = Alcanzables(coord, mov, tipo, terreno, filas, columnas);
        if (reach.Count == 0) return coord;

        // 1) ATACAR/CAZAR: mejor celda alcanzable con enemigos que ganamos, o
        //    cuartel sin defensor conquistable.
        string? mejorAtaque = null; int mejorValor = -1;
        foreach (var c in reach)
        {
            bool esCuartel = enemyCuarteles.Contains(c);
            if (enemyByCoord.ContainsKey(c))
            {
                if (!GanoAtacando(myF, myD, c, enemyByCoord, enemyCuarteles, cuartelOwner, botUid)) continue;
                int valor = enemyByCoord[c].Sum(Coste) + (esCuartel ? 1000 : 0);
                if (valor > mejorValor) { mejorValor = valor; mejorAtaque = c; }
            }
            else if (esCuartel && ConquistaVacio(c, myF, myD)) // cuartel enemigo vacío conquistable
            {
                if (1000 > mejorValor) { mejorValor = 1000; mejorAtaque = c; }
            }
        }
        if (mejorAtaque != null) return mejorAtaque;

        // v10: un cuartel enemigo "vacío" solo se pisa si se CONQUISTA contando
        // el refuerzo que su dueño puede desplegar (ReglasEntrada). Con F≤40 no
        // hay conquista posible y la carta solo espera a morir dentro.
        bool ConquistaVacio(string c, int f, int d)
            => _reglas != null ? ReglasEntrada.EntradaPermitida(_reglas, c, f, d) : f > UmbralCuartel;

        // Celdas a las que es seguro/legal moverse (sin pelea perdida, sin pisar
        // un cuartel que no podemos tomar).
        bool Segura(string c)
        {
            if (enemyCuarteles.Contains(c))
            {
                bool defend = CuartelDefendido(c, cuartelOwner, botUid, enemyByCoord);
                if (!defend) return ConquistaVacio(c, myF, myD);      // vacío: solo si conquistamos
                return GanoAtacando(myF, myD, c, enemyByCoord, enemyCuarteles, cuartelOwner, botUid);
            }
            if (cuartelCoords.Contains(c) && cuartelOwner.GetValueOrDefault(c) == botUid) return true; // mi cuartel
            if (enemyByCoord.ContainsKey(c))
                return GanoAtacando(myF, myD, c, enemyByCoord, enemyCuarteles, cuartelOwner, botUid);
            return true;
        }
        var seguras = reach.Where(Segura).ToList();
        if (seguras.Count == 0) return coord;

        // Evitar CAMINAR HACIA LA MUERTE: descartar (si hay alternativa) las
        // celdas donde la predicción dice que caerá un stack enemigo que nos gana.
        bool CaeEnemigoQueMeGana(string c)
        {
            foreach (var (src, land) in predEnemigo)
            {
                if (land != c) continue;
                if (!GanoAtacando(myF, myD, src, enemyByCoord, enemyCuarteles, cuartelOwner, botUid))
                    return true; // ahí aterriza alguien contra quien perdemos
            }
            return false;
        }
        // ANTI-SALLY (amenaza por ALCANCE): una celda está EXPUESTA si un stack
        // enemigo puede ALCANZARLA el próximo turno y batirnos allí. Cubre el caso
        // que la predicción por vector NO ve: un rival ATRINCHERADO y quieto (p. ej.
        // apilado en su cuartel) que SALE a batir a las unidades que se acercan a
        // farmear. Suma la fuerza de TODAS las cartas enemigas que alcanzan `c`; si
        // supera nuestra defensa allí, `c` es una trampa (perderíamos las tropas).
        bool ExpuestoASalida(string c)
        {
            if (cuartelCoords.Contains(c) && cuartelOwner.GetValueOrDefault(c) == botUid)
                return false; // mi propio cuartel: su defensa se gestiona aparte
            // v10: misma regla, pero en PODER (F+D) y con la evolución rival
            // pagable (ReglasEntrada.AmenazaSobre). Es la vara real del combate.
            if (_reglas != null) return ReglasEntrada.Expuesta(_reglas, c, myF + myD);
            // ANTES se sumaba la fuerza de TODOS los stacks enemigos que alcanzan
            // `c` y se comparaba solo contra MI DEFENSA. Eso supone que el mapa
            // entero converge a la vez sobre esta única celda: cerca de cualquier
            // zona disputada (el centro, sobre todo) TODAS las celdas de avance
            // salían "expuestas" y las unidades se clavaban en la retaguardia —
            // el "se quedan parados / no pelean el centro" de las partidas de
            // estudio. AHORA la amenaza efectiva es el MAYOR stack que llega más
            // la MITAD del resto (convergencia parcial, no total) y se compara
            // contra mi PODER completo (fuerza+defensa), que es la vara con la
            // que el juego resuelve el combate.
            int mayorStack = 0, restoStacks = 0;
            foreach (var (ecoord, ecartas) in enemyByCoord)
            {
                if (ecoord == c) continue; // combate directo ya lo cubre Segura
                if (!ecartas.Any(ec => Alcanzables(ecoord, Mov(ec), Tipo(ec), terreno, filas, columnas).Contains(c)))
                    continue;
                int f = ecartas.Sum(Fuerza);
                if (f > mayorStack) { restoStacks += mayorStack; mayorStack = f; }
                else restoStacks += f;
            }
            return mayorStack + restoStacks / 2 > myF + myD;
        }
        var sinPeligro = seguras.Where(c => !CaeEnemigoQueMeGana(c) && !ExpuestoASalida(c)).ToList();
        if (sinPeligro.Count > 0) seguras = sinPeligro;

        // NO ROMPER EL ASEDIO: si esta unidad está pegada a un cuartel enemigo
        // DEFENDIDO (forma parte del cerco), no la mandamos a farmear/cazar lejos:
        // se restringe a celdas que sigan pegadas a ese cuartel (mantener el anillo)
        // hasta que se pueda tomar (por asalto en masa o tras un disparo lejano).
        var cuartelCercado = enemyCuarteles.FirstOrDefault(q =>
            Manhattan(coord, q, filas, columnas) == 1
            && CuartelDefendido(q, cuartelOwner, botUid, enemyByCoord));
        if (cuartelCercado != null)
        {
            var mantieneCerco = seguras
                .Where(c => Manhattan(c, cuartelCercado, filas, columnas) <= 1)
                .ToList();
            if (mantieneCerco.Count > 0) seguras = mantieneCerco;
        }

        // 1.5) CAZA PREDICTIVA: en vez de ir a la casilla ACTUAL del enemigo,
        //      adivinar hacia dónde se moverá (por su vector de avance) y cortarle
        //      el paso. Solo contra enemigos poco móviles y batibles. Con
        //      _probPrediccion>=1 lo intenta siempre; si no, con esa probabilidad.
        if (predEnemigo.Count > 0 && (_probPrediccion >= 1.0 || _rng.NextDouble() < _probPrediccion))
        {
            var intercept = Interceptar(coord, card, filas, columnas,
                enemyByCoord, enemyCuarteles, cuartelOwner, botUid, seguras, predEnemigo);
            if (intercept != null) return intercept;
        }

        // 1.6) DEFENSA DEL TERRITORIO: si un rival ha entrado en MI continente y
        //      esta unidad está en casa, converge hacia el intruso más cercano
        //      para contenerlo entre varias (aunque en solitario no lo bata; el
        //      asalto en grupo lo remata). Prioriza defender antes que farmear.
        if (intrusos.Count > 0 && miContinente.Contains(coord))
        {
            var objIntruso = MasCercano(coord, intrusos, filas, columnas);
            if (objIntruso != null)
            {
                string mejorC = coord; int mejorDI = Manhattan(coord, objIntruso, filas, columnas);
                foreach (var c in seguras)
                {
                    int d = Manhattan(c, objIntruso, filas, columnas);
                    if (d < mejorDI) { mejorDI = d; mejorC = c; }
                }
                return mejorC;
            }
        }

        // 2) FARMEAR: si alguna celda segura da energía, ir a la de mayor farmeo.
        //    IMPORTANTE (economía): se incluye la celda ACTUAL como candidata si ya
        //    farmea y es segura, para NO abandonar un rayo (+10) / isla (+7) que ya
        //    ocupamos por otra celda de igual o menor valor. La energía es la
        //    condición de victoria: mantener el farmeo premium es prioritario.
        //    DOS EXCEPCIONES (v8, fallos observados en las partidas de estudio):
        //    · EMPUJE FINAL: con la partida ganada económicamente, seguir
        //      farmeando es acumular en balde; se salta el farmeo y la unidad
        //      marcha al objetivo (convertir la ventaja en victoria).
        //    · PIEZA DÉBIL EN TERRITORIO ENEMIGO: el +5 del continente rival
        //      atraía cartas débiles hasta la puerta de los cuarteles enemigos,
        //      donde solo mueren contra el +40. Una pieza incapaz de amenazar un
        //      cuartel NO farmea a ≤2 de un cuartel enemigo.
        if (!empujeFinal)
        {
            bool debil = myF <= UmbralCuartel;
            bool FarmeoPermitido(string c) =>
                Farm(c) > 0 && !(debil && enemyCuarteles.Any(q => Manhattan(c, q, filas, columnas) <= 2));
            var conFarm = seguras.Where(FarmeoPermitido).ToList();
            if (FarmeoPermitido(coord) && !CaeEnemigoQueMeGana(coord)) conFarm.Add(coord);
            if (conFarm.Count > 0)
                return conFarm.OrderByDescending(Farm)
                              .ThenBy(c => DistObjetivo(c, coord, enemyByCoord, enemyCuarteles, filas, columnas, myF, myD, cuartelOwner, botUid))
                              .First();
        }

        // 3) Sin farmeo a mano: moverse hacia un OBJETIVO (equilibra caza y energía).
        string? objetivo = ObjetivoGlobal(coord, enemyByCoord, enemyCuarteles, filas, columnas, myF, myD, cuartelOwner, botUid, Farm, atractoresEnergia, puntoReunion, empujeFinal, TurnosRayo, mov);
        if (objetivo == null) return coord;
        int distActual = Manhattan(coord, objetivo, filas, columnas);
        string mejor = coord; int mejorDist = distActual;
        foreach (var c in seguras)
        {
            int d = Manhattan(c, objetivo, filas, columnas);
            if (d < mejorDist) { mejorDist = d; mejor = c; }
        }
        return mejor;
    }

    // Objetivo hacia el que orientarse, EQUILIBRANDO caza y energía: si hay una
    // celda de energía (rayo / isla) estrictamente más cerca que el enemigo
    // batible más próximo, va primero a por la energía (la recoge de camino); si
    // no, caza. En último término, se orienta al cuartel enemigo más cercano.
    private string? ObjetivoGlobal(
        string from, Dictionary<string, List<Dictionary<string, object?>>> enemyByCoord,
        HashSet<string> enemyCuarteles, int filas, int columnas, int myF, int myD,
        Dictionary<string, string> cuartelOwner, string botUid, Func<string, int> Farm,
        HashSet<string> atractoresEnergia, string? puntoReunion, bool empujeFinal,
        Func<string, int> TurnosRayo, int mov)
    {
        // a) enemigo batible más cercano (caza)
        string? mejorEnemigo = null; int dEnemigo = int.MaxValue;
        foreach (var c in enemyByCoord.Keys)
        {
            if (!GanoAtacando(myF, myD, c, enemyByCoord, enemyCuarteles, cuartelOwner, botUid)) continue;
            int d = Manhattan(from, c, filas, columnas);
            if (d < dEnemigo) { dEnemigo = d; mejorEnemigo = c; }
        }

        // b) energía (rayo / isla) más cercana. v9b: un RAYO que se apagará antes
        //    de que lleguemos NO es un objetivo — duran 3 turnos y el bot
        //    emprendía marchas más largas que su vida. La isla no caduca.
        string? mejorEnergia = null; int dEnergia = int.MaxValue;
        foreach (var c in atractoresEnergia)
        {
            int d = Manhattan(from, c, filas, columnas);
            int vida = TurnosRayo(c);
            if (vida > 0)
            {
                int turnosViaje = mov > 0 ? (d + mov - 1) / mov : int.MaxValue;
                if (vida <= turnosViaje) continue;   // se apaga antes de llegar
            }
            if (d < dEnergia) { dEnergia = d; mejorEnergia = c; }
        }

        // Equilibrio, sesgado por estilo: _sesgoFrente>0 (agresivo) exige que la
        // energía esté MUCHO más cerca para desviarse a por ella; <0 (defensivo) se
        // desvía a farmear con más facilidad. =0 reproduce el criterio clásico.
        // En EMPUJE FINAL la energía deja de ser objetivo: se caza o se asedia.
        if (!empujeFinal && mejorEnergia != null && dEnergia + _sesgoFrente < dEnemigo) return mejorEnergia;
        if (mejorEnemigo != null) return mejorEnemigo;
        if (!empujeFinal && mejorEnergia != null) return mejorEnergia;

        // c) último recurso, según el PODER de la pieza (v8). Antes TODA unidad
        //    sin presa ni energía marchaba al cuartel enemigo más cercano: era el
        //    goteo de piezas débiles hacia cuarteles/continentes rivales que se
        //    veía en las partidas (solo mueren contra el +40). Ahora las piezas
        //    incapaces de amenazar un cuartel van al PUNTO DE REUNIÓN (la celda
        //    propia más fuerte) a formar masa; solo las capaces asedian.
        if (myF <= UmbralCuartel && puntoReunion != null && puntoReunion != from)
            return puntoReunion;
        // v9: las piezas capaces asedian el cuartel OBJETIVO del turno (el más
        // barato de tomar), no el más cercano a esta pieza.
        if (_objetivoAsedioTurno != null && enemyCuarteles.Contains(_objetivoAsedioTurno))
            return _objetivoAsedioTurno;
        return MasCercano(from, enemyCuarteles, filas, columnas);
    }

    // ── v9: objetivo de asedio y punto de reunión ofensivo ──────────────────
    /// Cuartel enemigo más BARATO de tomar: guarnición (F+D enemiga en la celda)
    /// + bono del cuartel, ponderado por la distancia media de mis unidades
    /// móviles (PesoDistanciaAsedio por casilla). Null si no hay cuarteles vivos.
    private static string? ElegirObjetivoAsedio(
        List<(string coord, Dictionary<string, object?> card, string inst)> ownUnits,
        string? miCuartel,
        Dictionary<string, List<Dictionary<string, object?>>> enemyByCoord,
        HashSet<string> enemyCuarteles, int filas, int columnas, string? cuartelLider = null,
        string? objetivoPrevio = null)
    {
        if (enemyCuarteles.Count == 0) return null;
        var moviles = ownUnits.Where(u => Mov(u.card) > 0).Select(u => u.coord).ToList();
        if (moviles.Count == 0 && miCuartel != null) moviles.Add(miCuartel);
        string? mejor = null; double mejorCoste = double.MaxValue;
        double costeLider = double.MaxValue, costePrevio = double.MaxValue;
        foreach (var q in enemyCuarteles)
        {
            int guarnicion = enemyByCoord.TryGetValue(q, out var g) ? g.Sum(c => Fuerza(c) + Defensa(c)) : 0;
            double dist = moviles.Count == 0 ? 0.0 : moviles.Average(c => (double)Manhattan(c, q, filas, columnas));
            double coste = guarnicion + UmbralCuartel + PesoDistanciaAsedio * dist;
            if (q == cuartelLider) costeLider = coste;
            if (q == objetivoPrevio) costePrevio = coste;
            if (coste < mejorCoste) { mejorCoste = coste; mejor = q; }
        }
        // v9b: si el cuartel del LÍDER no cuesta más de 1,5× el más barato, se le
        // asedia a él: rematar al último de la tabla solo acelera al que ya gana.
        string? elegido = (cuartelLider != null && costeLider <= mejorCoste * 1.5) ? cuartelLider : mejor;
        // v10: HISTÉRESIS. El objetivo se recalculaba cada turno por la distancia
        // media del ejército (que cambia al moverse), así que la marcha cambiaba
        // de rumbo y nunca cuajaba. Se conserva el objetivo anterior mientras
        // siga vivo y no sea claramente más caro (> HisteresisAsedio ×) que el
        // nuevo mejor.
        if (objetivoPrevio != null && enemyCuarteles.Contains(objetivoPrevio)
            && objetivoPrevio != elegido && costePrevio <= mejorCoste * HisteresisAsedio)
            return objetivoPrevio;
        return elegido;
    }

    /// Celda propia (≠ cuartel) donde formar la masa: la MÁS FUERTE de entre las
    /// más ADELANTADAS hacia el objetivo (a ≤2 casillas de la más cercana). Sin
    /// objetivo, la más fuerte del mapa (comportamiento v8).
    private static string? PuntoReunionOfensivo(
        List<(string coord, Dictionary<string, object?> card, string inst)> ownUnits,
        string? miCuartel, string? objetivo, int filas, int columnas)
    {
        var celdasPropias = ownUnits
            .Where(u => u.coord != miCuartel)
            .GroupBy(u => u.coord)
            .Select(g => (coord: g.Key, poder: g.Sum(x => Fuerza(x.card) + Defensa(x.card))))
            .ToList();
        if (celdasPropias.Count == 0) return null;
        if (objetivo == null)
            return celdasPropias.OrderByDescending(c => c.poder).First().coord;
        string obj = objetivo;
        int dMin = celdasPropias.Min(c => Manhattan(c.coord, obj, filas, columnas));
        return celdasPropias
            .Where(c => Manhattan(c.coord, obj, filas, columnas) <= dMin + 2)
            .OrderByDescending(c => c.poder)
            .ThenBy(c => Manhattan(c.coord, obj, filas, columnas))
            .First().coord;
    }

    private int DistObjetivo(
        string c, string from, Dictionary<string, List<Dictionary<string, object?>>> enemyByCoord,
        HashSet<string> enemyCuarteles, int filas, int columnas, int myF, int myD,
        Dictionary<string, string> cuartelOwner, string botUid)
    {
        var obj = MasCercano(from, enemyCuarteles, filas, columnas);
        return obj == null ? 0 : Manhattan(c, obj, filas, columnas);
    }

    // ── Combate ──
    private bool GanoAtacando(
        int myF, int myD, string coord,
        Dictionary<string, List<Dictionary<string, object?>>> enemyByCoord,
        HashSet<string> enemyCuarteles, Dictionary<string, string> cuartelOwner, string botUid,
        int sesgo = 0)
        => GanaGrupo(myF, myD, coord, enemyByCoord, enemyCuarteles, cuartelOwner, botUid, sesgo);

    // ¿Gana un GRUPO propio (suma de fuerza/defensa) atacando esa celda? El
    // combate del juego suma las cartas por celda, así que apilar unidades es la
    // forma legítima de ganar peleas que en solitario se pierden.
    private bool GanaGrupo(
        int sumF, int sumD, string coord,
        Dictionary<string, List<Dictionary<string, object?>>> enemyByCoord,
        HashSet<string> enemyCuarteles, Dictionary<string, string> cuartelOwner, string botUid,
        int sesgo = 0)
    {
        // v10: delega en ReglasEntrada (fuente única): combate exacto del
        // servidor por grupos de dueño, evolución rival PAGABLE con estadísticas
        // reales, refuerzo que el dueño de un cuartel puede desplegar y, como
        // antes, sin sesgo en cuarteles (ni contra stacks evolucionables: es la
        // trampa de 9UCN T9, un Capitán 8/7 "ganando" a una Bestia del abismo
        // que evolucionó a 55/18 en el mismo turno).
        if (_reglas != null)
        {
            var v = ReglasEntrada.Evaluar(_reglas, coord, sumF, sumD,
                contarRefuerzo: enemyCuarteles.Contains(coord), sesgo);
            return v is ReglasEntrada.Veredicto.Gana or ReglasEntrada.Veredicto.SinCombate;
        }

        // Fallback (sin contexto): lectura clásica.
        if (enemyCuarteles.Contains(coord)) sesgo = 0;
        if (!enemyByCoord.TryGetValue(coord, out var enemigos) || enemigos.Count == 0)
            return !enemyCuarteles.Contains(coord) || sumF > UmbralCuartel;
        int fe = enemigos.Sum(Fuerza), de = enemigos.Sum(Defensa);
        if (enemyCuarteles.Contains(coord) && CuartelDefendido(coord, cuartelOwner, botUid, enemyByCoord))
            de += UmbralCuartel;
        return (sumF - de) > (fe - sumD - sesgo);
    }

    /// v10: perfil DEFENSIVO de una carta como guarnición. Cuanto más alto,
    /// mejor se queda en casa: poder útil (para cubrir la amenaza con POCAS
    /// piezas y que el resto farmee) pero LENTA y sin valor ofensivo especial.
    /// Un general o una pieza rápida valen mucho más fuera. Ejemplos: Elefante
    /// (50, mov 2) 70 · Megalodón (41, mov 3) 37 · Belial (30, mov 1) 45 ·
    /// Tanqueta (15, mov 3) −15 · Soldado (5, mov 2) −20 · General Izanagi
    /// (80, mov 3) −85.
    private static int PerfilGuarnicion(Dictionary<string, object?> c)
    {
        int poder = Fuerza(c) + Defensa(c);
        bool general = M.Int(M.Get(c, "Condicion", "condicion")) == 5;
        return 2 * poder - 15 * Mov(c) - (general ? 200 : 0);
    }

    private static bool CuartelDefendido(
        string coord, Dictionary<string, string> cuartelOwner, string botUid,
        Dictionary<string, List<Dictionary<string, object?>>> enemyByCoord)
    {
        var dueno = cuartelOwner.GetValueOrDefault(coord, "");
        if (dueno == "" || dueno == botUid) return false;
        if (!enemyByCoord.TryGetValue(coord, out var cartas)) return false;
        return cartas.Any(c => M.Str(M.Get(c, "ownerUid")) == dueno);
    }

    // ── Objetivos de habilidad ──
    private List<string> ElegirObjetivos(
    Hab hab, string origen, int filas, int columnas,
    Dictionary<string, List<Dictionary<string, object?>>> enemyByCoord,
    HashSet<string> enemyCuarteles, string? miCuartel)
    {
        // Escudo: caso propio (blindar una celda amenazada). El resto delega en el
        // helper compartido (Opción A) para que la selección de objetivos sea única
        // e idéntica en todos los planificadores.
        if (hab.Efecto == Efe.Escudo)
        {
            bool amenaza = Vecinas(origen, filas, columnas).Any(enemyByCoord.ContainsKey);
            if (!amenaza) return new();
            return new() { hab.Rango == Rng.Propia ? origen : (miCuartel ?? origen) };
        }
        return AccionesTacticas.MejoresObjetivos(hab, origen, enemyByCoord, enemyCuarteles, filas, columnas);
    }

    // ── Movimiento (BFS ortogonal, réplica del cliente) ──
    private static HashSet<string> Alcanzables(
        string from, int mov, int tipo, Dictionary<string, string> terreno, int filas, int columnas)
    {
        var res = new HashSet<string>();
        if (mov <= 0 || Parse(from) == null) return res;
        var visited = new Dictionary<string, int> { [from] = 0 };
        var queue = new Queue<(string, int)>();
        queue.Enqueue((from, 0));
        var deltas = new (int, int)[] { (-1, 0), (1, 0), (0, -1), (0, 1) };
        while (queue.Count > 0)
        {
            var (coord, steps) = queue.Dequeue();
            if (steps >= mov) continue;
            var pos = Parse(coord); if (pos == null) continue;
            var (ri, ci) = pos.Value;
            foreach (var (dr, dc) in deltas)
            {
                int nr = ri + dr, nc = ci + dc;
                if (nr < 0 || nr >= filas || nc < 0 || nc >= columnas) continue;
                var nCoord = Label(nr, nc);
                int ns = steps + 1;
                if (visited.GetValueOrDefault(nCoord, 999) <= ns) continue;
                if (!CanTraverse(nCoord, tipo, terreno)) continue;
                visited[nCoord] = ns;
                if (nCoord != from && CanLand(nCoord, tipo, terreno)) res.Add(nCoord);
                if (ns < mov) queue.Enqueue((nCoord, ns));
            }
        }
        return res;
    }

    // ── Geometría / terreno ──
    private static (int ri, int ci)? Parse(string coord)
    {
        if (string.IsNullOrEmpty(coord) || coord.Length < 2) return null;
        int ri = char.ToUpperInvariant(coord[0]) - 'A';
        if (!int.TryParse(coord[1..], out int col)) return null;
        return (ri, col - 1);
    }
    private static string Label(int ri, int ci) => $"{(char)('A' + ri)}{ci + 1}";
    private static string Terr(string coord, Dictionary<string, string> t) => t.TryGetValue(coord, out var v) ? v : "land";
    private static bool CanTraverse(string coord, int tipo, Dictionary<string, string> t) => tipo switch
    {
        1 => Terr(coord, t) is "land" or "amphibious",
        3 => Terr(coord, t) is "sea" or "deepSea" or "amphibious",
        _ => true,
    };
    private static bool CanLand(string coord, int tipo, Dictionary<string, string> t) => tipo switch
    {
        1 or 2 => Terr(coord, t) is "land" or "amphibious",
        3 => Terr(coord, t) is "sea" or "deepSea" or "amphibious",
        _ => true,
    };
    private static int Manhattan(string a, string b, int filas, int columnas)
    {
        var pa = Parse(a); var pb = Parse(b);
        if (pa == null || pb == null) return int.MaxValue;
        return Math.Abs(pa.Value.ri - pb.Value.ri) + Math.Abs(pa.Value.ci - pb.Value.ci);
    }
    private static IEnumerable<string> Vecinas(string coord, int filas, int columnas)
    {
        var p = Parse(coord); if (p == null) yield break;
        var (ri, ci) = p.Value;
        foreach (var (dr, dc) in new[] { (-1, 0), (1, 0), (0, -1), (0, 1) })
        {
            int nr = ri + dr, nc = ci + dc;
            if (nr < 0 || nr >= filas || nc < 0 || nc >= columnas) continue;
            yield return Label(nr, nc);
        }
    }
    private static string? MasCercano(string? from, IEnumerable<string> cands, int filas, int columnas)
    {
        if (from == null) return cands.FirstOrDefault();
        string? mejor = null; int mejorD = int.MaxValue;
        foreach (var c in cands) { int d = Manhattan(from, c, filas, columnas); if (d < mejorD) { mejorD = d; mejor = c; } }
        return mejor;
    }

    // ── Stats ──
    private static int Fuerza(Dictionary<string, object?> c) => M.Int(M.Get(c, "Fuerza", "fuerza"));
    private static int Defensa(Dictionary<string, object?> c) => M.Int(M.Get(c, "Defensa", "defensa"));
    private static int Coste(Dictionary<string, object?> c) => M.Int(M.Get(c, "Coste", "coste"));
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
}

// ─────────────────────────────────────────────────────────────────────────────
// Orquestador del bot para UNA partida.
// ─────────────────────────────────────────────────────────────────────────────
public class WarZeroBot
{
    private readonly WarZeroFirestore _fs;
    private readonly WarZeroService _svc;
    private readonly WarZeroBotOptions _opt;
    private readonly IBotStrategy _strategy;

    private readonly record struct MapaInfo(
        Dictionary<string, string> Terreno, int Filas, int Columnas,
        HashSet<string> IslaCentral, Dictionary<string, List<string>> Continentes);

    private readonly ConcurrentDictionary<string, MapaInfo> _mapas = new();

    // Ejército resuelto por bot (estable durante la vida del proceso) y lista de
    // ejércitos existentes, para no releerlos en cada turno.
    private readonly ConcurrentDictionary<string, int> _ejercitoCache = new();
    private List<int>? _ejercitoIds;

    // ── Catálogo de cartas COMPARTIDO entre TODOS los runners de bots ──────────
    // Las definiciones de cartas son estáticas durante las partidas (y cambian
    // muy raramente). Antes cada bot releía de Firestore cada carta de su mano
    // —una a una— y consultaba las especiales en CADA turno, lo que multiplicaba
    // las lecturas. Ahora la colección `Cartas` se lee UNA vez y se sirve de
    // memoria para todos los bots, refrescándose solo si supera el TTL.
    private static volatile Dictionary<string, Dictionary<string, object?>>? _catalogo;
    private static DateTime _catalogoCargado = DateTime.MinValue;
    private static readonly TimeSpan _catalogoTtl = TimeSpan.FromMinutes(10);
    private static Task<Dictionary<string, Dictionary<string, object?>>>? _catalogoCargando;
    private static readonly object _catGate = new();

    public WarZeroBot(
        WarZeroFirestore fs, WarZeroService svc,
        WarZeroBotOptions? options = null, IBotStrategy? strategy = null,
        PerfilBot? perfil = null)
    {
        _fs = fs; _svc = svc;
        _opt = options ?? new WarZeroBotOptions();
        // Si no se inyecta una estrategia explícita, se construye la Estratega con
        // el PERFIL del bot (dificultad + estilo), envuelta en softmax: varía la
        // jugada entre variantes de su estilo y elige por función de evaluación.
        _strategy = strategy ?? new EstrategaSoftmaxStrategy(_opt, perfil ?? PerfilBot.PorDefecto);
    }

    public async Task RunForLobbyAsync(string lobbyId, string botUid, string botAlias, CancellationToken ct = default)
    {
        try
        {
            Log(botUid, $"entrando a rellenar la sala {lobbyId}");
            if (!await UnirseYMarcarListoAsync(lobbyId, botUid, botAlias, ct)) { Log(botUid, "no pude unirme"); return; }
            if (!await EsperarArranqueAsync(lobbyId, ct)) { Log(botUid, "la sala no arrancó; me retiro"); return; }
            await _svc.EntrarAsync(new EntrarRequest { LobbyId = lobbyId, Uid = botUid });
            Log(botUid, "dentro de la partida; empiezo a jugar");
            await BuclePartidaAsync(lobbyId, botUid, ct);
            Log(botUid, "partida terminada");
        }
        catch (OperationCanceledException) { Log(botUid, "cancelado"); }
        catch (Exception ex) { Console.Error.WriteLine($"[WZ][bot {botUid}] error fatal: {ex}"); }
    }

    // ── Reanudar una partida YA EN CURSO (recuperación tras reinicio) ──────────
    // La invoca el orquestador cuando encuentra una partida `en_curso` con este
    // bot como participante pero sin runner vivo. NO se une ni espera arranque:
    // la partida ya arrancó y el bot ya es participante. EntrarAsync es
    // idempotente (si ya tiene mano no la reparte otra vez), así que solo
    // re-adjunta el estado antes de retomar el bucle de juego.
    public async Task ResumeForLobbyAsync(string lobbyId, string botUid, string botAlias, CancellationToken ct = default)
    {
        try
        {
            Log(botUid, $"reanudando la partida {lobbyId} (recuperada tras reinicio)");
            await _svc.EntrarAsync(new EntrarRequest { LobbyId = lobbyId, Uid = botUid });
            await BuclePartidaAsync(lobbyId, botUid, ct);
            Log(botUid, "partida terminada");
        }
        catch (OperationCanceledException) { Log(botUid, "cancelado"); }
        catch (Exception ex) { Console.Error.WriteLine($"[WZ][bot {botUid}] error fatal (reanudar): {ex}"); }
    }

    private async Task<bool> UnirseYMarcarListoAsync(string lobbyId, string botUid, string botAlias, CancellationToken ct)
    {
        // EJÉRCITO: sin `ejercitoId`, el servidor reparte al bot un mazo por
        // defecto tomado del CATÁLOGO COMPLETO, mezclando cartas de todos los
        // ejércitos. Se resuelve antes de la transacción y se guarda en la entrada
        // del jugador, igual que hace un humano al elegir ejército en la sala.
        int ejercitoId = await EjercitoDeBotAsync(botUid, ct);

        var lobbyRef = _fs.Db.Collection("Partidas").Document(lobbyId);
        return await _fs.Db.RunTransactionAsync(async tx =>
        {
            var snap = await tx.GetSnapshotAsync(lobbyRef, ct);
            if (!snap.Exists) return false;
            var data = M.Map(M.FromFs(snap.ToDictionary()));
            if (M.Str(M.Get(data, "estado")) != "esperando") return false;
            var jugadores = M.List(M.Get(data, "jugadores")).Select(M.Map).ToList();
            int max = M.Int(M.Get(data, "maxJugadores"));
            bool yaEstoy = jugadores.Any(j => M.Str(M.Get(j, "uid")) == botUid);
            if (!yaEstoy)
            {
                if (max > 0 && jugadores.Count >= max) return false;
                jugadores.Add(new Dictionary<string, object?>
                {
                    ["uid"] = botUid,
                    ["alias"] = botAlias,
                    ["listo"] = true,
                    ["ejercitoId"] = ejercitoId,
                });
            }
            else foreach (var j in jugadores)
                if (M.Str(M.Get(j, "uid")) == botUid)
                {
                    j["listo"] = true;
                    if (M.Get(j, "ejercitoId") == null) j["ejercitoId"] = ejercitoId;
                }
            tx.Update(lobbyRef, new Dictionary<FieldPath, object>
            {
                [new FieldPath("jugadores")] = jugadores,
                [new FieldPath("participantes")] = FieldValue.ArrayUnion(botUid),
                // Marca la partida como "con bot" de forma persistente. WarZeroEstudio
                // solo registra la partida completa si este campo existe.
                [new FieldPath("botsUids")] = FieldValue.ArrayUnion(botUid),
            });
            return true;
        }, cancellationToken: ct);
    }

    // ── Cadencia de sondeo con RETROCESO ───────────────────────────────────────
    // Sondear una partida cuesta 1 lectura de Firestore por sondeo. En una
    // partida ACTIVA (el turno avanza entre sondeos) interesa ser rápido; en una
    // partida PARADA (un humano lleva horas o días sin cerrar) sondear cada 60 s
    // quema 1.440 lecturas/día por bot y partida para no hacer nada.
    //
    // Regla: mientras el turno cambie entre sondeos, cadencia base. Tras
    // `GraciaSinCambio` sondeos seguidos sin cambio, el intervalo se multiplica
    // por `FactorRetroceso` en cada sondeo hasta un tope. En cuanto el turno
    // avanza (o el bot juega), vuelve a la base. Un humano atento (turnos de
    // < 3 min en rápida) no nota nada; una partida abandonada pasa de 60 a ~12
    // lecturas/hora (rápida) o de 20 a 2 (diario/12h). Subir `TopeRapida` a
    // 10 min vuelve a dividir por dos el coste de las partidas paradas.
    private const int GraciaSinCambio = 3;
    private const double FactorRetroceso = 1.5;
    private static readonly TimeSpan TopeRapida = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan TopeLenta = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan BaseLenta = TimeSpan.FromMinutes(3);

    private static bool EsModoLento(Dictionary<string, object?> estado)
    {
        var modo = M.Str(M.Get(estado, "modoTurno"));
        return modo == "diario" || modo == "turno12h";
    }

    /// Intervalo BASE de sondeo según el modo de turno.
    private TimeSpan DelayPollBucle(Dictionary<string, object?> estado)
        => EsModoLento(estado) ? BaseLenta : _opt.PollInterval;

    /// Siguiente intervalo tras `sinCambio` sondeos seguidos sin que avance el turno.
    private TimeSpan DelayConRetroceso(Dictionary<string, object?> estado, int sinCambio)
    {
        var b = DelayPollBucle(estado);
        if (sinCambio < GraciaSinCambio) return b;
        var tope = EsModoLento(estado) ? TopeLenta : TopeRapida;
        var ms = b.TotalMilliseconds * Math.Pow(FactorRetroceso, sinCambio - GraciaSinCambio + 1);
        return TimeSpan.FromMilliseconds(Math.Min(ms, tope.TotalMilliseconds));
    }

    private async Task<bool> EsperarArranqueAsync(string lobbyId, CancellationToken ct)
    {
        var lobbyRef = _fs.Db.Collection("Partidas").Document(lobbyId);
        var limite = DateTime.UtcNow + _opt.MaxWaitStart;
        var delay = _opt.PollInterval;
        while (DateTime.UtcNow < limite)
        {
            ct.ThrowIfCancellationRequested();
            var snap = await lobbyRef.GetSnapshotAsync(ct);
            if (!snap.Exists) return false; // borrada por abandono
            var estado = M.Str(M.Get(M.Map(M.FromFs(snap.ToDictionary())), "estado"));
            if (estado == "en_curso") return true;
            if (estado != "esperando") return false;
            await Task.Delay(delay, ct);
            // Retroceso suave: 60 s → 90 → 135 → 202 → 300 (tope). Una sala que
            // tarda en llenarse cuesta ~7 lecturas en 15 min en vez de 15.
            delay = TimeSpan.FromMilliseconds(Math.Min(
                delay.TotalMilliseconds * FactorRetroceso, TopeRapida.TotalMilliseconds));
        }
        return false;
    }

    private async Task BuclePartidaAsync(string lobbyId, string botUid, CancellationToken ct)
    {
        int ultimoTurnoJugado = 0;
        int ultimoTurnoVisto = -1;
        int sondeosSinCambio = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var estado = await _svc.LeerEstadoAsync(lobbyId);
            if (estado == null) return;
            if (M.Str(M.Get(estado, "estado")) == "finalizada") return;
            var turno = M.Int(M.Get(estado, "turnoActual"));
            if (M.List(M.Get(estado, "jugadoresEliminados")).Select(M.Str).Contains(botUid)) return;
            bool yaCerre = M.List(M.Get(estado, "cerradoPor")).Select(M.Str).Contains(botUid);

            // ¿Ha avanzado el turno desde el último sondeo? (o es el primero)
            bool cambio = turno != ultimoTurnoVisto;
            ultimoTurnoVisto = turno;
            bool jugado = false;

            if (turno > ultimoTurnoJugado && !yaCerre)
            {
                await Task.Delay(_opt.ThinkDelay, ct);
                var fresco = await _svc.LeerEstadoAsync(lobbyId) ?? estado;
                if (M.Str(M.Get(fresco, "estado")) == "finalizada") return;
                int turnoFresco = M.Int(M.Get(fresco, "turnoActual"));
                var cerradoFresco = M.List(M.Get(fresco, "cerradoPor")).Select(M.Str).ToHashSet();
                if (turnoFresco == turno && !cerradoFresco.Contains(botUid))
                {
                    // Un turno que falle NO puede tirar el runner de toda la
                    // partida: se registra y se reintenta en el siguiente sondeo.
                    try
                    {
                        await JugarTurnoAsync(lobbyId, botUid, turno, fresco, ct);
                        ultimoTurnoJugado = turno;
                        jugado = true;
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"[WZ][bot {botUid}] turno {turno} falló (se reintenta en el siguiente sondeo): {ex}");
                    }
                }
            }

            // Tras jugar, los demás suelen cerrar enseguida → cadencia base. Si
            // nada cambia sondeo tras sondeo, ir espaciando.
            sondeosSinCambio = (cambio || jugado) ? 0 : sondeosSinCambio + 1;
            await Task.Delay(DelayConRetroceso(estado, sondeosSinCambio), ct);
        }
    }

    private async Task JugarTurnoAsync(string lobbyId, string botUid, int turno, Dictionary<string, object?> estado, CancellationToken ct)
    {
        var obeliscos = M.Map(M.Get(estado, "obeliscos"));
        var cuartel = M.Str(M.Get(obeliscos, botUid));
        var miStat = M.Map(M.Get(M.Map(M.Get(estado, "statsPartida")), botUid));
        int energia = M.Int(M.Get(miStat, "energies"));
        var mano = M.List(M.Get(miStat, "mano")).Select(M.Str).Where(s => s != "").ToList();

        var mapa = await CargarMapaAsync(estado, ct);
        var rayos = LeerRayos(estado);
        var rayosTurnos = LeerRayosTurnos(estado);
        var zona = ZonaDe(estado, botUid, cuartel, mapa.Filas, mapa.Columnas);
        var catalogo = await CargarCartasAsync(mano, ct);

        // ── Ejército del bot (para no mezclar cartas de ejércitos distintos y
        //    saber qué generales puede comprar) ──
        int ejercitoId = EjercitoDeJugador(estado, botUid) ?? await EjercitoDeBotAsync(botUid, ct);

        // ── Evoluciones referenciadas por las cartas en tablero ──
        // v10: de TODAS las cartas, no solo las mías. El catálogo ya está en
        // memoria (coste cero) y así ReglasEntrada / LookaheadDosPlies leen la
        // evolución REAL que el rival puede pagar (una Bestia del abismo 10/5 es
        // una Bestia del cielo 55/18 en potencia, no un 18/9 estimado).
        var idsEvo = new HashSet<string>();
        foreach (var celda in M.Map(M.Get(estado, "tablero")).Values)
            foreach (var c in M.List(celda))
            {
                var cm = M.Map(c);
                var ie = M.Str(M.Get(cm, "IdEvolucion", "idEvolucion"));
                if (ie != "" && M.Int(M.Get(cm, "Evolucion", "evolucion")) > 0) idsEvo.Add(ie);
            }
        var evoluciones = idsEvo.Count > 0
            ? await CargarCartasAsync(idsEvo.ToList(), ct)
            : new Dictionary<string, Dictionary<string, object?>>();

        // ── Generales (especiales) de mi ejército aún NO comprados ──
        var compradas = M.List(M.Get(miStat, "especialesCompradas")).Select(M.Str).ToHashSet();
        var generales = _opt.ComprarGenerales
            ? await CargarGeneralesAsync(ejercitoId, compradas, ct)
            : new List<Dictionary<string, object?>>();

        var ctx = new BotContext
        {
            EjercitoId = ejercitoId,
            Evoluciones = evoluciones,
            GeneralesDisponibles = generales,
            Estado = estado,
            BotUid = botUid,
            Turno = turno,
            Cuartel = cuartel,
            Energia = energia,
            Mano = mano,
            CatalogoMano = catalogo,
            Zona = zona,
            Terreno = mapa.Terreno,
            Filas = mapa.Filas,
            Columnas = mapa.Columnas,
            IslaCentral = mapa.IslaCentral,
            Continentes = mapa.Continentes,
            Rayos = rayos,
            RayosTurnos = rayosTurnos,
        };

        BotMove jugada;
        try { jugada = _strategy.DecidirJugada(ctx); }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[WZ][bot {botUid}] estrategia falló, cierre seguro: {ex}");
            jugada = new BotMove { Celdas = ArrastrarEjercito(estado, botUid), ManoResultante = mano };
        }

        // ── MODO elegido esta jugada (farmeo/defensa/caceria/libre), para medir en
        //    EstudioPartidas cuánto se usa cada uno. Si la estrategia no es la softmax
        //    (otro tipo de bot), queda "libre". Se ESCRIBE SIEMPRE, incluso en turnos
        //    pasivos en modo "libre": así la foto de EstudioPartidas refleja el modo
        //    REAL de ESTE turno y no arrastra el del turno anterior (que quedaría
        //    "pegado" ahora que la resolución conserva el campo). ──
        var modoBot = (_strategy as EstrategaSoftmaxStrategy)?.UltimoModo ?? "libre";
        var manoCambio = !mano.SequenceEqual(jugada.ManoResultante);

        try
        {
            await _svc.ActualizarStatsAsync(new StatsRequest
            {
                LobbyId = lobbyId,
                Uid = botUid,
                // v10: SOLO lo pre-pagado (despliegues/evoluciones/generales;
                // negativo si sacrifica). Las acciones las cobra el servidor al
                // resolver: incluirlas aquí era un doble cobro.
                EnergiesDelta = -jugada.EnergiaGastada,
                // Solo reemitir la mano si cambió: evita reescribirla en vano en los
                // turnos pasivos (EnergiesDelta 0 y EspecialComprada null también se
                // ignoran en ActualizarStatsAsync, así que en un turno pasivo el
                // único campo que se escribe es modoBot).
                Mano = manoCambio ? jugada.ManoResultante : null,
                // arrayUnion en `especialesCompradas`: el general queda marcado
                // como comprado para toda la partida (si muere, no se reinvoca).
                EspecialComprada = string.IsNullOrEmpty(jugada.EspecialComprada)
                    ? null : jugada.EspecialComprada,
                ModoBot = modoBot,
            });
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[WZ][bot {botUid}] actualizarStats falló: {ex}");
        }

        // ── CIERRE DE TURNO BLINDADO (v8) ──────────────────────────────────────
        // Antes, una excepción de CerrarTurnoAsync subía sin red hasta
        // BuclePartidaAsync y mataba el runner ("error fatal"): el bot dejaba de
        // jugar el RESTO de la partida (Modo B observado en EstudioPartidas:
        // desaparece de movimientosLog y el modoBot se queda pegado). Ahora:
        //   1) se intenta cerrar con la jugada completa;
        //   2) si falla, se REINTENTA una vez con un cierre seguro (arrastre del
        //      ejército, sin acciones): perder un turno de jugada es infinitamente
        //      mejor que perder el runner;
        //   3) si también falla, se registra y se deja que el bucle (ya blindado
        //      por iteración) lo reintente en el siguiente sondeo.
        try
        {
            var req = new CerrarTurnoRequest
            {
                LobbyId = lobbyId,
                Uid = botUid,
                Turno = turno,
                Celdas = JsonSerializer.SerializeToElement(jugada.Celdas),
                Acciones = JsonSerializer.SerializeToElement(jugada.Acciones),
            };
            var resp = await _svc.CerrarTurnoAsync(req);
            Log(botUid, $"turno {turno} cerrado (celdas={jugada.Celdas.Values.Sum(l => l.Count)}, acciones={jugada.Acciones.Count} por {jugada.EnergiaAcciones}, prepagado={jugada.EnergiaGastada}, resuelto={resp.Resuelto})");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[WZ][bot {botUid}] CerrarTurno falló con la jugada; reintento con cierre seguro: {ex}");
            var reqSafe = new CerrarTurnoRequest
            {
                LobbyId = lobbyId,
                Uid = botUid,
                Turno = turno,
                Celdas = JsonSerializer.SerializeToElement(ArrastrarEjercito(estado, botUid)),
                Acciones = JsonSerializer.SerializeToElement(new List<Dictionary<string, object?>>()),
            };
            var respSafe = await _svc.CerrarTurnoAsync(reqSafe);
            Log(botUid, $"turno {turno} cerrado en MODO SEGURO (resuelto={respSafe.Resuelto})");
        }
    }

    /// v9b: coord -> turnosRestantes de cada rayo activo. Retrocompat: el campo
    /// antiguo `rayo` (único) y los rayos sin `turnosRestantes` cuentan como 1
    /// (pagan este turno y no se cuenta con ellos para marchas largas).
    private static Dictionary<string, int> LeerRayosTurnos(Dictionary<string, object?> estado)
    {
        var res = new Dictionary<string, int>();
        var rayosRaw = M.Get(estado, "rayos");
        if (rayosRaw is System.Collections.IEnumerable en && rayosRaw is not string)
            foreach (var r in en)
            {
                var m = M.Map(r);
                var c = M.Str(M.Get(m, "coord"));
                if (c == "") continue;
                int t = M.Int(M.Get(m, "turnosRestantes"));
                res[c] = t > 0 ? t : 1;
            }
        if (res.Count == 0)
        {
            var uno = M.Map(M.Get(estado, "rayo"));
            var c = M.Str(M.Get(uno, "coord"));
            if (c != "") res[c] = 1;
        }
        return res;
    }

    private static HashSet<string> LeerRayos(Dictionary<string, object?> estado)
    {
        var res = new HashSet<string>();
        var rayosRaw = M.Get(estado, "rayos");
        if (rayosRaw is System.Collections.IEnumerable en && rayosRaw is not string)
            foreach (var r in en)
            {
                var c = M.Str(M.Get(M.Map(r), "coord"));
                if (c != "") res.Add(c);
            }
        if (res.Count == 0)
        {
            var uno = M.Map(M.Get(estado, "rayo"));
            var c = M.Str(M.Get(uno, "coord"));
            if (c != "") res.Add(c);
        }
        return res;
    }

    private static Dictionary<string, List<Dictionary<string, object?>>> ArrastrarEjercito(
        Dictionary<string, object?> estado, string botUid)
    {
        var celdas = new Dictionary<string, List<Dictionary<string, object?>>>();
        foreach (var (coord, raw) in M.Map(M.Get(estado, "tablero")))
            foreach (var cRaw in M.List(raw))
            {
                var carta = M.Map(cRaw);
                if (M.Str(M.Get(carta, "ownerUid")) != botUid) continue;
                if (!celdas.TryGetValue(coord, out var l)) { l = new(); celdas[coord] = l; }
                l.Add(new Dictionary<string, object?>(carta));
            }
        return celdas;
    }

    /// Ejército elegido por el jugador en la sala (`jugadores[].ejercitoId`),
    /// igual que lo lee WarZeroService para repartir la mano. null si no lo tiene.
    private static int? EjercitoDeJugador(Dictionary<string, object?> estado, string uid)
    {
        foreach (var j in M.List(M.Get(estado, "jugadores")))
        {
            var jm = M.Map(j);
            if (M.Str(M.Get(jm, "uid")) != uid) continue;
            var e = M.Get(jm, "ejercitoId");
            return e == null ? (int?)null : M.Int(e);
        }
        return null;
    }

    /// Ejército del bot: el de su documento en `Bots` (campo `ejercitoId`), el de
    /// las opciones, o uno derivado de forma ESTABLE del uid. Estable importa:
    /// así un bot siempre juega el mismo ejército y no mezcla cartas.
    private async Task<int> EjercitoDeBotAsync(string botUid, CancellationToken ct)
    {
        if (_ejercitoCache.TryGetValue(botUid, out var cache)) return cache;

        int elegido = 0;
        try
        {
            var snap = await _fs.Db.Collection("Bots").Document(botUid).GetSnapshotAsync(ct);
            if (snap.Exists)
                elegido = M.Int(M.Get(M.Map(M.FromFs(snap.ToDictionary())), "ejercitoId"));
        }
        catch (Exception ex) { Console.Error.WriteLine($"[WZ][bot {botUid}] leer ejercito falló: {ex}"); }

        if (elegido <= 0) elegido = _opt.EjercitoPorDefecto;
        if (elegido <= 0)
        {
            // Derivación estable por uid entre los ejércitos existentes (1..N).
            var ids = await CargarEjercitoIdsAsync(ct);
            if (ids.Count > 0)
            {
                int h = 0;
                foreach (var c in botUid) h = (h * 31 + c) & 0x7FFFFFFF;
                elegido = ids[h % ids.Count];
            }
            else elegido = 1;
        }

        _ejercitoCache[botUid] = elegido;
        return elegido;
    }

    /// Ids numéricos de la colección `Ejercitos` (el id del doc es el número).
    private async Task<List<int>> CargarEjercitoIdsAsync(CancellationToken ct)
    {
        if (_ejercitoIds != null) return _ejercitoIds;
        var ids = new List<int>();
        try
        {
            var snap = await _fs.Db.Collection("Ejercitos").GetSnapshotAsync(ct);
            foreach (var d in snap.Documents)
                if (int.TryParse(d.Id, out var n) && n > 0) ids.Add(n);
        }
        catch (Exception ex) { Console.Error.WriteLine($"[WZ][bot] leer Ejercitos falló: {ex}"); }
        ids.Sort();
        _ejercitoIds = ids;
        return ids;
    }

    /// GENERALES comprables: cartas especiales (Condicion == 5) del ejército del
    /// bot que aún no figuran en `especialesCompradas` de esta partida. Se filtra
    /// del catálogo EN MEMORIA (antes era una consulta a Firestore cada turno).
    private async Task<List<Dictionary<string, object?>>> CargarGeneralesAsync(
        int ejercitoId, HashSet<string> yaCompradas, CancellationToken ct)
    {
        var res = new List<Dictionary<string, object?>>();
        try
        {
            var cat = await ObtenerCatalogoAsync(ct);
            foreach (var kv in cat)
            {
                var map = kv.Value;
                if (M.Int(M.Get(map, "Condicion", "condicion")) != 5) continue;
                if (yaCompradas.Contains(kv.Key)) continue;
                if (ejercitoId > 0 && M.Int(M.Get(map, "Ejercito", "ejercito")) != ejercitoId) continue;
                // Copia superficial: no exponer las entradas del caché compartido.
                res.Add(new Dictionary<string, object?>(map));
            }
        }
        catch (Exception ex) { Console.Error.WriteLine($"[WZ][bot] filtrar generales falló: {ex}"); }
        return res;
    }

    private async Task<Dictionary<string, Dictionary<string, object?>>> CargarCartasAsync(List<string> ids, CancellationToken ct)
    {
        var res = new Dictionary<string, Dictionary<string, object?>>();
        try
        {
            var cat = await ObtenerCatalogoAsync(ct);
            foreach (var id in ids.Distinct())
            {
                if (cat.TryGetValue(id, out var map))
                    // Copia superficial: el llamante puede escribir claves de nivel
                    // superior sin corromper el caché compartido.
                    res[id] = new Dictionary<string, object?>(map);
            }
        }
        catch (Exception ex) { Console.Error.WriteLine($"[WZ][bot] leer cartas de caché falló: {ex}"); }
        return res;
    }

    // ── Catálogo compartido: carga perezosa con TTL y deduplicación ────────────
    // Devuelve el catálogo de `Cartas` en memoria. Si está fresco (< TTL) lo sirve
    // sin tocar Firestore. Si caducó, dispara UNA recarga compartida (aunque
    // varios bots la pidan a la vez) y, si esa recarga falla, sigue sirviendo el
    // catálogo anterior para no romper el turno.
    private async Task<Dictionary<string, Dictionary<string, object?>>> ObtenerCatalogoAsync(CancellationToken ct)
    {
        var cache = _catalogo;
        if (cache != null && (DateTime.UtcNow - _catalogoCargado) < _catalogoTtl)
            return cache;

        Task<Dictionary<string, Dictionary<string, object?>>> carga;
        lock (_catGate)
        {
            if (_catalogo != null && (DateTime.UtcNow - _catalogoCargado) < _catalogoTtl)
                return _catalogo;
            // Reutiliza una recarga en curso para no lanzar N lecturas simultáneas.
            _catalogoCargando ??= CargarCatalogoAsync();
            carga = _catalogoCargando;
        }

        try { return await carga; }
        catch
        {
            // Recarga fallida: si teníamos catálogo previo, seguimos con él.
            return _catalogo ?? new Dictionary<string, Dictionary<string, object?>>();
        }
    }

    private async Task<Dictionary<string, Dictionary<string, object?>>> CargarCatalogoAsync()
    {
        try
        {
            var nuevo = new Dictionary<string, Dictionary<string, object?>>();
            // Sin token por partida: es una carga compartida; que una partida se
            // cancele no debe abortar la recarga del resto. Es una lectura de toda
            // la colección UNA vez cada 10 min, compartida por todos los bots.
            var snap = await _fs.Db.Collection("Cartas").GetSnapshotAsync(CancellationToken.None);
            foreach (var d in snap.Documents)
            {
                var map = M.Map(M.FromFs(d.ToDictionary()));
                map["id"] = d.Id;
                nuevo[d.Id] = map;
            }
            _catalogo = nuevo;
            _catalogoCargado = DateTime.UtcNow;
            return nuevo;
        }
        finally
        {
            lock (_catGate) { _catalogoCargando = null; }
        }
    }

    private async Task<MapaInfo> CargarMapaAsync(Dictionary<string, object?> estado, CancellationToken ct)
    {
        var mapaId = M.Str(M.Get(estado, "mapaId"));
        int jugadores = M.List(M.Get(estado, "jugadores")).Count;
        var (filasDef, columnasDef) = DimensionesPreset(jugadores);
        if (mapaId == "")
            return new MapaInfo(new(), filasDef, columnasDef, new(), new());
        if (_mapas.TryGetValue(mapaId, out var cached)) return cached;

        var terreno = new Dictionary<string, string>();
        var isla = new HashSet<string>();
        var continentes = new Dictionary<string, List<string>>();
        int filas = filasDef, columnas = columnasDef;
        try
        {
            var snap = await _fs.Db.Collection("Mapas").Document(mapaId).GetSnapshotAsync(ct);
            if (snap.Exists)
            {
                var data = M.Map(M.FromFs(snap.ToDictionary()));
                foreach (var (coord, val) in M.Map(M.Get(data, "terreno"))) terreno[coord] = M.Str(val);
                foreach (var c in M.List(M.Get(data, "islaCentral")).Select(M.Str)) if (c != "") isla.Add(c);
                foreach (var (k, v) in M.Map(M.Get(data, "continentes")))
                    continentes[k] = M.List(v).Select(M.Str).Where(s => s != "").ToList();
                int f = M.Int(M.Get(data, "filas")), c2 = M.Int(M.Get(data, "columnas"));
                if (f > 0) filas = f;
                if (c2 > 0) columnas = c2;
            }
        }
        catch (Exception ex) { Console.Error.WriteLine($"[WZ][bot] leer mapa {mapaId} falló: {ex}"); }

        var info = new MapaInfo(terreno, filas, columnas, isla, continentes);
        _mapas[mapaId] = info;
        return info;
    }

    private static string ZonaDe(Dictionary<string, object?> estado, string botUid, string cuartel, int filas, int columnas)
    {
        foreach (var (_, raw) in M.Map(M.Get(estado, "tablero")))
            foreach (var cRaw in M.List(raw))
            {
                var carta = M.Map(cRaw);
                if (M.Str(M.Get(carta, "ownerUid")) == botUid)
                {
                    var z = M.Str(M.Get(carta, "ownerZone"));
                    if (z != "") return z;
                }
            }
        if (cuartel.Length < 2) return "";
        int ri = char.ToUpperInvariant(cuartel[0]) - 'A';
        if (!int.TryParse(cuartel[1..], out int col)) return "";
        int ci = col - 1;
        bool n = ri <= 2, s = ri >= filas - 3, w = ci <= 2, e = ci >= columnas - 3;
        if (n && e) return "ne"; if (n && w) return "nw"; if (s && e) return "se"; if (s && w) return "sw";
        if (n) return "north"; if (s) return "south"; if (w) return "west"; if (e) return "east";
        return "";
    }

    private static (int filas, int columnas) DimensionesPreset(int jugadores) => jugadores switch
    {
        2 => (6, 10),
        6 => (10, 16),
        8 => (12, 18),
        _ => (8, 14),
    };

    private static void Log(string botUid, string msg) => Console.WriteLine($"[WZ][bot {botUid}] {msg}");
}