#if UNITY_INCLUDE_TESTS
using System.Collections.Generic;
using Game.Ai;
using Game.Ai.V2;
using Game.Combat;
using Game.HexGrid;
using Game.Players;
using NUnit.Framework;

namespace Game.EditorTests
{
    // 2026-11-19 audit of the Economy and Defence blocks:
    // the behaviour changes that unify a rule across its readers.
    public class AiEconomyDefenceAuditTests
    {
        private static readonly HexCoord Hex = new HexCoord(2, -1);

        // E7 — an event guard blocks a hex in both halves of the one occupancy rule.
        [Test]
        public void EventGuardedHex_IsOccupied_AndNeutralHeld_UnlessAnEnemyStandsThere()
        {
            var snap = new WorldSnapshot { Known = new KnownSnapshot
            {
                EventGuards = new[] { new KnownEventGuardSnapshot(Hex, default, "guard", 1) },
            } };
            Assert.That(WorldAnalysis.KnownHostileAtHex(snap, Hex), Is.True);
            Assert.That(WorldAnalysis.KnownNeutralOnlyAtHex(snap, Hex), Is.True);
            Assert.That(WorldAnalysis.KnownHostileAtHex(snap, new HexCoord(0, 0)), Is.False);

            snap.Known.EnemySightings = new[] { new AiMapMemory.KnownEnemySighting(Hex,
                new PlayerSetupData(), "enemy", 1, 1f, 1f, new List<WorthIt.DefenderProfile>()) };
            Assert.That(WorldAnalysis.KnownHostileAtHex(snap, Hex), Is.True);
            Assert.That(WorldAnalysis.KnownNeutralOnlyAtHex(snap, Hex), Is.False);
        }

        // E14 — the trip keeps the WEAKEST sufficient escort (owner rule 2026-11-16), also when the
        // builder lightens at a base: the strong body stays available to the strike.
        [Test]
        public void LightenAtBase_KeepsTheWeakestSufficientEscort()
        {
            var strong = new WorthIt.DefenderProfile(6f, false, null, 9f, 12f, 3);
            var weak = new WorthIt.DefenderProfile(2f, false, null, 4f, 6f, 3);
            var army = new ArmySnapshot
            {
                ArmyId = 5, HasHero = true, HeroMoveMax = 3, MaxMovement = 3,
                Members = new[] { strong, weak },
                NonHeroActivationApCosts = new[] { 1, 1 },
                NonHeroMoveMax = new[] { 3, 3 },
            };
            var scout = new AiMapMemory.KnownEnemySighting(new HexCoord(4, -1), new PlayerSetupData(),
                "scout", 1, 1f, 1f, new List<WorthIt.DefenderProfile>
                    { new WorthIt.DefenderProfile(1f, false, null, 1f, 2f, 3) });
            Assert.That(EconomyBuilderSelection.EconomyRosterSafe(new[] { weak }, default,
                new[] { scout }, 1), Is.True, "precondition: the weak body alone is a sufficient escort");

            List<int> kept = EconomyBuilderSelection.MinimumSafeEconomyEscortIndices(army,
                new[] { scout }, minimumEscort: 1, minimumMovement: 1, unloadRoom: 2, existingBodies: 2);

            Assert.That(kept, Is.EqualTo(new[] { 1 }));
        }
    }
}
#endif
