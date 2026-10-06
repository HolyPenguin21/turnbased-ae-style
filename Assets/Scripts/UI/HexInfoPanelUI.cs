using System;
using UnityEngine;
using UnityEngine.UI;

namespace Game.UI
{
    // Contextual actions for the selected hex. Scene/prefab starts inactive; ShowHex
    // activates it before eligibility updates. Drawer layout is opt-in to preserve other
    // scenes/prefabs using the original navigation layout and the same public methods.
    public class HexInfoPanelUI : MonoBehaviour
    {
        [SerializeField] private GameObject panelRoot;
        [SerializeField] private Button garrisonButton;
        [SerializeField] private Button baseButton;
        [SerializeField] private Button researchButton;
        [SerializeField] private Button productionButton;

        [Header("Context drawer (Canvas_UI)")]
        [SerializeField] private bool drawerLayout;
        [SerializeField] private RectTransform drawerRect;
        [SerializeField] private RectTransform navigationRect;
        [SerializeField] private ResourceActionRowUI resourceActions;
        [SerializeField, Min(160f)] private float drawerWidth = 320f;
        [SerializeField, Min(0f)] private float padding = 24f;
        [SerializeField, Min(0f)] private float spacing = 8f;
        [SerializeField, Min(16f)] private float extractorHeight = 44f;
        [SerializeField, Min(0f)] private float resizeSpeed = 1000f;

        private readonly Button[] _navigation = new Button[4];

        private void LateUpdate()
        {
            RefreshDrawer(true);
        }

        // Called after selection has updated every action. Bottom pivot stays fixed while
        // the shell expands upward; unavailable actions occupy no cell.
        public void RefreshDrawer(bool animate = true)
        {
            if (!drawerLayout || drawerRect == null || navigationRect == null)
                return;
            _navigation[0] = baseButton;
            _navigation[1] = garrisonButton;
            _navigation[2] = researchButton;
            _navigation[3] = productionButton;
            int count = 0;
            foreach (Button item in _navigation)
                if (item != null && item.gameObject.activeSelf) count++;
            int extractionCount = resourceActions != null ? resourceActions.VisibleButtonCount : 0;
            if (count == 0 && extractionCount == 0)
            {
                if (panelRoot != null) panelRoot.SetActive(false);
                return;
            }

            float width = Mathf.Max(160f, drawerWidth);
            float inset = Mathf.Clamp(padding, 0f, width * 0.2f);
            float gap = Mathf.Clamp(spacing, 0f, width * 0.1f);
            float innerWidth = width - inset * 2f;
            float cell = (innerWidth - gap) * 0.5f;
            int rows = (count + 1) / 2;
            float navHeight = rows == 0 ? 0f : rows * cell + (rows - 1) * gap;
            int extractionRows = (extractionCount + 1) / 2;
            float actionHeight = extractionRows == 0 ? 0f : extractionRows * extractorHeight + (extractionRows - 1) * gap;
            float separation = count > 0 && extractionCount > 0 ? gap * 2f : 0f;
            float height = inset * 2f + navHeight + separation + actionHeight;
            float current = drawerRect.rect.height;
            float next = animate && Application.isPlaying && resizeSpeed > 0f
                ? Mathf.MoveTowards(current, height, resizeSpeed * Time.unscaledDeltaTime) : height;
            drawerRect.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, width);
            drawerRect.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, next);

            Place(navigationRect, inset, inset, innerWidth, navHeight);
            int index = 0;
            foreach (Button item in _navigation)
            {
                if (item == null || !item.gameObject.activeSelf) continue;
                Place((RectTransform)item.transform, (index % 2) * (cell + gap),
                    (index / 2) * (cell + gap), cell, cell);
                index++;
            }
            if (resourceActions != null)
                resourceActions.LayoutDrawer(inset, inset + navHeight + separation,
                    innerWidth, extractorHeight, gap);
        }

        internal static void Place(RectTransform rect, float x, float y, float width, float height)
        {
            rect.anchorMin = rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = new Vector2(x, -y);
            rect.sizeDelta = new Vector2(width, height);
        }

        public void ShowHex()
        {
            bool opening = panelRoot != null && !panelRoot.activeSelf;
            if (panelRoot != null)
                panelRoot.SetActive(true);
            if (opening && drawerLayout && drawerRect != null)
                drawerRect.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, padding * 2f);
        }

        public void Hide()
        {
            if (resourceActions != null)
                resourceActions.Hide();
            if (panelRoot != null)
                panelRoot.SetActive(false);
            if (garrisonButton != null)
                garrisonButton.gameObject.SetActive(false);
            if (baseButton != null)
                baseButton.gameObject.SetActive(false);
            if (researchButton != null)
                researchButton.gameObject.SetActive(false);
            if (productionButton != null)
                productionButton.gameObject.SetActive(false);
        }

        // Independent of ShowHex — a direct way to reach the garrison's modal regardless of
        // how many armies currently share the hex (see HexSelectionController.SelectHex),
        // since a lone unit sitting alone in the garrison would otherwise have no way to be
        // sorted into a movable army at all.
        public void SetGarrisonButtonVisible(bool visible, Action onClick)
        {
            if (garrisonButton == null)
                return;
            garrisonButton.gameObject.SetActive(visible);
            if (visible)
            {
                Game.UI.UIButtonEventUtility.ResetRuntimeListeners(garrisonButton);
                garrisonButton.onClick.AddListener(() => onClick?.Invoke());
            }
        }

        // Same idea as SetGarrisonButtonVisible, for BaseViewerModalUI — visible whenever this
        // hex's building has IsBase set and is owned by the current player (see
        // HexSelectionController.SelectHex).
        public void SetBaseButtonVisible(bool visible, Action onClick)
        {
            if (baseButton == null)
                return;
            baseButton.gameObject.SetActive(visible);
            if (visible)
            {
                Game.UI.UIButtonEventUtility.ResetRuntimeListeners(baseButton);
                baseButton.onClick.AddListener(() => onClick?.Invoke());
            }
        }

        // Same idea as SetGarrisonButtonVisible/SetBaseButtonVisible — Research now lives in
        // this fixed nav row instead of the variable-length ResourceActionRowUI (see
        // HexSelectionController.SelectHex), so it stays put next to Garrison/Base/Production
        // rather than shifting around with however many extraction-Facility buttons show.
        public void SetResearchButtonVisible(bool visible, Action onClick)
        {
            if (researchButton == null)
                return;
            researchButton.gameObject.SetActive(visible);
            if (visible)
            {
                Game.UI.UIButtonEventUtility.ResetRuntimeListeners(researchButton);
                researchButton.onClick.AddListener(() => onClick?.Invoke());
            }
        }

        // Same idea, for Production.
        public void SetProductionButtonVisible(bool visible, Action onClick)
        {
            if (productionButton == null)
                return;
            productionButton.gameObject.SetActive(visible);
            if (visible)
            {
                Game.UI.UIButtonEventUtility.ResetRuntimeListeners(productionButton);
                productionButton.onClick.AddListener(() => onClick?.Invoke());
            }
        }
    }
}
