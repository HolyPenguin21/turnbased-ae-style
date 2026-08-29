using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  DEMAND LAYER  (Strategy V2 — Strategic Manager)
    // ===========================================================================================
    //  Converts the turn's FROZEN strategic evaluation into concrete AxisDemand[] — capability
    //  SHORTAGES, never card requests. Axes say WHAT is missing; StrategicManager decides HOW.
    //
    //  Only Recon is wired. The other four axes are extensible hooks that return nothing yet.
    //
    //  Recon: sizes required Scout capacity from the ONE Recon-objective enumeration
    //  (ReconObjectiveEvaluator), NOT a private duplicate scout-target estimator. Objectives
    //  already covered by a valid active Recon intent do NOT need new capacity, and a solo Recce
    //  claimed by an active operation is "existing", not "available".
    // ===========================================================================================
    public static class DemandLayer
    {
        public static List<AxisDemand> Generate(WorldSnapshot snap, DesireBreakdown breakdown,
            IReadOnlyList<ReconObjective> objectives, IReadOnlyList<MissionIntent> activeIntents,
            ActorCommitments commitments)
        {
            var demands = new List<AxisDemand>();
            demands.AddRange(ReconDemands(snap, objectives, activeIntents, commitments));
            demands.AddRange(AggressionDemands(snap, breakdown));
            demands.AddRange(DefenceDemands(snap, breakdown));
            demands.AddRange(EconomyDemands(snap, breakdown));
            demands.AddRange(DevelopmentDemands(snap, breakdown));

            foreach (AxisDemand d in demands)
                AiDebugLog.Write($"[AI][V2]   demand — {d} | {d.Explain}");
            return demands;
        }

        // --------------------------------------------------------------------------- Recon ----
        private static IEnumerable<AxisDemand> ReconDemands(WorldSnapshot snap,
            IReadOnlyList<ReconObjective> objectives, IReadOnlyList<MissionIntent> activeIntents,
            ActorCommitments commitments)
        {
            if (snap?.Self?.Armies == null || objectives == null || objectives.Count == 0)
                yield break;

            var coveredKeys = new HashSet<MissionIntentKey>();
            if (activeIntents != null)
                foreach (MissionIntent i in activeIntents)
                    coveredKeys.Add(i.IntentKey);

            var uncovered = objectives
                .Where(o => o.BaseValue > 0f && !coveredKeys.Contains(o.IntentKey))
                .ToList();
            if (uncovered.Count == 0)
                yield break;

            int activeReconExecutions = activeIntents?.Count(i => i.Scout != null) ?? 0;
            int remainingConcurrency = Mathf.Max(0, AiConfigV2.maxConcurrentReconExecutions - activeReconExecutions);
            if (remainingConcurrency == 0)
                yield break;

            int requiredNewExecutions = Mathf.Min(remainingConcurrency, uncovered.Count);

            // "available" = solo Recce that can be tasked AND is not already claimed by an operation.
            int availableUncommittedScouts = snap.Self.Armies.Count(a =>
                a != null && a.IsSoloRecce && !a.IsPrison && !a.IsAir && a.MemberCount > 0
                && a.CurrentMovement > 0
                && (commitments == null || !commitments.IsArmyClaimed(a.ArmyId)));

            int desiredAmount = Mathf.Max(0, requiredNewExecutions - availableUncommittedScouts);
            if (desiredAmount <= 0)
                yield break;

            ReconObjective best = uncovered
                .OrderByDescending(o => o.BaseValue)
                .ThenBy(o => o.IntentKey)
                .First();
            bool wantStealth = uncovered
                .OrderByDescending(o => o.BaseValue)
                .Take(requiredNewExecutions)
                .Any(o => o.Stealth == StealthRequirement.Required || o.DetectionRisk > 0f);

            yield return new AxisDemand
            {
                RequestingAxis = DesireAxis.Recon,
                Capability = CapabilityKind.ScoutCapability,
                DesiredAmount = desiredAmount,
                PreferredTraits = wantStealth ? TraitPreference.Stealth : TraitPreference.None,
                TargetHex = best.FocusHex,
                Value = best.BaseValue,
                Explain = $"{uncovered.Count} uncovered obj, want {requiredNewExecutions} exec "
                    + $"(rem concurrency {remainingConcurrency}), have {availableUncommittedScouts} free scout(s), "
                    + $"miss {desiredAmount}",
            };
        }

        // ------------------------------------------------------- extensible axis hooks ----
        //  Kept as explicit no-op methods so the wiring point for each future axis is visible.
        private static IEnumerable<AxisDemand> AggressionDemands(WorldSnapshot s, DesireBreakdown b) =>
            Enumerable.Empty<AxisDemand>();
        private static IEnumerable<AxisDemand> DefenceDemands(WorldSnapshot s, DesireBreakdown b) =>
            Enumerable.Empty<AxisDemand>();
        private static IEnumerable<AxisDemand> EconomyDemands(WorldSnapshot s, DesireBreakdown b) =>
            Enumerable.Empty<AxisDemand>();
        private static IEnumerable<AxisDemand> DevelopmentDemands(WorldSnapshot s, DesireBreakdown b) =>
            Enumerable.Empty<AxisDemand>();
    }
}
