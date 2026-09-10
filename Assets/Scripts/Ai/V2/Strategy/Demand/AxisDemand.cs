using System.Collections.Generic;
using Game.Cards;
using Game.Economy;
using Game.HexGrid;

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
        GarrisonCombatPower,
        FieldCombatPower,
        Hero,
        EconomicInfrastructure,
        EconomicExpansionBase,
        DevelopmentInfrastructure,
        DevelopmentOperator,
        CardUpgrade,
    }

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
        public float Value;
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
        public float EconomyStrategicUrgency;
        public int? EconomyPreferredBuilderArmyId;
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
}
