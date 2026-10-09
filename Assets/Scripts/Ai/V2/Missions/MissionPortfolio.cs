using System.Collections.Generic;

namespace Game.Ai.V2
{
    // What one portfolio build produced: the valued proposals of this admission and the reasons the
    // planners withheld durable legs.
    internal readonly struct MissionPortfolioResult
    {
        internal readonly List<MissionProposal> Missions;
        internal readonly Dictionary<MissionIntentKey, string> Deferrals;

        internal MissionPortfolioResult(List<MissionProposal> missions,
            Dictionary<MissionIntentKey, string> deferrals)
        {
            Missions = missions;
            Deferrals = deferrals;
        }
    }

    // ===========================================================================================
    //  THE MISSION PORTFOLIO of one settled admission: the four mission planners in their order,
    //  and the valuation of what they proposed. Missions assembles and values; whether a return leg
    //  waits (Continuity), whether a job is parked (Provisioning) and when the first Phase B round
    //  ends (TurnLoop) are decided by their owners and applied by the caller afterwards.
    //
    //  Order, unchanged: refresh the Recon lane pressures from the current snapshot (Missions must
    //  consume current pressures, never trigger Strategy / Desire recomputation) -> Recon,
    //  Aggression, Economy, Development proposals -> AttemptId -> EffectiveValue = BaseValue x
    //  RadarValueScale -> AttackPreparationPriority -> task-score log -> demand correlation.
    //
    //  The Aggression operational facts are NOT refreshed here: the decision frame refreshes them
    //  (StrategyLayer.RefreshAggressionOperationalFacts) in the same step that refreshes the
    //  objectives, before every build. The only production caller always passed "already
    //  refreshed"; the unused re-refresh path was removed with the old orchestrator method.
    // ===========================================================================================
    internal static class MissionPortfolio
    {
        internal static MissionPortfolioResult Build(WorldSnapshot snapshot, DesireBreakdown breakdown,
            IReadOnlyList<MissionIntent> activeIntents, IReadOnlyList<ReconObjective> reconObjectives,
            IReadOnlyList<RaidObjective> aggressionObjectives, Radar radar,
            IReadOnlyList<AxisDemand> demands, V2TraceScope trace, AiTurnContext ctx)
        {
            using var __profile = new Game.Core.ProfileScope("AI/Pipeline.BuildMissionSet");
            // Refresh only the Recon lane pressures from the current snapshot right before Missions
            // consumes them, so a frontier completion earlier this same settled pass is reflected.
            StrategyLayer.RefreshReconLanePressures(snapshot, breakdown);
            var deferredThisPass = new Dictionary<MissionIntentKey, string>();
            List<MissionProposal> missions = ReconMissionPlanner.Propose(snapshot, breakdown,
                activeIntents, reconObjectives, deferredThisPass, ctx);
            missions.AddRange(AggressionMissionLayer.Propose(snapshot, breakdown,
                activeIntents, aggressionObjectives, ctx, deferredThisPass));
            missions.AddRange(EconomyMissionPlanner.Propose(snapshot, breakdown,
                activeIntents, demands, deferredThisPass));
            missions.AddRange(DevelopmentMissionPlanner.Propose(snapshot, activeIntents, demands));

            foreach (MissionProposal m in missions)
                if (m != null && string.IsNullOrEmpty(m.AttemptId))
                    m.AttemptId = trace?.NextMissionAttemptId() ?? "?";
            foreach (MissionProposal m in missions)
                if (m != null)
                    m.EffectiveValue = m.BaseValue * RadarValueScale.For(radar, m);
            AttackPreparationPriority.Apply(missions);
            AiFrameLog.TaskScores(snapshot?.Observer, snapshot?.TurnNumber ?? 0, missions);

            AiV2Trace.CorrelateDemandsToMissions(demands, missions);
            return new MissionPortfolioResult(missions, deferredThisPass);
        }
    }
}
