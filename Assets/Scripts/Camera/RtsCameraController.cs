using System;
using System.Collections;
using Game.UI;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Game.Cameras
{
    // Fixed-angle RTS-style orthographic camera. Pans along the ground plane (WASD/arrows,
    // plus optional edge-of-screen scrolling); zooms via Camera.orthographicSize (scroll
    // wheel) instead of physically moving closer/further — in orthographic projection, camera
    // distance alone doesn't change apparent size, only orthographicSize does. The camera
    // still sits a fixed `distance` back from its ground target along its own look direction
    // (whatever rotation it has when the scene starts becomes that fixed angle), purely to
    // stay within the near/far clip planes and at a sensible physical position — that value
    // never changes at runtime, unlike the old perspective dolly-zoom.
    public class RtsCameraController : MonoBehaviour
    {
        [Header("Pan")]
        [SerializeField] private float panSpeed = 20f;
        [SerializeField] private bool enableEdgeScroll = true;
        [SerializeField] private float edgeScrollBorder = 12f;
        [SerializeField] private bool enableMiddleMouseDrag = true;

        [Header("Zoom (orthographic size)")]
        [SerializeField] private float zoomSpeed = 20f;
        [SerializeField] private float minOrthoSize = 2.6f;
        [SerializeField] private float maxOrthoSize = 7f;
        [SerializeField] private float orthoSize = 4f;

        [Header("Camera Distance (fixed — clip planes/positioning only, not zoom)")]
        [SerializeField] private float distance = 7f;

        [Header("Bounds (optional)")]
        [SerializeField] private bool clampToBounds = false;
        [SerializeField] private Vector2 boundsMin = new Vector2(-30f, -30f);
        [SerializeField] private Vector2 boundsMax = new Vector2(30f, 30f);

        private Camera _camera;
        private Vector3 _groundTarget;
        private Vector3 _forward;
        private Coroutine _panRoutine;
        private bool _panningEnabled = true;
        private bool _isDragging;
        // The ground-plane point that was under the cursor when the middle-mouse drag started —
        // held fixed for the whole drag (not refreshed every frame) so the same point tracks the
        // cursor 1:1 the entire time, instead of drifting the way a per-frame mouse-delta approach
        // would once the camera itself has moved.
        private Vector3 _dragAnchorGround;

        // Turned off for the duration of the Tactical Battle Module (see BattleScreenUI.Show/
        // Hide) — the battle grid has its own fixed camera, dragging the strategic map's view
        // around underneath it makes no sense while it's up. PanTo (a programmatic glide, not
        // player-driven) still works regardless — this only gates the WASD/edge-scroll input.
        public void SetPanningEnabled(bool enabled) => _panningEnabled = enabled;

        private void Start()
        {
            _camera = GetComponent<Camera>();
            if (_camera != null)
            {
                _camera.orthographic = true;
                _camera.orthographicSize = orthoSize;
            }

            _forward = transform.forward;
            // Recover the ground point this camera is currently framing, from wherever it
            // was placed in the editor, so panning/zooming starts from that same spot.
            _groundTarget = transform.position - _forward * distance;
            ApplyPosition();
        }

        // Smoothly glides the camera to look at worldPosition instead of jumping there —
        // e.g. moving to a player's citadel once it's placed. Manual pan input is ignored
        // while the glide is in progress so it can't fight with it. onComplete (optional)
        // fires once the glide actually finishes — e.g. to only show a popup once the camera
        // has arrived, instead of at the same time it starts moving.
        public void PanTo(Vector3 worldPosition, float duration = 1.5f, Action onComplete = null)
        {
            if (_panRoutine != null)
                StopCoroutine(_panRoutine);
            _panRoutine = StartCoroutine(PanRoutine(worldPosition, duration, onComplete));
        }

        private IEnumerator PanRoutine(Vector3 worldPosition, float duration, Action onComplete)
        {
            Vector3 start = _groundTarget;
            Vector3 target = new Vector3(worldPosition.x, 0f, worldPosition.z);
            float elapsed = 0f;

            while (elapsed < duration)
            {
                elapsed += Time.deltaTime;
                float t = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(elapsed / duration));
                _groundTarget = Vector3.Lerp(start, target, t);
                ApplyPosition();
                yield return null;
            }

            _groundTarget = target;
            ApplyPosition();
            _panRoutine = null;
            onComplete?.Invoke();
        }

        private void Update()
        {
            if (_panRoutine != null)
                return;

            Vector3 forwardFlat = new Vector3(_forward.x, 0f, _forward.z).normalized;
            Vector3 rightFlat = new Vector3(forwardFlat.z, 0f, -forwardFlat.x);
            Vector3 panDelta = Vector3.zero;

            // WASD/arrows and edge-scroll read straight off the Keyboard/Mouse singletons, not
            // through the UI event system — a focused text field (e.g. RenameArmyPopupUI's
            // name field) doesn't stop them on its own, so the camera would otherwise pan out
            // from under the player while they're just trying to type an army's new name.
            // UIFocusUtility.IsTextFieldFocused()'s EventSystem/GetComponent lookup isn't free
            // (same reasoning as GameTurnController's own Enter-key check), so it's only run
            // below once there's an actual pan to potentially cancel — not on every one of the
            // many frames nothing is pressing a pan key or hovering an edge at all.
            Keyboard kb = Keyboard.current;
            if (kb != null && _panningEnabled)
            {
                if (kb.wKey.isPressed || kb.upArrowKey.isPressed) panDelta += forwardFlat;
                if (kb.sKey.isPressed || kb.downArrowKey.isPressed) panDelta -= forwardFlat;
                if (kb.dKey.isPressed || kb.rightArrowKey.isPressed) panDelta += rightFlat;
                if (kb.aKey.isPressed || kb.leftArrowKey.isPressed) panDelta -= rightFlat;
            }

            Mouse mouse = Mouse.current;
            if (enableEdgeScroll && mouse != null && _panningEnabled)
            {
                Vector2 mousePos = mouse.position.ReadValue();
                if (mousePos.x <= edgeScrollBorder) panDelta -= rightFlat;
                else if (mousePos.x >= Screen.width - edgeScrollBorder) panDelta += rightFlat;
                if (mousePos.y <= edgeScrollBorder) panDelta -= forwardFlat;
                else if (mousePos.y >= Screen.height - edgeScrollBorder) panDelta += forwardFlat;
            }

            // Middle-mouse drag: grab-and-pull, same convention as most DCC/RTS tools — whatever
            // ground point was under the cursor when the button went down stays glued under the
            // cursor for the rest of the drag. Applied straight to _groundTarget (not through
            // panDelta/panSpeed like WASD/edge-scroll) since it's a direct 1:1 mapping, not a
            // speed-based nudge.
            if (enableMiddleMouseDrag && mouse != null && _panningEnabled)
            {
                if (mouse.middleButton.wasPressedThisFrame && TryGetGroundPoint(mouse.position.ReadValue(), out Vector3 pressGround))
                {
                    _isDragging = true;
                    _dragAnchorGround = pressGround;
                }
                else if (mouse.middleButton.wasReleasedThisFrame)
                {
                    _isDragging = false;
                }

                if (_isDragging && mouse.middleButton.isPressed
                    && TryGetGroundPoint(mouse.position.ReadValue(), out Vector3 currentGround))
                {
                    _groundTarget += _dragAnchorGround - currentGround;
                }
            }
            else
            {
                _isDragging = false;
            }

            if (panDelta.sqrMagnitude > 0f && UIFocusUtility.IsTextFieldFocused())
                panDelta = Vector3.zero;

            if (panDelta.sqrMagnitude > 0f)
                _groundTarget += panDelta.normalized * panSpeed * Time.deltaTime;

            if (clampToBounds)
            {
                _groundTarget.x = Mathf.Clamp(_groundTarget.x, boundsMin.x, boundsMax.x);
                _groundTarget.z = Mathf.Clamp(_groundTarget.z, boundsMin.y, boundsMax.y);
            }

            if (mouse != null)
            {
                float scroll = mouse.scroll.ReadValue().y;
                if (Mathf.Abs(scroll) > 0.01f)
                {
                    orthoSize = Mathf.Clamp(orthoSize - scroll * zoomSpeed * 0.01f, minOrthoSize, maxOrthoSize);
                    if (_camera != null)
                        _camera.orthographicSize = orthoSize;
                }
            }

            ApplyPosition();
        }

        private void ApplyPosition()
        {
            transform.position = _groundTarget - _forward * distance;
        }

        // Where a screen point lands on the world's ground plane (y = 0 — same plane PanTo's
        // own worldPosition.y drop already assumes), from wherever the camera currently sits.
        // False on a screen point whose ray is parallel to that plane (e.g. genuinely edge-on),
        // which can't happen at this camera's fixed downward angle but is checked anyway since
        // Plane.Raycast itself can fail.
        private bool TryGetGroundPoint(Vector2 screenPos, out Vector3 groundPoint)
        {
            groundPoint = Vector3.zero;
            if (_camera == null)
                return false;
            Ray ray = _camera.ScreenPointToRay(screenPos);
            var groundPlane = new Plane(Vector3.up, Vector3.zero);
            if (!groundPlane.Raycast(ray, out float enter))
                return false;
            groundPoint = ray.GetPoint(enter);
            return true;
        }
    }
}
