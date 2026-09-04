using System.Collections.Generic;
using System.Linq;
using Game.Cards;
using Game.Units;
using UnityEngine;

using Game.Combat;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  AI POWER  (Strategy V2 strength model)
    // ===========================================================================================
    //  A purpose-built combat-power scalar to replace V1's flat WorthIt.AttackSum + DefenseSum.
    //  It is a RANKING number — "how much army is this", comparable across own/enemy forces and
    //  against the theoretical potentials below. It is deliberately NOT a battle prediction:
    //  whether a specific fight is winnable still goes through WorthIt's Monte Carlo
    //  (ThreatModel.AttackWinChance). Two separate tools, on purpose.
    //
    //  UnitPower(u)     = (atk*wA + def*wD + hp*wHP + init*wINI + res*wRES [+ fate*wFATE]) * abilityMult
    //  ArmyPower        = Σ UnitPower over members (heroes INCLUDED here, unlike WorthIt)
    //  EffectiveArmyPower = ArmyPower * (compoFloor + (1-compoFloor) * CompositionQuality)
    // ===========================================================================================
    public static class AiPower
    {
        // The stat-and-tag essence of one combatant, from either a live UnitData or a not-yet-
        // played CardDefinition — every aggregate below works on these so the two sources share
        // one code path.
        public readonly struct PowerUnit
        {
            public readonly float BasePower;              // stat line * ability multiplier, no composition
            public readonly IReadOnlyList<UnitTypeTag> Tags;
            public readonly int Range;
            public readonly bool IsHero;

            public PowerUnit(float basePower, IReadOnlyList<UnitTypeTag> tags, int range, bool isHero)
            {
                BasePower = basePower;
                Tags = tags ?? System.Array.Empty<UnitTypeTag>();
                Range = range;
                IsHero = isHero;
            }
        }

        // ---- per-unit -----------------------------------------------------------------------

        public static PowerUnit ToPowerUnit(UnitData u)
        {
            float line = u.Attack * AiConfigV2.powerAttackWeight
                       + u.Defense * AiConfigV2.powerDefenseWeight
                       + u.HitPointsCurrent * AiConfigV2.powerHitPointsWeight
                       + u.Initiative * AiConfigV2.powerInitiativeWeight
                       + u.Resistance * AiConfigV2.powerResistanceWeight;
            if (u.IsHero)
                line += u.Fate * AiConfigV2.powerHeroFateWeight;
            float p = Mathf.Max(0f, line) * AbilityMultiplier(u.Abilities);
            return new PowerUnit(p, u.TypeTags.ToList(), u.Range, u.IsHero);
        }

        public static PowerUnit ToPowerUnit(CardDefinition c)
        {
            float line = c.attack * AiConfigV2.powerAttackWeight
                       + c.defenseRating * AiConfigV2.powerDefenseWeight
                       + c.hitPoints * AiConfigV2.powerHitPointsWeight
                       + c.initiative * AiConfigV2.powerInitiativeWeight
                       + c.resistanceRating * AiConfigV2.powerResistanceWeight;
            bool isHero = c.cardType == CardType.Hero;
            if (isHero)
                line += c.fate * AiConfigV2.powerHeroFateWeight;
            float p = Mathf.Max(0f, line) * AbilityMultiplier(c.grantedAbilities);
            return new PowerUnit(p, c.unitTypeTags, c.range, isHero);
        }

        // review-r4 (AI-MGR-01 finding 8.2 / P1 ARCH) — the ONE projected stat line for a not-yet-
        // played CardDefinition with an ALREADY-ATTACHED equipment grant folded in at the STATS
        // level (EquipmentSystem.Predict), not just its abilities. Used by readiness, role
        // derivation, RoleFit and the effect-context model so planning and execution score the SAME
        // entity — an Attack/Defense/HP trinket over the readiness floor, a +MoveMax item over the
        // mobile threshold, a +HP item feeding a Regeneration effect's sustain value.
        public readonly struct ProjectedStrategicLine
        {
            public readonly float BasePower;
            public readonly int Attack, Defense, Resistance, Range, HitPoints, MoveMax, Initiative,
                CommandRating, Fate, ActivationApCost;
            public readonly IReadOnlyList<string> EffectiveAbilities;

            public ProjectedStrategicLine(float basePower, int attack, int defense, int resistance,
                int range, int hitPoints, int moveMax, int initiative, int commandRating, int fate,
                int activationApCost, IReadOnlyList<string> effectiveAbilities)
            {
                BasePower = basePower;
                Attack = attack; Defense = defense; Resistance = resistance; Range = range;
                HitPoints = hitPoints; MoveMax = moveMax; Initiative = initiative;
                CommandRating = commandRating; Fate = fate; ActivationApCost = activationApCost;
                EffectiveAbilities = effectiveAbilities ?? System.Array.Empty<string>();
            }
        }

        // Projected stat line of `c` with zero or more equipment grants applied IN ORDER (nulls
        // skipped). Multiple grants compose — e.g. equipment ALREADY attached to a hand card plus
        // equipment a plan attaches now — so planning never scores a different entity than
        // execution will materialize.
        public static ProjectedStrategicLine EffectiveLine(CardDefinition c, params EquipmentGrant[] grants)
        {
            if (c == null)
                return new ProjectedStrategicLine(0f, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, null);

            var stats = new Dictionary<EquipmentStat, int>
            {
                [EquipmentStat.Attack] = c.attack,
                [EquipmentStat.Defense] = c.defenseRating,
                [EquipmentStat.Resistance] = c.resistanceRating,
                [EquipmentStat.Range] = c.range,
                [EquipmentStat.HitPoints] = c.hitPoints,
                [EquipmentStat.MoveMax] = c.moveMax,
                [EquipmentStat.Initiative] = c.initiative,
                [EquipmentStat.ActivationApCost] = c.activationApCost,
                [EquipmentStat.CommandRating] = c.commandRating,
                [EquipmentStat.Fate] = c.fate,
            };
            IReadOnlyList<string> abilities = c.grantedAbilities != null
                ? new List<string>(c.grantedAbilities) : new List<string>();

            bool anyGrant = false;
            if (grants != null)
                foreach (EquipmentGrant g in grants)
                {
                    if (g == null) continue;
                    anyGrant = true;
                    PredictedEquipmentState pred = EquipmentSystem.Predict(g, stats, abilities);
                    if (pred.Stats != null)
                        foreach (KeyValuePair<EquipmentStat, int> kv in pred.Stats)
                            stats[kv.Key] = kv.Value;
                    abilities = pred.Abilities != null
                        ? new List<string>(pred.Abilities)
                        : EquipmentSystem.EffectiveAbilities(new List<string>(abilities), g);
                }

            int S(EquipmentStat st) => stats.TryGetValue(st, out int v) ? v : 0;
            float line = S(EquipmentStat.Attack) * AiConfigV2.powerAttackWeight
                       + S(EquipmentStat.Defense) * AiConfigV2.powerDefenseWeight
                       + S(EquipmentStat.HitPoints) * AiConfigV2.powerHitPointsWeight
                       + S(EquipmentStat.Initiative) * AiConfigV2.powerInitiativeWeight
                       + S(EquipmentStat.Resistance) * AiConfigV2.powerResistanceWeight;
            if (c.cardType == CardType.Hero)
                line += S(EquipmentStat.Fate) * AiConfigV2.powerHeroFateWeight;

            // No grants: keep ToPowerUnit as the canonical basePower (avoids any drift from the
            // stat-block recompute above).
            float basePower = anyGrant
                ? Mathf.Max(0f, line) * AbilityMultiplier(abilities)
                : ToPowerUnit(c).BasePower;

            return new ProjectedStrategicLine(basePower,
                S(EquipmentStat.Attack), S(EquipmentStat.Defense), S(EquipmentStat.Resistance),
                S(EquipmentStat.Range), S(EquipmentStat.HitPoints), S(EquipmentStat.MoveMax),
                S(EquipmentStat.Initiative), S(EquipmentStat.CommandRating), S(EquipmentStat.Fate),
                S(EquipmentStat.ActivationApCost), abilities);
        }

        // review-r4 P1 ARCH — the ONE authoritative projection of a MaterializationPlan's END RESULT:
        // base def + equipment ALREADY attached to the hand card + equipment the plan attaches now.
        // RoleFit / DeriveRoles / EffectContext / readiness all read this so they score the exact
        // physical entity execution will produce (previously the effect context saw only the plan's
        // NEW equipment and missed BaseCardInHand.Equipment entirely).
        public static ProjectedStrategicLine ProjectMaterialization(MaterializationPlan plan)
        {
            CardDefinition baseDef = plan?.BaseCardInHand?.Definition ?? plan?.GeneratedBaseDef;
            EquipmentGrant already = plan?.BaseCardInHand?.Equipment?.equipment;
            EquipmentGrant planned = plan?.GeneratedEquipmentDef?.equipment
                                     ?? plan?.EquipmentInHand?.Definition?.equipment;
            return EffectiveLine(baseDef, already, planned);
        }

        public static float UnitPower(UnitData u) => ToPowerUnit(u).BasePower;

        // A not-yet-played military CardDefinition as the same per-combatant snapshot WorthIt's
        // Monte Carlo consumes — the card-side counterpart of WorthIt.FromLiveUnit, so the
        // CombatOpportunityAnalyzer can fold hand cards into an assemblable roster and run the
        // SAME CanDamageAll / WinChance a real forming army would. Uses the card's printed stat
        // line (a fresh, undamaged unit) — HitPoints = hitPoints, not a current value.
        public static WorthIt.DefenderProfile ToDefenderProfile(CardDefinition c) =>
            new WorthIt.DefenderProfile(
                c.defenseRating,
                c.grantedAbilities != null && c.grantedAbilities.Contains(UnitAbilities.CeramicArmor),
                c.unitTypeTags,
                c.attack,
                c.hitPoints,
                c.initiative);

        // Power from a WorthIt.DefenderProfile roster — the only stat line available for a
        // remembered / fog-read enemy (no Range on a profile, so composition uses type coverage
        // and hero-count only, not front/reach balance). Used for enemy contacts in the
        // ThreatModel, where a full UnitData is never in hand.
        public static float EffectiveArmyPowerFromProfiles(IReadOnlyList<WorthIt.DefenderProfile> profiles)
        {
            if (profiles == null || profiles.Count == 0)
                return 0f;
            var pus = new List<PowerUnit>(profiles.Count);
            foreach (WorthIt.DefenderProfile p in profiles)
            {
                float line = p.Attack * AiConfigV2.powerAttackWeight
                           + p.Defense * AiConfigV2.powerDefenseWeight
                           + p.HitPoints * AiConfigV2.powerHitPointsWeight
                           + p.Initiative * AiConfigV2.powerInitiativeWeight;
                if (p.HasCeramicArmor)
                    line *= 1f + AiConfigV2.powerBumpCeramicArmor;
                pus.Add(new PowerUnit(Mathf.Max(0f, line), p.TypeTags, 1, false));
            }
            return EffectiveArmyPower(pus);
        }

        private static float AbilityMultiplier(IEnumerable<string> abilities)
        {
            if (abilities == null)
                return 1f;
            float bump = 0f;
            foreach (string a in abilities)
            {
                switch (a)
                {
                    case UnitAbilities.CeramicArmor: bump += AiConfigV2.powerBumpCeramicArmor; break;
                    case UnitAbilities.ShockAttack: bump += AiConfigV2.powerBumpShockAttack; break;
                    case UnitAbilities.CriticalDamage: bump += AiConfigV2.powerBumpCriticalDamage; break;
                    case UnitAbilities.Hyperkinetic:
                    case UnitAbilities.Pyrokinetic: bump += AiConfigV2.powerBumpSituationalCounter; break;
                }
            }
            return 1f + bump;
        }

        // ---- composition ------------------------------------------------------------------

        // [0..1] — how well-rounded a roster is: distinct type tags present, a front/reach mix,
        // and a hero. A lone unit or an all-one-type stack scores low (but never 0 — see
        // EffectiveArmyPower's compoFloor).
        public static float CompositionQuality(IReadOnlyCollection<PowerUnit> units)
        {
            if (units == null || units.Count == 0)
                return 0f;

            var distinctTags = new HashSet<UnitTypeTag>();
            bool hasFront = false, hasReach = false, hasHero = false;
            foreach (PowerUnit pu in units)
            {
                foreach (UnitTypeTag t in pu.Tags)
                    if (t != UnitTypeTag.Hero)
                        distinctTags.Add(t);
                if (pu.IsHero) hasHero = true;
                if (pu.Range <= 1) hasFront = true;
                else hasReach = true;
            }

            float typeCoverage = Mathf.Clamp01(distinctTags.Count / (float)Mathf.Max(1, AiConfigV2.compoTypeCoverageTarget));
            float rangeBalance = (hasFront && hasReach) ? 1f : (hasFront || hasReach) ? 0.5f : 0f;
            float heroPresent = hasHero ? 1f : 0f;

            float wSum = AiConfigV2.compoWeightTypeCoverage + AiConfigV2.compoWeightRangeBalance
                       + AiConfigV2.compoWeightHeroPresent;
            if (wSum < 0.0001f)
                return 0f;
            return (AiConfigV2.compoWeightTypeCoverage * typeCoverage
                  + AiConfigV2.compoWeightRangeBalance * rangeBalance
                  + AiConfigV2.compoWeightHeroPresent * heroPresent) / wSum;
        }

        public static float EffectiveArmyPower(IReadOnlyCollection<PowerUnit> units)
        {
            if (units == null || units.Count == 0)
                return 0f;
            float raw = units.Sum(u => u.BasePower);
            float q = CompositionQuality(units);
            return raw * (AiConfigV2.compoFloor + (1f - AiConfigV2.compoFloor) * q);
        }

        public static float EffectiveArmyPower(IEnumerable<UnitData> members)
        {
            List<PowerUnit> pus = members?.Select(ToPowerUnit).ToList();
            return pus == null || pus.Count == 0 ? 0f : EffectiveArmyPower(pus);
        }

        public static float CompositionQualityOf(IEnumerable<UnitData> members)
        {
            List<PowerUnit> pus = members?.Select(ToPowerUnit).ToList();
            return pus == null || pus.Count == 0 ? 0f : CompositionQuality(pus);
        }

        // ---- potentials ------------------------------------------------------------------

        // Composition-aware greedy stack build. Repeatedly adds whichever remaining candidate
        // maximises the resulting EffectiveArmyPower — and EffectiveArmyPower already folds in the
        // composition multiplier, so an all-one-type stack naturally pulls in a different type /
        // skill once that bump beats the raw-power delta of yet another same-type unit. One hero
        // max; `cap` slots total. Not a true knapsack (that candidate loop is per-slot greedy),
        // but it is an informational comparison scalar, not a battle plan. O(cap^2 * n), run once
        // per AI turn.
        public static List<PowerUnit> ComposeStack(IReadOnlyList<PowerUnit> pool, int cap)
        {
            var pick = new List<PowerUnit>();
            if (pool == null || pool.Count == 0)
                return pick;
            cap = Mathf.Max(1, cap);

            var remaining = new List<PowerUnit>(pool);
            bool heroTaken = false;
            while (pick.Count < cap && remaining.Count > 0)
            {
                int bestIdx = -1;
                float bestScore = float.NegativeInfinity;
                for (int i = 0; i < remaining.Count; i++)
                {
                    if (remaining[i].IsHero && heroTaken)
                        continue;
                    pick.Add(remaining[i]);
                    float score = EffectiveArmyPower(pick);
                    pick.RemoveAt(pick.Count - 1);
                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestIdx = i;
                    }
                }
                if (bestIdx < 0)
                    break;
                if (remaining[bestIdx].IsHero)
                    heroTaken = true;
                pick.Add(remaining[bestIdx]);
                remaining.RemoveAt(bestIdx);
            }
            return pick;
        }

        // Strongest single stack assemblable from `available`, capped at `cap` slots (the best
        // available hero's CommandRating, or the no-hero baseline). Dynamic — loses a strong unit
        // and this drops. Comparison scalar only; gates nothing.
        public static float BestStackPotential(IReadOnlyList<PowerUnit> available, int cap)
            => EffectiveArmyPower(ComposeStack(available, cap));

        // Whole-game ceiling: the strongest ONE stack the player could ever field, capped at the
        // most capacious hero anywhere in `pool` (own units + hand + remaining deck). Bounded by
        // that hero's CommandRating exactly like a real army — NOT an unbounded sum of every card
        // — and composition-aware, so it is "tanks + artillery + skill coverage", never "7 of the
        // same unit". Drops only when a unit dies or a card leaves the pool, never on mere damage.
        public static float TotalMilitaryPotential(IReadOnlyList<PowerUnit> pool, int bestHeroCommandRating)
            => EffectiveArmyPower(ComposeStack(pool, bestHeroCommandRating));
    }
}
