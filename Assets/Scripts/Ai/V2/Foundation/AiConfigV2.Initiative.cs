namespace Game.Ai.V2
{
    // Part of AiConfigV2 (file-split Task 2, see Docs/ai-v2-file-split-refactor-tasks.md).
    // Adaptive initiative investment.
    public static partial class AiConfigV2
    {
        // =======================================================================================
        //  ADAPTIVE INITIATIVE INVESTMENT  (pre-budget, one-way isolated — Game.Ai.V2.Initiative)
        //  Prices excess Human/Energy/Materials/Tech into extra initiative dice ONLY when the AI
        //  has enough real AP workload to justify the cost. NOT a DesireAxis / mission / demand /
        //  StrategicManager action; its output never re-enters the strategic pipeline. The dice
        //  count, price ladder and AP-by-rank live in Game.Turns.InitiativeRules, NOT here.
        //  All first-pass, meant to be tuned against real AiDebug.log runs.
        // =======================================================================================
        // Per-player initiative AP-telemetry ring buffer + how it reads "starvation" vs "idle".
        public const int initiativeHistoryMaxSamples = 8;
        public const int initiativeStarvationApThreshold = 1;   // EndAp <= this + work still queued => needed more AP
        public const float initiativeWasteLeftoverFrac = 0.35f; // EndAp/StartAp >= this + nothing to do => wasted AP

        // CurrentApPressure — structural workload only (no mission planners consulted).
        public const float initiativeApPressureArmyFull = 4f;    // this many separate field armies -> army term 1
        public const float initiativeApPressurePowerFull = 40f;  // this much EffectiveArmyPower -> power term 1
        public const float initiativeApPressureCardsFull = 4f;   // this many AP-costing hand cards -> card term 1
        public const float initiativeApPressureWeightArmies = 0.40f;
        public const float initiativeApPressureWeightPower = 0.35f;
        public const float initiativeApPressureWeightCards = 0.25f;

        // ApPressure = w_cur*CurrentApPressure + w_hist*HistoricalApPressure (both [0..1]).
        public const float initiativeApPressureCurrentWeight = 0.55f;
        public const float initiativeApPressureHistoryWeight = 0.45f;
        // TurnOrderPressure = CurrentApPressure * this — tempo stays a discounted secondary benefit.
        public const float initiativeTurnOrderPressureScale = 0.60f;

        // Marginal resource opportunity-cost model (PreTurnCapacityAnalysis.MarginalCostAt).
        public const float initiativeDeckDemandWeight = 0.50f;   // fraction of remaining-deck appetite that counts as "future demand"
        public const float initiativeIncomeHorizonTurns = 6f;    // income * this folded into effective supply
        public const float initiativeCostAtParity = 1.0f;        // one unit costs this when supply == demand
        public const float initiativeCoverageFloor = 0.25f;      // clamp on supply/demand ratio (scarce)
        public const float initiativeCoverageCeil = 4f;          // clamp on supply/demand ratio (abundant)
        public const int initiativeLowStockUnits = 3;            // draining at/under this many units adds a steep penalty
        public const float initiativeLowStockPenalty = 2.0f;

        // Value model — converts expected-AP / earliness gains into resource-comparable units.
        public const float initiativeApBenefitPerExpectedAp = 4.0f;   // one expected AP is worth ~this many resource units at full pressure
        public const float initiativeTempoBenefitPerEarliness = 3.0f; // full [0..1] earliness swing is worth ~this at full tempo pressure
        public const float initiativeNetValueEpsilon = 0.01f;         // candidates within this net value are "effectively equal"

    }
}
