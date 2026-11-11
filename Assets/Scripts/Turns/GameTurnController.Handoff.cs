using System;
using System.Collections;
using System.Collections.Generic;
using Game.Core;
using Game.Map;
using Game.Players;
using UnityEngine;

namespace Game.Turns
{
    // "Giving the screen to a player": the hotseat curtain, the seat and the rendered viewer.
    // Kept apart from the turn loop so a networked client can replace it with its own version
    // (the seat there is fixed on the local player and the curtain never shows).
    //
    // Order invariant (risk P3): curtain first (seat = null, opaque backdrop), and only after the
    // named player confirms: viewer -> player, seat -> player, then the curtain hides.
    // Never VisionSystem.CurrentViewer = null in a match with humans (null = whole map visible).
    public partial class GameTurnController : IDecisionHandoff
    {
        // Several local humans share this screen.
        public bool IsHotseat => LocalSeat.IsHotseat(GameSession.Players);

        // Shows the curtain naming `player`; on confirm switches the screen to them and runs
        // onSeated. A one-human match never calls this (the seat is implicit there).
        private void ShowHandoffCurtain(PlayerSetupData player, string message, Action onSeated, bool renderTheirFog = true)
        {
            LocalSeat.Set(null);
            if (popupPanel == null)
            {
                SeatPlayer(player, renderTheirFog);
                onSeated?.Invoke();
                return;
            }
            // The session generation, not matchGeneration: citadel placement hands over before
            // BeginGame has stamped the match.
            long generation = GameSession.Generation;
            popupPanel.ShowHandoff(message, () =>
            {
                if (_gameOver || !GameSession.IsCurrent(generation)) return;
                SeatPlayer(player, renderTheirFog);
                onSeated?.Invoke();
            });
        }

        // Behind the curtain: render `player`'s fog, give them the private UI.
        private void SeatPlayer(PlayerSetupData player, bool renderTheirFog = true)
        {
            if (player == null) return;
            if (!renderTheirFog) { LocalSeat.Set(player); return; }
            bool viewerChanged = !ReferenceEquals(VisionSystem.CurrentViewer, player);
            VisionSystem.CurrentViewer = player;
            LocalSeat.Set(player);
            // Only a real perspective change needs the fog/markers re-read (risk P13).
            if (viewerChanged) TurnStateChanged?.Invoke();
        }

        private static string TurnHandoffText(PlayerSetupData player)
            => $"Turn of {player?.Nickname}\nPass the screen to {player?.Nickname}.";

        // ResumeGame: a hotseat save opens behind the current human's curtain; the world was
        // restored, so no BeginPlayerTurn (it would refill movement/AA).
        private void ResumeSeatAfterLoad(PlayerSetupData human)
        {
            if (!IsHotseat || human == null) return;
            TurnConfirmed = false;
            RefreshEndTurnInteractable();
            ShowHandoffCurtain(human, TurnHandoffText(human), () =>
            {
                if (CurrentPlayer != human) return;
                popupPanel?.Hide();
                TurnConfirmed = true;
                _turnConfirmedFrame = Time.frameCount;
                RecomputeBlockedState();
                TurnStateChanged?.Invoke();
            });
        }

        // Citadel placement in hotseat: each human takes the screen before placing.
        public void ShowSetupHandoff(PlayerSetupData player, Action onSeated)
        {
            if (player == null || !player.IsLocalHuman || !IsHotseat)
            {
                onSeated?.Invoke();
                return;
            }
            // Placement runs with no viewer (the whole map is shown to every placer, as before):
            // only the seat changes here.
            ShowHandoffCurtain(player, $"Citadel placement: {player.Nickname}\nPass the screen to {player.Nickname}.", () =>
            {
                popupPanel?.Hide();
                onSeated?.Invoke();
            }, renderTheirFog: false);
        }

        // IDecisionHandoff — a decision that belongs to another local human (PlayerDecisionRouter).
        public IEnumerator HandTo(PlayerSetupData owner, string reason, Action<PlayerSetupData> previousSeat)
        {
            PlayerSetupData previous = LocalSeat.Current ?? LocalSeat.Last;
            previousSeat?.Invoke(previous);
            if (owner == null || LocalSeat.Is(owner)) yield break;
            bool seated = false;
            long generation = GameSession.Generation;
            ShowHandoffCurtain(owner, $"{reason}\nPass the screen to {owner.Nickname}.", () =>
            {
                popupPanel?.Hide();
                seated = true;
            });
            while (!seated && !_gameOver && GameSession.IsCurrent(generation))
                yield return null;
        }

        public IEnumerator ReturnTo(PlayerSetupData player)
        {
            if (player == null || LocalSeat.Is(player) || _gameOver) yield break;
            bool seated = false;
            long generation = GameSession.Generation;
            ShowHandoffCurtain(player, $"Return the screen to {player.Nickname}.", () =>
            {
                popupPanel?.Hide();
                seated = true;
            });
            while (!seated && !_gameOver && GameSession.IsCurrent(generation))
                yield return null;
            // An AI turn in progress gets its banner back once the screen is returned.
            if (seated && popupPanel != null && !popupPanel.IsShowing && !_aiObserverMatch
                && (CurrentPlayer == null || !CurrentPlayer.IsHuman))
                popupPanel.ShowForOther(CurrentPlayer, _otherTurnActivity);
        }

        // R5: a message meant for `owner`. Shown now when owner holds the screen (or when no seat
        // concept applies), otherwise queued with the owner's turn reports (saved with the turn,
        // shown when the owner's next turn is confirmed). AI owners: nothing to show.
        public void NotifyPlayer(PlayerSetupData owner, string message)
        {
            if (string.IsNullOrEmpty(message) || owner == null || !owner.IsHuman) return;
            if (owner.IsLocalHuman && (LocalSeat.Is(owner) || !IsHotseat))
            {
                ShowSpawnHint(message);
                return;
            }
            if (owner == CurrentPlayer && TurnConfirmed)
            {
                // Their own confirmed turn, the screen lent to someone else for a moment:
                // shown once the screen is back and nothing else is open.
                _aviationMessageQueue.Enqueue(message);
                return;
            }
            if (!_pendingAviationMessages.TryGetValue(owner, out List<string> list))
                _pendingAviationMessages[owner] = list = new List<string>();
            list.Add(message);
        }
    }
}
