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
    // Ionic / Bodyguard / Counterattack / Drill: the live BattleEngine rules, and the same rules
    // reaching the shared projections (BattleTargetSelector, WorthIt).
    public sealed class CombatReactionSkillsTests
    {
        private static readonly PlayerSetupData Own = new PlayerSetupData();
        private static readonly PlayerSetupData Foe = new PlayerSetupData();

        private sealed class CountingRandom : Random
        {
            public int Draws;
            public CountingRandom() : base(42) { }
            public override int Next(int maxValue) { Draws++; return base.Next(maxValue); }
        }

        private static UnitData Body(PlayerSetupData owner, int attack = 2, int defense = 1, int hp = 6,
            params string[] abilities)
        {
            var unit = new UnitData
            {
                Owner = owner, Attack = attack, Defense = defense,
                HitPointsCurrent = hp, HitPointsMax = hp, Range = 1, Initiative = 1,
            };
            foreach (string a in abilities) unit.Abilities.Add(a);
            return unit;
        }

        private static ArmyData Army(PlayerSetupData owner, params UnitData[] members)
        {
            var army = new ArmyData { Owner = owner };
            army.Members.AddRange(members);
            return army;
        }

        // `placements`: unit -> (row, col). Own army is the battle attacker, Foe the defender.
        private static BattleEngine Engine(ArmyData own, ArmyData foe,
            params (UnitData unit, int row, int col)[] placements)
        {
            var grid = new BattleGrid();
            foreach ((UnitData unit, int row, int col) p in placements)
                grid.Set(p.row, p.col, p.unit);
            return new BattleEngine(new BattleState(default, own, foe, grid, 42),
                AbilityMagnitudes.Default, new Random(42));
        }

        // Attacker successes - defender successes = `raw` (no Fate on either side).
        private static BattleChallengeRollResult Roll(int raw) =>
            new BattleChallengeRollResult(new bool[raw].Populate(true), new bool[0], 0, 0);

        [TestCase(0, false)]
        [TestCase(1, true)]
        public void PresentationBerserkFactRecordsAnAppliedPositiveHit(int damage, bool expected)
        {
            var attacker = Body(Own);
            var victim = Body(Foe, abilities: new[] { UnitAbilities.Berserk });
            var engine = Engine(Army(Own, attacker), Army(Foe, victim), (attacker, 2, 1), (victim, 1, 1));
            var hit = engine.ApplyGroundAttack(attacker, victim, damage);
            Assert.That(hit.BerserkTriggered, Is.EqualTo(expected));
        }

        [Test]
        public void PresentationShockFactIncludesCounterSuppressionWithoutPendingTurn()
        {
            var attacker = Body(Own, abilities: new[] { UnitAbilities.ShockAttack });
            var victim = Body(Foe, abilities: new[] { UnitAbilities.Counterattack });
            var engine = Engine(Army(Own, attacker), Army(Foe, victim), (attacker, 2, 1), (victim, 1, 1));
            var hit = engine.ResolveAndApplyGroundAttack(attacker, victim, Roll(1));
            Assert.That(hit.Shocked, Is.False);
            Assert.That(hit.ShockTriggered, Is.True);
            Assert.That(hit.Counterattacker, Is.Null);
        }

        [TestCase(0)]
        [TestCase(6)]
        public void PresentationShockDoesNotFlashForMissOrDestroyedVictim(int raw)
        {
            var attacker = Body(Own, abilities: new[] { UnitAbilities.ShockAttack });
            var victim = Body(Foe, abilities: new[] { UnitAbilities.Counterattack });
            var engine = Engine(Army(Own, attacker), Army(Foe, victim), (attacker, 2, 1), (victim, 1, 1));
            var hit = engine.ResolveAndApplyGroundAttack(attacker, victim, Roll(raw));
            Assert.That(hit.ShockTriggered, Is.False);
        }

        [Test]
        public void PresentationCollateralArmorAbsorptionDoesNotTriggerBerserk()
        {
            var attacker = Body(Own, abilities: new[] { UnitAbilities.Splash });
            var victim = Body(Foe);
            var neighbour = Body(Foe, abilities: new[] { UnitAbilities.Berserk, UnitAbilities.CeramicArmor });
            var engine = Engine(Army(Own, attacker), Army(Foe, victim, neighbour),
                (attacker, 2, 1), (victim, 1, 1), (neighbour, 1, 2));
            var hit = engine.ApplyGroundAttack(attacker, victim, 2);
            Assert.That(hit.SecondaryHits.Count, Is.EqualTo(1));
            Assert.That(hit.SecondaryHits[0].Damage, Is.Zero);
            Assert.That(hit.SecondaryHits[0].BerserkTriggered, Is.False);
        }

        [Test]
        public void HitEffectSummary_ListsEffectSkillsWithoutChangingCombatState()
        {
            UnitData attacker = Body(Own, abilities: new[]
                { UnitAbilities.ShockAttack, UnitAbilities.Splash, UnitAbilities.Scorcher });
            UnitData victim = Body(Foe, abilities: new[] { UnitAbilities.Berserk });
            var skills = new List<string> { UnitAbilities.CriticalDamage };
            BattleRules.AppendHitEffectAbilities(skills, attacker, victim, 2, new[]
                { UnitAbilities.ShockAttack, UnitAbilities.Splash, UnitAbilities.Scorcher });
            CollectionAssert.AreEquivalent(new[] { UnitAbilities.CriticalDamage,
                UnitAbilities.ShockAttack, UnitAbilities.Splash, UnitAbilities.Scorcher,
                UnitAbilities.Berserk }, skills);
            Assert.That(victim.HitPointsCurrent, Is.EqualTo(6));
            Assert.That(victim.BerserkStacks, Is.Zero);
        }

        [TestCase(0)]
        [TestCase(-1)]
        public void HitEffectSummary_MissesAndAbsorbedHitsDoNotListHitEffects(int damage)
        {
            UnitData attacker = Body(Own, abilities: new[]
                { UnitAbilities.ShockAttack, UnitAbilities.Splash, UnitAbilities.Scorcher });
            UnitData victim = Body(Foe, abilities: new[] { UnitAbilities.Berserk });
            var skills = new List<string> { UnitAbilities.CeramicArmor };
            BattleRules.AppendHitEffectAbilities(skills, attacker, victim, damage, new[] { UnitAbilities.ShockAttack, UnitAbilities.Splash });
            CollectionAssert.AreEqual(new[] { UnitAbilities.CeramicArmor }, skills);
        }

        [Test]
        public void HitEffectSummary_UsesResolvedGroundEffectsAndFieldCombatOnlyAddsBerserk()
        {
            UnitData attacker = Body(Own, abilities: new[]
                { UnitAbilities.ShockAttack, UnitAbilities.Splash, UnitAbilities.Scorcher });
            UnitData victim = Body(Foe, abilities: new[] { UnitAbilities.Berserk });
            var skills = new List<string>();
            BattleRules.AppendHitEffectAbilities(skills, attacker, victim, 1, new[] { UnitAbilities.ShockAttack });
            CollectionAssert.AreEquivalent(new[] { UnitAbilities.ShockAttack, UnitAbilities.Berserk }, skills);
            skills.Clear();
            BattleRules.AppendHitEffectAbilities(skills, attacker, victim, 2);
            CollectionAssert.AreEqual(new[] { UnitAbilities.Berserk }, skills);
        }

        [Test]
        public void HitEffectPreview_DoesNotReportAbsentCollateralOrAnAlreadySpentTurn()
        {
            UnitData attacker = Body(Own, abilities: new[]
                { UnitAbilities.ShockAttack, UnitAbilities.Splash, UnitAbilities.Scorcher });
            UnitData victim = Body(Foe);
            BattleEngine engine = Engine(Army(Own, attacker), Army(Foe, victim),
                (attacker, 0, 0), (victim, 2, 2));
            Assert.That(engine.PreviewGroundHitEffects(attacker, victim, 2), Is.Empty);
            engine.State.TurnOrder = new List<UnitData> { attacker, victim };
            CollectionAssert.AreEqual(new[] { UnitAbilities.ShockAttack },
                engine.PreviewGroundHitEffects(attacker, victim, 2));
            Assert.That(engine.PreviewGroundHitEffects(attacker, victim, 0), Is.Empty);
        }

        [Test]
        public void HitEffectPreview_UsesTheSameCollateralPlanAsTheAcknowledgedAttack()
        {
            UnitData attacker = Body(Own, abilities: new[]
                { UnitAbilities.ShockAttack, UnitAbilities.Splash, UnitAbilities.Scorcher });
            UnitData victim = Body(Foe), left = Body(Foe), right = Body(Foe), above = Body(Foe);
            foreach (var unit in new[] { left, right, above }) unit.TypeTags.Add(UnitTypeTag.Bio);
            var grid = new BattleGrid();
            grid.Set(0, 0, attacker);
            grid.Set(2, 2, victim);
            grid.Set(2, 1, left);
            grid.Set(2, 3, right);
            grid.Set(1, 2, above);
            var random = new CountingRandom();
            var engine = new BattleEngine(new BattleState(default, Army(Own, attacker),
                Army(Foe, victim, left, right, above), grid, 42), AbilityMagnitudes.Default, random);
            engine.State.TurnOrder = new List<UnitData> { attacker, victim };
            var effects = engine.PreviewGroundHitEffects(attacker, victim, 2);
            int draws = random.Draws;
            Assert.That(draws, Is.GreaterThan(0));
            CollectionAssert.AreEquivalent(new[]
                { UnitAbilities.ShockAttack, UnitAbilities.Splash, UnitAbilities.Scorcher }, effects);
            Assert.That(victim.HitPointsCurrent, Is.EqualTo(6));
            Assert.That(left.HitPointsCurrent, Is.EqualTo(6));
            Assert.That(engine.State.TurnOrder.Count, Is.EqualTo(2));
            CollectionAssert.AreEqual(effects, engine.PreviewGroundHitEffects(attacker, victim, 2));
            Assert.That(random.Draws, Is.EqualTo(draws), "Repeated presentation must not reroll collateral.");
            BattleAttackApplication applied = engine.ApplyGroundAttack(attacker, victim, 2);
            Assert.That(random.Draws, Is.EqualTo(draws), "Acknowledgement must use the displayed plan.");
            Assert.That(applied.Shocked, Is.True);
            CollectionAssert.AreEquivalent(new[] { UnitAbilities.Splash, UnitAbilities.Splash,
                UnitAbilities.Scorcher }, applied.SecondaryHits.ConvertAll(hit => hit.Skill));
            Assert.That(left.HitPointsCurrent, Is.EqualTo(5));
        }

        [Test]
        public void HitEffectPreview_ScorcherDoesNotClaimTargetsConsumedBySplashOrNonBioTargets()
        {
            UnitData attacker = Body(Own, abilities: new[] { UnitAbilities.Splash, UnitAbilities.Scorcher });
            UnitData victim = Body(Foe), neighbour = Body(Foe);
            BattleEngine engine = Engine(Army(Own, attacker), Army(Foe, victim, neighbour),
                (attacker, 0, 0), (victim, 2, 2), (neighbour, 2, 1));
            CollectionAssert.AreEqual(new[] { UnitAbilities.Splash },
                engine.PreviewGroundHitEffects(attacker, victim, 2));
            // Finish the existing plan before the next challenge changes the actor's skills.
            engine.ApplyGroundAttack(attacker, victim, 2);
            attacker.Abilities.Remove(UnitAbilities.Splash);
            Assert.That(engine.PreviewGroundHitEffects(attacker, victim, 2), Is.Empty);
        }

        [Test]
        public void Ionic_AddsFlatDamageOnlyAgainstMechanical()
        {
            var mechanical = new List<UnitTypeTag> { UnitTypeTag.Mechanical };
            var bio = new List<UnitTypeTag> { UnitTypeTag.Bio };
            string[] ionic = { UnitAbilities.Ionic };
            AbilityMagnitudes m = AbilityMagnitudes.Default;
            Assert.That(ChallengeResult.ApplyAbilityModifiers(1, ionic, mechanical, null, m), Is.EqualTo(3));
            Assert.That(ChallengeResult.ApplyAbilityModifiers(1, ionic, bio, null, m), Is.EqualTo(1));
            Assert.That(ChallengeResult.ApplyAbilityModifiers(0, ionic, mechanical, null, m), Is.Zero,
                "a miss stays a miss");
            Assert.That(ChallengeResult.ApplyAbilityModifiers(1, ionic, mechanical,
                new[] { UnitAbilities.CeramicArmor }, m), Is.EqualTo(2), "armor applies after Ionic");
        }

        [Test]
        public void Bodyguard_TakesTheHitOncePerRound_WithItsOwnArmor()
        {
            UnitData target = Body(Own), guard = Body(Own, hp: 10, abilities: new[]
                { UnitAbilities.Bodyguard, UnitAbilities.CeramicArmor });
            UnitData attacker = Body(Foe);
            BattleEngine engine = Engine(Army(Own, target, guard), Army(Foe, attacker),
                (target, 3, 1), (guard, 3, 2), (attacker, 2, 1));
            engine.StartRound();

            BattleAttackApplication first = engine.ResolveAndApplyGroundAttack(attacker, target, Roll(3));
            Assert.That(first.InterceptedBy, Is.SameAs(guard));
            Assert.That(first.Victim, Is.SameAs(guard));
            Assert.That(target.HitPointsCurrent, Is.EqualTo(6));
            Assert.That(guard.HitPointsCurrent, Is.EqualTo(8), "3 raw - 1 CeramicArmor of the guard");

            BattleAttackApplication second = engine.ResolveAndApplyGroundAttack(attacker, target, Roll(2));
            Assert.That(second.InterceptedBy, Is.Null, "spent for this round");
            Assert.That(target.HitPointsCurrent, Is.EqualTo(4));

            engine.State.Round++;
            engine.StartRound();
            Assert.That(engine.FindInterceptingBodyguard(attacker, target), Is.SameAs(guard));
        }

        [Test]
        public void Bodyguard_IgnoresMissesDiagonalsAndCounterattacks()
        {
            UnitData target = Body(Own), diagonal = Body(Own, abilities: new[] { UnitAbilities.Bodyguard });
            UnitData attacker = Body(Foe);
            BattleEngine engine = Engine(Army(Own, target, diagonal), Army(Foe, attacker),
                (target, 3, 1), (diagonal, 4, 2), (attacker, 2, 1));
            engine.StartRound();
            Assert.That(engine.FindInterceptingBodyguard(attacker, target), Is.Null, "diagonal is not adjacent");

            UnitData guard = Body(Own, abilities: new[] { UnitAbilities.Bodyguard });
            engine.State.Attacker.Members.Add(guard);
            engine.State.Grid.Set(3, 0, guard);
            Assert.That(engine.ResolveAndApplyGroundAttack(attacker, target, Roll(0)).InterceptedBy, Is.Null);
            Assert.That(engine.ResolveAndApplyGroundAttack(attacker, target, Roll(1), isCounterattack: true)
                .InterceptedBy, Is.Null);
            Assert.That(engine.FindInterceptingBodyguard(attacker, target), Is.SameAs(guard), "still unspent");
        }

        [Test]
        public void Counterattack_AnswersAnAdjacentAttackerOncePerRound()
        {
            UnitData victim = Body(Own, abilities: new[] { UnitAbilities.Counterattack });
            UnitData attacker = Body(Foe), shooter = Body(Foe);
            shooter.Range = 2;
            BattleEngine engine = Engine(Army(Own, victim), Army(Foe, attacker, shooter),
                (victim, 3, 1), (attacker, 2, 2), (shooter, 1, 1));
            engine.StartRound();

            Assert.That(engine.ResolveAndApplyGroundAttack(shooter, victim, Roll(1)).Counterattacker, Is.Null,
                "two cells away");
            Assert.That(engine.ResolveAndApplyGroundAttack(attacker, victim, Roll(1)).Counterattacker,
                Is.SameAs(victim), "diagonal neighbour is within 1 cell");
            Assert.That(engine.ResolveAndApplyGroundAttack(attacker, victim, Roll(1)).Counterattacker, Is.Null,
                "once per round");

            UnitData answerTarget = Body(Own, abilities: new[] { UnitAbilities.Counterattack });
            engine.State.Attacker.Members.Add(answerTarget);
            engine.State.Grid.Set(3, 2, answerTarget);
            Assert.That(engine.ResolveAndApplyGroundAttack(attacker, answerTarget, Roll(1), isCounterattack: true)
                .Counterattacker, Is.Null, "a counterattack is never answered");
        }

        [Test]
        public void Counterattack_CancelledByDeathAndByShockHit()
        {
            UnitData victim = Body(Own, hp: 2, abilities: new[] { UnitAbilities.Counterattack });
            UnitData shocker = Body(Foe, abilities: new[] { UnitAbilities.ShockAttack });
            BattleEngine engine = Engine(Army(Own, victim), Army(Foe, shocker), (victim, 3, 1), (shocker, 2, 1));
            engine.StartRound();
            Assert.That(engine.ResolveAndApplyGroundAttack(shocker, victim, Roll(1)).Counterattacker, Is.Null);
            Assert.That(engine.ResolveAndApplyGroundAttack(shocker, victim, Roll(0)).Counterattacker,
                Is.SameAs(victim), "a Shock miss does not cancel it");

            UnitData doomed = Body(Own, hp: 1, abilities: new[] { UnitAbilities.Counterattack });
            UnitData plain = Body(Foe);
            BattleEngine second = Engine(Army(Own, doomed), Army(Foe, plain), (doomed, 3, 1), (plain, 2, 1));
            second.StartRound();
            Assert.That(second.ResolveAndApplyGroundAttack(plain, doomed, Roll(1)).Counterattacker, Is.Null);
        }

        [Test]
        public void Drill_BuffsTheStrongestFightersForOneRoundAndRevertsAtBattleEnd()
        {
            UnitData drillA = Body(Own, abilities: new[] { UnitAbilities.Drill });
            drillA.IsHero = true;
            UnitData drillB = Body(Own, attack: 1, abilities: new[] { UnitAbilities.Drill });
            UnitData strong = Body(Own, attack: 4), mid = Body(Own, attack: 3), weak = Body(Own, attack: 1);
            UnitData foe = Body(Foe);
            ArmyData own = Army(Own, drillA, drillB, strong, mid, weak);
            BattleEngine engine = Engine(own, Army(Foe, foe), (drillA, 4, 0), (drillB, 3, 0),
                (strong, 3, 1), (mid, 3, 2), (weak, 3, 3), (foe, 1, 1));

            engine.StartRound();
            Assert.That(strong.Attack, Is.EqualTo(5));
            Assert.That(mid.Attack, Is.EqualTo(4), "a second carrier picks the next fighter");
            Assert.That(weak.Attack, Is.EqualTo(1));
            Assert.That(drillA.Attack, Is.EqualTo(2), "a hero is never drilled");

            engine.State.Round++;
            engine.StartRound();
            Assert.That(strong.Attack, Is.EqualTo(5), "re-dealt each round, never stacked");
            Assert.That(mid.Attack, Is.EqualTo(4));

            BattleEngine.RevertTemporaryBattleStats(own);
            Assert.That(strong.Attack, Is.EqualTo(4));
            Assert.That(mid.Attack, Is.EqualTo(3));
            Assert.That(strong.DrillAttackBonus, Is.Zero);
        }

        [Test]
        public void TargetSelector_PrefersTheTargetThatCannotCounterattack()
        {
            UnitData actor = Body(Own, attack: 3);
            UnitData counter = Body(Foe, attack: 4, abilities: new[] { UnitAbilities.Counterattack });
            UnitData plain = Body(Foe, attack: 4);
            var grid = new BattleGrid();
            grid.Set(2, 2, actor);
            grid.Set(1, 1, counter);
            grid.Set(1, 3, plain);
            Assert.That(BattleTargetSelector.TryChooseAttackTarget(grid, actor, 2, 2, AbilityMagnitudes.Default,
                null, 0, out BattleAi.AiAction action), Is.True);
            Assert.That(action.Target, Is.SameAs(plain));

            var spent = new BattleReactionState();
            spent.CountersUsed.Add(plain);
            spent.CountersUsed.Add(counter);
            Assert.That(BattleTargetSelector.TryChooseAttackTarget(grid, actor, 2, 2, AbilityMagnitudes.Default,
                null, 0, out BattleAi.AiAction tie, reactions: spent), Is.True,
                "with the answer spent both are plain targets");
        }

        private static WorthIt.DefenderProfile P(float attack, float defense, float hp, params string[] abilities) =>
            new WorthIt.DefenderProfile(defense, false, null, attack, hp, 1, abilities, hp, range: 1);

        private static WorthIt.DefenderProfile Hero(params string[] abilities) =>
            new WorthIt.DefenderProfile(1f, false, null, 0f, 5f, 1, abilities, 5f,
                isGroundCombatant: false, isHero: true, fateMax: 1, range: 1);

        [Test]
        public void WorthIt_CountsDrillCounterattackAndBodyguard()
        {
            var attackers = new[] { P(3, 1, 6), P(3, 1, 6), P(3, 1, 6) };
            var defenders = new[] { P(3, 1, 6), P(3, 1, 6), P(3, 1, 6) };
            float plain = WorthIt.Estimate(attackers, defenders, 0f).WinChance;

            var drilled = new List<WorthIt.DefenderProfile>(attackers) { Hero(UnitAbilities.Drill) };
            Assert.That(WorthIt.Estimate(drilled, defenders, 0f).WinChance, Is.GreaterThan(plain), "Drill");

            var countering = new[]
            {
                P(3, 1, 6, UnitAbilities.Counterattack), P(3, 1, 6, UnitAbilities.Counterattack),
                P(3, 1, 6, UnitAbilities.Counterattack),
            };
            Assert.That(WorthIt.Estimate(attackers, countering, 0f).WinChance, Is.LessThan(plain), "Counterattack");

            var guarded = new[]
            {
                P(1, 3, 6), P(1, 3, 6), P(1, 6, 18, UnitAbilities.Bodyguard, UnitAbilities.CeramicArmor),
            };
            var unguarded = new[] { P(1, 3, 6), P(1, 3, 6), P(1, 6, 18, UnitAbilities.CeramicArmor) };
            float withGuard = WorthIt.Estimate(guarded, defenders, 0f).WinChance;
            float withoutGuard = WorthIt.Estimate(unguarded, defenders, 0f).WinChance;
            Assert.That(withGuard, Is.GreaterThanOrEqualTo(withoutGuard), "Bodyguard");
            Assert.That(WorthIt.Estimate(guarded, defenders, 0f).ExpectedSurvivingHpRatioOnWin
                + withGuard, Is.GreaterThan(WorthIt.Estimate(unguarded, defenders, 0f).ExpectedSurvivingHpRatioOnWin
                + withoutGuard), "Bodyguard keeps more of the side alive");
        }

        [Test]
        public void SideCommander_KeepsADrillHeroInTheSimulatedRoster()
        {
            UnitData commander = Body(Own), drill = Body(Own, abilities: new[] { UnitAbilities.Drill });
            commander.IsHero = true;
            drill.IsHero = true;
            Assert.That(WorthIt.SideCommander.Of(new[] { commander, drill }).SupportHeroes.Count, Is.EqualTo(1));
        }
    }

    internal static class BoolArrayTestExtensions
    {
        public static bool[] Populate(this bool[] array, bool value)
        {
            for (int i = 0; i < array.Length; i++) array[i] = value;
            return array;
        }
    }
}
#endif
