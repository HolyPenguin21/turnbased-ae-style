using System;
using System.Collections.Generic;
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
    //  Returns a bounded MULTIPLIER on the base score (1.0 == "no opinion"), plus a
    //  MaterializationQualityBreakdown for logging. Ranks feasible chains only — it never gates.
    // ===========================================================================================
    internal static class CapabilityQualityEvaluator
    {
        private static readonly IReadOnlyList<string> NoAbilities = Array.Empty<string>();

        public static float QualityMultiplier(MaterializationPlan plan, AxisDemand demand,
            CapabilityInventory inv, int referenceMoveMax, out MaterializationQualityBreakdown bd)
        {
            bd = MaterializationQualityBreakdown.Neutral();
            if (plan == null || demand == null)
                return 1f;

            switch (demand.Capability)
            {
                case CapabilityKind.ScoutCapability:
                    return ScoutMultiplier(plan, demand, inv, referenceMoveMax, out bd);
                default:
                    return 1f;
            }
        }

        // The projected moveMax of a chain's END deployable — used both here and by ScorePlanA to
        // derive the feasible set's cheapest-mobility reference for the MARGINAL compare.
        public static int ProjectedMoveMax(MaterializationPlan plan)
        {
            CardDefinition def = plan?.BaseCardInHand?.Definition ?? plan?.GeneratedBaseDef;
            return def != null ? Math.Max(1, def.moveMax) : 1;
        }

        private static float ScoutMultiplier(MaterializationPlan plan, AxisDemand demand,
            CapabilityInventory inv, int referenceMoveMax, out MaterializationQualityBreakdown bd)
        {
            bd = MaterializationQualityBreakdown.Neutral();
            CardDefinition def = plan.BaseCardInHand?.Definition ?? plan.GeneratedBaseDef;
            if (def == null)
                return 1f;

            IReadOnlyList<string> abilities = plan.ProjectedAbilities
                ?? (IReadOnlyList<string>)def.grantedAbilities
                ?? NoAbilities;

            var inp = new ScoutCapabilityQuality.Inputs
            {
                MoveMax = def.moveMax,
                RecceRadius = AbilityParams.GetBestRecceRadius(abilities),
                SpotStrength = AbilityParams.GetBestRecceSpotStrength(abilities),
                HasStealth = (plan.ExpectedTraits & TraitPreference.Stealth) != 0,
                IsHero = def.cardType == CardType.Hero,
                ActivationApCost = def.activationApCost,
                HasFocus = demand.TargetHex.HasValue,
                DistanceToFocus = demand.TargetHex.HasValue
                    ? HexGridMath.Distance(plan.Deploy.Hex, demand.TargetHex.Value)
                    : 0,
                ReferenceMoveMax = referenceMoveMax > 0 ? referenceMoveMax : Math.Max(1, def.moveMax),
                Context = demand.ScoutContext,
                AvailableHeroes = inv?.AvailableHeroes ?? 0,
                CommittedHeroes = inv?.CommittedHeroes ?? 0,
            };
            return ScoutCapabilityQuality.Evaluate(inp, out bd);
        }
    }
}
