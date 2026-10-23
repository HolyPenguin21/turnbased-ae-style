using System;
using System.Collections.Generic;
using Game.Cards;
using UnityEngine;
using UnityEngine.UI;

namespace Game.UI
{
    // Tactical inventory column used by the battle screen.
    public class BattleHandUI : MonoBehaviour
    {
        [SerializeField] private RectTransform cardContainer;
        [SerializeField] private BattleTacticCardUI cardPrefab;
        [SerializeField] private Button scrollUpButton;
        [SerializeField] private Button scrollDownButton;
        [SerializeField] private float cardHeight = 150f;
        [SerializeField] private float verticalGap = 16f;
        // Fixed window size, matching the user's own spec ("помещается 4 карты") — unlike
        // CardHandUI's own MaxVisible, this isn't expected to ever need tuning per-instance.
        private const int MaxVisible = 4;

        private readonly List<CardData> _cards = new List<CardData>();
        private readonly List<BattleTacticCardUI> _visible = new List<BattleTacticCardUI>();
        private int _scrollOffset;
        private TacticHand _inventory;
        private Func<CardData, bool> _canSelect;
        private Action<CardData> _onSelect;
        private Func<CardData, string> _unavailableReason;
        private Func<CardData, Vector2, Camera, bool> _onDrop;
        private Action<string> _showError;
        private CardData _excluded;
        private CardData _revealOnRefresh;
        public bool IsBound => _inventory != null;

        public void Bind(TacticHand inventory, Func<CardData, bool> canSelect = null, Action<CardData> onSelect = null,
            Func<CardData, string> unavailableReason = null, Func<CardData, Vector2, Camera, bool> onDrop = null,
            Action<string> showError = null, CardData excluded = null)
        {
            _unavailableReason = unavailableReason;
            _onDrop = onDrop;
            _showError = showError;
            bool exclusionChanged = !ReferenceEquals(_excluded, excluded);
            _revealOnRefresh = exclusionChanged ? _excluded : null;
            _excluded = excluded;
            if (inventory != null && ReferenceEquals(_inventory, inventory) && !exclusionChanged)
            {
                _canSelect = canSelect;
                _onSelect = onSelect;
                foreach (var card in _visible)
                {
                    BindCardInteraction(card);
                }
                return;
            }
            if (_inventory != null) _inventory.Changed -= RefreshInventory;
            _inventory = inventory;
            _canSelect = canSelect;
            _onSelect = onSelect;
            if (_inventory != null) _inventory.Changed += RefreshInventory;
            RefreshInventory();
        }

        private void OnDestroy()
        {
            if (_inventory != null) _inventory.Changed -= RefreshInventory;
        }

        private void RefreshInventory()
        {
            _cards.Clear();
            if (_inventory != null)
                foreach (var card in _inventory.Cards)
                    if (!ReferenceEquals(card, _excluded)) _cards.Add(card);
            _scrollOffset = UIListUtility.OffsetToReveal(_scrollOffset, _cards.IndexOf(_revealOnRefresh), MaxVisible);
            _revealOnRefresh = null;
            _scrollOffset = Mathf.Clamp(_scrollOffset, 0, Mathf.Max(0, _cards.Count - MaxVisible));
            Relayout();
            // Keep an empty hand as the drop target while its last card occupies the slot.
            gameObject.SetActive(_cards.Count > 0 || _excluded != null);
        }

        public void RefreshInteraction() => Relayout();

        private void BindCardInteraction(BattleTacticCardUI card)
        {
            CardData data = card.Data;
            card.SetInteraction(_canSelect?.Invoke(data) == true, () => _onSelect?.Invoke(data),
                () => _unavailableReason?.Invoke(data),
                _onDrop == null ? (Func<Vector2, Camera, bool>)null : (point, camera) => _onDrop(data, point, camera),
                _showError);
        }

        public void CancelDrags()
        {
            foreach (var card in _visible) if (card != null) card.CancelDrag();
        }

        public bool ContainsScreenPoint(Vector2 point, Camera camera) =>
            RectTransformUtility.RectangleContainsScreenPoint((RectTransform)transform, point, camera);

        public BattleTacticCardUI CreatePreview(Transform parent, CardData data)
        {
            if (cardPrefab == null || data == null) return null;
            var card = Instantiate(cardPrefab, parent);
            card.Setup(data, GetComponentInParent<BattleScreenUI>(true)?.GameConfig);
            var rect = (RectTransform)card.transform;
            rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 1f);
            rect.pivot = new Vector2(0.5f, 1f);
            rect.anchoredPosition = new Vector2(0f, -6f);
            float scale = Mathf.Min(1f, 130f / Mathf.Max(1f, rect.rect.width),
                150f / Mathf.Max(1f, rect.rect.height));
            rect.localScale = Vector3.one * scale;
            foreach (var graphic in card.GetComponentsInChildren<Graphic>(true)) graphic.raycastTarget = false;
            return card;
        }

        private Vector2 _lastContainerSize;

        private void LateUpdate()
        {
            if (cardContainer != null && cardContainer.rect.size != _lastContainerSize)
                FitVisibleCards();
        }

        private void FitVisibleCards()
        {
            if (cardContainer == null || cardPrefab == null)
                return;
            _lastContainerSize = cardContainer.rect.size;
            RectTransform prefabRect = cardPrefab.transform as RectTransform;
            if (prefabRect == null || prefabRect.rect.width <= 0f || prefabRect.rect.height <= 0f)
                return;
            float step = cardHeight + verticalGap;
            float requiredHeight = prefabRect.rect.height + (MaxVisible - 1) * step;
            float requiredWidth = prefabRect.rect.width;
            float scale = Mathf.Clamp01(Mathf.Min(_lastContainerSize.x / requiredWidth,
                _lastContainerSize.y / Mathf.Max(1f, requiredHeight)));
            for (int i = 0; i < _visible.Count; i++)
            {
                _visible[i].transform.localScale = Vector3.one * scale;
                _visible[i].SetSlot(new Vector2(0f, -i * step * scale));
            }
        }

        private void Awake()
        {
            // Discard any serialized presentation cards before binding the match inventory.
            if (cardContainer != null)
                foreach (var oldCard in cardContainer.GetComponentsInChildren<BattleTacticCardUI>(true))
                    Destroy(oldCard.gameObject);
            if (scrollUpButton != null)
                scrollUpButton.onClick.AddListener(() => Scroll(-1));
            if (scrollDownButton != null)
                scrollDownButton.onClick.AddListener(() => Scroll(1));
            Relayout();
        }

        public void AddCard(CardData data)
        {
            if (_inventory != null || data == null || data.Definition == null || data.Definition.cardType != CardType.Tactic)
                return;
            _cards.Add(data);
            _scrollOffset = Mathf.Max(0, _cards.Count - MaxVisible);
            Relayout();
        }

        private void Scroll(int direction)
        {
            _scrollOffset = Mathf.Clamp(_scrollOffset + direction, 0, Mathf.Max(0, _cards.Count - MaxVisible));
            Relayout();
        }

        private void Relayout()
        {
            CancelDrags();
            foreach (var card in _visible) card.gameObject.SetActive(false);
            UIListUtility.DestroyAndClear(_visible);
            if (scrollUpButton != null)
                scrollUpButton.interactable = _scrollOffset > 0;
            if (scrollDownButton != null)
                scrollDownButton.interactable = _scrollOffset + MaxVisible < _cards.Count;

            if (cardContainer == null || cardPrefab == null)
                return;

            float step = cardHeight + verticalGap;
            int end = Mathf.Min(_cards.Count, _scrollOffset + MaxVisible);
            // Only for the cards' ability abbreviations (see BattleTacticCardUI.Setup).
            BattleScreenUI screen = GetComponentInParent<BattleScreenUI>(true);
            Game.Core.GameConfig config = screen != null ? screen.GameConfig : null;
            for (int i = _scrollOffset; i < end; i++)
            {
                BattleTacticCardUI card = Instantiate(cardPrefab, cardContainer);
                Game.Audio.SceneUIAudioBinder.BindCreatedRoot(card);
                CardData data = _cards[i];
                card.Setup(data, config);
                BindCardInteraction(card);
                card.SetSlot(new Vector2(0f, -(i - _scrollOffset) * step));
                _visible.Add(card);
            }
            FitVisibleCards();
        }
    }
}

