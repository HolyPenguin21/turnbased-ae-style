using System.Collections.Generic;
using System.Linq;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Units;

using Game.Combat;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  RAID ASSEMBLY PLANNER  (Strategy V2 build-order step 9 — the raid-force solver)
    // ===========================================================================================
    //  Target discovery/value stays snapshot-driven, but our own force is authoritative live state.
    //  The solver first prefers an already-sufficient army. If none exists it may build a minimal
    //  same-hex package by taking AT MOST ONE safe non-hero body from each donor. A donor is never
    //  emptied and every selected body is retained in the plan so Provisioning can transactionally
    //  revalidate/apply exactly the roster that passed WorthIt here.
    //
    //  ARCH-02 §29/§31 — this class is the constrained physical ASSEMBLY SOLVER only. Actor
    //  eligibility is GroundCombatActorEligibility; the WorthIt win/coverage check is GroundCombatFeasibility;
    //  same-hex donor legality is GroundCombatDonorPolicy; the fresh-vs-continuation win gates are
    //  GroundCombatAdmissionPolicy. It computes no strategic objective value.
    // ===========================================================================================
    public sealed class GroundCombatAssemblyTransfer
    {
        public int DonorArmyId;
        public UnitData Unit;
    }

    public sealed class GroundCombatAssemblyPlan
    {
        public bool Feasible;
        public string Reason;
        public int BaseArmyId;
        public bool NeedsAssembly;
        public readonly List<int> MergeArmyIds = new List<int>();
        public readonly List<GroundCombatAssemblyTransfer> Transfers = new List<GroundCombatAssemblyTransfer>();
        public float ProjectedWinChance;
        public bool CoversAllDefenders;
        public float ProjectedPower;
        // 2026-09-30 — the plan takes a garrison hero (AiArmyRoles.IsGarrisonHero) as its
        // fallback commander; the proposal carries its price (GarrisonHeroFallbackCost).
        public bool UsesGarrisonHero;
        // The win-chance gate this plan was admitted at (set where the plan is built against a
        // gate): any later re-check of the same plan asks the same question, never a stricter one.
        public float WinChanceGate;

        public static GroundCombatAssemblyPlan Infeasible(string reason) =>
            new GroundCombatAssemblyPlan { Feasible = false, Reason = reason };
    }

    // Audit F7 — a cross-hex gather: the host holds, the supports walk to it and hand over.
    public sealed class GroundCombatGatherPlan
    {
        public bool Feasible;
        public string Reason;
        public int HostArmyId;
        public HexCoord HostHex;
        // Planned supports, critical path first (longest walk to the host).
        public readonly List<int> SupportArmyIds = new List<int>();
        public float ProjectedWinChance;
        public bool CoversAllDefenders;
        public float ProjectedPower;
        // Turns until the slowest support stands on the host's hex.
        public int GatherTurns;
        // Turns the assembled roster needs from the host's hex to the target.
        public int AssaultEta;
        // Σ (support activation × walking turns + its handoff charge) + assembled activation ×
        // assault turns.
        public int TotalAp;
        // The gather stage alone: Σ (support walks + handoff charge). A gather STEP is priced with
        // this, never with the whole operation (project owner, 2026-10-02): the assault march is
        // paid when it is actually taken, turn by turn, like any other multi-turn work.
        public int GatherAp;
        // The TaskScore value of the operations the plan's bought supports abandon
        // (MissionIntent.DisplacementValue) — the gather's MoverOpportunityCost, never AP.
        public float DisplacedValue;
        // What the planner minimises when it compares hosts: AP plus the abandoned value in
        // AP-equivalents (ActionPrice.FromTaskScore). Planning only; the score keeps the two in
        // their own slots.
        public float SelectionCost =>
            TotalAp + ActionPrice.FromTaskScore(DisplacedValue);
        // The part of TotalAp paid THIS turn: the activation of every planned support that can
        // act now and has not activated yet. The legs, not the host, are what move first — a
        // host that already spent its own activation elsewhere makes the gather no cheaper.
        public int CurrentTurnAp;
        // False only for a T01 preparation plan (PlanGather allowPartial): the chosen supports
        // raise the host but do not yet take it past the power threshold / coverage.
        public bool ReachesThreshold = true;
        // Everything still to be paid on later turns (the rest of the walks, handoffs, assault).
        public int FutureAp => System.Math.Max(0, TotalAp - CurrentTurnAp);
        public int GatherFutureAp => System.Math.Max(0, GatherAp - CurrentTurnAp);
        public int TotalEta => GatherTurns + AssaultEta;

        public static GroundCombatGatherPlan Infeasible(string reason) =>
            new GroundCombatGatherPlan { Feasible = false, Reason = reason };
    }

    internal static class GroundCombatAdmissionPolicy
    {
        // Fresh admission still requires the strict raidMinViableWinChance (0.80, 2026-10-01).
        internal static float FreshStartWinChanceGate => AiConfigV2.raidMinViableWinChance;

        // Once a Hard raid has actually left its staging hex, small Monte-Carlo variance / loss of
        // same-hex donor availability must not instantly turn the incumbent actor into a structural
        // AssemblyInfeasible failure. The assigned incumbent may continue while it still covers
        // every known defender and keeps at least this lower safety floor (0.55 since the fresh gate
        // rose to 0.80, 2026-10-01; it was 0.40 under 0.65). It is intentionally
        // conservative: it fixes the observed 0.78-start -> ~0.41-next-turn discontinuity without
        // authorising a clearly hopeless attack. Fresh raids never see this floor.
        internal const float ContinuationWinChanceFloor = 0.55f;

        // Attack imposes no probability threshold; whether it checks known defender coverage is
        // RequiresCoverage's answer (AiConfigV2.attackRequiresDefenderCoverage).
        internal const float AttackCoverageGate = 0f;

        // Does a fight admitted at `gate` require every known defender to be damageable? Always
        // for Raid / ActiveDefence; for Attack's no-threshold gate only while the test switch is on.
        internal static bool RequiresCoverage(float gate) =>
            gate > AttackCoverageGate || AiConfigV2.attackRequiresDefenderCoverage;

        // The gate an assigned assault actor is (re)planned at: Attack's floor; otherwise the
        // continuation floor for the pinned Hard incumbent and the fresh gate for anything new.
        internal static float AssaultGate(MissionProposal proposal, int actorId) =>
            proposal != null && proposal.Kind == MissionKind.Attack
                ? proposal.Target is AttackMissionTarget attack && attack.IsIntermediateAssault
                    ? FreshStartWinChanceGate : AttackCoverageGate
            : PinnedOrFreshGate(proposal != null && proposal.FromDurableIntent
                && proposal.DurableFundingTier == CommitmentTier.Hard
                && proposal.PreferredMoverArmyId == actorId);

        // Raid / ActiveDefence: the continuation floor for the durable operation's own pinned
        // actor, the fresh gate for anything new. Every stage selects through this one predicate.
        internal static float PinnedOrFreshGate(bool continuesPinnedOperation) =>
            continuesPinnedOperation ? ContinuationWinChanceFloor : FreshStartWinChanceGate;

        // Raid: a started operation stays in Assault down to the continuation floor; every other
        // phase (and an unstarted one) returns to Assault only at the fresh gate. Continuity's
        // phase machine and the Demand layer's shortage test both read this, so a shortage is
        // reported exactly when Continuity will not let the primary assault on its own.
        internal static float RaidPrimaryGate(RaidIntent raid) =>
            PinnedOrFreshGate(raid != null && raid.OperationStarted
                && raid.Phase == RaidMissionPhase.Assault);
    }

    // The generalized ground-combat assembly REQUEST. The kernel below is shared by Raid,
    // Attack and ActiveDefence; nothing Raid-specific
    // (target merit, phase transitions, cooldowns) lives inside it. Every field is an explicit
    // constraint the caller owns.
    public sealed class GroundCombatAssemblyRequest
    {
        // The opposition the assembled force must beat: every defending army with its own
        // commander (AiV2Util.KnownOpposition / AttackObjectiveEvaluator.KnownSiteOpposition).
        // Empty == "no fight to win" (a pure escort / rendezvous request), which trivially clears.
        public IReadOnlyList<WorthIt.DefendingArmy> Opposition =
            System.Array.Empty<WorthIt.DefendingArmy>();
        // Fresh-start vs continuation win gate. Callers pass GroundCombatAdmissionPolicy's two constants.
        public float WinChanceGate = AiConfigV2.raidMinViableWinChance;
        // When set, this actor is tried first and (with PinToPreferred) exclusively.
        public int? PreferredPrimaryArmyId;
        public bool PinToPreferred;
        // Armies excluded because they are busy / already claimed this cycle.
        public ISet<int> ExcludedArmyIds;
        // Staging restriction: only armies standing on this hex may host the formation.
        public HexCoord? StagingHex;
        // May the kernel build a package out of same-hex donors, or must it find an
        // already-sufficient army?
        public bool AllowSameHexAssembly = true;
        // The result must be an army OTHER than PreferredPrimaryArmyId (a separate support actor).
        public bool RequiresSeparateSupportActor;
        // ATK §29/§30 — the defence bonus the DEFENDERS enjoy where the fight will happen
        // (structural + terrain), as the honest knowledge read AiMapMemory.KnownHexDefenseBonus
        // owns. Mission-agnostic: a field battle passes 0f, an assault on a known Base/Citadel
        // passes what memory actually observed. Never read live from BuildingRegistry here.
        public float DefenderHexDefenseBonus;
        // Attack fresh admission only. The shared assembly kernel compares the actual legal
        // projected roster; Raid and ActiveDefence leave this at zero.
        public float MinimumArmyPower;
    }

    public static partial class GroundCombatAssemblyPlanner
    {
        // Legacy/raid-shaped facade. `target` is identity only; the kernel needs the opposition.
        public static GroundCombatAssemblyPlan Plan(WorldSnapshot snap, RaidMissionTarget target,
            IReadOnlyList<WorthIt.DefendingArmy> opposition, ISet<int> excludeArmyIds,
            float defenderHexDefenseBonus = 0f) =>
            Plan(snap, new GroundCombatAssemblyRequest
            {
                Opposition = opposition ?? System.Array.Empty<WorthIt.DefendingArmy>(),
                WinChanceGate = GroundCombatAdmissionPolicy.FreshStartWinChanceGate,
                ExcludedArmyIds = excludeArmyIds,
                DefenderHexDefenseBonus = defenderHexDefenseBonus,
            });

        // The generalized kernel. Prefer an already-sufficient free army; otherwise (when allowed)
        // build a minimal same-hex package around a legal host.
        public static GroundCombatAssemblyPlan Plan(WorldSnapshot snap, GroundCombatAssemblyRequest request)
        {
            if (snap?.Self?.Armies == null)
                return GroundCombatAssemblyPlan.Infeasible("no own-force snapshot");
            if (request == null)
                return GroundCombatAssemblyPlan.Infeasible("no ground-combat assembly request");

            IReadOnlyList<WorthIt.DefendingArmy> opposition =
                request.Opposition ?? System.Array.Empty<WorthIt.DefendingArmy>();
            List<ArmySnapshot> eligible = OrderedEligible(snap, request);
            if (eligible.Count == 0)
                return GroundCombatAssemblyPlan.Infeasible("no free, mobile ground combat army exists this cycle");

            // Already-formed force always wins over reorganisation.
            foreach (ArmySnapshot a in eligible)
            {
                GroundCombatAssemblyPlan exact = PlanForArmyAtThreshold(
                    snap, opposition, a.ArmyId, request.WinChanceGate,
                    request.DefenderHexDefenseBonus);
                if (exact.Feasible && (request.MinimumArmyPower <= 0f
                    || exact.ProjectedPower > request.MinimumArmyPower))
                    return exact;
            }

            if (!request.AllowSameHexAssembly)
                return GroundCombatAssemblyPlan.Infeasible(
                    "no already-formed free ground force clears the shared combat estimator "
                    + "(same-hex assembly not permitted for this request)");

            // Minimal same-hex reinforcement. The host itself must be a real mobile combat body;
            // donors may be reserve armies or garrisons, but dedicated Recce / aviation / prisons
            // and mission-claimed containers are excluded.
            foreach (ArmySnapshot a in eligible)
            {
                GroundCombatAssemblyPlan assembled = TryAssembleForHost(
                    snap, opposition, a, request.ExcludedArmyIds, request.WinChanceGate,
                    request.DefenderHexDefenseBonus, request.MinimumArmyPower);
                if (assembled.Feasible)
                    return assembled;
            }

            return GroundCombatAssemblyPlan.Infeasible(
                "no already-formed or transactionally assemblable same-hex force clears the shared raid estimator");
        }

        // The ONE enumeration of "which ready ground armies could independently take this fight"
        // under the request's gate — the set Plan() would return one by one if it were re-run while
        // excluding each hit. Single pass with identical precedence: every already-formed actor
        // first (Plan's exact loop), then same-hex assembly hosts in the same power order, where
        // every earlier hit is excluded as a donor exactly as the repeated Plan() excluded it.
        // Each actor is Monte-Carlo-tested at most once per stage instead of once per earlier hit.
        internal static List<int> EligibleActorIds(WorldSnapshot snap, GroundCombatAssemblyRequest request)
        {
            var ids = new List<int>();
            if (snap?.Self?.Armies == null || request == null)
                return ids;
            IReadOnlyList<WorthIt.DefendingArmy> opposition =
                request.Opposition ?? System.Array.Empty<WorthIt.DefendingArmy>();
            List<ArmySnapshot> eligible = OrderedEligible(snap, request);
            foreach (ArmySnapshot a in eligible)
            {
                GroundCombatAssemblyPlan exact = PlanForArmyAtThreshold(snap, opposition,
                    a.ArmyId, request.WinChanceGate, request.DefenderHexDefenseBonus);
                if (exact.Feasible && (request.MinimumArmyPower <= 0f
                    || exact.ProjectedPower > request.MinimumArmyPower))
                    ids.Add(a.ArmyId);
            }
            if (!request.AllowSameHexAssembly)
                return ids;

            var excluded = request.ExcludedArmyIds == null
                ? new HashSet<int>() : new HashSet<int>(request.ExcludedArmyIds);
            excluded.UnionWith(ids);
            foreach (ArmySnapshot a in eligible)
            {
                if (excluded.Contains(a.ArmyId))
                    continue;
                if (TryAssembleForHost(snap, opposition, a, excluded, request.WinChanceGate,
                        request.DefenderHexDefenseBonus, request.MinimumArmyPower).Feasible)
                {
                    ids.Add(a.ArmyId);
                    excluded.Add(a.ArmyId);
                }
            }
            return ids;
        }

        // Free ready ground armies admissible for the request, strongest first. Both loops of
        // Plan() return on the FIRST army that clears the estimator, so a decisive matchup exits
        // after one Monte-Carlo call. OrderBy is stable, so this ordering survives untouched
        // through the PreferredPrimaryArmyId reorder below.
        private static List<ArmySnapshot> OrderedEligible(WorldSnapshot snap,
            GroundCombatAssemblyRequest request)
        {
            List<ArmySnapshot> eligible = GroundCombatActorEligibility
                .EligibleReadyArmies(snap, request.ExcludedArmyIds)
                .Where(a => Admissible(a, request))
                .OrderByDescending(a => a.EffectiveArmyPower)
                .ToList();
            if (request.PinToPreferred && request.PreferredPrimaryArmyId.HasValue)
                eligible = eligible.Where(a => a.ArmyId == request.PreferredPrimaryArmyId.Value).ToList();
            else if (request.PreferredPrimaryArmyId.HasValue)
                eligible = eligible
                    .OrderByDescending(a => a.ArmyId == request.PreferredPrimaryArmyId.Value)
                    .ToList();
            return eligible;
        }

        // Same projection, also reporting the projected win chance it already computed (PlanGather
        // ranks supports by it without a second Monte-Carlo run).

        // Same projection, also naming WHO moves: `incoming` — indices into
        // `sparableSupportBodies` that join the primary; `displacedIndex` — the index into
        // `primaryBodies` the swap sends back to the support (-1 for a fill). PlanGather prices
        // the handoff (GroundCombatReinforcement.ProjectedHandoffApCost) on exactly these units.

        private static bool Admissible(ArmySnapshot a, GroundCombatAssemblyRequest r)
        {
            if (a == null) return false;
            if (r.StagingHex.HasValue && !a.Hex.Equals(r.StagingHex.Value)) return false;
            if (r.RequiresSeparateSupportActor && r.PreferredPrimaryArmyId.HasValue
                && a.ArmyId == r.PreferredPrimaryArmyId.Value)
                return false;
            return true;
        }

        // Exact feasibility for one ALREADY ASSIGNED actor. Fresh actor admission is performed by
        // Plan() above at the strict raidMinViableWinChance. Provisioning calls this method only
        // after its batch assignment has picked a concrete actor; GroundCombatAdmissionRegistry additionally
        // uses it for the PreferredMover of a durable Hard Raid. That incumbent gets bounded
        // continuation hysteresis so a valid multi-turn operation is not destroyed by the stricter
        // start gate on every subsequent turn.
        public static GroundCombatAssemblyPlan PlanForArmy(WorldSnapshot snap, RaidMissionTarget target,
            IReadOnlyList<WorthIt.DefendingArmy> opposition, int armyId,
            float defenderHexDefenseBonus = 0f) =>
            PlanForArmyAtThreshold(snap, opposition, armyId,
                GroundCombatAdmissionPolicy.ContinuationWinChanceFloor, defenderHexDefenseBonus);

        // The exact gate the Aggression demand layer re-runs against the NEXT
        // objective before it may call an active Raid "covered". Threshold is explicit: a fresh
        // target is a fresh start decision even for an incumbent army.
        public static GroundCombatAssemblyPlan PlanForArmyAt(WorldSnapshot snap,
            IReadOnlyList<WorthIt.DefendingArmy> opposition, int armyId, float minWinChance,
            float defenderHexDefenseBonus = 0f) =>
            PlanForArmyAtThreshold(snap, opposition, armyId, minWinChance, defenderHexDefenseBonus);

        internal static GroundCombatAssemblyPlan PlanForArmyAtThreshold(WorldSnapshot snap,
            IReadOnlyList<WorthIt.DefendingArmy> opposition, int armyId, float minWinChance,
            float defenderHexDefenseBonus = 0f)
        {
            if (snap?.Self?.Armies == null)
                return GroundCombatAssemblyPlan.Infeasible("no own-force snapshot");

            ArmySnapshot a = snap.Self.Armies.FirstOrDefault(x => x != null && x.ArmyId == armyId);
            if (a == null || !a.IsStructuralRaidActor)
                return GroundCombatAssemblyPlan.Infeasible($"raid actor #{armyId} is not a free mobile ground combat army");

            opposition = opposition ?? System.Array.Empty<WorthIt.DefendingArmy>();
            List<WorthIt.DefenderProfile> roster =
                (a.Members ?? System.Array.Empty<WorthIt.DefenderProfile>()).ToList();
            if (!GroundCombatFeasibility.Clears(roster, a.Commander, opposition, minWinChance,
                    defenderHexDefenseBonus, out float win, out bool cover))
                return GroundCombatAssemblyPlan.Infeasible(
                    $"raid actor #{armyId} does not clear the assigned-actor raid estimator "
                    + $"(win {win:0.00} < {minWinChance:0.00} or coverage missing)");

            return new GroundCombatAssemblyPlan
            {
                Feasible = true,
                BaseArmyId = a.ArmyId,
                NeedsAssembly = false,
                ProjectedWinChance = win,
                CoversAllDefenders = cover,
                ProjectedPower = a.EffectiveArmyPower,
                WinChanceGate = minWinChance,
            };
        }

        private static GroundCombatAssemblyPlan TryAssembleForHost(WorldSnapshot snap,
            IReadOnlyList<WorthIt.DefendingArmy> opposition, ArmySnapshot hostSnap, ISet<int> excludeArmyIds,
            float minWinChance, float defenderHexDefenseBonus, float minimumArmyPower = 0f)
        {
            PlayerSetupData owner = hostSnap?.Owner;
            if (owner == null)
                return GroundCombatAssemblyPlan.Infeasible("assembly host has no owner");
            ArmyData host = ArmyRegistry.AllForOwner(owner)
                .FirstOrDefault(a => a != null && a.Id == hostSnap.ArmyId);
            if (host == null || host.Members.Count == 0 || host.CurrentMovement <= 0)
                return GroundCombatAssemblyPlan.Infeasible("assembly host is no longer live/mobile");
            return AssembleSameHex(snap, owner, host, opposition, excludeArmyIds, minWinChance,
                defenderHexDefenseBonus, minimumArmyPower, preparation: false);
        }

    }
}


