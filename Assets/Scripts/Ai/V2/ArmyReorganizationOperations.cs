using System.Collections.Generic;
using System.Linq;

namespace Game.Ai.V2
{
    public static partial class ArmyReorganizationPlanner
    {
        // §7 — zero-AP roster reorder inside one container: move `hero` to the front of the
        // roster (heroes stay a contiguous prefix). Membership and counts are unchanged.
        private static VState TryReorderCommander(VState state, int armyId, ReorgUnit hero)
        {
            VState c = state.Clone();
            List<ReorgUnit> roster = c.Roster[armyId];
            ReorgUnit u = roster.FirstOrDefault(x => x.Key == hero.Key);
            if (u == null || !u.IsHero || u.IsCommitted || c.MovedUnitKeys.Contains(u.Key))
                return null;
            int idx = roster.IndexOf(u);
            if (idx <= 0)
                return null;
            roster.RemoveAt(idx);
            roster.Insert(0, u);
            c.Transfers.Add(PlannedTransfer.Reorder(u.Key, armyId,
                "promote highest-capacity hero to commander"));
            c.MovedUnitKeys.Add(u.Key);
            return c;
        }

        private static VState TryWholeFold(VState state, int srcId, int dstId)
        {
            List<ReorgUnit> source = state.Roster[srcId];

            // Prefer source-safe non-hero-first. If destination capacity requires its incoming hero
            // first, retry hero-first. Same final roster; only the legal sequence differs.
            List<ReorgUnit> nonHeroFirst = source.Where(u => !u.IsHero).OrderBy(u => u.Key)
                .Concat(source.Where(u => u.IsHero).OrderBy(u => u.Key)).ToList();
            VState result = TryWholeFoldWithOrder(state, srcId, dstId, nonHeroFirst);
            if (result != null)
                return result;

            List<ReorgUnit> heroFirst = source.Where(u => u.IsHero).OrderBy(u => u.Key)
                .Concat(source.Where(u => !u.IsHero).OrderBy(u => u.Key)).ToList();
            return TryWholeFoldWithOrder(state, srcId, dstId, heroFirst);
        }

        private static VState TryWholeFoldWithOrder(VState state, int srcId, int dstId,
            List<ReorgUnit> order)
        {
            VState c = state.Clone();
            List<ReorgUnit> from = c.Roster[srcId];
            List<ReorgUnit> to = c.Roster[dstId];
            ReorgContainer srcMeta = c.Meta[srcId];
            ReorgContainer dstMeta = c.Meta[dstId];

            foreach (ReorgUnit original in order)
            {
                ReorgUnit u = from.FirstOrDefault(x => x.Key == original.Key);
                if (u == null || c.MovedUnitKeys.Contains(u.Key) || u.IsCommitted || u.IsAviation)
                    return null;
                if (!ReorgViability.CanLeaveWithoutOvercrowding(from, u, srcMeta.IsGarrison))
                    return null;
                if (!CanAccept(to, u, dstMeta))
                    return null;

                from.Remove(u);
                ReorgViability.AddMemberSorted(to, u);
                c.Transfers.Add(new PlannedTransfer(u.Key, srcId, dstId,
                    "fold weak/singleton army into destination"));
                c.MovedUnitKeys.Add(u.Key);
            }
            return c;
        }

        private static VState TryMoveOne(VState state, int srcId, int dstId, ReorgUnit unit,
            string reason)
        {
            VState c = state.Clone();
            List<ReorgUnit> from = c.Roster[srcId];
            List<ReorgUnit> to = c.Roster[dstId];
            ReorgUnit u = from.FirstOrDefault(x => x.Key == unit.Key);
            if (u == null || c.MovedUnitKeys.Contains(u.Key) || u.IsCommitted || u.IsAviation)
                return null;

            ReorgContainer srcMeta = c.Meta[srcId];
            ReorgContainer dstMeta = c.Meta[dstId];
            if (srcMeta.IsGarrison && !GarrisonMayRelease(from, u, srcMeta))
                return null;
            if (!ReorgViability.CanLeaveWithoutOvercrowding(from, u, srcMeta.IsGarrison))
                return null;
            if (!CanAccept(to, u, dstMeta))
                return null;

            from.Remove(u);
            ReorgViability.AddMemberSorted(to, u);
            c.Transfers.Add(new PlannedTransfer(u.Key, srcId, dstId, reason));
            c.MovedUnitKeys.Add(u.Key);
            return c;
        }

        private static VState TrySeed(VState state, int donorId, int weakId)
        {
            VState c = state.Clone();
            List<ReorgUnit> donor = c.Roster[donorId];
            List<ReorgUnit> weak = c.Roster[weakId];
            ReorgContainer donorMeta = c.Meta[donorId];
            ReorgContainer weakMeta = c.Meta[weakId];

            List<ReorgUnit> spare = donor.Where(u => !u.IsHero && !u.IsCommitted && !u.IsAviation
                    && !c.MovedUnitKeys.Contains(u.Key))
                .OrderBy(u => u.Power).ThenBy(u => u.Key).ToList();

            bool movedAny = false;
            foreach (ReorgUnit original in spare)
            {
                if (ReorgViability.IsViable(weak))
                    break;
                ReorgUnit u = donor.FirstOrDefault(x => x.Key == original.Key);
                if (u == null)
                    continue;
                if (donorMeta.IsGarrison && !GarrisonMayRelease(donor, u, donorMeta))
                    break;
                if (!ReorgViability.CanLeaveWithoutOvercrowding(donor, u, donorMeta.IsGarrison))
                    break;
                if (!CanAccept(weak, u, weakMeta))
                    break;

                var after = donor.Where(x => x != u).ToList();
                if (!donorMeta.IsGarrison && !ReorgViability.IsViable(after))
                    break;

                donor.Remove(u);
                ReorgViability.AddMemberSorted(weak, u);
                c.Transfers.Add(new PlannedTransfer(u.Key, donorId, weakId,
                    "seed weak army from donor surplus"));
                c.MovedUnitKeys.Add(u.Key);
                movedAny = true;
            }

            return movedAny && ReorgViability.IsViable(weak) ? c : null;
        }

        private static VState TrySwap(VState state, int aId, ReorgUnit unitA,
            int bId, ReorgUnit unitB)
        {
            VState c = state.Clone();
            ReorgContainer aMeta = c.Meta[aId];
            ReorgContainer bMeta = c.Meta[bId];
            List<ReorgUnit> a = c.Roster[aId];
            List<ReorgUnit> b = c.Roster[bId];
            ReorgUnit ua = a.FirstOrDefault(x => x.Key == unitA.Key);
            ReorgUnit ub = b.FirstOrDefault(x => x.Key == unitB.Key);

            if (ua == null || ub == null || ua.IsCommitted || ub.IsCommitted
                || ua.IsAviation || ub.IsAviation
                || c.MovedUnitKeys.Contains(ua.Key) || c.MovedUnitKeys.Contains(ub.Key))
                return null;

            // ua enters B, ub enters A. With a zero Housekeeping AP reserve both receivers must be
            // free under ArmyActions' real activated-destination rule.
            if ((bMeta.HasActivatedThisTurn && ua.ActivationApCost > 0)
                || (aMeta.HasActivatedThisTurn && ub.ActivationApCost > 0))
                return null;

            var afterA = new List<ReorgUnit>(a);
            afterA.Remove(ua);
            ReorgViability.AddMemberSorted(afterA, ub);
            var afterB = new List<ReorgUnit>(b);
            afterB.Remove(ub);
            ReorgViability.AddMemberSorted(afterB, ua);

            if (ReorgViability.Capacity(afterA, aMeta.IsGarrison) < afterA.Count
                || ReorgViability.Capacity(afterB, bMeta.IsGarrison) < afterB.Count)
                return null;

            a.Clear(); a.AddRange(afterA);
            b.Clear(); b.AddRange(afterB);
            c.Transfers.Add(PlannedTransfer.Swap(ua.Key, aId, ub.Key, bId,
                "composition swap between viable armies"));
            c.MovedUnitKeys.Add(ua.Key);
            c.MovedUnitKeys.Add(ub.Key);
            return c;
        }

        private static bool CanAccept(List<ReorgUnit> dest, ReorgUnit u, ReorgContainer destMeta)
        {
            if (u.IsAviation)
                return false;
            if (destMeta.HasActivatedThisTurn && u.ActivationApCost > 0)
                return false; // TransferMember would spend AP, which Step 8C does not own.

            var after = new List<ReorgUnit>(dest);
            ReorgViability.AddMemberSorted(after, u);
            return ReorgViability.Capacity(after, destMeta.IsGarrison) >= after.Count;
        }

        private static bool GarrisonMayRelease(List<ReorgUnit> garrison, ReorgUnit u,
            ReorgContainer meta)
        {
            if (u.IsHero)
            {
                if (garrison.Count <= 1)
                    return false;
            }
            else
            {
                int remainingNonHero = garrison.Count(x => !x.IsHero) - 1;
                if (remainingNonHero < meta.GarrisonNonHeroFloor)
                    return false;
            }
            // §P1 — headcount is not enough: a garrison that currently HOLDS a real defensive
            // power reserve must not be dropped below it by a zero-AP reorg move (a small,
            // already-below-reserve second base is still governed by the headcount floor above,
            // exactly as before).
            float before = ReorgViability.EffectivePower(garrison);
            if (before < AiConfigV2.housekeepingGarrisonReservePower)
                return true;
            var after = garrison.Where(x => x != u).ToList();
            return ReorgViability.EffectivePower(after) >= AiConfigV2.housekeepingGarrisonReservePower;
        }
    }
}
