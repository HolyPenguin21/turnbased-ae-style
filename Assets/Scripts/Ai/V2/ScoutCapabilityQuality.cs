using UnityEngine;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  SCOUT CAPABILITY QUALITY  (Strategy V2 — Strategic Manager, Capability Quality Model)
    // ===========================================================================================
    //  The pure, capability-SPECIFIC evaluator for CapabilityKind.ScoutCapability. Given the
    //  projected gameplay characteristics of the body a materialization chain would put on the map
    //  (moveMax, Recce radius, Recce spot strength, stealth, hero, activation AP) and the mission
    //  CONTEXT the demand was raised for (ScoutCapabilityContext + current Hero scarcity), it
    //  returns a bounded QUALITY MULTIPLIER applied to MaterializationCandidateBuilder.ScorePlanA's
    //  cost/fit base score. Every term is a MARGINAL value around 0 — "how much more useful is
    //  this than the cheapest feasible alternative, HERE" — never an unconditional raw-stat bonus
    //  (spec AC2 / AC3 / AC4 / AC8).
    //
    //  Nothing in here reads TrueWorld, live ArmyData, or a concrete card name. It is exercised
    //  directly by Tools/capability-quality-sim.
    // ===========================================================================================
    internal static class ScoutCapabilityQuality
    {
        public struct Inputs
        {
            public int MoveMax;
            public int RecceRadius;
            public int SpotStrength;
            public bool HasStealth;
            public bool IsHero;
            public int ActivationApCost;

            // Deploy hex -> focus hex plain hex distance (same first-pass ETA basis as ScoutCostModel).
            public int DistanceToFocus;
            public bool HasFocus;

            // The cheapest feasible alternative's mobility, for the MARGINAL mobility / ETA compare.
            // When this candidate is the only feasible one, pass its own MoveMax => mobility term 0.
            public int ReferenceMoveMax;

            public ScoutCapabilityContext Context;   // may be null (no mission context propagated)

            // Current Hero supply (CapabilityInventory) — the opportunity-cost signal for spending
            // a Hero card as a solo Recce. No second scarcity model.
            public int AvailableHeroes;
            public int CommittedHeroes;
        }

        public static float Evaluate(in Inputs x, out MaterializationQualityBreakdown bd)
        {
            bd = new MaterializationQualityBreakdown();

            float darkFrac = x.Context != null ? Mathf.Clamp01(x.Context.ExplorableUnknownFrac) : 0.5f;
            int refMove = Mathf.Max(1, x.ReferenceMoveMax > 0 ? x.ReferenceMoveMax : x.MoveMax);
            int move = Mathf.Max(1, x.MoveMax);

            // ---- ETA: turns saved reaching the focus vs the cheapest alternative ----
            int refEta = 1;
            if (x.HasFocus)
            {
                int eta = Eta(x.DistanceToFocus, move);
                refEta = Eta(x.DistanceToFocus, refMove);
                bd.Eta = Mathf.Max(0, refEta - eta) * AiConfigV2.scoutQualityMobilityEtaWeight;
            }

            // ---- mobility: raw movement headroom. Only worth something while the map is dark,
            //      and — when the baseline mover already reaches the focus this turn — only for
            //      the exploration FOLLOW-THROUGH it buys, not for reaching the focus itself. ----
            float followThrough = (x.HasFocus && refEta <= 1)
                ? AiConfigV2.scoutQualityMobilityFollowThroughFactor : 1f;
            bd.Mobility = Mathf.Max(0, move - refMove)
                        * AiConfigV2.scoutQualityMobilityWeight
                        * (0.25f + 0.75f * darkFrac)
                        * followThrough;

            // ---- vision: extra Recce radius, worth something only where it can reveal more ----
            int radiusOver = Mathf.Max(0, x.RecceRadius - 1);
            if (radiusOver > 0)
            {
                float localOpen = x.Context != null
                    ? Mathf.Clamp01(x.Context.FocusFreshNeighbors / Mathf.Max(0.0001f, (float)AiConfigV2.scoutInfoGainNorm))
                    : 0.5f;
                float infoRoom = Mathf.Max(localOpen, darkFrac * 0.7f);
                bd.VisionInfo = radiusOver * AiConfigV2.scoutQualityVisionWeight * (0.15f + 0.85f * infoRoom);
            }

            // ---- spot strength: only a real Scout-quality term in a detection / surveillance context ----
            if (x.SpotStrength > 0)
            {
                float spotNorm = Mathf.Clamp01(x.SpotStrength / Mathf.Max(0.0001f, (float)AiConfigV2.scoutQualitySpotNorm));
                bool detection = x.Context != null && x.Context.DetectionRelevant;
                float ctxFactor = detection
                    ? 0.55f + 0.6f * Mathf.Clamp01(x.Context.DetectionRisk)
                    : AiConfigV2.scoutQualitySpotIrrelevantFactor;
                bd.SpotDetection = spotNorm * AiConfigV2.scoutQualitySpotWeight * ctxFactor;
            }

            // ---- stealth (Preferred, not Required): contextual option + protection value ----
            if (x.HasStealth)
            {
                float risk = x.Context != null ? Mathf.Clamp01(x.Context.DetectionRisk) : 0f;
                float optionVal = AiConfigV2.scoutQualityStealthOptionValue * (0.3f + 0.7f * darkFrac);
                float protectVal = risk * AiConfigV2.scoutQualityStealthRiskValue;
                bd.StealthUtility = optionVal + protectVal;
            }

            // ---- hero opportunity cost: a Hero played as a solo Recce is a Hero NOT leading an army ----
            if (x.IsHero)
            {
                float scarcity;
                if (x.AvailableHeroes >= AiConfigV2.scoutQualityHeroAbundantAt)
                    scarcity = 0f;
                else if (x.AvailableHeroes <= AiConfigV2.scoutQualityHeroScarceAt)
                    scarcity = x.CommittedHeroes > 0 ? 1f : 0.7f;
                else
                    scarcity = 0.4f;
                bd.HeroOpportunityCost = -scarcity * AiConfigV2.scoutQualityHeroOppCostMax;
            }

            // ---- activation-AP drag: the follow-up cost costFactor (whole-chain deploy AP) misses ----
            bd.ActivationApDrag = -Mathf.Max(0, x.ActivationApCost - 1) * AiConfigV2.scoutQualityActivationApWeight;

            float sum = bd.Mobility + bd.Eta + bd.VisionInfo + bd.SpotDetection + bd.StealthUtility
                      + bd.HeroOpportunityCost + bd.ActivationApDrag;
            bd.Final = Mathf.Clamp(1f + sum,
                AiConfigV2.scoutQualityMultiplierMin, AiConfigV2.scoutQualityMultiplierMax);
            return bd.Final;
        }

        private static int Eta(int distance, int moveBudget)
        {
            if (distance <= 0)
                return 1;
            int b = Mathf.Max(1, moveBudget);
            return Mathf.Max(1, (distance + b - 1) / b);
        }
    }
}
