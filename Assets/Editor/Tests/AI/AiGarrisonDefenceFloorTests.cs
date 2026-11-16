#if UNITY_INCLUDE_TESTS
using System.Collections.Generic;
using System.Linq;
using Game.Ai;
using NUnit.Framework;

namespace Game.EditorTests
{
    // User decision 2026-09-30 — the garrison defence floor is a share of the ground force
    // (Citadel 10%, Base 5%); one body always stays.
    // Owner decision 2026-11-16 — the release rule judges the departing SET (the kept bodies hold
    // the floor); the strike view (SpareableBodies) takes the strongest body first that still
    // keeps it. The Vex T22 small-shortfall exception is dropped.
    public class AiGarrisonDefenceFloorTests
    {
        // Additive power stands in for AiPower here: the rule only compares set powers.
        private static HashSet<int> Spare(float floor, params float[] bodies) =>
            AiArmyRoles.SpareableBodies(bodies, set => set.Sum(), floor);

        [Test]
        public void Floor_IsTheCitadelAndBaseShareOfTheGroundForce()
        {
            Assert.That(AiArmyRoles.GarrisonDefenceFloor(74f, isCitadel: true), Is.EqualTo(7.4f).Within(1e-4f));
            Assert.That(AiArmyRoles.GarrisonDefenceFloor(74f, isCitadel: false), Is.EqualTo(3.7f).Within(1e-4f));
        }

        // Vex T22 (exception dropped 2026-11-16): floor 7.4, Ash Drifter 6.6 + MT Rust Tank 11.
        // The tank leaving would leave 6.6 < 7.4, so the floor keeps it; Ash Drifter may go.
        [Test]
        public void HardFloor_TheTankStays_WhenItsDepartureBreaksTheFloor()
        {
            Assert.That(Spare(7.4f, 6.6f, 11f), Is.EquivalentTo(new[] { 0 }));
        }

        // Match 2026-11-16, Cassia T27, Base (2,-1) (stat-line powers): the old kept-set search
        // released Mule + RC Vehicle and kept Sandnought. The strongest body that keeps the floor
        // goes first: Sandnought; after it no other body may leave.
        [Test]
        public void StrongestBodyThatKeepsTheFloor_GoesFirst()
        {
            // MedInf, ATInf, Coil, Mule, MedInf, RC, Sandnought
            Assert.That(Spare(48.6f, 8.25f, 8.25f, 9.5f, 9.45f, 8.25f, 8.85f, 13.9f),
                Is.EquivalentTo(new[] { 6 }));
        }

        [Test]
        public void ReleaseRule_JudgesTheWholeSet()
        {
            float Sum(IEnumerable<float> s) => s.Sum();
            var bodies = new List<float> { 3f, 6f, 3f, 12f };
            Assert.That(AiArmyRoles.MayReleaseBodies(bodies, new[] { 3 }, Sum, 4f), Is.True);
            Assert.That(AiArmyRoles.MayReleaseBodies(bodies, new[] { 3, 1 }, Sum, 4f), Is.True);
            Assert.That(AiArmyRoles.MayReleaseBodies(bodies, new[] { 3, 1, 0 }, Sum, 4f), Is.False,
                "each body alone may go, the set may not: only 3 would stay");
            Assert.That(AiArmyRoles.MayReleaseBodies(bodies, new[] { 0, 1, 2, 3 }, Sum, 0f), Is.False,
                "one body always stays");
            Assert.That(AiArmyRoles.MayReleaseBodies(bodies, new int[0], Sum, 100f), Is.True);
        }

        [Test]
        public void ExchangeRule_KeepsTheLowerOfHeldAndFloor()
        {
            float Sum(IEnumerable<float> s) => s.Sum();
            var garrison = new List<float> { 8f, 8f, 14f };   // held 30
            Assert.That(AiArmyRoles.KeepsFloorAfterExchange(garrison, 2, 7f, Sum, 22f), Is.True,
                "14 out, 7 in: 23 still holds the floor 22");
            Assert.That(AiArmyRoles.KeepsFloorAfterExchange(garrison, 2, 5f, Sum, 22f), Is.False,
                "14 out, 5 in: 21 < 22");
            Assert.That(AiArmyRoles.KeepsFloorAfterExchange(garrison, 0, 8f, Sum, 40f), Is.True,
                "already below the floor: an exchange may not lower it further, an equal one is fine");
            Assert.That(AiArmyRoles.KeepsFloorAfterExchange(garrison, 0, 7f, Sum, 40f), Is.False);
        }

        [Test]
        public void OneBodyAlwaysStays()
        {
            Assert.That(Spare(0f, 9.3f), Is.Empty);
            Assert.That(Spare(0f, 5f, 6f, 7f).Count, Is.EqualTo(2));
        }

        [Test]
        public void BelowTheFloorEvenWithAllBodies_NothingLeaves()
        {
            Assert.That(Spare(30f, 6.6f, 11f), Is.Empty);
        }

        [Test]
        public void HowManyLeave_IsBoundedByTheStrongestRemainder()
        {
            // floor 10: 12 leaves (11 + 3 hold it), 11 may not (3 alone), then 3 may (11 holds it).
            HashSet<int> spare = Spare(10f, 3f, 12f, 11f);
            Assert.That(spare, Is.EquivalentTo(new[] { 1, 0 }));
        }

        // 2026-10-15 — playtest: Sentinels two turns from Draven's Base (-1,3) while its garrison
        // sat above the 5% share. The known threat raises the floor of the garrison it reaches.
        private static AiMapMemory.KnownEnemySighting Enemy(Game.HexGrid.HexCoord hex, int move,
            bool garrison = false, bool air = false) =>
            new AiMapMemory.KnownEnemySighting(hex, new Game.Players.PlayerSetupData(), "enemy", 1, 3f, 5f,
                new[] { new Game.Combat.WorthIt.DefenderProfile(3f, false, attack: 5f, hitPoints: 5f) },
                armyId: 11, isGarrison: garrison, isAir: air, maxMovement: move);

        [Test]
        public void ThreatFloor_ReachingEnemyRaisesOnlyItsNearestGarrison()
        {
            var baseHex = new Game.HexGrid.HexCoord(-1, 3);
            var citadel = new Game.HexGrid.HexCoord(-1, 5);
            AiMapMemory.KnownEnemySighting sentinels = Enemy(new Game.HexGrid.HexCoord(1, 0), 3);
            float expected = Game.Ai.V2.AiPower.SightingPower(sentinels)
                * Game.Ai.V2.AiConfigV2.aggDefenceConfidenceMargin;

            Assert.That(AiArmyRoles.GarrisonThreatFloor(new[] { sentinels }, new[] { baseHex, citadel }, baseHex),
                Is.EqualTo(expected).Within(1e-4f));
            Assert.That(AiArmyRoles.GarrisonThreatFloor(new[] { sentinels }, new[] { baseHex, citadel }, citadel),
                Is.Zero, "one force contributes once, to the nearest garrison it reaches");
        }

        [Test]
        public void ThreatFloor_IgnoresOutOfReachGarrisonAndAirContacts()
        {
            var baseHex = new Game.HexGrid.HexCoord(-1, 3);
            Assert.That(AiArmyRoles.GarrisonThreatFloor(
                new[] { Enemy(new Game.HexGrid.HexCoord(6, -3), 1) }, new[] { baseHex }, baseHex), Is.Zero);
            Assert.That(AiArmyRoles.GarrisonThreatFloor(
                new[] { Enemy(new Game.HexGrid.HexCoord(1, 0), 3, garrison: true),
                    Enemy(new Game.HexGrid.HexCoord(1, 0), 3, air: true) }, new[] { baseHex }, baseHex), Is.Zero);
        }
        // 2026-11-15 (match Brynn vs Grimm, P2) — an airfield container is remembered as a non-air
        // army moving at aircraft speed; aircraft never take a structure, so it raises no garrison
        // floor and builds no structure threat (WorldAnalysis.TakesStructures, the same rule).
        [Test]
        public void ThreatFloor_AndStructureThreat_IgnoreAnAircraftOnlyContact()
        {
            var baseHex = new Game.HexGrid.HexCoord(-1, 3);
            var aircraft = new Game.Combat.WorthIt.DefenderProfile(3f, false,
                new[] { Game.Cards.UnitTypeTag.Aircraft }, attack: 5f, hitPoints: 5f);
            var airfield = new AiMapMemory.KnownEnemySighting(new Game.HexGrid.HexCoord(1, 0),
                new Game.Players.PlayerSetupData(), "airfield", 1, 3f, 5f, new[] { aircraft },
                armyId: 12, isGarrison: false, isAir: false, maxMovement: 6);
            Assert.That(AiArmyRoles.GarrisonThreatFloor(new[] { airfield }, new[] { baseHex }, baseHex), Is.Zero);
            Assert.That(AiArmyRoles.GarrisonThreatFloor(new[] { Enemy(new Game.HexGrid.HexCoord(1, 0), 3) },
                new[] { baseHex }, baseHex), Is.GreaterThan(0f), "control: a ground force raises it");

            Assert.That(Game.Ai.V2.WorldAnalysis.TakesStructures(new Game.Ai.V2.ArmySnapshot {
                Members = new[] { aircraft } }), Is.False);
            Assert.That(Game.Ai.V2.WorldAnalysis.TakesStructures(new Game.Ai.V2.ArmySnapshot {
                Members = new[] { new Game.Combat.WorthIt.DefenderProfile(3f, false, attack: 5f, hitPoints: 5f) } }),
                Is.True);
        }

        [Test]
        public void LastKnownContactHasTheSameAgeBeforeAndAfterCanonicalMemoryExpires()
        {
            AiMapMemory.Clear(); Game.Ai.V2.AiReconMemory.Clear();
            Game.Map.VisionSystem.Clear();
            var player = new Game.Players.PlayerSetupData();
            var enemy = new Game.Players.PlayerSetupData();
            var hex = new Game.HexGrid.HexCoord(0, 0);
            var profiles = new[] { new Game.Combat.WorthIt.DefenderProfile(3f, false, attack: 5f, hitPoints: 5f) };
            var sighting = new AiMapMemory.KnownEnemySighting(hex, enemy, "remembered", 1, 3f, 5f,
                profiles, seenTurn: 8, armyId: 11, maxMovement: 3);
            try
            {
                Game.Ai.V2.AiReconMemory.Observe(player, 10, 0, new[] { sighting });
                AiMapMemory.RememberForTest(player, sighting);
                float expected = Game.Ai.V2.AiPower.SightingPower(sighting) * 1.3f * (5f / 6f);
                float canonical = AiArmyRoles.GarrisonThreatFloor(player, hex);
                Assert.That(canonical, Is.EqualTo(expected).Within(0.0001f));
                enemy.IsEliminated = true;
                Assert.That(AiArmyRoles.GarrisonThreatFloor(player, hex), Is.Zero,
                    "A defeated owner cannot send this remembered army to our base.");
                enemy.IsEliminated = false;
                AiMapMemory.ForgetSightingsForTest(player);
                Assert.That(AiArmyRoles.GarrisonThreatFloor(player, hex), Is.EqualTo(canonical).Within(0.0001f),
                    "Moving the same observation to Historical must not change its age or confidence.");
                enemy.IsEliminated = true;
                Assert.That(AiArmyRoles.GarrisonThreatFloor(player, hex), Is.Zero);
            }
            finally
            {
                AiMapMemory.Clear(); Game.Ai.V2.AiReconMemory.Clear(); Game.Map.VisionSystem.Clear();
                Game.Ai.V2.ReconIntelSnapshotRegistry.Clear();
            }
        }
    }
}
#endif
