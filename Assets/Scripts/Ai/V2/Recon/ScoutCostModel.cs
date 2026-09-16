using System.Collections.Generic;
using System.Linq;
using Game.HexGrid;
using UnityEngine;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  SCOUT COST MODEL  (Strategy V2 build-order step 4 — the shared Scout estimator)
    // ===========================================================================================
    //  "ONE ESTIMATOR, MANY STAGES" for the cheap mission type, the same rule
    //  CombatOpportunityAnalyzer enforces for raids. Mission planning uses this model to publish a
    //  concrete, actor-priced ground alternative BEFORE generic funding; Assignment/Provisioning
    //  still owns the final live-world binding and feasibility check.
    //
    //  WHAT A GROUND SCOUT ACTUALLY COSTS (game rules, not tunables):
    //    * AP     — only to ACTIVATE the mover (ArmyData.ActivationApCost). Travelling across
    //               hexes spends MOVEMENT, never AP; an already-activated army costs 0 AP to move.
    //               A stealth-Required mission adds exactly 1 AP (scoutOptionalStealthAp) — the
    //               EnterStealth before the first risky step — UNLESS the mover is already hidden.
    //    * Energy — ground solo-Recce is 0. Actor-agnostic air fallback keeps the existing widened
    //               envelope until the aviation prepass / Assignment resolves a concrete air actor.
    //
    //  IMPORTANT COST SPLIT:
    //    RequiredAp / ApDesired is THIS TURN only. RecurringActivationAp is the real activation AP
    //    paid on later turns of a multi-turn march and exists for TaskScore.Delivery only. It must
    //    never be reserved or packed as future AP.
    // ===========================================================================================
    public struct ScoutCostEstimate
    {
        public bool MoverKnown;
        public bool MoverAlreadyHidden;
        public int? PreferredMoverArmyId;

        public float ApMinimum, ApDesired, ApMaximum;
        public float EnergyMinimum, EnergyDesired, EnergyMaximum;
        public int EtaTurns;
        public float EstimatedDistance;

        // Full-operation comparison fact only. Never part of MissionRequirements/current-turn
        // resource packing.
        public float RecurringActivationAp;
    }

    public struct ScoutPairCost
    {
        public int EffActivationAp;
        public float RequiredAp;
        public int EtaTurns;
        public int Distance;
        public bool AlreadyHidden;
    }

    public static class ScoutCostModel
    {
        public static ScoutPairCost PairCost(WorldSnapshot snap, ArmySnapshot mover, HexCoord executionHex, bool stealthRequired)
        {
            int fleetBudget = snap?.Self?.Armies != null
                ? snap.Self.Armies.Select(a => a.MaxMovement).DefaultIfEmpty(0).Max() : 0;
            if (fleetBudget <= 0) fleetBudget = 1;
            int budget = mover.MaxMovement > 0 ? mover.MaxMovement : fleetBudget;

            int dist = HexGridMath.Distance(mover.Hex, executionHex);
            int eta = mover.CurrentMovement >= dist ? 1 : 1 + CeilDiv(dist - mover.CurrentMovement, budget);
            int effAp = mover.HasActivatedThisTurn ? 0 : mover.ActivationApCost;
            bool hidden = mover.IsHidden;
            float required = effAp + (stealthRequired && !hidden ? AiConfigV2.scoutOptionalStealthAp : 0f);

            return new ScoutPairCost
            {
                EffActivationAp = effAp,
                RequiredAp = required,
                EtaTurns = eta,
                Distance = dist,
                AlreadyHidden = hidden,
            };
        }

        private readonly struct PlannedGroundCost
        {
            public readonly ArmySnapshot Mover;
            public readonly ScoutPairCost Cost;
            public readonly float FullOperationAp;

            public PlannedGroundCost(ArmySnapshot mover, ScoutPairCost cost)
            {
                Mover = mover;
                Cost = cost;
                FullOperationAp = cost.RequiredAp
                    + Mathf.Max(0, mover?.ActivationApCost ?? 0) * Mathf.Max(0, cost.EtaTurns - 1);
            }
        }

        // Pre-funding planning estimate. When a usable ground actor exists, publish the cheapest
        // deterministic actor-specific alternative. This does NOT claim or bind the actor: the
        // proposal merely carries PreferredMoverArmyId so MissionAdmissionPolicy/ResourceAllocator
        // can reject impossible same-actor portfolios before financing. ReconAssignmentPlanner
        // remains the final assignment authority and can invalidate/replace the plan if live route,
        // vantage or contention facts changed.
        public static ScoutCostEstimate Estimate(WorldSnapshot snap, ScoutMissionTarget target,
            int? preferredMoverArmyId = null)
        {
            PlannedGroundCost? planned = PlanGroundCost(snap, target, preferredMoverArmyId);
            if (planned.HasValue)
            {
                PlannedGroundCost p = planned.Value;
                return new ScoutCostEstimate
                {
                    MoverKnown = true,
                    MoverAlreadyHidden = p.Cost.AlreadyHidden,
                    PreferredMoverArmyId = p.Mover.ArmyId,
                    ApMinimum = p.Cost.RequiredAp,
                    ApDesired = p.Cost.RequiredAp,
                    ApMaximum = p.Cost.RequiredAp,
                    EnergyMinimum = 0f,
                    EnergyDesired = 0f,
                    EnergyMaximum = 0f,
                    EtaTurns = p.Cost.EtaTurns,
                    EstimatedDistance = p.Cost.Distance,
                    // Even when already activated THIS turn, later turns reactivate at the actor's
                    // real activation AP. This is comparison-only future cost, never a reservation.
                    RecurringActivationAp = Mathf.Max(0, p.Mover.ActivationApCost),
                };
            }

            return NotionalFallback(snap, target);
        }

        private static PlannedGroundCost? PlanGroundCost(WorldSnapshot snap, ScoutMissionTarget target,
            int? preferredMoverArmyId)
        {
            bool stealthRequired = target.Stealth == StealthRequirement.Required;
            var candidates = new List<PlannedGroundCost>();
            foreach (ArmySnapshot mover in ScoutMoverSelector.Eligible(snap, target, null))
            {
                HexCoord executionHex = target.FocusHex;
                if (target.Kind == ScoutTargetKind.Surveil)
                {
                    SurveilVantageCandidate? vantage = SurveilVantageSelector.Rank(snap, mover, target)
                        .Cast<SurveilVantageCandidate?>().FirstOrDefault();
                    if (!vantage.HasValue)
                        continue;
                    executionHex = vantage.Value.ExecutionHex;
                }

                ScoutPairCost pair = PairCost(snap, mover, executionHex, stealthRequired);
                candidates.Add(new PlannedGroundCost(mover, pair));
            }

            if (candidates.Count == 0)
                return null;

            if (preferredMoverArmyId.HasValue)
            {
                PlannedGroundCost? pinned = candidates
                    .Where(x => x.Mover.ArmyId == preferredMoverArmyId.Value)
                    .Cast<PlannedGroundCost?>().FirstOrDefault();
                if (pinned.HasValue)
                    return pinned;
            }

            return candidates
                .OrderBy(x => x.FullOperationAp)
                .ThenBy(x => x.Cost.EtaTurns)
                .ThenBy(x => x.Cost.Distance)
                .ThenBy(x => x.Mover.ArmyId)
                .First();
        }

        private static ScoutCostEstimate NotionalFallback(WorldSnapshot snap, ScoutMissionTarget target)
        {
            var est = new ScoutCostEstimate { MoverKnown = false, MoverAlreadyHidden = false };
            float stealthAp = AiConfigV2.scoutOptionalStealthAp;
            float notionalActivationAp = AiConfigV2.scoutNotionalActivationAp;

            bool airPlausible = (target.Kind == ScoutTargetKind.Surveil || ReconScoutKinds.IsRefresh(target.Kind))
                && target.Stealth != StealthRequirement.Required && !(target.DetectionRisk > 0f);

            int fleetBudget = snap?.Self?.Armies != null
                ? snap.Self.Armies.Select(a => a.MaxMovement).DefaultIfEmpty(0).Max() : 0;
            if (fleetBudget <= 0) fleetBudget = 1;

            est.EstimatedDistance = TaskScoreEvaluator.NearestOwnedHomeDistance(snap, target.FocusHex, 0);
            est.EtaTurns = Mathf.Max(1, CeilDiv((int)est.EstimatedDistance, fleetBudget));
            est.RecurringActivationAp = notionalActivationAp;

            if (target.Kind == ScoutTargetKind.Surveil)
            {
                float req = notionalActivationAp
                    + (target.Stealth == StealthRequirement.None ? 0f : stealthAp);
                est.ApMinimum = est.ApDesired = req;
                est.ApMaximum = Mathf.Max(req, airPlausible ? AiConfigV2.airReconNotionalActivationAp : 0f);
                est.EnergyMinimum = 0f;
                est.EnergyDesired = est.EnergyMaximum =
                    airPlausible ? AiConfigV2.airReconNotionalLaunchEnergy : 0f;
                return est;
            }

            est.EnergyMinimum = 0f;
            est.EnergyDesired = est.EnergyMaximum =
                airPlausible ? AiConfigV2.airReconNotionalLaunchEnergy : 0f;
            float airApFloor = airPlausible ? AiConfigV2.airReconNotionalActivationAp : 0f;
            switch (target.Stealth)
            {
                case StealthRequirement.None:
                    est.ApMinimum = est.ApDesired = notionalActivationAp;
                    est.ApMaximum = Mathf.Max(notionalActivationAp, airApFloor);
                    break;
                case StealthRequirement.Preferred:
                    est.ApMinimum = est.ApDesired = notionalActivationAp;
                    est.ApMaximum = Mathf.Max(notionalActivationAp + stealthAp, airApFloor);
                    break;
                case StealthRequirement.Required:
                    est.ApMinimum = est.ApDesired = est.ApMaximum = notionalActivationAp + stealthAp;
                    break;
            }

            return est;
        }

        private static int CeilDiv(int a, int b) => AiV2Util.CeilDiv(a, b);
    }
}
