using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using Game.Cards;
using UnityEngine;
using UnityEngine.UI;

namespace Game.UI
{
    // One "build extraction Facility" entry on HexInfoPanelUI: a visible label, a click callback,
    // and the CardDefinition the button reads AP/resource badges off for its hover preview.
    public sealed class HexActionDescriptor
    {
        public string Label;
        public Action OnClick;
        // Non-null only for cost actions (the extraction-Facility buttons) — drives the
        // hover cost preview in ResourceActionButtonUI. Null => simple label-only action.
        public CardDefinition CostSource;

        public HexActionDescriptor(string label, Action onClick, CardDefinition costSource = null)
        {
            Label = label;
            OnClick = onClick;
            CostSource = costSource;
        }
    }

    // Context buttons of the selected hex. Only the actions available on the current hex are
    // shown: they slide in from the right screen edge one after another (mirror of
    // ArmyButtonRowUI, which enters from the left) and slide back out when the set changes.
    // The panel's layout group positions per-button slots; the animation moves only the
    // buttons inside them. The four fixed buttons are wrapped once; extractor buttons are
    // instantiated per selection from the prefab handed to SetResourceActions.
    public class HexInfoPanelUI : MonoBehaviour
    {
        private const int Garrison = 0, Base = 1, Research = 2, Production = 3, FixedCount = 4;

        [SerializeField] private GameObject panelRoot;
        [SerializeField] private Button garrisonButton;
        [SerializeField] private Button baseButton;
        [SerializeField] private Button researchButton;
        [SerializeField] private Button productionButton;
        [SerializeField, Min(0.01f)] private float slideDuration = 0.2f;
        [SerializeField, Min(0f)] private float entryDelay = 0.05f;

        private sealed class Item
        {
            public RectTransform Slot;
            public RectTransform Content;
            public CanvasGroup Group;
            public int FixedIndex = -1;
            public int ResourceIndex = -1;
            public ResourceActionButtonUI Resource;
        }

        private Item[] _fixed;
        private readonly bool[] _wanted = new bool[FixedCount];
        private readonly Action[] _clicks = new Action[FixedCount];
        private List<HexActionDescriptor> _wantedActions = new List<HexActionDescriptor>();
        private ResourceActionButtonUI _resourcePrefab;

        private readonly List<Item> _shown = new List<Item>();
        private readonly List<Item> _resourceItems = new List<Item>();
        private string _targetSignature;
        private bool _wantsVisible;
        private bool _showRequested;
        private int _revision;
        private Coroutine _transition;

        private void Awake()
        {
            // ShowHex can activate an initially inactive root, invoking Awake synchronously.
            // In that case the selection request wins over the initial hidden state.
            if (Application.isPlaying && !_showRequested) Hide();
        }

        private void OnDisable()
        {
            StopTransition();
            ClearItems();
            _wantsVisible = false;
            _targetSignature = null;
        }

        public void SetGarrisonButtonVisible(bool visible, Action onClick) => SetWanted(Garrison, visible, onClick);
        public void SetBaseButtonVisible(bool visible, Action onClick) => SetWanted(Base, visible, onClick);
        public void SetResearchButtonVisible(bool visible, Action onClick) => SetWanted(Research, visible, onClick);
        public void SetProductionButtonVisible(bool visible, Action onClick) => SetWanted(Production, visible, onClick);

        // Selection only records what is available; ShowHex/HideAnimated apply it.
        private void SetWanted(int index, bool available, Action onClick)
        {
            _wanted[index] = available;
            _clicks[index] = available ? onClick : null;
        }

        public void SetResourceActions(IReadOnlyList<HexActionDescriptor> actions, ResourceActionButtonUI prefab)
        {
            _wantedActions = actions == null || prefab == null
                ? new List<HexActionDescriptor>() : new List<HexActionDescriptor>(actions);
            _wantedActions.RemoveAll(a => a == null);
            _resourcePrefab = prefab;
        }

        public void ShowHex()
        {
            if (!HasWantedActions())
            {
                HideAnimated();
                return;
            }
            _showRequested = true;
            if (panelRoot != null) panelRoot.SetActive(true);
            EnsureSlots();

            string signature = Signature();
            if (_wantsVisible && signature == _targetSignature)
            {
                // Same set of buttons (another hex, same actions): only the callbacks change.
                Rebind();
                return;
            }
            _wantsVisible = true;
            _targetSignature = signature;
            _revision++;
            if (!isActiveAndEnabled)
            {
                StopTransition();
                ClearItems();
                Apply();
                foreach (Item item in _shown) item.Content.anchoredPosition = Vector2.zero;
                return;
            }
            StartTransition();
        }

        // Used for an empty hex selection. Deselect/battle/startup keep immediate Hide.
        public void HideAnimated()
        {
            if (panelRoot == null || !panelRoot.activeSelf || !isActiveAndEnabled || _shown.Count == 0)
            {
                Hide();
                return;
            }
            _showRequested = false;
            _wantsVisible = false;
            _targetSignature = null;
            _revision++;
            StartTransition();
        }

        public void Hide()
        {
            _showRequested = false;
            _wantsVisible = false;
            _targetSignature = null;
            _revision++;
            StopTransition();
            ClearItems();
            Array.Clear(_wanted, 0, FixedCount);
            Array.Clear(_clicks, 0, FixedCount);
            _wantedActions.Clear();
            if (panelRoot != null) panelRoot.SetActive(false);
        }

        private bool HasWantedActions()
        {
            for (int i = 0; i < FixedCount; i++)
                if (_wanted[i] && FixedButton(i) != null) return true;
            return _wantedActions.Count > 0 && _resourcePrefab != null;
        }

        private string Signature()
        {
            var builder = new StringBuilder();
            for (int i = 0; i < FixedCount; i++) builder.Append(_wanted[i] ? '1' : '0');
            foreach (HexActionDescriptor action in _wantedActions) builder.Append('|').Append(action.Label);
            return builder.ToString();
        }

        private Button FixedButton(int index)
        {
            switch (index)
            {
                case Garrison: return garrisonButton;
                case Base: return baseButton;
                case Research: return researchButton;
                default: return productionButton;
            }
        }

        // Each fixed button gets a permanent layout slot at its own sibling position.
        private void EnsureSlots()
        {
            if (_fixed != null) return;
            _fixed = new Item[FixedCount];
            for (int i = 0; i < FixedCount; i++)
            {
                Button button = FixedButton(i);
                if (button == null) continue;
                var content = (RectTransform)button.transform;
                Item item = CreateItem(button.name, content.parent, content.rect.size, content.GetSiblingIndex());
                item.FixedIndex = i;
                content.SetParent(item.Slot, false);
                PlaceContent(content, item.Slot.sizeDelta);
                item.Content = content;
                button.gameObject.SetActive(true);
                item.Slot.gameObject.SetActive(false);
                _fixed[i] = item;
            }
        }

        private Item CreateItem(string name, Transform parent, Vector2 size, int siblingIndex)
        {
            var slot = new GameObject(name + "Slot", typeof(RectTransform), typeof(LayoutElement), typeof(CanvasGroup));
            slot.layer = gameObject.layer;
            var rect = (RectTransform)slot.transform;
            rect.SetParent(parent, false);
            if (siblingIndex >= 0) rect.SetSiblingIndex(siblingIndex);
            rect.anchorMin = rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.sizeDelta = size;
            var layout = slot.GetComponent<LayoutElement>();
            layout.preferredWidth = size.x;
            layout.preferredHeight = size.y;
            return new Item { Slot = rect, Group = slot.GetComponent<CanvasGroup>() };
        }

        private static void PlaceContent(RectTransform content, Vector2 size)
        {
            content.anchorMin = content.anchorMax = new Vector2(0f, 1f);
            content.pivot = new Vector2(0f, 1f);
            content.sizeDelta = size;
            content.anchoredPosition = Vector2.zero;
        }

        // Builds the shown set from the wanted state, parked beyond the right screen edge.
        private void Apply()
        {
            EnsureSlots();
            for (int i = 0; i < FixedCount; i++)
            {
                Item item = _fixed[i];
                if (!_wanted[i] || item == null) continue;
                item.Slot.gameObject.SetActive(true);
                BindFixed(item);
                _shown.Add(item);
            }
            if (_resourcePrefab != null)
            {
                RectTransform prefabRect = _resourcePrefab.GetComponent<RectTransform>();
                for (int i = 0; i < _wantedActions.Count; i++)
                {
                    Item item = CreateItem("ResourceAction", transform, prefabRect.rect.size, -1);
                    ResourceActionButtonUI button = Instantiate(_resourcePrefab, item.Slot);
                    button.gameObject.SetActive(true);
                    Game.Audio.SceneUIAudioBinder.BindCreatedRoot(button);
                    item.Content = (RectTransform)button.transform;
                    PlaceContent(item.Content, item.Slot.sizeDelta);
                    item.Resource = button;
                    item.ResourceIndex = i;
                    button.Setup(_wantedActions[i]);
                    _resourceItems.Add(item);
                    _shown.Add(item);
                }
            }
            _shown.Sort((a, b) => a.Slot.GetSiblingIndex().CompareTo(b.Slot.GetSiblingIndex()));
            Block(true);
            Canvas.ForceUpdateCanvases();
            foreach (Item item in _shown) item.Content.anchoredPosition = HiddenPosition(item);
        }

        private void BindFixed(Item item)
        {
            Button button = FixedButton(item.FixedIndex);
            UIButtonEventUtility.ResetRuntimeListeners(button);
            button.interactable = true;
            Action onClick = _clicks[item.FixedIndex];
            button.onClick.AddListener(() => onClick?.Invoke());
        }

        private void Rebind()
        {
            foreach (Item item in _shown)
            {
                if (item.FixedIndex >= 0) BindFixed(item);
                else if (item.ResourceIndex < _wantedActions.Count)
                    item.Resource.Setup(_wantedActions[item.ResourceIndex]);
            }
        }

        private void ClearItems()
        {
            // Deferred Destroy must not leave old entries in this frame's layout or raycasts.
            foreach (Item item in _shown)
                if (item.Slot != null) item.Slot.gameObject.SetActive(false);
            foreach (Item item in _resourceItems)
                if (item.Slot != null)
                {
                    item.Slot.gameObject.SetActive(false);
                    Destroy(item.Slot.gameObject);
                }
            _resourceItems.Clear();
            _shown.Clear();
        }

        private void Block(bool blocked)
        {
            foreach (Item item in _shown)
            {
                if (item.Group == null) continue;
                item.Group.interactable = !blocked;
                item.Group.blocksRaycasts = !blocked;
            }
        }

        private void StartTransition()
        {
            Block(true);
            if (_transition == null) _transition = StartCoroutine(Transition());
        }

        private void StopTransition()
        {
            if (_transition != null) StopCoroutine(_transition);
            _transition = null;
        }

        private IEnumerator Transition()
        {
            yield return null;
            while (true)
            {
                if (_shown.Count > 0) yield return Slide(false, _revision);
                ClearItems();
                if (!_wantsVisible) break;
                int revision = _revision;
                Apply();
                yield return Slide(true, revision);
                if (revision == _revision)
                {
                    _transition = null;
                    Block(false);
                    yield break;
                }
                // New request mid-entry: exit the actual positions, then apply only the latest state.
            }
            _transition = null;
            if (panelRoot != null) panelRoot.SetActive(false);
        }

        private IEnumerator Slide(bool entering, int revision)
        {
            Canvas.ForceUpdateCanvases();
            int count = _shown.Count;
            var starts = new Vector2[count];
            var ends = new Vector2[count];
            for (int i = 0; i < count; i++)
            {
                RectTransform content = _shown[i].Content;
                Vector2 hidden = HiddenPosition(_shown[i]);
                starts[i] = entering ? hidden : content.anchoredPosition;
                ends[i] = entering ? Vector2.zero : hidden;
                content.anchoredPosition = starts[i];
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
                    if (_shown[i].Content != null)
                        _shown[i].Content.anchoredPosition = Vector2.LerpUnclamped(starts[i], ends[i], eased);
                }
                yield return null;
            }
            for (int i = 0; i < count; i++)
                if (_shown[i].Content != null) _shown[i].Content.anchoredPosition = ends[i];
        }

        // Content position (inside its slot) at which the button is just past the right screen edge.
        private static Vector2 HiddenPosition(Item item)
        {
            RectTransform slot = item.Slot;
            Canvas canvas = slot.GetComponentInParent<Canvas>();
            Camera camera = canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay
                ? canvas.worldCamera : null;
            RectTransformUtility.ScreenPointToLocalPointInRectangle(slot,
                new Vector2(Screen.width + 20f, Screen.height * 0.5f), camera, out Vector2 right);
            return new Vector2(right.x - slot.rect.xMin, 0f);
        }
    }
}
