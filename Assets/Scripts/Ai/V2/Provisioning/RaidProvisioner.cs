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
    internal static class RaidProvisioner
    {
        public static ProvisioningResult Provision(PlayerSetupData player, PlayerRoot root, AiTurnContext ctx,
            ProvisioningSession session, FundedEntry funded)
        {
            MissionProposal m = funded.Mission;
            if (!(m.Target is RaidMissionTarget target))
                return ProvisioningResult.Fail(ProvisionFailure.AssemblyInfeasible("raid mission has no RaidMissionTarget"));

            StableMissionKey key = StableMissionKey.For(m);
            WorldSnapshot snap = session.Snapshot;
            float eps = AiConfigV2.allocatorSliceEpsilon;

            // The Assault leg uses the transactional same-hex assembly. Reinforcement, Return and
            // SupportReturn are their own, much narrower provisioning shapes; Return and
            // SupportReturn share one implementation
            // (mover = primary vs. mover = support), never re-picking the destination.
            if (target.Phase == RaidMissionPhase.AirSupport)
                return ProvisionAirSupport(player, root, ctx, session, funded, target, key, eps);
            if (target.Phase == RaidMissionPhase.Return)
                return ProvisionReturn(player, root, ctx, session, funded, target, key, eps,
                    RaidMissionPhase.Return, target.PrimaryArmyId);
            if (target.Phase == RaidMissionPhase.RecoveryReturn)
                return ProvisionReturn(player, root, ctx, session, funded, target, key, eps,
                    RaidMissionPhase.RecoveryReturn, target.PrimaryArmyId);
            if (target.Phase == RaidMissionPhase.SupportReturn)
                return ProvisionReturn(player, root, ctx, session, funded, target, key, eps,
                    RaidMissionPhase.SupportReturn, target.SupportArmyId);
            if (target.Phase == RaidMissionPhase.Reinforcement)
                return ProvisionReinforcement(player, root, ctx, session, funded, target, key, eps);

            // Assault — defender resolution goes through the single canonical resolver so an
            // EventGuard target (no ArmyId, no live sighting to find) and a NeutralArmy target
            // share one code path here instead of two parallel switches.
            RaidTargetRef raidTarget = target.Target;
            HexCoord targetHex;
            IReadOnlyList<WorthIt.DefenderProfile> defenders;
            bool targetIsNeutral;
            if (raidTarget.Kind == RaidTargetKind.EventGuard)
            {
                if (!HexEventRegistry.HasActiveEvent(raidTarget.Hex))
                {
                    return RaidObjectiveEvaluator.IsObjectiveSatisfiedLive(player, raidTarget)
                        ? ProvisioningResult.Fail(ProvisionFailure.TargetSatisfied(
                            $"raid target {raidTarget.DiagnosticLabel} already consumed"))
                        : ProvisioningResult.Fail(ProvisionFailure.TargetInvalidated(
                            $"raid target {raidTarget.DiagnosticLabel} no longer has an active event"));
                }
                targetHex = raidTarget.Hex;
                defenders = AiV2Util.KnownDefenders(snap, raidTarget);
                targetIsNeutral = true;
            }
            else
            {
                AiMapMemory.KnownEnemySighting? sighting = FindLiveSighting(player, raidTarget.ArmyId);
                if (sighting == null)
                {
                    if (RaidObjectiveEvaluator.IsObjectiveSatisfiedLive(player, raidTarget))
                        return ProvisioningResult.Fail(ProvisionFailure.TargetSatisfied(
                            $"raid target #{raidTarget.ArmyId} no longer exists (destroyed / captured)"));
                    return ProvisioningResult.Fail(ProvisionFailure.TargetInvalidated(
                        $"raid target #{raidTarget.ArmyId} has no current honest sighting; absence is not proof of destruction"));
                }
                // Defensive re-check only; RaidObjectiveEvaluator.IsNeutralRaidTarget
                // is the ONE canonical neutrality decision. Raid targets neutrals only, so ANY
                // non-neutral owner ends the leg here — "now ours" (captured) is reported as
                // satisfied, any other non-neutral owner (the target flipped to a different player
                // mid-Raid) is invalidated so Continuity retargets instead of continuing to attack a
                // now-illegal target.
                if (!RaidObjectiveEvaluator.IsNeutralRaidTarget(sighting.Value.Owner))
                {
                    return ProvisioningResult.Fail(ProvisionFailure.TargetSatisfied(
                        sighting.Value.Owner.Equals(player)
                            ? $"raid target #{raidTarget.ArmyId} is now ours"
                            : $"raid target #{raidTarget.ArmyId} is no longer neutral "
                                + "(now owned by another player)"));
                }

                targetHex = sighting.Value.Hex;
                defenders = sighting.Value.Defenders ?? System.Array.Empty<WorthIt.DefenderProfile>();
                targetIsNeutral = sighting.Value.Owner != null && sighting.Value.Owner.IsNeutral;
            }

            // ATK §28/§46 — the transactional assault assembly is the SHARED ground-combat
            // primitive (GroundCombatAssaultTransactionRunner). This lane supplies only which fight
            // it is; it owns no assembly, no estimator and no transaction of its own.
            GroundCombatAssaultOutcome assault = GroundCombatAssaultTransactionRunner.Run(
                new GroundCombatAssaultRequest
                {
                    Player = player, Root = root, Ctx = ctx, Session = session, Funded = funded,
                    Key = key, TargetHex = targetHex, Defenders = defenders,
                    DefenderHexDefenseBonus = 0f, LaneLabel = "raid", Eps = eps,
                });
            if (!assault.Success)
                return assault.Failure;
            ArmyData host = assault.Host;
            int actualAp = assault.ActualAp;
            return ProvisioningResult.Ok(new ProvisionedMission
            {
                Mission = m,
                Key = key,
                Kind = MissionKind.Raid,
                MoverArmyId = host.Id,
                FocusHex = targetHex,
                ExecutionHex = targetHex,
                RaidTarget = raidTarget,
                RaidLastKnownHex = targetHex,
                RaidTargetIsNeutral = targetIsNeutral,
                ClaimedPhysical = funded.PhysicalDraw,
                // The reconciled cost of the force that actually exists now — asserted equal to
                // the projected/funded figure above, so allocator, revalidator and executor all
                // debit this one number exactly once.
                ClaimedAp = actualAp,
                StealthApReserved = false,
            }, assault.AppliedTransfers);
        }

        private static ProvisioningResult ProvisionAirSupport(PlayerSetupData player, PlayerRoot root,
            AiTurnContext ctx, ProvisioningSession session, FundedEntry funded,
            RaidMissionTarget target, StableMissionKey key, float eps)
        {
            if (target.Target.Kind != RaidTargetKind.NeutralArmy
                || !target.AirSupportArmyId.HasValue || !target.PrimaryArmyId.HasValue)
                return ProvisioningResult.Fail(ProvisionFailure.TargetInvalidated(
                    "raid air support requires one exact physical neutral target and wing"));
            ArmyData wing = ResolveArmy(player, target.AirSupportArmyId.Value);
            if (wing == null || !AviationRules.IsValidAirArmy(wing) || wing.Owner != player
                || wing.Members.Count == 0 || session.ClaimedArmyIds.Contains(wing.Id))
                return ProvisioningResult.Fail(ProvisionFailure.MoverContended(
                    $"raid support wing #{target.AirSupportArmyId.Value} unavailable"));
            AirSortie active = AirSortieRegistry.ForArmy(player, wing);
            bool continuing = active != null && active.Kind == AirSortieKind.Strike
                && (active.Outbound && active.TargetHex.Equals(target.LastKnownHex)
                    || !active.Outbound);
            bool returning = active != null && active.Kind == AirSortieKind.Strike
                && !active.Outbound;
            if (active != null && !continuing)
                return ProvisioningResult.Fail(ProvisionFailure.MoverContended(
                    $"wing #{wing.Id} is reserved by another sortie"));

            AiMapMemory.KnownEnemySighting? sighting = (session.Snapshot?.Known?.NeutralSightings
                    ?? System.Array.Empty<AiMapMemory.KnownEnemySighting>())
                .Where(s => s.ArmyId == target.Target.ArmyId)
                .Select(s => (AiMapMemory.KnownEnemySighting?)s).FirstOrDefault();
            ArmyData defender = ArmyRegistry.AllAt(target.LastKnownHex)
                .FirstOrDefault(a => a != null && a.Id == target.Target.ArmyId
                    && a.Owner != null && a.Owner.IsNeutral && a.Members.Count > 1
                    && !HexEventRegistry.IsEventGuardArmy(target.LastKnownHex, a));
            if (!returning && (!sighting.HasValue
                    || sighting.Value.SeenTurn != session.Snapshot.TurnNumber
                    || defender == null))
                return ProvisioningResult.Fail(ProvisionFailure.TargetInvalidated(
                    "raid air support target is stale, absent, event-owned, or has only one defender"));

            HexCoord landing;
            if (continuing)
                landing = active.LandingHex;
            else
            {
                Sortie? sameTurn = AiAirSortiePlanner.TryPlanSortie(wing,
                    target.LastKnownHex, ctx.Map, player);
                MultiTurnSortie? multi = sameTurn.HasValue ? null
                    : AiAirSortiePlanner.TryPlanMultiTurnSortie(wing,
                        target.LastKnownHex, ctx.Map, player);
                if (!sameTurn.HasValue && !multi.HasValue)
                    return ProvisioningResult.Fail(ProvisionFailure.NoExecutableStep(
                        $"wing #{wing.Id} has no AA-safe recoverable route to exact raid target"));
                landing = sameTurn?.LandingHex ?? multi.Value.LandingHex;

                IReadOnlyList<WorthIt.DefenderProfile> defenders =
                    AiV2Util.KnownDefenders(session.Snapshot, target.Target);
                AviationCombatEstimator.AirStrikeEstimate estimate =
                    AviationCombatEstimator.EstimateAirStrike(wing.Members,
                        sighting.Value.DefenseSum, sighting.Value.AttackSum, defenders,
                        AirStrikePolicy.RaidSupport(target.Target.ArmyId));
                ArmyData primary = ResolveArmy(player, target.PrimaryArmyId.Value);
                float beforeWin = primary == null || defenders.Count == 0 ? 0f
                    : WorthIt.WinChance(primary, defenders, 0f);
                float afterWin = primary == null || estimate.ExpectedDefendersAfter.Count == 0
                    ? beforeWin
                    : WorthIt.WinChance(primary, estimate.ExpectedDefendersAfter, 0f);
                if (estimate.ExpectedDamage <= eps
                    || estimate.ExpectedDefendersAfter.Count < 1
                    || afterWin <= beforeWin + eps)
                    return ProvisioningResult.Fail(ProvisionFailure.SortieNotWorthwhile(
                        "nonlethal raid support does not improve the primary's projected odds"));
            }

            float ap = wing.HasActivatedThisTurn ? 0f : wing.ActivationApCost;
            float energy = wing.HasActivatedThisTurn ? 0f : wing.ActivationEnergyCost;
            if (ap > funded.Tentative.Ap + eps || energy > funded.PhysicalDraw.Energy + eps)
                return ProvisioningResult.Fail(ProvisionFailure.EnvelopeTooSmall(
                    new ProvisionRequirement(ap, new ResourceVector(0f, 0f, energy, 0f, 0f)),
                    $"raid support wing #{wing.Id} exceeds AP/Energy envelope"));
            float energyLeft = ProvisioningManager.AirSpendableEnergyLeft(player, root, ctx, session);
            if (energy > energyLeft + eps)
                return ProvisioningResult.Fail(ProvisionFailure.MoverContended(
                    $"spendable Energy exhausted: raid support wing #{wing.Id} needs {energy:0.##}, "
                    + $"{energyLeft:0.##} left after reservations and earlier claims this pass"));

            target.AirSupportLandingHex = landing;
            return ProvisioningResult.Ok(new ProvisionedMission
            {
                Mission = funded.Mission, Key = key, Kind = MissionKind.Raid,
                MoverArmyId = wing.Id, FocusHex = target.LastKnownHex,
                ExecutionHex = target.LastKnownHex,
                RaidPhase = RaidMissionPhase.AirSupport,
                RaidPrimaryArmyId = target.PrimaryArmyId,
                RaidAirSupportArmyId = wing.Id,
                RaidAirSupportLandingHex = landing,
                RaidDestinationHex = target.LastKnownHex,
                RaidTarget = target.Target,
                RaidLastKnownHex = target.LastKnownHex,
                RaidTargetIsNeutral = true,
                ClaimedAp = ap, ClaimedEnergy = energy,
                ClaimedPhysical = new ResourceVector(0f, 0f, energy, 0f, 0f),
            });
        }

        // =====================================================================================
        //  RETURN leg. Mover is the primary (Return) or the support
        //  (SupportReturn); the destination base was already chosen (and fixed) by Continuity.
        //  Provisioning re-validates the actor, the route and the AP envelope; it never re-picks
        //  the base, and never assumes the mover is the primary.
        // =====================================================================================
        private static ProvisioningResult ProvisionReturn(PlayerSetupData player, PlayerRoot root,
            AiTurnContext ctx, ProvisioningSession session, FundedEntry funded,
            RaidMissionTarget target, StableMissionKey key, float eps,
            RaidMissionPhase phase, int? moverArmyId)
        {
            string roleLabel = phase == RaidMissionPhase.SupportReturn ? "support"
                : phase == RaidMissionPhase.RecoveryReturn ? "recovery primary" : "primary";
            HexCoord home = target.DestinationHex;
            // ATK §28 — the walk-home checks are the shared ground-combat leg primitive.
            GroundCombatLegCheck check = GroundCombatLegChecks.ValidateWalkHome(player, root, ctx,
                session, funded, key, eps, moverArmyId, home, "raid", roleLabel);
            if (!check.Ok)
                return check.Failure;
            ArmyData mover = check.Mover;
            int activationAp = check.ActivationAp;

            string returnLabel = phase == RaidMissionPhase.SupportReturn ? "SUPPORT_RETURN"
                : phase == RaidMissionPhase.RecoveryReturn ? "RECOVERY_RETURN" : "RETURN";
            AiDebugLog.Write($"[AI][V2]   raid provision [{funded.Mission.AttemptId}] {key} — OK "
                + $"{returnLabel} "
                + $"{roleLabel} #{mover.Id} -> ({home.Q},{home.R}) ap {N(activationAp)}");
            return ProvisioningResult.Ok(new ProvisionedMission
            {
                Mission = funded.Mission,
                Key = key,
                Kind = MissionKind.Raid,
                MoverArmyId = mover.Id,
                FocusHex = home,
                ExecutionHex = home,
                RaidPhase = phase,
                RaidPrimaryArmyId = target.PrimaryArmyId,
                RaidSupportArmyId = target.SupportArmyId,
                RaidDestinationHex = home,
                RaidTarget = target.Target,
                RaidLastKnownHex = target.LastKnownHex,
                RaidTargetIsNeutral = target.TargetIsNeutral,
                ClaimedPhysical = funded.PhysicalDraw,
                ClaimedAp = activationAp,
                StealthApReserved = false,
            });
        }

        // =====================================================================================
        //  REINFORCEMENT leg. Mover is the SEPARATE support army; the rendezvous is
        //  the primary's current hex. The primary itself does not move while support is in
        //  transit (it is never the mover of this mission and is claimed by continuity).
        // =====================================================================================
        private static ProvisioningResult ProvisionReinforcement(PlayerSetupData player, PlayerRoot root,
            AiTurnContext ctx, ProvisioningSession session, FundedEntry funded,
            RaidMissionTarget target, StableMissionKey key, float eps)
        {
            if (!target.PrimaryArmyId.HasValue)
                return ProvisioningResult.Fail(ProvisionFailure.TargetInvalidated(
                    "raid reinforcement has no primary assigned"));
            ArmyData primary = ResolveArmy(player, target.PrimaryArmyId.Value);
            if (primary == null || primary.Owner != player || primary.Members.Count == 0
                || primary.IsPrison || primary.IsAirfield || AviationRules.IsAirArmy(primary))
                return ProvisioningResult.Fail(ProvisionFailure.TargetInvalidated(
                    $"raid reinforcement primary #{target.PrimaryArmyId.Value} is gone or no longer a field army"));

            // An UNPINNED leg (no materialization ever happened) has no
            // target.SupportArmyId; the concrete actor comes straight out of the SAME
            // batch-assignment solve PrepareGroundCombatAssignments already runs for Assault.
            int supportArmyId;
            if (!target.SupportArmyId.HasValue)
            {
                if (!session.TryGetAssignedGroundCombatActor(key, out supportArmyId))
                    return ProvisioningResult.Fail(ProvisionFailure.NoMoverExists(
                        $"raid reinforcement for primary #{target.PrimaryArmyId.Value} has no existing free "
                        + "support army assigned this cycle"));
            }
            else
            {
                supportArmyId = target.SupportArmyId.Value;
            }

            HexCoord rendezvous = primary.Hex;
            IReadOnlyList<WorthIt.DefenderProfile> defenders =
                AiV2Util.KnownDefenders(session.Snapshot, target.Target);
            // ATK §28/§46 — the convoy checks are the shared ground-combat leg primitive.
            GroundCombatLegCheck check = GroundCombatLegChecks.ValidateReinforcement(player, root,
                ctx, session, funded, key, eps, primary, supportArmyId, defenders, 0f, "raid",
                out bool atRendezvous);
            if (!check.Ok)
                return check.Failure;
            ArmyData support = check.Mover;
            int activationAp = check.ActivationAp;

            // The primary must not be handed to another mission while the convoy is in transit.
            session.ClaimedArmyIds.Add(primary.Id);

            AiDebugLog.Write($"[AI][V2]   raid provision [{funded.Mission.AttemptId}] {key} — OK REINFORCE "
                + $"support #{support.Id} -> primary #{primary.Id} at ({rendezvous.Q},{rendezvous.R}) "
                + $"{(atRendezvous ? "HANDOFF" : "TRANSIT")} ap {N(activationAp)}");
            return ProvisioningResult.Ok(new ProvisionedMission
            {
                Mission = funded.Mission,
                Key = key,
                Kind = MissionKind.Raid,
                MoverArmyId = support.Id,
                FocusHex = rendezvous,
                ExecutionHex = rendezvous,
                RaidPhase = RaidMissionPhase.Reinforcement,
                RaidPrimaryArmyId = primary.Id,
                RaidSupportArmyId = support.Id,
                RaidDestinationHex = rendezvous,
                RaidHandoffReady = atRendezvous,
                RaidTarget = target.Target,
                RaidLastKnownHex = target.LastKnownHex,
                RaidTargetIsNeutral = target.TargetIsNeutral,
                ClaimedPhysical = funded.PhysicalDraw,
                ClaimedAp = activationAp,
                StealthApReserved = false,
            });
        }

        // ATK §28/§46 — both halves moved to the GroundCombatReinforcement kernel; nothing in
        // either was Raid-specific and Attack must not get a copy.
        internal static List<UnitData> SparableSupportBodies(ArmyData support) =>
            GroundCombatReinforcement.SparableSupportBodies(support);

        // Both lanes share the one GroundCombatAssemblyTransaction primitive so "did the world
        // really change" is measured identically.
        private static bool RollbackAssembly(PlayerSetupData player, ArmyData host,
            List<GroundCombatAssemblyTransfer> applied, AiTurnContext ctx) =>
            GroundCombatAssemblyTransaction.Rollback(player, host, applied, ctx, "raid");

        // Was a duplicate of GroundCombatFeasibility.Clears — with a stale hardcoded win-chance
        // threshold and no `cover` output — now calls that shared, parameterized implementation
        // directly at the one call site above (see Docs/ai-duplicate-methods-analysis.md, D4).

        // Was byte-identical in ReconAssignmentPlanner and (twice) in this file — moved to AiV2Util.
        private static ArmyData ResolveArmy(PlayerSetupData player, int armyId) =>
            AiV2Util.ResolveArmy(player, armyId);

        private static string N(float v) => v.ToString("0.##", CultureInfo.InvariantCulture);

        private static AiMapMemory.KnownEnemySighting? FindLiveSighting(PlayerSetupData player, int armyId)
        {
            foreach (AiMapMemory.KnownEnemySighting s in AiMapMemory.AllKnownEnemySightings(player))
                if (s.ArmyId == armyId) return s;
            foreach (AiMapMemory.KnownEnemySighting s in AiMapMemory.AllKnownNeutralSightings(player))
                if (s.ArmyId == armyId) return s;
            return null;
        }
    }
}
