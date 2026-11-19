#if UNITY_INCLUDE_TESTS
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
    // 2026-11-19 (project owner's decisions, match Tessek vs Draven): free armies idled 50 of 69
    // army-turns while garrisons held ~163 power against ~70 in the field; Tessek founded Base
    // (-2,0) two hexes from a 63-power enemy Base with one Crawler. The standing fist musters on the
    // staging Base before the gate, its power covers the garrison floor of every own Base it
    // reaches before the enemy, a garrison takes no surplus card above its floor, and a founding
    // site prices the enemy that reaches it (garrisons included).
    public class AiFistMusterTests
    {
        private static readonly HexCoord Citadel = new HexCoord(0, 0);
        private static readonly HexCoord Staging = new HexCoord(3, 0);
        private static readonly HexCoord EnemyCitadel = new HexCoord(6, 0);
        private const int IdleId = 5, FistId = 7, StagingGarrisonId = 2;

        [SetUp]
        public void SetUp()
        {
            ArmyRegistry.Clear();
            MissionIntentRegistry.Clear();
            AiMapMemory.Clear();
            AiReconMemory.Clear();
            VisionSystem.Clear();
        }

        [TearDown]
        public void TearDown()
        {
            ArmyRegistry.Clear();
            MissionIntentRegistry.Clear();
            AiMapMemory.Clear();
            AiReconMemory.Clear();
            VisionSystem.Clear();
        }

        private static WorthIt.DefenderProfile Profile(float atk, float def, float hp) =>
            new WorthIt.DefenderProfile(def, false, null, atk, hp, 2, null, hp);

        private static ArmySnapshot Field(PlayerSetupData owner, int id, HexCoord hex, int capacity,
            bool hero, params WorthIt.DefenderProfile[] bodies) => new ArmySnapshot
        {
            ArmyId = id, Owner = owner, Hex = hex, MemberCount = bodies.Length + (hero ? 1 : 0),
            Members = bodies, HasHero = hero, HeroCount = hero ? 1 : 0, Capacity = capacity,
            OccupiedBattleSlots = bodies.Length + (hero ? 1 : 0),
            EffectiveArmyPower = AiPower.EffectiveArmyPowerFromProfiles(bodies),
            IsStructuralRaidActor = true, MaxMovement = 3, CurrentMovement = 3, ActivationApCost = 1,
            ReachableOwnBaseHexes = new[] { Citadel, Staging },
            // Analysis sets this for EVERY hero-led army (WorldAnalysis.Self: AiArmyRoles.IsHeroLed).
            IsMobileEconomyBuilder = hero,
        };

        private static (WorldSnapshot Snap, AttackObjective Objective) World(PlayerSetupData us)
        {
            var enemy = new PlayerSetupData { Nickname = "Them", ColorIndex = 4 };
            var garrisonBodies = new[] { Profile(4, 2, 6), Profile(4, 2, 6), Profile(4, 2, 6) };
            var garrison = new ArmySnapshot
            {
                ArmyId = StagingGarrisonId, Owner = us, Hex = Staging, IsGarrison = true, Capacity = 4,
                MemberCount = 3, OccupiedBattleSlots = 3, Members = garrisonBodies,
                EffectiveArmyPower = AiPower.EffectiveArmyPowerFromProfiles(garrisonBodies),
                NonHeroSpareable = new[] { true, true, true },
                NonHeroRuntimeIds = new[] { 21, 22, 23 },
                NonHeroMoveMax = new[] { 3, 3, 3 }, NonHeroCurrentMovement = new[] { 3, 3, 3 },
                NonHeroActivationApCosts = new[] { 1, 1, 1 },
                NonHeroIsAviation = new[] { false, false, false },
            };
            ArmySnapshot idle = Field(us, IdleId, Citadel, 2, false, Profile(6, 4, 8));
            ArmySnapshot fist = Field(us, FistId, Staging, 8, true, Profile(5, 3, 8), Profile(5, 3, 8));
            // Own share floor of the staging Base (5% of the ground force) between what one and two
            // of its bodies hold: one body may leave, a second may not.
            float one = AiPower.EffectiveArmyPowerFromProfiles(garrisonBodies.Take(1).ToList());
            float two = AiPower.EffectiveArmyPowerFromProfiles(garrisonBodies.Take(2).ToList());
            float available = 0.5f * (one + two) / AiConfig.garrisonDefenceShareBase;
            var snap = new WorldSnapshot
            {
                Observer = us, TurnNumber = 10,
                Self = new SelfSnapshot
                {
                    Citadel = Citadel, BaseHexes = new[] { Citadel, Staging },
                    Armies = new List<ArmySnapshot> { garrison, idle, fist },
                    AttackPeak = 40f, AvailablePower = available, DeployedPower = 1f,
                },
                Known = new KnownSnapshot(),
                Threat = new ThreatModel
                {
                    Contacts = new List<EnemyContactSnapshot>(),
                    Threats = new List<AssetThreatSnapshot>(),
                },
            };
            var objective = new AttackObjective
            {
                Target = AttackTargetRef.For(EnemyCitadel, enemy, AttackTargetKind.Citadel),
                TaskScore = new TaskScore(strategicRelevance: 10f),
            };
            return (snap, objective);
        }

        [Test]
        public void IdleArmyWalksToTheStagingBase_AndTheGarrisonGivesTheFistOnlyAboveItsOwnShare()
        {
            var us = new PlayerSetupData { Nickname = "Us", ColorIndex = 1 };
            (WorldSnapshot snap, AttackObjective objective) = World(us);
            Assert.That(FistMusterObjectiveEvaluator.StagingHex(snap, objective), Is.EqualTo(Staging),
                "the fist gathers on the own Base nearest to the objective");
            Assert.That(FistMusterObjectiveEvaluator.FistHost(snap, Staging, new HashSet<int>())?.ArmyId,
                Is.EqualTo(FistId), "a hero-led army is the fist (IsMobileEconomyBuilder is no exclusion)");

            List<FistMusterObjective> steps = FistMusterObjectiveEvaluator.Enumerate(snap, objective, new HashSet<int>());

            FistMusterObjective walk = steps.Single(s => s.Kind == FistMusterKind.Walk);
            Assert.That(walk.Mover.ArmyId, Is.EqualTo(IdleId));
            Assert.That(walk.StagingHex, Is.EqualTo(Staging));
            Assert.That(walk.TaskScore.Value, Is.GreaterThan(0f), "an idle army joining the fist is worth its walk");
            Assert.That(steps.Any(s => s.Kind == FistMusterKind.Walk && s.Mover.ArmyId == FistId), Is.False,
                "the fist itself already stands on the staging Base");

            FistMusterObjective intake = steps.Single(s => s.Kind == FistMusterKind.Intake);
            Assert.That(intake.Mover.ArmyId, Is.EqualTo(FistId));
            Assert.That(intake.DonorGarrisonId, Is.EqualTo(StagingGarrisonId));
            Assert.That(intake.UnitRuntimeIds.Count, Is.EqualTo(1),
                "the garrison keeps its own share floor: two of its three bodies stay");
            Assert.That(intake.EtaTurns, Is.Zero);
        }

        [Test]
        public void NoValueObjective_MustersNothing()
        {
            var us = new PlayerSetupData { Nickname = "Us", ColorIndex = 1 };
            (WorldSnapshot snap, AttackObjective objective) = World(us);
            objective.TaskScore = default(TaskScore);
            Assert.That(FistMusterObjectiveEvaluator.Enumerate(snap, objective, new HashSet<int>()), Is.Empty);
        }

        // ---- mobile reserve ------------------------------------------------------------------

        private static UnitData Body(PlayerSetupData owner, string name, int attack) => new UnitData
        {
            Name = name, Owner = owner, Attack = attack, Defense = 2,
            HitPointsMax = 6, HitPointsCurrent = 6, Initiative = 2, MoveMax = 3, MoveCurrent = 3,
        };

        private static ArmyData Army(PlayerSetupData owner, HexCoord hex, bool garrison, params UnitData[] members)
        {
            var army = new ArmyData { Owner = owner, Hex = hex, IsGarrison = garrison };
            army.Members.AddRange(members);
            ArmyRegistry.Register(army);
            return army;
        }

        [TestCase(2, 4, true)]
        [TestCase(9, 4, false)]
        [TestCase(2, 2, false)]
        public void FistOnAnotherBase_CoversTheNeedOfABaseItReachesBeforeTheEnemy_NeverItsRelease(
            int fistQ, int enemyDistance, bool covers)
        {
            var us = new PlayerSetupData { Nickname = "Us", ColorIndex = 1 };
            var them = new PlayerSetupData { Nickname = "Them", ColorIndex = 4 };
            var threatened = new HexCoord(0, 0);
            var fistBase = new HexCoord(fistQ, 0);
            Army(us, threatened, true, Body(us, "G", 2));
            Army(us, fistBase, true, Body(us, "H", 2));
            var enemyBodies = new[] { new WorthIt.DefenderProfile(3f, false, attack: 6f, hitPoints: 6f),
                new WorthIt.DefenderProfile(3f, false, attack: 6f, hitPoints: 6f) };
            var raider = new AiMapMemory.KnownEnemySighting(new HexCoord(-enemyDistance, 0), them, "raider", 2, 6f, 12f,
                enemyBodies, seenTurn: 10, armyId: 31, maxMovement: 3);
            AiMapMemory.RememberForTest(us, raider);
            const float available = 10f;   // share floor 0.5: the threat is the floor

            float threat = AiArmyRoles.GarrisonThreatFloor(us, threatened, false, out int eta);
            Assert.That(threat, Is.GreaterThan(0f));
            Assert.That(eta, Is.EqualTo((enemyDistance + 2) / 3));
            float release = AiArmyRoles.GarrisonDefenceFloor(us, available, threatened, false);
            Assert.That(release, Is.EqualTo(threat).Within(1e-3f));
            Assert.That(AiArmyRoles.GarrisonNeedFloor(us, available, threatened, false),
                Is.EqualTo(release).Within(1e-3f), "no fist: need and release agree");

            ArmyData fist = Army(us, fistBase, false,
                Enumerable.Range(0, 6).Select(i => Body(us, "F" + i, 8)).ToArray());
            float fistPower = AiPower.EffectiveArmyPower(fist.Members.ToList());
            float need = AiArmyRoles.GarrisonNeedFloor(us, available, threatened, false);
            if (covers)
                Assert.That(need, Is.EqualTo(System.Math.Max(AiArmyRoles.GarrisonDefenceFloor(available, false),
                    threat - fistPower)).Within(1e-3f), "two hexes away the fist arrives before the raider");
            else
                Assert.That(need, Is.EqualTo(release).Within(1e-3f),
                    "too far, or on the same turn as the enemy (who may move first): no credit");
            Assert.That(AiArmyRoles.GarrisonDefenceFloor(us, available, threatened, false),
                Is.EqualTo(release).Within(1e-3f),
                "the release floor never takes the credit: bodies freed by the fist would leave with it");
        }

        [Test]
        public void LiveFistContainer_IsACombatArmyOrACommandingLoneHero_NeverAScoutOrSupportHero()
        {
            var us = new PlayerSetupData { Nickname = "Us", ColorIndex = 1 };
            ArmyData fighters = Army(us, Staging, false, Body(us, "A", 6), Body(us, "B", 6));
            Assert.That(FistMusterObjectiveEvaluator.IsLiveFistContainer(us, fighters), Is.True);
            ArmyData garrison = Army(us, Staging, true, Body(us, "G", 6));
            Assert.That(FistMusterObjectiveEvaluator.IsLiveFistContainer(us, garrison), Is.False);
            var commander = new UnitData { Name = "Cmd", Owner = us, IsHero = true, CommandRating = 7,
                HitPointsMax = 6, HitPointsCurrent = 6, MoveMax = 3, MoveCurrent = 3 };
            Assert.That(FistMusterObjectiveEvaluator.IsLiveFistContainer(us, Army(us, Staging, false, commander)), Is.True);
            var support = new UnitData { Name = "Sup", Owner = us, IsHero = true, CommandRating = 7,
                HitPointsMax = 6, HitPointsCurrent = 6, MoveMax = 3, MoveCurrent = 3 };
            support.TypeTags.Add(Game.Cards.UnitTypeTag.Support);
            Assert.That(FistMusterObjectiveEvaluator.IsLiveFistContainer(us, Army(us, Staging, false, support)), Is.False,
                "a Support hero stays home, it never commands the fist");
        }

        // 2026-11-19 review: JoinFist built its ownership view without a snapshot, and
        // MissionActorPolicy.Build claims nothing without one, so an army another operation owns
        // could take the muster mover's bodies. The executor's snapshot now carries the claims.
        [TestCase(true)]
        [TestCase(false)]
        public void MusterMover_NeverJoinsAnArmyAnotherOperationOwns(bool claimed)
        {
            var us = new PlayerSetupData { Nickname = "Us", ColorIndex = 1 };
            ArmyData host = Army(us, Staging, false, Body(us, "A", 6));
            ArmyData mover = Army(us, Staging, false, Body(us, "B", 6));
            var snap = new WorldSnapshot
            {
                Observer = us, TurnNumber = 10,
                Self = new SelfSnapshot
                {
                    Citadel = Citadel, BaseHexes = new[] { Citadel, Staging },
                    Armies = new List<ArmySnapshot>
                    {
                        Field(us, host.Id, Staging, 2, false, Profile(6, 2, 6)),
                        Field(us, mover.Id, Staging, 2, false, Profile(6, 2, 6)),
                    },
                },
                Known = new KnownSnapshot(),
            };
            var state = MissionIntentRegistry.GetOrCreate(us);
            snap.Intents = state;
            if (claimed)
            {
                var attack = new MissionIntent { Kind = MissionKind.Attack, Status = IntentStatus.Active,
                    Objective = new AttackIntent { PrimaryArmyId = host.Id, Phase = AttackMissionPhase.Gather } };
                attack.IntentKey = MissionIntentKey.For(attack);
                state.Put(attack);
                Assert.That(ActorCommitments.FromIntents(WorldSnapshot.IntentsOf(snap), snap, null)
                    .IsArmyClaimed(host.Id), Is.True, "precondition: the Attack owns the host");
            }

            int moved = ActiveDefenceExecutor.JoinFist(us, null, mover, Staging, snap);

            if (claimed)
            {
                Assert.That(moved, Is.Zero, "the claimed army is no fist");
                Assert.That(host.Members.Count, Is.EqualTo(1));
                Assert.That(mover.Members.Count, Is.EqualTo(1));
            }
            else
                Assert.That(moved, Is.GreaterThan(0), "a free army on the Base is the fist the mover joins");
        }

        // ---- founding site --------------------------------------------------------------------

        [Test]
        public void FoundingSiteThreat_CountsTheEnemyGarrisonNextDoor_TheGarrisonFloorDoesNot()
        {
            var site = new HexCoord(-2, 0);
            var enemyBase = new AiMapMemory.KnownEnemySighting(new HexCoord(0, -1), new PlayerSetupData(), "garrison",
                7, 20f, 40f, new[] { new WorthIt.DefenderProfile(5f, false, attack: 8f, hitPoints: 8f) },
                armyId: 12, isGarrison: true, maxMovement: 3);
            var contacts = new[] { (enemyBase, 0) };
            float site_ = AiArmyRoles.GarrisonThreatFloor(contacts, new[] { site }, site, true, out int eta,
                includeGarrisons: true);
            Assert.That(site_, Is.EqualTo(AiPower.SightingPower(enemyBase) * AiConfigV2.aggDefenceConfidenceMargin)
                .Within(1e-3f));
            Assert.That(eta, Is.EqualTo(1));
            Assert.That(AiArmyRoles.GarrisonThreatFloor(contacts, new[] { site }, site, true, out _), Is.Zero,
                "an own garrison's floor keeps ignoring enemy garrisons");
        }

        [Test]
        public void FoundingDefence_ShortfallIsTheSiteRisk()
        {
            var full = new FoundingSiteDefence.Assessment(80f, 10f, 20f, 50f, 1);
            Assert.That(full.ShortfallShare, Is.Zero, "the party, the hand and the fist hold it");
            var bare = new FoundingSiteDefence.Assessment(80f, 8f, 12f, 0f, 1);
            Assert.That(bare.ShortfallShare, Is.EqualTo(0.75f).Within(1e-4f));
            Assert.That(new FoundingSiteDefence.Assessment(0f, 0f, 0f, 0f, int.MaxValue).ShortfallShare, Is.Zero);
        }

        // ---- no garrison overfill -------------------------------------------------------------

        [TestCase(100f, true)]
        [TestCase(300f, false)]
        public void SurplusCard_DoesNotOverfillAGarrisonThatHoldsItsNeedFloor(float available, bool held)
        {
            var us = new PlayerSetupData { Nickname = "Us", ColorIndex = 1 };
            ArmyData live = Army(us, Citadel, true, Body(us, "G1", 4), Body(us, "G2", 4));
            var bodies = new[] { Profile(4, 2, 6), Profile(4, 2, 6) };
            // The Citadel share floor is 10% of the ground force: 10 against 20 held, 30 against 20.
            var garrison = new ArmySnapshot
            {
                ArmyId = live.Id, Owner = us, Hex = Citadel, IsGarrison = true, Members = bodies,
                MemberCount = 2, EffectiveArmyPower = 20f,
            };
            var snap = new WorldSnapshot
            {
                Observer = us,
                Self = new SelfSnapshot
                {
                    Citadel = Citadel, AvailablePower = available,
                    Armies = new List<ArmySnapshot> { garrison },
                },
            };
            var plan = new MaterializationPlan { Deploy = new PlacementOption(Citadel, DeploymentKind.Garrison, live) };
            Assert.That(TempoCandidateProvider.GarrisonHoldsItsFloor(snap, plan, null, out _), Is.EqualTo(held));
            var toField = new MaterializationPlan { Deploy = new PlacementOption(Citadel, DeploymentKind.ExistingArmy, live) };
            Assert.That(TempoCandidateProvider.GarrisonHoldsItsFloor(snap, toField, null, out _), Is.False,
                "only a garrison deposit is held back");
        }
    }
}
#endif
