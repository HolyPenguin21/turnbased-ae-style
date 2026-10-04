#if UNITY_INCLUDE_TESTS
using System.Collections.Generic;
using System.Linq;
using Game.Ai.V2;
using Game.Aviation;
using Game.Combat;
using Game.Economy;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Units;
using NUnit.Framework;
using UnityEngine;

namespace Game.EditorTests
{
    // The complete aviation sortie contract (2026-10-04 task): launch paid once per sortie,
    // continuation / repeat strikes free on every turn, landing back into the airfield with the
    // shell kept, one capacity rule, the strike-series calendar, the shared estimator and the
    // ActiveDefence air-support identity.
    public class AiAviationSortieCycleTests
    {
        private PlayerSetupData _owner;
        private GameObject _rootObject;
        private PlayerRoot _root;

        [SetUp]
        public void SetUp()
        {
            ArmyRegistry.Clear();
            BuildingRegistry.Clear();
            PlayerRootRegistry.Clear();
            AirSortieRegistry.Clear();
            HexEventRegistry.Clear();
            _owner = new PlayerSetupData();
        }

        [TearDown]
        public void TearDown()
        {
            ArmyRegistry.Clear();
            BuildingRegistry.Clear();
            PlayerRootRegistry.Clear();
            AirSortieRegistry.Clear();
            HexEventRegistry.Clear();
            if (!ReferenceEquals(_rootObject, null))
                Object.DestroyImmediate(_rootObject);
            _rootObject = null;
            _root = null;
        }

        // PlayerRoot is a MonoBehaviour: only the tests that pay through the bank create one.
        private PlayerRoot Root()
        {
            if (!ReferenceEquals(_root, null))
                return _root;
            _rootObject = new GameObject("aviation-cycle-root");
            _root = _rootObject.AddComponent<PlayerRoot>();
            PlayerRootRegistry.Register(_owner, _root);
            return _root;
        }

        // ---- A. payment ------------------------------------------------------------------------

        [Test]
        public void Launch_IsChargedOnce_AndContinuationIsFreeOnLaterTurnsWithZeroResources()
        {
            ArmyData wing = Wing(new HexCoord(3, 0), Plane(ap: 1, energy: 2), Plane(ap: 2, energy: 3));
            Root().ActionPoints = 3;
            _root.AddResource(ResourceType.Energy, 5);

            Assert.That(wing.PendingActivationApCost, Is.EqualTo(3));
            Assert.That(wing.PendingActivationEnergyCost, Is.EqualTo(5));
            Assert.That(ArmyActions.TryPayActivation(wing, _root), Is.True);
            Assert.That(_root.ActionPoints, Is.Zero);
            Assert.That(_root.GetResource(ResourceType.Energy), Is.Zero);
            Assert.That(wing.Members.All(m => m.SortieLaunchPaid), Is.True);

            // Same turn, then a new turn with nothing in the bank: still free.
            Assert.That(ArmyActions.TryPayActivation(wing, _root), Is.True);
            wing.ResetActivationForNewTurn();
            Assert.That(wing.PendingActivationApCost, Is.Zero);
            Assert.That(wing.PendingActivationEnergyCost, Is.Zero);
            Assert.That(ArmyActions.CanAffordActivation(wing, _root), Is.True);
            Assert.That(ArmyActions.TryPayActivation(wing, _root), Is.True);
            Assert.That(_root.ActionPoints, Is.Zero);
            Assert.That(_root.GetResource(ResourceType.Energy), Is.Zero);
        }

        [Test]
        public void RejectedLaunch_ChargesNothing()
        {
            ArmyData wing = Wing(new HexCoord(3, 0), Plane(ap: 2, energy: 3));
            Root().ActionPoints = 2;
            _root.AddResource(ResourceType.Energy, 2);
            Assert.That(ArmyActions.TryPayActivation(wing, _root), Is.False);
            Assert.That(_root.ActionPoints, Is.EqualTo(2));
            Assert.That(_root.GetResource(ResourceType.Energy), Is.EqualTo(2));
            Assert.That(wing.Members[0].SortieLaunchPaid, Is.False);
        }

        [Test]
        public void GroundActivation_KeepsPerTurnSemantics()
        {
            var army = new ArmyData { Owner = _owner };
            army.AddMemberSorted(new UnitData { Owner = _owner, ActivationApCost = 2, MoveMax = 3, MoveCurrent = 3 });
            ArmyRegistry.Register(army);
            Root().ActionPoints = 4;
            Assert.That(army.PendingActivationApCost, Is.EqualTo(2));
            Assert.That(ArmyActions.TryPayActivation(army, _root), Is.True);
            Assert.That(army.PendingActivationApCost, Is.Zero);
            army.ResetActivationForNewTurn();
            Assert.That(army.PendingActivationApCost, Is.EqualTo(2), "ground armies pay again every turn");
            Assert.That(army.PendingActivationEnergyCost, Is.Zero);
        }

        [Test]
        public void StoredAircraftJoiningAPaidWing_OwesOnlyItsOwnShare_AndTransferChargesNothing()
        {
            HexCoord hex = new HexCoord(0, 0);
            ArmyData airfield = Airfield(hex, capacity: 4);
            ArmyData wing = Wing(hex, Plane(ap: 1, energy: 1));
            wing.Members[0].SortieLaunchPaid = true;
            UnitData stored = Plane(ap: 2, energy: 3);
            airfield.AddMemberSorted(stored);
            wing.MarkActivated();

            Assert.That(ArmyActions.TransferMember(stored, airfield, wing, null, out string why), Is.True, why);
            Assert.That(wing.PendingActivationApCost, Is.EqualTo(2), "only the newcomer's launch share");
            Assert.That(wing.PendingActivationEnergyCost, Is.EqualTo(3));
        }

        [Test]
        public void ShellSwap_DoesNotGiveAFreeNewSortieAfterLanding()
        {
            HexCoord hex = new HexCoord(0, 0);
            ArmyData airfield = Airfield(hex, capacity: 2);
            UnitData plane = Plane(ap: 1, energy: 2);
            ArmyData wing = Wing(hex, plane);
            plane.SortieLaunchPaid = true;
            plane.ConsecutiveUnlandedEnds = 1;

            Assert.That(AviationActions.LandInSlotOrder(wing, null), Is.EqualTo(1));
            Assert.That(plane.SortieLaunchPaid, Is.False, "landing closes the sortie");
            Assert.That(ArmyActions.TransferMember(plane, airfield, wing, null, out string why), Is.True, why);
            Assert.That(wing.PendingActivationApCost, Is.EqualTo(1), "a new sortie is paid again");
            Assert.That(wing.PendingActivationEnergyCost, Is.EqualTo(2));
        }

        // ---- landing, shells and capacity ------------------------------------------------------

        [Test]
        public void Landing_ReturnsAircraftToTheAirfield_KeepsTheShell_AndRepairsNothing()
        {
            HexCoord hex = new HexCoord(0, 0);
            ArmyData airfield = Airfield(hex, capacity: 2);
            UnitData plane = Plane(ap: 1, energy: 1);
            plane.HitPointsCurrent = 1;
            plane.HasEmergencyFlightPenalty = true;
            plane.ConsecutiveUnlandedEnds = 2;
            plane.SortieLaunchPaid = true;
            plane.HasAirAttackedThisTurn = true;
            plane.MoveCurrent = 1;
            ArmyData wing = Wing(hex, plane);
            int id = wing.Id;

            Assert.That(AviationActions.LandInSlotOrder(wing, null), Is.EqualTo(1));
            Assert.That(airfield.Members, Does.Contain(plane));
            Assert.That(wing.Members, Is.Empty);
            Assert.That(ArmyRegistry.AllForOwner(_owner).Any(a => a.Id == id), Is.True, "the shell is kept");
            Assert.That(wing.IsAirArmy, Is.False, "an ordinary empty shell again");
            Assert.That(plane.HitPointsCurrent, Is.EqualTo(1), "landing never repairs");
            Assert.That(plane.HasEmergencyFlightPenalty, Is.False);
            Assert.That(plane.ConsecutiveUnlandedEnds, Is.Zero);
            Assert.That(plane.HasAirAttackedThisTurn, Is.True, "a spent strike is not restored");
            Assert.That(plane.MoveCurrent, Is.EqualTo(1), "movement is not restored");
        }

        [Test]
        public void EndOfTurn_LandsOnlyWhatFits_AndPassingThroughLandsNothing()
        {
            HexCoord hex = new HexCoord(0, 0);
            ArmyData airfield = Airfield(hex, capacity: 1);
            ArmyData wing = Wing(hex, Plane(1, 1), Plane(1, 1));
            AviationTurnLifecycle.ResolveEndOfTurn(_owner, null);
            Assert.That(airfield.Members.Count, Is.EqualTo(1));
            Assert.That(wing.Members.Count, Is.EqualTo(1), "no free slot: stays airborne");
            Assert.That(wing.Members[0].ConsecutiveUnlandedEnds, Is.EqualTo(1));
        }

        [Test]
        public void FreeAirfieldCapacity_IsOneRule_ForStoredAndStandingWings()
        {
            HexCoord hex = new HexCoord(0, 0);
            ArmyData airfield = Airfield(hex, capacity: 3);
            airfield.AddMemberSorted(Plane(1, 1));
            ArmyData wing = Wing(hex, Plane(1, 1));
            Assert.That(AviationRules.FreeAirfieldCapacity(hex, _owner), Is.EqualTo(1));
            Assert.That(AviationRules.FreeAirfieldCapacity(hex, _owner, wing), Is.EqualTo(2),
                "a wing never counts against itself");
            Assert.That(AviationRules.FreeStorageSlots(hex, _owner), Is.EqualTo(2));
            ArmyData other = Wing(hex, Plane(1, 1));
            Assert.That(AviationRules.FreeAirfieldCapacity(hex, _owner), Is.Zero,
                "two standing wings cannot share the last slot");
            Assert.That(AiAirSortiePlanner.FreeLandingCapacity(hex, _owner, other),
                Is.EqualTo(AviationRules.FreeAirfieldCapacity(hex, _owner, other)),
                "planning reads the same rule");
            UnitData stored = Plane(1, 1);
            var lone = Wing(new HexCoord(1, 0), stored);
            Assert.That(ArmyActions.TransferMember(stored, lone, airfield, null, out _), Is.False,
                "an aircraft away from the airfield cannot land into it");
        }

        // ---- B. strike-series calendar ---------------------------------------------------------

        [TestCase(6, 1, 4, 4, 2, TestName = "Helicopter_Move6_Target4_TwoStrikeTurns")]
        [TestCase(5, 2, 4, 4, 3, TestName = "Dreadnought_Move5_Target4_ThreeStrikeTurns")]
        [TestCase(5, 2, 8, 4, 2, TestName = "FarTarget_ReducesStrikeTurns")]
        [TestCase(5, 0, 2, 2, 1, TestName = "ZeroEndurance_StrikesAndLandsSameTurn")]
        [TestCase(5, 0, 3, 3, 0, TestName = "ZeroEndurance_CannotReachAndReturn")]
        [TestCase(6, 1, 8, 8, 0, TestName = "Unreachable_InEndurance")]
        public void StrikeTurns_FollowTheFlightCalendar(int move, int endurance, int outbound,
            int back, int expected)
        {
            Assert.That(AviationRange.StrikeTurns(move, move, endurance, outbound, back),
                Is.EqualTo(expected));
        }

        [Test]
        public void MixedGroup_IsBoundByItsMostLimitedEndurance()
        {
            var group = new List<UnitData>
            {
                new UnitData { IsAviation = true, TurnsWithoutRefuel = 2, MoveMax = 5, MoveCurrent = 5 },
                new UnitData { IsAviation = true, TurnsWithoutRefuel = 0, MoveMax = 5, MoveCurrent = 5 },
            };
            int ends = AviationRange.SafeUnlandedEndsRemaining(group);
            Assert.That(ends, Is.Zero);
            Assert.That(AviationRange.StrikeTurns(5, 5, ends, 2, 2), Is.EqualTo(1));
        }

        [Test]
        public void HoldProof_AllowsTheHelicopterToStayOneNight_ThenForcesItHome()
        {
            HexMap map = Line(8, out GameObject mapObject);
            try
            {
                HexCoord home = new HexCoord(0, 0);
                Airfield(home, capacity: 2);
                UnitData heli = Plane(1, 1);
                heli.TurnsWithoutRefuel = 1;
                heli.MoveMax = heli.MoveCurrent = 6;
                ArmyData wing = Wing(new HexCoord(4, 0), heli);
                heli.MoveCurrent = 2;
                Assert.That(AiAirSortiePlanner.CanEndTurnHereAndRecover(wing, map, _owner), Is.True);
                heli.ConsecutiveUnlandedEnds = 1;
                Assert.That(AiAirSortiePlanner.CanEndTurnHereAndRecover(wing, map, _owner), Is.False,
                    "a second night would cost fuel damage");
                heli.MoveCurrent = 6;
                Assert.That(AiAirSortiePlanner.CanRecover(wing, map, _owner), Is.True,
                    "the second strike turn still lands in time");
            }
            finally
            {
                Object.DestroyImmediate(mapObject);
            }
        }

        // ---- real repeated strike (headless presenter, deterministic dice) ---------------------

        [Test]
        public void RepeatedStrikes_ReallyDamageTheTarget_OncePerAircraftPerTurn()
        {
            var enemy = new PlayerSetupData();
            HexCoord hex = new HexCoord(2, 0);
            var target = new ArmyData { Owner = enemy, Hex = hex, Name = "Target" };
            target.AddMemberSorted(new UnitData { Owner = enemy, Defense = 0, HitPointsMax = 60, HitPointsCurrent = 60 });
            ArmyRegistry.Register(target);
            UnitData plane = Plane(1, 1);
            plane.Attack = 12;
            plane.SortieLaunchPaid = true;
            ArmyData wing = Wing(hex, plane);
            var presenterObject = new GameObject("aviation-cycle-presenter");
            try
            {
                AviationCombatPresenter.HeadlessRng = new System.Random(7);
                var presenter = presenterObject.AddComponent<AviationCombatPresenter>();
                Run(AviationActions.ResolveStationaryStrike(presenter, wing, AirStrikePolicy.Standard));
                int afterFirst = target.Members[0].HitPointsCurrent;
                Assert.That(afterFirst, Is.LessThan(60), "the first strike damages the target");
                Assert.That(AviationActions.CanStrikeAtCurrentHex(wing), Is.False, "one strike per turn");

                plane.HasAirAttackedThisTurn = false; // the owner's next turn (GameTurnController)
                Root().ActionPoints = 0;
                Run(AviationActions.ResolveStationaryStrike(presenter, wing, AirStrikePolicy.Standard));
                Assert.That(target.Members.Count == 0 || target.Members[0].HitPointsCurrent < afterFirst,
                    Is.True, "the repeated strike changes the target again, free of charge");
            }
            finally
            {
                AviationCombatPresenter.HeadlessRng = new System.Random();
                Object.DestroyImmediate(presenterObject);
            }
        }

        // ---- events --------------------------------------------------------------------------

        [Test]
        public void EventGuard_IsNeverAStrikeTarget_ButAnOrdinaryArmyOnTheHexIs()
        {
            var neutral = new PlayerSetupData { IsNeutral = true };
            HexCoord hex = new HexCoord(2, 0);
            var guard = new ArmyData { Owner = neutral, Hex = hex, Name = "Guard" };
            guard.AddMemberSorted(new UnitData { Owner = neutral, HitPointsMax = 3, HitPointsCurrent = 3 });
            ArmyRegistry.Register(guard);
            var roaming = new ArmyData { Owner = neutral, Hex = hex, Name = "Roamers" };
            roaming.AddMemberSorted(new UnitData { Owner = neutral, HitPointsMax = 3, HitPointsCurrent = 3 });
            ArmyRegistry.Register(roaming);
            HexEventRegistry.Set(hex, null, "Guard", null, neutral, null);

            List<ArmyData> targets = AviationCombatPresenter.FindAirStrikeTargetsAt(hex, _owner);
            Assert.That(targets, Does.Not.Contain(guard));
            Assert.That(targets, Does.Contain(roaming));
            Assert.That(HexEventRegistry.FindAt(hex).Triggered, Is.False);
        }

        // ---- estimator -----------------------------------------------------------------------

        [Test]
        public void Estimator_UnknownRoster_IsANoOpNotAnEmptyFight()
        {
            var planes = new List<WorthIt.DefenderProfile> { Profile(attack: 6f) };
            AviationCombatEstimator.AirStrikeEstimate e = AviationCombatEstimator.EstimateAirStrike(
                planes, new List<WorthIt.DefenderProfile>(), AirStrikePolicy.Standard);
            Assert.That(e.ExpectedDamage, Is.Zero);
            Assert.That(e.ExpectedDefendersAfter, Is.Empty);
        }

        [Test]
        public void Estimator_MoreStrikePasses_DealMoreDamage_AndHeroFateMaxProtects()
        {
            var planes = new List<WorthIt.DefenderProfile> { Profile(attack: 6f) };
            var body = new List<WorthIt.DefenderProfile> { Profile(defense: 1f, hp: 30f) };
            float one = AviationCombatEstimator.EstimateAirStrike(planes, body, AirStrikePolicy.Standard, 1).ExpectedDamage;
            float three = AviationCombatEstimator.EstimateAirStrike(planes, body, AirStrikePolicy.Standard, 3).ExpectedDamage;
            Assert.That(three, Is.GreaterThan(one));

            var weakHero = new List<WorthIt.DefenderProfile> { HeroProfile(fateMax: 0) };
            var strongHero = new List<WorthIt.DefenderProfile> { HeroProfile(fateMax: 12) };
            float vsWeak = AviationCombatEstimator.EstimateAirStrike(planes, weakHero, AirStrikePolicy.Standard).ExpectedDamage;
            float vsStrong = AviationCombatEstimator.EstimateAirStrike(planes, strongHero, AirStrikePolicy.Standard).ExpectedDamage;
            Assert.That(vsStrong, Is.LessThan(vsWeak), "a hero defends with its FateMax");
        }

        [Test]
        public void Estimator_CachesExactInputs_AndAnyRosterChangeIsANewKey()
        {
            var planes = new List<WorthIt.DefenderProfile> { Profile(attack: 6f) };
            var body = new List<WorthIt.DefenderProfile> { Profile(defense: 1f, hp: 10f) };
            WorthIt.BeginEstimateCacheScope();
            try
            {
                int hits = AviationCombatEstimator.CacheHits;
                int misses = AviationCombatEstimator.CacheMisses;
                var a = AviationCombatEstimator.EstimateAirStrike(planes, body, AirStrikePolicy.Standard, 2);
                var b = AviationCombatEstimator.EstimateAirStrike(planes, body, AirStrikePolicy.Standard, 2);
                Assert.That(AviationCombatEstimator.CacheMisses, Is.EqualTo(misses + 1));
                Assert.That(AviationCombatEstimator.CacheHits, Is.EqualTo(hits + 1));
                Assert.That(b.ExpectedDamage, Is.EqualTo(a.ExpectedDamage));

                var wounded = new List<WorthIt.DefenderProfile> { Profile(defense: 1f, hp: 4f) };
                AviationCombatEstimator.EstimateAirStrike(planes, wounded, AirStrikePolicy.Standard, 2);
                AviationCombatEstimator.EstimateAirStrike(planes, body, AirStrikePolicy.RaidSupport(1), 2);
                AviationCombatEstimator.EstimateAirStrike(planes, body, AirStrikePolicy.Standard, 3);
                AviationCombatEstimator.EstimateAirStrike(
                    new List<WorthIt.DefenderProfile> { Profile(attack: 7f) }, body, AirStrikePolicy.Standard, 2);
                Assert.That(AviationCombatEstimator.CacheMisses, Is.EqualTo(misses + 5),
                    "HP, policy, passes and attacker stats are all part of the key");
            }
            finally
            {
                WorthIt.EndEstimateCacheScope();
            }
        }

        [Test]
        public void Estimator_IsDeterministic_WithoutAScope()
        {
            var planes = new List<WorthIt.DefenderProfile> { Profile(attack: 5f), Profile(attack: 3f) };
            var body = new List<WorthIt.DefenderProfile> { Profile(defense: 2f, hp: 8f), Profile(defense: 1f, hp: 3f) };
            var a = AviationCombatEstimator.EstimateAirStrike(planes, body, AirStrikePolicy.Standard, 2);
            var b = AviationCombatEstimator.EstimateAirStrike(planes, body, AirStrikePolicy.Standard, 2);
            Assert.That(b.ExpectedDamage, Is.EqualTo(a.ExpectedDamage));
            Assert.That(b.ExpectedKillCount, Is.EqualTo(a.ExpectedKillCount));
        }

        // ---- support options, keys, claims, recon cap ---------------------------------------

        [Test]
        public void SupportOptions_UnknownDefenders_StillOfferTheWingWithoutAFakeEstimate()
        {
            WorldSnapshot snap = SupportSnapshot(defendersKnown: false, out HexCoord target);
            List<AirSupportOption> options = GroundCombatAirSupport.Options(snap,
                System.Array.Empty<WorthIt.DefendingArmy>(), target, AirStrikePolicy.Standard,
                null, 0.4f, null);
            Assert.That(options, Has.Count.EqualTo(1));
            Assert.That(options[0].RosterKnown, Is.False);
            Assert.That(options[0].ExpectedDamage, Is.Zero);
            Assert.That(options[0].WinAfter, Is.EqualTo(0.4f));
            Assert.That(options[0].StrikeTurns, Is.EqualTo(2));
            Assert.That(options[0].Ap, Is.Zero, "a wing on a paid sortie launches for free");
            Assert.That(options[0].RecurringAp, Is.Zero);
        }

        [Test]
        public void SupportOptions_KnownDefenders_RankByTheSeriesWithoutAWinGainGate()
        {
            WorldSnapshot snap = SupportSnapshot(defendersKnown: true, out HexCoord target);
            var opposition = new[] { new WorthIt.DefendingArmy(
                new List<WorthIt.DefenderProfile> { Profile(defense: 1f, hp: 20f) }, default) };
            List<AirSupportOption> options = GroundCombatAirSupport.Options(snap, opposition, target,
                AirStrikePolicy.Standard, _ => 0.1f, 0.1f, null);
            Assert.That(options, Has.Count.EqualTo(1), "no minimum improvement of the ground win");
            Assert.That(options[0].ExpectedDamage, Is.GreaterThan(0f));
        }

        [Test]
        public void ActiveDefenceAirSupport_HasItsOwnKey_AndHoldsOnlyTheWing()
        {
            MissionIntentKey intercept = MissionIntentKey.ForActiveDefence(41);
            MissionIntentKey air = MissionIntentKey.ForActiveDefence(ActiveDefencePhase.AirSupport, 41, null, null);
            Assert.That(air, Is.Not.EqualTo(intercept));
            Assert.That((int)ActiveDefencePhase.Intercept, Is.Zero);
            Assert.That((int)ActiveDefencePhase.Return, Is.EqualTo(1));

            var interceptProposal = new MissionProposal { Kind = MissionKind.ActiveDefence,
                Target = new ActiveDefenceMissionTarget { Phase = ActiveDefencePhase.Intercept, EnemyArmyId = 41 } };
            var airProposal = new MissionProposal { Kind = MissionKind.ActiveDefence,
                Target = new ActiveDefenceMissionTarget { Phase = ActiveDefencePhase.AirSupport, EnemyArmyId = 41,
                    AirSupportArmyId = 9 } };
            Assert.That(StableMissionKey.For(airProposal), Is.Not.EqualTo(StableMissionKey.For(interceptProposal)));
            Assert.That(MissionIntentKey.For(airProposal), Is.EqualTo(air));

            var intent = new MissionIntent { Objective = new ActiveDefenceIntent
                { Phase = ActiveDefencePhase.AirSupport, EnemyArmyId = 41, AirSupportArmyId = 9 } };
            Assert.That(GroundCombatLegs.HeldAirSupportArmyId(intent), Is.EqualTo(9));
            Assert.That(intent.PreferredMoverArmyId, Is.Null, "the wing is never the ground actor");
            var ground = new MissionIntent { Objective = new ActiveDefenceIntent
                { Phase = ActiveDefencePhase.Intercept, EnemyArmyId = 41, PrimaryArmyId = 3 } };
            Assert.That(GroundCombatLegs.HeldAirSupportArmyId(ground), Is.Null);
            Assert.That(GroundCombatLegs.JoinsAssignmentSolve(airProposal), Is.False,
                "the wing never enters the ground assault solve");
        }

        [Test]
        public void AirRecon_HasOneActorPerTurn()
        {
            Assert.That(ReconAirCapacityPolicy.MaxAirReconActorsPerTurn, Is.EqualTo(1));
        }

        // ---- helpers --------------------------------------------------------------------------

        private static void Run(System.Collections.IEnumerator routine)
        {
            var stack = new Stack<System.Collections.IEnumerator>();
            stack.Push(routine);
            int guard = 10000;
            while (stack.Count > 0 && guard-- > 0)
            {
                System.Collections.IEnumerator top = stack.Peek();
                if (!top.MoveNext()) { stack.Pop(); continue; }
                if (top.Current is System.Collections.IEnumerator nested)
                    stack.Push(nested);
            }
        }

        private UnitData Plane(int ap, int energy) => new UnitData
        {
            Owner = _owner, IsAviation = true, ActivationApCost = ap, LaunchEnergyCost = energy,
            MoveMax = 6, MoveCurrent = 6, HitPointsMax = 4, HitPointsCurrent = 4, Attack = 3,
        };

        private ArmyData Wing(HexCoord hex, params UnitData[] planes)
        {
            var wing = new ArmyData { Owner = _owner, Hex = hex, IsAirArmy = true, Name = "Wing" };
            foreach (UnitData p in planes)
                wing.AddMemberSorted(p);
            ArmyRegistry.Register(wing);
            return wing;
        }

        private ArmyData Airfield(HexCoord hex, int capacity)
        {
            BuildingRegistry.Register(hex, new BuildingData
            {
                Owner = _owner, Hex = hex, IsBase = true, IsStartingCitadel = true, AirfieldCapacity = capacity,
            });
            var airfield = new ArmyData { Owner = _owner, Hex = hex, IsAirfield = true, Name = "Airfield" };
            ArmyRegistry.Register(airfield);
            return airfield;
        }

        private static HexMap Line(int length, out GameObject mapObject)
        {
            mapObject = new GameObject("aviation-cycle-map");
            HexMap map = mapObject.AddComponent<HexMap>();
            var terrain = new Game.Terrain.TerrainTypeEntry { moveCost = 1 };
            var hexes = new Dictionary<HexCoord, Game.Terrain.TerrainTypeEntry>();
            for (int q = 0; q <= length; q++)
                hexes[new HexCoord(q, 0)] = terrain;
            map.SetData(length, 1f, hexes);
            return map;
        }

        private static WorthIt.DefenderProfile Profile(float attack = 0f, float defense = 0f, float hp = 4f) =>
            new WorthIt.DefenderProfile(defense, false, attack: attack, hitPoints: hp, maxHitPoints: hp);

        private static WorthIt.DefenderProfile HeroProfile(int fateMax) =>
            new WorthIt.DefenderProfile(0f, false, hitPoints: 12f, maxHitPoints: 12f,
                isGroundCombatant: false, isHero: true, fateMax: fateMax);

        private WorldSnapshot SupportSnapshot(bool defendersKnown, out HexCoord target)
        {
            target = new HexCoord(4, 0);
            var member = new RaidRecoveryMemberSnapshot(1, 0, false, true, false, false, 1,
                Profile(attack: 6f), Profile(attack: 6f), false, ResourceVector.Zero);
            var wing = new ArmySnapshot
            {
                ArmyId = 5, Owner = _owner, Hex = new HexCoord(0, 0), IsAir = true, MemberCount = 1,
                CurrentMovement = 6, MaxMovement = 6, SafeUnlandedEndsRemaining = 1,
                PendingActivationApCost = 0, PendingActivationEnergyCost = 0,
                RecoveryMembers = new[] { member },
            };
            return new WorldSnapshot
            {
                Observer = _owner,
                Self = new SelfSnapshot { Armies = new[] { wing }, BaseHexes = new[] { new HexCoord(0, 0) } },
            };
        }
    }
}
#endif
