using System;
using System.Collections.Generic;
using Game.Core;
using Game.Map;
using Game.Players;
using Game.Turns;
using UnityEngine;
using UnityEngine.UI;

namespace Game.UI
{
    // Phase 1 of every turn: everyone rolls a 5-dice pool, highest score goes first, ties
    // reroll (see TurnOrderResolver). One DiceRowUI per player, spawned fresh each time this
    // is shown (a new turn means a fresh roll, not a reused one).
    public class TurnOrderPopupUI : MonoBehaviour
    {
        [SerializeField] private GameObject panelRoot;
        [SerializeField] private Transform rowContainer;
        [SerializeField] private DiceRowUI rowPrefab;
        [SerializeField] private Button rollButton;
        [SerializeField] private Button continueButton;
        [SerializeField] private InitiativeBuyPanelUI buyPanel;
        [SerializeField] private GameConfig gameConfig;

        private readonly List<DiceRowUI> _rows = new List<DiceRowUI>();
        private readonly Dictionary<PlayerSetupData, DiceRowUI> _rowByPlayer = new Dictionary<PlayerSetupData, DiceRowUI>();
        private List<PlayerSetupData> _players;
        private List<PlayerSetupData> _pendingOrder;
        private Action<List<PlayerSetupData>> _onResolved;

        // Space does whichever of Roll/Continue is currently showing — only one of the two is
        // ever active at once (see Show/RollAll), so no ambiguity about which it means.
        private void Update()
        {
            if (panelRoot != null && !panelRoot.activeSelf)
                return;
            if (!UIFocusUtility.WasSpacePressed())
                return;

            if (rollButton != null && rollButton.gameObject.activeSelf)
                RollAll();
            else if (continueButton != null && continueButton.gameObject.activeSelf)
                Finish(_pendingOrder);
        }

        public void Show(List<PlayerSetupData> players, Action<List<PlayerSetupData>> onResolved)
        {
            _players = players;
            _onResolved = onResolved;
            _pendingOrder = null;

            ClearRows();
            if (rowContainer != null && rowPrefab != null)
            {
                foreach (PlayerSetupData player in players)
                {
                    DiceRowUI row = Instantiate(rowPrefab, rowContainer);
                    row.SetPlayer(player, TurnOrderResolver.DiceCountFor(player));
                    _rows.Add(row);
                    _rowByPlayer[player] = row;
                }
            }

            ShowBuyPanel(players);

            if (panelRoot != null)
                panelRoot.SetActive(true);

            if (continueButton != null)
                continueButton.gameObject.SetActive(false);

            if (rollButton != null)
            {
                rollButton.gameObject.SetActive(true);
                rollButton.onClick.RemoveAllListeners();
                rollButton.onClick.AddListener(RollAll);
            }
        }

        // Only the human player buys dice through this panel — AI already bought its dice via
        // InitiativeDiceAI before Show was even called (see InitiativeBuyPanelUI.Show, which
        // no-ops and hides itself for any non-human/missing player).
        private void ShowBuyPanel(List<PlayerSetupData> players)
        {
            if (buyPanel == null)
                return;

            buyPanel.DiceCountChanged -= OnBuyPanelDiceCountChanged;
            buyPanel.DiceCountChanged += OnBuyPanelDiceCountChanged;

            PlayerSetupData human = players.Find(p => p != null && p.IsHuman);
            PlayerRoot humanRoot = human != null ? PlayerRootRegistry.FindFor(human) : null;
            int price = gameConfig != null ? gameConfig.initiativeDicePrice : 0;
            buyPanel.Show(human, humanRoot, price);
        }

        // The human just bought/refunded a die — resize their row's dice slots to match so
        // Roll doesn't visually snap the count when it's actually pressed.
        private void OnBuyPanelDiceCountChanged()
        {
            PlayerSetupData human = _players?.Find(p => p != null && p.IsHuman);
            if (human != null && _rowByPlayer.TryGetValue(human, out DiceRowUI row))
                row.SetPlayer(human, TurnOrderResolver.DiceCountFor(human));
        }

        private void RollAll()
        {
            if (buyPanel != null)
                buyPanel.Lock();

            List<PlayerSetupData> order = TurnOrderResolver.Resolve(_players, out Dictionary<PlayerSetupData, DiceRollResult> finalRolls);
            _pendingOrder = order;

            foreach (PlayerSetupData player in _players)
                if (_rowByPlayer.TryGetValue(player, out DiceRowUI row) && finalRolls.TryGetValue(player, out DiceRollResult roll))
                    row.ShowRoll(roll);

            for (int i = 0; i < order.Count; i++)
                if (_rowByPlayer.TryGetValue(order[i], out DiceRowUI row))
                    row.ShowRank(i + 1);

            if (rollButton != null)
                rollButton.gameObject.SetActive(false);

            if (continueButton != null)
            {
                continueButton.gameObject.SetActive(true);
                continueButton.onClick.RemoveAllListeners();
                continueButton.onClick.AddListener(() => Finish(order));
            }
        }

        private void Finish(List<PlayerSetupData> order)
        {
            if (panelRoot != null)
                panelRoot.SetActive(false);
            _onResolved?.Invoke(order);
        }

        private void ClearRows()
        {
            UIListUtility.DestroyAndClear(_rows);
            _rowByPlayer.Clear();
        }
    }
}
