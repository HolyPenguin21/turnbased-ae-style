#if UNITY_INCLUDE_TESTS
using System;
using System.Collections.Generic;
using Game.Ai;
using Game.Ai.V2;
using Game.Combat;
using Game.HexGrid;
using Game.Players;
using NUnit.Framework;

namespace Game.EditorTests
{
    // ===========================================================================================
    //  ATK stage 2 — ATTACK OBJECTIVE DISCOVERY, IDENTITY AND INTRINSIC SCORE.
    //
    //  Covers §83 groups A (target enumeration), B (fog of war), J (target owner changes), plus the
    //  §21/§72 identity rules, the §31 one-defender-package rule, the §34 "site defence is never a
    //  reward" rule and the §36/§38 offensive desire gate. No actor, no mission, no execution:
    //  those arrive with the lane itself in the next stage.
    // ===========================================================================================
    public class AiAttackObjectiveTests
    {
        private static readonly PlayerSetupData Us =
            new PlayerSetupData { Nickname = "Us", ColorIndex = 1 };
        private static readonly PlayerSetupData Red =
            new PlayerSetupData { Nickname = "Red", ColorIndex = 2 };
        private static readonly PlayerSetupData Blue =
            new PlayerSetupData { Nickname = "Blue", ColorIndex = 3 };
        private static readonly PlayerSetupData Neutral =
            new PlayerSetupData { Nickname = "Neutral", ColorIndex = 9, IsNeutral = true };

        private static readonly HexCoord OurBase = new HexCoord(0, 0);
        private static readonly HexCoord RedBase = new HexCoord(6, 0);
        private static readonly HexCoord RedCitadel = new HexCoord(9, 0);

        // ---- §83 A: target enumeration ---------------------------------------------------

        [Test]
        public void Enumerate_KnownHostileBaseAndCitadel_BothBecomeObjectives()
        {
            List<AttackObjective> objectives = AttackObjectiveEvaluator.Enumerate(
                Snap(new[] { B(OurBase, Us), B(RedBase, Red), B(RedCitadel, Red, citadel: true, isBase: false) },
                    ownBases: new[] { OurBase }));

            Assert.That(objectives.Count, Is.EqualTo(2));
            Assert.That(objectives.Exists(o => o.Target.Hex.Equals(RedBase)), Is.True);
            Assert.That(objectives.Exists(o => o.Target.Hex.Equals(RedCitadel)), Is.True);
            Assert.That(objectives.Exists(o => o.Target.Hex.Equals(OurBase)), Is.False,
                "our own Base is never an Attack target");
        }

        [Test]
        public void Enumerate_NeutralAndNonBaseAndEliminatedOwner_AreNotAttackTargets()
        {
            var elsewhere = new HexCoord(4, 0);
            var dead = new PlayerSetupData { Nickname = "Dead", ColorIndex = 4, IsEliminated = true };

            Assert.That(AttackObjectiveEvaluator.Enumerate(
                    Snap(new[] { B(elsewhere, Neutral) }, new[] { OurBase })),
                Is.Empty, "a neutral Base belongs to Raid's world, not Attack's");
            Assert.That(AttackObjectiveEvaluator.Enumerate(
                    Snap(new[] { B(elsewhere, Red, isBase: false) }, new[] { OurBase })),
                Is.Empty, "an enemy Facility that is not a Base/Citadel is not a strategic node");
            Assert.That(AttackObjectiveEvaluator.Enumerate(
                    Snap(new[] { B(elsewhere, dead) }, new[] { OurBase })),
                Is.Empty, "an eliminated player's structure is not an objective");
        }

        [Test]
        public void Enumerate_UnobservedBase_IsNotATarget()
        {
            Assert.That(AttackObjectiveEvaluator.Enumerate(
                    Snap(Array.Empty<AiMapMemory.KnownBuilding>(), new[] { OurBase })),
                Is.Empty,
                "honest structural memory is the ONLY source; nothing else may reveal a Base");
        }

        [Test]
        public void Enumerate_HexWeNowHold_DropsOutEvenWhileMemoryStillSaysEnemy()
        {
            // Self.BaseHexes is current truth; Known.Buildings still remembers the old owner.
            Assert.That(AttackObjectiveEvaluator.Enumerate(
                    Snap(new[] { B(RedBase, Red) }, new[] { OurBase, RedBase })),
                Is.Empty);
        }

        // ---- §62/§63 and §83 O — a base we LOST comes back as an ordinary Attack target ---

        // No RecaptureMission exists and none should: once our own topology no longer contains the
        // hex and our own honest memory has seen the new owner on it, the hex satisfies exactly the
        // §19 hostile-structure test every other Attack candidate satisfies. This is the knowledge
        // invariant the whole "lost base" chain rests on, so it is pinned here rather than assumed.
        [Test]
        public void Enumerate_OwnBaseLostAndReObserved_BecomesAnOrdinaryAttackTarget()
        {
            HexCoord lost = OurBase;

            // Still ours: current truth wins over any memory record on the same hex.
            Assert.That(AttackObjectiveEvaluator.Enumerate(
                    Snap(new[] { B(lost, Us) }, new[] { lost })),
                Is.Empty);

            // Captured by Red and re-observed: out of Self.BaseHexes, remembered under Red.
            List<AttackObjective> after = AttackObjectiveEvaluator.Enumerate(
                Snap(new[] { B(lost, Red) }, Array.Empty<HexCoord>()));

            Assert.That(after.Count, Is.EqualTo(1));
            Assert.That(after[0].Target.Hex, Is.EqualTo(lost));
            Assert.That(after[0].Target.ExpectedOwner, Is.EqualTo(Red));
            Assert.That(AttackObjectiveEvaluator.EvaluateTarget(
                    Snap(new[] { B(lost, Red) }, Array.Empty<HexCoord>()), after[0].Target),
                Is.EqualTo(AttackObjectiveEvaluator.AttackTargetStatus.Continue));
        }

        // ---- §21/§72 identity ------------------------------------------------------------

        [Test]
        public void TargetIdentity_IsHexPlusOwner_AndKindIsOnlyMetadata()
        {
            AttackTargetRef red = AttackTargetRef.For(RedBase, Red, AttackTargetKind.Base);
            AttackTargetRef blue = AttackTargetRef.For(RedBase, Blue, AttackTargetKind.Base);
            AttackTargetRef reclassified = AttackTargetRef.For(RedBase, Red, AttackTargetKind.Citadel);

            Assert.That(red.Equals(blue), Is.False,
                "the same hex under a different opponent is a different operation");
            Assert.That(MissionIntentKey.ForAttack(red).Equals(MissionIntentKey.ForAttack(blue)),
                Is.False);
            Assert.That(red.Equals(reclassified), Is.True,
                "a better observation may refine Base/Citadel without changing the objective");
            Assert.That(MissionIntentKey.ForAttack(red)
                .Equals(MissionIntentKey.ForAttack(reclassified)), Is.True);
            Assert.That(MissionIntentKey.ForAttack(red).Kind, Is.EqualTo(MissionKind.Attack));
            Assert.That(MissionIntentKey.ForAttack(red).ObjectiveId, Is.EqualTo(Red.ColorIndex),
                "owner identity is the stable numeric player id, never a hash or a nickname");
        }

        [Test]
        public void IntentKey_SurvivesChangesToTheSurroundingEnemyPicture()
        {
            // §72 — killing a weak enemy beside the route must not touch the Attack identity.
            WorldSnapshot before = Snap(new[] { B(RedBase, Red) }, new[] { OurBase },
                new[] { Sighting(31, new HexCoord(3, 0), Red, Body(5, 2, 8, 2)) });
            WorldSnapshot after = Snap(new[] { B(RedBase, Red) }, new[] { OurBase });

            Assert.That(AttackObjectiveEvaluator.Enumerate(before)[0].IntentKey,
                Is.EqualTo(AttackObjectiveEvaluator.Enumerate(after)[0].IntentKey));
        }

        // ---- §83 B/J: fog of war and owner changes ---------------------------------------

        [Test]
        public void EvaluateTarget_CoversCaptureInvalidationAndContinuation()
        {
            AttackTargetRef red = AttackTargetRef.For(RedBase, Red, AttackTargetKind.Base);

            Assert.That(AttackObjectiveEvaluator.EvaluateTarget(
                    Snap(new[] { B(RedBase, Red) }, new[] { OurBase }), red),
                Is.EqualTo(AttackObjectiveEvaluator.AttackTargetStatus.Continue));
            Assert.That(AttackObjectiveEvaluator.EvaluateTarget(
                    Snap(new[] { B(RedBase, Red) }, new[] { OurBase, RedBase }), red),
                Is.EqualTo(AttackObjectiveEvaluator.AttackTargetStatus.Captured),
                "ownership landing in our own Base topology IS the success condition");
            Assert.That(AttackObjectiveEvaluator.EvaluateTarget(
                    Snap(new[] { B(RedBase, Blue) }, new[] { OurBase }), red),
                Is.EqualTo(AttackObjectiveEvaluator.AttackTargetStatus.Invalidated),
                "a third party taking it makes this a different objective");
            Assert.That(AttackObjectiveEvaluator.EvaluateTarget(
                    Snap(Array.Empty<AiMapMemory.KnownBuilding>(), new[] { OurBase }), red),
                Is.EqualTo(AttackObjectiveEvaluator.AttackTargetStatus.Invalidated));
            Assert.That(AttackObjectiveEvaluator.EvaluateTarget(
                    Snap(new[] { B(RedBase, Red, isBase: false) }, new[] { OurBase }), red),
                Is.EqualTo(AttackObjectiveEvaluator.AttackTargetStatus.Invalidated));
        }

        [Test]
        public void EvaluateTarget_FoggedTarget_KeepsItsLastHonestAnswer()
        {
            AttackTargetRef red = AttackTargetRef.For(RedBase, Red, AttackTargetKind.Base);

            Assert.That(AttackObjectiveEvaluator.EvaluateTarget(
                    Snap(new[] { B(RedBase, Red, seenTurn: 2) }, new[] { OurBase }, null, turn: 40), red),
                Is.EqualTo(AttackObjectiveEvaluator.AttackTargetStatus.Continue),
                "38 turns without a look is not evidence that anything changed");
        }

        // ---- §31 one defender package ----------------------------------------------------

        [Test]
        public void SiteDefenders_GarrisonAndFieldArmies_AreOneDefenderPackage()
        {
            WorldSnapshot snap = Snap(new[] { B(RedBase, Red) }, new[] { OurBase }, new[]
            {
                Sighting(14, RedBase, Red, Body(4, 2, 9, 2), Body(4, 2, 9, 1)),
                Sighting(21, RedBase, Red, Body(6, 3, 10, 3)),
                Sighting(99, new HexCoord(2, 0), Red, Body(9, 9, 9, 9)),
            });

            List<AttackObjective> objectives = AttackObjectiveEvaluator.Enumerate(snap);

            Assert.That(objectives.Count, Is.EqualTo(1),
                "defenders of a Base never become objectives of their own");
            Assert.That(objectives[0].DefenderCount, Is.EqualTo(3),
                "every known body ON the site is one fight; a body elsewhere is not");
        }

        // ---- §34 the site's own defence is never a reward ---------------------------------

        [Test]
        public void IntrinsicScore_IsNotRaisedByDefenceOrDefenders()
        {
            float plain = AttackObjectiveEvaluator.Enumerate(
                Snap(new[] { B(RedBase, Red, defense: 0f) }, new[] { OurBase }))[0].BaseValue;
            float fortified = AttackObjectiveEvaluator.Enumerate(
                Snap(new[] { B(RedBase, Red, defense: 20f) }, new[] { OurBase }))[0].BaseValue;
            float defended = AttackObjectiveEvaluator.Enumerate(
                Snap(new[] { B(RedBase, Red) }, new[] { OurBase }, new[]
                {
                    Sighting(14, RedBase, Red, Body(4, 2, 9, 2), Body(6, 3, 10, 3)),
                }))[0].BaseValue;

            Assert.That(fortified, Is.EqualTo(plain).Within(0.0001f),
                "a better defended Base must not be worth MORE to attack");
            Assert.That(defended, Is.LessThanOrEqualTo(plain + 0.0001f),
                "defender power belongs to WinChance, not to a reward slot");
        }

        [Test]
        public void IntrinsicScore_UsesOnlyTheCanonicalSlotsAttackIsAllowed()
        {
            AttackObjective citadel = AttackObjectiveEvaluator.Enumerate(
                Snap(new[] { B(RedCitadel, Red, citadel: true, isBase: false) },
                    new[] { OurBase }))[0];
            AttackObjective ordinary = AttackObjectiveEvaluator.Enumerate(
                Snap(new[] { B(RedCitadel, Red) }, new[] { OurBase }))[0];

            Assert.That(citadel.TaskScore.StrategicRelevance,
                Is.GreaterThan(ordinary.TaskScore.StrategicRelevance),
                "a Citadel is a more relevant strategic node than an ordinary Base");
            Assert.That(citadel.TaskScore.TerrainDefense, Is.EqualTo(0f),
                "defender-side terrain is not an attacker bonus");
            Assert.That(citadel.TaskScore.MilitaryTargetRelevance, Is.EqualTo(0f),
                "§33 does not list this slot for Attack; Raid's fixed reward is not borrowed");
            Assert.That(citadel.TaskScore.EconomicExpansionValue, Is.EqualTo(0f),
                "no economic justification is claimed until the Economy model actually proves one");
        }

        [Test]
        public void IntrinsicScore_PenalisesStaleStructuralIntel()
        {
            AttackObjective fresh = AttackObjectiveEvaluator.Enumerate(
                Snap(new[] { B(RedBase, Red, seenTurn: 6) }, new[] { OurBase }, null, turn: 6))[0];
            AttackObjective stale = AttackObjectiveEvaluator.Enumerate(
                Snap(new[] { B(RedBase, Red, seenTurn: 1) }, new[] { OurBase }, null, turn: 30))[0];
            AttackObjective unstamped = AttackObjectiveEvaluator.Enumerate(
                Snap(new[] { B(RedBase, Red, seenTurn: 0) }, new[] { OurBase }, null, turn: 3))[0];

            Assert.That(stale.TaskScore.Staleness, Is.LessThan(fresh.TaskScore.Staleness),
                "Staleness is a penalty slot, so older intel scores lower");
            Assert.That(unstamped.IntelAgeTurns,
                Is.GreaterThanOrEqualTo(AiConfigV2.scoutSurveilStaleTurnsHi),
                "an unstamped record means 'age unknown', never 'observed on turn 0'");
        }

        // ---- §36/§38 the offensive desire gate -------------------------------------------

        [Test]
        public void OffensiveGate_OpensOnAKnownHostileBaseWithNoNeutralsLeft()
        {
            List<AttackObjective> objectives = AttackObjectiveEvaluator.Enumerate(
                Snap(new[] { B(RedBase, Red) }, new[] { OurBase }));
            var noNeutrals = new CombatOpportunityReport();

            Assert.That(StrategyLayer.HasOffensiveTarget(noNeutrals, objectives), Is.True,
                "a cleared map of neutrals must not make the war half of Aggression unreachable");
            Assert.That(StrategyLayer.HasOffensiveTarget(noNeutrals, new List<AttackObjective>()),
                Is.False);
            Assert.That(StrategyLayer.BestAttackOpportunity(objectives),
                Is.GreaterThanOrEqualTo(StrategyLayer.BestAttackOpportunity(new List<AttackObjective>())),
                "opportunity comes from the canonical world score, not a fixed capture bonus");
        }

        // ---- helpers ---------------------------------------------------------------------

        private static AiMapMemory.KnownBuilding B(HexCoord hex, PlayerSetupData owner,
            bool isBase = true, bool citadel = false, float defense = 0f, int seenTurn = 5) =>
            new AiMapMemory.KnownBuilding(hex, owner, citadel, null, null, 0, isBase, defense, seenTurn);

        private static WorthIt.DefenderProfile Body(float atk, float def, float hp, int init) =>
            new WorthIt.DefenderProfile(def, false, null, atk, hp, init, null, hp);

        private static AiMapMemory.KnownEnemySighting Sighting(int armyId, HexCoord hex,
            PlayerSetupData owner, params WorthIt.DefenderProfile[] bodies) =>
            new AiMapMemory.KnownEnemySighting(hex, owner, "sighted", bodies.Length, 0f, 0f,
                new List<WorthIt.DefenderProfile>(bodies), false, 0, 0, 5, armyId);

        private static WorldSnapshot Snap(IEnumerable<AiMapMemory.KnownBuilding> buildings,
            IEnumerable<HexCoord> ownBases = null,
            IEnumerable<AiMapMemory.KnownEnemySighting> sightings = null, int turn = 6) =>
            new WorldSnapshot
            {
                TurnNumber = turn,
                Observer = Us,
                Self = new SelfSnapshot
                {
                    BaseHexes = ownBases != null ? new List<HexCoord>(ownBases) : new List<HexCoord>(),
                    Citadel = OurBase,
                    Armies = new List<ArmySnapshot>(),
                },
                Known = new KnownSnapshot
                {
                    Buildings = new List<AiMapMemory.KnownBuilding>(buildings),
                    EnemySightings = sightings != null
                        ? new List<AiMapMemory.KnownEnemySighting>(sightings)
                        : new List<AiMapMemory.KnownEnemySighting>(),
                },
                Threat = new ThreatModel
                {
                    Contacts = new List<EnemyContactSnapshot>(),
                    Threats = new List<AssetThreatSnapshot>(),
                },
            };
    }
}
#endif
