using System.Collections.Generic;

namespace Game.Ai.V2
{
    // Ephemeral target-side physical feasibility metadata. The allocator still never BINDS an
    // actor — ProvisioningManager remains the single authority for concrete assignment — but the
    // admission policy can reject a Recon portfolio that provably has no injective scout assignment.
    // ScoutCostModel refreshes an entry every time it sizes a proposal against the current snapshot.
    internal static class ScoutAdmissionRegistry
    {
        private static readonly Dictionary<MissionIntentKey, HashSet<int>> EligibleByTarget =
            new Dictionary<MissionIntentKey, HashSet<int>>();

        public static void Record(WorldSnapshot snap, ScoutMissionTarget target)
        {
            if (snap == null)
                return;
            List<ArmySnapshot> eligible = ScoutMoverSelector.Eligible(snap, target, null);
            var ids = new HashSet<int>();
            foreach (ArmySnapshot a in eligible)
                ids.Add(a.ArmyId);
            EligibleByTarget[MissionIntentKey.ForScoutTarget(target)] = ids;
        }

        private static bool TryGet(MissionProposal proposal, out HashSet<int> ids)
        {
            if (proposal != null && proposal.Target is ScoutMissionTarget target
                && EligibleByTarget.TryGetValue(MissionIntentKey.ForScoutTarget(target), out ids))
                return true;
            ids = null;
            return false;
        }

        // With the current hard K=2, pairwise injectivity is exact: two proposals are physically
        // co-executable iff at least one distinct (a,b) assignment exists. If metadata is absent
        // (bare legacy harness), admission stays permissive and provisioning remains the final guard.
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
