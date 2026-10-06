#if UNITY_INCLUDE_TESTS
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Game.Ai;
using Game.Ai.V2;
using Game.Cards;
using Game.Combat;
using Game.Economy;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Units;
using NUnit.Framework;

namespace Game.EditorTests
{
    public class AiEconomyOwnershipTests
    {
        private static readonly HexCoord Home = new HexCoord(0, 0);
        private static readonly HexCoord Site = new HexCoord(2, 0);
        private static WorthIt.DefenderProfile Profile(float power = 20) =>
            new WorthIt.DefenderProfile(defense: power, hasCeramicArmor: false,
                attack: power, hitPoints: 8, initiative: 5);
        private static AiMapMemory.KnownEnemySighting[] Threats() => new[]
        {
            new AiMapMemory.KnownEnemySighting(new HexCoord(1, 0), new PlayerSetupData(),
                "enemy", 90, defenseSum: 1, attackSum: 1, defenders: new[] { Profile(1) }),
        };
        private static ArmySnapshot Builder(int bodies = 0) => new ArmySnapshot
        {
            ArmyId = 20, Hex = Home, HasHero = true, IsMobileEconomyBuilder = true,
            HeroActivationApCost = 1, HeroMoveMax = 4, HeroCurrentMovement = 4,
            MaxMovement = 4, CurrentMovement = 4, ActivationApCost = 1 + bodies,
            Capacity = 8, MemberCount = 1 + bodies,
            Members = Enumerable.Range(0, bodies).Select(_ => Profile()).ToArray(),
            NonHeroActivationApCosts = Enumerable.Repeat(1, bodies).ToArray(),
            NonHeroMoveMax = Enumerable.Repeat(4, bodies).ToArray(),
        };
        private static ArmySnapshot Garrison() => new ArmySnapshot
        {
            ArmyId = 21, Hex = Home, IsGarrison = true, Capacity = 8,
            Members = new[] { Profile(), Profile(30) }, MemberCount = 2,
            OccupiedBattleSlots = 2, NonHeroActivationApCosts = new[] { 1, 4 },
            NonHeroMoveMax = new[] { 4, 4 }, MaxMovement = 4,
        };
        private static EconomyBuilderRouteSnapshot Route(ArmySnapshot a, int cost = 2) =>
            new EconomyBuilderRouteSnapshot
            {
                ArmyId = a.ArmyId, TravelCost = cost, CurrentMovement = a.CurrentMovement,
                MaxMovement = a.MaxMovement, ActivationApCost = a.ActivationApCost,
                HasActivatedThisTurn = a.HasActivatedThisTurn, MaximumStepCost = 1,
                RouteThreats = Threats(), PathHexes = new[] { Home, new HexCoord(1, 0), Site },
            };
        private static WorldSnapshot Snapshot(params ArmySnapshot[] armies) => new WorldSnapshot
        {
            TurnNumber = 1, Self = new SelfSnapshot { Armies = armies, BaseHexes = new[] { Home } },
            Known = new KnownSnapshot(), Economy = new EconomyStanding(),
        };
        private static DemandLayer.EconomyBuilderChoice Assess(WorldSnapshot s, ArmySnapshot a,
            EconomyBuilderRouteSnapshot? route = null) => DemandLayer.AssessEconomyArmy(
                s, Site, route ?? Route(a), a, 2, false);

        [TearDown]
        public void Clear()
        {
            ArmyRegistry.Clear(); MissionIntentRegistry.Clear();
            StrategicResourceReservationLedger.ClearAll(); AiAllocatorStateRegistry.Clear();
        }

        [Test]
        public void Reinforcement_SelectsCheapestSafeEscortAndPinsItsIndex()
        {
            var a = Builder(); var g = Garrison(); var s = Snapshot(a, g);
            var choice = Assess(s, a);
            Assert.That(choice.AddedIndices, Is.EqualTo(new[] { 0 }));
            Assert.That(choice.ProjectedActivationApCost, Is.EqualTo(2));
        }
        [Test]
        public void AssessmentCache_ReadCannotOverwriteTheNextDecision()
        {
            var a = Builder(); var s = Snapshot(a, Garrison()); var route = Route(a);
            var first = Assess(s, a, route);
            float cost = first.TotalAssignmentApCost;
            first.TotalAssignmentApCost = -100;
            first.Suitability = DemandLayer.EconomyArmySuitability.Ineligible;
            first.Route.TravelCost = 999;
            first.AddedIndices = new[] { 1 };
            var second = Assess(s, a, route);
            Assert.That(second.TotalAssignmentApCost, Is.EqualTo(cost));
            Assert.That(second.Suitability, Is.Not.EqualTo(DemandLayer.EconomyArmySuitability.Ineligible));
            Assert.That(second.Route.TravelCost, Is.EqualTo(route.TravelCost));
            Assert.That(second.AddedIndices, Is.EqualTo(new[] { 0 }));
            Assert.That(second.Route.PathHexes, Is.Not.SameAs(route.PathHexes));
        }
        [Test]
        public void AssessmentCache_DifferentBuildAndReturnCostsHaveSeparateEntries()
        {
            var a = Builder(); var s = Snapshot(a, Garrison()); var route = Route(a);
            route.ReturnTravelCost = 8;
            float ordinary = DemandLayer.AssessEconomyArmy(s, Site, route, a, 2, false).TotalAssignmentApCost;
            Assert.That(DemandLayer.AssessEconomyArmy(s, Site, route, a, 5, false).TotalAssignmentApCost,
                Is.EqualTo(ordinary + 3));
            Assert.That(DemandLayer.AssessEconomyArmy(s, Site, route, a, 2, true).TotalAssignmentApCost,
                Is.GreaterThan(ordinary));
        }
        [Test]
        public void CollectorLostHomeAtStallLimit_RetargetsBeforeCapabilityRetirement()
        {
            var p = new PlayerSetupData(); var a = Builder(); a.Hex = Site;
            var i = ReturnIntent(a, EconomyTaskKind.ReturnCollector);
            i.StallTurns = AiConfigV2.commitmentStallTurns - 1;
            MissionIntentRegistry.GetOrCreate(p).Put(i);
            MissionContinuityLayer.ReconcileStep(p, 2, new MissionTurnOutcome
            {
                IntentKey = i.IntentKey, MissionKind = MissionKind.Economy,
                Outcome = ExecutionOutcome.Failed, StructuralFailure = true,
                ProvisionFailureKindValue = ProvisionFailureKind.TargetInvalidated,
            });
            var alternate = new HexCoord(7, 0);
            a.EconomyHomeRouteCosts = new Dictionary<HexCoord, int> { [alternate] = 3 };
            var s = Snapshot(a); s.TurnNumber = 2; s.Self.BaseHexes = new[] { alternate };
            Assert.That(MissionContinuityLayer.ResolveActive(p, s), Has.Count.EqualTo(1));
            Assert.That(i.Economy.TargetHex, Is.EqualTo(alternate));
            Assert.That(i.StallTurns, Is.Zero);
        }
        [Test]
        public void Escort_CheaperButCannotEnterRouteStep_IsRejected()
        {
            var a = Builder(); var g = Garrison(); g.NonHeroMoveMax = new[] { 1, 4 };
            var route = Route(a); route.MaximumStepCost = 2;
            Assert.That(Assess(Snapshot(a, g), a, route).AddedIndices, Is.EqualTo(new[] { 1 }));
        }
        [Test]
        public void Reinforcement_CannotExceedHeroCapacity()
        {
            var a = Builder(); a.Capacity = 1;
            Assert.That(Assess(Snapshot(a, Garrison()), a).Suitability,
                Is.EqualTo(DemandLayer.EconomyArmySuitability.Ineligible));
        }
        [Test]
        public void Escort_IncompleteThreatWitnessDoesNotInventSafety()
        {
            var a = Builder(2); var route = Route(a);
            route.RouteThreats = new[] { new AiMapMemory.KnownEnemySighting(Site,
                new PlayerSetupData(), "unknown", 50, 1, 1, defenders: null) };
            Assert.That(Assess(Snapshot(a, Garrison()), a, route).Suitability,
                Is.EqualTo(DemandLayer.EconomyArmySuitability.Ineligible));
        }
        [Test]
        public void Lightening_HonorsGarrisonCapacityAndPinsRetainedRoster()
        {
            var a = Builder(2); var g = Garrison(); g.OccupiedBattleSlots = g.Capacity;
            Assert.That(Assess(Snapshot(a, g), a).RetainedIndices, Is.EqualTo(new[] { 0, 1 }));
        }
        [Test]
        public void LoanedBuilder_RosterIsPreserved()
        {
            var a = Builder(2); a.EconomyRosterProtected = true;
            Assert.That(Assess(Snapshot(a, Garrison()), a).RetainedIndices, Is.EqualTo(new[] { 0, 1 }));
        }
        [Test]
        public void MissingRoute_ShortHexDistanceCannotProduceBuilder()
        {
            var a = Builder(1);
            Assert.That(DemandLayer.SelectEconomyBuilder(Snapshot(a), new HexCoord(1, 0),
                null, null, null, 100, 1, false), Is.Null);
        }
        [Test]
        public void WitnessedDetour_UsesRealCostRatherThanHexDistance()
        {
            var a = Builder(1); var route = Route(a, 9);
            var choice = Assess(Snapshot(a), a, route);
            Assert.That(choice.Route.TravelCost, Is.EqualTo(9));
            Assert.That(choice.TotalAssignmentApCost, Is.EqualTo(8)); // 3 activations * 2 + build 2
        }
        private static MissionProposal Proposal(WorldSnapshot s, ArmySnapshot a, int travel)
        {
            var route = Route(a, travel);
            s.Economy.ExtractionOpportunities = new[] { new EconomyExtractionOpportunity
                { Hex = Site, ResourceType = ResourceType.Materials, BuilderRoutes = new[] { route } } };
            var demand = new AxisDemand
            {
                RequestingAxis = DesireAxis.Economy, Capability = CapabilityKind.EconomicInfrastructure,
                TargetHex = Site, EconomyResourceType = ResourceType.Materials,
                EconomyPreferredBuilderArmyId = a.ArmyId, EconomySiteValue = 100, Value = 100,
                EconomyBuildApCost = 2, MinimumFollowupAp = 3,
                EconomyBuildResourceCost = new ResourceCost { materials = 4 },
                EconomyBuilderRoutes = new[] { route },
            };
            return EconomyMissionPlanner.Propose(s, null, null, new[] { demand }).Single();
        }
        [Test]
        public void Planner_IncludesActivatedBuilderReinforcementTransferAp()
        {
            var a = Builder(); a.HasActivatedThisTurn = true;
            Assert.That(Proposal(Snapshot(a, Garrison()), a, 2).Requirements.ApMinimum, Is.EqualTo(4));
        }
        [Test]
        public void MultiTurnStage_FundsPreparationAndTravelWithoutBuildResources()
        {
            var a = Builder(); var m = Proposal(Snapshot(a, Garrison()), a, 9);
            Assert.That(m.Requirements.ApMinimum, Is.EqualTo(2));
            Assert.That(m.Requirements.MaterialsMinimum, Is.Zero);
        }
        [Test]
        public void CompletionStage_IncludesActivationAndFollowup()
        {
            var a = Builder(); var m = Proposal(Snapshot(a, Garrison()), a, 2);
            Assert.That(m.Requirements.ApMinimum, Is.EqualTo(5));
            Assert.That(m.Requirements.MaterialsMinimum, Is.EqualTo(4));
        }
        [TestCase(EconomyTaskKind.ReturnCollector)]
        [TestCase(EconomyTaskKind.ReturnBuilder)]
        public void LostHome_RetargetsReachableAlternativeAndRekeys(EconomyTaskKind kind)
        {
            var p = new PlayerSetupData(); var a = Builder(); a.Hex = Site;
            var homeB = new HexCoord(6, 0); var s = Snapshot(a); s.Self.BaseHexes = new[] { homeB };
            a.EconomyHomeRouteCosts = new Dictionary<HexCoord, int> { [homeB] = 7 };
            var i = ReturnIntent(a, kind); var old = i.IntentKey;
            MissionIntentRegistry.GetOrCreate(p).Put(i);
            Assert.That(MissionContinuityLayer.ResolveActive(p, s).Single(), Is.SameAs(i));
            Assert.That(i.Economy.TargetHex, Is.EqualTo(homeB));
            Assert.That(i.Economy.SafeReturnHex, Is.EqualTo(homeB));
            Assert.That(MissionIntentRegistry.GetOrCreate(p).TryGet(old, out _), Is.False);
        }
        [TestCase(EconomyTaskKind.ReturnCollector)]
        [TestCase(EconomyTaskKind.ReturnBuilder)]
        public void LostLastReachableHome_Retires(EconomyTaskKind kind)
        {
            var p = new PlayerSetupData(); var a = Builder(); a.Hex = Site;
            a.EconomyHomeRouteCosts = new Dictionary<HexCoord, int>();
            var s = Snapshot(a); s.Self.BaseHexes = new[] { new HexCoord(6, 0) };
            var i = ReturnIntent(a, kind);
            MissionIntentRegistry.GetOrCreate(p).Put(i);
            Assert.That(MissionContinuityLayer.ResolveActive(p, s), Is.Empty);
            Assert.That(MissionIntentRegistry.GetOrCreate(p).Count, Is.Zero);
        }
        [TestCase(EconomyTaskKind.ReturnCollector)]
        [TestCase(EconomyTaskKind.ReturnBuilder)]
        public void OwnedHome_TemporaryRouteBlockKeepsExistingReturn(EconomyTaskKind kind)
        {
            var p = new PlayerSetupData(); var a = Builder(); a.Hex = Site;
            a.EconomyHomeRouteCosts = new Dictionary<HexCoord, int>();
            var s = Snapshot(a); var i = ReturnIntent(a, kind);
            MissionIntentRegistry.GetOrCreate(p).Put(i);
            Assert.That(MissionContinuityLayer.ResolveActive(p, s), Does.Contain(i));
            Assert.That(i.Economy.TargetHex, Is.EqualTo(Home));
        }
        [Test]
        public void Reinforcement_RemainingMovementControlsCompletionStage()
        {
            var a = Builder(); var g = Garrison();
            g.NonHeroCurrentMovement = new[] { 1, 4 };
            var s = Snapshot(a, g); var choice = Assess(s, a);
            Assert.That(choice.Route.CurrentMovement, Is.EqualTo(1));
            var m = Proposal(s, a, 2);
            Assert.That(m.Requirements.ApMinimum, Is.EqualTo(2));
            Assert.That(m.Requirements.MaterialsMinimum, Is.Zero);
        }
        [Test]
        public void GarrisonExtraction_IsPricedOnceForMultiTurnAssignment()
        {
            var a = Builder(); a.IsGarrison = true;
            var route = Route(a, 9); route.RequiresGarrisonExtraction = true;
            route.ExtractionContainerAvailable = true; route.ExtractionApCost = 2;
            var choice = DemandLayer.AssessEconomyArmy(Snapshot(a), Site, route, a, 2f, includeReturn: true);
            Assert.That(choice.PreparationApCost, Is.EqualTo(2));
            Assert.That(choice.Route.ActivationApCost, Is.EqualTo(1));
            Assert.That(choice.TotalAssignmentApCost, Is.EqualTo(7));
        }

        [Test]
        public void FoundingNeedsBody_ExtractionCanUseSafeSoloHero()
        {
            var hero = Builder(); var route = Route(hero);
            route.RouteThreats = Array.Empty<AiMapMemory.KnownEnemySighting>();
            WorldSnapshot snap = Snapshot(hero);
            Assert.That(Assess(snap, hero, route).Suitability,
                Is.EqualTo(DemandLayer.EconomyArmySuitability.Ineligible));
            Assert.That(DemandLayer.AssessEconomyArmy(snap, Site, route, hero, 2, true).Suitability,
                Is.EqualTo(DemandLayer.EconomyArmySuitability.Ready));
            // Mission leg pricing excludes the return independently of the building kind.
            Assert.That(DemandLayer.AssessEconomyArmy(snap, Site, route, hero, 2, false,
                requiresFoundingGarrison: false).Suitability,
                Is.EqualTo(DemandLayer.EconomyArmySuitability.Ready));
        }

        [Test]
        public void AssessmentCache_FoundingRequirementIsIndependentOfReturnPricing()
        {
            var solo = Builder(); var route = Route(solo);
            route.RouteThreats = Array.Empty<AiMapMemory.KnownEnemySighting>();
            var snap = Snapshot(solo);
            Assert.That(DemandLayer.AssessEconomyArmy(snap, Site, route, solo, 2, false,
                requiresFoundingGarrison: false).Suitability,
                Is.EqualTo(DemandLayer.EconomyArmySuitability.Ready));
            Assert.That(DemandLayer.AssessEconomyArmy(snap, Site, route, solo, 2, false,
                requiresFoundingGarrison: true).Suitability,
                Is.EqualTo(DemandLayer.EconomyArmySuitability.Ineligible));
            Assert.That(DemandLayer.AssessEconomyArmy(snap, Site, route, solo, 2, false,
                requiresFoundingGarrison: false).Suitability,
                Is.EqualTo(DemandLayer.EconomyArmySuitability.Ready));
        }

        [Test]
        public void OnSiteFounding_StillRespectsProtectedAttackOwnership()
        {
            var builder = Builder(1); builder.Hex = Site;
            var route = Route(builder, 0); route.IsOnTarget = true;
            route.RouteThreats = Array.Empty<AiMapMemory.KnownEnemySighting>();
            var snap = Snapshot(builder);
            Assert.That(Assess(snap, builder, route).Suitability,
                Is.EqualTo(DemandLayer.EconomyArmySuitability.Ready), "composition alone is valid");
            var attack = new MissionIntent {
                Kind = MissionKind.Attack, Status = IntentStatus.Active,
                Funding = CommitmentTier.Hard, PreferredMoverArmyId = builder.ArmyId,
            };
            Assert.That(DemandLayer.SelectEconomyBuilder(snap, Site, new[] { route },
                new[] { attack }, new ActorCommitments(), 50f, 2f, false), Is.Null,
                "on-site completion must not take a protected Attack mover");
            Assert.That(DemandLayer.SelectEconomyBuilder(snap, Site, new[] { route },
                Array.Empty<MissionIntent>(), new ActorCommitments(), 50f, 2f, false), Is.Not.Null);
        }

        [Test]
        public void LostEscort_NewSnapshotRevokesFoundingReadiness()
        {
            var escorted = Builder(1); var route = Route(escorted);
            route.RouteThreats = Array.Empty<AiMapMemory.KnownEnemySighting>();
            WorldSnapshot before = Snapshot(escorted);
            Assert.That(Assess(before, escorted, route).Suitability,
                Is.EqualTo(DemandLayer.EconomyArmySuitability.Ready));
            var solo = Builder(); WorldSnapshot after = Snapshot(solo);
            Assert.That(Assess(after, solo, route).Suitability,
                Is.EqualTo(DemandLayer.EconomyArmySuitability.Ineligible));
            Assert.That(Assess(before, escorted, route).Suitability,
                Is.EqualTo(DemandLayer.EconomyArmySuitability.Ready), "old facts retain their own decision");
        }

        [Test]
        public void ExtractedFoundingHero_PreparesAConcreteSparableBody()
        {
            var garrison = Builder(1); garrison.IsGarrison = true;
            garrison.NonHeroSpareable = new[] { true };
            var route = Route(garrison); route.RequiresGarrisonExtraction = true;
            route.ExtractionContainerAvailable = true; route.ExtractedHeroCapacity = 4;
            route.RouteThreats = Array.Empty<AiMapMemory.KnownEnemySighting>();
            var ready = Assess(Snapshot(garrison), garrison, route);
            Assert.That(ready.Suitability, Is.EqualTo(DemandLayer.EconomyArmySuitability.Ready));
            Assert.That(ready.AddedIndices, Is.EqualTo(new[] { 0 }));
            Assert.That(ready.ProjectedActivationApCost, Is.EqualTo(route.ActivationApCost + 1));
            garrison.NonHeroSpareable = new[] { false };
            Assert.That(Assess(Snapshot(garrison), garrison, route).Suitability,
                Is.EqualTo(DemandLayer.EconomyArmySuitability.Ineligible), "original garrison keeps its floor");
        }
        private static MissionIntent ReturnIntent(ArmySnapshot a, EconomyTaskKind kind)
        {
            var i = new MissionIntent { Kind = MissionKind.Economy, Status = IntentStatus.Active,
                PreferredMoverArmyId = a.ArmyId, LastProgressTurn = 1, Objective = new EconomyIntent
                { Kind = kind, TargetHex = Home, SafeReturnHex = Home, BuilderArmyId = a.ArmyId,
                    CollectorArmyId = a.ArmyId } };
            i.IntentKey = MissionIntentKey.For(i); return i;
        }
        [Test]
        public void InitialHomeAndRetarget_UseSameSafePathRanking()
        {
            var p = new PlayerSetupData(); var a = Builder(); a.Hex = Site;
            var nearHexButFarRoute = Home; var farHexButShortRoute = new HexCoord(8, 0);
            var s = Snapshot(a); s.Self.BaseHexes = new[] { nearHexButFarRoute, farHexButShortRoute };
            a.EconomyHomeRouteCosts = new Dictionary<HexCoord, int>
                { [nearHexButFarRoute] = 12, [farHexButShortRoute] = 6 };
            Assert.That(MissionContinuityLayer.SelectEconomyHome(s, Site, a.EconomyHomeRouteCosts),
                Is.EqualTo(farHexButShortRoute));
            Assert.That(MissionContinuityLayer.SelectEconomyRecoveryTarget(s, p, a),
                Is.EqualTo(farHexButShortRoute));
        }
        [Test]
        public void CollectorLostHomeFailure_PreservesIntentForFreshRetarget()
        {
            var p = new PlayerSetupData(); var a = Builder(); var i = ReturnIntent(a, EconomyTaskKind.ReturnCollector);
            MissionIntentRegistry.GetOrCreate(p).Put(i);
            MissionContinuityLayer.ReconcileStep(p, 2, new MissionTurnOutcome
                { IntentKey = i.IntentKey, MissionKind = MissionKind.Economy,
                    Outcome = ExecutionOutcome.Failed, StructuralFailure = true,
                    ProvisionFailureKindValue = ProvisionFailureKind.TargetInvalidated,
                    EconomyTarget = new EconomyMissionTarget { Kind = EconomyTaskKind.ReturnCollector } });
            Assert.That(MissionIntentRegistry.GetOrCreate(p).TryGet(i.IntentKey, out _), Is.True);
        }
        [Test]
        public void BuildMatcher_DemandAndPhaseAAgreeOnResourceAndCard()
        {
            var p = new PlayerSetupData(); var a = Builder();
            var card = new CardData(new CardDefinition { cardType = CardType.Facility });
            var d = new AxisDemand { RequestingAxis = DesireAxis.Economy,
                Capability = CapabilityKind.EconomicInfrastructure, TargetHex = Site,
                EconomyResourceType = ResourceType.Materials, EconomyBuildCard = card };
            var i = new MissionIntent { Kind = MissionKind.Economy, Status = IntentStatus.Suspended,
                Objective = new EconomyIntent { Kind = EconomyTaskKind.BuildExtraction,
                    TargetHex = Site, ResourceType = ResourceType.Energy, BuildCard = card } };
            MethodInfo phaseA = typeof(StrategicPhaseA).GetMethod("IsCommittedEconomyBuild",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(MissionContinuityLayer.HasEconomyBuildCommitment(new[] { i }, d), Is.False);
            Assert.That(phaseA.Invoke(null, new object[] { new[] { i }, d }), Is.EqualTo(false));
            i.Economy.ResourceType = ResourceType.Materials;
            Assert.That(MissionContinuityLayer.HasEconomyBuildCommitment(new[] { i }, d), Is.True);
            Assert.That(phaseA.Invoke(null, new object[] { new[] { i }, d }), Is.EqualTo(true));
        }
        [Test]
        public void ParallelBuildOwners_ReleaseDoesNotRemoveOtherHold()
        {
            var p = new PlayerSetupData();
            string a = EconomyMissionPlanner.OwnerKey(StableMissionKey.ForEconomy(
                EconomyTaskKind.BuildExtraction, (int)ResourceType.Materials, Site));
            string b = EconomyMissionPlanner.OwnerKey(StableMissionKey.ForEconomy(
                EconomyTaskKind.BuildExtraction, (int)ResourceType.Materials, new HexCoord(3, 0)));
            InfrastructureFulfillment.ReserveEconomyCost(p, 1, a, new ResourceCost { materials = 4 }, 0,
                StrategicReservationReason.EconomyDeferredBuild);
            InfrastructureFulfillment.ReserveEconomyCost(p, 1, b, new ResourceCost { energy = 2 }, 0,
                StrategicReservationReason.EconomyDeferredBuild);
            InfrastructureFulfillment.ReserveEconomyCost(p, 1, a, new ResourceCost { materials = 4 }, 3);
            StrategicResourceReservationLedger.ReleaseByOwner(p, 1, a);
            Assert.That(StrategicResourceReservationLedger.HasOwnerReason(p, 1, b,
                StrategicReservationReason.EconomyDeferredBuild), Is.True);
        }
        private static UnitData Unit(bool hero, int ap, int movement = 4) => new UnitData
        {
            Name = hero ? "builder" : "escort", IsHero = hero, CommandRating = 8,
            ActivationApCost = ap, MoveMax = movement, MoveCurrent = movement,
            Attack = 20, Defense = 20, HitPointsMax = 8, HitPointsCurrent = 8, Initiative = 5,
        };
        [TestCase(0)]
        [TestCase(1)]
        public void Provisioning_UsesMovementAfterPinnedUnload(int escortMovement)
        {
            var p = new PlayerSetupData();
            var hero = Unit(true, 1); var escort = Unit(false, 1);
            escort.MoveCurrent = escortMovement;
            var builder = new ArmyData { Owner = p, Hex = Home };
            builder.Members.AddRange(new[] { hero, escort });
            var garrison = new ArmyData { Owner = p, Hex = Home, IsGarrison = true };
            ArmyRegistry.Register(builder); ArmyRegistry.Register(garrison);
            var mapObject = new UnityEngine.GameObject("projected economy movement");
            var rootObject = new UnityEngine.GameObject("economy resources");
            try
            {
                var map = mapObject.AddComponent<HexMap>();
                map.SetData(2, 1, new Dictionary<HexCoord, Game.Terrain.TerrainTypeEntry>
                {
                    [Home] = new Game.Terrain.TerrainTypeEntry { moveCost = 1 },
                    [new HexCoord(1, 0)] = new Game.Terrain.TerrainTypeEntry { moveCost = 2 },
                });
                var root = rootObject.AddComponent<PlayerRoot>(); root.ActionPoints = 10;
                var a = WorldAnalysis.ToArmySnapshot(builder, p, true, 0);
                var g = WorldAnalysis.ToArmySnapshot(garrison, p, true, 0);
                var s = Snapshot(a, g);
                var target = new EconomyMissionTarget
                {
                    Kind = EconomyTaskKind.BuildExtraction, TargetHex = new HexCoord(1, 0),
                    ResourceType = ResourceType.Materials, BuildApCost = 2, BuildValue = 50,
                    BuilderRoutes = new[] { new EconomyBuilderRouteSnapshot
                    {
                        ArmyId = builder.Id, TravelCost = 2, CurrentMovement = escortMovement,
                        MaxMovement = 4, ActivationApCost = 2, MaximumStepCost = 2,
                        PathHexes = new[] { Home, new HexCoord(1, 0) },
                        RouteThreats = Array.Empty<AiMapMemory.KnownEnemySighting>(),
                    } },
                };
                var mission = new MissionProposal
                {
                    Kind = MissionKind.Economy, Target = target,
                    PreferredMoverArmyId = builder.Id, Requirements = new MissionRequirements(),
                };
                var result = ProvisioningManager.Provision(p, root, null,
                    new AiTurnContext { Map = map, TurnNumber = 1 }, new ProvisioningSession(s),
                    new FundedEntry { Mission = mission, Tentative = new ResourceVector(10) });
                Assert.That(result.Success, Is.True, result.Failure.ToString());
                Assert.That(result.Provisioned.EconomyExtractionPreparation.Unload, Is.EqualTo(new[] { escort }));
                Assert.That(result.Provisioned.EconomyExtractionPreparation.CompletionThisTurn, Is.True);
                Assert.That(builder.Members, Has.Count.EqualTo(2), "provisioning must remain read-only");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(mapObject);
                UnityEngine.Object.DestroyImmediate(rootObject);
            }
        }
        [Test]
        public void Materialization_AppliesDemandPinnedRosterRegardlessOfLivePowerTie()
        {
            var p = new PlayerSetupData(); var hero = Unit(true, 1);
            var cheap = Unit(false, 1); var dear = Unit(false, 4);
            var builder = new ArmyData { Owner = p, Hex = Home };
            builder.Members.AddRange(new[] { hero, cheap, dear });
            var garrison = new ArmyData { Owner = p, Hex = Home, IsGarrison = true };
            ArmyRegistry.Register(builder); ArmyRegistry.Register(garrison);
            var a = Builder(2); a.ArmyId = builder.Id;
            a.NonHeroRuntimeIds = new[] { cheap.RuntimeId, dear.RuntimeId };
            a.NonHeroActivationApCosts = new[] { 1, 4 };
            a.NonHeroCurrentMovement = new[] { 4, 4 };
            var g = Garrison(); g.ArmyId = garrison.Id; g.Members = Array.Empty<WorthIt.DefenderProfile>();
            g.NonHeroActivationApCosts = Array.Empty<int>(); g.MemberCount = g.OccupiedBattleSlots = 0;
            var choice = Assess(Snapshot(a, g), a);
            Assert.That(ProvisioningManager.MaterializeEconomyRoster(p, builder, choice,
                out ArmyData recipient, out List<UnitData> unload, out List<UnitData> add), Is.True);
            Assert.That(unload, Is.EqualTo(new[] { dear })); Assert.That(add, Is.Empty);
            Assert.That(ProvisioningManager.ApplyEconomyArmyLightening(builder, recipient, unload, add,
                new AiTurnContext()), Is.EqualTo(1));
            Assert.That(builder.Members, Is.EqualTo(new[] { hero, cheap }));
            Assert.That(ProvisioningManager.EconomyMissionClaimedAp(builder, 2, 3, null,
                travelNeeded: true, completionThisTurn: true), Is.EqualTo(5));
        }
        [Test]
        public void Materialization_ChangedRuntimeIdentityInvalidatesWitness()
        {
            var p = new PlayerSetupData(); var builder = new ArmyData { Owner = p, Hex = Home };
            builder.Members.AddRange(new[] { Unit(true, 1), Unit(false, 1) });
            var a = Builder(1); a.ArmyId = builder.Id; a.NonHeroRuntimeIds = new[] { -999 };
            Assert.That(ProvisioningManager.MaterializeEconomyRoster(p, builder, Assess(Snapshot(a), a),
                out _, out _, out _), Is.False);
        }
    }
}
#endif
