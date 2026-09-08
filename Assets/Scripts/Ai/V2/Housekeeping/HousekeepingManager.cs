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
    //  HOUSEKEEPING MANAGER  (Strategy V2 build-order step 8C)
    // ===========================================================================================
    //  Last ordinary mutating V2 layer. After the pending strategic reaction/tempo handoff it owns
    //  only task-neutral, same-hex army/garrison force packaging: Analyzer -> pure Planner ->
    //  Executor. It never chooses an objective or mission, moves an army between hexes, plays a
    //  card, or spends AP/resources. The invariant below protects that zero-cost boundary.
    //
    //  Strategic pressure and maintenance are Phase-B tempo candidates. ReconOnly Air Recon stays
    //  terminally inside TaskExecutor. Neither is a second Housekeeping lane.
    // ===========================================================================================
    public sealed class HousekeepingResult
    {
        public bool StateChanged;
        public int MaintenanceActions;
        public int GroupsPlanned;
        public int TransfersApplied;
        public int TransfersFailed;
        public bool ApInvariantViolated;
        public StrategicReactionResult Reaction;
    }

    internal static class HousekeepingManager
    {
        public static IEnumerator RunHousekeeping(WorldSnapshot snapshot, PlayerSetupData player,
            PlayerRoot root, AiTurnContext ctx, ActorCommitments commitments, HousekeepingResult result,
            MaterializationReservation carriedReservation = null)
        {
            if (result == null)
                result = new HousekeepingResult();
            carriedReservation = carriedReservation ?? new MaterializationReservation();

            // Phase B deliberately preserves AP while a discovery/hand interrupt is pending.
            // Consume it here before maintenance, then rebuild the FULL world snapshot because the
            // reaction may have changed both own forces and honest map knowledge.
            float apReservedForReactionBefore = (player != null && ctx != null)
                ? StrategicResourceReservationLedger.Active(
                    player, ctx.TurnNumber, StrategicReservedResource.ActionPoints)
                : 0f;
            var reaction = new StrategicReactionResult();
            yield return StrategicReactionPass.ExecuteIfPending(
                snapshot, player, root, ctx, reaction, carriedReservation);
            result.Reaction = reaction;
            AiHandData hand = AiHandRegistry.Peek(player);
            if (reaction.Ran)
            {
                result.StateChanged |= reaction.StateChanged;
                if (hand != null)
                    snapshot = WorldAnalysis.Scan(player, root, hand, ctx);
                commitments = ActorCommitments.FromIntents(
                    MissionIntentRegistry.GetOrCreate(player).All,
                    snapshot,
                    ReconObjectiveEvaluator.Enumerate(snapshot));
            }

            // AI-MGR-02 §4/§P0 — if the reaction held back AP and that reservation is now gone
            // (the pass ran and released it, its EndOfReaction expiry fired, or the pass never ran
            // and was scope-suppressed), the freed AP MUST re-enter arbitration THIS turn — never
            // stranded to EndTurn. This is independent of whether the reaction ran.
            if (hand != null && player != null && root != null && ctx != null
                && apReservedForReactionBefore > 0f)
            {
                StrategicResourceReservationLedger.ReleaseByReason(
                    player, ctx.TurnNumber, StrategicReservationReason.StrategicReactionPass);
                for (int rerun = 0; rerun < AiConfigV2.maxEndOfTurnTempoReruns; rerun++)
                {
                    int apBeforeTempo = root.ActionPoints;
                    snapshot = WorldAnalysis.RefreshOperationalState(snapshot, player, root, hand, ctx);
                    ActorCommitments tempoCommitments = ActorCommitments.FromIntents(
                        MissionIntentRegistry.GetOrCreate(player).All,
                        snapshot,
                        ReconObjectiveEvaluator.Enumerate(snapshot));
                    var tempo = new StrategicPhaseResult();
                    yield return StrategicManager.UseSurplus(
                        snapshot, player, root, hand, ctx, tempoCommitments, carriedReservation, tempo);
                    carriedReservation = tempo.Reservation;
                    if (tempo.StateChanged)
                    {
                        result.StateChanged = true;
                        snapshot = WorldAnalysis.Scan(player, root, hand, ctx);
                        commitments = ActorCommitments.FromIntents(
                            MissionIntentRegistry.GetOrCreate(player).All,
                            snapshot,
                            ReconObjectiveEvaluator.Enumerate(snapshot));
                    }
                    AiDebugLog.Write($"[AI][V2] tempo — end-of-turn tempo re-run after reaction reservation "
                        + $"released (reaction ran={(reaction.Ran ? 1 : 0)}); cardsPlayed {tempo.CardsPlayed}, "
                        + $"drawn {tempo.CardsDrawn}, ap {apBeforeTempo}->{root.ActionPoints}");
                    if (!tempo.StateChanged || tempo.CardsPlayed + tempo.CardsDrawn == 0)
                        break;
                }
            }

            // AI-MGR-02 §P0 — decisive structure pressure and strategic maintenance are NO LONGER
            // run here as post-tempo lanes. They are `StrategicSpend` candidates inside the one
            // end-of-turn tempo arbiter (StrategicManager.UseSurplus), so they compete for AP in the
            // same comparable utility space as Play / Draw / Hold / EndTurn. Housekeeping past this
            // point is ONLY the zero-AP / zero-resource structural reorganisation pass.

            Run(snapshot, player, root, ctx, commitments, result);
            StrategicCapabilityLeaseRegistry.Clear(player, ctx?.TurnNumber ?? 0);
            TurnResourceTelemetry.LogEnd(player, root, ctx?.TurnNumber ?? 0);
            yield break;
        }

        internal static void Run(WorldSnapshot snapshot, PlayerSetupData player, PlayerRoot root, AiTurnContext ctx,
            ActorCommitments commitments, HousekeepingResult result)
        {
            if (player == null || ctx == null)
            {
                AiDebugLog.Write("[AI][V2] housekeeping — no player/ctx, nothing to do.");
                return;
            }

            int apBefore = root != null ? root.ActionPoints : 0;
            ArmyReorgAnalysis analysis = ArmyReorgAnalyzer.Analyze(player, commitments, snapshot, ctx);
            if (analysis.Groups.Count == 0)
            {
                AiDebugLog.Write("[AI][V2] housekeeping — no local force group worth reorganising.");
                return;
            }

            AiDebugLog.Write("[AI][V2][HOUSEKEEPING] -- LOCAL FORCE PACKAGING ----------------");
            foreach (LocalForceGroup group in analysis.Groups)
            {
                LogGroupInput(group, analysis);
                ReorganizationPlan plan = ArmyReorganizationPlanner.Plan(group);
                if (plan.IsEmpty)
                {
                    AiDebugLog.Write($"[AI][V2][HOUSEKEEPING]   {plan.HexKey} decision: "
                        + $"{plan.DebugSummary()}; keep current composition.");
                    continue;
                }

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
                AiDebugLog.Write($"[AI][V2][ERROR] housekeeping AP invariant violated — AP {apBefore}->{root.ActionPoints}. "
                    + "Structural reorganisation owns no AP; strategic pressure/maintenance is measured before this boundary.");
            }

            AiDebugLog.Write($"[AI][V2] housekeeping — strategicActions {result.MaintenanceActions}, "
                + $"groups {result.GroupsPlanned}, operations applied {result.TransfersApplied}, "
                + $"failed {result.TransfersFailed}, stateChanged {(result.StateChanged ? 1 : 0)}, "
                + $"apInvariant {(result.ApInvariantViolated ? "FAIL" : "ok")}");
        }

        private static void LogGroupInput(LocalForceGroup group, ArmyReorgAnalysis analysis)
        {
            AiDebugLog.Write($"[AI][V2][HOUSEKEEPING]   ({group.Q},{group.R}) before: "
                + FormatProjectedGroup(group, analysis));

            if (group.ThreatBenchmarks.Count == 0)
            {
                AiDebugLog.Write($"[AI][V2][HOUSEKEEPING]   ({group.Q},{group.R}) benchmark: "
                    + "no deployed enemy field army; fallback=AiPower");
                return;
            }

            string threats = string.Join(" | ", group.ThreatBenchmarks.Select(t =>
                $"enemy#{t.ArmyId} {(t.HiddenFromUs ? "hidden-cheat" : "visible")} "
                + $"eta(group/base/used)={t.EtaToGroup}/{t.EtaToNearestBase}/{t.EffectiveEta} "
                + $"[{string.Join(",", t.Members.Select(FormatCombatProfile))}]"));
            AiDebugLog.Write($"[AI][V2][HOUSEKEEPING]   ({group.Q},{group.R}) benchmark: {threats}");
        }

        private static string FormatProjectedGroup(LocalForceGroup group, ArmyReorgAnalysis analysis) =>
            string.Join(" | ", group.Containers.OrderBy(c => c.ArmyId)
                .Select(c => FormatProjectedContainer(c, analysis)));

        private static string FormatProjectedContainer(ReorgContainer container,
            ArmyReorgAnalysis analysis) =>
            $"#{container.ArmyId}/{container.Role}[{string.Join(",", container.Units
                .Select(u => FormatProjectedUnit(u, analysis)))}]";

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
            return $"#{army.Id}/{container.Role}[{string.Join(",", army.Members
                .Select(u => FormatLiveUnit(u, operators)))}]";
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

        // §16 — the aggregate block above logs inputs, decision and final state; this adds only
        // the important UNRESOLVED
        // structural defects a debug run needs, without flooding the log with every rejected
        // candidate. Only containers the plan did not touch are reported.
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
                    AiDebugLog.Write($"[AI][V2] housekeeping {plan.HexKey} — singleton #{c.ArmyId} "
                        + "protected reason=StrategicCapabilityLease/mission");
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

                AiDebugLog.Write($"[AI][V2] housekeeping {plan.HexKey} — heroless formation #{c.ArmyId} "
                    + $"unresolved reason={(benchedCombatHero ? "activation_ap_or_capacity_blocked" : "no_benched_combat_leader")}");
            }
        }
    }
}
