using Google.Cloud.Firestore;

// ─────────────────────────────────────────────────────────────────────────────
// WarZeroDuelos.cs
//
// MODO DUELOS — acceso a Firestore de las salas y partidas (colección
// `Duelos`). Toda la lógica está en DuelosSalas.cs (pura); aquí solo se lee,
// se llama a esa lógica dentro de una transacción y se escribe el documento.
// Endpoints en WarZeroDuelosEndpoints.cs.
//
// RECOMPENSAS: cuando un duelo termina (por resolución, rendición o tiempo)
// se reparten UNA vez (RepartirRecompensasDueloAsync): se reclaman en una
// transacción (`recompensasRepartidas`) y luego se abonan a cada jugador con
// WarZeroRecompensas.AbonarDueloAsync. Las lecturas reintentan si faltaran.
//
// Cierre por TIEMPO (DuelosSalas.CerrarPorTiempo): se comprueba de forma
// perezosa al leer un duelo (sala, partida, mis duelos) y al mandar un turno,
// y además el orquestador llama a ResolverDuelosVencidosAsync en su barrido
// caro para los duelos diario/turno12h que nadie abre.
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
    /// [alCrear]: además exige que el mapa no esté oculto (`seleccionable`
    /// = false en la edición de mapas). Al iniciar no se comprueba, para que
    /// ocultar un mapa no rompa las salas ya creadas con él.
    private async Task<(bool[]? bloqueadas, string nombre, string? error)> MapaDueloAsync(
        string? mapaId, bool alCrear = false)
    {
        if (string.IsNullOrWhiteSpace(mapaId))
            return (null, "", alCrear ? "elige un mapa para el duelo" : null);
        var snap = await _fs.Db.Collection("Mapas").Document(mapaId).GetSnapshotAsync();
        if (!snap.Exists) return (null, "", "el mapa no existe");
        var mapa = DuelosSalas.Normalizar(snap.ToDictionary());
        if (alCrear && M.Get(mapa, "seleccionable") is bool sel && !sel)
            return (null, "", "ese mapa no está disponible");
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
        if (!string.IsNullOrEmpty(req.ModoTurno) && !DuelosSalas.ModosTurno.Contains(req.ModoTurno))
            return DueloError("modo de turno no válido");
        var (_, mapaNombre, error) = await MapaDueloAsync(req.MapaId, alCrear: true);
        if (error != null) return DueloError(error);

        var doc = DuelosSalas.NuevaSala(req.Uid, req.Alias, req.Nombre, req.EsPrivada, req.Contrasena,
                                        req.MapaId, mapaNombre, AhoraMs(), req.ModoTurno);
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
        bool terminado = false;
        var res = await _fs.Db.RunTransactionAsync(async tx =>
        {
            terminado = false;
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
            terminado = DuelosSalas.PendienteDeRecompensas(doc);
            var vista = (vistaPartida ? DuelosSalas.VistaPartida(doc, id, uid) : null)
                        ?? DuelosSalas.VistaSala(doc, id, uid);
            vista["ok"] = true;
            return vista;
        });
        if (terminado)
        {
            // El duelo acaba de terminar: recompensas, y la vista con lo recibido.
            var pagado = await RepartirRecompensasDueloAsync(id);
            if (pagado != null)
            {
                var r = pagado.FirstOrDefault(x => x.Uid == uid);
                if (r != null) res["recompensa"] = RecompensaAMapa(r);
            }
        }
        return res;
    }

    private static Dictionary<string, object?> RecompensaAMapa(DuelosRecompensa r) => new()
    {
        ["resultado"] = r.Resultado,
        ["pc"] = (long)r.Pc,
        ["xp"] = (long)r.Xp,
        ["dinero"] = (long)r.Dinero,
        ["zero"] = (long)r.Zero,
    };

    /// Reparte las recompensas de un duelo terminado, una sola vez: las
    /// reclama en una transacción y luego las abona a cada jugador. Devuelve lo
    /// abonado, o null si ya estaba pagado o el duelo no ha terminado.
    private async Task<List<DuelosRecompensa>?> RepartirRecompensasDueloAsync(string id)
    {
        var refDoc = Duelos.Document(id);
        List<DuelosRecompensa> lista;
        try
        {
            lista = await _fs.Db.RunTransactionAsync(async tx =>
            {
                var snap = await tx.GetSnapshotAsync(refDoc);
                if (!snap.Exists) return new List<DuelosRecompensa>();
                var doc = DuelosSalas.Normalizar(snap.ToDictionary());
                var l = DuelosSalas.ReclamarRecompensas(doc);
                if (l.Count > 0) tx.Set(refDoc, doc);
                return l;
            });
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[WZ.Duelos] reclamar recompensas de {id} falló: {ex.Message}");
            return null;
        }
        if (lista.Count == 0) return null;
        foreach (var r in lista)
        {
            try
            {
                await WarZeroRecompensas.AbonarDueloAsync(_fs.Db, r);
                Console.WriteLine($"[WZ.Duelos] duelo {id}: {r.Uid} {r.Resultado} " +
                                  $"pc={r.Pc} xp={r.Xp} dinero={r.Dinero} zero={r.Zero}");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[WZ.Duelos] abonar recompensa de {id} a {r.Uid} falló: {ex}");
            }
        }
        return lista;
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
            var rng = new Random();
            long ahora = AhoraMs();
            var error = DuelosSalas.EnviarTurno(doc, req.Uid, req.Turno, jugada, rng, ahora, out resuelto);
            // Si el turno ya había vencido y el rival no jugó, se cierra ahora.
            if (error == null && !resuelto) DuelosSalas.CerrarPorTiempo(doc, rng, ahora, out resuelto);
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

    /// Si el duelo terminó y no se pagaron sus recompensas (p. ej. falló el
    /// reparto al terminar), las reparte ahora y devuelve el documento con
    /// `recompensas` relleno para la vista.
    private async Task<Dictionary<string, object?>> RecompensasSiFaltanAsync(
        string id, Dictionary<string, object?> doc)
    {
        if (!DuelosSalas.PendienteDeRecompensas(doc)) return doc;
        var pagado = await RepartirRecompensasDueloAsync(id);
        if (pagado == null) return doc;
        doc["recompensasRepartidas"] = true;
        doc["recompensas"] = pagado.ToDictionary(r => r.Uid, r => (object?)RecompensaAMapa(r));
        return doc;
    }

    /// Cierre por tiempo de un duelo ya leído: si su turno venció, lo cierra en
    /// una transacción y devuelve el documento actualizado; si no, el mismo.
    /// También reparte las recompensas si el duelo terminó sin pagarlas.
    private async Task<Dictionary<string, object?>> RevisarTiempoDueloAsync(
        string id, Dictionary<string, object?> doc)
    {
        if (!DuelosSalas.TurnoVencido(doc, AhoraMs())) return await RecompensasSiFaltanAsync(id, doc);
        var refDoc = Duelos.Document(id);
        try
        {
            var (nuevo, resuelto) = await _fs.Db.RunTransactionAsync(async tx =>
            {
                var snap = await tx.GetSnapshotAsync(refDoc);
                if (!snap.Exists) return (doc, false);
                var d = DuelosSalas.Normalizar(snap.ToDictionary());
                if (!DuelosSalas.CerrarPorTiempo(d, new Random(), AhoraMs(), out var res)) return (d, false);
                tx.Set(refDoc, d);
                return (d, res);
            });
            if (resuelto)
                Console.WriteLine($"[WZ.Duelos] duelo {id}: turno cerrado por tiempo" +
                                  (M.Str(M.Get(nuevo, "estado")) == "finalizada" ? $" (fin: {M.Str(M.Get(nuevo, "motivo"))})" : ""));
            return await RecompensasSiFaltanAsync(id, nuevo);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[WZ.Duelos] cierre por tiempo de {id} falló: {ex.Message}");
            return doc;
        }
    }

    /// Barrido del orquestador: cierra los duelos diario/turno12h vencidos que
    /// nadie ha abierto. Los de rápida no: si nadie juega están en pausa.
    public async Task<int> ResolverDuelosVencidosAsync(CancellationToken ct = default)
    {
        var snap = await Duelos.WhereEqualTo("estado", "en_curso").Limit(300).GetSnapshotAsync(ct);
        int cerrados = 0;
        long ahora = AhoraMs();
        foreach (var d in snap.Documents)
        {
            ct.ThrowIfCancellationRequested();
            var doc = DuelosSalas.Normalizar(d.ToDictionary());
            if (DuelosSalas.ModoDe(doc) == DuelosSalas.ModoRapida) continue;
            if (!DuelosSalas.TurnoVencido(doc, ahora)) continue;
            await RevisarTiempoDueloAsync(d.Id, doc);
            cerrados++;
        }
        return cerrados;
    }

    public async Task<Dictionary<string, object?>> SalaDueloAsync(string id, string uid, bool partida)
    {
        if (string.IsNullOrWhiteSpace(id)) return DueloError("falta el id del duelo");
        var snap = await Duelos.Document(id).GetSnapshotAsync();
        if (!snap.Exists) return DueloError("el duelo no existe");
        var doc = await RevisarTiempoDueloAsync(id, DuelosSalas.Normalizar(snap.ToDictionary()));
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
            var doc = await RevisarTiempoDueloAsync(d.Id, DuelosSalas.Normalizar(d.ToDictionary()));
            var r = DuelosSalas.Resumen(doc, d.Id, uid);
            if (!M.Bool(M.Get(r, "soyParticipante"))) continue;
            if (M.Str(M.Get(doc, "estado")) == "finalizada" && M.Long(M.Get(doc, "fechaFin")) < corte) continue;
            res.Add(r);
        }
        return res.OrderByDescending(r => M.Long(M.Get(r, "creadoEn"))).ToList();
    }
}
