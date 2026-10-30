using System.Collections.Generic;
using System.Linq;
using Game.Cards;
using Game.Economy;
using Game.HexGrid;
using Game.Map;
using Game.Units;
using UnityEngine;

using Game.Combat;

namespace Game.Ai.V2
{
    // Strategic effect descriptors and their enums; the ability table that produces them is
    // StrategicEffectRegistry.ByAbility.
    internal enum StrategicEffectContext
    {
        Flat,               // BaseFit as-is
        RecurringResource,  // BaseFit scaled by economy insecurity (recurring AP income, …)
        GlobalRecurringResource, // AI-MGR — a PlayerGlobal per-turn resource yield (ApBonus today).
                                 // Value is fully DYNAMIC (yield x horizon x futureOpportunity x
                                 // marginalResourceUtility x carrierPersistence x saturation) — see
                                 // GlobalRecurringValue. BaseFit is IGNORED; GlobalYieldPerTurn +
                                 // GlobalResource carry the semantics.
        EnemyThreatScaled,  // BaseFit scaled by the matching enemy-threat magnitude (AA<->air, AT<->armour)
        TargetDensity,      // AoE   — BaseFit scaled by EXPECTED AFFECTED ENEMY BODIES near the deploy
        ExpectedSustain,    // regen — BaseFit scaled by expected combat DURATION × projected survivability
        EligibleAllies,     // aura (candidate -> army) — BaseFit scaled by eligible friendly units in the dest army
        AllyHealing,       // one wounded Bio ally, per carrier action/hero round
        TransportMobility, // actual expected movement gain of the whole recipient army
        MobileAirfield,    // held/deployed aircraft not yet served by mobile deck capacity
        MobileRepair,      // additional paid repair access for wounded own units on this hex
        BuildingDisruption, // missing carrier for a known hostile structural defence
        FreeBattleSlots,    // summon— BaseFit scaled by min(free slots, CapacityRequirement) / CapacityRequirement
    }

    // final closure §3 — generic effect semantics carried as DATA on the descriptor. The central
    // StrategicCardEvaluator NEVER branches on any of these; only StrategicEffectRegistry's own
    // contextual scalers read them. All default to a neutral value so every existing row is
    // unchanged.
    internal enum EffectScope
    {
        SelfBody,           // affects only the deployed body
        DestArmy,           // affects the army the body joins (auras)
        EnemiesNearDeploy,  // affects enemy bodies around the deploy hex (AoE / splash)
        PlayerGlobal,       // AI-MGR — affects the WHOLE player (a global per-turn economy / tempo
                            // effect). Not a role/placement contribution: priced ONCE per card,
                            // independent of the IntendedRole being scored, exactly like an
                            // incoming aura synergy.
    }

    // AI-MGR — which global per-turn resource a PlayerGlobal recurring effect yields. `None` for
    // every non-global row. New recurring mechanics extend this descriptor and the single
    // GlobalRecurringValue switch — never a card-specific evaluator.
    internal enum GlobalResourceKind
    {
        None,
        ActionPoints,
        Human,
        Energy,
        Materials,
        Tech,
    }

    internal enum EffectTiming
    {
        Persistent,    // always on while the body is fielded
        DuringCombat,  // only while a battle is resolving (regen, combat auras)
        OneShot,       // fires once (on deploy / on trigger)
    }

    internal enum EffectStacking
    {
        Stack,        // each copy adds full value
        Unique,       // only the first copy counts
        Diminishing,  // extra copies worth progressively less (not modelled yet — treated as Stack)
    }

    // Which StrategicUseScoreBreakdown term an effect contributes to. Lets AoE / regen / summon /
    // future mechanics plug into the RIGHT scoring axis instead of only RoleFit — the "registry is
    // bypassed for combat roles" blocker.
    internal enum EffectField
    {
        RoleFit,
        ImmediateTempo,
        ThreatResponse,
        CapabilityGap,
        ForceGrowth,
        Synergy,
    }

    internal readonly struct StrategicEffect
    {
        // Exact detached save reconstruction; no planning, registration or normalization.
        internal StrategicEffect(bool __matchSave, global::Game.Ai.V2.IntendedRole __Role, float __BaseFit, global::Game.Ai.V2.StrategicEffectContext __Context, global::Game.Ai.V2.EffectField __Field, bool __CountsAsCoverage, global::System.Func<global::Game.Combat.WorthIt.DefenderProfile, bool> __EligiblePredicate, global::Game.Ai.V2.EffectScope __Scope, float __Magnitude, float __Probability, global::Game.Ai.V2.EffectTiming __Timing, int __DurationRounds, int __CapacityRequirement, global::Game.Ai.V2.EffectStacking __Stacking, string __StackingKey, global::Game.Ai.V2.GlobalResourceKind __GlobalResource, float __GlobalYieldPerTurn, bool __PrimaryExchange, bool __RequiresHit, int __SecondaryTargets, int __SecondaryPriority, bool __BodyOnly)
        {
            Role = __Role;
            BaseFit = __BaseFit;
            Context = __Context;
            Field = __Field;
            CountsAsCoverage = __CountsAsCoverage;
            EligiblePredicate = __EligiblePredicate;
            Scope = __Scope;
            Magnitude = __Magnitude;
            Probability = __Probability;
            Timing = __Timing;
            DurationRounds = __DurationRounds;
            CapacityRequirement = __CapacityRequirement;
            Stacking = __Stacking;
            StackingKey = __StackingKey;
            GlobalResource = __GlobalResource;
            GlobalYieldPerTurn = __GlobalYieldPerTurn;
            PrimaryExchange = __PrimaryExchange;
            RequiresHit = __RequiresHit;
            SecondaryTargets = __SecondaryTargets;
            SecondaryPriority = __SecondaryPriority;
            BodyOnly = __BodyOnly;
        }

        public readonly IntendedRole Role;
        public readonly float BaseFit;
        public readonly StrategicEffectContext Context;
        public readonly EffectField Field;       // which breakdown term this value lands in
        public readonly bool CountsAsCoverage;   // contributes to "my force already covers role X"
        // review-r4 P1 ARCH follow-up — TARGET FILTER. For the EligibleAllies / aura contexts: which
        // bodies THIS effect benefits (an Armored aura vs a Ranged aura vs a generic "+X to all").
        // null => any body. Kept on the descriptor so a new aura mechanic supplies its own predicate,
        // not a registry-wide "generic" count. Operates on the SNAPSHOT member profile — so it works
        // for BOTH aura directions (candidate's aura over dest-army members, and a dest-army aura
        // over the incoming candidate's projected profile).
        public readonly System.Func<WorthIt.DefenderProfile, bool> EligiblePredicate;

        // --- final closure §3 generic semantics. Every field has a NEUTRAL default so an existing
        //     row that omits it is byte-for-byte unchanged; each one is genuinely CONSUMED (see
        //     ContextualValue / Contributions / ComputeIncomingAuraSynergy):
        //       Scope             which population an area/aura effect covers (TargetDensity)
        //       Magnitude         strength multiplier on BaseFit; 0 is legal ("no effect")
        //       Probability       0..1 chance/condition the effect lands; 0 is legal ("never")
        //       Timing            Persistent full value; DuringCombat discounted when no fight is
        //                         expected at the deploy; OneShot = single application
        //       DurationRounds    combat rounds the effect lasts (0 = permanent) — caps regen ticks
        //                         and scales a temporary Summon
        //       CapacityRequirement battle cells the effect needs to fully realise (Summon body count)
        //       Stacking          Stack: each copy adds full value; Unique: only one copy counts;
        //                         Diminishing: geometric decay per extra copy
        //       StackingKey       SEMANTIC identity for stacking. Copies of the SAME mechanic share
        //                         a non-null key and are reduced together by `Stacking`; a null key
        //                         (every existing row) means "stands alone" — never merged with any
        //                         other effect, so two different auras that happen to share numbers
        //                         are still two effects.
        public readonly EffectScope Scope;
        public readonly float Magnitude;
        public readonly float Probability;
        public readonly EffectTiming Timing;
        public readonly int DurationRounds;
        public readonly int CapacityRequirement;
        public readonly EffectStacking Stacking;
        public readonly string StackingKey;
        // AI-MGR — PlayerGlobal recurring-resource semantics (GlobalRecurringResource context).
        //   GlobalResource       which per-turn resource this yields (None for every other row)
        //   GlobalYieldPerTurn   how much of it per turn, per source (canonical gameplay number)
        public readonly GlobalResourceKind GlobalResource;
        public readonly float GlobalYieldPerTurn;
        public readonly bool PrimaryExchange;
        public readonly bool RequiresHit;
        public readonly int SecondaryTargets;
        public readonly int SecondaryPriority;
        // Worth something only on a body that attacks: a hero never acts in battle, so an
        // attacker-side skill on a hero card adds nothing (EquipmentEfficiency prices them 0 too).
        public readonly bool BodyOnly;

        public StrategicEffect(IntendedRole role, float baseFit, StrategicEffectContext context,
            EffectField field, bool coverage,
            System.Func<WorthIt.DefenderProfile, bool> eligiblePredicate = null,
            EffectScope scope = EffectScope.SelfBody, float magnitude = 1f, float probability = 1f,
            EffectTiming timing = EffectTiming.Persistent, int durationRounds = 0,
            int capacityRequirement = 1, EffectStacking stacking = EffectStacking.Stack,
            string stackingKey = null,
            GlobalResourceKind globalResource = GlobalResourceKind.None, float globalYieldPerTurn = 0f,
            bool primaryExchange = false, bool requiresHit = false,
            int secondaryTargets = 0, int secondaryPriority = 0, bool bodyOnly = false)
        {
            Role = role;
            BaseFit = baseFit;
            Context = context;
            Field = field;
            CountsAsCoverage = coverage;
            EligiblePredicate = eligiblePredicate;
            Scope = scope;
            // A caller that OMITS the arg gets 1f from the default; a caller that explicitly passes
            // 0 means it — no silent "0 becomes 100%". Only clamp the ranges.
            Magnitude = Mathf.Max(0f, magnitude);
            Probability = Mathf.Clamp01(probability);
            Timing = timing;
            DurationRounds = System.Math.Max(0, durationRounds);
            CapacityRequirement = capacityRequirement < 1 ? 1 : capacityRequirement;
            Stacking = stacking;
            StackingKey = string.IsNullOrEmpty(stackingKey) ? null : stackingKey;
            GlobalResource = globalResource;
            GlobalYieldPerTurn = Mathf.Max(0f, globalYieldPerTurn);
            PrimaryExchange = primaryExchange;
            RequiresHit = requiresHit;
            SecondaryTargets = System.Math.Max(0, secondaryTargets);
            SecondaryPriority = secondaryPriority;
            BodyOnly = bodyOnly;
        }
    }

    // The distributed strategic value of a card's effects for one role — one term per
    // StrategicUseScoreBreakdown axis, so nothing is double-counted and every axis is reachable.
    internal readonly struct EffectContribution
    {
        // TARGET / ROLE-DEPENDENT contributions — the evaluator scales these by placement / target-
        // hex fit where it does that (RoleFit in Phase A today).
        public readonly float RoleFit;
        public readonly float ImmediateTempo;
        public readonly float ThreatResponse;
        public readonly float CapabilityGap;
        public readonly float ForceGrowth;
        public readonly float Synergy;

        // AI-MGR — PlayerGlobal (target-INDEPENDENT) contributions, one term per breakdown axis so a
        // PlayerGlobal effect still honours its descriptor's EffectField: ApBonus -> GlobalRoleFit
        // today; a future +draw/turn / global force-growth / global tempo effect lands in its own
        // axis with ZERO evaluator edits. Priced ONCE per card and added to the matching bd.* term
        // AFTER any target multiplier — PlayerGlobal means "target-independent", not "is RoleFit".
        public readonly float GlobalRoleFit;
        public readonly float GlobalImmediateTempo;
        public readonly float GlobalThreatResponse;
        public readonly float GlobalCapabilityGap;
        public readonly float GlobalForceGrowth;
        public readonly float GlobalSynergy;

        public EffectContribution(
            float roleFit, float tempo, float threat, float gap, float grow, float syn,
            float globalRoleFit, float globalTempo, float globalThreat, float globalGap,
            float globalGrow, float globalSyn)
        {
            RoleFit = roleFit;
            ImmediateTempo = tempo;
            ThreatResponse = threat;
            CapabilityGap = gap;
            ForceGrowth = grow;
            Synergy = syn;
            GlobalRoleFit = globalRoleFit;
            GlobalImmediateTempo = globalTempo;
            GlobalThreatResponse = globalThreat;
            GlobalCapabilityGap = globalGap;
            GlobalForceGrowth = globalGrow;
            GlobalSynergy = globalSyn;
        }

        // The same contribution with its local RoleFit removed (every other axis untouched).
        public EffectContribution WithoutRoleFit() => new EffectContribution(
            0f, ImmediateTempo, ThreatResponse, CapabilityGap, ForceGrowth, Synergy,
            GlobalRoleFit, GlobalImmediateTempo, GlobalThreatResponse, GlobalCapabilityGap,
            GlobalForceGrowth, GlobalSynergy);
    }

    // A set of IntendedRoles a force element covers. Replaces the ad-hoc Has* bools on ArmySnapshot
    // / BaselineForceReadiness — a new coverage role flows in automatically.
    public readonly struct RoleCoverage
    {
        private readonly int _bits;
        private RoleCoverage(int bits) { _bits = bits; }

        public static readonly RoleCoverage None = new RoleCoverage(0);
        public bool Has(IntendedRole r) => (_bits & (1 << (int)r)) != 0;
        public RoleCoverage With(IntendedRole r) => new RoleCoverage(_bits | (1 << (int)r));
        public RoleCoverage Union(RoleCoverage other) => new RoleCoverage(_bits | other._bits);
        public bool Any => _bits != 0;
        public static bool operator ==(RoleCoverage a, RoleCoverage b) => a._bits == b._bits;
        public static bool operator !=(RoleCoverage a, RoleCoverage b) => a._bits != b._bits;
        public bool Equals(RoleCoverage other) => _bits == other._bits;
        public override bool Equals(object obj) => obj is RoleCoverage other && Equals(other);
        public override int GetHashCode() => _bits;
    }
}
