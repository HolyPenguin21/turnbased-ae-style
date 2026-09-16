#if UNITY_INCLUDE_TESTS
using System.Collections.Generic;
using System.Linq;
using Game.Ai;
using Game.Ai.V2;
using Game.Cards;
using Game.Combat;
using Game.HexGrid;
using Game.Players;
using NUnit.Framework;

namespace Game.EditorTests
{
    public class AiRaidConfidenceTests
    {
        [Test]
        public void FreshNeutralSighting_UsesItsOwnObservationTurn_NotEnemyThreatContacts()
        {
            WorldSnapshot snap = Snapshot(turn: 11);
            var neutral = new PlayerSetupData { IsNeutral = true, Nickname = "Neutral" };
            var hex = new HexCoord(7, 2);
            snap.Known.NeutralSightings = new List<AiMapMemory.KnownEnemySighting>
            {
                new AiMapMemory.KnownEnemySighting(hex, neutral, "Neutral", 1, 1f, 1f,
                    new List<WorthIt.DefenderProfile> { Weak() }, seenTurn: 11, armyId: 8),
            };

            CombatOpportunity opportunity = CombatOpportunityAnalyzer.Analyze(snap).NeutralOpportunities.Single();

            Assert.That(snap.Threat.Contacts, Is.Empty);
            Assert.That(opportunity.Confidence, Is.EqualTo(AiConfigV2.threatConfidenceExact));
        }

        [Test]
        public void OldNeutralSighting_KeepsLastKnownConfidence_NotFabricatedExactKnowledge()
        {
            WorldSnapshot snap = Snapshot(turn: 11);
            var neutral = new PlayerSetupData { IsNeutral = true, Nickname = "Neutral" };
            snap.Known.NeutralSightings = new List<AiMapMemory.KnownEnemySighting>
            {
                new AiMapMemory.KnownEnemySighting(new HexCoord(7, 2), neutral, "Neutral", 1, 1f, 1f,
                    new List<WorthIt.DefenderProfile> { Weak() }, seenTurn: 10, armyId: 8),
            };

            CombatOpportunity opportunity = CombatOpportunityAnalyzer.Analyze(snap).NeutralOpportunities.Single();

            Assert.That(opportunity.Confidence, Is.EqualTo(AiConfigV2.threatConfidenceLastKnown));
        }

        [Test]
        public void KnownFixedEventGuard_NoEnemyContact_DoesNotGetPhantomStalenessPenalty()
        {
            WorldSnapshot snap = Snapshot(turn: 11);
            var hex = new HexCoord(7, 2);
            var defenders = new List<WorthIt.DefenderProfile> { Weak(), Weak(), Weak() };
            snap.Known.EventGuards = new List<KnownEventGuardSnapshot>
            {
                new KnownEventGuardSnapshot(hex,
                    new AiMapMemory.GuardStrength(3f, 3f, defenders, "Guard"), "Guard", defenders.Count),
            };

            CombatOpportunityReport report = CombatOpportunityAnalyzer.Analyze(snap);
            CombatOpportunity opportunity = report.NeutralOpportunities.Single();
            List<AggressionObjective> objectives = AggressionObjectiveEvaluator.Enumerate(snap, report);

            Assert.That(snap.Threat.Contacts, Is.Empty);
            Assert.That(opportunity.Confidence, Is.EqualTo(AiConfigV2.threatConfidenceExact));
            Assert.That(objectives, Has.Count.EqualTo(1));
            Assert.That(objectives[0].TaskScore.Staleness, Is.Zero);
        }

        private static WorldSnapshot Snapshot(int turn)
        {
            var strong = new WorthIt.DefenderProfile(defense: 1f, hasCeramicArmor: false,
                attack: 25f, hitPoints: 30f, maxHitPoints: 30f);
            return new WorldSnapshot
            {
                TurnNumber = turn,
                Self = new SelfSnapshot
                {
                    BaseHexes = new List<HexCoord> { new HexCoord(0, 0) },
                    Armies = new List<ArmySnapshot>
                    {
                        new ArmySnapshot
                        {
                            ArmyId = 100,
                            Hex = new HexCoord(0, 0),
                            IsStructuralRaidActor = true,
                            MaxMovement = 3,
                            MemberCount = 2,
                            Members = new List<WorthIt.DefenderProfile> { strong, strong },
                        },
                    },
                    Hand = new List<CardData>(),
                    Deck = new List<CardDefinition>(),
                    FieldPower = 100f,
                },
                Known = new KnownSnapshot
                {
                    EnemySightings = new List<AiMapMemory.KnownEnemySighting>(),
                    NeutralSightings = new List<AiMapMemory.KnownEnemySighting>(),
                    EventGuards = new List<KnownEventGuardSnapshot>(),
                },
                Threat = new ThreatModel { Contacts = new List<EnemyContactSnapshot>() },
            };
        }

        private static WorthIt.DefenderProfile Weak() =>
            new WorthIt.DefenderProfile(defense: 1f, hasCeramicArmor: false,
                attack: 1f, hitPoints: 10f, maxHitPoints: 10f);
    }
}
#endif
