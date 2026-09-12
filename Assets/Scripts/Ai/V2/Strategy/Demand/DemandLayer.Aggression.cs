using System.Collections.Generic;
using System.Linq;
using Game.Cards;
using Game.Combat;
using Game.Economy;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using UnityEngine;

namespace Game.Ai.V2
{
    // AggressionDemands (thin wrapper over AggressionDemandEvaluator).
    // File-split (mechanical, no behaviour change) from DemandLayer.cs — see
    // Docs/ai-v2-file-split-refactor-tasks.md Task 4. Still exactly the DemandLayer
    // class; only this axis's slice moved to its own file.
    public static partial class DemandLayer
    {
        private static IEnumerable<AxisDemand> AggressionDemands(WorldSnapshot snap, DesireBreakdown b,
            IReadOnlyList<AggressionObjective> objectives, IReadOnlyList<MissionIntent> activeIntents,
            ActorCommitments commitments, PlayerSetupData player)
        {
            AggressionDemandEvaluation eval = AggressionDemandEvaluator.Build(
                snap, objectives, activeIntents, commitments, player);
            foreach (string line in eval.Diagnostics)
                AiDebugLog.Write(line);
            foreach (AxisDemand d in eval.Demands)
                yield return d;
        }
    }
}
