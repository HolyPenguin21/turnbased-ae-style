using System;
using System.Collections.Generic;
using System.Linq;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  ARMY REORGANIZATION PLANNER  (Strategy V2 — HousekeepingManager, step 8C)
    // ===========================================================================================
    //  PURE, DETERMINISTIC. Given one immutable LocalForceGroup it produces a complete same-hex
    //  ReorganizationPlan and mutates nothing. It is NOT a strategy layer — it never reasons about
    //  future turns. The only question it answers: "of the legal same-hex structures reachable
    //  from the CURRENT local formations, which is structurally better?"
    //
    //  POLICY — staged / lexicographic, NOT one weighted score. A candidate move is accepted only
    //  if the resulting outcome tuple is strictly lower than the current one:
    //
    //     ( garrisonSafetyDeficit,        // hard — never worsen a garrison's secure-floor deficit
    //       legalityViolations,           // hard — never leave a container over capacity
    //       nonExemptSingletonCount,      // remove pointless singleton formations
    //       nonViableOccupiedCount,       // reduce non-viable occupied formations
    //       -minViableFieldStrength,      // raise the weakest viable field army
    //       -compositionQuality,          // better front/reach + hero spread
    //       transferCount )               // fewer operations
    //
    //  Because every candidate adds transfers, a candidate that improves no earlier term can never
    //  beat the current tuple — so the greedy loop terminates on its own (and is bounded anyway).
    //  Mission ownership / aviation / prison / solo-recce containers are excluded from the mutable
    //  pool up front (CanDonate/CanReceive == false), so protected-violation is structural, not a
    //  tuple term.
    // ===========================================================================================
    public static class ArmyReorganizationPlanner
    {
        private const float FloatEps = 0.001f;

        // Lower is better on every slot. Floats are stored already negated where "more is better".
        private readonly struct Outcome
        {
            public readonly int GarrisonDeficit;
            public readonly int Legality;
            public readonly int Singletons;
            public readonly int NonViable;
            public readonly float NegMinStrength;
            public readonly float NegComposition;
            public readonly int Transfers;

            public Outcome(int gd, int legal, int singles, int nonViable, float negMin, float negComp, int transfers)
            {
                GarrisonDeficit = gd;
                Legality = legal;
                Singletons = singles;
                NonViable = nonViable;
                NegMinStrength = negMin;
                NegComposition = negComp;
                Transfers = transfers;
            }

            // <0 : this is strictly better than other. >0 : worse. 0 : indistinguishable.
            public int CompareTo(in Outcome o)
            {
                int c = GarrisonDeficit.CompareTo(o.GarrisonDeficit); if (c != 0) return c;
                c = Legality.CompareTo(o.Legality); if (c != 0) return c;
                c = Singletons.CompareTo(o.Singletons); if (c != 0) return c;
                c = NonViable.CompareTo(o.NonViable); if (c != 0) return c;
                if (NegMinStrength < o.NegMinStrength - FloatEps) return -1;
                if (NegMinStrength > o.NegMinStrength + FloatEps) return 1;
                if (NegComposition < o.NegComposition - FloatEps) return -1;
                if (NegComposition > o.NegComposition + FloatEps) return 1;
                return Transfers.CompareTo(o.Transfers);
            }
        }

        // Working copy of one container's roster inside the virtual planning state.
        private sealed class VState
        {
            public readonly Dictionary<int, List<ReorgUnit>> Roster = new Dictionary<int, List<ReorgUnit>>();
            public readonly Dictionary<int, ReorgContainer> Meta = new Dictionary<int, ReorgContainer>();
            public readonly List<PlannedTransfer> Transfers = new List<PlannedTransfer>();

            public VState Clone()
            {
                var v = new VState();
                foreach (var kv in Meta) v.Meta[kv.Key] = kv.Value;
                foreach (var kv in Roster) v.Roster[kv.Key] = new List<ReorgUnit>(kv.Value);
                v.Transfers.AddRange(Transfers);
                return v;
            }
        }

        public static ReorganizationPlan Plan(LocalForceGroup group)
        {
            var plan = new ReorganizationPlan { Q = group?.Q ?? 0, R = group?.R ?? 0 };
            if (group == null || !group.WorthPlanning())
            {
                if (group != null)
                    foreach (ReorgContainer c in group.Containers)
                        plan.ExpectedMembership[c.ArmyId] = c.Units.Select(u => u.Key).ToList();
                return plan;
            }

            var state = new VState();
            foreach (ReorgContainer c in group.Containers)
            {
                state.Meta[c.ArmyId] = c;
                state.Roster[c.ArmyId] = new List<ReorgUnit>(c.Units);
            }

            int iterations = 0;
            while (iterations++ < AiConfigV2.housekeepingMaxPlanIterationsPerHex)
            {
                Outcome current = Evaluate(state);
                VState best = null;
                Outcome bestOutcome = current;

                foreach (VState candidate in EnumerateCandidates(state))
                {
                    Outcome o = Evaluate(candidate);
                    if (o.Legality > 0)
                        continue; // never a legal candidate
                    if (o.CompareTo(bestOutcome) < 0)
                    {
                        best = candidate;
                        bestOutcome = o;
                    }
                }

                if (best == null)
                    break;
                // Adopt the improvement wholesale (it already carries its transfers).
                state.Roster.Clear();
                foreach (var kv in best.Roster) state.Roster[kv.Key] = kv.Value;
                state.Transfers.Clear();
                state.Transfers.AddRange(best.Transfers);
            }

            plan.Transfers.AddRange(state.Transfers);
            foreach (var kv in state.Roster)
                plan.ExpectedMembership[kv.Key] = kv.Value.Select(u => u.Key).ToList();
            return plan;
        }

        // ---- candidate generation ---------------------------------------------------------
        //  Deterministic: containers in ArmyId order, units in Key order, destination classes in
        //  the design-doc resolution order (existing occupied army -> garrison -> empty shell) so
        //  an exact tuple tie resolves the documented way.
        private static IEnumerable<VState> EnumerateCandidates(VState state)
        {
            List<int> armyIds = state.Meta.Keys.OrderBy(id => id).ToList();

            // (1) whole-source fold — empty a mutable ground field army into ONE destination.
            foreach (int srcId in armyIds)
            {
                ReorgContainer src = state.Meta[srcId];
                if (src.Role != ReorgPhysicalRole.NormalFieldArmy || !src.CanDonate)
                    continue;
                List<ReorgUnit> srcUnits = state.Roster[srcId];
                if (srcUnits.Count == 0)
                    continue;
                if (srcUnits.Any(u => u.IsCommitted || u.IsAviation))
                    continue; // can't fully empty it -> not a fold candidate

                foreach (int dstId in OrderedDestinations(state, armyIds, srcId))
                {
                    VState c = TryWholeFold(state, srcId, dstId);
                    if (c != null)
                        yield return c;
                }
            }

            // (2) single-unit garrison deposit — from a singleton / non-viable field army.
            int garrisonId = armyIds.FirstOrDefault(id => state.Meta[id].IsGarrison);
            if (state.Meta.TryGetValue(garrisonId, out ReorgContainer garr) && garr.IsGarrison && garr.CanReceive)
            {
                foreach (int srcId in armyIds)
                {
                    ReorgContainer src = state.Meta[srcId];
                    if (src.Role != ReorgPhysicalRole.NormalFieldArmy || !src.CanDonate)
                        continue;
                    List<ReorgUnit> srcUnits = state.Roster[srcId];
                    if (srcUnits.Count == 0)
                        continue;
                    if (ReorgViability.IsViable(srcUnits) && !ReorgViability.IsSingletonShape(srcUnits))
                        continue; // healthy army — leave it
                    foreach (ReorgUnit u in srcUnits.OrderBy(x => x.Key))
                    {
                        VState c = TryMoveOne(state, srcId, garrisonId, u, "singleton/weak deposit into garrison");
                        if (c != null)
                            yield return c;
                    }
                }
            }

            // (3) seed — a viable donor (field army or garrison surplus) tops a weak army up to
            //     viability; the donor must stay viable / secure afterwards.
            foreach (int weakId in armyIds)
            {
                ReorgContainer weak = state.Meta[weakId];
                if (weak.Role != ReorgPhysicalRole.NormalFieldArmy || !weak.CanReceive)
                    continue;
                List<ReorgUnit> weakUnits = state.Roster[weakId];
                if (weakUnits.Count == 0 || ReorgViability.IsViable(weakUnits) || weak.SingletonExempt)
                    continue;

                foreach (int donorId in armyIds)
                {
                    if (donorId == weakId)
                        continue;
                    ReorgContainer donor = state.Meta[donorId];
                    bool donorField = donor.Role == ReorgPhysicalRole.NormalFieldArmy;
                    if ((!donorField && !donor.IsGarrison) || !donor.CanDonate)
                        continue;
                    if (donorField && !ReorgViability.IsViable(state.Roster[donorId]))
                        continue;
                    VState c = TrySeed(state, donorId, weakId);
                    if (c != null)
                        yield return c;
                }
            }
        }

        // Existing occupied viable army -> occupied non-viable army -> garrison -> empty shell.
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
                if (c.Role == ReorgPhysicalRole.EmptyReusableArmy || state.Roster[id].Count == 0) { shells.Add(id); continue; }
                if (c.Role == ReorgPhysicalRole.NormalFieldArmy)
                {
                    if (ReorgViability.IsViable(state.Roster[id])) viable.Add(id);
                    else occupied.Add(id);
                }
            }
            foreach (int id in viable) yield return id;
            foreach (int id in occupied) yield return id;
            if (garrison >= 0) yield return garrison;
            foreach (int id in shells) yield return id;
        }

        // ---- primitive virtual transfers ------------------------------------------------

        private static VState TryWholeFold(VState state, int srcId, int dstId)
        {
            VState c = state.Clone();
            List<ReorgUnit> from = c.Roster[srcId];
            List<ReorgUnit> to = c.Roster[dstId];
            ReorgContainer srcMeta = c.Meta[srcId];
            ReorgContainer dstMeta = c.Meta[dstId];

            // non-heroes first, then heroes (a hero leaving last keeps the source legal longest)
            List<ReorgUnit> ordered = from.Where(u => !u.IsHero).OrderBy(u => u.Key)
                .Concat(from.Where(u => u.IsHero).OrderBy(u => u.Key)).ToList();

            foreach (ReorgUnit u in ordered)
            {
                if (!ReorgViability.CanLeaveWithoutOvercrowding(from, u, srcMeta.IsGarrison))
                    return null;
                if (!CanAccept(to, u, dstMeta.IsGarrison))
                    return null;
                from.Remove(u);
                to.Add(u);
                c.Transfers.Add(new PlannedTransfer(u.Key, srcId, dstId, "fold weak/singleton army into destination"));
            }
            return c;
        }

        private static VState TryMoveOne(VState state, int srcId, int dstId, ReorgUnit unit, string reason)
        {
            VState c = state.Clone();
            List<ReorgUnit> from = c.Roster[srcId];
            List<ReorgUnit> to = c.Roster[dstId];
            ReorgUnit u = from.FirstOrDefault(x => x.Key == unit.Key);
            if (u == null || u.IsCommitted || u.IsAviation)
                return null;
            ReorgContainer srcMeta = c.Meta[srcId];
            ReorgContainer dstMeta = c.Meta[dstId];

            if (srcMeta.IsGarrison && !GarrisonMayRelease(from, u, srcMeta))
                return null;
            if (!ReorgViability.CanLeaveWithoutOvercrowding(from, u, srcMeta.IsGarrison))
                return null;
            if (!CanAccept(to, u, dstMeta.IsGarrison))
                return null;

            from.Remove(u);
            to.Add(u);
            c.Transfers.Add(new PlannedTransfer(u.Key, srcId, dstId, reason));
            return c;
        }

        private static VState TrySeed(VState state, int donorId, int weakId)
        {
            VState c = state.Clone();
            List<ReorgUnit> donor = c.Roster[donorId];
            List<ReorgUnit> weak = c.Roster[weakId];
            ReorgContainer donorMeta = c.Meta[donorId];
            ReorgContainer weakMeta = c.Meta[weakId];

            // Give the weak army the donor's weakest spare non-heroes, one at a time, until it is
            // viable — stop the instant the donor would drop below viability / secure floor.
            List<ReorgUnit> spare = donor.Where(u => !u.IsHero && !u.IsCommitted && !u.IsAviation)
                .OrderBy(u => u.Power).ThenBy(u => u.Key).ToList();

            bool movedAny = false;
            foreach (ReorgUnit u in spare)
            {
                if (ReorgViability.IsViable(weak))
                    break;
                if (donorMeta.IsGarrison && !GarrisonMayRelease(donor, u, donorMeta))
                    break;
                if (!ReorgViability.CanLeaveWithoutOvercrowding(donor, u, donorMeta.IsGarrison))
                    break;
                if (!CanAccept(weak, u, weakMeta.IsGarrison))
                    break;

                var after = donor.Where(x => x != u).ToList();
                if (!donorMeta.IsGarrison && !ReorgViability.IsViable(after))
                    break; // never weaken a field donor below viability just to help another army

                donor.Remove(u);
                weak.Add(u);
                c.Transfers.Add(new PlannedTransfer(u.Key, donorId, weakId, "seed weak army from donor surplus"));
                movedAny = true;
            }

            return movedAny && ReorgViability.IsViable(weak) ? c : null;
        }

        private static bool CanAccept(List<ReorgUnit> dest, ReorgUnit u, bool destIsGarrison)
        {
            if (u.IsAviation)
                return false;
            var after = new List<ReorgUnit>(dest) { u };
            return ReorgViability.Capacity(after, destIsGarrison) >= after.Count;
        }

        // Canonical secure-floor rule (mirrors AiArmyRoles.CanSpareGarrisonMember with
        // allowCitadelEmergency:false): a non-hero may leave only if the floor still holds; a hero
        // may leave only if it is not the literal last member.
        private static bool GarrisonMayRelease(List<ReorgUnit> garrison, ReorgUnit u, ReorgContainer meta)
        {
            if (u.IsHero)
                return garrison.Count > 1;
            int remainingNonHero = garrison.Count(x => !x.IsHero) - 1;
            return remainingNonHero >= meta.GarrisonNonHeroFloor;
        }

        // ---- evaluation ---------------------------------------------------------------------

        private static Outcome Evaluate(VState s)
        {
            int garrisonDeficit = 0, legality = 0, singles = 0, nonViable = 0;
            float minStrength = float.MaxValue;
            float composition = 0f;
            bool anyViable = false;

            foreach (int id in s.Meta.Keys)
            {
                ReorgContainer meta = s.Meta[id];
                List<ReorgUnit> units = s.Roster[id];

                if (ReorgViability.Capacity(units, meta.IsGarrison) < units.Count)
                    legality++;

                if (meta.IsGarrison)
                {
                    int nonHero = units.Count(u => !u.IsHero);
                    garrisonDeficit += Math.Max(0, meta.GarrisonNonHeroFloor - nonHero);
                    if (units.Count == 0 && meta.GarrisonNonHeroFloor > 0)
                        garrisonDeficit++;
                    continue;
                }

                if (meta.Role != ReorgPhysicalRole.NormalFieldArmy || !meta.CanChangeComposition)
                    continue;
                if (units.Count == 0)
                    continue; // an emptied shell is a good outcome, never counted

                if (!meta.SingletonExempt && ReorgViability.IsSingletonShape(units))
                    singles++;

                if (ReorgViability.IsViable(units))
                {
                    anyViable = true;
                    float p = ReorgViability.StackPower(units);
                    if (p < minStrength) minStrength = p;
                    composition += CompositionScore(units);
                }
                else if (!meta.SingletonExempt)
                {
                    nonViable++;
                }
            }

            float negMin = anyViable ? -minStrength : 0f;
            return new Outcome(garrisonDeficit, legality, singles, nonViable, negMin, -composition, s.Transfers.Count);
        }

        private static float CompositionScore(IReadOnlyList<ReorgUnit> units)
        {
            bool front = false, reach = false, hero = false;
            foreach (ReorgUnit u in units)
            {
                if (u.IsHero) hero = true;
                if (u.Range <= 1) front = true; else reach = true;
            }
            return (front && reach ? 1f : 0f) + (hero ? 1f : 0f);
        }
    }
}
