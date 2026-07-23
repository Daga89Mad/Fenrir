using Google.Cloud.Firestore;

// ─────────────────────────────────────────────────────────────────────────────
// WarZeroRecompensas.cs
//
// Reparto de recompensas al FINALIZAR una partida (experiencia, dinero y nivel).
// Es IDEMPOTENTE: marca `recompensasRepartidas` en el doc de la partida dentro
// de una transacción, de modo que aunque se invoque varias veces solo reparte
// una vez.
//
// Las Victorias/Derrotas NO se tocan aquí: se cuentan POR COMBATE en
// ResolverTurnoCoreEnTx (Jugadores/{uid}/Estadisticas/Resultados).
//
// Posición final: se ordena por PC (puntos de combate de la partida, en
// statsPartida[uid].pc) de MAYOR a menor. El que más PC tenga es 1º, el
// siguiente 2º, etc. En caso de EMPATE de PC se desempata por supervivencia
// (el ganadorUid / último en pie primero, y luego del último eliminado al
// primero). Nota: el `ganadorUid` (último en pie) sigue siendo quien dispara el
// diálogo de victoria; puede NO coincidir con el 1º por PC.
// ─────────────────────────────────────────────────────────────────────────────

public static class WarZeroRecompensas
{
    /// Reparte recompensas si la partida está finalizada y aún no se repartieron.
    /// Seguro de llamar siempre: se auto-comprueba y es idempotente.
    public static async Task RepartirSiFinalizadaAsync(FirestoreDb db, string lobbyId)
    {
        if (string.IsNullOrWhiteSpace(lobbyId)) return;
        var lobbyRef = db.Collection("Partidas").Document(lobbyId);

        // 1) Reclamar el reparto de forma atómica (evita duplicados por carreras).
        Dictionary<string, object?>? datos = null;
        try
        {
            datos = await db.RunTransactionAsync<Dictionary<string, object?>?>(async tx =>
            {
                var snap = await tx.GetSnapshotAsync(lobbyRef);
                if (!snap.Exists) return null;
                var d = M.Map(M.FromFs(snap.ToDictionary()));
                if (M.Str(M.Get(d, "estado")) != "finalizada") return null;
                if (M.Bool(M.Get(d, "recompensasRepartidas"))) return null;
                tx.Update(lobbyRef, new Dictionary<string, object>
                {
                    ["recompensasRepartidas"] = true,
                });
                return d;
            });
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("[WZ][recompensas] claim falló lobby=" + lobbyId + ": " + ex);
            return;
        }

        if (datos == null) return; // no finalizada, o ya repartido.

        // 2) Construir el ranking final POR PC (puntos de combate) descendente.
        var jugadores = M.List(M.Get(datos, "jugadores"))
            .Select(j => M.Str(M.Get(M.Map(j), "uid")))
            .Where(u => u != "")
            .ToList();
        var eliminados = M.List(M.Get(datos, "jugadoresEliminados"))
            .Select(M.Str).Where(u => u != "").ToList();
        var ganadorUid = M.Str(M.Get(datos, "ganadorUid"));
        var playerCount = jugadores.Count;

        // PC de cada jugador desde statsPartida[uid].pc (0 si no tiene entrada).
        var stats = M.Map(M.Get(datos, "statsPartida"));
        int PcDe(string uid) =>
            stats.TryGetValue(uid, out var s) ? M.Int(M.Get(M.Map(s), "pc")) : 0;

        // Desempate por supervivencia: ganador/último en pie primero, luego del
        // último eliminado al primero. A menor índice, mejor posición en empate.
        var ordenSupervivencia = new List<string>();
        if (ganadorUid != "") ordenSupervivencia.Add(ganadorUid);
        for (int i = eliminados.Count - 1; i >= 0; i--)
            if (!ordenSupervivencia.Contains(eliminados[i])) ordenSupervivencia.Add(eliminados[i]);
        foreach (var u in jugadores)
            if (!ordenSupervivencia.Contains(u)) ordenSupervivencia.Add(u);
        int DesempatePorSupervivencia(string uid)
        {
            var idx = ordenSupervivencia.IndexOf(uid);
            return idx < 0 ? int.MaxValue : idx;
        }

        // Ranking final: más PC primero; a igualdad de PC, quien sobrevivió más.
        // OrderByDescending + ThenBy es estable y determinista.
        var ranking = jugadores
            .OrderByDescending(PcDe)
            .ThenBy(DesempatePorSupervivencia)
            .ToList();

        // 3) Repartir a cada jugador según su posición.
        for (int idx = 0; idx < ranking.Count; idx++)
        {
            var uid = ranking[idx];
            var (xp, dinero) = RecompensaPorPosicion(idx + 1, playerCount);
            try
            {
                await AplicarRecompensaJugadorAsync(db, uid, xp, dinero);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[WZ][recompensas] jugador={uid} falló: " + ex);
            }
        }
    }

    // ── Aplicar recompensa (experiencia/dinero/nivel) a un jugador ───────────
    private static async Task AplicarRecompensaJugadorAsync(
        FirestoreDb db, string uid, int xp, int dinero)
    {
        var jRef = db.Collection("Jugadores").Document(uid);

        // Leer XP actual para recalcular el nivel (nivel se DERIVA de la XP total).
        long xpActual = 0;
        var snap = await jRef.GetSnapshotAsync();
        if (snap.Exists)
        {
            var d = M.Map(M.FromFs(snap.ToDictionary()));
            xpActual = M.Long(M.Get(d, "experiencia"));
        }
        var nivel = NivelDesdeExperiencia(xpActual + xp);

        await jRef.SetAsync(new Dictionary<string, object>
        {
            ["experiencia"] = FieldValue.Increment(xp),
            ["dinero"] = FieldValue.Increment(dinero),
            ["nivel"] = nivel,
        }, SetOptions.MergeAll);
    }

    // ── Tabla de recompensas por posición ────────────────────────────────────
    private static (int xp, int dinero) RecompensaPorPosicion(int posicion, int playerCount)
    {
        var (baseXp, baseDinero) = RecompensaBase(playerCount);
        if (posicion == 1) return (baseXp, baseDinero);            // 1º por PC
        if (posicion == 2) return (baseXp / 2, baseDinero / 2);    // 2º por PC
        return (100, 25);                                          // resto
    }

    private static (int xp, int dinero) RecompensaBase(int playerCount) => playerCount switch
    {
        2 => (500, 100),
        4 => (1000, 200),
        6 => (2000, 300),
        8 => (3000, 400),
        _ => (1000, 200), // por defecto tratamos como 4 jugadores
    };

    // ── Nivel a partir de la XP total ────────────────────────────────────────
    // Coste por nivel duplicando: 1→2=1000, 2→3=2000, 3→4=4000, 4→5=8000, …
    // XP acumulada para ALCANZAR el nivel N = 1000 * (2^(N-1) - 1).
    public static int NivelDesdeExperiencia(long xp)
    {
        if (xp < 0) xp = 0;
        int nivel = 1;
        while (nivel < 50) // tope de seguridad
        {
            long umbralSiguiente = 1000L * ((1L << nivel) - 1); // para alcanzar nivel+1
            if (xp >= umbralSiguiente) nivel++;
            else break;
        }
        return nivel;
    }
}