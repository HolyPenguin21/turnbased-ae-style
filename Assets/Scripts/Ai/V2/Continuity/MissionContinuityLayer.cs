using System;
using System.Collections.Generic;
using System.Linq;
using Game.Cards;
using Game.Economy;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Aviation;

namespace Game.Ai.V2
{
    // MissionContinuityLayer (Strategy V2 build-order step 7): the one durable intent store's
    // continuity pass (ResolveActive), funding binding and step/turn reconciliation. Each mission
    // line's rules live in its own static owner (ReconContinuity, RaidContinuity, AttackContinuity,
    // ActiveDefenceContinuity, EconomyContinuity, DevelopmentContinuity, GroundCombatContinuity),
    // composed here by mission kind (MissionContinuityLayer.DomainResults.cs).
    internal static partial class MissionContinuityLayer
    {
        // A transient suspension (the pool or a capability was unavailable on an earlier pass) is
        // re-tested on every ResolveActive pass: the planner only proposes Active intents, and
        // AdvanceIntent / ShouldReap bound how long the retry may go on. Siege and EconomyLoan
        // are owned by their own resume edges, never by this one.
        internal static void ResumeTransientSuspension(MissionIntent intent)
        {
            if (intent.Status == IntentStatus.Suspended
                && (intent.Suspended == SuspendReason.PoolExhausted
                    || intent.Suspended == SuspendReason.CapabilityUnavailable))
            {
                intent.Status = IntentStatus.Active;
                intent.Suspended = SuspendReason.None;
            }
        }

        // `aggressionObjectives` is the freshly sorted, neutral-only objective list,
        // handed in exactly the way `reconObjectives` already is. It is what lets a durable Raid
        // role be RE-ORIENTED onto the next neutral once its current target is confirmed gone,
        // mirroring the Scout re-focus pattern instead of retiring and re-creating the operation.
        public static List<MissionIntent> ResolveActive(PlayerSetupData player, WorldSnapshot snap,
            IReadOnlyList<ReconObjective> reconObjectives = null,
            IReadOnlyList<RaidObjective> aggressionObjectives = null,
            AiTurnContext ctx = null, bool refreshSafeRoutes = true)
        {
            using var __profile = new Game.Core.ProfileScope("AI/Continuity.ResolveActive");
            MissionIntentState state = MissionIntentRegistry.GetOrCreate(player);
            var pass = new ActiveResolution(state, reconObjectives, !refreshSafeRoutes || ctx?.Map == null ? null
                : (Func<HexCoord, HexCoord, int, int>)((from, to, maxMovement) =>
                    SafeStepPathing.FindSafePathCost(ctx.Map, player, from, to, maxMovement)), ctx);
            // A builder released by the last retirement still needs its recovery decision.
            if (state.Count == 0 && !state.Economy.HasEndedOutboundBuilders)
            {
                GroundCombatContinuity.FinalizeAirSupportResolution(player, snap, pass);
                return pass.Active;
            }
            foreach (var prepare in ResolutionPreparers) prepare(player, snap, pass);
            pass.ActorClaims = ActorCommitments.FromIntents(state.All, snap, reconObjectives).ClaimedArmyIdSet;
            foreach (MissionIntent intent in state.All.ToList())
            {
                if (pass.DonatedOperations.Contains(intent.IntentKey)) continue;
                ResolveDomainOperation(player, snap, intent, pass);
            }
            AttackContinuity.RetireRecruitedReturnFallbacks(player, snap, pass);
            foreach (MissionIntentKey key in pass.Dead) state.Remove(key);
            // In-place rekeys preserve the same durable intent and accumulated progress.
            foreach ((MissionIntentKey oldKey, MissionIntent intent) in pass.Rekeys)
            {
                state.Remove(oldKey);
                state.Put(intent);
            }
            pass.Active.Sort((x, y) =>
            {
                int c = y.Funding.CompareTo(x.Funding); if (c != 0) return c;
                c = x.CreatedTurn.CompareTo(y.CreatedTurn); if (c != 0) return c;
                return x.IntentKey.CompareTo(y.IntentKey);
            });
            foreach (var finalize in ResolutionFinalizers) finalize(player, snap, pass);
            return pass.Active;
        }

        public static List<Commitment> BindFunding(IReadOnlyList<MissionIntent> activeIntents,
            IReadOnlyList<MissionProposal> proposals, WorldSnapshot snapshot = null,
            IReadOnlyDictionary<MissionIntentKey, string> deferredThisPass = null)
        {
            var commitments = new List<Commitment>();
            if (activeIntents == null || proposals == null)
                return commitments;

            var byKey = new Dictionary<MissionIntentKey, MissionProposal>();
            foreach (MissionProposal p in proposals)
                if (p != null)
                    byKey[MissionIntentKey.For(p)] = p;

            foreach (MissionIntent intent in activeIntents)
            {
                if (intent.Funding == CommitmentTier.None)
                    continue;
                if (!byKey.TryGetValue(intent.IntentKey, out MissionProposal p))
                {
                    // Missions owns the reason an otherwise-live durable intent deliberately has
                    // no executable proposal in THIS settled pass. Continuity must never re-run
                    // lane-specific eligibility here: that would duplicate planner logic and let
                    // diagnostics drift from the decision that actually withheld the proposal.
                    string deferred = null;
                    if (deferredThisPass != null)
                        deferredThisPass.TryGetValue(intent.IntentKey, out deferred);
                    if (!string.IsNullOrWhiteSpace(deferred))
                    {
                        AiDebugLog.WriteDeduped(intent.IntentKey.ToString(),
                            $"[AI][V2] continuity - DEFER {intent.IntentKey} ({intent.Funding}) "
                            + $"no step this pass; reason={deferred}");
                        continue;
                    }
                    AiDebugLog.WriteDeduped(intent.IntentKey.ToString(),
                        $"[AI][V2] continuity - WARN {intent.IntentKey} ({intent.Funding}) "
                        + "not materialised this turn; no planner deferral or funding bound");
                    continue;
                }
                commitments.Add(new Commitment
                {
                    IntentKey = intent.IntentKey,
                    Mission = p,
                    Tier = intent.Funding,
                    ContinuationValue = p.BaseValue,
                    SwitchingCost = 0f,
                });
            }
            return commitments;
        }

        // Mid-turn variant: apply exactly one settled outcome without aging, stalling or
        // reaping unrelated intents. ReconcileAfterTurn remains the sole end-of-turn sweep owner.
        public static void ReconcileStep(PlayerSetupData player, int turn,
            MissionStepResult outcome)
        {
            if (player == null || outcome == null)
                return;
            ReconcileOutcome(MissionIntentRegistry.GetOrCreate(player),
                AiAllocatorStateRegistry.GetOrCreate(player), outcome, turn);
        }

        // PhaseAEconomyHolds.ProtectActiveEconomyBuild reserves this intent's physical resources
        // and claims its card every cycle it is still committed — a real per-turn touch of the
        // intent's lifecycle, just one that has nothing to execute yet (builder already at/near
        // target, simply waiting for H/E/M/T to accumulate). Without this call that touch is
        // invisible to Continuity: the intent produces no MissionStepResult this turn, and
        // ReconcileAfterTurn's "unseen" branch grows StallTurns for it via the SAME raw idle
        // counter used for an abandoned project — reaping a still-legal, still-funded build that
        // is doing exactly the right thing (holding, not thrashing) after 2 quiet turns.
        //
        // Deliberately stamps LastProtectedTurn, NOT LastReconciledTurn: the unseen branch's own
        // `LastReconciledTurn == turn` guard skips its ENTIRE per-turn block, ShouldReap included,
        // so reusing that field here would also switch off the intent's absolute-age reap cap
        // (commitmentMaxTurns) for as long as it stays protected — trading an over-eager reap for
        // no reap at all. LastProtectedTurn only ever suppresses that one turn's StallTurns++;
        // TurnsActive and ShouldReap still run every turn, so a build that never becomes
        // affordable is still bounded by its ordinary age cap, just not punished for waiting.
        public static void MarkProtectedThisTurn(PlayerSetupData player, MissionIntentKey key, int turn)
        {
            if (player == null)
                return;
            MissionIntentState state = MissionIntentRegistry.GetOrCreate(player);
            if (state.TryGet(key, out MissionIntent intent))
                intent.LastProtectedTurn = turn;
        }

        public static void ReconcileAfterTurn(PlayerSetupData player, int turn,
            IReadOnlyList<MissionStepResult> outcomes)
        {
            MissionIntentState state = MissionIntentRegistry.GetOrCreate(player);
            AiAllocatorState allocState = AiAllocatorStateRegistry.GetOrCreate(player);
            var seen = new HashSet<MissionIntentKey>();

            foreach (MissionStepResult result in outcomes ?? Array.Empty<MissionStepResult>())
            {
                MissionStepResult o = result;
                seen.Add(o.IntentKey);
                ReconcileOutcome(state, allocState, o, turn);
            }

            foreach (MissionIntent intent in state.All.ToList())
            {
                if (seen.Contains(intent.IntentKey))
                    continue;
                if (intent.Status == IntentStatus.Suspended
                    && (intent.Suspended == SuspendReason.Siege
                        || intent.Suspended == SuspendReason.CapabilityUnavailable
                        || intent.Suspended == SuspendReason.EconomyLoan)
                    && intent.Kind != MissionKind.Development)
                    continue;

                if (intent.LastReconciledTurn == turn)
                    continue;

                intent.LastReconciledTurn = turn;
                intent.TurnsActive++;
                // A committed Economy build Phase A is still protecting this very turn (reserving
                // its resources, holding its card) has real, legitimate activity even though it
                // produced no MissionStepResult — it simply has nothing executable yet while H/E/
                // M/T accumulate. That is not the same "nothing happened" as an abandoned/invalid
                // intent, so it must not spend the same StallTurns budget. See MarkProtectedThisTurn.
                bool protectedWaitingOnResources = intent.LastProtectedTurn == turn;
                if (intent.Suspended != SuspendReason.PoolExhausted && !protectedWaitingOnResources)
                    intent.StallTurns++;

                if (ShouldReap(intent, turn))
                {
                    RetireOutcomeIntent(state, intent, null, turn);
                    StartPersistentCooldown(allocState, intent.LastAttemptKey, intent.Kind, turn, "IntentReapedIdle");
                    AiDebugLog.Write($"[AI][V2] continuity - {intent.IntentKey} reaped (idle: "
                        + $"{ReapProgress(intent)})");
                }
            }
        }

        private static void ReconcileOutcome(MissionIntentState state,
            AiAllocatorState allocState, MissionStepResult o, int turn)
        {
            state.TryGet(o.IntentKey, out MissionIntent intent);
            string aid = AiV2Trace.FormatCorrelation(o.Proposal);
            AiDebugLog.Write($"[AI][V2] [{aid}] outcome {o.Disposition}"
                + (o.ObjectiveSatisfied ? " satisfied" : "")
                + (o.Disposition == MissionStepDisposition.PermanentFailure ? " structural" : "")
                + $" {o.IntentKey}");
            RecordDomainStepProgress(state, o, turn);
            if (TryDomainTransition(SideLegTransitions, state, allocState, intent, o, turn)) return;

            if (o.Disposition == MissionStepDisposition.Completed && o.ObjectiveSatisfied)
            {
                if (TryDomainTransition(CompletionTransitions, state, allocState, intent, o, turn)) return;
                if (intent != null)
                    AiDebugLog.Write($"[AI][V2] continuity - [{aid}] {o.IntentKey} COMPLETED, retired");
                RetireOutcomeIntent(state, intent, o, turn);
                return;
            }
            if (o.Disposition == MissionStepDisposition.PermanentFailure)
            {
                if (TryDomainTransition(RecoveryTransitions, state, allocState, intent, o, turn)) return;
                RetireOutcomeIntent(state, intent, o, turn);
                string reason = o.ProvisionFailureKindValue?.ToString() ?? "StructuralFailure";
                StartPersistentCooldown(allocState, o.AttemptKey, o.MissionKind, turn, reason);
                AiDebugLog.Write($"[AI][V2] continuity - [{aid}] {o.IntentKey} structural failure ({reason}), retired + cooldown");
                return;
            }
            if (o.Disposition == MissionStepDisposition.Invalidated)
            {
                if (TryDomainTransition(RecoveryTransitions, state, allocState, intent, o, turn)) return;
                if (intent != null)
                    AiDebugLog.Write($"[AI][V2] continuity - [{aid}] {o.IntentKey} failed ({Describe(o)}), retired");
                RetireOutcomeIntent(state, intent, o, turn);
                return;
            }
            if (TryDomainTransition(NoProgressTransitions, state, allocState, intent, o, turn)) return;
            if (intent != null) AdvanceIntent(intent, o, turn, state, allocState);
            else TryDomainTransition(CreationTransitions, state, allocState, null, o, turn);
        }

        internal static void AdvanceIntent(MissionIntent intent, MissionStepResult o, int turn,
            MissionIntentState state, AiAllocatorState allocState)
        {
            bool firstReconcileThisTurn = intent.LastReconciledTurn != turn;
            if (firstReconcileThisTurn)
            {
                intent.LastReconciledTurn = turn;
                intent.TurnsActive++;
            }
            intent.LastAttemptKey = o.AttemptKey;
            if (o.Proposal != null && o.Proposal.BaseValue > 0f)
                intent.LastIntrinsicValue = o.Proposal.BaseValue;
            intent.CumulativeApSpent += o.ApSpent;
            intent.StepsMovedTotal += o.StepsMoved;
            if (o.MoverArmyId.HasValue)
            {
                foreach (var observe in BeforeMoverObservers) observe(state, allocState, intent, o, turn);
                if (!TryDomainTransition(MoverTransitions, state, allocState, intent, o, turn))
                    intent.PreferredMoverArmyId = o.MoverArmyId;
            }

            foreach (var observe in IntentFactObservers) observe(state, allocState, intent, o, turn);

            bool poolExhausted = o.AllocationDeferReason == DeferReason.CommitmentPoolExhausted;
            bool capabilityUnavailable =
                o.ProvisionFailureKindValue == ProvisionFailureKind.NoMoverExists
                || o.ProvisionFailureKindValue == ProvisionFailureKind.MoverContended;

            if (o.MadeProgress)
            {
                intent.LastProgressTurn = turn;
                intent.StallTurns = 0;
            }
            else if (firstReconcileThisTurn && !poolExhausted
                && (!capabilityUnavailable || DomainAgesCapabilityFailure(intent, forReaping: false)))
            {
                intent.StallTurns++;
            }

            if (poolExhausted)
            {
                intent.Status = IntentStatus.Suspended;
                intent.Suspended = SuspendReason.PoolExhausted;
            }
            else if (capabilityUnavailable)
            {
                intent.Status = IntentStatus.Suspended;
                intent.Suspended = SuspendReason.CapabilityUnavailable;

                if (TryDomainTransition(CapabilityFailureTransitions, state, allocState, intent, o, turn)) return;
            }

            // A Raid keeps its absolute age cap (raidIntentMaxTurns) through capability
            // suspensions: ResolveActive resumes it every pass, so without the cap a Raid waiting
            // on a support no stage can bind was held — primary claimed — forever.
            if ((!capabilityUnavailable || DomainAgesCapabilityFailure(intent, forReaping: true))
                && ShouldReap(intent, turn))
            {
                RetireOutcomeIntent(state, intent, o, turn);
                StartPersistentCooldown(allocState, intent.LastAttemptKey, intent.Kind, turn, "IntentReapedStall");
                AiDebugLog.Write($"[AI][V2] continuity - [{AiV2Trace.FormatCorrelation(o.Proposal)}] {intent.IntentKey} reaped ("
                    + $"{ReapProgress(intent)})");
            }
            else
            {
                AiDebugLog.Write($"[AI][V2] continuity - [{AiV2Trace.FormatCorrelation(o.Proposal)}] {intent.IntentKey} advanced "
                    + $"({o.Disposition}, progress {(o.MadeProgress ? 1 : 0)}, t{intent.TurnsActive} stall{intent.StallTurns}"
                    + (capabilityUnavailable ? $", suspended CapabilityUnavailable:{o.ProvisionFailureKindValue}" : "") + ")");
            }
        }

        // Shared skeleton for the three Create*Intent methods below — was three independent,
        // hand-written copies of the same 12-field MissionIntent construction (see
        // Docs/ai-duplicate-methods-analysis.md, group M). Collapsing them here means a future
        // field added to MissionIntent only has to be wired up once, instead of risking a silently
        // half-initialized intent from a copy nobody remembered to update.
        internal static MissionIntent NewIntent(MissionStepResult o, int turn, MissionKind kind,
            CommitmentTier funding, IMissionObjective objective)
        {
            return new MissionIntent
            {
                IntentKey = o.IntentKey,
                LastAttemptKey = o.AttemptKey,
                Kind = kind,
                Funding = funding,
                Status = IntentStatus.Active,
                Suspended = SuspendReason.None,
                Objective = objective,
                CreatedTurn = turn,
                TurnsActive = 1,
                LastReconciledTurn = turn,
                LastProgressTurn = turn,
                StallTurns = 0,
                CumulativeApSpent = o.ApSpent,
                StepsMovedTotal = o.StepsMoved,
                // For a ground-combat payload this setter IS the payload's PrimaryArmyId: keep a
                // primary the payload already names (an Attack Gather is born from a SUPPORT's
                // step, so the mover is not the host), and fall back to the mover only when the
                // payload has none (Raid).
                PreferredMoverArmyId = (objective as IGroundCombatOperation)?.PrimaryArmyId
                    ?? o.MoverArmyId,
            };
        }



        // Retirement of whatever intent an outcome names: an Economy intent through its own owner
        // above, any other kind is simply removed (a loan can only point at an Economy borrower).
        private static void RetireOutcomeIntent(MissionIntentState state, MissionIntent intent,
            MissionStepResult outcome, int turn)
        {
            foreach (var prepare in RetirementPreparers) prepare(state, null, intent, outcome, turn);
            state.Remove(intent?.IntentKey ?? outcome.IntentKey, turn);
        }

        // The limits ShouldReap applies to this intent, for the reap log line. A Raid is judged by
        // the raid* caps and Scout/Attack have no absolute age cap, so printing the shared
        // commitment* limits for every kind (e.g. "age 10/6" for a Raid) misreported the cause.
        private static string ReapProgress(MissionIntent i)
        {
            if (i.Kind == MissionKind.Raid)
                return $"stall {i.StallTurns}/{AiConfigV2.raidIntentStallTurns}, "
                    + $"age {i.TurnsActive}/{AiConfigV2.raidIntentMaxTurns}";
            if (i.Kind == MissionKind.Scout || i.Kind == MissionKind.Attack)
                return $"stall {i.StallTurns}/{AiConfigV2.commitmentStallTurns}, age {i.TurnsActive} (no age cap)";
            return $"stall {i.StallTurns}/{AiConfigV2.commitmentStallTurns}, "
                + $"age {i.TurnsActive}/{AiConfigV2.commitmentMaxTurns}";
        }

        internal static bool ShouldReap(MissionIntent i, int turn)
        {
            if (i.Kind == MissionKind.Raid)
                return i.StallTurns >= AiConfigV2.raidIntentStallTurns
                    || i.TurnsActive >= AiConfigV2.raidIntentMaxTurns;
            // Explore/Refresh are durable roles whose waypoint is re-focused by ResolveActive.
            // Productive movement resets StallTurns; absolute age must not turn that success into
            // IntentReapedStall. Objective exhaustion/invalidity is handled separately above.
            // Attack owns its full lifecycle (target validity, live primary, Gather/Reinforcement/
            // RecoveryReturn) and a gather plus a long march legitimately outlives the generic age
            // cap; only a real stall ends it here.
            // A withdrawal ordered by a hostile army is never dropped mid-route after two quiet
            // turns (the army would be freed in the middle of the field): it waits for movement /
            // AP like any marching leg, bounded only by a generous multiple of the ordinary stall.
            if (i.Kind == MissionKind.Attack && i.Attack?.TacticalRetreat == true
                && i.Attack.Phase == AttackMissionPhase.RecoveryReturn)
                return i.StallTurns >= AiConfigV2.commitmentStallTurns * AiConfigV2.tacticalRetreatStallMultiplier;
            if (i.Kind == MissionKind.Scout || i.Kind == MissionKind.Attack)
                return i.StallTurns >= AiConfigV2.commitmentStallTurns;
            // Economy audit B10 — an Economy obligation that advances every turn (a long walk to
            // a site, a collector holding its site, a builder walking home) is not aged out by
            // absolute age; the cap bounds time WITHOUT progress instead, which still ends a build
            // that waits (protected, unaffordable) longer than commitmentMaxTurns.
            if (i.Kind == MissionKind.Economy)
                return i.StallTurns >= AiConfigV2.commitmentStallTurns
                    || turn - i.LastProgressTurn >= AiConfigV2.commitmentMaxTurns;
            return i.StallTurns >= AiConfigV2.commitmentStallTurns
                || i.TurnsActive >= AiConfigV2.commitmentMaxTurns;
        }

        internal static void StartPersistentCooldown(AiAllocatorState state, StableMissionKey key,
            MissionKind kind, int turn, string reason)
        {
            int duration = kind == MissionKind.Raid
                ? AiConfigV2.raidRejectCooldownTurns
                : AiConfigV2.allocatorRejectCooldownTurns;
            int until = turn + duration;
            state.StartCooldown(key, turn, until, reason);
            AiDebugLog.Write($"[AI][V2] cooldown - {key} reason={reason} start=t{turn} until=t{until} duration={duration}");
        }

        internal static string Describe(MissionStepResult o) =>
            o.Proposal != null && o.Proposal.Target is ScoutMissionTarget t
                ? ReconScoutKinds.Name(t.Kind)
                : o.Proposal != null && o.Proposal.Target is EconomyMissionTarget e
                    ? $"{e.Kind}@{e.TargetHex.Q},{e.TargetHex.R}"
                    : "?";
    }
}


