#if UNITY_INCLUDE_TESTS
using System.Collections.Generic;
using System.Linq;
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
