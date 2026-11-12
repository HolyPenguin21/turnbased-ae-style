#if UNITY_INCLUDE_TESTS
using System.Collections.Generic;
using System.Linq;
using Game.Cards;
using Game.Combat;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Units;
using NUnit.Framework;

namespace Game.EditorTests
{
    // Battle rules under a sandstorm (spec 6): decided by the battle hex, frozen per encounter;
    // the defender loses terrain + building bonuses (never its own Defense), every unit attacks
    // with Range - 1 (min 1) once, and the AI forecast reads the same rule exactly once.
    public sealed class SandstormBattleRulesTests
    {
        private static readonly HexCoord BattleHex = new HexCoord(0, 0);

        [SetUp] public void SetUp() { SandstormState.ResetForMatch(); BuildingRegistry.Clear(); ArmyRegistry.Clear(); }
        [TearDown] public void TearDown() { SandstormState.ResetForMatch(); BuildingRegistry.Clear(); ArmyRegistry.Clear(); }

        // A storm body whose head covers the battle hex (restored exactly, no tick).
        private static void StormOverBattleHex()
        {
            var data = new SandstormStateData { profile = (int)MatchWeatherProfile.OneSandstorm, seed = 1, lastRound = 3 };
            var slot = new SandstormSlotData { id = 0, phase = (int)SandstormPhase.Active, age = 2, course = 0, random = 5 };
            foreach (HexCoord h in SandstormRules.SpotCells(new HexCoord(-1, 0), 0, grown: true)) { slot.bodyQ.Add(h.Q); slot.bodyR.Add(h.R); }
            data.slots.Add(slot);
            SandstormState.RestoreState(SandstormLifecycleTests.Field(6), data);
            Assume.That(SandstormState.Snapshot.Contains(BattleHex), Is.True);
        }

        [TestCase(1, 1)] [TestCase(2, 1)] [TestCase(3, 2)] [TestCase(5, 4)]
        [TestCase(0, 0)] [TestCase(-1, -1)]
        public void EffectiveRange_MinusOneNotBelowOne_UnknownAndZeroUntouched(int range, int stormRange)
        {
            Assert.That(BattleRules.EffectiveAttackRange(range, BattleEnvironment.Sandstorm), Is.EqualTo(stormRange));
            Assert.That(BattleRules.EffectiveAttackRange(range, BattleEnvironment.Clear), Is.EqualTo(range));
        }

        [Test]
        public void EnvironmentIsDecidedByTheBattleHex_AndOverlapsCountOnce()
        {
            var a = new SandstormInstanceView(0, SandstormPhase.Active, 1, 0, new[] { BattleHex });
            var b = new SandstormInstanceView(1, SandstormPhase.Active, 1, 0, new[] { BattleHex });
            var two = new SandstormSnapshot(MatchWeatherProfile.TwoSandstorms, 1, new[] { a, b });
            Assert.That(BattleEnvironment.At(BattleHex, two), Is.EqualTo(BattleEnvironment.Sandstorm));
            Assert.That(BattleEnvironment.At(new HexCoord(1, 0), two), Is.EqualTo(BattleEnvironment.Clear));
            Assert.That(BattleEnvironment.Sandstorm.AttackRange(BattleEnvironment.Sandstorm.AttackRange(3)), Is.EqualTo(1),
                "applying twice WOULD stack - which is why it is applied once");
            Assert.That(BattleEnvironment.Sandstorm.HexDefense(2, 3), Is.Zero);
            Assert.That(BattleEnvironment.Clear.HexDefense(2, 3), Is.EqualTo(5));
        }

        private static UnitData Unit(PlayerSetupData owner, int id, int range, int defense = 4)
        {
            UnitData unit = UnitData.CreateProjection(id);
            unit.Owner = owner; unit.Name = "U" + id; unit.Range = range; unit.Attack = 3; unit.Defense = defense;
            unit.HitPointsCurrent = unit.HitPointsMax = 6; unit.Initiative = 1;
            return unit;
        }

        private static (BattleEngine engine, UnitData attacker, UnitData defender) Encounter(int attackerRange, bool disruption = false)
        {
            var a = new PlayerSetupData { Nickname = "A" };
            var d = new PlayerSetupData { Nickname = "D" };
            BuildingRegistry.Register(BattleHex, new BuildingData { Owner = d, Hex = BattleHex, IsBase = true, Defense = 3 });
            var attackerArmy = ArmyData.WithIdentity(31); attackerArmy.Owner = a; attackerArmy.Hex = BattleHex;
            var defenderArmy = ArmyData.WithIdentity(32); defenderArmy.Owner = d; defenderArmy.Hex = BattleHex;
            UnitData attacker = Unit(a, 301, attackerRange), defender = Unit(d, 302, 2);
            if (disruption) attacker.Abilities.Add(UnitAbilities.Disruption);
            attackerArmy.Members.Add(attacker); defenderArmy.Members.Add(defender);
            var grid = new BattleGrid();
            grid.Set(BattleGrid.AttackerFrontRow, 2, attacker);
            grid.Set(BattleGrid.DefenderFrontRow, 2, defender); // two rows apart
            var state = new BattleState(BattleHex, attackerArmy, defenderArmy, grid, 7);
            return (new BattleEngine(state, AbilityMagnitudes.Default, new System.Random(1)), attacker, defender);
        }

        [Test]
        public void ClearHex_OwnBaseDefenseCounts_RangeTwoReaches()
        {
            var e = Encounter(2);
            Assert.That(e.engine.Rules.Environment.InSandstorm, Is.False);
            Assert.That(e.engine.GetDefenseBreakdown(e.defender, null).TotalBonus, Is.EqualTo(3));
            Assert.That(e.engine.State.Grid.AttackRange(e.attacker), Is.EqualTo(2));
        }

        [Test]
        public void StormHex_RemovesHexBonuses_KeepsOwnDefense_AndShortensRange()
        {
            StormOverBattleHex();
            var e = Encounter(2);
            Assert.That(e.engine.Rules.Environment.InSandstorm, Is.True);
            Assert.That(e.engine.GetDefenseBreakdown(e.defender, null).TotalBonus, Is.Zero);
            Assert.That(e.defender.Defense, Is.EqualTo(4), "the unit's own Defense is untouched");
            Assert.That(e.engine.State.Grid.AttackRange(e.attacker), Is.EqualTo(1));
            Assert.That(e.attacker.Range, Is.EqualTo(2), "UnitData.Range is never rewritten");
        }

        [Test]
        public void StormAndDisruption_NeverSubtractTwice()
        {
            StormOverBattleHex();
            var e = Encounter(2, disruption: true);
            Assert.That(e.engine.GetDefenseBreakdown(e.defender, null).TotalBonus, Is.Zero);
            SandstormState.ResetForMatch();
            BuildingRegistry.Clear();
            var clear = Encounter(2, disruption: true);
            Assert.That(clear.engine.GetDefenseBreakdown(clear.defender, null).TotalBonus, Is.Zero,
                "outside the storm Disruption alone cancels the building");
        }

        [Test]
        public void WeatherIsFrozenWhenTheEncounterStarts()
        {
            StormOverBattleHex();
            var e = Encounter(3);
            SandstormState.ResetForMatch();
            Assert.That(e.engine.Rules.Environment.InSandstorm, Is.True);
            Assert.That(e.engine.State.Grid.AttackRange(e.attacker), Is.EqualTo(2));
        }

        // ---- the AI forecast ---------------------------------------------------------------------

        private static WorthIt.DefenderProfile P(int range, float attack = 3f, float defense = 2f, float hp = 6f) =>
            new WorthIt.DefenderProfile(defense, false, attack: attack, hitPoints: hp, initiative: 1, range: range);

        private static List<WorthIt.DefenderProfile> Side(params int[] ranges) => ranges.Select(r => P(r)).ToList();

        [Test]
        public void Forecast_StormIsItsOwnCachedAnswer_AndZeroesTheHexBonus()
        {
            // Range-2 shooters on both sides: in a storm they must approach (effective range 1).
            var attackers = Side(2, 2, 3);
            var defenders = Side(2, 2, 2);
            float coldClear, coldStorm;
            WorthIt.BeginEstimateCacheScope();
            try { coldStorm = WorthIt.Estimate(attackers, defenders, 3f, environment: BattleEnvironment.Sandstorm).WinChance; }
            finally { WorthIt.EndEstimateCacheScope(); }
            WorthIt.BeginEstimateCacheScope();
            try { coldClear = WorthIt.Estimate(attackers, defenders, 3f).WinChance; }
            finally { WorthIt.EndEstimateCacheScope(); }
            WorthIt.BeginEstimateCacheScope();
            try
            {
                Assert.That(WorthIt.Estimate(attackers, defenders, 3f).WinChance, Is.EqualTo(coldClear));
                Assert.That(WorthIt.Estimate(attackers, defenders, 3f, environment: BattleEnvironment.Sandstorm).WinChance,
                    Is.EqualTo(coldStorm), "a storm estimate after a clear one in the same scope is the storm's own answer");
                Assert.That(WorthIt.Estimate(attackers, defenders, 0f, environment: BattleEnvironment.Sandstorm).WinChance,
                    Is.EqualTo(coldStorm), "the hex bonus is 0 in a storm, whatever the caller passed");
            }
            finally { WorthIt.EndEstimateCacheScope(); }
            Assert.That(attackers.Select(p => p.Range), Is.EqualTo(new[] { 2, 2, 3 }), "profiles are never rewritten");
        }

        [Test]
        public void SequentialForecast_ReadsTheHexWeatherFromTheOpposition()
        {
            var attackers = Side(3, 2);
            var first = Side(3, 2);
            var second = Side(2);
            var stormOpposition = new List<WorthIt.DefendingArmy>
            {
                new WorthIt.DefendingArmy(first, default, 2f, 1, 1f, environment: BattleEnvironment.Sandstorm),
                new WorthIt.DefendingArmy(second, default, 2f, 2, 1f, environment: BattleEnvironment.Sandstorm),
            };
            var clearOpposition = stormOpposition.Select(a => a.WithEnvironment(default)).ToList();
            var zeroBonusClear = new List<WorthIt.DefendingArmy>
            {
                new WorthIt.DefendingArmy(first, default, 0f, 1, 0f),
                new WorthIt.DefendingArmy(second, default, 0f, 2, 0f),
            };
            float fromOpposition = WorthIt.EstimateSequential(attackers, default, stormOpposition, 2f).WinChance;
            float explicitStorm = WorthIt.EstimateSequential(attackers, default, clearOpposition, 2f,
                environment: BattleEnvironment.Sandstorm).WinChance;
            float zeroBonusStorm = WorthIt.EstimateSequential(attackers, default, zeroBonusClear, 0f,
                environment: BattleEnvironment.Sandstorm).WinChance;
            Assert.That(fromOpposition, Is.EqualTo(explicitStorm));
            Assert.That(fromOpposition, Is.EqualTo(zeroBonusStorm), "every hex bonus and building part is 0 in a storm");
            Assert.That(WorthIt.FightOrder(attackers, default, stormOpposition, 2f).Select(a => a.Environment.InSandstorm),
                Is.All.True, "ordering returns the caller's own armies");
        }

        [Test]
        public void StormArmies_NeverGoNegativeWithDisruption()
        {
            var army = WorthIt.UnderWeather(new[]
            {
                new WorthIt.DefendingArmy(Side(2), default, 5f, 1, 3f, environment: BattleEnvironment.Sandstorm),
            }, BattleEnvironment.Sandstorm).Single();
            var carrier = new WorthIt.DefenderProfile(2f, false, attack: 3f, hitPoints: 4f,
                abilities: new[] { UnitAbilities.Disruption });
            Assert.That(army.DefenseBonus(5f, new[] { carrier }), Is.Zero);
            Assert.That(army.Environment.InSandstorm, Is.False, "the storm is applied by the estimate, once");
        }
    }
}
#endif
