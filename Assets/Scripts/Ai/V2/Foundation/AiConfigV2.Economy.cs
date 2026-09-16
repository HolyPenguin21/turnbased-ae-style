namespace Game.Ai.V2
{
    // Part of AiConfigV2 (file-split Task 2, see Docs/ai-v2-file-split-refactor-tasks.md).
    // Economy standing.
    public static partial class AiConfigV2
    {
        // =======================================================================================
        //  ECONOMY STANDING
        // =======================================================================================
        // How many turns the AI is allowed to take to afford one "typical" wanted card when
        // sizing DeckResourceNeed into a per-turn target income (variant (a): derive the absolute
        // floor from what the deck actually costs, not a fixed 2/2/2/2).
        public const float economyDeckNeedHorizonTurns = 8f;
        public const float economyHandPaydownHorizonTurns = 2f;
        public const float economyOperationalPaydownHorizonTurns = 1f;
        public const float economyDeckNeedDiscount = 0.35f;
        public const float economyRunwayHorizonTurns = 3f;
        // 2026-09-15 — normalizes the turns-to-afford bottleneck (see EconomyStanding.
        // CalculateResource's incomeGap) into the same [0,1] scale the rest of the deficit
        // composite uses: turnsToAfford==this many turns already saturates incomeGap to 1.0.
        public const float economyBottleneckReferenceTurns = 10f;
        public const float economyIncomeGapWeight = 0.30f;
        public const float economyRelativeGapWeight = 0.20f;
        public const float economyRunwayGapWeight = 0.25f;
        public const float economyOperationalPressureWeight = 0.15f;
        public const float economyStarvationWeight = 0.10f;
        public const float economyDesireMaxWeight = 0.65f;
        public const float economyDesireMeanWeight = 0.35f;
        public const float economyLatentMultiplier = 0.25f;

        // A protected extraction hex may beat a modest raw-yield advantage farther outside the
        // support radius; resource priority is still decided separately in DemandLayer.

        // 2026-09-15 — recalibrated down from 2.5 (project owner's own target: a near-zero score
        // should mean a genuinely far corner of the map, not "anywhere more than a few hexes from
        // home" — even a site right up against the enemy citadel should stay clearly worth
        // building, e.g. for its aviation-range value). Kept for legacy/non-TaskScore consumers;
        // migrated world-task delivery uses TaskScoreEvaluator.Delivery instead.

        // Net-new-yield weight: site.HexYield is now IncomeProjection-derived marginal income
        // (raw hex yield minus whatever an existing building already collects there), not the
        // hex's raw total — a Base converting an already-productive site no longer double-counts
        // what it destroys AND what it "gains" (see StrategicCardEvaluator.ScoreBaseSite /
        // term for the same reason (it modeled the identical "is this hex already productive"
        // question with a flat 0.5/1.0 guess instead of the real marginal number).

        // Symmetric to economyBaseHexYieldValue: a Base converting an already-productive owned

        // 2026-09-15 — Base's OWN multiplier for deliveryApCost (extra activation-AP the walk
        // itself costs beyond the card's own play cost), decoupled from economyBuildApPenalty
        // (down from reusing that 4). Calibrated together with economySiteTravelPenalty(0.8) so a
        // site near this game's own observed map-edge distance (citadel-to-citadel ~11 hexes,
        // 108-hex map) nets close to zero, while a moderate/near-enemy distance (~5-6 hexes) stays
        // clearly positive — per the project owner's explicit target, not derived from a formula.
        // Extraction's own deliveryApCost (near the top of DemandLayer.Economy.cs) still uses
        // economyBuildApPenalty unchanged — a routine, usually-nearby investment, not recalibrated
        // this round.

        public const float economyExtractionMaxPaybackTurns = 8f;

        public const float economyBaseDemandMinValue = 12f;
        public const int economyBaseFoundScanRadius = 3;
        public const int economyBaseMinSpacing = 3;
        // 2026-09-15 — priority order per project owner: (1) most distinct resource types on the
        // hex, with genuine per-type deficit allowed to override that ordering (already how
        // hexYield's deficit-weighted sum behaves, no change needed there); (2) a single resource;
        // (3) a defense-only hex. A combo (resource + defense) should add on top of the resource
        // score but still lose to a purely better resource site. Weight kept below
        // defense bonus alone can add to a site's score but cannot out-rank an extra resource type.
        public const float economyBaseMaxDefenseModifier = 2f;   // normalizer — current terrain catalog's max defenseModifier

        public const float economyBaseUrgencyPerDeferredTurn = 12f;
        // 2026-09-15 round 18 — split from one shared constant into two, because the two decisions
        // it gated are not the same size of commitment. Pre-commitment staging (no mover moving,
        // nothing spent) must track whichever hex is genuinely best RIGHT NOW almost every turn —
        // a small anti-jitter margin only, so accumulating urgency (economyBaseUrgencyPerDeferredTurn,
        // clears economyBaseDemandMinValue in as little as 1-2 turns) always attaches to the current
        // best candidate rather than force-admitting a stale hex a fresh, clearly-better site has
        // already outclassed (observed in the wild: a staged hex value=-17.7 got built while a fresh
        // hex value=-8.77 sat un-staged the whole time — an 8.93 gap the old shared threshold=10
        // never cleared). Post-commitment release (a real mover already walking, resources already
        // reserved) keeps the old, larger margin — abandoning sunk travel/reservations for a merely
        // marginal improvement is real churn, not staging noise.
        public const float economyBaseStagingHysteresisThreshold = 3f;
        // Margin a rival Base site's Value must clear an ALREADY-COMMITTED active Base build's Value
        // by before StrategicPhaseA releases that commitment (mover mid-journey, resources reserved)
        // in favour of the rival. See economyBaseStagingHysteresisThreshold above for the
        // pre-commitment staging margin — no longer the same number.
        public const float economyBaseSwitchHysteresisThreshold = 10f;
        public const float economySameTurnCompletionBonus = 8f;
        public const float economyAdmissionCompletionCostWeight = 1f;
        // Actor-loan admission now consumes TaskScore.Value. Keep the public Economy policy names
        // as aliases so all existing consumers share the same migrated scale rather than mixing the
        public const float economyLoanHysteresisThreshold = taskScoreEconomyLoanHysteresisThreshold;
        public const float economyLoanContinuationLoss = taskScoreEconomyLoanContinuationLoss;
        // Bounded AP-equivalent penalty added to a home-vocation hero's TotalAssignmentApCost when
        // ranking economy builders (RankEconomyBuilders) — a small, real cost a large travel-cost
        // gap can still outweigh, not a hard preference that always wins ties regardless of
        // distance.
        public const float economyHomeHeroAssignmentApPenalty = 1.5f;
        // Blend weights for EconomicSecurity = w_abs*AbsFloor + w_rel*relTerm + w_bot*(1-Bottleneck).
        public const float economySecurityAbsWeight = 0.5f;
        public const float economySecurityRelWeight = 0.3f;
        public const float economySecurityBottleneckWeight = 0.2f;

    }
}
