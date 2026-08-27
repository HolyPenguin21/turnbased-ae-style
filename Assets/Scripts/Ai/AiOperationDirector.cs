using System.Collections.Generic;
using System.Linq;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using UnityEngine;

namespace Game.Ai
{
    // Operations layer (2026-08-27, project owner's own redesign) — sits between the strategic
    // layer (AiStrategyDirector, "what do I want") and the per-step orchestrator (AiTurnController.
    // Decide, "what do I do this step"). An AiOperation is a multi-turn campaign with one objective,
    // coordinated across several assets, that PERSISTS across turns and drives the lower layers
    // until it completes or aborts — the thing that makes the AI read as intent-driven ("it's
    // pushing my east base") rather than a swarm of independently-sensible armies.
    //
    // v1 scope: Offensive only. It doesn't create armies or move code of its own — it ADOPTS the
    // player's existing raid task (AiTaskKind.RaidWeakerArmy), pins it to a strategic objective (a
    // known enemy building, else a known enemy army's hex), shields it from AiAggressionPlanner's
    // own retarget/stall watchdogs (which would otherwise wander it off to the nearest soft
    // neutral or cancel it), and marches it in with a single advance directive plus the air
    // support AiAviationSupport already scores for a raid. It ends when the objective resolves,
    // when home comes under siege, or when its deadline runs out — at which point the raid task is
    // handed straight back to the ordinary planners (OperationId cleared).
    //
    // DefensiveConsolidation is a v2 stub — the Defence axis plus the existing DefendCitadel /
    // TryDefencePreemptCandidates already cover "pull forces home under threat"; a second
    // defensive coordinator on top is risk without much added value right now.
    public enum AiOperationType { Offensive, DefensiveConsolidation }

    // Re-derived from live map state every turn (AiOperationPlanner.Assess), never trusted stale —
    // same "непрерывная переоценка" rule the AiTask planners already follow. Stored only for the
    // status log.
    public enum AiOperationPhase { Forming, Advancing, Engaging, Consolidating, Withdrawing }

    public sealed class AiOperation
    {
        public int Id;
        public AiOperationType Type;
        public HexCoord Objective;
        public AiOperationPhase Phase = AiOperationPhase.Forming;
        public int StartedTurn;
        public int DeadlineTurn;
        // The raid task this operation has adopted (Offensive) — null until one exists to adopt.
        public AiTask StrikeTask;
        // First turn the adopted strike force read as unable to realistically take the objective
        // (-1 = not currently hopeless). AiConfig.operationHopelessTurns of it running aborts the
        // operation early rather than waiting out the full deadline.
        public int HopelessSince = -1;
    }

    internal static class AiOperationRegistry
    {
        private static readonly Dictionary<PlayerSetupData, List<AiOperation>> ByPlayer =
            new Dictionary<PlayerSetupData, List<AiOperation>>();
        // When this player's last Offensive operation ended — TryStartOffensive holds off for
        // AiConfig.operationCooldownTurns after, so a campaign that fizzles doesn't respawn the
        // very next turn (2026-08-27 log audit — five 1-2 turn Offensive ops back to back).
        private static readonly Dictionary<PlayerSetupData, int> LastEndedTurn =
            new Dictionary<PlayerSetupData, int>();
        private static int _nextId = 1;

        public static List<AiOperation> For(PlayerSetupData player)
        {
            if (!ByPlayer.TryGetValue(player, out List<AiOperation> list))
                ByPlayer[player] = list = new List<AiOperation>();
            return list;
        }

        public static AiOperation Create(PlayerSetupData player, AiOperationType type, HexCoord objective, int turn, int deadline)
        {
            var op = new AiOperation
            {
                Id = _nextId++,
                Type = type,
                Objective = objective,
                StartedTurn = turn,
                DeadlineTurn = turn + deadline,
            };
            For(player).Add(op);
            return op;
        }

        public static void Remove(PlayerSetupData player, AiOperation op, int endedTurn)
        {
            For(player).Remove(op);
            LastEndedTurn[player] = endedTurn;
        }

        public static int LastEnded(PlayerSetupData player) =>
            LastEndedTurn.TryGetValue(player, out int t) ? t : int.MinValue / 2;

        public static void Clear()
        {
            ByPlayer.Clear();
            LastEndedTurn.Clear();
        }
    }

    public static class AiOperationPlanner
    {
        // Called once per turn from AiTurnController.RunTurn, right after the strategy/budget
        // assessment and before the per-step Decide loop. Advances every active operation's phase
        // from live state, retires the finished/aborted ones, and starts a new Offensive when the
        // strategic posture calls for it.
        public static void AssessAll(PlayerSetupData player, PlayerRoot root, AiTurnContext ctx, AiStrategyAssessment strategy)
        {
            if (player == null || root == null || ctx == null)
                return;

            List<AiOperation> ops = AiOperationRegistry.For(player);
            for (int i = ops.Count - 1; i >= 0; i--)
            {
                AiOperation op = ops[i];
                string endReason = op.Type == AiOperationType.Offensive
                    ? AssessOffensive(player, root, ctx, op)
                    : "defensive-consolidation is a v2 stub";
                if (endReason != null)
                {
                    ReleaseStrikeTask(op);
                    AiDebugLog.Write($"[AI] {player.Nickname}: operation {op.Type}#{op.Id} ends — {endReason} "
                        + $"(phase={op.Phase}, objective ({op.Objective.Q},{op.Objective.R})).");
                    AiOperationRegistry.Remove(player, op, ctx.TurnNumber);
                }
            }

            TryStartOffensive(player, ctx, strategy, ops);
        }

        // Null = keep running; a non-null string is the end reason (logged, campaign retired).
        private static string AssessOffensive(PlayerSetupData player, PlayerRoot root, AiTurnContext ctx, AiOperation op)
        {
            // Abort: home under siege — the strike force is needed at home far more than at the
            // enemy's doorstep (the strategy layer has already snapped the Defence axis to ~0.95).
            if (AiDefencePlanner.IsUnderSiege(player, ctx))
            {
                op.Phase = AiOperationPhase.Withdrawing;
                return "home under siege";
            }

            bool strikeAdjacent = op.StrikeTask?.Army != null
                && HexGridMath.Distance(op.StrikeTask.Army.Hex, op.Objective) <= 1;

            // Objective no longer a valid target. If the strike was right on top of it, that reads
            // as a capture/razing (success). Otherwise the enemy building we were marching on is
            // simply gone from memory — nothing was accomplished, abandon the campaign (not a
            // "Consolidating" that reads as a win — 2026-08-27 log audit).
            if (!RaidWeakerArmyTask.IsStillValidTarget(player, op.Objective))
            {
                op.Phase = strikeAdjacent ? AiOperationPhase.Consolidating : AiOperationPhase.Withdrawing;
                return strikeAdjacent ? "objective taken" : "objective no longer a valid target";
            }

            // Adopt / keep the player's raid task and pin it to the objective.
            AiTask raid = AiTaskRegistry.TasksFor(player)
                .FirstOrDefault(t => t.Kind == AiTaskKind.RaidWeakerArmy && !t.Retreating && t.Army != null);
            if (raid != null)
            {
                raid.OperationId = op.Id;
                raid.TargetHex = op.Objective;
                op.StrikeTask = raid;
            }
            else
            {
                op.StrikeTask = null;
            }

            bool reachedObjective = op.StrikeTask?.Army != null
                && HexGridMath.Distance(op.StrikeTask.Army.Hex, op.Objective) <= 1;

            if (ctx.TurnNumber >= op.DeadlineTurn && !reachedObjective)
            {
                op.Phase = AiOperationPhase.Withdrawing;
                return "deadline reached";
            }

            // Hopeless-force abort (WorthIt feasibility, re-checked live) — the projected strike
            // strength has stayed well below the objective's defence for several turns running.
            // Don't wait out the full deadline on a campaign that can't land.
            RaidWeakerArmyTask.ThreatStrength threat = RaidWeakerArmyTask.RequiredStrengthAt(player, op.Objective, ctx.Map);
            float projected = ProjectedStrikeStrength(player, op.StrikeTask?.Army);
            bool hopeless = !threat.IsUndefended && projected < threat.Defense * AiConfig.operationHopelessRatio;
            op.HopelessSince = hopeless ? (op.HopelessSince < 0 ? ctx.TurnNumber : op.HopelessSince) : -1;
            if (op.HopelessSince >= 0 && ctx.TurnNumber - op.HopelessSince >= AiConfig.operationHopelessTurns && !reachedObjective)
            {
                op.Phase = AiOperationPhase.Withdrawing;
                return $"strike force can't realistically take the objective (force {projected:0} vs defence {threat.Defense:0})";
            }

            // Phase from live raid state — purely descriptive, recomputed fresh every turn.
            if (op.StrikeTask?.Army == null)
                op.Phase = AiOperationPhase.Forming;
            else if (reachedObjective)
                op.Phase = AiOperationPhase.Engaging;
            else if (op.StrikeTask.StillAssembling)
                op.Phase = AiOperationPhase.Forming;
            else
                op.Phase = AiOperationPhase.Advancing;

            string strikeName = op.StrikeTask?.Army != null ? $"\"{op.StrikeTask.Army.Name}\"" : "(forming)";
            AiDebugLog.Write($"[AI] {player.Nickname}: operation Offensive#{op.Id} \"take ({op.Objective.Q},{op.Objective.R})\" "
                + $"phase={op.Phase} turn {ctx.TurnNumber - op.StartedTurn + 1}/{op.DeadlineTurn - op.StartedTurn} — "
                + $"strike {strikeName} (force {projected:0} vs defence {threat.Defense:0}) | abort: siege / deadline t{op.DeadlineTurn}.");
            return null;
        }

        private static void TryStartOffensive(PlayerSetupData player, AiTurnContext ctx, AiStrategyAssessment strategy, List<AiOperation> ops)
        {
            if (ops.Count >= AiConfig.maxConcurrentOperations)
                return;
            if (ops.Any(o => o.Type == AiOperationType.Offensive))
                return;
            if (strategy.Posture != AiStrategyPosture.Pressure && strategy.Posture != AiStrategyPosture.AllIn)
                return;
            if (strategy.Aggression < AiConfig.operationOffensiveMinAggression)
                return;
            if (AiDefencePlanner.IsUnderSiege(player, ctx))
                return;
            // Cooldown after the last campaign ended — don't respawn on the very next turn.
            if (ctx.TurnNumber - AiOperationRegistry.LastEnded(player) < AiConfig.operationCooldownTurns)
                return;

            HexCoord? objective = PickObjective(player, ctx.Map);
            if (!objective.HasValue)
                return;

            AiOperation op = AiOperationRegistry.Create(player, AiOperationType.Offensive, objective.Value,
                ctx.TurnNumber, AiConfig.operationDeadlineTurns);
            AiDebugLog.Write($"[AI] {player.Nickname}: operation Offensive#{op.Id} STARTED — objective "
                + $"({objective.Value.Q},{objective.Value.R}) (posture {strategy.Posture}, AGG {strategy.Aggression:0.00}), "
                + $"deadline turn {op.DeadlineTurn}.");
        }

        // Nearest known enemy-OWNED BUILDING (a persistent, strategic target — razing/taking it
        // actually hurts them) that a realistically-projected strike force could take. Enemy army
        // sightings are deliberately NOT objectives — they expire in enemySightingMemoryTurns(2)
        // and had the operation churning a fresh 1-2 turn campaign every turn (2026-08-27 log
        // audit). Null when nothing qualifies — an Offensive operation is a real commitment, so no
        // takeable enemy building known = no operation.
        private static HexCoord? PickObjective(PlayerSetupData player, HexMap map)
        {
            HexCoord garrison = AiTurnController.GarrisonHexFor(player);
            float projected = ProjectedStrikeStrength(player, null);
            HexCoord? best = null;
            int bestDist = int.MaxValue;

            foreach (AiMapMemory.KnownBuilding b in AiMapMemory.AllKnownBuildings(player))
            {
                if (b.Owner == null || b.Owner == player || b.Owner.IsNeutral || b.IsStartingCitadel)
                    continue;
                RaidWeakerArmyTask.ThreatStrength threat = RaidWeakerArmyTask.RequiredStrengthAt(player, b.Hex, map);
                if (!threat.IsUndefended && projected < threat.Defense * AiConfig.operationFeasibilityRatio)
                    continue; // can't realistically take it even fully assembled — skip
                int d = HexGridMath.Distance(garrison, b.Hex);
                if (d < bestDist)
                {
                    bestDist = d;
                    best = b.Hex;
                }
            }
            return best;
        }

        // Rough upper bound on the force this player could bring to bear on one objective — the
        // strongest field combat army (or the operation's own adopted strike, whichever is bigger),
        // plus half of everything sitting in garrisons (recruitable into the raid). Deliberately
        // generous: this gates "could this campaign EVER work", not "can we win right now".
        private static float ProjectedStrikeStrength(PlayerSetupData player, ArmyData strikeArmy)
        {
            float bestField = strikeArmy != null ? WorthIt.AttackSum(strikeArmy) + WorthIt.DefenseSum(strikeArmy) : 0f;
            float garrison = 0f;
            foreach (ArmyData a in ArmyRegistry.AllForOwner(player))
            {
                if (a == null || a.IsPrison)
                    continue;
                float s = WorthIt.AttackSum(a) + WorthIt.DefenseSum(a);
                if (a.IsGarrison)
                    garrison += s;
                else if (s > bestField)
                    bestField = s;
            }
            return bestField + garrison * 0.5f;
        }

        // Called from AiTurnController.Decide's candidate gathering. v1 emits at most one candidate:
        // the Offensive operation's "march the strike force to the objective" advance directive,
        // built from the same shared path primitive the raid's own continuation uses, scored above
        // ordinary raid travel so the advance stays decisive. The raid task's own continuation is
        // still in the pool too and points the same way (target pinned) — whichever wins, the
        // strike advances.
        public static List<AiDecision> EmitDirectives(PlayerSetupData player, PlayerRoot root, AiTurnContext ctx)
        {
            var results = new List<AiDecision>();
            if (player == null || ctx?.Map == null)
                return results;

            foreach (AiOperation op in AiOperationRegistry.For(player))
            {
                if (op.Type != AiOperationType.Offensive)
                    continue;
                AiTask raid = op.StrikeTask;
                if (raid?.Army == null || raid.StillAssembling || raid.Retreating)
                    continue;
                if (HexGridMath.Distance(raid.Army.Hex, op.Objective) <= 1)
                    continue; // already engaging — the raid's own attack step takes it from here

                HexCoord? step = AiTurnController.FindPathStepAvoidingZone(ctx.Map, raid.Army, op.Objective, null, 0);
                if (step == null)
                    continue;

                results.Add(AiDecision.Move(raid.Army, step.Value,
                    $"operation Offensive#{op.Id} — strike force advances on ({op.Objective.Q},{op.Objective.R})",
                    raid, AiConfig.operationAdvanceScore, AiTaskCategory.Aggression));
            }
            return results;
        }

        // Whether any candidate carrying `task` should get the operations boost in Decide.
        public static bool IsOperationTask(AiTask task) => task != null && task.OperationId >= 0;

        private static void ReleaseStrikeTask(AiOperation op)
        {
            if (op.StrikeTask != null)
                op.StrikeTask.OperationId = -1;
            op.StrikeTask = null;
        }
    }
}
