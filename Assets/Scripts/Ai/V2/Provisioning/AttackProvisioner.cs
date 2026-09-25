using System.Collections.Generic;
using System.Globalization;
using Game.Aviation;
using Game.Combat;
using Game.HexGrid;
using Game.Map;
using Game.Players;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  ATK §40/§46/§47 — PROVISIONING FOR THE ATTACK LANE.
    //
    //  Every physical procedure here belongs to the shared GroundCombat kernel: the assault is the
    //  one transactional assembly (GroundCombatAssaultTransactionRunner), the convoy and walk-home
    //  legs are the one pair of leg checks (GroundCombatLegChecks), and the actor was already
    //  chosen by the ONE batch assignment (PrepareGroundCombatAssignments). This class only decides
    //  which fight the lane is in and fills the payload Execution reads.
    // ===========================================================================================
    internal static class AttackProvisioner
    {
        public static ProvisioningResult Provision(PlayerSetupData player, PlayerRoot root,
            AiTurnContext ctx, ProvisioningSession session, FundedEntry funded)
        {
            MissionProposal m = funded.Mission;
            if (!(m.Target is AttackMissionTarget target))
                return ProvisioningResult.Fail(ProvisionFailure.AssemblyInfeasible(
                    "attack mission has no AttackMissionTarget"));

            StableMissionKey key = StableMissionKey.For(m);
            float eps = AiConfigV2.allocatorSliceEpsilon;

            switch (target.Phase)
            {
                case AttackMissionPhase.RecoveryReturn:
                    return ProvisionWalkHome(player, root, ctx, session, funded, target, key, eps,
                        target.PrimaryArmyId, "recovery primary");
                case AttackMissionPhase.SupportReturn:
                    return ProvisionWalkHome(player, root, ctx, session, funded, target, key, eps,
                        target.SupportArmyId, "support");
                case AttackMissionPhase.GatherReturn:
                    return ProvisionWalkHome(player, root, ctx, session, funded, target, key, eps,
                        target.SupportArmyId, "gather donor");
                // Audit F7 — a Gather leg is the same convoy + handoff with a pinned support.
                case AttackMissionPhase.Reinforcement:
                case AttackMissionPhase.Gather:
                    return ProvisionReinforcement(player, root, ctx, session, funded, target, key, eps);
            }

            return ProvisionAssault(player, root, ctx, session, funded, target, key, eps);
        }

        // §25 — the target must still be the thing we set out to capture, answered from honest
        // memory by the one owner. "Now ours" is a satisfied objective, not a failure.
        private static ProvisioningResult ProvisionAssault(PlayerSetupData player, PlayerRoot root,
            AiTurnContext ctx, ProvisioningSession session, FundedEntry funded,
            AttackMissionTarget target, StableMissionKey key, float eps)
        {
            WorldSnapshot snap = session.Snapshot;
            AttackObjectiveEvaluator.AttackTargetStatus status =
                AttackObjectiveEvaluator.EvaluateTarget(snap, target.Target);
            if (status == AttackObjectiveEvaluator.AttackTargetStatus.Captured)
                return ProvisioningResult.Fail(ProvisionFailure.TargetSatisfied(
                    $"attack target {target.Target.DiagnosticLabel} is already ours"));
            if (status == AttackObjectiveEvaluator.AttackTargetStatus.Invalidated)
                return ProvisioningResult.Fail(ProvisionFailure.TargetInvalidated(
                    $"attack target {target.Target.DiagnosticLabel} is no longer a hostile Attack structure "
                    + "under its expected owner"));

            HexCoord targetHex = target.Target.Hex;
            // Re-read the site from the CURRENT snapshot rather than trusting the proposal's frozen
            // copy: a fresh observation between planning and provisioning is exactly the kind of
            // honest news that must reach the estimator.
            IReadOnlyList<WorthIt.DefendingArmy> opposition =
                AttackObjectiveEvaluator.KnownSiteOpposition(snap, targetHex);
            float hexBonus = AttackObjectiveEvaluator.KnownSiteDefenceBonus(snap, ctx?.Map, targetHex);

            GroundCombatAssaultOutcome assault = GroundCombatAssaultTransactionRunner.Run(
                new GroundCombatAssaultRequest
                {
                    Player = player, Root = root, Ctx = ctx, Session = session, Funded = funded,
                    Key = key, TargetHex = targetHex, Opposition = opposition,
                    DefenderHexDefenseBonus = hexBonus, LaneLabel = "attack", Eps = eps,
                });
            if (!assault.Success)
                return assault.Failure;

            target.PrimaryArmyId = assault.Host.Id;
            target.DestinationHex = targetHex;
            target.DefenderHexDefenseBonus = hexBonus;
            target.DefenderCount = WorthIt.UnitsOf(opposition).Count;
            target.ProjectedWinChance = assault.Plan.ProjectedWinChance;
            target.CoversAllDefenders = assault.Plan.CoversAllDefenders;

            return ProvisioningResult.Ok(new ProvisionedMission
            {
                Mission = funded.Mission,
                Key = key,
                Kind = MissionKind.Attack,
                MoverArmyId = assault.Host.Id,
                FocusHex = targetHex,
                ExecutionHex = targetHex,
                AttackTarget = target,
                ClaimedPhysical = funded.PhysicalDraw,
                ClaimedAp = assault.ActualAp,
                StealthApReserved = false,
            }, assault.AppliedTransfers, otherMutation: assault.CommanderReordered);
        }

        private static ProvisioningResult ProvisionWalkHome(PlayerSetupData player, PlayerRoot root,
            AiTurnContext ctx, ProvisioningSession session, FundedEntry funded,
            AttackMissionTarget target, StableMissionKey key, float eps,
            int? moverArmyId, string roleLabel)
        {
            HexCoord home = target.DestinationHex;
            GroundCombatLegCheck check = GroundCombatLegChecks.ValidateWalkHome(player, root, ctx,
                session, funded, key, eps, moverArmyId, home, "attack", roleLabel);
            if (!check.Ok)
                return check.Failure;

            AiDebugLog.Write($"[AI][V2]   attack provision [{funded.Mission.AttemptId}] {key} — OK "
                + $"{target.Phase} {roleLabel} #{check.Mover.Id} -> ({home.Q},{home.R}) "
                + $"ap {N(check.ActivationAp)}");
            return ProvisioningResult.Ok(new ProvisionedMission
            {
                Mission = funded.Mission,
                Key = key,
                Kind = MissionKind.Attack,
                MoverArmyId = check.Mover.Id,
                FocusHex = home,
                ExecutionHex = home,
                AttackTarget = target,
                ClaimedPhysical = funded.PhysicalDraw,
                ClaimedAp = check.ActivationAp,
                StealthApReserved = false,
            });
        }

        private static ProvisioningResult ProvisionReinforcement(PlayerSetupData player,
            PlayerRoot root, AiTurnContext ctx, ProvisioningSession session, FundedEntry funded,
            AttackMissionTarget target, StableMissionKey key, float eps)
        {
            if (!target.PrimaryArmyId.HasValue)
                return ProvisioningResult.Fail(ProvisionFailure.TargetInvalidated(
                    "attack reinforcement has no primary assigned"));
            ArmyData primary = AiV2Util.ResolveArmy(player, target.PrimaryArmyId.Value);
            if (primary == null || primary.Owner != player || primary.Members.Count == 0
                || primary.IsPrison || primary.IsAirfield || AviationRules.IsAirArmy(primary))
                return ProvisioningResult.Fail(ProvisionFailure.TargetInvalidated(
                    $"attack reinforcement primary #{target.PrimaryArmyId.Value} is gone or no longer a field army"));

            // An UNPINNED leg takes its actor straight out of the SAME batch-assignment solve the
            // assault legs use — never a private free-army search.
            int supportArmyId;
            if (!target.SupportArmyId.HasValue)
            {
                if (!session.TryGetAssignedGroundCombatActor(key, out supportArmyId))
                    return ProvisioningResult.Fail(ProvisionFailure.NoMoverExists(
                        $"attack reinforcement for primary #{target.PrimaryArmyId.Value} has no existing "
                        + "free support army assigned this cycle"));
            }
            else
            {
                supportArmyId = target.SupportArmyId.Value;
            }

            WorldSnapshot snap = session.Snapshot;
            HexCoord targetHex = target.Target.Hex;
            IReadOnlyList<WorthIt.DefendingArmy> opposition =
                AttackObjectiveEvaluator.KnownSiteOpposition(snap, targetHex);
            float hexBonus = AttackObjectiveEvaluator.KnownSiteDefenceBonus(snap, ctx?.Map, targetHex);

            GroundCombatLegCheck check = GroundCombatLegChecks.ValidateReinforcement(player, root,
                ctx, session, funded, key, eps, primary, supportArmyId, opposition, hexBonus,
                "attack", out bool atRendezvous);
            if (!check.Ok)
                return check.Failure;

            // The primary must not be handed to another mission while the convoy is in transit.
            session.ClaimedArmyIds.Add(primary.Id);

            target.SupportArmyId = check.Mover.Id;
            target.DestinationHex = primary.Hex;
            target.DefenderHexDefenseBonus = hexBonus;
            target.DefenderCount = WorthIt.UnitsOf(opposition).Count;

            AiDebugLog.Write($"[AI][V2]   attack provision [{funded.Mission.AttemptId}] {key} — OK "
                + $"{(target.Phase == AttackMissionPhase.Gather ? "GATHER" : "REINFORCE")} "
                + $"support #{check.Mover.Id} -> primary #{primary.Id} at ({primary.Hex.Q},{primary.Hex.R}) "
                + $"{(atRendezvous ? "HANDOFF" : "TRANSIT")} ap {N(check.ActivationAp)}");
            return ProvisioningResult.Ok(new ProvisionedMission
            {
                Mission = funded.Mission,
                Key = key,
                Kind = MissionKind.Attack,
                MoverArmyId = check.Mover.Id,
                FocusHex = primary.Hex,
                ExecutionHex = primary.Hex,
                AttackTarget = target,
                AttackHandoffReady = atRendezvous,
                ClaimedPhysical = funded.PhysicalDraw,
                ClaimedAp = check.ActivationAp,
                StealthApReserved = false,
            });
        }

        private static string N(float v) => v.ToString("0.##", CultureInfo.InvariantCulture);
    }
}
