#if UNITY_INCLUDE_TESTS
using System.Linq;
using Game.Ai;
using Game.Ai.V2;
using Game.Combat;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using NUnit.Framework;

namespace Game.EditorTests
{
    // Match 2026-11-13 Sable vs Korrin (no fight T39-T52): an enemy field army seen two turns ago
    // four hexes away read ETA 0 to the Base whose whole neighbourhood Sable saw, and pinned that
    // Base's field army. The one unobserved-army model (AiReconMemory.PossibleDistance) bounds how
    // near an unseen contact can be by what the observer sees now.
    public class AiMatch20261113StalemateTests
    {
        [SetUp]
        [TearDown]
        public void Reset()
        {
            AiReconMemory.Clear();
            ArmyRegistry.Clear();
            BuildingRegistry.Clear();
            VisionSystem.Clear();
        }

        // A building gives its owner sight of its hex and the ring around it (radius 1).
        private static PlayerSetupData ObserverSeeing(HexCoord hex)
        {
            var player = new PlayerSetupData { Nickname = "Sable" };
            BuildingRegistry.Register(hex, new BuildingData { Owner = player, Hex = hex });
            VisionSystem.RecomputeFor(player);
            return player;
        }

        [Test]
        public void UnseenContact_CannotStandInsideTheObserversSight()
        {
            var home = new HexCoord(0, 0);
            var lastSeen = new HexCoord(4, 0);
            PlayerSetupData sable = ObserverSeeing(home);

            Assert.That(AiReconMemory.PossibleDistance(null, lastSeen, 2, 2, false, home), Is.Zero,
                "the bare advance bound: 4 hexes minus 2 turns x 2 MP");
            Assert.That(AiReconMemory.PossibleDistance(sable, lastSeen, 2, 2, false, home), Is.EqualTo(2),
                "the seen ring (radius 1) holds no hidden army: the nearest unseen hex is 2 away");
            Assert.That(AiReconMemory.PossibleDistance(sable, lastSeen, 2, 0, false, home), Is.EqualTo(4),
                "a current observation is its own distance");
            Assert.That(AiReconMemory.PossibleDistance(sable, lastSeen, 1, 1, false, home), Is.EqualTo(3),
                "the advance bound stays when it is the larger one");
            System.Func<HexCoord, bool, bool> onlyFarSide = (h, air) => h.Q >= 3;
            Assert.That(AiReconMemory.PossibleDistance(sable, lastSeen, 2, 2, false, home, onlyFarSide),
                Is.EqualTo(3), "only enterable hexes can hide it");
        }

        // A fully stealthy army may stand unseen on a hex in sight: sight bounds nothing for it,
        // and an empty last hex in sight is no proof it is gone.
        [Test]
        public void StealthyContact_IsNotBoundedBySight()
        {
            var home = new HexCoord(0, 0);
            PlayerSetupData sable = ObserverSeeing(home);
            var stealthy = new[] { new WorthIt.DefenderProfile(1f, false, attack: 2f, hitPoints: 4f,
                abilities: new[] { Game.Cards.UnitAbilities.Stealth4 }) };
            var plain = new[] { new WorthIt.DefenderProfile(1f, false, attack: 2f, hitPoints: 4f) };
            Assert.That(AiReconMemory.HidesInSight(stealthy), Is.True);
            Assert.That(AiReconMemory.HidesInSight(plain), Is.False);
            Assert.That(AiReconMemory.HidesInSight(stealthy.Concat(plain)), Is.False,
                "one visible member gives the army away");
            Assert.That(AiReconMemory.PossibleDistance(sable, new HexCoord(4, 0), 2, 2, false, home,
                hidesInSight: true), Is.Zero);

            System.Func<HexCoord, bool, bool> onlyRing = (h, air) => HexGridMath.Distance(h, home) <= 1;
            var seen = new ReconObservation { ArmyId = 31, Owner = new PlayerSetupData(), LastObservedHex = home,
                LastObservedTurn = 20, MaxMovement = 2, Defenders = plain };
            Assert.That(AiReconMemory.GoneOnReobservation(sable, 21, seen, onlyRing), Is.True);
            seen.Defenders = stealthy;
            Assert.That(AiReconMemory.GoneOnReobservation(sable, 21, seen, onlyRing), Is.False);
        }

        // Buildings on every hex within `radius - 1` give sight of the disc of `radius`.
        private static PlayerSetupData ObserverSeeingDisc(HexCoord center, int radius)
        {
            var player = new PlayerSetupData { Nickname = "Sable" };
            foreach (HexCoord h in HexGridMath.HexesInRange(center, radius - 1))
                BuildingRegistry.Register(h, new BuildingData { Owner = player, Hex = h });
            VisionSystem.RecomputeFor(player);
            return player;
        }

        [Test]
        public void GarrisonThreatFloor_ReadsTheSameUnobservedArmyModel()
        {
            var home = new HexCoord(0, 0);
            PlayerSetupData sable = ObserverSeeingDisc(home, 4);
            var enemy = new AiMapMemory.KnownEnemySighting(new HexCoord(8, 0), new PlayerSetupData(),
                "Cold Blood", 1, 3f, 5f, new[] { new WorthIt.DefenderProfile(3f, false, attack: 5f, hitPoints: 5f) },
                armyId: 24, maxMovement: 2);
            var contacts = new[] { (enemy, 3) };

            Assert.That(AiArmyRoles.GarrisonThreatFloor(contacts, new[] { home }, home, false), Is.GreaterThan(0f),
                "without an observer: 8 hexes <= 2 MP x (2 + 3 unseen turns)");
            Assert.That(AiReconMemory.PossibleDistance(sable, enemy.Hex, 2, 3, false, home), Is.EqualTo(5),
                "the seen disc of radius 4 holds no hidden army");
            Assert.That(AiReconMemory.PossibleDistance(sable, enemy.Hex, 2, 3, false, home, limit: 4), Is.EqualTo(5),
                "a scan limited to 4 reads anything farther as 5");
            Assert.That(AiReconMemory.PossibleDistance(sable, enemy.Hex, 2, 3, false, home, limit: 6), Is.EqualTo(5),
                "within the limit the answer is exact");
            Assert.That(AiArmyRoles.GarrisonThreatFloor(contacts, new[] { home }, home, false, sable), Is.Zero,
                "5 hexes is beyond 2 MP x 2 turns: the unseen army cannot reach the garrison in time");
        }
    }
}
#endif
