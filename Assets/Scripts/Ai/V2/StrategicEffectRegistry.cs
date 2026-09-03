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
        // review-r4 P1 ARCH follow-up — for the EligibleAllies context: which allies THIS effect
        // benefits (an Armored aura vs a Ranged aura vs a generic "+X to all"). null => any member.
        // Kept on the descriptor so a new aura mechanic supplies its own predicate, not a
        // registry-wide "generic allies" count. Operates on the SNAPSHOT member profile.
        public readonly System.Func<WorthIt.DefenderProfile, bool> EligiblePredicate;

        public StrategicEffect(IntendedRole role, float baseFit, StrategicEffectContext context,
            EffectField field, bool coverage,
            System.Func<WorthIt.DefenderProfile, bool> eligiblePredicate = null)
        {
            Role = role;
            BaseFit = baseFit;
            Context = context;
            Field = field;
            CountsAsCoverage = coverage;
            EligiblePredicate = eligiblePredicate;
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
        public readonly AiPower.ProjectedStrategicLine ProjectedLine;
        public readonly IReadOnlyList<WorthIt.DefenderProfile> DestArmyMembers;
        public readonly int FreeBattleSlots;

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
            IReadOnlyList<ArmySnapshot> enemies = snap?.TrueWorld?.EnemyArmies;
            if (enemies != null && plan != null)
            {
                HexCoord at = plan.Deploy.Hex;
                foreach (ArmySnapshot e in enemies)
                    if (e != null
                        && HexGridMath.Distance(e.Hex, at) <= AiConfigV2.effectTargetDensityRadius)
                        LocalEnemyArmies++;
            }

            ResolveDestination(snap, plan, out FreeBattleSlots, out DestArmyMembers);
        }

        // Snapshot-only. For a real recipient army (ExistingArmy / Garrison) the occupancy comes
        // from the captured ArmySnapshot by id; for a synthetic NewArmy / ReusableShell the capacity
        // is projected from the deployment rules (hero primary -> its CommandRating, else the field/
        // garrison base). Always minus 1 for the plan's own primary body.
        private static void ResolveDestination(WorldSnapshot snap, MaterializationPlan plan,
            out int freeSlots, out IReadOnlyList<WorthIt.DefenderProfile> members)
        {
            members = System.Array.Empty<WorthIt.DefenderProfile>();
            freeSlots = -1;
            if (plan == null)
                return;

            CardDefinition primary = plan.BaseCardInHand?.Definition ?? plan.GeneratedBaseDef;
            bool primaryIsHero = primary != null && primary.cardType == CardType.Hero;
            int primaryHeroCr = primaryIsHero ? primary.commandRating : 0;
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
                    members = a.Members ?? (IReadOnlyList<WorthIt.DefenderProfile>)members;
                    int cap = System.Math.Max(a.Capacity, primaryHeroCr); // a hero primary can raise it
                    freeSlots = System.Math.Max(0, cap - a.OccupiedBattleSlots - primaryBodySlots);
                    return;
                }
                case DeploymentKind.NewArmy:
                case DeploymentKind.ReusableShell:
                {
                    // A ReusableShell is an existing empty army; if its snapshot is present use its
                    // (0-member) capacity, else fall back to the field-army nominal.
                    int shellCap = -1;
                    if (plan.Deploy.Kind == DeploymentKind.ReusableShell && plan.Deploy.Army != null
                        && snap?.Self?.Armies != null)
                        foreach (ArmySnapshot s in snap.Self.Armies)
                            if (s != null && s.ArmyId == plan.Deploy.Army.Id) { shellCap = s.Capacity; break; }

                    int nominalField = ArmyData.ComputeCapacity(
                        System.Array.Empty<UnitData>(), isGarrison: false); // heroless field base
                    int cap = System.Math.Max(
                        primaryIsHero ? primaryHeroCr : nominalField,
                        shellCap);
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
                // Future mechanics are ONE row each — no evaluator edit. An aura supplies its own
                // snapshot-profile eligibility predicate; a generic "+X to all" omits it:
                //   [UnitAbilities.Splash]       = { (CombatBody,  w, TargetDensity,   RoleFit,     false) },
                //   [UnitAbilities.Regenerate]   = { (CombatBody,  w, ExpectedSustain, RoleFit,     false) },
                //   [UnitAbilities.ArmoredAura]  = { (Support, w, EligibleAllies, Synergy, true,
                //                                     p => p.TypeTags != null && p.TypeTags.Contains(UnitTypeTag.Armored)) },
                //   [UnitAbilities.Summon]       = { (ForceGrowth, w, FreeBattleSlots, ForceGrowth, false) },
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
                    // DESTINATION-LOCAL: enemy armies around the deploy hex, not a global count.
                    return e.BaseFit * Mathf.Clamp01(
                        ctx.LocalEnemyArmies / Mathf.Max(1f, AiConfigV2.effectTargetDensityNorm));
                case StrategicEffectContext.ExpectedSustain:
                    // PROJECTED HP (card + attached equipment), the same line readiness uses.
                    return e.BaseFit * Mathf.Clamp01(
                        ctx.ProjectedHitPoints / Mathf.Max(1f, AiConfigV2.effectSustainHpNorm));
                case StrategicEffectContext.EligibleAllies:
                    // Only the allies THIS effect benefits (its own EligiblePredicate).
                    return e.BaseFit * Mathf.Clamp01(
                        ctx.CountEligibleAllies(e.EligiblePredicate)
                        / Mathf.Max(1f, AiConfigV2.effectAuraAllyNorm));
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
