using Game.HexGrid;
using UnityEngine;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  MISSION ADMISSION POLICY  (Strategy V2 build-order step 7.1)
    // ===========================================================================================
    //  The SINGLE owner of execution-side pairwise admission rules. It never binds an actor;
    //  ProvisioningManager remains authoritative. Physical actor metadata is proposal-side only
    //  and is used here to reject portfolios that are already provably impossible.
    // ===========================================================================================
    public enum ExecutionLane
    {
        None,
        Recon,
        Aggression,
    }

    internal static class MissionAdmissionPolicy
    {
        public static ExecutionLane LaneFor(MissionProposal mission)
        {
            if (mission == null) return ExecutionLane.None;
            switch (mission.Kind)
            {
                case MissionKind.Scout: return ExecutionLane.Recon;
                case MissionKind.Raid: return ExecutionLane.Aggression;
                default: return ExecutionLane.None;
            }
        }

        public static int Capacity(ExecutionLane lane)
        {
            switch (lane)
            {
                case ExecutionLane.Recon:
                    return AiConfigV2.maxConcurrentReconExecutions;
                case ExecutionLane.Aggression:
                    // No arbitrary Raid K. Real ready actors, AP/physical resources, target
                    // conflicts and commitments bound Aggression throughput.
                    return int.MaxValue;
                default:
                    return int.MaxValue;
            }
        }

        // Pairwise execution conflicts:
        // Recon:
        //   · same FocusHex
        //   · Explore+Explore closer than scoutTargetMinSeparation
        //   · no distinct physical scout assignment
        // Raid:
        //   · same target army
        //   · no distinct ready combat-army assignment for the pair
        //
        // These are admission facts, never cooldowns. The Raid pair check catches the common
        // one-ready-army/two-target collision before funding; the provisioning door still owns
        // live N-way contention and classifies it transiently.
        public static bool Conflicts(MissionProposal a, MissionProposal b)
        {
            if (a == null || b == null) return false;

            if (a.Kind == MissionKind.Raid && b.Kind == MissionKind.Raid
                && a.Target is RaidMissionTarget ra && b.Target is RaidMissionTarget rb)
            {
                if (ra.TargetArmyId == rb.TargetArmyId)
                    return true;
                return !RaidAdmissionRegistry.PairHasDistinctAssignment(a, b);
            }

            if (!(a.Target is ScoutMissionTarget ta) || !(b.Target is ScoutMissionTarget tb))
                return false;

            if (ta.FocusHex.Equals(tb.FocusHex))
                return true;

            bool bothExplore = ta.Kind == ScoutTargetKind.Explore && tb.Kind == ScoutTargetKind.Explore;
            if (bothExplore
                && HexGridMath.Distance(ta.FocusHex, tb.FocusHex) < AiConfigV2.scoutTargetMinSeparation)
                return true;

            return !ScoutAdmissionRegistry.PairHasDistinctAssignment(a, b);
        }

        public static float AdmissionRank(MissionProposal m) =>
            m == null ? 0f : AdmissionRank(m.LocalAdmissionScore, m.FromDurableIntent, m.DurableFundingTier);

        public static float AdmissionRank(float localScore, bool fromDurableIntent, CommitmentTier tier)
        {
            if (fromDurableIntent && tier == CommitmentTier.None)
                return localScore * (1f + AiConfigV2.commitmentRetargetMargin);
            return localScore;
        }
    }
}
