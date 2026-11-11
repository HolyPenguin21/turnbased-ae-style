using System;
using System.Collections;
using System.Collections.Generic;
using Game.Core;
using Game.Map;
using Game.Players;
using Game.Turns;
using UnityEngine;
using UnityEngine.UI;

namespace Game.UI
{
    // Phase 1 of every turn: everyone rolls InitiativeRules.BaseTokens plus any purchased bonus
    // tokens, highest score goes first, and tied players visibly reroll until the tie is broken
    // (see TurnOrderResolver). One TokenRowUI per player is spawned fresh each turn.
    public class TurnOrderPopupUI : MonoBehaviour
    {
        [SerializeField] private GameObject panelRoot;
        [SerializeField] private Transform rowContainer;
        [SerializeField] private TokenRowUI rowPrefab;
        [SerializeField] private Button rollButton;
        [SerializeField] private Button continueButton;
        [SerializeField] private InitiativeBuyPanelUI buyPanel;
        // Player-facing "auto-press Roll/Continue for me" checkbox (AutoRoll_Checkbox, under
        // TurnOrderPopup) — same principle as BattleAttackPopupUI's own Autoroll_Toggle,
        // persisted via PlayerPrefs. One toggle gates two delayed auto-presses: Roll once this
        // popup is shown, then Continue once the roll has produced a winner (see Show/RollAll).
        [SerializeField] private Toggle autoRollToggle;
        [SerializeField] private float autoRollDelay = 0.75f;
        [SerializeField] private float autoContinueDelay = 0.5f;
        private const string AutoRollPrefKey = "TurnOrderPopup.AutoRollEnabled";

        private readonly List<TokenRowUI> _rows = new List<TokenRowUI>();
        private readonly Dictionary<PlayerSetupData, TokenRowUI> _rowByPlayer = new Dictionary<PlayerSetupData, TokenRowUI>();
        private List<PlayerSetupData> _players;
        private List<PlayerSetupData> _pendingOrder;
        // The human whose purchases the buy panel currently shows.
        private PlayerSetupData _buyer;
        // Hotseat: humans buy one after another (curtain -> panel -> Continue), then one roll.
        private bool _hotseatBuying;
        private Action<List<PlayerSetupData>> _onResolved;

        private void Awake()
        {
            if (autoRollToggle != null)
            {
                autoRollToggle.SetIsOnWithoutNotify(PlayerPrefs.GetInt(AutoRollPrefKey, 0) != 0);
                autoRollToggle.onValueChanged.AddListener(OnAutoRollToggleChanged);
            }
        }

        private static void OnAutoRollToggleChanged(bool isOn)
        {
            PlayerPrefs.SetInt(AutoRollPrefKey, isOn ? 1 : 0);
            PlayerPrefs.Save();
        }

        private bool IsAutoRollEnabled => autoRollToggle != null && autoRollToggle.isOn;

        // Space does whichever of Roll/Continue is currently showing — only one of the two is
        // ever active at once (see Show/RollAll), so no ambiguity about which it means.
        private void Update()
        {
            if (panelRoot != null && !panelRoot.activeSelf)
                return;
            if (!UIFocusUtility.WasSpacePressed())
                return;

            Game.Audio.GameAudioManager.Instance?.PlayClick();
            if (rollButton != null && rollButton.gameObject.activeSelf)
                RollAll();
            else if (continueButton != null && continueButton.gameObject.activeSelf)
                continueButton.onClick.Invoke();
        }

        public void Show(List<PlayerSetupData> players, Action<List<PlayerSetupData>> onResolved)
        {
            // Stops a still-pending auto-Roll/auto-Continue coroutine from a PREVIOUS Show() on
            // this same (reused) popup instance from firing against the fresh turn set up below.
            StopAllCoroutines();

            _players = players;
            _onResolved = onResolved;
            _pendingOrder = null;

            ClearRows();
            if (rowContainer != null && rowPrefab != null)
            {
                foreach (PlayerSetupData player in players)
                {
                    TokenRowUI row = Instantiate(rowPrefab, rowContainer);
                    Game.Audio.SceneUIAudioBinder.BindCreatedRoot(row);
                    row.SetPlayer(player, TurnOrderResolver.TokenCountFor(player));
                    _rows.Add(row);
                    _rowByPlayer[player] = row;
                }
            }

            if (panelRoot != null)
                panelRoot.SetActive(true);

            var buyers = players.FindAll(p => p != null && p.IsLocalHuman && !p.IsEliminated);
            if (buyers.Count > 1 && PlayerDecisionRouter.Handoff != null)
            {
                StartCoroutine(HotseatPurchases(buyers));
                return;
            }
            ShowBuyPanel(players);

            if (continueButton != null)
                continueButton.gameObject.SetActive(false);

            if (rollButton != null)
            {
                rollButton.gameObject.SetActive(true);
                Game.UI.UIUtility.ResetRuntimeListeners(rollButton);
                rollButton.onClick.AddListener(RollAll);
            }

            if (IsAutoRollEnabled)
                StartCoroutine(AutoRollAfterDelay());
        }

        private IEnumerator AutoRollAfterDelay()
        {
            if (autoRollDelay > 0f)
                yield return new WaitForSeconds(autoRollDelay);
            if (rollButton != null && rollButton.gameObject.activeSelf)
                RollAll();
        }

        private IEnumerator AutoContinueAfterDelay(List<PlayerSetupData> order)
        {
            if (autoContinueDelay > 0f)
                yield return new WaitForSeconds(autoContinueDelay);
            if (continueButton != null && continueButton.gameObject.activeSelf)
                Finish(order);
        }

        // Only the seated human buys tokens through this panel. AI purchases are already
        // applied by InitiativeCoordinatorV2 before Show is called.
        private void ShowBuyPanel(List<PlayerSetupData> players)
        {
            PlayerSetupData seat = LocalSeat.Current;
            ShowBuyPanelFor(seat != null && players.Contains(seat) ? seat : null);
        }

        private void ShowBuyPanelFor(PlayerSetupData human)
        {
            _buyer = human;
            if (buyPanel == null)
                return;
            buyPanel.TokenCountChanged -= OnBuyPanelTokenCountChanged;
            buyPanel.TokenCountChanged += OnBuyPanelTokenCountChanged;
            buyPanel.Show(human, human != null ? PlayerRootRegistry.FindFor(human) : null);
        }

        // Hotseat: every local human takes the screen and buys in turn; the shared roll starts
        // only after the last one pressed Continue. Final token counts are public, as at a roll.
        private IEnumerator HotseatPurchases(List<PlayerSetupData> buyers)
        {
            _hotseatBuying = true;
            if (rollButton != null) rollButton.gameObject.SetActive(false);
            foreach (PlayerSetupData buyer in buyers)
            {
                yield return PlayerDecisionRouter.TakeScreenFor(buyer, $"Initiative purchase: {buyer.Nickname}", null);
                ShowBuyPanelFor(buyer);
                bool done = false;
                if (continueButton != null)
                {
                    continueButton.gameObject.SetActive(true);
                    Game.UI.UIUtility.ResetRuntimeListeners(continueButton);
                    continueButton.onClick.AddListener(() => done = true);
                }
                else done = true;
                while (!done) yield return null;
                if (continueButton != null) continueButton.gameObject.SetActive(false);
                buyPanel?.Lock();
            }
            _hotseatBuying = false;
            ShowBuyPanelFor(null);
            if (rollButton != null)
            {
                rollButton.gameObject.SetActive(true);
                Game.UI.UIUtility.ResetRuntimeListeners(rollButton);
                rollButton.onClick.AddListener(RollAll);
            }
            if (IsAutoRollEnabled)
                StartCoroutine(AutoRollAfterDelay());
        }

        // The buyer just bought/refunded a die — resize their row's tokens slots to match so
        // Roll doesn't visually snap the count when it's actually pressed.
        private void OnBuyPanelTokenCountChanged()
        {
            PlayerSetupData human = _buyer;
            if (human != null && _rowByPlayer.TryGetValue(human, out TokenRowUI row))
                row.SetPlayer(human, TurnOrderResolver.TokenCountFor(human));
        }

        private void RollAll()
        {
            if (_hotseatBuying || (rollButton != null && !rollButton.gameObject.activeSelf))
                return;
            if (buyPanel != null)
                buyPanel.Lock();

            TurnOrderResolution resolution = TurnOrderResolver.Resolve(_players);
            _pendingOrder = resolution.Order;

            if (rollButton != null)
                rollButton.gameObject.SetActive(false);

            StartCoroutine(AnimateResolution(resolution));
        }

        private IEnumerator AnimateResolution(TurnOrderResolution resolution)
        {
            foreach (InitiativeRollRound round in resolution.Rounds)
            {
                int pending = 0;
                foreach (KeyValuePair<PlayerSetupData, TokenRollResult> entry in round.Rolls)
                    if (_rowByPlayer.TryGetValue(entry.Key, out TokenRowUI row))
                    {
                        pending++;
                        row.ShowRoll(entry.Value, () => pending--);
                    }
                yield return new WaitUntil(() => pending == 0);
            }

            for (int i = 0; i < resolution.Order.Count; i++)
                if (_rowByPlayer.TryGetValue(resolution.Order[i], out TokenRowUI row))
                    row.ShowRank(i + 1);

            if (continueButton != null)
            {
                continueButton.gameObject.SetActive(true);
                Game.UI.UIUtility.ResetRuntimeListeners(continueButton);
                continueButton.onClick.AddListener(() => Finish(resolution.Order));
            }

            if (IsAutoRollEnabled)
                StartCoroutine(AutoContinueAfterDelay(resolution.Order));
        }

        // panelRoot's own activeSelf doubles as the "already finished" guard — a manual
        // Continue click and the auto-Continue coroutine (see AutoContinueAfterDelay) can both be
        // armed at once when the toggle is on, and continueButton itself is never deactivated on
        // Finish (only panelRoot is), so without this a click just before the delay expires
        // would let the coroutine fire Finish a second time and double-invoke _onResolved.
        private void Finish(List<PlayerSetupData> order)
        {
            if (panelRoot != null && !panelRoot.activeSelf)
                return;
            if (panelRoot != null)
                panelRoot.SetActive(false);
            _onResolved?.Invoke(order);
        }

        private void ClearRows()
        {
            UIUtility.DestroyAndClear(_rows);
            _rowByPlayer.Clear();
        }
    }
}
