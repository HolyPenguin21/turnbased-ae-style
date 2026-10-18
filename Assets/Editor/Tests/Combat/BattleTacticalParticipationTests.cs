#if UNITY_INCLUDE_TESTS
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Game.Cards;
using Game.Combat;
using Game.Map;
using Game.Players;
using Game.Units;
using NUnit.Framework;
using UnityEngine;

namespace Game.EditorTests
{
    public sealed class BattleTacticalParticipationTests
    {
        private static UnitData Unit(PlayerSetupData owner, int id, int range = 2,
            int attack = 3, int defense = 1, int hp = 4)
        {
            UnitData unit = UnitData.CreateProjection(id);
            unit.Owner = owner; unit.Name = "Unit " + id;
            unit.Range = range; unit.Attack = attack; unit.Defense = defense;
            unit.HitPointsCurrent = unit.HitPointsMax = hp;
            unit.Initiative = 1;
            return unit;
        }

        private static (ArmyData own, ArmyData enemy, UnitData actor, UnitData target, BattleGrid grid)
            Contact(int range, bool defending = false)
        {
            var a = new PlayerSetupData { Nickname = "A" };
            var b = new PlayerSetupData { Nickname = "B" };
            var own = ArmyData.WithIdentity(10); own.Owner = a;
            var enemy = ArmyData.WithIdentity(20); enemy.Owner = b;
            UnitData actor = Unit(a, 101, range), target = Unit(b, 201, 2);
            own.Members.Add(actor); enemy.Members.Add(target);
            var grid = new BattleGrid();
            grid.Set(defending ? 0 : 4, 2, actor);
            grid.Set(defending ? 3 : 1, 2, target);
            return (own, enemy, actor, target, grid);
        }

        private static BattleAi.AiAction Act(BattleGrid grid, UnitData actor, ArmyData own,
            ArmyData enemy, Dictionary<UnitData, Vector2Int> previous = null)
            => BattleAi.ChooseAction(grid, actor, new Dictionary<UnitData, int>(), own, enemy,
                AbilityMagnitudes.Default, new List<UnitData> { actor, enemy.Members[0] }, 0,
                battleDefender: enemy, previousPositions: previous);

        [TestCase(1, false)] [TestCase(2, false)]
        [TestCase(1, true)] [TestCase(2, true)]
        public void OutOfRangeUnit_EntersFiringRouteImmediately(int range, bool defending)
        {
            var c = Contact(range, defending);
            var action = Act(c.grid, c.actor, c.own, c.enemy);
            Assert.That(action.Kind, Is.EqualTo(BattleAi.AiActionKind.Move));
            Assert.That(action.Row, Is.EqualTo(defending ? 1 : 3));
        }

        [Test]
        public void ArtilleryOutsideItsOwnRange_AdvancesAgainstLongerRange()
        {
            var c = Contact(3);
            c.grid.Set(1, 2, null); c.grid.Set(0, 2, c.target); c.target.Range = 4;
            var action = Act(c.grid, c.actor, c.own, c.enemy);
            Assert.That(action.Kind, Is.EqualTo(BattleAi.AiActionKind.Move));
            Assert.That(action.Row, Is.EqualTo(3));
        }

        [TestCase(2)] [TestCase(3)] [TestCase(4)]
        public void UnitWithReachableTarget_AttacksWithoutUnnecessaryMovement(int range)
        {
            var c = Contact(range);
            c.grid.Set(4, 2, null); c.grid.Set(3, 2, c.actor);
            var action = Act(c.grid, c.actor, c.own, c.enemy);
            Assert.That(action.Kind, Is.EqualTo(BattleAi.AiActionKind.Attack));
            Assert.That(action.Target, Is.SameAs(c.target));
        }

        [Test]
        public void BlockedShooter_UsesSideStepToReachFiringPosition()
        {
            var c = Contact(2);
            UnitData ally = Unit(c.own.Owner, 102); c.own.Members.Add(ally);
            c.grid.Set(3, 2, ally);
            var previous = new Dictionary<UnitData, Vector2Int>();
            for (int move = 0; move < 2; move++)
            {
                c.grid.TryFindPosition(c.actor, out int row, out int col);
                var action = Act(c.grid, c.actor, c.own, c.enemy, previous);
                Assert.That(action.Kind, Is.EqualTo(BattleAi.AiActionKind.Move));
                Assert.That(c.grid.Get(action.Row, action.Col), Is.Null);
                c.grid.Set(row, col, null); c.grid.Set(action.Row, action.Col, c.actor);
            }
            Assert.That(Act(c.grid, c.actor, c.own, c.enemy, previous).Kind,
                Is.EqualTo(BattleAi.AiActionKind.Attack), "a side step must complete a firing route");
        }

        [Test]
        public void ExpectedDeath_DoesNotExcludeEveryRouteIntoTheFight()
        {
            var c = Contact(2); c.actor.HitPointsCurrent = 1;
            c.target.Attack = 30; c.target.Range = 2;
            var action = Act(c.grid, c.actor, c.own, c.enemy);
            Assert.That(action.Kind, Is.EqualTo(BattleAi.AiActionKind.Move));
            Assert.That(action.Row, Is.EqualTo(3));
        }

        [Test]
        public void SealedUnit_WaitsUntilARealRouteOpens()
        {
            var c = Contact(2);
            int id = 300;
            foreach (var cell in new[] { (3, 2), (4, 1), (4, 3) })
            {
                UnitData ally = Unit(c.own.Owner, id++); c.own.Members.Add(ally);
                c.grid.Set(cell.Item1, cell.Item2, ally);
            }
            Assert.That(Act(c.grid, c.actor, c.own, c.enemy).Kind, Is.EqualTo(BattleAi.AiActionKind.Pass));
            c.grid.Set(4, 1, null);
            Assert.That(Act(c.grid, c.actor, c.own, c.enemy).Kind, Is.EqualTo(BattleAi.AiActionKind.Move));
        }

        [TestCase(1)] [TestCase(2)] [TestCase(3)]
        public void LiveAndProjectedMovement_ChooseTheSameCell(int range)
        {
            var c = Contact(range);
            c.grid.Set(1, 2, null); c.grid.Set(0, 4, c.target);
            var order = new List<UnitData> { c.actor };
            var live = BattleAi.ChooseAction(c.grid, c.actor, new Dictionary<UnitData, int>(),
                c.own, c.enemy, AbilityMagnitudes.Default, order, 0, battleDefender: c.enemy);
            var hp = c.grid.AllUnits().ToDictionary(u => u, u => (float)u.HitPointsCurrent);
            var attack = c.grid.AllUnits().ToDictionary(u => u, u => u.Attack);
            var defense = c.grid.AllUnits().ToDictionary(u => u, u => u.Defense);
            MethodInfo run = typeof(BattleAi).GetMethod("RunOneRound", BindingFlags.Static | BindingFlags.NonPublic);
            var arguments = new List<object> { c.grid, hp, order, AbilityMagnitudes.Default, null,
                true, c.enemy, 0, null, null, attack, defense, c.own, c.enemy,
                new Dictionary<UnitData, Vector2Int>(), false };
            if (run.GetParameters().Length == 17) arguments.Add(null);
            run.Invoke(null, arguments.ToArray());
            Assert.That(live.Kind, Is.EqualTo(BattleAi.AiActionKind.Move));
            Assert.That(c.grid.TryFindPosition(c.actor, out int row, out int col), Is.True);
            Assert.That((row, col), Is.EqualTo((live.Row, live.Col)));
        }

        [TestCase(0)] [TestCase(1)] [TestCase(2)] [TestCase(3)]
        [TestCase(4)] [TestCase(5)] [TestCase(6)] [TestCase(7)]
        public void TwoEqualRangeTwoShooters_BothJoinTheOpeningLine(int identity)
        {
            var c = Contact(2);
            UnitData ally = Unit(c.own.Owner, 102 + identity * 10);
            UnitData secondEnemy = Unit(c.enemy.Owner, 202 + identity * 10, hp: 5);
            c.own.Members.Add(ally); c.enemy.Members.Add(secondEnemy);
            c.grid.Set(3, 1, ally); c.grid.Set(1, 1, secondEnemy);
            BattleAi.ArrangeArmy(c.grid, c.own, 3, 4, c.enemy, AbilityMagnitudes.Default, c.enemy);
            foreach (UnitData unit in c.own.Members)
            {
                Assert.That(c.grid.TryFindPosition(unit, out int row, out _), Is.True);
                Assert.That(row, Is.EqualTo(3), "equal shooters must participate together in this matchup");
            }
        }

        [Test]
        public void HealerCanSupportAnEngagedBioAllyWithoutCharging()
        {
            var c = Contact(1);
            c.actor.Attack = 1; c.actor.Abilities.Add(UnitAbilities.Heal);
            UnitData ally = Unit(c.own.Owner, 102, hp: 6);
            ally.HitPointsCurrent = 3; ally.TypeTags.Add(UnitTypeTag.Bio);
            c.own.Members.Add(ally); c.grid.Set(3, 2, ally);
            Assert.That(Act(c.grid, c.actor, c.own, c.enemy).Kind, Is.EqualTo(BattleAi.AiActionKind.Pass));
        }

        [Test]
        public void HealDoesNotCountAsBodyguardForAnAdjacentMechanicalAlly()
        {
            var c = Contact(1);
            c.actor.Attack = 1; c.actor.Abilities.Add(UnitAbilities.Heal);
            UnitData ally = Unit(c.own.Owner, 102);
            ally.TypeTags.Add(UnitTypeTag.Mechanical);
            c.own.Members.Add(ally); c.grid.Set(3, 2, ally);
            Assert.That(Act(c.grid, c.actor, c.own, c.enemy).Kind, Is.EqualTo(BattleAi.AiActionKind.Move));
        }

        [Test]
        public void LastHealerStillApproachesWhenNoAllyCanFightForIt()
        {
            var c = Contact(1); c.actor.Attack = 1;
            c.actor.Abilities.Add(UnitAbilities.Heal);
            Assert.That(Act(c.grid, c.actor, c.own, c.enemy).Kind, Is.EqualTo(BattleAi.AiActionKind.Move));
        }

        [Test]
        public void PureRangeThreeArtilleryCanDeployIntoOpeningRange()
        {
            var c = Contact(3); c.actor.Attack = 4; c.actor.HitPointsCurrent = 2;
            c.target.Range = 3;
            UnitData ally = Unit(c.own.Owner, 102, 3, 4, 1, 2);
            c.own.Members.Add(ally);
            BattleAi.ArrangeArmy(c.grid, c.own, 3, 4, c.enemy, AbilityMagnitudes.Default, c.enemy);
            Assert.That(c.own.Members.Any(u => c.grid.TryFindPosition(u, out int row, out _) && row == 3), Is.True);
        }

        [Test]
        public void ImpossibleArmorShot_DoesNotPreventApproachingAVulnerableTarget()
        {
            var c = Contact(1);
            c.actor.Attack = 1;
            c.grid.Set(4, 2, null); c.grid.Set(3, 2, c.actor);
            c.grid.Set(1, 2, null); c.grid.Set(2, 2, c.target);
            c.target.Abilities.Add(UnitAbilities.CeramicArmor);
            UnitData soft = Unit(c.enemy.Owner, 202);
            c.enemy.Members.Add(soft); c.grid.Set(1, 4, soft);
            var action = Act(c.grid, c.actor, c.own, c.enemy);
            Assert.That(action.Kind, Is.EqualTo(BattleAi.AiActionKind.Move));
            Assert.That(action.Col, Is.EqualTo(3));
        }

        [Test]
        public void ZeroDamageAgainstEveryEnemy_DoesNotCausePointlessMovement()
        {
            var c = Contact(1); c.actor.Attack = 1;
            c.target.Abilities.Add(UnitAbilities.CeramicArmor);
            Assert.That(Act(c.grid, c.actor, c.own, c.enemy).Kind, Is.EqualTo(BattleAi.AiActionKind.Pass));
        }

        [Test]
        public void TacticalProjections_DoNotMutateLiveStats()
        {
            var c = Contact(2);
            c.actor.Abilities.Add(UnitAbilities.Berserk);
            c.actor.Abilities.Add(UnitAbilities.Drill);
            var before = (c.actor.Attack, c.actor.Defense, c.actor.HitPointsCurrent,
                c.target.Attack, c.target.Defense, c.target.HitPointsCurrent);
            Act(c.grid, c.actor, c.own, c.enemy);
            BattleAi.SimulateRounds(c.grid, c.own, c.enemy, 3, AbilityMagnitudes.Default, c.enemy);
            Assert.That((c.actor.Attack, c.actor.Defense, c.actor.HitPointsCurrent,
                c.target.Attack, c.target.Defense, c.target.HitPointsCurrent), Is.EqualTo(before));
        }
    }
}
#endif
