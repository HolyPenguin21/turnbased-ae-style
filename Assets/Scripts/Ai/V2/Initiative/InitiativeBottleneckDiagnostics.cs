using Game.Players;

namespace Game.Ai.V2.Initiative
{
    // Initiative spendability diagnostics are also a bounded negative-feedback input. A turn that
    // stranded a large fraction of its AP is evidence that buying still more AP was not the active
    // bottleneck; do not pay resources for another die until a subsequent turn demonstrates that
    // the slack disappeared.
    internal static class InitiativeBottleneckDiagnostics
    {
        public static string Describe(PlayerSetupData player, PreTurnCapacityAnalysis current)
        {
            var history = InitiativeAnalyticsHistory.For(player);
            if (history.Count == 0)
                return current.CurrentApPressure > 0f ? "current-work/no-history" : "no-structural-work/no-history";

            InitiativeTurnRecord last = history[history.Count - 1];
            float leftover = last.TotalStartAp > 0 ? (float)last.EndAp / last.TotalStartAp : 0f;
            bool highSlack = leftover >= AiConfigV2.initiativeWasteLeftoverFrac;
            bool armyWorkRemained = last.UnactivatedActionableArmyCountAtEnd > 0;

            if (armyWorkRemained && !highSlack)
                return $"ap-limited(prevEnd={last.EndAp}/{last.TotalStartAp})";
            if (armyWorkRemained && highSlack)
                return $"non-ap-blocked(prevEnd={last.EndAp}/{last.TotalStartAp})";
            if (highSlack)
                return $"ap-stranded/non-ap-or-demand-limited(prevEnd={last.EndAp}/{last.TotalStartAp})";
            return $"capacity-balanced(prevEnd={last.EndAp}/{last.TotalStartAp})";
        }

        public static bool ShouldSuppressBonusDice(PlayerSetupData player, out string reason)
        {
            reason = null;
            var history = InitiativeAnalyticsHistory.For(player);
            if (history.Count == 0)
                return false;

            InitiativeTurnRecord last = history[history.Count - 1];
            if (last.TotalStartAp <= 0)
                return false;

            float leftover = (float)last.EndAp / last.TotalStartAp;
            if (leftover < AiConfigV2.initiativeWasteLeftoverFrac)
                return false;

            reason = $"previous turn stranded {last.EndAp}/{last.TotalStartAp} AP "
                + $"({leftover:0.00} >= {AiConfigV2.initiativeWasteLeftoverFrac:0.00}); active bottleneck was not AP";
            return true;
        }
    }
}
