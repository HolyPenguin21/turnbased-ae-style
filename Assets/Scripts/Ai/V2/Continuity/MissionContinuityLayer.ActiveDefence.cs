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
    //    Return    — one army's withdrawal (regroup at the Citadel, or retreat home): kept until
    //                it arrives, whatever became of the threat that started it.
    // ===========================================================================================
    internal static partial class MissionContinuityLayer
    {
        // The ActiveDefence lane's own lifecycle answer for ResolveActive (the counterpart of
        // ResolveAttackIntent / ResolveRaidIntent). False retires the intent. A Return whose home
        // had to be re-picked is re-keyed through `rekeys` (its identity is mover + destination).
        private static bool ResolveActiveDefenceIntent(PlayerSetupData player, WorldSnapshot snap,
            MissionIntent intent, List<(MissionIntentKey Old, MissionIntent Intent)> rekeys)
        {
            ActiveDefenceIntent defence = intent.ActiveDefence;
            ArmySnapshot actor = snap?.Self?.Armies?.FirstOrDefault(a => a != null
                && defence?.PrimaryArmyId == a.ArmyId && a.IsStructuralRaidActor);
            if (defence == null || actor == null || ShouldReap(intent, snap?.TurnNumber ?? 0))
            {
                AiDebugLog.Write($"[AI][V2][ActiveDefence][Continuity] decision=END {intent.IntentKey} "
                    + $"reason={(defence == null ? "no_payload" : actor == null ? "actor_lost" : "reaped")}");
                return false;
            }

            if (defence.Phase == ActiveDefencePhase.Return)
            {
                // The same walk-home rule every lifecycle leg uses: a destination that was lost or
                // became unreachable is re-picked, never walked to.
                HexCoord? home = KeepOrReselectHome(snap, player, defence.PrimaryArmyId,
                    defence.ReturnHex, out bool reselected);
                if (!home.HasValue || actor.Hex.Equals(home.Value))
                {
                    AiDebugLog.Write($"[AI][V2][ActiveDefence][Continuity] decision=ARRIVED "
                        + $"actor={actor.ArmyId} claim released");
                    return false;
                }
                if (reselected)
                {
                    MissionIntentKey oldKey = intent.IntentKey;
                    defence.ReturnHex = home;
                    intent.IntentKey = MissionIntentKey.For(intent);
                    intent.StallTurns = 0;
                    if (!oldKey.Equals(intent.IntentKey))
                        rekeys.Add((oldKey, intent));
                }
                ResumeTransientSuspension(intent);
                return true;
            }

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
            ResumeTransientSuspension(intent);
            return true;
        }

        private static void CreateActiveDefenceIntent(MissionIntentState state,
            MissionTurnOutcome o, int turn)
        {
            ActiveDefenceMissionTarget t = o.ActiveDefenceTarget;
            var payload = new ActiveDefenceIntent
            {
                Phase = t.Phase,
                EnemyArmyId = t.EnemyArmyId,
                LastKnownHex = t.LastKnownHex, LastObservedTurn = t.LastObservedTurn,
                Confidence = t.Confidence, ProtectedAssetHex = t.ProtectedAssetHex,
                ProtectedAssetKind = t.ProtectedAssetKind,
                ProtectedAssetValue = t.ProtectedAssetValue,
                ThreatSeverity = t.ThreatSeverity,
                PrimaryArmyId = o.MoverArmyId ?? t.PrimaryArmyId,
                ReturnHex = t.ReturnHex, ProjectedWinChance = t.ProjectedWinChance,
                CoversAllDefenders = t.CoversAllDefenders, EstimatedEta = t.EstimatedEta,
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
    }
}
