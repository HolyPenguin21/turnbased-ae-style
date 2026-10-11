using System.Collections.Generic;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Turns;
using Game.Units;
using UnityEngine;

namespace Game.UI
{
    // Owns the channel, bounded view pool and a single two-army exchange. Cosmetic only.
    public sealed class ArmyRadioChatterController : MonoBehaviour
    {
        private sealed class Observation
        {
            public float StationarySince;
            public HexCoord Hex;
        }
        private sealed class Exchange
        {
            public ArmyData First, Second;
            public UnitData FirstUnit, SecondUnit;
            public HexCoord FirstHex, SecondHex;
            public int Variation;
            public float ReplyAt, ExpiresAt;
        }
        private GameTurnController _turns;
        private HexSelectionController _selection;
        private Camera _camera;
        private RectTransform _hud;
        [SerializeField] private RectTransform _canvas;
        [SerializeField] private Canvas _overlayCanvas;
        private readonly Vector3[] _hudCorners = new Vector3[4];
        [SerializeField] private ArmyRadioBubbleView[] _views = System.Array.Empty<ArmyRadioBubbleView>();
        private readonly List<Rect> _occupied = new List<Rect>(96);
        private readonly List<ArmyData> _candidates = new List<ArmyData>();
        private readonly List<ArmyData> _retired = new List<ArmyData>();
        private readonly Dictionary<ArmyData, Observation> _observations = new Dictionary<ArmyData, Observation>();
        private readonly HashSet<ArmyData> _moved = new HashSet<ArmyData>();
        private readonly HashSet<ArmyData> _contacts = new HashSet<ArmyData>();
        private readonly HashSet<ArmyData> _newContacts = new HashSet<ArmyData>();
        private readonly int[] _variations = new int[8];
        private readonly System.Random _random = new System.Random();
        private ArmyRadioCadence _cadence;
        private ArmyRadioChatterSettings _settings;
        private PlayerSetupData _viewer, _actor;
        private Exchange _exchange;
        private int _lastSpeakerId = -1, _lastJoke = -1, _lastDialogue = -1;
        private bool _dirty = true, _subscribed;
        private float _nextAmbient, _nextObservation;

        public void Configure(GameTurnController turns, Camera camera, RectTransform hud,
            ArmyRadioChatterSettings settings, HexSelectionController selection = null)
        {
            Unsubscribe(); Clear();
            _turns = turns; _camera = camera; _hud = hud; _selection = selection;
            _settings = null;
            if (!HasConfiguredUI())
            {
                Debug.LogError("Army radio needs its authored overlay Canvas and 1–3 message views. Assign the ArmyRadioOverlay prefab in the scene.", this);
                return;
            }
            _settings = settings ?? new ArmyRadioChatterSettings();
            _viewer = null; _actor = null; ResetChannel();
            for (int i = 0; i < _variations.Length; i++)
                _variations[i] = _random.Next(ArmyRadioChatter.PhraseCount((ArmyRadioSituation)i));
            if (isActiveAndEnabled) Subscribe();
        }
        private void OnEnable() { if (_turns != null) Subscribe(); }
        private void OnDisable() { Unsubscribe(); Clear(); }
        private void Subscribe()
        {
            if (_subscribed || _turns == null || _settings == null) return;
            _subscribed = true;
            _turns.TurnChanging += Invalidate; _turns.TurnStateChanged += OnTurnStateChanged;
            VisionSystem.VisibilityChanged += OnVision; VisionSystem.VisibleContentChanged += OnContent;
            ArmyRegistry.ArmyRelocated += OnRelocated; StealthSystem.StealthChanged += Invalidate;
            if (_selection != null) _selection.ArmyInteracted += OnArmyInteracted;
            OnTurnStateChanged();
        }
        private void Unsubscribe()
        {
            if (!_subscribed) return;
            _subscribed = false;
            if (_turns != null) { _turns.TurnChanging -= Invalidate; _turns.TurnStateChanged -= OnTurnStateChanged; }
            VisionSystem.VisibilityChanged -= OnVision; VisionSystem.VisibleContentChanged -= OnContent;
            ArmyRegistry.ArmyRelocated -= OnRelocated; StealthSystem.StealthChanged -= Invalidate;
            if (_selection != null) _selection.ArmyInteracted -= OnArmyInteracted;
        }
        private void ResetChannel()
        {
            _contacts.Clear(); _newContacts.Clear(); _moved.Clear(); _observations.Clear();
            _cadence = new ArmyRadioCadence(Time.unscaledTime); _lastSpeakerId = -1;
            _nextAmbient = Time.unscaledTime + Mathf.Max(5f, _settings.ambientInterval);
            _nextObservation = 0f; _dirty = true;
        }
        private void OnTurnStateChanged()
        {
            var viewer = VisionSystem.CurrentViewer;
            if (viewer != _viewer) { Clear(); _viewer = viewer; ResetChannel(); }
            if (_turns.CurrentPlayer != _actor)
            {
                _actor = _turns.CurrentPlayer;
                if (_actor == _viewer) _moved.Clear();
                _exchange = null;
            }
            Invalidate();
        }
        private void OnVision(PlayerSetupData player) { if (player == _viewer) Invalidate(); }
        private void OnContent(PlayerSetupData player, HexCoord hex) { if (player == _viewer) Invalidate(); }
        private void OnRelocated(ArmyData army, HexCoord from, HexCoord to)
        {
            if (army.Owner == _viewer)
            {
                _moved.Add(army);
                if (_observations.TryGetValue(army, out var state))
                { state.Hex = to; state.StationarySince = Time.unscaledTime; }
                CancelExchangeFor(army);
            }
            Invalidate();
        }
        private void Invalidate() => _dirty = true;
        private void CancelExchangeFor(ArmyData army)
        { if (_exchange != null && (army == _exchange.First || army == _exchange.Second)) _exchange = null; }
        private void OnArmyInteracted(ArmyData army)
        {
            float now = Time.unscaledTime;
            foreach (var view in _views) if (view.Speaker == army) view.Dismiss(now);
            CancelExchangeFor(army);
            if (army != null && army.Owner == _viewer) _cadence.Reserve(army.Id, now);
        }

        private void Update()
        {
            if (_settings == null) return;
            float now = Time.unscaledTime;
            foreach (var view in _views) view.Tick(now);
            if (_turns == null || !_settings.enabled || !_turns.isActiveAndEnabled || _camera == null)
            { Clear(); return; }
            if (_viewer != VisionSystem.CurrentViewer) OnTurnStateChanged();
            if (_viewer == null) { Clear(); return; }
            if (!_turns.CanPresentArmyRadio)
            {
                _exchange = null;
                foreach (var view in _views) view.Dismiss(now);
                return;
            }
            if (_dirty || now >= _nextObservation)
            { Sample(now); _dirty = false; _nextObservation = now + 1f; }
            Follow();
            PresentReply(now);
            if (ActiveCount() >= Mathf.Clamp(_settings.maximumMessages, 1, _views.Length)) return;
            bool ambient = now >= _nextAmbient;
            foreach (var army in _candidates)
            {
                bool contact = _newContacts.Contains(army);
                if ((!contact && !ambient) || Busy(army) || IsReserved(army)
                    || !_cadence.CanSpeak(army.Id, now, _settings.armyCooldown) || !CanPresent(army, out _, out _)) continue;
                float stationary = now - _observations[army].StationarySince;
                bool waiting = _actor == _viewer && stationary >= Mathf.Max(1f, _settings.waitingSeconds);
                var situation = ArmyRadioChatter.Situation(army, _contacts.Contains(army), _moved.Contains(army), waiting ? 1f : 0f);
                string text;
                ArmyData partner = null; int dialogue = -1;
                bool quiet = !contact && !_contacts.Contains(army) && CanIdleFlavor(army)
                    && stationary >= Mathf.Max(1f, _settings.waitingSeconds);
                if (quiet && _exchange == null && ActiveCount() == 0 && Mathf.Min(_settings.maximumMessages, _views.Length) > 1
                    && _random.NextDouble() < Mathf.Clamp01(_settings.dialogueChance)) partner = FindPartner(army, now);
                if (partner != null)
                { dialogue = NextDifferent(ArmyRadioChatter.DialogueCount, ref _lastDialogue); text = ArmyRadioChatter.Dialogue(dialogue, false); }
                else if (quiet && stationary >= Mathf.Max(10f, _settings.jokeIdleSeconds)
                    && _random.NextDouble() < Mathf.Clamp01(_settings.jokeChance))
                    text = ArmyRadioChatter.Joke(NextDifferent(ArmyRadioChatter.JokeCount, ref _lastJoke));
                else text = ArmyRadioChatter.Phrase(situation, _variations[(int)situation]++);
                if (!Show(army, text, now)) continue;
                _cadence.Spoke(army.Id, now, _settings.minimumGap);
                _newContacts.Remove(army); _lastSpeakerId = army.Id;
                _nextAmbient = now + Mathf.Max(5f, _settings.ambientInterval);
                if (partner != null)
                {
                    _cadence.Reserve(partner.Id, now);
                    _exchange = new Exchange { First = army, Second = partner,
                        FirstUnit = ArmyRadioChatter.FirstUnit(army), SecondUnit = ArmyRadioChatter.FirstUnit(partner),
                        FirstHex = army.Hex, SecondHex = partner.Hex, Variation = dialogue,
                        ReplyAt = now + Mathf.Max(.5f, _settings.replyDelay),
                        ExpiresAt = now + Mathf.Max(.5f, _settings.replyDelay) + 1.5f };
                }
                return;
            }
            if (ambient) _nextAmbient = now + 2f; // Retry briefly if every visible army is cooling down.
        }
        private static bool CanIdleFlavor(ArmyData army)
        {
            if (army.IsAirArmy || StealthSystem.IsArmyFullyHidden(army)) return false;
            foreach (var unit in army.Members)
                if (unit != null && unit.HitPointsCurrent > 0 && unit.HitPointsCurrent < unit.HitPointsMax) return false;
            return true;
        }
        private int NextDifferent(int count, ref int previous)
        {
            int next = _random.Next(previous < 0 ? count : count - 1);
            if (next >= previous && previous >= 0) next++;
            previous = next; return next;
        }
        private int ActiveCount() { int count = 0; foreach (var view in _views) if (view.Active) count++; return count; }
        private bool Busy(ArmyData army) { foreach (var view in _views) if (view.Speaker == army) return true; return false; }
        private bool IsReserved(ArmyData army) => _exchange != null && (army == _exchange.First || army == _exchange.Second);
        private ArmyData FindPartner(ArmyData first, float now)
        {
            foreach (var second in _candidates)
                if (second != first && !Busy(second) && !_contacts.Contains(second)
                    && HexGridMath.Distance(first.Hex, second.Hex) == 1
                    && now - _observations[second].StationarySince >= Mathf.Max(1f, _settings.waitingSeconds)
                    && CanIdleFlavor(second)
                    && _cadence.ArmyReady(second.Id, now, _settings.armyCooldown) && CanPresent(second, out _, out _)) return second;
            return null;
        }
        private void PresentReply(float now)
        {
            var exchange = _exchange;
            if (exchange == null) return;
            bool firstSpeaking = false;
            foreach (var view in _views)
                if (view.Speaker == exchange.First && !view.Timing.IsDismissing) firstSpeaking = true;
            if (!firstSpeaking || now > exchange.ExpiresAt || _contacts.Contains(exchange.First) || _contacts.Contains(exchange.Second)
                || !exchange.First.Hex.Equals(exchange.FirstHex) || !exchange.Second.Hex.Equals(exchange.SecondHex)
                || ArmyRadioChatter.FirstUnit(exchange.First) != exchange.FirstUnit || ArmyRadioChatter.FirstUnit(exchange.Second) != exchange.SecondUnit
                || !CanPresent(exchange.First, out _, out _) || !CanPresent(exchange.Second, out _, out _))
            { _exchange = null; return; }
            if (now < exchange.ReplyAt || ActiveCount() >= Mathf.Clamp(_settings.maximumMessages, 1, _views.Length)) return;
            if (!Show(exchange.Second, ArmyRadioChatter.Dialogue(exchange.Variation, true), now)) return;
            _cadence.Spoke(exchange.Second.Id, now, _settings.minimumGap);
            _exchange = null;
        }

        private void Sample(float now)
        {
            _candidates.Clear();
            foreach (var army in ArmyRegistry.AllForOwner(_viewer))
            {
                if (!ArmyRadioChatter.CanSpeak(army, _viewer)) continue;
                _candidates.Add(army);
                if (!_observations.TryGetValue(army, out var state))
                { state = new Observation { Hex = army.Hex, StationarySince = now }; _observations.Add(army, state); }
                if (army.Controller != null && army.Controller.IsMoving)
                { _moved.Add(army); state.StationarySince = now; CancelExchangeFor(army); }
                if (!state.Hex.Equals(army.Hex)) { state.Hex = army.Hex; state.StationarySince = now; }
                bool contact = ArmyRadioChatter.SeesEnemy(army);
                if (contact) { if (_contacts.Add(army)) _newContacts.Add(army); }
                else { _contacts.Remove(army); _newContacts.Remove(army); }
            }
            _retired.Clear();
            foreach (var pair in _observations)
                if (!ArmyRegistry.IsRegistered(pair.Key) || !ArmyRadioChatter.CanSpeak(pair.Key, _viewer)) _retired.Add(pair.Key);
            foreach (var army in _retired)
            {
                _observations.Remove(army); _cadence.Forget(army.Id); _moved.Remove(army);
                _contacts.Remove(army); _newContacts.Remove(army); CancelExchangeFor(army);
            }
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
            markerRadius = pixels / Mathf.Max(.01f, _overlayCanvas.rootCanvas.scaleFactor); return true;
        }
        private bool Show(ArmyData army, string text, float now)
        {
            foreach (var view in _views)
            {
                if (view.Active) continue;
                if (!CanPresent(army, out var anchor, out float radius)) return false;
                view.Show(army, text, now, _settings);
                if (!view.Follow(SafeRect(), anchor, radius, _occupied)) { view.Hide(); return false; }
                view.AddObstacles(_occupied);
                return true;
            }
            return false;
        }
        private void LateUpdate() { if (_settings != null && _camera != null && _viewer != null) Follow(); }
        private void Follow()
        {
            _occupied.Clear();
            foreach (var view in _views)
            {
                if (!view.Active) continue;
                if (_viewer != VisionSystem.CurrentViewer || view.Unit != ArmyRadioChatter.FirstUnit(view.Speaker)
                    || !CanPresent(view.Speaker, out var anchor, out float radius)
                    || !view.Follow(SafeRect(), anchor, radius, _occupied))
                {
                    // No valid on-screen position: suppress immediately rather than draw overlapping
                    // art during an exit fade. Normal lifetime/click/modal exits retain their fade.
                    CancelExchangeFor(view.Speaker); view.Hide(); continue;
                }
                view.AddObstacles(_occupied);
            }
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
                { ScreenToLocal(RectTransformUtility.WorldToScreenPoint(hudCamera, corner), out var point); top = Mathf.Max(top, point.y); }
                if (top < safe.center.y) safe.yMin = top + 8f;
            }
            return ArmyRadioLayout.Inset(safe, 12f);
        }
        private void Clear()
        {
            _exchange = null;
            if (_views == null) return;
            foreach (var view in _views) if (view != null) view.Hide();
        }
        private bool HasConfiguredUI()
        {
            if (_canvas == null || _overlayCanvas == null || _overlayCanvas.transform != _canvas
                || _overlayCanvas.renderMode != RenderMode.ScreenSpaceOverlay || _views == null || _views.Length == 0 || _views.Length > 3) return false;
            for (int i = 0; i < _views.Length; i++)
            {
                if (_views[i] == null || !_views[i].IsConfigured || !_views[i].transform.IsChildOf(_canvas)) return false;
                for (int j = 0; j < i; j++) if (_views[j] == _views[i]) return false;
            }
            return true;
        }
    }
}
