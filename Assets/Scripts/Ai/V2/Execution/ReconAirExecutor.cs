using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Game.Aviation;
using Game.Cards;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Units;

using Game.Combat;

namespace Game.Ai.V2
{
    // ARCH-02 §36 — the common lifecycle-readable projection for a whole air-recon pass. Air recon
    // is a coroutine (it cannot `return` a value), so the caller passes one of these in to read
    // "did the world move, what did it cost, what is the state-version now".
    internal sealed class AirReconExecutionResult : IV2ActionResult
    {
        public bool AnyMoved;
        public bool AnyLaunched;
        public bool AnyStruck;
        public int Steps;
        public float ApSpent;
        public Game.Cards.ResourceCost ResourcesSpent;   // real H/E/M/T delta over the pass (null = none)
        public int StateVersionAfter = -1;

        public void RecordMove() { AnyMoved = true; Steps++; }
        public void RecordLaunch() { AnyLaunched = true; }
        public void RecordStrike() { AnyStruck = true; }

        public bool Mutated => AnyMoved || AnyLaunched || AnyStruck;

        public V2ActionOutcome Outcome => new V2ActionOutcome(
            succeeded: Mutated, stateChanged: Mutated, apSpent: ApSpent, resourcesSpent: ResourcesSpent,
            played: false, generated: false, attached: false, moved: AnyMoved, created: AnyLaunched,
            needsReplan: false, stateVersionAfter: StateVersionAfter,
            failReason: Mutated ? null : "air recon pass changed nothing");
    }

    // ARCH-02 §35 — EXECUTION ONLY. It receives an AirReconPlan (round 4: actor/airfield/subset WHO
    // is now picked by ReconAssignmentPlanner/ProvisioningManager, the same single owner Ground has;
    // AirReconPlanner only turns that binding, plus continuing wings, into this plan's shape — mode,
    // first-step gate and energy policy re-derived fresh as execution-input assembly, never a second
    // actor selection) and, for each airborne actor, asks AirReconStepDirector for the next tactical
    // decision and issues exactly the canonical Move / Strike / assignment-bookkeeping call it names.
    // It never chooses a mode, a step, a landing, a phase transition or whether a strike is
    // worthwhile — that all lives in the director, which is free to replan live on every call.
    //
    // AiTaskKind.AirRecon is retained only as the EXISTING landing-slot reservation primitive.
    internal static class ReconAirExecutor
    {
        // RECON-AIR-06 (round 5) — `perMissionResults` collects one ExecutionResult PER air-executed
        // ProvisionedMission this pass, the SAME shape Ground's TaskExecutor produces, so each one
        // flows into MissionOutcomeLedger.RecordExecution / MissionContinuity exactly like Ground's
        // do. The aggregate `result` (AirReconExecutionResult) remains — it is still what the
        // orchestrator logs as a pass-wide telemetry rollup — but it is no longer the ONLY thing
        // produced; a caller that omits `perMissionResults` (older call sites / tests) still gets the
        // aggregate only, exactly the previous behaviour.
        public static IEnumerator Execute(AirReconPlan plan, PlayerSetupData player, PlayerRoot root,
            AiTurnContext ctx, WorldSnapshot snapshot, AirReconExecutionResult result = null,
            List<ExecutionResult> perMissionResults = null)
        {
            result ??= new AirReconExecutionResult();
            if (plan == null || player == null || root == null || ctx?.Map == null || snapshot?.Self == null)
            {
                AiDebugLog.Write($"[AI][V2][Recon][Air] exec — not reached ({plan?.Summary ?? "no plan"})");
                result.StateVersionAfter = V2StateVersion.Current;
                yield break;
            }
            AiDebugLog.Write($"[AI][V2][Recon][Air] exec — {plan.Summary}");
            int apBefore = root.ActionPoints;
            int h0 = root.GetResource(Game.Economy.ResourceType.Human);
            int e0 = root.GetResource(Game.Economy.ResourceType.Energy);
            int m0 = root.GetResource(Game.Economy.ResourceType.Materials);
            int t0 = root.GetResource(Game.Economy.ResourceType.Tech);

            // Continuing wings have no fresh ProvisionedMission this pass (Continuity, not fresh
            // Assignment — see AirReconPlanner's header) — anchor their live replanning at the
            // durable target already recorded on their ReconPatrolState (RECON-AIR-05), but they
            // produce no per-mission ExecutionResult (there is no ledger row for them this turn).
            foreach (int id in plan.ContinueActorIds)
            {
                ArmyData air = Resolve(player, id);
                if (air != null && AviationRules.IsValidAirArmy(air) && air.Controller != null
                    && air.CurrentMovement > 0 && !AviationRules.IsOwnedAirfieldAt(air.Hex, player))
                {
                    HexCoord? focus = ReconPatrolStateRegistry.TryGet(player, id, out ReconPatrolState st)
                        ? st.StrategicAnchor : (HexCoord?)null;
                    yield return RunActor(player, root, ctx, snapshot, air, result, missionFocusHex: focus);
                }
            }

            foreach (int id in plan.ReadyActorIds)
            {
                ArmyData air = Resolve(player, id);
                if (air == null || !AviationRules.IsValidAirArmy(air) || air.Controller == null
                    || air.CurrentMovement <= 0)
                    continue;

                plan.ReadyMissionByActorId.TryGetValue(id, out ProvisionedMission pm);
                ExecutionResult perMission = pm != null ? NewPerMissionResult(pm, air.Hex, air.Id) : null;
                int apBeforeActor = root.ActionPoints;
                yield return RunActor(player, root, ctx, snapshot, air, result,
                    missionFocusHex: pm?.FocusHex, perMissionResult: perMission);
                if (perMission != null)
                {
                    perMission.ApSpent = Math.Max(0f, apBeforeActor - root.ActionPoints);
                    FinalizePerMissionResult(player, pm, perMission);
                    perMissionResults?.Add(perMission);
                }
            }

            foreach (AirLaunchPlan lp in plan.Launches)
                yield return LaunchOne(lp, player, root, ctx, snapshot, result, perMissionResults);

            result.ApSpent = Math.Max(0, apBefore - root.ActionPoints);
            int hSpent = Math.Max(0, h0 - root.GetResource(Game.Economy.ResourceType.Human));
            int eSpent = Math.Max(0, e0 - root.GetResource(Game.Economy.ResourceType.Energy));
            int mSpent = Math.Max(0, m0 - root.GetResource(Game.Economy.ResourceType.Materials));
            int tSpent = Math.Max(0, t0 - root.GetResource(Game.Economy.ResourceType.Tech));
            result.ResourcesSpent = (hSpent | eSpent | mSpent | tSpent) != 0
                ? new Game.Cards.ResourceCost { human = hSpent, energy = eSpent, materials = mSpent, tech = tSpent }
                : null;
            result.StateVersionAfter = V2StateVersion.Current;
        }

        // Fly one planned launch. The stale-plan guard (CanAffordLaunch re-check) mirrors §35: if
        // an earlier sortie this pass consumed the AP/Energy, this launch is skipped and reported —
        // the executor does NOT re-plan a different subset or airfield.
        //
        // RECON-AIR-06 — every exit path (including the early "skip, no replan" ones) reports a
        // per-mission ExecutionResult when lp.Mission is set, so a ProvisionedMission that never
        // actually launched still gets a real ledger row (StepsMoved=0, an honest StopReason)
        // instead of silently vanishing from MissionOutcomeLedger/MissionContinuity.
        private static IEnumerator LaunchOne(AirLaunchPlan lp, PlayerSetupData player, PlayerRoot root,
            AiTurnContext ctx, WorldSnapshot snapshot, AirReconExecutionResult result,
            List<ExecutionResult> perMissionResults = null)
        {
            ProvisionedMission pm = lp?.Mission;
            void ReportNoLaunch(ExecutionStopReason why)
            {
                if (pm == null || perMissionResults == null) return;
                ExecutionResult er = NewPerMissionResult(pm, lp.AirfieldHex, -1);
                er.StopReason = why;
                perMissionResults.Add(er);
            }

            if (lp?.Subset == null || lp.Subset.Count == 0)
                yield break;
            if (!AiAirSortiePlanner.CanAffordLaunch(root, player, lp.Subset))
            {
                AiDebugLog.Write($"[AI][V2][Recon][Air][Storage] airfield=({lp.AirfieldHex.Q},{lp.AirfieldHex.R}) "
                    + "— planned launch no longer affordable (earlier sortie spent it); skip, no replan");
                ReportNoLaunch(ExecutionStopReason.MoverLost);
                yield break;
            }

            ReconAirEnergyDecision energy = ReconAirEnergyPolicy.Evaluate(player, root, ctx.Map,
                lp.LaunchEnergy, lp.Score, excludeArmyId: -1);
            AiDebugLog.Write(energy.ToLog($"airfield=({lp.AirfieldHex.Q},{lp.AirfieldHex.R})"));
            if (!energy.Allowed)
            {
                AiDebugLog.Write($"[AI][V2][Recon][Air][Storage] airfield=({lp.AirfieldHex.Q},{lp.AirfieldHex.R}) "
                    + "— energy reserve now rejects the planned launch; skip, no replan");
                ReportNoLaunch(ExecutionStopReason.MoverLost);
                yield break;
            }

            bool firstVisitedBefore = VisionSystem.IsVisited(player, lp.FirstStepHex);
            var beforeIds = new HashSet<int>(ArmyRegistry.AllForOwner(player)
                .Where(AviationRules.IsValidAirArmy).Select(a => a.Id));
            var launchDecision = new AiDecision
            {
                Kind = AiActionKind.LaunchAirRecon,
                ExistingArmy = null,
                TargetHex = lp.AirfieldHex,
                AircraftToLaunch = lp.Subset,
                AirActionHex = lp.FirstStepHex,
                AirLandingHex = lp.LandingHex,
                Score = lp.Score,
                Reason = $"V2 Air Recon — {lp.Mode} one-step launch; {lp.Reason}",
            };

            int apBeforeLaunch = root.ActionPoints;
            yield return AiAirSortiePlanner.LaunchRoutine(player, launchDecision, ctx, AirSortieKind.Recon);

            ArmyData launched = ArmyRegistry.AllForOwner(player)
                .Where(a => a != null && AviationRules.IsValidAirArmy(a) && !beforeIds.Contains(a.Id))
                .OrderBy(a => a.Id)
                .FirstOrDefault();
            if (launched == null)
            {
                AiDebugLog.Write($"[AI][V2][Recon][Air][Storage] airfield=({lp.AirfieldHex.Q},{lp.AirfieldHex.R}) "
                    + "— launch formed no aircraft");
                ReportNoLaunch(ExecutionStopReason.MoverLost);
                yield break;
            }

            if (launched.Hex.Equals(lp.AirfieldHex))
            {
                RemoveAirReconReservation(player, launched);
                AiDebugLog.Write($"[AI][V2][Recon][Air][Storage] actor=#{launched.Id} launch formed but "
                    + "first step made no progress; V2 assignment not started");
                ReportNoLaunch(ExecutionStopReason.NoSafeStep);
                yield break;
            }

            // ARCH-02 §36 — a formed sortie that left the airfield is an authoritative mutation:
            // the aircraft was created AND took its first movement step off the airfield.
            V2StateVersion.Bump();
            result.RecordLaunch();
            result.RecordMove();

            AirSortie reservationTask = AirSortieRegistry.ForArmy(player, launched);
            if (reservationTask != null && reservationTask.Kind == AirSortieKind.Recon)
            {
                reservationTask.Outbound = true;
                reservationTask.TargetHex = lp.FirstStepHex;
                reservationTask.LandingHex = lp.LandingHex;
            }

            ReconPatrolState assignment = ReconPatrolStateRegistry.GetOrCreate(player, launched.Id,
                lp.AirfieldHex, lp.FirstStepHex, lp.Mode, ctx.TurnNumber);
            ReconPatrolStateRegistry.MarkProgress(player, launched.Id, ctx.TurnNumber);
            ReconAirSortieState launchSortie = ReconAirSortieRegistry.GetOrCreate(player, launched.Id, lp.AirfieldHex);
            launchSortie.LaunchTurn = ctx.TurnNumber;
            launchSortie.RecordStep(launched.Hex);
            launchSortie.BestOutboundStepScore = Math.Max(launchSortie.BestOutboundStepScore, lp.Score);
            AiReconIntelMemory.ObserveCurrentVisibility(player, ctx.TurnNumber);
            StampObservedFootprint(player, ctx, launched, launched.Hex);
            LogVisitedInvariant(player, lp.FirstStepHex, firstVisitedBefore, "storage-launch-first-step");
            AiDebugLog.Write($"[AI][V2][Recon][Air][Handoff] actor=#{launched.Id} "
                + $"launch=({lp.AirfieldHex.Q},{lp.AirfieldHex.R}) first=({launched.Hex.Q},{launched.Hex.R}) "
                + $"mode={assignment.Mode}; V1 task retained only as landing-slot reservation");

            // RECON-AIR-06 — the real ArmyId now exists. From here, the synthetic negative ActorKey
            // Assignment used to hold the airfield's uniqueness claim is resolved to the real
            // ArmyId — that real id is what the per-mission result (and, through it, Continuity)
            // carries from now on, never the synthetic key.
            ExecutionResult perMission = pm != null ? NewPerMissionResult(pm, lp.AirfieldHex, launched.Id) : null;
            if (perMission != null)
            {
                perMission.StepsMoved = 1; // the launch's own first step, off the airfield
                perMission.StartHex = lp.AirfieldHex;
            }

            if (launched.Controller != null && launched.CurrentMovement > 0
                && !AviationRules.IsOwnedAirfieldAt(launched.Hex, player))
                // RECON-AIR-05 — anchor further live replanning at the SAME Refresh target this
                // launch was bound to (lp.Mission.FocusHex), not a fresh pick.
                yield return RunActor(player, root, ctx, snapshot, launched, result,
                    arrivalStrikeCheckPending: true, missionFocusHex: pm?.FocusHex, perMissionResult: perMission);

            if (perMission != null)
            {
                perMission.ApSpent = Math.Max(0f, apBeforeLaunch - root.ActionPoints);
                FinalizePerMissionResult(player, pm, perMission);
                perMissionResults?.Add(perMission);
            }
        }

        // RECON-AIR-06 — shared per-mission ExecutionResult scaffold, mirroring how
        // TaskExecutor/ReconGroundExecutor seed one: Key/Source/StartHex from the ProvisionedMission,
        // ActualActorArmyId the REAL army id (>=0) once known (-1 = none yet / never materialised).
        private static ExecutionResult NewPerMissionResult(ProvisionedMission pm, HexCoord startHex, int actorArmyId) =>
            new ExecutionResult
            {
                Key = pm.Key,
                Source = pm,
                StartHex = startHex,
                FinalHex = startHex,
                ActualActorArmyId = actorArmyId >= 0 ? actorArmyId : (int?)null,
            };

        // RECON-AIR-06 — the same "is the bound objective satisfied" question Ground's
        // RefreshObjectiveSatisfied asks, reused verbatim here so Air's per-mission result carries
        // the SAME ReachedGoal/ObjectiveSatisfied semantics Ground's does.
        private static void FinalizePerMissionResult(PlayerSetupData player, ProvisionedMission pm, ExecutionResult er)
        {
            if (player == null || pm == null || er == null || er.ReachedGoal)
                return;
            bool satisfied = pm.ScoutKind == ScoutTargetKind.Surveil
                ? ScoutObjectiveEvaluator.IsSurveilSatisfiedLive(player, pm.FocusHex, pm.TrackedArmyId, pm.BaselineObservedTurn)
                : ScoutObjectiveEvaluator.IsRefreshSatisfiedLive(player, pm.FocusHex);
            if (satisfied)
                er.ReachedGoal = true;
        }

        // Thin execute loop. Every decision comes from AirReconStepDirector; the executor only
        // resolves live liveness (lost / battle / landed / out of MP) and then issues the canonical
        // gameplay call the decision names.
        //
        // RECON-AIR-05/06 — `missionFocusHex` is forwarded to every PlanStep call so the tactical
        // planner's live replanning stays anchored at the bound target; `perMissionResult`, when
        // given, accumulates this actor's StepsMoved/FinalHex/StopReason for its ONE provisioned
        // mission this pass (a continuing wing with no fresh mission passes null — see Execute).
        private static IEnumerator RunActor(PlayerSetupData player, PlayerRoot root, AiTurnContext ctx,
            WorldSnapshot snapshot, ArmyData initial, AirReconExecutionResult result,
            bool arrivalStrikeCheckPending = false, HexCoord? missionFocusHex = null,
            ExecutionResult perMissionResult = null)
        {
            bool movedAny = arrivalStrikeCheckPending;
            bool arrivalStrikeCheck = arrivalStrikeCheckPending;
            int armyId = initial.Id;
            int guard = Math.Max(4, initial.CurrentMovement + 5);
            int localSteps = 0;
            ExecutionStopReason localStop = ExecutionStopReason.OutOfMovement;

            while (guard-- > 0)
            {
                ArmyData air = Resolve(player, armyId);
                if (air == null || !AviationRules.IsValidAirArmy(air) || air.Controller == null)
                {
                    ReconPatrolStateRegistry.Retire(player, armyId, "air mover lost / invalid");
                    ReconAirSortieRegistry.Retire(player, armyId);
                    if (air != null) RemoveAirReconReservation(player, air);
                    localStop = ExecutionStopReason.MoverLost;
                    break;
                }

                if (ctx.HexSelection != null && ctx.HexSelection.IsBattleActive)
                {
                    localStop = ExecutionStopReason.BattleStarted;
                    break;
                }

                bool atAirfield = AviationRules.IsOwnedAirfieldAt(air.Hex, player);
                if (atAirfield && movedAny)
                {
                    AiDebugLog.Write($"[AI][V2][Recon][Air] actor=#{armyId} phase=Landing at "
                        + $"({air.Hex.Q},{air.Hex.R}); sortie complete");
                    ReconPatrolStateRegistry.Retire(player, armyId, "air recon landed");
                    ReconAirSortieRegistry.Retire(player, armyId);
                    RemoveAirReconReservation(player, air);
                    localStop = ExecutionStopReason.OutOfMovement;
                    break;
                }
                if (air.CurrentMovement <= 0)
                {
                    localStop = ExecutionStopReason.OutOfMovement;
                    break;
                }

                ReconAirSortieState sortie = ReconAirSortieRegistry.GetOrCreate(player, armyId, air.Hex);
                // Explicit lifecycle bookkeeping is the executor's job, not the planner's:
                // observe where the wing actually is, then mark this AI turn processed.
                ReconAirSortieLifecycle.Observe(sortie, air, ctx, atAirfield);
                bool newTurn = ReconAirSortieLifecycle.BeginTurn(sortie, ctx.TurnNumber);
                AirReconStepDirector.StepDecision d = AirReconStepDirector.PlanStep(
                    player, root, ctx, snapshot, air, sortie, newTurn, arrivalStrikeCheck, missionFocusHex);
                arrivalStrikeCheck = false;

                if (d.Kind == AirReconStepDirector.StepKind.Stop)
                {
                    if (d.RetireAssignment)
                        ReconPatrolStateRegistry.Retire(player, armyId, d.Reason);
                    if (d.RemoveReservation)
                        RemoveAirReconReservation(player, air);
                    localStop = ExecutionStopReason.NoSafeStep;
                    break;
                }

                if (d.Kind == AirReconStepDirector.StepKind.HoldEndTurn)
                {
                    localStop = ExecutionStopReason.OutOfMovement;
                    break;
                }

                if (d.Kind == AirReconStepDirector.StepKind.HoldReopen)
                {
                    yield return ExecuteOpportunisticStrike(player, ctx, air, sortie, result);
                    ArmyData afterHoldStrike = Resolve(player, armyId);
                    if (afterHoldStrike == null || !AviationRules.IsValidAirArmy(afterHoldStrike)
                        || afterHoldStrike.Controller == null)
                    {
                        ReconPatrolStateRegistry.Retire(player, armyId, "air mover lost / invalid");
                        ReconAirSortieRegistry.Retire(player, armyId);
                        break;
                    }
                    // The strike (if it fired) may already have set Return; only a still-Hold
                    // sortie resumes to the director's chosen ResumePhase. Apply stamps the
                    // decision reason now that the hold has actually been acted on.
                    if (sortie.Phase == ReconAirPhase.Hold)
                        sortie.Phase = d.ResumePhase;
                    ReconAirSortieLifecycle.Apply(sortie, d);
                    ReconPatrolStateRegistry.MarkProgress(player, armyId, ctx.TurnNumber);
                    continue;
                }

                if (d.Kind == AirReconStepDirector.StepKind.Strike)
                {
                    yield return ExecuteOpportunisticStrike(player, ctx, air, sortie, result);
                    ReconPatrolStateRegistry.MarkProgress(player, armyId, ctx.TurnNumber);
                    continue;
                }

                if (d.Kind == AirReconStepDirector.StepKind.ReturnStep)
                {
                    AirSortie reservation = EnsureAirReconReservation(player, air, d.LandingHex, outbound: false);
                    if (reservation == null)
                    {
                        AiDebugLog.Write($"[AI][V2][Recon][Air] actor=#{armyId} return blocked — another task owns aircraft");
                        localStop = ExecutionStopReason.NoSafeStep;
                        break;
                    }
                    AiDebugLog.Write($"[AI][V2][Recon][Air] actor=#{armyId} phase=Return "
                        + $"({air.Hex.Q},{air.Hex.R})->({d.Step.Q},{d.Step.R}) "
                        + $"landing=({d.LandingHex.Q},{d.LandingHex.R}) informative={(d.AlsoInformative ? 1 : 0)} {d.Reason}");
                    bool moved = false;
                    yield return MoveOne(player, ctx, air, d.Step, "V2 Air Recon — safe return", () => moved = true);
                    movedAny |= moved;
                    if (!moved) { localStop = ExecutionStopReason.MoveRejected; break; }
                    V2StateVersion.Bump();
                    result.RecordMove();
                    localSteps++;
                    ReconAirSortieLifecycle.Apply(sortie, d);   // Phase=Return + reason + best-score, now the step actually happened
                    ArmyData afterReturn = Resolve(player, armyId);
                    if (afterReturn != null) sortie.RecordStep(afterReturn.Hex);
                    arrivalStrikeCheck = true;
                    ReconPatrolStateRegistry.MarkProgress(player, armyId, ctx.TurnNumber);
                    continue;
                }

                // ForwardStep
                ReconPatrolState assignment = ReconPatrolStateRegistry.GetOrCreate(player, armyId, air.Hex,
                    d.Step, d.Mode, ctx.TurnNumber);
                AirSortie reservationTask = EnsureAirReconReservation(player, air, d.LandingHex,
                    outbound: true, target: d.Step);
                if (reservationTask == null)
                {
                    AiDebugLog.Write($"[AI][V2][Recon][Air] actor=#{armyId} step blocked — another task owns aircraft");
                    localStop = ExecutionStopReason.NoSafeStep;
                    break;
                }
                // ChosenLandingHex / HasChosenLanding are part of the intended transition now
                // (d.SetChosenLanding) — applied by ReconAirSortieLifecycle.Apply only after the
                // step below actually moves the wing (r5 — no landing write on a rejected move).

                bool stepMoved = false;
                yield return MoveOne(player, ctx, air, d.Step,
                    $"V2 Air Recon — {assignment.Mode} {sortie.Phase} one-step live replan", () => stepMoved = true);
                movedAny |= stepMoved;
                if (!stepMoved) { localStop = ExecutionStopReason.MoveRejected; break; }
                V2StateVersion.Bump();
                result.RecordMove();
                localSteps++;
                ArmyData afterStep = Resolve(player, armyId);
                if (afterStep != null) sortie.RecordStep(afterStep.Hex);
                // Apply the intended transition ONLY now that the move succeeded (r4 — a rejected
                // ForwardStep must not leave the sortie in Turning/Return).
                ReconAirSortieLifecycle.Apply(sortie, d);
                if (d.PivotToReturnAfterMove)
                    AiDebugLog.Write($"[AI][V2][Recon][Air] actor=#{armyId} phase=Turning->Return pivot step taken");
                arrivalStrikeCheck = true;
                ReconPatrolStateRegistry.MarkProgress(player, armyId, ctx.TurnNumber);
            }

            // A ready wing that was evaluated this pass but never actually left its airfield must
            // not leave a half-initialised sortie state behind (AI-AIR-02 lifecycle drift).
            ArmyData settled = Resolve(player, armyId);
            if (!movedAny && settled != null && AviationRules.IsOwnedAirfieldAt(settled.Hex, player))
                ReconAirSortieRegistry.Retire(player, armyId);

            // RECON-AIR-06 — fold this actor's steps/final position/stop reason into its bound
            // mission's ExecutionResult (StepsMoved accumulates — LaunchOne may have already counted
            // the launch's own first step before handing off to this same RunActor call).
            if (perMissionResult != null)
            {
                perMissionResult.StepsMoved += localSteps;
                perMissionResult.FinalHex = settled?.Hex ?? perMissionResult.FinalHex;
                perMissionResult.StopReason = localStop;
            }
        }

        // §46 — EXECUTION of an opportunistic air strike the director already judged favourable and
        // safe. The executor re-guards CanStrikeAtCurrentHex (live), resolves the AviationActions
        // call, refreshes intel, then hands the post-strike phase decision back to the director.
        private static IEnumerator ExecuteOpportunisticStrike(PlayerSetupData player, AiTurnContext ctx,
            ArmyData air, ReconAirSortieState sortie, AirReconExecutionResult passResult)
        {
            if (air == null || ctx == null || !AviationRules.IsValidAirArmy(air))
                yield break;

            AirReconStepDirector.StrikeAssessment assess =
                AirReconStepDirector.EvaluateOpportunisticStrike(player, ctx, air);
            if (!assess.Favourable)
                yield break;

            AviationCombatPresenter presenter = ctx.HexSelection?.AviationCombatPresenter;
            if (presenter == null)
                yield break;

            AiDebugLog.Write($"[AI][V2][Recon][Air][Opportunity] actor=#{air.Id} hex=({air.Hex.Q},{air.Hex.R}) "
                + $"decision=STRIKE dmgFrac={assess.DamageFraction:0.00} killP={assess.KillProbability:0.00}");
            var strike = new AviationCombatPresenter.AirStrikeResult();
            yield return AviationActions.ResolveStationaryStrike(presenter, air, strike);

            if (strike.Attacked)
            {
                V2StateVersion.Bump();
                passResult.RecordStrike();
            }

            ArmyData afterStrike = Resolve(player, air.Id);
            AiReconIntelMemory.ObserveCurrentVisibility(player, ctx.TurnNumber);
            AirReconStepDirector.ResolveAfterStrike(player, ctx, afterStrike, sortie, strike.Attacked);
        }

        private static IEnumerator MoveOne(PlayerSetupData player, AiTurnContext ctx, ArmyData air,
            HexCoord next, string reason, Action onMoved)
        {
            HexCoord before = air.Hex;
            bool visitedBefore = VisionSystem.IsVisited(player, next);
            var decision = AiDecision.Move(air, next, reason, 0f);
            var trace = new AiMoveExecutionTrace();
            yield return AiTurnController.MoveArmyRoutine(player, decision, ctx, trace);

            ArmyData live = Resolve(player, air.Id);
            HexCoord after = live != null ? live.Hex : trace.EndHex;
            if (!after.Equals(before))
                onMoved?.Invoke();

            AiReconIntelMemory.ObserveCurrentVisibility(player, ctx.TurnNumber);
            LogVisitedInvariant(player, next, visitedBefore, "live-step");
            if (!after.Equals(before))
                StampObservedFootprint(player, ctx, air, after);
            AiDebugLog.Write($"[AI][V2][Recon][Air][Observe] actor=#{air.Id} "
                + $"({before.Q},{before.R})->({after.Q},{after.R}) intel refreshed; groundVisitedWrite=0");
        }

        private static AirSortie EnsureAirReconReservation(PlayerSetupData player, ArmyData air,
            HexCoord landing, bool outbound, HexCoord? target = null)
        {
            AirSortie task = AirSortieRegistry.ForArmy(player, air);
            if (task == null)
            {
                task = new AirSortie { Kind = AirSortieKind.Recon, Army = air };
                AirSortieRegistry.Add(player, task);
            }
            if (task.Kind != AirSortieKind.Recon)
                return null;

            task.Outbound = outbound;
            task.LandingHex = landing;
            task.TargetHex = target ?? landing;
            return task;
        }

        private static void RemoveAirReconReservation(PlayerSetupData player, ArmyData air)
        {
            AirSortie task = air != null ? AirSortieRegistry.ForArmy(player, air) : null;
            if (task == null || task.Kind != AirSortieKind.Recon)
                return;
            AirSortieRegistry.Remove(player, task);
        }

        private static void LogVisitedInvariant(PlayerSetupData player, HexCoord hex, bool visitedBefore, string phase)
        {
            bool visitedAfter = VisionSystem.IsVisited(player, hex);
            if (!visitedBefore && visitedAfter)
                AiDebugLog.Write($"[AI][V2][Recon][Air][INVARIANT-FAIL] phase={phase} aircraft unexpectedly "
                    + $"marked ground Visited at ({hex.Q},{hex.R})");
        }

        // AI-AIR-01 §5 — record the whole on-map vision footprint of a completed air step as
        // recently-air-observed, tagged with this sortie.
        private static void StampObservedFootprint(PlayerSetupData player, AiTurnContext ctx,
            ArmyData air, HexCoord center)
        {
            int sortieId = ReconAirSortieRegistry.TryGet(player, air.Id, out ReconAirSortieState st)
                ? st.SortieId : -1;
            int vision = (ctx?.GameConfig != null ? ctx.GameConfig.armyVisionRadius : 0)
                + AbilityParams.GetBestRecceRadius(air);
            foreach (HexCoord h in HexGridMath.HexesInRange(center, Math.Max(0, vision)))
                if (ctx?.Map != null && ctx.Map.TryGetTerrainAt(h, out _))
                    AirReconCoverageRegistry.Record(player, h, ctx.TurnNumber, sortieId);
        }

        private static ArmyData Resolve(PlayerSetupData player, int armyId) =>
            ArmyRegistry.AllForOwner(player).FirstOrDefault(a => a != null && a.Id == armyId);
    }
}
