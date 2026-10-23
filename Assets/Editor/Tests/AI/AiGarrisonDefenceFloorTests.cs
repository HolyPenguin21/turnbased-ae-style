#if UNITY_INCLUDE_TESTS
using System.Collections.Generic;
using System.Linq;
using Game.Ai;
using NUnit.Framework;

namespace Game.EditorTests
{
    // User decision 2026-09-30 — the garrison defence floor is a share of the ground force
    // (Citadel 10%, Base 5%); one body always stays; among the legal choices the strongest body
    // goes when the defence shortfall is smaller than the attack power it frees.
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

        // Vex T22: floor 7.4, Ash Drifter 6.6 + MT Rust Tank 11. Keeping the tank frees 6.6;
        // keeping Ash Drifter frees 11 at a 0.8 shortfall — the tank goes.
        [Test]
        public void TankLeaves_WhenTheShortfallIsSmallerThanTheAttackGain()
        {
            Assert.That(Spare(7.4f, 6.6f, 11f), Is.EquivalentTo(new[] { 1 }));
        }

        // floor 15, bodies 6.6 / 11 / 4: one may go. Releasing the tank (11, shortfall 4.4) and
        // releasing Ash Drifter (6.6, no shortfall) both free 6.6 net — the tie keeps the stronger
        // defence, so Ash Drifter goes and the tank stays.
        [Test]
        public void Tie_KeepsTheStrongerDefender()
        {
            Assert.That(Spare(15f, 6.6f, 11f, 4f), Is.EquivalentTo(new[] { 0 }));
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
            // floor 10: the strongest single body (12) holds it, so two may go — the two strongest
            // leave when the weakest remaining body's shortfall (10-3=7) is smaller than the gain.
            HashSet<int> spare = Spare(10f, 3f, 12f, 11f);
            Assert.That(spare.Count, Is.EqualTo(2));
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
                var flags = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic;
                System.Type type = typeof(AiMapMemory).GetNestedType("EnemySighting", System.Reflection.BindingFlags.NonPublic);
                object record = System.Activator.CreateInstance(type, nonPublic: true);
                void Put(string field, object value) => type.GetField(field).SetValue(record, value);
                Put("ArmyId", 11); Put("Owner", enemy); Put("Hex", hex); Put("MemberCount", 1);
                Put("DefenseSum", 3f); Put("AttackSum", 5f); Put("SeenTurn", 8); Put("MaxMovement", 3);
                Put("Defenders", profiles.ToList());
                var all = (System.Collections.IDictionary)typeof(AiMapMemory).GetField("EnemySightings", flags).GetValue(null);
                var store = (System.Collections.IDictionary)System.Activator.CreateInstance(
                    typeof(Dictionary<,>).MakeGenericType(typeof(int), type));
                store[11] = record; all[player] = store;
                float expected = Game.Ai.V2.AiPower.SightingPower(sighting) * 1.3f * (5f / 6f);
                float canonical = AiArmyRoles.GarrisonThreatFloor(player, hex);
                Assert.That(canonical, Is.EqualTo(expected).Within(0.0001f));
                enemy.IsEliminated = true;
                Assert.That(AiArmyRoles.GarrisonThreatFloor(player, hex), Is.Zero,
                    "A defeated owner cannot send this remembered army to our base.");
                enemy.IsEliminated = false;
                store.Clear();
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
