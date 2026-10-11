#if UNITY_INCLUDE_TESTS
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Units;
using NUnit.Framework;

namespace Game.EditorTests
{
    public class VisionContainerTests
    {
        [SetUp] public void SetUp()
        {
            VisionSystem.Clear();
            VisionSystem.Configure(null);
            BuildingRegistry.Clear();
            ArmyRegistry.Clear();
        }
        [TearDown] public void TearDown() { VisionSystem.Clear(); BuildingRegistry.Clear(); ArmyRegistry.Clear(); }

        [Test]
        public void CapturedCitadelHexIsLostDespiteOldOwnersPrisonAndEmptyShell()
        {
            var loser = new PlayerSetupData { ColorIndex = 0 };
            var captor = new PlayerSetupData { ColorIndex = 1 };
            var hex = new HexCoord(2, 3);
            var building = new BuildingData { Owner = loser, IsBase = true, Hex = hex };
            BuildingRegistry.Register(hex, building);
            ArmyRegistry.Register(new ArmyData { Owner = loser, Hex = hex, IsPrison = true });
            ArmyRegistry.Register(new ArmyData { Owner = loser, Hex = hex });
            Assert.That(VisionSystem.IsVisible(loser, hex), Is.True);

            BuildingRegistry.CaptureOrDestroy(building, captor, null);

            Assert.That(VisionSystem.IsVisible(loser, hex), Is.False);
            Assert.That(VisionSystem.IsVisible(captor, hex), Is.True);
        }

        [Test]
        public void ArmyWithMembersStillGivesVision()
        {
            var owner = new PlayerSetupData { ColorIndex = 0 };
            var hex = new HexCoord(1, 1);
            var army = new ArmyData { Owner = owner, Hex = hex };
            army.Members.Add(new UnitData { Owner = owner });
            ArmyRegistry.Register(army);
            Assert.That(VisionSystem.IsVisible(owner, hex), Is.True);
        }
    }
}
#endif
