using System.Collections;
using Game.Map;
using Game.Players;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  HOUSEKEEPING MANAGER  (Strategy V2 build-order step 8C)
    // ===========================================================================================
    //  Last ordinary mutating V2 layer. It has three deliberately separated pieces:
    //    · decisive strategic pressure (may move/spend activation AP) toward an honestly-known
    //      enemy Citadel after the army-targeted Raid lane runs out of contacts;
    //    · bounded strategic maintenance (may spend AP/resources): internal Facility placement,
    //      Base/Citadel slot-capacity upgrade, Equipment on live units, standalone generation;
    //    · local same-hex army/garrison structural reorganisation (must remain zero-AP).
    //
    //  ReconOnly Air Recon is NOT housekeeping. It runs once, terminally, inside TaskExecutor
    //  after the provisioned Ground Recon batch. Keeping it there gives one owner for the whole
    //  one-hex sortie lifecycle and prevents a second Housekeeping air pass from spending the same
    //  leftover AP/Energy again.
    //
    //  A pending strategic interrupt is consumed first. Only after that bounded replan settles do
    //  pressure/maintenance actions run, and only after those settle do we enter the zero-AP
    //  Analyzer -> Planner -> Executor reorganisation pass. The AP invariant below therefore
    //  starts AFTER all strategic actions and still protects structural cleanup from drift.
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
            PlayerRoot root, AiTurnContext ctx, ActorCommitments commitments, HousekeepingResult result)
        {
            if (result == null)
                result = new HousekeepingResult();

            // Phase B deliberately preserves AP while a discovery/hand interrupt is pending.
            // Consume it here before maintenance, then rebuild the FULL world snapshot because the
            // reaction may have changed both own forces and honest map knowledge.
            var reaction = new StrategicReactionResult();
            yield return StrategicReactionPass.ExecuteIfPending(snapshot, player, root, ctx, reaction);
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
            else if (hand != null && player != null && root != null && ctx != null
                     && StrategicResourceReservationLedger.ReleaseByReason(
                         player, ctx.TurnNumber, StrategicReservationReason.StrategicReactionPass))
            {
                // AI-MGR-02 §4 — the bounded reaction pass did NOT run (scope-suppressed, or the
                // pending invalidation went away before it), so the AP Phase B explicitly reserved
                // for it is stranded. Release it and run end-of-turn tempo spending once more THIS
                // turn so the freed AP is offered to Play / Draw again instead of being lost.
                for (int rerun = 0; rerun < AiConfigV2.maxEndOfTurnTempoReruns; rerun++)
                {
                    int apBeforeTempo = root.ActionPoints;
                    snapshot = WorldAnalysis.RefreshOperationalState(snapshot, player, root, hand, ctx);
                    ActorCommitments tempoCommitments = ActorCommitments.FromIntents(
                        MissionIntentRegistry.GetOrCreate(player).All,
                        snapshot,
                        ReconObjectiveEvaluator.Enumerate(snapshot));
                    StrategicPhaseResult tempo = StrategicManager.UseSurplus(
                        snapshot, player, root, hand, ctx, tempoCommitments, new MaterializationReservation());
                    if (tempo.StateChanged)
                    {
                        result.StateChanged = true;
                        snapshot = WorldAnalysis.Scan(player, root, hand, ctx);
                        commitments = ActorCommitments.FromIntents(
                            MissionIntentRegistry.GetOrCreate(player).All,
                            snapshot,
                            ReconObjectiveEvaluator.Enumerate(snapshot));
                    }
                    AiDebugLog.Write($"[AI][V2] tempo — end-of-turn tempo re-run: reaction pass did not "
                        + $"run; cardsPlayed {tempo.CardsPlayed}, drawn {tempo.CardsDrawn}, "
                        + $"ap {apBeforeTempo}->{root.ActionPoints}");
                    if (!tempo.StateChanged || tempo.CardsPlayed + tempo.CardsDrawn == 0)
                        break;
                }
            }

            // Structure pressure is movement, so execute it before the synchronous maintenance
            // actions below. Terminal Draw has already declined to consume its activation budget.
            if (hand != null && player != null && root != null && ctx != null)
            {
                StrategicPressurePlan pressure = StrategicPressureAdvance.BuildPlan(
                    player, root, hand, ctx, commitments);
                if (pressure != null)
                {
                    bool pressureChanged = false;
                    yield return StrategicPressureAdvance.Execute(
                        player, root, ctx, pressure, changed => pressureChanged = changed);
                    if (pressureChanged)
                    {
                        result.MaintenanceActions++;
                        result.StateChanged = true;
                        snapshot = WorldAnalysis.Scan(player, root, hand, ctx);
                        commitments = ActorCommitments.FromIntents(
                            MissionIntentRegistry.GetOrCreate(player).All,
                            snapshot,
                            ReconObjectiveEvaluator.Enumerate(snapshot));
                    }
                }
            }

            // Terminal Draw has already declined to consume AP whenever this policy has a useful
            // action. Execute a small bounded number here. Each success refreshes the operational
            // snapshot so a capacity upgrade can expose a Facility placement next, and generation
            // of Equipment can expose an attach on the following iteration.
            if (hand != null && player != null && root != null && ctx != null)
            {
                int remaining = System.Math.Max(0,
                    StrategicMaintenancePolicy.MaxActionsPerTurn - result.MaintenanceActions);
                for (int i = 0; i < remaining; i++)
                {
                    if (!StrategicMaintenancePolicy.TryExecuteBest(snapshot, player, root, hand, ctx))
                        break;
                    result.MaintenanceActions++;
                    result.StateChanged = true;
                    snapshot = WorldAnalysis.RefreshOperationalState(snapshot, player, root, hand, ctx);
                }
                if (result.MaintenanceActions > 0)
                    AiDebugLog.Write($"[AI][V2] housekeeping maintenance — {result.MaintenanceActions} strategic action(s) executed before structural cleanup");
            }

            Run(player, root, ctx, commitments, result);
            StrategicCapabilityLeaseRegistry.Clear(player, ctx?.TurnNumber ?? 0);
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
                    + "Structural reorganisation owns no AP; strategic pressure/maintenance is measured before this boundary.");
            }

            AiDebugLog.Write($"[AI][V2] housekeeping — strategicActions {result.MaintenanceActions}, "
                + $"groups {result.GroupsPlanned}, operations applied {result.TransfersApplied}, "
                + $"failed {result.TransfersFailed}, stateChanged {(result.StateChanged ? 1 : 0)}, "
                + $"apInvariant {(result.ApInvariantViolated ? "FAIL" : "ok")}");
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
