using System.Globalization;
using UnityEngine;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  SCOUT OPTIONAL STEALTH POLICY  (Strategy V2 — Task Execution)
    // ===========================================================================================
    //  Required stealth already has its own hard path: provisioning RESERVES the transition AP and
    //  TaskExecutor enters stealth before the first step or aborts the mission. This is the
    //  separate, SOFT decision for a stealth-CAPABLE mover on a mission that did NOT require it —
    //  taken immediately before its first movement activation.
    //
    //  Optional AP is allowed to come only from execution SLACK: real AP minus every mandatory AP
    //  claim owned by this and later provisioned missions. It can never raid another mission's
    //  funded envelope. Within that slack, the policy compares honest route/end detection pressure
    //  against concrete later option value such as card draws. Bounded marginal comparison only.
    // ===========================================================================================
    public enum OptionalStealthDecision { Skip, Enter }

    public readonly struct OptionalStealthEvaluation
    {
        public readonly OptionalStealthDecision Decision;
        public readonly float Risk;
        public readonly float Protection;         // ThreatProtectionBenefit (spec §12)
        public readonly float RouteBenefit;      // RouteAccess + RouteShortening (spec §12)
        public readonly float ApOpportunity;
        public readonly string Explain;

        public OptionalStealthEvaluation(OptionalStealthDecision decision, float risk, float protection,
            float routeBenefit, float apOpportunity, string explain)
        {
            Decision = decision;
            Risk = risk;
            Protection = protection;
            RouteBenefit = routeBenefit;
            ApOpportunity = apOpportunity;
            Explain = explain;
        }

        public string ToCompact()
        {
            string N(float v) => v.ToString("0.00", CultureInfo.InvariantCulture);
            return $"risk {N(Risk)} protect {N(Protection)} route {N(RouteBenefit)} opportunity {N(ApOpportunity)} -> "
                 + (Decision == OptionalStealthDecision.Enter ? "ENTER" : "SKIP");
        }
    }

    public struct OptionalStealthInputs
    {
        public float LegDetectionRisk;      // max honest KNOWN risk before first activation
        public bool MoverAlreadyHidden;
        public bool MoverIsStrategicBody;
        public int ApRemaining;
        public int StealthApCost;

        // Mandatory AP already owned by this + later provisioned missions. Optional stealth may
        // touch only max(0, ApRemaining - MandatoryApClaims).
        public float MandatoryApClaims;

        public bool DrawAvailable;
        public int DrawApCost;
        // Upper bound on currently useful/legal draw count (hand/deck availability). The policy
        // further caps it by AP slack before/after stealth, so it notices loss of one draw even when
        // 4 AP -> 3 AP still leaves a different draw legal.
        public int DrawOpportunities;

        // Spec §12 — stealth is route topology, not only a defensive modifier. Both [0..1],
        // caller-supplied from the live map: RouteAccessBenefit is high when hiding unlocks an
        // otherwise-blocked next step (a neutral/enemy occupant a visible mover would engage);
        // RouteShorteningBenefit is high when a hidden corridor threads a cluster of occupied hexes
        // toward the objective.
        public float RouteAccessBenefit;
        public float RouteShorteningBenefit;
    }

    public static class ScoutOptionalStealthPolicy
    {
        // Optional stealth is currently used by Explore; Surveil routes are Required-stealth and
        // never reach this policy. Entering an unvisited frontier is therefore not "risk 0" merely
        // because no detector has been discovered yet: the whole purpose of the move is to reveal
        // information we do not have. This conservative baseline is deliberately below a known
        // detector's normalised risk and still has to beat the AP/draw opportunity-cost test below.
        // It makes a fresh stealth scout hide BEFORE its first blind step when 1 AP is genuinely
        // spare, instead of waiting until discovery has already activated the army and made
        // EnterStealth impossible for the rest of the turn.
        private const float UnknownFrontierRisk = 0.35f;

        public static OptionalStealthEvaluation Evaluate(in OptionalStealthInputs x)
        {
            float knownRisk = Mathf.Clamp01(x.LegDetectionRisk);
            float risk = Mathf.Max(knownRisk, UnknownFrontierRisk);
            float mandatory = Mathf.Max(0f, x.MandatoryApClaims);
            float slack = Mathf.Max(0f, x.ApRemaining - mandatory);

            float routeAccess = Mathf.Clamp01(x.RouteAccessBenefit);
            float routeShorten = Mathf.Clamp01(x.RouteShorteningBenefit);
            float routeBenefit = Mathf.Clamp01(
                AiConfigV2.scoutStealthRouteAccessWeight * routeAccess
                + AiConfigV2.scoutStealthRouteShorteningWeight * routeShorten);

            if (x.MoverAlreadyHidden)
                return new OptionalStealthEvaluation(OptionalStealthDecision.Skip, risk, 0f, routeBenefit, 0f, "already hidden");
            if (x.StealthApCost <= 0 || slack + AiConfigV2.allocatorSliceEpsilon < x.StealthApCost)
                return new OptionalStealthEvaluation(OptionalStealthDecision.Skip, risk, 0f, routeBenefit, 0f,
                    "no unclaimed AP slack for optional stealth");
            // Spec §12 — the leg-risk floor is a THREAT-protection gate only. Stealth may still be
            // worth entering purely for route access / shortening even with no known detector.
            if (risk < AiConfigV2.scoutOptionalStealthMinRisk && routeBenefit < AiConfigV2.scoutOptionalStealthEnterMargin)
                return new OptionalStealthEvaluation(OptionalStealthDecision.Skip, risk, 0f, routeBenefit, 0f,
                    "leg risk below floor and no route benefit");

            float protection = risk * AiConfigV2.scoutOptionalStealthProtectionScale
                * (x.MoverIsStrategicBody ? AiConfigV2.scoutOptionalStealthStrategicBodyFactor : 1f);
            protection = Mathf.Clamp01(protection);

            float apOpportunity = AiConfigV2.scoutOptionalStealthBaseApOpportunity;
            if (x.DrawAvailable && x.DrawApCost > 0 && x.DrawOpportunities > 0)
            {
                int before = Mathf.Min(x.DrawOpportunities, Mathf.FloorToInt(slack / x.DrawApCost));
                int after = Mathf.Min(x.DrawOpportunities,
                    Mathf.FloorToInt(Mathf.Max(0f, slack - x.StealthApCost) / x.DrawApCost));
                int lostDraws = Mathf.Max(0, before - after);
                apOpportunity += lostDraws * AiConfigV2.scoutOptionalStealthDrawOpportunity;
            }

            float totalBenefit = Mathf.Clamp01(protection + routeBenefit);
            OptionalStealthDecision decision =
                totalBenefit - apOpportunity >= AiConfigV2.scoutOptionalStealthEnterMargin
                    ? OptionalStealthDecision.Enter
                    : OptionalStealthDecision.Skip;

            string explain = $"knownRisk={knownRisk.ToString("0.00", CultureInfo.InvariantCulture)} "
                + $"effectiveRisk={risk.ToString("0.00", CultureInfo.InvariantCulture)} "
                + $"protect={protection.ToString("0.00", CultureInfo.InvariantCulture)} "
                + $"route={routeBenefit.ToString("0.00", CultureInfo.InvariantCulture)}(access={routeAccess.ToString("0.00", CultureInfo.InvariantCulture)},short={routeShorten.ToString("0.00", CultureInfo.InvariantCulture)}) "
                + $"apOpp={apOpportunity.ToString("0.00", CultureInfo.InvariantCulture)} "
                + $"slack={slack.ToString("0.##", CultureInfo.InvariantCulture)}";
            return new OptionalStealthEvaluation(decision, risk, protection, routeBenefit, apOpportunity, explain);
        }
    }
}
