using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Game.Combat;
using Game.Map;
using Game.Players;
using Game.Units;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  HOUSEKEEPING MANAGER — the day pass: zero-AP, task-neutral same-hex force packaging.
    // ===========================================================================================
    //  Analyzer -> pure Planner -> Executor, per own hex. It never chooses an objective or a
    //  mission, moves an army between hexes, plays a card or spends AP/resources; the AP check
    //  below guards that boundary. The pending reaction and its tempo re-run run before it
    //  (AiStrategyV2Pipeline); the night fold after every end-of-turn measurement.
    // ===========================================================================================
    public sealed class HousekeepingResult
    {
        public bool StateChanged;
        public int GroupsPlanned;
        public int TransfersApplied;
        public int TransfersFailed;
        public bool ApInvariantViolated;
    }

    internal static class HousekeepingManager
    {
        internal static IEnumerator Run(WorldSnapshot snapshot, PlayerSetupData player, PlayerRoot root, AiTurnContext ctx,
            ActorCommitments commitments, HousekeepingResult result)
        {
            if (player == null || ctx == null)
            {
                AiDebugLog.Write("[AI][V2] housekeeping - no player/ctx, nothing to do.");
                yield break;
            }

            int apBefore = root != null ? root.ActionPoints : 0;
            ArmyReorgAnalysis analysis = ArmyReorgAnalyzer.Analyze(player, commitments, snapshot, ctx);
            if (analysis.Groups.Count == 0)
            {
                AiDebugLog.Write("[AI][V2] housekeeping - no own army on the map.");
                yield break;
            }

            AiDebugLog.Write("[AI][V2][HOUSEKEEPING] -- LOCAL FORCE PACKAGING ----------------");
            foreach (LocalForceGroup group in analysis.Groups)
            {
                // Every own hex is planned; the plan gives frames back between candidates.
                var pacer = new FramePacer();
                StagePerfMark planStart = StagePerfTelemetry.Mark(pacer);
                var plan = new ReorganizationPlan();
                yield return ArmyReorganizationPlanner.PlanRoutine(group, pacer, plan);
                if (group.ThreatBenchmarks.Count > 0)
                    StagePerfTelemetry.Log(pacer, planStart, "housekeeping.plan",
                        $"{plan.HexKey} threats={group.ThreatBenchmarks.Count}");
                if (plan.IsEmpty)
                {
                    // One line per unchanged hex (its threats, when any, on the benchmark line).
                    AiDebugLog.Write($"[AI][V2][HOUSEKEEPING]   ({group.Q},{group.R}) keep: "
                        + FormatProjectedGroup(group, analysis));
                    if (group.ThreatBenchmarks.Count > 0)
                        LogBenchmark(group);
                    continue;
                }

                LogGroupInput(group, analysis);
                result.GroupsPlanned++;
                AiDebugLog.Write($"[AI][V2][HOUSEKEEPING]   {plan.HexKey} decision: {plan.DebugSummary()}");
                HousekeepingExecResult exec = HousekeepingExecutor.Execute(plan, analysis, player, ctx, commitments);
                result.StateChanged |= exec.StateChanged;
                result.TransfersApplied += exec.Applied;
                result.TransfersFailed += exec.Failed;
                AiDebugLog.Write($"[AI][V2][HOUSEKEEPING]   {plan.HexKey} after: "
                    + FormatLiveGroup(group, analysis));

                LogUnresolvedStructuralDefects(group, plan);
            }

            if (root != null && root.ActionPoints != apBefore)
            {
                result.ApInvariantViolated = true;
                AiDebugLog.Write($"[AI][V2][ERROR] housekeeping AP invariant violated - AP {apBefore}->{root.ActionPoints}. "
                    + "Structural reorganisation owns no AP; strategic pressure/maintenance is measured before this boundary.");
            }

            AiDebugLog.Write($"[AI][V2] housekeeping - groups {result.GroupsPlanned}, operations applied {result.TransfersApplied}, "
                + $"failed {result.TransfersFailed}, stateChanged {(result.StateChanged ? 1 : 0)}, "
                + $"apInvariant {(result.ApInvariantViolated ? "FAIL" : "ok")}");
        }

        private static void LogGroupInput(LocalForceGroup group, ArmyReorgAnalysis analysis)
        {
            AiDebugLog.Write($"[AI][V2][HOUSEKEEPING]   ({group.Q},{group.R}) before: "
                + FormatProjectedGroup(group, analysis));
            LogBenchmark(group);
        }

        private static void LogBenchmark(LocalForceGroup group)
        {
            if (group.ThreatBenchmarks.Count == 0)
            {
                AiDebugLog.Write($"[AI][V2][HOUSEKEEPING]   ({group.Q},{group.R}) benchmark: "
                    + "no deployed enemy field army; fallback=AiPower");
                return;
            }

            string threats = string.Join(" | ", group.ThreatBenchmarks.Select(t =>
                $"enemy#{t.ArmyId} {(t.HiddenFromUs ? "hidden-cheat" : "visible")} "
                + $"eta(group/base)={t.EtaToGroup}/{t.EtaToNearestBase} "
                + $"[{string.Join(",", t.Members.Select(FormatCombatProfile))}]"));
            AiDebugLog.Write($"[AI][V2][HOUSEKEEPING]   ({group.Q},{group.R}) benchmark: {threats}");
        }

        private static string FormatProjectedGroup(LocalForceGroup group, ArmyReorgAnalysis analysis) =>
            string.Join(" | ", group.Containers.OrderBy(c => c.ArmyId)
                .Select(c => FormatProjectedContainer(c, analysis)));

        private static string FormatProjectedContainer(ReorgContainer container,
            ArmyReorgAnalysis analysis) =>
            $"#{container.ArmyId}/{container.Role}"
            + (container.MissionLabel != null
                ? $"({container.MissionLabel}{(container.IsMissionReceiver ? ",inbound" : ",locked")}"
                    + (container.MovementFloor >= 0 ? $",mp>={container.MovementFloor}/{container.MaxMovementFloor}" : "")
                    + ")"
                : "")
            + $"[{string.Join(",", container.Units.Select(u => FormatProjectedUnit(u, analysis)))}]";

        private static string FormatProjectedUnit(ReorgUnit unit, ArmyReorgAnalysis analysis)
        {
            string name = analysis.UnitByKey.TryGetValue(unit.Key, out UnitData live)
                ? live.Name : "u" + unit.Key;
            if (!unit.IsHero)
                return name;
            return name + "{H:" + unit.HeroRole
                + (unit.IsDevelopmentOperator ? ",operator" : "") + "}";
        }

        private static string FormatLiveGroup(LocalForceGroup group, ArmyReorgAnalysis analysis)
        {
            var operators = new HashSet<UnitData>(group.Containers.SelectMany(c => c.Units)
                .Where(u => u.IsDevelopmentOperator)
                .Select(u => analysis.UnitByKey.TryGetValue(u.Key, out UnitData live) ? live : null)
                .Where(u => u != null));
            return string.Join(" | ", group.Containers.OrderBy(c => c.ArmyId)
                .Select(c => FormatLiveContainer(c, analysis, operators)));
        }

        private static string FormatLiveContainer(ReorgContainer container,
            ArmyReorgAnalysis analysis, HashSet<UnitData> operators)
        {
            if (!analysis.ArmyById.TryGetValue(container.ArmyId, out ArmyData army) || army == null)
                return $"#{container.ArmyId}/missing";
            return $"#{army.Id}/{container.Role}[{string.Join(",", army.Members.Select(u => FormatLiveUnit(u, operators)))}]";
        }

        private static string FormatLiveUnit(UnitData unit, HashSet<UnitData> operators)
        {
            if (!unit.IsHero)
                return unit.Name;
            return unit.Name + "{H:" + HeroRoleEvaluator.Classify(unit)
                + (operators.Contains(unit) ? ",operator" : "") + "}";
        }

        private static string FormatCombatProfile(WorthIt.DefenderProfile p) =>
            $"A{p.Attack:0.#}/D{p.Defense:0.#}/HP{p.HitPoints:0.#}/I{p.Initiative}"
            + (p.Abilities.Count > 0 ? "/skills:" + string.Join("+", p.Abilities) : "");

        // The block above logs inputs, decision and final state; this adds only the unresolved
        // structural defects of containers the plan did not touch.
        private static void LogUnresolvedStructuralDefects(LocalForceGroup group, ReorganizationPlan plan)
        {
            if (group?.Containers == null || plan == null)
                return;
            foreach (ReorgContainer c in group.Containers)
            {
                bool touched = false;
                foreach (PlannedTransfer t in plan.Transfers)
                    if (t.FromArmyId == c.ArmyId || t.ToArmyId == c.ArmyId) { touched = true; break; }
                if (touched)
                    continue;

                if (c.Role == ReorgPhysicalRole.ProtectedMissionArmy && c.MemberCount <= 1)
                {
                    AiDebugLog.Write($"[AI][V2] housekeeping {plan.HexKey} - singleton #{c.ArmyId} "
                        + $"protected reason=StrategicCapabilityLease/mission contract={c.MissionLabel} "
                        + $"inbound={(c.IsMissionReceiver ? "allowed_no_legal_free_member" : "locked")}");
                    continue;
                }

                bool herolessViableFormation = c.IsMutableGround && c.CanChangeComposition
                    && !c.SingletonExempt && c.MemberCount >= 2
                    && !c.Units.Exists(u => u.IsHero) && ReorgViability.IsViable(c.Units);
                if (!herolessViableFormation)
                    continue;

                bool benchedCombatHero = false;
                foreach (ReorgContainer g in group.Containers)
                    if (g.CanChangeComposition && g.Units.Exists(u => u.IsHero
                        && u.HeroRole != HeroOperationalRole.SupportOperator
                        && (g.IsGarrison ? g.Units.Count > 1 : g.Units.Count == 1)))
                        benchedCombatHero = true;

                AiDebugLog.Write($"[AI][V2] housekeeping {plan.HexKey} - heroless formation #{c.ArmyId} "
                    + $"unresolved reason={(benchedCombatHero ? "activation_ap_or_capacity_blocked" : "no_benched_combat_leader")}");
            }
        }
    }
}
