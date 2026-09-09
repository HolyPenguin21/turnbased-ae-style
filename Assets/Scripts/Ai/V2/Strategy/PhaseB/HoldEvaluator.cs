using Game.Cards;
using Game.Economy;
using Game.Map;
using UnityEngine;

namespace Game.Ai.V2
{
    // ARCH-02 §8/§41 — the canonical owner of the "value of NOT spending persistent resources"
    // policy. Extracted verbatim from StrategicManager. Hold is a real terminal tempo alternative,
    // but a persistent-resource hold never blocks a compatible AP-only action (Draw / AP-only
    // pressure) — the arbiter enforces that by calling this only for non-card spends.
    internal static class HoldEvaluator
    {
        // §P0 (round 4) — the H/E/M/T retention/opportunity-cost policy. NOT a global stop gate.
        // For a concrete non-card spend, `onlyConsumed` is priced by StrategicCardEvaluator's
        // canonical exact-vector resource model. Passing null retains the whole-pool scarcity
        // indicator below, used only for the diagnostic line.
        //   base = (fragility*fragilityWeight + scarcity*scarcityWeight) * scale
        //          where scarcity = 1 - min over the in-scope resources of (stock / comfortable)
        //   - Σ STRATEGIC OVERSTOCK relief: the game has NO hard resource cap so nothing is
        //     physically lost; a resource far above its runway need (runwayTarget = max(comfortable,
        //     IncomeTarget[r] * overstockRunwayHorizon)) is just worth less to hoard. overstock =
        //     max(0, (stock + PerTurnIncome[r]) - runwayTarget); summed, floored at 0 per resource.
        internal static float HoldResourcesUtility(PlayerRoot root, WorldSnapshot snap,
            ResourceCost onlyConsumed = null, PlayerSetupData player = null, AiTurnContext ctx = null)
        {
            if (root == null)
                return 0f;

            // An actual spend must be priced in the SAME marginal opportunity-cost space as card
            // resources. The former min-stock formula valued the entire scarcest pool regardless
            // of whether the action consumed one or ten units; a [H1 E1 M1 T1] capacity tier was
            // therefore charged 0.9..1.7 utility while four card-resource units cost about 0.04..
            // 0.36 in StrategicCardEvaluator. This made the dependency permanently lose before
            // execution. Keep the whole-pool policy below for diagnostics, but use the canonical
            // exact-vector scorer whenever a concrete cost is supplied.
            if (onlyConsumed != null)
                return Mathf.Max(0f, StrategicCardEvaluator.StrategicResourceCostValue(
                    onlyConsumed, snap,
                    player != null && ctx != null
                        ? (System.Func<ResourceType, float>)(type =>
                            StrategicSpendability.SpendableAmount(player, root, ctx, type))
                        : null,
                    player));

            float eco = snap?.Economy != null ? Mathf.Clamp01(snap.Economy.EconomicSecurity) : 0.5f;
            float fragility = 1f - eco;
            float comfortable = Mathf.Max(1f, AiConfigV2.tempoHoldResourceComfortableStock);

            float minStockNorm = 1f;
            float overstockRelief = 0f;
            bool anyInScope = false;
            foreach (ResourceType rt in ResourceBundle.All)
            {
                if (onlyConsumed != null && onlyConsumed.Get(rt) <= 0)
                    continue;
                anyInScope = true;
                int stock = root.GetResource(rt);
                minStockNorm = Mathf.Min(minStockNorm, Mathf.Clamp01(stock / comfortable));

                float incomeTarget = snap?.Economy?.IncomeTarget.Get(rt) ?? 0f;
                float nextIncome = snap?.Self != null ? snap.Self.PerTurnIncome.Get(rt) : 0f;
                float runwayTarget = Mathf.Max(comfortable, incomeTarget * AiConfigV2.tempoHoldOverstockRunwayHorizon);
                float overstock = Mathf.Max(0f, (stock + nextIncome) - runwayTarget);
                overstockRelief += overstock * AiConfigV2.tempoHoldOverstockReliefWeight;
            }
            if (!anyInScope)
                return 0f;

            float scarcity = 1f - minStockNorm;
            float u = (fragility * AiConfigV2.tempoHoldFragilityWeight
                       + scarcity * AiConfigV2.tempoHoldScarcityWeight)
                      * AiConfigV2.tempoHoldPersistentResourceValueScale;
            u -= Mathf.Min(AiConfigV2.tempoHoldOverstockReliefCap, overstockRelief);
            return Mathf.Clamp(u,
                -AiConfigV2.tempoHoldOverstockReliefCap, AiConfigV2.tempoHoldPersistentResourceValueCap);
        }
    }
}
