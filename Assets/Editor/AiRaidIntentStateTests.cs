#if UNITY_INCLUDE_TESTS
using System.Collections.Generic;
using System.Linq;
using Game.Ai.V2;
using Game.HexGrid;
using NUnit.Framework;

namespace Game.EditorTests
{
    public sealed class AiRaidIntentStateTests
    {
        [Test]
        public void PhaseTransition_ClearsReinforcementRequestAge()
        {
            var raid = new RaidIntent();
            raid.Phase = RaidMissionPhase.Reinforcement;
            raid.ReinforcementRequestedTurn = 7;

            raid.Phase = RaidMissionPhase.Assault;

            Assert.That(raid.ReinforcementRequestedTurn, Is.EqualTo(-1));
        }

        [Test]
        public void RepeatedReinforcement_WithoutSupport_StartsFreshRequestCycle()
        {
            var raid = new RaidIntent();
            raid.Phase = RaidMissionPhase.Reinforcement;
            raid.ReinforcementRequestedTurn = 7;

            raid.Phase = RaidMissionPhase.Reinforcement;

            Assert.That(raid.ReinforcementRequestedTurn, Is.EqualTo(-1));
        }

        [Test]
        public void RepeatedReinforcement_WithBoundSupport_PreservesCurrentRequestAge()
        {
            var raid = new RaidIntent();
            raid.Phase = RaidMissionPhase.Reinforcement;
            raid.SupportArmyId = 42;
            raid.ReinforcementRequestedTurn = 7;

            raid.Phase = RaidMissionPhase.Reinforcement;

            Assert.That(raid.ReinforcementRequestedTurn, Is.EqualTo(7));
        }

        [Test]
        public void SupportReturnTransition_ClearsReinforcementRequestAge()
        {
            var raid = new RaidIntent();
            raid.Phase = RaidMissionPhase.Reinforcement;
            raid.SupportArmyId = 42;
            raid.ReinforcementRequestedTurn = 7;

            raid.Phase = RaidMissionPhase.SupportReturn;

            Assert.That(raid.ReinforcementRequestedTurn, Is.EqualTo(-1));
        }

        [Test]
        public void SupportReturnToReinforcement_CannotReviveOldRequestAge()
        {
            var raid = new RaidIntent();
            raid.Phase = RaidMissionPhase.Reinforcement;
            raid.SupportArmyId = 42;
            raid.ReinforcementRequestedTurn = 7;
            raid.Phase = RaidMissionPhase.SupportReturn;
            raid.SupportArmyId = null;

            raid.Phase = RaidMissionPhase.Reinforcement;

            Assert.That(raid.ReinforcementRequestedTurn, Is.EqualTo(-1));
        }

        [Test]
        public void SupportReturn_TargetSatisfiedDuringProvisioning_DoesNotCompleteDurableRaid()
        {
            var proposal = new MissionProposal
            {
                Kind = MissionKind.Raid,
                Target = new RaidMissionTarget
                {
                    Phase = RaidMissionPhase.SupportReturn,
                    PrimaryArmyId = 11,
                    SupportArmyId = 22,
                    DestinationHex = new HexCoord(3, 4),
                    Target = RaidTargetRef.ForNeutralArmy(99),
                    LastKnownHex = new HexCoord(8, 1),
                    TargetIsNeutral = true,
                },
            };
            var ledger = new MissionOutcomeLedger();
            ledger.RegisterProposals(new List<MissionProposal> { proposal });
            ledger.RecordProvisionFailure(proposal,
                ProvisionFailure.TargetSatisfied("support already home"));

            MissionTurnOutcome outcome = ledger.Finalize().Single();

            Assert.That(outcome.Outcome, Is.EqualTo(ExecutionOutcome.ProductiveStop));
            Assert.That(outcome.MadeProgress, Is.True);
            Assert.That(outcome.ObjectiveSatisfied, Is.False);
        }

        [Test]
        public void PrimaryReturn_TargetSatisfiedDuringProvisioning_StillCompletesRaid()
        {
            var proposal = new MissionProposal
            {
                Kind = MissionKind.Raid,
                Target = new RaidMissionTarget
                {
                    Phase = RaidMissionPhase.Return,
                    PrimaryArmyId = 11,
                    DestinationHex = new HexCoord(3, 4),
                    Target = RaidTargetRef.ForNeutralArmy(99),
                    LastKnownHex = new HexCoord(8, 1),
                    TargetIsNeutral = true,
                },
            };
            var ledger = new MissionOutcomeLedger();
            ledger.RegisterProposals(new List<MissionProposal> { proposal });
            ledger.RecordProvisionFailure(proposal,
                ProvisionFailure.TargetSatisfied("primary already home"));

            MissionTurnOutcome outcome = ledger.Finalize().Single();

            Assert.That(outcome.Outcome, Is.EqualTo(ExecutionOutcome.Completed));
            Assert.That(outcome.ObjectiveSatisfied, Is.True);
        }
    }
}
#endif
