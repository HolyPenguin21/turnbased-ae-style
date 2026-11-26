using System;
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
        internal const string RewardPanelKey = "Prefabs/Rewards/CollectionRewards";
        internal const string RewardCardKey = "Prefabs/Rewards/CollectionRewardCard";
        private GameTurnController turns;
        private GameConfig config;
        private RectTransform canvas;
        private CollectionRewardView view;
        private CollectionRewardCardView cardTemplate;
        private readonly HashSet<string> selection = new HashSet<string>();
        private MatchResultOperation operation;
        private Action resumedOnClosed;
        private string displayedMatchId, focusedKey;
        private bool exiting;

        public void Configure(GameTurnController controller, GameConfig gameConfig)
        {
            if (turns != null) turns.MatchResultReady -= OnMatchResultReady;
            turns = controller;
            config = gameConfig;
            turns.MatchResultReady += OnMatchResultReady;
            OnMatchResultReady();
        }

        public void Resume(GameConfig gameConfig, Action onClosed = null)
        {
            var resumed = MatchResultOperation.Open(gameConfig, MatchResultOrigin.MainMenu);
            resumed.Advance();
            Present(gameConfig, resumed, onClosed);
            if (resumed.Current.Stage != MatchResultStage.MenuNotice && onClosed == null
                && !string.IsNullOrWhiteSpace(ProgressionContext.Notice))
                CollectionMessageUI.ShowReward(ProgressionContext.Notice, "COLLECTION NOTICE");
        }

        internal void Present(GameConfig gameConfig, MatchResultOperation result, Action onClosed)
        {
            config = gameConfig;
            operation = result;
            resumedOnClosed = onClosed;
            Draw();
        }

        internal void ClearPresentation()
        {
            operation = null;
            resumedOnClosed = null;
            Close();
        }

        private void OnMatchResultReady()
        {
            operation = turns.ResultOperation;
            Draw();
        }

        private bool IsCurrent(MatchResultOperation displayed) => !exiting && isActiveAndEnabled
            && ReferenceEquals(operation, displayed) && (turns == null || ReferenceEquals(turns.ResultOperation, displayed));

        private void Draw()
        {
            var displayed = operation;
            if (displayed == null) { Close(); return; }
            if (!IsCurrent(displayed)) return;
            var state = displayed.Current;
            if (state.Stage == MatchResultStage.MenuNotice)
            {
                ClearPresentation();
                CollectionMessageUI.ShowReward(state.Error, "REWARD UNAVAILABLE");
                return;
            }
            if (state.Stage != MatchResultStage.Retry && state.Reward == null)
            {
                CompletePresentation(state.Stage);
                return;
            }

            if (canvas == null)
            {
                canvas = CollectionUIElements.Canvas("CollectionRewards", new Vector2(1920, 1080));
                view = Instantiate(GameAssetCatalog.Load<GameObject>(RewardPanelKey).GetComponent<CollectionRewardView>(), canvas);
                cardTemplate = GameAssetCatalog.Load<GameObject>(RewardCardKey).GetComponent<CollectionRewardCardView>();
            }
            UIFocusUtility.SetModal(this, canvas.gameObject, ModalBlock.Full, view.PrimaryButton);
            CollectionUIElements.Clear(view.CardsHost);
            view.CardsHost.gameObject.SetActive(true);
            view.BodyRoot.SetActive(false);
            view.Subtitle.gameObject.SetActive(true);
            view.Status.gameObject.SetActive(true);
            view.PrimaryButton.onClick.RemoveAllListeners();
            if (state.Stage == MatchResultStage.Retry)
            {
                DrawSaveError(state.Error, () =>
                {
                    if (!IsCurrent(displayed) || !ReferenceEquals(state, displayed.Current)) return;
                    displayed.Advance();
                    Draw();
                });
                return;
            }
            var reward = state.Reward;
            if (displayedMatchId != reward.MatchId)
            {
                displayedMatchId = reward.MatchId;
                selection.Clear();
                focusedKey = null;
            }

            var keys = reward.Keys;
            if (!reward.Claimed) selection.RemoveWhere(key => !keys.Contains(key));
            int required = reward.RequiredSelectionCount;
            string notice = reward.UnavailableCount > 0
                ? $"{reward.UnavailableCount} reward cards are no longer available." : "";
            string title = reward.Outcome == MatchOutcome.Victory ? "VICTORY" : "DEFEAT";
            view.Title.text = title;
            view.Subtitle.text = keys.Count == 0 ? "No eligible rewards remain." : reward.Claimed ? "REWARD ACQUIRED" : $"Choose {required} rewards - selected {selection.Count}/{required}";
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
                cell.ChooseButton.gameObject.SetActive(!reward.Claimed);
                cell.SetSelected(!reward.Claimed && selection.Contains(key));
                if (!reward.Claimed)
                {
                    var choose = cell.ChooseButton;
                    choose.onClick.AddListener(() =>
                    {
                        if (!IsCurrent(displayed) || !ReferenceEquals(state, displayed.Current)) return;
                        focusedKey = key;
                        if (!selection.Remove(key) && selection.Count < required) selection.Add(key);
                        Draw();
                    });
                    if (firstChoice == null) firstChoice = choose;
                    if (focusedKey == key) focusedChoice = choose;
                }
            }
            view.Status.text = string.IsNullOrEmpty(state.Error) ? notice : state.Error;
            var button = view.PrimaryButton;
            button.GetComponentInChildren<TMP_Text>().text = reward.Claimed ? "CONTINUE" : "CONFIRM REWARDS";
            button.interactable = reward.Claimed || selection.Count == required;
            if (!reward.Claimed)
            {
                button.onClick.AddListener(() =>
                {
                    if (!IsCurrent(displayed) || !ReferenceEquals(state, displayed.Current)) return;
                    displayed.Claim(reward.MatchId, selection);
                    Draw();
                });
                if (focusedChoice != null) focusedChoice.Select();
                else if (firstChoice != null) firstChoice.Select();
                else button.Select();
            }
            else
            {
                button.onClick.AddListener(() =>
                {
                    if (!IsCurrent(displayed) || !ReferenceEquals(state, displayed.Current)) return;
                    displayed.Dismiss(reward.MatchId);
                    Draw();
                });
                button.Select();
            }
        }
        private void DrawSaveError(string message, Action retry)
        {
            view.Title.text = "REWARD ERROR";
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
        private void CompletePresentation(MatchResultStage stage)
        {
            Close();
            if (stage == MatchResultStage.ReturnToCampaign || stage == MatchResultStage.ReturnToMenu)
            {
                exiting = true;
                Game.Setup.MatchStateInventory.EndMatch(turns);
                SceneManager.LoadScene(stage == MatchResultStage.ReturnToCampaign ? SceneNames.Campaign : SceneNames.MainMenu);
                return;
            }
            var callback = resumedOnClosed;
            resumedOnClosed = null;
            callback?.Invoke();
        }

        private void Close()
        {
            UIFocusUtility.SetModal(this, null);
            if (canvas != null)
            {
                canvas.gameObject.SetActive(false);
                Destroy(canvas.gameObject);
            }
            canvas = null;
            view = null;
        }

        private void OnEnable()
        {
            if (turns != null) OnMatchResultReady();
            else Draw();
        }

        private void OnDisable() => Close();

        private void OnDestroy()
        {
            if (turns != null) turns.MatchResultReady -= OnMatchResultReady;
            Close();
        }
    }
}
