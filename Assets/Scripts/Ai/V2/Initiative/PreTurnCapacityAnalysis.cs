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
        public int ActionableFieldArmyCount;      // non-garrison, non-prison armies that could need activation
        public int IndependentActivationOpportunities; // separate armies => separate activation chances
        public float ActionableMilitaryPower;     // Σ AiPower.EffectiveArmyPower over those armies
        public int ApCostingActionsAvailable;     // hand cards with a real play-time AP cost (0 if no hand yet)
        public float CurrentApPressure;           // [0..1]

        // ---- Historical ----
        public float HistoricalApPressure;        // [0..1], 0.5 == neutral / no history

        // ---- Blended pressures the value model consumes ----
        public float ApPressure;                  // [0..1] current structural workload + recent starvation
        public float TurnOrderPressure;           // [0..1] generic capacity only — never DesireVector / mission priorities

        // ---- Resource expendability (index order: Human, Energy, Materials, Tech) ----
        public readonly int[] Available = new int[4];              // AiResourceReservation.Available (reservation-aware)
        public readonly int[] IncomePerTurn = new int[4];
        public readonly int[] DeckDemand = new int[4];             // remaining-game resource appetite (shared formula)
        public readonly float[] FirstUnitOpportunityCost = new float[4]; // marginal cost of spending ONE unit now

        public static readonly ResourceType[] Types = InitiativeDeckDemand.Types;

        // Marginal strategic cost of spending one more unit of resource `typeIndex` when the
        // hypothetical remaining stockpile of it is `hypotheticalStock`. Recomputed as a payment
        // plan drains a resource, so draining the last units of something scarce is priced far
        // above spending the first unit of something abundant.
        public float MarginalCostAt(int typeIndex, int hypotheticalStock)
        {
            if (typeIndex < 0 || typeIndex >= 4)
                return AiConfigV2.initiativeCostAtParity;

            float futureDemand = DeckDemand[typeIndex] * AiConfigV2.initiativeDeckDemandWeight;
            float supply = hypotheticalStock + IncomePerTurn[typeIndex] * AiConfigV2.initiativeIncomeHorizonTurns;
            float coverageRatio = supply / Mathf.Max(1f, futureDemand);
            coverageRatio = Mathf.Clamp(coverageRatio, AiConfigV2.initiativeCoverageFloor, AiConfigV2.initiativeCoverageCeil);

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
            List<ArmyData> armies = ArmyRegistry.AllForOwner(player)
                .Where(ar => ar != null && !ar.IsGarrison && !ar.IsPrison)
                .ToList();
            a.ActionableFieldArmyCount = armies.Count;
            a.IndependentActivationOpportunities = armies.Count;
            foreach (ArmyData ar in armies)
                a.ActionableMilitaryPower += AiPower.EffectiveArmyPower(ar.Members);

            // Read an existing hand non-destructively — never create one just to price resources.
            AiHandData hand = AiHandRegistry.Peek(player);
            if (hand != null)
                a.ApCostingActionsAvailable = hand.Hand.Count(c => c != null && AiCardCost.PlayAp(c) > 0);

            float armyTerm = Mathf.Clamp01(a.ActionableFieldArmyCount / Mathf.Max(1f, AiConfigV2.initiativeApPressureArmyFull));
            float powerTerm = Mathf.Clamp01(a.ActionableMilitaryPower / Mathf.Max(1f, AiConfigV2.initiativeApPressurePowerFull));
            float cardTerm = Mathf.Clamp01(a.ApCostingActionsAvailable / Mathf.Max(1f, AiConfigV2.initiativeApPressureCardsFull));
            float wSum = AiConfigV2.initiativeApPressureWeightArmies + AiConfigV2.initiativeApPressureWeightPower
                       + AiConfigV2.initiativeApPressureWeightCards;
            a.CurrentApPressure = Mathf.Clamp01((
                AiConfigV2.initiativeApPressureWeightArmies * armyTerm
                + AiConfigV2.initiativeApPressureWeightPower * powerTerm
                + AiConfigV2.initiativeApPressureWeightCards * cardTerm) / Mathf.Max(0.0001f, wSum));

            a.HistoricalApPressure = InitiativeAnalyticsHistory.HistoricalApPressure(player);

            a.ApPressure = Mathf.Clamp01(
                AiConfigV2.initiativeApPressureCurrentWeight * a.CurrentApPressure
                + AiConfigV2.initiativeApPressureHistoryWeight * a.HistoricalApPressure);

            // Tempo is the same generic capacity info, deliberately discounted so earlier turn
            // position stays a secondary benefit and never drives an expensive purchase alone.
            a.TurnOrderPressure = Mathf.Clamp01(a.CurrentApPressure * AiConfigV2.initiativeTurnOrderPressureScale);

            // --- resource expendability ---
            IEnumerable<CardDefinition> demandDefs;
            if (hand != null)
                demandDefs = hand.Hand.Where(c => c != null).Select(c => c.Definition)
                    .Concat(hand.RemainingDeck);
            else
                demandDefs = deckCatalog != null ? deckCatalog.BuildDeckPool(player.Faction)
                    : Enumerable.Empty<CardDefinition>();
            InitiativeDeckDemand.Accumulate(demandDefs, a.DeckDemand);

            for (int i = 0; i < 4; i++)
            {
                a.Available[i] = Mathf.Max(0, AiResourceReservation.Available(root, player, Types[i]));
                a.IncomePerTurn[i] = Mathf.Max(0, AiGoalScorer.IncomeFor(player, Types[i], map));
                a.FirstUnitOpportunityCost[i] = a.MarginalCostAt(i, a.Available[i]);
            }
            return a;
        }

        // Shared with the pipeline's end-of-turn analytics recorder: an army that could require
        // activation this turn. Kept here so "actionable" means the same thing at both ends.
        public static int CountActionableFieldArmies(PlayerSetupData player, bool unactivatedOnly)
        {
            if (player == null)
                return 0;
            return ArmyRegistry.AllForOwner(player).Count(ar =>
                ar != null && !ar.IsGarrison && !ar.IsPrison
                && (!unactivatedOnly || !ar.HasActivatedThisTurn));
        }
    }
}
