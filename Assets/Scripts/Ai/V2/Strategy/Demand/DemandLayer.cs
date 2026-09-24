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
    // File-split (mechanical, no behaviour change, Docs/ai-v2-file-split-refactor-tasks.md
    // Task 4): this file keeps only Generate, the single assembly point that calls the
    // per-axis methods below — the ONLY place that calls them. Each axis's demand-building
    // logic lives in the sibling DemandLayer.<Axis>.cs partial file.
    public static partial class DemandLayer
    {
        public static List<AxisDemand> Generate(WorldSnapshot snap, DesireBreakdown breakdown,
            IReadOnlyList<ReconObjective> objectives, IReadOnlyList<AggressionObjective> aggressionObjectives,
            IReadOnlyList<MissionIntent> activeIntents,
            ActorCommitments commitments, PlayerSetupData player, AiTurnContext ctx = null,
            PlayerRoot root = null, ISet<DesireAxis> dirtyAxes = null)
        {
            var demands = new List<AxisDemand>();
            bool GenerateAxis(DesireAxis axis) => dirtyAxes == null || dirtyAxes.Contains(axis);
            // §17 — decay the resource-starvation feedback once per turn before it is read.
            if (player != null && snap != null)
                ResourceStarvationRegistry.DecayOncePerTurn(player, snap.TurnNumber);
            // The Research/Production investment window is recorded once per turn from the first
            // (turn-start) snapshot, before any spender reads it.
            DevelopmentInvestmentGate.Observe(player, snap);
            if (GenerateAxis(DesireAxis.Recon))
                demands.AddRange(ReconDemands(snap, objectives, activeIntents, commitments, player, ctx, root));
            if (GenerateAxis(DesireAxis.Aggression))
                demands.AddRange(AggressionDemands(snap, breakdown, aggressionObjectives, activeIntents, commitments, player));
            // There is no Defence axis: ActiveDefence lives inside Aggression.
            if (GenerateAxis(DesireAxis.Economy))
            {
                var timer = System.Diagnostics.Stopwatch.StartNew();
                demands.AddRange(EconomyDemands(snap, breakdown, player, ctx, root,
                    activeIntents, commitments));
                AiDebugLog.WriteDeduped("economy-timing",
                    $"[AI][V2][Timing] EconomyDemands elapsedMs={timer.ElapsedMilliseconds}");
            }
            if (GenerateAxis(DesireAxis.Development))
                demands.AddRange(DevelopmentDemands(snap, activeIntents, player, ctx, root));
            // Correlation: one DemandTraceId per demand for this pass, in deterministic list order
            // (AiV2Trace scope was opened by the orchestrator). Rides on AxisDemand.TraceId /
            // ToString from here — into Phase A and every [CHECK] line raised for the demand.
            V2TraceScope scope = AiV2Trace.CurrentScope(player);
            foreach (AxisDemand d in demands)
                if (d != null && string.IsNullOrEmpty(d.TraceId))
                    d.TraceId = scope?.NextDemandId() ?? "?";
            foreach (AxisDemand d in demands)
                AiDebugLog.WriteDedupedWithId(d.TraceId, $"[AI][V2]   demand — {d} | {d.Explain}");
            return demands;
        }
    }
}

