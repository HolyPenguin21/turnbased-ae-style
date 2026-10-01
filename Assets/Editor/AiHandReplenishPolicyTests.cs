#if UNITY_INCLUDE_TESTS
using Game.Ai.V2;
using NUnit.Framework;

namespace Game.EditorTests
{
    public class AiHandReplenishPolicyTests
    {
        private const int DrawCost = 2;

        [Test]
        public void EmptyHand_DrawsUpToTarget()
        {
            Assert.AreEqual(2, HandReplenishPolicy.DrawsWanted(
                handCount: 0, freeSlots: 10, deckCount: 19, ap: 8, drawApCost: DrawCost, drawsUsedThisTurn: 0));
        }

        [Test]
        public void HandAtTarget_DrawsNothing()
        {
            Assert.AreEqual(0, HandReplenishPolicy.DrawsWanted(
                handCount: AiConfigV2.handReplenishTargetCards, freeSlots: 8, deckCount: 19,
                ap: 10, drawApCost: DrawCost, drawsUsedThisTurn: 0));
        }

        [Test]
        public void KeepsMinimumApForTheTurn()
        {
            // 6 AP (last initiative rank) - one draw leaves exactly the protected 4.
            Assert.AreEqual(1, HandReplenishPolicy.DrawsWanted(
                handCount: 0, freeSlots: 10, deckCount: 19, ap: 6, drawApCost: DrawCost, drawsUsedThisTurn: 0));
            Assert.AreEqual(0, HandReplenishPolicy.DrawsWanted(
                handCount: 0, freeSlots: 10, deckCount: 19, ap: 5, drawApCost: DrawCost, drawsUsedThisTurn: 0));
        }

        [Test]
        public void BoundedByDeckSlotsAndTurnDrawCap()
        {
            Assert.AreEqual(1, HandReplenishPolicy.DrawsWanted(
                handCount: 0, freeSlots: 10, deckCount: 1, ap: 10, drawApCost: DrawCost, drawsUsedThisTurn: 0));
            Assert.AreEqual(0, HandReplenishPolicy.DrawsWanted(
                handCount: 0, freeSlots: 10, deckCount: 0, ap: 10, drawApCost: DrawCost, drawsUsedThisTurn: 0));
            Assert.AreEqual(0, HandReplenishPolicy.DrawsWanted(
                handCount: 0, freeSlots: 10, deckCount: 19, ap: 10, drawApCost: DrawCost,
                drawsUsedThisTurn: AiConfigV2.maxTerminalDrawsPerTurn));
        }
    }
}
#endif
