#if UNITY_INCLUDE_TESTS
using System.Collections.Generic;
using System.Linq;
using Game.Ai;
using NUnit.Framework;

namespace Game.EditorTests
{
    // AiArmyRoles.SpareableBodies is a property of the WHOLE garrison roster, not of one body:
    // a batch it releases may not be releasable one body at a time against the shrinking
    // garrison. Every planner/executor pair must therefore ask about the complete batch on the
    // pre-transfer garrison (AttackExecutor preparation, GroundCombatAssaultTransaction).
    public class AiGarrisonSpareBatchTests
    {
        private static float Sum(IEnumerable<float> set) => set.Sum();

        [Test]
        public void SpareableBatch_IsNotSpareableBodyByBody()
        {
            // Orlan T14 (playtest 2026-10-01): LI, MI, LI, MT; the floor is met by the tank alone,
            // and keeping one weak LI slightly under the floor releases more power.
            var garrison = new List<float> { 3f, 6f, 3f, 12f };
            const float floor = 4f;

            HashSet<int> batch = AiArmyRoles.SpareableBodies(garrison, Sum, floor);
            Assert.That(batch, Is.EquivalentTo(new[] { 1, 2, 3 }));

            // After MT and MI left, the two LI that remain release nobody.
            HashSet<int> afterTwo = AiArmyRoles.SpareableBodies(new List<float> { 3f, 3f }, Sum, floor);
            Assert.That(afterTwo, Is.Empty);
        }
    }
}
#endif
