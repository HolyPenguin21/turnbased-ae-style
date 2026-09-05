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
    //               A stealth-Required mission adds exactly 1 AP (scoutOptionalStealthAp) — the
    //               EnterStealth before the first risky step — UNLESS the mover is already hidden.
    //    * Energy — ArmyData.ActivationEnergyCost is non-zero ONLY for a real air army, so a
    //               ground solo-Recce is 0. Kept in the contract for AirRecon later.
    //
    //  MOVER ELIGIBILITY (mission.Stealth):
    //    None/Preferred — any fielded solo Recce.
    //    Required       — only a mover that is already hidden, OR can still slip into stealth
    //                     before its first move (CanEnterStealth && !HasActivatedThisTurn). A
    //                     visible, already-activated scout is NOT a valid executor (parity with
    //                     V1's hard exclusion). If none qualifies, MoverKnown = false and the
    //                     estimate is sized off a NOTIONAL capable scout — Provisioning (step 6)
    //                     either finds a real one or fails cleanly into the bounded re-allocate.
    //
    //  Distance is plain hex distance (no pathfinding yet — same first-pass ETA basis as the rest
    //  of V2); a live overload with a concrete ArmyData / real path lands with Provisioning.
    // ===========================================================================================
    public struct ScoutCostEstimate
    {
        public bool MoverKnown;
        public bool MoverAlreadyHidden;

        public float ApMinimum, ApDesired, ApMaximum;
        public float ActivationEnergy;
        public int EtaTurns;
        public float EstimatedDistance;
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

        // §6/§8 — Mission-stage estimation is actor-agnostic: it must size the MissionRequirements
        // envelope from POLICY constants and generic geometry only, never by enumerating or ranking
        // concrete movers (that is ReconAssignmentPlanner's job, strictly after Funding). MoverKnown
        // is therefore always false here — Assignment is what actually proves an executor exists —
        // and every AP/energy/ETA figure below is a notional-mover, worst-reasonable-case estimate
        // good enough to size a funding request. ReconAssignmentPlanner.EvaluateCandidate /
        // BuildCandidates refine the real figure once a concrete actor is bound; if the bound
        // actor's real cost exceeds what this estimate funded, ProvisioningManager's envelope check
        // already reports EnvelopeTooSmall (ProvisionDisposition.RepriceThisTurn) and
        // ResourceAllocator re-funds at the raised floor next pass — the existing repack loop, not a
        // second actor-aware estimator here.
        public static ScoutCostEstimate Estimate(WorldSnapshot snap, ScoutMissionTarget target)
        {
            var est = new ScoutCostEstimate { MoverKnown = false, MoverAlreadyHidden = false };
            float stealthAp = AiConfigV2.scoutOptionalStealthAp;
            float notionalActivationAp = AiConfigV2.scoutNotionalActivationAp;

            if (target.Kind == ScoutTargetKind.Surveil)
            {
                float req = notionalActivationAp
                    + (target.Stealth == StealthRequirement.None ? 0f : stealthAp);
                est.ApMinimum = est.ApDesired = est.ApMaximum = req;
                est.ActivationEnergy = 0;
                est.EstimatedDistance = 0f;
                est.EtaTurns = 0;
                return est;
            }

            // Generic route geometry: distance from the nearest own base to the target — a policy
            // heuristic, not a pathfind against any concrete mover's current position.
            int fleetBudget = snap?.Self?.Armies != null
                ? snap.Self.Armies.Select(a => a.MaxMovement).DefaultIfEmpty(0).Max() : 0;
            if (fleetBudget <= 0) fleetBudget = 1;

            int DistFrom(HexCoord h) => HexGridMath.Distance(h, target.FocusHex);
            HexCoord notionalFrom = snap?.Self?.BaseHexes != null && snap.Self.BaseHexes.Count > 0
                ? snap.Self.BaseHexes.OrderBy(DistFrom).First()
                : target.FocusHex;

            est.ActivationEnergy = 0;
            est.EstimatedDistance = DistFrom(notionalFrom);
            est.EtaTurns = Mathf.Max(1, CeilDiv((int)est.EstimatedDistance, fleetBudget));

            switch (target.Stealth)
            {
                case StealthRequirement.None:
                    est.ApMinimum = est.ApDesired = est.ApMaximum = notionalActivationAp;
                    break;
                case StealthRequirement.Preferred:
                    est.ApMinimum = est.ApDesired = notionalActivationAp;
                    est.ApMaximum = notionalActivationAp + stealthAp;
                    break;
                case StealthRequirement.Required:
                    // Generic estimate cannot know whether the eventual mover is already hidden;
                    // size the worst-reasonable (not-yet-hidden) case, refined by Assignment.
                    est.ApMinimum = est.ApDesired = est.ApMaximum = notionalActivationAp + stealthAp;
                    break;
            }

            return est;
        }

        private static int CeilDiv(int a, int b) => b <= 0 ? a : (a + b - 1) / b;
    }
}
