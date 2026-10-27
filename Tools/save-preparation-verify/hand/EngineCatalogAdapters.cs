// Managed verification adapters ONLY. Never import this file into Assets or player builds.
// The production card/hand/registry/snapshot/quota code is compiled unchanged by run.py.
// Catalog, definition DTO and engine surfaces below only satisfy that slice's dependencies.
// Untested catalog/production/random operations throw instead of reporting invented behavior.
using System;
using System.Collections.Generic;

namespace UnityEngine
{
    public static class Mathf
    {
        public static int Max(int a, int b) => Math.Max(a, b);
        public static int Clamp(int value, int min, int max) => Math.Clamp(value, min, max);
    }
    public static class Random
    {
        public static int Range(int min, int max) =>
            throw new NotSupportedException("The hand snapshot checks must not consume randomness.");
    }
}
namespace Game.Players
{
    public enum Faction { None, Neutral, IronConcord, Ashen, Vessels }
    public class PlayerSetupData
    {
        public Faction Faction;
        public string AiDeckName, AiGrantedLeaderKey;
    }
}
namespace Game.Cards
{
    public enum CardType { Unit, Hero, Base, Facility, Equipment, Tactic }
    public class ResourceCost { public int human, energy, materials, tech; }
    public class CardDefinition
    {
        public string authoredKey;
        public CardType cardType;
        public bool isAviation;
        public int moveMax, apCost, activationApCost;
        public ResourceCost resourceCost;
        public List<string> grantedAbilities = new List<string>();
    }
    public static class UnitAbilities { public const string RapidReaction = "rapid"; }
    public static class AbilityParams
    {
        public static int GetBestRecceRadius(List<string> abilities) =>
            throw new NotSupportedException("Opening hand selection requires the Unity suite.");
    }
    public sealed class StartingDeckCatalog
    {
        public List<CardDefinition> BuildAiDeckPool(Game.Players.Faction faction, string name, string key) =>
            throw new NotSupportedException("Authored catalog loading requires the Unity suite.");
        public bool TryBuildDeckPool(List<DeckCardEntry> entries, out List<CardDefinition> pool, out string error) =>
            throw new NotSupportedException("Deck construction requires the Unity suite.");
    }
    public class DeckCardEntry { public string cardKey; public int count; }
    public sealed class DeckRules
    {
        public StartingDeckCatalog Starting;
        public Validation Validate(Game.Progression.SavedDeck source, Func<string, int> ownership) =>
            throw new NotSupportedException("Deck validation requires the Unity suite.");
        public CardDefinition Resolve(string key) =>
            throw new NotSupportedException("Card catalog resolution requires the Unity suite.");
    }
    public sealed class Validation { public bool IsValid; public List<string> Errors; }
    public static class ResearchProductionSystem
    {
        public static CardData MintCard(CardDefinition definition) =>
            throw new NotSupportedException("Production execution is outside these hand snapshot checks.");
    }
}
namespace Game.Map
{
    public static class ArmyActions
    {
        public static int EffectiveDeployApCost(Game.Cards.CardDefinition card) =>
            throw new NotSupportedException("Deploy cost calculation requires the gameplay suite.");
    }
}
namespace UnityEngine
{
    public static class JsonUtility
    {
        public static string ToJson(object value) => throw new NotSupportedException("Profile JSON requires the collection/Unity suites.");
        public static T FromJson<T>(string value) => throw new NotSupportedException("Profile JSON requires the collection/Unity suites.");
    }
}
