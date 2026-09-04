using System.Collections.Generic;
using System.Linq;
using Game.Cards;
using Game.Economy;
using Game.Map;
using Game.Players;
using Game.Turns;
using Game.Units;
using UnityEngine;

namespace Game.Ai.V2.Initiative
{
    // A tiny, EPHEMERAL, read-only snapshot built once per round, before any player's AP has
    // been allocated, for the sole purpose of pricing "is another initiative die worth it".
    //
    // It is NOT the V2 WorldSnapshot and must never be reused as one: other players move between
    // now and this AI's real turn. It creates no missions / demands / budget slices / strategic
    // vectors and advances NOTHING (no radar, no momentum, no desire smoothing, no recon memory,
    // no AiMapMemory.OnTurnStarted, no mission-continuity state). It only reads.
    public sealed class PreTurnCapacityAnalysis
    {
        public PlayerSetupData Player;

        // ---- Current AP workload (structural only — the mission planners are NOT consulted) ----
        public int ActionableFieldArmyCount;      // occupied non-garrison/non-prison field armies
        public float ActionableMilitaryPower;     // Σ AiPower.EffectiveArmyPower over those armies
        public int ApCostingActionsAvailable;     // hand cards with a real play-time AP cost (0 if no hand yet)
        public float CurrentApPressure;           // [0..1]

        // ---- Historical ----
        public float HistoricalApPressure;        // [0..1]; equals current pressure when no history exists yet

        // ---- Blended pressures the value model consumes ----
        public float ApPressure;                  // [0..1] current structural workload + recent starvation evidence
        public float TurnOrderPressure;           // [0..1] generic capacity only — never DesireVector / mission priorities

        // ---- Resource expendability (index order: Human, Energy, Materials, Tech) ----
        public readonly int[] Available = new int[4];              // AiResourceReservation.Available (reservation-aware)
        public readonly int[] IncomePerTurn = new int[4];
        public readonly int[] DeckDemand = new int[4];             // remaining-game resource appetite

        public static readonly ResourceType[] Types = InitiativeDeckDemand.Types;

        // Marginal strategic cost of spending one more unit of resource `typeIndex` when the
        // hypothetical remaining stockpile of it is `hypotheticalStock`. Recomputed as a payment
        // plan drains a resource, so draining the last units of something scarce is priced far
        // above spending the first unit of something abundant.
        public float MarginalCostAt(int typeIndex, int hypotheticalStock)
        {
            if (typeIndex < 0 || typeIndex >= Types.Length)
                return AiConfigV2.initiativeCostAtParity;

            float futureDemand = DeckDemand[typeIndex] * AiConfigV2.initiativeDeckDemandWeight;
            float supply = hypotheticalStock + IncomePerTurn[typeIndex] * AiConfigV2.initiativeIncomeHorizonTurns;
            float coverageRatio = supply / Mathf.Max(1f, futureDemand);
            coverageRatio = Mathf.Clamp(coverageRatio,
                AiConfigV2.initiativeCoverageFloor, AiConfigV2.initiativeCoverageCeil);

            float cost = AiConfigV2.initiativeCostAtParity / coverageRatio;

            // Steep extra cost as the stockpile is drained toward empty.
            if (hypotheticalStock <= AiConfigV2.initiativeLowStockUnits)
            {
                float t = 1f - hypotheticalStock / (float)(AiConfigV2.initiativeLowStockUnits + 1);
                cost += AiConfigV2.initiativeLowStockPenalty * Mathf.Clamp01(t);
            }
            return cost;
        }

        public static PreTurnCapacityAnalysis Build(PlayerSetupData player, PlayerRoot root, HexMap map,
            StartingDeckCatalog deckCatalog)
        {
            var a = new PreTurnCapacityAnalysis { Player = player };
            if (player == null || root == null)
                return a;

            // --- structural AP workload ---
            // Empty ArmyData shells are intentionally reusable infrastructure in V2; they are NOT
            // formations that can consume an activation and must never create initiative pressure.
            List<ArmyData> armies = ArmyRegistry.AllForOwner(player)
                .Where(IsMeaningfullyActionableFieldArmy)
                .ToList();
            a.ActionableFieldArmyCount = armies.Count;
            foreach (ArmyData ar in armies)
                a.ActionableMilitaryPower += AiPower.EffectiveArmyPower(ar.Members);

            // Read an existing hand non-destructively — never create one just to price resources.
            AiHandData hand = AiHandRegistry.Peek(player);
            if (hand != null)
                a.ApCostingActionsAvailable = hand.Hand.Count(c => c != null && CardCostRules.PlayAp(c) > 0);

            float armyTerm = Mathf.Clamp01(a.ActionableFieldArmyCount
                / Mathf.Max(1f, AiConfigV2.initiativeApPressureArmyFull));
            float powerTerm = Mathf.Clamp01(a.ActionableMilitaryPower
                / Mathf.Max(1f, AiConfigV2.initiativeApPressurePowerFull));
            float cardTerm = Mathf.Clamp01(a.ApCostingActionsAvailable
                / Mathf.Max(1f, AiConfigV2.initiativeApPressureCardsFull));
            float currentWeightSum = AiConfigV2.initiativeApPressureWeightArmies
                + AiConfigV2.initiativeApPressureWeightPower
                + AiConfigV2.initiativeApPressureWeightCards;
            a.CurrentApPressure = Mathf.Clamp01((
                AiConfigV2.initiativeApPressureWeightArmies * armyTerm
                + AiConfigV2.initiativeApPressureWeightPower * powerTerm
                + AiConfigV2.initiativeApPressureWeightCards * cardTerm)
                / Mathf.Max(0.0001f, currentWeightSum));

            // No samples means "unknown", not a synthetic 0.5 need. Falling back to the current
            // signal keeps the first observed turn neutral with respect to history: the blend is
            // exactly CurrentApPressure rather than history inventing extra demand.
            if (!InitiativeAnalyticsHistory.TryHistoricalApPressure(player, out float historical))
                historical = a.CurrentApPressure;
            a.HistoricalApPressure = historical;

            float blendWeightSum = AiConfigV2.initiativeApPressureCurrentWeight
                + AiConfigV2.initiativeApPressureHistoryWeight;
            a.ApPressure = a.CurrentApPressure <= 0.0001f
                ? 0f
                : Mathf.Clamp01((
                    AiConfigV2.initiativeApPressureCurrentWeight * a.CurrentApPressure
                    + AiConfigV2.initiativeApPressureHistoryWeight * a.HistoricalApPressure)
                    / Mathf.Max(0.0001f, blendWeightSum));

            // Tempo is the same generic current capacity info, deliberately discounted so earlier
            // turn position stays a secondary benefit and never drives an expensive purchase alone.
            a.TurnOrderPressure = Mathf.Clamp01(
                a.CurrentApPressure * AiConfigV2.initiativeTurnOrderPressureScale);

            // --- resource expendability ---
            IEnumerable<CardDefinition> demandDefs;
            if (hand != null)
                demandDefs = hand.Hand.Where(c => c != null).Select(c => c.Definition)
                    .Concat(hand.RemainingDeck);
            else
                demandDefs = deckCatalog != null
                    ? deckCatalog.BuildDeckPool(player.Faction)
                    : Enumerable.Empty<CardDefinition>();
            InitiativeDeckDemand.Accumulate(demandDefs, a.DeckDemand);

            for (int i = 0; i < Types.Length; i++)
            {
                a.Available[i] = Mathf.Max(0, AiResourceReservation.Available(root, player, Types[i]));
                a.IncomePerTurn[i] = Mathf.Max(0, IncomeProjection.IncomeFor(player, Types[i], map));
            }
            return a;
        }

        // Shared with the pipeline's end-of-turn analytics recorder. "Actionable" means a real,
        // occupied field formation — never an empty reusable shell.
        public static int CountActionableFieldArmies(PlayerSetupData player, bool unactivatedOnly)
        {
            if (player == null)
                return 0;
            return ArmyRegistry.AllForOwner(player).Count(ar =>
                IsMeaningfullyActionableFieldArmy(ar)
                && (!unactivatedOnly || !ar.HasActivatedThisTurn));
        }

        private static bool IsMeaningfullyActionableFieldArmy(ArmyData army) =>
            army != null && !army.IsGarrison && !army.IsPrison && army.Members.Count > 0;
    }
}
