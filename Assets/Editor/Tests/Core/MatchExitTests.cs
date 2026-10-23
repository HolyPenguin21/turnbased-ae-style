#if UNITY_INCLUDE_TESTS
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Game.Ai;
using Game.Ai.V2;
using Game.Campaign;
using Game.Combat;
using Game.Core;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Setup;
using Game.Terrain;
using Game.Units;
using NUnit.Framework;

namespace Game.EditorTests
{
    public sealed class MatchExitTests
    {
        private string directory;
        private string Summary => Path.Combine(directory, "summary.txt");
        private string Turns => Path.Combine(directory, "turns.csv");

        [SetUp]
        public void SetUp()
        {
            MatchStateInventory.EndMatch();
            directory = Path.Combine(Path.GetTempPath(), "match-exit-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
        }

        [TearDown]
        public void TearDown()
        {
            MatchStateInventory.EndMatch();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }

        private static FieldInfo Stats(string name) => typeof(AiMatchStats)
            .GetField(name, BindingFlags.Static | BindingFlags.NonPublic);

        // Supply an isolated sink without BeginMatch opening the user's real debug files.
        private void SeedStatistics(PlayerSetupData player)
        {
            var entries = (IDictionary)Stats("ByPlayer").GetValue(null);
            Type rowType = entries.GetType().GetGenericArguments()[1];
            entries.Add(player, Activator.CreateInstance(rowType, true));
            ((IList)Stats("Order").GetValue(null)).Add(player);
            Stats("_active").SetValue(null, true);
            Stats("_turn").SetValue(null, 7);
            Stats("_summaryPath").SetValue(null, Summary);
            Stats("_turnsWriter").SetValue(null, new StreamWriter(Turns));
        }

        [Test]
        public void ExitCapturesTheLiveWorldThenClearsWorldAndSession()
        {
            var player = new PlayerSetupData { Nickname = "Old match", IsHuman = true };
            var players = new List<PlayerSetupData> { player };
            GameSession.Players = players;
            GameSession.SetCampaignContext(new CampaignMatchContext { MatchId = "old-match" });
            GameSession.CompleteMatch(new CompletedMatchResult("old-match", Faction.Ashen, Faction.Vessels, false));
            typeof(GameSession).GetProperty("RewardsEligible").SetValue(null, true);
            GameSession.SelectedMapSize = MapSize.Small;
            GameSession.SelectedBiome = Biome.Desert;
            GameSession.SetupError = "old setup error";
            var hex = new HexCoord(1, 1);
            var army = new ArmyData { Owner = player, Hex = hex };
            army.Members.Add(new UnitData { Owner = player });
            ArmyRegistry.Register(army);
            BuildingRegistry.Register(hex, new BuildingData { Owner = player, Hex = hex, IsBase = true });
            VisionSystem.CurrentViewer = player;
            SeedStatistics(player);

            MatchStateInventory.EndMatch();

            string summary = File.ReadAllText(Summary);
            StringAssert.Contains("status: stopped (returned from match)", summary);
            StringAssert.Contains("fieldArmies=1  units=1  bases=1", summary,
                "the snapshot must precede the world reset");
            Assert.That(ArmyRegistry.AllAt(hex), Is.Empty);
            Assert.That(BuildingRegistry.FindAt(hex), Is.Null);
            Assert.That(VisionSystem.CurrentViewer, Is.Null);
            Assert.That(GameSession.Players, Is.Empty);
            Assert.That(GameSession.Players, Is.Not.SameAs(players));
            Assert.That(players.Count, Is.EqualTo(1), "release the session reference without mutating other owners' lists");
            Assert.That(GameSession.MatchId, Is.Null);
            Assert.That(GameSession.CampaignContext, Is.Null);
            Assert.That(GameSession.FinalResult, Is.Null);
            Assert.That(GameSession.RewardsEligible, Is.False);
            Assert.That(GameSession.SelectedMapSize, Is.Null);
            Assert.That(GameSession.SelectedBiome, Is.Null);
            Assert.That(GameSession.SelectedResourceConditions, Is.Null);
            Assert.That(GameSession.SetupError, Is.Null);
            Assert.That(Stats("_active").GetValue(null), Is.False);
            Assert.That(((IDictionary)Stats("ByPlayer").GetValue(null)).Count, Is.Zero);
            Assert.That(((IList)Stats("Order").GetValue(null)).Count, Is.Zero);
            Assert.That(Stats("_turnsWriter").GetValue(null), Is.Null);
            using (File.Open(Turns, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void RepeatedExitPreservesTheAlreadyWrittenOutcome(bool gameOver)
        {
            var player = new PlayerSetupData { Nickname = "Winner" };
            SeedStatistics(player);
            if (gameOver) AiMatchStats.RecordGameOver(player);
            MatchStateInventory.EndMatch();
            string summary = File.ReadAllText(Summary);
            StringAssert.Contains(gameOver ? "game over - Winner wins" : "stopped (returned from match)", summary);
            MatchStateInventory.EndMatch();
            AiMatchStats.OnRoundStarted(100);
            Assert.That(File.ReadAllText(Summary), Is.EqualTo(summary));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void RecipientStoresAreReleasedOnBothExitAndNewMatch(bool exiting)
        {
            Type memo = typeof(DevelopmentOpportunityEvaluator).GetNestedType("RecipientEvaluationMemo", BindingFlags.NonPublic);
            MethodInfo storeFor = memo.GetMethod("StoreFor", BindingFlags.Static | BindingFlags.NonPublic);
            var stores = (IDictionary)memo.GetField("s_stores", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
            var player = new PlayerSetupData();
            object old = storeFor.Invoke(null, new object[] { player, "same-inputs" });
            Assert.That(storeFor.Invoke(null, new object[] { player, "same-inputs" }), Is.SameAs(old));
            if (exiting) MatchStateInventory.EndMatch();
            else MatchStateInventory.ResetForNewMatch();
            Assert.That(stores.Count, Is.Zero);
            Assert.That(storeFor.Invoke(null, new object[] { player, "same-inputs" }), Is.Not.SameAs(old));
        }

        [Test]
        public void ExitClosesAnAbandonedAiSessionAndItsReservations()
        {
            var player = new PlayerSetupData();
            var old = AiTurnSession.Begin(player, null, null, null, 8);
            StrategicResourceReservationLedger.Upsert(player, 8, new StrategicResourceReservation
            {
                Owner = "exit-test", Resource = StrategicReservedResource.Materials, Amount = 3,
                Reason = StrategicReservationReason.StrategicReactionPass,
                ExpirationStage = StrategicReservationExpiry.EndOfTurn,
            });
            Assert.That(StrategicResourceReservationLedger.HasAny(player, 8), Is.True);

            MatchStateInventory.EndMatch();

            Assert.That(AiTurnSession.PeekActive(player), Is.Null);
            Assert.That(StrategicResourceReservationLedger.Rows(player, 8), Is.Empty);
            Assert.Throws<ObjectDisposedException>(() => old.Apply(default));
            using var next = AiTurnSession.Begin(player, null, null, null, 8);
            old.Dispose();
            Assert.That(AiTurnSession.PeekActive(player), Is.SameAs(next));
        }

        [Test]
        public void ExitClosesTheCombatCacheWithoutLettingAnOldLeaseCloseANewOne()
        {
            var old = WorthIt.OpenEstimateCacheScope();
            Assert.That(WorthIt.EstimateCacheActive, Is.True);
            MatchStateInventory.EndMatch();
            Assert.That(WorthIt.EstimateCacheActive, Is.False);
            using var next = WorthIt.OpenEstimateCacheScope();
            old.Dispose();
            Assert.That(WorthIt.EstimateCacheActive, Is.True);
        }

        [Test]
        public void MatchResetReleasesTheOldScenesStealthProviders()
        {
            var player = new PlayerSetupData();
            StealthSystem.CompletedTurnsProvider = p => p == player ? 37 : 0;
            StealthSystem.ObserverTakingTurnProvider = p => p == player;
            StealthSystem.TerrainMoveCostProvider = h => h.Equals(new HexCoord(1, 1)) ? 5 : 2;
            var roller = StealthSystem.ChallengeRoller;

            MatchStateInventory.EndMatch();

            Assert.That(StealthSystem.CompletedTurnsProvider(player), Is.Zero);
            Assert.That(StealthSystem.ObserverTakingTurnProvider(player), Is.False);
            Assert.That(StealthSystem.TerrainMoveCostProvider(new HexCoord(1, 1)), Is.EqualTo(1));
            Assert.That(StealthSystem.ChallengeRoller, Is.SameAs(roller));
        }
    }
}
#endif
