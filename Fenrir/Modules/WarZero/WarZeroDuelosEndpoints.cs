// ─────────────────────────────────────────────────────────────────────────────
// WarZeroDuelosEndpoints.cs
//
// Endpoints del MODO DUELOS (salas, elección de generales y turnos). La lógica
// está en DuelosSalas.cs y el acceso a Firestore en WarZeroDuelos.cs.
// Todas las respuestas llevan `ok`; si `ok` es false, `error` explica por qué
// (y la respuesta es 400).
//
// Se registran en Program.cs con:  app.MapWarZeroDuelosEndpoints();
//
//   GET  /warzero/duelos/catalogo                 ejércitos, generales y reglas
//   GET  /warzero/duelos/publicas?uid=            salas públicas en espera
//   GET  /warzero/duelos/mispartidas?uid=         duelos del jugador
//   GET  /warzero/duelos/sala?id=&uid=            sala (sin secretos del rival)
//   GET  /warzero/duelos/partida?id=&uid=         sala + vista de la partida
//   POST /warzero/duelos/crear    { uid, alias, nombre, esPrivada, contrasena, mapaId }
//   POST /warzero/duelos/unirse   { uid, alias, id, contrasena }
//   POST /warzero/duelos/salir    { uid, id }       (en curso = rendirse)
//   POST /warzero/duelos/elegir   { uid, id, ejercitoId, generales: [a, b], comandante: 0|1 }
//   POST /warzero/duelos/iniciar  { uid, id }       (solo el anfitrión)
//   POST /warzero/duelos/turno    { uid, id, turno, jugada }
// ─────────────────────────────────────────────────────────────────────────────

public static class WarZeroDuelosExtensions
{
    public static WebApplication MapWarZeroDuelosEndpoints(this WebApplication app)
    {
        app.MapGet("/warzero/duelos/catalogo", (WarZeroService svc, ILoggerFactory lf) =>
            Ejecutar(lf, "catalogo", () => svc.CatalogoDuelosAsync()));

        app.MapGet("/warzero/duelos/publicas", async (WarZeroService svc, string? uid, ILoggerFactory lf) =>
        {
            try { return Results.Ok(new { ok = true, duelos = await svc.DuelosPublicosAsync(uid ?? "") }); }
            catch (Exception ex) { return Fallo(lf, "publicas", ex); }
        });

        app.MapGet("/warzero/duelos/mispartidas", async (WarZeroService svc, string uid, ILoggerFactory lf) =>
        {
            try { return Results.Ok(new { ok = true, duelos = await svc.MisDuelosAsync(uid) }); }
            catch (Exception ex) { return Fallo(lf, "mispartidas", ex); }
        });

        app.MapGet("/warzero/duelos/sala", (WarZeroService svc, string id, string uid, ILoggerFactory lf) =>
            Ejecutar(lf, "sala", () => svc.SalaDueloAsync(id, uid, partida: false)));

        app.MapGet("/warzero/duelos/partida", (WarZeroService svc, string id, string uid, ILoggerFactory lf) =>
            Ejecutar(lf, "partida", () => svc.SalaDueloAsync(id, uid, partida: true)));

        app.MapPost("/warzero/duelos/crear", (WarZeroService svc, CrearDueloRequest req, ILoggerFactory lf) =>
            Ejecutar(lf, "crear", () => svc.CrearDueloAsync(req)));

        app.MapPost("/warzero/duelos/unirse", (WarZeroService svc, UnirseDueloRequest req, ILoggerFactory lf) =>
            Ejecutar(lf, "unirse", () => svc.UnirseDueloAsync(req)));

        app.MapPost("/warzero/duelos/salir", (WarZeroService svc, SalirDueloRequest req, ILoggerFactory lf) =>
            Ejecutar(lf, "salir", () => svc.SalirDueloAsync(req)));

        app.MapPost("/warzero/duelos/elegir", (WarZeroService svc, ElegirDueloRequest req, ILoggerFactory lf) =>
            Ejecutar(lf, "elegir", () => svc.ElegirDueloAsync(req)));

        app.MapPost("/warzero/duelos/iniciar", (WarZeroService svc, IniciarDueloRequest req, ILoggerFactory lf) =>
            Ejecutar(lf, "iniciar", () => svc.IniciarDueloAsync(req)));

        app.MapPost("/warzero/duelos/turno", (WarZeroService svc, TurnoDueloRequest req, ILoggerFactory lf) =>
            Ejecutar(lf, "turno", () => svc.TurnoDueloAsync(req)));

        return app;
    }

    /// Ejecuta la operación: 200 si `ok`, 400 con `error` si no, 500 si lanza.
    private static async Task<IResult> Ejecutar(
        ILoggerFactory lf, string etiqueta, Func<Task<Dictionary<string, object?>>> op)
    {
        try
        {
            var res = await op();
            return res.TryGetValue("ok", out var ok) && ok is true ? Results.Ok(res) : Results.BadRequest(res);
        }
        catch (Exception ex) { return Fallo(lf, etiqueta, ex); }
    }

    private static IResult Fallo(ILoggerFactory lf, string etiqueta, Exception ex)
    {
        lf.CreateLogger("WarZero.Duelos").LogError(ex, "Error en duelos/{Etiqueta}", etiqueta);
        return Results.Problem(title: $"Error en duelos/{etiqueta}", detail: ex.Message, statusCode: 500);
    }
}
