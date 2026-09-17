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
    // DevelopmentDemands and its private helpers.
    // File-split (mechanical, no behaviour change) from DemandLayer.cs — see
    // Docs/ai-v2-file-split-refactor-tasks.md Task 4. Still exactly the DemandLayer
    // class; only this axis's slice moved to its own file.
    public static partial class DemandLayer
    {
        private static IEnumerable<AxisDemand> DevelopmentDemands(WorldSnapshot s, DesireBreakdown b,
            IReadOnlyList<DevelopmentOpportunity> devOpportunities,
            IReadOnlyList<AxisDemand> formedDemands, IReadOnlyList<MissionIntent> activeIntents,
            PlayerSetupData player, AiTurnContext ctx, PlayerRoot root)
        {
            // Radar §E — AxisDemand.Value is the demand's OWN intrinsic merit, never pre-scaled by
            // radar. Radar is applied exactly once, at the point competing spends are compared
            // (MissionProposal.EffectiveValue / the allocator); Demand/urgency thresholds must not
            // shift just because the radar weight moved.
            if (s?.Self == null)
            {
                AiDebugLog.Write("[AI][V2][Demand][Development] decision=NONE reason=no_self_snapshot");
                yield break;
            }

            AiHandData hand = AiHandRegistry.Peek(player);
            bool SupportsNeed(DevelopmentOpportunity op) =>
                HasSupportedDevelopmentAxisDemand(op, formedDemands, activeIntents, player);
            int operatorPrerequisites = 0;
            // Only prepare a mode/site with a concrete supported output, recipient and operator.
            // One best prerequisite per pass; the next settled pass sees the completed stage.
            DevelopmentOpportunity preparation = DevelopmentOpportunityEvaluator.EnumeratePreparation(
                s, player, root, hand, ctx, SupportsNeed).FirstOrDefault();
            if (preparation != null)
            {
                bool facilityReady = s.Development?.Facilities?.Any(f => f.Mode == preparation.Mode
                    && f.Hex.Equals(preparation.FacilityHex)) == true;
                operatorPrerequisites++;
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
                    Explain = preparation.Explain,
                };
            }
            // Filter recipients BEFORE selecting the best one for each offering. Otherwise an
            // unsupported combat upgrade can hide a smaller, useful Recon improvement.
            if (root != null && hand != null)
                devOpportunities = DevelopmentOpportunityEvaluator.Enumerate(
                    s, player, root, hand, null, SupportsNeed);
            // An unstaffed mode must not suppress real opportunities from another ready mode.
            int emitted = 0;
            if (devOpportunities != null)
                foreach (DevelopmentOpportunity op in devOpportunities)
                {
                    if (op == null || op.BaseValue <= 0f) continue;
                    if (!HasSupportedDevelopmentAxisDemand(
                        op, formedDemands, activeIntents, player))
                    {
                        AiDebugLog.Write($"[AI][V2][Demand][Development] decision=REJECT "
                            + $"card={op.Card?.displayName ?? "?"} recipient={op.RecipientLabel ?? "?"} "
                            + "reason=no_supported_axis_demand");
                        continue;
                    }
                    emitted++;
                    yield return new AxisDemand
                    {
                        RequestingAxis = DesireAxis.Development,
                        Capability = CapabilityKind.CardUpgrade,
                        DesiredAmount = 1,
                        RequiredTraits = TraitPreference.None,
                        MinimumFollowupAp = 0f,
                        TargetHex = op.FacilityHex,
                        Value = op.BaseValue,   // radar-blind — see Radar §E note above
                        DevOpportunity = op,
                        Explain = op.Explain,
                    };
                }

            if (emitted > 0)
                AiDebugLog.Write($"[AI][V2][Demand][Development] decision=UPGRADE count={emitted} "
                    + "reason=facility_ready_scored_opportunities");
            else if (operatorPrerequisites == 0)
                AiDebugLog.Write("[AI][V2][Demand][Development] decision=SATISFIED "
                    + "reason=no_supported_profitable_development_use");
        }


        // Production amplifies an already-owned need; it never originates one. In the current
        // scope only a real Recon capability delta or the exact builder of an Economy obligation
        // is a valid witness. Attack/Defence matching remains with WorthIt when those axes return.
        internal static bool HasSupportedDevelopmentAxisDemand(DevelopmentOpportunity op,
            IReadOnlyList<AxisDemand> formedDemands, IReadOnlyList<MissionIntent> activeIntents,
            PlayerSetupData player)
        {
            if (op == null)
                return false;

            bool hasReconDemand = formedDemands?.Any(d => d != null
                && d.RequestingAxis == DesireAxis.Recon
                && d.Capability == CapabilityKind.ScoutCapability) == true;
            if (op.RecipientKind == DevRecipientKind.HandCard)
                return hasReconDemand && ImprovesReconCapability(op);

            if (op.RecipientUnit == null || player == null)
                return false;
            ArmyData army = ArmyRegistry.AllForOwner(player)
                .FirstOrDefault(a => a?.Members != null && a.Members.Contains(op.RecipientUnit));
            if (army == null)
                return false;

            bool economyWitness = formedDemands?.Any(d => d != null
                    && d.RequestingAxis == DesireAxis.Economy
                    && d.EconomyPreferredBuilderArmyId == army.Id) == true
                || activeIntents?.Any(i => i != null && i.Status == IntentStatus.Active
                    && i.Kind == MissionKind.Economy
                    // A builder already walking home (ReturnBuilder) has no outstanding build
                    // obligation left — it cannot justify a fresh Production/CardUpgrade demand.
                    && (i.Economy?.Kind == EconomyTaskKind.BuildExtraction
                        || i.Economy?.Kind == EconomyTaskKind.FoundBase)
                    && (i.PreferredMoverArmyId == army.Id
                        || i.Economy?.BuilderArmyId == army.Id)) == true;
            if (economyWitness)
                return true;

            bool reconWitness = activeIntents?.Any(i => i != null
                && i.Status == IntentStatus.Active && i.Kind == MissionKind.Scout
                && i.PreferredMoverArmyId == army.Id) == true;
            return reconWitness && ImprovesReconCapability(op);
        }

        private static bool ImprovesReconCapability(DevelopmentOpportunity op)
        {
            EquipmentGrant grant = op?.Card?.equipment;
            if (grant == null)
                return false;

            IEnumerable<string> beforeAbilities;
            int beforeMove;
            int beforeActivation;
            if (op.RecipientKind == DevRecipientKind.HandCard)
            {
                CardDefinition host = op.RecipientCard?.Definition;
                if (host == null)
                    return false;
                beforeAbilities = EquipmentSystem.EffectiveAbilities(
                    host.grantedAbilities, op.RecipientCard.Equipment?.equipment);
                beforeMove = host.moveMax;
                beforeActivation = host.activationApCost;
            }
            else
            {
                if (op.RecipientUnit == null)
                    return false;
                beforeAbilities = op.RecipientUnit.Abilities;
                beforeMove = op.RecipientUnit.MoveMax;
                beforeActivation = op.RecipientUnit.ActivationApCost;
            }

            var beforeStats = new Dictionary<EquipmentStat, int>
            {
                [EquipmentStat.MoveMax] = beforeMove,
                [EquipmentStat.ActivationApCost] = beforeActivation,
            };
            // Compare two states normalized by the same gameplay-owned predictor. In
            // particular, a host that already has RapidReaction already has effective activation
            // AP 0 before this grant; the grant must not receive credit for that existing ability.
            PredictedEquipmentState before = EquipmentSystem.Predict(
                null, beforeStats, beforeAbilities);
            PredictedEquipmentState after = EquipmentSystem.Predict(
                grant, beforeStats, beforeAbilities);
            int normalizedBeforeMove = before.Stats.TryGetValue(
                EquipmentStat.MoveMax, out int beforePredictedMove)
                ? beforePredictedMove : beforeMove;
            int normalizedBeforeActivation = before.Stats.TryGetValue(
                EquipmentStat.ActivationApCost, out int beforePredictedActivation)
                ? beforePredictedActivation : beforeActivation;
            int afterMove = after.Stats.TryGetValue(EquipmentStat.MoveMax, out int move)
                ? move : normalizedBeforeMove;
            int afterActivation = after.Stats.TryGetValue(
                EquipmentStat.ActivationApCost, out int activation)
                ? activation : normalizedBeforeActivation;
            return AbilityParams.GetBestRecceRadius(after.Abilities)
                    > AbilityParams.GetBestRecceRadius(before.Abilities)
                || AbilityParams.GetBestRecceSpotStrength(after.Abilities)
                    > AbilityParams.GetBestRecceSpotStrength(before.Abilities)
                || BestStealthLevel(after.Abilities) > BestStealthLevel(before.Abilities)
                || afterMove > normalizedBeforeMove
                || afterActivation < normalizedBeforeActivation;
        }

        private static int BestStealthLevel(IEnumerable<string> abilities)
        {
            int best = 0;
            if (abilities == null)
                return best;
            foreach (string ability in abilities)
                if (AbilityParams.TryGetStealthLevel(ability, out int level))
                    best = System.Math.Max(best, level);
            return best;
        }

    }
}

