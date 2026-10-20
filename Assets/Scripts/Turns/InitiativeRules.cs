namespace Game.Turns
{
    // The one authoritative gameplay rules contract for initiative tokens. Human buy UI
    // (InitiativeBuyPanelUI), AI planning (Game.Ai.V2.Initiative) and the roll/AP steps
    // (TurnOrderResolver, GameTurnController) ALL read these — none of them may keep their own
    // copy of a base-tokens count, a max, a price ladder or an AP-by-rank table.
    public static class InitiativeRules
    {
        // Players who still take part in a round: not eliminated. A defeated player rolls no
        // tokens, holds no turn-order slot (so survivors' ranks and AP are never pushed down by
        // them), and earns no income; their armies simply stay where they stand.
        public static System.Collections.Generic.List<Game.Players.PlayerSetupData> ActivePlayers(
            System.Collections.Generic.IEnumerable<Game.Players.PlayerSetupData> players)
        {
            var result = new System.Collections.Generic.List<Game.Players.PlayerSetupData>();
            if (players == null)
                return result;
            foreach (Game.Players.PlayerSetupData p in players)
                if (p != null && !p.IsEliminated)
                    result.Add(p);
            return result;
        }

        // Every player rolls this many tokens for free every turn.
        public const int BaseTokens = 5;

        // The most bonus tokens a player may ever pay for on top of BaseTokens.
        public const int MaxBonusTokens = 5;

        // BaseTokens + MaxBonusTokens — the hard ceiling on one player's initiative pool.
        public const int MaxTotalTokens = BaseTokens + MaxBonusTokens; // 10

        // Progressive purchase cost: the Nth bonus die (1-based N) costs 2^(N-1) resource units,
        // so the ladder is 1, 2, 4, 8, 16. `alreadyPurchased` is how many bonus tokens this player
        // has already bought this turn (0 => the first die, cost 1). A die's cost can be paid
        // from any mix of Human/Energy/Materials/Tech, one unit at a time (see PlayerRoot.
        // PurchaseInitiativeToken); human UI and AI use the same PlayerRoot API.
        public static int NextBonusTokenCost(int alreadyPurchased)
        {
            if (alreadyPurchased < 0)
                alreadyPurchased = 0;
            return 1 << alreadyPurchased;
        }

        // Total resource units needed to have exactly `tokenCount` bonus tokens fully paid off —
        // the running sum of NextBonusTokenCost(0..tokenCount-1), i.e. 1+2+4+...+2^(tokenCount-1) =
        // 2^tokenCount - 1. A single die's cost no longer has to come from one resource in one
        // purchase (see PlayerRoot.PurchaseInitiativeToken): it can be paid in any mix of 1-unit
        // H/E/M/T contributions, so completed-tokens count and "how far into the current die" are
        // both derived from this running total rather than counted per-purchase.
        public static int CumulativeUnitsForTokenCount(int tokenCount)
        {
            if (tokenCount <= 0)
                return 0;
            return (1 << tokenCount) - 1;
        }

        // AP granted for finishing the initiative roll at a given 0-based rank. Rank 0 (first) is
        // 12, rank 1 (second) is 10, every later rank is 8 — for ANY player count. There is no
        // special two-player rule. (2026-10-02: +2 for every rank, was 10/8/6 — playtest showed
        // ~12 AP/turn of unmet demand at 8-12 AP/turn.) This is the single function both the real AP allocation
        // (GameTurnController.AllocateActionPoints) and Initiative expected-AP math call.
        public static int ApForRank(int rankZeroBased)
        {
            if (rankZeroBased <= 0)
                return 12;
            if (rankZeroBased == 1)
                return 10;
            return 8;
        }
    }
}
