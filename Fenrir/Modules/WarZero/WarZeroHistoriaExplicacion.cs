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

        // ── 🎯 Objetivo ──────────────────────────────────────────────────────
        var objetivo = new List<string>();
        if (def.Jugador.Objetivo == ObjetivoHistoria.Conquistar)
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
        var derrota = new List<string> { $"• Si conquistan tu cuartel ({cuartelJugador})." };
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
        else
        {
            tropas.Add($"Todas tus cartas empiezan en tu cuartel ({cuartelJugador}). " +
                       "No tienes mano ni mazo: solo juegas con estas:");
            tropas.AddRange(LineasCartas(def.Jugador.Cartas, catalogo));
            tropas.Add($"Empiezas con {def.Jugador.EnergiaInicial} Ø y recibes +{Math.Max(0, def.SuerteDelPerdedor)} Ø cada turno.");
        }
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
        if (def.Bot.Cartas.Count > 0)
        {
            enemigo.Add($"{enemigoNombre} tiene en su cuartel ({cuartelBot}):");
            enemigo.AddRange(LineasCartas(def.Bot.Cartas, catalogo));
        }
        else
        {
            enemigo.Add($"{enemigoNombre} empieza sin tropas en el tablero y las despliega desde su mano.");
        }
        var guarnicion = def.Bot.Cartas.Where(c => c.Guarnicion > 0).ToList();
        if (guarnicion.Count > 0)
            enemigo.Add("No salen nunca de su cuartel: " +
                        string.Join(", ", guarnicion.Select(c => $"{c.Guarnicion} × {NombreCarta(catalogo, c.CartaId)}")) + ".");

        if (def.EsPartidaNormal)
            enemigo.Add("Juega como un comandante: despliega cartas desde su mano y roba cada turno.");
        else
            enemigo.Add(def.ComportamientoBotEfectivo switch
            {
                ComportamientoBotHistoria.Cazar =>
                    "Sus tropas salen a CAZAR tus cartas: se reparten entre tus grupos y solo " +
                    "entran en tu casilla si llegan con más fuerza (F + D) que el grupo que persiguen. " +
                    "Si no, te acechan desde cerca.",
                ComportamientoBotHistoria.Defender =>
                    "Se atrinchera y defiende su cuartel.",
                _ =>
                    "Avanza hacia tu cuartel para conquistarlo.",
            });
        if (HistoriaGuiones.Get(def.Id) != null)
            enemigo.Add("Recibirá refuerzos en oleadas durante la batalla.");
        Seccion("👹", "El enemigo", string.Join("\n", enemigo));

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
                    : (nombreEvo != "" ? $" ({evo} ya evolucionadas: {nombreEvo})" : $" ({evo} ya evolucionadas)");
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