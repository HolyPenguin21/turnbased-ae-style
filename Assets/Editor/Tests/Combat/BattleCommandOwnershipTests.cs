#if UNITY_INCLUDE_TESTS
using System;
using Game.Cards;
using Game.Combat;
using Game.UI;
using Game.Map;
using Game.Players;
using Game.Units;
using NUnit.Framework;

namespace Game.EditorTests
{
    public sealed class BattleCommandOwnershipTests
    {
        private static BattleEngine Create(out UnitData actor, out UnitData foe, out UnitData ally, Random rng = null)
        {
            var own = new PlayerSetupData(); var enemy = new PlayerSetupData();
            actor = new UnitData { Owner = own, Attack = 2, Defense = 1, Range = 5, Initiative = 10, HitPointsCurrent = 10, HitPointsMax = 10 };
            ally = new UnitData { Owner = own, Attack = 2, Defense = 1, Range = 5, Initiative = 1, HitPointsCurrent = 10, HitPointsMax = 10 };
            foe = new UnitData { Owner = enemy, Attack = 2, Defense = 1, Range = 5, Initiative = 1, HitPointsCurrent = 10, HitPointsMax = 10 };
            var a = new ArmyData { Owner = own }; a.Members.Add(actor); a.Members.Add(ally);
            var b = new ArmyData { Owner = enemy }; b.Members.Add(foe);
            var grid = new BattleGrid(); grid.Set(2, 1, actor); grid.Set(3, 2, ally); grid.Set(1, 1, foe);
            return new BattleEngine(new BattleState(default, a, b, grid, 17), AbilityMagnitudes.Default, rng);
        }
        private static BattleChallengeRollResult Hit => new BattleChallengeRollResult(new[] { true, true }, Array.Empty<bool>(), 0, 0);

        [Test]
        public void EngineRejectsWrongActorAndWrongPhaseWithoutMutation()
        {
            var e = Create(out var actor, out var foe, out var ally);
            e.ResolveAndApplyGroundAttack(actor, foe, Hit);
            Assert.That(foe.HitPointsCurrent, Is.EqualTo(10), "deployment is not an action phase");
            Assert.That(e.TryMoveUnit(actor, 2, 1, 2, 0), Is.False);
            e.StartRound();
            e.ResolveAndApplyGroundAttack(ally, foe, Hit);
            Assert.That(foe.HitPointsCurrent, Is.EqualTo(10), "wrong actor");
            e.ResolveAndApplyGroundAttack(actor, foe, Hit);
            Assert.That(foe.HitPointsCurrent, Is.EqualTo(8));
            e.ResolveAndApplyGroundAttack(actor, foe, Hit);
            Assert.That(foe.HitPointsCurrent, Is.EqualTo(8), "repeat callback");
        }
        [Test]
        public void PendingCounterattackCannotBeBypassedByTurnAdvance()
        {
            var e = Create(out var actor, out var foe, out _);
            foe.Abilities.Add(UnitAbilities.Counterattack);
            e.StartRound();
            Assert.That(e.ResolveAndApplyGroundAttack(actor, foe, Hit).Counterattacker, Is.SameAs(foe));
            int index = e.State.TurnIndex; e.AdvanceTurn();
            Assert.That(e.State.TurnIndex, Is.EqualTo(index));
            e.ResolveAndApplyGroundAttack(foe, actor, Hit, isCounterattack: true);
            Assert.That(actor.HitPointsCurrent, Is.EqualTo(8));
            e.ResolveAndApplyGroundAttack(foe, actor, Hit, isCounterattack: true);
            Assert.That(actor.HitPointsCurrent, Is.EqualTo(8));
        }
        [Test]
        public void LegacyFixedDamageApplyIncludesCounterattack()
        {
            var e = Create(out var actor, out var foe, out _); foe.Abilities.Add(UnitAbilities.Counterattack);
            e.StartRound();
            Assert.That(e.ApplyGroundAttack(actor, foe, 1).Counterattacker, Is.SameAs(foe));
        }
        [Test]
        public void FinalizationCannotSkipMandatoryHeroCaptureKill()
        {
            var e = Create(out _, out var foe, out _);
            e.State.Defender.Members.Clear();
            var hero = new UnitData { Owner = foe.Owner, IsHero = true, HitPointsCurrent = 3, HitPointsMax = 3 };
            e.State.Defender.Members.Add(hero);
            int notices = 0;
            Action<BattleState, BattleEncounterFinalization> handler = (s, f) => { if (s == e.State) notices++; };
            BattleEngine.EncounterFinalized += handler;
            try { BattleEngine.FinalizeEncounter(e.State, null); Assert.That(notices, Is.Zero); }
            finally { BattleEngine.EncounterFinalized -= handler; }
        }
        [Test]
        public void ChallengeCannotRerollOrSkipAnOpenDecisionWindow()
        {
            Create(out var actor, out var foe, out _);
            int draws = 0;
            var session = new BattleChallengeSession(BattleChallengeMode.GroundCombat, actor, foe, 1, 1, 2, 2,
                AbilityMagnitudes.Default, rollTokens: n => { draws++; return new bool[n]; });
            session.Roll(); session.Roll();
            Assert.That(draws, Is.EqualTo(2), "one initial roll");
            Assert.That(session.TrySpend(false, out _, out _), Is.False);
            Assert.That(session.TryNextFateTurn(out bool defending), Is.True); Assert.That(defending, Is.True);
            Assert.That(session.TryNextFateTurn(out defending), Is.True); Assert.That(defending, Is.True);
            Assert.That(session.TrySpend(false, out _, out _), Is.False);
        }
        [Test]
        public void ChallengeRollReceiptCannotRewriteSessionTokens()
        {
            Create(out var actor, out var foe, out _);
            var session = new BattleChallengeSession(BattleChallengeMode.GroundCombat, actor, foe, 1, 0, 0, 0,
                AbilityMagnitudes.Default, rollTokens: n => new bool[n]);
            session.Roll();
            session.ToRollResult().AttackerTokens[0] = true;
            Assert.That(session.RawDamage, Is.Zero);
            session.AttackerTokens[0] = true;
            Assert.That(session.RawDamage, Is.Zero);
        }
        [TestCase(true)]
        [TestCase(false)]
        public void EarlyOrStaleCaptureQueueQueryCannotSuppressRequiredHero(bool queryEarly)
        {
            var e = Create(out _, out var foe, out _);
            if (queryEarly) e.CreateCaptureKillSequence(true, true);
            e.State.Defender.Members.Clear();
            e.State.Defender.Members.Add(new UnitData { Owner = foe.Owner, IsHero = true, HitPointsCurrent = 3, HitPointsMax = 3 });
            if (!queryEarly) e.CreateCaptureKillSequence(true, true);
            Assert.That(e.CreateCaptureKillSequence(true, false).HasPending, Is.True);
        }
        private sealed class HitRandom : Random
        {
            public override double NextDouble() => 0.1;
            public override int Next(int maxValue) => 0;
            public override int Next(int minValue, int maxValue) => minValue;
        }
        [Test]
        public void GroundExplosionUsesEncounterRulesAndRevertsTheSameBerserkGain()
        {
            var initial = Create(out var actor, out var foe, out var ally);
            initial.State.Attacker.Members.Remove(ally); initial.State.Grid.Set(3, 2, null);
            actor.Abilities.UnionWith(new[] { UnitAbilities.SingleUse, UnitAbilities.Explosion3, UnitAbilities.CriticalDamage });
            foe.HitPointsCurrent = foe.HitPointsMax = 100; foe.Defense = 0; foe.Abilities.Add(UnitAbilities.Berserk);
            var rules = new AbilityMagnitudes(3f, 2, 1, 2, 4, 1);
            var e = new BattleEngine(new BattleState(default, initial.State.Attacker, initial.State.Defender, initial.State.Grid, 17), rules, new HitRandom());
            e.StartRound(); Assert.That(e.TryBeginAttack(actor, foe), Is.True);
            var run = ExplosionAttackPresenter.Run(e.CreateExplosionAttack(actor), null, battle: e);
            try { while (run.MoveNext()) { } } finally { (run as IDisposable)?.Dispose(); }
            Assert.That(foe.HitPointsCurrent, Is.EqualTo(70));
            Assert.That(foe.Attack, Is.EqualTo(6));
            e.CompleteBattle(false, true, null);
            Assert.That(foe.Attack, Is.EqualTo(2));
        }
        [Test]
        public void EncounterFinalizationIsIdempotentWithoutPresenter()
        {
            var e = Create(out var actor, out _, out _); e.State.Defender.Members.Clear();
            int notices = 0;
            Action<BattleState, BattleEncounterFinalization> handler = (s, f) => { if (s == e.State) notices++; };
            BattleEngine.EncounterFinalized += handler;
            try
            {
                var first = BattleEngine.FinalizeEncounter(e.State, null); actor.Fate = 0;
                var again = BattleEngine.FinalizeEncounter(e.State, null);
                Assert.That(again.Survivor, Is.SameAs(first.Survivor));
                Assert.That(notices, Is.EqualTo(1)); Assert.That(actor.Fate, Is.Zero);
            }
            finally { BattleEngine.EncounterFinalized -= handler; }
        }

        private sealed class CountingRandom : Random
        {
            public int Draws;
            public override int Next(int maxValue) { Draws++; return 0; }
            public override double NextDouble() { Draws++; return 0.1; }
        }
        [Test]
        public void AttackPlanPreviewDoesNotRerollAndAppliesOnce()
        {
            var rng = new CountingRandom();
            var e = Create(out var actor, out var foe, out _, rng);
            actor.Abilities.Add(UnitAbilities.Splash);
            for (int i = 0; i < 3; i++)
            {
                var neighbour = new UnitData { Owner = foe.Owner, HitPointsCurrent = 10, HitPointsMax = 10 };
                e.State.Defender.Members.Add(neighbour);
                e.State.Grid.Set(i == 0 ? 0 : 1, i == 0 ? 1 : i == 1 ? 0 : 2, neighbour);
            }
            e.StartRound();
            Assert.That(e.TryBeginAttack(actor, foe, out var request), Is.True);
            var roll = Hit;
            var plan = e.PrepareGroundAttack(request, roll);
            int draws = rng.Draws;
            Assert.That(draws, Is.GreaterThan(0));
            roll.AttackerTokens[0] = false;
            Assert.That(e.PrepareGroundAttack(request, default), Is.SameAs(plan));
            Assert.That(rng.Draws, Is.EqualTo(draws));
            var applied = e.ApplyAttackPlan(plan);
            Assert.That(applied.Applied, Is.True);
            Assert.That(applied.SecondaryHits.Count, Is.EqualTo(2));
            Assert.That(foe.HitPointsCurrent, Is.EqualTo(8));
            Assert.That(rng.Draws, Is.EqualTo(draws));
            Assert.That(e.ApplyAttackPlan(plan).Applied, Is.False);
            Assert.That(foe.HitPointsCurrent, Is.EqualTo(8));
        }
        [TestCase(true)]
        [TestCase(false)]
        public void ApplyAndAcknowledgeAreIndependentOneTimeReceipts(bool applyFirst)
        {
            var e = Create(out var actor, out _, out _);
            e.State.Defender.Members.Clear();
            actor.IsHero = true; actor.Fate = 0; actor.FateMax = 3;
            if (applyFirst) BattleEncounterCoordinator.Apply(e.State);
            Assert.That(BattleEncounterCoordinator.TryAcknowledge(e.State, out var first), Is.True);
            Assert.That(actor.Fate, Is.EqualTo(3));
            actor.Fate = 1;
            Assert.That(BattleEncounterCoordinator.TryAcknowledge(e.State, out var again), Is.False);
            Assert.That(BattleEncounterCoordinator.Apply(e.State).Survivor, Is.SameAs(first.Survivor));
            Assert.That(again.Survivor, Is.SameAs(first.Survivor));
            Assert.That(actor.Fate, Is.EqualTo(1));
        }
        [Test]
        public void RawHitAndPositiveDamageHaveDifferentExplicitMeanings()
        {
            Create(out var actor, out var foe, out _);
            actor.Attack = 1; foe.Defense = 0; foe.Abilities.Add(UnitAbilities.CeramicArmor);
            var odds = BattleCombatOdds.Evaluate(actor, foe, 0, AbilityMagnitudes.Default, 10);
            Assert.That(odds.RawHitProbability, Is.EqualTo(0.5f));
            Assert.That(odds.PositiveDamageProbability, Is.Zero);
            Assert.That(odds.HitProbability, Is.EqualTo(odds.PositiveDamageProbability));
        }
        [Test]
        public void StaleCaptureCallbackCannotResolveTheNextHero()
        {
            var e = Create(out _, out var foe, out _);
            e.State.Defender.Members.Clear();
            for (int i = 0; i < 2; i++) e.State.Defender.Members.Add(new UnitData
                { Owner = foe.Owner, IsHero = true, HitPointsCurrent = 3, HitPointsMax = 3 });
            var sequence = e.CreateCaptureKillSequence(true, false);
            Assert.That(sequence.TryGetCurrent(out var first), Is.True);
            Assert.That(sequence.TryResolveCurrent(first, default, null, null, out _), Is.True);
            Assert.That(sequence.Count, Is.EqualTo(1));
            Assert.That(sequence.TryResolveCurrent(first, default, null, null, out _), Is.False);
            Assert.That(sequence.Count, Is.EqualTo(1));
        }
        [Test]
        public void StandaloneAcceptedMissStillReportsApplied()
        {
            Create(out var actor, out var foe, out _);
            var application = BattleEngine.ResolveStandaloneAttack(actor, foe, default, AbilityMagnitudes.Default);
            Assert.That(application.Applied, Is.True);
            Assert.That(application.Damage, Is.Zero);
            Assert.That(foe.HitPointsCurrent, Is.EqualTo(10));
        }
        [Test]
        public void RetreatWaitsForOpponentGraceRoundAndFinishesOnce()
        {
            var e = Create(out var actor, out _, out _);
            var army = e.State.Defender;
            e.CommitRetreat(army);
            Assert.That(e.CompleteRetreat(army, e.State.Attacker, null, null).Destroyed, Is.False);
            Assert.That(army.Members.Count, Is.EqualTo(1));
            e.StartRound();
            Assert.That(e.CurrentActor, Is.SameAs(actor));
            while (e.Phase == BattleCommandPhase.Acting)
            {
                Assert.That(e.State.OwningArmy(e.CurrentActor), Is.SameAs(e.State.Attacker));
                Assert.That(e.TryPass(e.CurrentActor), Is.True); e.AdvanceTurn();
            }
            Assert.That(e.Phase, Is.EqualTo(BattleCommandPhase.RetreatReady));
            var result = e.CompleteRetreat(army, e.State.Attacker, null, null);
            Assert.That(result.Destroyed, Is.True);
            Assert.That(e.CompleteRetreat(army, e.State.Attacker, null, null).Destroyed, Is.True);
            Assert.That(army.Members, Is.Empty);
        }
        [Test]
        public void ExplosionCannotAlsoPrepareAnOrdinaryHitForTheSameAction()
        {
            var e = Create(out var actor, out var foe, out _);
            actor.Abilities.Add(UnitAbilities.Explosion3);
            e.StartRound(); Assert.That(e.TryBeginAttack(actor, foe, out var request), Is.True);
            var plan = e.PrepareGroundAttack(request, Hit);
            Assert.That(plan, Is.Null);
            var blast = e.CreateExplosionAttack(actor);
            Assert.That(blast, Is.Not.Null);
            Assert.That(e.ApplyAttackPlan(plan).Applied, Is.False);
            Assert.That(foe.HitPointsCurrent, Is.EqualTo(10));
        }
        [Test]
        public void AForeignExplosionCannotMutateThisEnginesAction()
        {
            var e = Create(out var actor, out var foe, out _);
            actor.Abilities.Add(UnitAbilities.Explosion3); e.StartRound();
            Assert.That(e.TryBeginAttack(actor, foe), Is.True);
            var foreign = new ExplosionAttack(actor, default, new Random(1), e.State.Attacker, e.State.Defender);
            var target = System.Linq.Enumerable.First(foreign.Targets, t => t.Unit == foe);
            Assert.Throws<InvalidOperationException>(() => foreign.Resolve(target, foe, Hit, e.Magnitudes, battle: e));
            Assert.That(foe.HitPointsCurrent, Is.EqualTo(10));
            Assert.That(foreign.ResolvedCount, Is.Zero);
            Assert.Throws<InvalidOperationException>(() => foreign.Complete(e));
            Assert.That(e.Phase, Is.EqualTo(BattleCommandPhase.AwaitingAttack));
        }
        [Test]
        public void BattleSummonsCanOnlyBeSpawnedOnceDuringDeployment()
        {
            var e = Create(out var actor, out _, out _);
            actor.Abilities.Add(UnitAbilities.RaiseTheRots);
            var card = new CardDefinition();
            UnitData Spawn(CardDefinition _, PlayerSetupData owner) => new UnitData { Owner = owner, HitPointsCurrent = 3 };
            var army = e.State.Attacker;
            Assert.That(e.SpawnBattleSummons(army, 3, 4, card, 1, Spawn).Spawned, Is.EqualTo(1));
            Assert.That(e.SpawnBattleSummons(army, 3, 4, card, 1, Spawn).Spawned, Is.Zero);
            e.StartRound();
            Assert.That(e.SpawnBattleSummons(army, 3, 4, card, 1, Spawn).Spawned, Is.Zero);
            Assert.That(army.Members.Count, Is.EqualTo(3));
        }
        [Test]
        public void AppliedEncounterCannotAcquireNewSummons()
        {
            var e = Create(out var actor, out _, out _); e.State.Defender.Members.Clear();
            actor.Abilities.Add(UnitAbilities.RaiseTheRots); BattleEncounterCoordinator.Apply(e.State);
            Assert.That(e.SpawnBattleSummons(e.State.Attacker, 3, 4, new CardDefinition(), 1,
                (_, owner) => new UnitData { Owner = owner, HitPointsCurrent = 3 }).Spawned, Is.Zero);
        }
        [Test]
        public void FinalizationBeforeEngineConstructionCannotEndALiveEncounter()
        {
            var original = Create(out _, out _, out _);
            var state = new BattleState(default, original.State.Attacker, original.State.Defender, original.State.Grid, 17);
            Assert.That(BattleEncounterCoordinator.TryAcknowledge(state, out _), Is.False);
            var e = new BattleEngine(state, AbilityMagnitudes.Default);
            e.StartRound(); Assert.That(e.Phase, Is.EqualTo(BattleCommandPhase.Acting));
        }
        [Test]
        public void LegacyStandaloneFinishUsesTheCurrentEncounterReceipt()
        {
            var e = Create(out var actor, out _, out _); e.State.Defender.Members.Clear();
            actor.IsHero = true; actor.FateMax = 3; actor.Fate = 0;
            var first = BattleEngine.FinalizeStandaloneCaptureKill(e.State.Attacker, e.State.Defender, null);
            Assert.That(actor.Fate, Is.EqualTo(3)); actor.Fate = 1;
            Assert.That(BattleEngine.FinalizeStandaloneCaptureKill(e.State.Attacker, e.State.Defender, null).Survivor,
                Is.SameAs(first.Survivor));
            Assert.That(actor.Fate, Is.EqualTo(1));
            var next = new BattleState(default, e.State.Attacker, e.State.Defender, new BattleGrid(), 18);
            BattleEngine.FinalizeStandaloneCaptureKill(next.Attacker, next.Defender, null);
            Assert.That(actor.Fate, Is.EqualTo(3), "a newly created encounter has a new receipt");
        }
        [Test]
        public void LegacyFinishInReverseRolesKeepsTheSameReceipt()
        {
            var e = Create(out _, out var foe, out _); e.State.Attacker.Members.Clear();
            var hero = new UnitData { Owner = foe.Owner, IsHero = true, HitPointsCurrent = 3, FateMax = 3 };
            e.State.Defender.Members.Add(hero);
            BattleEncounterCoordinator.Apply(e.State); hero.Fate = 1;
            var result = BattleEngine.FinalizeStandaloneCaptureKill(e.State.Defender, e.State.Attacker, null);
            Assert.That(hero.Fate, Is.EqualTo(1)); Assert.That(result.Survivor, Is.SameAs(e.State.Defender));
            Assert.That(result.AttackerHere, Is.True, "legacy hunter orientation is preserved");
        }
        [Test]
        public void StaleLegacyPairCannotReplaceTheCurrentEncounter()
        {
            var e = Create(out var actor, out var foe, out _); e.State.Defender.Members.Clear();
            actor.IsHero = true; actor.FateMax = 3;
            BattleEncounterCoordinator.Apply(e.State);
            var nextArmy = new ArmyData { Owner = foe.Owner };
            nextArmy.Members.Add(foe);
            var next = new BattleState(default, e.State.Attacker, nextArmy, new BattleGrid(), 19);
            actor.Fate = 1;
            Assert.That(BattleEngine.FinalizeStandaloneCaptureKill(e.State.Attacker, e.State.Defender, null).Survivor, Is.Null);
            Assert.That(actor.Fate, Is.EqualTo(1));
            Assert.That(BattleEncounterCoordinator.TryAcknowledge(next, out _), Is.False);
        }
        [Test]
        public void EngineExplosionKeepsItsOwnerWhenOptionalBattleIsOmitted()
        {
            var e = Create(out var actor, out var foe, out _);
            actor.Abilities.UnionWith(new[] { UnitAbilities.Explosion3, UnitAbilities.SingleUse, UnitAbilities.CriticalDamage });
            foe.HitPointsCurrent = foe.HitPointsMax = 100;
            e = new BattleEngine(new BattleState(default, e.State.Attacker, e.State.Defender, e.State.Grid, 18),
                new AbilityMagnitudes(3f, 2, 1, 2, 4, 1));
            e.StartRound(); Assert.That(e.TryBeginAttack(actor, foe), Is.True);
            var attack = e.CreateExplosionAttack(actor);
            var target = System.Linq.Enumerable.First(attack.Targets, t => t.Unit == foe);
            var application = attack.Resolve(target, foe, new BattleChallengeRollResult(
                System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Repeat(true, 10)), Array.Empty<bool>(), 0, 0),
                AbilityMagnitudes.Default);
            Assert.That(application.Damage, Is.EqualTo(30), "engine rules apply even through the optional legacy arguments");
            attack.Complete();
            Assert.That(e.Phase, Is.EqualTo(BattleCommandPhase.AwaitingAdvance));
            Assert.That(e.State.Grid.TryFindPosition(actor, out _, out _), Is.False);
        }
    }
}
#endif
