#if UNITY_INCLUDE_TESTS
using Game.Ai;
using Game.Ai.V2;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using NUnit.Framework;

namespace Game.EditorTests
{
    // Owner decisions of 2026-10-24 on the Phase A review that need no Phase A pass to check:
    // follow-up AP promises end once their army has stepped (L3), the local-refit promise has one
    // null-safe rule (L7), and a delivered builder Hero or Base builder escort claims its builder in
    // Phase A and Phase B alike (L8).
    public class AiPhaseAOwnerDecisionTests
    {
        private PlayerSetupData _player;

        [SetUp]
        public void SetUp() => _player = new PlayerSetupData();

        [TearDown]
        public void TearDown() => ArmyRegistry.Clear();

        private ArmyData Army() => new ArmyData { Owner = _player, Hex = new HexCoord(1, 1) };

        [Test]
        public void DeliveryFollowup_StaysUntilItsArmySteps()
        {
            var budget = PhaseAApBudget.Create(null);
            ArmyData army = Army();
            budget.ReserveDeliveryFollowup(army, 2f);

            budget.ReleaseSpentDeliveryFollowups(id => id == army.Id ? army : null);
            Assert.That(budget.ReservedFollowup(), Is.EqualTo(2f), "the army has not stepped yet");

            army.MarkActivated();
            budget.ReleaseSpentDeliveryFollowups(id => id == army.Id ? army : null);
            Assert.That(budget.ReservedFollowup(), Is.Zero, "its activation took the promised step");
        }

        [Test]
        public void DeliveryFollowup_EndsWithItsArmy_AndOneArmyKeepsOnePromise()
        {
            var budget = PhaseAApBudget.Create(null);
            ArmyData army = Army();
            ArmyData other = Army();
            budget.ReserveDeliveryFollowup(army, 2f);
            budget.ReserveDeliveryFollowup(army, 3f);
            budget.ReserveDeliveryFollowup(other, 1f);
            Assert.That(budget.ReservedFollowup(), Is.EqualTo(4f), "one next activation per army: 3 + 1");

            budget.ReleaseSpentDeliveryFollowups(id => id == army.Id ? army : null);
            Assert.That(budget.ReservedFollowup(), Is.EqualTo(3f), "the vanished army's promise is gone");
        }

        [Test]
        public void DeliveryFollowup_DoesNotTouchRefitPromises()
        {
            var budget = PhaseAApBudget.Create(null);
            budget.ReserveActorFollowup(4242, 3f);
            budget.ReleaseSpentDeliveryFollowups(id => null);
            Assert.That(budget.ActorFollowup(4242), Is.EqualTo(3f), "refit promises are recomputed by Phase A, not here");
        }

        [Test]
        public void RefitFollowup_IsZeroWhenThePrimaryIsGone()
        {
            Assert.That(StrategicPhaseA.RefitFollowupAp(_player, null,
                new AiTurnContext { TurnNumber = 5 }, null, 4242), Is.Zero);
        }

        [Test]
        public void RefitFollowup_IsZeroWithoutItsAttackOperation()
        {
            ArmyData primary = Army();
            ArmyRegistry.Register(primary);
            Assert.That(StrategicPhaseA.RefitFollowupAp(_player, null,
                new AiTurnContext { TurnNumber = 5 }, null, primary.Id), Is.Zero);
        }

        [TestCase(CapabilityKind.Hero, false, true)]
        [TestCase(CapabilityKind.FieldCombatPower, true, true)]
        [TestCase(CapabilityKind.FieldCombatPower, false, false)]
        public void DeliveredPrerequisite_ClaimsItsBuilder(CapabilityKind capability, bool escort, bool claims)
        {
            var demand = new AxisDemand
            {
                RequestingAxis = DesireAxis.Economy, Capability = capability,
                EconomyEscortArmyId = escort ? 31 : (int?)null, EconomyPreferredBuilderArmyId = 31,
            };
            var commitments = new ActorCommitments();
            CapabilityDeliveryEvaluator.ClaimEconomyBuilder(commitments, demand);
            Assert.That(commitments.IsArmyClaimed(31), Is.EqualTo(claims));
        }

        [Test]
        public void DeliveredPrerequisite_WithoutAChosenBuilder_ClaimsNothing()
        {
            var demand = new AxisDemand { RequestingAxis = DesireAxis.Economy, Capability = CapabilityKind.Hero };
            var commitments = new ActorCommitments();
            Assert.DoesNotThrow(() => CapabilityDeliveryEvaluator.ClaimEconomyBuilder(commitments, demand));
            Assert.DoesNotThrow(() => CapabilityDeliveryEvaluator.ClaimEconomyBuilder(null, demand));
        }
    }
}
#endif
