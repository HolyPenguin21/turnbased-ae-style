using System.Collections.Generic;
using System.Linq;
using Game.Map;

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
    //  An intent's PreferredMoverArmyId is only claimed while the actor is STILL VALID for that
    //  intent. Active Raid combat phases use the same structural actor shape ProvisioningManager
    //  accepts: a real ground field army, not prison/airfield/air/Recce and not a lone hero awaiting
    //  escort. Raid Return is deliberately different: the objective is to bring the surviving
    //  ground container home, so it keeps the claim while it still matches ProvisionReturn's
    //  live/non-empty ground-container contract even if battle damage made it combat-ineligible.
    // ===========================================================================================
    public sealed class ActorCommitments
    {
        private readonly HashSet<int> _claimedArmyIds = new HashSet<int>();

        public IReadOnlyCollection<int> ClaimedArmyIds => _claimedArmyIds;

        // Live copy for the shared eligibility primitive (ScoutMoverSelector.Eligible takes an ISet).
        public HashSet<int> ClaimedArmyIdSet => new HashSet<int>(_claimedArmyIds);

        // armyId here is always an already-resolved concrete actor id, never a "no army" signal —
        // 0 is a legitimate ArmyData.Id (see ArmyData identity sequencing) and must be claimable
        // exactly like any other id.
        public bool IsArmyClaimed(int armyId) => _claimedArmyIds.Contains(armyId);

        // A claim with no stated contract protects the whole container (no inbound, no outbound,
        // no commander change) — the conservative default for every lane that has not said what
        // its function needs.
        public void Claim(int armyId) => Claim(armyId, ArmyMutationContract.FullyProtected);

        // T05 — the claim plus the minimum its operation needs preserved, as Housekeeping reads
        // it (ArmyReorgAnalyzer, HousekeepingExecutor). An army claimed by two operations keeps
        // only what BOTH allow.
        public void Claim(int armyId, ArmyMutationContract contract)
        {
            _claimedArmyIds.Add(armyId);
            _contracts[armyId] = _contracts.TryGetValue(armyId, out ArmyMutationContract prior)
                ? prior.Intersect(contract) : contract;
        }

        private readonly Dictionary<int, ArmyMutationContract> _contracts =
            new Dictionary<int, ArmyMutationContract>();

        // What an operation lets zero-AP Housekeeping do to its claimed container. Unclaimed
        // armies have no contract (null) — they are ordinary free formations.
        public ArmyMutationContract MutationContractOf(int armyId) =>
            _contracts.TryGetValue(armyId, out ArmyMutationContract c) ? c : null;

        // T01 — hosts of a live Attack mobilization preparation (AttackIntent.Preparation, Gather).
        // Claimed like every other operation actor; kept separately only so the ONE pinned card
        // delivery may still reach this exact claimed (possibly empty) container
        // (PlacementSelector, MaterializationDeliveryPolicy). Never a second occupancy truth.
        private readonly HashSet<int> _preparationHostIds = new HashSet<int>();
        public bool IsPreparationHost(int armyId) => _preparationHostIds.Contains(armyId);

        internal void MarkPreparationHost(int armyId) => _preparationHostIds.Add(armyId);

        // Compatibility entry points; all role validation lives in MissionActorPolicy.
        public static ActorCommitments FromIntents(IEnumerable<MissionIntent> intents,
            WorldSnapshot snap, IReadOnlyList<ReconObjective> reconObjectives) =>
            MissionActorPolicy.Build(intents, snap, reconObjectives);
        internal static bool PreparationHostStillValid(int armyId, WorldSnapshot snap) =>
            MissionActorPolicy.PreparationHostStillValid(armyId, snap);
        internal static bool GroundContainerStillValid(int armyId, WorldSnapshot snap) =>
            MissionActorPolicy.GroundContainerStillValid(armyId, snap);
        public static bool HasCapableActor(MissionIntent intent, WorldSnapshot snap, StealthRequirement requirement) =>
            MissionActorPolicy.HasCapableActor(intent, snap, requirement);

    }

    // ===========================================================================================
    //  ARMY MUTATION CONTRACT  (T05)
    // ===========================================================================================
    //  The minimum a live operation needs Housekeeping to preserve in its claimed container —
    //  function and ownership, not a frozen roster. Built only by ActorCommitments.FromIntents
    //  (the one claim authority) from the intent that claims the army; rebuilt with it, so a
    //  finished mission leaves no contract and its army is an ordinary free formation again.
    //    · MayReceive           — free same-hex bodies/heroes may join (never makes it donate:
    //                             a claimed container gives nothing away, is never folded,
    //                             swapped or deposited into a garrison);
    //    · MayReorderCommander  — the best legal hero ALREADY in the roster may take command;
    //    · KeepsMovement        — the operation moves on a route: an inbound member must not
    //                             lower the army's remaining or maximum movement (ETA, return
    //                             leg, interception timing);
    //    · MayReleaseExcessHeroes — ATK-F03, the Attack preparation host only: a hero that is not
    //                             its best commander (HeroRoleEvaluator) and no Research/Production
    //                             operator only takes a fighter slot, so it may leave, zero-AP, to
    //                             a free local container. Nothing else ever leaves a claimed army.
    //  Supports/convoys, air wings, Economy/Development actors and scouts claim with the default
    //  FullyProtected contract: their delivery/operator/stealth/income semantics are not modelled
    //  here, so Housekeeping keeps its hands off them entirely.
    // ===========================================================================================
    public sealed class ArmyMutationContract
    {
        public readonly string Label;
        public readonly bool MayReceive;
        public readonly bool MayReorderCommander;
        public readonly bool KeepsMovement;
        public readonly bool MayReleaseExcessHeroes;

        private ArmyMutationContract(string label, bool mayReceive, bool mayReorderCommander,
            bool keepsMovement, bool mayReleaseExcessHeroes = false)
        {
            Label = label;
            MayReceive = mayReceive;
            MayReorderCommander = mayReorderCommander;
            KeepsMovement = keepsMovement;
            MayReleaseExcessHeroes = mayReleaseExcessHeroes;
        }

        public static readonly ArmyMutationContract FullyProtected =
            new ArmyMutationContract("protected", false, false, true);

        // Leased operational capability (StrategicCapabilityLeaseRegistry): fresh materialized
        // force with no route yet — it may be reinforced, never taken apart.
        public static readonly ArmyMutationContract Leased =
            new ArmyMutationContract("lease", true, true, false);

        // An Attack mobilization host is being BUILT on its own base: any legal free body helps,
        // and its march speed is decided by the composition it ends up with.
        public static ArmyMutationContract PreparationHost() =>
            new ArmyMutationContract("Attack:PreparationHost", true, true, false,
                mayReleaseExcessHeroes: true);

        // Raid / Attack / ActiveDefence primaries, including their return legs.
        public static ArmyMutationContract MovingOperation(string label) =>
            new ArmyMutationContract(label, true, true, true);

        public ArmyMutationContract Intersect(ArmyMutationContract other) =>
            other == null ? this : new ArmyMutationContract(Label + "+" + other.Label,
                MayReceive && other.MayReceive,
                MayReorderCommander && other.MayReorderCommander,
                KeepsMovement || other.KeepsMovement,
                MayReleaseExcessHeroes && other.MayReleaseExcessHeroes);
    }
}
