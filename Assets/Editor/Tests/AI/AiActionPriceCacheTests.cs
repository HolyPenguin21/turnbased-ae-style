#if UNITY_INCLUDE_TESTS
using System;
using System.Collections.Generic;
using Game.Ai.V2;
using Game.Cards;
using Game.Economy;
using NUnit.Framework;

namespace Game.EditorTests
{
    public sealed class AiActionPriceCacheTests
    {
        private static WorldSnapshot Snapshot(IReadOnlyList<CardDefinition> deck,
            IReadOnlyList<CardData> hand = null) => new WorldSnapshot
        {
            Self = new SelfSnapshot
            {
                Deck = deck, Hand = hand ?? Array.Empty<CardData>(),
                Stockpile = new ResourceBundle { Human = 5, Energy = 5, Materials = 5, Tech = 5 }
            }
        };

        private static CardDefinition Card(int h, int e = 0, int m = 0, int t = 0) =>
            new CardDefinition { resourceCost = new ResourceCost(h, e, m, t) };

        [TestCase(ResourceType.Human)]
        [TestCase(ResourceType.Energy)]
        [TestCase(ResourceType.Materials)]
        [TestCase(ResourceType.Tech)]
        public void SubCentSupplyChangesAgreeWithFreshCalculationInBothOrders(ResourceType changed)
        {
            foreach (bool reverse in new[] { false, true })
            {
                var snap = Snapshot(new[] { Card(1, 1, 1, 1) });
                float Supply(ResourceType type, float amount) => type == changed ? amount : 1;
                float first = reverse ? 1.004f : .996f;
                float second = reverse ? .996f : 1.004f;
                ActionPrice.ScarcityProfile(snap, type => Supply(type, first));
                Assert.That(ActionPrice.ScarcityProfile(snap, type => Supply(type, second)),
                    Is.EqualTo(ActionPrice.ScarcityProfile(Snapshot(snap.Self.Deck),
                        type => Supply(type, second))), $"{changed}, reverse={reverse}");
            }
        }

        [TestCase(ResourceType.Human)]
        [TestCase(ResourceType.Energy)]
        [TestCase(ResourceType.Materials)]
        [TestCase(ResourceType.Tech)]
        public void InPlaceDeckCostEditAndRevertAgreeWithFreshCalculation(ResourceType changed)
        {
            CardDefinition card = Card(1, 1, 1, 1);
            var snap = Snapshot(new[] { card });
            ActionPrice.ScarcityProfile(snap);
            void Set(int value)
            {
                switch (changed)
                {
                    case ResourceType.Human: card.resourceCost.human = value; break;
                    case ResourceType.Energy: card.resourceCost.energy = value; break;
                    case ResourceType.Materials: card.resourceCost.materials = value; break;
                    case ResourceType.Tech: card.resourceCost.tech = value; break;
                }
            }
            foreach (int value in new[] { 10, 1 })
            {
                Set(value);
                Assert.That(ActionPrice.ScarcityProfile(snap),
                    Is.EqualTo(ActionPrice.ScarcityProfile(Snapshot(new[] { card }))));
            }
        }

        [Test]
        public void ReplacingDeckCardAtSameCountRefreshesDemand()
        {
            var deck = new List<CardDefinition> { Card(1) };
            var snap = Snapshot(deck);
            ActionPrice.ScarcityProfile(snap);
            deck[0] = Card(10);
            Assert.That(ActionPrice.ScarcityProfile(snap),
                Is.EqualTo(ActionPrice.ScarcityProfile(Snapshot(deck))));
        }

        [Test]
        public void ProductionCreatedFlagRefreshesEffectiveHandDemand()
        {
            var card = new CardData(Card(10));
            var snap = Snapshot(Array.Empty<CardDefinition>(), new[] { card });
            ActionPrice.ScarcityProfile(snap);
            card.ResearchProductionCreated = true;
            Assert.That(ActionPrice.ScarcityProfile(snap),
                Is.EqualTo(ActionPrice.ScarcityProfile(Snapshot(snap.Self.Deck, new[] { card }))));
        }

        [Test]
        public void EqualTotalCardReorderPreservesColdPlayabilityOrder()
        {
            var deck = new List<CardDefinition> { Card(1, 1), Card(2) };
            var snap = Snapshot(deck);
            float Supply(ResourceType t) => t == ResourceType.Human ? 2 : t == ResourceType.Energy ? 1 : 0;
            ActionPrice.ScarcityProfile(snap, Supply);
            deck.Reverse();
            Assert.That(ActionPrice.ScarcityProfile(snap, Supply),
                Is.EqualTo(ActionPrice.ScarcityProfile(Snapshot(deck), Supply)));
        }

        [Test]
        public void NullCostsAndZeroCostsCanBeReplacedWithoutStaleDemand()
        {
            var deck = new List<CardDefinition> { null, new CardDefinition { resourceCost = null }, Card(0) };
            var snap = Snapshot(deck, new CardData[] { null });
            ActionPrice.ScarcityProfile(snap);
            deck[1].resourceCost = new ResourceCost(10);
            Assert.That(ActionPrice.ScarcityProfile(snap),
                Is.EqualTo(ActionPrice.ScarcityProfile(Snapshot(deck, snap.Self.Hand))));
        }

        [Test]
        public void DevelopmentVerdictDistinguishesEqualTotalsWithDifferentBills()
        {
            var player = new Game.Players.PlayerSetupData();
            var snap = Snapshot(new[] { Card(2), Card(0, 2) });
            snap.Observer = player;
            snap.Self.Stockpile = new ResourceBundle { Human = 1, Energy = 2 };
            string Key() => DevelopmentAdmission.RecipientVerdictKey(snap,
                Array.Empty<MissionIntent>(), player, null);
            string first = Key();
            var before = ActionPrice.ScarcityProfile(snap);
            snap.Self.Deck[0].resourceCost.energy = 2;
            snap.Self.Deck[1].resourceCost.energy = 0;
            Assert.That(Key(), Is.Not.EqualTo(first));
            Assert.That(ActionPrice.ScarcityProfile(snap), Is.Not.EqualTo(before));
        }

        [Test]
        public void DevelopmentPriceKeyDistinguishesSubMillResourceChanges()
        {
            var player = new Game.Players.PlayerSetupData();
            var root = Game.Map.PlayerRoot.Create(player, "price key");
            var context = new Game.Ai.AiTurnContext { TurnNumber = 1 };
            try
            {
                var snap = Snapshot(new[] { Card(1) });
                root.AddResource(ResourceType.Human, 1);
                void Hold(float amount) => StrategicResourceReservationLedger.Upsert(player, 1,
                    new StrategicResourceReservation
                    {
                        Owner = "price-test",
                        Resource = StrategicReservedResource.Human, Amount = amount,
                        Reason = StrategicReservationReason.EconomyBuildCompletion,
                        ExpirationStage = StrategicReservationExpiry.EndOfTurn,
                    });
                Hold(.0001f);
                string key = DevelopmentAdmission.PriceInputs(snap, player, root, context);
                Hold(.0004f);
                Assert.That(DevelopmentAdmission.PriceInputs(snap, player, root, context), Is.Not.EqualTo(key));
            }
            finally
            {
                StrategicResourceReservationLedger.ClearAll();
                UnityEngine.Object.DestroyImmediate(root.gameObject);
            }
        }

        [Test]
        public void CardScoreReadsEachLiveResourceOnceAndSeesTheNextChange()
        {
            var snap = Snapshot(new[] { Card(2, 2, 2, 2) });
            var bill = new ResourceCost(1, 1, 1, 1);
            int reads = 0;
            float amount = 1f;
            float Read(ResourceType type) { reads++; return amount; }
            float before = StrategicCardEvaluator.StrategicResourceCostValue(bill, snap, Read);
            Assert.That(reads, Is.EqualTo(4));
            amount = 3f;
            float after = StrategicCardEvaluator.StrategicResourceCostValue(bill, snap, Read);
            Assert.That(reads, Is.EqualTo(8));
            Assert.That(after, Is.Not.EqualTo(before));
        }

        [Test]
        public void NewSupplyEvictsOneProfileAndLeavesTheOtherHotProfiles()
        {
            var snap = Snapshot(new[] { Card(10) });
            // Verify this property against the real memo, independently of timing and allocations.
            var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;
            var table = typeof(ActionPrice).GetField("ProfileMemo", flags).GetValue(null);
            var memo = table.GetType().GetMethod("GetOrCreateValue").Invoke(table, new object[] { snap });
            var profiles = (System.Collections.IDictionary)memo.GetType()
                .GetField("profiles", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                .GetValue(memo);
            for (int i = 1; i <= 64; i++)
                ActionPrice.ScarcityProfile(snap, _ => i);
            var hot = new System.Collections.Generic.List<object>();
            foreach (object key in profiles.Keys) hot.Add(key);
            ActionPrice.ScarcityProfile(snap, _ => 65f);
            int retained = hot.FindAll(profiles.Contains).Count;
            Assert.That(retained, Is.EqualTo(63));
            snap.Self.Deck[0].resourceCost.human = 20;
            ActionPrice.ScarcityProfile(snap, _ => 65f);
            Assert.That(profiles.Count, Is.EqualTo(1));
        }

        [Test]
        public void ReturnedProfilesCannotCorruptLaterReadsOrDifferentSupplies()
        {
            var snap = Snapshot(new[] { Card(4, 3) });
            var original = ActionPrice.ScarcityProfile(snap);
            var changed = ActionPrice.ScarcityProfile(snap);
            changed[0] = -100;
            for (int supply = 0; supply < 100; supply++)
            {
                float amount = supply;
                ActionPrice.ScarcityProfile(snap, _ => amount);
            }
            Assert.That(ActionPrice.ScarcityProfile(snap), Is.EqualTo(original));
        }
    }
}
#endif
