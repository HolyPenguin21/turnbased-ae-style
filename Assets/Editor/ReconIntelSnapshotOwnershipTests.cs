#if UNITY_INCLUDE_TESTS
using System;
using System.Collections.Generic;
using System.Reflection;
using Game.Ai.V2;
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
            IReadOnlyDictionary<HexCoord, int> observed)
        {
            MethodInfo method = RegistryType.GetMethod("Capture",
                BindingFlags.Public | BindingFlags.Static);
            Assert.That(method, Is.Not.Null);
            method.Invoke(null, new object[] { player, turn, observed });
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
    }
}
#endif
