using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Game.Map;
using Game.Players;

using Game.Cards;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  AI STRATEGY V2 — TURN PIPELINE
    // ===========================================================================================
    //  The single production path of the AI turn. The normative ownership map (folders, dependency
    //  direction, canonical seams, mid-turn loop contract) is Assets/Scripts/Ai/V2/ARCHITECTURE.md;
    //  this file only ORDERS the stages and never scores, prices or decides eligibility itself.
    //
    //  INVARIANTS THE ORDERING PROTECTS
    //  --------------------------------------------------------------------------------------------
    //  · One shared WorldSnapshot per cycle (WorldAnalysis). Downstream stages read only it.
    //  · Radar: raw per-axis desires (response curves) normalised ONCE to sum == 1. It scales
    //    objective VALUE (EffectiveValue), never slices AP. Axes: Recon, Economy, Aggression,
    //    Development; ActiveDefence and Attack live inside Aggression, there is no Management axis.
    //  · Card play is a service: Phase A (StrategicPhaseA) fulfils AxisDemand capability gaps from
    //    the live AP pool (PhaseAApBudget); Phase B (UseSurplus) is the bounded end-of-turn tempo
    //    arbiter over genuinely remaining AP/resources; Housekeeping is the zero-AP reorg pass.
    //  · Mission<->axis is many-to-many: a MissionProposal carries an AxisContribution vector.
    //  · Re-allocate on provisioning failure is a HARD-BOUNDED loop (iteration cap, per-mission
    //    rejected set, cooldown).
    //  · One estimator, two stages: mission requirements and provisioning feasibility call the
    //    same estimator (WorthIt / AiPower), so the allocator never approves what provisioning
    //    cannot deliver.
    //  · Commitment is first-class: durable MissionIntents and funded commitments survive radar
    //    noise; retarget hysteresis applies (Continuity).
    //  · Provisioning is ATOMIC: one mission at a time in priority order, all-or-nothing claim,
    //    no partial-commit state between its entry and exit.
    // ===========================================================================================

    // --- Stage 3a/3b types (DesireAxis / DesireAxes / DesireVector / Radar / AxisContribution)
    //     live in DesireModels.cs; Stage 4's target types (MissionKind / EconomyTaskKind /
    //     EconomyMissionTarget / DevelopmentMissionTarget / ScoutTargetKind / StealthRequirement /
    //     ScoutMissionTarget) in MissionTargetModels.cs; Stage 4's output + Stage 7's Commitment
    //     (MissionProposal / MissionRequirements / Commitment) in MissionProposal.cs. All three
    //     are still this same stage map, split out for navigability only (round-2 file split).

    // --- Stage 5 types (BudgetSlice / FundingStage / FundedEntry / DeferReason / DeferredEntry /
    //     TentativeAllocation / StableMissionKey / ResourceVector / ProvisionFailureKind /
    //     AiAllocatorState / AllocationSession) live in ResourceAllocator.cs — the whole stage
    //     grew out of a stub into its own file (build-order step 5).

    // --- Stage 6 output lives in Provisioning/: ProvisionedMission.cs, ProvisioningResult.cs
    //     (ProvisionFailure + ProvisioningResult) and ProvisioningSession.cs, with the lane
    //     provisioners beside them. ProvisionFailureKind / ProvisionDisposition live in
    //     ResourceAllocator.cs beside the AllocationSession that consumes them. ExecutionResult /
    //     ExecutionStopReason live in TaskExecutor.cs.

    // ===========================================================================================
    //  THE PIPELINE — one AI turn (RunTurn), in this order:
    //   1. Start: hand top-up, one WorldAnalysis scan + estimate warm-up, Radar, the turn's Recon
    //      and Aggression objectives, Continuity (ResolveActive) and actor claims, the first demand
    //      frame, Phase A (deferred while an aviation obligation is pending), wing formation.
    //   2. The ONE main loop (TurnLoop.Run): typed operational admission passes (mandatory aviation
    //      before funded missions, one settled work step per iteration), the Phase B tempo rounds
    //      (the first after the settle window), the zero-Radar residual once. Strategic
    //      re-admission (StrategicReadmission) follows each settled step through
    //      StepTriggerSequence: a resumed rebase takes one take->reenter pair, other work two.
    //      The works are components (AdmissionIteration, TempoRound, ColdResidual, with
    //      StrategicReadmissionRunner) over one DecisionFrame; RunTurn only assembles them.
    //   3. End: air-support safety recall, final Continuity reconciliation, summary, Housekeeping
    //      (which runs a pending Reaction pass), turn-end audit and reservation release, telemetry.
    //  Remaining exceptions, by design: every work kind keeps its own observation/settle order
    //  (only trigger resolution is shared); the cold residual calls Phase A directly, without the
    //  re-admission key gate; the provisioning retry belongs to Provisioning (ProvisionNext) and runs
    //  inside the mission step; the recall,
    //  Housekeeping and Reaction stay outside the loop.
    // ===========================================================================================
    public static partial class Pipeline
    {
        internal static bool RefreshDevelopmentOpportunities(ISet<DesireAxis> dirtyAxes) =>
            dirtyAxes != null && dirtyAxes.Contains(DesireAxis.Development);

        public static IEnumerator RunTurn(PlayerSetupData player, PlayerRoot root, AiHandData hand, AiTurnContext ctx)
        {
            AiDebugLog.Write($"[AI][V2] === {player?.Nickname} — Strategy V2 pipeline owns this turn "
                + $"(turn {ctx?.TurnNumber}) ===");

            if (player == null || root == null || ctx == null || ctx.Map == null)
            {
                AiDebugLog.Write("[AI][V2] missing player/root/ctx/map — nothing to do.");
                yield break;
            }

            // Correlation scope for this whole main pass (T{turn}-P{colorIndex}-M) + the physical
            // resource totals it opens with, for the end-of-Main [STATE] control line (spec §2.7).
            V2TraceScope trace = AiV2Trace.BeginMain(player, ctx.TurnNumber);
            V2ResourceStamp stateStart = AiV2Trace.Stamp(root);

            // Coroutine disposal/exception also closes all session-owned turn state.
            using var turnSession = AiTurnSession.Begin(player, root, hand, ctx);

            // Initiative AP telemetry — captured now (turn start) and written back at turn end.
            // Belongs EXCLUSIVELY to Game.Ai.V2.Initiative analysis; nothing else in this pipeline
            // reads it (see InitiativeAnalyticsHistory).
            int initiativeStartAp = root.ActionPoints;
            int initiativeBaseAp = root.LastApFromInitiative;
            int initiativeActionableAtStart =
                Game.Ai.V2.Initiative.PreTurnCapacityAnalysis.CountActionableFieldArmies(player, unactivatedOnly: false);

            // 1b. A thin hand is refilled BEFORE the scan, so the drawn cards are in this turn's
            //     demand fulfilment instead of waiting for leftover AP in Phase B.
            int replenishDrawn = HandReplenishPolicy.Run(player, root, hand, ctx);

            // 2. One shared scan.
            WorldSnapshot scanned = WorldAnalysis.Scan(player, root, hand, ctx);
            // The first Analyze (desires, Aggression facts) is ~50 ms of Monte Carlo: fill the exact
            // estimate cache across frames first, so Analyze below only reads it.
            yield return CombatOpportunityAnalyzer.WarmEstimates(scanned);
            // P_start: the first scanned force ceiling is this player's baseline for the game.
            ForceBaselineRegistry.RecordStart(player, scanned.Self.TotalMilitaryPotential);
            AiFrameLog.GameState(scanned, hand);
            AiFrameLog.WorldAnalysis(scanned);
            ApBudgetTelemetry.Begin(player, ctx.TurnNumber, initiativeStartAp, scanned);
            // The first Attack preparation step's AP hold reads this turn's mobilization gate.
            StrategicManager.ObserveInitialForce(scanned, player, ctx);

            // 3. Strategy: independent raw desires -> normalize once -> radar. StrategyLayer writes
            //    its own detailed "[AI][V2]   desires — ..." trace; the line below is the summary.
            AiRadarState radarState = AiRadarStateRegistry.GetOrCreate(player);
            RadarAssessment assessment = StrategyLayer.Evaluate(scanned, radarState);
            DesireVector desires = assessment.Desires;
            Radar radar = assessment.Radar;
            AiDebugLog.Write($"[AI][V2] {player.Nickname}: radar — {radar.DebugLine()} "
                + $"| threat {desires.MilitaryThreat.ToString("0.00", CultureInfo.InvariantCulture)} "
                + $"runway {desires.EconomicRunway.ToString("0.00", CultureInfo.InvariantCulture)}");
            AiFrameLog.Strategy(assessment);

            // The decision frame holds the settled snapshot and what is derived from it from here on.
            DecisionFrame frame = DecisionFrame.Begin(scanned, turnSession, player, root, hand, ctx,
                assessment.Breakdown);

            // 3c. The ONE Recon-opportunity enumeration for the turn — shared by DemandLayer and
            //     ReconMissionPlanner. FROZEN here (before StrategicManager touches own forces): Strategic
            //     Manager changes which SCOUT can execute, never which objectives exist.
            frame.EnumerateObjectives();

            //     (3d. The ONE Aggression-opportunity enumeration for the turn is made by the same call —
            //     shared by DemandLayer and AggressionMissionLayer.)
            // 3e. Development opportunities are NOT enumerated here: DemandLayer.Development calls
            //     DevelopmentOpportunityEvaluator.Enumerate against the settled state of each pass.

            foreach (RaidObjective ao in frame.Aggression)
                AiDebugLog.Write($"[AI][V2]   aggObjective — {ao.ObjectiveId} @{ao.LastKnownHex.Q},{ao.LastKnownHex.R} "
                    + $"base {ao.BaseValue.ToString("0.0", CultureInfo.InvariantCulture)} "
                    + $"readyWin {ao.ReadyWinChance.ToString("0.00", CultureInfo.InvariantCulture)} "
                    + $"asmWin {ao.AssemblableWinChance.ToString("0.00", CultureInfo.InvariantCulture)} "
                    + $"def {ao.DefenderCount} gate {(ao.GatePassed ? 1 : 0)}"
                    + $"{(ao.NeedsCombatPower ? " needsPower" : "")}{(ao.NeedsHero ? " needsHero" : "")}");
            AiFrameLog.Objectives(frame.Recon, frame.Aggression);

            // 7a. Mission Continuity — resolve durable in-flight intents FIRST. This cleanly
            //     retires stale Raid intents
            //     before ActorCommitments or the allocator can protect them.
            // Also builds the normalized "which of my armies are already committed to an operation"
            // view, so DemandLayer / CapabilityInventory / ReusableArmySelector can tell an EXISTING
            // scout from an AVAILABLE one without knowing how continuity stores mover ownership.
            frame.ResolveInitialOwnership();

            AiFrameLog.MissionContinuity(frame.Intents, frame.Commitments);
            AiFrameLog.Forces(frame.Snapshot, frame.Commitments);

            // DemandLayer measures air capacity itself via ReconAssignmentPlanner.MeasureAirCapacity
            //     (the same canonical capacity owner ground uses), recomputed fresh every call — no
            //     cross-call registry.

            // S1. Demand Layer — capability SHORTAGES (no card selection). All real axes are live.
            var demandAxes = new HashSet<DesireAxis>(DesireAxes.All);
            frame.RebuildDemands(demandAxes);

            // S2. Phase A's AP budget reads the player's live AP; its only own state is the
            //     follow-up AP promised to capabilities it delivers. Radar scales objective value
            //     only. Every other owner's hold is TurnResourceBook's, applied at each chain guard.
            PhaseAApBudget apBudget = PhaseAApBudget.Create(root);
            AiDebugLog.Write($"[AI][V2] {player.Nickname}: Phase A budget — {apBudget.DebugLine()}");

            // S3. Strategic Manager Phase A — demand-driven card play, before mission planning.
            //     The demand set can materialize only capability requested by a real axis.
            //     Aviation obligations come first (AviationObligations): while a wing must still
            //     return or rebase, Phase A is deferred and the loop below settles the wings; every
            //     axis is then admitted in one re-admission pass.
            int handAtStart = (hand?.Hand?.Count ?? 0) - replenishDrawn;
            var readmission = new StrategicReadmission();
            StrategicPhaseResult phaseA;
            if (AviationObligations.Pending(player, ctx))
            {
                phaseA = new StrategicPhaseResult();
                readmission.Deferred.Defer(demandAxes);
                AiDebugLog.Write("[AI][V2] Phase A deferred — aviation obligations settle first");
            }
            else
            {
                phaseA = StrategicManager.FulfillDemands(frame.Snapshot, player, root, hand,
                    ctx, apBudget, frame.Demands, frame.Commitments, frame.Intents, frame.Recon,
                    radar: radar, deferFreshZeroRadar: true);
                ReservationInvariants.CheckBoundary(player, root, ctx, "phaseA");
            }
            phaseA.CardsDrawn += replenishDrawn;

            // S4. Analysis owns refresh granularity. The existing AiMapMemory revision decides
            //     whether honest knowledge/map facts changed; action kind is not used as a proxy.
            //     Radar remains fixed while operational Aggression facts refresh below.
            if (phaseA.StateChanged)
            {
                // Direct Economy construction can atomically turn the builder's existing intent
                // into ReturnBuilder (or resume a safe scout); re-reading the same continuity owner
                // here keeps stale pre-build actor claims from executing.
                yield return frame.AcceptChangedPhaseA();
                // Phase A changed the settled facts behind the initial demand frame. Refresh that
                // frame once here; the first operational admission consumes it without another
                // full Generate call.
                // This call regenerates every axis, so DemandLayer.Development
                // builds its own opportunities against this pass's complete need context.
                frame.RebuildDemands(demandAxes);
            }
            // Phase A is fully reflected in the settled snapshot/continuity view before pause.
            yield return ctx.WaitAtObserverActionBoundary();

            // Combat support preparation for existing ground operations. AirSweep formation
            // belongs to execution of its own admitted and funded Scout task.
            if (!AviationObligations.Pending(player, ctx))
            {
                AviationRebasePlan formation = AviationRebasePlanner.BuildFormationPlan(
                    frame.Snapshot, player, root, ctx, frame.Recon, frame.Intents, frame.Commitments);
                if (formation != null)
                {
                    bool formedWing = false;
                    yield return AviationRebasePlanner.Execute(player, root, ctx, formation,
                        changed => formedWing |= changed);
                    if (formedWing)
                    {
                        // The launch/flight actions already published their revision receipts.
                        frame.RefreshAfterFormation();
                    }
                    yield return ctx.WaitAtObserverActionBoundary();
                }
            }

            var phaseB = new StrategicPhaseResult();
            var telemetry = new TurnTelemetry();

            // The typed mid-turn architecture is the canonical production path. The
            // initial Phase A settles before operational admission; a later factual Development
            // invalidation may re-enter that same manager through the shared ledger. Each Recon
            // admission still settles exactly one task command, through the same bounded
            // settle -> observe -> typed re-admission path.
            {
                // The turn loop's control state (TurnLoop): settled steps, no progress, the
                // zero-Radar residual window, waiting return legs (LifecycleReturnPolicy: they wait
                // until the first Phase B round), Phase B rounds, stage and the open pass.
                var loop = new TurnLoopState();
                // The first Phase A already refreshed the derived part when it changed the world.
                frame.StartWithCredit(phaseA.StateChanged);

                // The components of the loop, each with its own explicit dependencies. They share
                // the frame (the settled snapshot and what is derived from it), the results of the
                // two spending phases and the turn-activity telemetry; none of them reaches back into
                // RunTurn.
                var phases = new PhaseResults(phaseA, phaseB);
                var runner = new StrategicReadmissionRunner(frame, readmission, phases, apBudget, radar,
                    player, root, hand, ctx);
                runner.SeedKeys(demandAxes);

                // A stand-alone Base level bought by the first Phase A opened a slot AFTER the admission
                // baselines above were taken: its Facility demand was generated on the refreshed world but
                // never judged, and an equal fingerprint would keep rejecting it as "settled". Forget the
                // Development baseline and admit it now, before missions or Phase B spend what is left.
                if (phaseA.CapacityUnlocks > 0 && demandAxes.Contains(DesireAxis.Development))
                {
                    yield return runner.Run(ReadmissionCause.CapacityUnlock,
                        StrategicInvalidationReason.Infrastructure | StrategicInvalidationReason.Capability,
                        new HashSet<DesireAxis> { DesireAxis.Development });
                }

                // The ONE main loop of the turn (TurnLoop): admission passes, Phase B rounds and the
                // cold residual in their baseline order, each opened and closed by its single owner.
                yield return TurnLoop.Run(loop,
                    new AdmissionIteration(frame, turnSession, runner, telemetry, assessment.Breakdown,
                        radar, trace, player, root, hand, ctx),
                    new TempoRound(frame, turnSession, runner, phases, player, root, hand, ctx),
                    new ColdResidual(frame, phases, apBudget, radar, demandAxes, player, root, hand, ctx));

                // Air-support safety net: a wing still over its target that cannot safely end
                // another turn there (its strike leg was not run, or found nothing) flies home now
                // instead of taking fuel damage. Free: the sortie launch was already paid.
                foreach (ArmyData unsafeWing in GroundCombatAirSupport.RecallUnsafeStrikes(player, ctx.Map))
                {
                    WorldAnalysis.StepObservationStamp beforeRecall =
                        WorldAnalysis.CaptureStepObservation(root, hand, frame.Snapshot);
                    bool recallChanged = false;
                    yield return AviationRebasePlanner.ExecuteContinuation(
                        player, root, ctx, unsafeWing, v => recallChanged = v);
                    frame.ObserveSettled(beforeRecall, null);
                    ReservationInvariants.CheckBoundary(player, root, ctx,
                        $"air-support recall #{unsafeWing.Id}");
                    yield return ctx.WaitAtObserverActionBoundary();
                }

                // Cold Phase A and the following typed admissions may have created or
                // re-bound actors AFTER management captured postCommitments. Housekeeping
                // must see the latest canonical ownership, never the pre-cold snapshot.
                frame.RefreshFinalOwnership();

                // Final reconciliation remains the only owner of end-of-turn aging/reaping. Intents
                // already reconciled locally carry LastReconciledTurn==turn and are not aged twice.
                turnSession.SettleAfterTurn(System.Array.Empty<MissionStepResult>());
                ReconAcceptanceAudit.Summarize(player, ctx.TurnNumber);
            }

            // Spec §9 — one per-turn StrategicManager summary so it is always answerable why each
            // hand card was or was not played this turn. Per-card blocking reasons are on the
            // strat.A/strat.B diag lines above (fails=[card: needs H/E/M/T=…] / defer … / hold …).
            // "remaining" carries no strategy-scope rejection: Phase B always runs, and card type
            // alone never suppresses a legal card.
            int mPlayed = phaseA.CardsPlayed + phaseB.CardsPlayed;
            int mGen = phaseA.GeneratedCardsSucceeded + phaseB.GeneratedCardsSucceeded;
            int mEquip = phaseA.EquipmentAssignmentsSucceeded + phaseB.EquipmentAssignmentsSucceeded;
            int mInfra = phaseA.InfrastructureBuilt + phaseB.InfrastructureBuilt;
            int mDrawn = phaseA.CardsDrawn + phaseB.CardsDrawn;
            int handEnd = hand?.Hand?.Count ?? 0;
            if (AiDebugLog.Verbose) AiDebugLog.Write($"[AI][V2][StrategicManager][Summary] handStart={handAtStart} "
                + $"played={mPlayed} (phaseA {phaseA.CardsPlayed}, phaseB {phaseB.CardsPlayed}) "
                + $"generated={mGen} equipAttached={mEquip} infraBuilt={mInfra} drawn={mDrawn} "
                + $"handEnd={handEnd} remaining={System.Math.Max(0, handEnd)} "
                + $"matAttempts={phaseA.MaterializationAttempts + phaseB.MaterializationAttempts} "
                + $"capDeliveries={phaseA.CapabilityDeliveries + phaseB.CapabilityDeliveries} "
                + "blockedReasons=see strat.A/strat.B diag lines");

            // End-of-Main physical resource control totals (spec §2.7). Housekeeping is zero-AP by
            // invariant and the bounded reaction pass logs its own [STATE]; captured here so the
            // Main line means the main phase.
            AiV2Trace.LogState(trace.Id, stateStart, AiV2Trace.Stamp(root));

            // 8. Off-budget housekeeping — NOT an axis, guaranteed minimum, cannot be out-competed.
            //    This safety/cleanup layer does not buy cards or create new
            //    capability and remains the authoritative same-hex reorganisation path.
            var housekeeping = new HousekeepingResult();
            yield return HousekeepingManager.RunHousekeeping(
                frame.Snapshot, player, root, ctx, frame.PostCommitments, housekeeping, phaseB.Reservation);
            ReservationInvariants.CheckBoundary(player, root, ctx, "housekeeping");
            if (housekeeping.StateChanged)
                frame.AcceptHousekeeping();
            yield return ctx.WaitAtObserverActionBoundary();

            // --- Main-phase activity bucket. DERIVED once, here, from this pipeline's own facts —
            //     never incremented inside a nested layer (spec §11). The Reaction bucket is owned
            //     by StrategicReactionPass the same way. Total = Main + Reaction, no double count.
            V2PhaseActivity main = V2TurnActivityTelemetry.Phase(player, ctx.TurnNumber, V2Phase.Main);
            main.DemandsRaised = frame.Demands.Count;
            main.MissionsConsidered = telemetry.Missions.Count;
            // §8 — the activity bucket's peers (Provisioned, ExecutionAttempts, …) are all
            // full-turn cumulative, so MissionsFunded is the distinct-missions-funded-this-turn
            // count, not just the last pack's.
            main.MissionsFunded = telemetry.FundedKeys.Count;
            main.Provisioned = telemetry.Provisioned.Count;
            foreach (KeyValuePair<ProvisionFailureKind, int> failure in telemetry.ProvisioningFailures)
                for (int i = 0; i < failure.Value; i++)
                    main.RecordProvisionFailure(failure.Key);
            main.ExecutionAttempts = telemetry.AllExecuted.Count(MissionRevalidator.WasAttempt);
            main.ExecutionsSucceeded = telemetry.AllExecuted.Count(MissionRevalidator.WasGenuineExecution);
            main.ExecutionsStaleOrSkipped = telemetry.AllExecuted.Count(MissionRevalidator.WasStaleOrSkipped);
            main.CardsPlayed = phaseA.CardsPlayed + phaseB.CardsPlayed;
            main.CardsDrawn = phaseA.CardsDrawn + phaseB.CardsDrawn;
            main.InfrastructureAttempts = phaseA.InfrastructureAttempts + phaseB.InfrastructureAttempts;
            main.InfrastructureBuilt = phaseA.InfrastructureBuilt + phaseB.InfrastructureBuilt;
            main.MaterializationAttempts = phaseA.MaterializationAttempts + phaseB.MaterializationAttempts;
            main.MaterializationsSucceeded = phaseA.MaterializationsSucceeded + phaseB.MaterializationsSucceeded;
            main.GeneratedCardAttempts = phaseA.GeneratedCardAttempts + phaseB.GeneratedCardAttempts;
            main.GeneratedCardsSucceeded = phaseA.GeneratedCardsSucceeded + phaseB.GeneratedCardsSucceeded;
            main.EquipmentAssignmentAttempts = phaseA.EquipmentAssignmentAttempts + phaseB.EquipmentAssignmentAttempts;
            main.EquipmentAssignmentsSucceeded = phaseA.EquipmentAssignmentsSucceeded + phaseB.EquipmentAssignmentsSucceeded;
            main.CapabilityDeliveries = phaseA.CapabilityDeliveries + phaseB.CapabilityDeliveries;

            // No strategic resource reservation may survive turn end. Anything still
            // standing is an owner that failed to release; log it and force-clear.
            turnSession.AuditTurnEnd(frame.Snapshot, frame.Recon);
            turnSession.CompleteReservations();
            ReservationInvariants.LogTurnSummary(player, ctx.TurnNumber);

            RecordInitiativeAnalytics(player, root, hand, initiativeStartAp, initiativeBaseAp, initiativeActionableAtStart);

            // Emit the canonical turn summary only after every turn-scoped cleanup/invariant check
            // has completed, so [TURN-END] really is the final strategic lifecycle marker.
            if (AiDebugLog.Verbose) AiDebugLog.Write($"[AI][V2] === {player.Nickname} — V2 turn ends "
                + $"(demands {frame.Demands.Count}, stratA {phaseA.CardsPlayed}, missions {telemetry.Missions.Count}, "
                + $"lastPackFunded {telemetry.Allocation.Funded.Count}, turnFundedUnique {telemetry.FundedKeys.Count}, "
                + $"provisioned {telemetry.Provisioned.Count}, executed {telemetry.AllExecuted.Count}, stratB {phaseB.CardsPlayed}) ===");
            V2TurnActivityTelemetry.LogSummary(player, ctx.TurnNumber);
            ApTurnMeasure apMeasure = ApTurnPressure.Measure(player, root, hand, ctx,
                initiativeStartAp, telemetry.Allocation.Deferred);
            ApTurnPressure.Record(player, ctx.TurnNumber, apMeasure);
            ApBudgetTelemetry.End(player, ctx,
                StrategicTempoBudget.For(player, ctx.TurnNumber).DrawActionsUsed, apMeasure);
            turnSession.Dispose();
            yield return null;
        }

        // End-of-turn initiative AP telemetry write-back (see the turn-start capture above). A
        // turn that ended at 0 AP only counts as "needed more AP" if real AP work still remained
        // — an unactivated field army, or an affordable AP-costing card still in hand.
        private static void RecordInitiativeAnalytics(PlayerSetupData player, PlayerRoot root, AiHandData hand,
            int startAp, int baseAp, int actionableAtStart)
        {
            int endAp = root.ActionPoints;
            int apSpent = UnityEngine.Mathf.Max(0, startAp - endAp);
            int unactivatedActionable =
                Game.Ai.V2.Initiative.PreTurnCapacityAnalysis.CountActionableFieldArmies(player, unactivatedOnly: true);

            bool affordableCardWaiting = false;
            if (hand != null && endAp > 0)
                foreach (Game.Cards.CardData c in hand.Hand)
                {
                    int ap = c != null ? CardCostRules.PlayAp(c) : 0;
                    if (ap > 0 && ap <= endAp) { affordableCardWaiting = true; break; }
                }

            bool hadPotentialWork = unactivatedActionable > 0 || affordableCardWaiting;

            Game.Ai.V2.Initiative.InitiativeAnalyticsHistory.Record(player,
                new Game.Ai.V2.Initiative.InitiativeTurnRecord(
                    baseAp, startAp, apSpent, endAp,
                    actionableAtStart, unactivatedActionable, hadPotentialWork));
            AiMatchStats.RecordAiTurn(player, startAp, apSpent, endAp);
        }
    }

    // The stage owners (WorldAnalysis, StrategyLayer, the mission planners, MissionContinuityLayer,
    // ResourceAllocator, ProvisioningManager, TaskExecutor / ReconAirExecutor, StrategicManager,
    // HousekeepingManager) live in their own folders; Assets/Scripts/Ai/V2/ARCHITECTURE.md is the
    // normative ownership map. This file only orders and calls them.
}
