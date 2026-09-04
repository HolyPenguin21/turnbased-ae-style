using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;

using Game.Combat;

namespace Game.Ai.V2
{
    // Proposal-side physical feasibility for Raid. Like ScoutAdmissionRegistry, this NEVER binds
    // an actor; ProvisioningManager remains authoritative. It only records which ready ground
    // armies can independently clear the SAME WorthIt estimator for each target, so obvious
    // same-turn actor collisions can be rejected as portfolio admission rather than discovered as
    // a fake structural target failure after funding.
    internal static class RaidAdmissionRegistry
    {
        private sealed class Entry
        {
            public readonly HashSet<int> EligibleArmyIds;
            public Entry(IEnumerable<int> ids) => EligibleArmyIds = new HashSet<int>(ids);
        }

        private static readonly ConditionalWeakTable<MissionProposal, Entry> ByProposal =
            new ConditionalWeakTable<MissionProposal, Entry>();

        public static void Record(MissionProposal proposal, WorldSnapshot snap)
        {
            if (proposal == null || snap == null || !(proposal.Target is RaidMissionTarget target))
                return;

            IReadOnlyList<WorthIt.DefenderProfile> defenders = DefendersFor(snap, target.TargetArmyId);
            var excluded = new HashSet<int>();
            var ids = new List<int>();

            // RaidAssemblyPlanner.Plan always applies the STRICT fresh-raid win gate and returns the
            // strongest currently-eligible ready actor. Re-run while excluding each hit to enumerate
            // the whole fresh set without duplicating its eligibility or WorthIt rules here.
            while (true)
            {
                RaidAssemblyPlan plan = RaidAssemblyPlanner.Plan(snap, target, defenders, excluded);
                if (!plan.Feasible || !excluded.Add(plan.BaseArmyId))
                    break;
                ids.Add(plan.BaseArmyId);
            }

            // A started Hard Raid is not a fresh admission decision. Its PreferredMover already
            // passed the strict gate when the operation began and continuity/ActorCommitments owns
            // that physical actor across turns. Re-test that exact incumbent through the bounded
            // continuation gate so a small Monte-Carlo drop (the observed ~0.78 -> ~0.41 case) does
            // not produce the impossible state "Hard/CLAIM actor #X" + "readyActors=[none]".
            //
            // If the incumbent passes, PIN the operation to it. PrepareRaidAssignments deliberately
            // sorts actors by activation/power and otherwise has no knowledge of PreferredMover; if
            // we left fresh actors in the set it could silently switch a Hard operation to another
            // army and orphan the physical force continuity just protected. If the incumbent fails
            // the continuation gate, the strict fresh set remains available as a legitimate fallback.
            if (proposal.FromDurableIntent
                && proposal.DurableFundingTier == CommitmentTier.Hard
                && proposal.PreferredMoverArmyId.HasValue)
            {
                int incumbentId = proposal.PreferredMoverArmyId.Value;
                RaidAssemblyPlan incumbent = RaidAssemblyPlanner.PlanForArmy(
                    snap, target, defenders, incumbentId);
                if (incumbent.Feasible)
                {
                    ids.Clear();
                    ids.Add(incumbentId);
                    AiDebugLog.Write($"[AI][V2][RaidAdmission] decision=CONTINUE targetArmy={target.TargetArmyId} "
                        + $"actor={incumbentId} win={incumbent.ProjectedWinChance:0.00} "
                        + "reason=durable_hard_incumbent_passed_continuation_gate");
                }
            }

            ByProposal.Remove(proposal);
            ByProposal.Add(proposal, new Entry(ids));
        }

        public static bool TryGet(MissionProposal proposal, out HashSet<int> ids)
        {
            if (proposal != null && ByProposal.TryGetValue(proposal, out Entry entry))
            {
                ids = entry.EligibleArmyIds;
                return true;
            }
            ids = null;
            return false;
        }

        // Exact for a two-mission comparison: at least one distinct actor assignment must exist.
        // The allocator's bounded provision/re-pack remains the final N-way guard; importantly,
        // RaidProvisioner separately classifies a solver failure caused only by earlier actor
        // claims as MoverContended (transient), never AssemblyInfeasible/cooldown.
        public static bool PairHasDistinctAssignment(MissionProposal a, MissionProposal b)
        {
            if (!TryGet(a, out HashSet<int> aa) || !TryGet(b, out HashSet<int> bb))
                return true; // legacy/bare harness: keep the final Provisioning guard authoritative
            return SetsHaveDistinctAssignment(aa, bb);
        }

        internal static bool SetsHaveDistinctAssignment(IEnumerable<int> a, IEnumerable<int> b)
        {
            if (a == null || b == null)
                return true;
            int[] aa = a.Distinct().ToArray();
            int[] bb = b.Distinct().ToArray();
            foreach (int x in aa)
                foreach (int y in bb)
                    if (x != y)
                        return true;
            return false;
        }

        public static string EligibleIds(MissionProposal proposal)
        {
            if (!TryGet(proposal, out HashSet<int> ids))
                return "?";
            return ids.Count == 0 ? "none" : string.Join(",", ids.OrderBy(x => x));
        }

        private static IReadOnlyList<WorthIt.DefenderProfile> DefendersFor(WorldSnapshot snap, int targetArmyId)
        {
            if (snap?.Known == null || targetArmyId == 0)
                return Array.Empty<WorthIt.DefenderProfile>();

            IEnumerable<AiMapMemory.KnownEnemySighting> sightings =
                (snap.Known.EnemySightings ?? Array.Empty<AiMapMemory.KnownEnemySighting>())
                .Concat(snap.Known.NeutralSightings ?? Array.Empty<AiMapMemory.KnownEnemySighting>());
            foreach (AiMapMemory.KnownEnemySighting s in sightings)
                if (s.ArmyId == targetArmyId)
                    return s.Defenders ?? Array.Empty<WorthIt.DefenderProfile>();
            return Array.Empty<WorthIt.DefenderProfile>();
        }
    }
}
