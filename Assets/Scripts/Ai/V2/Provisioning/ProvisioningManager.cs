using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Game.Aviation;
using Game.Cards;
using Game.Economy;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Units;
using UnityEngine;

using Game.Combat;

namespace Game.Ai.V2
{
    internal static partial class ProvisioningManager
    {
        private static int StealthTransitionApCost => AiConfigV2.scoutOptionalStealthAp;

        // Live structural revalidation uses the same canonical army-role predicate Analysis
        // froze into ArmySnapshot.IsMobileEconomyBuilder.
        public static void PreparePass(PlayerSetupData player, PlayerRoot root, AiTurnContext ctx,
            ProvisioningSession session, TentativeAllocation allocation,
            ActorCommitments durableCommitments = null)
        {
            session.SetDurableClaims(durableCommitments?.ClaimedArmyIds);
            PrepareScoutAssignments(player, root, ctx, session, allocation, durableCommitments);
            PrepareGroundCombatAssignments(session, allocation, durableCommitments);
        }

        // Assignment is solved for the whole funded Scout set. Expose every negative result as one
        // batch so orchestration can return all impossible jobs to the existing allocator before it
        // chooses the next mission. This is vocabulary translation only; ReconAssignmentPlanner
        // remains the sole owner of assignment feasibility and rejection reasons.
        internal static IReadOnlyList<(FundedEntry Funded, ProvisionFailure Failure)>
            ScoutAssignmentFailures(ProvisioningSession session, TentativeAllocation allocation)
        {
            var failures = new List<(FundedEntry, ProvisionFailure)>();
            if (session == null || allocation?.Funded == null)
                return failures;
            foreach (FundedEntry funded in allocation.Funded)
            {
                if (funded?.Mission?.Kind != MissionKind.Scout
                    || !(funded.Mission.Target is ScoutMissionTarget target))
                    continue;
                StableMissionKey key = StableMissionKey.For(funded.Mission);
                if (!session.AssignmentRejections.ContainsKey(key))
                    continue;
                failures.Add((funded, AssignmentFailure(session, key, target)));
            }
            return failures;
        }

        // Delegates the actual actor<->job matching to ReconAssignmentPlanner (the ONE canonical
        // Assignment owner, spec Level 5) — ProvisioningManager only collects the open FUNDED Scout
        // missions, hands them over, and stores the result. Only funded missions ever reach here:
        // there is no pre-funding reservation state to reconcile against any more. Round 4 — `root`
        // is now threaded through so AssignFunded can size/probe the air-actor pool (Energy/AP gates)
        // the same way ReconAirReservationPrepass already does for capacity sizing.
        private static void PrepareScoutAssignments(PlayerSetupData player, PlayerRoot root, AiTurnContext ctx,
            ProvisioningSession session, TentativeAllocation allocation,
            ActorCommitments durableCommitments)
        {
            var open = new List<FundedEntry>();
            if (allocation?.Funded != null)
                foreach (FundedEntry fe in allocation.Funded)
                {
                    if (fe?.Mission == null || fe.Mission.Kind != MissionKind.Scout
                        || !(fe.Mission.Target is ScoutMissionTarget)
                        || session.AlreadyProvisioned(StableMissionKey.For(fe.Mission)))
                        continue;
                    open.Add(fe);
                }
            open.Sort((a, b) => a.Priority.CompareTo(b.Priority));

            ReconAssignmentResult result = ReconAssignmentPlanner.AssignFunded(
                session.Snapshot, ctx, player, open, session.ClaimedArmyIds, root,
                session.Successful.Values.ToList(), durableCommitments?.ClaimedArmyIdSet);
            session.SetAssignment(result);
        }

        private static void PrepareGroundCombatAssignments(ProvisioningSession session,
            TentativeAllocation allocation, ActorCommitments durableCommitments)
        {
            var open = new List<FundedEntry>();
            // ALL ground-combat proposals are decided in ONE pass, so one
            // army can never simultaneously receive an Assault assignment and be pinned as another
            // mission's primary or reinforcement convoy. Every ground-combat MissionKind admitted
            // below shares this one solve; a new lane joins the batch, it does not get its own.
            var pinnedByOtherLegs = new HashSet<int>();
            if (allocation?.Funded != null)
                foreach (FundedEntry fe in allocation.Funded)
                {
                    // A lifecycle leg that already carries Continuity-pinned actors pins them for
                    // every other ground-combat proposal in the same solve (GroundCombatLegs).
                    if (!GroundCombatLegs.PinsActors(fe?.Mission, out int? pinnedPrimary,
                            out int? pinnedSupport))
                        continue;
                    if (pinnedPrimary.HasValue) pinnedByOtherLegs.Add(pinnedPrimary.Value);
                    if (pinnedSupport.HasValue) pinnedByOtherLegs.Add(pinnedSupport.Value);
                }
            if (allocation?.Funded != null)
                foreach (FundedEntry fe in allocation.Funded)
                {
                    if (fe?.Mission == null
                        || (fe.Mission.Kind != MissionKind.Raid
                            && fe.Mission.Kind != MissionKind.ActiveDefence
                            && fe.Mission.Kind != MissionKind.Attack)
                        || session.AlreadyProvisioned(StableMissionKey.For(fe.Mission)))
                        continue;
                    // Lifecycle legs already have their actor pinned by Continuity and take no
                    // part in the assignment solve; Assault / Intercept and an UNPINNED
                    // Reinforcement leg (no support bound yet) are actor-contention decisions.
                    if (!GroundCombatLegs.JoinsAssignmentSolve(fe.Mission))
                        continue;
                    open.Add(fe);
                }
            open.Sort((a, b) => a.Priority.CompareTo(b.Priority));

            // A re-pack refreshes the entire assignment; never let last pass's assignments
            // exclude current candidates while solving the new batch.
            session.SetGroundCombatAssignment(new Dictionary<StableMissionKey, int>());
            session.SetGroundCombatConstraints(durableCommitments, pinnedByOtherLegs);
            var cands = new List<List<int>>(open.Count);
            foreach (FundedEntry fe in open)
            {
                HashSet<int> excluded = session.ExcludedForGroundCombat(fe.Mission);
                var ids = new List<int>();
                if (GroundCombatAdmissionRegistry.TryGet(fe.Mission, out HashSet<int> eligible))
                    ids.AddRange(eligible
                        .Where(id => !excluded.Contains(id))
                        .OrderBy(id => GroundCombatActorActivation(session.Snapshot, id))
                        .ThenBy(id => GroundCombatActorPower(session.Snapshot, id))
                        .ThenBy(id => id));
                cands.Add(ids);
            }

            var chosen = new int[open.Count];
            var best = new int[open.Count];
            for (int i = 0; i < best.Length; i++) best[i] = -1;
            long[] bestKey = null;
            RecurseGroundCombat(0, open, cands, chosen, new HashSet<int>(), session.Snapshot, ref bestKey, best);

            var map = new Dictionary<StableMissionKey, int>();
            for (int i = 0; i < open.Count; i++)
                if (best[i] >= 0)
                    map[StableMissionKey.For(open[i].Mission)] = cands[i][best[i]];
            session.SetGroundCombatAssignment(map);

            if (open.Count > 0)
                AiDebugLog.Write($"[AI][V2]   provision prepare ground-combat — {open.Count} open, assigned ["
                    + string.Join(" ", map.Select(kv => $"{kv.Key}->#{kv.Value}")) + "]");
        }

        private static int GroundCombatActorActivation(WorldSnapshot snap, int id)
        {
            ArmySnapshot a = snap?.Self?.Armies?.FirstOrDefault(x => x != null && x.ArmyId == id);
            return a == null || a.HasActivatedThisTurn ? 0 : a.ActivationApCost;
        }

        private static float GroundCombatActorPower(WorldSnapshot snap, int id) =>
            snap?.Self?.Armies?.FirstOrDefault(x => x != null && x.ArmyId == id)?.EffectiveArmyPower ?? float.MaxValue;

        private static void RecurseGroundCombat(int i, List<FundedEntry> open, List<List<int>> cands,
            int[] chosen, HashSet<int> usedArmyIds, WorldSnapshot snap, ref long[] bestKey, int[] best)
        {
            if (i == open.Count)
            {
                long[] key = ScoreGroundCombatAssignment(open, cands, chosen, snap);
                if (bestKey == null || Lex(key, bestKey) < 0)
                {
                    bestKey = key;
                    Array.Copy(chosen, best, chosen.Length);
                }
                return;
            }

            chosen[i] = -1;
            RecurseGroundCombat(i + 1, open, cands, chosen, usedArmyIds, snap, ref bestKey, best);
            for (int c = 0; c < cands[i].Count; c++)
            {
                int aid = cands[i][c];
                if (usedArmyIds.Contains(aid)) continue;
                usedArmyIds.Add(aid);
                chosen[i] = c;
                RecurseGroundCombat(i + 1, open, cands, chosen, usedArmyIds, snap, ref bestKey, best);
                usedArmyIds.Remove(aid);
            }
            chosen[i] = -1;
        }

        private static long[] ScoreGroundCombatAssignment(List<FundedEntry> open, List<List<int>> cands,
            int[] chosen, WorldSnapshot snap)
        {
            int n = open.Count;
            int covered = 0;
            long priorityCoverage = 0;
            int actorDiscontinuity = 0;
            long activation = 0;
            long overkillPower = 0;
            long actorIdSum = 0;

            for (int i = 0; i < n; i++)
            {
                if (chosen[i] < 0) continue;
                int actorId = cands[i][chosen[i]];
                covered++;
                priorityCoverage += n - i;
                activation += GroundCombatActorActivation(snap, actorId);
                overkillPower += Mathf.RoundToInt(GroundCombatActorPower(snap, actorId) * 100f);
                actorIdSum += actorId;

                int? preferred = open[i].Mission.PreferredMoverArmyId;
                if (preferred.HasValue && actorId != preferred.Value && cands[i].Contains(preferred.Value))
                    actorDiscontinuity++;
            }

            var key = new long[6 + n];
            key[0] = -covered;
            key[1] = -priorityCoverage;
            key[2] = actorDiscontinuity;
            key[3] = activation;
            key[4] = overkillPower;
            key[5] = actorIdSum;
            for (int i = 0; i < n; i++)
                key[6 + i] = chosen[i] < 0 ? long.MaxValue : cands[i][chosen[i]];
            return key;
        }

        // Was byte-identical to ReconAssignmentPlanner's copy — body moved to AiV2Util.
        private static int Lex(long[] a, long[] b) => AiV2Util.Lex(a, b);

        public static ProvisioningResult Provision(PlayerSetupData player, PlayerRoot root, AiHandData hand,
            AiTurnContext ctx, ProvisioningSession session, FundedEntry funded)
        {
            MissionProposal m = funded?.Mission;
            if (m == null || ctx?.Map == null || root == null)
                return ProvisioningResult.Fail(ProvisionFailure.AssemblyInfeasible("no mission / map / root"));

            if (m.Kind == MissionKind.Raid)
                return RaidProvisioner.Provision(player, root, ctx, session, funded);
            if (m.Kind == MissionKind.ActiveDefence)
                return ActiveDefenceProvisioner.Provision(player, root, ctx, session, funded);
            if (m.Kind == MissionKind.Attack)
                return AttackProvisioner.Provision(player, root, ctx, session, funded);

            if (m.Kind == MissionKind.Economy && m.Target is EconomyMissionTarget economy)
                return ProvisionEconomy(player, root, hand, ctx, session, funded, economy);

            if (m.Kind == MissionKind.Development && m.Target is DevelopmentMissionTarget development)
                return ProvisionDevelopment(player, root, ctx, session, funded, development);

            if (m.Kind != MissionKind.Scout || !(m.Target is ScoutMissionTarget target))
                return ProvisioningResult.Fail(ProvisionFailure.AssemblyInfeasible("unsupported mission kind"));

            StableMissionKey key = StableMissionKey.For(m);
            bool surveil = target.Kind == ScoutTargetKind.Surveil;
            bool refresh = ReconScoutKinds.IsRefresh(target.Kind);

            if (!session.TryGetAssignedExecution(key, out ScoutExecutionCandidate exec))
                return ClassifyNoAssignment(session, key, target);

            if (exec.ExecutorKind != ScoutExecutorKind.Ground)
                return ProvisionAir(player, root, ctx, session, funded, exec, target, key);

            // Identity uses the destination SHELL from the start for a garrison candidate: the
            // source garrison id must never leak into MoverArmyId / claims / durable intent. The
            // pair Assignment already pinned (BuildCandidates: source garrison + a then-free
            // ReusableArmySelector shell) is re-resolved here as a pure PREFLIGHT probe —
            // Provisioning must never re-search for "any" shell, or two funded missions in the same
            // pass could silently agree on a container Assignment never reserved for both.
            int moverArmyId = exec.RequiresGarrisonExtraction ? exec.MaterializationArmyId : exec.Army.ArmyId;
            ArmyData garrisonArmy = null;
            ArmyData destinationShell = null;
            UnitData plannedExtractUnit = null;
            ArmyData army;

            if (exec.RequiresGarrisonExtraction)
            {
                garrisonArmy = ResolveArmy(player, exec.SourceGarrisonArmyId);
                plannedExtractUnit = garrisonArmy == null
                    ? null : AiArmyRoles.BestSparableGarrisonRecce(player, garrisonArmy);
                destinationShell = ResolveArmy(player, exec.MaterializationArmyId);
                // The live re-check goes through the SAME canonical
                // ReusableArmySelector.IsReusableShell predicate Assignment's own shell search is
                // built on (owner/controller/prison/garrison/ aviation/commitment-claim). It also
                // rejects a shell claimed earlier THIS session (another mission's MoverArmyId) and
                // one that already activated this turn — ArmyActions.TransferMember would otherwise
                // charge the incoming unit's ActivationApCost immediately against
                // root.ActionPoints, an AP spend this pass's ClaimedAp /
                // ProvisioningSession.ApClaimed accounting has no channel to report without
                // double-subtracting it. ActorCommitments here mirrors the exact construction
                // ReconAssignmentPlanner.AssignFunded used to admit this pair.
                ActorCommitments commitments = ActorCommitments.FromIntents(
                    MissionIntentRegistry.GetOrCreate(player).All
                        .Where(i => i != null && i.Status == IntentStatus.Active).ToList(),
                    session.Snapshot, null);
                if (garrisonArmy == null || plannedExtractUnit == null
                    || destinationShell == null
                    || !ReusableArmySelector.IsReusableShell(destinationShell, player, commitments)
                    || session.ClaimedArmyIds.Contains(destinationShell.Id)
                    || destinationShell.HasActivatedThisTurn)
                    return ProvisioningResult.Fail(ProvisionFailure.MoverContended(
                        $"assigned garrison #{exec.SourceGarrisonArmyId} / shell #{exec.MaterializationArmyId} "
                        + "is no longer a usable extraction pair"));
                army = null; // not a real mover until TransferMember commits, below
            }
            else
            {
                army = ResolveArmy(player, moverArmyId);
                if (army == null || army.Owner != player || army.Members.Count == 0
                    || !AiArmyRoles.IsSoloRecce(army) || army.CurrentMovement <= 0)
                    return ProvisioningResult.Fail(ProvisionFailure.MoverContended(
                        $"assigned mover #{moverArmyId} is no longer a usable solo Recce"));
            }

            HexCoord focus = target.FocusHex;
            HexCoord executionHex = exec.ExecutionHex;

            if (surveil)
            {
                int trackedId = target.Contact?.Army?.ArmyId ?? -1;
                if (trackedId < 0 || target.Contact.Source != ContactSource.Honest
                    || target.Contact.Knowledge != ContactKnowledge.LastKnown)
                    return ProvisioningResult.Fail(ProvisionFailure.TargetInvalidated(
                        "surveil target is no longer an honest last-known contact"));
                int baseline = target.Contact.LastObservedTurn;
                if (VisionSystem.IsVisible(player, focus) || HasFresherSighting(player, trackedId, baseline))
                    return ProvisioningResult.Fail(ProvisionFailure.TargetSatisfied(
                        $"tracked #{trackedId} already re-observed (focus ({focus.Q},{focus.R}), baseline turn {baseline})"));
                if (executionHex.Equals(focus))
                    return ProvisioningResult.Fail(ProvisionFailure.AssemblyInfeasible(
                        "surveil ExecutionHex == FocusHex — invariant violation"));
                if (HexGridMath.Distance(executionHex, focus) > exec.Army.EffectiveVisionRadius)
                    return ProvisioningResult.Fail(ProvisionFailure.NoExecutableStep(
                        $"mover #{moverArmyId} vision {exec.Army.EffectiveVisionRadius} no longer covers focus from vantage"));
                if (ScoutExecutionSafety.VantageBlockedNow(player, executionHex, ctx.TurnNumber,
                        target.NeedsStealth))
                    return ProvisioningResult.Fail(ProvisionFailure.NoExecutableStep(
                        $"vantage ({executionHex.Q},{executionHex.R}) is now occupied by a current force / foreign building"));
            }
            else if (refresh)
            {
                // A Refresh target was selected because frozen IntelAge was stale. Previously
                // Visited ground remains valid; only a NEW current observation completes it.
                if (ScoutObjectiveEvaluator.IsRefreshSatisfiedLive(player, focus))
                    return ProvisioningResult.Fail(ProvisionFailure.TargetSatisfied(
                        $"refresh focus ({focus.Q},{focus.R}) is already visible again"));
                if (!executionHex.Equals(focus))
                {
                    // Recon audit B2 — a vantage Refresh (the Attack observation need on a known
                    // hostile site): its defenders are the point of the look, never a reason to
                    // cancel it. The vantage itself gets Surveil's live checks.
                    if (HexGridMath.Distance(executionHex, focus) > exec.Army.EffectiveVisionRadius)
                        return ProvisioningResult.Fail(ProvisionFailure.NoExecutableStep(
                            $"mover #{moverArmyId} vision {exec.Army.EffectiveVisionRadius} no longer covers focus from vantage"));
                    if (ScoutExecutionSafety.VantageBlockedNow(player, executionHex, ctx.TurnNumber,
                            target.NeedsStealth))
                        return ProvisioningResult.Fail(ProvisionFailure.NoExecutableStep(
                            $"vantage ({executionHex.Q},{executionHex.R}) is now occupied by a current force / foreign building"));
                }
                else if (AiMapMemory.KnownEnemySightingAt(player, focus).HasValue)
                    return ProvisioningResult.Fail(ProvisionFailure.TargetInvalidated(
                        $"refresh focus ({focus.Q},{focus.R}) now holds a known army"));
            }
            else
            {
                if (VisionSystem.IsVisited(player, focus))
                    return ProvisioningResult.Fail(ProvisionFailure.TargetSatisfied(
                        $"focus ({focus.Q},{focus.R}) already visited — nothing left to discover there"));
                if (AiMapMemory.KnownEnemySightingAt(player, focus).HasValue)
                    return ProvisioningResult.Fail(ProvisionFailure.TargetInvalidated(
                        $"focus ({focus.Q},{focus.R}) now holds a known army"));
            }

            // Task 2 — preflight approach + AP purely off PRE-mutation data: the real mover for a
            // normal candidate, or the not-yet-extracted unit's own stats/hex for a garrison one
            // (the exact same coordinate-based primitive BuildCandidates already used to admit this
            // candidate — see ReconAssignmentPlanner.BuildCandidates' garrison-extraction branch).
            HexCoord fromHex = exec.RequiresGarrisonExtraction ? garrisonArmy.Hex : army.Hex;
            // ScoutExecutionSafety admits a vantage on a foreign structure that
            // knowledge says nobody is holding, so this preflight must ask the same question or
            // it would strand exactly the vantage the selector just approved.
            bool hasSafeApproach = exec.RequiresGarrisonExtraction
                ? SafeStepPathing.FindSafePath(ctx.Map, player, fromHex, executionHex,
                    plannedExtractUnit.MoveMax) != null
                : SafeStepPathing.FindNextSafeStep(ctx.Map, army, executionHex) != null;
            if (!hasSafeApproach)
                return ProvisioningResult.Fail(ProvisionFailure.NoExecutableStep(
                    $"no safe first step from ({fromHex.Q},{fromHex.R}) toward ({executionHex.Q},{executionHex.R})"));

            int activationAp = exec.RequiresGarrisonExtraction
                ? plannedExtractUnit.ActivationApCost
                : (army.HasActivatedThisTurn ? 0 : army.ActivationApCost);
            bool alreadyHidden = exec.RequiresGarrisonExtraction
                ? plannedExtractUnit.IsHidden
                : army.Members.Any(mem => mem.IsHidden);
            bool reserveStealth = target.Stealth == StealthRequirement.Required && !alreadyHidden;
            int stealthAp = reserveStealth ? StealthTransitionApCost : 0;
            float realNeed = activationAp + stealthAp;

            float eps = AiConfigV2.allocatorSliceEpsilon;
            float envelope = funded.Tentative.Ap;
            if (realNeed > envelope + eps)
                return ProvisioningResult.Fail(ProvisionFailure.EnvelopeTooSmall(realNeed,
                    $"mover #{moverArmyId} needs {N(realNeed)} AP, envelope is {N(envelope)}"));

            float turnApLeft = root.ActionPoints - session.ApClaimed;
            if (realNeed > turnApLeft + eps)
                return ProvisioningResult.Fail(ProvisionFailure.MoverContended(
                    $"turn AP exhausted: need {N(realNeed)}, {N(turnApLeft)} left after earlier claims"));

            // Task 2 — COMMIT. Only now, after every check above ran on pre-mutation data, does the
            // garrison Recce actually leave. `extractedUnit` tracks the EXACT UnitData that moved so
            // FailAfterRecce can send that same unit back rather than re-deriving "the" Recce from
            // the shell's roster (a Host-style shared container could, in principle, hold more than
            // one candidate unit — this project's Shell-tier extraction never does today, but the
            // tracked reference removes the ambiguity at the source instead of relying on that).
            UnitData extractedUnit = null;
            if (exec.RequiresGarrisonExtraction)
            {
                if (!ArmyActions.TransferMember(plannedExtractUnit, garrisonArmy, destinationShell, ctx.HexSelection, out string why))
                {
                    return ProvisioningResult.Fail(ProvisionFailure.MoverContended(
                        $"garrison #{exec.SourceGarrisonArmyId} extraction into shell #{exec.MaterializationArmyId} failed: {why}"));
                }
                extractedUnit = plannedExtractUnit;
                army = destinationShell;
                AiDebugLog.Write($"[AI][V2][Recon] extracted idle Recce {plannedExtractUnit.Name} "
                    + $"from garrison #{garrisonArmy.Id} into #{destinationShell.Id} for scouting duty");
            }

            // Every failure branch from here on must go through FailAfterRecce (mirrors Economy's
            // FailAfterHero) so a future check inserted below this line can never forget to unwind
            // the extraction and leave the Recce permanently stranded outside its garrison.
            ProvisioningResult FailAfterRecce(ProvisionFailure failure)
            {
                if (extractedUnit == null)
                    return ProvisioningResult.Fail(failure);
                bool rolledBack = ArmyActions.TransferMember(
                    extractedUnit, destinationShell, garrisonArmy, ctx.HexSelection, out string why);
                if (!rolledBack)
                    AiDebugLog.Write($"[AI][V2][Recon] garrison extraction rollback FAILED "
                        + $"#{destinationShell.Id}->#{garrisonArmy.Id}: {why}");
                bool stateChanged = !rolledBack;
                return ProvisioningResult.Fail(failure, stateChanged, stateChanged ? 1 : 0);
            }

            // Task 2 — the one genuine post-commit check TransferMember's own validation does not
            // already cover end-to-end: the resulting shell must actually BE a usable solo Recce. A
            // real (non-garrison) mover repeats the same cheap structural predicate already proven
            // above (harmless — nothing mutated it in between); a freshly extracted garrison Recce
            // rolls back through FailAfterRecce instead of leaving a phantom army downstream stages
            // were never told to expect.
            if (army == null || army.Owner != player || army.Members.Count == 0
                || !AiArmyRoles.IsSoloRecce(army) || army.CurrentMovement <= 0)
                return FailAfterRecce(ProvisionFailure.MoverContended(
                    $"assigned mover #{moverArmyId} is no longer a usable solo Recce"));

            return ProvisioningResult.Ok(new ProvisionedMission
            {
                Mission = m,
                Key = key,
                Kind = MissionKind.Scout,
                ScoutKind = target.Kind,
                MoverArmyId = moverArmyId,
                FocusHex = focus,
                ExecutionHex = executionHex,
                TrackedArmyId = surveil ? target.Contact.Army.ArmyId : (int?)null,
                BaselineObservedTurn = surveil ? target.Contact.LastObservedTurn : 0,
                ClaimedAp = realNeed,
                ClaimedPhysical = funded.PhysicalDraw,
                StealthApReserved = stealthAp > 0,
                RequiresStealth = target.NeedsStealth,
            }, extractedUnit != null ? 1 : 0);
        }

        // Round 3 (Problem 2) — PURE translation of ReconAssignmentPlanner.AssignFunded's already-
        // computed rejection reason into a ProvisionFailure. No eligibility / route / vantage
        // re-derivation happens here any more — "why couldn't this job be assigned" has exactly ONE
        // owner, ReconAssignmentPlanner, and this is just its vocabulary mapped onto Provisioning's.
        private static ProvisioningResult ClassifyNoAssignment(ProvisioningSession session,
            StableMissionKey key, ScoutMissionTarget target)
            => ProvisioningResult.Fail(AssignmentFailure(session, key, target));

        private static ProvisionFailure AssignmentFailure(ProvisioningSession session,
            StableMissionKey key, ScoutMissionTarget target)
        {
            bool needStealth = target.Stealth == StealthRequirement.Required;
            if (!session.TryGetAssignmentRejection(key, out ScoutAssignmentFailureReason reason))
                // Should not happen — AssignFunded rejects or accepts every mission it is handed.
                // Fail safe rather than re-deriving anything ourselves.
                return ProvisionFailure.MoverContended(
                    $"{key} had no assignment result this pass (assignment/provisioning desync)");

            switch (reason)
            {
                case ScoutAssignmentFailureReason.NoMoverExists:
                    return ProvisionFailure.NoMoverExists(
                        "no solo Recce" + (needStealth ? " with stealth capability" : "") + " on the map");
                case ScoutAssignmentFailureReason.NoObservationVantage:
                    return ProvisionFailure.NoObservationVantage(
                        $"no on-map vantage within any scout's vision of ({target.FocusHex.Q},{target.FocusHex.R})");
                case ScoutAssignmentFailureReason.NoExecutableStep:
                    return ProvisionFailure.NoExecutableStep(
                        $"eligible scout(s)/vantage exist but none reachable this turn toward "
                        + $"({target.FocusHex.Q},{target.FocusHex.R})");
                default:
                    return ProvisionFailure.MoverContended(
                        "a capable solo Recce exists but is spent / activated / claimed this cycle");
            }
        }

        private static bool HasFresherSighting(PlayerSetupData player, int trackedArmyId, int baselineTurn)
        {
            foreach (AiMapMemory.KnownEnemySighting s in AiMapMemory.AllKnownEnemySightings(player))
                if (s.ArmyId == trackedArmyId && s.SeenTurn > baselineTurn)
                    return true;
            return false;
        }

        // Was byte-identical in ReconAssignmentPlanner and (twice) in this file — moved to AiV2Util.
        private static ArmyData ResolveArmy(PlayerSetupData player, int armyId) =>
            AiV2Util.ResolveArmy(player, armyId);

        private static string N(float v) => v.ToString("0.##", CultureInfo.InvariantCulture);
    }
}
