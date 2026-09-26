using System;
using System.Collections.Generic;
using System.Linq;
using Game.Aviation;
using Game.Combat;
using Game.HexGrid;
using Game.Players;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  RAID CONTINUITY — a mechanical partial of MissionContinuityLayer, the Raid counterpart of
    //  MissionContinuityLayer.Attack.cs: the Raid lane's own lifecycle answers (phase machine,
    //  recovery, support legs, walk-home completion). Intents still live in the one
    //  MissionIntentState and are reaped by the same rules.
    // ===========================================================================================
    internal static partial class MissionContinuityLayer
    {
        // The Raid lane's own lifecycle answers for ResolveActive (the counterpart of
        // ResolveAttackIntent). Returns false when the intent must be retired; true keeps it, and
        // ResolveActive adds it to this pass's active set while it is Active.
        // `raidClaims` — every durable claim (recovery may not recruit another operation's actor).
        private static bool ResolveRaidIntent(PlayerSetupData player, WorldSnapshot snap,
            MissionIntent intent, ISet<int> raidClaims,
            Func<HexCoord, HexCoord, int, int> safeRouteCost)
        {
            RaidIntent ri = intent.Raid;
            if (ri == null)
            {
                AiDebugLog.Write($"[AI][V2] continuity — {intent.IntentKey} retired at turn start (no raid objective)");
                return false;
            }

            if (ri.Phase == RaidMissionPhase.AirSupport)
            {
                if (!GroundCombatAirSupport.SortieLive(player, ri.AirSupportArmyId, out _))
                {
                    int? released = ri.AirSupportArmyId;
                    ri.AirSupportAttemptedTurn = snap.TurnNumber;
                    ri.AirSupportArmyId = null;
                    ri.AirSupportLandingHex = null;
                    if (PrimaryClearsTarget(snap, player, ri.PrimaryArmyId, ri.Target))
                    {
                        ri.Phase = RaidMissionPhase.Assault;
                        ClearRaidRecovery(ri);
                    }
                    else
                    {
                        HashSet<int> unavailable = RecoveryUnavailable(raidClaims, ri);
                        if (!TransitionToBestRecovery(player, snap, intent, ri, unavailable,
                                "air support ended below threshold", safeRouteCost))
                        {
                            return false;
                        }
                    }
                    AiDebugLog.Write($"[AI][V2][Raid][AirSupport] wing "
                        + $"#{(released.HasValue ? released.Value.ToString() : "none")} "
                        + $"released; next phase={ri.Phase}");
                }
            }

            // §5 actor ownership — a LOST SUPPORT actor releases only the support claim;
            // the primary Raid survives untouched. Only revert to Assault if the primary can
            // actually clear the target alone (same PrimaryClearsTarget gate AdvanceRaidPhase
            // uses below) — otherwise this reset would set Phase=Assault BEFORE
            // AdvanceRaidPhase runs later in this same pass, and its own analogous re-check
            // (`ri.Phase == RaidMissionPhase.Reinforcement && ...`) would then silently no-op
            // because Phase is already Assault, letting a still-too-weak primary be proposed
            // for an ordinary Assault this step. A support in SupportReturn is walking home
            // after a successful swap, not carrying reinforcement — its loss there is handled
            // separately, below, without reverting the phase.
            if (ri.SupportArmyId.HasValue && ri.Phase == RaidMissionPhase.Reinforcement
                && !ActorCommitments.GroundContainerStillValid(ri.SupportArmyId.Value, snap))
            {
                int lostSupportId = ri.SupportArmyId.Value;
                ri.ReinforcementRequestedTurn = -1;
                bool nowClears = ReleaseRaidSupport(snap, player, ri);
                AiDebugLog.Write($"[AI][V2][Raid] {intent.IntentKey} support #{lostSupportId} lost; "
                    + $"released support claim, primary raid kept, phase={ri.Phase} "
                    + $"(primaryNowClears={(nowClears ? 1 : 0)})");
            }
            // §SupportReturn — a lost returning support must close the return leg as well as
            // release the actor claim. Reuse the same lifecycle transition as physical
            // arrival so Phase cannot remain SupportReturn with no support actor.
            else if (ri.SupportArmyId.HasValue && ri.PrimaryArmyId.HasValue
                && ri.Phase == RaidMissionPhase.SupportReturn
                && !ActorCommitments.GroundContainerStillValid(ri.SupportArmyId.Value, snap))
            {
                int lostSupportId = ri.SupportArmyId.Value;
                CompleteRaidSupportReturn(player, snap, ri.PrimaryArmyId.Value,
                    $"support #{lostSupportId} lost en route home");
                intent.LastProgressTurn = snap?.TurnNumber ?? intent.LastProgressTurn;
                intent.StallTurns = 0;
            }

            // A started Raid needs a surviving, non-empty primary container in every
            // phase. Structural combat eligibility is checked AFTER AdvanceRaidPhase:
            // a completed objective may need to send a battle-depleted survivor home.
            // null means unbound; ArmyId 0 is a valid bound army.
            bool primaryContainerAlive = ri.PrimaryArmyId.HasValue
                && ActorCommitments.GroundContainerStillValid(ri.PrimaryArmyId.Value, snap);
            if (ri.OperationStarted && !primaryContainerAlive)
            {
                AiDebugLog.Write($"[AI][V2][Raid] {intent.IntentKey} retired — primary "
                    + $"#{(ri.PrimaryArmyId.HasValue ? ri.PrimaryArmyId.Value.ToString() : "none")} "
                    + $"missing or dead in phase {ri.Phase} "
                    + $"(support #{(ri.SupportArmyId.HasValue ? ri.SupportArmyId.Value.ToString() : "none")} released)");
                return false;
            }

            // A SupportReturn already satisfied in the fresh snapshot is a continuity fact,
            // not a provisioning failure. Resolve it here through the same canonical
            // transition Execution uses on physical arrival. Otherwise ProvisionReturn's
            // generic TargetSatisfied bypasses Raid phase payload and ReconcileOutcome can
            // retire the whole durable campaign instead of only releasing the support.
            if (ri.Phase == RaidMissionPhase.SupportReturn
                && ri.PrimaryArmyId.HasValue && ri.SupportArmyId.HasValue
                && ri.SupportReturnHex.HasValue)
            {
                ArmySnapshot returningSupport = snap?.Self?.Armies?.FirstOrDefault(a => a != null
                    && a.ArmyId == ri.SupportArmyId.Value);
                if (returningSupport != null
                    && returningSupport.Hex.Equals(ri.SupportReturnHex.Value))
                {
                    int returnedSupportId = returningSupport.ArmyId;
                    CompleteRaidSupportReturn(player, snap, ri.PrimaryArmyId.Value,
                        $"support #{returnedSupportId} already home during reconciliation");
                    intent.LastProgressTurn = snap?.TurnNumber ?? intent.LastProgressTurn;
                    intent.StallTurns = 0;
                }
            }

            // RecoveryReturn arrival is a waypoint, never completion of the Raid. The
            // primary stays claimed and the next fresh snapshot selects one exact Refit
            // action. This also handles arrival between bounded execution cycles.
            if (ri.Phase == RaidMissionPhase.RecoveryReturn
                && ri.PrimaryArmyId.HasValue && ri.RecoveryBaseHex.HasValue)
            {
                ArmySnapshot returningPrimary = snap?.Self?.Armies?.FirstOrDefault(a => a != null
                    && a.ArmyId == ri.PrimaryArmyId.Value);
                if (returningPrimary != null
                    && returningPrimary.Hex.Equals(ri.RecoveryBaseHex.Value))
                {
                    CompleteRaidRecoveryReturn(player, snap, ri.PrimaryArmyId.Value,
                        "primary already at recovery base during reconciliation");
                    intent.LastProgressTurn = snap?.TurnNumber ?? intent.LastProgressTurn;
                    intent.StallTurns = 0;
                }
            }

            // §5/§SupportReturn phase machine. Return and SupportReturn never consult target
            // validity (their objective is a base, not the Raid target); every other phase
            // keeps the existing fog!=death rule.
            bool isReturnLeg = ri.Phase == RaidMissionPhase.Return || ri.Phase == RaidMissionPhase.SupportReturn;
            HashSet<int> recoveryUnavailable = RecoveryUnavailable(raidClaims, ri);
            if (!isReturnLeg
                && !AdvanceRaidPhase(player, snap, intent, ri,
                    recoveryUnavailable, safeRouteCost))
            {
                return false;
            }

            // AdvanceRaidPhase may have changed Assault/Reinforcement to Return. Never
            // validate the finished target under the stale pre-transition phase.
            isReturnLeg = ri.Phase == RaidMissionPhase.Return || ri.Phase == RaidMissionPhase.SupportReturn;
            // SupportReturn still requires a combat-capable PRIMARY: only the primary's
            // own Return leg relaxes that gate. Unfinished combat raids remain strict.
            bool recoveryGroundGate = ri.Phase == RaidMissionPhase.RecoveryReturn;
            if (ri.OperationStarted && ri.Phase != RaidMissionPhase.Return && !recoveryGroundGate
                && (!ri.PrimaryArmyId.HasValue
                    || !GroundCombatPrimaryAlive(snap, ri.PrimaryArmyId.Value)))
            {
                AiDebugLog.Write($"[AI][V2][Raid] {intent.IntentKey} retired — "
                    + $"primary #{ri.PrimaryArmyId} no longer structural in phase {ri.Phase}");
                return false;
            }

            if (!isReturnLeg
                && !RaidObjectiveEvaluator.IsIntentStillValid(snap, ri))
            {
                AiDebugLog.Write($"[AI][V2] continuity — {intent.IntentKey} retired at turn start (raid target no longer valid)");
                return false;
            }
            if (ri.Phase == RaidMissionPhase.Return)
            {
                // §11 — losing the chosen base is a controlled RETARGET, never a stall.
                HexCoord? home = KeepOrReselectHome(snap, player, ri.PrimaryArmyId,
                    ri.ReturnHex, out bool retargeted);
                if (home == null)
                {
                    AiDebugLog.Write($"[AI][V2][Raid] {intent.IntentKey} retired — return base lost "
                        + "and no replacement base exists");
                    return false;
                }
                if (retargeted)
                {
                    AiDebugLog.Write($"[AI][V2][Raid] {intent.IntentKey} return base retargeted to "
                        + $"({home.Value.Q},{home.Value.R})");
                    ri.ReturnHex = home;
                    intent.StallTurns = 0;
                }
            }
            if (ri.Phase == RaidMissionPhase.RecoveryReturn
                && !RecoveryBaseStillValid(snap, player, ri.PrimaryArmyId, ri.RecoveryBaseHex))
            {
                RaidRecoveryProjection replacement = RaidRecoveryPlanner.Choose(
                    snap, ri, recoveryUnavailable, safeRouteCost: safeRouteCost);
                if (!replacement.Viable)
                {
                    AiDebugLog.Write($"[AI][V2][RaidRecovery] decision=ABANDON intent={intent.IntentKey} "
                        + "reason=recovery_base_lost_and_no_viable_fallback");
                    return false;
                }
                if (replacement.Phase == RaidMissionPhase.AirSupport)
                {
                    ri.Phase = RaidMissionPhase.AirSupport;
                    ri.AirSupportArmyId = replacement.AirSupportArmyId;
                    ri.AirSupportLandingHex = replacement.AirSupportLandingHex;
                    ri.SupportArmyId = null;
                    ClearRaidRecovery(ri);
                }
                else if (replacement.Phase == RaidMissionPhase.Reinforcement)
                {
                    ri.Phase = RaidMissionPhase.Reinforcement;
                    ri.SupportArmyId = replacement.SupportArmyId;
                    ri.AirSupportArmyId = null;
                    ri.AirSupportLandingHex = null;
                    ClearRaidRecovery(ri);
                }
                else
                {
                    ri.AirSupportArmyId = null;
                    ri.AirSupportLandingHex = null;
                    ri.RecoveryBaseHex = replacement.BaseHex;
                    ri.Phase = replacement.Phase;
                    intent.StallTurns = 0;
                    AiDebugLog.Write($"[AI][V2][RaidRecovery] decision=RETURN_HOME "
                        + $"intent={intent.IntentKey} base=({replacement.BaseHex.Value.Q},{replacement.BaseHex.Value.R}) "
                        + "reason=recovery_base_retargeted");
                }
            }
            // §SupportReturn — same controlled-retarget rule for the support's own home.
            // Losing the support (already handled above) always releases the claim before
            // this point can even run against a stale actor.
            if (ri.Phase == RaidMissionPhase.SupportReturn && ri.SupportArmyId.HasValue)
            {
                HexCoord? supportHome = KeepOrReselectHome(snap, player, ri.SupportArmyId,
                    ri.SupportReturnHex, out bool supportRetargeted);
                if (supportHome == null)
                {
                    // §SupportReturn — no own base to send it to must never wedge the Raid:
                    // release the support and let the primary carry on being re-evaluated.
                    AiDebugLog.Write($"[AI][V2][Raid] {intent.IntentKey} support #{ri.SupportArmyId.Value} "
                        + "has no reachable home base — released, primary continues "
                        + "reason=no_replacement_base_for_support_return");
                    ri.SupportReturnHex = null;
                    ReleaseRaidSupport(snap, player, ri);
                }
                else if (supportRetargeted)
                {
                    AiDebugLog.Write($"[AI][V2][Raid] {intent.IntentKey} support return base "
                        + $"retargeted to ({supportHome.Value.Q},{supportHome.Value.R})");
                    ri.SupportReturnHex = supportHome;
                    intent.StallTurns = 0;
                }
            }
            if (intent.Status == IntentStatus.Suspended
                && (intent.Suspended == SuspendReason.PoolExhausted
                    || intent.Suspended == SuspendReason.CapabilityUnavailable))
            {
                intent.Status = IntentStatus.Active;
                intent.Suspended = SuspendReason.None;
            }
            return true;
        }

        // A completed target is a strategic decision boundary: this intent becomes a Return
        // fallback with an unclaimed actor. Any next Raid / Attack / ActiveDefence is proposed as
        // a fresh mission and competes through TaskScore + the global allocator.
        // Returns false only when the operation cannot continue in any phase (caller retires it).
        private static bool AdvanceRaidPhase(PlayerSetupData player, WorldSnapshot snap,
            MissionIntent intent, RaidIntent ri, ISet<int> unavailableArmyIds,
            Func<HexCoord, HexCoord, int, int> safeRouteCost)
        {
            // Loss of VISIBILITY is never proof of destruction — IsObjectiveSatisfiedLive is the
            // positive live read (ours / another player's roster / honest map memory / event-guard
            // Consumed state, per target kind).
            bool targetGone = ri.Target.HasValue
                && (RaidObjectiveEvaluator.IsObjectiveSatisfiedLive(player, ri.Target)
                    || RaidObjectiveEvaluator.IsKnownTargetNoLongerNeutral(snap, ri.Target));
            if (!targetGone)
            {
                if (ri.Phase == RaidMissionPhase.RecoveryReturn)
                {
                    if (PrimaryClearsTarget(snap, player, ri.PrimaryArmyId, ri.Target))
                    {
                        ri.Phase = RaidMissionPhase.Assault;
                        ClearRaidRecovery(ri);
                        AiDebugLog.Write($"[AI][V2][RaidRecovery] decision=RECOVERY_COMPLETE "
                            + $"intent={intent.IntentKey} reason=primary_recovered_during_return");
                    }
                    return true;
                }
                // A Reinforcement whose primary has since become strong enough again returns to
                // Assault on its own; Provisioning re-checks this after every roster transfer too.
                if (ri.Phase == RaidMissionPhase.Reinforcement && !ri.SupportArmyId.HasValue
                    && PrimaryClearsTarget(snap, player, ri.PrimaryArmyId, ri.Target))
                {
                    ri.Phase = RaidMissionPhase.Assault;
                    AiDebugLog.Write($"[AI][V2][Raid] {intent.IntentKey} phase Reinforcement -> Assault "
                        + "reason=primary_clears_current_target_again");
                }
                // The symmetric direction. AggressionDemandEvaluator.Build is a pure snapshot read;
                // Continuity is the sole owner of durable Phase, so a bound primary that no longer clears its CURRENT
                // (possibly already re-oriented) target is moved to Reinforcement here, before
                // Demand/Missions run this same pass.
                else if (ri.Phase == RaidMissionPhase.Assault && ri.PrimaryArmyId.HasValue
                    && !PrimaryClearsTarget(snap, player, ri.PrimaryArmyId, ri.Target,
                        GroundCombatAdmissionPolicy.RaidPrimaryGate(ri)))
                {
                    if (ri.OperationStarted)
                        return TransitionToBestRecovery(player, snap, intent, ri,
                            unavailableArmyIds, "primary_no_longer_clears_current_target",
                            safeRouteCost);
                    ri.Phase = RaidMissionPhase.Reinforcement;
                    AiDebugLog.Write($"[AI][V2][Raid] {intent.IntentKey} phase Assault -> Reinforcement "
                        + "reason=primary_no_longer_clears_current_target_before_operation_start");
                }
                return true;
            }

            ri.CompletedTargetAwaitingFreshDecision = true;
            intent.Funding = CommitmentTier.None;
            return BeginTerminalRaidReturn(player, snap, intent, ri,
                "target completed; awaiting fresh global Aggression decision");
        }

        private static HashSet<int> RecoveryUnavailable(ISet<int> allClaims, RaidIntent raid)
        {
            var unavailable = allClaims == null ? new HashSet<int>() : new HashSet<int>(allClaims);
            if (raid != null && raid.PrimaryArmyId.HasValue)
                unavailable.Remove(raid.PrimaryArmyId.Value);
            if (raid != null && raid.SupportArmyId.HasValue)
                unavailable.Remove(raid.SupportArmyId.Value);
            return unavailable;
        }

        private static bool TransitionToBestRecovery(PlayerSetupData player, WorldSnapshot snap,
            MissionIntent intent, RaidIntent raid, ISet<int> unavailableArmyIds, string reason,
            Func<HexCoord, HexCoord, int, int> safeRouteCost)
        {
            RaidRecoveryProjection plan = RaidRecoveryPlanner.Choose(snap, raid,
                unavailableArmyIds, safeRouteCost: safeRouteCost);
            if (!plan.Viable)
            {
                AiDebugLog.Write($"[AI][V2][RaidRecovery] decision=ABANDON intent={intent.IntentKey} "
                    + $"primary={raid.PrimaryArmyId} target={raid.Target.DiagnosticLabel} "
                    + $"currentWin={plan.CurrentWinChance:0.00} reason={reason}; {plan.Reason}");
                return BeginTerminalRaidReturn(player, snap, intent, raid,
                    $"recovery cannot prove a path to threshold: {plan.Reason}");
            }

            raid.SupportArmyId = plan.Phase == RaidMissionPhase.Reinforcement
                ? plan.SupportArmyId : null;
            raid.AirSupportArmyId = plan.Phase == RaidMissionPhase.AirSupport
                ? plan.AirSupportArmyId : null;
            raid.AirSupportLandingHex = plan.Phase == RaidMissionPhase.AirSupport
                ? plan.AirSupportLandingHex : null;
            raid.ReinforcementRequestedTurn = -1;
            raid.Phase = plan.Phase;
            if (plan.Phase == RaidMissionPhase.AirSupport)
            {
                ClearRaidRecovery(raid);
                AiDebugLog.Write($"[AI][V2][RaidRecovery] decision=AIR_SUPPORT "
                    + $"intent={intent.IntentKey} primary={raid.PrimaryArmyId} target={raid.Target.DiagnosticLabel} "
                    + $"currentWin={plan.CurrentWinChance:0.00} projectedWin={plan.ProjectedWinChance:0.00} "
                    + $"eta={plan.EtaTurns} wing={plan.AirSupportArmyId} score={plan.Score.Value:0.00} "
                    + $"reason={reason}; {plan.Reason}");
            }
            else if (plan.Phase == RaidMissionPhase.Reinforcement)
            {
                ClearRaidRecovery(raid);
                AiDebugLog.Write($"[AI][V2][RaidRecovery] decision=FIELD_REINFORCEMENT "
                    + $"intent={intent.IntentKey} primary={raid.PrimaryArmyId} target={raid.Target.DiagnosticLabel} "
                    + $"currentWin={plan.CurrentWinChance:0.00} projectedWin={plan.ProjectedWinChance:0.00} "
                    + $"reinforcementEta={plan.EtaTurns} support={plan.SupportArmyId} ap={plan.ApCost:0.##} "
                    + $"reason={reason}; {plan.Reason}");
            }
            else
            {
                raid.RecoveryBaseHex = plan.BaseHex;
                AiDebugLog.Write($"[AI][V2][RaidRecovery] decision=RETURN_HOME "
                    + $"intent={intent.IntentKey} primary={raid.PrimaryArmyId} target={raid.Target.DiagnosticLabel} "
                    + $"currentWin={plan.CurrentWinChance:0.00} projectedWin={plan.ProjectedWinChance:0.00} "
                    + $"recoveryEta={plan.EtaTurns} base=({plan.BaseHex.Value.Q},{plan.BaseHex.Value.R}) "
                    + $"ap={plan.ApCost:0.##} resources=[{plan.ResourceCost.FmtPhysical()}] "
                    + $"reason={reason}; {plan.Reason}");
            }
            intent.StallTurns = 0;
            intent.LastProgressTurn = snap.TurnNumber;
            return true;
        }

        private static bool BeginTerminalRaidReturn(PlayerSetupData player, WorldSnapshot snap,
            MissionIntent intent, RaidIntent raid, string reason)
        {
            ArmySnapshot primary = raid.PrimaryArmyId.HasValue
                ? snap?.Self?.Armies?.FirstOrDefault(a => a != null
                    && a.ArmyId == raid.PrimaryArmyId.Value) : null;
            HexCoord? home = raid.RecoveryBaseHex.HasValue && primary != null
                    && primary.Hex.Equals(raid.RecoveryBaseHex.Value)
                ? raid.RecoveryBaseHex
                : SelectReturnBase(snap, player, raid.PrimaryArmyId);
            if (!home.HasValue)
            {
                AiDebugLog.Write($"[AI][V2][RaidRecovery] decision=ABANDON intent={intent.IntentKey} "
                    + $"reason={reason}; no_return_base");
                return false;
            }
            raid.Phase = RaidMissionPhase.Return;
            raid.ReturnHex = home;
            raid.SupportArmyId = null;
            raid.ReinforcementRequestedTurn = -1;
            ClearRaidRecovery(raid);
            intent.StallTurns = 0;
            intent.LastProgressTurn = snap?.TurnNumber ?? intent.LastProgressTurn;
            AiDebugLog.Write($"[AI][V2][RaidRecovery] decision=ABANDON intent={intent.IntentKey} "
                + $"reason={reason} return=({home.Value.Q},{home.Value.R}) primary={raid.PrimaryArmyId}");
            return true;
        }

        private static void ClearRaidRecovery(RaidIntent raid)
        {
            if (raid == null) return;
            raid.RecoveryBaseHex = null;
        }

        private static float CurrentRaidWinChance(WorldSnapshot snap, RaidIntent raid)
        {
            ArmySnapshot primary = raid != null && raid.PrimaryArmyId.HasValue
                ? snap?.Self?.Armies?.FirstOrDefault(a => a != null
                    && a.ArmyId == raid.PrimaryArmyId.Value) : null;
            if (primary == null) return 0f;
            var roster = (primary.RecoveryMembers ?? System.Array.Empty<RaidRecoveryMemberSnapshot>())
                .Where(m => m.IsGroundBattleBody).Select(m => m.CurrentProfile).ToList();
            GroundCombatFeasibility.Clears(roster, primary.Commander,
                AiV2Util.KnownOpposition(snap, raid.Target),
                AiConfigV2.raidMinViableWinChance, AiV2Util.KnownRaidDefenceBonus(snap, raid.Target),
                out float win, out _);
            return win;
        }

        private static string RefitDecision(RaidRefitActionKind kind) =>
            kind == RaidRefitActionKind.RepairUnit ? "REPAIR"
            : kind == RaidRefitActionKind.TransferUnit ? "TRANSFER" : "SWAP";

        // Execution has finished the atomic rendezvous handoff. Continuity (the
        // sole owner of intent state) releases the support claim and returns the operation to
        // Assault ONLY when the post-transfer roster actually re-cleared the shared estimator.
        internal static void CompleteRaidReinforcement(PlayerSetupData player, int primaryArmyId,
            bool rosterVerified, string detail)
        {
            if (player == null)
                return;
            MissionIntentState state = MissionIntentRegistry.GetOrCreate(player);
            MissionIntent intent = state.All.FirstOrDefault(i => i?.Raid != null
                && i.Raid.PrimaryArmyId == primaryArmyId);
            if (intent == null)
                return;
            RaidIntent ri = intent.Raid;
            ri.SupportArmyId = null;
            ri.ReinforcementRequestedTurn = -1;
            ri.Phase = rosterVerified ? RaidMissionPhase.Assault : RaidMissionPhase.Reinforcement;
            AiDebugLog.Write($"[AI][V2][Raid] {intent.IntentKey} reinforcement complete — "
                + $"phase={ri.Phase} verified={(rosterVerified ? 1 : 0)} {detail}");
        }

        // A full/full swap displaced a primary member into support;
        // Execution has finished the swap and now hands the support army a Return leg home while
        // the primary stays put on the target. Called once, from the same execution step that ran
        // ArmyActions.SwapMembers.
        internal static void BeginRaidSupportReturn(PlayerSetupData player, WorldSnapshot snap,
            int primaryArmyId, int supportArmyId, string detail)
        {
            if (player == null)
                return;
            MissionIntentState state = MissionIntentRegistry.GetOrCreate(player);
            MissionIntent intent = state.All.FirstOrDefault(i => i?.Raid != null
                && i.Raid.PrimaryArmyId == primaryArmyId);
            if (intent == null)
                return;
            RaidIntent ri = intent.Raid;
            HexCoord? home = SelectReturnBase(snap, player, supportArmyId);
            if (home == null)
            {
                // No base to send it home to — never block the Raid on this. Release the support
                // right away and let the usual reinforcement-loss path re-evaluate the primary.
                ri.ReinforcementRequestedTurn = -1;
                ReleaseRaidSupport(snap, player, ri);
                AiDebugLog.Write($"[AI][V2][Raid] {intent.IntentKey} support #{supportArmyId} swapped "
                    + "out but no reachable home base exists — released immediately "
                    + $"phase={ri.Phase} {detail}");
                return;
            }
            ri.SupportArmyId = supportArmyId;
            ri.SupportReturnHex = home;
            ri.Phase = RaidMissionPhase.SupportReturn;
            AiDebugLog.Write($"[AI][V2][Raid] {intent.IntentKey} full/full swap complete — support "
                + $"#{supportArmyId} -> SupportReturn home ({home.Value.Q},{home.Value.R}); "
                + $"primary #{primaryArmyId} holds target {ri.Target.DiagnosticLabel} {detail}");
        }

        // The one "support leaves a Raid" edge: release the support claim and send the primary
        // back to Assault if it clears the current target on its own, else back to Reinforcement.
        private static bool ReleaseRaidSupport(WorldSnapshot snap, PlayerSetupData player, RaidIntent ri)
        {
            ri.SupportArmyId = null;
            bool nowClears = PrimaryClearsTarget(snap, player, ri.PrimaryArmyId, ri.Target);
            ri.Phase = nowClears ? RaidMissionPhase.Assault : RaidMissionPhase.Reinforcement;
            return nowClears;
        }

        // The return leg ended (arrival or actor loss). Release its claim,
        // clear the leg, and re-evaluate the primary against the current target exactly like any
        // other reinforcement-completion edge.
        internal static void CompleteRaidSupportReturn(PlayerSetupData player, WorldSnapshot snap,
            int primaryArmyId, string detail)
        {
            if (player == null)
                return;
            MissionIntentState state = MissionIntentRegistry.GetOrCreate(player);
            MissionIntent intent = state.All.FirstOrDefault(i => i?.Raid != null
                && i.Raid.PrimaryArmyId == primaryArmyId);
            if (intent == null)
                return;
            RaidIntent ri = intent.Raid;
            ri.SupportArmyId = null;
            ri.SupportReturnHex = null;
            bool stillHasTarget = ri.Target.HasValue
                && !RaidObjectiveEvaluator.IsObjectiveSatisfiedLive(player, ri.Target)
                && !RaidObjectiveEvaluator.IsKnownTargetNoLongerNeutral(snap, ri.Target);
            if (!stillHasTarget)
            {
                // The target finished while support was walking home — let the ordinary phase
                // machine pick the next target (or Return) on the next ResolveActive pass; parking
                // in Assault here is a safe default since AdvanceRaidPhase re-derives everything.
                ri.Phase = RaidMissionPhase.Assault;
                AiDebugLog.Write($"[AI][V2][Raid] {intent.IntentKey} support return ended; target already "
                    + $"resolved while away — will re-orient next pass {detail}");
                return;
            }
            bool nowClears = ReleaseRaidSupport(snap, player, ri);
            AiDebugLog.Write($"[AI][V2][Raid] {intent.IntentKey} support return ended; released — "
                + $"phase={ri.Phase} primaryClears={(nowClears ? 1 : 0)} {detail}");
        }

        // The primary reached its recovery base. This operation is OVER the moment it retreats —
        // never a commitment to refit and march back out at the same target: a healed unit belongs
        // to the standing force, not to a specific past operation. Repair is not this module's
        // concern —
        // StrategicMaintenancePolicy's own repair candidate (a parallel, independent Phase-B
        // maintenance action) picks up any wounded member of this now-ordinary army on its own,
        // whether or not it was ever part of a raid. A fresh RaidIntent decides independently,
        // later, whether/where to raid again.
        internal static void CompleteRaidRecoveryReturn(PlayerSetupData player, WorldSnapshot snap,
            int primaryArmyId, string detail)
        {
            if (player == null) return;
            MissionIntentState state = MissionIntentRegistry.GetOrCreate(player);
            MissionIntent intent = state.All.FirstOrDefault(i => i?.Raid?.PrimaryArmyId == primaryArmyId);
            if (intent?.Raid == null) return;
            RaidIntent raid = intent.Raid;
            if (raid.Phase != RaidMissionPhase.RecoveryReturn)
                return;
            state.Remove(intent.IntentKey);
            AiDebugLog.Write($"[AI][V2][RaidRecovery] decision=RETIRED intent={intent.IntentKey} "
                + $"primary={primaryArmyId} base={raid.RecoveryBaseHex} reason=primary_home {detail}");
        }

        // §5/§6 — the one shared "can the primary take THIS target right now" question. Fresh
        // target => fresh start gate.
        // `winChanceGate` defaults to the fresh start gate (the "clears again" exits keep it as
        // hysteresis); a started operation's own stay-in-Assault check passes the continuation
        // floor, so this re-check is never stricter than the admission it re-checks.
        internal static bool PrimaryClearsTarget(WorldSnapshot snap, PlayerSetupData player,
            int? primaryArmyId, RaidTargetRef target, float? winChanceGate = null)
        {
            if (snap == null || !primaryArmyId.HasValue || !target.HasValue)
                return false;
            GroundCombatAssemblyPlan plan = GroundCombatAssemblyPlanner.PlanForArmyAt(
                snap, AiV2Util.KnownOpposition(snap, target), primaryArmyId.Value,
                winChanceGate ?? GroundCombatAdmissionPolicy.FreshStartWinChanceGate,
                AiV2Util.KnownRaidDefenceBonus(snap, target));
            return plan.Feasible;
        }

        private static bool RecoveryBaseStillValid(WorldSnapshot snap, PlayerSetupData player,
            int? primaryArmyId, HexCoord? hex)
        {
            if (!ReturnBaseStillValid(snap, player, primaryArmyId, hex) || !hex.HasValue
                || !primaryArmyId.HasValue)
                return false;
            ArmySnapshot primary = snap?.Self?.Armies?.FirstOrDefault(a => a != null
                && a.ArmyId == primaryArmyId.Value);
            return primary != null && (primary.Hex.Equals(hex.Value)
                || (primary.ReachableOwnBaseHexes != null
                    && primary.ReachableOwnBaseHexes.Contains(hex.Value)));
        }

        private static void CreateRaidIntent(MissionIntentState state, MissionTurnOutcome o, int turn)
        {
            var ri = new RaidIntent
            {
                Target = o.RaidTarget,
                LastKnownHex = o.RaidLastKnownHex,
                TargetIsNeutral = o.RaidTargetIsNeutral,
                OperationStarted = true,
            };
            MissionIntent intent = NewIntent(o, turn, MissionKind.Raid, CommitmentTier.Hard, ri);
            RetireReturnFallbacksForActor(state, intent.PreferredMoverArmyId,
                "fresh Raid admitted");
            state.Put(intent);
            AiDebugLog.Write($"[AI][V2] continuity — [{AiV2Trace.FormatCorrelation(o.Proposal)}] {intent.IntentKey} created (Hard raid, mover #{o.MoverArmyId})");
        }
    }
}
