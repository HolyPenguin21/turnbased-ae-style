using Game.Cards;

namespace Game.Ai.V2
{
    // ARCH-02 §36 — the common, lifecycle-readable shape every V2 execution-tier result projects
    // to. A caller that only needs "did it succeed, did the world move, what did it cost, do I
    // replan" reads one struct instead of learning Deployed vs Built vs ReachedGoal vs Played and
    // a different resource-spend field per producer. Each result keeps its own domain-specific
    // payload; this is the shared projection, not a replacement.
    public readonly struct V2ActionOutcome
    {
        public readonly bool Succeeded;        // the action's own success verb, normalised
        public readonly bool StateChanged;     // authoritative world state moved (even on partial failure)
        public readonly float ApSpent;         // real PlayerRoot AP delta
        public readonly ResourceCost ResourcesSpent;  // real H/E/M/T delta (null = none / not measured)
        public readonly bool Played;           // a card left the hand into play
        public readonly bool Generated;        // a card was minted this action
        public readonly bool Attached;         // equipment was attached this action
        public readonly bool Moved;            // a mover changed hex this action
        public readonly bool Created;          // a new army / building was created this action
        public readonly bool NeedsReplan;      // preflight went stale — caller must refresh + replan
        public readonly int StateVersionAfter; // producer's state-version stamp (-1 = not tracked)
        public readonly string FailReason;     // non-null iff !Succeeded (or a partial-failure note)

        public V2ActionOutcome(bool succeeded, bool stateChanged, float apSpent, ResourceCost resourcesSpent,
            bool played, bool generated, bool attached, bool moved, bool created, bool needsReplan,
            int stateVersionAfter, string failReason)
        {
            Succeeded = succeeded;
            StateChanged = stateChanged;
            ApSpent = apSpent;
            ResourcesSpent = resourcesSpent;
            Played = played;
            Generated = generated;
            Attached = attached;
            Moved = moved;
            Created = created;
            NeedsReplan = needsReplan;
            StateVersionAfter = stateVersionAfter;
            FailReason = failReason;
        }
    }

    // Implemented by every V2 execution-tier result (MaterializationResult, CardPlayResult,
    // BuildingPlayResult, InfraFulfillResult, TaskExecutor.ExecutionResult).
    public interface IV2ActionResult
    {
        V2ActionOutcome Outcome { get; }
    }
}
