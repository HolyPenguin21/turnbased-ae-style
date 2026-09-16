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
        Economy,
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
                case MissionKind.Economy: return ExecutionLane.Economy;
                default: return ExecutionLane.None;
            }
        }

        public static int Capacity(ExecutionLane lane)
        {
            switch (lane)
            {
                case ExecutionLane.Recon:
                    // Generic funding cannot know whether Assignment will bind a Scout mission to
                    // a ground actor or to aviation. The hard concurrency limit applies only to
                    // GROUND scouts and is therefore enforced by ReconAssignmentPlanner, where the
                    // executor kind is known. Air keeps its independent aviation actor cap.
                    return int.MaxValue;
                case ExecutionLane.Aggression:
                    // No arbitrary Raid K. Real ready actors, AP/physical resources, target
                    // conflicts and commitments bound Aggression throughput.
                    return int.MaxValue;
                case ExecutionLane.Economy:
                    return int.MaxValue;
                default:
                    return int.MaxValue;
            }
        }

        // Pairwise execution conflicts:
        // Recon:
        //   · same FocusHex
        // Raid:
        //   · same typed Raid target (neutral army id OR guarded-event hex)
        //   · no distinct ready combat-army assignment for the pair
        //
        // Recon deliberately carries NO actor-pair distinctness check here any more — Generic
        // Funding must never know WHO (spec review finding 2). Scout/actor contention is resolved
        // entirely in Provisioning/Assignment (ReconAssignmentPlanner.AssignFunded, one actor <= one
        // job) with ResourceAllocator's existing repack loop reconciling any funded mission that
        // Assignment could not actually staff. Raid keeps its own pairwise actor-distinctness
        // rejection (GroundCombatAdmissionRegistry) — that lane is untouched by this pass.
        public static bool Conflicts(MissionProposal a, MissionProposal b)
        {
            if (a == null || b == null) return false;

            if (a.Kind == MissionKind.Raid && b.Kind == MissionKind.Raid
                && a.Target is RaidMissionTarget ra && b.Target is RaidMissionTarget rb)
            {
                // RaidTargetRef is the canonical identity owner. ArmyId 0 is legitimate and an
                // EventGuard has no army id until Explore spawns it, so collapsing identity back
                // to TargetArmyId would make every guarded event look like target #0 and would also
                // collide with a real neutral Army#0.
                if (ra.Target.HasValue && rb.Target.HasValue && ra.Target.Equals(rb.Target))
                    return true;
                return !GroundCombatAdmissionRegistry.PairHasDistinctAssignment(a, b);
            }

            if (a.Kind == MissionKind.Economy && b.Kind == MissionKind.Economy
                && a.Target is EconomyMissionTarget ea && b.Target is EconomyMissionTarget eb)
                return ea.TargetHex.Equals(eb.TargetHex);

            if (!(a.Target is ScoutMissionTarget ta) || !(b.Target is ScoutMissionTarget tb))
                return false;

            if (ta.FocusHex.Equals(tb.FocusHex))
                return true;

            // Actor-specific spacing belongs to Assignment, alongside the one-actor/one-job and
            // ground-vs-air constraints. At this layer only the objective identity is knowable.
            return false;
        }

        public static float AdmissionRank(MissionProposal m)
        {
            if (m == null) return 0f;
            float score = m.LocalAdmissionScore;
            if (m.Kind == MissionKind.Economy && m.Target is EconomyMissionTarget)
            {
                float sameTurn = m.Requirements != null && m.Requirements.EtaTurns <= 0
                    ? AiConfigV2.economySameTurnCompletionBonus : 0f;
                // TaskScore already priced the card AP/resources and the actor's delivery AP/
                // distance once. EffectiveValue transports that intrinsic value through the shared
                // Radar policy. Subtracting ApDesired + EstimatedDistance again here was an
                // Economy-only second physical-cost scorer and changed cross-lane ordering.
                // Keep only genuine admission policy: same-turn scheduling and urgency above the
                // intrinsic BaseValue; neither is written back into TaskScore.
                score = m.EffectiveValue + sameTurn
                    + Mathf.Max(0f, m.LocalAdmissionScore - m.BaseValue);
            }
            return AdmissionRank(score, m.FromDurableIntent, m.DurableFundingTier);
        }

        public static float AdmissionRank(float localScore, bool fromDurableIntent, CommitmentTier tier)
        {
            if (fromDurableIntent && tier == CommitmentTier.None)
                return localScore * (1f + AiConfigV2.commitmentRetargetMargin);
            return localScore;
        }
    }
}
