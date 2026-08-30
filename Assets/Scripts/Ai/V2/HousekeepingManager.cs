using System.Collections;
using Game.Map;
using Game.Players;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  HOUSEKEEPING MANAGER  (Strategy V2 build-order step 8C)
    // ===========================================================================================
    //  The OFF-BUDGET, late-turn structural cleanup pass. It runs as the LAST mutating AI layer of
    //  a V2 turn — after Strategic Manager Phase B and the final operational refresh, before
    //  end-turn state is saved:
    //
    //     StrategicManager Phase B -> RefreshOperationalState -> HousekeepingManager -> end turn
    //
    //  WHAT IT DOES
    //    For each friendly hex holding more than one ground container it builds an immutable
    //    LocalForceGroup, classifies every container (ArmyReorgAnalyzer), asks the pure
    //    ArmyReorganizationPlanner for the structurally-better legal same-hex arrangement, and
    //    applies it through the canonical ArmyActions.TransferMember (HousekeepingExecutor). The
    //    goal is FEWER pointless occupied formations — non-exempt singletons, non-viable weak
    //    armies — while an emptied ArmyData stays a registered, reusable shell.
    //
    //  WHAT IT NEVER DOES  (see the Step 8C design record §5 / §21 for the full list)
    //    · no movement / pathfinding / regroup task           · no card play / generated cards
    //    · no new Objective / Mission / mission ownership      · no Equipment select/attach/detach
    //    · no new ArmyData                                     · no strategic axis entitlement
    //    · never touches aviation / prison / protected armies  · never deletes a normal empty shell
    //
    //  OWNERSHIP
    //    Protection comes from the canonical ActorCommitments projection (rebuilt from the
    //    reconciled intent registry before Phase B) — Housekeeping consumes it, it never keeps its
    //    own reservation registry. Garrison safety uses the canonical AiArmyRoles secure-floor
    //    predicates. Strength uses AiPower (the shared V2 ranking scalar, already Equipment-aware).
    //
    //  COST
    //    Same-hex ArmyActions.TransferMember between not-yet-activated armies is free, so this pass
    //    costs 0 AP in practice and takes nothing from any strategic axis. It never spends
    //    Human/Energy/Materials/Tech. (housekeepingApReserve stays 0 until a genuinely AP-costing
    //    canonical reorg action is ever added here.)
    //
    //  NOTE ON SHARED CLEANUP
    //    The V1 empty-army sweeps (AiTurnController.RunEmptyArmyCleanup / RunGarrisonReorgPhase)
    //    do NOT run on a V2 turn — RunTurn returns right after Pipeline.RunTurn. TransferMember
    //    itself never calls DeleteArmyIfEmptied. So an ArmyData emptied by this pass survives as a
    //    reusable shell with no extra guarding needed.
    // ===========================================================================================
    public sealed class HousekeepingResult
    {
        public bool StateChanged;
        public int GroupsPlanned;
        public int TransfersApplied;
        public int TransfersFailed;
    }

    internal static class HousekeepingManager
    {
        // Coroutine form kept for the pipeline (`yield return`). The body is synchronous — no move
        // orders, no battles — so it simply runs and ends. `result` is the out-channel (same shape
        // as TaskExecutor's results list) the pipeline reads to decide on a final refresh.
        public static IEnumerator RunHousekeeping(WorldSnapshot snapshot, PlayerSetupData player,
            PlayerRoot root, AiTurnContext ctx, ActorCommitments commitments, HousekeepingResult result)
        {
            Run(player, ctx, commitments, result ?? new HousekeepingResult());
            yield break;
        }

        internal static void Run(PlayerSetupData player, AiTurnContext ctx, ActorCommitments commitments,
            HousekeepingResult result)
        {
            if (player == null || ctx == null)
            {
                AiDebugLog.Write("[AI][V2] housekeeping — no player/ctx, nothing to do.");
                return;
            }

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

            AiDebugLog.Write($"[AI][V2] housekeeping — groups {result.GroupsPlanned}, "
                + $"transfers applied {result.TransfersApplied}, failed {result.TransfersFailed}, "
                + $"stateChanged {(result.StateChanged ? 1 : 0)}");
        }
    }
}
