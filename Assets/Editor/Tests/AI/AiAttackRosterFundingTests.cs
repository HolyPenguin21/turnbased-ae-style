#if UNITY_INCLUDE_TESTS
using System.Collections.Generic;
using System.Linq;
using Game.Ai;
using Game.Ai.V2;
using Game.Cards;
using Game.Map;
using Game.Players;
using NUnit.Framework;

namespace Game.EditorTests
{
    // 2026-11-13 (owner, P7, match Korrin vs Brynn) — Brynn B21-B31 held the Attack preparation
    // on a roster of 3x Iron Maw + Slag Lobber with Materials income 2: Iron Maw was never played.
    // A hand/deck position is expected only while stock + income x economyDeckNeedHorizonTurns
    // pays it; the roster is re-composed from such positions.
    public class AiAttackRosterFundingTests
    {
        private static CardDefinition Card(string key, int materials) => new CardDefinition
        {
            authoredKey = key, displayName = key, cardType = CardType.Unit,
            resourceCost = new ResourceCost(materials: materials),
        };

        private static WorldSnapshot Snap(float materialsStock, float materialsIncome,
            params (CardDefinition Card, bool InHand)[] pool)
        {
            var self = new SelfSnapshot
            {
                PoolCards = pool.Select(p => (p.Card, (CardDefinition)null, (CardDefinition)null, p.InHand)).ToList(),
                AttackPeak = 90f,
            };
            self.PerTurnIncome.Materials = materialsIncome;
            var economy = new EconomyStanding();
            economy.SpendableStockpile.Materials = materialsStock;
            return new WorldSnapshot { Observer = new PlayerSetupData(), TurnNumber = 22, Self = self, Economy = economy };
        }

        private static StrikeRosterCandidate Candidate(string key, float power, ForceSource source) =>
            new StrikeRosterCandidate(new StrikeRosterSlot(key, false, power, source),
                new AiPower.PowerUnit(power, null, 1, false));

        [Test]
        public void ACardIsFundableOnlyWithinTheDeckHorizon()
        {
            WorldSnapshot snap = Snap(0f, 2f);
            float horizon = AiConfigV2.economyDeckNeedHorizonTurns;
            Assert.That(AggressionDemandEvaluator.FundableWithinDeckHorizon(snap,
                new ResourceCost(materials: (int)(2f * horizon))), Is.True);
            Assert.That(AggressionDemandEvaluator.FundableWithinDeckHorizon(snap,
                new ResourceCost(materials: (int)(2f * horizon) + 1)), Is.False);
        }

        // The owner's condition (2026-11-13): the reserve must never stop Research/Production
        // altogether. Only ONE card's cost is held, only on the types it costs; spends above it,
        // spends of other types and spends into the host itself go on.
        [Test]
        public void RosterReserve_HoldsOneCardCost_AndLeavesTheSurplusToEverySpender()
        {
            var player = new PlayerSetupData();
            PlayerRoot root = PlayerRoot.Create(player, "roster reserve test");
            PlayerRootRegistry.Register(player, root);
            ArmyRegistry.Clear();
            AiHandRegistry.Clear();
            try
            {
                foreach (Game.Economy.ResourceType t in new[] { Game.Economy.ResourceType.Energy,
                             Game.Economy.ResourceType.Materials, Game.Economy.ResourceType.Tech })
                    root.AddResource(t, 5);
                var host = new ArmyData { Owner = player };
                ArmyRegistry.Register(host);
                var other = new ArmyData { Owner = player };
                ArmyRegistry.Register(other);
                CardDefinition slag = Card("slag-lobber", 2);
                slag.resourceCost = new ResourceCost(energy: 2, materials: 2, tech: 2);
                var attack = new AttackIntent { Preparation = true, Phase = AttackMissionPhase.Gather,
                    PrimaryArmyId = host.Id,
                    TargetRoster = new List<StrikeRosterSlot> { new StrikeRosterSlot("slag-lobber", false, 12f, ForceSource.Hand) } };
                var need = new StrikeSupportNeed
                {
                    Operation = new MissionIntent { Kind = MissionKind.Attack, Status = IntentStatus.Active, Objective = attack },
                    Attack = attack, Cost = slag.resourceCost, Source = "hand:slag-lobber", RosterKey = "slag-lobber",
                };
                var ctx = new AiTurnContext { TurnNumber = 22 };
                AiHandData hand = AiHandRegistry.GetOrCreate(player, null, 0);
                bool Allowed(ResourceCost cost, int? recipient, CardData card = null) =>
                    PhaseAOperationSupport.LeavesRosterFunding(player, root, ctx, hand, need, cost, card, recipient, "test", out _);

                Assert.That(Allowed(new ResourceCost(tech: 3), other.Id), Is.True,
                    "Research for 3 Tech of 5 leaves the 2 Tech the card needs: allowed");
                Assert.That(Allowed(new ResourceCost(tech: 4), other.Id), Is.False,
                    "4 of 5 would dip into the card's 2 Tech");
                Assert.That(Allowed(new ResourceCost(human: 4), other.Id), Is.True, "Human is not reserved");
                Assert.That(Allowed(new ResourceCost(tech: 5), host.Id), Is.True, "a spend into the host is never held");

                var card = new CardData(slag);
                hand.AddCard(card);
                Assert.That(Allowed(new ResourceCost(), other.Id, card), Is.False,
                    "the only Slag Lobber in hand is the host's");
                hand.AddCard(new CardData(slag));
                Assert.That(Allowed(new ResourceCost(), other.Id, card), Is.True, "a second copy is free");

                host.Members.Add(new Game.Units.UnitData { Owner = player, OriginatingCard = slag });
                Assert.That(Allowed(new ResourceCost(tech: 5), other.Id), Is.True,
                    "the host holds its card: the reserve is gone");
            }
            finally
            {
                ArmyRegistry.Clear();
                AiHandRegistry.Clear();
                PlayerRootRegistry.Clear();
                StrategicResourceReservationLedger.ClearAll();
                UnityEngine.Object.DestroyImmediate(root.gameObject);
            }
        }

        [Test]
        public void UnfundableRosterPosition_ReFreezesTheRosterFromFundablePositions()
        {
            float horizon = AiConfigV2.economyDeckNeedHorizonTurns;
            CardDefinition ironMaw = Card("iron-maw", (int)(2f * horizon) + 5);
            CardDefinition slag = Card("slag-lobber", 1);
            CardDefinition brute = Card("rad-brute", 2);
            WorldSnapshot snap = Snap(0f, 2f, (ironMaw, true), (slag, true), (brute, false));
            snap.Self.StrikePool = new List<StrikeRosterCandidate>
            {
                Candidate("iron-maw", 30f, ForceSource.Hand),
                Candidate("slag-lobber", 12f, ForceSource.Hand),
                Candidate("rad-brute", 10f, ForceSource.Deck),
            };
            snap.Self.StrikePoolKeyCounts = new Dictionary<string, int>
                { ["iron-maw"] = 1, ["slag-lobber"] = 1, ["rad-brute"] = 1 };
            Assert.That(AttackContinuity.RosterCandidateFundable(snap, snap.Self.StrikePool[0]), Is.False);
            Assert.That(AttackContinuity.RosterCandidateFundable(snap, snap.Self.StrikePool[1]), Is.True);

            var host = new ArmyData { Owner = snap.Observer };
            var a = new AttackIntent
            {
                TargetRoster = new List<StrikeRosterSlot>
                {
                    new StrikeRosterSlot("iron-maw", false, 30f, ForceSource.Hand),
                    new StrikeRosterSlot("slag-lobber", false, 12f, ForceSource.Hand),
                },
                TargetRosterPeak = snap.Self.AttackPeak,
                TargetRosterCapacity = StrikeRoster.HostRosterCapacity(host),
            };
            var intent = new MissionIntent { Kind = MissionKind.Attack, Status = IntentStatus.Active, Objective = a };

            AttackContinuity.RefreshTargetRoster(snap, intent, a, host);

            Assert.That(a.TargetRoster.Select(s => s.Key), Does.Not.Contain("iron-maw"),
                "an Iron Maw the income cannot pay is no position to wait for");
            Assert.That(a.TargetRoster.Select(s => s.Key), Does.Contain("slag-lobber"));
        }
    }
}
#endif
