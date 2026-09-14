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
        public int RaidTargetArmyId;
        public HexCoord RaidLastKnownHex;
        public bool RaidTargetIsNeutral;
        public EconomyMissionTarget EconomyTarget;
        public string ReservationOwner;
        public MissionIntentKey? EconomyLoanSource;
        public ResourceVector ClaimedPhysical;
        public float ClaimedAp;
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

        public static ProvisioningResult Ok(ProvisionedMission m, int transferredMemberCount = 0)
        {
            bool changed = transferredMemberCount > 0;
            int version = changed ? V2StateVersion.Bump() : V2StateVersion.Current;
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

        private readonly Dictionary<StableMissionKey, ProvisionedMission> _successful =
            new Dictionary<StableMissionKey, ProvisionedMission>();
        private readonly Dictionary<StableMissionKey, ScoutExecutionCandidate> _assignment =
            new Dictionary<StableMissionKey, ScoutExecutionCandidate>();
        // Round 3 (Problem 2) — the rejection reason ReconAssignmentPlanner.AssignFunded already
        // computed for every Scout mission that got no actor this pass. ClassifyNoAssignment below
        // is now a pure translation of this into a ProvisionFailure — it never re-derives it.
        private readonly Dictionary<StableMissionKey, ScoutAssignmentFailureReason> _assignmentRejections =
            new Dictionary<StableMissionKey, ScoutAssignmentFailureReason>();
        private readonly Dictionary<StableMissionKey, int> _raidAssignment =
            new Dictionary<StableMissionKey, int>();

        public ProvisioningSession(WorldSnapshot snapshot) { Snapshot = snapshot; }
        public IReadOnlyDictionary<StableMissionKey, ProvisionedMission> Successful => _successful;
        public bool AlreadyProvisioned(StableMissionKey k) => _successful.ContainsKey(k);

        public void RegisterSuccess(StableMissionKey k, ProvisionedMission m)
        {
            _successful[k] = m;
            ApClaimed += m.ClaimedAp;
            EnergyClaimed += m.ClaimedEnergy;
            ClaimedArmyIds.Add(m.MoverArmyId);
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

        internal void SetRaidAssignment(Dictionary<StableMissionKey, int> a)
        {
            _raidAssignment.Clear();
            foreach (KeyValuePair<StableMissionKey, int> kv in a)
                _raidAssignment[kv.Key] = kv.Value;
        }

        internal bool TryGetAssignedRaidActor(StableMissionKey k, out int armyId) =>
            _raidAssignment.TryGetValue(k, out armyId);
    }

    internal static class ProvisioningManager
    {
        private static int StealthTransitionApCost => AiConfigV2.scoutOptionalStealthAp;

        // Live structural revalidation uses the same canonical army-role predicate Analysis
        // froze into ArmySnapshot.IsMobileEconomyBuilder.
        private static bool IsMobileEconomyHero(ArmyData army, PlayerSetupData player) =>
            army != null && army.Owner == player && AiArmyRoles.IsHeroLed(army);

        // 2026-09-14 review round 2 — the garrison-extraction container choice is now a pure,
        // read-only RESOLVE step (GarrisonExtractionCandidate) that both the real materialization
        // path (ResolveGarrisonExtractionCandidate + ApplyGarrisonExtraction, called together from
        // the mover-selection loop in ProvisionEconomy) and the FoundBase diagnostic TRACE call —
        // the TRACE prints this struct's fields verbatim instead of re-deriving its own copy of the
        // eligibility logic (the previous copy drifted out of sync with the real gates: it never
        // accounted for ArmyData.RequiresActivationCharge, an existing hero already in a "host"
        // candidate, or ProvisioningSession.ClaimedArmyIds — see
        // Docs/ai-economy-mover-materialization-decision-tree.md).
        private enum GarrisonExtractionTier { None, Shell, Host, Create }

        private readonly struct GarrisonExtractionCandidate
        {
            public readonly GarrisonExtractionTier Tier;
            public readonly UnitData Hero;         // the EXACT UnitData that would be extracted
            public readonly ArmyData Container;    // existing shell/host; null for Create (nothing exists yet)
            public readonly float ApCost;          // Shell/Host: TransferMember's activation charge, if any. Create: CreateArmyApCost.
            public readonly string Reason;          // set only when Tier == None

            private GarrisonExtractionCandidate(GarrisonExtractionTier tier, UnitData hero,
                ArmyData container, float apCost, string reason)
            {
                Tier = tier; Hero = hero; Container = container; ApCost = apCost; Reason = reason;
            }

            public static GarrisonExtractionCandidate No(string reason) =>
                new GarrisonExtractionCandidate(GarrisonExtractionTier.None, null, null, 0f, reason);
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
        private static GarrisonExtractionCandidate ResolveGarrisonExtractionCandidate(
            PlayerSetupData player, ArmyData garrison, ActorCommitments commitments,
            ProvisioningSession session, PlayerRoot root, float ecoApEnvelopeRemaining)
        {
            UnitData sparable = AiArmyRoles.BestSparableEconomyHero(player, garrison);
            if (sparable == null)
                return GarrisonExtractionCandidate.No("no sparable hero in garrison");

            bool Affordable(float apCost) =>
                apCost <= ecoApEnvelopeRemaining + AiConfigV2.allocatorSliceEpsilon
                && (root == null || root.CanSpendActionPoints(Mathf.CeilToInt(apCost)));

            ArmyData shell = ReusableArmySelector.FindReusableAt(player, garrison.Hex, commitments);
            if (shell != null && ArmyActions.CanTransferMembers(
                    new[] { sparable }, garrison, shell, out _))
            {
                float apCost = shell.RequiresActivationCharge(sparable) ? sparable.ActivationApCost : 0f;
                if (Affordable(apCost))
                    return GarrisonExtractionCandidate.Yes(GarrisonExtractionTier.Shell, sparable, shell, apCost);
            }

            foreach (ArmyData host in EconomyHostCandidates(player, garrison, commitments, session))
            {
                if (!ArmyActions.CanTransferMembers(new[] { sparable }, garrison, host, out _))
                    continue;
                float apCost = host.RequiresActivationCharge(sparable) ? sparable.ActivationApCost : 0f;
                if (Affordable(apCost))
                    return GarrisonExtractionCandidate.Yes(GarrisonExtractionTier.Host, sparable, host, apCost);
            }

            // A fresh empty army always has room for the first member (CardPlayExecutor.Preflight
            // makes the same assumption for its own NewArmy path), so no CanTransferMembers probe
            // is possible or needed here — there is no ArmyData yet to probe against.
            if (Affordable(ArmyActions.CreateArmyApCost))
                return GarrisonExtractionCandidate.Yes(
                    GarrisonExtractionTier.Create, sparable, null, ArmyActions.CreateArmyApCost);

            return GarrisonExtractionCandidate.No(
                "no free shell, no eligible host army, and no ECO-axis room left to create one");
        }

        // Turns a resolved GarrisonExtractionCandidate into a real, separate field mover. All
        // three tiers share one transfer primitive (ArmyActions.TransferMember — the exact same
        // safety gate and mutation path Raid's own donor path already trusts in production). A
        // shell minted here via ArmyActions.CreateArmy but never successfully populated is kept,
        // never rolled back (same rule CardPlayExecutor documents for its own NewArmy path) — it
        // simply becomes a future Shell-tier candidate.
        private static ArmyData ApplyGarrisonExtraction(PlayerSetupData player, ArmyData garrison,
            GarrisonExtractionCandidate candidate, AiTurnContext ctx, out UnitData extractedHero)
        {
            extractedHero = null;
            if (candidate.Tier == GarrisonExtractionTier.None)
                return null;

            ArmyData container = candidate.Container;
            if (candidate.Tier == GarrisonExtractionTier.Create)
            {
                FactionCardCatalog catalog = ctx.StartingDeckCatalog?.GetCatalog(player.Faction);
                container = ArmyActions.CreateArmy(player, garrison.Hex, catalog, ctx.HexSelection);
                if (container == null)
                    return null;
            }

            if (!ArmyActions.TransferMember(candidate.Hero, garrison, container, ctx.HexSelection, out string why))
            {
                AiDebugLog.WriteVerbose($"[AI][V2][Economy] garrison hero extraction "
                    + $"({candidate.Tier}) to #{container.Id} failed: {why}"
                    + (candidate.Tier == GarrisonExtractionTier.Create
                        ? " — shell kept as a reusable asset" : ""));
                return null;
            }
            extractedHero = candidate.Hero;
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

        // Recon's counterpart to the Economy ApplyGarrisonExtraction pair above — same safety gate
        // (AiArmyRoles.CanSpareGarrisonMember, via BestSparableGarrisonRecce) and same transfer
        // primitive (ArmyActions.TransferMember into an already-existing free shell), just for a
        // Recce-capable unit/hero instead of a mobile_hero. Still Shell-tier only (no Host/Create
        // fallback) — out of scope for the 2026-09-14 Economy pass; if Recon hits the same
        // no-free-shell dead end, apply the same three-tier fix here. If no shell is free, or the unit no
        // longer qualifies by the time this runs, extraction simply fails here and Recon falls back
        // to its existing card-materialization path, unchanged.
        private static ArmyData TryExtractGarrisonRecceForScouting(PlayerSetupData player,
            ArmyData garrison, AiTurnContext ctx, ProvisioningSession session)
        {
            UnitData sparable = AiArmyRoles.BestSparableGarrisonRecce(player, garrison);
            if (sparable == null)
                return null;
            ActorCommitments commitments = ActorCommitments.FromIntents(
                MissionIntentRegistry.GetOrCreate(player).All
                    .Where(i => i != null && i.Status == IntentStatus.Active).ToList(),
                session?.Snapshot, null);
            ArmyData shell = ReusableArmySelector.FindReusableAt(player, garrison.Hex, commitments);
            if (shell == null)
                return null;
            if (!ArmyActions.TransferMember(sparable, garrison, shell, ctx.HexSelection, out string why))
            {
                AiDebugLog.WriteVerbose(
                    $"[AI][V2][Recon] garrison Recce extraction failed: {why}");
                return null;
            }
            AiDebugLog.Write($"[AI][V2][Recon] extracted idle Recce {sparable.Name} "
                + $"from garrison #{garrison.Id} into #{shell.Id} for scouting duty");
            return shell;
        }

        public static void PreparePass(PlayerSetupData player, PlayerRoot root, AiTurnContext ctx,
            ProvisioningSession session, TentativeAllocation allocation,
            ActorCommitments durableCommitments = null)
        {
            PrepareScoutAssignments(player, root, ctx, session, allocation, durableCommitments);
            PrepareRaidAssignments(session, allocation);
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

        private static void PrepareRaidAssignments(ProvisioningSession session, TentativeAllocation allocation)
        {
            var open = new List<FundedEntry>();
            if (allocation?.Funded != null)
                foreach (FundedEntry fe in allocation.Funded)
                {
                    if (fe?.Mission == null || fe.Mission.Kind != MissionKind.Raid
                        || session.AlreadyProvisioned(StableMissionKey.For(fe.Mission)))
                        continue;
                    open.Add(fe);
                }
            open.Sort((a, b) => a.Priority.CompareTo(b.Priority));

            var cands = new List<List<int>>(open.Count);
            foreach (FundedEntry fe in open)
            {
                var ids = new List<int>();
                if (RaidAdmissionRegistry.TryGet(fe.Mission, out HashSet<int> eligible))
                    ids.AddRange(eligible.Where(id => !session.ClaimedArmyIds.Contains(id))
                        .OrderBy(id => RaidActorActivation(session.Snapshot, id))
                        .ThenBy(id => RaidActorPower(session.Snapshot, id))
                        .ThenBy(id => id));
                cands.Add(ids);
            }

            var chosen = new int[open.Count];
            var best = new int[open.Count];
            for (int i = 0; i < best.Length; i++) best[i] = -1;
            long[] bestKey = null;
            RecurseRaid(0, open, cands, chosen, new HashSet<int>(), session.Snapshot, ref bestKey, best);

            var map = new Dictionary<StableMissionKey, int>();
            for (int i = 0; i < open.Count; i++)
                if (best[i] >= 0)
                    map[StableMissionKey.For(open[i].Mission)] = cands[i][best[i]];
            session.SetRaidAssignment(map);

            if (open.Count > 0)
                AiDebugLog.Write($"[AI][V2]   provision prepare raid — {open.Count} open, assigned ["
                    + string.Join(" ", map.Select(kv => $"{kv.Key}->#{kv.Value}")) + "]");
        }

        private static int RaidActorActivation(WorldSnapshot snap, int id)
        {
            ArmySnapshot a = snap?.Self?.Armies?.FirstOrDefault(x => x != null && x.ArmyId == id);
            return a == null || a.HasActivatedThisTurn ? 0 : a.ActivationApCost;
        }

        private static float RaidActorPower(WorldSnapshot snap, int id) =>
            snap?.Self?.Armies?.FirstOrDefault(x => x != null && x.ArmyId == id)?.EffectiveArmyPower ?? float.MaxValue;

        private static void RecurseRaid(int i, List<FundedEntry> open, List<List<int>> cands,
            int[] chosen, HashSet<int> usedArmyIds, WorldSnapshot snap, ref long[] bestKey, int[] best)
        {
            if (i == open.Count)
            {
                long[] key = ScoreRaidAssignment(open, cands, chosen, snap);
                if (bestKey == null || Lex(key, bestKey) < 0)
                {
                    bestKey = key;
                    Array.Copy(chosen, best, chosen.Length);
                }
                return;
            }

            chosen[i] = -1;
            RecurseRaid(i + 1, open, cands, chosen, usedArmyIds, snap, ref bestKey, best);
            for (int c = 0; c < cands[i].Count; c++)
            {
                int aid = cands[i][c];
                if (usedArmyIds.Contains(aid)) continue;
                usedArmyIds.Add(aid);
                chosen[i] = c;
                RecurseRaid(i + 1, open, cands, chosen, usedArmyIds, snap, ref bestKey, best);
                usedArmyIds.Remove(aid);
            }
            chosen[i] = -1;
        }

        private static long[] ScoreRaidAssignment(List<FundedEntry> open, List<List<int>> cands,
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
                activation += RaidActorActivation(snap, actorId);
                overkillPower += Mathf.RoundToInt(RaidActorPower(snap, actorId) * 100f);
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

            if (m.Kind == MissionKind.Economy && m.Target is EconomyMissionTarget economy)
                return ProvisionEconomy(player, root, hand, ctx, session, funded, economy);

            if (m.Kind != MissionKind.Scout || !(m.Target is ScoutMissionTarget target))
                return ProvisioningResult.Fail(ProvisionFailure.AssemblyInfeasible("unsupported mission kind"));

            StableMissionKey key = StableMissionKey.For(m);
            bool surveil = target.Kind == ScoutTargetKind.Surveil;
            bool refresh = ReconScoutKinds.IsRefresh(target.Kind);

            if (!session.TryGetAssignedExecution(key, out ScoutExecutionCandidate exec))
                return ClassifyNoAssignment(session, key, target);

            if (exec.ExecutorKind != ScoutExecutorKind.Ground)
                return ProvisionAir(player, root, ctx, session, funded, exec, target, key);

            int moverArmyId = exec.Army.ArmyId;
            ArmyData army;
            if (exec.Army.RequiresGarrisonExtraction)
            {
                ArmyData garrisonArmy = ResolveArmy(player, moverArmyId);
                army = garrisonArmy == null
                    ? null : TryExtractGarrisonRecceForScouting(player, garrisonArmy, ctx, session);
            }
            else
            {
                army = ResolveArmy(player, moverArmyId);
            }
            if (army == null || army.Owner != player || army.Members.Count == 0
                || !AiArmyRoles.IsSoloRecce(army) || army.CurrentMovement <= 0)
                return ProvisioningResult.Fail(ProvisionFailure.MoverContended(
                    $"assigned mover #{moverArmyId} is no longer a usable solo Recce"));

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
                if (ScoutExecutionSafety.VantageBlockedNow(player, executionHex, ctx.TurnNumber))
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

            HexCoord? firstStep = SafeStepPathing.FindNextSafeStep(ctx.Map, army, executionHex);
            if (firstStep == null)
                return ProvisioningResult.Fail(ProvisionFailure.NoExecutableStep(
                    $"no safe first step from ({army.Hex.Q},{army.Hex.R}) toward ({executionHex.Q},{executionHex.R})"));

            int activationAp = army.HasActivatedThisTurn ? 0 : army.ActivationApCost;
            bool alreadyHidden = army.Members.Any(mem => mem.IsHidden);
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
            });
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

        private static ProvisioningResult ProvisionEconomy(PlayerSetupData player, PlayerRoot root,
            AiHandData hand, AiTurnContext ctx, ProvisioningSession session, FundedEntry funded,
            EconomyMissionTarget target)
        {
            MissionProposal m = funded.Mission;
            StableMissionKey key = StableMissionKey.For(m);
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
            DemandLayer.EconomyBuilderChoice builderChoice = null;
            ArmyData hero = null;
            ArmyData extractionSourceGarrison = null;
            UnitData extractionHeroUnit = null;           // the EXACT unit that left the garrison
            bool extractionCreatedContainer = false;      // true once ArmyActions.CreateArmy ran (never rolled back)
            float extractionExtraApSpent = 0f;
            float ecoApEnvelopeRemaining = funded.Tentative.Ap;
            foreach (DemandLayer.EconomyBuilderChoice candidate in eligibleBuilders
                .OrderBy(x => m.PreferredMoverArmyId == x.Route.ArmyId ? 0 : 1)
                .Where(IsCandidateEligible))
            {
                ArmyData candidateHero;
                ArmyData candidateGarrison = null;
                UnitData candidateExtractedUnit = null;
                float candidateExtraApSpent = 0f;
                bool candidateCreatedContainer = false;
                if (candidate.Route.RequiresGarrisonExtraction)
                {
                    candidateGarrison = ResolveArmy(player, candidate.Route.ArmyId);
                    if (candidateGarrison != null)
                    {
                        GarrisonExtractionCandidate plan = ResolveGarrisonExtractionCandidate(
                            player, candidateGarrison, actorCommitments, session,
                            root, ecoApEnvelopeRemaining);
                        candidateCreatedContainer = plan.Tier == GarrisonExtractionTier.Create;
                        candidateExtraApSpent = plan.ApCost;
                        candidateHero = ApplyGarrisonExtraction(
                            player, candidateGarrison, plan, ctx, out candidateExtractedUnit);
                    }
                    else
                        candidateHero = null;
                }
                else
                {
                    candidateHero = ResolveArmy(player, candidate.Route.ArmyId);
                }
                if (candidateHero != null)
                {
                    extractionSourceGarrison = candidate.Route.RequiresGarrisonExtraction
                        ? candidateGarrison : null;
                    extractionHeroUnit = candidateExtractedUnit;
                    extractionCreatedContainer = candidateCreatedContainer;
                    extractionExtraApSpent = candidateExtraApSpent;
                    builderChoice = candidate;
                    hero = candidateHero;
                    break;
                }
            }

            // 2026-09-14 fix — ApplyGarrisonExtraction (inside the loop above) is a real,
            // authoritative mutation: ArmyActions.TransferMember pulling the hero out of the
            // garrison into whichever container ResolveGarrisonExtractionCandidate found (an
            // existing shell, an existing field army it borrowed, or one it just paid to create —
            // see those methods' own headers). Every check from here down to the
            // ApplyEconomyArmyLightening call below is still a pure affordability/feasibility check
            // on `hero`, not a further mutation of its roster — if any of them fails, the hero must
            // go back to the garrison in the same attempt, or a same-turn reprice retry
            // (ResourceAllocator's EnvelopeTooSmall -> RepriceThisTurn loop) finds the container it
            // used already spent/claimed and reports NoMoverExists even though a hero really was
            // available. FailAfterHero is the single point every such early return below goes
            // through so the rollback can never be forgotten at a future call site. It reverses
            // ONLY the exact tracked extractionHeroUnit (never re-derived by scanning `hero.Members`
            // for "the" hero — a container borrowed via the Host tier can, in principle, already
            // carry a different hero, and re-deriving would risk sending the WRONG one back). A
            // container minted via ArmyActions.CreateArmy along the way keeps its already-spent AP
            // and is simply left behind as a future reusable shell (same "never roll back a paid
            // empty army" rule CardPlayExecutor's own NewArmy path already documents) — so a Create
            // extraction always reports a real, permanent StateChanged even when the hero itself
            // rolls back cleanly.
            ProvisioningResult FailAfterHero(ProvisionFailure failure)
            {
                if (extractionHeroUnit == null)
                    return ProvisioningResult.Fail(failure, extractionCreatedContainer, extractionCreatedContainer ? 1 : 0);
                bool rolledBack = ArmyActions.TransferMember(
                    extractionHeroUnit, hero, extractionSourceGarrison, ctx.HexSelection, out string why);
                if (!rolledBack)
                    AiDebugLog.Write($"[AI][V2][Economy] garrison extraction rollback FAILED "
                        + $"#{hero.Id}->#{extractionSourceGarrison.Id}: {why}");
                bool stateChanged = extractionCreatedContainer || !rolledBack;
                return ProvisioningResult.Fail(failure, stateChanged, stateChanged ? 1 : 0);
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
                            AiDebugLog.Write($"[AI][V2][Economy][TRACE]   #{x.Route.ArmyId} (garrison-extraction) "
                                + $"resolved={g != null} sparableHero={sparable != null} "
                                + $"notUnderImmediateThreat={cThreatG} notClaimedThisPass={cClaimedG} hasPath={cPathG} "
                                + $"container={(cContainerG ? containerPlan.Tier.ToString() : containerPlan.Reason)} "
                                + (cContainerG ? $"containerApCost={containerPlan.ApCost:0.##} " : "")
                                + $"=> ELIGIBLE={g != null && sparable != null && cThreatG && cClaimedG && cPathG && cContainerG}");
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
                        AiDebugLog.Write($"[AI][V2][Economy][TRACE]   #{a.Id} hex=({a.Hex.Q},{a.Hex.R}) "
                            + $"currentMovement={a.CurrentMovement} maxMovement={a.MaxMovement} "
                            + $"isMobileEconomyHero={cMobile} notUnderImmediateThreat={cThreat} "
                            + $"notClaimedThisPass={cClaimed} noConflictingIntent={cDonorConflict}"
                            + (conflicting != null ? $" (conflictsWith={conflicting.IntentKey} kind={conflicting.Kind} status={conflicting.Status})" : "")
                            + $" atTargetHex={atTarget} hasSafeNextStep={(atTarget ? (object)"n/a" : nextStep.HasValue)} "
                            + $"=> ELIGIBLE={cMobile && cThreat && cClaimed && cDonorConflict && cPath}");
                    }
                }
                return ProvisioningResult.Fail(ProvisionFailure.NoMoverExists(
                    "no free hero can advance toward economy site"));
            }

            MissionIntent donor = standingIntents.FirstOrDefault(i => i != null
                && i.Kind != MissionKind.Economy && i.PreferredMoverArmyId == hero.Id
                && DemandLayer.EconomyDonorStructurallyEligible(i));
            int distance = SafeStepPathing.FindSafePathCost(ctx.Map, hero, target.TargetHex);
            if (distance == int.MaxValue)
                return FailAfterHero(ProvisionFailure.NoExecutableStep("no safe economy route"));
            if (donor != null)
            {
                if (!DemandLayer.EconomyLoanAllowed(donor, target.BuildValue, distance,
                        hero.CurrentMovement, out float loanNet))
                    return FailAfterHero(ProvisionFailure.MoverContended(
                        $"loan rejected donor={donor.IntentKey} distance={distance} move={hero.CurrentMovement} net={loanNet:0.##}"));
            }

            bool travelNeeded = !hero.Hex.Equals(target.TargetHex);
            bool completionThisTurn = distance <= hero.CurrentMovement;
            ResourceCost stageCost = completionThisTurn ? target.BuildResourceCost : null;
            List<UnitData> lighteningPlan = PlanEconomyArmyLightening(
                player, hero, target.TargetHex, session.Snapshot, ctx,
                builderChoice.MinimumEscortCount, out ArmyData garrison,
                out List<UnitData> reinforcementPlan);
            if (builderChoice.Suitability == DemandLayer.EconomyArmySuitability.ReinforceAtBase
                && reinforcementPlan.Count == 0)
                return FailAfterHero(ProvisionFailure.AssemblyInfeasible(
                    $"economy builder #{hero.Id} no longer has its planned minimum escort"));
            float realAp = EconomyMissionClaimedAp(hero, target.BuildApCost,
                target.MinimumFollowupAp, lighteningPlan, reinforcementPlan,
                travelNeeded, completionThisTurn);
            // extractionExtraApSpent (Variant A — ArmyActions.CreateArmy) is real AP already
            // deducted from root.ActionPoints by the time we get here, so it must count against
            // the ECO-axis ENVELOPE (a separate, static budget number that does not shrink on its
            // own) but must NOT be added again to the raw-pool check below — root.ActionPoints
            // already reflects that spend, adding it twice would demand the same 2 AP be free
            // a second time.
            if (realAp + extractionExtraApSpent > funded.Tentative.Ap + AiConfigV2.allocatorSliceEpsilon)
                return FailAfterHero(ProvisionFailure.EnvelopeTooSmall(realAp + extractionExtraApSpent,
                    $"economy hero #{hero.Id} needs {realAp:0.##} AP for "
                    + (completionThisTurn ? "delivery + completion" : "this travel stage")
                    + (extractionExtraApSpent > 0f ? $" (+{extractionExtraApSpent:0.##} already spent creating its army)" : "")));
            if (realAp > root.ActionPoints - session.ApClaimed + AiConfigV2.allocatorSliceEpsilon)
                return FailAfterHero(ProvisionFailure.MoverContended("economy AP no longer available"));

            string owner = EconomyMissionPlanner.OwnerKey(key);
            if (!StrategicSpendability.FitsSpendableResources(player, root, ctx,
                    stageCost, owner))
                return FailAfterHero(ProvisionFailure.EnvelopeTooSmall(
                    new ProvisionRequirement(realAp, CostVector(stageCost)),
                    "economy completion resources no longer spendable"));
            int preparedMembers = ApplyEconomyArmyLightening(
                hero, garrison, lighteningPlan, reinforcementPlan, ctx);
            int plannedTransfers = lighteningPlan.Count + reinforcementPlan.Count;
            if (preparedMembers != plannedTransfers)
                return FailAfterHero(ProvisionFailure.AssemblyInfeasible(
                    $"economy builder #{hero.Id} composition transaction did not commit"));
            // Atomic transfer failure leaves the original roster intact, so claim its real live AP
            // rather than the projected lighter value.
            realAp = EconomyMissionClaimedAp(hero, target.BuildApCost,
                target.MinimumFollowupAp, null, travelNeeded, completionThisTurn);
            // 2026-09-14 review round 2 — this was a plain ProvisioningResult.Fail, the one early
            // return past the extraction that did NOT go through FailAfterHero. The window is
            // narrow (preparedMembers already matched plannedTransfers above, so this recompute
            // almost always agrees with the estimate that already cleared the envelope check
            // earlier), but "almost never" still leaves the hero permanently outside the garrison
            // with the demand reported as untouched. Note: this only reverses the hero's own
            // transfer, same as every other FailAfterHero call — a lightening/reinforcement batch
            // that already ran above is not itself reversed here (ApplyEconomyArmyLightening's own
            // atomicity already guarantees the ONLY way to reach this line is a fully-applied
            // batch, so there is no partial roster to unwind).
            if (realAp + extractionExtraApSpent > funded.Tentative.Ap + AiConfigV2.allocatorSliceEpsilon)
                return FailAfterHero(ProvisionFailure.EnvelopeTooSmall(realAp + extractionExtraApSpent,
                    "economy army could not be lightened within the funded AP envelope"));
            if (completionThisTurn)
                InfrastructureFulfillment.ReserveEconomyCost(player, ctx.TurnNumber, owner,
                    target.BuildResourceCost, target.BuildApCost);

            MissionIntent loan = donor;
            if (loan != null)
            {
                loan.Status = IntentStatus.Suspended;
                loan.Suspended = SuspendReason.EconomyLoan;
                AiDebugLog.Write($"[AI][V2][Economy][Loan] borrow actor=#{hero.Id} from={loan.IntentKey} to={key}");
            }

            return ProvisioningResult.Ok(new ProvisionedMission
            {
                Mission = m, Key = key, Kind = MissionKind.Economy,
                MoverArmyId = hero.Id, FocusHex = target.TargetHex,
                ExecutionHex = target.TargetHex, EconomyTarget = target,
                // 2026-09-14 review round 2 — extractionExtraApSpent must NOT be folded into
                // ClaimedAp: it is AP ArmyActions.TransferMember/CreateArmy already deducted for
                // real from root.ActionPoints during extraction above (the local envelope check
                // just above already validated it against funded.Tentative.Ap at the point it was
                // spent). ClaimedAp feeds ProvisioningSession.RegisterSuccess -> session.ApClaimed,
                // which every OTHER mission this pass checks against "root.ActionPoints -
                // session.ApClaimed" — root.ActionPoints already reflects the extraction spend, so
                // adding it into ClaimedAp too would subtract it a second time and starve later
                // Recon/Economy/Development attempts this same turn for no reason.
                ClaimedAp = realAp, ClaimedPhysical = CostVector(stageCost),
                ReservationOwner = owner,
                EconomyLoanSource = loan?.IntentKey,
                // The extraction itself (hero leaving the garrison into a new/borrowed container)
                // is a real change even when lightening/reinforcement moved nobody else — count it
                // so StateChanged/V2StateVersion honestly reflect it either way.
            }, preparedMembers + (extractionHeroUnit != null ? 1 : 0));
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
                && i.PreferredMoverArmyId == builder.Id))
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

        private static int ApplyEconomyArmyLightening(ArmyData builder,
            ArmyData garrison, IReadOnlyList<UnitData> unload,
            IReadOnlyList<UnitData> reinforcement, AiTurnContext ctx)
        {
            if (builder == null || garrison == null)
                return 0;
            if (unload.Count > 0 && !ArmyActions.TransferMembersAtomic(
                    unload, builder, garrison, ctx.HexSelection, out string whyUnload))
            {
                if (!string.IsNullOrEmpty(whyUnload))
                    AiDebugLog.WriteVerbose($"[AI][V2][Economy] builder lighten skipped: {whyUnload}");
                return 0;
            }
            if (reinforcement.Count > 0 && !ArmyActions.TransferMembersAtomic(
                    reinforcement, garrison, builder, ctx.HexSelection, out string whyAdd))
            {
                if (!string.IsNullOrEmpty(whyAdd))
                    AiDebugLog.WriteVerbose($"[AI][V2][Economy] builder reinforce skipped: {whyAdd}");
                return unload.Count;
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
                travelNeeded: true, completionThisTurn: true);

        internal static float EconomyMissionClaimedAp(ArmyData builder, float buildApCost,
            float minimumFollowupAp, IReadOnlyCollection<UnitData> unloaded,
            bool travelNeeded, bool completionThisTurn)
            => EconomyMissionClaimedAp(builder, buildApCost, minimumFollowupAp,
                unloaded, added: null, travelNeeded, completionThisTurn);

        internal static float EconomyMissionClaimedAp(ArmyData builder, float buildApCost,
            float minimumFollowupAp, IReadOnlyCollection<UnitData> unloaded,
            IReadOnlyCollection<UnitData> added, bool travelNeeded, bool completionThisTurn)
        {
            float activation = 0f;
            if (travelNeeded && builder != null && !builder.HasActivatedThisTurn)
                activation = builder.Members
                    .Where(u => u != null && (unloaded == null || !unloaded.Contains(u)))
                    .Concat(added ?? System.Array.Empty<UnitData>())
                    .Distinct().Sum(u => u.ActivationApCost);
            float completion = completionThisTurn
                ? Mathf.Max(buildApCost, minimumFollowupAp) : 0f;
            return activation + completion;
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

        private static ResourceVector CostVector(ResourceCost cost) => cost == null
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

            AiMapMemory.KnownEnemySighting? sighting = FindLiveSighting(player, target.TargetArmyId);
            if (sighting == null)
            {
                if (RaidObjectiveEvaluator.IsObjectiveSatisfiedLive(player, target.TargetArmyId))
                    return ProvisioningResult.Fail(ProvisionFailure.TargetSatisfied(
                        $"raid target #{target.TargetArmyId} no longer exists (destroyed / captured)"));
                return ProvisioningResult.Fail(ProvisionFailure.TargetInvalidated(
                    $"raid target #{target.TargetArmyId} has no current honest sighting; absence is not proof of destruction"));
            }
            if (sighting.Value.Owner != null && !sighting.Value.Owner.IsNeutral && sighting.Value.Owner.Equals(player))
                return ProvisioningResult.Fail(ProvisionFailure.TargetSatisfied(
                    $"raid target #{target.TargetArmyId} is now ours"));

            HexCoord targetHex = sighting.Value.Hex;
            IReadOnlyList<WorthIt.DefenderProfile> defenders =
                sighting.Value.Defenders ?? System.Array.Empty<WorthIt.DefenderProfile>();

            RaidAssemblyPlan plan = null;
            if (session.TryGetAssignedRaidActor(key, out int assignedActor)
                && !session.ClaimedArmyIds.Contains(assignedActor))
            {
                RaidAssemblyPlan assigned = RaidAssemblyPlanner.PlanForArmy(snap, target, defenders, assignedActor);
                if (assigned.Feasible) plan = assigned;
            }
            if (plan == null)
                plan = RaidAssemblyPlanner.Plan(snap, target, defenders, session.ClaimedArmyIds);

            if (!plan.Feasible)
            {
                RaidAssemblyPlan unrestricted = RaidAssemblyPlanner.Plan(snap, target, defenders, null);
                if (unrestricted.Feasible)
                    return ProvisioningResult.Fail(ProvisionFailure.MoverContended(
                        $"raid target #{target.TargetArmyId} has an executable force but its host/donor is already claimed; {plan.Reason}"));
                return ProvisioningResult.Fail(ProvisionFailure.AssemblyInfeasible(plan.Reason));
            }

            ArmyData host = ResolveArmy(player, plan.BaseArmyId);
            if (host == null || host.Members.Count == 0 || host.CurrentMovement <= 0
                || host.IsPrison || host.IsAirfield || AviationRules.IsAirArmy(host)
                || AiArmyRoles.IsSoloRecce(host) || AiArmyRoles.IsSoloHeroAwaitingEscort(host))
                return ProvisioningResult.Fail(ProvisionFailure.MoverContended(
                    $"raid host #{plan.BaseArmyId} is no longer a usable ground combat army"));
            if (host.Owner != player || session.ClaimedArmyIds.Contains(host.Id))
                return ProvisioningResult.Fail(ProvisionFailure.MoverContended(
                    $"raid host #{plan.BaseArmyId} was claimed by an earlier mission this cycle"));

            var transfers = new List<RaidAssemblyTransfer>();
            var claimedDonors = new HashSet<int>();
            var projectedUnits = new List<UnitData>(host.Members);
            if (plan.NeedsAssembly)
            {
                int heroTransfers = 0;
                foreach (RaidAssemblyTransfer t in plan.Transfers)
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
                if (!RaidCombatFeasibility.Clears(projectedProfiles, defenders, out float projectedWin, out _))
                    return ProvisioningResult.Fail(ProvisionFailure.AssemblyInfeasible(
                        "planned same-hex roster no longer clears the shared WorthIt estimator"));
                plan.ProjectedWinChance = projectedWin;
            }

            if (SafeStepPathing.FindNextSafeStep(ctx.Map, host, targetHex) == null)
                return ProvisioningResult.Fail(ProvisionFailure.NoExecutableStep(
                    $"no safe first step from ({host.Hex.Q},{host.Hex.R}) toward raid target ({targetHex.Q},{targetHex.R})"));

            int activationAp = host.HasActivatedThisTurn ? 0 : host.ActivationApCost;
            float envelope = funded.Tentative.Ap;
            if (activationAp > envelope + eps)
                return ProvisioningResult.Fail(ProvisionFailure.EnvelopeTooSmall(activationAp,
                    $"raid host #{host.Id} needs {N(activationAp)} AP, envelope is {N(envelope)}"));
            float turnApLeft = root.ActionPoints - session.ApClaimed;
            if (activationAp > turnApLeft + eps)
                return ProvisioningResult.Fail(ProvisionFailure.MoverContended(
                    $"turn AP exhausted: raid needs {N(activationAp)}, {N(turnApLeft)} left"));

            var applied = new List<RaidAssemblyTransfer>();
            foreach (RaidAssemblyTransfer t in transfers)
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

            foreach (int d in claimedDonors)
                session.ClaimedArmyIds.Add(d);

            AiDebugLog.Write($"[AI][V2]   raid provision [{m.AttemptId}] {key} — OK host #{host.Id} "
                + $"{(plan.NeedsAssembly ? $"(+{transfers.Count} body from {claimedDonors.Count} donor) " : "")}" 
                + $"win~{plan.ProjectedWinChance.ToString("0.00", CultureInfo.InvariantCulture)} "
                + $"ap {N(activationAp)} -> ({targetHex.Q},{targetHex.R})");

            return ProvisioningResult.Ok(new ProvisionedMission
            {
                Mission = m,
                Key = key,
                Kind = MissionKind.Raid,
                MoverArmyId = host.Id,
                FocusHex = targetHex,
                ExecutionHex = targetHex,
                RaidTargetArmyId = target.TargetArmyId,
                RaidLastKnownHex = targetHex,
                RaidTargetIsNeutral = sighting.Value.Owner != null && sighting.Value.Owner.IsNeutral,
                ClaimedPhysical = funded.PhysicalDraw,
                ClaimedAp = activationAp,
                StealthApReserved = false,
            }, applied.Count);
        }

        private static bool RollbackAssembly(PlayerSetupData player, ArmyData host,
            List<RaidAssemblyTransfer> applied, AiTurnContext ctx)
        {
            bool ok = true;
            for (int i = applied.Count - 1; i >= 0; i--)
            {
                RaidAssemblyTransfer t = applied[i];
                ArmyData donor = ResolveArmy(player, t.DonorArmyId);
                string why = donor == null ? "donor missing" : !host.Members.Contains(t.Unit) ? "unit no longer in host" : null;
                if (donor == null || !host.Members.Contains(t.Unit)
                    || !ArmyActions.TransferMember(t.Unit, host, donor, ctx.HexSelection, out why))
                {
                    ok = false;
                    AiDebugLog.Write($"[AI][V2]   raid assembly rollback — FAILED {t.Unit?.Name} "
                        + $"host #{host.Id}->donor #{t.DonorArmyId}: {why}");
                }
            }
            return ok;
        }

        // Was a duplicate of RaidCombatFeasibility.Clears — with a stale hardcoded win-chance
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
