using System;
using System.Collections.Generic;
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
    internal static partial class ActiveDefenceProvisioner
    {
        internal static ProvisioningResult Provision(PlayerSetupData player, PlayerRoot root,
            AiTurnContext ctx, ProvisioningSession session, FundedEntry funded)
        {
            MissionProposal mission = funded?.Mission;
            if (mission == null || !(mission.Target is ActiveDefenceMissionTarget target))
                return ProvisioningResult.Fail(ProvisionFailure.TargetInvalidated(
                    "active defence has no typed target"));

            if (target.RequiresSortieMaterialization)
                return ProvisionSortie(player, root, ctx, session, funded, target);
            if (target.Phase == ActiveDefencePhase.Return)
                return ProvisionReturn(player, root, ctx, session, funded, target);
            if (target.Phase == ActiveDefencePhase.AirSupport)
                return ProvisionAirSupport(player, root, ctx, session, funded, target);

            AiMapMemory.KnownEnemySighting? sighting = AiMapMemory.KnownHostileSightingOf(player, target.EnemyArmyId);
            if (!sighting.HasValue)
                return ProvisioningResult.Fail(ProvisionFailure.TargetInvalidated(
                    $"active defence enemy #{target.EnemyArmyId} has no honest sighting"));
            // AiMapMemory.OnVisibilityChanged is the single canonical, fog-honest writer for
            // EnemySightings: it removes an entry the instant its hex is re-observed empty and
            // otherwise leaves a last-known sighting untouched while the hex stays fogged.
            // `sighting.HasValue` above is therefore the complete answer to "is this enemy still a
            // live threat, per what we honestly know". Do not re-derive it from a true-world
            // ArmyRegistry read: that disagrees with memory exactly when the sighting is
            // stale-but-unobserved, and the false "gone" makes the objective re-create and
            // re-satisfy every pass with no new information.
            bool hexVisibleNow = VisionSystem.IsVisible(player, sighting.Value.Hex);
            AiDebugLog.Write($"[AGG][ActiveDefence] enemy=#{target.EnemyArmyId} "
                + $"contact={(hexVisibleNow ? "LIVE" : "LAST_KNOWN")} "
                + $"hexVisible={hexVisibleNow.ToString().ToLowerInvariant()} "
                + "decision=APPROACH_LAST_KNOWN");

            // The intercept is one more ground-combat assault: the ONE transactional assembly
            // (GroundCombatAssaultTransactionRunner) binds the actor the batch assignment picked,
            // re-plans it at GroundCombatAdmissionPolicy.AssaultGate (the continuation floor for the
            // pinned Hard incumbent, the fresh gate otherwise), validates and applies the same-hex
            // transfers atomically and reconciles the funded AP. This lane supplies only its fight.
            StableMissionKey key = StableMissionKey.For(mission);
            // The same fight admission priced (KnownOpposition.OfEnemyArmy): the
            // enemy (with its known hex bonus) plus every other known body on its hex.
            var opposition = new List<WorthIt.DefendingArmy>
            {
                new WorthIt.DefendingArmy(sighting.Value.Defenders, sighting.Value.Commander,
                    AiMapMemory.KnownHexDefenseBonusFor(player, ctx?.Map, sighting.Value.Hex,
                        sighting.Value.Owner), sighting.Value.ArmyId),
            };
            KnownOpposition.AddCoOccupants(session.Snapshot, sighting.Value.Hex, target.EnemyArmyId, opposition);
            bool immediate = target.InterceptPurpose == ActiveDefenceInterceptPurpose.ImmediateOpportunity;
            if (immediate && (!hexVisibleNow || target.ImmediateTurn != session.Snapshot?.TurnNumber))
                return ProvisioningResult.Fail(ProvisionFailure.TargetInvalidated(
                    $"immediate intercept of #{target.EnemyArmyId} is no longer a visible same-turn opportunity"));
            if (target.IsMaterializedSortie)
                opposition = SortieOpposition(player, ctx, target.Sortie);
            GroundCombatAssaultOutcome assault = GroundCombatAssaultTransactionRunner.Run(
                new GroundCombatAssaultRequest
                {
                    Player = player, Root = root, Ctx = ctx, Session = session, Funded = funded,
                    Key = key, TargetHex = sighting.Value.Hex, Opposition = opposition,
                    DefenderHexDefenseBonus = 0f, LaneLabel = "active-defence",
                    Eps = AiConfigV2.allocatorSliceEpsilon,
                    // Re-prove timing on the roster that will actually march, before transfers/AP.
                    // Strategic defence keeps its damage deadline; ImmediateOpportunity keeps its
                    // stronger same-turn reach rule (and its separate sortie witness).
                    PreMutationVeto = (host, units) => !immediate
                        ? StrategicReachVeto(session.Snapshot, host, units, sighting.Value.Hex, target.EnemyEta)
                        : target.IsMaterializedSortie
                            ? (TryEvaluateSortie(player, ctx, target, host.Hex, units, out string why) ? null : why)
                            : ImmediateReachVeto(ctx, player, host, units, sighting.Value.Hex, target.EnemyArmyId),
                });
            if (!assault.Success)
                return assault.Failure;

            ArmyData host = assault.Host;
            target.LastKnownHex = sighting.Value.Hex;
            target.LastObservedTurn = sighting.Value.SeenTurn;
            target.PrimaryArmyId = host.Id;
            target.ProjectedWinChance = assault.Plan.ProjectedWinChance;
            target.CoversAllDefenders = assault.Plan.CoversAllDefenders;
            AiDebugLog.Write($"[AI][V2][ActiveDefence][Provision] decision=OK enemy={target.EnemyArmyId} "
                + $"actor={host.Id} transfers={assault.AppliedTransfers} "
                + $"win={assault.Plan.ProjectedWinChance:0.00}");
            return ProvisioningResult.Ok(new ProvisionedMission
            {
                Mission = mission, Key = key, Kind = MissionKind.ActiveDefence,
                MoverArmyId = host.Id, FocusHex = target.LastKnownHex,
                ExecutionHex = target.LastKnownHex, ActiveDefenceTarget = target,
                ClaimedPhysical = funded.PhysicalDraw,
                // The reconciled cost of the force that actually exists now — asserted equal to
                // the projected/funded figure, so allocator, revalidator and executor all debit
                // this one number exactly once.
                ClaimedAp = assault.ActualAp,
            }, assault.AppliedTransfers, otherMutation: assault.CommanderReordered);
        }

        // The transaction passes its final roster AFTER legal shedding/recruitment and before
        // the first mutation. A slower or spent recruit must not turn a funded defence into a late march.
        internal static string StrategicReachVeto(WorldSnapshot snap, ArmyData host,
            IReadOnlyList<UnitData> units, HexCoord enemyHex, int? enemyEta)
        {
            if (host == null || units == null || units.Count == 0)
                return "strategic_intercept_roster_missing";
            int current = ArmyData.ComputeCurrentMovement(units), maximum = ArmyData.ComputeMaxMovement(units);
            bool timely = ActiveDefenceObjectiveEvaluator.CanInterceptRosterBeforeThreat(snap,
                host.Hex, enemyHex, enemyEta, current, maximum, out int eta);
            return timely ? null : $"strategic_intercept_deadline_missed eta={eta} "
                + $"enemyEta={enemyEta?.ToString() ?? "unknown"} mp={current}/{maximum}";
        }

        // null = the assembled roster reaches the enemy and begins the battle with its CURRENT movement.
        private static string ImmediateReachVeto(AiTurnContext ctx, PlayerSetupData player, ArmyData host,
            IReadOnlyList<Game.Units.UnitData> units, HexCoord enemyHex, int enemyId)
        {
            int mp = ArmyData.ComputeCurrentMovement(units), max = ArmyData.ComputeMaxMovement(units);
            bool ok = ActiveDefenceObjectiveEvaluator.CanReachNow(ctx.Map, player, host.Hex, enemyHex,
                mp, max, out int cost);
            AiDebugLog.Write($"[AI][V2][ActiveDefence][Immediate][Provision] enemy={enemyId} actor={host.Id} "
                + $"roster={units.Count} mp={mp}/{max} routeCost={(cost == int.MaxValue ? "none" : cost.ToString())} "
                + $"decision={(ok ? "REACH_OK" : "REACH_REFUSED")}");
            return ok ? null : $"immediate_unreachable mp={mp} routeCost={(cost == int.MaxValue ? "none" : cost.ToString())}";
        }

        // The wing striking the threat: the one GroundCombatAirSupport provisioning, aimed at the
        // threat's honest last-known hex (it follows the threat). Nothing about the ground
        // response is required — no PrimaryArmyId, no intercept win chance.
        private static ProvisioningResult ProvisionAirSupport(PlayerSetupData player, PlayerRoot root,
            AiTurnContext ctx, ProvisioningSession session, FundedEntry funded,
            ActiveDefenceMissionTarget target)
        {
            if (!target.AirSupportArmyId.HasValue)
                return ProvisioningResult.Fail(ProvisionFailure.TargetInvalidated(
                    "active defence air support has no wing"));
            AiMapMemory.KnownEnemySighting? sighting = AiMapMemory.KnownHostileSightingOf(player, target.EnemyArmyId);
            if (!sighting.HasValue)
                return ProvisioningResult.Fail(ProvisionFailure.TargetInvalidated(
                    $"active defence air support: enemy #{target.EnemyArmyId} has no honest sighting"));
            if (!GroundCombatAirSupport.TryResolveWing(player, session, target.AirSupportArmyId.Value,
                    "active-defence", out AirSupportWing w, out ProvisionFailure wingFailure,
                    defenceHandoffEnemyId: target.EnemyArmyId))
                return ProvisioningResult.Fail(wingFailure);
            HexCoord targetHex = sighting.Value.Hex;
            float eps = AiConfigV2.allocatorSliceEpsilon;
            if (!GroundCombatAirSupport.TryFinishWing(player, root, ctx, session, funded, w, targetHex,
                    "active-defence", eps, out HexCoord landing, out float ap, out float energy,
                    out ProvisionFailure finishFailure))
                return ProvisioningResult.Fail(finishFailure);
            target.LastKnownHex = targetHex;
            target.LastObservedTurn = sighting.Value.SeenTurn;
            target.AirSupportLandingHex = landing;
            return ProvisioningResult.Ok(new ProvisionedMission
            {
                Mission = funded.Mission, Key = StableMissionKey.For(funded.Mission),
                Kind = MissionKind.ActiveDefence, MoverArmyId = w.Wing.Id,
                FocusHex = targetHex, ExecutionHex = targetHex,
                ActiveDefenceTarget = target,
                ClaimedAp = ap, ClaimedEnergy = energy,
                ClaimedPhysical = new ResourceVector(0f, 0f, energy, 0f, 0f),
            });
        }

        private static ProvisioningResult ProvisionReturn(PlayerSetupData player, PlayerRoot root,
            AiTurnContext ctx, ProvisioningSession session, FundedEntry funded,
            ActiveDefenceMissionTarget target)
        {
            if (!target.PrimaryArmyId.HasValue || !target.ReturnHex.HasValue)
                return ProvisioningResult.Fail(ProvisionFailure.TargetInvalidated(
                    "active defence return has no actor or home"));
            if (target.ReturnPurpose == ActiveDefenceReturnPurpose.GarrisonRelief)
                return ProvisionGarrisonRelief(player, root, ctx, session, funded, target);
            ArmyData actor = AiV2Util.ResolveArmy(player, target.PrimaryArmyId.Value);
            if (!AiV2Util.IsLiveOwnArmy(actor, player))
                return ProvisioningResult.Fail(ProvisionFailure.TargetInvalidated(
                    "active defence return actor is gone"));
            if (actor.Hex.Equals(target.ReturnHex.Value))
                return ProvisioningResult.Fail(ProvisionFailure.TargetSatisfied(
                    "active defence responder is already home"));
            if (session.ExcludedForGroundCombat(funded.Mission).Contains(actor.Id))
                return ProvisioningResult.Fail(ProvisionFailure.MoverContended(
                    "active defence return actor is claimed"));
            WorldSnapshot snap = session.Snapshot;
            if (snap?.Self?.BaseHexes?.Contains(target.ReturnHex.Value) != true)
                return ProvisioningResult.Fail(ProvisionFailure.TargetInvalidated(
                    "protected_base_lost"));
            if (target.ReturnPurpose == ActiveDefenceReturnPurpose.RegroupForAsset)
            {
                IReadOnlyList<MissionIntent> intents = WorldSnapshot.IntentsOf(session.Snapshot).ToList();
                ActiveDefenceObjective objective = ActiveDefenceObjectiveEvaluator.ForTrackedEnemy(snap,
                    target.EnemyArmyId);
                ActiveDefenceResponse response = objective == null
                    || !objective.Target.ProtectedAssetHex.Equals(target.ReturnHex.Value) ? null
                    : AssessRegroupForProvisioning(snap, funded.Mission, session, intents);
                if (response?.Kind == ActiveDefenceResponseKind.Shortage)
                    return ProvisioningResult.Fail(ProvisionFailure.NoExecutableStep(
                        "regroup_funded_cohort_insufficient"));
                if (response?.Kind != ActiveDefenceResponseKind.Regroup
                    || !response.ReinforcementArmyIds.Contains(actor.Id))
                    return ProvisioningResult.Fail(ProvisionFailure.TargetInvalidated(
                        "regroup_no_longer_needed"));
            }
            else
            {
                ArmySnapshot current = snap.Self.Armies.FirstOrDefault(a => a.ArmyId == actor.Id);
                var committed = ActorCommitments.FromIntents(
                    WorldSnapshot.IntentsOf(session.Snapshot), snap, null).ClaimedArmyIdSet;
                committed.UnionWith(session.ExcludedForGroundCombat(funded.Mission));
                committed.Remove(actor.Id);
                if (!ActiveDefenceObjectiveEvaluator.NeedsSafeWithdrawal(snap, current)
                    || !ActiveDefenceObjectiveEvaluator.SafeWithdrawalBase(snap, current, committed,
                        target.ReturnHex).Equals(target.ReturnHex))
                    return ProvisioningResult.Fail(ProvisionFailure.TargetInvalidated(
                        "safe_withdrawal_released_or_home_unsafe"));
            }
            if (SafeStepPathing.FindNextSafeStep(ctx.Map, actor, target.ReturnHex.Value,
                    profile: SafeRouteProfile.Combat) == null)
                return ProvisioningResult.Fail(ProvisionFailure.NoExecutableStep(
                    "active defence responder has no safe return step"));
            int ap = actor.HasActivatedThisTurn ? 0 : actor.ActivationApCost;
            if (ap > funded.Tentative.Ap + AiConfigV2.allocatorSliceEpsilon)
                return ProvisioningResult.Fail(ProvisionFailure.EnvelopeTooSmall(ap,
                    "active defence return AP envelope is stale"));
            return ProvisioningResult.Ok(new ProvisionedMission
            {
                Mission = funded.Mission, Key = StableMissionKey.For(funded.Mission),
                Kind = MissionKind.ActiveDefence, MoverArmyId = actor.Id,
                FocusHex = target.ReturnHex.Value, ExecutionHex = target.ReturnHex.Value,
                ActiveDefenceTarget = target, ClaimedPhysical = funded.PhysicalDraw,
                ClaimedAp = ap,
            });
        }

        // GarrisonRelief. A fresh leg mutates nothing here: it checks the pinned pair (donor garrison,
        // spare bodies, empty shell) and a safe route for the detachment and hands the plan to the
        // executor, which moves the bodies as its own terminal step (the one-mutation-per-step
        // contract the Economy / Development extraction keeps). That step makes the leg durable, so
        // the loaded shell is claimed from the moment it holds the bodies. A leg already under way
        // binds its loaded shell like any Return mover.
        private static ProvisioningResult ProvisionGarrisonRelief(PlayerSetupData player, PlayerRoot root,
            AiTurnContext ctx, ProvisioningSession session, FundedEntry funded,
            ActiveDefenceMissionTarget target)
        {
            HexCoord home = target.ReturnHex.Value;
            WorldSnapshot snap = session.Snapshot;
            if (snap?.Self?.BaseHexes?.Contains(home) != true)
                return ProvisioningResult.Fail(ProvisionFailure.TargetInvalidated("relief_base_lost"));
            ArmyData shell = AiV2Util.ResolveArmy(player, target.PrimaryArmyId.Value);
            if (shell == null || shell.Owner != player)
                return ProvisioningResult.Fail(ProvisionFailure.TargetInvalidated(
                    "relief detachment is gone"));
            if (session.ExcludedForGroundCombat(funded.Mission).Contains(shell.Id))
                return ProvisioningResult.Fail(ProvisionFailure.MoverContended(
                    "relief detachment is claimed"));

            if (target.ReliefDonorGarrisonId.HasValue)
            {
                ActorCommitments commitments = ActorCommitments.FromIntents(
                    WorldSnapshot.IntentsOf(snap).Where(i => i != null && i.Status == IntentStatus.Active).ToList(),
                    snap, null);
                if (session.ClaimedArmyIds.Contains(shell.Id))
                    return ProvisioningResult.Fail(ProvisionFailure.MoverContended(
                        $"relief shell #{shell.Id} is claimed this pass"));
                if (!TryResolveReliefExtraction(player, target, shell, commitments,
                        out ArmyData donor, out List<UnitData> units, out string why))
                    return ProvisioningResult.Fail(ProvisionFailure.MoverContended(
                        $"relief donor #{target.ReliefDonorGarrisonId} / shell #{shell.Id}: {why}"));
                if (SafeStepPathing.FindSafePath(ctx.Map, player, donor.Hex, home,
                        units.Min(u => u.PermanentMoveMax), SafeRouteProfile.Combat) == null)
                    return ProvisioningResult.Fail(ProvisionFailure.NoExecutableStep(
                        "relief detachment would have no safe route"));
                // The extraction itself is free (an empty, unactivated shell; a garrison never
                // activates); the walk is the next step's activation.
                return ProvisioningResult.Ok(new ProvisionedMission
                {
                    Mission = funded.Mission, Key = StableMissionKey.For(funded.Mission),
                    Kind = MissionKind.ActiveDefence, MoverArmyId = shell.Id,
                    FocusHex = home, ExecutionHex = home,
                    ActiveDefenceTarget = target, ClaimedPhysical = funded.PhysicalDraw,
                    ClaimedAp = 0f,
                });
            }

            if (shell.Members.Count == 0)
                return ProvisioningResult.Fail(ProvisionFailure.TargetInvalidated("relief detachment is empty"));
            if (shell.Hex.Equals(home))
                return ProvisioningResult.Fail(ProvisionFailure.TargetSatisfied("relief detachment is already there"));
            if (SafeStepPathing.FindNextSafeStep(ctx.Map, shell, home, profile: SafeRouteProfile.Combat) == null)
                return ProvisioningResult.Fail(ProvisionFailure.NoExecutableStep("relief detachment has no safe step"));
            int ap = shell.HasActivatedThisTurn ? 0 : shell.ActivationApCost;
            if (ap > funded.Tentative.Ap + AiConfigV2.allocatorSliceEpsilon)
                return ProvisioningResult.Fail(ProvisionFailure.EnvelopeTooSmall(ap,
                    "relief detachment AP envelope is stale"));
            return ProvisioningResult.Ok(new ProvisionedMission
            {
                Mission = funded.Mission, Key = StableMissionKey.For(funded.Mission),
                Kind = MissionKind.ActiveDefence, MoverArmyId = shell.Id,
                FocusHex = home, ExecutionHex = home,
                ActiveDefenceTarget = target, ClaimedPhysical = funded.PhysicalDraw,
                ClaimedAp = ap,
            });
        }

        // THE live check of a pinned relief extraction, read by Provisioning (preflight) and by the
        // executor right before it moves the bodies: the donor is our garrison on the shell's hex,
        // every planned body is still in it, its floor lets them all go together
        // (AiArmyRoles.CanSpareGarrisonMembers) and the shell is still an empty, unclaimed,
        // unactivated reusable container. `commitments` null skips only the claim test.
        internal static bool TryResolveReliefExtraction(PlayerSetupData player,
            ActiveDefenceMissionTarget target, ArmyData shell, ActorCommitments commitments,
            out ArmyData donor, out List<UnitData> units, out string why)
        {
            return GarrisonSortieProvisioning.TryResolveExtraction(player, target.ReliefDonorGarrisonId, shell?.Hex,
                target.ReliefUnitRuntimeIds, shell, commitments, false, out donor, out units, out why);
        }

        // Re-evaluate the funded cohort together, but bind only this leg's own actor. The
        // canonical exclusion view still rejects every independent operation and contested peer.
        internal static ActiveDefenceResponse AssessRegroupForProvisioning(WorldSnapshot snap,
            MissionProposal mission, ProvisioningSession session, IReadOnlyList<MissionIntent> intents)
        {
            if (!(mission?.Target is ActiveDefenceMissionTarget target)) return null;
            var fundedActors = new HashSet<int>();
            foreach (MissionProposal peer in session.PinnedGroundCombatLegs.Concat(new[] { mission }))
            {
                if (!(peer.Target is ActiveDefenceMissionTarget leg)
                    || leg.Phase != ActiveDefencePhase.Return
                    || leg.ReturnPurpose != ActiveDefenceReturnPurpose.RegroupForAsset
                    || leg.EnemyArmyId != target.EnemyArmyId
                    || !leg.ProtectedAssetHex.Equals(target.ProtectedAssetHex)
                    || !leg.ReturnHex.Equals(target.ReturnHex) || !leg.PrimaryArmyId.HasValue) continue;
                int id = leg.PrimaryArmyId.Value;
                bool ownSuccess = session.Successful.TryGetValue(StableMissionKey.For(peer), out var bound)
                    && bound.MoverArmyId == id && bound.Kind == MissionKind.ActiveDefence
                    && bound.ActiveDefenceTarget.Phase == ActiveDefencePhase.Return
                    && bound.ActiveDefenceTarget.ReturnPurpose == ActiveDefenceReturnPurpose.RegroupForAsset
                    && bound.ActiveDefenceTarget.EnemyArmyId == leg.EnemyArmyId;
                if (!ownSuccess && session.ExcludedForGroundCombat(peer).Contains(id)) continue;
                fundedActors.Add(id);
            }
            var committed = ActorCommitments.FromIntents(intents, snap, null).ClaimedArmyIdSet;
            committed.UnionWith(session.ExcludedForGroundCombat(mission));
            var objective = ActiveDefenceObjectiveEvaluator.ForTrackedEnemy(snap, target.EnemyArmyId);
            return objective == null ? null : ActiveDefenceObjectiveEvaluator.AssessResponse(snap,
                objective, committed, ActiveDefenceObjectiveEvaluator.WithdrawingArmyIds(intents),
                null, intents, fundedActors);
        }

    }
}
