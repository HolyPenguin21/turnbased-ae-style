using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Game.Combat;
using Game.HexGrid;
using Game.Players;
using UnityEngine;

namespace Game.Ai.V2
{
    public enum ActiveDefencePhase { Intercept, Return }

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
        public MissionIntentKey? SuspendedRaidIntentKey;
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

            foreach (IGrouping<int, AssetThreatSnapshot> group in threats
                .Where(IsHonestPositionedHostile)
                .GroupBy(t => t.Contact.Army.ArmyId))
            {
                AssetThreatSnapshot chosen = group
                    .Select(t => new { Threat = t, Score = ScoreThreat(snap, t) })
                    .OrderByDescending(x => x.Score.Value)
                    .ThenByDescending(x => x.Threat.Asset.Value * x.Threat.Severity)
                    .ThenBy(x => x.Threat.EnemyEta ?? int.MaxValue)
                    .ThenBy(x => x.Threat.Asset.Hex.Q).ThenBy(x => x.Threat.Asset.Hex.R)
                    .Select(x => x.Threat).First();
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
                    EstimatedEta = chosen.EnemyEta ?? AiConfigV2.etaUnknownContactPenalty,
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

        public static ActiveDefenceObjective ForTrackedEnemy(WorldSnapshot snap, int enemyArmyId) =>
            Enumerate(snap).FirstOrDefault(o => o.Target.EnemyArmyId == enemyArmyId);

        internal static bool ShouldStopPursuit(bool hasListedThreat, bool movingAway,
            bool homeDistanceAtFullNegative, bool activeStillBeatsAlternative) =>
            !hasListedThreat && movingAway && homeDistanceAtFullNegative
            && !activeStillBeatsAlternative;

        public static TaskScore WithResponse(ActiveDefenceObjective objective, ArmySnapshot actor,
            float winChance, int eta, float moverOpportunityCost = 0f)
        {
            TaskScore s = objective.TaskScore;
            float activation = actor != null && !actor.HasActivatedThisTurn
                ? Mathf.Max(0, actor.ActivationApCost) : 0f;
            return new TaskScore(
                staleness: s.Staleness,
                strategicRelevance: s.StrategicRelevance,
                threatDirection: s.ThreatDirection,
                ownTerritoryProximity: s.OwnTerritoryProximity,
                militaryTargetRelevance: s.MilitaryTargetRelevance,
                winChance: TaskScoreEvaluator.WinChance(winChance),
                cardPrice: activation * AiConfigV2.taskScoreReactivationApWeight,
                delivery: TaskScoreEvaluator.DeliveryFromEta(actor?.ActivationApCost ?? 0,
                    eta, AiConfigV2.taskScoreReactivationApWeight),
                moverOpportunityCost: Mathf.Max(0f, moverOpportunityCost));
        }

        private static bool IsHonestPositionedHostile(AssetThreatSnapshot t) =>
            t?.Asset != null && t.Contact?.Army != null
            && t.Contact.Army.ArmyId >= 0
            && t.Contact.Source == ContactSource.Honest
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
                    1f / (1f + (t.EnemyEta ?? AiConfigV2.etaUnknownContactPenalty))),
                militaryTargetRelevance: TaskScoreEvaluator.MilitaryTargetRelevance(t.PotentialDamage),
                staleness: TaskScoreEvaluator.StaleIntelPenalty(
                    age / (float)Mathf.Max(1, AiConfigV2.scoutSurveilStaleTurnsHi)),
                ownTerritoryProximity: TaskScoreEvaluator.OwnTerritoryProximity(homeDistance));
        }
    }
}
