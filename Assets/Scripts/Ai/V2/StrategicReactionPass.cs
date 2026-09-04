using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using UnityEngine;

namespace Game.Ai.V2
{
    // Bounded same-turn replanning. Round 0 consumes the ordinary strategic invalidation. A single
    // round 1 is permitted only when round 0 itself materializes new operational capability or a
    // terminal draw exposes an actionable hand. New contact discovery never recursively chains.
    public sealed class StrategicReactionResult
    {
        public bool Ran;
        public bool StateChanged;
        public int DiscoveredTargets;
        public int Demands;
        public int Missions;
        public int Provisioned;
        public int Executed;          // real attempts (superseded stale missions excluded)
        public int CardsPlayed;
        public int CardsDrawn;
        public int Rounds;
    }

    // AI-MGR-02 §7 (round 4) — a BOUNDED REACTION BUDGET, honestly named. The bounded reaction
    // round re-runs the whole Demand→Mission→Provision→Execute pipeline and picks its OWN action,
    // so there is no single pre-planned action to price exactly. This struct therefore describes:
    //   · whether a same-turn reaction is worth reserving for at all (there is pending content AND
    //     at least one plausible way to act on it), and
    //   · a bounded AP budget for that replan (ReservedApBudget), capped at reactionReserveApCap.
    // The reservation Owner is a stable "reaction-budget" tag; it is revalidated before the round
    // runs (§P1.3) and released if no reaction is actionable any more.
    internal readonly struct StrategicReactionOpportunity
    {
        public readonly bool IsActionable;
        public readonly string OwnerKey;         // reservation owner tag ("reaction-budget:<kind>")
        public readonly string Kind;             // "RespondToDiscovery" | "HandFollowup"
        public readonly float ReservedApBudget;  // BOUNDED replan budget (<= reactionReserveApCap), not an exact cost
        public readonly string Rationale;        // human-readable "why this budget"
        public readonly string FailReason;

        public StrategicReactionOpportunity(bool actionable, string ownerKey, string kind,
            float reservedApBudget, string rationale, string failReason)
        {
            IsActionable = actionable;
            OwnerKey = ownerKey;
            Kind = kind;
            ReservedApBudget = reservedApBudget;
            Rationale = rationale;
            FailReason = failReason;
        }

        public static StrategicReactionOpportunity None(string failReason) =>
            new StrategicReactionOpportunity(false, null, null, 0f, null, failReason);
    }

    internal static class StrategicReactionPass
    {
        // AI-MGR-02 §7 — CAN the pass run at all in this scope / with a resolvable hand.
        internal static bool CanStrategicReactionPassRun(PlayerSetupData player, AiTurnContext ctx)
        {
            if (player == null || ctx == null || ctx.Map == null)
                return false;
            // ExecuteIfPending consumes-and-suppresses the whole pass in ReconOnly scope.
            return !AiStrategyV2Scope.IsReconOnly;
        }

        // AI-MGR-02 §7 (round 4) — is a same-turn bounded reaction replan worth reserving AP for,
        // and how much (bounded)? Actionable requires a pending invalidation, a resolvable hand AND
        // a PLAUSIBLE way to act: a discovered target with at least one eligible responder army, or
        // pending follow-up with at least one card that passes a lightweight affordability gate.
        // ReservedApBudget is a CEILING-bounded budget for the replan, never a claim about a
        // specific action's exact cost (the replan re-runs the whole pipeline and chooses freely).
        internal static StrategicReactionOpportunity BuildReactionOpportunity(PlayerSetupData player,
            PlayerRoot root, AiTurnContext ctx)
        {
            if (!CanStrategicReactionPassRun(player, ctx))
                return StrategicReactionOpportunity.None("cannotRun(scope)");
            if (!StrategicInterruptRegistry.HasPending(player, ctx.TurnNumber))
                return StrategicReactionOpportunity.None("noPendingInvalidation");

            AiHandData hand = AiHandRegistry.Peek(player);
            if (hand == null)
                StrategicInterruptRegistry.TryGetHand(player, ctx.TurnNumber, out hand);
            if (hand == null)
                return StrategicReactionOpportunity.None("noResolvableHand");

            bool targetDriven = StrategicInterruptRegistry.TargetIds(player, ctx.TurnNumber).Count > 0;
            bool followupDriven = StrategicInterruptRegistry.HasPendingFollowup(player, ctx.TurnNumber);
            if (!targetDriven && !followupDriven)
                return StrategicReactionOpportunity.None("noActionableContent");

            float apAvailable = root != null ? Mathf.Max(0f, root.ActionPoints) : 0f;
            int cap = AiConfigV2.reactionReserveApCap;

            if (targetDriven)
            {
                // Actionability gate: at least one own field army could respond. Budget estimate:
                // the cheapest such responder's activation + one move step — but this is only an
                // ESTIMATE feeding a bounded budget, so it is allowed to exceed the ceiling; we
                // then reserve the ceiling and say so (never silently shrink an "exact" number).
                int cheapestResponderCost = int.MaxValue;
                foreach (ArmyData a in ArmyRegistry.AllForOwner(player))
                {
                    if (a == null || a.Members.Count == 0 || a.CurrentMovement <= 0
                        || a.IsGarrison || a.IsPrison || a.IsAirfield || a.IsAirArmy
                        || AiArmyRoles.IsSoloRecce(a))
                        continue;
                    int c = a.HasActivatedThisTurn ? 0 : a.ActivationApCost;
                    if (!a.HasActivatedThisTurn && !root.CanSpendActionPoints(a.ActivationApCost))
                        continue;
                    cheapestResponderCost = Mathf.Min(cheapestResponderCost, c);
                }
                if (cheapestResponderCost == int.MaxValue)
                    return StrategicReactionOpportunity.None("noEligibleResponder");

                float estimate = cheapestResponderCost + AiConfigV2.reactionResponderMoveApEstimate;
                float budget = Mathf.Min(estimate, Mathf.Min(apAvailable, cap));
                if (budget <= 0f)
                    return StrategicReactionOpportunity.None("noApAvailable");
                string rationale = estimate > cap
                    ? $"estimate {estimate:0.#} AP exceeds ceiling {cap}; reserving the ceiling as a bounded replan budget"
                    : $"cheapest responder {cheapestResponderCost} + move {AiConfigV2.reactionResponderMoveApEstimate}";
                return new StrategicReactionOpportunity(true, "reaction-budget:RespondToDiscovery",
                    "RespondToDiscovery", budget, rationale, null);
            }

            // Hand-only follow-up. Actionability gate: at least one hand card clears a lightweight
            // affordability check (AP + persistent cost). This is NOT a claim that this exact card
            // will be played — the replan chooses — it only proves a hand replay is plausible.
            var affordable = hand.Hand
                .Where(c => c?.Definition != null
                    && c.EffectivePlayApCost <= apAvailable + 0.001f
                    && (c.EffectivePlayResourceCost == null || c.EffectivePlayResourceCost.CanAfford(root)))
                .OrderBy(c => c.EffectivePlayApCost)
                .ToList();
            if (affordable.Count == 0)
                return StrategicReactionOpportunity.None("noAffordableHandCard");

            float cardEstimate = Mathf.Max(affordable[0].EffectivePlayApCost, AiConfigV2.reactionFollowupApEstimate);
            float cardBudget = Mathf.Min(cardEstimate, Mathf.Min(apAvailable, cap));
            if (cardBudget <= 0f)
                return StrategicReactionOpportunity.None("noApAvailable");
            return new StrategicReactionOpportunity(true, "reaction-budget:HandFollowup", "HandFollowup",
                cardBudget, $"cheapest affordable hand card {affordable[0].EffectivePlayApCost:0.#} AP", null);
        }

        // §P1.3 — before the bounded reaction round runs, a same-turn reaction must still be
        // actionable at all. If not, its budget reservation is released so the AP re-enters
        // arbitration this turn (HousekeepingManager's tempo re-run).
        internal static bool ReactionStillActionable(PlayerSetupData player, PlayerRoot root, AiTurnContext ctx)
            => BuildReactionOpportunity(player, root, ctx).IsActionable;

        public static IEnumerator ExecuteIfPending(WorldSnapshot priorSnapshot, PlayerSetupData player,
            PlayerRoot root, AiTurnContext ctx, StrategicReactionResult result)
        {
            // ReconOnly isolates the current deep-rework from the legacy strategic reaction loop.
            // The live Recon executor will own ordinary step->refresh->reaction; until that lands,
            // do not let a contact discovery reopen Aggression/Defence/Economy/Development through
            // this second orchestration path. Consume the turn-scoped invalidation so it cannot
            // leak into the next turn.
            if (AiStrategyV2Scope.IsReconOnly)
            {
                if (player != null && ctx != null && StrategicInterruptRegistry.HasPending(player, ctx.TurnNumber))
                {
                    StrategicInterruptRegistry.Clear(player, ctx.TurnNumber);
                    AiDebugLog.Write("[AI][V2][Scope] strategic reaction pass suppressed reason=ReconOnly");
                }
                // AI-MGR-02 §4 — a scope-suppressed pass deliberately leaves any AP reservation in
                // place: HousekeepingManager releases it and re-runs end-of-turn tempo spending with
                // the freed AP the same turn (so it is not stranded).
                yield break;
            }

            yield return ExecuteRound(priorSnapshot, player, root, ctx,
                result ?? new StrategicReactionResult(), 0);

            // AI-MGR-02 §4 — the pass has had its bounded round(s); any AP Phase B reserved for it
            // is now free (its own inner Phase B call already spent whatever it wanted).
            if (player != null && ctx != null)
                StrategicResourceReservationLedger.ExpireStage(player, ctx.TurnNumber,
                    StrategicReservationExpiry.EndOfReaction);
        }

        private static IEnumerator ExecuteRound(WorldSnapshot priorSnapshot, PlayerSetupData player,
            PlayerRoot root, AiTurnContext ctx, StrategicReactionResult result, int round)
        {
            if (player == null || root == null || ctx == null || ctx.Map == null)
                yield break;
            if (!StrategicInterruptRegistry.HasPending(player, ctx.TurnNumber))
                yield break;

            // §P1.3 — before running the bounded round, a same-turn reaction must still be
            // actionable at all (a responder / affordable card still exists). If not, release the
            // budget reservation now so the freed AP re-enters arbitration THIS turn
            // (HousekeepingManager's tempo re-run) instead of being pinned to a dead budget.
            if (round == 0
                && StrategicResourceReservationLedger.HasAny(player, ctx.TurnNumber)
                && !ReactionStillActionable(player, root, ctx))
            {
                StrategicResourceReservationLedger.ReleaseByReason(player, ctx.TurnNumber,
                    StrategicReservationReason.StrategicReactionPass);
                AiDebugLog.Write("[AI][V2] reaction — no same-turn reaction is actionable any more; "
                    + "released the reaction-budget reservation before executing");
            }

            HashSet<int> targetIds = StrategicInterruptRegistry.TargetIds(player, ctx.TurnNumber);
            AiHandData hand = AiHandRegistry.Peek(player);
            if (hand == null)
                StrategicInterruptRegistry.TryGetHand(player, ctx.TurnNumber, out hand);

            StrategicInterruptRegistry.Clear(player, ctx.TurnNumber);
            result.Ran = true;
            result.Rounds++;
            result.DiscoveredTargets += targetIds.Count;

            // Correlation scope for this bounded round: round 0 -> T{turn}-P{c}-R1, round 1 -> …-R2.
            V2TraceScope rtrace = AiV2Trace.BeginReaction(player, ctx.TurnNumber, round);
            V2ResourceStamp rStart = AiV2Trace.Stamp(root);

            // A bounded reaction round is a fresh capability-exhaustion scope: Phase A below may
            // materialise new capability, so nothing the main pass marked exhausted carries in.
            CapabilityPoolExhaustionRegistry.BeginRound(player, ctx.TurnNumber, round + 1);

            if (hand == null)
            {
                AiDebugLog.Write("[AI][V2] reaction — pending invalidation consumed, but no AI hand is available; defer to next turn");
                // The round opened a scope; close it with a [STATE] line on this early exit too.
                AiV2Trace.LogState(rtrace.Id, rStart, AiV2Trace.Stamp(root));
                yield break;
            }

            int apAtStart = root.ActionPoints;
            AiDebugLog.Write($"[AI][V2] reaction — BEGIN round {round + 1}/2 bounded strategic replan "
                + $"targets=[{string.Join(",", targetIds.OrderBy(x => x))}] ap={apAtStart}");

            WorldSnapshot snapshot = WorldAnalysis.Scan(player, root, hand, ctx);
            AiRadarState radarState = AiRadarStateRegistry.GetOrCreate(player);
            RadarAssessment assessment = StrategyLayer.Evaluate(snapshot, radarState);
            Radar radar = assessment.Radar;
            List<ReconObjective> reconObjectives = ReconObjectiveEvaluator.Enumerate(snapshot);
            List<AggressionObjective> aggressionObjectives =
                AggressionObjectiveEvaluator.Enumerate(snapshot, assessment.Breakdown.OpportunityReport);

            AiDebugLog.Write($"[AI][V2] reaction — radar {radar.DebugLine()} "
                + $"aggObjectives={aggressionObjectives.Count} reconObjectives={reconObjectives.Count}");
            foreach (AggressionObjective ao in aggressionObjectives)
                AiDebugLog.Write($"[AI][V2]   reaction aggObjective — {ao.ObjectiveId} "
                    + $"@{ao.LastKnownHex.Q},{ao.LastKnownHex.R} base "
                    + $"{ao.BaseValue.ToString("0.0", CultureInfo.InvariantCulture)} "
                    + $"readyWin {ao.ReadyWinChance.ToString("0.00", CultureInfo.InvariantCulture)} "
                    + $"asmWin {ao.AssemblableWinChance.ToString("0.00", CultureInfo.InvariantCulture)} "
                    + $"gate {(ao.GatePassed ? 1 : 0)}");

            List<MissionIntent> activeIntents = MissionContinuityLayer.ResolveActive(player, snapshot);
            ActorCommitments actorCommitments = ActorCommitments.FromIntents(activeIntents, snapshot, reconObjectives);
            // AI-RECON-01 — the reaction round runs its OWN air reservation prepass. The main pass's
            // reservation was already consumed by its terminal air fallback (aircraft moved / AP
            // spent), so reusing its stale ReservedLaunchSorties would let DemandLayer suppress a
            // ground scout for capacity that no longer exists. Reset + re-evaluate against the
            // now-current AP / Energy / movement.
            ReconAirReservationPrepass.Run(snapshot, player, root, ctx, activeIntents, actorCommitments, reconObjectives);
            List<AxisDemand> demands = DemandLayer.Generate(snapshot, assessment.Breakdown,
                reconObjectives, aggressionObjectives, activeIntents, actorCommitments, player);
            result.Demands += demands.Count;

            ReconAirReservationState airReservation =
                ReconAirReservationRegistry.ForTurn(player, snapshot.TurnNumber);
            AxisBudgetLedger apLedger = AxisBudgetLedger.Create(
                UnityEngine.Mathf.Max(0f, (snapshot.Self?.ActionPoints ?? 0) - airReservation.ProtectedAp), radar);
            StrategicPhaseResult phaseA = StrategicManager.FulfillDemands(snapshot, player, root, hand,
                ctx, apLedger, demands, actorCommitments);
            result.CardsPlayed += phaseA.CardsPlayed;
            result.StateChanged |= phaseA.StateChanged;
            if (phaseA.StateChanged)
                snapshot = WorldAnalysis.RefreshOperationalState(snapshot, player, root, hand, ctx);

            List<MissionProposal> missions = MissionLayer.Propose(snapshot, assessment.Breakdown,
                activeIntents, reconObjectives);
            missions.AddRange(AggressionMissionLayer.Propose(snapshot, assessment.Breakdown,
                activeIntents, aggressionObjectives));
            // AI-RECON-01 — the reaction round runs the SAME DemandLayer -> MissionLayer -> Allocator
            // -> Provisioning path as the main pass, so it needs the same actor-before-budget
            // reservation, or every reaction-round Scout would defer ReconActorUnreserved.
            var reconActorCtx = new ReconActorReservationContext();
            ReconActorReservationPlanner.Plan(reconActorCtx, snapshot, ctx, player, missions, actorCommitments,
                activeIntents, reconObjectives);
            foreach (MissionProposal m in missions)
                if (m != null && string.IsNullOrEmpty(m.AttemptId))
                    m.AttemptId = rtrace?.NextMissionAttemptId() ?? "?";
            AiV2Trace.CorrelateDemandsToMissions(demands, missions);
            foreach (MissionProposal m in missions)
                AiDebugLog.Write($"[AI][V2]   reaction mission — [{m.AttemptId}] causeDemand={m.CauseDemandTrace} "
                    + $"{m.Kind} base {m.BaseValue.ToString("0.0", CultureInfo.InvariantCulture)} | {m.Explain}");
            result.Missions += missions.Count;

            List<Commitment> commitments = MissionContinuityLayer.BindFunding(activeIntents, missions);
            var outcomeLedger = new MissionOutcomeLedger();
            outcomeLedger.RegisterProposals(missions);
            outcomeLedger.RegisterCommitments(commitments);

            AllocationSession session = ResourceAllocator.BeginTurn(snapshot, radar, missions,
                commitments, player, apLedger, airReservation.ProtectedEnergy, airReservation.ProtectedAp);
            var provSession = new ProvisioningSession(snapshot);
            TentativeAllocation allocation = session.Pack();
            var provisioned = new List<ProvisionedMission>();

            int reallocPass = 0;
            while (true)
            {
                bool anyFailure = false;
                bool anySuccess = false;
                bool allFailuresArePoolWide = true;
                ProvisioningManager.PreparePass(player, root, ctx, provSession, allocation,
                    reconActorCtx.ReservedActorIds);
                foreach (FundedEntry fe in allocation.Funded)
                {
                    if (fe?.Mission == null) continue;
                    StableMissionKey key = StableMissionKey.For(fe.Mission);
                    if (provSession.AlreadyProvisioned(key)) continue;
                    // A capability pool proven pool-wide unable stays exhausted across the reaction
                    // round boundary; it is only re-tried if revalidation now finds an actor (spec §7).
                    if (!CapabilityPoolExhaustionRegistry.RevalidateAndClearIfRecovered(player,
                            CapabilityPoolExhaustionRegistry.PoolFor(fe.Mission), snapshot))
                        continue;

                    ProvisioningResult provision = ProvisioningManager.Provision(
                        player, root, hand, ctx, provSession, fe);
                    if (provision.Success)
                    {
                        anySuccess = true;
                        provSession.RegisterSuccess(key, provision.Provisioned);
                        session.RegisterProvisionSuccess(fe, provision.Provisioned.ClaimedAp);
                        outcomeLedger.RecordProvisionSuccess(fe.Mission, provision.Provisioned);
                        provisioned.Add(provision.Provisioned);
                        AiV2Trace.CheckProvisionEnvelope(fe.Mission.AttemptId,
                            provision.Provisioned.ClaimedAp, fe.Tentative.Ap);
                        AiDebugLog.Write($"[AI][V2]   reaction provision [{fe.Mission.AttemptId}] {key} — OK mover "
                            + $"#{provision.Provisioned.MoverArmyId} ap "
                            + $"{provision.Provisioned.ClaimedAp.ToString("0.#", CultureInfo.InvariantCulture)}");
                    }
                    else
                    {
                        anyFailure = true;
                        bool poolWide = CapabilityPoolExhaustionRegistry.ProvenPoolWideUnable(
                            snapshot, player, fe.Mission, provision.Failure);
                        if (poolWide)
                            CapabilityPoolExhaustionRegistry.MarkExhausted(player,
                                CapabilityPoolExhaustionRegistry.PoolFor(fe.Mission),
                                $"reaction {provision.Failure.Kind}: no eligible actor in snapshot");
                        allFailuresArePoolWide &= poolWide;
                        session.RegisterProvisionFailure(fe, provision.Failure);
                        outcomeLedger.RecordProvisionFailure(fe.Mission, provision.Failure);
                        if (fe.Mission.Kind == MissionKind.Scout
                            && provision.Failure.Kind != ProvisionFailureKind.EnvelopeTooSmall)
                            ReconActorReservationPlanner.RecordProvisionFailure(reconActorCtx, fe.Mission,
                                provision.Failure.Kind);
                        AiDebugLog.Write($"[AI][V2]   reaction provision [{fe.Mission.AttemptId}] {key} — FAIL "
                            + $"{provision.Failure.Kind} [{provision.Failure.Disposition}] "
                            + provision.Failure.Detail);
                    }
                }

                if (anyFailure && allFailuresArePoolWide)
                {
                    AiDebugLog.Write("[AI][V2] reaction — every funded mission's capability pool is exhausted this cycle; stop key-by-key reallocation");
                    break;
                }
                bool reconRematched = ReconActorReservationPlanner.Rematch(reconActorCtx, missions, provSession,
                    allocation, portfolioChanged: anyFailure || anySuccess);
                if (!reconRematched && (!session.HasNewFailures || session.Converged))
                    break;
                if (++reallocPass >= AiConfigV2.maxReallocIterations)
                    break;
                allocation = session.Pack();
            }

            result.Provisioned += provisioned.Count;
            var executed = new List<ExecutionResult>();
            // Same lifecycle as the main pipeline: outcomeLedger.RegisterProposals(missions) rowed
            // every Explore proposal (incl. deferred), so the stale-Explore replacement picker must
            // be told the whole focus set, not just what reached the queue. Shared helper keeps the
            // two passes from drifting.
            HashSet<HexCoord> exploreProposalFoci = MissionRevalidator.CollectExploreProposalFoci(missions);
            yield return TaskExecutor.Execute(player, root, ctx, provisioned, executed, snapshot, exploreProposalFoci,
                () => ReconAirReservationPrepass.ReleaseProtection(player));
            ReconAirReservationPrepass.ReleaseProtection(player);
            result.Executed += executed.Count(MissionRevalidator.WasAttempt);
            foreach (ExecutionResult er in executed)
            {
                if (er.IsReplacement && er.Source?.Mission != null)
                {
                    outcomeLedger.RegisterProposals(new[] { er.Source.Mission });
                    outcomeLedger.RecordProvisionSuccess(er.Source.Mission, er.Source);
                }
                outcomeLedger.RecordExecution(er);
            }
            outcomeLedger.RecordDeferrals(allocation.Deferred);
            outcomeLedger.RefreshObjectiveStatesLive(player);
            MissionContinuityLayer.ReconcileAfterTurn(player, snapshot.TurnNumber, outcomeLedger.Finalize());

            if (StrategicInterruptRegistry.HasPendingContactDiscovery(player, ctx.TurnNumber))
            {
                HashSet<int> deferred = StrategicInterruptRegistry.TargetIds(player, ctx.TurnNumber);
                AiDebugLog.Write($"[AI][V2] reaction — contact recursion suppressed; additional discovery "
                    + $"[{string.Join(",", deferred.OrderBy(x => x))}] deferred to next strategic scan");
                StrategicInterruptRegistry.ClearDiscovery(player, ctx.TurnNumber);
            }

            snapshot = WorldAnalysis.RefreshOperationalState(snapshot, player, root, hand, ctx);
            ActorCommitments postCommitments = ActorCommitments.FromIntents(
                MissionIntentRegistry.GetOrCreate(player).All, snapshot, ReconObjectiveEvaluator.Enumerate(snapshot));
            // AI-MGR-02 §7/§P0 — the reaction round is NOW executing its own spend. The AP that
            // Phase B reserved as a placeholder for "the reaction will need AP" must be released
            // BEFORE this inner tempo arbitration, or the reaction cannot use the very AP it held
            // back (and it would look stranded until end of turn).
            StrategicResourceReservationLedger.ReleaseByReason(player, ctx.TurnNumber,
                StrategicReservationReason.StrategicReactionPass);
            var phaseB = new StrategicPhaseResult();
            yield return StrategicManager.UseSurplus(snapshot, player, root, hand, ctx,
                postCommitments, phaseA.Reservation, phaseB);
            result.CardsPlayed += phaseB.CardsPlayed;
            result.CardsDrawn += phaseB.CardsDrawn;
            result.StateChanged |= phaseB.StateChanged || executed.Count > 0;

            // Reaction-phase activity bucket (additive across the up-to-2 bounded rounds). Every
            // execution counter is DERIVED from `executed` exactly once — never incremented inside
            // TaskExecutor (spec §11). The Main bucket is owned by Pipeline.RunTurn; Total = Main +
            // Reaction, no double count.
            V2PhaseActivity ract = V2TurnActivityTelemetry.Phase(player, ctx.TurnNumber, V2Phase.Reaction);
            ract.DemandsRaised += demands.Count;
            ract.MissionsConsidered += missions.Count;
            ract.MissionsFunded += allocation.Funded.Count;
            ract.Provisioned += provisioned.Count;
            ract.ExecutionAttempts += executed.Count(MissionRevalidator.WasAttempt);
            ract.ExecutionsSucceeded += executed.Count(MissionRevalidator.WasGenuineExecution);
            ract.ExecutionsStaleOrSkipped += executed.Count(MissionRevalidator.WasStaleOrSkipped);
            ract.ReplacementMissions += executed.Count(MissionRevalidator.WasReplacement);
            ract.CardsPlayed += phaseA.CardsPlayed + phaseB.CardsPlayed;
            ract.CardsDrawn += phaseB.CardsDrawn;
            ract.CapabilityDeliveries += phaseA.CapabilityDeliveries + phaseB.CapabilityDeliveries;
            ract.InfrastructureAttempts += phaseA.InfrastructureAttempts + phaseB.InfrastructureAttempts;
            ract.InfrastructureBuilt += phaseA.InfrastructureBuilt + phaseB.InfrastructureBuilt;
            ract.MaterializationAttempts += phaseA.MaterializationAttempts + phaseB.MaterializationAttempts;
            ract.MaterializationsSucceeded += phaseA.MaterializationsSucceeded + phaseB.MaterializationsSucceeded;
            ract.GeneratedCardAttempts += phaseA.GeneratedCardAttempts + phaseB.GeneratedCardAttempts;
            ract.GeneratedCardsSucceeded += phaseA.GeneratedCardsSucceeded + phaseB.GeneratedCardsSucceeded;
            ract.EquipmentAssignmentAttempts += phaseA.EquipmentAssignmentAttempts + phaseB.EquipmentAssignmentAttempts;
            ract.EquipmentAssignmentsSucceeded += phaseA.EquipmentAssignmentsSucceeded + phaseB.EquipmentAssignmentsSucceeded;

            AiDebugLog.Write($"[AI][V2] reaction — END round {round + 1}/2 ap {apAtStart}->{root.ActionPoints}, "
                + $"demands {demands.Count}, missions {missions.Count}, provisioned {provisioned.Count}, "
                + $"executed {executed.Count}, cardsPlayed {phaseA.CardsPlayed + phaseB.CardsPlayed}, "
                + $"draws {phaseB.CardsDrawn}");
            // End-of-round physical resource control totals (spec §2.7).
            AiV2Trace.LogState(rtrace.Id, rStart, AiV2Trace.Stamp(root));

            if (StrategicInterruptRegistry.HasPendingFollowup(player, ctx.TurnNumber))
            {
                if (round == 0)
                {
                    AiDebugLog.Write("[AI][V2] reaction — operational hand/capability changed inside round 1; run one bounded follow-up round");
                    yield return ExecuteRound(snapshot, player, root, ctx, result, 1);
                }
                else
                {
                    AiDebugLog.Write("[AI][V2] reaction — follow-up bound reached; remaining hand/capability invalidation deferred to next strategic scan");
                    StrategicInterruptRegistry.Clear(player, ctx.TurnNumber);
                }
            }
        }
    }
}