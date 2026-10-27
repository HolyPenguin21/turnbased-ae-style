using System;
using System.Collections.Generic;
using System.Linq;
using Game.Cards;
using Game.HexGrid;
using Game.Map;

namespace Game.Ai.V2
{
    internal sealed partial class DevelopmentLifecycleState
    {
        internal IReadOnlyDictionary<int, (HexCoord Site, ResearchProductionMode Mode, int Turn)> CapturePersistentState(Action<CardData> includeReferencedCard = null) =>
            AiPersistenceCollections.Freeze(_generatedDevelopmentOperators.Select(x =>
            {
                includeReferencedCard?.Invoke(x.Key);
                return new KeyValuePair<int, (HexCoord Site, ResearchProductionMode Mode, int Turn)>(x.Key.RuntimeId, x.Value);
            }));

        internal void RestorePersistentState(
            IReadOnlyDictionary<int, (HexCoord Site, ResearchProductionMode Mode, int Turn)> state,
            Func<int, CardData> card)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            if (card == null) throw new ArgumentNullException(nameof(card));
            var rebound = state.ToDictionary(x => AiPersistenceCollections.ResolveCard(x.Key, card), x => x.Value);
            _generatedDevelopmentOperators.Clear();
            foreach (var row in rebound) _generatedDevelopmentOperators.Add(row.Key, row.Value);
        }
    }
}
