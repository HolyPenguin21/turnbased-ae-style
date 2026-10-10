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
        public static bool Initialize(GameConfig config)
        {
            if (Collection != null) return true;
            try
            {
                if (config == null || config.collectionDeckCatalog == null || config.collectionResearchCatalog == null)
                    throw new InvalidOperationException("Collection catalogs are not configured.");
                var store = new CollectionProfileStore(UnityEngine.Application.persistentDataPath);
                var loaded = store.Load(out string notice);
                var service = new CollectionService(new DeckRules(config.collectionDeckCatalog, config.collectionResearchCatalog), store, loaded ?? new CollectionProfile());
                if (loaded == null && !service.Transact(service.InitializeStarters, out string failure)) throw new InvalidOperationException(failure);
                if (loaded != null && !service.EnsureStarterBlueprintOwnership(out string blueprintFailure)) throw new InvalidOperationException(blueprintFailure);
                Collection = service; Rewards = new RewardService(service);
                var obsolete = service.Snapshot.ownedCards.Where(e => service.Rules.Resolve(e.cardKey) == null).Select(e => e.cardKey).ToList();
                Notice = notice;
                if (obsolete.Count > 0) Notice = (notice ?? "") + "\nObsolete card identities retained: " + string.Join(", ", obsolete);
                Error = null;
                return true;
            }
            catch (Exception ex) { Error = ex.Message; return false; }
        }
    }
}
