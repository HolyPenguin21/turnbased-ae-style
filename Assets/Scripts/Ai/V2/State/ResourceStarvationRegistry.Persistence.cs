using System;
using System.Collections.Generic;
using System.Linq;
using Game.HexGrid;
using Game.Players;
using Game.Map;

namespace Game.Ai.V2
{
    internal static partial class ResourceStarvationRegistry
    {
        internal sealed class ForecastState
        {
        // Exact detached save reconstruction; no planning, registration or normalization.
        internal ForecastState(bool __matchSave, int __Turn, global::System.Collections.Generic.IReadOnlyDictionary<string, global::Game.Ai.V2.ResourceBundle> __Needs)
        {
            Turn = __Turn;
            Needs = __Needs;
        }

            internal readonly int Turn;
            internal readonly IReadOnlyDictionary<string, ResourceBundle> Needs;
            internal ForecastState(int turn, IReadOnlyDictionary<string, ResourceBundle> needs)
            { Turn = turn; Needs = needs; }
        }
        internal sealed class PersistentState
        {
        // Exact detached save reconstruction; no planning, registration or normalization.
        internal PersistentState(bool __matchSave, global::System.Collections.Generic.IReadOnlyDictionary<string, global::Game.Ai.V2.ResourceStarvationRegistry.ForecastState> __Forecasts, global::System.Collections.Generic.IReadOnlyDictionary<global::Game.Economy.ResourceType, float> __Pressure, global::System.Collections.Generic.IReadOnlyDictionary<global::Game.Economy.ResourceType, global::Game.Ai.V2.ResourceBlockEvidence> __CurrentBlocks, global::System.Collections.Generic.IReadOnlyList<global::Game.Economy.ResourceType> __VerifiedPending, global::System.Collections.Generic.IReadOnlyDictionary<global::Game.Economy.ResourceType, int> __LastPressureHitTurn, bool __RequireVerifiedEvidence, int __LastDecayTurn)
        {
            Forecasts = __Forecasts;
            Pressure = __Pressure;
            CurrentBlocks = __CurrentBlocks;
            VerifiedPending = __VerifiedPending;
            LastPressureHitTurn = __LastPressureHitTurn;
            RequireVerifiedEvidence = __RequireVerifiedEvidence;
            LastDecayTurn = __LastDecayTurn;
        }

            internal readonly IReadOnlyDictionary<string, ForecastState> Forecasts;
            internal readonly IReadOnlyDictionary<Game.Economy.ResourceType, float> Pressure;
            internal readonly IReadOnlyDictionary<Game.Economy.ResourceType, ResourceBlockEvidence> CurrentBlocks;
            internal readonly IReadOnlyList<Game.Economy.ResourceType> VerifiedPending;
            internal readonly IReadOnlyDictionary<Game.Economy.ResourceType, int> LastPressureHitTurn;
            internal readonly bool RequireVerifiedEvidence;
            internal readonly int LastDecayTurn;
            internal PersistentState(IReadOnlyDictionary<string, ForecastState> forecasts,
                IReadOnlyDictionary<Game.Economy.ResourceType, float> pressure,
                IReadOnlyDictionary<Game.Economy.ResourceType, ResourceBlockEvidence> currentBlocks,
                IReadOnlyList<Game.Economy.ResourceType> verifiedPending,
                IReadOnlyDictionary<Game.Economy.ResourceType, int> lastPressureHitTurn,
                bool requireVerifiedEvidence, int lastDecayTurn)
            {
                Forecasts = forecasts; Pressure = pressure; CurrentBlocks = currentBlocks;
                VerifiedPending = verifiedPending; LastPressureHitTurn = lastPressureHitTurn;
                RequireVerifiedEvidence = requireVerifiedEvidence; LastDecayTurn = lastDecayTurn;
            }
        }
        internal static IReadOnlyDictionary<string, PersistentState> CapturePersistentState(Func<PlayerSetupData, string> playerId) =>
            AiPersistenceCollections.Capture(ByPlayer, playerId, x => new PersistentState(
                AiPersistenceCollections.Freeze(x.Forecasts.Select(row => new KeyValuePair<string, ForecastState>(row.Key,
                    new ForecastState(row.Value.Turn, AiPersistenceCollections.Freeze(row.Value.Needs))))),
                AiPersistenceCollections.Freeze(x.Pressure), AiPersistenceCollections.Freeze(x.CurrentBlocks),
                AiPersistenceCollections.FreezeList(x.VerifiedPending), AiPersistenceCollections.Freeze(x.LastPressureHitTurn),
                x.RequireVerifiedEvidence, x.LastDecayTurn));
        internal static void RestorePersistentState(IReadOnlyDictionary<string, PersistentState> state,
            Func<string, PlayerSetupData> player) => AiPersistenceCollections.Restore(ByPlayer, state, player, x =>
            {
                var restored = new State { RequireVerifiedEvidence = x.RequireVerifiedEvidence, LastDecayTurn = x.LastDecayTurn };
                foreach (var row in x.Forecasts) restored.Forecasts.Add(row.Key, new OperationalForecast
                    { Turn = row.Value.Turn, Needs = row.Value.Needs.ToDictionary(n => n.Key, n => n.Value) });
                foreach (var row in x.Pressure) restored.Pressure.Add(row.Key, row.Value);
                foreach (var row in x.CurrentBlocks) restored.CurrentBlocks.Add(row.Key, row.Value);
                foreach (var row in x.VerifiedPending) restored.VerifiedPending.Add(row);
                foreach (var row in x.LastPressureHitTurn) restored.LastPressureHitTurn.Add(row.Key, row.Value);
                return restored;
            });
    }
}
