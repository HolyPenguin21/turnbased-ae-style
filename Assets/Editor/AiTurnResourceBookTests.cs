#if UNITY_INCLUDE_TESTS
using System.Linq;
using Game.Ai.V2;
using Game.Cards;
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
            Claim(TurnResourceBook.OperationContinuationOwner,
                ResourceClaimKind.OperationContinuation, Ap, 2f),
        };

        [Test]
        public void LedgerWriteDetachesCallerAndReadDetachesInspection()
        {
            var player = new PlayerSetupData();
            const int turn = 7;
            var row = new StrategicResourceReservation
            {
                Owner = "buildA", Reason = StrategicReservationReason.EconomyBuildCompletion,
                Resource = Materials, Amount = 3f,
                ExpirationStage = StrategicReservationExpiry.EndOfTurn,
            };
            try
            {
                StrategicResourceReservationLedger.Upsert(player, turn, row);
                row.Amount = 9f;
                row.Owner = "other";
                var read = StrategicResourceReservationLedger.Rows(player, turn);
                Assert.That(read[0].Owner, Is.EqualTo("buildA"));
                Assert.That(read[0].Amount, Is.EqualTo(3f));
                read[0].Amount = 20f;
                Assert.That(TurnResourceBook.Free(10f,
                    TurnResourceBook.LedgerClaims(player, turn), Materials, default), Is.EqualTo(7f));
            }
            finally { StrategicResourceReservationLedger.ClearAll(); }
        }

        [TestCase(StrategicReservedResource.ActionPoints)]
        [TestCase(StrategicReservedResource.Human)]
        [TestCase(StrategicReservedResource.Energy)]
        [TestCase(StrategicReservedResource.Materials)]
        [TestCase(StrategicReservedResource.Tech)]
        public void BookReadsUpdatedHoldAndReleaseImmediately(StrategicReservedResource resource)
        {
            var player = new PlayerSetupData();
            const int turn = 8;
            var request = new StrategicResourceReservation
            {
                Owner = "build", Reason = StrategicReservationReason.EconomyBuildCompletion,
                Resource = resource, Amount = 3f, ExpirationStage = StrategicReservationExpiry.EndOfTurn,
            };
            try
            {
                float Free(SpendAuthority authority = default) => TurnResourceBook.Free(10f,
                    TurnResourceBook.LedgerClaims(player, turn), resource, authority);
                StrategicResourceReservationLedger.Upsert(player, turn, request);
                Assert.That(Free(), Is.EqualTo(7f));
                Assert.That(Free(new SpendAuthority("build", false)), Is.EqualTo(10f));
                request.Amount = 5f;
                Assert.That(Free(), Is.EqualTo(7f), "editing the request is not a bank write");
                StrategicResourceReservationLedger.Upsert(player, turn, request);
                Assert.That(Free(), Is.EqualTo(5f));
                StrategicResourceReservationLedger.ReleaseByOwner(player, turn, "build");
                Assert.That(Free(), Is.EqualTo(10f));
            }
            finally { StrategicResourceReservationLedger.ClearAll(); }
        }

        [Test]
        public void RepeatedReactionReplacesTheEnvelopeAndReleasesThePreviousOwner()
        {
            var player = new PlayerSetupData();
            const int turn = 9;
            StrategicReactionOpportunity Choice(string owner, float ap, ResourceCost envelope) =>
                new StrategicReactionOpportunity(true, owner, "RespondToDiscovery", ap,
                    envelope, default, "test", null);
            try
            {
                StrategicPhaseB.RefreshReactionReservation(player, turn,
                    Choice("old", 3f, new ResourceCost(energy: 4)));
                StrategicPhaseB.RefreshReactionReservation(player, turn,
                    Choice("new", 2f, new ResourceCost(materials: 2)));
                Assert.That(StrategicResourceReservationLedger.Active(player, turn,
                    StrategicReservedResource.Energy), Is.Zero);
                Assert.That(StrategicResourceReservationLedger.Active(player, turn,
                    StrategicReservedResource.Materials), Is.EqualTo(2f));
                Assert.That(StrategicResourceReservationLedger.Active(player, turn,
                    StrategicReservedResource.ActionPoints), Is.EqualTo(2f));
                Assert.That(StrategicResourceReservationLedger.Rows(player, turn)
                    .All(r => r.Owner == "new"), Is.True);

                StrategicPhaseB.RefreshReactionReservation(player, turn,
                    Choice("new", 1f, null));
                Assert.That(StrategicResourceReservationLedger.Active(player, turn,
                    StrategicReservedResource.Materials), Is.Zero);
                Assert.That(StrategicResourceReservationLedger.Active(player, turn,
                    StrategicReservedResource.ActionPoints), Is.EqualTo(1f));
                StrategicPhaseB.RefreshReactionReservation(player, turn,
                    Choice("new", 1f, null));
                Assert.That(StrategicResourceReservationLedger.Rows(player, turn), Has.Count.EqualTo(1),
                    "an identical re-probe must not stack new rows");
            }
            finally { StrategicResourceReservationLedger.ClearAll(); }
        }

        [Test]
        public void UpsertReleaseAndExpiryAreIsolatedByOwnerPlayerAndTurn()
        {
            var a = new PlayerSetupData();
            var b = new PlayerSetupData();
            const int turn = 7;
            try
            {
                StrategicResourceReservation Row(string owner, float amount) => new StrategicResourceReservation
                {
                    Owner = owner, Reason = StrategicReservationReason.StrategicReactionPass,
                    Resource = Materials, Amount = amount,
                    ExpirationStage = StrategicReservationExpiry.EndOfReaction,
                };
                StrategicResourceReservationLedger.Upsert(a, turn, Row("one", 2f));
                StrategicResourceReservationLedger.Upsert(a, turn, Row("one", 3f));
                StrategicResourceReservationLedger.Upsert(a, turn, Row("two", 4f));
                StrategicResourceReservationLedger.Upsert(b, turn, Row("one", 5f));
                Assert.That(StrategicResourceReservationLedger.Rows(a, turn), Has.Count.EqualTo(2));
                Assert.That(StrategicResourceReservationLedger.Active(a, turn, Materials), Is.EqualTo(7f));
                StrategicResourceReservationLedger.ReleaseByOwner(a, turn, "one");
                Assert.That(StrategicResourceReservationLedger.Active(a, turn, Materials), Is.EqualTo(4f));
                Assert.That(StrategicResourceReservationLedger.Active(b, turn, Materials), Is.EqualTo(5f));
                Assert.That(StrategicResourceReservationLedger.Active(a, turn + 1, Materials), Is.Zero);
                StrategicResourceReservationLedger.ExpireStage(a, turn, StrategicReservationExpiry.EndOfReaction);
                Assert.That(StrategicResourceReservationLedger.Active(a, turn, Materials), Is.Zero);
                Assert.That(StrategicResourceReservationLedger.Active(b, turn, Materials), Is.EqualTo(5f));
            }
            finally { StrategicResourceReservationLedger.ClearAll(); }
        }

        [Test]
        public void OptionalStealthCannotUseReactionApButCanUseReleasedAp()
        {
            var claims = new[] { Claim("reaction", ResourceClaimKind.Reaction, Ap, 2f) };
            var input = new OptionalStealthInputs
            {
                ApRemaining = (int)TurnResourceBook.Free(3f, claims, Ap, default),
                MandatoryApClaims = 1f, StealthApCost = 1,
                LegDetectionRisk = 1f, RouteAccessBenefit = 1f,
            };
            Assert.That(ScoutOptionalStealthPolicy.Evaluate(input).Decision,
                Is.EqualTo(OptionalStealthDecision.Skip));
            input.ApRemaining = (int)TurnResourceBook.Free(3f,
                System.Array.Empty<ResourceClaim>(), Ap, default);
            Assert.That(ScoutOptionalStealthPolicy.Evaluate(input).Decision,
                Is.EqualTo(OptionalStealthDecision.Enter));
        }

        [Test]
        public void ProvisioningAcknowledgementClaimsResourcesOncePerMission()
        {
            var session = new ProvisioningSession(new WorldSnapshot());
            var mission = new ProvisionedMission
            {
                MoverArmyId = 17, ClaimedAp = 2f, ClaimedEnergy = 3f,
                ClaimedNextTurnAirAp = 1f, ClaimedNextTurnAirEnergy = 4f,
            };
            var key = default(StableMissionKey);
            session.RegisterSuccess(key, mission);
            session.RegisterSuccess(key, mission);
            Assert.That(session.Successful, Has.Count.EqualTo(1));
            Assert.That(session.ApClaimed, Is.EqualTo(2f));
            Assert.That(session.EnergyClaimed, Is.EqualTo(3f));
            Assert.That(session.NextTurnAirApClaimed, Is.EqualTo(1f));
            Assert.That(session.NextTurnAirEnergyClaimed, Is.EqualTo(4f));
            Assert.That(session.ClaimedArmyIds, Does.Contain(17));
        }

        [Test]
        public void NoAuthority_SeesEveryClaim()
        {
            Assert.That(TurnResourceBook.Free(10f, Claims, Materials, default), Is.EqualTo(5f));
            Assert.That(TurnResourceBook.Free(10f, Claims, Ap, default), Is.EqualTo(5f));
        }

        [Test]
        public void Owner_MayDrawOnItsOwnHoldOnly()
        {
            var buildB = new SpendAuthority("buildB", economyCompletesNow: false);

            Assert.That(TurnResourceBook.Free(10f, Claims, Materials, buildB), Is.EqualTo(7f));
            Assert.That(TurnResourceBook.Free(10f, Claims, Ap, buildB), Is.EqualTo(7f),
                "its own completion AP is usable; reaction and continuation are not");
        }

        [Test]
        public void EconomyCompletingNow_MayDrawOnOtherBuildsDeferredHolds()
        {
            var completesNow = new SpendAuthority("buildB", economyCompletesNow: true);

            Assert.That(TurnResourceBook.Free(10f, Claims, Materials, completesNow), Is.EqualTo(10f));
            Assert.That(TurnResourceBook.Free(10f, Claims, Ap, completesNow), Is.EqualTo(7f),
                "seniority over deferred holds grants nothing over reaction or continuation claims");
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
        public void Outstanding_NetsAnOwnersHoldByWhatItsOwnWorkAlreadyDrew()
        {
            var claims = new[]
            {
                Claim("buildA", ResourceClaimKind.EconomyCompletion, Materials, 3f),
                Claim("buildB", ResourceClaimKind.EconomyCompletion, Materials, 2f),
            };
            var draws = new System.Collections.Generic.Dictionary<string, float> { ["buildA"] = 2f };

            Assert.That(TurnResourceBook.Outstanding(claims, Materials, default, draws), Is.EqualTo(3f),
                "buildA drew 2 of its own 3 (1 still held) + buildB's untouched 2");
        }

        [Test]
        public void Outstanding_ADrawBeyondTheHoldNeverGoesNegative()
        {
            var claims = new[] { Claim("buildA", ResourceClaimKind.EconomyCompletion, Materials, 3f) };
            var draws = new System.Collections.Generic.Dictionary<string, float> { ["buildA"] = 5f };

            Assert.That(TurnResourceBook.Outstanding(claims, Materials, default, draws), Is.Zero);
        }

        [Test]
        public void Outstanding_DrawsOfOneOwnerDoNotReleaseAnothersHold()
        {
            var claims = new[]
            {
                Claim("buildA", ResourceClaimKind.EconomyCompletion, Materials, 3f),
                Claim("reaction", ResourceClaimKind.Reaction, Materials, 2f),
            };
            var draws = new System.Collections.Generic.Dictionary<string, float> { ["buildB"] = 4f };

            Assert.That(TurnResourceBook.Outstanding(claims, Materials, default, draws), Is.EqualTo(5f));
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

