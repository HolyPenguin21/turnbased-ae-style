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
    //  RaidAdmissionPolicy. It computes no strategic objective value.
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

        public static GroundCombatAssemblyPlan Infeasible(string reason) =>
            new GroundCombatAssemblyPlan { Feasible = false, Reason = reason };
    }

    // ARCH-02 §29 — the fresh-start vs continuation win-chance gates. Starting a raid and
    // continuing an already-started operation are deliberately different decisions.
    internal static class RaidAdmissionPolicy
    {
        // Fresh admission still requires the strict raidMinViableWinChance (0.65 today).
        internal static float FreshStartWinChanceGate => AiConfigV2.raidMinViableWinChance;

        // Once a Hard raid has actually left its staging hex, small Monte-Carlo variance / loss of
        // same-hex donor availability must not instantly turn the incumbent actor into a structural
        // AssemblyInfeasible failure. The assigned incumbent may continue while it still covers
        // every known defender and keeps at least this lower safety floor. 0.40 is intentionally
        // conservative: it fixes the observed 0.78-start -> ~0.41-next-turn discontinuity without
        // authorising a clearly hopeless attack. Fresh raids never see this floor.
        internal const float ContinuationWinChanceFloor = 0.40f;
    }

    // AGG-RAID §1 — the generalized ground-combat assembly REQUEST. The kernel below is shared by
    // Raid (Assault / Reinforcement) and by any future Active Defence lane; nothing Raid-specific
    // (target merit, phase transitions, cooldowns) lives inside it. Every field is an explicit
    // constraint the caller owns.
    public sealed class GroundCombatAssemblyRequest
    {
        // Defenders the assembled force must be able to beat. Empty == "no fight to win" (a pure
        // escort / rendezvous request), which trivially clears the estimator.
        public IReadOnlyList<WorthIt.DefenderProfile> Defenders =
            System.Array.Empty<WorthIt.DefenderProfile>();
        // Fresh-start vs continuation win gate. Callers pass RaidAdmissionPolicy's two constants.
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
    }

    public static class GroundCombatAssemblyPlanner
    {
        // Legacy/raid-shaped facade. `target` is identity only; the kernel needs the defenders.
        public static GroundCombatAssemblyPlan Plan(WorldSnapshot snap, RaidMissionTarget target,
            IReadOnlyList<WorthIt.DefenderProfile> defenders, ISet<int> excludeArmyIds) =>
            Plan(snap, new GroundCombatAssemblyRequest
            {
                Defenders = defenders ?? System.Array.Empty<WorthIt.DefenderProfile>(),
                WinChanceGate = RaidAdmissionPolicy.FreshStartWinChanceGate,
                ExcludedArmyIds = excludeArmyIds,
            });

        // The generalized kernel. Prefer an already-sufficient free army; otherwise (when allowed)
        // build a minimal same-hex package around a legal host.
        public static GroundCombatAssemblyPlan Plan(WorldSnapshot snap, GroundCombatAssemblyRequest request)
        {
            if (snap?.Self?.Armies == null)
                return GroundCombatAssemblyPlan.Infeasible("no own-force snapshot");
            if (request == null)
                return GroundCombatAssemblyPlan.Infeasible("no ground-combat assembly request");

            IReadOnlyList<WorthIt.DefenderProfile> defenders =
                request.Defenders ?? System.Array.Empty<WorthIt.DefenderProfile>();
            List<ArmySnapshot> eligible = GroundCombatActorEligibility
                .EligibleReadyArmies(snap, request.ExcludedArmyIds)
                .Where(a => Admissible(a, request))
                .ToList();
            if (request.PinToPreferred && request.PreferredPrimaryArmyId.HasValue)
                eligible = eligible.Where(a => a.ArmyId == request.PreferredPrimaryArmyId.Value).ToList();
            else if (request.PreferredPrimaryArmyId.HasValue)
                eligible = eligible
                    .OrderByDescending(a => a.ArmyId == request.PreferredPrimaryArmyId.Value)
                    .ToList();
            if (eligible.Count == 0)
                return GroundCombatAssemblyPlan.Infeasible("no free, mobile ground combat army exists this cycle");

            // Already-formed force always wins over reorganisation.
            foreach (ArmySnapshot a in eligible)
            {
                GroundCombatAssemblyPlan exact = PlanForArmyAtThreshold(
                    snap, defenders, a.ArmyId, request.WinChanceGate);
                if (exact.Feasible)
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
                    snap, defenders, a, request.ExcludedArmyIds, request.WinChanceGate);
                if (assembled.Feasible)
                    return assembled;
            }

            return GroundCombatAssemblyPlan.Infeasible(
                "no already-formed or transactionally assemblable same-hex force clears the shared raid estimator");
        }

        // AGG-RAID P0#1 — reinforcement support-actor candidates. An EXISTING free ground-combat
        // army (already on the map, needing no card play / materialization) qualifies as a
        // Reinforcement support actor when merging its roster into the primary's would improve the
        // primary's projected WorthIt win chance against the current defenders — the same economics
        // ProvisioningManager.ReinforcementImprovesOdds re-checks live at execution time, evaluated
        // here at snapshot granularity so Missions can propose a concrete leg before an actor is
        // physically claimed. Demand reads this to decide whether a NEW army even needs to be
        // materialized; Missions/Provisioning read it to run the normal actor-contention batch solve.
        public static List<int> ReinforcementSupportCandidates(WorldSnapshot snap, int primaryArmyId,
            IReadOnlyList<WorthIt.DefenderProfile> defenders, ISet<int> excludeArmyIds)
        {
            var ids = new List<int>();
            if (snap?.Self?.Armies == null || primaryArmyId == 0)
                return ids;
            ArmySnapshot primary = snap.Self.Armies.FirstOrDefault(a => a != null && a.ArmyId == primaryArmyId);
            if (primary == null)
                return ids;

            defenders = defenders ?? System.Array.Empty<WorthIt.DefenderProfile>();
            var before = (primary.Members ?? System.Array.Empty<WorthIt.DefenderProfile>()).ToList();
            float winBefore = defenders.Count == 0 ? 1f
                : WorthIt.WinChance(before, (IReadOnlyCollection<WorthIt.DefenderProfile>)defenders, 0f);

            var excluded = excludeArmyIds != null ? new HashSet<int>(excludeArmyIds) : new HashSet<int>();
            excluded.Add(primaryArmyId);
            foreach (ArmySnapshot candidate in GroundCombatActorEligibility.EligibleReadyArmies(snap, excluded))
            {
                IReadOnlyList<WorthIt.DefenderProfile> bodies =
                    candidate.Members ?? System.Array.Empty<WorthIt.DefenderProfile>();
                // Mirrors SparableSupportBodies' minimum-container invariant at snapshot level:
                // leave at least one total member (a hero may be that retained member). Previously
                // a single-body army was advertised as support even though execution could transfer
                // nothing, producing a permanent select -> reject loop.
                int transferable = System.Math.Min(bodies.Count,
                    System.Math.Max(0, candidate.MemberCount - 1));
                if (transferable <= 0)
                    continue;

                var after = new List<WorthIt.DefenderProfile>(before);
                after.AddRange(bodies.Take(transferable));
                float winAfter = defenders.Count == 0 ? 1f
                    : WorthIt.WinChance(after, (IReadOnlyCollection<WorthIt.DefenderProfile>)defenders, 0f);
                if (winAfter > winBefore + 0.001f)
                    ids.Add(candidate.ArmyId);
            }
            return ids;
        }

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
            IReadOnlyList<WorthIt.DefenderProfile> defenders, int armyId) =>
            PlanForArmyAtThreshold(snap, defenders, armyId, RaidAdmissionPolicy.ContinuationWinChanceFloor);

        // AGG-RAID §6 — the exact gate the Aggression demand layer re-runs against the NEXT
        // objective before it may call an active Raid "covered". Threshold is explicit: a fresh
        // target is a fresh start decision even for an incumbent army.
        public static GroundCombatAssemblyPlan PlanForArmyAt(WorldSnapshot snap,
            IReadOnlyList<WorthIt.DefenderProfile> defenders, int armyId, float minWinChance) =>
            PlanForArmyAtThreshold(snap, defenders, armyId, minWinChance);

        internal static GroundCombatAssemblyPlan PlanForArmyAtThreshold(WorldSnapshot snap,
            IReadOnlyList<WorthIt.DefenderProfile> defenders, int armyId, float minWinChance)
        {
            if (snap?.Self?.Armies == null)
                return GroundCombatAssemblyPlan.Infeasible("no own-force snapshot");

            ArmySnapshot a = snap.Self.Armies.FirstOrDefault(x => x != null && x.ArmyId == armyId);
            if (a == null || !a.IsStructuralRaidActor)
                return GroundCombatAssemblyPlan.Infeasible($"raid actor #{armyId} is not a free mobile ground combat army");

            defenders = defenders ?? System.Array.Empty<WorthIt.DefenderProfile>();
            List<WorthIt.DefenderProfile> roster =
                (a.Members ?? System.Array.Empty<WorthIt.DefenderProfile>()).ToList();
            if (!GroundCombatFeasibility.Clears(roster, defenders, minWinChance, out float win, out bool cover))
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
            };
        }

        private static GroundCombatAssemblyPlan TryAssembleForHost(WorldSnapshot snap,
            IReadOnlyList<WorthIt.DefenderProfile> defenders, ArmySnapshot hostSnap, ISet<int> excludeArmyIds,
            float minWinChance)
        {
            PlayerSetupData owner = hostSnap?.Owner;
            if (owner == null)
                return GroundCombatAssemblyPlan.Infeasible("assembly host has no owner");
            ArmyData host = ArmyRegistry.AllForOwner(owner)
                .FirstOrDefault(a => a != null && a.Id == hostSnap.ArmyId);
            if (host == null || host.Members.Count == 0 || host.CurrentMovement <= 0)
                return GroundCombatAssemblyPlan.Infeasible("assembly host is no longer live/mobile");

            var projectedUnits = new List<UnitData>(host.Members);
            var projectedProfiles = projectedUnits.Select(WorthIt.FromLiveUnit).ToList();
            var selected = new List<GroundCombatAssemblyTransfer>();

            // §12 — a heroless host may take ONE eligible same-hex hero from a safe donor
            // (typically the garrison). Preference: CombatLeader > Flexible > SupportOperator.
            // A lone-hero container is intentionally left to Housekeeping first: Provisioning's
            // canonical raid transaction never empties donor containers, so the planner must not
            // promise a transfer the executor will reject.
            if (!projectedUnits.Any(u => u != null && u.IsHero))
            {
                (ArmyData heroDonor, UnitData hero) = GroundCombatDonorPolicy.PickAttachableHero(owner, host, excludeArmyIds);
                if (hero != null)
                {
                    var withHero = new List<UnitData>(projectedUnits) { hero };
                    if (ArmyData.ComputeCapacity(withHero, host.IsGarrison) >= withHero.Count)
                    {
                        projectedUnits.Add(hero);
                        projectedProfiles.Add(WorthIt.FromLiveUnit(hero));
                        selected.Add(new GroundCombatAssemblyTransfer { DonorArmyId = heroDonor.Id, Unit = hero });
                        if (GroundCombatFeasibility.Clears(projectedProfiles, defenders, minWinChance, out float hWin, out bool hCover))
                            return FinishAssembly(host, selected, hWin, hCover);
                    }
                }
            }

            IEnumerable<ArmySnapshot> donorSnaps = snap.Self.Armies
                .Where(d => d != null && d.ArmyId != host.Id && d.Owner == owner
                    && d.Hex.Equals(host.Hex) && !d.IsPrison && !d.IsAir && !d.IsSoloRecce
                    && d.MemberCount > 1
                    && (excludeArmyIds == null || !excludeArmyIds.Contains(d.ArmyId)))
                .OrderByDescending(d => d.EffectiveArmyPower)
                .ThenBy(d => d.ArmyId);

            foreach (ArmySnapshot donorSnap in donorSnaps)
            {
                ArmyData donor = ArmyRegistry.AllForOwner(owner)
                    .FirstOrDefault(a => a != null && a.Id == donorSnap.ArmyId);
                if (donor == null || donor.Members.Count <= 1 || !donor.Hex.Equals(host.Hex)
                    || donor.IsPrison || donor.IsAirfield || donor.IsAirArmy || AiArmyRoles.IsSoloRecce(donor))
                    continue;

                UnitData pick = donor.Members
                    .Where(u => u != null && !u.IsHero && !u.IsAviation
                        && donor.Members.Count > 1
                        && donor.CanLeaveWithoutOvercrowding(u)
                        && (!donor.IsGarrison || AiArmyRoles.CanSpareGarrisonMember(owner, donor, u))
                        && (!host.HasActivatedThisTurn || u.ActivationApCost <= 0))
                    .OrderByDescending(GroundCombatDonorPolicy.UnitCombatValue)
                    .ThenBy(u => u.Name)
                    .FirstOrDefault();
                if (pick == null)
                    continue;

                var withPick = new List<UnitData>(projectedUnits) { pick };
                if (ArmyData.ComputeCapacity(withPick, host.IsGarrison) < withPick.Count)
                    continue;

                projectedUnits.Add(pick);
                projectedProfiles.Add(WorthIt.FromLiveUnit(pick));
                selected.Add(new GroundCombatAssemblyTransfer { DonorArmyId = donor.Id, Unit = pick });

                if (GroundCombatFeasibility.Clears(projectedProfiles, defenders, minWinChance, out float win, out bool cover))
                    return FinishAssembly(host, selected, win, cover);
            }

            // The hero alone (no bodies available/needed) may already clear.
            if (selected.Count > 0 && GroundCombatFeasibility.Clears(projectedProfiles, defenders, minWinChance, out float wOnly, out bool cOnly))
                return FinishAssembly(host, selected, wOnly, cOnly);

            return GroundCombatAssemblyPlan.Infeasible($"raid actor #{host.Id} cannot reach the win bar from safe same-hex donors");
        }

        private static GroundCombatAssemblyPlan FinishAssembly(ArmyData host,
            IReadOnlyList<GroundCombatAssemblyTransfer> selected, float win, bool cover)
        {
            var plan = new GroundCombatAssemblyPlan
            {
                Feasible = true,
                BaseArmyId = host.Id,
                NeedsAssembly = true,
                ProjectedWinChance = win,
                CoversAllDefenders = cover,
            };
            foreach (GroundCombatAssemblyTransfer t in selected)
            {
                plan.Transfers.Add(t);
                if (!plan.MergeArmyIds.Contains(t.DonorArmyId))
                    plan.MergeArmyIds.Add(t.DonorArmyId);
            }
            return plan;
        }
    }
}
