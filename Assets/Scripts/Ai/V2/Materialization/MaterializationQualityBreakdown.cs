using System.Globalization;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  MATERIALIZATION QUALITY BREAKDOWN  (Strategy V2 — Strategic Manager, Capability Quality)
    // ===========================================================================================
    //  Diagnostic-only decomposition of the capability-quality MULTIPLIER a chain earned, so the
    //  "why did Nora beat Ash Drifter" log line is legible. It is produced during evaluation /
    //  logging and never persisted or fed back into a decision — the single authoritative number
    //  is Final (the multiplier applied to ScorePlanA's base score).
    // ===========================================================================================
    public sealed class MaterializationQualityBreakdown
    {
        public float Mobility;                 // marginal value of moveMax at this dark-map / ETA setting
        public float Eta;                      // marginal value of reaching the focus a turn sooner
        public float VisionInfo;               // marginal value of Recce radius given local darkness
        public float SpotDetection;            // marginal value of Recce spot strength (detection context only)
        public float StealthUtility;           // contextual option / protection value of a stealth-capable body
        public float HeroOpportunityCost;      // negative — burning a scarce Hero as a solo Recce
        public float ActivationApDrag;         // negative — extra activation AP this body will cost the executor
        public float Final;                    // the clamped multiplier actually applied

        public static MaterializationQualityBreakdown Neutral() =>
            new MaterializationQualityBreakdown { Final = 1f };

        public string ToCompact()
        {
            string F(float v) => v.ToString("+0.00;-0.00;0.00", CultureInfo.InvariantCulture);
            return $"move {F(Mobility)} eta {F(Eta)} vision {F(VisionInfo)} spot {F(SpotDetection)} "
                 + $"stealth {F(StealthUtility)} heroOpp {F(HeroOpportunityCost)} actAp {F(ActivationApDrag)} "
                 + $"= x{Final.ToString("0.00", CultureInfo.InvariantCulture)}";
        }
    }
}
