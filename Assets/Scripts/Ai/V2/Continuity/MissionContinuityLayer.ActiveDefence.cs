using System.Collections.Generic;
using System.Linq;
using Game.HexGrid;
using Game.Players;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  ACTIVE DEFENCE CONTINUITY — a mechanical partial of MissionContinuityLayer, beside
    //  MissionContinuityLayer.Attack.cs / .Raid.cs: the ActiveDefence lane's own lifecycle answers.
    //  Two independent one-actor intents and nothing else:
    //    Intercept — kept while its honest objective and its capable actor both exist; a victory,
    //                a vanished threat or a lost / no longer capable actor ends it, and the next
    //                global replan decides afresh.
    //    Return    — a necessary reinforcement of this own asset, or a safe withdrawal:
    //                re-evaluated against the corresponding current need before every step.
    // ===========================================================================================
    internal static partial class MissionContinuityLayer
    {
        internal static bool IsActiveDefenceStepObjectiveSatisfiedLive(PlayerSetupData player, ProvisionedMission pm)
        {
            if (pm.ActiveDefenceTarget.Phase == ActiveDefencePhase.Return)
            {
                Game.Map.ArmyData actor = Game.Map.ArmyRegistry.AllForOwner(player)
                    .FirstOrDefault(a => a != null && a.Id == pm.MoverArmyId);
                return actor != null && pm.ActiveDefenceTarget.ReturnHex.HasValue
                    && actor.Hex.Equals(pm.ActiveDefenceTarget.ReturnHex.Value);
            }
            // Preserve the observation-owned fog-of-war seam; no global enemy sweep.
            return ActiveDefenceObjectiveEvaluator.IsObjectiveSatisfiedLive(player,
                pm.ActiveDefenceTarget.EnemyArmyId);
        }

        // The ActiveDefence lane's own lifecycle answer for ResolveActive (the counterpart of
        // ResolveAttackIntent / ResolveRaidIntent). False retires the intent. A Return whose home
        // had to be re-picked is re-keyed through `rekeys` (its identity is mover + destination).
        private static bool ResolveActiveDefenceIntent(PlayerSetupData player, WorldSnapshot snap,
            MissionIntent intent, List<(MissionIntentKey Old, MissionIntent Intent)> rekeys)
        {
            ActiveDefenceIntent defence = intent.ActiveDefence;
            if (defence?.Phase == ActiveDefencePhase.AirSupport)
                return ResolveActiveDefenceAirSupport(player, snap, intent, defence);
            ArmySnapshot actor = snap?.Self?.Armies?.FirstOrDefault(a => a != null
                && defence?.PrimaryArmyId == a.ArmyId
                && (defence.Phase == ActiveDefencePhase.Return
                    ? MissionActorPolicy.GroundContainerStillValid(a.ArmyId, snap)
                    : a.IsStructuralRaidActor));
            if (defence == null || actor == null || ShouldReap(intent, snap?.TurnNumber ?? 0))
            {
                AiDebugLog.Write($"[AI][V2][ActiveDefence][Continuity] decision=END {intent.IntentKey} "
                    + $"reason={(defence == null ? "no_payload" : actor == null ? "actor_lost" : "reaped")}");
                return false;
            }

            if (defence.Phase == ActiveDefencePhase.Return)
            {
                if (actor.Hex.Equals(defence.ReturnHex)) return false;
                IReadOnlyList<MissionIntent> intents = MissionIntentRegistry.GetOrCreate(player).All.ToList();
                HashSet<int> committed = ActorCommitments.FromIntents(intents, snap, null).ClaimedArmyIdSet;
                if (defence.ReturnPurpose == ActiveDefenceReturnPurpose.RegroupForAsset)
                {
                    if (!ActiveDefenceObjectiveEvaluator.IsDefendableAsset(snap,
                            defence.ProtectedAssetKind, defence.ProtectedAssetHex))
                    {
                        AiDebugLog.Write($"[AI][V2][ActiveDefence][Continuity] decision=END actor={actor.ArmyId} reason=protected_base_lost");
                        return false;
                    }
                    ActiveDefenceObjective regroupObjective = ActiveDefenceObjectiveEvaluator.ForTrackedEnemy(snap, defence.EnemyArmyId);
                    // A different threatened asset requires a NEW global decision, not a silent
                    // retarget of an old Hard leg.
                    ActiveDefenceResponse response = regroupObjective == null
                        || !regroupObjective.Target.ProtectedAssetHex.Equals(defence.ProtectedAssetHex) ? null
                        : ActiveDefenceObjectiveEvaluator.AssessResponse(snap, regroupObjective, committed,
                            ActiveDefenceObjectiveEvaluator.WithdrawingArmyIds(intents), null, intents);
                    if (response?.Kind != ActiveDefenceResponseKind.Regroup
                        || !response.ReinforcementArmyIds.Contains(actor.ArmyId))
                    {
                        AiDebugLog.Write($"[AI][V2][ActiveDefence][Continuity] decision=END actor={actor.ArmyId} "
                            + $"reason={(regroupObjective == null ? "threat_no_longer_listed" : "regroup_no_longer_needed")}");
                        return false;
                    }
                    if (!defence.ReturnHex.HasValue || !defence.ReturnHex.Value.Equals(defence.ProtectedAssetHex)) return false;
                    defence.EnemyEta = regroupObjective.Target.EnemyEta;
                    defence.LastKnownHex = regroupObjective.Target.LastKnownHex;
                    defence.LastObservedTurn = regroupObjective.Target.LastObservedTurn;
                    defence.Confidence = regroupObjective.Target.Confidence;
                    defence.ThreatSeverity = regroupObjective.Target.ThreatSeverity;
                }
                else
                {
                    if (!ActiveDefenceObjectiveEvaluator.NeedsSafeWithdrawal(snap, actor))
                    {
                        AiDebugLog.Write($"[AI][V2][ActiveDefence][Continuity] decision=END actor={actor.ArmyId} reason=safe_withdrawal_released");
                        return false;
                    }
                    committed.Remove(actor.ArmyId);
                    HexCoord? home = ActiveDefenceObjectiveEvaluator.SafeWithdrawalBase(snap, actor, committed, defence.ReturnHex);
                    if (!home.HasValue || actor.Hex.Equals(home.Value)) return false;
                    if (!home.Equals(defence.ReturnHex))
                    {
                        MissionIntentKey oldKey = intent.IntentKey;
                        defence.ReturnHex = home;
                        intent.IntentKey = MissionIntentKey.For(intent);
                        intent.StallTurns = 0;
                        if (!oldKey.Equals(intent.IntentKey)) rekeys.Add((oldKey, intent));
                    }
                }
                ResumeTransientSuspension(intent);
                return true;
            }

            if (defence.InterceptPurpose == ActiveDefenceInterceptPurpose.ImmediateOpportunity)
                return ResolveImmediateIntercept(snap, intent, defence, actor);

            ActiveDefenceObjective objective =
                ActiveDefenceObjectiveEvaluator.ForTrackedEnemy(snap, defence.EnemyArmyId);
            if (objective == null)
            {
                // No listed objective means no Intercept proposal at all. The intercept simply
                // ends; a threat that is listed again is a fresh objective.
                EnemyContactSnapshot contact = snap?.Threat?.Contacts?.FirstOrDefault(c =>
                    c?.Army != null && c.Army.ArmyId == defence.EnemyArmyId
                    && c.Position.HasValue);
                bool handedOffToAttack = contact != null
                    && ActiveDefenceObjectiveEvaluator.OnKnownForeignStructure(
                        snap, contact.Position.Value);
                AiDebugLog.Write($"[AI][V2][ActiveDefence][Continuity] decision=END "
                    + $"enemy={defence.EnemyArmyId} reason="
                    + (handedOffToAttack ? "enemy_on_known_foreign_structure"
                        : contact == null ? "honest_contact_lost" : "threat_no_longer_listed"));
                return false;
            }
            // The actor must still be able to win this fight on its own roster (the continuation
            // floor it was admitted under). A weakened defender is released, never reinforced:
            // the fresh replan re-assesses the threat (another responder, regroup or retreat).
            if (!GroundCombatAssemblyPlanner.PlanForArmyAtThreshold(snap,
                    ActiveDefenceObjectiveEvaluator.Opposition(snap, defence.EnemyArmyId),
                    actor.ArmyId, GroundCombatAdmissionPolicy.ContinuationWinChanceFloor).Feasible)
            {
                AiDebugLog.Write($"[AI][V2][ActiveDefence][Continuity] decision=END "
                    + $"enemy={defence.EnemyArmyId} actor={actor.ArmyId} reason=actor_no_longer_capable");
                return false;
            }

            ActiveDefenceMissionTarget current = objective.Target;
            defence.LastKnownHex = current.LastKnownHex;
            defence.LastObservedTurn = current.LastObservedTurn;
            defence.Confidence = current.Confidence;
            defence.ProtectedAssetHex = current.ProtectedAssetHex;
            defence.ProtectedAssetKind = current.ProtectedAssetKind;
            defence.ProtectedAssetValue = current.ProtectedAssetValue;
            defence.ThreatSeverity = current.ThreatSeverity;
            defence.EstimatedEta = current.EstimatedEta;
            defence.EnemyEta = current.EnemyEta;
            ResumeTransientSuspension(intent);
            return true;
        }

        // ImmediateOpportunity lives only inside the turn that admitted it: a later turn, a contact
        // that is no longer visible there, a route that closed, movement that no longer pays for the
        // fight, or an actor that cannot win it any more ends the intent. Ending releases the claims
        // and leaves the army where it stands (no Return is created); a fresh snapshot may admit a
        // new, independent opportunity.
        private static bool ResolveImmediateIntercept(WorldSnapshot snap, MissionIntent intent,
            ActiveDefenceIntent defence, ArmySnapshot actor)
        {
            ActiveDefenceObjective objective = ActiveDefenceObjectiveEvaluator.ForTrackedEnemy(snap,
                defence.EnemyArmyId, ActiveDefenceInterceptPurpose.ImmediateOpportunity);
            string end = defence.ImmediateTurn != (snap?.TurnNumber ?? 0) ? "immediate_turn_over"
                : objective == null ? "immediate_contact_lost"
                : !ActiveDefenceObjectiveEvaluator.CanReachNow(snap, actor, objective.Target.LastKnownHex,
                    out _) ? "immediate_unreachable"
                : !GroundCombatAssemblyPlanner.PlanForArmyAtThreshold(snap,
                    ActiveDefenceObjectiveEvaluator.Opposition(snap, defence.EnemyArmyId),
                    actor.ArmyId, GroundCombatAdmissionPolicy.ContinuationWinChanceFloor).Feasible
                    ? "actor_no_longer_capable"
                : null;
            if (end != null)
            {
                AiDebugLog.Write($"[AI][V2][ActiveDefence][Continuity] decision=END enemy={defence.EnemyArmyId} "
                    + $"actor={actor.ArmyId} purpose=ImmediateOpportunity reason={end}");
                return false;
            }
            defence.LastKnownHex = objective.Target.LastKnownHex;
            defence.LastObservedTurn = objective.Target.LastObservedTurn;
            defence.Confidence = objective.Target.Confidence;
            ResumeTransientSuspension(intent);
            return true;
        }

        // AirSupport — the wing striking this threat beside the ground answer. Kept while the
        // threat is still a listed objective (honest memory; the ground Intercept may have ended,
        // be regrouping, retreating or never have existed) and the wing still flies its strike
        // series or was bound this turn and has not launched yet. A lost contact, a finished threat
        // or a series that is over ends only this intent; an airborne wing then flies home
        // (GroundCombatAirSupport.ReleaseOrphanStrikes), never left holding forever.
        private static bool ResolveActiveDefenceAirSupport(PlayerSetupData player, WorldSnapshot snap,
            MissionIntent intent, ActiveDefenceIntent defence)
        {
            int turn = snap?.TurnNumber ?? 0;
            bool flying = GroundCombatAirSupport.SortieLive(player, defence.AirSupportArmyId,
                out bool wingValid);
            if (flying)
                defence.AirSupportSortieSeen = true;
            ActiveDefenceObjective objective =
                ActiveDefenceObjectiveEvaluator.ForTrackedEnemy(snap, defence.EnemyArmyId);
            string end = !defence.AirSupportArmyId.HasValue ? "no_wing"
                : !wingValid ? (GroundCombatAirSupport.WingLanded(player, defence.AirSupportArmyId)
                    ? "series_over" : "wing_lost")
                : objective == null ? "threat_no_longer_listed"
                : !flying && (defence.AirSupportSortieSeen || defence.AirSupportBoundTurn < turn)
                    ? (defence.AirSupportSortieSeen ? "series_over" : "never_took_off")
                : ShouldReap(intent, turn) ? "reaped"
                : null;
            if (end != null)
            {
                AiDebugLog.Write($"[AI][V2][ActiveDefence][AirSupport][Continuity] decision=END "
                    + $"{intent.IntentKey} wing={defence.AirSupportArmyId} reason={end}");
                return false;
            }
            ActiveDefenceMissionTarget current = objective.Target;
            defence.LastKnownHex = current.LastKnownHex;
            defence.LastObservedTurn = current.LastObservedTurn;
            defence.Confidence = current.Confidence;
            defence.ProtectedAssetHex = current.ProtectedAssetHex;
            defence.ProtectedAssetKind = current.ProtectedAssetKind;
            defence.ProtectedAssetValue = current.ProtectedAssetValue;
            defence.ThreatSeverity = current.ThreatSeverity;
            ResumeTransientSuspension(intent);
            return true;
        }

        private static void CreateActiveDefenceIntent(MissionIntentState state,
            MissionStepResult o, int turn)
        {
            ActiveDefenceMissionTarget t = o.DefenceFacts().ActiveDefenceTarget;
            var payload = new ActiveDefenceIntent
            {
                Phase = t.Phase,
                EnemyArmyId = t.EnemyArmyId,
                LastKnownHex = t.LastKnownHex, LastObservedTurn = t.LastObservedTurn,
                Confidence = t.Confidence, ProtectedAssetHex = t.ProtectedAssetHex,
                ProtectedAssetKind = t.ProtectedAssetKind,
                ProtectedAssetValue = t.ProtectedAssetValue,
                ThreatSeverity = t.ThreatSeverity,
                // The AirSupport mover is the wing: it is kept apart from the ground actor slot.
                PrimaryArmyId = t.Phase == ActiveDefencePhase.AirSupport ? null
                    : o.MoverArmyId ?? t.PrimaryArmyId,
                ReturnHex = t.ReturnHex, ReturnPurpose = t.ReturnPurpose, EnemyEta = t.EnemyEta,
                InterceptPurpose = t.InterceptPurpose, ImmediateTurn = t.ImmediateTurn,
                ProjectedWinChance = t.ProjectedWinChance,
                CoversAllDefenders = t.CoversAllDefenders, EstimatedEta = t.EstimatedEta,
                AirSupportArmyId = t.Phase == ActiveDefencePhase.AirSupport
                    ? o.MoverArmyId ?? t.AirSupportArmyId : null,
                AirSupportLandingHex = t.AirSupportLandingHex,
                AirSupportBoundTurn = turn,
                AirSupportSortieSeen = t.Phase == ActiveDefencePhase.AirSupport,
            };
            MissionIntent intent = NewIntent(o, turn, MissionKind.ActiveDefence,
                CommitmentTier.Hard, payload);
            RetireReturnFallbacksForActor(state, payload.PrimaryArmyId,
                "fresh ActiveDefence admitted");
            if (payload.Phase == ActiveDefencePhase.Return)
                // The army withdraws: an intercept it still held is over.
                foreach (MissionIntent held in state.All.Where(i => i?.ActiveDefence != null
                    && i.ActiveDefence.Phase == ActiveDefencePhase.Intercept
                    && i.ActiveDefence.PrimaryArmyId == payload.PrimaryArmyId).ToList())
                    state.Remove(held.IntentKey);
            state.Put(intent);
            AiDebugLog.Write($"[AI][V2][ActiveDefence][Continuity] decision=CREATE {intent.IntentKey} "
                + $"phase={payload.Phase} enemy={t.EnemyArmyId} actor={payload.PrimaryArmyId}");
        }
        private static bool TryCreateActiveDefenceStep(MissionIntentState state, AiAllocatorState allocState,
            MissionIntent intent, MissionStepResult o, int turn)
        {
            if (!(o.DefenceFacts().HasActiveDefencePayload && o.MadeProgress)) return false;
            CreateActiveDefenceIntent(state, o, turn);
            return true;
        }

        private static void ResolveDefenceOperation(Game.Players.PlayerSetupData player, WorldSnapshot snap, MissionIntent intent, ActiveResolution pass)
        {
            var state = pass.State;
            var active = pass.Active;
            var dead = pass.Dead;
            var rekeys = pass.Rekeys;

            if (!ResolveActiveDefenceIntent(player, snap, intent, rekeys))
                dead.Add(intent.IntentKey);
            else if (intent.Status == IntentStatus.Active)
                active.Add(intent);
            return;
        }

        private static void CaptureActiveDefenceProvisionFacts(ProvisionedMission pm, MissionStepResult o)
        {
            o.DefenceFactsForWrite().HasActiveDefencePayload = true;
            o.DefenceFactsForWrite().ActiveDefenceTarget = pm.ActiveDefenceTarget;
        }

    }
}


