using System;
using System.Collections.Generic;
using System.Linq;
using Game.Cards;
using Game.Economy;
using Game.HexGrid;
using Game.Map;
using Game.Players;

namespace Game.Ai.V2
{
    // Persistent per-player generated operator claims. The reconciliation rule is unchanged.
    internal sealed class DevelopmentLifecycleState
    {
        // Challenge mints a physical Hero before the destination factory can deploy it.
        // This per-player intent store survives turns; AP/resource reservations do not.
        private readonly Dictionary<CardData, (HexCoord Site, ResearchProductionMode Mode, int Turn)>
            _generatedDevelopmentOperators =
                new Dictionary<CardData, (HexCoord, ResearchProductionMode, int)>();

        internal void RememberGeneratedDevelopmentOperator(CardData card, HexCoord site,
            ResearchProductionMode mode, int turn)
        {
            if (card == null) return;
            // Only one card may be earmarked for each facility/role at once.
            foreach (CardData previous in _generatedDevelopmentOperators
                .Where(x => x.Value.Site.Equals(site) && x.Value.Mode == mode)
                .Select(x => x.Key).ToList())
                _generatedDevelopmentOperators.Remove(previous);
            _generatedDevelopmentOperators[card] = (site, mode, turn);
        }

        // Infrastructure provides current structural eligibility; State owns identity,
        // finite age and removal. An AP shortage is NOT a reason to discard a valid card.
        internal IReadOnlyList<CardData> ReconcileGeneratedDevelopmentOperators(int turn,
            Func<CardData, HexCoord, ResearchProductionMode, bool> stillNeeded)
        {
            foreach (var claim in _generatedDevelopmentOperators.ToList())
                if (turn < claim.Value.Turn
                    || turn - claim.Value.Turn > System.Math.Max(1, AiConfigV2.commitmentStallTurns)
                    || stillNeeded == null
                    || !stillNeeded(claim.Key, claim.Value.Site, claim.Value.Mode))
                    _generatedDevelopmentOperators.Remove(claim.Key);
            return _generatedDevelopmentOperators.Keys.ToList();
        }

    }
}
