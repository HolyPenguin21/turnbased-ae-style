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
    //  ACTIVE DEFENCE CONTINUITY — a mechanical partial of MissionContinuityLayer, beside
    //  MissionContinuityLayer.Attack.cs / .Raid.cs: the ActiveDefence lane's own lifecycle answers
    //  (Intercept -> Return, the borrowed offensive's suspend/resume, local base stabilisation).
    // ===========================================================================================
    internal static partial class MissionContinuityLayer
    {
        private enum DefenceResolution
        {
            Retire,         // the intent ends this pass
            Keep,           // kept and active this pass (a Return leg, a freshly begun Return)
            KeepIfActive,   // kept; active this pass only while its Status is Active
        }

        // The ActiveDefence lane's own lifecycle answers for ResolveActive (the counterpart of
        // ResolveAttackIntent / ResolveRaidIntent).
        private static DefenceResolution ResolveActiveDefenceIntent(PlayerSetupData player,
            WorldSnapshot snap, MissionIntentState state, MissionIntent intent)
        {
            ActiveDefenceIntent defence = intent.ActiveDefence;
            ArmySnapshot actor = snap?.Self?.Armies?.FirstOrDefault(a => a != null
                && defence?.PrimaryArmyId == a.ArmyId && a.IsStructuralRaidActor);
            ActiveDefenceObjective objective = defence == null ? null
                : ActiveDefenceObjectiveEvaluator.ForTrackedEnemy(snap, defence.EnemyArmyId);
            if (defence != null && defence.Phase == ActiveDefencePhase.Intercept
                && objective == null && ProtectedBaseWasLost(snap, defence))
            {
                // Ownership is authoritative on the fresh Self snapshot. Treat a lost Base like a
                // completed/invalidated protection objective so the actor returns or is released;
                // never keep pursuing on behalf of foreign infrastructure.
                defence.ObjectiveCompleted = true;
            }
            if (defence == null || actor == null || ShouldReap(intent, snap?.TurnNumber ?? 0))
            {
                TryResumePreemptedOffensive(state, defence, "defence_ended");
                return DefenceResolution.Retire;
            }
            if (defence.Phase == ActiveDefencePhase.Return)
            {
                // The same walk-home rule every lifecycle leg uses: a home base that was lost or
                // became unreachable is re-picked, never walked to.
                HexCoord? home = KeepOrReselectHome(snap, player, defence.PrimaryArmyId,
                    defence.ReturnHex, out _);
                if (!home.HasValue || actor.Hex.Equals(home.Value))
                    return DefenceResolution.Retire;
                defence.ReturnHex = home;
                return DefenceResolution.Keep;
            }
            if (objective == null)
            {
                if (defence.ObjectiveCompleted)
                {
                    if (RequiresLocalBaseStabilization(snap, defence, actor))
                    {
                        // Releasing the mission claim is the hand-off to the existing same-hex
                        // Housekeeping owner. It may package eligible members into the garrison;
                        // Continuity never mutates rosters itself.
                        AiDebugLog.Write($"[AI][V2][ActiveDefence][Continuity] decision=STABILIZE_RELEASE "
                            + $"actor={actor.ArmyId} base=({actor.Hex.Q},{actor.Hex.R})");
                        return DefenceResolution.Retire;
                    }
                    defence.ObjectiveCompleted = false;
                    return BeginDefenceReturn(snap, player, intent, defence, actor)
                        ? DefenceResolution.Keep : DefenceResolution.Retire;
                }
                // No listed objective means no Intercept proposal at all: the planner
                // (AppendActiveDefence) proposes only from ActiveDefenceObjectiveEvaluator.Enumerate.
                // Keeping the intent Active here only held its actor claimed — and a borrowed
                // offensive suspended — with nothing moving it, until it was reaped with a cooldown
                // on this enemy. The intercept ends now: the borrowed offensive resumes, otherwise
                // the actor returns home. A threat that is listed again is a fresh objective.
                EnemyContactSnapshot contact = snap?.Threat?.Contacts?.FirstOrDefault(c =>
                    c?.Army != null && c.Army.ArmyId == defence.EnemyArmyId
                    && c.Source == ContactSource.Honest && c.Position.HasValue);
                bool handedOffToAttack = contact != null
                    && ActiveDefenceObjectiveEvaluator.OnKnownForeignStructure(
                        snap, contact.Position.Value);
                AiDebugLog.Write($"[AI][V2][ActiveDefence][Continuity] decision=END "
                    + $"enemy={defence.EnemyArmyId} reason="
                    + (handedOffToAttack ? "enemy_on_known_foreign_structure"
                        : contact == null ? "honest_contact_lost" : "threat_no_longer_listed"));
                if (TryResumePreemptedOffensive(state, defence, "threat_ended"))
                    return DefenceResolution.Retire;
                return BeginDefenceReturn(snap, player, intent, defence, actor)
                    ? DefenceResolution.Keep : DefenceResolution.Retire;
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
            if (intent.Status == IntentStatus.Suspended
                && intent.Suspended != SuspendReason.ActiveDefencePreemption)
            {
                intent.Status = IntentStatus.Active;
                intent.Suspended = SuspendReason.None;
            }
            return DefenceResolution.KeepIfActive;
        }

        // The ONE "resume exactly the offensive this defence preempted" edge. An offensive
        // suspended for another reason (Siege, pool exhaustion) is not this defence's to revive.
        // Returns true when it resumed one.
        private static bool TryResumePreemptedOffensive(MissionIntentState state,
            ActiveDefenceIntent defence, string reason)
        {
            if (defence?.SuspendedOffensiveIntentKey.HasValue != true
                || !state.TryGet(defence.SuspendedOffensiveIntentKey.Value, out MissionIntent offensive)
                || !IsOffensiveGroundCombatIntent(offensive)
                || offensive.Suspended != SuspendReason.ActiveDefencePreemption)
                return false;
            offensive.Status = IntentStatus.Active;
            offensive.Suspended = SuspendReason.None;
            AiDebugLog.Write($"[AI][V2][ActiveDefence][Continuity] decision=RESUME "
                + $"offensive={offensive.IntentKey} reason={reason}");
            return true;
        }

        // The ONE ended-intercept walk home. Audit F6 — a zero-value fallback exactly like a
        // completed Raid's Return: no commitment protection, the actor competes in fresh
        // allocation (ActorCommitments does not claim it). False when there is no own base to walk
        // to or the actor already stands on it (the intent then simply ends).
        private static bool BeginDefenceReturn(WorldSnapshot snap, PlayerSetupData player,
            MissionIntent intent, ActiveDefenceIntent defence, ArmySnapshot actor)
        {
            HexCoord? home = SelectReturnBase(snap, player, defence.PrimaryArmyId);
            if (!home.HasValue || actor.Hex.Equals(home.Value))
                return false;
            defence.Phase = ActiveDefencePhase.Return;
            defence.ReturnHex = home;
            intent.Funding = CommitmentTier.None;
            intent.Status = IntentStatus.Active;
            intent.Suspended = SuspendReason.None;
            AiDebugLog.Write($"[AI][V2][ActiveDefence][Continuity] decision=RETURN "
                + $"actor={actor.ArmyId} home=({home.Value.Q},{home.Value.R})");
            return true;
        }

        // Snapshot-pure hand-off gate between ActiveDefence and the existing Housekeeping owner.
        // Only a defender already standing on the protected, still-owned secondary Base is held
        // locally; Continuity never drags a remote army there and never edits a garrison roster.
        internal static bool RequiresLocalBaseStabilization(WorldSnapshot snap,
            ActiveDefenceIntent defence, ArmySnapshot actor)
        {
            if (snap?.Self == null || defence == null || actor == null
                || defence.ProtectedAssetKind != AssetKind.Base
                || !actor.Hex.Equals(defence.ProtectedAssetHex)
                || snap.Self.BaseHexes == null
                || !snap.Self.BaseHexes.Contains(defence.ProtectedAssetHex))
                return false;
            int garrisonNonHeroes = snap.Self.Armies?
                .Where(a => a != null && a.IsGarrison
                    && a.Hex.Equals(defence.ProtectedAssetHex))
                .Sum(a => a.Members?.Count ?? 0) ?? 0;
            return garrisonNonHeroes < AiConfig.secureBaseMinNonHeroUnits
                && (actor.Members?.Count ?? 0) > 0;
        }

        internal static bool ProtectedBaseWasLost(WorldSnapshot snap, ActiveDefenceIntent defence) =>
            defence != null && defence.ProtectedAssetKind == AssetKind.Base
            && (snap?.Self?.BaseHexes == null
                || !snap.Self.BaseHexes.Contains(defence.ProtectedAssetHex));

        private static void CreateActiveDefenceIntent(MissionIntentState state,
            MissionTurnOutcome o, int turn)
        {
            ActiveDefenceMissionTarget t = o.ActiveDefenceTarget;
            var payload = new ActiveDefenceIntent
            {
                Phase = t.Phase, EnemyArmyId = t.EnemyArmyId,
                LastKnownHex = t.LastKnownHex, LastObservedTurn = t.LastObservedTurn,
                Confidence = t.Confidence, ProtectedAssetHex = t.ProtectedAssetHex,
                ProtectedAssetKind = t.ProtectedAssetKind,
                ProtectedAssetValue = t.ProtectedAssetValue,
                ThreatSeverity = t.ThreatSeverity,
                PrimaryArmyId = o.MoverArmyId ?? t.PrimaryArmyId,
                SuspendedOffensiveIntentKey = t.SuspendedOffensiveIntentKey,
                ReturnHex = t.ReturnHex, ProjectedWinChance = t.ProjectedWinChance,
                CoversAllDefenders = t.CoversAllDefenders, EstimatedEta = t.EstimatedEta,
            };
            MissionIntent intent = NewIntent(o, turn, MissionKind.ActiveDefence,
                CommitmentTier.Hard, payload);
            RetireReturnFallbacksForActor(state, payload.PrimaryArmyId,
                "fresh ActiveDefence admitted");
            state.Put(intent);
            if (t.SuspendedOffensiveIntentKey.HasValue
                && state.TryGet(t.SuspendedOffensiveIntentKey.Value, out MissionIntent offensive)
                && IsOffensiveGroundCombatIntent(offensive)
                && offensive.PreferredMoverArmyId == payload.PrimaryArmyId)
            {
                offensive.Status = IntentStatus.Suspended;
                offensive.Suspended = SuspendReason.ActiveDefencePreemption;
                AiDebugLog.Write($"[AI][V2][ActiveDefence][Continuity] decision=SUSPEND offensive={offensive.IntentKey} actor={payload.PrimaryArmyId}");
            }
            AiDebugLog.Write($"[AI][V2][ActiveDefence][Continuity] decision=CREATE enemy={t.EnemyArmyId} actor={payload.PrimaryArmyId}");
        }
    }
}
