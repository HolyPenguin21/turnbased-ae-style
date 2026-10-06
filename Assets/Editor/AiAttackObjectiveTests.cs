#if UNITY_INCLUDE_TESTS
using System;
using System.Collections.Generic;
using System.Linq;
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
        [TestCase(79f, false)]
        [TestCase(80f, false)]
        [TestCase(80.001f, true)]
        public void FreshAttack_RequiresStrictlyMoreThanFourFifthsOfCurrentPeak(
            float armyPower, bool expected)
        {
            Assert.That(AttackForceReadiness.ForceReady(armyPower, 100f), Is.EqualTo(expected));
        }

        [Test]
        public void CurrentPeak_UsesCapacityOfTheHeroActuallyInTheStack()
        {
            var pool = new List<AiPower.PowerUnit>
            {
                new AiPower.PowerUnit(40f, null, 1, true, 2),
                new AiPower.PowerUnit(1f, null, 1, true, 5),
                new AiPower.PowerUnit(30f, null, 1, false),
                new AiPower.PowerUnit(29f, null, 1, false),
                new AiPower.PowerUnit(28f, null, 1, false),
                new AiPower.PowerUnit(27f, null, 1, false),
            };
            float peak = AiPower.TotalMilitaryPotential(pool);
            float impossible = AiPower.EffectiveArmyPower(
                AiPower.ComposeStack(pool, 5));
            Assert.That(peak, Is.LessThan(impossible),
                "the strong two-slot hero cannot command the five-slot roster");
            Assert.That(AiPower.TotalMilitaryPotential(pool.Skip(1).ToList()), Is.EqualTo(peak),
                "losing the unused two-slot hero cannot reduce the five-slot commander's peak");
            Assert.That(AiPower.TotalMilitaryPotential(pool.Where((_, index) => index != 1).ToList()), Is.LessThan(peak),
                "losing the commander of the best roster must reduce the current peak");
        }

        [Test]
        public void CurrentPeak_FollowsLossAndRewardButNotCardLocation()
        {
            var hero = new AiPower.PowerUnit(10f, null, 1, true, 3);
            var body = new AiPower.PowerUnit(20f, null, 1, false);
            var reward = new AiPower.PowerUnit(100f, null, 1, false);
            var map = new List<AiPower.PowerUnit> { hero };
            var hand = new List<AiPower.PowerUnit> { body };
            var deck = new List<AiPower.PowerUnit>();
            float initial = AiPower.TotalMilitaryPotential(map.Concat(hand).Concat(deck).ToList());
            hand.Remove(body);
            deck.Add(body);
            Assert.That(AiPower.TotalMilitaryPotential(map.Concat(hand).Concat(deck).ToList()),
                Is.EqualTo(initial));
            deck.Remove(body);
            map.Add(body);
            Assert.That(AiPower.TotalMilitaryPotential(map.Concat(hand).Concat(deck).ToList()),
                Is.EqualTo(initial));
            Assert.That(AiPower.TotalMilitaryPotential(map.Take(1).ToList()), Is.LessThan(initial));
            deck.Add(reward);
            Assert.That(AiPower.TotalMilitaryPotential(map.Concat(deck).ToList()), Is.GreaterThan(initial));
        }
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
        public void Enumerate_EnemyNonBaseStructure_IsNeverAnAttackTarget()
        {
            var site = new HexCoord(4, 0);
            Assert.That(AttackObjectiveEvaluator.Enumerate(
                Snap(new[] { B(site, Red, isBase: false) }, new[] { OurBase })), Is.Empty);
            Assert.That(AttackObjectiveEvaluator.Enumerate(
                Snap(new[] { B(site, Red, isBase: false) }, new[] { OurBase },
                    new[] { Sighting(77, site, Red, Body(2f, 2f, 3f, 1)) })), Is.Empty,
                "a defender does not make a non-Base building an Attack objective");
        }

        [TestCase(Game.Cards.UnitAbilities.CollectHuman)]
        [TestCase(Game.Cards.UnitAbilities.CollectEnergy)]
        [TestCase(Game.Cards.UnitAbilities.CollectMaterials)]
        [TestCase(Game.Cards.UnitAbilities.CollectTech)]
        public void Enumerate_ExtractionOnlyFacility_IsNotAnAttackTarget(string extraction)
        {
            var site = new HexCoord(4, 0);
            var building = new AiMapMemory.KnownBuilding(site, Red, false,
                new[] { extraction }, isBase: false);
            WorldSnapshot snap = Snap(new[] { building }, new[] { OurBase },
                new[] { Sighting(77, site, Red, Body(2f, 2f, 3f, 1)) });

            Assert.That(AttackObjectiveEvaluator.Enumerate(snap), Is.Empty);
            Assert.That(ActiveDefenceObjectiveEvaluator.OnKnownForeignStructure(snap, site), Is.True,
                "a tactical detour must still avoid fighting on an extraction site");
        }

        [Test]
        public void Enumerate_IntelCenter_IsNotAnIndependentAttackObjective()
        {
            var site = new HexCoord(4, 0);
            // Intel Center is a Facility card with ApBonus. Its slot belongs to a Base;
            // the non-Base record also tests that its ability cannot create a target.
            var building = new AiMapMemory.KnownBuilding(site, Red, false,
                new[] { Game.Cards.UnitAbilities.ApBonus },
                isBase: false);

            Assert.That(AttackObjectiveEvaluator.Enumerate(
                Snap(new[] { building }, new[] { OurBase })), Is.Empty);
            var baseWithFacilities = new AiMapMemory.KnownBuilding(site, Red, false,
                new[] { Game.Cards.UnitAbilities.ApBonus },
                isBase: true);
            Assert.That(AttackObjectiveEvaluator.Enumerate(
                Snap(new[] { baseWithFacilities }, new[] { OurBase })), Has.Count.EqualTo(1),
                "the Base remains one objective; its Intel Center is never a separate target");
        }

        [Test]
        public void EvaluateTarget_FormerBaseNowNonBase_IsInvalidated()
        {
            var site = new HexCoord(4, 0);
            AttackTargetRef target = AttackTargetRef.For(site, Red, AttackTargetKind.Base);

            Assert.That(AttackObjectiveEvaluator.EvaluateTarget(
                    Snap(new[] { B(site, Red) }, new[] { OurBase }), target),
                Is.EqualTo(AttackObjectiveEvaluator.AttackTargetStatus.Continue));
            Assert.That(AttackObjectiveEvaluator.EvaluateTarget(
                    Snap(Array.Empty<AiMapMemory.KnownBuilding>(), new[] { OurBase }), target),
                Is.EqualTo(AttackObjectiveEvaluator.AttackTargetStatus.Invalidated));
            Assert.That(AttackObjectiveEvaluator.EvaluateTarget(
                    Snap(new[] { B(site, Red, isBase: false) }, new[] { OurBase },
                        new[] { Sighting(77, site, Red, Body(2f, 2f, 3f, 1)) }), target),
                Is.EqualTo(AttackObjectiveEvaluator.AttackTargetStatus.Invalidated),
                "a non-Base building does not preserve an obsolete Attack intent");
        }

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
            Assert.That(citadel.TaskScore.AttackReadiness, Is.GreaterThanOrEqualTo(0f));
            Assert.That(citadel.TaskScore.RaidReward, Is.EqualTo(0f),
                "Attack never borrows the Raid reward slot");
            Assert.That(citadel.TaskScore.EconomicExpansionValue, Is.EqualTo(0f),
                "no economic justification is claimed until the Economy model actually proves one");
        }

        [Test]
        public void IntrinsicScore_MilitaryRealizationRaisesAttackThroughCanonicalSlot()
        {
            WorldSnapshot low = Snap(new[] { B(RedBase, Red) }, new[] { OurBase });
            low.Self.FistPower = 10f;
            low.Self.FieldPotential = 100f;
            low.Self.TotalMilitaryPotential = 100f;
            WorldSnapshot high = Snap(new[] { B(RedBase, Red) }, new[] { OurBase });
            high.Self.FistPower = 90f;
            high.Self.FieldPotential = 100f;
            high.Self.TotalMilitaryPotential = 100f;

            AttackObjective lowAttack = AttackObjectiveEvaluator.Enumerate(low)[0];
            AttackObjective highAttack = AttackObjectiveEvaluator.Enumerate(high)[0];
            Assert.That(highAttack.TaskScore.AttackReadiness,
                Is.GreaterThan(lowAttack.TaskScore.AttackReadiness));
            Assert.That(highAttack.BaseValue, Is.GreaterThan(lowAttack.BaseValue));
        }

        [Test]
        public void IntrinsicScore_DoesNotPriceStaleStructuralIntel()
        {
            AttackObjective fresh = AttackObjectiveEvaluator.Enumerate(
                Snap(new[] { B(RedBase, Red, seenTurn: 6) }, new[] { OurBase }, null, turn: 6))[0];
            AttackObjective stale = AttackObjectiveEvaluator.Enumerate(
                Snap(new[] { B(RedBase, Red, seenTurn: 1) }, new[] { OurBase }, null, turn: 30))[0];
            AttackObjective unstamped = AttackObjectiveEvaluator.Enumerate(
                Snap(new[] { B(RedBase, Red, seenTurn: 0) }, new[] { OurBase }, null, turn: 3))[0];

            Assert.That(stale.TaskScore.IntelAgePenalty, Is.Zero,
                "an Attack structure is rarely re-observed; fresh intel is a bonus, never a price");
            Assert.That(fresh.TaskScore.IntelAgePenalty, Is.Zero);
            Assert.That(stale.TaskScore.Staleness, Is.Zero,
                "Staleness is Recon's refresh value, never Attack's penalty");
            Assert.That(stale.IntelAgeTurns, Is.GreaterThan(fresh.IntelAgeTurns),
                "intel age is still tracked on the objective");
            Assert.That(unstamped.IntelAgeTurns,
                Is.GreaterThanOrEqualTo(AiConfigV2.reconIntelStaleTurnsHi),
                "an unstamped record means 'age unknown', never 'observed on turn 0'");
        }

        // Presence of combat activity belongs to the Radar axis, independent of target score.
        [Test]
        public void AggressionActivity_RecognizesHostileStructureWithoutScoringAttack()
        {
            WorldSnapshot known = Snap(new[] { B(RedBase, Red) }, new[] { OurBase });
            WorldSnapshot unknown = Snap(Array.Empty<AiMapMemory.KnownBuilding>(), new[] { OurBase });
            Assert.That(ForceNeedModel.HasKnownCombatActivity(known), Is.True);
            Assert.That(ForceNeedModel.HasKnownCombatActivity(unknown), Is.False);
        }

        [Test]
        public void ActiveDefenceThreatSeverity_DoesNotFeedAggressionRadarDesire()
        {
            WorldSnapshot low = Snap(new[] { B(RedBase, Red) }, new[] { OurBase });
            WorldSnapshot high = Snap(new[] { B(RedBase, Red) }, new[] { OurBase });
            low.Threat.Threats = new[] { Threat(0.1f) };
            high.Threat.Threats = new[] { Threat(0.95f) };

            RadarAssessment lowAssessment = StrategyLayer.Evaluate(low, new AiRadarState());
            RadarAssessment highAssessment = StrategyLayer.Evaluate(high, new AiRadarState());

            Assert.That(highAssessment.Desires.MilitaryThreat,
                Is.GreaterThan(lowAssessment.Desires.MilitaryThreat),
                "the general threat fact must still report the more severe danger");
            Assert.That(highAssessment.Desires.Raw[DesireAxis.Aggression],
                Is.EqualTo(lowAssessment.Desires.Raw[DesireAxis.Aggression]).Within(0.0001f),
                "threat severity must not raise every Aggression peer through Radar");
        }

        // Log 2026-09-27, Kryll T9–T11: under siege the former damp cut AGG raw to 0.19 (surplus and
        // edge both 1.00), so ActiveDefence — same axis — lost the AP pool to Recon/Development.
        // Home threat must move the Aggression Radar in neither direction; offensive restraint is
        // the Raid/Attack CitadelThreatRisk slot instead.
        [Test]
        public void Siege_DoesNotDampAggressionRadar_OffenceCarriesCitadelThreatRisk()
        {
            WorldSnapshot calm = Snap(new[] { B(RedBase, Red) }, new[] { OurBase });
            WorldSnapshot siege = Snap(new[] { B(RedBase, Red) }, new[] { OurBase });
            siege.Threat.UnderSiege = true;
            siege.Threat.CitadelThreatSeverity = 0.8f;

            Assert.That(StrategyLayer.Evaluate(siege, new AiRadarState()).Desires.Raw[DesireAxis.Aggression],
                Is.EqualTo(StrategyLayer.Evaluate(calm, new AiRadarState()).Desires.Raw[DesireAxis.Aggression])
                    .Within(0.0001f),
                "siege must not scale down the axis ActiveDefence shares with Raid/Attack");

            TaskScore calmAttack = AttackObjectiveEvaluator.Enumerate(calm).Single().TaskScore;
            TaskScore siegeAttack = AttackObjectiveEvaluator.Enumerate(siege).Single().TaskScore;
            Assert.That(calmAttack.CitadelThreatRisk, Is.Zero);
            Assert.That(siegeAttack.CitadelThreatRisk,
                Is.EqualTo(0.8f * AiConfigV2.taskScoreCitadelThreatRiskMax).Within(0.0001f));
            Assert.That(siegeAttack.Value, Is.EqualTo(calmAttack.Value - siegeAttack.CitadelThreatRisk)
                .Within(0.0001f), "offensive restraint is exactly the home-threat slot");
        }

        [Test]
        public void AttackTaskScore_DoesNotFeedAggressionRadarDesire()
        {
            WorldSnapshot fresh = Snap(new[] { B(RedBase, Red, seenTurn: 6) }, new[] { OurBase });
            WorldSnapshot stale = Snap(new[] { B(RedBase, Red, seenTurn: 1) }, new[] { OurBase });
            float freshTask = AttackObjectiveEvaluator.Enumerate(fresh).Single().TaskScore.Value;
            float staleTask = AttackObjectiveEvaluator.Enumerate(stale).Single().TaskScore.Value;

            float freshAxis = StrategyLayer.Evaluate(fresh, new AiRadarState())
                .Desires.Raw[DesireAxis.Aggression];
            float staleAxis = StrategyLayer.Evaluate(stale, new AiRadarState())
                .Desires.Raw[DesireAxis.Aggression];

            Assert.That(freshTask, Is.Not.EqualTo(staleTask), "staleness changes Attack TaskScore");
            Assert.That(staleAxis, Is.EqualTo(freshAxis).Within(0.0001f),
                "Attack-specific score changes must not feed Radar and then all Aggression peers");
        }

        [Test]
        public void RaidTaskScore_DoesNotFeedAggressionRadarDesire()
        {
            WorldSnapshot near = Snap(Array.Empty<AiMapMemory.KnownBuilding>(), new[] { OurBase });
            WorldSnapshot far = Snap(Array.Empty<AiMapMemory.KnownBuilding>(), new[] { OurBase });
            near.Known.NeutralSightings = new[]
            {
                Sighting(70, new HexCoord(1, 0), Neutral, Body(1f, 1f, 4f, 1)),
            };
            far.Known.NeutralSightings = new[]
            {
                Sighting(70, new HexCoord(12, 0), Neutral, Body(1f, 1f, 4f, 1)),
            };
            float nearTask = RaidObjectiveEvaluator.Enumerate(near,
                CombatOpportunityAnalyzer.Analyze(near)).Single().TaskScore.Value;
            float farTask = RaidObjectiveEvaluator.Enumerate(far,
                CombatOpportunityAnalyzer.Analyze(far)).Single().TaskScore.Value;

            float nearAxis = StrategyLayer.Evaluate(near, new AiRadarState())
                .Desires.Raw[DesireAxis.Aggression];
            float farAxis = StrategyLayer.Evaluate(far, new AiRadarState())
                .Desires.Raw[DesireAxis.Aggression];

            Assert.That(nearTask, Is.Not.EqualTo(farTask), "target proximity changes Raid TaskScore");
            Assert.That(farAxis, Is.EqualTo(nearAxis).Within(0.0001f),
                "Raid-specific score changes must not feed Radar and then all Aggression peers");
        }

        // ---- 2026-10-04 Radar triggers --------------------------------------------------------

        private static PlayerSetupData OpponentWithCitadel(HexCoord citadel) =>
            new PlayerSetupData
            {
                Nickname = "Far", ColorIndex = 6, CitadelHexQ = citadel.Q, CitadelHexR = citadel.R,
            };

        private static void WithOpponent(WorldSnapshot snap, PlayerSetupData opponent) =>
            snap.TrueWorld = new TrueWorldSnapshot
            {
                Opponents = new[] { new OpponentSnapshot { Player = opponent, ArmyCount = 1 } },
            };

        [Test]
        public void AggressionWitness_SanctionedCitadelOrOpenMobilizationNeedsNoSighting()
        {
            WorldSnapshot blind = Snap(Array.Empty<AiMapMemory.KnownBuilding>(), new[] { OurBase });
            Assert.That(ForceNeedModel.HasKnownCombatActivity(blind), Is.False);
            Assert.That(ForceNeedModel.HasAggressionWitness(blind), Is.False,
                "no fight and no war target: Aggression stays cold");

            WorldSnapshot located = Snap(Array.Empty<AiMapMemory.KnownBuilding>(), new[] { OurBase });
            WithOpponent(located, OpponentWithCitadel(new HexCoord(9, 0)));
            Assert.That(ForceNeedModel.HasAggressionWitness(located), Is.True,
                "an opponent's starting Citadel by coordinates is an Attack objective");
            Assert.That(ForceNeedModel.HasMilitaryWitness(located), Is.False,
                "a location is no measured fight: Development need keeps its stricter witness");

            WorldSnapshot mobilized = Snap(Array.Empty<AiMapMemory.KnownBuilding>(), new[] { OurBase });
            mobilized.Self.DeployedPower = 80f;
            mobilized.Self.AvailablePower = 100f;
            Assert.That(ForceNeedModel.HasAggressionWitness(mobilized), Is.True,
                "an open mobilization gate is a war in preparation");
        }

        [Test]
        public void Radar_SanctionedCitadelAloneKeepsTheAggressionAxisWarm()
        {
            WorldSnapshot blind = Snap(Array.Empty<AiMapMemory.KnownBuilding>(), new[] { OurBase });
            WorldSnapshot located = Snap(Array.Empty<AiMapMemory.KnownBuilding>(), new[] { OurBase });
            WithOpponent(located, OpponentWithCitadel(new HexCoord(9, 0)));

            Assert.That(StrategyLayer.Evaluate(blind, new AiRadarState()).Desires.Raw[DesireAxis.Aggression],
                Is.Zero);
            Assert.That(StrategyLayer.Evaluate(located, new AiRadarState()).Desires.Raw[DesireAxis.Aggression],
                Is.GreaterThan(0f), "the Attack preparation is not left to the zero-Radar remainder");
        }

        // The threat reserve used to shrink the surplus term, i.e. a home threat damped the axis
        // ActiveDefence lives on. The earlier siege test had no threatening army, so no reserve.
        [Test]
        public void Radar_HomeThreatReserveDoesNotDampAggression()
        {
            WorldSnapshot calm = Snap(new[] { B(RedBase, Red) }, new[] { OurBase });
            WorldSnapshot threatened = Snap(new[] { B(RedBase, Red) }, new[] { OurBase });
            calm.Self.TotalPower = 20f;
            threatened.Self.TotalPower = 20f;
            threatened.Threat.Threats = new[] { Threat(0.9f) };

            RadarAssessment calmRadar = StrategyLayer.Evaluate(calm, new AiRadarState());
            RadarAssessment threatRadar = StrategyLayer.Evaluate(threatened, new AiRadarState());

            Assert.That(threatRadar.Breakdown.RequiredDefensiveReserve,
                Is.GreaterThan(calmRadar.Breakdown.RequiredDefensiveReserve), "the reserve is still measured");
            Assert.That(threatRadar.Desires.Raw[DesireAxis.Aggression],
                Is.EqualTo(calmRadar.Desires.Raw[DesireAxis.Aggression]).Within(0.0001f),
                "a home threat must not starve ActiveDefence through the shared axis");
        }

        [Test]
        public void Radar_ReconRefreshRisesForALiveAttackTargetNeverObserved()
        {
            WorldSnapshot idle = Snap(new[] { B(RedBase, Red) }, new[] { OurBase });
            WorldSnapshot attacking = Snap(new[] { B(RedBase, Red) }, new[] { OurBase });
            MissionIntentRegistry.Clear();
            try
            {
                float idleRefresh = StrategyLayer.Evaluate(idle, new AiRadarState())
                    .Breakdown.ReconRefreshPressure;
                MissionIntentRegistry.GetOrCreate(Us).Put(new MissionIntent
                {
                    Kind = MissionKind.Attack,
                    Status = IntentStatus.Active,
                    IntentKey = MissionIntentKey.ForAttack(AttackTargetRef.For(RedBase, Red, AttackTargetKind.Base)),
                    Objective = new AttackIntent
                    {
                        Target = AttackTargetRef.For(RedBase, Red, AttackTargetKind.Base),
                        Phase = AttackMissionPhase.Assault,
                        PrimaryArmyId = 7,
                    },
                });
                float attackRefresh = StrategyLayer.Evaluate(attacking, new AiRadarState())
                    .Breakdown.ReconRefreshPressure;

                Assert.That(attackRefresh - idleRefresh,
                    Is.EqualTo(AiConfigV2.reconRefreshWeightAttackTarget).Within(0.0001f),
                    "the live Attack's unobserved target is its own Refresh term");
            }
            finally
            {
                MissionIntentRegistry.Clear();
            }
        }

        [Test]
        public void ForceNeed_DefendedHostileBaseBeyondOurStrongestStackIsAnUnwinnableFight()
        {
            WorthIt.DefenderProfile[] garrison = { Body(9f, 6f, 20f, 4), Body(9f, 6f, 20f, 4) };
            WorldSnapshot weak = Snap(new[] { B(RedBase, Red) }, new[] { OurBase },
                new[] { Sighting(50, RedBase, Red, garrison) });
            WorldSnapshot strong = Snap(new[] { B(RedBase, Red) }, new[] { OurBase },
                new[] { Sighting(50, RedBase, Red, garrison) });
            WorldSnapshot unobserved = Snap(new[] { B(RedBase, Red) }, new[] { OurBase });
            weak.Self.FieldPower = 1f;
            strong.Self.FieldPower = 500f;

            Assert.That(ForceNeedModel.ChangeKey(weak), Does.Contain("sites=1/1"));
            Assert.That(ForceNeedModel.ChangeKey(strong), Does.Contain("sites=1/0"));
            Assert.That(ForceNeedModel.ChangeKey(unobserved), Does.Contain("sites=0/0"),
                "a site without a known garrison is no measured fight");
            Assert.That(ForceNeedModel.JustifiedForceNeed(weak).Offensive, Is.EqualTo(1f).Within(0.0001f));
        }

        // ---- helpers ---------------------------------------------------------------------

        private static AiMapMemory.KnownBuilding B(HexCoord hex, PlayerSetupData owner,
            bool isBase = true, bool citadel = false, float defense = 0f, int seenTurn = 5) =>
            new AiMapMemory.KnownBuilding(hex, owner, citadel, null, null, 0, isBase, defense, seenTurn);

        private static WorthIt.DefenderProfile Body(float atk, float def, float hp, int init) =>
            new WorthIt.DefenderProfile(def, false, null, atk, hp, init, null, hp);

        private static AssetThreatSnapshot Threat(float severity) => new AssetThreatSnapshot
        {
            Contact = new EnemyContactSnapshot
            {
                Army = new ArmySnapshot { ArmyId = 42, EffectiveArmyPower = 8f },
                Position = RedBase,
                Confidence = 1f,
            },
            Asset = new StrategicAssetSnapshot
            {
                Kind = AssetKind.Base,
                Hex = OurBase,
                Value = 10f,
            },
            Severity = severity,
            Confidence = 1f,
        };

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
