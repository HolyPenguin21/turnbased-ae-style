#if UNITY_INCLUDE_TESTS
using System.Linq;
using Game.Ai.V2;
using Game.Players;
using NUnit.Framework;

namespace Game.EditorTests
{
    public class AiReservationInvariantsTests
    {
        // Indexed by StrategicReservedResource: ActionPoints, Human, Energy, Materials, Tech.
        private static float[] V(float ap = 0, float human = 0, float energy = 0,
            float materials = 0, float tech = 0) => new[] { ap, human, energy, materials, tech };

        private static StrategicResourceReservation Row(StrategicReservationReason reason,
            StrategicReservedResource resource, float amount, string owner = "o") =>
            new StrategicResourceReservation
            {
                Owner = owner, Reason = reason, Resource = resource, Amount = amount,
                ExpirationStage = StrategicReservationExpiry.EndOfTurn,
            };

        [Test]
        public void DeferredBuildSavingBeyondStock_IsNotACommittedViolation()
        {
            var rows = new[]
            {
                Row(StrategicReservationReason.EconomyDeferredBuild,
                    StrategicReservedResource.Materials, 10f),
            };

            float[] held = ReservationInvariants.CommittedHeld(rows, derived: null);

            Assert.That(ReservationInvariants.Uncovered(held, V(materials: 4f)), Is.Empty,
                "a deferred build saves up its full cost; exceeding the stock is its normal state");
        }

        [Test]
        public void CompletionAndReactionHoldsBeyondStock_AreUncovered()
        {
            var rows = new[]
            {
                Row(StrategicReservationReason.EconomyBuildCompletion,
                    StrategicReservedResource.ActionPoints, 3f, "build"),
                Row(StrategicReservationReason.StrategicReactionPass,
                    StrategicReservedResource.ActionPoints, 2f, "reaction"),
            };

            float[] held = ReservationInvariants.CommittedHeld(rows, derived: null);

            Assert.That(ReservationInvariants.Uncovered(held, V(ap: 4f)),
                Is.EqualTo(new[] { StrategicReservedResource.ActionPoints }));
            Assert.That(ReservationInvariants.Uncovered(held, V(ap: 5f)), Is.Empty);
        }

        [Test]
        public void DerivedRecoveryHold_CountsTowardCommittedCoverage()
        {
            var rows = new[]
            {
                Row(StrategicReservationReason.EconomyBuildCompletion,
                    StrategicReservedResource.Energy, 2f),
            };

            float[] held = ReservationInvariants.CommittedHeld(rows, derived: V(energy: 2f));

            Assert.That(ReservationInvariants.Uncovered(held, V(energy: 3f)),
                Is.EqualTo(new[] { StrategicReservedResource.Energy }));
        }

        [Test]
        public void Overspent_FlagsOnlySpendBeyondWhatWasSpendable()
        {
            Assert.That(ReservationInvariants.Overspent(
                    before: V(ap: 5f), after: V(ap: 1f), spendableBefore: V(ap: 3f)),
                Is.EqualTo(new[] { StrategicReservedResource.ActionPoints }),
                "4 AP spent while only 3 were spendable: the action ate a hold");
            Assert.That(ReservationInvariants.Overspent(
                    before: V(ap: 5f), after: V(ap: 2f), spendableBefore: V(ap: 3f)),
                Is.Empty, "spending exactly the spendable amount is legal");
            Assert.That(ReservationInvariants.Overspent(
                    before: V(materials: 2f), after: V(materials: 6f), spendableBefore: V()),
                Is.Empty, "income during an action is not a spend");
        }

        [Test]
        public void Structural_FlagsDeferredApAndNonPositiveRows()
        {
            var rows = new[]
            {
                Row(StrategicReservationReason.EconomyDeferredBuild,
                    StrategicReservedResource.ActionPoints, 2f, "a"),
                Row(StrategicReservationReason.StrategicReactionPass,
                    StrategicReservedResource.Human, 0f, "b"),
                Row(StrategicReservationReason.EconomyDeferredBuild,
                    StrategicReservedResource.Human, 1f, "c"),
            };

            var found = ReservationInvariants.Structural(rows);

            Assert.That(found.Select(f => (f.Rule, f.Row.Owner)), Is.EquivalentTo(new[]
            {
                (ReservationInvariantRule.DeferredApHold, "a"),
                (ReservationInvariantRule.NonPositiveHold, "b"),
            }));
        }

        [Test]
        public void LedgerRows_AreDetachedCopies()
        {
            var player = new PlayerSetupData();
            const int turn = 4;
            StrategicResourceReservationLedger.BeginTurn(player, turn);
            StrategicResourceReservationLedger.Upsert(player, turn, Row(
                StrategicReservationReason.StrategicReactionPass,
                StrategicReservedResource.ActionPoints, 3f));

            var rows = StrategicResourceReservationLedger.Rows(player, turn);
            rows[0].Amount = 99f;

            Assert.That(StrategicResourceReservationLedger.Active(
                player, turn, StrategicReservedResource.ActionPoints), Is.EqualTo(3f));
            Assert.That(StrategicResourceReservationLedger.Rows(player, turn + 1), Is.Empty,
                "a stale turn reads as empty");
        }

        [Test]
        public void LeakAtTurnEnd_IsRecordedAsViolation()
        {
            var player = new PlayerSetupData();
            const int turn = 6;
            StrategicResourceReservationLedger.BeginTurn(player, turn);
            StrategicResourceReservationLedger.Upsert(player, turn, Row(
                StrategicReservationReason.EconomyDeferredBuild,
                StrategicReservedResource.Materials, 2f));

            StrategicResourceReservationLedger.AssertClearAtTurnEnd(player, turn);

            Assert.That(ReservationInvariants.Violations(player, turn).Select(v => v.Rule),
                Is.EqualTo(new[] { ReservationInvariantRule.LeakAtTurnEnd }));
            Assert.That(StrategicResourceReservationLedger.HasAny(player, turn), Is.False);
        }
    }
}
#endif
