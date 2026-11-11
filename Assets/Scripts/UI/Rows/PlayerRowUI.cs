using System;
using System.Collections.Generic;
using System.Linq;
using Game.Cards;
using Game.Players;
using Game.Styles;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Game.UI
{
    // One player's row in the game setup panel: nickname (editable), colour, faction,
    // human/AI, and a remove button. Build the actual UI hierarchy as a prefab in the editor
    // and wire these references — this script only owns the behaviour.
    public class PlayerRowUI : MonoBehaviour
    {
        // Player-selectable options for the faction dropdown, in display order. Dropdown option
        // index is mapped through this array rather than cast directly to/from Faction, since
        // Faction's own enum values don't run consecutively from 0 (None/Neutral sit in between
        // and aren't player-selectable) — see Faction.cs.
        private static readonly Faction[] SelectableFactions =
        {
            Faction.IronConcord,
            Faction.Ashen,
            Faction.Vessels,
            Faction.Random
        };

        private static string FactionLabel(Faction faction) => faction switch
        {
            Faction.IronConcord => "Iron Concord",
            Faction.Ashen => "The Ashen",
            Faction.Vessels => "The Vessels",
            Faction.Random => "Random",
            _ => faction.ToString()
        };

        [SerializeField] private TMP_InputField nicknameField;
        [SerializeField] private Image colorSwatch;
        [SerializeField] private TMP_Dropdown colorDropdown;
        [SerializeField] private TMP_Dropdown factionDropdown;
        [SerializeField] private StartingDeckCatalog startingDeckCatalog;
        [SerializeField] private TMP_Dropdown controllerDropdown;
        [SerializeField] private Button removeButton;
        [SerializeField] private GameObject deckSelectionRoot;
        [SerializeField] private TMP_Dropdown deckDropdown;
        [SerializeField] private Button editDecksButton;

        // Colour dropdown only ever lists colours not taken by another row, so its option
        // positions don't line up 1:1 with PlayerColorPalette indices — this maps
        // "dropdown option position" -> "actual palette index" for whatever list was last built.
        private readonly List<int> _colorOptionIndices = new List<int>();
        private Action _onChanged;

        private Game.Core.GameConfig deckConfig;
        private bool refreshingDecks;
        public void ConfigureDeckSelection(Game.Core.GameConfig config)
        {
            deckConfig = config;
            if (deckDropdown == null) return;
            deckDropdown.onValueChanged.RemoveAllListeners();
            deckDropdown.onValueChanged.AddListener(index =>
            {
                if (refreshingDecks) return;
                if (!Data.IsHuman)
                {
                    // Options: Random, authored AI decks, then the player's collection decks.
                    var options = AuthoredAiDecks();
                    var custom = AiCollectionDecks();
                    Data.AiDeckName = index > 0 && index <= options.Count ? options[index - 1].deckName : null;
                    int customIndex = index - 1 - options.Count;
                    Data.SelectedDeckId = customIndex >= 0 && customIndex < custom.Count ? custom[customIndex].deckId : null;
                    return;
                }
                var decks = Game.Progression.ProgressionContext.Collection.Decks(Data.Faction);
                if (index >= 0 && index < decks.Count) Data.SelectedDeckId = decks[index].deckId;
            });
            if (editDecksButton != null)
            {
                Game.UI.UIUtility.ResetRuntimeListeners(editDecksButton);
                editDecksButton.onClick.AddListener(() =>
                {
                    var menu = UnityEngine.Object.FindAnyObjectByType<MainMenuController>();
                    if (menu != null) menu.OpenDeckBuilderFromSetup(RefreshDeckOptions, Data.Faction, Data.SelectedDeckId);
                });
            }
            RefreshDeckOptions();
        }
        internal void RefreshResolvedFaction()
        {
            factionDropdown?.SetValueWithoutNotify(Mathf.Max(0, Array.IndexOf(SelectableFactions, Data.Faction)));
            RefreshDeckOptions();
        }
        public void RefreshDeckOptions()
        {
            if (deckDropdown == null) return;
            refreshingDecks = true;
            if (deckSelectionRoot != null) deckSelectionRoot.SetActive(true);
            deckDropdown.ClearOptions();
            if (editDecksButton != null) editDecksButton.gameObject.SetActive(Data.IsHuman);
            if (!Data.IsHuman)
            {
                var options = AuthoredAiDecks();
                var custom = AiCollectionDecks();
                int selected = options.FindIndex(d => d.deckName == Data.AiDeckName);
                int customSelected = selected >= 0 ? -1 : custom.FindIndex(d => d.deckId == Data.SelectedDeckId);
                if (selected < 0) Data.AiDeckName = null;
                if (customSelected < 0) Data.SelectedDeckId = null;
                var profile = Game.Progression.ProgressionContext.Collection;
                deckDropdown.AddOptions(new List<string> { "Random" }
                    .Concat(options.Select(d => d.deckName))
                    .Concat(custom.Select(d => "My deck: " + DeckLabel(profile, d))).ToList());
                deckDropdown.interactable = options.Count + custom.Count > 0;
                deckDropdown.SetValueWithoutNotify(customSelected >= 0 ? 1 + options.Count + customSelected : selected + 1);
                deckDropdown.RefreshShownValue();
                refreshingDecks = false; return;
            }
            Data.AiDeckName = null;
            if (Data.Faction == Faction.Random)
            { Data.SelectedDeckId = null; deckDropdown.AddOptions(new List<string> { "Starter Deck" }); deckDropdown.interactable = false; refreshingDecks = false; return; }
            if (!Game.Progression.ProgressionContext.Initialize(deckConfig))
            { deckDropdown.AddOptions(new List<string> { Game.Progression.ProgressionContext.Error }); refreshingDecks = false; return; }
            var collection = Game.Progression.ProgressionContext.Collection;
            var decks = collection.Decks(Data.Faction);
            if (!decks.Any(d => d.deckId == Data.SelectedDeckId)) Data.SelectedDeckId = collection.DefaultDeck(Data.Faction)?.deckId;
            deckDropdown.AddOptions(decks.Count == 0 ? new List<string> { "No saved decks - open editor" }
                : decks.Select(d => DeckLabel(collection, d)).ToList());
            deckDropdown.SetValueWithoutNotify(Mathf.Max(0, decks.FindIndex(d => d.deckId == Data.SelectedDeckId)));
            deckDropdown.interactable = decks.Count > 0;
            refreshingDecks = false;
        }

        private static string DeckLabel(Game.Progression.CollectionService collection, Game.Progression.SavedDeck d)
        {
            var validation = collection.Rules.Validate(d, collection.Owned);
            return d.name + " - " + validation.Points + "/100" + (validation.IsValid ? "" : " - " + string.Join("; ", validation.Errors));
        }

        private List<Game.Cards.StartingDeck> AuthoredAiDecks() =>
            startingDeckCatalog?.GetAiDecks(Data.Faction).ToList() ?? new List<Game.Cards.StartingDeck>();

        // The human's saved decks are offered to an AI row only for a concrete faction; Random
        // faction/deck never resolves to them (GameSession.AssignAiDecks picks authored decks).
        private List<Game.Progression.SavedDeck> AiCollectionDecks()
        {
            if (Data.Faction == Faction.Random || !Game.Progression.ProgressionContext.Initialize(deckConfig))
                return new List<Game.Progression.SavedDeck>();
            return Game.Progression.ProgressionContext.Collection.Decks(Data.Faction);
        }

        public PlayerSetupData Data { get; private set; }

        public void Bind(PlayerSetupData data, Action onChanged, Action<PlayerRowUI> onRemoveRequested)
        {
            Data = data;
            _onChanged = onChanged;

            if (nicknameField != null)
            {
                nicknameField.text = data.Nickname;
                nicknameField.onValueChanged.RemoveAllListeners();
                nicknameField.onValueChanged.AddListener(value =>
                {
                    Data.Nickname = value;
                    _onChanged?.Invoke();
                });
            }

            SetupColorDropdownListener();
            SetupFactionDropdown();
            SetupControllerDropdown();
            RefreshColorSwatch();

            if (removeButton != null)
            {
                Game.UI.UIUtility.ResetRuntimeListeners(removeButton);
                removeButton.onClick.AddListener(() => onRemoveRequested?.Invoke(this));
            }
        }

        // Called by the controller after any row changes, so every row's colour list stays in
        // sync with what everyone else currently has picked. excludedIndices = colours taken
        // by OTHER rows (this row's own current colour is always kept available/selected).
        public void RefreshAvailableColors(ICollection<int> excludedIndices)
        {
            if (colorDropdown == null) return;

            _colorOptionIndices.Clear();
            var optionLabels = new List<string>();
            for (int i = 0; i < PlayerColorPalette.Colors.Length; i++)
            {
                if (i == PlayerColorPalette.NeutralColorIndex)
                    continue;
                if (excludedIndices.Contains(i) && i != Data.ColorIndex)
                    continue;
                _colorOptionIndices.Add(i);
                optionLabels.Add(PlayerColorPalette.Names[i]);
            }

            colorDropdown.ClearOptions();
            colorDropdown.AddOptions(optionLabels);

            int selectedOption = _colorOptionIndices.IndexOf(Data.ColorIndex);
            colorDropdown.SetValueWithoutNotify(Mathf.Max(selectedOption, 0));
        }

        private void SetupColorDropdownListener()
        {
            if (colorDropdown == null) return;

            colorDropdown.onValueChanged.RemoveAllListeners();
            colorDropdown.onValueChanged.AddListener(optionIndex =>
            {
                if (optionIndex < 0 || optionIndex >= _colorOptionIndices.Count) return;
                Data.ColorIndex = _colorOptionIndices[optionIndex];
                RefreshColorSwatch();
                _onChanged?.Invoke();
            });
        }

        private void SetupFactionDropdown()
        {
            if (factionDropdown == null) return;

            factionDropdown.ClearOptions();
            factionDropdown.AddOptions(SelectableFactions.Select(FactionLabel).ToList());
            int selectedOption = Array.IndexOf(SelectableFactions, Data.Faction);
            factionDropdown.SetValueWithoutNotify(Mathf.Max(selectedOption, 0));
            factionDropdown.onValueChanged.RemoveAllListeners();
            factionDropdown.onValueChanged.AddListener(value =>
            {
                if (value < 0 || value >= SelectableFactions.Length) return;
                Data.Faction = SelectableFactions[value];
                Data.SelectedDeckId = null; Data.AiDeckName = null; RefreshDeckOptions();
                _onChanged?.Invoke();
            });
        }

        private void SetupControllerDropdown()
        {
            if (controllerDropdown == null) return;

            controllerDropdown.ClearOptions();
            controllerDropdown.AddOptions(new List<string> { "Human", "AI" });
            controllerDropdown.SetValueWithoutNotify(Data.IsHuman ? 0 : 1);
            controllerDropdown.onValueChanged.RemoveAllListeners();
            controllerDropdown.onValueChanged.AddListener(value =>
            {
                Data.IsHuman = value == 0;
                // A collection deck reaches an AI row only by an explicit pick on that row.
                Data.SelectedDeckId = null; Data.AiDeckName = null;
                RefreshDeckOptions();
                _onChanged?.Invoke();
            });
        }

        private void RefreshColorSwatch()
        {
            if (colorSwatch != null)
                colorSwatch.color = PlayerColorPalette.Colors[Data.ColorIndex];
        }
    }
}
