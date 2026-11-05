#if UNITY_INCLUDE_TESTS
using System;
using System.Collections;
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
            while (arguments.Count < run.GetParameters().Length) arguments.Add(null);
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

        [TestCase(2, false)] [TestCase(4, false)] [TestCase(5, false)]
        [TestCase(2, true)] [TestCase(4, true)] [TestCase(5, true)]
        public void UnequalShooters_UseFreeFrontCellsWhenThereIsNoBenefitToWaiting(int count, bool defending)
        {
            var c = Contact(2, defending);
            c.target.Range = 4; c.target.Attack = 0;
            c.target.HitPointsCurrent = c.target.HitPointsMax = 40;
            for (int i = 1; i < count; i++)
                c.own.Members.Add(Unit(c.own.Owner, 101 + i, attack: 2 + i, defense: i, hp: 4 + i));
            int front = defending ? 1 : 3, back = defending ? 0 : 4;
            BattleAi.ArrangeArmy(c.grid, c.own, front, back, c.enemy, AbilityMagnitudes.Default);
            foreach (UnitData shooter in c.own.Members)
            {
                Assert.That(c.grid.TryFindPosition(shooter, out int row, out _), Is.True);
                Assert.That(row, Is.EqualTo(front), "waiting cannot improve survival against this harmless opponent");
                Assert.That(Act(c.grid, shooter, c.own, c.enemy).Kind, Is.EqualTo(BattleAi.AiActionKind.Attack));
            }
        }

        [TestCase(false)] [TestCase(true)]
        public void FiveMeleeAndShooter_KeepTheMeleeApproachOpen(bool defending)
        {
            var c = Contact(2, defending);
            c.actor.Attack = 2; c.actor.Initiative = 0;
            c.actor.HitPointsCurrent = c.actor.HitPointsMax = 3;
            c.enemy.Members.Clear();
            for (int i = 0; i < BattleGrid.Columns; i++)
            {
                UnitData melee = Unit(c.own.Owner, 110 + i, range: 1, attack: 6, defense: 3, hp: 8);
                melee.Initiative = 4; c.own.Members.Add(melee);
                c.enemy.Members.Add(Unit(c.enemy.Owner, 210 + i, range: 1, attack: 4, defense: 2, hp: 10));
            }
            BattleGrid grid = defending ? BattleGrid.FromArmies(c.enemy, c.own) : BattleGrid.FromArmies(c.own, c.enemy);
            int front = defending ? 1 : 3, back = defending ? 0 : 4;
            BattleAi.ArrangeArmy(grid, c.own, front, back, c.enemy, AbilityMagnitudes.Default);
            Assert.That(grid.TryFindPosition(c.actor, out int shooterRow, out _), Is.True);
            Assert.That(shooterRow, Is.EqualTo(back));
            foreach (UnitData melee in c.own.Members.Where(u => u != c.actor))
            {
                Assert.That(grid.TryFindPosition(melee, out int row, out _), Is.True);
                Assert.That(row, Is.EqualTo(front), "the earlier melee turns must have direct routes to the neutral row");
            }
        }

        [TestCase(false)] [TestCase(true)]
        public void RearShooterCanBeTactical_WithFreeFrontCellsAndAnEnemyThatApproachesFirst(bool defending)
        {
            var c = Contact(2, defending);
            c.actor.Attack = 4; c.actor.Defense = 0;
            c.actor.HitPointsCurrent = c.actor.HitPointsMax = 2;
            UnitData tank = Unit(c.own.Owner, 102, attack: 4, defense: 10, hp: 30);
            c.own.Members.Add(tank);
            c.target.Range = 1; c.target.Attack = 8; c.target.Defense = 1;
            c.target.HitPointsCurrent = c.target.HitPointsMax = 20;
            c.target.Initiative = 10;
            // Cover all columns so a front-row lateral dodge cannot give the shooter the
            // same protection. The earlier enemy turns approach before our ranged turns.
            for (int i = 1; i < BattleGrid.Columns; i++)
            {
                UnitData enemy = Unit(c.enemy.Owner, 201 + i, range: 1, attack: 8, defense: 1, hp: 20);
                enemy.Initiative = 10; c.enemy.Members.Add(enemy);
            }
            c.grid = defending ? BattleGrid.FromArmies(c.enemy, c.own) : BattleGrid.FromArmies(c.own, c.enemy);
            int front = defending ? 1 : 3, back = defending ? 0 : 4;
            BattleAi.ArrangeArmy(c.grid, c.own, front, back, c.enemy, AbilityMagnitudes.Default);
            Assert.That(c.grid.TryFindPosition(c.actor, out int row, out _), Is.True);
            Assert.That(row, Is.EqualTo(back), "the tank can take melee hits without costing the rear shooter its opening shot");
            Assert.That(c.grid.TryFindPosition(tank, out int tankRow, out _), Is.True);
            Assert.That(tankRow, Is.EqualTo(front));
            foreach (UnitData enemy in c.enemy.Members)
            {
                c.grid.TryFindPosition(enemy, out int enemyRow, out int enemyCol);
                c.grid.Set(enemyRow, enemyCol, null); c.grid.Set(BattleGrid.NeutralRow, enemyCol, enemy);
            }
            Assert.That(Act(c.grid, c.actor, c.own, c.enemy).Kind, Is.EqualTo(BattleAi.AiActionKind.Attack));
        }

        [Test]
        public void HealerWithOnlyMechanicalAllies_DoesNotGiveUpItsAttackForNonexistentHealing()
        {
            var c = Contact(2);
            c.actor.Attack = 1; c.actor.Abilities.Add(UnitAbilities.Heal);
            UnitData ally = Unit(c.own.Owner, 102, attack: 5);
            ally.TypeTags.Add(UnitTypeTag.Mechanical); c.own.Members.Add(ally);
            c.target.Range = 4; c.target.Attack = 0;
            c.target.HitPointsCurrent = c.target.HitPointsMax = 40;
            BattleAi.ArrangeArmy(c.grid, c.own, 3, 4, c.enemy, AbilityMagnitudes.Default);
            Assert.That(c.grid.TryFindPosition(c.actor, out int row, out _), Is.True);
            Assert.That(row, Is.EqualTo(3));
            Assert.That(Act(c.grid, c.actor, c.own, c.enemy).Kind, Is.EqualTo(BattleAi.AiActionKind.Attack));
        }

        [Test]
        public void CoroutineCancellation_PreservesBothLiveLayoutsAndStats()
        {
            var c = Contact(2);
            UnitData ally = Unit(c.own.Owner, 102); c.own.Members.Add(ally); c.grid.Set(3, 0, ally);
            var before = c.grid.AllUnits().ToDictionary(u => u, u =>
            {
                c.grid.TryFindPosition(u, out int r, out int col);
                return (r, col, u.Attack, u.Defense, u.HitPointsCurrent);
            });
            bool active = true;
            var routine = BattleAi.ArrangeArmyRoutine(c.grid, c.own, 3, 4, c.enemy,
                AbilityMagnitudes.Default, canCommit: () => active);
            Assert.That(routine.MoveNext(), Is.True);
            var evaluation = routine.Current as IEnumerator;
            Assert.That(evaluation, Is.Not.Null);
            evaluation.MoveNext(); // run a real projection before cancellation
            active = false;
            Drain(evaluation);
            Drain(routine);
            foreach (var pair in before)
            {
                Assert.That(c.grid.TryFindPosition(pair.Key, out int r, out int col), Is.True);
                Assert.That((r, col, pair.Key.Attack, pair.Key.Defense, pair.Key.HitPointsCurrent), Is.EqualTo(pair.Value));
            }
        }

        [Test]
        public void CoroutineAndSynchronousSearch_ChooseSameLayoutWithoutReadingEnemyPositions()
        {
            var c = Contact(2);
            UnitData ally = Unit(c.own.Owner, 102, range: 3); c.own.Members.Add(ally);
            BattleGrid other = BattleGrid.FromArmies(c.own, c.enemy);
            // Its actual position differs from Contact's, but both searches must use the
            // same hypothetical enemy layouts. The live target must retain its position.
            other.TryFindPosition(c.target, out int enemyRow, out int enemyCol);
            other.Set(enemyRow, enemyCol, null); other.Set(0, 4, c.target);
            BattleAi.ArrangeArmy(c.grid, c.own, 3, 4, c.enemy, AbilityMagnitudes.Default);
            Drain(BattleAi.ArrangeArmyRoutine(other, c.own, 3, 4, c.enemy, AbilityMagnitudes.Default));
            foreach (UnitData unit in c.own.Members)
            {
                c.grid.TryFindPosition(unit, out int r, out int col);
                Assert.That(other.TryFindPosition(unit, out int otherRow, out int otherCol), Is.True);
                Assert.That((otherRow, otherCol), Is.EqualTo((r, col)));
            }
            Assert.That(other.Get(0, 4), Is.SameAs(c.target));
        }

        [Test]
        public void FullBattlefield_SearchYieldsAndKeepsLiveCellsUntilTheFinalCommit()
        {
            var c = Contact(2);
            c.own.Members.Clear(); c.enemy.Members.Clear();
            for (int i = 0; i < 10; i++)
            {
                var own = Unit(c.own.Owner, 100 + i, range: 1 + i % 3, attack: 3 + i % 2, hp: 8);
                var enemy = Unit(c.enemy.Owner, 200 + i, range: 1 + i % 3, attack: 3 + i % 2, hp: 8);
                own.IsHero = enemy.IsHero = i == 0;
                c.own.Members.Add(own); c.enemy.Members.Add(enemy);
            }
            BattleGrid grid = BattleGrid.FromArmies(c.own, c.enemy);
            var before = grid.AllUnits().ToDictionary(u => u, u =>
            {
                grid.TryFindPosition(u, out int row, out int col); return (row, col);
            });
            var pending = new Stack<IEnumerator>();
            pending.Push(BattleAi.ArrangeArmyRoutine(grid, c.own, 3, 4, c.enemy, AbilityMagnitudes.Default));
            int steps = 0, frames = 0;
            double peakStepMs = 0;
            var total = System.Diagnostics.Stopwatch.StartNew();
            while (pending.Count > 0)
            {
                Assert.That(++steps, Is.LessThan(2000), "templates plus two local passes must remain bounded");
                var step = System.Diagnostics.Stopwatch.StartNew();
                bool more = pending.Peek().MoveNext();
                peakStepMs = Math.Max(peakStepMs, step.Elapsed.TotalMilliseconds);
                if (!more) { pending.Pop(); continue; }
                if (pending.Peek().Current is IEnumerator nested) { pending.Push(nested); continue; }
                frames++;
                foreach (var pair in before)
                {
                    Assert.That(grid.TryFindPosition(pair.Key, out int row, out int col), Is.True);
                    Assert.That((row, col), Is.EqualTo(pair.Value), "partial search must not alter live deployment");
                }
            }
            Assert.That(frames, Is.GreaterThan(0));
            Assert.That(c.own.Members.All(u => grid.TryFindPosition(u, out _, out _)), Is.True);
            Assert.That(grid.TryFindPosition(c.own.Members[0], out int heroRow, out _), Is.True);
            Assert.That(heroRow, Is.EqualTo(4));
            foreach (UnitData enemy in c.enemy.Members)
            {
                Assert.That(grid.TryFindPosition(enemy, out int row, out int col), Is.True);
                Assert.That((row, col), Is.EqualTo(before[enemy]));
            }
            TestContext.WriteLine($"10-vs-10 deployment: {total.Elapsed.TotalMilliseconds:0.0} ms total, "
                + $"{frames} frame yields, {peakStepMs:0.0} ms longest projection slice (machine-dependent)");
        }

        private static void Drain(IEnumerator routine)
        {
            if (routine == null) return;
            int steps = 0;
            while (routine.MoveNext())
            {
                Assert.That(++steps, Is.LessThan(1000), "deployment search must remain bounded");
                if (routine.Current is IEnumerator nested) Drain(nested);
            }
        }

        [TestCase(0, 0)] [TestCase(0, 4)] [TestCase(3, 2)] [TestCase(4, 4)] [TestCase(8, 5)]
        public void OptimizedExchangeOdds_MatchesIndependentAttackAndDefensePools(int attack, int defense)
        {
            var c = Contact(2);
            c.actor.Attack = attack; c.target.Defense = defense;
            c.actor.Abilities.UnionWith(new[] { UnitAbilities.CriticalDamage, UnitAbilities.Hyperkinetic,
                UnitAbilities.Pyrokinetic, UnitAbilities.Ionic });
            c.target.TypeTags.UnionWith(new[] { UnitTypeTag.Bio, UnitTypeTag.Armored, UnitTypeTag.Mechanical });
            c.target.Abilities.Add(UnitAbilities.CeramicArmor);
            UnitData guard = Unit(c.enemy.Owner, 202);
            guard.TypeTags.Add(UnitTypeTag.Bio); guard.Abilities.Add(UnitAbilities.CeramicArmor);
            foreach (var magnitudes in new[] { AbilityMagnitudes.Default, new AbilityMagnitudes(1.5f, 3, 2, 4, 1, 1, 5) })
            foreach (UnitData interceptor in new[] { null, guard })
            {
                float expected = 0, hit = 0, kill = 0, rawHit = 0;
                const float hp = 2.5f;
                for (int a = 0; a <= attack; a++)
                    for (int d = 0; d <= defense; d++)
                    {
                        float p = BattleSimulationKernel.BinomialHalfProbability(attack, a)
                            * BattleSimulationKernel.BinomialHalfProbability(defense, d);
                        int raw = Math.Max(0, a - d);
                        int damage = ChallengeResult.ApplyAbilityModifiers(raw, c.actor, c.target, magnitudes);
                        if (interceptor != null && damage > 0)
                            damage = BattleSimulationKernel.InterceptedDamage(raw, c.actor.Abilities,
                                interceptor.TypeTags, interceptor.Abilities, magnitudes);
                        expected += p * damage;
                        if (raw > 0) rawHit += p;
                        if (damage > 0) hit += p;
                        if (damage >= hp) kill += p;
                    }
                var actual = BattleCombatOdds.Evaluate(c.actor, c.target, 0, magnitudes, hp, interceptor: interceptor);
                Assert.That(actual.ExpectedDamage, Is.EqualTo(expected).Within(0.00001f));
                Assert.That(actual.HitProbability, Is.EqualTo(hit).Within(0.00001f));
                Assert.That(actual.KillProbability, Is.EqualTo(kill).Within(0.00001f));
                Assert.That(actual.RawHitProbability, Is.EqualTo(rawHit).Within(0.00001f));
            }
        }

        [Test]
        public void ParticipationProjection_IncludesCollateralAndSplitsIntoIdenticalInitiativeSamples()
        {
            var c = Contact(2);
            c.grid.Set(4, 2, null); c.grid.Set(3, 2, c.actor);
            c.actor.Attack = 6; c.actor.Abilities.Add(UnitAbilities.Splash);
            UnitData second = Unit(c.enemy.Owner, 202); c.enemy.Members.Add(second); c.grid.Set(1, 3, second);
            var opening = BattleAi.SimulateRounds(c.grid, c.own, c.enemy, 1, AbilityMagnitudes.Default,
                simulationTrials: 1, deterministic: true, measureParticipation: true);
            Assert.That(opening.OwnParticipationValue, Is.EqualTo(8f - opening.EnemyHpRemaining).Within(0.00001f),
                "participation must include the whole exchange, including Splash and capped lethal damage");
            var together = BattleAi.SimulateRounds(c.grid, c.own, c.enemy, 3, AbilityMagnitudes.Default,
                simulationTrials: 2, deterministic: true, measureParticipation: true);
            var first = BattleAi.SimulateRounds(c.grid, c.own, c.enemy, 3, AbilityMagnitudes.Default,
                simulationTrials: 1, deterministic: true, measureParticipation: true);
            var next = BattleAi.SimulateRounds(c.grid, c.own, c.enemy, 3, AbilityMagnitudes.Default,
                simulationTrials: 1, deterministic: true, measureParticipation: true, firstTrial: 1);
            Assert.That(together.OwnHpRemaining, Is.EqualTo((first.OwnHpRemaining + next.OwnHpRemaining) / 2f).Within(0.00001f));
            Assert.That(together.EnemyHpRemaining, Is.EqualTo((first.EnemyHpRemaining + next.EnemyHpRemaining) / 2f).Within(0.00001f));
            Assert.That(together.OwnParticipationValue, Is.EqualTo((first.OwnParticipationValue + next.OwnParticipationValue) / 2f).Within(0.00001f));
        }

        [Test]
        public void RoundRemainder_DoesNotReuseASpentBodyguardOrMutateTheLiveReactionState()
        {
            var c = Contact(2);
            c.grid.Set(4, 2, null); c.grid.Set(3, 2, c.actor);
            c.actor.Attack = 4; c.target.Defense = 0; c.target.HitPointsCurrent = 2;
            UnitData guard = Unit(c.enemy.Owner, 202, attack: 0, defense: 0, hp: 10);
            guard.Abilities.Add(UnitAbilities.Bodyguard);
            c.enemy.Members.Add(guard); c.grid.Set(1, 3, guard);
            var hp = c.grid.AllUnits().ToDictionary(u => u, u => (float)u.HitPointsCurrent);
            var spent = new BattleReactionState(); spent.BodyguardsUsed.Add(guard);
            MethodInfo run = typeof(BattleAi).GetMethod("RunOneRound", BindingFlags.Static | BindingFlags.NonPublic);
            var args = new object[] { c.grid, hp, new List<UnitData> { c.actor }, AbilityMagnitudes.Default,
                null, false, c.enemy, 0, null, null, null, null, c.own, c.enemy, null, false, null, spent };
            run.Invoke(null, args.Take(run.GetParameters().Length).ToArray());
            Assert.That(hp[c.target], Is.EqualTo(0f).Within(0.00001f));
            Assert.That(hp[guard], Is.EqualTo(10f));
            Assert.That(spent.BodyguardsUsed, Is.EquivalentTo(new[] { guard }));
            Assert.That(c.target.HitPointsCurrent, Is.EqualTo(2), "projection HP must remain private");

            c.grid.Set(1, 2, c.target); hp[c.target] = 2;
            args[17] = null; // the following round starts with fresh reactions
            run.Invoke(null, args.Take(run.GetParameters().Length).ToArray());
            Assert.That(hp[c.target], Is.EqualTo(2f));
            Assert.That(hp[guard], Is.LessThan(10f));
        }

        [TestCase(true, false)] [TestCase(false, false)] [TestCase(false, true)]
        public void SlowerDamage_CanWinWithMoreProgress_ButNotByPostponingTheSameExchange(bool makesProgress, bool sameFinal)
        {
            Type type = typeof(BattleAi).GetNestedType("DeploymentCandidate", BindingFlags.NonPublic);
            IList candidates = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(type));
            object Candidate(float score, float participation, float exchange, float enemyLoss, float opening)
            {
                object result = Activator.CreateInstance(type, nonPublic: true);
                type.GetField("Score").SetValue(result, score);
                type.GetField("Participation").SetValue(result, participation);
                type.GetField("Exchange").SetValue(result, exchange);
                type.GetField("OpeningScore").SetValue(result, opening);
                type.GetField("EnemyLoss")?.SetValue(result, enemyLoss);
                candidates.Add(result);
                return result;
            }
            object active = makesProgress ? Candidate(.2f, .4f, .6f, .4f, .1f)
                : Candidate(-.2f, .2f, .6f, .2f, -.1f);
            object slower = makesProgress ? Candidate(.25f, .3f, .75f, .5f, 0)
                : sameFinal ? Candidate(-.2f, .12f, .6f, .2f, 0) : Candidate(-.1f, .1f, .3f, .1f, 0);
            object chosen = typeof(BattleAi).GetMethod("BestDeployment", BindingFlags.Static | BindingFlags.NonPublic)
                .Invoke(null, new object[] { candidates });
            Assert.That(chosen, Is.SameAs(makesProgress ? slower : active));
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
