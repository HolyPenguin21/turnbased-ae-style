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
        public readonly float MilitaryTargetRelevance;
        public readonly float WinChance;
        public readonly float CardPrice;
        public readonly float Delivery;
        public readonly float MoverOpportunityCost;
        public readonly float HexThreatRisk;
        public readonly float DetectionRisk;
        public readonly float ExistingValueLoss;

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
            float existingValueLoss = 0f)
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
            ExistingValueLoss = existingValueLoss;
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
            - score.CardPrice
            - score.Delivery
            - score.MoverOpportunityCost
            - score.HexThreatRisk
            - score.DetectionRisk
            - score.ExistingValueLoss;

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

        internal static float EconomicHexBenefit(float marginalGain, float resourcePriority)
        {
            float physical = Mathf.Max(0f, marginalGain);
            if (physical <= AiConfigV2.allocatorSliceEpsilon)
                return 0f;

            float physicalContribution = Mathf.Min(
                AiConfigV2.taskScoreEconomicPhysicalBenefitMax,
                physical * AiConfigV2.taskScoreEconomicPhysicalBenefitWeight);
            float marginalGainFactor = Mathf.Clamp01(
                physical / Mathf.Max(AiConfigV2.allocatorSliceEpsilon,
                    AiConfigV2.taskScoreEconomicDeficitFullGain));
            float deficitContribution = Mathf.Clamp01(resourcePriority) * marginalGainFactor
                * AiConfigV2.taskScoreEconomicDeficitBonusMax;
            return physicalContribution + deficitContribution;
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

        internal static float Delivery(float extraApCost, float travelDistance) =>
            Mathf.Max(0f, extraApCost) * AiConfigV2.taskScoreDeliveryApWeight
            + Mathf.Max(0f, travelDistance) * AiConfigV2.taskScoreTravelWeight;

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
            float quality = 1f - Mathf.Clamp01(
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
    }

    internal static class TaskScoreDiagnostics
    {
        internal static void Log(string kind, HexCoord? target, TaskScore score, string rawFacts = null)
        {
            string F(float v) => v.ToString("0.##", CultureInfo.InvariantCulture);
            string targetText = target.HasValue ? $"({target.Value.Q},{target.Value.R})" : "none";
            AiDebugLog.Write($"[AI][V2][TaskScore] kind={kind} target={targetText} "
                + $"economic={F(score.EconomicHexBenefit)} payback={F(score.Payback)} "
                + $"airfield={F(score.Airfield)} global={F(score.GlobalCardEffect)} "
                + $"info={F(score.InfoGain)} stale={F(score.Staleness)} "
                + $"strategic={F(score.StrategicRelevance)} threatDir={F(score.ThreatDirection)} "
                + $"contact={F(score.ContactRelevance)} front={F(score.FrontProgress)} "
                + $"corridor={F(score.CorridorAlignment)} proximity={F(score.OwnTerritoryProximity)} "
                + $"defense={F(score.TerrainDefense)} targetRel={F(score.MilitaryTargetRelevance)} "
                + $"win={F(score.WinChance)} cardPrice={F(score.CardPrice)} "
                + $"delivery={F(score.Delivery)} moverOpp={F(score.MoverOpportunityCost)} "
                + $"hexRisk={F(score.HexThreatRisk)} detection={F(score.DetectionRisk)} "
                + $"existingLoss={F(score.ExistingValueLoss)} final={F(score.Value)}"
                + (string.IsNullOrEmpty(rawFacts) ? string.Empty : $" raw=[{rawFacts}]"));
        }
    }
}
