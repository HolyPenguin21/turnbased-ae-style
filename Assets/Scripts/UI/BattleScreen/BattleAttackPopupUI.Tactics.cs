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
        // Reservation is presentation-only. The session consumes only on Accept.
        private CardData _pendingTactic;
        private CardData _displayedCommittedTactic;
        private bool _pendingTacticDefender;
        private bool _tacticCommittedThisTurn;
        private CardUI _slottedTacticView;
        private Button _tacticSlot;
        [SerializeField] private Image tacticalCardInput;
        [SerializeField] private TMP_Text currentResultText;
        private Sprite _tacticInputDefaultSprite;
        private Color _tacticInputDefaultColor;
        private bool _tacticInputInitialized;
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
            _mapHand = FindAnyObjectByType<CardHandUI>(FindObjectsInactive.Include);
            if (_mapHand != null && _mapHand.gameObject.activeInHierarchy)
                _mapHand.ShowTactics(owner.Tactics);
            else
            {
                _mapHand = null;
                _tacticHand = FindAnyObjectByType<BattleHandUI>(FindObjectsInactive.Include);
                _tacticHand?.Bind(owner.Tactics);
                SetBattleTacticOverlay(true);
            }
            InitializeTacticInput();
            _mapHand?.SetTacticInputOverlay(true);
            CloseTacticDecision();
            OnTacticInfoVisibilityChanged();
        }

        private void InitializeTacticInput()
        {
            if (_tacticInputInitialized || tacticalCardInput == null) return;
            _tacticInputInitialized = true;
            _tacticInputDefaultSprite = tacticalCardInput.sprite;
            _tacticInputDefaultColor = tacticalCardInput.color;
            _tacticSlot = tacticalCardInput.GetComponent<Button>();
            if (_tacticSlot == null) _tacticSlot = tacticalCardInput.gameObject.AddComponent<Button>();
            _tacticSlot.targetGraphic = tacticalCardInput;
            _tacticSlot.transition = Selectable.Transition.None;
            _tacticSlot.onClick.AddListener(StageSelectedTactic);
            _tacticSlot.interactable = false;
        }

        private void ResetTacticInput()
        {
            InitializeTacticInput();
            _displayedCommittedTactic = null;
            RefreshTacticInputFace();
            if (_slottedTacticView != null) _slottedTacticView.gameObject.SetActive(false);
            if (currentResultText != null) currentResultText.text = string.Empty;
        }

        private void RefreshTacticInputFace()
        {
            InitializeTacticInput();
            if (tacticalCardInput == null) return;
            bool showCommitted = _pendingTactic == null && _displayedCommittedTactic?.Definition != null;
            tacticalCardInput.sprite = showCommitted ? _displayedCommittedTactic.Definition.art : _tacticInputDefaultSprite;
            tacticalCardInput.color = showCommitted ? Color.white : _tacticInputDefaultColor;
        }

        private PlayerSetupData TacticOwner() => _kind == ChallengeKind.ResearchProduction
            ? _rpHero?.Owner : HumanParticipant();

        private string TacticSelectionFailure(CardData card) => TacticSelectionFailure(card, true);

        private string TacticSelectionFailure(CardData card, bool checkInput)
        {
            if (checkInput && TacticInputBlocked) return "Close the message to continue.";
            if (_phase != Phase.InProgress || !_awaitingHumanDecision || _humanSpent || _humanDeclined)
                return "Wait for your challenge turn; the defender acts first.";
            var owner = (_kind == ChallengeKind.ResearchProduction ? _rpHero
                : _defenderTurnActive ? _defender : _attacker)?.Owner;
            if (_automateHumanSides || owner?.IsHuman != true)
                return "Wait for your turn in this challenge.";
            if (_kind == ChallengeKind.ResearchProduction)
                return _rpTactics == null ? "No active challenge." : _rpTactics.TacticUnavailableReason(card);
            return _challengeSession == null ? "No active challenge."
                : _challengeSession.TacticUnavailableReason(_defenderTurnActive, card);
        }

        private bool CanSelectTactic(CardData card) => TacticSelectionFailure(card, false) == null;

        private void ShowTacticError(string message)
        {
            // Reuse the game's blocking info panel. Its normal sibling order must also cover
            // the temporarily raised tactic hand, so lower that hand while the hint is open.
            if (_tacticInfoPopup != null && _tacticInfoPopup.IsShowing && !_tacticInfoPopup.IsOtherTurnShowing) return;
            CancelTacticDrags();
            if (_tacticTurnController != null) _tacticTurnController.ShowSpawnHint(message);
            else _tacticInfoPopup?.ShowHint(message);
        }

        private void OnTacticInfoVisibilityChanged()
        {
            bool showing = _tacticInfoPopup != null && _tacticInfoPopup.IsShowing && !_tacticInfoPopup.IsOtherTurnShowing;
            if (_tacticInfoWasShowing && !showing) _tacticInfoDismissedFrame = Time.frameCount;
            _tacticInfoWasShowing = showing;
            if (showing) CancelTacticDrags();
            SetBattleTacticOverlay(!showing);
            _mapHand?.SetTacticInputOverlay(!showing);
        }

        private void SetBattleTacticOverlay(bool raised)
        {
            if (_tacticHand == null) return;
            var canvas = _tacticHand.GetComponent<Canvas>();
            if (canvas == null && raised) canvas = _tacticHand.gameObject.AddComponent<Canvas>();
            if (canvas != null)
            {
                canvas.overrideSorting = raised;
                canvas.sortingOrder = 25000;
            }
            if (raised && _tacticHand.GetComponent<GraphicRaycaster>() == null)
                _tacticHand.gameObject.AddComponent<GraphicRaycaster>();
        }

        private void CancelTacticDrags()
        {
            _tacticHand?.CancelDrags();
            _mapHand?.CancelTacticDrags();
            _slottedTacticView?.CancelDrag();
        }

        private bool ContainsTacticHand(Vector2 point, Camera camera) => _mapHand != null
            ? _mapHand.ContainsTacticScreenPoint(point, camera)
            : _tacticHand?.ContainsScreenPoint(point, camera) == true;

        private void BindTacticHand(bool interactive)
        {
            var owner = interactive
                ? (_kind == ChallengeKind.ResearchProduction ? _rpHero
                    : _defenderTurnActive ? _defender : _attacker)?.Owner
                : TacticOwner();
            int version = _tacticDecisionVersion;
            System.Func<CardData, bool> canSelect = interactive ? CanSelectTactic : null;
            System.Action<CardData> select = interactive ? SelectTactic : null;
            System.Func<CardData, string> reason = interactive ? TacticSelectionFailure : null;
            System.Func<CardData, Vector2, Camera, bool> drop = interactive
                ? (card, point, camera) => TryDropTactic(card, point, camera, version) : null;
            if (_mapHand != null)
                _mapHand.BindTactics(owner?.Tactics, canSelect, select, reason, drop, ShowTacticError, _pendingTactic);
            else _tacticHand?.Bind(owner?.Tactics, canSelect, select, reason, drop, ShowTacticError, _pendingTactic);
            RefreshSlottedTactic();
            OfferTacticTutorials();
        }

        private bool TacticDragInProgress => _slottedTacticView?.IsDragging == true
            || (_mapHand != null ? _mapHand.TutorialActionInProgress
                : _tacticHand != null && _tacticHand.TutorialDragInProgress);

        private bool TacticTutorialDecisionReady(int version) => this != null && IsShowing
            && version == _tacticDecisionVersion && _phase == Phase.InProgress
            && _awaitingHumanDecision && !_humanSpent && !_humanDeclined
            && !RunsAutomatically && !TacticInputBlocked
            && !TacticDragInProgress
            && (_mapHand != null ? _mapHand.TutorialTacticInputReady
                : _tacticHand != null && _tacticHand.TutorialInputReady)
            && (_kind == ChallengeKind.ResearchProduction ? _rpHero
                : _defenderTurnActive ? _defender : _attacker)?.Owner?.IsHuman == true;

        private void OfferTacticTutorials()
        {
            if (tacticalCardInput == null) return;
            int version = _tacticDecisionVersion;
            TutorialHintController.OfferDropTarget(TutorialHintId.Tactic, tacticalCardInput.rectTransform,
                () => TacticTutorialDecisionReady(version) && _pendingTactic == null
                    && (_kind == ChallengeKind.ResearchProduction ? _rpTactics?.HasTactic == true
                        : _challengeSession?.HasTactic(_defenderTurnActive) == true));
            TutorialHintController.OfferDropTarget(TutorialHintId.EditTactic, tacticalCardInput.rectTransform,
                () => TacticTutorialDecisionReady(version) && SlottedTacticFailure() == null);
        }

        private void OpenTacticDecision(bool defenderSide)
        {
            _defenderTurnActive = defenderSide;
            _selectedTactic = null;
            ++_tacticDecisionVersion;
            BindTacticHand(true);
            if (_tacticSlot != null)
            {
                _tacticSlot.interactable = false;
            }
            RefreshTacticTotals();
        }

        private void SelectTactic(CardData card)
        {
            string reason = TacticSelectionFailure(card);
            if (reason != null) { ShowTacticError(reason); return; }
            if (ReferenceEquals(_selectedTactic, card))
            {
                _selectedTactic = null;
                if (_tacticSlot != null) _tacticSlot.interactable = false;
                return;
            }
            _selectedTactic = card;
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
                if (ContainsTacticHand(point, camera)) return false;
                ShowTacticError("Drop the tactical card into the challenge's tactic slot.");
                return false;
            }
            return TryStageTactic(card);
        }

        private void StageSelectedTactic() => TryStageTactic(_selectedTactic);

        private bool TryStageTactic(CardData card)
        {
            string reason = TacticSelectionFailure(card);
            if (reason != null) { ShowTacticError(reason); return false; }
            bool replacing = _pendingTactic != null && !ReferenceEquals(_pendingTactic, card);
            _pendingTactic = card;
            _pendingTacticDefender = _defenderTurnActive;
            _selectedTactic = null;
            if (_tacticSlot != null) _tacticSlot.interactable = false;
            TutorialHintController.Learned(TutorialHintId.Tactic);
            if (replacing) TutorialHintController.Learned(TutorialHintId.EditTactic);
            BindTacticHand(true);
            RefreshTacticTotals();
            RefreshTacticSpendButtons();
            return true;
        }

        private string SlottedTacticFailure() => _pendingTactic == null
            || _phase != Phase.InProgress || !_awaitingHumanDecision || _humanSpent || _humanDeclined
            || _pendingTacticDefender != _defenderTurnActive
                ? "Wait for your challenge decision."
                : TacticInputBlocked ? "Close the message to continue." : null;

        private void RefreshSlottedTactic()
        {
            RefreshTacticInputFace();
            if (tacticalCardInput == null) return;
            if (_pendingTactic == null)
            {
                if (_slottedTacticView != null) _slottedTacticView.gameObject.SetActive(false);
                return;
            }
            if (_slottedTacticView == null)
            {
                var go = new GameObject("SlottedTactic", typeof(RectTransform), typeof(Image), typeof(CardUI));
                var rect = (RectTransform)go.transform;
                rect.SetParent(tacticalCardInput.transform, false);
                rect.anchorMin = Vector2.zero;
                rect.anchorMax = Vector2.one;
                rect.sizeDelta = Vector2.zero;
                _slottedTacticView = go.GetComponent<CardUI>();
            }
            var image = _slottedTacticView.GetComponent<Image>();
            image.sprite = _pendingTactic.Definition.art;
            image.color = Color.white;
            _slottedTacticView.gameObject.SetActive(true);
            int version = _tacticDecisionVersion;
            _slottedTacticView.ConfigureTacticInteraction(_pendingTactic,
                () => { if (_selectedTactic != null) StageSelectedTactic(); }, SlottedTacticFailure,
                (point, camera) => TryReturnTactic(point, camera, version), ShowTacticError);
            _slottedTacticView.SetTacticHome(Vector2.zero, 1f);
        }

        private bool TryReturnTactic(Vector2 point, Camera camera, int version)
        {
            if (version != _tacticDecisionVersion) return false;
            string reason = SlottedTacticFailure();
            if (reason != null) { ShowTacticError(reason); return false; }
            if (!ContainsTacticHand(point, camera))
            {
                ShowTacticError("Drop the tactical card back into your hand.");
                return false;
            }
            _pendingTactic = null;
            TutorialHintController.Learned(TutorialHintId.EditTactic);
            BindTacticHand(true);
            RefreshTacticTotals();
            RefreshTacticSpendButtons();
            return true;
        }

        private bool CommitPendingTactic()
        {
            if (_pendingTactic == null) return true;
            CardData card = _pendingTactic;
            string reason = TacticSelectionFailure(card);
            if (reason != null) { ShowTacticError(reason); return false; }
            // Clear the preview before Consume publishes the inventory change, avoiding double effects.
            _pendingTactic = null;
            bool applied = _kind == ChallengeKind.ResearchProduction
                ? _rpTactics.TryPlay(card, out reason)
                : _challengeSession.TryPlayTactic(_defenderTurnActive, card, out reason);
            if (!applied)
            {
                _pendingTactic = card;
                ShowTacticError(reason ?? "The tactical card could not be applied.");
                return false;
            }
            _tacticCommittedThisTurn = true;
            ShowTacticResult(card);
            RefreshSlottedTactic();
            return true;
        }

        // Always rebuild from committed modifiers + the current roll; never undo a clamped effect.
        private void GetTacticPreview(out int attackerModifier, out int defenderModifier)
        {
            bool production = _kind == ChallengeKind.ResearchProduction;
            attackerModifier = production ? _rpTactics?.SuccessModifier ?? 0 : _challengeSession?.AttackerModifier ?? 0;
            defenderModifier = production ? _rpTactics?.DifficultyModifier ?? 0 : _challengeSession?.DefenderModifier ?? 0;
            if (_pendingTactic == null) return;
            int opposing = production ? _rpTactics.Required
                : _pendingTacticDefender ? _challengeSession.CurrentResult.AttackerSuccesses
                    : _challengeSession.CurrentResult.DefenderSuccesses;
            TacticRules.Apply(_pendingTactic.Definition, _pendingTacticDefender, opposing,
                ref attackerModifier, ref defenderModifier);
        }

        private bool CanSpendResearchProductionFate()
        {
            if (_rpHero == null || _rpTactics == null) return false;
            GetTacticPreview(out int a, out int d);
            return ResearchProductionSystem.CanSpendFate(_rpTokens, _rpHero.Fate,
                System.Math.Max(0, _rpTactics.BaseRequired + d) - a);
        }

        private void RefreshTacticSpendButtons()
        {
            if (!_awaitingHumanDecision || _humanSpent || _humanDeclined) return;
            if (_kind == ChallengeKind.ResearchProduction)
            {
                bool canSpend = CanSpendResearchProductionFate();
                attackerRow?.SetSpendInteractable(canSpend);
                OfferFateTutorial(attackerRow, false, canSpend);
            }
        }

        private void ShowTacticResult(CardData card)
        {
            _displayedCommittedTactic = card;
            RefreshTacticInputFace();
            RefreshTacticTotals();
            BattleDebugLog.Write($"[Tactic] {card.Definition.authoredKey}: {TacticRules.Description(card.Definition)}");
        }

        private void RefreshTacticTotals()
        {
            GetTacticPreview(out int a, out int d);
            RefreshCurrentResult();
            if (_kind == ChallengeKind.ResearchProduction && _rpTactics != null)
            {
                attackerRow?.SetSuccessModifier(a);
                defenderRow?.SetFixedSuccesses(System.Math.Max(0, _rpTactics.BaseRequired + d));
                if (_rpHero != null) attackerRow?.SetFatePreview(_rpHero.Fate);
            }
            else if (_challengeSession != null)
            {
                attackerRow?.SetSuccessModifier(a);
                defenderRow?.SetSuccessModifier(d);
            }
        }

        private void RefreshCurrentResult()
        {
            if (currentResultText == null) return;
            GetTacticPreview(out int a, out int d);
            bool ready;
            bool hit;
            if (_kind == ChallengeKind.ResearchProduction)
            {
                ready = _rpTactics?.Tokens != null;
                hit = ready && System.Math.Max(0, ResearchProductionSystem.CountHits(_rpTactics.Tokens) + a)
                    >= System.Math.Max(0, _rpTactics.BaseRequired + d);
            }
            else
            {
                ready = _challengeSession?.AttackerTokens != null && _challengeSession.DefenderTokens != null;
                var result = ready ? new ChallengeResult(_challengeSession.AttackerTokens,
                    _challengeSession.DefenderTokens, a, d) : null;
                hit = ready && (_kind == ChallengeKind.CaptureKill
                    ? result.AttackerSuccesses >= result.DefenderSuccesses : result.Damage > 0);
            }
            currentResultText.text = ready ? (hit ? "Result : Hit" : "Result : Miss") : string.Empty;
        }

        private void CloseTacticDecision()
        {
            ++_tacticDecisionVersion;
            _selectedTactic = null;
            if (_tacticSlot != null) _tacticSlot.interactable = false;
            BindTacticHand(false);
        }

        private void CloseTacticPresentation()
        {
            _pendingTactic = null;
            _tacticCommittedThisTurn = false;
            CancelTacticDrags();
            CloseTacticDecision();
            ResetTacticInput();
            if (_tacticInfoPopup != null) _tacticInfoPopup.VisibilityChanged -= OnTacticInfoVisibilityChanged;
            _tacticInfoPopup = null;
            _tacticInfoWasShowing = false;
            if (_tacticHand != null)
            {
                _tacticHand.Bind(TacticOwner()?.Tactics);
                var canvas = _tacticHand.GetComponent<Canvas>();
                if (canvas != null) canvas.overrideSorting = false;
            }
            _tacticHand = null;
            if (_mapHand != null) _mapHand.RestoreMainHand();
            _mapHand = null;
        }
    }
}
