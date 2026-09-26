using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Game.Combat;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using UnityEngine;

namespace Game.Ai.V2
{
    // Reinforcement — the intercept's primary alone misses the gate while existing free armies
    // can bring it over (the shared cross-hex gather, GroundCombatAssemblyPlanner.PlanGather):
    // one support at a time walks to the primary and hands its bodies over; the operation
    // intercepts once the primary clears, or pulls the gather's next support in.
    public enum ActiveDefencePhase { Intercept, Return, Reinforcement }

    public struct ActiveDefenceMissionTarget
    {
        public ActiveDefencePhase Phase;
        public int EnemyArmyId;
        public HexCoord LastKnownHex;
        public int LastObservedTurn;
        public float Confidence;
        public HexCoord ProtectedAssetHex;
        public AssetKind ProtectedAssetKind;
        public float ProtectedAssetValue;
        public float ThreatSeverity;
        public int? PrimaryArmyId;
        // The Reinforcement leg's walker (its mover); null on Intercept / Return.
        public int? SupportArmyId;
        // ATK §49 — see ActiveDefenceIntent.SuspendedOffensiveIntentKey.
        public MissionIntentKey? SuspendedOffensiveIntentKey;
        public HexCoord? ReturnHex;
        public float ProjectedWinChance;
        public bool CoversAllDefenders;
        public int EstimatedEta;
    }

    public sealed class ActiveDefenceObjective
    {
        public ActiveDefenceMissionTarget Target;
        public TaskScore TaskScore;
        public float BaseValue => TaskScore.Value;
        public MissionIntentKey IntentKey => MissionIntentKey.ForActiveDefence(Target.EnemyArmyId);
    }

    // Threat -> objective is deliberately snapshot-only. One hostile army yields at most one
    // objective: the protected asset is selected by the canonical TaskScore, never by a private
    // defence-family multiplier.
    public static class ActiveDefenceObjectiveEvaluator
    {
        public static List<ActiveDefenceObjective> Enumerate(WorldSnapshot snap)
        {
            var result = new List<ActiveDefenceObjective>();
            IEnumerable<AssetThreatSnapshot> threats = snap?.Threat?.Threats
                ?? Array.Empty<AssetThreatSnapshot>();

            // Analysis includes historical AiReconMemory contacts in Threat. They are honest
            // observations, but are not necessarily present in AiMapMemory's canonical enemy
            // sightings. ActiveDefenceProvisioner requires that canonical sighting to resolve
            // an actual intercept. Filter at objective admission, BEFORE AP allocation; retain
            // historical threats in Analysis for strategic pressure and Recon information needs.
            // AiReconMemory.Historical excludes IDs already in Known.EnemySightings, so an ID
            // membership check identifies precisely the source contract the provisioner uses.
            var interceptableIds = new HashSet<int>(snap?.Known?.EnemySightings?
                .Select(s => s.ArmyId) ?? Enumerable.Empty<int>());

            foreach (IGrouping<int, AssetThreatSnapshot> group in threats
                .Where(t => IsHonestPositionedHostile(t)
                    && interceptableIds.Contains(t.Contact.Army.ArmyId))
                .GroupBy(t => t.Contact.Army.ArmyId))
            {
                AssetThreatSnapshot chosen = group
                    .Select(t => new { Threat = t, Score = ScoreThreat(snap, t) })
                    .OrderByDescending(x => x.Score.Value)
                    .ThenByDescending(x => x.Threat.Asset.Value * x.Threat.Severity)
                    .ThenBy(x => x.Threat.EnemyEta ?? int.MaxValue)
                    .ThenBy(x => x.Threat.Asset.Hex.Q).ThenBy(x => x.Threat.Asset.Hex.R)
                    .Select(x => x.Threat).First();
                if (OnKnownForeignStructure(snap, chosen.Contact.Position.Value))
                {
                    AiDebugLog.WriteDeduped(group.Key.ToString(CultureInfo.InvariantCulture),
                        $"[AI][V2][ActiveDefence][Objective] decision=DEFER enemy={group.Key} "
                        + "reason=enemy_on_known_foreign_structure attack_owner_required");
                    continue;
                }
                TaskScore score = ScoreThreat(snap, chosen);
                var target = new ActiveDefenceMissionTarget
                {
                    Phase = ActiveDefencePhase.Intercept,
                    EnemyArmyId = group.Key,
                    LastKnownHex = chosen.Contact.Position.Value,
                    LastObservedTurn = chosen.Contact.LastObservedTurn,
                    Confidence = chosen.Confidence,
                    ProtectedAssetHex = chosen.Asset.Hex,
                    ProtectedAssetKind = chosen.Asset.Kind,
                    ProtectedAssetValue = chosen.Asset.Value,
                    ThreatSeverity = chosen.Severity,
                    EstimatedEta = chosen.EnemyEta.GetValueOrDefault(),
                };
                result.Add(new ActiveDefenceObjective { Target = target, TaskScore = score });
                AiDebugLog.WriteDeduped(group.Key.ToString(CultureInfo.InvariantCulture),
                    $"[AI][V2][ActiveDefence][Objective] decision=ACCEPT enemy={group.Key} "
                    + $"asset={chosen.Asset.Kind}@({chosen.Asset.Hex.Q},{chosen.Asset.Hex.R}) "
                    + $"task={score.Value:0.00} severity={chosen.Severity:0.00}");
            }

            return result.OrderByDescending(o => o.BaseValue)
                .ThenBy(o => o.Target.EnemyArmyId).ToList();
        }

        // An enemy standing on a structure this player remembers as someone else's is never an
        // intercept. Winning a fight on a hostile structure captures/destroys it — that is the
        // Attack objective for the site (AttackObjectiveEvaluator.IsHostileAttackStructure, which
        // takes the enemy as part of the site's one defender package, §31). Any other foreign
        // owner is refused by the ground-move gate for a Combat step anyway (the structure reads
        // as an undefended foreign takeover). Deciding it here, at admission, keeps the planner,
        // Demand and Continuity on one answer: no objective, no shortage, no pursuit.
        internal static bool OnKnownForeignStructure(WorldSnapshot snap, HexCoord hex)
        {
            IReadOnlyList<AiMapMemory.KnownBuilding> buildings = snap?.Known?.Buildings;
            if (buildings == null || snap.Observer == null)
                return false;
            for (int i = 0; i < buildings.Count; i++)
                if (buildings[i].Hex.Equals(hex) && buildings[i].Owner != null
                    && buildings[i].Owner != snap.Observer)
                    return true;
            return false;
        }

        public static ActiveDefenceObjective ForTrackedEnemy(WorldSnapshot snap, int enemyArmyId) =>
            Enumerate(snap).FirstOrDefault(o => o.Target.EnemyArmyId == enemyArmyId);

        // ---- LIVE (revalidation / post-execution ledger pass) --------------------------------

        // The ONE honest completion check for an Intercept objective, and the live counterpart of
        // Enumerate above. MissionRevalidator, MissionOutcomeLedger, Enumerate and
        // ActiveDefenceProvisioner must all apply the same knowledge rule: strategic knowledge of
        // an enemy army is AiMapMemory's (ARCH-02 canonical seams), and "absent from the world" is
        // never objective completion — a global ArmyRegistry sweep would let an intercept retire
        // because the army moved/died somewhere we cannot see, while the other stages still
        // consider it a live target, recreating and instantly "completing" the mission every pass.
        // Mirrors RaidObjectiveEvaluator.IsObjectiveSatisfiedLive:
        //  1) the tracked id is fielded by US now — positive confirmation of the outcome of an
        //     action we ourselves completed (the one ArmyRegistry read the seam allows);
        //  2) honest memory still tracks it as a hostile, non-neutral army — NOT satisfied. It is
        //     exactly as real as our last observation says, fog included;
        //  3) honest memory now tracks it as a NEUTRAL army — this hostile-threat objective is
        //     over (Raid, not ActiveDefence, owns neutrals);
        //  4) honest memory no longer tracks it at all — either the hex was genuinely re-observed
        //     empty (AiMapMemory's own "corrected (gone on re-observation)" write) or a
        //     player-owned sighting aged out under the existing enemySightingMemoryTurns
        //     uncertainty policy. Both are "gone as far as we may honestly know"; neither invents
        //     a new lost-contact state, and Enumerate cannot re-admit an objective for an id that
        //     is no longer in Known.EnemySightings, so this can never oscillate.
        public static bool IsObjectiveSatisfiedLive(PlayerSetupData player, int enemyArmyId)
        {
            if (player == null)
                return false;
            if (ArmyRegistry.AllForOwner(player)
                .Any(a => a != null && a.Id == enemyArmyId && a.Members.Count > 0))
                return true;
            foreach (AiMapMemory.KnownEnemySighting s in AiMapMemory.AllKnownEnemySightings(player))
                if (s.ArmyId == enemyArmyId)
                    return false;
            return true;
        }

        internal static bool ShouldStopPursuit(bool hasListedThreat, bool movingAway,
            bool homeDistanceAtFullNegative, bool activeStillBeatsAlternative) =>
            !hasListedThreat && movingAway && homeDistanceAtFullNegative
            && !activeStillBeatsAlternative;

        // `projectedActivationAp` — the activation AP of the roster the assembly plan will really
        // field, when the caller has that projection (GroundCombatAssemblyPlanner is its one
        // owner). Null keeps the actor's own snapshot figure. The score must be folded from the
        // same force the envelope is priced from.
        public static TaskScore WithResponse(ActiveDefenceObjective objective, ArmySnapshot actor,
            float winChance, int eta, float moverOpportunityCost = 0f,
            int? projectedActivationAp = null) =>
            TaskScoreEvaluator.WithActorResponse(objective.TaskScore, actor, winChance, eta,
                moverOpportunityCost, projectedActivationAp);

        private static bool IsHonestPositionedHostile(AssetThreatSnapshot t) =>
            t?.Asset != null && t.Contact?.Army != null
            && t.Contact.Army.ArmyId >= 0
            && t.Contact.Position.HasValue
            && t.Contact.Army.Owner != null && !t.Contact.Army.Owner.IsNeutral;

        private static TaskScore ScoreThreat(WorldSnapshot snap, AssetThreatSnapshot t)
        {
            int age = Math.Max(0, (snap?.TurnNumber ?? 0) - t.Contact.LastObservedTurn);
            int homeDistance = TaskScoreEvaluator.NearestOwnedHomeDistance(snap,
                t.Contact.Position.Value);
            float assetNorm = t.Asset.Value / Mathf.Max(1f, AiConfigV2.assetValueCitadel);
            return new TaskScore(
                strategicRelevance: TaskScoreEvaluator.StrategicRelevance(assetNorm),
                threatDirection: TaskScoreEvaluator.ThreatDirection(
                    1f / (1f + t.EnemyEta.GetValueOrDefault())),
                militaryTargetRelevance: TaskScoreEvaluator.MilitaryTargetRelevance(t.PotentialDamage),
                staleness: TaskScoreEvaluator.StaleIntelPenalty(
                    age / (float)Mathf.Max(1, AiConfigV2.scoutSurveilStaleTurnsHi)),
                ownTerritoryProximity: TaskScoreEvaluator.OwnTerritoryProximity(homeDistance));
        }
    }
}
