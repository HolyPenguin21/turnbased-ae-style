using System.Collections.Generic;
using System.Globalization;
using Game.Economy;
using Game.HexGrid;
using UnityEngine;

namespace Game.Ai.V2
{
    /// <summary>
    /// Canonical intrinsic score for every AI V2 task that interacts with the global map.
    /// A physical/strategic fact belongs to exactly one slot. Unused slots stay at zero.
    /// Lifecycle policy (continuity, urgency, incumbent hysteresis) deliberately does not live here.
    /// </summary>
    public readonly struct TaskScore
    {
        public readonly float EconomicHexBenefit;
        public readonly float Payback;
        public readonly float Airfield;
        public readonly float GlobalCardEffect;
        public readonly float InfoGain;
        public readonly float Staleness;
        public readonly float StrategicRelevance;
        public readonly float ThreatDirection;
        public readonly float ContactRelevance;
        public readonly float FrontProgress;
        public readonly float CorridorAlignment;
        public readonly float OwnTerritoryProximity;
        public readonly float TerrainDefense;
        // Structural Economy fact: this Base project would open a new hexagon of the resource
        // network (a cluster not already reachable from an owned base), independent of whether
        // that cluster's income is currently useful (EconomicHexBenefit prices that separately).
        // Base-only slot; never populated by Extraction/Recon/Raid.
        public readonly float EconomicExpansionValue;
        // Legacy storage name retained for existing score transport and tests. Raid now fills this
        // ONE slot from the fixed reward, not from defender power. Never add both contributions.
        public readonly float MilitaryTargetRelevance;
        public float RaidReward => MilitaryTargetRelevance;
        public readonly float WinChance;
        public readonly float CardPrice;
        public readonly float Delivery;
        public readonly float MoverOpportunityCost;
        public readonly float HexThreatRisk;
        public readonly float DetectionRisk;

        public TaskScore(
            float economicHexBenefit = 0f,
            float payback = 0f,
            float airfield = 0f,
            float globalCardEffect = 0f,
            float infoGain = 0f,
            float staleness = 0f,
            float strategicRelevance = 0f,
            float threatDirection = 0f,
            float contactRelevance = 0f,
            float frontProgress = 0f,
            float corridorAlignment = 0f,
            float ownTerritoryProximity = 0f,
            float terrainDefense = 0f,
            float militaryTargetRelevance = 0f,
            float winChance = 0f,
            float cardPrice = 0f,
            float delivery = 0f,
            float moverOpportunityCost = 0f,
            float hexThreatRisk = 0f,
            float detectionRisk = 0f,
            float economicExpansionValue = 0f)
        {
            EconomicHexBenefit = economicHexBenefit;
            Payback = payback;
            Airfield = airfield;
            GlobalCardEffect = globalCardEffect;
            InfoGain = infoGain;
            Staleness = staleness;
            StrategicRelevance = strategicRelevance;
            ThreatDirection = threatDirection;
            ContactRelevance = contactRelevance;
            FrontProgress = frontProgress;
            CorridorAlignment = corridorAlignment;
            OwnTerritoryProximity = ownTerritoryProximity;
            TerrainDefense = terrainDefense;
            MilitaryTargetRelevance = militaryTargetRelevance;
            WinChance = winChance;
            CardPrice = cardPrice;
            Delivery = delivery;
            MoverOpportunityCost = moverOpportunityCost;
            HexThreatRisk = hexThreatRisk;
            DetectionRisk = detectionRisk;
            EconomicExpansionValue = economicExpansionValue;
        }

        public float Value => TaskScoreEvaluator.Fold(this);
    }

    /// <summary>
    /// One conversion/fold owner for world-task scoring. None of these functions knows task kind,
    /// desire axis, mission kind or objective kind; callers decide which canonical slots apply.
    /// </summary>
    internal static class TaskScoreEvaluator
    {
        internal static float Fold(TaskScore score) =>
              score.EconomicHexBenefit
            + score.Payback
            + score.Airfield
            + score.GlobalCardEffect
            + score.InfoGain
            + score.Staleness
            + score.StrategicRelevance
            + score.ThreatDirection
            + score.ContactRelevance
            + score.FrontProgress
            + score.CorridorAlignment
            + score.OwnTerritoryProximity
            + score.TerrainDefense
            + score.MilitaryTargetRelevance
            + score.WinChance
            + score.EconomicExpansionValue
            - score.CardPrice
            - score.Delivery
            - score.MoverOpportunityCost
            - score.HexThreatRisk
            - score.DetectionRisk;

        // Component-wise change between two canonical world-task projections. Keeping every fact
        // in its original slot matters even when callers only consume Value: diagnostics and
        // future tuning must still be able to say whether a relocation improved InfoGain,
        // StrategicRelevance, risk, etc. `additionalCardPrice` / `additionalDelivery` are the
        // physical price of making the change, not a synthetic benefit slot.
        internal static TaskScore NetChange(TaskScore from, TaskScore to,
            float additionalCardPrice = 0f, float additionalDelivery = 0f) =>
            new TaskScore(
                economicHexBenefit: to.EconomicHexBenefit - from.EconomicHexBenefit,
                payback: to.Payback - from.Payback,
                airfield: to.Airfield - from.Airfield,
                globalCardEffect: to.GlobalCardEffect - from.GlobalCardEffect,
                infoGain: to.InfoGain - from.InfoGain,
                staleness: to.Staleness - from.Staleness,
                strategicRelevance: to.StrategicRelevance - from.StrategicRelevance,
                threatDirection: to.ThreatDirection - from.ThreatDirection,
                contactRelevance: to.ContactRelevance - from.ContactRelevance,
                frontProgress: to.FrontProgress - from.FrontProgress,
                corridorAlignment: to.CorridorAlignment - from.CorridorAlignment,
                ownTerritoryProximity: to.OwnTerritoryProximity - from.OwnTerritoryProximity,
                terrainDefense: to.TerrainDefense - from.TerrainDefense,
                militaryTargetRelevance: to.MilitaryTargetRelevance - from.MilitaryTargetRelevance,
                winChance: to.WinChance - from.WinChance,
                cardPrice: to.CardPrice - from.CardPrice + Mathf.Max(0f, additionalCardPrice),
                delivery: to.Delivery - from.Delivery + Mathf.Max(0f, additionalDelivery),
                moverOpportunityCost: to.MoverOpportunityCost - from.MoverOpportunityCost,
                hexThreatRisk: to.HexThreatRisk - from.HexThreatRisk,
                detectionRisk: to.DetectionRisk - from.DetectionRisk,
                economicExpansionValue: to.EconomicExpansionValue - from.EconomicExpansionValue);

        internal static float ResourcePriority(EconomyResourceStanding standing,
            float externalStarvationPressure = 0f)
        {
            float handShortfall = standing.HandResourceNeed <= AiConfigV2.allocatorSliceEpsilon
                ? 0f
                : Mathf.Clamp01((standing.HandResourceNeed - standing.SpendableStockpile)
                    / standing.HandResourceNeed);
            float operationalShortfall = standing.ReservedOperationalNeed <= AiConfigV2.allocatorSliceEpsilon
                ? 0f
                : Mathf.Clamp01((standing.ReservedOperationalNeed - standing.SpendableStockpile)
                    / standing.ReservedOperationalNeed);
            return Mathf.Max(standing.DeficitScore, standing.StarvationPressure,
                Mathf.Clamp01(externalStarvationPressure), handShortfall, operationalShortfall);
        }

        private static float FoldEconomicBenefit(float totalGain, float weightedDeficit)
        {
            float physical = Mathf.Max(0f, totalGain);
            if (physical <= AiConfigV2.allocatorSliceEpsilon)
                return 0f;
            return Mathf.Min(AiConfigV2.taskScoreEconomicPhysicalBenefitMax,
                    physical * AiConfigV2.taskScoreEconomicPhysicalBenefitWeight)
                + Mathf.Clamp01(weightedDeficit) * AiConfigV2.taskScoreEconomicDeficitBonusMax;
        }

        internal static float EconomicHexBenefit(float marginalGain, float resourcePriority) =>
            FoldEconomicBenefit(marginalGain,
                Mathf.Clamp01(resourcePriority) * Mathf.Clamp01(
                    Mathf.Max(0f, marginalGain) / Mathf.Max(AiConfigV2.allocatorSliceEpsilon,
                        AiConfigV2.taskScoreEconomicDeficitFullGain)));

        // Multi-resource shortage belongs to EACH actually produced type. All resource gains
        // aggregate before the one physical cap and the one shortage cap in FoldEconomicBenefit.
        internal static float EconomicHexBenefit(IReadOnlyList<(float Gain, float Priority)> perResource)
        {
            if (perResource == null)
                return 0f;
            float totalGain = 0f;
            float weightedDeficit = 0f;
            foreach (var resource in perResource)
            {
                float gain = Mathf.Max(0f, resource.Gain);
                if (gain <= AiConfigV2.allocatorSliceEpsilon)
                    continue;
                totalGain += gain;
                weightedDeficit += Mathf.Clamp01(resource.Priority) * Mathf.Clamp01(
                    gain / Mathf.Max(AiConfigV2.allocatorSliceEpsilon,
                        AiConfigV2.taskScoreEconomicDeficitFullGain));
            }
            return FoldEconomicBenefit(totalGain, weightedDeficit);
        }

        internal static float Payback(float paybackTurns)
        {
            if (paybackTurns < 0f || float.IsNaN(paybackTurns) || float.IsInfinity(paybackTurns))
                return 0f;
            float quality = 1f - Mathf.Clamp01(
                paybackTurns / Mathf.Max(1f, AiConfigV2.taskScorePaybackHorizonTurns));
            return quality * AiConfigV2.taskScorePaybackMax;
        }

        internal static float CardPrice(float apCost, float resourceCost) =>
            Mathf.Max(0f, apCost) * AiConfigV2.taskScoreCardPriceApWeight
            + Mathf.Max(0f, resourceCost) * AiConfigV2.taskScoreCardPriceResourceWeight;

        // Raid/Recon already derive a real ETA (hexes -> mover's MaxMovement -> turns) for their
        // own cost models. The game's own rule (AiTurnController.MoveArmyRoutine) is: MP moves
        // an army freely within a turn, but re-activating it on each NEW turn of a multi-turn
        // march pays its ActivationApCost again (gated on !HasActivatedThisTurn, which resets
        // every turn) — so the honest delivery cost is that SAME real per-turn activation fee,
        // repeated once per turn beyond the first (already priced by cardPrice), not a second
        // invented distance-weight constant. apWeight is passed in so this charges at whichever
        // rate the caller already prices that same fee at via cardPrice (e.g. Raid's own reduced
        // activation weight), never a different one for the same physical AP.
        internal static float DeliveryFromEta(float perTurnApCost, float etaTurns, float apWeight) =>
            Mathf.Max(0f, perTurnApCost) * Mathf.Max(0f, etaTurns - 1f) * apWeight;

        internal static int NearestOwnedHomeDistance(WorldSnapshot snap, HexCoord target,
            int fallbackDistance = 0)
        {
            int best = int.MaxValue;
            if (snap?.Self?.BaseHexes != null)
                foreach (HexCoord home in snap.Self.BaseHexes)
                    best = Mathf.Min(best, HexGridMath.Distance(home, target));
            if (snap?.Self != null)
                best = Mathf.Min(best, HexGridMath.Distance(snap.Self.Citadel, target));
            return best == int.MaxValue ? Mathf.Max(0, fallbackDistance) : best;
        }

        internal static float OwnTerritoryProximity(float nearestHomeDistance)
        {
            if (nearestHomeDistance < 0f || float.IsNaN(nearestHomeDistance)
                || float.IsInfinity(nearestHomeDistance))
                return 0f;
            // Proximity is a signed positional advantage, not another delivery/AP charge.
            // Recenter the established 6-point spread: close +3, midpoint 0, distant -3.
            // Preserve the original slope so travel already priced by Delivery is not doubled.
            float quality = 0.5f - Mathf.Clamp01(
                nearestHomeDistance / Mathf.Max(1f, AiConfigV2.taskScoreProximityFullFalloffDistance));
            return quality * AiConfigV2.taskScoreProximityMax;
        }

        internal static float HexThreatRisk(float normalizedRisk) =>
            Mathf.Clamp01(normalizedRisk) * AiConfigV2.taskScoreThreatRiskMax;

        internal static float DetectionRisk(float normalizedRisk) =>
            Mathf.Clamp01(normalizedRisk) * AiConfigV2.taskScoreDetectionRiskMax;

        internal static float InfoGain(float normalizedGain) =>
            Mathf.Clamp01(normalizedGain) * AiConfigV2.taskScoreInfoGainMax;

        internal static float PositiveStaleness(float normalizedStaleness) =>
            Mathf.Clamp01(normalizedStaleness) * AiConfigV2.taskScoreStalenessMax;

        internal static float StaleIntelPenalty(float normalizedStaleness) =>
            -Mathf.Clamp01(normalizedStaleness) * AiConfigV2.taskScoreStalenessMax;

        internal static float StrategicRelevance(float normalizedValue) =>
            Mathf.Clamp01(normalizedValue) * AiConfigV2.taskScoreStrategicRelevanceMax;

        internal static float ThreatDirection(float normalizedValue) =>
            Mathf.Clamp01(normalizedValue) * AiConfigV2.taskScoreThreatDirectionMax;

        internal static float ContactRelevance(float normalizedValue) =>
            Mathf.Clamp01(normalizedValue) * AiConfigV2.taskScoreContactRelevanceMax;

        internal static float FrontProgress(float normalizedValue) =>
            Mathf.Clamp01(normalizedValue) * AiConfigV2.taskScoreFrontProgressMax;

        internal static float CorridorAlignment(float normalizedValue) =>
            Mathf.Clamp01(normalizedValue) * AiConfigV2.taskScoreCorridorAlignmentMax;

        internal static float TerrainDefense(float normalizedValue) =>
            Mathf.Clamp01(normalizedValue) * AiConfigV2.taskScoreTerrainDefenseMax;

        internal static float Airfield(float normalizedValue) =>
            Mathf.Clamp01(normalizedValue) * AiConfigV2.taskScoreAirfieldMax;

        internal static float GlobalCardEffect(float normalizedValue) =>
            Mathf.Clamp01(normalizedValue) * AiConfigV2.taskScoreGlobalCardEffectMax;

        internal static float MilitaryTargetRelevance(float normalizedValue) =>
            Mathf.Clamp01(normalizedValue) * AiConfigV2.taskScoreMilitaryTargetMax;

        internal static float WinChance(float probability) =>
            Mathf.Clamp01(probability) * AiConfigV2.taskScoreWinChanceMax;

        internal static float EconomicExpansionValue(float normalizedValue) =>
            Mathf.Clamp01(normalizedValue) * AiConfigV2.taskScoreEconomicExpansionMax;
    }

}
