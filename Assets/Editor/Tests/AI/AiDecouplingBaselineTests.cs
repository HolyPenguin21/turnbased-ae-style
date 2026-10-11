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
            trace.Record(turn, "Pass", "turn_start");

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

        // S2: a rebase step runs ONE take->reenter pair, a recovery/mission/tempo step runs TWO. The
        // first reentry publishes a compound fact (Capability). After one pair it is still pending
        // (a later take gets it); after two pairs the second take has fanned it out. Pending-set
        // model: take returns what is pending and consumes exactly that snapshot.
        [TestCase(StepTriggerSequence.RebasePairs, 1, false)]
        [TestCase(StepTriggerSequence.StandardPairs, 2, true)]
        public void S2_Triggers_RebaseRunsOnePairAndRecoveryTwo_FirstReentryFactIsNotLost(
            int pairs, int expectedTakes, bool compoundSeenByThisStep)
        {
            var pending = StrategicInvalidationReason.Actor;
            int takes = 0, reentries = 0;
            StepTriggerOutcome outcome = default;

            System.Collections.IEnumerator run = StepTriggerSequence.Run(pairs,
                take: () =>
                {
                    takes++;
                    StrategicInvalidationReason snapshot = pending;
                    pending &= ~snapshot; // consume exactly the snapshot
                    return new TypedTriggerSplit(StrategicInvalidationReason.None, snapshot,
                        new System.Collections.Generic.HashSet<DesireAxis>());
                },
                reenter: (reasons, axes) => Reenter(() =>
                {
                    reentries++;
                    // the first reentry admits axes and publishes a compound fact
                    if (reentries == 1) pending |= StrategicInvalidationReason.Capability;
                }),
                reentryChanged: () => reentries == 1,
                done: o => outcome = o);
            Drain(run);

            Assert.That(takes, Is.EqualTo(expectedTakes));
            Assert.That(reentries, Is.EqualTo(expectedTakes));
            Assert.That(outcome.Strategic.HasFlag(StrategicInvalidationReason.Actor), Is.True);
            Assert.That(outcome.Strategic.HasFlag(StrategicInvalidationReason.Capability),
                Is.EqualTo(compoundSeenByThisStep));
            Assert.That(pending.HasFlag(StrategicInvalidationReason.Capability),
                Is.EqualTo(!compoundSeenByThisStep),
                "the compound fact of the first reentry stays pending for the next take");
            Assert.That(outcome.StrategicChanged, Is.True);
        }

        // Runs nested coroutines to completion the way the AI controller unwinds yielded IEnumerators.
        private static void Drain(System.Collections.IEnumerator root)
        {
            var stack = new System.Collections.Generic.Stack<System.Collections.IEnumerator>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                if (!stack.Peek().MoveNext()) { stack.Pop(); continue; }
                if (stack.Peek().Current is System.Collections.IEnumerator nested) stack.Push(nested);
            }
        }

        private static System.Collections.IEnumerator Reenter(System.Action body)
        {
            body();
            yield break;
        }

        // S2: a stalled aviation actor is ignored for ITS turn only.
        [Test]
        public void S2_Stall_AMarkedActorIsStalledForItsTurnOnly()
        {
            var player = new PlayerSetupData();
            var other = new PlayerSetupData();
            AviationObligationStallRegistry.MarkStalled(player, 10, 77);
            Assert.That(AviationObligationStallRegistry.IsStalled(player, 10, 77), Is.True);
            Assert.That(AviationObligationStallRegistry.IsStalled(player, 10, 78), Is.False);
            Assert.That(AviationObligationStallRegistry.IsStalled(player, 11, 77), Is.False,
                "the next turn tries the actor again");
            Assert.That(AviationObligationStallRegistry.IsStalled(other, 10, 77), Is.False);
            AviationObligationStallRegistry.EndTurn(player, 10);
            Assert.That(AviationObligationStallRegistry.IsStalled(player, 10, 77), Is.False);
            AviationObligationStallRegistry.Clear();
        }

        // S4: ordinary return legs wait for the first Phase B round once; urgent ones (tactical
        // retreat, ActiveDefence return) never wait; the wait is recorded per turn and the next
        // turn the same leg goes at once; a waiting leg is protected from the stall counter only
        // for that turn.
        [Test]
        public void S4_Returns_OrdinaryWaitsOnce_UrgentNeverWaits_ProtectionIsForTheWaitTurnOnly()
        {
            var player = new PlayerSetupData { Nickname = "Us", ColorIndex = 1 };
            var target = new EconomyMissionTarget
            { Kind = EconomyTaskKind.ReturnBuilder, TargetHex = new HexCoord(3, 3), BuilderArmyId = 7 };
            var proposal = new MissionProposal { Kind = MissionKind.Economy, Target = target };
            var intent = new MissionIntent
            {
                Kind = MissionKind.Economy, Status = IntentStatus.Active,
                IntentKey = MissionIntentKey.For(proposal),
                Objective = new EconomyIntent
                { Kind = EconomyTaskKind.ReturnBuilder, TargetHex = target.TargetHex, BuilderArmyId = 7 },
            };
            var active = new[] { intent };
            try
            {
                MissionIntentRegistry.GetOrCreate(player).Put(intent);
                MissionIntentKey key = intent.IntentKey;

                // turn 5, before the first Phase B round
                var waiting = LifecycleReturnPolicy.SelectWaiting(new[] { proposal }, active, player, 5);
                Assert.That(waiting.Count, Is.EqualTo(1), "an ordinary return waits");
                LifecycleReturnPolicy.RecordWait(player, key, 5);
                MissionContinuityLayer.MarkProtectedThisTurn(player, key, 5);
                Assert.That(intent.LastProtectedTurn, Is.EqualTo(5));
                Assert.That(LifecycleReturnPolicy.SelectWaiting(new[] { proposal }, active, player, 5).Count,
                    Is.EqualTo(1), "later admissions of the same turn still wait");

                // turn 6: waited last turn - goes now; protection stamp is the old turn
                Assert.That(LifecycleReturnPolicy.SelectWaiting(new[] { proposal }, active, player, 6),
                    Is.Empty, "no second wait in a row");
                Assert.That(intent.LastProtectedTurn, Is.Not.EqualTo(6));

                // urgent: an ActiveDefence leg is not deferrable at all
                var adProposal = new MissionProposal { Kind = MissionKind.ActiveDefence };
                var adIntent = new MissionIntent
                {
                    Kind = MissionKind.ActiveDefence, Status = IntentStatus.Active,
                    IntentKey = MissionIntentKey.For(adProposal),
                    Objective = new ActiveDefenceIntent { Phase = ActiveDefencePhase.Return },
                };
                Assert.That(LifecycleReturnPolicy.SelectWaiting(new[] { adProposal },
                    new[] { adIntent }, player, 7), Is.Empty);
            }
            finally
            {
                LifecycleReturnPolicy.ClearAll();
                MissionIntentRegistry.GetOrCreate(player).Remove(intent.IntentKey);
            }
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
