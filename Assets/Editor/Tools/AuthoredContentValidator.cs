using System;
using System.Collections.Generic;
using System.Linq;
using Game.Aviation;
using Game.Cards;
using Game.Core;
using Game.Economy;
using Game.Players;
using Game.Saves;
using Game.UI;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Game.EditorTools
{
    internal sealed class AuthoredContentGraph
    {
        internal IReadOnlyDictionary<Object, string> Sources { get; }
        internal IReadOnlyDictionary<EventCatalog, IReadOnlyList<NeutralArmyCatalog>> EventGuards { get; }
        internal IReadOnlyDictionary<Object, IReadOnlyList<GameConfig>> MatchScopes { get; }

        internal AuthoredContentGraph(IReadOnlyDictionary<Object, string> sources,
            IReadOnlyDictionary<EventCatalog, IReadOnlyList<NeutralArmyCatalog>> eventGuards = null,
            IReadOnlyDictionary<Object, IReadOnlyList<GameConfig>> matchScopes = null)
        {
            Sources = sources;
            EventGuards = eventGuards ?? new Dictionary<EventCatalog, IReadOnlyList<NeutralArmyCatalog>>();
            MatchScopes = matchScopes ?? new Dictionary<Object, IReadOnlyList<GameConfig>>();
        }

        internal IEnumerable<T> Assets<T>() where T : Object => Sources.Keys.OfType<T>();
    }

    internal readonly struct ContentIssue
    {
        internal readonly Object Asset;
        internal readonly string Key;
        internal readonly string Path;
        internal readonly string Cause;

        internal ContentIssue(Object asset, string key, string path, string cause)
        {
            Asset = asset;
            Key = key;
            Path = path;
            Cause = cause;
        }

        public override string ToString() => $"{Path} [{Key ?? "<missing key>"}] {Cause}";
    }

    // Validation inspects only the supplied assets. It does not load assets, log or initialize progression.
    internal sealed class AuthoredContentValidator
    {
        private readonly AuthoredContentGraph graph;
        private readonly List<ContentIssue> issues = new List<ContentIssue>();
        private readonly Dictionary<object, CardContentIndex> indices = new Dictionary<object, CardContentIndex>();
        private readonly Dictionary<GameConfig, CardContentIndex> matchIndices = new Dictionary<GameConfig, CardContentIndex>();

        private AuthoredContentValidator(AuthoredContentGraph graph) => this.graph = graph;

        internal static IReadOnlyList<ContentIssue> Validate(AuthoredContentGraph graph)
        {
            var validator = new AuthoredContentValidator(graph);
            validator.ValidateAll();
            return validator.issues.AsReadOnly();
        }

        private void Issue(Object asset, string key, string field, string cause)
        {
            graph.Sources.TryGetValue(asset, out string path);
            issues.Add(new ContentIssue(asset, key, $"{path ?? asset.name}/{field}", cause));
        }

        private static IEnumerable<T> Rows<T>(IEnumerable<T> rows) => rows ?? Enumerable.Empty<T>();

        private CardDefinition Resolve(Object owner, IEnumerable<FactionCardCatalog> catalogs, string key, bool legacy)
        {
            if (!indices.TryGetValue(owner, out var index))
                indices.Add(owner, index = new CardContentIndex(CardContentIndex.Definitions(catalogs)));
            return index.Resolve(catalogs, key, legacy);
        }

        private void ValidateMatchReference(Object owner, string key, string field, CardDefinition card,
            IEnumerable<GameConfig> configs)
        {
            foreach (var config in configs)
            {
                if (!matchIndices.TryGetValue(config, out var index))
                    matchIndices.Add(config, index = new CardContentIndex(MatchContentResolver.MatchDefinitions(config)));
                if (!ReferenceEquals(index.Find(card.authoredKey, out _), card))
                {
                    graph.Sources.TryGetValue(config, out string path);
                    Issue(owner, key, field, $"Card cannot be saved through match catalog '{path ?? config.name}'.");
                }
            }
        }

        private IReadOnlyList<GameConfig> MatchScopes(Object owner)
            => graph.MatchScopes.TryGetValue(owner, out var configs) ? configs : Array.Empty<GameConfig>();

        private void ValidateAll()
        {
            var cards = new HashSet<string>(StringComparer.Ordinal);
            foreach (var catalog in graph.Assets<FactionCardCatalog>())
            {
                if (catalog.cards == null)
                    Issue(catalog, null, nameof(catalog.cards), "Missing card definitions.");
                foreach (var (card, i) in Rows(catalog.cards).Select((row, index) => (row, index)))
                {
                    string field = $"cards[{i}]";
                    if (card == null) { Issue(catalog, null, field, "Missing card definition."); continue; }
                    if (string.IsNullOrWhiteSpace(card.authoredKey) || !cards.Add(card.authoredKey))
                        Issue(catalog, card.authoredKey, field, "Missing or duplicate authored card identity.");
                    ValidateCard(catalog, card, field);
                }
            }

            foreach (var catalog in graph.Assets<NeutralArmyCatalog>()) ValidateArmies(catalog);
            var eventKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var catalog in graph.Assets<EventCatalog>()) ValidateEvents(catalog, eventKeys);
            foreach (var catalog in graph.Assets<ResearchProductionCatalog>()) ValidateResearch(catalog);
            foreach (var catalog in graph.Assets<UnitAbilityCatalog>()) ValidateAbilities(catalog);
            foreach (var catalog in graph.Assets<GameAssetCatalog>()) ValidateRuntimeAssets(catalog);

            var configuredDecks = new HashSet<(StartingDeckCatalog, ResearchProductionCatalog)>();
            foreach (var config in graph.Assets<GameConfig>())
            {
                if (config.collectionDeckCatalog == null) Issue(config, null, "collectionDeckCatalog", "Missing collection deck catalog.");
                if (config.collectionResearchCatalog == null) Issue(config, null, "collectionResearchCatalog", "Missing research/production catalog.");
                if (config.collectionDeckCatalog != null && config.collectionResearchCatalog != null
                    && configuredDecks.Add((config.collectionDeckCatalog, config.collectionResearchCatalog)))
                {
                    var starting = config.collectionDeckCatalog;
                    var research = config.collectionResearchCatalog;
                    ValidateDecks(new DeckRules(starting, research,
                        key => Resolve(starting, starting.catalogs, key, true),
                        key => Resolve(research, research.cardCatalogs, key, false)));
                }
                var matchKeys = new HashSet<string>(CardContentIndex.Definitions(config.collectionDeckCatalog?.catalogs)
                    .Where(card => card != null).Select(card => card.authoredKey), StringComparer.Ordinal);
                foreach (var (card, i) in Rows(config.extractionFacilityCards).Select((row, index) => (row, index)))
                {
                    string field = $"extractionFacilityCards[{i}]";
                    if (card == null || card.cardType != CardType.Facility)
                        Issue(config, card?.authoredKey, field, "Expected an extraction facility.");
                    if (card == null) continue;
                    // Unkeyed legacy extractors are snapshot-based, not catalog references.
                    if (!string.IsNullOrWhiteSpace(card.authoredKey) && !matchKeys.Add(card.authoredKey))
                        Issue(config, card.authoredKey, field, "Duplicate match card identity.");
                    ValidateCard(config, card, field);
                }
                ValidateUpgradeTiers(config, config.baseUpgradeTiers, nameof(config.baseUpgradeTiers));
                ValidateUpgradeTiers(config, config.facilityUpgradeTiers, nameof(config.facilityUpgradeTiers));
            }
        }

        private void ValidateUpgradeTiers(GameConfig config, IEnumerable<BaseUpgradeTier> tiers, string field)
        {
            foreach (var (tier, i) in Rows(tiers).Select((row, index) => (row, index)))
                if (tier == null || tier.apCost < 0 || Negative(tier.cost))
                    Issue(config, null, $"{field}[{i}]", "Missing tier or negative upgrade price.");
        }

        private static bool Negative(ResourceCost cost)
            => cost != null && (cost.human < 0 || cost.energy < 0 || cost.materials < 0 || cost.tech < 0);

        private void ValidateCard(Object asset, CardDefinition card, string field)
        {
            string key = card.authoredKey;
            if (!Enum.IsDefined(typeof(CardType), card.cardType)) Issue(asset, key, field, "Unknown card type.");
            if (card.apCost < 0 || Negative(card.resourceCost) || card.deckPointCost < 0 || card.deckCopyLimit < 0 || card.launchEnergyCost < 0)
                Issue(asset, key, field, "Negative card price or copy limit.");
            if (card.cardType == CardType.Tactic && (card.tacticAmount <= 0 || !Enum.IsDefined(typeof(TacticEffect), card.tacticEffect)))
                Issue(asset, key, field, "Invalid tactic effect or amount.");
            ValidateTags(asset, key, field + ".grantedAbilities", card.grantedAbilities);
            if (!string.IsNullOrEmpty(card.requiredBuildingAbility))
                ValidateTags(asset, key, field + ".requiredBuildingAbility", new[] { card.requiredBuildingAbility });
            if (card.grantedAbilities?.Contains(UnitAbilities.AntiAir) == true && card.antiAirRadius < 0
                || card.grantedAbilities?.Contains(UnitAbilities.Airfield) == true && card.airfieldCapacity < 0)
                Issue(asset, key, field, "Negative anti-air radius or airfield capacity.");

            if (card.cardType != CardType.Equipment) return;
            var grant = card.equipment;
            if (!Enum.IsDefined(typeof(AttachmentSlot), card.attachmentSlot) || grant == null)
            {
                Issue(asset, key, field, "Equipment requires a supported slot and a grant.");
                return;
            }
            // Production-only blueprints can intentionally have no attachment host.
            if (Rows(grant.hostKinds).Any(kind => !Enum.IsDefined(typeof(EquipmentHostKind), kind))
                || Rows(grant.hostTypeTags).Any(tag => !Enum.IsDefined(typeof(UnitTypeTag), tag))
                || Rows(grant.clearAbilityFamilies).Any(family => !Enum.IsDefined(typeof(AbilityFamily), family))
                || Rows(grant.statChanges).Any(change => change == null || !Enum.IsDefined(typeof(EquipmentStat), change.stat)))
                Issue(asset, key, field + ".equipment", "Invalid attachment host, ability family or stat.");
            ValidateTags(asset, key, field + ".equipment.addAbilities", grant.addAbilities);
            ValidateTags(asset, key, field + ".equipment.removeAbilities", grant.removeAbilities);
        }

        private void ValidateTags(Object owner, string key, string field, IEnumerable<string> tags)
        {
            foreach (var tag in Rows(tags))
                if (!UnitAbilities.All.Contains(tag) && !AbilityParams.TryGetRecce(tag, out _, out _)
                    && !(AbilityParams.TryGetStealthLevel(tag, out int stealth) && stealth > 0)
                    && !AntiAirRules.TryGetRadius(new[] { tag }, 1, out _))
                    Issue(owner, key, field, $"Unknown ability or invalid ability parameter '{tag}'.");
        }

        private void ValidateAbilities(UnitAbilityCatalog catalog)
        {
            var multiplier = catalog.criticalDamageMultiplier;
            if (float.IsNaN(multiplier) || float.IsInfinity(multiplier) || multiplier <= 0
                || catalog.ceramicArmorReduction < 0 || catalog.berserkAttackGain < 0 || catalog.berserkDefenseLoss < 0
                || catalog.hyperkineticBonusDamage < 0 || catalog.pyrokineticBonusDamage < 0
                || catalog.ionicBonusDamage < 0 || catalog.drillAttackBonus < 0 || catalog.raiseTheRotsUnitsPerSummoner < 0)
                Issue(catalog, null, "magnitudes", "Invalid combat ability magnitudes.");
            if (catalog.raiseTheRotsUnitsPerSummoner > 0 && catalog.ResolveRaiseTheRotsCard()?.cardType != CardType.Unit)
                Issue(catalog, catalog.raiseTheRotsUnitKey, "raiseTheRotsUnitKey", "Expected a summonable unit.");
        }

        private void ValidateDecks(DeckRules rules)
        {
            var catalog = rules.Starting;
            ValidateCatalogReferences(catalog, catalog.catalogs, "catalogs");
            var starterFactions = new HashSet<Faction>();
            foreach (var deck in Rows(catalog.decks))
            {
                if (deck != null && !starterFactions.Add(deck.faction))
                    Issue(catalog, deck.deckName, "decks", "Duplicate starter faction.");
                ValidateDeck(rules, deck, false);
            }
            var leaders = new HashSet<(Faction, string)>();
            foreach (var deck in Rows(catalog.aiDecks))
            {
                if (deck != null && !leaders.Add((deck.faction, deck.deckName)))
                    Issue(catalog, deck.deckName, "aiDecks", "Duplicate AI deck name within faction.");
                ValidateDeck(rules, deck, true);
            }
        }

        private void ValidateDeck(DeckRules rules, StartingDeck deck, bool ai)
        {
            var catalog = rules.Starting;
            string field = (ai ? "aiDecks" : "decks") + "/" + deck?.deckName;
            if (deck == null || !DeckRules.PlayableFactions.Contains(deck.faction) || string.IsNullOrWhiteSpace(deck.deckName))
            { Issue(catalog, deck?.deckName, field, "Missing deck or invalid name/faction."); return; }

            var selected = new HashSet<string>(StringComparer.Ordinal);
            var main = new List<CardDefinition>();
            var blueprints = new List<CardDefinition>();
            long points = 0;
            void Check(IEnumerable<DeckCardEntry> rows, DeckCategory? category, string list)
            {
                if (rows == null) { Issue(catalog, deck.deckName, field + "/" + list, "Missing deck rows."); return; }
                foreach (var row in rows)
                {
                    var card = Resolve(catalog, catalog.catalogs, row?.cardKey, true);
                    if (card == null)
                    { Issue(catalog, row?.cardKey, field + "/" + list, "Unresolved or ambiguous deck card."); continue; }
                    var actual = DeckRules.Category(card);
                    if (!selected.Add(card.authoredKey) || !rules.Permitted(card, deck.faction)
                        || (category.HasValue && actual != category) || row.count <= 0 || row.count > DeckRules.CopyLimit(card))
                        Issue(catalog, row.cardKey, field + "/" + list, "Duplicate card, invalid copy count, faction or deck slot.");
                    if (actual == DeckCategory.Main) main.Add(card);
                    else if (actual == DeckCategory.Equipment || actual == DeckCategory.Mutator) blueprints.Add(card);
                    if (!ai || category == DeckCategory.Main) points += (long)card.deckPointCost * row.count;
                }
            }
            Check(deck.cards, ai ? DeckCategory.Main : (DeckCategory?)null, "cards");
            if (ai)
            {
                Check(deck.equipment, DeckCategory.Equipment, "equipment");
                Check(deck.mutators, DeckCategory.Mutator, "mutators");
                if (deck.cards != null && StartingDeckCatalog.ResolveAiLeader(deck, key => Resolve(catalog, catalog.catalogs, key, true)) == null)
                    Issue(catalog, deck.leaderCardKey, field + "/leaderCardKey", "Missing or incompatible AI leader.");
            }
            else
            {
                foreach (var card in blueprints)
                    if (!DeckRules.HasStarterHost(deck.faction, card, main))
                        Issue(catalog, card.authoredKey, field + "/cards", "Initial blueprint has no compatible starter host.");
            }
            if (points > DeckRules.MaximumPoints)
                Issue(catalog, deck.deckName, field, $"Deck exceeds {DeckRules.MaximumPoints} points.");
        }

        private void ValidateCatalogReferences(Object owner, IEnumerable<FactionCardCatalog> catalogs, string field)
        {
            if (catalogs == null || catalogs.Any(catalog => catalog == null))
                Issue(owner, null, field, "Missing card catalog reference.");
        }

        private void ValidateResearch(ResearchProductionCatalog catalog)
        {
            ValidateCatalogReferences(catalog, catalog.cardCatalogs, "cardCatalogs");
            var configs = graph.Assets<GameConfig>().Where(config => ReferenceEquals(config.collectionResearchCatalog, catalog)).ToArray();
            void Check(IEnumerable<ResearchProductionEntry> rows, string field)
            {
                var seen = new HashSet<(string, Faction)>();
                foreach (var row in Rows(rows))
                {
                    var card = Resolve(catalog, catalog.cardCatalogs, row?.cardKey, false);
                    if (card == null) { Issue(catalog, row?.cardKey, field, "Unresolved or ambiguous blueprint."); continue; }
                    if (!seen.Add((row.cardKey, row.factionRestriction))
                        || (row.factionRestriction != Faction.None && !DeckRules.PlayableFactions.Contains(row.factionRestriction)))
                        Issue(catalog, row.cardKey, field, "Duplicate blueprint or invalid faction restriction.");
                    var result = catalog.ResolveResult(card, key => Resolve(catalog, catalog.cardCatalogs, key, false));
                    // ResolveResult owns blueprint output policy, including equipment blueprints that produce units.
                    if (result == null)
                        Issue(catalog, row.resultCardKey ?? row.cardKey, field, "Unresolved or conflicting blueprint result.");
                    if (result != null) ValidateMatchReference(catalog, row.resultCardKey ?? row.cardKey, field, result, configs);
                }
            }
            Check(catalog.researchCards, "researchCards");
            Check(catalog.productionCards, "productionCards");
        }

        private void ValidateArmies(NeutralArmyCatalog catalog)
        {
            ValidateCatalogReferences(catalog, catalog.cardCatalogs, "cardCatalogs");
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var army in Rows(catalog.armies))
            {
                if (army == null || string.IsNullOrWhiteSpace(army.name) || !names.Add(army.name))
                    Issue(catalog, army?.name, "armies", "Missing or duplicate army identity.");
                if (army?.members == null || army.members.Count == 0)
                { Issue(catalog, army?.name, "armies/members", "Missing army members."); continue; }
                foreach (var row in army.members)
                {
                    var card = Resolve(catalog, catalog.cardCatalogs, row?.cardKey, true);
                    if (card == null || (card.cardType != CardType.Unit && card.cardType != CardType.Hero) || row.count <= 0)
                        Issue(catalog, row?.cardKey, $"armies/{army.name}/members", "Unresolved guard, wrong card type or invalid count.");
                    else ValidateMatchReference(catalog, row.cardKey, $"armies/{army.name}/members", card, MatchScopes(catalog));
                }
            }
        }

        private void ValidateEvents(EventCatalog catalog, HashSet<string> eventKeys)
        {
            ValidateCatalogReferences(catalog, catalog.cardCatalogs, "cardCatalogs");
            var guards = graph.EventGuards.TryGetValue(catalog, out var configured)
                ? configured : graph.Assets<NeutralArmyCatalog>().ToArray();
            foreach (var definition in Rows(catalog.events))
            {
                if (definition == null || string.IsNullOrWhiteSpace(definition.authoredKey) || !eventKeys.Add(definition.authoredKey))
                    Issue(catalog, definition?.authoredKey, "events", "Missing or duplicate event identity.");
                if (definition == null) continue;
                string field = $"events/{definition.authoredKey}";
                void Check(string guard, IEnumerable<RewardEntry> rewards, string source)
                {
                    if (!string.IsNullOrWhiteSpace(guard) && (guards.Count == 0 || guards.Any(army => army?.GetArmy(guard) == null)))
                        Issue(catalog, guard, source + "/guardArmyName", "Unresolved event guard.");
                    foreach (var reward in Rows(rewards))
                    {
                        if (reward == null || !Enum.IsDefined(typeof(RewardType), reward.type))
                            Issue(catalog, null, source + "/rewards", "Missing reward or invalid reward type.");
                        else if (reward.type == RewardType.Card)
                        {
                            var card = Resolve(catalog, catalog.cardCatalogs, reward.cardKey, true);
                            if (!EventCatalog.CanGrantCard(card))
                                Issue(catalog, reward.cardKey, source + "/rewards", "Unresolved or unsupported event card reward.");
                            else ValidateMatchReference(catalog, reward.cardKey, source + "/rewards", card, MatchScopes(catalog));
                        }
                        else if (reward.type == RewardType.Resources && (reward.resources == null
                            || reward.resources.human < 0 || reward.resources.energy < 0 || reward.resources.materials < 0 || reward.resources.tech < 0))
                            Issue(catalog, definition.authoredKey, source + "/rewards", "Invalid resource reward.");
                    }
                }
                Check(definition.guardArmyName, definition.rewards, field);
                foreach (var (variant, i) in Rows(definition.variants).Select((row, index) => (row, index)))
                {
                    string source = $"{field}/variants[{i}]";
                    if (variant == null || variant.resourceCount < 0)
                        Issue(catalog, definition.authoredKey, source, "Missing variant or negative resource budget.");
                    if (variant != null) Check(variant.guardArmyName, variant.rewards, source);
                }
            }
        }

        private void ValidateRuntimeAssets(GameAssetCatalog catalog)
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            var entries = catalog.Entries;
            foreach (var entry in entries)
                if (string.IsNullOrWhiteSpace(entry.key) || !keys.Add(entry.key))
                    Issue(catalog, entry.key, "entries", "Missing or duplicate runtime identity.");
            ValidatePrefab(CollectionRewardUI.RewardPanelKey, typeof(CollectionRewardView));
            ValidatePrefab(CollectionRewardUI.RewardCardKey, typeof(CollectionRewardCardView));
            ValidatePrefab(CollectionMessageUI.RewardDialogKey, typeof(CollectionRewardView));

            void ValidatePrefab(string key, Type component)
            {
                // The player loads the first GameObject for the key and uses a root component.
                var prefab = entries.FirstOrDefault(entry => entry.key == key).assets?.OfType<GameObject>().FirstOrDefault();
                if (prefab == null || prefab.GetComponent(component) == null)
                    Issue(catalog, key, "entries", $"Runtime reward prefab requires a root {component.Name}.");
            }
        }
    }
}
