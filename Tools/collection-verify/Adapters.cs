// Test-only dependencies for the production collection transactions and DeckRules.
// Native Unity serialization and scenes require the project's Unity editor.
using System;
using System.Collections.Generic;
using System.Linq;
namespace UnityEngine
{
    public class Object { public static void DestroyImmediate(Object value) { } }
    public class ScriptableObject : Object { public static T CreateInstance<T>() where T : ScriptableObject, new() => new T(); }
    public static class Debug { public static void LogWarning(object message) { } public static void LogWarning(object message, object context) { } }
}
namespace Game.Campaign { public sealed class CampaignOperation { } }
namespace Game.Players
{
    public enum Faction { None, Neutral, IronConcord, Ashen, Vessels }
    public sealed class PlayerSetupData { public Faction Faction; public bool IsHuman; }
}
namespace Game.Cards
{
    public enum CardType { Hero, Unit, Base, Facility, Equipment, Tactic }
    public enum AttachmentSlot { None, Equipment, Mutator }
    public enum ResearchProductionMode { Production, Research }
    public sealed class Cost { public int human, energy, materials, tech; }
    public sealed class CardDefinition { public string authoredKey, displayName; public int deckCopyLimit = 4, deckPointCost, fate, apCost; public bool deckBuilderExcluded; public CardType cardType; public AttachmentSlot attachmentSlot; public Game.Players.Faction faction; public Cost resourceCost = new Cost(); }
    public sealed class DeckCardEntry { public string cardKey; public int count; }
    public sealed class StartingDeckDefinition { public string deckName; public List<DeckCardEntry> cards = new List<DeckCardEntry>(); }
    public sealed class FactionCardCatalog : UnityEngine.ScriptableObject { public Game.Players.Faction faction; public List<CardDefinition> cards = new List<CardDefinition>(); }
    public sealed class StartingDeckCatalog : UnityEngine.ScriptableObject
    {
        public List<FactionCardCatalog> catalogs = new List<FactionCardCatalog>();
        public FactionCardCatalog GetCatalog(Game.Players.Faction f) => catalogs.FirstOrDefault(c => c.faction == f);
        public StartingDeckDefinition GetDeck(Game.Players.Faction f) => null;
        public CardDefinition ResolveCard(string key) => catalogs.SelectMany(c => c.cards).FirstOrDefault(c => c.authoredKey == key);
    }
    public sealed class ResearchProductionCatalog : UnityEngine.ScriptableObject
    {
        public List<FactionCardCatalog> cardCatalogs = new List<FactionCardCatalog>();
        public CardDefinition ResolveCard(string key) => cardCatalogs.SelectMany(c => c.cards).FirstOrDefault(c => c.authoredKey == key);
        public IEnumerable<CardDefinition> ResolveFor(ResearchProductionMode mode, Game.Players.Faction faction) => Enumerable.Empty<CardDefinition>();
    }
}
