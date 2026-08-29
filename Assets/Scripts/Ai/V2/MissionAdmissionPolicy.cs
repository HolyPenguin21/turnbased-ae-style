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
        None,   // not execution-capacity managed (no lane hits this today)
        Recon,  // Scout missions — bounded by AiConfigV2.maxConcurrentReconExecutions
    }

    internal static class MissionAdmissionPolicy
    {
        public static ExecutionLane LaneFor(MissionProposal mission) =>
            mission != null && mission.Kind == MissionKind.Scout ? ExecutionLane.Recon : ExecutionLane.None;

        public static int Capacity(ExecutionLane lane)
        {
            switch (lane)
            {
                case ExecutionLane.Recon:
                    return AiConfigV2.maxConcurrentReconExecutions;
                default:
                    return int.MaxValue;
            }
        }

        // Pairwise execution conflict between two Recon missions:
        //   · same FocusHex                                             -> conflict
        //   · Explore + Explore, distance < scoutTargetMinSeparation    -> conflict
        //   · Explore + Surveil / Surveil + Surveil, different FocusHex -> allowed (different jobs)
        // Two Surveils that share a hex but track DIFFERENT armies still conflict on the hex — one
        // vantage observes both; the allocator funds one and the other falls through as a backup.
        // Exact-identity duplicates are removed upstream (MissionLayer dedup), never here.
        public static bool Conflicts(MissionProposal a, MissionProposal b)
        {
            if (a == null || b == null) return false;
            if (!(a.Target is ScoutMissionTarget ta) || !(b.Target is ScoutMissionTarget tb))
                return false;

            if (ta.FocusHex.Equals(tb.FocusHex))
                return true;
            bool bothExplore = ta.Kind == ScoutTargetKind.Explore && tb.Kind == ScoutTargetKind.Explore;
            return bothExplore
                && HexGridMath.Distance(ta.FocusHex, tb.FocusHex) < AiConfigV2.scoutTargetMinSeparation;
        }

        // Planner-local admission rank for one alternative inside its lane. Fresh candidate rides at
        // its LocalAdmissionScore; a re-materialised None-tier intent (an in-flight Explore, or a
        // short Surveil that has not earned Soft yet) rides at LocalAdmissionScore * (1 + margin) so
        // it only yields to a fresh candidate that genuinely beats it. Soft/Hard intents never
        // reach this path — they are pre-bound Commitments funded before the fresh loop.
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
