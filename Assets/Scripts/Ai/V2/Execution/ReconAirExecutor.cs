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
        internal sealed class ActorStepControl
        {
            public bool CanContinue;
            public bool MovedAny;
            public bool CommandAttempted;
            public ExecutionStopReason StopReason = ExecutionStopReason.StepCompleted;

            public void ResetStep()
            {
                CanContinue = false;
                CommandAttempted = false;
                StopReason = ExecutionStopReason.StepCompleted;
            }
        }

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

            foreach (AirReconSkippedMission skipped in plan.SkippedMissions)
            {
                ProvisionedMission pm = skipped?.Mission;
                if (pm == null || perMissionResults == null)
                    continue;
                ExecutionResult er = NewPerMissionResult(pm, pm.AirfieldHex, -1);
                if (skipped.Reason != ExecutionStopReason.MoverLost && ObjectiveSatisfied(player, pm))
                    MarkSatisfiedNoOp(pm, er);
                else
                    er.StopReason = skipped.Reason;
                er.StateVersionAfter = V2StateVersion.Current;
                perMissionResults.Add(er);
            }

            // Round 7 (Problem 1) — MANDATORY AIR RECOVERY is the one legitimate lifecycle/safety
            // action allowed outside funded Recon progress: a sortie that MUST physically return to
            // avoid being stranded/lost flies unconditionally, never gated on this pass's Mission ->
            // Funding -> Assignment -> Provisioning pipeline. Everything else (still-outbound,
            // still-observing continuation) is Strategic Recon Progress and MUST have won a fresh
            // ProvisionedMission this pass (plan.ReadyActorIds, below) — AirReconPlanner no longer
            // discovers/carries forward continuing actors on its own (see its header).
            //
            // Discover: airborne wings with a live ReconPatrolState that are NOT already bound to a
            // fresh ProvisionedMission this pass, whose lifecycle projection (the SAME read-only
            // projection ReconAirReservationPrepass/capacity sizing already uses — never a second
            // must-recover rule) resolves to Phase.Return. A wing that projects to anything else
            // (still Outbound/Turning/Hold) is NOT flown here — it makes no progress this turn unless
            // it won fresh funding.
            // A provisioned objective that became stale after Ground execution no longer grants
            // strategic progress, but it also must not suppress the independent recovery pass.
            var fundedThisPass = new HashSet<int>(plan.ReadyActorIds.Where(id =>
                !plan.ReadyMissionByActorId.TryGetValue(id, out ProvisionedMission mission)
                || !ObjectiveSatisfied(player, mission)));
            foreach (ArmyData air in ArmyRegistry.AllForOwner(player)
                         .Where(a => a != null && AviationRules.IsValidAirArmy(a)
                             && a.Controller != null && a.CurrentMovement > 0
                             && !AviationRules.IsOwnedAirfieldAt(a.Hex, player)
                             && !fundedThisPass.Contains(a.Id)
                             && ReconPatrolStateRegistry.TryGet(player, a.Id, out _))
                         .OrderBy(a => a.Id))
            {
                ReconAirSortieState projected = ReconAirReservationPrepass.ProjectScoringSortie(player, ctx, air);
                if (projected == null || projected.Phase != ReconAirPhase.Return)
                    continue; // not a mandatory recovery — unfunded continuing progress, skip this pass
                HexCoord? focus = ReconPatrolStateRegistry.TryGet(player, air.Id, out ReconPatrolState st)
                    ? st.StrategicAnchor : (HexCoord?)null;
                AiDebugLog.Write($"[AI][V2][Recon][Air][Recovery] actor=#{air.Id} has no strategic "
                    + "progress entitlement but must-recover — flying unconditionally (lifecycle safety)");
                yield return RunActor(player, root, ctx, snapshot, air, result, missionFocusHex: focus);
            }

            foreach (int id in plan.ReadyActorIds)
            {
                ArmyData air = Resolve(player, id);
                plan.ReadyMissionByActorId.TryGetValue(id, out ProvisionedMission pm);
                if (pm != null && air != null && ObjectiveSatisfied(player, pm))
                {
                    ExecutionResult stale = NewPerMissionResult(pm, air?.Hex ?? pm.ExecutionHex,
                        air != null ? air.Id : -1);
                    MarkSatisfiedNoOp(pm, stale);
                    stale.StateVersionAfter = V2StateVersion.Current;
                    perMissionResults?.Add(stale);
                    continue;
                }
                if (air == null || !AviationRules.IsValidAirArmy(air) || air.Controller == null
                    || air.CurrentMovement <= 0)
                {
                    if (pm != null && perMissionResults != null)
                    {
                        ExecutionResult missing = NewPerMissionResult(pm, pm.ExecutionHex, -1);
                        missing.StopReason = ExecutionStopReason.MoverLost;
                        missing.StateVersionAfter = V2StateVersion.Current;
                        perMissionResults.Add(missing);
                    }
                    continue;
                }

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
        // Compatibility adapter for the current terminal pass: launch atomically, then let the
        // same actor consume its remaining movement through the bounded airborne adapter.
        private static IEnumerator LaunchOne(AirLaunchPlan lp, PlayerSetupData player,
            PlayerRoot root, AiTurnContext ctx, WorldSnapshot snapshot,
            AirReconExecutionResult result, List<ExecutionResult> perMissionResults = null)
        {
            yield return LaunchOneCore(lp, player, root, ctx, snapshot, result,
                perMissionResults, continueAfterLaunch: true);
        }

        // One admitted launch task step. LaunchRoutine itself is intentionally indivisible:
        // formation, activation-Energy reservation and the first move either all settle or the
        // aircraft are returned to storage. No later airborne action is executed here.
        internal static IEnumerator LaunchOneStep(AirLaunchPlan lp, PlayerSetupData player,
            PlayerRoot root, AiTurnContext ctx, WorldSnapshot snapshot,
            AirReconExecutionResult result, List<ExecutionResult> perMissionResults = null)
        {
            result ??= new AirReconExecutionResult();
            if (lp == null || player == null || root == null || ctx?.Map == null)
            {
                result.StateVersionAfter = V2StateVersion.Current;
                yield break;
            }
            int apBefore = root.ActionPoints;
            int h0 = root != null ? root.GetResource(Game.Economy.ResourceType.Human) : 0;
            int e0 = root != null ? root.GetResource(Game.Economy.ResourceType.Energy) : 0;
            int m0 = root != null ? root.GetResource(Game.Economy.ResourceType.Materials) : 0;
            int t0 = root != null ? root.GetResource(Game.Economy.ResourceType.Tech) : 0;

            yield return LaunchOneCore(lp, player, root, ctx, snapshot, result,
                perMissionResults, continueAfterLaunch: false);

            result.ApSpent = Math.Max(0f,
                apBefore - (root != null ? root.ActionPoints : apBefore));
            int hSpent = root != null ? Math.Max(0, h0 - root.GetResource(Game.Economy.ResourceType.Human)) : 0;
            int eSpent = root != null ? Math.Max(0, e0 - root.GetResource(Game.Economy.ResourceType.Energy)) : 0;
            int mSpent = root != null ? Math.Max(0, m0 - root.GetResource(Game.Economy.ResourceType.Materials)) : 0;
            int tSpent = root != null ? Math.Max(0, t0 - root.GetResource(Game.Economy.ResourceType.Tech)) : 0;
            result.ResourcesSpent = (hSpent | eSpent | mSpent | tSpent) != 0
                ? new Game.Cards.ResourceCost
                    { human = hSpent, energy = eSpent, materials = mSpent, tech = tSpent }
                : null;
            result.StateVersionAfter = V2StateVersion.Current;
        }

        private static IEnumerator LaunchOneCore(AirLaunchPlan lp, PlayerSetupData player,
            PlayerRoot root, AiTurnContext ctx, WorldSnapshot snapshot,
            AirReconExecutionResult result, List<ExecutionResult> perMissionResults,
            bool continueAfterLaunch)
        {
            ProvisionedMission pm = lp?.Mission;
            V2ResourceStamp resourcesBefore = root != null ? AiV2Trace.Stamp(root) : default;
            void ReportNoLaunch(ExecutionStopReason why)
            {
                if (pm == null || perMissionResults == null) return;
                ExecutionResult er = NewPerMissionResult(pm, lp.AirfieldHex, -1);
                er.StopReason = why;
                er.ResourcesBefore = resourcesBefore;
                if (root != null) er.ResourcesAfter = AiV2Trace.Stamp(root);
                er.StateVersionAfter = V2StateVersion.Current;
                perMissionResults.Add(er);
            }

            if (lp?.Subset == null || lp.Subset.Count == 0)
            {
                ReportNoLaunch(ExecutionStopReason.TargetInvalidated);
                yield break;
            }
            // Ground execution earlier in this batch may already have refreshed this objective.
            // Revalidate before spending launch AP/Energy, matching TaskExecutor's live stale gate.
            if (pm != null && ObjectiveSatisfied(player, pm))
            {
                if (perMissionResults != null)
                {
                    ExecutionResult stale = NewPerMissionResult(pm, lp.AirfieldHex, -1);
                    MarkSatisfiedNoOp(pm, stale);
                    stale.StateVersionAfter = V2StateVersion.Current;
                    perMissionResults.Add(stale);
                }
                yield break;
            }
            if (!AiAirSortiePlanner.CanAffordLaunch(root, player, lp.Subset))
            {
                AiDebugLog.Write($"[AI][V2][Recon][Air][Storage] airfield=({lp.AirfieldHex.Q},{lp.AirfieldHex.R}) "
                    + "— planned launch no longer affordable (earlier sortie spent it); skip, no replan");
                ReportNoLaunch(ExecutionStopReason.MoverLost);
                yield break;
            }

            // No strategic Energy re-evaluation here. ProvisioningManager.AirSortieReservationAdmission
            // already decided (once, this turn) that this sortie is worth its Energy. Execution only
            // enforces LIVE HARD gates — CanAffordLaunch (above), and CanIssueMoveNow / AA / safe
            // return downstream in AirReconStepDirector.

            bool firstVisitedBefore = VisionSystem.IsVisited(player, lp.FirstStepHex);
            HashSet<int> enemyBeforeLaunch = KnownIds(AiMapMemory.AllKnownEnemySightings(player));
            HashSet<int> neutralBeforeLaunch = KnownIds(AiMapMemory.AllKnownNeutralSightings(player));
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
            var launchTrace = new AiMoveExecutionTrace();
            yield return AiAirSortiePlanner.LaunchRoutine(
                player, launchDecision, ctx, AirSortieKind.Recon, launchTrace);

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

            // RECON-AIR-06 (round 6 / Bug A) — seed StrategicAnchor from the Assignment-bound
            // mission target (pm.FocusHex), NOT the tactical first step. lp.FirstStepHex is where
            // the wing is headed THIS step, not what it is flying FOR; feeding it in here is what
            // let the anchor drift into a moving tactical waypoint. Mirrors Ground's pattern
            // (ReconGroundExecutor's `strategicAnchor` local = pm.FocusHex/ExecutionHex, re-affirmed
            // identically every call) — fall back to the tactical step only when no mission is
            // bound (should not normally happen for a fresh launch).
            ReconPatrolState assignment = ReconPatrolStateRegistry.GetOrCreate(player, launched.Id,
                lp.AirfieldHex, pm?.FocusHex ?? lp.FirstStepHex, lp.Mode, ctx.TurnNumber);
            ReconPatrolStateRegistry.MarkProgress(player, launched.Id, ctx.TurnNumber);
            ReconAirSortieState launchSortie = ReconAirSortieRegistry.GetOrCreate(player, launched.Id, lp.AirfieldHex);
            launchSortie.LaunchTurn = ctx.TurnNumber;
            launchSortie.RecordStep(launched.Hex);
            launchSortie.ArrivalStrikeCheckPending = true;
            launchSortie.BestOutboundStepScore = Math.Max(launchSortie.BestOutboundStepScore, lp.Score);
            AiReconIntelMemory.ObserveCurrentVisibility(player, ctx.TurnNumber);
            RecordDiscoveries(player, ctx.TurnNumber, launched.Id, enemyBeforeLaunch, neutralBeforeLaunch);
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
                perMission.ResourcesBefore = resourcesBefore;
            }

            if (continueAfterLaunch && launched.Controller != null && launched.CurrentMovement > 0
                && !AviationRules.IsOwnedAirfieldAt(launched.Hex, player))
                // RECON-AIR-05 — anchor further live replanning at the SAME Refresh target this
                // launch was bound to (lp.Mission.FocusHex), not a fresh pick.
                yield return RunActor(player, root, ctx, snapshot, launched, result,
                    arrivalStrikeCheckPending: true, missionFocusHex: pm?.FocusHex, perMissionResult: perMission);

            if (perMission != null)
            {
                perMission.ApSpent = Math.Max(0f, apBeforeLaunch - root.ActionPoints);
                if (!continueAfterLaunch)
                    perMission.StopReason = launchTrace.BattleOccurred
                        ? ExecutionStopReason.BattleStarted
                        : launchTrace.HexEventOccurred
                            ? ExecutionStopReason.HexEventStarted
                            : launched.CurrentMovement > 0
                                ? ExecutionStopReason.StepCompleted
                                : ExecutionStopReason.OutOfMovement;
                perMission.ResourcesAfter = AiV2Trace.Stamp(root);
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
                PlannedAtStateVersion = pm.PlannedAtStateVersion,
            };

        // RECON-AIR-06 — the same "is the bound objective satisfied" question Ground's
        // RefreshObjectiveSatisfied asks, reused verbatim here so Air's per-mission result carries
        // the SAME ReachedGoal/ObjectiveSatisfied semantics Ground's does.
        private static void FinalizePerMissionResult(PlayerSetupData player, ProvisionedMission pm, ExecutionResult er)
        {
            if (er == null)
                return;
            er.StateVersionAfter = V2StateVersion.Current;
            if (player == null || pm == null || er.ReachedGoal)
                return;
            bool satisfied = pm.ScoutKind == ScoutTargetKind.Surveil
                ? ScoutObjectiveEvaluator.IsSurveilSatisfiedLive(player, pm.FocusHex, pm.TrackedArmyId, pm.BaselineObservedTurn)
                : ScoutObjectiveEvaluator.IsRefreshSatisfiedLive(player, pm.FocusHex);
            if (satisfied)
            {
                er.ReachedGoal = true;
                er.DurableRoleContinues = pm.Mission?.FromDurableIntent == true
                    && pm.ScoutKind != ScoutTargetKind.Surveil;
            }
        }

        private static bool ObjectiveSatisfied(PlayerSetupData player, ProvisionedMission pm) =>
            pm != null && (pm.ScoutKind == ScoutTargetKind.Surveil
                ? ScoutObjectiveEvaluator.IsSurveilSatisfiedLive(
                    player, pm.FocusHex, pm.TrackedArmyId, pm.BaselineObservedTurn)
                : ScoutObjectiveEvaluator.IsRefreshSatisfiedLive(player, pm.FocusHex));

        private static void MarkSatisfiedNoOp(ProvisionedMission pm, ExecutionResult er)
        {
            er.ReachedGoal = true;
            er.StaleNoOp = true;
            er.StopReason = ExecutionStopReason.ReachedGoal;
            er.DurableRoleContinues = pm?.Mission?.FromDurableIntent == true
                && pm.ScoutKind != ScoutTargetKind.Surveil;
        }

        // Thin execute loop. Every decision comes from AirReconStepDirector; the executor only
        // resolves live liveness (lost / battle / landed / out of MP) and then issues the canonical
        // gameplay call the decision names.
        //
        // RECON-AIR-05/06 — `missionFocusHex` is forwarded to every PlanStep call so the tactical
        // planner's live replanning stays anchored at the bound target; `perMissionResult`, when
        // given, accumulates this actor's StepsMoved/FinalHex/StopReason for its ONE provisioned
        // mission this pass (a continuing wing with no fresh mission passes null — see Execute).
        // Compatibility adapter for the current terminal AirRecon pass. Every iteration delegates
        // to the same one-command core used by the mid-turn loop.
        private static IEnumerator RunActor(PlayerSetupData player, PlayerRoot root, AiTurnContext ctx,
            WorldSnapshot snapshot, ArmyData initial, AirReconExecutionResult result,
            bool arrivalStrikeCheckPending = false, HexCoord? missionFocusHex = null,
            ExecutionResult perMissionResult = null)
        {
            int armyId = initial.Id;
            int guard = Math.Max(4, initial.CurrentMovement + 5);
            var control = new ActorStepControl { MovedAny = arrivalStrikeCheckPending };
            if (arrivalStrikeCheckPending)
                ReconAirSortieRegistry.GetOrCreate(player, armyId, initial.Hex)
                    .ArrivalStrikeCheckPending = true;

            ExecutionStopReason stop = ExecutionStopReason.OutOfMovement;
            while (guard-- > 0)
            {
                control.ResetStep();
                yield return RunActorStepCore(player, root, ctx, snapshot, armyId, result,
                    missionFocusHex, perMissionResult, control);
                stop = control.StopReason;
                if (!control.CanContinue)
                    break;
            }

            FinishActorStep(player, armyId, perMissionResult, control, stop);
        }

        // One admitted airborne Recon task step. It executes at most one canonical stationary
        // strike OR one adjacent MoveArmyRoutine command. Local Hold/phase bookkeeping may also be
        // resolved, but this method never selects another mission or actor.
        internal static IEnumerator RunActorStep(PlayerSetupData player, PlayerRoot root,
            AiTurnContext ctx, WorldSnapshot snapshot, ArmyData initial,
            AirReconExecutionResult result, int apBefore, HexCoord? missionFocusHex = null,
            ExecutionResult perMissionResult = null, ActorStepControl control = null)
        {
            control ??= new ActorStepControl();
            control.ResetStep();
            if (initial == null)
            {
                control.StopReason = ExecutionStopReason.MoverLost;
                FinishActorStep(player, -1, perMissionResult, control, control.StopReason);
                yield break;
            }

            var stepResult = result ?? new AirReconExecutionResult();
            if (perMissionResult != null && root != null)
                perMissionResult.ResourcesBefore = AiV2Trace.Stamp(root);
            int h0 = root != null ? root.GetResource(Game.Economy.ResourceType.Human) : 0;
            int e0 = root != null ? root.GetResource(Game.Economy.ResourceType.Energy) : 0;
            int m0 = root != null ? root.GetResource(Game.Economy.ResourceType.Materials) : 0;
            int t0 = root != null ? root.GetResource(Game.Economy.ResourceType.Tech) : 0;

            yield return RunActorStepCore(player, root, ctx, snapshot, initial.Id,
                stepResult, missionFocusHex, perMissionResult, control);
            FinishActorStep(player, initial.Id, perMissionResult, control, control.StopReason);

            stepResult.ApSpent = Math.Max(0f,
                apBefore - (root != null ? root.ActionPoints : apBefore));
            int hSpent = root != null ? Math.Max(0, h0 - root.GetResource(Game.Economy.ResourceType.Human)) : 0;
            int eSpent = root != null ? Math.Max(0, e0 - root.GetResource(Game.Economy.ResourceType.Energy)) : 0;
            int mSpent = root != null ? Math.Max(0, m0 - root.GetResource(Game.Economy.ResourceType.Materials)) : 0;
            int tSpent = root != null ? Math.Max(0, t0 - root.GetResource(Game.Economy.ResourceType.Tech)) : 0;
            stepResult.ResourcesSpent = (hSpent | eSpent | mSpent | tSpent) != 0
                ? new Game.Cards.ResourceCost
                    { human = hSpent, energy = eSpent, materials = mSpent, tech = tSpent }
                : null;
            stepResult.StateVersionAfter = V2StateVersion.Current;

            if (perMissionResult != null)
            {
                perMissionResult.ApSpent = stepResult.ApSpent;
                if (root != null)
                    perMissionResult.ResourcesAfter = AiV2Trace.Stamp(root);
                FinalizePerMissionResult(player, perMissionResult.Source, perMissionResult);
            }
        }

        private static IEnumerator RunActorStepCore(PlayerSetupData player, PlayerRoot root,
            AiTurnContext ctx, WorldSnapshot snapshot, int armyId, AirReconExecutionResult result,
            HexCoord? missionFocusHex, ExecutionResult perMissionResult, ActorStepControl control)
        {
            ArmyData air = Resolve(player, armyId);
            if (air == null || !AviationRules.IsValidAirArmy(air) || air.Controller == null)
            {
                ReconPatrolStateRegistry.Retire(player, armyId, "air mover lost / invalid");
                ReconAirSortieRegistry.Retire(player, armyId);
                if (air != null) RemoveAirReconReservation(player, air);
                control.StopReason = ExecutionStopReason.MoverLost;
                yield break;
            }

            if (ctx?.Map == null)
            {
                control.StopReason = ExecutionStopReason.TargetInvalidated;
                yield break;
            }
            if (ctx.HexSelection != null && ctx.HexSelection.IsBattleActive)
            {
                control.StopReason = ExecutionStopReason.BattleStarted;
                yield break;
            }

            ReconAirSortieState sortie = ReconAirSortieRegistry.GetOrCreate(
                player, armyId, air.Hex);
            bool atAirfield = AviationRules.IsOwnedAirfieldAt(air.Hex, player);
            bool hasDeparted = control.MovedAny || sortie.LaunchTurn >= 0 || sortie.Trail.Count > 1;
            if (atAirfield && hasDeparted)
            {
                AiDebugLog.Write($"[AI][V2][Recon][Air] actor=#{armyId} phase=Landing at "
                    + $"({air.Hex.Q},{air.Hex.R}); sortie complete");
                ReconPatrolStateRegistry.Retire(player, armyId, "air recon landed");
                ReconAirSortieRegistry.Retire(player, armyId);
                RemoveAirReconReservation(player, air);
                control.StopReason = ExecutionStopReason.OutOfMovement;
                yield break;
            }
            if (air.CurrentMovement <= 0)
            {
                control.StopReason = ExecutionStopReason.OutOfMovement;
                yield break;
            }

            ReconAirSortieLifecycle.Observe(sortie, air, ctx, atAirfield);
            bool newTurn = ReconAirSortieLifecycle.BeginTurn(sortie, ctx.TurnNumber);
            bool arrivalStrikeCheck = sortie.ArrivalStrikeCheckPending;
            sortie.ArrivalStrikeCheckPending = false;
            AirReconStepDirector.StepDecision d = AirReconStepDirector.PlanStep(
                player, root, ctx, snapshot, air, sortie, newTurn,
                arrivalStrikeCheck, missionFocusHex);

            if (d.Kind == AirReconStepDirector.StepKind.Stop)
            {
                if (d.RetireAssignment)
                    ReconPatrolStateRegistry.Retire(player, armyId, d.Reason);
                if (d.RemoveReservation)
                    RemoveAirReconReservation(player, air);
                control.StopReason = ExecutionStopReason.NoSafeStep;
                yield break;
            }
            if (d.Kind == AirReconStepDirector.StepKind.HoldEndTurn)
            {
                control.StopReason = ExecutionStopReason.OutOfMovement;
                yield break;
            }

            if (d.Kind == AirReconStepDirector.StepKind.HoldReopen)
            {
                control.CommandAttempted = true;
                yield return ExecuteOpportunisticStrike(
                    player, ctx, air, sortie, result, perMissionResult);
                ArmyData afterHoldStrike = Resolve(player, armyId);
                if (afterHoldStrike == null || !AviationRules.IsValidAirArmy(afterHoldStrike)
                    || afterHoldStrike.Controller == null)
                {
                    ReconPatrolStateRegistry.Retire(player, armyId, "air mover lost / invalid");
                    ReconAirSortieRegistry.Retire(player, armyId);
                    control.StopReason = ExecutionStopReason.MoverLost;
                    yield break;
                }
                if (sortie.Phase == ReconAirPhase.Hold)
                    sortie.Phase = d.ResumePhase;
                ReconAirSortieLifecycle.Apply(sortie, d);
                ReconPatrolStateRegistry.MarkProgress(player, armyId, ctx.TurnNumber);
                control.CanContinue = true;
                control.StopReason = ExecutionStopReason.StepCompleted;
                yield break;
            }

            if (d.Kind == AirReconStepDirector.StepKind.Strike)
            {
                control.CommandAttempted = true;
                yield return ExecuteOpportunisticStrike(
                    player, ctx, air, sortie, result, perMissionResult);
                ReconPatrolStateRegistry.MarkProgress(player, armyId, ctx.TurnNumber);
                control.CanContinue = Resolve(player, armyId) != null;
                control.StopReason = control.CanContinue
                    ? ExecutionStopReason.StepCompleted
                    : ExecutionStopReason.MoverLost;
                yield break;
            }

            if (d.Kind == AirReconStepDirector.StepKind.ReturnStep)
            {
                AirSortie reservation = EnsureAirReconReservation(
                    player, air, d.LandingHex, outbound: false);
                if (reservation == null)
                {
                    AiDebugLog.Write($"[AI][V2][Recon][Air] actor=#{armyId} return blocked — another task owns aircraft");
                    control.StopReason = ExecutionStopReason.NoSafeStep;
                    yield break;
                }
                AiDebugLog.Write($"[AI][V2][Recon][Air] actor=#{armyId} phase=Return "
                    + $"({air.Hex.Q},{air.Hex.R})->({d.Step.Q},{d.Step.R}) "
                    + $"landing=({d.LandingHex.Q},{d.LandingHex.R}) informative={(d.AlsoInformative ? 1 : 0)} {d.Reason}");
                bool moved = false;
                control.CommandAttempted = true;
                yield return MoveOne(player, ctx, air, d.Step,
                    "V2 Air Recon — safe return", () => moved = true);
                if (!moved)
                {
                    control.StopReason = ExecutionStopReason.MoveRejected;
                    yield break;
                }
                V2StateVersion.Bump();
                result.RecordMove();
                control.MovedAny = true;
                if (perMissionResult != null) perMissionResult.StepsMoved++;
                ReconAirSortieLifecycle.Apply(sortie, d);
                ArmyData afterReturn = Resolve(player, armyId);
                if (afterReturn != null) sortie.RecordStep(afterReturn.Hex);
                sortie.ArrivalStrikeCheckPending = true;
                ReconPatrolStateRegistry.MarkProgress(player, armyId, ctx.TurnNumber);
                control.CanContinue = true;
                control.StopReason = ExecutionStopReason.StepCompleted;
                yield break;
            }

            ReconPatrolState assignment = ReconPatrolStateRegistry.GetOrCreate(
                player, armyId, air.Hex, missionFocusHex ?? d.Step, d.Mode, ctx.TurnNumber);
            AirSortie reservationTask = EnsureAirReconReservation(
                player, air, d.LandingHex, outbound: true, target: d.Step);
            if (reservationTask == null)
            {
                AiDebugLog.Write($"[AI][V2][Recon][Air] actor=#{armyId} step blocked — another task owns aircraft");
                control.StopReason = ExecutionStopReason.NoSafeStep;
                yield break;
            }

            bool stepMoved = false;
            control.CommandAttempted = true;
            yield return MoveOne(player, ctx, air, d.Step,
                $"V2 Air Recon — {assignment.Mode} {sortie.Phase} one-step live replan",
                () => stepMoved = true);
            if (!stepMoved)
            {
                control.StopReason = ExecutionStopReason.MoveRejected;
                yield break;
            }

            V2StateVersion.Bump();
            result.RecordMove();
            control.MovedAny = true;
            if (perMissionResult != null) perMissionResult.StepsMoved++;
            ArmyData afterStep = Resolve(player, armyId);
            if (afterStep != null) sortie.RecordStep(afterStep.Hex);
            ReconAirSortieLifecycle.Apply(sortie, d);
            sortie.ArrivalStrikeCheckPending = true;
            if (d.PivotToReturnAfterMove)
                AiDebugLog.Write($"[AI][V2][Recon][Air] actor=#{armyId} phase=Turning->Return pivot step taken");
            ReconPatrolStateRegistry.MarkProgress(player, armyId, ctx.TurnNumber);
            control.CanContinue = true;
            control.StopReason = ExecutionStopReason.StepCompleted;
        }

        private static void FinishActorStep(PlayerSetupData player, int armyId,
            ExecutionResult perMissionResult, ActorStepControl control,
            ExecutionStopReason stop)
        {
            ArmyData settled = Resolve(player, armyId);
            if (!control.MovedAny && settled != null
                && AviationRules.IsOwnedAirfieldAt(settled.Hex, player))
                ReconAirSortieRegistry.Retire(player, armyId);

            if (perMissionResult != null)
            {
                perMissionResult.FinalHex = settled?.Hex ?? perMissionResult.FinalHex;
                perMissionResult.StopReason = stop;
                perMissionResult.StateVersionAfter = V2StateVersion.Current;
            }
        }

        // §46 — EXECUTION of an opportunistic air strike the director already judged favourable and
        // safe. The executor re-guards CanStrikeAtCurrentHex (live), resolves the AviationActions
        // call, refreshes intel, then hands the post-strike phase decision back to the director.
        private static IEnumerator ExecuteOpportunisticStrike(PlayerSetupData player, AiTurnContext ctx,
            ArmyData air, ReconAirSortieState sortie, AirReconExecutionResult passResult,
            ExecutionResult perMissionResult = null)
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
            HashSet<int> enemyBefore = KnownIds(AiMapMemory.AllKnownEnemySightings(player));
            HashSet<int> neutralBefore = KnownIds(AiMapMemory.AllKnownNeutralSightings(player));
            var strike = new AviationCombatPresenter.AirStrikeResult();
            yield return AviationActions.ResolveStationaryStrike(presenter, air, strike);

            if (strike.Attacked)
            {
                V2StateVersion.Bump();
                passResult.RecordStrike();
                if (perMissionResult != null)
                    perMissionResult.CombatChanged = true;
            }

            ArmyData afterStrike = Resolve(player, air.Id);
            AiReconIntelMemory.ObserveCurrentVisibility(player, ctx.TurnNumber);
            RecordDiscoveries(player, ctx.TurnNumber, air.Id, enemyBefore, neutralBefore);
            AirReconStepDirector.ResolveAfterStrike(player, ctx, afterStrike, sortie, strike.Attacked);
        }

        private static IEnumerator MoveOne(PlayerSetupData player, AiTurnContext ctx, ArmyData air,
            HexCoord next, string reason, Action onMoved)
        {
            HexCoord before = air.Hex;
            bool visitedBefore = VisionSystem.IsVisited(player, next);
            HashSet<int> enemyBefore = KnownIds(AiMapMemory.AllKnownEnemySightings(player));
            HashSet<int> neutralBefore = KnownIds(AiMapMemory.AllKnownNeutralSightings(player));
            var decision = AiDecision.Move(air, next, reason, 0f);
            var trace = new AiMoveExecutionTrace();
            yield return AiTurnController.MoveArmyRoutine(player, decision, ctx, trace);

            ArmyData live = Resolve(player, air.Id);
            HexCoord after = live != null ? live.Hex : trace.EndHex;
            if (!after.Equals(before))
                onMoved?.Invoke();

            AiReconIntelMemory.ObserveCurrentVisibility(player, ctx.TurnNumber);
            RecordDiscoveries(player, ctx.TurnNumber, air.Id, enemyBefore, neutralBefore);
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

        private static HashSet<int> KnownIds(IEnumerable<AiMapMemory.KnownEnemySighting> sightings)
        {
            var ids = new HashSet<int>();
            foreach (AiMapMemory.KnownEnemySighting sighting in sightings)
                ids.Add(sighting.ArmyId);
            return ids;
        }

        private static void RecordDiscoveries(PlayerSetupData player, int turn, int actorId,
            HashSet<int> enemyBefore, HashSet<int> neutralBefore)
        {
            int[] enemies = KnownIds(AiMapMemory.AllKnownEnemySightings(player))
                .Where(id => enemyBefore == null || !enemyBefore.Contains(id)).ToArray();
            int[] neutrals = KnownIds(AiMapMemory.AllKnownNeutralSightings(player))
                .Where(id => neutralBefore == null || !neutralBefore.Contains(id)).ToArray();
            if (enemies.Length > 0)
            {
                StrategicInterruptRegistry.MarkDiscovery(player, turn, enemies);
                AiDebugLog.Write($"[AI][V2][Recon][Air][Discovery] actor=#{actorId} "
                    + $"enemy=[{string.Join(",", enemies)}]");
            }
            if (neutrals.Length > 0)
            {
                StrategicInterruptRegistry.MarkDiscovery(player, turn, neutrals);
                AiDebugLog.Write($"[AI][V2][Recon][Air][Discovery] actor=#{actorId} "
                    + $"neutral=[{string.Join(",", neutrals)}]");
            }
        }
    }
}
