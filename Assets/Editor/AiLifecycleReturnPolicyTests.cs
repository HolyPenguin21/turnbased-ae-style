#if UNITY_INCLUDE_TESTS
using Game.Ai.V2;
using Game.HexGrid;
using NUnit.Framework;

namespace Game.EditorTests
{
    public class AiLifecycleReturnPolicyTests
    {
        private static readonly HexCoord Site = new HexCoord(3, 3);

        private static (MissionIntent intent, MissionProposal proposal) Economy(EconomyTaskKind kind)
        {
            var target = new EconomyMissionTarget { Kind = kind, TargetHex = Site, BuilderArmyId = 7 };
            var proposal = new MissionProposal { Kind = MissionKind.Economy, Target = target };
            var intent = new MissionIntent
            {
                Kind = MissionKind.Economy, Status = IntentStatus.Active,
                IntentKey = MissionIntentKey.For(proposal),
                Objective = new EconomyIntent { Kind = kind, TargetHex = Site, BuilderArmyId = 7 },
            };
            return (intent, proposal);
        }

        [Test]
        public void AReturnLegWaits_ARealTaskDoesNot()
        {
            var (returnIntent, returnProposal) = Economy(EconomyTaskKind.ReturnBuilder);
            var (buildIntent, buildProposal) = Economy(EconomyTaskKind.BuildExtraction);

            Assert.That(LifecycleReturnPolicy.IsDeferrableReturn(returnProposal, new[] { returnIntent }), Is.True);
            Assert.That(LifecycleReturnPolicy.IsDeferrableReturn(buildProposal, new[] { buildIntent }), Is.False);
            Assert.That(LifecycleReturnPolicy.IsDeferrableReturn(returnProposal, new MissionIntent[0]), Is.False,
                "no durable intent, nothing to keep waiting");
        }

        [Test]
        public void AnActiveDefenceWithdrawalNeverWaits()
        {
            var proposal = new MissionProposal { Kind = MissionKind.ActiveDefence };
            var intent = new MissionIntent
            {
                Kind = MissionKind.ActiveDefence, Status = IntentStatus.Active,
                IntentKey = MissionIntentKey.For(proposal),
                Objective = new ActiveDefenceIntent { Phase = ActiveDefencePhase.Return },
            };
            Assert.That(intent.IsLifecycleLeg, Is.True);
            Assert.That(LifecycleReturnPolicy.IsDeferrableReturn(proposal, new[] { intent }), Is.False);
        }

        [Test]
        public void AReturnNeverWaitsTwoTurnsInARow()
        {
            var player = new Game.Players.PlayerSetupData { Nickname = "Us", ColorIndex = 1 };
            var (_, proposal) = Economy(EconomyTaskKind.ReturnBuilder);
            MissionIntentKey key = MissionIntentKey.For(proposal);
            try
            {
                Assert.That(LifecycleReturnPolicy.MayWait(player, key, 5), Is.True);
                LifecycleReturnPolicy.RecordWait(player, key, 5);
                Assert.That(LifecycleReturnPolicy.MayWait(player, key, 5), Is.True, "later passes of the same turn");
                Assert.That(LifecycleReturnPolicy.MayWait(player, key, 6), Is.False, "waited last turn: goes now");
                Assert.That(LifecycleReturnPolicy.MayWait(player, key, 7), Is.True);
            }
            finally
            {
                LifecycleReturnPolicy.ClearAll();
            }
        }

        [Test]
        public void OnlyARealHomeThreatStopsTheWait()
        {
            WorldSnapshot Snap(float citadel, float baseSev, bool siege = false) => new WorldSnapshot
            {
                Threat = new ThreatModel
                {
                    CitadelThreatSeverity = citadel, BaseThreatSeverity = baseSev, UnderSiege = siege,
                },
            };
            Assert.That(LifecycleReturnPolicy.HomeThreatened(Snap(0.1f, 0f)), Is.False, "a far weak contact");
            Assert.That(LifecycleReturnPolicy.HomeThreatened(Snap(AiConfigV2.lifecycleReturnHomeThreatSeverity, 0f)), Is.True);
            Assert.That(LifecycleReturnPolicy.HomeThreatened(Snap(0f, 0.5f)), Is.True);
            Assert.That(LifecycleReturnPolicy.HomeThreatened(Snap(0f, 0f, siege: true)), Is.True);
        }
    }
}
#endif
