#if UNITY_INCLUDE_TESTS
using System.Collections.Generic;
using System.Linq;
using Game.Ai;
using Game.Ai.V2;
using Game.Combat;
using Game.Core;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Units;
using NUnit.Framework;

namespace Game.EditorTests
{
    // Playtest 2026-10-21 (Vex vs Mordak, 22 turns): one regression per root cause.
    public class AiPlaytest20261021Tests
    {
        private List<PlayerSetupData> _previousPlayers;

        [SetUp]
        public void SetUp()
        {
            _previousPlayers = GameSession.Players;
            Reset();
        }

        [TearDown]
        public void TearDown()
        {
            Reset();
            GameSession.Players = _previousPlayers;
        }

        private static void Reset()
        {
            AiReconMemory.Clear();
            AiMapMemory.Clear();
            ArmyRegistry.Clear();
            BuildingRegistry.Clear();
            VisionSystem.Clear();
        }

        private static AiMapMemory.KnownEnemySighting Sighting(ArmyData army, HexCoord hex, int turn) =>
            new AiMapMemory.KnownEnemySighting(hex, army.Owner, army.Name, 1, 1f, 2f,
                new[] { new WorthIt.DefenderProfile(1f, false, attack: 2f, hitPoints: 4f) },
                seenTurn: turn, armyId: army.Id, maxMovement: 2);

        // Ф4 — Vex's Nomads wiped Mordak's Mud Crawl #33 at T20; the Recon history kept it as a
        // contact drifting toward Vex's Citadel until T22 and called the marching Attack home.
        [Test]
        public void ReconHistory_ForgetsAnArmyTheObserverSawDestroyed()
        {
            var vex = new PlayerSetupData { Nickname = "Vex" };
            var mordak = new PlayerSetupData { Nickname = "Mordak" };
            var bystander = new PlayerSetupData { Nickname = "Bystander" };
            GameSession.Players = new List<PlayerSetupData> { vex, mordak, bystander };
            var hex = new HexCoord(1, 2);
            var mudCrawl = new ArmyData { Owner = mordak, Name = "Mud Crawl" };
            var nomads = new ArmyData { Owner = vex, Name = "Nomads" };

            AiReconMemory.Observe(vex, 20, 1, new[] { Sighting(mudCrawl, hex, 20) });
            AiReconMemory.Observe(bystander, 20, 1, new[] { Sighting(mudCrawl, hex, 20) });
            Assert.That(AiReconMemory.Historical(vex, new HashSet<int>()).Select(o => o.ArmyId),
                Has.Member(mudCrawl.Id));

            AiMapMemory.RecordDestroyed(mudCrawl, hex, nomads, mudCrawl);

            Assert.That(AiMapMemory.IsWitnessedDestroyed(vex, mudCrawl.Id), Is.True, "a battle side learns it");
            Assert.That(AiReconMemory.Historical(vex, new HashSet<int>()).Select(o => o.ArmyId),
                Has.No.Member(mudCrawl.Id), "the death is honoured before the next Observe");
            AiReconMemory.Observe(vex, 21, 2, System.Array.Empty<AiMapMemory.KnownEnemySighting>());
            Assert.That(AiReconMemory.Historical(vex, new HashSet<int>()), Is.Empty);

            Assert.That(AiMapMemory.IsWitnessedDestroyed(bystander, mudCrawl.Id), Is.False,
                "a player who neither fought nor saw the hex learns nothing");
            Assert.That(AiReconMemory.Historical(bystander, new HashSet<int>()).Select(o => o.ArmyId),
                Has.Member(mudCrawl.Id));
        }

        // A garrison container survives its battle (a captured base keeps it): never marked dead.
        [Test]
        public void ReconHistory_NeverMarksAGarrisonContainerDestroyed()
        {
            var vex = new PlayerSetupData { Nickname = "Vex" };
            var mordak = new PlayerSetupData { Nickname = "Mordak" };
            GameSession.Players = new List<PlayerSetupData> { vex, mordak };
            var garrison = new ArmyData { Owner = vex, Name = "Garrison", IsGarrison = true };
            var horde = new ArmyData { Owner = mordak, Name = "Pale Horde" };

            AiMapMemory.RecordDestroyed(garrison, new HexCoord(2, 1), horde, garrison);

            Assert.That(AiMapMemory.IsWitnessedDestroyed(mordak, garrison.Id), Is.False);
        }

        // ---- Ф3: the preparation's staging Base -------------------------------------------------

        private static readonly HexCoord Target = new HexCoord(5, 0);
        private static readonly HexCoord Near = new HexCoord(1, 0);
        private static readonly PlayerSetupData Enemy = new PlayerSetupData { Nickname = "Mordak" };

        private static WorldSnapshot StagingSnap(HexCoord donorBase, out ArmySnapshot host)
        {
            var us = new PlayerSetupData { Nickname = "Vex" };
            host = new ArmySnapshot
            {
                ArmyId = 20, Owner = us, Hex = new HexCoord(0, 0), EffectiveArmyPower = 22f,
                IsStructuralRaidActor = true, CurrentMovement = 2, MaxMovement = 2, MemberCount = 3,
                Capacity = 7, Members = System.Array.Empty<WorthIt.DefenderProfile>(),
                ReachableOwnBaseHexes = new[] { Near, donorBase },
            };
            var body = new WorthIt.DefenderProfile(2f, false, null, 4f, 6f, 2);
            var garrison = new ArmySnapshot
            {
                ArmyId = 31, Owner = us, Hex = donorBase, IsGarrison = true, MemberCount = 4,
                Members = Enumerable.Repeat(body, 4).ToArray(),
                NonHeroSpareable = Enumerable.Repeat(true, 4).ToArray(),
            };
            return new WorldSnapshot
            {
                Observer = us, TurnNumber = 18,
                Self = new SelfSnapshot { Armies = new[] { host, garrison },
                    BaseHexes = new[] { Near, donorBase }, Citadel = donorBase, AttackPeak = 100f },
            };
        }

        // Vex T16-T21: the host walked six hexes (three turns at MaxMovement 2) for a front-line
        // spare that was gone on arrival. A spare is measured against threats two turns out
        // (activeDefenceWithdrawMaxEnemyEta): a donor farther than that is not a staging reason.
        [TestCase(-3, true)]
        [TestCase(-6, false)]
        public void StagingBase_GarrisonDonorOnlyWithinTheThreatHorizon(int donorQ, bool donorChosen)
        {
            var donorBase = new HexCoord(donorQ, 0);
            WorldSnapshot snap = StagingSnap(donorBase, out ArmySnapshot host);
            Assert.That(AttackPreparationPolicy.SpareGarrisonPower(snap, donorBase), Is.GreaterThan(0f));

            Assert.That(AttackPreparationPolicy.PreparationStagingBase(snap, Target, host),
                Is.EqualTo(donorChosen ? donorBase : Near));
        }

        // A live preparation keeps the Base it was born with while that Base is own and reachable;
        // only its loss re-reads the choice.
        [Test]
        public void StagingBase_LivePreparationKeepsItsFrozenBaseUntilLost()
        {
            var donorBase = new HexCoord(-3, 0);
            WorldSnapshot snap = StagingSnap(donorBase, out ArmySnapshot host);
            var attack = new AttackIntent
            {
                Target = AttackTargetRef.For(Target, Enemy, AttackTargetKind.Base), Preparation = true,
                Phase = AttackMissionPhase.Gather, PrimaryArmyId = host.ArmyId, StagingHex = Near,
            };
            Assert.That(AttackPreparationPolicy.KeptStagingBase(snap, attack, host), Is.EqualTo(Near),
                "the fresh choice would be the donor; the operation keeps its own Base");

            snap.Self.BaseHexes = new[] { donorBase };
            host.ReachableOwnBaseHexes = new[] { donorBase };
            Assert.That(AttackPreparationPolicy.KeptStagingBase(snap, attack, host), Is.EqualTo(donorBase),
                "a lost staging Base is re-chosen");
        }

        // ---- Ф1: the second, surplus-only operation ---------------------------------------------

        private static MissionIntent AttackOp(int x, bool secondary, AttackMissionPhase phase = AttackMissionPhase.Assault) =>
            new MissionIntent
            {
                Kind = MissionKind.Attack, Status = IntentStatus.Active,
                Objective = new AttackIntent
                {
                    Target = AttackTargetRef.For(new HexCoord(x, 0), Enemy, AttackTargetKind.Base),
                    Phase = phase, Secondary = secondary,
                },
            };

        [Test]
        public void SecondaryOperation_IsNeverTheOneLiveOperation()
        {
            MissionIntent main = AttackOp(1, secondary: false);
            MissionIntent second = AttackOp(2, secondary: true);
            var both = new List<MissionIntent> { second, main };

            Assert.That(AggressionMissionLayer.LiveAttackOperation(both), Is.SameAs(main));
            Assert.That(AggressionMissionLayer.LiveSecondaryAttack(both), Is.SameAs(second));
            Assert.That(AggressionMissionLayer.LiveAttackOperation(new List<MissionIntent> { second }), Is.Null,
                "a live second operation does not hold the main slot");
            Assert.That(AggressionMissionLayer.LiveSecondaryAttack(new List<MissionIntent>
                { AttackOp(3, secondary: true, phase: AttackMissionPhase.RecoveryReturn) }), Is.Null,
                "a return leg is not a live operation");
        }

        // The peak roster uses two Ash Runners and Hank of a pool of three Ash Runners, one Hank and
        // two Scrap Mortars: one Ash Runner is spare, Hank is not, the Mortars are free.
        [Test]
        public void SecondaryOperation_SurplusIsThePoolBeyondThePeakPositions()
        {
            var self = new SelfSnapshot
            {
                StrikeRoster = new[]
                {
                    new StrikeRosterSlot("ash-runner", false, 5f, ForceSource.Map),
                    new StrikeRosterSlot("ash-runner", false, 5f, ForceSource.Map),
                    new StrikeRosterSlot("hank-mercer", true, 0f, ForceSource.Map),
                },
                StrikePoolKeyCounts = new Dictionary<string, int>
                    { ["ash-runner"] = 3, ["hank-mercer"] = 1, ["scrap-mortar"] = 2 },
            };

            Dictionary<string, int> spare = AttackSecondaryPolicy.SurplusByKey(self);

            Assert.That(spare["ash-runner"], Is.EqualTo(1));
            Assert.That(spare["hank-mercer"], Is.Zero);
            Assert.That(spare.ContainsKey("scrap-mortar"), Is.False, "a key the peak never uses is free");
        }

        // ---- Ф2: which Recon role may lend its scout ---------------------------------------------

        private static MissionIntent ScoutRole(int mover, CommitmentTier funding = CommitmentTier.None) =>
            new MissionIntent
            {
                Kind = MissionKind.Scout, Status = IntentStatus.Active, Funding = funding,
                PreferredMoverArmyId = mover, LastIntrinsicValue = 6.3f,
                Objective = new ScoutIntent { Kind = ScoutTargetKind.Refresh, FocusHex = new HexCoord(-2, -2) },
            };

        // Mordak T17-T20: Hooded #15 (two hexes from Base (1,-1)) kept a durable Refresh worth 6.3
        // while the Assault's observation (16-21) went unserved. Its role may lend it; a Hard lane,
        // or an actor another operation also holds (an Economy loan), never does.
        [Test]
        public void ReconDisplacement_OnlyAnOrdinaryReconRoleLendsItsScout()
        {
            MissionIntent lane = ScoutRole(15);
            Assert.That(ReconMissionPlanner.DisplaceableReconRole(new List<MissionIntent> { lane }, 15),
                Is.SameAs(lane));
            Assert.That(lane.DisplacementValue, Is.EqualTo(6.3f).Within(1e-5f));
            Assert.That(ReconMissionPlanner.DisplaceableReconRole(
                new List<MissionIntent> { ScoutRole(15, CommitmentTier.Hard) }, 15), Is.Null);
            var loan = new MissionIntent { Kind = MissionKind.Economy, Status = IntentStatus.Active,
                PreferredMoverArmyId = 15 };
            Assert.That(ReconMissionPlanner.DisplaceableReconRole(
                new List<MissionIntent> { lane, loan }, 15), Is.Null);
        }

        // ---- S2: the second operation from a garrison with no field host ------------------------

        private static WorthIt.DefenderProfile Profile(float attack, float defense, float hp) =>
            new WorthIt.DefenderProfile(defense, false, null, attack, hp, 3, null, hp);

        // Mordak's Base (2,0) next to Vex's (2,1); no field army stands anywhere. The garrison keeps
        // its weakest body as cover and its floor; a spare surplus body takes the observed site.
        private static WorldSnapshot SortieWorld(int seenTurn, out AttackObjective objective)
        {
            var us = new PlayerSetupData { Nickname = "Mordak" };
            var home = new HexCoord(2, 0);
            var site = new HexCoord(2, 1);
            var roster = new[] { Profile(12, 6, 18), Profile(9, 4, 14), Profile(1, 0, 2) };
            int[] ids = { 501, 502, 503 };
            var live = ArmyData.WithIdentity(37); live.Owner = us; live.Hex = home; live.IsGarrison = true;
            foreach (int id in ids)
            {
                var unit = UnitData.CreateProjection(id); unit.Owner = us; unit.HitPointsCurrent = 10;
                live.Members.Add(unit);
            }
            ArmyRegistry.Register(live);
            var garrison = new ArmySnapshot
            {
                ArmyId = 37, Owner = us, Hex = home, IsGarrison = true, MemberCount = 3, Members = roster,
                EffectiveArmyPower = AiPower.EffectiveArmyPowerFromProfiles(roster),
                NonHeroSpareable = new[] { true, true, false }, NonHeroRuntimeIds = ids,
                NonHeroMoveMax = new[] { 3, 3, 3 }, NonHeroCurrentMovement = new[] { 3, 3, 3 },
                NonHeroActivationApCosts = new[] { 2, 1, 1 }, NonHeroIsAviation = new[] { false, false, false },
            };
            var shell = new ArmySnapshot { ArmyId = 30, Owner = us, Hex = home, Capacity = 2 };
            objective = new AttackObjective { Target = AttackTargetRef.For(site, Enemy, AttackTargetKind.Base) };
            return new WorldSnapshot
            {
                Observer = us, TurnNumber = 9,
                Self = new SelfSnapshot { Citadel = new HexCoord(-3, -3), BaseHexes = new[] { home },
                    Armies = new[] { garrison, shell } },
                Known = new KnownSnapshot
                {
                    Buildings = new[] { new AiMapMemory.KnownBuilding(site, Enemy, false, null,
                        isBase: true, seenTurn: seenTurn) },
                    EnemySightings = System.Array.Empty<AiMapMemory.KnownEnemySighting>(),
                },
                Threat = new ThreatModel { Contacts = System.Array.Empty<EnemyContactSnapshot>(),
                    Threats = System.Array.Empty<AssetThreatSnapshot>() },
            };
        }

        [Test]
        public void GarrisonSortie_TakesTheObservedBaseNextDoorWithSpareSurplus()
        {
            WorldSnapshot snap = SortieWorld(9, out AttackObjective objective);

            GarrisonSortiePlan plan = AttackSecondaryPolicy.PlanGarrisonSortie(snap, objective, 0f, new HashSet<int>());

            Assert.That(plan, Is.Not.Null);
            Assert.That(plan.UnitRuntimeIds, Is.EqualTo(new[] { 501 }), "strongest spare body first, the minimum");
            Assert.That(plan.CoverRuntimeId, Is.EqualTo(503), "the weakest body stays as cover");
            Assert.That(plan.ShellArmyId, Is.EqualTo(30), "an empty shell on the Base is reused");
            Assert.That(plan.EnemyArmyId, Is.EqualTo(-1));
            Assert.That(plan.EnemyHex, Is.EqualTo(objective.Hex));
            Assert.That(snap.Self.Armies[0].MemberCount, Is.EqualTo(3), "planning stays read-only");

            var leg = new MissionProposal { Kind = MissionKind.Attack, Target = new AttackMissionTarget
                { Phase = AttackMissionPhase.Assault, Target = objective.Target, Secondary = true,
                  Sortie = plan, PrimaryArmyId = plan.ShellArmyId } };
            Assert.That(GroundCombatLegs.JoinsAssignmentSolve(leg), Is.False,
                "materialized by its own step, never a pick of the ground assignment solve");
            Assert.That(GroundCombatLegs.PinsActors(leg, out int? shellId, out int? donorId), Is.True);
            Assert.That(shellId, Is.EqualTo(30));
            Assert.That(donorId, Is.EqualTo(37));
        }

        // The main fist's body is never taken; a site seen too long ago is not struck.
        [Test]
        public void GarrisonSortie_LeavesPeakBodiesAndNeedsAFreshObservation()
        {
            WorldSnapshot snap = SortieWorld(9, out AttackObjective objective);
            snap.Self.StrikeRoster = new[] { new StrikeRosterSlot("unit#501", false, 12f, ForceSource.Map) };
            snap.Self.StrikePoolKeyCounts = new Dictionary<string, int> { ["unit#501"] = 1 };
            Assert.That(AttackSecondaryPolicy.PlanGarrisonSortie(snap, objective, 0f, new HashSet<int>())
                .UnitRuntimeIds, Is.EqualTo(new[] { 502 }));

            Reset();
            WorldSnapshot stale = SortieWorld(5, out AttackObjective old);
            Assert.That(AttackSecondaryPolicy.PlanGarrisonSortie(stale, old, 0f, new HashSet<int>()), Is.Null);
        }

        // ---- B: a preparation host's detour --------------------------------------------------------

        // A detour never competes with another leg that pins the host, never runs outside a walking
        // preparation and never twice in a turn.
        [Test]
        public void PreparationDetour_OnlyForAWalkingPreparationWithNothingElseHoldingTheHost()
        {
            WorldSnapshot snap = StagingSnap(new HexCoord(-3, 0), out ArmySnapshot host);
            var objectives = new List<AttackObjective>();
            AttackIntent Prep() => new AttackIntent
            {
                Target = AttackTargetRef.For(Target, Enemy, AttackTargetKind.Base), Preparation = true,
                Phase = AttackMissionPhase.Gather, PrimaryArmyId = host.ArmyId, StagingHex = Near,
            };

            AttackIntent withSupport = Prep();
            withSupport.GatherSupportArmyIds.Add(17);
            Assert.That(AttackIntermediateBasePolicy.SelectForPreparationWalk(snap, withSupport, objectives), Is.Null);
            AttackIntent assault = Prep();
            assault.Phase = AttackMissionPhase.Assault;
            Assert.That(AttackIntermediateBasePolicy.SelectForPreparationWalk(snap, assault, objectives), Is.Null);
            AttackIntent struck = Prep();
            struck.LastOpportunisticStrikeTurn = snap.TurnNumber;
            Assert.That(AttackIntermediateBasePolicy.SelectForPreparationWalk(snap, struck, objectives), Is.Null);
        }
    }
}
#endif
