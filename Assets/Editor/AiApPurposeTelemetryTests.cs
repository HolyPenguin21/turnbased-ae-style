#if UNITY_INCLUDE_TESTS
using Game.Ai.V2;
using NUnit.Framework;

namespace Game.EditorTests
{
    public sealed class AiApPurposeTelemetryTests
    {
        [TestCase("Raid(Guard@-3,0)", "Raid.Guard")]
        [TestCase("Raid(Return -2,-4)", "Raid.Return")]
        [TestCase("Attack(Gather #0 -4,5)", "Attack.Gather")]
        [TestCase("Explore", "Explore")]
        [TestCase("", "unknown")]
        public void PurposeIsKindPlusFirstTargetWord(string label, string expected)
            => Assert.That(ApBudgetTelemetry.PurposeOf(label), Is.EqualTo(expected));
    }
}
#endif
