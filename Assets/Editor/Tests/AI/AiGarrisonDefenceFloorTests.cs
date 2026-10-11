#if UNITY_INCLUDE_TESTS
using System.Collections.Generic;
using System.Linq;
using Game.Ai;
using NUnit.Framework;

namespace Game.EditorTests
{
    // User decision 2026-09-30 — the garrison defence floor is a share of the ground force
    // (Citadel 10%, Base 5%); one body always stays; among the legal choices the strongest body
    // goes when the defence shortfall is smaller than the attack power it frees.
    public class AiGarrisonDefenceFloorTests
    {
        // Additive power stands in for AiPower here: the rule only compares set powers.
        private static HashSet<int> Spare(float floor, params float[] bodies) =>
            AiArmyRoles.SpareableBodies(bodies, set => set.Sum(), floor);

        [Test]
        public void Floor_IsTheCitadelAndBaseShareOfTheGroundForce()
        {
            Assert.That(AiArmyRoles.GarrisonDefenceFloor(74f, isCitadel: true), Is.EqualTo(7.4f).Within(1e-4f));
            Assert.That(AiArmyRoles.GarrisonDefenceFloor(74f, isCitadel: false), Is.EqualTo(3.7f).Within(1e-4f));
        }

        // Vex T22: floor 7.4, Ash Drifter 6.6 + MT Rust Tank 11. Keeping the tank frees 6.6;
        // keeping Ash Drifter frees 11 at a 0.8 shortfall — the tank goes.
        [Test]
        public void TankLeaves_WhenTheShortfallIsSmallerThanTheAttackGain()
        {
            Assert.That(Spare(7.4f, 6.6f, 11f), Is.EquivalentTo(new[] { 1 }));
        }

        // floor 15, bodies 6.6 / 11 / 4: one may go. Releasing the tank (11, shortfall 4.4) and
        // releasing Ash Drifter (6.6, no shortfall) both free 6.6 net — the tie keeps the stronger
        // defence, so Ash Drifter goes and the tank stays.
        [Test]
        public void Tie_KeepsTheStrongerDefender()
        {
            Assert.That(Spare(15f, 6.6f, 11f, 4f), Is.EquivalentTo(new[] { 0 }));
        }

        [Test]
        public void OneBodyAlwaysStays()
        {
            Assert.That(Spare(0f, 9.3f), Is.Empty);
            Assert.That(Spare(0f, 5f, 6f, 7f).Count, Is.EqualTo(2));
        }

        [Test]
        public void BelowTheFloorEvenWithAllBodies_NothingLeaves()
        {
            Assert.That(Spare(30f, 6.6f, 11f), Is.Empty);
        }

        [Test]
        public void HowManyLeave_IsBoundedByTheStrongestRemainder()
        {
            // floor 10: the strongest single body (12) holds it, so two may go — the two strongest
            // leave when the weakest remaining body's shortfall (10-3=7) is smaller than the gain.
            HashSet<int> spare = Spare(10f, 3f, 12f, 11f);
            Assert.That(spare.Count, Is.EqualTo(2));
        }
    }
}
#endif
