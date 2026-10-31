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
    // ===========================================================================================
    //  STRATEGIC EFFECT REGISTRY
    // ===========================================================================================
    //  The ONE place "this ability / stat feature is worth X toward strategic role Y" knowledge
    //  lives, so a new mechanic (Splash/AoE, Regeneration, army aura, temporary Summon, …)
    //  influences strategic scoring without a `Contains(UnitAbilities.X)` edit in any evaluator.
    //
    //  Pipeline:  Skill/ability -> StrategicEffect(s) -> Capability/role + Contextual value ->
    //             RoleFit / RoleCoverage / DeriveRoles.
    //
    //  ACCEPTANCE: adding a new skill/effect descriptor must touch ONLY `ByAbility` here (+ its
    //  contextual-value branch if it uses a new context) — never StrategicManager, never the
    //  StrategicCardEvaluator role switch.
    //
    //  Recce/Stealth deliberately stay direct `AbilityParams` helpers (concrete gameplay
    //  capabilities), and the Research/Production "operator vocation" stays
    //  a direct ability check — those are not the strategic-scoring switch this layer replaces.
    // ===========================================================================================

    internal static partial class StrategicEffectRegistry
    {
        // The ONLY ability-name table for strategic scoring. One row per mechanic. BaseFit for
        // AntiAir/AntiArmor is threatResponseValueWeight so ThreatResponse-field parity with the old
        // ThreatResponseValue holds.
        internal const string DevelopmentOperatorSkillKey = "DevelopmentOperatorSkill";

        private static readonly Dictionary<string, StrategicEffect[]> ByAbility =
            new Dictionary<string, StrategicEffect[]>
            {
                [UnitAbilities.AntiAir] = new[]
                {
                    new StrategicEffect(IntendedRole.AntiAir, AiConfigV2.threatResponseValueWeight,
                        StrategicEffectContext.EnemyThreatScaled, EffectField.ThreatResponse, coverage: true, primaryExchange: true),
                },
                [UnitAbilities.Hyperkinetic] = new[]
                {
                    new StrategicEffect(IntendedRole.AntiArmor, AiConfigV2.threatResponseValueWeight,
                        StrategicEffectContext.EnemyThreatScaled, EffectField.ThreatResponse, coverage: true,
                        primaryExchange: true, bodyOnly: true),
                },
                [UnitAbilities.ApBonus] = new[]
                {
                    // AI-MGR — Dynamic Strategic Effect Utility. ApBonus is a PlayerGlobal /
                    // Persistent / RecurringResource effect: it raises the AP available every
                    // following turn, so its value is the DYNAMIC extra strategic opportunity that
                    // creates in the CURRENT game state (yield x horizon x futureOpportunity x
                    // marginalApUtility x carrierPersistence x saturation — see GlobalRecurringValue),
                    // never a flat "+0.75 because the ability is present". Descriptor-driven and
                    // carrier-agnostic: the SAME row prices it on a Hero, Unit, Base, Facility or a
                    // generated card. ONE authoritative contribution (RoleFit); nothing re-adds it
                    // under another field. Role Support + coverage:true keep it in DeriveRoles /
                    // BaselineForceReadiness exactly as before. Diminishing stacking guards the
                    // (unrealistic) double-ApBonus card; multi-SOURCE diminishing is in the scaler.
                    new StrategicEffect(IntendedRole.Support, 0f,
                        StrategicEffectContext.GlobalRecurringResource, EffectField.RoleFit, coverage: true,
                        scope: EffectScope.PlayerGlobal,
                        stacking: EffectStacking.Diminishing, stackingKey: "ApBonusGlobal",
                        globalResource: GlobalResourceKind.ActionPoints,
                        globalYieldPerTurn: UnitAbilities.ApBonusActionPointsPerSource),
                },
                // Researcher / Assembler — the ONE fixed value of the Research/Production operator
                // ability, for a Unit OR a Hero, in the Development role every Development use
                // (Phase A demand, Phase B surplus) scores under. The game tag Support is not its
                // source. Unique + shared key: a hero carrying both abilities is paid once for the
                // same deployment. The evaluator keeps it only while a compatible preparation or
                // facility path exists (StrategicCardEvaluator.HasDevelopmentRolePath).
                [UnitAbilities.Researcher] = new[]
                {
                    new StrategicEffect(IntendedRole.Development, AiConfigV2.developmentOperatorSkillValue,
                        StrategicEffectContext.Flat, EffectField.RoleFit, coverage: false,
                        stacking: EffectStacking.Unique, stackingKey: DevelopmentOperatorSkillKey),
                },
                [UnitAbilities.Assembler] = new[]
                {
                    new StrategicEffect(IntendedRole.Development, AiConfigV2.developmentOperatorSkillValue,
                        StrategicEffectContext.Flat, EffectField.RoleFit, coverage: false,
                        stacking: EffectStacking.Unique, stackingKey: DevelopmentOperatorSkillKey),
                },
                // §3.5 EXTENSIBILITY ACCEPTANCE — CriticalDamage ("successful attack deals x2") was
                // NOT represented in strategic scoring before. Wiring it end-to-end took exactly
                // this ONE row: Resolve() -> Roles() (CombatBody) -> DeriveRoles -> Contributions()
                // -> bd.RoleFit, all through the registry. ZERO edits to StrategicManager, the
                // StrategicCardEvaluator role switch, Phase A, Phase B, or MaterializationCandidate
                // Builder arbitration. It is a permanent unit property (Persistent), and a duplicate
                // copy stacks additively (StackingKey + Stacking.Stack — explicit, not accidental).
                [UnitAbilities.CriticalDamage] = new[]
                {
                    new StrategicEffect(IntendedRole.CombatBody, AiConfigV2.effectCriticalDamageFit,
                        StrategicEffectContext.Flat, EffectField.RoleFit, coverage: false,
                        stacking: EffectStacking.Stack, stackingKey: "CriticalDamage", primaryExchange: true,
                        bodyOnly: true),
                },
                // ---- New skills (2026-09), each ONE row on the existing machinery — the AoE /
                // regen / summon contexts below were already scored by ContextualValue, just never
                // referenced by a real ability row. Additive: an existing card carries none of
                // these tags, so its score is unchanged.
                [UnitAbilities.Splash] = new[]
                {
                    new StrategicEffect(IntendedRole.CombatBody, AiConfigV2.effectSplashFit,
                        StrategicEffectContext.TargetDensity, EffectField.RoleFit, coverage: false,
                        scope: EffectScope.EnemiesNearDeploy, magnitude: 0.5f, probability: 0.9f,
                        stacking: EffectStacking.Unique, stackingKey: "Splash", requiresHit: true, secondaryTargets: 2,
                        bodyOnly: true),
                },
                [UnitAbilities.Scorcher] = new[]
                {
                    new StrategicEffect(IntendedRole.CombatBody, AiConfigV2.effectScorcherFit,
                        StrategicEffectContext.TargetDensity, EffectField.RoleFit, coverage: false,
                        eligiblePredicate: p => p.TypeTags != null && p.TypeTags.Contains(UnitTypeTag.Bio),
                        scope: EffectScope.EnemiesNearDeploy, magnitude: 0.5f, probability: 0.6f,
                        stacking: EffectStacking.Unique, stackingKey: "Scorcher", requiresHit: true, secondaryTargets: 1, secondaryPriority: 1,
                        bodyOnly: true),
                },
                // Pyrokinetic / ShockAttack / Berserk / CeramicArmor: the combat kernel already prices their
                // exchange (primaryExchange keeps them out of AttachmentValue); these rows give the
                // card scoring the same CombatBody fit CriticalDamage has. Pyrokinetic is the Bio
                // counterpart of Hyperkinetic, scaled by the known Bio enemies near the deploy.
                [UnitAbilities.Pyrokinetic] = new[]
                {
                    new StrategicEffect(IntendedRole.CombatBody, AiConfigV2.effectPyrokineticFit,
                        StrategicEffectContext.TargetDensity, EffectField.RoleFit, coverage: false,
                        eligiblePredicate: p => p.TypeTags != null && p.TypeTags.Contains(UnitTypeTag.Bio),
                        scope: EffectScope.EnemiesNearDeploy,
                        stacking: EffectStacking.Unique, stackingKey: "Pyrokinetic", primaryExchange: true,
                        bodyOnly: true),
                },
                [UnitAbilities.ShockAttack] = new[]
                {
                    new StrategicEffect(IntendedRole.CombatBody, AiConfigV2.effectShockAttackFit,
                        StrategicEffectContext.Flat, EffectField.RoleFit, coverage: false,
                        stacking: EffectStacking.Unique, stackingKey: "ShockAttack", primaryExchange: true,
                        bodyOnly: true),
                },
                [UnitAbilities.Berserk] = new[]
                {
                    new StrategicEffect(IntendedRole.CombatBody, AiConfigV2.effectBerserkFit,
                        StrategicEffectContext.Flat, EffectField.RoleFit, coverage: false,
                        stacking: EffectStacking.Unique, stackingKey: "Berserk", primaryExchange: true,
                        bodyOnly: true),
                },
                [UnitAbilities.CeramicArmor] = new[]
                {
                    new StrategicEffect(IntendedRole.CombatBody, AiConfigV2.effectCeramicArmorFit,
                        StrategicEffectContext.Flat, EffectField.RoleFit, coverage: false,
                        stacking: EffectStacking.Unique, stackingKey: "CeramicArmor", primaryExchange: true),
                },
                // Ionic: the Mechanical counterpart of Pyrokinetic, priced by the kernel's exchange
                // (EquipmentEfficiency.Utility) and scaled here by known Mechanical enemies near the deploy.
                [UnitAbilities.Ionic] = new[]
                {
                    new StrategicEffect(IntendedRole.CombatBody, AiConfigV2.effectIonicFit,
                        StrategicEffectContext.TargetDensity, EffectField.RoleFit, coverage: false,
                        eligiblePredicate: p => p.TypeTags != null && p.TypeTags.Contains(UnitTypeTag.Mechanical),
                        scope: EffectScope.EnemiesNearDeploy,
                        stacking: EffectStacking.Unique, stackingKey: "Ionic", primaryExchange: true,
                        bodyOnly: true),
                },
                // Counterattack: the equipment utility's contact series prices the answer
                // (primaryExchange); this row is the CombatBody fit for card scoring.
                [UnitAbilities.Counterattack] = new[]
                {
                    new StrategicEffect(IntendedRole.CombatBody, AiConfigV2.effectCounterattackFit,
                        StrategicEffectContext.Flat, EffectField.RoleFit, coverage: false,
                        stacking: EffectStacking.Unique, stackingKey: "Counterattack", primaryExchange: true,
                        bodyOnly: true),
                },
                // Bodyguard protects OTHER bodies, which the one-host equipment utility cannot see,
                // so it is NOT primaryExchange: this flat fit is also its attachment value.
                [UnitAbilities.Bodyguard] = new[]
                {
                    new StrategicEffect(IntendedRole.CombatBody, AiConfigV2.effectBodyguardFit,
                        StrategicEffectContext.Flat, EffectField.RoleFit, coverage: false,
                        stacking: EffectStacking.Unique, stackingKey: "Bodyguard", bodyOnly: true),
                },
                // Structure suppression is an army support capability, with no generic field
                // power bump. Its value is the known defence of the site the destination's Attack
                // targets (EffectEvaluationContext.DisruptionSiteDefense; a live attachment: its fight).
                [UnitAbilities.Disruption] = new[]
                {
                    new StrategicEffect(IntendedRole.CombatBody, AiConfigV2.effectDrillFit,
                        StrategicEffectContext.BuildingDisruption, EffectField.Synergy, coverage: false,
                        scope: EffectScope.DestArmy, stacking: EffectStacking.Unique,
                        stackingKey: UnitAbilities.Disruption),
                },
                // Drill is an aura over the destination army's fighters (EligibleAllies).
                [UnitAbilities.Drill] = new[]
                {
                    new StrategicEffect(IntendedRole.CombatBody, AiConfigV2.effectDrillFit,
                        StrategicEffectContext.EligibleAllies, EffectField.Synergy, coverage: false,
                        eligiblePredicate: p => p.IsGroundCombatant && !p.IsHero, scope: EffectScope.DestArmy,
                        timing: EffectTiming.DuringCombat, stacking: EffectStacking.Stack, stackingKey: "Drill"),
                },
                [UnitAbilities.Heal] = new[]
                {
                    new StrategicEffect(IntendedRole.CombatBody, AiConfigV2.effectRegenerationFit,
                        StrategicEffectContext.AllyHealing, EffectField.Synergy, coverage: true,
                        eligiblePredicate: p => p.HitPoints > 0 && p.MaxHitPoints > 1
                            && p.TypeTags.Contains(UnitTypeTag.Bio), scope: EffectScope.DestArmy,
                        timing: EffectTiming.DuringCombat, stacking: EffectStacking.Stack, stackingKey: "Heal"),
                },
                [UnitAbilities.Transport] = new[]
                {
                    new StrategicEffect(IntendedRole.MobileCombat, AiConfigV2.effectRegenerationFit,
                        StrategicEffectContext.TransportMobility, EffectField.Synergy, coverage: true,
                        eligiblePredicate: p => p.TypeTags.Contains(UnitTypeTag.Infantry),
                        scope: EffectScope.DestArmy, stacking: EffectStacking.Stack, stackingKey: "Transport"),
                },
                [UnitAbilities.Regeneration] = new[]
                {
                    new StrategicEffect(IntendedRole.CombatBody, AiConfigV2.effectRegenerationFit,
                        StrategicEffectContext.ExpectedSustain, EffectField.RoleFit, coverage: false,
                        timing: EffectTiming.DuringCombat, durationRounds: 0,
                        stacking: EffectStacking.Stack, stackingKey: "Regeneration"),
                },
                [UnitAbilities.Airfield] = new[]
                {
                    new StrategicEffect(IntendedRole.CombatBody, AiConfigV2.effectMobileAirfieldFit,
                        StrategicEffectContext.MobileAirfield, EffectField.Synergy, coverage: false,
                        scope: EffectScope.DestArmy, stacking: EffectStacking.Unique, stackingKey: "Airfield",
                        bodyOnly: true),
                },
                [UnitAbilities.Repair] = new[]
                {
                    new StrategicEffect(IntendedRole.CombatBody, AiConfigV2.effectMobileRepairFit,
                        StrategicEffectContext.MobileRepair, EffectField.Synergy, coverage: false,
                        scope: EffectScope.DestArmy, stacking: EffectStacking.Unique, stackingKey: "Repair"),
                },
                [UnitAbilities.RaiseTheRots] = new[]
                {
                    new StrategicEffect(IntendedRole.ForceGrowth, AiConfigV2.effectRaiseTheRotsFit,
                        StrategicEffectContext.FreeBattleSlots, EffectField.ForceGrowth, coverage: false,
                        magnitude: 0.6f, timing: EffectTiming.OneShot, durationRounds: 0,
                        capacityRequirement: 2,
                        stacking: EffectStacking.Unique, stackingKey: "RaiseTheRots"),
                },
                // Produce{X}: persistent PlayerGlobal income. The same descriptor prices the
                // ability on a Hero, Unit, Base or Facility against the matching frozen economy
                // deficit. IncomeProjection remains the gameplay-income owner; this registry only
                // evaluates the strategic marginal value of that already-defined +1/turn.
                [UnitAbilities.ProduceHuman] = new[]
                {
                    new StrategicEffect(IntendedRole.Economy, 0f,
                        StrategicEffectContext.GlobalRecurringResource, EffectField.RoleFit, coverage: false,
                        scope: EffectScope.PlayerGlobal,
                        stacking: EffectStacking.Stack, stackingKey: "ProduceHuman",
                        globalResource: GlobalResourceKind.Human, globalYieldPerTurn: 1f),
                },
                [UnitAbilities.ProduceEnergy] = new[]
                {
                    new StrategicEffect(IntendedRole.Economy, 0f,
                        StrategicEffectContext.GlobalRecurringResource, EffectField.RoleFit, coverage: false,
                        scope: EffectScope.PlayerGlobal,
                        stacking: EffectStacking.Stack, stackingKey: "ProduceEnergy",
                        globalResource: GlobalResourceKind.Energy, globalYieldPerTurn: 1f),
                },
                [UnitAbilities.ProduceMaterials] = new[]
                {
                    new StrategicEffect(IntendedRole.Economy, 0f,
                        StrategicEffectContext.GlobalRecurringResource, EffectField.RoleFit, coverage: false,
                        scope: EffectScope.PlayerGlobal,
                        stacking: EffectStacking.Stack, stackingKey: "ProduceMaterials",
                        globalResource: GlobalResourceKind.Materials, globalYieldPerTurn: 1f),
                },
                [UnitAbilities.ProduceTech] = new[]
                {
                    new StrategicEffect(IntendedRole.Economy, 0f,
                        StrategicEffectContext.GlobalRecurringResource, EffectField.RoleFit, coverage: false,
                        scope: EffectScope.PlayerGlobal,
                        stacking: EffectStacking.Stack, stackingKey: "ProduceTech",
                        globalResource: GlobalResourceKind.Tech, globalYieldPerTurn: 1f),
                },
                // Further mechanics are ONE row each — no evaluator / StrategicManager / Phase-A/B
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
                //         scope: EffectScope.DestArmy, stackingKey: "ArmoredAura") },  // both aura directions; StackingKey keeps it distinct from a Bio aura
                //   [UnitAbilities.Summon]     = { new StrategicEffect(IntendedRole.ForceGrowth, w,
                //         StrategicEffectContext.FreeBattleSlots, EffectField.ForceGrowth, coverage:false,
                //         timing: EffectTiming.OneShot, durationRounds: 2, capacityRequirement: 3,
                //         stacking: EffectStacking.Unique, stackingKey: "Summon") },   // coverage:false => never standing readiness
            };

        // Every strategic effect a card's EFFECTIVE ability set + stat line yields.
        // A secondary hero may carry army support without commanding it. Housekeeping retains
        // these semantic carriers for the mission's full counterfactual (battle and movement),
        // instead of maintaining a second list of Heal/Drill/Transport/Summon ability names.
        internal static bool HasArmySupportEffect(IEnumerable<string> abilities, int moveMax) =>
            Resolve(abilities, moveMax).Any(e => e.Scope == EffectScope.DestArmy
                || e.Context == StrategicEffectContext.FreeBattleSlots);

        // A live army support carrier (Heal, Drill, Transport, an aura, a summon): never slot
        // waste, kept by every departure, handover and preparation rule.
        internal static bool IsArmySupportCarrier(Game.Units.UnitData unit) =>
            unit != null && HasArmySupportEffect(unit.Abilities, unit.MoveMax);

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
            => Contributions(role, effectiveAbilities, effectiveMoveMax, ctx, out _);

        public static EffectContribution Contributions(IntendedRole role,
            IEnumerable<string> effectiveAbilities, int effectiveMoveMax, in EffectEvaluationContext ctx,
            out string effectDetail)
        {
            effectDetail = null;
            float roleFit = 0f, tempo = 0f, threat = 0f, gap = 0f, grow = 0f, syn = 0f;
            // AI-MGR — PlayerGlobal (target-independent) value, kept SEPARATE per axis so the
            // descriptor's EffectField still decides where it lands. The evaluator adds these AFTER
            // any target multiplier.
            float gRoleFit = 0f, gTempo = 0f, gThreat = 0f, gGap = 0f, gGrow = 0f, gSyn = 0f;

            void Add(EffectField field, float v)
            {
                switch (field)
                {
                    case EffectField.RoleFit: roleFit += v; break;
                    case EffectField.ImmediateTempo: tempo += v; break;
                    case EffectField.ThreatResponse: threat += v; break;
                    case EffectField.CapabilityGap: gap += v; break;
                    case EffectField.ForceGrowth: grow += v; break;
                    case EffectField.Synergy: syn += v; break;
                }
            }

            void AddGlobal(EffectField field, float v)
            {
                switch (field)
                {
                    case EffectField.RoleFit: gRoleFit += v; break;
                    case EffectField.ImmediateTempo: gTempo += v; break;
                    case EffectField.ThreatResponse: gThreat += v; break;
                    case EffectField.CapabilityGap: gGap += v; break;
                    case EffectField.ForceGrowth: gGrow += v; break;
                    case EffectField.Synergy: gSyn += v; break;
                }
            }

            List<StrategicEffect> all = Resolve(effectiveAbilities, effectiveMoveMax);

            // AI-MGR — PlayerGlobal effects are NOT role/placement contributions: they are priced
            // ONCE per card, independent of the IntendedRole being scored (like IncomingAuraSynergy
            // below), so they are handled here and EXCLUDED from the role-filtered pass. Copies that
            // share a StackingKey are reduced together.
            List<StrategicEffect> global = all.Where(e => e.Scope == EffectScope.PlayerGlobal).ToList();
            foreach (StrategicEffect e in global.Where(e => e.StackingKey == null))
                AddGlobal(e.Field, GlobalRecurringValue(e, ctx, ref effectDetail));
            foreach (IGrouping<string, StrategicEffect> g in global
                         .Where(e => e.StackingKey != null).GroupBy(e => e.StackingKey))
            {
                StrategicEffect e = g.First();
                AddGlobal(e.Field, EffectEvaluationContext.StackedTotal(
                    e.Stacking, GlobalRecurringValue(e, ctx, ref effectDetail), g.Count()));
            }

            // Equipment's marginal non-global effects are priced by the shared upgrade
            // projection. Retain the unmodified body's registry contribution, and keep final
            // PlayerGlobal effects above: each mechanic has exactly one owner in this chain.
            List<StrategicEffect> bodyEffects = all;
            if (ctx.Plan?.UsesEquipment == true)
            {
                CardDefinition body = ctx.Plan.BaseCardInHand?.Definition ?? ctx.Plan.GeneratedBaseDef;
                if (body != null)
                {
                    var before = EquipmentSystem.Project(body, ctx.Plan.BaseCardInHand?.Equipment,
                        ctx.Plan.BaseCardInHand?.Mutator);
                    bodyEffects = Resolve(before.Abilities, effectiveMoveMax);
                }
            }
            bool heroCard = ctx.ProjectedIsHero;
            List<StrategicEffect> forRole = bodyEffects
                .Where(e => e.Role == role && e.Scope != EffectScope.PlayerGlobal
                    && !(e.BodyOnly && heroCard)).ToList();

            // An effect with NO StackingKey stands alone — evaluated individually with its OWN
            // context (predicate / scope / duration), never merged with anything (every existing
            // row on a normal card takes this path, byte-identical to before).
            foreach (StrategicEffect e in forRole)
                if (e.StackingKey == null)
                    Add(e.Field, ContextualValue(e, ctx));

            // Copies of the SAME mechanic (matching non-null StackingKey) are reduced together by
            // that mechanic's Stacking policy — a duplicated Unique effect adds one copy, not N.
            foreach (IGrouping<string, StrategicEffect> g in forRole
                         .Where(e => e.StackingKey != null)
                         .GroupBy(e => e.StackingKey))
            {
                StrategicEffect e = g.First();
                Add(e.Field, EffectEvaluationContext.StackedTotal(
                    e.Stacking, ContextualValue(e, ctx), g.Count()));
            }

            // §3.3 (army -> candidate) — value the DEST ARMY's standing auras add to this incoming
            // body. Not one of the candidate's own effects, so it is added here (not role-filtered)
            // — the same value on every competing role candidate; only one is ever executed.
            syn += ctx.IncomingAuraSynergy;

            return new EffectContribution(roleFit, tempo, threat, gap, grow, syn,
                gRoleFit, gTempo, gThreat, gGap, gGrow, gSyn);
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

        // AI-MGR — does this ability set carry ANY PlayerGlobal recurring-resource effect (ApBonus
        // today; a future +Energy/turn, +draw/N-turns, +movement-budget row the same). GENERIC — it
        // never names a concrete ability, so a new such mechanic is picked up with zero edits here.
        // The owner-witnessed AP-workload measurement pass is only worth running when this is true
        // for some reachable carrier; otherwise nothing reads the number and the discounted
        // structural fallback stands.
        public static bool HasGlobalRecurringEffect(IEnumerable<string> effectiveAbilities)
            => Resolve(effectiveAbilities, 0).Any(e =>
                e.Scope == EffectScope.PlayerGlobal
                && e.Context == StrategicEffectContext.GlobalRecurringResource);

        // Any card among `defs` whose granted abilities carry a PlayerGlobal recurring-resource
        // effect — the cheap "is a witnessed AP-workload pass worth it this turn" gate over a
        // hand + deck (deck covers a card a Challenge could still mint).
        public static bool AnyGlobalRecurringCarrier(IEnumerable<CardDefinition> defs)
            => defs != null && defs.Any(d => d != null && HasGlobalRecurringEffect(d.grantedAbilities));
    }
}
