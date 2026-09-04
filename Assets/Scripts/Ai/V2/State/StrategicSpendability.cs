using Game.Cards;
using Game.Economy;
using Game.Map;
using Game.Players;
using UnityEngine;

namespace Game.Ai.V2
{
    // ARCH-02 §45 — the canonical strategic-spendability seam. One place answers "does this cost
    // fit the resources I may actually spend right now", netting BOTH the strategic reservation
    // ledger and the legacy recon-air reservation so the same unit is never promised twice.
    // Extracted verbatim from StrategicManager (Phase A / Phase B / StrategicReactionPass all
    // consumed the same two methods) so there is a single owner, not a per-caller reimplementation.
    public static class StrategicSpendability
    {
        // spec §6 — a spend candidate must fit SPENDABLE persistent resources, not just raw stock:
        // the strategic reservation ledger AND the legacy recon-air reservation are both netted out
        // so the same resource is never promised to two owners. Also the canonical resource-
        // affordability probe reused by StrategicReactionPass (spec round 5 §3/§4).
        // round 6 §P1 / round 7 (P1) — `excludeOwner` drops the caller's OWN reservation (by its
        // EXACT Owner key, not the shared Reason) from the strategic spendable, so a re-probe of the
        // very reaction that placed a hold doesn't fail against it and two owners sharing a Reason
        // can't shadow each other.
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
                StrategicReservedResource srr = StrategicResourceReservationLedger.Map(t);
                float strategic = excludeOwner == null
                    ? StrategicResourceReservationLedger.Spendable(player, ctx.TurnNumber, srr, root.GetResource(t))
                    : StrategicResourceReservationLedger.SpendableExcludingOwner(
                        player, ctx.TurnNumber, srr, root.GetResource(t), excludeOwner);
                float legacy = Mathf.Max(0f, Game.Ai.AiResourceReservation.Available(root, player, t));
                if (Mathf.Min(strategic, legacy) + AiConfigV2.allocatorSliceEpsilon < need)
                    return false;
            }
            return true;
        }

        internal static bool ReservesOkAfterChain(PlayerRoot root, MaterializationPlan plan,
            PlayerSetupData player = null)
        {
            if (root == null || plan == null)
                return false;
            if (root.ActionPoints - plan.ApCost < 0f)
                return false;

            ResourceCost cost = plan.ResCost;
            if (cost == null)
                return true;

            return Has(ResourceType.Human, cost.human)
                && Has(ResourceType.Energy, cost.energy)
                && Has(ResourceType.Materials, cost.materials)
                && Has(ResourceType.Tech, cost.tech);

            // AI-RECON-01 — go through AiResourceReservation.Available so the recon-air reservation's
            // protected Energy is netted out here too: Phase A must not commit a materialisation
            // chain that spends Energy a planned-but-unlaunched recon sortie is holding.
            bool Has(ResourceType type, int spend)
            {
                float available = Mathf.Max(0f, Game.Ai.AiResourceReservation.Available(root, player, type));
                return available >= Mathf.Max(0, spend);
            }
        }
    }
}
