#if UNITY_INCLUDE_TESTS
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
