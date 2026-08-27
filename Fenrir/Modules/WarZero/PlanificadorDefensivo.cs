using System;
using System.Collections.Generic;
using System.Linq;

// ─────────────────────────────────────────────────────────────────────────────
// PlanificadorDefensivo.cs  —  MODO DEFENSA (planificador)
//
// Genera UN plan candidato de CONSOLIDACIÓN: repliega todas las unidades del bot
// hacia su cuartel (cada una tanto como le dé su movimiento) para concentrar
// fuerza y ganar el combate por poder neto, y mantiene la guarnición del cuartel.
//
// No es un interruptor de "modo": es un candidato MÁS que se añade a la lista de
// la softmax. El lookahead lo elige SOLO cuando es la mejor respuesta —hay una
// amenaza real y defender de verdad supera a farmear/atacar—; si no hay amenaza,
// replegar puntúa peor (pierde economía y centro) y no se usa. Así el cambio de
// modo es EMERGENTE de la simulación, no una regla que haya que acertar.
//
// Este planificador RESUELVE el caso de IA 14: tenía 516 de fuerza repartida y
// perdió el cuartel; con este plan entre los candidatos, el lookahead (que ya ve
// la amenaza evolucionada) puede elegir "traer la fuerza a casa y aguantar".
//
// La retirada RESPETA EL TERRENO (TerrenoUtil): una unidad de mar no repliega
// pisando tierra, se queda en el borde. Límite v1: el paso es greedy (no rodea
// obstáculos por celdas anfibias; eso sería pathfinding completo). Tampoco
// despliega refuerzos desde la mano ni lanza escudos aún; solo reposiciona lo que
// ya está en el tablero (gasto de energía = 0).
// ─────────────────────────────────────────────────────────────────────────────
public static class PlanificadorDefensivo
{
    /// Genera el plan de consolidación defensiva hacia el cuartel del bot.
    public static BotMove Generar(BotContext ctx)
    {
        string miCuartel = ctx.Cuartel;
        int filas = ctx.Filas, columnas = ctx.Columnas;

        var celdas = new Dictionary<string, List<Dictionary<string, object?>>>();
        var tablero = M.Map(M.Get(ctx.Estado, "tablero"));
        foreach (var kv in tablero)
        {
            foreach (var cRaw in M.List(kv.Value))
            {
                var c = M.Map(cRaw);
                if (M.Str(M.Get(c, "ownerUid")) != ctx.BotUid) continue;

                // La guarnición del cuartel se queda; el resto repliega hacia él.
                string destino = kv.Key;
                if (miCuartel != "" && kv.Key != miCuartel)
                {
                    int mov = M.Int(M.Get(c, "Movimiento", "movimiento"));
                    var (tierra, mar) = TerrenoUtil.ClaseDeTipo(M.Int(M.Get(c, "Tipo", "tipo")));
                    destino = TerrenoUtil.PasoHaciaTerreno(
                        kv.Key, miCuartel, mov, tierra, mar, ctx.Terreno, filas, columnas);
                }
                if (!celdas.TryGetValue(destino, out var lst)) { lst = new(); celdas[destino] = lst; }
                lst.Add(c);
            }
        }

        return new BotMove
        {
            Celdas = celdas,
            Acciones = new List<Dictionary<string, object?>>(),
            ManoResultante = new List<string>(ctx.Mano),   // no despliega nada
            EnergiaGastada = 0,                             // reposicionar es gratis
        };
    }
}