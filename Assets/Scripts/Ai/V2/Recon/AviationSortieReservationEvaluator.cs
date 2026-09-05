using Game.Ai;
using Game.Economy;
using Game.Map;
using Game.Players;
using UnityEngine;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  AVIATION SORTIE RESERVATION EVALUATOR  (AI-MGR — Context-Aware Aviation Sortie Reservation)
    // ===========================================================================================
    //  Having an available aircraft + a legal route is NOT, on its own, a reason to protect AP /
    //  Energy for it. This evaluator replaces that implicit entitlement with an explicit staged
    //  decision:
    //
    //     Resource Outlook -> Hand/Deck Energy Pressure -> Sortie Value -> Reservation Decision
    //
    //  It is PURE / READ-ONLY: it does not spend resources, does not create a reservation, does not
    //  mutate Mission/World state. The caller (ReconAirReservationPrepass) applies the result.
    //
    //  Recon sortie value is NOT re-derived here — it is the AIR-01 route score
    //  (AirReconRouteScorer via ReconAirStepPlanner.Pick/PickFromStorage), which already folds in
    //  freshness, enemy/frontline/objective proximity, frontier coverage and actionable value
    //  (InformationAge != InformationValue is an AIR-01 property this evaluator inherits, not
    //  re-implements).
    //
    //  Hand/Deck Energy pressure reuses ReconAirEnergyPolicy's generic (name-free) card scan —
    //  a new Energy-cost card in hand or deck is picked up automatically, no hardcoding.
    //
    //  Combat sortie value is NOT evaluated yet (SelectedSortieUtility is always Recon this pass);
    //  CombatUtility is reported as 0 so the diagnostic shape already matches the eventual combat
    //  addition without pretending a decision was made.
    // ===========================================================================================
    internal enum AviationSortieType { None, Recon, Combat }

    internal readonly struct AviationReservationDecision
    {
        public readonly bool ShouldReserve;
        public readonly AviationSortieType SortieType;
        public readonly int ReserveAp;
        public readonly int ReserveEnergy;

        public readonly float ResourceHeadroom;
        public readonly float HandEnergyPressure;
        public readonly float DeckEnergyPressure;
        public readonly int ProtectedCardEnergy;

        public readonly float ReconUtility;
        public readonly float CombatUtility;
        public readonly float SelectedSortieUtility;
        public readonly float OpportunityCost;

        public readonly string Reason;

        private AviationReservationDecision(bool shouldReserve, AviationSortieType sortieType,
            int reserveAp, int reserveEnergy, float resourceHeadroom, float handEnergyPressure,
            float deckEnergyPressure, int protectedCardEnergy, float reconUtility, float combatUtility,
            float selectedSortieUtility, float opportunityCost, string reason)
        {
            ShouldReserve = shouldReserve;
            SortieType = sortieType;
            ReserveAp = reserveAp;
            ReserveEnergy = reserveEnergy;
            ResourceHeadroom = resourceHeadroom;
            HandEnergyPressure = handEnergyPressure;
            DeckEnergyPressure = deckEnergyPressure;
            ProtectedCardEnergy = protectedCardEnergy;
            ReconUtility = reconUtility;
            CombatUtility = combatUtility;
            SelectedSortieUtility = selectedSortieUtility;
            OpportunityCost = opportunityCost;
            Reason = reason ?? string.Empty;
        }

        public static AviationReservationDecision None(string reason) =>
            new AviationReservationDecision(false, AviationSortieType.None, 0, 0, 0f, 0f, 0f, 0,
                0f, 0f, 0f, 0f, reason);

        public static AviationReservationDecision Rejected(AviationSortieType sortieType,
            float resourceHeadroom, float handEnergyPressure, float deckEnergyPressure,
            int protectedCardEnergy, float reconUtility, float combatUtility,
            float selectedSortieUtility, float opportunityCost, string reason) =>
            new AviationReservationDecision(false, sortieType, 0, 0, resourceHeadroom,
                handEnergyPressure, deckEnergyPressure, protectedCardEnergy, reconUtility,
                combatUtility, selectedSortieUtility, opportunityCost, reason);

        public static AviationReservationDecision Reserve(AviationSortieType sortieType,
            int reserveAp, int reserveEnergy, float resourceHeadroom, float handEnergyPressure,
            float deckEnergyPressure, int protectedCardEnergy, float reconUtility,
            float combatUtility, float selectedSortieUtility, float opportunityCost, string reason) =>
            new AviationReservationDecision(true, sortieType, reserveAp, reserveEnergy,
                resourceHeadroom, handEnergyPressure, deckEnergyPressure, protectedCardEnergy,
                reconUtility, combatUtility, selectedSortieUtility, opportunityCost, reason);

        public string ToLog(string actorLabel) =>
            $"[AI][V2][Recon][Air][AviationReserveEval] {actorLabel} "
            + $"headroom={ResourceHeadroom:0.#} handPressure={HandEnergyPressure:0.#} "
            + $"deckPressure={DeckEnergyPressure:0.#} protectedCardEnergy={ProtectedCardEnergy} "
            + $"reconUtil={ReconUtility:0.00} combatUtil={CombatUtility:0.00} "
            + $"selected={SortieType} sortieUtil={SelectedSortieUtility:0.00} oppCost={OpportunityCost:0.00} "
            + $"decision={(ShouldReserve ? "RESERVE" : "SKIP")} "
            + $"reserveAp={ReserveAp} reserveEnergy={ReserveEnergy} reason={Reason}";
    }

    internal static class AviationSortieReservationEvaluator
    {
        // Only Recon sorties are evaluated for now — Combat sortie value is a follow-up addition.
        // launchApCost / launchEnergyCost — this candidate sortie's own first-activation cost.
        // reconInformationValue — the AIR-01 route score for this candidate (already the full
        // InformationGain + StaleIntelRefreshValue + EnemyInterest + ... composite, §3 of the spec).
        // excludeArmyId — the actor being evaluated (so it never counts its own owed Energy as
        // "committed"); negative for a not-yet-formed storage launch.
        // extraCommittedAp / extraCommittedEnergy — AP/Energy already claimed by earlier candidates
        // reserved in the SAME planning pass this turn (several sorties must not each evaluate
        // against the full stockpile).
        public static AviationReservationDecision EvaluateRecon(PlayerSetupData player, PlayerRoot root,
            HexMap map, int launchApCost, int launchEnergyCost, float reconInformationValue,
            int excludeArmyId, int extraCommittedAp, int extraCommittedEnergy)
        {
            if (player == null || root == null)
                return AviationReservationDecision.None("missing_player_or_root");

            // ---- Stage 1: Resource Outlook ----
            int energyStock = Mathf.Max(0, root.GetResource(ResourceType.Energy));
            int committedEnergy = ReconAirEnergyPolicy.CommittedAirActivationEnergy(player, excludeArmyId)
                + Mathf.Max(0, extraCommittedEnergy);
            int availableEnergy = Mathf.Max(0, energyStock - committedEnergy);

            float expectedEnergyIncome = map != null
                ? Mathf.Max(0f, IncomeProjection.IncomeFor(player, ResourceType.Energy, map))
                : 0f;
            float energyHeadroom = availableEnergy
                + expectedEnergyIncome * AiConfigV2.aviationReserveIncomeHorizon;

            int apStock = Mathf.Max(0, root.ActionPoints);
            int committedAp = Mathf.Max(0, extraCommittedAp);
            int availableAp = Mathf.Max(0, apStock - committedAp);

            // ---- Stage 2: Hand + Deck Energy Pressure ----
            // Both terms reuse ReconAirEnergyPolicy's existing generic (name-free) hand/deck scan —
            // "currently playable, above a high-value floor" for hand, probability-weighted mean of
            // the remaining deck for the near-term draw. Nothing here is hardcoded per card.
            float handEnergyPressure = ReconAirEnergyPolicy.ProtectedHandEnergy(root, player);
            float deckEnergyPressure = ReconAirEnergyPolicy.ProtectedNearTermDrawEnergy(player);
            int protectedCardEnergy = Mathf.RoundToInt(handEnergyPressure + deckEnergyPressure);

            // ---- Stage 3: Sortie value ----
            // Recon utility is the AIR-01 route score verbatim — InformationAge is only one of the
            // inputs AirReconRouteScorer already folds into it; this evaluator does not re-weight it
            // by age alone.
            float reconUtility = reconInformationValue;
            const float combatUtility = 0f; // deferred — no standalone combat-sortie value yet
            const AviationSortieType sortieType = AviationSortieType.Recon;
            float selectedUtility = reconUtility;

            if (selectedUtility < AiConfigV2.aviationReserveMinSortieUtility)
                return AviationReservationDecision.Rejected(sortieType, energyHeadroom,
                    handEnergyPressure, deckEnergyPressure, protectedCardEnergy, reconUtility,
                    combatUtility, selectedUtility, 0f, "no_actionable_recon_value");

            // ---- Stage 4: Reservation decision ----
            if (launchApCost > availableAp)
                return AviationReservationDecision.Rejected(sortieType, energyHeadroom,
                    handEnergyPressure, deckEnergyPressure, protectedCardEnergy, reconUtility,
                    combatUtility, selectedUtility, 0f, "insufficient_ap_headroom");

            float spendableEnergy = Mathf.Max(0f, availableEnergy - protectedCardEnergy);
            if (launchEnergyCost > spendableEnergy)
            {
                string blockedReason = protectedCardEnergy <= 0
                    ? "insufficient_energy_headroom"
                    : handEnergyPressure >= deckEnergyPressure ? "energy_needed_by_hand" : "future_energy_pressure";
                return AviationReservationDecision.Rejected(sortieType, energyHeadroom,
                    handEnergyPressure, deckEnergyPressure, protectedCardEnergy, reconUtility,
                    combatUtility, selectedUtility, 0f, blockedReason);
            }

            // Soft opportunity term — a marginal sortie is trimmed when spendable Energy is thin
            // relative to near-term income; a healthy runway makes the same sortie cheap. Mirrors
            // the shape of the retired ReconAirEnergyPolicy soft term, now folded into ONE staged
            // decision instead of a second independent gate.
            float effectiveSpendable = spendableEnergy + expectedEnergyIncome * AiConfigV2.aviationReserveIncomeHorizon;
            float opportunityCost = (launchEnergyCost / Mathf.Max(1f, effectiveSpendable))
                * AiConfigV2.aviationReserveOpportunityWeight;
            float netUtility = selectedUtility - opportunityCost;

            if (netUtility < AiConfigV2.aviationReserveMinNetUtility)
                return AviationReservationDecision.Rejected(sortieType, energyHeadroom,
                    handEnergyPressure, deckEnergyPressure, protectedCardEnergy, reconUtility,
                    combatUtility, selectedUtility, opportunityCost, "sortie_below_opportunity_cost");

            string acceptReason = protectedCardEnergy > 0
                ? "sortie_overrides_card_pressure"
                : energyHeadroom > availableEnergy * 1.5f
                    ? "resource_surplus"
                    : "valuable_recon_opportunity";

            return AviationReservationDecision.Reserve(sortieType, launchApCost, launchEnergyCost,
                energyHeadroom, handEnergyPressure, deckEnergyPressure, protectedCardEnergy,
                reconUtility, combatUtility, selectedUtility, opportunityCost, acceptReason);
        }
    }
}
