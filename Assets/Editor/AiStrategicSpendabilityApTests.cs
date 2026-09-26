#if UNITY_INCLUDE_TESTS
using System.Collections.Generic;
using System.Linq;
using Game.Ai;
using Game.Ai.V2;
using Game.Map;
using Game.Players;
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
            Assert.That(allocation.Funded.Select(f => f.Mission), Does.Not.Contain(scout));
            Assert.That(allocation.Deferred.Any(d => d.Mission == scout
                && d.Reason == DeferReason.InsufficientBudget), Is.True);

            Assert.That(StrategicResourceReservationLedger.ReleaseByOwner(
                player, turn, "Economy:build"), Is.True);
            TentativeAllocation released = ResourceAllocator.BeginTurn(snap, Radar.Even(),
                new List<MissionProposal> { scout }, new List<Commitment>(), player).Pack();
            Assert.That(released.Funded.Select(f => f.Mission), Does.Contain(scout),
                "the allocator must read the current ledger, not cache an expired hold");
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
