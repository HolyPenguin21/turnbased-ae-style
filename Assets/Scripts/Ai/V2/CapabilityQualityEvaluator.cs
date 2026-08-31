using System;
using System.Collections.Generic;
using System.Linq;
using Game.Cards;
using Game.HexGrid;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  CAPABILITY QUALITY EVALUATOR  (Strategy V2 — Strategic Manager, Capability Quality Model)
    // ===========================================================================================
    //  The single SEAM between MaterializationCandidateBuilder.ScorePlanA (which owns cost / fit /
    //  chain-shape ranking) and the capability-SPECIFIC "how good is the body this chain produces
    //  for the shortage it is closing" question. One profile is wired — ScoutCapability — and the
    //  switch is the extension point for FieldCombatPower / Hero / Garrison / AirRecon profiles
    //  later, so ScorePlanA never becomes a monolith of per-capability rules.
    //
    //  Returns a bounded MULTIPLIER on the positive base score (1.0 == "no opinion"), plus a
    //  MaterializationQualityBreakdown for logging. Ranks feasible chains only — it never gates.
    //  Projected body stats use EquipmentSystem.Predict, the gameplay-owned pre-attach predictor,
    //  so Direct / Attach / GenerateAttach all evaluate the same END state they will actually spawn.
    // ===========================================================================================
    internal static class CapabilityQualityEvaluator
    {
        private static readonly IReadOnlyList<string> NoAbilities = Array.Empty<string>();

        private readonly struct ProjectedBodyStats
        {
            public readonly int MoveMax;
            public readonly int ActivationApCost;

            public ProjectedBodyStats(int moveMax, int activationApCost)
            {
                MoveMax = Math.Max(1, moveMax);
                ActivationApCost = Math.Max(0, activationApCost);
            }
        }

        public static float QualityMultiplier(MaterializationPlan plan, AxisDemand demand,
            CapabilityInventory inv, int referenceMoveMax, bool hasCompetingHeroDemand,
            out MaterializationQualityBreakdown bd)
        {
            bd = MaterializationQualityBreakdown.Neutral();
            if (plan == null || demand == null)
                return 1f;

            switch (demand.Capability)
            {
                case CapabilityKind.ScoutCapability:
                    return ScoutMultiplier(plan, demand, inv, referenceMoveMax,
                        hasCompetingHeroDemand, out bd);
                default:
                    return 1f;
            }
        }

        // Projected END-state values. These helpers are also used by Phase-A affordability so the
        // AP promised to the later executor matches the body the selected equipment chain creates.
        public static int ProjectedMoveMax(MaterializationPlan plan) => ProjectedStats(plan).MoveMax;
        public static int ProjectedActivationApCost(MaterializationPlan plan) =>
            ProjectedStats(plan).ActivationApCost;

        private static ProjectedBodyStats ProjectedStats(MaterializationPlan plan)
        {
            CardDefinition def = plan?.BaseCardInHand?.Definition ?? plan?.GeneratedBaseDef;
            if (def == null)
                return new ProjectedBodyStats(1, AiConfigV2.scoutNotionalActivationAp);

            int move = def.moveMax;
            int activationAp = def.activationApCost;
            CardDefinition equipment = EffectiveEquipmentDef(plan);
            if (equipment?.equipment != null)
            {
                var before = new Dictionary<EquipmentStat, int>
                {
                    [EquipmentStat.MoveMax] = move,
                    [EquipmentStat.ActivationApCost] = activationAp,
                };
                PredictedEquipmentState predicted = EquipmentSystem.Predict(
                    equipment.equipment, before, def.grantedAbilities);
                if (predicted.Stats.TryGetValue(EquipmentStat.MoveMax, out int projectedMove))
                    move = projectedMove;
                if (predicted.Stats.TryGetValue(EquipmentStat.ActivationApCost, out int projectedAp))
                    activationAp = projectedAp;
            }

            IReadOnlyList<string> finalAbilities = plan?.ProjectedAbilities
                ?? (IReadOnlyList<string>)def.grantedAbilities
                ?? NoAbilities;
            if (finalAbilities.Contains(UnitAbilities.RapidReaction))
                activationAp = 0;

            return new ProjectedBodyStats(move, activationAp);
        }

        private static CardDefinition EffectiveEquipmentDef(MaterializationPlan plan)
        {
            if (plan == null)
                return null;
            if (plan.GeneratedEquipmentDef != null)
                return plan.GeneratedEquipmentDef;
            if (plan.EquipmentInHand?.Definition != null)
                return plan.EquipmentInHand.Definition;
            return plan.BaseCardInHand?.Equipment;
        }

        private static float ScoutMultiplier(MaterializationPlan plan, AxisDemand demand,
            CapabilityInventory inv, int referenceMoveMax, bool hasCompetingHeroDemand,
            out MaterializationQualityBreakdown bd)
        {
            bd = MaterializationQualityBreakdown.Neutral();
            CardDefinition def = plan.BaseCardInHand?.Definition ?? plan.GeneratedBaseDef;
            if (def == null)
                return 1f;

            IReadOnlyList<string> abilities = plan.ProjectedAbilities
                ?? (IReadOnlyList<string>)def.grantedAbilities
                ?? NoAbilities;
            ProjectedBodyStats stats = ProjectedStats(plan);

            var inp = new ScoutCapabilityQuality.Inputs
            {
                MoveMax = stats.MoveMax,
                RecceRadius = AbilityParams.GetBestRecceRadius(abilities),
                SpotStrength = AbilityParams.GetBestRecceSpotStrength(abilities),
                HasStealth = (plan.ExpectedTraits & TraitPreference.Stealth) != 0,
                IsHero = def.cardType == CardType.Hero,
                ActivationApCost = stats.ActivationApCost,
                HasFocus = demand.TargetHex.HasValue,
                DistanceToFocus = demand.TargetHex.HasValue
                    ? HexGridMath.Distance(plan.Deploy.Hex, demand.TargetHex.Value)
                    : 0,
                ReferenceMoveMax = referenceMoveMax > 0 ? referenceMoveMax : stats.MoveMax,
                Context = demand.ScoutContext,
                HasCompetingHeroDemand = hasCompetingHeroDemand,
                AvailableHeroes = inv?.AvailableHeroes ?? 0,
            };
            return ScoutCapabilityQuality.Evaluate(inp, out bd);
        }
    }
}
