using System.Collections.Generic;
using System.Globalization;
using System.Linq;
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
                case AttackMissionPhase.AirSupport:
                    return ProvisionAirSupport(player, root, ctx, session, funded, target, key, eps);
                // Audit F7 — a Gather leg is the same convoy + handoff with a pinned support.
                // T01 — a host-side preparation step has no support; it binds / fills the host.
                case AttackMissionPhase.Gather when target.PreparationStep != AttackPreparationStep.None:
                    return ProvisionPreparation(player, root, session, funded, target, key, eps);
                case AttackMissionPhase.Reinforcement:
                case AttackMissionPhase.Gather:
                    return ProvisionReinforcement(player, root, ctx, session, funded, target, key, eps);
            }

            return ProvisionAssault(player, root, ctx, session, funded, target, key, eps);
        }

        // The support wing's sortie: the one GroundCombatAirSupport provisioning. The site must
        // still be the operation's target with fresh defenders (unless the wing is already on its
        // way home); the strike is judged on the primary's own sequential fight at the site.
        private static ProvisioningResult ProvisionAirSupport(PlayerSetupData player, PlayerRoot root,
            AiTurnContext ctx, ProvisioningSession session, FundedEntry funded,
            AttackMissionTarget target, StableMissionKey key, float eps)
        {
            WorldSnapshot snap = session.Snapshot;
            if (!target.AirSupportArmyId.HasValue || !target.AirSupportLandingHex.HasValue)
                return ProvisioningResult.Fail(ProvisionFailure.TargetInvalidated(
                    "attack air support requires one bound wing and its landing base"));
            HexCoord targetHex = target.Target.Hex;
            if (!GroundCombatAirSupport.TryResolveWing(player, session, target.AirSupportArmyId.Value,
                    targetHex, "attack", out AirSupportWing w, out ProvisionFailure wingFailure))
                return ProvisioningResult.Fail(wingFailure);

            MissionIntent intent = null;
            MissionIntentRegistry.GetOrCreate(player).TryGet(MissionIntentKey.ForAttack(target.Target),
                out intent);
            ArmyData primary = intent?.Attack?.PrimaryArmyId is int primaryId
                ? AiV2Util.ResolveArmy(player, primaryId) : null;
            List<AiMapMemory.KnownEnemySighting> site = (snap?.Known?.EnemySightings
                    ?? (IReadOnlyList<AiMapMemory.KnownEnemySighting>)System.Array.Empty<AiMapMemory.KnownEnemySighting>())
                .Where(s => s.Hex.Equals(targetHex) && s.Defenders != null && s.Defenders.Count > 0)
                .ToList();
            if (!w.Returning && (primary == null || site.Count == 0
                    || AttackObjectiveEvaluator.EvaluateTarget(snap, target.Target)
                        != AttackObjectiveEvaluator.AttackTargetStatus.Continue
                    || site.Any(s => snap.TurnNumber - s.SeenTurn > AiConfigV2.attackIntelMaxAgeTurns)))
                return ProvisioningResult.Fail(ProvisionFailure.TargetInvalidated(
                    "attack air support site is no longer the target, has no fresh defenders, "
                    + "or the primary is gone"));

            IReadOnlyList<WorthIt.DefendingArmy> opposition =
                AttackObjectiveEvaluator.KnownSiteOpposition(snap, targetHex);
            float hexBonus = AttackObjectiveEvaluator.KnownSiteDefenceBonus(snap, ctx?.Map, targetHex);
            List<WorthIt.DefenderProfile> roster = primary == null
                ? new List<WorthIt.DefenderProfile>()
                : primary.Members.Where(u => AiArmyRoles.IsGroundBattleBody(u))
                    .Select(WorthIt.FromLiveUnit).ToList();
            WorthIt.SideCommander commander = WorthIt.SideCommander.Of(primary?.Commander);
            if (!GroundCombatAirSupport.TryFinishWing(player, root, ctx, session, funded, w, targetHex,
                    opposition, site.Sum(s => s.DefenseSum), site.Sum(s => s.AttackSum),
                    AirStrikePolicy.Standard,
                    opp => WorthIt.EstimateSequential(roster, commander, opp, hexBonus).WinChance,
                    "attack", eps, out HexCoord landing, out float ap, out float energy,
                    out float nextTurnEnergy, out float nextTurnAp,
                    out ProvisionFailure finishFailure))
                return ProvisioningResult.Fail(finishFailure);

            target.AirSupportLandingHex = landing;
            return ProvisioningResult.Ok(new ProvisionedMission
            {
                Mission = funded.Mission,
                Key = key,
                Kind = MissionKind.Attack,
                MoverArmyId = w.Wing.Id,
                FocusHex = targetHex,
                ExecutionHex = targetHex,
                AttackTarget = target,
                ClaimedAp = ap,
                ClaimedEnergy = energy,
                ClaimedNextTurnAirEnergy = nextTurnEnergy,
                ClaimedNextTurnAirAp = nextTurnAp,
                ClaimedPhysical = new ResourceVector(0f, 0f, energy, 0f, 0f),
            });
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

        // T01 — a preparation host step. Re-validates the frozen decision against the live world and
        // this cycle's claims, prices it (2 AP only when a container is really created, 0 for a
        // reuse or a same-hex join) and pins the exact transfers. No mutation here: the step is
        // Execution's one canonical action (AttackExecutor.RunPreparationStep).
        private static ProvisioningResult ProvisionPreparation(PlayerSetupData player, PlayerRoot root,
            ProvisioningSession session, FundedEntry funded, AttackMissionTarget target,
            StableMissionKey key, float eps)
        {
            WorldSnapshot snap = session.Snapshot;
            AttackObjectiveEvaluator.AttackTargetStatus status =
                AttackObjectiveEvaluator.EvaluateTarget(snap, target.Target);
            if (status == AttackObjectiveEvaluator.AttackTargetStatus.Captured)
                return ProvisioningResult.Fail(ProvisionFailure.TargetSatisfied(
                    $"attack target {target.Target.DiagnosticLabel} is already ours"));
            if (status == AttackObjectiveEvaluator.AttackTargetStatus.Invalidated)
                return ProvisioningResult.Fail(ProvisionFailure.TargetInvalidated(
                    $"attack target {target.Target.DiagnosticLabel} is no longer a hostile Attack structure"));

            HexCoord hex = target.DestinationHex;
            ArmyData host = null;
            if (target.PrimaryArmyId.HasValue)
            {
                host = AiV2Util.ResolveArmy(player, target.PrimaryArmyId.Value);
                if (host == null || host.Owner != player || host.IsGarrison || host.IsPrison
                    || host.IsAirfield || AviationRules.IsAirArmy(host) || !host.Hex.Equals(hex))
                    return ProvisioningResult.Fail(ProvisionFailure.TargetInvalidated(
                        $"attack preparation host #{target.PrimaryArmyId.Value} is gone, moved or no "
                        + "longer an own ground field container"));
                if (session.ClaimedArmyIds.Contains(host.Id))
                    return ProvisioningResult.Fail(ProvisionFailure.MoverContended(
                        $"attack preparation host #{host.Id} was claimed by an earlier mission this cycle"));
            }
            else
            {
                // A container is created only on the player's OWN starting Citadel, and never
                // beside an empty shell that could be reused instead.
                if (snap?.Self == null || !snap.Self.HoldsStartingCitadel || !hex.Equals(snap.Self.Citadel))
                    return ProvisioningResult.Fail(ProvisionFailure.TargetInvalidated(
                        $"attack preparation: ({hex.Q},{hex.R}) is not the held own starting Citadel"));
                ArmyData shell = ReusableArmySelector.FindReusableAt(player, hex, null);
                if (shell != null && !session.ClaimedArmyIds.Contains(shell.Id))
                    return ProvisioningResult.Fail(ProvisionFailure.AssemblyInfeasible(
                        $"attack preparation: reusable shell #{shell.Id} stands on the Citadel; reuse it "
                        + "instead of creating a container"));
            }

            HashSet<int> excluded = session.ExcludedForGroundCombat(funded.Mission);
            if (host != null)
                excluded.Remove(host.Id);
            ArmyData planned = host;
            if (planned == null)
            {
                planned = ArmyData.CreateVisualSnapshot();
                planned.Hex = hex;
                planned.Owner = player;
                planned.IsGarrison = false;
            }
            GroundCombatAssemblyPlan assembly = GroundCombatAssemblyPlanner.PlanPreparationAssembly(
                snap, planned, excluded, AttackObjectiveEvaluator.KnownSiteOpposition(snap, target.Target.Hex),
                target.DefenderHexDefenseBonus);
            if (!assembly.Feasible)
                assembly = null;
            if (target.PreparationStep == AttackPreparationStep.Assemble && assembly == null)
                return ProvisioningResult.Fail(ProvisionFailure.AssemblyInfeasible(
                    $"attack preparation host #{host?.Id}: no legal same-hex body raises it any more"));
            if (assembly != null
                && assembly.Transfers.Any(t => session.ClaimedArmyIds.Contains(t.DonorArmyId)))
                return ProvisioningResult.Fail(ProvisionFailure.MoverContended(
                    "attack preparation donor was claimed by an earlier mission this cycle"));

            float ap = host == null ? ArmyActions.CreateArmyApCost : 0f;
            float envelope = funded.Tentative.Ap;
            if (ap > envelope + eps)
                return ProvisioningResult.Fail(ProvisionFailure.EnvelopeTooSmall(ap,
                    $"attack preparation needs {N(ap)} AP to create its host, envelope is {N(envelope)}"));
            float turnApLeft = root.ActionPoints - session.ApClaimed;
            if (ap > turnApLeft + eps)
                return ProvisioningResult.Fail(ProvisionFailure.MoverContended(
                    $"turn AP exhausted: attack preparation needs {N(ap)}, {N(turnApLeft)} left"));

            if (host != null)
                session.ClaimedArmyIds.Add(host.Id);
            if (assembly != null)
                foreach (GroundCombatAssemblyTransfer t in assembly.Transfers)
                    session.ClaimedArmyIds.Add(t.DonorArmyId);

            AiDebugLog.Write($"[AI][V2]   attack provision [{funded.Mission.AttemptId}] {key} — OK "
                + $"PREPARATION {target.PreparationStep} host "
                + $"{(host != null ? $"#{host.Id} ({host.Members.Count}/{host.Capacity})" : "new")} at "
                + $"({hex.Q},{hex.R}) ap {N(ap)} transfers="
                + (assembly == null ? "none"
                    : string.Join(",", assembly.Transfers.Select(t => $"{t.Unit?.Name}<-#{t.DonorArmyId}"))));
            return ProvisioningResult.Ok(new ProvisionedMission
            {
                Mission = funded.Mission,
                Key = key,
                Kind = MissionKind.Attack,
                MoverArmyId = host?.Id ?? -1,
                FocusHex = hex,
                ExecutionHex = hex,
                AttackTarget = target,
                AttackPreparationAssembly = assembly,
                ClaimedPhysical = funded.PhysicalDraw,
                ClaimedAp = ap,
                StealthApReserved = false,
            });
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
                "attack", out bool atRendezvous, allowCommandHandover: true);
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
