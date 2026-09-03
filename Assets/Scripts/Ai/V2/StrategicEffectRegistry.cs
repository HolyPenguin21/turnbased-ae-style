using System.Collections.Generic;
using System.Linq;
using Game.Cards;
using Game.HexGrid;
using Game.Map;
using Game.Units;
using UnityEngine;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  STRATEGIC EFFECT REGISTRY  (Strategy V2 — AI-MGR-01 review-r4 P1 ARCH)
    // ===========================================================================================
    //  The ONE place "this ability / stat feature is worth X toward strategic role Y" knowledge
    //  lives. Before this, DeriveRoles / SupportRoleFit / SurplusCapabilityGap / ThreatResponse /
    //  the BaselineForceReadiness + WorldAnalysis coverage vector each hard-coded
    //  `Contains(UnitAbilities.AntiAir | Hyperkinetic | ApBonus | Researcher | Assembler)`, so a new
    //  mechanic (Splash/AoE, Regeneration, army aura, temporary Summon, …) could not influence
    //  strategic scoring without another `Contains(UnitAbilities.X)` edit in the evaluator.
    //
    //  Pipeline:  Skill/ability -> StrategicEffect(s) -> Capability/role + Contextual value ->
    //             RoleFit / RoleCoverage / DeriveRoles.
    //
    //  ACCEPTANCE: adding a new skill/effect descriptor must touch ONLY `ByAbility` here (+ its
    //  contextual-value branch if it uses a new context) — never StrategicManager, never the
    //  StrategicCardEvaluator role switch.
    //
    //  Recce/Stealth deliberately stay direct `AbilityParams` helpers (concrete gameplay
    //  capabilities, like they already were), and the Research/Production "operator vocation" stays
    //  a direct ability check — those are not the strategic-scoring switch this layer replaces.
    // ===========================================================================================

    internal enum StrategicEffectContext
    {
        Flat,               // BaseFit as-is
        RecurringResource,  // BaseFit scaled by economy insecurity (recurring AP income, …)
        EnemyThreatScaled,  // BaseFit scaled by the matching enemy-threat magnitude (AA<->air, AT<->armour)
        TargetDensity,      // AoE   — BaseFit scaled by EXPECTED AFFECTED ENEMY BODIES near the deploy
        ExpectedSustain,    // regen — BaseFit scaled by expected combat DURATION × projected survivability
        EligibleAllies,     // aura (candidate -> army) — BaseFit scaled by eligible friendly units in the dest army
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

        // --- final closure §3 generic semantics (neutral defaults) ---
        public readonly EffectScope Scope;
        public readonly float Magnitude;          // strength multiplier on BaseFit (1 = as-is)
        public readonly float Probability;        // 0..1 chance/condition the effect actually lands (1 = certain)
        public readonly EffectTiming Timing;
        public readonly int DurationRounds;       // combat rounds the effect persists (0 = permanent / n/a)
        public readonly int CapacityRequirement;  // battle cells the effect needs to fully realise (e.g. a Summon's body count); 1 default
        public readonly EffectStacking Stacking;

        public StrategicEffect(IntendedRole role, float baseFit, StrategicEffectContext context,
            EffectField field, bool coverage,
            System.Func<WorthIt.DefenderProfile, bool> eligiblePredicate = null,
            EffectScope scope = EffectScope.SelfBody, float magnitude = 1f, float probability = 1f,
            EffectTiming timing = EffectTiming.Persistent, int durationRounds = 0,
            int capacityRequirement = 1, EffectStacking stacking = EffectStacking.Stack)
        {
            Role = role;
            BaseFit = baseFit;
            Context = context;
            Field = field;
            CountsAsCoverage = coverage;
            EligiblePredicate = eligiblePredicate;
            Scope = scope;
            Magnitude = magnitude <= 0f ? 1f : magnitude;
            Probability = Mathf.Clamp01(probability <= 0f ? 1f : probability);
            Timing = timing;
            DurationRounds = durationRounds;
            CapacityRequirement = capacityRequirement < 1 ? 1 : capacityRequirement;
            Stacking = stacking;
        }
    }

    // The distributed strategic value of a card's effects for one role — one term per
    // StrategicUseScoreBreakdown axis, so nothing is double-counted and every axis is reachable.
    internal readonly struct EffectContribution
    {
        public readonly float RoleFit;
        public readonly float ImmediateTempo;
        public readonly float ThreatResponse;
        public readonly float CapabilityGap;
        public readonly float ForceGrowth;
        public readonly float Synergy;

        public EffectContribution(float roleFit, float tempo, float threat, float gap, float grow, float syn)
        {
            RoleFit = roleFit;
            ImmediateTempo = tempo;
            ThreatResponse = threat;
            CapabilityGap = gap;
            ForceGrowth = grow;
            Synergy = syn;
        }
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
    }

    // The world context every contextual scaler reads. Built once per Card x IntendedUse
    // evaluation, ENTIRELY from the captured WorldSnapshot (or a deterministic projection for a
    // synthetic NewArmy / ReusableShell destination) — never live ArmyData, so one Materialization
    // Plan.Score can't mix two world states. DESTINATION-LOCAL where it matters.
    //
    //   RecurringIncomeWeight  0..1, high when the economy is INSECURE (recurring income worth more)
    //   LocalEnemyArmies       enemy armies within effectTargetDensityRadius of the deploy hex — the
    //                          AoE / TargetDensity signal (0 with no plan/hex, never a global count)
    //   ProjectedLine          the body's PROJECTED stat line (card + attached equipment) — the same
    //                          line readiness / role derivation use; ExpectedSustain reads HitPoints
    //   DestArmyMembers        snapshot member profiles already in the destination army (empty for a
    //                          fresh NewArmy / empty ReusableShell) — an aura's EligiblePredicate
    //                          counts the ones IT benefits
    //   FreeBattleSlots        battle cells free in the destination AFTER the plan's own primary body
    //                          takes its slot — the capacity a Summon would ACTUALLY have. Projected
    //                          for NewArmy / ReusableShell (nominal capacity − primary body), never
    //                          the pre-materialization count. -1 only when the dest army is unknown.
    internal readonly struct EffectEvaluationContext
    {
        public readonly WorldSnapshot Snap;
        public readonly MaterializationPlan Plan;
        public readonly float RecurringIncomeWeight;
        public readonly int LocalEnemyArmies;
        // final closure §3.1 — expected AFFECTED ENEMY BODIES (sum of enemy unit counts) within the
        // AoE radius of the deploy hex. This, not the army count, is the AoE-value driver.
        public readonly int LocalEnemyBodies;
        // final closure §3.2 — proxy for how many combat rounds a fight at the deploy would last
        // (>= 1). Drives how many regen ticks a Regenerate effect would actually get to use.
        public readonly float ExpectedCombatRounds;
        public readonly AiPower.ProjectedStrategicLine ProjectedLine;
        public readonly IReadOnlyList<WorthIt.DefenderProfile> DestArmyMembers;
        public readonly int FreeBattleSlots;
        // final closure §3.3 (army -> candidate direction) — the value the DEST ARMY's already-
        // present auras add specifically to THIS incoming candidate's projected profile. The
        // candidate does not carry the aura ability itself, so this cannot come from Resolve(card);
        // it is folded into EffectContribution.Synergy once, for every role.
        public readonly float IncomingAuraSynergy;

        public float ProjectedHitPoints => ProjectedLine.HitPoints;

        public EffectEvaluationContext(WorldSnapshot snap, MaterializationPlan plan)
        {
            Snap = snap;
            Plan = plan;
            RecurringIncomeWeight = snap?.Economy != null
                ? 1f - Mathf.Clamp01(snap.Economy.EconomicSecurity)
                : 0.5f;

            // The projected END RESULT — base def + already-attached equipment + plan equipment.
            ProjectedLine = AiPower.ProjectMaterialization(plan);

            LocalEnemyArmies = 0;
            LocalEnemyBodies = 0;
            float enemyPowerNear = 0f;
            IReadOnlyList<ArmySnapshot> enemies = snap?.TrueWorld?.EnemyArmies;
            if (enemies != null && plan != null)
            {
                HexCoord at = plan.Deploy.Hex;
                foreach (ArmySnapshot e in enemies)
                {
                    if (e == null
                        || HexGridMath.Distance(e.Hex, at) > AiConfigV2.effectTargetDensityRadius)
                        continue;
                    LocalEnemyArmies++;
                    LocalEnemyBodies += System.Math.Max(1, e.OccupiedBattleSlots);
                    enemyPowerNear += e.EffectiveArmyPower;
                }
            }

            float ownPower = Mathf.Max(1f, ProjectedLine.BasePower);
            ExpectedCombatRounds = enemyPowerNear <= 0f
                ? 1f
                : Mathf.Clamp(
                    1f + enemyPowerNear / (ownPower * Mathf.Max(0.01f, AiConfigV2.effectCombatRoundsPowerRatio)),
                    1f, AiConfigV2.effectCombatRoundsMax);

            ResolveDestination(snap, plan, out FreeBattleSlots, out DestArmyMembers,
                out ArmySnapshot destArmy);

            IncomingAuraSynergy = ComputeIncomingAuraSynergy(plan, destArmy);
        }

        // §3.3 army -> candidate — each aura already standing in the dest army that the incoming
        // candidate's projected profile satisfies contributes its MARGINAL value for one more
        // eligible body (BaseFit / auraNorm, scaled by that aura's own Magnitude × Probability).
        private static float ComputeIncomingAuraSynergy(MaterializationPlan plan, ArmySnapshot destArmy)
        {
            if (destArmy?.AllyAuraEffects == null || destArmy.AllyAuraEffects.Count == 0)
                return 0f;
            CardDefinition primary = plan?.BaseCardInHand?.Definition ?? plan?.GeneratedBaseDef;
            if (primary == null)
                return 0f;
            WorthIt.DefenderProfile candidate = AiPower.ToDefenderProfile(primary);
            float perBody = 1f / Mathf.Max(1f, AiConfigV2.effectAuraAllyNorm);
            float total = 0f;
            foreach (StrategicEffect aura in destArmy.AllyAuraEffects)
                if (aura.EligiblePredicate == null || aura.EligiblePredicate(candidate))
                    total += aura.BaseFit * aura.Magnitude * aura.Probability * perBody;
            return total;
        }

        // Snapshot-only. For a real recipient army (ExistingArmy / Garrison) the occupancy comes
        // from the captured ArmySnapshot by id; for a synthetic NewArmy / ReusableShell the capacity
        // is projected from the deployment rules (hero primary -> its CommandRating, else the field/
        // garrison base). Always minus 1 for the plan's own primary body.
        private static void ResolveDestination(WorldSnapshot snap, MaterializationPlan plan,
            out int freeSlots, out IReadOnlyList<WorthIt.DefenderProfile> members,
            out ArmySnapshot destArmy)
        {
            members = System.Array.Empty<WorthIt.DefenderProfile>();
            freeSlots = -1;
            destArmy = null;
            if (plan == null)
                return;

            // The plan's own primary body (real card in hand or a generated base def). Its hero /
            // CommandRating handling is entirely inside CardPlayExecutor.ProjectedCapacityAfterDeploy.
            CardDefinition primary = plan.BaseCardInHand?.Definition ?? plan.GeneratedBaseDef;
            const int primaryBodySlots = 1;

            switch (plan.Deploy.Kind)
            {
                case DeploymentKind.ExistingArmy:
                case DeploymentKind.Garrison:
                {
                    int wantId = plan.Deploy.Army != null ? plan.Deploy.Army.Id : -1;
                    ArmySnapshot a = null;
                    if (snap?.Self?.Armies != null)
                        foreach (ArmySnapshot s in snap.Self.Armies)
                            if (s != null && s.ArmyId == wantId) { a = s; break; }
                    if (a == null)
                        return;                       // stale plan — leave -1
                    destArmy = a;
                    members = a.Members ?? (IReadOnlyList<WorthIt.DefenderProfile>)members;
                    // Mirror CardPlayExecutor / ArmyActions: a hero rewrites capacity to its
                    // CommandRating ONLY as the FIRST hero — a second hero is appended after the
                    // existing commander and does NOT raise capacity (no auto TryReorderCommander).
                    int cap = CardPlayExecutor.ProjectedCapacityAfterDeploy(
                        a.Capacity, a.HasHero, primary);
                    freeSlots = System.Math.Max(0, cap - a.OccupiedBattleSlots - primaryBodySlots);
                    return;
                }
                case DeploymentKind.NewArmy:
                case DeploymentKind.ReusableShell:
                {
                    // Nominal capacity of the (heroless, empty) destination base: a ReusableShell's
                    // own snapshot capacity when present, else the freshly-created field-army value.
                    int nominalCap = ArmyData.ComputeCapacity(
                        System.Array.Empty<UnitData>(), isGarrison: false); // heroless field base
                    if (plan.Deploy.Kind == DeploymentKind.ReusableShell && plan.Deploy.Army != null
                        && snap?.Self?.Armies != null)
                        foreach (ArmySnapshot s in snap.Self.Armies)
                            if (s != null && s.ArmyId == plan.Deploy.Army.Id) { nominalCap = s.Capacity; break; }

                    // Same canonical rule as a real recipient: a hero primary sets capacity to its
                    // CommandRating (first hero into an empty base), a non-hero keeps nominalCap —
                    // NOT Math.Max(heroCr, nominalCap), which is where phantom slots came from.
                    int cap = CardPlayExecutor.ProjectedCapacityAfterDeploy(
                        nominalCap, targetHasHero: false, primary);
                    freeSlots = System.Math.Max(0, cap - primaryBodySlots);
                    return;
                }
            }
        }

        public int CountEligibleAllies(System.Func<WorthIt.DefenderProfile, bool> predicate)
        {
            int n = 0;
            foreach (WorthIt.DefenderProfile m in DestArmyMembers)
                if (predicate == null || predicate(m))
                    n++;
            return n;
        }
    }

    internal static class StrategicEffectRegistry
    {
        // The ONLY ability-name table for strategic scoring. One row per mechanic. BaseFit for
        // AntiAir/AntiArmor is threatResponseValueWeight so ThreatResponse-field parity with the old
        // ThreatResponseValue holds.
        private static readonly Dictionary<string, StrategicEffect[]> ByAbility =
            new Dictionary<string, StrategicEffect[]>
            {
                [UnitAbilities.AntiAir] = new[]
                {
                    new StrategicEffect(IntendedRole.AntiAir, AiConfigV2.threatResponseValueWeight,
                        StrategicEffectContext.EnemyThreatScaled, EffectField.ThreatResponse, coverage: true),
                },
                [UnitAbilities.Hyperkinetic] = new[]
                {
                    new StrategicEffect(IntendedRole.AntiArmor, AiConfigV2.threatResponseValueWeight,
                        StrategicEffectContext.EnemyThreatScaled, EffectField.ThreatResponse, coverage: true),
                },
                [UnitAbilities.ApBonus] = new[]
                {
                    // final closure §4 — a recurring-AP effect yields TWO explicit contributions so
                    // nothing is double-counted (the old hidden flat `recurringAp` add in
                    // ScoreSurplusRole is gone): a long-term Support role-fit value AND an
                    // immediate-tempo value, both scaled by economy insecurity, both reaching Phase A
                    // and Phase B identically through the registry.
                    new StrategicEffect(IntendedRole.Support, AiConfigV2.surplusRecurringApIncomeBonus,
                        StrategicEffectContext.RecurringResource, EffectField.RoleFit, coverage: true),
                    new StrategicEffect(IntendedRole.Support, AiConfigV2.surplusRecurringApTempoBonus,
                        StrategicEffectContext.RecurringResource, EffectField.ImmediateTempo, coverage: false),
                },
                [UnitAbilities.Researcher] = new[]
                {
                    new StrategicEffect(IntendedRole.Support, AiConfigV2.heroSupportFitValue,
                        StrategicEffectContext.Flat, EffectField.RoleFit, coverage: true),
                },
                [UnitAbilities.Assembler] = new[]
                {
                    new StrategicEffect(IntendedRole.Support, AiConfigV2.heroSupportFitValue,
                        StrategicEffectContext.Flat, EffectField.RoleFit, coverage: true),
                },
                // Future mechanics are ONE row each — no evaluator / StrategicManager / Phase-A/B
                // edit (final closure §3.5 acceptance). The generic semantics ride on the descriptor:
                //   [UnitAbilities.Splash]     = { new StrategicEffect(IntendedRole.CombatBody, w,
                //         StrategicEffectContext.TargetDensity, EffectField.RoleFit, coverage:false,
                //         scope: EffectScope.EnemiesNearDeploy, magnitude: 0.5f, probability: 0.9f) },
                //   [UnitAbilities.Regenerate] = { new StrategicEffect(IntendedRole.CombatBody, w,
                //         StrategicEffectContext.ExpectedSustain, EffectField.RoleFit, coverage:false,
                //         timing: EffectTiming.DuringCombat, durationRounds: 99) },
                //   [UnitAbilities.ArmoredAura]= { new StrategicEffect(IntendedRole.Support, w,
                //         StrategicEffectContext.EligibleAllies, EffectField.Synergy, coverage:true,
                //         p => p.TypeTags != null && p.TypeTags.Contains(UnitTypeTag.Armored),
                //         scope: EffectScope.DestArmy) },   // also drives IncomingAuraSynergy the other way
                //   [UnitAbilities.Summon]     = { new StrategicEffect(IntendedRole.ForceGrowth, w,
                //         StrategicEffectContext.FreeBattleSlots, EffectField.ForceGrowth, coverage:false,
                //         timing: EffectTiming.OneShot, durationRounds: 2, capacityRequirement: 3,
                //         stacking: EffectStacking.Unique) },   // coverage:false => never becomes standing readiness
            };

        // Every strategic effect a card's EFFECTIVE ability set + stat line yields.
        public static List<StrategicEffect> Resolve(IEnumerable<string> effectiveAbilities, int effectiveMoveMax)
        {
            var list = new List<StrategicEffect>();
            if (effectiveAbilities != null)
                foreach (string a in effectiveAbilities)
                    if (a != null && ByAbility.TryGetValue(a, out StrategicEffect[] eff))
                        list.AddRange(eff);

            // Stat-derived: a fast non-recce body is a mobile-combat option (matches
            // StrategicCardEvaluator.DeriveRoles' old inline moveMax check).
            if (!AbilityParams.AbilitiesHaveAnyRecce(effectiveAbilities)
                && effectiveMoveMax >= AiConfigV2.mobileCombatMoveMax)
                list.Add(new StrategicEffect(IntendedRole.MobileCombat, AiConfigV2.effectMobileBaseFit,
                    StrategicEffectContext.Flat, EffectField.RoleFit, coverage: true));

            return list;
        }

        // Distinct strategic roles the effects enable (feeds DeriveRoles).
        public static IEnumerable<IntendedRole> Roles(IEnumerable<string> effectiveAbilities, int effectiveMoveMax)
            => Resolve(effectiveAbilities, effectiveMoveMax).Select(e => e.Role).Distinct();

        // ALL of a card's effect value for `role`, distributed by breakdown axis. This is how a new
        // mechanic reaches CombatBody / ForceGrowth / … scoring, not just RoleFit — the evaluator
        // adds each field to the matching bd.* term exactly once.
        public static EffectContribution Contributions(IntendedRole role,
            IEnumerable<string> effectiveAbilities, int effectiveMoveMax, in EffectEvaluationContext ctx)
        {
            float roleFit = 0f, tempo = 0f, threat = 0f, gap = 0f, grow = 0f, syn = 0f;
            foreach (StrategicEffect e in Resolve(effectiveAbilities, effectiveMoveMax))
            {
                if (e.Role != role) continue;
                float v = ContextualValue(e, ctx);
                switch (e.Field)
                {
                    case EffectField.RoleFit: roleFit += v; break;
                    case EffectField.ImmediateTempo: tempo += v; break;
                    case EffectField.ThreatResponse: threat += v; break;
                    case EffectField.CapabilityGap: gap += v; break;
                    case EffectField.ForceGrowth: grow += v; break;
                    case EffectField.Synergy: syn += v; break;
                }
            }

            // §3.3 (army -> candidate) — value the DEST ARMY's standing auras add to this incoming
            // body. Not one of the candidate's own effects, so it is added here (not role-filtered)
            // — the same value on every competing role candidate; only one is ever executed.
            syn += ctx.IncomingAuraSynergy;

            return new EffectContribution(roleFit, tempo, threat, gap, grow, syn);
        }

        // The roles this ability/stat set already COVERS for standing-force readiness.
        public static RoleCoverage CoverageOf(IEnumerable<string> abilities, int moveMax)
        {
            RoleCoverage c = RoleCoverage.None;
            foreach (StrategicEffect e in Resolve(abilities, moveMax))
                if (e.CountsAsCoverage)
                    c = c.With(e.Role);
            return c;
        }

        // Does the card carry ANY effect using the given context (e.g. RecurringResource -> the
        // "recurring-AP income" immediate-tempo bonus, without naming ApBonus).
        public static bool HasContext(IEnumerable<string> abilities, int moveMax, StrategicEffectContext context)
            => Resolve(abilities, moveMax).Any(e => e.Context == context);

        public static float ContextualValue(in StrategicEffect e, in EffectEvaluationContext ctx)
        {
            // final closure §3 — every effect's realised value is scaled by its declared strength
            // and by the chance/condition it actually lands. Neutral (1 × 1) for every existing row.
            float magP = e.Magnitude * e.Probability;

            switch (e.Context)
            {
                case StrategicEffectContext.Flat:
                    return e.BaseFit * magP;
                case StrategicEffectContext.RecurringResource:
                    return e.BaseFit * magP
                        * Mathf.Lerp(AiConfigV2.effectRecurringFloor, 1f, ctx.RecurringIncomeWeight);
                case StrategicEffectContext.EnemyThreatScaled:
                    return e.BaseFit * magP * EnemyThreatModel.CounterDemandFactor(e.Role, ctx.Snap);
                case StrategicEffectContext.TargetDensity:
                {
                    // §3.1 — AoE value ≈ BaseFit × (expected affected ENEMY BODIES / norm) × magP.
                    // Body count (unit density), NOT army count: 1 army of 6 units outscores 1 of 1.
                    float coverage = Mathf.Clamp01(
                        ctx.LocalEnemyBodies / Mathf.Max(1f, AiConfigV2.effectAoeBodiesNorm));
                    return e.BaseFit * magP * coverage;
                }
                case StrategicEffectContext.ExpectedSustain:
                {
                    // §3.2 — regen value ≈ BaseFit × (expected combat DURATION / norm) × HP factor ×
                    // magP. Duration is the primary driver: the same HP body is worth more regen in a
                    // drawn-out fight than a one-round exchange.
                    float duration = Mathf.Clamp01(
                        ctx.ExpectedCombatRounds / Mathf.Max(1f, AiConfigV2.effectSustainRoundsNorm));
                    float hp = Mathf.Clamp01(
                        ctx.ProjectedHitPoints / Mathf.Max(1f, AiConfigV2.effectSustainHpNorm));
                    return e.BaseFit * magP * duration * Mathf.Lerp(0.5f, 1f, hp);
                }
                case StrategicEffectContext.EligibleAllies:
                    // §3.3 (candidate -> army) — only the allies THIS aura benefits (its predicate).
                    return e.BaseFit * magP * Mathf.Clamp01(
                        ctx.CountEligibleAllies(e.EligiblePredicate)
                        / Mathf.Max(1f, AiConfigV2.effectAuraAllyNorm));
                case StrategicEffectContext.FreeBattleSlots:
                {
                    // §3.4 — a Summon needs CapacityRequirement free battle cells to fully realise.
                    // Value scales with the fraction it can actually field: 0 slots -> 0, and a
                    // 3-body summon with 1 free slot gets 1/3, never the full BaseFit.
                    if (ctx.FreeBattleSlots <= 0)
                        return 0f;
                    float usable = Mathf.Min(ctx.FreeBattleSlots, e.CapacityRequirement)
                        / (float)Mathf.Max(1, e.CapacityRequirement);
                    return e.BaseFit * magP * usable;
                }
                default:
                    return e.BaseFit * magP;
            }
        }
    }

    // ===========================================================================================
    //  ENEMY THREAT MODEL — the counterpart of StrategicEffectRegistry for "what the enemy fields".
    //  Cheat-biased DIRECTIONAL signal off omniscient TrueWorld composition; never becomes normal
    //  AI intel. A new enemy threat type = one branch here, no evaluator edit.
    // ===========================================================================================
    internal static class EnemyThreatModel
    {
        // Magnitude in [0..1] (after norm) of the threat a given COUNTER role answers.
        public static float CounterDemandFactor(IntendedRole counterRole, WorldSnapshot snap)
        {
            float power = ThreatPower(counterRole, snap);
            return power <= 0f
                ? 0f
                : Mathf.Clamp(power / Mathf.Max(1f, AiConfigV2.threatResponseNorm), 0f, 1f);
        }

        // Is the triggering threat present at all (Hold NearTermExpectedDemand / capability-gap gates).
        public static bool ThreatPresent(IntendedRole counterRole, WorldSnapshot snap)
            => ThreatPower(counterRole, snap) > 0f;

        private static float ThreatPower(IntendedRole counterRole, WorldSnapshot snap)
        {
            IReadOnlyList<ArmySnapshot> enemies = snap?.TrueWorld?.EnemyArmies;
            if (enemies == null || enemies.Count == 0)
                return 0f;
            float p = 0f;
            foreach (ArmySnapshot a in enemies)
            {
                if (a == null) continue;
                switch (counterRole)
                {
                    case IntendedRole.AntiAir:
                        if (a.IsAir) p += a.EffectiveArmyPower;
                        break;
                    case IntendedRole.AntiArmor:
                        if (HasArmoredMember(a)) p += a.EffectiveArmyPower;
                        break;
                }
            }
            return p;
        }

        private static bool HasArmoredMember(ArmySnapshot a)
        {
            if (a?.Members == null) return false;
            foreach (WorthIt.DefenderProfile m in a.Members)
                if (m.TypeTags != null && m.TypeTags.Contains(UnitTypeTag.Armored))
                    return true;
            return false;
        }
    }
}
