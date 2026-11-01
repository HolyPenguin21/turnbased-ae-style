using System.Collections.Generic;
using System.Linq;
using Game.Cards;
using Game.HexGrid;
using Game.Map;
using Game.Terrain;
using Game.Units;
using UnityEngine;

namespace Game.Combat
{
    // The one shared "is this ground fight worth it" estimate — every army-vs-army comparison on
    // the map goes through here, never a private formula at a call site.
    //
    //  · DefenderProfile — one combatant as the estimator sees it: our own live units
    //    (FromLiveUnit) or an enemy's fog-honest remembered roster (AiMapMemory).
    //  · Estimate / WinChance — MonteCarloTrials complete battles (SimulateOneBattle): each round a
    //    shuffle-then-Initiative turn order, every living actor rolls real tokens (50% per die)
    //    against a random living enemy, the canonical ability-modifier chain, and the Fate duel of
    //    every exchange (ResolveExchange, spending decided by FateDuelAi). Each side may have a
    //    commander (SideCommander — ArmyData.Commander, the army's first hero): its Initiative is
    //    added to every combatant of its side and its Fate buys rerolls. No grid positions.
    //  · EstimateSequential — a hex held by several armies: one battle per defending army,
    //    strongest first, wounds carry over, Fate refills.
    //  · HexDefenseBonus — terrain + Base-building defence, added to the DEFENDER's tokens only,
    //    exactly as BattleScreenUI.Combat's BeginAttack does in a real fight.
    //  · CanDamageAll — the coverage gate: every known defender must have at least one attacker
    //    wounding it in at least CoverageMinHitProbability of exchanges (the exact tokens odds
    //    BattleCombatOdds gives live combat), because a win chance alone can overstate a fight
    //    nothing in the roster can actually hurt.
    //  · CombatValue — the quick body value every strongest-first roster pick uses.
    //
    //  Files: this one (public API), .Profiles (inputs), .Simulation (trial loops), .Roster
    //  (BattleUnit and exchange mechanics), .Coverage (CanDamage*), .EstimateCache (exact memo).
    public static partial class WorthIt
    {
        // Attack-sum of `army`'s own non-hero members — the same side of the comparison every
        // caller here always uses for the ATTACKING army (heroes never counted, matching every
        // other flat Attack/Defense sum already in this codebase).
        public static float AttackSum(ArmyData army) => AttackSum(army?.Members);

        // Own non-hero Defense sum, no hex bonus — used on its own as the ATTACKER's side of Score's
        // return-fire read (an attacking army isn't standing on a defensible hex it gets credit for, it's marching onto the defender's).
        public static float DefenseSum(ArmyData army) => DefenseSum(army?.Members);

        // Roster-scoped overloads — same non-hero flat sum against an explicit member set rather
        // than a whole ArmyData. Needed where the caller must rank/measure only the members it is
        // actually allowed to know about: a defender's members that are HIDDEN from the observer
        // must not feed the "which army do I contact" power read (BattleInitiator.FindEnemyAt),
        // or an invisible heavy unit inside a mixed army would still steer the enemy's target
        // pick (project owner's own P1).
        public static float AttackSum(IEnumerable<UnitData> members) => members == null ? 0f : CombatantsOf(members).Sum(m => ExplosionAttack.AttackPower(m.Attack, m.Abilities));
        public static float DefenseSum(IEnumerable<UnitData> members) => members == null ? 0f : CombatantsOf(members).Sum(m => m.Defense);

        // The ONE ground-combatant filter every estimator entry point below applies to BOTH sides
        // (UnitData.IsGroundCombatant / DefenderProfile.IsGroundCombatant). Callers pass whole
        // rosters; no caller filters heroes itself, so no caller can disagree with the battle.
        private static List<UnitData> CombatantsOf(IEnumerable<UnitData> units) =>
            units == null ? new List<UnitData>() : units.Where(u => u != null && u.IsGroundCombatant).ToList();

        private static List<DefenderProfile> CombatantsOf(IEnumerable<DefenderProfile> profiles) =>
            profiles == null ? new List<DefenderProfile>() : profiles.Where(p => p.IsGroundCombatant).ToList();

        // Tactical battle has two different concepts now: actors and targets. Heroes never act,
        // but while combatants are still fighting they are ordinary attackable board targets.
        private static List<UnitData> TacticalTargetsOf(IEnumerable<UnitData> units) =>
            units == null ? new List<UnitData>() : units.Where(u => u != null && (u.IsGroundCombatant || u.IsHero)).ToList();

        private static List<DefenderProfile> TacticalTargetsOf(IEnumerable<DefenderProfile> profiles) =>
            profiles == null ? new List<DefenderProfile>() : profiles.Where(p => p.IsGroundCombatant || p.IsHero).ToList();

        private static List<DefenderProfile> WithBattleSummons(IEnumerable<DefenderProfile> source)
        {
            var roster = TacticalTargetsOf(source);
            if (roster.Any(p => p.IsSummoned))
                return roster;

            UnitAbilityCatalog catalog = UnitAbilityCatalog.Active;
            CardDefinition template = catalog != null ? catalog.ResolveRaiseTheRotsCard() : null;
            if (template == null || catalog.raiseTheRotsUnitsPerSummoner <= 0)
                return roster;

            int summoners = roster.Count(p => p.Abilities != null
                && p.Abilities.Contains(UnitAbilities.RaiseTheRots));
            int freeDeploymentCells = Mathf.Max(0, BattleGrid.Columns * 2 - roster.Count);
            int count = Mathf.Min(freeDeploymentCells,
                summoners * Mathf.Max(0, catalog.raiseTheRotsUnitsPerSummoner));
            for (int i = 0; i < count; i++)
                roster.Add(ProfileFromCard(template));
            return roster;
        }

        private static DefenderProfile ProfileFromCard(CardDefinition card) =>
            new DefenderProfile(card.defenseRating,
                card.grantedAbilities != null && card.grantedAbilities.Contains(UnitAbilities.CeramicArmor),
                card.unitTypeTags, card.attack, card.hitPoints, card.initiative,
                card.grantedAbilities, card.hitPoints, isGroundCombatant: true, isSummoned: true,
                range: card.range);

        // Number of simulated exchanges Score/WinChance each run per call — capped at 100 per the
        // project owner's own explicit call (2026-08-22: "ограничиваемся сотней вызовов"),
        // lowered to 25 (2026-09-19) once profiling showed CombatOpportunityAnalyzer actually
        // calls this twice per known target, per settled step, many settled steps per AI turn —
        // not "once or twice per turn" as assumed when the 100-trial limit was first set. 25
        // trials still gives a usable win-chance estimate; it's a named constant purely so nobody
        // quietly cranks it back up on a hot path without weighing that cost again.
        private const int MonteCarloTrials = 25;

        // Full-roster win fraction — MonteCarloTrials complete battles (SimulateOneBattle above),
        // fresh HP for every trial. `hexDefenseBonus` is folded into every defending unit's own
        // Defense once here (same as CanDamage's own `extraDefense`), never pre-baked by the
        // caller. Empty/null `enemyUnits` is a trivial
        // win (nothing known to fight — matches CanDamageAll's own "vacuously coverable" reading);
        // empty/null `attackerUnits` against a real enemy roster is a trivial loss.
        public static float WinChance(IReadOnlyCollection<DefenderProfile> attackerUnits,
            IReadOnlyCollection<DefenderProfile> enemyUnits, float hexDefenseBonus = 0f,
            SideCommander attackerCommander = default, SideCommander defenderCommander = default,
            AbilityMagnitudes? magnitudes = null) =>
            Estimate(attackerUnits, enemyUnits, hexDefenseBonus, attackerCommander, defenderCommander,
                magnitudes).WinChance;

        // Converts a real UnitData into the same per-combatant snapshot DefenderProfile carries for
        // a remembered/cheat-read enemy — used both for our own army (never behind fog of war) and
        // for a REAL, ground-truth-visible enemy roster no memory lookup is needed for (e.g. an
        // AirStrike loiter/repeat target the army is physically sitting on top of right now — see
        // AiAggressionPlanner.TryEnterLoiterAtTarget). Either way this always uses the unit's REAL
        // current HP (never HitPointsMax the way a remembered/fogged enemy has to). Public since
        // 2026-08-26 (air-strike scoring rework) for that second use — same single conversion, no
        // second copy of it anywhere else.
        public static DefenderProfile FromLiveUnit(UnitData unit) =>
            new DefenderProfile(unit.Defense, unit.HasAbility(UnitAbilities.CeramicArmor), unit.TypeTags.ToList(),
                unit.Attack, unit.HitPointsCurrent, unit.Initiative, unit.Abilities.ToList(),
                unit.HitPointsMax, unit.IsGroundCombatant, unit.IsHero, unit.FateMax, unit.IsSummoned,
                unit.Range, EquipmentSystem.Consumption(unit), ExplosionAttack.IsSingleUseUnit(unit),
                canDisruptBuilding: !unit.IsAviation && !unit.IsPrisoner);

        // Asset-free counterpart of EquipmentSystem.ConsumeAfterAttack for forecasts.
        public static DefenderProfile AfterAttack(DefenderProfile p)
        {
            if (p.SingleUseUnit)
                return new DefenderProfile(p.Defense, p.HasCeramicArmor, p.TypeTags, p.Attack, 0f,
                    p.Initiative, p.Abilities, p.MaxHitPoints, p.IsGroundCombatant, p.IsHero, p.FateMax,
                    p.IsSummoned, p.Range, p.Consumption, true, p.CanDisruptBuilding);
            EquipmentConsumption c = p.Consumption;
            if (c == null) return p;
            var abilities = c.ApplyAbilities(p.Abilities);
            float hp = c.RemainingHitPoints(p.HitPoints, p.MaxHitPoints, out float maxHp);
            return new DefenderProfile(p.Defense + c.Delta(EquipmentStat.Defense),
                abilities.Contains(UnitAbilities.CeramicArmor), p.TypeTags,
                p.Attack + c.Delta(EquipmentStat.Attack), hp,
                p.Initiative + c.Delta(EquipmentStat.Initiative), abilities, maxHp,
                p.IsGroundCombatant, p.IsHero, p.FateMax + c.Delta(EquipmentStat.Fate),
                p.IsSummoned, p.Range + c.Delta(EquipmentStat.Range), canDisruptBuilding: p.CanDisruptBuilding);
        }

        // Richer Monte Carlo readout added 2026-08-24 (project owner's own P1 plan, "WorthIt не
        // оценивает цену победы") alongside the bare win/lose verdict WinChance always returned —
        // a 95% WinChance can still mean the survivors limp home critically wounded, which the old
        // float couldn't say anything about. WinChance(ArmyData, ...) below is now a thin wrapper
        // over Estimate() so every existing pass/fail caller is unaffected.
        public readonly struct BattleEstimate
        {
            public readonly float WinChance;

            // Mean fraction of the attacking army's starting HP (sum across all non-hero members)
            // still standing at the end, averaged only over the trials the attacker actually won —
            // a losing trial says nothing about "surviving" a win that didn't happen.
            public readonly float ExpectedSurvivingHpRatioOnWin;

            // Fraction of WON trials where at least one surviving non-hero member ends the battle
            // at or below half its OWN real MaxHp — the exact predicate
            // RaidWeakerArmyTask.IsCriticallyWounded already applies to a real post-battle army, so
            // this number answers "how often does even a WIN immediately trigger that same
            // return-to-base-to-repair verdict".
            public readonly float CriticalAfterBattleChance;

            public BattleEstimate(float winChance, float expectedSurvivingHpRatioOnWin, float criticalAfterBattleChance)
            {
                WinChance = winChance;
                ExpectedSurvivingHpRatioOnWin = expectedSurvivingHpRatioOnWin;
                CriticalAfterBattleChance = criticalAfterBattleChance;
            }
        }

        // Same full-roster Monte Carlo WinChance(ArmyData, ...) always ran, just also tracking
        // what happens to OUR OWN side across the trials it wins (see BattleEstimate's own
        // comment). Observability only for now (2026-08-24 plan's own explicit scope) — nothing
        // gates on this yet, callers just log it. Seeded identically to the pre-existing
        // WinChance(ArmyData, ...) call (same FromLiveUnit-derived profile list feeds
        // BuildRosterSeed) so this change doesn't shift which battles a given call used to roll.
        // The attacker's commander is read off the live army itself (ArmyData.Commander).
        public static BattleEstimate Estimate(ArmyData attacker, IReadOnlyCollection<DefenderProfile> enemyUnits,
            float hexDefenseBonus = 0f, SideCommander defenderCommander = default,
            AbilityMagnitudes? magnitudes = null)
        {
            enemyUnits = WithBattleSummons(enemyUnits);
            if (!enemyUnits.Any(p => p.IsGroundCombatant))
                return new BattleEstimate(1f, 1f, 0f);

            SideCommander attackerCommander = SideCommander.Of(attacker?.Commander);
            List<BattleUnit> baseline = ToAttackerBattleUnits(attacker, attackerCommander.Initiative);
            var liveProfiles = TacticalTargetsOf(attacker.Members).Select(FromLiveUnit).ToList();
            List<DefenderProfile> liveWithSummons = WithBattleSummons(liveProfiles);
            if (liveWithSummons.Count > liveProfiles.Count)
                baseline.AddRange(ToBattleUnits(
                    liveWithSummons.Skip(liveProfiles.Count).ToList(), 0f, attackerCommander.Initiative));
            if (!baseline.Any(u => !u.IsHero))
                return new BattleEstimate(0f, 0f, 0f);

            // Seeded off the same FromLiveUnit-derived profile the live roster represents — real
            // MaxHp still comes from ToAttackerBattleUnits above (a wounded attacker's true max is
            // not recoverable from DefenderProfile.HitPoints, which only ever carries CURRENT hp).
            var seedProfiles = liveWithSummons;
            int seed = BuildRosterSeed(seedProfiles, enemyUnits, hexDefenseBonus,
                attackerCommander, defenderCommander);
            return EstimateCore(baseline, enemyUnits, hexDefenseBonus, seed,
                attackerCommander, defenderCommander, magnitudes);
        }

        // Roster-vs-roster overload (2026-09-14, Housekeeping contact-selection sync) — the same
        // full round-by-round Monte Carlo as the ArmyData overload above, for a virtual/projected
        // attacker that has no live ArmyData (e.g. an enemy DefenderProfile roster read off
        // AiMapMemory, or a Housekeeping virtual defender roster). Both overloads now share the one
        // EstimateCore loop below — no second Monte Carlo copy.
        public static BattleEstimate Estimate(IReadOnlyCollection<DefenderProfile> attackerUnits,
            IReadOnlyCollection<DefenderProfile> defenderUnits, float hexDefenseBonus,
            SideCommander attackerCommander = default, SideCommander defenderCommander = default,
            AbilityMagnitudes? magnitudes = null)
        {
            attackerUnits = WithBattleSummons(attackerUnits);
            defenderUnits = WithBattleSummons(defenderUnits);
            if (!defenderUnits.Any(p => p.IsGroundCombatant))
                return new BattleEstimate(1f, 1f, 0f);

            if (!attackerUnits.Any(p => !p.IsHero))
                return new BattleEstimate(0f, 0f, 0f);

            int seed = BuildRosterSeed(attackerUnits, defenderUnits, hexDefenseBonus,
                attackerCommander, defenderCommander);
            return EstimateCore(attackerUnits, defenderUnits, hexDefenseBonus, seed,
                attackerCommander, defenderCommander, magnitudes);
        }

        // THE quick "how much fight is in this body" stat sum (Attack + Defense + HitPoints +
        // 0.25 × Initiative). Roster picks (strongest-first ordering, swap candidates, the
        // Monte-Carlo pre-filter) read it through AiPower.SkilledCombatValue, which adds the
        // skill bump. One formula, never re-typed at a call site.
        public static float CombatValue(float attack, float defense, float hitPoints, int initiative) =>
            attack + defense + hitPoints + 0.25f * initiative;

        // Every defending body of an opposition, in army order — the flat roster callers use for
        // counts, power and coverage. Built from the per-army list, never kept as a second source.
        public static List<DefenderProfile> UnitsOf(IEnumerable<DefendingArmy> armies)
        {
            var units = new List<DefenderProfile>();
            if (armies != null)
                foreach (DefendingArmy a in armies)
                    if (a.Units != null)
                        units.AddRange(a.Units);
            return units;
        }

        // Whether this roster brings a ground combatant into a battle (after summons). One without
        // is a Capture/Kill target only: it can escape, but it never damages the hunter
        // (BattleEngine.ApplyCaptureKillOutcome).
        public static bool FieldsGroundCombatant(IEnumerable<DefenderProfile> units) =>
            units != null && WithBattleSummons(units).Any(p => p.IsGroundCombatant);

        // Hero-only contacts have no tactical actors, but their Capture/Kill challenge is not a
        // guaranteed success. Use the canonical pools, Fate policy/order and outcome resolver.
        // Pure observed inputs, deterministic RNG and a separate exact cache mode; no live lookup.
        public static BattleEstimate EstimateCaptureKill(IReadOnlyCollection<DefenderProfile> attackers,
            SideCommander attackerCommander, IReadOnlyList<DefendingArmy> heroArmies)
        {
            int bodyCount = attackers?.Count(p => !p.IsHero && p.IsGroundCombatant) ?? 0;
            if (bodyCount == 0)
                return new BattleEstimate(0f, 0f, 0f);
            var targets = new List<(int pool, int fate)>();
            foreach (DefendingArmy army in heroArmies ?? System.Array.Empty<DefendingArmy>())
            {
                bool firstHero = true;
                foreach (DefenderProfile hero in army.Units.Where(p => p.IsHero))
                {
                    targets.Add((hero.FateMax, firstHero && army.Commander.Present
                        ? army.Commander.Fate : hero.FateMax));
                    firstHero = false;
                }
            }
            if (targets.Count == 0)
                return new BattleEstimate(1f, 1f, 0f);
            int hunterPool = BattleRules.CaptureKillHunterPool(bodyCount);
            int hunterFate = attackerCommander.Present ? attackerCommander.Fate : 0;
            int seed = unchecked(hunterPool * 397 + hunterFate);
            foreach (var target in targets)
                seed = unchecked((seed * 31 + target.pool) * 31 + target.fate);
            return CachedEstimate(3, seed, AbilityMagnitudes.Default,
                key =>
                {
                    key.Add(hunterPool); key.Add(hunterFate); key.Add(targets.Count);
                    foreach (var target in targets) { key.Add(target.pool); key.Add(target.fate); }
                },
                () =>
                {
                    var rng = new System.Random(seed);
                    int wins = 0;
                    for (int trial = 0; trial < MonteCarloTrials; trial++)
                    {
                        int fate = hunterFate;
                        bool won = true;
                        foreach (var target in targets)
                        {
                            int heroFate = target.fate;
                            if (BattleSimulationKernel.ResolveCaptureKill(hunterPool, target.pool,
                                    ref fate, ref heroFate, rng) == CaptureKillOutcome.Escaped)
                                won = false;
                        }
                        if (won) wins++;
                    }
                    return new BattleEstimate((float)wins / MonteCarloTrials, 1f, 0f);
                });
        }

        // A hex held by several armies is taken the way the real rules take it: one battle per
        // defending army, strongest defender first (BattleInitiator.FindEnemyAt — the army the
        // attacker is least likely to beat), the surviving attacker carrying its wounds into the
        // next battle (ResolveHexAfterVictory) while both sides' Fate refills for every battle
        // (ReplenishFateForNewBattle). The attack wins only by winning every battle; the hex
        // defence bonus applies to every defending army. A single army is exactly Estimate().
        public static BattleEstimate EstimateSequential(IReadOnlyCollection<DefenderProfile> attackerUnits,
            SideCommander attackerCommander, IReadOnlyList<DefendingArmy> defendingArmies,
            float hexDefenseBonus, AbilityMagnitudes? magnitudes = null)
        {
            BattleEstimate fights = EstimateSequentialFights(attackerUnits, attackerCommander,
                defendingArmies, hexDefenseBonus, magnitudes);
            // Hero-only armies are no battle, they are Capture/Kill challenges taken after the fights.
            // The opposition-based AI estimate only: the flat Estimate overloads stay as they are, since
            // BattleInitiator.FindEnemyAt ranks real defenders through them.
            // Judged after summons exactly like the fights filter, so a summoning hero is never both.
            List<DefendingArmy> heroOnly = (defendingArmies ?? System.Array.Empty<DefendingArmy>())
                .Where(a => a.Units != null && a.Units.Any(p => p.IsHero)
                    && !FieldsGroundCombatant(a.Units)).ToList();
            if (heroOnly.Count == 0)
                return fights;
            BattleEstimate capture = EstimateCaptureKill(attackerUnits, attackerCommander, heroOnly);
            return new BattleEstimate(fights.WinChance * capture.WinChance,
                fights.ExpectedSurvivingHpRatioOnWin, fights.CriticalAfterBattleChance);
        }

        // `attacker`'s own live non-hero roster as the same DefenderProfile snapshot shape —
        // ArmyData convenience overload of the full-roster WinChance above. Thin wrapper over
        // Estimate() (2026-08-24) — every pass/fail caller here keeps working unchanged.
        public static float WinChance(ArmyData attacker, IReadOnlyCollection<DefenderProfile> enemyUnits,
            float hexDefenseBonus = 0f, SideCommander defenderCommander = default,
            AbilityMagnitudes? magnitudes = null) =>
            Estimate(attacker, enemyUnits, hexDefenseBonus, defenderCommander, magnitudes).WinChance;

        // The hex's own contribution alone, no army — terrain.defenseModifier (see
        // TerrainTypeEntry's own comment: added to the defender's tokens pool only, in every real
        // fight) plus a Base building's own Defense stat if one sits here (see
        // BuildingData.IsBase — only Base buildings carry this, per BattingScreenUI.Combat.cs's
        // own gate). Needed on its own for a Hex Event's card-stat guard (AiMapMemory.
        // KnownEventGuardStrengthAt) — that guard is never a live ArmyData sitting on the hex until
        // Explore is chosen, so there's no army to hand over, only the hex's own bonus to add
        // on top of the guard's own card total.
        public static float HexDefenseBonus(HexCoord hex, HexMap map)
        {
            float bonus = 0f;
            if (map != null && map.TryGetTerrainAt(hex, out TerrainTypeEntry terrain) && terrain != null)
                bonus += terrain.defenseModifier;

            BuildingData building = BuildingRegistry.FindAt(hex);
            if (building != null && building.IsBase)
                bonus += building.Defense;

            return bonus;
        }
    }
}
