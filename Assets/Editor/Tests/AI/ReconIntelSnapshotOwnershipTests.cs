#if UNITY_INCLUDE_TESTS
using System;
using System.Collections.Generic;
using System.Reflection;
using Game.Ai.V2;
using Game.Ai;
using Game.Map;
using Game.HexGrid;
using Game.Players;
using NUnit.Framework;

namespace Game.EditorTests
{
    public sealed class ReconIntelSnapshotOwnershipTests
    {
        private static Type RegistryType => typeof(WorldSnapshot).Assembly.GetType(
            "Game.Ai.V2.ReconIntelSnapshotRegistry", throwOnError: true);

        private static void Clear()
        {
            RegistryType.GetMethod("Clear", BindingFlags.Public | BindingFlags.Static)
                ?.Invoke(null, null);
        }

        private static void Capture(PlayerSetupData player, int turn,
            IReadOnlyDictionary<HexCoord, int> observed, int knowledgeVersion = 0)
        {
            MethodInfo method = RegistryType.GetMethod("Capture",
                BindingFlags.Public | BindingFlags.Static);
            Assert.That(method, Is.Not.Null);
            method.Invoke(null, new object[] { player, turn, knowledgeVersion, observed });
        }

        private static IReadOnlyDictionary<HexCoord, int> Read(WorldSnapshot snapshot)
        {
            MethodInfo method = RegistryType.GetMethod("LastObservedFor",
                BindingFlags.Public | BindingFlags.Static);
            Assert.That(method, Is.Not.Null);
            return (IReadOnlyDictionary<HexCoord, int>)method.Invoke(null, new object[] { snapshot });
        }

        [SetUp]
        public void SetUp() => Clear();

        [TearDown]
        public void TearDown() => Clear();

        [Test]
        public void EmptySnapshotsRemainStrictlyPlayerScopedByExplicitObserver()
        {
            var a = new PlayerSetupData();
            var b = new PlayerSetupData();
            var hexA = new HexCoord(7, -3);
            var hexB = new HexCoord(-4, 9);
            Capture(a, 12, new Dictionary<HexCoord, int> { { hexA, 9 } });
            Capture(b, 12, new Dictionary<HexCoord, int> { { hexB, 11 } });

            var snapA = new WorldSnapshot { Observer = a, TurnNumber = 12 };
            var snapB = new WorldSnapshot { Observer = b, TurnNumber = 12 };

            IReadOnlyDictionary<HexCoord, int> readA = Read(snapA);
            IReadOnlyDictionary<HexCoord, int> readB = Read(snapB);
            Assert.That(readA.ContainsKey(hexA), Is.True);
            Assert.That(readA.ContainsKey(hexB), Is.False);
            Assert.That(readB.ContainsKey(hexB), Is.True);
            Assert.That(readB.ContainsKey(hexA), Is.False);
        }

        [Test]
        public void MissingObserverNeverFallsBackToOtherPlayersCache()
        {
            var player = new PlayerSetupData();
            var hex = new HexCoord(2, 5);
            Capture(player, 4, new Dictionary<HexCoord, int> { { hex, 3 } });

            var unowned = new WorldSnapshot { TurnNumber = 4 };
            Assert.That(Read(unowned).Count, Is.Zero);
        }

        [Test]
        public void EachKnowledgeRevisionKeepsItsOwnCopyWithinOneTurn()
        {
            var player = new PlayerSetupData();
            var hexA = new HexCoord(1, 1);
            var hexB = new HexCoord(3, -2);
            Capture(player, 5, new Dictionary<HexCoord, int> { { hexA, 2 } }, knowledgeVersion: 10);
            var snapshotA = new WorldSnapshot { Observer = player, TurnNumber = 5, KnowledgeVersion = 10 };
            Capture(player, 5, new Dictionary<HexCoord, int> { { hexA, 5 }, { hexB, 5 } },
                knowledgeVersion: 11);
            var snapshotB = new WorldSnapshot { Observer = player, TurnNumber = 5, KnowledgeVersion = 11 };

            IReadOnlyDictionary<HexCoord, int> readA = Read(snapshotA);
            Assert.That(readA[hexA], Is.EqualTo(2), "V10 keeps its own value after V11 is captured");
            Assert.That(readA.ContainsKey(hexB), Is.False, "V10 never sees V11 data");
            IReadOnlyDictionary<HexCoord, int> readB = Read(snapshotB);
            Assert.That(readB[hexA], Is.EqualTo(5));
            Assert.That(readB.ContainsKey(hexB), Is.True);

            var uncaptured = new WorldSnapshot { Observer = player, TurnNumber = 5, KnowledgeVersion = 12 };
            Assert.That(Read(uncaptured).Count, Is.Zero, "no fallback to the latest revision");
        }

        [Test]
        public void CaptureCopiesInputAndRepeatedCaptureKeepsPublishedRevision()
        {
            var player = new PlayerSetupData();
            var hex = new HexCoord(2, 1);
            var input = new Dictionary<HexCoord, int> { [hex] = 3 };
            Capture(player, 7, input, knowledgeVersion: 11);
            var snapshot = new WorldSnapshot { Observer = player, TurnNumber = 7, KnowledgeVersion = 11 };
            input[hex] = 7;
            Assert.That(Read(snapshot)[hex], Is.EqualTo(3), "capture detaches the writer's dictionary");
            Capture(player, 7, input, knowledgeVersion: 11);
            Assert.That(Read(snapshot)[hex], Is.EqualTo(3), "the same snapshot identity remains frozen");
        }

        [Test]
        public void ReaderCannotModifyPublishedRevision()
        {
            var player = new PlayerSetupData();
            var hex = new HexCoord(2, 1);
            Capture(player, 7, new Dictionary<HexCoord, int> { [hex] = 3 });
            var snapshot = new WorldSnapshot { Observer = player, TurnNumber = 7 };
            var writable = Read(snapshot) as IDictionary<HexCoord, int>;
            if (writable != null)
            {
                Assert.That(writable.IsReadOnly, Is.True);
                Assert.Throws<NotSupportedException>(() => writable[hex] = 7);
            }
            Assert.That(Read(snapshot)[hex], Is.EqualTo(3));
        }

        [Test]
        public void EmptyHexObservationInvalidatesKnowledgeOnlyWhenStampChanges()
        {
            var player = new PlayerSetupData();
            var other = new PlayerSetupData();
            var hex = new HexCoord(30, 30);
            var visible = (Dictionary<PlayerSetupData, HashSet<HexCoord>>)typeof(VisionSystem)
                .GetField("Visible", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
            visible[player] = new HashSet<HexCoord> { hex };
            try
            {
                AiReconIntelMemory.Clear();
                int otherVersion = AiMapMemory.KnowledgeVersionFor(other);
                int version = AiMapMemory.KnowledgeVersionFor(player);
                AiReconIntelMemory.ObserveCurrentVisibility(player, 6);
                Assert.That(AiMapMemory.KnowledgeVersionFor(player), Is.GreaterThan(version));
                int observedVersion = AiMapMemory.KnowledgeVersionFor(player);
                AiReconIntelMemory.ObserveCurrentVisibility(player, 6);
                Assert.That(AiMapMemory.KnowledgeVersionFor(player), Is.EqualTo(observedVersion),
                    "same-turn repeats do not invalidate the cache");
                AiReconIntelMemory.ObserveCurrentVisibility(player, 7);
                Assert.That(AiMapMemory.KnowledgeVersionFor(player), Is.GreaterThan(observedVersion));
                Assert.That(AiMapMemory.KnowledgeVersionFor(other), Is.EqualTo(otherVersion));
                Assert.That(AiReconIntelMemory.TryGetIntelAge(player, hex, 7, out int age), Is.True);
                Assert.That(age, Is.Zero);
                Assert.That(VisionSystem.IsVisited(player, hex), Is.False);
            }
            finally
            {
                visible.Remove(player);
                AiReconIntelMemory.Clear();
            }
        }

        [Test]
        public void VisionFootprintChangeInvalidatesKnowledgeWithoutInvalidatingRoutes()
        {
            var player = new PlayerSetupData();
            int knowledge = AiMapMemory.KnowledgeVersionFor(player);
            long routes = AiMapMemory.RouteMemoryVersionFor(player);
            // VisionSystem publishes this event for visited-set changes too. No visible content
            // is necessary: visiting an already-observed empty hex must invalidate MapKnowledge.
            typeof(AiMapMemory).GetMethod("OnVisionStateChanged",
                BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, new object[] { player });
            Assert.That(AiMapMemory.KnowledgeVersionFor(player), Is.GreaterThan(knowledge));
            Assert.That(AiMapMemory.RouteMemoryVersionFor(player), Is.EqualTo(routes));
        }

        [Test]
        public void PreviousTurnRevisionIsNeverServed()
        {
            var player = new PlayerSetupData();
            var hex = new HexCoord(0, 2);
            Capture(player, 4, new Dictionary<HexCoord, int> { { hex, 4 } }, knowledgeVersion: 7);
            Capture(player, 5, new Dictionary<HexCoord, int>(), knowledgeVersion: 8);

            var old = new WorldSnapshot { Observer = player, TurnNumber = 4, KnowledgeVersion = 7 };
            Assert.That(Read(old).Count, Is.Zero, "a new turn drops the previous turn's revisions");
        }
    }
}
#endif
