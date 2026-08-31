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
    //  taken immediately before a potentially risky movement leg.
    //
    //  Honest inputs only (no TrueWorld): the detection risk of the leg (known enemies that could
    //  roll a stealth challenge on the next hex), whether the mover is already hidden, the AP that
    //  actually remains, the AP the transition costs, and whether spending it would destroy an
    //  otherwise-legal later action the AI already knows about (a terminal draw). Bounded marginal
    //  comparison — never a turn-wide planner.
    // ===========================================================================================
    public enum OptionalStealthDecision { Skip, Enter }

    public readonly struct OptionalStealthEvaluation
    {
        public readonly OptionalStealthDecision Decision;
        public readonly float Risk;            // detection risk of the leg, [0..1]
        public readonly float Protection;      // expected protective value of hiding here, [0..1]-ish
        public readonly float ApOpportunity;   // value of the AP the transition would consume
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
        public float LegDetectionRisk;      // [0..1] honest risk of the next movement leg
        public bool MoverAlreadyHidden;
        public bool MoverIsStrategicBody;   // hero / otherwise expensive-to-lose scout -> protection worth more
        public int ApRemaining;
        public int StealthApCost;           // AP the EnterStealth transition costs (scoutOptionalStealthAp)

        // The one concrete later action the AI already knows it could still take with this AP.
        public bool DrawAvailable;          // free hand slot AND deck non-empty
        public int DrawApCost;
    }

    public static class ScoutOptionalStealthPolicy
    {
        public static OptionalStealthEvaluation Evaluate(in OptionalStealthInputs x)
        {
            float risk = Mathf.Clamp01(x.LegDetectionRisk);

            if (x.MoverAlreadyHidden)
                return new OptionalStealthEvaluation(OptionalStealthDecision.Skip, risk, 0f, 0f, "already hidden");
            if (x.StealthApCost <= 0 || x.ApRemaining < x.StealthApCost)
                return new OptionalStealthEvaluation(OptionalStealthDecision.Skip, risk, 0f, 0f, "stealth AP unaffordable");
            if (risk < AiConfigV2.scoutOptionalStealthMinRisk)
                return new OptionalStealthEvaluation(OptionalStealthDecision.Skip, risk, 0f, 0f, "leg risk below floor");

            float protection = risk * AiConfigV2.scoutOptionalStealthProtectionScale
                * (x.MoverIsStrategicBody ? AiConfigV2.scoutOptionalStealthStrategicBodyFactor : 1f);
            protection = Mathf.Clamp01(protection);

            // AP opportunity cost: only material when the spend would drop a currently-legal draw.
            float apOpportunity = AiConfigV2.scoutOptionalStealthBaseApOpportunity;
            if (x.DrawAvailable && x.DrawApCost > 0
                && x.ApRemaining >= x.DrawApCost
                && x.ApRemaining - x.StealthApCost < x.DrawApCost)
                apOpportunity += AiConfigV2.scoutOptionalStealthDrawOpportunity;

            OptionalStealthDecision decision =
                protection - apOpportunity >= AiConfigV2.scoutOptionalStealthEnterMargin
                    ? OptionalStealthDecision.Enter
                    : OptionalStealthDecision.Skip;

            string explain = $"risk={risk.ToString("0.00", CultureInfo.InvariantCulture)} "
                + $"protect={protection.ToString("0.00", CultureInfo.InvariantCulture)} "
                + $"apOpp={apOpportunity.ToString("0.00", CultureInfo.InvariantCulture)}";
            return new OptionalStealthEvaluation(decision, risk, protection, apOpportunity, explain);
        }
    }
}
