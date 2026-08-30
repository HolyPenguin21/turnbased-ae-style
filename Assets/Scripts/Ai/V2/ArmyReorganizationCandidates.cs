using System.Collections.Generic;
using System.Linq;

namespace Game.Ai.V2
{
    public static partial class ArmyReorganizationPlanner
    {
        private static IEnumerable<VState> EnumerateCandidates(VState state)
        {
            List<int> armyIds = state.Meta.Keys.OrderBy(id => id).ToList();

            // 1. Absorb/combine an occupied mutable field container into one local destination.
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
