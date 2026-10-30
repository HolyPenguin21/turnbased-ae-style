using System;
using System.Collections.Generic;
using Game.Progression;
using Game.Players;
using Game.Map;

namespace Game.Cards
{
    // Immutable by ownership: mutable DTOs are never exposed or shared with the saved profile.
    public sealed class MatchLoadout
    {
        private readonly SavedDeck deck;
        private readonly Dictionary<string, int> owned = new Dictionary<string, int>();
        public Faction Faction => deck.faction;
        public MatchLoadout(SavedDeck source, DeckRules rules, Func<string, int> ownership)
        {
            var validation = rules.Validate(source, ownership);
            if (!validation.IsValid) throw new InvalidOperationException(string.Join("\n", validation.Errors));
            deck = CollectionProfile.CopyDeck(source);
            // Unknown cards are only warnings in Validate; they never reach the match.
            deck.mainCards.RemoveAll(e => rules.Resolve(e.cardKey) == null);
            deck.equipment.RemoveAll(e => rules.Resolve(e.cardKey) == null);
            deck.mutators.RemoveAll(e => rules.Resolve(e.cardKey) == null);
            foreach (var e in deck.mainCards) owned[e.cardKey] = e.count;
            foreach (var e in deck.equipment) owned[e.cardKey] = e.count;
            foreach (var e in deck.mutators) owned[e.cardKey] = e.count;
        }
        public bool TryBuildPool(DeckRules rules, out List<CardDefinition> pool, out string error)
        {
            var validation = rules.Validate(deck, key => owned.TryGetValue(key, out int n) ? n : 0);
            if (!validation.IsValid) { pool = null; error = string.Join("\n", validation.Errors); return false; }
            return rules.Starting.TryBuildDeckPool(deck.mainCards, out pool, out error);
        }
        // A match saves its frozen starting loadout, never the currently selected profile deck.
        // The existing profile copy boundary also detaches the saved match from profile edits.
        public SavedDeck CaptureState() => CollectionProfile.CopyDeck(deck);

        public static MatchLoadout FromState(SavedDeck state, DeckRules rules)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            var savedOwnership = new Dictionary<string, int>();
            foreach (var entries in new[] { state.mainCards, state.equipment, state.mutators })
                if (entries != null)
                    foreach (var entry in entries)
                        if (entry != null && !string.IsNullOrEmpty(entry.cardKey))
                        {
                            if (rules?.Resolve(entry.cardKey) == null)
                                throw new InvalidOperationException("Cannot resolve saved loadout card: " + entry.cardKey);
                            savedOwnership[entry.cardKey] = entry.count;
                        }
            return new MatchLoadout(state, rules, key => savedOwnership.TryGetValue(key, out int n) ? n : 0);
        }

        internal List<DeckCardEntry> BlueprintEntries()
        {
            var result = new List<DeckCardEntry>();
            foreach (var e in deck.equipment) result.Add(new DeckCardEntry { cardKey = e.cardKey, count = e.count });
            foreach (var e in deck.mutators) result.Add(new DeckCardEntry { cardKey = e.cardKey, count = e.count });
            return result;
        }
    }
    // Runtime quotas have no reference to the profile. Each participant owns a separate instance.
    public sealed class BlueprintQuota
    {
        private readonly HashSet<string> unlocked = new HashSet<string>();
        private readonly HashSet<string> pending = new HashSet<string>();
        public BlueprintQuota(MatchLoadout loadout) { foreach (var e in loadout.BlueprintEntries()) if (e.count > 0) unlocked.Add(e.cardKey); }
        private BlueprintQuota() { }
        public BlueprintQuotaState CaptureState()
        {
            var state = new BlueprintQuotaState();
            state.unlockedKeys.AddRange(unlocked);
            state.pendingKeys.AddRange(pending);
            state.unlockedKeys.Sort(StringComparer.Ordinal);
            state.pendingKeys.Sort(StringComparer.Ordinal);
            return state;
        }
        public static BlueprintQuota FromState(BlueprintQuotaState state)
        {
            if (state?.unlockedKeys == null || state.pendingKeys == null)
                throw new InvalidOperationException("Invalid saved blueprint quota.");
            var quota = new BlueprintQuota();
            foreach (var key in state.unlockedKeys)
                if (string.IsNullOrEmpty(key) || !quota.unlocked.Add(key))
                    throw new InvalidOperationException("Invalid saved blueprint identity.");
            foreach (var key in state.pendingKeys)
                if (!quota.Unlocked(key) || !quota.pending.Add(key))
                    throw new InvalidOperationException("Pending blueprint is missing or duplicated.");
            return quota;
        }
        // A blueprint in the deck unlocks unlimited production; only one attempt per card may be in flight.
        public bool Unlocked(string key) => unlocked.Contains(key ?? "");
        public bool Available(string key) => Unlocked(key) && !pending.Contains(key);
        internal bool TryReserve(string key) { if (!Available(key)) return false; return pending.Add(key); }
        internal void Release(string key) { pending.Remove(key); }
    }
    [Serializable]
    public sealed class BlueprintQuotaState
    {
        public List<string> unlockedKeys = new List<string>();
        public List<string> pendingKeys = new List<string>();
    }
    public sealed class ProductionAttempt : IDisposable
    {
        private readonly CardDefinition card;
        private readonly BlueprintQuota quota;
        private readonly CardDefinition output;
        private bool finished;
        private readonly PlayerSetupData owner;
        private readonly PlayerRoot account;
        private readonly PlayerHandData destination;
        private readonly string blueprintKey;
        internal ProductionAttempt(CardDefinition card, BlueprintQuota quota, CardDefinition output = null,
            PlayerSetupData owner = null, PlayerRoot account = null, PlayerHandData destination = null)
        {
            this.card = card; this.quota = quota; this.output = output ?? card;
            this.owner = owner; this.account = account; this.destination = destination;
            blueprintKey = card.authoredKey;
        }
        private bool IsCurrent => owner == null || (!owner.IsEliminated
            && ReferenceEquals(PlayerRootRegistry.FindFor(owner), account)
            && (destination == null || ReferenceEquals(owner.Hand, destination)));
        public CardData Complete(bool success)
        {
            if (finished) return null;
            finished = true; quota?.Release(blueprintKey);
            return success && IsCurrent ? ResearchProductionSystem.MintCard(output) : null;
        }
        // Application completion owns both terminal mint and delivery. Legacy Complete keeps
        // its return-a-card contract for existing callers; the same terminal flag gates both.
        public CardData CompleteToHand(bool success)
        {
            using var mutation = VisionSystem.BeginMutation();
            if (destination == null) { Complete(false); return null; }
            var produced = Complete(success);
            return produced != null && destination.TryAddCard(produced) ? produced : null;
        }
        public void Dispose() { Complete(false); }
    }
}
