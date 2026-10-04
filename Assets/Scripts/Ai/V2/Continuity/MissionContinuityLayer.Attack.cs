using System;
using System.Collections.Generic;
using System.Linq;
using Game.Aviation;
using Game.Combat;
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

            // ---- the primary must still exist as a real ground force ---------------------------
            // RecoveryReturn only walks the survivors home, so it needs a live ground container,
            // not a structural combat actor (the gate ActorCommitments already claims it under,
            // and Raid's recoveryGroundGate): a battle-depleted remnant must still withdraw.
            if (a.Phase == AttackMissionPhase.RecoveryReturn)
            {
                if (!a.PrimaryArmyId.HasValue
                    || !ActorCommitments.GroundContainerStillValid(a.PrimaryArmyId.Value, snap))
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
                    || !ActorCommitments.PreparationHostStillValid(a.PrimaryArmyId.Value, snap))
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
                        && ActorCommitments.GroundContainerStillValid(a.PrimaryArmyId.Value, snap)
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

            ResolveGatherReturns(snap, player, intent, a);
            ResolveAttackAirSupport(snap, player, intent, a, unavailableArmyIds);

            bool supportLostThisPass = false;
            if (a.SupportArmyId.HasValue
                && (a.Phase == AttackMissionPhase.Reinforcement
                    || a.Phase == AttackMissionPhase.SupportReturn)
                && !ActorCommitments.GroundContainerStillValid(a.SupportArmyId.Value, snap))
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
                if (s == null || !ActorCommitments.GroundContainerStillValid(r.ArmyId, snap))
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
                return s == null || !ActorCommitments.GroundContainerStillValid(id, snap)
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
                return s == null || !ActorCommitments.GroundContainerStillValid(id, snap)
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
        internal static void CreateAttackIntent(MissionIntentState state, MissionTurnOutcome o, int turn)
        {
            AttackMissionTarget t = o.AttackTarget;
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
                LastOpportunisticStrikeTurn = o.AttackOpportunisticStrike
                    ? turn : t.OpportunisticStrikeTurn,
            };
            // Audit F7 — an operation born from its first Gather step carries the whole frozen
            // plan; the support that already attempted its handoff on that step is done.
            if (t.Phase == AttackMissionPhase.Gather && t.GatherSupportArmyIds != null)
                payload.GatherSupportArmyIds.AddRange(t.GatherSupportArmyIds.Where(id =>
                    id != payload.PrimaryArmyId
                    && !(o.ReinforcementHandoffAttempted && id == o.MoverArmyId)));
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
    }
}

