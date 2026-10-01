using System.Collections.Generic;
using System.Globalization;
using Game.Economy;
using Game.HexGrid;
using UnityEngine;

namespace Game.Ai.V2
{
    /// <summary>
    /// Every TaskScore slot, in fold order. The slot table (<see cref="TaskScoreEvaluator.Sign"/>,
    /// <see cref="TaskScoreEvaluator.GroupOf"/>) is the one place that says how a slot folds and
    /// who fills it; Fold, NetChange and every composition iterate it instead of listing fields.
    /// </summary>
    public enum TaskSlot
    {
        EconomicHexBenefit,
        Payback,
        Airfield,
        GlobalCardEffect,
        InfoGain,
        Staleness,
        StrategicRelevance,
        ThreatDirection,
        ContactRelevance,
        FrontProgress,
        CorridorAlignment,
        OwnTerritoryProximity,
        TerrainDefense,
        RaidReward,
        EventReward,
        AttackReadiness,
        PreventedDamage,
        WinChance,
        EconomicExpansionValue,
        ForceAmplification,
        CardPrice,
        Delivery,
        MoverOpportunityCost,
        HexThreatRisk,
        CitadelThreatRisk,
        BaseThreatRisk,
        DetectionRisk,
        IntelAgePenalty,
    }

    /// <summary>
    /// Intrinsic slots are facts of the task's target, built once by the objective/site owner.
    /// Execution slots are facts of the concrete actor/chain doing it (fight odds, AP spent now,
    /// recurring AP on the way, value taken from the actor's current task). A composition never
    /// copies slots by hand: it keeps one side and replaces the other.
    /// </summary>
    public enum TaskSlotGroup { Intrinsic, Execution }

    /// <summary>
    /// What a slot means to the fold: Value = Benefit - Cost - Risk - Opportunity. Every task
    /// family reads in these four words; the slots inside Benefit are that family's terms.
    /// </summary>
    public enum TaskSlotCategory { Benefit, Cost, Risk, Opportunity }

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
        // Value of refreshing old intel (Recon). Its opposite — acting on old intel — is
        // IntelAgePenalty, a separate price slot.
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
        // One military fact per task family — never summed into a shared slot:
        //   RaidReward      — the fixed expected resource/card reward of completing a Raid;
        //   EventReward     — a Hex Event guard Raid's own event reward, by guard tier;
        //   AttackReadiness — Attack's stronghold readiness (assembly x deployment), Base/Citadel only;
        //   PreventedDamage — the damage an ActiveDefence intercept keeps off the threatened asset.
        public readonly float RaidReward;
        public readonly float EventReward;
        public readonly float AttackReadiness;
        public readonly float PreventedDamage;
        // Development output: the need-justified force a Research/Production output adds
        // (Production amplifies an Attack/Defence need; it never creates one).
        public readonly float ForceAmplification;
        public readonly float WinChance;
        public readonly float CardPrice;
        public readonly float Delivery;
        public readonly float MoverOpportunityCost;
        // Threat at / near the task's own hex.
        public readonly float HexThreatRisk;
        // Threat against the starting Citadel / against any other own Base (Analysis
        // ThreatModel.CitadelThreatSeverity / BaseThreatSeverity), charged to tasks that should
        // wait while home is threatened. Kept apart from HexThreatRisk: home is not the task hex.
        public readonly float CitadelThreatRisk;
        public readonly float BaseThreatRisk;
        public readonly float DetectionRisk;
        // Acting on an aged sighting of a mobile target (Attack, ActiveDefence).
        public readonly float IntelAgePenalty;

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
            float raidReward = 0f,
            float eventReward = 0f,
            float attackReadiness = 0f,
            float preventedDamage = 0f,
            float winChance = 0f,
            float cardPrice = 0f,
            float delivery = 0f,
            float moverOpportunityCost = 0f,
            float hexThreatRisk = 0f,
            float citadelThreatRisk = 0f,
            float baseThreatRisk = 0f,
            float detectionRisk = 0f,
            float economicExpansionValue = 0f,
            float forceAmplification = 0f,
            float intelAgePenalty = 0f)
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
            RaidReward = raidReward;
            EventReward = eventReward;
            AttackReadiness = attackReadiness;
            PreventedDamage = preventedDamage;
            WinChance = winChance;
            CardPrice = cardPrice;
            Delivery = delivery;
            MoverOpportunityCost = moverOpportunityCost;
            HexThreatRisk = hexThreatRisk;
            CitadelThreatRisk = citadelThreatRisk;
            BaseThreatRisk = baseThreatRisk;
            DetectionRisk = detectionRisk;
            EconomicExpansionValue = economicExpansionValue;
            ForceAmplification = forceAmplification;
            IntelAgePenalty = intelAgePenalty;
        }

        public float Value => TaskScoreEvaluator.Fold(this);

        public float this[TaskSlot slot]
        {
            get
            {
                switch (slot)
                {
                    case TaskSlot.EconomicHexBenefit: return EconomicHexBenefit;
                    case TaskSlot.Payback: return Payback;
                    case TaskSlot.Airfield: return Airfield;
                    case TaskSlot.GlobalCardEffect: return GlobalCardEffect;
                    case TaskSlot.InfoGain: return InfoGain;
                    case TaskSlot.Staleness: return Staleness;
                    case TaskSlot.StrategicRelevance: return StrategicRelevance;
                    case TaskSlot.ThreatDirection: return ThreatDirection;
                    case TaskSlot.ContactRelevance: return ContactRelevance;
                    case TaskSlot.FrontProgress: return FrontProgress;
                    case TaskSlot.CorridorAlignment: return CorridorAlignment;
                    case TaskSlot.OwnTerritoryProximity: return OwnTerritoryProximity;
                    case TaskSlot.TerrainDefense: return TerrainDefense;
                    case TaskSlot.RaidReward: return RaidReward;
                    case TaskSlot.EventReward: return EventReward;
                    case TaskSlot.AttackReadiness: return AttackReadiness;
                    case TaskSlot.PreventedDamage: return PreventedDamage;
                    case TaskSlot.ForceAmplification: return ForceAmplification;
                    case TaskSlot.IntelAgePenalty: return IntelAgePenalty;
                    case TaskSlot.WinChance: return WinChance;
                    case TaskSlot.EconomicExpansionValue: return EconomicExpansionValue;
                    case TaskSlot.CardPrice: return CardPrice;
                    case TaskSlot.Delivery: return Delivery;
                    case TaskSlot.MoverOpportunityCost: return MoverOpportunityCost;
                    case TaskSlot.HexThreatRisk: return HexThreatRisk;
                    case TaskSlot.CitadelThreatRisk: return CitadelThreatRisk;
                    case TaskSlot.BaseThreatRisk: return BaseThreatRisk;
                    case TaskSlot.DetectionRisk: return DetectionRisk;
                    default: throw new System.ArgumentOutOfRangeException(nameof(slot), slot, null);
                }
            }
        }

        // The one slot-wise constructor. Compositions (NetChange, WithExecution) go through here,
        // so a slot added to the enum and this switch can never be silently dropped by a copy.
        internal static TaskScore FromSlots(System.Func<TaskSlot, float> value) =>
            new TaskScore(
                economicHexBenefit: value(TaskSlot.EconomicHexBenefit),
                payback: value(TaskSlot.Payback),
                airfield: value(TaskSlot.Airfield),
                globalCardEffect: value(TaskSlot.GlobalCardEffect),
                infoGain: value(TaskSlot.InfoGain),
                staleness: value(TaskSlot.Staleness),
                strategicRelevance: value(TaskSlot.StrategicRelevance),
                threatDirection: value(TaskSlot.ThreatDirection),
                contactRelevance: value(TaskSlot.ContactRelevance),
                frontProgress: value(TaskSlot.FrontProgress),
                corridorAlignment: value(TaskSlot.CorridorAlignment),
                ownTerritoryProximity: value(TaskSlot.OwnTerritoryProximity),
                terrainDefense: value(TaskSlot.TerrainDefense),
                raidReward: value(TaskSlot.RaidReward),
                eventReward: value(TaskSlot.EventReward),
                attackReadiness: value(TaskSlot.AttackReadiness),
                preventedDamage: value(TaskSlot.PreventedDamage),
                winChance: value(TaskSlot.WinChance),
                cardPrice: value(TaskSlot.CardPrice),
                delivery: value(TaskSlot.Delivery),
                moverOpportunityCost: value(TaskSlot.MoverOpportunityCost),
                hexThreatRisk: value(TaskSlot.HexThreatRisk),
                citadelThreatRisk: value(TaskSlot.CitadelThreatRisk),
                baseThreatRisk: value(TaskSlot.BaseThreatRisk),
                detectionRisk: value(TaskSlot.DetectionRisk),
                economicExpansionValue: value(TaskSlot.EconomicExpansionValue),
                forceAmplification: value(TaskSlot.ForceAmplification),
                intelAgePenalty: value(TaskSlot.IntelAgePenalty));
    }

    /// <summary>
    /// One conversion/fold owner for world-task scoring. None of these functions knows task kind,
    /// desire axis, mission kind or objective kind; callers decide which canonical slots apply.
    /// </summary>
    internal static class TaskScoreEvaluator
    {
        internal static readonly TaskSlot[] AllSlots =
            (TaskSlot[])System.Enum.GetValues(typeof(TaskSlot));

        internal static TaskSlotCategory CategoryOf(TaskSlot slot)
        {
            switch (slot)
            {
                case TaskSlot.CardPrice:
                case TaskSlot.Delivery:
                    return TaskSlotCategory.Cost;
                case TaskSlot.HexThreatRisk:
                case TaskSlot.CitadelThreatRisk:
                case TaskSlot.BaseThreatRisk:
                case TaskSlot.DetectionRisk:
                case TaskSlot.IntelAgePenalty:
                    return TaskSlotCategory.Risk;
                case TaskSlot.MoverOpportunityCost:
                    return TaskSlotCategory.Opportunity;
                default:
                    return TaskSlotCategory.Benefit;
            }
        }

        // Benefit adds; Cost, Risk and Opportunity are stored non-negative and subtract. A signed
        // benefit (OwnTerritoryProximity) stays a Benefit.
        internal static float Sign(TaskSlot slot) =>
            CategoryOf(slot) == TaskSlotCategory.Benefit ? 1f : -1f;

        internal static float CategoryTotal(TaskScore score, TaskSlotCategory category)
        {
            float total = 0f;
            foreach (TaskSlot slot in AllSlots)
                if (CategoryOf(slot) == category)
                    total += score[slot];
            return total;
        }

        // The one human-readable decomposition every task log uses:
        //   value=13.1 | benefit 19.1 (RaidReward 8.0, WinChance 9.6, OwnTerritoryProximity 1.5)
        //   | cost 6.0 (CardPrice 3.0, Delivery 3.0) | risk 0.0 | opportunity 0.0
        internal static string Describe(TaskScore score)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("value=").Append(F(score.Value));
            foreach (TaskSlotCategory category in (TaskSlotCategory[])System.Enum.GetValues(typeof(TaskSlotCategory)))
            {
                sb.Append(" | ").Append(category.ToString().ToLowerInvariant()).Append(' ')
                    .Append(F(CategoryTotal(score, category)));
                bool open = false;
                foreach (TaskSlot slot in AllSlots)
                {
                    if (CategoryOf(slot) != category || Mathf.Abs(score[slot]) < 0.005f)
                        continue;
                    sb.Append(open ? ", " : " (").Append(slot).Append(' ').Append(F(score[slot]));
                    open = true;
                }
                if (open)
                    sb.Append(')');
            }
            return sb.ToString();
        }

        private static string F(float v) => v.ToString("0.0", CultureInfo.InvariantCulture);

        internal static TaskSlotGroup GroupOf(TaskSlot slot)
        {
            switch (slot)
            {
                case TaskSlot.WinChance:
                case TaskSlot.CardPrice:
                case TaskSlot.Delivery:
                case TaskSlot.MoverOpportunityCost:
                    return TaskSlotGroup.Execution;
                default:
                    return TaskSlotGroup.Intrinsic;
            }
        }

        // Value = Benefit - Cost - Risk - Opportunity. The slots inside each category are that
        // category's terms (for Benefit: the task family's own facts); the fold only sees totals.
        internal static float Fold(TaskScore score) =>
              CategoryTotal(score, TaskSlotCategory.Benefit)
            - CategoryTotal(score, TaskSlotCategory.Cost)
            - CategoryTotal(score, TaskSlotCategory.Risk)
            - CategoryTotal(score, TaskSlotCategory.Opportunity);

        // Component-wise change between two canonical world-task projections. Keeping every fact
        // in its original slot matters even when callers only consume Value: diagnostics and
        // future tuning must still be able to say whether a relocation improved InfoGain,
        // StrategicRelevance, risk, etc. `additionalCardPrice` / `additionalDelivery` are the
        // physical price of making the change, not a synthetic benefit slot.
        internal static TaskScore NetChange(TaskScore from, TaskScore to,
            float additionalCardPrice = 0f, float additionalDelivery = 0f) =>
            TaskScore.FromSlots(slot => to[slot] - from[slot]
                + (slot == TaskSlot.CardPrice ? Mathf.Max(0f, additionalCardPrice)
                    : slot == TaskSlot.Delivery ? Mathf.Max(0f, additionalDelivery) : 0f));

        // The one composition of a target with the actor/chain that serves it: every intrinsic
        // slot of `intrinsic` is carried unchanged, every execution slot comes from `execution`.
        internal static TaskScore WithExecution(TaskScore intrinsic, TaskScore execution) =>
            TaskScore.FromSlots(slot => GroupOf(slot) == TaskSlotGroup.Intrinsic
                ? intrinsic[slot] : execution[slot]);

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

        // The ONE converter of every execution-cost slot (CardPrice, Delivery): AP-equivalents
        // from ActionPrice -> TaskScore points. Callers pass raw AP (+ ActionPrice.Resources);
        // they never multiply a weight themselves.
        internal static float Price(float apEquivalents) => ActionPrice.ToTaskScore(apEquivalents);

        // Raw fact: the TaskScore value the actor's current task loses if the actor is taken
        // (MissionIntent.DisplacementValue). Already in TaskScore points — no price conversion;
        // never a count of actors or an AP figure.
        internal static float MoverOpportunityCost(float displacedTaskValue) =>
            Mathf.Max(0f, displacedTaskValue);

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

        // ActiveDefence's proximity: the common signed slope, steepened past the leash radius so
        // a pursuit far from home loses its desire gradually (no hard gate).
        internal static float ActiveDefenceProximity(int nearestHomeDistance) =>
            OwnTerritoryProximity(nearestHomeDistance)
            - Mathf.Max(0, nearestHomeDistance - AiConfigV2.activeDefenceLeashHexes)
                * AiConfigV2.taskScoreActiveDefenceLeashPerHex;

        internal static float HexThreatRisk(float normalizedRisk) =>
            Mathf.Clamp01(normalizedRisk) * AiConfigV2.taskScoreThreatRiskMax;

        internal static float CitadelThreatRisk(WorldSnapshot snap) =>
            Mathf.Clamp01(snap?.Threat?.CitadelThreatSeverity ?? 0f)
            * AiConfigV2.taskScoreCitadelThreatRiskMax;

        internal static float BaseThreatRisk(WorldSnapshot snap) =>
            Mathf.Clamp01(snap?.Threat?.BaseThreatSeverity ?? 0f)
            * AiConfigV2.taskScoreBaseThreatRiskMax;

        internal static float DetectionRisk(float normalizedRisk) =>
            Mathf.Clamp01(normalizedRisk) * AiConfigV2.taskScoreDetectionRiskMax;

        internal static float InfoGain(float normalizedGain) =>
            Mathf.Clamp01(normalizedGain) * AiConfigV2.taskScoreInfoGainMax;

        internal static float PositiveStaleness(float normalizedStaleness) =>
            Mathf.Clamp01(normalizedStaleness) * AiConfigV2.taskScoreStalenessMax;

        // Price of acting on an aged sighting; stored non-negative, subtracted by the fold.
        internal static float IntelAgePenalty(float normalizedAge) =>
            Mathf.Clamp01(normalizedAge) * AiConfigV2.taskScoreIntelAgePenaltyMax;

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

        // Some card evaluators already author this fact in TaskScore units. Clamp it here rather
        // than letting task owners read the shared score cap directly.
        internal static float GlobalCardEffectScoreUnits(float scoreUnits) =>
            Mathf.Clamp(scoreUnits, 0f, AiConfigV2.taskScoreGlobalCardEffectMax);

        // Fixed per eligible Raid; never derived from defender power (that is WinChance's).
        internal static float RaidReward() => AiConfigV2.RaidReward;

        // Raw fact: the event guard's authored tier (0 light / 1 medium / 2 heavy, -1 unknown).
        internal static float EventReward(int guardTier) =>
            guardTier == 0 ? AiConfigV2.taskScoreEventRewardLight
            : guardTier == 1 ? AiConfigV2.taskScoreEventRewardMedium
            : guardTier >= 2 ? AiConfigV2.taskScoreEventRewardHeavy
            : AiConfigV2.taskScoreEventRewardUnknownTier;

        internal static float AttackReadiness(float normalizedValue) =>
            Mathf.Clamp01(normalizedValue) * AiConfigV2.taskScoreAttackReadinessMax;

        internal static float PreventedDamage(float normalizedValue) =>
            Mathf.Clamp01(normalizedValue) * AiConfigV2.taskScorePreventedDamageMax;

        internal static float WinChance(float probability) =>
            Mathf.Clamp01(probability) * AiConfigV2.taskScoreWinChanceMax;

        // Raw fact: the force a Development output adds, in combat-body units, already weighted by
        // the need it answers (known-threat matchup for Equipment, JustifiedForceNeed for a minted
        // Unit/Hero/Aviation) and by every success chance on the way.
        internal static float ForceAmplification(float needWeightedBodies) =>
            Mathf.Clamp01(needWeightedBodies
                / Mathf.Max(0.0001f, AiConfigV2.taskScoreForceAmplificationFullBodies))
            * AiConfigV2.taskScoreForceAmplificationMax;

        internal static float EconomicExpansionValue(float normalizedValue) =>
            Mathf.Clamp01(normalizedValue) * AiConfigV2.taskScoreEconomicExpansionMax;

        // The ONE ground-combat response fold (Raid, Attack, ActiveDefence): the objective's
        // intrinsic slots are carried unchanged and the concrete force adds its own four — the
        // win chance the shared estimator gave it, the activation AP it spends now (CardPrice), the
        // recurring AP of every further turn on the way (Delivery) and the cost of taking this
        // army off what it does now. Lanes never fold these slots a second, private way.
        internal static TaskScore WithResponse(TaskScore intrinsic, float winChance,
            float activationApNow, float recurringActivationAp, float etaTurns,
            float moverOpportunityCost = 0f) =>
            WithExecution(intrinsic, new TaskScore(
                winChance: WinChance(winChance),
                cardPrice: Price(activationApNow),
                delivery: Price(ActionPrice.RecurringAp(recurringActivationAp, etaTurns)),
                moverOpportunityCost: MoverOpportunityCost(moverOpportunityCost)));

        // The same fold priced off one actor: its activation is spent now only if it has not
        // activated yet this turn. `projectedActivationAp` — the activation of the roster the
        // assembly plan will really field (GroundCombatAssemblyPlanner is its owner); null keeps
        // the actor's own snapshot figure.
        internal static TaskScore WithActorResponse(TaskScore intrinsic, ArmySnapshot actor,
            float winChance, int eta, float moverOpportunityCost = 0f,
            int? projectedActivationAp = null)
        {
            int recurring = projectedActivationAp ?? actor?.ActivationApCost ?? 0;
            float now = actor != null && !actor.HasActivatedThisTurn ? Mathf.Max(0, recurring) : 0f;
            return WithResponse(intrinsic, winChance, now, recurring, eta, moverOpportunityCost);
        }
    }

}
