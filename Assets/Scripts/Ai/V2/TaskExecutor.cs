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
    //  TASK EXECUTOR  (Strategy V2 build-order step 6a — Explore end to end)
    // ===========================================================================================
    //  Runs each ProvisionedMission on the real map through the SAME movement path V1 and the
    //  human use (AiTurnController.MoveArmyRoutine -> HexSelectionController.IssueMoveOrder). No V2
    //  AiTaskRegistry entry, no persistent task object — the ProvisionedMission plus the live
    //  world IS the state, exactly as the stateless-recon design intends.
    //
    //  PER-HEX LOOP  (Q3 — "uses the whole movement budget, one hex per iteration")
    //    pick 1 safe next hex -> move it -> wait for MoveArmyRoutine to fully settle (vision,
    //    stealth, contact, event, any chained battle) -> re-read live state -> decide whether to
    //    continue. This reproduces V1's Decide -> move -> Decide cadence, scoped to one mover.
    //
    //  A STEP ENDS THE MISSION FOR THIS TURN (never a strategic re-target here — the frontier is
    //  stale the moment the mover moves; picking a new FocusHex is MissionLayer's job next turn):
    //    ReachedGoal        — arrived at ExecutionHex, or it got visited underneath us
    //    OutOfMovement      — no movement points left
    //    NoSafeStep         — FindNextSafeStep == null this instant (retry next turn)
    //    EnemyDiscovered    — a new known non-neutral sighting appeared near the mover
    //    NeutralDiscovered  — a new known neutral sighting appeared near the mover
    //    BattleStarted      — a contact pulled the mover into a fight
    //    MoverLost          — the army is gone / no longer ours / no longer a solo Recce
    //    TargetInvalidated  — ExecutionHex now holds a known army
    //    MoveRejected       — an issued order made zero progress (loop-guard: never spin)
    //
    //  EnemyDiscovered / NeutralDiscovered / OutOfMovement are PRODUCTIVE stops, not failures —
    //  the scout brought back information, it just stopped short of ExecutionHex. Nothing
    //  downstream (CommitmentLayer / Manager, step 7+) may read them as a provisioning/mission
    //  failure. (ExecutionOutcome { Completed / ProductiveStop / Blocked / Failed } is a step-7
    //  concept; the mapping is fixed now — see the pipeline design record.)
    // ===========================================================================================
    public enum ExecutionStopReason
    {
        ReachedGoal,
        OutOfMovement,
        NoSafeStep,
        EnemyDiscovered,
        NeutralDiscovered,
        BattleStarted,
        HexEventStarted,   // reserved — MoveArmyRoutine already settles events before it returns in 6a
        MoverLost,
        TargetInvalidated,
        MoveRejected,
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

                // A stealth-Required mission (ProvisionedMission.StealthApReserved) enters stealth
                // BEFORE its first move, unconditionally — provisioning already reserved the 1 AP
                // and the gameplay layer can only enter stealth while the mover is not yet
                // activated. If it can't be delivered (should be impossible — the mover was
                // eligibility-checked for exactly this), abort rather than send a Required mission
                // out visible.
                if (pm.StealthApReserved && !TryEnterRequiredStealth(root, army))
                {
                    result.FinalHex = army.Hex;
                    result.StopReason = ExecutionStopReason.MoverLost;
                    result.ApSpent = Mathf.Max(0f, apBefore - (root != null ? root.ActionPoints : apBefore));
                    results.Add(result);
                    AiDebugLog.Write($"[AI][V2] exec {pm.Key} — WARN mover #{pm.MoverArmyId} could not enter "
                        + "required stealth; mission aborted for this turn");
                    continue;
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
                    if (army.CurrentMovement <= 0) { stop = ExecutionStopReason.OutOfMovement; break; }

                    if (army.Hex.Equals(pm.ExecutionHex) || VisionSystem.IsVisited(player, pm.ExecutionHex))
                    {
                        result.ReachedGoal = true;
                        stop = ExecutionStopReason.ReachedGoal;
                        break;
                    }
                    if (AiMapMemory.KnownEnemySightingAt(player, pm.ExecutionHex).HasValue)
                    {
                        stop = ExecutionStopReason.TargetInvalidated;
                        break;
                    }

                    HexCoord? next = VisitHexTask.FindNextSafeStep(ctx.Map, army, pm.ExecutionHex);
                    if (next == null) { stop = ExecutionStopReason.NoSafeStep; break; }

                    HexCoord before = army.Hex;
                    var decision = AiDecision.Move(army, next.Value,
                        $"V2 recon — {pm.Kind} toward ({pm.ExecutionHex.Q},{pm.ExecutionHex.R})",
                        null, 0f, AiTaskCategory.Reconnaissance);
                    var trace = new AiMoveExecutionTrace();
                    yield return AiTurnController.MoveArmyRoutine(player, decision, ctx, trace);

                    army = Resolve(player, pm.MoverArmyId);
                    if (army == null) { stop = ExecutionStopReason.MoverLost; break; }
                    result.FinalHex = army.Hex;

                    // A fight this step ends the mission for the turn — read it from the trace, NOT
                    // from IsBattleActive (MoveArmyRoutine only returns once that has gone false
                    // again, and an AI mover's dead opponent + refreshed memory can hide the
                    // contact from the sighting diff below).
                    if (trace.BattleOccurred) { stop = ExecutionStopReason.BattleStarted; break; }

                    if (army.Hex.Equals(before))
                    {
                        // the order made zero progress this instant — never re-issue the same one
                        stop = ExecutionStopReason.MoveRejected;
                        break;
                    }
                    result.StepsMoved++;

                    // A step that revealed a previously-unknown army ends the turn — a non-neutral
                    // one outranks a neutral one. Both are PRODUCTIVE stops (recon delivered).
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

        // Mirror of MoveArmyRoutine's own voluntary-stealth entry (1 AP per member, solo Recce =
        // 1), but STRICT: a stealth-Required V2 mission calls this before its first move instead
        // of relying on the optional "is this step risky" policy. True if the mover ends up hidden
        // (already was, or entered now); false only if the state that provisioning verified has
        // since changed.
        private static bool TryEnterRequiredStealth(PlayerRoot root, ArmyData army)
        {
            if (army == null || army.Members.Count == 0)
                return false;
            if (army.Members.Any(m => m.IsHidden))
                return true; // already hidden — Required satisfied, nothing to spend
            if (army.HasActivatedThisTurn)
                return false;
            var scout = army.Members[0];
            if (!StealthSystem.CanEnterStealth(scout))
                return false;
            if (root == null || !root.CanSpendActionPoints(army.ActivationApCost + 1))
                return false;
            root.SpendActionPoints(1);
            StealthSystem.EnterStealth(scout);
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
