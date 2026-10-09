using System;
using System.Collections;
using Game.HexGrid;
using Game.Map;
using Game.Players;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  Adapter from a selected mandatory aviation obligation to its existing executor. It owns no
    //  policy and no state: planning and physics stay in AviationRebasePlanner / ReconAirExecutor,
    //  and the shared post-step protocol stays in the pipeline. `actionChanged` reports whether the
    //  executor changed the world (rebase: the wing moved; recovery: the actor step mutated).
    // ===========================================================================================
    internal static class MandatoryAviationStep
    {
        internal static IEnumerator Execute(MandatoryAviationKind kind, ArmyData actor,
            PlayerSetupData player, PlayerRoot root, AiTurnContext ctx, WorldSnapshot snapshot,
            Action<bool> actionChanged)
        {
            if (kind == MandatoryAviationKind.Rebase)
            {
                // Route safety and destination validity are live-rechecked inside the executor.
                yield return AviationRebasePlanner.ExecuteContinuation(
                    player, root, ctx, actor, actionChanged);
            }
            else if (kind == MandatoryAviationKind.Recovery)
            {
                HexCoord? focus =
                    ReconPatrolStateRegistry.TryGet(player, actor.Id, out ReconPatrolState state)
                        ? state.StrategicAnchor : (HexCoord?)null;
                var result = new AirReconExecutionResult();
                yield return ReconAirExecutor.RunActorStep(player, root, ctx, snapshot, actor,
                    result, root.ActionPoints, focus, perMissionResult: null,
                    control: new ReconAirExecutor.ActorStepControl());
                actionChanged?.Invoke(result.Mutated);
            }
        }
    }
}
