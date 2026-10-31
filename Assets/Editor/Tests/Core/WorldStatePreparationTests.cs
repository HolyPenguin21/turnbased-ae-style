#if UNITY_INCLUDE_TESTS
using System;
using System.Collections.Generic;
using Game.Aviation;
using Game.Combat;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Setup;
using Game.Units;
using NUnit.Framework;

namespace Game.EditorTests
{
    public sealed class WorldStatePreparationTests
    {
        [TearDown] public void Clear()
        { ArmyRegistry.Clear(); BuildingRegistry.Clear(); VisionSystem.Clear(); StealthSystem.Clear(); StealthSystem.CompletedTurnsProvider = _ => 0L; StealthSystem.ObserverTakingTurnProvider = _ => false; AntiAirState.Clear(); DelayedBattleRegistry.Clear(); }

        [Test] public void ActivationRestoreRetainsPaidDepartedUnitAndRejectsMissingReferenceAtomically()
        {
            var army = new ArmyData(); var departed = new UnitData();
            army.Members.Add(departed); army.MarkActivated(); army.Members.Clear();
            UnitData included = null;
            var state = army.CaptureActivationState(unit => included = unit); army.ResetActivationForNewTurn();
            Assert.That(included, Is.SameAs(departed), "An entity codec must discover paid units even after they departed.");
            army.RestoreActivationState(state, id => id == departed.RuntimeId ? departed : null);
            Assert.That(army.HasActivatedThisTurn, Is.True);
            Assert.That(army.RequiresActivationCharge(departed), Is.False);
            Assert.Throws<ArgumentException>(() => army.RestoreActivationState(state, _ => null));
            Assert.That(army.RequiresActivationCharge(departed), Is.False);
        }

        [Test] public void MovementRestorePreservesSpentBeyondReducedMaximumAndTransportLinks()
        {
            var army = new ArmyData(); var carrier = new UnitData();
            var body = new UnitData { MoveMax = 3, MoveCurrent = 2 };
            body.SetTransportCarrier(carrier, army); body.RestoreMovementSpent(7);
            var state = body.CaptureMovementState(); body.SetTransportCarrier(null, null); body.MoveMax = 9;
            body.RestoreMovementState(state, id => id == carrier.RuntimeId ? carrier : null,
                id => id == army.Id ? army : null);
            Assert.That(body.PermanentMoveMax, Is.EqualTo(3));
            Assert.That(body.MovementSpent, Is.EqualTo(7));
            Assert.That(body.AssignedTransportCarrier, Is.SameAs(carrier));
            Assert.That(body.TransportArmy, Is.SameAs(army));
        }

        [Test] public void RestoredTransportOwnerCanClearRecipientLinksWithoutReallocatingThem()
        {
            var army = new ArmyData(); var carrier = new UnitData(); var body = new UnitData();
            army.Members.Add(carrier); army.Members.Add(body);
            body.SetTransportCarrier(carrier, army);
            army.RebindRestoredTransportRecipients();
            Assert.That(body.AssignedTransportCarrier, Is.SameAs(carrier));
            army.ClearTransport();
            Assert.That(body.AssignedTransportCarrier, Is.Null);
            Assert.That(body.TransportArmy, Is.Null);
        }

        [Test] public void RestoreScopeRegistrationDoesNotCreateCarrierContainersOrLearnFog()
        {
            var owner = new PlayerSetupData { PlayerId = "p1" };
            var army = new ArmyData { Owner = owner, Hex = new HexCoord(2, 3) };
            var carrier = new UnitData { Owner = owner, AirfieldCapacity = 3 }; army.Members.Add(carrier);
            int notices = 0; Action<PlayerSetupData> listener = _ => notices++;
            VisionSystem.VisibilityChanged += listener;
            try
            {
                using (MatchRestoreScope.Begin())
                { ArmyRegistry.Register(army); BuildingRegistry.Register(new HexCoord(8, 1), new BuildingData { Owner = owner }); }
                Assert.That(carrier.CarrierAirfield, Is.Null);
                Assert.That(VisionSystem.HasEverSeen(owner, army.Hex), Is.False);
                Assert.That(notices, Is.Zero);
                Assert.That(ArmyRegistry.IsRegistered(army), Is.True);
            }
            finally { VisionSystem.VisibilityChanged -= listener; }
        }

        [Test] public void RestoreScopeRemovalAndRelocationDoNotPublishWorldActions()
        {
            var owner = new PlayerSetupData { PlayerId = "p1" }; var origin = new HexCoord(1, 0);
            var army = new ArmyData { Owner = owner, Hex = origin };
            var building = new BuildingData { Owner = owner, Hex = origin };
            ArmyRegistry.Register(army); BuildingRegistry.Register(origin, building);
            int actions = 0;
            Action<ArmyData, HexCoord, HexCoord> moved = (a, h, j) => actions++;
            Action<BuildingData> destroyed = b => actions++;
            ArmyRegistry.ArmyRelocated += moved; BuildingRegistry.BuildingDestroyed += destroyed;
            try
            {
                using (MatchRestoreScope.Begin())
                {
                    ArmyRegistry.MoveArmy(army, new HexCoord(2, 0));
                    BuildingRegistry.Unregister(origin); ArmyRegistry.Unregister(army);
                }
                Assert.That(actions, Is.Zero);
                Assert.That(ArmyRegistry.IsRegistered(army), Is.False);
                Assert.That(BuildingRegistry.FindAt(origin), Is.Null);
            }
            finally { ArmyRegistry.ArmyRelocated -= moved; BuildingRegistry.BuildingDestroyed -= destroyed; }
        }

        [Test] public void RestoreScopeNeverRollsFreshDetectionChallenge()
        {
            var unit = new UnitData { IsHidden = true };
            var observer = new PlayerSetupData { PlayerId = "p1" };
            var source = new ArmyData { Owner = observer, Hex = new HexCoord(0, 0) }; source.Members.Add(new UnitData());
            ArmyRegistry.Register(source);
            Assert.That(StealthSystem.SpotPoolAgainst(observer, source.Hex), Is.GreaterThan(0));
            int rolls = 0; var original = StealthSystem.ChallengeRoller;
            StealthSystem.ChallengeRoller = (a, b) => { rolls++; throw new System.InvalidOperationException("Restore must not roll a detection challenge."); };
            try
            {
                using (MatchRestoreScope.Begin()) StealthSystem.ResolveDetection(unit, observer, new HexCoord(0, 0));
                Assert.That(rolls, Is.Zero);
            }
            finally { StealthSystem.ChallengeRoller = original; }
        }

        [Test] public void DetectionRestoreKeepsExactExpiryRatherThanRenewingWindow()
        {
            var observer = new PlayerSetupData { PlayerId = "p1" }; var unit = new UnitData { IsHidden = true };
            long turns = 7; StealthSystem.CompletedTurnsProvider = _ => turns;
            StealthSystem.ObserverTakingTurnProvider = _ => true; StealthSystem.MarkDetected(unit, observer);
            var state = StealthSystem.CaptureState(); StealthSystem.Clear(); turns = 8;
            StealthSystem.RestoreState(state, _ => unit, _ => observer);
            Assert.That(StealthSystem.IsDetectedBy(unit, observer), Is.True);
            turns = 9; Assert.That(StealthSystem.IsDetectedBy(unit, observer), Is.False);
        }

        [Test] public void FogRestoreKeepsDistinctVisibleVisitedAndSeenSetsWithoutEvents()
        {
            var owner = new PlayerSetupData { PlayerId = "p1" }; var visible = new HexCoord(1, 0); var visited = new HexCoord(2, 0); var seen = new HexCoord(3, 0);
            var state = new VisionState { CurrentViewerId = "p1", Players = new List<PlayerVisionState> {
                new PlayerVisionState { PlayerId = "p1", Visible = new List<HexCoord>{visible}, Visited = new List<HexCoord>{visited}, EverSeen = new List<HexCoord>{visible, visited, seen} } } };
            VisionSystem.RestoreState(state, _ => owner);
            Assert.That(VisionSystem.IsVisible(owner, visible), Is.True);
            Assert.That(VisionSystem.IsVisited(owner, visible), Is.False);
            Assert.That(VisionSystem.IsVisited(owner, visited), Is.True);
            Assert.That(VisionSystem.HasEverSeen(owner, seen), Is.True);
            Assert.That(VisionSystem.CurrentViewer, Is.SameAs(owner));
        }

        [Test] public void AntiAirRestoreRebindsFacilityGunnerAndPreservesSkippedPair()
        {
            var facility = new FacilityData(); var building = new BuildingData { Hex = new HexCoord(2, 0) }; building.FacilitySlots[0] = facility;
            BuildingRegistry.Register(building.Hex, building);
            var gunner = new UnitData(); facility.AntiAirGunner = gunner;
            AntiAirState.RecordPrompted(gunner, 12000, false);
            var state = AntiAirState.CaptureState(); AntiAirState.Clear(); facility.AntiAirGunner = null;
            AntiAirState.RestoreState(state, id => id == gunner.RuntimeId ? gunner : null);
            Assert.That(facility.AntiAirGunner, Is.SameAs(gunner));
            Assert.That(AntiAirState.CanReact(gunner, 12000), Is.False);
            Assert.That(AntiAirState.CanReact(gunner, 12001), Is.True);
            Assert.That(new ArmyData().Id, Is.GreaterThan(12000));
        }

        [Test] public void HumanMemoryRestoreUsesObservedSnapshotWithoutAdvancingLiveIdentity()
        {
            var viewer = new PlayerSetupData { PlayerId = "p1", IsHuman = true };
            var enemy = new PlayerSetupData { PlayerId = "p2" };
            int rememberedId = ArmyData.IdentityHighWaterMark + 500;
            var live = ArmyData.WithIdentity(rememberedId); live.Owner = enemy; live.Hex = new HexCoord(4, 0);
            var body = new UnitData { Name = "Observed", HitPointsCurrent = 3 }; live.Members.Add(body);
            int unitWatermark = UnitData.IdentityHighWaterMark;
            HumanVisualMemory.ObserveArmy(viewer, live, live.Hex);
            Assert.That(UnitData.IdentityHighWaterMark, Is.EqualTo(unitWatermark), "Display memory must not mint live identities.");
            ArmyData display = null;
            var state = HumanVisualMemory.CaptureState(snapshot => { display = snapshot; return "snapshot:1"; });
            body.HitPointsCurrent = 1; HumanVisualMemory.Clear(); ArmyRegistry.Clear();
            HumanVisualMemory.RestoreState(state, id => id == "p1" ? viewer : enemy, key => display);
            Assert.That(HumanVisualMemory.TryGetArmySighting(viewer, rememberedId, out var sighting), Is.True);
            Assert.That(sighting.Army.Members[0].HitPointsCurrent, Is.EqualTo(3));
            Assert.That(new ArmyData().Id, Is.Zero, "Historical restore must leave live identity restoration to MatchIdentityState.");
            Assert.Throws<ArgumentException>(() => HumanVisualMemory.RestoreState(state, _ => viewer, _ => live));
            Assert.That(HumanVisualMemory.TryGetArmySighting(viewer, rememberedId, out sighting), Is.True);
        }

        [Test] public void EconomyRestorePreservesContributionOrderWithoutBuyingOrPublishingChanges()
        {
            var owner = new PlayerSetupData { PlayerId = "p1" };
            var root = PlayerRoot.Create(owner, "Restore economy test");
            try
            {
                var state = new PlayerEconomyState { ActionPoints = 4, Human = 2, Energy = 3, Materials = 5, Tech = 7,
                    InitiativeContributions = new List<Game.Economy.ResourceType> { Game.Economy.ResourceType.Human, Game.Economy.ResourceType.Tech } };
                int notices = 0; root.ResourcesChanged += () => notices++;
                root.RestoreEconomyState(state);
                Assert.That(root.ActionPoints, Is.EqualTo(4));
                Assert.That(root.GetResource(Game.Economy.ResourceType.Tech), Is.EqualTo(7));
                Assert.That(root.CanRefundInitiativeToken(Game.Economy.ResourceType.Tech), Is.True);
                Assert.That(root.CaptureEconomyState().InitiativeContributions, Is.EqualTo(state.InitiativeContributions));
                Assert.That(notices, Is.Zero);
            }
            finally { UnityEngine.Object.DestroyImmediate(root.gameObject); }
        }

        [Test] public void CarrierLinksRestoreReusesExistingContainersWithoutMintingNewArmies()
        {
            var carrier = new UnitData(); var airfield = new ArmyData(); var deck = new ArmyData();
            carrier.CarrierAirfield = airfield; carrier.CarrierFlightDeck = deck;
            var state = carrier.CaptureCarrierLinksState(); carrier.CarrierAirfield = null; carrier.CarrierFlightDeck = null;
            int next = ArmyData.IdentityHighWaterMark;
            carrier.RestoreCarrierLinksState(state, id => id == airfield.Id ? airfield : id == deck.Id ? deck : null);
            Assert.That(carrier.CarrierAirfield, Is.SameAs(airfield));
            Assert.That(carrier.CarrierFlightDeck, Is.SameAs(deck));
            Assert.That(ArmyData.IdentityHighWaterMark, Is.EqualTo(next));
        }

        [Test] public void IdentityReserveIncludesDestroyedFactsAndRejectsInvalidIds()
        {
            int card = Game.Cards.CardData.IdentityHighWaterMark + 700;
            Game.Cards.CardData.ReserveIdentity(card);
            Assert.That(new Game.Cards.CardData(null).RuntimeId, Is.GreaterThan(card));
            int unit = UnitData.IdentityHighWaterMark + 700; UnitData.ReserveIdentity(unit);
            Assert.That(new UnitData().RuntimeId, Is.GreaterThan(unit));
            Assert.Throws<ArgumentOutOfRangeException>(() => ArmyData.WithIdentity(-1));
            Assert.Throws<ArgumentOutOfRangeException>(() => UnitData.WithRuntimeId(0));
            Assert.Throws<ArgumentOutOfRangeException>(() => Game.Cards.CardData.WithRuntimeId(null, int.MaxValue));
        }

        [Test] public void DelayedBattleRestorePreservesQueueAndParticipantOrder()
        {
            var a = new ArmyData(); var b = new ArmyData(); var hex = new HexCoord(1, 3);
            DelayedBattleRegistry.Add(new PendingBattle { Hex = hex, Participants = new List<ArmyData>{ b, a } });
            var state = DelayedBattleRegistry.CaptureState(); DelayedBattleRegistry.Clear();
            DelayedBattleRegistry.RestoreState(state, id => id == a.Id ? a : id == b.Id ? b : null);
            Assert.That(DelayedBattleRegistry.TakeNext().Participants, Is.EqualTo(new[]{b, a}));
            Assert.That(DelayedBattleRegistry.HasAny, Is.False);
        }

        [Test] public void DelayedBattleCaptureRetainsRemovedParticipantWithoutRegisteringItOnRestore()
        {
            var firstHex = new HexCoord(1, 3); var secondHex = new HexCoord(4, 2);
            var attacker = new ArmyData { Hex = firstHex }; var removed = new ArmyData { Hex = firstHex };
            var otherAttacker = new ArmyData { Hex = secondHex }; var otherDefender = new ArmyData { Hex = secondHex };
            foreach (var army in new[] { attacker, removed, otherAttacker, otherDefender })
            { army.Members.Add(new UnitData()); ArmyRegistry.Register(army); }
            DelayedBattleRegistry.Add(new PendingBattle { Hex = firstHex, Participants = new List<ArmyData> { attacker, removed } });
            DelayedBattleRegistry.Add(new PendingBattle { Hex = secondHex, Participants = new List<ArmyData> { otherAttacker, otherDefender } });
            removed.Members.Clear(); ArmyRegistry.Unregister(removed);

            var retained = new Dictionary<int, ArmyData>();
            var state = DelayedBattleRegistry.CaptureState(army => retained.Add(army.Id, army));
            Assert.That(retained[removed.Id], Is.SameAs(removed));
            Assert.That(ArmyRegistry.IsRegistered(removed), Is.False, "Capturing a stale participant must not revive it.");

            ArmyRegistry.Clear(); DelayedBattleRegistry.Clear();
            var restored = new Dictionary<int, ArmyData>();
            foreach (var pair in retained)
            {
                var shell = ArmyData.WithIdentity(pair.Key); shell.Hex = pair.Value.Hex;
                restored.Add(pair.Key, shell);
                if (pair.Value != removed) ArmyRegistry.Register(shell);
            }
            DelayedBattleRegistry.RestoreState(state, id => restored.TryGetValue(id, out var army) ? army : null);

            var first = DelayedBattleRegistry.TakeNext(); var second = DelayedBattleRegistry.TakeNext();
            Assert.That(first.Hex, Is.EqualTo(firstHex));
            Assert.That(first.Participants, Is.EqualTo(new[] { restored[attacker.Id], restored[removed.Id] }));
            Assert.That(second.Hex, Is.EqualTo(secondHex));
            Assert.That(second.Participants, Is.EqualTo(new[] { restored[otherAttacker.Id], restored[otherDefender.Id] }));
            Assert.That(ArmyRegistry.IsRegistered(restored[removed.Id]), Is.False, "The delayed queue owns a reference, not live map membership.");
            Assert.That(DelayedBattleRegistry.HasAny, Is.False);
        }
    }
}
#endif
