using System.Collections.Generic;
using Game.Cards;
using Game.Economy;
using Game.HexGrid;
using UnityEngine;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  AXIS DEMAND  (Strategy V2 — Strategic Manager)
    // ===========================================================================================
    //  The generic contract a strategic axis (Recon / Aggression / Defence / Economy /
    //  Development) uses to report a MISSING CAPABILITY — never a concrete card. Axes describe
    //  WHAT is missing; StrategicManager decides HOW (which card, where, reuse vs. create an
    //  army, whether it is worth doing at all). Deliberately extensible: new CapabilityKind /
    //  TraitPreference values are added as later axes need them, without reshaping this contract.
    //
    //  Strategic Manager is NOT a DesireAxis and gets NO radar slice. A demand-driven card play
    //  spends the shared AxisBudgetLedger AP pool. RequestingAxis affects value/priority and is
    //  retained in spend telemetry; it does not own a separate AP or H/E/M/T wallet.
    // ===========================================================================================

    public enum CapabilityKind
    {
        ScoutCapability,
        FieldCombatPower,
        Hero,
        EconomicInfrastructure,
        EconomicExpansionBase,
        DevelopmentInfrastructure,
        DevelopmentOperator,
        CardUpgrade,
        // A mobile resource collector, deployed SOLO (mirrors ScoutCapability's own-army rule —
        // see MaterializationChainEnumerator.EnumerateForDemand) for one specific known resource
        // hex (AxisDemand.EconomyResourceType). No facility is built; the card itself is the unit.
        CollectorCapability,
    }

    // AGG-RAID §6 — HOW a capability must be delivered, orthogonal to WHICH capability it is.
    //   Any                 — the existing, unconstrained behaviour (attach, garrison, new army…).
    //   IndependentFieldArmy— the capability must arrive as a SEPARATE mobile field army that can
    //                         move to the consumer on its own. A Raid reinforcement cannot be
    //                         satisfied by attaching a unit onto the (remote) primary or by
    //                         depositing it into a garrison.
    public enum CapabilityDeliveryShape { Any, IndependentFieldArmy }

    [System.Flags]
    public enum TraitPreference
    {
        None       = 0,
        Stealth    = 1 << 0,
        AntiArmour = 1 << 1,
        Ranged     = 1 << 2,
        Melee      = 1 << 3,
    }

    public sealed class AxisDemand
    {
        public string TraceId;
        public DesireAxis RequestingAxis;
        // Legacy transport value. Migrated world-map demand families assign this from
        // WorldTaskScore.Value; non-world families (Development/Production) keep their existing
        // value path until their own migration.
        public float Value;
        public TaskScore WorldTaskScore;
        public HexCoord? TargetHex;
        public CapabilityKind Capability;
        public float DesiredAmount;
        public TraitPreference RequiredTraits;
        public TraitPreference PreferredTraits;
        public float MinimumFollowupAp;
        public string Explain;
        public ScoutCapabilityContext ScoutContext;
        public DevelopmentOpportunity DevOpportunity;
        public ResourceType? EconomyResourceType;
        public CardData EconomyBuildCard;
        public ResourceCost EconomyBuildResourceCost;
        public float EconomyBuildApCost;
        public float EconomyExpectedIncomeGain;
        public float EconomySiteValue;
        public float EconomyTravelCost;
        public float EconomyThreatExposure;
        public float EconomyHeroOpportunityCost;
        public float EconomyAssignmentApCost;
        public float EconomyPaybackTurns;
        // Continuity-owned, pre-intent wait pressure. It affects only Economy's within-lane
        // admission order; BaseValue remains intrinsic so critical Defence/Reaction is untouched.
        // Full intrinsic incumbent value witnessed during the same Base candidate scan.
        // Only non-null for the ONE selected challenger; never substitute site-only BuildValue.
        public float? EconomySwitchIncumbentValue;
        public int? EconomyPreferredBuilderArmyId;
        public int EconomyProjectedActivationApCost;
        public int EconomyProjectedMaxMovement;
        // Analysis-owned structural routes for the selected site. Demand applies intent/commitment
        // policy; no downstream stage has to query Provisioning or live registries to rediscover it.
        public IReadOnlyList<EconomyBuilderRouteSnapshot> EconomyBuilderRoutes;

        // DEV OPERATOR: preserves the exact Research/Production lane that raised the prerequisite.
        // Null for every unrelated demand and for legacy/test demands that intentionally do not
        // constrain a mode. InfrastructureFulfillment consumes this identity; it must not re-pick
        // a different facility mode merely because that facility happens to share the target hex.
        public ResearchProductionMode? DevelopmentOperatorMode;

        public float RequiredCapabilityPower;
        public bool IsPersistenceDeferred;

        // AGG-RAID §6 — delivery-shape constraint (see CapabilityDeliveryShape) and the EXACT
        // durable mission this capability is for. ConsumerIntentKey turns a generic
        // "FieldCombatPower please" into "FieldCombatPower for Raid #42", so Phase A can hand the
        // delivered army straight to that intent instead of leaving it to generic housekeeping,
        // and so a second identical support convoy is never requested for the same operation.
        public CapabilityDeliveryShape DeliveryShape = CapabilityDeliveryShape.Any;
        public MissionIntentKey? ConsumerIntentKey;

        public override string ToString() =>
            (string.IsNullOrEmpty(TraceId) ? "" : $"[{TraceId}] ")
            + $"{DesireAxes.Abbrev(RequestingAxis)} needs {DesiredAmount:0.#}x {Capability}"
            + (DevelopmentOperatorMode.HasValue ? $" ({DevelopmentOperatorMode.Value})" : "")
            + (RequiredTraits != TraitPreference.None ? $" !{RequiredTraits}" : "")
            + (PreferredTraits != TraitPreference.None ? $" ~{PreferredTraits}" : "")
            + (TargetHex.HasValue ? $" @{TargetHex.Value.Q},{TargetHex.Value.R}" : "")
            + (MinimumFollowupAp > 0f ? $" +{MinimumFollowupAp:0.#}fu" : "")
            + $" val {Value:0.0}";
    }

    // Lifecycle policy for translating AxisDemand.Value into an urgency fraction/bonus. This is
    // deliberately outside TaskScoreEvaluator: urgency is not intrinsic world value. AxisDemand is
    // the migration boundary that knows which value family a demand belongs to, so every downstream
    // consumer must use this one adapter instead of guessing a numeric scale independently.
    internal static class DemandUrgencyPolicy
    {
        internal static float Normalized(AxisDemand demand)
        {
            if (demand == null)
                return 0f;
            if (demand.RequestingAxis != DesireAxis.Development)
                return NormalizedWorldValue(demand.Value);
            return Mathf.Clamp01((demand.Value - AiConfigV2.stratHoldUrgencyRampLo)
                / Mathf.Max(0.01f,
                    AiConfigV2.stratHoldUrgencyRampHi - AiConfigV2.stratHoldUrgencyRampLo));
        }

        // Verified AGG/RCN resource blocks carry the same migrated world TaskScore.Value.
        // Keep both urgency consumers on this one existing scale adapter.
        internal static float NormalizedWorldValue(float value) =>
            Mathf.Clamp01((value - AiConfigV2.taskScoreUrgencyRampLo)
                / Mathf.Max(0.01f,
                    AiConfigV2.taskScoreUrgencyRampHi - AiConfigV2.taskScoreUrgencyRampLo));

        internal static float Bonus(AxisDemand demand) =>
            Normalized(demand) * AiConfigV2.stratHoldUrgencyMax;
    }
}
