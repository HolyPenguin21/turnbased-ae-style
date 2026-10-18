using System;
using System.Collections;
using Game.Cards;
using UnityEngine;

namespace Game.UI
{
    public partial class CardHandUI
    {
        private bool _showingTactics;
        private BattleHandUI _tacticView;
        private Coroutine _tacticFlip;

        public BattleHandUI ShowTactics(BattleHandUI template, TacticHand inventory)
        {
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
            _tacticView?.Bind(inventory);
            if (!_showingTactics)
            {
                _showingTactics = true;
                CancelAttachMode();
                StartTacticFlip(true);
            }
            return _tacticView;
        }

        public void RestoreMainHand()
        {
            if (!_showingTactics) return;
            _showingTactics = false;
            _tacticView?.Bind(null);
            StartTacticFlip(false);
        }

        private void ResetTacticPresentation()
        {
            if (_tacticFlip != null) StopCoroutine(_tacticFlip);
            _tacticFlip = null;
            _showingTactics = false;
            _tacticView?.Bind(null);
            SetTacticFace(false);
            if (handContainer != null) handContainer.localScale = Vector3.one;
            if (_tacticView != null) _tacticView.transform.localScale = Vector3.one;
        }

        private void StartTacticFlip(bool tactics)
        {
            if (_tacticFlip != null) StopCoroutine(_tacticFlip);
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
