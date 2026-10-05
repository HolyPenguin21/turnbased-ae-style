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
    //  UnitPower(u)     = (atk*wA + def*wD + hp*wHP + init*wINI + res*wRES) * abilityMult; 0 for a
    //                     hero — a hero is the army's container (CommandRating slots), not a body
    //  ArmyPower        = Σ UnitPower over the bodies in the slots
    //  EffectiveArmyPower = ArmyPower * (compoFloor + (1-compoFloor) * CompositionQuality)
    //  CompositionQuality reads the bodies only (type coverage, front/reach balance).
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
            public readonly int CommandRating;

            public PowerUnit(float basePower, IReadOnlyList<UnitTypeTag> tags, int range, bool isHero,
                int commandRating = 0)
            {
                BasePower = basePower;
                Tags = tags ?? System.Array.Empty<UnitTypeTag>();
                Range = range;
                IsHero = isHero;
                CommandRating = commandRating;
            }
        }

        // ---- per-unit -----------------------------------------------------------------------

        // A hero is the army's CONTAINER, not a combat equivalent of a body: heroes never act in
        // a ground battle (WorthIt), so their combat power is 0. A hero shapes an army's power
        // only through its slots (CommandRating = capacity, filled by bodies whose power sums).
        // Its indirect bonuses (initiative, battle Fate, command choice) belong to
        // HeroRoleEvaluator, never to this scalar.
        public static PowerUnit ToPowerUnit(UnitData u)
        {
            float p = u.IsHero ? 0f : StatLinePower(u);
            return new PowerUnit(p, u.TypeTags.ToList(), u.Range, u.IsHero, u.CommandRating);
        }

        public static PowerUnit ToPowerUnit(CardDefinition c)
        {
            bool isHero = c.cardType == CardType.Hero;
            float p = 0f;
            if (!isHero)
            {
                float line = c.attack * AiConfigV2.powerAttackWeight
                           + c.defenseRating * AiConfigV2.powerDefenseWeight
                           + c.hitPoints * AiConfigV2.powerHitPointsWeight
                           + c.initiative * AiConfigV2.powerInitiativeWeight
                           + c.resistanceRating * AiConfigV2.powerResistanceWeight;
                p = Mathf.Max(0f, line) * AbilityMultiplier(c.grantedAbilities);
            }
            return new PowerUnit(p, c.unitTypeTags, c.range, isHero, c.commandRating);
        }

        // The unit's own stat line (hero Fate included) — the value of its hit points and stats
        // as a unit, NOT army combat power. Only for readers pricing the unit itself, e.g. the
        // repair of a wounded hero (StrategicMaintenancePolicy); army strength reads ToPowerUnit.
        //
        // In-battle Berserk stacks are read out (BattleEngine reverts them when the battle ends):
        // a unit's strength between battles is its pre-battle line, so the panel and any AI read
        // taken while a battle runs do not see a transient spike (playtest 2026-10-01 #7).
        public static float StatLinePower(UnitData u)
        {
            int attack = u.Attack - u.BerserkStacks * AbilityMagnitudes.Default.BerserkAttackGain;
            int defense = u.Defense + u.BerserkDefenseLost;
            float line = attack * AiConfigV2.powerAttackWeight
                       + defense * AiConfigV2.powerDefenseWeight
                       + u.HitPointsCurrent * AiConfigV2.powerHitPointsWeight
                       + u.Initiative * AiConfigV2.powerInitiativeWeight
                       + u.Resistance * AiConfigV2.powerResistanceWeight;
            if (u.IsHero)
                line += u.Fate * AiConfigV2.powerHeroFateWeight;
            return Mathf.Max(0f, line) * AbilityMultiplier(u.Abilities);
        }

        // The ONE projected stat line for a not-yet-
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

            return ProjectedLine(c, stats, abilities, anyGrant);
        }

        private static ProjectedStrategicLine ProjectedLine(CardDefinition c,
            IReadOnlyDictionary<EquipmentStat, int> stats, IReadOnlyList<string> abilities, bool anyGrant)
        {
            int S(EquipmentStat st) => stats.TryGetValue(st, out int v) ? v : 0;
            float line = S(EquipmentStat.Attack) * AiConfigV2.powerAttackWeight
                       + S(EquipmentStat.Defense) * AiConfigV2.powerDefenseWeight
                       + S(EquipmentStat.HitPoints) * AiConfigV2.powerHitPointsWeight
                       + S(EquipmentStat.Initiative) * AiConfigV2.powerInitiativeWeight
                       + S(EquipmentStat.Resistance) * AiConfigV2.powerResistanceWeight;
            if (c.cardType == CardType.Hero)
                line += S(EquipmentStat.Fate) * AiConfigV2.powerHeroFateWeight;

            // No grants: keep ToPowerUnit as the canonical basePower (avoids any drift from the
            // stat-block recompute above). A hero card carries no combat power (see ToPowerUnit).
            float basePower = c.cardType == CardType.Hero ? 0f
                : anyGrant
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
            CardDefinition planned = plan?.GeneratedEquipmentDef ?? plan?.EquipmentInHand?.Definition;
            return EffectiveCardLine(plan?.BaseCardInHand ?? new CardData(baseDef), planned);
        }

        public static ProjectedStrategicLine EffectiveCardLine(CardData card, CardDefinition candidate = null)
        {
            if (card?.Definition == null) return EffectiveLine(null);
            var state = EquipmentSystem.Project(card, candidate);
            return ProjectedLine(card.Definition, state.Stats, state.Abilities,
                card.Equipment?.equipment != null || card.Mutator?.equipment != null || candidate?.equipment != null);
        }

        public static float UnitPower(UnitData u) => ToPowerUnit(u).BasePower;

        // 2026-10-01 — a held card with the equipment already attached to it (EffectiveLine): a
        // Plasma Cannon on an RC Vehicle in hand makes it the strongest body, and the peak, the
        // strike roster and the additive force must see that (Ysolde T17-T23).
        public static PowerUnit ToPowerUnit(CardData card)
        {
            CardDefinition d = card?.Definition;
            if (d == null)
                return new PowerUnit(0f, null, 1, false);
            if (card.Equipment?.equipment == null && card.Mutator?.equipment == null)
                return ToPowerUnit(d);
            ProjectedStrategicLine line = EffectiveCardLine(card);
            PowerUnit plain = ToPowerUnit(d);
            return new PowerUnit(line.BasePower, plain.Tags, line.Range, plain.IsHero, line.CommandRating);
        }

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
                c.initiative,
                c.grantedAbilities,
                isGroundCombatant: c.cardType != CardType.Hero);

        // Power from a WorthIt.DefenderProfile roster — the only stat line available for a
        // remembered / fog-read enemy (no Range on a profile, so composition uses type coverage
        // and hero-count only, not front/reach balance). Used for enemy contacts in the
        // ThreatModel, where a full UnitData is never in hand.
        // `extraDefense` — a defender's hex bonus, folded into every unit's Defense exactly as
        // WorthIt.WinChance folds `hexDefenseBonus`, so a power figure and the estimator it
        // stands in for read the same fortified site.
        public static float EffectiveArmyPowerFromProfiles(IReadOnlyList<WorthIt.DefenderProfile> profiles,
            float extraDefense = 0f)
        {
            if (profiles == null || profiles.Count == 0)
                return 0f;
            var pus = new List<PowerUnit>(profiles.Count);
            foreach (WorthIt.DefenderProfile p in profiles)
            {
                // A defending hero never acts in the battle either: no combat power.
                if (p.IsHero)
                {
                    pus.Add(new PowerUnit(0f, p.TypeTags, 1, true));
                    continue;
                }
                float line = p.Attack * AiConfigV2.powerAttackWeight
                           + (p.Defense + extraDefense) * AiConfigV2.powerDefenseWeight
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
                    case UnitAbilities.Splash: bump += AiConfigV2.powerBumpSplash; break;
                    case UnitAbilities.Scorcher: bump += AiConfigV2.powerBumpScorcher; break;
                    case UnitAbilities.RaiseTheRots: bump += AiConfigV2.powerBumpRaiseTheRots; break;
                    case UnitAbilities.Regeneration: bump += AiConfigV2.powerBumpRegeneration; break;
                }
            }
            return 1f + bump;
        }

        // ---- composition ------------------------------------------------------------------

        // [0..1] — how well-rounded a roster's BODIES are: distinct type tags present and a
        // front/reach mix (a hero is neither). A lone unit or an all-one-type stack scores low (but never 0 — see
        // EffectiveArmyPower's compoFloor).
        public static float CompositionQuality(IReadOnlyCollection<PowerUnit> units)
        {
            if (units == null || units.Count == 0)
                return 0f;

            var distinctTags = new HashSet<UnitTypeTag>();
            bool hasFront = false, hasReach = false;
            foreach (PowerUnit pu in units)
            {
                // Bodies only: a hero never fights, so its tags are no type coverage and it is
                // neither front nor reach.
                if (pu.IsHero) continue;
                foreach (UnitTypeTag t in pu.Tags)
                    if (t != UnitTypeTag.Hero)
                        distinctTags.Add(t);
                if (pu.Range <= 1) hasFront = true;
                else hasReach = true;
            }

            float typeCoverage = Mathf.Clamp01(distinctTags.Count / (float)Mathf.Max(1, AiConfigV2.compoTypeCoverageTarget));
            float rangeBalance = (hasFront && hasReach) ? 1f : (hasFront || hasReach) ? 0.5f : 0f;

            // A hero shapes an army only through its slots (ToPowerUnit): its presence is not a
            // composition quality of the fighting bodies either.
            float wSum = AiConfigV2.compoWeightTypeCoverage + AiConfigV2.compoWeightRangeBalance;
            if (wSum < 0.0001f)
                return 0f;
            return (AiConfigV2.compoWeightTypeCoverage * typeCoverage
                  + AiConfigV2.compoWeightRangeBalance * rangeBalance) / wSum;
        }

        public static float EffectiveArmyPower(IReadOnlyCollection<PowerUnit> units)
        {
            if (units == null || units.Count == 0)
                return 0f;
            float raw = units.Sum(u => u.BasePower);
            float q = CompositionQuality(units);
            return raw * (AiConfigV2.compoFloor + (1f - AiConfigV2.compoFloor) * q);
        }

        // A unit summoned into one battle (UnitData.IsSummoned, removed at battle end) is never
        // part of an army's strength.
        public static float EffectiveArmyPower(IEnumerable<UnitData> members)
        {
            List<PowerUnit> pus = members?.Where(m => m != null && !m.IsSummoned)
                .Select(ToPowerUnit).ToList();
            return pus == null || pus.Count == 0 ? 0f : EffectiveArmyPower(pus);
        }

        public static float CompositionQualityOf(IEnumerable<UnitData> members)
        {
            List<PowerUnit> pus = members?.Select(ToPowerUnit).ToList();
            return pus == null || pus.Count == 0 ? 0f : CompositionQuality(pus);
        }

        // ---- potentials ------------------------------------------------------------------

        // Shared live/card pool for Attack and the player data panel. Preserve Analysis's
        // map, hand bodies, deck bodies, hand heroes, deck heroes ordering for greedy ties.
        public static List<PowerUnit> MilitaryPool(IEnumerable<UnitData> live,
            IEnumerable<CardData> hand, IEnumerable<CardDefinition> deck, bool groundOnly = true)
        {
            var result = new List<PowerUnit>();
            if (live != null)
                foreach (UnitData unit in live)
                    if (unit != null && !unit.IsPrisoner && !unit.IsSummoned
                        && (!groundOnly || !unit.IsAviation))
                        result.Add(ToPowerUnit(unit));

            List<CardData> handCards = hand?.Where(c => c?.Definition != null).ToList()
                ?? new List<CardData>();
            List<CardDefinition> deckCards = deck?.ToList() ?? new List<CardDefinition>();
            void AddHand(CardType kind)
            {
                foreach (CardData card in handCards)
                    if (card.Definition.cardType == kind && (!groundOnly || !card.Definition.isAviation))
                        result.Add(ToPowerUnit(card));
            }
            void Add(IEnumerable<CardDefinition> cards, CardType kind)
            {
                foreach (CardDefinition card in cards)
                    if (card != null && card.cardType == kind && (!groundOnly || !card.isAviation))
                        result.Add(ToPowerUnit(card));
            }
            AddHand(CardType.Unit);
            Add(deckCards, CardType.Unit);
            AddHand(CardType.Hero);
            Add(deckCards, CardType.Hero);
            return result;
        }

        // Composition-aware greedy stack build. Repeatedly adds whichever remaining candidate
        // maximises the resulting EffectiveArmyPower — and EffectiveArmyPower already folds in the
        // composition multiplier, so an all-one-type stack naturally pulls in a different type /
        // skill once that bump beats the raw-power delta of yet another same-type unit. One hero
        // max; `cap` slots total. Not a true knapsack (that candidate loop is per-slot greedy),
        // but it is an informational comparison scalar, not a battle plan. O(cap^2 * n), run once
        // per AI turn.
        public static List<PowerUnit> ComposeStack(IReadOnlyList<PowerUnit> pool, int cap,
            PowerUnit? commander = null) =>
            ComposeStackOf(pool, u => u, cap, commander.HasValue, commander.GetValueOrDefault());

        // The same greedy over identified candidates (`unitOf` reads each one's PowerUnit): the
        // strike-force target roster needs WHICH cards form the peak, not only its power. The
        // PowerUnit overload above delegates here, so both always pick the same stack.
        public static List<T> ComposeStackOf<T>(IReadOnlyList<T> pool, System.Func<T, PowerUnit> unitOf,
            int cap, bool hasCommander = false, T commander = default)
        {
            var pick = new List<T>();
            var pickUnits = new List<PowerUnit>();
            if (hasCommander) { pick.Add(commander); pickUnits.Add(unitOf(commander)); }
            if (pool == null || pool.Count == 0)
                return pick;
            cap = Mathf.Max(1, cap);

            var remaining = new List<T>(pool);
            bool heroTaken = hasCommander;
            while (pick.Count < cap && remaining.Count > 0)
            {
                int bestIdx = -1;
                float bestScore = float.NegativeInfinity;
                for (int i = 0; i < remaining.Count; i++)
                {
                    PowerUnit candidate = unitOf(remaining[i]);
                    if (candidate.IsHero && heroTaken)
                        continue;
                    pickUnits.Add(candidate);
                    float score = EffectiveArmyPower(pickUnits);
                    pickUnits.RemoveAt(pickUnits.Count - 1);
                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestIdx = i;
                    }
                }
                if (bestIdx < 0)
                    break;
                PowerUnit chosen = unitOf(remaining[bestIdx]);
                if (chosen.IsHero)
                    heroTaken = true;
                pick.Add(remaining[bestIdx]);
                pickUnits.Add(chosen);
                remaining.RemoveAt(bestIdx);
            }
            return pick;
        }

        // Strongest single stack assemblable from `available`, capped at `cap` slots (the best
        // available hero's CommandRating, or the no-hero baseline). Dynamic — loses a strong unit
        // and this drops. Comparison scalar only; gates nothing.
        public static float BestStackPotential(IReadOnlyList<PowerUnit> available, int cap)
            => EffectiveArmyPower(ComposeStack(available, cap));

        // Current ceiling: the strongest ONE stack from own units, hand and remaining deck.
        // Each candidate hero sets its own capacity, including the no-hero two-body case — not
        // a capacity borrowed from a different hero and not an unbounded sum of every card
        // — and composition-aware, so it is "tanks + artillery + skill coverage", never "7 of the
        // same unit". Live units use their current stats, including current hit points.
        public static float TotalMilitaryPotential(IReadOnlyList<PowerUnit> pool) =>
            PeakStackOf(pool, u => u, out _);

        // TotalMilitaryPotential over identified candidates: returns the peak power and the
        // roster (commander first when a hero leads it) that reaches it.
        public static float PeakStackOf<T>(IReadOnlyList<T> pool, System.Func<T, PowerUnit> unitOf,
            out List<T> roster)
        {
            roster = new List<T>();
            if (pool == null) return 0f;
            // A hero's own command rating, not another hero's, determines the capacity of
            // the stack containing it. Enumerate the commander before the greedy body pick.
            List<T> bodies = pool.Where(u => !unitOf(u).IsHero).ToList();
            roster = ComposeStackOf(bodies, unitOf, 2);
            float best = EffectiveArmyPower(roster.Select(unitOf).ToList());
            foreach (T hero in pool.Where(u => unitOf(u).IsHero))
            {
                int capacity = unitOf(hero).CommandRating;
                if (capacity < 1) continue;
                List<T> candidate = ComposeStackOf(bodies, unitOf, capacity, true, hero);
                float power = EffectiveArmyPower(candidate.Select(unitOf).ToList());
                if (power > best)
                {
                    best = power;
                    roster = candidate;
                }
            }
            return best;
        }

        // The nested ground ceilings of one player's force, on TotalMilitaryPotential's one
        // commander-in-slot rule: Field = map pool, Units = map + hand/deck bodies (only the
        // map's own commanders), Total = + hand/deck heroes. A pool that contains a smaller one
        // is never weaker than it: that stack is still legal in the bigger pool, while the
        // greedy body pick alone is not monotone under added candidates. So
        // Field + (Units - Field) + (Total - Units) == Total by construction, and a hero card
        // raises the ceiling only through the slots its CommandRating opens.
        public readonly struct ForcePotentials
        {
            public readonly float Field, Units, Total;

            public ForcePotentials(float field, float units, float total)
            {
                Field = field;
                Units = units;
                Total = total;
            }

            public float UnitsReserve => Units - Field;
            public float HeroReserve => Total - Units;
        }

        public static ForcePotentials NestedPotentials(IEnumerable<UnitData> live,
            IEnumerable<CardData> hand, IEnumerable<CardDefinition> deck)
        {
            List<PowerUnit> map = MilitaryPool(live, null, null);
            // MilitaryPool orders map, hand bodies, deck bodies, hand heroes, deck heroes.
            List<PowerUnit> cards = MilitaryPool(null, hand, deck);
            return NestedPotentials(map, cards.Where(u => !u.IsHero).ToList(),
                cards.Where(u => u.IsHero).ToList());
        }

        public static ForcePotentials NestedPotentials(IReadOnlyList<PowerUnit> map,
            IReadOnlyList<PowerUnit> cardBodies, IReadOnlyList<PowerUnit> cardHeroes) =>
            NestedPotentialsOf(map, cardBodies, cardHeroes, u => u, out _);

        // NestedPotentials over identified candidates, plus the roster of the Total ceiling: the
        // stack of whichever nested pool reaches it (a smaller pool wins ties, as the Max does).
        public static ForcePotentials NestedPotentialsOf<T>(IReadOnlyList<T> map,
            IReadOnlyList<T> cardBodies, IReadOnlyList<T> cardHeroes, System.Func<T, PowerUnit> unitOf,
            out List<T> peakRoster)
        {
            var withBodies = new List<T>(map ?? System.Array.Empty<T>());
            withBodies.AddRange(cardBodies ?? System.Array.Empty<T>());
            var full = new List<T>(withBodies);
            full.AddRange(cardHeroes ?? System.Array.Empty<T>());

            float field = PeakStackOf(map ?? System.Array.Empty<T>(), unitOf, out List<T> fieldRoster);
            float unitsRaw = PeakStackOf(withBodies, unitOf, out List<T> unitsRoster);
            float units = Mathf.Max(field, unitsRaw);
            if (unitsRaw <= field) unitsRoster = fieldRoster;
            float totalRaw = PeakStackOf(full, unitOf, out List<T> totalRoster);
            float total = Mathf.Max(units, totalRaw);
            peakRoster = totalRaw <= units ? unitsRoster : totalRoster;
            return new ForcePotentials(field, units, total);
        }
    }
}
