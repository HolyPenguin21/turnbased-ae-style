using System.Linq;
using Game.Players;
using UnityEngine;

namespace Game.Ai
{
    // Commitment layer (2026-08-28, project owner's own "committed Raid has no commitment priority"
    // spec) — the seam between the strategic tilt (AiStrategyLayer.Adjust) and the operations boost
    // in AiTurnController.Decide. It exists for one narrow case the other layers structurally can't
    // cover: a RaidWeakerArmy task the AI has ALREADY paid to assemble, that is finished building and
    // that the aggression planner itself scores as allowed to attack, still losing the step turn
    // after turn to routine reconnaissance sitting a few points above it (observed: Recon 109.9 vs
    // ready Raid 104.9, then 109.6 vs 105.7 — Recon first both times).
    //
    // Why not just add the bump inside AiStrategyDirector's raw-aggression axis: that lifts EVERY
    // Aggression candidate (a brand-new raid, BuildBase, AirStrike, RequestRaidArmy, recall, every
    // assembly step) when the sunk cost belongs to ONE existing task. Why not touch
    // AssembleRaidForce's own score: raidAssembleMinBonusFactor deliberately decays the assembly
    // bonus of a far-from-ready raid so a 0-20%-win-chance force can't monopolise the sole raid slot
    // — that anti-starvation mechanic is correct and must survive intact. So the fix is per-task and
    // lives here, keyed strictly on "already committed AND already ready".
    public static class AiCommitmentLayer
    {
        // Returns the candidate's adjusted score (the full value, not a delta — same contract as
        // AiStrategyLayer.Adjust). A no-op for everything that isn't a registered, non-operation,
        // non-retreating, finished-assembling RaidWeakerArmy candidate scored below the tactical
        // exempt line.
        public static float Adjust(PlayerSetupData player, AiDecision candidate)
        {
            if (candidate == null)
                return 0f;

            // The tactical / retreat / emergency ladder (>= strategyExemptScore) is sacred — the
            // same line AiStrategyLayer and the operations boost both refuse to cross.
            if (candidate.Score >= AiConfig.strategyExemptScore)
                return candidate.Score;

            AiTask task = candidate.Task;
            if (task == null)
                return candidate.Score;

            // A hypothetical not-yet-registered task (a "start a new raid" candidate that hasn't
            // won arbitration yet) has no sunk cost to honour.
            if (!AiTaskRegistry.TasksFor(player).Contains(task))
                return candidate.Score;

            // Operation-owned tasks already have their own, stronger commitment mechanism
            // (operationDirectiveBoost, applied right after this).
            if (AiOperationPlanner.IsOperationTask(task))
                return candidate.Score;

            if (task.Kind != AiTaskKind.RaidWeakerArmy)
                return candidate.Score;

            // A raid already walking home is being abandoned on purpose — don't prop it up.
            if (task.Retreating)
                return candidate.Score;

            // Critical boundary: a still-forming raid keeps riding raidAssembleMinBonusFactor's
            // anti-starvation decay. Only a finished force earns the continuation bump.
            if (task.StillAssembling)
                return candidate.Score;

            return Mathf.Min(
                candidate.Score + AiConfig.committedRaidContinuationBonus,
                AiConfig.strategyExemptScore - 1f);
        }
    }
}
