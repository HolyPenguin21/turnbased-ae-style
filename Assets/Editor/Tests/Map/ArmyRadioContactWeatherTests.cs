#if UNITY_INCLUDE_TESTS
using System;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.UI;
using Game.Units;
using NUnit.Framework;

namespace Game.EditorTests
{
    public sealed class ArmyRadioContactWeatherTests
    {
        private PlayerSetupData _owner, _enemy;

        [SetUp]
        public void SetUp()
        {
            ArmyRegistry.Clear(); BuildingRegistry.Clear(); VisionSystem.Clear();
            VisionSystem.Configure(null); SandstormState.ResetForMatch();
            _owner = new PlayerSetupData { IsHuman = true };
            _enemy = new PlayerSetupData();
            VisionSystem.CurrentViewer = _owner;
        }

        [TearDown]
        public void TearDown()
        {
            ArmyRegistry.Clear(); BuildingRegistry.Clear(); VisionSystem.Clear();
            SandstormState.ResetForMatch();
        }

        private ArmyData Army(PlayerSetupData owner, int count)
        {
            var army = new ArmyData { Owner = owner, Hex = new HexCoord(0, 0) };
            for (int i = 0; i < count; i++)
                army.Members.Add(new UnitData { Owner = owner, Attack = 3, Defense = 2,
                    HitPointsCurrent = 5, HitPointsMax = 5, Range = 1 });
            ArmyRegistry.Register(army);
            return army;
        }

        [TestCase(4, 1, "WeakEnemy")]
        [TestCase(1, 4, "StrongEnemy")]
        [TestCase(2, 2, "Contact")]
        public void ContactDescribesVisibleStrengthRelativeToSpeaker(int own, int enemy, string expected)
        {
            var speaker = Army(_owner, own);
            Army(_enemy, enemy);
            Assert.That(ArmyRadioChatter.Situation(speaker, true, false, 0f).ToString(), Is.EqualTo(expected));
        }

        [Test]
        public void HiddenDeadAndPrisonerMembersDoNotMakeContactStronger()
        {
            var speaker = Army(_owner, 4);
            var enemy = Army(_enemy, 1);
            enemy.Members.Add(new UnitData { Owner = _enemy, Attack = 999, HitPointsCurrent = 5, IsHidden = true });
            enemy.Members.Add(new UnitData { Owner = _enemy, Attack = 999, HitPointsCurrent = 0 });
            enemy.Members.Add(new UnitData { Owner = _enemy, Attack = 999, HitPointsCurrent = 5, IsPrisoner = true });
            Assert.That(ArmyRadioChatter.Situation(speaker, true, false, 0f).ToString(), Is.EqualTo("WeakEnemy"));
        }

        [Test]
        public void StrongVisibleContactTakesPrecedenceOverWeakContact()
        {
            var speaker = Army(_owner, 2);
            Army(_enemy, 1); Army(_enemy, 8);
            Assert.That(ArmyRadioChatter.Situation(speaker, true, false, 0f).ToString(), Is.EqualTo("StrongEnemy"));
        }

        [Test]
        public void AssessmentIsRevalidatedWhenTheVisibleRosterChanges()
        {
            var speaker = Army(_owner, 4);
            var enemy = Army(_enemy, 1);
            var candidate = new ArmyRadioCandidate(Category("WeakEnemy"), speaker, _owner, null, speaker.Hex, 0f, 6f);
            Assert.That(ArmyRadioContext.IsCurrent(candidate, _owner, contact: true), Is.True);
            enemy.Members[0].Attack = 999;
            Assert.That(ArmyRadioContext.IsCurrent(candidate, _owner, contact: true), Is.False);
            Assert.That(ArmyRadioChatter.Situation(speaker, true, false, 0f).ToString(), Is.EqualTo("StrongEnemy"));
            enemy.Members[0].IsHidden = true;
            Assert.That(ArmyRadioContext.IsCurrent(candidate, _owner, contact: true), Is.False);
        }

        [Test]
        public void AnotherArmysVisionDoesNotExtendTheSpeakersRange()
        {
            var speaker = Army(_owner, 4);
            var scout = Army(_owner, 1);
            var enemy = Army(_enemy, 1);
            ArmyRegistry.MoveArmy(scout, new HexCoord(1, 0));
            ArmyRegistry.MoveArmy(enemy, scout.Hex);
            Assert.That(VisionSystem.IsVisible(_owner, enemy.Hex), Is.True);
            Assert.That(ArmyRadioChatter.SeesEnemy(speaker), Is.False);
            Assert.That(ArmyRadioChatter.Situation(speaker, true, false, 0f).ToString(), Is.EqualTo("Contact"));
        }

        [Test]
        public void NoVisibleEnemyDoesNotProduceAnAssessmentEvenWithDebugReveal()
        {
            var speaker = Army(_owner, 4);
            var enemy = Army(_enemy, 1);
            enemy.Members[0].IsHidden = true;
            Assert.That(ArmyRadioChatter.Situation(speaker, true, false, 0f).ToString(), Is.EqualTo("Contact"));
            enemy.Members[0].IsHidden = false;
            ArmyRegistry.MoveArmy(enemy, new HexCoord(5, 0));
            bool before = VisionSystem.DebugRevealAll;
            try
            {
                VisionSystem.DebugRevealAll = true;
                Assert.That(ArmyRadioChatter.SeesEnemy(speaker), Is.False);
                Assert.That(ArmyRadioChatter.Situation(speaker, true, false, 0f).ToString(), Is.EqualTo("Contact"));
            }
            finally { VisionSystem.DebugRevealAll = before; }
        }

        private static ArmyRadioCategory Category(string name)
        {
            Assert.That(Enum.TryParse(name, out ArmyRadioCategory result), Is.True, name + " must be dispatched");
            return result;
        }

        private static HexCoord StartStorm()
        {
            SandstormState.Initialize(new MatchWeatherConditions(MatchWeatherProfile.OneSandstorm, 42), null);
            SandstormState.AdvanceRound(SandstormLifecycleTests.Field(6), 1);
            return SandstormState.Snapshot.Cells[0];
        }

        [TestCase(false, "GroundSandstorm")]
        [TestCase(true, "AirSandstorm")]
        public void StormReportsRequireCurrentExposureAndCorrectArmyType(bool air, string name)
        {
            var army = Army(_owner, 1);
            var hex = StartStorm();
            ArmyRegistry.MoveArmy(army, hex);
            army.IsAirArmy = air;
            army.Members[0].IsAviation = air;
            army.Members[0].SortieLaunchPaid = air;
            var candidate = new ArmyRadioCandidate(Category(name), army, _owner, new object(), hex, 0f, 6f);
            Assert.That(ArmyRadioContext.IsCurrent(candidate, _owner), Is.True);
            army.AirWingLanded = air;
            if (air) Assert.That(ArmyRadioContext.IsCurrent(candidate, _owner), Is.False);
            army.AirWingLanded = false;
            ArmyRegistry.MoveArmy(army, new HexCoord(20, 0));
            Assert.That(ArmyRadioContext.IsCurrent(candidate, _owner), Is.False);
        }

        [Test]
        public void ReadyWingReportsFlyingOnlyAfterARealFlightAction()
        {
            var army = Army(_owner, 1);
            ArmyRegistry.MoveArmy(army, StartStorm());
            army.IsAirArmy = true; army.Members[0].IsAviation = true;
            Assert.That(ArmyRadioChatter.SandstormCategory(army), Is.Null,
                "forming a wing on an airfield is not take-off");
            army.Members[0].SortieLaunchPaid = true;
            Assert.That(ArmyRadioChatter.SandstormCategory(army), Is.EqualTo(Category("AirSandstorm")));
            army.Members[0].SortieLaunchPaid = false;
            Assert.That(ArmyRadioChatter.SandstormCategory(army), Is.Null);
        }

        [Test]
        public void AircraftRosterUsesCanonicalAirClassificationEvenBeforeTheFlagIsRefreshed()
        {
            var army = Army(_owner, 1);
            ArmyRegistry.MoveArmy(army, StartStorm());
            army.Members[0].IsAviation = true; army.Members[0].SortieLaunchPaid = true;
            Assert.That(army.IsAirArmy, Is.False);
            Assert.That(ArmyRadioChatter.SandstormCategory(army), Is.EqualTo(Category("AirSandstorm")));
        }

        [Test]
        public void StoredAndOnDeckAircraftNeverReportFlyingInTheStorm()
        {
            var army = Army(_owner, 1);
            ArmyRegistry.MoveArmy(army, StartStorm());
            army.Members[0].IsAviation = true; army.Members[0].SortieLaunchPaid = true;
            army.IsAirfield = true;
            Assert.That(ArmyRadioChatter.SandstormCategory(army), Is.Null);
            army.IsAirfield = false; army.CarrierUnit = new UnitData();
            Assert.That(ArmyRadioChatter.SandstormCategory(army), Is.Null);
        }

        [Test]
        public void WeatherReportsUseTheExistingBoundedDispatchAndExpire()
        {
            var army = Army(_owner, 1);
            var category = Category("GroundSandstorm");
            var dispatch = new ArmyRadioDispatch();
            var identity = new object();
            Assert.That(dispatch.Add(new ArmyRadioCandidate(category, army, _owner, identity, army.Hex, 0f, 6f), 0f, 24), Is.True);
            Assert.That(dispatch.Add(new ArmyRadioCandidate(category, army, _owner, identity, army.Hex, 1f, 6f), 1f, 24), Is.False);
            dispatch.Prune(6f, null);
            Assert.That(dispatch.PendingCount, Is.Zero);
        }

        [Test]
        public void EveryDispatchCategoryResolvesToItsOwnPhraseTable()
        {
            foreach (ArmyRadioCategory category in Enum.GetValues(typeof(ArmyRadioCategory)))
            {
                if (ArmyRadioDispatch.Operational(category))
                    Assert.That(ArmyRadioChatter.OperationalPhrase(category, -1), Is.Not.Null.And.Not.Empty);
            }
            foreach (ArmyRadioSituation situation in Enum.GetValues(typeof(ArmyRadioSituation)))
            {
                Assert.That(Enum.TryParse(situation.ToString(), out ArmyRadioCategory category), Is.True);
                Assert.That((int)category, Is.EqualTo((int)ArmyRadioCategory.Contact + (int)situation));
                Assert.That(ArmyRadioChatter.Phrase(situation, -1), Is.Not.Null.And.Not.Empty);
            }
        }
    }
}
#endif
