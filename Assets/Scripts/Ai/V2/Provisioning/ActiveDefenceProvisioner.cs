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
    internal static class ActiveDefenceProvisioner
    {
        internal static ProvisioningResult Provision(PlayerSetupData player, PlayerRoot root,
            AiTurnContext ctx, ProvisioningSession session, FundedEntry funded)
        {
            MissionProposal mission = funded?.Mission;
            if (mission == null || !(mission.Target is ActiveDefenceMissionTarget target))
                return ProvisioningResult.Fail(ProvisionFailure.TargetInvalidated(
                    "active defence has no typed target"));

            if (target.Phase == ActiveDefencePhase.Return)
                return ProvisionReturn(player, root, ctx, session, funded, target);
            if (target.Phase == ActiveDefencePhase.AirSupport)
                return ProvisionAirSupport(player, root, ctx, session, funded, target);

            AiMapMemory.KnownEnemySighting? sighting = AiMapMemory.AllKnownEnemySightings(player)
                .Where(s => s.ArmyId == target.EnemyArmyId && s.Owner != null
                    && !s.Owner.IsNeutral && s.Owner != player)
                .Select(s => (AiMapMemory.KnownEnemySighting?)s).FirstOrDefault();
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
            IReadOnlyList<WorthIt.DefendingArmy> opposition = new[]
                { new WorthIt.DefendingArmy(sighting.Value.Defenders, sighting.Value.Commander) };
            GroundCombatAssaultOutcome assault = GroundCombatAssaultTransactionRunner.Run(
                new GroundCombatAssaultRequest
                {
                    Player = player, Root = root, Ctx = ctx, Session = session, Funded = funded,
                    Key = key, TargetHex = sighting.Value.Hex, Opposition = opposition,
                    DefenderHexDefenseBonus = 0f, LaneLabel = "active-defence",
                    Eps = AiConfigV2.allocatorSliceEpsilon,
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
            AiMapMemory.KnownEnemySighting? sighting = AiMapMemory.AllKnownEnemySightings(player)
                .Where(s => s.ArmyId == target.EnemyArmyId && s.Owner != null
                    && !s.Owner.IsNeutral && s.Owner != player)
                .Select(s => (AiMapMemory.KnownEnemySighting?)s).FirstOrDefault();
            if (!sighting.HasValue)
                return ProvisioningResult.Fail(ProvisionFailure.TargetInvalidated(
                    $"active defence air support: enemy #{target.EnemyArmyId} has no honest sighting"));
            if (!GroundCombatAirSupport.TryResolveWing(player, session, target.AirSupportArmyId.Value,
                    "active-defence", out AirSupportWing w, out ProvisionFailure wingFailure))
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
            ArmyData actor = AiV2Util.ResolveArmy(player, target.PrimaryArmyId.Value);
            if (actor == null || actor.Owner != player || actor.Members.Count == 0)
                return ProvisioningResult.Fail(ProvisionFailure.TargetInvalidated(
                    "active defence return actor is gone"));
            if (actor.Hex.Equals(target.ReturnHex.Value))
                return ProvisioningResult.Fail(ProvisionFailure.TargetSatisfied(
                    "active defence responder is already home"));
            if (session.ClaimedArmyIds.Contains(actor.Id))
                return ProvisioningResult.Fail(ProvisionFailure.MoverContended(
                    "active defence return actor is claimed"));
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

    }
}
