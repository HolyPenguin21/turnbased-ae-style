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
        // Starting a raid and continuing an already-started operation are deliberately different
        // decisions. Fresh admission still requires raidMinViableWinChance (0.65 today). Once a
        // Hard raid has actually left its staging hex, however, small Monte-Carlo variance / loss
        // of same-hex donor availability must not instantly turn the incumbent actor into a
        // structural AssemblyInfeasible failure. The assigned incumbent may continue while it
        // still covers every known defender and keeps at least this lower safety floor.
        //
        // 0.40 is intentionally conservative: it fixes the observed 0.78-start -> ~0.41-next-turn
        // discontinuity without authorising a clearly hopeless attack. Fresh raids never see this
        // floor because Plan() remains on the normal 0.65 gate.
        private const float ContinuationWinChanceFloor = 0.40f;

        public static RaidAssemblyPlan Plan(WorldSnapshot snap, RaidMissionTarget target,
            IReadOnlyList<WorthIt.DefenderProfile> defenders, ISet<int> excludeArmyIds)
        {
            if (snap?.Self?.Armies == null)
                return RaidAssemblyPlan.Infeasible("no own-force snapshot");

            defenders = defenders ?? System.Array.Empty<WorthIt.DefenderProfile>();
            List<ArmySnapshot> eligible = EligibleReadyArmies(snap, excludeArmyIds);
            if (eligible.Count == 0)
                return RaidAssemblyPlan.Infeasible("no free, mobile ground combat army exists this cycle");

            // Already-formed force always wins over reorganisation. This is FRESH admission, so it
            // must retain the normal strict raid bar; continuation hysteresis is only for an actor
            // that was explicitly assigned to an already-started durable Raid.
            foreach (ArmySnapshot a in eligible)
            {
                RaidAssemblyPlan exact = PlanForArmyAtThreshold(
                    snap, target, defenders, a.ArmyId, AiConfigV2.raidMinViableWinChance);
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
            PlanForArmyAtThreshold(snap, target, defenders, armyId, ContinuationWinChanceFloor);

        private static RaidAssemblyPlan PlanForArmyAtThreshold(WorldSnapshot snap, RaidMissionTarget target,
            IReadOnlyList<WorthIt.DefenderProfile> defenders, int armyId, float minWinChance)
        {
            if (snap?.Self?.Armies == null)
                return RaidAssemblyPlan.Infeasible("no own-force snapshot");

            ArmySnapshot a = snap.Self.Armies.FirstOrDefault(x => x != null && x.ArmyId == armyId);
            if (a == null || !IsReadyRaidActor(a))
                return RaidAssemblyPlan.Infeasible($"raid actor #{armyId} is not a free mobile ground combat army");

            defenders = defenders ?? System.Array.Empty<WorthIt.DefenderProfile>();
            List<WorthIt.DefenderProfile> roster =
                (a.Members ?? System.Array.Empty<WorthIt.DefenderProfile>()).ToList();
            if (!Clears(roster, defenders, minWinChance, out float win, out bool cover))
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
                (ArmyData heroDonor, UnitData hero) = PickAttachableHero(owner, host, excludeArmyIds);
                if (hero != null)
                {
                    var withHero = new List<UnitData>(projectedUnits) { hero };
                    if (ArmyData.ComputeCapacity(withHero, host.IsGarrison) >= withHero.Count)
                    {
                        projectedUnits.Add(hero);
                        projectedProfiles.Add(WorthIt.FromLiveUnit(hero));
                        selected.Add(new RaidAssemblyTransfer { DonorArmyId = heroDonor.Id, Unit = hero });
                        if (Clears(projectedProfiles, defenders, out float hWin, out bool hCover))
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

                if (Clears(projectedProfiles, defenders, out float win, out bool cover))
                    return FinishAssembly(host, selected, win, cover);
            }

            // The hero alone (no bodies available/needed) may already clear.
            if (selected.Count > 0 && Clears(projectedProfiles, defenders, out float wOnly, out bool cOnly))
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

        // §12 — the best same-hex hero that may legally join `host`, or (null, null).
        // CombatLeader > Flexible > SupportOperator, then a stable donor-id tiebreak. A donor must
        // retain at least one member because Provisioning enforces that same transaction boundary.
        private static (ArmyData donor, UnitData hero) PickAttachableHero(PlayerSetupData owner,
            ArmyData host, ISet<int> excludeArmyIds)
        {
            var candidates = new List<(ArmyData donor, UnitData hero)>();
            foreach (ArmyData donor in ArmyRegistry.AllForOwner(owner))
            {
                if (donor == null || donor.Id == host.Id || donor.Members.Count <= 1
                    || !donor.Hex.Equals(host.Hex)
                    || donor.IsPrison || donor.IsAirfield || donor.IsAirArmy || AiArmyRoles.IsSoloRecce(donor)
                    || (excludeArmyIds != null && excludeArmyIds.Contains(donor.Id)))
                    continue;
                foreach (UnitData h in donor.Members)
                {
                    if (h == null || !h.IsHero || h.IsAviation)
                        continue;
                    if (!donor.CanLeaveWithoutOvercrowding(h))
                        continue;
                    if (donor.IsGarrison && !AiArmyRoles.CanSpareGarrisonMember(owner, donor, h))
                        continue;
                    if (host.HasActivatedThisTurn && h.ActivationApCost > 0)
                        continue;
                    candidates.Add((donor, h));
                }
            }
            if (candidates.Count == 0)
                return (null, null);
            candidates.Sort((x, y) =>
            {
                int c = HeroRoleEvaluator.CompareForFieldCommand(x.hero, y.hero);
                return c != 0 ? c : x.donor.Id.CompareTo(y.donor.Id);
            });
            return candidates[0];
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

        // Deliberately structural, NOT `CurrentMovement > 0`. A Hard raid incumbent that already
        // made productive progress earlier in the same turn is still the correct actor. Let the
        // live Provisioning seam reject that spent actor as MoverContended/RetryNextTurn; treating
        // zero remaining movement here as AssemblyInfeasible poisons a valid multi-turn raid with
        // a structural cooldown and is exactly what stranded Dead Tide in the T7 log.
        internal static bool IsReadyRaidActor(ArmySnapshot a) =>
            IsStructuralRaidActor(a);

        private static bool Clears(IReadOnlyList<WorthIt.DefenderProfile> attackers,
            IReadOnlyList<WorthIt.DefenderProfile> defenders, out float win, out bool cover) =>
            Clears(attackers, defenders, AiConfigV2.raidMinViableWinChance, out win, out cover);

        private static bool Clears(IReadOnlyList<WorthIt.DefenderProfile> attackers,
            IReadOnlyList<WorthIt.DefenderProfile> defenders, float minWinChance,
            out float win, out bool cover)
        {
            cover = ProfilesCoverAll(attackers, defenders);
            win = defenders.Count == 0
                ? 1f
                : WorthIt.WinChance((IReadOnlyCollection<WorthIt.DefenderProfile>)attackers,
                    (IReadOnlyCollection<WorthIt.DefenderProfile>)defenders, 0f);
            return cover && win >= minWinChance;
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
