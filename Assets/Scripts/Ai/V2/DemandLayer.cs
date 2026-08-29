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

            // An objective is only COVERED when a live intent tracks it AND that intent's committed
            // actor is still STRUCTURALLY capable of running it. ActorCommitments already encodes
            // exactly that test (it only claims a mover whose actor is a solo Recce still capable
            // of the intent's real stealth requirement), so "is this objective covered" reduces to
            // "is the intent's mover claimed" — one source of truth, no second capability check.
            var coveredKeys = new HashSet<MissionIntentKey>();
            int activeReconExecutions = 0;
            if (activeIntents != null && commitments != null)
                foreach (MissionIntent i in activeIntents)
                {
                    if (i.Scout == null || i.PreferredMoverArmyId == null
                        || !commitments.IsArmyClaimed(i.PreferredMoverArmyId.Value))
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
            List<ReconObjective> topN = uncovered.Take(requiredNewExecutions).ToList();

            // Split by capability PROFILE — a stealth-Required objective needs a stealth scout; a
            // plain Explore is fine with any scout. Emitting one blanket stealth demand would make
            // the AI build stealth scouts to cover plain jobs an ordinary existing scout already
            // handles.
            int stealthNeeded = topN.Count(o =>
                o.Stealth == StealthRequirement.Required || o.DetectionRisk > 0f);
            int genericNeeded = requiredNewExecutions - stealthNeeded;

            var claimed = commitments?.ClaimedArmyIdSet;
            int stealthSupply = ScoutMoverSelector.Eligible(snap,
                new ScoutMissionTarget { Stealth = StealthRequirement.Required }, claimed).Count;
            int anySupply = ScoutMoverSelector.Eligible(snap,
                new ScoutMissionTarget { Stealth = StealthRequirement.None }, claimed).Count;

            // Stealth jobs consume stealth scouts first; whatever stealth scouts are left plus the
            // non-stealth eligible scouts cover the generic jobs.
            int missStealth = Mathf.Max(0, stealthNeeded - stealthSupply);
            int stealthLeftover = Mathf.Max(0, stealthSupply - stealthNeeded);
            int genericSupply = Mathf.Max(0, anySupply - stealthSupply) + stealthLeftover;
            int missGeneric = Mathf.Max(0, genericNeeded - genericSupply);

            // FIXED mission overhead only (actor-independent). Recon has none — the deployed
            // scout's own activation AP and the stealth surcharge are added per candidate by
            // StrategicManager from the card + RequiredTraits.
            const float reconFixedOverheadAp = 0f;

            if (missStealth > 0)
            {
                ReconObjective best = topN.FirstOrDefault(o =>
                    o.Stealth == StealthRequirement.Required || o.DetectionRisk > 0f) ?? uncovered[0];
                yield return new AxisDemand
                {
                    RequestingAxis = DesireAxis.Recon,
                    Capability = CapabilityKind.ScoutCapability,
                    DesiredAmount = missStealth,
                    RequiredTraits = TraitPreference.Stealth,
                    MinimumFollowupAp = reconFixedOverheadAp,
                    TargetHex = best.FocusHex,
                    Value = best.BaseValue,
                    Explain = $"{stealthNeeded} stealth job(s), {stealthSupply} stealth scout(s) free, miss {missStealth}",
                };
            }

            if (missGeneric > 0)
            {
                ReconObjective best = topN.FirstOrDefault(o =>
                    o.Stealth != StealthRequirement.Required && !(o.DetectionRisk > 0f)) ?? uncovered[0];
                yield return new AxisDemand
                {
                    RequestingAxis = DesireAxis.Recon,
                    Capability = CapabilityKind.ScoutCapability,
                    DesiredAmount = missGeneric,
                    RequiredTraits = TraitPreference.None,
                    PreferredTraits = TraitPreference.Stealth,   // nice-to-have, never a filter
                    MinimumFollowupAp = reconFixedOverheadAp,
                    TargetHex = best.FocusHex,
                    Value = best.BaseValue,
                    Explain = $"{genericNeeded} generic job(s), {genericSupply} scout(s) free "
                        + $"(any {anySupply}, stealth {stealthSupply}), miss {missGeneric}",
                };
            }
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
