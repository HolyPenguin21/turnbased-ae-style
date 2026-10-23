#if UNITY_INCLUDE_TESTS
using System.Collections;
using System.Collections.Generic;
using Game.Ai;
using Game.Ai.V2;
using Game.Cards;
using Game.Players;
using NUnit.Framework;

namespace Game.EditorTests
{
    // 2026-10-17 — frame pacing of the AI turn and the reuse key of Development's
    // (equipment, recipient) verdicts across the passes of one turn.
    public sealed class AiTurnPacingAndVerdictKeyTests
    {
        [SetUp] public void SetUp() => AiMapMemory.Clear();
        [TearDown] public void TearDown() => AiMapMemory.Clear();

        [Test]
        public void Checkpoint_YieldsOnlyOnceTheBudgetIsSpent_ThenStartsANewBudget()
        {
            var pacer = new FramePacer();
            Assert.That(pacer.Checkpoint().MoveNext(), Is.False, "a fresh budget does not yield");

            System.Threading.Thread.Sleep((int)(FramePacer.BudgetSeconds * 1000) + 10);
            IEnumerator spent = pacer.Checkpoint();
            Assert.That(spent.MoveNext(), Is.True, "a spent budget gives the frame back");
            Assert.That(spent.Current, Is.Null);
            Assert.That(spent.MoveNext(), Is.False);

            Assert.That(pacer.Checkpoint().MoveNext(), Is.False, "the next frame starts a new budget");
        }

        private static WorldSnapshot Snap(int turn) => new WorldSnapshot { TurnNumber = turn };

        [Test]
        public void VerdictKey_IsStableForTheSameInputs()
        {
            var player = new PlayerSetupData();
            var hand = new AiHandData(null, default, 0);
            hand.AddCard(new CardData(AttachmentSlotTests.Host(hero: true)));
            Assert.That(DevelopmentAdmission.RecipientVerdictKey(Snap(4), null, player, hand),
                Is.EqualTo(DevelopmentAdmission.RecipientVerdictKey(Snap(4), null, player, hand)));
        }

        [Test]
        public void VerdictKey_ChangesWithARecipientTheTurnOrTheMemorySession()
        {
            var player = new PlayerSetupData();
            var hand = new AiHandData(null, default, 0);
            hand.AddCard(new CardData(AttachmentSlotTests.Host(hero: true)));
            string before = DevelopmentAdmission.RecipientVerdictKey(Snap(4), null, player, hand);

            var recipient = new CardData(AttachmentSlotTests.Host(hero: false));
            hand.AddCard(recipient);
            Assert.That(DevelopmentAdmission.RecipientVerdictKey(Snap(4), null, player, hand),
                Is.Not.EqualTo(before), "a new hand recipient");
            hand.RemoveCard(recipient);
            Assert.That(DevelopmentAdmission.RecipientVerdictKey(Snap(4), null, player, hand), Is.EqualTo(before));

            Assert.That(DevelopmentAdmission.RecipientVerdictKey(Snap(5), null, player, hand),
                Is.Not.EqualTo(before), "another turn");

            AiMapMemory.Clear();
            Assert.That(DevelopmentAdmission.RecipientVerdictKey(Snap(4), null, player, hand),
                Is.Not.EqualTo(before), "a new memory session never reuses old verdicts");
        }

        // The key's text memos (roster digest, profile rows, deck benchmarks) are reused by
        // identity; a change made in place inside the same lists must still change the key.
        [Test]
        public void VerdictKey_SeesInPlaceRosterAndDeckChanges()
        {
            var player = new PlayerSetupData();
            var hand = new AiHandData(null, default, 0);
            var roster = new List<Game.Combat.WorthIt.DefenderProfile> {
                new Game.Combat.WorthIt.DefenderProfile(1, false, null, 3, 4, 1, new[] { UnitAbilities.Berserk }) };
            var deck = new List<CardDefinition> { AttachmentSlotTests.Host(hero: false) };
            var snap = Snap(4);
            snap.Self = new SelfSnapshot { Hand = hand.Hand, Deck = deck,
                Armies = new[] { new ArmySnapshot { ArmyId = 7, MembersWithHeroes = roster } } };
            string before = DevelopmentAdmission.RecipientVerdictKey(snap, null, player, hand);
            Assert.That(DevelopmentAdmission.RecipientVerdictKey(snap, null, player, hand), Is.EqualTo(before));

            roster[0] = new Game.Combat.WorthIt.DefenderProfile(2, false, null, 3, 4, 1, new[] { UnitAbilities.Berserk });
            string wounded = DevelopmentAdmission.RecipientVerdictKey(snap, null, player, hand);
            Assert.That(wounded, Is.Not.EqualTo(before), "a replaced profile in the same roster list");

            var other = AttachmentSlotTests.Host(hero: false);
            other.attack += 2;
            deck[0] = other;
            Assert.That(DevelopmentAdmission.RecipientVerdictKey(snap, null, player, hand),
                Is.Not.EqualTo(wounded), "another card in the same deck list");
        }
    }
}
#endif
