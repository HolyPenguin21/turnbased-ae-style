using Game.Map;

namespace Game.Ai.V2
{
    // The lifecycle facts of one mission step (ExecutionResult.Outcome): did it succeed, did the
    // world move. Also the one AP rule every execution result reports by.
    public readonly struct V2ActionOutcome
    {
        public readonly bool Succeeded;        // the action's own success verb, normalised
        public readonly bool StateChanged;     // authoritative world state moved (even on partial failure)

        public V2ActionOutcome(bool succeeded, bool stateChanged)
        {
            Succeeded = succeeded;
            StateChanged = stateChanged;
        }

        // The AP an action spent: the turn pool's loss across it, never negative (inside one
        // action AP only falls; a refund returns that action's own payment).
        internal static float ApSpentSince(int apBefore, PlayerRoot root) =>
            root != null ? UnityEngine.Mathf.Max(0f, apBefore - root.ActionPoints) : 0f;
    }
}
