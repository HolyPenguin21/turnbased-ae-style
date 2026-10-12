#if UNITY_INCLUDE_TESTS
using System;
using Game.Cards;
using Game.Aviation;
using Game.Combat;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.UI;
using Game.Units;
using NUnit.Framework;

namespace Game.EditorTests
{
    public sealed class ArmyRadioEventContextTests
    {
        private PlayerSetupData _viewer, _enemy;
        private ArmyData _army;
        [SetUp] public void Setup()
        {
            ArmyRegistry.Clear(); BuildingRegistry.Clear(); VisionSystem.Clear(); HexEventRegistry.Clear(); StealthSystem.Clear();
            VisionSystem.Configure(null); VisionSystem.DebugRevealAll = false;
            _viewer = new PlayerSetupData { Nickname = "V", CitadelHexQ = 0, CitadelHexR = 0 };
            _enemy = new PlayerSetupData { Nickname = "E" };
            VisionSystem.CurrentViewer = _viewer;
            _army = Army(_viewer, default); ArmyRegistry.Register(_army);
        }
        [TearDown] public void Cleanup()
        { ArmyRegistry.Clear(); BuildingRegistry.Clear(); VisionSystem.Clear(); HexEventRegistry.Clear(); StealthSystem.Clear(); VisionSystem.DebugRevealAll = false; }
        private static ArmyData Army(PlayerSetupData owner, HexCoord hex)
        {
            var army = new ArmyData { Owner = owner, Hex = hex };
            army.Members.Add(new UnitData { Owner = owner, HitPointsCurrent = 3, HitPointsMax = 3, Attack = 1 });
            return army;
        }
        private ArmyRadioCandidate Candidate(ArmyRadioCategory category, object subject = null, HexCoord? hex = null)
            => new ArmyRadioCandidate(category, _army, _viewer, subject, hex ?? _army.Hex, 0f, 10f);
        [Test] public void SpeakerAndViewer_RevalidatedAfterDeathRemovalAndPerspectiveSwitch()
        {
            var candidate = Candidate(ArmyRadioCategory.HeavyLosses);
            Assert.That(ArmyRadioContext.IsCurrent(candidate, _viewer), Is.True);
            VisionSystem.CurrentViewer = _enemy;
            Assert.That(ArmyRadioContext.IsCurrent(candidate, _viewer), Is.False);
            Assert.That(ArmyRadioContext.IsCurrent(candidate, _enemy), Is.False);
            VisionSystem.CurrentViewer = _viewer; _army.Members[0].HitPointsCurrent = 0;
            Assert.That(ArmyRadioContext.IsCurrent(candidate, _viewer), Is.False);
            _army.Members[0].HitPointsCurrent = 3; ArmyRegistry.Unregister(_army);
            Assert.That(ArmyRadioContext.IsCurrent(candidate, _viewer), Is.False);
        }
        [Test] public void HiddenBaseAndDebugReveal_NeverAuthorizeDiscovery()
        {
            var building = new BuildingData { Owner = _enemy, IsBase = true, Hex = new HexCoord(3, 0) };
            BuildingRegistry.Register(building.Hex, building);
            var candidate = Candidate(ArmyRadioCategory.EnemyBaseDiscovered, building, building.Hex);
            VisionSystem.DebugRevealAll = true;
            Assert.That(ArmyRadioContext.IsCurrent(candidate, _viewer), Is.False);
            int observed = 0;
            ArmyRadioChatter.Observe(_army, _ => observed++);
            Assert.That(observed, Is.EqualTo(1), "Only the army's one visible radius-zero hex is read.");
            ArmyRegistry.MoveArmy(_army, building.Hex);
            Assert.That(ArmyRadioContext.IsCurrent(candidate, _viewer), Is.True);
            ArmyRegistry.MoveArmy(_army, new HexCoord(4, 0));
            Assert.That(ArmyRadioContext.IsCurrent(candidate, _viewer), Is.False);
        }
        [Test] public void RepairsAndDamage_RevalidatedAgainstActualMemberCondition()
        {
            var unit = _army.Members[0];
            var damaged = Candidate(ArmyRadioCategory.Damaged);
            var repaired = Candidate(ArmyRadioCategory.RepairsCompleted, unit);
            unit.HitPointsCurrent = 1;
            Assert.That(ArmyRadioContext.IsCurrent(damaged, _viewer), Is.True);
            Assert.That(ArmyRadioContext.IsCurrent(repaired, _viewer), Is.False);
            unit.HitPointsCurrent = 3;
            Assert.That(ArmyRadioContext.IsCurrent(damaged, _viewer), Is.False);
            Assert.That(ArmyRadioContext.IsCurrent(repaired, _viewer), Is.True);
            _army.Members.Remove(unit);
            Assert.That(ArmyRadioContext.IsCurrent(repaired, _viewer), Is.False);
        }
        [Test] public void ArrivalAndRetreat_InvalidateWhenSpeakerMovesFurther()
        {
            var reinforcement = Candidate(ArmyRadioCategory.ReinforcementsArrived, _army.Members[0]);
            var retreat = Candidate(ArmyRadioCategory.Retreat);
            Assert.That(ArmyRadioContext.IsCurrent(reinforcement, _viewer), Is.True);
            Assert.That(ArmyRadioContext.IsCurrent(retreat, _viewer), Is.True);
            ArmyRegistry.MoveArmy(_army, new HexCoord(1, 0));
            Assert.That(ArmyRadioContext.IsCurrent(reinforcement, _viewer), Is.False);
            Assert.That(ArmyRadioContext.IsCurrent(retreat, _viewer), Is.False);
        }
        [Test] public void EventDiscovery_RequiresPersonalKnowledgeAndUnconsumedCurrentSite()
        {
            HexEventRegistry.Set(default, null, null, null, _enemy, null);
            var entry = HexEventRegistry.FindAt(default);
            var candidate = Candidate(ArmyRadioCategory.EventDiscovered, entry);
            Assert.That(ArmyRadioContext.IsCurrent(candidate, _viewer), Is.False);
            int calls = 0;
            Action<HexCoord, PlayerSetupData> listener = (hex, player) =>
            { Assert.That(HexEventRegistry.IsKnownBy(hex, player), Is.True); calls++; };
            HexEventRegistry.EventDiscovered += listener;
            try { HexEventRegistry.MarkDiscovered(default, _viewer); HexEventRegistry.MarkDiscovered(default, _viewer); }
            finally { HexEventRegistry.EventDiscovered -= listener; }
            Assert.That(calls, Is.EqualTo(1)); Assert.That(ArmyRadioContext.IsCurrent(candidate, _viewer), Is.True);
            HexEventRegistry.MarkConsumed(default, _viewer);
            Assert.That(ArmyRadioContext.IsCurrent(candidate, _viewer), Is.False);
        }
        [Test] public void GroundExploration_PublishesCommittedKnowledgeOnceAndExcludesAircraft()
        {
            int calls = 0;
            Action<PlayerSetupData, HexCoord> listener = (owner, hex) =>
            {
                Assert.That(VisionSystem.IsVisited(owner, hex), Is.True);
                Assert.That(VisionSystem.HasEverSeen(owner, hex), Is.True);
                Assert.That(VisionSystem.IsVisible(owner, hex), Is.True); calls++;
            };
            VisionSystem.GroundHexVisited += listener;
            try
            {
                ArmyRegistry.MoveArmy(_army, new HexCoord(1, 0)); ArmyRegistry.MoveArmy(_army, default);
                Assert.That(calls, Is.EqualTo(1));
                _army.IsAirArmy = true; _army.Members[0].IsAviation = true;
                ArmyRegistry.MoveArmy(_army, new HexCoord(2, 0)); Assert.That(calls, Is.EqualTo(1));
            }
            finally { VisionSystem.GroundHexVisited -= listener; }
        }
        [Test] public void BaseCapture_PostCommitNotificationIgnoresUnchangedOwner()
        {
            var building = new BuildingData { Owner = _enemy, IsBase = true, Hex = default };
            building.Abilities.Add(UnitAbilities.Barracks); BuildingRegistry.Register(default, building);
            int calls = 0;
            Action<BuildingData, PlayerSetupData> listener = (captured, previous) =>
            {
                Assert.That(captured.Owner, Is.SameAs(_viewer)); Assert.That(previous, Is.SameAs(_enemy));
                Assert.That(BuildingRegistry.FindAt(default), Is.SameAs(captured));
                Assert.That(ArmyRegistry.FindGarrisonAt(default, _viewer), Is.Not.Null);
                Assert.That(VisionSystem.IsVisible(_viewer, default), Is.True); calls++;
            };
            BuildingRegistry.BaseCaptured += listener;
            try { BuildingRegistry.CaptureOrDestroy(building, _viewer, null); BuildingRegistry.CaptureOrDestroy(building, _viewer, null); }
            finally { BuildingRegistry.BaseCaptured -= listener; }
            Assert.That(calls, Is.EqualTo(1));
            Assert.That(ArmyRadioContext.IsCurrent(Candidate(ArmyRadioCategory.BaseCaptured, building), _viewer), Is.True);
        }
        [Test] public void HeroCapture_OnlyRealImprisonmentPublishesCustody()
        {
            var target = Army(_enemy, default); target.Members[0].IsHero = true;
            var hero = target.Members[0]; ArmyRegistry.Register(target);
            var prison = new ArmyData { Owner = _viewer, Hex = default, IsPrison = true }; ArmyRegistry.Register(prison);
            int calls = 0;
            Action<ArmyData, UnitData> listener = (hunter, captive) =>
            { Assert.That(captive.IsPrisoner, Is.True); Assert.That(prison.Members.Contains(captive), Is.True); calls++; };
            BattleEngine.HeroImprisoned += listener;
            try
            {
                var result = BattleEngine.ApplyCaptureKillOutcome(null, CaptureKillOutcome.Captured, hero, target, _army);
                Assert.That(result.Imprisoned, Is.True); Assert.That(calls, Is.EqualTo(1));
                Assert.That(ArmyRadioContext.IsCurrent(Candidate(ArmyRadioCategory.HeroCaptured, hero), _viewer), Is.True);
                prison.Members.Remove(hero);
                Assert.That(ArmyRadioContext.IsCurrent(Candidate(ArmyRadioCategory.HeroCaptured, hero), _viewer), Is.False);
                var another = Army(_enemy, default); another.Members[0].IsHero = true;
                BattleEngine.ApplyCaptureKillOutcome(null, CaptureKillOutcome.Killed, another.Members[0], another, _army);
                Assert.That(calls, Is.EqualTo(1));
            }
            finally { BattleEngine.HeroImprisoned -= listener; }
        }
        [Test] public void EncounterFinalization_NotificationUsesAuthoritativeSurvivorAfterSummonCleanup()
        {
            var defender = new ArmyData { Owner = _enemy, Hex = default }; ArmyRegistry.Register(defender);
            var summon = new UnitData { Owner = _viewer, IsSummoned = true, HitPointsCurrent = 1 }; _army.Members.Add(summon);
            var state = new BattleState(default, _army, defender, new BattleGrid(), 91);
            int calls = 0;
            Action<BattleState, BattleEncounterFinalization> listener = (observed, result) =>
            {
                Assert.That(observed, Is.SameAs(state)); Assert.That(result.Survivor, Is.SameAs(_army));
                Assert.That(_army.Members.Contains(summon), Is.False); calls++;
            };
            BattleEngine.EncounterFinalized += listener;
            try { BattleEngine.FinalizeEncounter(state, null); }
            finally { BattleEngine.EncounterFinalized -= listener; }
            Assert.That(calls, Is.EqualTo(1));
        }
        [Test] public void DestroyedRetreat_ResultCannotCreateLivingSpeaker()
        {
            var opposing = Army(_enemy, default); ArmyRegistry.Register(opposing);
            var state = new BattleState(default, _army, opposing, new BattleGrid(), 13);
            int calls = 0;
            Action<BattleState, ArmyData, BattleRetreatApplication> listener = (battle, army, result) =>
            { Assert.That(result.Destroyed, Is.True); Assert.That(army.Members, Is.Empty); calls++; };
            BattleEngine.ArmyRetreated += listener;
            try { BattleEngine.PerformRetreat(state, _army, opposing, null, null); }
            finally { BattleEngine.ArmyRetreated -= listener; }
            Assert.That(calls, Is.EqualTo(1));
            Assert.That(ArmyRadioContext.IsCurrent(Candidate(ArmyRadioCategory.Retreat), _viewer), Is.False);
        }
        private ArmyData Wing()
        {
            var wing = new ArmyData { Owner = _viewer, IsAirArmy = true, Hex = default };
            wing.Members.Add(new UnitData { Owner = _viewer, IsAviation = true, HitPointsCurrent = 3, HitPointsMax = 3,
                SortieLaunchPaid = true, ConsecutiveUnlandedEnds = 1, HasEmergencyFlightPenalty = true });
            ArmyRegistry.Register(wing);
            BuildingRegistry.Register(default, new BuildingData { Owner = _viewer, Hex = default, IsBase = true, AirfieldCapacity = 4 });
            return wing;
        }
        [TestCase(true)] [TestCase(false)]
        public void AutomaticLanding_PublishesAfterRefuelForHumanAndAi(bool human)
        {
            _viewer.IsHuman = human; var wing = Wing(); var aircraft = wing.Members[0]; int calls = 0;
            Action<ArmyData, ArmyData, UnitData> listener = (source, receiver, landed) =>
            {
                Assert.That(source, Is.SameAs(wing)); Assert.That(landed, Is.SameAs(aircraft));
                Assert.That(receiver.Members.Contains(landed), Is.True); Assert.That(receiver.IsAirfield, Is.EqualTo(!human));
                Assert.That(landed.SortieLaunchPaid, Is.False); Assert.That(landed.ConsecutiveUnlandedEnds, Is.Zero);
                Assert.That(landed.HasEmergencyFlightPenalty, Is.False);
                Assert.That(ArmyRadioContext.IsCurrent(new ArmyRadioCandidate(ArmyRadioCategory.AviationReturned,
                    _army, _viewer, landed, default, 0f, 6f, receiver), _viewer), Is.True); calls++;
            };
            AviationActions.AircraftLanded += listener;
            try { AviationTurnLifecycle.ResolveEndOfTurn(_viewer, null); }
            finally { AviationActions.AircraftLanded -= listener; }
            Assert.That(calls, Is.EqualTo(1)); Assert.That(wing.Members.Contains(aircraft), Is.EqualTo(human));
        }
        [TestCase(false)] [TestCase(true)]
        public void ManualLanding_SingleAndAtomicTransferPublishCommittedStorage(bool batch)
        {
            var wing = Wing(); var aircraft = wing.Members[0]; var airfield = AviationActions.EnsureAirfield(null, _viewer, default);
            int calls = 0;
            Action<ArmyData, ArmyData, UnitData> listener = (source, receiver, landed) =>
            {
                Assert.That(source, Is.SameAs(wing)); Assert.That(receiver, Is.SameAs(airfield));
                Assert.That(source.Members.Contains(aircraft), Is.False); Assert.That(receiver.Members.Contains(aircraft), Is.True);
                Assert.That(aircraft.SortieLaunchPaid, Is.False); calls++;
            };
            AviationActions.AircraftLanded += listener;
            try
            {
                bool ok = batch ? ArmyActions.TransferMembersAtomic(new[] { aircraft }, wing, airfield, null, out _)
                    : ArmyActions.TransferMember(aircraft, wing, airfield, null, out _);
                Assert.That(ok, Is.True);
            }
            finally { AviationActions.AircraftLanded -= listener; }
            Assert.That(calls, Is.EqualTo(1));
        }
        [Test] public void PartialLanding_PublishesOnlyTheLandedMemberAndPreservesRemainingFlight()
        {
            var wing = Wing(); var first = wing.Members[0]; var second = new UnitData
                { Owner = _viewer, IsAviation = true, HitPointsCurrent = 3, HitPointsMax = 3, SortieLaunchPaid = true };
            wing.Members.Add(second); BuildingRegistry.FindAt(default).AirfieldCapacity = 1; int calls = 0;
            Action<ArmyData, ArmyData, UnitData> listener = (source, receiver, landed) =>
            {
                Assert.That(landed, Is.SameAs(first)); Assert.That(receiver.Members.Contains(first), Is.True);
                Assert.That(source.Members.Contains(second), Is.True); Assert.That(second.SortieLaunchPaid, Is.True); calls++;
            };
            AviationActions.AircraftLanded += listener;
            try { Assert.That(AviationActions.LandInSlotOrder(wing, null), Is.EqualTo(1)); }
            finally { AviationActions.AircraftLanded -= listener; }
            Assert.That(calls, Is.EqualTo(1));
        }
        [TestCase(false)] [TestCase(true)]
        public void LandingContext_RejectsNewTakeoffForStoredAndRetainedAircraft(bool retained)
        {
            var wing = Wing(); var aircraft = wing.Members[0]; _viewer.IsHuman = retained;
            AviationTurnLifecycle.ResolveEndOfTurn(_viewer, null);
            var receiver = retained ? wing : AviationRules.FindAirfieldAt(default, _viewer);
            var candidate = new ArmyRadioCandidate(ArmyRadioCategory.AviationReturned, _army, _viewer,
                aircraft, default, 0f, 6f, receiver);
            Assert.That(ArmyRadioContext.IsCurrent(candidate, _viewer), Is.True);
            aircraft.SortieLaunchPaid = true;
            Assert.That(ArmyRadioContext.IsCurrent(candidate, _viewer), Is.False);
        }
        [Test] public void Contact_DisappearingOrHiddenEnemyCannotRemainAuthorized()
        {
            var enemy = Army(_enemy, default); ArmyRegistry.Register(enemy);
            var candidate = Candidate(ArmyRadioCategory.Contact);
            Assert.That(ArmyRadioContext.IsCurrent(candidate, _viewer, contact: ArmyRadioChatter.SeesEnemy(_army)), Is.True);
            enemy.Members[0].IsHidden = true;
            Assert.That(ArmyRadioContext.IsCurrent(candidate, _viewer, contact: ArmyRadioChatter.SeesEnemy(_army)), Is.False);
            enemy.Members[0].IsHidden = false; ArmyRegistry.MoveArmy(enemy, new HexCoord(1, 0));
            Assert.That(ArmyRadioContext.IsCurrent(candidate, _viewer, contact: ArmyRadioChatter.SeesEnemy(_army)), Is.False);
        }
    }
}
#endif
