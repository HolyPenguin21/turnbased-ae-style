#if UNITY_INCLUDE_TESTS
using System.Collections.Generic;
using System.Linq;
using Game.Ai.V2;
using Game.HexGrid;
using Game.Map;
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
        public void Assault_CompletedEvent_DegradedPrimary_ReturnsWithoutChaining()
        {
            var player = new PlayerSetupData { Nickname = "RaidCompletedDegradedPrimary" };
            var baseHex = new HexCoord(0, 0);
            var targetHex = new HexCoord(5, 1);
            RaidTargetRef target = RaidTargetRef.ForEventGuard(targetHex);
            HexEventRegistry.Set(targetHex, null, null, null, null, null);
            try
            {
                HexEventRegistry.MarkConsumed(targetHex);
                Assert.That(RaidObjectiveEvaluator.IsObjectiveSatisfiedLive(player, target), Is.True);

                WorldSnapshot snap = SnapshotWithDegradedPrimary(player, primaryArmyId: 11, baseHex);
                MissionIntent intent = PutStartedRaid(player, primaryArmyId: 11,
                    RaidMissionPhase.Assault, returnHex: null, target: target);
                MissionIntentKey originalKey = intent.IntentKey;
                var next = new AggressionObjective
                {
                    Target = RaidTargetRef.ForNeutralArmy(101),
                    LastKnownHex = new HexCoord(7, 1),
                    TargetIsNeutral = true,
                    BaseValue = 10f,
                };

                List<MissionIntent> active = MissionContinuityLayer.ResolveActive(player, snap,
                    aggressionObjectives: new List<AggressionObjective> { next });
                ActorCommitments commitments = ActorCommitments.FromIntents(active, snap, null);

                Assert.That(active, Does.Contain(intent));
                Assert.That(intent.Raid.Phase, Is.EqualTo(RaidMissionPhase.Return));
                Assert.That(intent.Raid.ReturnHex, Is.EqualTo(baseHex));
                Assert.That(intent.Raid.PrimaryArmyId, Is.EqualTo(11));
                Assert.That(intent.Raid.Target, Is.EqualTo(target),
                    "continuity must never mutate a completed Raid into the next objective");
                Assert.That(intent.IntentKey, Is.EqualTo(originalKey));
                Assert.That(intent.Raid.CompletedTargetAwaitingFreshDecision, Is.True);
                Assert.That(intent.Funding, Is.EqualTo(CommitmentTier.None));
                Assert.That(commitments.IsArmyClaimed(11), Is.False);
                Assert.That(MissionIntentRegistry.GetOrCreate(player).TryGet(originalKey, out _), Is.True);
            }
            finally { HexEventRegistry.Clear(); }
        }

        [Test]
        public void Assault_DegradedPrimary_IsStillRetired()
        {
            var player = new PlayerSetupData { Nickname = "RaidAssaultDegradedPrimary" };
            var baseHex = new HexCoord(0, 0);
            var targetHex = new HexCoord(5, 2);
            HexEventRegistry.Set(targetHex, null, null, null, null, null);
            try
            {
                RaidTargetRef target = RaidTargetRef.ForEventGuard(targetHex);
                Assert.That(RaidObjectiveEvaluator.IsObjectiveSatisfiedLive(player, target), Is.False);
                WorldSnapshot snap = SnapshotWithDegradedPrimary(player, primaryArmyId: 11, baseHex);
                MissionIntent intent = PutStartedRaid(player, primaryArmyId: 11,
                    RaidMissionPhase.Assault, returnHex: null, target: target);

                List<MissionIntent> active = MissionContinuityLayer.ResolveActive(player, snap);

                Assert.That(active, Has.No.Member(intent));
                Assert.That(MissionIntentRegistry.GetOrCreate(player).TryGet(intent.IntentKey, out _), Is.False);
            }
            finally { HexEventRegistry.Clear(); }
        }

        [TestCase(true)]
        [TestCase(false)]
        public void Return_MissingOrEmptyPrimary_IsRetired(bool missing)
        {
            var player = new PlayerSetupData { Nickname = "RaidReturnMissingPrimary" + missing };
            var baseHex = new HexCoord(0, 0);
            WorldSnapshot snap = SnapshotWithDegradedPrimary(player, primaryArmyId: 11, baseHex);
            snap.Self.Armies = missing ? new List<ArmySnapshot>() : new List<ArmySnapshot>
            {
                new ArmySnapshot { ArmyId = 11, Owner = player, MemberCount = 0 },
            };
            MissionIntent intent = PutStartedRaid(player, primaryArmyId: 11,
                RaidMissionPhase.Return, baseHex);

            List<MissionIntent> active = MissionContinuityLayer.ResolveActive(player, snap);

            Assert.That(active, Has.No.Member(intent));
            Assert.That(MissionIntentRegistry.GetOrCreate(player).TryGet(intent.IntentKey, out _), Is.False);
        }

        [Test]
        public void StartedRaid_UnboundPrimary_IsRetired()
        {
            var player = new PlayerSetupData { Nickname = "RaidUnboundPrimary" };
            var baseHex = new HexCoord(0, 0);
            WorldSnapshot snap = SnapshotWithDegradedPrimary(player, primaryArmyId: 11, baseHex);
            MissionIntent intent = PutStartedRaid(player, primaryArmyId: 11,
                RaidMissionPhase.Return, baseHex);
            intent.Raid.PrimaryArmyId = null;

            List<MissionIntent> active = MissionContinuityLayer.ResolveActive(player, snap);

            Assert.That(active, Has.No.Member(intent));
            Assert.That(MissionIntentRegistry.GetOrCreate(player).TryGet(intent.IntentKey, out _), Is.False);
        }

        [TestCase(RaidMissionPhase.Reinforcement)]
        [TestCase(RaidMissionPhase.SupportReturn)]
        public void CombatPhases_DegradedPrimary_DoNotGetReturnEligibility(RaidMissionPhase phase)
        {
            var player = new PlayerSetupData { Nickname = "RaidCombatDegraded" + phase };
            var baseHex = new HexCoord(0, 0);
            var targetHex = new HexCoord(6, 2);
            HexEventRegistry.Set(targetHex, null, null, null, null, null);
            try
            {
                WorldSnapshot snap = SnapshotWithDegradedPrimary(player, primaryArmyId: 11, baseHex);
                if (phase == RaidMissionPhase.SupportReturn)
                    snap.Self.Armies = new List<ArmySnapshot>
                    {
                        snap.Self.Armies.Single(),
                        new ArmySnapshot
                        {
                            ArmyId = 22, Owner = player, Hex = new HexCoord(3, 0),
                            MemberCount = 1, IsPrison = false, IsAir = false,
                        },
                    };
                MissionIntent intent = PutStartedRaid(player, primaryArmyId: 11, phase,
                    returnHex: null, target: RaidTargetRef.ForEventGuard(targetHex));
                if (phase == RaidMissionPhase.SupportReturn)
                {
                    intent.Raid.SupportArmyId = 22;
                    intent.Raid.SupportReturnHex = baseHex;
                }

                List<MissionIntent> active = MissionContinuityLayer.ResolveActive(player, snap);

                Assert.That(active, Has.No.Member(intent));
                Assert.That(MissionIntentRegistry.GetOrCreate(player).TryGet(intent.IntentKey, out _), Is.False);
            }
            finally { HexEventRegistry.Clear(); }
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
                    // ATK §20/§48 — own-Base identity is the current-truth topology; Known.Buildings
                    // below stays the metadata memory holds about that same hex.
                    BaseHexes = new[] { baseHex },
                    Citadel = baseHex,
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
            RaidMissionPhase phase, HexCoord? returnHex, RaidTargetRef? target = null)
        {
            var intent = new MissionIntent
            {
                Kind = MissionKind.Raid,
                Funding = CommitmentTier.Hard,
                Status = IntentStatus.Active,
                Objective = new RaidIntent
                {
                    Target = target ?? RaidTargetRef.ForNeutralArmy(99),
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
