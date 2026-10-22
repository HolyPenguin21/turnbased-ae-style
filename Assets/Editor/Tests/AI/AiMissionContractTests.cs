#if UNITY_INCLUDE_TESTS
using System;
using Game.Ai.V2;
using Game.HexGrid;
using Game.Players;
using NUnit.Framework;

namespace Game.EditorTests
{
    // The per-kind mission contract: every MissionKind is wired into Continuity once, and a
    // target's own identity agrees with the durable objective it becomes.
    public sealed class AiMissionContractTests
    {
        [Test]
        public void EveryMissionKind_HasAContinuityRegistration()
        {
            foreach (MissionKind kind in Enum.GetValues(typeof(MissionKind)))
                Assert.That(MissionContinuityLayer.HasContinuity(kind), Is.True,
                    $"{kind} must be registered, never read as Scout");
        }

        [Test]
        public void TargetAndObjective_EncodeTheSameIntentKey()
        {
            var hex = new HexCoord(3, -1);
            var red = new PlayerSetupData { Nickname = "Red", ColorIndex = 1 };
            AttackTargetRef attack = AttackTargetRef.For(hex, red, AttackTargetKind.Base);
            RaidTargetRef raid = RaidTargetRef.ForNeutralArmy(0);

            Pair(new ScoutMissionTarget { Kind = ScoutTargetKind.Refresh, FocusHex = hex },
                new ScoutIntent { Kind = ScoutTargetKind.Refresh, FocusHex = hex });
            Pair(new ScoutMissionTarget { Kind = ScoutTargetKind.AirSweep, FocusHex = hex },
                new ScoutIntent { Kind = ScoutTargetKind.AirSweep, FocusHex = new HexCoord(9, 9) });
            Pair(new RaidMissionTarget { Target = raid }, new RaidIntent { Target = raid });
            Pair(new AttackMissionTarget { Target = attack }, new AttackIntent { Target = attack });
            Pair(new ActiveDefenceMissionTarget { Phase = ActiveDefencePhase.Return, EnemyArmyId = 4,
                    PrimaryArmyId = 7, ReturnHex = hex },
                new ActiveDefenceIntent { Phase = ActiveDefencePhase.Return, EnemyArmyId = 4,
                    PrimaryArmyId = 7, ReturnHex = hex });
            Pair(new EconomyMissionTarget { Kind = EconomyTaskKind.ReturnBuilder, BuilderArmyId = 5, TargetHex = hex },
                new EconomyIntent { Kind = EconomyTaskKind.ReturnBuilder, BuilderArmyId = 5, TargetHex = hex });
            Pair(new DevelopmentMissionTarget { FacilityHex = hex },
                new DevelopmentIntent { FacilityHex = hex });
        }

        [Test]
        public void ProposalKey_DisagreeingKindKeepsTheBareKey()
        {
            var proposal = new MissionProposal { Kind = MissionKind.Raid,
                Target = new ScoutMissionTarget { Kind = ScoutTargetKind.Explore, FocusHex = new HexCoord(1, 1) } };
            Assert.That(MissionIntentKey.For(proposal),
                Is.EqualTo(new MissionIntentKey(MissionKind.Raid, 0, 0, 0, 0)));
            Assert.That(StableMissionKey.For(proposal),
                Is.EqualTo(new StableMissionKey(MissionKind.Raid, 0, 0, 0, 0)));
        }

        private static void Pair(IMissionTarget target, IMissionObjective objective)
        {
            Assert.That(objective.Kind, Is.EqualTo(target.Kind));
            var proposal = new MissionProposal { Kind = target.Kind, Target = target };
            var intent = new MissionIntent { Kind = target.Kind, Objective = objective };
            Assert.That(MissionIntentKey.For(intent), Is.EqualTo(MissionIntentKey.For(proposal)),
                $"{target.Kind}: the durable objective must keep its proposal's identity");
            Assert.That(StableMissionKey.For(proposal).Kind, Is.EqualTo(target.Kind));
        }
    }
}
#endif
