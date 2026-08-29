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
        ScoutCapability,       // a solo Recce able to run a Scout mission (the only one wired now)
        GarrisonCombatPower,   // defensive body at a specific base            (future)
        FieldCombatPower,      // offensive body for a field force             (future)
        Hero,                  // a hero to lead / research / build            (future)
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

        public override string ToString() =>
            $"{DesireAxes.Abbrev(RequestingAxis)} needs {DesiredAmount:0.#}x {Capability}"
            + (RequiredTraits != TraitPreference.None ? $" !{RequiredTraits}" : "")
            + (PreferredTraits != TraitPreference.None ? $" ~{PreferredTraits}" : "")
            + (TargetHex.HasValue ? $" @{TargetHex.Value.Q},{TargetHex.Value.R}" : "")
            + (MinimumFollowupAp > 0f ? $" +{MinimumFollowupAp:0.#}fu" : "")
            + $" val {Value:0.0}";
    }
}
