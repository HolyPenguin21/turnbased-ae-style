#if UNITY_INCLUDE_TESTS
using System.Linq;
using Game.Ai.V2;
using Game.Aviation;
using Game.Cards;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Units;
using NUnit.Framework;

namespace Game.EditorTests
{
    public sealed class CarrierAirfieldTests
    {
        private PlayerSetupData _owner;
        private static readonly HexCoord Home = new HexCoord(0, 0);

        [SetUp]
        public void SetUp()
        {
            ArmyRegistry.Clear(); BuildingRegistry.Clear(); AirSortieRegistry.Clear(); VisionSystem.Clear();
            AntiAirState.Clear();
            StrategicResourceReservationLedger.ClearAll();
            _owner = new PlayerSetupData { IsHuman = true };
        }

        [TearDown]
        public void TearDown()
        {
            ArmyRegistry.Clear(); BuildingRegistry.Clear(); AirSortieRegistry.Clear(); VisionSystem.Clear();
            StrategicResourceReservationLedger.ClearAll();
        }

        private UnitData Carrier(ArmyData army = null)
        {
            var unit = new UnitData { Name = "Ark Carrier", Owner = _owner, AirfieldCapacity = 2,
                HitPointsCurrent = 8, HitPointsMax = 8, MoveMax = 2, MoveCurrent = 2 };
            unit.Abilities.Add(UnitAbilities.Airfield);
            if (army == null)
            {
                army = new ArmyData { Owner = _owner, Hex = Home };
                army.Members.Add(unit); ArmyRegistry.Register(army);
            }
            else { army.Members.Add(unit); AviationActions.RefreshCarrierContainers(army); }
            return unit;
        }

        private UnitData Plane() => new UnitData { Name = "Plane", Owner = _owner, IsAviation = true,
            HitPointsCurrent = 4, HitPointsMax = 4, MoveMax = 5, MoveCurrent = 1,
            SortieLaunchPaid = true, HasAirAttackedThisTurn = true, TurnsWithoutRefuel = 0 };

        private ArmyData Wing(params UnitData[] planes)
        {
            var wing = new ArmyData { Owner = _owner, Hex = Home, IsAirArmy = true };
            wing.Members.AddRange(planes); ArmyRegistry.Register(wing); return wing;
        }

        [Test]
        public void SpawnCreatesOneNamedHangarAndDeck_WithoutBuilding()
        {
            UnitData c = Carrier();
            AviationActions.EnsureCarrierContainers(ArmyRegistry.FindArmyContaining(c));
            Assert.That(ArmyRegistry.AllForOwner(_owner).Count(), Is.EqualTo(3));
            Assert.That(c.CarrierAirfield.Name, Does.Contain("Ark Carrier").And.Contain("Airfield"));
            Assert.That(c.CarrierFlightDeck.Name, Does.Contain("Air Wing"));
            Assert.That(AviationRules.IsOwnedAirfieldAt(Home, _owner), Is.True);
            Assert.That(AviationRules.AirfieldCapacityAt(Home, _owner), Is.EqualTo(2));
            Assert.That(AviationRules.CanContain(c.CarrierFlightDeck, new UnitData()), Is.False);
        }

        [Test]
        public void CarrierMovementMovesCargoAndReadyWing_WithoutSpendingTheirMovement()
        {
            UnitData c = Carrier(); UnitData stored = Plane(), ready = Plane();
            c.CarrierAirfield.Members.Add(stored); c.CarrierFlightDeck.Members.Add(ready);
            HexCoord destination = new HexCoord(1, 0);
            ArmyRegistry.MoveArmy(ArmyRegistry.FindArmyContaining(c), destination);
            Assert.That(c.CarrierAirfield.Hex, Is.EqualTo(destination));
            Assert.That(c.CarrierFlightDeck.Hex, Is.EqualTo(destination));
            Assert.That(stored.MoveCurrent, Is.EqualTo(1)); Assert.That(ready.MoveCurrent, Is.EqualTo(1));
            Assert.That(stored.HasAirAttackedThisTurn, Is.True);
            Assert.That(AiAirSortiePlanner.OwnedAirfieldHexes(_owner), Does.Contain(destination));
            Assert.That(AiAirSortiePlanner.OwnedAirfieldHexes(_owner), Has.No.Member(Home));
        }

        [Test]
        public void TakeOffDetachesWingAndRestoresExactlyOneEmptyDeck()
        {
            UnitData c = Carrier(); ArmyData departing = c.CarrierFlightDeck;
            departing.Members.Add(Plane()); AviationActions.DetachCarrierWing(departing);
            ArmyRegistry.MoveArmy(ArmyRegistry.FindArmyContaining(c), new HexCoord(1, 0));
            Assert.That(departing.Hex, Is.EqualTo(Home)); Assert.That(departing.IsCarrierContainer, Is.False);
            Assert.That(c.CarrierFlightDeck, Is.Not.SameAs(departing));
            Assert.That(c.CarrierFlightDeck.Members, Is.Empty);
            Assert.That(ArmyRegistry.AllForOwner(_owner).Count(a => a.IsCarrierFlightDeck), Is.EqualTo(1));
        }

        [Test]
        public void LaunchUsesCarrierDeckAndRejectsDuplicateAircraft()
        {
            UnitData c = Carrier(), p = Plane(); c.CarrierAirfield.Members.Add(p);
            Assert.That(AviationActions.TryLaunch(c.CarrierAirfield, new[] { p, p }, null, null,
                out _, out _), Is.False);
            Assert.That(c.CarrierAirfield.Members.Count, Is.EqualTo(1));
            Assert.That(AviationActions.TryLaunch(c.CarrierAirfield, new[] { p }, null, null,
                out ArmyData wing, out _), Is.True);
            Assert.That(wing, Is.SameAs(c.CarrierFlightDeck));
            Assert.That(wing.Members, Is.EquivalentTo(new[] { p }));
        }

        [Test]
        public void LaunchBesideOccupiedDeckFormsSeparateWingOfExactlySelectedAircraft()
        {
            UnitData c = Carrier(), ready = Plane(), next = Plane();
            c.CarrierFlightDeck.Members.Add(ready); c.CarrierAirfield.Members.Add(next);
            Assert.That(AviationActions.TryLaunch(c.CarrierAirfield, new[] { next }, null, null,
                out ArmyData wing, out _), Is.True);
            Assert.That(wing, Is.Not.SameAs(c.CarrierFlightDeck));
            Assert.That(wing.Members, Is.EquivalentTo(new[] { next }));
            Assert.That(wing.LaunchCarrier, Is.SameAs(c));
            Assert.That(c.CarrierFlightDeck.Members, Is.EquivalentTo(new[] { ready }));
            Assert.That(AviationRules.FreeAirfieldCapacity(Home, _owner), Is.EqualTo(0));
        }

        [Test]
        public void DepartingCarrierLandsArrivedWingInsteadOfStrandingIt()
        {
            UnitData c = Carrier(), p = Plane(); ArmyData arrived = Wing(p);
            var destination = new HexCoord(1, 0);
            ArmyRegistry.MoveArmy(ArmyRegistry.FindArmyContaining(c), destination);
            Assert.That(c.CarrierAirfield.Members, Does.Contain(p));
            Assert.That(c.CarrierAirfield.Hex, Is.EqualTo(destination));
            Assert.That(ArmyRegistry.IsRegistered(arrived), Is.False);
            Assert.That(p.SortieLaunchPaid, Is.False);
        }

        [Test]
        public void DepartingCarrierLeavesWingsThatStillHaveAFixedAirfield()
        {
            UnitData c = Carrier();
            BuildingRegistry.Register(Home, new BuildingData { Owner = _owner, Hex = Home, AirfieldCapacity = 4 });
            UnitData p = Plane(); ArmyData arrived = Wing(p);
            ArmyRegistry.MoveArmy(ArmyRegistry.FindArmyContaining(c), new HexCoord(1, 0));
            Assert.That(ArmyRegistry.IsRegistered(arrived), Is.True);
            Assert.That(arrived.Members, Does.Contain(p));
            Assert.That(c.CarrierAirfield.Members, Is.Empty);
        }

        [TestCase(true)]
        [TestCase(false)]
        public void OrdinaryWingAtFixedAirfieldBesideCarrierKeepsItsEndTurnLifetime(bool human)
        {
            _owner.IsHuman = human; UnitData c = Carrier();
            BuildingRegistry.Register(Home, new BuildingData { Owner = _owner, Hex = Home, AirfieldCapacity = 4 });
            ArmyData fixedAirfield = AviationActions.EnsureAirfield(null, _owner, Home);
            UnitData p = Plane(); ArmyData wing = Wing(p);
            AviationTurnLifecycle.ResolveEndOfTurn(_owner, null);
            Assert.That(ArmyRegistry.IsRegistered(wing), Is.True);
            Assert.That(c.CarrierAirfield.Members, Is.Empty);
            if (human) Assert.That(wing.Members, Does.Contain(p));
            else Assert.That(fixedAirfield.Members, Does.Contain(p));
        }

        [Test]
        public void AirfieldHexOrderKeepsStartingCitadelFirstAndCarriersAfterFixedFields()
        {
            var citadel = new HexCoord(5, 0); var outpost = new HexCoord(2, 0);
            BuildingRegistry.Register(outpost, new BuildingData { Owner = _owner, Hex = outpost, AirfieldCapacity = 2 });
            BuildingRegistry.Register(citadel, new BuildingData { Owner = _owner, Hex = citadel, AirfieldCapacity = 2,
                IsStartingCitadel = true });
            Carrier();
            Assert.That(AviationRules.OwnedAirfieldHexes(_owner), Is.EqualTo(new[] { citadel, outpost, Home }));
        }

        [Test]
        public void ReadyWingAndHangarShareCapacity_AndTransfersDoNotNeedAnExtraSlot()
        {
            UnitData c = Carrier(); UnitData a = Plane(), b = Plane();
            c.CarrierAirfield.Members.AddRange(new[] { a, b });
            Assert.That(AviationRules.FreeAirfieldCapacity(Home, _owner), Is.EqualTo(0));
            Assert.That(ArmyActions.TransferMember(a, c.CarrierAirfield, c.CarrierFlightDeck, null, out _), Is.True);
            Assert.That(AviationRules.FreeAirfieldCapacity(Home, _owner), Is.EqualTo(0));
            Assert.That(c.CarrierAirfield.Members.Count + c.CarrierFlightDeck.Members.Count, Is.EqualTo(2));
        }

        [Test]
        public void FixedAirfieldAndCarrierSumCapacityWithoutGivingCarrierTheBuildingSlots()
        {
            UnitData c = Carrier();
            BuildingRegistry.Register(Home, new BuildingData { Owner = _owner, Hex = Home, AirfieldCapacity = 4 });
            AviationActions.EnsureAirfield(null, _owner, Home);
            Assert.That(AviationRules.AirfieldCapacityAt(Home, _owner), Is.EqualTo(6));
            Assert.That(AviationRules.ContainerCapacity(c.CarrierAirfield), Is.EqualTo(2));
            Assert.That(AviationRules.AirfieldsAt(Home, _owner).Count(), Is.EqualTo(2));
        }

        [TestCase(true)]
        [TestCase(false)]
        public void EndTurnActuallyStoresReturnedAircraftAndDeletesArrival(bool human)
        {
            _owner.IsHuman = human; UnitData c = Carrier(); UnitData p = Plane(); ArmyData arrived = Wing(p);
            AviationTurnLifecycle.ResolveEndOfTurn(_owner, null);
            Assert.That(c.CarrierAirfield.Members, Does.Contain(p));
            Assert.That(ArmyRegistry.IsRegistered(arrived), Is.False);
            Assert.That(c.CarrierFlightDeck.Members, Is.Empty);
            Assert.That(ArmyRegistry.AllForOwner(_owner).Count(a => a.IsCarrierFlightDeck), Is.EqualTo(1));
            Assert.That(p.SortieLaunchPaid, Is.False); Assert.That(p.HasAirAttackedThisTurn, Is.True);
            Assert.That(p.MoveCurrent, Is.EqualTo(1)); Assert.That(p.HitPointsCurrent, Is.EqualTo(4));
        }

        [Test]
        public void FullDeckLeavesOverflowAirborneAndAppliesNormalFuelDamage()
        {
            UnitData c = Carrier(); UnitData a = Plane(), b = Plane(), overflow = Plane();
            ArmyData arrived = Wing(a, b, overflow);
            AviationTurnLifecycle.ResolveEndOfTurn(_owner, null);
            Assert.That(c.CarrierAirfield.Members.Count, Is.EqualTo(2));
            Assert.That(arrived.Members, Is.EquivalentTo(new[] { overflow }));
            Assert.That(overflow.HitPointsCurrent, Is.EqualTo(2));
            Assert.That(overflow.HasEmergencyFlightPenalty, Is.True);
        }

        [Test]
        public void TwoCarriersShareHexCapacityButKeepCargoOwnership()
        {
            UnitData first = Carrier(), second = Carrier();
            ArmyData wing = Wing(Plane(), Plane(), Plane());
            Assert.That(AviationRules.AirfieldCapacityAt(Home, _owner), Is.EqualTo(4));
            Assert.That(AviationActions.LandInSlotOrder(wing, null), Is.EqualTo(3));
            Assert.That(first.CarrierAirfield.Members.Count, Is.EqualTo(2));
            Assert.That(second.CarrierAirfield.Members.Count, Is.EqualTo(1));
            ArmyRegistry.MoveArmy(ArmyRegistry.FindArmyContaining(first), new HexCoord(1, 0));
            Assert.That(second.CarrierAirfield.Hex, Is.EqualTo(Home));
            Assert.That(first.CarrierAirfield.Members.Count, Is.EqualTo(2));
        }

        [Test]
        public void TransferringCarrierPreservesBothContainers()
        {
            UnitData c = Carrier(); ArmyData hangar = c.CarrierAirfield, deck = c.CarrierFlightDeck;
            hangar.Members.Add(Plane());
            var target = new ArmyData { Owner = _owner, Hex = Home }; ArmyRegistry.Register(target);
            Assert.That(ArmyActions.TransferMember(c, ArmyRegistry.FindArmyContaining(c), target, null, out _), Is.True);
            Assert.That(c.CarrierAirfield, Is.SameAs(hangar)); Assert.That(c.CarrierFlightDeck, Is.SameAs(deck));
            ArmyRegistry.MoveArmy(target, new HexCoord(1, 0));
            Assert.That(hangar.Hex, Is.EqualTo(target.Hex)); Assert.That(hangar.Members.Count, Is.EqualTo(1));
        }

        [Test]
        public void CarrierDeathRemovesOnlyItsContainers_WhileEscortAndAirborneWingSurvive()
        {
            UnitData c = Carrier(); ArmyData ground = ArmyRegistry.FindArmyContaining(c);
            ground.Members.Add(new UnitData { Owner = _owner });
            ArmyData hangar = c.CarrierAirfield, deck = c.CarrierFlightDeck;
            UnitData stored = Plane(); stored.OriginatingCard = new CardDefinition { displayName = "Plane" };
            hangar.Members.Add(stored); ArmyData airborne = Wing(Plane());
            UnitData other = Carrier(); int returned = 0;
            System.Action<PlayerSetupData, CardData> onReturned = (owner, card) => returned++;
            AviationActions.AircraftReturnedToHand += onReturned;
            try { c.HitPointsCurrent = 0; ground.Members.Remove(c); StealthSystem.OnUnitRemoved(c); }
            finally { AviationActions.AircraftReturnedToHand -= onReturned; }
            Assert.That(returned, Is.EqualTo(1)); Assert.That(ArmyRegistry.IsRegistered(hangar), Is.False);
            Assert.That(ArmyRegistry.IsRegistered(deck), Is.False); Assert.That(ArmyRegistry.IsRegistered(ground), Is.True);
            Assert.That(ArmyRegistry.IsRegistered(airborne), Is.True);
            Assert.That(ArmyRegistry.IsRegistered(other.CarrierAirfield), Is.True);
        }

        [Test]
        public void MovingCarrierReleasesReservationAtItsOldHex()
        {
            UnitData c = Carrier(); ArmyData wing = Wing(Plane());
            ArmyRegistry.MoveArmy(wing, new HexCoord(0, 1));
            AirSortieRegistry.Add(_owner, new AirSortie { Army = wing, LandingHex = Home });
            Assert.That(AviationRules.ReservedLandingSlots(Home, _owner, null), Is.EqualTo(1));
            ArmyRegistry.MoveArmy(ArmyRegistry.FindArmyContaining(c), new HexCoord(1, 0));
            Assert.That(AviationRules.ReservedLandingSlots(Home, _owner, null), Is.EqualTo(0));
        }

        [Test]
        public void CombatSupportLandingSelectionUsesAirfieldHexesInsteadOfBaseHexes()
        {
            var mobile = new HexCoord(3, 0);
            var snapshot = new WorldSnapshot { Self = new SelfSnapshot
                { BaseHexes = new[] { Home }, AirfieldHexes = new[] { mobile } } };
            Assert.That(GroundCombatAirSupport.LandingBase(snapshot, new HexCoord(4, 0)), Is.EqualTo(mobile));
        }

        [Test]
        public void FlyingAaCanReactButStoredAaCannot()
        {
            UnitData c = Carrier(); UnitData stored = Plane(); stored.Abilities.Add(UnitAbilities.AntiAir);
            c.CarrierFlightDeck.Members.Add(stored);
            UnitData escort = Plane(); escort.Abilities.Add(UnitAbilities.AntiAir); ArmyData escortWing = Wing(escort);
            var enemy = new PlayerSetupData();
            var target = new ArmyData { Owner = enemy, Hex = Home, IsAirArmy = true };
            target.Members.Add(new UnitData { Owner = enemy, IsAviation = true }); ArmyRegistry.Register(target);
            var reactions = AntiAirRules.CollectEntryReactions(target, Home);
            Assert.That(reactions.Any(r => r.AaUnit == escort && r.AaArmy == escortWing), Is.True);
            Assert.That(reactions.Any(r => r.AaUnit == stored), Is.False);
        }

        [Test]
        public void ParkedAircraftAreNotIndependentStrikeTargets()
        {
            UnitData c = Carrier(); c.CarrierAirfield.Members.Add(Plane());
            c.CarrierFlightDeck.Members.Add(Plane()); ArmyData flying = Wing(Plane());
            var targets = AviationCombatPresenter.FindAirStrikeTargetsAt(Home, new PlayerSetupData());
            Assert.That(targets, Has.Member(ArmyRegistry.FindArmyContaining(c)).And.Member(flying));
            Assert.That(targets, Has.No.Member(c.CarrierAirfield).And.No.Member(c.CarrierFlightDeck));
        }

        [Test]
        public void RemoteDeckTransfersAreRejectedWithoutChangingEitherRoster()
        {
            UnitData c = Carrier(), p = Plane(); ArmyData wing = Wing(p);
            ArmyRegistry.MoveArmy(wing, new HexCoord(2, 0));
            Assert.That(ArmyActions.TransferMember(p, wing, c.CarrierFlightDeck, null, out _), Is.False);
            Assert.That(ArmyActions.TransferMembersAtomic(new[] { p }, wing, c.CarrierFlightDeck,
                null, out _), Is.False);
            Assert.That(wing.Members, Does.Contain(p)); Assert.That(c.CarrierFlightDeck.Members, Is.Empty);
        }

        [TestCase(StrategicReservedResource.ActionPoints)]
        [TestCase(StrategicReservedResource.Human)]
        [TestCase(StrategicReservedResource.Energy)]
        [TestCase(StrategicReservedResource.Materials)]
        [TestCase(StrategicReservedResource.Tech)]
        public void FreeCarrierFormationNeitherSpendsNorRewritesBankClaims(StrategicReservedResource resource)
        {
            const int turn = 7;
            StrategicResourceReservationLedger.Upsert(_owner, turn, new StrategicResourceReservation
            {
                Owner = "other-operation", Reason = StrategicReservationReason.EconomyBuildCompletion,
                Resource = resource, Amount = 100, ExpirationStage = StrategicReservationExpiry.EndOfTurn,
            });
            // No PlayerRoot exists, and all hypothetical physical stock is held by another owner.
            // Container creation, forming and replacement after take-off must still be free.
            UnitData c = Carrier(), p = Plane(); c.CarrierAirfield.Members.Add(p);
            Assert.That(AviationWingPreparation.TryForm(_owner, null, c.CarrierAirfield, new[] { p },
                null, out ArmyData wing, out _), Is.True);
            AviationActions.DetachCarrierWing(wing);
            AviationActions.EnsureCarrierContainers(ArmyRegistry.FindArmyContaining(c));
            var claims = TurnResourceBook.LedgerClaims(_owner, turn);
            Assert.That(claims.Count, Is.EqualTo(1));
            Assert.That(claims[0].Owner, Is.EqualTo("other-operation"));
            Assert.That(claims[0].Amount, Is.EqualTo(100));
            Assert.That(TurnResourceBook.Free(100, claims, resource, default), Is.Zero);
            Assert.That(c.CarrierFlightDeck.Members, Is.Empty);
            Assert.That(ArmyRegistry.AllForOwner(_owner).Count(a => a.IsCarrierFlightDeck), Is.EqualTo(1));
        }

        [Test]
        public void CarrierRelocationPublishesInfrastructureDeltaWithoutMutatingOldSnapshot()
        {
            using var session = AiTurnSession.Begin(_owner, null, null, null, 4);
            UnitData c = Carrier(); ArmyData host = ArmyRegistry.FindArmyContaining(c);
            WorldSnapshot Read() => new WorldSnapshot { Observer = _owner, TurnNumber = 4,
                Self = new SelfSnapshot { AirfieldHexes = AviationRules.OwnedAirfieldHexes(_owner).ToArray(),
                    Armies = new[] { WorldAnalysis.ToArmySnapshot(host, _owner, true, 1, null) } } };
            WorldSnapshot before = Read(); var destination = new HexCoord(1, 0);
            ArmyRegistry.MoveArmy(host, destination); WorldSnapshot after = Read();
            WorldAnalysis.PublishStepObservationDelta(_owner, 4,
                new WorldAnalysis.StepObservationStamp(before, default, null),
                new WorldAnalysis.StepObservationStamp(after, default, null), null);
            Assert.That(before.Self.AirfieldHexes, Is.EqualTo(new[] { Home }));
            Assert.That(before.Self.Armies[0].Hex, Is.EqualTo(Home));
            Assert.That(after.Self.AirfieldHexes, Is.EqualTo(new[] { destination }));
            Assert.That(session.PendingInvalidations.Reasons.HasFlag(StrategicInvalidationReason.Infrastructure), Is.True);
            Assert.That(session.PendingInvalidations.Reasons.HasFlag(StrategicInvalidationReason.Capability), Is.True);
            Assert.That(session.PendingInvalidations.ActorIds, Does.Contain(host.Id));
        }

        [TestCase(0, 0, 0f)]
        [TestCase(2, 0, 1f)]
        [TestCase(2, 2, 0f)]
        public void CarrierSupportValueUsesActualAircraftDemandAndExistingDecks(int aircraft, int capacity, float expected)
        {
            var definition = new CardDefinition { cardType = CardType.Unit, airfieldCapacity = 2 };
            var plan = new MaterializationPlan { BaseCardInHand = new CardData(definition) };
            var snap = new WorldSnapshot { Self = new SelfSnapshot { Armies = new[] {
                new ArmySnapshot { IsAir = true, MemberCount = aircraft },
                new ArmySnapshot { MobileAirfieldCapacity = capacity } } } };
            Assert.That(new EffectEvaluationContext(snap, plan).MobileAirfieldService, Is.EqualTo(expected));
            Assert.That(StrategicEffectRegistry.Resolve(new[] { UnitAbilities.Airfield }, 0), Is.Not.Empty);
        }
    }
}
#endif
