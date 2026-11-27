namespace Game.Ai.V2
{
    // The lifecycle facts of one mission step (ExecutionResult.Outcome): did it succeed, did the
    // world move.
    public readonly struct V2ActionOutcome
    {
        public readonly bool Succeeded;        // the action's own success verb, normalised
        public readonly bool StateChanged;     // authoritative world state moved (even on partial failure)

        public V2ActionOutcome(bool succeeded, bool stateChanged)
        {
            Succeeded = succeeded;
            StateChanged = stateChanged;
        }
    }
}
