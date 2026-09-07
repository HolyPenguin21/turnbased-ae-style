using System.Collections.Generic;
using Game.Map;
using Game.Players;
using UnityEngine;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  AP WORKLOAD AGGREGATOR  (AI-MGR — Dynamic Strategic Effect Utility, owner-aggregation rework)
    // ===========================================================================================
    //  WorldAnalysis holds only the STRUCTURAL facts of the AP action economy (Self.ApEconomy) — it
    //  cannot know which of those actions are legal / useful this turn. This stage runs AFTER
    //  WorldAnalysis, once the owners have spoken, and turns their witnessed workload into the
    //  authoritative WorldSnapshot.ApWorkload:
    //
    //    · actionable armies      -> PreTurnCapacityAnalysis.CountActionableFieldArmies (unactivated)
    //    · strategic card play    -> the DemandLayer AxisDemand set (already scope-filtered and
    //                                shaped to real capability gaps — StrategicManager's owner signal)
    //    · Development opportunity -> a Development-axis / DevelopmentInfrastructure demand exists
    //    · recon-air sorties      -> ReconAssignmentPlanner.MeasureAirCapacity (the WITNESSED launch
    //                                count, not the structural upper bound)
    //
    //  MarginalApUtility is then the SAME ramp WorldAnalysis used to run, now fed owner-witnessed
    //  input instead of a structural guess. No card-legality / mission-planning logic lives here —
    //  every component is read from a primitive an existing owner already publishes.
    // ===========================================================================================
    public static class ApWorkloadAggregator
    {
        // card-materialised capability kinds — an unmet demand for one of these is AP the AI could
        // still usefully spend on a Strategic Manager card play this turn. Infrastructure kinds are
        // fulfilled through BuildingPlayExecutor, not the AP-costing card path, so they are excluded.
        private static bool IsCardMaterialised(CapabilityKind k) =>
            k == CapabilityKind.ScoutCapability
            || k == CapabilityKind.GarrisonCombatPower
            || k == CapabilityKind.FieldCombatPower
            || k == CapabilityKind.Hero;

        public static ApWorkloadAssessment Assess(WorldSnapshot snap, IReadOnlyList<AxisDemand> demands,
            PlayerSetupData player, AiTurnContext ctx, PlayerRoot root,
            IReadOnlyList<ReconObjective> reconObjectives, IReadOnlyList<MissionIntent> activeIntents,
            ActorCommitments commitments)
        {
            var a = new ApWorkloadAssessment();
            SelfSnapshot self = snap?.Self;
            if (self == null)
                return a;

            float baseAp = Mathf.Max(1f,
                self.ApEconomy != null ? self.ApEconomy.BaseActionPoints : self.ActionPoints);

            // --- actionable armies (owner: execution / capacity state) --------------------------
            int actionableArmies = Game.Ai.V2.Initiative.PreTurnCapacityAnalysis
                .CountActionableFieldArmies(player, unactivatedOnly: true);
            float perArmyAp = RepresentativeActivationAp(self);
            a.ArmyActionableAp = actionableArmies * perArmyAp;

            // --- strategic card play (owner: StrategicManager, via the DemandLayer demand set) ---
            float cardAp = 0f;
            bool devOpportunity = false;
            if (demands != null)
                foreach (AxisDemand d in demands)
                {
                    if (d == null) continue;
                    if (d.RequestingAxis == DesireAxis.Development
                        || d.Capability == CapabilityKind.DevelopmentInfrastructure)
                        devOpportunity = true;
                    if (!IsCardMaterialised(d.Capability)) continue;
                    cardAp += Mathf.Max(0f, d.MinimumFollowupAp)
                        + AiConfigV2.apStrategicCardApProxy * Mathf.Clamp(d.DesiredAmount, 0f, 3f);
                }
            a.StrategicCardAp = cardAp;

            // --- Development opportunity (owner: Development axis) ------------------------------
            a.DevelopmentAp = devOpportunity ? AiConfigV2.apDevActionApProxy : 0f;

            // --- recon-air sorties (owner: ReconAssignmentPlanner, the canonical capacity owner) -
            (int airborneWitnessed, int spareLaunchWitnessed) = ReconAssignmentPlanner.MeasureAirCapacity(
                ctx, player, root, snap, reconObjectives, activeIntents, commitments);
            a.AirSortieAp = (airborneWitnessed + spareLaunchWitnessed) * AiConfigV2.apAirSortieApProxy;

            a.UsefulApDemand = a.ArmyActionableAp + a.StrategicCardAp + a.DevelopmentAp + a.AirSortieAp;
            a.MarginalApUtility = Curves.Ramp(a.UsefulApDemand / baseAp,
                AiConfigV2.apMarginalUtilRampLo, AiConfigV2.apMarginalUtilRampHi);
            return a;
        }

        // Mean activation AP over own real field formations (non-garrison / non-prison / non-air with
        // members), floored at 1 — the cost one more "activate an army" action would carry.
        private static float RepresentativeActivationAp(SelfSnapshot self)
        {
            if (self.Armies == null) return 1f;
            float sum = 0f;
            int n = 0;
            foreach (ArmySnapshot ar in self.Armies)
            {
                if (ar == null || ar.IsGarrison || ar.IsPrison || ar.IsAir || ar.MemberCount == 0)
                    continue;
                sum += Mathf.Max(1f, ar.ActivationApCost);
                n++;
            }
            return n > 0 ? sum / n : 1f;
        }
    }
}
