using Game.Cards;
using Game.Combat;
using Game.Players;
using Game.Turns;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Game.UI
{
    public partial class BattleAttackPopupUI
    {
        private bool _tacticsCritical;
        private ResearchProductionChallengeSession _rpTactics;
        private CardData _selectedTactic;
        private Button _tacticSlot;
        private TMP_Text _tacticSlotText;
        private BattleTacticCardUI _tacticPreview;
        private BattleHandUI _tacticHand;
        private CardHandUI _mapHand;
        private GameTurnController _tacticTurnController;
        private PopupPanelUI _tacticInfoPopup;
        private int _tacticInfoDismissedFrame = -1;
        private bool _tacticInfoWasShowing;
        private int _tacticDecisionVersion;

        private bool TacticInputBlocked => UIFocusUtility.IsGameplayInputBlocked
            || (_tacticInfoPopup != null && _tacticInfoPopup.IsShowing && !_tacticInfoPopup.IsOtherTurnShowing)
            || _tacticInfoDismissedFrame == Time.frameCount;

        private void OnDisable()
        {
            _awaitingHumanDecision = false;
            CloseTacticPresentation();
            FinalizeResearchProduction(false);
        }

        private PlayerSetupData HumanParticipant() => IsHumanSide(_defender) ? _defender?.Owner
            : IsHumanSide(_attacker) ? _attacker?.Owner : null;

        private void PrepareTacticPresentation(PlayerSetupData owner)
        {
            if (owner?.IsHuman != true || _automateHumanSides || panelRoot == null) return;
            _tacticTurnController = FindAnyObjectByType<GameTurnController>(FindObjectsInactive.Include);
            _tacticInfoPopup = FindAnyObjectByType<PopupPanelUI>(FindObjectsInactive.Include);
            if (_tacticInfoPopup != null)
            {
                _tacticInfoWasShowing = _tacticInfoPopup.IsShowing && !_tacticInfoPopup.IsOtherTurnShowing;
                _tacticInfoPopup.VisibilityChanged -= OnTacticInfoVisibilityChanged;
                _tacticInfoPopup.VisibilityChanged += OnTacticInfoVisibilityChanged;
            }
            BattleHandUI template = null;
            foreach (var hand in FindObjectsByType<BattleHandUI>(FindObjectsInactive.Include))
                if (!hand.Horizontal) { template = hand; break; }
            _mapHand = FindAnyObjectByType<CardHandUI>(FindObjectsInactive.Include);
            _tacticHand = _mapHand != null && _mapHand.gameObject.activeInHierarchy
                ? _mapHand.ShowTactics(template, owner.Tactics) : template;
            if (_tacticHand != null)
            {
                _tacticHand.Bind(owner.Tactics);
                // The existing full-screen modal blocks ordinary map input. Only the tactic
                // hand is brought above it; its buttons still use the challenge decision gate.
                // '??' would not work here: in the editor GetComponent returns a non-null placeholder for a missing component.
                var canvas = _tacticHand.GetComponent<Canvas>();
                if (canvas == null) canvas = _tacticHand.gameObject.AddComponent<Canvas>();
                canvas.overrideSorting = true;
                canvas.sortingOrder = 25000;
                if (_tacticHand.GetComponent<GraphicRaycaster>() == null)
                    _tacticHand.gameObject.AddComponent<GraphicRaycaster>();
            }
            if (_tacticSlot == null && acceptButton != null)
            {
                _tacticSlot = Instantiate(acceptButton, rollStateRoot != null ? rollStateRoot.transform : panelRoot.transform);
                _tacticSlot.name = "TacticSlot";
                _tacticSlot.onClick = new Button.ButtonClickedEvent();
                _tacticSlot.onClick.AddListener(ApplySelectedTactic);
                var rect = (RectTransform)_tacticSlot.transform;
                rect.anchorMin = rect.anchorMax = new Vector2(1f, 0.5f);
                rect.pivot = new Vector2(0f, 0.5f);
                rect.anchoredPosition = new Vector2(15f, 0f);
                rect.sizeDelta = new Vector2(145f, 200f);
                _tacticSlotText = _tacticSlot.GetComponentInChildren<TMP_Text>(true);
                if (_tacticSlotText != null)
                {
                    _tacticSlotText.fontSize = 16f;
                    var textRect = _tacticSlotText.rectTransform;
                    textRect.anchorMin = Vector2.zero;
                    textRect.anchorMax = new Vector2(1f, 0f);
                    textRect.pivot = new Vector2(0.5f, 0f);
                    textRect.anchoredPosition = Vector2.zero;
                    textRect.sizeDelta = new Vector2(-8f, 42f);
                }
            }
            CloseTacticDecision();
            OnTacticInfoVisibilityChanged();
        }

        private string TacticSelectionFailure(CardData card) => TacticSelectionFailure(card, true);

        private string TacticSelectionFailure(CardData card, bool checkInput)
        {
            if (checkInput && TacticInputBlocked) return "Close the information window before continuing the challenge.";
            if (_phase != Phase.InProgress || !_awaitingHumanDecision || _humanSpent || _humanDeclined)
                return "Wait for your challenge decision. The defender acts first.";
            var owner = (_kind == ChallengeKind.ResearchProduction ? _rpHero
                : _defenderTurnActive ? _defender : _attacker)?.Owner;
            if (_automateHumanSides || owner?.IsHuman != true)
                return "It is not your challenge decision.";
            if (_kind == ChallengeKind.ResearchProduction)
                return _rpTactics == null ? "There is no active challenge." : _rpTactics.TacticUnavailableReason(card);
            return _challengeSession == null ? "There is no active challenge."
                : _challengeSession.TacticUnavailableReason(_defenderTurnActive, card);
        }

        private bool CanSelectTactic(CardData card) => TacticSelectionFailure(card, false) == null;

        private void ShowTacticError(string message)
        {
            // Reuse the game's blocking info panel. Its normal sibling order must also cover
            // the temporarily raised tactic hand, so lower that hand while the hint is open.
            if (_tacticInfoPopup != null && _tacticInfoPopup.IsShowing && !_tacticInfoPopup.IsOtherTurnShowing) return;
            _tacticHand?.CancelDrags();
            if (_tacticTurnController != null) _tacticTurnController.ShowSpawnHint(message);
            else _tacticInfoPopup?.ShowHint(message);
        }

        private void OnTacticInfoVisibilityChanged()
        {
            bool showing = _tacticInfoPopup != null && _tacticInfoPopup.IsShowing && !_tacticInfoPopup.IsOtherTurnShowing;
            if (_tacticInfoWasShowing && !showing) _tacticInfoDismissedFrame = Time.frameCount;
            _tacticInfoWasShowing = showing;
            if (_tacticHand == null) return;
            if (showing) _tacticHand.CancelDrags();
            var canvas = _tacticHand.GetComponent<Canvas>();
            if (canvas != null) canvas.overrideSorting = !showing;
        }

        private void OpenTacticDecision(bool defenderSide)
        {
            _defenderTurnActive = defenderSide;
            _selectedTactic = null;
            int decisionVersion = ++_tacticDecisionVersion;
            if (_tacticHand == null) return;
            var owner = (_kind == ChallengeKind.ResearchProduction ? _rpHero
                : defenderSide ? _defender : _attacker)?.Owner;
            _tacticHand.Bind(owner?.Tactics, CanSelectTactic, SelectTactic, TacticSelectionFailure,
                (card, point, camera) => TryDropTactic(card, point, camera, decisionVersion), ShowTacticError);
            if (_tacticSlot != null)
            {
                _tacticSlot.gameObject.SetActive(true);
                _tacticSlot.interactable = false;
                if (_tacticSlotText != null) _tacticSlotText.text = "Tactic slot — select a card";
            }
            RefreshTacticTotals();
        }

        private void SelectTactic(CardData card)
        {
            string reason = TacticSelectionFailure(card);
            if (reason != null) { ShowTacticError(reason); return; }
            if (_tacticPreview != null) Destroy(_tacticPreview.gameObject);
            _tacticPreview = null;
            if (ReferenceEquals(_selectedTactic, card))
            {
                _selectedTactic = null;
                if (_tacticSlot != null) _tacticSlot.interactable = false;
                if (_tacticSlotText != null) _tacticSlotText.text = "Select a tactic";
                return;
            }
            _selectedTactic = card;
            if (_tacticSlot != null) _tacticPreview = _tacticHand.CreatePreview(_tacticSlot.transform, card);
            if (_tacticSlotText != null)
                _tacticSlotText.text = "Apply tactic";
            if (_tacticSlot != null) _tacticSlot.interactable = true;
        }

        private bool TryDropTactic(CardData card, Vector2 point, Camera camera, int decisionVersion)
        {
            if (decisionVersion != _tacticDecisionVersion)
            {
                ShowTacticError("This challenge decision has ended. Try again on your next decision.");
                return false;
            }
            string reason = TacticSelectionFailure(card);
            if (reason != null) { ShowTacticError(reason); return false; }
            if (_tacticSlot == null || !_tacticSlot.gameObject.activeInHierarchy
                || !RectTransformUtility.RectangleContainsScreenPoint((RectTransform)_tacticSlot.transform, point, camera))
            {
                if (_tacticHand?.ContainsScreenPoint(point, camera) == true) return false;
                ShowTacticError("Drop the tactical card into the challenge's tactic slot.");
                return false;
            }
            return TryApplyTactic(card);
        }

        private void ApplySelectedTactic() => TryApplyTactic(_selectedTactic);

        private bool TryApplyTactic(CardData card)
        {
            string reason = TacticSelectionFailure(card);
            if (reason != null) { ShowTacticError(reason); return false; }
            bool applied = _kind == ChallengeKind.ResearchProduction
                ? _rpTactics.TryPlay(card, out reason) : _challengeSession.TryPlayTactic(_defenderTurnActive, card, out reason);
            if (!applied) { ShowTacticError(reason ?? "The tactical card could not be applied."); return false; }
            _selectedTactic = null;
            _rerollAnimDone = true;
            _humanSpent = true;
            ShowTacticResult(card);
            if (acceptButton != null) acceptButton.interactable = false;
            attackerRow?.SetSpendInteractable(false);
            defenderRow?.SetSpendInteractable(false);
            CloseTacticDecision();
            return true;
        }

        private void ShowTacticResult(CardData card)
        {
            RefreshTacticTotals();
            BattleDebugLog.Write($"[Tactic] {card.Definition.authoredKey}: {TacticRules.Description(card.Definition)}");
        }

        private void RefreshTacticTotals()
        {
            if (_kind == ChallengeKind.ResearchProduction && _rpTactics != null)
            {
                attackerRow?.SetSuccessModifier(_rpTactics.SuccessModifier);
                defenderRow?.SetFixedSuccesses(_rpTactics.Required);
            }
            else if (_challengeSession != null)
            {
                attackerRow?.SetSuccessModifier(_challengeSession.AttackerModifier);
                defenderRow?.SetSuccessModifier(_challengeSession.DefenderModifier);
            }
        }

        private void CloseTacticDecision()
        {
            ++_tacticDecisionVersion;
            _selectedTactic = null;
            if (_tacticPreview != null) Destroy(_tacticPreview.gameObject);
            _tacticPreview = null;
            if (_tacticSlot != null) _tacticSlot.gameObject.SetActive(false);
            if (_tacticHand != null) _tacticHand.Bind(HumanParticipant()?.Tactics);
        }

        private void CloseTacticPresentation()
        {
            CloseTacticDecision();
            if (_tacticInfoPopup != null) _tacticInfoPopup.VisibilityChanged -= OnTacticInfoVisibilityChanged;
            _tacticInfoPopup = null;
            _tacticInfoWasShowing = false;
            if (_tacticHand != null)
            {
                _tacticHand.Bind(_tacticHand.Horizontal ? null : HumanParticipant()?.Tactics);
                var canvas = _tacticHand.GetComponent<Canvas>();
                if (canvas != null) canvas.overrideSorting = false;
            }
            _tacticHand = null;
            if (_mapHand != null) _mapHand.RestoreMainHand();
            _mapHand = null;
        }
    }
}
