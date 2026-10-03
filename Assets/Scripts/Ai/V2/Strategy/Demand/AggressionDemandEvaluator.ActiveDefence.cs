using System.Collections.Generic;
using System.Linq;
using Game.Players;
using UnityEngine;

using Game.Combat;

namespace Game.Ai.V2
{
    public static partial class AggressionDemandEvaluator
    {
        // ActiveDefence shares Aggression's one capability-demand owner. The response itself is
        // decided once, by ActiveDefenceObjectiveEvaluator.AssessResponse (the same answer the
        // Mission planner acts on); this method only translates a proven capability SHORTAGE into
        // the FieldCombatPower contract Phase A already materializes. A direct response, a
        // capable force that is merely unavailable this pass, and enough power that only has to
        // regroup at the Citadel are never production.
        internal static IReadOnlyList<AxisDemand> BuildActiveDefenceDemands(WorldSnapshot snap,
            IReadOnlyList<ActiveDefenceObjective> objectives,
            IReadOnlyList<MissionIntent> activeIntents, ActorCommitments commitments,
            PlayerSetupData player, out IReadOnlyList<string> diagnostics)
        {
            var demands = new List<AxisDemand>();
            var diag = new List<string>();
            diagnostics = diag;
            if (snap?.Self?.Armies == null)
                return demands;

            objectives ??= ActiveDefenceObjectiveEvaluator.Enumerate(snap);
            HashSet<int> withdrawing = ActiveDefenceObjectiveEvaluator.WithdrawingArmyIds(activeIntents);
            foreach (ActiveDefenceObjective objective in objectives
                .Where(o => o != null)
                .OrderByDescending(o => o.BaseValue)
                .ThenBy(o => o.Target.EnemyArmyId))
            {
                int? pinnedActor = ActiveDefenceObjectiveEvaluator.IncumbentIntercept(
                    activeIntents, objective.Target.EnemyArmyId)?.ActiveDefence?.PrimaryArmyId;
                ActiveDefenceResponse response = ActiveDefenceObjectiveEvaluator.AssessResponse(
                    snap, objective, commitments?.ClaimedArmyIdSet, withdrawing, pinnedActor);
                if (response == null)
                    continue;
                string label = $"[AI][V2][ActiveDefence][Demand] enemy={objective.Target.EnemyArmyId} "
                    + $"asset={objective.Target.ProtectedAssetKind}@({objective.Target.ProtectedAssetHex.Q},"
                    + $"{objective.Target.ProtectedAssetHex.R})";
                switch (response.Kind)
                {
                    case ActiveDefenceResponseKind.Intercept:
                        diag.Add($"{label} decision=SATISFIED reason={response.Reason} "
                            + $"actor=#{response.Plan.BaseArmyId} win={response.Plan.ProjectedWinChance:0.00}");
                        continue;
                    case ActiveDefenceResponseKind.Defer:
                        diag.Add($"{label} decision=DEFER reason={response.Reason}");
                        continue;
                    case ActiveDefenceResponseKind.Regroup:
                        diag.Add($"{label} decision=NONE reason={response.Reason} "
                            + $"power={response.AvailablePower:0.#}/{response.RequiredPower:0.#}");
                        continue;
                }

                // A real capability shortage, sized by the one ground-combat requirement owner:
                // the missing total, or — when the total is already there but no formation of it
                // clears (regroup exhausted) — what the strongest force it joins still lacks.
                float required = response.RequiredPower;
                float available = response.AvailablePower;
                float deficit = Mathf.Max(1f, available + AiConfigV2.allocatorSliceEpsilon < required
                    ? required - available : required - response.StrongestPower);
                MissionIntentKey consumer = MissionIntentKey.ForActiveDefence(
                    objective.Target.EnemyArmyId);
                diag.Add($"{label} decision=CREATE capability=FieldCombatPower required={required:0.#} "
                    + $"available={available:0.#} deficit={deficit:0.#} reason={response.Reason}");
                demands.Add(new AxisDemand
                {
                    RequestingAxis = DesireAxis.Aggression,
                    Capability = CapabilityKind.FieldCombatPower,
                    DeliveryShape = CapabilityDeliveryShape.IndependentFieldArmy,
                    ConsumerIntentKey = consumer,
                    ConsumerMissionKind = MissionKind.ActiveDefence,
                    DesiredAmount = deficit,
                    RequiredCapabilityPower = deficit,
                    RequiredTraits = TraitPreference.None,
                    MinimumFollowupAp = 0f,
                    TargetHex = objective.Target.ProtectedAssetHex,
                    WorldTaskScore = objective.TaskScore,
                    Value = objective.TaskScore.Value,
                    Explain = $"ActiveDefence enemy #{objective.Target.EnemyArmyId} threatening "
                        + $"{objective.Target.ProtectedAssetKind}@({objective.Target.ProtectedAssetHex.Q},"
                        + $"{objective.Target.ProtectedAssetHex.R}) needs ~{deficit:0.#} field power "
                        + $"({available:0.#}/{required:0.#}); consumer={consumer}",
                });
            }
            return demands;
        }
    }
}
