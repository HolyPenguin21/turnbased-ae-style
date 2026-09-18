using System.Collections.Generic;
using Game.Core;
using Game.Players;
using Game.Terrain;
using Game.UI;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Game.Setup
{
    // The pre-game setup panel opened from the main menu's "New Game" button. Owns a fresh
    // GameSetupModel each time it's opened, and instantiates one PlayerRowUI per player.
    public class GameSetupController : MonoBehaviour
    {
        [SerializeField] private Transform playerListContainer;
        [SerializeField] private Button addPlayerButton;
        [SerializeField] private GameObject mainMenuPanel;
        [SerializeField] private GameConfig gameConfig;

        [Header("Map")]
        // Both optional — left unassigned, the model just keeps its own Medium/Arid defaults
        // (see GameSetupModel), so an existing setup scene with neither dropdown wired in yet
        // keeps generating exactly the map it always has. Options are populated from the
        // MapSize/Biome enums themselves (see PopulateMapDropdowns), never hand-typed, so they
        // can't drift out of sync with a future added biome/size.
        [SerializeField] private TMP_Dropdown mapSizeDropdown;
        [SerializeField] private TMP_Dropdown biomeDropdown;

        private GameSetupModel _model;
        private readonly List<PlayerRowUI> _rows = new List<PlayerRowUI>();

        // This component lives on the setup panel itself, so Update() only ever runs while
        // that panel is the active one — no extra "is this screen showing" guard needed
        // (unlike MainMenuController, which sits on a persistently-active object watching a
        // separate panel). Skips while a nickname field is focused — otherwise typing a
        // space into a player's name would also launch the game.
        private void Update()
        {
            if (!UIFocusUtility.WasSpacePressed())
                return;
            if (UIFocusUtility.IsTextFieldFocused())
                return;
            OnStartGameClicked();
        }

        private void OnEnable()
        {
            if (gameConfig == null)
            {
                Debug.LogWarning("GameSetupController: Game Config is not assigned.");
                return;
            }

            ClearRows();
            _model = new GameSetupModel(gameConfig.minPlayers, gameConfig.maxPlayers);

            for (int i = 0; i < gameConfig.minPlayers; i++)
                AddPlayerRow();

            PopulateMapDropdowns();
            RefreshButtons();
        }

        public void OnAddPlayerClicked() => AddPlayerRow();

        public void OnStartGameClicked()
        {
            ResolveRandomFactions();
            GameSession.Players = _model.Players;
            GameSession.SelectedMapSize = _model.MapSize;
            GameSession.SelectedBiome = _model.Biome;
            SceneManager.LoadScene(SceneNames.Game);
        }

        // MapSize's own int values are ring radii (5/6/7/8, see MapSize), not 0-based dropdown
        // indices — so the dropdown is indexed by position in this values array, never by
        // casting the enum value itself, unlike Biome (which has no meaningful underlying value
        // and can just cast straight to/from its ordinal).
        private static readonly MapSize[] MapSizeValues = (MapSize[])System.Enum.GetValues(typeof(MapSize));

        // Options come straight from the MapSize/Biome enum names (Small/Medium/Large/Huge,
        // Arid/Desert) rather than a hand-authored label list — same reasoning as
        // PlayerRowUI.SelectableFactions: it can't silently drift out of sync with the enum if
        // a size or biome is ever added or renamed.
        private void PopulateMapDropdowns()
        {
            if (mapSizeDropdown != null)
            {
                mapSizeDropdown.ClearOptions();
                mapSizeDropdown.AddOptions(new List<string>(System.Enum.GetNames(typeof(MapSize))));
                mapSizeDropdown.SetValueWithoutNotify(System.Array.IndexOf(MapSizeValues, _model.MapSize));
                mapSizeDropdown.onValueChanged.RemoveAllListeners();
                mapSizeDropdown.onValueChanged.AddListener(value => _model.MapSize = MapSizeValues[value]);
            }

            if (biomeDropdown != null)
            {
                biomeDropdown.ClearOptions();
                biomeDropdown.AddOptions(new List<string>(System.Enum.GetNames(typeof(Biome))));
                biomeDropdown.SetValueWithoutNotify((int)_model.Biome);
                biomeDropdown.onValueChanged.RemoveAllListeners();
                biomeDropdown.onValueChanged.AddListener(value => _model.Biome = (Biome)value);
            }
        }

        // "Random" is a setup-time placeholder, never itself a real faction (see Faction's own
        // comment) — nothing downstream (CardHandUI's starting hand, AiTurnController's
        // army/card catalog lookups, ...) knows how to resolve it, so it must become a concrete
        // faction exactly once, here, before the Game scene ever sees these players.
        private static readonly Faction[] RandomizableFactions = { Faction.IronConcord, Faction.Ashen };

        private void ResolveRandomFactions()
        {
            foreach (PlayerSetupData player in _model.Players)
                if (player.Faction == Faction.Random)
                    player.Faction = RandomizableFactions[UnityEngine.Random.Range(0, RandomizableFactions.Length)];
        }

        public void OnBackClicked()
        {
            gameObject.SetActive(false);
            if (mainMenuPanel != null)
                mainMenuPanel.SetActive(true);
        }

        private void AddPlayerRow()
        {
            PlayerSetupData data = _model.AddPlayer();
            if (data == null) return;

            PlayerRowUI row = Instantiate(gameConfig.playerRowPrefab, playerListContainer);
            row.Bind(data, RefreshButtons, OnRemoveRowClicked);
            _rows.Add(row);
            RefreshButtons();
        }

        private void OnRemoveRowClicked(PlayerRowUI row)
        {
            if (!_model.CanRemovePlayer)
                return;

            _model.RemovePlayer(row.Data);
            _rows.Remove(row);
            Destroy(row.gameObject);
            RefreshButtons();
        }

        private void ClearRows() => UIListUtility.DestroyAndClear(_rows);

        private void RefreshButtons()
        {
            if (addPlayerButton != null)
                addPlayerButton.interactable = _model.CanAddPlayer;

            RefreshAllColorOptions();
        }

        // Keeps every row's colour dropdown limited to colours nobody else currently has —
        // re-run after any row's colour/nickname/faction/controller changes, or a row is
        // added/removed, since any of those can change who's "taken" which colour.
        private void RefreshAllColorOptions()
        {
            foreach (PlayerRowUI row in _rows)
            {
                var takenByOthers = new HashSet<int>();
                foreach (PlayerRowUI other in _rows)
                    if (other != row)
                        takenByOthers.Add(other.Data.ColorIndex);
                row.RefreshAvailableColors(takenByOthers);
            }
        }
    }
}
