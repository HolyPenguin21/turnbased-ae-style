#if UNITY_INCLUDE_TESTS
using Game.Ai.V2;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Units;
using NUnit.Framework;

namespace Game.EditorTests
{
    // 2026-11-25 (match Grimm/Brynn/Halden) — the muster Intake (the staging garrison's bodies into
    // the fist on the same hex) never executed in 38 rounds: the pre-execution revalidation read
    // every ActiveDefence Return as goal-met once its mover stood on ReturnHex, and an Intake's
    // mover is the fist already standing there. A pinned transfer's goal is the transfer.
    public class AiMusterIntakeRevalidationTests
    {
        private static readonly HexCoord Staging = new HexCoord(4, -6);
        private PlayerSetupData _player;
        private ArmyData _fist, _garrison;

        [SetUp]
        public void SetUp()
        {
            ArmyRegistry.Clear();
            _player = new PlayerSetupData { Nickname = "Halden" };
            _fist = new ArmyData { Owner = _player, Hex = Staging };
            _fist.Members.Add(new UnitData { Owner = _player, HitPointsMax = 5, HitPointsCurrent = 5 });
            ArmyRegistry.Register(_fist);
            _garrison = new ArmyData { Owner = _player, Hex = Staging, IsGarrison = true };
            _garrison.Members.Add(new UnitData { Owner = _player, HitPointsMax = 5, HitPointsCurrent = 5 });
            ArmyRegistry.Register(_garrison);
        }

        [TearDown]
        public void TearDown() => ArmyRegistry.Clear();

        private ProvisionedMission Muster(int? donor) => new ProvisionedMission
        {
            Kind = MissionKind.ActiveDefence, MoverArmyId = _fist.Id,
            ActiveDefenceTarget = new ActiveDefenceMissionTarget
            {
                Phase = ActiveDefencePhase.Return, ReturnPurpose = ActiveDefenceReturnPurpose.Muster,
                EnemyArmyId = -1, PrimaryArmyId = _fist.Id, ReturnHex = Staging,
                ReliefDonorGarrisonId = donor,
            },
        };

        [Test]
        public void IntakeOnTheStagingHex_IsNotGoalMetByPosition()
        {
            ProvisionedMission intake = Muster(_garrison.Id);
            Assert.That(MissionRevalidator.Validate(_player, null, null, intake), Is.EqualTo(MissionValidity.Valid));
            Assert.That(MissionContinuityLayer.IsStepObjectiveSatisfiedLive(_player, intake), Is.False);
        }

        [Test]
        public void PlainReturnOnItsHex_StaysGoalMet()
        {
            ProvisionedMission walk = Muster(null);
            Assert.That(MissionRevalidator.Validate(_player, null, null, walk), Is.EqualTo(MissionValidity.StaleGoalMet));
            Assert.That(MissionContinuityLayer.IsStepObjectiveSatisfiedLive(_player, walk), Is.True);
        }
    }
}
#endif
