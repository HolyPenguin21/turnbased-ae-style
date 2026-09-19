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
        // An already-airborne wing whose canonical lifecycle projection demands Return is safety
        // work, not another discretionary Recon sortie. Phase A runs before operational admission,
        // so the typed loop's mandatory-return step alone cannot protect its first activation from
        // earlier card spending. Read the SAME recovery predicate ReconAirExecutor executes, and
        // the wing's actual unpaid activation costs. Do not reserve a fixed amount per aircraft,
        // future-turn AP, or the cost of already activated wings. Re-evaluate against live actors:
        // landing, activation, loss and lifecycle transitions release the protection immediately.
        // The executor visits actors in ID order and stops when its recovery step cannot progress;
        // protect only the prefix whose unpaid costs fit today's physical AP/Energy. An already
        // unaffordable recovery must not freeze otherwise usable resources for the rest of the turn.
        private static (float Ap, int Energy) OutstandingRecoveryActivation(
            PlayerSetupData player, PlayerRoot root, AiTurnContext ctx)
        {
            if (player == null || root == null || ctx?.Map == null)
                return (0f, 0);
            float ap = 0f;
            int energy = 0;
            foreach (ArmyData wing in ReconAirExecutor.FindMandatoryRecoveryActors(player, ctx))
            {
                float activationAp = wing.HasActivatedThisTurn ? 0f
                    : Mathf.Max(0, wing.ActivationApCost);
                int activationEnergy = wing.HasActivatedThisTurn ? 0
                    : Mathf.Max(0, wing.ActivationEnergyCost);
                if (ap + activationAp > root.ActionPoints
                    || energy + activationEnergy > root.GetResource(ResourceType.Energy))
                    break;
                ap += activationAp;
                energy += activationEnergy;
            }
            return (ap, energy);
        }

        // An existing owner-aware hold and the recovery obligation are independent claims on
        // the SAME physical stock. Legacy reservations are a separate view of that stock and
        // must not be subtracted a second time if they already protect a wing.
        internal static float SpendableWithRecovery(float ownerAwareSpendable,
            float legacySpendable, float unpaidRecoveryCost) =>
            Mathf.Min(Mathf.Max(0f, ownerAwareSpendable - Mathf.Max(0f, unpaidRecoveryCost)),
                Mathf.Max(0f, legacySpendable));

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
            float recovery = t == ResourceType.Energy
                ? OutstandingRecoveryActivation(player, root, ctx).Energy : 0f;
            return SpendableWithRecovery(strategic, legacy, recovery);
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

        // Physical guard for a whole materialization chain: AP and every persistent resource
        // must fit the SAME owner-aware spendable pool. Raw AP is not available to a discretionary
        // chain while another owner holds an explicit reaction/completion reservation. Callers
        // without a turn-scoped owner retain the historical raw-AP fallback.
        internal static bool ReservesOkAfterChain(PlayerRoot root, AiTurnContext ctx,
            MaterializationPlan plan, PlayerSetupData player = null)
        {
            if (root == null || plan == null)
                return false;
            float availableAp = player != null && ctx != null
                ? StrategicResourceReservationLedger.SpendableAp(
                    player, ctx.TurnNumber, root.ActionPoints)
                    - OutstandingRecoveryActivation(player, root, ctx).Ap
                : root.ActionPoints;
            if (availableAp - plan.ApCost < 0f)
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
