#if UNITY_INCLUDE_TESTS
using System;
using System.Collections.Generic;
using System.Linq;
using Game.Ai.V2;
using Game.Cards;
using Game.HexGrid;
using Game.Players;
using NUnit.Framework;

namespace Game.EditorTests
{
    public class AiTaskScoreBaseSwitchRegressionTests
    {
        private static AxisDemand BaseDemand(CardData card, int actor, int q,
            float net, float site) => new AxisDemand
        {
            RequestingAxis = DesireAxis.Economy,
            Capability = CapabilityKind.EconomicExpansionBase,
            DesiredAmount = 1f,
            TargetHex = new HexCoord(q, 0),
            EconomyBuildCard = card,
            EconomyPreferredBuilderArmyId = actor,
            Value = net,
            EconomySiteValue = site,
            EconomyBuildApCost = 2f,
            MinimumFollowupAp = 2f,
        };

        [Test]
        public void Selector_SeesChallengerBeyondActiveFirstCap_UsingFullValue()
        {
            var card = new CardData(new CardDefinition { cardType = CardType.Base });
            var player = new PlayerSetupData();
            try
            {
                AxisDemand original = BaseDemand(card, 9, 3, 20f, 40f);
                MissionIntent incumbent = EconomyContinuity.BeginEconomyDelivery(
                    player, original, 9, 1);
                AxisDemand oldSite = BaseDemand(card, 9, 3, 20f, 40f);
                AxisDemand rival = BaseDemand(card, 9, 6, 34f, 38f);
                AxisDemand chosen = DemandLayer.SelectBaseDemandForCurrentCommitment(
                    new[] { oldSite, rival }, new[] { incumbent });
                Assert.That(chosen, Is.SameAs(rival));
                Assert.That(chosen.EconomySwitchIncumbentValue, Is.EqualTo(20f));
                Assert.That(DemandLayer.CanReplaceCommittedBase(incumbent, chosen), Is.True);
                Assert.That(DemandLayer.SelectBaseDemandForCurrentCommitment(
                    new[] { oldSite, BaseDemand(card, 9, 7, 29f, 80f) },
                    new[] { incumbent }), Is.SameAs(oldSite),
                    "a site-only score of 80 must not defeat net 20 by less than the +10 margin");
                Assert.That(DemandLayer.SelectBaseDemandForCurrentCommitment(
                    new[] { oldSite, BaseDemand(card, 11, 8, 90f, 90f) },
                    new[] { incumbent }), Is.SameAs(oldSite),
                    "a different actor cannot steal this already committed card/mission");
            }
            finally { MissionIntentRegistry.Clear(); }
        }

        [Test]
        public void Selector_RejectsUnqualifiedRivalWhenIncumbentSiteMissingOrScoreUnknown()
        {
            var player = new PlayerSetupData();
            var card = new CardData(new CardDefinition { cardType = CardType.Base });
            try
            {
                MissionIntent incumbent = EconomyContinuity.BeginEconomyDelivery(
                    player, BaseDemand(card, 9, 3, 20f, 40f), 9, 1);
                AxisDemand rival = BaseDemand(card, 9, 6, 29f, 80f);
                Assert.That(DemandLayer.SelectBaseDemandForCurrentCommitment(
                    new[] { rival }, new[] { incumbent }), Is.Null,
                    "a rejected rival must not escape as the only executable demand");
                incumbent.Economy.IntrinsicValue = null;
                rival.Value = 90f;
                Assert.That(DemandLayer.SelectBaseDemandForCurrentCommitment(
                    new[] { rival }, new[] { incumbent }), Is.Null,
                    "unknown incumbent net value must not authorize any switch");
                AxisDemand incumbentSite = BaseDemand(card, 9, 3, 20f, 40f);
                Assert.That(DemandLayer.SelectBaseDemandForCurrentCommitment(
                    new[] { incumbentSite, rival }, new[] { incumbent }),
                    Is.SameAs(rival),
                    "A current scan of the incumbent site supplies the previously unknown value");
                Assert.That(rival.EconomySwitchIncumbentValue, Is.EqualTo(20f));
            }
            finally { MissionIntentRegistry.Clear(); }
        }

        [Test]
        public void Retarget_RejectsMidTurnMoveWithoutChangingDurableIntent()
        {
            var player = new PlayerSetupData();
            var card = new CardData(new CardDefinition { cardType = CardType.Base });
            try
            {
                MissionIntent incumbent = EconomyContinuity.BeginEconomyDelivery(
                    player, BaseDemand(card, 9, 3, 20f, 40f), 9, 1);
                MissionIntentKey oldKey = incumbent.IntentKey;
                StableMissionKey oldAttempt = incumbent.LastAttemptKey;
                incumbent.LastProgressTurn = 2;
                incumbent.StepsMovedTotal = 1;
                AxisDemand rival = BaseDemand(card, 9, 6, 34f, 38f);
                rival.EconomySwitchIncumbentValue = 20f;
                Assert.That(EconomyContinuity.TryRetargetCommittedBase(
                    player, incumbent, rival, 2), Is.False);
                Assert.That(incumbent.IntentKey, Is.EqualTo(oldKey));
                Assert.That(incumbent.LastAttemptKey, Is.EqualTo(oldAttempt));
                Assert.That(MissionIntentRegistry.GetOrCreate(player).Count, Is.EqualTo(1));
            }
            finally { MissionIntentRegistry.Clear(); }
        }

        // Playtest 2026-10-16: a Base admitted at +0.4 was held at -2..-7 for three turns. The
        // admission predicate (positive net TaskScore) is re-read on the refreshed same-site value.
        [Test]
        public void Release_EndsOnlyACommittedBaseWhoseRefreshedValueNoLongerClearsAdmission()
        {
            var player = new PlayerSetupData();
            var card = new CardData(new CardDefinition { cardType = CardType.Base });
            try
            {
                MissionIntent incumbent = EconomyContinuity.BeginEconomyDelivery(
                    player, BaseDemand(card, 9, 3, 0.4f, 10f), 9, 3);
                MissionIntentState state = MissionIntentRegistry.GetOrCreate(player);
                Assert.That(EconomyContinuity.HasEconomyBuildCommitment(new[] { incumbent },
                    BaseDemand(card, 9, 3, -3.5f, 10f)), Is.True, "Phase A finds the refreshed same-site value");
                Assert.That(EconomyContinuity.TryReleaseUnprofitableBuild(state, player, incumbent,
                    BaseDemand(card, 9, 3, 2f, 10f), 4),
                    Is.False, "a still profitable Base keeps its commitment");
                incumbent.LastProgressTurn = 4;
                incumbent.StepsMovedTotal = 1;
                Assert.That(EconomyContinuity.TryReleaseUnprofitableBuild(state, player, incumbent,
                    BaseDemand(card, 9, 3, -3.5f, 10f), 4),
                    Is.False, "a builder that already advanced this turn finishes its step first");
                Assert.That(state.Count, Is.EqualTo(1));
                Assert.That(EconomyContinuity.TryReleaseUnprofitableBuild(state, player, incumbent,
                    BaseDemand(card, 9, 3, -3.5f, 10f), 5),
                    Is.True);
                Assert.That(state.Count, Is.EqualTo(0));
            }
            finally { MissionIntentRegistry.Clear(); }
        }

        [Test]
        public void Retarget_PreservesOneActorAndCard_RekeysIntentAndAttempt()
        {
            var player = new PlayerSetupData();
            var card = new CardData(new CardDefinition { cardType = CardType.Base });
            try
            {
                MissionIntent incumbent = EconomyContinuity.BeginEconomyDelivery(
                    player, BaseDemand(card, 9, 3, 20f, 40f), 9, 1);
                incumbent.Funding = CommitmentTier.Hard;
                MissionIntentKey oldKey = incumbent.IntentKey;
                StableMissionKey oldAttempt = incumbent.LastAttemptKey;
                AxisDemand rival = BaseDemand(card, 9, 6, 34f, 38f);
                rival.EconomySwitchIncumbentValue = 20f;
                Assert.That(EconomyContinuity.TryRetargetCommittedBase(
                    player, incumbent, rival, 2), Is.True);
                MissionIntentState state = MissionIntentRegistry.GetOrCreate(player);
                Assert.That(state.Count, Is.EqualTo(1));
                Assert.That(state.TryGet(oldKey, out _), Is.False);
                Assert.That(state.TryGet(incumbent.IntentKey, out MissionIntent found), Is.True);
                Assert.That(found, Is.SameAs(incumbent));
                Assert.That(incumbent.LastAttemptKey, Is.Not.EqualTo(oldAttempt));
                Assert.That(incumbent.PreferredMoverArmyId, Is.EqualTo(9));
                Assert.That(incumbent.Economy.BuildCard, Is.SameAs(card));
                Assert.That(incumbent.Economy.IntrinsicValue, Is.EqualTo(34f));
                Assert.That(incumbent.Economy.BuildValue, Is.EqualTo(38f));
                Assert.That(incumbent.Funding, Is.EqualTo(CommitmentTier.Hard));
                Assert.That(EconomyContinuity.TryRetargetCommittedBase(
                    player, incumbent, rival, 2), Is.False,
                    "a switched intent cannot re-switch onto its already-owned target");
            }
            finally { MissionIntentRegistry.Clear(); }
        }
    }
}
#endif
