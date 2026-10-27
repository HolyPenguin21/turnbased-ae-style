namespace Game.Setup
{
    // A transient projection of owner state. This is a policy, not another match-state store.
    public struct MatchSaveConditions
    {
        public bool HasMatch, HumanTurn, TurnConfirmed;
        public bool GameOver, Restoring, Combat, Moving, Production, BlockingDecision, CardOperation;
    }

    public static class MatchSaveEligibility
    {
        public static string UnavailableReason(MatchSaveConditions state)
        {
            if (!state.HasMatch) return "No active match to save.";
            if (state.GameOver) return "The match has finished.";
            if (state.Restoring) return "Wait for the match to finish loading.";
            if (!state.HumanTurn) return "Save is available only on your turn.";
            if (!state.TurnConfirmed) return "Confirm your turn and wait for its opening actions.";
            if (state.Combat) return "Finish the encounter before saving.";
            if (state.Moving) return "Wait for every army to finish moving.";
            if (state.Production) return "Finish the Research / Production challenge.";
            if (state.BlockingDecision) return "Close the current decision or report before saving.";
            if (state.CardOperation) return "Finish or cancel the card operation before saving.";
            return null;
        }
    }
}
