#if UNITY_INCLUDE_TESTS
using System.Collections.Generic;
using Game.Ai;
using Game.Ai.V2;
using Game.HexGrid;
using Game.Players;
using NUnit.Framework;

namespace Game.EditorTests
{
    // Pins the Recon exposure / detector-risk rule (who can come and engage a scout, who can see
    // through its stealth) that the objective scan, the vantage ranking and the frontier annotation
    // all apply, so merging their copies keeps the same numbers.
    public class AiReconDetectorRiskTests
    {
        private static readonly HexCoord Focus = new HexCoord(4, 3);
        private static readonly PlayerSetupData Enemy = new PlayerSetupData { Nickname = "Enemy" };

        [TearDown]
        public void ClearState() => MissionIntentRegistry.Clear();

        [Test]
        public void DistantBlindFieldArmy_RequiresStealthWithoutDetectionRisk()
        {
            WorldSnapshot snap = WithSightings(Sighting(new HexCoord(6, 3), recce: 0, garrison: false));
            ReconObjective o = ReconObjectiveEvaluator.ExploreAt(snap, Focus);

            Assert.That(o.Stealth, Is.EqualTo(StealthRequirement.Required));
            Assert.That(o.DetectionRisk, Is.Zero);
            Assert.That(ScoutRiskModel.DetectorRisk(snap, Focus), Is.Zero);
        }

        [Test]
        public void AdjacentRecceArmy_IsOneDetector()
        {
            WorldSnapshot snap = WithSightings(Sighting(new HexCoord(5, 3), recce: 1, garrison: false));
            ReconObjective o = ReconObjectiveEvaluator.ExploreAt(snap, Focus);

            float oneDetector = 1f / AiConfigV2.scoutDetectionRiskNorm;
            Assert.That(ScoutRiskModel.DetectorRisk(snap, Focus), Is.EqualTo(oneDetector).Within(1e-5f));
            Assert.That(o.Stealth, Is.EqualTo(StealthRequirement.Required));
            Assert.That(o.DetectionRisk, Is.EqualTo(oneDetector).Within(1e-5f));
        }

        [Test]
        public void AdjacentGarrison_DetectsButDoesNotExpose()
        {
            WorldSnapshot snap = WithSightings(Sighting(new HexCoord(5, 3), recce: 1, garrison: true));
            ReconObjective o = ReconObjectiveEvaluator.ExploreAt(snap, Focus);

            Assert.That(ScoutRiskModel.DetectorRisk(snap, Focus), Is.GreaterThan(0f));
            Assert.That(o.Stealth, Is.EqualTo(StealthRequirement.None));
            Assert.That(o.DetectionRisk, Is.Zero);
        }

        // Owner's example (2026-10-24): a blind field army two hexes from a solo scout cannot see
        // through stealth, but it can come and engage a visible scout — hiding is worth it, even
        // when the AP would otherwise fund a card draw.
        [Test]
        public void BlindFieldArmyTwoHexesAway_IsAFullStealthThreat_AndOutweighsADraw()
        {
            var sightings = new List<AiMapMemory.KnownEnemySighting> { Sighting(new HexCoord(6, 3), recce: 0, garrison: false) };
            Assert.That(ScoutRiskModel.DetectorRisk(sightings, Focus), Is.Zero);
            Assert.That(ScoutRiskModel.StealthThreat(sightings, Focus), Is.EqualTo(1f));

            var input = new OptionalStealthInputs
            {
                LegDetectionRisk = ScoutRiskModel.StealthThreat(sightings, Focus),
                ApRemaining = 2, StealthApCost = 1,
                DrawAvailable = true, DrawApCost = 1, DrawOpportunities = 2,
            };
            Assert.That(ScoutOptionalStealthPolicy.Evaluate(input).Decision, Is.EqualTo(OptionalStealthDecision.Enter));
        }

        [Test]
        public void AdjacentGarrisonDetector_IsAPartialThreat_NoForceIsNone()
        {
            var garrison = new List<AiMapMemory.KnownEnemySighting> { Sighting(new HexCoord(5, 3), recce: 1, garrison: true) };
            Assert.That(ScoutRiskModel.StealthThreat(garrison, Focus),
                Is.EqualTo(1f / AiConfigV2.scoutDetectionRiskNorm).Within(1e-5f));
            Assert.That(ScoutRiskModel.StealthThreat(new List<AiMapMemory.KnownEnemySighting>(), Focus), Is.Zero);
        }

        private static AiMapMemory.KnownEnemySighting Sighting(HexCoord hex, int recce, bool garrison) =>
            new AiMapMemory.KnownEnemySighting(hex, Enemy, "e", 2, 4f, 4f, null,
                recceRadius: recce, recceSpotStrength: recce, seenTurn: 11, armyId: 77,
                isGarrison: garrison);

        private static WorldSnapshot WithSightings(params AiMapMemory.KnownEnemySighting[] s)
        {
            var player = new PlayerSetupData { Nickname = "Recon risk" };
            WorldSnapshot snap = AiReconAuditBugTests.Snapshot(player, 11, Focus);
            snap.Known = new KnownSnapshot { EnemySightings = new List<AiMapMemory.KnownEnemySighting>(s) };
            return snap;
        }
    }
}
#endif
