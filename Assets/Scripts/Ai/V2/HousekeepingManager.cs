using System.Collections;
using Game.Map;
using Game.Players;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  HOUSEKEEPING MANAGER  (Strategy V2 build-order step 8C)
    // ===========================================================================================
    //  Last ordinary mutating V2 layer: local same-hex army/garrison structural reorganisation.
    //  A pending Recon strategic interrupt is consumed immediately before housekeeping by the one
    //  bounded StrategicReactionPass; only after that pass settles do we run the zero-AP structural
    //  cleanup below. Analyzer -> pure deterministic Planner -> canonical Executor.
    //
    //  AP OWNERSHIP: housekeepingApReserve is currently 0. Therefore every planned/executed
    //  transfer/swap must be zero-cost under ArmyActions' real activated-destination rule. The
    //  executor enforces that before mutation; this manager also records a turn-end invariant
    //  violation if AP changed anyway, protecting against future gameplay-rule drift.
    // ===========================================================================================
    public sealed class HousekeepingResult
    {
        public bool StateChanged;
        public int GroupsPlanned;
        public int TransfersApplied;
        public int TransfersFailed;
        public bool ApInvariantViolated;
        public StrategicReactionResult Reaction;
    }

    internal static class HousekeepingManager
    {
        public static IEnumerator RunHousekeeping(WorldSnapshot snapshot, PlayerSetupData player,
            PlayerRoot root, AiTurnContext ctx, ActorCommitments commitments, HousekeepingResult result)
        {
            if (result == null)
                result = new HousekeepingResult();

            // Phase B deliberately preserves AP while a discovery interrupt is pending. Consume it
            // here before structural cleanup, then rebuild the FULL world snapshot because the
            // reaction may have changed both own forces and honest map knowledge.
            var reaction = new StrategicReactionResult();
            yield return StrategicReactionPass.ExecuteIfPending(snapshot, player, root, ctx, reaction);
            result.Reaction = reaction;
            if (reaction.Ran)
            {
                result.StateChanged |= reaction.StateChanged;
                AiHandData hand = AiHandRegistry.Peek(player);
                if (hand != null)
                    snapshot = WorldAnalysis.Scan(player, root, hand, ctx);
                commitments = ActorCommitments.FromIntents(
                    MissionIntentRegistry.GetOrCreate(player).All,
                    snapshot,
                    ReconObjectiveEvaluator.Enumerate(snapshot));
            }

            Run(player, root, ctx, commitments, result);
            // Strategic capability leases exist only to bridge Phase A/Reaction materialization to
            // this final structural pass. Once housekeeping has respected them they must expire;
            // otherwise a one-turn preparation decision would freeze that army in later turns.
            StrategicCapabilityLeaseRegistry.Clear(player, ctx?.TurnNumber ?? 0);
            // Run() can return early when there is nothing to reorganise; resource telemetry is a
            // turn-level concern and must still be emitted exactly once after the last mutating V2
            // layer has had its chance.
            TurnResourceTelemetry.LogEnd(player, root, ctx?.TurnNumber ?? 0);
            yield break;
        }

        internal static void Run(PlayerSetupData player, PlayerRoot root, AiTurnContext ctx,
            ActorCommitments commitments, HousekeepingResult result)
        {
            if (player == null || ctx == null)
            {
                AiDebugLog.Write("[AI][V2] housekeeping — no player/ctx, nothing to do.");
                return;
            }

            int apBefore = root != null ? root.ActionPoints : 0;
            ArmyReorgAnalysis analysis = ArmyReorgAnalyzer.Analyze(player, commitments);
            if (analysis.Groups.Count == 0)
            {
                AiDebugLog.Write("[AI][V2] housekeeping — no local force group worth reorganising.");
                return;
            }

            foreach (LocalForceGroup group in analysis.Groups)
            {
                ReorganizationPlan plan = ArmyReorganizationPlanner.Plan(group);
                if (plan.IsEmpty)
                {
                    AiDebugLog.Write($"[AI][V2] housekeeping {plan.HexKey} — analysed, no legal improvement.");
                    continue;
                }

                result.GroupsPlanned++;
                AiDebugLog.Write($"[AI][V2] housekeeping plan — {plan.DebugSummary()}");
                HousekeepingExecResult exec = HousekeepingExecutor.Execute(plan, analysis, player, ctx, commitments);
                result.StateChanged |= exec.StateChanged;
                result.TransfersApplied += exec.Applied;
                result.TransfersFailed += exec.Failed;

                LogUnresolvedStructuralDefects(group, plan);
            }

            if (root != null && root.ActionPoints != apBefore)
            {
                result.ApInvariantViolated = true;
                AiDebugLog.Write($"[AI][V2][ERROR] housekeeping AP invariant violated — AP {apBefore}->{root.ActionPoints}. "
                    + "Step 8C owns no AP while housekeepingApReserve is 0.");
            }

            AiDebugLog.Write($"[AI][V2] housekeeping — groups {result.GroupsPlanned}, "
                + $"operations applied {result.TransfersApplied}, failed {result.TransfersFailed}, "
                + $"stateChanged {(result.StateChanged ? 1 : 0)}, apInvariant {(result.ApInvariantViolated ? "FAIL" : "ok")}");
        }

        // §16 — final decisions are logged by the executor; this adds the important UNRESOLVED
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
