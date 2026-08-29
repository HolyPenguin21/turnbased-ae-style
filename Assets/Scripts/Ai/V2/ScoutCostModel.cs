using System.Linq;
using Game.HexGrid;
using UnityEngine;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  SCOUT COST MODEL  (Strategy V2 build-order step 4 — the shared Scout estimator)
    // ===========================================================================================
    //  "ONE ESTIMATOR, MANY STAGES" for the cheap mission type, the same rule
    //  CombatOpportunityAnalyzer enforces for raids. Both the step-4 MissionRequirements and the
    //  step-6 ProvisioningManager feasibility check call THIS, so the allocator can never fund a
    //  Scout the provisioner then can't pay for.
    //
    //  WHAT A GROUND SCOUT ACTUALLY COSTS (game rules, not tunables):
    //    * AP     — only to ACTIVATE the mover (ArmyData.ActivationApCost). Travelling across
    //               hexes spends MOVEMENT, never AP; an already-activated army costs 0 AP to move.
    //    * Energy — ArmyData.ActivationEnergyCost is non-zero ONLY for a real air army, so a
    //               ground solo-Recce is 0. Kept in the contract so AirRecon (a different
    //               provisioning profile) can fill it later without reshaping anything.
    //    * Stealth— a separate opt-in 1 AP (AiConfigV2.scoutOptionalStealthAp), and only when the
    //               route runs near a known non-neutral force. Reported as the gap between
    //               ApDesired and ApMaximum, never required.
    //
    //  SNAPSHOT TIER: picks the cheapest eligible mover already on the map (a solo Recce). If
    //  there is none, it sizes a NOTIONAL cheap mover (scoutNotionalActivationAp / 0 energy) and
    //  sets MoverKnown = false — the proposal still forms and Provisioning (step 6) either finds a
    //  real mover or fails cleanly into the bounded re-allocate loop. Distance is plain hex
    //  distance (no pathfinding yet — same first-pass ETA basis as everywhere else in V2); a live
    //  overload with a concrete ArmyData / real path lands with Provisioning.
    // ===========================================================================================
    public struct ScoutCostEstimate
    {
        public bool MoverKnown;
        public float ActivationAp;
        public float ActivationEnergy;
        public float OptionalStealthAp;
        public int EtaTurns;
        public float EstimatedDistance;
    }

    public static class ScoutCostModel
    {
        public static ScoutCostEstimate Estimate(WorldSnapshot snap, ScoutMissionTarget target)
        {
            var est = new ScoutCostEstimate();
            if (snap?.Self == null)
            {
                est.MoverKnown = false;
                est.ActivationAp = AiConfigV2.scoutNotionalActivationAp;
                est.EtaTurns = 1;
                return est;
            }

            // ---- cheapest eligible mover already fielded (a dedicated solo scout) ----
            ArmySnapshot mover = snap.Self.Armies
                .Where(a => a != null && a.IsSoloRecce && !a.IsPrison && !a.IsAir && a.MemberCount > 0)
                .OrderBy(a => a.ActivationApCost)
                .ThenByDescending(a => a.CurrentMovement)
                .FirstOrDefault();

            // ---- move budget: this mover's, else our fastest army's, else 1 ----
            int moveBudget = mover != null && mover.MaxMovement > 0 ? mover.MaxMovement : 0;
            if (moveBudget <= 0)
                moveBudget = snap.Self.Armies.Select(a => a.MaxMovement).DefaultIfEmpty(0).Max();
            if (moveBudget <= 0)
                moveBudget = 1;

            // ---- distance from the mover (or the nearest base if notional) to the focus hex ----
            HexCoord from = mover != null
                ? mover.Hex
                : (snap.Self.BaseHexes != null && snap.Self.BaseHexes.Count > 0
                    ? snap.Self.BaseHexes.OrderBy(b => HexGridMath.Distance(b, target.FocusHex)).First()
                    : target.FocusHex);
            int dist = HexGridMath.Distance(from, target.FocusHex);
            est.EstimatedDistance = dist;
            est.EtaTurns = Mathf.Max(1, CeilDiv(dist, moveBudget));

            if (mover != null)
            {
                est.MoverKnown = true;
                est.ActivationAp = mover.HasActivatedThisTurn ? 0 : mover.ActivationApCost;
                est.ActivationEnergy = mover.HasActivatedThisTurn ? 0 : mover.ActivationEnergyCost;
            }
            else
            {
                est.MoverKnown = false;
                est.ActivationAp = AiConfigV2.scoutNotionalActivationAp;
                est.ActivationEnergy = 0; // ground Scout contract
            }

            est.OptionalStealthAp = RouteHasDetectionRisk(snap, target.FocusHex)
                ? AiConfigV2.scoutOptionalStealthAp
                : 0f;

            return est;
        }

        // A known non-neutral force sitting within the same avoid radius the frontier scan uses —
        // enough that slipping into stealth first is worth an AP. Honest sightings only.
        private static bool RouteHasDetectionRisk(WorldSnapshot snap, HexCoord focus)
        {
            var sightings = snap.Known?.EnemySightings;
            if (sightings == null) return false;
            foreach (var s in sightings)
                if (HexGridMath.Distance(s.Hex, focus) <= AiConfigV2.frontierEnemyAvoidRadius)
                    return true;
            return false;
        }

        private static int CeilDiv(int a, int b) => b <= 0 ? a : (a + b - 1) / b;
    }
}
