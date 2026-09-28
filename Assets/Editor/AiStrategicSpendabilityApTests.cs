#if UNITY_INCLUDE_TESTS
using System.Collections.Generic;
using System.Linq;
using Game.Ai;
using Game.Ai.V2;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Units;
using NUnit.Framework;
using UnityEngine;

namespace Game.EditorTests
{
    public class AiStrategicSpendabilityApTests
    {
        [TearDown]
        public void TearDown()
        {
            StrategicResourceReservationLedger.ClearAll();
            AiAllocatorStateRegistry.Clear();
            OperationContinuationWindow.ClearAll();
            MissionIntentRegistry.Clear();
            ArmyRegistry.Clear();
        }

        // A started Raid's primary (activation 3, 2 MP left) about to take its next Assault step.
        private static (MissionIntent intent, ArmyData army) StartedRaid(PlayerSetupData player)
        {
            var army = new ArmyData { Owner = player, Hex = new HexCoord(1, 0) };
            army.Members.Add(new UnitData
            {
                Owner = player, ActivationApCost = 3, MoveMax = 2, MoveCurrent = 2,
            });
            ArmyRegistry.Register(army);
            var intent = new MissionIntent
            {
                Kind = MissionKind.Raid,
                Status = IntentStatus.Active,
                Funding = CommitmentTier.Hard,
                Objective = new RaidIntent
                {
                    Phase = RaidMissionPhase.Assault, OperationStarted = true,
                    PrimaryArmyId = army.Id,
                },
            };
            intent.IntentKey = MissionIntentKey.For(intent);
            MissionIntentRegistry.GetOrCreate(player).Put(intent);
            return (intent, army);
        }

        // Variant A — Phase A card play runs before the allocator funds commitments, so the next
        // step of a Hard operation owns its unpaid activation against every card spend.
        [Test]
        public void HardOperationContinuation_ProtectsItsNextStepFromCardPlay()
        {
            var player = new PlayerSetupData();
            var rootObject = new GameObject("hard-operation-continuation-test");
            try
            {
                PlayerRoot root = rootObject.AddComponent<PlayerRoot>();
                root.ActionPoints = 5;
                var ctx = new AiTurnContext { TurnNumber = 21 };
                var plan = new MaterializationPlan { ApCost = 3f };
                (MissionIntent intent, ArmyData army) = StartedRaid(player);

                Assert.That(StrategicSpendability.SpendableAp(player, root, ctx), Is.EqualTo(2f));
                Assert.That(StrategicSpendability.ReservesOkAfterChain(root, ctx, plan, player),
                    Is.False, "a 3 AP card would strand the assault's 3 AP activation");

                intent.Funding = CommitmentTier.Soft;
                Assert.That(StrategicSpendability.SpendableAp(player, root, ctx), Is.EqualTo(5f),
                    "a Soft commitment has not started and owns no protection");
                intent.Funding = CommitmentTier.Hard;

                intent.Raid.Phase = RaidMissionPhase.Return;
                Assert.That(StrategicSpendability.SpendableAp(player, root, ctx), Is.EqualTo(5f),
                    "a lifecycle leg carries no operation value and owns no protection");
                intent.Raid.Phase = RaidMissionPhase.Assault;

                army.Members[0].MoveCurrent = 0;
                Assert.That(StrategicSpendability.SpendableAp(player, root, ctx), Is.EqualTo(5f),
                    "an army that cannot move this turn has no step to protect");
                army.Members[0].MoveCurrent = 2;

                root.ActionPoints = 2;
                Assert.That(StrategicSpendability.SpendableAp(player, root, ctx), Is.EqualTo(2f),
                    "an unaffordable continuation never freezes the pool");
                root.ActionPoints = 5;

                OperationContinuationWindow.Settle(player, ctx.TurnNumber);
                Assert.That(StrategicSpendability.SpendableAp(player, root, ctx), Is.EqualTo(5f),
                    "after the mission loop settles, Phase B sees every AP nobody will spend");
                Assert.That(OperationContinuationWindow.IsSettled(player, ctx.TurnNumber + 1),
                    Is.False, "the settle mark is turn-stamped");
            }
            finally
            {
                Object.DestroyImmediate(rootObject);
            }
        }

        [TestCase(StrategicReservationReason.EconomyBuildCompletion)]
        [TestCase(StrategicReservationReason.StrategicReactionPass)]
        public void OtherOwnerApHold_BlocksMissionAtAllocatorAdmission(
            StrategicReservationReason reason)
        {
            var player = new PlayerSetupData();
            const int turn = 13;
            StrategicResourceReservationLedger.BeginTurn(player, turn);
            StrategicResourceReservationLedger.Upsert(player, turn,
                new StrategicResourceReservation
                {
                    Owner = "Economy:build",
                    Reason = reason,
                    Resource = StrategicReservedResource.ActionPoints,
                    Amount = 4f,
                    ExpirationStage = reason == StrategicReservationReason.StrategicReactionPass
                        ? StrategicReservationExpiry.EndOfReaction
                        : StrategicReservationExpiry.EndOfTurn,
                });

            var scout = new MissionProposal
            {
                Kind = MissionKind.Scout,
                Target = new ScoutMissionTarget { Kind = ScoutTargetKind.Explore },
                BaseValue = 10f,
                EffectiveValue = 10f,
                Requirements = new MissionRequirements
                {
                    ApMinimum = 3f, ApDesired = 3f, ApMaximum = 3f,
                },
            };
            scout.Axes.Value[DesireAxis.Recon] = 1f;
            var snap = new WorldSnapshot
            {
                TurnNumber = turn,
                Self = new SelfSnapshot { ActionPoints = 5 },
            };

            TentativeAllocation allocation = ResourceAllocator.BeginTurn(snap, Radar.Even(),
                new List<MissionProposal> { scout }, new List<Commitment>(), player).Pack();

            Assert.That(StrategicResourceReservationLedger.SpendableAp(player, turn, 5f),
                Is.EqualTo(1f));
            Assert.That(StrategicResourceReservationLedger.SpendableExcludingOwner(player, turn,
                    StrategicReservedResource.ActionPoints, 5f, "Economy:build"), Is.EqualTo(5f),
                "the build must still be able to spend its own reservation");
            Assert.That(allocation.Funded.Select(f => f.Mission), Has.No.Member(scout));
            Assert.That(allocation.Deferred.Any(d => d.Mission == scout
                && d.Reason == DeferReason.InsufficientBudget), Is.True);

            Assert.That(StrategicResourceReservationLedger.ReleaseByOwner(
                player, turn, "Economy:build"), Is.True);
            TentativeAllocation released = ResourceAllocator.BeginTurn(snap, Radar.Even(),
                new List<MissionProposal> { scout }, new List<Commitment>(), player).Pack();
            Assert.That(released.Funded.Select(f => f.Mission), Has.Member(scout),
                "the allocator must read the current ledger, not cache an expired hold");
        }

        private static MissionProposal AirScout(float energy)
        {
            var scout = new MissionProposal
            {
                Kind = MissionKind.Scout,
                Target = new ScoutMissionTarget { Kind = ScoutTargetKind.Explore },
                BaseValue = 10f,
                EffectiveValue = 10f,
                Requirements = new MissionRequirements
                {
                    ApMinimum = 1f, ApDesired = 1f, ApMaximum = 1f,
                    EnergyMinimum = energy, EnergyDesired = energy, EnergyMaximum = energy,
                },
            };
            scout.Axes.Value[DesireAxis.Recon] = 1f;
            return scout;
        }

        // One physical pool for every mission kind: a non-Economy Energy draw is funded from the
        // same owner-aware spendable stock its Provisioning gate (AirSpendableEnergyLeft) uses,
        // so it never takes Energy another axis holds.
        // Ysolde T3 (2026-09-28 log): three re-admission passes each protected a different Base
        // site and all three deferred holds stacked. Switching the single pre-intent hold keeps
        // only the new owner's deferred rows and never touches a completion hold.
        [Test]
        public void SwitchingDeferredEconomyHold_ReleasesPreviousOwnerOnly()
        {
            var player = new PlayerSetupData();
            const int turn = 17;
            StrategicResourceReservationLedger.BeginTurn(player, turn);
            void Hold(string owner, StrategicReservationReason reason) =>
                StrategicResourceReservationLedger.Upsert(player, turn,
                    new StrategicResourceReservation
                    {
                        Owner = owner, Reason = reason,
                        Resource = StrategicReservedResource.Human, Amount = 3f,
                        ExpirationStage = StrategicReservationExpiry.EndOfTurn,
                    });
            Hold("Economy:siteA", StrategicReservationReason.EconomyDeferredBuild);
            Hold("Economy:siteB", StrategicReservationReason.EconomyDeferredBuild);
            Hold("Economy:done", StrategicReservationReason.EconomyBuildCompletion);

            StrategicResourceReservationLedger.ReleaseReasonExceptOwner(player, turn,
                StrategicReservationReason.EconomyDeferredBuild, "Economy:siteB");

            Assert.That(StrategicResourceReservationLedger.HasOwnerReason(player, turn,
                "Economy:siteA", StrategicReservationReason.EconomyDeferredBuild), Is.False);
            Assert.That(StrategicResourceReservationLedger.HasOwnerReason(player, turn,
                "Economy:siteB", StrategicReservationReason.EconomyDeferredBuild), Is.True);
            Assert.That(StrategicResourceReservationLedger.HasOwnerReason(player, turn,
                "Economy:done", StrategicReservationReason.EconomyBuildCompletion), Is.True);
            Assert.That(StrategicResourceReservationLedger.Active(player, turn,
                StrategicReservedResource.Human), Is.EqualTo(6f));
        }

        [Test]
        public void OtherAxisPhysicalHold_BlocksNonEconomyMissionAtAllocatorAdmission()
        {
            var player = new PlayerSetupData();
            const int turn = 15;
            StrategicResourceReservationLedger.BeginTurn(player, turn);
            StrategicResourceReservationLedger.Upsert(player, turn,
                new StrategicResourceReservation
                {
                    Owner = "Economy:other",
                    Reason = StrategicReservationReason.EconomyDeferredBuild,
                    Resource = StrategicReservedResource.Energy,
                    Amount = 3f,
                    ExpirationStage = StrategicReservationExpiry.EndOfTurn,
                });
            MissionProposal scout = AirScout(2f);
            var snap = new WorldSnapshot
            {
                TurnNumber = turn,
                Self = new SelfSnapshot
                {
                    ActionPoints = 5,
                    Stockpile = new ResourceBundle { Energy = 4f },
                },
            };

            TentativeAllocation held = ResourceAllocator.BeginTurn(snap, Radar.Even(),
                new List<MissionProposal> { scout }, new List<Commitment>(), player).Pack();
            Assert.That(held.Funded.Select(f => f.Mission), Has.No.Member(scout));
            Assert.That(held.Deferred.Any(d => d.Mission == scout
                && d.Reason == DeferReason.InsufficientPhysical), Is.True,
                "4 Energy minus a 3 Energy hold leaves 1 for a 2 Energy sortie");

            StrategicResourceReservationLedger.ReleaseByOwner(player, turn, "Economy:other");
            TentativeAllocation released = ResourceAllocator.BeginTurn(snap, Radar.Even(),
                new List<MissionProposal> { scout }, new List<Commitment>(), player).Pack();
            Assert.That(released.Funded.Select(f => f.Mission), Has.Member(scout));
        }

        // A hold its own Economy mission already drew in this pack is not subtracted a second
        // time for the candidates behind it.
        [Test]
        public void FundedBuildPhysicalHold_IsCreditedOnceForOtherAxis()
        {
            var player = new PlayerSetupData();
            const int turn = 16;
            var build = new MissionProposal
            {
                Kind = MissionKind.Economy,
                Target = new EconomyMissionTarget
                {
                    Kind = EconomyTaskKind.BuildExtraction,
                    TargetHex = new HexCoord(3, 0),
                },
                BaseValue = 20f,
                EffectiveValue = 20f,
                Requirements = new MissionRequirements
                {
                    ApMinimum = 1f, ApDesired = 1f, ApMaximum = 1f,
                    EnergyMinimum = 3f, EnergyDesired = 3f, EnergyMaximum = 3f,
                },
            };
            build.Axes.Value[DesireAxis.Economy] = 1f;
            MissionProposal scout = AirScout(1f);
            StrategicResourceReservationLedger.BeginTurn(player, turn);
            StrategicResourceReservationLedger.Upsert(player, turn,
                new StrategicResourceReservation
                {
                    Owner = EconomyMissionPlanner.OwnerKey(StableMissionKey.For(build)),
                    Reason = StrategicReservationReason.EconomyBuildCompletion,
                    Resource = StrategicReservedResource.Energy,
                    Amount = 3f,
                    ExpirationStage = StrategicReservationExpiry.EndOfTurn,
                });
            var snap = new WorldSnapshot
            {
                TurnNumber = turn,
                Self = new SelfSnapshot
                {
                    ActionPoints = 5,
                    Stockpile = new ResourceBundle { Energy = 4f },
                },
            };

            TentativeAllocation allocation = ResourceAllocator.BeginTurn(snap, Radar.Even(),
                new List<MissionProposal> { build, scout }, new List<Commitment>(), player).Pack();
            Assert.That(allocation.Funded.Select(f => f.Mission), Has.Member(build));
            Assert.That(allocation.Funded.Select(f => f.Mission), Has.Member(scout),
                "the build's 3 Energy is drawn once; the remaining 1 Energy is free for the sortie");
        }

        [TestCase(false)]
        [TestCase(true)]
        public void CompletionApHold_FundsItsOwnBuildAndLeavesOnlyUnreservedApForRecon(
            bool committedBuild)
        {
            var player = new PlayerSetupData();
            const int turn = 14;
            var build = new MissionProposal
            {
                Kind = MissionKind.Economy,
                Target = new EconomyMissionTarget
                {
                    Kind = EconomyTaskKind.BuildExtraction,
                    TargetHex = new HexCoord(3, 0),
                },
                BaseValue = 20f,
                EffectiveValue = 20f,
                Requirements = new MissionRequirements
                    { ApMinimum = 4f, ApDesired = 4f, ApMaximum = 4f },
            };
            build.Axes.Value[DesireAxis.Economy] = 1f;
            var scout = new MissionProposal
            {
                Kind = MissionKind.Scout,
                Target = new ScoutMissionTarget { Kind = ScoutTargetKind.Explore },
                BaseValue = 10f,
                EffectiveValue = 10f,
                Requirements = new MissionRequirements
                    { ApMinimum = 1f, ApDesired = 3f, ApMaximum = 5f },
            };
            scout.Axes.Value[DesireAxis.Recon] = 1f;
            StrategicResourceReservationLedger.BeginTurn(player, turn);
            StrategicResourceReservationLedger.Upsert(player, turn,
                new StrategicResourceReservation
                {
                    Owner = EconomyMissionPlanner.OwnerKey(StableMissionKey.For(build)),
                    Reason = StrategicReservationReason.EconomyBuildCompletion,
                    Resource = StrategicReservedResource.ActionPoints,
                    Amount = 4f,
                    ExpirationStage = StrategicReservationExpiry.EndOfTurn,
                });
            var snap = new WorldSnapshot
                { TurnNumber = turn, Self = new SelfSnapshot { ActionPoints = 5 } };

            TentativeAllocation allocation = ResourceAllocator.BeginTurn(snap, Radar.Even(),
                committedBuild ? new List<MissionProposal> { scout }
                    : new List<MissionProposal> { scout, build },
                committedBuild ? new List<Commitment> { new Commitment { Mission = build } }
                    : new List<Commitment>(), player).Pack();

            Assert.That(allocation.Funded.Single(f => f.Mission == build).Tentative.Ap,
                Is.EqualTo(4f), "an Economy build can consume its own completion reservation");
            Assert.That(allocation.Funded.Single(f => f.Mission == scout).Tentative.Ap,
                Is.EqualTo(1f), "the build's funded AP must not be subtracted a second time");
        }

        [Test]
        public void CompletionApHold_BlocksRemainderTopUpForOtherAxis()
        {
            var player = new PlayerSetupData();
            const int turn = 15;
            StrategicResourceReservationLedger.BeginTurn(player, turn);
            StrategicResourceReservationLedger.Upsert(player, turn,
                new StrategicResourceReservation
                {
                    Owner = "Economy:pending",
                    Reason = StrategicReservationReason.EconomyBuildCompletion,
                    Resource = StrategicReservedResource.ActionPoints,
                    Amount = 4f,
                    ExpirationStage = StrategicReservationExpiry.EndOfTurn,
                });
            var scout = new MissionProposal
            {
                Kind = MissionKind.Scout,
                Target = new ScoutMissionTarget { Kind = ScoutTargetKind.Explore },
                BaseValue = 10f,
                EffectiveValue = 10f,
                Requirements = new MissionRequirements
                    { ApMinimum = 1f, ApDesired = 3f, ApMaximum = 5f },
            };
            scout.Axes.Value[DesireAxis.Recon] = 1f;
            var snap = new WorldSnapshot
                { TurnNumber = turn, Self = new SelfSnapshot { ActionPoints = 5 } };

            TentativeAllocation allocation = ResourceAllocator.BeginTurn(snap, Radar.Even(),
                new List<MissionProposal> { scout }, new List<Commitment>(), player).Pack();

            Assert.That(allocation.Funded.Single(f => f.Mission == scout).Tentative.Ap,
                Is.EqualTo(1f), "the remainder pass cannot spend another mission's AP hold");
        }

        [Test]
        public void ReactionRoundRelease_OpensItsBudgetWithoutReleasingEconomyCompletion()
        {
            var player = new PlayerSetupData();
            const int turn = 16;
            StrategicResourceReservationLedger.BeginTurn(player, turn);
            StrategicResourceReservationLedger.Upsert(player, turn,
                new StrategicResourceReservation
                {
                    Owner = "reaction:round",
                    Reason = StrategicReservationReason.StrategicReactionPass,
                    Resource = StrategicReservedResource.ActionPoints,
                    Amount = 3f,
                    ExpirationStage = StrategicReservationExpiry.EndOfReaction,
                });
            StrategicResourceReservationLedger.Upsert(player, turn,
                new StrategicResourceReservation
                {
                    Owner = "Economy:build",
                    Reason = StrategicReservationReason.EconomyBuildCompletion,
                    Resource = StrategicReservedResource.ActionPoints,
                    Amount = 2f,
                    ExpirationStage = StrategicReservationExpiry.EndOfTurn,
                });
            var scout = new MissionProposal
            {
                Kind = MissionKind.Scout,
                Target = new ScoutMissionTarget { Kind = ScoutTargetKind.Explore },
                BaseValue = 10f,
                EffectiveValue = 10f,
                Requirements = new MissionRequirements
                    { ApMinimum = 3f, ApDesired = 3f, ApMaximum = 3f },
            };
            scout.Axes.Value[DesireAxis.Recon] = 1f;
            var snap = new WorldSnapshot
                { TurnNumber = turn, Self = new SelfSnapshot { ActionPoints = 5 } };

            TentativeAllocation before = ResourceAllocator.BeginTurn(snap, Radar.Even(),
                new List<MissionProposal> { scout }, new List<Commitment>(), player).Pack();
            Assert.That(before.Funded, Is.Empty);

            // ReactionRoundExecutor releases this reason before its Phase A and mission pass.
            StrategicResourceReservationLedger.ReleaseByReason(player, turn,
                StrategicReservationReason.StrategicReactionPass);
            TentativeAllocation during = ResourceAllocator.BeginTurn(snap, Radar.Even(),
                new List<MissionProposal> { scout }, new List<Commitment>(), player).Pack();
            Assert.That(during.Funded.Select(f => f.Mission), Has.Member(scout));
            Assert.That(StrategicResourceReservationLedger.SpendableAp(player, turn, 5f),
                Is.EqualTo(3f), "the Economy owner's two AP remain protected");
        }

        [Test]
        public void MaterializationGuard_UsesSpendableApAndReleasesItWhenReservationEnds()
        {
            var owner = new PlayerSetupData();
            var rootObject = new GameObject("strategic-spendability-ap-test");
            try
            {
                PlayerRoot root = rootObject.AddComponent<PlayerRoot>();
                root.ActionPoints = 5;
                var ctx = new AiTurnContext { TurnNumber = 13 };
                var plan = new MaterializationPlan { ApCost = 3f };
                StrategicResourceReservationLedger.BeginTurn(owner, ctx.TurnNumber);
                StrategicResourceReservationLedger.Upsert(owner, ctx.TurnNumber,
                    new StrategicResourceReservation
                    {
                        Owner = "other-operation",
                        Reason = StrategicReservationReason.StrategicReactionPass,
                        Resource = StrategicReservedResource.ActionPoints,
                        Amount = 4f,
                        ExpirationStage = StrategicReservationExpiry.EndOfReaction,
                    });

                Assert.That(StrategicSpendability.ReservesOkAfterChain(root, ctx, plan, owner), Is.False,
                    "five physical AP are not three spendable AP while four belong to another operation");
                Assert.That(StrategicResourceReservationLedger.ReleaseByOwner(
                    owner, ctx.TurnNumber, "other-operation"), Is.True);
                Assert.That(StrategicSpendability.ReservesOkAfterChain(root, ctx, plan, owner), Is.True,
                    "releasing the owning operation restores the normal AP eligibility");
            }
            finally
            {
                Object.DestroyImmediate(rootObject);
            }
        }
    }
}
#endif
