using System.Collections;
using System.Collections.Generic;
using Game.Aviation;
using Game.HexGrid;
using Game.Terrain;
using Game.Units;
using UnityEngine;

namespace Game.Map
{
    // Sits on the same GameObject as an army's MapObjectVisual — the map-level presence for
    // one ArmyData (see ArmyData.Controller). A unit has no independent map presence any more
    // (see the project's own history: it used to be one MonoBehaviour/marker per UNIT, with
    // N-1 hidden per hex to fake "one army" — this replaces that with one real marker per army,
    // full stop). Holds no data of its own beyond the ArmyData reference; movement advances
    // every member's MoveCurrent in lockstep by reading Data.Members directly.
    public class ArmyController : MonoBehaviour
    {
        // Unity coroutines can't return a value the ordinary way — a resolveStepAsync callback
        // (see MoveAlong) mutates this scratch instance instead, and MoveRoutine reads it back
        // the instant the coroutine that callback returned actually finishes.
        public sealed class StepResolutionOutcome
        {
            // Supplied by MoveRoutine and evaluated by the resolver after any entry reaction has
            // mutated the roster. This keeps terminality based on the actual surviving formation:
            // losing the previous slowest aircraft to AA can change whether the next step is legal.
            public System.Func<bool> CanContinueMovement;
            public bool IsTerminalStep => CanContinueMovement == null || !CanContinueMovement();
            public bool StopMovement;
        }

        [SerializeField] private float pulseAmount = 0.1f;
        [SerializeField] private float pulseSpeed = 4f;
        [SerializeField] private float stepDuration = 0.3f;
        // How long it takes the idle bob/pulse to ease back to its resting pose before a move
        // starts — see SettleThen.
        [SerializeField] private float settleDuration = 0.12f;

        public ArmyData Data { get; private set; }
        public MapObjectVisual Visual { get; private set; }
        public bool IsMoving { get; private set; }

        // Where this army actually is right now, updated per-step during MoveAlong — separate
        // from Data.Hex, which stays the registry-authoritative value (only changed once, by
        // ArmyRegistry.MoveArmy, after the whole move finishes) so ArmyRegistry's hex->army
        // lookup is never out of sync mid-animation. Initialised from Data.Hex whenever a move
        // isn't in progress.
        public HexCoord CurrentHex => IsMoving ? _currentHex : Data.Hex;
        private HexCoord _currentHex;

        private Vector3 _baseScale;
        private Vector3 _defaultScale;
        private Coroutine _selectionAnimation;
        private Coroutine _settling;
        private Coroutine _movement;
        private System.Action _onComplete;
        private System.Action<HexCoord> _onCancelled;

        private const float LayoutDuration = 0.18f;
        private bool _layoutTransition;
        private Vector3 _layoutStart, _layoutTarget;
        private float _layoutElapsed;
        private Game.Players.PlayerSetupData _layoutViewer;
        private Vector3? _arrivalLayoutPosition;

        // Visibility is decided by the reconciler, never by this visual transition. A new
        // perspective/newly revealed marker snaps directly to its permitted resting pose.
        public void SetLayoutPosition(Vector3 position, bool animate)
        {
            if (IsMoving)
            {
                _arrivalLayoutPosition = position;
                return;
            }
            bool sameViewer = ReferenceEquals(_layoutViewer, VisionSystem.CurrentViewer);
            _layoutViewer = VisionSystem.CurrentViewer;
            if (!animate || !sameViewer || !isActiveAndEnabled || Visual == null || !Visual.IsVisible)
            {
                _layoutTransition = false;
                transform.position = position;
                return;
            }
            if (_layoutTransition && (_layoutTarget - position).sqrMagnitude < 0.000001f) return;
            if ((transform.position - position).sqrMagnitude < 0.000001f)
            {
                _layoutTransition = false;
                return;
            }
            _layoutStart = transform.position;
            _layoutTarget = position;
            _layoutElapsed = 0f;
            _layoutTransition = true;
        }

        private void Update()
        {
            if (!_layoutTransition || IsMoving) return;
            if (!ReferenceEquals(_layoutViewer, VisionSystem.CurrentViewer) || Visual == null || !Visual.IsVisible)
            {
                // Stop immediately; the next reconciliation sets the new viewer's pose.
                _layoutTransition = false;
                return;
            }
            _layoutElapsed += Time.deltaTime;
            float t = Mathf.Clamp01(_layoutElapsed / LayoutDuration);
            transform.position = Vector3.Lerp(_layoutStart, _layoutTarget, t * t * (3f - 2f * t));
            if (t >= 1f) _layoutTransition = false;
        }

        // Cancellation is a terminal order outcome, not arrival. Keep the entered hex even
        // when Unity stops a coroutine without running its tail (disable / Destroy).
        public void CancelMovement()
        {
            if (Data != null) Data.PendingAirStrikePolicy = null;
            _layoutTransition = false;
            _arrivalLayoutPosition = null;
            if (!IsMoving) return;
            HexCoord finalHex = _currentHex;
            var cancelled = _onCancelled;
            _onComplete = null;
            _onCancelled = null;
            IsMoving = false;
            var settling = _settling;
            var movement = _movement;
            _settling = null;
            _movement = null;
            if (settling != null) StopCoroutine(settling);
            if (movement != null) StopCoroutine(movement);
            cancelled?.Invoke(finalHex);
        }

        private void OnDisable() => CancelMovement();

        private void OnDestroy()
        {
            CancelMovement();
            if (Data != null && object.ReferenceEquals(Data.Controller, this))
                Data.Controller = null;
        }

        private void CompleteMovement()
        {
            if (!IsMoving) return;
            var completed = _onComplete;
            _onComplete = null;
            _onCancelled = null;
            _settling = null;
            _movement = null;
            try
            {
                // Existing arrival callbacks read CurrentHex while IsMoving is still true.
                completed?.Invoke();
            }
            finally
            {
                IsMoving = false;
                if (Data != null) Data.PendingAirStrikePolicy = null;
                if (_arrivalLayoutPosition.HasValue)
                {
                    Vector3 position = _arrivalLayoutPosition.Value;
                    _arrivalLayoutPosition = null;
                    SetLayoutPosition(position, true);
                }
            }
        }

        private void Awake()
        {
            _defaultScale = transform.localScale;
            Visual = GetComponent<MapObjectVisual>();
        }

        public void SetData(ArmyData data)
        {
            Data = data;
            _currentHex = data.Hex;
        }

        // Only meant to be called for the current player's own army — HexSelectionController is
        // responsible for that check, this just plays/stops the animation unconditionally.
        public void SetSelected(bool selected)
        {
            if (selected)
            {
                if (_selectionAnimation != null)
                    return;
                _baseScale = transform.localScale;
                _selectionAnimation = StartCoroutine(AnimateSelected());
            }
            else if (_selectionAnimation != null)
            {
                StopCoroutine(_selectionAnimation);
                _selectionAnimation = null;
                transform.localScale = _baseScale;
            }
        }

        private IEnumerator AnimateSelected()
        {
            float t = 0f;
            while (true)
            {
                t += Time.deltaTime;
                float pulsePhase = Mathf.Sin(t * pulseSpeed); // -1..1, starts at 0
                transform.localScale = _baseScale * (1f + pulseAmount * pulsePhase);
                yield return null;
            }
        }

        // Stops the selection animation and snaps back to a clean resting transform — used
        // whenever the army is deselected (a new hex is picked, or the turn passes to someone
        // else) so it never lingers mid-pulse.
        public void ResetTransform(HexMap map, Vector3 iconOffset)
        {
            SetSelected(false);
            if (map != null)
                SetLayoutPosition(map.HexToWorld(Data.Hex) + iconOffset, true);
            transform.localScale = _defaultScale;
        }

        // Eases the current pulse scale back down to the resting scale over settleDuration,
        // THEN invokes onSettled — instead of an abrupt snap. Used right before a move starts.
        // Also claims IsMoving right away (not just once the move animation itself starts) so a
        // second order can't sneak in while this one is still easing out.
        public void SettleThen(System.Action onSettled, System.Action<HexCoord> onCancelled = null)
        {
            if (IsMoving)
                return;
            _currentHex = Data.Hex;
            _layoutTransition = false;
            _arrivalLayoutPosition = null;
            _onCancelled = onCancelled;
            IsMoving = true;

            if (_selectionAnimation == null)
            {
                onSettled?.Invoke();
                return;
            }
            _settling = StartCoroutine(SettleRoutine(onSettled));
        }

        private IEnumerator SettleRoutine(System.Action onSettled)
        {
            StopCoroutine(_selectionAnimation);
            _selectionAnimation = null;

            Vector3 startScale = transform.localScale;
            float elapsed = 0f;
            while (elapsed < settleDuration)
            {
                elapsed += Time.deltaTime;
                float t = elapsed / settleDuration;
                transform.localScale = Vector3.Lerp(startScale, _baseScale, t);
                yield return null;
            }
            transform.localScale = _baseScale;
            _settling = null;
            if (IsMoving) onSettled?.Invoke();
        }

        // path[0] is this army's current hex (not entered — no cost, already there); each later
        // entry is walked to in turn, in world-space order, as long as the army's shared move
        // budget can afford that hex's full cost. Stops short the moment the next hex would cost
        // more than what's left — never enters a hex it can't fully pay for. resolveOffset is
        // called fresh for every hex entered
        // (including the starting one, via ResetTransform below) since a hex's correct icon
        // offset depends on what else is on it (see HexObjectLayout), not just at the final
        // destination. SettleThen claims IsMoving before the easing animation, preventing
        // another order during that phase. MoveAlong retains that claim until completion
        // or explicit cancellation, including direct callers without an easing animation.
        public void MoveAlong(HexMap map, List<HexCoord> path, System.Func<HexCoord, Vector3> resolveOffset,
            System.Action onComplete = null, System.Func<HexCoord, bool> shouldStopEarly = null,
            System.Action<HexCoord, HexCoord> onStepStarted = null,
            System.Action<HexCoord, HexCoord> onStepCompleted = null,
            System.Func<HexCoord, HexCoord, StepResolutionOutcome, IEnumerator> resolveStepAsync = null,
            System.Func<bool> beforeFirstStep = null)
        {
            if (map == null || path == null || path.Count < 2 || resolveOffset == null || Data == null || Data.Members.Count == 0)
            {
                CancelMovement();
                return;
            }

            _onComplete = onComplete;
            IsMoving = true;
            _currentHex = Data.Hex;
            _layoutTransition = false;
            _arrivalLayoutPosition = null;
            SetSelected(false);
            transform.localScale = _defaultScale;
            _movement = StartCoroutine(MoveRoutine(map, path, resolveOffset, shouldStopEarly,
                onStepStarted, onStepCompleted, resolveStepAsync, beforeFirstStep));
        }

        // shouldStopEarly is called once per hex actually entered (never the origin), AFTER this
        // army has visually landed there and _currentHex/vision have been updated for it — same
        // "stop short" idea as running out of shared move points below, just driven by the
        // caller instead (see HexSelectionController.Movement.cs's own reveal-on-entry check:
        // fog hides what a hex holds until the mover is actually standing on it, so a path
        // computed from the fogged-out start can't already know to stop there on its own).
        //
        // resolveStepAsync (optional) is the ONE place this whole shared pipeline can actually
        // PAUSE an in-progress animated move — an AA reaction or air strike (see Game.Aviation.
        // AviationCombatPresenter) needs the exact same army sitting still while its own popup
        // resolves, rather than kicking off a second, nested move of its own. Existing ground
        // callers simply never pass one and keep today's fire-and-forget onStepCompleted timing.
        private IEnumerator MoveRoutine(HexMap map, List<HexCoord> path, System.Func<HexCoord, Vector3> resolveOffset,
            System.Func<HexCoord, bool> shouldStopEarly,
            System.Action<HexCoord, HexCoord> onStepStarted,
            System.Action<HexCoord, HexCoord> onStepCompleted,
            System.Func<HexCoord, HexCoord, StepResolutionOutcome, IEnumerator> resolveStepAsync,
            System.Func<bool> beforeFirstStep)
        {
            List<UnitData> members = Data.Members;
            bool started = false;
            for (int i = 1; IsMoving && members.Count > 0 && i < path.Count; i++)
            {
                HexCoord next = path[i];
                if (!map.CanEnter(next, Data) || HexGridMath.Distance(_currentHex, next) != 1)
                    break;
                map.TryGetTerrainAt(next, out TerrainTypeEntry entry);
                int terrainCost = entry != null ? Mathf.Max(1, entry.moveCost) : 1;
                int cost = AviationRules.MovementCost(Data, terrainCost);

                int sharedMoveCurrent = AviationRules.EffectiveMoveCurrent(members[0]);
                for (int m = 1; m < members.Count; m++)
                    if (AviationRules.EffectiveMoveCurrent(members[m]) < sharedMoveCurrent)
                        sharedMoveCurrent = AviationRules.EffectiveMoveCurrent(members[m]);
                if (sharedMoveCurrent < cost)
                    break;
                // Activation is committed only once a legal, affordable first entry exists.
                if (!started && beforeFirstStep != null && !beforeFirstStep()) break;
                started = true;

                foreach (UnitData member in members)
                {
                    // A fuel-penalised aircraft exposes half its raw MP as usable MP. Spend
                    // two raw points per entered hex so that displayed usable MP falls by one
                    // every step instead of every second step.
                    int rawCost = member.HasEmergencyFlightPenalty ? cost * 2 : cost;
                    member.MoveCurrent = Mathf.Max(0, member.MoveCurrent - rawCost);
                }
                HexCoord previous = _currentHex;
                _currentHex = next;
                onStepStarted?.Invoke(previous, next);
                if (!IsMoving) yield break;

                Vector3 targetPosition = map.HexToWorld(next) + resolveOffset(next);
                yield return StepTo(targetPosition);
                if (!IsMoving) yield break;
                onStepCompleted?.Invoke(previous, next);
                if (!IsMoving) yield break;

                if (resolveStepAsync != null)
                {
                    int resolvedStepIndex = i;
                    var outcome = new StepResolutionOutcome
                    {
                        CanContinueMovement = () => CanAffordNextMovementStep(
                            map, path, resolvedStepIndex, Data, Data.Members)
                    };
                    yield return resolveStepAsync(previous, next, outcome);
                    if (!IsMoving) yield break;
                    // Data.Members is the SAME list `members` already points at — a reaction that
                    // destroyed every member (e.g. AA/air-strike wiping this army out) shrinks it
                    // in place, so the next loop iteration's members[0] lookup above must never
                    // run against an empty roster.
                    if (Data.Members.Count == 0 || outcome.StopMovement)
                        break;
                }

                if (shouldStopEarly != null && shouldStopEarly(next))
                    break;
            }

            // onComplete (see HexSelectionController.TryIssueMoveOrder) reads CurrentHex to find
            // out where the army actually ended up — that property falls back to the stale
            // Data.Hex once IsMoving is false, so IsMoving must still read true for the whole
            // duration of this call. Nothing else runs between here and onComplete returning
            // (no yield in between), so deferring the flip costs nothing.
            CompleteMovement();
        }

        // Computes the endpoint from the same path, terrain and shared movement rules the next
        // loop iteration would use. Kept here, at the movement owner, so aviation does not
        // duplicate terrain/fuel-penalty accounting merely to decide whether it may strike.
        private static bool CanAffordNextMovementStep(HexMap map, List<HexCoord> path, int currentIndex,
            ArmyData army, List<UnitData> members)
        {
            if (map == null || path == null || currentIndex >= path.Count - 1
                || army == null || members == null || members.Count == 0)
                return false;

            HexCoord next = path[currentIndex + 1];
            if (!map.CanEnter(next, army)) return false;
            map.TryGetTerrainAt(next, out TerrainTypeEntry entry);
            int terrainCost = entry != null ? Mathf.Max(1, entry.moveCost) : 1;
            int nextCost = AviationRules.MovementCost(army, terrainCost);

            int sharedMoveCurrent = AviationRules.EffectiveMoveCurrent(members[0]);
            for (int i = 1; i < members.Count; i++)
                if (AviationRules.EffectiveMoveCurrent(members[i]) < sharedMoveCurrent)
                    sharedMoveCurrent = AviationRules.EffectiveMoveCurrent(members[i]);
            return sharedMoveCurrent >= nextCost;
        }

        private IEnumerator StepTo(Vector3 targetPosition)
        {
            Vector3 start = transform.position;
            float elapsed = 0f;
            while (elapsed < stepDuration)
            {
                elapsed += Time.deltaTime;
                transform.position = Vector3.Lerp(start, targetPosition, elapsed / stepDuration);
                yield return null;
            }
            transform.position = targetPosition;
        }
    }
}

