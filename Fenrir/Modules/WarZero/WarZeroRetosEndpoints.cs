// ─────────────────────────────────────────────────────────────────────────────
// WarZeroRetosEndpoints.cs
//
// Endpoints del modo RETOS. Van en su propio fichero (y su propia extensión) en
// vez de dentro de WarZeroExtensions.MapWarZeroEndpoints para que añadir retos
// no obligue a tocar el mapeo general de la API.
//
// Se registran en Program.cs con:  app.MapWarZeroRetoEndpoints();
// ─────────────────────────────────────────────────────────────────────────────

public static class WarZeroRetosExtensions
{
    public static WebApplication MapWarZeroRetoEndpoints(this WebApplication app)
    {
        // ── Catálogo de retos publicados ─────────────────────────────────────
        // GET /warzero/retos
        // El cliente puede pintar la lista sin duplicar el catálogo, aunque hoy
        // la pantalla de retos lleva sus propios títulos para poder mostrarlos
        // sin esperar a la red.
        app.MapGet("/warzero/retos", () => Results.Ok(new
        {
            retos = RetoCatalogo.Todos.Select(r => new
            {
                id = r.Id,
                orden = r.Orden,
                titulo = r.Titulo,
                descripcion = r.Descripcion,
                mapaId = r.MapaId,
                ejercitoJugador = r.EjercitoJugador,
                jugadores = r.MaxJugadores,
                bots = r.Bots,
                modoBots = r.ModoBots.ToString(),
            }).ToList(),
        }));

        // ── Crear (o reanudar) la partida de un reto ─────────────────────────
        // POST /warzero/reto/crear  { uid, retoId }
        app.MapPost("/warzero/reto/crear", async (
            WarZeroService svc, CrearRetoRequest req, ILoggerFactory lf) =>
        {
            var log = lf.CreateLogger("WarZero.RetoCrear");
            try
            {
                var res = await svc.CrearPartidaRetoAsync(req);
                if (!res.Ok)
                    return Results.BadRequest(new { error = res.Error ?? "no se pudo crear el reto" });
                return Results.Ok(res);
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Error al crear reto uid={Uid} id={Id}", req.Uid, req.RetoId);
                return Results.Problem(
                    title: "Error al crear la partida del reto",
                    detail: Describe(ex),
                    statusCode: 500);
            }
        });

        return app;
    }

    /// Descripción encadenada de una excepción (mensaje + inner), para devolver
    /// un `detail` útil en los 500 sin volcar el stack completo.
    private static string Describe(Exception ex)
    {
        var partes = new List<string>();
        for (Exception? e = ex; e != null; e = e.InnerException)
            partes.Add($"{e.GetType().Name}: {e.Message}");
        return string.Join(" <- ", partes);
    }
}