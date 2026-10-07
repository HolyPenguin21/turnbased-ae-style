using System.Collections.Generic;
using System.Linq;
using Game.HexGrid;
namespace Game.Ai.V2
{
    // Existing domain result semantics, owned by the same continuity policy.
    internal static partial class MissionContinuityLayer
    {
        internal static bool EconomyBuilderReadyForCompletion(IEnumerable<MissionIntent> activeIntents,
            WorldSnapshot snapshot)
        {
            foreach (MissionIntent intent in activeIntents ?? new List<MissionIntent>())
            {
                if (intent?.Kind != MissionKind.Economy
                    || intent.Status != IntentStatus.Active
                    || intent.Economy == null
                    || !intent.PreferredMoverArmyId.HasValue)
                    continue;
                ArmySnapshot actor = snapshot?.Self?.Armies?.FirstOrDefault(a => a != null
                    && a.ArmyId == intent.PreferredMoverArmyId.Value);
                if (actor == null || !actor.Hex.Equals(intent.Economy.TargetHex))
                    continue;
                // A ReturnBuilder that just reached home is about to be retired by the next
                // MissionContinuityLayer.ResolveActive pass, freeing its builder for a new
                // Economy demand this same turn — that is exactly as actionable as a builder
                // arriving at a fresh build hex.
                return true;
            }
            return false;
        }

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
        private static void PrepareEconomyRetirement(MissionIntentState state, AiAllocatorState allocState,
            MissionIntent intent, MissionTurnOutcome o, int turn)
        {
            // Preserve the old null-intent loan hand-back (the attempt may never become durable).
            // Ownership cleanup belongs to the common retirement boundary, not this domain hook.
            if (intent == null || intent.Kind == MissionKind.Economy) RepayEconomyLoan(state, intent, o);
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

        private static void RepairEconomyLoans(Game.Players.PlayerSetupData player, WorldSnapshot snap, ActiveResolution pass)
        {
            var state = pass.State;
            // There is deliberately NO global "primary" build selection here. ResolveActive
            // validates each build intent on its OWN facts (objective completed / target still
            // legal / actor still alive / route still usable). Progress on one site is not evidence
            // that another site's obligation became illegal; real ownership conflicts (same actor,
            // same objective identity, same physical card) are resolved where ownership is actually
            // granted — BeginEconomyDelivery — not by retiring bystanders here.
            var liveLoanSources = new HashSet<MissionIntentKey>(state.All
                .Where(i => i?.Kind == MissionKind.Economy && i.Economy?.Loaned == true)
                .Select(i => i.Economy.LoanSource));
            foreach (MissionIntent orphanedDonor in state.All.Where(i => i != null
                && i.Status == IntentStatus.Suspended && i.Suspended == SuspendReason.EconomyLoan
                && !liveLoanSources.Contains(i.IntentKey)))
            {
                AiDebugLog.Write($"[AI][V2][Economy][Loan] orphan repair donor={orphanedDonor.IntentKey}");
                ResumeEconomyLender(orphanedDonor);
            }
        }

        private static void ResolveEconomyOperation(Game.Players.PlayerSetupData player, WorldSnapshot snap, MissionIntent intent, ActiveResolution pass)
        {
            var state = pass.State;
            var active = pass.Active;
            var dead = pass.Dead;
            var rekeys = pass.Rekeys;

            EconomyIntent ei = intent.Economy;
            bool collectorMission = ei?.Kind == EconomyTaskKind.MobileCollection
                || ei?.Kind == EconomyTaskKind.ReturnCollector;
            ArmySnapshot actor = snap?.Self?.Armies?.FirstOrDefault(a => a != null
                && a.ArmyId == intent.PreferredMoverArmyId && !a.IsPrison && !a.IsAir
                && (collectorMission || a.HasHero));
            if (ei?.Kind == EconomyTaskKind.MobileCollection)
            {
                bool capable = actor != null && ei.ResourceType.HasValue
                    && actor.CollectionCapacity.Get(ei.ResourceType.Value) > 0f;
                bool arrived = capable && actor.Hex.Equals(ei.TargetHex);
                if (arrived && ei.ArrivalTurn < 0)
                    ei.ArrivalTurn = snap.TurnNumber;
                if (arrived && snap.TurnNumber > ei.ArrivalTurn)
                    ei.LastConfirmedIncomeTick = snap.TurnNumber;
                // Economy audit B12 — the SAME usefulness test Analysis admits a collector
                // with (UsefulMarginalIncomeGain), judged without the income this collector
                // itself now produces; a second "income below target" rule sent a still
                // useful collector home and Analysis sent it straight back.
                bool useful = capable && ei.ResourceType.HasValue
                    && CollectorStillUseful(snap, ei.ResourceType.Value, ei.ExpectedMarginalYield)
                    && WorldAnalysis.KnownExtractionYields(snap).Any(x =>
                        x.Hex.Equals(ei.TargetHex) && x.Type == ei.ResourceType.Value
                        && x.Yield > 0);
                bool safe = !WorldAnalysis.KnownHostileAtHex(snap, ei.TargetHex);
                if (!capable)
                {
                    RetireEconomyIntent(state, intent, null, snap?.TurnNumber ?? 0);
                    AiDebugLog.Write($"[AI][V2][Economy][Mobile] retire {intent.IntentKey} "
                        + "reason=collector_lost_or_capability_lost");
                    return;
                }
                if (arrived && ei.LastConfirmedIncomeTick >= 0 && (!useful || !safe))
                {
                    HexCoord? home = ei.SafeReturnHex;
                    if (!home.HasValue || !IsProtectedEconomyHex(snap, player, home.Value))
                        home = SelectEconomyRecoveryTarget(snap, player, actor);
                    if (!home.HasValue)
                    {
                        RetireEconomyIntent(state, intent, null, snap?.TurnNumber ?? 0);
                        return;
                    }
                    MissionIntentKey oldKey = intent.IntentKey;
                    ei.Kind = EconomyTaskKind.ReturnCollector;
                    ei.TargetHex = home.Value;
                    intent.IntentKey = MissionIntentKey.For(intent);
                    if (!oldKey.Equals(intent.IntentKey))
                        rekeys.Add((oldKey, intent));
                    active.Add(intent);
                    AiDebugLog.Write($"[AI][V2][Economy][Mobile] return collector "
                        + $"#{actor.ArmyId} -> ({home.Value.Q},{home.Value.R})");
                    return;
                }
                // Holding the site IS the collector's activity (the planner proposes no
                // step for it): record it as progress so it neither stalls nor ages out.
                if (arrived)
                {
                    intent.LastProgressTurn = snap.TurnNumber;
                    intent.LastProtectedTurn = snap.TurnNumber;
                    intent.StallTurns = 0;
                }
                ResumeTransientSuspension(intent);
                active.Add(intent);
                return;
            }
            if (ei?.Kind == EconomyTaskKind.ReturnBuilder || ei?.Kind == EconomyTaskKind.ReturnCollector)
            {
                bool completed = actor != null && actor.Hex.Equals(ei.TargetHex);
                bool targetValid = IsProtectedEconomyHex(snap, player, ei.TargetHex);
                if (!targetValid && actor != null)
                {
                    HexCoord? retarget = SelectEconomyRecoveryTarget(snap, player, actor);
                    if (retarget.HasValue)
                    {
                        MissionIntentKey oldKey = intent.IntentKey;
                        ei.TargetHex = retarget.Value;
                        ei.SafeReturnHex = retarget.Value;
                        intent.StallTurns = 0;
                        completed = actor.Hex.Equals(ei.TargetHex);
                        intent.IntentKey = completed ? oldKey : MissionIntentKey.For(intent);
                        if (!completed && !oldKey.Equals(intent.IntentKey))
                            rekeys.Add((oldKey, intent));
                        targetValid = true;
                    }
                }
                if (completed || actor == null || !targetValid)
                {
                    RetireEconomyIntent(state, intent, null, snap?.TurnNumber ?? 0);
                    AiDebugLog.Write($"[AI][V2][Economy][Recovery] retire {intent.IntentKey} "
                        + $"arrived={(completed ? 1 : 0)} actor={(actor != null ? 1 : 0)} "
                        + $"target={(targetValid ? 1 : 0)}");
                    return;
                }
                ResumeTransientSuspension(intent);
                active.Add(intent);
                return;
            }

            bool completedBuild = ei == null || EconomyLifecycleState.ObjectiveSatisfied(player,
                new EconomyMissionTarget { Kind = ei.Kind, TargetHex = ei.TargetHex,
                    ResourceType = ei.ResourceType, BuilderArmyId = ei.BuilderArmyId });
            bool targetValidBuild = ei != null && (ei.Kind == EconomyTaskKind.FoundBase
                ? snap?.Self?.Hand?.Contains(ei.BuildCard) == true
                    && snap?.Economy?.BaseOpportunities?.Any(
                        site => site.Hex.Equals(ei.TargetHex)) == true
                : ei.ResourceType.HasValue && snap?.Economy?.IsExtractionActionable(
                    ei.TargetHex, ei.ResourceType.Value) == true);
            if (completedBuild || actor == null || !targetValidBuild)
            {
                RetireEconomyIntent(state, intent, null, snap?.TurnNumber ?? 0);
                AiDebugLog.Write($"[AI][V2][Economy] retire {intent.IntentKey} "
                    + $"completed={(completedBuild ? 1 : 0)} actor={(actor != null ? 1 : 0)} "
                    + $"target={(targetValidBuild ? 1 : 0)}");
                return;
            }
            IReadOnlyList<EconomyBuilderRouteSnapshot> routes = ei.Kind == EconomyTaskKind.FoundBase
                ? snap.Economy.BaseOpportunities.FirstOrDefault(x => x.Hex.Equals(ei.TargetHex)).BuilderRoutes
                : snap.Economy.ExtractionOpportunities.FirstOrDefault(x => x.Hex.Equals(ei.TargetHex)
                    && ei.ResourceType.HasValue && x.ResourceType == ei.ResourceType.Value).BuilderRoutes;
            bool recoveredBuilder = false;
            foreach (EconomyBuilderRouteSnapshot route in routes
                ?? System.Array.Empty<EconomyBuilderRouteSnapshot>())
            {
                if (route.ArmyId != actor.ArmyId) continue;
                var suitability = DemandLayer.AssessEconomyArmy(snap, ei.TargetHex, route,
                    actor, ei.BuildApCost, includeReturn: ei.Kind == EconomyTaskKind.BuildExtraction);
                if (suitability.Suitability != DemandLayer.EconomyArmySuitability.Ineligible
                    || suitability.IneligibleReason == "escort_activated_this_turn")
                    break;
                // A composition failure does not heal when movement resets. Release the
                // outbound envelope and let the existing recovery lifecycle own the actor.
                MissionLeaseBook.ReleaseByOwner(player, snap.TurnNumber,
                    EconomyMissionPlanner.OwnerKey(intent.LastAttemptKey));
                AiDebugLog.Write($"[AI][V2][Economy] recover {intent.IntentKey} actor=#{actor.ArmyId} "
                    + $"reason={suitability.IneligibleReason}");
                BeginEconomyBuilderRecovery(player, snap, new AxisDemand
                {
                    RequestingAxis = DesireAxis.Economy,
                    Capability = CapabilityKind.EconomicInfrastructure,
                    TargetHex = ei.TargetHex, EconomySiteValue = ei.BuildValue,
                }, actor.ArmyId, snap.TurnNumber);
                // Publish a replacement recovery in this same pass so downstream actor
                // commitments cannot briefly expose the returning builder as unassigned.
                if (state.TryGet(intent.IntentKey, out MissionIntent recovery)
                    && recovery.Economy?.Kind == EconomyTaskKind.ReturnBuilder)
                    active.Add(recovery);
                // Recovery may instead have released the actor at a protected hex.
                recoveredBuilder = true;
                break;
            }
            if (recoveredBuilder) return;
            ResumeTransientSuspension(intent);
            if (intent.Status == IntentStatus.Active) active.Add(intent);
            return;
        }

        private static void CaptureEconomyProvisionFacts(ProvisionedMission pm, MissionTurnOutcome o)
        {
            o.HasEconomyPayload = true;
            o.EconomyTarget = pm.EconomyTarget;
            o.EconomyLoanSource = pm.EconomyLoanSource;
        }

        private static void CaptureEconomyExecutionFacts(ExecutionResult e, MissionTurnOutcome o)
        {
            o.EconomyBuildCompleted = e.InfrastructureChanged;
            o.PayloadForWrite<EconomyStepPayload>().DeliveryReady = e.EconomyDeliveryReady;
            o.PayloadForWrite<EconomyStepPayload>().Holding = e.EconomyHolding;
        }

    }
}
