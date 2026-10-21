using System.Collections.Generic;
using System.Linq;
using Game.Core;
using Game.Cards;
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
        private RectTransform canvas;
        private CollectionRewardView view;
        private CollectionRewardCardView cardTemplate;
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
                view = Instantiate(GameAssetCatalog.Load<GameObject>("Prefabs/Rewards/CollectionRewards").GetComponent<CollectionRewardView>(), canvas);
                cardTemplate = GameAssetCatalog.Load<GameObject>("Prefabs/Rewards/CollectionRewardCard").GetComponent<CollectionRewardCardView>();
            }
            UIFocusUtility.SetOverlay(this, true);
            CollectionUIElements.Clear(view.CardsHost);
            view.CardsHost.gameObject.SetActive(true);
            view.BodyRoot.SetActive(false);
            view.Subtitle.gameObject.SetActive(true);
            view.Status.gameObject.SetActive(true);
            view.PrimaryButton.onClick.RemoveAllListeners();
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
            view.Title.text = title;
            view.Subtitle.text = keys.Count == 0 ? "No eligible rewards remain." : reward.claimed ? "REWARD ACQUIRED" : $"Choose {required} rewards - selected {selection.Count}/{required}";
            Button firstChoice = null, focusedChoice = null;
            for (int i = 0; i < keys.Count; i++)
            {
                string key = keys[i]; var card = ProgressionContext.Collection.Rules.Resolve(key);
                var cell = Instantiate(cardTemplate, view.CardsHost);
                cell.CopyCountLabel.text = $"Owned: {ProgressionContext.Collection.Owned(key)} / {DeckRules.CopyLimit(card)}";
                cell.Preview.SetupPreview(card, config,
                    selectedCard => CollectionMessageUI.ShowReward(CollectionCardDetail.Describe(selectedCard, config, ProgressionContext.Collection.Owned(key))),
                    () => !CollectionMessageUI.IsShowing);
                cell.DetailsButton.GetComponentInChildren<TMP_Text>().text = card?.displayName ?? key;
                cell.DetailsButton.onClick.AddListener(() => CollectionMessageUI.ShowReward(
                    CollectionCardDetail.Describe(card, config, ProgressionContext.Collection.Owned(key))));
                cell.ChooseButton.gameObject.SetActive(!reward.claimed);
                cell.SetSelected(!reward.claimed && selection.Contains(key));
                if (!reward.claimed)
                {
                    var choose = cell.ChooseButton;
                    choose.onClick.AddListener(() =>
                    { focusedKey = key; if (!selection.Remove(key) && selection.Count < required) selection.Add(key); Draw(); });
                    if (firstChoice == null) firstChoice = choose;
                    if (focusedKey == key) focusedChoice = choose;
                }
            }
            view.Status.text = string.IsNullOrEmpty(error) ? notice : error;
            var button = view.PrimaryButton;
            button.GetComponentInChildren<TMP_Text>().text = reward.claimed ? "CONTINUE" : "CONFIRM REWARDS";
            button.interactable = reward.claimed || selection.Count == required;
            if (!reward.claimed)
            {
                button.onClick.AddListener(() =>
                { ProgressionContext.Rewards.Claim(reward.matchId, selection, out error); Draw(); });
                if (focusedChoice != null) focusedChoice.Select();
                else if (firstChoice != null) firstChoice.Select();
                else button.Select();
            }
            else
            {
                button.onClick.AddListener(() =>
                { if (!ProgressionContext.Rewards.Dismiss(reward.matchId, out error)) { Draw(); return; } FinishDismiss(reward.matchId); });
                button.Select();
            }
        }
        private void DrawSaveError(string message, System.Action retry)
        {
            view.Title.text = "REWARD SAVE ERROR";
            view.CardsHost.gameObject.SetActive(false);
            view.Subtitle.gameObject.SetActive(false);
            view.Status.gameObject.SetActive(false);
            view.BodyRoot.SetActive(true);
            view.Body.text = message;
            view.PrimaryButton.GetComponentInChildren<TMP_Text>().text = "RETRY";
            view.PrimaryButton.interactable = true;
            view.PrimaryButton.onClick.AddListener(() => retry());
            view.PrimaryButton.Select();
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
        private void Close() { UIFocusUtility.SetOverlay(this, false); if (canvas != null) { canvas.gameObject.SetActive(false); Destroy(canvas.gameObject); } canvas = null; view = null; }
        private void OnDestroy() { if (turns != null) turns.ParticipantFinished -= OnResult; Close(); }
    }
}

