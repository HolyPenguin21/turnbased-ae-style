using System;
using System.Collections.Generic;
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
        [SerializeField] private TMP_InputField nicknameField;
        [SerializeField] private Image colorSwatch;
        [SerializeField] private TMP_Dropdown colorDropdown;
        [SerializeField] private TMP_Dropdown factionDropdown;
        [SerializeField] private TMP_Dropdown controllerDropdown;
        [SerializeField] private Button removeButton;

        // Colour dropdown only ever lists colours not taken by another row, so its option
        // positions don't line up 1:1 with PlayerColorPalette indices — this maps
        // "dropdown option position" -> "actual palette index" for whatever list was last built.
        private readonly List<int> _colorOptionIndices = new List<int>();
        private Action _onChanged;

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
                removeButton.onClick.RemoveAllListeners();
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
            factionDropdown.AddOptions(new List<string> { "Iron Concord", "Random" });
            factionDropdown.SetValueWithoutNotify((int)Data.Faction);
            factionDropdown.onValueChanged.RemoveAllListeners();
            factionDropdown.onValueChanged.AddListener(value =>
            {
                Data.Faction = (Faction)value;
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
