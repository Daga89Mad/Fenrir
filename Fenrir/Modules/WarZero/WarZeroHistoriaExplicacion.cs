using System;
using System.Collections.Generic;
using System.Linq;

// ─────────────────────────────────────────────────────────────────────────────
// WarZeroHistoriaExplicacion.cs
//
// VENTANA EXPLICATIVA de las batallas del modo historia. Antes de empezar a
// jugar, el cliente muestra una ventana con cómo funciona la batalla. Su
// contenido lo genera el servidor AQUÍ, a partir de la propia `HistoriaDef`,
// y viaja en la partida como `historia.explicacion`:
//
//   [ { icono: "🎯", titulo: "Objetivo", texto: "…" }, … ]
//
// Secciones, en orden (solo las que aplican a la batalla):
//   📜 Introducción      · `HistoriaDef.Introduccion` (texto libre)
//   🎯 Objetivo           · cómo gana el jugador
//   💀 Derrota            · cómo pierde
//   ⚔  Tus tropas         · cartas, mano/mazo, energía, generales comprables
//   👹 El enemigo         · sus cartas, guarnición y cómo se comporta
//   💥 Bombardeo          · si la batalla tiene GuionBombardeo
//   ⛨  Casillas desactivadoras · si el bombardeo las tiene
//   ⚔  Asalto general     · si `HistoriaDef.TurnoAsaltoGeneral` > 0
//   🕳  El túnel inundable · si la batalla tiene túnel (HistoriaTuneles.cs)
//   🔑 Núcleos y puertas  · si la batalla es una cárcel (HistoriaCarcel.cs)
//   🚨 La alarma          · ídem
//   🔎 Marcas del tablero · 🎯 cazadores, ⚔ asalto, 👑 cartas clave, túnel, cárcel
//   …  `HistoriaDef.SeccionesExplicacion` (secciones extra a mano)
//
// Las reglas GENERALES del modo historia (turnos de tiempo, abandonar,
// reiniciar desde la parte 1) las añade el cliente, que conoce la duración
// real del turno.
//
// Como se genera de la definición, una historia NUEVA tiene su ventana sin
// escribir nada; solo hace falta `Introduccion` / `SeccionesExplicacion` para
// contar lo que no se deduce de los datos.
// ─────────────────────────────────────────────────────────────────────────────

public partial class WarZeroService
{
    /// Secciones de la ventana explicativa de [def], listas para guardar en
    /// `historia.explicacion`. [catalogo] resuelve los nombres de las cartas.
    internal static List<object?> ConstruirExplicacionHistoria(
        HistoriaDef def,
        Dictionary<string, Dictionary<string, object?>> catalogo,
        string cuartelJugador,
        string cuartelBot)
    {
        var secciones = new List<object?>();
        void Seccion(string icono, string titulo, string texto)
        {
            if (string.IsNullOrWhiteSpace(texto)) return;
            secciones.Add(new Dictionary<string, object?>
            {
                ["icono"] = icono,
                ["titulo"] = titulo,
                ["texto"] = texto.Trim(),
            });
        }

        // ── 📜 Introducción ──────────────────────────────────────────────────
        if (!string.IsNullOrWhiteSpace(def.Introduccion))
            Seccion("📜", "La batalla", def.Introduccion!);

        // Cartas CLAVE del jugador (nombres, para objetivo y derrota).
        var vips = def.Jugador.Cartas.Where(c => c.Vip)
            .Select(c => NombreCarta(catalogo, c.CartaId)).Distinct().ToList();

        // Duelo de generales (HistoriaDuelo.cs): objetivo, derrota y reglas propias.
        var duelo = HistoriaDuelos.Get(def.Id);
        string NombreDe(string id) => NombreCarta(catalogo, id);

        // Cárcel biónica (HistoriaCarcel.cs): objetivo, derrota, enemigo y reglas propias.
        var carcel = HistoriaCarcel.Get(def.Id);

        // ── 🎯 Objetivo ──────────────────────────────────────────────────────
        var objetivo = new List<string>();
        if (duelo != null && duelo.JugadorEsJefe)
        {
            objetivo.Add(duelo.JefeGanaAlLimite
                ? $"Eres {NombreDe(duelo.JefeId)}. Sigue con vida al cerrar el turno {duelo.TurnoLimite}, " +
                  $"o haz caer antes a {NombreDe(duelo.CazadorId)} o a {NombreDe(duelo.VerdugoId)}."
                : $"Eres {NombreDe(duelo.JefeId)}. Haz caer a {NombreDe(duelo.CazadorId)} " +
                  $"({duelo.VidasCazador} vidas) o a {NombreDe(duelo.VerdugoId)} ({duelo.VidasVerdugo} vidas) " +
                  $"antes de cerrar el turno {duelo.TurnoLimite}.");
            objetivo.Add($"{NombreDe(duelo.CazadorId)} te PARALIZA si coincide contigo; " +
                         $"{NombreDe(duelo.VerdugoId)} te quita una vida si entra mientras estás paralizado.");
        }
        else if (duelo != null && duelo.EsMuralla)
        {
            objetivo.Add($"Arranca los {duelo.VidasJefe} núcleos de BioZero de {NombreDe(duelo.JefeId)}.");
            objetivo.Add("Su escudo para todo lo que le llega de frente: golpéala por el flanco o la espalda, " +
                         "hazla hundirse en una de tus grietas o rompe su guardia con el odio.");
        }
        else if (duelo != null && duelo.EsEmbestida)
        {
            objetivo.Add($"Quita las {duelo.VidasJefe} vidas a {NombreDe(duelo.JefeId)}" +
                         (duelo.TurnoLimite > 0 ? $" antes de cerrar el turno {duelo.TurnoLimite}." : "."));
            objetivo.Add($"Solo puedes herirle cuando está ATURDIDO, y solo se aturde al estrellarse " +
                         "contra un pilar: haz que embista contra uno y entra en su casilla el turno siguiente.");
        }
        else if (duelo != null)
        {
            objetivo.Add($"Quita las {duelo.VidasJefe} vidas a {NombreDe(duelo.JefeId)}" +
                         (duelo.TurnoLimite > 0 ? $" antes de cerrar el turno {duelo.TurnoLimite}." : "."));
            objetivo.Add($"{NombreDe(duelo.CazadorId)} lo PARALIZA al caer en su casilla; " +
                         $"{NombreDe(duelo.VerdugoId)} le quita una vida si entra mientras está paralizado.");
        }
        else if (carcel != null)
        {
            objetivo.Add($"Escapa de la cárcel: abre las {carcel.Salas.Count} puertas, una por sala, y cruza la " +
                         $"última ({HistoriaCarcel.N(carcel.Salas[^1].Puerta)}).");
            objetivo.Add("Cada puerta solo se abre con el núcleo de BioZero que lleva uno de los guardias de su " +
                         "sala, y no sabes cuál. No hay límite de turnos, pero la alarma no deja de subir.");
        }
        else if (def.VictoriaSinEnemigos)
        {
            objetivo.Add("Elimina a TODAS las tropas enemigas, también los refuerzos que vayan llegando.");
            objetivo.Add($"Mientras tanto, que no conquisten tu cuartel ({cuartelJugador})" +
                         (vips.Count > 0 ? $" y que sigan con vida {UnirY(vips)}." : "."));
        }
        else if (def.Jugador.Objetivo == ObjetivoHistoria.Conquistar)
        {
            objetivo.Add($"Conquista el cuartel enemigo ({cuartelBot}).");
            if (def.TurnosSupervivencia > 0)
                objetivo.Add($"También ganas si sigues en pie al cerrar el turno {def.TurnosSupervivencia}.");
            else
                objetivo.Add("No hay límite de turnos.");
        }
        else
        {
            objetivo.Add(def.TurnosSupervivencia > 0
                ? $"Resiste hasta cerrar el turno {def.TurnosSupervivencia} sin que conquisten tu cuartel ({cuartelJugador})."
                : $"Resiste sin que conquisten tu cuartel ({cuartelJugador}).");
        }
        Seccion("🎯", "Objetivo", string.Join("\n", objetivo));

        // ── 💀 Derrota ───────────────────────────────────────────────────────
        var derrota = duelo != null || carcel != null
            ? new List<string>()
            : new List<string> { $"• Si conquistan tu cuartel ({cuartelJugador})." };
        if (duelo != null && duelo.JugadorEsJefe)
        {
            derrota.Add($"• Si {NombreDe(duelo.JefeId)} pierde sus {duelo.VidasJefe} vidas.");
            if (!duelo.JefeGanaAlLimite && duelo.TurnoLimite > 0)
                derrota.Add($"• Si se cierra el turno {duelo.TurnoLimite} sin que haya caído ninguno de los dos.");
        }
        else if (duelo != null && duelo.EsMuralla)
        {
            derrota.Add($"• Si {NombreDe(duelo.CazadorId)} pierde sus {duelo.VidasCazador} vidas.");
            derrota.Add("• Si la línea biónica te alcanza y no tienes a dónde retroceder: caes de la muralla.");
        }
        else if (duelo != null && duelo.EsEmbestida)
        {
            derrota.Add($"• Si {NombreDe(duelo.CazadorId)} pierde sus {duelo.VidasCazador} vidas.");
            if (duelo.TurnoLimite > 0)
                derrota.Add($"• Si {NombreDe(duelo.JefeId)} sigue vivo al cerrar el turno {duelo.TurnoLimite}: escapa.");
        }
        else if (duelo != null)
        {
            derrota.Add($"• Si {NombreDe(duelo.CazadorId)} ({duelo.VidasCazador} vidas) o " +
                        $"{NombreDe(duelo.VerdugoId)} ({duelo.VidasVerdugo} vidas) se quedan sin vidas.");
            if (duelo.TurnoLimite > 0)
                derrota.Add($"• Si {NombreDe(duelo.JefeId)} sigue vivo al cerrar el turno {duelo.TurnoLimite}: escapa.");
        }
        else
            foreach (var v in vips)
                derrota.Add($"• Si muere {v} (👑 carta clave).");
        if (carcel != null)
            derrota.Add($"• Si la alarma llega a {carcel.AlarmaMax}: la cárcel se sella.");
        if (def.DerrotaJugadorSinCartas)
            derrota.Add(def.EsPartidaNormal
                ? "• Si te quedas sin cartas en el tablero y en la mano."
                : "• Si te quedas sin cartas en el tablero.");
        Seccion("💀", "Derrota", string.Join("\n", derrota));

        // ── ⚔ Tus tropas ─────────────────────────────────────────────────────
        var tropas = new List<string>();
        if (def.EsPartidaNormal)
        {
            if (def.Jugador.Cartas.Count > 0)
            {
                tropas.Add($"Empiezas con estas cartas en tu cuartel ({cuartelJugador}):");
                tropas.AddRange(LineasCartas(def.Jugador.Cartas, catalogo));
            }
            var mazo = def.Jugador.Mazo ?? Array.Empty<CartaHistoria>();
            if (mazo.Count > 0)
            {
                tropas.Add("Juegas con mano y robas 1 carta por turno de este mazo fijo:");
                tropas.AddRange(LineasCartas(mazo, catalogo, mostrarEvolucion: false));
            }
            tropas.Add($"Empiezas con {def.Jugador.EnergiaInicial} Ø.");
        }
        else if (def.Jugador.Cartas.Any(c => !string.IsNullOrWhiteSpace(c.Coord)))
        {
            tropas.Add("No tienes mano ni mazo: solo juegas con estas cartas, que empiezan así:");
            tropas.AddRange(LineasPorCelda(def.Jugador.Cartas, catalogo, cuartelJugador, esBot: false));
            if (carcel != null)
                tropas.Add($"Una vez en toda la batalla puedes desatar su ODIO: +{carcel.OdioFuerza} de fuerza " +
                           "en el combate de ese turno (botón de habilidad de su ficha, sin coste).");
            else if (duelo == null)
                tropas.Add($"Empiezas con {def.Jugador.EnergiaInicial} Ø y recibes +{Math.Max(0, def.SuerteDelPerdedor)} Ø cada turno.");
        }
        else
        {
            tropas.Add($"Todas tus cartas empiezan en tu cuartel ({cuartelJugador}). " +
                       "No tienes mano ni mazo: solo juegas con estas:");
            tropas.AddRange(LineasCartas(def.Jugador.Cartas, catalogo));
            tropas.Add($"Empiezas con {def.Jugador.EnergiaInicial} Ø y recibes +{Math.Max(0, def.SuerteDelPerdedor)} Ø cada turno.");
        }
        var guarnicionJug = def.Jugador.Cartas.Where(c => c.Guarnicion > 0).ToList();
        if (guarnicionJug.Count > 0)
            tropas.Add("GUARNICIÓN (no se mueven nunca de tu cuartel): " +
                       string.Join(", ", guarnicionJug.Select(c => $"{c.Guarnicion} × {NombreCarta(catalogo, c.CartaId)}")) + ".");
        var especiales = (def.Jugador.EspecialesCuartel ?? Array.Empty<string>())
            .Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
        if (especiales.Count > 0)
            tropas.Add("En tu cuartel puedes comprar (una vez cada uno): " +
                       string.Join(", ", especiales.Select(id => NombreCarta(catalogo, id))) + ".");
        Seccion("⚔", "Tus tropas", string.Join("\n", tropas));

        // ── 👹 El enemigo ────────────────────────────────────────────────────
        var enemigoNombre = string.IsNullOrWhiteSpace(def.Bot.Alias)
            ? NombreEjercito(def.Bot.Ejercito)
            : def.Bot.Alias!;
        var enemigo = new List<string>();
        if (carcel != null)
        {
            // Resumen por sala (la lista casilla a casilla sería enorme).
            enemigo.Add($"{enemigoNombre} vigila cada sala:");
            foreach (var s in carcel.Salas)
            {
                var guardias = s.Guardias.GroupBy(g => g.CartaId)
                    .Select(g => $"{g.Count()} × {NombreCarta(catalogo, g.Key)}");
                enemigo.Add($"• {s.Nombre}: {string.Join(", ", guardias)}.");
            }
            enemigo.Add("Los guardias no se mueven de su puesto… salvo el que lleva el núcleo, que en las salas " +
                        "marcadas huye de ti en cuanto te acercas.");
        }
        else if (def.Bot.Cartas.Count > 0)
        {
            if (def.Bot.Cartas.Any(c => !string.IsNullOrWhiteSpace(c.Coord)))
            {
                enemigo.Add($"{enemigoNombre} empieza con:");
                enemigo.AddRange(LineasPorCelda(def.Bot.Cartas, catalogo, cuartelBot, esBot: true));
            }
            else
            {
                enemigo.Add($"{enemigoNombre} tiene en su cuartel ({cuartelBot}):");
                enemigo.AddRange(LineasCartas(def.Bot.Cartas, catalogo));
            }
        }
        else
        {
            enemigo.Add($"{enemigoNombre} empieza sin tropas en el tablero y las despliega desde su mano.");
        }
        var guarnicion = def.Bot.Cartas.Where(c => c.Guarnicion > 0).ToList();
        if (guarnicion.Count > 0)
            enemigo.Add("No salen nunca de su cuartel: " +
                        string.Join(", ", guarnicion.Select(c => $"{c.Guarnicion} × {NombreCarta(catalogo, c.CartaId)}")) + ".");
        if (def.CuartelBotInaccesible && carcel == null)
            enemigo.Add($"Su cuartel ({cuartelBot}) es solo un campamento: no puedes entrar en él ni hace falta conquistarlo.");

        bool hayAsalto = def.TurnoAsaltoGeneral > 0
                         || def.Bot.Cartas.Any(c => c.Rol == RolBotHistoria.Asalto);
        if (carcel != null)
        {
            // Ya descrito arriba.
        }
        else if (def.EsPartidaNormal)
            enemigo.Add("Juega como un comandante: despliega cartas desde su mano y roba cada turno.");
        else
            enemigo.Add(def.ComportamientoBotEfectivo switch
            {
                ComportamientoBotHistoria.Cazar => hayAsalto
                    ? "🎯 CAZADORES: salen a cazar tus cartas" +
                      (vips.Count > 0 ? $", primero a {UnirY(vips)}" : "") +
                      ", y se comen el cebo que encuentren. Solo entran en tu casilla si llegan con más " +
                      "fuerza (F + D) que tu grupo; si no, te acechan desde cerca.\n" +
                      "⚔ ASALTO: no cazan; esperan" +
                      (string.IsNullOrWhiteSpace(def.ReunionAsalto) ? "" : $" en {def.ReunionAsalto}") +
                      " para lanzarse contra tu cuartel."
                    : "Sus tropas salen a CAZAR tus cartas: se reparten entre tus grupos y solo " +
                      "entran en tu casilla si llegan con más fuerza (F + D) que el grupo que persiguen. " +
                      "Si no, te acechan desde cerca.",
                ComportamientoBotHistoria.Defender =>
                    "Se atrinchera y defiende su cuartel.",
                ComportamientoBotHistoria.Duelo when duelo != null && duelo.EsMuralla =>
                    "Camina siempre de frente hacia ti y nunca retrocede. Al empezar cada turno ves lo " +
                    "que va a hacer: cargar en línea recta o girarse. Tras ella avanza la línea biónica.",
                ComportamientoBotHistoria.Duelo when duelo != null && duelo.EsEmbestida =>
                    "Te acecha y embiste. Antes de cada carga se coloca para que no tengas un pilar " +
                    "a la espalda; no siempre lo consigue: léelo y provócalo.",
                ComportamientoBotHistoria.Duelo => duelo != null && duelo.JugadorEsJefe
                    ? $"{NombreDe(duelo.CazadorId)} te persigue para paralizarte y {NombreDe(duelo.VerdugoId)} " +
                      "espera cerca a que lo consiga para herirte. Esquivan el % del Rompe escudos, pero no " +
                      "saben dónde harás caer las rocas."
                    : "Huye de quien puede paralizarlo y castiga cualquier error. No es perfecto: " +
                      "obsérvalo, aprende cómo se mueve y acorrálalo entre los pilares.",
                _ =>
                    "Avanza hacia tu cuartel para conquistarlo.",
            });
        var guionOleadas = HistoriaGuiones.Get(def.Id);
        if (guionOleadas != null)
        {
            enemigo.Add("Recibirá refuerzos en oleadas durante la batalla:");
            foreach (var o in guionOleadas.Oleadas.OrderBy(o => o.TurnoInicio))
            {
                var grupos = o.Grupos.Select(g =>
                {
                    int evo = Math.Clamp(g.CantidadEvolucionada, 0, Math.Max(1, g.Cantidad));
                    var nombre = NombreCarta(catalogo, g.CartaId);
                    var txt = $"{Math.Max(1, g.Cantidad)} × {nombre}";
                    if (evo > 0)
                    {
                        var idEvo = IdEvolucionDe(catalogo, g.CartaId);
                        txt += evo == g.Cantidad
                            ? (idEvo != "" ? $" (ya {NombreCarta(catalogo, idEvo)})" : " (evolucionadas)")
                            : (idEvo != "" ? $" ({evo} ya {NombreCarta(catalogo, idEvo)})" : $" ({evo} evolucionadas)");
                    }
                    if (g.Rol == RolBotHistoria.Asalto) txt += " ⚔";
                    else if (g.Rol == RolBotHistoria.Cazador) txt += " 🎯";
                    return txt;
                });
                var donde = o.Grupos.Select(g => g.Coordenada).Distinct().ToList();
                enemigo.Add($"• Turno {o.TurnoInicio}: {string.Join(", ", grupos)}" +
                            (donde.Count == 1 && !donde[0].StartsWith("CUARTEL") ? $" en {donde[0]}." : "."));
            }
        }
        Seccion("👹", "El enemigo", string.Join("\n", enemigo));

        // ── ⚔ Asalto general ─────────────────────────────────────────────────
        if (def.TurnoAsaltoGeneral > 0)
            Seccion("⚔", "Asalto general",
                $"Desde el turno {def.TurnoAsaltoGeneral} TODAS sus tropas dejan de cazar" +
                (string.IsNullOrWhiteSpace(def.ReunionAsalto)
                    ? " y van a por tu cuartel."
                    : $", se reúnen en {def.ReunionAsalto} y entran JUNTAS en tu cuartel, sumando su fuerza.") +
                " Prepara la defensa antes de ese turno.");

        // ── ⚔ Duelo ──────────────────────────────────────────────────────────
        if (duelo != null)
            Seccion("⚔", "El duelo", TextoDuelo(duelo, NombreDe));

        // ── 🕳 Túnel ─────────────────────────────────────────────────────────
        var tunel = HistoriaTuneles.Get(def.Id);
        if (tunel != null)
            Seccion("🕳", "El túnel inundable", TextoTunel(tunel));

        // ── 🔑 Cárcel ────────────────────────────────────────────────────────
        if (carcel != null)
        {
            Seccion("🔑", "Núcleos y puertas", TextoCarcelNucleos(carcel));
            Seccion("🚨", "La alarma", TextoCarcelAlarma(carcel));
        }

        // ── 🔎 Marcas del tablero ────────────────────────────────────────────
        if (def.ComportamientoBotEfectivo == ComportamientoBotHistoria.Cazar || vips.Count > 0 || duelo != null)
        {
            var marcas = new List<string>();
            if (duelo != null && duelo.JugadorEsJefe)
            {
                marcas.Add($"• ❤ vidas de cada general · 🔗 {NombreDe(duelo.JefeId)} paralizado · 🛡✖ {NombreDe(duelo.CazadorId)} sin escudo.");
                marcas.Add("• 🪨 casillas que has elegido para tu lluvia de rocas · 🛡 % de Rompe escudos sobre Alvaroth.");
                marcas.Add("• Las casillas negras (pilares) no se pueden pisar ni atravesar.");
            }
            else if (duelo != null && duelo.EsMuralla)
            {
                marcas.Add($"• ❤ vidas de {NombreDe(duelo.CazadorId)} y núcleos de {NombreDe(duelo.JefeId)} · " +
                           "▲▶▼◀ hacia dónde mira (su escudo).");
                marcas.Add("• Casillas rojas: el carril de su carga de este turno · ↻: este turno se gira · " +
                           "🛡 alrededor: barrido.");
                marcas.Add("• 🛡 filas negras: la línea biónica · ⚡ grieta · ⚡⚡ grieta profunda · 🕳 hueco " +
                           "(no se puede pisar).");
                marcas.Add("• 🔥 tus puntos de odio.");
            }
            else if (duelo != null && duelo.EsEmbestida)
            {
                marcas.Add($"• ❤ vidas de {NombreDe(duelo.CazadorId)} y de {NombreDe(duelo.JefeId)} · 🔗 aturdido.");
                marcas.Add("• 🐂 casillas rojas: el carril de la embestida de este turno.");
                marcas.Add("• ⛰ pilar en pie (aturde) · ✖ escombro (bloquea, pero ya no aturde). " +
                           "Las casillas negras no se pueden pisar ni atravesar.");
            }
            else if (duelo != null)
            {
                marcas.Add("• ❤ vidas de cada general · 🔗 Alexander paralizado · 🛡✖ sin escudo.");
                marcas.Add("• 💥 % de rocas en los turnos de lluvia · 🛡 % de Rompe escudos.");
                marcas.Add("• Las casillas bloqueadas (pilares) no se pueden pisar ni atravesar.");
            }
            if (def.ComportamientoBotEfectivo == ComportamientoBotHistoria.Cazar)
            {
                marcas.Add("• 🎯 sobre una casilla enemiga: cazadores (cuántos).");
                if (hayAsalto) marcas.Add("• ⚔ sobre una casilla enemiga: tropas de asalto.");
            }
            if (vips.Count > 0) marcas.Add("• 👑 tus cartas clave: si mueren, pierdes.");
            if (carcel != null)
            {
                marcas.Add("• 🔑 / 🪶 + % sobre un guardia: probabilidad de que lleve ese núcleo. Se actualiza " +
                           "con cada guardia que cae, cada pista y cada intercambio.");
                marcas.Add("• 🚪 puerta cerrada (en verde: ya puedes entrar) · casillas negras: muros.");
                marcas.Add("• 🔀 guardias que se acaban de pasar los núcleos · 💨 guardia que acaba de huir.");
            }
            if (tunel != null)
                marcas.Add("• Túnel: casilla oscura = sin explorar · ✓ = limpia · azul 💧 = inundada.");
            Seccion("🔎", "Marcas del tablero", string.Join("\n", marcas));
        }

        // ── 💥 Bombardeo ─────────────────────────────────────────────────────
        var guion = HistoriaBombardeos.Get(def.Id);
        if (guion != null)
        {
            var bombardeo = new List<string>
            {
                "Cada turno caen disparos lejanos en CADA fila del tablero: " + TextoTramos(guion.Tramos) + ".",
                "Cada disparo destruye TODO lo que haya en su casilla.",
                "El % que ves en cada casilla es la probabilidad real de que le caiga un disparo ese turno.",
            };
            if (guion.Castigo != null)
                bombardeo.Add($"Agruparse se paga: un grupo de más de {guion.Castigo.Umbral} cartas en la misma " +
                              "casilla atrae el fuego sobre su zona. Sepárate.");
            bombardeo.Add("Los disparos nunca caen sobre las tropas enemigas ni sobre casillas con tu escudo.");
            Seccion("💥", "Bombardeo", string.Join("\n", bombardeo));

            // ── ⛨ Casillas desactivadoras ────────────────────────────────────
            var cfg = guion.Desactivadoras;
            if (cfg != null && (cfg.UnaEnElCentro || cfg.AlAzar > 0))
            {
                int total = (cfg.UnaEnElCentro ? 1 : 0) + Math.Max(0, cfg.AlAzar);
                string donde = cfg.UnaEnElCentro && cfg.AlAzar > 0
                    ? (cfg.AlAzar == 1 ? "una en el centro del tablero y otra al azar"
                                       : $"una en el centro del tablero y {cfg.AlAzar} al azar")
                    : cfg.UnaEnElCentro ? "en el centro del tablero" : "al azar";
                var desact = new List<string>
                {
                    $"Hay {total} casilla{(total == 1 ? "" : "s")} desactivadora{(total == 1 ? "" : "s")} " +
                    $"(marcadas en cian): {donde}. Cambian de sitio cada {Math.Max(1, cfg.DuracionTurnos)} turnos.",
                    "Si terminas el turno con una carta en una de ellas:",
                    $"• el turno siguiente cae {cfg.ReduccionPorCasilla} disparo menos EN CADA FILA por cada desactivadora ocupada;",
                    $"• se forma sobre ella un escudo tuyo de {cfg.EscudoTurnos} turnos: los disparos no le afectan " +
                    "y el enemigo no puede entrar.",
                };
                Seccion("⛨", "Casillas desactivadoras", string.Join("\n", desact));
            }
        }

        // ── Secciones extra de la definición ────────────────────────────────
        foreach (var s in def.SeccionesExplicacion ?? Array.Empty<SeccionExplicacion>())
            Seccion(string.IsNullOrWhiteSpace(s.Icono) ? "•" : s.Icono, s.Titulo, s.Texto);

        return secciones;
    }

    /// Texto de la sección del DUELO a partir de su configuración.
    private static string TextoDuelo(ConfigDuelo d, Func<string, string> nombre)
    {
        if (d.EsEmbestida) return TextoDueloEmbestida(d, nombre);
        if (d.EsMuralla) return TextoDueloMuralla(d, nombre);
        if (d.JugadorEsJefe) return TextoDueloInvertido(d, nombre);
        var jefe = nombre(d.JefeId);
        var caz = nombre(d.CazadorId);
        var ver = nombre(d.VerdugoId);
        string Cada(int primero, int cada) => cada == 1
            ? $"todos los turnos desde el {primero}"
            : $"cada {cada} turnos desde el turno {primero}";
        var l = new List<string>
        {
            $"• Aquí no hay combate normal: cada general tiene VIDAS ({jefe} {d.VidasJefe} · {caz} {d.VidasCazador} · {ver} {d.VidasVerdugo}).",
            $"• {caz} (con escudo) cae en la casilla de {jefe} libre → lo PARALIZA {d.TurnosParalisis} turno.",
            $"• {ver} entra en la casilla de {jefe} paralizado → le quita una vida; {jefe} se libera y salta lejos.",
            $"• Si {caz} y {ver} caen JUNTOS sobre {jefe} libre, os SEPARA: {caz} sale despedido lejos y {ver} a una casilla contigua.",
            $"• {ver} solo en la casilla de {jefe} libre pierde una vida.",
            $"• LLUVIA DE ROCAS ({Cada(d.LluviaPrimerTurno, d.LluviaCadaTurnos)}): {jefe} canaliza y NO SE MUEVE. " +
            (d.FilasConLluvia > 0
                ? $"Llueve en {d.FilasConLluvia} filas al azar ({d.FilasConLluviaFuria} con su última vida), {d.DisparosPorFila} roca por fila; "
                : $"Caen {d.DisparosPorFila} roca(s) en cada fila ({d.DisparosPorFilaFuria} con su última vida); ") +
            "ves el % de cada casilla (0 % = a salvo). " +
            "Cada roca quita una vida al general que esté debajo.",
        };
        if (d.RompeCadaTurnos > 0)
            l.Add($"• ROMPE ESCUDOS ({Cada(d.RompePrimerTurno, d.RompeCadaTurnos)}): ves un % en las casillas a las que puede ir {caz}. " +
                  $"Si termina en la que recibe el golpe, se queda SIN ESCUDO ese turno y {d.TurnosSinEscudo} más: no puede paralizar, " +
                  $"{jefe} irá a por él y, si coinciden, {caz} pierde una vida.");
        if (d.TurnoLimite > 0)
            l.Add($"• Tienes hasta el turno {d.TurnoLimite}.");
        return string.Join("\n", l);
    }

    /// Texto del DUELO DE LA MURALLA (nefilim_2).
    private static string TextoDueloMuralla(ConfigDuelo d, Func<string, string> nombre)
    {
        var m = d.Muralla!;
        var jefe = nombre(d.JefeId).Replace("General ", "");
        var gen = nombre(d.CazadorId).Replace("General ", "");
        string Turnos(int n) => n == 1 ? "cada turno" : $"cada {n} turnos";
        var l = new List<string>
        {
            $"• Aquí no hay combate normal: {jefe} tiene {d.VidasJefe} NÚCLEOS y {gen} {d.VidasCazador} vidas.",
            $"• ESCUDO FRONTAL: {jefe} siempre mira hacia un lado (la flecha). Si entras en su casilla viniendo " +
            "de DELANTE, pierdes una vida y sales despedido. Si vienes de un LATERAL o de la ESPALDA, ella pierde " +
            "un núcleo. No puedes atravesar su casilla: solo terminar en ella.",
            "• NUNCA RETROCEDE. Al empezar cada turno ves lo que va a hacer:",
            $"   – CARGA: si estás delante, avanza en línea recta hasta donde estabas (como mucho {m.AlcanceCarga} " +
            "casillas). Si terminas en su carril (en rojo), te arrolla.",
            "   – BARRIDO: si empiezas el turno pegado a ella (también en diagonal) y no estás delante, barre " +
            "con el escudo su casilla y las 8 de alrededor. Aléjate.",
            "   – GIRO: en cualquier otro caso se gira 90° hacia ti sin moverse. Es tu momento: si empezaste el " +
            "turno a 2 casillas, entra en la suya y la golpearás por el flanco.",
            "• Tras cada golpe sales despedido delante de ella y se vuelve hacia ti: el turno siguiente te carga.",
            $"• LÍNEA BIÓNICA: tras ella avanza una fila de soldados {Turnos(m.LineaCada)}. No puedes pisarla. " +
            "Si te alcanza, pierdes una vida y retrocedes; cuando ya no queda muralla, caes.",
            $"• GRIETAS: cada vez que terminas un turno en una casilla, la agrietas (⚡). A la {m.GrietaPisadas}.ª vez " +
            $"es una GRIETA PROFUNDA (⚡⚡). Si {jefe} TERMINA una carga sobre una, se hunde: queda ATURDIDA el " +
            "turno siguiente y cualquier golpe le quita un núcleo. Después la casilla es un hueco." +
            (m.GrietaDuracion > 0 ? $" Una grieta que no vuelves a pisar en {m.GrietaDuracion} turnos se asienta." : ""),
            $"• ODIO: cada vida que pierdes te da 1 punto de odio (máx. {m.OdioMax}). Con {m.OdioCoste}, lanza " +
            "desde la ficha de Alexander la EMBESTIDA DEL ODIO: ese turno, chocar con ella de frente le quita un " +
            "núcleo a ella. Si no llegas a tocarla, conservas el odio.",
            $"• Con {m.VidasFase2} núcleos: ESCUDO DOBLE (cubre también el lado hacia el que gira: solo la espalda la " +
            $"hiere), su carga llega a {m.AlcanceCargaFase2} casillas, barre un carril de {m.AnchoCargaFase2} de " +
            "ancho y carga contra todo lo que tenga delante.",
            $"• Con {m.VidasFase3} núcleo: GIRA Y CARGA en el mismo turno y la línea avanza {Turnos(m.LineaCadaFase3)}. " +
            "Solo te quedan tus grietas, el odio… o su espalda.",
        };
        return string.Join("\n", l);
    }

    /// Texto del DUELO DE LA EMBESTIDA (bionicos_3).
    private static string TextoDueloEmbestida(ConfigDuelo d, Func<string, string> nombre)
    {
        var jefe = nombre(d.JefeId);
        var gen = nombre(d.CazadorId);
        int pilares = d.Pilares?.Count ?? 0;
        string Cada(int primero, int cada) => cada == 1
            ? $"todos los turnos desde el {primero}"
            : $"cada {cada} turnos, empezando en el turno {primero}";
        var l = new List<string>
        {
            $"• Aquí no hay combate normal: cada uno tiene VIDAS ({gen} {d.VidasCazador} · {jefe} {d.VidasJefe}).",
            $"• ACECHO: {jefe} mueve {d.MovimientoJefe} hacia ti. Si acabáis en la misma casilla y no está aturdido, pierdes una vida.",
            $"• EMBESTIDA ({Cada(d.CargaPrimerTurno, d.CargaCadaTurnos)}): al empezar el turno ves el CARRIL en rojo, " +
            "de 3 casillas de ancho, en la fila o columna que apunta a ti. Recorre la línea hasta chocar con algo. " +
            "Si terminas el turno dentro del carril, pierdes una vida y sales despedido.",
            "• Si choca contra un PILAR" +
            (d.CuernosChocan ? " (de frente, o rozándolo con el carril al pasar junto a él)" : "") +
            " queda ATURDIDO el turno siguiente y el pilar se derrumba. " +
            $"Entra entonces en su casilla: pierde una vida y salta lejos. Contra el borde o los escombros no le pasa nada.",
            $"• Hay {pilares} pilares y {jefe} tiene {d.VidasJefe} vidas: cada pilar sirve una sola vez." +
            (d.BordeAturdeSinPilares ? " Si se acaban, el borde del valle también le aturde." : ""),
        };
        if (d.VidasOnda > 0)
            l.Add($"• Con {d.VidasOnda} vida{(d.VidasOnda == 1 ? "" : "s")} o menos, cada choque contra un pilar levanta una ONDA " +
                  "en las 8 casillas que lo rodean: si estás en una, pierdes una vida.");
        if (d.CargaEnLFuria)
            l.Add("• Con su última vida entra en FURIA: si el primer tramo no acaba en un pilar, gira 90° hacia donde " +
                  "estés y vuelve a cargar. Ese giro NO se avisa: ponte a cubierto tras un pilar o un escombro.");
        if (d.TurnoLimite > 0)
            l.Add($"• Tienes hasta el turno {d.TurnoLimite}.");
        return string.Join("\n", l);
    }

    /// Texto del DUELO cuando el jugador lleva al jefe (reto «El duelo de
    /// Alexander»).
    private static string TextoDueloInvertido(ConfigDuelo d, Func<string, string> nombre)
    {
        var jefe = nombre(d.JefeId);
        var caz = nombre(d.CazadorId);
        var ver = nombre(d.VerdugoId);
        string Cada(int primero, int cada) => cada == 1
            ? $"todos los turnos desde el {primero}"
            : $"cada {cada} turnos desde el turno {primero}";
        string Filas(int n) => n > 0 ? $"en {n} fila{(n == 1 ? "" : "s")} como mucho" : "en cualquier fila";
        var l = new List<string>
        {
            $"• Aquí no hay combate normal: cada general tiene VIDAS ({jefe} {d.VidasJefe} · {caz} {d.VidasCazador} · {ver} {d.VidasVerdugo}).",
            $"• Si {caz} (con escudo) y tú termináis en la misma casilla, te PARALIZA: el turno siguiente no puedes moverte.",
            $"• Si {ver} entra en tu casilla mientras estás paralizado, pierdes una vida y saltas lejos.",
            $"• Si {ver} está SOLA en tu casilla y no estás paralizado, es ella quien pierde una vida.",
            $"• Si {caz} y {ver} están JUNTOS en tu casilla, los SEPARAS: {caz} sale despedido lejos y {ver} a una casilla contigua.",
        };
        if (d.LluviaManual)
            l.Add($"• LLUVIA DE ROCAS (tu habilidad, sin coste): abre la ficha de {jefe} y lánzala. Eliges TÚ las casillas " +
                  $"donde cae una roca: {d.RocasPorFila(false)} por fila {Filas(d.MaxFilasLluvia(false))}" +
                  (d.MaxFilasLluvia(true) != d.MaxFilasLluvia(false) ? $" ({Filas(d.MaxFilasLluvia(true))} con tu última vida)" : "") +
                  ". Cada roca quita una vida al general que esté debajo. El turno en que la lanzas NO te mueves y " +
                  $"después necesita {d.LluviaRecarga} turno{(d.LluviaRecarga == 1 ? "" : "s")} de recarga.");
        if (d.RompeCadaTurnos > 0)
            l.Add($"• ROMPE ESCUDOS (automático, {Cada(d.RompePrimerTurno, d.RompeCadaTurnos)}): verás un % morado en las " +
                  $"casillas a las que puede ir {caz}. Si termina en la que recibe el golpe, se queda SIN ESCUDO ese turno y " +
                  $"{d.TurnosSinEscudo} más: no puede paralizarte y, si coincidís, pierde una vida.");
        if (d.TurnoLimite > 0)
            l.Add(d.JefeGanaAlLimite
                ? $"• Si sigues vivo al cerrar el turno {d.TurnoLimite}, ganas."
                : $"• Tienes hasta el turno {d.TurnoLimite}: si no ha caído ninguno, pierdes.");
        return string.Join("\n", l);
    }

    /// Texto de la sección del TÚNEL a partir de su configuración.
    private static string TextoTunel(ConfigTunel cfg)
    {
        var entrada = cfg.Tramos[0];
        var salida = cfg.Tramos[^1];
        string Bocas(IEnumerable<string> celdas) => string.Join("/", celdas
            .SelectMany(c => cfg.Bocas.TryGetValue(c, out var ext) ? ext : Array.Empty<string>()));
        int ancho = cfg.Tramos.Count > 0 ? cfg.Tramos.Max(t => t.Count) : 3;
        var lineas = new List<string>
        {
            $"• Va de {string.Join("-", entrada.First(), entrada.Last())} (se entra desde {Bocas(entrada)}) " +
            $"a {string.Join("-", salida.First(), salida.Last())} (se sale a {Bocas(salida)}). " +
            "Solo se entra y se sale por sus bocas: el resto son paredes.",
            $"• Dentro, TODAS las cartas mueven {cfg.Movimiento} casillas por turno, y solo por casillas del túnel.",
            $"• {cfg.TramosConAgua} tramos tienen agua (casillas oscuras). En cada uno solo UNA de sus {ancho} " +
            $"casillas es segura y no sabes cuál: en cada paso eliges entre {ancho} opciones.",
            "• Entrar en una casilla sin explorar termina el movimiento. Al resolver el turno, si era la segura " +
            "queda LIMPIA (✓) y todos pueden pasar; si no, se INUNDA (azul 💧): mueren las cartas que haya en " +
            "ella y ya nadie puede entrar.",
            "• Divide tu grupo: manda exploradores por delante y que el resto pise solo casillas limpias. " +
            "El camino cambia en cada partida.",
            "• El enemigo no puede entrar en el túnel ni ve lo que hay dentro… pero puede esperarte a la salida.",
        };
        return string.Join("\n", lineas);
    }

    /// Texto de NÚCLEOS Y PUERTAS de la cárcel a partir de su configuración.
    private static string TextoCarcelNucleos(ConfigCarcel cfg)
    {
        string Turnos(int n) => n == 1 ? "1 turno" : $"{n} turnos";
        var l = new List<string>
        {
            "• En cada sala, UNO de los guardias lleva el núcleo que abre su puerta. Cada guardia muestra el % " +
            "de llevarlo: los de más rango lo llevan más a menudo.",
            "• Mata al portador y el núcleo es tuyo. Matar a uno equivocado no te elimina: solo pierdes " +
            "tiempo y la alarma sube.",
            "• Para cruzar una puerta tienes que TERMINAR el movimiento sobre ella llevando sus núcleos. " +
            "Si hay guardias dentro, tendrás que vencerlos. Después queda abierta.",
        };
        for (int k = 0; k < cfg.Salas.Count; k++)
        {
            var s = cfg.Salas[k];
            var slots = string.Join(" + ", s.Slots.Select(n =>
                $"{n.Icono} {n.Nombre} ({(n.Duracion > 0 ? $"{Turnos(n.Duracion)} para llegar a la puerta" : "sin límite de tiempo")})"));
            var extras = new List<string>();
            if (s.Pistas) extras.Add("los equivocados dejan una pista");
            if (s.PortadorHuye) extras.Add("el portador huye si te acercas");
            if (s.Intercambios == 1) extras.Add("cada turno una pareja de guardias cercanos se pasa el núcleo");
            else if (s.Intercambios > 1) extras.Add($"cada turno {s.Intercambios} parejas de guardias cercanos se pasan el núcleo");
            l.Add($"• Sala {k + 1} · {s.Nombre} (puerta {HistoriaCarcel.N(s.Puerta)}): {slots}" +
                  (extras.Count > 0 ? $". Además, {UnirY(extras)}." : "."));
        }
        l.Add("• Si un núcleo se apaga antes de llegar a la puerta, otro guardia vivo de la sala recibe uno " +
              "nuevo y tendrás que buscarlo otra vez.");
        l.Add("• PISTAS: son siempre ciertas (distancia a la puerta, fila, tipo de guardia o distancia a ti) " +
              "y los % ya las tienen en cuenta.");
        return string.Join("\n", l);
    }

    /// Texto de LA ALARMA de la cárcel.
    private static string TextoCarcelAlarma(ConfigCarcel cfg)
    {
        var l = new List<string>
        {
            $"• Sube {cfg.AlarmaPorTurno} por turno, {cfg.AlarmaPorError} por cada guardia equivocado y " +
            $"{cfg.AlarmaPorApagado} por cada núcleo que se apaga.",
            "• Al llegar a cada umbral aparece un refuerzo en la puerta de la sala en la que estés: " +
            string.Join(", ", cfg.Refuerzos.Select(r => $"{r.Umbral} {r.Nombre}")) + ".",
            "• Los refuerzos se acumulan en la puerta: para cruzarla tendrás que vencerlos a todos a la vez. " +
            $"Si son demasiados, desata el ODIO de Alexander (+{cfg.OdioFuerza} de fuerza, una sola vez).",
            $"• Con {cfg.AlarmaMax} la cárcel se sella y pierdes.",
        };
        return string.Join("\n", l);
    }

    /// Cartas de un bando agrupadas por la celda donde nacen:
    /// "En A11:" + una viñeta por carta (👑 clave · ⚔/🎯 papel del bot).
    private static IEnumerable<string> LineasPorCelda(
        IEnumerable<CartaHistoria> cartas,
        Dictionary<string, Dictionary<string, object?>> catalogo,
        string cuartel, bool esBot)
    {
        foreach (var g in cartas.GroupBy(c => string.IsNullOrWhiteSpace(c.Coord) ? cuartel : c.Coord!.Trim().ToUpperInvariant()))
        {
            yield return g.Key == cuartel ? $"En {(esBot ? "su" : "tu")} cuartel ({cuartel}):" : $"En {g.Key}:";
            foreach (var c in g)
            {
                var linea = LineasCartas(new[] { c }, catalogo).First();
                if (c.Vip) linea += " 👑";
                if (esBot && c.Rol == RolBotHistoria.Asalto) linea += " ⚔";
                else if (esBot && c.Rol == RolBotHistoria.Cazador) linea += " 🎯";
                yield return linea;
            }
        }
    }

    /// "A", "A y B", "A, B y C".
    private static string UnirY(IReadOnlyList<string> items) => items.Count switch
    {
        0 => "",
        1 => items[0],
        _ => string.Join(", ", items.Take(items.Count - 1)) + " y " + items[^1],
    };

    /// "4 en los turnos 1-2, 5 en los turnos 3-4 y 6 a partir del turno 5".
    private static string TextoTramos(IReadOnlyList<TramoDisparos> tramos)
    {
        var orden = tramos.OrderBy(t => t.DesdeTurno).ToList();
        var partes = new List<string>();
        for (int i = 0; i < orden.Count; i++)
        {
            var t = orden[i];
            int? hasta = i + 1 < orden.Count ? orden[i + 1].DesdeTurno - 1 : null;
            string cuando = hasta == null
                ? (t.DesdeTurno <= 1 ? "todos los turnos" : $"a partir del turno {t.DesdeTurno}")
                : hasta == t.DesdeTurno ? $"en el turno {t.DesdeTurno}"
                : $"en los turnos {t.DesdeTurno}-{hasta}";
            partes.Add($"{t.Disparos} {cuando}");
        }
        return partes.Count switch
        {
            0 => "ninguno",
            1 => partes[0],
            _ => string.Join(", ", partes.Take(partes.Count - 1)) + " y " + partes[^1],
        };
    }

    /// Una viñeta por entrada: "• 10 × Soldado celeste (2 ya evolucionadas: X)".
    private static IEnumerable<string> LineasCartas(
        IEnumerable<CartaHistoria> cartas,
        Dictionary<string, Dictionary<string, object?>> catalogo,
        bool mostrarEvolucion = true)
    {
        // Se agrupan las entradas repetidas de la misma carta (p. ej. dos
        // entradas de HumB, una normal y otra evolucionada).
        foreach (var g in cartas.GroupBy(c => c.CartaId))
        {
            int cant = g.Sum(c => Math.Max(1, c.Cantidad));
            int evo = mostrarEvolucion ? g.Sum(c => Math.Clamp(c.Evolucionadas, 0, Math.Max(1, c.Cantidad))) : 0;
            var linea = $"• {cant} × {NombreCarta(catalogo, g.Key)}";
            if (evo > 0)
            {
                var idEvo = IdEvolucionDe(catalogo, g.Key);
                var nombreEvo = idEvo != "" ? NombreCarta(catalogo, idEvo) : "";
                linea += evo == cant
                    ? (nombreEvo != "" ? $" (todas ya evolucionadas: {nombreEvo})" : " (todas ya evolucionadas)")
                    : (nombreEvo != "" ? $" ({evo} ya evolucionada{(evo == 1 ? "" : "s")}: {nombreEvo})"
                                       : $" ({evo} ya evolucionada{(evo == 1 ? "" : "s")})");
            }
            yield return linea;
        }
    }

    private static string NombreCarta(Dictionary<string, Dictionary<string, object?>> catalogo, string id)
    {
        if (catalogo.TryGetValue(id, out var cd))
        {
            var n = M.Str(M.Get(cd, "Nombre", "nombre"));
            if (n != "") return n;
        }
        return "Carta desconocida";
    }
}