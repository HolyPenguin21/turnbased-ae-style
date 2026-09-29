#if UNITY_INCLUDE_TESTS
using System.Collections.Generic;
using Game.Ai;
using Game.Combat;
using Game.Core;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Units;
using NUnit.Framework;

namespace Game.EditorTests
{
    public sealed class AiCombatCacheLifecycleTests
    {
        [SetUp]
        public void SetUp()
        {
            ArmyRegistry.Clear();
            BuildingRegistry.Clear();
            VisionSystem.Clear();
            AiMapMemory.Clear();
            GameSession.Players = new List<PlayerSetupData>();
            AiMapMemory.EnsureSubscribed();
        }

        [TearDown]
        public void TearDown()
        {
            ArmyRegistry.Clear();
            BuildingRegistry.Clear();
            VisionSystem.Clear();
            AiMapMemory.Clear();
            GameSession.Players = new List<PlayerSetupData>();
        }

        [Test]
        public void PreBattleVisibleContact_IsWrittenAndReadableFromHonestMemory()
        {
            var observer = new PlayerSetupData { Nickname = "Observer" };
            var enemyOwner = new PlayerSetupData { Nickname = "Enemy" };
            HexCoord hex = new HexCoord(2, -1);

            ArmyData own = Army(observer, hex, "Own", hp: 4);
            ArmyData enemy = Army(enemyOwner, hex, "Enemy", hp: 5);

            ArmyRegistry.Register(own);
            int knowledgeBeforeEnemy = AiMapMemory.KnowledgeVersionFor(observer);
            long routeBeforeEnemy = AiMapMemory.RouteMemoryVersionFor(observer);

            ArmyRegistry.Register(enemy);

            AiMapMemory.KnownEnemySighting? sighting =
                AiMapMemory.KnownEnemySightingAt(observer, hex);
            Assert.That(sighting.HasValue, Is.True);
            Assert.That(sighting.Value.ArmyId, Is.EqualTo(enemy.Id));
            Assert.That(sighting.Value.MemberCount, Is.EqualTo(1));
            Assert.That(sighting.Value.Defenders.Count, Is.EqualTo(1));
            Assert.That(sighting.Value.Defenders[0].HitPoints, Is.EqualTo(5f));

            AiMapMemory.GroundArrival arrival =
                AiMapMemory.KnownGroundArrival(observer, hex, moverFullyHidden: false);
            Assert.That(arrival.Contact, Is.True,
                "execution's pre-battle authorization must read the same visible contact memory");
            Assert.That(AiMapMemory.KnowledgeVersionFor(observer), Is.Not.EqualTo(knowledgeBeforeEnemy));
            Assert.That(AiMapMemory.RouteMemoryVersionFor(observer), Is.Not.EqualTo(routeBeforeEnemy),
                "a newly known blocker must invalidate this observer's route cache");
        }

        [Test]
        public void PostBattleHpWrite_RefreshesStrategicKnowledgeButNotRouteBlockers()
        {
            var observer = new PlayerSetupData { Nickname = "Observer" };
            var enemyOwner = new PlayerSetupData { Nickname = "Enemy" };
            var uninvolved = new PlayerSetupData { Nickname = "Uninvolved" };
            HexCoord hex = new HexCoord(3, -2);

            ArmyData own = Army(observer, hex, "Own", hp: 4);
            ArmyData enemy = Army(enemyOwner, hex, "Enemy", hp: 5);
            ArmyRegistry.Register(own);
            ArmyRegistry.Register(enemy);

            int knowledgeBefore = AiMapMemory.KnowledgeVersionFor(observer);
            long routeBefore = AiMapMemory.RouteMemoryVersionFor(observer);
            int uninvolvedKnowledgeBefore = AiMapMemory.KnowledgeVersionFor(uninvolved);
            long uninvolvedRouteBefore = AiMapMemory.RouteMemoryVersionFor(uninvolved);

            enemy.Members[0].HitPointsCurrent = 2;
            VisionSystem.NotifyContentChanged(hex);

            AiMapMemory.KnownEnemySighting? sighting =
                AiMapMemory.KnownEnemySightingAt(observer, hex);
            Assert.That(sighting.HasValue, Is.True);
            Assert.That(sighting.Value.Defenders[0].HitPoints, Is.EqualTo(2f),
                "post-battle HP must be re-written into honest memory for a player still watching");
            Assert.That(AiMapMemory.KnowledgeVersionFor(observer), Is.Not.EqualTo(knowledgeBefore),
                "combat-stat changes must invalidate the strategic knowledge snapshot");
            Assert.That(AiMapMemory.RouteMemoryVersionFor(observer), Is.EqualTo(routeBefore),
                "same enemy on same hex is still the same route blocker");

            Assert.That(AiMapMemory.KnowledgeVersionFor(uninvolved), Is.EqualTo(uninvolvedKnowledgeBefore),
                "a player with no vision of the battle must not receive a cache write");
            Assert.That(AiMapMemory.RouteMemoryVersionFor(uninvolved), Is.EqualTo(uninvolvedRouteBefore));
        }

        [Test]
        public void PostBattleDestruction_RemovesSightingAndInvalidatesRouteCache()
        {
            var observer = new PlayerSetupData { Nickname = "Observer" };
            var enemyOwner = new PlayerSetupData { Nickname = "Enemy" };
            HexCoord hex = new HexCoord(4, -3);

            ArmyData own = Army(observer, hex, "Own", hp: 4);
            ArmyData enemy = Army(enemyOwner, hex, "Enemy", hp: 1);
            ArmyRegistry.Register(own);
            ArmyRegistry.Register(enemy);

            Assert.That(AiMapMemory.KnownEnemySightingAt(observer, hex).HasValue, Is.True);
            int knowledgeBefore = AiMapMemory.KnowledgeVersionFor(observer);
            long routeBefore = AiMapMemory.RouteMemoryVersionFor(observer);

            // Mirrors BattleEngine's post-combat ordering: roster reaches terminal state first,
            // then the still-visible hex publishes its final content before an empty shell is
            // unregistered and may remove the loser's vision.
            enemy.Members.Clear();
            VisionSystem.NotifyContentChanged(hex);

            Assert.That(AiMapMemory.KnownEnemySightingAt(observer, hex).HasValue, Is.False);
            Assert.That(AiMapMemory.KnownGroundArrival(
                observer, hex, moverFullyHidden: false).Contact, Is.False);
            Assert.That(AiMapMemory.KnowledgeVersionFor(observer), Is.Not.EqualTo(knowledgeBefore));
            Assert.That(AiMapMemory.RouteMemoryVersionFor(observer), Is.Not.EqualTo(routeBefore),
                "removing a known hostile blocker must invalidate cached safe routes");
        }

        [Test]
        public void FinalPostBattleWrite_PersistsForLosingObserverAfterVisionDrops()
        {
            var loser = new PlayerSetupData { Nickname = "Loser" };
            var winner = new PlayerSetupData { Nickname = "Winner" };
            HexCoord hex = new HexCoord(5, -4);

            ArmyData losingArmy = Army(loser, hex, "LoserArmy", hp: 1);
            ArmyData winningArmy = Army(winner, hex, "WinnerArmy", hp: 5);
            ArmyRegistry.Register(losingArmy);
            ArmyRegistry.Register(winningArmy);

            winningArmy.Members[0].HitPointsCurrent = 2;
            losingArmy.Members.Clear();

            // BattleEngine.FinalizeEncounter intentionally publishes this while the empty losing
            // shell is still registered, so its owner has one last honest observation.
            VisionSystem.NotifyContentChanged(hex);
            AiMapMemory.KnownEnemySighting? finalSeen =
                AiMapMemory.KnownEnemySightingAt(loser, hex);
            Assert.That(finalSeen.HasValue, Is.True);
            Assert.That(finalSeen.Value.Defenders[0].HitPoints, Is.EqualTo(2f));

            ArmyRegistry.Unregister(losingArmy);
            Assert.That(VisionSystem.IsVisible(loser, hex), Is.False);
            AiMapMemory.KnownEnemySighting? fogMemory =
                AiMapMemory.KnownEnemySightingAt(loser, hex);
            Assert.That(fogMemory.HasValue, Is.True,
                "losing vision after finalization must not erase the last honestly observed survivor");
            Assert.That(fogMemory.Value.Defenders[0].HitPoints, Is.EqualTo(2f));
        }

        [Test]
        public void BaseCapture_RewritesKnownOwnerAndInvalidatesRouteBlocker()
        {
            var observer = new PlayerSetupData { Nickname = "Observer" };
            var enemyOwner = new PlayerSetupData { Nickname = "Enemy" };
            HexCoord hex = new HexCoord(6, -5);

            ArmyData own = Army(observer, hex, "Own", hp: 4);
            ArmyRegistry.Register(own);

            var building = new BuildingData
            {
                Hex = hex,
                Owner = enemyOwner,
                Name = "Enemy Base",
                IsBase = true,
                Defense = 2,
            };
            BuildingRegistry.Register(hex, building);

            AiMapMemory.KnownBuilding? before = AiMapMemory.KnownBuildingAt(observer, hex);
            Assert.That(before.HasValue, Is.True);
            Assert.That(before.Value.Owner, Is.SameAs(enemyOwner));
            Assert.That(AiMapMemory.KnownHexDefenseBonusFor(observer, hex, enemyOwner), Is.EqualTo(2f));
            Assert.That(AiMapMemory.KnownHexDefenseBonusFor(observer, hex, observer), Is.Zero);
            long routeBefore = AiMapMemory.RouteMemoryVersionFor(observer);

            BuildingRegistry.CaptureOrDestroy(building, observer, hexSelection: null);

            AiMapMemory.KnownBuilding? after = AiMapMemory.KnownBuildingAt(observer, hex);
            Assert.That(after.HasValue, Is.True);
            Assert.That(after.Value.Owner, Is.SameAs(observer));
            Assert.That(AiMapMemory.KnownHexDefenseBonusFor(observer, hex, enemyOwner), Is.Zero);
            Assert.That(AiMapMemory.KnownHexDefenseBonusFor(observer, hex, observer), Is.EqualTo(2f));
            Assert.That(AiMapMemory.RouteMemoryVersionFor(observer), Is.Not.EqualTo(routeBefore),
                "foreign-structure blocker becoming owned must invalidate safe-route caches");
        }

        [Test]
        public void FormerOwner_RemembersObservedBaseCaptureAfterBuildingVisionDrops()
        {
            var formerOwner = new PlayerSetupData { Nickname = "Former" };
            var capturer = new PlayerSetupData { Nickname = "Capturer" };
            GameSession.Players = new List<PlayerSetupData> { formerOwner, capturer };
            HexCoord hex = new HexCoord(7, -6);

            var building = new BuildingData
            {
                Hex = hex,
                Owner = formerOwner,
                Name = "Observed Base",
                IsBase = true,
                Defense = 3,
            };
            BuildingRegistry.Register(hex, building);

            Assert.That(VisionSystem.IsVisible(formerOwner, hex), Is.True);
            Assert.That(AiMapMemory.KnownBuildingAt(formerOwner, hex)?.Owner, Is.SameAs(formerOwner));

            ArmyData capturingArmy = Army(capturer, hex, "CapturerArmy", hp: 4);
            ArmyRegistry.Register(capturingArmy);
            BuildingRegistry.CaptureOrDestroy(building, capturer, hexSelection: null);

            Assert.That(VisionSystem.IsVisible(formerOwner, hex), Is.False,
                "former owner should lose building-derived vision after capture");
            AiMapMemory.KnownBuilding? remembered = AiMapMemory.KnownBuildingAt(formerOwner, hex);
            Assert.That(remembered.HasValue, Is.True);
            Assert.That(remembered.Value.Owner, Is.SameAs(capturer),
                "capture was observed before vision dropped, so fog memory must retain the new owner");
        }

        private static ArmyData Army(PlayerSetupData owner, HexCoord hex, string name, int hp)
        {
            var army = new ArmyData
            {
                Owner = owner,
                Hex = hex,
                Name = name,
            };
            army.Members.Add(new UnitData
            {
                Owner = owner,
                Attack = 3,
                Defense = 2,
                Range = 2,
                Initiative = 1,
                HitPointsCurrent = hp,
                HitPointsMax = hp,
            });
            return army;
        }
    }
}
#endif
