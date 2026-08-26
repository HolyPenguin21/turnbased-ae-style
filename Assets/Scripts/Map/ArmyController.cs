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
                transform.position = map.HexToWorld(Data.Hex) + iconOffset;
            transform.localScale = _defaultScale;
        }

        // Eases the current pulse scale back down to the resting scale over settleDuration,
        // THEN invokes onSettled — instead of an abrupt snap. Used right before a move starts.
        // Also claims IsMoving right away (not just once the move animation itself starts) so a
        // second order can't sneak in while this one is still easing out.
        public void SettleThen(System.Action onSettled)
        {
            if (IsMoving)
                return;
            IsMoving = true;

            if (_selectionAnimation == null)
            {
                onSettled?.Invoke();
                return;
            }
            StartCoroutine(SettleRoutine(onSettled));
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
            onSettled?.Invoke();
        }

        // path[0] is this army's current hex (not entered — no cost, already there); each later
        // entry is walked to in turn, in world-space order, as long as the army's shared move
        // budget can afford that hex's full cost. Stops short the moment the next hex would cost
        // more than what's left — never enters a hex it can't fully pay for. resolveOffset is
        // called fresh for every hex entered
        // (including the starting one, via ResetTransform below) since a hex's correct icon
        // offset depends on what else is on it (see HexObjectLayout), not just at the final
        // destination. IsMoving is deliberately NOT checked/set here any more — SettleThen
        // already claims it the moment a move order is committed, so a caller going through that
        // gate first is what makes re-entrancy safe.
        public void MoveAlong(HexMap map, List<HexCoord> path, System.Func<HexCoord, Vector3> resolveOffset,
            System.Action onComplete = null, System.Func<HexCoord, bool> shouldStopEarly = null,
            System.Action<HexCoord, HexCoord> onStepStarted = null,
            System.Action<HexCoord, HexCoord> onStepCompleted = null)
        {
            if (map == null || path == null || path.Count < 2 || resolveOffset == null || Data == null || Data.Members.Count == 0)
            {
                IsMoving = false; // release SettleThen's claim — there's no MoveRoutine coming to do it
                return;
            }

            _currentHex = Data.Hex;
            ResetTransform(map, resolveOffset(Data.Hex));
            StartCoroutine(MoveRoutine(map, path, resolveOffset, onComplete, shouldStopEarly,
                onStepStarted, onStepCompleted));
        }

        // shouldStopEarly is called once per hex actually entered (never the origin), AFTER this
        // army has visually landed there and _currentHex/vision have been updated for it — same
        // "stop short" idea as running out of shared move points below, just driven by the
        // caller instead (see HexSelectionController.Movement.cs's own reveal-on-entry check:
        // fog hides what a hex holds until the mover is actually standing on it, so a path
        // computed from the fogged-out start can't already know to stop there on its own).
        private IEnumerator MoveRoutine(HexMap map, List<HexCoord> path, System.Func<HexCoord, Vector3> resolveOffset,
            System.Action onComplete, System.Func<HexCoord, bool> shouldStopEarly,
            System.Action<HexCoord, HexCoord> onStepStarted,
            System.Action<HexCoord, HexCoord> onStepCompleted)
        {
            List<UnitData> members = Data.Members;
            for (int i = 1; i < path.Count; i++)
            {
                HexCoord next = path[i];
                map.TryGetTerrainAt(next, out TerrainTypeEntry entry);
                int terrainCost = entry != null ? Mathf.Max(1, entry.moveCost) : 1;
                int cost = AviationRules.MovementCost(Data, terrainCost);

                int sharedMoveCurrent = AviationRules.EffectiveMoveCurrent(members[0]);
                for (int m = 1; m < members.Count; m++)
                    if (AviationRules.EffectiveMoveCurrent(members[m]) < sharedMoveCurrent)
                        sharedMoveCurrent = AviationRules.EffectiveMoveCurrent(members[m]);
                if (sharedMoveCurrent < cost)
                    break;

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

                Vector3 targetPosition = map.HexToWorld(next) + resolveOffset(next);
                yield return StepTo(targetPosition);
                onStepCompleted?.Invoke(previous, next);

                if (shouldStopEarly != null && shouldStopEarly(next))
                    break;
            }

            // onComplete (see HexSelectionController.TryIssueMoveOrder) reads CurrentHex to find
            // out where the army actually ended up — that property falls back to the stale
            // Data.Hex once IsMoving is false, so IsMoving must still read true for the whole
            // duration of this call. Nothing else runs between here and onComplete returning
            // (no yield in between), so deferring the flip costs nothing.
            onComplete?.Invoke();
            IsMoving = false;
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
