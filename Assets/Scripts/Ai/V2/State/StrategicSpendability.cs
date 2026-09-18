using Game.Cards;
using Game.Economy;
using Game.Map;
using Game.Players;
using UnityEngine;

namespace Game.Ai.V2
{
    // ARCH-02 §45/§47 — the ONE owner-aware strategic-spendability seam. Every "can I afford this
    // persistent-resource cost right now" question in the strategic + materialization + reaction
    // paths goes through SpendableAmount, which nets BOTH:
    //   · StrategicResourceReservationLedger  — the owner-aware explicit reservations (e.g. a
    //     bounded reaction envelope), optionally excluding the caller's own owner key; and
    //   · Game.Ai.AiResourceReservation       — the legacy recon-air protected pool.
    // Before this fix ReservesOkAfterChain and MaterializationCandidateBuilder.ChainResourcesAffordable
    // and MaterializationPortfolioSolver's resPool each consulted ONLY AiResourceReservation, so the
    // owner-aware ledger was not authoritative for materialization feasibility.
    public static class StrategicSpendability
    {
        // The canonical primitive: how much of resource `t` may actually be spent this turn.
        internal static float SpendableAmount(PlayerSetupData player, PlayerRoot root, AiTurnContext ctx,
            ResourceType t, string excludeOwner = null)
        {
            if (root == null)
                return 0f;
            float legacy = Mathf.Max(0f, Game.Ai.AiResourceReservation.Available(root, player, t));
            if (player == null || ctx == null)
                return legacy;
            StrategicReservedResource srr = StrategicResourceReservationLedger.Map(t);
            float strategic = excludeOwner == null
                ? StrategicResourceReservationLedger.Spendable(player, ctx.TurnNumber, srr, root.GetResource(t))
                : StrategicResourceReservationLedger.SpendableExcludingOwner(
                    player, ctx.TurnNumber, srr, root.GetResource(t), excludeOwner);
            return Mathf.Min(strategic, legacy);
        }

        // AP reservations are turn-local and live in the existing owner-aware ledger. Materialization
        // is NOT the owner of an Economy completion/reaction reservation, so it must not exclude it.
        // No separate AP wallet: without a player/turn context the physical pool is the fallback.
        internal static float SpendableActionPoints(PlayerSetupData player, PlayerRoot root,
            AiTurnContext ctx)
        {
            if (root == null)
                return 0f;
            return player == null || ctx == null ? root.ActionPoints
                : StrategicResourceReservationLedger.SpendableAp(
                    player, ctx.TurnNumber, root.ActionPoints);
        }

        // spec §6 — a spend candidate must fit SPENDABLE persistent resources, not just raw stock.
        // round 6/7 (P1) — `excludeOwner` drops the caller's OWN reservation (by its EXACT Owner
        // key, not the shared Reason) so a re-probe of the reaction that placed a hold does not fail
        // against itself and two owners sharing a Reason can't shadow each other's revalidation.
        internal static bool FitsSpendableResources(PlayerSetupData player, PlayerRoot root,
            AiTurnContext ctx, ResourceCost cost, string excludeOwner = null)
        {
            if (cost == null)
                return true;
            foreach (ResourceType t in ResourceBundle.All)
            {
                int need = cost.Get(t);
                if (need <= 0)
                    continue;
                if (SpendableAmount(player, root, ctx, t, excludeOwner) + AiConfigV2.allocatorSliceEpsilon < need)
                    return false;
            }
            return true;
        }

        // Physical guard for a whole materialization chain: AP and every persistent resource in
        // the chain's canonical cost must fit the owner-aware SPENDABLE pool. Raw ActionPoints
        // alone would let a card consume AP held for an accepted Economy build completion.
        internal static bool ReservesOkAfterChain(PlayerRoot root, AiTurnContext ctx,
            MaterializationPlan plan, PlayerSetupData player = null)
        {
            if (root == null || plan == null)
                return false;
            if (SpendableActionPoints(player, root, ctx) + AiConfigV2.allocatorSliceEpsilon < plan.ApCost)
                return false;

            ResourceCost cost = plan.ResCost;
            if (cost == null)
                return true;

            foreach (ResourceType t in ResourceBundle.All)
                if (SpendableAmount(player, root, ctx, t) < Mathf.Max(0, cost.Get(t)))
                    return false;
            return true;
        }
    }
}
