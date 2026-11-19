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
    // Score — the TaskScore of defending an asset (one formula for the strategic Intercept, the
    // immediate strike and the held-base garrison demand) and the actor-response fold.
    public static partial class ActiveDefenceObjectiveEvaluator
    {
        // `projectedActivationAp` — the activation AP of the roster the assembly plan will really
        // field, when the caller has that projection (GroundCombatAssemblyPlanner is its one
        // owner). Null keeps the actor's own snapshot figure. The score must be folded from the
        // same force the envelope is priced from.
        public static TaskScore WithResponse(ActiveDefenceObjective objective, ArmySnapshot actor,
            float winChance, int eta, float moverOpportunityCost = 0f,
            int? projectedActivationAp = null) =>
            TaskScoreEvaluator.WithActorResponse(objective.TaskScore, actor, winChance, eta,
                moverOpportunityCost, projectedActivationAp);

        private static TaskScore BuildActiveDefenceScore(WorldSnapshot snap, AssetThreatSnapshot t)
        {
            int age = Math.Max(0, (snap?.TurnNumber ?? 0) - t.Contact.LastObservedTurn);
            int homeDistance = TaskScoreEvaluator.NearestOwnedHomeDistance(snap,
                t.Contact.Position.Value);
            // PotentialDamage is the chance the contact takes the asset (a 0..1 FRACTION of
            // Asset.Value). The damage kept off is that fraction of this asset's value on the
            // Citadel scale: a scout that would take an Extractor is not a Citadel-sized loss.
            // 2026-09-30 (user decision) — the asset's relevance and the threat's direction are
            // worth defending only in the measure the contact can actually hurt it: both are
            // weighted by the same PotentialDamage, so a passing weak contact is a cheap target.
            AssetDefenceTerms(t, out float relevance, out float threatDirection, out float preventedDamage);
            return new TaskScore(
                strategicRelevance: relevance,
                threatDirection: threatDirection,
                preventedDamage: preventedDamage,
                intelAgePenalty: TaskScoreEvaluator.IntelAgePenalty(
                    age / (float)Mathf.Max(1, AiConfigV2.reconIntelStaleTurnsHi)),
                ownTerritoryProximity: TaskScoreEvaluator.LeashedProximity(homeDistance,
                    AiConfigV2.activeDefenceLeashHexes, AiConfigV2.taskScoreActiveDefenceLeashPerHex));
        }

        // What keeping this contact off its asset is worth: the asset's relevance (its Citadel-scale
        // value), the threat's direction and the damage it would do, all weighted by
        // PotentialDamage. One formula for the strategic Intercept score, the immediate strike that
        // keeps a saboteur off an own structure (EnumerateImmediate) and the held-base garrison
        // demand (AggressionDemandEvaluator.Garrison), which defends the same asset in place.
        internal static void AssetDefenceTerms(AssetThreatSnapshot t, out float strategicRelevance,
            out float threatDirection, out float preventedDamage)
        {
            float assetNorm = t.Asset.Value / Mathf.Max(1f, AiConfigV2.assetValueCitadel);
            float damage = Mathf.Clamp01(t.PotentialDamage);
            strategicRelevance = TaskScoreEvaluator.StrategicRelevance(assetNorm * damage);
            threatDirection = TaskScoreEvaluator.ThreatDirection(
                damage / (1f + t.EnemyEta.GetValueOrDefault()));
            preventedDamage = TaskScoreEvaluator.PreventedDamage(
                t.PotentialDamage * Mathf.Clamp01(assetNorm));
        }
    }
}
