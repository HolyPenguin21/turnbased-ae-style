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
        public readonly float Protection;
        public readonly float ApOpportunity;
        public readonly string Explain;

        public OptionalStealthEvaluation(OptionalStealthDecision decision, float risk, float protection,
            float apOpportunity, string explain)
        {
            Decision = decision;
            Risk = risk;
            Protection = protection;
            ApOpportunity = apOpportunity;
            Explain = explain;
        }

        public string ToCompact()
        {
            string N(float v) => v.ToString("0.00", CultureInfo.InvariantCulture);
            return $"risk {N(Risk)} protect {N(Protection)} opportunity {N(ApOpportunity)} -> "
                 + (Decision == OptionalStealthDecision.Enter ? "ENTER" : "SKIP");
        }
    }

    public struct OptionalStealthInputs
    {
        public float LegDetectionRisk;      // max honest risk known before first activation
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
    }

    public static class ScoutOptionalStealthPolicy
    {
        public static OptionalStealthEvaluation Evaluate(in OptionalStealthInputs x)
        {
            float risk = Mathf.Clamp01(x.LegDetectionRisk);
            float mandatory = Mathf.Max(0f, x.MandatoryApClaims);
            float slack = Mathf.Max(0f, x.ApRemaining - mandatory);

            if (x.MoverAlreadyHidden)
                return new OptionalStealthEvaluation(OptionalStealthDecision.Skip, risk, 0f, 0f, "already hidden");
            if (x.StealthApCost <= 0 || slack + AiConfigV2.allocatorSliceEpsilon < x.StealthApCost)
                return new OptionalStealthEvaluation(OptionalStealthDecision.Skip, risk, 0f, 0f,
                    "no unclaimed AP slack for optional stealth");
            if (risk < AiConfigV2.scoutOptionalStealthMinRisk)
                return new OptionalStealthEvaluation(OptionalStealthDecision.Skip, risk, 0f, 0f, "leg risk below floor");

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

            OptionalStealthDecision decision =
                protection - apOpportunity >= AiConfigV2.scoutOptionalStealthEnterMargin
                    ? OptionalStealthDecision.Enter
                    : OptionalStealthDecision.Skip;

            string explain = $"risk={risk.ToString("0.00", CultureInfo.InvariantCulture)} "
                + $"protect={protection.ToString("0.00", CultureInfo.InvariantCulture)} "
                + $"apOpp={apOpportunity.ToString("0.00", CultureInfo.InvariantCulture)} "
                + $"slack={slack.ToString("0.##", CultureInfo.InvariantCulture)}";
            return new OptionalStealthEvaluation(decision, risk, protection, apOpportunity, explain);
        }
    }
}
