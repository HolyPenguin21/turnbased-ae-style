using System;
using System.Collections.Generic;
using System.Linq;
using Game.Cards;
using Game.Economy;
using Game.HexGrid;

namespace Game.Ai.V2
{
    internal sealed partial class EconomyLifecycleState
    {
        internal sealed class DeliveryEvidence<TKey>
        {
        // Exact detached save reconstruction; no planning, registration or normalization.
        internal DeliveryEvidence(bool __matchSave, global::System.Collections.Generic.IReadOnlyDictionary<TKey, (int Turn, int Count)> __Failures, global::System.Collections.Generic.IReadOnlyDictionary<TKey, int> __SuppressedUntil, global::System.Collections.Generic.IReadOnlyDictionary<TKey, int> __ProgressTurns)
        {
            Failures = __Failures;
            SuppressedUntil = __SuppressedUntil;
            ProgressTurns = __ProgressTurns;
        }

            internal readonly IReadOnlyDictionary<TKey, (int Turn, int Count)> Failures;
            internal readonly IReadOnlyDictionary<TKey, int> SuppressedUntil;
            internal readonly IReadOnlyDictionary<TKey, int> ProgressTurns;
            internal DeliveryEvidence(IReadOnlyDictionary<TKey, (int Turn, int Count)> failures,
                IReadOnlyDictionary<TKey, int> suppressedUntil, IReadOnlyDictionary<TKey, int> progressTurns)
            { Failures = failures; SuppressedUntil = suppressedUntil; ProgressTurns = progressTurns; }
        }
        internal sealed class PersistentState
        {
        // Exact detached save reconstruction; no planning, registration or normalization.
        internal PersistentState(bool __matchSave, global::Game.Ai.V2.EconomyLifecycleState.DeliveryEvidence<(int CardId, global::Game.HexGrid.HexCoord Hex)> __BaseDelivery, global::Game.Ai.V2.EconomyLifecycleState.DeliveryEvidence<(global::Game.Economy.ResourceType? Resource, global::Game.HexGrid.HexCoord Hex)> __ExtractionDelivery, global::System.Collections.Generic.IReadOnlyDictionary<int, (global::Game.HexGrid.HexCoord Site, float Value)> __EndedOutboundBuilders)
        {
            BaseDelivery = __BaseDelivery;
            ExtractionDelivery = __ExtractionDelivery;
            EndedOutboundBuilders = __EndedOutboundBuilders;
        }

            internal readonly DeliveryEvidence<(int CardId, HexCoord Hex)> BaseDelivery;
            internal readonly DeliveryEvidence<(ResourceType? Resource, HexCoord Hex)> ExtractionDelivery;
            internal readonly IReadOnlyDictionary<int, (HexCoord Site, float Value)> EndedOutboundBuilders;
            internal PersistentState(EconomyLifecycleState state, Action<CardData> includeReferencedCard)
            {
                BaseDelivery = state._baseDeliveryFailures.Capture(x =>
                {
                    includeReferencedCard?.Invoke(x.Item1);
                    return (x.Item1.RuntimeId, x.Item2);
                });
                ExtractionDelivery = state._extractionDeliveryFailures.Capture(x => x);
                EndedOutboundBuilders = AiPersistenceCollections.Freeze(state._endedOutboundBuilders);
            }
        }
        // The entity writer must retain card objects held only by private failure evidence.
        internal PersistentState CapturePersistentState(Action<CardData> includeReferencedCard = null) =>
            new PersistentState(this, includeReferencedCard);
        internal void RestorePersistentState(PersistentState state, Func<int, CardData> card)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            if (card == null) throw new ArgumentNullException(nameof(card));
            var bound = state.BaseDelivery.Failures.Keys.Concat(state.BaseDelivery.SuppressedUntil.Keys)
                .Concat(state.BaseDelivery.ProgressTurns.Keys).Select(x => x.CardId).Distinct()
                .ToDictionary(x => x, x => AiPersistenceCollections.ResolveCard(x, card));
            _baseDeliveryFailures.Restore(state.BaseDelivery, x => (bound[x.CardId], x.Hex));
            _extractionDeliveryFailures.Restore(state.ExtractionDelivery, x => x);
            _endedOutboundBuilders.Clear();
            foreach (var row in state.EndedOutboundBuilders) _endedOutboundBuilders.Add(row.Key, row.Value);
        }
    }
}
