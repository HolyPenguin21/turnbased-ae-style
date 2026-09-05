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
    //  is charged to demand.RequestingAxis's AP entitlement (AxisBudgetLedger) — the axis that
    //  needed the capability pays for it.
    // ===========================================================================================

    public enum CapabilityKind
    {
        ScoutCapability,       // a solo Recce able to run a Scout mission
        GarrisonCombatPower,   // defensive body at a specific base
        FieldCombatPower,      // offensive body for a field force
        Hero,                  // a hero to lead / research / build

        // Infrastructure. Fulfilled by BuildingPlayExecutor through the authoritative gameplay
        // APIs (HexSelectionController.SpawnBuilding / TryBuildExtractionFacility), NOT by the
        // Unit/Hero MaterializationCandidateBuilder path — see InfrastructureFulfillment.
        EconomicInfrastructure,    // an extraction facility / economy building at a resource site
        DevelopmentInfrastructure, // a Research/Production-capable base or facility
    }

    // Optional preferred characteristics of the capability. Flags so a demand can want several.
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
        // Turn-scoped correlation id (AiV2Trace — "{scope}-D01"). Assigned by DemandLayer.Generate
        // once the full demand list for the pass exists; carried into StrategicManager Phase A and
        // every [CHECK] line raised for this demand. Null only in a bare unit test / sim.
        public string TraceId;

        public DesireAxis RequestingAxis;

        // Strategic merit of the UNMET opportunity behind this demand, on the same 0..100 scale
        // as MissionProposal.BaseValue (for Recon: the BaseValue of the best uncovered objective).
        public float Value;

        // Where the capability is wanted, when that is meaningful (biases placement / card fit).
        public HexCoord? TargetHex;

        public CapabilityKind Capability;

        // How many units of the capability are still MISSING (already-available supply subtracted).
        public float DesiredAmount;

        // HARD constraint — a card that does not satisfy every RequiredTraits flag cannot fulfil
        // this demand at all (e.g. a Surveil objective needs a stealth-capable scout; a plain
        // Recce played for it would still fail provisioning — NoMoverExists). The available-supply
        // count that produced DesiredAmount must be computed against the SAME constraint.
        public TraitPreference RequiredTraits;

        // SOFT preference — only a scoring tie-break between cards that already satisfy
        // RequiredTraits. Never a filter.
        public TraitPreference PreferredTraits;

        // FIXED mission overhead AP that does NOT depend on which actor / card fulfils the demand
        // (0 for Recon today; e.g. a raid's fixed assembly overhead later). StrategicManager adds
        // the ACTOR-dependent part per candidate — the deployed unit's own activation AP plus any
        // action surcharge the demand's RequiredTraits imply — on top of this. The full follow-up
        // total is RESERVED, never spent: Phase A must not spend the requesting axis's entitlement
        // (or real AP) down past it, or it creates a capability the mission allocator can no
        // longer fund the same turn.
        public float MinimumFollowupAp;

        public string Explain;

        // OPTIONAL capability-specific mission context, so StrategicManager can judge the QUALITY
        // of a materialization in the setting the demand was raised for — not just capability +
        // trait match. Never a card choice / card name / pre-scored card. Populated per axis:
        // Recon fills ScoutContext; other capabilities add their own typed context as they land.
        public ScoutCapabilityContext ScoutContext;

        // --- Identity extensions (2026-08-31 review follow-up) --------------------------------
        // ECONOMY: the resource type this EconomicInfrastructure demand is about. Fulfillment must
        // create an income source for THIS type (an extraction facility on a same-type site) — a
        // generic Base elsewhere is NOT a valid fulfillment (spec §4).
        public ResourceType? EconomyResourceType;

        // Target capability POWER the demand needs met, on the same power scale as
        // ArmySnapshot.EffectiveArmyPower. Set by DefenceDemands (= required garrison power at the
        // asset). MaterializationCandidateBuilder.ScorePlanA reads it for the garrison-saturation
        // penalty: a destination that already reaches this figure should not keep attracting cards.
        public float RequiredCapabilityPower;

        // Persistence-gate escape (spec: "Persistence Gate: Bootstrap & No-Alternative-Work
        // Escape"). True for a demand an axis raised for a REAL runnable opportunity + deliverable
        // capability gap, but whose capacity deficit has not yet persisted long enough to auto-play.
        // Not axis-specific — any axis's persistence gate can use it. StrategicPhaseA excludes it
        // from the normal per-turn arbitration pool and only reconsiders it once every currently
        // active (non-deferred) demand this pass is satisfied, blocked, or infeasible — i.e. once
        // there is no other actionable work left to prefer over it — and only if a legal/affordable
        // deliverable candidate exists for it right now (never a phantom fulfillment).
        public bool IsPersistenceDeferred;

        // How much PRE-EXISTING OperationallyFeasibleIfFunded capacity of this demand's class the
        // emitting axis measured at emission time (0 when none) — see ReconOperationalFeasibility.
        // This does NOT mean funded/actionable-now: it is measured before AxisBudgetLedger.Create
        // even runs, so it says nothing about whether the requesting axis will actually hold AP for
        // that actor's work. StrategicPhaseA's own per-turn loop also runs BEFORE MissionLayer/the
        // mission allocator bind an idle-but-uncommitted existing actor to a runnable job, so Phase A
        // cannot see whether such an actor is about to get real work either.
        // A no-alternative-work reconciliation must NOT promote a persistence-deferred demand while
        // this is > 0 AND the requesting axis's AxisBudgetLedger balance is still funded (see
        // ReconOperationalFeasibility.FundedActionableNow, checked post-ledger in StrategicPhaseA) —
        // that would materialise an extra unit of capacity ahead of an existing actor that is about
        // to be given funded work of its own a few pipeline stages later, stealing its AP/resources.
        // When the axis is NOT funded, this pre-existing count must not block the escape — an actor
        // that physically exists but that its own axis cannot pay for right now is not "alternative
        // actionable work".
        // Left at its 0 default for demands that never go through IsPersistenceDeferred.
        public int ExistingUsableCapacityAtEmission;

        public override string ToString() =>
            (string.IsNullOrEmpty(TraceId) ? "" : $"[{TraceId}] ")
            + $"{DesireAxes.Abbrev(RequestingAxis)} needs {DesiredAmount:0.#}x {Capability}"
            + (RequiredTraits != TraitPreference.None ? $" !{RequiredTraits}" : "")
            + (PreferredTraits != TraitPreference.None ? $" ~{PreferredTraits}" : "")
            + (TargetHex.HasValue ? $" @{TargetHex.Value.Q},{TargetHex.Value.R}" : "")
            + (MinimumFollowupAp > 0f ? $" +{MinimumFollowupAp:0.#}fu" : "")
            + $" val {Value:0.0}";
    }
}
