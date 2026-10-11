using System.Collections.Generic;
using Game.Core;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Turns;
using Game.Units;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Game.UI
{
    // One lightweight radio channel for the current viewer's own armies. Events invalidate
    // contact observations; only the active bubble follows the camera each LateUpdate.
    public sealed class ArmyRadioChatterController : MonoBehaviour
    {
        private GameTurnController _turns;
        private Camera _camera;
        private RectTransform _hud, _canvas, _group, _panel, _shadow;
        private CanvasGroup _fade;
        private ArmyRadioBubbleGraphic _bubble, _bubbleShadow;
        private Image _art;
        private TMP_Text _message;
        private Canvas _overlayCanvas;
        private readonly Vector3[] _hudCorners = new Vector3[4];
        private readonly List<ArmyData> _retired = new List<ArmyData>();
        private float _measuredWidth = -1f, _messageHeight;
        private ArmyRadioChatterSettings _settings;
        private PlayerSetupData _viewer, _actor;
        private ArmyData _speaker;
        private UnitData _unit;
        private readonly List<ArmyData> _candidates = new List<ArmyData>();
        private readonly HashSet<ArmyData> _moved = new HashSet<ArmyData>();
        private readonly HashSet<ArmyData> _contacts = new HashSet<ArmyData>();
        private readonly HashSet<ArmyData> _newContacts = new HashSet<ArmyData>();
        private readonly Dictionary<ArmyData, float> _lastSpoke = new Dictionary<ArmyData, float>();
        private readonly int[] _variations = new int[8];
        private int _lastSpeakerId = -1;
        private bool _dirty = true, _subscribed;
        private float _shownAt, _turnBeganAt, _nextAmbient, _nextAllowed;

        public void Configure(GameTurnController turns, Camera camera, RectTransform hud, ArmyRadioChatterSettings settings)
        {
            Unsubscribe(); Clear();
            _turns = turns; _camera = camera; _hud = hud;
            _settings = settings ?? new ArmyRadioChatterSettings();
            _viewer = null; _actor = null;
            _contacts.Clear(); _newContacts.Clear(); _moved.Clear(); _lastSpoke.Clear();
            _lastSpeakerId = -1; _nextAllowed = Time.unscaledTime + 2f;
            _nextAmbient = Time.unscaledTime + Mathf.Max(5f, _settings.ambientInterval);
            System.Array.Clear(_variations, 0, _variations.Length);
            if (_canvas == null) CreateUI();
            _message.fontSize = Mathf.Clamp(_settings.fontSize, 12f, 20f);
            if (isActiveAndEnabled) Subscribe();
        }

        private void OnEnable() { if (_turns != null) Subscribe(); }
        private void OnDisable() { Unsubscribe(); Clear(); }

        private void Subscribe()
        {
            if (_subscribed || _turns == null) return;
            _subscribed = true;
            _turns.TurnChanging += OnTurnChanging;
            _turns.TurnStateChanged += OnTurnStateChanged;
            VisionSystem.VisibilityChanged += OnVision;
            VisionSystem.VisibleContentChanged += OnContent;
            ArmyRegistry.ArmyRelocated += OnRelocated;
            StealthSystem.StealthChanged += Invalidate;
            OnTurnStateChanged();
        }

        private void Unsubscribe()
        {
            if (!_subscribed) return;
            _subscribed = false;
            if (_turns != null)
            {
                _turns.TurnChanging -= OnTurnChanging;
                _turns.TurnStateChanged -= OnTurnStateChanged;
            }
            VisionSystem.VisibilityChanged -= OnVision;
            VisionSystem.VisibleContentChanged -= OnContent;
            ArmyRegistry.ArmyRelocated -= OnRelocated;
            StealthSystem.StealthChanged -= Invalidate;
        }

        private void OnTurnChanging() => Invalidate();
        private void OnTurnStateChanged()
        {
            PlayerSetupData viewer = VisionSystem.CurrentViewer;
            if (viewer != _viewer)
            {
                Clear(); _viewer = viewer;
                _contacts.Clear(); _newContacts.Clear(); _moved.Clear(); _lastSpoke.Clear();
                _lastSpeakerId = -1;
                _nextAmbient = Time.unscaledTime + 5f;
            }
            if (_turns.CurrentPlayer != _actor)
            {
                _actor = _turns.CurrentPlayer;
                if (_actor == _viewer) { _moved.Clear(); _turnBeganAt = Time.unscaledTime; }
            }
            Invalidate();
        }
        private void OnVision(PlayerSetupData player) { if (player == _viewer) Invalidate(); }
        private void OnContent(PlayerSetupData player, HexCoord hex) { if (player == _viewer) Invalidate(); }
        private void OnRelocated(ArmyData army, HexCoord from, HexCoord to)
        {
            if (army.Owner == _viewer) _moved.Add(army);
            Invalidate();
        }
        private void Invalidate() => _dirty = true;

        private void Update()
        {
            if (_turns == null || _settings == null || !_settings.enabled || !_turns.isActiveAndEnabled
                || !_turns.CanPresentArmyRadio || _camera == null || _viewer == null)
            { Clear(); return; }
            if (_viewer != VisionSystem.CurrentViewer) OnTurnStateChanged();
            float now = Time.unscaledTime;
            if (_speaker != null)
            {
                float duration = Mathf.Max(2f, _settings.displaySeconds);
                float elapsed = now - _shownAt;
                if (elapsed >= duration) Clear();
                else _fade.alpha = Mathf.Min(Mathf.Clamp01(elapsed / .22f), Mathf.Clamp01((duration - elapsed) / .35f));
            }
            if (now < _nextAllowed || _speaker != null || (!_dirty && now < _nextAmbient)) return;
            Sample(); _dirty = false;
            bool ambient = now >= _nextAmbient;
            foreach (ArmyData army in _candidates)
            {
                bool contact = _newContacts.Contains(army);
                if (!contact && !ambient) continue;
                if (_lastSpoke.TryGetValue(army, out float previous)
                    && now - previous < Mathf.Max(10f, _settings.armyCooldown)) continue;
                if (!CanPresent(army, out _, out _)) continue;
                bool waiting = _actor == _viewer && now - _turnBeganAt >= Mathf.Max(1f, _settings.waitingSeconds);
                var situation = ArmyRadioChatter.Situation(army, contact, _moved.Contains(army), waiting ? 1f : 0f);
                Show(army, situation);
                _newContacts.Remove(army); _lastSpoke[army] = now; _lastSpeakerId = army.Id;
                _nextAllowed = now + Mathf.Max(Mathf.Max(2f, _settings.displaySeconds) + 1f, _settings.minimumGap);
                _nextAmbient = now + Mathf.Max(5f, _settings.ambientInterval);
                // Other fresh contacts remain candidates, revalidated after the quiet gap.
                _dirty = _newContacts.Count > 0;
                return;
            }
            if (ambient) _nextAmbient = now + Mathf.Max(5f, _settings.ambientInterval);
        }

        private void Sample()
        {
            _candidates.Clear();
            foreach (ArmyData army in ArmyRegistry.AllForOwner(_viewer))
            {
                if (!ArmyRadioChatter.CanSpeak(army, _viewer)) continue;
                _candidates.Add(army);
                if (army.Controller != null && army.Controller.IsMoving) _moved.Add(army);
                bool contact = ArmyRadioChatter.SeesEnemy(army);
                if (contact) { if (_contacts.Add(army)) _newContacts.Add(army); }
                else { _contacts.Remove(army); _newContacts.Remove(army); }
            }
            _contacts.RemoveWhere(a => !ArmyRegistry.IsRegistered(a));
            _newContacts.RemoveWhere(a => !ArmyRegistry.IsRegistered(a));
            _moved.RemoveWhere(a => !ArmyRegistry.IsRegistered(a));
            _retired.Clear();
            foreach (var pair in _lastSpoke)
                if (!ArmyRegistry.IsRegistered(pair.Key)) _retired.Add(pair.Key);
            foreach (ArmyData army in _retired) _lastSpoke.Remove(army);
            // Round-robin avoids the first army monopolizing sparse ambient chatter.
            _candidates.Sort((a, b) =>
            {
                int contact = _newContacts.Contains(b).CompareTo(_newContacts.Contains(a));
                if (contact != 0) return contact;
                int wrap = (a.Id <= _lastSpeakerId).CompareTo(b.Id <= _lastSpeakerId);
                return wrap != 0 ? wrap : a.Id.CompareTo(b.Id);
            });
        }

        private bool CanPresent(ArmyData army, out Vector2 anchor, out float markerRadius)
        {
            anchor = Vector2.zero; markerRadius = 0f;
            if (!ArmyRegistry.IsRegistered(army) || !ArmyRadioChatter.CanSpeak(army, _viewer)
                || army.Controller == null || !army.Controller.isActiveAndEnabled || army.Controller.IsMoving
                || army.Controller.Visual == null || ArmyRadioChatter.DetailArt(ArmyRadioChatter.FirstUnit(army)) == null
                || !army.Controller.Visual.TryGetScreenAnchor(_camera, out var screen, out float pixels)) return false;
            if (!ScreenToLocal(screen, out anchor) || !SafeRect().Contains(anchor)) return false;
            markerRadius = pixels / Mathf.Max(.01f, _overlayCanvas.scaleFactor);
            return true;
        }

        private void Show(ArmyData army, ArmyRadioSituation situation)
        {
            _speaker = army; _unit = ArmyRadioChatter.FirstUnit(army); _shownAt = Time.unscaledTime;
            _art.sprite = ArmyRadioChatter.DetailArt(_unit); _art.color = Color.white;
            _message.text = ArmyRadioChatter.Phrase(situation, _settings.language, _variations[(int)situation]++);
            _measuredWidth = -1f;
            _fade.alpha = 0f; _group.gameObject.SetActive(true);
            Follow();
        }

        private void LateUpdate() { if (_speaker != null) Follow(); }
        private void Follow()
        {
            if (_turns == null || !_turns.CanPresentArmyRadio || !_settings.enabled
                || !CanPresent(_speaker, out Vector2 anchor, out float radius) || _viewer != VisionSystem.CurrentViewer
                || _unit != ArmyRadioChatter.FirstUnit(_speaker)) { Clear(); return; }
            Vector2 artSize = ArmyRadioLayout.ArtSize(_art.sprite, Mathf.Clamp(_settings.artHeight, 40f, 110f));
            Rect safe = SafeRect();
            float width = Mathf.Min(Mathf.Clamp(_settings.messageWidth, 140f, 300f), safe.width - artSize.x - 8f);
            if (width < 100f || safe.height < artSize.y) { Clear(); return; }
            if (Mathf.Abs(width - _measuredWidth) > .01f)
            {
                _measuredWidth = width;
                _messageHeight = Mathf.Max(52f, _message.GetPreferredValues(_message.text, width - 22f, 200f).y + 20f);
            }
            float height = _messageHeight;
            Vector2 size = new Vector2(width + artSize.x + 4f, Mathf.Max(artSize.y, height));
            if (size.y > safe.height) { Clear(); return; }
            _group.sizeDelta = size;
            _group.anchoredPosition = ArmyRadioLayout.Place(safe, anchor, size, radius + 12f, out bool left);
            _art.rectTransform.sizeDelta = artSize;
            _art.rectTransform.anchoredPosition = new Vector2((left ? -1f : 1f) * (size.x - artSize.x) * .5f, 0f);
            Vector2 panelPosition = new Vector2((left ? 1f : -1f) * (artSize.x + 4f) * .5f, 0f);
            _panel.sizeDelta = _shadow.sizeDelta = new Vector2(width, height);
            _panel.anchoredPosition = panelPosition;
            _shadow.anchoredPosition = panelPosition + new Vector2(0f, -2f);
            _bubble.AimAt(anchor - _group.anchoredPosition - panelPosition, radius + 2f);
            _bubbleShadow.AimAt(anchor - _group.anchoredPosition - _shadow.anchoredPosition, radius + 2f);
        }

        private bool ScreenToLocal(Vector2 screen, out Vector2 local) =>
            RectTransformUtility.ScreenPointToLocalPointInRectangle(_canvas, screen, null, out local);

        private Rect SafeRect()
        {
            Rect screen = Screen.safeArea, cameraRect = _camera.pixelRect;
            screen = Rect.MinMaxRect(Mathf.Max(screen.xMin, cameraRect.xMin), Mathf.Max(screen.yMin, cameraRect.yMin),
                Mathf.Min(screen.xMax, cameraRect.xMax), Mathf.Min(screen.yMax, cameraRect.yMax));
            ScreenToLocal(screen.min, out var min); ScreenToLocal(screen.max, out var max);
            Rect safe = Rect.MinMaxRect(min.x, min.y, max.x, max.y);
            if (_hud != null && _hud.gameObject.activeInHierarchy)
            {
                _hud.GetWorldCorners(_hudCorners);
                Canvas hudCanvas = _hud.GetComponentInParent<Canvas>();
                Camera hudCamera = hudCanvas != null && hudCanvas.renderMode != RenderMode.ScreenSpaceOverlay ? hudCanvas.worldCamera : null;
                float top = safe.yMin;
                foreach (Vector3 corner in _hudCorners)
                {
                    ScreenToLocal(RectTransformUtility.WorldToScreenPoint(hudCamera, corner), out var point);
                    top = Mathf.Max(top, point.y);
                }
                // Only reserve a bottom-anchored HUD, never an arbitrary full-screen parent.
                if (top < safe.center.y) safe.yMin = top + 8f;
            }
            return ArmyRadioLayout.Inset(safe, 12f);
        }

        private void Clear()
        {
            _speaker = null; _unit = null;
            if (_group != null) _group.gameObject.SetActive(false);
        }

        private void CreateUI()
        {
            var root = new GameObject("ArmyRadioOverlay", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler));
            root.transform.SetParent(transform, false); _canvas = (RectTransform)root.transform;
            Canvas canvas = _overlayCanvas = root.GetComponent<Canvas>(); canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.overrideSorting = true; canvas.sortingOrder = 5;
            CanvasScaler scaler = root.GetComponent<CanvasScaler>(); scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1280f, 720f); scaler.matchWidthOrHeight = .5f;
            _group = CreateRect(_canvas, "RadioMessage");
            _fade = _group.gameObject.AddComponent<CanvasGroup>(); _fade.blocksRaycasts = false; _fade.interactable = false;
            _shadow = CreateRect(_group, "Shadow"); _bubbleShadow = _shadow.gameObject.AddComponent<ArmyRadioBubbleGraphic>();
            _bubbleShadow.color = new Color(.13f, .10f, .07f, .16f); _bubbleShadow.BorderColor = Color.clear;
            _bubbleShadow.raycastTarget = false;
            _panel = CreateRect(_group, "Message"); _bubble = _panel.gameObject.AddComponent<ArmyRadioBubbleGraphic>();
            _bubble.color = new Color(.80f, .72f, .60f, .86f); _bubble.raycastTarget = false;
            RectTransform label = CreateRect(_panel, "Speech");
            label.anchorMin = Vector2.zero; label.anchorMax = Vector2.one; label.offsetMin = new Vector2(11f, 10f); label.offsetMax = new Vector2(-11f, -10f);
            _message = label.gameObject.AddComponent<TextMeshProUGUI>();
            _message.font = Resources.Load<TMP_FontAsset>("Fonts/GameMenuFont") ?? TMP_Settings.defaultFontAsset;
            _message.color = CollectionUIElements.CollectionInk; _message.alignment = TextAlignmentOptions.MidlineLeft;
            _message.richText = false; _message.textWrappingMode = TextWrappingModes.Normal;
            _message.overflowMode = TextOverflowModes.Overflow; _message.raycastTarget = false;
            _art = CreateRect(_group, "FirstUnitDetailArt").gameObject.AddComponent<Image>();
            _art.preserveAspect = true; _art.raycastTarget = false;
            Clear();
        }

        private static RectTransform CreateRect(Transform parent, string name)
        {
            var rect = (RectTransform)new GameObject(name, typeof(RectTransform)).transform;
            rect.SetParent(parent, false); rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(.5f, .5f);
            return rect;
        }
    }
}
