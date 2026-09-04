using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Game.Ai.V2
{
    // Prices the Scout portfolio against DISTINCT movers before ResourceAllocator sees AP.
    // Provisioning still owns the final live injective assignment, so the AP floor must remain safe
    // if it has to choose another eligible mover than the soft preferred witness.
    internal static class ScoutPricingWitness
    {
        private readonly struct Candidate
        {
            public readonly ArmySnapshot Army;
            public readonly float RequiredAp;
            public readonly int Eta;
            public readonly int Distance;
            public readonly bool StealthCapable;

            public Candidate(ArmySnapshot army, float requiredAp, int eta, int distance)
            {
                Army = army;
                RequiredAp = requiredAp;
                Eta = eta;
                Distance = distance;
                StealthCapable = army != null && (army.IsHidden || army.CanEnterStealth);
            }
        }

        public static void Apply(WorldSnapshot snap, IList<MissionProposal> proposals)
        {
            if (snap?.Self?.Armies == null || proposals == null || proposals.Count == 0)
                return;

            var open = proposals
                .Select((m, i) => (mission: m, ordinal: i))
                .Where(x => x.mission != null && x.mission.Kind == MissionKind.Scout
                    && x.mission.Target is ScoutMissionTarget)
                .ToList();
            if (open.Count == 0)
                return;

            var cands = new List<List<Candidate>>(open.Count);
            foreach (var item in open)
            {
                var target = (ScoutMissionTarget)item.mission.Target;
                bool requiredStealth = target.Stealth == StealthRequirement.Required;
                bool groundTarget = ReconScoutKinds.IsGround(target.Kind);
                var list = new List<Candidate>();
                foreach (ArmySnapshot mover in ScoutMoverSelector.Eligible(snap, target, null))
                {
                    ScoutPairCost pc = ScoutCostModel.PairCost(snap, mover, target.FocusHex, requiredStealth);
                    list.Add(new Candidate(mover, pc.RequiredAp,
                        groundTarget ? pc.EtaTurns : 0,
                        groundTarget ? pc.Distance : 0));
                }
                list.Sort((a, b) =>
                {
                    int c = a.RequiredAp.CompareTo(b.RequiredAp); if (c != 0) return c;
                    c = a.Eta.CompareTo(b.Eta); if (c != 0) return c;
                    c = a.Distance.CompareTo(b.Distance); if (c != 0) return c;
                    return a.Army.ArmyId.CompareTo(b.Army.ArmyId);
                });
                cands.Add(list);
            }

            var chosen = new int[open.Count];
            var best = new int[open.Count];
            for (int i = 0; i < best.Length; i++) best[i] = -1;
            long[] bestKey = null;
            Recurse(0, open, cands, chosen, new HashSet<int>(), ref bestKey, best);

            for (int i = 0; i < open.Count; i++)
            {
                if (best[i] < 0)
                    continue;
                MissionProposal m = open[i].mission;
                Candidate c = cands[i][best[i]];
                MissionRequirements r = m.Requirements;
                if (r == null)
                    continue;

                // PreferredMover is soft. A later funded subset can make Provisioning choose a
                // different eligible scout than this full-beam witness. Price the strict envelope
                // at the worst AP claim among currently eligible actors so that reassignment cannot
                // manufacture an EnvelopeTooSmall retry. This reserves budget only; execution still
                // spends the authoritative live activation/stealth claim and unused AP stays real.
                float assignmentSafeAp = cands[i].Count > 0
                    ? cands[i].Max(x => x.RequiredAp)
                    : c.RequiredAp;

                m.PreferredMoverArmyId = c.Army.ArmyId;
                r.MoverKnown = true;
                r.ApMinimum = assignmentSafeAp;
                r.ApDesired = assignmentSafeAp;
                r.ApMaximum = Mathf.Max(r.ApMaximum, assignmentSafeAp);
                ScoutTargetKind kind = ((ScoutMissionTarget)m.Target).Kind;
                if (ReconScoutKinds.IsGround(kind))
                {
                    r.EtaTurns = c.Eta;
                    r.EstimatedDistance = c.Distance;
                }

                AiDebugLog.Write($"[AI][V2]   mission pricing witness — {StableMissionKey.For(m)} "
                    + $"-> #{c.Army.ArmyId} actorAp {c.RequiredAp:0.#} safeAp {assignmentSafeAp:0.#} "
                    + $"eta {c.Eta} d{c.Distance}");
            }
        }

        private static void Recurse(int i,
            List<(MissionProposal mission, int ordinal)> open, List<List<Candidate>> cands,
            int[] chosen, HashSet<int> used, ref long[] bestKey, int[] best)
        {
            if (i == open.Count)
            {
                long[] key = Score(open, cands, chosen);
                if (bestKey == null || Lex(key, bestKey) < 0)
                {
                    bestKey = key;
                    Array.Copy(chosen, best, chosen.Length);
                }
                return;
            }

            chosen[i] = -1;
            Recurse(i + 1, open, cands, chosen, used, ref bestKey, best);
            for (int c = 0; c < cands[i].Count; c++)
            {
                int id = cands[i][c].Army.ArmyId;
                if (!used.Add(id))
                    continue;
                chosen[i] = c;
                Recurse(i + 1, open, cands, chosen, used, ref bestKey, best);
                used.Remove(id);
            }
            chosen[i] = -1;
        }

        private static long[] Score(List<(MissionProposal mission, int ordinal)> open,
            List<List<Candidate>> cands, int[] chosen)
        {
            int n = open.Count;
            int covered = 0;
            long priorityCoverage = 0;
            int actorDiscontinuity = 0;
            int wastedStealth = 0;
            long ap = 0, eta = 0, distance = 0, actorSum = 0;

            for (int i = 0; i < n; i++)
            {
                if (chosen[i] < 0) continue;
                Candidate c = cands[i][chosen[i]];
                covered++;
                priorityCoverage += n - i;
                actorSum += c.Army.ArmyId;
                ap += Mathf.RoundToInt(c.RequiredAp * 1000f);
                eta += c.Eta;
                distance += c.Distance;

                int? preferred = open[i].mission.PreferredMoverArmyId;
                if (preferred.HasValue && preferred.Value != c.Army.ArmyId
                    && cands[i].Any(x => x.Army.ArmyId == preferred.Value))
                    actorDiscontinuity++;

                var target = (ScoutMissionTarget)open[i].mission.Target;
                if (target.Stealth != StealthRequirement.Required && c.StealthCapable
                    && cands[i].Any(x => !x.StealthCapable))
                    wastedStealth++;
            }

            var key = new long[7 + n];
            key[0] = -covered;
            key[1] = -priorityCoverage;
            key[2] = actorDiscontinuity;
            key[3] = wastedStealth;
            key[4] = ap;
            key[5] = eta + distance;
            key[6] = actorSum;
            for (int i = 0; i < n; i++)
                key[7 + i] = chosen[i] < 0 ? long.MaxValue : cands[i][chosen[i]].Army.ArmyId;
            return key;
        }

        private static int Lex(long[] a, long[] b)
        {
            for (int i = 0; i < a.Length; i++)
            {
                int c = a[i].CompareTo(b[i]);
                if (c != 0) return c;
            }
            return 0;
        }
    }
}
