using Google.Cloud.Firestore;

// ─────────────────────────────────────────────────────────────────────────────
// WarZeroDuelos.cs
//
// MODO DUELOS — acceso a Firestore de las salas y partidas (colección
// `Duelos`). Toda la lógica está en DuelosSalas.cs (pura); aquí solo se lee,
// se llama a esa lógica dentro de una transacción y se escribe el documento.
// Endpoints en WarZeroDuelosEndpoints.cs.
// ─────────────────────────────────────────────────────────────────────────────

public partial class WarZeroService
{
    private const string ColeccionDuelos = "Duelos";

    private CollectionReference Duelos => _fs.Db.Collection(ColeccionDuelos);

    private static Dictionary<string, object?> DueloError(string msg) =>
        new() { ["ok"] = false, ["error"] = msg };

    private static long AhoraMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    // ── Catálogo ─────────────────────────────────────────────────────────────

    public async Task<Dictionary<string, object?>> CatalogoDuelosAsync()
    {
        var cartas = await ObtenerCatalogoCartasAsync();
        var r = DuelosSalas.Catalogo(cartaId =>
            cartas.TryGetValue(cartaId, out var c) ? M.Str(M.Get(c, "Imagen", "imagen")) : "");
        r["ok"] = true;
        return r;
    }

    // ── Mapa ─────────────────────────────────────────────────────────────────

    /// Lee el mapa y devuelve sus casillas bloqueadas (null si no hay mapa).
    /// [error] se rellena si el mapa no existe o no sirve para un duelo.
    private async Task<(bool[]? bloqueadas, string nombre, string? error)> MapaDueloAsync(string? mapaId)
    {
        if (string.IsNullOrWhiteSpace(mapaId)) return (null, "", null);
        var snap = await _fs.Db.Collection("Mapas").Document(mapaId).GetSnapshotAsync();
        if (!snap.Exists) return (null, "", "el mapa no existe");
        var mapa = DuelosSalas.Normalizar(snap.ToDictionary());
        var bloq = DuelosSalas.BloqueadasDesdeMapa(mapa, out var error);
        if (bloq == null) return (null, "", error ?? "el mapa no sirve para un duelo");
        var nombre = M.Str(M.Get(mapa, "nombre"));
        return (bloq, nombre == "" ? mapaId : nombre, null);
    }

    // ── Crear ────────────────────────────────────────────────────────────────

    public async Task<Dictionary<string, object?>> CrearDueloAsync(CrearDueloRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Uid)) return DueloError("falta el uid");
        if (req.EsPrivada && string.IsNullOrEmpty(req.Contrasena))
            return DueloError("una sala privada necesita contraseña");
        var (_, mapaNombre, error) = await MapaDueloAsync(req.MapaId);
        if (error != null) return DueloError(error);

        var doc = DuelosSalas.NuevaSala(req.Uid, req.Alias, req.Nombre, req.EsPrivada, req.Contrasena,
                                        req.MapaId, mapaNombre, AhoraMs());
        var nuevo = Duelos.Document();
        await nuevo.SetAsync(doc);
        Console.WriteLine($"[WZ.Duelos] sala {nuevo.Id} creada por {req.Uid}");
        var r = DuelosSalas.VistaSala(doc, nuevo.Id, req.Uid);
        r["ok"] = true;
        return r;
    }

    // ── Operaciones sobre una sala (transacción: leer, modificar, escribir) ──

    /// Ejecuta [op] sobre el documento dentro de una transacción. [op] devuelve
    /// el error (o null) y si hay que borrar la sala. Devuelve la vista de la
    /// sala tras el cambio o el error.
    private async Task<Dictionary<string, object?>> OperarDueloAsync(
        string id, string uid,
        Func<Dictionary<string, object?>, (string? error, bool borrar)> op,
        bool vistaPartida = false)
    {
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(uid))
            return DueloError("faltan el uid o el id del duelo");
        var refDoc = Duelos.Document(id);
        return await _fs.Db.RunTransactionAsync(async tx =>
        {
            var snap = await tx.GetSnapshotAsync(refDoc);
            if (!snap.Exists) return DueloError("el duelo no existe");
            var doc = DuelosSalas.Normalizar(snap.ToDictionary());
            var (error, borrar) = op(doc);
            if (error != null) return DueloError(error);
            if (borrar)
            {
                tx.Delete(refDoc);
                return new Dictionary<string, object?> { ["ok"] = true, ["borrada"] = true };
            }
            tx.Set(refDoc, doc);
            var vista = (vistaPartida ? DuelosSalas.VistaPartida(doc, id, uid) : null)
                        ?? DuelosSalas.VistaSala(doc, id, uid);
            vista["ok"] = true;
            return vista;
        });
    }

    public Task<Dictionary<string, object?>> UnirseDueloAsync(UnirseDueloRequest req) =>
        OperarDueloAsync(req.Id, req.Uid, doc => (DuelosSalas.Unirse(doc, req.Uid, req.Alias, req.Contrasena), false));

    public Task<Dictionary<string, object?>> SalirDueloAsync(SalirDueloRequest req) =>
        OperarDueloAsync(req.Id, req.Uid, doc =>
        {
            var error = DuelosSalas.Salir(doc, req.Uid, AhoraMs(), out var borrar);
            return (error, borrar);
        });

    public Task<Dictionary<string, object?>> ElegirDueloAsync(ElegirDueloRequest req) =>
        OperarDueloAsync(req.Id, req.Uid, doc =>
            (DuelosSalas.Elegir(doc, req.Uid, req.EjercitoId, req.Generales, req.Comandante), false));

    public async Task<Dictionary<string, object?>> IniciarDueloAsync(IniciarDueloRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Id)) return DueloError("falta el id del duelo");
        // El mapa no cambia: se lee fuera de la transacción.
        var previo = await Duelos.Document(req.Id).GetSnapshotAsync();
        if (!previo.Exists) return DueloError("el duelo no existe");
        var mapaId = M.Str(M.Get(DuelosSalas.Normalizar(previo.ToDictionary()), "mapaId"));
        var (bloqueadas, _, error) = await MapaDueloAsync(mapaId);
        if (error != null) return DueloError(error);

        var res = await OperarDueloAsync(req.Id, req.Uid, doc =>
            (DuelosSalas.Iniciar(doc, req.Uid, bloqueadas, new Random(), AhoraMs()), false),
            vistaPartida: true);
        if (M.Bool(M.Get(res, "ok"))) Console.WriteLine($"[WZ.Duelos] duelo {req.Id} iniciado");
        return res;
    }

    public async Task<Dictionary<string, object?>> TurnoDueloAsync(TurnoDueloRequest req)
    {
        var jugada = DuelosSalas.JugadaAMapa(req.Jugada);
        bool resuelto = false;
        var res = await OperarDueloAsync(req.Id, req.Uid, doc =>
        {
            var error = DuelosSalas.EnviarTurno(doc, req.Uid, req.Turno, jugada, new Random(), AhoraMs(), out resuelto);
            return (error, false);
        }, vistaPartida: true);
        if (M.Bool(M.Get(res, "ok")))
        {
            res["resuelto"] = resuelto;
            if (resuelto) Console.WriteLine($"[WZ.Duelos] duelo {req.Id}: turno {req.Turno} resuelto");
        }
        return res;
    }

    // ── Lecturas ─────────────────────────────────────────────────────────────

    public async Task<Dictionary<string, object?>> SalaDueloAsync(string id, string uid, bool partida)
    {
        if (string.IsNullOrWhiteSpace(id)) return DueloError("falta el id del duelo");
        var snap = await Duelos.Document(id).GetSnapshotAsync();
        if (!snap.Exists) return DueloError("el duelo no existe");
        var doc = DuelosSalas.Normalizar(snap.ToDictionary());
        var vista = (partida ? DuelosSalas.VistaPartida(doc, id, uid) : null)
                    ?? DuelosSalas.VistaSala(doc, id, uid);
        vista["ok"] = true;
        return vista;
    }

    /// Salas públicas en espera con hueco.
    public async Task<List<Dictionary<string, object?>>> DuelosPublicosAsync(string uid)
    {
        var snap = await Duelos.WhereEqualTo("estado", "esperando").Limit(50).GetSnapshotAsync();
        var res = new List<Dictionary<string, object?>>();
        foreach (var d in snap.Documents)
        {
            var doc = DuelosSalas.Normalizar(d.ToDictionary());
            if (M.Bool(M.Get(doc, "esPrivada"))) continue;
            var r = DuelosSalas.Resumen(doc, d.Id, uid);
            if (M.Int(M.Get(r, "numJugadores")) >= DuelosSalas.MaxJugadores && !M.Bool(M.Get(r, "soyParticipante")))
                continue;
            res.Add(r);
        }
        return res.OrderByDescending(r => M.Long(M.Get(r, "creadoEn"))).ToList();
    }

    /// Duelos del jugador: en espera, en curso y terminados en las últimas 24 h.
    public async Task<List<Dictionary<string, object?>>> MisDuelosAsync(string uid)
    {
        var res = new List<Dictionary<string, object?>>();
        if (string.IsNullOrWhiteSpace(uid)) return res;
        var snap = await Duelos.WhereArrayContains("participantes", uid).Limit(100).GetSnapshotAsync();
        long corte = AhoraMs() - 24L * 3600 * 1000;
        foreach (var d in snap.Documents)
        {
            var doc = DuelosSalas.Normalizar(d.ToDictionary());
            var r = DuelosSalas.Resumen(doc, d.Id, uid);
            if (!M.Bool(M.Get(r, "soyParticipante"))) continue;
            if (M.Str(M.Get(doc, "estado")) == "finalizada" && M.Long(M.Get(doc, "fechaFin")) < corte) continue;
            res.Add(r);
        }
        return res.OrderByDescending(r => M.Long(M.Get(r, "creadoEn"))).ToList();
    }
}
