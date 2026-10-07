namespace Game.Ai.V2
{
    // Existing domain result semantics, owned by the same continuity policy.
    internal static partial class MissionContinuityLayer
    {
        internal static bool IsEconomyStepObjectiveSatisfiedLive(Game.Players.PlayerSetupData player,
            ProvisionedMission pm) => EconomyLifecycleState.ObjectiveSatisfied(player, pm.EconomyTarget);

        internal static void ClassifyEconomyStep(ExecutionResult e, MissionTurnOutcome o)
        {
            // A committed roster mutation remains progress if its pinned tail became stale.
            if (e.EconomyPrepared && e.StopReason == ExecutionStopReason.TargetInvalidated)
            {
                o.Outcome = ExecutionOutcome.ProductiveStop;
                return;
            }
            switch (e.StopReason)
            {
                case ExecutionStopReason.StepCompleted:
                case ExecutionStopReason.OutOfMovement:
                    o.Outcome = ExecutionOutcome.ProductiveStop;
                    break;
                case ExecutionStopReason.NoSafeStep:
                case ExecutionStopReason.MoveRejected:
                // Economy audit B2 — every execution-side TargetInvalidated of an Economy step
                // is transient (stale plan, unaffordable activation, AP/resources of a pinned
                // preparation gone this pass), never proof the durable build is invalid:
                // Continuity re-validates the objective itself (ResolveActive).
                case ExecutionStopReason.TargetInvalidated:
                    o.Outcome = ExecutionOutcome.Blocked;
                    break;
                default:
                    o.Outcome = ExecutionOutcome.Failed;
                    break;
            }
            return;
        }
        private static void RecordEconomyStepProgress(MissionIntentState state, MissionTurnOutcome o, int turn)
        {
            // A build project that really progressed this turn is not failing to deliver: end its
            // delivery-failure streak before any branch below may record a failure for it.
            if (o.MissionKind == MissionKind.Economy
                && (o.MadeProgress || o.Outcome == ExecutionOutcome.Completed)
                && TryGetEconomyTarget(o, out EconomyMissionTarget progressed))
            {
                if (progressed.Kind == EconomyTaskKind.FoundBase)
                    state.Economy.RecordBaseExpansionDeliveryProgress(
                        turn, progressed.BuildCard, progressed.TargetHex);
                else if (progressed.Kind == EconomyTaskKind.BuildExtraction)
                    state.Economy.RecordExtractionDeliveryProgress(
                        turn, progressed.ResourceType, progressed.TargetHex);
            }

        }
        private static bool TryKeepEconomyRecovery(MissionIntentState state, AiAllocatorState allocState,
            MissionIntent intent, MissionTurnOutcome o, int turn)
        {
            bool returnRecoveryOutcome = o.MissionKind == MissionKind.Economy
                && (o.EconomyTarget.Kind == EconomyTaskKind.ReturnBuilder
                    || o.EconomyTarget.Kind == EconomyTaskKind.ReturnCollector
                    || intent?.Economy?.Kind == EconomyTaskKind.ReturnBuilder
                    || intent?.Economy?.Kind == EconomyTaskKind.ReturnCollector);
            if (returnRecoveryOutcome && intent != null)
            {
                KeepEconomyReturn(state, intent, o, turn);
                return true;
            }
            return false;
        }
        private static bool TryHandleEconomyNoProgress(MissionIntentState state, AiAllocatorState allocState,
            MissionIntent intent, MissionTurnOutcome o, int turn)
        {
            if (o.MissionKind == MissionKind.Economy && !o.MadeProgress)
            {
                if (TryKeepEconomyRecovery(state, allocState, intent, o, turn))
                {
                    return true;
                }
                // A no-progress Economy outcome ends its outbound commitment only on a PROVEN
                // route failure: Provisioning's NoExecutableStep (no live safe route for the
                // committed mover) or an executed step that found no safe / legal move. Every
                // other no-progress outcome is transient — NoMoverExists / MoverContended
                // suspend the intent (AdvanceIntent, CapabilityUnavailable, bounded by the
                // per-project delivery-failure streaks), anything else (an AP / resource
                // envelope that did not fit this pass, a stale plan or activation) ages it
                // through StallTurns/ShouldReap like any idle intent (Economy audit B1/B2).
                // ReturnBuilder (the return-trip leg) keeps its own preservation rule above.
                // P1 fix: a build project that never got far enough to become a durable intent
                // must still count toward the delivery-failure streak, or that project can retry
                // forever without ever reaching AdvanceIntent's own call (below), which only fires
                // once intent != null. Every no-progress kind counts here, not only NoMoverExists /
                // MoverContended: a fresh project blocked turn after turn by alternating reasons
                // (no mover, then no AP envelope, then no step) is just as undeliverable, and while
                // Demand keeps selecting it Phase A keeps its H/E/M/T frozen under an
                // EconomyDeferredBuild hold. The streak is per project and consecutive-turn only,
                // so a transient one-turn miss never suppresses anything. BuildExtraction is
                // counted the same way as FoundBase. This is the ONE registration point for the
                // no-intent case; the intent paths below only fire for an existing intent, so the
                // two never double-count the same outcome.
                if (intent == null && TryGetEconomyTarget(o, out EconomyMissionTarget freshTarget))
                {
                    if (freshTarget.Kind == EconomyTaskKind.FoundBase)
                        state.Economy.RecordBaseExpansionDeliveryFailure(
                            turn, freshTarget.BuildCard, freshTarget.TargetHex);
                    else if (freshTarget.Kind == EconomyTaskKind.BuildExtraction)
                        state.Economy.RecordExtractionDeliveryFailure(
                            turn, freshTarget.ResourceType, freshTarget.TargetHex);
                }

                if (intent != null && !IsEconomyRouteFailure(o))
                {
                    AdvanceIntent(intent, o, turn, state, allocState);
                    return true;
                }
                RetireEconomyIntent(state, intent, o, turn);
                return true;
            }

            return false;
        }

        private static bool TryCreateEconomyStep(MissionIntentState state, AiAllocatorState allocState,
            MissionIntent intent, MissionTurnOutcome o, int turn)
        {
            if (!(o.HasEconomyPayload && o.MadeProgress)) return false;
            CreateEconomyIntent(state, o, turn);
            return true;
        }


        // A materialized outcome carries its EconomyTarget directly, but a fresh mission that failed
        // provisioning before ever producing a ProvisionedMission only has it on the proposal.
        private static bool TryGetEconomyTarget(MissionTurnOutcome o, out EconomyMissionTarget target)
        {
            if (o.HasEconomyPayload)
            {
                target = o.EconomyTarget;
                return true;
            }
            if (o.Proposal?.Target is EconomyMissionTarget proposed)
            {
                target = proposed;
                return true;
            }
            target = default;
            return false;
        }

        private static void CreateEconomyIntent(MissionIntentState state, MissionTurnOutcome o, int turn)
        {
            EconomyMissionTarget t = o.EconomyTarget;
            var ei = new EconomyIntent
            {
                Kind = t.Kind, TargetHex = t.TargetHex, ResourceType = t.ResourceType,
                BuilderArmyId = t.Kind == EconomyTaskKind.MobileCollection
                    || t.Kind == EconomyTaskKind.ReturnCollector ? t.BuilderArmyId : o.MoverArmyId,
                CollectorArmyId = t.Kind == EconomyTaskKind.MobileCollection
                    || t.Kind == EconomyTaskKind.ReturnCollector ? o.MoverArmyId : t.CollectorArmyId,
                CollectorSourceArmyId = t.CollectorSourceArmyId,
                ExpectedMarginalYield = t.ExpectedMarginalYield,
                SafeReturnHex = t.SafeReturnHex,
                ArrivalTurn = t.Kind == EconomyTaskKind.MobileCollection
                    && o.FinalHex.Equals(t.TargetHex) ? turn : -1,
                BuildCard = t.BuildCard, BuildResourceCost = t.BuildResourceCost,
                BuildApCost = t.BuildApCost, BuildValue = t.BuildValue,
                IntrinsicValue = o.Proposal?.BaseValue,
                MinimumFollowupAp = t.MinimumFollowupAp,
                Loaned = o.EconomyLoanSource.HasValue,
                LoanSource = o.EconomyLoanSource ?? default,
            };
            CommitmentTier funding = o.EconomyBuildCompleted ? CommitmentTier.Hard : CommitmentTier.Soft;
            MissionIntent intent = NewIntent(o, turn, MissionKind.Economy, funding, ei);
            state.Put(intent);
            AiDebugLog.Write($"[AI][V2][Economy] continuity create {intent.IntentKey} mover=#{o.MoverArmyId}");
        }

        private static void RepayEconomyLoan(MissionIntentState state, MissionIntent economy,
            MissionTurnOutcome outcome)
        {
            MissionIntentKey? source = outcome?.EconomyLoanSource;
            if (!source.HasValue && economy?.Economy?.Loaned == true)
                source = economy.Economy.LoanSource;
            if (source.HasValue && state.TryGet(source.Value, out MissionIntent lender))
                ResumeEconomyLender(lender);
        }

        // The ONE retirement of an Economy intent: a borrowed Recon/Raid owner gets its actor back,
        // this owner's turn-scoped H/E/M/T/AP holds are released (Phase B may spend them for the
        // rest of the turn), then the intent goes. `outcome` may carry the loan of a mission that
        // never became durable. Economy audit B8.
        private static void RetireEconomyIntent(MissionIntentState state, MissionIntent intent,
            MissionTurnOutcome outcome, int turn, bool returnLoan = true)
        {
            if (returnLoan)
                RepayEconomyLoan(state, intent, outcome);
            if (intent == null)
            {
                if (outcome != null) state.Remove(outcome.IntentKey, turn);
                return;
            }
            state.Remove(intent.IntentKey, turn);
        }

        // A proven route failure of an outbound Economy step (see ReconcileOutcome).
        private static bool IsEconomyRouteFailure(MissionTurnOutcome o) =>
            o.ProvisionFailureKindValue == ProvisionFailureKind.NoExecutableStep
            || (!o.ProvisionFailureKindValue.HasValue
                && (o.StopReason == ExecutionStopReason.NoSafeStep
                    || o.StopReason == ExecutionStopReason.MoveRejected));

        // Economy return legs are preserved through every transient failure (a lost shelter is re-targeted
        // by ResolveActive, a blocked way home may clear) — but only while it still gets home:
        // a builder that has not advanced for commitmentMaxTurns is released, its lender resumed,
        // instead of holding the hero (and the lender) forever. Economy audit S1.
        private static void KeepEconomyReturn(MissionIntentState state, MissionIntent intent,
            MissionTurnOutcome o, int turn)
        {
            // Preserve the collector's existing bounded capability retry contract. Lost-home
            // failure stays alive until fresh ResolveActive can retarget and reset the stall.
            if (intent.Economy?.Kind == EconomyTaskKind.ReturnCollector)
            {
                // A vanished home needs fresh ownership/route facts, not another capability
                // strike against the actor. ResolveActive either retargets or retires it.
                if (o.ProvisionFailureKindValue == ProvisionFailureKind.TargetInvalidated
                    || o.StopReason == ExecutionStopReason.TargetInvalidated)
                {
                    intent.Status = IntentStatus.Active;
                    intent.Suspended = SuspendReason.None;
                    intent.LastReconciledTurn = turn;
                    return;
                }
                AdvanceIntent(intent, o, turn, state, AiAllocatorStateRegistry.GetOrCreate(state.Owner));
                if (ShouldReap(intent, turn)) RetireEconomyIntent(state, intent, o, turn);
                return;
            }
            if (turn - intent.LastProgressTurn >= AiConfigV2.commitmentMaxTurns)
            {
                AiDebugLog.Write($"[AI][V2][Economy][Recovery] release {intent.IntentKey} — no progress "
                    + $"home since t{intent.LastProgressTurn}");
                RetireEconomyIntent(state, intent, o, turn);
                return;
            }
            intent.Status = IntentStatus.Active;
            intent.Suspended = SuspendReason.None;
            intent.LastReconciledTurn = turn;
            intent.StallTurns = 0;
        }

        internal static bool IsCollectorEconomyIntent(MissionIntent i) =>
            i != null && i.Kind == MissionKind.Economy
            && (i.Economy?.Kind == EconomyTaskKind.MobileCollection
                || i.Economy?.Kind == EconomyTaskKind.ReturnCollector);
        private static bool TryRetireEconomyOutcome(MissionIntentState state, AiAllocatorState allocState,
            MissionIntent intent, MissionTurnOutcome o, int turn)
        {
            if (intent != null && intent.Kind != MissionKind.Economy) return false;
            RetireEconomyIntent(state, intent, o, turn);
            return true;
        }

        private static void ApplyEconomyStepFacts(MissionIntentState state, AiAllocatorState allocator,
            MissionIntent intent, MissionTurnOutcome o, int turn)
        {
            if (o.HasEconomyPayload && intent.Economy != null)
            {
                intent.Economy.TargetHex = o.EconomyTarget.TargetHex;
                intent.Economy.BuilderArmyId = intent.PreferredMoverArmyId;
                // The mission has already used the continuity-pinned builder's world score.
                // Persist the last accepted intrinsic value so a later turn without a refreshed
                // demand cannot silently revive the unrelated site-only BuildValue.
                if (o.MadeProgress && o.Proposal?.Target is EconomyMissionTarget scored
                    && scored.Kind == intent.Economy.Kind
                    && scored.TargetHex.Equals(intent.Economy.TargetHex)
                    && scored.BuilderArmyId == intent.PreferredMoverArmyId)
                    intent.Economy.IntrinsicValue = o.Proposal.BaseValue;
                if (o.EconomyBuildCompleted) intent.Funding = CommitmentTier.Hard;
            }

        }

        private static bool TryHandleEconomyCapabilityFailure(MissionIntentState state, AiAllocatorState allocState,
            MissionIntent intent, MissionTurnOutcome o, int turn)
        {
            // Base expansion is deliberately exempt from StallTurns/ShouldReap aging (see the
            // comment above transientCapability in ReconcileOutcome) so an in-progress delivery
            // survives a transient blip. That exemption previously had no upper bound: the same
            // stuck project — NoMoverExists / MoverContended, turn after turn — never triggered
            // the existing MissionIntentState delivery-failure cooldown because nothing called
            // it. Wire it here, the one place this intent is suspended for that reason. A gap
            // turn without a capability failure (real progress or a different suspend reason)
            // breaks RecordBaseExpansionDeliveryFailure's consecutive-turn streak on its own —
            // no separate reset is needed.
            if (!o.MadeProgress && intent.Kind == MissionKind.Economy
                && intent.Economy?.Kind == EconomyTaskKind.FoundBase
                && state.Economy.RecordBaseExpansionDeliveryFailure(
                    turn, intent.Economy.BuildCard, intent.Economy.TargetHex))
            {
                RetireEconomyIntent(state, intent, o, turn);
                StartPersistentCooldown(allocState, intent.LastAttemptKey, intent.Kind, turn,
                    "BaseExpansionDeliverySuppressed");
                AiDebugLog.Write($"[AI][V2] continuity — [{AiV2Trace.FormatCorrelation(o.Proposal)}] "
                    + $"{intent.IntentKey} Base delivery repeatedly failed "
                    + $"({o.ProvisionFailureKindValue}); suppressed for cooldown, retired");
                return true;
            }

            // BuildExtraction needs the same upper bound: it is exempt from StallTurns/ShouldReap
            // for the identical reason (capabilityUnavailable, above), so its consecutive
            // NoMoverExists/MoverContended turns are counted here — otherwise an intent whose
            // pinned mover can no longer advance (e.g. its safe route stays blocked every turn)
            // would stay suspended forever with its actor and card claim held.
            // Extraction has no single staged slot like Base (several sites can be active at
            // once), so the counter is keyed per (resource, site) in MissionIntentState — same
            // owner, same StartPersistentCooldown exit already used by every other retirement
            // path (reap/structural-failure/Base) — no new registry or manager.
            if (!o.MadeProgress && intent.Kind == MissionKind.Economy
                && intent.Economy?.Kind == EconomyTaskKind.BuildExtraction
                && state.Economy.RecordExtractionDeliveryFailure(
                    turn, intent.Economy.ResourceType, intent.Economy.TargetHex))
            {
                RetireEconomyIntent(state, intent, o, turn);
                StartPersistentCooldown(allocState, intent.LastAttemptKey, intent.Kind, turn,
                    "ExtractionDeliverySuppressed");
                AiDebugLog.Write($"[AI][V2] continuity — [{AiV2Trace.FormatCorrelation(o.Proposal)}] "
                    + $"{intent.IntentKey} Extraction delivery repeatedly failed "
                    + $"({o.ProvisionFailureKindValue}); suppressed for cooldown, retired");
                return true;
            }
            return false;
        }

    }
}
