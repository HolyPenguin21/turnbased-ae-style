#if UNITY_INCLUDE_TESTS
using System.Linq;
using Game.Ai.V2;
using Game.Aviation;
using Game.Cards;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Units;
using NUnit.Framework;

namespace Game.EditorTests
{
    // AA Facility parity with AA units, plus the turn-start AA pass for wings that never move.
    public sealed class AntiAirFacilityTests
    {
        private static readonly HexCoord BaseHex = new HexCoord(0, 0);
        private static readonly HexCoord Adjacent = new HexCoord(1, 0);
        private static readonly HexCoord TwoAway = new HexCoord(2, 0);
        private PlayerSetupData _player;
        private PlayerSetupData _enemy;

        [SetUp]
        public void SetUp()
        {
            Reset();
            _player = new PlayerSetupData();
            _enemy = new PlayerSetupData();
        }

        [TearDown]
        public void TearDown() => Reset();

        private static void Reset()
        {
            ArmyRegistry.Clear(); BuildingRegistry.Clear(); AntiAirState.Clear();
            VisionSystem.Clear(); VisionSystem.Configure(null); StealthSystem.Clear();
        }

        private BuildingData AaBase(PlayerSetupData owner, out FacilityData facility)
        {
            var building = new BuildingData { Name = "Base", Owner = owner, Hex = BaseHex, IsBase = true };
            facility = new FacilityData { Name = "AA Defence", Attack = 5, StructurePointsCurrent = 6, StructurePointsMax = 6 };
            facility.Abilities.Add(UnitAbilities.AntiAir);
            building.FacilitySlots[0] = facility;
            BuildingRegistry.Register(BaseHex, building);
            return building;
        }

        private ArmyData Wing(PlayerSetupData owner, HexCoord hex)
        {
            var wing = new ArmyData { Owner = owner, Hex = hex };
            wing.Members.Add(new UnitData { IsAviation = true, Owner = owner, HitPointsMax = 3, HitPointsCurrent = 3 });
            ArmyRegistry.Register(wing);
            return wing;
        }

        [Test]
        public void FacilityReactsToEntryWithinRadiusOneLikeAUnit()
        {
            BuildingData building = AaBase(_player, out FacilityData facility);
            ArmyData wing = Wing(_enemy, Adjacent);
            VisionSystem.RecomputeFor(_player);

            var reactions = AntiAirRules.CollectEntryReactions(wing, Adjacent);
            Assert.That(reactions.Count, Is.EqualTo(1));
            Assert.That(reactions[0].AaArmy, Is.Null);
            Assert.That(reactions[0].AaBuilding, Is.SameAs(building));
            Assert.That(reactions[0].AaOwner, Is.SameAs(_player));
            Assert.That(reactions[0].AaUnit, Is.SameAs(facility.AntiAirGunner));
            Assert.That(reactions[0].AaUnit.Owner, Is.SameAs(_player));
            Assert.That(reactions[0].AaUnit.Attack, Is.EqualTo(5));
            Assert.That(AntiAirRules.CollectEntryReactions(wing, TwoAway), Is.Empty);
        }

        [Test]
        public void FacilityFiresOncePerTurnAcrossAirArmies()
        {
            AaBase(_player, out FacilityData facility);
            ArmyData first = Wing(_enemy, Adjacent);
            ArmyData second = Wing(_enemy, BaseHex);
            VisionSystem.RecomputeFor(_player);

            UnitData gunner = AntiAirRules.CollectEntryReactions(first, Adjacent).Single().AaUnit;
            AntiAirState.RecordPrompted(gunner, first.Id, true);
            Assert.That(AntiAirRules.CollectEntryReactions(first, Adjacent), Is.Empty);
            Assert.That(AntiAirRules.CollectEntryReactions(second, BaseHex), Is.Empty);

            AntiAirState.ResetForOwner(_player);
            Assert.That(AntiAirRules.CollectEntryReactions(second, BaseHex).Single().AaUnit,
                Is.SameAs(facility.AntiAirGunner));
        }

        [Test]
        public void TurnStartFindsStationaryWingsForUnitsAndFacilities()
        {
            AaBase(_player, out _);
            var ground = new ArmyData { Owner = _player, Hex = TwoAway };
            var aaUnit = new UnitData { Owner = _player, Attack = 3, HitPointsMax = 3, HitPointsCurrent = 3 };
            aaUnit.Abilities.Add(UnitAbilities.AntiAir);
            ground.Members.Add(aaUnit);
            ArmyRegistry.Register(ground);
            ArmyData wing = Wing(_enemy, Adjacent);
            VisionSystem.RecomputeFor(_player);

            var reactions = AntiAirRules.CollectTurnStartOpportunities(_player);
            Assert.That(reactions.Count, Is.EqualTo(2));
            Assert.That(reactions.All(r => r.AirArmy == wing), Is.True);
            Assert.That(reactions.Any(r => r.AaUnit == aaUnit && r.AaArmy == ground), Is.True);
            Assert.That(reactions.Any(r => r.AaArmy == null && r.AaBuilding != null), Is.True);
            Assert.That(AntiAirRules.CollectTurnStartOpportunities(_enemy), Is.Empty);
        }

        [Test]
        public void WingFullyHiddenFromTheAaOwnerIsNotATarget()
        {
            AaBase(_player, out _);
            var ground = new ArmyData { Owner = _player, Hex = TwoAway };
            var aaUnit = new UnitData { Owner = _player, Attack = 3, HitPointsMax = 3, HitPointsCurrent = 3 };
            aaUnit.Abilities.Add(UnitAbilities.AntiAir);
            ground.Members.Add(aaUnit);
            ArmyRegistry.Register(ground);
            ArmyData wing = Wing(_enemy, Adjacent);
            wing.Members[0].IsHidden = true;
            VisionSystem.RecomputeFor(_player);

            Assert.That(AntiAirRules.CollectEntryReactions(wing, Adjacent), Is.Empty);
            Assert.That(AntiAirRules.CollectTurnStartOpportunities(_player), Is.Empty);

            // One aircraft the AA owner can see makes the wing a target again.
            wing.Members.Add(new UnitData { IsAviation = true, Owner = _enemy, HitPointsMax = 3, HitPointsCurrent = 3 });
            Assert.That(AntiAirRules.CollectEntryReactions(wing, Adjacent).Count, Is.EqualTo(2));
            Assert.That(AntiAirRules.CollectTurnStartOpportunities(_player).Count, Is.EqualTo(2));
        }

        [Test]
        public void AiBaselineCountsAnOwnAaFacilityAsAntiAirCoverage()
        {
            WorldSnapshot Snap(bool facilityAa) => new WorldSnapshot
            {
                Self = new SelfSnapshot { Armies = new ArmySnapshot[0], HasFacilityAntiAir = facilityAa },
                Threat = new ThreatModel { Threats = new AssetThreatSnapshot[0] },
            };
            Assert.That(BaselineForceReadiness.Evaluate(Snap(false), null).HasAntiAir, Is.False);
            Assert.That(BaselineForceReadiness.Evaluate(Snap(true), null).HasAntiAir, Is.True);
        }

        [Test]
        public void CapturedBaseHandsItsFacilityAaToTheNewOwner()
        {
            BuildingData building = AaBase(_player, out FacilityData facility);
            ArmyData enemyWing = Wing(_enemy, Adjacent);
            VisionSystem.RecomputeFor(_player);
            UnitData gunner = AntiAirRules.CollectEntryReactions(enemyWing, Adjacent).Single().AaUnit;
            AntiAirState.RecordPrompted(gunner, enemyWing.Id, true);

            building.Owner = _enemy;
            AntiAirRules.SyncFacilityGunnerOwners();
            AntiAirState.ResetForOwner(_enemy);
            ArmyData playerWing = Wing(_player, Adjacent);
            VisionSystem.RecomputeFor(_enemy);

            Assert.That(AntiAirRules.CollectEntryReactions(enemyWing, Adjacent), Is.Empty);
            var reaction = AntiAirRules.CollectEntryReactions(playerWing, Adjacent).Single();
            Assert.That(reaction.AaOwner, Is.SameAs(_enemy));
            Assert.That(reaction.AaUnit, Is.SameAs(facility.AntiAirGunner));
            Assert.That(reaction.AaUnit.Owner, Is.SameAs(_enemy));
        }
    }
}
#endif
