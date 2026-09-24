using System.Collections.Generic;
using System.Linq;
using Game.Cards;
using Game.Economy;
using Game.Map;
using Game.Players;
using UnityEngine;

namespace Game.Ai.V2
{
    // ARCH-02 §45/§47 — the ONE owner-aware strategic-spendability seam. Every "can I afford this
    // persistent-resource cost right now" question in the strategic + materialization + reaction
    // paths goes through SpendableAmount, which nets from the raw PlayerRoot stockpile:
    //   · StrategicResourceReservationLedger — the owner-aware explicit reservations (e.g. a
    //     bounded reaction envelope), optionally excluding the caller's own owner key; and
    //   · the unpaid activation of mandatory air-recovery wings (OutstandingRecoveryActivation).
    public static class StrategicSpendability
    {
        // An already-airborne wing whose canonical lifecycle projection demands Return, or whose
        // live Rebase record still commits it to landing, is safety work rather than a new
        // discretionary sortie. Phase A runs before operational admission, so the typed loop's
        // continuation step alone cannot protect its first activation from earlier card spending.
        // Read the SAME owner predicates the executors use and the wing's actual unpaid activation
        // costs. Do not reserve a fixed amount per aircraft, future-turn AP, or already-paid costs.
        // Re-evaluate against live actors: landing, activation, loss and lifecycle transitions
        // release the protection immediately.
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
            IEnumerable<ArmyData> obligations =
                ReconAirExecutor.FindMandatoryRecoveryActors(player, ctx)
                    .Concat(AviationRebasePlanner.FindMandatoryContinuations(player))
                    .GroupBy(a => a.Id).Select(g => g.First()).OrderBy(a => a.Id);
            foreach (ArmyData wing in obligations)
            {
                float activationAp = wing.HasActivatedThisTurn ? 0f
                    : Mathf.Max(0, wing.ActivationApCost);
                int activationEnergy = wing.HasActivatedThisTurn ? 0
                    : Mathf.Max(0, wing.ActivationEnergyCost);
                if (!CanFundRecoveryPrefix(root.ActionPoints,
                    root.GetResource(ResourceType.Energy), ap, energy,
                    activationAp, activationEnergy))
                    break;
                ap += activationAp;
                energy += activationEnergy;
            }
            return (ap, energy);
        }

        internal static bool CanFundRecoveryPrefix(float availableAp, int availableEnergy,
            float alreadyCommittedAp, int alreadyCommittedEnergy,
            float nextActivationAp, int nextActivationEnergy) =>
            alreadyCommittedAp + nextActivationAp <= availableAp
            && alreadyCommittedEnergy + nextActivationEnergy <= availableEnergy;

        // An existing owner-aware hold and the recovery obligation are independent claims on
        // the SAME physical stock, so both are subtracted.
        internal static float SpendableWithRecovery(float ownerAwareSpendable,
            float unpaidRecoveryCost) =>
            Mathf.Max(0f, ownerAwareSpendable - Mathf.Max(0f, unpaidRecoveryCost));

        internal static float SpendableAp(PlayerSetupData player, PlayerRoot root,
            AiTurnContext ctx, string excludeOwner = null)
        {
            if (root == null)
                return 0f;
            if (player == null || ctx == null)
                return Mathf.Max(0f, root.ActionPoints);
            float strategic = excludeOwner == null
                ? StrategicResourceReservationLedger.SpendableAp(
                    player, ctx.TurnNumber, root.ActionPoints)
                : StrategicResourceReservationLedger.SpendableExcludingOwner(
                    player, ctx.TurnNumber, StrategicReservedResource.ActionPoints,
                    root.ActionPoints, excludeOwner);
            return Mathf.Max(0f, strategic - OutstandingRecoveryActivation(player, root, ctx).Ap);
        }

        // The canonical primitive: how much of resource `t` may actually be spent this turn.
        internal static float SpendableAmount(PlayerSetupData player, PlayerRoot root, AiTurnContext ctx,
            ResourceType t, string excludeOwner = null,
            StrategicReservationReason? ignoreReason = null)
        {
            if (root == null)
                return 0f;
            if (player == null || ctx == null)
                return Mathf.Max(0f, root.GetResource(t));
            StrategicReservedResource srr = StrategicResourceReservationLedger.Map(t);
            float strategic = excludeOwner == null && ignoreReason == null
                ? StrategicResourceReservationLedger.Spendable(player, ctx.TurnNumber, srr, root.GetResource(t))
                : StrategicResourceReservationLedger.SpendableExcludingOwner(
                    player, ctx.TurnNumber, srr, root.GetResource(t), excludeOwner, ignoreReason);
            float recovery = t == ResourceType.Energy
                ? OutstandingRecoveryActivation(player, root, ctx).Energy : 0f;
            return SpendableWithRecovery(strategic, recovery);
        }

        // spec §6 — a spend candidate must fit SPENDABLE persistent resources, not just raw stock.
        // `excludeOwner` drops the caller's OWN reservation (by its EXACT Owner
        // key, not by the shared Reason) so a re-probe of the reaction that placed a hold does not fail
        // against itself and two owners sharing a Reason can't shadow each other's revalidation.
        internal static bool FitsSpendableResources(PlayerSetupData player, PlayerRoot root,
            AiTurnContext ctx, ResourceCost cost, string excludeOwner = null)
            => FitsSpendable(player, root, ctx, cost, excludeOwner, ignoreReason: null);

        // The Economy-completion gate: an Economy build that finishes THIS turn (Provisioning's
        // completion stage, its Execution re-check, or a Phase A on-hex build). Every other
        // owner's EconomyDeferredBuild hold is ignored — by contract such a hold belongs to a
        // build that cannot complete this turn and only shields H/E/M/T from non-Economy spending
        // (cards, Phase B, reactions), which keep using FitsSpendableResources. Reaction holds,
        // other builds' proven EconomyBuildCompletion rows and unpaid air-recovery activation
        // still count, so two builds completing in the same turn are ordered by whoever claims first.
        internal static bool FitsSpendableForEconomyCompletion(PlayerSetupData player,
            PlayerRoot root, AiTurnContext ctx, ResourceCost cost, string owner)
            => FitsSpendable(player, root, ctx, cost, owner,
                StrategicReservationReason.EconomyDeferredBuild);

        private static bool FitsSpendable(PlayerSetupData player, PlayerRoot root,
            AiTurnContext ctx, ResourceCost cost, string excludeOwner,
            StrategicReservationReason? ignoreReason)
        {
            if (cost == null)
                return true;
            foreach (ResourceType t in ResourceBundle.All)
            {
                int need = cost.Get(t);
                if (need <= 0)
                    continue;
                if (SpendableAmount(player, root, ctx, t, excludeOwner, ignoreReason)
                    + AiConfigV2.allocatorSliceEpsilon < need)
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
                ? SpendableAp(player, root, ctx)
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
