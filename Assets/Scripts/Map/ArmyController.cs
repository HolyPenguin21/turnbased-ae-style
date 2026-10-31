using System.Collections;
using System.Collections.Generic;
using Game.HexGrid;
using UnityEngine;

namespace Game.Map
{
    // Map marker presentation. ArmyMovementOperation owns legal movement and world effects.
    public class ArmyController : MonoBehaviour
    {
        // Compatibility outcome shared with aviation. ArmyMovementOperation evaluates it
        // after the suspended resolver has completed.
        public sealed class StepResolutionOutcome
        {
            // Supplied by the operation and evaluated by the resolver after any entry reaction has
            // mutated the roster. This keeps terminality based on the actual surviving formation:
            // losing the previous slowest aircraft to AA can change whether the next step is legal.
            public System.Func<bool> CanContinueMovement;
            public bool IsTerminalStep => CanContinueMovement == null || !CanContinueMovement();
            public bool StopMovement;
            public bool Cancelled;
            // Explicit ownership bridge for a suspended per-step operation; Unity stopping a
            // coroutine alone does not guarantee disposal of its nested iterators.
            public System.Action CancelResolution;
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

        // Legal position is owned by the operation and registry, including during animation.
        public HexCoord CurrentHex => Data?.Movement?.IsBusy == true ? Data.Movement.CurrentHex : Data.Hex;
        private ArmyMovementOperation _presentedMovement;
        private System.Action<ArmyMovementResult> _movementTermination;
        internal int MovementGeneration => _motionEpoch;
        internal bool PresentsMovement(ArmyMovementOperation operation) => operation != null && IsMoving && ReferenceEquals(_presentedMovement, operation);

        private Vector3 _baseScale;
        private Vector3 _defaultScale;
        private Coroutine _selectionAnimation;
        private Coroutine _settling;
        private Coroutine _movement;
        private int _motionEpoch;
        private HexMap _movementMap;
        private System.Func<HexCoord, Vector3> _movementOffset;
        private System.Action _onSettled;
        private System.Action<HexCoord> _onCancelled;
        private System.Action _afterMovement;
        private int _continuationsInProgress;
        internal bool HasPendingMovementContinuation => _afterMovement != null || _continuationsInProgress > 0;

        // An entry effect may synchronously decide to continue (event Skip). Transfer that
        // intent after this presentation settles, before a waiting caller sees an idle marker.
        internal bool ContinueAfterMovement(ArmyMovementOperation expected, System.Action continuation)
        {
            if (!PresentsMovement(expected) || continuation == null || _afterMovement != null) return false;
            _afterMovement = continuation;
            return true;
        }

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
            _afterMovement = null;
            // A raw SettleThen reservation has no domain owner yet. Real operations clear
            // their own policy before Completed; an old presenter must not clear a new order's.
            if (_presentedMovement == null && Data != null && !Data.IsMovementBusy) Data.PendingAirStrikePolicy = null;
            _layoutTransition = false;
            _arrivalLayoutPosition = null;
            if (!IsMoving) { Data?.Movement?.Cancel(); return; }
            HexCoord finalHex = CurrentHex;
            var cancelled = _onCancelled;
            var operation = _presentedMovement;
            var settling = _settling;
            var movement = _movement;
            var movementMap = _movementMap;
            var movementOffset = _movementOffset;
            _onSettled = null; _onCancelled = null;
            _settling = null; _movement = null;
            _movementMap = null; _movementOffset = null;
            if (operation != null && _movementTermination != null) operation.Completed -= _movementTermination;
            _movementTermination = null; _presentedMovement = null;
            IsMoving = false;
            _motionEpoch++;
            // Release this presentation before publishing the domain result. A Completed
            // subscriber may immediately bind a replacement move on the same marker.
            try
            {
                if (movementMap != null && movementOffset != null && Data != null
                    && Data.Members.Count > 0 && ArmyRegistry.IsRegistered(Data))
                    SetLayoutPosition(movementMap.HexToWorld(finalHex) + movementOffset(finalHex), false);
                cancelled?.Invoke(finalHex);
            }
            finally
            {
                try { operation?.Cancel(); }
                finally
                {
                    if (settling != null) StopCoroutine(settling);
                    if (movement != null) StopCoroutine(movement);
                }
            }
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
            var settled = _onSettled;
            var continuation = _afterMovement;
            _onSettled = null;
            _onCancelled = null;
            _settling = null;
            _movement = null;
            _movementMap = null; _movementOffset = null;
            IsMoving = false;
            if (_arrivalLayoutPosition.HasValue)
            {
                Vector3 position = _arrivalLayoutPosition.Value;
                _arrivalLayoutPosition = null;
                SetLayoutPosition(position, true);
            }
            if (continuation != null) _continuationsInProgress++;
            try { settled?.Invoke(); }
            finally
            {
                try
                {
                    // A settlement callback may cancel or replace the order. In that case
                    // its old continuation has been cleared by CancelMovement/PlayMovement.
                    if (ReferenceEquals(_afterMovement, continuation))
                    {
                        _afterMovement = null;
                        continuation?.Invoke();
                    }
                }
                finally { if (continuation != null) _continuationsInProgress--; }
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
            if (IsMoving || Data?.IsMovementBusy == true)
                return;
            _layoutTransition = false;
            _arrivalLayoutPosition = null;
            _onCancelled = onCancelled;
            IsMoving = true;

            if (_selectionAnimation == null)
            {
                onSettled?.Invoke();
                return;
            }
            int epoch = ++_motionEpoch;
            var settling = StartCoroutine(SettleRoutine(onSettled, epoch));
            if (_motionEpoch == epoch && IsMoving) _settling = settling;
        }

        private IEnumerator SettleRoutine(System.Action onSettled, int epoch)
        {
            StopCoroutine(_selectionAnimation);
            _selectionAnimation = null;

            Vector3 startScale = transform.localScale;
            float elapsed = 0f;
            while (IsMoving && epoch == _motionEpoch && elapsed < settleDuration)
            {
                elapsed += Time.deltaTime;
                float t = elapsed / settleDuration;
                transform.localScale = Vector3.Lerp(startScale, _baseScale, t);
                yield return null;
            }
            if (!IsMoving || epoch != _motionEpoch) yield break;
            transform.localScale = _baseScale;
            _settling = null;
            if (IsMoving) onSettled?.Invoke();
        }

        // Compatibility adapter for callers supplying an explicit route and callbacks.
        public void MoveAlong(HexMap map, List<HexCoord> path, System.Func<HexCoord, Vector3> resolveOffset,
            System.Action onComplete = null, System.Func<HexCoord, bool> shouldStopEarly = null,
            System.Action<HexCoord, HexCoord> onStepStarted = null,
            System.Action<HexCoord, HexCoord> onStepCompleted = null,
            System.Func<HexCoord, HexCoord, StepResolutionOutcome, IEnumerator> resolveStepAsync = null,
            System.Func<bool> beforeFirstStep = null, System.Action onSettled = null)
        {
            if (map == null || path == null || path.Count < 2 || resolveOffset == null || Data == null || Data.Members.Count == 0)
            { CancelMovement(); return; }
            PlayMovement(ArmyMovementOperation.ForMap(Data, map, path, beforeFirstStep, onComplete),
                map, resolveOffset, shouldStopEarly, onStepStarted, onStepCompleted, resolveStepAsync, onSettled, settle: false);
        }

        // Normal order entry already owns a Pending operation before any easing animation.
        // The compatibility MoveAlong adapter above uses exactly the same operation and driver.
        public void PlayMovement(ArmyMovementOperation operation, HexMap map,
            System.Func<HexCoord, Vector3> resolveOffset, System.Func<HexCoord, bool> shouldStopEarly = null,
            System.Action<HexCoord, HexCoord> onStepStarted = null,
            System.Action<HexCoord, HexCoord> onStepCompleted = null,
            System.Func<HexCoord, HexCoord, StepResolutionOutcome, IEnumerator> resolveStepAsync = null,
            System.Action onSettled = null, bool settle = true, System.Action onCancelled = null)
        {
            if (operation == null || !operation.IsBusy || Data == null || !ReferenceEquals(Data.Movement, operation)) return;
            if (_presentedMovement?.IsBusy == true) return;
            if (map == null || resolveOffset == null || !isActiveAndEnabled) { operation.Cancel(); return; }
            if (_presentedMovement != null)
            {
                if (_movementTermination != null) _presentedMovement.Completed -= _movementTermination;
                // A Completed subscriber can replace a terminal operation before its old
                // presentation tail. Cancellation callbacks belong to that old order only.
                // A raw SettleThen reservation has no presented operation and keeps its hook.
                _onCancelled = null;
            }
            int epoch = ++_motionEpoch;
            _afterMovement = null;
            _presentedMovement = operation;
            System.Action<ArmyMovementResult> terminated = null;
            terminated = result =>
            {
                operation.Completed -= terminated;
                if (!ReferenceEquals(_presentedMovement, operation)) return;
                _movementTermination = null;
                if (result.Kind == ArmyMovementEnd.Cancelled || result.Kind == ArmyMovementEnd.Dead) CancelMovement();
            };
            _movementTermination = terminated;
            operation.Completed += terminated;
            _onSettled = onSettled;
            if (onCancelled != null) _onCancelled += _ => onCancelled();
            _movementMap = map; _movementOffset = resolveOffset;
            IsMoving = true;
            _layoutTransition = false;
            _arrivalLayoutPosition = null;
            var movement = StartCoroutine(PresentMovement(operation, map, resolveOffset, shouldStopEarly,
                onStepStarted, onStepCompleted, resolveStepAsync, settle));
            if (_motionEpoch == epoch && ReferenceEquals(_presentedMovement, operation)) _movement = movement;
        }
        private IEnumerator PresentMovement(ArmyMovementOperation operation, HexMap map,
            System.Func<HexCoord, Vector3> resolveOffset, System.Func<HexCoord, bool> shouldStopEarly,
            System.Action<HexCoord, HexCoord> onStepStarted, System.Action<HexCoord, HexCoord> onStepCompleted,
            System.Func<HexCoord, HexCoord, StepResolutionOutcome, IEnumerator> resolveStepAsync, bool settle)
        {
            try
            {
                if (settle && _selectionAnimation != null) yield return SettleRoutine(null, _motionEpoch);
                if (!operation.IsBusy || !ReferenceEquals(_presentedMovement, operation)) yield break;
                SetSelected(false);
                transform.localScale = _defaultScale;
                yield return operation.Run(hex => StepTo(map.HexToWorld(hex) + resolveOffset(hex), operation),
                    onStepStarted, onStepCompleted, resolveStepAsync, shouldStopEarly);
            }
            finally
            {
                if (operation.IsBusy) operation.Cancel();
                if (ReferenceEquals(_presentedMovement, operation))
                {
                    _presentedMovement = null;
                    CompleteMovement();
                }
            }
        }

        private IEnumerator StepTo(Vector3 targetPosition, ArmyMovementOperation operation)
        {
            Vector3 start = transform.position;
            float elapsed = 0f;
            while (operation.IsBusy && ReferenceEquals(_presentedMovement, operation) && elapsed < stepDuration)
            {
                elapsed += Time.deltaTime;
                transform.position = Vector3.Lerp(start, targetPosition, elapsed / stepDuration);
                yield return null;
            }
            if (operation.IsBusy && ReferenceEquals(_presentedMovement, operation)) transform.position = targetPosition;
        }
    }
}

