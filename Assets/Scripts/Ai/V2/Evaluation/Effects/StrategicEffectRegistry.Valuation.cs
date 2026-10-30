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
    // Contextual value of a resolved effect: timing, global recurring yield, attachment stats and
    // the per-context scalers (ContextualValue). The ability table itself is in StrategicEffectRegistry.cs.
    internal static partial class StrategicEffectRegistry
    {
        // Timing discount: a DuringCombat effect (regen, combat aura) is worth its full value only
        // where a fight is actually expected at the deploy; a OneShot / Persistent effect is not
        // combat-gated. Neutral (1) for every existing row (all Persistent).
        private static float TimingFactor(in StrategicEffect e, in EffectEvaluationContext ctx)
            => e.Timing == EffectTiming.DuringCombat && ctx.ExpectedCombatRounds <= 1f
                ? AiConfigV2.effectNoCombatTimingFloor
                : 1f;

        // ===================================================================================
        //  AI-MGR — DYNAMIC GLOBAL RECURRING-RESOURCE VALUE  (the ApBonus production case)
        // ===================================================================================
        //  Answers "how much ADDITIONAL strategic opportunity does +yield of this global resource
        //  every turn create in the CURRENT game state?", not "the ability is present, +X".
        //
        //      value = perUnitTurnValue x yield x (horizon x futureOpportunity)
        //              x marginalResourceUtility        (can the AI actually spend it?  §6)
        //              x carrierPersistence              (how durably the source stays in play)
        //              x saturationFactor                (base + existing + candidate vs usable  §8)
        //
        //  Generation risk (will the carrier card ever materialise?) is NOT owned here — it lives
        //  once in StrategicUseScoreBreakdown.GenerationRiskDiscount. This registry answers "how good is the
        //  effect IF the card is materialised".
        //
        //  Carrier-agnostic: identical on Hero / Unit / Base / Facility / generated card. ONE
        //  contribution — the caller adds it to exactly one EffectField. `detail` is the AiDebug
        //  decomposition (§15). A future +Energy/turn or +draw/N-turns effect is another
        //  GlobalResourceKind branch here, never a new evaluator.
        private static float GlobalRecurringValue(in StrategicEffect e, in EffectEvaluationContext ctx,
            ref string detail)
        {
            if (e.GlobalYieldPerTurn <= 0f)
                return 0f;

            if (e.GlobalResource != GlobalResourceKind.ActionPoints)
                return GlobalRecurringStockpileValue(e, ctx, ref detail);

            ApActionEconomySnapshot ape = ctx.ApEconomy;
            float yield = e.GlobalYieldPerTurn;
            float future = ctx.RecurringFutureOpportunity;
            float horizonTurns = AiConfigV2.effectRecurringHorizonTurns * future;

            float marginal = Mathf.Lerp(AiConfigV2.apMarginalUtilFloor, 1f, ctx.EffectiveMarginalApUtility);
            float persistence = Mathf.Lerp(AiConfigV2.effectRecurringRealisationFloor, 1f,
                Mathf.Clamp01(CarrierDurabilityOf(ctx)));
            float saturation = SaturationFactor(ape, ctx.EffectiveUsefulApDemand, yield);

            float raw = AiConfigV2.effectGlobalRecurringApPerTurnValue * yield * horizonTurns;
            float value = Mathf.Min(AiConfigV2.effectGlobalRecurringValueCap,
                raw * marginal * persistence * saturation);

            string line =
                $"effect=ApBonus scope=PlayerGlobal yield=+{yield:0.#}AP/turn "
                + $"horizon={AiConfigV2.effectRecurringHorizonTurns}x future={future:0.00} "
                + $"apMarginalUtility={marginal:0.00} existingRecurringSources={(ape?.RecurringApSources ?? 0)} "
                + $"saturation={saturation:0.00} persistence={persistence:0.00} effectValue={value:0.00}";
            detail = string.IsNullOrEmpty(detail) ? line : detail + " ; " + line;
            return value;
        }

        private static float GlobalRecurringStockpileValue(in StrategicEffect e,
            in EffectEvaluationContext ctx, ref string detail)
        {
            if (!TryStockpileResource(e.GlobalResource, out ResourceType type)
                || ctx.Snap?.Economy?.PerType == null)
                return 0f;

            EconomyResourceStanding? current = null;
            foreach (EconomyResourceStanding standing in ctx.Snap.Economy.PerType)
            {
                if (standing.Type == type)
                {
                    current = standing;
                    break;
                }
            }
            if (!current.HasValue)
                return 0f;

            EconomyResourceStanding before = current.Value;
            EconomyResourceStanding after = EconomyStanding.CalculateResource(
                type,
                before.OwnIncome + e.GlobalYieldPerTurn,
                before.OpponentMedianIncome,
                before.HandResourceNeed,
                before.RemainingDeckResourceNeed,
                before.ReservedOperationalNeed,
                before.SpendableStockpile,
                before.StarvationPressure,
                before.ForecastOperationalNeed);

            float relativeRelief = before.DeficitScore <= AiConfigV2.allocatorSliceEpsilon
                ? 0f
                : Mathf.Clamp01((before.DeficitScore - after.DeficitScore)
                    / before.DeficitScore);
            float urgency = Mathf.Clamp01(Mathf.Max(before.DeficitScore, relativeRelief));
            float marginal = Mathf.Lerp(AiConfigV2.effectStockpileMarginalUtilFloor, 1f, urgency);
            float persistence = Mathf.Lerp(AiConfigV2.effectRecurringRealisationFloor, 1f,
                Mathf.Clamp01(CarrierDurabilityOf(ctx)));
            float future = ctx.RecurringFutureOpportunity;
            float value = Mathf.Min(AiConfigV2.effectGlobalRecurringValueCap,
                AiConfigV2.effectProduceResourceFit * e.GlobalYieldPerTurn
                * future * marginal * persistence);

            string line =
                $"effect=Produce{type} scope=PlayerGlobal yield=+{e.GlobalYieldPerTurn:0.#}/turn "
                + $"deficit={before.DeficitScore:0.00}->{after.DeficitScore:0.00} "
                + $"marginalUtility={marginal:0.00} future={future:0.00} "
                + $"persistence={persistence:0.00} effectValue={value:0.00}";
            detail = string.IsNullOrEmpty(detail) ? line : detail + " ; " + line;
            return value;
        }

        private static bool TryStockpileResource(GlobalResourceKind kind, out ResourceType type)
        {
            switch (kind)
            {
                case GlobalResourceKind.Human: type = ResourceType.Human; return true;
                case GlobalResourceKind.Energy: type = ResourceType.Energy; return true;
                case GlobalResourceKind.Materials: type = ResourceType.Materials; return true;
                case GlobalResourceKind.Tech: type = ResourceType.Tech; return true;
                default: type = default; return false;
            }
        }

        // How durably a recurring source stays in play once fielded: infrastructure (Base /
        // Facility / plan-less non-combat) is the most certain, a Hero less so, a Unit body least.
        private static float CarrierDurabilityOf(in EffectEvaluationContext ctx)
        {
            CardDefinition def = ctx.Plan?.BaseCardInHand?.Definition ?? ctx.Plan?.GeneratedBaseDef;
            if (def == null) return 1f;
            if (def.cardType == CardType.Unit) return AiConfigV2.effectRecurringCarrierDurabilityUnit;
            if (def.cardType == CardType.Hero) return AiConfigV2.effectRecurringCarrierDurabilityHero;
            return 1f;
        }

        // §8 — diminishing marginal utility. Adding this source's yield on top of the AP the AI
        // ALREADY has: if that leaves idle AP (headroom above what it can usefully spend) the extra
        // yield is worth progressively less; and each recurring source ALREADY in play discounts the
        // next one geometrically. 1 (no discount) when there is no ApEconomy read. `usefulApDemand`
        // is the owner-aggregated figure resolved once on EffectEvaluationContext.
        private static float SaturationFactor(ApActionEconomySnapshot ape, float usefulApDemand, float yield)
        {
            if (ape == null) return 1f;
            float projectedAvail = ape.BaseActionPoints + yield;
            float headroom = projectedAvail - usefulApDemand;
            float f = headroom <= 0f
                ? 1f
                : Mathf.Clamp01(1f - headroom / Mathf.Max(1f, yield * 2f));
            f *= Mathf.Pow(AiConfigV2.effectRecurringSourceDiminish, Mathf.Max(0, ape.RecurringApSources));
            return Mathf.Clamp01(f);
        }

        // Marginal attachment consumers subtract this value before/after. Existing descriptor
        // semantics remain the source of effect knowledge; primary damage is already projected
        // by the canonical exchange and must never be added again as a role bonus.
        internal static float AttachmentValue(IReadOnlyDictionary<EquipmentStat, int> stats,
            IReadOnlyList<string> abilities, IReadOnlyList<UnitTypeTag> tags,
            IReadOnlyList<WorthIt.DefendingArmy> opposition, ArmyData army, bool hero, WorldSnapshot snap,
            bool includeGlobal = true, MaterializationPlan deployment = null,
            IReadOnlyList<WorthIt.DefenderProfile> referenceTargets = null, int hpSpent = 0,
            AttachmentTargetSet prepared = null, UnitData host = null)
        {
            var ctx = new EffectEvaluationContext(snap, deployment);
            float total = 0;
            // The opposition-derived selections depend on neither stats nor abilities; a caller
            // pricing many pairs against one opposition passes them prepared once.
            AttachmentTargetSet set = prepared ?? new AttachmentTargetSet(opposition, referenceTargets);
            List<WorthIt.DefenderProfile> targets = set.Targets;
            var effects = Resolve(abilities?.Distinct(), 0).Where(e => !e.PrimaryExchange
                && !(e.Context == StrategicEffectContext.Flat && e.StackingKey == DevelopmentOperatorSkillKey)
                && (!hero || e.Scope == EffectScope.PlayerGlobal || e.Scope == EffectScope.DestArmy)
                && (includeGlobal || e.Scope != EffectScope.PlayerGlobal)).ToList();
            foreach (var group in effects.Select((e, index) => (e, index)).GroupBy(x => x.e.StackingKey ?? "effect:" + x.index))
            {
                StrategicEffect e = group.First().e;
                float value;
                if (e.Context == StrategicEffectContext.GlobalRecurringResource)
                {
                    string detail = null;
                    value = GlobalRecurringValue(e, ctx, ref detail);
                }
                else if (e.Context == StrategicEffectContext.TargetDensity)
                {
                    // Use WorthIt's aggregate-roster adjacency approximation, not map positions.
                    // Exact neighbours and friendly fire remain with tactical targeting/gameplay.
                    // A primary must land meaningful damage and secondary slots must remain.
                    value = 0;
                    int battles = 0;
                    foreach (var bodies in set.Battles)
                    {
                        float battleValue = 0;
                        foreach (var target in bodies)
                        {
                            BattleSimulationKernel.ExpectedExchangeDamage(stats[EquipmentStat.Attack],
                                Mathf.RoundToInt(target.Defense), abilities, target.TypeTags, target.Abilities,
                                Mathf.CeilToInt(target.HitPoints), out float hit);
                            if (target.TypeTags.Contains(UnitTypeTag.Aircraft) && !abilities.Contains(UnitAbilities.AntiAir))
                                hit = 0;
                            if (e.SecondaryTargets > 0)
                            {
                                int available = bodies.Count - 1;
                                int prior = effects.Where(x => x.SecondaryTargets > 0
                                    && x.SecondaryPriority < e.SecondaryPriority).Sum(x => x.SecondaryTargets);
                                if (available <= prior || hit <= 0) continue;
                                float secondaryDamage = 0;
                                bool skippedPrimary = false;
                                foreach (var secondary in bodies)
                                {
                                    if (!skippedPrimary && secondary.Equals(target))
                                    { skippedPrimary = true; continue; }
                                    if (e.EligiblePredicate != null && !e.EligiblePredicate(secondary)) continue;
                                    secondaryDamage += BattleSimulationKernel.ExpectedExchangeDamage(
                                        stats[EquipmentStat.Attack], Mathf.RoundToInt(target.Defense),
                                        abilities, target.TypeTags, target.Abilities,
                                        Mathf.CeilToInt(secondary.HitPoints), out _, true, secondary.Abilities)
                                        / Mathf.Max(2f, secondary.HitPoints);
                                }
                                battleValue += e.BaseFit * e.Probability * secondaryDamage
                                    * Mathf.Min(e.SecondaryTargets, available - prior) / available;
                            }
                            else
                            {
                                int eligible = bodies.Count(t => e.EligiblePredicate == null || e.EligiblePredicate(t));
                                if (e.EligiblePredicate == null || e.EligiblePredicate(target)) eligible--;
                                battleValue += e.BaseFit * e.Magnitude * e.Probability
                                    * Mathf.Clamp01(eligible / Mathf.Max(1f, AiConfigV2.effectAoeBodiesNorm))
                                    * (e.RequiresHit ? hit : 1f);
                            }
                        }
                        value += battleValue / bodies.Count;
                        battles++;
                    }
                    value /= Mathf.Max(1, battles);
                }
                else if (e.Context == StrategicEffectContext.AllyHealing)
                {
                    int eligible = army != null
                        ? army.Members.Count(u => u != null && u.HitPointsCurrent > 0 && u.HitPointsMax > 1
                            && u.TypeTags.Contains(UnitTypeTag.Bio))
                        : ctx.CountEligibleAllies(e.EligiblePredicate);
                    if (army == null && tags != null && tags.Contains(UnitTypeTag.Bio)
                        && stats[EquipmentStat.HitPoints] > 1) eligible++;
                    value = eligible > 0 ? e.BaseFit * Mathf.Max(1f, ctx.ExpectedCombatRounds) : 0f;
                }
                else if (e.Context == StrategicEffectContext.TransportMobility)
                {
                    if (army?.Members != null)
                    {
                        var baseline = army.Members.Where(u => u != host).ToList();
                        // A pure replacement projection retains every other member's native
                        // speed and support; it never changes the real army's links.
                        UnitData candidate = UnitData.CreateProjection(host?.RuntimeId ?? -1);
                        candidate.MoveMax = stats[EquipmentStat.MoveMax];
                        candidate.MoveCurrent = candidate.MoveMax;
                        candidate.HitPointsCurrent = candidate.HitPointsMax = stats[EquipmentStat.HitPoints];
                        candidate.Abilities.UnionWith(abilities);
                        if (tags != null) candidate.TypeTags.UnionWith(tags);
                        candidate.Owner = army.Owner;
                        candidate.IsHero = hero;
                        baseline.Add(candidate);
                        value = e.BaseFit * Mathf.Max(0, ArmyData.ComputeMaxMovement(baseline)
                            - Mathf.Min(army.MaxMovement, candidate.PermanentMoveMax));
                    }
                    else value = e.BaseFit * ctx.TransportMovementGain;
                }
                else if (e.Context == StrategicEffectContext.MobileRepair)
                {
                    int? replacing = UnitRepair.IsRepairSourceMember(host) ? army?.Id : null;
                    value = host?.IsAviation == true ? 0f : e.BaseFit * (army != null
                        ? EffectEvaluationContext.RepairServiceAt(snap, army.Hex, replacing)
                        : ctx.MobileRepairService);
                }
                else if (e.Context == StrategicEffectContext.ExpectedSustain)
                {
                    // Gameplay heals ONE HP at end of turn, only while alive and damaged.
                    // P(lethal) = E[min(D,hp)] - E[min(D,hp-1)]; no second battle model.
                    int currentHp = Mathf.Max(0, stats[EquipmentStat.HitPoints] - hpSpent);
                    bool wounded = currentHp < stats[EquipmentStat.HitPoints];
                    value = 0;
                    if (currentHp > 0)
                    {
                        float recovery = targets.Count == 0 ? (wounded ? 1f : 0f) : 0f;
                        foreach (var target in targets)
                        {
                            float full = BattleSimulationKernel.ExpectedExchangeDamage(Mathf.RoundToInt(target.Attack),
                                stats[EquipmentStat.Defense], target.Abilities, tags, abilities, currentHp, out float hit);
                            float below = BattleSimulationKernel.ExpectedExchangeDamage(Mathf.RoundToInt(target.Attack),
                                stats[EquipmentStat.Defense], target.Abilities, tags, abilities, currentHp - 1, out _);
                            float lethal = Mathf.Clamp01(full - below);
                            recovery += Mathf.Max(0, (wounded ? 1f : hit) - lethal);
                        }
                        value = e.BaseFit * e.Magnitude * e.Probability
                            * (targets.Count > 0 ? recovery / targets.Count : recovery);
                    }
                }
                else if (e.Context == StrategicEffectContext.FreeBattleSlots)
                    value = e.BaseFit * e.Magnitude * e.Probability * Mathf.Clamp01(
                        (deployment != null ? ctx.FreeBattleSlots
                            : army == null ? 0 : Mathf.Max(0, army.Capacity - army.Members.Count))
                        / (float)e.CapacityRequirement);
                else if (e.Context == StrategicEffectContext.EligibleAllies)
                    value = e.BaseFit * e.Magnitude * e.Probability * Mathf.Clamp01(
                        (deployment != null ? ctx.CountEligibleAllies(e.EligiblePredicate)
                            : army?.Members?.Count(u => u != null
                                && (e.EligiblePredicate == null || e.EligiblePredicate(WorthIt.FromLiveUnit(u)))) ?? 0)
                        / Mathf.Max(1f, AiConfigV2.effectAuraAllyNorm));
                else
                    value = ContextualValue(e, ctx);
                total += EffectEvaluationContext.StackedTotal(e.Stacking, value, group.Count());
            }
            return total;
        }

        // The ordinal-sorted, capped target and battle selections AttachmentValue reads from an
        // opposition. Targets are built eagerly; the TargetDensity battles only when first read.
        internal sealed class AttachmentTargetSet
        {
            private readonly IReadOnlyList<WorthIt.DefendingArmy> _opposition;
            private List<List<WorthIt.DefenderProfile>> _battles;
            public readonly List<WorthIt.DefenderProfile> Targets;

            public AttachmentTargetSet(IReadOnlyList<WorthIt.DefendingArmy> opposition,
                IReadOnlyList<WorthIt.DefenderProfile> referenceTargets)
            {
                _opposition = opposition;
                Targets = opposition.SelectMany(a => a.Units).Where(p => !p.IsHero)
                    .OrderBy(AttachmentProfileKey, System.StringComparer.Ordinal).Take(16).ToList();
                if (Targets.Count == 0 && referenceTargets != null) Targets.AddRange(referenceTargets.Take(16));
            }

            // Non-empty ground bodies of the first 16 battles, in the original order.
            public List<List<WorthIt.DefenderProfile>> Battles
            {
                get
                {
                    if (_battles != null) return _battles;
                    _battles = new List<List<WorthIt.DefenderProfile>>();
                    foreach (var battle in _opposition.OrderBy(a => string.Join(";", a.Units.Select(AttachmentProfileKey)
                        .OrderBy(k => k, System.StringComparer.Ordinal)), System.StringComparer.Ordinal).Take(16))
                    {
                        var bodies = battle.Units.Where(p => !p.IsHero && p.IsGroundCombatant
                            && !p.TypeTags.Contains(UnitTypeTag.Aircraft))
                            .OrderBy(AttachmentProfileKey, System.StringComparer.Ordinal).Take(16).ToList();
                        if (bodies.Count > 0) _battles.Add(bodies);
                    }
                    return _battles;
                }
            }
        }

        private static string AttachmentProfileKey(WorthIt.DefenderProfile p) =>
            p.Defense.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + ":"
            + p.Attack.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + ":"
            + p.HitPoints.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + ":"
            + string.Join(",", p.TypeTags.OrderBy(t => t)) + ":"
            + string.Join(",", p.Abilities.OrderBy(a => a, System.StringComparer.Ordinal));

        public static float ContextualValue(in StrategicEffect e, in EffectEvaluationContext ctx)
        {
            // final closure §3 — every effect's realised value is scaled by its declared strength,
            // the chance/condition it lands, and its timing. Neutral (1 × 1 × 1) for existing rows.
            float magP = e.Magnitude * e.Probability * TimingFactor(e, ctx);

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
                    // §3.1 — AoE value ≈ BaseFit × (expected affected BODIES / norm) × magP. Scope
                    // selects the population: EnemiesNearDeploy -> KNOWN enemy unit density (a splash
                    // attack); DestArmy -> friendly bodies in the dest army (a buff nova);
                    // SelfBody -> just this body.
                    int affected;
                    switch (e.Scope)
                    {
                        case EffectScope.DestArmy:
                            affected = ctx.CountEligibleAllies(e.EligiblePredicate);
                            break;
                        case EffectScope.SelfBody:
                            affected = 1;
                            break;
                        default:
                            // EnemiesNearDeploy — honour the TargetFilter (Armored splash counts
                            // Armored known bodies only), not the raw member total.
                            affected = ctx.CountEligibleEnemies(e.EligiblePredicate);
                            break;
                    }
                    float coverage = Mathf.Clamp01(
                        affected / Mathf.Max(1f, AiConfigV2.effectAoeBodiesNorm));
                    return e.BaseFit * magP * coverage;
                }
                case StrategicEffectContext.AllyHealing:
                    return ctx.CountEligibleAllies(e.EligiblePredicate) > 0
                        || ctx.ProjectedIsBio && ctx.ProjectedHitPoints > 1
                        ? e.BaseFit * Mathf.Max(1f, ctx.ExpectedCombatRounds) : 0f;
                case StrategicEffectContext.TransportMobility:
                    return e.BaseFit * ctx.TransportMovementGain;
                case StrategicEffectContext.MobileAirfield:
                    return e.BaseFit * ctx.MobileAirfieldService;
                case StrategicEffectContext.MobileRepair:
                    return e.BaseFit * ctx.MobileRepairService;
                case StrategicEffectContext.BuildingDisruption:
                    if (DisruptionRules.HasCarrier(ctx.DestArmyMembers)) return 0f;
                    var observer = ctx.Snap?.Observer;
                    float structureDefense = (ctx.Snap?.Known?.Buildings
                        ?? System.Array.Empty<AiMapMemory.KnownBuilding>())
                        .Where(b => b.IsBase && b.Owner != null && b.Owner != observer)
                        .Select(b => Mathf.Max(0f, b.Defense)).DefaultIfEmpty(0f).Max();
                    return e.BaseFit * magP * structureDefense;
                case StrategicEffectContext.ExpectedSustain:
                {
                    // §3.2 — regen value ≈ BaseFit × (usable rounds / norm) × HP factor × magP.
                    // Usable rounds = min(expected combat duration, the effect's own DurationRounds)
                    // — a 1-round regen in a 5-round fight only gets one round of value; DurationRounds
                    // 0 means "permanent" (no cap).
                    float usableRounds = e.DurationRounds > 0
                        ? Mathf.Min(ctx.ExpectedCombatRounds, e.DurationRounds)
                        : ctx.ExpectedCombatRounds;
                    float duration = Mathf.Clamp01(
                        usableRounds / Mathf.Max(1f, AiConfigV2.effectSustainRoundsNorm));
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
                    // §3.4 — a Summon needs CapacityRequirement free battle cells to fully realise,
                    // AND a temporary summon (DurationRounds > 0) is worth less than a permanent one.
                    // 0 slots -> 0; a 3-body summon with 1 free slot -> 1/3; magP carries the
                    // "expected summon power" via Magnitude.
                    if (ctx.FreeBattleSlots <= 0)
                        return 0f;
                    float usable = Mathf.Min(ctx.FreeBattleSlots, e.CapacityRequirement)
                        / (float)Mathf.Max(1, e.CapacityRequirement);
                    float durationFactor = e.DurationRounds > 0
                        ? Mathf.Clamp01(e.DurationRounds / Mathf.Max(1f, AiConfigV2.effectSummonDurationNorm))
                        : 1f;   // permanent summon
                    return e.BaseFit * magP * usable * durationFactor;
                }
                default:
                    return e.BaseFit * magP;
            }
        }
    }
}
