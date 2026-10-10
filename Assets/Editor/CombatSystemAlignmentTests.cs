#if UNITY_INCLUDE_TESTS
using System;
using System.Collections.Generic;
using Game.Cards;
using Game.Combat;
using Game.Map;
using Game.Players;
using Game.Units;
using NUnit.Framework;

namespace Game.EditorTests
{
    public sealed class CombatSystemAlignmentTests
    {
        [TestCase(1)]
        [TestCase(42)]
        [TestCase(123)]
        public void CaptureKillEstimateKernel_MatchesTheLiveChallengeSession(int seed)
        {
            var rng = new System.Random(seed);
            var session = new BattleChallengeSession(BattleChallengeMode.CaptureKill,
                null, null, 2, 3, 3, 3, AbilityMagnitudes.Default,
                rollDice: count => BattleSimulationKernel.RollDice(count, rng));
            session.Roll();
            while (session.TryNextFateTurn(out bool defenderTurn))
            {
                bool spent = false;
                while (session.ShouldAiSpend(defenderTurn))
                {
                    if (!session.TrySpend(defenderTurn, out _, out bool hit)) break;
                    spent = true;
                    if (!hit) break;
                }
                session.ReportFateTurn(spent);
            }
            int hunterFate = 3, heroFate = 3;
            CaptureKillOutcome estimated = BattleSimulationKernel.ResolveCaptureKill(2, 3,
                ref hunterFate, ref heroFate, new System.Random(seed));
            Assert.That(estimated, Is.EqualTo(session.ResolveCaptureKillOutcome()));
            Assert.That(hunterFate, Is.EqualTo(session.AttackerFateRemaining));
            Assert.That(heroFate, Is.EqualTo(session.DefenderFateRemaining));
        }

        [Test]
        public void CaptureKillEstimateCache_IsSeparatedFromTacticalCombat_AndKeysObservedFate()
        {
            var hero = new WorthIt.DefenderProfile(0, false, hitPoints: 4,
                isGroundCombatant: false, isHero: true, fateMax: 4);
            var bodies = new[] { new WorthIt.DefenderProfile(3, false, attack: 8, hitPoints: 4) };
            WorthIt.BeginEstimateCacheScope();
            try
            {
                var opposition = new[] { new WorthIt.DefendingArmy(new[] { hero }, new WorthIt.SideCommander(0, 4)) };
                float first = WorthIt.EstimateCaptureKill(bodies, default, opposition).WinChance;
                float again = WorthIt.EstimateCaptureKill(bodies, default, opposition).WinChance;
                Assert.That(again, Is.EqualTo(first));
                Assert.That(WorthIt.CurrentEstimateCacheStats.Misses, Is.EqualTo(1));
                Assert.That(WorthIt.CurrentEstimateCacheStats.Hits, Is.EqualTo(1));
                var spentFate = new[] { new WorthIt.DefendingArmy(new[] { hero }, new WorthIt.SideCommander(0, 0)) };
                WorthIt.EstimateCaptureKill(bodies, default, spentFate);
                WorthIt.EstimateCaptureKill(bodies, new WorthIt.SideCommander(0, 3), spentFate);
                Assert.That(WorthIt.CurrentEstimateCacheStats.Misses, Is.EqualTo(3),
                    "observed defender Fate and our current Fate are separate cache inputs");
                Assert.That(first, Is.LessThan(1f), "the empty tactical battle must not supply capture odds");
                Assert.That(WorthIt.EstimateSequential(bodies, default, opposition, 0).WinChance, Is.EqualTo(first),
                    "a hero-only opposition is priced by its capture odds, not by the empty tactical battle");
            }
            finally { WorthIt.EndEstimateCacheScope(); }
        }

        [Test]
        public void EqualAttackDefense_HasPositiveExpectedDamage()
        {
            var attacker = new UnitData { Attack = 4, Defense = 1, HitPointsCurrent = 4, HitPointsMax = 4 };
            var defender = new UnitData { Attack = 1, Defense = 4, HitPointsCurrent = 10, HitPointsMax = 10 };

            BattleCombatOdds.ExchangeOdds odds = BattleCombatOdds.Evaluate(
                attacker, defender, 0, AbilityMagnitudes.Default, defender.HitPointsCurrent);

            Assert.That(odds.ExpectedDamage, Is.EqualTo(0.546875f).Within(0.00001f));
            Assert.That(odds.HitProbability, Is.GreaterThan(0f));
        }

        [Test]
        public void HeroPlacement_IsBackRowOnly()
        {
            var hero = new UnitData { IsHero = true };

            Assert.That(BattlePlacementRules.CanPlace(hero, BattleGrid.AttackerBackRow, 2,
                BattleGrid.AttackerFrontRow, BattleGrid.AttackerBackRow), Is.True);
            Assert.That(BattlePlacementRules.CanPlace(hero, BattleGrid.AttackerFrontRow, 2,
                BattleGrid.AttackerFrontRow, BattleGrid.AttackerBackRow), Is.False);
        }

        [Test]
        public void BattleGrid_DefaultPlacement_KeepsAllHeroesInBackRow()
        {
            var owner = new PlayerSetupData { Nickname = "A" };
            var army = new ArmyData { Owner = owner, Name = "A" };
            var hero1 = new UnitData { Owner = owner, IsHero = true, FateMax = 3, HitPointsCurrent = 5, HitPointsMax = 5 };
            var hero2 = new UnitData { Owner = owner, IsHero = true, FateMax = 2, HitPointsCurrent = 5, HitPointsMax = 5 };
            var body = Body(owner);
            army.Members.Add(body);
            army.Members.Add(hero1);
            army.Members.Add(hero2);

            BattleGrid grid = BattleGrid.FromArmies(army, null);

            Assert.That(grid.TryFindPosition(hero1, out int h1Row, out _), Is.True);
            Assert.That(grid.TryFindPosition(hero2, out int h2Row, out _), Is.True);
            Assert.That(h1Row, Is.EqualTo(BattleGrid.AttackerBackRow));
            Assert.That(h2Row, Is.EqualTo(BattleGrid.AttackerBackRow));
            Assert.That(grid.TryFindPosition(body, out int bodyRow, out _), Is.True);
            Assert.That(bodyRow, Is.EqualTo(BattleGrid.AttackerFrontRow));
        }

        [Test]
        public void BattleAi_Arrangement_PreservesAllHeroesInBackRow()
        {
            var owner = new PlayerSetupData { Nickname = "A" };
            var army = new ArmyData { Owner = owner, Name = "A" };
            var hero1 = new UnitData { Owner = owner, IsHero = true, FateMax = 3, HitPointsCurrent = 5, HitPointsMax = 5 };
            var hero2 = new UnitData { Owner = owner, IsHero = true, FateMax = 2, HitPointsCurrent = 5, HitPointsMax = 5 };
            var melee = Body(owner);
            melee.Range = 1;
            army.Members.Add(hero1);
            army.Members.Add(melee);
            army.Members.Add(hero2);

            BattleGrid grid = BattleGrid.FromArmies(army, null);
            BattleAi.ArrangeArmy(
                grid, army, BattleGrid.AttackerFrontRow, BattleGrid.AttackerBackRow,
                enemyArmy: null, magnitudes: AbilityMagnitudes.Default);

            Assert.That(grid.TryFindPosition(hero1, out int h1Row, out int h1Col), Is.True);
            Assert.That(grid.TryFindPosition(hero2, out int h2Row, out int h2Col), Is.True);
            Assert.That(h1Row, Is.EqualTo(BattleGrid.AttackerBackRow));
            Assert.That(h2Row, Is.EqualTo(BattleGrid.AttackerBackRow));
            Assert.That(h1Col, Is.Not.EqualTo(h2Col));
            Assert.That(grid.TryFindPosition(melee, out int meleeRow, out _), Is.True);
            Assert.That(meleeRow, Is.EqualTo(BattleGrid.AttackerFrontRow));
        }

        [Test]
        public void Hero_IsOrdinaryGroundCombatTarget_WhenInRange()
        {
            var a = new PlayerSetupData { Nickname = "A" };
            var b = new PlayerSetupData { Nickname = "B" };
            var attackerArmy = new ArmyData { Owner = a, Name = "A" };
            var defenderArmy = new ArmyData { Owner = b, Name = "B" };
            var attacker = new UnitData
            {
                Owner = a, Attack = 4, Defense = 1, Range = 4,
                HitPointsCurrent = 4, HitPointsMax = 4,
            };
            var hero = new UnitData
            {
                Owner = b, IsHero = true, Fate = 4, FateMax = 4,
                HitPointsCurrent = 6, HitPointsMax = 6,
            };
            attackerArmy.Members.Add(attacker);
            defenderArmy.Members.Add(hero);

            var grid = new BattleGrid();
            grid.Set(BattleGrid.AttackerFrontRow, 2, attacker);
            grid.Set(BattleGrid.DefenderBackRow, 2, hero);
            var state = new BattleState(default, attackerArmy, defenderArmy, grid, 123);
            var engine = new BattleEngine(state, AbilityMagnitudes.Default, new System.Random(1));

            Assert.That(engine.CanGroundAttack(attacker, hero), Is.True);

            bool found = BattleTargetSelector.TryChooseAttackTarget(
                grid, attacker, BattleGrid.AttackerFrontRow, 2, AbilityMagnitudes.Default,
                new List<UnitData> { attacker }, 0, out BattleAi.AiAction action);

            Assert.That(found, Is.True);
            Assert.That(action.Kind, Is.EqualTo(BattleAi.AiActionKind.Attack));
            Assert.That(action.Target, Is.SameAs(hero));
        }

        [Test]
        public void HeroTarget_RemainsPassiveInBattleTurnOrder()
        {
            var a = new PlayerSetupData { Nickname = "A" };
            var b = new PlayerSetupData { Nickname = "B" };
            var aa = new ArmyData { Owner = a, Name = "A" };
            var bb = new ArmyData { Owner = b, Name = "B" };
            var attacker = Body(a);
            var defender = Body(b);
            var hero = new UnitData
            {
                Owner = b, IsHero = true, Fate = 4, FateMax = 4,
                Initiative = 99, HitPointsCurrent = 5, HitPointsMax = 5,
            };
            aa.Members.Add(attacker);
            bb.Members.Add(defender);
            bb.Members.Add(hero);

            var grid = new BattleGrid();
            grid.Set(BattleGrid.AttackerFrontRow, 2, attacker);
            grid.Set(BattleGrid.DefenderFrontRow, 2, defender);
            grid.Set(BattleGrid.DefenderBackRow, 2, hero);

            List<UnitData> order = BattleTurnOrder.BuildOrder(grid, aa, bb, 123);

            CollectionAssert.Contains(order, attacker);
            CollectionAssert.Contains(order, defender);
            CollectionAssert.DoesNotContain(order, hero);
        }

        [Test]
        public void HeroGroundDefense_UsesFateMaxInsteadOfDefenseStat()
        {
            var attacker = new UnitData
            {
                Attack = 5, Range = 4, HitPointsCurrent = 4, HitPointsMax = 4,
            };
            var hero = new UnitData
            {
                IsHero = true, Defense = 0, Fate = 1, FateMax = 4,
                HitPointsCurrent = 6, HitPointsMax = 6,
            };
            var ordinary = new UnitData
            {
                Defense = 4, HitPointsCurrent = 6, HitPointsMax = 6,
            };

            BattleCombatOdds.ExchangeOdds heroOdds = BattleCombatOdds.Evaluate(
                attacker, hero, 0, AbilityMagnitudes.Default, hero.HitPointsCurrent);
            BattleCombatOdds.ExchangeOdds defenseFourOdds = BattleCombatOdds.Evaluate(
                attacker, ordinary, 0, AbilityMagnitudes.Default, ordinary.HitPointsCurrent);

            Assert.That(heroOdds.ExpectedDamage, Is.EqualTo(defenseFourOdds.ExpectedDamage).Within(0.00001f));
            Assert.That(heroOdds.HitProbability, Is.EqualTo(defenseFourOdds.HitProbability).Within(0.00001f));
        }

        [Test]
        public void KillingHeroWithGroundAttack_RemovesHeroFromGridAndArmy()
        {
            var a = new PlayerSetupData { Nickname = "A" };
            var b = new PlayerSetupData { Nickname = "B" };
            var attackerArmy = new ArmyData { Owner = a, Name = "A" };
            var defenderArmy = new ArmyData { Owner = b, Name = "B" };
            var attacker = Body(a);
            var hero = new UnitData
            {
                Owner = b, IsHero = true, Fate = 3, FateMax = 3,
                HitPointsCurrent = 2, HitPointsMax = 2,
            };
            attackerArmy.Members.Add(attacker);
            defenderArmy.Members.Add(hero);

            var grid = new BattleGrid();
            grid.Set(BattleGrid.AttackerFrontRow, 2, attacker);
            grid.Set(BattleGrid.DefenderBackRow, 2, hero);
            var state = new BattleState(default, attackerArmy, defenderArmy, grid, 123);
            var engine = new BattleEngine(state, AbilityMagnitudes.Default, new System.Random(1));

            BattleAttackApplication result = engine.ApplyGroundAttack(attacker, hero, 2);

            Assert.That(result.DefenderDied, Is.True);
            Assert.That(defenderArmy.Members.Contains(hero), Is.False);
            Assert.That(grid.TryFindPosition(hero, out _, out _), Is.False);
        }

        [Test]
        public void GuardedBackRow_GainsOneDefenseDie()
        {
            var owner = new PlayerSetupData { Nickname = "D" };
            var defenderArmy = new ArmyData { Owner = owner };
            var target = new UnitData
            {
                Owner = owner, Defense = 2, HitPointsCurrent = 4, HitPointsMax = 4,
            };
            var guard = new UnitData
            {
                Owner = owner, Defense = 3, HitPointsCurrent = 4, HitPointsMax = 4,
            };
            defenderArmy.Members.Add(target);
            defenderArmy.Members.Add(guard);

            var grid = new BattleGrid();
            grid.Set(BattleGrid.DefenderBackRow, 2, target);
            grid.Set(BattleGrid.DefenderFrontRow, 1, guard);

            Assert.That(BattleProtectionRules.GetGuardedDefenseBonus(grid, target, defenderArmy),
                Is.EqualTo(BattleProtectionRules.GuardedDefenseBonus));

            grid.Set(BattleGrid.DefenderFrontRow, 1, null);
            Assert.That(BattleProtectionRules.GetGuardedDefenseBonus(grid, target, defenderArmy), Is.Zero);
        }

        [Test]
        public void GuardedBonus_ReducesExactExpectedDamage()
        {
            var attacker = new UnitData { Attack = 4, Defense = 1, HitPointsCurrent = 4, HitPointsMax = 4 };
            var defender = new UnitData { Attack = 1, Defense = 3, HitPointsCurrent = 6, HitPointsMax = 6 };

            float open = BattleCombatOdds.Evaluate(attacker, defender, 0,
                AbilityMagnitudes.Default, defender.HitPointsCurrent).ExpectedDamage;
            float guarded = BattleCombatOdds.Evaluate(attacker, defender,
                BattleProtectionRules.GuardedDefenseBonus, AbilityMagnitudes.Default,
                defender.HitPointsCurrent).ExpectedDamage;

            Assert.That(guarded, Is.LessThan(open));
        }

        [Test]
        public void CaptureKill_TieMeansKilled()
        {
            Assert.That(BattleResolutionRules.ResolveCaptureKill(
                new[] { true, false }, new[] { true, false }), Is.EqualTo(CaptureKillOutcome.Killed));
        }

        [Test]
        public void InitiativeTieBreak_IsStableForSameSeed()
        {
            var a = new PlayerSetupData { Nickname = "A" };
            var b = new PlayerSetupData { Nickname = "B" };
            var aa = new ArmyData { Owner = a };
            var bb = new ArmyData { Owner = b };
            var u1 = new UnitData { Owner = a, Initiative = 1, HitPointsCurrent = 4, HitPointsMax = 4 };
            var u2 = new UnitData { Owner = b, Initiative = 1, HitPointsCurrent = 4, HitPointsMax = 4 };
            aa.Members.Add(u1);
            bb.Members.Add(u2);
            var grid = new BattleGrid();
            grid.Set(BattleGrid.AttackerFrontRow, 0, u1);
            grid.Set(BattleGrid.DefenderFrontRow, 0, u2);

            List<UnitData> first = BattleTurnOrder.BuildOrder(grid, aa, bb, 12345);
            List<UnitData> second = BattleTurnOrder.BuildOrder(grid, aa, bb, 12345);

            CollectionAssert.AreEqual(first, second);
            Assert.That(first.Count, Is.EqualTo(2));
        }

        [Test]
        public void ShockAttack_RemovesPendingTargetTurn()
        {
            BuildSimpleBattle(out BattleState state, out BattleEngine engine,
                out UnitData attacker, out UnitData defender, out _);
            attacker.Abilities.Add(UnitAbilities.ShockAttack);
            state.TurnOrder = new List<UnitData> { attacker, defender };
            state.TurnIndex = 0;

            BattleAttackApplication result = engine.ApplyGroundAttack(attacker, defender, 1);

            Assert.That(result.Shocked, Is.True);
            CollectionAssert.DoesNotContain(state.TurnOrder, defender);
            Assert.That(defender.HitPointsCurrent, Is.EqualTo(3));
        }

        [Test]
        public void DeathBeforeTurn_RemovesUnitFromQueueAndArmy()
        {
            BuildSimpleBattle(out BattleState state, out BattleEngine engine,
                out UnitData attacker, out UnitData defender, out ArmyData defenderArmy);
            defender.HitPointsCurrent = 1;
            var later = new UnitData
            {
                Owner = defender.Owner, Attack = 1, Defense = 1, Range = 2,
                HitPointsCurrent = 4, HitPointsMax = 4,
            };
            defenderArmy.Members.Add(later);
            state.Grid.Set(BattleGrid.DefenderFrontRow, 3, later);
            state.TurnOrder = new List<UnitData> { attacker, defender, later };
            state.TurnIndex = 0;

            BattleAttackApplication result = engine.ApplyGroundAttack(attacker, defender, 1);

            Assert.That(result.DefenderDied, Is.True);
            CollectionAssert.DoesNotContain(state.TurnOrder, defender);
            CollectionAssert.DoesNotContain(defenderArmy.Members, defender);
            CollectionAssert.Contains(state.TurnOrder, later);
            Assert.That(state.Grid.TryFindPosition(defender, out _, out _), Is.False);
        }

        [Test]
        public void BerserkLifecycle_StacksOnHitAndRevertsAtBattleEnd()
        {
            BuildSimpleBattle(out _, out BattleEngine engine,
                out UnitData attacker, out UnitData defender, out ArmyData defenderArmy);
            defender.Attack = 2;
            defender.Defense = 2;
            defender.Abilities.Add(UnitAbilities.Berserk);

            engine.ApplyGroundAttack(attacker, defender, 1);

            Assert.That(defender.Attack, Is.EqualTo(3));
            Assert.That(defender.Defense, Is.EqualTo(1));
            Assert.That(defender.BerserkStacks, Is.EqualTo(1));

            BattleEngine.RevertTemporaryBattleStats(defenderArmy);

            Assert.That(defender.Attack, Is.EqualTo(2));
            Assert.That(defender.Defense, Is.EqualTo(2));
            Assert.That(defender.BerserkStacks, Is.Zero);
            Assert.That(defender.BerserkDefenseLost, Is.Zero);
        }

        [Test]
        public void Splash_HitsOnlyOrthogonalNeighbours()
        {
            BuildSimpleBattle(out BattleState state, out BattleEngine engine,
                out UnitData attacker, out UnitData defender, out ArmyData defenderArmy);
            attacker.Abilities.Add(UnitAbilities.Splash);

            var left = Body(defender.Owner, hp: 5);
            var right = Body(defender.Owner, hp: 5);
            var far = Body(defender.Owner, hp: 5);
            defenderArmy.Members.Add(left);
            defenderArmy.Members.Add(right);
            defenderArmy.Members.Add(far);
            state.Grid.Set(BattleGrid.DefenderFrontRow, 1, left);
            state.Grid.Set(BattleGrid.DefenderFrontRow, 3, right);
            state.Grid.Set(BattleGrid.DefenderBackRow, 0, far);

            BattleAttackApplication result = engine.ApplyGroundAttack(attacker, defender, 4);

            Assert.That(result.SecondaryHits.Count, Is.EqualTo(2));
            Assert.That(left.HitPointsCurrent, Is.EqualTo(3));
            Assert.That(right.HitPointsCurrent, Is.EqualTo(3));
            Assert.That(far.HitPointsCurrent, Is.EqualTo(5));
        }

        [Test]
        public void Scorcher_RandomNonBioPickMissesEvenWhenAdjacentBioExists()
        {
            BuildSimpleBattle(out BattleState state, out _, out UnitData attacker,
                out UnitData defender, out ArmyData defenderArmy);
            attacker.Abilities.Add(UnitAbilities.Scorcher);

            var adjacentNonBio = Body(defender.Owner, hp: 5);
            var adjacentBio = Body(defender.Owner, hp: 5);
            adjacentBio.TypeTags.Add(UnitTypeTag.Bio);
            defenderArmy.Members.Add(adjacentNonBio);
            defenderArmy.Members.Add(adjacentBio);

            // Neighbour enumeration is up/down/left/right. Put non-Bio first and force random
            // index 0: Scorcher must miss rather than searching the remaining Bio neighbour.
            state.Grid.Set(BattleGrid.DefenderBackRow, 2, adjacentNonBio);
            state.Grid.Set(BattleGrid.DefenderFrontRow, 1, adjacentBio);
            var engine = new BattleEngine(state, AbilityMagnitudes.Default, new SequenceRandom(0d));

            BattleAttackApplication result = engine.ApplyGroundAttack(attacker, defender, 4);

            Assert.That(result.SecondaryHits.Count, Is.Zero);
            Assert.That(adjacentNonBio.HitPointsCurrent, Is.EqualTo(5));
            Assert.That(adjacentBio.HitPointsCurrent, Is.EqualTo(5));
        }

        [Test]
        public void SecondaryTargetKernel_SplashThenScorcherNeverDoubleTargets()
        {
            List<BattleSimSecondaryTarget> selected =
                BattleSimulationKernel.SelectSecondaryTargets(
                    candidateCount: 3,
                    isBio: i => i == 2,
                    splash: true,
                    scorcher: true,
                    rng: null);

            Assert.That(selected.Count, Is.EqualTo(3));
            Assert.That(selected[0].Index, Is.EqualTo(0));
            Assert.That(selected[0].Skill, Is.EqualTo("Splash"));
            Assert.That(selected[1].Index, Is.EqualTo(1));
            Assert.That(selected[1].Skill, Is.EqualTo("Splash"));
            Assert.That(selected[2].Index, Is.EqualTo(2));
            Assert.That(selected[2].Skill, Is.EqualTo("Scorcher"));
        }

        [Test]
        public void SimulationKernel_FateIsDepletedBySameDuelPolicy()
        {
            int attackerFate = 2;
            int defenderFate = 0;
            var rng = new SequenceRandom(0.9d, 0.9d);

            BattleSimulationKernel.ResolveExchange(
                1, 0, Array.Empty<string>(), Array.Empty<UnitTypeTag>(), Array.Empty<string>(),
                ref attackerFate, ref defenderFate, 4, AbilityMagnitudes.Default, rng);

            Assert.That(attackerFate, Is.EqualTo(1),
                "An initial miss followed by a failed reroll must consume exactly one Fate.");
            Assert.That(defenderFate, Is.Zero);
        }

        [Test]
        public void BaseDefense_AppliesOnlyToBaseOwnersDefendingArmy()
        {
            BuildSimpleBattle(out BattleState state, out BattleEngine engine,
                out UnitData attacker, out UnitData defender, out _);
            BuildingRegistry.Clear();
            try
            {
                var foreignBase = new BuildingData
                {
                    Hex = state.BattleHex,
                    Owner = attacker.Owner,
                    IsBase = true,
                    Defense = 4,
                    Name = "Foreign Base",
                };
                BuildingRegistry.Register(state.BattleHex, foreignBase);

                BattleDefenseBreakdown foreign = engine.GetDefenseBreakdown(defender, map: null);
                Assert.That(foreign.ConstructionBonus, Is.Zero,
                    "the tactical defender must not receive another owner's Base defense");

                foreignBase.Owner = defender.Owner;
                BattleDefenseBreakdown owned = engine.GetDefenseBreakdown(defender, map: null);
                Assert.That(owned.ConstructionBonus, Is.EqualTo(4));
            }
            finally
            {
                BuildingRegistry.Clear();
            }
        }

        [Test]
        public void TerrainAndGuarded_DefenseBonusesStackOnce()
        {
            var owner = new PlayerSetupData { Nickname = "D" };
            var defenderArmy = new ArmyData { Owner = owner };
            var target = Body(owner, hp: 5);
            var guard = Body(owner, hp: 5);
            defenderArmy.Members.Add(target);
            defenderArmy.Members.Add(guard);
            var grid = new BattleGrid();
            grid.Set(BattleGrid.DefenderBackRow, 2, target);
            grid.Set(BattleGrid.DefenderFrontRow, 2, guard);

            int combined = BattleProtectionRules.GetTotalDefenseBonus(
                grid, target, defenderArmy, defenderHexBonus: 2);

            Assert.That(combined, Is.EqualTo(3));
            float terrainOnly = BattleCombatOdds.Evaluate(
                new UnitData { Attack = 5 }, target, 2, AbilityMagnitudes.Default, 5).ExpectedDamage;
            float terrainAndGuard = BattleCombatOdds.Evaluate(
                new UnitData { Attack = 5 }, target, combined, AbilityMagnitudes.Default, 5).ExpectedDamage;
            Assert.That(terrainAndGuard, Is.LessThan(terrainOnly));
        }

        [Test]
        public void ChallengeFate_IsCommittedOnlyByEngine()
        {
            BuildSimpleBattle(out _, out BattleEngine engine,
                out UnitData attacker, out UnitData defender, out _);
            var attackerHero = new UnitData { IsHero = true, Fate = 4, FateMax = 4 };
            var defenderHero = new UnitData { IsHero = true, Fate = 3, FateMax = 3 };
            var roll = new BattleChallengeRollResult(
                new[] { true }, new[] { false }, attackerFateRemaining: 2, defenderFateRemaining: 1);

            // Constructing/transporting the UI result must not mutate domain Fate.
            Assert.That(attackerHero.Fate, Is.EqualTo(4));
            Assert.That(defenderHero.Fate, Is.EqualTo(3));

            engine.ResolveAndApplyGroundAttack(
                attacker, defender, roll, attackerHero, defenderHero);

            Assert.That(attackerHero.Fate, Is.EqualTo(2));
            Assert.That(defenderHero.Fate, Is.EqualTo(1));
        }

        [Test]
        public void PrimaryHit_EngineAndSimulationKernelKeepShockAndBerserkInParity()
        {
            BuildSimpleBattle(out BattleState state, out BattleEngine engine,
                out UnitData attacker, out UnitData defender, out _);
            attacker.Abilities.Add(UnitAbilities.ShockAttack);
            defender.Abilities.Add(UnitAbilities.Berserk);
            defender.Attack = 2;
            defender.Defense = 2;
            state.TurnOrder = new List<UnitData> { attacker, defender };
            state.TurnIndex = 0;

            BattleAttackApplication live = engine.ApplyGroundAttack(attacker, defender, 1);

            float simHp = 4f;
            int simAttack = 2;
            int simDefense = 2;
            var simulatedOutcome = new BattleSimExchangeOutcome(
                damage: 1, hit: true, attackerSuccesses: 1, defenderSuccesses: 0);
            BattleSimulationKernel.ApplyPrimaryOutcome(
                simulatedOutcome, attacker.Abilities, defender.Abilities,
                ref simHp, ref simAttack, ref simDefense,
                AbilityMagnitudes.Default, out bool simSuppresses);

            Assert.That(simHp, Is.EqualTo(defender.HitPointsCurrent));
            Assert.That(simAttack, Is.EqualTo(defender.Attack));
            Assert.That(simDefense, Is.EqualTo(defender.Defense));
            Assert.That(simSuppresses, Is.EqualTo(live.Shocked));
        }

        [Test]
        public void Guarded_HeroInBackRow_ReceivesFormationDefense()
        {
            var owner = new PlayerSetupData { Nickname = "A" };
            var army = new ArmyData { Owner = owner, Name = "A" };
            var hero = new UnitData { Owner = owner, IsHero = true, FateMax = 4, HitPointsCurrent = 5, HitPointsMax = 5 };
            var guard = Body(owner);
            army.Members.Add(hero);
            army.Members.Add(guard);
            var grid = new BattleGrid();
            grid.Set(BattleGrid.AttackerBackRow, 2, hero);
            grid.Set(BattleGrid.AttackerFrontRow, 2, guard);

            Assert.That(BattleProtectionRules.GetGuardedDefenseBonus(grid, hero, battleDefender: null),
                Is.EqualTo(BattleProtectionRules.GuardedDefenseBonus));
        }

        [Test]
        public void TurnOrder_UsesNextLivingHeroAfterCommanderLeavesGrid()
        {
            var owner = new PlayerSetupData { Nickname = "A" };
            var enemyOwner = new PlayerSetupData { Nickname = "D" };
            var army = new ArmyData { Owner = owner, Name = "A" };
            var enemy = new ArmyData { Owner = enemyOwner, Name = "D" };
            var commander = new UnitData { Owner = owner, IsHero = true, Initiative = 5, HitPointsCurrent = 5, HitPointsMax = 5 };
            var secondHero = new UnitData { Owner = owner, IsHero = true, Initiative = 2, HitPointsCurrent = 5, HitPointsMax = 5 };
            var body = Body(owner);
            body.Initiative = 3;
            army.Members.Add(commander);
            army.Members.Add(secondHero);
            army.Members.Add(body);
            var grid = new BattleGrid();
            grid.Set(BattleGrid.AttackerBackRow, 0, commander);
            grid.Set(BattleGrid.AttackerBackRow, 1, secondHero);
            grid.Set(BattleGrid.AttackerFrontRow, 0, body);

            Assert.That(BattleTurnOrder.LivingCommanderOnGrid(grid, army), Is.SameAs(commander));
            grid.Set(BattleGrid.AttackerBackRow, 0, null);
            Assert.That(BattleTurnOrder.LivingCommanderOnGrid(grid, army), Is.SameAs(secondHero));
        }

        [Test]
        public void WorthIt_LiveHeroProfile_IsPassiveTargetWithFateDefense()
        {
            var owner = new PlayerSetupData { Nickname = "A" };
            var hero = new UnitData
            {
                Owner = owner, IsHero = true, Defense = 99, FateMax = 4, Fate = 4,
                Attack = 0, Initiative = 3, HitPointsCurrent = 5, HitPointsMax = 5,
            };

            WorthIt.DefenderProfile profile = WorthIt.FromLiveUnit(hero);

            Assert.That(profile.IsHero, Is.True);
            Assert.That(profile.IsGroundCombatant, Is.False);
            Assert.That(profile.Defense, Is.EqualTo(4),
                "hero tactical defense must come from FateMax, never the ordinary Defense stat");
            Assert.That(profile.FateMax, Is.EqualTo(4));
        }

        [Test]
        public void FinalizeEncounter_HeroPlusSummons_PreservesHeroArmyAsSurvivor()
        {
            var attackerOwner = new PlayerSetupData { Nickname = "A" };
            var defenderOwner = new PlayerSetupData { Nickname = "D" };
            var attackerArmy = new ArmyData { Owner = attackerOwner, Name = "A" };
            var defenderArmy = new ArmyData { Owner = defenderOwner, Name = "D" };
            var hero = new UnitData
            {
                Owner = attackerOwner, IsHero = true, Fate = 3, FateMax = 3,
                HitPointsCurrent = 5, HitPointsMax = 5,
            };
            var summon = Body(attackerOwner);
            summon.IsSummoned = true;
            attackerArmy.Members.Add(hero);
            attackerArmy.Members.Add(summon);

            var grid = new BattleGrid();
            grid.Set(BattleGrid.AttackerBackRow, 0, hero);
            grid.Set(BattleGrid.AttackerFrontRow, 0, summon);
            var state = new BattleState(default, attackerArmy, defenderArmy, grid, 123);

            BattleEncounterFinalization result =
                BattleEngine.FinalizeEncounter(state, hexSelectionController: null);

            Assert.That(result.Survivor, Is.SameAs(attackerArmy));
            Assert.That(result.AttackerHere, Is.True);
            Assert.That(result.DefenderHere, Is.False);
            Assert.That(attackerArmy.Members.Count, Is.EqualTo(1));
            Assert.That(attackerArmy.Members[0], Is.SameAs(hero));
            Assert.That(grid.TryFindPosition(summon, out _, out _), Is.False);
        }

        [Test]
        public void FinalizeEncounter_SummonsOnly_DoesNotCreatePersistentSurvivor()
        {
            var attackerOwner = new PlayerSetupData { Nickname = "A" };
            var defenderOwner = new PlayerSetupData { Nickname = "D" };
            var attackerArmy = new ArmyData { Owner = attackerOwner, Name = "A" };
            var defenderArmy = new ArmyData { Owner = defenderOwner, Name = "D" };
            var summon = Body(attackerOwner);
            summon.IsSummoned = true;
            attackerArmy.Members.Add(summon);

            var grid = new BattleGrid();
            grid.Set(BattleGrid.AttackerFrontRow, 0, summon);
            var state = new BattleState(default, attackerArmy, defenderArmy, grid, 123);

            BattleEncounterFinalization result =
                BattleEngine.FinalizeEncounter(state, hexSelectionController: null);

            Assert.That(result.Survivor, Is.Null);
            Assert.That(result.AttackerHere, Is.False);
            Assert.That(attackerArmy.Members, Is.Empty);
            Assert.That(grid.TryFindPosition(summon, out _, out _), Is.False);
        }

        [Test]
        public void CaptureKillSequence_RetreatsOnlyAfterLastHeroOfArmy()
        {
            var hunterOwner = new PlayerSetupData { Nickname = "Hunter" };
            var targetOwner = new PlayerSetupData { Nickname = "Target" };
            var hunter = new ArmyData { Owner = hunterOwner, Name = "Hunter" };
            var target = new ArmyData { Owner = targetOwner, Name = "Target" };
            var hunterBody = Body(hunterOwner);
            var hero1 = new UnitData
            {
                Owner = targetOwner, IsHero = true, Fate = 1, FateMax = 1,
                HitPointsCurrent = 6, HitPointsMax = 6,
            };
            var hero2 = new UnitData
            {
                Owner = targetOwner, IsHero = true, Fate = 1, FateMax = 1,
                HitPointsCurrent = 6, HitPointsMax = 6,
            };
            hunter.Members.Add(hunterBody);
            target.Members.Add(hero1);
            target.Members.Add(hero2);

            var state = new BattleState(default, hunter, target, new BattleGrid(), 77);
            BattleCaptureKillSequence sequence =
                BattleEngine.CreateTargetOnlyCaptureKillSequence(state, hunter, target);

            // 0 attacker hits vs 1 defender hit => Escaped.
            var escape = new BattleChallengeRollResult(
                new[] { false }, new[] { true }, 0, 0);

            BattleCaptureKillStep first = sequence.ResolveCurrent(
                escape, map: null, hexSelectionController: null);
            Assert.That(first.Application.EffectiveOutcome, Is.EqualTo(CaptureKillOutcome.Escaped));
            Assert.That(first.Retreat.Destroyed, Is.False,
                "retreat must not resolve while another hero from the same army is still pending");
            Assert.That(target.Members.Count, Is.EqualTo(2));

            BattleCaptureKillStep second = sequence.ResolveCurrent(
                escape, map: null, hexSelectionController: null);
            Assert.That(second.Application.EffectiveOutcome, Is.EqualTo(CaptureKillOutcome.Escaped));
            Assert.That(second.Retreat.Destroyed, Is.True,
                "after the last hero resolves, a hero-only army must complete its retreat consequence");
        }

        [Test]
        public void WorthItSequentialCoverage_UsesPerArmyDefenseOverride()
        {
            var attacker = new WorthIt.DefenderProfile(
                defense: 1, hasCeramicArmor: false, attack: 4, hitPoints: 4);
            var defender = new WorthIt.DefenderProfile(
                defense: 2, hasCeramicArmor: false, attack: 1, hitPoints: 4);
            var opposition = new[]
            {
                new WorthIt.DefendingArmy(
                    new[] { defender }, default, defenseBonusOverride: 0f),
            };

            Assert.That(WorthIt.CanDamageAll(
                new[] { attacker }, opposition, fallbackDefenseBonus: 8f), Is.True,
                "an exact per-army bonus must override the shared site fallback");
            Assert.That(WorthIt.CanDamageAll(
                new[] { attacker }, new[] { defender }, extraDefense: 8f), Is.False,
                "the same body would be blocked if the foreign Base bonus were incorrectly shared");
        }

        [Test]
        public void WorthItCoverage_UsesProvidedAbilityMagnitudes()
        {
            var attacker = new WorthIt.DefenderProfile(
                defense: 1, hasCeramicArmor: false, attack: 4, hitPoints: 4);
            var defender = new WorthIt.DefenderProfile(
                defense: 2, hasCeramicArmor: true, attack: 1, hitPoints: 4);

            var noArmorReduction = new AbilityMagnitudes(
                criticalDamageMultiplier: 2f,
                hyperkineticBonusDamage: 2,
                ceramicArmorReduction: 0,
                pyrokineticBonusDamage: 2,
                berserkAttackGain: 1,
                berserkDefenseLoss: 1);

            Assert.That(WorthIt.CanDamage(attacker, defender, 0f, noArmorReduction), Is.True);
            Assert.That(WorthIt.CanDamage(attacker, defender, 0f, AbilityMagnitudes.Default), Is.False);
        }

        [Test]
        public void BerserkRevert_UsesAuthoredAttackGain()
        {
            var owner = new PlayerSetupData { Nickname = "D" };
            var army = new ArmyData { Owner = owner };
            var unit = Body(owner, hp: 5);
            unit.Attack = 2;
            unit.Defense = 4;
            unit.Abilities.Add(UnitAbilities.Berserk);
            army.Members.Add(unit);

            var magnitudes = new AbilityMagnitudes(
                criticalDamageMultiplier: 2f,
                hyperkineticBonusDamage: 2,
                ceramicArmorReduction: 1,
                pyrokineticBonusDamage: 2,
                berserkAttackGain: 3,
                berserkDefenseLoss: 2);

            ChallengeResult.ApplyBerserkOnHit(unit, magnitudes);
            Assert.That(unit.Attack, Is.EqualTo(5));
            Assert.That(unit.Defense, Is.EqualTo(2));

            BattleEngine.RevertTemporaryBattleStats(army, magnitudes);

            Assert.That(unit.Attack, Is.EqualTo(2));
            Assert.That(unit.Defense, Is.EqualTo(4));
            Assert.That(unit.BerserkStacks, Is.Zero);
            Assert.That(unit.BerserkDefenseLost, Is.Zero);
        }

        [Test]
        public void RangeOneAi_ClosesDistanceImmediatelyWhenLegal()
        {
            var a = new PlayerSetupData { Nickname = "A" };
            var d = new PlayerSetupData { Nickname = "D" };
            var ownArmy = new ArmyData { Owner = a, Name = "Melee" };
            var enemyArmy = new ArmyData { Owner = d, Name = "Enemy" };
            UnitData melee = Body(a);
            melee.Range = 1;
            UnitData enemy = Body(d);
            enemy.Range = 4;
            ownArmy.Members.Add(melee);
            enemyArmy.Members.Add(enemy);

            var grid = new BattleGrid();
            grid.Set(BattleGrid.AttackerBackRow, 2, melee);
            grid.Set(BattleGrid.DefenderFrontRow, 4, enemy);

            int before = Math.Abs(BattleGrid.AttackerBackRow - BattleGrid.DefenderFrontRow)
                + Math.Abs(2 - 4);
            var waitStreak = new Dictionary<UnitData, int>();

            BattleAi.AiAction action = BattleAi.ChooseAction(
                grid, melee, waitStreak, ownArmy, enemyArmy, AbilityMagnitudes.Default,
                new List<UnitData> { melee, enemy }, 0);

            Assert.That(action.Kind, Is.EqualTo(BattleAi.AiActionKind.Move));
            int after = Math.Abs(action.Row - BattleGrid.DefenderFrontRow)
                + Math.Abs(action.Col - 4);
            Assert.That(after, Is.LessThan(before),
                "a Range-1 unit that stays in the fight must take a real closing step whenever one exists");
        }

        [Test]
        public void RangeOneAi_DoesNotWaitToAvoidExposure()
        {
            var a = new PlayerSetupData { Nickname = "A" };
            var d = new PlayerSetupData { Nickname = "D" };
            var ownArmy = new ArmyData { Owner = a, Name = "Melee" };
            var enemyArmy = new ArmyData { Owner = d, Name = "Ranged" };
            UnitData melee = Body(a);
            melee.Range = 1;
            UnitData enemy = Body(d);
            enemy.Range = 2;
            ownArmy.Members.Add(melee);
            enemyArmy.Members.Add(enemy);

            var grid = new BattleGrid();
            grid.Set(BattleGrid.AttackerBackRow, 2, melee);
            grid.Set(BattleGrid.DefenderFrontRow, 2, enemy);

            var waitStreak = new Dictionary<UnitData, int>();
            BattleAi.AiAction action = BattleAi.ChooseAction(
                grid, melee, waitStreak, ownArmy, enemyArmy, AbilityMagnitudes.Default,
                new List<UnitData> { melee, enemy }, 0);

            Assert.That(action.Kind, Is.EqualTo(BattleAi.AiActionKind.Move),
                "exposure caution must not make a committed Range-1 fighter wait instead of closing");
            Assert.That(action.Row, Is.LessThan(BattleGrid.AttackerBackRow));
        }

        [Test]
        public void RangeOneAi_AttacksWhenTargetIsAlreadyReachable()
        {
            var a = new PlayerSetupData { Nickname = "A" };
            var d = new PlayerSetupData { Nickname = "D" };
            var ownArmy = new ArmyData { Owner = a, Name = "Melee" };
            var enemyArmy = new ArmyData { Owner = d, Name = "Enemy" };
            UnitData melee = Body(a);
            melee.Range = 1;
            UnitData enemy = Body(d);
            ownArmy.Members.Add(melee);
            enemyArmy.Members.Add(enemy);

            var grid = new BattleGrid();
            grid.Set(BattleGrid.NeutralRow, 2, melee);
            grid.Set(BattleGrid.DefenderFrontRow, 2, enemy);

            var waitStreak = new Dictionary<UnitData, int>();
            BattleAi.AiAction action = BattleAi.ChooseAction(
                grid, melee, waitStreak, ownArmy, enemyArmy, AbilityMagnitudes.Default,
                new List<UnitData> { melee, enemy }, 0);

            Assert.That(action.Kind, Is.EqualTo(BattleAi.AiActionKind.Attack));
            Assert.That(action.Target, Is.SameAs(enemy));
        }

        [Test]
        public void ChallengeSession_FailedFateRerollEndsCurrentSpendChain()
        {
            int rollCall = 0;
            bool[] Roll(int count)
            {
                rollCall++;
                if (count <= 0)
                    return Array.Empty<bool>();
                // Initial attacker die misses, defender has no dice, then the Fate reroll misses.
                if (rollCall == 1)
                    return new[] { false };
                if (rollCall == 2)
                    return Array.Empty<bool>();
                return new[] { false };
            }

            var session = new BattleChallengeSession(
                BattleChallengeMode.GroundCombat,
                attacker: null, defender: null,
                attackerPoolSize: 1, defenderPoolSize: 0,
                attackerFate: 2, defenderFate: 0,
                AbilityMagnitudes.Default,
                rollDice: Roll);

            session.Roll();
            Assert.That(session.TrySpend(false, out int rerolledIndex, out bool hit), Is.True);
            Assert.That(rerolledIndex, Is.EqualTo(0));
            Assert.That(hit, Is.False);
            Assert.That(session.LastSpendHit, Is.False);
            Assert.That(session.AttackerFateRemaining, Is.EqualTo(1));
        }

        [Test]
        public void ChallengeSession_OwnsDefenderFirstFateOrder()
        {
            var session = new BattleChallengeSession(
                BattleChallengeMode.GroundCombat,
                attacker: null, defender: null,
                attackerPoolSize: 1, defenderPoolSize: 1,
                attackerFate: 1, defenderFate: 1,
                AbilityMagnitudes.Default);

            Assert.That(session.TryNextFateTurn(out bool defenderFirst), Is.True);
            Assert.That(defenderFirst, Is.True);
            session.ReportFateTurn(spent: false);

            Assert.That(session.TryNextFateTurn(out bool attackerSecond), Is.True);
            Assert.That(attackerSecond, Is.False);
            session.ReportFateTurn(spent: false);

            Assert.That(session.TryNextFateTurn(out _), Is.False);
        }

        private sealed class SequenceRandom : System.Random
        {
            private readonly Queue<double> _values;

            public SequenceRandom(params double[] values)
            {
                _values = new Queue<double>(values);
            }

            protected override double Sample() =>
                _values.Count > 0 ? _values.Dequeue() : 0.9d;
        }

        private static UnitData Body(PlayerSetupData owner, int hp = 4) => new UnitData
        {
            Owner = owner,
            Attack = 3,
            Defense = 1,
            Range = 2,
            Initiative = 1,
            HitPointsCurrent = hp,
            HitPointsMax = hp,
        };

        private static void BuildSimpleBattle(out BattleState state, out BattleEngine engine,
            out UnitData attacker, out UnitData defender, out ArmyData defenderArmy)
        {
            var a = new PlayerSetupData { Nickname = "A" };
            var d = new PlayerSetupData { Nickname = "D" };
            var attackerArmy = new ArmyData { Owner = a, Name = "A" };
            defenderArmy = new ArmyData { Owner = d, Name = "D" };
            attacker = Body(a);
            defender = Body(d);
            attackerArmy.Members.Add(attacker);
            defenderArmy.Members.Add(defender);

            var grid = new BattleGrid();
            grid.Set(BattleGrid.AttackerFrontRow, 2, attacker);
            grid.Set(BattleGrid.DefenderFrontRow, 2, defender);
            state = new BattleState(default, attackerArmy, defenderArmy, grid, 123);
            engine = new BattleEngine(state, AbilityMagnitudes.Default, new System.Random(1));
        }
    }
}
#endif
