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
        public int StateVersionAfter = -1;

        public void RecordMove() { AnyMoved = true; Steps++; }
        public void RecordLaunch() { AnyLaunched = true; }
        public void RecordStrike() { AnyStruck = true; }

        public bool Mutated => AnyMoved || AnyLaunched || AnyStruck;

        public V2ActionOutcome Outcome => new V2ActionOutcome(
            succeeded: Mutated, stateChanged: Mutated, apSpent: ApSpent, resourcesSpent: null,
            played: false, generated: false, attached: false, moved: AnyMoved, created: AnyLaunched,
            needsReplan: false, stateVersionAfter: StateVersionAfter,
            failReason: Mutated ? null : "air recon pass changed nothing");
    }

    // ARCH-02 §35 — EXECUTION ONLY. It receives an AirReconPlan (built by AirReconPlanner: actor
    // discovery/selection, ReconMode, launch-subset, first-step gate and energy policy) and, for
    // each airborne actor, asks AirReconStepDirector for the next tactical decision and issues
    // exactly the canonical Move / Strike / assignment-bookkeeping call it names. It never chooses
    // a mode, a step, a landing, a phase transition or whether a strike is worthwhile — that all
    // lives in the director, which is free to replan live on every call.
    //
    // AiTaskKind.AirRecon is retained only as the EXISTING landing-slot reservation primitive.
    internal static class ReconAirExecutor
    {
        public static IEnumerator Execute(AirReconPlan plan, PlayerSetupData player, PlayerRoot root,
            AiTurnContext ctx, WorldSnapshot snapshot, AirReconExecutionResult result = null)
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

            foreach (int id in plan.ContinueActorIds)
            {
                ArmyData air = Resolve(player, id);
                if (air != null && AviationRules.IsValidAirArmy(air) && air.Controller != null
                    && air.CurrentMovement > 0 && !AviationRules.IsOwnedAirfieldAt(air.Hex, player))
                    yield return RunActor(player, root, ctx, snapshot, air, result);
            }

            foreach (int id in plan.ReadyActorIds)
            {
                ArmyData air = Resolve(player, id);
                if (air != null && AviationRules.IsValidAirArmy(air) && air.Controller != null
                    && air.CurrentMovement > 0)
                    yield return RunActor(player, root, ctx, snapshot, air, result);
            }

            foreach (AirLaunchPlan lp in plan.Launches)
                yield return LaunchOne(lp, player, root, ctx, snapshot, result);

            result.ApSpent = Math.Max(0, apBefore - root.ActionPoints);
            result.StateVersionAfter = V2StateVersion.Current;
        }

        // Fly one planned launch. The stale-plan guard (CanAffordLaunch re-check) mirrors §35: if
        // an earlier sortie this pass consumed the AP/Energy, this launch is skipped and reported —
        // the executor does NOT re-plan a different subset or airfield.
        private static IEnumerator LaunchOne(AirLaunchPlan lp, PlayerSetupData player, PlayerRoot root,
            AiTurnContext ctx, WorldSnapshot snapshot, AirReconExecutionResult result)
        {
            if (lp?.Subset == null || lp.Subset.Count == 0)
                yield break;
            if (!AiAirSortiePlanner.CanAffordLaunch(root, player, lp.Subset))
            {
                AiDebugLog.Write($"[AI][V2][Recon][Air][Storage] airfield=({lp.AirfieldHex.Q},{lp.AirfieldHex.R}) "
                    + "— planned launch no longer affordable (earlier sortie spent it); skip, no replan");
                yield break;
            }

            ReconAirEnergyDecision energy = ReconAirEnergyPolicy.Evaluate(player, root, ctx.Map,
                lp.LaunchEnergy, lp.Score, excludeArmyId: -1);
            AiDebugLog.Write(energy.ToLog($"airfield=({lp.AirfieldHex.Q},{lp.AirfieldHex.R})"));
            if (!energy.Allowed)
            {
                AiDebugLog.Write($"[AI][V2][Recon][Air][Storage] airfield=({lp.AirfieldHex.Q},{lp.AirfieldHex.R}) "
                    + "— energy reserve now rejects the planned launch; skip, no replan");
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

            yield return AiAirSortiePlanner.LaunchRoutine(player, launchDecision, ctx, AirSortieKind.Recon);

            ArmyData launched = ArmyRegistry.AllForOwner(player)
                .Where(a => a != null && AviationRules.IsValidAirArmy(a) && !beforeIds.Contains(a.Id))
                .OrderBy(a => a.Id)
                .FirstOrDefault();
            if (launched == null)
            {
                AiDebugLog.Write($"[AI][V2][Recon][Air][Storage] airfield=({lp.AirfieldHex.Q},{lp.AirfieldHex.R}) "
                    + "— launch formed no aircraft");
                yield break;
            }

            if (launched.Hex.Equals(lp.AirfieldHex))
            {
                RemoveAirReconReservation(player, launched);
                AiDebugLog.Write($"[AI][V2][Recon][Air][Storage] actor=#{launched.Id} launch formed but "
                    + "first step made no progress; V2 assignment not started");
                yield break;
            }

            // ARCH-02 §36 — a formed sortie that left the airfield is an authoritative mutation.
            V2StateVersion.Bump();
            result.RecordLaunch();

            AirSortie reservationTask = AirSortieRegistry.ForArmy(player, launched);
            if (reservationTask != null && reservationTask.Kind == AirSortieKind.Recon)
            {
                reservationTask.Outbound = true;
                reservationTask.TargetHex = lp.FirstStepHex;
                reservationTask.LandingHex = lp.LandingHex;
            }

            ReconAssignment assignment = ReconAssignmentRegistry.GetOrCreate(player, launched.Id,
                lp.AirfieldHex, lp.FirstStepHex, lp.Mode, ctx.TurnNumber);
            ReconAssignmentRegistry.MarkProgress(player, launched.Id, ctx.TurnNumber);
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

            if (launched.Controller != null && launched.CurrentMovement > 0
                && !AviationRules.IsOwnedAirfieldAt(launched.Hex, player))
                yield return RunActor(player, root, ctx, snapshot, launched, result, arrivalStrikeCheckPending: true);
        }

        // Thin execute loop. Every decision comes from AirReconStepDirector; the executor only
        // resolves live liveness (lost / battle / landed / out of MP) and then issues the canonical
        // gameplay call the decision names.
        private static IEnumerator RunActor(PlayerSetupData player, PlayerRoot root, AiTurnContext ctx,
            WorldSnapshot snapshot, ArmyData initial, AirReconExecutionResult result,
            bool arrivalStrikeCheckPending = false)
        {
            bool movedAny = arrivalStrikeCheckPending;
            bool arrivalStrikeCheck = arrivalStrikeCheckPending;
            int armyId = initial.Id;
            int guard = Math.Max(4, initial.CurrentMovement + 5);

            while (guard-- > 0)
            {
                ArmyData air = Resolve(player, armyId);
                if (air == null || !AviationRules.IsValidAirArmy(air) || air.Controller == null)
                {
                    ReconAssignmentRegistry.Retire(player, armyId, "air mover lost / invalid");
                    ReconAirSortieRegistry.Retire(player, armyId);
                    if (air != null) RemoveAirReconReservation(player, air);
                    break;
                }

                if (ctx.HexSelection != null && ctx.HexSelection.IsBattleActive)
                    break;

                bool atAirfield = AviationRules.IsOwnedAirfieldAt(air.Hex, player);
                if (atAirfield && movedAny)
                {
                    AiDebugLog.Write($"[AI][V2][Recon][Air] actor=#{armyId} phase=Landing at "
                        + $"({air.Hex.Q},{air.Hex.R}); sortie complete");
                    ReconAssignmentRegistry.Retire(player, armyId, "air recon landed");
                    ReconAirSortieRegistry.Retire(player, armyId);
                    RemoveAirReconReservation(player, air);
                    break;
                }
                if (air.CurrentMovement <= 0)
                    break;

                ReconAirSortieState sortie = ReconAirSortieRegistry.GetOrCreate(player, armyId, air.Hex);
                AirReconStepDirector.StepDecision d = AirReconStepDirector.PlanStep(
                    player, root, ctx, snapshot, air, sortie, arrivalStrikeCheck);
                arrivalStrikeCheck = false;

                if (d.Kind == AirReconStepDirector.StepKind.Stop)
                {
                    if (d.RetireAssignment)
                        ReconAssignmentRegistry.Retire(player, armyId, d.Reason);
                    if (d.RemoveReservation)
                        RemoveAirReconReservation(player, air);
                    break;
                }

                if (d.Kind == AirReconStepDirector.StepKind.HoldEndTurn)
                    break;

                if (d.Kind == AirReconStepDirector.StepKind.HoldReopen)
                {
                    yield return ExecuteOpportunisticStrike(player, ctx, air, sortie, result);
                    ArmyData afterHoldStrike = Resolve(player, armyId);
                    if (afterHoldStrike == null || !AviationRules.IsValidAirArmy(afterHoldStrike)
                        || afterHoldStrike.Controller == null)
                    {
                        ReconAssignmentRegistry.Retire(player, armyId, "air mover lost / invalid");
                        ReconAirSortieRegistry.Retire(player, armyId);
                        break;
                    }
                    // The strike (if it fired) may already have set Return; only a still-Hold
                    // sortie resumes to the director's chosen ResumePhase.
                    if (sortie.Phase == ReconAirPhase.Hold)
                        sortie.Phase = d.ResumePhase;
                    ReconAssignmentRegistry.MarkProgress(player, armyId, ctx.TurnNumber);
                    continue;
                }

                if (d.Kind == AirReconStepDirector.StepKind.Strike)
                {
                    yield return ExecuteOpportunisticStrike(player, ctx, air, sortie, result);
                    ReconAssignmentRegistry.MarkProgress(player, armyId, ctx.TurnNumber);
                    continue;
                }

                if (d.Kind == AirReconStepDirector.StepKind.ReturnStep)
                {
                    AirSortie reservation = EnsureAirReconReservation(player, air, d.LandingHex, outbound: false);
                    if (reservation == null)
                    {
                        AiDebugLog.Write($"[AI][V2][Recon][Air] actor=#{armyId} return blocked — another task owns aircraft");
                        break;
                    }
                    AiDebugLog.Write($"[AI][V2][Recon][Air] actor=#{armyId} phase=Return "
                        + $"({air.Hex.Q},{air.Hex.R})->({d.Step.Q},{d.Step.R}) "
                        + $"landing=({d.LandingHex.Q},{d.LandingHex.R}) informative={(d.AlsoInformative ? 1 : 0)} {d.Reason}");
                    bool moved = false;
                    yield return MoveOne(player, ctx, air, d.Step, "V2 Air Recon — safe return", () => moved = true);
                    movedAny |= moved;
                    if (!moved) break;
                    V2StateVersion.Bump();
                    result.RecordMove();
                    ArmyData afterReturn = Resolve(player, armyId);
                    if (afterReturn != null) sortie.RecordStep(afterReturn.Hex);
                    arrivalStrikeCheck = true;
                    ReconAssignmentRegistry.MarkProgress(player, armyId, ctx.TurnNumber);
                    continue;
                }

                // ForwardStep
                ReconAssignment assignment = ReconAssignmentRegistry.GetOrCreate(player, armyId, air.Hex,
                    d.Step, d.Mode, ctx.TurnNumber);
                AirSortie reservationTask = EnsureAirReconReservation(player, air, d.LandingHex,
                    outbound: true, target: d.Step);
                if (reservationTask == null)
                {
                    AiDebugLog.Write($"[AI][V2][Recon][Air] actor=#{armyId} step blocked — another task owns aircraft");
                    break;
                }
                sortie.ChosenLandingHex = d.LandingHex;
                sortie.HasChosenLanding = true;

                bool stepMoved = false;
                yield return MoveOne(player, ctx, air, d.Step,
                    $"V2 Air Recon — {assignment.Mode} {sortie.Phase} one-step live replan", () => stepMoved = true);
                movedAny |= stepMoved;
                if (!stepMoved) break;
                V2StateVersion.Bump();
                result.RecordMove();
                ArmyData afterStep = Resolve(player, armyId);
                if (afterStep != null) sortie.RecordStep(afterStep.Hex);
                if (d.PivotToReturnAfterMove && sortie.Phase == ReconAirPhase.Turning)
                {
                    AiDebugLog.Write($"[AI][V2][Recon][Air] actor=#{armyId} phase=Turning->Return pivot step taken");
                    sortie.Phase = ReconAirPhase.Return;
                }
                arrivalStrikeCheck = true;
                ReconAssignmentRegistry.MarkProgress(player, armyId, ctx.TurnNumber);
            }

            // A ready wing that was evaluated this pass but never actually left its airfield must
            // not leave a half-initialised sortie state behind (AI-AIR-02 lifecycle drift).
            ArmyData settled = Resolve(player, armyId);
            if (!movedAny && settled != null && AviationRules.IsOwnedAirfieldAt(settled.Hex, player))
                ReconAirSortieRegistry.Retire(player, armyId);
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
