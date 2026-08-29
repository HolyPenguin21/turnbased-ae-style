using System.Collections.Generic;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  ACTOR COMMITMENTS  (Strategy V2 — Strategic Manager)
    // ===========================================================================================
    //  A normalized "which of my own armies are already committed to an active operation" view.
    //  Built once from MissionContinuityLayer's resolved intents (and rebuilt from the reconciled
    //  registry before Phase B). Downstream code — DemandLayer, CapabilityInventory,
    //  ReusableArmySelector, StrategicManager — only ever asks IsArmyClaimed(id); it never learns
    //  HOW continuity stores mover ownership. This is what lets "existing Scout" be told apart
    //  from "available Scout", and it extends unchanged to Raid / Defence / Assembly when those
    //  gain persistent missions.
    // ===========================================================================================
    public sealed class ActorCommitments
    {
        private readonly HashSet<int> _claimedArmyIds = new HashSet<int>();

        public IReadOnlyCollection<int> ClaimedArmyIds => _claimedArmyIds;

        public bool IsArmyClaimed(int armyId) => armyId != 0 && _claimedArmyIds.Contains(armyId);

        public void Claim(int armyId)
        {
            if (armyId != 0)
                _claimedArmyIds.Add(armyId);
        }

        // For the current Scout implementation an intent's committed mover is its
        // PreferredMoverArmyId — the army that carried the intent last turn. That is an INPUT
        // here, not a contract: raids / defence assemblies will add their own claim sources.
        public static ActorCommitments FromIntents(IEnumerable<MissionIntent> intents)
        {
            var c = new ActorCommitments();
            if (intents != null)
                foreach (MissionIntent i in intents)
                    if (i?.PreferredMoverArmyId.HasValue == true)
                        c.Claim(i.PreferredMoverArmyId.Value);
            return c;
        }
    }
}
