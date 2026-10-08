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

        // Read-only destination binding. Expired claims never constrain a new candidate.
        internal bool CanUseGeneratedOperatorAt(CardData card, HexCoord site,
            ResearchProductionMode mode, int turn) =>
            !_generatedDevelopmentOperators.TryGetValue(card, out var claim)
                || turn < claim.Turn || turn - claim.Turn > Math.Max(1, AiConfigV2.commitmentStallTurns)
                || (claim.Site.Equals(site) && claim.Mode == mode);

        internal CardData GeneratedOperatorFor(HexCoord site, ResearchProductionMode mode, int turn) =>
            _generatedDevelopmentOperators.Where(x => x.Value.Site.Equals(site) && x.Value.Mode == mode
                && turn >= x.Value.Turn && turn - x.Value.Turn <= Math.Max(1, AiConfigV2.commitmentStallTurns))
                .Select(x => x.Key).FirstOrDefault();

        internal string GeneratedOperatorFacts(int turn) => string.Join(";",
            _generatedDevelopmentOperators.Where(x => turn >= x.Value.Turn
                    && turn - x.Value.Turn <= Math.Max(1, AiConfigV2.commitmentStallTurns))
                .Select(x => $"{GenerationSource.StableCardKey(x.Key)}:{x.Value.Site}:{x.Value.Mode}:{x.Value.Turn}")
                .OrderBy(x => x, StringComparer.Ordinal));

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

