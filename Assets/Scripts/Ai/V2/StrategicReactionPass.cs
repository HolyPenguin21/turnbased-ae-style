using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Game.HexGrid;
using Game.Map;
using Game.Players;

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

    internal static class StrategicReactionPass
    {
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
                yield break;
            }

            yield return ExecuteRound(priorSnapshot, player, root, ctx,
                result ?? new StrategicReactionResult(), 0);
        }

        private static IEnumerator ExecuteRound(WorldSnapshot priorSnapshot, PlayerSetupData player,
            PlayerRoot root, AiTurnContext ctx, StrategicReactionResult result, int round)
        {
            if (player == null || root == null || ctx == null || ctx.Map == null)
                yield break;
            if (!StrategicInterruptRegistry.HasPending(player, ctx.TurnNumber))
                yield break;

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
            List<AxisDemand> demands = DemandLayer.Generate(snapshot, assessment.Breakdown,
                reconObjectives, aggressionObjectives, activeIntents, actorCommitments, player);
            result.Demands += demands.Count;

            AxisBudgetLedger apLedger = AxisBudgetLedger.Create(snapshot.Self?.ActionPoints ?? 0, radar);
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
                commitments, player, apLedger);
            var provSession = new ProvisioningSession(snapshot);
            TentativeAllocation allocation = session.Pack();
            var provisioned = new List<ProvisionedMission>();

            int reallocPass = 0;
            while (true)
            {
                bool anyFailure = false;
                bool allFailuresArePoolWide = true;
                ProvisioningManager.PreparePass(player, root, ctx, provSession, allocation);
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
                if (!session.HasNewFailures || session.Converged
                    || ++reallocPass >= AiConfigV2.maxReallocIterations)
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
            yield return TaskExecutor.Execute(player, root, ctx, provisioned, executed, snapshot, exploreProposalFoci);
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
            StrategicPhaseResult phaseB = StrategicManager.UseSurplus(snapshot, player, root, hand, ctx,
                postCommitments, phaseA.Reservation);
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