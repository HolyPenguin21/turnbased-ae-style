#if UNITY_INCLUDE_TESTS
using System;
using Game.UI;
using NUnit.Framework;
using UnityEngine;

namespace Game.EditorTests
{
    public sealed class BattleSkillEffectAtlasTests
    {
        [TestCase(BattleSkillEffect.Kind.Heal, 819, 205)]
        [TestCase(BattleSkillEffect.Kind.Drill, 614, 205)]
        [TestCase(BattleSkillEffect.Kind.Berserk, 410, 204)]
        [TestCase(BattleSkillEffect.Kind.Bodyguard, 205, 205)]
        [TestCase(BattleSkillEffect.Kind.Shock, 0, 205)]
        public void AtlasRowsMatchApprovedTopDownLayout(BattleSkillEffect.Kind skill,
            int expectedBottom, int expectedHeight)
        {
            for (int frame = 0; frame < BattleSkillEffect.FrameCount; frame++)
            {
                Rect rect = BattleSkillEffect.FrameRect(skill, frame);
                Assert.That(rect.x, Is.EqualTo(frame * 192f));
                Assert.That(rect.y, Is.EqualTo(expectedBottom));
                Assert.That(rect.width, Is.EqualTo(192f));
                Assert.That(rect.height, Is.EqualTo(expectedHeight));
            }
        }

        [Test]
        public void AllFortyFramesTileThe1536By1024AtlasWithoutGaps()
        {
            float totalRowHeight = 0f;
            float previousBottom = BattleSkillEffect.AtlasHeight;
            for (int row = 0; row < BattleSkillEffect.SkillCount; row++)
            {
                Rect first = BattleSkillEffect.FrameRect((BattleSkillEffect.Kind)row, 0);
                Assert.That(first.yMax, Is.EqualTo(previousBottom));
                Assert.That(BattleSkillEffect.FrameRect((BattleSkillEffect.Kind)row, 7).xMax,
                    Is.EqualTo(BattleSkillEffect.AtlasWidth));
                previousBottom = first.yMin;
                totalRowHeight += first.height;
            }
            Assert.That(totalRowHeight, Is.EqualTo(BattleSkillEffect.AtlasHeight));
            Assert.That(previousBottom, Is.Zero);
        }

        [Test]
        public void InvalidFrameOrRowCannotSampleNeighbouringAnimation()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                BattleSkillEffect.FrameRect(BattleSkillEffect.Kind.Heal, -1));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                BattleSkillEffect.FrameRect(BattleSkillEffect.Kind.Heal, 8));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                BattleSkillEffect.FrameRect((BattleSkillEffect.Kind)5, 0));
        }
    }
}
#endif
