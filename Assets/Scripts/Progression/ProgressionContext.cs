using System;
using System.Linq;
using Game.Cards;
using Game.Core;

namespace Game.Progression
{
    // Scene-independent composition root for the local profile, without gameplay or UI ownership.
    public static class ProgressionContext
    {
        public static CollectionService Collection { get; private set; }
        public static RewardService Rewards { get; private set; }
        public static string Notice { get; private set; }
        public static string Error { get; private set; }
        [UnityEngine.RuntimeInitializeOnLoadMethod(UnityEngine.RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void Reset() { Collection = null; Rewards = null; Error = null; Notice = null; }
        public static bool Initialize(IProgressionContent content)
            => Initialize(content, () => new CollectionProfileStore(UnityEngine.Application.persistentDataPath));

        internal static bool Initialize(IProgressionContent content, Func<CollectionProfileStore> openStore)
        {
            if (Collection != null) return true;
            try
            {
                var starting = content?.CollectionDeckCatalog;
                var research = content?.CollectionResearchCatalog;
                if (starting == null || research == null)
                    throw new InvalidOperationException("Collection catalogs are not configured.");
                var store = openStore();
                var loaded = store.Load(out string notice);
                var service = new CollectionService(new DeckRules(starting, research), store, loaded ?? new CollectionProfile());
                if (loaded == null && !service.InitializeStarters(out string failure)) throw new InvalidOperationException(failure);
                if (loaded != null && !service.EnsureStarterDecks(out string blueprintFailure)) throw new InvalidOperationException(blueprintFailure);
                var obsolete = service.Snapshot.ownedCards.Where(e => service.Rules.Resolve(e.cardKey) == null).Select(e => e.cardKey).ToList();
                Notice = notice;
                if (obsolete.Count > 0) Notice = (notice ?? "") + $"\n{obsolete.Count} collection cards are currently unavailable. Your ownership is retained.";
                if (service.StarterIssues.Count > 0) Notice = (Notice ?? "") + "\nSome unavailable cards were skipped in starter decks.";
                Rewards = new RewardService(service);
                Collection = service;
                Error = null;
                return true;
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogWarning(ex);
                Error = ex is NotSupportedException ? "Update the game to load your collection."
                    : "Could not load your collection. Try again.";
                return false;
            }
        }
    }
}
