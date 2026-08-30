using Game.Players;

namespace Game.Ai.V2.Initiative
{
    // Diagnostic only: initiative still decides dice from its existing value model. This label
    // prevents a zero-dice plan from being misread as "there was no strategic work" when recent
    // turns actually ended with large AP slack because actor/materialization capacity, not AP,
    // was the limiting resource.
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
    }
}
