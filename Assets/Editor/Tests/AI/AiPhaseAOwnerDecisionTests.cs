#if UNITY_INCLUDE_TESTS
using System.Collections.Generic;
using System.Linq;
using Game.Ai;
using Game.Cards;
using Game.Economy;
using Game.Ai.V2;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Units;
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

        private UnitData Body(int move) => new UnitData { Owner = _player, Name = "body", MoveMax = move,
            MoveCurrent = move, HitPointsMax = 6, HitPointsCurrent = 6, ActivationApCost = 1 };

        [Test]
        public void DeliveryFollowup_SurvivesASlowerBodyJoining_EndsWhenAMemberSteps()
        {
            var budget = PhaseAApBudget.Create(null);
            ArmyData army = Army();
            UnitData fast = Body(4);
            army.Members.Add(fast);
            budget.ReserveDeliveryFollowup(army, 2f);

            army.Members.Add(Body(2));
            budget.ReleaseSpentDeliveryFollowups(id => id == army.Id ? army : null);
            Assert.That(budget.ReservedFollowup(), Is.EqualTo(2f),
                "a slower body lowers the army's movement, but no step was taken");

            fast.MoveCurrent = 3;
            budget.ReleaseSpentDeliveryFollowups(id => id == army.Id ? army : null);
            Assert.That(budget.ReservedFollowup(), Is.Zero, "a member of the promise spent movement: the army stepped");
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

        // ---- 2026-11-09 (owner, match Q1 option a): the bottleneck yields to its relief ----

        private static WorldSnapshot DeckSnapshot(int human, int humanCards) => new WorldSnapshot
        {
            Self = new SelfSnapshot
            {
                Hand = System.Array.Empty<CardData>(),
                Deck = Enumerable.Range(0, humanCards)
                    .Select(_ => new CardDefinition { resourceCost = new ResourceCost(human: 1) }).ToArray(),
                Stockpile = new ResourceBundle { Human = human, Energy = 3, Materials = 2, Tech = 3 },
                PerTurnIncome = new ResourceBundle { Human = 1, Energy = 1, Materials = 1, Tech = 1 },
            },
        };

        private static DemandState State(AxisDemand demand, float decision, int human = 1) => new DemandState
        {
            Demand = demand, Remaining = 1f,
        };

        private static List<DemandCandidate> Chain(float decision, int human = 1) => new List<DemandCandidate>
        {
            new DemandCandidate(new MaterializationPlan { ResCost = new ResourceCost(human: human) },
                0f, decision, 0f, decision),
        };

        // Brynn T2: H1 + income 1 against a deck of 37 one-Human cards. Nora (Recon, 2.04) and a
        // Rad Leaper for the unthreatened garrison would take the one Human the Human extractor's
        // builder hero (Economy, 1.44) needs; an intercept keeps its priority.
        [Test]
        public void BottleneckSpend_YieldsToTheIncomeReliefOfTheSameResource()
        {
            WorldSnapshot snap = DeckSnapshot(human: 1, humanCards: 37);
            Assert.That(ActionPrice.BottleneckTypes(snap)[(int)ResourceType.Human], Is.True);
            Assert.That(ActionPrice.BottleneckTypes(snap)[(int)ResourceType.Energy], Is.False);

            DemandState recon = State(new AxisDemand { RequestingAxis = DesireAxis.Recon,
                Capability = CapabilityKind.ScoutCapability }, 2.04f);
            DemandState garrison = State(new AxisDemand { RequestingAxis = DesireAxis.Aggression,
                Capability = CapabilityKind.FieldCombatPower,
                ConsumerPurpose = CapabilityConsumerPurpose.HeldBaseGarrison }, -0.4f);
            DemandState intercept = State(new AxisDemand { RequestingAxis = DesireAxis.Aggression,
                Capability = CapabilityKind.FieldCombatPower, ActiveDefenceEnemyEta = 1 }, 1f);
            DemandState builder = State(new AxisDemand { RequestingAxis = DesireAxis.Economy,
                Capability = CapabilityKind.Hero, EconomyResourceType = ResourceType.Human,
                TargetHex = new HexCoord(4, 0) }, 1.44f);
            var options = new Dictionary<DemandState, List<DemandCandidate>>
            {
                [recon] = Chain(2.04f), [garrison] = Chain(-0.4f), [intercept] = Chain(1f), [builder] = Chain(1.44f),
            };

            PhaseAEconomyHolds.YieldBottleneckToRelief(snap, options, (d, t) => snap.Self.Stockpile.Get(t));

            Assert.That(options.Keys, Is.EquivalentTo(new[] { intercept, builder }),
                "Recon and the unthreatened garrison would leave the builder hero 0 of 1 Human");
        }

        [Test]
        public void BottleneckRule_IsSilentWhenTheReliefStaysPayableOrTheEconomyIsCovered()
        {
            DemandState Recon() => State(new AxisDemand { RequestingAxis = DesireAxis.Recon,
                Capability = CapabilityKind.ScoutCapability }, 2.04f);
            DemandState Builder() => State(new AxisDemand { RequestingAxis = DesireAxis.Economy,
                Capability = CapabilityKind.Hero, EconomyResourceType = ResourceType.Human }, 1.44f);

            // Two Human: the scout and the hero both fit.
            WorldSnapshot two = DeckSnapshot(human: 2, humanCards: 37);
            DemandState recon = Recon(), builder = Builder();
            var options = new Dictionary<DemandState, List<DemandCandidate>>
                { [recon] = Chain(2.04f), [builder] = Chain(1.44f) };
            PhaseAEconomyHolds.YieldBottleneckToRelief(two, options, (d, t) => two.Self.Stockpile.Get(t));
            Assert.That(options.Keys, Has.Member(recon));

            // A covered deck (Ysolde): Human is no bottleneck, the order of Phase A is untouched.
            WorldSnapshot covered = DeckSnapshot(human: 1, humanCards: 5);
            Assert.That(ActionPrice.BottleneckTypes(covered).Any(b => b), Is.False);
            recon = Recon(); builder = Builder();
            options = new Dictionary<DemandState, List<DemandCandidate>> { [recon] = Chain(2.04f), [builder] = Chain(1.44f) };
            PhaseAEconomyHolds.YieldBottleneckToRelief(covered, options, (d, t) => covered.Self.Stockpile.Get(t));
            Assert.That(options.Keys, Has.Member(recon));

            // A relief that is not worthwhile holds nothing back.
            WorldSnapshot one = DeckSnapshot(human: 1, humanCards: 37);
            recon = Recon(); builder = Builder();
            options = new Dictionary<DemandState, List<DemandCandidate>> { [recon] = Chain(2.04f), [builder] = Chain(-0.5f) };
            PhaseAEconomyHolds.YieldBottleneckToRelief(one, options, (d, t) => one.Self.Stockpile.Get(t));
            Assert.That(options.Keys, Has.Member(recon));

            // A spend of a resource the relief does not pay is not held back.
            recon = Recon(); builder = Builder();
            options = new Dictionary<DemandState, List<DemandCandidate>>
                { [recon] = new List<DemandCandidate> { new DemandCandidate(
                    new MaterializationPlan { ResCost = new ResourceCost(energy: 1) }, 0f, 2f, 0f, 2f) },
                  [builder] = Chain(1.44f) };
            PhaseAEconomyHolds.YieldBottleneckToRelief(one, options, (d, t) => one.Self.Stockpile.Get(t));
            Assert.That(options.Keys, Has.Member(recon));
        }
    }
}
#endif
