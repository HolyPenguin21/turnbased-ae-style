using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Game.Ai.V2
{
    // Ephemeral proposal-side physical feasibility metadata. The allocator still never BINDS an
    // actor — ProvisioningManager remains the single authority for concrete assignment — but the
    // admission policy can now reject a portfolio that provably has no injective scout assignment.
    // ConditionalWeakTable keeps proposal lifetime ownership local and leak-free.
    internal static class ScoutAdmissionRegistry
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
            if (proposal == null || snap == null || !(proposal.Target is ScoutMissionTarget target))
                return;
            List<ArmySnapshot> eligible = ScoutMoverSelector.Eligible(snap, target, null);
            var ids = new List<int>(eligible.Count);
            foreach (ArmySnapshot a in eligible)
                ids.Add(a.ArmyId);
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

        // With the current hard K=2, pairwise injectivity is exact: two proposals are physically
        // co-executable iff at least one distinct (a,b) assignment exists. If metadata is absent
        // (bare legacy harness / non-Recon proposal), admission stays permissive and provisioning
        // remains the final guard.
        public static bool PairHasDistinctAssignment(MissionProposal a, MissionProposal b)
        {
            if (!TryGet(a, out HashSet<int> aa) || !TryGet(b, out HashSet<int> bb))
                return true;
            foreach (int x in aa)
                foreach (int y in bb)
                    if (x != y)
                        return true;
            return false;
        }
    }
}
