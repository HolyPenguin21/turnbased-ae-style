using System;
using System.Collections.Generic;
using System.Linq;
using Game.Aviation;
using Game.Combat;
using Game.Cards;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Units;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  ATK §24/§25/§47/§61/§70/§71 — ATTACK CONTINUITY.
    //
    //  A mechanical partial of MissionContinuityLayer, not a second continuity owner: intents still
    //  live in the one MissionIntentState, are keyed by the one MissionIntentKey, and are reaped by
    //  the same rules. This file holds only the Attack lane's own lifecycle answers.
    // ===========================================================================================
    internal static partial class MissionContinuityLayer
    {
        internal static bool IsAttackStepObjectiveSatisfiedLive(PlayerSetupData player, ProvisionedMission pm)
        {
            AttackMissionTarget attack = pm.AttackTarget;
            if (attack.Phase == AttackMissionPhase.RecoveryReturn
                || attack.Phase == AttackMissionPhase.SupportReturn
                || attack.Phase == AttackMissionPhase.GatherReturn)
            {
                ArmyData actor = ArmyRegistry.AllForOwner(player)
                    .FirstOrDefault(a => a != null && a.Id == pm.MoverArmyId);
                return actor != null && actor.Hex.Equals(attack.DestinationHex);
            }
            // Reinforcement is a rendezvous with the primary, never the site's capture.
            return attack.Phase == AttackMissionPhase.Assault
                && AttackObjectiveEvaluator.EvaluateTargetLive(player, attack.Target)
                    == AttackObjectiveEvaluator.AttackTargetStatus.Captured;
        }

        // Returns false when the intent must be retired. `success` distinguishes "we took the
        // Base" (§8: the army STAYS there, mission claim released, Housekeeping stabilises) from
        // "this operation is over for another reason".
        // `unavailableArmyIds` — armies no Gather re-plan may recruit (other intents' claims and
        // return-fallback walkers); only read by the Gather phase.
        internal static bool ResolveAttackIntent(PlayerSetupData player, WorldSnapshot snap,
            MissionIntent intent, AttackIntent a, ISet<int> unavailableArmyIds, out bool success)
        {
            success = false;
            if (a == null || !a.Target.HasValue)
            {
                AiDebugLog.Write($"[AI][V2][Attack] {intent.IntentKey} retired — no attack objective");
                return false;
            }

            // ---- §25/§61 target status, from honest knowledge only -----------------------------
            // Once withdrawal has begun, its destination is our own base. Changes to the
            // former enemy site must not release the recovering army mid-route.
            AttackObjectiveEvaluator.AttackTargetStatus status = a.Phase == AttackMissionPhase.RecoveryReturn
                ? AttackObjectiveEvaluator.AttackTargetStatus.Continue
                : AttackObjectiveEvaluator.EvaluateTarget(snap, a.Target);
            if (status == AttackObjectiveEvaluator.AttackTargetStatus.Captured)
            {
                success = true;
                AiDebugLog.Write($"[AI][V2][Attack] {intent.IntentKey} COMPLETE — "
                    + $"{a.Target.DiagnosticLabel} captured; army #{a.PrimaryArmyId} stays in place, "
                    + "claim released for Housekeeping");
                return false;
            }
            if (status == AttackObjectiveEvaluator.AttackTargetStatus.Invalidated)
            {
                // §7/§25 — a new owner on the same hex is a NEW objective, never a silent retarget
                // of this operation. Retire and let the next pass enumerate it fresh.
                AiDebugLog.Write($"[AI][V2][Attack] {intent.IntentKey} retired — "
                    + $"{a.Target.DiagnosticLabel} is no longer a hostile Attack structure under its "
                    + "expected owner");
                return false;
            }

            // A local capture/ownership change or a phase change releases only the detour.
            if (a.IntermediateTarget.HasValue && (a.Phase != AttackMissionPhase.Assault
                || AttackObjectiveEvaluator.EvaluateTarget(snap, a.IntermediateTarget)
                    != AttackObjectiveEvaluator.AttackTargetStatus.Continue))
            {
                a.IntermediateTarget = AttackTargetRef.None;
                a.LastOpportunisticStrikeTurn = snap.TurnNumber;
            }

            // ---- the primary must still exist as a real ground force ---------------------------
            // RecoveryReturn only walks the survivors home, so it needs a live ground container,
            // not a structural combat actor (the gate ActorCommitments already claims it under,
            // and Raid's recoveryGroundGate): a battle-depleted remnant must still withdraw.
            if (a.Phase == AttackMissionPhase.RecoveryReturn)
            {
                if (!a.PrimaryArmyId.HasValue
                    || !MissionActorPolicy.GroundContainerStillValid(a.PrimaryArmyId.Value, snap))
                {
                    AiDebugLog.Write($"[AI][V2][Attack] {intent.IntentKey} retired — recovering "
                        + $"primary #{a.PrimaryArmyId} is no longer a ground container");
                    return false;
                }
            }
            else if (a.Preparation && a.Phase == AttackMissionPhase.Gather)
            {
                // T01 — a preparation host is legitimately weak, hero-only or an empty shell: it is
                // never sent to RecoveryReturn for not being a combat actor yet. Only a host that
                // stopped being an own ground field container ends the preparation (its claims are
                // released with the intent; a replacement is a fresh decision).
                if (!a.PrimaryArmyId.HasValue
                    || !MissionActorPolicy.PreparationHostStillValid(a.PrimaryArmyId.Value, snap))
                {
                    AiDebugLog.Write($"[AI][V2][Attack][Mobilization] {intent.IntentKey} retired — "
                        + $"preparation host #{a.PrimaryArmyId} is no longer an own ground field "
                        + "container; claims released");
                    return false;
                }
            }
            else if (a.Phase != AttackMissionPhase.SupportReturn)
            {
                if (!a.PrimaryArmyId.HasValue
                    || !GroundCombatPrimaryAlive(snap, a.PrimaryArmyId.Value))
                {
                    // A started operation whose primary survives only as a non-combat remnant
                    // (lone hero, lone recce) withdraws like any other non-viable operation (§47);
                    // only a primary with no container left, or no own base to reach, retires here.
                    HexCoord? withdrawTo = a.OperationStarted && a.PrimaryArmyId.HasValue
                        && MissionActorPolicy.GroundContainerStillValid(a.PrimaryArmyId.Value, snap)
                            ? AiReturnBasePolicy.SelectReturnBase(snap, player, a.PrimaryArmyId) : null;
                    if (!withdrawTo.HasValue)
                    {
                        AiDebugLog.Write($"[AI][V2][Attack] {intent.IntentKey} retired — primary "
                            + $"#{a.PrimaryArmyId} is no longer a structural ground actor in phase {a.Phase}");
                        return false;
                    }
                    a.Phase = AttackMissionPhase.RecoveryReturn;
                    a.RecoveryBaseHex = withdrawTo;
                    a.SupportArmyId = null;
                    a.GatherSupportArmyIds.Clear();
                    AiDebugLog.Write($"[AI][V2][Attack] {intent.IntentKey} phase -> RecoveryReturn "
                        + $"({withdrawTo.Value.Q},{withdrawTo.Value.R}); primary #{a.PrimaryArmyId} "
                        + "is only a non-combat remnant");
                }
            }

            // 2026-10-08 — a marching army meets a relevant, significant hostile army it cannot
            // beat: the attack ends and the army walks home. Checked before anything else may
            // re-plan the march, only while the army is actually marching (an operation already
            // withdrawing is not re-triggered: the edge is idempotent).
            if (a.AssaultStarted && (a.Phase == AttackMissionPhase.Assault
                    || a.Phase == AttackMissionPhase.Reinforcement) && a.PrimaryArmyId.HasValue)
            {
                RetreatOutcome retreat = TryTacticalRetreat(player, snap, intent, a);
                if (retreat == RetreatOutcome.Retire)
                    return false;
            }

            ResolveGatherReturns(snap, player, intent, a);
            ResolveAttackAirSupport(snap, player, intent, a, unavailableArmyIds);

            bool supportLostThisPass = false;
            if (a.SupportArmyId.HasValue
                && (a.Phase == AttackMissionPhase.Reinforcement
                    || a.Phase == AttackMissionPhase.SupportReturn)
                && !MissionActorPolicy.GroundContainerStillValid(a.SupportArmyId.Value, snap))
            {
                int lostSupportId = a.SupportArmyId.Value;
                a.SupportArmyId = null;
                a.SupportReturnHex = null;
                a.RendezvousHex = null;
                a.ReinforcementRequestedTurn = -1;
                supportLostThisPass = true;
                if (a.Phase == AttackMissionPhase.SupportReturn)
                    a.Phase = AttackMissionPhase.Assault;
                AiDebugLog.Write($"[AI][V2][Attack] {intent.IntentKey} support #{lostSupportId} "
                    + $"lost; binding released, phase={a.Phase}");
            }

            // ---- walking-home legs: arrival is the end of the leg ------------------------------
            if (a.Phase == AttackMissionPhase.SupportReturn)
            {
                if (!a.SupportArmyId.HasValue)
                {
                    ReleaseAttackSupport(a);
                    return true;
                }
                // The leg was entered by an execution fact (a full/full swap); THIS is where the
                // destination gets chosen, through the one own-Base selection owner.
                bool hadHome = a.SupportReturnHex.HasValue;
                HexCoord? home = AiReturnBasePolicy.KeepOrReselectHome(snap, player, a.SupportArmyId,
                    a.SupportReturnHex, out bool retargeted);
                ArmySnapshot support = snap?.Self?.Armies?.FirstOrDefault(x => x != null
                    && x.ArmyId == a.SupportArmyId.Value);
                // No own base to send it to must never wedge the operation: release the support
                // and let the primary carry on being re-evaluated.
                if (support == null || !home.HasValue || support.Hex.Equals(home.Value))
                {
                    AiDebugLog.Write($"[AI][V2][Attack] {intent.IntentKey} support "
                        + $"#{a.SupportArmyId} released after SupportReturn"
                        + (home.HasValue ? "" : " (no reachable home base)"));
                    ReleaseAttackSupport(a);
                }
                else
                {
                    a.SupportReturnHex = home;
                    if (retargeted && hadHome)
                        intent.StallTurns = 0;
                }
                return true;
            }

            if (a.Phase == AttackMissionPhase.RecoveryReturn)
            {
                // §47 — best reachable OWN base, chosen by the one owner. Never a hardcoded
                // starting Citadel.
                HexCoord? recoveryHome = AiReturnBasePolicy.KeepOrReselectHome(snap, player, a.PrimaryArmyId,
                    a.RecoveryBaseHex, out bool recoveryRetargeted);
                if (recoveryHome == null)
                {
                    AiDebugLog.Write($"[AI][V2][Attack] {intent.IntentKey} retired — "
                        + "recovery base lost and no replacement own base exists");
                    return false;
                }
                if (recoveryRetargeted)
                {
                    AiDebugLog.Write($"[AI][V2][Attack] {intent.IntentKey} recovery base retargeted "
                        + $"to ({recoveryHome.Value.Q},{recoveryHome.Value.R})");
                    a.RecoveryBaseHex = recoveryHome;
                    intent.StallTurns = 0;
                }
                ArmySnapshot recovering = snap?.Self?.Armies?.FirstOrDefault(x => x != null
                    && a.PrimaryArmyId.HasValue && x.ArmyId == a.PrimaryArmyId.Value);
                if (recovering != null && a.RecoveryBaseHex.HasValue
                    && recovering.Hex.Equals(a.RecoveryBaseHex.Value))
                {
                    AiDebugLog.Write($"[AI][V2][Attack] {intent.IntentKey} retired — primary "
                        + $"#{a.PrimaryArmyId} reached its recovery base; the operation is over and "
                        + "the army is free for a fresh decision");
                    return false;
                }
                return true;
            }

            if (a.Phase == AttackMissionPhase.Gather)
                return ResolveAttackGather(snap, intent, a, unavailableArmyIds);

            // ---- §24 the Assault / Reinforcement decision --------------------------------------
            bool clears = AttackPrimaryClearsTarget(snap, a);
            if (a.AssaultStarted)
                return ResolveCommittedAssault(snap, intent, a, clears, unavailableArmyIds);
            if (clears)
            {
                if (a.Phase != AttackMissionPhase.Assault)
                    AiDebugLog.Write($"[AI][V2][Attack] {intent.IntentKey} phase "
                        + $"{a.Phase} -> Assault (primary #{a.PrimaryArmyId} clears the site)");
                a.Phase = AttackMissionPhase.Assault;
                a.ReinforcementRequestedTurn = -1;
                return true;
            }

            // Before the assault march (a Gather that fell apart, an Assault whose first march step
            // has not run yet): the primary cannot take the site. Reinforcement is the answer while
            // there is any prospect of one; only a started operation with no prospect withdraws
            // (§47). A committed Assault never comes here (ResolveCommittedAssault).
            if (a.Phase != AttackMissionPhase.Reinforcement)
            {
                a.Phase = AttackMissionPhase.Reinforcement;
                a.ReinforcementRequestedTurn = -1;
                AiDebugLog.Write($"[AI][V2][Attack] {intent.IntentKey} phase -> Reinforcement "
                    + $"(primary #{a.PrimaryArmyId} does not clear {a.Target.DiagnosticLabel})");
                return true;
            }

            bool reinforcementPossible = a.SupportArmyId.HasValue
                || AttackSupportCandidateExists(snap, a, unavailableArmyIds);
            if (reinforcementPossible)
                return true;

            // A vanished support is fresh shortage evidence. Keep this operation in
            // Reinforcement for the rest of the current settle pass so Demand can re-emit the
            // existing FieldCombatPower request. On the next reconciliation, if neither delivery
            // nor an existing support candidate appeared, the ordinary RecoveryReturn fallback
            // below remains authoritative.
            if (supportLostThisPass)
                return true;

            if (!a.OperationStarted)
            {
                // Nothing has been spent physically yet — this was simply not a viable objective.
                AiDebugLog.Write($"[AI][V2][Attack] {intent.IntentKey} retired — not viable and the "
                    + "operation never started");
                return false;
            }

            HexCoord? recovery = AiReturnBasePolicy.SelectReturnBase(snap, player, a.PrimaryArmyId);
            if (recovery == null)
            {
                AiDebugLog.Write($"[AI][V2][Attack] {intent.IntentKey} retired — no reinforcement "
                    + "prospect and no own base to withdraw to");
                return false;
            }
            a.Phase = AttackMissionPhase.RecoveryReturn;
            a.RecoveryBaseHex = recovery;
            a.SupportArmyId = null;
            AiDebugLog.Write($"[AI][V2][Attack] {intent.IntentKey} phase -> RecoveryReturn "
                + $"({recovery.Value.Q},{recovery.Value.R}); operation no longer viable");
            return true;
        }

        private enum RetreatOutcome { NotTriggered, Withdrawing, Retire }

        // The one edge from a marching Attack to a protected walk home because of a hostile army.
        // Uses the SAME pure decision the planner and the executor use (no second estimator). The
        // return keeps the army's lease (RecoveryReturn is the existing leg); the local and support
        // bindings of the offensive come off, the witness that stops an identical restart is
        // written, and the operation ends on arrival (ResolveAttackIntent's RecoveryReturn branch).
        private static RetreatOutcome TryTacticalRetreat(PlayerSetupData player, WorldSnapshot snap,
            MissionIntent intent, AttackIntent a)
        {
            ArmySnapshot primary = snap?.Self?.Armies?.FirstOrDefault(x => x != null
                && x.ArmyId == a.PrimaryArmyId.Value);
            if (primary == null || !primary.IsStructuralRaidActor)
                return RetreatOutcome.NotTriggered;
            // lastLocalTurn = this turn: the mandatory path-contact rule only, no voluntary search
            AttackLocalAction local = AttackTacticalOpportunity.Decide(snap, primary, a.Target,
                snap.TurnNumber, intermediateBaseAvailable: false, adObjectives: null);
            if (local.Kind != AttackLocalActionKind.Retreat)
                return RetreatOutcome.NotTriggered;

            HexCoord? home = AiReturnBasePolicy.SelectReturnBase(snap, player, a.PrimaryArmyId);
            if (!home.HasValue)
            {
                AiDebugLog.Write($"[AI][V2][Attack][Retreat] {intent.IntentKey} retired — hostile army "
                    + $"#{local.EnemyArmyId} cannot be beaten (win {local.WinChance:0.00}) and the army "
                    + "has no own base to withdraw to; claims released");
                return RetreatOutcome.Retire;
            }
            a.Phase = AttackMissionPhase.RecoveryReturn;
            a.RecoveryBaseHex = home;
            a.TacticalRetreat = true;
            a.IntermediateTarget = AttackTargetRef.None;
            a.RendezvousHex = null;
            a.ReinforcementRequestedTurn = -1;
            // A support still walking to the primary has handed nothing over: it is simply free
            // again. Donors already walking home keep doing so (GatherReturns).
            a.SupportArmyId = null;
            a.SupportReturnHex = null;
            a.GatherSupportArmyIds.Clear();
            intent.StallTurns = 0;
            MissionIntentRegistry.GetOrCreate(player).PutRetreatWitness(new AttackRetreatWitness
            {
                Target = a.Target,
                OwnArmyId = primary.ArmyId,
                EnemyArmyId = local.EnemyArmyId,
                EnemyHex = local.Hex,
                EnemyFingerprint = local.ContactFingerprint,
                OwnFingerprint = AttackRetreatWitness.OwnFingerprintOf(primary),
                Turn = snap.TurnNumber,
                Reason = local.Reason,
            });
            AiDebugLog.Write($"[AI][V2][Attack][Retreat] {intent.IntentKey} phase -> RecoveryReturn "
                + $"({home.Value.Q},{home.Value.R}); relevant hostile army #{local.EnemyArmyId} "
                + $"({local.EnemyName}) at ({local.Hex.Q},{local.Hex.R}) win={local.WinChance:0.00} "
                + $"< {GroundCombatAdmissionPolicy.AttackLocalWinChanceGate:0.00}; witness recorded");
            return RetreatOutcome.Withdrawing;
        }

        // 2026-10-04 — a COMMITTED Assault (AssaultStarted: the fist began its march on the target).
        // Worse news about the defenders never revokes it: no RecoveryReturn, no Production
        // request (AppendAttackDemands skips it), no new strategic target. The only question is
        // whether an army ALREADY on the map can join the primary on its route to the target:
        //   primary clears                         -> Assault (a bound support is released)
        //   it does not, a bound support still
        //     improves it and still meets it ahead -> Reinforcement continues (rendezvous refreshed)
        //   otherwise the best other such support  -> Reinforcement with that one
        //   none                                   -> Assault continues with the current roster
        // Structural ends (target captured / invalidated, primary only a remnant or gone) are
        // decided above, by the same rules as before.
        private static bool ResolveCommittedAssault(WorldSnapshot snap, MissionIntent intent,
            AttackIntent a, bool clears, ISet<int> unavailableArmyIds)
        {
            a.ReinforcementRequestedTurn = -1;
            if (clears)
            {
                if (a.Phase != AttackMissionPhase.Assault)
                    AiDebugLog.Write($"[AI][V2][Attack] {intent.IntentKey} phase {a.Phase} -> Assault "
                        + $"(primary #{a.PrimaryArmyId} clears the site"
                        + (a.SupportArmyId.HasValue ? $"; support #{a.SupportArmyId} released)" : ")"));
                ReleaseCommittedReinforcement(a);
                return true;
            }

            ArmySnapshot primary = snap.Self.Armies?.FirstOrDefault(x => x != null
                && x.ArmyId == a.PrimaryArmyId.Value);
            IReadOnlyList<WorthIt.DefendingArmy> opposition =
                AttackObjectiveEvaluator.KnownSiteOpposition(snap, a.Target.Hex);
            float hexBonus = AttackObjectiveEvaluator.KnownSiteDefenceBonus(snap, null, a.Target.Hex);

            if (a.Phase == AttackMissionPhase.Reinforcement && a.SupportArmyId.HasValue)
            {
                ArmySnapshot bound = snap.Self.Armies?.FirstOrDefault(x => x != null
                    && x.ArmyId == a.SupportArmyId.Value);
                string why = null;
                RendezvousPlan? meet = null;
                if (bound == null || !GroundCombatAssemblyPlanner.SupportImprovesPrimary(primary, bound,
                        opposition, hexBonus, allowCommandHandover: true, allowCompleteTransfer: true))
                    why = "no longer improves the primary";
                else
                    meet = GroundCombatRendezvous.SelectForward(snap, primary, bound, a.Target.Hex,
                        AiConfigV2.attackReinforcementMaxWaitTurns, out why, keep: a.RendezvousHex);
                if (meet.HasValue)
                {
                    if (!a.RendezvousHex.HasValue || !a.RendezvousHex.Value.Equals(meet.Value.Hex))
                        AiDebugLog.Write($"[AI][V2][Attack][Reinforcement] {intent.IntentKey} support "
                            + $"#{a.SupportArmyId} -> primary #{a.PrimaryArmyId} {meet.Value.Describe()}");
                    a.RendezvousHex = meet.Value.Hex;
                    return true;
                }
                AiDebugLog.Write($"[AI][V2][Attack][Reinforcement] {intent.IntentKey} support "
                    + $"#{a.SupportArmyId} dropped: {why}; looking for another existing support");
                ReleaseCommittedReinforcement(a);
            }

            int? supportId = FindOperationalReinforcement(snap, intent, a, primary, opposition,
                hexBonus, unavailableArmyIds, out RendezvousPlan plan, out string none);
            if (supportId.HasValue)
            {
                AiDebugLog.Write($"[AI][V2][Attack][Reinforcement] {intent.IntentKey} support "
                    + $"#{supportId.Value} improves primary #{a.PrimaryArmyId}; {plan.Describe()}; "
                    + $"phase {a.Phase} -> Reinforcement");
                a.Phase = AttackMissionPhase.Reinforcement;
                a.SupportArmyId = supportId;
                a.RendezvousHex = plan.Hex;
                return true;
            }

            a.Phase = AttackMissionPhase.Assault;
            AiDebugLog.WriteDeduped(intent.IntentKey + "#committed",
                $"[AI][V2][Attack][Continuity] {intent.IntentKey} primary #{a.PrimaryArmyId} no longer "
                + $"clears {a.Target.DiagnosticLabel} ({DescribeClearance(snap, a)}); existing "
                + $"reinforcement candidate none ({none}); committed assault continues");
            return true;
        }

        // The committed Reinforcement's support: an EXISTING free army the one assembly owner says
        // improves the primary (ReinforcementSupportCandidates — the same handoff projection
        // Provisioning and Execution run), that can meet it on its route to the target
        // (GroundCombatRendezvous). Least primary wait, then the support's cheapest walk, then the
        // lowest id. Armies this operation already sends home are never recalled.
        private static int? FindOperationalReinforcement(WorldSnapshot snap, MissionIntent intent,
            AttackIntent a, ArmySnapshot primary, IReadOnlyList<WorthIt.DefendingArmy> opposition,
            float hexBonus, ISet<int> unavailableArmyIds, out RendezvousPlan best, out string none)
        {
            best = default;
            none = "no free army improves the primary";
            var excluded = unavailableArmyIds == null ? new HashSet<int>() : new HashSet<int>(unavailableArmyIds);
            foreach (AttackGatherReturn r in a.GatherReturns)
                excluded.Add(r.ArmyId);
            List<int> candidates = GroundCombatAssemblyPlanner.ReinforcementSupportCandidates(snap,
                a.PrimaryArmyId.Value, opposition, excluded, hexBonus, allowCommandHandover: true);
            int? bestId = null;
            var rejected = new List<string>();
            foreach (int id in candidates.OrderBy(x => x))
            {
                ArmySnapshot support = snap.Self.Armies.FirstOrDefault(x => x != null && x.ArmyId == id);
                RendezvousPlan? meet = GroundCombatRendezvous.SelectForward(snap, primary, support,
                    a.Target.Hex, AiConfigV2.attackReinforcementMaxWaitTurns, out string why);
                if (!meet.HasValue)
                {
                    rejected.Add($"#{id}: handoff improves primary but {why}");
                    continue;
                }
                if (!bestId.HasValue || meet.Value.WaitTurns < best.WaitTurns
                    || (meet.Value.WaitTurns == best.WaitTurns && meet.Value.SupportCost < best.SupportCost))
                {
                    bestId = id;
                    best = meet.Value;
                }
            }
            if (rejected.Count > 0)
            {
                none = $"{candidates.Count} improving support(s) rejected";
                AiDebugLog.WriteDeduped(intent.IntentKey + "#support-rejected",
                    $"[AI][V2][Attack][Reinforcement] {intent.IntentKey} support rejected: "
                    + string.Join(" | ", rejected));
            }
            return bestId;
        }

        // Ends a committed Reinforcement that never handed off: the support is simply free again.
        private static void ReleaseCommittedReinforcement(AttackIntent a)
        {
            a.Phase = AttackMissionPhase.Assault;
            a.SupportArmyId = null;
            a.RendezvousHex = null;
        }

        // Strike force step 5 — gather donors that already handed over walk home. The base is
        // chosen (and re-chosen when lost) by the one own-Base selection owner; a donor leaves the
        // list on arrival, when its container is gone, or when no home is reachable.
        private static void ResolveGatherReturns(WorldSnapshot snap, PlayerSetupData player,
            MissionIntent intent, AttackIntent a)
        {
            a.GatherReturns.RemoveAll(r =>
            {
                ArmySnapshot s = snap?.Self?.Armies?.FirstOrDefault(x => x != null
                    && x.ArmyId == r.ArmyId);
                if (s == null || !MissionActorPolicy.GroundContainerStillValid(r.ArmyId, snap))
                    return true;
                r.BaseHex = AiReturnBasePolicy.KeepOrReselectHome(snap, player, r.ArmyId, r.BaseHex, out _);
                bool done = !r.BaseHex.HasValue || s.Hex.Equals(r.BaseHex.Value);
                if (done)
                    AiDebugLog.Write($"[AI][V2][Attack][Gather] {intent.IntentKey} donor #{r.ArmyId} "
                        + (r.BaseHex.HasValue ? "is home" : "has no reachable home base")
                        + " — released");
                return done;
            });
        }

        // Strike force — the fist's air support, the one GroundCombatAirSupport (as Raid's and
        // ActiveDefence's). A free formed wing is bound while the operation is in Assault — however
        // far or close the primary is (ETA is a cost, not a launch window), whether or not the
        // site's defenders are known or fresh, with no minimum win gain: the basis is the existing
        // Attack task, a recoverable route and the launch fitting the free bank (provisioning).
        // The wing may arrive before the fist; the assault never waits for the series. It stays
        // bound while its strike series flies (never orphaned mid-air) and is released once the
        // series is over (sortie turned home), when it never took off by a later turn, or when it
        // stops being a valid wing.
        private static void ResolveAttackAirSupport(WorldSnapshot snap, PlayerSetupData player,
            MissionIntent intent, AttackIntent a, ISet<int> unavailableArmyIds)
        {
            int turn = snap?.TurnNumber ?? 0;
            if (GroundCombatAirSupport.TargetKnownEmpty(snap, a.Target.Hex, Game.Aviation.AirStrikePolicy.Standard))
            {
                if (a.AirSupportArmyId.HasValue)
                {
                    var wing = AiV2Util.ResolveArmy(player, a.AirSupportArmyId.Value);
                    var sortie = AirSortieRegistry.ForArmy(player, wing);
                    if (sortie != null && sortie.Kind == AirSortieKind.Strike)
                        GroundCombatAirSupport.SendHome(player, sortie, "nothing left to strike");
                    ReleaseAttackAirSupport(a, turn);
                }
                return;
            }
            if (a.AirSupportArmyId.HasValue)
            {
                bool flying = GroundCombatAirSupport.SortieLive(player, a.AirSupportArmyId,
                    out bool wingValid);
                if (flying)
                {
                    a.AirSupportSortieSeen = true;
                    return;
                }
                if (wingValid && !a.AirSupportSortieSeen && a.AirSupportBoundTurn >= turn)
                    return;
                AiDebugLog.Write($"[AI][V2][Attack][AirSupport] {intent.IntentKey} wing "
                    + $"#{a.AirSupportArmyId} released ("
                    + (!wingValid ? "no longer a valid wing" : a.AirSupportSortieSeen ? "series over" : "never took off")
                    + ")");
                ReleaseAttackAirSupport(a, turn);
                return;
            }

            if (a.Phase != AttackMissionPhase.Assault || a.AirSupportAttemptedTurn == turn
                || snap?.Self?.Armies == null)
                return;
            ArmySnapshot primary = a.PrimaryArmyId.HasValue
                ? snap.Self.Armies.FirstOrDefault(x => x != null && x.ArmyId == a.PrimaryArmyId.Value)
                : null;

            // Known defenders only RANK the wings; an unknown site is supported all the same.
            IReadOnlyList<WorthIt.DefendingArmy> opposition =
                AttackObjectiveEvaluator.KnownSiteOpposition(snap, a.Target.Hex);
            Func<IReadOnlyList<WorthIt.DefendingArmy>, float> win = null;
            float current = 0f;
            if (primary != null)
            {
                float hexBonus = AttackObjectiveEvaluator.KnownSiteDefenceBonus(snap, null, a.Target.Hex);
                IReadOnlyList<WorthIt.DefenderProfile> roster = primary.Members
                    ?? (IReadOnlyList<WorthIt.DefenderProfile>)System.Array.Empty<WorthIt.DefenderProfile>();
                win = opp => WorthIt.EstimateSequential(roster, primary.Commander, opp, hexBonus).WinChance;
                current = win(opposition);
            }

            // A wing already flying any sortie is not free for this one.
            var unavailable = unavailableArmyIds == null
                ? new HashSet<int>() : new HashSet<int>(unavailableArmyIds);
            foreach (ArmySnapshot w in snap.Self.Armies)
                if (w != null && w.IsAir && AirSortieRegistry.ForArmy(player,
                        AiV2Util.ResolveArmy(player, w.ArmyId)) != null)
                    unavailable.Add(w.ArmyId);

            List<AirSupportOption> options = GroundCombatAirSupport.Ranked(GroundCombatAirSupport.Options(
                snap, opposition, a.Target.Hex, AirStrikePolicy.Standard, win, current, unavailable));
            if (options.Count == 0)
                return;
            AirSupportOption best = options[0];
            a.AirSupportArmyId = best.WingArmyId;
            a.AirSupportLandingHex = best.LandingHex;
            a.AirSupportBoundTurn = turn;
            a.AirSupportSortieSeen = false;
            AiDebugLog.Write($"[AI][V2][Attack][AirSupport] {intent.IntentKey} bound wing "
                + $"#{best.WingArmyId} for {a.Target.DiagnosticLabel}: "
                + (best.RosterKnown ? $"expected damage {best.ExpectedDamage:0.0} over {best.StrikeTurns} strike turn(s), win {current:0.00} -> {best.WinAfter:0.00}"
                    : $"defenders unknown, {best.StrikeTurns} strike turn(s)")
                + $", eta {best.EtaTurns}, landing ({best.LandingHex.Q},{best.LandingHex.R})");
        }

        private static void ReleaseAttackAirSupport(AttackIntent a, int turn)
        {
            a.AirSupportArmyId = null;
            a.AirSupportLandingHex = null;
            a.AirSupportSortieSeen = false;
            a.AirSupportAttemptedTurn = turn;
        }

        // The one "support leaves an Attack" edge after SupportReturn: release the claim and hand
        // the operation back to the Assault / Reinforcement decision below (§24), which re-reads
        // whether the primary clears the site on its own.
        private static void ReleaseAttackSupport(AttackIntent a)
        {
            a.SupportArmyId = null;
            a.SupportReturnHex = null;
            a.Phase = AttackMissionPhase.Assault;
        }

        // Audit F7 — the Gather phase. The host (PrimaryArmyId) holds; every support in
        // GatherSupportArmyIds walks to it and hands over (AdvanceIntent drops a support once its
        // handoff was attempted, and it walks home). Strike force step 5: the gather builds the
        // fist to its PEAK, so the host marches once every planned support is spent and it clears
        // the current force threshold — or earlier, when it already clears and a leg has stalled. When every
        // planned support is spent and the host still falls short, the gather is re-planned around
        // the same host from what is free now; if nothing can complete it, the existing
        // Reinforcement path takes over (partial improvement, then the Production demand, then
        // RecoveryReturn).
        private static bool ResolveAttackGather(WorldSnapshot snap, MissionIntent intent,
            AttackIntent a, ISet<int> unavailableArmyIds)
        {
            if (a.Preparation)
                return ResolveAttackPreparation(snap, intent, a, unavailableArmyIds);
            IReadOnlyList<WorthIt.DefendingArmy> opposition =
                AttackObjectiveEvaluator.KnownSiteOpposition(snap, a.Target.Hex);
            float hexBonus = AttackObjectiveEvaluator.KnownSiteDefenceBonus(snap, null, a.Target.Hex);

            // A support that stopped existing, or whose bodies no longer improve the host (arrival
            // order filled the host differently than planned, defenders changed), leaves the plan:
            // otherwise its leg would be rejected by Provisioning forever and the host would hold
            // for nothing.
            ArmySnapshot host = snap?.Self?.Armies?.FirstOrDefault(x => x != null
                && x.ArmyId == a.PrimaryArmyId.Value);
            List<int> dropped = a.GatherSupportArmyIds.Where(id =>
            {
                ArmySnapshot s = snap?.Self?.Armies?.FirstOrDefault(x => x != null && x.ArmyId == id);
                // A support stays while its bodies improve the host OR its hero would take command
                // (the plan may keep it for that alone).
                return s == null || !MissionActorPolicy.GroundContainerStillValid(id, snap)
                    || !GroundCombatAssemblyPlanner.SupportImprovesPrimary(host, s, opposition, hexBonus,
                        allowCommandHandover: true, allowCompleteTransfer: true);
            }).ToList();
            if (dropped.Count > 0)
            {
                a.GatherSupportArmyIds.RemoveAll(dropped.Contains);
                AiDebugLog.Write($"[AI][V2][Attack][Gather] {intent.IntentKey} support(s) "
                    + $"[{string.Join(",", dropped)}] lost or no longer improve host #{a.PrimaryArmyId}; "
                    + $"remaining [{string.Join(",", a.GatherSupportArmyIds)}]");
            }

            if ((a.GatherSupportArmyIds.Count == 0 || intent.StallTurns > 0)
                && AttackPrimaryClearsTarget(snap, a))
            {
                AiDebugLog.Write($"[AI][V2][Attack][Gather] {intent.IntentKey} phase Gather -> Assault "
                    + $"(host #{a.PrimaryArmyId} clears {a.Target.DiagnosticLabel} "
                    + $"win={a.ProjectedWinChance:0.00}); released supports "
                    + $"[{string.Join(",", a.GatherSupportArmyIds)}]");
                a.GatherSupportArmyIds.Clear();
                a.Phase = AttackMissionPhase.Assault;
                return true;
            }
            if (a.GatherSupportArmyIds.Count > 0)
                return true;

            var unavailable = unavailableArmyIds == null
                ? new HashSet<int>() : new HashSet<int>(unavailableArmyIds);
            unavailable.Remove(a.PrimaryArmyId.Value);
            // ATK-F05 — Continuity keeps accepted legs and re-plans with FREE armies only (a
            // completed Raid's unclaimed fallback included); an army another operation holds is
            // bought only by a fresh, priced proposal that wins the allocator.
            GroundCombatGatherPlan plan = GroundCombatAssemblyPlanner.PlanGather(snap, opposition,
                hexBonus, a.Target.Hex, unavailable, GroundCombatAdmissionPolicy.AttackCoverageGate,
                a.PrimaryArmyId, null,
                minimumArmyPower: AttackForceReadiness.RequiredPower(snap.Self.AttackPeak));
            if (plan.Feasible && plan.SupportArmyIds.Count > 0)
            {
                a.GatherSupportArmyIds.AddRange(plan.SupportArmyIds);
                intent.StallTurns = 0;
                AiDebugLog.Write($"[AI][V2][Attack][Gather] {intent.IntentKey} re-planned around host "
                    + $"#{a.PrimaryArmyId}: supports [{string.Join(",", plan.SupportArmyIds)}] "
                    + $"win={plan.ProjectedWinChance:0.00} gatherTurns={plan.GatherTurns} "
                    + $"assaultEta={plan.AssaultEta} ap={plan.TotalAp}");
                return true;
            }

            a.Phase = AttackMissionPhase.Reinforcement;
            a.ReinforcementRequestedTurn = -1;
            AiDebugLog.Write($"[AI][V2][Attack][Gather] {intent.IntentKey} phase Gather -> Reinforcement "
                + $"(host #{a.PrimaryArmyId} still short and no complete gather remains: {plan.Reason})");
            return true;
        }

        // T01 — the mobilization preparation inside the Gather phase. The host (PrimaryArmyId)
        // stays; supports that no longer raise it leave; it becomes an ordinary Assault only when it
        // strictly clears the CURRENT peak with known coverage (AttackPrimaryClearsTarget re-reads
        // TotalMilitaryPotential every pass, so a reward before the march raises the bar) and the
        // target has really been observed. Waiting for movement, AP, a same-hex step or a pinned
        // card delivery is a legal continuation (protected from the stall clock); with no legal
        // continuation left the existing stall lifecycle ends it — no private timer.
        private static bool ResolveAttackPreparation(WorldSnapshot snap, MissionIntent intent,
            AttackIntent a, ISet<int> unavailableArmyIds)
        {
            IReadOnlyList<WorthIt.DefendingArmy> opposition =
                AttackObjectiveEvaluator.KnownSiteOpposition(snap, a.Target.Hex);
            float hexBonus = AttackObjectiveEvaluator.KnownSiteDefenceBonus(snap, null, a.Target.Hex);
            int hostId = a.PrimaryArmyId.Value;
            ArmySnapshot host = snap.Self.Armies.FirstOrDefault(x => x != null && x.ArmyId == hostId);
            var unavailable = unavailableArmyIds == null
                ? new HashSet<int>() : new HashSet<int>(unavailableArmyIds);
            unavailable.Remove(hostId);
            float required = AttackForceReadiness.RequiredPower(snap.Self.AttackPeak);
            RefreshTargetRoster(snap, intent, a, AiV2Util.ResolveArmy(snap.Observer, hostId));
            string at = $"{intent.IntentKey} host=#{hostId} hex=({host.Hex.Q},{host.Hex.R}) "
                + $"roster={host.MemberCount}/{host.Capacity} fist={host.EffectiveArmyPower:0.#} "
                + $"ideal={snap.Self.AttackPeak:0.#} required>{required:0.#}";

            // An empty shell has nothing invested in it: a free field army that can host the fist
            // supersedes it (the shell stays a reusable, paid container).
            if (host.MemberCount == 0 && GroundCombatActorEligibility.EligibleArmies(snap, unavailable,
                    requireMovementNow: false).Any(x => x.ArmyId != hostId))
            {
                AiDebugLog.Write($"[AI][V2][Attack][Mobilization] {at} retired — empty preparation "
                    + "shell superseded by a free field army; the next pass re-selects the host");
                return false;
            }

            List<int> dropped = a.GatherSupportArmyIds.Where(id =>
            {
                ArmySnapshot s = snap.Self.Armies.FirstOrDefault(x => x != null && x.ArmyId == id);
                return s == null || !MissionActorPolicy.GroundContainerStillValid(id, snap)
                    || host.MemberCount == 0
                    || !GroundCombatAssemblyPlanner.SupportImprovesPrimary(host, s, opposition, hexBonus,
                        allowCommandHandover: true, allowCompleteTransfer: true);
            }).ToList();
            if (dropped.Count > 0)
            {
                a.GatherSupportArmyIds.RemoveAll(dropped.Contains);
                AiDebugLog.Write($"[AI][V2][Attack][Mobilization] {at} support(s) "
                    + $"[{string.Join(",", dropped)}] lost or no longer raise the host");
            }

            // 2026-10-01 (variant B) — the fetched commander stays the operation's while its
            // lone-hero container still exists outside any garrison; once its hero leads the host
            // (the handoff empties the container) or it is lost, the marker clears.
            if (a.CommanderArmyId.HasValue)
            {
                ArmyData commanderArmy = AiV2Util.ResolveArmy(snap.Observer, a.CommanderArmyId.Value);
                if (commanderArmy == null || commanderArmy.IsGarrison
                    || !commanderArmy.Members.Any(u => u != null && u.IsHero))
                {
                    AiDebugLog.Write($"[AI][V2][Attack][Mobilization] {at} commander container "
                        + $"#{a.CommanderArmyId.Value} gone or without its hero — released");
                    a.CommanderArmyId = null;
                }
            }

            bool locationOnly = AttackObjectiveEvaluator.IsLocationOnly(snap, a.Target);
            if ((a.GatherSupportArmyIds.Count == 0 || intent.StallTurns > 0)
                && AttackPrimaryClearsTarget(snap, a))
            {
                // 2026-10-01 (user decision) — a fist at the peak has nothing left to wait for:
                // an unobserved (location-only) target no longer holds it. It marches, observes on
                // the way, and every pass of the march re-checks the then-known defenders.
                AiDebugLog.Write($"[AI][V2][Attack][Mobilization] {at} decision=ASSAULT-READY "
                    + $"(fist strictly > 80% of the current peak, coverage "
                    + $"{(locationOnly ? "unknown: target not yet observed" : a.CoversAllDefenders ? "ok" : "missing")}); "
                    + $"Gather -> Assault, released supports [{string.Join(",", a.GatherSupportArmyIds)}]");
                a.GatherSupportArmyIds.Clear();
                a.CommanderArmyId = null;
                a.Preparation = false;
                a.Phase = AttackMissionPhase.Assault;
                return true;
            }
            // A walking commander is progress (protected from the stall clock); the rest of the
            // preparation goes on meanwhile.
            if (a.CommanderArmyId.HasValue)
                intent.LastProtectedTurn = snap.TurnNumber;
            if (a.GatherSupportArmyIds.Count > 0)
            {
                intent.LastProtectedTurn = snap.TurnNumber;
                return true;
            }

            // The fist assembles on its staging Base (the own Base nearest to the target): a host
            // standing elsewhere walks there first (the planner's MoveHost leg); supports are
            // planned only once it has arrived. No own Base left -> the ordinary stall lifecycle.
            HexCoord? staging = AttackPreparationPolicy.PreparationStagingBase(snap, a.Target.Hex, host);
            if (host.MemberCount > 0 && (!staging.HasValue || !host.Hex.Equals(staging.Value)))
            {
                if (staging.HasValue)
                    intent.LastProtectedTurn = snap.TurnNumber;
                AiDebugLog.WriteDeduped(intent.IntentKey + "#wait",
                    $"[AI][V2][Attack][Mobilization] {at} decision="
                    + (staging.HasValue
                        ? $"WAIT next=host_to_staging staging=({staging.Value.Q},{staging.Value.R})"
                        : "STALL blocker=no_own_base_reachable_by_host"));
                return true;
            }

            GroundCombatGatherPlan plan = host.MemberCount == 0
                ? GroundCombatGatherPlan.Infeasible("empty host takes no walking support")
                // ATK-F05 — free armies only; a bought donor is the planner's priced
                // RecruitDonors proposal (AggressionMissionPlanner.AppendPreparationRecruit).
                : GroundCombatAssemblyPlanner.PlanGather(snap, opposition, hexBonus, a.Target.Hex,
                    unavailable, GroundCombatAdmissionPolicy.AttackCoverageGate, hostId, null,
                    minimumArmyPower: required, allowPartial: true);
            if (plan.Feasible && plan.SupportArmyIds.Count > 0)
            {
                a.GatherSupportArmyIds.AddRange(plan.SupportArmyIds);
                a.PreparationCardWaitSinceTurn = -1;
                intent.StallTurns = 0;
                intent.LastProtectedTurn = snap.TurnNumber;
                AiDebugLog.Write($"[AI][V2][Attack][Mobilization] {at} re-planned supports "
                    + $"[{string.Join(",", plan.SupportArmyIds)}] projected={plan.ProjectedPower:0.#} "
                    + $"reachesThreshold={plan.ReachesThreshold} gatherTurns={plan.GatherTurns}");
                return true;
            }

            ArmyData liveHost = AiV2Util.ResolveArmy(snap.Observer, hostId);
            bool sameHexStep = liveHost != null && GroundCombatAssemblyPlanner.PlanPreparationAssembly(
                snap, liveHost, unavailable, opposition, hexBonus).Feasible;
            // ATK-F02 — a WAIT names its concrete source; a positive Reserve alone is no delivery
            // into this exact (possibly full) host.
            string cardSource = sameHexStep ? null
                : AggressionDemandEvaluator.PreparationHostCardSource(snap, liveHost, a.TargetRoster,
                    a.Target.Hex);
            // A support another operation holds is not a WAIT witness: it is bought only by the
            // priced RecruitDonors proposal, whose execution resets the stall (AdvanceIntent).
            if (sameHexStep || cardSource != null)
            {
                intent.LastProtectedTurn = snap.TurnNumber;
                if (sameHexStep)
                    a.PreparationCardWaitSinceTurn = -1;
                else
                {
                    if (a.PreparationCardWaitSinceTurn < 0)
                        a.PreparationCardWaitSinceTurn = snap.TurnNumber;
                    // Waiting on a card that may never be drawn must not hold the one live
                    // operation hostage: a known target the host's CURRENT fist already clears
                    // (cheaper or nearer than the bar-setting one) takes over on the next pass.
                    if (snap.TurnNumber - a.PreparationCardWaitSinceTurn
                        >= AiConfigV2.attackPreparationCardWaitTurns
                        && AttackPreparationPolicy.ClearedAlternativeTarget(snap, a.Target, hostId, out string alt))
                    {
                        AiDebugLog.Write($"[AI][V2][Attack][Mobilization] {at} retired — waited "
                            + $"{snap.TurnNumber - a.PreparationCardWaitSinceTurn} turn(s) for "
                            + $"{cardSource}; the host's fist already clears {alt}, the next pass "
                            + "re-selects the target");
                        return false;
                    }
                }
                AiDebugLog.WriteDeduped(intent.IntentKey + "#wait",
                    $"[AI][V2][Attack][Mobilization] {at} decision=WAIT "
                    + $"next={(sameHexStep ? "same_hex_assembly" : "pinned_card_delivery")} "
                    + $"witness={cardSource ?? "same_hex_body"} "
                    + $"clearance={DescribeClearance(snap, a)} "
                    + $"missing=[{MissingLabel(a.TargetRoster, liveHost)}] gather={plan.Reason}");
                return true;
            }
            // Name what keeps the host from marching: a fist above the bar can still fail the
            // win-chance gate or leave a known defender no body can damage (coverage).
            string clearance = DescribeClearance(snap, a);
            AiDebugLog.WriteDeduped(intent.IntentKey + "#wait",
                $"[AI][V2][Attack][Mobilization] {at} decision=STALL blocker=no_legal_source "
                + $"clearance={clearance} "
                + $"(no support, no same-hex body, no hand/deck/generated card strengthens this host"
                + $"{(snap.Self.BaseHexes?.Contains(host.Hex) == true ? "" : " off an own Base")}: "
                + $"{plan.Reason}); the existing stall lifecycle ends the preparation");
            return true;
        }

        // 2026-10-01 (user decision) — the preparation gathers toward a frozen target roster
        // (StrikeRoster), composed under the commander its host actually has (variant B): the
        // bodies come from the whole pool, capped by that commander's CommandRating. Frozen at
        // the first pass; re-frozen only when the host's commander changed (a fetched commander
        // took over), the peak grew by more than attackTargetRosterRefreezeGrowth, or a frozen
        // position left the whole pool (a unit died, a card was spent elsewhere) — a small
        // reshuffle of the greedy pick never churns it. Housekeeping may take armies apart
        // meanwhile: the roster is by card key, not by army, so the host re-gathers it.
        private static void RefreshTargetRoster(WorldSnapshot snap, MissionIntent intent, AttackIntent a,
            ArmyData liveHost)
        {
            UnitData commander = liveHost?.Commander;
            string commanderKey = commander == null ? null : StrikeRoster.UnitKey(commander);
            string why = null;
            if (a.TargetRoster == null || a.TargetRoster.Count == 0)
                why = "frozen";
            else if (commanderKey != a.TargetRosterCommanderKey)
                why = $"commander {a.TargetRosterCommanderKey ?? "none"}->{commanderKey ?? "none"}";
            else if (snap.Self.AttackPeak > a.TargetRosterPeak
                     * (1f + AiConfigV2.attackTargetRosterRefreezeGrowth))
                why = $"peak {a.TargetRosterPeak:0.#}->{snap.Self.AttackPeak:0.#}";
            else
            {
                var pool = snap.Self.StrikePoolKeyCounts.ToDictionary(kv => kv.Key, kv => kv.Value);
                foreach (StrikeRosterSlot slot in a.TargetRoster)
                {
                    // The commander slot is the host's own hero (pinned by TargetRosterCommanderKey,
                    // checked above), not a pool pick: a garrison-tagged commander is never in the pool.
                    if (slot.IsHero) continue;
                    pool.TryGetValue(slot.Key, out int n);
                    if (n <= 0) { why = $"position {slot.Key} left the pool"; break; }
                    pool[slot.Key] = n - 1;
                }
            }
            if (why == null || snap.Self.StrikePool == null || snap.Self.StrikePool.Count == 0)
                return;
            int capacity = liveHost != null ? liveHost.Capacity
                : ArmyData.ComputeCapacity(System.Array.Empty<UnitData>(), false);
            List<StrikeRosterSlot> roster = StrikeRoster.ComposeUnder(snap.Self.StrikePool,
                commander == null ? (StrikeRosterCandidate?)null : StrikeRoster.CommanderCandidate(commander),
                capacity, out float power);
            a.TargetRoster = roster;
            a.TargetRosterPeak = snap.Self.AttackPeak;
            a.TargetRosterCommanderKey = commanderKey;
            a.TargetRosterPower = power;
            AiDebugLog.Write($"[AI][V2][Attack][Mobilization] {intent.IntentKey} target roster {why}: "
                + $"[{string.Join(",", a.TargetRoster.Select(x => x.IsHero ? x.Key + "*" : x.Key))}] "
                + $"power={power:0.#} capacity={capacity} peak={a.TargetRosterPeak:0.#}");
        }

        private static string MissingLabel(IReadOnlyList<StrikeRosterSlot> target, ArmyData host) =>
            target == null || host == null ? "-"
                : string.Join(",", StrikeRoster.Missing(target, host.Members)
                    .Select(m => $"{m.Key}({m.Source})"));

        // Does the bound primary, on its own, still clear the target site? The SAME shared estimator
        // and the SAME honest hex-defence read the mission layer used, with known defender coverage; before the march it also checks the current force threshold.
        // Why the primary does not clear the target yet: fist_below_bar (power not strictly above
        // the bar), coverage_missing (a known defender no roster body can damage), win_below_gate,
        // or ready (it clears; the assault starts on the next pass).
        private static string DescribeClearance(WorldSnapshot snap, AttackIntent a)
        {
            ArmySnapshot primary = a.PrimaryArmyId.HasValue
                ? snap.Self.Armies?.FirstOrDefault(x => x != null && x.ArmyId == a.PrimaryArmyId.Value)
                : null;
            return AttackPreparationReadiness.Assess(primary, snap.Self.AttackPeak,
                AttackObjectiveEvaluator.KnownSiteOpposition(snap, a.Target.Hex),
                AttackObjectiveEvaluator.KnownSiteDefenceBonus(snap, null, a.Target.Hex)).Reason;
        }

        private static bool AttackPrimaryClearsTarget(WorldSnapshot snap, AttackIntent a)
        {
            if (!a.PrimaryArmyId.HasValue)
                return false;
            IReadOnlyList<WorthIt.DefendingArmy> opposition =
                AttackObjectiveEvaluator.KnownSiteOpposition(snap, a.Target.Hex);
            // Continuity is snapshot-pure and has no map, so terrain is not in this read; the
            // remembered structural defence still is. The mission/provisioning layers, which do have
            // the map, apply the full bonus before anything is actually funded or executed.
            float hexBonus = AttackObjectiveEvaluator.KnownSiteDefenceBonus(snap, null, a.Target.Hex);
            ArmySnapshot primary = snap.Self.Armies?.FirstOrDefault(x => x != null
                && x.ArmyId == a.PrimaryArmyId.Value);
            AttackPreparationAssessment readiness = AttackPreparationReadiness.Assess(
                primary, snap.Self.AttackPeak, opposition, hexBonus);
            bool clears = a.AssaultStarted
                ? readiness.HostAvailable && readiness.StructuralActor && readiness.CombatFeasible
                : readiness.Ready;
            if (clears)
            {
                a.ProjectedWinChance = readiness.ProjectedWinChance;
                a.CoversAllDefenders = readiness.CoversAllDefenders;
            }
            return clears;
        }

        // §41/§46 — is there any EXISTING free army whose merge would improve the primary's odds?
        // The shared kernel answers it; a "no" here is what makes the shortage a real Demand.
        // Armies claimed by other operations are excluded exactly as the planner's unpinned leg
        // (AppendAttackUnpinnedReinforcement) and the Demand layer exclude them; counting them here
        // kept the operation waiting for a support no stage would ever deliver, so it was reaped
        // on stall instead of withdrawing through RecoveryReturn.
        private static bool AttackSupportCandidateExists(WorldSnapshot snap, AttackIntent a,
            ISet<int> unavailableArmyIds)
        {
            if (!a.PrimaryArmyId.HasValue)
                return false;
            IReadOnlyList<WorthIt.DefendingArmy> opposition =
                AttackObjectiveEvaluator.KnownSiteOpposition(snap, a.Target.Hex);
            float hexBonus = AttackObjectiveEvaluator.KnownSiteDefenceBonus(snap, null, a.Target.Hex);
            return GroundCombatAssemblyPlanner.ReinforcementSupportCandidates(snap,
                a.PrimaryArmyId.Value, opposition, unavailableArmyIds, hexBonus,
                allowCommandHandover: true).Count > 0;
        }

        // §70 — a durable intent is created only once the operation has REALLY begun (a step taken
        // or a battle fought), never on a bare candidate enumeration.
        internal static void CreateAttackIntent(MissionIntentState state, MissionStepResult o, int turn)
        {
            AttackMissionTarget t = o.AttackFacts().AttackTarget;
            var payload = new AttackIntent
            {
                Target = t.Target,
                Phase = t.Phase,
                OperationStarted = true,
                AssaultStarted = t.Phase == AttackMissionPhase.Assault,
                Preparation = t.Preparation && t.Phase == AttackMissionPhase.Gather,
                PrimaryArmyId = t.PrimaryArmyId ?? o.MoverArmyId,
                // A Gather leg's mover is one of several supports, never the Reinforcement support.
                SupportArmyId = t.Phase == AttackMissionPhase.Gather ? null : t.SupportArmyId,
                RecoveryBaseHex = t.RecoveryBaseHex,
                SupportReturnHex = t.SupportReturnHex,
                ProjectedWinChance = t.ProjectedWinChance,
                CoversAllDefenders = t.CoversAllDefenders,
                // §17 — the operation may well have BEGUN with its side strike, in which case the
                // intent is born having already spent this turn's one diversion. Anything else
                // would let the very first turn take two.
                LastOpportunisticStrikeTurn = o.AttackFacts().AttackOpportunisticStrike
                    ? turn : t.OpportunisticStrikeTurn,
            };
            // Audit F7 — an operation born from its first Gather step carries the whole frozen
            // plan; the support that already attempted its handoff on that step is done.
            if (t.Phase == AttackMissionPhase.Gather && t.GatherSupportArmyIds != null)
                payload.GatherSupportArmyIds.AddRange(t.GatherSupportArmyIds.Where(id =>
                    id != payload.PrimaryArmyId
                    && !(o.GroundFacts().ReinforcementHandoffAttempted && id == o.MoverArmyId)));
            MissionIntent intent = NewIntent(o, turn, MissionKind.Attack, CommitmentTier.Hard, payload);
            RetireReturnFallbacksForActor(state, payload.PrimaryArmyId,
                "fresh Attack admitted");
            foreach (int supportId in payload.GatherSupportArmyIds)
                RetireReturnFallbacksForActor(state, supportId, "fresh Attack gather admitted");
            state.Put(intent);
            AiDebugLog.Write($"[AI][V2][Attack] continuity — "
                + $"[{AiV2Trace.FormatCorrelation(o.Proposal)}] {intent.IntentKey} created "
                + $"(Hard attack on {t.Target.DiagnosticLabel}, primary #{payload.PrimaryArmyId}, "
                + $"phase {payload.Phase}"
                + (payload.Phase == AttackMissionPhase.Gather
                    ? $", gather supports [{string.Join(",", payload.GatherSupportArmyIds)}]" : "")
                + (payload.Preparation ? $", mobilization preparation via {t.PreparationStep}" : "")
                + ")");
        }

        // §71 — a started operation is NOT re-pointed at a slightly better-scoring target. This is
        // the only place Attack applies continuity protection, and it is a statement about identity,
        // not a score comparison: while the bound target is still valid and the operation is still
        // viable, a fresh candidate simply competes for a DIFFERENT intent next time this one ends.
        internal static bool AttackIntentIsProtected(MissionIntent intent) =>
            intent?.Attack != null && intent.Attack.OperationStarted
            && intent.Status == IntentStatus.Active;
        internal static void ClassifyAttackStep(ExecutionResult e, MissionStepResult o)
        {
            if (GroundCombatLegs.IsSupportLeg(o)
                && (e.StopReason == ExecutionStopReason.MoverLost
                    || e.StopReason == ExecutionStopReason.TargetInvalidated))
            {
                o.Disposition = MissionStepDisposition.Waiting;
                return;
            }
            MissionStepResultPolicy.ClassifyDefaultExecution(e, o);
        }

        private static bool TryHandleAttackSideLeg(MissionIntentState state, AiAllocatorState allocState,
            MissionIntent intent, MissionStepResult o, int turn)
        {
            string aid = AiV2Trace.FormatCorrelation(o.Proposal);
            // Strike force — a side leg (a gather donor walking home, the support wing's sortie) is
            // no step of the operation: whatever its outcome, the Attack intent's lifecycle
            // (progress, stall, suspension, retirement) is untouched. Arrival, landing or loss is
            // read from the next snapshot (ResolveGatherReturns / ResolveAttackAirSupport); a
            // failed leg releases just that donor or wing (an airborne wing then lands through
            // GroundCombatAirSupport.ReleaseOrphanStrikes).
            // The leg is read from the payload or, when Provisioning failed before one existed,
            // from the proposal (GroundCombatLegs.AttackLegOf): it shares the operation's
            // IntentKey, so the generic branches below would otherwise end the whole operation.
            AttackMissionTarget? attackLeg = GroundCombatLegs.AttackLegOf(o);
            // Failure of an optional base leg never invalidates its MAIN target. Claims/AP are
            // reconciled normally by the allocator; Continuity drops only the local choice.
            if (attackLeg.HasValue && attackLeg.Value.IsIntermediateAssault
                && !o.ObjectiveSatisfied && (o.IsFailed
                    || o.IsBlocked && !o.MadeProgress))
            {
                if (intent?.Attack != null)
                {
                    intent.Attack.IntermediateTarget = AttackTargetRef.None;
                    intent.Attack.LastOpportunisticStrikeTurn = turn;
                }
                AiDebugLog.Write($"[AI][V2][Attack][Intermediate] {o.IntentKey} "
                    + "local leg rejected; main operation preserved, no further detour this turn");
                return true;
            }
            if (attackLeg.HasValue && GroundCombatLegs.IsAttackSideLeg(attackLeg.Value.Phase))
            {
                AttackMissionTarget leg = attackLeg.Value;
                bool failed = o.IsFailed
                    || o.ProvisionFailureKindValue == ProvisionFailureKind.TargetInvalidated;
                if (failed && intent?.Attack != null
                    && leg.Phase == AttackMissionPhase.GatherReturn
                    && leg.SupportArmyId.HasValue)
                {
                    intent.Attack.GatherReturns.RemoveAll(r => r.ArmyId == leg.SupportArmyId.Value);
                    AiDebugLog.Write($"[AI][V2][Attack][Gather] continuity — [{aid}] {o.IntentKey} donor "
                        + $"#{leg.SupportArmyId.Value} walk home failed ({Describe(o)}); released");
                }
                if (failed && intent?.Attack != null
                    && leg.Phase == AttackMissionPhase.AirSupport
                    && intent.Attack.AirSupportArmyId == leg.AirSupportArmyId)
                {
                    ReleaseAttackAirSupport(intent.Attack, turn);
                    AiDebugLog.Write($"[AI][V2][Attack][AirSupport] continuity — [{aid}] {o.IntentKey} wing "
                        + $"#{leg.AirSupportArmyId} sortie failed ({Describe(o)}); released");
                }
                return true;
            }

            return false;
        }
        private static bool TryCompleteAttackLeg(MissionIntentState state, AiAllocatorState allocState,
            MissionIntent intent, MissionStepResult o, int turn)
        {
            string aid = AiV2Trace.FormatCorrelation(o.Proposal);
            // ATK §7/§8 — an Attack that reached its objective is DONE. One intent is one
            // target stronghold (Base/Citadel captured), so there is
            // deliberately no re-orient here: the army stays where it
            // is, the claim is released, and the next global replan decides what the new
            // topology is worth. An intent that still exists is advanced so ResolveActive
            // observes the capture through the ordinary path and logs the release once.
            if (o.MissionKind == MissionKind.Attack)
            {
                if (intent != null)
                {
                    AdvanceIntent(intent, o, turn, state, allocState);
                    AiDebugLog.Write($"[AI][V2][Attack] continuity — [{aid}] {o.IntentKey} "
                        + (o.AttackFacts().AttackTarget.Phase == AttackMissionPhase.Assault
                            ? "objective reached; operation ends at the captured site"
                            : $"{o.AttackFacts().AttackTarget.Phase} leg reached its goal"));
                    return true;
                }
                if (o.AttackFacts().HasAttackPayload && o.GroundFacts().OperationStarted)
                {
                    CreateAttackIntent(state, o, turn);
                    AiDebugLog.Write($"[AI][V2][Attack] continuity — [{aid}] {o.IntentKey} "
                        + "captured on its opening step; intent recorded for a clean release");
                    return true;
                }
            }

            return false;
        }

        private static bool TryCreateAttackStep(MissionIntentState state, AiAllocatorState allocState,
            MissionIntent intent, MissionStepResult o, int turn)
        {
            if (!(o.AttackFacts().HasAttackPayload && o.GroundFacts().OperationStarted)) return false;
            CreateAttackIntent(state, o, turn);
            return true;
        }

        private static bool TryPreserveAttackSupportMover(MissionIntentState state, AiAllocatorState allocator,
            MissionIntent intent, MissionStepResult o, int turn) =>
            intent.Attack != null && o.AttackFacts().HasAttackPayload
                && GroundCombatLegs.IsAttackSupportLeg(o.AttackFacts().AttackTarget.Phase);

        private static void ApplyAttackStepFacts(MissionIntentState state, AiAllocatorState allocator,
            MissionIntent intent, MissionStepResult o, int turn)
        {
            if (o.AttackFacts().HasAttackPayload && intent.Attack != null)
            {
                AttackIntent ai = intent.Attack;
                ai.OperationStarted |= o.GroundFacts().OperationStarted;
                if (o.AttackFacts().AttackIntermediateCaptured && o.AttackFacts().AttackTarget.IsIntermediateAssault)
                {
                    ai.RefitBaseHex = o.AttackFacts().AttackTarget.IntermediateTarget.Hex;
                    ai.RefitCaptureTurn = turn;
                    ai.RefitBattleStopTurn = o.AttackFacts().AttackCaptureHadBattle ? turn : -1;
                }
                if (o.GroundFacts().OperationStarted && o.AttackFacts().AttackTarget.Phase == AttackMissionPhase.Assault)
                {
                    ai.AssaultStarted = true;
                    ai.IntermediateTarget = o.AttackFacts().AttackOpportunisticStrike
                        ? AttackTargetRef.None : o.AttackFacts().AttackTarget.IntermediateTarget;
                }
                if (o.AttackFacts().AttackTarget.Phase == AttackMissionPhase.Gather)
                {
                    // Audit F7 — an attempted handoff (full, partial or rejected) ends that
                    // support's gather leg. Strike force step 5: whatever container is left walks
                    // home (GatherReturn; ResolveAttackIntent picks the base). It never becomes the
                    // Reinforcement support.
                    if (o.GroundFacts().ReinforcementHandoffAttempted && o.MoverArmyId.HasValue
                        && ai.GatherSupportArmyIds.Remove(o.MoverArmyId.Value)
                        && !ai.GatherReturns.Any(r => r.ArmyId == o.MoverArmyId.Value))
                        ai.GatherReturns.Add(new AttackGatherReturn { ArmyId = o.MoverArmyId.Value });
                    // 2026-10-01 (variant B) — the fetched commander: its container is recorded
                    // once created; an attempted handoff ends its leg (a body exchanged into the
                    // container walks home like any gather donor; an emptied shell just stays).
                    if (o.AttackFacts().AttackTarget.PreparationStep == AttackPreparationStep.FetchCommander
                        && o.MoverArmyId.HasValue && o.MoverArmyId.Value >= 0)
                    {
                        ai.CommanderArmyId = o.MoverArmyId.Value;
                        intent.StallTurns = 0;
                        intent.LastProtectedTurn = turn;
                    }
                    if (o.AttackFacts().AttackTarget.CommanderLeg && o.GroundFacts().ReinforcementHandoffAttempted
                        && o.MoverArmyId.HasValue && ai.CommanderArmyId == o.MoverArmyId.Value)
                    {
                        ai.CommanderArmyId = null;
                        if (!ai.GatherReturns.Any(r => r.ArmyId == o.MoverArmyId.Value))
                            ai.GatherReturns.Add(new AttackGatherReturn { ArmyId = o.MoverArmyId.Value });
                    }
                    // ATK-F05 — the one claim transition of a priced donor purchase: the frozen
                    // supports enter the operation; the lenders retire on the next pass
                    // ("given to an Attack gather"), their reservations with them.
                    if (o.AttackFacts().AttackTarget.PreparationStep == AttackPreparationStep.RecruitDonors
                        && o.AttackFacts().AttackTarget.GatherSupportArmyIds != null)
                    {
                        foreach (int id in o.AttackFacts().AttackTarget.GatherSupportArmyIds)
                            if (id != ai.PrimaryArmyId && !ai.GatherSupportArmyIds.Contains(id))
                                ai.GatherSupportArmyIds.Add(id);
                        intent.StallTurns = 0;
                        intent.LastProtectedTurn = turn;
                    }
                }
                else
                {
                    // The primary's own walk to the rendezvous only names the support; it never
                    // (re)binds it — Continuity alone does, and may already have released it.
                    if (o.AttackFacts().AttackTarget.SupportArmyId.HasValue && !o.AttackFacts().AttackTarget.PrimaryRendezvousLeg)
                        ai.SupportArmyId = o.AttackFacts().AttackTarget.SupportArmyId;
                    if (o.AttackFacts().AttackTarget.RecoveryBaseHex.HasValue)
                        ai.RecoveryBaseHex = o.AttackFacts().AttackTarget.RecoveryBaseHex;
                    // §46/§23 — a full/full swap displaced a primary body into the support
                    // container, so the whole support army must walk itself home. Same shared
                    // handoff semantics and the same SupportReturn leg the Raid lane uses.
                    // The destination itself is chosen by ResolveAttackIntent, which has the
                    // snapshot and the player: AdvanceIntent only records the immutable fact.
                    // 2026-10-04 — a committed Assault does not wait for that walk: the support's
                    // container goes home as a GatherReturn leg beside the operation, and the
                    // primary resumes its march at once.
                    if (o.GroundFacts().ReinforcementHandoffAttempted && ai.SupportArmyId.HasValue && ai.AssaultStarted
                        && o.AttackFacts().AttackTarget.Phase == AttackMissionPhase.Reinforcement)
                    {
                        int handedOver = ai.SupportArmyId.Value;
                        if (!ai.GatherReturns.Any(r => r.ArmyId == handedOver))
                            ai.GatherReturns.Add(new AttackGatherReturn { ArmyId = handedOver });
                        ai.SupportArmyId = null;
                        ai.RendezvousHex = null;
                        ai.Phase = AttackMissionPhase.Assault;
                    }
                    else if (o.GroundFacts().ReinforcementHandoffAttempted && ai.SupportArmyId.HasValue)
                        ai.Phase = AttackMissionPhase.SupportReturn;
                }
                // §17 — the operation's turn-local side-strike marker. Continuity is the only
                // writer; Execution merely reported that the diversion was really spent.
                if (o.AttackFacts().AttackOpportunisticStrike)
                    ai.LastOpportunisticStrikeTurn = turn;
            }
        }

        private static void RetireGatherDonors(Game.Players.PlayerSetupData player, WorldSnapshot snap, ActiveResolution pass)
        {
            var state = pass.State;
            var dead = pass.Dead;
            var donatedOperations = pass.DonatedOperations;
            // Strike force — a Raid whose primary an Attack gather bought ends here.
            // The gather priced the abandoned Raid into its own score
            // (GroundCombatDonorPolicy.BorrowableDonorValues) and won the allocation; the army now
            // walks to the host and, after the handoff, home. ActiveDefence responders are never
            // gather donors and therefore never enter this retirement path.
            var givenToGather = new HashSet<int>(state.All
                .Where(i => i?.Kind == MissionKind.Attack && i.Status == IntentStatus.Active
                    && i.Attack?.Phase == AttackMissionPhase.Gather)
                .SelectMany(i => i.Attack.GatherSupportArmyIds));
            foreach (MissionIntent lender in state.All.Where(i => i != null
                && i.Kind == MissionKind.Raid
                && i.PreferredMoverArmyId.HasValue
                && givenToGather.Contains(i.PreferredMoverArmyId.Value)))
            {
                donatedOperations.Add(lender.IntentKey);
                dead.Add(lender.IntentKey);
                AiDebugLog.Write($"[AI][V2][Attack][Gather] continuity — {lender.IntentKey} retired: "
                    + $"its army #{lender.PreferredMoverArmyId} was given to an Attack gather "
                    + $"(abandoned value {lender.LastIntrinsicValue:0.00})");
            }
        }

        private static void ResolveAttackOperation(Game.Players.PlayerSetupData player, WorldSnapshot snap, MissionIntent intent, ActiveResolution pass)
        {
            var state = pass.State;
            var active = pass.Active;
            var dead = pass.Dead;
            var rekeys = pass.Rekeys;
            var raidClaims = pass.ActorClaims;

            // ATK §24/§25 — the Attack lane's own lifecycle answers live in
            // MissionContinuityLayer.Attack.cs (a mechanical partial of this same owner).
            // Audit F7 — a Gather re-plan may not recruit another intent's actor. T07 — an
            // army walking home on a completed Raid's Return fallback is NOT another
            // intent's actor (the fallback has no commitment protection and no claim); if
            // the gather recruits it, its fallback is retired below in this same pass.
            pass.AttackGatherUnavailable = pass.AttackGatherUnavailable
                ?? new HashSet<int>(raidClaims ?? new HashSet<int>());
            if (!ResolveAttackIntent(player, snap, intent, intent.Attack,
                    pass.AttackGatherUnavailable, out bool captured))
            {
                dead.Add(intent.IntentKey);
                if (captured)
                    // §8/§59 — success releases the claim in place. Nothing here touches the
                    // army's roster or its garrison: Housekeeping owns local stabilisation.
                    AiDebugLog.Write($"[AI][V2][Attack] {intent.IntentKey} claim released on "
                        + "captured base; Housekeeping owns garrison stabilisation");
                return;
            }
            // A transient capability / pool suspension is re-tested every pass, exactly as
            // Raid and ActiveDefence do. Without this an Attack suspended once was never
            // resumed, never aged by ReconcileAfterTurn and never reaped.
            ResumeTransientSuspension(intent);
            if (intent.Status == IntentStatus.Active) active.Add(intent);
            return;
        }

        private static void RetireRecruitedReturnFallbacks(Game.Players.PlayerSetupData player, WorldSnapshot snap, ActiveResolution pass)
        {
            var state = pass.State;
            var active = pass.Active;
            var dead = pass.Dead;
            // T07 — a live Attack gather re-planned above may just have recruited the actor of a
            // completed Raid's Return fallback. Hand it over in this same pass (the rule the
            // pre-loop "given to gather" retirement applies next pass): the fallback leg is
            // retired, so the army never has two owners and never walks home instead.
            var recruitedNow = new HashSet<int>(state.All
                .Where(i => i?.Kind == MissionKind.Attack && i.Status == IntentStatus.Active
                    && i.Attack?.Phase == AttackMissionPhase.Gather)
                .SelectMany(i => i.Attack.GatherSupportArmyIds));
            foreach (MissionIntent fallback in state.All.Where(i => i?.Raid != null
                && i.Raid.CompletedTargetAwaitingFreshDecision && i.Raid.PrimaryArmyId.HasValue
                && recruitedNow.Contains(i.Raid.PrimaryArmyId.Value)
                && !dead.Contains(i.IntentKey)).ToList())
            {
                dead.Add(fallback.IntentKey);
                active.Remove(fallback);
                AiDebugLog.Write($"[AI][V2][Attack][Gather] continuity — {fallback.IntentKey} return "
                    + $"fallback retired: its army #{fallback.Raid.PrimaryArmyId} joins a live Attack "
                    + "gather after completing its Raid target");
            }
        }

        private static void CaptureAttackProvisionFacts(ProvisionedMission pm, MissionStepResult o)
        {
            o.AttackFactsForWrite().HasAttackPayload = true;
            o.AttackFactsForWrite().AttackTarget = pm.AttackTarget;
        }

        private static void CaptureAttackExecutionFacts(ExecutionResult e, MissionStepResult o)
        {
            o.GroundFactsForWrite().OperationStarted = e.OperationStarted || e.StepsMoved > 0
                || e.StopReason == ExecutionStopReason.BattleStarted;
            o.AttackFactsForWrite().AttackOpportunisticStrike = e.AttackOpportunisticStrike;
            o.AttackFactsForWrite().AttackIntermediateCaptured = e.AttackIntermediateCaptured;
            o.AttackFactsForWrite().AttackCaptureHadBattle = e.AttackCaptureHadBattle;

            o.PayloadForWrite<AttackStepPayload>().AirSupportStrikeSucceeded = e.AirSupportStrikeSucceeded;
        }

        // The frozen target roster of the live Attack preparation this army hosts (null if none).
        private static List<StrikeRosterSlot> PreparationTargetRoster(PlayerSetupData player, ArmyData army)
        {
            if (player == null || army == null)
                return null;
            MissionIntent prep = MissionIntentRegistry.GetOrCreate(player).All.FirstOrDefault(i =>
                i != null && i.Status == IntentStatus.Active && i.Kind == MissionKind.Attack
                && i.Attack != null && i.Attack.Preparation && i.Attack.Phase == AttackMissionPhase.Gather
                && i.Attack.PrimaryArmyId == army.Id);
            return prep?.Attack?.TargetRoster;
        }

        // Card keys of the held ground Unit cards that deploy on `hex` (a held card lands in the
        // host by Phase A only through a building there that deploys it).
        // A card Phase A already failed to chain into this host frees no slot either.
        private static List<string> HeldFieldCardKeys(PlayerSetupData player, HexCoord hex,
            int hostArmyId, int turn) =>
            (AiHandRegistry.Peek(player)?.Hand ?? Enumerable.Empty<CardData>())
                .Select(c => c?.Definition)
                .Where(d => d != null && d.cardType == CardType.Unit && !d.isAviation
                    && ArmyActions.HasRequiredGroundDeploymentBuilding(player, hex, d)
                    && !PreparationDeliveryMemory.NoChainRecently(player, hostArmyId, turn,
                        StrikeRoster.CardKey(d)))
                .Select(StrikeRoster.CardKey).ToList();

        // Live twin of the planner's body release (HousekeepingExecutor preflight): is this a
        // non-commander BODY of a preparation host that its target roster does not contain?
        private static bool IsPreparationNonRosterBody(PlayerSetupData player, ArmyData host, UnitData unit)
        {
            if (unit == null || unit.IsHero || host == null || !host.Members.Contains(unit))
                return false;
            List<StrikeRosterSlot> target = PreparationTargetRoster(player, host);
            return target != null && target.Count > 0
                && StrikeRoster.NonTargetBodies(target, host.Members).Contains(unit);
        }

        internal static ArmyMutationContract AttackPreparationMutationContract(int turn) =>
            ArmyMutationContract.PreparationHost(
                host => {
                    var target = PreparationTargetRoster(host?.Owner, host);
                    return target != null && target.Count > 0 ? target.Select(slot => slot.Key).ToArray() : null;
                },
                host => host == null ? null : HeldFieldCardKeys(host.Owner, host.Hex, host.Id, turn),
                (host, unit) => IsPreparationNonRosterBody(host?.Owner, host, unit));

    }
}


