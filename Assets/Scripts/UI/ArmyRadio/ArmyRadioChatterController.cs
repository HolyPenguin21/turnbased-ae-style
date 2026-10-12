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
    public sealed partial class ArmyRadioChatterController : MonoBehaviour
    {
        private sealed class Observation
        {
            public float StationarySince;
            public HexCoord Hex;
            public float ContactAt;
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
        [Tooltip("Zoom reference for the map camera; zooming out shrinks text, art and lines together.")]
        [Min(.01f)] [SerializeField] private float referenceOrthographicSize = 2.6f;
        [Tooltip("Uniform size multiplier for the complete radio message, including placement bounds and pointers.")]
        [Min(.01f)] [SerializeField] private float presentationScale = 2f;
        private readonly Vector3[] _hudCorners = new Vector3[4];
        [SerializeField] private ArmyRadioBubbleView[] _views = System.Array.Empty<ArmyRadioBubbleView>();
        private readonly List<Rect> _occupied = new List<Rect>(96);
        private readonly List<ArmyData> _candidates = new List<ArmyData>();
        private readonly List<ArmyData> _retired = new List<ArmyData>();
        private readonly Dictionary<ArmyData, Observation> _observations = new Dictionary<ArmyData, Observation>();
        private readonly HashSet<ArmyData> _moved = new HashSet<ArmyData>();
        private readonly HashSet<ArmyData> _contacts = new HashSet<ArmyData>();
        private readonly HashSet<ArmyData> _newContacts = new HashSet<ArmyData>();
        private readonly System.Random _random = new System.Random();
        private ArmyRadioCadence _cadence;
        private ArmyRadioChatterSettings _settings;
        private PlayerSetupData _viewer, _actor;
        private Exchange _exchange;
        private int _lastSpeakerId = -1;
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
            _cadence = new ArmyRadioCadence(Time.unscaledTime);
            _viewer = null; _actor = null; ResetChannel();
            if (isActiveAndEnabled) Subscribe();
        }
        private void OnEnable() { if (_turns != null) Subscribe(); }
        private void OnDisable() { Unsubscribe(); Clear(); }
        private void Subscribe()
        {
            if (_subscribed || _turns == null || _settings == null) return;
            _subscribed = true;
            SubscribeOperations();
            _turns.TurnChanging += Invalidate; _turns.TurnStateChanged += OnTurnStateChanged;
            VisionSystem.VisibilityChanged += OnVision; VisionSystem.VisibleContentChanged += OnContent;
            ArmyRegistry.ArmyRelocated += OnRelocated; StealthSystem.StealthChanged += Invalidate;
            if (_selection != null)
            {
                _selection.ArmyInteracted += OnArmyInteracted;
                _selection.HexInteracted += OnHexInteracted;
            }
            OnTurnStateChanged();
        }
        private void Unsubscribe()
        {
            if (!_subscribed) return;
            _subscribed = false;
            UnsubscribeOperations();
            if (_turns != null) { _turns.TurnChanging -= Invalidate; _turns.TurnStateChanged -= OnTurnStateChanged; }
            VisionSystem.VisibilityChanged -= OnVision; VisionSystem.VisibleContentChanged -= OnContent;
            ArmyRegistry.ArmyRelocated -= OnRelocated; StealthSystem.StealthChanged -= Invalidate;
            if (_selection != null)
            {
                _selection.ArmyInteracted -= OnArmyInteracted;
                _selection.HexInteracted -= OnHexInteracted;
            }
        }
        private void ResetChannel()
        {
            _contacts.Clear(); _newContacts.Clear(); _moved.Clear(); _observations.Clear();
            ClearOperations();
            _cadence.ResetSpeakers(); _lastSpeakerId = -1;
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
                OnFlightOperation(army);
                if (_exploredOnMove.TryGetValue(army, out var visited))
                {
                    if (visited.Equals(to)) Enqueue(ArmyRadioCategory.TerritoryExplored, army, null, to);
                    _exploredOnMove.Remove(army);
                }
            }
            Invalidate();
        }
        private void Invalidate() => _dirty = true;
        private void CancelExchangeFor(ArmyData army)
        { if (_exchange != null && (army == _exchange.First || army == _exchange.Second)) _exchange = null; }
        private void OnArmyInteracted(ArmyData army)
        {
            if (army == null) return;
            float now = Time.unscaledTime;
            foreach (var view in _views) if (view.Speaker == army) view.Hide();
            CancelExchangeFor(army);
            if (army.Owner == _viewer) _cadence.Reserve(army.Id, now);
        }
        private void OnHexInteracted(HexCoord hex)
        {
            // A click on the waiting partner must cancel its reply as well.
            if (_exchange != null)
            {
                if (_exchange.First.Hex.Equals(hex)) OnArmyInteracted(_exchange.First);
                else if (_exchange.Second.Hex.Equals(hex)) OnArmyInteracted(_exchange.Second);
            }
            foreach (var view in _views)
                if (view.Speaker != null && view.Speaker.Hex.Equals(hex)) OnArmyInteracted(view.Speaker);
        }

        private void Update()
        {
            if (_settings == null) return;
            float now = Time.unscaledTime;
            foreach (var view in _views) view.Tick(now);
            if (_turns == null || !_settings.enabled || !_turns.isActiveAndEnabled || _camera == null)
            { Clear(); return; }
            if (_viewer != VisionSystem.CurrentViewer) OnTurnStateChanged();
            if (_viewer == null || !_turns.IsArmyRadioMatchActive) { Clear(); return; }
            if (now >= _nextObservation) _dispatch.Prune(now, _validCandidate);
            if (!_turns.CanPresentArmyRadio)
            {
                _exchange = null;
                foreach (var view in _views) view.Hide();
                if (now >= _nextObservation) _nextObservation = now + 1f;
                return;
            }
            if (now >= _nextObservation)
            { Sample(now); _dirty = false; _nextObservation = now + 1f; }
            Follow();
            // An operational report may suppress a future ambient reply, without cutting off art.
            if (_dispatch.PendingCount > 0) _exchange = null;
            PresentReply(now);
            if (ActiveCount() >= Mathf.Clamp(_settings.maximumMessages, 1, _views.Length)
                || !_cadence.ChannelReady(now, _settings.maximumPerMinute)) return;
            if (_dispatch.PendingCount == 0 && _newContacts.Count == 0 && now < _nextAmbient) return;
            if (_dispatch.TryTake(now, _lastSpeakerId, _settings.categoryCooldown,
                _validCandidate, _readyCandidate, out var operation))
            {
                Decide(operation, now);
                return;
            }
            bool ambient = now >= _nextAmbient;
            ArmyData selected = null;
            bool newContact = false;
            foreach (var army in _candidates)
            {
                bool contact = _newContacts.Contains(army);
                if (contact && now - _observations[army].ContactAt >= 6f)
                { _newContacts.Remove(army); contact = false; }
                if ((!contact && !ambient) || Busy(army) || IsReserved(army)
                    || !_cadence.CanSpeak(army.Id, now, _settings.armyCooldown) || !CanPresent(army, out _, out _)) continue;
                float idle = now - _observations[army].StationarySince;
                var stateSituation = CurrentSituation(army, now, _contacts.Contains(army));
                var stateCategory = (ArmyRadioCategory)((int)ArmyRadioCategory.Contact + (int)stateSituation);
                bool idleFlavor = _dispatch.PendingCount == 0 && !_contacts.Contains(army)
                    && idle >= Mathf.Max(1f, _settings.waitingSeconds) && ArmyRadioChatter.CanIdleFlavor(army);
                if (!_dispatch.CategoryReady(stateCategory, now, _settings.categoryCooldown)
                    && (!idleFlavor || !_dispatch.CategoryReady(ArmyRadioCategory.Atmosphere, now, _settings.categoryCooldown))) continue;
                // Preserve new-contact preference and speaker rotation without sorting the roster.
                if (selected == null || (contact && !newContact)
                    || (contact == newContact && PreferSpeaker(army, selected)))
                { selected = army; newContact = contact; }
            }
            if (selected == null) { _cadence.Quiet(now, 1f); return; }
            var speaker = selected;
            float stationary = now - _observations[speaker].StationarySince;
            bool hasContact = ArmyRadioChatter.SeesEnemy(speaker);
            if (hasContact && _contacts.Add(speaker)) _observations[speaker].ContactAt = now;
            if (!hasContact) { _contacts.Remove(speaker); _newContacts.Remove(speaker); }
            if (newContact && !hasContact) { _cadence.Quiet(now, 1f); return; }
            var situation = CurrentSituation(speaker, now, hasContact);
            bool quiet = _dispatch.PendingCount == 0 && !hasContact && stationary >= Mathf.Max(1f, _settings.waitingSeconds)
                && ArmyRadioChatter.CanIdleFlavor(speaker);
            // Atmosphere is generated only in this open opportunity, never queued ahead of time.
            var stateCategoryForSpeaker = (ArmyRadioCategory)((int)ArmyRadioCategory.Contact + (int)situation);
            bool flavor = quiet && _dispatch.CategoryReady(ArmyRadioCategory.Atmosphere, now, _settings.categoryCooldown)
                && (!_dispatch.CategoryReady(stateCategoryForSpeaker, now, _settings.categoryCooldown) || _random.NextDouble() < .5d);
            var category = flavor ? ArmyRadioCategory.Atmosphere : (ArmyRadioCategory)((int)ArmyRadioCategory.Contact + (int)situation);
            _newContacts.Remove(speaker); // One decision for this contact, including a refusal.
            _nextAmbient = now + Mathf.Max(5f, _settings.ambientInterval);
            _cadence.Quiet(now, _settings.minimumGap);
            if (!_dispatch.CategoryReady(category, now, _settings.categoryCooldown)
                || !ArmyRadioDispatch.Accept(_random.NextDouble(), ArmyRadioDispatch.Chance(category, _settings), _cadence.RecentCount(now))) return;
            var candidate = new ArmyRadioCandidate(category, speaker, _viewer, null, speaker.Hex, now, 0f);
            if (!IsCurrent(candidate)) return;
            ArmyData partner = null; int dialogue = -1;
            string text;
            if (flavor && _exchange == null && ActiveCount() == 0 && _cadence.DensityReady(now, _settings.maximumPerMinute, 2)
                && Mathf.Min(_settings.maximumMessages, _views.Length) > 1
                && _random.NextDouble() < Mathf.Clamp01(_settings.dialogueChance)) partner = FindPartner(speaker, now);
            if (partner != null)
            {
                dialogue = ChooseDialogue();
                if (dialogue < 0) return;
                text = ArmyRadioChatter.Dialogue(dialogue, false);
            }
            else if (flavor && stationary >= Mathf.Max(10f, _settings.jokeIdleSeconds)
                && _random.NextDouble() < Mathf.Clamp01(_settings.jokeChance))
                text = ChoosePhrase(ArmyRadioChatter.JokeCount, ArmyRadioChatter.Joke);
            else if (flavor && speaker.IsAirArmy) text = ChoosePhrase(ArmyRadioChatter.PilotAmbientCount, ArmyRadioChatter.PilotAmbient);
            else if (flavor) text = ChoosePhrase(ArmyRadioChatter.AmbientCount, ArmyRadioChatter.Ambient);
            else text = ChoosePhrase(ArmyRadioChatter.PhraseCount(situation), i => ArmyRadioChatter.Phrase(situation, i));
            if (text == null || !Show(speaker, text, now)) return;
            RecordShown(candidate, text, now);
            if (partner != null)
            {
                _cadence.Reserve(partner.Id, now);
                _exchange = new Exchange { First = speaker, Second = partner,
                    FirstUnit = ArmyRadioChatter.FirstUnit(speaker), SecondUnit = ArmyRadioChatter.FirstUnit(partner),
                    FirstHex = speaker.Hex, SecondHex = partner.Hex, Variation = dialogue,
                    ReplyAt = now + Mathf.Max(.5f, _settings.replyDelay),
                    ExpiresAt = now + Mathf.Max(.5f, _settings.replyDelay) + 1.5f };
            }
        }
        private ArmyRadioSituation CurrentSituation(ArmyData army, float now, bool contact)
        {
            float stationary = now - _observations[army].StationarySince;
            bool waiting = _actor == _viewer && stationary >= Mathf.Max(1f, _settings.waitingSeconds);
            var situation = ArmyRadioChatter.Situation(army, contact, _moved.Contains(army), waiting ? 1f : 0f);
            // Activation alone is not arrival; do not recycle old arrival claims later in the turn.
            if (situation == ArmyRadioSituation.Moving && (!_moved.Contains(army) || stationary >= 6f))
                return waiting ? ArmyRadioSituation.Waiting : ArmyRadioSituation.Ready;
            return situation;
        }
        private bool PreferSpeaker(ArmyData a, ArmyData b)
        {
            bool wrapA = a.Id <= _lastSpeakerId, wrapB = b.Id <= _lastSpeakerId;
            return wrapA != wrapB ? !wrapA : a.Id < b.Id;
        }
        private string ChoosePhrase(int count, System.Func<int, string> phrase)
        {
            int start = _random.Next(count);
            for (int i = 0; i < count; i++)
            {
                string text = phrase((start + i) % count);
                if (!_dispatch.Recent(text)) return text;
            }
            return null;
        }
        private int ChooseDialogue()
        {
            int start = _random.Next(ArmyRadioChatter.DialogueCount);
            for (int i = 0; i < ArmyRadioChatter.DialogueCount; i++)
            {
                int index = (start + i) % ArmyRadioChatter.DialogueCount;
                if (!_dispatch.Recent(ArmyRadioChatter.Dialogue(index, false))
                    && !_dispatch.Recent(ArmyRadioChatter.Dialogue(index, true))) return index;
            }
            return -1;
        }
        private void Decide(ArmyRadioCandidate candidate, float now)
        {
            _cadence.Quiet(now, _settings.minimumGap);
            _nextAmbient = now + Mathf.Max(5f, _settings.ambientInterval);
            if (!ArmyRadioDispatch.Accept(_random.NextDouble(), ArmyRadioDispatch.Chance(candidate.Category, _settings), _cadence.RecentCount(now))) return;
            string text = ChoosePhrase(ArmyRadioChatter.OperationalPhraseCount(candidate.Category),
                i => ArmyRadioChatter.OperationalPhrase(candidate.Category, i));
            if (text != null && IsCurrent(candidate) && Show(candidate.Speaker, text, now)) RecordShown(candidate, text, now);
        }
        private void RecordShown(ArmyRadioCandidate candidate, string text, float now)
        {
            _dispatch.Shown(candidate.Category, text, now, _settings.recentPhraseHistorySize);
            _cadence.Spoke(candidate.Speaker.Id, now, _settings.minimumGap);
            _lastSpeakerId = candidate.Speaker.Id;
            _retired.Clear();
            foreach (var pair in _onAir) if (!Busy(pair.Key)) _retired.Add(pair.Key);
            foreach (var army in _retired) _onAir.Remove(army);
            _onAir[candidate.Speaker] = candidate;
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
                    && ArmyRadioChatter.CanIdleFlavor(second)
                    && _cadence.ArmyReady(second.Id, now, _settings.armyCooldown) && CanPresent(second, out _, out _)) return second;
            return null;
        }
        private void PresentReply(float now)
        {
            var exchange = _exchange;
            if (exchange == null) return;
            bool firstSpeaking = false;
            foreach (var view in _views)
                if (view.Active && view.Speaker == exchange.First && !view.Timing.IsDismissing) firstSpeaking = true;
            if (!firstSpeaking || !ArmyRadioChatter.CanIdleFlavor(exchange.First) || !ArmyRadioChatter.CanIdleFlavor(exchange.Second) || now > exchange.ExpiresAt || _contacts.Contains(exchange.First) || _contacts.Contains(exchange.Second)
                || !exchange.First.Hex.Equals(exchange.FirstHex) || !exchange.Second.Hex.Equals(exchange.SecondHex)
                || ArmyRadioChatter.FirstUnit(exchange.First) != exchange.FirstUnit || ArmyRadioChatter.FirstUnit(exchange.Second) != exchange.SecondUnit
                || !CanPresent(exchange.First, out _, out _) || !CanPresent(exchange.Second, out _, out _))
            { _exchange = null; return; }
            if (now < exchange.ReplyAt || !_cadence.DensityReady(now, _settings.maximumPerMinute) || ActiveCount() >= Mathf.Clamp(_settings.maximumMessages, 1, _views.Length)) return;
            if (ArmyRadioChatter.SeesEnemy(exchange.First) || ArmyRadioChatter.SeesEnemy(exchange.Second))
            { _exchange = null; Invalidate(); return; }
            string reply = ArmyRadioChatter.Dialogue(exchange.Variation, true);
            if (_dispatch.Recent(reply) || !Show(exchange.Second, reply, now)) { _exchange = null; return; }
            RecordShown(new ArmyRadioCandidate(ArmyRadioCategory.Atmosphere, exchange.Second, _viewer,
                null, exchange.Second.Hex, now, 0f), reply, now);
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
                _samplingArmy = army;
                bool contact = _dirty || _primeObservation ? ArmyRadioChatter.Observe(army, _observeBase) : _contacts.Contains(army);
                if (contact) { if (_contacts.Add(army)) { _newContacts.Add(army); state.ContactAt = now; } }
                else { _contacts.Remove(army); _newContacts.Remove(army); }
            }
            _retired.Clear();
            foreach (var pair in _observations)
                if (!ArmyRegistry.IsRegistered(pair.Key) || !ArmyRadioChatter.CanSpeak(pair.Key, _viewer)) _retired.Add(pair.Key);
            foreach (var army in _retired)
            {
                _observations.Remove(army); _cadence.Forget(army.Id); _moved.Remove(army); _sorties.Remove(army); _onAir.Remove(army); _exploredOnMove.Remove(army);
                _contacts.Remove(army); _newContacts.Remove(army); CancelExchangeFor(army);
            }
            _samplingArmy = null; _primeObservation = false;
        }
        private bool CanPresent(ArmyData army, out Vector2 anchor, out float markerRadius)
        {
            anchor = Vector2.zero; markerRadius = 0f;
            if (!ArmyRegistry.IsRegistered(army) || !ArmyRadioChatter.CanSpeak(army, _viewer)
                || !HasSpeakerVisual(army) || army.Controller.IsMoving
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
                if (!view.Follow(SafeRect(), anchor, radius, _occupied, MessageScale())) { view.Hide(); return false; }
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
                if ((_onAir.TryGetValue(view.Speaker, out var current) && !IsCurrent(current))
                    || _viewer != VisionSystem.CurrentViewer || view.Unit != ArmyRadioChatter.FirstUnit(view.Speaker)
                    || !CanPresent(view.Speaker, out var anchor, out float radius)
                    || !view.Follow(SafeRect(), anchor, radius, _occupied, MessageScale()))
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
        private float MessageScale() => Mathf.Max(.01f, presentationScale) * (_camera.orthographic
            ? ArmyRadioLayout.CameraScale(_camera.orthographicSize, referenceOrthographicSize) : 1f);
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
            ClearOperations();
            foreach (var army in _observations.Keys) _cadence?.Forget(army.Id);
            _observations.Clear(); _contacts.Clear(); _newContacts.Clear(); _moved.Clear(); _candidates.Clear();
            _dirty = true;
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
