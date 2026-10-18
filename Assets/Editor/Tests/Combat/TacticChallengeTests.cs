#if UNITY_INCLUDE_TESTS
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Linq;
using System.Runtime.Serialization;
using Game.Cards;
using Game.Combat;
using Game.Players;
using Game.UI;
using Game.Units;
using NUnit.Framework;

namespace Game.EditorTests
{
    public sealed class TacticChallengeTests
    {
        private static CardData Card(TacticEffect effect, int amount = 1) => new CardData(
            new CardDefinition { cardType = CardType.Tactic, tacticEffect = effect, tacticAmount = amount });

        private static UnitData Unit(PlayerSetupData owner, int hp = 3) => new UnitData
            { Owner = owner, Attack = 2, Defense = 2, HitPointsCurrent = hp, HitPointsMax = hp };

        private static BattleChallengeSession Session(UnitData attacker, UnitData defender,
            bool[] attack, bool[] defense, int attackerFate = 0, bool critical = false,
            BattleChallengeMode mode = BattleChallengeMode.GroundCombat)
        {
            int call = 0;
            var session = new BattleChallengeSession(mode, attacker, defender, attack.Length, defense.Length,
                attackerFate, 0, AbilityMagnitudes.Default,
                rollDice: n => call++ == 0 ? (bool[])attack.Clone() : call == 2 ? (bool[])defense.Clone() : new[] { true },
                tacticsCritical: critical);
            session.Roll();
            return session;
        }

        [Test]
        public void NoFate_DefenderStillOpensAndCanPlay_ThenAttackerCanAnswer()
        {
            var a = new PlayerSetupData(); var d = new PlayerSetupData();
            var ac = Card(TacticEffect.AddSuccess); var dc = Card(TacticEffect.AddSuccess);
            a.Tactics.Add(ac); d.Tactics.Add(dc);
            var s = Session(Unit(a), Unit(d), new[] { true }, new[] { false });
            Assert.That(s.HasAnyFate, Is.False);
            Assert.That(s.HasAnyDecision, Is.True);
            Assert.That(s.TryNextFateTurn(out bool defending) && defending, Is.True);
            Assert.That(s.TryPlayTactic(false, ac), Is.False, "out-of-turn play must not consume");
            Assert.That(s.TryPlayTactic(true, dc), Is.True);
            Assert.That(s.RawDamage, Is.Zero);
            s.ReportFateTurn(true);
            Assert.That(s.TryNextFateTurn(out defending) && !defending, Is.True);
            Assert.That(s.TryPlayTactic(false, ac), Is.True);
            Assert.That(s.RawDamage, Is.EqualTo(1));
            s.ReportFateTurn(true);
            Assert.That(s.TryNextFateTurn(out defending) && defending, Is.True);
            s.ReportFateTurn(false);
            Assert.That(s.TryNextFateTurn(out defending), Is.False);
        }

        [Test]
        public void LastFate_DoesNotRemoveRemainingTacticDecision()
        {
            var a = new PlayerSetupData(); var d = new PlayerSetupData();
            var card = Card(TacticEffect.AddSuccess); a.Tactics.Add(card);
            var s = Session(Unit(a), Unit(d), new[] { false }, new[] { true }, 1);
            s.TryNextFateTurn(out _); s.ReportFateTurn(false); s.TryNextFateTurn(out _);
            Assert.That(s.TrySpend(false, out _, out _), Is.True);
            Assert.That(s.AttackerFateRemaining, Is.Zero);
            Assert.That(s.HasTactic(false), Is.True);
            Assert.That(s.TryPlayTactic(false, card), Is.True);
            Assert.That(s.RawDamage, Is.EqualTo(1));
        }

        [Test]
        public void GuaranteedSuccess_CanExceedPool_AndDoesNotMutateOriginalDice()
        {
            var a = new PlayerSetupData(); var card = Card(TacticEffect.AddSuccess); a.Tactics.Add(card);
            var s = Session(Unit(a), Unit(new PlayerSetupData()), new[] { true }, new[] { true });
            s.TryNextFateTurn(out _); s.ReportFateTurn(false); s.TryNextFateTurn(out _);
            Assert.That(s.TryPlayTactic(false, card), Is.True);
            Assert.That(s.CurrentResult.AttackerSuccesses, Is.EqualTo(2));
            Assert.That(s.AttackerDice, Is.EqualTo(new[] { true }));
        }

        [Test]
        public void Suppression_IsClamped_AndNeverCreatesARerollableMiss()
        {
            var d = new PlayerSetupData(); var card = Card(TacticEffect.SuppressSuccess, 5); d.Tactics.Add(card);
            var s = Session(Unit(new PlayerSetupData()), Unit(d), new[] { true }, new[] { false }, 3);
            s.TryNextFateTurn(out _);
            Assert.That(s.TryPlayTactic(true, card), Is.True);
            Assert.That(s.CurrentResult.AttackerSuccesses, Is.Zero);
            Assert.That(s.CanSpend(false), Is.False);
            Assert.That(s.AttackerDice[0], Is.True);
        }

        [Test]
        public void EmptyOpposingSuccessPool_DisablesSuppression()
        {
            var d = new PlayerSetupData(); var card = Card(TacticEffect.SuppressSuccess); d.Tactics.Add(card);
            var s = Session(Unit(new PlayerSetupData()), Unit(d), new[] { false }, new[] { true });
            s.TryNextFateTurn(out _);
            Assert.That(s.HasTactic(true), Is.False);
            Assert.That(s.TryPlayTactic(true, card), Is.False);
            Assert.That(d.Tactics.Cards.Count, Is.EqualTo(1));
        }

        [Test]
        public void OneCardPerSidePerChallenge_ConsumesExactlyOnce_AndRejectsForeignCards()
        {
            var a = new PlayerSetupData(); var d = new PlayerSetupData();
            var first = Card(TacticEffect.AddSuccess); var second = Card(TacticEffect.AddSuccess);
            var foreign = Card(TacticEffect.AddSuccess);
            a.Tactics.Add(first); a.Tactics.Add(second); d.Tactics.Add(foreign);
            var s = Session(Unit(a), Unit(d), new[] { true }, new[] { false });
            s.TryNextFateTurn(out _); s.ReportFateTurn(false); s.TryNextFateTurn(out _);
            Assert.That(s.TryPlayTactic(false, foreign), Is.False);
            Assert.That(s.TryPlayTactic(false, first), Is.True);
            Assert.That(s.TryPlayTactic(false, first), Is.False);
            Assert.That(s.TryPlayTactic(false, second), Is.False);
            Assert.That(a.Tactics.Cards, Is.EqualTo(new[] { second }));
            Assert.That(a.Tactics.Version, Is.EqualTo(3));
        }

        [Test]
        public void RollResult_TransportsModifiersIntoRealDamageAndSkills()
        {
            var a = new PlayerSetupData(); var attacker = Unit(a); var defender = Unit(new PlayerSetupData(), 10);
            attacker.Abilities.Add(UnitAbilities.CriticalDamage);
            var card = Card(TacticEffect.AddSuccess); a.Tactics.Add(card);
            var s = Session(attacker, defender, new[] { true }, new[] { true });
            s.TryNextFateTurn(out _); s.ReportFateTurn(false); s.TryNextFateTurn(out _); s.TryPlayTactic(false, card);
            int predicted = s.ResolveGroundOutcome().Damage;
            var applied = BattleEngine.ResolveStandaloneAttack(attacker, defender, s.ToRollResult(), AbilityMagnitudes.Default);
            Assert.That(predicted, Is.GreaterThan(1));
            Assert.That(applied.Damage, Is.EqualTo(predicted));
            Assert.That(defender.HitPointsCurrent, Is.EqualTo(10 - predicted));
        }

        [Test]
        public void CaptureKill_DefenderTacticTurnsLethalTieIntoEscape()
        {
            var d = new PlayerSetupData(); var card = Card(TacticEffect.AddSuccess); d.Tactics.Add(card);
            var s = Session(Unit(new PlayerSetupData()), Unit(d), new[] { true }, new[] { true },
                mode: BattleChallengeMode.CaptureKill);
            Assert.That(s.ResolveCaptureKillOutcome(), Is.EqualTo(CaptureKillOutcome.Killed));
            s.TryNextFateTurn(out _); s.TryPlayTactic(true, card);
            var roll = s.ToRollResult();
            Assert.That(BattleResolutionRules.ResolveCaptureKill(roll.AttackerDice, roll.DefenderDice,
                roll.AttackerModifier, roll.DefenderModifier), Is.EqualTo(CaptureKillOutcome.Escaped));
        }

        [TestCase(TacticEffect.AddSuccess, 1)]
        [TestCase(TacticEffect.SuppressSuccess, 1)]
        [TestCase(TacticEffect.AddSuccess, 2)]
        [TestCase(TacticEffect.SuppressSuccess, 2)]
        [TestCase(TacticEffect.AddAndSuppressSuccess, 1)]
        public void Production_TacticRescuesPaidAttemptWithoutChangingDefinition(TacticEffect effect, int amount)
        {
            var owner = new PlayerSetupData(); var hero = Unit(owner); var product = new CardDefinition { fate = 4 };
            var card = Card(effect, amount); owner.Tactics.Add(card);
            var session = new ResearchProductionChallengeSession(hero, product)
                { Dice = new[] { true, true, true, false, false } };
            Assert.That(session.Success, Is.False);
            Assert.That(session.HasTactic, Is.True);
            Assert.That(session.TryPlay(card), Is.True);
            Assert.That(session.Success, Is.True);
            Assert.That(product.fate, Is.EqualTo(4));
            Assert.That(owner.Tactics.Cards.Count, Is.Zero);
            Assert.That(session.TryPlay(card), Is.False);
        }

        [Test]
        public void Ai_PreservesCardsOutsideCriticalEncounters_AndWhenCardCannotSaveUnit()
        {
            var d = new PlayerSetupData(); var card = Card(TacticEffect.AddSuccess); d.Tactics.Add(card);
            var s = Session(Unit(new PlayerSetupData()), Unit(d, 1), new[] { true }, new[] { false });
            Assert.That(s.ChooseAiTactic(true), Is.Null);
            s = Session(Unit(new PlayerSetupData()), Unit(d, 1), new[] { true, true, true }, new[] { false }, critical: true);
            Assert.That(s.ChooseAiTactic(true), Is.Null);
            Assert.That(d.Tactics.Cards.Count, Is.EqualTo(1));
        }

        [Test]
        public void Ai_CriticalLethalHit_IsPreventedWithOneCard()
        {
            var d = new PlayerSetupData(); var card = Card(TacticEffect.AddSuccess); d.Tactics.Add(card);
            var s = Session(Unit(new PlayerSetupData()), Unit(d, 1), new[] { true }, new[] { false }, critical: true);
            s.TryNextFateTurn(out _);
            Assert.That(s.ChooseAiTactic(true), Is.SameAs(card));
            Assert.That(s.TryPlayTactic(true, s.ChooseAiTactic(true)), Is.True);
            Assert.That(s.ResolveGroundOutcome().Damage, Is.Zero);
        }

#if UNITY_6000_3_OR_NEWER
        [Test]
        public void MatchPreparation_ClearsAnExistingPlayersTacticInventory()
        {
            var owner = new PlayerSetupData(); owner.Tactics.Add(Card(TacticEffect.AddSuccess));
            Assert.That(Game.Core.GameSession.TryPrepareMatch(new List<PlayerSetupData> { owner }, null, out _), Is.True);
            Assert.That(owner.Tactics.Cards, Is.Empty);
        }

#endif
        [Test]
        public void AuthoredCards_AreFreeEventRewardsOutsideTheDeck_AndLoadWithExistingContent()
        {
            var blocks = AttachmentContentTestData.Blocks(AttachmentContentTestData.NeutralPath).ToList();
            var tactics = blocks.Where(b => (CardType)AttachmentContentTestData.Number(b, "cardType") == CardType.Tactic)
                .Select(b =>
                {
                    var card = AttachmentContentTestData.Read(b);
                    card.tacticEffect = (TacticEffect)AttachmentContentTestData.Number(b, "tacticEffect");
                    card.tacticAmount = AttachmentContentTestData.Number(b, "tacticAmount");
                    return card;
                }).ToList();
            Assert.That(tactics.Count, Is.EqualTo(5));
            Assert.That(tactics.Select(c => $"{c.tacticEffect}:{c.tacticAmount}"), Is.EquivalentTo(new[]
                { "AddSuccess:1", "SuppressSuccess:1", "AddSuccess:2", "SuppressSuccess:2", "AddAndSuppressSuccess:1" }));
            foreach (var card in tactics)
            {
                Assert.That(TacticRules.IsTactic(card), Is.True);
                Assert.That(DeckRules.Category(card), Is.EqualTo(DeckCategory.System));
                Assert.That(card.apCost, Is.Zero);
                Assert.That(card.resourceCost.human + card.resourceCost.energy
                    + card.resourceCost.materials + card.resourceCost.tech, Is.Zero);
                Assert.That(AttachmentContentTestData.Text("Assets/Cards/EventCatalog.asset"), Does.Contain(card.authoredKey));
            }
        }

        [TestCase(false, 0)]
        [TestCase(false, 2)]
        [TestCase(true, 0)]
        [TestCase(true, 2)]
        public void CombinedCard_ChangesBothSides_ClampsAtZero_AndConsumesOnce(bool defending, int opposing)
        {
            var owner = new PlayerSetupData(); var card = Card(TacticEffect.AddAndSuppressSuccess);
            owner.Tactics.Add(card);
            var ownDice = new[] { false, false };
            var opposingDice = Enumerable.Repeat(true, opposing).ToArray();
            var s = Session(Unit(defending ? new PlayerSetupData() : owner),
                Unit(defending ? owner : new PlayerSetupData()),
                defending ? opposingDice : ownDice, defending ? ownDice : opposingDice);
            s.TryNextFateTurn(out _);
            if (!defending) { s.ReportFateTurn(false); s.TryNextFateTurn(out _); }
            Assert.That(s.TryPlayTactic(defending, card), Is.True);
            var result = s.CurrentResult;
            Assert.That(defending ? result.DefenderSuccesses : result.AttackerSuccesses, Is.EqualTo(1));
            Assert.That(defending ? result.AttackerSuccesses : result.DefenderSuccesses,
                Is.EqualTo(Math.Max(0, opposing - 1)));
            Assert.That(s.AttackerDice, Is.EqualTo(defending ? opposingDice : ownDice));
            Assert.That(s.DefenderDice, Is.EqualTo(defending ? ownDice : opposingDice));
            Assert.That(s.TryPlayTactic(defending, card), Is.False);
            Assert.That(owner.Tactics.Cards, Is.Empty);
            Assert.That(owner.Tactics.Version, Is.EqualTo(2));
        }

        [Test]
        public void ProductionCombinedCard_ClosesTwoSuccessGap_WithoutChangingProductOrDice()
        {
            var owner = new PlayerSetupData(); var card = Card(TacticEffect.AddAndSuppressSuccess);
            owner.Tactics.Add(card);
            var product = new CardDefinition { fate = 4 };
            var dice = new[] { true, true, false, false, false };
            var s = new ResearchProductionChallengeSession(Unit(owner), product) { Dice = dice };
            Assert.That(s.Success, Is.False);
            Assert.That(s.TryPlay(card), Is.True);
            Assert.That(s.SuccessModifier, Is.EqualTo(1));
            Assert.That(s.DifficultyModifier, Is.EqualTo(-1));
            Assert.That(s.Successes, Is.EqualTo(3));
            Assert.That(s.Required, Is.EqualTo(3));
            Assert.That(s.Success, Is.True);
            Assert.That(product.fate, Is.EqualTo(4));
            Assert.That(s.Dice, Is.EqualTo(new[] { true, true, false, false, false }));
            Assert.That(owner.Tactics.Version, Is.EqualTo(2));
        }

        [TestCase(TacticEffect.AddSuccess, 2)]
        [TestCase(TacticEffect.SuppressSuccess, 2)]
        [TestCase(TacticEffect.AddAndSuppressSuccess, 1)]
        public void Ai_StrongerCardCanSecureCriticalKill_WhenPlusOneCannot(TacticEffect effect, int amount)
        {
            var owner = new PlayerSetupData(); var weak = Card(TacticEffect.AddSuccess);
            var strong = Card(effect, amount); owner.Tactics.Add(weak); owner.Tactics.Add(strong);
            var s = Session(Unit(owner), Unit(new PlayerSetupData(), 2),
                new[] { true, true }, new[] { true, true }, critical: true);
            s.TryNextFateTurn(out _); s.ReportFateTurn(false); s.TryNextFateTurn(out _);
            Assert.That(s.ChooseAiTactic(false), Is.SameAs(strong));
            Assert.That(s.TryPlayTactic(false, strong), Is.True);
            Assert.That(s.ResolveGroundOutcome().Damage, Is.EqualTo(2));
            Assert.That(owner.Tactics.Cards, Is.EqualTo(new[] { weak }));
        }

        [Test]
        public void WrongTurn_ReturnsAnInfoReason_WithoutConsumingOrChangingTheRoll()
        {
            var owner = new PlayerSetupData(); var card = Card(TacticEffect.AddSuccess); owner.Tactics.Add(card);
            var session = Session(Unit(owner), Unit(new PlayerSetupData()), new[] { true }, new[] { true });
            session.TryNextFateTurn(out _);
            Assert.That(session.TryPlayTactic(false, card, out string reason), Is.False);
            Assert.That(reason, Does.Contain("defender acts first"));
            Assert.That(session.AttackerModifier, Is.Zero);
            Assert.That(owner.Tactics.Cards, Is.EqualTo(new[] { card }));
        }

        [Test]
        public void SuppressionWithoutSuccess_ReturnsTheSameReasonForCombatAndProduction()
        {
            var owner = new PlayerSetupData(); var card = Card(TacticEffect.SuppressSuccess); owner.Tactics.Add(card);
            var session = Session(Unit(new PlayerSetupData()), Unit(owner), new[] { false }, new[] { true });
            session.TryNextFateTurn(out _);
            Assert.That(session.TryPlayTactic(true, card, out string reason), Is.False);
            var production = new ResearchProductionChallengeSession(Unit(owner), new CardDefinition { fate = 0 })
                { Dice = new[] { true } };
            Assert.That(production.TryPlay(card, out string productionReason), Is.False);
            Assert.That(reason, Is.EqualTo("There are no opposing successes to remove."));
            Assert.That(productionReason, Is.EqualTo(reason));
            Assert.That(owner.Tactics.Version, Is.EqualTo(1));
        }

        [Test]
        public void CardLimitAndForeignCard_ReturnDistinctInfoReasons_AndDoNotSpendAgain()
        {
            var owner = new PlayerSetupData(); var card = Card(TacticEffect.AddSuccess);
            var next = Card(TacticEffect.AddSuccess); owner.Tactics.Add(card); owner.Tactics.Add(next);
            var session = Session(Unit(owner), Unit(new PlayerSetupData()), new[] { true }, new[] { true });
            session.TryNextFateTurn(out _); session.ReportFateTurn(false); session.TryNextFateTurn(out _);
            Assert.That(session.TryPlayTactic(false, Card(TacticEffect.AddSuccess), out string foreign), Is.False);
            Assert.That(foreign, Does.Contain("not in your hand"));
            Assert.That(session.TryPlayTactic(false, card, out string success), Is.True);
            Assert.That(success, Is.Null);
            Assert.That(session.TryPlayTactic(false, next, out string limit), Is.False);
            Assert.That(limit, Does.Contain("already used"));
            Assert.That(session.AttackerModifier, Is.EqualTo(1));
            Assert.That(owner.Tactics.Cards, Is.EqualTo(new[] { next }));
        }

        [Test]
        public void ProductionBeforeRoll_ReturnsInfoReason_AndPreservesCard()
        {
            var owner = new PlayerSetupData(); var card = Card(TacticEffect.AddSuccess); owner.Tactics.Add(card);
            var production = new ResearchProductionChallengeSession(Unit(owner), new CardDefinition { fate = 4 });
            Assert.That(production.TryPlay(card, out string reason), Is.False);
            Assert.That(reason, Does.Contain("dice roll"));
            Assert.That(owner.Tactics.Cards.Count, Is.EqualTo(1));
        }

#if UNITY_6000_3_OR_NEWER
        [Test]
        public void HumanNoFate_WithTactic_WaitsForDecisionInsteadOfAutoAccepting()
        {
            var d = new PlayerSetupData { IsHuman = true }; var card = Card(TacticEffect.AddSuccess); d.Tactics.Add(card);
            var attacker = Unit(new PlayerSetupData()); var defender = Unit(d);
            var session = Session(attacker, defender, new[] { true }, new[] { false });
            session.TryNextFateTurn(out _);
            var popup = (BattleAttackPopupUI)FormatterServices.GetUninitializedObject(typeof(BattleAttackPopupUI));
            Set(popup, "_attacker", attacker); Set(popup, "_defender", defender); Set(popup, "_challengeSession", session);
            var loop = (IEnumerator)typeof(BattleAttackPopupUI).GetMethod("RunHumanTurn", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(popup, new object[] { true });
            Assert.That(loop.MoveNext(), Is.True);
            Assert.That((bool)Get(popup, "_awaitingHumanDecision"), Is.True);
            Set(popup, "_humanDeclined", true);
            Assert.That(loop.MoveNext(), Is.False);
            Assert.That(d.Tactics.Cards.Count, Is.EqualTo(1));
        }

#endif
        private static void Set(object target, string name, object value) => target.GetType()
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(target, value);
        private static object Get(object target, string name) => target.GetType()
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(target);
    }
}
#endif
