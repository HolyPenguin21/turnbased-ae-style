using System;
using System.Collections.Generic;
using System.Linq;
using Game.Core;
using Game.Map;
using Game.Setup;
using Game.UI;
using UnityEngine;

namespace Game.Turns
{
    public partial class GameTurnController
    {
        public bool CanSaveMatch(out string reason)
        {
            reason = MatchSaveEligibility.UnavailableReason(new MatchSaveConditions
            {
                HasMatch = !string.IsNullOrWhiteSpace(GameSession.MatchId) && TurnNumber > 0,
                HumanTurn = CurrentPlayer != null && CurrentPlayer.IsHuman && !CurrentPlayer.IsEliminated,
                TurnConfirmed = TurnConfirmed && !_turnConfirmationInProgress,
                GameOver = _gameOver,
                Restoring = MatchRestoreScope.IsRestoring || GameSession.PendingRestore != null,
                Combat = IsCombatPresentationActive || (hexSelectionController != null && hexSelectionController.IsBattleActive),
                Moving = ArmyRegistry.AllOccupiedHexes().SelectMany(ArmyRegistry.AllAt)
                    .Any(a => a?.Controller != null && a.Controller.IsMoving),
                Production = hexSelectionController != null && hexSelectionController.HasProductionTransaction,
                BlockingDecision = UIFocusUtility.HasOverlay || UIFocusUtility.IsTextFieldFocused()
                    || (popupPanel != null && popupPanel.IsShowing)
                    || (armyViewerModal != null && armyViewerModal.IsShowing)
                    || (baseViewerModal != null && baseViewerModal.IsShowing)
                    || (researchProductionModal != null && researchProductionModal.IsShowing)
                    || (eventChoicePopup != null && eventChoicePopup.IsShowing)
                    || (eventRewardPopup != null && eventRewardPopup.IsShowing)
                    || _aviationMessageQueue.Count > 0,
                CardOperation = _attachModeActive || (cardHand != null && cardHand.HasPendingCardOperation),
            });
            return reason == null;
        }

        public GameTurnState CaptureTurnState()
        {
            if (!CanSaveMatch(out string reason)) throw new InvalidOperationException(reason);
            var state = new GameTurnState
            {
                Round = TurnNumber,
                PlayerOrder = CurrentTurnOrder.Select(p => p.PlayerId).ToArray(),
                CurrentPlayerIndex = _currentPlayerIndex,
                Completed = GameSession.Players.Select(p => new PlayerTurnCount
                    { PlayerId = p.PlayerId, CompletedTurns = CompletedTurnsFor(p) }).ToArray(),
                GreetingsShown = _matchGreetingsShown.Select(p => p.PlayerId).ToArray(),
                PendingMessages = _pendingAviationMessages.Select(p => new PlayerTurnMessages
                    { PlayerId = p.Key.PlayerId, Messages = p.Value.ToArray() }).ToArray(),
            };
            state.Validate(GameSession.Players.Select(p => p.PlayerId), CurrentPlayer.PlayerId);
            return state;
        }

        public void ResumeGame(GameTurnState state)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            var players = GameSession.Players;
            var human = GameSession.FindHumanPlayer();
            state.Validate(players.Select(p => p.PlayerId), human?.PlayerId);
            if (human.IsEliminated) throw new ArgumentException("Cannot resume an eliminated human player.");
            var byId = players.ToDictionary(p => p.PlayerId, StringComparer.Ordinal);
            TurnNumber = state.Round;
            CurrentTurnOrder = state.PlayerOrder.Select(id => byId[id]).ToList();
            _currentPlayerIndex = state.CurrentPlayerIndex;
            CurrentPlayer = human;
            _completedTurns.Clear();
            foreach (var entry in state.Completed) _completedTurns.Add(byId[entry.PlayerId], entry.CompletedTurns);
            _matchGreetingsShown.Clear();
            foreach (string id in state.GreetingsShown) _matchGreetingsShown.Add(byId[id]);
            _pendingAviationMessages.Clear();
            foreach (var entry in state.PendingMessages)
                _pendingAviationMessages.Add(byId[entry.PlayerId], new List<string>(entry.Messages));
            _aviationMessageQueue.Clear();
            _turnConfirmationInProgress = false;
            _otherTurnPopupSuspended = false;
            _suspendedOtherTurnPlayer = null;
            _attachModeActive = false;
            _gameOver = false;
            TurnConfirmed = true;
            _turnConfirmedFrame = Time.frameCount;
            ConfigureStealthTurnProviders();
            VisionSystem.CurrentViewer = human;
            // Round/AP/income/movement/AA are already restored by their owners. Do not call
            // BeginGame, BeginNewTurn, BeginPlayerTurn or CompleteTurnConfirmation here.
            InitializeMatchPresentation();
            cardHand?.HideAiHandDebug();
            resourceBar?.HideRootDebug();
            RecomputeBlockedState();
            TurnStateChanged?.Invoke();
        }
    }
}
