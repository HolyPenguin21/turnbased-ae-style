#if UNITY_INCLUDE_TESTS
using System;
using System.Collections.Generic;
using System.Linq;
using Game.Aviation;
using Game.Cards;
using Game.Combat;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Setup;
using Game.Units;
using NUnit.Framework;
namespace Game.EditorTests
{
    public sealed class WorldMutationContractTests
    {
        private readonly HexCoord hex = new HexCoord(2, 2);
        private PlayerSetupData owner, capturer, stranger;
        [SetUp] public void SetUp()
        {
            ArmyRegistry.Clear(); BuildingRegistry.Clear(); VisionSystem.Clear(); VisionSystem.Configure(null); StealthSystem.Clear();
            owner = new PlayerSetupData { PlayerId = "old", IsHuman = true, Hand = new PlayerHandData(null, 0) };
            capturer = new PlayerSetupData { PlayerId = "new", IsHuman = true };
            stranger = new PlayerSetupData { PlayerId = "stranger", IsHuman = true };
        }
        [TearDown] public void TearDown()
        { ArmyRegistry.Clear(); BuildingRegistry.Clear(); VisionSystem.Clear(); StealthSystem.Clear(); }
        private ArmyData Army(PlayerSetupData player, params UnitData[] units)
        {
            var army = new ArmyData { Owner = player, Hex = hex }; army.Members.AddRange(units);
            using (MatchRestoreScope.Begin()) ArmyRegistry.Register(army); return army;
        }
        private UnitData Unit(bool air = false) => new UnitData { Owner = owner, IsAviation = air,
            HitPointsCurrent = 3, HitPointsMax = 3, OriginatingCard = new CardDefinition { displayName = "aircraft", cardType = CardType.Unit } };
        private BuildingData Building(bool isBase = true)
        {
            var building = new BuildingData { Hex = hex, Owner = owner, IsBase = isBase, AirfieldCapacity = 3 };
            using (MatchRestoreScope.Begin()) BuildingRegistry.Register(hex, building); return building;
        }
        [Test] public void RosterQueriesCannotMutateRegistry()
        {
            var army = Army(owner, Unit()); ArmyRegistry.AllAt(hex).Clear();
            Assert.That(ArmyRegistry.IsRegistered(army), Is.True);
            Assert.That(ArmyRegistry.AllAt(hex), Does.Contain(army));
        }
        [Test] public void ReadOnlyQueriesAndRostersRejectMutation()
        {
            var army = Army(owner, Unit());
            Assert.Throws<NotSupportedException>(() => ((IList<ArmyData>)ArmyRegistry.At(hex)).Clear());
            Assert.Throws<NotSupportedException>(() => ((IList<UnitData>)army.Roster).Clear());
            Assert.That(army.Members.Count, Is.EqualTo(1));
        }
        [Test] public void EmptyQueriesAreIndependent()
        {
            ArmyRegistry.AllAt(new HexCoord(90, 90)).Add(new ArmyData());
            Assert.That(ArmyRegistry.AllAt(new HexCoord(91, 91)), Is.Empty);
        }
        [Test] public void TransferCannotCrossOwnershipBoundary()
        {
            var member = Unit(); var source = Army(owner, member); var target = Army(capturer);
            Assert.That(ArmyActions.CanTransferMember(member, source, target, out _), Is.False);
            Assert.That(ArmyActions.CanTransferMembers(new[] { member }, source, target, out _), Is.False);
        }
        [Test] public void SingleAndBatchCannotReleasePrisoners()
        {
            var member = Unit(); member.IsPrisoner = true;
            var source = Army(owner, member); source.IsPrison = true; var target = Army(owner);
            Assert.That(ArmyActions.CanTransferMember(member, source, target, out _), Is.False);
            Assert.That(ArmyActions.CanTransferMembers(new[] { member }, source, target, out _), Is.False);
        }
        [Test] public void BatchRejectsDuplicateDisplacedMembers()
        {
            var first = Unit(); var second = Unit(); var source = Army(owner, first); var target = Army(owner, second);
            Assert.That(ArmyActions.CanExchangeMembers(new[] { first }, source, target, null, new[] { second, second }, out _), Is.False);
        }
        [Test] public void CaptureWithoutSelectionReturnsAircraftExactlyOnce()
        {
            var building = Building(); building.Abilities.Add(UnitAbilities.Barracks);
            var garrison = Army(owner); garrison.IsGarrison = true;
            var aircraft = Unit(true); var field = Army(owner, aircraft); field.IsAirfield = true;
            var equipment = new CardDefinition { cardType = CardType.Equipment }; aircraft.Equipment = equipment;
            BuildingRegistry.CaptureOrDestroy(building, capturer, null); BuildingRegistry.CaptureOrDestroy(building, capturer, null);
            Assert.That(garrison.Owner, Is.SameAs(capturer)); Assert.That(field.Members, Is.Empty);
            Assert.That(ArmyRegistry.IsRegistered(field), Is.False);
            Assert.That(owner.Hand.Hand.Count, Is.EqualTo(1)); Assert.That(owner.Hand.Hand[0].Equipment, Is.SameAs(equipment));
        }
        [Test] public void CapturedAirfieldBaseGivesCapturerAnAirfieldContainer()
        {
            var building = Building(); var field = Army(owner, Unit(true)); field.IsAirfield = true;
            BuildingRegistry.CaptureOrDestroy(building, capturer, null);
            ArmyData captured = AviationRules.FindAirfieldAt(hex, capturer);
            Assert.That(captured, Is.Not.Null); Assert.That(captured, Is.Not.SameAs(field));
            Assert.That(ArmyRegistry.IsRegistered(captured), Is.True); Assert.That(captured.Members, Is.Empty);
        }
        [Test] public void CaptureObserversSeeFinalRosterAndReturnedCards()
        {
            var building = Building(); var field = Army(owner, Unit(true)); field.IsAirfield = true; int calls = 0; bool final = false;
            Action<HexCoord, BuildingData> changed = (_, current) =>
            { calls++; final = field.Members.Count == 0 && owner.Hand.Hand.Count == 1 && current.Owner == capturer; };
            BuildingRegistry.VisualStateChanged += changed;
            try { BuildingRegistry.CaptureOrDestroy(building, capturer, null); }
            finally { BuildingRegistry.VisualStateChanged -= changed; }
            Assert.That(calls, Is.EqualTo(1)); Assert.That(final, Is.True);
        }
        [Test] public void DestructionWithoutSelectionReturnsAircraftAndCleansShell()
        {
            Building(false); var field = Army(owner, Unit(true)); field.IsAirfield = true;
            BuildingRegistry.Unregister(hex);
            Assert.That(owner.Hand.Hand.Count, Is.EqualTo(1)); Assert.That(ArmyRegistry.IsRegistered(field), Is.False);
        }
        [Test] public void CarrierRemovalReturnsHangarAndDeckWithoutSelection()
        {
            var carrier = Unit(); carrier.AirfieldCapacity = 2; var host = Army(owner, carrier);
            AviationActions.EnsureCarrierContainers(host);
            var hangar = carrier.CarrierAirfield; var deck = carrier.CarrierFlightDeck;
            hangar.Members.Add(Unit(true)); deck.Members.Add(Unit(true)); ArmyRegistry.Unregister(host);
            Assert.That(carrier.CarrierAirfield, Is.Null); Assert.That(carrier.CarrierFlightDeck, Is.Null);
            Assert.That(ArmyRegistry.IsRegistered(hangar), Is.False); Assert.That(ArmyRegistry.IsRegistered(deck), Is.False);
            Assert.That(owner.Hand.Hand.Count, Is.EqualTo(2));
        }
        [Test] public void CommanderReorderCannotReduceCapacityBelowRoster()
        {
            var strong = Unit(); var weak = Unit(); strong.IsHero = weak.IsHero = true;
            strong.CommandRating = 12; weak.CommandRating = 1;
            var army = Army(owner, strong, weak, Unit(), Unit(), Unit(), Unit());
            Assert.That(army.TryReorderCommander(weak, out _), Is.False); Assert.That(army.Members[0], Is.SameAs(strong));
        }
        [Test] public void CapturedBaseFormerOwnerWitnessesFinalHandoverBeforeVisionLoss()
        {
            var building = Building(); VisionSystem.RecomputeFor(owner); int seen = 0; bool witnessed = false;
            Action<HexCoord, BuildingData> changed = (_, current) =>
            { witnessed = VisionSystem.IsVisible(owner, hex) && !VisionSystem.IsVisible(stranger, hex) && current.Owner == capturer; seen++; };
            BuildingRegistry.VisualStateChanged += changed;
            try { BuildingRegistry.CaptureOrDestroy(building, capturer, null); }
            finally { BuildingRegistry.VisualStateChanged -= changed; }
            Assert.That(seen, Is.EqualTo(1)); Assert.That(witnessed, Is.True); Assert.That(VisionSystem.IsVisible(owner, hex), Is.False);
            Assert.That(HumanVisualMemory.IsBuildingKnown(owner, hex), Is.True); Assert.That(HumanVisualMemory.IsBuildingKnown(stranger, hex), Is.False);
        }
        [Test] public void RegistryRejectsDuplicateLiveUnitContainer()
        {
            var unit = Unit(); var original = Army(owner, unit); var duplicate = new ArmyData { Owner = owner, Hex = new HexCoord(7, 7) };
            duplicate.Members.Add(unit); Assert.Throws<InvalidOperationException>(() => ArmyRegistry.Register(duplicate));
            Assert.That(ArmyRegistry.FindArmyContaining(unit), Is.SameAs(original)); Assert.That(ArmyRegistry.IsRegistered(duplicate), Is.False);
        }
        [Test] public void MatchResetCancelsPendingWorldNotifications()
        {
            int calls = 0;
            using (VisionSystem.BeginMutation()) { VisionSystem.PublishAfterMutation(() => calls++); VisionSystem.Clear(); }
            Assert.That(calls, Is.Zero);
            using (VisionSystem.BeginMutation()) VisionSystem.PublishAfterMutation(() => calls++);
            Assert.That(calls, Is.EqualTo(1));
        }
        [Test] public void SubscriberExceptionCannotLeaveCapturedBaseVisionUncommitted()
        {
            var building = Building(); VisionSystem.RecomputeFor(owner);
            Action<HexCoord, BuildingData> broken = (_, __) => throw new InvalidOperationException("view failed");
            BuildingRegistry.VisualStateChanged += broken;
#if UNITY_EDITOR
            UnityEngine.TestTools.LogAssert.Expect(UnityEngine.LogType.Exception, new System.Text.RegularExpressions.Regex("view failed"));
#endif
            try { Assert.DoesNotThrow(() => BuildingRegistry.CaptureOrDestroy(building, capturer, null)); }
            finally { BuildingRegistry.VisualStateChanged -= broken; }
            Assert.That(building.Owner, Is.SameAs(capturer)); Assert.That(VisionSystem.IsVisible(owner, hex), Is.False);
            Assert.That(VisionSystem.IsVisible(capturer, hex), Is.True);
        }
        [Test] public void TransferPublishesOnlyReconciledCarrierReferences()
        {
            var carrier = Unit(); carrier.AirfieldCapacity = 2; var source = Army(owner, carrier); var target = Army(owner);
            AviationActions.EnsureCarrierContainers(source); int calls = 0; bool final = false;
            VisionSystem.RestoreState(new VisionState { Players = new List<PlayerVisionState> { new PlayerVisionState { PlayerId = stranger.PlayerId,
                Visible = new List<HexCoord> { hex } } } }, id => id == stranger.PlayerId ? stranger : owner);
            Action<PlayerSetupData, HexCoord> changed = (viewer, _) =>
            { if (viewer != stranger) return; calls++; final = source.Members.Count == 0 && ArmyRegistry.FindArmyContaining(carrier) == target
                && carrier.CarrierAirfield.Hex.Equals(target.Hex) && carrier.CarrierAirfield.Owner == target.Owner; };
            VisionSystem.VisibleContentChanged += changed;
            try { Assert.That(ArmyActions.TransferMember(carrier, source, target, null, out _), Is.True); }
            finally { VisionSystem.VisibleContentChanged -= changed; }
            Assert.That(calls, Is.EqualTo(1)); Assert.That(final, Is.True);
        }
        [Test] public void BadSubscriberDoesNotStarveLaterWitnesses()
        {
            var building = Building(); int calls = 0;
            Action<HexCoord, BuildingData> broken = (_, __) => throw new InvalidOperationException("view failed");
            Action<HexCoord, BuildingData> later = (_, current) => { calls++; Assert.That(current.Owner, Is.SameAs(capturer)); };
            BuildingRegistry.VisualStateChanged += broken; BuildingRegistry.VisualStateChanged += later;
#if UNITY_EDITOR
            UnityEngine.TestTools.LogAssert.Expect(UnityEngine.LogType.Exception, new System.Text.RegularExpressions.Regex("view failed"));
#endif
            try { Assert.DoesNotThrow(() => BuildingRegistry.CaptureOrDestroy(building, capturer, null)); }
            finally { BuildingRegistry.VisualStateChanged -= broken; BuildingRegistry.VisualStateChanged -= later; }
            Assert.That(calls, Is.EqualTo(1));
        }
        [Test] public void RegistryRejectsProjectionUnitsAndDuplicateArmyIdentities()
        {
            var projection = UnitData.CreateProjection(); projection.Owner = owner;
            var candidate = new ArmyData { Owner = owner, Hex = hex }; candidate.Members.Add(projection);
            Assert.Throws<InvalidOperationException>(() => ArmyRegistry.Register(candidate));
            var original = Army(owner); var duplicate = ArmyData.WithIdentity(original.Id); duplicate.Owner = owner; duplicate.Hex = hex;
            Assert.Throws<InvalidOperationException>(() => ArmyRegistry.Register(duplicate));
        }
        [Test] public void LaunchCannotMaterializeSnapshotAircraft()
        {
            var snapshot = ArmyData.CreateVisualSnapshot(); snapshot.Owner = owner; snapshot.Hex = hex; snapshot.IsAirfield = true;
            var aircraft = Unit(true); snapshot.Members.Add(aircraft);
            Assert.That(AviationActions.TryLaunch(snapshot, new[] { aircraft }, null, null, out _, out _), Is.False);
            Assert.That(snapshot.Members, Does.Contain(aircraft)); Assert.That(ArmyRegistry.AllOccupiedHexes(), Is.Empty);
        }
        [Test] public void WingPreparationCannotMutateSnapshots()
        {
            var ground = ArmyData.CreateVisualSnapshot(); ground.Owner = owner; ground.Hex = hex; ground.Members.Add(Unit());
            var garrison = ArmyData.CreateVisualSnapshot(); garrison.Owner = owner; garrison.Hex = hex; garrison.IsGarrison = true;
            var field = ArmyData.CreateVisualSnapshot(); field.Owner = owner; field.Hex = hex; field.IsAirfield = true;
            var aircraft = Unit(true); field.Members.Add(aircraft);
            Assert.That(ArmyActions.TryUnloadAndBoardAircraft(ground, garrison, field, new[] { aircraft }, null, out _), Is.False);
            Assert.That(ground.Members[0].IsAviation, Is.False); Assert.That(garrison.Members, Is.Empty);
        }

        [Test] public void DetectionRollbackRestoresKnowledgeAndQueuedNoticesWithoutSaveIds()
        {
            owner.PlayerId = null; // compensation is a gameplay boundary, not a save codec
            var hidden = Unit(); hidden.Owner = capturer; hidden.IsHidden = true;
            Army(capturer, hidden);
            var building = new BuildingData { Owner = owner, Hex = hex }; building.Abilities.Add("r1s4");
            var rollback = StealthSystem.CaptureDetectionRollback();
            var previousRoller = StealthSystem.ChallengeRoller;
            try
            {
                StealthSystem.ChallengeRoller = (_, __) => new ChallengeResult(new[] { true }, Array.Empty<bool>());
                StealthSystem.RunChecksForNewVisionSource(building);
                Assert.That(StealthSystem.IsDetectedBy(hidden, owner), Is.True);
                rollback();
                Assert.That(StealthSystem.IsDetectedBy(hidden, owner), Is.False);
                Assert.That(StealthSystem.TakeDetectionNotices(owner), Is.Empty);
            }
            finally { StealthSystem.ChallengeRoller = previousRoller; }
        }
        [Test] public void BlastCompletionPublishesAfterDeadCarrierCargoReturns()
        {
            var carrier = Unit(); carrier.AirfieldCapacity = 2; var host = Army(owner, carrier);
            AviationActions.EnsureCarrierContainers(host); carrier.CarrierAirfield.Members.Add(Unit(true));
            var launcher = Unit(); launcher.Owner = capturer; launcher.Abilities.Add(UnitAbilities.Explosion10);
            var attacker = Army(capturer, launcher);
            var blast = new ExplosionAttack(launcher, hex, new Random(4), attacker, host);
            int calls = 0; bool sawDanglingContainer = false;
            VisionSystem.RestoreState(new VisionState { Players = new List<PlayerVisionState> { new PlayerVisionState {
                PlayerId = stranger.PlayerId, Visible = new List<HexCoord> { hex } } } }, _ => stranger);
            Action<PlayerSetupData, HexCoord> changed = (viewer, _) =>
            {
                if (viewer != stranger) return;
                calls++;
                sawDanglingContainer |= carrier.CarrierAirfield != null || carrier.CarrierFlightDeck != null;
            };
            VisionSystem.VisibleContentChanged += changed;
            try
            {
                var target = blast.Targets.Single(t => t.Unit == carrier);
                blast.Resolve(target, carrier, new BattleChallengeRollResult(Enumerable.Repeat(true, 10).ToArray(), Array.Empty<bool>(), 0, 0), AbilityMagnitudes.Default);
                Assert.That(calls, Is.Zero, "the pending blast has not completed carrier cleanup");
                blast.Complete(); blast.Complete();
                Assert.That(calls, Is.EqualTo(1)); Assert.That(sawDanglingContainer, Is.False);
                Assert.That(owner.Hand.Hand.Count, Is.EqualTo(1)); Assert.That(ArmyRegistry.IsRegistered(host), Is.False);
            }
            finally { VisionSystem.VisibleContentChanged -= changed; }
        }
        [Test] public void FinalizationObserversNeverSeeAnIndexedEmptyLoser()
        {
            var attacker = Army(owner, Unit()); var defender = Army(capturer);
            var state = new BattleState(hex, attacker, defender, null, 1);
            VisionSystem.RestoreState(new VisionState { Players = new List<PlayerVisionState> { new PlayerVisionState {
                PlayerId = stranger.PlayerId, Visible = new List<HexCoord> { hex } } } }, _ => stranger);
            int calls = 0; bool sawLoser = false;
            Action<PlayerSetupData, HexCoord> changed = (viewer, _) =>
            { if (viewer == stranger) { calls++; sawLoser |= ArmyRegistry.IsRegistered(defender); } };
            VisionSystem.VisibleContentChanged += changed;
            try { BattleEngine.FinalizeEncounter(state, null); }
            finally { VisionSystem.VisibleContentChanged -= changed; }
            Assert.That(calls, Is.EqualTo(1)); Assert.That(sawLoser, Is.False);
        }

        [Test] public void FullHandDoesNotPublishASuccessfulAircraftReturn()
        {
            owner.Hand.SetCapacity(1); owner.Hand.TryAddCard(new CardData(new CardDefinition()));
            var field = Army(owner, Unit(true)); field.IsAirfield = true;
            int returns = 0; Action<PlayerSetupData, CardData> returned = (_, __) => returns++;
            AviationActions.AircraftReturnedToHand += returned;
            try { AviationActions.ReturnAircraftToDeck(field, null); }
            finally { AviationActions.AircraftReturnedToHand -= returned; }
            Assert.That(owner.Hand.Hand.Count, Is.EqualTo(1), "the existing ordinary-card capacity remains unchanged");
            Assert.That(returns, Is.Zero); Assert.That(field.Members, Is.Empty);
        }

        [Test] public void RegistrationRejectsDuplicateRuntimeIdsWithinTheNewRoster()
        {
            var first = Unit(); var duplicate = UnitData.CreateProjection(first.RuntimeId); duplicate.Owner = owner;
            var candidate = new ArmyData { Owner = owner, Hex = hex };
            candidate.Members.Add(first); candidate.Members.Add(duplicate);
            Assert.Throws<InvalidOperationException>(() => ArmyRegistry.Register(candidate));
            Assert.That(ArmyRegistry.At(hex), Is.Empty);
        }
        [Test] public void BatchTransferAcceptsTheSourceRosterAsItsReadOnlyInput()
        {
            var first = Unit(); var second = Unit(); var source = Army(owner, first, second); var target = Army(owner);
            bool transferred = false;
            Assert.DoesNotThrow(() => transferred = ArmyActions.TransferMembersAtomic(source.Members, source, target, null, out _));
            Assert.That(transferred, Is.True); Assert.That(source.Members, Is.Empty);
            Assert.That(target.Members, Is.EqualTo(new[] { first, second }));
        }
        [Test] public void PrisonReleasePublishesTheFinalCanonicalTransfer()
        {
            var hero = Unit(); hero.IsHero = true; hero.CommandRating = 4; hero.IsPrisoner = true; hero.Owner = capturer; hero.CapturedFrom = owner;
            var prison = Army(capturer, hero); prison.IsPrison = true;
            var home = new HexCoord(8, 8); owner.CitadelHexQ = home.Q; owner.CitadelHexR = home.R;
            var garrison = Army(owner); garrison.IsGarrison = true;
            using (MatchRestoreScope.Begin()) ArmyRegistry.MoveArmy(garrison, home);
            VisionSystem.RecomputeFor(capturer);
            int calls = 0; bool final = false;
            Action<PlayerSetupData, HexCoord> changed = (viewer, at) =>
            {
                if (viewer != owner || !at.Equals(home)) return;
                calls++; final = hero.Owner == owner && !hero.IsPrisoner && hero.CapturedFrom == null
                    && prison.Members.Count == 0 && ArmyRegistry.FindArmyContaining(hero) == garrison;
            };
            VisionSystem.VisibleContentChanged += changed;
            try
            {
                ArmyActions.ReleasePrisoners(capturer, hex);
            }
            finally { VisionSystem.VisibleContentChanged -= changed; }
            Assert.That(calls, Is.EqualTo(1)); Assert.That(final, Is.True);
        }

        [Test] public void CarrierDeckFormationPublishesTheCompletedRoster()
        {
            var carrier = Unit(); carrier.AirfieldCapacity = 2; var host = Army(owner, carrier);
            AviationActions.EnsureCarrierContainers(host);
            var aircraft = Unit(true); carrier.CarrierAirfield.Members.Add(aircraft);
            int calls = 0; bool final = false;
            Action<PlayerSetupData, HexCoord> changed = (viewer, at) =>
            {
                if (viewer != owner || !at.Equals(hex)) return;
                calls++; final = carrier.CarrierAirfield.Members.Count == 0
                    && carrier.CarrierFlightDeck.Members.SequenceEqual(new[] { aircraft });
            };
            VisionSystem.VisibleContentChanged += changed;
            try { Assert.That(AviationActions.TryLaunch(carrier.CarrierAirfield, new[] { aircraft }, null, null, out _, out _), Is.True); }
            finally { VisionSystem.VisibleContentChanged -= changed; }
            Assert.That(calls, Is.EqualTo(1)); Assert.That(final, Is.True);
        }
        [Test] public void WingPreparationAcceptsTheAirfieldRosterAsItsReadOnlyInput()
        {
            var ground = Army(owner, Unit()); var garrison = Army(owner); garrison.IsGarrison = true;
            var first = Unit(true); var second = Unit(true); var field = Army(owner, first, second); field.IsAirfield = true;
            bool prepared = false;
            Assert.DoesNotThrow(() => prepared = ArmyActions.TryUnloadAndBoardAircraft(ground, garrison, field, field.Members, null, out _));
            Assert.That(prepared, Is.True); Assert.That(field.Members, Is.Empty);
            Assert.That(ground.Members, Is.EqualTo(new[] { first, second })); Assert.That(garrison.Members.Count, Is.EqualTo(1));
        }
        [Test] public void BattleSummonsPublishOnlyAfterRosterAndGridAgree()
        {
            var summoner = Unit(); summoner.Abilities.Add(UnitAbilities.RaiseTheRots);
            var attacker = Army(owner, summoner); var defender = Army(capturer, new UnitData { Owner = capturer });
            var grid = BattleGrid.FromArmies(attacker, defender);
            var state = new BattleState(hex, attacker, defender, grid, 1);
            var engine = new BattleEngine(state, AbilityMagnitudes.Default);
            int calls = 0; bool final = false;
            Action<PlayerSetupData, HexCoord> changed = (viewer, at) =>
            {
                if (viewer != owner || !at.Equals(hex)) return;
                calls++; final = attacker.Members.Count == 2
                    && grid.TryFindPosition(attacker.Members.Single(u => u.IsSummoned), out _, out _);
            };
            VisionSystem.RecomputeFor(owner); VisionSystem.VisibleContentChanged += changed;
            try { engine.SpawnBattleSummons(attacker, BattleGrid.AttackerFrontRow, BattleGrid.AttackerBackRow,
                    new CardDefinition(), 1, (_, __) => Unit()); }
            finally { VisionSystem.VisibleContentChanged -= changed; }
            Assert.That(calls, Is.EqualTo(1)); Assert.That(final, Is.True);
        }

        [Test] public void EndTurnSweepRemovesTravellingShellsAndRetainsPermanentContainers()
        {
            var travelling = Army(owner); travelling.Hex = new HexCoord(9, 9);
            var prison = Army(owner); prison.IsPrison = true;
            var building = Building(); building.Abilities.Add(UnitAbilities.Barracks);
            var garrison = Army(owner); garrison.IsGarrison = true;
            var fixedField = Army(owner); fixedField.IsAirfield = true;
            ArmyActions.DisbandEmptyArmiesOf(owner);
            Assert.That(ArmyRegistry.IsRegistered(travelling), Is.False);
            Assert.That(ArmyRegistry.IsRegistered(prison), Is.True);
            Assert.That(ArmyRegistry.IsRegistered(garrison), Is.True);
            Assert.That(ArmyRegistry.IsRegistered(fixedField), Is.True);
        }
        [Test] public void PrisonReleaseKeepsAHeroWhoseHomeGarrisonHasNoCapacity()
        {
            owner.CitadelHexQ = hex.Q; owner.CitadelHexR = hex.R;
            var garrison = Army(owner, Unit(), Unit(), Unit(), Unit()); garrison.IsGarrison = true;
            var hero = Unit(); hero.IsHero = true; hero.CommandRating = 1; hero.IsPrisoner = true;
            hero.Owner = capturer; hero.CapturedFrom = owner;
            var prison = Army(capturer, hero); prison.IsPrison = true;
            ArmyActions.ReleasePrisoners(capturer, hex);
            Assert.That(ArmyRegistry.FindArmyContaining(hero), Is.SameAs(prison));
            Assert.That(hero.Owner, Is.SameAs(capturer)); Assert.That(hero.IsPrisoner, Is.True);
            Assert.That(garrison.Members.Count, Is.EqualTo(4));
        }
        [Test] public void ActivationCannotCommitOnADisplaySnapshot()
        {
            var snapshot = ArmyData.CreateVisualSnapshot(); snapshot.Owner = owner;
            snapshot.MarkActivated();
            Assert.That(ArmyActions.TryPayActivation(snapshot, null), Is.False);
        }

        [Test] public void SilentRestoreRelocationPublishesNothing()
        {
            var army = Army(owner, Unit()); int calls = 0; Action<PlayerSetupData> changed = _ => calls++;
            VisionSystem.VisibilityChanged += changed;
            try { using (MatchRestoreScope.Begin()) ArmyRegistry.MoveArmy(army, new HexCoord(4, 4)); }
            finally { VisionSystem.VisibilityChanged -= changed; }
            Assert.That(calls, Is.Zero); Assert.That(ArmyRegistry.AllAt(new HexCoord(4, 4)), Does.Contain(army));
        }

        [TestCase(false)] [TestCase(true)]
        public void TransferRejectsUnregisteredEndpointWithoutLosingCanonicalMember(bool removeSource)
        {
            var unit = Unit(); var source = Army(owner, unit); var target = Army(owner);
            ArmyRegistry.Unregister(removeSource ? source : target);
            var beforeSource = source.Members.ToArray(); var beforeTarget = target.Members.ToArray();
            bool changed = ArmyActions.TransferMember(unit, source, target, null, out _);
            Assert.That(changed, Is.False);
            Assert.That(source.Members, Is.EqualTo(beforeSource)); Assert.That(target.Members, Is.EqualTo(beforeTarget));
            Assert.That(ArmyRegistry.FindArmyContaining(unit), Is.SameAs(removeSource ? null : source));
        }
    }
}
#endif
