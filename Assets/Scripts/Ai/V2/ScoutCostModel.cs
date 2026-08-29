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
        public float ActivationEnergy;      // -> Energy minimum == desired == maximum
        public int EtaTurns;
        public float EstimatedDistance;
    }

    // Cost of ONE concrete (mover, executionHex) pair — the step-6 provision-stage number, used
    // for both Explore (executionHex == FocusHex) and Surveil (executionHex == chosen vantage).
    // Travel is MP, never AP; ETA/Distance are toward the executionHex, NOT toward a Surveil
    // FocusHex the scout never steps on.
    public struct ScoutPairCost
    {
        public int EffActivationAp;
        public float RequiredAp;    // EffActivationAp + stealth transition when Required && !hidden
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

        public static ScoutCostEstimate Estimate(WorldSnapshot snap, ScoutMissionTarget target)
        {
            var est = new ScoutCostEstimate();

            // SURVEIL (build-order step 6b) — proposal stage sizes MONEY ONLY. The spatial cost is
            // unknowable here: ExecutionHex (the vantage) does not exist until a concrete mover is
            // assigned in provisioning. EstimatedDistance / EtaTurns = 0 means "not spatially
            // estimated", NOT "already on site"; nothing may use them for commitment before step 7.
            // The AP envelope is still real: Surveil is always stealth-Required, so it is
            // activation + (1 unless a hidden mover exists), taken as the MINIMUM over the actually
            // eligible scouts (Rank's stealth-AP ordering would otherwise pick a non-optimal one).
            if (target.Kind == ScoutTargetKind.Surveil)
            {
                float stealthAp0 = AiConfigV2.scoutOptionalStealthAp;
                var eligible = ScoutMoverSelector.Eligible(snap, target, null);
                if (eligible.Count > 0)
                {
                    est.MoverKnown = true;
                    est.MoverAlreadyHidden = eligible.Any(a => a.IsHidden);
                    float minAp = eligible.Min(a =>
                        (a.HasActivatedThisTurn ? 0 : a.ActivationApCost) + (a.IsHidden ? 0f : stealthAp0));
                    est.ApMinimum = est.ApDesired = est.ApMaximum = minAp;
                }
                else
                {
                    est.MoverKnown = false;
                    est.ApMinimum = est.ApDesired = est.ApMaximum = AiConfigV2.scoutNotionalActivationAp + stealthAp0;
                }
                est.ActivationEnergy = 0;
                est.EstimatedDistance = 0f;
                est.EtaTurns = 0;
                return est;
            }

            if (snap?.Self == null)
            {
                est.MoverKnown = false;
                float notional = AiConfigV2.scoutNotionalActivationAp
                    + (target.Stealth == StealthRequirement.Required ? AiConfigV2.scoutOptionalStealthAp : 0);
                est.ApMinimum = est.ApDesired = est.ApMaximum = notional;
                est.EtaTurns = 1;
                return est;
            }

            // Mover selection is the shared ScoutMoverSelector (step 6) — the estimate takes the
            // top-ranked candidate; ProvisioningManager takes the whole list and assigns across
            // missions. No exclusions at estimate time (no mover is claimed yet this cycle).
            var ranked = ScoutMoverSelector.Rank(snap, target, null);

            int fleetBudget = snap.Self.Armies.Select(a => a.MaxMovement).DefaultIfEmpty(0).Max();
            if (fleetBudget <= 0) fleetBudget = 1;

            int DistFrom(HexCoord h) => HexGridMath.Distance(h, target.FocusHex);
            HexCoord notionalFrom = snap.Self.BaseHexes != null && snap.Self.BaseHexes.Count > 0
                ? snap.Self.BaseHexes.OrderBy(DistFrom).First()
                : target.FocusHex;

            float activationAp;
            if (ranked.Count > 0)
            {
                ScoutMoverCandidate top = ranked[0];
                est.MoverKnown = true;
                est.MoverAlreadyHidden = top.AlreadyHidden;
                activationAp = top.EffActivationAp;
                est.ActivationEnergy = top.Army.HasActivatedThisTurn ? 0 : top.Army.ActivationEnergyCost;
                est.EstimatedDistance = top.Distance;
                est.EtaTurns = top.EtaTurns;
            }
            else
            {
                est.MoverKnown = false;
                est.MoverAlreadyHidden = false;
                activationAp = AiConfigV2.scoutNotionalActivationAp;
                est.ActivationEnergy = 0; // ground Scout contract
                est.EstimatedDistance = DistFrom(notionalFrom);
                est.EtaTurns = Mathf.Max(1, CeilDiv((int)est.EstimatedDistance, fleetBudget));
            }

            // ---- AP envelope by stealth requirement ----
            float stealthAp = AiConfigV2.scoutOptionalStealthAp;
            switch (target.Stealth)
            {
                case StealthRequirement.None:
                    est.ApMinimum = est.ApDesired = est.ApMaximum = activationAp;
                    break;
                case StealthRequirement.Preferred:
                    est.ApMinimum = est.ApDesired = activationAp;
                    est.ApMaximum = activationAp + stealthAp;
                    break;
                case StealthRequirement.Required:
                    // Already-hidden mover satisfies Required for free; anyone else (real eligible
                    // mover, or the notional capable scout) must pay the 1 AP to hide first.
                    float req = activationAp + (est.MoverAlreadyHidden ? 0f : stealthAp);
                    est.ApMinimum = est.ApDesired = est.ApMaximum = req;
                    break;
            }

            return est;
        }

        private static int CeilDiv(int a, int b) => b <= 0 ? a : (a + b - 1) / b;
    }
}
