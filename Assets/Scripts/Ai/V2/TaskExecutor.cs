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
    // ===========================================================================================
    //  TASK EXECUTOR  (Strategy V2 build-order step 6 — Explore 6a + Surveil 6b)
    // ===========================================================================================
    //  Runs each ProvisionedMission on the real map through the SAME movement path V1 and the
    //  human use (AiTurnController.MoveArmyRoutine -> HexSelectionController.IssueMoveOrder). No V2
    //  AiTaskRegistry entry, no persistent task object — the ProvisionedMission plus the live
    //  world IS the state, exactly as the stateless-recon design intends.
    //
    //  PER-HEX LOOP  (Q3 — "uses the whole movement budget, one hex per iteration")
    //    pick 1 safe next hex toward ExecutionHex -> move it -> wait for MoveArmyRoutine to fully
    //    settle (vision, stealth, contact, event, any chained battle) -> re-read live state ->
    //    decide whether to continue. Reproduces V1's Decide -> move -> Decide cadence, one mover.
    //
    //  EXPLORE vs SURVEIL — what "done" means
    //    Explore  — ExecutionHex == FocusHex. Done = reached / visited it.
    //    Surveil  — ExecutionHex is a safe VANTAGE; the scout NEVER steps onto FocusHex. Done is
    //               INFORMATION: FocusHex visible again, OR TrackedArmyId re-sighted anywhere with
    //               SeenTurn > BaselineObservedTurn. Physically reaching the vantage without that
    //               observation is NOT success (ObservationUnavailable — an invariant canary, not
    //               a cue to walk to FocusHex). The executor never picks a different vantage or
    //               re-targets the tracked enemy.
    //
    //  A STEP ENDS THE MISSION FOR THIS TURN (never a strategic re-target here):
    //    ReachedGoal            — Explore: reached/visited ExecutionHex. Surveil: observation made.
    //    OutOfMovement          — no movement points left
    //    NoSafeStep             — FindNextSafeStep == null this instant (retry next turn)
    //    EnemyDiscovered        — a new (different) known non-neutral sighting appeared
    //    NeutralDiscovered      — a new known neutral sighting appeared
    //    BattleStarted          — a contact pulled the mover into a fight
    //    HexEventStarted        — a clean Hex Event resolved on the step
    //    MoverLost              — the army is gone / no longer ours / no longer a solo Recce
    //    TargetInvalidated      — ExecutionHex now holds a known army
    //    MoveRejected           — an issued order made zero progress (loop-guard: never spin)
    //    ObservationUnavailable — Surveil: reached the vantage, still cannot observe FocusHex
    //
    //  EnemyDiscovered / NeutralDiscovered / OutOfMovement are PRODUCTIVE stops, not failures —
    //  the scout brought back information. Nothing downstream (CommitmentLayer / Manager, step 7+)
    //  may read them as a provisioning/mission failure. (ExecutionOutcome { Completed /
    //  ProductiveStop / Blocked / Failed } is a step-7 concept; the mapping is fixed now — see the
    //  pipeline design record.)
    // ===========================================================================================
    public enum ExecutionStopReason
    {
        ReachedGoal,
        OutOfMovement,
        NoSafeStep,
        EnemyDiscovered,
        NeutralDiscovered,
        BattleStarted,
        HexEventStarted,          // a clean Hex Event resolved on the step (explored or skipped) — via AiMoveExecutionTrace
        MoverLost,
        TargetInvalidated,
        MoveRejected,
        RequiredStealthUnavailable, // a stealth-Required mission's mover could not enter stealth before its first move
        ObservationUnavailable,     // Surveil: mover reached the vantage but FocusHex is still not observable — invariant canary
    }

    public sealed class ExecutionResult
    {
        public StableMissionKey Key;
        public HexCoord StartHex;
        public HexCoord FinalHex;
        public int StepsMoved;
        public bool ReachedGoal;
        public float ApSpent;
        public ExecutionStopReason StopReason;

        // A stealth STATE change happened this turn (Required entry before the first move, or a
        // voluntary mid-move entry) — a real change of the unit's readiness, not just spent AP.
        // Step 7's "earned continuation" test is StepsMoved > 0 || EnteredStealth (never ApSpent).
        public bool EnteredStealth;
    }

    internal static class TaskExecutor
    {
        public static IEnumerator Execute(PlayerSetupData player, PlayerRoot root, AiTurnContext ctx,
            IReadOnlyList<ProvisionedMission> provisioned, List<ExecutionResult> results)
        {
            if (provisioned == null || provisioned.Count == 0 || ctx?.Map == null)
                yield break;

            foreach (ProvisionedMission pm in provisioned)
            {
                var result = new ExecutionResult { Key = pm.Key };

                ArmyData army = Resolve(player, pm.MoverArmyId);
                if (army == null)
                {
                    result.StartHex = pm.ExecutionHex;
                    result.FinalHex = pm.ExecutionHex;
                    result.StopReason = ExecutionStopReason.MoverLost;
                    results.Add(result);
                    AiDebugLog.Write($"[AI][V2] exec {pm.Key} — mover #{pm.MoverArmyId} gone before first step");
                    continue;
                }

                result.StartHex = army.Hex;
                result.FinalHex = army.Hex;
                int apBefore = root != null ? root.ActionPoints : 0;

                // Objective already met by an EARLIER mission this turn (pipeline provisions all,
                // then executes in order)? Then this Surveil does nothing at all — no movement, no
                // stealth AP. This check MUST precede every execution mutation.
                if (pm.ScoutKind == ScoutTargetKind.Surveil && IsSurveilSatisfied(player, pm))
                {
                    result.ReachedGoal = true;
                    result.StopReason = ExecutionStopReason.ReachedGoal;
                    result.ApSpent = 0f;
                    results.Add(result);
                    AiDebugLog.Write($"[AI][V2] exec {pm.Key} — surveil already satisfied before start — no movement, 0 AP");
                    continue;
                }

                // A stealth-Required mission (ProvisionedMission.StealthApReserved) enters stealth
                // BEFORE its first move, unconditionally — provisioning already reserved the 1 AP
                // and the gameplay layer can only enter stealth while the mover is not yet
                // activated. If it can't be delivered (should be impossible — the mover was
                // eligibility-checked for exactly this), abort rather than send a Required mission
                // out visible.
                if (pm.StealthApReserved)
                {
                    if (!TryEnterRequiredStealth(root, army, out bool enteredStealth))
                    {
                        result.FinalHex = army.Hex;
                        result.StopReason = ExecutionStopReason.RequiredStealthUnavailable;
                        result.ApSpent = Mathf.Max(0f, apBefore - (root != null ? root.ActionPoints : apBefore));
                        results.Add(result);
                        AiDebugLog.Write($"[AI][V2] exec {pm.Key} — WARN mover #{pm.MoverArmyId} could not enter "
                            + "required stealth; mission aborted for this turn");
                        continue;
                    }
                    result.EnteredStealth |= enteredStealth;
                }

                int maxIterations = army.CurrentMovement + 1; // loop guard (NOT a cross-turn stall watchdog)
                int iterations = 0;
                ExecutionStopReason stop = ExecutionStopReason.OutOfMovement;

                // Map-wide known-sighting ids (NOT radius-bounded to the mover) so "new" means
                // "honestly discovered this step", never "an already-known army we walked closer to".
                HashSet<int> knownEnemyIds = KnownIds(AiMapMemory.AllKnownEnemySightings(player));
                HashSet<int> knownNeutralIds = KnownIds(AiMapMemory.AllKnownNeutralSightings(player));

                while (true)
                {
                    if (++iterations > maxIterations)
                    {
                        stop = ExecutionStopReason.MoveRejected;
                        break;
                    }

                    army = Resolve(player, pm.MoverArmyId);
                    if (army == null || army.Owner != player) { stop = ExecutionStopReason.MoverLost; break; }
                    if (!AiArmyRoles.IsSoloRecce(army)) { stop = ExecutionStopReason.MoverLost; break; }
                    if (ctx.HexSelection != null && ctx.HexSelection.IsBattleActive) { stop = ExecutionStopReason.BattleStarted; break; }

                    // Objective already met (by us last iteration, or by another mission before we
                    // moved)? BEFORE the CurrentMovement check — a spent mover must not turn an
                    // already-achieved observation into OutOfMovement.
                    if (pm.ScoutKind == ScoutTargetKind.Surveil)
                    {
                        if (IsSurveilSatisfied(player, pm))
                        {
                            result.ReachedGoal = true;
                            stop = ExecutionStopReason.ReachedGoal;
                            break;
                        }
                        if (army.Hex.Equals(pm.ExecutionHex))
                        {
                            // Reached the vantage but still can't observe FocusHex — the vantage
                            // was wrong (vision changed / deeper fog). Do NOT walk to FocusHex.
                            stop = ExecutionStopReason.ObservationUnavailable;
                            break;
                        }
                        if (ScoutExecutionSafety.VantageBlockedNow(player, pm.ExecutionHex, ctx.TurnNumber))
                        {
                            // A CURRENT force / foreign building is now on the vantage — a stale
                            // enemy position never trips this (that is the mission). Same rule
                            // SurveilVantageSelector applied against the snapshot.
                            stop = ExecutionStopReason.TargetInvalidated;
                            break;
                        }
                    }
                    else if (army.Hex.Equals(pm.ExecutionHex) || VisionSystem.IsVisited(player, pm.ExecutionHex))
                    {
                        result.ReachedGoal = true;
                        stop = ExecutionStopReason.ReachedGoal;
                        break;
                    }
                    else if (AiMapMemory.KnownEnemySightingAt(player, pm.ExecutionHex).HasValue)
                    {
                        stop = ExecutionStopReason.TargetInvalidated;
                        break;
                    }

                    if (army.CurrentMovement <= 0) { stop = ExecutionStopReason.OutOfMovement; break; }

                    HexCoord? next = VisitHexTask.FindNextSafeStep(ctx.Map, army, pm.ExecutionHex);
                    if (next == null) { stop = ExecutionStopReason.NoSafeStep; break; }

                    HexCoord before = army.Hex;
                    var decision = AiDecision.Move(army, next.Value,
                        $"V2 recon — {pm.ScoutKind} toward ({pm.ExecutionHex.Q},{pm.ExecutionHex.R})",
                        null, 0f, AiTaskCategory.Reconnaissance);
                    var trace = new AiMoveExecutionTrace();
                    yield return AiTurnController.MoveArmyRoutine(player, decision, ctx, trace);
                    result.EnteredStealth |= trace.EnteredStealthThisStep;

                    // (1) Record the physical move FIRST, before classifying why we stop. The
                    //     mover may have died in a fight this step — trace.EndHex was captured on
                    //     arrival, before the battle, so the step still counts and FinalHex is
                    //     honest even when Resolve() now returns null.
                    army = Resolve(player, pm.MoverArmyId);
                    HexCoord endHex = army != null ? army.Hex : trace.EndHex;
                    bool moved = !endHex.Equals(before);
                    if (moved)
                        result.StepsMoved++;
                    result.FinalHex = endHex;

                    // (2) Now the stop reason, in priority order:
                    //   MoverLost -> BattleStarted -> HexEventStarted -> [Surveil: objective met]
                    //   -> MoveRejected -> EnemyDiscovered -> NeutralDiscovered.
                    if (army == null) { stop = ExecutionStopReason.MoverLost; break; }
                    if (trace.BattleOccurred) { stop = ExecutionStopReason.BattleStarted; break; }
                    if (trace.HexEventOccurred) { stop = ExecutionStopReason.HexEventStarted; break; }

                    // Surveil early success outranks both a zero-progress order and generic
                    // discovery: re-sighting the tracked army (even at a different hex) IS the win
                    // (D9), not an "EnemyDiscovered" side effect.
                    if (pm.ScoutKind == ScoutTargetKind.Surveil && IsSurveilSatisfied(player, pm))
                    {
                        result.ReachedGoal = true;
                        stop = ExecutionStopReason.ReachedGoal;
                        break;
                    }

                    if (!moved)
                    {
                        // the order made zero progress this instant — never re-issue the same one
                        stop = ExecutionStopReason.MoveRejected;
                        break;
                    }

                    // A step that revealed a previously-unknown army ends the turn — a non-neutral
                    // one outranks a neutral one. Both are PRODUCTIVE stops (recon delivered). A
                    // re-sighting of the TRACKED Surveil army can look "new" here if its stale
                    // sighting had already aged out of V1 memory — but the IsSurveilSatisfied check
                    // above runs first and claims it as ReachedGoal, so this diff only ever labels
                    // a genuinely different army.
                    HashSet<int> enemyNow = KnownIds(AiMapMemory.AllKnownEnemySightings(player));
                    HashSet<int> neutralNow = KnownIds(AiMapMemory.AllKnownNeutralSightings(player));
                    bool newEnemy = enemyNow.Any(id => !knownEnemyIds.Contains(id));
                    bool newNeutral = neutralNow.Any(id => !knownNeutralIds.Contains(id));
                    knownEnemyIds = enemyNow;
                    knownNeutralIds = neutralNow;
                    if (newEnemy) { stop = ExecutionStopReason.EnemyDiscovered; break; }
                    if (newNeutral) { stop = ExecutionStopReason.NeutralDiscovered; break; }
                }

                result.FinalHex = army?.Hex ?? result.FinalHex;
                result.StopReason = stop;
                result.ApSpent = Mathf.Max(0f, apBefore - (root != null ? root.ActionPoints : apBefore));
                results.Add(result);

                AiDebugLog.Write($"[AI][V2] exec {pm.Key} — ({result.StartHex.Q},{result.StartHex.R})→"
                    + $"({result.FinalHex.Q},{result.FinalHex.R}) steps {result.StepsMoved} "
                    + $"ap −{result.ApSpent.ToString("0.#", CultureInfo.InvariantCulture)} stop {stop}"
                    + (result.ReachedGoal ? " (goal)" : ""));
            }
        }

        private static ArmyData Resolve(PlayerSetupData player, int armyId) =>
            ArmyRegistry.AllForOwner(player).FirstOrDefault(a => a.Id == armyId);

        // Surveil is an INFORMATION objective: satisfied the moment FocusHex is visible again, or
        // the tracked army is honestly re-sighted ANYWHERE with SeenTurn past the baseline. The
        // rule lives in ScoutObjectiveEvaluator (the single completion/validity home, step 7) —
        // provisioning and the continuity layer call the same primitive. Honest memory only.
        private static bool IsSurveilSatisfied(PlayerSetupData player, ProvisionedMission pm) =>
            pm.ScoutKind == ScoutTargetKind.Surveil
            && ScoutObjectiveEvaluator.IsSurveilSatisfiedLive(player, pm.FocusHex, pm.TrackedArmyId, pm.BaselineObservedTurn);

        // Mirror of MoveArmyRoutine's own voluntary-stealth entry (1 AP per member, solo Recce =
        // 1), but STRICT: a stealth-Required V2 mission calls this before its first move instead
        // of relying on the optional "is this step risky" policy. True if the mover ends up hidden
        // (already was, or entered now); false only if the state that provisioning verified has
        // since changed.
        private static bool TryEnterRequiredStealth(PlayerRoot root, ArmyData army, out bool entered)
        {
            entered = false;
            if (army == null || army.Members.Count == 0)
                return false;
            if (army.Members.Any(m => m.IsHidden))
                return true; // already hidden — Required satisfied, no state change
            if (army.HasActivatedThisTurn)
                return false;
            var scout = army.Members[0];
            if (!StealthSystem.CanEnterStealth(scout))
                return false;
            if (root == null || !root.CanSpendActionPoints(army.ActivationApCost + 1))
                return false;
            root.SpendActionPoints(1);
            StealthSystem.EnterStealth(scout);
            entered = true;
            return true;
        }

        private static HashSet<int> KnownIds(IEnumerable<AiMapMemory.KnownEnemySighting> sightings)
        {
            var set = new HashSet<int>();
            foreach (AiMapMemory.KnownEnemySighting s in sightings)
                set.Add(s.ArmyId);
            return set;
        }
    }
}
