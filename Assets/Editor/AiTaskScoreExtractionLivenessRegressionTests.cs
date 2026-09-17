#if UNITY_INCLUDE_TESTS
using Game.Ai.V2;
using Game.Economy;
using Game.HexGrid;
using NUnit.Framework;

namespace Game.EditorTests
{
    // R3 (2026-09-17) — MissionContinuityLayer.AdvanceIntent suspends a durable BuildExtraction
    // intent on repeated NoMoverExists/MoverContended (SuspendReason.CapabilityUnavailable) and
    // deliberately exempts it from StallTurns/ShouldReap so a transient blip does not lose the
    // project. Before this fix nothing bounded that exemption for Extraction (only FoundBase had
    // RecordBaseExpansionDeliveryFailure), so a durable Extraction intent whose pinned mover could
    // never advance stayed suspended forever, holding its actor/card reservation. These tests cover
    // the new MissionIntentState.RecordExtractionDeliveryFailure/IsExtractionDeliverySuppressed
    // directly — the same bounded-suppression owner Base already uses, generalized to a per-site
    // key because several Extraction intents can be durable at once.
    public class AiTaskScoreExtractionLivenessRegressionTests
    {
        [Test]
        public void RecordExtractionDeliveryFailure_ConsecutiveTurns_SuppressesAfterStallWindow()
        {
            var state = new MissionIntentState();
            var hex = new HexCoord(8, -2);

            // commitmentStallTurns == 2: the first consecutive failure only counts, the second
            // consecutive failure crosses the threshold and suppresses.
            bool firstSuppressed = state.RecordExtractionDeliveryFailure(10, ResourceType.Energy, hex);
            Assert.That(firstSuppressed, Is.False,
                "one failure must not retire an otherwise-live durable commitment");
            Assert.That(state.IsExtractionDeliverySuppressed(10, ResourceType.Energy, hex), Is.False);

            bool secondSuppressed = state.RecordExtractionDeliveryFailure(11, ResourceType.Energy, hex);
            Assert.That(secondSuppressed, Is.True,
                "repeated consecutive-turn MoverContended/NoMoverExists must eventually bound the suspension");
            Assert.That(state.IsExtractionDeliverySuppressed(11, ResourceType.Energy, hex), Is.True);
            Assert.That(state.IsExtractionDeliverySuppressed(12, ResourceType.Energy, hex), Is.True,
                "the cooldown window must outlive the triggering turn, not just gate that one call");
        }

        [Test]
        public void RecordExtractionDeliveryFailure_GapTurn_ResetsConsecutiveStreak()
        {
            var state = new MissionIntentState();
            var hex = new HexCoord(3, 4);

            state.RecordExtractionDeliveryFailure(5, ResourceType.Materials, hex);
            // A gap turn without a capability failure (real progress, or a different suspend
            // reason) breaks the consecutive-turn streak on its own — no separate reset call.
            bool suppressedAfterGap = state.RecordExtractionDeliveryFailure(8, ResourceType.Materials, hex);

            Assert.That(suppressedAfterGap, Is.False,
                "a non-consecutive failure must restart the count, exactly like Base's own streak rule");
        }

        [Test]
        public void RecordExtractionDeliveryFailure_IsKeyedPerSite_NotGlobal()
        {
            var state = new MissionIntentState();
            var siteA = new HexCoord(8, -2);
            var siteB = new HexCoord(1, 1);

            state.RecordExtractionDeliveryFailure(1, ResourceType.Energy, siteA);
            bool siteASuppressed = state.RecordExtractionDeliveryFailure(2, ResourceType.Energy, siteA);
            bool siteBSuppressedByUnrelatedFailures =
                state.IsExtractionDeliverySuppressed(2, ResourceType.Energy, siteB);

            Assert.That(siteASuppressed, Is.True);
            Assert.That(siteBSuppressedByUnrelatedFailures, Is.False,
                "unlike Base's single staged slot, Extraction can hold several durable intents at " +
                "once — one stuck site must never suppress an unrelated one");
        }
    }
}
#endif
