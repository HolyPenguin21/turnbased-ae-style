#if UNITY_INCLUDE_TESTS
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Game.Aviation;
using Game.Cards;
using Game.Core;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Terrain;
using Game.UI;
using Game.Units;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;
namespace Game.EditorTests
{
    [Category("UnityNative")]
    public sealed class WorldMutationUnityTests
    {
        private GameObject scene, marker;
        private GameConfig config;
        private StartingDeckCatalog decks;
        private FactionCardCatalog catalog;
        private HexSelectionController selection;
        private PlayerSetupData owner;
        private PlayerRoot root;
        private ArmyData source;
        private UnitData hero;
        private readonly HexCoord hex = new HexCoord(1, 1);
        private static void Bind(object target, string field, object value) => target.GetType()
            .GetField(field, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(target, value);
        [SetUp] public void SetUp()
        {
            VisionSystem.Clear(); VisionSystem.Configure(null); ArmyRegistry.Clear(); BuildingRegistry.Clear(); PlayerRootRegistry.Clear(); StealthSystem.Clear();
            owner = new PlayerSetupData { IsHuman = true, PlayerId = "human", Hand = new PlayerHandData(null, 0) };
            root = PlayerRoot.Create(owner, "mutation-test-owner"); root.ActionPoints = 20; PlayerRootRegistry.Register(owner, root);
            scene = new GameObject("mutation-test-scene"); scene.SetActive(false);
            var map = scene.AddComponent<HexMap>(); map.SetData(3, 1f, new Dictionary<HexCoord, TerrainTypeEntry> { [hex] = new TerrainTypeEntry() });
            selection = scene.AddComponent<HexSelectionController>(); config = ScriptableObject.CreateInstance<GameConfig>();
            decks = ScriptableObject.CreateInstance<StartingDeckCatalog>(); catalog = ScriptableObject.CreateInstance<FactionCardCatalog>(); catalog.faction = owner.Faction;
            marker = new GameObject("mutation-test-marker"); marker.SetActive(false); catalog.basePrefab = marker.AddComponent<MapObjectVisual>(); decks.catalogs.Add(catalog);
            var handObject = new GameObject("inactive-hand"); handObject.SetActive(false); handObject.transform.SetParent(scene.transform);
            var hand = handObject.AddComponent<CardHandUI>(); Bind(hand, "startingDeckCatalog", decks);
            Bind(selection, "map", map); Bind(selection, "gameConfig", config); Bind(selection, "cardHandUI", hand);
            hero = new UnitData { Owner = owner, IsHero = true, CommandRating = 12, HitPointsCurrent = 4, MoveMax = 3, MoveCurrent = 3 };
            source = new ArmyData { Owner = owner, Hex = hex }; source.Members.Add(hero); ArmyRegistry.Register(source);
        }
        [TearDown] public void TearDown()
        {
            VisionSystem.Clear(); ArmyRegistry.Clear(); BuildingRegistry.Clear(); PlayerRootRegistry.Clear(); StealthSystem.Clear();
            if (scene != null) Object.DestroyImmediate(scene);
            if (root != null) Object.DestroyImmediate(root.gameObject);
            if (marker != null) Object.DestroyImmediate(marker);
            Object.DestroyImmediate(config); Object.DestroyImmediate(decks); Object.DestroyImmediate(catalog);
        }
        private CardDefinition BaseCard()
        {
            var card = new CardDefinition { displayName = "Base", cardType = CardType.Base, hitPoints = 4, airfieldCapacity = 2 };
            card.grantedAbilities.Add(UnitAbilities.Barracks); return card;
        }
        [TestCase(false)] [TestCase(true)]
        public void FailedFoundingCompensatesRosterAndNewContainersWithoutPublishingSuccess(bool addCarrier)
        {
            var armies = ArmyRegistry.AllForOwner(owner).ToArray(); int notifications = 0, founded = 0, destroyed = 0;
            bool transferred = false, deployed = false;
            Action<PlayerSetupData, HexCoord> changed = (_, __) => notifications++;
            Action<BuildingData> built = _ => founded++; Action<BuildingData> removed = _ => destroyed++;
            VisionSystem.VisibleContentChanged += changed; InfrastructureActions.BaseFounded += built; BuildingRegistry.BuildingDestroyed += removed;
            try
            {
                var result = InfrastructureActions.TryFoundBase(selection, BaseCard(), hex, owner, 2, new ResourceCost(), building =>
                {
                    var garrison = ArmyRegistry.FindGarrisonAt(hex, owner);
                    transferred = ArmyActions.TransferMember(hero, source, garrison, null, out _);
                    if (addCarrier)
                    {
                        var carrier = new CardDefinition { displayName = "carrier", cardType = CardType.Unit,
                            hitPoints = 5, airfieldCapacity = 2, requiredBuildingAbility = UnitAbilities.Barracks };
                        deployed = ArmyActions.DeployUnitFromCard(carrier, owner, garrison, root, selection, out _);
                    }
                    return false;
                });
                Assert.That(transferred, Is.True); Assert.That(deployed, Is.EqualTo(addCarrier));
                Assert.That(result.Ok, Is.False); Assert.That(root.ActionPoints, Is.EqualTo(20)); Assert.That(source.Members, Is.EqualTo(new[] { hero }));
                Assert.That(ArmyRegistry.AllForOwner(owner), Is.EquivalentTo(armies)); Assert.That(BuildingRegistry.FindAt(hex), Is.Null);
                Assert.That(notifications + founded + destroyed, Is.Zero);
            }
            finally { VisionSystem.VisibleContentChanged -= changed; InfrastructureActions.BaseFounded -= built; BuildingRegistry.BuildingDestroyed -= removed; }
        }
        [Test] public void FoundingObserversSeeCompletedGarrisonAndFinalPayment()
        {
            int calls = 0; bool final = false;
            Action<PlayerSetupData, HexCoord> changed = (_, at) =>
            { if (!at.Equals(hex)) return; calls++; final = ArmyRegistry.FindGarrisonAt(hex, owner).Members.Contains(hero)
                && source.Members.Count == 0 && root.ActionPoints == 18; };
            VisionSystem.VisibleContentChanged += changed;
            try
            {
                var result = InfrastructureActions.TryFoundBase(selection, BaseCard(), hex, owner, 2, new ResourceCost(),
                    building => ArmyActions.TransferMember(hero, source, ArmyRegistry.FindGarrisonAt(hex, owner), null, out _));
                Assert.That(result.Ok, Is.True); Assert.That(calls, Is.EqualTo(1)); Assert.That(final, Is.True);
            }
            finally { VisionSystem.VisibleContentChanged -= changed; }
        }
        [TestCase(false)] [TestCase(true)]
        public void DeclinedFoundingCannotRetainDetectionFromBuildingOrCompletion(bool detectDuringCompletion)
        {
            var enemy = new PlayerSetupData { PlayerId = "enemy" };
            var hidden = new UnitData { Owner = enemy, IsHidden = true, HitPointsCurrent = 4 };
            var enemyArmy = new ArmyData { Owner = enemy, Hex = hex }; enemyArmy.Members.Add(hidden); ArmyRegistry.Register(enemyArmy);
            var card = BaseCard(); card.grantedAbilities.Add("r1s4");
            var originalRoller = StealthSystem.ChallengeRoller;
            bool detectedInsideCompletion = false;
            try
            {
                StealthSystem.ChallengeRoller = (_, __) => new Game.Combat.ChallengeResult(new[] { true }, Array.Empty<bool>());
                var result = InfrastructureActions.TryFoundBase(selection, card, hex, owner, 2, new ResourceCost(), building =>
                {
                    if (detectDuringCompletion)
                    {
                        StealthSystem.RunChecksForNewVisionSource(building);
                        detectedInsideCompletion = StealthSystem.IsDetectedBy(hidden, owner);
                    }
                    return false;
                });
                Assert.That(result.Ok, Is.False); Assert.That(root.ActionPoints, Is.EqualTo(20));
                Assert.That(detectedInsideCompletion, Is.EqualTo(detectDuringCompletion));
                Assert.That(StealthSystem.IsDetectedBy(hidden, owner), Is.False);
                Assert.That(StealthSystem.TakeDetectionNotices(owner), Is.Empty);
            }
            finally { StealthSystem.ChallengeRoller = originalRoller; }
        }
        [Test] public void FailedFoundingRestoresAUnitTransferredFromAnotherIndexedHex()
        {
            var unit = new UnitData { Owner = owner, HitPointsCurrent = 4 };
            var remote = new ArmyData { Owner = owner, Hex = new HexCoord(2, 1) }; remote.Members.Add(unit); ArmyRegistry.Register(remote);
            bool transferred = false;
            var result = InfrastructureActions.TryFoundBase(selection, BaseCard(), hex, owner, 2, new ResourceCost(), building =>
            {
                transferred = ArmyActions.TransferMember(unit, remote, ArmyRegistry.FindGarrisonAt(hex, owner), null, out _);
                return false;
            });
            Assert.That(transferred, Is.True); Assert.That(result.Ok, Is.False); Assert.That(root.ActionPoints, Is.EqualTo(20));
            Assert.That(remote.Members, Is.EqualTo(new[] { unit })); Assert.That(ArmyRegistry.FindArmyContaining(unit), Is.SameAs(remote));
            Assert.That(BuildingRegistry.FindAt(hex), Is.Null);
        }

        [Test] public void FailedFoundingRestoresRelocatedCarrierContainers()
        {
            var carrier = new UnitData { Owner = owner, HitPointsCurrent = 4, AirfieldCapacity = 2 };
            var remote = new ArmyData { Owner = owner, Hex = new HexCoord(2, 1) }; remote.Members.Add(carrier); ArmyRegistry.Register(remote);
            var hangar = carrier.CarrierAirfield; var deck = carrier.CarrierFlightDeck;
            var cargo = new UnitData { Owner = owner, IsAviation = true }; hangar.Members.Add(cargo);
            bool transferred = false;
            var result = InfrastructureActions.TryFoundBase(selection, BaseCard(), hex, owner, 2, new ResourceCost(), building =>
            {
                transferred = ArmyActions.TransferMember(carrier, remote, ArmyRegistry.FindGarrisonAt(hex, owner), null, out _);
                return false;
            });
            Assert.That(transferred, Is.True); Assert.That(result.Ok, Is.False);
            Assert.That(ArmyRegistry.FindArmyContaining(carrier), Is.SameAs(remote));
            Assert.That(carrier.CarrierAirfield, Is.SameAs(hangar)); Assert.That(carrier.CarrierFlightDeck, Is.SameAs(deck));
            Assert.That(hangar.Hex, Is.EqualTo(new HexCoord(2, 1))); Assert.That(deck.Hex, Is.EqualTo(new HexCoord(2, 1)));
            Assert.That(ArmyRegistry.At(hangar.Hex), Does.Contain(hangar)); Assert.That(hangar.Members, Is.EqualTo(new[] { cargo }));
            Assert.That(owner.Hand.Hand, Is.Empty); Assert.That(root.ActionPoints, Is.EqualTo(20));
        }
        [Test] public void FailedFoundingRestoresAircraftSortieAndWingStateAfterLanding()
        {
            var aircraft = new UnitData { Owner = owner, IsAviation = true, SortieLaunchPaid = true,
                ConsecutiveUnlandedEnds = 3, HasEmergencyFlightPenalty = true };
            var wing = new ArmyData { Owner = owner, Hex = hex, IsAirArmy = true }; wing.Members.Add(aircraft); ArmyRegistry.Register(wing);
            bool landed = false;
            var result = InfrastructureActions.TryFoundBase(selection, BaseCard(), hex, owner, 2, new ResourceCost(), building =>
            {
                landed = ArmyActions.TransferMember(aircraft, wing, AviationRules.FindAirfieldAt(hex, owner), null, out _);
                return false;
            });
            Assert.That(landed, Is.True); Assert.That(result.Ok, Is.False);
            Assert.That(ArmyRegistry.FindArmyContaining(aircraft), Is.SameAs(wing)); Assert.That(wing.IsAirArmy, Is.True);
            Assert.That(aircraft.SortieLaunchPaid, Is.True); Assert.That(aircraft.ConsecutiveUnlandedEnds, Is.EqualTo(3));
            Assert.That(aircraft.HasEmergencyFlightPenalty, Is.True); Assert.That(BuildingRegistry.FindAt(hex), Is.Null);
        }

        [Test] public void BrokenObserverCannotLeaveBuiltBaseAndUnconsumedSourceCard()
        {
            var card = new CardData(BaseCard()); owner.Hand.TryAddCard(card);
            Action<BuildingData> broken = _ => throw new InvalidOperationException("view failed");
            InfrastructureActions.BaseFounded += broken;
#if UNITY_EDITOR
            UnityEngine.TestTools.LogAssert.Expect(LogType.Exception, new System.Text.RegularExpressions.Regex("view failed"));
#endif
            try
            {
                var result = InfrastructureActions.TryFoundBase(selection, card.Definition, hex, owner, 2, new ResourceCost());
                if (result.Ok) owner.Hand.RemoveCard(card); // same completion boundary as CardHandUI
                Assert.That(result.Ok, Is.True); Assert.That(owner.Hand.Hand, Is.Empty);
                Assert.That(BuildingRegistry.FindAt(hex), Is.SameAs(result.Building));
                Assert.That(root.ActionPoints, Is.EqualTo(18));
            }
            finally { InfrastructureActions.BaseFounded -= broken; }
        }

        [Test] public void SuccessfulFoundingStillDetectsAHiddenResidentAtCommit()
        {
            var enemy = new PlayerSetupData { PlayerId = "enemy" };
            var hidden = new UnitData { Owner = enemy, IsHidden = true, HitPointsCurrent = 4 };
            var enemyArmy = new ArmyData { Owner = enemy, Hex = hex }; enemyArmy.Members.Add(hidden); ArmyRegistry.Register(enemyArmy);
            var card = BaseCard(); card.grantedAbilities.Add("r1s4");
            var previous = StealthSystem.ChallengeRoller;
            try
            {
                StealthSystem.ChallengeRoller = (_, __) => new Game.Combat.ChallengeResult(new[] { true }, Array.Empty<bool>());
                var result = InfrastructureActions.TryFoundBase(selection, card, hex, owner, 2, new ResourceCost(), _ => true);
                Assert.That(result.Ok, Is.True); Assert.That(StealthSystem.IsDetectedBy(hidden, owner), Is.True);
                Assert.That(StealthSystem.TakeDetectionNotices(owner), Is.Not.Empty);
            }
            finally { StealthSystem.ChallengeRoller = previous; }
        }

        [Test] public void PaidCreationAndActivationPublishOnlyFinalState()
        {
            int calls = 0; bool sawIncompleteCreation = false;
            Action changed = () => { calls++; sawIncompleteCreation |= ArmyRegistry.AllForOwner(owner).Count() != 2; };
            root.ResourcesChanged += changed;
            try { Assert.That(ArmyActions.CreateArmy(owner, hex, catalog, selection), Is.Not.Null); }
            finally { root.ResourcesChanged -= changed; }
            Assert.That(calls, Is.EqualTo(1)); Assert.That(sawIncompleteCreation, Is.False);
            hero.ActivationApCost = 2; bool sawUnpaidFlags = false;
            changed = () => sawUnpaidFlags |= !source.HasActivatedThisTurn || !source.HasActivationCoverageFor(hero);
            root.ResourcesChanged += changed;
            try { Assert.That(ArmyActions.TryPayActivation(source, root), Is.True); }
            finally { root.ResourcesChanged -= changed; }
            Assert.That(sawUnpaidFlags, Is.False);
        }
        [Test] public void SplitRejectsDisplaySnapshotWithoutChargingOrRegisteringCopies()
        {
            var snapshot = ArmyData.CreateVisualSnapshot(); snapshot.Owner = owner; snapshot.Hex = hex;
            snapshot.Members.Add(hero.CloneForCombat());
            Assert.That(ArmyActions.CreateArmyWithMembers(owner, hex, catalog, snapshot, snapshot.Members, selection, out _), Is.Null);
            Assert.That(root.ActionPoints, Is.EqualTo(20)); Assert.That(ArmyRegistry.AllForOwner(owner).Count(), Is.EqualTo(1));
        }
        [Test] public void AirStrikeObserversSeeBothSingleUseLauncherAndCasualtyCleanedUp()
        {
            var enemy = new PlayerSetupData { PlayerId = "enemy", IsHuman = true };
            var aircraft = new UnitData { Owner = owner, IsAviation = true, Attack = 100, HitPointsCurrent = 3, HitPointsMax = 3 };
            aircraft.Abilities.Add(UnitAbilities.SingleUse);
            var wing = new ArmyData { Owner = owner, Hex = hex, IsAirArmy = true }; wing.Members.Add(aircraft); ArmyRegistry.Register(wing);
            var victim = new UnitData { Owner = enemy, HitPointsCurrent = 1, HitPointsMax = 1 };
            var target = new ArmyData { Owner = enemy, Hex = hex }; target.Members.Add(victim); ArmyRegistry.Register(target);
            var presenter = scene.AddComponent<AviationCombatPresenter>();
            var viewer = new PlayerSetupData { PlayerId = "battle-viewer" };
            VisionSystem.RestoreState(new VisionState { Players = new List<PlayerVisionState> { new PlayerVisionState {
                PlayerId = viewer.PlayerId, Visible = new List<HexCoord> { hex } } } }, _ => viewer);
            int observations = 0; bool intermediate = false;
            Action<PlayerSetupData, HexCoord> changed = (who, at) =>
            {
                if (who != viewer || !at.Equals(hex)) return;
                observations++; intermediate |= ArmyRegistry.IsRegistered(wing) || ArmyRegistry.IsRegistered(target);
            };
            var previous = AviationCombatPresenter.HeadlessRng;
            VisionSystem.VisibleContentChanged += changed;
            try
            {
                AviationCombatPresenter.HeadlessRng = new System.Random(4);
                Drain(presenter.ResolveAirStrikeAtCurrentHex(wing, hex, AirStrikePolicy.Standard));
            }
            finally { AviationCombatPresenter.HeadlessRng = previous; VisionSystem.VisibleContentChanged -= changed; }
            Assert.That(observations, Is.GreaterThan(0)); Assert.That(intermediate, Is.False);
            Assert.That(victim.HitPointsCurrent, Is.Zero); Assert.That(aircraft.HitPointsCurrent, Is.Zero);
        }
        private static void Drain(System.Collections.IEnumerator operation)
        {
            while (operation.MoveNext())
                if (operation.Current is System.Collections.IEnumerator nested) Drain(nested);
        }
        [Test] public void SplitAcceptsTheSourceRosterAsItsReadOnlyInput()
        {
            var created = ArmyActions.CreateArmyWithMembers(owner, hex, catalog, source, source.Members, selection, out _);
            Assert.That(created, Is.Not.Null); Assert.That(source.Members, Is.Empty);
            Assert.That(created.Members, Is.EqualTo(new[] { hero }));
            Assert.That(ArmyRegistry.FindArmyContaining(hero), Is.SameAs(created));
            Assert.That(root.ActionPoints, Is.EqualTo(18));
        }
        [Test] public void DeploymentCannotConsumePaymentIntoADisplaySnapshot()
        {
            var building = new BuildingData { Owner = owner, Hex = hex }; building.Abilities.Add(UnitAbilities.Barracks); BuildingRegistry.Register(hex, building);
            var snapshot = ArmyData.CreateVisualSnapshot(); snapshot.Owner = owner; snapshot.Hex = hex;
            var card = new CardDefinition { displayName = "unit", cardType = CardType.Unit, apCost = 2, requiredBuildingAbility = UnitAbilities.Barracks };
            Assert.That(ArmyActions.DeployUnitFromCard(card, owner, snapshot, root, selection, out _), Is.False);
            Assert.That(snapshot.Members, Is.Empty); Assert.That(root.ActionPoints, Is.EqualTo(20));
        }

        [Test] public void EndTurnWithoutSelectionDisbandsTheEmptyTravellingShell()
        {
            var shell = new ArmyData { Owner = owner, Hex = hex }; ArmyRegistry.Register(shell);
            var turn = scene.AddComponent<Game.Turns.GameTurnController>();
            Bind(turn, "<CurrentPlayer>k__BackingField", owner);
            Bind(turn, "<CurrentTurnOrder>k__BackingField", new List<PlayerSetupData> { owner, new PlayerSetupData { IsHuman = true } });
            typeof(Game.Turns.GameTurnController).GetMethod("AdvanceToNextPlayer", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(turn, null);
            Assert.That(ArmyRegistry.IsRegistered(shell), Is.False);
        }
        [Test] public void MovingCasualtyDoesNotPublishTheDistantLossAtTheIndexedOrigin()
        {
            var first = new UnitData { Owner = owner, IsAviation = true }; var last = new UnitData { Owner = owner, IsAviation = true };
            var wing = new ArmyData { Owner = owner, Hex = hex, IsAirArmy = true }; wing.Members.Add(first); wing.Members.Add(last); ArmyRegistry.Register(wing);
            catalog.airArmyPrefab = marker.GetComponent<MapObjectVisual>();
            var controller = selection.CreateArmyMarker(wing);
            Bind(controller, "<IsMoving>k__BackingField", true); Bind(controller, "_currentHex", new HexCoord(9, 9));
            var viewer = new PlayerSetupData { PlayerId = "origin-viewer" };
            VisionSystem.RestoreState(new VisionState { Players = new List<PlayerVisionState> { new PlayerVisionState {
                PlayerId = viewer.PlayerId, Visible = new List<HexCoord> { hex } } } }, _ => viewer);
            int observations = 0; Action<PlayerSetupData, HexCoord> changed = (who, at) => { if (who == viewer && at.Equals(hex)) observations++; };
            VisionSystem.VisibleContentChanged += changed;
            try { ArmyActions.RemoveMember(wing, first); }
            finally { VisionSystem.VisibleContentChanged -= changed; }
            Assert.That(observations, Is.Zero); Assert.That(wing.Members, Is.EqualTo(new[] { last }));
            Bind(controller, "<IsMoving>k__BackingField", false);
        }

        [TestCase(true)] [TestCase(false)]
        public void DomainDeletionClearsSelectionWhenEnabledOrOnReenable(bool enabled)
        {
            catalog.armyPrefab = marker.GetComponent<MapObjectVisual>();
            var controller = selection.CreateArmyMarker(source); Bind(selection, "_selectedArmy", controller);
            scene.SetActive(true); selection.enabled = enabled;
            ArmyActions.ClearMembers(source); ArmyActions.DeleteArmyIfEmptied(source);
            if (!enabled) selection.enabled = true;
            var selected = typeof(HexSelectionController).GetField("_selectedArmy", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(selection);
            Assert.That(selected, Is.Null);
        }

        [TestCase(true)] [TestCase(false)]
        public void CaptureWithEnabledOrDisabledSelectionReturnsEachAircraftOnce(bool enabled)
        {
            scene.SetActive(true); selection.enabled = enabled;
            var building = new BuildingData { Hex = hex, Owner = owner, IsBase = true, AirfieldCapacity = 2 }; BuildingRegistry.Register(hex, building);
            var field = new ArmyData { Hex = hex, Owner = owner, IsAirfield = true };
            field.Members.Add(new UnitData { Owner = owner, IsAviation = true, OriginatingCard = new CardDefinition() }); ArmyRegistry.Register(field);
            BuildingRegistry.CaptureOrDestroy(building, new PlayerSetupData(), selection);
            Assert.That(owner.Hand.Hand.Count, Is.EqualTo(1)); Assert.That(ArmyRegistry.IsRegistered(field), Is.False);
        }
    }
}
#endif
