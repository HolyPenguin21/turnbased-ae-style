#if UNITY_INCLUDE_TESTS
using System.Collections.Generic;
using Game.Ai;
using Game.Combat;
using Game.HexGrid;
using Game.Players;
using NUnit.Framework;

namespace Game.EditorTests
{
    // The one change rule of a remembered body: memory updates and snapshot observation must notice
    // every field a battle reads (Range, the commander, a garrison) and only those.
    public sealed class AiChangeDetectionTests
    {
        private static readonly PlayerSetupData Red = new PlayerSetupData { Nickname = "Red", ColorIndex = 1 };

        private static WorthIt.DefenderProfile Body(int range = 1, int fate = 0) =>
            new WorthIt.DefenderProfile(2f, false, null, 3f, 6f, 2, null, 6f, range: range, fateMax: fate);

        private static AiMapMemory.KnownEnemySighting Sighting(WorthIt.DefenderProfile body,
            bool garrison = false, WorthIt.SideCommander commander = default) =>
            new AiMapMemory.KnownEnemySighting(new HexCoord(2, 0), Red, "army", 1, 2f, 3f,
                new List<WorthIt.DefenderProfile> { body }, seenTurn: 4, armyId: 7,
                isGarrison: garrison, commander: commander);

        [Test]
        public void RebuiltProfiles_AreTheSameContent()
        {
            Assert.That(WorthIt.DefenderProfile.SameContent(new[] { Body() }, new[] { Body() }), Is.True);
            Assert.That(Sighting(Body()).SameContent(Sighting(Body())), Is.True);
        }

        [Test]
        public void RangeOrFate_IsAChange()
        {
            Assert.That(Body(range: 1).SameContent(Body(range: 3)), Is.False, "range decides counter-fire");
            Assert.That(Body(fate: 0).SameContent(Body(fate: 2)), Is.False);
        }

        [Test]
        public void CommanderOrGarrison_IsAChange()
        {
            Assert.That(Sighting(Body()).SameContent(Sighting(Body(), garrison: true)), Is.False);
            Assert.That(Sighting(Body()).SameContent(
                Sighting(Body(), commander: new WorthIt.SideCommander(3, 2))), Is.False);
        }
    }
}
#endif
