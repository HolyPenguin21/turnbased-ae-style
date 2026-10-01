#if UNITY_INCLUDE_TESTS
using Game.Ai.V2;
using NUnit.Framework;

namespace Game.EditorTests
{
    public class AiHandReplenishPolicyTests
    {
        private const int DrawCost = 2;
        private static readonly int Keep = AiConfigV2.handReplenishMinApLeft;

        private static int Wanted(int handCount = 0, int freeSlots = 10, int deckCount = 19,
            int ap = 10, int apToKeep = -1, int drawsUsed = 0) =>
            HandReplenishPolicy.DrawsWanted(handCount, freeSlots, deckCount, ap,
                apToKeep < 0 ? Keep : apToKeep, DrawCost, drawsUsed);

        [Test]
        public void EmptyHand_DrawsUpToTarget()
        {
            Assert.AreEqual(2, Wanted(ap: 8));
        }

        [Test]
        public void HandAtTarget_DrawsNothing()
        {
            Assert.AreEqual(0, Wanted(handCount: AiConfigV2.handReplenishTargetCards, freeSlots: 8));
        }

        [Test]
        public void KeepsMinimumApForTheTurn()
        {
            // 6 AP (last initiative rank) - one draw leaves exactly the protected 4.
            Assert.AreEqual(1, Wanted(ap: 6));
            Assert.AreEqual(0, Wanted(ap: 5));
        }

        [Test]
        public void AviationObligationApIsKeptOnTopOfTheFloor()
        {
            // 8 AP, two wings returning at 1 AP each: keep 4 + 2, so only one draw fits.
            Assert.AreEqual(1, Wanted(ap: 8, apToKeep: Keep + 2));
            Assert.AreEqual(0, Wanted(ap: 6, apToKeep: Keep + 2));
        }

        [Test]
        public void BoundedByDeckSlotsAndTurnDrawCap()
        {
            Assert.AreEqual(1, Wanted(deckCount: 1));
            Assert.AreEqual(0, Wanted(deckCount: 0));
            Assert.AreEqual(1, Wanted(freeSlots: 1));
            Assert.AreEqual(0, Wanted(drawsUsed: AiConfigV2.maxTerminalDrawsPerTurn));
        }
    }
}
#endif
