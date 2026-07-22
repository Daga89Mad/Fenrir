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
// Posición final:
//   1º  = ganador (último en pie).
//   2º  = último eliminado.
//   3º+ = resto, del más reciente al más antiguo eliminado.
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

        // 2) Construir el ranking final.
        var jugadores = M.List(M.Get(datos, "jugadores"))
            .Select(j => M.Str(M.Get(M.Map(j), "uid")))
            .Where(u => u != "")
            .ToList();
        var eliminados = M.List(M.Get(datos, "jugadoresEliminados"))
            .Select(M.Str).Where(u => u != "").ToList();
        var ganadorUid = M.Str(M.Get(datos, "ganadorUid"));
        var playerCount = jugadores.Count;

        var ranking = new List<string>();
        if (ganadorUid != "") ranking.Add(ganadorUid);                 // 1º
        for (int i = eliminados.Count - 1; i >= 0; i--)                // 2º, 3º…
            if (!ranking.Contains(eliminados[i])) ranking.Add(eliminados[i]);
        foreach (var u in jugadores)                                   // por si acaso
            if (!ranking.Contains(u)) ranking.Add(u);

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
        if (posicion == 1) return (baseXp, baseDinero);            // ganador
        if (posicion == 2) return (baseXp / 2, baseDinero / 2);    // subcampeón
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