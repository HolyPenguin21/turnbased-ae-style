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
        [SerializeField] private Image factionLogo;
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
                var decks = Game.Progression.ProgressionContext.Collection.Decks(Data.Faction);
                if (index >= 0 && index < decks.Count) Data.SelectedDeckId = decks[index].deckId;
            });
            if (editDecksButton != null)
            {
                Game.UI.UIButtonEventUtility.ResetRuntimeListeners(editDecksButton);
                editDecksButton.onClick.AddListener(() =>
                {
                    var menu = UnityEngine.Object.FindAnyObjectByType<MainMenuController>();
                    if (menu != null) menu.OpenDeckBuilderFromSetup(RefreshDeckOptions);
                });
            }
            RefreshDeckOptions();
        }
        internal void RefreshResolvedFaction()
        {
            factionDropdown?.SetValueWithoutNotify(Mathf.Max(0, Array.IndexOf(SelectableFactions, Data.Faction)));
            RefreshFactionLogo(); RefreshDeckOptions();
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
                // AI rows show the deck the AI will play this match (chosen randomly among its
                // faction's AI decks, see GameSession.AssignAiDecks); it is not editable.
                Data.SelectedDeckId = null;
                deckDropdown.interactable = false;
                if (Data.Faction == Faction.Random)
                { Data.AiDeckName = null; deckDropdown.AddOptions(new List<string> { "Random AI deck after Random faction resolves" }); }
                else
                {
                    if (startingDeckCatalog != null && !startingDeckCatalog.GetAiDecks(Data.Faction).Any(d => d.deckName == Data.AiDeckName))
                        Data.AiDeckName = startingDeckCatalog.PickAiDeckName(Data.Faction);
                    deckDropdown.AddOptions(new List<string> { Data.AiDeckName ?? "No AI deck — faction starter deck" });
                }
                deckDropdown.SetValueWithoutNotify(0);
                deckDropdown.RefreshShownValue();
                refreshingDecks = false; return;
            }
            Data.AiDeckName = null;
            if (Data.Faction == Faction.Random)
            { Data.SelectedDeckId = null; deckDropdown.AddOptions(new List<string> { "Default deck after Random faction resolves" }); deckDropdown.interactable = false; refreshingDecks = false; return; }
            if (!Game.Progression.ProgressionContext.Initialize(deckConfig))
            { deckDropdown.AddOptions(new List<string> { Game.Progression.ProgressionContext.Error }); refreshingDecks = false; return; }
            var collection = Game.Progression.ProgressionContext.Collection;
            var decks = collection.Decks(Data.Faction);
            if (!decks.Any(d => d.deckId == Data.SelectedDeckId)) Data.SelectedDeckId = collection.DefaultDeck(Data.Faction)?.deckId;
            deckDropdown.AddOptions(decks.Count == 0 ? new List<string> { "No saved decks — open editor" } : decks.Select(d =>
            { var validation = collection.Rules.Validate(d, collection.Owned); return d.name + " — " + validation.Points + "/100" + (validation.IsValid ? "" : " — " + string.Join("; ", validation.Errors)); }).ToList());
            deckDropdown.SetValueWithoutNotify(Mathf.Max(0, decks.FindIndex(d => d.deckId == Data.SelectedDeckId)));
            deckDropdown.interactable = decks.Count > 0;
            refreshingDecks = false;
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
                Game.UI.UIButtonEventUtility.ResetRuntimeListeners(removeButton);
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
            RefreshFactionLogo();
            factionDropdown.onValueChanged.RemoveAllListeners();
            factionDropdown.onValueChanged.AddListener(value =>
            {
                if (value < 0 || value >= SelectableFactions.Length) return;
                Data.Faction = SelectableFactions[value];
                RefreshFactionLogo();
                Data.SelectedDeckId = null; Data.AiDeckName = null; RefreshDeckOptions();
                _onChanged?.Invoke();
            });
        }

        private void RefreshFactionLogo()
        {
            if (factionLogo == null) return;
            factionLogo.sprite = startingDeckCatalog?.GetCatalog(Data.Faction)?.logo;
            factionLogo.enabled = factionLogo.sprite != null;
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
