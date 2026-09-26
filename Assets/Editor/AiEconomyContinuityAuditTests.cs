#if UNITY_INCLUDE_TESTS
using System.Collections.Generic;
using System.Linq;
using Game.Ai.V2;
using Game.Cards;
using Game.Economy;
using Game.HexGrid;
using Game.Players;
using NUnit.Framework;

namespace Game.EditorTests
{
    // Regression cover for the Economy-branch audit (lifecycle of durable Economy intents).
    // Pure ledger / Continuity / registry level: no map, no PlayerRoot, no live ArmyData.
    public class AiEconomyContinuityAuditTests
    {
        private const int Actor = 21;
        private static readonly HexCoord Site = new HexCoord(6, -2);
        private static readonly HexCoord Home = new HexCoord(0, 0);

        [TearDown]
        public void TearDown()
        {
            MissionIntentRegistry.Clear();
            AiAllocatorStateRegistry.Clear();
            StrategicResourceReservationLedger.ClearAll();
        }

        private static MissionProposal Proposal(EconomyTaskKind kind, HexCoord target,
            CardData card = null)
        {
            bool mobile = kind == EconomyTaskKind.MobileCollection
                || kind == EconomyTaskKind.ReturnCollector;
            var m = new MissionProposal
            {
                Kind = MissionKind.Economy,
                Target = new EconomyMissionTarget
                {
                    Kind = kind, TargetHex = target,
                    ResourceType = kind == EconomyTaskKind.FoundBase
                        || kind == EconomyTaskKind.ReturnBuilder
                        || kind == EconomyTaskKind.ReturnCollector
                        ? (ResourceType?)null : ResourceType.Materials,
                    BuilderArmyId = Actor,
                    CollectorArmyId = mobile ? Actor : (int?)null,
                    BuildCard = card,
                    BuildResourceCost = new ResourceCost(materials: 3),
                    ExpectedMarginalYield = 2,
                },
                PreferredMoverArmyId = Actor,
                FromDurableIntent = true,
                BaseValue = 5f,
                Requirements = new MissionRequirements(),
            };
            m.Axes.Value[DesireAxis.Economy] = 1f;
            return m;
        }

        private static MissionIntent DurableIntent(PlayerSetupData player, MissionProposal m,
            int turn)
        {
            var t = (EconomyMissionTarget)m.Target;
            var intent = new MissionIntent
            {
                Kind = MissionKind.Economy,
                Funding = CommitmentTier.Soft,
                Status = IntentStatus.Active,
                PreferredMoverArmyId = Actor,
                CreatedTurn = turn,
                TurnsActive = 1,
                LastReconciledTurn = turn,
                LastProgressTurn = turn,
                Objective = new EconomyIntent
                {
                    Kind = t.Kind, TargetHex = t.TargetHex, ResourceType = t.ResourceType,
                    BuilderArmyId = Actor, CollectorArmyId = t.CollectorArmyId,
                    BuildCard = t.BuildCard, BuildResourceCost = t.BuildResourceCost,
                    ExpectedMarginalYield = t.ExpectedMarginalYield,
                },
                IntentKey = MissionIntentKey.For(m),
                LastAttemptKey = StableMissionKey.For(m),
            };
            MissionIntentRegistry.GetOrCreate(player).Put(intent);
            return intent;
        }

        private static MissionTurnOutcome Settle(PlayerSetupData player, MissionProposal m, int turn,
            ProvisionFailure? failure = null, ExecutionResult execution = null)
        {
            var ledger = new MissionOutcomeLedger();
            ledger.RegisterProposals(new[] { m });
            if (failure.HasValue)
                ledger.RecordProvisionFailure(m, failure.Value);
            else
            {
                var pm = new ProvisionedMission
                {
                    Mission = m, Key = StableMissionKey.For(m), Kind = MissionKind.Economy,
                    MoverArmyId = Actor, EconomyTarget = (EconomyMissionTarget)m.Target,
                };
                ledger.RecordProvisionSuccess(m, pm);
                if (execution != null)
                {
                    execution.Key = pm.Key;
                    execution.Source = pm;
                    ledger.RecordExecution(execution);
                }
            }
            List<MissionTurnOutcome> outcomes = ledger.Finalize();
            MissionContinuityLayer.ReconcileAfterTurn(player, turn, outcomes);
            return outcomes.Single();
        }

        private static bool Has(PlayerSetupData player, MissionIntent intent) =>
            MissionIntentRegistry.GetOrCreate(player).TryGet(intent.IntentKey, out _);

        // --- B1: productive hold on target is progress, not a failure ------------------------

        [Test]
        public void B1_BuildDeliveryReadyOnTarget_KeepsTheDurableBuild()
        {
            var player = new PlayerSetupData();
            MissionProposal m = Proposal(EconomyTaskKind.BuildExtraction, Site);
            MissionIntent intent = DurableIntent(player, m, 3);
            intent.StallTurns = 1;

            MissionTurnOutcome o = Settle(player, m, 4, execution: new ExecutionResult
            {
                StopReason = ExecutionStopReason.StepCompleted, EconomyDeliveryReady = true,
                FinalHex = Site,
            });

            Assert.That(o.MadeProgress, Is.True);
            Assert.That(Has(player, intent), Is.True,
                "a builder standing on its site for Phase A's build must keep its commitment");
            Assert.That(intent.StallTurns, Is.EqualTo(0));
            Assert.That(intent.LastProgressTurn, Is.EqualTo(4));
        }

        [Test]
        public void B1_MobileCollectorHoldingItsSite_KeepsItsIntent()
        {
            var player = new PlayerSetupData();
            MissionProposal m = Proposal(EconomyTaskKind.MobileCollection, Site);
            MissionIntent intent = DurableIntent(player, m, 3);

            Settle(player, m, 4, execution: new ExecutionResult
            {
                StopReason = ExecutionStopReason.StepCompleted, EconomyHolding = true,
                FinalHex = Site,
            });

            Assert.That(Has(player, intent), Is.True,
                "a collector holding its site keeps its lifecycle (useful/safe -> ReturnCollector)");
        }

        [Test]
        public void B1_CollectorAlreadyOnItsSite_IsNotProposedAsAStep()
        {
            var player = new PlayerSetupData();
            MissionProposal m = Proposal(EconomyTaskKind.MobileCollection, Site);
            MissionIntent intent = DurableIntent(player, m, 3);
            var snap = new WorldSnapshot
            {
                TurnNumber = 4,
                Self = new SelfSnapshot
                {
                    Armies = new List<ArmySnapshot>
                    {
                        new ArmySnapshot { ArmyId = Actor, Hex = Site, MemberCount = 1, CurrentMovement = 2 },
                    },
                },
            };

            List<MissionProposal> proposals = EconomyMissionPlanner.Propose(snap, null,
                new[] { intent }, null);

            Assert.That(proposals, Is.Empty,
                "a zero-AP hold would be the first funded task of every admission and stop the loop");
        }

        // --- B2: transient no-progress outcomes age the intent, route failure retires it -----

        [Test]
        public void B2_EnvelopeTooSmall_AgesTheDurableBuildInsteadOfRetiringIt()
        {
            var player = new PlayerSetupData();
            MissionProposal m = Proposal(EconomyTaskKind.BuildExtraction, Site);
            MissionIntent intent = DurableIntent(player, m, 3);

            Settle(player, m, 4, failure: ProvisionFailure.EnvelopeTooSmall(4f, "ap"));
            Assert.That(Has(player, intent), Is.True, "one short AP pass is not an abandoned build");
            Assert.That(intent.StallTurns, Is.EqualTo(1));

            Settle(player, m, 5, failure: ProvisionFailure.EnvelopeTooSmall(4f, "ap"));
            Assert.That(Has(player, intent), Is.False, "the stall bound still ends a stuck build");
        }

        [Test]
        public void B2_StaleExecutionTargetInvalidated_IsBlockedAndKeepsTheBuild()
        {
            var player = new PlayerSetupData();
            MissionProposal m = Proposal(EconomyTaskKind.FoundBase, Site,
                new CardData(new CardDefinition { cardType = CardType.Base }));
            MissionIntent intent = DurableIntent(player, m, 3);

            MissionTurnOutcome o = Settle(player, m, 4, execution: new ExecutionResult
            {
                StopReason = ExecutionStopReason.TargetInvalidated,
            });

            Assert.That(o.Outcome, Is.EqualTo(ExecutionOutcome.Blocked));
            Assert.That(Has(player, intent), Is.True);
        }

        [Test]
        public void ProvenRouteFailure_StillRetiresTheOutboundBuild()
        {
            var player = new PlayerSetupData();
            MissionProposal m = Proposal(EconomyTaskKind.BuildExtraction, Site);
            MissionIntent intent = DurableIntent(player, m, 3);

            Settle(player, m, 4, failure: ProvisionFailure.NoExecutableStep("no safe route"));

            Assert.That(Has(player, intent), Is.False);
        }

        // --- B3: a suspended ReturnCollector is re-tested, never parked forever --------------

        [Test]
        public void B3_SuspendedReturnCollector_IsResumedByResolveActive()
        {
            var player = new PlayerSetupData();
            MissionProposal m = Proposal(EconomyTaskKind.ReturnCollector, Home);
            MissionIntent intent = DurableIntent(player, m, 3);
            intent.Status = IntentStatus.Suspended;
            intent.Suspended = SuspendReason.CapabilityUnavailable;
            var snap = new WorldSnapshot
            {
                TurnNumber = 4,
                Self = new SelfSnapshot
                {
                    BaseHexes = new List<HexCoord> { Home },
                    Armies = new List<ArmySnapshot>
                    {
                        new ArmySnapshot { ArmyId = Actor, Hex = Site, MemberCount = 1 },
                    },
                },
            };

            List<MissionIntent> active = MissionContinuityLayer.ResolveActive(player, snap);

            Assert.That(active, Does.Contain(intent));
            Assert.That(intent.Status, Is.EqualTo(IntentStatus.Active));
        }

        [Test]
        public void B3_ReturnCollectorContendedEveryTurn_IsEventuallyReaped()
        {
            var player = new PlayerSetupData();
            MissionProposal m = Proposal(EconomyTaskKind.ReturnCollector, Home);
            MissionIntent intent = DurableIntent(player, m, 3);

            for (int turn = 4; turn <= 6 && Has(player, intent); turn++)
            {
                intent.Status = IntentStatus.Active;   // what ResolveActive now does each pass
                Settle(player, m, turn, failure: ProvisionFailure.MoverContended("claimed"));
            }

            Assert.That(Has(player, intent), Is.False,
                "a collector that can never move home must not hold its army forever");
        }

        // --- B4: two stuck Base projects each reach their own suppression -------------------

        [Test]
        public void B4_TwoStuckBaseProjects_DoNotResetEachOthersStreak()
        {
            var state = new MissionIntentState();
            var cardA = new CardData(new CardDefinition { cardType = CardType.Base });
            var cardB = new CardData(new CardDefinition { cardType = CardType.Base });
            var hexA = new HexCoord(3, 0);
            var hexB = new HexCoord(-3, 2);

            bool a = false, b = false;
            for (int turn = 1; turn <= 3; turn++)
            {
                a |= state.RecordBaseExpansionDeliveryFailure(turn, cardA, hexA);
                b |= state.RecordBaseExpansionDeliveryFailure(turn, cardB, hexB);
            }

            Assert.That(a && b, Is.True);
            Assert.That(state.IsBaseExpansionDeliverySuppressed(3, cardA, hexA), Is.True);
            Assert.That(state.IsBaseExpansionDeliverySuppressed(3, cardB, hexB), Is.True);
        }

        // --- B5: collectors do not draw on the hero-builder pool ----------------------------

        [Test]
        public void B5_CollectorMissions_AreOutsideTheHeroBuilderPool()
        {
            Assert.That(CapabilityPoolExhaustionRegistry.PoolFor(
                Proposal(EconomyTaskKind.MobileCollection, Site)), Is.EqualTo(CapabilityPoolKind.None));
            Assert.That(CapabilityPoolExhaustionRegistry.PoolFor(
                Proposal(EconomyTaskKind.ReturnCollector, Home)), Is.EqualTo(CapabilityPoolKind.None));
            Assert.That(CapabilityPoolExhaustionRegistry.PoolFor(
                Proposal(EconomyTaskKind.BuildExtraction, Site)),
                Is.EqualTo(CapabilityPoolKind.EconomyHeroBuilder));
        }

        // --- B8: retiring an Economy intent releases its holds and repays its loan ----------

        [Test]
        public void B8_FailedDurableBuild_ReleasesItsDeferredResourceHold()
        {
            var player = new PlayerSetupData();
            MissionProposal m = Proposal(EconomyTaskKind.BuildExtraction, Site);
            MissionIntent intent = DurableIntent(player, m, 4);
            InfrastructureFulfillment.ReserveDeferredEconomyResourcesForActiveIntent(player, 4, intent);
            Assert.That(StrategicResourceReservationLedger.Active(player, 4,
                StrategicReservedResource.Materials), Is.EqualTo(3f));

            Settle(player, m, 4, failure: ProvisionFailure.TargetInvalidated("builder gone"));

            Assert.That(Has(player, intent), Is.False);
            Assert.That(StrategicResourceReservationLedger.Active(player, 4,
                StrategicReservedResource.Materials), Is.Zero,
                "an abandoned build must not keep Phase B from spending its resources");
        }

        [Test]
        public void B8_SuppressedFoundBase_RepaysItsBorrowedDonor()
        {
            var player = new PlayerSetupData();
            MissionProposal m = Proposal(EconomyTaskKind.FoundBase, Site,
                new CardData(new CardDefinition { cardType = CardType.Base }));
            MissionIntent intent = DurableIntent(player, m, 3);
            var donor = new MissionIntent
            {
                Kind = MissionKind.Scout, PreferredMoverArmyId = Actor,
                Status = IntentStatus.Suspended, Suspended = SuspendReason.EconomyLoan,
                Objective = new ScoutIntent { Kind = ScoutTargetKind.Explore, FocusHex = new HexCoord(9, 9) },
            };
            donor.IntentKey = MissionIntentKey.For(donor);
            MissionIntentRegistry.GetOrCreate(player).Put(donor);
            intent.Economy.Loaned = true;
            intent.Economy.LoanSource = donor.IntentKey;

            for (int turn = 4; turn <= 6 && Has(player, intent); turn++)
            {
                intent.Status = IntentStatus.Active;
                Settle(player, m, turn, failure: ProvisionFailure.MoverContended("cannot advance"));
            }

            Assert.That(Has(player, intent), Is.False);
            Assert.That(donor.Status, Is.EqualTo(IntentStatus.Active),
                "a retired borrower returns its actor at once, not after an orphan repair");
        }

        // --- B7: the allocator funds an Economy completion from the pool Provisioning checks --

        private static TentativeAllocation PackEconomyCompletion(PlayerSetupData player,
            StrategicReservationReason otherOwnersReason)
        {
            MissionProposal m = Proposal(EconomyTaskKind.BuildExtraction, Site);
            m.FromDurableIntent = false;
            m.Requirements = new MissionRequirements
            {
                ApMinimum = 1f, ApDesired = 1f, ApMaximum = 1f,
                MaterialsMinimum = 3f, MaterialsDesired = 3f, MaterialsMaximum = 3f,
            };
            InfrastructureFulfillment.ReserveEconomyCost(player, 1, "Economy:another-build",
                new ResourceCost(materials: 2), 0f, otherOwnersReason);
            var snap = new WorldSnapshot
            {
                TurnNumber = 1,
                Self = new SelfSnapshot
                {
                    ActionPoints = 5,
                    Stockpile = new ResourceBundle { Materials = 4f },
                },
            };
            return ResourceAllocator.BeginTurn(snap, Radar.Even(),
                new List<MissionProposal> { m }, new List<Commitment>(), player).Pack();
        }

        [Test]
        public void B7_AnotherOwnersCompletionHold_IsNotAvailableToAnEconomyBuild()
        {
            TentativeAllocation a = PackEconomyCompletion(new PlayerSetupData(),
                StrategicReservationReason.EconomyBuildCompletion);

            Assert.That(a.Funded, Is.Empty,
                "4 Materials minus another build's completion hold of 2 cannot fund 3");
            Assert.That(a.Deferred.Single().Reason, Is.EqualTo(DeferReason.InsufficientPhysical));
        }

        [Test]
        public void B7_AnotherOwnersDeferredHold_DoesNotBlockACompletingBuild()
        {
            TentativeAllocation a = PackEconomyCompletion(new PlayerSetupData(),
                StrategicReservationReason.EconomyDeferredBuild);

            Assert.That(a.Funded.Count, Is.EqualTo(1),
                "a build completing now outranks other builds' deferred holds, as in Provisioning");
        }

        [Test]
        public void B7_TwoFundedCompletions_DoNotDoubleSubtractFirstOwnersHold()
        {
            var player = new PlayerSetupData();
            const int turn = 9;
            StrategicResourceReservationLedger.BeginTurn(player, turn);
            MissionProposal Build(int armyId, HexCoord hex, float value)
            {
                var m = new MissionProposal
                {
                    Kind = MissionKind.Economy,
                    Target = new EconomyMissionTarget
                    {
                        Kind = EconomyTaskKind.BuildExtraction,
                        TargetHex = hex,
                        ResourceType = ResourceType.Materials,
                        BuilderArmyId = armyId,
                    },
                    PreferredMoverArmyId = armyId,
                    BaseValue = value,
                    EffectiveValue = value,
                    Requirements = new MissionRequirements
                    {
                        ApMinimum = 1f, ApDesired = 1f, ApMaximum = 1f,
                        MaterialsMinimum = 4f, MaterialsDesired = 4f, MaterialsMaximum = 4f,
                    },
                };
                m.Axes.Value[DesireAxis.Economy] = 1f;
                InfrastructureFulfillment.ReserveEconomyCost(player, turn,
                    EconomyMissionPlanner.OwnerKey(StableMissionKey.For(m)),
                    new ResourceCost(materials: 4), 1f);
                return m;
            }

            MissionProposal first = Build(21, new HexCoord(6, -2), 20f);
            MissionProposal second = Build(22, new HexCoord(7, -2), 10f);
            var snap = new WorldSnapshot
            {
                TurnNumber = turn,
                Self = new SelfSnapshot
                {
                    ActionPoints = 5,
                    Stockpile = new ResourceBundle { Materials = 8f },
                },
            };

            TentativeAllocation allocation = ResourceAllocator.BeginTurn(snap, Radar.Even(),
                new List<MissionProposal> { first, second }, new List<Commitment>(), player).Pack();

            Assert.That(allocation.Funded.Select(f => f.Mission),
                Is.EquivalentTo(new[] { first, second }),
                "each owner has four Materials; funding one already accounts for its own hold");
            Assert.That(allocation.PhysicalFunded.Materials, Is.EqualTo(8f));
        }

        // --- B10 / S1: Economy age is time without progress ---------------------------------

        [Test]
        public void B10_BuildAdvancingEveryTurn_IsNotReapedByAbsoluteAge()
        {
            var player = new PlayerSetupData();
            MissionProposal m = Proposal(EconomyTaskKind.BuildExtraction, Site);
            MissionIntent intent = DurableIntent(player, m, 1);
            intent.TurnsActive = 6;
            intent.LastReconciledTurn = 6;
            intent.LastProgressTurn = 6;

            Settle(player, m, 7, execution: new ExecutionResult
            {
                StopReason = ExecutionStopReason.StepCompleted, StepsMoved = 1,
            });

            Assert.That(Has(player, intent), Is.True, "a long walk that keeps moving is not stale");
        }

        [Test]
        public void B10_BuildWaitingWithoutProgress_IsStillBoundedByMaxTurns()
        {
            var player = new PlayerSetupData();
            MissionProposal m = Proposal(EconomyTaskKind.BuildExtraction, Site);
            MissionIntent intent = DurableIntent(player, m, 1);
            intent.LastReconciledTurn = 6;
            intent.LastProtectedTurn = 7;   // Phase A still holds its resources this turn

            MissionContinuityLayer.ReconcileAfterTurn(player, 7, new List<MissionTurnOutcome>());

            Assert.That(Has(player, intent), Is.False,
                "a build that has waited commitmentMaxTurns without any progress is released");
        }

        [Test]
        public void S1_ReturnBuilderThatNeverGetsHome_IsReleasedAndItsLenderResumed()
        {
            var player = new PlayerSetupData();
            MissionProposal m = Proposal(EconomyTaskKind.ReturnBuilder, Home);
            MissionIntent intent = DurableIntent(player, m, 1);
            var donor = new MissionIntent
            {
                Kind = MissionKind.Scout, PreferredMoverArmyId = Actor,
                Status = IntentStatus.Suspended, Suspended = SuspendReason.EconomyLoan,
                Objective = new ScoutIntent { Kind = ScoutTargetKind.Explore, FocusHex = new HexCoord(9, 9) },
            };
            donor.IntentKey = MissionIntentKey.For(donor);
            MissionIntentRegistry.GetOrCreate(player).Put(donor);
            intent.Economy.Loaned = true;
            intent.Economy.LoanSource = donor.IntentKey;

            Settle(player, m, 3, failure: ProvisionFailure.NoExecutableStep("route home blocked"));
            Assert.That(Has(player, intent), Is.True, "a briefly blocked way home is kept");

            Settle(player, m, 7, failure: ProvisionFailure.NoExecutableStep("route home blocked"));
            Assert.That(Has(player, intent), Is.False);
            Assert.That(donor.Status, Is.EqualTo(IntentStatus.Active));
        }

        // --- B11 / B13: suspended builds keep their lease; durable routes come from Analysis --

        [Test]
        public void B11_TransientlySuspendedBuild_KeepsItsResourceHold()
        {
            var player = new PlayerSetupData();
            MissionProposal m = Proposal(EconomyTaskKind.BuildExtraction, Site);
            MissionIntent intent = DurableIntent(player, m, 4);
            intent.Status = IntentStatus.Suspended;
            intent.Suspended = SuspendReason.CapabilityUnavailable;

            InfrastructureFulfillment.ReserveDeferredEconomyResourcesForActiveIntent(player, 4, intent);

            Assert.That(StrategicResourceReservationLedger.Active(player, 4,
                StrategicReservedResource.Materials), Is.EqualTo(3f),
                "a build that still leases its site is still committed to its resources");
        }

        [Test]
        public void B13_DurableBuildWithoutRefreshedDemand_ProvisionsOverAnalysisRoutes()
        {
            var player = new PlayerSetupData();
            MissionProposal m = Proposal(EconomyTaskKind.BuildExtraction, Site);
            MissionIntent intent = DurableIntent(player, m, 3);
            var routes = new[]
            {
                new EconomyBuilderRouteSnapshot { ArmyId = Actor, TravelCost = 3, MaxMovement = 2 },
            };
            var snap = new WorldSnapshot
            {
                TurnNumber = 4,
                Self = new SelfSnapshot
                {
                    Armies = new List<ArmySnapshot>
                    {
                        new ArmySnapshot { ArmyId = Actor, Hex = Home, MemberCount = 1,
                            CurrentMovement = 2, MaxMovement = 2, HasHero = true },
                    },
                },
                Economy = new EconomyStanding
                {
                    ExtractionOpportunities = new[]
                    {
                        new EconomyExtractionOpportunity
                        {
                            Hex = Site, ResourceType = ResourceType.Materials, BuilderRoutes = routes,
                        },
                    },
                },
            };

            MissionProposal proposed = EconomyMissionPlanner.Propose(snap, null, new[] { intent }, null)
                .Single();

            Assert.That(((EconomyMissionTarget)proposed.Target).BuilderRoutes, Is.SameAs(routes));
        }

        // --- refactor pins: one Economy build kind / owner key -----------------------------

        [Test]
        public void BuildKind_BaseHeroPrerequisite_SharesTheBaseBuildsOwnerKey()
        {
            var card = new CardData(new CardDefinition { cardType = CardType.Base });
            var baseDemand = new AxisDemand
            {
                RequestingAxis = DesireAxis.Economy, Capability = CapabilityKind.EconomicExpansionBase,
                TargetHex = Site, EconomyBuildCard = card,
                EconomyBuildResourceCost = new ResourceCost(materials: 3),
            };
            AxisDemand hero = DemandLayer.EconomyHeroPrerequisite(baseDemand);

            Assert.That(DemandLayer.EconomyBuildKind(hero), Is.EqualTo(EconomyTaskKind.FoundBase));
            Assert.That(InfrastructureFulfillment.EconomyHeroPrerequisiteOwner(hero),
                Is.EqualTo(InfrastructureFulfillment.EconomyReservationOwner(baseDemand)));
            Assert.That(InfrastructureFulfillment.EconomyReservationOwner(baseDemand),
                Is.EqualTo(EconomyMissionPlanner.OwnerKey(StableMissionKey.For(
                    Proposal(EconomyTaskKind.FoundBase, Site, card)))));
        }

        [Test]
        public void BuilderCandidateGate_ReportsEachStructuralRejection()
        {
            var snap = new WorldSnapshot
            {
                TurnNumber = 1,
                Self = new SelfSnapshot
                {
                    Armies = new List<ArmySnapshot>
                    {
                        new ArmySnapshot { ArmyId = 1, Hex = Home, IsGarrison = true, HasHero = true },
                        new ArmySnapshot { ArmyId = 2, Hex = Home },
                        new ArmySnapshot { ArmyId = 3, Hex = Home, IsMobileEconomyBuilder = true, HasHero = true },
                        new ArmySnapshot { ArmyId = 4, Hex = Home, IsMobileEconomyBuilder = true, HasHero = true },
                        new ArmySnapshot { ArmyId = 5, Hex = Home, IsMobileEconomyBuilder = true, HasHero = true },
                    },
                },
            };
            var routes = Enumerable.Range(1, 5).Select(id => new EconomyBuilderRouteSnapshot
                { ArmyId = id, TravelCost = 4 }).ToList();
            var surveil = new MissionIntent
            {
                Kind = MissionKind.Scout, Status = IntentStatus.Active, PreferredMoverArmyId = 3,
                Funding = CommitmentTier.Soft,
                Objective = new ScoutIntent { Kind = ScoutTargetKind.Surveil },
            };
            var commitments = new ActorCommitments();
            commitments.Claim(4);

            string Why(int id) => DemandLayer.EconomyBuilderCandidateRejection(snap, Site, routes, id,
                new[] { surveil }, commitments);

            Assert.That(Why(1), Is.EqualTo("garrison_without_extraction_route"));
            Assert.That(Why(2), Is.EqualTo("not_mobile_economy_builder"));
            Assert.That(Why(3), Does.StartWith("protected_assignment="));
            Assert.That(Why(4), Is.EqualTo("claimed"));
            Assert.That(Why(5), Does.StartWith("ranking_rejected"), "the gate passes a free builder");
            Assert.That(Why(6), Is.EqualTo("no_witnessed_route"));
        }

        // --- B12: collector usefulness is judged without its own contribution ---------------

        [Test]
        public void B12_ArrivedCollectorStaysUsefulWhileItsOwnIncomeCoversTheNeed()
        {
            var standing = new EconomyResourceStanding
            {
                Type = ResourceType.Materials,
                HandResourceNeed = 12f, SpendableStockpile = 0f,
                OwnIncome = 4f,   // includes the arrived collector's 2
            };

            Assert.That(standing.UsefulMarginalIncomeGain(2f), Is.Zero,
                "with its own income counted the collector looks surplus");
            Assert.That(standing.UsefulRetainedIncomeGain(2f), Is.GreaterThan(0f),
                "without it the need is uncovered: the collector is still worth its site");
        }
    }
}
#endif
