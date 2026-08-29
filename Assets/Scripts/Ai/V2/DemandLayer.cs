using System.Collections.Generic;
using System.Linq;
using Game.Players;
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
            ActorCommitments commitments, PlayerSetupData player)
        {
            var demands = new List<AxisDemand>();
            demands.AddRange(ReconDemands(snap, objectives, activeIntents, commitments, player));
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
            ActorCommitments commitments, PlayerSetupData player)
        {
            if (snap?.Self?.Armies == null || objectives == null || objectives.Count == 0)
                yield break;

            // An objective is only COVERED when a live intent tracks it AND that intent still has a
            // live, owned committed actor — an intent whose scout was destroyed leaves its
            // objective genuinely uncovered (netted back out below by available spare scouts).
            var coveredKeys = new HashSet<MissionIntentKey>();
            int activeReconExecutions = 0;
            if (activeIntents != null)
                foreach (MissionIntent i in activeIntents)
                {
                    if (i.Scout == null || !ActorCommitments.HasLiveActor(i, player))
                        continue;
                    coveredKeys.Add(i.IntentKey);
                    activeReconExecutions++;
                }

            var uncovered = objectives
                .Where(o => o.BaseValue > 0f && !coveredKeys.Contains(o.IntentKey))
                .OrderByDescending(o => o.BaseValue)
                .ThenBy(o => o.IntentKey)
                .ToList();
            if (uncovered.Count == 0)
                yield break;

            int remainingConcurrency = Mathf.Max(0, AiConfigV2.maxConcurrentReconExecutions - activeReconExecutions);
            if (remainingConcurrency == 0)
                yield break;

            int requiredNewExecutions = Mathf.Min(remainingConcurrency, uncovered.Count);

            // Does the work we would newly fund actually need stealth? If any of the top
            // requiredNewExecutions uncovered objectives is stealth-Required, treat the whole
            // demand as stealth-Required — conservative (may over-request when the AI holds
            // non-stealth scouts against a mix of jobs) but it removes the estimator divergence:
            // never count a plain scout toward a job it cannot execute.
            bool stealthRequired = uncovered
                .Take(requiredNewExecutions)
                .Any(o => o.Stealth == StealthRequirement.Required || o.DetectionRisk > 0f);

            // Available supply via the SHARED eligibility primitive — the exact rule provisioning
            // applies (solo Recce, can act now, and for a stealth job: hidden or able to enter
            // stealth before its first move), minus armies already claimed by an operation.
            var probe = new ScoutMissionTarget
            {
                Stealth = stealthRequired ? StealthRequirement.Required : StealthRequirement.None,
            };
            int availableUncommittedScouts = ScoutMoverSelector
                .Eligible(snap, probe, commitments?.ClaimedArmyIdSet)
                .Count;

            int desiredAmount = Mathf.Max(0, requiredNewExecutions - availableUncommittedScouts);
            if (desiredAmount <= 0)
                yield break;

            ReconObjective best = uncovered[0];
            float minFollowup = AiConfigV2.scoutNotionalActivationAp
                + (stealthRequired ? AiConfigV2.scoutOptionalStealthAp : 0);

            yield return new AxisDemand
            {
                RequestingAxis = DesireAxis.Recon,
                Capability = CapabilityKind.ScoutCapability,
                DesiredAmount = desiredAmount,
                RequiredTraits = stealthRequired ? TraitPreference.Stealth : TraitPreference.None,
                PreferredTraits = stealthRequired ? TraitPreference.None : TraitPreference.Stealth,
                MinimumFollowupAp = minFollowup,
                TargetHex = best.FocusHex,
                Value = best.BaseValue,
                Explain = $"{uncovered.Count} uncovered obj, want {requiredNewExecutions} exec "
                    + $"(rem concurrency {remainingConcurrency}), have {availableUncommittedScouts} eligible "
                    + $"{(stealthRequired ? "stealth-" : "")}scout(s), miss {desiredAmount}, followup {minFollowup}ap",
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
