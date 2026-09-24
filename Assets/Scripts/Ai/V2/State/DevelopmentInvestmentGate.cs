using System.Collections.Generic;
using Game.Players;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  DEVELOPMENT INVESTMENT GATE  (Strategy V2 — when Research/Production may spend)
    // ===========================================================================================
    //  Laboratory / Factory are a late resource sink: they strengthen the units the main deck has
    //  already put on the map and must never compete with playing that deck. The window opens only
    //  after resources have been piling up — WorldAnalysis' coarse four-resource headroom
    //  (DevelopmentReadiness.InvestmentSurplus) at or above devInvestmentSurplusThreshold for
    //  devInvestmentSurplusTurns consecutive turns. In the opening everything gets spent, so the
    //  streak never builds; once the deck stops absorbing income, the window opens by itself.
    //
    //  One writer: DemandLayer.Generate records the turn's fact once, from the turn-start snapshot
    //  (before this turn's own spending). Every spender reads the same answer through IsOpen:
    //  facility preparation, the ready-facility upgrade and — through GenerationSource — every
    //  Challenge a materialization or non-combat chain could start. A turn that was never
    //  observed reads as closed.
    // ===========================================================================================
    internal static class DevelopmentInvestmentGate
    {
        private sealed class State
        {
            public int LastTurn = int.MinValue;
            public int Streak;
            public float Surplus;
        }

        private static readonly Dictionary<PlayerSetupData, State> ByPlayer =
            new Dictionary<PlayerSetupData, State>();

        public static void Clear() => ByPlayer.Clear();

        // First call of a turn wins; repeated mid-turn Generate passes keep the turn-start fact.
        internal static void Observe(PlayerSetupData player, WorldSnapshot snap)
        {
            if (player == null || snap?.Development == null)
                return;
            if (!ByPlayer.TryGetValue(player, out State s))
                ByPlayer[player] = s = new State();
            if (s.LastTurn == snap.TurnNumber)
                return;
            // A skipped turn breaks the streak: the window proves resources accumulated on
            // consecutive turns, not merely on two turns ever.
            bool consecutive = s.LastTurn == snap.TurnNumber - 1;
            s.Surplus = snap.Development.InvestmentSurplus;
            s.Streak = s.Surplus >= AiConfigV2.devInvestmentSurplusThreshold
                ? (consecutive ? s.Streak : 0) + 1 : 0;
            s.LastTurn = snap.TurnNumber;
            AiDebugLog.Write($"[AI][V2][Development][Gate] turn={snap.TurnNumber} "
                + $"investSurplus={s.Surplus:0.00} threshold={AiConfigV2.devInvestmentSurplusThreshold:0.00} "
                + $"streak={s.Streak}/{AiConfigV2.devInvestmentSurplusTurns} "
                + $"decision={(s.Streak >= AiConfigV2.devInvestmentSurplusTurns ? "OPEN" : "CLOSED")}");
        }

        internal static bool IsOpen(PlayerSetupData player, int turn) =>
            IsOpen(player, turn, out _, out _);

        internal static bool IsOpen(PlayerSetupData player, int turn, out int streak, out float surplus)
        {
            streak = 0;
            surplus = 0f;
            if (player == null || !ByPlayer.TryGetValue(player, out State s) || s.LastTurn != turn)
                return false;
            streak = s.Streak;
            surplus = s.Surplus;
            return s.Streak >= AiConfigV2.devInvestmentSurplusTurns;
        }
    }
}
