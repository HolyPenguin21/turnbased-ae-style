using System.Collections.Generic;
using System.Linq;
using Game.Core;
using Game.Progression;
using Game.Turns;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Game.UI
{
    public sealed class CollectionRewardUI : MonoBehaviour
    {
        private GameTurnController turns;
        private GameConfig config;
        private RectTransform canvas, root;
        private readonly HashSet<string> selection = new HashSet<string>();
        private ParticipantResult? retryResult;
        private string error;
        private bool returnToMenu;
        public void Configure(GameTurnController controller, GameConfig gameConfig)
        { turns = controller; config = gameConfig; turns.ParticipantFinished += OnResult; returnToMenu = true; }
        public void Resume(GameConfig gameConfig)
        {
            config = gameConfig;
            if (!ProgressionContext.Initialize(config)) { CollectionScreensUI.ShowMessage(transform, ProgressionContext.Error); return; }
            if (ProgressionContext.Collection.Snapshot.pendingRewards.Count > 0) Draw();
            if (!string.IsNullOrWhiteSpace(ProgressionContext.Notice)) CollectionScreensUI.ShowMessage(transform, ProgressionContext.Notice);
        }
        private void OnResult(ParticipantResult result)
        {
            if (!ProgressionContext.Initialize(config)) { error = ProgressionContext.Error; retryResult = result; }
            else if (!ProgressionContext.Rewards.Record(result, out error)) retryResult = result;
            else retryResult = null;
            selection.Clear(); Draw();
        }
        private void Draw()
        {
            if (canvas == null) { canvas = CollectionUIElements.Canvas("CollectionRewards"); root = CollectionUIElements.Panel(canvas, "Rewards"); CollectionUIElements.Stretch(root); }
            UIFocusUtility.SetOverlay(this, true); CollectionUIElements.Clear(root);
            if (retryResult.HasValue)
            {
                CollectionUIElements.Label(root, "Cannot save the match reward.\n" + error, 160, 200, 704, 160, 22);
                CollectionUIElements.Button(root, "Retry", 412, 400, 200, 40, () => OnResult(retryResult.Value)); return;
            }
            var reward = ProgressionContext.Collection.Snapshot.pendingRewards.FirstOrDefault();
            if (reward == null) { Close(); return; }
            var keys = reward.claimed ? reward.acquiredKeys : ProgressionContext.Rewards.AvailableOffers(reward);
            if (!reward.claimed) selection.RemoveWhere(key => !keys.Contains(key));
            int required = Mathf.Min(2, keys.Count);
            string notice = !reward.claimed && keys.Count < reward.offeredKeys.Count
                ? $"{reward.offeredKeys.Count - keys.Count} offers unavailable after a content/limit update. Original identities retained; no replacements." : "";
            string title = reward.outcome == MatchOutcome.Victory ? "VICTORY" : "DEFEAT";
            CollectionUIElements.Label(root, title, 200, 30, 624, 48, 30);
            CollectionUIElements.Label(root, keys.Count == 0 ? "No eligible rewards remain." : reward.claimed ? "Reward acquired" : $"Choose {required} rewards — selected {selection.Count}/{required}", 200, 84, 624, 42, 22);
            for (int i = 0; i < keys.Count; i++)
            {
                string key = keys[i]; var card = ProgressionContext.Collection.Rules.Resolve(key);
                float x = 145 + i % 3 * 248, y = 140 + i / 3 * 218;
                var cell = CollectionUIElements.Panel(root, key); CollectionUIElements.Place(cell, x, y, 235, 206);
                var art = CollectionUIElements.Rect(cell, "Art"); CollectionUIElements.Place(art, 4, 4, 227, 140);
                var image = art.gameObject.AddComponent<Image>(); image.sprite = card?.art; image.preserveAspect = true;
                CollectionUIElements.Button(cell, card?.displayName ?? key, 4, 148, 227, 25, () => CollectionScreensUI.ShowMessage(root, CollectionCardDetail.Describe(card, config, ProgressionContext.Collection.Owned(key))));
                CollectionUIElements.Button(cell, reward.claimed ? "Owned: " + ProgressionContext.Collection.Owned(key) : selection.Contains(key) ? "Selected" : "Select", 4, 179, 227, 25, () =>
                { if (reward.claimed) return; if (!selection.Remove(key) && selection.Count < required) selection.Add(key); Draw(); });
                if (selection.Contains(key)) cell.GetComponent<Image>().color = new Color(.28f, .4f, .24f);
            }
            CollectionUIElements.Label(root, string.IsNullOrEmpty(error) ? notice : error, 140, 596, 744, 45);
            if (!reward.claimed)
            {
                var button = CollectionUIElements.Button(root, "Confirm", 412, 656, 200, 40, () =>
                { ProgressionContext.Rewards.Claim(reward.matchId, selection, out error); Draw(); });
                button.interactable = selection.Count == required;
            }
            else CollectionUIElements.Button(root, "Continue", 412, 656, 200, 40, () =>
            { if (!ProgressionContext.Rewards.Dismiss(reward.matchId, out error)) { Draw(); return; } if (ProgressionContext.Collection.Snapshot.pendingRewards.Count > 0) Draw(); else { Close(); if (returnToMenu) { GameSession.EndRewardEligibility(); SceneManager.LoadScene(SceneNames.MainMenu); } } });
        }
        private void Close() { UIFocusUtility.SetOverlay(this, false); if (canvas != null) Destroy(canvas.gameObject); canvas = null; root = null; }
        private void OnDestroy() { if (turns != null) turns.ParticipantFinished -= OnResult; Close(); }
    }
}
