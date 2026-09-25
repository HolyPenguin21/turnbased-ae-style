using System.Collections.Generic;
using System.Linq;
using Game.Combat;

namespace Game.Ai.V2
{
    public static partial class ArmyReorganizationPlanner
    {
        private static IEnumerable<VState> EnumerateCandidates(VState state)
        {
            List<int> armyIds = state.Meta.Keys.OrderBy(id => id).ToList();

            // 0. Required development operators belong under the strongest on-hex defence.
            // This is a contextual relocation into the existing garrison, not a permanent role;
            // the operator remains on the facility hex and all transfer/capacity/AP rules still apply.
            int operatorGarrisonId = armyIds.FirstOrDefault(id => state.Meta[id].IsGarrison);
            if (state.Meta.TryGetValue(operatorGarrisonId, out ReorgContainer operatorGarrison)
                && operatorGarrison.IsGarrison && operatorGarrison.CanReceive)
            {
                foreach (int srcId in armyIds)
                {
                    ReorgContainer src = state.Meta[srcId];
                    if (!IsFieldContainer(src) || !src.CanDonate)
                        continue;
                    foreach (ReorgUnit u in state.Roster[srcId]
                                 .Where(x => x != null && x.IsDevelopmentOperator)
                                 .OrderBy(x => x.Key))
                    {
                        VState moved = TryMoveOne(state, srcId, operatorGarrisonId, u,
                            "protect Research/Production operator in local garrison",
                            allowDevelopmentOperator: true);
                        if (moved != null)
                            yield return moved;
                    }
                }
            }

            // 0. Commander reorder — zero-AP, membership-preserving. For any reorderable container
            // (field OR garrison) holding >= 2 heroes, promote the one commander evaluation's best
            // hero (HeroRoleEvaluator — its formation's fight against the group's strongest threat,
            // then capacity, then role/leadership) so capacity, initiative and Fate follow it.
            IReadOnlyList<WorthIt.DefendingArmy> commandContext = CommandContext(state);
            foreach (int armyId in armyIds)
            {
                ReorgContainer meta = state.Meta[armyId];
                if (!meta.CanChangeComposition)
                    continue;
                List<ReorgUnit> units = state.Roster[armyId];
                if (units.Count(u => u != null && u.IsHero) < 2)
                    continue;
                ReorgUnit current = units.First(u => u != null && u.IsHero);
                ReorgUnit best = BestCommander(units, meta.IsGarrison, commandContext);
                if (best == null || ReferenceEquals(best, current))
                    continue;
                VState c = TryReorderCommander(state, armyId, best);
                if (c != null)
                    yield return c;
            }

            // 0.25 §8/§9 — if a viable combat formation is already led by a SupportOperator while
            // a better combat-capable hero is benched locally, exchange the commanders. This is
            // intentionally a hero-for-hero swap: the support hero goes back to the garrison/lone
            // bench instead of being discarded into a random body slot, membership counts stay
            // fixed, and both post-swap capacities are validated by TrySwap/ArmyActions.
            foreach (int dstId in armyIds)
            {
                ReorgContainer dst = state.Meta[dstId];
                if (!IsFieldContainer(dst) || !dst.CanChangeComposition
                    || !ReorgViability.IsViable(state.Roster[dstId]))
                    continue;
                List<ReorgUnit> dstUnits = state.Roster[dstId];
                ReorgUnit supportCommander = dstUnits.FirstOrDefault(u => u != null && u.IsHero);
                if (supportCommander == null || supportCommander.HeroRole != HeroOperationalRole.SupportOperator)
                    continue;

                foreach (int srcId in armyIds)
                {
                    if (srcId == dstId)
                        continue;
                    ReorgContainer src = state.Meta[srcId];
                    if (!src.CanDonate || !src.CanReceive || !src.CanChangeComposition)
                        continue;
                    List<ReorgUnit> srcUnits = state.Roster[srcId];
                    bool srcIsBench = src.IsGarrison
                        || (IsFieldContainer(src) && srcUnits.Count == 1 && srcUnits[0].IsHero);
                    if (!srcIsBench)
                        continue;

                    ReorgUnit combatHero = BestBenchedHeroForField(srcUnits, dstUnits, commandContext);
                    if (combatHero == null)
                        continue;
                    if (src.IsGarrison && !GarrisonMayRelease(srcUnits, combatHero, src))
                        continue;

                    VState swapped = TrySwap(state, srcId, combatHero, dstId, supportCommander);
                    if (swapped != null)
                        yield return swapped;
                }
            }

            // 0.5 §9 — give a heroless viable field formation a suitable benched combat hero from
            // the local garrison or a lone-hero container. Direct move when the destination has a
            // free slot; otherwise a canonical hero-for-body swap (the "no room in either army"
            // case the user called out — SwapMembers keeps both headcounts fixed while the hero
            // raises the destination's own Capacity). The greedy loop fills one formation per
            // iteration, so multiple heroless formations are led round-robin.
            foreach (int dstId in armyIds)
            {
                ReorgContainer dst = state.Meta[dstId];
                if (!IsFieldContainer(dst) || !dst.CanReceive)
                    continue;
                List<ReorgUnit> dstUnits = state.Roster[dstId];
                if (dstUnits.Any(u => u.IsHero) || dstUnits.Count(u => u.IsGroundCombatant) < 2
                    || !ReorgViability.IsViable(dstUnits))
                    continue;

                foreach (int srcId in armyIds)
                {
                    if (srcId == dstId)
                        continue;
                    ReorgContainer src = state.Meta[srcId];
                    if (!src.CanDonate)
                        continue;
                    List<ReorgUnit> srcUnits = state.Roster[srcId];
                    bool srcIsBench = src.IsGarrison
                        || (IsFieldContainer(src) && srcUnits.Count == 1 && srcUnits[0].IsHero);
                    if (!srcIsBench)
                        continue;

                    ReorgUnit hero = BestBenchedHeroForField(srcUnits, dstUnits, commandContext);
                    if (hero == null)
                        continue;
                    if (src.IsGarrison && !GarrisonMayRelease(srcUnits, hero, src))
                        continue;

                    VState moved = TryMoveOne(state, srcId, dstId, hero,
                        "assign benched combat hero to heroless field formation");
                    if (moved != null)
                    {
                        yield return moved;
                        continue;
                    }

                    ReorgUnit weakestBody = state.Roster[dstId]
                        .Where(u => u.IsGroundBattleBody && !u.IsCommitted
                            && !state.MovedUnitKeys.Contains(u.Key))
                        .OrderBy(u => u.Power).ThenBy(u => u.Key)
                        .FirstOrDefault();
                    if (weakestBody == null)
                        continue;
                    VState swapped = TrySwap(state, srcId, hero, dstId, weakestBody);
                    if (swapped != null)
                        yield return swapped;
                }
            }

            // 1. Whole-fold any occupied mutable field container when the transfer is physically
            // legal. Candidate generation owns legality only; policy belongs to Evaluate(). A
            // healthy viable source is therefore allowed to collapse into a stronger local field
            // formation when the strongest-first profile improves. The source ArmyData remains as
            // an empty reusable field shell — Housekeeping never destroys containers.
            foreach (int srcId in armyIds)
            {
                ReorgContainer src = state.Meta[srcId];
                if (!IsFieldContainer(src) || !src.CanDonate)
                    continue;
                List<ReorgUnit> srcUnits = state.Roster[srcId];
                if (srcUnits.Count == 0 || srcUnits.Any(u =>
                    u.IsCommitted || u.IsAviation || state.MovedUnitKeys.Contains(u.Key)))
                    continue;

                foreach (int dstId in OrderedDestinations(state, armyIds, srcId))
                {
                    VState c = TryWholeFold(state, srcId, dstId);
                    if (c != null)
                        yield return c;
                }
            }

            // 2. Deposit a singleton/non-viable field member into the local garrison.
            int garrisonId = armyIds.FirstOrDefault(id => state.Meta[id].IsGarrison);
            if (state.Meta.TryGetValue(garrisonId, out ReorgContainer garr)
                && garr.IsGarrison && garr.CanReceive)
            {
                foreach (int srcId in armyIds)
                {
                    ReorgContainer src = state.Meta[srcId];
                    if (!IsFieldContainer(src) || !src.CanDonate)
                        continue;
                    List<ReorgUnit> srcUnits = state.Roster[srcId];
                    if (srcUnits.Count == 0)
                        continue;
                    if (ReorgViability.IsViable(srcUnits) && !ReorgViability.IsSingletonShape(srcUnits))
                        continue;
                    foreach (ReorgUnit u in srcUnits.OrderBy(x => x.Key))
                    {
                        VState c = TryMoveOne(state, srcId, garrisonId, u,
                            "singleton/weak deposit into garrison");
                        if (c != null)
                            yield return c;
                    }
                }
            }

            // 2b. Strike force step 7 — a garrison below its non-hero floor takes ONE body from a
            // viable same-hex field army (the fist that just took the base): the most wounded,
            // then the weakest, never a hero. Evaluate() keeps it only if the field army's side
            // stays legal; the hand had its chance first (the held-base garrison demand, Phase A).
            if (state.Meta.TryGetValue(garrisonId, out ReorgContainer floorGarrison)
                && floorGarrison.IsGarrison && floorGarrison.CanReceive
                && state.Roster[garrisonId].Count(u => u.IsGroundCombatant) < floorGarrison.GarrisonNonHeroFloor)
            {
                foreach (int srcId in armyIds)
                {
                    ReorgContainer src = state.Meta[srcId];
                    if (!IsFieldContainer(src) || !src.CanDonate
                        || !ReorgViability.IsViable(state.Roster[srcId]))
                        continue;
                    ReorgUnit body = state.Roster[srcId]
                        .Where(u => u != null && !u.IsHero && u.IsGroundBattleBody && !u.IsAviation
                            && !u.IsCommitted)
                        .OrderBy(u => u.CombatProfile.MaxHitPoints > 0f
                            ? u.CombatProfile.HitPoints / u.CombatProfile.MaxHitPoints : 1f)
                        .ThenBy(u => WorthIt.CombatValue(u.CombatProfile))
                        .ThenBy(u => u.Key)
                        .FirstOrDefault();
                    if (body == null)
                        continue;
                    VState c = TryMoveOne(state, srcId, garrisonId, body,
                        "garrison floor from a viable same-hex field army (most wounded / weakest)");
                    if (c != null)
                        yield return c;
                }
            }

            // 3. Seed an occupied weak field container from a viable field donor. Empty reusable
            // shells are deliberately excluded: they are useful future containers, not defects to
            // be filled merely for housekeeping symmetry.
            foreach (int weakId in armyIds)
            {
                ReorgContainer weak = state.Meta[weakId];
                if (!IsFieldContainer(weak) || !weak.CanReceive)
                    continue;
                List<ReorgUnit> weakUnits = state.Roster[weakId];
                if (weakUnits.Count == 0 || ReorgViability.IsViable(weakUnits) || weak.SingletonExempt)
                    continue;

                foreach (int donorId in armyIds)
                {
                    if (donorId == weakId)
                        continue;
                    ReorgContainer donor = state.Meta[donorId];
                    // §P1 — the garrison is a defensive stack, not a spare-parts bin. Housekeeping
                    // never lends garrison bodies to make a purposeless weak/shell field army
                    // structurally viable; that force is folded/absorbed instead, or left for a
                    // deliberate AP-owning operational path.
                    if (!IsFieldContainer(donor) || !donor.CanDonate)
                        continue;
                    if (!ReorgViability.IsViable(state.Roster[donorId]))
                        continue;
                    VState c = TrySeed(state, donorId, weakId);
                    if (c != null)
                        yield return c;
                }
            }

            // 4a. One-way concentration/composition redistribution between viable field armies.
            // Do not require both projected rosters to remain viable here: generation owns legal
            // moves only. Evaluate() will reject an occupied singleton/non-viable intermediate via
            // the higher-priority structural tuple, while a legal path that leaves an empty shell
            // can win through the strongest-first formation profile.
            foreach (int srcId in armyIds)
            {
                ReorgContainer src = state.Meta[srcId];
                if (!IsFieldContainer(src) || !src.CanDonate
                    || !ReorgViability.IsViable(state.Roster[srcId]))
                    continue;

                foreach (int dstId in armyIds)
                {
                    if (dstId == srcId)
                        continue;
                    ReorgContainer dst = state.Meta[dstId];
                    if (!IsFieldContainer(dst) || !dst.CanReceive
                        || !ReorgViability.IsViable(state.Roster[dstId]))
                        continue;

                    foreach (ReorgUnit u in state.Roster[srcId].OrderBy(x => x.Key))
                    {
                        VState c = TryMoveOne(state, srcId, dstId, u,
                            "combat concentration/composition redistribution");
                        if (c != null)
                            yield return c;
                    }
                }
            }

            // 4b. Full/full composition improvement via canonical 1-for-1 SwapMembers.
            // Field-only: garrison replacement has separate secure-floor semantics.
            for (int i = 0; i < armyIds.Count; i++)
            {
                int aId = armyIds[i];
                ReorgContainer a = state.Meta[aId];
                if (!IsFieldContainer(a) || !a.CanChangeComposition
                    || !ReorgViability.IsViable(state.Roster[aId]))
                    continue;

                for (int j = i + 1; j < armyIds.Count; j++)
                {
                    int bId = armyIds[j];
                    ReorgContainer b = state.Meta[bId];
                    if (!IsFieldContainer(b) || !b.CanChangeComposition
                        || !ReorgViability.IsViable(state.Roster[bId]))
                        continue;

                    foreach (ReorgUnit ua in state.Roster[aId].OrderBy(x => x.Key))
                        foreach (ReorgUnit ub in state.Roster[bId].OrderBy(x => x.Key))
                        {
                            VState c = TrySwap(state, aId, ua, bId, ub);
                            if (c != null)
                                yield return c;
                        }
                }
            }
        }

        // §8/§9 — the best benched hero to lead a field formation: never a SupportOperator
        // (Housekeeping keeps those for base/research/production; an urgent operation takes its own
        // support-fallback path). CombatLeader before Flexible, then combat leadership, then key.
        // A benched (garrison / lone) hero that may lead `field`: never a SupportOperator, never a
        // committed or development-operator hero; among the rest, the one commander evaluation's
        // best for the field formation's own bodies.
        private static ReorgUnit BestBenchedHeroForField(List<ReorgUnit> roster,
            List<ReorgUnit> field, IReadOnlyList<WorthIt.DefendingArmy> context)
        {
            List<WorthIt.DefenderProfile> bodies = CommandBodies(field);
            return roster
                .Where(u => u != null && u.IsHero && !u.IsCommitted && !u.IsDevelopmentOperator
                    && u.HeroRole != HeroOperationalRole.SupportOperator)
                .Select(u => (unit: u, candidate: CommandCandidateFor(u, bodies, 0, context)))
                .OrderBy(x => x.candidate, Comparer<HeroRoleEvaluator.CommandCandidate>.Create(
                    HeroRoleEvaluator.CompareCandidates))
                .Select(x => x.unit)
                .FirstOrDefault();
        }

        // The fight Housekeeping judges a commander against: HeroRoleEvaluator's strongest-enemy
        // context over this group's benchmarks.
        private static IReadOnlyList<WorthIt.DefendingArmy> CommandContext(VState state) =>
            HeroRoleEvaluator.CommandContext(state.ThreatBenchmarks
                .Where(t => t != null)
                .Select(t => (t.ArmyId, t.Members, t.Commander)));

        // The best commander among the heroes already in `units` (null when none). Only a hero
        // the WHOLE current roster fits under may lead it — a reorder never breaks capacity
        // (ReorgViability.Capacity of the reordered roster). With no such hero the current
        // commander stays: there is no legal choice to make.
        private static ReorgUnit BestCommander(List<ReorgUnit> units, bool isGarrison,
            IReadOnlyList<WorthIt.DefendingArmy> context)
        {
            List<ReorgUnit> heroes = units.Where(u => u != null && u.IsHero).ToList();
            if (heroes.Count == 0)
                return null;
            List<ReorgUnit> legal = heroes
                .Where(h => ReorgViability.Capacity(LedBy(units, h), isGarrison) >= units.Count)
                .ToList();
            if (legal.Count == 0)
                return heroes[0];
            List<WorthIt.DefenderProfile> bodies = CommandBodies(units);
            return legal
                .Select(h => (unit: h, candidate: CommandCandidateFor(h, bodies, heroes.Count - 1, context)))
                .OrderBy(x => x.candidate, Comparer<HeroRoleEvaluator.CommandCandidate>.Create(
                    HeroRoleEvaluator.CompareCandidates))
                .First().unit;
        }

        // `units` with `hero` moved to the commander slot (TryReorderCommander's roster order).
        private static List<ReorgUnit> LedBy(List<ReorgUnit> units, ReorgUnit hero)
        {
            var roster = new List<ReorgUnit>(units.Count) { hero };
            roster.AddRange(units.Where(u => !ReferenceEquals(u, hero)));
            return roster;
        }

        private static List<WorthIt.DefenderProfile> CommandBodies(List<ReorgUnit> units) =>
            (units ?? new List<ReorgUnit>())
                .Where(u => u != null && u.IsGroundCombatant)
                .Select(u => u.CombatProfile)
                .ToList();

        private static HeroRoleEvaluator.CommandCandidate CommandCandidateFor(ReorgUnit hero,
            IEnumerable<WorthIt.DefenderProfile> bodies, int otherHeroes,
            IReadOnlyList<WorthIt.DefendingArmy> context) =>
            new HeroRoleEvaluator.CommandCandidate(
                HeroRoleEvaluator.ProjectCommand(hero.CommandRating, otherHeroes, hero.AsCommander,
                    bodies, context, 0f),
                HeroRoleEvaluator.RolePreference(hero.HeroRole), hero.HeroCombatLeadership,
                hero.CommandRating, hero.AsCommander.Fate, hero.Key);

        private static IEnumerable<int> OrderedDestinations(VState state, List<int> armyIds, int srcId)
        {
            var viable = new List<int>();
            var occupied = new List<int>();
            var shells = new List<int>();
            int garrison = -1;

            foreach (int id in armyIds)
            {
                if (id == srcId)
                    continue;
                ReorgContainer c = state.Meta[id];
                if (!c.CanReceive)
                    continue;
                if (c.IsGarrison) { garrison = id; continue; }
                if (!IsFieldContainer(c))
                    continue;
                if (state.Roster[id].Count == 0) { shells.Add(id); continue; }
                if (ReorgViability.IsViable(state.Roster[id])) viable.Add(id);
                else occupied.Add(id);
            }

            foreach (int id in viable) yield return id;
            foreach (int id in occupied) yield return id;
            if (garrison >= 0) yield return garrison;
            foreach (int id in shells) yield return id;
        }
    }
}
