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

        // Stronger check for "this objective is covered": the committed actor must not only exist,
        // it must still be STRUCTURALLY able to run the intent — a solo Recce, and for a stealth
        // intent (Surveil, or an exposed Explore) also stealth-capable. Read from the snapshot's
        // own-army list (populated with IsSoloRecce / IsHidden / CanEnterStealth / StealthLevel),
        // so no live game-system call. A live-but-incapable actor (folded into a combat army,
        // lost its stealth unit) leaves the objective genuinely uncovered.
        public static bool HasCapableActor(MissionIntent intent, WorldSnapshot snap)
        {
            if (intent?.PreferredMoverArmyId == null || snap?.Self?.Armies == null)
                return false;
            int id = intent.PreferredMoverArmyId.Value;
            ArmySnapshot a = null;
            foreach (ArmySnapshot s in snap.Self.Armies)
                if (s != null && s.ArmyId == id) { a = s; break; }
            if (a == null || !a.IsSoloRecce || a.IsPrison || a.IsAir || a.MemberCount <= 0)
                return false;

            bool needsStealth = intent.Scout?.Kind == ScoutTargetKind.Surveil;
            if (needsStealth && !(a.IsHidden || a.CanEnterStealth || a.StealthLevel > 0))
                return false;
            return true;
        }
    }
}
