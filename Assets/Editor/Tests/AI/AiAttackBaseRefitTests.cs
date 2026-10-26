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
            var delivered = new ArmyData { Owner = player, Hex = Base };
            int refit = delivered.Id + 1000;
            budget.ReserveDeliveryFollowup(delivered, 2);
            budget.ReserveActorFollowup(refit, 3);
            budget.ReserveActorFollowup(refit, 4);
            Assert.That(budget.ReservedFollowup(), Is.EqualTo(6));
            Assert.That(budget.ActorFollowup(refit), Is.EqualTo(4));
            budget.ReserveActorFollowup(refit, 0);
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
                new AiTurnContext { TurnNumber = 6 }, demand, new ActorCommitments(), null, null,
                MissionIntentRegistry.GetOrCreate(player).All);
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
            Assert.That(AttackBaseRefitPolicy.Validate(plan, snap, player,
                MissionIntentRegistry.GetOrCreate(player).All, out _, out _), Is.False,
                "a stale plan must fail before any spending or hand mutation");
            snap.TurnNumber = 8;
            Assert.That(AttackBaseRefitPolicy.Enumerate(snap, player, hand,
                new AiTurnContext { TurnNumber = 8 }, demand, new ActorCommitments(), null, null,
                MissionIntentRegistry.GetOrCreate(player).All), Is.Empty);
            Assert.That(attack.Phase, Is.EqualTo(AttackMissionPhase.Assault));
        }

        // ---- the local refit's other windows (playtest 2026-10-22, Kryll T19-T23) --------------

        private CardData StrongBody() => new CardData(new CardDefinition {
            cardType = CardType.Unit, displayName = "Heavy Tank", requiredBuildingAbility = UnitAbilities.Barracks,
            attack = 9, defenseRating = 4, hitPoints = 8, moveMax = 3, apCost = 1, activationApCost = 1 });

        private ArmyData OwnBaseArmy(bool garrison, params UnitData[] members)
        {
            if (BuildingRegistry.FindAt(Base) == null)
            {
                var building = new BuildingData { Owner = player, Hex = Base, IsBase = true };
                building.Abilities.Add(UnitAbilities.Barracks);
                BuildingRegistry.Register(Base, building);
            }
            var army = new ArmyData { Owner = player, Hex = Base, IsGarrison = garrison };
            army.Members.AddRange(members);
            ArmyRegistry.Register(army);
            return army;
        }

        private System.Collections.Generic.List<MaterializationPlan> Enumerate(AxisDemand demand, CardData card,
            WorldSnapshot snap)
        {
            var hand = new AiHandData(null, default, 0);
            hand.AddCard(card);
            return AttackBaseRefitPolicy.Enumerate(snap, player, hand, new AiTurnContext { TurnNumber = 6 },
                demand, new ActorCommitments(), null, null, MissionIntentRegistry.GetOrCreate(player).All);
        }

        // Host #17 was full 7/7 on the Citadel and every pinned demand needed the card inside it:
        // Heavy Tank never reached it. The preparation window lands it beside the host and
        // exchanges it for the weakest body; nothing is promised for an onward activation.
        [TestCase(true)] [TestCase(false)]
        public void PreparationHost_FullRosterIsServedByExchange_FreeSlotByOrdinaryPlacement(bool full)
        {
            var host = full ? OwnBaseArmy(false, Body(2), Body(3)) : OwnBaseArmy(false, Body(2));
            var attack = new AttackIntent { PrimaryArmyId = host.Id, Preparation = true,
                Phase = AttackMissionPhase.Gather };
            MissionIntentRegistry.GetOrCreate(player).Put(new MissionIntent {
                Kind = MissionKind.Attack, Status = IntentStatus.Active, Funding = CommitmentTier.Hard,
                IntentKey = MissionIntentKey.ForAttack(attack.Target), Objective = attack });
            var snap = Snapshot(); snap.Self.Armies.Single().ArmyId = host.Id;
            var demand = new AxisDemand { AttackFistArmyId = host.Id, AttackFistIsPreparationHost = true,
                TargetHex = Base, Capability = CapabilityKind.FieldCombatPower };

            var plans = Enumerate(demand, StrongBody(), snap);

            if (!full)
            {
                Assert.That(plans, Is.Empty, "a card that fits is the ordinary direct placement's");
                return;
            }
            var plan = plans.Single();
            Assert.That(plan.AttackRefitWindow, Is.EqualTo(AttackBaseRefitPolicy.RefitWindow.AttackPreparation));
            Assert.That(plan.Deploy.Kind, Is.EqualTo(DeploymentKind.NewArmy));
            Assert.That(plan.AttackRefitFollowupAp, Is.Zero);
            Assert.That(AttackBaseRefitPolicy.ServesDemand(plan, demand, snap, player,
                MissionIntentRegistry.GetOrCreate(player).All), Is.True);
            Assert.That(AttackBaseRefitPolicy.ExchangeRaisesRecipient(host, plan.BaseCardInHand.Definition,
                AiPower.EffectiveLine(plan.BaseCardInHand.Definition, null, null), player), Is.True,
                "the demand's wait witness reads the same exchange");
            Assert.That(host.Members.Count, Is.EqualTo(2), "planning moves nobody");
        }

        [TestCase(false)] [TestCase(true)]
        public void DirectPreparationCardServesOnlyItsOwnConsumer(bool differentConsumer)
        {
            var host = OwnBaseArmy(false, Body(2));
            var attack = new AttackIntent { PrimaryArmyId = host.Id, Preparation = true,
                Phase = AttackMissionPhase.Gather,
                Target = AttackTargetRef.For(new HexCoord(5, 0), new PlayerSetupData(), AttackTargetKind.Base) };
            var intent = new MissionIntent { Kind = MissionKind.Attack, Status = IntentStatus.Active,
                PreferredMoverArmyId = host.Id, IntentKey = MissionIntentKey.ForAttack(attack.Target),
                Objective = attack };
            MissionIntentRegistry.GetOrCreate(player).Put(intent);
            var snap = Snapshot(); snap.Self.Armies.Single().ArmyId = host.Id;
            snap.Intents = MissionIntentRegistry.GetOrCreate(player);
            var demand = new AxisDemand { RequestingAxis = DesireAxis.Aggression,
                Capability = CapabilityKind.FieldCombatPower, ConsumerMissionKind = MissionKind.Attack,
                ConsumerIntentKey = differentConsumer ? MissionIntentKey.ForAttack(
                    AttackTargetRef.For(new HexCoord(6, 0), attack.Target.ExpectedOwner, AttackTargetKind.Base)) : intent.IntentKey,
                AttackFistArmyId = host.Id, AttackFistIsPreparationHost = true, TargetHex = Base };
            var plan = new MaterializationPlan { BaseCardInHand = StrongBody(),
                Deploy = new PlacementOption(Base, DeploymentKind.ExistingArmy, host) };
            var result = MaterializationDeliveryPolicy.AssessDemandOperationally(plan, demand, snap,
                player, new AiTurnContext());
            Assert.That(result.CanDeliver, Is.EqualTo(!differentConsumer), result.ToString());
            var pass = new PhaseAPass(snap, player, null, null, new AiTurnContext(), null,
                null, null, MissionIntentRegistry.GetOrCreate(player), null, null, null, null, false,
                new StrategicPhaseResult { Reservation = new MaterializationReservation() }) {
                ColdStates = new System.Collections.Generic.List<DemandState> {
                    new DemandState { Demand = demand, Remaining = 1f } } };
            pass.PublishEarlyResiduals();
            AxisDemand residual = pass.Reservation.UnresolvedDemands.Single();
            Assert.That(residual.ConsumerMissionKind, Is.EqualTo(MissionKind.Attack));
            Assert.That(MaterializationDeliveryPolicy.AssessDemandOperationally(plan, residual, snap,
                player, new AiTurnContext()).CanDeliver, Is.EqualTo(!differentConsumer),
                "Phase B must retain the same consumer identity and delivery contract");
            Assert.That(host.Members.Count, Is.EqualTo(1), "the delivery proof is read-only");
        }

        [TestCase(1, false)] [TestCase(2, true)]
        public void PreparationCardRouteProofMatchesRefitAndWaitWitness(int incomingMovement, bool allowed)
        {
            var mapObject = new UnityEngine.GameObject("preparation card route");
            try
            {
                var map = mapObject.AddComponent<HexMap>();
                var goal = new HexCoord(3, 0);
                map.SetData(5, 1f, new System.Collections.Generic.Dictionary<HexCoord, Game.Terrain.TerrainTypeEntry> {
                    [Base] = new Game.Terrain.TerrainTypeEntry { moveCost = 1 },
                    [goal] = new Game.Terrain.TerrainTypeEntry { moveCost = 2 },
                });
                var host = OwnBaseArmy(false, Body(2), Body(3));
                var attack = new AttackIntent { PrimaryArmyId = host.Id, Preparation = true,
                    Phase = AttackMissionPhase.Gather,
                    Target = AttackTargetRef.For(goal, new PlayerSetupData(), AttackTargetKind.Base) };
                var intent = new MissionIntent { Kind = MissionKind.Attack, Status = IntentStatus.Active,
                    PreferredMoverArmyId = host.Id, IntentKey = MissionIntentKey.ForAttack(attack.Target), Objective = attack };
                MissionIntentRegistry.GetOrCreate(player).Put(intent);
                var snap = Snapshot(); snap.Map = map; snap.Intents = MissionIntentRegistry.GetOrCreate(player);
                snap.Self.Armies.Single().ArmyId = host.Id;
                var card = StrongBody(); card.Definition.moveMax = incomingMovement;
                var demand = new AxisDemand { AttackFistArmyId = host.Id, AttackFistIsPreparationHost = true,
                    TargetHex = Base, AttackObjectiveHex = goal, Capability = CapabilityKind.FieldCombatPower };
                var plans = Enumerate(demand, card, snap);
                Assert.That(plans.Count, Is.EqualTo(allowed ? 1 : 0));
                if (allowed)
                {
                    Assert.That(AttackBaseRefitPolicy.Validate(plans.Single(), snap, player,
                        MissionIntentRegistry.GetOrCreate(player).All, out _, out _), Is.True);
                    card.Definition.moveMax = 1;
                    Assert.That(AttackBaseRefitPolicy.Validate(plans.Single(), snap, player,
                        MissionIntentRegistry.GetOrCreate(player).All, out _, out _), Is.False,
                        "a pinned refit is rechecked before it spends or mutates");
                    card.Definition.moveMax = incomingMovement;
                }
                snap.Self.Hand = new[] { card };
                Assert.That(AggressionDemandEvaluator.PreparationHostCardSource(snap, host,
                    taskTarget: goal) != null, Is.EqualTo(allowed), "the WAIT must name an executable delivery");
                host.Members.RemoveAt(1);
                var direct = new MaterializationPlan { BaseCardInHand = card,
                    Deploy = new PlacementOption(Base, DeploymentKind.ExistingArmy, host) };
                demand.ConsumerMissionKind = MissionKind.Attack; demand.ConsumerIntentKey = intent.IntentKey;
                Assert.That(MaterializationDeliveryPolicy.AssessDemandOperationally(direct, demand, snap,
                    player, new AiTurnContext { Map = map }).CanDeliver, Is.EqualTo(allowed));
                Assert.That(MaterializationDeliveryPolicy.CardPreservesAttackTaskRoute(snap, host,
                    card.Definition, AiPower.ProjectMaterialization(direct)), Is.EqualTo(allowed),
                    "execution resolves the same active target without a demand object");
                Assert.That(host.Members.Count, Is.EqualTo(1));
            }
            finally { UnityEngine.Object.DestroyImmediate(mapObject); }
        }

        // The unbound demand's free fist: the same exchange; a fist an operation owns is not free.
        [TestCase(false, 1)] [TestCase(true, 0)]
        public void FreeFist_FullRosterIsServedByExchangeOnlyWhileUnclaimed(bool claimed, int expected)
        {
            var fist = OwnBaseArmy(false, Body(2), Body(3));
            if (claimed)
                MissionIntentRegistry.GetOrCreate(player).Put(new MissionIntent {
                    Kind = MissionKind.Attack, Status = IntentStatus.Active, Funding = CommitmentTier.Hard,
                    PreferredMoverArmyId = fist.Id,
                    IntentKey = MissionIntentKey.ForAttack(new AttackIntent().Target),
                    Objective = new AttackIntent { PrimaryArmyId = fist.Id, Phase = AttackMissionPhase.Assault } });
            var snap = Snapshot(); snap.Self.Armies.Single().ArmyId = fist.Id;
            var demand = new AxisDemand { AttackFistArmyId = fist.Id, TargetHex = Base,
                Capability = CapabilityKind.FieldCombatPower };

            Assert.That(Enumerate(demand, StrongBody(), snap).Count, Is.EqualTo(expected));
        }

        // A full garrison below its floor: the card lands beside it and the weakest garrison body
        // moves out to the new container; only a power gain counts.
        [Test]
        public void GarrisonFloor_FullGarrisonIsServedByExchange()
        {
            var garrison = OwnBaseArmy(true, Body(1), Body(2), Body(2), Body(3));
            var snap = Snapshot();
            var demand = new AxisDemand { DeliveryShape = CapabilityDeliveryShape.Garrison, TargetHex = Base,
                Capability = CapabilityKind.FieldCombatPower };

            var plan = Enumerate(demand, StrongBody(), snap).Single();

            Assert.That(plan.AttackRefitWindow, Is.EqualTo(AttackBaseRefitPolicy.RefitWindow.GarrisonFloor));
            Assert.That(plan.AttackRefitPrimaryId, Is.EqualTo(garrison.Id));
            Assert.That(AttackBaseRefitPolicy.Validate(plan, snap, player,
                MissionIntentRegistry.GetOrCreate(player).All, out HandoffPlan handoff, out _), Is.True);
            Assert.That(handoff.Displaced.Single().Attack, Is.EqualTo(1), "the weakest body gives way");
            Assert.That(Enumerate(demand, new CardData(new CardDefinition {
                cardType = CardType.Unit, displayName = "weak", requiredBuildingAbility = UnitAbilities.Barracks,
                attack = 0, defenseRating = 0, hitPoints = 1, moveMax = 3, apCost = 1 }), snap), Is.Empty,
                "a body that adds no power is never an exchange");
        }

    }
}
#endif
