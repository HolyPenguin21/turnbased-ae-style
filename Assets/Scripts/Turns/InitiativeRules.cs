namespace Game.Turns
{
    // The one authoritative gameplay rules contract for initiative dice. Human buy UI
    // (InitiativeBuyPanelUI), AI planning (Game.Ai.V2.Initiative) and the roll/AP steps
    // (TurnOrderResolver, GameTurnController) ALL read these — none of them may keep their own
    // copy of a base-dice count, a max, a price ladder or an AP-by-rank table, so the three can
    // never silently disagree (project owner's own redesign, "AI Strategy V2 — Adaptive
    // Initiative Investment").
    public static class InitiativeRules
    {
        // Every player rolls this many dice for free every turn.
        public const int BaseDice = 5;

        // The most bonus dice a player may ever pay for on top of BaseDice.
        public const int MaxBonusDice = 5;

        // BaseDice + MaxBonusDice — the hard ceiling on one player's initiative pool.
        public const int MaxTotalDice = BaseDice + MaxBonusDice; // 10

        // Progressive purchase cost: the Nth bonus die (1-based N) costs 2^(N-1) resource units,
        // so the ladder is 1, 2, 4, 8, 16 and the running total through die N is 2^N - 1
        // (1, 3, 7, 15, 31). `alreadyPurchased` is how many bonus dice this player has already
        // bought this turn (0 => the first die, cost 1). Any legal mix of Human/Energy/Materials/
        // Tech may fund a die as long as the bundle sums to exactly this — the resource
        // composition has no effect on the roll itself.
        public static int NextBonusDieCost(int alreadyPurchased)
        {
            if (alreadyPurchased < 0)
                alreadyPurchased = 0;
            return 1 << alreadyPurchased;
        }

        // Total resource units spent to hold `bonusDiceCount` bought dice (2^count - 1).
        public static int TotalCostThrough(int bonusDiceCount)
        {
            if (bonusDiceCount <= 0)
                return 0;
            return (1 << bonusDiceCount) - 1;
        }

        // AP granted for finishing the initiative roll at a given 0-based rank. Rank 0 (first) is
        // 10, rank 1 (second) is 8, every later rank is 6 — for ANY player count. There is no
        // special two-player rule any more (project owner's own call in the V2 redesign). This is
        // the single function both the real AP allocation (GameTurnController.AllocateActionPoints)
        // and the Initiative AI's expected-AP math call — neither may hard-code the numbers.
        public static int ApForRank(int rankZeroBased)
        {
            if (rankZeroBased <= 0)
                return 10;
            if (rankZeroBased == 1)
                return 8;
            return 6;
        }
    }
}
