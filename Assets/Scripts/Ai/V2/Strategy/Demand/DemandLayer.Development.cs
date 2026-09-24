using System.Collections.Generic;
using System.Linq;
using Game.Map;
using Game.Players;

namespace Game.Ai.V2
{
    // DevelopmentDemands — Laboratory / Factory capability shortages.
    // Research/Production is a late resource sink that strengthens units already on the map. WHEN
    // it may spend is DevelopmentInvestmentGate; WHAT is worth doing is
    // DevelopmentOpportunityEvaluator.Enumerate (the one admission). This file only turns that
    // admitted list into AxisDemands — it never re-admits with a predicate of its own.
    public static partial class DemandLayer
    {
        private static IEnumerable<AxisDemand> DevelopmentDemands(WorldSnapshot s,
            IReadOnlyList<MissionIntent> activeIntents, PlayerSetupData player, AiTurnContext ctx,
            PlayerRoot root)
        {
            // Radar §E — AxisDemand.Value is the demand's OWN intrinsic merit, never pre-scaled by
            // radar. Radar is applied exactly once, where competing spends are compared.
            if (s?.Self == null)
            {
                AiDebugLog.Write("[AI][V2][Demand][Development] decision=NONE reason=no_self_snapshot");
                yield break;
            }

            AiHandData hand = AiHandRegistry.Peek(player);
            if (!DevelopmentInvestmentGate.IsOpen(player, s.TurnNumber))
            {
                AiDebugLog.WriteDeduped("decision", "[AI][V2][Demand][Development] decision=HOLD "
                    + "reason=investment_window_closed");
                yield break;
            }
            List<DevelopmentOpportunity> opportunities = DevelopmentOpportunityEvaluator.Enumerate(
                s, player, root, hand, ctx, activeIntents);

            // One prerequisite per pass; the next settled pass sees the completed stage.
            DevelopmentOpportunity preparation = opportunities.FirstOrDefault(o => o.IsPreparation);
            if (preparation != null)
            {
                bool facilityReady = s.Development?.Facilities?.Any(f => f.Mode == preparation.Mode
                    && f.Hex.Equals(preparation.FacilityHex)) == true;
                yield return new AxisDemand
                {
                    RequestingAxis = DesireAxis.Development,
                    Capability = facilityReady ? CapabilityKind.DevelopmentOperator
                        : CapabilityKind.DevelopmentInfrastructure,
                    DesiredAmount = 1,
                    TargetHex = preparation.FacilityHex,
                    DevelopmentOperatorMode = preparation.Mode,
                    DevOpportunity = preparation,
                    Value = preparation.BaseValue,
                    Explain = "prepare " + preparation.Explain,
                };
            }

            int upgrades = 0;
            foreach (DevelopmentOpportunity op in opportunities.Where(o => !o.IsPreparation))
            {
                upgrades++;
                // The upgrade's world relevance is the share of known threats it improves the
                // outcome against; its own stat gain is priced by the card scorer
                // (StrategicCardEvaluator.ScoreGeneratedEquipmentUpgrade), never here.
                var devScore = new TaskScore(
                    upgradeMatchupValue: TaskScoreEvaluator.UpgradeMatchupValue(op.MatchupFit));
                yield return new AxisDemand
                {
                    RequestingAxis = DesireAxis.Development,
                    Capability = CapabilityKind.CardUpgrade,
                    DesiredAmount = 1,
                    RequiredTraits = TraitPreference.None,
                    MinimumFollowupAp = 0f,
                    TargetHex = op.FacilityHex,
                    WorldTaskScore = devScore,
                    Value = devScore.Value,
                    DevOpportunity = op,
                    Explain = op.Explain,
                };
            }

            AiDebugLog.WriteDeduped("decision", upgrades > 0 || preparation != null
                ? $"[AI][V2][Demand][Development] decision={(preparation != null ? "PREPARE" : "UPGRADE")} "
                    + $"upgrades={upgrades} reason=admitted_opportunities"
                : "[AI][V2][Demand][Development] decision=SATISFIED reason=no_profitable_development_use");
        }
    }
}
