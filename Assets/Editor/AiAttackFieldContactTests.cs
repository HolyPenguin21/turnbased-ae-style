#if UNITY_INCLUDE_TESTS
using System;
using System.Collections.Generic;
using System.Linq;
using Game.Ai;
using Game.Ai.V2;
using Game.Combat;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Units;
using NUnit.Framework;

namespace Game.EditorTests
{
    // ===========================================================================================
    //  2026-10-08 — ATTACK ARMY: ONE-DAY LOCAL ACTION, PATH CONTACT, RETREAT, NO IDENTICAL RESTART.
    //
    //  The estimator is a Monte-Carlo run, so every case here uses rosters that are decisively
    //  stronger or weaker than their opposition; the numeric 0.39 / 0.40 / 0.41 boundary is
    //  exercised on the pure policy (GroundCombatAdmissionPolicy), never on a random outcome.
    //  The snapshots carry no map: routes are straight lines of one cost point per hex, as in the
    //  other synthetic fixtures; terrain/profile behaviour needs a real HexMap (AiRouteCacheIsolation).
    // ===========================================================================================
    public sealed class AiAttackFieldContactTests
    {
        private static readonly PlayerSetupData Us = new PlayerSetupData { Nickname = "Us", ColorIndex = 1 };
        private static readonly PlayerSetupData Red = new PlayerSetupData { Nickname = "Red", ColorIndex = 2 };
        private static readonly HexCoord Origin = new HexCoord(0, 0);
        private static readonly HexCoord Home = new HexCoord(-1, 0);
        private static readonly HexCoord MainHex = new HexCoord(12, 0);
        private static readonly AttackTargetRef Main = AttackTargetRef.For(MainHex, Red, AttackTargetKind.Citadel);
        private HashSet<HexCoord> _visible;
        private Func<PlayerSetupData, HexCoord, bool> _savedVisibility;

        [SetUp]
        public void SetUp()
        {
            ArmyRegistry.Clear();
            MissionIntentRegistry.Clear();
            _visible = new HashSet<HexCoord>();
            _savedVisibility = AttackTacticalOpportunity.HexVisibleNow;
            AttackTacticalOpportunity.HexVisibleNow = (p, h) => _visible.Contains(h);
        }

        [TearDown]
        public void TearDown()
        {
            AttackTacticalOpportunity.HexVisibleNow = _savedVisibility;
            ArmyRegistry.Clear();
            MissionIntentRegistry.Clear();
            AiAllocatorStateRegistry.Clear();
        }

        // ---- the one-day voluntary fight -------------------------------------------------------

        [Test]
        public void Intercept_EnemyWithHigherRawStrength_IsTakenWhenTheEstimatorLikesIt()
        {
            // enemy raw (25+12=37) > ours (2 x (14+2)=32): the removed "weaker than us" rule refused this
            ArmySnapshot us = Army(7, Origin, new[] { Body(14, 2, 60), Body(14, 2, 60) });
            var enemy = Sight(5, new HexCoord(0, 2), Red, new[] { Body(25, 12, 3) });
            AttackLocalAction a = Decide(us, enemy);
            Assert.That(a.Kind, Is.EqualTo(AttackLocalActionKind.Intercept), a.Reason);
            Assert.That(a.EnemyArmyId, Is.EqualTo(5));
            Assert.That(a.ContactCost, Is.EqualTo(2));
            Assert.That(a.WinChance, Is.GreaterThanOrEqualTo(GroundCombatAdmissionPolicy.AttackLocalWinChanceGate));
        }

        [Test]
        public void Intercept_UsesTheLastMovementPoint_AndNeverChasesPastIt()
        {
            ArmySnapshot us = Army(7, Origin, Strong());
            Assert.That(Decide(us, Sight(5, new HexCoord(0, 3), Red, Weak())).Kind,
                Is.EqualTo(AttackLocalActionKind.Intercept), "cost == CurrentMovement is allowed");
            Assert.That(Decide(us, Sight(5, new HexCoord(0, 4), Red, Weak())).Kind,
                Is.EqualTo(AttackLocalActionKind.Continue), "cost == CurrentMovement + 1 is a chase");
        }

        [Test]
        public void MainTargetReachableNow_BeatsAnyVoluntaryFight()
        {
            ArmySnapshot us = Army(7, Origin, Strong());
            var snap = Snap(us, Sight(5, new HexCoord(0, 2), Red, Weak()));
            AttackTargetRef near = AttackTargetRef.For(new HexCoord(3, 0), Red, AttackTargetKind.Citadel);
            AttackLocalAction a = AttackTacticalOpportunity.Decide(snap, us, near, -1, false, null);
            Assert.That(a.Kind, Is.EqualTo(AttackLocalActionKind.Continue));
            Assert.That(a.Reason, Is.EqualTo("main_target_reachable_now"));
        }

        [Test]
        public void ContactSeenThisTurnButNotInViewNow_IsNotChased()
        {
            ArmySnapshot us = Army(7, Origin, Strong());
            var enemy = Sight(5, new HexCoord(0, 2), Red, Weak());
            var snap = Snap(us, enemy);
            _visible.Clear(); // it was seen earlier this turn (SeenTurn == TurnNumber) and then left
            Assert.That(AttackTacticalOpportunity.Decide(snap, us, Main, -1, false, null).Kind,
                Is.EqualTo(AttackLocalActionKind.Continue));
            _visible.Add(enemy.Hex);
            Assert.That(AttackTacticalOpportunity.Decide(snap, us, Main, -1, false, null).Kind,
                Is.EqualTo(AttackLocalActionKind.Intercept));
        }

        [Test]
        public void StackOnTheContactHex_IsOneFight_NotTheWeakestBodyOfIt()
        {
            ArmySnapshot us = Army(7, Origin, new[] { Body(20, 5, 40), Body(20, 5, 40) });
            var weak = Sight(5, new HexCoord(0, 2), Red, Weak());
            var wall = Sight(6, new HexCoord(0, 2), Red, new[] { Body(60, 60, 5000), Body(60, 60, 5000), Body(60, 60, 5000) });
            Assert.That(Decide(us, weak).Kind, Is.EqualTo(AttackLocalActionKind.Intercept),
                "the weak army alone would be taken");
            Assert.That(Decide(us, weak, wall).Kind, Is.Not.EqualTo(AttackLocalActionKind.Intercept),
                "the package on the hex is what is fought");
        }

        [Test]
        public void ActiveDefenceObjective_OutranksAStrongerOrdinaryArmy()
        {
            ArmySnapshot us = Army(7, Origin, Strong());
            var ordinary = Sight(9, new HexCoord(0, 2), Red, new[] { Body(8, 8, 8), Body(8, 8, 8) });
            var urgent = Sight(4, new HexCoord(2, -2), Red, new[] { Body(6, 6, 6) });
            var snap = Snap(us, ordinary, urgent);
            var ad = new[] { new ActiveDefenceObjective { Target = new ActiveDefenceMissionTarget { EnemyArmyId = 4 } } };
            AttackLocalAction a = AttackTacticalOpportunity.Decide(snap, us, Main, -1, true, ad);
            Assert.That(a.Kind, Is.EqualTo(AttackLocalActionKind.Intercept));
            Assert.That(a.EnemyArmyId, Is.EqualTo(4));
            Assert.That(a.ServesActiveDefence, Is.True);
        }

        [Test]
        public void IntermediateBase_OutranksAnOrdinaryArmy_ButNotActiveDefence()
        {
            ArmySnapshot us = Army(7, Origin, Strong());
            var ordinary = Sight(9, new HexCoord(0, 2), Red, new[] { Body(8, 8, 8) });
            var snap = Snap(us, ordinary);
            Assert.That(AttackTacticalOpportunity.Decide(snap, us, Main, -1, true, null).Kind,
                Is.EqualTo(AttackLocalActionKind.IntermediateBase));
            var ad = new[] { new ActiveDefenceObjective { Target = new ActiveDefenceMissionTarget { EnemyArmyId = 9 } } };
            Assert.That(AttackTacticalOpportunity.Decide(snap, us, Main, -1, true, ad).Kind,
                Is.EqualTo(AttackLocalActionKind.Intercept));
        }

        [Test]
        public void EqualTargets_AreChosenByPowerThenCostThenArmyId_IndependentOfInputOrder()
        {
            ArmySnapshot us = Army(7, Origin, Strong());
            var a = Sight(8, new HexCoord(0, 2), Red, new[] { Body(8, 8, 8) });
            var b = Sight(3, new HexCoord(-2, 2), Red, new[] { Body(8, 8, 8) });
            var c = Sight(5, new HexCoord(1, 1), Red, new[] { Body(9, 9, 9) });
            int first = Decide(us, a, b, c).EnemyArmyId;
            Assert.That(first, Is.EqualTo(5), "the strongest package first");
            Assert.That(Decide(us, c, b, a).EnemyArmyId, Is.EqualTo(first));
            Assert.That(Decide(us, b, a, c).EnemyArmyId, Is.EqualTo(first));
            // equal power, cost 2 vs 2: the lower ArmyId wins both ways
            Assert.That(Decide(us, a, b).EnemyArmyId, Is.EqualTo(3));
            Assert.That(Decide(us, b, a).EnemyArmyId, Is.EqualTo(3));
        }

        [Test]
        public void OneVoluntaryFightPerTurn()
        {
            ArmySnapshot us = Army(7, Origin, Strong());
            var snap = Snap(us, Sight(5, new HexCoord(0, 2), Red, Weak()));
            Assert.That(AttackTacticalOpportunity.Decide(snap, us, Main, snap.TurnNumber, false, null).Kind,
                Is.EqualTo(AttackLocalActionKind.Continue));
        }

        // ---- the contact on the path -------------------------------------------------------------

        [Test]
        public void WinnableContactOnThePath_IsFoughtOnTheWay_EvenWithTheVoluntaryFightSpent()
        {
            ArmySnapshot us = Army(7, Origin, Strong());
            var snap = Snap(us, Sight(5, new HexCoord(2, 0), Red, Weak()));
            AttackLocalAction a = AttackTacticalOpportunity.Decide(snap, us, Main, snap.TurnNumber, false, null);
            Assert.That(a.Kind, Is.EqualTo(AttackLocalActionKind.Continue));
            Assert.That(a.FightsPathContact, Is.True);
            Assert.That(a.Hex, Is.EqualTo(new HexCoord(2, 0)));
        }

        [Test]
        public void SignificantHostileArmyOnThePath_ThatCannotBeBeaten_SendsTheAttackHome()
        {
            ArmySnapshot us = Army(7, Origin, new[] { Body(2, 2, 6) });
            var enemy = Sight(5, new HexCoord(2, 0), Red, new[] { Body(60, 60, 500), Body(60, 60, 500) });
            AttackLocalAction a = Decide(us, enemy);
            Assert.That(a.Kind, Is.EqualTo(AttackLocalActionKind.Retreat));
            Assert.That(a.Reason, Is.EqualTo("hostile_contact_below_threshold"));
            Assert.That(a.EnemyArmyId, Is.EqualTo(5));
            Assert.That(a.WinChance, Is.LessThan(GroundCombatAdmissionPolicy.AttackLocalWinChanceGate));
            // the voluntary-fight limit does not suppress the mandatory evaluation
            var snap = Snap(us, enemy);
            Assert.That(AttackTacticalOpportunity.Decide(snap, us, Main, snap.TurnNumber, false, null).Kind,
                Is.EqualTo(AttackLocalActionKind.Retreat));
        }

        [Test]
        public void NeutralOrFarContact_IsBypassedAndNeverACampaignRetreat()
        {
            ArmySnapshot us = Army(7, Origin, new[] { Body(2, 2, 6) });
            var guard = new AiMapMemory.KnownEnemySighting(new HexCoord(2, 0), null, "guard", 2, 120, 120,
                new[] { Body(60, 60, 500), Body(60, 60, 500) }, seenTurn: 6, armyId: 77);
            WorldSnapshot snap = Snap(us);
            snap.Known.NeutralSightings = new[] { guard };
            _visible.Add(guard.Hex);
            Assert.That(AttackTacticalOpportunity.Decide(snap, us, Main, -1, false, null).Kind,
                Is.EqualTo(AttackLocalActionKind.Continue));
            // an unbeatable enemy army far outside today's reach and one more turn of movement
            var far = Sight(5, new HexCoord(9, 0), Red, new[] { Body(60, 60, 500), Body(60, 60, 500) });
            Assert.That(Decide(us, far).Kind, Is.EqualTo(AttackLocalActionKind.Continue));
        }

        [Test]
        public void InsignificantHostileArmy_IsNotWorthAWithdrawal()
        {
            ArmySnapshot us = Army(7, Origin, new[] { Body(1, 1, 2) });
            var scrap = Sight(5, new HexCoord(2, 0), Red, new[] { Body(1, 0, 3) });
            Assert.That(AiPower.EffectiveArmyPowerFromProfiles(scrap.Defenders),
                Is.LessThan(AiConfigV2.activeDefenceMinEnemyPower));
            Assert.That(Decide(us, scrap).Kind, Is.Not.EqualTo(AttackLocalActionKind.Retreat));
        }

        [Test]
        public void ExecutionKeepsTheFrozenEnemy_AndTakesAMandatoryRetreat()
        {
            var frozen = new AttackLocalAction(AttackLocalActionKind.Intercept, new HexCoord(0, 2), 5, "a", 2, 0.9f, true, "x");
            var other = new AttackLocalAction(AttackLocalActionKind.Intercept, new HexCoord(1, 1), 9, "b", 2, 0.9f, true, "x");
            var retreat = new AttackLocalAction(AttackLocalActionKind.Retreat, new HexCoord(1, 1), 9, "b", 2, 0.1f, false, "x");
            Assert.That(AttackTacticalOpportunity.ForExecution(frozen, frozen, out string r1).EnemyArmyId, Is.EqualTo(5));
            Assert.That(r1, Is.Null);
            AttackTacticalOpportunity.ForExecution(frozen, other, out string r2);
            Assert.That(r2, Is.Not.Null, "never jumps to another enemy after funding");
            AttackTacticalOpportunity.ForExecution(frozen, AttackLocalAction.Continue(), out string r3);
            Assert.That(r3, Is.Not.Null, "the chosen contact is gone");
            Assert.That(AttackTacticalOpportunity.ForExecution(AttackLocalAction.Continue(), other, out string r4).Kind,
                Is.EqualTo(AttackLocalActionKind.Continue), "an unfunded voluntary target is not taken");
            Assert.That(r4, Is.Null);
            AttackTacticalOpportunity.ForExecution(AttackLocalAction.Continue(), retreat, out string r5);
            Assert.That(r5, Is.Not.Null);
        }

        // ---- pure policy: threshold and coverage are two facts -----------------------------------

        [Test]
        public void Policy_ThresholdAndCoverageAreIndependent()
        {
            Assert.That(GroundCombatAdmissionPolicy.AttackLocalWinChanceGate, Is.EqualTo(0.40f));
            Assert.That(GroundCombatAdmissionPolicy.AttackLocalArmyRequiresCoverage, Is.True);
            Assert.That(GroundCombatAdmissionPolicy.AttackIntermediateBaseWinChanceGate, Is.EqualTo(0.40f));
            Assert.That(GroundCombatAdmissionPolicy.AttackIntermediateBaseRequiresCoverage, Is.False);
            // same 0.40: an army intercept demands coverage, an optional Base does not
            Assert.That(GroundCombatAdmissionPolicy.RequiresCoverage(0.40f, true), Is.True);
            Assert.That(GroundCombatAdmissionPolicy.RequiresCoverage(0.40f, false), Is.False);
            // no explicit rule: the old gate-driven default (Raid / ActiveDefence unchanged)
            Assert.That(GroundCombatAdmissionPolicy.RequiresCoverage(0.80f, null), Is.True);
            Assert.That(GroundCombatAdmissionPolicy.RequiresCoverage(0.55f, null), Is.True);
            Assert.That(GroundCombatAdmissionPolicy.FreshStartWinChanceGate, Is.EqualTo(0.80f));
            Assert.That(GroundCombatAdmissionPolicy.ContinuationWinChanceFloor, Is.EqualTo(0.55f));
            // main Base / Citadel: no floor
            Assert.That(GroundCombatAdmissionPolicy.AttackCoverageGate, Is.Zero);
        }

        [TestCase(0.39f, false)]
        [TestCase(0.40f, true)]
        [TestCase(0.41f, true)]
        public void Policy_BoundaryOfTheLocalGate(float win, bool clears) =>
            Assert.That(win >= GroundCombatAdmissionPolicy.AttackLocalWinChanceGate, Is.EqualTo(clears));

        [Test]
        public void IntermediateBaseProposal_IsAssignedAtTheLocalGateWithoutCoverage()
        {
            var local = AttackTargetRef.For(new HexCoord(2, 0), Red, AttackTargetKind.Base);
            var proposal = new MissionProposal { Kind = MissionKind.Attack, Target = new AttackMissionTarget
                { Phase = AttackMissionPhase.Assault, Target = Main, IntermediateTarget = local } };
            Assert.That(GroundCombatAdmissionPolicy.AssaultGate(proposal, 7), Is.EqualTo(0.40f));
            Assert.That(GroundCombatAdmissionPolicy.AssaultCoverage(proposal), Is.False);
            var main = new MissionProposal { Kind = MissionKind.Attack, Target = new AttackMissionTarget
                { Phase = AttackMissionPhase.Assault, Target = Main } };
            Assert.That(GroundCombatAdmissionPolicy.AssaultGate(main, 7), Is.Zero);
            Assert.That(GroundCombatAdmissionPolicy.AssaultCoverage(main), Is.Null, "main target keeps its default");
        }

        [Test]
        public void SameRosterSameOpposition_IntermediateBasePasses_ArmyInterceptKeepsItsCoverageRule()
        {
            // an enemy no body of ours can damage: the estimate may or may not like the duel, the
            // coverage fact stays honest and gates only the rule that asks for it
            var attackers = new List<WorthIt.DefenderProfile> { Body(1, 1, 500) };
            var opposition = new[] { new WorthIt.DefendingArmy(new[] { Body(1, 900, 3) }, default) };
            bool armyRule = GroundCombatFeasibility.Clears(attackers, default, opposition, 0.40f, 0f,
                out float win, out bool cover, true);
            bool baseRule = GroundCombatFeasibility.Clears(attackers, default, opposition, 0.40f, 0f,
                out float win2, out bool cover2, false);
            Assert.That(cover, Is.False);
            Assert.That(armyRule, Is.False, "no coverage: the voluntary army fight is not allowed");
            Assert.That(baseRule, Is.EqualTo(win2 >= 0.40f), "the Base rule reads the chance alone");
            Assert.That(cover2, Is.EqualTo(cover));
        }

        // ---- the withdrawal edge and its witness -------------------------------------------------

        [Test]
        public void Retreat_MovesTheMarchingAttackToRecoveryReturn_AndRecordsTheWitness()
        {
            ArmySnapshot us = Army(7, Origin, new[] { Body(2, 2, 6) });
            var enemy = Sight(5, new HexCoord(2, 0), Red, new[] { Body(60, 60, 500), Body(60, 60, 500) });
            WorldSnapshot snap = Snap(us, enemy);
            AttackIntent attack = Marching();
            MissionIntent intent = Intent(attack);

            Assert.That(MissionContinuityLayer.ResolveAttackIntent(Us, snap, intent, attack,
                new HashSet<int>(), out _), Is.True);
            Assert.That(attack.Phase, Is.EqualTo(AttackMissionPhase.RecoveryReturn));
            Assert.That(attack.TacticalRetreat, Is.True);
            Assert.That(attack.RecoveryBaseHex, Is.EqualTo(Home));
            Assert.That(attack.Target, Is.EqualTo(Main), "the identity is untouched");
            Assert.That(MissionIntentRegistry.GetOrCreate(Us).TryGetRetreatWitness(Main, out AttackRetreatWitness w), Is.True);
            Assert.That(w.EnemyArmyId, Is.EqualTo(5));

            // idempotent: a second pass neither re-triggers nor loses the leg
            Assert.That(MissionContinuityLayer.ResolveAttackIntent(Us, snap, intent, attack,
                new HashSet<int>(), out _), Is.True);
            Assert.That(attack.Phase, Is.EqualTo(AttackMissionPhase.RecoveryReturn));
        }

        [Test]
        public void Retreat_WithNoOwnBase_EndsTheOperation_AndWinnableContactDoesNotRetreat()
        {
            ArmySnapshot us = Army(7, Origin, new[] { Body(2, 2, 6) });
            var enemy = Sight(5, new HexCoord(2, 0), Red, new[] { Body(60, 60, 500), Body(60, 60, 500) });
            WorldSnapshot homeless = Snap(us, enemy);
            homeless.Self.BaseHexes = new List<HexCoord>();
            AttackIntent attack = Marching();
            Assert.That(MissionContinuityLayer.ResolveAttackIntent(Us, homeless, Intent(attack), attack,
                new HashSet<int>(), out _), Is.False, "controlled end: the claim is released with the intent");

            ArmySnapshot strong = Army(7, Origin, Strong());
            AttackIntent ok = Marching();
            Assert.That(MissionContinuityLayer.ResolveAttackIntent(Us, Snap(strong, Sight(5, new HexCoord(2, 0), Red, Weak())),
                Intent(ok), ok, new HashSet<int>(), out _), Is.True);
            Assert.That(ok.Phase, Is.EqualTo(AttackMissionPhase.Assault));
            Assert.That(ok.TacticalRetreat, Is.False);
        }

        [Test]
        public void Witness_BlocksAnIdenticalRestart_UntilTheFightRealChanges()
        {
            var state = MissionIntentRegistry.GetOrCreate(Us);
            ArmySnapshot weak = Army(7, Origin, new[] { Body(2, 2, 6) });
            var enemyRoster = new[] { Body(60, 60, 500), Body(60, 60, 500) };
            var enemy = Sight(5, new HexCoord(2, 0), Red, enemyRoster);
            WorldSnapshot snap = Snap(weak, enemy);
            state.PutRetreatWitness(new AttackRetreatWitness
            {
                Target = Main, OwnArmyId = 7, EnemyArmyId = 5, EnemyHex = enemy.Hex,
                EnemyFingerprint = AttackTacticalOpportunity.CombatFingerprint(
                    WorthIt.UnitsOf(AttackTacticalOpportunity.OppositionOn(snap, enemy.Hex))),
                OwnFingerprint = AttackRetreatWitness.OwnFingerprintOf(weak),
                Turn = 6,
            });

            // a later turn, more AP, an elapsed timer: nothing about the FIGHT changed
            snap.TurnNumber = 9;
            Assert.That(AttackRetreatWitness.Blocks(state, snap, Main, weak, out string why), Is.True);
            Assert.That(why, Is.EqualTo("unchanged_obstacle"));

            // our force really grew and now beats the unchanged enemy: lifted
            ArmySnapshot reinforced = Army(7, Origin, new[] { Body(300, 300, 5000), Body(300, 300, 5000), Body(300, 300, 5000) });
            Assert.That(AttackRetreatWitness.Blocks(state, snap, Main, reinforced, out _), Is.False);
            Assert.That(state.TryGetRetreatWitness(Main, out _), Is.False, "cleared once the odds are confirmed");
        }

        [Test]
        public void Witness_IsLiftedWhenTheEnemyIsGone_AndKeptWhileTheEstimatorStillSaysNo()
        {
            var state = MissionIntentRegistry.GetOrCreate(Us);
            ArmySnapshot weak = Army(7, Origin, new[] { Body(2, 2, 6) });
            var enemy = Sight(5, new HexCoord(2, 0), Red, new[] { Body(60, 60, 500), Body(60, 60, 500) });
            WorldSnapshot snap = Snap(weak, enemy);
            state.PutRetreatWitness(new AttackRetreatWitness { Target = Main, OwnArmyId = 7, EnemyArmyId = 5,
                EnemyHex = enemy.Hex, EnemyFingerprint = -1, OwnFingerprint = -1, Turn = 6 });
            // fingerprints differ -> the estimator is asked again and still says no: stays blocked
            Assert.That(AttackRetreatWitness.Blocks(state, snap, Main, weak, out string why), Is.True);
            StringAssert.StartsWith("still_unwinnable", why);
            // the enemy is no longer known at all
            snap.Known.EnemySightings = Array.Empty<AiMapMemory.KnownEnemySighting>();
            Assert.That(AttackRetreatWitness.Blocks(state, snap, Main, weak, out _), Is.False);
            Assert.That(state.TryGetRetreatWitness(Main, out _), Is.False);
        }

        // ---- ActiveDefence served by the marching army: one owner, no lost answer ---------------

        private static MissionProposal AdProposal(int enemy, ActiveDefencePhase phase, int actor, bool durable = false) =>
            new MissionProposal
            {
                Kind = MissionKind.ActiveDefence, FromDurableIntent = durable, PreferredMoverArmyId = actor,
                Target = new ActiveDefenceMissionTarget { Phase = phase, EnemyArmyId = enemy, PrimaryArmyId = actor },
            };

        private static MissionProposal AttackProposal() => new MissionProposal
        {
            Kind = MissionKind.Attack, PreferredMoverArmyId = 7, FromDurableIntent = true,
            DurableFundingTier = CommitmentTier.Hard,
            Target = new AttackMissionTarget { Phase = AttackMissionPhase.Assault, Target = Main, PrimaryArmyId = 7 },
        };

        private static AttackLocalAction ServingAd(int enemy) => new AttackLocalAction(
            AttackLocalActionKind.Intercept, new HexCoord(0, 2), enemy, "e", 2, 0.9f, true, "serves_active_defence",
            servesActiveDefence: true);

        [Test]
        public void ServingActiveDefence_WithdrawsOnlyTheIndependentGroundIntercept_OfThatEnemy()
        {
            WorldSnapshot snap = Snap(Army(7, Origin, Strong()));
            MissionIntent hard = Intent(Marching());
            var independent = AdProposal(4, ActiveDefencePhase.Intercept, 11);
            var air = AdProposal(4, ActiveDefencePhase.AirSupport, 12);
            var otherEnemy = AdProposal(9, ActiveDefencePhase.Intercept, 13);
            var durable = AdProposal(4, ActiveDefencePhase.Intercept, 14, durable: true);
            var proposals = new List<MissionProposal> { independent, air, otherEnemy, durable };
            Assert.That(AggressionMissionLayer.TryServeActiveDefence(snap, proposals, AttackProposal(), hard, ServingAd(4)), Is.True);
            Assert.That(proposals, Is.EquivalentTo(new[] { air, otherEnemy, durable }),
                "air support, another enemy's answer and an incumbent AD operation all stay");
        }

        [Test]
        public void ServingActiveDefence_LeavesTheIndependentAnswer_WhenTheAttackStepCannotBeRelied()
        {
            WorldSnapshot snap = Snap(Army(7, Origin, Strong()));
            var independent = AdProposal(4, ActiveDefencePhase.Intercept, 11);
            // not a protected Hard operation
            MissionIntent soft = Intent(Marching()); soft.Funding = CommitmentTier.Soft;
            var list = new List<MissionProposal> { independent };
            Assert.That(AggressionMissionLayer.TryServeActiveDefence(snap, list, AttackProposal(), soft, ServingAd(4)), Is.False);
            // a Hard step whose attempt was just rejected and is cooling down: the threat keeps its handler
            MissionProposal attack = AttackProposal();
            AiAllocatorStateRegistry.GetOrCreate(Us).StartCooldown(StableMissionKey.For(attack), 6, 8, "rejected");
            Assert.That(AggressionMissionLayer.TryServeActiveDefence(snap, list, attack, Intent(Marching()), ServingAd(4)), Is.False);
            // an ordinary (non-AD) intercept never displaces anything
            var ordinary = new AttackLocalAction(AttackLocalActionKind.Intercept, new HexCoord(0, 2), 4, "e", 2, 0.9f, true, "x");
            AiAllocatorStateRegistry.Clear();
            Assert.That(AggressionMissionLayer.TryServeActiveDefence(snap, list, AttackProposal(), Intent(Marching()), ordinary), Is.False);
            Assert.That(list, Is.EquivalentTo(new[] { independent }));
        }

        [Test]
        public void CompletedLocalFight_KeepsTheOperationTargetAndTheArmyClaim()
        {
            ArmySnapshot us = Army(7, Origin, Strong());
            WorldSnapshot snap = Snap(us);
            AttackIntent attack = Marching();
            attack.LastOpportunisticStrikeTurn = snap.TurnNumber; // the fight was spent this turn
            MissionIntent intent = Intent(attack);
            Assert.That(MissionContinuityLayer.ResolveAttackIntent(Us, snap, intent, attack, new HashSet<int>(), out _), Is.True);
            Assert.That(attack.Target, Is.EqualTo(Main));
            Assert.That(attack.PrimaryArmyId, Is.EqualTo(7));
            Assert.That(attack.Phase, Is.EqualTo(AttackMissionPhase.Assault));
            Assert.That(intent.Status, Is.EqualTo(IntentStatus.Active));
            // the claim is derived from this very Active intent (ActorCommitments), so it survives with it
        }

        // ---- fixtures -----------------------------------------------------------------------------

        private AttackLocalAction Decide(ArmySnapshot us, params AiMapMemory.KnownEnemySighting[] enemies) =>
            AttackTacticalOpportunity.Decide(Snap(us, enemies), us, Main, -1, false, null);

        private WorldSnapshot Snap(ArmySnapshot us, params AiMapMemory.KnownEnemySighting[] enemies)
        {
            foreach (var e in enemies) _visible.Add(e.Hex);
            return new WorldSnapshot
            {
                TurnNumber = 6, Observer = Us,
                Self = new SelfSnapshot
                {
                    Armies = new List<ArmySnapshot> { us }, BaseHexes = new List<HexCoord> { Home },
                    Citadel = Home, DeployedPower = 75f, AvailablePower = 100f,
                },
                Known = new KnownSnapshot
                {
                    EnemySightings = enemies,
                    NeutralSightings = Array.Empty<AiMapMemory.KnownEnemySighting>(),
                    Buildings = new[] { new AiMapMemory.KnownBuilding(MainHex, Red, true, null, seenTurn: 6) },
                    EventGuards = Array.Empty<KnownEventGuardSnapshot>(),
                },
            };
        }

        private static AttackIntent Marching() => new AttackIntent
        {
            Target = Main, Phase = AttackMissionPhase.Assault, PrimaryArmyId = 7,
            OperationStarted = true, AssaultStarted = true,
        };

        private static MissionIntent Intent(AttackIntent attack) => new MissionIntent
        {
            IntentKey = MissionIntentKey.ForAttack(Main), Kind = MissionKind.Attack,
            Status = IntentStatus.Active, Funding = CommitmentTier.Hard, Objective = attack,
        };

        private static ArmySnapshot Army(int id, HexCoord hex, IReadOnlyList<WorthIt.DefenderProfile> roster) =>
            new ArmySnapshot
            {
                ArmyId = id, Owner = Us, Hex = hex, IsStructuralRaidActor = true, MemberCount = roster.Count,
                MaxMovement = 3, CurrentMovement = 3, Members = roster,
                EffectiveArmyPower = AiPower.EffectiveArmyPowerFromProfiles(roster),
                ReachableOwnBaseHexes = new[] { Home },
            };

        private static AiMapMemory.KnownEnemySighting Sight(int id, HexCoord hex, PlayerSetupData owner,
            IReadOnlyList<WorthIt.DefenderProfile> roster) =>
            new AiMapMemory.KnownEnemySighting(hex, owner, "army" + id, roster.Count, roster.Sum(r => r.Defense),
                roster.Sum(r => r.Attack), roster, seenTurn: 6, armyId: id);

        private static List<WorthIt.DefenderProfile> Strong() => new List<WorthIt.DefenderProfile>
            { Body(40, 8, 120), Body(40, 8, 120), Body(40, 8, 120) };

        private static WorthIt.DefenderProfile[] Weak() => new[] { Body(12, 2, 3), Body(12, 2, 3) };

        private static WorthIt.DefenderProfile Body(float atk, float def, float hp) =>
            new WorthIt.DefenderProfile(def, false, null, atk, hp, 4, null, hp);
    }
}
#endif
