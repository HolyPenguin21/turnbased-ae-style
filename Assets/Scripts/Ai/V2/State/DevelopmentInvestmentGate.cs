using System.Collections.Generic;
using System.Linq;
using Game.Cards;
using Game.Economy;
using Game.Players;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  DEVELOPMENT INVESTMENT GATE  (Strategy V2 — when Research/Production may spend)
    // ===========================================================================================
    //  Laboratory / Factory are a late resource sink: they strengthen the units the main deck has
    //  already put on the map and must never compete with playing that deck. A resource is in
    //  surplus once its headroom (DevelopmentReadiness.InvestmentSurplusByType) has stayed at or
    //  above devInvestmentSurplusThreshold for devInvestmentSurplusTurns consecutive turns. In the
    //  opening everything gets spent, so no streak builds; once the deck stops absorbing a
    //  resource, it opens by itself.
    //
    //  The window is judged PER SPEND and only on the resources that spend consumes: an empty Tech
    //  stock never blocks an Energy+Materials Equipment. A spend with no H/E/M/T cost is not a
    //  resource sink at all and is always open — its AP competition with the main deck is settled
    //  in Phase A's one card arbitration (intrinsic card score x the Development Radar scale).
    //
    //  One writer: DemandLayer.Generate records the turn's fact once, from the turn-start snapshot
    //  (before this turn's own spending). Every spender reads the same answer: facility
    //  preparation, the ready-facility upgrade and — through GenerationSource — every Challenge a
    //  materialization or non-combat chain could start. A turn that was never observed reads as
    //  closed for every resource.
    // ===========================================================================================
    internal static partial class DevelopmentInvestmentGate
    {
        private sealed class State
        {
            public int LastTurn = int.MinValue;
            public readonly Dictionary<ResourceType, int> Streak = new Dictionary<ResourceType, int>();
            public ResourceBundle Surplus;
        }

        private static readonly Dictionary<PlayerSetupData, State> ByPlayer =
            new Dictionary<PlayerSetupData, State>();

        public static void Clear() => ByPlayer.Clear();

        // First call of a turn wins; repeated mid-turn Generate passes keep the turn-start fact.
        internal static void Observe(PlayerSetupData player, WorldSnapshot snap)
        {
            if (player == null || snap?.Development == null)
                return;
            if (!ByPlayer.TryGetValue(player, out State s))
                ByPlayer[player] = s = new State();
            if (s.LastTurn == snap.TurnNumber)
                return;
            // A skipped turn breaks every streak: the window proves resources accumulated on
            // consecutive turns, not merely on two turns ever.
            bool consecutive = s.LastTurn == snap.TurnNumber - 1;
            s.Surplus = snap.Development.InvestmentSurplusByType;
            foreach (ResourceType t in ResourceBundle.All)
            {
                int previous = consecutive && s.Streak.TryGetValue(t, out int p) ? p : 0;
                float? cardNeed = snap.Economy != null
                    ? MainDeckNeed(snap.Economy.DeckResourceNeed.Get(t), snap.Self?.Hand, snap.Self?.Deck, t)
                    : (float?)null;
                s.Streak[t] = CountsAsSurplus(s.Surplus.Get(t), cardNeed) ? previous + 1 : 0;
            }
            s.LastTurn = snap.TurnNumber;
            AiDebugLog.Write($"[AI][V2][Development][Gate] turn={snap.TurnNumber} "
                + $"threshold={AiConfigV2.devInvestmentSurplusThreshold:0.00} "
                + $"turns={AiConfigV2.devInvestmentSurplusTurns} "
                + string.Join(" ", ResourceBundle.All.Select(t =>
                    $"{Abbrev(t)}={s.Surplus.Get(t):0.00}/{s.Streak[t]}"
                    + (s.Streak[t] >= AiConfigV2.devInvestmentSurplusTurns ? ":OPEN" : ":closed"))));
        }

        // 2026-10-16 — the window protects the hand and the deck: a resource neither still needs
        // (EconomySnapshot.DeckResourceNeed 0) is theirs no longer, so it counts as surplus even
        // while Development or aviation keep spending it. Playtest: deck empty, every Energy unit
        // went to equipment and sorties, so the 1-Energy mutators never saw an open window.
        // `cardNeed` null = unknown (no economy snapshot): only the headroom decides.
        internal static bool CountsAsSurplus(float headroom, float? cardNeed) =>
            headroom >= AiConfigV2.devInvestmentSurplusThreshold
            || (cardNeed.HasValue && cardNeed.Value <= 0f);

        // 2026-10-17 — the hand/deck need the window protects is the MAIN deck's. A Research /
        // Production Facility card is Development's own spend: counting its bill as "still
        // needed" kept the window closed against that very card. Playtest: Cassia T19-T22, deck
        // empty, hand = Laboratory only, deckNeed H/E/M/T = 1/1/0/1 = the Laboratory's cost.
        internal static float MainDeckNeed(float deckResourceNeed, IEnumerable<CardData> hand,
            IEnumerable<CardDefinition> deck, ResourceType t)
        {
            float own = 0f;
            foreach (CardData c in hand ?? Enumerable.Empty<CardData>())
                if (ResearchProductionSystem.IsResearchProductionFacility(c?.Definition))
                    own += CardCostRules.PlayResources(c)?.Get(t) ?? 0;
            foreach (CardDefinition d in deck ?? Enumerable.Empty<CardDefinition>())
                if (ResearchProductionSystem.IsResearchProductionFacility(d))
                    own += d.resourceCost?.Get(t) ?? 0;
            return System.Math.Max(0f, deckResourceNeed - own);
        }

        // Is this concrete spend inside the window? Only resources with a positive cost count.
        internal static bool IsOpenFor(PlayerSetupData player, int turn, ResourceCost cost) =>
            ClosedResources(player, turn, cost).Count == 0;

        // The consumed resources that are NOT yet in surplus — the diagnostic form of IsOpenFor.
        internal static List<ResourceType> ClosedResources(PlayerSetupData player, int turn,
            ResourceCost cost)
        {
            var closed = new List<ResourceType>();
            State state = null;
            bool observed = player != null && ByPlayer.TryGetValue(player, out state)
                && state.LastTurn == turn;
            foreach (ResourceType t in ResourceBundle.All)
            {
                if ((cost?.Get(t) ?? 0) <= 0)
                    continue;
                if (!observed || !state.Streak.TryGetValue(t, out int streak)
                    || streak < AiConfigV2.devInvestmentSurplusTurns)
                    closed.Add(t);
            }
            return closed;
        }

        // Which resources are open this turn, e.g. "H-EM" — the admission fingerprint's key.
        internal static string OpenMask(PlayerSetupData player, int turn)
        {
            if (player == null || !ByPlayer.TryGetValue(player, out State s) || s.LastTurn != turn)
                return "----";
            return string.Concat(ResourceBundle.All.Select(t =>
                s.Streak.TryGetValue(t, out int streak)
                && streak >= AiConfigV2.devInvestmentSurplusTurns ? Abbrev(t) : "-"));
        }

        internal static string Abbrev(ResourceType t) => t switch
        {
            ResourceType.Human => "H",
            ResourceType.Energy => "E",
            ResourceType.Materials => "M",
            _ => "T",
        };
    }
}
