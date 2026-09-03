using System.Collections.Generic;
using System.Linq;
using Game.Cards;
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
        TargetDensity,      // AoE   — BaseFit scaled by density of likely targets near the deploy
        ExpectedSustain,    // regen — BaseFit scaled by the body's projected survivability
        EligibleAllies,     // aura  — BaseFit scaled by count of eligible friendly units in the dest army
        FreeBattleSlots,    // summon— BaseFit ONLY when real free battle slots exist (no phantom capacity)
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

        public StrategicEffect(IntendedRole role, float baseFit, StrategicEffectContext context,
            EffectField field, bool coverage)
        {
            Role = role;
            BaseFit = baseFit;
            Context = context;
            Field = field;
            CountsAsCoverage = coverage;
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

    // The world context every contextual scaler reads. Built once per Card x Role evaluation from
    // whatever the caller has; a field left at its "unknown" sentinel keeps that context
    // conservative (FreeBattleSlots stays 0 -> no phantom Summon capacity).
    //
    //   RecurringIncomeWeight  0..1, high when the economy is INSECURE (recurring income worth more)
    //   EnemyContactCount      known + cheat enemy armies — TargetDensity proxy
    //   ProjectedHitPoints     the body's projected HP (card + attached equipment) — ExpectedSustain
    //   EligibleAllyCount      non-hero members already in the destination army — EligibleAllies aura
    //   FreeBattleSlots        real/predicted free battle cells; -1 = unknown -> Summon scores 0
    internal readonly struct EffectEvaluationContext
    {
        public readonly WorldSnapshot Snap;
        public readonly MaterializationPlan Plan;
        public readonly float RecurringIncomeWeight;
        public readonly int EnemyContactCount;
        public readonly float ProjectedHitPoints;
        public readonly int EligibleAllyCount;
        public readonly int FreeBattleSlots;

        public EffectEvaluationContext(WorldSnapshot snap, MaterializationPlan plan)
        {
            Snap = snap;
            Plan = plan;
            RecurringIncomeWeight = snap?.Economy != null
                ? 1f - Mathf.Clamp01(snap.Economy.EconomicSecurity)
                : 0.5f;
            EnemyContactCount = snap?.TrueWorld?.EnemyArmies?.Count ?? 0;

            CardDefinition baseDef = plan?.BaseCardInHand?.Definition ?? plan?.GeneratedBaseDef;
            ProjectedHitPoints = baseDef?.hitPoints ?? 0f;

            ArmyData dest = plan != null && plan.Deploy.Kind == DeploymentKind.ExistingArmy
                ? plan.Deploy.Army : null;
            EligibleAllyCount = dest?.Members != null
                ? dest.Members.Count(m => m != null && !m.IsHero)
                : 0;

            FreeBattleSlots = -1;   // no real battle-cell data threaded yet
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
                    new StrategicEffect(IntendedRole.Support, AiConfigV2.surplusRecurringApIncomeBonus,
                        StrategicEffectContext.RecurringResource, EffectField.RoleFit, coverage: true),
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
                // Future mechanics are ONE row each — no evaluator edit:
                //   [UnitAbilities.Splash]      = { (CombatBody,  w, TargetDensity,   RoleFit,        false) },
                //   [UnitAbilities.Regenerate]  = { (CombatBody,  w, ExpectedSustain, RoleFit,        false) },
                //   [UnitAbilities.CommandAura] = { (Support,     w, EligibleAllies,  Synergy,        true ) },
                //   [UnitAbilities.Summon]      = { (ForceGrowth, w, FreeBattleSlots, ForceGrowth,    false) },
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
            switch (e.Context)
            {
                case StrategicEffectContext.Flat:
                    return e.BaseFit;
                case StrategicEffectContext.RecurringResource:
                    return e.BaseFit * Mathf.Lerp(AiConfigV2.effectRecurringFloor, 1f, ctx.RecurringIncomeWeight);
                case StrategicEffectContext.EnemyThreatScaled:
                    return e.BaseFit * EnemyThreatModel.CounterDemandFactor(e.Role, ctx.Snap);
                case StrategicEffectContext.TargetDensity:
                    return e.BaseFit * Mathf.Clamp01(
                        ctx.EnemyContactCount / Mathf.Max(1f, AiConfigV2.effectTargetDensityNorm));
                case StrategicEffectContext.ExpectedSustain:
                    return e.BaseFit * Mathf.Clamp01(
                        ctx.ProjectedHitPoints / Mathf.Max(1f, AiConfigV2.effectSustainHpNorm));
                case StrategicEffectContext.EligibleAllies:
                    return e.BaseFit * Mathf.Clamp01(
                        ctx.EligibleAllyCount / Mathf.Max(1f, AiConfigV2.effectAuraAllyNorm));
                case StrategicEffectContext.FreeBattleSlots:
                    // No phantom Summon capacity: BaseFit only when real free battle cells exist.
                    return ctx.FreeBattleSlots >= 1 ? e.BaseFit : 0f;
                default:
                    return e.BaseFit;
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
