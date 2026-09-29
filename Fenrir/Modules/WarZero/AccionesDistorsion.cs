using Tablero = System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<System.Collections.Generic.Dictionary<string, object?>>>;
using EfectosCelda = System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<System.Collections.Generic.Dictionary<string, object?>>>;

// ─────────────────────────────────────────────────────────────────────────────
// AccionesDistorsion.cs  (ARCHIVO NUEVO — añádelo al proyecto Fenrir)
//
// Lógica de servidor de las cuatro acciones de DISTORSIÓN. Cada una existe en
// tres rangos (cercano = adyacente, medio = radio 7, lejano = cualquiera); el
// rango lo valida el cliente al seleccionar, igual que el resto de habilidades.
//
//   · CLON (27/28/29) — Crea en la celda objetivo una COPIA de una carta propia
//     (`esClon:true`, `clonTurnos`). Para el rival es indistinguible de una
//     carta real (suma fuerza/defensa al mirar la celda), pero:
//       - NO combate: se retira del tablero antes del combate (ExtraerClones) y
//         se repone después solo si en su celda no había ningún enemigo.
//       - Si coincide con un enemigo (o está en un cuartel enemigo) DESAPARECE.
//       - No farmea, no lanza habilidades y caduca a los ClonDuracion turnos.
//       - Su creación NO se anuncia en el informe (sería delatarla).
//     El servidor valida los clones contra el tablero anterior (ValidarClones):
//     un cliente no puede fabricarlos ni quitarles la marca.
//
//   · MURO (30/31/32) — Tres celdas (1 en rango + 2 colindantes encadenadas).
//     Efecto de CELDA `muro` durante MuroDuracion turnos. Solo se levanta sobre
//     celdas vacías, sin cuartel y sin escudo rival. Nadie (tampoco su dueño)
//     puede terminar el movimiento en un muro ni atravesarlo: el servidor
//     revierte a su celda anterior cualquier carta que entre en un muro o que
//     solo hubiera podido llegar a su destino cruzándolo (RevertirMovimientosPorMuros).
//
//   · CONFUSIÓN (33/34/35) — Efecto `confusion` anclado a las cartas ENEMIGAS de
//     la celda objetivo (no es efecto de zona). Mientras dure:
//       - Su dueño no puede moverlas (se revierten como la parálisis) ni lanzar
//         sus habilidades.
//       - Cada resolución se mueven SOLAS a una celda aleatoria alcanzable según
//         su movimiento y tipo (tierra/aire/mar), sin pisar muros, cuarteles ni
//         escudos rivales (MoverConfundidas).
//       - En combate forman un bando propio: luchan contra su dueño y contra
//         los enemigos (ver Combate.Agrupar). No generan botín.
//
//   · FRACTURA (36/37/38) — objetivos = [origen, destino]. Desplaza TODAS las
//     cartas de la celda origen a la celda destino (a ≤ FracturaDistanciaMax).
//     La carta que no puede estar en el destino (terreno) o es estática se
//     queda donde está.
//
// Orden en la resolución (WarZeroService.ResolverTurnoCoreEnTx):
//   merge → parálisis/confusión (bloqueo) → ValidarClones →
//   RevertirMovimientosPorMuros → MoverConfundidas → AplicarAcciones (muro →
//   tele → fractura → clon → disparo → veneno → parálisis → confusión) →
//   trampas → descarga → ExtraerClones → Combate → ReinsertarClones →
//   TickEfectos (incluye TickClones).
// ─────────────────────────────────────────────────────────────────────────────
public static class AccionesDistorsion
{
    public const string TipoMuro = "muro";
    public const string TipoConfusion = "confusion";

    // ═════════════════════════════════════════════════════════════════════════
    // CONSULTAS
    // ═════════════════════════════════════════════════════════════════════════

    /// Celdas con un MURO activo.
    public static HashSet<string> CeldasConMuro(EfectosCelda? e)
    {
        var res = new HashSet<string>();
        if (e == null) return res;
        foreach (var kv in e)
        {
            if (kv.Value.Any(ef =>
                    M.Str(M.Get(ef, "tipo")) == TipoMuro &&
                    M.Int(M.Get(ef, "turnosRestantes")) > 0))
                res.Add(kv.Key);
        }
        return res;
    }

    /// ¿Hay alguna carta confundida en el tablero?
    public static bool HayConfundidas(Tablero t)
        => t.Values.Any(l => l.Any(CartaHelper.EstaConfundida));

    // ═════════════════════════════════════════════════════════════════════════
    // APLICACIÓN DE ACCIONES (llamadas desde Habilidades.AplicarAcciones)
    // ═════════════════════════════════════════════════════════════════════════

    /// MURO: valida que las celdas declaradas formen una cadena de colindantes y
    /// levanta el muro en las que estén libres (sin cartas, sin cuartel y sin
    /// escudo rival). Las demás se descartan y se anotan en el log.
    public static void AplicarMuro(
        Dictionary<string, object?> a, Tablero t, EfectosCelda e,
        List<Dictionary<string, object?>> log,
        Dictionary<string, string> obeliscos,
        Dictionary<string, string> protegidas)
    {
        var h = CatalogoHabilidades.Get(M.Int(M.Get(a, "habilidadId")));
        if (h == null) return;
        var uid = M.Str(M.Get(a, "uid"));

        var objetivos = M.List(M.Get(a, "objetivos")).Select(M.Str)
            .Where(s => s != "").Distinct().Take(CatalogoHabilidades.MuroNumCeldas).ToList();
        if (objetivos.Count == 0)
        {
            log.Add(Fallo(a, h, "Muro sin celdas"));
            return;
        }
        if (!Encadenadas(objetivos))
        {
            log.Add(Fallo(a, h, "Las celdas del muro deben ser colindantes"));
            return;
        }

        var cuarteles = obeliscos.Values.ToHashSet();
        var levantadas = new List<object?>();
        var descartadas = new List<object?>();

        foreach (var coord in objetivos)
        {
            string? motivo = null;
            if (cuarteles.Contains(coord)) motivo = "cuartel";
            else if (Habilidades.BloqueadaPorEscudo(protegidas, coord, uid)) motivo = "escudo rival";
            else if (t.TryGetValue(coord, out var cartas) && cartas.Count > 0) motivo = "ocupada";

            if (motivo != null)
            {
                descartadas.Add(new Dictionary<string, object?> { ["coord"] = coord, ["motivo"] = motivo });
                continue;
            }

            AgregarOFusionarEfectoCelda(e, coord, new Dictionary<string, object?>
            {
                ["tipo"] = TipoMuro,
                ["turnosRestantes"] = h.DuracionTurnos,
                ["magnitud"] = 0,
                ["origenUid"] = uid,
            });
            levantadas.Add(coord);
        }

        log.Add(new Dictionary<string, object?>
        {
            ["tipo"] = TipoMuro,
            ["habilidadId"] = h.Id,
            ["habilidadNombre"] = h.Nombre,
            ["uid"] = uid,
            ["zona"] = M.Str(M.Get(a, "zona")),
            ["origen"] = M.Str(M.Get(a, "origen")),
            ["objetivo"] = levantadas.Count > 0 ? levantadas[0] : objetivos[0],
            ["objetivos"] = levantadas,
            ["descartadas"] = descartadas,
            ["turnosRestantes"] = h.DuracionTurnos,
        });
    }

    /// FRACTURA: mueve las cartas de objetivos[0] a objetivos[1]. Cada carta se
    /// valida por separado: estáticas y cartas sin terreno compatible se quedan.
    public static void AplicarFractura(
        Dictionary<string, object?> a, Tablero t,
        List<Dictionary<string, object?>> log,
        Dictionary<string, string> obeliscos,
        Dictionary<string, string> protegidas,
        HashSet<string> celdasMuro,
        Dictionary<string, string>? terreno)
    {
        var h = CatalogoHabilidades.Get(M.Int(M.Get(a, "habilidadId")));
        if (h == null) return;
        var uid = M.Str(M.Get(a, "uid"));

        var objetivos = M.List(M.Get(a, "objetivos")).Select(M.Str).ToList();
        if (objetivos.Count < 2 || objetivos[0] == "" || objetivos[1] == "")
        {
            log.Add(Fallo(a, h, "Datos de fractura incompletos"));
            return;
        }
        var desde = objetivos[0];
        var hasta = objetivos[1];

        if (desde == hasta)
        {
            log.Add(Fallo(a, h, "El destino debe ser distinto del origen"));
            return;
        }
        var dist = Manhattan(desde, hasta);
        if (dist > CatalogoHabilidades.FracturaDistanciaMax)
        {
            log.Add(Fallo(a, h, $"El destino está a más de {CatalogoHabilidades.FracturaDistanciaMax} celdas"));
            return;
        }
        var cuarteles = obeliscos.Values.ToHashSet();
        if (cuarteles.Contains(desde) || cuarteles.Contains(hasta))
        {
            log.Add(Fallo(a, h, "La fractura no puede afectar a un cuartel"));
            return;
        }
        if (Habilidades.BloqueadaPorEscudo(protegidas, desde, uid) ||
            Habilidades.BloqueadaPorEscudo(protegidas, hasta, uid))
        {
            log.Add(Fallo(a, h, "Celda protegida por un escudo"));
            return;
        }
        if (celdasMuro.Contains(hasta))
        {
            log.Add(Fallo(a, h, "Celda destino bloqueada por un muro"));
            return;
        }

        var movidas = new List<object?>();
        var quietas = new List<object?>();

        if (t.TryGetValue(desde, out var cartas) && cartas.Count > 0)
        {
            var mover = new List<Dictionary<string, object?>>();
            foreach (var c in cartas.ToList())
            {
                string? motivo = null;
                if (CartaHelper.EsEstatica(c)) motivo = "estática";
                else if (terreno != null && !Habilidades.TeleCanLand(hasta, CartaHelper.Tipo(c), terreno))
                    motivo = "terreno incompatible";

                // Las cartas invisibles se desplazan igual, pero no se nombran en
                // el informe (no se delata su presencia).
                var resumen = CartaHelper.EsInvisible(c) ? null : ResumenCarta(c);
                if (motivo == null)
                {
                    mover.Add(c);
                    if (resumen != null) movidas.Add(resumen);
                }
                else if (resumen != null)
                {
                    resumen["motivo"] = motivo;
                    quietas.Add(resumen);
                }
            }

            if (mover.Count > 0)
            {
                cartas.RemoveAll(c => mover.Contains(c));
                if (cartas.Count == 0) t.Remove(desde);
                if (!t.TryGetValue(hasta, out var destino)) { destino = new(); t[hasta] = destino; }
                destino.AddRange(mover);
            }
        }

        log.Add(new Dictionary<string, object?>
        {
            ["tipo"] = "fractura",
            ["habilidadId"] = h.Id,
            ["habilidadNombre"] = h.Nombre,
            ["uid"] = uid,
            ["zona"] = M.Str(M.Get(a, "zona")),
            ["origen"] = M.Str(M.Get(a, "origen")),
            ["objetivo"] = desde,
            ["destino"] = hasta,
            ["cartasMovidas"] = movidas,
            ["cartasQuietas"] = quietas,
        });
    }

    /// CLON: copia una carta PROPIA (cartaOrigenCoord + cartaOrigenId/Indice,
    /// igual que el teletransporte) en la celda objetivo. El clon se crea en
    /// secreto: si sale bien NO se escribe nada en el informe compartido.
    public static void AplicarClon(
        Dictionary<string, object?> a, Tablero t,
        List<Dictionary<string, object?>> log,
        Dictionary<string, string> obeliscos,
        Dictionary<string, string> protegidas,
        HashSet<string> celdasMuro,
        Dictionary<string, string>? terreno)
    {
        var h = CatalogoHabilidades.Get(M.Int(M.Get(a, "habilidadId")));
        if (h == null) return;
        var uid = M.Str(M.Get(a, "uid"));

        var destino = M.List(M.Get(a, "objetivos")).Select(M.Str).FirstOrDefault(s => s != "") ?? "";
        if (destino == "")
        {
            log.Add(Fallo(a, h, "Datos de clon incompletos"));
            return;
        }
        if (obeliscos.Values.Contains(destino))
        {
            log.Add(Fallo(a, h, "No se puede clonar sobre un cuartel"));
            return;
        }
        if (Habilidades.BloqueadaPorEscudo(protegidas, destino, uid))
        {
            log.Add(Fallo(a, h, "Celda destino protegida por un escudo"));
            return;
        }
        if (celdasMuro.Contains(destino))
        {
            log.Add(Fallo(a, h, "Celda destino bloqueada por un muro"));
            return;
        }

        var original = LocalizarOriginal(a, t, uid);
        if (original == null)
        {
            log.Add(Fallo(a, h, "La carta a clonar ya no existe"));
            return;
        }
        if (CartaHelper.EsEstatica(original))
        {
            log.Add(Fallo(a, h, "No se puede clonar una carta estática"));
            return;
        }
        if (terreno != null && !Habilidades.TeleCanLand(destino, CartaHelper.Tipo(original), terreno))
        {
            log.Add(Fallo(a, h, "Terreno incompatible para el clon"));
            return;
        }

        var clon = new Dictionary<string, object?>(original);
        clon.Remove("Efectos");
        clon.Remove("UltimoUsoHabilidad");
        clon.Remove("ultimoUsoHabilidad");
        clon.Remove("turnosEnCelda");
        clon.Remove("origenTurno");
        clon["instanceId"] = "clon-" + Guid.NewGuid().ToString("N").Substring(0, 16);
        clon["esClon"] = true;
        clon["clonTurnos"] = Math.Max(1, h.DuracionTurnos);

        if (!t.TryGetValue(destino, out var lista)) { lista = new(); t[destino] = lista; }
        lista.Add(clon);
    }

    /// CONFUSIÓN: aplica el efecto a las cartas NO propias del lanzador que
    /// estén ahora en la celda. No deja efecto de celda: quien entre después no
    /// queda confundido.
    public static void AplicarConfusion(
        Dictionary<string, object?> a, Tablero t,
        List<Dictionary<string, object?>> log,
        Dictionary<string, string> obeliscos,
        Dictionary<string, string> protegidas)
    {
        var h = CatalogoHabilidades.Get(M.Int(M.Get(a, "habilidadId")));
        if (h == null) return;
        var uid = M.Str(M.Get(a, "uid"));
        var cuarteles = obeliscos.Values.ToHashSet();

        foreach (var obj in M.List(M.Get(a, "objetivos")).Select(M.Str).Where(s => s != "").Distinct())
        {
            if (h.ExcluyeCG && cuarteles.Contains(obj))
            {
                log.Add(Fallo(a, h, "La confusión no puede lanzarse sobre un cuartel"));
                continue;
            }
            if (Habilidades.BloqueadaPorEscudo(protegidas, obj, uid)) continue;

            var efecto = new Dictionary<string, object?>
            {
                ["tipo"] = TipoConfusion,
                ["turnosRestantes"] = h.DuracionTurnos,
                ["magnitud"] = 0,
                ["origenUid"] = uid,
            };

            int afectadasVisibles = 0;
            if (t.TryGetValue(obj, out var cartas))
            {
                foreach (var c in cartas)
                {
                    if (CartaHelper.OwnerUid(c) == uid) continue;
                    AgregarOFusionarEfectoCarta(c, efecto);
                    if (!CartaHelper.EsInvisible(c)) afectadasVisibles++;
                }
            }

            log.Add(new Dictionary<string, object?>
            {
                ["tipo"] = TipoConfusion,
                ["habilidadId"] = h.Id,
                ["habilidadNombre"] = h.Nombre,
                ["uid"] = uid,
                ["zona"] = M.Str(M.Get(a, "zona")),
                ["origen"] = M.Str(M.Get(a, "origen")),
                ["objetivo"] = obj,
                ["turnosRestantes"] = h.DuracionTurnos,
                ["cartasAfectadas"] = afectadasVisibles,
            });
        }
    }

    // ═════════════════════════════════════════════════════════════════════════
    // FASE PREVIA A LAS ACCIONES (WarZeroService, sobre el tablero fusionado)
    // ═════════════════════════════════════════════════════════════════════════

    /// Autoridad del servidor sobre los CLONES:
    ///   · Una carta cuyo instanceId era un clon el turno anterior SIGUE siendo
    ///     clon (se repone `esClon` y `clonTurnos`, aunque el cliente los quite).
    ///   · Una carta marcada como clon que no existía como clon el turno
    ///     anterior se elimina (un cliente no puede fabricar clones).
    /// Devuelve cuántas cartas se eliminaron.
    public static int ValidarClones(Tablero merged, Tablero? previo)
    {
        var clonesPrevios = new Dictionary<string, int>();
        if (previo != null)
            foreach (var lst in previo.Values)
                foreach (var c in lst)
                {
                    if (!CartaHelper.EsClon(c)) continue;
                    var iid = CartaHelper.InstanceId(c);
                    if (iid != "") clonesPrevios[iid] = Math.Max(1, CartaHelper.ClonTurnos(c));
                }

        int eliminadas = 0;
        var vistos = new HashSet<string>();
        foreach (var coord in merged.Keys.ToList())
        {
            var lst = merged[coord];
            var quedan = new List<Dictionary<string, object?>>();
            foreach (var c in lst)
            {
                var iid = CartaHelper.InstanceId(c);
                if (iid != "" && clonesPrevios.TryGetValue(iid, out var turnos))
                {
                    if (!vistos.Add(iid)) { eliminadas++; continue; } // duplicado
                    c["esClon"] = true;
                    c["clonTurnos"] = turnos;
                    quedan.Add(c);
                    continue;
                }
                if (CartaHelper.EsClon(c)) { eliminadas++; continue; } // fabricado
                quedan.Add(c);
            }
            if (quedan.Count == 0) merged.Remove(coord);
            else merged[coord] = quedan;
        }
        return eliminadas;
    }

    /// MUROS (autoritativo, humanos y bots): revierte a su celda del turno
    /// anterior cualquier carta que
    ///   (a) termine su movimiento en una celda con muro, o
    ///   (b) solo pudiera alcanzar su destino ATRAVESANDO un muro.
    /// Para (b) se compara el alcance con y sin muros (BFS con terreno): si el
    /// destino es alcanzable sin muros pero no con ellos, el movimiento cruzó un
    /// muro. Si ni siquiera sin muros es alcanzable, no se juzga aquí (esa regla
    /// no es competencia del muro).
    public static void RevertirMovimientosPorMuros(
        Tablero merged, Tablero? previo, EfectosCelda? efectosPrevios,
        Dictionary<string, string>? terreno, HashSet<string>? celdasValidas,
        List<Dictionary<string, object?>> log)
    {
        var muros = CeldasConMuro(efectosPrevios);
        if (muros.Count == 0 || previo == null) return;

        var previoPorInst = new Dictionary<string, (string coord, Dictionary<string, object?> card)>();
        foreach (var (coord, lst) in previo)
            foreach (var c in lst)
            {
                var iid = CartaHelper.InstanceId(c);
                if (iid != "") previoPorInst[iid] = (coord, c);
            }

        var revertir = new List<(string desde, string hacia, Dictionary<string, object?> carta, string motivo)>();
        foreach (var (coord, lst) in merged)
            foreach (var c in lst)
            {
                var iid = CartaHelper.InstanceId(c);
                if (iid == "" || !previoPorInst.TryGetValue(iid, out var prev)) continue;
                if (prev.coord == coord) continue; // no se movió

                if (muros.Contains(coord))
                    revertir.Add((coord, prev.coord, c, "entra en un muro"));
                else if (CruzaMuro(prev.coord, coord, prev.card, muros, terreno, celdasValidas))
                    revertir.Add((coord, prev.coord, c, "atraviesa un muro"));
            }

        foreach (var (desde, hacia, carta, motivo) in revertir)
        {
            if (merged.TryGetValue(desde, out var lst)) lst.Remove(carta);
            if (!merged.TryGetValue(hacia, out var dst)) { dst = new(); merged[hacia] = dst; }
            dst.Add(carta);

            if (CartaHelper.EsInvisible(carta)) continue; // no delatar su ruta
            log.Add(new Dictionary<string, object?>
            {
                ["tipo"] = "muroBloqueoMovimiento",
                ["uid"] = CartaHelper.OwnerUid(carta),
                ["zona"] = CartaHelper.OwnerZone(carta),
                ["origen"] = hacia,
                ["objetivo"] = desde,
                ["cartaNombre"] = CartaHelper.Nombre(carta),
                ["motivo"] = motivo,
            });
        }

        foreach (var k in merged.Keys.Where(k => merged[k].Count == 0).ToList())
            merged.Remove(k);
    }

    /// CONFUSIÓN: mueve cada carta confundida (no paralizada, no estática, con
    /// movimiento) a una celda ALEATORIA alcanzable según su movimiento y tipo,
    /// sin atravesar muros y sin terminar en muros, cuarteles ni celdas
    /// escudadas por otro jugador. Si no tiene destino posible, se queda.
    /// Debe llamarse con las confundidas ya devueltas a su celda anterior.
    public static void MoverConfundidas(
        Tablero merged, EfectosCelda? efectosPrevios,
        Dictionary<string, string> obeliscos,
        Dictionary<string, string>? terreno, HashSet<string>? celdasValidas,
        Random rng, List<Dictionary<string, object?>> log)
    {
        var candidatas = new List<(string coord, Dictionary<string, object?> carta)>();
        foreach (var coord in merged.Keys.OrderBy(k => k, StringComparer.Ordinal))
            foreach (var c in merged[coord])
            {
                if (!CartaHelper.EstaConfundida(c)) continue;
                if (CartaHelper.EstaParalizada(c)) continue;
                if (CartaHelper.EsEstatica(c)) continue;
                if (CartaHelper.MovimientoEfectivo(c) <= 0) continue;
                candidatas.Add((coord, c));
            }
        if (candidatas.Count == 0) return;

        var muros = CeldasConMuro(efectosPrevios);
        var protegidas = efectosPrevios != null
            ? Habilidades.CeldasProtegidas(efectosPrevios)
            : new Dictionary<string, string>();
        var cuarteles = obeliscos.Values.ToHashSet();

        foreach (var (coord, carta) in candidatas)
        {
            var owner = CartaHelper.OwnerUid(carta);
            var tipo = CartaHelper.Tipo(carta);
            var alcance = Alcanzables(coord, CartaHelper.MovimientoEfectivo(carta), tipo,
                terreno, celdasValidas, muros);

            var destinos = alcance
                .Where(d => d != coord)
                .Where(d => !cuarteles.Contains(d))
                .Where(d => !muros.Contains(d))
                .Where(d => !(protegidas.TryGetValue(d, out var s) && s != owner))
                .Where(d => PuedeAterrizar(d, tipo, terreno))
                .OrderBy(d => d, StringComparer.Ordinal)
                .ToList();
            if (destinos.Count == 0) continue;

            var destino = destinos[rng.Next(destinos.Count)];
            if (merged.TryGetValue(coord, out var lst)) lst.Remove(carta);
            if (!merged.TryGetValue(destino, out var dst)) { dst = new(); merged[destino] = dst; }
            dst.Add(carta);

            if (CartaHelper.EsInvisible(carta)) continue;
            var lanzador = M.List(M.Get(carta, "Efectos")).Select(M.Map)
                .Where(ef => M.Str(M.Get(ef, "tipo")) == TipoConfusion)
                .Select(ef => M.Str(M.Get(ef, "origenUid")))
                .FirstOrDefault() ?? "";
            log.Add(new Dictionary<string, object?>
            {
                ["tipo"] = "confusionMovimiento",
                ["uid"] = lanzador,
                ["propietarioUid"] = owner,
                ["zona"] = CartaHelper.OwnerZone(carta),
                ["origen"] = coord,
                ["cartaOrigenCoord"] = coord,
                ["destino"] = destino,
                ["cartaNombre"] = CartaHelper.Nombre(carta),
            });
        }

        foreach (var k in merged.Keys.Where(k => merged[k].Count == 0).ToList())
            merged.Remove(k);
    }

    // ═════════════════════════════════════════════════════════════════════════
    // COMBATE: los clones no luchan
    // ═════════════════════════════════════════════════════════════════════════

    /// Retira TODOS los clones de `t` antes del combate. Devuelve los que
    /// SOBREVIVEN (los que no compartían celda con ningún enemigo ni estaban en
    /// un cuartel enemigo) para reponerlos tras el combate. Los demás se
    /// disipan sin dejar rastro.
    public static Tablero ExtraerClones(
        Tablero t, Dictionary<string, string> obeliscos,
        Dictionary<string, string>? aliadoDe)
    {
        var supervivientes = new Tablero();
        var duenoCuartel = new Dictionary<string, string>();
        foreach (var kv in obeliscos) duenoCuartel[kv.Value] = kv.Key;

        bool Aliados(string a, string b)
            => aliadoDe != null && aliadoDe.TryGetValue(a, out var x) && x == b;

        foreach (var coord in t.Keys.ToList())
        {
            var lst = t[coord];
            if (!lst.Any(CartaHelper.EsClon)) continue;

            foreach (var clon in lst.Where(CartaHelper.EsClon).ToList())
            {
                var dueno = CartaHelper.OwnerUid(clon);
                var hayEnemigo = lst.Any(o =>
                {
                    var ou = CartaHelper.OwnerUid(o);
                    return ou != "" && ou != dueno && !Aliados(dueno, ou);
                });
                if (!hayEnemigo && duenoCuartel.TryGetValue(coord, out var cg)
                    && cg != dueno && !Aliados(dueno, cg))
                    hayEnemigo = true;

                if (hayEnemigo) continue; // se disipa
                if (!supervivientes.TryGetValue(coord, out var s)) { s = new(); supervivientes[coord] = s; }
                s.Add(clon);
            }

            lst.RemoveAll(CartaHelper.EsClon);
            if (lst.Count == 0) t.Remove(coord);
        }
        return supervivientes;
    }

    /// Repone los clones supervivientes tras el combate.
    public static void ReinsertarClones(Tablero t, Tablero clones)
    {
        foreach (var (coord, lst) in clones)
        {
            if (lst.Count == 0) continue;
            if (!t.TryGetValue(coord, out var dst)) { dst = new(); t[coord] = dst; }
            dst.AddRange(lst);
        }
    }

    /// Caducidad de los clones: −1 turno por resolución; a 0 desaparecen.
    public static void TickClones(Tablero t)
    {
        foreach (var coord in t.Keys.ToList())
        {
            var lst = t[coord];
            if (!lst.Any(CartaHelper.EsClon)) continue;
            var quedan = new List<Dictionary<string, object?>>();
            foreach (var c in lst)
            {
                if (!CartaHelper.EsClon(c)) { quedan.Add(c); continue; }
                var turnos = CartaHelper.ClonTurnos(c) - 1;
                if (turnos <= 0) continue;
                c["clonTurnos"] = turnos;
                quedan.Add(c);
            }
            if (quedan.Count == 0) t.Remove(coord);
            else t[coord] = quedan;
        }
    }

    // ═════════════════════════════════════════════════════════════════════════
    // BFS DE MOVIMIENTO (réplica de GameScreen._computeMovableBFS)
    // ═════════════════════════════════════════════════════════════════════════

    /// Celdas alcanzables desde `desde` con `mov` pasos ortogonales, pasando
    /// solo por celdas atravesables para `tipo` y nunca por `bloqueadas`.
    /// `celdasValidas` (opcional) acota el tablero. No incluye `desde`.
    public static HashSet<string> Alcanzables(
        string desde, int mov, int tipo,
        Dictionary<string, string>? terreno, HashSet<string>? celdasValidas,
        HashSet<string>? bloqueadas)
    {
        var res = new HashSet<string>();
        if (mov <= 0) return res;
        var p0 = Parse(desde);
        if (p0 == null) return res;

        var visitado = new Dictionary<string, int> { [desde] = 0 };
        var cola = new Queue<(string coord, int pasos)>();
        cola.Enqueue((desde, 0));
        var deltas = new[] { (-1, 0), (1, 0), (0, -1), (0, 1) };

        while (cola.Count > 0)
        {
            var (coord, pasos) = cola.Dequeue();
            if (pasos >= mov) continue;
            var p = Parse(coord);
            if (p == null) continue;
            foreach (var (dr, dc) in deltas)
            {
                int nr = p.Value.ri + dr, nc = p.Value.ci + dc;
                if (nr < 0 || nc < 0 || nr >= 26) continue;
                var n = Format(nr, nc);
                if (celdasValidas != null && !celdasValidas.Contains(n)) continue;
                var np = pasos + 1;
                if (visitado.TryGetValue(n, out var v) && v <= np) continue;
                if (bloqueadas != null && bloqueadas.Contains(n)) continue;
                if (!PuedeAtravesar(n, tipo, terreno)) continue;
                visitado[n] = np;
                if (n != desde) res.Add(n);
                if (np < mov) cola.Enqueue((n, np));
            }
        }
        return res;
    }

    /// ¿Para ir de `desde` a `hasta` la carta tuvo que atravesar un muro?
    public static bool CruzaMuro(
        string desde, string hasta, Dictionary<string, object?> cartaPrevia,
        HashSet<string> muros, Dictionary<string, string>? terreno,
        HashSet<string>? celdasValidas)
    {
        if (CartaHelper.EsEstatica(cartaPrevia)) return false;
        var mov = CartaHelper.MovimientoEfectivo(cartaPrevia);
        if (mov <= 0) return false;
        var tipo = CartaHelper.Tipo(cartaPrevia);
        var sinMuros = Alcanzables(desde, mov, tipo, terreno, celdasValidas, null);
        if (!sinMuros.Contains(hasta)) return false;
        var conMuros = Alcanzables(desde, mov, tipo, terreno, celdasValidas, muros);
        return !conMuros.Contains(hasta);
    }

    /// Paso por terreno (espejo de GameConfig.canTraverse):
    /// tierra → land/amphibious · aire → todo · mar → sea/deepSea/amphibious.
    public static bool PuedeAtravesar(string coord, int tipo, Dictionary<string, string>? terreno)
    {
        var t = terreno != null && terreno.TryGetValue(coord, out var v) ? v : "land";
        return tipo switch
        {
            1 => t is "land" or "amphibious",
            3 => t is "sea" or "deepSea" or "amphibious",
            _ => true,
        };
    }

    /// Aterrizaje por terreno (espejo de GameConfig.canLand / TeleCanLand).
    public static bool PuedeAterrizar(string coord, int tipo, Dictionary<string, string>? terreno)
        => Habilidades.TeleCanLand(coord, tipo, terreno ?? new Dictionary<string, string>());

    // ═════════════════════════════════════════════════════════════════════════
    // INTERNOS
    // ═════════════════════════════════════════════════════════════════════════

    /// Localiza la carta PROPIA (no clon) a copiar: primero en cartaOrigenCoord
    /// por id de catálogo o índice; si ya no está ahí (la movió un
    /// teletransporte o una fractura este turno) se busca en todo el tablero.
    private static Dictionary<string, object?>? LocalizarOriginal(
        Dictionary<string, object?> a, Tablero t, string uid)
    {
        var fromCoord = M.Str(M.Get(a, "cartaOrigenCoord"));
        var cartaId = M.Str(M.Get(a, "cartaOrigenId"));
        var idxObj = M.Get(a, "cartaOrigenIndice");

        bool Valida(Dictionary<string, object?> c)
            => CartaHelper.OwnerUid(c) == uid && !CartaHelper.EsClon(c);

        if (fromCoord != "" && t.TryGetValue(fromCoord, out var lst) && lst.Count > 0)
        {
            if (cartaId != "")
            {
                var porId = lst.FirstOrDefault(c => Valida(c) && M.Str(M.Get(c, "id", "Id")) == cartaId);
                if (porId != null) return porId;
            }
            else if (idxObj != null)
            {
                var idx = M.Int(idxObj);
                if (idx >= 0 && idx < lst.Count && Valida(lst[idx])) return lst[idx];
            }
        }

        if (cartaId == "") return null;
        foreach (var coord in t.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            var c = t[coord].FirstOrDefault(x => Valida(x) && M.Str(M.Get(x, "id", "Id")) == cartaId);
            if (c != null) return c;
        }
        return null;
    }

    /// True si cada celda (a partir de la segunda) es colindante con alguna de
    /// las anteriores: el muro forma una cadena conexa (recta o en L).
    public static bool Encadenadas(IReadOnlyList<string> celdas)
    {
        for (int i = 0; i < celdas.Count; i++)
        {
            if (Parse(celdas[i]) == null) return false;
            if (i == 0) continue;
            bool conectada = false;
            for (int j = 0; j < i && !conectada; j++)
                conectada = Manhattan(celdas[i], celdas[j]) == 1;
            if (!conectada) return false;
        }
        return true;
    }

    private static Dictionary<string, object?> ResumenCarta(Dictionary<string, object?> c) => new()
    {
        ["Nombre"] = CartaHelper.Nombre(c),
        ["ownerUid"] = CartaHelper.OwnerUid(c),
        ["ownerZone"] = CartaHelper.OwnerZone(c),
    };

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

    private static Dictionary<string, object?> Fallo(Dictionary<string, object?> a, Habilidad h, string motivo) => new()
    {
        ["tipo"] = "fallida",
        ["habilidadId"] = h.Id,
        ["habilidadNombre"] = h.Nombre,
        ["uid"] = M.Str(M.Get(a, "uid")),
        ["zona"] = M.Str(M.Get(a, "zona")),
        ["origen"] = M.Str(M.Get(a, "origen")),
        ["motivo"] = motivo,
    };

    public static int Manhattan(string a, string b)
    {
        var pa = Parse(a); var pb = Parse(b);
        if (pa == null || pb == null) return int.MaxValue;
        return Math.Abs(pa.Value.ri - pb.Value.ri) + Math.Abs(pa.Value.ci - pb.Value.ci);
    }

    private static (int ri, int ci)? Parse(string coord)
    {
        if (string.IsNullOrEmpty(coord) || coord.Length < 2) return null;
        int ri = char.ToUpperInvariant(coord[0]) - 'A';
        if (ri < 0 || ri >= 26) return null;
        if (!int.TryParse(coord[1..], out int col) || col < 1) return null;
        return (ri, col - 1);
    }

    private static string Format(int ri, int ci) => $"{(char)('A' + ri)}{ci + 1}";
}