using System.Collections.Generic;
using System.Linq;
using Game.Map;
using Game.Players;
using Game.Units;

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
    //  Actor order is mobility-first: already-activated / cheaper activation first, then the least
    //  powerful sufficient host. This avoids feeding an already-winning raid into an ever larger,
    //  ever more expensive stack.
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

    public static class RaidAssemblyPlanner
    {
        public static RaidAssemblyPlan Plan(WorldSnapshot snap, RaidMissionTarget target,
            IReadOnlyList<WorthIt.DefenderProfile> defenders, ISet<int> excludeArmyIds)
        {
            if (snap?.Self?.Armies == null)
                return RaidAssemblyPlan.Infeasible("no own-force snapshot");

            defenders = defenders ?? System.Array.Empty<WorthIt.DefenderProfile>();
            List<ArmySnapshot> eligible = EligibleReadyArmies(snap, excludeArmyIds);
            if (eligible.Count == 0)
                return RaidAssemblyPlan.Infeasible("no free, mobile ground combat army exists this cycle");

            // Already-formed force always wins over reorganisation.
            foreach (ArmySnapshot a in eligible)
            {
                RaidAssemblyPlan exact = PlanForArmy(snap, target, defenders, a.ArmyId);
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

        // Exact feasibility for one actor. Provisioning's batch assignment uses this to preserve an
        // injective mapping between independently-ready hosts. If the exact assignment becomes
        // stale, the fallback Plan() above may still form a minimal same-hex package.
        public static RaidAssemblyPlan PlanForArmy(WorldSnapshot snap, RaidMissionTarget target,
            IReadOnlyList<WorthIt.DefenderProfile> defenders, int armyId)
        {
            if (snap?.Self?.Armies == null)
                return RaidAssemblyPlan.Infeasible("no own-force snapshot");

            ArmySnapshot a = snap.Self.Armies.FirstOrDefault(x => x != null && x.ArmyId == armyId);
            if (a == null || !IsReadyRaidActor(a))
                return RaidAssemblyPlan.Infeasible($"raid actor #{armyId} is not a free mobile ground combat army");

            defenders = defenders ?? System.Array.Empty<WorthIt.DefenderProfile>();
            List<WorthIt.DefenderProfile> roster =
                (a.Members ?? System.Array.Empty<WorthIt.DefenderProfile>()).ToList();
            if (!Clears(roster, defenders, out float win, out bool cover))
                return RaidAssemblyPlan.Infeasible($"raid actor #{armyId} does not clear the shared raid estimator");

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
                    .OrderByDescending(UnitCombatValue)
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

                if (!Clears(projectedProfiles, defenders, out float win, out bool cover))
                    continue;

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

            return RaidAssemblyPlan.Infeasible($"raid actor #{host.Id} cannot reach the win bar from safe same-hex donors");
        }

        private static float UnitCombatValue(UnitData u) =>
            u == null ? 0f : u.Attack + u.Defense + u.HitPointsCurrent + 0.25f * u.Initiative;

        private static List<ArmySnapshot> EligibleReadyArmies(WorldSnapshot snap, ISet<int> excludeArmyIds) =>
            snap.Self.Armies
                .Where(a => a != null && IsReadyRaidActor(a)
                            && (excludeArmyIds == null || !excludeArmyIds.Contains(a.ArmyId)))
                .OrderBy(a => a.HasActivatedThisTurn ? 0 : a.ActivationApCost)
                .ThenBy(a => a.EffectiveArmyPower)
                .ThenBy(a => a.ArmyId)
                .ToList();

        // ONE structural Raid actor predicate shared by Strategy diagnostics, Demand capability
        // inventory and final Provisioning. Garrison is reserve/potential power, never a mover.
        internal static bool IsStructuralRaidActor(ArmySnapshot a)
        {
            if (a == null || a.IsPrison || a.IsAir || a.IsGarrison || a.IsSoloRecce || a.MemberCount <= 0)
                return false;

            PlayerSetupData owner = a.Owner;
            if (owner == null)
                return false;
            ArmyData live = ArmyRegistry.AllForOwner(owner).FirstOrDefault(x => x != null && x.Id == a.ArmyId);
            return live != null && !live.IsPrison && !live.IsGarrison && !live.IsAirfield && !live.IsAirArmy
                && !AiArmyRoles.IsSoloRecce(live) && !AiArmyRoles.IsSoloHeroAwaitingEscort(live)
                && live.Members.Count > 0;
        }

        internal static bool IsReadyRaidActor(ArmySnapshot a) =>
            IsStructuralRaidActor(a) && a.CurrentMovement > 0;

        private static bool Clears(IReadOnlyList<WorthIt.DefenderProfile> attackers,
            IReadOnlyList<WorthIt.DefenderProfile> defenders, out float win, out bool cover)
        {
            cover = ProfilesCoverAll(attackers, defenders);
            win = defenders.Count == 0
                ? 1f
                : WorthIt.WinChance((IReadOnlyCollection<WorthIt.DefenderProfile>)attackers,
                    (IReadOnlyCollection<WorthIt.DefenderProfile>)defenders, 0f);
            return cover && win >= AiConfigV2.raidMinViableWinChance;
        }

        private static bool ProfilesCoverAll(IReadOnlyList<WorthIt.DefenderProfile> attackers,
            IReadOnlyList<WorthIt.DefenderProfile> defenders)
        {
            if (defenders == null || defenders.Count == 0) return true;
            if (attackers == null || attackers.Count == 0) return false;
            foreach (WorthIt.DefenderProfile def in defenders)
            {
                bool covered = false;
                foreach (WorthIt.DefenderProfile atk in attackers)
                    if (WorthIt.CanDamage(atk.Attack, def, 0f)) { covered = true; break; }
                if (!covered) return false;
            }
            return true;
        }
    }
}
