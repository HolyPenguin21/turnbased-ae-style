namespace Game.Turns
{
    // The one authoritative gameplay rules contract for initiative dice. Human buy UI
    // (InitiativeBuyPanelUI), AI planning (Game.Ai.V2.Initiative) and the roll/AP steps
    // (TurnOrderResolver, GameTurnController) ALL read these — none of them may keep their own
    // copy of a base-dice count, a max, a price ladder or an AP-by-rank table.
    public static class InitiativeRules
    {
        // Every player rolls this many dice for free every turn.
        public const int BaseDice = 5;

        // The most bonus dice a player may ever pay for on top of BaseDice.
        public const int MaxBonusDice = 5;

        // BaseDice + MaxBonusDice — the hard ceiling on one player's initiative pool.
        public const int MaxTotalDice = BaseDice + MaxBonusDice; // 10

        // Progressive purchase cost: the Nth bonus die (1-based N) costs 2^(N-1) resource units,
        // so the ladder is 1, 2, 4, 8, 16. `alreadyPurchased` is how many bonus dice this player
        // has already bought this turn (0 => the first die, cost 1). One die is paid ENTIRELY from
        // one Human/Energy/Materials/Tech stockpile; human UI and AI use the same PlayerRoot API.
        public static int NextBonusDieCost(int alreadyPurchased)
        {
            if (alreadyPurchased < 0)
                alreadyPurchased = 0;
            return 1 << alreadyPurchased;
        }

        // AP granted for finishing the initiative roll at a given 0-based rank. Rank 0 (first) is
        // 10, rank 1 (second) is 8, every later rank is 6 — for ANY player count. There is no
        // special two-player rule. This is the single function both the real AP allocation
        // (GameTurnController.AllocateActionPoints) and Initiative expected-AP math call.
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
