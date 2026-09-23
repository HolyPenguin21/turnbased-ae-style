using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Game.Aviation;
using Game.Cards;
using Game.Economy;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Units;
using UnityEngine;

using Game.Combat;

namespace Game.Ai.V2
{
    public sealed class ProvisionedMission
    {
        public MissionProposal Mission;
        public StableMissionKey Key;
        public MissionKind Kind;
        public ScoutTargetKind ScoutKind;
        public int MoverArmyId;
        public HexCoord FocusHex;
        public HexCoord ExecutionHex;
        public int? TrackedArmyId;
        public int BaselineObservedTurn;
        // Single source of truth for this provisioned task's Raid target. RaidTargetArmyId below is
        // a read-only projection for existing non-Raid/logging readers — never a second settable copy.
        public RaidTargetRef RaidTarget;
        public int RaidTargetArmyId => RaidTarget.Kind == RaidTargetKind.NeutralArmy ? RaidTarget.ArmyId : 0;
        public HexCoord RaidLastKnownHex;
        public bool RaidTargetIsNeutral;
        // AGG-RAID §9 — which leg of the Raid this provisioned task is, and the concrete actors /
        // destination it was provisioned for. Execution reads these and does NOT re-decide any of
        // them (no target re-pick, no base re-pick, no strategic re-scoring).
        public RaidMissionPhase RaidPhase = RaidMissionPhase.Assault;
        public int? RaidPrimaryArmyId;
        public int? RaidSupportArmyId;
        public int? RaidAirSupportArmyId;
        public HexCoord? RaidAirSupportLandingHex;
        public HexCoord RaidDestinationHex;
        // Reinforcement only: the support army is already standing on the primary's hex, so this
        // step is the ATOMIC roster handoff (transfer / swap) and must perform no movement.
        public bool RaidHandoffReady;
        public RaidRefitAction RaidRefitAction;
        public ActiveDefenceMissionTarget ActiveDefenceTarget;
        public EconomyMissionTarget EconomyTarget;
        public DevelopmentMissionTarget DevelopmentTarget;
        public string ReservationOwner;
        public MissionIntentKey? EconomyLoanSource;
        public ResourceVector ClaimedPhysical;
        // AP still needed for EXECUTION to carry this mission out (e.g. Economy travel/build cost).
        public float ClaimedAp;
        // 2026-09-14 review round 4 — Economy garrison-extraction no longer mutates anything during
        // Provisioning at all (see MaterializeEconomyGarrisonBuilder in TaskExecutor.cs): the round-3
        // ProvisioningApSpent field this comment used to describe is gone because there is nothing
        // for it to carry any more — Provisioning only estimates and defers, Execution spends for
        // real inside its own beforeStep/afterStep window. EconomyExtractionGarrisonArmyId (below) is
        // the deferred-materialization marker that replaced it.
        //
        // >= 0 marks this ProvisionedMission as NOT YET a real mover: MoverArmyId is a synthetic
        // negative id (SyntheticGarrisonExtractionActorId), and this field names the garrison
        // TaskExecutor.RunEconomyStep must extract a hero from — mirroring the pattern AirLaunch
        // already established (ScoutExecutorKind.AirLaunch's synthetic ActorKey + AirfieldHex/
        // LaunchSubset below materialize the same way, inside ReconAirExecutor). -1 (default) means
        // MoverArmyId is already a real, resolvable army — the common case, unchanged.
        public int EconomyExtractionGarrisonArmyId = -1;
        // 2026-09-14 review round 5 — the EXACT resolved plan (tier/hero/container/AP) Provisioning
        // chose, pinned so Execution materializes precisely that plan instead of re-running
        // ResolveGarrisonExtractionCandidate with weaker inputs (commitments:null, session:null),
        // which could legally pick a different — or already-claimed-by-someone-else — actor. Default
        // (Tier == None) whenever EconomyExtractionGarrisonArmyId is -1 (nothing deferred).
        internal ProvisioningManager.GarrisonExtractionCandidate EconomyExtractionPlan;
        // 2026-09-14 review round 8 — the FULL preparation decision (composition, donor, real AP,
        // resource stage cost) PlanEconomyCompletion computed against a read-only preview of the
        // not-yet-real container. Execution applies this pinned plan directly — see
        // TaskExecutor.MaterializeEconomyGarrisonBuilder — never re-deriving it. Default
        // (Feasible == false) whenever EconomyExtractionGarrisonArmyId is -1.
        internal ProvisioningManager.EconomyCompletionPlan EconomyExtractionPreparation;
        // 2026-09-14 review round 10 (P0) — true whenever EconomyExtractionPreparation has been
        // pinned by Provisioning but not yet APPLIED — for a garrison-extraction candidate (always,
        // alongside EconomyExtractionGarrisonArmyId >= 0) AND for a direct-army candidate whose hero
        // is already real but whose composition change and/or donor-loan suspend Provisioning left
        // for Execution to apply, never itself. False (with EconomyExtractionGarrisonArmyId == -1)
        // means this hero's roster is already exactly right — nothing to defer, straight to
        // movement, same as always.
        internal bool EconomyPreparationPending;
        // RECON-AIR-01 — the REAL Energy this mission's bound actor needs to activate (0 for Ground,
        // which never spends Energy to activate). Folded into ClaimedPhysical.Energy so it flows
        // through the SAME generic ResourceAllocator accounting AP already uses (RegisterProvisionSuccess).
        public float ClaimedEnergy;
        public bool StealthApReserved;
        // State version after provisioning completed. In the current batch adapter several
        // provisioned missions may coexist; the future selected-only loop executes immediately
        // and can require this version to still be current before issuing a gameplay command.
        public int PlannedAtStateVersion = -1;
        // AI-RECON-02 — this Scout mission's requirement is a stealthy one (StealthRequirement.
        // Required OR a non-zero DetectionRisk). Flows Requirement -> Mission -> Intent so the
        // durable ScoutIntent knows an active lane is a stealth lane a generic scout can't cover.
        public bool RequiresStealth;
        // Round 4 — which executor this Scout mission is bound to. Ground (default) is executed by
        // ReconGroundExecutor through TaskExecutor, exactly as before. AirExisting/AirLaunch are
        // executed by ReconAirExecutor (the orchestrator routes provisioned Scout missions to the
        // right executor by this tag BEFORE calling TaskExecutor.Execute — see AiStrategyV2Pipeline).
        public ScoutExecutorKind ExecutorKind = ScoutExecutorKind.Ground;
        public HexCoord AirfieldHex;                       // AirLaunch only
        public System.Collections.Generic.List<Game.Units.UnitData> LaunchSubset; // AirLaunch only
    }

    public readonly struct ProvisionFailure
    {
        public readonly ProvisionFailureKind Kind;
        public readonly ProvisionDisposition Disposition;
        // Round 7 (Problem 3) — the GENERIC multi-resource envelope this failure reports as needed.
        // Only meaningful for EnvelopeTooSmall/RepriceThisTurn; every other constructor leaves it at
        // ProvisionRequirement.Zero. RequiredAp is kept as a read-only convenience projection for
        // existing AP-only call sites/log lines — it is never the underlying storage any more.
        public readonly ProvisionRequirement Requirement;
        public readonly string Detail;

        public float RequiredAp => Requirement.Ap;

        public ProvisionFailure(ProvisionFailureKind kind, ProvisionDisposition disposition, ProvisionRequirement requirement, string detail)
        {
            Kind = kind;
            Disposition = disposition;
            Requirement = requirement;
            Detail = detail;
        }

        public static ProvisionFailure MoverContended(string d) =>
            new ProvisionFailure(ProvisionFailureKind.MoverContended, ProvisionDisposition.RetryNextTurn, ProvisionRequirement.Zero, d);
        public static ProvisionFailure NoMoverExists(string d) =>
            new ProvisionFailure(ProvisionFailureKind.NoMoverExists, ProvisionDisposition.RetryNextTurn, ProvisionRequirement.Zero, d);
        public static ProvisionFailure NoObservationVantage(string d) =>
            new ProvisionFailure(ProvisionFailureKind.NoObservationVantage, ProvisionDisposition.RejectWithCooldown, ProvisionRequirement.Zero, d);
        // AP-only convenience overload — every existing caller (Ground Scout, Raid, and any future
        // Aggression/Defence/Economy/Development provisioner) that has no physical-resource shortfall
        // keeps calling this exactly as before; Physical is Zero, so the allocator's component-wise
        // max reduces to the pre-round-7 float-floor behaviour for them.
        public static ProvisionFailure EnvelopeTooSmall(float requiredAp, string d) =>
            EnvelopeTooSmall(ProvisionRequirement.ApOnly(requiredAp), d);
        public static ProvisionFailure EnvelopeTooSmall(ProvisionRequirement requirement, string d) =>
            new ProvisionFailure(ProvisionFailureKind.EnvelopeTooSmall, ProvisionDisposition.RepriceThisTurn, requirement, d);
        public static ProvisionFailure NoExecutableStep(string d) =>
            new ProvisionFailure(ProvisionFailureKind.NoExecutableStep, ProvisionDisposition.RetryNextTurn, ProvisionRequirement.Zero, d);
        public static ProvisionFailure DestinationUnreachable(string d) =>
            new ProvisionFailure(ProvisionFailureKind.DestinationUnreachable, ProvisionDisposition.RetryNextTurn, ProvisionRequirement.Zero, d);
        public static ProvisionFailure TargetSatisfied(string d) =>
            new ProvisionFailure(ProvisionFailureKind.TargetSatisfied, ProvisionDisposition.DropThisTurn, ProvisionRequirement.Zero, d);
        public static ProvisionFailure TargetInvalidated(string d) =>
            new ProvisionFailure(ProvisionFailureKind.TargetInvalidated, ProvisionDisposition.RetryNextTurn, ProvisionRequirement.Zero, d);
        public static ProvisionFailure AssemblyInfeasible(string d) =>
            new ProvisionFailure(ProvisionFailureKind.AssemblyInfeasible, ProvisionDisposition.RejectWithCooldown, ProvisionRequirement.Zero, d);
        // Air recon only — hard feasibility passed (CanAffordLaunch + funded envelope + live AP/Energy),
        // but the staged strategic reservation decision declined to protect Energy/AP for the sortie
        // this turn. No cooldown: it is re-evaluated from a fresh snapshot every turn.
        public static ProvisionFailure SortieNotWorthwhile(string d) =>
            new ProvisionFailure(ProvisionFailureKind.SortieNotWorthwhile, ProvisionDisposition.RetryNextTurn, ProvisionRequirement.Zero, d);
    }

    public sealed class ProvisioningResult
    {
        public bool Success;
        public ProvisionedMission Provisioned;
        public ProvisionFailure Failure;
        // Provisioning is normally pure binding. Raid assembly and Economy's same-hex builder
        // lightening are the transactional exceptions: successful member transfers are versioned once only
        // after the whole transaction commits. A complete rollback reports no mutation.
        public bool StateChanged;
        public int StateVersionAfter = -1;
        public int TransferredMemberCount;

        // 2026-09-14 review round 6 (P0) — `bumpVersion` lets a caller that is itself the SOLE
        // version-bump owner for its own execution result (TaskExecutor.StampVersion, during the
        // deferred garrison-extraction Execution step) suppress this constructor's own bump so
        // V2StateVersion is bumped exactly once per real mutation, not twice. Every other caller
        // (Provisioning's own direct-army path, which has no separate StampVersion call for this
        // mutation) keeps the default `true` — unchanged behaviour.
        public static ProvisioningResult Ok(ProvisionedMission m, int transferredMemberCount = 0,
            bool bumpVersion = true)
        {
            bool changed = transferredMemberCount > 0;
            int version = changed && bumpVersion ? V2StateVersion.Bump() : V2StateVersion.Current;
            if (m != null) m.PlannedAtStateVersion = version;
            return new ProvisioningResult
            {
                Success = true,
                Provisioned = m,
                StateChanged = changed,
                StateVersionAfter = version,
                TransferredMemberCount = transferredMemberCount,
            };
        }

        public static ProvisioningResult Fail(ProvisionFailure f,
            bool stateChanged = false, int transferredMemberCount = 0)
        {
            int version = stateChanged ? V2StateVersion.Bump() : V2StateVersion.Current;
            return new ProvisioningResult
            {
                Success = false,
                Failure = f,
                StateChanged = stateChanged,
                StateVersionAfter = version,
                TransferredMemberCount = transferredMemberCount,
            };
        }
    }

    public sealed class ProvisioningSession
    {
        public readonly WorldSnapshot Snapshot;
        public float ApClaimed { get; private set; }
        // RECON-AIR-03 — the cumulative real Energy every AirLaunch mission provisioned so far
        // THIS pass has claimed. Mirrors ApClaimed's role for AP: without this, two separate
        // AirLaunch missions provisioned sequentially within the same pass each check affordability
        // against the SAME unmutated root Energy stock independently, so both can pass even though
        // launching both together would exceed it (ProvisioningManager.ProvisionAir checks against
        // this before accepting a launch).
        public float EnergyClaimed { get; private set; }
        public readonly HashSet<int> ClaimedArmyIds = new HashSet<int>();
        // Durable ownership is distinct from same-pass claims. Provisioning must preserve both:
        // the batch solvers filter with this set, and Raid live revalidation uses it for hosts and
        // assembly donors so a retry cannot steal an Economy/Recon/Raid incumbent.
        public readonly HashSet<int> DurableClaimedArmyIds = new HashSet<int>();

        private readonly Dictionary<StableMissionKey, ProvisionedMission> _successful =
            new Dictionary<StableMissionKey, ProvisionedMission>();
        private readonly Dictionary<StableMissionKey, ScoutExecutionCandidate> _assignment =
            new Dictionary<StableMissionKey, ScoutExecutionCandidate>();
        // Round 3 (Problem 2) — the rejection reason ReconAssignmentPlanner.AssignFunded already
        // computed for every Scout mission that got no actor this pass. ClassifyNoAssignment below
        // is now a pure translation of this into a ProvisionFailure — it never re-derives it.
        private readonly Dictionary<StableMissionKey, ScoutAssignmentFailureReason> _assignmentRejections =
            new Dictionary<StableMissionKey, ScoutAssignmentFailureReason>();
        private readonly Dictionary<StableMissionKey, int> _groundCombatAssignment =
            new Dictionary<StableMissionKey, int>();
        // The SAME ownership constraints used for the single ground-combat batch assignment must
        // survive into the per-leg provisioners (including their same-hex donors). Refreshed on
        // every repack. ATK §44 — this store is the ONE ground-combat actor assignment for every
        // lane that fights on the ground (Raid, ActiveDefence and, from ATK, Attack); it was named
        // after Raid only because Raid happened to be the first lane built on it.
        private ActorCommitments _groundCombatDurableCommitments;
        private HashSet<int> _groundCombatPinnedByOtherLegs = new HashSet<int>();

        public ProvisioningSession(WorldSnapshot snapshot) { Snapshot = snapshot; }
        public IReadOnlyDictionary<StableMissionKey, ProvisionedMission> Successful => _successful;
        public bool AlreadyProvisioned(StableMissionKey k) => _successful.ContainsKey(k);

        public void RegisterSuccess(StableMissionKey k, ProvisionedMission m)
        {
            _successful[k] = m;
            ApClaimed += m.ClaimedAp;
            EnergyClaimed += m.ClaimedEnergy;
            ClaimedArmyIds.Add(m.MoverArmyId);
            // 2026-09-14 review round 5 — a deferred garrison-extraction mission's MoverArmyId is a
            // synthetic negative id; the garrison and the chosen container (if one already exists —
            // Shell/Host tiers) are the REAL armies this mission has committed to and must not be
            // handed to a second mission later in the same batch pass.
            if (m.EconomyExtractionGarrisonArmyId >= 0)
                ClaimedArmyIds.Add(m.EconomyExtractionGarrisonArmyId);
            if (m.EconomyExtractionPlan.Container != null)
                ClaimedArmyIds.Add(m.EconomyExtractionPlan.Container.Id);
        }

        internal void SetAssignment(ReconAssignmentResult result)
        {
            _assignment.Clear();
            _assignmentRejections.Clear();
            if (result == null) return;
            foreach (KeyValuePair<StableMissionKey, ScoutExecutionCandidate> kv in result.Assigned)
                _assignment[kv.Key] = kv.Value;
            foreach (KeyValuePair<StableMissionKey, ScoutAssignmentFailureReason> kv in result.Rejected)
                _assignmentRejections[kv.Key] = kv.Value;
        }

        internal bool TryGetAssignedExecution(StableMissionKey k, out ScoutExecutionCandidate exec) =>
            _assignment.TryGetValue(k, out exec);

        internal bool TryGetAssignmentRejection(StableMissionKey k, out ScoutAssignmentFailureReason reason) =>
            _assignmentRejections.TryGetValue(k, out reason);

        internal IReadOnlyDictionary<StableMissionKey, ScoutAssignmentFailureReason>
            AssignmentRejections => _assignmentRejections;

        internal void SetGroundCombatConstraints(ActorCommitments durableCommitments,
            ISet<int> pinnedByOtherLegs)
        {
            _groundCombatDurableCommitments = durableCommitments;
            _groundCombatPinnedByOtherLegs = pinnedByOtherLegs == null
                ? new HashSet<int>() : new HashSet<int>(pinnedByOtherLegs);
        }

        // Single ground-combat ownership read, shared by batch assignment AND final binding.
        // A durable mission may use its own incumbent, but never another mission's army;
        // exclusions also apply to donors, not merely the primary host.
        internal HashSet<int> ExcludedForGroundCombat(MissionProposal proposal)
        {
            var excluded = new HashSet<int>(ClaimedArmyIds);
            foreach (int pinnedId in _groundCombatPinnedByOtherLegs)
            {
                // The pinned set is computed across ALL funded non-Assault Raid legs, including
                // this very Reinforcement/Return leg. Its own actor must remain permitted;
                // never erase a real same-pass claim or an assignment belonging to another Raid.
                bool thisLegsActor = proposal?.Target is RaidMissionTarget raid
                    && ((raid.Phase == RaidMissionPhase.Reinforcement
                            && raid.SupportArmyId == pinnedId)
                        || (raid.Phase == RaidMissionPhase.Return
                            && raid.PrimaryArmyId == pinnedId)
                        || (raid.Phase == RaidMissionPhase.RecoveryReturn
                            && raid.PrimaryArmyId == pinnedId)
                        || (raid.Phase == RaidMissionPhase.SupportReturn
                            && raid.SupportArmyId == pinnedId));
                if (!thisLegsActor)
                    excluded.Add(pinnedId);
            }
            if (_groundCombatDurableCommitments != null)
                foreach (int id in _groundCombatDurableCommitments.ClaimedArmyIds)
                {
                    bool ownIncumbent = proposal != null && proposal.FromDurableIntent
                        && proposal.PreferredMoverArmyId == id;
                    bool exactOffensiveBorrow = proposal?.Target is ActiveDefenceMissionTarget defence
                        && defence.SuspendedOffensiveIntentKey.HasValue
                        && proposal.PreferredMoverArmyId == id;
                    if (!ownIncumbent && !exactOffensiveBorrow)
                        excluded.Add(id);
                }
            // Batch-assigned Raid hosts/support are also unavailable as donors, even before
            // their mission executes and RegisterSuccess adds them to ClaimedArmyIds.
            StableMissionKey? ownKey = proposal == null
                ? (StableMissionKey?)null : StableMissionKey.For(proposal);
            foreach (KeyValuePair<StableMissionKey, int> assignment in _groundCombatAssignment)
                if (!ownKey.HasValue || !assignment.Key.Equals(ownKey.Value))
                    excluded.Add(assignment.Value);
            return excluded;
        }

        internal void SetGroundCombatAssignment(Dictionary<StableMissionKey, int> a)
        {
            _groundCombatAssignment.Clear();
            foreach (KeyValuePair<StableMissionKey, int> kv in a)
                _groundCombatAssignment[kv.Key] = kv.Value;
        }

        internal void SetDurableClaims(IEnumerable<int> ids)
        {
            DurableClaimedArmyIds.Clear();
            if (ids == null) return;
            foreach (int id in ids) DurableClaimedArmyIds.Add(id);
        }

        internal bool TryGetAssignedGroundCombatActor(StableMissionKey k, out int armyId) =>
            _groundCombatAssignment.TryGetValue(k, out armyId);
    }

    internal static class ProvisioningManager
    {
        private static int StealthTransitionApCost => AiConfigV2.scoutOptionalStealthAp;

        // Live structural revalidation uses the same canonical army-role predicate Analysis
        // froze into ArmySnapshot.IsMobileEconomyBuilder.
        private static bool IsMobileEconomyHero(ArmyData army, PlayerSetupData player) =>
            army != null && army.Owner == player && AiArmyRoles.IsHeroLed(army);

        // 2026-09-14 review round 4 — Economy garrison-extraction no longer materializes during
        // Provisioning at all. ResolveGarrisonExtractionCandidate (pure, unchanged since round 2)
        // decides the tier here only to produce a conservative pre-mutation cost ESTIMATE for the
        // funding envelope; the actual ArmyActions.CreateArmy/TransferMember mutation
        // (ApplyGarrisonExtraction, also unchanged) now runs from
        // TaskExecutor.MaterializeEconomyGarrisonBuilder, inside that step's own beforeStep/
        // afterStep observation window — mirroring the synthetic-actor-id pattern
        // ScoutExecutorKind.AirLaunch already established (see SyntheticGarrisonExtractionActorId
        // below and MoverArmyId's own field comment on ProvisionedMission).
        internal static int SyntheticGarrisonExtractionActorId(int garrisonArmyId) =>
            -(3_000_000 + (garrisonArmyId & 0xFFFFFF));

        // The garrison-extraction container choice is a pure, read-only RESOLVE step
        // (GarrisonExtractionCandidate) that every caller — the Provisioning-time estimate above,
        // TaskExecutor's real materialization, and the FoundBase diagnostic TRACE — shares. The
        // TRACE prints this struct's fields verbatim instead of re-deriving its own copy of the
        // eligibility logic (the previous copy drifted out of sync with the real gates: it never
        // accounted for ArmyData.RequiresActivationCharge, an existing hero already in a "host"
        // candidate, or ProvisioningSession.ClaimedArmyIds — see
        // Docs/ai-economy-mover-materialization-decision-tree.md).
        internal enum GarrisonExtractionTier { None, Shell, Host, Create }

        internal readonly struct GarrisonExtractionCandidate
        {
            public readonly GarrisonExtractionTier Tier;
            public readonly UnitData Hero;         // the EXACT UnitData that would be extracted
            public readonly ArmyData Container;    // existing shell/host; null for Create (nothing exists yet)
            // Shell/Host: TransferMember's activation charge, if any. Create: CreateArmyApCost.
            // P0-2, AI V2 economy audit 2026-09-21 — for Tier == None this is now the cheapest
            // structurally-legal tier's AP cost when one exists but exceeded the envelope (0f only
            // when truly nothing exists, e.g. no sparable hero at all). Callers that only cared
            // about Tier are unaffected; a caller that wants the real shortfall reads this instead
            // of falling back to a generic NoMoverExists with no price.
            public readonly float ApCost;
            public readonly string Reason;          // set only when Tier == None

            private GarrisonExtractionCandidate(GarrisonExtractionTier tier, UnitData hero,
                ArmyData container, float apCost, string reason)
            {
                Tier = tier; Hero = hero; Container = container; ApCost = apCost; Reason = reason;
            }

            public static GarrisonExtractionCandidate No(string reason, float requiredAp = 0f) =>
                new GarrisonExtractionCandidate(GarrisonExtractionTier.None, null, null, requiredAp, reason);
            public static GarrisonExtractionCandidate Yes(GarrisonExtractionTier tier, UnitData hero,
                ArmyData container, float apCost) =>
                new GarrisonExtractionCandidate(tier, hero, container, apCost, null);
        }

        // Three tiers, tried in order, matching the decision tree in
        // Docs/ai-economy-mover-materialization-decision-tree.md (case 1.3 / two 1.4 extensions).
        // Pure: never touches game state. A container's AP cost (activation charge on an
        // already-acted shell/host, or CreateArmyApCost for a fresh one) is checked against BOTH
        // the ECO-axis envelope and the raw AP pool before it is accepted, so a candidate this
        // pass genuinely cannot afford is skipped in favour of the next one rather than accepted
        // and left to fail downstream.
        internal static GarrisonExtractionCandidate ResolveGarrisonExtractionCandidate(
            PlayerSetupData player, ArmyData garrison, ActorCommitments commitments,
            ProvisioningSession session, PlayerRoot root, float ecoApEnvelopeRemaining,
            UnitData exactDevelopmentHero = null,
            ResearchProductionMode? exactDevelopmentMode = null)
        {
            UnitData sparable = exactDevelopmentHero == null
                ? AiArmyRoles.BestSparableEconomyHero(player, garrison)
                : exactDevelopmentMode.HasValue
                    ? AiArmyRoles.BestSparableDevelopmentHero(player, garrison,
                        ResearchProductionSystem.RoleAbility(exactDevelopmentMode.Value))
                    : null;
            if (exactDevelopmentHero != null && !ReferenceEquals(sparable, exactDevelopmentHero))
                return GarrisonExtractionCandidate.No("specific Development hero is no longer sparable");
            if (sparable == null)
                return GarrisonExtractionCandidate.No("no sparable hero in garrison");

            bool Affordable(float apCost) =>
                apCost <= ecoApEnvelopeRemaining + AiConfigV2.allocatorSliceEpsilon
                && (root == null || root.CanSpendActionPoints(Mathf.CeilToInt(apCost)));

            // P0-2, AI V2 economy audit 2026-09-21 — track the cheapest structurally-legal tier's
            // cost even when it is rejected for being unaffordable THIS turn, so a caller with the
            // real envelope can report EnvelopeTooSmall(requiredAp) instead of a generic NoMoverExists
            // whenever a container genuinely exists and only the budget was too small. The Shell ->
            // Host -> Create try-in-order and every existing eligibility/CanTransferMembers gate are
            // unchanged; this only adds bookkeeping on the already-rejected path.
            float? cheapestUnaffordable = null;
            void TrackUnaffordable(float apCost) => cheapestUnaffordable =
                cheapestUnaffordable.HasValue ? Mathf.Min(cheapestUnaffordable.Value, apCost) : apCost;

            ArmyData shell = ReusableArmySelector.FindReusableAt(player, garrison.Hex, commitments);
            if (shell != null && (session == null || !session.ClaimedArmyIds.Contains(shell.Id))
                && ArmyActions.CanTransferMembers(
                    new[] { sparable }, garrison, shell, out _))
            {
                float apCost = shell.RequiresActivationCharge(sparable) ? sparable.ActivationApCost : 0f;
                if (Affordable(apCost))
                    return GarrisonExtractionCandidate.Yes(GarrisonExtractionTier.Shell, sparable, shell, apCost);
                TrackUnaffordable(apCost);
            }

            foreach (ArmyData host in EconomyHostCandidates(player, garrison, commitments, session))
            {
                if (!ArmyActions.CanTransferMembers(new[] { sparable }, garrison, host, out _))
                    continue;
                float apCost = host.RequiresActivationCharge(sparable) ? sparable.ActivationApCost : 0f;
                if (Affordable(apCost))
                    return GarrisonExtractionCandidate.Yes(GarrisonExtractionTier.Host, sparable, host, apCost);
                TrackUnaffordable(apCost);
            }

            // A fresh empty army always has room for the first member (CardPlayExecutor.Preflight
            // makes the same assumption for its own NewArmy path), so no CanTransferMembers probe
            // is possible or needed here — there is no ArmyData yet to probe against.
            if (Affordable(ArmyActions.CreateArmyApCost))
                return GarrisonExtractionCandidate.Yes(
                    GarrisonExtractionTier.Create, sparable, null, ArmyActions.CreateArmyApCost);
            TrackUnaffordable(ArmyActions.CreateArmyApCost);

            return GarrisonExtractionCandidate.No(
                "no free shell, no eligible host army, and no ECO-axis room left to create one",
                cheapestUnaffordable ?? ArmyActions.CreateArmyApCost);
        }

        // 2026-09-14 review round 8 (P0) — builds a READ-ONLY preview of what the deferred
        // garrison-extraction container will look like immediately after the (real) hero transfer,
        // so PlanEconomyCompletion can compute the FULL composition/donor/AP decision at
        // Provisioning time against ArmyData's own real Members/MaxMovement/CurrentMovement/
        // HasActivatedThisTurn projections — no duplicated math, no live mutation.
        // ArmyData.CreateVisualSnapshot() never touches ArmyRegistry or burns a real Id (Id stays
        // -1), which is exactly what a caller must use `identityArmyId` for instead (see
        // PlanEconomyArmyLightening's own comment on that parameter).
        private static ArmyData BuildGarrisonExtractionPreview(
            PlayerSetupData player, ArmyData garrison, GarrisonExtractionCandidate plan)
        {
            ArmyData preview = ArmyData.CreateVisualSnapshot();
            preview.Hex = garrison.Hex;
            preview.Owner = player;
            if (plan.Container != null)
            {
                preview.Members.AddRange(plan.Container.Members);
                // Propagate the REAL container's activation state so the projected AP/charge math
                // (EconomyMissionClaimedAp, ArmyActions.RequiresActivationCharge-style reasoning)
                // matches what Execution will actually see once the hero is really transferred in —
                // MarkActivated covers exactly the pre-existing members, matching a container that
                // already moved this turn; the hero appended below is deliberately NOT covered, same
                // as any brand-new join into an activated army.
                if (plan.Container.HasActivatedThisTurn)
                    preview.MarkActivated();
            }
            preview.Members.Add(plan.Hero);
            return preview;
        }

        // Applies the resolved extraction through canonical domain actions.
        internal static ArmyData ApplyGarrisonExtraction(PlayerSetupData player, ArmyData garrison,
            GarrisonExtractionCandidate candidate, AiTurnContext ctx)
        {
            if (candidate.Tier == GarrisonExtractionTier.None)
                return null;

            ArmyData container = candidate.Container;
            if (candidate.Tier == GarrisonExtractionTier.Create)
            {
                FactionCardCatalog catalog = ctx.StartingDeckCatalog?.GetCatalog(player.Faction);
                container = ArmyActions.CreateArmyWithMember(player, garrison.Hex, catalog,
                    garrison, candidate.Hero, ctx.HexSelection, out string whyCreate);
                if (container == null)
                    return null;
                AiDebugLog.Write($"[AI][V2][Economy] extracted idle hero {candidate.Hero.Name} "
                    + $"from garrison #{garrison.Id} into #{container.Id} ({candidate.Tier}, "
                    + $"ap {candidate.ApCost:0.##}) for economy mobile_hero duty");
                return container;
            }

            if (!ArmyActions.TransferMember(candidate.Hero, garrison, container,
                    ctx.HexSelection, out string why))
                return null;
            AiDebugLog.Write($"[AI][V2][Economy] extracted idle hero {candidate.Hero.Name} "
                + $"from garrison #{garrison.Id} into #{container.Id} ({candidate.Tier}, "
                + $"ap {candidate.ApCost:0.##}) for economy mobile_hero duty");
            return container;
        }

        // 1.1.1 (not claimed by ANY other active mission — durable intents via `commitments`,
        // AND missions already provisioned earlier in this same batch pass via
        // session.ClaimedArmyIds, which ActorCommitments does not see) + 1.1.2 (smallest first).
        // Populated armies only — the empty case is ReusableArmySelector's own, separate
        // responsibility; this never overlaps it (Members.Count > 0 here). An army that already
        // has a hero is excluded: it is itself a potential direct Economy mover (case 1.2a), not a
        // container to extract a SECOND hero into — ArmyData.AddMemberSorted inserts a new hero
        // after any existing ones, so a "which hero did we actually just extract" ambiguity is a
        // structural risk this exclusion removes at the source rather than downstream.
        private static IEnumerable<ArmyData> EconomyHostCandidates(
            PlayerSetupData player, ArmyData garrison, ActorCommitments commitments,
            ProvisioningSession session)
        {
            return ArmyRegistry.AllForOwner(player)
                .Where(a => a != null && a != garrison && a.Hex.Equals(garrison.Hex)
                    && !a.IsGarrison && !a.IsPrison
                    && !AviationRules.IsAirfield(a) && !AviationRules.IsAirArmy(a)
                    && a.Members.Count > 0 && !a.Members.Any(u => u != null && u.IsHero)
                    && (commitments == null || !commitments.IsArmyClaimed(a.Id))
                    && (session == null || !session.ClaimedArmyIds.Contains(a.Id)))
                .OrderBy(a => a.Members.Count)
                .ThenBy(a => a.Id);
        }

        // 2026-09-14 (garrison recon materialization consistency, Task 2/3) — the old
        // TryExtractGarrisonRecceForScouting re-searched ReusableArmySelector.FindReusableAt itself
        // at commit time, a SECOND container search independent of the one
        // ReconAssignmentPlanner.BuildCandidates already ran to admit the candidate in the first
        // place — the exact "two owners can silently disagree" gap this pass closes. Provision's
        // Scout/Ground branch below now resolves the pinned (source garrison, destination shell)
        // pair Assignment already chose and commits/rolls back that EXACT pair inline; there is no
        // longer a separate extraction primitive to call.

        public static void PreparePass(PlayerSetupData player, PlayerRoot root, AiTurnContext ctx,
            ProvisioningSession session, TentativeAllocation allocation,
            ActorCommitments durableCommitments = null)
        {
            session.SetDurableClaims(durableCommitments?.ClaimedArmyIds);
            PrepareScoutAssignments(player, root, ctx, session, allocation, durableCommitments);
            PrepareGroundCombatAssignments(session, allocation, durableCommitments);
        }

        // Assignment is solved for the whole funded Scout set. Expose every negative result as one
        // batch so orchestration can return all impossible jobs to the existing allocator before it
        // chooses the next mission. This is vocabulary translation only; ReconAssignmentPlanner
        // remains the sole owner of assignment feasibility and rejection reasons.
        internal static IReadOnlyList<(FundedEntry Funded, ProvisionFailure Failure)>
            ScoutAssignmentFailures(ProvisioningSession session, TentativeAllocation allocation)
        {
            var failures = new List<(FundedEntry, ProvisionFailure)>();
            if (session == null || allocation?.Funded == null)
                return failures;
            foreach (FundedEntry funded in allocation.Funded)
            {
                if (funded?.Mission?.Kind != MissionKind.Scout
                    || !(funded.Mission.Target is ScoutMissionTarget target))
                    continue;
                StableMissionKey key = StableMissionKey.For(funded.Mission);
                if (!session.AssignmentRejections.ContainsKey(key))
                    continue;
                failures.Add((funded, AssignmentFailure(session, key, target)));
            }
            return failures;
        }

        // Delegates the actual actor<->job matching to ReconAssignmentPlanner (the ONE canonical
        // Assignment owner, spec Level 5) — ProvisioningManager only collects the open FUNDED Scout
        // missions, hands them over, and stores the result. Only funded missions ever reach here:
        // there is no pre-funding reservation state to reconcile against any more. Round 4 — `root`
        // is now threaded through so AssignFunded can size/probe the air-actor pool (Energy/AP gates)
        // the same way ReconAirReservationPrepass already does for capacity sizing.
        private static void PrepareScoutAssignments(PlayerSetupData player, PlayerRoot root, AiTurnContext ctx,
            ProvisioningSession session, TentativeAllocation allocation,
            ActorCommitments durableCommitments)
        {
            var open = new List<FundedEntry>();
            if (allocation?.Funded != null)
                foreach (FundedEntry fe in allocation.Funded)
                {
                    if (fe?.Mission == null || fe.Mission.Kind != MissionKind.Scout
                        || !(fe.Mission.Target is ScoutMissionTarget)
                        || session.AlreadyProvisioned(StableMissionKey.For(fe.Mission)))
                        continue;
                    open.Add(fe);
                }
            open.Sort((a, b) => a.Priority.CompareTo(b.Priority));

            ReconAssignmentResult result = ReconAssignmentPlanner.AssignFunded(
                session.Snapshot, ctx, player, open, session.ClaimedArmyIds, root,
                session.Successful.Values.ToList(), durableCommitments?.ClaimedArmyIdSet);
            session.SetAssignment(result);
        }

        private static void PrepareGroundCombatAssignments(ProvisioningSession session,
            TentativeAllocation allocation, ActorCommitments durableCommitments)
        {
            var open = new List<FundedEntry>();
            // AGG-RAID §8 / ATK §44 — ALL ground-combat proposals are decided in ONE pass, so one
            // army can never simultaneously receive an Assault assignment and be pinned as another
            // mission's primary or reinforcement convoy. Every ground-combat MissionKind admitted
            // below shares this one solve; a new lane joins the batch, it does not get its own.
            var pinnedByOtherLegs = new HashSet<int>();
            if (allocation?.Funded != null)
                foreach (FundedEntry fe in allocation.Funded)
                {
                    if (fe?.Mission?.Target is ActiveDefenceMissionTarget activeReturn
                        && activeReturn.Phase == ActiveDefencePhase.Return
                        && activeReturn.PrimaryArmyId.HasValue)
                    {
                        pinnedByOtherLegs.Add(activeReturn.PrimaryArmyId.Value);
                        continue;
                    }
                    if (fe?.Mission == null || fe.Mission.Kind != MissionKind.Raid
                        || !(fe.Mission.Target is RaidMissionTarget rt)
                        || rt.Phase == RaidMissionPhase.Assault)
                        continue;
                    if (rt.PrimaryArmyId.HasValue) pinnedByOtherLegs.Add(rt.PrimaryArmyId.Value);
                    if (rt.SupportArmyId.HasValue) pinnedByOtherLegs.Add(rt.SupportArmyId.Value);
                }
            if (allocation?.Funded != null)
                foreach (FundedEntry fe in allocation.Funded)
                {
                    if (fe?.Mission == null
                        || (fe.Mission.Kind != MissionKind.Raid
                            && fe.Mission.Kind != MissionKind.ActiveDefence)
                        || session.AlreadyProvisioned(StableMissionKey.For(fe.Mission)))
                        continue;
                    // Non-Assault legs normally already have their actor pinned by Continuity and
                    // take no part in the assignment solve. AGG-RAID P0#1 — an UNPINNED
                    // Reinforcement leg (no SupportArmyId yet, i.e. no prior materialization handoff
                    // assigned one) IS an actor-contention decision for an EXISTING free army and
                    // must join the same batch solve Assault uses.
                    if (fe.Mission.Target is RaidMissionTarget t && t.Phase != RaidMissionPhase.Assault
                        && !(t.Phase == RaidMissionPhase.Reinforcement && !t.SupportArmyId.HasValue))
                        continue;
                    if (fe.Mission.Target is ActiveDefenceMissionTarget ad
                        && ad.Phase == ActiveDefencePhase.Return)
                        continue;
                    open.Add(fe);
                }
            open.Sort((a, b) => a.Priority.CompareTo(b.Priority));

            // A re-pack refreshes the entire assignment; never let last pass's assignments
            // exclude current candidates while solving the new batch.
            session.SetGroundCombatAssignment(new Dictionary<StableMissionKey, int>());
            session.SetGroundCombatConstraints(durableCommitments, pinnedByOtherLegs);
            var cands = new List<List<int>>(open.Count);
            foreach (FundedEntry fe in open)
            {
                HashSet<int> excluded = session.ExcludedForGroundCombat(fe.Mission);
                var ids = new List<int>();
                if (GroundCombatAdmissionRegistry.TryGet(fe.Mission, out HashSet<int> eligible))
                    ids.AddRange(eligible
                        .Where(id => !excluded.Contains(id))
                        .OrderBy(id => GroundCombatActorActivation(session.Snapshot, id))
                        .ThenBy(id => GroundCombatActorPower(session.Snapshot, id))
                        .ThenBy(id => id));
                cands.Add(ids);
            }

            var chosen = new int[open.Count];
            var best = new int[open.Count];
            for (int i = 0; i < best.Length; i++) best[i] = -1;
            long[] bestKey = null;
            RecurseGroundCombat(0, open, cands, chosen, new HashSet<int>(), session.Snapshot, ref bestKey, best);

            var map = new Dictionary<StableMissionKey, int>();
            for (int i = 0; i < open.Count; i++)
                if (best[i] >= 0)
                    map[StableMissionKey.For(open[i].Mission)] = cands[i][best[i]];
            session.SetGroundCombatAssignment(map);

            if (open.Count > 0)
                AiDebugLog.Write($"[AI][V2]   provision prepare ground-combat — {open.Count} open, assigned ["
                    + string.Join(" ", map.Select(kv => $"{kv.Key}->#{kv.Value}")) + "]");
        }

        private static int GroundCombatActorActivation(WorldSnapshot snap, int id)
        {
            ArmySnapshot a = snap?.Self?.Armies?.FirstOrDefault(x => x != null && x.ArmyId == id);
            return a == null || a.HasActivatedThisTurn ? 0 : a.ActivationApCost;
        }

        private static float GroundCombatActorPower(WorldSnapshot snap, int id) =>
            snap?.Self?.Armies?.FirstOrDefault(x => x != null && x.ArmyId == id)?.EffectiveArmyPower ?? float.MaxValue;

        private static void RecurseGroundCombat(int i, List<FundedEntry> open, List<List<int>> cands,
            int[] chosen, HashSet<int> usedArmyIds, WorldSnapshot snap, ref long[] bestKey, int[] best)
        {
            if (i == open.Count)
            {
                long[] key = ScoreGroundCombatAssignment(open, cands, chosen, snap);
                if (bestKey == null || Lex(key, bestKey) < 0)
                {
                    bestKey = key;
                    Array.Copy(chosen, best, chosen.Length);
                }
                return;
            }

            chosen[i] = -1;
            RecurseGroundCombat(i + 1, open, cands, chosen, usedArmyIds, snap, ref bestKey, best);
            for (int c = 0; c < cands[i].Count; c++)
            {
                int aid = cands[i][c];
                if (usedArmyIds.Contains(aid)) continue;
                usedArmyIds.Add(aid);
                chosen[i] = c;
                RecurseGroundCombat(i + 1, open, cands, chosen, usedArmyIds, snap, ref bestKey, best);
                usedArmyIds.Remove(aid);
            }
            chosen[i] = -1;
        }

        private static long[] ScoreGroundCombatAssignment(List<FundedEntry> open, List<List<int>> cands,
            int[] chosen, WorldSnapshot snap)
        {
            int n = open.Count;
            int covered = 0;
            long priorityCoverage = 0;
            int actorDiscontinuity = 0;
            long activation = 0;
            long overkillPower = 0;
            long actorIdSum = 0;

            for (int i = 0; i < n; i++)
            {
                if (chosen[i] < 0) continue;
                int actorId = cands[i][chosen[i]];
                covered++;
                priorityCoverage += n - i;
                activation += GroundCombatActorActivation(snap, actorId);
                overkillPower += Mathf.RoundToInt(GroundCombatActorPower(snap, actorId) * 100f);
                actorIdSum += actorId;

                int? preferred = open[i].Mission.PreferredMoverArmyId;
                if (preferred.HasValue && actorId != preferred.Value && cands[i].Contains(preferred.Value))
                    actorDiscontinuity++;
            }

            var key = new long[6 + n];
            key[0] = -covered;
            key[1] = -priorityCoverage;
            key[2] = actorDiscontinuity;
            key[3] = activation;
            key[4] = overkillPower;
            key[5] = actorIdSum;
            for (int i = 0; i < n; i++)
                key[6 + i] = chosen[i] < 0 ? long.MaxValue : cands[i][chosen[i]];
            return key;
        }

        // Was byte-identical to ReconAssignmentPlanner's copy — body moved to AiV2Util.
        private static int Lex(long[] a, long[] b) => AiV2Util.Lex(a, b);

        public static ProvisioningResult Provision(PlayerSetupData player, PlayerRoot root, AiHandData hand,
            AiTurnContext ctx, ProvisioningSession session, FundedEntry funded)
        {
            MissionProposal m = funded?.Mission;
            if (m == null || ctx?.Map == null || root == null)
                return ProvisioningResult.Fail(ProvisionFailure.AssemblyInfeasible("no mission / map / root"));

            if (m.Kind == MissionKind.Raid)
                return RaidProvisioner.Provision(player, root, ctx, session, funded);
            if (m.Kind == MissionKind.ActiveDefence)
                return ActiveDefenceProvisioner.Provision(player, root, ctx, session, funded);

            if (m.Kind == MissionKind.Economy && m.Target is EconomyMissionTarget economy)
                return ProvisionEconomy(player, root, hand, ctx, session, funded, economy);

            if (m.Kind == MissionKind.Development && m.Target is DevelopmentMissionTarget development)
                return ProvisionDevelopment(player, root, ctx, session, funded, development);

            if (m.Kind != MissionKind.Scout || !(m.Target is ScoutMissionTarget target))
                return ProvisioningResult.Fail(ProvisionFailure.AssemblyInfeasible("unsupported mission kind"));

            StableMissionKey key = StableMissionKey.For(m);
            bool surveil = target.Kind == ScoutTargetKind.Surveil;
            bool refresh = ReconScoutKinds.IsRefresh(target.Kind);

            if (!session.TryGetAssignedExecution(key, out ScoutExecutionCandidate exec))
                return ClassifyNoAssignment(session, key, target);

            if (exec.ExecutorKind != ScoutExecutorKind.Ground)
                return ProvisionAir(player, root, ctx, session, funded, exec, target, key);

            // Task 1/2/3 (2026-09-14, garrison recon materialization consistency) — identity uses
            // the destination SHELL from the start for a garrison candidate (Task 3: the source
            // garrison id must never leak into MoverArmyId / claims / durable intent), and the pair
            // Assignment already pinned (BuildCandidates: source garrison + a then-free
            // ReusableArmySelector shell) is re-resolved here as a pure PREFLIGHT probe — Provisioning
            // must never re-search for "any" shell, or two funded missions in the same pass could
            // silently agree on a container Assignment never actually reserved for both (Task 2).
            int moverArmyId = exec.RequiresGarrisonExtraction ? exec.MaterializationArmyId : exec.Army.ArmyId;
            ArmyData garrisonArmy = null;
            ArmyData destinationShell = null;
            UnitData plannedExtractUnit = null;
            ArmyData army;

            if (exec.RequiresGarrisonExtraction)
            {
                garrisonArmy = ResolveArmy(player, exec.SourceGarrisonArmyId);
                plannedExtractUnit = garrisonArmy == null
                    ? null : AiArmyRoles.BestSparableGarrisonRecce(player, garrisonArmy);
                destinationShell = ResolveArmy(player, exec.MaterializationArmyId);
                // Review round (2026-09-14) items 4/5 — the live re-check now goes through the SAME
                // canonical ReusableArmySelector.IsReusableShell predicate Assignment's own shell
                // search is built on (owner/controller/prison/garrison/aviation/commitment-claim),
                // not a hand-rolled `Members.Count == 0` check that misses all of those. Also
                // rejects a shell claimed earlier THIS session (another mission's MoverArmyId already)
                // and one that already activated this turn (item 4) — ArmyActions.TransferMember
                // would otherwise charge the incoming unit's ActivationApCost immediately, live,
                // against root.ActionPoints, an AP spend this pass's ClaimedAp/
                // ProvisioningSession.ApClaimed accounting has no channel to report without double-
                // subtracting it. ActorCommitments here mirrors the exact construction
                // ReconAssignmentPlanner.AssignFunded already used to admit this same pair.
                ActorCommitments commitments = ActorCommitments.FromIntents(
                    MissionIntentRegistry.GetOrCreate(player).All
                        .Where(i => i != null && i.Status == IntentStatus.Active).ToList(),
                    session.Snapshot, null);
                if (garrisonArmy == null || plannedExtractUnit == null
                    || destinationShell == null
                    || !ReusableArmySelector.IsReusableShell(destinationShell, player, commitments)
                    || session.ClaimedArmyIds.Contains(destinationShell.Id)
                    || destinationShell.HasActivatedThisTurn)
                    return ProvisioningResult.Fail(ProvisionFailure.MoverContended(
                        $"assigned garrison #{exec.SourceGarrisonArmyId} / shell #{exec.MaterializationArmyId} "
                        + "is no longer a usable extraction pair"));
                army = null; // not a real mover until TransferMember commits, below
            }
            else
            {
                army = ResolveArmy(player, moverArmyId);
                if (army == null || army.Owner != player || army.Members.Count == 0
                    || !AiArmyRoles.IsSoloRecce(army) || army.CurrentMovement <= 0)
                    return ProvisioningResult.Fail(ProvisionFailure.MoverContended(
                        $"assigned mover #{moverArmyId} is no longer a usable solo Recce"));
            }

            HexCoord focus = target.FocusHex;
            HexCoord executionHex = exec.ExecutionHex;

            if (surveil)
            {
                int trackedId = target.Contact?.Army?.ArmyId ?? -1;
                if (trackedId < 0 || target.Contact.Source != ContactSource.Honest
                    || target.Contact.Knowledge != ContactKnowledge.LastKnown)
                    return ProvisioningResult.Fail(ProvisionFailure.TargetInvalidated(
                        "surveil target is no longer an honest last-known contact"));
                int baseline = target.Contact.LastObservedTurn;
                if (VisionSystem.IsVisible(player, focus) || HasFresherSighting(player, trackedId, baseline))
                    return ProvisioningResult.Fail(ProvisionFailure.TargetSatisfied(
                        $"tracked #{trackedId} already re-observed (focus ({focus.Q},{focus.R}), baseline turn {baseline})"));
                if (executionHex.Equals(focus))
                    return ProvisioningResult.Fail(ProvisionFailure.AssemblyInfeasible(
                        "surveil ExecutionHex == FocusHex — invariant violation"));
                if (HexGridMath.Distance(executionHex, focus) > exec.Army.EffectiveVisionRadius)
                    return ProvisioningResult.Fail(ProvisionFailure.NoExecutableStep(
                        $"mover #{moverArmyId} vision {exec.Army.EffectiveVisionRadius} no longer covers focus from vantage"));
                if (ScoutExecutionSafety.VantageBlockedNow(player, executionHex, ctx.TurnNumber,
                        target.Stealth == StealthRequirement.Required || target.DetectionRisk > 0f))
                    return ProvisioningResult.Fail(ProvisionFailure.NoExecutableStep(
                        $"vantage ({executionHex.Q},{executionHex.R}) is now occupied by a current force / foreign building"));
            }
            else if (refresh)
            {
                // A Refresh target was selected because frozen IntelAge was stale. Previously
                // Visited ground remains valid; only a NEW current observation completes it.
                if (ScoutObjectiveEvaluator.IsRefreshSatisfiedLive(player, focus))
                    return ProvisioningResult.Fail(ProvisionFailure.TargetSatisfied(
                        $"refresh focus ({focus.Q},{focus.R}) is already visible again"));
                if (AiMapMemory.KnownEnemySightingAt(player, focus).HasValue)
                    return ProvisioningResult.Fail(ProvisionFailure.TargetInvalidated(
                        $"refresh focus ({focus.Q},{focus.R}) now holds a known army"));
            }
            else
            {
                if (VisionSystem.IsVisited(player, focus))
                    return ProvisioningResult.Fail(ProvisionFailure.TargetSatisfied(
                        $"focus ({focus.Q},{focus.R}) already visited — nothing left to discover there"));
                if (AiMapMemory.KnownEnemySightingAt(player, focus).HasValue)
                    return ProvisioningResult.Fail(ProvisionFailure.TargetInvalidated(
                        $"focus ({focus.Q},{focus.R}) now holds a known army"));
            }

            // Task 2 — preflight approach + AP purely off PRE-mutation data: the real mover for a
            // normal candidate, or the not-yet-extracted unit's own stats/hex for a garrison one
            // (the exact same coordinate-based primitive BuildCandidates already used to admit this
            // candidate — see ReconAssignmentPlanner.BuildCandidates' garrison-extraction branch).
            HexCoord fromHex = exec.RequiresGarrisonExtraction ? garrisonArmy.Hex : army.Hex;
            // FIX-07 — ScoutExecutionSafety now admits a vantage on a foreign structure that
            // knowledge says nobody is holding, so this preflight must ask the same question or
            // it would strand exactly the vantage the selector just approved.
            bool hasSafeApproach = exec.RequiresGarrisonExtraction
                ? SafeStepPathing.FindSafePath(ctx.Map, player, fromHex, executionHex,
                    plannedExtractUnit.MoveMax) != null
                : SafeStepPathing.FindNextSafeStep(ctx.Map, army, executionHex) != null;
            if (!hasSafeApproach)
                return ProvisioningResult.Fail(ProvisionFailure.NoExecutableStep(
                    $"no safe first step from ({fromHex.Q},{fromHex.R}) toward ({executionHex.Q},{executionHex.R})"));

            int activationAp = exec.RequiresGarrisonExtraction
                ? plannedExtractUnit.ActivationApCost
                : (army.HasActivatedThisTurn ? 0 : army.ActivationApCost);
            bool alreadyHidden = exec.RequiresGarrisonExtraction
                ? plannedExtractUnit.IsHidden
                : army.Members.Any(mem => mem.IsHidden);
            bool reserveStealth = target.Stealth == StealthRequirement.Required && !alreadyHidden;
            int stealthAp = reserveStealth ? StealthTransitionApCost : 0;
            float realNeed = activationAp + stealthAp;

            float eps = AiConfigV2.allocatorSliceEpsilon;
            float envelope = funded.Tentative.Ap;
            if (realNeed > envelope + eps)
                return ProvisioningResult.Fail(ProvisionFailure.EnvelopeTooSmall(realNeed,
                    $"mover #{moverArmyId} needs {N(realNeed)} AP, envelope is {N(envelope)}"));

            float turnApLeft = root.ActionPoints - session.ApClaimed;
            if (realNeed > turnApLeft + eps)
                return ProvisioningResult.Fail(ProvisionFailure.MoverContended(
                    $"turn AP exhausted: need {N(realNeed)}, {N(turnApLeft)} left after earlier claims"));

            // Task 2 — COMMIT. Only now, after every check above ran on pre-mutation data, does the
            // garrison Recce actually leave. `extractedUnit` tracks the EXACT UnitData that moved so
            // FailAfterRecce can send that same unit back rather than re-deriving "the" Recce from
            // the shell's roster (a Host-style shared container could, in principle, hold more than
            // one candidate unit — this project's Shell-tier extraction never does today, but the
            // tracked reference removes the ambiguity at the source instead of relying on that).
            UnitData extractedUnit = null;
            if (exec.RequiresGarrisonExtraction)
            {
                if (!ArmyActions.TransferMember(plannedExtractUnit, garrisonArmy, destinationShell, ctx.HexSelection, out string why))
                {
                    return ProvisioningResult.Fail(ProvisionFailure.MoverContended(
                        $"garrison #{exec.SourceGarrisonArmyId} extraction into shell #{exec.MaterializationArmyId} failed: {why}"));
                }
                extractedUnit = plannedExtractUnit;
                army = destinationShell;
                AiDebugLog.Write($"[AI][V2][Recon] extracted idle Recce {plannedExtractUnit.Name} "
                    + $"from garrison #{garrisonArmy.Id} into #{destinationShell.Id} for scouting duty");
            }

            // Every failure branch from here on must go through FailAfterRecce (mirrors Economy's
            // FailAfterHero) so a future check inserted below this line can never forget to unwind
            // the extraction and leave the Recce permanently stranded outside its garrison.
            ProvisioningResult FailAfterRecce(ProvisionFailure failure)
            {
                if (extractedUnit == null)
                    return ProvisioningResult.Fail(failure);
                bool rolledBack = ArmyActions.TransferMember(
                    extractedUnit, destinationShell, garrisonArmy, ctx.HexSelection, out string why);
                if (!rolledBack)
                    AiDebugLog.Write($"[AI][V2][Recon] garrison extraction rollback FAILED "
                        + $"#{destinationShell.Id}->#{garrisonArmy.Id}: {why}");
                bool stateChanged = !rolledBack;
                return ProvisioningResult.Fail(failure, stateChanged, stateChanged ? 1 : 0);
            }

            // Task 2 — the one genuine post-commit check TransferMember's own validation does not
            // already cover end-to-end: the resulting shell must actually BE a usable solo Recce. A
            // real (non-garrison) mover repeats the same cheap structural predicate already proven
            // above (harmless — nothing mutated it in between); a freshly extracted garrison Recce
            // rolls back through FailAfterRecce instead of leaving a phantom army downstream stages
            // were never told to expect.
            if (army == null || army.Owner != player || army.Members.Count == 0
                || !AiArmyRoles.IsSoloRecce(army) || army.CurrentMovement <= 0)
                return FailAfterRecce(ProvisionFailure.MoverContended(
                    $"assigned mover #{moverArmyId} is no longer a usable solo Recce"));

            return ProvisioningResult.Ok(new ProvisionedMission
            {
                Mission = m,
                Key = key,
                Kind = MissionKind.Scout,
                ScoutKind = target.Kind,
                MoverArmyId = moverArmyId,
                FocusHex = focus,
                ExecutionHex = executionHex,
                TrackedArmyId = surveil ? target.Contact.Army.ArmyId : (int?)null,
                BaselineObservedTurn = surveil ? target.Contact.LastObservedTurn : 0,
                ClaimedAp = realNeed,
                ClaimedPhysical = funded.PhysicalDraw,
                StealthApReserved = stealthAp > 0,
                RequiresStealth = target.Stealth == StealthRequirement.Required || target.DetectionRisk > 0f,
            }, extractedUnit != null ? 1 : 0);
        }

        internal static bool IsEligibleEconomyRecoveryActor(
            MissionProposal mission, ArmySnapshot actor)
        {
            if (mission == null || actor == null || mission.Kind != MissionKind.Economy
                || !(mission.Target is EconomyMissionTarget target)
                || target.Kind != EconomyTaskKind.ReturnBuilder
                || !mission.PreferredMoverArmyId.HasValue)
                return false;
            return actor.ArmyId == mission.PreferredMoverArmyId.Value
                && (!target.BuilderArmyId.HasValue
                    || actor.ArmyId == target.BuilderArmyId.Value)
                && actor.HasHero && !actor.IsPrison && !actor.IsAir && !actor.IsAirfield;
        }

        // Reuses the EXACT Economy garrison-extraction solver/claim fields and the canonical
        // SafeStepPathing route. No new mover, reservation ledger or actor assignment manager.
        private static ProvisioningResult ProvisionDevelopment(PlayerSetupData player, PlayerRoot root,
            AiTurnContext ctx, ProvisioningSession session, FundedEntry funded,
            DevelopmentMissionTarget target)
        {
            MissionProposal mission = funded.Mission;
            if (target.Hero == null || target.Hero.Owner != player || !target.Hero.IsHero
                || target.Hero.IsPrisoner || !target.Hero.HasAbility(
                    ResearchProductionSystem.RoleAbility(target.Mode))
                || !string.Equals(target.HeroKey, GenerationSource.StableHeroKey(target.Hero),
                    StringComparison.Ordinal))
                return ProvisioningResult.Fail(ProvisionFailure.TargetInvalidated(
                    "bound Development hero lost or no longer qualified"));

            BuildingData facility = BuildingRegistry.FindAt(target.FacilityHex);
            if (facility == null || facility.Owner != player
                || !facility.HasFacilityWithAbility(ResearchProductionSystem.FacilityAbility(target.Mode)))
                return ProvisioningResult.Fail(ProvisionFailure.TargetInvalidated(
                    "bound Development facility missing"));
            if (BattleInitiator.FindEnemyAt(target.FacilityHex, player) != null)
                return ProvisioningResult.Fail(ProvisionFailure.NoExecutableStep(
                    "Development facility temporarily contested"));

            ArmyData army = ArmyRegistry.AllForOwner(player).FirstOrDefault(a => a != null
                && !a.IsPrison && a.Members.Contains(target.Hero));
            if (army == null || army.Owner != player)
                return ProvisioningResult.Fail(ProvisionFailure.NoMoverExists(
                    "bound Development hero no longer belongs to a live own army"));
            if (ResearchProductionSystem.ActorStillQualifies(player, target.Hero,
                    target.FacilityHex, target.Mode))
                return ProvisioningResult.Fail(ProvisionFailure.TargetSatisfied(
                    "exact Development hero arrived and can operate the facility"));
            if (army.Hex.Equals(target.FacilityHex))
                return ProvisioningResult.Fail(ProvisionFailure.TargetInvalidated(
                    "hero reached facility but cannot qualify for production"));
            if (mission.FromDurableIntent && mission.PreferredMoverArmyId.HasValue
                && army.Id != mission.PreferredMoverArmyId.Value)
                return ProvisioningResult.Fail(ProvisionFailure.TargetInvalidated(
                    "durable Development actor changed outside its bound extraction"));

            List<MissionIntent> intents = MissionIntentRegistry.GetOrCreate(player).All
                .Where(i => i != null && i.Status == IntentStatus.Active).ToList();
            MissionIntentKey ownKey = MissionIntentKey.For(mission);
            if (intents.Any(i => !i.IntentKey.Equals(ownKey)
                    && (i.PreferredMoverArmyId == army.Id
                        || i.Development?.Hero == target.Hero)))
                return ProvisioningResult.Fail(ProvisionFailure.MoverContended(
                    "hero is committed to another active mission"));
            ActorCommitments claims = ActorCommitments.FromIntents(intents, session.Snapshot, null);
            if ((claims.IsArmyClaimed(army.Id)
                    && !intents.Any(i => i.IntentKey.Equals(ownKey)
                        && i.PreferredMoverArmyId == army.Id))
                || session.ClaimedArmyIds.Contains(army.Id)
                || DemandLayer.EconomyBuilderUnderImmediateThreat(session.Snapshot, army.Hex))
                return ProvisioningResult.Fail(ProvisionFailure.MoverContended(
                    "Development actor contested, threatened or already claimed"));

            bool extract = army.IsGarrison;
            if (!extract && (!AiArmyRoles.IsHeroLed(army)
                    || army.Members.Count(u => ReferenceEquals(u, target.Hero)) != 1))
                return ProvisioningResult.Fail(ProvisionFailure.AssemblyInfeasible(
                    "Development needs a unique qualified hero in a mobile ground army"));
            // Live source protection mirrors the preparation witness and never steals an
            // operator from a different working factory.
            BuildingData source = BuildingRegistry.FindAt(army.Hex);
            if (source != null && source.Owner == player
                && new[] { ResearchProductionMode.Research, ResearchProductionMode.Production }
                    .Any(mode => source.HasFacilityWithAbility(
                        ResearchProductionSystem.FacilityAbility(mode))
                        && ResearchProductionSystem.ActorStillQualifies(player,
                            target.Hero, army.Hex, mode)))
                return ProvisioningResult.Fail(ProvisionFailure.MoverContended(
                    "hero is operating another working facility"));

            int travelCost = extract
                ? SafeStepPathing.FindSafePathCost(ctx.Map, player, army.Hex,
                    target.FacilityHex, target.Hero.MoveMax)
                : SafeStepPathing.FindSafePathCost(ctx.Map, army, target.FacilityHex);
            if (travelCost == int.MaxValue)
                return ProvisioningResult.Fail(ProvisionFailure.DestinationUnreachable(
                    "no legal safe route to the Development facility"));

            GarrisonExtractionCandidate extraction = default;
            float requiredAp;
            if (extract)
            {
                extraction = ResolveGarrisonExtractionCandidate(player, army, claims,
                    session, root, funded.Tentative.Ap, target.Hero, target.Mode);
                if (extraction.Tier == GarrisonExtractionTier.None)
                    return ProvisioningResult.Fail(ProvisionFailure.NoExecutableStep(
                        extraction.Reason ?? "no legal extraction of the bound Development hero"));
                requiredAp = extraction.ApCost;
            }
            else
            {
                if (army.CurrentMovement <= 0)
                    return ProvisioningResult.Fail(ProvisionFailure.NoExecutableStep(
                        "Development mover has no movement this turn"));
                requiredAp = army.HasActivatedThisTurn ? 0f : army.ActivationApCost;
            }
            if (requiredAp > funded.Tentative.Ap + AiConfigV2.allocatorSliceEpsilon)
                return ProvisioningResult.Fail(ProvisionFailure.EnvelopeTooSmall(requiredAp,
                    "Development transport AP envelope below extraction/activation"));
            if (requiredAp > root.ActionPoints - session.ApClaimed + AiConfigV2.allocatorSliceEpsilon)
                return ProvisioningResult.Fail(ProvisionFailure.MoverContended(
                    "Development transport cannot spend shared AP pool"));

            var provisioned = new ProvisionedMission
            {
                Mission = mission, Kind = MissionKind.Development,
                Key = StableMissionKey.For(mission), DevelopmentTarget = target,
                MoverArmyId = extract ? SyntheticGarrisonExtractionActorId(army.Id) : army.Id,
                FocusHex = target.FacilityHex, ExecutionHex = army.Hex,
                EconomyExtractionGarrisonArmyId = extract ? army.Id : -1,
                EconomyExtractionPlan = extraction, ClaimedAp = requiredAp,
            };
            AiDebugLog.Write($"[AI][V2][Development] bound hero={target.HeroKey} "
                + $"actor=#{army.Id} site=({target.FacilityHex.Q},{target.FacilityHex.R}) "
                + $"extract={extract} route={travelCost} ap={requiredAp:0.##}");
            return ProvisioningResult.Ok(provisioned);
        }

        private static ProvisioningResult ProvisionEconomy(PlayerSetupData player, PlayerRoot root,
            AiHandData hand, AiTurnContext ctx, ProvisioningSession session, FundedEntry funded,
            EconomyMissionTarget target)
        {
            MissionProposal m = funded.Mission;
            StableMissionKey key = StableMissionKey.For(m);
            if (target.Kind == EconomyTaskKind.MobileCollection
                || target.Kind == EconomyTaskKind.ReturnCollector)
                return ProvisionMobileCollection(player, root, ctx, session, funded, target, key);
            if (target.Kind == EconomyTaskKind.ReturnBuilder)
                return ProvisionEconomyRecovery(player, root, ctx, session, funded, target, key);
            if (MissionOutcomeLedger.EconomyObjectiveSatisfied(player, target))
                return ProvisioningResult.Fail(ProvisionFailure.TargetSatisfied("economy target already built"));
            if (target.Kind == EconomyTaskKind.FoundBase
                && (target.BuildCard == null || hand?.Hand == null || !hand.Hand.Contains(target.BuildCard)))
                return ProvisioningResult.Fail(ProvisionFailure.TargetInvalidated("founding card no longer in hand"));

            List<MissionIntent> standingIntents = MissionIntentRegistry.GetOrCreate(player).All
                .Where(i => i != null && i.Status == IntentStatus.Active).ToList();
            MissionIntentKey currentIntentKey = MissionIntentKey.For(m);
            ActorCommitments actorCommitments = ActorCommitments.FromIntents(
                standingIntents, session.Snapshot, null);
            IReadOnlyList<DemandLayer.EconomyBuilderChoice> rankedBuilders =
                DemandLayer.RankEconomyBuilders(session.Snapshot, target.TargetHex,
                    target.BuilderRoutes, standingIntents, actorCommitments,
                    target.BuildValue, target.BuildApCost,
                    includeReturn: target.Kind == EconomyTaskKind.BuildExtraction);
            IEnumerable<DemandLayer.EconomyBuilderChoice> eligibleBuilders = rankedBuilders;
            if (m.FromDurableIntent && m.PreferredMoverArmyId.HasValue)
                eligibleBuilders = eligibleBuilders.Where(
                    x => x.Route.ArmyId == m.PreferredMoverArmyId.Value);

            // DIAGNOSTIC (kept for FoundBase only, per project owner's request 2026-09-13 — the
            // BuildExtraction stuck-builder case is resolved and no longer needs this) — traces
            // which single eligibility clause below rejects a durable intent's committed mover,
            // since the FirstOrDefault predicate normally swallows all of them into one
            // MoverContended result.
            if (target.Kind == EconomyTaskKind.FoundBase
                && m.FromDurableIntent && m.PreferredMoverArmyId.HasValue)
            {
                int preferredId = m.PreferredMoverArmyId.Value;
                var eligibleList = eligibleBuilders.ToList();
                eligibleBuilders = eligibleList;
                bool inRankedAtAll = rankedBuilders.Any(x => x.Route.ArmyId == preferredId);
                AiDebugLog.Write($"[AI][V2][Economy][TRACE] {player?.Nickname} durable mover #{preferredId} "
                    + $"target=({target.TargetHex.Q},{target.TargetHex.R}) turn={session.Snapshot?.TurnNumber} — "
                    + $"inRankedBuilders={inRankedAtAll} rankedBuildersTotal={rankedBuilders.Count} "
                    + $"eligibleAfterMoverFilter={eligibleList.Count}");
                if (!inRankedAtAll)
                {
                    // Never reached EconomyBuilderCandidates' yield at all — replicate its gates
                    // here (read-only, does not touch the real generator) to see which one ate it.
                    ArmySnapshot snapArmy = session.Snapshot?.Self?.Armies
                        ?.FirstOrDefault(a => a != null && a.ArmyId == preferredId);
                    if (snapArmy == null)
                    {
                        AiDebugLog.Write($"[AI][V2][Economy][TRACE]   #{preferredId} upstream — "
                            + "not found in WorldSnapshot.Self.Armies (destroyed/merged/not owned this turn?)");
                    }
                    else
                    {
                        MissionIntent upstreamAssignment = standingIntents.FirstOrDefault(
                            i => i != null && i.Status == IntentStatus.Active
                            && i.PreferredMoverArmyId == preferredId);
                        bool economyTargetMismatch = upstreamAssignment != null
                            && upstreamAssignment.Kind == MissionKind.Economy
                            && (upstreamAssignment.Economy == null
                                || !upstreamAssignment.Economy.TargetHex.Equals(target.TargetHex));
                        bool nonEconomyDonorBlock = upstreamAssignment != null
                            && upstreamAssignment.Kind != MissionKind.Economy
                            && !DemandLayer.EconomyDonorStructurallyEligible(upstreamAssignment);
                        bool claimedUpstream = actorCommitments != null
                            && actorCommitments.IsArmyClaimed(preferredId);
                        AiDebugLog.Write($"[AI][V2][Economy][TRACE]   #{preferredId} upstream "
                            + $"(EconomyBuilderCandidates-equivalent) — hex=({snapArmy.Hex.Q},{snapArmy.Hex.R}) "
                            + $"isMobileEconomyBuilder={snapArmy.IsMobileEconomyBuilder} "
                            + $"assignment={(upstreamAssignment == null ? "none" : $"{upstreamAssignment.Kind}/{upstreamAssignment.IntentKey} status={upstreamAssignment.Status}")} "
                            + $"economyTargetMismatch={economyTargetMismatch} nonEconomyDonorBlock={nonEconomyDonorBlock} "
                            + $"claimedUpstream={claimedUpstream} "
                            + $"underImmediateThreat={DemandLayer.EconomyBuilderUnderImmediateThreat(session.Snapshot, snapArmy.Hex)}");
                    }
                }
                foreach (DemandLayer.EconomyBuilderChoice x in eligibleList)
                {
                    ArmyData a = ResolveArmy(player, x.Route.ArmyId);
                    if (a == null)
                    {
                        AiDebugLog.Write($"[AI][V2][Economy][TRACE]   #{x.Route.ArmyId} — ResolveArmy returned null");
                        continue;
                    }
                    bool cMobile = IsMobileEconomyHero(a, player);
                    bool cThreat = !DemandLayer.EconomyBuilderUnderImmediateThreat(session.Snapshot, a.Hex);
                    bool cClaimed = !session.ClaimedArmyIds.Contains(a.Id);
                    MissionIntent conflicting = standingIntents.FirstOrDefault(i => i.PreferredMoverArmyId == a.Id
                        && !i.IntentKey.Equals(currentIntentKey)
                        && !DemandLayer.EconomyDonorStructurallyEligible(i));
                    bool cDonorConflict = conflicting == null;
                    bool atTarget = a.Hex.Equals(target.TargetHex);
                    HexCoord? nextStep = atTarget
                        ? (HexCoord?)null
                        : SafeStepPathing.FindNextSafeStep(ctx.Map, a, target.TargetHex);
                    bool cPath = atTarget || (a.CurrentMovement > 0 && nextStep.HasValue);
                    AiDebugLog.Write($"[AI][V2][Economy][TRACE]   #{a.Id} hex=({a.Hex.Q},{a.Hex.R}) "
                        + $"currentMovement={a.CurrentMovement} maxMovement={a.MaxMovement} "
                        + $"isMobileEconomyHero={cMobile} notUnderImmediateThreat={cThreat} "
                        + $"notClaimedThisPass={cClaimed} noConflictingIntent={cDonorConflict}"
                        + (conflicting != null ? $" (conflictsWith={conflicting.IntentKey} kind={conflicting.Kind} status={conflicting.Status})" : "")
                        + $" atTargetHex={atTarget} hasSafeNextStep={(atTarget ? (object)"n/a" : nextStep.HasValue)} "
                        + $"=> ELIGIBLE={cMobile && cThreat && cClaimed && cDonorConflict && cPath}");
                }
            }

            bool IsCandidateEligible(DemandLayer.EconomyBuilderChoice x)
            {
                if (x.Route.RequiresGarrisonExtraction)
                {
                    // ArmyId here names the Garrison, not yet a separate mover — re-derive the
                    // exact same candidate AiArmyRoles.BestSparableEconomyHero would give
                    // Analysis right now (canonical, same predicate as CanSpareGarrisonMember),
                    // never trusting a hero identity carried across from an earlier phase.
                    ArmyData g = ResolveArmy(player, x.Route.ArmyId);
                    UnitData sparable = g == null ? null
                        : AiArmyRoles.BestSparableEconomyHero(player, g);
                    return g != null && sparable != null
                        && !DemandLayer.EconomyBuilderUnderImmediateThreat(session.Snapshot, g.Hex)
                        && !session.ClaimedArmyIds.Contains(g.Id)
                        && (g.Hex.Equals(target.TargetHex)
                            || SafeStepPathing.FindSafePathCost(
                                ctx.Map, player, g.Hex, target.TargetHex, sparable.MoveMax)
                                != int.MaxValue);
                }
                ArmyData a = ResolveArmy(player, x.Route.ArmyId);
                return a != null && IsMobileEconomyHero(a, player)
                && !DemandLayer.EconomyBuilderUnderImmediateThreat(session.Snapshot, a.Hex)
                && !session.ClaimedArmyIds.Contains(a.Id)
                && !standingIntents.Any(i => i.PreferredMoverArmyId == a.Id
                    && !i.IntentKey.Equals(currentIntentKey)
                    && !DemandLayer.EconomyDonorStructurallyEligible(i))
                && (a.Hex.Equals(target.TargetHex)
                    || (a.CurrentMovement > 0
                        && SafeStepPathing.FindNextSafeStep(
                            ctx.Map, a, target.TargetHex).HasValue));
            }

            // Fallthrough fix (2026-09-13, found via the stuck-builder TRACE below): a
            // garrison-extraction candidate can pass IsCandidateEligible (a sparable hero exists)
            // yet still fail to materialize into an actual mover, because
            // TryExtractGarrisonHeroForEconomy separately needs a free reusable army shell
            // (ReusableArmySelector.FindReusableAt) at that hex — a resource this predicate never
            // checks. The old code picked exactly one candidate (FirstOrDefault) and gave up the
            // whole provisioning attempt if THAT ONE couldn't materialize, even when other
            // eligible, non-garrison candidates were sitting right there in the same ranked list.
            // Walk the ranked list in order and keep trying until one actually produces a hero.
            // 2026-09-14 review round 4 — garrison extraction no longer mutates here at all. This
            // loop either finds a hero that ALREADY exists as a real field mover (direct-army
            // candidates, unchanged from before) or, for a garrison candidate, a viable
            // GarrisonExtractionCandidate PLAN plus a conservative pre-mutation cost estimate —
            // never both an army and a plan. The plan's real materialization
            // (ArmyActions.CreateArmy/TransferMember) happens later, in
            // TaskExecutor.MaterializeEconomyGarrisonBuilder, inside that step's own
            // beforeStep/afterStep window — see the synthetic-MoverArmyId return further down.
            DemandLayer.EconomyBuilderChoice builderChoice = null;
            ArmyData hero = null;
            ArmyData deferredGarrison = null;
            GarrisonExtractionCandidate deferredPlan = default;
            EconomyCompletionPlan deferredPreparation = default;
            float ecoApEnvelopeRemaining = funded.Tentative.Ap;
            float rawApRemaining = root.ActionPoints - session.ApClaimed;
            float eps = AiConfigV2.allocatorSliceEpsilon;
            // P0-2, AI V2 economy audit 2026-09-21 — the cheapest garrison-extraction cost seen
            // across every candidate that was structurally legal but rejected only for exceeding
            // the AP envelope/pool. If the loop ends with no builder AND this is set, the real
            // failure is a funding shortfall, not "no legal way to get a builder" — the caller
            // below reports EnvelopeTooSmall(requiredAp) instead of a generic NoMoverExists.
            float? economyBuilderShortfallAp = null;
            void TrackEconomyShortfall(float requiredAp) => economyBuilderShortfallAp =
                economyBuilderShortfallAp.HasValue
                    ? Mathf.Min(economyBuilderShortfallAp.Value, requiredAp) : requiredAp;
            foreach (DemandLayer.EconomyBuilderChoice candidate in eligibleBuilders
                .OrderBy(x => m.PreferredMoverArmyId == x.Route.ArmyId ? 0 : 1)
                .Where(IsCandidateEligible))
            {
                if (candidate.Route.RequiresGarrisonExtraction)
                {
                    ArmyData candidateGarrison = ResolveArmy(player, candidate.Route.ArmyId);
                    if (candidateGarrison == null)
                        continue;
                    GarrisonExtractionCandidate plan = ResolveGarrisonExtractionCandidate(
                        player, candidateGarrison, actorCommitments, session,
                        root, ecoApEnvelopeRemaining);
                    if (plan.Tier == GarrisonExtractionTier.None)
                    {
                        // plan.ApCost is 0f only for a true non-existence (no sparable hero); a
                        // positive value here is the cheapest tier's real cost, rejected purely for
                        // exceeding ecoApEnvelopeRemaining (see ResolveGarrisonExtractionCandidate).
                        if (plan.ApCost > 0f)
                            TrackEconomyShortfall(plan.ApCost);
                        continue;
                    }
                    // 2026-09-14 review round 10 (P1) — cheap pre-check before paying for a full
                    // composition search: ONLY the one cost that is unconditionally real regardless
                    // of hex/turn specifics (creating the container, or the hero's own late-join
                    // charge into an already-activated Shell/Host). The old estimate also added
                    // Hero.ActivationApCost and the build/followup cost unconditionally — both can
                    // be zero in the real plan (no travel needed, or the build can't complete this
                    // stage), so that estimate was not a true lower bound and could reject a
                    // genuinely affordable candidate before PlanEconomyCompletion ever got to look.
                    float roughEstimate = plan.ApCost;
                    if (roughEstimate > ecoApEnvelopeRemaining + eps
                        || roughEstimate > rawApRemaining + eps)
                    {
                        // Passed ResolveGarrisonExtractionCandidate's own envelope check but not the
                        // rawApRemaining one (session.ApClaimed by other missions this same pass,
                        // which the resolver cannot see) — still a real funding shortfall, not a
                        // structural impossibility.
                        TrackEconomyShortfall(roughEstimate);
                        continue;
                    }

                    // 2026-09-14 review round 8 (P0) — compute and PIN the FULL preparation plan
                    // (composition, donor, authoritative AP, resource stage cost) here, against a
                    // read-only preview of the not-yet-real container (BuildGarrisonExtractionPreview)
                    // — the SAME PlanEconomyCompletion the direct-army path uses below, so Execution
                    // never re-plans, only re-validates this exact decision and applies it.
                    // `plan.ApCost` is passed as `alreadyCommittedApCost` (round 10, P0) so the
                    // feasibility checks inside see the budget correctly reduced by the extraction
                    // cost this candidate will ALSO have to pay — see that parameter's own comment.
                    ArmyData preview = BuildGarrisonExtractionPreview(player, candidateGarrison, plan);
                    int identityArmyId = plan.Container?.Id ?? -1;
                    EconomyCompletionPlan prep = PlanEconomyCompletion(player, root, ctx,
                        session.Snapshot, standingIntents, key, target, candidate, preview,
                        identityArmyId, ecoApEnvelopeRemaining, rawApRemaining, plan.ApCost);
                    if (!prep.Feasible)
                        continue;

                    deferredGarrison = candidateGarrison;
                    deferredPlan = plan;
                    deferredPreparation = prep;
                    builderChoice = candidate;
                    break;
                }
                ArmyData a = ResolveArmy(player, candidate.Route.ArmyId);
                if (a == null)
                    continue;
                builderChoice = candidate;
                hero = a;
                break;
            }

            if (deferredGarrison != null)
            {
                // Preflight the canonical Continuity site lease before spending a builder
                // extraction AP or reserving completion resources for an incompatible project.
                int candidateBuilder = deferredPlan.Container?.Id ?? -1;
                if (!MissionContinuityLayer.CanGrantEconomyBuildSite(player, target.TargetHex,
                        currentIntentKey, candidateBuilder, target.BuildCard))
                    return ProvisioningResult.Fail(ProvisionFailure.MoverContended(
                        "economy build site already leased by another active project"));

                // 2026-09-14 review round 10 (P1) — reserve the physical build-stage resources NOW,
                // at the moment this mission commits to being deferred, not only after it
                // materializes in Execution. Until this round `ClaimedPhysical`/`ReservationOwner`
                // stayed default (zero/null) for the whole deferred window, so a SECOND Economy
                // mission provisioned later in the same batch pass could see the pool as still fully
                // free and claim the exact same resources StrategicSpendability.FitsSpendableResources
                // had already approved for this one. A later stale/failure in Execution already
                // routes through the existing ReleaseEconomyReservation — no new release lifecycle
                // needed, it just needs ReservationOwner to actually be set from here on.
                if (deferredPreparation.CompletionThisTurn)
                    InfrastructureFulfillment.ReserveEconomyCost(player, ctx.TurnNumber,
                        deferredPreparation.OwnerKey, target.BuildResourceCost, target.BuildApCost);

                // Nothing mutated — transferredMemberCount 0 is honest (ProvisioningResult.Ok's own
                // "changed = transferredMemberCount > 0" rule). MoverArmyId is a synthetic negative
                // id, same pattern ScoutExecutorKind.AirLaunch already uses for an actor that does
                // not exist yet; TaskExecutor.RunEconomyStep detects EconomyExtractionGarrisonArmyId
                // >= 0 and materializes for real before doing anything else.
                return ProvisioningResult.Ok(new ProvisionedMission
                {
                    Mission = m, Key = key, Kind = MissionKind.Economy,
                    MoverArmyId = SyntheticGarrisonExtractionActorId(deferredGarrison.Id),
                    EconomyExtractionGarrisonArmyId = deferredGarrison.Id,
                    EconomyExtractionPlan = deferredPlan,
                    EconomyExtractionPreparation = deferredPreparation,
                    EconomyPreparationPending = true,
                    FocusHex = target.TargetHex, ExecutionHex = deferredGarrison.Hex,
                    EconomyTarget = target,
                    // 2026-09-14 review round 10 (P0) — ClaimedAp is the TOTAL funded amount now:
                    // deferredPlan.ApCost (CreateArmy, or the hero's own late-join charge into an
                    // already-activated Shell/Host) was previously dropped entirely from this figure,
                    // so a Create-tier extraction's own 2 AP silently vanished from the envelope
                    // Execution re-validates against — see PlanEconomyCompletion's own comment on
                    // `alreadyCommittedApCost` for why that AP is real and separate from RealAp.
                    ClaimedAp = deferredPlan.ApCost + deferredPreparation.RealAp,
                    ClaimedPhysical = CostVector(deferredPreparation.StageCost),
                    ReservationOwner = deferredPreparation.OwnerKey,
                });
            }

            if (hero == null)
            {
                if (m.FromDurableIntent && m.PreferredMoverArmyId.HasValue)
                {
                    int preferredId = m.PreferredMoverArmyId.Value;
                    ArmyData preferredArmy = ResolveArmy(player, preferredId);

                    if (preferredArmy == null)
                        return ProvisioningResult.Fail(ProvisionFailure.TargetInvalidated(
                            $"committed economy builder #{preferredId} no longer exists"));

                    // Canonical, army-specific safe-route witness (same contract EconomyBuilderRoutes
                    // uses at Analysis time — MaxMovement, not this turn's remaining CurrentMovement,
                    // so a merely-spent-for-now mover is never misclassified as unreachable). When
                    // this is int.MaxValue the committed mover has no safe path at all right now, as
                    // distinct from "a path exists but this mover didn't clear the other eligibility
                    // checks this turn" — the latter stays MoverContended below, unchanged.
                    int routeCost = SafeStepPathing.FindSafePathCost(
                        ctx.Map, preferredArmy, target.TargetHex);
                    if (routeCost == int.MaxValue)
                        return ProvisioningResult.Fail(ProvisionFailure.NoExecutableStep(
                            $"committed economy builder #{preferredId} has no safe route to the site right now"));

                    return ProvisioningResult.Fail(ProvisionFailure.MoverContended(
                        $"committed economy builder #{preferredId} cannot advance this turn"));
                }
                // DIAGNOSTIC (kept for FoundBase only, per project owner's request 2026-09-13 — the
                // BuildExtraction stuck-builder case is resolved and no longer needs this) — the
                // durable-mover trace above never fires here: this is reached only once a fresh
                // Economy mission's FirstOrDefault predicate rejected every ranked candidate (or
                // rankedBuilders was empty to begin with). Replicate that exact predicate per
                // candidate, read-only, so the single clause eating each one is visible.
                if (target.Kind == EconomyTaskKind.FoundBase)
                {
                    AiDebugLog.Write($"[AI][V2][Economy][TRACE] {player?.Nickname} fresh economy mission "
                        + $"kind={target.Kind} target=({target.TargetHex.Q},{target.TargetHex.R}) "
                        + $"turn={session.Snapshot?.TurnNumber} — rankedBuildersTotal={rankedBuilders.Count}");
                    foreach (DemandLayer.EconomyBuilderChoice x in rankedBuilders)
                    {
                        if (x.Route.RequiresGarrisonExtraction)
                        {
                            ArmyData g = ResolveArmy(player, x.Route.ArmyId);
                            UnitData sparable = g == null ? null
                                : AiArmyRoles.BestSparableEconomyHero(player, g);
                            bool cThreatG = g != null
                                && !DemandLayer.EconomyBuilderUnderImmediateThreat(session.Snapshot, g.Hex);
                            bool cClaimedG = g != null && !session.ClaimedArmyIds.Contains(g.Id);
                            bool cPathG = g != null && sparable != null && (g.Hex.Equals(target.TargetHex)
                                || SafeStepPathing.FindSafePathCost(
                                    ctx.Map, player, g.Hex, target.TargetHex, sparable.MoveMax) != int.MaxValue);
                            // 2026-09-14 review round 2 — the trace used to keep its own copy of the
                            // container search (which shell/host/create tier would apply), and that
                            // copy drifted out of sync with the real one more than once. It now
                            // calls the SAME pure resolver the real extraction path
                            // (ResolveGarrisonExtractionCandidate + ApplyGarrisonExtraction) uses —
                            // single owner, no second implementation to keep in sync.
                            GarrisonExtractionCandidate containerPlan = g == null
                                ? GarrisonExtractionCandidate.No("garrison not resolved")
                                : ResolveGarrisonExtractionCandidate(player, g, actorCommitments,
                                    session, root, funded.Tentative.Ap);
                            bool cContainerG = containerPlan.Tier != GarrisonExtractionTier.None;
                            bool shallowEligibleG = g != null && sparable != null && cThreatG
                                && cClaimedG && cPathG && cContainerG;
                            // 2026-09-15 — the shallow gates above (mirrored from the real loop's
                            // IsCandidateEligible + ResolveGarrisonExtractionCandidate) are NOT the
                            // whole real gate any more: since review round 8/9/10 the real loop also
                            // runs the roughEstimate AP pre-check and the full PlanEconomyCompletion
                            // (donor loan, real path for the PREVIEW army, composition/lightening,
                            // authoritative AP, StrategicSpendability) before accepting a candidate —
                            // see line ~1122-1141 above. "ELIGIBLE=True" here used to mean nothing
                            // beyond "a container tier exists", which is exactly the stale-diagnostic
                            // trap this same file's history (see docs/ai-economy-mover-materialization-
                            // decision-tree.md, "Known trap") already burned us on once. Replicate the
                            // SAME two downstream checks, read-only, so the real rejection reason is
                            // visible instead of falling through to the generic NoMoverExists below.
                            string prepDetailG = "n/a";
                            bool prepFeasibleG = false;
                            if (shallowEligibleG)
                            {
                                float roughEstimateG = containerPlan.ApCost;
                                if (roughEstimateG > ecoApEnvelopeRemaining + eps
                                    || roughEstimateG > rawApRemaining + eps)
                                {
                                    prepDetailG = $"RoughEstimateTooBig ap={roughEstimateG:0.##} "
                                        + $"ecoEnvelope={ecoApEnvelopeRemaining:0.##} rawPool={rawApRemaining:0.##}";
                                }
                                else
                                {
                                    ArmyData previewG = BuildGarrisonExtractionPreview(player, g, containerPlan);
                                    int identityArmyIdG = containerPlan.Container?.Id ?? -1;
                                    EconomyCompletionPlan prepG = PlanEconomyCompletion(player, root, ctx,
                                        session.Snapshot, standingIntents, key, target, x, previewG,
                                        identityArmyIdG, ecoApEnvelopeRemaining, rawApRemaining, containerPlan.ApCost);
                                    prepFeasibleG = prepG.Feasible;
                                    prepDetailG = prepG.Feasible
                                        ? $"Feasible realAp={prepG.RealAp:0.##} completionThisTurn={prepG.CompletionThisTurn}"
                                        : $"{prepG.Failure.Kind} — {prepG.Failure.Detail}";
                                }
                            }
                            AiDebugLog.Write($"[AI][V2][Economy][TRACE]   #{x.Route.ArmyId} (garrison-extraction) "
                                + $"resolved={g != null} sparableHero={sparable != null} "
                                + $"notUnderImmediateThreat={cThreatG} notClaimedThisPass={cClaimedG} hasPath={cPathG} "
                                + $"container={(cContainerG ? containerPlan.Tier.ToString() : containerPlan.Reason)} "
                                + (cContainerG ? $"containerApCost={containerPlan.ApCost:0.##} " : "")
                                + $"shallowEligible={shallowEligibleG} plan=[{prepDetailG}] "
                                + $"=> ELIGIBLE={shallowEligibleG && prepFeasibleG}");
                            continue;
                        }
                        ArmyData a = ResolveArmy(player, x.Route.ArmyId);
                        if (a == null)
                        {
                            AiDebugLog.Write($"[AI][V2][Economy][TRACE]   #{x.Route.ArmyId} — ResolveArmy returned null");
                            continue;
                        }
                        bool cMobile = IsMobileEconomyHero(a, player);
                        bool cThreat = !DemandLayer.EconomyBuilderUnderImmediateThreat(session.Snapshot, a.Hex);
                        bool cClaimed = !session.ClaimedArmyIds.Contains(a.Id);
                        MissionIntent conflicting = standingIntents.FirstOrDefault(i => i.PreferredMoverArmyId == a.Id
                            && !i.IntentKey.Equals(currentIntentKey)
                            && !DemandLayer.EconomyDonorStructurallyEligible(i));
                        bool cDonorConflict = conflicting == null;
                        bool atTarget = a.Hex.Equals(target.TargetHex);
                        HexCoord? nextStep = atTarget
                            ? (HexCoord?)null
                            : SafeStepPathing.FindNextSafeStep(ctx.Map, a, target.TargetHex);
                        bool cPath = atTarget || (a.CurrentMovement > 0 && nextStep.HasValue);
                        bool shallowEligible = cMobile && cThreat && cClaimed && cDonorConflict && cPath;
                        // 2026-09-15 — same reasoning as the garrison-extraction branch above: the
                        // shallow checks here are only a mirror of IsCandidateEligible, not of the
                        // full PlanEconomyCompletion the real loop (line ~1305) runs against this
                        // exact army once selected. Replicate that final gate too so a direct-army
                        // candidate that looks ELIGIBLE here but fails on donor loan / AP / spendable
                        // resources shows its real reason instead of the generic NoMoverExists below.
                        string prepDetail = "n/a";
                        bool prepFeasible = false;
                        if (shallowEligible)
                        {
                            EconomyCompletionPlan prep = PlanEconomyCompletion(player, root, ctx,
                                session.Snapshot, standingIntents, key, target, x, a, a.Id,
                                ecoApEnvelopeRemaining, rawApRemaining);
                            prepFeasible = prep.Feasible;
                            prepDetail = prep.Feasible
                                ? $"Feasible realAp={prep.RealAp:0.##} completionThisTurn={prep.CompletionThisTurn}"
                                : $"{prep.Failure.Kind} — {prep.Failure.Detail}";
                        }
                        AiDebugLog.Write($"[AI][V2][Economy][TRACE]   #{a.Id} hex=({a.Hex.Q},{a.Hex.R}) "
                            + $"currentMovement={a.CurrentMovement} maxMovement={a.MaxMovement} "
                            + $"isMobileEconomyHero={cMobile} notUnderImmediateThreat={cThreat} "
                            + $"notClaimedThisPass={cClaimed} noConflictingIntent={cDonorConflict}"
                            + (conflicting != null ? $" (conflictsWith={conflicting.IntentKey} kind={conflicting.Kind} status={conflicting.Status})" : "")
                            + $" atTargetHex={atTarget} hasSafeNextStep={(atTarget ? (object)"n/a" : nextStep.HasValue)} "
                            + $"shallowEligible={shallowEligible} plan=[{prepDetail}] "
                            + $"=> ELIGIBLE={shallowEligible && prepFeasible}");
                    }
                }
                // P0-2 — a structurally legal container existed (Shell/Host/Create) for at least one
                // candidate this pass and was rejected only for exceeding the AP envelope/pool: that
                // is a repriceable funding shortfall, not a genuine absence of any way to get a
                // builder. RepriceThisTurn lets the allocator fund it properly instead of the
                // candidate quietly retrying next turn under RetryNextTurn with no larger envelope.
                if (economyBuilderShortfallAp.HasValue)
                    return ProvisioningResult.Fail(ProvisionFailure.EnvelopeTooSmall(
                        economyBuilderShortfallAp.Value,
                        $"garrison-extraction builder needs {economyBuilderShortfallAp.Value:0.##} AP, "
                        + "envelope/pool too small"));
                return ProvisioningResult.Fail(ProvisionFailure.NoMoverExists(
                    "no free hero can advance toward economy site"));
            }

            // 2026-09-14 review round 10 (P0) — the direct-army path (hero already a real, live
            // field army) no longer applies its own composition change inside Provisioning either:
            // PlanEconomyCompletion (pure) decides, and the SAME Execution apply path the
            // garrison-extraction candidate already uses (TaskExecutor.ApplyEconomyPreparation)
            // commits it — no live-army mutation happens inside Provisioning for ANY Economy actor
            // any more, extracted or not. `identityArmyId = hero.Id` since the hero is already real.
            EconomyCompletionPlan directPrep = PlanEconomyCompletion(player, root, ctx,
                session.Snapshot, standingIntents, key, target, builderChoice, hero, hero.Id,
                funded.Tentative.Ap, root.ActionPoints - session.ApClaimed);
            if (!directPrep.Feasible)
                return ProvisioningResult.Fail(directPrep.Failure);

            if (!MissionContinuityLayer.CanGrantEconomyBuildSite(player, target.TargetHex,
                    currentIntentKey, hero.Id, target.BuildCard))
                return ProvisioningResult.Fail(ProvisionFailure.MoverContended(
                    "economy build site already leased by another active project"));

            // Reserve the physical stage cost NOW (same cross-mission-visibility reasoning as the
            // deferred garrison-extraction branch above) — whether or not composition/donor work is
            // still pending, this mission has committed to this build.
            if (directPrep.CompletionThisTurn)
                InfrastructureFulfillment.ReserveEconomyCost(player, ctx.TurnNumber,
                    directPrep.OwnerKey, target.BuildResourceCost, target.BuildApCost);
            else
            {
                // AI economy commitment/recovery audit (2026-09-15) — this hero cannot finish the
                // build this turn, so it is genuinely a multi-turn delivery starting or continuing.
                // Give Continuity a durable identity for it (mirrors the Hero-materialization path
                // in CapabilityDeliveryEvaluator) so StrategicPhaseA's protectedActiveEconomyBuild
                // protects the full H/E/M/T vector every later turn regardless of remaining travel —
                // without this, InfrastructureFulfillment.ShouldReserveDeferredEconomyResources'
                // one-turn horizon would have to (and used to) protect unconditionally on every turn
                // of the walk, freezing resources far earlier than necessary on the very first turn.
                MissionIntent delivery = MissionContinuityLayer.BeginEconomyDelivery(player, new AxisDemand
                {
                    RequestingAxis = DesireAxis.Economy,
                    Capability = target.Kind == EconomyTaskKind.FoundBase
                        ? CapabilityKind.EconomicExpansionBase : CapabilityKind.EconomicInfrastructure,
                    TargetHex = target.TargetHex,
                    EconomyResourceType = target.ResourceType,
                    EconomyBuildCard = target.BuildCard,
                    EconomyBuildResourceCost = target.BuildResourceCost,
                    EconomyBuildApCost = target.BuildApCost,
                    MinimumFollowupAp = target.MinimumFollowupAp,
                    EconomySiteValue = target.BuildValue,
                }, hero.Id, ctx.TurnNumber);
                if (delivery == null)
                    return ProvisioningResult.Fail(ProvisionFailure.MoverContended(
                        "economy build site lease changed during provisioning"));
            }

            // "Pending" means Execution still has real work to do before movement: an actual
            // composition change, OR a donor loan that must be suspended (bookkeeping only, but
            // still not something Provisioning may do — see PlanEconomyCompletion's own comment on
            // why it stays read-only). When neither applies the hero's roster is already exactly
            // right and there is nothing to defer — this mission proceeds straight to movement
            // exactly as it always has, no extra admission pass spent on a step that would mutate
            // nothing.
            bool preparationPending = directPrep.Donor != null
                || directPrep.Unload.Count > 0 || directPrep.Reinforcement.Count > 0;

            return ProvisioningResult.Ok(new ProvisionedMission
            {
                Mission = m, Key = key, Kind = MissionKind.Economy,
                MoverArmyId = hero.Id, FocusHex = target.TargetHex,
                ExecutionHex = target.TargetHex, EconomyTarget = target,
                EconomyPreparationPending = preparationPending,
                EconomyExtractionPreparation = directPrep,
                ClaimedAp = directPrep.RealAp,
                ClaimedPhysical = CostVector(directPrep.StageCost),
                ReservationOwner = directPrep.OwnerKey,
            });
        }

        // 2026-09-14 review round 8 (P0) — the pure DECISION half of what FinishEconomyBuilder used
        // to compute inline: donor loan, route, lightening/reinforcement composition, AP/resource
        // feasibility. Split out so Provisioning can compute and PIN this exact decision against a
        // read-only preview (see BuildGarrisonExtractionPreview) for a deferred garrison-extraction
        // candidate — Execution then only re-validates the volatile parts (AP, resources) and
        // APPLIES the pinned Unload/Reinforcement, never re-deriving them. The direct-army path
        // (FinishEconomyBuilder, below) calls this with the REAL live hero — identical behaviour to
        // before this split, since every read here (Hex/Members/MaxMovement/CurrentMovement/
        // HasActivatedThisTurn) is satisfied the same way by a real ArmyData or by the preview.
        // `identityArmyId` is `hero.Id` for the direct-army path; for a preview it is the REAL
        // container id when one already exists (Shell/Host) or -1 for Create (nothing could already
        // be bound to an army that does not exist yet).
        internal readonly struct EconomyCompletionPlan
        {
            public readonly bool Feasible;
            public readonly ProvisionFailure Failure;
            public readonly MissionIntent Donor;
            public readonly ArmyData Garrison;
            public readonly List<UnitData> Unload;
            public readonly List<UnitData> Reinforcement;
            public readonly bool TravelNeeded;
            public readonly bool CompletionThisTurn;
            public readonly ResourceCost StageCost;
            public readonly float RealAp;
            public readonly string OwnerKey;

            private EconomyCompletionPlan(bool feasible, ProvisionFailure failure,
                MissionIntent donor, ArmyData garrison, List<UnitData> unload,
                List<UnitData> reinforcement, bool travelNeeded, bool completionThisTurn,
                ResourceCost stageCost, float realAp, string ownerKey)
            {
                Feasible = feasible; Failure = failure; Donor = donor; Garrison = garrison;
                Unload = unload; Reinforcement = reinforcement; TravelNeeded = travelNeeded;
                CompletionThisTurn = completionThisTurn; StageCost = stageCost; RealAp = realAp;
                OwnerKey = ownerKey;
            }

            public static EconomyCompletionPlan No(ProvisionFailure failure) =>
                new EconomyCompletionPlan(false, failure, null, null, null, null,
                    false, false, null, 0f, null);
            public static EconomyCompletionPlan Yes(MissionIntent donor, ArmyData garrison,
                List<UnitData> unload, List<UnitData> reinforcement, bool travelNeeded,
                bool completionThisTurn, ResourceCost stageCost, float realAp, string ownerKey) =>
                new EconomyCompletionPlan(true, default, donor, garrison, unload, reinforcement,
                    travelNeeded, completionThisTurn, stageCost, realAp, ownerKey);
        }

        // 2026-09-14 review round 10 (P0) — `alreadyCommittedApCost` is the extraction AP a deferred
        // garrison-extraction candidate's OWN GarrisonExtractionCandidate.ApCost already accounts
        // for (CreateArmy, or a hero joining an already-activated Shell/Host) — a cost
        // EconomyMissionClaimedAp (inside this function) has no way to know about, since it only
        // ever reads the projected roster's own ActivationApCost sum, never a separate container-
        // creation/late-join charge. Subtracted from the envelope/pool BEFORE any feasibility check
        // here, so a candidate whose extraction cost alone would blow the budget is correctly
        // rejected (rather than accepted on a budget that silently excluded a cost the caller must
        // still pay). The direct-army path (hero already real, no extraction) passes the default 0.
        internal static EconomyCompletionPlan PlanEconomyCompletion(PlayerSetupData player,
            PlayerRoot root, AiTurnContext ctx, WorldSnapshot snapshot,
            IReadOnlyList<MissionIntent> standingIntents, StableMissionKey key,
            EconomyMissionTarget target, DemandLayer.EconomyBuilderChoice builderChoice,
            ArmyData hero, int identityArmyId, float apEnvelope, float apPoolRemaining,
            float alreadyCommittedApCost = 0f)
        {
            apEnvelope -= alreadyCommittedApCost;
            apPoolRemaining -= alreadyCommittedApCost;
            float eps = AiConfigV2.allocatorSliceEpsilon;
            MissionIntent donor = standingIntents.FirstOrDefault(i => i != null
                && i.Kind != MissionKind.Economy && i.PreferredMoverArmyId == identityArmyId
                && DemandLayer.EconomyDonorStructurallyEligible(i));
            int distance = SafeStepPathing.FindSafePathCost(ctx.Map, hero, target.TargetHex);
            if (distance == int.MaxValue)
                return EconomyCompletionPlan.No(ProvisionFailure.NoExecutableStep("no safe economy route"));
            if (donor != null)
            {
                // Provisioning validates LIVE path/MP against the SAME Demand-owned loan
                // predicate. Reprice the already selected builder's projected roster from
                // current physical facts; never resurrect the old raw-hex distance scorer.
                EconomyBuilderRouteSnapshot liveRoute = builderChoice.Route;
                liveRoute.TravelCost = distance;
                liveRoute.CurrentMovement = hero.CurrentMovement;
                liveRoute.HasActivatedThisTurn = hero.HasActivatedThisTurn;
                var liveChoice = new DemandLayer.EconomyBuilderChoice
                {
                    Route = liveRoute,
                    TotalAssignmentApCost = DemandLayer.EstimateEconomyAssignmentAp(
                        liveRoute, target.BuildApCost,
                        target.Kind == EconomyTaskKind.BuildExtraction),
                };
                if (!DemandLayer.EconomyLoanAllowed(donor, target.BuildValue,
                        liveChoice, target.BuildApCost, out float loanNet))
                    return EconomyCompletionPlan.No(ProvisionFailure.MoverContended(
                        $"loan rejected donor={donor.IntentKey} distance={distance} move={hero.CurrentMovement} net={loanNet:0.##}"));
            }

            bool travelNeeded = !hero.Hex.Equals(target.TargetHex);
            bool completionThisTurn = distance <= hero.CurrentMovement;
            ResourceCost stageCost = completionThisTurn ? target.BuildResourceCost : null;
            List<UnitData> lighteningPlan = PlanEconomyArmyLightening(
                player, hero, identityArmyId, target.TargetHex, snapshot, ctx,
                builderChoice.MinimumEscortCount, out ArmyData garrison,
                out List<UnitData> reinforcementPlan);
            if (builderChoice.Suitability == DemandLayer.EconomyArmySuitability.ReinforceAtBase
                && reinforcementPlan.Count == 0)
                return EconomyCompletionPlan.No(ProvisionFailure.AssemblyInfeasible(
                    $"economy builder #{identityArmyId} no longer has its planned minimum escort"));
            float realAp = EconomyMissionClaimedAp(hero, target.BuildApCost,
                target.MinimumFollowupAp, lighteningPlan, reinforcementPlan,
                garrison, travelNeeded, completionThisTurn);
            if (realAp > apEnvelope + eps)
                return EconomyCompletionPlan.No(ProvisionFailure.EnvelopeTooSmall(realAp,
                    $"economy hero #{identityArmyId} needs {realAp:0.##} AP for "
                    + (completionThisTurn ? "delivery + completion" : "this travel stage")));
            if (realAp > apPoolRemaining + eps)
                return EconomyCompletionPlan.No(ProvisionFailure.MoverContended("economy AP no longer available"));

            string owner = EconomyMissionPlanner.OwnerKey(key);
            if (!StrategicSpendability.FitsSpendableResources(player, root, ctx, stageCost, owner))
                return EconomyCompletionPlan.No(ProvisionFailure.EnvelopeTooSmall(
                    new ProvisionRequirement(realAp, CostVector(stageCost)),
                    "economy completion resources no longer spendable"));

            return EconomyCompletionPlan.Yes(donor, garrison, lighteningPlan, reinforcementPlan,
                travelNeeded, completionThisTurn, stageCost, realAp, owner);
        }

        // Economy-specific, same-hex preparation belongs here because the target and its route are
        // already selected. The canonical atomic batch transfer guarantees an all-or-nothing
        // roster change; a failed preflight simply leaves the original army usable.
        internal static int TryLightenEconomyArmy(PlayerSetupData player, ArmyData builder,
            HexCoord target, WorldSnapshot snapshot, AiTurnContext ctx)
            => TryLightenEconomyArmy(player, builder, target, snapshot, ctx,
                minimumEscort: 0);

        internal static int TryLightenEconomyArmy(PlayerSetupData player, ArmyData builder,
            HexCoord target, WorldSnapshot snapshot, AiTurnContext ctx, int minimumEscort)
        {
            List<UnitData> plan = PlanEconomyArmyLightening(
                player, builder, target, snapshot, ctx, minimumEscort,
                out ArmyData garrison, out List<UnitData> reinforcement);
            return ApplyEconomyArmyLightening(
                builder, garrison, plan, reinforcement, ctx);
        }

        private static List<UnitData> PlanEconomyArmyLightening(PlayerSetupData player,
            ArmyData builder, HexCoord target, WorldSnapshot snapshot, AiTurnContext ctx,
            int minimumEscort, out ArmyData garrison,
            out List<UnitData> reinforcement)
            => PlanEconomyArmyLightening(player, builder, builder?.Id ?? -1, target, snapshot, ctx,
                minimumEscort, out garrison, out reinforcement);

        // 2026-09-14 review round 8 (P0) — `identityArmyId` decouples the loan-protection intent
        // check from `builder.Id` so a Provisioning-time READ-ONLY PREVIEW of a not-yet-real
        // garrison-extraction container (ArmyData.CreateVisualSnapshot(), Id always -1) can still be
        // checked against the REAL container id it stands in for when one already exists (Shell/Host
        // tier) — using the preview's own -1 id would silently skip this protection for those tiers.
        // Both existing callers (the overload above, TryLightenEconomyArmy) keep passing `builder.Id`
        // — unchanged behaviour for every live-army caller.
        private static List<UnitData> PlanEconomyArmyLightening(PlayerSetupData player,
            ArmyData builder, int identityArmyId, HexCoord target, WorldSnapshot snapshot,
            AiTurnContext ctx, int minimumEscort, out ArmyData garrison,
            out List<UnitData> reinforcement)
        {
            garrison = null;
            reinforcement = new List<UnitData>();
            var unload = new List<UnitData>();
            if (player == null || builder == null || ctx == null || builder.IsGarrison
                || builder.IsPrison || builder.IsAirfield || builder.IsAirArmy
                || !builder.Members.Any(u => u != null && u.IsHero))
                return unload;
            // A loan may temporarily redirect an Explore/early-Raid actor, but it must not also
            // rewrite that durable mission's roster behind Continuity's back. Hard Defence,
            // Surveil and started Raid are already excluded at assignment; this preserves the
            // remaining loanable obligations as well.
            if (MissionIntentRegistry.GetOrCreate(player).All.Any(i => i != null
                && i.Status == IntentStatus.Active && i.Kind != MissionKind.Economy
                && i.PreferredMoverArmyId == identityArmyId))
                return unload;
            BuildingData home = BuildingRegistry.FindAt(builder.Hex);
            bool isCitadel = player.CitadelHexQ == builder.Hex.Q
                && player.CitadelHexR == builder.Hex.R;
            if ((home == null || home.Owner != player || (!home.IsBase && !isCitadel)))
                return unload;
            garrison = ArmyRegistry.FindGarrisonAt(builder.Hex, player);
            if (garrison == null || garrison == builder
                || garrison.HasActivatedThisTurn)
                return unload;

            List<UnitData> bodies = builder.Members
                .Where(u => u != null && !u.IsHero && !u.IsAviation)
                .OrderByDescending(u => AiPower.ToPowerUnit(u).BasePower)
                .ThenBy(u => u.Name).ToList();
            HexPath escortRoute = SafeStepPathing.FindSafePath(
                ctx.Map, player, builder.Hex, target, builder.MaxMovement);
            // No route means no reliable exposure witness. Keep the live roster intact instead
            // of substituting endpoints and potentially unloading the escort for an unreachable
            // operation. The caller may retry after the map/known blockers change.
            if (escortRoute == null)
                return unload;
            IReadOnlyList<AiMapMemory.KnownEnemySighting> threats =
                WorldAnalysis.KnownThreatsAffectingEconomyRoute(
                    snapshot, escortRoute.Hexes);
            IReadOnlyList<UnitData> retained = SelectEconomyEscort(
                builder, bodies, threats, minimumEscort);
            if (retained != null)
            {
                var keep = new HashSet<UnitData>(retained);
                unload.AddRange(bodies.Where(u => !keep.Contains(u))
                    .OrderByDescending(u => u.ActivationApCost)
                    .ThenBy(u => AiPower.ToPowerUnit(u).BasePower)
                    .ThenBy(u => u.Name));
                while (unload.Count > 0
                       && !ArmyActions.CanTransferMembers(
                           unload, builder, garrison, out _))
                    unload.RemoveAt(unload.Count - 1);
                return unload;
            }

            // The field roster is deficient. At a Base/Citadel add only the smallest garrison
            // subset that makes the whole hero-led formation safe; do not add surplus beyond it.
            List<UnitData> reserve = garrison.Members
                .Where(u => u != null && !u.IsHero && !u.IsAviation)
                .OrderBy(u => u.ActivationApCost)
                .ThenByDescending(u => AiPower.ToPowerUnit(u).BasePower)
                .ThenBy(u => u.Name).ToList();
            for (int count = 1; count <= reserve.Count; count++)
                foreach (List<UnitData> subset in Combinations(reserve, count))
                {
                    var projected = bodies.Concat(subset)
                        .Select(WorthIt.FromLiveUnit).ToList();
                    if (!DemandLayer.EconomyRosterSafe(
                            projected, threats, minimumEscort))
                        continue;
                    if (!ArmyActions.CanTransferMembers(
                            subset, garrison, builder, out _))
                        continue;
                    reinforcement.AddRange(subset);
                    return unload;
                }
            return unload;
        }

        internal static int ApplyEconomyArmyLightening(ArmyData builder,
            ArmyData garrison, IReadOnlyList<UnitData> unload,
            IReadOnlyList<UnitData> reinforcement, AiTurnContext ctx)
        {
            if (builder == null || garrison == null)
                return 0;
            bool unloadApplied = false;
            if (unload.Count > 0)
            {
                if (!ArmyActions.TransferMembersAtomic(
                        unload, builder, garrison, ctx.HexSelection, out string whyUnload))
                    return 0;
                unloadApplied = true;
            }
            if (reinforcement.Count > 0 && !ArmyActions.TransferMembersAtomic(
                    reinforcement, garrison, builder, ctx.HexSelection, out string whyAdd))
            {
                // 2026-09-14 review round 6 (P0) — unload+reinforce is ONE canonical composition
                // change, not two independent ones: a reinforcement failure must not leave an
                // already-applied unload silently uncommitted-for (caller reported failure while the
                // unload stayed real). The current planner never actually produces both non-empty at
                // once, but this must not depend on that as an unstated invariant. Roll the unload
                // back so this call is honestly all-or-nothing.
                if (unloadApplied && !ArmyActions.TransferMembersAtomic(
                        unload, garrison, builder, ctx.HexSelection, out string whyRollback))
                    AiDebugLog.Write($"[AI][V2][Economy][WARN] lighten rollback failed for builder "
                        + $"#{builder.Id} after a reinforce failure: {whyRollback} — roster left "
                        + "partially unloaded");
                return 0;
            }
            int changed = unload.Count + reinforcement.Count;
            if (changed > 0)
                AiDebugLog.Write($"[AI][V2][Economy] builder #{builder.Id} prepared "
                    + $"unload={unload.Count} add={reinforcement.Count} "
                    + $"garrison=#{garrison.Id} power={AiPower.EffectiveArmyPower(builder.Members):0.##}");
            return changed;
        }

        // Composition selection only; combat truth remains WorthIt (coverage + full-roster Monte
        // Carlo) and tie quality remains AiPower. The smallest safe body count wins. If the memory
        // lacks per-unit profiles, null deliberately refuses lightening rather than inventing a
        // third Economy strength surrogate.
        internal static IReadOnlyList<UnitData> SelectEconomyEscort(ArmyData builder,
            IReadOnlyList<UnitData> bodies,
            IReadOnlyList<AiMapMemory.KnownEnemySighting> threats)
            => SelectEconomyEscort(builder, bodies, threats, minimumEscort: 0);

        internal static IReadOnlyList<UnitData> SelectEconomyEscort(ArmyData builder,
            IReadOnlyList<UnitData> bodies,
            IReadOnlyList<AiMapMemory.KnownEnemySighting> threats, int minimumEscort)
        {
            if (builder == null)
                return null;
            if (threats == null || threats.Count == 0)
                return (bodies ?? System.Array.Empty<UnitData>())
                    .Where(u => u != null)
                    .OrderBy(u => u.ActivationApCost)
                    .ThenByDescending(u => u.MoveMax)
                    .ThenBy(u => u.Name)
                    .Take(Mathf.Max(0, minimumEscort)).ToList();
            if (threats.Any(t => t.Defenders == null || t.Defenders.Count == 0))
                return null;

            List<UnitData> pool = (bodies ?? System.Array.Empty<UnitData>())
                .Where(u => u != null).Distinct().ToList();
            for (int count = Mathf.Max(0, minimumEscort); count <= pool.Count; count++)
            {
                List<UnitData> best = null;
                int bestAp = int.MaxValue;
                int bestMove = int.MinValue;
                float bestPower = float.MinValue;
                foreach (List<UnitData> subset in Combinations(pool, count))
                {
                    // Heroes travel with the builder but do not participate in ground combat;
                    // WorthIt's ArmyData overloads apply the same non-hero boundary.
                    var roster = subset.Select(WorthIt.FromLiveUnit).ToList();
                    bool safe = DemandLayer.EconomyRosterSafe(
                        roster, threats, minimumEscort);
                    if (!safe)
                        continue;
                    int ap = subset.Sum(u => u.ActivationApCost);
                    int move = subset.Count == 0 ? builder.MaxMovement
                        : subset.Min(u => u.MoveMax);
                    float power = AiPower.EffectiveArmyPower(subset);
                    if (best == null || ap < bestAp
                        || (ap == bestAp && move > bestMove)
                        || (ap == bestAp && move == bestMove
                            && power > bestPower + AiConfigV2.allocatorSliceEpsilon))
                    {
                        best = subset;
                        bestAp = ap;
                        bestMove = move;
                        bestPower = power;
                    }
                }
                if (best != null)
                    return best;
            }
            return null;
        }

        private static IEnumerable<List<UnitData>> Combinations(
            IReadOnlyList<UnitData> source, int count, int start = 0,
            List<UnitData> prefix = null)
        {
            prefix ??= new List<UnitData>();
            if (prefix.Count == count)
            {
                yield return new List<UnitData>(prefix);
                yield break;
            }
            for (int i = start; i <= source.Count - (count - prefix.Count); i++)
            {
                prefix.Add(source[i]);
                foreach (List<UnitData> result in Combinations(source, count, i + 1, prefix))
                    yield return result;
                prefix.RemoveAt(prefix.Count - 1);
            }
        }

        internal static float EconomyMissionClaimedAp(ArmyData builder, float buildApCost,
            float minimumFollowupAp, IReadOnlyCollection<UnitData> unloaded) =>
            EconomyMissionClaimedAp(builder, buildApCost, minimumFollowupAp, unloaded,
                added: null, unloadTarget: null, travelNeeded: true, completionThisTurn: true);

        internal static float EconomyMissionClaimedAp(ArmyData builder, float buildApCost,
            float minimumFollowupAp, IReadOnlyCollection<UnitData> unloaded,
            bool travelNeeded, bool completionThisTurn)
            => EconomyMissionClaimedAp(builder, buildApCost, minimumFollowupAp,
                unloaded, added: null, unloadTarget: null,
                travelNeeded: travelNeeded, completionThisTurn: completionThisTurn);

        internal static float EconomyMissionClaimedAp(ArmyData builder, float buildApCost,
            float minimumFollowupAp, IReadOnlyCollection<UnitData> unloaded,
            IReadOnlyCollection<UnitData> added, ArmyData unloadTarget,
            bool travelNeeded, bool completionThisTurn)
        {
            float immediateTransfers = ArmyActions.TransferMembersApCost(added, builder)
                + ArmyActions.TransferMembersApCost(unloaded, unloadTarget);
            float activation = 0f;
            if (travelNeeded && builder != null && !builder.HasActivatedThisTurn)
                activation = builder.Members
                    .Where(u => u != null && (unloaded == null || !unloaded.Contains(u)))
                    .Concat(added ?? System.Array.Empty<UnitData>())
                    .Distinct().Sum(u => u.ActivationApCost);
            float completion = completionThisTurn
                ? Mathf.Max(buildApCost, minimumFollowupAp) : 0f;
            return immediateTransfers + activation + completion;
        }

        private static ProvisioningResult ProvisionEconomyRecovery(
            PlayerSetupData player, PlayerRoot root, AiTurnContext ctx,
            ProvisioningSession session, FundedEntry funded,
            EconomyMissionTarget target, StableMissionKey key)
        {
            MissionProposal mission = funded.Mission;
            int? preferredId = mission.PreferredMoverArmyId ?? target.BuilderArmyId;
            if (!preferredId.HasValue)
                return ProvisioningResult.Fail(ProvisionFailure.NoMoverExists(
                    "return-builder mission has no preferred actor"));

            ArmySnapshot actorSnapshot = session.Snapshot?.Self?.Armies?
                .FirstOrDefault(a => a != null && a.ArmyId == preferredId.Value);
            if (!IsEligibleEconomyRecoveryActor(mission, actorSnapshot))
                return ProvisioningResult.Fail(ProvisionFailure.MoverContended(
                    $"preferred return builder #{preferredId.Value} is no longer eligible"));

            ArmyData actor = ResolveArmy(player, preferredId.Value);
            if (actor == null || actor.Owner != player
                || !actor.Members.Any(u => u != null && u.IsHero))
                return ProvisioningResult.Fail(ProvisionFailure.MoverContended(
                    $"preferred return builder #{preferredId.Value} no longer exists"));
            BuildingData shelter = BuildingRegistry.FindAt(target.TargetHex);
            bool isOwnCitadel = player.CitadelHexQ == target.TargetHex.Q
                && player.CitadelHexR == target.TargetHex.R;
            if (shelter == null || shelter.Owner != player
                || (!shelter.IsBase && !isOwnCitadel))
                return ProvisioningResult.Fail(ProvisionFailure.TargetInvalidated(
                    $"recovery shelter ({target.TargetHex.Q},{target.TargetHex.R}) is no longer protected"));
            if (actor.Hex.Equals(target.TargetHex))
                return ProvisioningResult.Fail(ProvisionFailure.TargetSatisfied(
                    $"return builder #{actor.Id} already protected"));
            if (actor.CurrentMovement <= 0)
                return ProvisioningResult.Fail(ProvisionFailure.NoExecutableStep(
                    $"return builder #{actor.Id} has no movement"));
            if (!SafeStepPathing.FindNextSafeStep(ctx.Map, actor, target.TargetHex).HasValue
                || SafeStepPathing.FindSafePathCost(ctx.Map, actor, target.TargetHex) == int.MaxValue)
                return ProvisioningResult.Fail(ProvisionFailure.NoExecutableStep(
                    $"no safe recovery route for builder #{actor.Id}"));

            float activation = actor.HasActivatedThisTurn ? 0f : actor.ActivationApCost;
            if (activation > funded.Tentative.Ap + AiConfigV2.allocatorSliceEpsilon)
                return ProvisioningResult.Fail(ProvisionFailure.EnvelopeTooSmall(activation,
                    $"return builder #{actor.Id} needs {activation:0.##} AP"));
            if (activation > root.ActionPoints - session.ApClaimed
                + AiConfigV2.allocatorSliceEpsilon)
                return ProvisioningResult.Fail(ProvisionFailure.MoverContended(
                    "recovery activation AP no longer available"));

            return ProvisioningResult.Ok(new ProvisionedMission
            {
                Mission = mission, Key = key, Kind = MissionKind.Economy,
                MoverArmyId = actor.Id, FocusHex = target.TargetHex,
                ExecutionHex = target.TargetHex, EconomyTarget = target,
                ClaimedAp = activation, ClaimedPhysical = ResourceVector.Zero,
                ReservationOwner = null,
            });
        }

        private static ProvisioningResult ProvisionMobileCollection(
            PlayerSetupData player, PlayerRoot root, AiTurnContext ctx,
            ProvisioningSession session, FundedEntry funded,
            EconomyMissionTarget target, StableMissionKey key)
        {
            MissionProposal mission = funded.Mission;
            int? actorId = mission.PreferredMoverArmyId ?? target.CollectorArmyId;
            if (!actorId.HasValue)
                return ProvisioningResult.Fail(ProvisionFailure.NoMoverExists(
                    "mobile collection mission has no pinned collector"));
            ArmySnapshot frozen = session.Snapshot?.Self?.Armies?.FirstOrDefault(a => a != null
                && a.ArmyId == actorId.Value);
            bool returning = target.Kind == EconomyTaskKind.ReturnCollector;
            if (frozen == null || frozen.IsAir || frozen.IsAirfield || frozen.IsGarrison
                || frozen.IsPrison || (!returning && (!target.ResourceType.HasValue
                    || frozen.CollectionCapacity.Get(target.ResourceType.Value) <= 0f)))
                return ProvisioningResult.Fail(ProvisionFailure.MoverContended(
                    $"collector #{actorId.Value} is no longer eligible"));
            ArmyData actor = ResolveArmy(player, actorId.Value);
            if (actor == null || actor.Owner != player || actor.IsAirArmy || actor.IsAirfield
                || actor.IsGarrison || actor.IsPrison)
                return ProvisioningResult.Fail(ProvisionFailure.MoverContended(
                    $"collector #{actorId.Value} no longer exists"));
            if (session.ClaimedArmyIds.Contains(actor.Id))
                return ProvisioningResult.Fail(ProvisionFailure.MoverContended(
                    $"collector #{actor.Id} already claimed this pass"));
            if (actor.Hex.Equals(target.TargetHex))
            {
                if (returning)
                    return ProvisioningResult.Fail(ProvisionFailure.TargetSatisfied(
                        $"collector #{actor.Id} already protected"));
                return ProvisioningResult.Ok(new ProvisionedMission
                {
                    Mission = mission, Key = key, Kind = MissionKind.Economy,
                    MoverArmyId = actor.Id, FocusHex = target.TargetHex,
                    ExecutionHex = target.TargetHex, EconomyTarget = target,
                    ClaimedAp = 0f, ClaimedPhysical = ResourceVector.Zero,
                });
            }
            if (actor.CurrentMovement <= 0
                || !SafeStepPathing.FindNextSafeStep(ctx.Map, actor, target.TargetHex).HasValue
                || SafeStepPathing.FindSafePathCost(ctx.Map, actor, target.TargetHex) == int.MaxValue)
                return ProvisioningResult.Fail(ProvisionFailure.NoExecutableStep(
                    $"collector #{actor.Id} has no safe executable step"));
            float activation = actor.HasActivatedThisTurn ? 0f : actor.ActivationApCost;
            if (activation > funded.Tentative.Ap + AiConfigV2.allocatorSliceEpsilon)
                return ProvisioningResult.Fail(ProvisionFailure.EnvelopeTooSmall(activation,
                    $"collector #{actor.Id} needs {activation:0.##} AP"));
            if (root == null || activation > root.ActionPoints - session.ApClaimed
                + AiConfigV2.allocatorSliceEpsilon)
                return ProvisioningResult.Fail(ProvisionFailure.MoverContended(
                    "collector activation AP no longer available"));
            return ProvisioningResult.Ok(new ProvisionedMission
            {
                Mission = mission, Key = key, Kind = MissionKind.Economy,
                MoverArmyId = actor.Id, FocusHex = target.TargetHex,
                ExecutionHex = target.TargetHex, EconomyTarget = target,
                ClaimedAp = activation, ClaimedPhysical = ResourceVector.Zero,
            });
        }

        internal static ResourceVector CostVector(ResourceCost cost) => cost == null
            ? ResourceVector.Zero
            : new ResourceVector(0f, cost.Get(ResourceType.Human), cost.Get(ResourceType.Energy),
                cost.Get(ResourceType.Materials), cost.Get(ResourceType.Tech));

        // RECON-AIR-01 (round 5) — claim the air actor/subset Assignment already picked, THROUGH
        // THE SAME generic funding/provisioning accounting Ground uses: the real AP/Energy Assignment
        // resolved for this exact actor/subset (ScoutExecutionCandidate.RequiredAp/RequiredEnergy —
        // see ReconAssignmentPlanner.AppendAirCandidates) is checked against the envelope Funding
        // granted (funded.Tentative.Ap / funded.PhysicalDraw.Energy) and, if it fits, claimed for
        // real — ClaimedAp/ClaimedEnergy are no longer hard-coded 0. If it does not fit, this returns
        // the ordinary EnvelopeTooSmall failure and lets the existing repack/reprice loop
        // (ResourceAllocator.RegisterProvisionFailure) handle it exactly like ground already does —
        // no separate air ledger. The terminal air execution stage still re-checks LIVE HARD gates
        // (AiAirSortiePlanner.CanAffordLaunch / CanIssueMoveNow / AA / safe return) against the
        // post-ground-movement world state before actually spending anything — but it no longer
        // re-runs any strategic hand/deck/income economics: that decision is made once, here, by
        // AirSortieReservationAdmission.
        private static ProvisioningResult ProvisionAir(PlayerSetupData player, PlayerRoot root, AiTurnContext ctx,
            ProvisioningSession session, FundedEntry funded, ScoutExecutionCandidate exec,
            ScoutMissionTarget target, StableMissionKey key)
        {
            MissionProposal m = funded.Mission;
            bool surveil = target.Kind == ScoutTargetKind.Surveil;
            HexCoord focus = target.FocusHex;
            HexCoord executionHex = exec.ExecutionHex;

            if (surveil)
            {
                int trackedId = target.Contact?.Army?.ArmyId ?? -1;
                if (trackedId < 0 || target.Contact.Source != ContactSource.Honest
                    || target.Contact.Knowledge != ContactKnowledge.LastKnown)
                    return ProvisioningResult.Fail(ProvisionFailure.TargetInvalidated(
                        "surveil target is no longer an honest last-known contact"));
                int baseline = target.Contact.LastObservedTurn;
                if (VisionSystem.IsVisible(player, focus) || HasFresherSighting(player, trackedId, baseline))
                    return ProvisioningResult.Fail(ProvisionFailure.TargetSatisfied(
                        $"tracked #{trackedId} already re-observed (focus ({focus.Q},{focus.R}), baseline turn {baseline})"));
            }
            else
            {
                // Air only serves Refresh in this round's scope (see AppendAirCandidates).
                if (ScoutObjectiveEvaluator.IsRefreshSatisfiedLive(player, focus))
                    return ProvisioningResult.Fail(ProvisionFailure.TargetSatisfied(
                        $"refresh focus ({focus.Q},{focus.R}) is already visible again"));
            }

            int moverArmyId;
            HexCoord airfieldHex = default;
            List<UnitData> launchSubset = null;

            if (exec.ExecutorKind == ScoutExecutorKind.AirExisting)
            {
                ArmyData wing = ResolveArmy(player, exec.Army.ArmyId);
                if (wing == null || wing.Owner != player || !AviationRules.IsValidAirArmy(wing)
                    || wing.CurrentMovement <= 0)
                    return ProvisioningResult.Fail(ProvisionFailure.MoverContended(
                        $"assigned air actor #{exec.Army.ArmyId} is no longer a usable air wing"));

                // Round 8 (Problem 1) — ProvisionAir validates the SAME two air-actor states the pool
                // ReconAssignmentPlanner.AssignFunded now offers (detail.AirborneWings first, then
                // ready spares); the old code accepted only the first and rejected every continuing
                // wing an incumbent ScoutIntent had just re-won a FRESH funded mission for, so the
                // continuation architecture was wired end to end but never executable. The two states
                // are EXPLICITLY MUTUALLY EXCLUSIVE — computed once from one live-sortie lookup, not
                // an airfield check in one branch and a registry check in the other:
                //   · ReadyAirExisting      = own airfield  + NO live sortie: about to start one.
                //   · ContinuingAirExisting = airborne + a LIVE Recon sortie + a durable
                //     ReconPatrolState + a non-null projected sortie state whose phase is not
                //     Return/Hold. It is mid-sortie by definition, so the ready-idle-wing shape is not
                //     demanded of it; it is rejected only when forced into recovery this turn (that
                //     lifecycle is Mandatory Flight Recovery's — ReconAirExecutor flies it
                //     unconditionally, outside funding — never strategic Recon progress). A null
                //     projected state is NOT a silent pass: no valid live Recon sortie => reject.
                bool onOwnAirfield = AviationRules.IsOwnedAirfieldAt(wing.Hex, player);
                AirSortie liveSortie = AirSortieRegistry.ForArmy(player, wing);
                bool hasPatrolState = ReconPatrolStateRegistry.TryGet(player, wing.Id, out _);

                bool ready = onOwnAirfield && liveSortie == null;
                bool continuing = !onOwnAirfield
                    && wing.Controller != null
                    && liveSortie != null
                    && liveSortie.Kind == AirSortieKind.Recon
                    && hasPatrolState;

                if (continuing)
                {
                    ReconAirSortieState projected = ReconAirReservationPrepass.ProjectScoringSortie(player, ctx, wing);
                    if (projected == null)
                        return ProvisioningResult.Fail(ProvisionFailure.MoverContended(
                            $"continuing air actor #{wing.Id} has no valid live Recon sortie state"));
                    if (projected.Phase == ReconAirPhase.Return || projected.Phase == ReconAirPhase.Hold)
                        return ProvisioningResult.Fail(ProvisionFailure.NoExecutableStep(
                            $"continuing air actor #{wing.Id} is Return/Hold-bound this turn (recovery, not fresh Recon progress)"));
                }
                else if (!ready)
                {
                    return ProvisioningResult.Fail(ProvisionFailure.MoverContended(
                        $"assigned air actor #{wing.Id} is neither a ready standalone wing nor a valid continuing Recon sortie"));
                }
                moverArmyId = wing.Id;
            }
            else // AirLaunch
            {
                ArmyData airfield = AviationRules.FindAirfieldAt(exec.AirfieldHex, player);
                if (airfield == null || exec.LaunchSubset == null || exec.LaunchSubset.Count == 0
                    || !AiAirSortiePlanner.CanAffordLaunch(root, player, exec.LaunchSubset))
                    return ProvisioningResult.Fail(ProvisionFailure.MoverContended(
                        $"assigned launch airfield ({exec.AirfieldHex.Q},{exec.AirfieldHex.R}) no longer has an affordable subset"));
                moverArmyId = exec.ActorKey;
                airfieldHex = exec.AirfieldHex;
                launchSubset = new List<UnitData>(exec.LaunchSubset);
            }

            // RECON-AIR-01 — the real, actor-specific cost Assignment already resolved for THIS
            // exact candidate (see AppendAirCandidates: a live Pick/PickFromStorage against the
            // bound mission target, not a generic "some useful step exists" probe). Compare against
            // the envelope Funding granted; claim for real only if it fits.
            float eps = AiConfigV2.allocatorSliceEpsilon;
            float realAp = exec.RequiredAp;
            float realEnergy = exec.RequiredEnergy;
            float apEnvelope = funded.Tentative.Ap;
            float energyEnvelope = funded.PhysicalDraw.Energy;
            if (realAp > apEnvelope + eps || realEnergy > energyEnvelope + eps)
                // Round 7 (Problem 3) — report BOTH the real AP and the real Energy this exact air
                // actor needs, not AP alone: an air launch's Energy shortfall must raise an Energy
                // floor too, so ResourceAllocator's next Pack() can actually fund it, instead of
                // repricing only the AP dimension and looping on the same Energy-starved envelope.
                return ProvisioningResult.Fail(ProvisionFailure.EnvelopeTooSmall(
                    new ProvisionRequirement(realAp, new ResourceVector(0f, 0f, realEnergy, 0f, 0f)),
                    $"air actor #{moverArmyId} needs {N(realAp)} AP / {N(realEnergy)} Energy, "
                    + $"envelope is {N(apEnvelope)} AP / {N(energyEnvelope)} Energy"));

            float turnApLeft = root.ActionPoints - session.ApClaimed;
            if (realAp > turnApLeft + eps)
                return ProvisioningResult.Fail(ProvisionFailure.MoverContended(
                    $"turn AP exhausted: air actor #{moverArmyId} needs {N(realAp)}, {N(turnApLeft)} left after earlier claims"));

            // RECON-AIR-03 — the cumulative, SEQUENTIAL check the funded-envelope comparison above
            // cannot provide on its own: two separate AirLaunch (or AirExisting) missions provisioned
            // one after another THIS pass both see the SAME unmutated root.Energy (Provisioning never
            // mutates world resources — only Execution does), so each could pass its OWN envelope
            // check independently while jointly exceeding the real stockpile. session.EnergyClaimed
            // accumulates every earlier real claim this pass, mirroring session.ApClaimed for AP.
            float liveEnergyLeft = root.GetResource(Game.Economy.ResourceType.Energy) - session.EnergyClaimed;
            if (realEnergy > liveEnergyLeft + eps)
                return ProvisioningResult.Fail(ProvisionFailure.MoverContended(
                    $"turn Energy exhausted: air actor #{moverArmyId} needs {N(realEnergy)}, "
                    + $"{N(liveEnergyLeft)} left after earlier claims this pass"));

            // AI-MGR — the SINGLE strategic sortie-reservation admission. Everything above
            // (CanAffordLaunch, the funded-envelope comparison, the cumulative live AP/Energy checks)
            // is HARD feasibility: "enough resources exist right now". This is the one strategic
            // question — "is THIS exact sortie worth paying for this turn versus keeping the Energy
            // for hand/deck card pressure?" — and it is answered HERE and nowhere else. The canonical
            // staged decision (Resource Outlook -> Hand/Deck Energy Pressure -> Recon Value ->
            // Reserve) is AviationSortieReservationEvaluator, fed the exact per-actor AP/Energy and
            // the mission-specific route score Assignment already resolved (ScoutExecutionCandidate),
            // never a fresh air-route re-probe. Recomputed every turn: an idle aircraft never yields
            // a standing reservation. Tactical layers below MUST NOT re-run this economics — they are
            // limited to live hard/safety gates (CanAffordLaunch / CanIssueMoveNow / AA / safe return).
            ProvisionFailure? sortieDeclined = AirSortieReservationAdmission(
                player, root, ctx, session, exec, moverArmyId, airfieldHex, realAp, realEnergy);
            if (sortieDeclined.HasValue)
                return ProvisioningResult.Fail(sortieDeclined.Value);

            return ProvisioningResult.Ok(new ProvisionedMission
            {
                Mission = m,
                Key = key,
                Kind = MissionKind.Scout,
                ScoutKind = target.Kind,
                MoverArmyId = moverArmyId,
                FocusHex = focus,
                ExecutionHex = executionHex,
                TrackedArmyId = surveil ? target.Contact.Army.ArmyId : (int?)null,
                BaselineObservedTurn = surveil ? target.Contact.LastObservedTurn : 0,
                ClaimedAp = realAp,
                ClaimedEnergy = realEnergy,
                ClaimedPhysical = new ResourceVector(0f, 0f, realEnergy, 0f, 0f),
                StealthApReserved = false,
                RequiresStealth = false,
                ExecutorKind = exec.ExecutorKind,
                AirfieldHex = airfieldHex,
                LaunchSubset = launchSubset,
            });
        }

        // The ONE strategic admission for an air-recon sortie whose HARD feasibility already passed
        // inside ProvisionAir. Returns null to proceed with the reservation, or a SortieNotWorthwhile
        // failure (RetryNextTurn, no cooldown) when the canonical evaluator declines this turn.
        //
        // No generic air-route re-probe happens here: Assignment (ReconAssignmentPlanner.
        // AppendAirCandidates) already picked this exact actor/airfield AND proved a mission-specific
        // route — the resulting AIR-01 route score and the exact per-actor AP/Energy ride in on the
        // ScoutExecutionCandidate. This method feeds those figures, plus what earlier missions this
        // pass have already claimed (session.ApClaimed/EnergyClaimed), straight into the canonical
        // AviationSortieReservationEvaluator (Resource Outlook -> Hand/Deck Energy Pressure -> Recon
        // Value -> Reserve), which reads live hand/deck/income Energy pressure itself. No new
        // evaluator, no per-turn reservation registry — recomputed every turn.
        private static ProvisionFailure? AirSortieReservationAdmission(
            PlayerSetupData player, PlayerRoot root, AiTurnContext ctx, ProvisioningSession session,
            ScoutExecutionCandidate exec, int moverArmyId, HexCoord airfieldHex, float realAp, float realEnergy)
        {
            // Bare harness / no world to reason about — hard gates already passed, leave behaviour unchanged.
            if (ctx?.Map == null || session?.Snapshot == null)
                return null;

            bool existing = exec.ExecutorKind == ScoutExecutorKind.AirExisting;
            string label = existing ? $"actor=#{moverArmyId}" : $"airfield=({airfieldHex.Q},{airfieldHex.R})";

            AviationReservationDecision decision = AviationSortieReservationEvaluator.EvaluateRecon(
                player, root, ctx.Map,
                Mathf.CeilToInt(Mathf.Max(0f, realAp)),
                Mathf.CeilToInt(Mathf.Max(0f, realEnergy)),
                exec.RouteScore,
                existing ? moverArmyId : -1,
                Mathf.CeilToInt(Mathf.Max(0f, session.ApClaimed)),
                Mathf.CeilToInt(Mathf.Max(0f, session.EnergyClaimed)),
                // Actors already in session.EnergyClaimed (a continuing wing provisioned earlier
                // this pass) — the evaluator's live scan must not re-count their owed Energy.
                session.ClaimedArmyIds);
            AiDebugLog.Write(decision.ToLog(label));

            return decision.ShouldReserve
                ? (ProvisionFailure?)null
                : ProvisionFailure.SortieNotWorthwhile(
                    $"air actor #{moverArmyId}: sortie not worth reserving this turn ({decision.Reason})");
        }

        // Round 3 (Problem 2) — PURE translation of ReconAssignmentPlanner.AssignFunded's already-
        // computed rejection reason into a ProvisionFailure. No eligibility / route / vantage
        // re-derivation happens here any more — "why couldn't this job be assigned" has exactly ONE
        // owner, ReconAssignmentPlanner, and this is just its vocabulary mapped onto Provisioning's.
        private static ProvisioningResult ClassifyNoAssignment(ProvisioningSession session,
            StableMissionKey key, ScoutMissionTarget target)
            => ProvisioningResult.Fail(AssignmentFailure(session, key, target));

        private static ProvisionFailure AssignmentFailure(ProvisioningSession session,
            StableMissionKey key, ScoutMissionTarget target)
        {
            bool needStealth = target.Stealth == StealthRequirement.Required;
            if (!session.TryGetAssignmentRejection(key, out ScoutAssignmentFailureReason reason))
                // Should not happen — AssignFunded rejects or accepts every mission it is handed.
                // Fail safe rather than re-deriving anything ourselves.
                return ProvisionFailure.MoverContended(
                    $"{key} had no assignment result this pass (assignment/provisioning desync)");

            switch (reason)
            {
                case ScoutAssignmentFailureReason.NoMoverExists:
                    return ProvisionFailure.NoMoverExists(
                        "no solo Recce" + (needStealth ? " with stealth capability" : "") + " on the map");
                case ScoutAssignmentFailureReason.NoObservationVantage:
                    return ProvisionFailure.NoObservationVantage(
                        $"no on-map vantage within any scout's vision of ({target.FocusHex.Q},{target.FocusHex.R})");
                case ScoutAssignmentFailureReason.NoExecutableStep:
                    return ProvisionFailure.NoExecutableStep(
                        $"eligible scout(s)/vantage exist but none reachable this turn toward "
                        + $"({target.FocusHex.Q},{target.FocusHex.R})");
                default:
                    return ProvisionFailure.MoverContended(
                        "a capable solo Recce exists but is spent / activated / claimed this cycle");
            }
        }

        private static bool HasFresherSighting(PlayerSetupData player, int trackedArmyId, int baselineTurn)
        {
            foreach (AiMapMemory.KnownEnemySighting s in AiMapMemory.AllKnownEnemySightings(player))
                if (s.ArmyId == trackedArmyId && s.SeenTurn > baselineTurn)
                    return true;
            return false;
        }

        // Was byte-identical in ReconAssignmentPlanner and (twice) in this file — moved to AiV2Util.
        private static ArmyData ResolveArmy(PlayerSetupData player, int armyId) =>
            AiV2Util.ResolveArmy(player, armyId);

        private static string N(float v) => v.ToString("0.##", CultureInfo.InvariantCulture);
    }

    internal static class ActiveDefenceProvisioner
    {
        // Same invariant AP formatting RaidProvisioner logs with — the two lanes' provisioning
        // lines are read side by side.
        private static string N(float v) => v.ToString("0.##", CultureInfo.InvariantCulture);

        internal static ProvisioningResult Provision(PlayerSetupData player, PlayerRoot root,
            AiTurnContext ctx, ProvisioningSession session, FundedEntry funded)
        {
            MissionProposal mission = funded?.Mission;
            if (mission == null || !(mission.Target is ActiveDefenceMissionTarget target))
                return ProvisioningResult.Fail(ProvisionFailure.TargetInvalidated(
                    "active defence has no typed target"));

            if (target.Phase == ActiveDefencePhase.Return)
                return ProvisionReturn(player, root, ctx, session, funded, target);

            AiMapMemory.KnownEnemySighting? sighting = AiMapMemory.AllKnownEnemySightings(player)
                .Where(s => s.ArmyId == target.EnemyArmyId && s.Owner != null
                    && !s.Owner.IsNeutral && s.Owner != player)
                .Select(s => (AiMapMemory.KnownEnemySighting?)s).FirstOrDefault();
            if (!sighting.HasValue)
                return ProvisioningResult.Fail(ProvisionFailure.TargetInvalidated(
                    $"active defence enemy #{target.EnemyArmyId} has no honest sighting"));
            // P0-5, AI V2 economy/aggression audit 2026-09-21 — AiMapMemory.OnVisibilityChanged is
            // already the single canonical, fog-honest writer for EnemySightings: it removes a
            // stale entry the instant its hex is genuinely re-observed and found empty ("corrected
            // (gone on re-observation)"), and otherwise leaves a last-known sighting untouched while
            // that hex stays fogged. `sighting.HasValue` above is therefore already the complete,
            // correct answer to "is this enemy still a live threat, per what we honestly know" — a
            // second true-world ArmyRegistry.AllAt(sighting.Value.Hex) read here used to re-derive
            // the same fact from ground truth instead of memory, and disagreed with it exactly when
            // the sighting was stale-but-unobserved (enemy moved off an unwatched hex): that false
            // "gone" produced TargetSatisfied → the objective was re-created and immediately
            // re-satisfied every subsequent pass with no new information (18x on #13, T9-T10).
            bool hexVisibleNow = VisionSystem.IsVisible(player, sighting.Value.Hex);
            AiDebugLog.Write($"[AGG][ActiveDefence] enemy=#{target.EnemyArmyId} "
                + $"contact={(hexVisibleNow ? "LIVE" : "LAST_KNOWN")} "
                + $"hexVisible={hexVisibleNow.ToString().ToLowerInvariant()} "
                + "decision=APPROACH_LAST_KNOWN");

            StableMissionKey key = StableMissionKey.For(mission);
            if (!session.TryGetAssignedGroundCombatActor(key, out int actorId))
                return ProvisioningResult.Fail(ProvisionFailure.MoverContended(
                    $"active defence {key} has no actor in shared ground-combat assignment"));
            HashSet<int> excluded = session.ExcludedForGroundCombat(mission);
            if (excluded.Contains(actorId))
                return ProvisioningResult.Fail(ProvisionFailure.MoverContended(
                    $"active defence actor #{actorId} is owned by another mission"));

            IReadOnlyList<WorthIt.DefenderProfile> defenders = sighting.Value.Defenders
                ?? Array.Empty<WorthIt.DefenderProfile>();
            // 2026-09-21 Block C2 — re-checking an admitted mission must re-apply the SAME
            // admission threshold it was legitimately admitted under, otherwise Missions accepts a
            // continuation at ContinuationWinChanceFloor, the allocator funds it, and Provisioning
            // then rejects the identical facts at FreshStartWinChanceGate every single turn.
            // GroundCombatAdmissionPolicy stays the sole owner of both numbers; this only selects
            // between them with the same predicate Missions and GroundCombatAdmissionRegistry use
            // (`FromDurableIntent`), additionally confirming that the actor actually bound here is
            // the pinned incumbent — a different actor is a fresh intercept and keeps the fresh gate.
            MissionIntent interceptIncumbent = MissionIntentRegistry.GetOrCreate(player).All
                .FirstOrDefault(i => i != null && i.Status == IntentStatus.Active
                    && i.Kind == MissionKind.ActiveDefence
                    && i.ActiveDefence?.EnemyArmyId == target.EnemyArmyId);
            bool continuesPinnedIntercept = mission.FromDurableIntent
                && interceptIncumbent?.ActiveDefence?.PrimaryArmyId == actorId;
            GroundCombatAssemblyPlan plan = GroundCombatAssemblyPlanner.Plan(session.Snapshot,
                new GroundCombatAssemblyRequest
                {
                    Defenders = defenders,
                    PreferredPrimaryArmyId = actorId,
                    PinToPreferred = true,
                    ExcludedArmyIds = excluded,
                    WinChanceGate = continuesPinnedIntercept
                        ? GroundCombatAdmissionPolicy.ContinuationWinChanceFloor
                        : GroundCombatAdmissionPolicy.FreshStartWinChanceGate,
                });
            if (!plan.Feasible)
                return ProvisioningResult.Fail(ProvisionFailure.MoverContended(
                    $"active defence actor #{actorId} cannot complete roster: {plan.Reason}"));

            ArmyData host = AiV2Util.ResolveArmy(player, actorId);
            if (host == null || host.Owner != player || host.Members.Count == 0
                || host.CurrentMovement <= 0 || session.ClaimedArmyIds.Contains(host.Id))
                return ProvisioningResult.Fail(ProvisionFailure.MoverContended(
                    $"active defence actor #{actorId} is no longer available"));
            // FIX-03 — the assembly is ONE transaction with explicit stages, the same shape the
            // Raid lane already has: PREPARE / VALIDATE ALL TRANSFERS (every legality question
            // asked before any mutation) -> APPLY -> RECONCILE -> COMMIT CLAIMS. Previously a
            // donor was written into session.ClaimedArmyIds immediately after EACH successful
            // transfer, so a later failure rolled the world back but left those donors claimed for
            // the rest of the pass (a claim leak that silently starved Raid/Economy of actors),
            // and the refusal always reported StateChanged=false — even when the rollback itself
            // had been incomplete and the world really HAD changed.
            float eps = AiConfigV2.allocatorSliceEpsilon;
            var transfers = new List<GroundCombatAssemblyTransfer>();
            var claimedDonors = new HashSet<int>();
            var projectedUnits = new List<UnitData>(host.Members);
            if (plan.NeedsAssembly)
            {
                int heroTransfers = 0;
                foreach (GroundCombatAssemblyTransfer t in plan.Transfers)
                {
                    if (t?.Unit == null)
                        return ProvisioningResult.Fail(ProvisionFailure.AssemblyInfeasible(
                            "active-defence assembly contains a null unit"));
                    ArmyData donor = AiV2Util.ResolveArmy(player, t.DonorArmyId);
                    if (donor == null || donor.Members.Count <= 1 || !donor.Hex.Equals(host.Hex))
                        return ProvisioningResult.Fail(ProvisionFailure.AssemblyInfeasible(
                            $"active-defence donor #{t.DonorArmyId} is gone, moved, or would be emptied"));
                    if (session.ClaimedArmyIds.Contains(donor.Id))
                        return ProvisioningResult.Fail(ProvisionFailure.MoverContended(
                            $"active-defence donor #{donor.Id} was claimed by an earlier mission this cycle"));
                    if (t.Unit.IsHero
                        && (++heroTransfers > 1 || projectedUnits.Any(u => u != null && u.IsHero)))
                        return ProvisioningResult.Fail(ProvisionFailure.AssemblyInfeasible(
                            $"active-defence host #{host.Id} may take at most one hero and only when heroless"));
                    if (donor.IsPrison || donor.IsAirfield || AviationRules.IsAirArmy(donor)
                        || AiArmyRoles.IsSoloRecce(donor) || !donor.Members.Contains(t.Unit)
                        || t.Unit.IsAviation)
                        return ProvisioningResult.Fail(ProvisionFailure.AssemblyInfeasible(
                            $"active-defence donor #{donor.Id} / unit {t.Unit.Name} is no longer legal"));
                    if (!donor.CanLeaveWithoutOvercrowding(t.Unit)
                        || (donor.IsGarrison && !AiArmyRoles.CanSpareGarrisonMember(player, donor, t.Unit)))
                        return ProvisioningResult.Fail(ProvisionFailure.AssemblyInfeasible(
                            $"active-defence donor #{donor.Id} can no longer spare {t.Unit.Name}"));
                    if (host.HasActivatedThisTurn && t.Unit.ActivationApCost > 0)
                        return ProvisioningResult.Fail(ProvisionFailure.AssemblyInfeasible(
                            $"adding {t.Unit.Name} to an activated active-defence host would spend unbudgeted AP"));
                    var withUnit = new List<UnitData>(projectedUnits) { t.Unit };
                    if (ArmyData.ComputeCapacity(withUnit, host.IsGarrison) < withUnit.Count)
                        return ProvisioningResult.Fail(ProvisionFailure.AssemblyInfeasible(
                            $"active-defence host #{host.Id} no longer has capacity for planned assembly"));
                    projectedUnits.Add(t.Unit);
                    transfers.Add(t);
                    claimedDonors.Add(donor.Id);
                }
                foreach (IGrouping<int, GroundCombatAssemblyTransfer> group in transfers.GroupBy(t => t.DonorArmyId))
                {
                    ArmyData donor = AiV2Util.ResolveArmy(player, group.Key);
                    List<UnitData> units = group.Select(t => t.Unit).ToList();
                    if (donor == null || donor.Members.Count - units.Count < 1
                        || donor.IsGarrison
                            && !AiArmyRoles.CanSpareGarrisonMembers(player, donor, units))
                        return ProvisioningResult.Fail(ProvisionFailure.AssemblyInfeasible(
                            $"active-defence donor #{group.Key} cannot spare the complete batch"));
                }
            }

            // FIX-02 — every physical-executability check below is asked about `projectedUnits`,
            // the roster this plan will actually march, and all of them still run BEFORE the first
            // ArmyActions.TransferMember. The old code asked the UNTOUCHED host whether it could
            // afford the activation and reach the target, then transferred bodies in — the exact
            // shape AI-01 already removed from the Raid lane, where the assembled force turned out
            // to cost more AP than was ever funded only after the world had been mutated.
            // Reachability too: a recruit slower than the host lowers the whole formation's shared
            // movement, so the first step is re-asked for the projected roster.
            if (SafeStepPathing.FindNextSafeStepForRoster(ctx.Map, host, sighting.Value.Hex,
                    projectedUnits) == null)
                return ProvisioningResult.Fail(ProvisionFailure.NoExecutableStep(
                    $"no safe step toward active defence enemy #{target.EnemyArmyId}"
                    + (plan.NeedsAssembly ? " for the projected assembled roster" : "")));

            int activationAp = host.ProjectedActivationApCost(projectedUnits);
            float envelope = funded.Tentative.Ap;
            if (activationAp > envelope + eps)
                return ProvisioningResult.Fail(ProvisionFailure.EnvelopeTooSmall(activationAp,
                    $"active defence actor #{actorId} needs {N(activationAp)} AP for its projected "
                    + $"{projectedUnits.Count}-body roster, envelope is {N(envelope)}"));
            if (activationAp > root.ActionPoints - session.ApClaimed + eps)
                return ProvisioningResult.Fail(ProvisionFailure.MoverContended(
                    "turn AP exhausted before active defence"));

            // APPLY. Nothing above this line has mutated the world, so every refusal so far is a
            // clean Complete rollback by construction: no transfer, no claim, StateChanged=false.
            var applied = new List<GroundCombatAssemblyTransfer>();
            foreach (GroundCombatAssemblyTransfer transfer in transfers)
            {
                ArmyData donor = AiV2Util.ResolveArmy(player, transfer.DonorArmyId);
                string why = donor == null ? "donor missing" : null;
                if (donor == null || !ArmyActions.TransferMember(transfer.Unit, donor, host,
                        ctx.HexSelection, out why))
                {
                    bool rollbackOk = GroundCombatAssemblyTransaction.Rollback(player, host,
                        applied, ctx, "active-defence");
                    int stillApplied = GroundCombatAssemblyTransaction.RemainingApplied(host, applied);
                    AiDebugLog.Write($"[AI][V2][ActiveDefence][Provision] decision=ROLLBACK "
                        + $"enemy={target.EnemyArmyId} actor={host.Id} unit={transfer.Unit?.Name} "
                        + $"donor=#{transfer.DonorArmyId}: {why}; "
                        + $"rollback={(rollbackOk ? "OK" : "FAILED")}; remainingTransfers={stillApplied}");
                    // An incomplete rollback is an honest partial mutation: report StateChanged so
                    // the version is bumped and no later stage keeps planning on the old world.
                    return ProvisioningResult.Fail(ProvisionFailure.AssemblyInfeasible(
                            rollbackOk ? $"atomic active-defence assembly rejected: {why}"
                                : $"active-defence assembly rollback incomplete: {why}"),
                        stillApplied > 0, stillApplied);
                }
                applied.Add(transfer);
            }

            // RECONCILE — the assembled force must cost exactly what was projected and funded.
            // Any divergence rolls the whole transaction back rather than succeeding partially.
            int actualAp = host.ProjectedActivationApCost(host.Members);
            if (actualAp != activationAp || actualAp > envelope + eps)
            {
                bool reconcileRollbackOk = GroundCombatAssemblyTransaction.Rollback(player, host,
                    applied, ctx, "active-defence");
                int stillApplied = GroundCombatAssemblyTransaction.RemainingApplied(host, applied);
                AiDebugLog.Write($"[AI][V2][ActiveDefence][Provision] decision=RECONCILE_FAIL "
                    + $"enemy={target.EnemyArmyId} actor={host.Id} actual={N(actualAp)} "
                    + $"projected={N(activationAp)} envelope={N(envelope)}; "
                    + $"rollback={(reconcileRollbackOk ? "OK" : "FAILED")}; remainingTransfers={stillApplied}");
                return ProvisioningResult.Fail(ProvisionFailure.EnvelopeTooSmall(actualAp,
                        $"active-defence host #{host.Id} reconciled activation {N(actualAp)} AP "
                        + $"diverges from the projected {N(activationAp)} AP"),
                    stillApplied > 0, stillApplied);
            }

            // COMMIT CLAIMS — only now, against a confirmed complete assembly.
            foreach (int donorId in claimedDonors)
                session.ClaimedArmyIds.Add(donorId);

            target.LastKnownHex = sighting.Value.Hex;
            target.LastObservedTurn = sighting.Value.SeenTurn;
            target.PrimaryArmyId = host.Id;
            target.ProjectedWinChance = plan.ProjectedWinChance;
            target.CoversAllDefenders = plan.CoversAllDefenders;
            AiDebugLog.Write($"[AI][V2][ActiveDefence][Provision] decision=OK enemy={target.EnemyArmyId} "
                + $"actor={host.Id} transfers={applied.Count} win={plan.ProjectedWinChance:0.00}");
            return ProvisioningResult.Ok(new ProvisionedMission
            {
                Mission = mission, Key = key, Kind = MissionKind.ActiveDefence,
                MoverArmyId = host.Id, FocusHex = target.LastKnownHex,
                ExecutionHex = target.LastKnownHex, ActiveDefenceTarget = target,
                ClaimedPhysical = funded.PhysicalDraw,
                // The reconciled cost of the force that actually exists now — asserted equal to
                // the projected/funded figure above, so allocator, revalidator and executor all
                // debit this one number exactly once.
                ClaimedAp = actualAp,
            }, applied.Count);
        }

        private static ProvisioningResult ProvisionReturn(PlayerSetupData player, PlayerRoot root,
            AiTurnContext ctx, ProvisioningSession session, FundedEntry funded,
            ActiveDefenceMissionTarget target)
        {
            if (!target.PrimaryArmyId.HasValue || !target.ReturnHex.HasValue)
                return ProvisioningResult.Fail(ProvisionFailure.TargetInvalidated(
                    "active defence return has no actor or home"));
            ArmyData actor = AiV2Util.ResolveArmy(player, target.PrimaryArmyId.Value);
            if (actor == null || actor.Owner != player || actor.Members.Count == 0)
                return ProvisioningResult.Fail(ProvisionFailure.TargetInvalidated(
                    "active defence return actor is gone"));
            if (actor.Hex.Equals(target.ReturnHex.Value))
                return ProvisioningResult.Fail(ProvisionFailure.TargetSatisfied(
                    "active defence responder is already home"));
            if (session.ClaimedArmyIds.Contains(actor.Id))
                return ProvisioningResult.Fail(ProvisionFailure.MoverContended(
                    "active defence return actor is claimed"));
            if (SafeStepPathing.FindNextSafeStep(ctx.Map, actor, target.ReturnHex.Value) == null)
                return ProvisioningResult.Fail(ProvisionFailure.NoExecutableStep(
                    "active defence responder has no safe return step"));
            int ap = actor.HasActivatedThisTurn ? 0 : actor.ActivationApCost;
            if (ap > funded.Tentative.Ap + AiConfigV2.allocatorSliceEpsilon)
                return ProvisioningResult.Fail(ProvisionFailure.EnvelopeTooSmall(ap,
                    "active defence return AP envelope is stale"));
            return ProvisioningResult.Ok(new ProvisionedMission
            {
                Mission = funded.Mission, Key = StableMissionKey.For(funded.Mission),
                Kind = MissionKind.ActiveDefence, MoverArmyId = actor.Id,
                FocusHex = target.ReturnHex.Value, ExecutionHex = target.ReturnHex.Value,
                ActiveDefenceTarget = target, ClaimedPhysical = funded.PhysicalDraw,
                ClaimedAp = ap,
            });
        }

    }

    // FIX-03 — the one same-hex ground-combat assembly rollback/accounting primitive. Raid and
    // ActiveDefence ran two byte-similar private copies of this; a transaction that has to report
    // honestly whether the world was left mutated must measure that the same way in both lanes.
    // No state of its own: it is the undo half of the Provisioning-tier transaction, nothing more.
    internal static class GroundCombatAssemblyTransaction
    {
        // Undo every applied transfer, newest first. Returns false if ANY body could not be put
        // back — the caller must then report the mutation honestly instead of claiming a clean
        // rejection.
        internal static bool Rollback(PlayerSetupData player, ArmyData host,
            List<GroundCombatAssemblyTransfer> applied, AiTurnContext ctx, string lane)
        {
            bool ok = true;
            if (host == null || applied == null)
                return false;
            for (int i = applied.Count - 1; i >= 0; --i)
            {
                GroundCombatAssemblyTransfer t = applied[i];
                ArmyData donor = AiV2Util.ResolveArmy(player, t?.DonorArmyId ?? -1);
                string why = donor == null ? "donor missing"
                    : t?.Unit == null || !host.Members.Contains(t.Unit) ? "unit no longer in host"
                    : null;
                if (donor == null || t?.Unit == null || !host.Members.Contains(t.Unit)
                    || !ArmyActions.TransferMember(t.Unit, host, donor, ctx.HexSelection, out why))
                {
                    ok = false;
                    AiDebugLog.Write($"[AI][V2]   {lane} assembly rollback — FAILED {t?.Unit?.Name} "
                        + $"host #{host.Id}->donor #{t?.DonorArmyId}: {why}");
                }
            }
            return ok;
        }

        // How many of the applied transfers are STILL sitting in the host after a rollback attempt
        // — i.e. how much of the world this transaction really changed. Zero means a Complete
        // rollback (no mutation to report); anything else is an Incomplete one.
        internal static int RemainingApplied(ArmyData host,
            List<GroundCombatAssemblyTransfer> applied)
        {
            if (host == null || applied == null)
                return 0;
            int count = 0;
            foreach (GroundCombatAssemblyTransfer t in applied)
                if (t?.Unit != null && host.Members.Contains(t.Unit))
                    count++;
            return count;
        }
    }

    internal static class RaidProvisioner
    {
        public static ProvisioningResult Provision(PlayerSetupData player, PlayerRoot root, AiTurnContext ctx,
            ProvisioningSession session, FundedEntry funded)
        {
            MissionProposal m = funded.Mission;
            if (!(m.Target is RaidMissionTarget target))
                return ProvisioningResult.Fail(ProvisionFailure.AssemblyInfeasible("raid mission has no RaidMissionTarget"));

            StableMissionKey key = StableMissionKey.For(m);
            WorldSnapshot snap = session.Snapshot;
            float eps = AiConfigV2.allocatorSliceEpsilon;

            // AGG-RAID §9/§SupportReturn — the Assault leg keeps the existing transactional
            // same-hex assembly verbatim. Reinforcement, Return and SupportReturn are their own,
            // much narrower provisioning shapes; Return and SupportReturn share one implementation
            // (mover = primary vs. mover = support), never re-picking the destination.
            if (target.Phase == RaidMissionPhase.AirSupport)
                return ProvisionAirSupport(player, root, ctx, session, funded, target, key, eps);
            if (target.Phase == RaidMissionPhase.Return)
                return ProvisionReturn(player, root, ctx, session, funded, target, key, eps,
                    RaidMissionPhase.Return, target.PrimaryArmyId);
            if (target.Phase == RaidMissionPhase.RecoveryReturn)
                return ProvisionReturn(player, root, ctx, session, funded, target, key, eps,
                    RaidMissionPhase.RecoveryReturn, target.PrimaryArmyId);
            if (target.Phase == RaidMissionPhase.SupportReturn)
                return ProvisionReturn(player, root, ctx, session, funded, target, key, eps,
                    RaidMissionPhase.SupportReturn, target.SupportArmyId);
            if (target.Phase == RaidMissionPhase.Reinforcement)
                return ProvisionReinforcement(player, root, ctx, session, funded, target, key, eps);

            // Assault — defender resolution goes through the single canonical resolver so an
            // EventGuard target (no ArmyId, no live sighting to find) and a NeutralArmy target
            // share one code path here instead of two parallel switches.
            RaidTargetRef raidTarget = target.Target;
            HexCoord targetHex;
            IReadOnlyList<WorthIt.DefenderProfile> defenders;
            bool targetIsNeutral;
            if (raidTarget.Kind == RaidTargetKind.EventGuard)
            {
                if (!HexEventRegistry.HasActiveEvent(raidTarget.Hex))
                {
                    return RaidObjectiveEvaluator.IsObjectiveSatisfiedLive(player, raidTarget)
                        ? ProvisioningResult.Fail(ProvisionFailure.TargetSatisfied(
                            $"raid target {raidTarget.DiagnosticLabel} already consumed"))
                        : ProvisioningResult.Fail(ProvisionFailure.TargetInvalidated(
                            $"raid target {raidTarget.DiagnosticLabel} no longer has an active event"));
                }
                targetHex = raidTarget.Hex;
                defenders = AiV2Util.KnownDefenders(snap, raidTarget);
                targetIsNeutral = true;
            }
            else
            {
                AiMapMemory.KnownEnemySighting? sighting = FindLiveSighting(player, raidTarget.ArmyId);
                if (sighting == null)
                {
                    if (RaidObjectiveEvaluator.IsObjectiveSatisfiedLive(player, raidTarget))
                        return ProvisioningResult.Fail(ProvisionFailure.TargetSatisfied(
                            $"raid target #{raidTarget.ArmyId} no longer exists (destroyed / captured)"));
                    return ProvisioningResult.Fail(ProvisionFailure.TargetInvalidated(
                        $"raid target #{raidTarget.ArmyId} has no current honest sighting; absence is not proof of destruction"));
                }
                // AGG-RAID P0#2 — defensive re-check only; RaidObjectiveEvaluator.IsNeutralRaidTarget
                // is the ONE canonical neutrality decision. Raid targets neutrals only, so ANY
                // non-neutral owner ends the leg here — "now ours" (captured) is reported as
                // satisfied, any other non-neutral owner (the target flipped to a different player
                // mid-Raid) is invalidated so Continuity retargets instead of continuing to attack a
                // now-illegal target.
                if (!RaidObjectiveEvaluator.IsNeutralRaidTarget(sighting.Value.Owner))
                {
                    return ProvisioningResult.Fail(ProvisionFailure.TargetSatisfied(
                        sighting.Value.Owner.Equals(player)
                            ? $"raid target #{raidTarget.ArmyId} is now ours"
                            : $"raid target #{raidTarget.ArmyId} is no longer neutral "
                                + "(now owned by another player)"));
                }

                targetHex = sighting.Value.Hex;
                defenders = sighting.Value.Defenders ?? System.Array.Empty<WorthIt.DefenderProfile>();
                targetIsNeutral = sighting.Value.Owner != null && sighting.Value.Owner.IsNeutral;
            }

            // Assault actor ownership is decided once by PrepareGroundCombatAssignments. Do not
            // re-run a FREE army search here: re-plan ONLY the assigned host, through the same
            // ExcludedForGroundCombat ownership view the batch solver used, so a Raid can never steal a
            // durable Economy/Recon/Raid actor after the batch solver correctly rejected it.
            GroundCombatAssemblyPlan plan = PlanAssignedAssault(session, m, defenders,
                out ProvisionFailure assignmentFailure);
            if (plan == null)
                return ProvisioningResult.Fail(assignmentFailure);

            ArmyData host = ResolveArmy(player, plan.BaseArmyId);
            if (host == null || host.Members.Count == 0 || host.CurrentMovement <= 0
                || host.IsPrison || host.IsAirfield || AviationRules.IsAirArmy(host)
                || AiArmyRoles.IsSoloRecce(host) || AiArmyRoles.IsSoloHeroAwaitingEscort(host))
                return ProvisioningResult.Fail(ProvisionFailure.MoverContended(
                    $"raid host #{plan.BaseArmyId} is no longer a usable ground combat army"));
            if (host.Owner != player || session.ClaimedArmyIds.Contains(host.Id))
                return ProvisioningResult.Fail(ProvisionFailure.MoverContended(
                    $"raid host #{plan.BaseArmyId} was claimed by an earlier mission this cycle"));

            var transfers = new List<GroundCombatAssemblyTransfer>();
            var claimedDonors = new HashSet<int>();
            var projectedUnits = new List<UnitData>(host.Members);
            if (plan.NeedsAssembly)
            {
                int heroTransfers = 0;
                foreach (GroundCombatAssemblyTransfer t in plan.Transfers)
                {
                    if (t?.Unit == null)
                        return ProvisioningResult.Fail(ProvisionFailure.AssemblyInfeasible("raid assembly contains a null unit"));
                    ArmyData donor = ResolveArmy(player, t.DonorArmyId);
                    if (donor == null || donor.Members.Count <= 1 || !donor.Hex.Equals(host.Hex))
                        return ProvisioningResult.Fail(ProvisionFailure.AssemblyInfeasible(
                            $"raid donor #{t.DonorArmyId} is gone, moved, or would be emptied"));
                    if (session.ClaimedArmyIds.Contains(donor.Id))
                        return ProvisioningResult.Fail(ProvisionFailure.MoverContended(
                            $"raid donor #{donor.Id} was claimed by an earlier mission this cycle"));
                    bool unitIsHero = t.Unit.IsHero;
                    if (unitIsHero && (++heroTransfers > 1 || projectedUnits.Any(u => u != null && u.IsHero)))
                        return ProvisioningResult.Fail(ProvisionFailure.AssemblyInfeasible(
                            $"raid host #{host.Id} may take at most one hero and only when heroless"));
                    if (donor.IsPrison || donor.IsAirfield || AviationRules.IsAirArmy(donor)
                        || AiArmyRoles.IsSoloRecce(donor) || !donor.Members.Contains(t.Unit)
                        || t.Unit.IsAviation)
                        return ProvisioningResult.Fail(ProvisionFailure.AssemblyInfeasible(
                            $"raid donor #{donor.Id} / unit {t.Unit.Name} is no longer legal"));
                    if (!donor.CanLeaveWithoutOvercrowding(t.Unit)
                        || (donor.IsGarrison && !AiArmyRoles.CanSpareGarrisonMember(player, donor, t.Unit)))
                        return ProvisioningResult.Fail(ProvisionFailure.AssemblyInfeasible(
                            $"raid donor #{donor.Id} can no longer spare {t.Unit.Name}"));
                    if (host.HasActivatedThisTurn && t.Unit.ActivationApCost > 0)
                        return ProvisioningResult.Fail(ProvisionFailure.AssemblyInfeasible(
                            $"adding {t.Unit.Name} to activated raid host would spend unbudgeted AP"));

                    var withU = new List<UnitData>(projectedUnits) { t.Unit };
                    if (ArmyData.ComputeCapacity(withU, host.IsGarrison) < withU.Count)
                        return ProvisioningResult.Fail(ProvisionFailure.AssemblyInfeasible(
                            $"raid host #{host.Id} no longer has capacity for planned assembly"));
                    projectedUnits.Add(t.Unit);
                    transfers.Add(t);
                    claimedDonors.Add(donor.Id);
                }

                List<WorthIt.DefenderProfile> projectedProfiles = projectedUnits.Select(WorthIt.FromLiveUnit).ToList();
                foreach (IGrouping<int, GroundCombatAssemblyTransfer> group in transfers.GroupBy(t => t.DonorArmyId))
                {
                    ArmyData donor = ResolveArmy(player, group.Key);
                    List<UnitData> units = group.Select(t => t.Unit).ToList();
                    if (donor == null || donor.Members.Count - units.Count < 1
                        || donor.IsGarrison
                            && !AiArmyRoles.CanSpareGarrisonMembers(player, donor, units))
                        return ProvisioningResult.Fail(ProvisionFailure.AssemblyInfeasible(
                            $"raid donor #{group.Key} cannot spare the complete planned batch"));
                }
                if (!GroundCombatFeasibility.Clears(projectedProfiles, defenders, out float projectedWin, out _))
                    return ProvisioningResult.Fail(ProvisionFailure.AssemblyInfeasible(
                        "planned same-hex roster no longer clears the shared WorthIt estimator"));
                plan.ProjectedWinChance = projectedWin;
            }

            // AI-01 — every check below is priced against `projectedUnits`, the roster that will
            // actually march, and every one of them runs BEFORE the first ArmyActions.TransferMember
            // call. The old code asked the untouched host whether it could afford the step and the
            // activation, then transferred bodies in, and only MissionRevalidator later discovered
            // the assembled force cost more AP than was ever funded — by which point the world had
            // already been mutated.
            // Reachability too: a recruit slower than the host lowers the whole army's shared
            // movement (ArmyData.ComputeCurrentMovement), so the first step must be re-asked with
            // the projected movement rather than the host's own.
            if (SafeStepPathing.FindNextSafeStepForRoster(ctx.Map, host, targetHex, projectedUnits) == null)
                return ProvisioningResult.Fail(ProvisionFailure.NoExecutableStep(
                    $"no safe first step from ({host.Hex.Q},{host.Hex.R}) toward raid target ({targetHex.Q},{targetHex.R})"
                    + (plan.NeedsAssembly ? " for the projected assembled roster" : "")));

            int activationAp = host.ProjectedActivationApCost(projectedUnits);
            float envelope = funded.Tentative.Ap;
            if (activationAp > envelope + eps)
                return ProvisioningResult.Fail(ProvisionFailure.EnvelopeTooSmall(activationAp,
                    $"raid host #{host.Id} needs {N(activationAp)} AP for its projected "
                    + $"{projectedUnits.Count}-body roster, envelope is {N(envelope)}"));
            float turnApLeft = root.ActionPoints - session.ApClaimed;
            if (activationAp > turnApLeft + eps)
                return ProvisioningResult.Fail(ProvisionFailure.MoverContended(
                    $"turn AP exhausted: raid needs {N(activationAp)}, {N(turnApLeft)} left"));

            var applied = new List<GroundCombatAssemblyTransfer>();
            foreach (GroundCombatAssemblyTransfer t in transfers)
            {
                ArmyData donor = ResolveArmy(player, t.DonorArmyId);
                string why = donor == null ? "donor missing" : null;
                if (donor == null || !ArmyActions.TransferMember(t.Unit, donor, host, ctx.HexSelection, out why))
                {
                    bool rollbackOk = RollbackAssembly(player, host, applied, ctx);
                    int transfersStillApplied = applied.Count(x => x?.Unit != null && host.Members.Contains(x.Unit));
                    bool rollbackChangedWorld = transfersStillApplied > 0;
                    AiDebugLog.Write($"[AI][V2]   raid provision [{m.AttemptId}] {key} — assembly transaction failed on "
                        + $"{t.Unit.Name} from #{t.DonorArmyId}: {why}; rollback={(rollbackOk ? "OK" : "FAILED")}; "
                        + $"remainingTransfers={transfersStillApplied}");
                    return ProvisioningResult.Fail(ProvisionFailure.AssemblyInfeasible(
                            rollbackOk ? $"atomic raid assembly rejected: {why}" : $"raid assembly failed and rollback was incomplete: {why}"),
                        rollbackChangedWorld, transfersStillApplied);
                }
                applied.Add(t);
            }

            // AI-01 — the assembled force must cost exactly what was projected and funded. Any
            // divergence (a transfer that landed differently than planned, a roster the host
            // reshaped) is a failure, never a partial success: roll the transaction back and let
            // the existing repack/reprice loop re-decide with honest numbers.
            int actualAp = host.ProjectedActivationApCost(host.Members);
            if (actualAp != activationAp || actualAp > envelope + eps)
            {
                bool reconcileRollbackOk = RollbackAssembly(player, host, applied, ctx);
                int stillApplied = applied.Count(x => x?.Unit != null && host.Members.Contains(x.Unit));
                AiDebugLog.Write($"[AI][V2]   raid provision [{m.AttemptId}] {key} — assembled host #{host.Id} "
                    + $"costs {N(actualAp)} AP but {N(activationAp)} was projected/funded "
                    + $"(envelope {N(envelope)}); rollback={(reconcileRollbackOk ? "OK" : "FAILED")}; "
                    + $"remainingTransfers={stillApplied}");
                return ProvisioningResult.Fail(ProvisionFailure.EnvelopeTooSmall(actualAp,
                        $"raid host #{host.Id} reconciled activation {N(actualAp)} AP diverges from the "
                        + $"projected {N(activationAp)} AP"),
                    stillApplied > 0, stillApplied);
            }

            foreach (int d in claimedDonors)
                session.ClaimedArmyIds.Add(d);

            AiDebugLog.Write($"[AI][V2]   raid provision [{m.AttemptId}] {key} — OK host #{host.Id} "
                + $"{(plan.NeedsAssembly ? $"(+{transfers.Count} body from {claimedDonors.Count} donor) " : "")}" 
                + $"win~{plan.ProjectedWinChance.ToString("0.00", CultureInfo.InvariantCulture)} "
                + $"ap {N(actualAp)} (projected {N(activationAp)}) -> ({targetHex.Q},{targetHex.R})");

            return ProvisioningResult.Ok(new ProvisionedMission
            {
                Mission = m,
                Key = key,
                Kind = MissionKind.Raid,
                MoverArmyId = host.Id,
                FocusHex = targetHex,
                ExecutionHex = targetHex,
                RaidTarget = raidTarget,
                RaidLastKnownHex = targetHex,
                RaidTargetIsNeutral = targetIsNeutral,
                ClaimedPhysical = funded.PhysicalDraw,
                // The reconciled cost of the force that actually exists now — asserted equal to
                // the projected/funded figure above, so allocator, revalidator and executor all
                // debit this one number exactly once.
                ClaimedAp = actualAp,
                StealthApReserved = false,
            }, applied.Count);
        }

        private static ProvisioningResult ProvisionAirSupport(PlayerSetupData player, PlayerRoot root,
            AiTurnContext ctx, ProvisioningSession session, FundedEntry funded,
            RaidMissionTarget target, StableMissionKey key, float eps)
        {
            if (target.Target.Kind != RaidTargetKind.NeutralArmy
                || !target.AirSupportArmyId.HasValue || !target.PrimaryArmyId.HasValue)
                return ProvisioningResult.Fail(ProvisionFailure.TargetInvalidated(
                    "raid air support requires one exact physical neutral target and wing"));
            ArmyData wing = ResolveArmy(player, target.AirSupportArmyId.Value);
            if (wing == null || !AviationRules.IsValidAirArmy(wing) || wing.Owner != player
                || wing.Members.Count == 0 || session.ClaimedArmyIds.Contains(wing.Id))
                return ProvisioningResult.Fail(ProvisionFailure.MoverContended(
                    $"raid support wing #{target.AirSupportArmyId.Value} unavailable"));
            AirSortie active = AirSortieRegistry.ForArmy(player, wing);
            bool continuing = active != null && active.Kind == AirSortieKind.Strike
                && (active.Outbound && active.TargetHex.Equals(target.LastKnownHex)
                    || !active.Outbound);
            bool returning = active != null && active.Kind == AirSortieKind.Strike
                && !active.Outbound;
            if (active != null && !continuing)
                return ProvisioningResult.Fail(ProvisionFailure.MoverContended(
                    $"wing #{wing.Id} is reserved by another sortie"));

            AiMapMemory.KnownEnemySighting? sighting = (session.Snapshot?.Known?.NeutralSightings
                    ?? System.Array.Empty<AiMapMemory.KnownEnemySighting>())
                .Where(s => s.ArmyId == target.Target.ArmyId)
                .Select(s => (AiMapMemory.KnownEnemySighting?)s).FirstOrDefault();
            ArmyData defender = ArmyRegistry.AllAt(target.LastKnownHex)
                .FirstOrDefault(a => a != null && a.Id == target.Target.ArmyId
                    && a.Owner != null && a.Owner.IsNeutral && a.Members.Count > 1
                    && !HexEventRegistry.IsEventGuardArmy(target.LastKnownHex, a));
            if (!returning && (!sighting.HasValue
                    || sighting.Value.SeenTurn != session.Snapshot.TurnNumber
                    || defender == null))
                return ProvisioningResult.Fail(ProvisionFailure.TargetInvalidated(
                    "raid air support target is stale, absent, event-owned, or has only one defender"));

            HexCoord landing;
            if (continuing)
                landing = active.LandingHex;
            else
            {
                Sortie? sameTurn = AiAirSortiePlanner.TryPlanSortie(wing,
                    target.LastKnownHex, ctx.Map, player);
                MultiTurnSortie? multi = sameTurn.HasValue ? null
                    : AiAirSortiePlanner.TryPlanMultiTurnSortie(wing,
                        target.LastKnownHex, ctx.Map, player);
                if (!sameTurn.HasValue && !multi.HasValue)
                    return ProvisioningResult.Fail(ProvisionFailure.NoExecutableStep(
                        $"wing #{wing.Id} has no AA-safe recoverable route to exact raid target"));
                landing = sameTurn?.LandingHex ?? multi.Value.LandingHex;

                IReadOnlyList<WorthIt.DefenderProfile> defenders =
                    AiV2Util.KnownDefenders(session.Snapshot, target.Target);
                AviationCombatEstimator.AirStrikeEstimate estimate =
                    AviationCombatEstimator.EstimateAirStrike(wing.Members,
                        sighting.Value.DefenseSum, sighting.Value.AttackSum, defenders,
                        AirStrikePolicy.RaidSupport(target.Target.ArmyId));
                ArmyData primary = ResolveArmy(player, target.PrimaryArmyId.Value);
                float beforeWin = primary == null || defenders.Count == 0 ? 0f
                    : WorthIt.WinChance(primary, defenders, 0f);
                float afterWin = primary == null || estimate.ExpectedDefendersAfter.Count == 0
                    ? beforeWin
                    : WorthIt.WinChance(primary, estimate.ExpectedDefendersAfter, 0f);
                if (estimate.ExpectedDamage <= eps
                    || estimate.ExpectedDefendersAfter.Count < 1
                    || afterWin <= beforeWin + eps)
                    return ProvisioningResult.Fail(ProvisionFailure.SortieNotWorthwhile(
                        "nonlethal raid support does not improve the primary's projected odds"));
            }

            float ap = wing.HasActivatedThisTurn ? 0f : wing.ActivationApCost;
            float energy = wing.HasActivatedThisTurn ? 0f : wing.ActivationEnergyCost;
            if (ap > funded.Tentative.Ap + eps || energy > funded.PhysicalDraw.Energy + eps)
                return ProvisioningResult.Fail(ProvisionFailure.EnvelopeTooSmall(
                    new ProvisionRequirement(ap, new ResourceVector(0f, 0f, energy, 0f, 0f)),
                    $"raid support wing #{wing.Id} exceeds AP/Energy envelope"));

            target.AirSupportLandingHex = landing;
            return ProvisioningResult.Ok(new ProvisionedMission
            {
                Mission = funded.Mission, Key = key, Kind = MissionKind.Raid,
                MoverArmyId = wing.Id, FocusHex = target.LastKnownHex,
                ExecutionHex = target.LastKnownHex,
                RaidPhase = RaidMissionPhase.AirSupport,
                RaidPrimaryArmyId = target.PrimaryArmyId,
                RaidAirSupportArmyId = wing.Id,
                RaidAirSupportLandingHex = landing,
                RaidDestinationHex = target.LastKnownHex,
                RaidTarget = target.Target,
                RaidLastKnownHex = target.LastKnownHex,
                RaidTargetIsNeutral = true,
                ClaimedAp = ap, ClaimedEnergy = energy,
                ClaimedPhysical = new ResourceVector(0f, 0f, energy, 0f, 0f),
            });
        }

        // A single binding path for assault, used by both the real Provision method and
        // regression tests. The batch solver owns actor identity; the combat assembly kernel
        // owns feasibility of THAT actor and donors, never a replacement actor search.
        internal static GroundCombatAssemblyPlan PlanAssignedAssault(ProvisioningSession session,
            MissionProposal proposal, IReadOnlyList<WorthIt.DefenderProfile> defenders,
            out ProvisionFailure failure)
        {
            failure = default;
            StableMissionKey key = StableMissionKey.For(proposal);
            if (!session.TryGetAssignedGroundCombatActor(key, out int actorId))
            {
                failure = ProvisionFailure.MoverContended(
                    $"raid {key} has no actor in the shared ground-combat assignment");
                return null;
            }

            HashSet<int> excluded = session.ExcludedForGroundCombat(proposal);
            if (excluded.Contains(actorId))
            {
                failure = ProvisionFailure.MoverContended(
                    $"raid {key} assigned actor #{actorId} is claimed by another mission");
                return null;
            }

            // Keep the strict gate for fresh actors and the bounded continuation floor for
            // the same Hard incumbent. Unlike PlanForArmy, this request can also assemble
            // a legal same-hex roster, but may never re-select a different primary.
            GroundCombatAssemblyPlan plan = GroundCombatAssemblyPlanner.Plan(session.Snapshot,
                new GroundCombatAssemblyRequest
                {
                    Defenders = defenders,
                    PreferredPrimaryArmyId = actorId,
                    PinToPreferred = true,
                    ExcludedArmyIds = excluded,
                    // New operations keep the strict fresh gate; only a pinned Hard
                    // incumbent may use the existing bounded continuation floor.
                    WinChanceGate = proposal.FromDurableIntent
                        && proposal.DurableFundingTier == CommitmentTier.Hard
                        && proposal.PreferredMoverArmyId == actorId
                        ? RaidAdmissionPolicy.ContinuationWinChanceFloor
                        : RaidAdmissionPolicy.FreshStartWinChanceGate,
                });
            if (!plan.Feasible)
            {
                // The actor was admitted by the strict proposal-side registry. Rejection now
                // is transient (e.g. a donor became unavailable), not target infeasibility.
                failure = ProvisionFailure.MoverContended(
                    $"raid {key} assigned actor #{actorId} / eligible donors unavailable: {plan.Reason}");
                return null;
            }
            return plan;
        }

        // =====================================================================================
        //  AGG-RAID §9/§SupportReturn — RETURN leg. Mover is the primary (Return) or the support
        //  (SupportReturn); the destination base was already chosen (and fixed) by Continuity.
        //  Provisioning re-validates the actor, the route and the AP envelope; it never re-picks
        //  the base, and never assumes the mover is the primary.
        // =====================================================================================
        private static ProvisioningResult ProvisionReturn(PlayerSetupData player, PlayerRoot root,
            AiTurnContext ctx, ProvisioningSession session, FundedEntry funded,
            RaidMissionTarget target, StableMissionKey key, float eps,
            RaidMissionPhase phase, int? moverArmyId)
        {
            string roleLabel = phase == RaidMissionPhase.SupportReturn ? "support"
                : phase == RaidMissionPhase.RecoveryReturn ? "recovery primary" : "primary";
            if (!moverArmyId.HasValue)
                return ProvisioningResult.Fail(ProvisionFailure.TargetInvalidated(
                    $"raid {roleLabel} return has no mover assigned"));
            ArmyData mover = ResolveArmy(player, moverArmyId.Value);
            if (mover == null || mover.Owner != player || mover.Members.Count == 0
                || mover.IsPrison || mover.IsAirfield || AviationRules.IsAirArmy(mover))
                return ProvisioningResult.Fail(ProvisionFailure.TargetInvalidated(
                    $"raid {roleLabel} return mover #{moverArmyId.Value} is no longer a usable field army"));
            if (session.ClaimedArmyIds.Contains(mover.Id))
                return ProvisioningResult.Fail(ProvisionFailure.MoverContended(
                    $"raid {roleLabel} return mover #{mover.Id} was claimed by an earlier mission this cycle"));

            HexCoord home = target.DestinationHex;
            if (mover.Hex.Equals(home))
                return ProvisioningResult.Fail(ProvisionFailure.TargetSatisfied(
                    $"raid {roleLabel} return mover #{mover.Id} is already home at ({home.Q},{home.R})"));
            if (mover.CurrentMovement <= 0)
                return ProvisioningResult.Fail(ProvisionFailure.NoExecutableStep(
                    $"raid {roleLabel} return mover #{mover.Id} has no movement left"));
            if (SafeStepPathing.FindNextSafeStep(ctx.Map, mover, home) == null)
            {
                // AGG-RAID P1#3 — defensive re-check only; the frozen Analysis reachability fact
                // (ReturnBaseStillValid) already retargets a genuinely unreachable base at turn-start
                // reconciliation, before Provisioning ever runs. This classifies the rare same-turn
                // edge case (the fact changed after reconciliation) distinctly from an ordinary
                // "blocked only this turn" retry.
                ArmySnapshot moverSnap = session.Snapshot?.Self?.Armies?
                    .FirstOrDefault(a => a != null && a.ArmyId == mover.Id);
                bool genuinelyUnreachable = moverSnap != null && moverSnap.IsStructuralRaidActor
                    && !moverSnap.ReachableOwnBaseHexes.Contains(home);
                return ProvisioningResult.Fail(genuinelyUnreachable
                    ? ProvisionFailure.DestinationUnreachable(
                        $"return base ({home.Q},{home.R}) has no safe route at all from "
                        + $"({mover.Hex.Q},{mover.Hex.R})")
                    : ProvisionFailure.NoExecutableStep(
                        $"no safe first step from ({mover.Hex.Q},{mover.Hex.R}) toward return base ({home.Q},{home.R})"));
            }

            int activationAp = mover.HasActivatedThisTurn ? 0 : mover.ActivationApCost;
            if (activationAp > funded.Tentative.Ap + eps)
                return ProvisioningResult.Fail(ProvisionFailure.EnvelopeTooSmall(activationAp,
                    $"raid return needs {N(activationAp)} AP, envelope is {N(funded.Tentative.Ap)}"));
            if (activationAp > root.ActionPoints - session.ApClaimed + eps)
                return ProvisioningResult.Fail(ProvisionFailure.MoverContended(
                    $"turn AP exhausted: raid return needs {N(activationAp)}"));

            string returnLabel = phase == RaidMissionPhase.SupportReturn ? "SUPPORT_RETURN"
                : phase == RaidMissionPhase.RecoveryReturn ? "RECOVERY_RETURN" : "RETURN";
            AiDebugLog.Write($"[AI][V2]   raid provision [{funded.Mission.AttemptId}] {key} — OK "
                + $"{returnLabel} "
                + $"{roleLabel} #{mover.Id} -> ({home.Q},{home.R}) ap {N(activationAp)}");
            return ProvisioningResult.Ok(new ProvisionedMission
            {
                Mission = funded.Mission,
                Key = key,
                Kind = MissionKind.Raid,
                MoverArmyId = mover.Id,
                FocusHex = home,
                ExecutionHex = home,
                RaidPhase = phase,
                RaidPrimaryArmyId = target.PrimaryArmyId,
                RaidSupportArmyId = target.SupportArmyId,
                RaidDestinationHex = home,
                RaidTarget = target.Target,
                RaidLastKnownHex = target.LastKnownHex,
                RaidTargetIsNeutral = target.TargetIsNeutral,
                ClaimedPhysical = funded.PhysicalDraw,
                ClaimedAp = activationAp,
                StealthApReserved = false,
            });
        }

        // =====================================================================================
        //  AGG-RAID §9 — REINFORCEMENT leg. Mover is the SEPARATE support army; the rendezvous is
        //  the primary's current hex. The primary itself does not move while support is in
        //  transit (it is never the mover of this mission and is claimed by continuity).
        // =====================================================================================
        private static ProvisioningResult ProvisionReinforcement(PlayerSetupData player, PlayerRoot root,
            AiTurnContext ctx, ProvisioningSession session, FundedEntry funded,
            RaidMissionTarget target, StableMissionKey key, float eps)
        {
            if (!target.PrimaryArmyId.HasValue)
                return ProvisioningResult.Fail(ProvisionFailure.TargetInvalidated(
                    "raid reinforcement has no primary assigned"));
            ArmyData primary = ResolveArmy(player, target.PrimaryArmyId.Value);
            if (primary == null || primary.Owner != player || primary.Members.Count == 0
                || primary.IsPrison || primary.IsAirfield || AviationRules.IsAirArmy(primary))
                return ProvisioningResult.Fail(ProvisionFailure.TargetInvalidated(
                    $"raid reinforcement primary #{target.PrimaryArmyId.Value} is gone or no longer a field army"));

            // AGG-RAID P0#1 — an UNPINNED leg (no materialization ever happened) has no
            // target.SupportArmyId; the concrete actor comes straight out of the SAME
            // batch-assignment solve PrepareGroundCombatAssignments already runs for Assault.
            int supportArmyId;
            if (!target.SupportArmyId.HasValue)
            {
                if (!session.TryGetAssignedGroundCombatActor(key, out supportArmyId))
                    return ProvisioningResult.Fail(ProvisionFailure.NoMoverExists(
                        $"raid reinforcement for primary #{target.PrimaryArmyId.Value} has no existing free "
                        + "support army assigned this cycle"));
            }
            else
            {
                supportArmyId = target.SupportArmyId.Value;
            }

            ArmyData support = ResolveArmy(player, supportArmyId);
            if (support == null || support.Owner != player || support.Id == primary.Id
                || support.Members.Count == 0 || support.IsPrison || support.IsGarrison
                || support.IsAirfield || AviationRules.IsAirArmy(support))
                return ProvisioningResult.Fail(ProvisionFailure.NoMoverExists(
                    $"raid reinforcement support #{supportArmyId} is not a separate mobile ground army"));
            if (session.ExcludedForGroundCombat(funded.Mission).Contains(support.Id))
                return ProvisioningResult.Fail(ProvisionFailure.MoverContended(
                    $"raid reinforcement support #{support.Id} is claimed by another mission or Raid leg"));

            HexCoord rendezvous = primary.Hex;
            bool atRendezvous = support.Hex.Equals(rendezvous);

            // Does the projected delivered roster actually improve the primary's odds? Re-run the
            // SAME WorthIt projection provisioning/execution will use, never a separate estimator.
            IReadOnlyList<WorthIt.DefenderProfile> defenders =
                AiV2Util.KnownDefenders(session.Snapshot, target.Target);
            if (!ReinforcementImprovesOdds(primary, support, defenders, out string why))
                return ProvisioningResult.Fail(ProvisionFailure.AssemblyInfeasible(
                    $"raid reinforcement #{support.Id} -> #{primary.Id} would not improve the primary's odds: {why}"));

            if (!atRendezvous)
            {
                if (support.CurrentMovement <= 0)
                    return ProvisioningResult.Fail(ProvisionFailure.NoExecutableStep(
                        $"raid reinforcement support #{support.Id} has no movement left"));
                if (SafeStepPathing.FindNextSafeStep(ctx.Map, support, rendezvous) == null)
                    return ProvisioningResult.Fail(ProvisionFailure.NoExecutableStep(
                        $"no safe first step from ({support.Hex.Q},{support.Hex.R}) toward rendezvous ({rendezvous.Q},{rendezvous.R})"));
            }

            int activationAp = support.HasActivatedThisTurn ? 0 : support.ActivationApCost;
            if (activationAp > funded.Tentative.Ap + eps)
                return ProvisioningResult.Fail(ProvisionFailure.EnvelopeTooSmall(activationAp,
                    $"raid reinforcement needs {N(activationAp)} AP, envelope is {N(funded.Tentative.Ap)}"));
            if (activationAp > root.ActionPoints - session.ApClaimed + eps)
                return ProvisioningResult.Fail(ProvisionFailure.MoverContended(
                    $"turn AP exhausted: raid reinforcement needs {N(activationAp)}"));

            // The primary must not be handed to another mission while the convoy is in transit.
            session.ClaimedArmyIds.Add(primary.Id);

            AiDebugLog.Write($"[AI][V2]   raid provision [{funded.Mission.AttemptId}] {key} — OK REINFORCE "
                + $"support #{support.Id} -> primary #{primary.Id} at ({rendezvous.Q},{rendezvous.R}) "
                + $"{(atRendezvous ? "HANDOFF" : "TRANSIT")} ap {N(activationAp)}");
            return ProvisioningResult.Ok(new ProvisionedMission
            {
                Mission = funded.Mission,
                Key = key,
                Kind = MissionKind.Raid,
                MoverArmyId = support.Id,
                FocusHex = rendezvous,
                ExecutionHex = rendezvous,
                RaidPhase = RaidMissionPhase.Reinforcement,
                RaidPrimaryArmyId = primary.Id,
                RaidSupportArmyId = support.Id,
                RaidDestinationHex = rendezvous,
                RaidHandoffReady = atRendezvous,
                RaidTarget = target.Target,
                RaidLastKnownHex = target.LastKnownHex,
                RaidTargetIsNeutral = target.TargetIsNeutral,
                ClaimedPhysical = funded.PhysicalDraw,
                ClaimedAp = activationAp,
                StealthApReserved = false,
            });
        }

        // §9 — the projection: would merging the support's transferable bodies into the primary
        // raise its WorthIt win chance against the current defenders? A convoy that cannot help is
        // never provisioned.
        private static bool ReinforcementImprovesOdds(ArmyData primary, ArmyData support,
            IReadOnlyList<WorthIt.DefenderProfile> defenders, out string why)
        {
            List<UnitData> sparable = SparableSupportBodies(support);
            List<WorthIt.DefenderProfile> primaryBodies = primary.Members
                .Where(u => u != null && !u.IsHero && !u.IsAviation)
                .Select(WorthIt.FromLiveUnit)
                .ToList();
            List<WorthIt.DefenderProfile> supportBodies = sparable
                .Select(WorthIt.FromLiveUnit)
                .ToList();
            int capacity = ArmyData.ComputeCapacity(primary.Members, primary.IsGarrison);
            return GroundCombatAssemblyPlanner.TryProjectReinforcement(
                primaryBodies, supportBodies, capacity, primary.Members.Count,
                defenders, out _, out why);
        }

        // A support container is never emptied and never gives up its own hero.
        internal static List<UnitData> SparableSupportBodies(ArmyData support)
        {
            var list = new List<UnitData>();
            if (support == null)
                return list;
            foreach (UnitData u in support.Members
                .Where(x => x != null && !x.IsHero && !x.IsAviation)
                .OrderByDescending(GroundCombatDonorPolicy.UnitCombatValue)
                .ThenBy(x => x.Name))
            {
                if (support.Members.Count - list.Count <= 1)
                    break;  // minimum-body invariant: leave at least one member behind
                if (!support.CanLeaveWithoutOvercrowding(u))
                    continue;
                list.Add(u);
            }
            return list;
        }

        // FIX-03 — was a private copy of what ActiveDefence also ran; both lanes now share the one
        // GroundCombatAssemblyTransaction primitive so "did the world really change" is measured
        // identically. Same behaviour, same log line shape.
        private static bool RollbackAssembly(PlayerSetupData player, ArmyData host,
            List<GroundCombatAssemblyTransfer> applied, AiTurnContext ctx) =>
            GroundCombatAssemblyTransaction.Rollback(player, host, applied, ctx, "raid");

        // Was a duplicate of GroundCombatFeasibility.Clears — with a stale hardcoded win-chance
        // threshold and no `cover` output — now calls that shared, parameterized implementation
        // directly at the one call site above (see Docs/ai-duplicate-methods-analysis.md, D4).

        // Was byte-identical in ReconAssignmentPlanner and (twice) in this file — moved to AiV2Util.
        private static ArmyData ResolveArmy(PlayerSetupData player, int armyId) =>
            AiV2Util.ResolveArmy(player, armyId);

        private static string N(float v) => v.ToString("0.##", CultureInfo.InvariantCulture);

        private static AiMapMemory.KnownEnemySighting? FindLiveSighting(PlayerSetupData player, int armyId)
        {
            foreach (AiMapMemory.KnownEnemySighting s in AiMapMemory.AllKnownEnemySightings(player))
                if (s.ArmyId == armyId) return s;
            foreach (AiMapMemory.KnownEnemySighting s in AiMapMemory.AllKnownNeutralSightings(player))
                if (s.ArmyId == armyId) return s;
            return null;
        }
    }
}
