using System;
using System.Collections.Generic;
using System.Linq;
using Game.Map;
using Game.Players;
using Game.Units;

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
        private readonly MissionLeaseBook _leases;
        public ActorCommitments() : this(new MissionLeaseBook()) { }
        internal ActorCommitments(MissionLeaseBook leases) { _leases = leases; }
        public IReadOnlyCollection<int> ClaimedArmyIds => _leases.ClaimedActors;
        public HashSet<int> ClaimedArmyIdSet => new HashSet<int>(_leases.ClaimedActors);
        public bool IsArmyClaimed(int armyId) => _leases.IsClaimed(armyId);
        public void Claim(int armyId) => Claim(armyId, ArmyMutationContract.FullyProtected);
        public void Claim(int armyId, ArmyMutationContract contract) => _leases.ClaimForPass(armyId, contract);
        internal void Claim(MissionIntentKey operation, int armyId,
            ArmyMutationContract contract = null, bool preparationHost = false) =>
            _leases.Claim(operation, armyId, contract ?? ArmyMutationContract.FullyProtected, preparationHost);
        public ArmyMutationContract MutationContractOf(int armyId) => _leases.ContractOf(armyId);
        public bool IsPreparationHost(int armyId) => _leases.IsPreparationHost(armyId);
        internal IReadOnlyCollection<MissionIntentKey> OwnersOf(int armyId) => _leases.OwnersOf(armyId);
        internal static ArmyMutationContract PhysicalMutationContract(PlayerSetupData player, int turn,
            ArmyData army, ActorCommitments commitments) =>
            MissionActorPolicy.PhysicalMutationContract(player, turn, army, commitments);

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
        internal readonly bool ProtectsSoloRole;
        private readonly Func<ArmyData, IReadOnlyList<string>> _targetKeys;
        private readonly Func<ArmyData, IReadOnlyList<string>> _deploymentKeys;
        private readonly Func<ArmyData, UnitData, bool> _releaseBody;

        private ArmyMutationContract(string label, bool mayReceive, bool mayReorderCommander,
            bool keepsMovement, bool mayReleaseExcessHeroes = false, bool protectsSoloRole = false,
            Func<ArmyData, IReadOnlyList<string>> targetKeys = null,
            Func<ArmyData, IReadOnlyList<string>> deploymentKeys = null,
            Func<ArmyData, UnitData, bool> releaseBody = null)
        {
            Label = label;
            MayReceive = mayReceive;
            MayReorderCommander = mayReorderCommander;
            KeepsMovement = keepsMovement;
            MayReleaseExcessHeroes = mayReleaseExcessHeroes;
            ProtectsSoloRole = protectsSoloRole;
            _targetKeys = targetKeys; _deploymentKeys = deploymentKeys; _releaseBody = releaseBody;
        }

        // Read-only domain projections. No roster or mission lifecycle state is copied here.
        internal IReadOnlyList<string> TargetRosterKeys(ArmyData host) => _targetKeys?.Invoke(host);
        internal IReadOnlyList<string> ReservedDeploymentKeys(ArmyData host) => _deploymentKeys?.Invoke(host);
        internal bool MayReleaseBody(ArmyData host, UnitData unit) =>
            MayReleaseExcessHeroes && _releaseBody?.Invoke(host, unit) == true;
        internal static readonly ArmyMutationContract SoloRole =
            new ArmyMutationContract("solo-role", false, false, true, protectsSoloRole: true);

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

        internal static ArmyMutationContract PreparationHost(
            Func<ArmyData, IReadOnlyList<string>> targetKeys,
            Func<ArmyData, IReadOnlyList<string>> deploymentKeys,
            Func<ArmyData, UnitData, bool> releaseBody) =>
            new ArmyMutationContract("Attack:PreparationHost", true, true, false,
                mayReleaseExcessHeroes: true, targetKeys: targetKeys,
                deploymentKeys: deploymentKeys, releaseBody: releaseBody);

        // Raid / Attack / ActiveDefence primaries, including their return legs.
        public static ArmyMutationContract MovingOperation(string label) =>
            new ArmyMutationContract(label, true, true, true);

        public ArmyMutationContract Intersect(ArmyMutationContract other) =>
            other == null ? this : new ArmyMutationContract(Label + "+" + other.Label,
                MayReceive && other.MayReceive,
                MayReorderCommander && other.MayReorderCommander,
                KeepsMovement || other.KeepsMovement,
                MayReleaseExcessHeroes && other.MayReleaseExcessHeroes,
                ProtectsSoloRole || other.ProtectsSoloRole,
                _targetKeys ?? other._targetKeys, _deploymentKeys ?? other._deploymentKeys,
                (host, unit) => MayReleaseBody(host, unit) && other.MayReleaseBody(host, unit));
    }
}
