using System;
using System.Collections;
using System.Collections.Generic;
using Game.Cards;
using UnityEngine;
using UnityEngine.UI;

namespace Game.UI
{
    public partial class CardHandUI
    {
        [SerializeField] private Button cardsTypeSwitchButton;
        [SerializeField, Min(0.01f)] private float tacticFlipDuration = 0.24f;
        [SerializeField, Min(0f)] private float tacticFlipInterval = 0.05f;
        private TacticHand _tacticInventory;
        private TacticHand _tacticSource;
        private readonly List<CardUI> _tacticCards = new List<CardUI>();
        private int _tacticScrollOffset;
        private bool _tacticChallengeActive;
        private bool _showingTactics;
        private Coroutine _tacticFlip;
        private bool _tacticFlipping;
        public bool IsHandTransitioning => _tacticFlipping;
        private Func<CardData, bool> _canSelectTactic;
        private Action<CardData> _onSelectTactic;
        private Func<CardData, string> _tacticUnavailableReason;
        private Func<CardData, Vector2, Camera, bool> _onDropTactic;
        private Action<string> _showTacticError;
        private CardData _excludedTactic;

        private void InitializeTacticSwitch()
        {
            if (cardsTypeSwitchButton == null) return;
            cardsTypeSwitchButton.onClick.RemoveListener(OnCardsTypeSwitchClicked);
            cardsTypeSwitchButton.onClick.AddListener(OnCardsTypeSwitchClicked);
            RefreshTacticSwitch();
        }

        private void BindTacticInventory()
        {
            UnbindTacticInventory();
            _tacticInventory = HumanPlayer?.Tactics;
            if (_tacticInventory != null) _tacticInventory.Changed += RefreshTacticSwitch;
            BindTactics(_tacticInventory);
            RefreshTacticSwitch();
        }

        private void UnbindTacticInventory()
        {
            if (_tacticInventory != null) _tacticInventory.Changed -= RefreshTacticSwitch;
            _tacticInventory = null;
            if (_tacticSource != null) _tacticSource.Changed -= RebuildTacticCards;
            _tacticSource = null;
        }

        private void RefreshTacticSwitch()
        {
            bool hasCards = _tacticInventory?.Cards.Count > 0 && !ShowingDebugHand;
            // An empty decision hand must remain available as a drop target for the slotted card.
            if (!hasCards && _showingTactics && !_tacticChallengeActive) RestoreMainHand();
            if (cardsTypeSwitchButton == null) return;
            cardsTypeSwitchButton.gameObject.SetActive(hasCards || (_tacticChallengeActive && _showingTactics));
            cardsTypeSwitchButton.GetComponent<ArmyButtonUI>()?.SetSelected(_showingTactics);
        }

        private void OnCardsTypeSwitchClicked()
        {
            if (UIFocusUtility.IsGameplayInputBlocked
                || (!(_tacticInventory?.Cards.Count > 0) && !_tacticChallengeActive)) return;
            CancelTacticDrags();
            CancelAttachMode();
            StartTacticFlip(!_showingTactics);
            RefreshTacticSwitch();
        }

        public void SetTacticInputOverlay(bool raised)
        {
            SetTacticOverlay(handContainer, raised);
            SetTacticOverlay(cardsTypeSwitchButton != null ? cardsTypeSwitchButton.transform : null, raised);
            SetTacticOverlay(scrollLeftButton != null ? scrollLeftButton.transform : null, raised);
            SetTacticOverlay(scrollRightButton != null ? scrollRightButton.transform : null, raised);
            if (cardSeparators != null)
                foreach (var separator in cardSeparators)
                    if (separator != null) SetTacticOverlay(separator.parent, raised);
        }

        private static void SetTacticOverlay(Transform root, bool raised)
        {
            if (root == null) return;
            var canvas = root.GetComponent<Canvas>();
            if (canvas == null && raised) canvas = root.gameObject.AddComponent<Canvas>();
            if (canvas != null)
            {
                canvas.overrideSorting = raised;
                canvas.sortingOrder = 25000;
            }
            if (raised && root.GetComponent<GraphicRaycaster>() == null)
                root.gameObject.AddComponent<GraphicRaycaster>();
        }

        public void BindTactics(TacticHand inventory, Func<CardData, bool> canSelect = null,
            Action<CardData> onSelect = null, Func<CardData, string> unavailableReason = null,
            Func<CardData, Vector2, Camera, bool> onDrop = null, Action<string> showError = null,
            CardData excluded = null)
        {
            bool rebuild = !ReferenceEquals(_tacticSource, inventory) || !ReferenceEquals(_excludedTactic, excluded);
            CardData returned = !ReferenceEquals(_excludedTactic, excluded) ? _excludedTactic : null;
            if (!ReferenceEquals(_tacticSource, inventory))
            {
                if (_tacticSource != null) _tacticSource.Changed -= RebuildTacticCards;
                _tacticSource = inventory;
                if (_tacticSource != null) _tacticSource.Changed += RebuildTacticCards;
                _tacticScrollOffset = 0;
            }
            _canSelectTactic = canSelect;
            _onSelectTactic = onSelect;
            _tacticUnavailableReason = unavailableReason;
            _onDropTactic = onDrop;
            _showTacticError = showError;
            _excludedTactic = excluded;
            if (rebuild)
            {
                RebuildTacticCards();
                int index = returned == null ? -1 : _tacticCards.FindIndex(c => ReferenceEquals(c.Data, returned));
                _tacticScrollOffset = UIUtility.OffsetToReveal(_tacticScrollOffset, index, MaxVisible);
                Relayout(animated: false);
            }
            else foreach (var card in _tacticCards) BindTacticInteraction(card);
        }

        private void BindTacticInteraction(CardUI card)
        {
            CardData data = card.Data;
            card.ConfigureTacticInteraction(data, () => _onSelectTactic?.Invoke(data),
                () => _tacticFlipping ? "Wait for the cards to finish turning."
                    : _tacticUnavailableReason?.Invoke(data) ?? (_canSelectTactic?.Invoke(data) == true
                        ? null : "Wait for your challenge decision to use a tactical card."),
                _onDropTactic == null ? (Func<Vector2, Camera, bool>)null
                    : (point, camera) => _onDropTactic(data, point, camera),
                _showTacticError ?? (reason => turnController?.ShowSpawnHint(reason)), useHandLayout: true);
        }

        private void RebuildTacticCards()
        {
            StopTacticFlip();
            CancelTacticDrags();
            foreach (var card in _tacticCards) card.gameObject.SetActive(false);
            UIUtility.DestroyAndClear(_tacticCards);
            if (_tacticSource != null && cardPrefab != null && handContainer != null)
                foreach (var data in _tacticSource.Cards)
                {
                    if (ReferenceEquals(data, _excludedTactic)) continue;
                    var card = Instantiate(cardPrefab, handContainer);
                    Game.Audio.SceneUIAudioBinder.BindCreatedRoot(card);
                    card.Setup(this, data, restingScale, hoverScale, hoverLift, animDuration, dragHoverShrink);
                    BindTacticInteraction(card);
                    card.ResetHandPose(Vector2.zero);
                    _tacticCards.Add(card);
                }
            _lastDisplayedHandCount = -1;
            Relayout(animated: false);
        }

        public void ShowTactics(TacticHand inventory)
        {
            _tacticChallengeActive = true;
            foreach (var card in _debugCards) card.gameObject.SetActive(false);
            _lastDisplayedHandCount = -1;
            RefreshDeckCountText();
            BindTactics(inventory);
            CancelAttachMode();
            if (inventory?.Cards.Count > 0 && !_showingTactics) StartTacticFlip(true);
            else Relayout(animated: false);
            RefreshTacticSwitch();
            RefreshDrawButtonInteractable();
        }

        public void CancelTacticDrags()
        {
            foreach (var card in _tacticCards) if (card != null) card.CancelDrag();
        }

        public bool ContainsTacticScreenPoint(Vector2 point, Camera camera) => handContainer != null
            && RectTransformUtility.RectangleContainsScreenPoint(handContainer, point, camera);

        public void RestoreMainHand()
        {
            _tacticChallengeActive = false;
            CancelTacticDrags();
            BindTactics(_tacticInventory);
            SetTacticInputOverlay(false);
            if (ShowingDebugHand)
            {
                StopTacticFlip();
                _showingTactics = false;
                Relayout(animated: false);
                RenderDebugHand(_debugHand);
            }
            else if (_showingTactics) StartTacticFlip(false);
            RefreshTacticSwitch();
            RefreshDrawButtonInteractable();
        }

        private void ResetTacticPresentation()
        {
            StopTacticFlip();
            _showingTactics = false;
            _lastDisplayedHandCount = -1;
            _tacticChallengeActive = false;
            _excludedTactic = null;
            CancelTacticDrags();
            SetTacticInputOverlay(false);
            Relayout(animated: false);
        }

        private void RelayoutTactics(bool animated)
        {
            _tacticScrollOffset = Mathf.Clamp(_tacticScrollOffset, 0, Mathf.Max(0, _tacticCards.Count - MaxVisible));
            for (int i = 0; i < _tacticCards.Count; i++)
            {
                int slot = i - _tacticScrollOffset;
                bool visible = _showingTactics && !ShowingDebugHand && slot >= 0 && slot < MaxVisible;
                _tacticCards[i].gameObject.SetActive(visible);
                if (visible) _tacticCards[i].SetHome(new Vector2(SlotX(slot), 0f), animated);
            }
        }

        private void StopTacticFlip()
        {
            bool interrupted = _tacticFlipping;
            if (_tacticFlip != null) StopCoroutine(_tacticFlip);
            _tacticFlip = null;
            _tacticFlipping = false;
            if (!interrupted) return;
            ResetFlipCards(_cards, _scrollOffset);
            ResetFlipCards(_tacticCards, _tacticScrollOffset);
            RefreshDrawButtonInteractable();
        }

        private void ResetFlipCards(List<CardUI> cards, int offset)
        {
            for (int i = 0; i < cards.Count; i++)
                cards[i].ResetHandPose(new Vector2(SlotX(i - offset), 0f));
        }

        private void StartTacticFlip(bool tactics)
        {
            StopTacticFlip();
            ResetFlipCards(_cards, _scrollOffset);
            ResetFlipCards(_tacticCards, _tacticScrollOffset);
            _showingTactics = tactics;
            _lastDisplayedHandCount = -1;
            Relayout(animated: false);
            int visibleSlots = Mathf.Min(MaxVisible,
                Mathf.Max(_cards.Count - _scrollOffset, _tacticCards.Count - _tacticScrollOffset));
            if (!gameObject.activeInHierarchy || visibleSlots <= 0) return;
            _tacticFlipping = true;
            UpdateScrollButtons();
            RefreshDrawButtonInteractable();
            _tacticFlip = StartCoroutine(FlipTacticHand(tactics));
        }

        private IEnumerator FlipTacticHand(bool tactics)
        {
            var outgoing = tactics ? _cards : _tacticCards;
            var incoming = tactics ? _tacticCards : _cards;
            int outOffset = tactics ? _scrollOffset : _tacticScrollOffset;
            int inOffset = tactics ? _tacticScrollOffset : _scrollOffset;
            float duration = Mathf.Max(0.01f, tacticFlipDuration);
            float interval = Mathf.Clamp(tacticFlipInterval, 0f, duration * 0.9f);
            int visibleSlots = Mathf.Min(MaxVisible, Mathf.Max(outgoing.Count - outOffset, incoming.Count - inOffset));
            float total = duration + (visibleSlots - 1) * interval;
            for (float elapsed = 0f; elapsed < total; elapsed += Time.unscaledDeltaTime)
            {
                for (int slot = 0; slot < visibleSlots; slot++)
                {
                    float progress = Mathf.Clamp01((elapsed - slot * interval) / duration);
                    CardUI oldCard = slot + outOffset < outgoing.Count ? outgoing[slot + outOffset] : null;
                    CardUI newCard = slot + inOffset < incoming.Count ? incoming[slot + inOffset] : null;
                    if (oldCard != null)
                    {
                        oldCard.gameObject.SetActive(progress < 0.5f);
                        float scale = restingScale * HandLayoutScale;
                        oldCard.transform.localScale = new Vector3(scale * Mathf.Clamp01(1f - progress * 2f), scale, scale);
                    }
                    if (newCard != null)
                    {
                        newCard.gameObject.SetActive(progress >= 0.5f);
                        float scale = restingScale * HandLayoutScale;
                        newCard.transform.localScale = new Vector3(scale * Mathf.Clamp01(progress * 2f - 1f), scale, scale);
                    }
                }
                yield return null;
            }
            _tacticFlip = null;
            _tacticFlipping = false;
            ResetFlipCards(outgoing, outOffset);
            ResetFlipCards(incoming, inOffset);
            Relayout(animated: false);
            RefreshDrawButtonInteractable();
        }
    }
}
