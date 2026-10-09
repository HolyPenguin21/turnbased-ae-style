#if UNITY_INCLUDE_TESTS
using Game.Ai.V2;
using Game.Cards;
using Game.HexGrid;
using Game.Players;
using NUnit.Framework;

namespace Game.EditorTests
{
    // Baseline fixtures of the decoupling task (docs/ai-v2-decoupling-evidence.md, scenarios S1-S9).
    // Expected values are fixed by the reservation rules (each build releases only its own AP;
    // nothing survives the turn), written before any decoupling change - never copied from a new
    // implementation. Every fixture also records a JSONL trace (AiDecouplingTrace) that the Python
    // checkers compare across stages when AI_V2_TRACE_DIR is set.
    public class AiDecouplingBaselineTests
    {
        private static MissionIntent ActiveFoundBaseIntent(int builderArmyId, HexCoord target,
            ResourceCost cost, float buildAp)
        {
            var key = new StableMissionKey(MissionKind.Economy,
                (int)EconomyTaskKind.FoundBase, 0, target.Q, target.R);
            return new MissionIntent
            {
                Kind = MissionKind.Economy,
                Status = IntentStatus.Active,
                LastAttemptKey = key,
                PreferredMoverArmyId = builderArmyId,
                Objective = new EconomyIntent
                {
                    Kind = EconomyTaskKind.FoundBase,
                    TargetHex = target,
                    BuilderArmyId = builderArmyId,
                    BuildResourceCost = cost,
                    BuildApCost = buildAp,
                    MinimumFollowupAp = buildAp,
                },
            };
        }

        private static float Ap(PlayerSetupData p, int turn) =>
            StrategicResourceReservationLedger.Active(p, turn, StrategicReservedResource.ActionPoints);

        // S1 (bank part): two Economy owners hold completion AP; after a settled move the first
        // owner's completion is downgraded (only its own AP is released, its physical H/M stay as a
        // deferred hold); the stage is idempotent; the turn end clears every row; the next turn of
        // the same player starts empty.
        [Test]
        public void S1_Bank_CompletionDowngradeReleasesOnlyOwnApAndNothingSurvivesTheTurn()
        {
            var player = new PlayerSetupData();
            var other2 = new PlayerSetupData();
            const int turn = 21;
            var trace = new AiDecouplingTrace("S1_bank", player);

            StrategicResourceReservationLedger.BeginTurn(player, turn);
            StrategicResourceReservationLedger.BeginTurn(other2, turn);
            trace.Record(turn, "Pass", "turn_start", ap: 0f);

            var ownerCost = new ResourceCost(human: 2, materials: 3);
            var otherCost = new ResourceCost(energy: 2, tech: 1);
            MissionIntent intent = ActiveFoundBaseIntent(19, new HexCoord(3, 2), ownerCost, 4f);
            string owner = EconomyMissionPlanner.OwnerKey(intent.LastAttemptKey);
            const string other = "independent-other-build";

            EconomyReservationLifecycle.ReserveEconomyCost(player, turn, ReservationOwner.ForPass(owner),
                ownerCost, 4f);
            trace.Record(turn, "Mission", "reserve_completion");
            EconomyReservationLifecycle.ReserveEconomyCost(player, turn, ReservationOwner.ForPass(other),
                otherCost, 3f);
            trace.Record(turn, "Mission", "reserve_completion");
            // A different player's bank is a separate scope and must never be touched.
            EconomyReservationLifecycle.ReserveEconomyCost(other2, turn,
                ReservationOwner.ForPass("foreign-player-build"), new ResourceCost(materials: 1), 5f);
            Assert.That(Ap(player, turn), Is.EqualTo(7f), "4 + 3 AP held before the move");

            // Settled movement: the first owner can no longer finish this turn.
            EconomyReservationLifecycle.ReconcileEconomyCompletionOwner(player, turn, owner, intent,
                durableValid: true, completionThisTurn: false);
            trace.Record(turn, "Mission", "reconcile_completion");
            Assert.That(Ap(player, turn), Is.EqualTo(3f), "only the first owner's 4 AP is released");
            Assert.That(StrategicResourceReservationLedger.OwnerReasonMatches(player, turn, owner,
                StrategicReservationReason.EconomyDeferredBuild, ownerCost, 4f), Is.True,
                "its physical hold survives as a deferred build");
            Assert.That(StrategicResourceReservationLedger.Active(player, turn,
                StrategicReservedResource.Human), Is.EqualTo(2f));
            Assert.That(StrategicResourceReservationLedger.Active(player, turn,
                StrategicReservedResource.Materials), Is.EqualTo(3f));

            // Idempotent reentry, and a deferred restatement cannot promote AP again.
            EconomyReservationLifecycle.ReconcileEconomyCompletionOwner(player, turn, owner, intent,
                durableValid: true, completionThisTurn: false);
            EconomyReservationLifecycle.ReserveDeferredEconomyResourcesForActiveIntent(player, turn, intent);
            trace.Record(turn, "Tempo", "reconcile_completion");
            Assert.That(Ap(player, turn), Is.EqualTo(3f));
            Assert.That(Ap(other2, turn), Is.EqualTo(5f), "another player's rows are untouched");

            // End of turn: stage expiry, then the leak assertion has nothing to clear.
            StrategicResourceReservationLedger.ExpireStage(player, turn, StrategicReservationExpiry.EndOfTurn);
            StrategicResourceReservationLedger.AssertClearAtTurnEnd(player, turn);
            trace.Record(turn, "Pass", "turn_end");
            Assert.That(StrategicResourceReservationLedger.HasAny(player, turn), Is.False,
                "nothing survives the turn");

            // Next turn of the same player: a new scope, empty, the old rows are not visible.
            StrategicResourceReservationLedger.BeginTurn(player, turn + 1);
            trace.Record(turn + 1, "Pass", "turn_start");
            Assert.That(StrategicResourceReservationLedger.Rows(player, turn + 1), Is.Empty);
            Assert.That(StrategicResourceReservationLedger.Rows(player, turn), Is.Empty,
                "an expired view is not served for the previous turn either");
            Assert.That(trace.Lines.Count, Is.EqualTo(7));
            trace.Flush();
        }

        // S9 (bank part): rekey/retire of one owner and a stale writer after a new turn began.
        [Test]
        public void S9_Bank_ReleaseByOwnerIsScopedAndStaleTurnWritesAreIgnored()
        {
            var player = new PlayerSetupData();
            const int turn = 31;
            var trace = new AiDecouplingTrace("S9_bank", player);
            StrategicResourceReservationLedger.BeginTurn(player, turn);
            trace.Record(turn, "Pass", "turn_start");

            var costA = new ResourceCost(materials: 2);
            var costB = new ResourceCost(energy: 1);
            EconomyReservationLifecycle.ReserveEconomyCost(player, turn,
                ReservationOwner.ForPass("owner-a"), costA, 4f);
            EconomyReservationLifecycle.ReserveEconomyCost(player, turn,
                ReservationOwner.ForPass("owner-b"), costB, 2f);
            trace.Record(turn, "Mission", "reserve_completion");
            Assert.That(Ap(player, turn), Is.EqualTo(6f));

            Assert.That(StrategicResourceReservationLedger.ReleaseByOwner(player, turn, "owner-a"), Is.True);
            trace.Record(turn, "Mission", "release_owner");
            Assert.That(Ap(player, turn), Is.EqualTo(2f), "only owner-a's rows are retired");
            Assert.That(StrategicResourceReservationLedger.ReleaseByOwner(player, turn, "owner-a"), Is.False,
                "a second release of the same owner is a no-op");

            // A new turn begins; a stale writer of the previous turn must not resurrect rows.
            StrategicResourceReservationLedger.BeginTurn(player, turn + 1);
            trace.Record(turn + 1, "Pass", "turn_start");
            Assert.That(StrategicResourceReservationLedger.ReleaseByOwner(player, turn, "owner-b"), Is.False);
            Assert.That(StrategicResourceReservationLedger.Rows(player, turn + 1), Is.Empty);
            Assert.That(trace.Lines.Count, Is.EqualTo(4));
            trace.Flush();
        }
    }
}
#endif
