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

        [TestCase(0, 10, 5)]
        [TestCase(1, 10, 10)]
        [TestCase(2, 5, 5)]
        public void FirstTurnOutboundBudget_IsDerivedFromEndurance(
            int turnsWithoutRefuel, int movement, int expected)
        {
            var unit = new UnitData
            {
                IsAviation = true,
                TurnsWithoutRefuel = turnsWithoutRefuel,
                MoveMax = movement,
                MoveCurrent = movement,
            };
            Assert.That(AviationRange.FirstTurnOutboundBudget(
                new List<UnitData> { unit }), Is.EqualTo(expected));
        }

        [Test]
        public void FutureActivationBudget_UsesCumulativeEnergyAndGuaranteedApFloor()
        {
            Assert.That(AviationContinuationBudget.CanGuaranteeNextActivation(
                null, null, energyAvailableAfterCurrentActivation: 5f,
                nextTurnEnergyCost: 5f, nextTurnApCost: 6f, out _), Is.True);

            Assert.That(AviationContinuationBudget.CanGuaranteeNextActivation(
                null, null, energyAvailableAfterCurrentActivation: 5f,
                nextTurnEnergyCost: 6f, nextTurnApCost: 6f, out string energyBlock), Is.False);
            Assert.That(energyBlock, Is.EqualTo("insufficient_next_turn_air_energy"));

            Assert.That(AviationContinuationBudget.CanGuaranteeNextActivation(
                null, null, energyAvailableAfterCurrentActivation: 10f,
                nextTurnEnergyCost: 5f, nextTurnApCost: 7f, out string apBlock), Is.False);
            Assert.That(apBlock, Is.EqualTo("insufficient_guaranteed_next_turn_ap"));
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
