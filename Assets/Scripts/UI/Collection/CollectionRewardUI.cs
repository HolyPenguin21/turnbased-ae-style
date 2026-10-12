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
        private System.Action resumedOnClosed;
        private string failedDismissMatchId;
        private string focusedKey;
        public void Configure(GameTurnController controller, GameConfig gameConfig)
        { turns = controller; config = gameConfig; turns.ParticipantFinished += OnResult; returnToMenu = true; }
        public void Resume(GameConfig gameConfig, System.Action onClosed = null)
        {
            config = gameConfig; resumedOnClosed = onClosed;
            if (!ProgressionContext.Initialize(config)) { CollectionMessageUI.ShowReward(ProgressionContext.Error, "REWARD UNAVAILABLE"); return; }
            if (ProgressionContext.Collection.Snapshot.pendingRewards.Count > 0) Draw();
            if (onClosed == null && !string.IsNullOrWhiteSpace(ProgressionContext.Notice)) CollectionMessageUI.ShowReward(ProgressionContext.Notice, "COLLECTION NOTICE");
        }
        private void OnResult(ParticipantResult result)
        {
            if (!Game.Campaign.CampaignMatchBridge.RecordFinalResult(out error)) retryResult = result;
            else if (!ProgressionContext.Initialize(config)) { error = ProgressionContext.Error; retryResult = result; }
            else if (!ProgressionContext.Rewards.Record(result, out error)) retryResult = result;
            else retryResult = null;
            selection.Clear(); focusedKey = null; Draw();
        }
        private void Draw()
        {
            if (canvas == null)
            {
                canvas = CollectionUIElements.Canvas("CollectionRewards", new Vector2(1920, 1080));
                var backdrop = CollectionUIElements.Panel(canvas, "RewardBackdrop"); CollectionUIElements.Stretch(backdrop);
                backdrop.GetComponent<Image>().color = new Color(0, 0, 0, .8f);
                root = CollectionUIElements.RewardWindow(backdrop, "Rewards", true);
            }
            UIFocusUtility.SetOverlay(this, true); CollectionUIElements.Clear(root);
            if (retryResult.HasValue)
            {
                DrawSaveError("Cannot save the match reward.\n" + error, () => OnResult(retryResult.Value)); return;
            }
            if (failedDismissMatchId != null)
            {
                DrawSaveError("Cannot save campaign continuation.\n" + error, () => FinishDismiss(failedDismissMatchId)); return;
            }
            var reward = ProgressionContext.Collection.Snapshot.pendingRewards.FirstOrDefault();
            if (reward == null)
            {
                if (returnToMenu && GameSession.CampaignContext != null)
                {
                    if (!Game.Campaign.CampaignMatchBridge.RecoverReward(config, out error))
                    { retryResult = new ParticipantResult(GameSession.MatchId, GameSession.FindHumanPlayer(), MatchOutcome.Draw); Draw(); return; }
                    Close(); GameSession.EndRewardEligibility(); SceneManager.LoadScene(SceneNames.Campaign); return;
                }
                Close(); var callback = resumedOnClosed; resumedOnClosed = null; callback?.Invoke(); return;
            }
            var keys = reward.claimed ? reward.acquiredKeys : ProgressionContext.Rewards.AvailableOffers(reward);
            if (!reward.claimed) selection.RemoveWhere(key => !keys.Contains(key));
            int required = Mathf.Min(2, keys.Count);
            string notice = !reward.claimed && keys.Count < reward.offeredKeys.Count
                ? $"{reward.offeredKeys.Count - keys.Count} offers unavailable after a content/limit update. Original identities retained; no replacements." : "";
            string title = reward.outcome == MatchOutcome.Victory ? "VICTORY" : "DEFEAT";
            CollectionUIElements.RewardTitle(root, title, true);
            var subtitle = CollectionUIElements.Label(root, keys.Count == 0 ? "No eligible rewards remain." : reward.claimed ? "REWARD ACQUIRED" : $"Choose {required} rewards — selected {selection.Count}/{required}", 88, 136, 1264, 46, 26);
            subtitle.alignment = TextAlignmentOptions.Center;
            Button firstChoice = null, focusedChoice = null;
            float rowWidth = keys.Count * 240 + Mathf.Max(0, keys.Count - 1) * 16;
            for (int i = 0; i < keys.Count; i++)
            {
                string key = keys[i]; var card = ProgressionContext.Collection.Rules.Resolve(key);
                // Five offers fit in one row; acquired cards and reduced offers stay centered.
                float x = (1440 - rowWidth) / 2 + i * 256;
                var cell = CollectionUIElements.Panel(root, key); CollectionUIElements.Place(cell, x, 204, 240, 438);
                CollectionUIElements.ApplySurface(cell.GetComponent<Image>(), "Panel_Inset");
                var art = CollectionUIElements.Rect(cell, "Art"); CollectionUIElements.Place(art, 26, 18, 188, 274);
                if (config != null && config.armyUnitCardPrefab != null)
                {
                    var preview = Instantiate(config.armyUnitCardPrefab, art);
                    preview.gameObject.AddComponent<RectMask2D>();
                    foreach (var label in preview.GetComponentsInChildren<TMP_Text>(true))
                    {
                        label.overflowMode = TextOverflowModes.Ellipsis;
                        label.enableAutoSizing = true;
                        label.fontSizeMax = label.fontSize;
                        label.fontSizeMin = Mathf.Min(label.fontSize, 5f);
                    }
                    preview.SetupPreview(card, config,
                        selectedCard => CollectionMessageUI.ShowReward(CollectionCardDetail.Describe(selectedCard, config, ProgressionContext.Collection.Owned(key))),
                        () => !CollectionMessageUI.IsShowing);
                    CollectionUIElements.FitCardPreview((RectTransform)preview.transform, art);
                }
                else
                {
                    var image = art.gameObject.AddComponent<Image>(); image.sprite = card?.art;
                    image.preserveAspect = true; image.raycastTarget = false;
                }
                var details = CollectionUIElements.RewardButton(cell, card?.displayName ?? key, 12, 310, 216, 52,
                    () => CollectionMessageUI.ShowReward(CollectionCardDetail.Describe(card, config, ProgressionContext.Collection.Owned(key))), 19);
                var name = details.GetComponentInChildren<TMP_Text>();
                name.enableAutoSizing = true; name.fontSizeMin = 14; name.fontSizeMax = 19;
                if (reward.claimed)
                {
                    var owned = CollectionUIElements.Label(cell, "Owned: " + ProgressionContext.Collection.Owned(key), 12, 380, 216, 40, 21);
                    owned.alignment = TextAlignmentOptions.Center;
                }
                else
                {
                    bool selected = selection.Contains(key);
                    var choose = CollectionUIElements.RewardButton(cell, selected ? "SELECTED" : "SELECT", 12, 380, 216, 40, () =>
                    { focusedKey = key; if (!selection.Remove(key) && selection.Count < required) selection.Add(key); Draw(); }, 20);
                    if (firstChoice == null) firstChoice = choose;
                    if (focusedKey == key) focusedChoice = choose;
                    if (selected)
                    {
                        var outline = cell.gameObject.AddComponent<Outline>();
                        outline.effectColor = new Color(.95f, .66f, .25f, .95f); outline.effectDistance = new Vector2(2, 2);
                        choose.GetComponent<Image>().color = new Color(1, .82f, .52f);
                    }
                }
            }
            var status = CollectionUIElements.Label(root, string.IsNullOrEmpty(error) ? notice : error, 96, 670, 1248, 62, 20);
            status.alignment = TextAlignmentOptions.Center; status.enableAutoSizing = true; status.fontSizeMin = 14; status.fontSizeMax = 20;
            if (!reward.claimed)
            {
                var button = CollectionUIElements.RewardButton(root, "CONFIRM REWARDS", 490, 760, 460, 54, () =>
                { ProgressionContext.Rewards.Claim(reward.matchId, selection, out error); Draw(); });
                button.interactable = selection.Count == required;
                if (focusedChoice != null) focusedChoice.Select();
                else if (firstChoice != null) firstChoice.Select();
                else button.Select();
            }
            else CollectionUIElements.RewardButton(root, "CONTINUE", 490, 760, 460, 54, () =>
            { if (!ProgressionContext.Rewards.Dismiss(reward.matchId, out error)) { Draw(); return; } FinishDismiss(reward.matchId); }).Select();
        }
        private void DrawSaveError(string message, System.Action retry)
        {
            CollectionUIElements.RewardTitle(root, "REWARD SAVE ERROR", true);
            var content = CollectionUIElements.RewardScroll(root, "SaveError", 130, 210, 1180, 440);
            var label = CollectionUIElements.Label(content, message, 0, 0, 1130, 1, 25);
            float height = Mathf.Max(1, label.GetPreferredValues(message, 1130, 10000).y);
            ((RectTransform)label.transform).sizeDelta = new Vector2(1130, height);
            content.sizeDelta = new Vector2(1130, height);
            CollectionUIElements.RewardButton(root, "RETRY", 490, 760, 460, 54, retry).Select();
        }
        private void FinishDismiss(string matchId)
        {
            string destination = Game.Campaign.CampaignMatchBridge.ReturnScene(matchId);
            if (!Game.Campaign.CampaignMatchBridge.RewardDismissed(matchId, out error))
            { failedDismissMatchId = matchId; Draw(); return; }
            failedDismissMatchId = null;
            if (ProgressionContext.Collection.Snapshot.pendingRewards.Count > 0) { Draw(); return; }
            Close();
            if (returnToMenu) { GameSession.EndRewardEligibility(); SceneManager.LoadScene(destination); }
            else { var callback = resumedOnClosed; resumedOnClosed = null; callback?.Invoke(); }
        }
        private void Close() { UIFocusUtility.SetOverlay(this, false); if (canvas != null) { canvas.gameObject.SetActive(false); Destroy(canvas.gameObject); } canvas = null; root = null; }
        private void OnDestroy() { if (turns != null) turns.ParticipantFinished -= OnResult; Close(); }
    }
}
