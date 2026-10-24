#if UNITY_INCLUDE_TESTS
using Game.UI;
using Game.Map;
using Game.Players;
using Game.Combat;
using Game.Units;
using System.Reflection;
using System.Runtime.Serialization;
using NUnit.Framework;

namespace Game.EditorTests
{
    public sealed class BattleSkillFlipbookTests
    {
        [TestCase(true, true)]
        [TestCase(false, false)]
        public void FastAndNonHumanResolutionDoNotTouchOrWaitForNativeCards(bool local, bool fast)
        {
            var screen = (BattleScreenUI)FormatterServices.GetUninitializedObject(typeof(BattleScreenUI));
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            typeof(BattleScreenUI).GetField("_localArmy",flags).SetValue(screen,
                local ? new ArmyData { Owner = new PlayerSetupData { IsHuman = true } } : null);
            typeof(BattleScreenUI).GetField("_fastResolve",flags).SetValue(screen,fast);
            Assert.DoesNotThrow(() => typeof(BattleScreenUI).GetMethod("PlayAttackEffects",flags).Invoke(screen,
                new object[] { null, new BattleAttackApplication { Victim = new UnitData() } }));
        }

        [TestCase(true, 19, 1.38f)]
        [TestCase(false, 15, 1.08f)]
        public void PlaybackUsesOnlyAuthoredFramesAndEndsCleanly(bool fire, int count, float duration)
        {
            Assert.That(BattleSkillFlipbook.FrameCount(fire), Is.EqualTo(count));
            Assert.That(BattleSkillFlipbook.Duration(fire), Is.EqualTo(duration).Within(.00001f));
            Assert.That(BattleSkillFlipbook.Sample(-1f, fire).Frame, Is.EqualTo(-1));
            Assert.That(BattleSkillFlipbook.Sample(duration, fire).Frame, Is.EqualTo(-1));
            int previous = -1;
            for (int ms = 0; ms < duration * 1000; ms++)
            {
                var sample = BattleSkillFlipbook.Sample(ms / 1000f, fire);
                Assert.That(sample.Frame, Is.InRange(0, count - 1));
                Assert.That(sample.Frame, Is.GreaterThanOrEqualTo(previous));
                Assert.That(sample.Blend, Is.InRange(0f, 1f));
                previous = sample.Frame;
            }
        }

        [Test]
        public void FireKeepsTwoAdditionalEngulfFramesBeforeCooling()
        {
            Assert.That(BattleSkillFlipbook.Sample(.72f, true).Frame, Is.EqualTo(12));
            Assert.That(BattleSkillFlipbook.Sample(.82f, true).Frame, Is.EqualTo(13));
            Assert.That(BattleSkillFlipbook.Sample(.9f, true).Frame, Is.EqualTo(14));
        }

        [TestCase(true)]
        [TestCase(false)]
        public void BlendApproachesNextFrameWithoutRestartingOrDisplayingAnEmptyCell(bool fire)
        {
            var middle = BattleSkillFlipbook.Sample(.03f, fire);
            Assert.That(middle.Frame, Is.Zero);
            Assert.That(middle.Next, Is.EqualTo(1));
            Assert.That(middle.Blend, Is.EqualTo(.5f).Within(.001f));
            var tail = BattleSkillFlipbook.Sample(BattleSkillFlipbook.Duration(fire) - .001f, fire);
            Assert.That(tail.Next, Is.EqualTo(-1));
            Assert.That(tail.Blend, Is.GreaterThan(.99f));
        }
    }
}
#endif
