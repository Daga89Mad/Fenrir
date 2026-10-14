// ─────────────────────────────────────────────────────────────────────────────
// DuelosSalas.cs
//
// MODO DUELOS — salas y partidas guardadas en la colección `Duelos`. Lógica
// pura sobre el documento (Dictionary): la usan los métodos de WarZeroDuelos.cs,
// que solo leen y escriben Firestore, y se puede probar sin servidor.
//
// La app NUNCA lee `Duelos` de Firestore: todo pasa por la API, que solo
// devuelve vistas saneadas (VistaSala, Resumen, VistaPartida). Así el
// COMANDANTE de cada jugador (el general que hay que proteger) y las jugadas
// pendientes nunca llegan al rival. Conviene que las reglas de Firestore
// denieguen al cliente el acceso a la colección `Duelos`.
//
// Documento Duelos/{id}:
//   nombre, hostUid, esPrivada, contrasena, mapaId, mapaNombre,
//   estado: "esperando" | "en_curso" | "finalizada",
//   creadoEn, fechaInicio, fechaFin (ms UTC),
//   jugadores: [ { uid, alias, ejercitoId, generales: [nombre, nombre], listo } ],
//   participantes: [uid, …]          (para la consulta de "mis duelos")
//   secretos: { uid: { comandante: 0|1 } }          ← nunca sale de la API
//   juego: DuelosSerializacion.ToMapa(estado)         ← nunca sale tal cual
//   jugadas: { uid: { turno, jugada } }              ← jugadas del turno en curso
//   turno, ganadorUid, motivo
// ─────────────────────────────────────────────────────────────────────────────

public static class DuelosSalas
{
    public const int MaxJugadores = 2;

    /// Nombre que ve el jugador para el general que hay que proteger.
    public const string TerminoComandante = "Comandante";

    // ═════════════════════════════════════════════════════════════════════════
    // CATÁLOGO PARA LA APP
    // ═════════════════════════════════════════════════════════════════════════

    /// Ejércitos, generales con su habilidad, guarnición fija y reglas básicas.
    /// [imagen]: cartaId → URL de la imagen (catálogo de cartas).
    public static Dictionary<string, object?> Catalogo(Func<string, string> imagen)
    {
        var ejercitos = DuelosCatalogo.Ejercitos.Values.OrderBy(e => (int)e.Ejercito).Select(e => (object?)new Dictionary<string, object?>
        {
            ["id"] = (long)(int)e.Ejercito,
            ["nombre"] = e.Nombre,
            ["habilidadEjercito"] = new Dictionary<string, object?>
            {
                ["tipo"] = e.HabilidadEjercito.ToString(),
                ["nombre"] = DuelosCatalogo.NombreHabilidadEjercito(e.HabilidadEjercito),
                ["descripcion"] = DuelosCatalogo.DescripcionHabilidadEjercito(e.HabilidadEjercito),
            },
            ["generales"] = e.Generales.Select(g => (object?)new Dictionary<string, object?>
            {
                ["nombre"] = g.Nombre,
                ["cartaId"] = g.CartaId,
                ["imagen"] = imagen(g.CartaId),
                ["ataque"] = (long)DuelosCatalogo.Ataque(g.Papel),
                ["vidas"] = (long)DuelosAjustes.VidasGeneral,
                ["habilidad"] = new Dictionary<string, object?>
                {
                    ["tipo"] = g.Habilidad.ToString(),
                    ["nombre"] = DuelosCatalogo.NombreHabilidad(g.Habilidad),
                    ["descripcion"] = DuelosCatalogo.DescripcionHabilidad(g.Habilidad),
                },
            }).ToList(),
            ["guarnicion"] = e.Guarnicion.Select(s => (object?)new Dictionary<string, object?>
            {
                ["nombre"] = s.Nombre,
                ["cartaId"] = s.CartaId,
                ["imagen"] = imagen(s.CartaId),
                ["papel"] = s.Papel.ToString(),
                ["ataque"] = (long)DuelosCatalogo.Ataque(s.Papel),
            }).ToList(),
        }).ToList();

        return new Dictionary<string, object?>
        {
            ["comandante"] = TerminoComandante,
            ["ejercitos"] = ejercitos,
            ["reglas"] = new Dictionary<string, object?>
            {
                ["movimiento"] = (long)DuelosAjustes.Movimiento,
                ["vidasGeneral"] = (long)DuelosAjustes.VidasGeneral,
                ["topeCasilla"] = (long)DuelosAjustes.TopeCartasCasilla,
                ["disparosLejanos"] = (long)DuelosAjustes.DisparosLejanos,
                ["turnoLimite"] = (long)DuelosAjustes.TurnoLimite,
                ["filas"] = (long)DuelosAjustes.Filas,
                ["columnas"] = (long)DuelosAjustes.Columnas,
            },
        };
    }

    // ═════════════════════════════════════════════════════════════════════════
    // MAPA
    // ═════════════════════════════════════════════════════════════════════════

    /// Casillas bloqueadas de un mapa de la colección `Mapas`: debe ser de 7×7;
    /// las casillas que no son de tierra quedan bloqueadas (todas las cartas de
    /// Duelos son de tierra). Comprueba que cada jugador tenga sitio para
    /// desplegar. Devuelve null y [error] si el mapa no sirve.
    public static bool[]? BloqueadasDesdeMapa(Dictionary<string, object?> mapa, out string? error)
    {
        error = null;
        int filas = Int(mapa, "filas"), columnas = Int(mapa, "columnas");
        if (filas != DuelosAjustes.Filas || columnas != DuelosAjustes.Columnas)
        {
            error = $"el mapa de un duelo debe ser de {DuelosAjustes.Filas}×{DuelosAjustes.Columnas}";
            return null;
        }
        var bloq = new bool[DuelosAjustes.Celdas];
        foreach (var kv in Dic(Get(mapa, "terreno")))
        {
            int c = DuelosTablero.Parse(kv.Key);
            var tipo = kv.Value?.ToString() ?? "land";
            if (c >= 0 && tipo != "land") bloq[c] = true;
        }
        for (int j = 0; j < 2; j++)
        {
            int libres = Enumerable.Range(0, DuelosAjustes.Celdas)
                .Count(c => DuelosTablero.EnZonaDespliegue(j, c) && !bloq[c]);
            if (libres * DuelosAjustes.TopeCartasCasilla < 6 || libres < 2)
            {
                error = "el mapa no deja sitio para desplegar en las filas traseras";
                return null;
            }
        }
        return bloq;
    }

    // ═════════════════════════════════════════════════════════════════════════
    // SALA
    // ═════════════════════════════════════════════════════════════════════════

    public static Dictionary<string, object?> NuevaSala(
        string uid, string alias, string nombre, bool esPrivada, string contrasena,
        string? mapaId, string mapaNombre, long ahoraMs) => new()
    {
        ["nombre"] = string.IsNullOrWhiteSpace(nombre) ? "Duelo" : nombre.Trim(),
        ["hostUid"] = uid,
        ["esPrivada"] = esPrivada,
        ["contrasena"] = esPrivada ? contrasena ?? "" : "",
        ["mapaId"] = string.IsNullOrWhiteSpace(mapaId) ? null : mapaId,
        ["mapaNombre"] = mapaNombre,
        ["estado"] = "esperando",
        ["creadoEn"] = ahoraMs,
        ["jugadores"] = new List<object?> { NuevoJugador(uid, alias) },
        ["participantes"] = new List<object?> { uid },
        ["secretos"] = new Dictionary<string, object?>(),
        ["jugadas"] = new Dictionary<string, object?>(),
        ["turno"] = 0L,
    };

    private static Dictionary<string, object?> NuevoJugador(string uid, string alias) => new()
    {
        ["uid"] = uid,
        ["alias"] = string.IsNullOrWhiteSpace(alias) ? "Jugador" : alias.Trim(),
        ["ejercitoId"] = null,
        ["generales"] = new List<object?>(),
        ["listo"] = false,
    };

    public static List<Dictionary<string, object?>> Jugadores(Dictionary<string, object?> doc) =>
        Lst(doc, "jugadores").Select(Dic).ToList();

    private static int IndiceDe(Dictionary<string, object?> doc, string uid) =>
        Jugadores(doc).FindIndex(j => Str(j, "uid") == uid);

    public static string? Unirse(Dictionary<string, object?> doc, string uid, string alias, string? contrasena)
    {
        if (string.IsNullOrWhiteSpace(uid)) return "falta el uid";
        var jugadores = Jugadores(doc);
        if (jugadores.Any(j => Str(j, "uid") == uid)) return null; // ya estaba
        if (Str(doc, "estado") != "esperando") return "el duelo ya ha empezado";
        if (jugadores.Count >= MaxJugadores) return "la sala está llena";
        if (Bool(doc, "esPrivada") && Str(doc, "contrasena") != (contrasena ?? "")) return "contraseña incorrecta";
        jugadores.Add(NuevoJugador(uid, alias));
        doc["jugadores"] = jugadores.Cast<object?>().ToList();
        var part = Lst(doc, "participantes").Select(x => x?.ToString() ?? "").ToList();
        if (!part.Contains(uid)) part.Add(uid);
        doc["participantes"] = part.Cast<object?>().ToList();
        return null;
    }

    /// Salir de la sala. En espera: se quita al jugador (y se borra la sala si
    /// queda vacía; si se va el anfitrión, lo pasa a ser el otro). En curso:
    /// es una RENDICIÓN y gana el rival.
    public static string? Salir(Dictionary<string, object?> doc, string uid, long ahoraMs, out bool borrar)
    {
        borrar = false;
        var jugadores = Jugadores(doc);
        int i = jugadores.FindIndex(j => Str(j, "uid") == uid);
        if (i < 0) return "no estás en esta sala";
        var estado = Str(doc, "estado");
        if (estado == "esperando")
        {
            jugadores.RemoveAt(i);
            if (jugadores.Count == 0) { borrar = true; return null; }
            doc["jugadores"] = jugadores.Cast<object?>().ToList();
            doc["participantes"] = Lst(doc, "participantes").Where(x => x?.ToString() != uid).ToList();
            Dic(doc, "secretos").Remove(uid);
            if (Str(doc, "hostUid") == uid) doc["hostUid"] = Str(jugadores[0], "uid");
            return null;
        }
        if (estado == "en_curso")
        {
            var rival = jugadores.Where((_, k) => k != i).Select(j => Str(j, "uid")).FirstOrDefault() ?? "";
            Finalizar(doc, rival, "abandono", ahoraMs);
            return null;
        }
        return "el duelo ya ha terminado";
    }

    /// Elección de ejército, generales y comandante (secreto). Solo en espera.
    public static string? Elegir(Dictionary<string, object?> doc, string uid, int ejercitoId,
                                 IList<string>? generales, int comandante)
    {
        if (Str(doc, "estado") != "esperando") return "el duelo ya ha empezado";
        var jugadores = Jugadores(doc);
        int i = jugadores.FindIndex(j => Str(j, "uid") == uid);
        if (i < 0) return "no estás en esta sala";
        if (!Enum.IsDefined(typeof(DuelosEjercito), ejercitoId)) return "ejército no válido";
        var ej = (DuelosEjercito)ejercitoId;
        if (generales == null || generales.Count != 2) return "elige exactamente 2 generales";
        var defs = generales.Select(n => DuelosCatalogo.General(ej, n ?? "")).ToList();
        if (defs.Any(d => d == null)) return $"esos generales no son de {DuelosCatalogo.Ejercitos[ej].Nombre}";
        if (defs[0]!.Nombre == defs[1]!.Nombre) return "elige dos generales distintos";
        if (comandante is < 0 or > 1) return $"elige a tu {TerminoComandante.ToLowerInvariant()}";

        jugadores[i]["ejercitoId"] = (long)ejercitoId;
        jugadores[i]["generales"] = defs.Select(d => (object?)d!.Nombre).ToList();
        jugadores[i]["listo"] = true;
        doc["jugadores"] = jugadores.Cast<object?>().ToList();
        var secretos = Dic(doc, "secretos");
        secretos[uid] = new Dictionary<string, object?> { ["comandante"] = (long)comandante };
        doc["secretos"] = secretos;
        return null;
    }

    /// Arranca el duelo (solo el anfitrión, con los dos jugadores listos).
    /// [bloqueadas]: casillas del mapa (null = tablero por defecto).
    public static string? Iniciar(Dictionary<string, object?> doc, string uid, bool[]? bloqueadas,
                                  Random rng, long ahoraMs)
    {
        if (Str(doc, "estado") != "esperando") return "el duelo ya ha empezado";
        if (Str(doc, "hostUid") != uid) return "solo el anfitrión puede iniciar el duelo";
        var jugadores = Jugadores(doc);
        if (jugadores.Count < MaxJugadores) return "falta un rival";
        if (!jugadores.All(j => Bool(j, "listo"))) return "los dos jugadores tienen que elegir sus generales";
        var secretos = Dic(doc, "secretos");

        var cfg = new DuelosConfigJugador[2];
        for (int k = 0; k < 2; k++)
        {
            var j = jugadores[k];
            var sec = Dic(Get(secretos, Str(j, "uid")));
            int comandante = Int(sec, "comandante");
            cfg[k] = new DuelosConfigJugador
            {
                Ejercito = (DuelosEjercito)Int(j, "ejercitoId", 1),
                Generales = Lst(j, "generales").Select(x => x?.ToString() ?? "").ToArray(),
                ReyIndice = comandante,
                Despliegue = DuelosBot.ConRey(DuelosBot.Desplegar(k, rng, bloqueadas), comandante),
            };
        }

        DuelosEstado e;
        try { e = DuelosMotor.Crear(cfg[0], cfg[1], rng, bloqueadas); }
        catch (Exception ex) { return ex.Message; }

        doc["juego"] = DuelosSerializacion.ToMapa(e);
        doc["estado"] = "en_curso";
        doc["turno"] = (long)e.Turno;
        doc["fechaInicio"] = ahoraMs;
        doc["jugadas"] = new Dictionary<string, object?>();
        return null;
    }

    // ═════════════════════════════════════════════════════════════════════════
    // TURNO
    // ═════════════════════════════════════════════════════════════════════════

    /// Guarda la jugada de [uid] para el turno en curso. Cuando están las dos,
    /// resuelve el turno. [resuelto] = true si esta llamada lo resolvió.
    public static string? EnviarTurno(Dictionary<string, object?> doc, string uid, int turno,
                                      Dictionary<string, object?> jugada, Random rng, long ahoraMs,
                                      out bool resuelto)
    {
        resuelto = false;
        if (Str(doc, "estado") != "en_curso") return "el duelo no está en curso";
        int yo = IndiceDe(doc, uid);
        if (yo < 0) return "no juegas este duelo";
        int turnoActual = Int(doc, "turno", 1);
        if (turno > 0 && turno != turnoActual) return $"el turno en curso es el {turnoActual}";

        var jugadas = Dic(doc, "jugadas");
        jugadas[uid] = new Dictionary<string, object?> { ["turno"] = (long)turnoActual, ["jugada"] = jugada };
        doc["jugadas"] = jugadas;

        var uids = Jugadores(doc).Select(j => Str(j, "uid")).ToList();
        if (!uids.All(u => jugadas.ContainsKey(u))) return null;

        var e = DuelosSerializacion.DesdeMapa(Get(doc, "juego"));
        var j0 = DuelosSerializacion.JugadaDesdeMapa(Get(Dic(Get(jugadas, uids[0])), "jugada"));
        var j1 = DuelosSerializacion.JugadaDesdeMapa(Get(Dic(Get(jugadas, uids[1])), "jugada"));
        DuelosMotor.Resolver(e, j0, j1, rng);
        resuelto = true;

        doc["juego"] = DuelosSerializacion.ToMapa(e);
        doc["turno"] = (long)e.Turno;
        doc["jugadas"] = new Dictionary<string, object?>();
        if (e.Terminada)
        {
            string ganador = e.Ganador is 0 or 1 ? uids[e.Ganador] : "";
            Finalizar(doc, ganador, e.Motivo, ahoraMs);
        }
        return null;
    }

    private static void Finalizar(Dictionary<string, object?> doc, string ganadorUid, string motivo, long ahoraMs)
    {
        doc["estado"] = "finalizada";
        doc["ganadorUid"] = ganadorUid;
        doc["motivo"] = motivo;
        doc["fechaFin"] = ahoraMs;
        doc["jugadas"] = new Dictionary<string, object?>();
    }

    // ═════════════════════════════════════════════════════════════════════════
    // VISTAS PARA LA APP (nunca incluyen secretos ni jugadas ajenas)
    // ═════════════════════════════════════════════════════════════════════════

    /// Fila de las listas (EN CURSO / PÚBLICAS).
    public static Dictionary<string, object?> Resumen(Dictionary<string, object?> doc, string id, string uid)
    {
        var jugadores = Jugadores(doc);
        var estado = Str(doc, "estado");
        var jugadas = Dic(doc, "jugadas");
        string resultado = "";
        if (estado == "finalizada")
        {
            var g = Str(doc, "ganadorUid");
            resultado = g == "" ? "tablas" : g == uid ? "victoria" : "derrota";
        }
        return new Dictionary<string, object?>
        {
            ["id"] = id,
            ["nombre"] = Str(doc, "nombre"),
            ["estado"] = estado,
            ["esPrivada"] = Bool(doc, "esPrivada"),
            ["mapaNombre"] = Str(doc, "mapaNombre"),
            ["hostUid"] = Str(doc, "hostUid"),
            ["creadoEn"] = Long(doc, "creadoEn"),
            ["turno"] = (long)Int(doc, "turno"),
            ["numJugadores"] = (long)jugadores.Count,
            ["maxJugadores"] = (long)MaxJugadores,
            ["jugadores"] = jugadores.Select(j => (object?)new Dictionary<string, object?>
            {
                ["uid"] = Str(j, "uid"),
                ["alias"] = Str(j, "alias"),
                ["listo"] = Bool(j, "listo"),
            }).ToList(),
            ["soyParticipante"] = jugadores.Any(j => Str(j, "uid") == uid),
            // En curso: true si este jugador ya mandó su jugada del turno.
            ["yaJugue"] = estado == "en_curso" && jugadas.ContainsKey(uid),
            ["resultado"] = resultado,
            ["motivo"] = Str(doc, "motivo"),
        };
    }

    /// Sala de espera: los datos de los dos jugadores, pero la elección de
    /// ejército y generales del rival solo se ve cuando empieza el duelo, y el
    /// comandante solo el propio.
    public static Dictionary<string, object?> VistaSala(Dictionary<string, object?> doc, string id, string uid)
    {
        var r = Resumen(doc, id, uid);
        bool empezado = Str(doc, "estado") != "esperando";
        r["mapaId"] = Get(doc, "mapaId");
        r["jugadores"] = Jugadores(doc).Select(j =>
        {
            bool propio = Str(j, "uid") == uid;
            var m = new Dictionary<string, object?>
            {
                ["uid"] = Str(j, "uid"),
                ["alias"] = Str(j, "alias"),
                ["listo"] = Bool(j, "listo"),
                ["esHost"] = Str(j, "uid") == Str(doc, "hostUid"),
                ["propio"] = propio,
            };
            if (propio || empezado)
            {
                m["ejercitoId"] = Get(j, "ejercitoId");
                m["generales"] = Lst(j, "generales");
            }
            if (propio)
            {
                var sec = Dic(Get(Dic(doc, "secretos"), uid));
                m["comandante"] = sec.Count > 0 ? (long)Int(sec, "comandante") : null;
            }
            return (object?)m;
        }).ToList();
        return r;
    }

    /// Partida en curso o terminada desde el punto de vista de [uid].
    public static Dictionary<string, object?>? VistaPartida(Dictionary<string, object?> doc, string id, string uid)
    {
        int yo = IndiceDe(doc, uid);
        if (yo < 0 || Get(doc, "juego") == null) return null;
        var e = DuelosSerializacion.DesdeMapa(Get(doc, "juego"));
        var r = VistaSala(doc, id, uid);
        r["partida"] = DuelosSerializacion.Vista(e, yo);
        return r;
    }

    /// Convierte la jugada tipada de la petición en el mapa que se guarda.
    public static Dictionary<string, object?> JugadaAMapa(DuelosJugadaDto? dto)
    {
        dto ??= new DuelosJugadaDto();
        return new Dictionary<string, object?>
        {
            ["movimientos"] = (dto.Movimientos ?? new()).ToDictionary(kv => kv.Key, kv => (object?)kv.Value),
            ["habilidades"] = (dto.Habilidades ?? new()).Select(h => (object?)new Dictionary<string, object?>
            {
                ["generalId"] = (long)h.GeneralId,
                ["cartaId"] = (long)h.CartaId,
                ["celdas"] = (h.Celdas ?? new()).Select(c => (object?)c).ToList(),
            }).ToList(),
            ["disparos"] = (dto.Disparos ?? new()).Select(c => (object?)c).ToList(),
            ["usarHabilidadEjercito"] = dto.UsarHabilidadEjercito,
            ["habilidadEjercitoCartaId"] = (long)dto.HabilidadEjercitoCartaId,
        };
    }

    // ═════════════════════════════════════════════════════════════════════════
    // AYUDAS (tolerantes con los tipos que devuelve Firestore)
    // ═════════════════════════════════════════════════════════════════════════

    /// Copia profunda de un documento de Firestore a Dictionary/List mutables.
    public static Dictionary<string, object?> Normalizar(object? o) => Dic(Copia(o));

    private static object? Copia(object? o)
    {
        switch (o)
        {
            case null: return null;
            case string s: return s;
            case System.Collections.IDictionary d:
            {
                var r = new Dictionary<string, object?>();
                foreach (System.Collections.DictionaryEntry kv in d) r[kv.Key.ToString() ?? ""] = Copia(kv.Value);
                return r;
            }
            case System.Collections.IEnumerable en:
            {
                var l = new List<object?>();
                foreach (var x in en) l.Add(Copia(x));
                return l;
            }
            case int i: return (long)i;
            default: return o;
        }
    }

    private static object? Get(Dictionary<string, object?> d, string k) => d.TryGetValue(k, out var v) ? v : null;

    private static Dictionary<string, object?> Dic(object? o)
    {
        if (o is Dictionary<string, object?> d) return d;
        return o is System.Collections.IDictionary ? (Dictionary<string, object?>)Copia(o)! : new Dictionary<string, object?>();
    }

    /// Mapa hijo [k] de [d], creándolo si no existe (para modificarlo).
    private static Dictionary<string, object?> Dic(Dictionary<string, object?> d, string k)
    {
        if (d.TryGetValue(k, out var v) && v is Dictionary<string, object?> m) return m;
        var n = Dic(v);
        d[k] = n;
        return n;
    }

    private static List<object?> Lst(Dictionary<string, object?> d, string k)
    {
        var o = Get(d, k);
        if (o is List<object?> l) return l;
        if (o is string || o is not System.Collections.IEnumerable en) return new List<object?>();
        var r = new List<object?>();
        foreach (var x in en) r.Add(x);
        return r;
    }

    private static long Long(Dictionary<string, object?> d, string k) => Get(d, k) switch
    {
        null => 0,
        int i => i,
        long l => l,
        double db => (long)db,
        string s when long.TryParse(s, out var p) => p,
        var o => Convert.ToInt64(o),
    };

    private static int Int(Dictionary<string, object?> d, string k, int def = 0) =>
        Get(d, k) == null ? def : (int)Long(d, k);

    private static string Str(Dictionary<string, object?> d, string k) => Get(d, k)?.ToString() ?? "";

    private static bool Bool(Dictionary<string, object?> d, string k) => Get(d, k) is bool b && b;
}

// ═════════════════════════════════════════════════════════════════════════════
// PETICIONES
// ═════════════════════════════════════════════════════════════════════════════
public class CrearDueloRequest
{
    public string Uid { get; set; } = "";
    public string Alias { get; set; } = "";
    public string Nombre { get; set; } = "";
    public bool EsPrivada { get; set; }
    public string Contrasena { get; set; } = "";
    public string? MapaId { get; set; }
}

public class UnirseDueloRequest
{
    public string Uid { get; set; } = "";
    public string Alias { get; set; } = "";
    public string Id { get; set; } = "";
    public string? Contrasena { get; set; }
}

public class SalirDueloRequest
{
    public string Uid { get; set; } = "";
    public string Id { get; set; } = "";
}

public class ElegirDueloRequest
{
    public string Uid { get; set; } = "";
    public string Id { get; set; } = "";
    public int EjercitoId { get; set; }
    public List<string>? Generales { get; set; }
    /// 0 o 1: cuál de los dos generales es el comandante.
    public int Comandante { get; set; } = -1;
}

public class IniciarDueloRequest
{
    public string Uid { get; set; } = "";
    public string Id { get; set; } = "";
}

public class TurnoDueloRequest
{
    public string Uid { get; set; } = "";
    public string Id { get; set; } = "";
    public int Turno { get; set; }
    public DuelosJugadaDto? Jugada { get; set; }
}

/// Jugada de un turno: { movimientos: { "<cartaId>": "C4" }, habilidades:
/// [ { generalId, celdas: ["C4"], cartaId } ], disparos: ["C4"],
/// usarHabilidadEjercito, habilidadEjercitoCartaId }.
public class DuelosJugadaDto
{
    public Dictionary<string, string>? Movimientos { get; set; }
    public List<DuelosUsoHabilidadDto>? Habilidades { get; set; }
    public List<string>? Disparos { get; set; }
    public bool UsarHabilidadEjercito { get; set; }
    public int HabilidadEjercitoCartaId { get; set; } = -1;
}

public class DuelosUsoHabilidadDto
{
    public int GeneralId { get; set; }
    public List<string>? Celdas { get; set; }
    public int CartaId { get; set; } = -1;
}
