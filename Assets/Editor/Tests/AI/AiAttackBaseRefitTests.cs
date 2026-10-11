#if UNITY_INCLUDE_TESTS
using System;
using System.Linq;
using Game.Ai;
using Game.Ai.V2;
using Game.Cards;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Units;
using NUnit.Framework;

namespace Game.EditorTests
{
    public class AiAttackBaseRefitTests
    {
        private PlayerSetupData player;
        private static readonly HexCoord Base = new HexCoord(2, 0);

        [SetUp]
        public void Setup()
        {
            player = new PlayerSetupData();
            ArmyRegistry.Clear();
            BuildingRegistry.Clear();
            MissionIntentRegistry.Clear();
            OperationContinuationWindow.ClearAll();
            StrategicResourceReservationLedger.ClearAll();
        }

        [TearDown]
        public void Cleanup()
        {
            ArmyRegistry.Clear();
            BuildingRegistry.Clear();
            MissionIntentRegistry.Clear();
            OperationContinuationWindow.ClearAll();
            StrategicResourceReservationLedger.ClearAll();
        }

        private AttackIntent Attack() => new AttackIntent {
            AssaultStarted = true, OperationStarted = true, PrimaryArmyId = 7,
            Phase = AttackMissionPhase.Assault, RefitBaseHex = Base, RefitCaptureTurn = 6 };

        private WorldSnapshot Snapshot(int turn = 6) => new WorldSnapshot {
            Observer = player, TurnNumber = turn, Self = new SelfSnapshot {
                BaseHexes = new[] { Base }, Armies = new[] { new ArmySnapshot {
                    ArmyId = 7, Owner = player, Hex = Base, MemberCount = 2,
                    IsStructuralRaidActor = true, CurrentMovement = 0, MaxMovement = 3 } } } };

        [TestCase(5, false)] [TestCase(6, true)] [TestCase(7, true)] [TestCase(8, false)]
        public void Window_IsOnlyCaptureTurnAndFollowingTurn(int turn, bool open)
            => Assert.That(AttackBaseRefitPolicy.WindowOpen(Snapshot(turn), Attack()), Is.EqualTo(open));

        [Test]
        public void Window_ClosesAfterLeavingOrLosingBase()
        {
            var snap = Snapshot();
            snap.Self.Armies.Single().Hex = new HexCoord(3, 0);
            Assert.That(AttackBaseRefitPolicy.WindowOpen(snap, Attack()), Is.False);
            snap = Snapshot(); snap.Self.BaseHexes = Array.Empty<HexCoord>();
            Assert.That(AttackBaseRefitPolicy.WindowOpen(snap, Attack()), Is.False);
        }

        [TestCase(AttackMissionPhase.Gather, false)]
        [TestCase(AttackMissionPhase.Assault, true)]
        [TestCase(AttackMissionPhase.Reinforcement, true)]
        [TestCase(AttackMissionPhase.RecoveryReturn, false)]
        public void Window_DoesNotReplacePreparationOrRecovery(AttackMissionPhase phase, bool open)
        {
            var attack = Attack(); attack.Phase = phase;
            Assert.That(AttackBaseRefitPolicy.WindowOpen(Snapshot(), attack), Is.EqualTo(open));
        }

        [Test]
        public void Projections_ConsumeNeitherUnitNorArmyIdentity()
        {
            var first = new UnitData();
            var army = new ArmyData();
            for (int i = 0; i < 10; i++)
            {
                Assert.That(UnitData.CreateProjection().RuntimeId, Is.EqualTo(-1));
                Assert.That(ArmyData.CreateVisualSnapshot().Id, Is.EqualTo(-1));
            }
            Assert.That(new UnitData().RuntimeId, Is.EqualTo(first.RuntimeId + 1));
            Assert.That(new ArmyData().Id, Is.EqualTo(army.Id + 1));
        }

        private UnitData Body(int attack, int movement = 3, int activation = 1) => new UnitData {
            Owner = player, Name = "body", Attack = attack, Defense = 2, MoveMax = movement,
            MoveCurrent = movement, HitPointsMax = 6, HitPointsCurrent = 6, ActivationApCost = activation };

        [Test]
        public void SharedHandoff_ExchangesFullRosterAndRejectsSlowerUpgrade()
        {
            var primary = new ArmyData { Owner = player, Hex = Base };
            primary.Members.Add(Body(2)); primary.Members.Add(Body(3));
            var support = new ArmyData { Owner = player, Hex = Base };
            support.Members.Add(Body(9, 2));
            var plan = GroundCombatReinforcement.PlanAttackHandoff(primary, support, null, 0,
                false, out string why, capacityIsProgress: true);
            Assert.That(plan, Is.Not.Null, why);
            Assert.That(plan.Displaced.Count, Is.EqualTo(1));
            Assert.That(AttackBaseRefitPolicy.KeepsMovement(primary, plan), Is.False);
            support.Members.Single().MoveMax = support.Members.Single().MoveCurrent = 3;
            Assert.That(AttackBaseRefitPolicy.KeepsMovement(primary, plan), Is.True);
        }

        [Test]
        public void RosterStamp_ChangesForEqualPowerReplacementAbilitiesAndActivation()
        {
            var army = new ArmyData { Owner = player, Hex = Base };
            army.Members.Add(Body(2)); string first = AttackBaseRefitPolicy.RosterKey(army);
            army.Members[0] = Body(2);
            Assert.That(AttackBaseRefitPolicy.RosterKey(army), Is.Not.EqualTo(first));
            first = AttackBaseRefitPolicy.RosterKey(army);
            army.Members[0].Abilities.Add(UnitAbilities.AntiAir);
            Assert.That(AttackBaseRefitPolicy.RosterKey(army), Is.Not.EqualTo(first));
            first = AttackBaseRefitPolicy.RosterKey(army); army.MarkActivated();
            Assert.That(AttackBaseRefitPolicy.RosterKey(army), Is.Not.EqualTo(first));
        }

        [Test]
        public void OptionalRefit_HasNoAuthorityToSpendContinuationHold()
        {
            var demand = new AxisDemand { ConsumerMissionKind = MissionKind.Attack, AttackLocalRefit = true };
            Assert.That(demand.UsesAttackContinuationAp, Is.False);
            demand.AttackLocalRefit = false;
            Assert.That(demand.UsesAttackContinuationAp, Is.True);
        }

        [Test]
        public void Followup_ProtectsOnlyAddedActivationWhileBankProtectsAssault()
        {
            var obj = new UnityEngine.GameObject("attack-refit-followup-test");
            try
            {
                var root = obj.AddComponent<PlayerRoot>(); root.ActionPoints = 6;
                var primary = new ArmyData { Owner = player, Hex = Base };
                primary.Members.Add(Body(2, activation: 2)); ArmyRegistry.Register(primary);
                var attack = Attack(); attack.PrimaryArmyId = primary.Id;
                MissionIntentRegistry.GetOrCreate(player).Put(new MissionIntent {
                    IntentKey = MissionIntentKey.ForAttack(attack.Target), Kind = MissionKind.Attack,
                    Status = IntentStatus.Active, Funding = CommitmentTier.Hard, Objective = attack });
                var final = primary.Members.Concat(new[] { Body(5, activation: 1) }).ToList();
                var ctx = new AiTurnContext { TurnNumber = 7 };
                Assert.That(AttackBaseRefitPolicy.FollowupAp(player, primary, attack, final, ctx, root), Is.EqualTo(1));
                OperationContinuationWindow.Settle(player, 7);
                Assert.That(AttackBaseRefitPolicy.FollowupAp(player, primary, attack, final, ctx, root), Is.EqualTo(3));
                primary.MarkActivated();
                Assert.That(AttackBaseRefitPolicy.FollowupAp(player, primary, attack, final, ctx, root), Is.Zero);
            }
            finally { UnityEngine.Object.DestroyImmediate(obj); }
        }
        [Test]
        public void PartialHandoff_ReportsPhysicalCardPlayAndRequiresReplan()
        {
            var result = new MaterializationResult { CardDeployed = true, Deployed = false,
                StateChanged = true, PlacementStale = true, ApSpent = 2,
                FailReason = "handoff rejected" };
            Assert.That(result.Outcome.Played, Is.True);
            Assert.That(result.Outcome.Succeeded, Is.False);
            Assert.That(result.Outcome.NeedsReplan, Is.True);
            Assert.That(result.Outcome.ApSpent, Is.EqualTo(2));
        }

        [Test]
        public void RepeatedRefits_ReplaceOneActorsFollowupPromise()
        {
            var budget = PhaseAApBudget.Create(null);
            budget.ReserveFollowup(2);
            budget.ReserveActorFollowup(7, 3);
            budget.ReserveActorFollowup(7, 4);
            Assert.That(budget.ReservedFollowup(), Is.EqualTo(6));
            Assert.That(budget.ActorFollowup(7), Is.EqualTo(4));
            budget.ReserveActorFollowup(7, 0);
            Assert.That(budget.ReservedFollowup(), Is.EqualTo(2));
        }

        [TestCase(false)] [TestCase(true)]
        public void Housekeeping_ProtectsStartedAttackButCanStillBuildPreparation(bool started)
        {
            var snap = Snapshot();
            var primary = new ArmyData { Owner = player, Hex = Base };
            primary.Members.Add(Body(2)); ArmyRegistry.Register(primary);
            snap.Self.Armies.Single().ArmyId = primary.Id;
            var attack = Attack(); attack.PrimaryArmyId = primary.Id;
            attack.AssaultStarted = started;
            if (!started) { attack.Preparation = true; attack.Phase = AttackMissionPhase.Gather; }
            var intent = new MissionIntent { Kind = MissionKind.Attack, Status = IntentStatus.Active,
                IntentKey = MissionIntentKey.ForAttack(attack.Target), Objective = attack };
            var commitments = ActorCommitments.FromIntents(new[] { intent }, snap, null);
            var contract = commitments.MutationContractOf(primary.Id);
            Assert.That(contract, Is.Not.Null);
            Assert.That(contract.MayReceive, Is.EqualTo(!started));
            Assert.That(contract.MayReorderCommander, Is.EqualTo(!started));
        }

        [TestCase(false, 0)] [TestCase(true, 3)]
        public void SharedHandoffPrice_ChargesJoinIntoActivatedPrimaryOnly(bool activated, int ap)
        {
            var primary = new ArmyData { Owner = player, Hex = Base };
            primary.Members.Add(Body(2));
            if (activated) primary.MarkActivated();
            var support = new ArmyData { Owner = player, Hex = Base };
            support.Members.Add(Body(9, activation: 3));
            var handoff = GroundCombatReinforcement.PlanAttackHandoff(primary, support, null, 0,
                false, out string why, capacityIsProgress: true);
            Assert.That(handoff, Is.Not.Null, why);
            Assert.That(GroundCombatReinforcement.HandoffApCost(handoff, primary, support), Is.EqualTo(ap));
        }

        [Test]
        public void FundingProjection_PreservesOtherOwnersCompletionApWithoutWritingBank()
        {
            var obj = new UnityEngine.GameObject("attack-refit-bank-test");
            try
            {
                var root = obj.AddComponent<PlayerRoot>(); root.ActionPoints = 6;
                var primary = new ArmyData { Owner = player, Hex = Base };
                primary.Members.Add(Body(2, activation: 2)); ArmyRegistry.Register(primary);
                var final = primary.Members.Concat(new[] { Body(5, activation: 1) }).ToList();
                var ctx = new AiTurnContext { TurnNumber = 7 };
                var plan = new MaterializationPlan { AttackRefitPrimaryId = primary.Id };
                Assert.That(AttackBaseRefitPolicy.OnwardFunded(plan, player, root, ctx, 2, final), Is.True);
                StrategicResourceReservationLedger.Upsert(player, 7, new StrategicResourceReservation {
                    Owner = "other-build", Reason = StrategicReservationReason.EconomyBuildCompletion,
                    Resource = StrategicReservedResource.ActionPoints, Amount = 2 });
                Assert.That(AttackBaseRefitPolicy.OnwardFunded(plan, player, root, ctx, 2, final), Is.False);
                Assert.That(root.ActionPoints, Is.EqualTo(6));
                Assert.That(StrategicResourceReservationLedger.Active(player, 7,
                    StrategicReservedResource.ActionPoints), Is.EqualTo(2));
            }
            finally { UnityEngine.Object.DestroyImmediate(obj); }
        }

        [TestCase(4, false, 3)] [TestCase(6, true, 0)]
        public void FundingProjection_RequiresPrimaryAndOtherOperationTogether(int ap, bool funded, int followup)
        {
            var obj = new UnityEngine.GameObject("attack-refit-operation-prefix-test");
            try
            {
                var root = obj.AddComponent<PlayerRoot>(); root.ActionPoints = ap;
                // Lower actor id is protected first; the primary falls outside the prefix at 4 AP.
                var other = new ArmyData { Owner = player, Hex = new HexCoord(0, 0) };
                other.Members.Add(Body(2, activation: 2)); ArmyRegistry.Register(other);
                var raid = new MissionIntent { Kind = MissionKind.Raid, Status = IntentStatus.Active,
                    Funding = CommitmentTier.Hard, Objective = new RaidIntent {
                        Phase = RaidMissionPhase.Assault, OperationStarted = true, PrimaryArmyId = other.Id } };
                raid.IntentKey = MissionIntentKey.For(raid);
                MissionIntentRegistry.GetOrCreate(player).Put(raid);
                var primary = new ArmyData { Owner = player, Hex = Base };
                primary.Members.Add(Body(2, activation: 3)); ArmyRegistry.Register(primary);
                var attack = Attack(); attack.PrimaryArmyId = primary.Id;
                MissionIntentRegistry.GetOrCreate(player).Put(new MissionIntent {
                    IntentKey = MissionIntentKey.ForAttack(attack.Target), Kind = MissionKind.Attack,
                    Status = IntentStatus.Active, Funding = CommitmentTier.Hard, Objective = attack });
                var ctx = new AiTurnContext { TurnNumber = 7 };
                var final = new[] { Body(9, activation: 3) };
                var plan = new MaterializationPlan { AttackRefitPrimaryId = primary.Id };
                Assert.That(AttackBaseRefitPolicy.FollowupAp(player, primary, attack, final, ctx, root),
                    Is.EqualTo(followup));
                Assert.That(AttackBaseRefitPolicy.OnwardFunded(plan, player, root, ctx, 1, final),
                    Is.EqualTo(funded));
                Assert.That(StrategicSpendability.OperationContinuationHold(player, root, ctx),
                    Is.EqualTo(ap == 4 ? 2 : 5));
                Assert.That(root.ActionPoints, Is.EqualTo(ap));
            }
            finally { UnityEngine.Object.DestroyImmediate(obj); }
        }

        // 2026-10-07 playtest: 4 AP of EconomyBuildCompletion + 3 AP of continuation against 6 AP
        // raised CommittedHoldUncovered. The continuation is the junior claim: it protects only the
        // AP the committed ledger rows leave, so the two never exceed the stock.
        [TestCase(0, 3f)] [TestCase(3, 3f)] [TestCase(4, 0f)] [TestCase(6, 0f)]
        public void OperationContinuationHold_NeverExceedsWhatCommittedLedgerRowsLeave(int ledgerAp, float expected)
        {
            var obj = new UnityEngine.GameObject("continuation-hold-vs-ledger-test");
            try
            {
                var root = obj.AddComponent<PlayerRoot>(); root.ActionPoints = 6;
                var runner = new ArmyData { Owner = player, Hex = new HexCoord(0, 0) };
                runner.Members.Add(Body(2, activation: 3)); ArmyRegistry.Register(runner);
                var raid = new MissionIntent { Kind = MissionKind.Raid, Status = IntentStatus.Active,
                    Funding = CommitmentTier.Hard, Objective = new RaidIntent {
                        Phase = RaidMissionPhase.Assault, OperationStarted = true, PrimaryArmyId = runner.Id } };
                raid.IntentKey = MissionIntentKey.For(raid);
                MissionIntentRegistry.GetOrCreate(player).Put(raid);
                if (ledgerAp > 0)
                    StrategicResourceReservationLedger.Upsert(player, 7, new StrategicResourceReservation {
                        Owner = "build", Reason = StrategicReservationReason.EconomyBuildCompletion,
                        Resource = StrategicReservedResource.ActionPoints, Amount = ledgerAp });
                var ctx = new AiTurnContext { TurnNumber = 7 };

                float hold = StrategicSpendability.OperationContinuationHold(player, root, ctx);

                Assert.That(hold, Is.EqualTo(expected));
                Assert.That(hold + ledgerAp, Is.LessThanOrEqualTo(root.ActionPoints));
            }
            finally { UnityEngine.Object.DestroyImmediate(obj); }
        }

        [TestCase(false)] [TestCase(true)]
        public void Enumeration_UsesHeldCardOnlyAndPricesDirectOrFullRosterExchange(bool full)
        {
            var building = new BuildingData { Owner = player, Hex = Base, IsBase = true };
            building.Abilities.Add(UnitAbilities.Barracks); BuildingRegistry.Register(Base, building);
            var primary = new ArmyData { Owner = player, Hex = Base };
            primary.Members.Add(Body(2));
            if (full) primary.Members.Add(Body(3));
            ArmyRegistry.Register(primary);
            var attack = Attack(); attack.PrimaryArmyId = primary.Id;
            MissionIntentRegistry.GetOrCreate(player).Put(new MissionIntent {
                Kind = MissionKind.Attack, Status = IntentStatus.Active, Funding = CommitmentTier.Hard,
                IntentKey = MissionIntentKey.ForAttack(attack.Target), Objective = attack });
            var snap = Snapshot(); snap.Self.Armies.Single().ArmyId = primary.Id;
            snap.Self.Armies.Single().MemberCount = primary.Members.Count;
            var hand = new AiHandData(null, default, 0);
            var card = new CardData(new CardDefinition {
                cardType = CardType.Unit, displayName = "strong body", requiredBuildingAbility = UnitAbilities.Barracks,
                attack = 9, defenseRating = 2, hitPoints = 6, moveMax = 3, apCost = 1, activationApCost = 1 });
            hand.AddCard(card);
            var demand = new AxisDemand { AttackLocalRefit = true, AttackFistArmyId = primary.Id,
                TargetHex = Base, Capability = CapabilityKind.FieldCombatPower };
            int lastId = new UnitData().RuntimeId;
            var plans = AttackBaseRefitPolicy.Enumerate(snap, player, hand,
                new AiTurnContext { TurnNumber = 6 }, demand, new ActorCommitments(), null, null);
            Assert.That(plans.Count, Is.EqualTo(1));
            var plan = plans.Single();
            Assert.That(plan.Deploy.Kind, Is.EqualTo(full ? DeploymentKind.NewArmy : DeploymentKind.ExistingArmy));
            Assert.That(plan.ApCost, Is.EqualTo(full ? 1 + ArmyActions.CreateArmyApCost : 1));
            Assert.That(plan.Generation, Is.Null);
            Assert.That(plan.BaseCardInHand, Is.SameAs(card));
            Assert.That(plan.AttackRefitPrimaryId, Is.EqualTo(primary.Id));
            Assert.That(hand.Hand.Single(), Is.SameAs(card));
            Assert.That(primary.Members.Count, Is.EqualTo(full ? 2 : 1));
            Assert.That(new UnitData().RuntimeId, Is.EqualTo(lastId + 1));
            primary.Members[0].HitPointsCurrent--;
            Assert.That(AttackBaseRefitPolicy.Validate(plan, snap, player, out _, out _), Is.False,
                "a stale plan must fail before any spending or hand mutation");
            snap.TurnNumber = 8;
            Assert.That(AttackBaseRefitPolicy.Enumerate(snap, player, hand,
                new AiTurnContext { TurnNumber = 8 }, demand, new ActorCommitments(), null, null), Is.Empty);
            Assert.That(attack.Phase, Is.EqualTo(AttackMissionPhase.Assault));
        }

    }
}
#endif
