using Game.HexGrid;
using UnityEngine;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  MISSION ADMISSION POLICY  (Strategy V2 build-order step 7.1)
    // ===========================================================================================
    //  The SINGLE owner of the execution-side admission rules that used to be split between
    //  MissionLayer (which trimmed its own output to K and dropped conflicting alternatives) and
    //  an implicit "the allocator funds whatever fits". Step 7.1 separates:
    //
    //    N  — how many sensible alternatives the planner hands downstream
    //         (AiConfigV2.scoutCandidateBeamWidth). MissionLayer's job.
    //    K  — how many operations of a lane may actually EXECUTE per AI turn
    //         (Capacity(lane)). The allocator's job.
    //
    //  THREE QUESTIONS, ONE HOME
    //    LaneFor(mission)       — which execution lane a mission draws a slot from.
    //    Capacity(lane)         — the absolute per-turn slot count for that lane.
    //    Conflicts(a, b)        — pairwise execution conflict (two missions that cannot both run).
    //    AdmissionRank(...)     — planner-local ordering of alternatives WITHIN one lane, with the
    //                             step-7 retarget hysteresis applied (so a half-walked intent is
    //                             not displaced by a marginally better fresh candidate — at the
    //                             beam AND at the allocator's K-cut, one formula, no drift).
    //
    //  Conflicts / Capacity are recomputed against the CURRENT funded+locked portfolio on every
    //  Pack() — they are never a cooldown and never a structural failure. "A good candidate
    //  existed but did not fit this turn's portfolio" is an ordinary outcome.
    //
    //  Raid is deliberately NOT a lane yet: when it lands (step 9) it may not be a fixed K at all
    //  (heroes / armies / commitment / resources bound it instead), so the enum stays minimal.
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
                    return int.MaxValue;
                default:
                    return int.MaxValue;
            }
        }

        // Pairwise execution conflict between two Recon missions:
        //   · same FocusHex                                             -> conflict
        //   · Explore + Explore, distance < scoutTargetMinSeparation    -> conflict
        //   · no distinct physical scout assignment                    -> conflict
        //
        // The physical rule is admission-only. It does not bind an actor; ProvisioningManager
        // remains authoritative. With Recon K=2 this pairwise test is an exact injective-matching
        // test and prevents the allocator from knowingly funding two jobs for one real scout.
        public static bool Conflicts(MissionProposal a, MissionProposal b)
        {
            if (a == null || b == null) return false;

            if (a.Kind == MissionKind.Raid && b.Kind == MissionKind.Raid
                && a.Target is RaidMissionTarget ra && b.Target is RaidMissionTarget rb)
                return ra.TargetArmyId == rb.TargetArmyId;

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
