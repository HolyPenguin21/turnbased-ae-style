using System.Collections.Generic;
using System.Linq;
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
    //  eligibility is RaidActorEligibility; the WorthIt win/coverage check is RaidCombatFeasibility;
    //  same-hex donor legality is RaidDonorPolicy; the fresh-vs-continuation win gates are
    //  RaidAdmissionPolicy. It computes no strategic objective value.
    // ===========================================================================================
    public sealed class RaidAssemblyTransfer
    {
        public int DonorArmyId;
        public UnitData Unit;
    }

    public sealed class RaidAssemblyPlan
    {
        public bool Feasible;
        public string Reason;
        public int BaseArmyId;
        public bool NeedsAssembly;
        public readonly List<int> MergeArmyIds = new List<int>();
        public readonly List<RaidAssemblyTransfer> Transfers = new List<RaidAssemblyTransfer>();
        public float ProjectedWinChance;
        public bool CoversAllDefenders;

        public static RaidAssemblyPlan Infeasible(string reason) =>
            new RaidAssemblyPlan { Feasible = false, Reason = reason };
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

    public static class RaidAssemblyPlanner
    {
        public static RaidAssemblyPlan Plan(WorldSnapshot snap, RaidMissionTarget target,
            IReadOnlyList<WorthIt.DefenderProfile> defenders, ISet<int> excludeArmyIds)
        {
            if (snap?.Self?.Armies == null)
                return RaidAssemblyPlan.Infeasible("no own-force snapshot");

            defenders = defenders ?? System.Array.Empty<WorthIt.DefenderProfile>();
            List<ArmySnapshot> eligible = RaidActorEligibility.EligibleReadyArmies(snap, excludeArmyIds);
            if (eligible.Count == 0)
                return RaidAssemblyPlan.Infeasible("no free, mobile ground combat army exists this cycle");

            // Already-formed force always wins over reorganisation. This is FRESH admission, so it
            // must retain the normal strict raid bar; continuation hysteresis is only for an actor
            // that was explicitly assigned to an already-started durable Raid.
            foreach (ArmySnapshot a in eligible)
            {
                RaidAssemblyPlan exact = PlanForArmyAtThreshold(
                    snap, target, defenders, a.ArmyId, RaidAdmissionPolicy.FreshStartWinChanceGate);
                if (exact.Feasible)
                    return exact;
            }

            // Minimal same-hex reinforcement. The host itself must be a real mobile combat body;
            // donors may be reserve armies or garrisons, but dedicated Recce / aviation / prisons
            // and mission-claimed containers are excluded.
            foreach (ArmySnapshot a in eligible)
            {
                RaidAssemblyPlan assembled = TryAssembleForHost(snap, defenders, a, excludeArmyIds);
                if (assembled.Feasible)
                    return assembled;
            }

            return RaidAssemblyPlan.Infeasible(
                "no already-formed or transactionally assemblable same-hex force clears the shared raid estimator");
        }

        // Exact feasibility for one ALREADY ASSIGNED actor. Fresh actor admission is performed by
        // Plan() above at the strict raidMinViableWinChance. Provisioning calls this method only
        // after its batch assignment has picked a concrete actor; RaidAdmissionRegistry additionally
        // uses it for the PreferredMover of a durable Hard Raid. That incumbent gets bounded
        // continuation hysteresis so a valid multi-turn operation is not destroyed by the stricter
        // start gate on every subsequent turn.
        public static RaidAssemblyPlan PlanForArmy(WorldSnapshot snap, RaidMissionTarget target,
            IReadOnlyList<WorthIt.DefenderProfile> defenders, int armyId) =>
            PlanForArmyAtThreshold(snap, target, defenders, armyId, RaidAdmissionPolicy.ContinuationWinChanceFloor);

        private static RaidAssemblyPlan PlanForArmyAtThreshold(WorldSnapshot snap, RaidMissionTarget target,
            IReadOnlyList<WorthIt.DefenderProfile> defenders, int armyId, float minWinChance)
        {
            if (snap?.Self?.Armies == null)
                return RaidAssemblyPlan.Infeasible("no own-force snapshot");

            ArmySnapshot a = snap.Self.Armies.FirstOrDefault(x => x != null && x.ArmyId == armyId);
            if (a == null || !RaidActorEligibility.IsStructuralRaidActor(a))
                return RaidAssemblyPlan.Infeasible($"raid actor #{armyId} is not a free mobile ground combat army");

            defenders = defenders ?? System.Array.Empty<WorthIt.DefenderProfile>();
            List<WorthIt.DefenderProfile> roster =
                (a.Members ?? System.Array.Empty<WorthIt.DefenderProfile>()).ToList();
            if (!RaidCombatFeasibility.Clears(roster, defenders, minWinChance, out float win, out bool cover))
                return RaidAssemblyPlan.Infeasible(
                    $"raid actor #{armyId} does not clear the assigned-actor raid estimator "
                    + $"(win {win:0.00} < {minWinChance:0.00} or coverage missing)");

            return new RaidAssemblyPlan
            {
                Feasible = true,
                BaseArmyId = a.ArmyId,
                NeedsAssembly = false,
                ProjectedWinChance = win,
                CoversAllDefenders = cover,
            };
        }

        private static RaidAssemblyPlan TryAssembleForHost(WorldSnapshot snap,
            IReadOnlyList<WorthIt.DefenderProfile> defenders, ArmySnapshot hostSnap, ISet<int> excludeArmyIds)
        {
            PlayerSetupData owner = hostSnap?.Owner;
            if (owner == null)
                return RaidAssemblyPlan.Infeasible("assembly host has no owner");
            ArmyData host = ArmyRegistry.AllForOwner(owner)
                .FirstOrDefault(a => a != null && a.Id == hostSnap.ArmyId);
            if (host == null || host.Members.Count == 0 || host.CurrentMovement <= 0)
                return RaidAssemblyPlan.Infeasible("assembly host is no longer live/mobile");

            var projectedUnits = new List<UnitData>(host.Members);
            var projectedProfiles = projectedUnits.Select(WorthIt.FromLiveUnit).ToList();
            var selected = new List<RaidAssemblyTransfer>();

            // §12 — a heroless host may take ONE eligible same-hex hero from a safe donor
            // (typically the garrison). Preference: CombatLeader > Flexible > SupportOperator.
            // A lone-hero container is intentionally left to Housekeeping first: Provisioning's
            // canonical raid transaction never empties donor containers, so the planner must not
            // promise a transfer the executor will reject.
            if (!projectedUnits.Any(u => u != null && u.IsHero))
            {
                (ArmyData heroDonor, UnitData hero) = RaidDonorPolicy.PickAttachableHero(owner, host, excludeArmyIds);
                if (hero != null)
                {
                    var withHero = new List<UnitData>(projectedUnits) { hero };
                    if (ArmyData.ComputeCapacity(withHero, host.IsGarrison) >= withHero.Count)
                    {
                        projectedUnits.Add(hero);
                        projectedProfiles.Add(WorthIt.FromLiveUnit(hero));
                        selected.Add(new RaidAssemblyTransfer { DonorArmyId = heroDonor.Id, Unit = hero });
                        if (RaidCombatFeasibility.Clears(projectedProfiles, defenders, out float hWin, out bool hCover))
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
                    .OrderByDescending(RaidDonorPolicy.UnitCombatValue)
                    .ThenBy(u => u.Name)
                    .FirstOrDefault();
                if (pick == null)
                    continue;

                var withPick = new List<UnitData>(projectedUnits) { pick };
                if (ArmyData.ComputeCapacity(withPick, host.IsGarrison) < withPick.Count)
                    continue;

                projectedUnits.Add(pick);
                projectedProfiles.Add(WorthIt.FromLiveUnit(pick));
                selected.Add(new RaidAssemblyTransfer { DonorArmyId = donor.Id, Unit = pick });

                if (RaidCombatFeasibility.Clears(projectedProfiles, defenders, out float win, out bool cover))
                    return FinishAssembly(host, selected, win, cover);
            }

            // The hero alone (no bodies available/needed) may already clear.
            if (selected.Count > 0 && RaidCombatFeasibility.Clears(projectedProfiles, defenders, out float wOnly, out bool cOnly))
                return FinishAssembly(host, selected, wOnly, cOnly);

            return RaidAssemblyPlan.Infeasible($"raid actor #{host.Id} cannot reach the win bar from safe same-hex donors");
        }

        private static RaidAssemblyPlan FinishAssembly(ArmyData host,
            IReadOnlyList<RaidAssemblyTransfer> selected, float win, bool cover)
        {
            var plan = new RaidAssemblyPlan
            {
                Feasible = true,
                BaseArmyId = host.Id,
                NeedsAssembly = true,
                ProjectedWinChance = win,
                CoversAllDefenders = cover,
            };
            foreach (RaidAssemblyTransfer t in selected)
            {
                plan.Transfers.Add(t);
                if (!plan.MergeArmyIds.Contains(t.DonorArmyId))
                    plan.MergeArmyIds.Add(t.DonorArmyId);
            }
            return plan;
        }
    }
}
