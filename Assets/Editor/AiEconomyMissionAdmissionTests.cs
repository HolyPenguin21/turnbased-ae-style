#if UNITY_INCLUDE_TESTS
using Game.Ai.V2;
using Game.HexGrid;
using NUnit.Framework;

namespace Game.EditorTests
{
    public class AiEconomyMissionAdmissionTests
    {
        [Test]
        public void CommittedBuilder_SpentTravelIsDeferredButArrivalAndRefillAreAdmitted()
        {
            var target = new HexCoord(3, 2);
            var actor = new ArmySnapshot
            {
                ArmyId = 15,
                Hex = new HexCoord(2, 2),
                HasHero = true,
                CurrentMovement = 0,
                MaxMovement = 3,
            };
            var snapshot = new WorldSnapshot
            {
                Self = new SelfSnapshot { Armies = new[] { actor } },
            };
            var intent = new MissionIntent
            {
                Kind = MissionKind.Economy,
                Status = IntentStatus.Active,
                Funding = CommitmentTier.Soft,
                PreferredMoverArmyId = actor.ArmyId,
                Objective = new EconomyIntent
                {
                    Kind = EconomyTaskKind.ReturnBuilder,
                    TargetHex = target,
                    BuilderArmyId = actor.ArmyId,
                },
            };

            Assert.That(EconomyMissionPlanner.Propose(snapshot, null,
                new[] { intent }, null), Is.Empty,
                "Phase B changing hand/resources must not re-fund a pinned builder with zero MP");

            actor.CurrentMovement = 1;
            Assert.That(EconomyMissionPlanner.Propose(snapshot, null,
                new[] { intent }, null), Has.Count.EqualTo(1),
                "a genuinely refreshed movement state must make the mission executable again");

            actor.CurrentMovement = 0;
            actor.Hex = target;
            Assert.That(EconomyMissionPlanner.Propose(snapshot, null,
                new[] { intent }, null), Has.Count.EqualTo(1),
                "zero movement must not prevent completion on the destination hex");
        }
    }
}
#endif
