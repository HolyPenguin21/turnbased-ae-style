using System;
using UnityEngine;
using UnityEngine.UI;

namespace Game.UI
{
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
        [SerializeField] private ResourceActionRowUI resourceActions;
        [Tooltip("Downward offsets as fractions of screen height, for 0 through 4 unextracted resources.")]
        [SerializeField] private float[] resourceScreenOffsets =
            { 107f / 768f, 81f / 768f, 56f / 768f, 31f / 768f, 0f };
        [SerializeField, Min(0.01f)] private float motionSmoothTime = 0.06f;

        private int _resourceCount;
        private float _verticalVelocity;
        private bool _showRequested;
        private bool _closing;

        public bool UsesDrawerLayout => drawerLayout;

        private void Awake()
        {
            if (drawerRect == null) drawerRect = transform as RectTransform;
            // ShowHex can activate an initially inactive root, invoking Awake synchronously.
            // In that case the selection request wins over the initial hidden state.
            if (Application.isPlaying && !_showRequested) Hide();
        }

        private void LateUpdate() => RefreshDrawer(true);

        public void RefreshDrawer(bool animate = true)
        {
            if (!drawerLayout || drawerRect == null || (!_showRequested && !_closing)) return;
            int index = Mathf.Clamp(_resourceCount, 0, 4);
            float offset = resourceScreenOffsets != null && resourceScreenOffsets.Length == 5
                ? resourceScreenOffsets[index] : DefaultScreenOffset(index);
            float target = _closing ? HiddenPositionY() : -offset * ScreenHeightInParentUnits();
            Vector2 position = drawerRect.anchoredPosition;
            position.y = animate && Application.isPlaying
                ? Mathf.SmoothDamp(position.y, target, ref _verticalVelocity,
                    Mathf.Max(0.01f, motionSmoothTime), Mathf.Infinity, Time.unscaledDeltaTime)
                : target;
            if (Mathf.Abs(position.y - target) < 0.05f)
            {
                position.y = target;
                _verticalVelocity = 0f;
            }
            drawerRect.anchoredPosition = position;
            if (_closing && position.y == target) Hide();
        }

        private static float DefaultScreenOffset(int count)
        {
            switch (count)
            {
                case 1: return 81f / 768f;
                case 2: return 56f / 768f;
                case 3: return 31f / 768f;
                case 4: return 0f;
                default: return 107f / 768f;
            }
        }

        private float ScreenHeightInParentUnits()
        {
            Canvas canvas = drawerRect.GetComponentInParent<Canvas>();
            if (canvas != null && canvas.rootCanvas.transform is RectTransform canvasRect)
            {
                Vector3 height = canvasRect.TransformVector(Vector3.up * canvasRect.rect.height);
                return drawerRect.parent != null
                    ? Mathf.Abs(drawerRect.parent.InverseTransformVector(height).y) : height.magnitude;
            }
            return drawerRect.parent is RectTransform parent ? parent.rect.height : 768f;
        }

        private float HiddenPositionY()
        {
            if (!(drawerRect.parent is RectTransform parent))
                return -ScreenHeightInParentUnits() - drawerRect.rect.height;
            float anchorY = Mathf.Lerp(drawerRect.anchorMin.y, drawerRect.anchorMax.y, drawerRect.pivot.y);
            float anchorPosition = parent.rect.yMin + parent.rect.height * anchorY;
            return parent.rect.yMin - anchorPosition
                - drawerRect.rect.height * (1f - drawerRect.pivot.y) - 1f;
        }

        public void ShowHex(int unextractedResourceCount = 0)
        {
            bool opening = (!_showRequested && !_closing) || (panelRoot != null && !panelRoot.activeSelf);
            _resourceCount = Mathf.Clamp(unextractedResourceCount, 0, 4);
            _showRequested = true;
            _closing = false;
            if (panelRoot != null) panelRoot.SetActive(true);
            if (drawerLayout && drawerRect != null && opening)
            {
                Vector2 position = drawerRect.anchoredPosition;
                position.y = HiddenPositionY();
                drawerRect.anchoredPosition = position;
                _verticalVelocity = 0f;
            }
        }

        public void Hide()
        {
            _showRequested = false;
            _closing = false;
            _verticalVelocity = 0f;
            if (resourceActions != null) resourceActions.Hide();
            SetButton(garrisonButton, false, null);
            SetButton(baseButton, false, null);
            SetButton(researchButton, false, null);
            SetButton(productionButton, false, null);
            if (panelRoot != null) panelRoot.SetActive(false);
        }

        // Used for an empty hex selection. Deselect/battle/startup keep immediate Hide.
        public void HideAnimated()
        {
            if (!drawerLayout || drawerRect == null || panelRoot == null || !panelRoot.activeSelf)
            {
                Hide();
                return;
            }
            _showRequested = false;
            _closing = true;
            if (resourceActions != null) resourceActions.Hide();
            SetButton(garrisonButton, false, null);
            SetButton(baseButton, false, null);
            SetButton(researchButton, false, null);
            SetButton(productionButton, false, null);
        }

        // Preserve the existing selection API. Drawer sections are always present;
        // other scenes can keep the original hide/show behavior.
        private void SetButton(Button button, bool available, Action onClick)
        {
            if (button == null) return;
            UIButtonEventUtility.ResetRuntimeListeners(button);
            button.gameObject.SetActive(drawerLayout || available);
            button.interactable = available;
            if (available) button.onClick.AddListener(() => onClick?.Invoke());
        }

        public void SetGarrisonButtonVisible(bool visible, Action onClick) => SetButton(garrisonButton, visible, onClick);
        public void SetBaseButtonVisible(bool visible, Action onClick) => SetButton(baseButton, visible, onClick);
        public void SetResearchButtonVisible(bool visible, Action onClick) => SetButton(researchButton, visible, onClick);
        public void SetProductionButtonVisible(bool visible, Action onClick) => SetButton(productionButton, visible, onClick);
    }
}
