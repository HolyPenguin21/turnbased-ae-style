#if UNITY_INCLUDE_TESTS
using System.Collections.Generic;
using System.Linq;
using Game.Ai;
using Game.Ai.V2;
using Game.Aviation;
using Game.Combat;
using Game.Cards;
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

        [Test]
        public void ProfileSnapshot_DetachesAndProtectsAbilityAndTypeLists()
        {
            var abilities = new List<string> { UnitAbilities.Berserk };
            var tags = new List<UnitTypeTag> { default };
            var profile = new WorthIt.DefenderProfile(2f, false, tags, attack: 3f,
                hitPoints: 40f, abilities: abilities);
            abilities.Clear();
            tags.Clear();
            Assert.That(profile.Abilities, Does.Contain(UnitAbilities.Berserk));
            Assert.That(profile.TypeTags.Count, Is.EqualTo(1));
            Assert.That(((IList<string>)profile.Abilities).IsReadOnly, Is.True);
            Assert.That(((IList<UnitTypeTag>)profile.TypeTags).IsReadOnly, Is.True);
        }

        [Test]
        public void Estimator_CachedResultCannotBeMutatedThroughReadOnlyInterfaces()
        {
            var planes = new[] { Profile(attack: 8f) };
            var targets = new[] { new AviationCombatEstimator.DefendingAirArmy(1,
                new[] { Profile(hp: 100f) }, new[] { 0 }) };
            WorthIt.BeginEstimateCacheScope();
            try
            {
                var estimate = AviationCombatEstimator.EstimateAirStrikeAgainstArmies(
                    planes, targets, AirStrikePolicy.Standard);
                Assert.That(estimate.ExpectedDefendersAfter.Count, Is.EqualTo(1));
                Assert.That(((IList<WorthIt.DefenderProfile>)estimate.ExpectedDefendersAfter).IsReadOnly, Is.True);
                Assert.That(((IList<int>)estimate.SurvivorSourceIndices).IsReadOnly, Is.True);
                int hits = AviationCombatEstimator.CacheHits;
                var hit = AviationCombatEstimator.EstimateAirStrikeAgainstArmies(
                    planes, targets, AirStrikePolicy.Standard);
                Assert.That(AviationCombatEstimator.CacheHits, Is.EqualTo(hits + 1));
                Assert.That(hit.ExpectedDefendersAfter[0].HitPoints,
                    Is.EqualTo(estimate.ExpectedDefendersAfter[0].HitPoints));
            }
            finally { WorthIt.EndEstimateCacheScope(); }
        }

        [Test]
        public void AirCache_InputChangesMatchColdResults_AndNewScopeStartsEmpty()
        {
            var planes = new[] { Profile(attack: 8f) };
            WorthIt.DefenderProfile Target(float hp = 100f, float attack = 2f, float defense = 2f,
                int initiative = 1, bool ground = true, bool summoned = false,
                bool hero = false, int fateMax = 0, string ability = null, bool ceramic = false,
                float maxHp = 120f) => new WorthIt.DefenderProfile(defense, ceramic,
                    attack: attack, hitPoints: hp, initiative: initiative,
                    abilities: ability == null ? null : new[] { ability }, maxHitPoints: maxHp,
                    isGroundCombatant: ground, isHero: hero, fateMax: fateMax, isSummoned: summoned);
            var profiles = new[] { Target(), Target(hp: 80f), Target(attack: 9f), Target(defense: 5f),
                Target(initiative: 4), Target(ground: false), Target(summoned: true),
                Target(hero: true, fateMax: 3), Target(ability: UnitAbilities.Berserk),
                Target(ceramic: true), Target(maxHp: 160f) };
            var cached = new List<AviationCombatEstimator.AirStrikeEstimate>();
            WorthIt.BeginEstimateCacheScope();
            try
            {
                int misses = AviationCombatEstimator.CacheMisses;
                int hits = AviationCombatEstimator.CacheHits;
                foreach (var profile in profiles)
                {
                    var armies = new[] { new AviationCombatEstimator.DefendingAirArmy(1,
                        new[] { profile }, new[] { 2 }) };
                    cached.Add(AviationCombatEstimator.EstimateAirStrikeAgainstArmies(
                        planes, armies, AirStrikePolicy.Standard, 2));
                    AviationCombatEstimator.EstimateAirStrikeAgainstArmies(planes, armies, AirStrikePolicy.Standard, 2);
                }
                Assert.That(AviationCombatEstimator.CacheMisses, Is.EqualTo(misses + profiles.Length));
                Assert.That(AviationCombatEstimator.CacheHits, Is.EqualTo(hits + profiles.Length));
            }
            finally { WorthIt.EndEstimateCacheScope(); }
            for (int i = 0; i < profiles.Length; i++)
            {
                var cold = AviationCombatEstimator.EstimateAirStrikeAgainstArmies(planes,
                    new[] { new AviationCombatEstimator.DefendingAirArmy(1, new[] { profiles[i] }, new[] { 2 }) },
                    AirStrikePolicy.Standard, 2);
                Assert.That(cold.ExpectedDamage, Is.EqualTo(cached[i].ExpectedDamage));
                Assert.That(AviationCombatEstimator.BuildKey(planes, cold.ExpectedDefendersAfter,
                    AirStrikePolicy.Standard, 2, 0, AbilityMagnitudes.Default),
                    Is.EqualTo(AviationCombatEstimator.BuildKey(planes, cached[i].ExpectedDefendersAfter,
                        AirStrikePolicy.Standard, 2, 0, AbilityMagnitudes.Default)));
                Assert.That(cold.SurvivorSourceIndices, Is.EqualTo(cached[i].SurvivorSourceIndices));
                Assert.That(cold.ExpectedCurrentFatesAfter, Is.EqualTo(cached[i].ExpectedCurrentFatesAfter));
            }
            WorthIt.BeginEstimateCacheScope();
            try
            {
                int misses = AviationCombatEstimator.CacheMisses;
                AviationCombatEstimator.EstimateAirStrikeAgainstArmies(planes,
                    new[] { new AviationCombatEstimator.DefendingAirArmy(1, new[] { profiles[0] }, new[] { 2 }) },
                    AirStrikePolicy.Standard, 2);
                Assert.That(AviationCombatEstimator.CacheMisses, Is.EqualTo(misses + 1));
            }
            finally { WorthIt.EndEstimateCacheScope(); }
        }

        [Test]
        public void AirProjection_SpentFateStaysSpentInGroundCommander()
        {
            var body = Profile(hp: 100f);
            var roster = new AviationCombatEstimator.DefendingAirArmy(7,
                new[] { HeroProfile(4), body }, new[] { 0, 0 });
            var estimate = AviationCombatEstimator.EstimateAirStrikeAgainstArmies(
                new[] { Profile(attack: 0f) }, new[] { roster }, AirStrikePolicy.Standard);
            var opposition = new[] { new WorthIt.DefendingArmy(new[] { body },
                new WorthIt.SideCommander(1, 4), 0f, 7) };
            var targets = new[] { new AiMapMemory.KnownAirSighting(default, null, 1, true, roster) };
            var after = GroundCombatAirSupport.AfterAirStrike(opposition, targets, estimate);
            Assert.That(after.Single().Commander.Present, Is.True);
            Assert.That(after.Single().Commander.Fate, Is.Zero,
                "a surviving hero does not recover spent Fate between air and ground combat");
            Assert.That(estimate.ExpectedCurrentFatesAfter[0], Is.Zero);
            Assert.That(((IList<int>)estimate.ExpectedCurrentFatesAfter).IsReadOnly, Is.True);
        }

        [Test]
        public void LiveCommander_UsesCurrentFateAfterStandaloneStrike()
        {
            var hero = new UnitData { IsHero = true, Initiative = 2, FateMax = 4, Fate = 1 };
            Assert.That(WorthIt.SideCommander.Of(hero).Fate, Is.EqualTo(1));
        }

        [Test]
        public void GroundObservation_DetachesRosterFromProducer()
        {
            var profiles = new List<WorthIt.DefenderProfile> { Profile(hp: 40f) };
            var observation = new AiMapMemory.KnownEnemySighting(default, null, "seen", 1, 1f, 1f, profiles);
            var guard = new AiMapMemory.GuardStrength(1f, 1f, profiles);
            profiles.Clear();
            Assert.That(observation.Defenders.Count, Is.EqualTo(1));
            Assert.That(guard.Defenders.Count, Is.EqualTo(1));
            Assert.That(((IList<WorthIt.DefenderProfile>)observation.Defenders).IsReadOnly, Is.True);
            Assert.That(((IList<WorthIt.DefenderProfile>)guard.Defenders).IsReadOnly, Is.True);
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

        [Test]
        public void AviationCardPrice_HasNoImaginaryRecurringFlightCharge()
        {
            var card = new CardData(new CardDefinition { isAviation = true });
            var candidate = StrategicCardEvaluator.ScoreNonCombat(NonCombatRole.Aviation,
                card, null, null, null, 0f, witnessedUsefulApDemand: 100f,
                actualApCost: 2f, actualResourceCost: new ResourceCost());
            Assert.That(candidate.Breakdown.ResourceEfficiency,
                Is.EqualTo(-ActionPrice.ToCardScore(ActionPrice.Ap(2f))));
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
        public void HumanEndOfTurn_RefuelsWithoutChangingWingMembership()
        {
            _owner.IsHuman = true;
            var hex = new HexCoord(0, 0);
            ArmyData airfield = Airfield(hex, capacity: 2);
            UnitData plane = Plane(1, 1);
            plane.ConsecutiveUnlandedEnds = 2;
            plane.HasEmergencyFlightPenalty = true;
            plane.SortieLaunchPaid = true;
            plane.HasAirAttackedThisTurn = true;
            plane.HitPointsCurrent = 1;
            ArmyData wing = Wing(hex, plane);
            AviationTurnLifecycle.ResolveEndOfTurn(_owner, null);
            Assert.That(wing.Members, Does.Contain(plane));
            Assert.That(airfield.Members, Is.Empty);
            Assert.That(plane.ConsecutiveUnlandedEnds, Is.Zero);
            Assert.That(plane.HasEmergencyFlightPenalty, Is.False);
            Assert.That(plane.SortieLaunchPaid, Is.False);
            Assert.That(plane.HasAirAttackedThisTurn, Is.True);
            Assert.That(plane.HitPointsCurrent, Is.EqualTo(1));
        }

        [Test]
        public void HumanEndOfTurn_MultipleWingsCannotRefuelBeyondCapacity()
        {
            _owner.IsHuman = true;
            var hex = new HexCoord(0, 0);
            ArmyData airfield = Airfield(hex, capacity: 1);
            UnitData first = Plane(1, 1), second = Plane(1, 1);
            ArmyData a = Wing(hex, first), b = Wing(hex, second);
            AviationTurnLifecycle.ResolveEndOfTurn(_owner, null);
            Assert.That(a.Members.Count + b.Members.Count, Is.EqualTo(2));
            Assert.That(airfield.Members, Is.Empty);
            Assert.That(first.ConsecutiveUnlandedEnds + second.ConsecutiveUnlandedEnds, Is.EqualTo(1));
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

        [Test]
        public void InFlightReservation_ProtectsDomainCapacity_AndArrivalCountsOnce()
        {
            HexCoord home = new HexCoord(0, 0);
            ArmyData airfield = Airfield(home, capacity: 3);
            airfield.AddMemberSorted(Plane(1, 1));
            ArmyData returning = Wing(new HexCoord(2, 0), Plane(1, 1));
            var reservation = new AirSortie { Army = returning, LandingHex = home };
            AirSortieRegistry.Add(_owner, reservation);
            AirSortieRegistry.Add(_owner, reservation); // duplicate records cannot reserve twice
            Assert.That(AviationRules.FreeAirfieldCapacity(home, _owner), Is.EqualTo(1));
            Assert.That(AviationRules.FreeAirfieldCapacity(home, _owner, returning), Is.EqualTo(2));
            ArmyData standing = Wing(home, Plane(1, 1));
            Assert.That(AviationRules.FreeAirfieldCapacity(home, _owner), Is.Zero);
            Assert.That(AviationActions.LandInSlotOrder(standing, null), Is.EqualTo(1));
            Assert.That(airfield.Members.Count, Is.EqualTo(2));
            Assert.That(AviationRules.FreeStorageSlots(home, _owner), Is.Zero,
                "the remaining slot belongs to the returning wing");
            ArmyRegistry.MoveArmy(returning, home);
            Assert.That(AviationRules.FreeAirfieldCapacity(home, _owner), Is.Zero,
                "arrival replaces the in-flight claim with the standing wing");
            Assert.That(AviationActions.LandInSlotOrder(returning, null), Is.EqualTo(1));
            Assert.That(airfield.Members.Count, Is.EqualTo(3));
        }

        [Test]
        public void ReservedLastSlot_RejectsTransfer_AndRetargetingReleasesIt()
        {
            HexCoord home = new HexCoord(0, 0);
            ArmyData airfield = Airfield(home, capacity: 1);
            ArmyData returning = Wing(new HexCoord(2, 0), Plane(1, 1));
            var reservation = new AirSortie { Army = returning, LandingHex = home };
            AirSortieRegistry.Add(_owner, reservation);
            UnitData other = Plane(1, 1);
            ArmyData standing = Wing(home, other);
            Assert.That(ArmyActions.TransferMember(other, standing, airfield, null, out _), Is.False);
            Assert.That(standing.Members, Does.Contain(other));
            Assert.That(airfield.Members, Is.Empty);
            reservation.LandingHex = new HexCoord(4, 0);
            Assert.That(ArmyActions.TransferMember(other, standing, airfield, null, out string why),
                Is.True, why);
            Assert.That(airfield.Members, Does.Contain(other));
        }

        [Test]
        public void InvalidReservation_DoesNotHoldSlots_AndClearReleasesClaims()
        {
            HexCoord home = new HexCoord(0, 0);
            Airfield(home, capacity: 2);
            ArmyData wing = Wing(new HexCoord(1, 0), Plane(1, 1));
            AirSortieRegistry.Add(_owner, new AirSortie { Army = wing, LandingHex = home });
            Assert.That(AviationRules.FreeAirfieldCapacity(home, _owner), Is.EqualTo(1));
            wing.Members.Clear();
            Assert.That(AviationRules.FreeAirfieldCapacity(home, _owner), Is.EqualTo(2));
            wing.AddMemberSorted(Plane(1, 1));
            AirSortieRegistry.Clear();
            Assert.That(AviationRules.FreeAirfieldCapacity(home, _owner), Is.EqualTo(2));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void RejectedUnloadAndBoard_ChangesNeitherRosterNorActivationNorBank(bool paidGarrison)
        {
            HexCoord home = new HexCoord(0, 0);
            ArmyData airfield = Airfield(home, capacity: 8);
            var ground = new ArmyData { Owner = _owner, Hex = home };
            var unit = new UnitData { Owner = _owner, ActivationApCost = 1 };
            ground.AddMemberSorted(unit);
            var garrison = new ArmyData { Owner = _owner, Hex = home, IsGarrison = true };
            if (paidGarrison) garrison.MarkActivated();
            var aircraft = Enumerable.Range(0, paidGarrison ? 1 : ArmyData.ComputeCapacity(System.Array.Empty<UnitData>(), false) + 1)
                .Select(_ => Plane(1, 1)).ToList();
            foreach (var plane in aircraft) airfield.AddMemberSorted(plane);
            Root().ActionPoints = 10;
            Assert.That(ArmyActions.TryUnloadAndBoardAircraft(ground, garrison, airfield,
                aircraft, null, out _), Is.False);
            Assert.That(ground.Members, Is.EqualTo(new[] { unit }));
            Assert.That(garrison.Members, Is.Empty);
            Assert.That(airfield.Members, Is.EqualTo(aircraft));
            Assert.That(garrison.RequiresActivationCharge(unit), Is.EqualTo(paidGarrison));
            Assert.That(_root.ActionPoints, Is.EqualTo(10));
            Assert.That(ground.IsAirArmy, Is.False);
        }

        [Test]
        public void UnloadAndBoard_CommitsBothRosters_WithoutRestoringAircraftState()
        {
            HexCoord home = new HexCoord(0, 0);
            ArmyData airfield = Airfield(home, capacity: 2);
            UnitData plane = Plane(2, 3);
            plane.HitPointsCurrent = 1;
            plane.MoveCurrent = 1;
            plane.HasAirAttackedThisTurn = true;
            airfield.AddMemberSorted(plane);
            var ground = new ArmyData { Owner = _owner, Hex = home };
            var unit = new UnitData { Owner = _owner };
            ground.AddMemberSorted(unit);
            var garrison = new ArmyData { Owner = _owner, Hex = home, IsGarrison = true };
            Assert.That(ArmyActions.TryUnloadAndBoardAircraft(ground, garrison, airfield,
                new[] { plane }, null, out string why), Is.True, why);
            Assert.That(garrison.Members, Is.EqualTo(new[] { unit }));
            Assert.That(ground.Members, Is.EqualTo(new[] { plane }));
            Assert.That(airfield.Members, Is.Empty);
            Assert.That(ground.IsAirArmy, Is.True);
            Assert.That(ground.PendingActivationApCost, Is.EqualTo(2));
            Assert.That(ground.PendingActivationEnergyCost, Is.EqualTo(3));
            Assert.That(plane.HitPointsCurrent, Is.EqualTo(1));
            Assert.That(plane.MoveCurrent, Is.EqualTo(1));
            Assert.That(plane.HasAirAttackedThisTurn, Is.True);
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

        [Test]
        public void HeadlessStrike_KeepsUnspentHeroFate()
        {
            var enemy = new PlayerSetupData();
            HexCoord hex = new HexCoord(2, 0);
            var target = new ArmyData { Owner = enemy, Hex = hex };
            var hero = new UnitData { Owner = enemy, IsHero = true, FateMax = 3, Fate = 3,
                HitPointsMax = 100, HitPointsCurrent = 100 };
            target.AddMemberSorted(hero);
            ArmyRegistry.Register(target);
            UnitData plane = Plane(1, 1);
            plane.Attack = 0;
            plane.SortieLaunchPaid = true;
            ArmyData wing = Wing(hex, plane);
            var presenterObject = new GameObject("aviation-fate-presenter");
            try
            {
                var presenter = presenterObject.AddComponent<AviationCombatPresenter>();
                Run(AviationActions.ResolveStationaryStrike(presenter, wing, AirStrikePolicy.Standard));
                Assert.That(plane.HasAirAttackedThisTurn, Is.True, "the real strike was resolved");
                Assert.That(hero.Fate, Is.EqualTo(3), "no incoming damage: the defender has no reason to spend Fate");
            }
            finally { Object.DestroyImmediate(presenterObject); }
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
            Assert.That(targets, Has.No.Member(guard));
            Assert.That(targets, Has.Member(roaming));
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

        [TestCase(false)]
        [TestCase(true)]
        public void Estimator_ProjectsBerserkStats_AndHeroesKeepTheirFateMaxPool(bool hero)
        {
            var attackers = new[] { Profile(attack: 40f) };
            var defender = new WorthIt.DefenderProfile(8f, false, attack: 2f,
                hitPoints: 1000f, maxHitPoints: 1000f,
                abilities: new[] { UnitAbilities.Berserk }, isHero: hero, fateMax: 12);
            var estimate = AviationCombatEstimator.EstimateAirStrike(attackers,
                new[] { defender }, AirStrikePolicy.Standard, 3);
            Assert.That(estimate.ExpectedDefendersAfter.Count, Is.EqualTo(1));
            var after = estimate.ExpectedDefendersAfter[0];
            Assert.That(after.HitPoints, Is.LessThan(defender.HitPoints));
            Assert.That(after.Attack, Is.GreaterThan(defender.Attack));
            if (hero) Assert.That(after.Defense, Is.EqualTo(12f));
            else Assert.That(after.Defense, Is.LessThan(defender.Defense));
            Assert.That(estimate.ExpectedAttackAfter, Is.EqualTo(after.Attack));
            Assert.That(estimate.ExpectedDefenseAfter, Is.EqualTo(after.Defense));
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

        [Test]
        public void GroupedStrike_ExactPolicyIgnoresOtherArmies()
        {
            var planes = new[] { Profile(attack: 8f) };
            var target = new AviationCombatEstimator.DefendingAirArmy(7,
                new[] { Profile(hp: 20f), HeroProfile(3) }, new[] { 0, 2 });
            var other = new AviationCombatEstimator.DefendingAirArmy(8,
                new[] { Profile(hp: 1000f) }, new[] { 0 });
            var alone = AviationCombatEstimator.EstimateAirStrikeAgainstArmies(planes,
                new[] { target }, AirStrikePolicy.RaidSupport(7), 3);
            var alongside = AviationCombatEstimator.EstimateAirStrikeAgainstArmies(planes,
                new[] { other, target }, AirStrikePolicy.RaidSupport(7), 3);
            Assert.That(alongside.ExpectedDamage, Is.EqualTo(alone.ExpectedDamage));
            Assert.That(alongside.SurvivorSourceIndices, Is.EquivalentTo(alone.SurvivorSourceIndices));
            Assert.That(alongside.ExpectedDefendersAfter.Count, Is.LessThanOrEqualTo(2));
        }

        [Test]
        public void GroupedStrike_CurrentFateAndArmyPartitionInvalidateCache()
        {
            var planes = new[] { Profile(attack: 7f) };
            var hero = HeroProfile(4);
            WorthIt.BeginEstimateCacheScope();
            try
            {
                int misses = AviationCombatEstimator.CacheMisses;
                int hits = AviationCombatEstimator.CacheHits;
                var together = new AviationCombatEstimator.DefendingAirArmy(1,
                    new[] { hero, Profile(hp: 20f) }, new[] { 3, 0 });
                AviationCombatEstimator.EstimateAirStrikeAgainstArmies(planes, new[] { together }, AirStrikePolicy.Standard);
                AviationCombatEstimator.EstimateAirStrikeAgainstArmies(planes, new[] { together }, AirStrikePolicy.Standard);
                AviationCombatEstimator.EstimateAirStrikeAgainstArmies(planes, new[] {
                    new AviationCombatEstimator.DefendingAirArmy(1, together.Units, new[] { 0, 0 }) }, AirStrikePolicy.Standard);
                AviationCombatEstimator.EstimateAirStrikeAgainstArmies(planes, new[] {
                    new AviationCombatEstimator.DefendingAirArmy(1, new[] { hero }, new[] { 3 }),
                    new AviationCombatEstimator.DefendingAirArmy(2, new[] { Profile(hp: 20f) }, new[] { 0 }) }, AirStrikePolicy.Standard);
                Assert.That(AviationCombatEstimator.CacheMisses, Is.EqualTo(misses + 3));
                Assert.That(AviationCombatEstimator.CacheHits, Is.EqualTo(hits + 1));
            }
            finally { WorthIt.EndEstimateCacheScope(); }
        }

        [Test]
        public void GroupedStrike_CommanderFateDoesNotProtectAnotherArmy()
        {
            var planes = new[] { Profile(attack: 6f) };
            var hero = HeroProfile(8);
            var body = Profile(defense: 8f, hp: 1000f);
            var together = new[] { new AviationCombatEstimator.DefendingAirArmy(1,
                new[] { hero, body }, new[] { 10000, 0 }) };
            var separate = new[] {
                new AviationCombatEstimator.DefendingAirArmy(1, new[] { hero }, new[] { 10000 }),
                new AviationCombatEstimator.DefendingAirArmy(2, new[] { body }, new[] { 0 }) };
            float protectedDamage = AviationCombatEstimator.EstimateAirStrikeAgainstArmies(planes, together,
                AirStrikePolicy.Standard, 5).ExpectedDamage;
            float separateDamage = AviationCombatEstimator.EstimateAirStrikeAgainstArmies(planes, separate,
                AirStrikePolicy.Standard, 5).ExpectedDamage;
            Assert.That(separateDamage, Is.GreaterThan(protectedDamage),
                "the other army's body must not borrow the hero's current Fate");
        }

        [Test]
        public void GroupedStrike_SpentAircraftSkipOnlyFirstPass()
        {
            var planes = new[] { Profile(attack: 8f) };
            var targets = new[] { new AviationCombatEstimator.DefendingAirArmy(1,
                new[] { Profile(hp: 100f) }, new[] { 0 }) };
            var spent = new[] { true };
            Assert.That(AviationCombatEstimator.EstimateAirStrikeAgainstArmies(planes, targets,
                AirStrikePolicy.Standard, 1, spent).ExpectedDamage, Is.Zero);
            Assert.That(AviationCombatEstimator.EstimateAirStrikeAgainstArmies(planes, targets,
                AirStrikePolicy.Standard, 2, spent).ExpectedDamage, Is.GreaterThan(0f));
        }

        [Test]
        public void AirObservation_VisibleRosterFlowsThroughSnapshotToSupport_WithoutHiddenHero()
        {
            var observer = new PlayerSetupData();
            var enemy = new PlayerSetupData();
            var body = new UnitData { Owner = enemy, Attack = 2, Defense = 1,
                HitPointsCurrent = 30, HitPointsMax = 60 };
            var hero = new UnitData { Owner = enemy, IsHero = true, Fate = 3, FateMax = 4,
                HitPointsCurrent = 12, HitPointsMax = 12 };
            var hidden = new UnitData { Owner = enemy, IsHero = true, IsHidden = true,
                Fate = 99, FateMax = 99 };
            WorldSnapshot snap = SupportSnapshot(false, out HexCoord target);
            snap.Observer = observer;
            var army = new ArmyData { Owner = enemy, Hex = target, Name = "observed" };
            army.Members.AddRange(new[] { body, hero, hidden });
            const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.NonPublic
                | System.Reflection.BindingFlags.Static;
            var armies = (Dictionary<HexCoord, List<ArmyData>>)typeof(ArmyRegistry)
                .GetField("ByHex", flags).GetValue(null);
            var visible = (Dictionary<PlayerSetupData, HashSet<HexCoord>>)typeof(VisionSystem)
                .GetField("Visible", flags).GetValue(null);
            AiMapMemory.Clear();
            armies[target] = new List<ArmyData> { army };
            visible[observer] = new HashSet<HexCoord> { target };
            try
            {
                AiMapMemory.RefreshVisibleForTest(observer);
                var buildKnown = typeof(WorldAnalysis).GetMethod("BuildKnown", flags);
                snap.Known = (KnownSnapshot)buildKnown.Invoke(null,
                    new object[] { observer, System.Array.Empty<HexCoord>() });
                var observed = snap.Known.AirSightings.Single();
                Assert.That(observed.Roster.Units.Count, Is.EqualTo(2));
                Assert.That(observed.Roster.Units.Count(u => u.IsHero), Is.EqualTo(1));
                Assert.That(observed.Roster.CurrentFates[1], Is.EqualTo(3));
                Assert.That(snap.Known.EnemySightings.Single().Defenders.Count, Is.EqualTo(1));
                Assert.That(snap.Known.EnemySightings.Single().Defenders[0].MaxHitPoints, Is.EqualTo(60f));
                var opposition = new[] { new WorthIt.DefendingArmy(new[] { WorthIt.FromLiveUnit(body) },
                    WorthIt.SideCommander.Of(hero), 2f, army.Id) };
                var options = GroundCombatAirSupport.Options(snap, opposition, target,
                    AirStrikePolicy.RaidSupport(army.Id), null, 0.4f, null);
                Assert.That(options.Count, Is.EqualTo(1));
                Assert.That(options[0].RosterKnown, Is.True);
                Assert.That(options[0].ExpectedDamage, Is.GreaterThan(0f));
                int knowledge = AiMapMemory.KnowledgeVersionFor(observer);
                long routes = AiMapMemory.RouteMemoryVersionFor(observer);
                hero.Fate = 0;
                AiMapMemory.RefreshVisibleForTest(observer);
                Assert.That(AiMapMemory.KnowledgeVersionFor(observer), Is.GreaterThan(knowledge));
                Assert.That(AiMapMemory.RouteMemoryVersionFor(observer), Is.EqualTo(routes));
                Assert.That(observed.Roster.CurrentFates[1], Is.EqualTo(3), "old snapshot stays immutable");
                Assert.That(AiMapMemory.AllKnownAirSightings(observer).Single().Roster.CurrentFates[1], Is.Zero);
                visible[observer].Clear();
                AiMapMemory.RefreshVisibleForTest(observer);
                Assert.That(AiMapMemory.AllKnownAirSightings(observer).Count(), Is.EqualTo(1),
                    "fog does not erase the last observation");
                armies.Remove(target);
                visible[observer].Add(target);
                AiMapMemory.RefreshVisibleForTest(observer);
                Assert.That(AiMapMemory.AllKnownAirSightings(observer), Is.Empty);
            }
            finally
            {
                armies.Remove(target);
                visible.Remove(observer);
                AiMapMemory.Clear();
            }
        }

        [Test]
        public void AirObservation_CopiesFateAndDetectsFateOnlyChanges()
        {
            var profiles = new[] { HeroProfile(4), Profile() };
            var fates = new[] { 3, 99 };
            var roster = new AviationCombatEstimator.DefendingAirArmy(7, profiles, fates);
            var before = new AiMapMemory.KnownAirSighting(default, _owner, 1, true, roster);
            fates[0] = 0;
            profiles[0] = HeroProfile(9);
            Assert.That(roster.CurrentFates[0], Is.EqualTo(3));
            Assert.That(roster.CurrentFates[1], Is.Zero);
            Assert.That(roster.Units[0].FateMax, Is.EqualTo(4));
            var after = new AiMapMemory.KnownAirSighting(default, _owner, 1, true,
                new AviationCombatEstimator.DefendingAirArmy(7, roster.Units, fates));
            Assert.That(AiMapMemory.SameAirSighting(before, after), Is.False);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void AfterAirStrike_PreservesUnstruckArmiesAndGuard_AndTransfersCommander(bool commanderSurvives)
        {
            var body = Profile(hp: 10f);
            var firstHero = HeroProfile(4);
            var secondHero = HeroProfile(2);
            var commander = new WorthIt.SideCommander(0, 4);
            var opposition = new[] {
                new WorthIt.DefendingArmy(new[] { body }, commander, 2f, 7),
                new WorthIt.DefendingArmy(new[] { body }, commander, 0f, 8),
                new WorthIt.DefendingArmy(new[] { body }, default, 5f) };
            var target = new AiMapMemory.KnownAirSighting(default, _owner, 1, true,
                new AviationCombatEstimator.DefendingAirArmy(7,
                    new[] { body, firstHero, secondHero }, new[] { 0, 3, 1 }, 1));
            var indices = commanderSurvives ? new[] { 0, 1, 2 } : new[] { 0, 2 };
            var estimate = new AviationCombatEstimator.AirStrikeEstimate(0, 0,
                indices.Select(i => target.Roster.Units[i]).ToList(), 0, survivorSourceIndices: indices);
            var after = GroundCombatAirSupport.AfterAirStrike(opposition, new[] { target }, estimate);
            Assert.That(after.Count, Is.EqualTo(3));
            Assert.That(after[0].Commander.Fate, Is.EqualTo(commanderSurvives ? 3 : 1));
            Assert.That(after[0].DefenseBonusOverride, Is.EqualTo(2f));
            Assert.That(after[0].ArmyId, Is.EqualTo(7));
            Assert.That(after[1].Equals(opposition[1]), Is.True);
            Assert.That(after[2].Equals(opposition[2]), Is.True);
        }

        [Test]
        public void AirObservation_FateOnlyChangeInvalidatesContact()
        {
            var hero = HeroProfile(4);
            WorldSnapshot Snapshot(int fate) => new WorldSnapshot { Known = new KnownSnapshot {
                AirSightings = new[] { new AiMapMemory.KnownAirSighting(default, _owner, 1, true,
                    new AviationCombatEstimator.DefendingAirArmy(7, new[] { hero }, new[] { fate })) } } };
            var method = typeof(WorldAnalysis).GetMethod("ChangedContactIds",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            Assert.That(method, Is.Not.Null);
            var changed = (HashSet<int>)method.Invoke(null, new object[] { Snapshot(3), Snapshot(0) });
            Assert.That(changed, Does.Contain(7));
            var unchanged = (HashSet<int>)method.Invoke(null, new object[] { Snapshot(3), Snapshot(3) });
            Assert.That(unchanged, Is.Empty);
        }

        [Test]
        public void AfterAirStrike_DeadOnlyHeroRemovesCommander()
        {
            var body = Profile();
            var opposition = new[] { new WorthIt.DefendingArmy(new[] { body },
                new WorthIt.SideCommander(3, 4), 0f, 7) };
            var target = new AiMapMemory.KnownAirSighting(default, _owner, 1, true,
                new AviationCombatEstimator.DefendingAirArmy(7,
                    new[] { body, HeroProfile(4) }, new[] { 0, 2 }));
            var after = GroundCombatAirSupport.AfterAirStrike(opposition, new[] { target },
                new AviationCombatEstimator.AirStrikeEstimate(0, 0, new[] { body }, 12,
                    survivorSourceIndices: new[] { 0 }));
            Assert.That(after[0].Commander.Present, Is.False);
            Assert.That(after[0].Units.Count, Is.EqualTo(1));
        }

        [Test]
        public void AfterAirStrike_UnobservedCommanderIsNotDeclaredDead()
        {
            var body = Profile();
            var commander = new WorthIt.SideCommander(3, 4);
            var opposition = new[] { new WorthIt.DefendingArmy(new[] { body }, commander, 2f, 7) };
            var target = new AiMapMemory.KnownAirSighting(default, _owner, 1, true,
                new AviationCombatEstimator.DefendingAirArmy(7, new[] { body }, new[] { 0 }, -1));
            var after = GroundCombatAirSupport.AfterAirStrike(opposition, new[] { target },
                new AviationCombatEstimator.AirStrikeEstimate(0, 0, new[] { body }, 0,
                    survivorSourceIndices: new[] { 0 }));
            Assert.That(after[0].Commander.Equals(commander), Is.True);
        }

        [Test]
        public void RaidAirTargetCount_IncludesVisibleHero_AndExcludesOtherArmiesAndGuards()
        {
            var target = new HexCoord(3, -1);
            var body = Profile();
            var owner = new PlayerSetupData();
            var snap = new WorldSnapshot { Observer = _owner, Known = new KnownSnapshot {
                AirSightings = new[] {
                    new AiMapMemory.KnownAirSighting(target, owner, 1, true,
                        new AviationCombatEstimator.DefendingAirArmy(7,
                            new[] { body, HeroProfile(4) }, new[] { 0, 2 })),
                    new AiMapMemory.KnownAirSighting(target, owner, 1, true,
                        new AviationCombatEstimator.DefendingAirArmy(8,
                            new[] { body, body, body }, new[] { 0, 0, 0 })) } } };
            var ground = new[] { new WorthIt.DefendingArmy(new[] { body, body, body, body }, default) };
            Assert.That(GroundCombatAirSupport.KnownTargetCount(snap, target,
                AirStrikePolicy.RaidSupport(7), ground), Is.EqualTo(2));
            Assert.That(GroundCombatAirSupport.KnownTargetCount(snap, target,
                AirStrikePolicy.RaidSupport(99), ground), Is.Zero);
        }

        // ---- support options, keys, claims, recon cap ---------------------------------------

        [Test]
        public void CombatRequests_PreserveDistinctRaidPoliciesAtOneHex_AndSkipAttemptedTurn()
        {
            var target = new HexCoord(4, 0);
            var neutral = new PlayerSetupData { IsNeutral = true };
            var snap = new WorldSnapshot { Observer = _owner, TurnNumber = 3,
                Known = new KnownSnapshot {
                    EnemySightings = System.Array.Empty<AiMapMemory.KnownEnemySighting>(),
                    NeutralSightings = System.Array.Empty<AiMapMemory.KnownEnemySighting>(),
                    AirSightings = new[] { 7, 8 }.Select(id => new AiMapMemory.KnownAirSighting(target,
                        neutral, 3, true, new AviationCombatEstimator.DefendingAirArmy(id,
                            new[] { Profile(), Profile() }, new[] { 0, 0 }))).ToList() } };
            var intents = new[] { 7, 8 }.Select(id => new MissionIntent { Kind = MissionKind.Raid,
                Status = IntentStatus.Active, Objective = new RaidIntent { Target = RaidTargetRef.ForNeutralArmy(id),
                    LastKnownHex = target, Phase = RaidMissionPhase.Reinforcement, AirSupportAttemptedTurn = -1 } }).ToList();
            foreach (var intent in intents) intent.IntentKey = MissionIntentKey.For(intent);
            var requests = AviationRebasePlanner.CombatSupportTargets(snap, intents);
            Assert.That(requests.Count, Is.EqualTo(2));
            Assert.That(requests.Select(r => r.Key).Distinct().Count(), Is.EqualTo(2));
            Assert.That(requests.Select(r => r.Policy.ExactTargetArmyId), Is.EquivalentTo(new int?[] { 7, 8 }));
            Assert.That(requests.All(r => r.Policy.MinimumSurvivors == 1), Is.True);
            intents[0].Raid.AirSupportAttemptedTurn = 3;
            Assert.That(AviationRebasePlanner.CombatSupportTargets(snap, intents).Count, Is.EqualTo(1));
        }

        [Test]
        public void CombatProjection_ActualRouteRejectsPlane_AndAdmitsMultiTurnHelicopter()
        {
            HexMap map = Line(4, out GameObject mapObject);
            try
            {
                ArmyData field = Airfield(default, 2);
                UnitData aircraft = Plane(1, 1);
                field.Members.Add(aircraft);
                var snap = new WorldSnapshot { Observer = _owner, Known = new KnownSnapshot {
                    AirSightings = System.Array.Empty<AiMapMemory.KnownAirSighting>() } };
                var request = new CombatAirSupportRequest(MissionIntentKey.ForActiveDefence(7),
                    new HexCoord(4, 0), AirStrikePolicy.DefenceSupport(7), new TaskScore(preventedDamage: 10));
                Assert.That(GroundCombatAirSupport.ProjectService(snap, _owner, map,
                    new[] { aircraft }, field.Hex, request), Is.Null);
                aircraft.TurnsWithoutRefuel = 1;
                var service = GroundCombatAirSupport.ProjectService(snap, _owner, map,
                    new[] { aircraft }, field.Hex, request);
                Assert.That(service.HasValue, Is.True);
                Assert.That(service.Value.RouteCost, Is.EqualTo(8));
                Assert.That(service.Value.StrikeTurns, Is.EqualTo(2));
                Assert.That(service.Value.RosterKnown, Is.False);
                Assert.That(service.Value.Score.Value, Is.GreaterThan(0f));
                aircraft.HasAirAttackedThisTurn = true;
                aircraft.TurnsWithoutRefuel = 0;
                request = new CombatAirSupportRequest(request.Key, new HexCoord(1, 0), request.Policy, request.Score);
                Assert.That(GroundCombatAirSupport.ProjectService(snap, _owner, map,
                    new[] { aircraft }, field.Hex, request), Is.Null, "stored aircraft do not regain their strike");
                aircraft.HasAirAttackedThisTurn = false;
                aircraft.MoveCurrent = 1;
                Assert.That(AiAirSortiePlanner.TryPlanSortieFromStorage(field.Hex,
                    new[] { aircraft }, request.Target, map, _owner), Is.Null,
                    "remaining movement cannot cover an outward step and a return step");
            }
            finally { Object.DestroyImmediate(mapObject); }
        }

        [Test]
        public void CombatProjection_SpentPlaneDoesNotCountAsCurrentTurnService()
        {
            HexMap map = Line(1, out GameObject mapObject);
            var actorObject = new GameObject("air-support-actor");
            try
            {
                Airfield(default, 2);
                UnitData aircraft = Plane(1, 1);
                ArmyData wing = Wing(default, aircraft);
                wing.Controller = actorObject.AddComponent<ArmyController>();
                var snap = new WorldSnapshot { Observer = _owner, Known = new KnownSnapshot {
                    AirSightings = System.Array.Empty<AiMapMemory.KnownAirSighting>() } };
                var request = new CombatAirSupportRequest(MissionIntentKey.ForActiveDefence(7),
                    new HexCoord(1, 0), AirStrikePolicy.DefenceSupport(7), new TaskScore(preventedDamage: 10));
                Assert.That(GroundCombatAirSupport.ProjectService(snap, _owner, map,
                    wing.Members, wing.Hex, request, wing).HasValue, Is.True);
                aircraft.HasAirAttackedThisTurn = true;
                Assert.That(GroundCombatAirSupport.ProjectService(snap, _owner, map,
                    wing.Members, wing.Hex, request, wing), Is.Null);
            }
            finally { Object.DestroyImmediate(actorObject); Object.DestroyImmediate(mapObject); }
        }

        [Test]
        public void CombatCoverage_UsesJointLandingCapacity_WithoutPublishingClaims()
        {
            HexMap map = Line(2, out GameObject mapObject);
            var actors = new List<GameObject>();
            try
            {
                Airfield(default, 1);
                var wings = new List<ArmyData>();
                for (int i = 0; i < 2; i++)
                {
                    var actor = new GameObject("air-coverage-actor"); actors.Add(actor);
                    var wing = Wing(new HexCoord(1, 0), Plane(1, 1));
                    wing.Controller = actor.AddComponent<ArmyController>(); wings.Add(wing);
                }
                Root().ActionPoints = 10; _root.AddResource(ResourceType.Energy, 10);
                var snap = new WorldSnapshot { Observer = _owner, Known = new KnownSnapshot {
                    AirSightings = System.Array.Empty<AiMapMemory.KnownAirSighting>() } };
                var requests = new[] {
                    new CombatAirSupportRequest(MissionIntentKey.ForActiveDefence(7), new HexCoord(2, 0),
                        AirStrikePolicy.DefenceSupport(7), new TaskScore(preventedDamage: 10)),
                    new CombatAirSupportRequest(MissionIntentKey.ForActiveDefence(8), new HexCoord(2, 0),
                        AirStrikePolicy.DefenceSupport(8), new TaskScore(preventedDamage: 10)) };
                var ctx = new AiTurnContext { Map = map };
                var remaining = GroundCombatAirSupport.UncoveredRequests(snap, _owner, _root, ctx,
                    requests, new ActorCommitments());
                Assert.That(remaining.Count, Is.EqualTo(1));
                Assert.That(AiAirSortiePlanner.FreeLandingCapacity(default, _owner), Is.EqualTo(1));
                Assert.That(AirSortieRegistry.For(_owner), Is.Empty);
                Assert.That(_root.ActionPoints, Is.EqualTo(10));
                Assert.That(_root.GetResource(ResourceType.Energy), Is.EqualTo(10));
                BuildingRegistry.FindAt(default).AirfieldCapacity = 2;
                _root.ActionPoints = 1;
                Assert.That(GroundCombatAirSupport.UncoveredRequests(snap, _owner, _root, ctx,
                    requests, new ActorCommitments()).Count, Is.EqualTo(1), "joint AP bank");
                _root.ActionPoints = 10;
                _root.AddResource(ResourceType.Energy, -9);
                Assert.That(GroundCombatAirSupport.UncoveredRequests(snap, _owner, _root, ctx,
                    requests, new ActorCommitments()).Count, Is.EqualTo(1), "joint Energy bank");
                var claims = new ActorCommitments(); claims.Claim(wings[0].Id); claims.Claim(wings[1].Id);
                Assert.That(GroundCombatAirSupport.UncoveredRequests(snap, _owner, _root, ctx,
                    requests, claims).Count, Is.EqualTo(2));
            }
            finally { foreach (var actor in actors) Object.DestroyImmediate(actor); Object.DestroyImmediate(mapObject); }
        }

        [Test]
        public void AviationPurchase_CombatOnlyTaskCreatesValuedCandidate_WithoutCreatingAGoal()
        {
            HexMap map = Line(1, out GameObject mapObject);
            MissionIntentRegistry.Clear();
            try
            {
                Airfield(default, 2);
                Root().ActionPoints = 10; _root.AddResource(ResourceType.Energy, 10);
                var target = new HexCoord(1, 0);
                var neutral = new PlayerSetupData { IsNeutral = true };
                var snap = new WorldSnapshot { Observer = _owner, TurnNumber = 1,
                    Self = new SelfSnapshot { Armies = System.Array.Empty<ArmySnapshot>(),
                        BaseHexes = new[] { default(HexCoord) } },
                    Known = new KnownSnapshot { EnemySightings = System.Array.Empty<AiMapMemory.KnownEnemySighting>(),
                        NeutralSightings = System.Array.Empty<AiMapMemory.KnownEnemySighting>(),
                        Buildings = System.Array.Empty<AiMapMemory.KnownBuilding>(),
                        AirSightings = new[] { new AiMapMemory.KnownAirSighting(target, neutral, 1, true,
                            new AviationCombatEstimator.DefendingAirArmy(7,
                                new[] { Profile(hp: 20), Profile(hp: 20) }, new[] { 0, 0 })) } } };
                var intent = new MissionIntent { Kind = MissionKind.Raid, Status = IntentStatus.Active,
                    Objective = new RaidIntent { Target = RaidTargetRef.ForNeutralArmy(7),
                        LastKnownHex = target, Phase = RaidMissionPhase.Reinforcement, TargetIsNeutral = true } };
                intent.IntentKey = MissionIntentKey.For(intent);
                MissionIntentRegistry.GetOrCreate(_owner).Put(intent);
                var card = new CardData(new CardDefinition { isAviation = true, attack = 6,
                    moveMax = 6, activationApCost = 0, launchEnergyCost = 0 });
                var hand = new AiHandData(null, _owner.Faction, 0); hand.Hand.Add(card);
                var ctx = new AiTurnContext { Map = map, TurnNumber = 1 };
                Assert.That(ReconObjectiveEvaluator.Enumerate(snap), Is.Empty);
                var build = typeof(NonCombatCardPlayer).GetMethod("BuildAviationPlays",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
                var arguments = new object[] { card, null, snap, _owner, _root, hand, ctx,
                    new List<string>(), null, false };
                var candidates = (List<NonCombatCardPlayer.NonCombatPlay>)build.Invoke(null, arguments);
                Assert.That(candidates.Count, Is.EqualTo(1));
                Assert.That(candidates[0].Explain, Does.Contain("serviceTask="));
                Assert.That(candidates[0].Explain, Does.Contain(intent.IntentKey.ToString()));
                Assert.That(MissionIntentRegistry.GetOrCreate(_owner).Count, Is.EqualTo(1));
                MissionIntentRegistry.Clear();
                candidates = (List<NonCombatCardPlayer.NonCombatPlay>)build.Invoke(null, arguments);
                Assert.That(candidates, Is.Empty, "a sighting alone must not invent a task for aviation");
            }
            finally { MissionIntentRegistry.Clear(); Object.DestroyImmediate(mapObject); }
        }

        [Test]
        public void Coverage_AnUnreachableWingDoesNotCoverATask()
        {
            var match = GroundCombatAirSupport.MatchCoverage(1, 1, (r, w) => false, _ => true);
            Assert.That(match, Is.Empty);
        }

        [Test]
        public void Coverage_OneWingCannotCoverTwoDistinctTasksAtTheSameHex()
        {
            var requests = new[] {
                new CombatAirSupportRequest(MissionIntentKey.ForActiveDefence(7), default,
                    AirStrikePolicy.DefenceSupport(7), new TaskScore(preventedDamage: 10)),
                new CombatAirSupportRequest(MissionIntentKey.ForActiveDefence(8), default,
                    AirStrikePolicy.DefenceSupport(8), new TaskScore(preventedDamage: 10)) };
            var match = GroundCombatAirSupport.MatchCoverage(requests.Length, 1, (r, w) => true, _ => true);
            Assert.That(match.Count, Is.EqualTo(1));
            Assert.That(requests[0].Key.Equals(requests[1].Key), Is.False);
        }

        [Test]
        public void Coverage_ReassignsAFlexibleWingToPreserveAConstrainedTask()
        {
            var match = GroundCombatAirSupport.MatchCoverage(2, 2, (r, w) => w == 0 || r == 0, _ => true);
            Assert.That(match.Count, Is.EqualTo(2));
            Assert.That(match[0], Is.EqualTo(1));
            Assert.That(match[1], Is.EqualTo(0));
        }

        [Test]
        public void Coverage_RetriesATaskAfterAnotherExecutableAssignmentOpensCapacity()
        {
            var match = GroundCombatAirSupport.MatchCoverage(2, 2, (r, w) => r == w,
                a => !a.Values.Contains(0) || a.Values.Contains(1));
            Assert.That(match.Count, Is.EqualTo(2));
        }

        [TestCase(true)]
        [TestCase(false)]
        public void Coverage_JointBankRejectsTwoIndividuallyAffordableLaunches(bool apBank)
        {
            int[] ap = apBank ? new[] { 2, 2 } : new[] { 0, 0 };
            int[] energy = apBank ? new[] { 0, 0 } : new[] { 2, 2 };
            var match = GroundCombatAirSupport.MatchCoverage(2, 2, (r, w) => true,
                a => a.Keys.Sum(w => ap[w]) <= 2 && a.Keys.Sum(w => energy[w]) <= 2);
            Assert.That(match.Count, Is.EqualTo(1));
            Assert.That(match.Values.Distinct().Count(), Is.EqualTo(match.Count));
        }

        [Test]
        public void Coverage_JointLandingClaimDoesNotCountTwoWingsInOneSlot()
        {
            var match = GroundCombatAirSupport.MatchCoverage(2, 2, (r, w) => true, a => a.Count <= 1);
            Assert.That(match.Count, Is.EqualTo(1));
        }

        [Test]
        public void Coverage_ClaimedActorsAreNotBorrowedFromRecon()
        {
            var commitments = new ActorCommitments();
            commitments.Claim(0);
            var match = GroundCombatAirSupport.MatchCoverage(2, 2,
                (r, w) => !commitments.IsArmyClaimed(w), _ => true);
            Assert.That(match.Count, Is.EqualTo(1));
            Assert.That(match.Keys, Has.No.Member(0));
            Assert.That(commitments.IsArmyClaimed(0), Is.True);
        }

        [Test]
        public void StoredOutboundBudget_UsesRemainingMovement_AndEmergencyPenalty()
        {
            var aircraft = new UnitData { IsAviation = true, MoveMax = 8, MoveCurrent = 2 };
            Assert.That(AviationRange.FirstTurnOutboundBudget(new[] { aircraft }), Is.EqualTo(1));
            aircraft.TurnsWithoutRefuel = 1;
            Assert.That(AviationRange.FirstTurnOutboundBudget(new[] { aircraft }), Is.EqualTo(2));
            aircraft.HasEmergencyFlightPenalty = true;
            Assert.That(AviationRange.FirstTurnOutboundBudget(new[] { aircraft }), Is.EqualTo(1));
        }

        [Test]
        public void CombatServiceScore_InheritsTaskAndPaysLaunchOnlyOnce()
        {
            var intrinsic = new TaskScore(preventedDamage: 10f);
            var one = GroundCombatAirSupport.ServiceScore(intrinsic, 0.5f, 2, 3, 1);
            var many = GroundCombatAirSupport.ServiceScore(intrinsic, 0.5f, 2, 3, 5);
            Assert.That(one.PreventedDamage, Is.EqualTo(intrinsic.PreventedDamage));
            Assert.That(many.CardPrice, Is.EqualTo(one.CardPrice));
            Assert.That(one.Delivery, Is.Zero);
            Assert.That(many.Delivery, Is.Zero);
            Assert.That(one.Value, Is.GreaterThan(0f));
        }

        [Test]
        public void CombatServiceScore_EntersAviationCardOperationalValue()
        {
            var service = GroundCombatAirSupport.ServiceScore(new TaskScore(raidReward: 10),
                0.5f, 1, 1, 2);
            var card = new CardData(new CardDefinition { isAviation = true });
            var candidate = StrategicCardEvaluator.ScoreNonCombat(NonCombatRole.Aviation,
                card, null, null, null, 0f, operationalTask: service);
            var without = StrategicCardEvaluator.ScoreNonCombat(NonCombatRole.Aviation,
                card, null, null, null, 0f);
            Assert.That(candidate.Breakdown.OperationalTaskValue, Is.EqualTo(service.Value));
            Assert.That(candidate.NetScore - without.NetScore, Is.EqualTo(service.Value).Within(0.0001f));
        }

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
        public void AfterStrike_PreservesEachSurvivingArmiesDefenceBonus_IncludingZeroAndUnknown()
        {
            var body = Profile(attack: 2f, defense: 1f, hp: 4f);
            var opposition = new[]
            {
                new WorthIt.DefendingArmy(new[] { body }, default, 100f),
                new WorthIt.DefendingArmy(new[] { body }, default, 0f),
                new WorthIt.DefendingArmy(new[] { body }, default, 3f),
                new WorthIt.DefendingArmy(new[] { body }, default),
            };
            var projected = GroundCombatAirSupport.AfterStrike(opposition,
                new[] { body, body, body }, new[] { 1, 2, 3 });
            Assert.That(projected.Count, Is.EqualTo(3), "the wiped army leaves the package");
            Assert.That(projected[0].DefenseBonus(9f), Is.Zero,
                "known zero must not acquire the site's fallback structure defence");
            Assert.That(projected[1].DefenseBonus(9f), Is.EqualTo(3f));
            Assert.That(projected[2].DefenseBonusOverride.HasValue, Is.False,
                "unknown retains the caller's fallback contract");
            Assert.That(projected[2].DefenseBonus(9f), Is.EqualTo(9f));
            Assert.That(opposition[0].Units.Count, Is.EqualTo(1), "projection is read-only");
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

        // Level 2: a stalled rebase wing stops counting as an obligation, the other wing still
        // does, and the selection hands the turn back to the missions once none is left.
        // Engine-bound (ArmyController is a MonoBehaviour): runs in the Unity editor only.
        [Test]
        public void MandatoryRebase_StalledWingsStopCounting_ForTheirTurnOnly()
        {
            var actors = new List<GameObject>();
            AviationObligationStallRegistry.Clear();
            try
            {
                var wings = new List<ArmyData>();
                for (int i = 0; i < 2; i++)
                {
                    var actor = new GameObject("rebase-actor"); actors.Add(actor);
                    ArmyData wing = Wing(new HexCoord(1, 0), Plane(1, 1));
                    wing.Controller = actor.AddComponent<ArmyController>();
                    AirSortieRegistry.Add(_owner, new AirSortie
                        { Army = wing, Kind = AirSortieKind.Rebase, LandingHex = default });
                    wings.Add(wing);
                }
                var ctx = new AiTurnContext { TurnNumber = 5 };
                var all = AviationRebasePlanner.FindMandatoryContinuations(_owner, 5);
                Assert.That(all, Is.EqualTo(wings.OrderBy(w => w.Id).ToList()));
                Assert.That(AviationObligations.Pending(_owner, ctx), Is.True);
                var first = MandatoryAviationOrder.Next(all,
                    ReconAirExecutor.FindMandatoryRecoveryActors(_owner, ctx));
                Assert.That(first.Kind, Is.EqualTo(MandatoryAviationKind.Rebase));
                Assert.That(first.Actor, Is.SameAs(all[0]));

                AviationObligationStallRegistry.MarkStalled(_owner, 5, all[0].Id);
                var rest = AviationRebasePlanner.FindMandatoryContinuations(_owner, 5);
                Assert.That(rest, Is.EqualTo(new[] { all[1] }));
                Assert.That(MandatoryAviationOrder.Next(rest, new List<ArmyData>()).Actor, Is.SameAs(all[1]));

                AviationObligationStallRegistry.MarkStalled(_owner, 5, all[1].Id);
                Assert.That(AviationObligations.Pending(_owner, ctx), Is.False,
                    "the last obligation stalled: Phase A and the deferred axes are released");
                Assert.That(OperationalWorkSelection.Select(MandatoryAviationKind.None, 1),
                    Is.EqualTo(OperationalWorkKind.Mission));
                Assert.That(AviationRebasePlanner.FindMandatoryContinuations(_owner, 6).Count, Is.EqualTo(2),
                    "the next turn re-tries both");
            }
            finally
            {
                AviationObligationStallRegistry.Clear();
                foreach (var actor in actors) Object.DestroyImmediate(actor);
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
