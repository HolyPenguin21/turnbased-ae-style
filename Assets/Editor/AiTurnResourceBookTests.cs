#if UNITY_INCLUDE_TESTS
using Game.Ai.V2;
using Game.Players;
using NUnit.Framework;

namespace Game.EditorTests
{
    // TurnResourceBook.Free is the one spendability formula; MayDrawOn is its one table.
    public class AiTurnResourceBookTests
    {
        private const StrategicReservedResource Ap = StrategicReservedResource.ActionPoints;
        private const StrategicReservedResource Materials = StrategicReservedResource.Materials;

        private static ResourceClaim Claim(string owner, ResourceClaimKind kind,
            StrategicReservedResource resource, float amount) =>
            new ResourceClaim(owner, kind, resource, amount);

        private static readonly ResourceClaim[] Claims =
        {
            Claim("buildA", ResourceClaimKind.EconomyDeferred, Materials, 3f),
            Claim("buildB", ResourceClaimKind.EconomyCompletion, Materials, 2f),
            Claim("buildB", ResourceClaimKind.EconomyCompletion, Ap, 2f),
            Claim("reaction", ResourceClaimKind.Reaction, Ap, 1f),
            Claim(TurnResourceBook.AirRecoveryOwner, ResourceClaimKind.AirRecovery, Ap, 1f),
            Claim(TurnResourceBook.OperationContinuationOwner,
                ResourceClaimKind.OperationContinuation, Ap, 2f),
        };

        [Test]
        public void NoAuthority_SeesEveryClaim()
        {
            Assert.That(TurnResourceBook.Free(10f, Claims, Materials, default), Is.EqualTo(5f));
            Assert.That(TurnResourceBook.Free(10f, Claims, Ap, default), Is.EqualTo(4f));
        }

        [Test]
        public void Owner_MayDrawOnItsOwnHoldOnly()
        {
            var buildB = new SpendAuthority("buildB", economyCompletesNow: false);

            Assert.That(TurnResourceBook.Free(10f, Claims, Materials, buildB), Is.EqualTo(7f));
            Assert.That(TurnResourceBook.Free(10f, Claims, Ap, buildB), Is.EqualTo(6f),
                "its own completion AP is usable; reaction, recovery and continuation are not");
        }

        [Test]
        public void EconomyCompletingNow_MayDrawOnOtherBuildsDeferredHolds()
        {
            var completesNow = new SpendAuthority("buildB", economyCompletesNow: true);

            Assert.That(TurnResourceBook.Free(10f, Claims, Materials, completesNow), Is.EqualTo(10f));
            Assert.That(TurnResourceBook.Free(10f, Claims, Ap, completesNow), Is.EqualTo(6f),
                "seniority over deferred holds grants nothing over reaction or recovery claims");
        }

        [Test]
        public void EconomyCompletingNow_DoesNotDrawOnAnotherBuildsCompletion()
        {
            var otherBuild = new SpendAuthority("buildC", economyCompletesNow: true);

            Assert.That(TurnResourceBook.Free(10f, Claims, Materials, otherBuild), Is.EqualTo(8f));
        }

        [Test]
        public void Free_NeverGoesNegative()
        {
            Assert.That(TurnResourceBook.Free(1f, Claims, Ap, default), Is.Zero);
        }

        [Test]
        public void KindOf_MapsEveryLedgerReason()
        {
            Assert.That(TurnResourceBook.KindOf(StrategicReservationReason.EconomyDeferredBuild),
                Is.EqualTo(ResourceClaimKind.EconomyDeferred));
            Assert.That(TurnResourceBook.KindOf(StrategicReservationReason.EconomyBuildCompletion),
                Is.EqualTo(ResourceClaimKind.EconomyCompletion));
            Assert.That(TurnResourceBook.KindOf(StrategicReservationReason.StrategicReactionPass),
                Is.EqualTo(ResourceClaimKind.Reaction));
        }

        [Test]
        public void Claims_ListLedgerRowsWithTheirKind()
        {
            var player = new PlayerSetupData();
            const int turn = 3;
            StrategicResourceReservationLedger.BeginTurn(player, turn);
            StrategicResourceReservationLedger.Upsert(player, turn, new StrategicResourceReservation
            {
                Owner = "buildA", Reason = StrategicReservationReason.EconomyDeferredBuild,
                Resource = Materials, Amount = 4f,
                ExpirationStage = StrategicReservationExpiry.EndOfTurn,
            });

            // Materials only: no live AP/Energy obligation scan, so no engine objects are needed.
            var claims = TurnResourceBook.Claims(player, root: null,
                new Game.Ai.AiTurnContext { TurnNumber = turn }, Materials);

            Assert.That(claims, Has.Count.EqualTo(1));
            Assert.That(claims[0].Kind, Is.EqualTo(ResourceClaimKind.EconomyDeferred));
            Assert.That(claims[0].Owner, Is.EqualTo("buildA"));
            Assert.That(claims[0].Amount, Is.EqualTo(4f));
        }
    }
}
#endif
