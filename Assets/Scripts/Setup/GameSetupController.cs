using System.Collections.Generic;
using Game.Core;
using Game.Players;
using Game.UI;
using UnityEngine;
using UnityEngine.InputSystem;
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
        [SerializeField] private GameObject mainMenuCanvas;
        [SerializeField] private GameConfig gameConfig;

        private GameSetupModel _model;
        private readonly List<PlayerRowUI> _rows = new List<PlayerRowUI>();

        // This component lives on the setup panel itself, so Update() only ever runs while
        // that panel is the active one — no extra "is this screen showing" guard needed
        // (unlike MainMenuController, which sits on a persistently-active object watching a
        // separate canvas). Skips while a nickname field is focused — otherwise typing a
        // space into a player's name would also launch the game.
        private void Update()
        {
            if (Keyboard.current == null || !Keyboard.current.spaceKey.wasPressedThisFrame)
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

            RefreshButtons();
        }

        public void OnAddPlayerClicked() => AddPlayerRow();

        public void OnStartGameClicked()
        {
            GameSession.Players = _model.Players;
            SceneManager.LoadScene(SceneNames.Game);
        }

        public void OnBackClicked()
        {
            gameObject.SetActive(false);
            if (mainMenuCanvas != null)
                mainMenuCanvas.SetActive(true);
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
