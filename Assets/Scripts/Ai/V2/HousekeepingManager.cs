using System.Collections;
using Game.Map;
using Game.Players;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  HOUSEKEEPING MANAGER  (Strategy V2 build-order step 8C)
    // ===========================================================================================
    //  Last mutating V2 layer: local same-hex army/garrison structural reorganisation only.
    //  Analyzer -> pure deterministic Planner -> canonical Executor. It never moves across hexes,
    //  creates/deletes ArmyData, touches cards/Equipment, or changes mission ownership.
    //
    //  AP OWNERSHIP: housekeepingApReserve is currently 0. Therefore every planned/executed
    //  transfer/swap must be zero-cost under ArmyActions' real activated-destination rule. The
    //  executor enforces that before mutation; this manager also records a turn-end invariant
    //  violation if AP changed anyway, protecting against future gameplay-rule drift.
    // ===========================================================================================
    public sealed class HousekeepingResult
    {
        public bool StateChanged;
        public int GroupsPlanned;
        public int TransfersApplied;
        public int TransfersFailed;
        public bool ApInvariantViolated;
    }

    internal static class HousekeepingManager
    {
        public static IEnumerator RunHousekeeping(WorldSnapshot snapshot, PlayerSetupData player,
            PlayerRoot root, AiTurnContext ctx, ActorCommitments commitments, HousekeepingResult result)
        {
            Run(player, root, ctx, commitments, result ?? new HousekeepingResult());
            yield break;
        }

        internal static void Run(PlayerSetupData player, PlayerRoot root, AiTurnContext ctx,
            ActorCommitments commitments, HousekeepingResult result)
        {
            if (player == null || ctx == null)
            {
                AiDebugLog.Write("[AI][V2] housekeeping — no player/ctx, nothing to do.");
                return;
            }

            int apBefore = root != null ? root.ActionPoints : 0;
            ArmyReorgAnalysis analysis = ArmyReorgAnalyzer.Analyze(player, commitments);
            if (analysis.Groups.Count == 0)
            {
                AiDebugLog.Write("[AI][V2] housekeeping — no local force group worth reorganising.");
                return;
            }

            foreach (LocalForceGroup group in analysis.Groups)
            {
                ReorganizationPlan plan = ArmyReorganizationPlanner.Plan(group);
                if (plan.IsEmpty)
                {
                    AiDebugLog.Write($"[AI][V2] housekeeping {plan.HexKey} — analysed, no legal improvement.");
                    continue;
                }

                result.GroupsPlanned++;
                AiDebugLog.Write($"[AI][V2] housekeeping plan — {plan.DebugSummary()}");
                HousekeepingExecResult exec = HousekeepingExecutor.Execute(plan, analysis, player, ctx, commitments);
                result.StateChanged |= exec.StateChanged;
                result.TransfersApplied += exec.Applied;
                result.TransfersFailed += exec.Failed;
            }

            if (root != null && root.ActionPoints != apBefore)
            {
                result.ApInvariantViolated = true;
                AiDebugLog.Write($"[AI][V2][ERROR] housekeeping AP invariant violated — AP {apBefore}->{root.ActionPoints}. "
                    + "Step 8C owns no AP while housekeepingApReserve is 0.");
            }

            AiDebugLog.Write($"[AI][V2] housekeeping — groups {result.GroupsPlanned}, "
                + $"operations applied {result.TransfersApplied}, failed {result.TransfersFailed}, "
                + $"stateChanged {(result.StateChanged ? 1 : 0)}, apInvariant {(result.ApInvariantViolated ? "FAIL" : "ok")}");
        }
    }
}
