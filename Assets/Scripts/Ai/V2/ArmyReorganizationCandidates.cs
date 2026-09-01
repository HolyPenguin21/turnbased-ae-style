using System.Collections.Generic;
using System.Linq;

namespace Game.Ai.V2
{
    public static partial class ArmyReorganizationPlanner
    {
        private static IEnumerable<VState> EnumerateCandidates(VState state)
        {
            List<int> armyIds = state.Meta.Keys.OrderBy(id => id).ToList();

            // 0. Commander reorder — zero-AP, membership-preserving. For any reorderable container
            // (field OR garrison) holding >= 2 heroes whose first hero is not the highest
            // CommandRating, promote the strongest hero so ComputeCapacity reads its rating.
            foreach (int armyId in armyIds)
            {
                ReorgContainer meta = state.Meta[armyId];
                if (!meta.CanChangeComposition)
                    continue;
                List<ReorgUnit> units = state.Roster[armyId];
                var heroes = units.Where(u => u.IsHero).ToList();
                if (heroes.Count < 2)
                    continue;
                ReorgUnit current = heroes[0];
                // §7 primary rule is maximum legal capacity == CommandRating; §8 role and combat
                // leadership only break CommandRating ties deterministically.
                ReorgUnit best = heroes
                    .OrderByDescending(h => h.CommandRating)
                    .ThenByDescending(h => (int)h.HeroRole == (int)HeroOperationalRole.CombatLeader ? 2
                        : (int)h.HeroRole == (int)HeroOperationalRole.Flexible ? 1 : 0)
                    .ThenByDescending(h => h.HeroCombatLeadership)
                    .ThenByDescending(h => h.Power)
                    .ThenBy(h => h.Key)
                    .First();
                if (ReferenceEquals(best, current) || best.CommandRating <= current.CommandRating)
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

                    ReorgUnit combatHero = BestBenchedHeroForField(srcUnits);
                    if (combatHero == null)
                        continue;
                    if (src.IsGarrison && !GarrisonMayRelease(srcUnits, combatHero, src))
                        continue;

                    VState swapped = TrySwap(state, srcId, combatHero, dstId, supportCommander);
                    if (swapped != null)
                    {
                        swapped.Transfers[swapped.Transfers.Count - 1].Reason =
                            "replace support operator with combat-capable field commander";
                        yield return swapped;
                    }
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
                if (dstUnits.Any(u => u.IsHero) || dstUnits.Count(u => !u.IsHero) < 2
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

                    ReorgUnit hero = BestBenchedHeroForField(srcUnits);
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
                        .Where(u => !u.IsHero && !u.IsCommitted && !u.IsAviation
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

            // 1. Absorb/combine only a STRUCTURALLY DEGRADED occupied mutable field container.
            // WorthPlanning also admits healthy groups now so the composition pass below can run;
            // therefore it can no longer serve as the implicit guard that kept viable armies out
            // of whole-fold consolidation. Without this local guard, two healthy armies can be
            // collapsed into one merely because the survivor has a higher minStrength, which is
            // not a structural Housekeeping improvement (and regresses the healthy-hex no-op).
            foreach (int srcId in armyIds)
            {
                ReorgContainer src = state.Meta[srcId];
                if (!IsFieldContainer(src) || !src.CanDonate)
                    continue;
                List<ReorgUnit> srcUnits = state.Roster[srcId];
                if (srcUnits.Count == 0 || srcUnits.Any(u =>
                    u.IsCommitted || u.IsAviation || state.MovedUnitKeys.Contains(u.Key)))
                    continue;
                if (ReorgViability.IsViable(srcUnits)
                    && !ReorgViability.IsSingletonShape(srcUnits))
                    continue; // healthy source: composition pass may rebalance/swap it, never erase it wholesale

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

            // 3. Seed a weak field container from a viable field donor or safe garrison surplus.
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
                    bool donorField = IsFieldContainer(donor);
                    if ((!donorField && !donor.IsGarrison) || !donor.CanDonate)
                        continue;
                    if (donorField && !ReorgViability.IsViable(state.Roster[donorId]))
                        continue;
                    VState c = TrySeed(state, donorId, weakId);
                    if (c != null)
                        yield return c;
                }
            }

            // 4a. One-way composition/strength redistribution between viable field armies.
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
                            "composition/strength redistribution");
                        if (c != null && ReorgViability.IsViable(c.Roster[srcId])
                            && ReorgViability.IsViable(c.Roster[dstId]))
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
        // (Housekeeping keeps those for base/research/production; an urgent raid takes its own
        // support-fallback path). CombatLeader before Flexible, then combat leadership, then key.
        private static ReorgUnit BestBenchedHeroForField(List<ReorgUnit> roster)
        {
            return roster
                .Where(u => u != null && u.IsHero && !u.IsCommitted
                    && u.HeroRole != HeroOperationalRole.SupportOperator)
                .OrderByDescending(u => u.HeroRole == HeroOperationalRole.CombatLeader ? 1 : 0)
                .ThenByDescending(u => u.HeroCombatLeadership)
                .ThenBy(u => u.Key)
                .FirstOrDefault();
        }

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
