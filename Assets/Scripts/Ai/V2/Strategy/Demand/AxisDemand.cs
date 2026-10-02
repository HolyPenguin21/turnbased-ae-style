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
    //  spends the live shared AP pool (PhaseAApBudget). RequestingAxis affects value/priority and is
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
        // An Economy "budget of opportunities" source: a card whose effective abilities carry a
        // PlayerGlobal recurring-resource effect (ApBonus / Produce*, see
        // StrategicEffectRegistry.HasGlobalRecurringEffect) put into play from hand. The card is
        // pinned in AxisDemand.EconomySourceCard; the effect works wherever the carrier is in play,
        // so there is no target hex. Facility carriers are placed by InfrastructureFulfillment,
        // Unit/Hero carriers by the ordinary materialization chain. A Base carrier is not here —
        // founding a Base stays with the FoundBase pipeline (its TaskScore prices the same effect).
        GlobalResourceFacility,
        GlobalResourceCarrier,
    }

    // HOW a capability must be delivered, orthogonal to WHICH capability it is.
    //   Any                 — the existing, unconstrained behaviour (attach, garrison, new army…).
    //   IndependentFieldArmy— the capability must arrive as a SEPARATE mobile field army that can
    //                         move to the consumer on its own. A Raid reinforcement cannot be
    //                         satisfied by attaching a unit onto the (remote) primary or by
    //                         depositing it into a garrison.
    //   Garrison            — the capability must be deposited into the garrison at TargetHex
    //                         (strike force step 7: a base the fist just took is held from hand).
    public enum CapabilityDeliveryShape { Any, IndependentFieldArmy, Garrison }

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
        // Legacy transport value. Every demand family, including Development/Production, assigns
        // this from WorldTaskScore.Value.
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
        // Collector demands only: the resource this collector serves is itself scarce
        // (TaskScoreEvaluator.ResourcePriority >= AiConfigV2.economyCollectorScarcePriority). Only
        // then may the collector draw on other builds' deferred holds (SpendAuthorityFor).
        public bool EconomyResourceScarce;
        public CardData EconomyBuildCard;
        public ResourceCost EconomyBuildResourceCost;
        public float EconomyBuildApCost;
        public float EconomyExpectedIncomeGain;
        public float EconomySiteValue;
        public float EconomyTravelCost;
        public float EconomyHeroOpportunityCost;
        public float EconomyAssignmentApCost;
        public float EconomyPaybackTurns;
        // GlobalResourceFacility / GlobalResourceCarrier only: the exact hand card this demand puts
        // into play. Null for every other demand.
        public CardData EconomySourceCard;
        // Continuity-owned, pre-intent wait pressure. It affects only Economy's within-lane
        // admission order; BaseValue remains intrinsic so critical Defence/Reaction is untouched.
        // Full intrinsic incumbent value witnessed during the same Base candidate scan.
        // Only non-null for the ONE selected challenger; never substitute site-only BuildValue.
        public float? EconomySwitchIncumbentValue;
        public int? EconomyPreferredBuilderArmyId;
        // Set only on an Economy "new hero" alternative (DemandLayer.PairedNewHeroAlternative and
        // the ready-loss fallback): what delivering this same build with the best READY hero costs,
        // in TaskScore units (delivery + mover opportunity). A new-hero chain is admitted only when
        // its own card price plus delivery is lower. Null — no ready hero to compare against.
        public float? EconomyReadyDeliveryCost;
        public bool IsEconomyNewHeroAlternative => EconomyReadyDeliveryCost.HasValue;
        // Analysis-owned structural routes for the selected site. Demand applies intent/commitment
        // policy; no downstream stage has to query Provisioning or live registries to rediscover it.
        public IReadOnlyList<EconomyBuilderRouteSnapshot> EconomyBuilderRoutes;

        // DEV OPERATOR: preserves the exact Research/Production lane that raised the prerequisite.
        // Null for every unrelated demand and for legacy/test demands that intentionally do not
        // constrain a mode. InfrastructureFulfillment consumes this identity; it must not re-pick
        // a different facility mode merely because that facility happens to share the target hex.
        public ResearchProductionMode? DevelopmentOperatorMode;

        public float RequiredCapabilityPower;
        // Unbound Attack only: the actual free field army this hand card must strengthen.
        // A separate purchased container does not satisfy this request.
        public int? AttackFistArmyId;
        // T01 — AttackFistArmyId names the claimed host of a live Attack mobilization preparation
        // (ActorCommitments.IsPreparationHost): it may still be weak, hero-only or an empty shell,
        // so delivery checks the exact claimed container instead of a structural combat actor.
        public bool AttackFistIsPreparationHost;
        // The preparation host already clears the power bar but cannot damage every known
        // defender of AttackCoverageTargetHex (GroundCombatFeasibility.Clears' coverage): delivery
        // accepts only a card that lets the host damage more of them.
        public bool AttackCoverageGap;
        public HexCoord? AttackCoverageTargetHex;
        public bool IsPersistenceDeferred;
        // 2026-10-01 — Phase A proved this demand structurally undeliverable this pass (no chain
        // shape at all, or shapes that pass the play preflight yet cannot deliver): it keeps no
        // strategic claim on hand cards (MaterializationFeasibility.UnresolvedClaimFor), so Phase B
        // may spend them (Korrin/Orlan T15-T25: heroes held for an Economy Hero demand whose
        // every delivery exceeded its site value).
        public bool StructurallyUndeliverable;

        // For an Economy Hero-prerequisite demand (the builder hero a pending build still needs):
        // the reservation owner of THAT build, whose deferred hold the hero's own card may draw on —
        // the hero is the build's first step, not a rival spend. Null for every other demand.
        public string EconomyHeroBuildOwner =>
            InfrastructureFulfillment.EconomyHeroPrerequisiteOwner(this);

        // Which strategic reservations the action closing this demand may draw on — the one rule
        // every Phase-A stage reads (InfrastructureFulfillment.SpendAuthorityFor).
        internal SpendAuthority SpendAuthority => InfrastructureFulfillment.SpendAuthorityFor(this);

        // Delivery-shape constraint (see CapabilityDeliveryShape) and the EXACT
        // durable mission this capability is for. ConsumerIntentKey turns a generic
        // "FieldCombatPower please" into "FieldCombatPower for Raid #42", so Phase A can hand the
        // delivered army straight to that intent instead of leaving it to generic housekeeping,
        // and so a second identical support convoy is never requested for the same operation.
        public CapabilityDeliveryShape DeliveryShape = CapabilityDeliveryShape.Any;
        public MissionIntentKey? ConsumerIntentKey;
        // Optional durable consumer family. ConsumerIntentKey supplies stable identity; this field
        // prevents family-specific delivery finalization (Raid support handoff) from claiming a
        // different operation that happens to request the same capability shape.
        public MissionKind? ConsumerMissionKind;

        public override string ToString() =>
            (string.IsNullOrEmpty(TraceId) ? "" : $"[{TraceId}] ")
            + $"{DesireAxes.Abbrev(RequestingAxis)} needs {DesiredAmount:0.#}x {Capability}"
            + (DevelopmentOperatorMode.HasValue ? $" ({DevelopmentOperatorMode.Value})" : "")
            + (EconomySourceCard?.Definition != null ? $" [{EconomySourceCard.Definition.displayName}]" : "")
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
        internal static float Normalized(AxisDemand demand) =>
            demand == null ? 0f : NormalizedWorldValue(demand.Value);

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
