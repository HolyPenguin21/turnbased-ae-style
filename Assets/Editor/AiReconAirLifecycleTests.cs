#if UNITY_INCLUDE_TESTS
using System.Collections.Generic;
using Game.Ai.V2;
using Game.Aviation;
using Game.Units;
using NUnit.Framework;

namespace Game.EditorTests
{
    // Air-recon lifecycle facts that must reach Continuity honestly.
    public class AiReconAirLifecycleTests
    {
        [Test]
        public void OwnedAirfieldDuringOutbound_DoesNotCompleteSortie()
        {
            var wing = new ReconAirSortieState { Phase = ReconAirPhase.Outbound };
            Assert.That(ReconAirSortieLifecycle.CompletesAtAirfield(wing, atAirfield: true,
                hasDeparted: true), Is.False, "an intermediate airfield does not end an outbound sortie");
        }

        [Test]
        public void ReturnPhaseAtOwnedAirfield_CompletesSortie()
        {
            var wing = new ReconAirSortieState { Phase = ReconAirPhase.Return };
            Assert.That(ReconAirSortieLifecycle.CompletesAtAirfield(wing, true, true), Is.True);
            Assert.That(ReconAirSortieLifecycle.CompletesAtAirfield(wing, true, hasDeparted: false),
                Is.False, "a wing that never left its airfield has not flown a sortie");
            Assert.That(ReconAirSortieLifecycle.CompletesAtAirfield(wing, atAirfield: false, true),
                Is.False);
        }

        [TestCase(0, 0, 0)]
        [TestCase(1, 0, 1)]
        [TestCase(2, 0, 2)]
        [TestCase(2, 1, 1)]
        [TestCase(2, 2, 0)]
        public void RemainingEndurance_IsDerivedOnlyFromTurnsWithoutRefuel(
            int turnsWithoutRefuel, int unlandedEnds, int expected)
        {
            var unit = new UnitData
            {
                IsAviation = true,
                TurnsWithoutRefuel = turnsWithoutRefuel,
                ConsecutiveUnlandedEnds = unlandedEnds,
            };
            Assert.That(AviationRange.SafeUnlandedEndsRemaining(
                new List<UnitData> { unit }), Is.EqualTo(expected));
        }

    }
}
#endif
