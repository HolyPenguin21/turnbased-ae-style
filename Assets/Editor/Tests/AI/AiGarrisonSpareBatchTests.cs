#if UNITY_INCLUDE_TESTS
using System.Collections.Generic;
using System.Linq;
using Game.Ai;
using NUnit.Framework;

namespace Game.EditorTests
{
    // The garrison release rule (AiArmyRoles.MayReleaseBodies) judges the WHOLE departing set:
    // bodies that may each leave alone may not leave together. Every planner/executor pair must
    // therefore ask about the complete batch on the pre-transfer garrison (AttackExecutor
    // preparation, GroundCombatAssaultTransaction, GarrisonDetachment.MayRelease).
    public class AiGarrisonSpareBatchTests
    {
        private static float Sum(IEnumerable<float> set) => set.Sum();

        [Test]
        public void SpareableBatch_IsNotSpareableBodyByBody()
        {
            // Orlan T14 (playtest 2026-10-01): LI, MI, LI, MT, floor 4. Strongest first while the
            // rest holds the floor: MT, then MI (two LI = 6 stay); a third body would leave 3.
            var garrison = new List<float> { 3f, 6f, 3f, 12f };
            const float floor = 4f;

            HashSet<int> batch = AiArmyRoles.SpareableBodies(garrison, Sum, floor);
            Assert.That(batch, Is.EquivalentTo(new[] { 1, 3 }));
            Assert.That(AiArmyRoles.MayReleaseBodies(garrison, new[] { 0 }, Sum, floor), Is.True,
                "an LI alone may leave...");
            Assert.That(AiArmyRoles.MayReleaseBodies(garrison, new[] { 0, 1, 3 }, Sum, floor), Is.False,
                "...but not on top of the batch");

            // A snapshot planner taking several bodies checks the set on the scan's floor.
            var donor = new Game.Ai.V2.ArmySnapshot
            {
                ArmyId = 5, IsGarrison = true,
                Members = garrison.Select(p => new Game.Combat.WorthIt.DefenderProfile(0f, false,
                    attack: p, hitPoints: 1f)).ToArray(),
                NonHeroSpareable = new[] { true, true, true, true },
            };
            float donorFloor = Game.Ai.V2.AiPower.EffectiveArmyPowerFromProfiles(
                new[] { donor.Members[0], donor.Members[2] }) - 0.01f;
            var snap = new Game.Ai.V2.WorldSnapshot { Self = new Game.Ai.V2.SelfSnapshot {
                GarrisonReleaseFloors = new Dictionary<int, float> { [5] = donorFloor } } };
            Assert.That(Game.Ai.V2.GarrisonDetachment.MayRelease(snap, donor, new[] { 3, 1 }), Is.True);
            Assert.That(Game.Ai.V2.GarrisonDetachment.MayRelease(snap, donor, new[] { 3, 1, 0 }), Is.False);
            Assert.That(Game.Ai.V2.GarrisonDetachment.MayRelease(new Game.Ai.V2.WorldSnapshot(), donor,
                new[] { 3, 1, 0 }), Is.True, "no floor witness: the flags decide (isolated snapshots)");

            // After MT and MI left, the two LI that remain release nobody.
            HashSet<int> afterTwo = AiArmyRoles.SpareableBodies(new List<float> { 3f, 3f }, Sum, floor);
            Assert.That(afterTwo, Is.Empty);
        }
    }
}
#endif
