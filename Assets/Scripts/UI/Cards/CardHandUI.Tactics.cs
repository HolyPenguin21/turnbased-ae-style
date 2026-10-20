using System.Collections;
using Game.Cards;
using UnityEngine;
using UnityEngine.UI;

namespace Game.UI
{
    public partial class CardHandUI
    {
        [SerializeField] private Button cardsTypeSwitchButton;
        [SerializeField] private BattleHandUI tacticHandTemplate;
        private TacticHand _tacticInventory;
        private bool _tacticChallengeActive;
        private bool _showingTactics;
        private BattleHandUI _tacticView;
        private Coroutine _tacticFlip;

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
            RefreshTacticSwitch();
        }

        private void UnbindTacticInventory()
        {
            if (_tacticInventory != null) _tacticInventory.Changed -= RefreshTacticSwitch;
            _tacticInventory = null;
        }

        private void RefreshTacticSwitch()
        {
            bool hasCards = _tacticInventory?.Cards.Count > 0 && !_showingDebugHand;
            if (!hasCards && _showingTactics)
            {
                if (_tacticChallengeActive)
                {
                    // Keep the challenge's bindings while returning to the ordinary hand.
                    _showingTactics = false;
                    StartTacticFlip(false);
                }
                else RestoreMainHand();
            }
            if (cardsTypeSwitchButton == null) return;
            cardsTypeSwitchButton.gameObject.SetActive(hasCards);
            cardsTypeSwitchButton.GetComponent<ArmyButtonUI>()?.SetSelected(_showingTactics);
        }

        private void OnCardsTypeSwitchClicked()
        {
            if (UIFocusUtility.IsGameplayInputBlocked || !(_tacticInventory?.Cards.Count > 0)) return;
            if (!_showingTactics)
            {
                EnsureTacticView(tacticHandTemplate);
                if (_tacticView == null) return;
                // A challenge owns the callbacks even while its hand is temporarily hidden.
                if (!_tacticChallengeActive) _tacticView.Bind(_tacticInventory);
            }
            else _tacticView?.CancelDrags();
            _showingTactics = !_showingTactics;
            CancelAttachMode();
            StartTacticFlip(_showingTactics);
            RefreshTacticSwitch();
        }

        public void SetTacticInputOverlay(bool raised)
        {
            if (cardsTypeSwitchButton == null) return;
            var canvas = cardsTypeSwitchButton.GetComponent<Canvas>();
            if (canvas == null && raised) canvas = cardsTypeSwitchButton.gameObject.AddComponent<Canvas>();
            if (canvas != null)
            {
                canvas.overrideSorting = raised;
                canvas.sortingOrder = 25000;
            }
            if (raised && cardsTypeSwitchButton.GetComponent<GraphicRaycaster>() == null)
                cardsTypeSwitchButton.gameObject.AddComponent<GraphicRaycaster>();
        }

        private void EnsureTacticView(BattleHandUI template)
        {
            if (_tacticView != null) return;
            if (template == null)
                foreach (var hand in FindObjectsByType<BattleHandUI>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                    if (!hand.Horizontal) { template = hand; break; }
            if (_tacticView == null && template != null && handContainer != null)
            {
                _tacticView = Instantiate(template, handContainer.parent);
                _tacticView.name = "MapTacticHand";
                _tacticView.MakeHorizontal();
                var rt = (RectTransform)_tacticView.transform;
                rt.anchorMin = handContainer.anchorMin;
                rt.anchorMax = handContainer.anchorMax;
                rt.pivot = handContainer.pivot;
                rt.sizeDelta = handContainer.sizeDelta;
                rt.anchoredPosition = handContainer.anchoredPosition;
                rt.localScale = Vector3.one;
                _tacticView.gameObject.SetActive(false);
            }
        }

        public BattleHandUI ShowTactics(BattleHandUI template, TacticHand inventory)
        {
            if (!(inventory?.Cards.Count > 0)) return null;
            EnsureTacticView(template);
            if (_tacticView == null) return null;
            _tacticChallengeActive = true;
            _tacticView?.Bind(inventory);
            if (!_showingTactics)
            {
                _showingTactics = true;
                CancelAttachMode();
                StartTacticFlip(true);
            }
            RefreshTacticSwitch();
            return _tacticView;
        }

        public void RestoreMainHand()
        {
            _tacticChallengeActive = false;
            _tacticView?.Bind(null);
            SetTacticInputOverlay(false);
            if (_showingTactics)
            {
                _showingTactics = false;
                StartTacticFlip(false);
            }
            RefreshTacticSwitch();
        }

        private void ResetTacticPresentation()
        {
            if (_tacticFlip != null) StopCoroutine(_tacticFlip);
            _tacticFlip = null;
            _showingTactics = false;
            _tacticChallengeActive = false;
            _tacticView?.Bind(null);
            SetTacticInputOverlay(false);
            SetTacticFace(false);
            if (handContainer != null) handContainer.localScale = Vector3.one;
            if (_tacticView != null) _tacticView.transform.localScale = Vector3.one;
        }

        private void StartTacticFlip(bool tactics)
        {
            if (_tacticFlip != null) StopCoroutine(_tacticFlip);
            _tacticFlip = null;
            if (handContainer != null) handContainer.localScale = Vector3.one;
            if (_tacticView != null) _tacticView.transform.localScale = Vector3.one;
            if (!gameObject.activeInHierarchy) { SetTacticFace(tactics); return; }
            _tacticFlip = StartCoroutine(FlipTacticHand(tactics));
        }

        private IEnumerator FlipTacticHand(bool tactics)
        {
            Transform outgoing = tactics ? handContainer : _tacticView?.transform;
            for (float t = 0f; t < 0.12f; t += Time.unscaledDeltaTime)
            {
                if (outgoing != null) outgoing.localScale = new Vector3(1f - t / 0.12f, 1f, 1f);
                yield return null;
            }
            SetTacticFace(tactics);
            Transform incoming = tactics ? _tacticView?.transform : handContainer;
            for (float t = 0f; t < 0.12f; t += Time.unscaledDeltaTime)
            {
                if (incoming != null) incoming.localScale = new Vector3(t / 0.12f, 1f, 1f);
                yield return null;
            }
            if (outgoing != null) outgoing.localScale = Vector3.one;
            if (incoming != null) incoming.localScale = Vector3.one;
            _tacticFlip = null;
        }

        private void SetTacticFace(bool tactics)
        {
            cardsTypeSwitchButton?.GetComponent<ArmyButtonUI>()?.SetSelected(tactics);
            if (handContainer != null) handContainer.gameObject.SetActive(!tactics);
            if (_tacticView != null) _tacticView.gameObject.SetActive(tactics);
            if (drawButton != null) drawButton.gameObject.SetActive(!tactics);
            if (scrollLeftButton != null) scrollLeftButton.gameObject.SetActive(!tactics);
            if (scrollRightButton != null) scrollRightButton.gameObject.SetActive(!tactics);
            if (cardSeparators != null)
                foreach (var separator in cardSeparators)
                    if (separator != null) separator.gameObject.SetActive(!tactics);
        }
    }
}
