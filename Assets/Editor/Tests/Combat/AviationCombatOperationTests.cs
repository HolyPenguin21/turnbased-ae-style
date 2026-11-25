#if UNITY_INCLUDE_TESTS
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using Game.Aviation;
using Game.Cards;
using Game.Combat;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Units;
using NUnit.Framework;

namespace Game.EditorTests
{
    public class AviationCombatOperationTests
    {
        private readonly HexCoord _hex = new HexCoord(2, 2);
        [SetUp] public void SetUp() { ArmyRegistry.Clear(); BuildingRegistry.Clear(); AntiAirState.Clear(); VisionSystem.Clear(); VisionSystem.Configure(null); }
        [TearDown] public void TearDown() { ArmyRegistry.Clear(); BuildingRegistry.Clear(); AntiAirState.Clear(); VisionSystem.Clear(); }
        private UnitData Body(PlayerSetupData owner, bool aircraft = false, int attack = 3, int hp = 4)
            => new UnitData { Owner = owner, IsAviation = aircraft, Attack = attack,
                HitPointsCurrent = hp, HitPointsMax = hp, Defense = 0 };
        private ArmyData Army(PlayerSetupData owner, params UnitData[] members)
        {
            var army = new ArmyData { Owner = owner, Hex = _hex };
            foreach (var unit in members) army.Members.Add(unit);
            army.IsAirArmy = members.All(u => u.IsAviation);
            ArmyRegistry.Register(army);
            return army;
        }
        private static bool[] Hits(int n) => Enumerable.Repeat(true, n).ToArray();
        private static BattleChallengeRollResult Hit(int n) => new BattleChallengeRollResult(Hits(n), Array.Empty<bool>(), 0, 0);
        private (AaReaction reaction, UnitData aa, ArmyData wing) Reaction(bool human = true)
        {
            var aaOwner = new PlayerSetupData { IsHuman = human };
            var aa = Body(aaOwner, attack: 2);
            aa.Abilities.Add(UnitAbilities.AntiAir); aa.AntiAirRadius = 2;
            var battery = Army(aaOwner, aa);
            var enemy = new PlayerSetupData();
            var wing = Army(enemy, Body(enemy, true, hp: 3));
            return (new AaReaction(aa, battery, wing, 0), aa, wing);
        }
        [Test] public void AcceptedAaWithoutPresentationFiresDoubleAttackAndRemovesDestroyedWing()
        {
            var scenario = Reaction();
            int attackPool = -1;
            var operation = new AviationCombatOperation(new Random(1), n => { attackPool = Math.Max(attackPool, n); return Hits(n); });
            AviationCombatHeadlessAdapter.Run(operation.ResolveAaReaction(scenario.reaction), _ => true);
            Assert.That(attackPool, Is.EqualTo(4));
            Assert.That(scenario.wing.Members, Is.Empty);
            Assert.That(AntiAirState.CanReact(scenario.aa, scenario.wing.Id), Is.False);
        }
        [Test] public void AaReactionAndStrikeEachCountAsOneFightForTheirSurvivors()
        {
            var scenario = Reaction();
            var plane = scenario.wing.Members[0];
            AviationCombatHeadlessAdapter.Run(new AviationCombatOperation(new Random(1), Hits)
                .ResolveAaReaction(scenario.reaction), _ => true);
            Assert.That(scenario.aa.CombatRecord.DamageDealt, Is.EqualTo(3), "The plane had 3 HP.");
            Assert.That(scenario.aa.CombatRecord.Kills, Is.EqualTo(1));
            Assert.That(scenario.aa.CombatRecord.BattlesSurvived, Is.EqualTo(1));
            Assert.That(plane.CombatRecord.DamageTaken, Is.EqualTo(3));
            Assert.That(plane.CombatRecord.BattlesSurvived, Is.Zero);

            ArmyRegistry.Clear();
            var owner = new PlayerSetupData(); var foe = new PlayerSetupData();
            var bomber = Body(owner, true, attack: 3); var wing = Army(owner, bomber);
            var ground = Body(foe, hp: 4); Army(foe, ground);
            AviationCombatHeadlessAdapter.Run(new AviationCombatOperation(new Random(1), Hits)
                .ResolveAirStrikeAtCurrentHex(wing, _hex, AirStrikePolicy.Standard));
            Assert.That(bomber.CombatRecord.DamageDealt, Is.EqualTo(3));
            Assert.That(bomber.CombatRecord.BattlesSurvived, Is.EqualTo(1));
            Assert.That(ground.CombatRecord.DamageTaken, Is.EqualTo(3));
            Assert.That(ground.CombatRecord.BattlesSurvived, Is.EqualTo(1));
            Assert.That(bomber.CombatRecord.Kills + ground.CombatRecord.Kills, Is.Zero);
        }

        [Test] public void SkipRecordsPairButKeepsShotForAnotherWing()
        {
            var scenario = Reaction();
            var operation = new AviationCombatOperation(new Random(1), Hits);
            AviationCombatHeadlessAdapter.Run(operation.ResolveAaReaction(scenario.reaction), _ => false);
            Assert.That(scenario.wing.Members.Count, Is.EqualTo(1));
            Assert.That(AntiAirState.CanReact(scenario.aa, scenario.wing.Id), Is.False);
            Assert.That(AntiAirState.CanReact(scenario.aa, scenario.wing.Id + 1), Is.True);
        }
        [Test] public void HiddenWingProducesNoPromptAndDoesNotSpendAa()
        {
            var scenario = Reaction(); scenario.wing.Members[0].IsHidden = true;
            Assert.That(new AviationCombatOperation().ResolveAaReaction(scenario.reaction).Any(), Is.False);
            Assert.That(AntiAirState.CanReact(scenario.aa, scenario.wing.Id), Is.True);
        }
        [Test] public void RequestMustBeAnsweredBeforeGameplayCanAdvance()
        {
            var scenario = Reaction();
            using var iterator = new AviationCombatOperation().ResolveAaReaction(scenario.reaction).GetEnumerator();
            Assert.That(iterator.MoveNext(), Is.True);
            Assert.Throws<InvalidOperationException>(() => iterator.MoveNext());
            Assert.That(AntiAirState.CanReact(scenario.aa, scenario.wing.Id), Is.True);
        }
        [Test] public void AaAnswerAndChallengeCompletionAreIdempotent()
        {
            var scenario = Reaction();
            using var iterator = new AviationCombatOperation(new Random(2), n => Enumerable.Range(0, n).Select(i => i == 0).ToArray()).ResolveAaReaction(scenario.reaction).GetEnumerator();
            Assert.That(iterator.MoveNext(), Is.True);
            var decision = (AviationCombatOperation.AaDecisionRequest)iterator.Current;
            decision.Complete(true); decision.Complete(false);
            Assert.That(iterator.MoveNext(), Is.True);
            var challenge = (AviationCombatOperation.ChallengeRequest)iterator.Current;
            AviationCombatHeadlessAdapter.Resolve(challenge); challenge.Complete(Hit(10));
            Assert.That(iterator.MoveNext(), Is.False);
            Assert.That(scenario.wing.Members[0].HitPointsCurrent, Is.EqualTo(2));
        }
        [Test] public void ChallengeRejectsForgedCallbackAndUsesOnlyAcceptedSessionTokens()
        {
            var scenario = Reaction();
            using var iterator = new AviationCombatOperation(new Random(2), n => new bool[n])
                .ResolveAaReaction(scenario.reaction).GetEnumerator();
            Assert.That(iterator.MoveNext(), Is.True);
            ((AviationCombatOperation.AaDecisionRequest)iterator.Current).Complete(true);
            Assert.That(iterator.MoveNext(), Is.True);
            var challenge = (AviationCombatOperation.ChallengeRequest)iterator.Current;
            Assert.Throws<InvalidOperationException>(() => challenge.Complete(Hit(99)));
            challenge.Session.Roll(); challenge.Session.ResolveAiDecisions();
            challenge.Complete(Hit(99)); // callers cannot replace the accepted session's misses
            Assert.That(iterator.MoveNext(), Is.False);
            Assert.That(scenario.wing.Members[0].HitPointsCurrent, Is.EqualTo(3));
        }
        [Test] public void AbandoningPendingChallengeCancelsSessionWithoutApplyingDamage()
        {
            var scenario = Reaction();
            var iterator = new AviationCombatOperation(new Random(2), Hits)
                .ResolveAaReaction(scenario.reaction).GetEnumerator();
            Assert.That(iterator.MoveNext(), Is.True);
            ((AviationCombatOperation.AaDecisionRequest)iterator.Current).Complete(true);
            Assert.That(iterator.MoveNext(), Is.True);
            var challenge = (AviationCombatOperation.ChallengeRequest)iterator.Current;
            iterator.Dispose();
            Assert.That(challenge.Session.IsCancelled, Is.True);
            Assert.That(scenario.wing.Members[0].HitPointsCurrent, Is.EqualTo(3));
        }
        [Test] public void EntryAaDestructionStopsMovementWithoutPublishingOldStrikeReceipt()
        {
            var scenario = Reaction(); scenario.wing.LastAirStrikeAttacked = true;
            var visible = (Dictionary<PlayerSetupData, HashSet<HexCoord>>)typeof(VisionSystem)
                .GetField("Visible", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
            visible[scenario.aa.Owner] = new HashSet<HexCoord> { _hex };
            var operation = new AviationCombatOperation(new Random(1), Hits);
            int strikes = 0; operation.StrikeCompleted += _ => strikes++;
            var outcome = new ArmyController.StepResolutionOutcome();
            AviationCombatHeadlessAdapter.Run(operation.ResolveStep(scenario.wing, _hex, outcome));
            Assert.That(outcome.StopMovement, Is.True);
            Assert.That(scenario.wing.Members, Is.Empty);
            Assert.That(strikes, Is.Zero);
        }
        [Test] public void EmptyMoverPublicStepStopsAndRemovesWing()
        {
            var owner = new PlayerSetupData(); var wing = Army(owner, Body(owner, true));
            wing.Members.Clear(); wing.PendingAirStrikePolicy = AirStrikePolicy.Standard;
            var outcome = new ArmyController.StepResolutionOutcome();
            AviationCombatHeadlessAdapter.Run(new AviationCombatOperation().ResolveStep(wing, _hex, outcome));
            Assert.That(outcome.StopMovement, Is.True);
            Assert.That(wing.PendingAirStrikePolicy, Is.Null);
            Assert.That(ArmyRegistry.IsRegistered(wing), Is.False);
        }

        [Test] public void AbandonedStrikeDoesNotPublishAttackReceiptBeforeApplication()
        {
            var owner = new PlayerSetupData(); var foe = new PlayerSetupData();
            var plane = Body(owner, true); var wing = Army(owner, plane);
            var defender = Body(foe); Army(foe, defender);
            var result = new AviationCombatResult();
            int completions = 0;
            var operation = new AviationCombatOperation(new Random(1), Hits);
            operation.StrikeCompleted += _ => completions++;
            var iterator = operation.ResolveAirStrikeAtCurrentHex(wing, _hex, AirStrikePolicy.Standard, result).GetEnumerator();
            Assert.That(iterator.MoveNext(), Is.True);
            var challenge = (AviationCombatOperation.ChallengeRequest)iterator.Current;
            Assert.That(plane.HasAirAttackedThisTurn, Is.True, "Launching still commits the attack cost.");
            Assert.That(result.Attacked, Is.False, "Selecting a target has not applied an attack.");
            Assert.That(result.AttackedArmyId, Is.Null);
            iterator.Dispose();
            Assert.That(challenge.Session.IsCancelled, Is.True);
            Assert.That(defender.HitPointsCurrent, Is.EqualTo(4));
            Assert.That(result.Attacked, Is.False);
            Assert.That(completions, Is.Zero);
        }

        [Test] public void TransitAndRaidFloorDoNotRollOrConsumeAircraftAttack()
        {
            var owner = new PlayerSetupData(); var foe = new PlayerSetupData();
            var plane = Body(owner, true); var wing = Army(owner, plane); var defenders = Army(foe, Body(foe));
            var operation = new AviationCombatOperation(rollTokens: n => throw new Exception("No roll expected"));
            AviationCombatHeadlessAdapter.Run(operation.ResolveAirStrikeAtCurrentHex(wing, _hex, AirStrikePolicy.Transit));
            AviationCombatHeadlessAdapter.Run(operation.ResolveAirStrikeAtCurrentHex(wing, _hex, AirStrikePolicy.RaidSupport(defenders.Id)));
            Assert.That(plane.HasAirAttackedThisTurn, Is.False);
        }
        [Test] public void ExactArmyAndHeroDefensePoolAreSharedWithChallengeAdapter()
        {
            var owner = new PlayerSetupData(); var foe = new PlayerSetupData();
            var plane = Body(owner, true); var wing = Army(owner, plane);
            var bystander = Army(foe, Body(foe));
            var hero = Body(foe); hero.IsHero = true; hero.FateMax = 7; hero.Defense = 1;
            var target = Army(foe, hero);
            using var iterator = new AviationCombatOperation(new Random(1), Hits)
                .ResolveAirStrikeAtCurrentHex(wing, _hex, AirStrikePolicy.DefenceSupport(target.Id)).GetEnumerator();
            Assert.That(iterator.MoveNext(), Is.True);
            var challenge = (AviationCombatOperation.ChallengeRequest)iterator.Current;
            Assert.That(challenge.Defender, Is.SameAs(hero));
            Assert.That(challenge.Session.DefenderPoolSize, Is.EqualTo(7));
            AviationCombatHeadlessAdapter.Resolve(challenge); Assert.That(iterator.MoveNext(), Is.False);
            Assert.That(bystander.Members[0].HitPointsCurrent, Is.EqualTo(4));
        }
        [Test] public void ScriptedRequestsAndHeadlessUseIdenticalSeededTargetsRollsAndRoster()
        {
            string Run(bool headless)
            {
                ArmyRegistry.Clear();
                var owner = new PlayerSetupData(); var foe = new PlayerSetupData();
                var wing = Army(owner, Body(owner, true), Body(owner, true));
                var targets = Army(foe, Body(foe, hp: 4), Body(foe, hp: 4), Body(foe, hp: 4));
                var result = new AviationCombatResult();
                var operation = new AviationCombatOperation(new Random(378), Hits);
                var requests = operation.ResolveAirStrikeAtCurrentHex(wing, _hex, AirStrikePolicy.Standard, result);
                if (headless) AviationCombatHeadlessAdapter.Run(requests);
                else foreach (var request in requests)
                {
                    var challenge = (AviationCombatOperation.ChallengeRequest)request;
                    challenge.Session.Roll(); challenge.Session.ResolveAiDecisions();
                    challenge.Complete(challenge.Session.ToRollResult());
                }
                return string.Join(",", targets.Members.Select(u => u.HitPointsCurrent)) + ":" + result.DamageDealt;
            }
            Assert.That(Run(false), Is.EqualTo(Run(true)));
        }
        [Test] public void DisposingLaunchedExplosionCompletesSingleUseAfterResolvedHit()
        {
            var owner = new PlayerSetupData { IsHuman = true }; var foe = new PlayerSetupData();
            var missile = Body(owner, true); missile.Abilities.Add(UnitAbilities.Explosion3); missile.Abilities.Add(UnitAbilities.SingleUse);
            var wing = Army(owner, missile); Army(foe, Body(foe, hp: 20), Body(foe, hp: 20));
            var iterator = new AviationCombatOperation(new Random(1), Hits)
                .ResolveAirStrikeAtCurrentHex(wing, _hex, AirStrikePolicy.Standard).GetEnumerator();
            Assert.That(iterator.MoveNext(), Is.True);
            AviationCombatHeadlessAdapter.Resolve((AviationCombatOperation.ChallengeRequest)iterator.Current);
            Assert.That(iterator.MoveNext(), Is.True); // first hit applied; another victim is waiting
            iterator.Dispose();
            Assert.That(missile.HitPointsCurrent, Is.Zero);
            Assert.That(wing.Members, Is.Empty);
        }
        [Test] public void PresenterWithoutPopupsExecutesAcceptedAaInsteadOfDroppingShot()
        {
            var scenario = Reaction();
            var visible = (Dictionary<PlayerSetupData, HashSet<HexCoord>>)typeof(VisionSystem)
                .GetField("Visible", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
            visible[scenario.aa.Owner] = new HashSet<HexCoord> { _hex };
            var presenter = (AviationCombatPresenter)FormatterServices.GetUninitializedObject(typeof(AviationCombatPresenter));
            // FormatterServices bypasses field initialization normally performed by Unity.
            var presentations = typeof(AviationCombatPresenter).GetField("_presentations", BindingFlags.NonPublic | BindingFlags.Instance);
            presentations.SetValue(presenter, Activator.CreateInstance(presentations.FieldType));
            var previous = AviationCombatPresenter.HeadlessRng;
            try
            {
                AviationCombatPresenter.HeadlessRng = new Random(1);
                IEnumerator routine = presenter.ResolveTurnStartAntiAir(scenario.aa.Owner);
                while (routine.MoveNext()) Assert.Fail("Headless adapter must not wait for a popup.");
                Assert.That(scenario.wing.Members.Count == 0 || scenario.wing.Members[0].HitPointsCurrent < 3, Is.True);
            }
            finally { AviationCombatPresenter.HeadlessRng = previous; }
        }
    }
}
#endif
