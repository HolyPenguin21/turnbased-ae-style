using System.Collections.Generic;
using System.Linq;
using Game.Cards;
using Game.Economy;
using Game.HexGrid;
using Game.Players;
using static Game.Ai.V2.MissionContinuityLayer;

namespace Game.Ai.V2
{
    // The Economy line's continuity rules (builder delivery and recovery, build-site ownership,
    // loans, collectors); MissionContinuityLayer composes them by mission kind.
    internal static class EconomyContinuity
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

        internal static void ClassifyEconomyStep(ExecutionResult e, MissionStepResult o)
        {
            // A committed roster mutation remains progress if its pinned tail became stale.
            if (e.EconomyPrepared && e.StopReason == ExecutionStopReason.TargetInvalidated)
            {
                o.Disposition = MissionStepDisposition.Progress;
                return;
            }
            switch (e.StopReason)
            {
                case ExecutionStopReason.StepCompleted:
                case ExecutionStopReason.OutOfMovement:
                    o.Disposition = MissionStepDisposition.Progress;
                    break;
                case ExecutionStopReason.NoSafeStep:
                case ExecutionStopReason.MoveRejected:
                // Economy audit B2 — every execution-side TargetInvalidated of an Economy step
                // is transient (stale plan, unaffordable activation, AP/resources of a pinned
                // preparation gone this pass), never proof the durable build is invalid:
                // Continuity re-validates the objective itself (ResolveActive).
                case ExecutionStopReason.TargetInvalidated:
                    o.Disposition = MissionStepDisposition.Waiting;
                    break;
                default:
                    o.Fail();
                    break;
            }
            return;
        }
        internal static void RecordEconomyStepProgress(MissionIntentState state, MissionStepResult o, int turn)
        {
            // A build project that really progressed this turn is not failing to deliver: end its
            // delivery-failure streak before any branch below may record a failure for it.
            if (o.MissionKind == MissionKind.Economy
                && (o.MadeProgress || o.Disposition == MissionStepDisposition.Completed)
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
        internal static bool TryKeepEconomyRecovery(MissionIntentState state, AiAllocatorState allocState,
            MissionIntent intent, MissionStepResult o, int turn)
        {
            bool returnRecoveryOutcome = o.MissionKind == MissionKind.Economy
                && (o.EconomyFacts().EconomyTarget.Kind == EconomyTaskKind.ReturnBuilder
                    || o.EconomyFacts().EconomyTarget.Kind == EconomyTaskKind.ReturnCollector
                    || intent?.Economy?.Kind == EconomyTaskKind.ReturnBuilder
                    || intent?.Economy?.Kind == EconomyTaskKind.ReturnCollector);
            if (returnRecoveryOutcome && intent != null)
            {
                KeepEconomyReturn(state, intent, o, turn);
                return true;
            }
            return false;
        }
        internal static bool TryHandleEconomyNoProgress(MissionIntentState state, AiAllocatorState allocState,
            MissionIntent intent, MissionStepResult o, int turn)
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
                // The route failure ends the outbound commitment, not the builder's lifecycle.
                RecordEndedOutboundBuilder(state, intent);
                RetireEconomyIntent(state, intent, o, turn);
                return true;
            }

            return false;
        }

        internal static bool TryCreateEconomyStep(MissionIntentState state, AiAllocatorState allocState,
            MissionIntent intent, MissionStepResult o, int turn)
        {
            if (!(o.EconomyFacts().HasEconomyPayload && o.MadeProgress)) return false;
            CreateEconomyIntent(state, o, turn);
            return true;
        }


        // A materialized outcome carries its EconomyTarget directly, but a fresh mission that failed
        // provisioning before ever producing a ProvisionedMission only has it on the proposal.
        private static bool TryGetEconomyTarget(MissionStepResult o, out EconomyMissionTarget target)
        {
            if (o.EconomyFacts().HasEconomyPayload)
            {
                target = o.EconomyFacts().EconomyTarget;
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

        private static void CreateEconomyIntent(MissionIntentState state, MissionStepResult o, int turn)
        {
            EconomyMissionTarget t = o.EconomyFacts().EconomyTarget;
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
                Loaned = o.EconomyFacts().EconomyLoanSource.HasValue,
                LoanSource = o.EconomyFacts().EconomyLoanSource ?? default,
            };
            CommitmentTier funding = o.EconomyFacts().EconomyBuildCompleted ? CommitmentTier.Hard : CommitmentTier.Soft;
            MissionIntent intent = NewIntent(o, turn, MissionKind.Economy, funding, ei);
            state.Put(intent);
            AiDebugLog.Write($"[AI][V2][Economy] continuity create {intent.IntentKey} mover=#{o.MoverArmyId}");
        }

        internal static void RepayEconomyLoan(MissionIntentState state, MissionIntent economy,
            MissionStepResult outcome)
        {
            MissionIntentKey? source = outcome?.EconomyFacts().EconomyLoanSource;
            if (!source.HasValue && economy?.Economy?.Loaned == true)
                source = economy.Economy.LoanSource;
            if (source.HasValue && state.TryGet(source.Value, out MissionIntent lender))
                ResumeEconomyLender(lender);
        }

        // The ONE retirement of an Economy intent: a borrowed Recon/Raid owner gets its actor back,
        // this owner's turn-scoped H/E/M/T/AP holds are released (Phase B may spend them for the
        // rest of the turn), then the intent goes. `outcome` may carry the loan of a mission that
        // never became durable. Economy audit B8.
        internal static void RetireEconomyIntent(MissionIntentState state, MissionIntent intent,
            MissionStepResult outcome, int turn, bool returnLoan = true)
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
        private static bool IsEconomyRouteFailure(MissionStepResult o) =>
            o.ProvisionFailureKindValue == ProvisionFailureKind.NoExecutableStep
            || (!o.ProvisionFailureKindValue.HasValue
                && (o.StopReason == ExecutionStopReason.NoSafeStep
                    || o.StopReason == ExecutionStopReason.MoveRejected));

        // Economy return legs are preserved through every transient failure (a lost shelter is re-targeted
        // by ResolveActive, a blocked way home may clear) — but only while it still gets home:
        // a builder that has not advanced for commitmentMaxTurns is released, its lender resumed,
        // instead of holding the hero (and the lender) forever. Economy audit S1.
        private static void KeepEconomyReturn(MissionIntentState state, MissionIntent intent,
            MissionStepResult o, int turn)
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
                AiDebugLog.Write($"[AI][V2][Economy][Recovery] release {intent.IntentKey} - no progress "
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
        internal static void PrepareEconomyRetirement(MissionIntentState state, AiAllocatorState allocState,
            MissionIntent intent, MissionStepResult o, int turn)
        {
            // Preserve the old null-intent loan hand-back (the attempt may never become durable).
            // Ownership cleanup belongs to the common retirement boundary, not this domain hook.
            if (intent == null || intent.Kind == MissionKind.Economy) RepayEconomyLoan(state, intent, o);
            RecordEndedOutboundBuilder(state, intent);
        }

        // An outbound build retired by an outcome leaves its builder wherever the step stopped.
        // Recorded here, decided by RecoverEndedOutboundBuilders on the next snapshot.
        private static void RecordEndedOutboundBuilder(MissionIntentState state, MissionIntent intent)
        {
            if (state == null || !IsLiveEconomyBuild(intent) || !intent.PreferredMoverArmyId.HasValue)
                return;
            state.Economy.RecordEndedOutboundBuilder(intent.PreferredMoverArmyId.Value,
                intent.Economy.TargetHex, intent.Economy.BuildValue);
        }

        // ResolveActive preparer: each builder an outcome released in the field enters the ONE
        // builder recovery decision (BeginEconomyBuilderRecovery: a ReturnBuilder home, or a
        // release when no shelter is reachable) unless another operation already owns it — a
        // repaid loan donor, a fresh delivery, a Phase B handoff.
        internal static void RecoverEndedOutboundBuilders(PlayerSetupData player, WorldSnapshot snap,
            ActiveResolution pass)
        {
            MissionIntentState state = pass.State;
            if (snap == null || !state.Economy.HasEndedOutboundBuilders)
                return;
            ActorCommitments owners = ActorCommitments.FromIntents(state.All, snap, pass.ReconObjectives);
            foreach ((int builderArmyId, HexCoord site, float value) in state.Economy.TakeEndedOutboundBuilders())
            {
                // Only a builder left in the field: a garrison (extraction never ran) or a hero
                // still on an own base is already home, and must not be walked to another one.
                ArmySnapshot builder = snap.Self?.Armies?.FirstOrDefault(a => a != null
                    && a.ArmyId == builderArmyId);
                if (builder == null || builder.IsGarrison || IsProtectedEconomyHex(snap, player, builder.Hex)
                    || owners.IsArmyClaimed(builderArmyId))
                    continue;
                AiDebugLog.Write($"[AI][V2][Economy] recover ended outbound build "
                    + $"({site.Q},{site.R}) actor=#{builderArmyId}");
                BeginEconomyBuilderRecovery(player, snap, new AxisDemand
                {
                    RequestingAxis = DesireAxis.Economy,
                    Capability = CapabilityKind.EconomicInfrastructure,
                    TargetHex = site, EconomySiteValue = value,
                }, builderArmyId, snap.TurnNumber);
            }
        }

        internal static void ApplyEconomyStepFacts(MissionIntentState state, AiAllocatorState allocator,
            MissionIntent intent, MissionStepResult o, int turn)
        {
            if (o.EconomyFacts().HasEconomyPayload && intent.Economy != null)
            {
                intent.Economy.TargetHex = o.EconomyFacts().EconomyTarget.TargetHex;
                intent.Economy.BuilderArmyId = intent.PreferredMoverArmyId;
                // The mission has already used the continuity-pinned builder's world score.
                // Persist the last accepted intrinsic value so a later turn without a refreshed
                // demand cannot silently revive the unrelated site-only BuildValue.
                if (o.MadeProgress && o.Proposal?.Target is EconomyMissionTarget scored
                    && scored.Kind == intent.Economy.Kind
                    && scored.TargetHex.Equals(intent.Economy.TargetHex)
                    && scored.BuilderArmyId == intent.PreferredMoverArmyId)
                    intent.Economy.IntrinsicValue = o.Proposal.BaseValue;
                if (o.EconomyFacts().EconomyBuildCompleted) intent.Funding = CommitmentTier.Hard;
            }

        }

        internal static bool TryHandleEconomyCapabilityFailure(MissionIntentState state, AiAllocatorState allocState,
            MissionIntent intent, MissionStepResult o, int turn)
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
                RecordEndedOutboundBuilder(state, intent);
                RetireEconomyIntent(state, intent, o, turn);
                StartPersistentCooldown(allocState, intent.LastAttemptKey, intent.Kind, turn,
                    "BaseExpansionDeliverySuppressed");
                AiDebugLog.Write($"[AI][V2] continuity - [{AiV2Trace.FormatCorrelation(o.Proposal)}] "
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
                RecordEndedOutboundBuilder(state, intent);
                RetireEconomyIntent(state, intent, o, turn);
                StartPersistentCooldown(allocState, intent.LastAttemptKey, intent.Kind, turn,
                    "ExtractionDeliverySuppressed");
                AiDebugLog.Write($"[AI][V2] continuity - [{AiV2Trace.FormatCorrelation(o.Proposal)}] "
                    + $"{intent.IntentKey} Extraction delivery repeatedly failed "
                    + $"({o.ProvisionFailureKindValue}); suppressed for cooldown, retired");
                return true;
            }
            return false;
        }

        internal static void RepairEconomyLoans(Game.Players.PlayerSetupData player, WorldSnapshot snap, ActiveResolution pass)
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

        internal static void ResolveEconomyOperation(Game.Players.PlayerSetupData player, WorldSnapshot snap, MissionIntent intent, ActiveResolution pass)
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
                    ei.LastIncomeOpportunityTurn = snap.TurnNumber;
                // Economy audit B12 — the SAME usefulness test Analysis admits a collector
                // with (UsefulMarginalIncomeGain), judged without the income this collector
                // itself now produces; a second "income below target" rule sent a still
                // useful collector home and Analysis sent it straight back.
                int contribution = capable ? WorldAnalysis.CollectorMarginalIncome(snap, actor,
                    ei.TargetHex, ei.ResourceType.Value) : 0;
                EconomyResourceStanding standing = snap?.Economy?.PerType?
                    .FirstOrDefault(x => x.Type == ei.ResourceType) ?? default;
                bool useful = contribution > 0 && (arrived
                    ? standing.UsefulRetainedIncomeGain(contribution)
                    : standing.UsefulMarginalIncomeGain(contribution)) > AiConfigV2.allocatorSliceEpsilon;
                bool safe = WorldAnalysis.CollectorRouteSafe(snap, actor, ei.TargetHex);
                ei.ExpectedMarginalYield = contribution;
                if (!capable)
                {
                    RetireEconomyIntent(state, intent, null, snap?.TurnNumber ?? 0);
                    AiDebugLog.Write($"[AI][V2][Economy][Mobile] retire {intent.IntentKey} "
                        + "reason=collector_lost_or_capability_lost");
                    return;
                }
                if (!safe || contribution <= 0
                    || (!useful && (!arrived || ei.LastIncomeOpportunityTurn >= 0)))
                {
                    HexCoord? home = ei.SafeReturnHex;
                    bool ShelterSafe(HexCoord h) => IsProtectedEconomyHex(snap, player, h)
                        && !WorldAnalysis.KnownHostileAtHex(snap, h)
                        && WorldAnalysis.KnownThreatsAffectingEconomyRoute(snap, new[] { h }).Count == 0
                        && actor.EconomyHomeRouteCosts?.ContainsKey(h) == true;
                    if (!home.HasValue || !ShelterSafe(home.Value))
                        home = SelectEconomyHome(snap, actor.Hex,
                            (actor.EconomyHomeRouteCosts ?? new Dictionary<HexCoord, int>())
                                .Where(x => ShelterSafe(x.Key)).ToDictionary(x => x.Key, x => x.Value));
                    if (!home.HasValue)
                    {
                        // Keep ownership for the bounded ordinary stall lifecycle. The planner
                        // defers this unsafe/unproductive site; a later scan may prove a shelter.
                        ResumeTransientSuspension(intent);
                        active.Add(intent);
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
                if (arrived && useful && safe)
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
            // A STRUCTURAL invalidation, unlike a short AP or a stale step: the builder stands on a hex
            // whose served operator it would take away and cannot leave home. FoundBase and
            // BuildExtraction share this branch; the outbound envelope is released, the intent retired
            // (no cooldown: the project itself is not at fault) and Demand no longer offers this actor.
            if (actor.OperatorDutyBlocksDeparture && !actor.Hex.Equals(ei.TargetHex))
            {
                MissionLeaseBook.ReleaseByOwner(player, snap.TurnNumber,
                    EconomyMissionPlanner.OwnerKey(intent.LastAttemptKey));
                RetireEconomyIntent(state, intent, null, snap?.TurnNumber ?? 0);
                AiDebugLog.Write($"[AI][V2][Economy] retire {intent.IntentKey} actor=#{actor.ArmyId} "
                    + "reason=operator_leaves_served_facility");
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
                var suitability = EconomyBuilderSelection.AssessEconomyArmy(snap, ei.TargetHex, route,
                    actor, ei.BuildApCost, includeReturn: ei.Kind == EconomyTaskKind.BuildExtraction);
                if (suitability.Suitability != EconomyBuilderSelection.EconomyArmySuitability.Ineligible
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

        internal static void CaptureEconomyProvisionFacts(ProvisionedMission pm, MissionStepResult o)
        {
            o.EconomyFactsForWrite().HasEconomyPayload = true;
            o.EconomyFactsForWrite().EconomyTarget = pm.EconomyTarget;
            o.EconomyFactsForWrite().EconomyLoanSource = pm.EconomyLoanSource;
        }

        internal static void CaptureEconomyExecutionFacts(ExecutionResult e, MissionStepResult o)
        {
            o.EconomyFactsForWrite().EconomyBuildCompleted = e.InfrastructureChanged;
            o.PayloadForWrite<EconomyStepPayload>().DeliveryReady = e.EconomyDeliveryReady;
            o.PayloadForWrite<EconomyStepPayload>().Holding = e.EconomyHolding;
            if (e.ActorMaterialized)
                o.PayloadForWrite<EconomyStepPayload>().MaterializationSourceArmyId =
                    e.EconomyMaterializationSourceArmyId;
        }

        internal static HexCoord? SelectEconomyRecoveryTarget(WorldSnapshot snap,
            PlayerSetupData player, ArmySnapshot actor, bool avoidCurrentHex = false)
        {
            if (actor == null || player == null) return null;
            return SelectEconomyHome(snap, actor.Hex, actor.EconomyHomeRouteCosts, avoidCurrentHex);
        }

        // Economy chooses the closest physically reachable shelter. Combat's "most active base"
        // ranking intentionally has a different purpose and keeps its separate owner.
        internal static HexCoord? SelectEconomyHome(WorldSnapshot snap, HexCoord from,
            IReadOnlyDictionary<HexCoord, int> routeCosts, bool avoidCurrentHex = false)
        {
            if (snap?.Self?.BaseHexes == null || routeCosts == null) return null;
            IEnumerable<HexCoord> candidates = snap.Self.BaseHexes.Where(h =>
                routeCosts.TryGetValue(h, out int cost) && cost < int.MaxValue);
            if (avoidCurrentHex && candidates.Any(h => !h.Equals(from)))
                candidates = candidates.Where(h => !h.Equals(from));
            return candidates.OrderBy(h => routeCosts[h])
                .ThenBy(h => AiReturnBasePolicy.BaseThreatSeverityAt(snap, h))
                .ThenByDescending(h => h.Equals(snap.Self.Citadel))
                .ThenBy(h => h.Q).ThenBy(h => h.R)
                .Select(h => (HexCoord?)h).FirstOrDefault();
        }

        // Exact obligation identity, distinct from the coarser site lease. The optional card is
        // an unpinned durable choice; resource type always distinguishes extraction obligations.
        internal static bool MatchesEconomyBuild(MissionIntent intent, EconomyTaskKind kind,
            HexCoord hex, ResourceType? resource, CardData card) => HoldsEconomyBuildSite(intent, hex)
                && intent.Economy.Kind == kind
                && (kind == EconomyTaskKind.FoundBase || intent.Economy.ResourceType == resource)
                && (intent.Economy.BuildCard == null || intent.Economy.BuildCard == card);

        internal static bool HasEconomyBuildCommitment(IReadOnlyList<MissionIntent> intents,
            AxisDemand demand) => demand?.TargetHex != null && intents != null
                && intents.Any(i => MatchesEconomyBuild(i, DemandLayer.EconomyBuildKind(demand),
                    demand.TargetHex.Value, demand.EconomyResourceType, demand.EconomyBuildCard));

        internal static bool RequiresEconomyBuilderRecovery(EconomyTaskKind completedKind,
            MissionIntent lender, bool underImmediateThreat, bool alreadyProtected,
            bool hasRecoveryTarget)
        {
            if (!hasRecoveryTarget) return false;
            if (alreadyProtected && !underImmediateThreat) return false;
            if (lender?.Kind == MissionKind.Scout && lender.Scout != null
                && !underImmediateThreat)
                return false;
            return true;
        }

        // Continuity is the sole owner of durable build-site leases. Physical placement may
        // allow a completed extraction site to become a Base later; two unfinished owners of
        // the SAME site are nevertheless incompatible. Recovery/collection is not construction.
        internal static bool HoldsEconomyBuildSite(MissionIntent intent, HexCoord hex) =>
            IsLiveEconomyBuild(intent) && intent.Economy.TargetHex.Equals(hex);

        // A build obligation (BuildExtraction / FoundBase) that still holds its site lease: Active,
        // or Suspended on a transient reason ResolveActive re-tests. The one predicate behind the
        // lease grant AND every Demand / Phase A "is this build committed" read — a transiently
        // suspended build is not a free site to originate a second builder for (audit B11).
        internal static bool IsLiveEconomyBuild(MissionIntent intent) =>
            intent != null && intent.Kind == MissionKind.Economy
            && (intent.Status == IntentStatus.Active || intent.Status == IntentStatus.Suspended)
            && intent.Economy != null
            && (intent.Economy.Kind == EconomyTaskKind.FoundBase
                || intent.Economy.Kind == EconomyTaskKind.BuildExtraction);

        // Same actor, same objective and same physical card use existing takeover ownership
        // policy. An independent actor/card/objective cannot acquire an already-leased site.
        internal static bool CanGrantEconomyBuildSite(PlayerSetupData player, HexCoord hex,
            MissionIntentKey candidateKey, int builderArmyId, CardData card)
        {
            if (player == null) return false;
            return !MissionIntentRegistry.GetOrCreate(player).All.Any(intent =>
                HoldsEconomyBuildSite(intent, hex)
                && intent.PreferredMoverArmyId != builderArmyId
                && !intent.IntentKey.Equals(candidateKey)
                && (card == null || intent.Economy.BuildCard != card));
        }

        // Materialization has delivered the Hero for one concrete Economy prerequisite (Capability.
        // Hero), OR Provisioning has just bound an ALREADY-EXISTING mobile builder to a multi-turn
        // delivery (Capability.EconomicInfrastructure/EconomicExpansionBase — see the direct-army
        // path in ProvisioningManager.ProvisionEconomy). Either way, the transaction-local capability
        // lease ends at this handoff; Continuity immediately becomes the sole owner of the actor and
        // exact build objective — Phase A's PhaseAEconomyHolds.ProtectActiveBuilds then protects the
        // full H/E/M/T vector every following turn regardless of remaining travel distance, so
        // InfrastructureFulfillment's own one-turn horizon only ever has to cover the turn BEFORE
        // this intent exists.
        internal static MissionIntent BeginEconomyDelivery(PlayerSetupData player,
            AxisDemand demand, int builderArmyId, int turn)
        {
            if (player == null || demand?.TargetHex == null
                || demand.RequestingAxis != DesireAxis.Economy
                || (demand.Capability != CapabilityKind.Hero
                    && demand.Capability != CapabilityKind.EconomicInfrastructure
                    && demand.Capability != CapabilityKind.EconomicExpansionBase))
                return null;

            EconomyTaskKind kind = DemandLayer.EconomyBuildKind(demand);
            var objective = new EconomyIntent
            {
                Kind = kind,
                TargetHex = demand.TargetHex.Value,
                ResourceType = demand.EconomyResourceType,
                BuilderArmyId = builderArmyId,
                BuildCard = demand.EconomyBuildCard,
                BuildResourceCost = demand.EconomyBuildResourceCost,
                BuildApCost = demand.EconomyBuildApCost,
                IntrinsicValue = demand.Value,
                BuildValue = demand.EconomySiteValue,
                MinimumFollowupAp = demand.MinimumFollowupAp,
            };
            var intent = new MissionIntent
            {
                Kind = MissionKind.Economy,
                Funding = CommitmentTier.Soft,
                Status = IntentStatus.Active,
                Suspended = SuspendReason.None,
                Objective = objective,
                CreatedTurn = turn,
                TurnsActive = 1,
                LastReconciledTurn = turn,
                LastProgressTurn = turn,
                PreferredMoverArmyId = builderArmyId,
            };
            intent.IntentKey = MissionIntentKey.For(intent);
            intent.LastAttemptKey = StableMissionKey.ForEconomy(kind, intent.IntentKey.ObjectiveId,
                objective.TargetHex);

            MissionIntentState state = MissionIntentRegistry.GetOrCreate(player);

            // Reentry: this exact (target/resource/kind, actor) delivery is already the active
            // intent — return the SAME object so its CreatedTurn/TurnsActive/StepsMovedTotal/
            // Funding/LoanSource history survives, instead of deleting and rebuilding it fresh
            // every time Provisioning re-confirms the same ongoing multi-turn walk.
            if (state.TryGet(intent.IntentKey, out MissionIntent existing)
                && existing.Status == IntentStatus.Active
                && existing.PreferredMoverArmyId == builderArmyId)
            {
                AiDebugLog.Write($"[ECO][Continuity] intent={existing.IntentKey} "
                    + $"actor=#{builderArmyId} decision=REUSE createdTurn={existing.CreatedTurn} "
                    + $"progress={existing.StepsMovedTotal} funding={existing.Funding}");
                return existing;
            }

            // Reject independent spatial contenders BEFORE retiring any existing intent or
            // touching its per-owner reserves. Demand-level dedup is not a durable ownership gate.
            if (!CanGrantEconomyBuildSite(player, objective.TargetHex, intent.IntentKey,
                    builderArmyId, objective.BuildCard))
            {
                AiDebugLog.Write($"[AI][V2][Economy] ownership refused {intent.IntentKey} "
                    + $"actor=#{builderArmyId} reason=site_owned_by_independent_contender");
                return null;
            }

            // This is the ONE place
            // Economy ownership is granted, so it is the one place that resolves ownership
            // CONFLICTS. Once the reentry case above has returned, a pre-existing Economy intent is
            // superseded only when it actually collides with the new grant:
            //   · same actor (PreferredMoverArmyId) — one army cannot hold two assignments, and a
            //     fresh delivery supersedes that same actor's own ReturnBuilder recovery walk (a
            //     ReturnBuilder belonging to a DIFFERENT actor is untouched);
            //   · same objective identity (IntentKey) — a takeover of this exact objective;
            //   · same physical build card — one card cannot fund two sites at once.
            // Any OTHER active Economy intent — a different target run by a different actor with a
            // different card — is an independent delivery and survives this handoff untouched.
            bool ConflictsWithGrant(MissionIntent i)
            {
                if (i == null || i.Kind != MissionKind.Economy) return false;
                if (i.PreferredMoverArmyId == builderArmyId) return true;
                if (i.Economy?.Kind == EconomyTaskKind.ReturnBuilder) return false;
                if (i.IntentKey.Equals(intent.IntentKey)) return true;
                return objective.BuildCard != null && i.Economy?.BuildCard == objective.BuildCard;
            }
            // A takeover/redirect conflict retires the DISPLACED intent through the ordinary
            // retirement, releasing its own reservation, so a forced handoff cannot leave this
            // turn's H/E/M/T hold reserved for an owner key nothing will ever complete or release.
            // Only a superseded ReturnBuilder returns its loan here: its actor goes back to the
            // lender's side of the ledger; a superseded build's actor stays on Economy work.
            foreach (MissionIntent stale in state.All.Where(ConflictsWithGrant).ToList())
                EconomyContinuity.RetireEconomyIntent(state, stale, null, turn,
                    returnLoan: stale.Economy?.Kind == EconomyTaskKind.ReturnBuilder);
            state.Put(intent);
            AiDebugLog.Write($"[AI][V2][Economy] materialization handoff {intent.IntentKey} "
                + $"actor=#{builderArmyId} funding=Soft");
            return intent;
        }

        // The existing Continuity owner performs the only Base commitment switch. Same
        // card/actor are mandatory: no double-booking, no accidental donor/loan release.
        // Release the old reservation owner before rekeying; Phase A then reserves the new
        // target using the SAME intent object already referenced by the active-intent list.
        internal static bool TryRetargetCommittedBase(PlayerSetupData player,
            MissionIntent incumbent, AxisDemand challenger, int turn)
        {
            if (player == null || !DemandLayer.CanReplaceCommittedBase(incumbent, challenger)
                || (incumbent.LastProgressTurn == turn && incumbent.StepsMovedTotal > 0))
                return false; // an actor that already advanced this turn cannot be rerouted mid-step
            MissionIntentState state = MissionIntentRegistry.GetOrCreate(player);
            MissionIntentKey oldKey = incumbent.IntentKey;
            if (!state.TryGet(oldKey, out MissionIntent owned)
                || !object.ReferenceEquals(owned, incumbent))
                return false;
            HexCoord target = challenger.TargetHex.Value;
            var newKey = MissionIntentKey.ForEconomy(EconomyTaskKind.FoundBase, 0, target);
            if (state.TryGet(newKey, out MissionIntent occupied)
                && !object.ReferenceEquals(occupied, incumbent))
                return false;

            // Retarget has no displacement transaction for a third-party site lease.
            // Validate BEFORE releasing the original owner or rewriting its objective.
            if (state.All.Any(i => !object.ReferenceEquals(i, incumbent)
                    && HoldsEconomyBuildSite(i, target)))
                return false;

            string oldOwner = EconomyMissionPlanner.OwnerKey(incumbent.LastAttemptKey);
            string oldTargetOwner = InfrastructureFulfillment.EconomyReservationOwner(new AxisDemand
            {
                Capability = CapabilityKind.EconomicExpansionBase,
                TargetHex = incumbent.Economy.TargetHex,
            });
            MissionLeaseBook.ReleaseByOwner(player, turn, oldOwner);
            if (oldTargetOwner != oldOwner)
                MissionLeaseBook.ReleaseByOwner(player, turn, oldTargetOwner);
            state.Remove(oldKey);
            EconomyIntent objective = incumbent.Economy;
            objective.TargetHex = target;
            objective.BuildCard = challenger.EconomyBuildCard;
            objective.BuildResourceCost = challenger.EconomyBuildResourceCost;
            objective.BuildApCost = challenger.EconomyBuildApCost;
            objective.MinimumFollowupAp = challenger.MinimumFollowupAp;
            objective.IntrinsicValue = challenger.Value;
            objective.BuildValue = challenger.EconomySiteValue;
            objective.BuilderArmyId = incumbent.PreferredMoverArmyId;
            incumbent.IntentKey = newKey;
            incumbent.LastAttemptKey = StableMissionKey.ForEconomy(EconomyTaskKind.FoundBase, 0, target);
            incumbent.CreatedTurn = turn;
            incumbent.TurnsActive = 1;
            incumbent.LastProgressTurn = turn;
            incumbent.StallTurns = 0;
            incumbent.StepsMovedTotal = 0;
            incumbent.CumulativeApSpent = 0f;
            state.Put(incumbent);
            return true;
        }

        // Phase A supplies an authoritative revaluation of this build: future benefit and the
        // pinned builder's remaining route, never cumulative sunk AP or already-settled steps.
        internal static bool TryReleaseUnprofitableBuild(MissionIntentState state, PlayerSetupData player,
            MissionIntent incumbent, AxisDemand refreshed, int turn)
        {
            if (state == null || player == null || !IsLiveEconomyBuild(incumbent)
                || refreshed == null || !HasEconomyBuildCommitment(new[] { incumbent }, refreshed))
                return false;
            bool extraction = incumbent.Economy.Kind == EconomyTaskKind.BuildExtraction;
            bool profitable = refreshed.Value > AiConfigV2.allocatorSliceEpsilon
                && (!extraction || refreshed.EconomyPaybackTurns <= AiConfigV2.economyExtractionMaxPaybackTurns);
            if (profitable || (!extraction && incumbent.LastProgressTurn == turn
                    && incumbent.StepsMovedTotal > 0))
                return false;
            if (!state.TryGet(incumbent.IntentKey, out MissionIntent owned)
                || !object.ReferenceEquals(owned, incumbent))
                return false;
            string owner = EconomyMissionPlanner.OwnerKey(incumbent.LastAttemptKey);
            string targetOwner = InfrastructureFulfillment.EconomyReservationOwner(new AxisDemand
            {
                Capability = extraction ? CapabilityKind.EconomicInfrastructure
                    : CapabilityKind.EconomicExpansionBase,
                TargetHex = incumbent.Economy.TargetHex,
                EconomyResourceType = incumbent.Economy.ResourceType,
            });
            MissionLeaseBook.ReleaseByOwner(player, turn, owner);
            if (targetOwner != owner)
                MissionLeaseBook.ReleaseByOwner(player, turn, targetOwner);
            RecordEndedOutboundBuilder(state, incumbent);
            RetireEconomyIntent(state, incumbent, null, turn);
            if (extraction)
                state.Economy.SuppressExtractionDelivery(turn, incumbent.Economy.ResourceType,
                    incumbent.Economy.TargetHex);
            AiDebugLog.Write($"[AI][V2][Economy] release {incumbent.IntentKey} - refreshed "
                + $"{incumbent.Economy.Kind} value={refreshed.Value:0.##} "
                + $"payback={refreshed.EconomyPaybackTurns:0.##} no longer clears admission; bank freed");
            return true;
        }

        internal static void BeginEconomyBuilderRecovery(PlayerSetupData player,
            WorldSnapshot snap, AxisDemand completedDemand, int builderArmyId, int turn)
        {
            if (player == null || snap == null || completedDemand?.TargetHex == null)
                return;
            MissionIntentState state = MissionIntentRegistry.GetOrCreate(player);
            // With several builds legally active at once, "the first Economy
            // intent holding this actor" must resolve to the CONCRETE mission that just completed.
            // Prefer the intent whose own target matches the completed demand; only then fall back
            // to the actor match, and make that fallback deterministic instead of dictionary-order.
            List<MissionIntent> actorOwned = state.All.Where(i => i != null
                && i.Kind == MissionKind.Economy && i.PreferredMoverArmyId == builderArmyId).ToList();
            MissionIntent economy = actorOwned
                .OrderByDescending(i => i.Economy != null && completedDemand.TargetHex.HasValue
                    && i.Economy.TargetHex.Equals(completedDemand.TargetHex.Value) ? 1 : 0)
                .ThenBy(i => i.IntentKey)
                .FirstOrDefault();
            MissionIntent lender = null;
            if (economy?.Economy?.Loaned == true)
                state.TryGet(economy.Economy.LoanSource, out lender);
            if (lender == null)
                lender = state.All.FirstOrDefault(i => i != null && i.Kind != MissionKind.Economy
                    && i.PreferredMoverArmyId == builderArmyId
                    && EconomyBuilderSelection.EconomyDonorStructurallyEligible(i));

            ArmySnapshot actor = snap.Self?.Armies?.FirstOrDefault(a => a != null
                && a.ArmyId == builderArmyId && a.HasHero && !a.IsPrison && !a.IsAir);
            if (actor == null)
            {
                ResumeEconomyLender(lender);
                EconomyContinuity.RetireEconomyIntent(state, economy, null, turn, returnLoan: false);
                return;
            }

            EconomyTaskKind completedKind = DemandLayer.EconomyBuildKind(completedDemand);
            // The same threat witness that sizes a builder's escort: a known enemy army close
            // enough to the finished site sends the builder home.
            bool threatened = WorldAnalysis.KnownThreatsAffectingEconomyRoute(
                snap, new[] { actor.Hex }).Count > 0;
            bool alreadyProtected = (completedKind == EconomyTaskKind.FoundBase && !threatened)
                || IsProtectedEconomyHex(snap, player, actor.Hex);
            HexCoord? target = SelectEconomyRecoveryTarget(
                snap, player, actor, avoidCurrentHex: threatened);
            if (!RequiresEconomyBuilderRecovery(completedKind, lender, threatened,
                    alreadyProtected, target.HasValue))
            {
                ResumeEconomyLender(lender);
                EconomyContinuity.RetireEconomyIntent(state, economy, null, turn, returnLoan: false);
                AiDebugLog.Write($"[AI][V2][Economy][Recovery] actor=#{builderArmyId} "
                    + "released at safe build hex / resumed scout");
                return;
            }

            MissionIntentKey oldKey = economy?.IntentKey ?? default;
            MissionIntent recovery = economy ?? new MissionIntent
            {
                Kind = MissionKind.Economy, CreatedTurn = turn, TurnsActive = 1,
                LastReconciledTurn = turn, PreferredMoverArmyId = builderArmyId,
            };
            recovery.Kind = MissionKind.Economy;
            recovery.Funding = CommitmentTier.Hard;
            recovery.Status = IntentStatus.Active;
            recovery.Suspended = SuspendReason.None;
            recovery.PreferredMoverArmyId = builderArmyId;
            recovery.LastProgressTurn = turn;
            recovery.StallTurns = 0;
            recovery.Objective = new EconomyIntent
            {
                Kind = EconomyTaskKind.ReturnBuilder, TargetHex = target.Value,
                BuilderArmyId = builderArmyId,
                BuildValue = completedDemand.EconomySiteValue > 0f
                    ? completedDemand.EconomySiteValue : completedDemand.Value,
                Loaned = lender != null, LoanSource = lender?.IntentKey ?? default,
            };
            recovery.IntentKey = MissionIntentKey.For(recovery);
            recovery.LastAttemptKey = StableMissionKey.ForEconomy(EconomyTaskKind.ReturnBuilder,
                recovery.IntentKey.ObjectiveId, target.Value);
            if (economy != null && !oldKey.Equals(recovery.IntentKey))
                state.Remove(oldKey);
            if (lender != null)
            {
                lender.Status = IntentStatus.Suspended;
                lender.Suspended = SuspendReason.EconomyLoan;
            }
            state.Put(recovery);
            AiDebugLog.Write($"[AI][V2][Economy][Recovery] actor=#{builderArmyId} -> "
                + $"({target.Value.Q},{target.Value.R})"
                + (lender != null ? $" lender={lender.IntentKey}" : ""));
        }

        // ATK §20/§53 — same single own-Base identity owner as SelectEconomyRecoveryTarget and
        // SelectReturnBase above.
        // Existing home ownership and current safe-route availability are different facts.
        // Keep an owned home through transient blockers; Provisioning retries its movement.
        // When ownership is lost, SelectEconomyHome requires a reachable replacement witness.

        private static bool IsProtectedEconomyHex(WorldSnapshot snap,
            PlayerSetupData player, HexCoord hex) =>
            snap?.Self?.BaseHexes != null && snap.Self.BaseHexes.Contains(hex);

        // The ONE EconomyLoan -> Active transition (repayment, supersede, orphan repair).
        private static void ResumeEconomyLender(MissionIntent lender)
        {
            if (lender != null && lender.Status == IntentStatus.Suspended
                && lender.Suspended == SuspendReason.EconomyLoan)
            {
                lender.Status = IntentStatus.Active;
                lender.Suspended = SuspendReason.None;
                AiDebugLog.Write($"[AI][V2][Economy][Loan] resume actor=#{lender.PreferredMoverArmyId} "
                    + $"to={lender.IntentKey}");
            }
        }
    }
}
