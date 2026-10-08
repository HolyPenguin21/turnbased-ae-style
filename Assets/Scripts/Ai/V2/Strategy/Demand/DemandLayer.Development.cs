using System.Collections.Generic;
using System.Linq;
using Game.Map;
using Game.Players;

namespace Game.Ai.V2
{
    // DevelopmentDemands — Laboratory / Factory capability shortages.
    // Research/Production strengthens units already on the map; its facility and operator steps
    // are ordinary card actions that compete in Phase A's single arbitration. WHEN
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
            List<DevelopmentOpportunity> opportunities = DevelopmentOpportunityEvaluator.Enumerate(
                s, player, root, hand, ctx, activeIntents);

            // One prerequisite per pass (the best peer step by PreparationRank: the card score of
            // a card step, a walked hero's net read in card units); the next settled pass sees the
            // completed stage. Phase A then ranks this step against every other card action.
            DevelopmentOpportunity preparation = opportunities.Where(o => o.IsPreparation)
                .OrderByDescending(o => o.PreparationRank).ThenBy(o => (int)o.Mode)
                .ThenBy(o => o.FacilityHex.Q).ThenBy(o => o.FacilityHex.R)
                .ThenBy(o => o.PreparationKind).FirstOrDefault();
            if (preparation != null)
            {
                yield return new AxisDemand
                {
                    RequestingAxis = DesireAxis.Development,
                    Capability = preparation.PreparationKind == DevelopmentPreparationKind.Operator
                        ? CapabilityKind.DevelopmentOperator : CapabilityKind.DevelopmentInfrastructure,
                    DesiredAmount = 1,
                    TargetHex = preparation.FacilityHex,
                    DevelopmentOperatorMode = preparation.Mode,
                    DevOpportunity = preparation,
                    // A card step (facility, hand/generated operator) is a card action: its Value
                    // is its intrinsic card score and it carries NO world TaskScore, so no second
                    // urgency or fixed infrastructure value can be added to it. Only the walk of an
                    // existing hero is a world task, competing on its TaskScore.
                    WorldTaskScore = preparation.PreparationCardScore.HasValue
                        ? default : preparation.WorldTaskScore,
                    Value = preparation.PreparationCardScore ?? preparation.WorldTaskScore.Value,
                    Explain = "prepare " + preparation.Explain,
                };
            }

            int upgrades = 0;
            foreach (DevelopmentOpportunity op in opportunities.Where(o => !o.IsPreparation))
            {
                upgrades++;
                // The upgrade's whole value — stat delta AND known-threat matchup — is priced ONCE
                // by StrategicCardEvaluator.EquipmentUpgradeValue inside the card scorer. A
                // world-task urgency here would count the matchup a second time.
                var devScore = new TaskScore();
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
