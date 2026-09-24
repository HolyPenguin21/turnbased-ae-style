#if UNITY_INCLUDE_TESTS
using System.Collections.Generic;
using System.Linq;
using Game.Ai.V2;
using Game.Cards;
using Game.Combat;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using NUnit.Framework;

namespace Game.EditorTests
{
    // AI V2 Raid target unification: RaidTargetRef (neutral army vs event guard), the ArmyId==0
    // sentinel fix, and the SupportReturn phase. Kept separate from AiEconomyDecisionTests.cs per
    // the task's own instruction (own test file, not folded into Economy).
    //
    // These are pure-data / static-API tests: they build WorldSnapshot/ArmySnapshot/RaidIntent
    // objects directly and call the V2 evaluators, same pattern as AiEconomyDecisionTests. Anything
    // that needs a live scene (SpawnEventGuard, the Tactical Battle Module, ArmyActions.SwapMembers
    // actually moving units on a real ArmyData, AiTurnController.MoveArmyRoutine) is out of reach
    // for an EditMode unit test and is left for manual play-test, per this project's usual handoff
    // for AI V2 work.
    public class AiAggressionRaidTests
    {
        [TearDown]
        public void ClearHexEvents() => HexEventRegistry.Clear();

        // ---- RaidTargetRef / key distinctness ------------------------------------------------

        [Test]
        public void RaidTargetRef_NeutralArmyZero_And_EventGuardAtOrigin_AreDistinct()
        {
            RaidTargetRef army0 = RaidTargetRef.ForNeutralArmy(0);
            RaidTargetRef guardAtOrigin = RaidTargetRef.ForEventGuard(new HexCoord(0, 0));

            Assert.That(army0.HasValue, Is.True);
            Assert.That(guardAtOrigin.HasValue, Is.True);
            Assert.That(army0, Is.Not.EqualTo(guardAtOrigin));
            Assert.That(army0.GetHashCode(), Is.Not.EqualTo(guardAtOrigin.GetHashCode()));
        }

        [Test]
        public void RaidTargetRef_None_HasNoValue_WithoutAnyNumericSentinel()
        {
            RaidTargetRef none = RaidTargetRef.None;
            Assert.That(none.HasValue, Is.False);
            Assert.That(default(RaidTargetRef).HasValue, Is.False);
        }

        [Test]
        public void MissionIntentKey_NeutralArmyZero_And_EventGuardSameHex_AreDistinctKeys()
        {
            MissionIntentKey armyKey = MissionIntentKey.ForRaid(RaidTargetRef.ForNeutralArmy(0));
            MissionIntentKey guardKey = MissionIntentKey.ForRaid(RaidTargetRef.ForEventGuard(new HexCoord(0, 0)));

            Assert.That(armyKey, Is.Not.EqualTo(guardKey));
        }

        [Test]
        public void StableMissionKey_ForRaidAssault_NeutralArmyZero_And_EventGuard_AreDistinctKeys()
        {
            StableMissionKey armyKey = StableMissionKey.ForRaidAssault(RaidTargetRef.ForNeutralArmy(0));
            StableMissionKey guardKey = StableMissionKey.ForRaidAssault(RaidTargetRef.ForEventGuard(new HexCoord(0, 0)));

            Assert.That(armyKey, Is.Not.EqualTo(guardKey));
        }

        // ---- ArmyId == 0 is a real, claimable, resolvable id ----------------------------------

        [Test]
        public void ActorCommitments_ClaimsArmyIdZero()
        {
            var commitments = new ActorCommitments();
            Assert.That(commitments.IsArmyClaimed(0), Is.False);

            commitments.Claim(0);

            Assert.That(commitments.IsArmyClaimed(0), Is.True);
        }

        [Test]
        public void KnownDefenders_ResolvesLiveNeutralArmy_WithIdZero()
        {
            var defenders = new List<WorthIt.DefenderProfile> { Weak() };
            WorldSnapshot snap = SnapshotWithNeutralSighting(armyId: 0, hex: new HexCoord(3, 0), defenders: defenders);

            IReadOnlyList<WorthIt.DefenderProfile> found = AiV2Util.KnownDefenders(snap, RaidTargetRef.ForNeutralArmy(0));

            Assert.That(found.Count, Is.EqualTo(1));
        }

        [Test]
        public void KnownDefenders_ResolvesEventGuard_ByHex_NotByArmyId()
        {
            var hex = new HexCoord(5, 5);
            WorldSnapshot snap = SnapshotWithEventGuard(hex, Weak());

            IReadOnlyList<WorthIt.DefenderProfile> found = AiV2Util.KnownDefenders(snap, RaidTargetRef.ForEventGuard(hex));

            Assert.That(found.Count, Is.EqualTo(1));
        }

        // ---- CombatOpportunityAnalyzer: event guards join the same estimator -------------------

        [Test]
        public void CombatOpportunityAnalyzer_LiveNeutralArmyIdZero_BecomesOpportunity()
        {
            WorldSnapshot snap = SnapshotWithNeutralSighting(armyId: 0, hex: new HexCoord(3, 0),
                defenders: new List<WorthIt.DefenderProfile> { Weak() }, withOwnArmy: true);

            CombatOpportunityReport report = CombatOpportunityAnalyzer.Analyze(snap);

            CombatOpportunity opp = report.NeutralOpportunities.Single();
            Assert.That(opp.Target.Kind, Is.EqualTo(RaidTargetKind.NeutralArmy));
            Assert.That(opp.Target.ArmyId, Is.EqualTo(0));
            Assert.That(opp.TargetIsNeutral, Is.True);
        }

        [Test]
        public void CombatOpportunityAnalyzer_KnownEventGuard_AppearsInNeutralOpportunities()
        {
            var hex = new HexCoord(4, 4);
            WorldSnapshot snap = SnapshotWithEventGuard(hex, Weak(), withOwnArmy: true);

            CombatOpportunityReport report = CombatOpportunityAnalyzer.Analyze(snap);

            CombatOpportunity opp = report.NeutralOpportunities.Single();
            Assert.That(opp.Target.Kind, Is.EqualTo(RaidTargetKind.EventGuard));
            Assert.That(opp.Target.Hex, Is.EqualTo(hex));
            Assert.That(opp.TargetIsNeutral, Is.True);
            // No ArmyId is ever fabricated for a guard target — the projection is always 0/unused.
            Assert.That(opp.TargetArmyId, Is.EqualTo(0));
        }

        [Test]
        public void CombatOpportunityAnalyzer_NeutralArmy_And_EventGuard_OnSameHex_AreTwoDistinctOpportunities()
        {
            var hex = new HexCoord(2, 2);
            WorldSnapshot snap = SnapshotWithNeutralSighting(armyId: 0, hex: hex,
                defenders: new List<WorthIt.DefenderProfile> { Weak() }, withOwnArmy: true);
            snap.Known.EventGuards = new List<KnownEventGuardSnapshot>
            {
                new KnownEventGuardSnapshot(hex, new Game.Ai.AiMapMemory.GuardStrength(
                    Weak().Defense, Weak().Attack, new List<WorthIt.DefenderProfile> { Weak() }, "Guard"), "Guard", 1),
            };

            CombatOpportunityReport report = CombatOpportunityAnalyzer.Analyze(snap);

            Assert.That(report.NeutralOpportunities.Count, Is.EqualTo(2));
            Assert.That(report.NeutralOpportunities.Select(o => o.Target).Distinct().Count(), Is.EqualTo(2));
        }

        [Test]
        public void CombatOpportunityAnalyzer_FiveWeakDefenders_CoverageGatePasses()
        {
            var weakDefenders = Enumerable.Range(0, 5).Select(_ => Weak()).ToList();
            WorldSnapshot snap = SnapshotWithNeutralSighting(armyId: 7, hex: new HexCoord(3, 0),
                defenders: weakDefenders, withOwnArmy: true);

            CombatOpportunityReport report = CombatOpportunityAnalyzer.Analyze(snap);

            CombatOpportunity opp = report.NeutralOpportunities.Single();
            Assert.That(opp.DefenderCount, Is.EqualTo(5));
            Assert.That(opp.CanCoverAllDefenders, Is.True);
        }

        [Test]
        public void CombatOpportunityAnalyzer_OneUnbeatableDefender_CoverageGateFails_RegardlessOfCount()
        {
            WorldSnapshot snap = SnapshotWithNeutralSighting(armyId: 8, hex: new HexCoord(3, 0),
                defenders: new List<WorthIt.DefenderProfile> { Unbeatable() }, withOwnArmy: true);

            CombatOpportunityReport report = CombatOpportunityAnalyzer.Analyze(snap);

            CombatOpportunity opp = report.NeutralOpportunities.Single();
            Assert.That(opp.DefenderCount, Is.EqualTo(1));
            Assert.That(opp.CanCoverAllDefenders, Is.False);
            Assert.That(opp.GatePassed, Is.False);
        }

        // ---- AggressionObjectiveEvaluator: no defender-count cap, base-value floor kept --------

        [Test]
        public void Enumerate_FiveDefenders_NoLongerRejectedByCount()
        {
            var weakDefenders = Enumerable.Range(0, 5).Select(_ => Weak()).ToList();
            WorldSnapshot snap = SnapshotWithNeutralSighting(armyId: 9, hex: new HexCoord(3, 0),
                defenders: weakDefenders, withOwnArmy: true, closeToBase: true);
            CombatOpportunityReport report = CombatOpportunityAnalyzer.Analyze(snap);

            List<AggressionObjective> objectives = AggressionObjectiveEvaluator.Enumerate(snap, report);

            Assert.That(objectives.Any(o => o.Target.Equals(RaidTargetRef.ForNeutralArmy(9))), Is.True);
        }

        [Test]
        public void Enumerate_OrdinaryNeutralArmy_ProducesValidObjective_UnchangedFlow()
        {
            WorldSnapshot snap = SnapshotWithNeutralSighting(armyId: 42, hex: new HexCoord(3, 0),
                defenders: new List<WorthIt.DefenderProfile> { Weak() }, withOwnArmy: true, closeToBase: true);
            CombatOpportunityReport report = CombatOpportunityAnalyzer.Analyze(snap);

            List<AggressionObjective> objectives = AggressionObjectiveEvaluator.Enumerate(snap, report);

            AggressionObjective obj = objectives.Single();
            Assert.That(obj.Target.Kind, Is.EqualTo(RaidTargetKind.NeutralArmy));
            Assert.That(obj.Target.ArmyId, Is.EqualTo(42));
            Assert.That(obj.TargetIsNeutral, Is.True);
        }

        [Test]
        public void Enumerate_KnownEventGuard_FiveWeakDefenders_Accepted()
        {
            var hex = new HexCoord(2, 0);
            var weakDefenders = Enumerable.Range(0, 5).Select(_ => Weak()).ToList();
            WorldSnapshot snap = SnapshotWithEventGuard(hex, null, withOwnArmy: true, closeToBase: true);
            snap.Known.EventGuards = new List<KnownEventGuardSnapshot>
            {
                new KnownEventGuardSnapshot(hex,
                    new Game.Ai.AiMapMemory.GuardStrength(1f, 1f, weakDefenders, "Guard"), "Guard", 5),
            };
            CombatOpportunityReport report = CombatOpportunityAnalyzer.Analyze(snap);

            List<AggressionObjective> objectives = AggressionObjectiveEvaluator.Enumerate(snap, report);

            Assert.That(objectives.Any(o => o.Target.Kind == RaidTargetKind.EventGuard
                && o.Target.Hex.Equals(hex)), Is.True);
        }

        [Test]
        public void Enumerate_MinBaseValueGate_StillAppliedToEveryAcceptedObjective()
        {
            // raidObjectiveMinBaseValue is consulted on every candidate. There is no second
            // Raid-local value scale or floor: accepted objective value is canonical TaskScore.
            WorldSnapshot snap = SnapshotWithNeutralSighting(armyId: 55, hex: new HexCoord(500, 500),
                defenders: new List<WorthIt.DefenderProfile> { Weak() }, withOwnArmy: true, closeToBase: false);
            CombatOpportunityReport report = CombatOpportunityAnalyzer.Analyze(snap);

            List<AggressionObjective> objectives = AggressionObjectiveEvaluator.Enumerate(snap, report);

            AggressionObjective obj = objectives.Single(o => o.Target.Equals(RaidTargetRef.ForNeutralArmy(55)));
            Assert.That(obj.BaseValue, Is.GreaterThanOrEqualTo(AiConfigV2.raidObjectiveMinBaseValue));
        }

        [Test]
        public void RaidValue_StationaryNeutralAndEventNeverLoseIntelValue()
        {
            WorldSnapshot neutral = SnapshotWithNeutralSighting(armyId: 77,
                hex: new HexCoord(3, 0),
                defenders: new List<WorthIt.DefenderProfile> { Weak() }, withOwnArmy: true);
            neutral.TurnNumber = 8; // the sighting was recorded on turn zero
            CombatOpportunityReport neutralReport = CombatOpportunityAnalyzer.Analyze(neutral);
            Assert.That(neutralReport.NeutralOpportunities.Single().Confidence,
                Is.EqualTo(AiConfigV2.threatConfidenceLastKnown));
            AggressionObjective neutralRaid = AggressionObjectiveEvaluator.Enumerate(
                neutral, neutralReport).Single();
            Assert.That(neutralRaid.TaskScore.Staleness, Is.Zero);
            Assert.That(neutralRaid.BaseValue, Is.EqualTo(AiConfigV2.RaidReward
                + TaskScoreEvaluator.OwnTerritoryProximity(
                    TaskScoreEvaluator.NearestOwnedHomeDistance(neutral, neutralRaid.LastKnownHex))));

            WorldSnapshot eventSnap = SnapshotWithEventGuard(
                new HexCoord(4, 0), Weak(), withOwnArmy: true);
            eventSnap.TurnNumber = 8;
            CombatOpportunityReport eventReport = CombatOpportunityAnalyzer.Analyze(eventSnap);
            AggressionObjective eventRaid = AggressionObjectiveEvaluator.Enumerate(
                eventSnap, eventReport).Single();
            Assert.That(eventRaid.Target.Kind, Is.EqualTo(RaidTargetKind.EventGuard));
            Assert.That(eventRaid.TaskScore.Staleness, Is.Zero);
            Assert.That(eventRaid.BaseValue, Is.EqualTo(AiConfigV2.RaidReward
                + TaskScoreEvaluator.OwnTerritoryProximity(
                    TaskScoreEvaluator.NearestOwnedHomeDistance(eventSnap, eventRaid.LastKnownHex))));
            Assert.That(TaskScoreEvaluator.StaleIntelPenalty(0.5f), Is.LessThan(0f));
        }

        [Test]
        public void RaidValue_UsesSignedNearestHomeProximity_ForNeutralAndEvent()
        {
            HexCoord nearHex = new HexCoord(3, 0);
            WorldSnapshot near = SnapshotWithNeutralSighting(armyId: 81, hex: nearHex,
                defenders: new List<WorthIt.DefenderProfile> { Weak() }, withOwnArmy: true);
            AggressionObjective nearRaid = AggressionObjectiveEvaluator.Enumerate(near,
                CombatOpportunityAnalyzer.Analyze(near)).Single();
            Assert.That(nearRaid.TaskScore.OwnTerritoryProximity, Is.EqualTo(1.5f).Within(0.0001f));
            Assert.That(nearRaid.BaseValue, Is.EqualTo(AiConfigV2.RaidReward + 1.5f).Within(0.0001f));

            WorldSnapshot far = SnapshotWithNeutralSighting(armyId: 82,
                hex: new HexCoord(12, 0),
                defenders: new List<WorthIt.DefenderProfile> { Weak() }, withOwnArmy: true);
            AggressionObjective farRaid = AggressionObjectiveEvaluator.Enumerate(far,
                CombatOpportunityAnalyzer.Analyze(far)).Single();
            Assert.That(farRaid.TaskScore.OwnTerritoryProximity, Is.EqualTo(-3f).Within(0.0001f));
            Assert.That(farRaid.BaseValue, Is.EqualTo(AiConfigV2.RaidReward - 3f).Within(0.0001f));

            WorldSnapshot eventSnap = SnapshotWithEventGuard(nearHex, Weak(), withOwnArmy: true);
            AggressionObjective eventRaid = AggressionObjectiveEvaluator.Enumerate(eventSnap,
                CombatOpportunityAnalyzer.Analyze(eventSnap)).Single();
            Assert.That(eventRaid.TaskScore.OwnTerritoryProximity,
                Is.EqualTo(nearRaid.TaskScore.OwnTerritoryProximity));
            Assert.That(eventRaid.BaseValue, Is.EqualTo(nearRaid.BaseValue));
        }

        [Test]
        public void RaidValue_IsIndependentOfMilitaryPotentialRealization()
        {
            WorldSnapshot low = SnapshotWithNeutralSighting(armyId: 83,
                hex: new HexCoord(3, 0),
                defenders: new List<WorthIt.DefenderProfile> { Weak() }, withOwnArmy: true);
            low.Self.BestStackPotential = 10f;
            low.Self.TotalMilitaryPotential = 100f;
            WorldSnapshot high = SnapshotWithNeutralSighting(armyId: 83,
                hex: new HexCoord(3, 0),
                defenders: new List<WorthIt.DefenderProfile> { Weak() }, withOwnArmy: true);
            high.Self.BestStackPotential = 90f;
            high.Self.TotalMilitaryPotential = 100f;

            AggressionObjective lowRaid = AggressionObjectiveEvaluator.Enumerate(low,
                CombatOpportunityAnalyzer.Analyze(low)).Single();
            AggressionObjective highRaid = AggressionObjectiveEvaluator.Enumerate(high,
                CombatOpportunityAnalyzer.Analyze(high)).Single();

            Assert.That(highRaid.BaseValue, Is.EqualTo(lowRaid.BaseValue));
            Assert.That(highRaid.TaskScore.Value, Is.EqualTo(lowRaid.TaskScore.Value));
        }

        // ---- RaidObjectiveEvaluator: event-guard lifecycle -------------------------------------

        [Test]
        public void IsObjectiveSatisfiedLive_EventGuard_ConsumedIsSatisfied()
        {
            var hex = new HexCoord(1, 1);
            SetActiveEvent(hex);
            HexEventRegistry.MarkConsumed(hex);
            var player = new PlayerSetupData { Nickname = "P1" };

            bool satisfied = RaidObjectiveEvaluator.IsObjectiveSatisfiedLive(player, RaidTargetRef.ForEventGuard(hex));

            Assert.That(satisfied, Is.True);
        }

        [Test]
        public void IsObjectiveSatisfiedLive_EventGuard_NotConsumedIsNotSatisfied()
        {
            var hex = new HexCoord(1, 2);
            SetActiveEvent(hex);
            var player = new PlayerSetupData { Nickname = "P1" };

            bool satisfied = RaidObjectiveEvaluator.IsObjectiveSatisfiedLive(player, RaidTargetRef.ForEventGuard(hex));

            Assert.That(satisfied, Is.False);
        }

        [Test]
        public void IsIntentStillValid_EventGuard_ActiveWhileEventActive_RegardlessOfSightings()
        {
            var hex = new HexCoord(1, 3);
            SetActiveEvent(hex);
            WorldSnapshot snap = SnapshotWithNeutralSighting(armyId: 0, hex: new HexCoord(9, 9),
                defenders: new List<WorthIt.DefenderProfile>(), withOwnArmy: false);
            var intent = new RaidIntent { Target = RaidTargetRef.ForEventGuard(hex), OperationStarted = true };

            Assert.That(RaidObjectiveEvaluator.IsIntentStillValid(snap, intent), Is.True);

            HexEventRegistry.MarkConsumed(hex);

            Assert.That(RaidObjectiveEvaluator.IsIntentStillValid(snap, intent), Is.False);
        }

        // ---- SupportReturn phase ---------------------------------------------------------------

        [Test]
        public void BeginRaidSupportReturn_WithHomeBase_EntersSupportReturnPhase()
        {
            var player = new PlayerSetupData { Nickname = "SupportReturnBegin" };
            var baseHex = new HexCoord(0, 0);
            WorldSnapshot snap = SnapshotWithOwnBase(player, baseHex);
            MissionIntent intent = PutRaidIntent(player, primaryArmyId: 1,
                target: RaidTargetRef.ForNeutralArmy(2), phase: RaidMissionPhase.Reinforcement);

            MissionContinuityLayer.BeginRaidSupportReturn(player, snap, primaryArmyId: 1, supportArmyId: 3, "test");

            Assert.That(intent.Raid.Phase, Is.EqualTo(RaidMissionPhase.SupportReturn));
            Assert.That(intent.Raid.SupportArmyId, Is.EqualTo(3));
            Assert.That(intent.Raid.SupportReturnHex, Is.EqualTo(baseHex));
        }

        [Test]
        public void BeginRaidSupportReturn_NoHomeBase_ReleasesSupportImmediately_NeverBlocksRaid()
        {
            var player = new PlayerSetupData { Nickname = "SupportReturnNoBase" };
            WorldSnapshot snap = SnapshotWithNeutralSighting(armyId: 0, hex: new HexCoord(9, 9),
                defenders: new List<WorthIt.DefenderProfile>(), withOwnArmy: false); // no bases known
            MissionIntent intent = PutRaidIntent(player, primaryArmyId: 1,
                target: RaidTargetRef.ForNeutralArmy(2), phase: RaidMissionPhase.Reinforcement);

            MissionContinuityLayer.BeginRaidSupportReturn(player, snap, primaryArmyId: 1, supportArmyId: 3, "test");

            Assert.That(intent.Raid.Phase, Is.Not.EqualTo(RaidMissionPhase.SupportReturn));
            Assert.That(intent.Raid.SupportArmyId, Is.Null);
        }

        [Test]
        public void CompleteRaidSupportReturn_ReleasesSupport_AndClearsTheLeg()
        {
            var player = new PlayerSetupData { Nickname = "SupportReturnComplete" };
            WorldSnapshot snap = SnapshotWithOwnBase(player, new HexCoord(0, 0));
            MissionIntent intent = PutRaidIntent(player, primaryArmyId: 1,
                target: RaidTargetRef.ForNeutralArmy(2), phase: RaidMissionPhase.SupportReturn);
            intent.Raid.SupportArmyId = 3;
            intent.Raid.SupportReturnHex = new HexCoord(0, 0);

            MissionContinuityLayer.CompleteRaidSupportReturn(player, snap, primaryArmyId: 1, "test");

            Assert.That(intent.Raid.SupportArmyId, Is.Null);
            Assert.That(intent.Raid.SupportReturnHex, Is.Null);
            Assert.That(intent.Raid.Phase, Is.Not.EqualTo(RaidMissionPhase.SupportReturn));
        }

        [Test]
        public void ActorCommitments_ClaimsSupport_DuringSupportReturn_NotJustReinforcement()
        {
            var player = new PlayerSetupData { Nickname = "SupportReturnClaim" };
            var primary = new ArmySnapshot { ArmyId = 1, Owner = player, MemberCount = 1, IsPrison = false, IsAir = false };
            var support = new ArmySnapshot { ArmyId = 3, Owner = player, MemberCount = 1, IsPrison = false, IsAir = false };
            WorldSnapshot snap = new WorldSnapshot
            {
                Self = new SelfSnapshot { Armies = new List<ArmySnapshot> { primary, support } },
            };
            var intent = new MissionIntent
            {
                Kind = MissionKind.Raid,
                Objective = new RaidIntent
                {
                    Target = RaidTargetRef.ForNeutralArmy(2),
                    Phase = RaidMissionPhase.SupportReturn,
                    PrimaryArmyId = 1,
                    SupportArmyId = 3,
                },
            };

            ActorCommitments commitments = ActorCommitments.FromIntents(
                new List<MissionIntent> { intent }, snap, null);

            Assert.That(commitments.IsArmyClaimed(3), Is.True);
        }

        // ---- test helpers -----------------------------------------------------------------------

        private static WorthIt.DefenderProfile Weak() =>
            new WorthIt.DefenderProfile(defense: 1f, hasCeramicArmor: false, attack: 1f, hitPoints: 5f, maxHitPoints: 5f);

        // Defense high enough that no plausible attacker (Attack far below Defense) can ever clear
        // WorthIt.CanDamage's `attack*0.5 - defense*0.5 > 0` threshold.
        private static WorthIt.DefenderProfile Unbeatable() =>
            new WorthIt.DefenderProfile(defense: 500f, hasCeramicArmor: false, attack: 50f, hitPoints: 50f, maxHitPoints: 50f);

        private static WorthIt.DefenderProfile Strong() =>
            new WorthIt.DefenderProfile(defense: 1f, hasCeramicArmor: false, attack: 20f, hitPoints: 20f, maxHitPoints: 20f);

        private static void SetActiveEvent(HexCoord hex) =>
            HexEventRegistry.Set(hex, new Game.Cards.EventDefinition { rewards = new List<RewardEntry>() },
                guardArmyName: null, resolvedGuardMembers: null, guardOwner: null, resolvedCardRewards: null);

        private static WorldSnapshot SnapshotWithNeutralSighting(int armyId, HexCoord hex,
            List<WorthIt.DefenderProfile> defenders, bool withOwnArmy = false, bool closeToBase = true)
        {
            var neutralOwner = new PlayerSetupData { IsNeutral = true, Nickname = "Neutral" };
            var snap = BaseSnapshot(withOwnArmy, closeToBase);
            snap.Known.NeutralSightings = new List<Game.Ai.AiMapMemory.KnownEnemySighting>
            {
                new Game.Ai.AiMapMemory.KnownEnemySighting(hex, neutralOwner, "Target", defenders.Count,
                    defenders.Sum(d => d.Defense), defenders.Sum(d => d.Attack), defenders, armyId: armyId),
            };
            return snap;
        }

        private static WorldSnapshot SnapshotWithEventGuard(HexCoord hex, WorthIt.DefenderProfile? defender,
            bool withOwnArmy = false, bool closeToBase = true)
        {
            var snap = BaseSnapshot(withOwnArmy, closeToBase);
            var defenders = defender.HasValue
                ? new List<WorthIt.DefenderProfile> { defender.Value }
                : new List<WorthIt.DefenderProfile>();
            snap.Known.EventGuards = new List<KnownEventGuardSnapshot>
            {
                new KnownEventGuardSnapshot(hex,
                    new Game.Ai.AiMapMemory.GuardStrength(
                        defenders.Sum(d => d.Defense), defenders.Sum(d => d.Attack), defenders, "Guard"),
                    "Guard", defenders.Count),
            };
            return snap;
        }

        private static WorldSnapshot SnapshotWithOwnBase(PlayerSetupData player, HexCoord hex)
        {
            var snap = BaseSnapshot(withOwnArmy: false, closeToBase: true);
            snap.Known.Buildings = new List<Game.Ai.AiMapMemory.KnownBuilding>
            {
                new Game.Ai.AiMapMemory.KnownBuilding(hex, player, isStartingCitadel: true, isBase: true,
                    facilityAbilities: null, collectedAmounts: null, freeFacilitySlots: 0),
            };
            return snap;
        }

        // Own strong ready army parked at the base hex so CombatOpportunityAnalyzer has a real
        // roster to project a WinChance/coverage against, and Enumerate's proximity term has a base
        // to measure distance from.
        private static WorldSnapshot BaseSnapshot(bool withOwnArmy, bool closeToBase)
        {
            var baseHex = new HexCoord(0, 0);
            var armies = new List<ArmySnapshot>();
            if (withOwnArmy)
            {
                armies.Add(new ArmySnapshot
                {
                    ArmyId = 100,
                    Hex = closeToBase ? baseHex : new HexCoord(-500, -500),
                    IsStructuralRaidActor = true,
                    MaxMovement = 4,
                    MemberCount = 2,
                    Members = new List<WorthIt.DefenderProfile> { Strong(), Strong() },
                });
            }
            return new WorldSnapshot
            {
                TurnNumber = 1,
                Self = new SelfSnapshot
                {
                    BaseHexes = new List<HexCoord> { baseHex },
                    Armies = armies,
                    Hand = new List<CardData>(),
                    Deck = new List<CardDefinition>(),
                    FieldPower = 100f,
                },
                Known = new KnownSnapshot
                {
                    EnemySightings = new List<Game.Ai.AiMapMemory.KnownEnemySighting>(),
                    NeutralSightings = new List<Game.Ai.AiMapMemory.KnownEnemySighting>(),
                    Buildings = new List<Game.Ai.AiMapMemory.KnownBuilding>(),
                    ResourceHexes = new List<Game.Ai.AiMapMemory.KnownResourceHex>(),
                    EventGuards = new List<KnownEventGuardSnapshot>(),
                },
                Threat = new ThreatModel
                {
                    Contacts = new List<EnemyContactSnapshot>(),
                    Threats = new List<AssetThreatSnapshot>(),
                },
            };
        }

        private static MissionIntent PutRaidIntent(PlayerSetupData player, int primaryArmyId,
            RaidTargetRef target, RaidMissionPhase phase)
        {
            var ri = new RaidIntent
            {
                Target = target,
                TargetIsNeutral = true,
                OperationStarted = true,
                Phase = phase,
                PrimaryArmyId = primaryArmyId,
            };
            var intent = new MissionIntent
            {
                Kind = MissionKind.Raid,
                Objective = ri,
                Funding = CommitmentTier.Hard,
                Status = IntentStatus.Active,
            };
            intent.IntentKey = MissionIntentKey.For(intent);
            MissionIntentState state = MissionIntentRegistry.GetOrCreate(player);
            state.Put(intent);
            return intent;
        }
    }
}
#endif
