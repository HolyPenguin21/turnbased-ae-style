using System;
using System.Collections.Generic;
using Game.Audio;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Game.UI
{
    // Layout and skin live in Game.unity; this view only binds catalog data to templates.
    public sealed class GameHelpPanelUI : MonoBehaviour
    {
        [SerializeField] private GameHelpCatalog catalog;
        [SerializeField] private TMP_Text windowTitle, closeLabel, articleTitle;
        [SerializeField] private Button closeButton, topicButtonTemplate;
        [SerializeField] private TMP_Text sectionTitleTemplate, textBlockTemplate;
        [SerializeField] private Image imageBlockTemplate;
        [SerializeField] private ScrollRect topicsScroll, articleScroll;
        private readonly List<Button> topicButtons = new List<Button>();
        private readonly List<HelpTopic> topics = new List<HelpTopic>();
        private readonly List<TMP_Text> sectionLabels = new List<TMP_Text>();
        private readonly List<HelpText> sectionTitles = new List<HelpText>();
        private readonly List<GameObject> articleBlocks = new List<GameObject>();
        private Func<string, string> textResolver;
        private int selectedIndex = -1;
        private GameObject lastFocusedObject;
        public event Action Closed;
        public string SelectedTopicId => selectedIndex >= 0 ? topics[selectedIndex].id : null;

        public bool HasBindings => catalog != null && windowTitle != null && closeLabel != null
            && articleTitle != null && closeButton != null && topicButtonTemplate != null
            && topicButtonTemplate.GetComponentInChildren<TMP_Text>(true) != null
            && sectionTitleTemplate != null && textBlockTemplate != null && imageBlockTemplate != null
            && topicsScroll != null && topicsScroll.content != null && articleScroll != null
            && articleScroll.content != null;

        public void Show()
        {
            if (!HasBindings) return;
            gameObject.SetActive(true);
            if (topicButtons.Count == 0) BuildNavigation();
            RefreshLocalizedText();
            if (selectedIndex < 0 && topics.Count > 0) SelectTopic(0);
            else RebuildArticle();
            closeButton.Select();
        }

        // The future locale service supplies a key lookup and calls this when language changes.
        // The view retains topic identity, list position and all authored fallback strings.
        public void SetTextResolver(Func<string, string> resolver)
        {
            textResolver = resolver;
            if (!HasBindings) return;
            RefreshLocalizedText();
            if (gameObject.activeInHierarchy) RebuildArticle();
        }

        private string Text(HelpText value) => value?.Resolve(textResolver) ?? string.Empty;
        private void BuildNavigation()
        {
            sectionTitleTemplate.gameObject.SetActive(false);
            topicButtonTemplate.gameObject.SetActive(false);
            textBlockTemplate.gameObject.SetActive(false);
            imageBlockTemplate.gameObject.SetActive(false);
            foreach (var section in catalog.sections)
            {
                if (section == null) continue;
                var label = Instantiate(sectionTitleTemplate, topicsScroll.content);
                SceneUIAudioBinder.BindCreatedRoot(label);
                label.gameObject.SetActive(true);
                sectionLabels.Add(label); sectionTitles.Add(section.title);
                foreach (var topic in section.topics)
                {
                    if (topic == null) continue;
                    int index = topics.Count;
                    var button = Instantiate(topicButtonTemplate, topicsScroll.content);
                    SceneUIAudioBinder.BindCreatedRoot(button);
                    button.gameObject.SetActive(true);
                    button.onClick.AddListener(() => SelectTopic(index));
                    topicButtons.Add(button); topics.Add(topic);
                }
            }
        }

        private void RefreshLocalizedText()
        {
            windowTitle.text = Text(catalog.windowTitle);
            closeLabel.text = Text(catalog.closeLabel);
            for (int i = 0; i < sectionLabels.Count; i++) sectionLabels[i].text = Text(sectionTitles[i]);
            for (int i = 0; i < topicButtons.Count; i++)
                topicButtons[i].GetComponentInChildren<TMP_Text>(true).text = Text(topics[i].title);
        }

        public bool SelectTopic(string id)
        {
            int index = topics.FindIndex(t => t.id == id);
            if (index < 0) return false;
            SelectTopic(index); return true;
        }
        private void SelectTopic(int index)
        {
            if (index < 0 || index >= topics.Count) return;
            selectedIndex = index;
            // Selection remains visible after focus moves to the close button or article scrollbar.
            for (int i = 0; i < topicButtons.Count; i++)
            {
                var colors = topicButtons[i].colors;
                colors.normalColor = i == index ? new Color(.65f, .53f, .32f, 1f) : Color.white;
                topicButtons[i].colors = colors;
            }
            RebuildArticle();
        }

        private void RebuildArticle()
        {
            foreach (var block in articleBlocks)
            {
                block.SetActive(false); // Destroy is deferred; old layout must disappear immediately.
                Destroy(block);
            }
            articleBlocks.Clear();
            articleTitle.text = selectedIndex >= 0 ? Text(topics[selectedIndex].title) : string.Empty;
            if (selectedIndex < 0) return;
            var blocks = topics[selectedIndex].blocks;
            if (blocks == null || blocks.Length == 0) AddText(Text(catalog.pendingArticle));
            else foreach (var block in blocks)
            {
                if (block == null) continue;
                if (block.kind == HelpBlockKind.Image && block.image != null)
                {
                    var image = Instantiate(imageBlockTemplate, articleScroll.content);
                    SceneUIAudioBinder.BindCreatedRoot(image);
                    image.sprite = block.image;
                    image.gameObject.SetActive(true); articleBlocks.Add(image.gameObject);
                }
                string caption = Text(block.text);
                if (!string.IsNullOrEmpty(caption)) AddText(caption);
            }
            LayoutRebuilder.ForceRebuildLayoutImmediate(articleScroll.content);
            UIUtility.ResetDetailScroll(articleScroll);
        }
        private void AddText(string value)
        {
            var label = Instantiate(textBlockTemplate, articleScroll.content);
            SceneUIAudioBinder.BindCreatedRoot(label);
            label.text = value; label.gameObject.SetActive(true); articleBlocks.Add(label.gameObject);
        }
        private void OnEnable() { if (closeButton != null) closeButton.onClick.AddListener(Close); }
        private void Update()
        {
            var focused = EventSystem.current != null ? EventSystem.current.currentSelectedGameObject : null;
            if (focused != lastFocusedObject)
            {
                lastFocusedObject = focused;
                if (focused != null && topicsScroll != null && topicsScroll.viewport != null
                    && focused.transform.IsChildOf(topicsScroll.content)) RevealTopic(focused.transform);
            }
            if (Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame)
            {
                GameAudioManager.Instance?.PlayClick(); Close();
            }
        }
        private void RevealTopic(Transform row)
        {
            LayoutRebuilder.ForceRebuildLayoutImmediate(topicsScroll.content);
            var viewport = topicsScroll.viewport;
            var bounds = RectTransformUtility.CalculateRelativeRectTransformBounds(viewport, row);
            float shift = bounds.max.y > viewport.rect.yMax ? viewport.rect.yMax - bounds.max.y
                : bounds.min.y < viewport.rect.yMin ? viewport.rect.yMin - bounds.min.y : 0f;
            if (Mathf.Approximately(shift, 0f)) return;
            topicsScroll.StopMovement();
            topicsScroll.content.anchoredPosition += new Vector2(0f, shift);
        }
        public void Close() { if (gameObject.activeSelf) gameObject.SetActive(false); }
        private void OnDisable()
        {
            if (closeButton != null) closeButton.onClick.RemoveListener(Close);
            Closed?.Invoke();
        }
    }
}
