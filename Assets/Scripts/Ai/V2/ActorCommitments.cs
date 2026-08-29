using System.Collections.Generic;
using System.Linq;
using Game.Map;
using Game.Players;

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
    //
    //  An intent's PreferredMoverArmyId only counts as a claim when it still resolves to a LIVE
    //  army owned by this player — a claim on a destroyed / transferred army is meaningless and
    //  would wrongly suppress a replacement request (the objective is really uncovered).
    // ===========================================================================================
    public sealed class ActorCommitments
    {
        private readonly HashSet<int> _claimedArmyIds = new HashSet<int>();

        public IReadOnlyCollection<int> ClaimedArmyIds => _claimedArmyIds;

        // Live copy for the shared eligibility primitive (ScoutMoverSelector.Eligible takes an ISet).
        public HashSet<int> ClaimedArmyIdSet => new HashSet<int>(_claimedArmyIds);

        public bool IsArmyClaimed(int armyId) => armyId != 0 && _claimedArmyIds.Contains(armyId);

        public void Claim(int armyId)
        {
            if (armyId != 0)
                _claimedArmyIds.Add(armyId);
        }

        // For the current Scout implementation an intent's committed mover is its
        // PreferredMoverArmyId — the army that carried the intent last turn. That is an INPUT
        // here, not a contract: raids / defence assemblies will add their own claim sources.
        public static ActorCommitments FromIntents(IEnumerable<MissionIntent> intents, PlayerSetupData player)
        {
            var c = new ActorCommitments();
            if (intents == null || player == null)
                return c;

            HashSet<int> liveOwn = new HashSet<int>(
                ArmyRegistry.AllForOwner(player).Where(a => a != null).Select(a => a.Id));

            foreach (MissionIntent i in intents)
                if (i?.PreferredMoverArmyId.HasValue == true && liveOwn.Contains(i.PreferredMoverArmyId.Value))
                    c.Claim(i.PreferredMoverArmyId.Value);
            return c;
        }

        // Does this intent still have a live, owned committed actor?
        public static bool HasLiveActor(MissionIntent intent, PlayerSetupData player) =>
            intent?.PreferredMoverArmyId.HasValue == true && player != null
            && ArmyRegistry.AllForOwner(player).Any(a => a != null && a.Id == intent.PreferredMoverArmyId.Value);
    }
}
