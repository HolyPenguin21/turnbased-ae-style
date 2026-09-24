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
    internal static class ActiveDefenceProvisioner
    {
        // Same invariant AP formatting RaidProvisioner logs with — the two lanes' provisioning
        // lines are read side by side.
        private static string N(float v) => v.ToString("0.##", CultureInfo.InvariantCulture);

        internal static ProvisioningResult Provision(PlayerSetupData player, PlayerRoot root,
            AiTurnContext ctx, ProvisioningSession session, FundedEntry funded)
        {
            MissionProposal mission = funded?.Mission;
            if (mission == null || !(mission.Target is ActiveDefenceMissionTarget target))
                return ProvisioningResult.Fail(ProvisionFailure.TargetInvalidated(
                    "active defence has no typed target"));

            if (target.Phase == ActiveDefencePhase.Return)
                return ProvisionReturn(player, root, ctx, session, funded, target);

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

            StableMissionKey key = StableMissionKey.For(mission);
            if (!session.TryGetAssignedGroundCombatActor(key, out int actorId))
                return ProvisioningResult.Fail(ProvisionFailure.MoverContended(
                    $"active defence {key} has no actor in shared ground-combat assignment"));
            HashSet<int> excluded = session.ExcludedForGroundCombat(mission);
            if (excluded.Contains(actorId))
                return ProvisioningResult.Fail(ProvisionFailure.MoverContended(
                    $"active defence actor #{actorId} is owned by another mission"));

            IReadOnlyList<WorthIt.DefenderProfile> defenders = sighting.Value.Defenders
                ?? Array.Empty<WorthIt.DefenderProfile>();
            // Re-checking an admitted mission must re-apply the SAME admission threshold it was
            // admitted under; otherwise Missions accepts a continuation at
            // ContinuationWinChanceFloor, the allocator funds it, and Provisioning rejects the
            // identical facts at FreshStartWinChanceGate. GroundCombatAdmissionPolicy stays the
            // sole owner of both numbers; this only selects between them with the same predicate
            // Missions and GroundCombatAdmissionRegistry use (`FromDurableIntent`), additionally
            // confirming that the actor bound here is the pinned incumbent — a different actor is a
            // fresh intercept and keeps the fresh gate.
            MissionIntent interceptIncumbent = MissionIntentRegistry.GetOrCreate(player).All
                .FirstOrDefault(i => i != null && i.Status == IntentStatus.Active
                    && i.Kind == MissionKind.ActiveDefence
                    && i.ActiveDefence?.EnemyArmyId == target.EnemyArmyId);
            bool continuesPinnedIntercept = mission.FromDurableIntent
                && interceptIncumbent?.ActiveDefence?.PrimaryArmyId == actorId;
            GroundCombatAssemblyPlan plan = GroundCombatAssemblyPlanner.Plan(session.Snapshot,
                new GroundCombatAssemblyRequest
                {
                    Defenders = defenders,
                    PreferredPrimaryArmyId = actorId,
                    PinToPreferred = true,
                    ExcludedArmyIds = excluded,
                    WinChanceGate = continuesPinnedIntercept
                        ? GroundCombatAdmissionPolicy.ContinuationWinChanceFloor
                        : GroundCombatAdmissionPolicy.FreshStartWinChanceGate,
                });
            if (!plan.Feasible)
                return ProvisioningResult.Fail(ProvisionFailure.MoverContended(
                    $"active defence actor #{actorId} cannot complete roster: {plan.Reason}"));

            ArmyData host = AiV2Util.ResolveArmy(player, actorId);
            if (host == null || host.Owner != player || host.Members.Count == 0
                || host.CurrentMovement <= 0 || session.ClaimedArmyIds.Contains(host.Id))
                return ProvisioningResult.Fail(ProvisionFailure.MoverContended(
                    $"active defence actor #{actorId} is no longer available"));
            // The assembly is ONE transaction with explicit stages, the same shape as the Raid
            // lane: PREPARE / VALIDATE ALL TRANSFERS (every legality question asked before any
            // mutation) -> APPLY -> RECONCILE -> COMMIT CLAIMS. Donors are claimed only at COMMIT,
            // so a failure that rolls the world back leaves no donor claimed for the rest of the
            // pass, and a refusal reports StateChanged honestly when the rollback was incomplete.
            float eps = AiConfigV2.allocatorSliceEpsilon;
            var transfers = new List<GroundCombatAssemblyTransfer>();
            var claimedDonors = new HashSet<int>();
            var projectedUnits = new List<UnitData>(host.Members);
            if (plan.NeedsAssembly)
            {
                int heroTransfers = 0;
                foreach (GroundCombatAssemblyTransfer t in plan.Transfers)
                {
                    if (t?.Unit == null)
                        return ProvisioningResult.Fail(ProvisionFailure.AssemblyInfeasible(
                            "active-defence assembly contains a null unit"));
                    ArmyData donor = AiV2Util.ResolveArmy(player, t.DonorArmyId);
                    if (donor == null || donor.Members.Count <= 1 || !donor.Hex.Equals(host.Hex))
                        return ProvisioningResult.Fail(ProvisionFailure.AssemblyInfeasible(
                            $"active-defence donor #{t.DonorArmyId} is gone, moved, or would be emptied"));
                    if (session.ClaimedArmyIds.Contains(donor.Id))
                        return ProvisioningResult.Fail(ProvisionFailure.MoverContended(
                            $"active-defence donor #{donor.Id} was claimed by an earlier mission this cycle"));
                    if (t.Unit.IsHero
                        && (++heroTransfers > 1 || projectedUnits.Any(u => u != null && u.IsHero)))
                        return ProvisioningResult.Fail(ProvisionFailure.AssemblyInfeasible(
                            $"active-defence host #{host.Id} may take at most one hero and only when heroless"));
                    if (donor.IsPrison || donor.IsAirfield || AviationRules.IsAirArmy(donor)
                        || AiArmyRoles.IsSoloRecce(donor) || !donor.Members.Contains(t.Unit)
                        || t.Unit.IsAviation)
                        return ProvisioningResult.Fail(ProvisionFailure.AssemblyInfeasible(
                            $"active-defence donor #{donor.Id} / unit {t.Unit.Name} is no longer legal"));
                    if (!donor.CanLeaveWithoutOvercrowding(t.Unit)
                        || (donor.IsGarrison && !AiArmyRoles.CanSpareGarrisonMember(player, donor, t.Unit)))
                        return ProvisioningResult.Fail(ProvisionFailure.AssemblyInfeasible(
                            $"active-defence donor #{donor.Id} can no longer spare {t.Unit.Name}"));
                    if (host.HasActivatedThisTurn && t.Unit.ActivationApCost > 0)
                        return ProvisioningResult.Fail(ProvisionFailure.AssemblyInfeasible(
                            $"adding {t.Unit.Name} to an activated active-defence host would spend unbudgeted AP"));
                    var withUnit = new List<UnitData>(projectedUnits) { t.Unit };
                    if (ArmyData.ComputeCapacity(withUnit, host.IsGarrison) < withUnit.Count)
                        return ProvisioningResult.Fail(ProvisionFailure.AssemblyInfeasible(
                            $"active-defence host #{host.Id} no longer has capacity for planned assembly"));
                    projectedUnits.Add(t.Unit);
                    transfers.Add(t);
                    claimedDonors.Add(donor.Id);
                }
                foreach (IGrouping<int, GroundCombatAssemblyTransfer> group in transfers.GroupBy(t => t.DonorArmyId))
                {
                    ArmyData donor = AiV2Util.ResolveArmy(player, group.Key);
                    List<UnitData> units = group.Select(t => t.Unit).ToList();
                    if (donor == null || donor.Members.Count - units.Count < 1
                        || donor.IsGarrison
                            && !AiArmyRoles.CanSpareGarrisonMembers(player, donor, units))
                        return ProvisioningResult.Fail(ProvisionFailure.AssemblyInfeasible(
                            $"active-defence donor #{group.Key} cannot spare the complete batch"));
                }
            }

            // Every physical-executability check below is asked about `projectedUnits`, the roster
            // this plan will actually march, and all of them run BEFORE the first
            // ArmyActions.TransferMember — asking the UNTOUCHED host would let the assembled force
            // turn out to cost more AP than was funded only after the world had been mutated.
            // Reachability too: a recruit slower than the host lowers the whole formation's shared
            // movement, so the first step is re-asked for the projected roster.
            if (SafeStepPathing.FindNextSafeStepForRoster(ctx.Map, host, sighting.Value.Hex,
                    projectedUnits) == null)
                return ProvisioningResult.Fail(ProvisionFailure.NoExecutableStep(
                    $"no safe step toward active defence enemy #{target.EnemyArmyId}"
                    + (plan.NeedsAssembly ? " for the projected assembled roster" : "")));

            int activationAp = host.ProjectedActivationApCost(projectedUnits);
            float envelope = funded.Tentative.Ap;
            if (activationAp > envelope + eps)
                return ProvisioningResult.Fail(ProvisionFailure.EnvelopeTooSmall(activationAp,
                    $"active defence actor #{actorId} needs {N(activationAp)} AP for its projected "
                    + $"{projectedUnits.Count}-body roster, envelope is {N(envelope)}"));
            if (activationAp > root.ActionPoints - session.ApClaimed + eps)
                return ProvisioningResult.Fail(ProvisionFailure.MoverContended(
                    "turn AP exhausted before active defence"));

            // APPLY. Nothing above this line has mutated the world, so every refusal so far is a
            // clean Complete rollback by construction: no transfer, no claim, StateChanged=false.
            var applied = new List<GroundCombatAssemblyTransfer>();
            foreach (GroundCombatAssemblyTransfer transfer in transfers)
            {
                ArmyData donor = AiV2Util.ResolveArmy(player, transfer.DonorArmyId);
                string why = donor == null ? "donor missing" : null;
                if (donor == null || !ArmyActions.TransferMember(transfer.Unit, donor, host,
                        ctx.HexSelection, out why))
                {
                    bool rollbackOk = GroundCombatAssemblyTransaction.Rollback(player, host,
                        applied, ctx, "active-defence");
                    int stillApplied = GroundCombatAssemblyTransaction.RemainingApplied(host, applied);
                    AiDebugLog.Write($"[AI][V2][ActiveDefence][Provision] decision=ROLLBACK "
                        + $"enemy={target.EnemyArmyId} actor={host.Id} unit={transfer.Unit?.Name} "
                        + $"donor=#{transfer.DonorArmyId}: {why}; "
                        + $"rollback={(rollbackOk ? "OK" : "FAILED")}; remainingTransfers={stillApplied}");
                    // An incomplete rollback is an honest partial mutation: report StateChanged so
                    // the version is bumped and no later stage keeps planning on the old world.
                    return ProvisioningResult.Fail(ProvisionFailure.AssemblyInfeasible(
                            rollbackOk ? $"atomic active-defence assembly rejected: {why}"
                                : $"active-defence assembly rollback incomplete: {why}"),
                        stillApplied > 0, stillApplied);
                }
                applied.Add(transfer);
            }

            // RECONCILE — the assembled force must cost exactly what was projected and funded.
            // Any divergence rolls the whole transaction back rather than succeeding partially.
            int actualAp = host.ProjectedActivationApCost(host.Members);
            if (actualAp != activationAp || actualAp > envelope + eps)
            {
                bool reconcileRollbackOk = GroundCombatAssemblyTransaction.Rollback(player, host,
                    applied, ctx, "active-defence");
                int stillApplied = GroundCombatAssemblyTransaction.RemainingApplied(host, applied);
                AiDebugLog.Write($"[AI][V2][ActiveDefence][Provision] decision=RECONCILE_FAIL "
                    + $"enemy={target.EnemyArmyId} actor={host.Id} actual={N(actualAp)} "
                    + $"projected={N(activationAp)} envelope={N(envelope)}; "
                    + $"rollback={(reconcileRollbackOk ? "OK" : "FAILED")}; remainingTransfers={stillApplied}");
                return ProvisioningResult.Fail(ProvisionFailure.EnvelopeTooSmall(actualAp,
                        $"active-defence host #{host.Id} reconciled activation {N(actualAp)} AP "
                        + $"diverges from the projected {N(activationAp)} AP"),
                    stillApplied > 0, stillApplied);
            }

            // COMMIT CLAIMS — only now, against a confirmed complete assembly.
            foreach (int donorId in claimedDonors)
                session.ClaimedArmyIds.Add(donorId);

            target.LastKnownHex = sighting.Value.Hex;
            target.LastObservedTurn = sighting.Value.SeenTurn;
            target.PrimaryArmyId = host.Id;
            target.ProjectedWinChance = plan.ProjectedWinChance;
            target.CoversAllDefenders = plan.CoversAllDefenders;
            AiDebugLog.Write($"[AI][V2][ActiveDefence][Provision] decision=OK enemy={target.EnemyArmyId} "
                + $"actor={host.Id} transfers={applied.Count} win={plan.ProjectedWinChance:0.00}");
            return ProvisioningResult.Ok(new ProvisionedMission
            {
                Mission = mission, Key = key, Kind = MissionKind.ActiveDefence,
                MoverArmyId = host.Id, FocusHex = target.LastKnownHex,
                ExecutionHex = target.LastKnownHex, ActiveDefenceTarget = target,
                ClaimedPhysical = funded.PhysicalDraw,
                // The reconciled cost of the force that actually exists now — asserted equal to
                // the projected/funded figure above, so allocator, revalidator and executor all
                // debit this one number exactly once.
                ClaimedAp = actualAp,
            }, applied.Count);
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
            if (SafeStepPathing.FindNextSafeStep(ctx.Map, actor, target.ReturnHex.Value) == null)
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
