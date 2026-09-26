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

        [Test]
        public void CompletionApHold_AllocatorAdmissionAndSpendabilityHaveDifferentPools()
        {
            var player = new PlayerSetupData();
            const int turn = 13;
            StrategicResourceReservationLedger.BeginTurn(player, turn);
            StrategicResourceReservationLedger.Upsert(player, turn,
                new StrategicResourceReservation
                {
                    Owner = "Economy:build",
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
            Assert.That(allocation.Funded.Select(f => f.Mission), Does.Contain(scout),
                "Characterizes the current mismatch: the common allocator funds a fresh task "
                + "from raw AP even while another owner's completion reservation protects it. "
                + "The test should change to rejection when admission honors this hold.");
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
