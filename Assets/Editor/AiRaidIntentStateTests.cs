#if UNITY_INCLUDE_TESTS
using System.Collections.Generic;
using System.Linq;
using Game.Ai.V2;
using Game.HexGrid;
using Game.Players;
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

        [Test]
        public void Return_DegradedPrimary_RemainsActiveAndClaimed()
        {
            var player = new PlayerSetupData { Nickname = "RaidReturnDegradedPrimary" };
            var baseHex = new HexCoord(0, 0);
            WorldSnapshot snap = SnapshotWithDegradedPrimary(player, primaryArmyId: 11, baseHex);
            MissionIntent intent = PutStartedRaid(player, primaryArmyId: 11,
                RaidMissionPhase.Return, baseHex);

            List<MissionIntent> active = MissionContinuityLayer.ResolveActive(player, snap);
            ActorCommitments commitments = ActorCommitments.FromIntents(active, snap, null);

            Assert.That(active, Does.Contain(intent));
            Assert.That(commitments.IsArmyClaimed(11), Is.True);
            Assert.That(MissionIntentRegistry.GetOrCreate(player).TryGet(intent.IntentKey, out _), Is.True);
        }

        [Test]
        public void Assault_DegradedPrimary_IsStillRetired()
        {
            var player = new PlayerSetupData { Nickname = "RaidAssaultDegradedPrimary" };
            var baseHex = new HexCoord(0, 0);
            WorldSnapshot snap = SnapshotWithDegradedPrimary(player, primaryArmyId: 11, baseHex);
            MissionIntent intent = PutStartedRaid(player, primaryArmyId: 11,
                RaidMissionPhase.Assault, returnHex: null);

            List<MissionIntent> active = MissionContinuityLayer.ResolveActive(player, snap);

            Assert.That(active, Does.Not.Contain(intent));
            Assert.That(MissionIntentRegistry.GetOrCreate(player).TryGet(intent.IntentKey, out _), Is.False);
        }

        private static WorldSnapshot SnapshotWithDegradedPrimary(PlayerSetupData player,
            int primaryArmyId, HexCoord baseHex)
        {
            var primary = new ArmySnapshot
            {
                ArmyId = primaryArmyId,
                Owner = player,
                Hex = new HexCoord(2, 0),
                MemberCount = 1,
                IsPrison = false,
                IsAir = false,
                IsStructuralRaidActor = false,
            };
            return new WorldSnapshot
            {
                TurnNumber = 5,
                Self = new SelfSnapshot
                {
                    Armies = new List<ArmySnapshot> { primary },
                },
                Known = new KnownSnapshot
                {
                    Buildings = new List<Game.Ai.AiMapMemory.KnownBuilding>
                    {
                        new Game.Ai.AiMapMemory.KnownBuilding(baseHex, player,
                            isStartingCitadel: true, isBase: true,
                            facilityAbilities: null, collectedAmounts: null, freeFacilitySlots: 0),
                    },
                },
            };
        }

        private static MissionIntent PutStartedRaid(PlayerSetupData player, int primaryArmyId,
            RaidMissionPhase phase, HexCoord? returnHex)
        {
            var intent = new MissionIntent
            {
                Kind = MissionKind.Raid,
                Funding = CommitmentTier.Hard,
                Status = IntentStatus.Active,
                Objective = new RaidIntent
                {
                    Target = RaidTargetRef.ForNeutralArmy(99),
                    TargetIsNeutral = true,
                    OperationStarted = true,
                    Phase = phase,
                    PrimaryArmyId = primaryArmyId,
                    ReturnHex = returnHex,
                },
            };
            intent.IntentKey = MissionIntentKey.For(intent);
            MissionIntentRegistry.GetOrCreate(player).Put(intent);
            return intent;
        }
    }
}
#endif
