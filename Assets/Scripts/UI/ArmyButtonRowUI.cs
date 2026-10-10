using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Game.Core;
using Game.Map;
using UnityEngine;
using UnityEngine.UI;

namespace Game.UI
{
    // Shared map selector and modal switcher. Only the map presentation (showStats) slides.
    public class ArmyButtonRowUI : MonoBehaviour
    {
        [SerializeField] private GameObject panelRoot;
        [SerializeField] private Transform buttonContainer;
        [SerializeField] private GameConfig gameConfig;
        [SerializeField] private Button scrollLeftButton;
        [SerializeField] private Button scrollRightButton;
        [SerializeField] private int maxVisible = 99;
        [SerializeField, Min(0.01f)] private float slideDuration = 0.2f;
        [SerializeField, Min(0f)] private float entryDelay = 0.05f;

        private readonly List<ArmyButtonUI> _buttons = new List<ArmyButtonUI>();
        // Layout controls the slots; animation moves only their button children.
        private readonly List<GameObject> _slots = new List<GameObject>();
        private List<ArmyData> _armies = new List<ArmyData>();
        private Action<ArmyData> _onArmyClicked;
        private ArmyData _selectedArmy;
        private bool _showStats;
        private bool _wantsVisible;
        private object _presentationContext;
        private int _scrollOffset;
        private int _revision;
        private Coroutine _transition;

        public IReadOnlyList<ArmyButtonUI> Buttons => _buttons;
        public void SetMaxVisible(int value) => maxVisible = Mathf.Max(1, value);

        private void Awake()
        {
            if (scrollLeftButton != null) scrollLeftButton.onClick.AddListener(() => Scroll(-1));
            if (scrollRightButton != null) scrollRightButton.onClick.AddListener(() => Scroll(1));
        }

        public void Show(IReadOnlyList<ArmyData> armies, Action<ArmyData> onArmyClicked,
            ArmyData selectedArmy = null, bool showStats = false, object presentationContext = null)
        {
            var next = armies == null ? new List<ArmyData>() : armies.Where(a => a != null)
                .OrderBy(a => a.IsAirfield ? 0 : a.IsGarrison ? 1 : 2).ToList();
            bool samePresentation = _wantsVisible && _showStats == showStats
                && Equals(_presentationContext, presentationContext) && _armies.SequenceEqual(next);
            _armies = next;
            _onArmyClicked = onArmyClicked;
            _selectedArmy = selectedArmy;
            _showStats = showStats;
            _presentationContext = presentationContext;
            _wantsVisible = next.Count > 0;
            if (samePresentation)
            {
                SetPanelVisible(_wantsVisible);
                RefreshButtons();
                return;
            }
            _scrollOffset = 0;
            _revision++;
            // panelRoot can contain this component and be inactive after the previous hide.
            SetPanelVisible(_wantsVisible);
            if (!_showStats || !isActiveAndEnabled)
            {
                StopTransition();
                Render(false);
                SetPanelVisible(_wantsVisible);
                return;
            }
            StartTransition();
        }

        public void Hide()
        {
            if (!_wantsVisible && _transition != null) return;
            if (!_wantsVisible && _buttons.Count == 0)
            {
                SetPanelVisible(false);
                return;
            }
            _wantsVisible = false;
            _revision++;
            if (_showStats && isActiveAndEnabled && _buttons.Count > 0) StartTransition();
            else
            {
                StopTransition();
                ClearButtons();
                SetPanelVisible(false);
            }
        }

        private void StartTransition()
        {
            BlockButtons(true);
            if (_transition == null) _transition = StartCoroutine(Transition());
        }

        private IEnumerator Transition()
        {
            // Yield before completing, including an empty request, so the handle is valid.
            yield return null;
            while (true)
            {
                if (_buttons.Count > 0) yield return Slide(false, _revision);
                ClearButtons();
                if (!_wantsVisible) break;
                int revision = _revision;
                Render(true);
                yield return Slide(true, revision);
                if (revision == _revision)
                {
                    _transition = null;
                    RefreshButtons();
                    BlockButtons(false);
                    yield break;
                }
                // New request mid-entry: exit actual positions, then render only latest data.
            }
            _transition = null;
            SetPanelVisible(false);
        }

        private IEnumerator Slide(bool entering, int revision)
        {
            Canvas.ForceUpdateCanvases();
            int count = _buttons.Count;
            var starts = new Vector2[count];
            var ends = new Vector2[count];
            for (int i = 0; i < count; i++)
            {
                RectTransform rect = _buttons[i].RectTransform;
                Vector2 hidden = HiddenPosition(rect);
                starts[i] = entering ? hidden : rect.anchoredPosition;
                ends[i] = entering ? Vector2.zero : hidden;
                rect.anchoredPosition = starts[i];
            }
            float duration = Mathf.Max(0.01f, slideDuration);
            float delay = entering ? Mathf.Max(0f, entryDelay) : 0f;
            float total = duration + Mathf.Max(0, count - 1) * delay;
            float elapsed = 0f;
            while (elapsed < total)
            {
                if (entering && revision != _revision) yield break;
                elapsed += Time.unscaledDeltaTime;
                for (int i = 0; i < count; i++)
                {
                    float t = Mathf.Clamp01((elapsed - i * delay) / duration);
                    float eased = entering ? 1f - Mathf.Pow(1f - t, 3f) : t * t * (3f - 2f * t);
                    if (_buttons[i] != null)
                        _buttons[i].RectTransform.anchoredPosition = Vector2.LerpUnclamped(starts[i], ends[i], eased);
                }
                yield return null;
            }
            for (int i = 0; i < count; i++)
                if (_buttons[i] != null) _buttons[i].RectTransform.anchoredPosition = ends[i];
        }

        private static Vector2 HiddenPosition(RectTransform rect)
        {
            var parent = rect.parent as RectTransform;
            if (parent == null) return new Vector2(-rect.rect.width - 20f, rect.anchoredPosition.y);
            Canvas canvas = rect.GetComponentInParent<Canvas>();
            Camera camera = canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay
                ? canvas.worldCamera : null;
            var corners = new Vector3[4];
            rect.GetWorldCorners(corners);
            RectTransformUtility.ScreenPointToLocalPointInRectangle(parent,
                new Vector2(-20f, Screen.height * 0.5f), camera, out Vector2 left);
            float right = Mathf.Max(parent.InverseTransformPoint(corners[2]).x,
                parent.InverseTransformPoint(corners[3]).x);
            return new Vector2(rect.anchoredPosition.x + left.x - right, rect.anchoredPosition.y);
        }

        private void Scroll(int direction)
        {
            int offset = Mathf.Clamp(_scrollOffset + direction, 0, Mathf.Max(0, _armies.Count - maxVisible));
            if (offset == _scrollOffset) return;
            _scrollOffset = offset;
            _revision++;
            if (_showStats) StartTransition();
            else Render(false);
        }

        private void Render(bool animated)
        {
            ClearButtons();
            ArmyButtonUI prefab = gameConfig == null ? null
                : !_showStats && gameConfig.armyModalButtonPrefab != null
                    ? gameConfig.armyModalButtonPrefab : gameConfig.armyButtonPrefab;
            if (buttonContainer != null && prefab != null)
            {
                int end = Mathf.Min(_armies.Count, _scrollOffset + maxVisible);
                for (int i = _scrollOffset; i < end; i++)
                {
                    Transform parent = buttonContainer;
                    if (animated)
                    {
                        var slot = new GameObject("ArmyButtonSlot", typeof(RectTransform), typeof(LayoutElement));
                        slot.layer = buttonContainer.gameObject.layer;
                        var rect = (RectTransform)slot.transform;
                        rect.SetParent(buttonContainer, false);
                        RectTransform prefabRect = prefab.RectTransform;
                        rect.anchorMin = prefabRect.anchorMin;
                        rect.anchorMax = prefabRect.anchorMax;
                        rect.pivot = prefabRect.pivot;
                        rect.sizeDelta = prefabRect.sizeDelta;
                        var layout = slot.GetComponent<LayoutElement>();
                        layout.preferredWidth = prefabRect.rect.width;
                        layout.preferredHeight = prefabRect.rect.height;
                        _slots.Add(slot);
                        parent = rect;
                    }
                    ArmyButtonUI button = Instantiate(prefab, parent);
                    if (animated)
                    {
                        RectTransform rect = button.RectTransform;
                        rect.anchorMin = rect.anchorMax = new Vector2(0f, 1f);
                        rect.pivot = new Vector2(0f, 1f);
                        rect.anchoredPosition = Vector2.zero;
                    }
                    button.gameObject.SetActive(true);
                    Game.Audio.SceneUIAudioBinder.BindCreatedRoot(button);
                    button.Setup(_armies[i], _onArmyClicked, _showStats && _armies[i] == _selectedArmy, _showStats);
                    button.SetSelected(_armies[i] == _selectedArmy);
                    _buttons.Add(button);
                }
            }
            BlockButtons(animated);
            ScrollRect scroll = buttonContainer != null
                ? buttonContainer.GetComponentInParent<ScrollRect>() : null;
            if (scroll != null && scroll.content == buttonContainer)
            {
                scroll.StopMovement();
                scroll.content.anchoredPosition = Vector2.zero;
            }
            if (animated)
            {
                // Hide before yielding to the animator: no one-frame flash at the resting position.
                Canvas.ForceUpdateCanvases();
                foreach (ArmyButtonUI button in _buttons)
                    button.RectTransform.anchoredPosition = HiddenPosition(button.RectTransform);
            }
            bool needsPaging = _armies.Count > maxVisible;
            if (scrollLeftButton != null)
            {
                scrollLeftButton.gameObject.SetActive(needsPaging);
                scrollLeftButton.interactable = _scrollOffset > 0;
            }
            if (scrollRightButton != null)
            {
                scrollRightButton.gameObject.SetActive(needsPaging);
                scrollRightButton.interactable = _scrollOffset + maxVisible < _armies.Count;
            }
        }

        private void RefreshButtons()
        {
            foreach (ArmyButtonUI button in _buttons)
            {
                if (button == null) continue;
                ArmyData army = button.Army;
                bool selected = army == _selectedArmy;
                button.Setup(army, _onArmyClicked, _showStats && selected && !army.IsGarrison, _showStats);
                button.SetSelected(selected);
            }
        }

        private void BlockButtons(bool blocked)
        {
            foreach (ArmyButtonUI button in _buttons)
            {
                if (button == null) continue;
                CanvasGroup group = button.GetComponent<CanvasGroup>();
                if (group == null && !blocked) continue;
                if (group == null) group = button.gameObject.AddComponent<CanvasGroup>();
                group.interactable = !blocked;
                group.blocksRaycasts = !blocked;
            }
        }

        private void SetPanelVisible(bool visible)
        {
            if (panelRoot != null && panelRoot.activeSelf != visible) panelRoot.SetActive(visible);
        }

        private void StopTransition()
        {
            if (_transition != null) StopCoroutine(_transition);
            _transition = null;
        }

        private void OnDisable()
        {
            StopTransition();
            ClearButtons();
            _wantsVisible = false;
        }

        private void ClearButtons()
        {
            // Deferred Destroy must not leave old entries participating in this frame's layout.
            foreach (ArmyButtonUI button in _buttons)
                if (button != null) button.gameObject.SetActive(false);
            UIListUtility.DestroyAndClear(_buttons);
            foreach (GameObject slot in _slots)
                if (slot != null)
                {
                    slot.SetActive(false);
                    Destroy(slot);
                }
            _slots.Clear();
        }
    }
}


