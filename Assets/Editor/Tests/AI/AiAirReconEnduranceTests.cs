#if UNITY_INCLUDE_TESTS
using Game.Ai.V2;
using NUnit.Framework;

namespace Game.EditorTests
{
    // Playtest 2026-10-07 — Rusty Vulture (TurnsWithoutRefuel = 1) turned for home after two steps
    // of an AirSweep as if it were a plane.
    public class AiAirReconEnduranceTests
    {
        [Test]
        public void Plane_TurnsHomeWhenOnlyTheReturnReserveIsLeft()
        {
            Assert.That(AirReconStepDirector.TurnsForReturnReserve(1, 0, 0), Is.True);
            Assert.That(AirReconStepDirector.TurnsForReturnReserve(1, AiConfigV2.airReconTurningMpReserveSlack, 0), Is.True);
        }

        [Test]
        public void Helicopter_WithASafeUnlandedEnd_KeepsFlyingOutward()
        {
            Assert.That(AirReconStepDirector.TurnsForReturnReserve(1, 0, 1), Is.False);
        }

        [Test]
        public void PlentyOfMovementOrALongerRoute_NeverTriggersTheReserve()
        {
            Assert.That(AirReconStepDirector.TurnsForReturnReserve(1, AiConfigV2.airReconTurningMpReserveSlack + 1, 0), Is.False);
            Assert.That(AirReconStepDirector.TurnsForReturnReserve(2, 0, 0), Is.False);
        }
    }
}
#endif
