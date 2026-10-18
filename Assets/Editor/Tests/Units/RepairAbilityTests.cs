#if UNITY_INCLUDE_TESTS
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Game.Ai.V2;
using Game.Cards;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Units;
using NUnit.Framework;

namespace Game.EditorTests
{
    public sealed class RepairAbilityTests
    {
        private PlayerSetupData _owner, _enemy;
        private static readonly HexCoord Field = new HexCoord(5, 2);
        private static Dictionary<HexCoord, List<ArmyData>> Armies =>
            (Dictionary<HexCoord, List<ArmyData>>)typeof(ArmyRegistry)
                .GetField("ByHex", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
        private static Dictionary<HexCoord, BuildingData> Buildings =>
            (Dictionary<HexCoord, BuildingData>)typeof(BuildingRegistry)
                .GetField("ByHex", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);

        [SetUp] public void SetUp()
        {
            ArmyRegistry.Clear(); BuildingRegistry.Clear(); VisionSystem.Clear(); VisionSystem.Configure(null);
            _owner = new PlayerSetupData(); _enemy = new PlayerSetupData();
        }
        [TearDown] public void TearDown()
        {
            ArmyRegistry.Clear(); BuildingRegistry.Clear(); VisionSystem.Clear();
        }

        private UnitData Unit(bool repair = false) {
            var unit = new UnitData { Owner = _owner, Name = "body", Attack = 3,
                HitPointsCurrent = 2, HitPointsMax = 5, RepairResourceCost = new ResourceCost() };
            if (repair) unit.Abilities.Add(UnitAbilities.Repair);
            return unit;
        }
        // Seed the indexed world without native vision callbacks. The production lookup and
        // all target/source rules execute unchanged; movement publication has separate coverage.
        private ArmyData Army(params UnitData[] units) {
            var army = new ArmyData { Owner = _owner, Hex = Field }; army.Members.AddRange(units);
            if (!Armies.TryGetValue(Field, out var at)) Armies[Field] = at = new List<ArmyData>();
            at.Add(army); return army;
        }

        [Test] public void MechanicServesAnotherArmyAndItselfWithoutCreatingABase()
        {
            var mechanic = Unit(true); var source = Army(mechanic);
            var target = Unit(); var other = Army(target);
            Assert.That(UnitRepair.CanRepair(target, other), Is.True);
            Assert.That(UnitRepair.CanRepair(mechanic, source), Is.True);
            Assert.That(BuildingRegistry.FindAt(Field), Is.Null);
            Assert.That(UnitRepair.CanRepairAt(Field, _enemy), Is.False);
        }
        [Test] public void BasesRetainRepairAndBareBarracksOrExtractionSitesDoNotGainIt()
        {
            var building = new BuildingData { Owner = _owner, Hex = Field };
            building.Abilities.Add(UnitAbilities.Barracks); Buildings[Field] = building;
            Assert.That(UnitRepair.CanRepairAt(Field, _owner), Is.False);
            building.IsBase = true;
            Assert.That(UnitRepair.CanRepairAt(Field, _owner), Is.True);
            Assert.That(UnitRepair.CanRepairAt(Field, _enemy), Is.False);
            Assert.That(UnitRepair.CanRepairAt(Field, null), Is.False);
        }
        [TestCase("dead")][TestCase("prisoner")][TestCase("aircraft")]
        [TestCase("foreignMember")][TestCase("foreignArmy")][TestCase("lostSkill")]
        [TestCase("prison")][TestCase("airfield")][TestCase("eliminated")]
        public void InvalidSourceNeverGrantsRepair(string invalid)
        {
            var mechanic = Unit(true); var army = Army(mechanic);
            switch (invalid) {
                case "dead": mechanic.HitPointsCurrent = 0; break;
                case "prisoner": mechanic.IsPrisoner = true; break;
                case "aircraft": mechanic.IsAviation = true; break;
                case "foreignMember": mechanic.Owner = _enemy; break;
                case "foreignArmy": army.Owner = _enemy; mechanic.Owner = _enemy; break;
                case "lostSkill": mechanic.Abilities.Clear(); break;
                case "prison": army.IsPrison = true; break;
                case "airfield": army.IsAirfield = true; break;
                case "eliminated": _owner.IsEliminated = true; break;
            }
            Assert.That(UnitRepair.CanRepairAt(Field, _owner), Is.False);
        }
        [Test] public void GarrisonMechanicWorksAndMultipleSourcesDoNotChangePrice()
        {
            var mechanic = Unit(true); var army = Army(mechanic, Unit(true)); army.IsGarrison = true;
            Assert.That(UnitRepair.RepairSourceCount(army), Is.EqualTo(2));
            Assert.That(UnitRepair.CanRepair(mechanic, army), Is.True);
            Assert.That(UnitRepair.ApCost(mechanic), Is.EqualTo(1));
            var cost = UnitRepair.ResourceCost(mechanic);
            army.Members.RemoveAt(1);
            Assert.That(UnitRepair.CanRepairAt(Field, _owner), Is.True);
            Assert.That(UnitRepair.ResourceCost(mechanic), Is.SameAs(cost));
        }
        [Test] public void TransferAndRelocationUseCurrentMembershipWithoutStaleHexState()
        {
            var mechanic = Unit(true); var first = Army(mechanic); var second = Army(Unit());
            first.Members.Remove(mechanic); second.Members.Add(mechanic);
            Assert.That(UnitRepair.CanRepairAt(Field, _owner), Is.True);
            var next = new HexCoord(6, 2);
            Armies[Field].Remove(second); second.Hex = next; Armies[next] = new List<ArmyData> { second };
            Assert.That(UnitRepair.CanRepairAt(Field, _owner), Is.False);
            Assert.That(UnitRepair.CanRepairAt(next, _owner), Is.True);
            mechanic.HitPointsCurrent = 0;
            Assert.That(UnitRepair.CanRepairAt(next, _owner), Is.False);
            Buildings[Field] = new BuildingData { Owner = _owner, Hex = Field, IsBase = true };
            Assert.That(UnitRepair.CanRepairAt(Field, _owner), Is.True);
        }
        [TestCase("dead")][TestCase("healthy")][TestCase("prisoner")]
        [TestCase("foreign")][TestCase("unregistered")][TestCase("removed")][TestCase("prison")]
        public void InvalidTargetNeverOffersRepair(string invalid)
        {
            Army(Unit(true)); var target = Unit(); var army = Army(target);
            switch (invalid) {
                case "dead": target.HitPointsCurrent = 0; break;
                case "healthy": target.HitPointsCurrent = target.HitPointsMax; break;
                case "prisoner": target.IsPrisoner = true; break;
                case "foreign": target.Owner = _enemy; break;
                case "unregistered": Armies[Field].Remove(army); break;
                case "removed": army.Members.Remove(target); break;
                case "prison": army.IsPrison = true; break;
            }
            Assert.That(UnitRepair.CanRepair(target, army), Is.False);
        }
        [TestCase(true)][TestCase(false)]
        public void HeroesAndStoredAircraftUseTheSameRepairPoint(bool hero)
        {
            Army(Unit(true)); var unit = Unit(); unit.IsHero = hero; unit.IsAviation = !hero;
            var army = Army(unit); army.IsAirfield = !hero;
            Assert.That(UnitRepair.CanRepair(unit, army), Is.True);
            Assert.That(UnitRepair.RepairSourceCount(army), Is.Zero);
        }
        [Test] public void PhaseBCandidatesUseTheSharedRuleIncludingStoredAircraft()
        {
            Army(Unit(true)); var unit = Unit(); var army = Army(unit); army.IsAirfield = true;
            var find = typeof(StrategicMaintenancePolicy).GetMethod("FindRepairCandidates",
                BindingFlags.Static | BindingFlags.NonPublic);
            var candidates = ((IEnumerable<(UnitData Unit, HexCoord Hex)>)find.Invoke(null,
                new object[] { _owner })).ToList();
            Assert.That(candidates.Any(x => x.Unit == unit && x.Hex.Equals(Field)), Is.True);
            unit.IsPrisoner = true;
            Assert.That(((IEnumerable<(UnitData Unit, HexCoord Hex)>)find.Invoke(null,
                new object[] { _owner })).Any(x => x.Unit == unit), Is.False);
        }
        [Test] public void OwnSnapshotFreezesSourcesAndRefreshesAfterDeathOrTransfer()
        {
            var mechanic = Unit(true); var army = Army(mechanic); var other = Army(Unit());
            var before = WorldAnalysis.ToArmySnapshot(army, _owner, true, 1, null);
            Assert.That(before.RepairSourceCount, Is.EqualTo(1));
            army.Members.Remove(mechanic); other.Members.Add(mechanic);
            Assert.That(WorldAnalysis.ToArmySnapshot(army, _owner, true, 1, null).RepairSourceCount, Is.Zero);
            Assert.That(WorldAnalysis.ToArmySnapshot(other, _owner, true, 1, null).RepairSourceCount, Is.EqualTo(1));
            mechanic.HitPointsCurrent = 0;
            Assert.That(WorldAnalysis.ToArmySnapshot(other, _owner, true, 1, null).RepairSourceCount, Is.Zero);
            Assert.That(before.RepairSourceCount, Is.EqualTo(1));
        }
    }
}
#endif
