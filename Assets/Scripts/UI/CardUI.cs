using System.Collections;
using Game.Cards;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Game.UI
{
    // One card's visual + interaction inside CardHandUI's hand: shows its art, grows to full
    // size and lifts on hover, and can be picked up with LMB and dragged — CardHandUI reorders
    // the rest of the hand LIVE as it's dragged (see PreviewDrag), not just on release, so
    // the hand visibly makes room for it while it's still being held.
    public class CardUI : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler,
        IBeginDragHandler, IDragHandler, IEndDragHandler, IPointerClickHandler
    {
        [SerializeField] private RectTransform rectTransform;
        [SerializeField] private Image artImage;
        [SerializeField] private TMP_Text nameText;
        [SerializeField] private TMP_Text typeText;

        // Five compact stat badges, shown for Unit/Hero/Base cards (hidden entirely for
        // Facility — see Setup) — each slot is repurposed per CardType rather than having its
        // own dedicated field per stat, since only 5 numbers are ever shown at once and which
        // stat occupies which slot is a deliberate, fixed mapping (see the user's own spec):
        //   AttackBadge slot: Attack (Unit) / Command Rating (Hero) / Level (Base)
        //   DefenseBadge slot: Defense (Unit) / Fate (Hero) / Defense (Base)
        //   HpBadge slot: HP, current/max (all three — Base uses Structure Points as its HP)
        //   MoveBadge slot: Move (Unit) / Move (Hero) / Resistance (Base)
        //   RangeBadge slot: Range (Unit) / Initiative (Hero) / Fate (Base)
        // A card still sitting in hand hasn't been spawned yet, so "current" HP/Structure and
        // Base Level are shown as their eventual starting values (max/max, Level 1) rather than
        // tracking anything real — see ArmyUnitCardUI/BaseSlotCardUI for the versions of this
        // that show the REAL current values once something's actually on the map.
        [SerializeField] private GameObject statsRow;
        [SerializeField] private TMP_Text attackStatText;
        [SerializeField] private TMP_Text defenseStatText;
        [SerializeField] private TMP_Text hpStatText;
        [SerializeField] private TMP_Text moveStatText;
        [SerializeField] private TMP_Text rangeStatText;

        // Cost badges — one per currency (AP + the four stockpiled resources), each hidden
        // entirely when that currency's cost is 0 rather than shown as "0" (see Setup). Same
        // colours as ResourceBarUI's HUD icons (see GameObject "ResourceBar" in Game.unity) so
        // a card's cost reads as the same currency at a glance.
        [SerializeField] private GameObject apBadgeRoot;
        [SerializeField] private TMP_Text apBadgeText;
        [SerializeField] private GameObject humanBadgeRoot;
        [SerializeField] private TMP_Text humanBadgeText;
        [SerializeField] private GameObject energyBadgeRoot;
        [SerializeField] private TMP_Text energyBadgeText;
        [SerializeField] private GameObject materialsBadgeRoot;
        [SerializeField] private TMP_Text materialsBadgeText;
        [SerializeField] private GameObject techBadgeRoot;
        [SerializeField] private TMP_Text techBadgeText;

        // Handed down by CardHandUI at spawn time (see Setup) rather than read from a
        // GameConfig reference on every single card, or kept as this component's own
        // [SerializeField] — the hand is the one place these are tuned, all together.
        private float _restingScale = 0.9f;
        private float _hoverScale = 1f;
        private float _hoverLift;
        private float _animDuration = 0.12f;
        // How much smaller the card gets while poised over a hex it could actually be dropped
        // on while dragging (see SetDragHoverValid) — a fraction of DragScale, not resting/hover
        // scale, e.g. 0.5 means 50% of full carried size.
        private float _dragHoverShrink = 0.5f;

        public CardData Data { get; private set; }
        public bool IsDragging { get; private set; }

        private CardHandUI _hand;
        private Vector2 _homePosition;
        private bool _isHovered;
        private bool _dragAllowed;
        private bool _dragHoverValid;
        private Coroutine _animRoutine;
        private Canvas _canvas;

        // Scale while being carried — 1 (full/native size) normally, shrunk by
        // dragHoverShrink while poised over a hex this card could actually be dropped on (see
        // SetDragHoverValid).
        private const float DragScale = 1f;

        private void Awake()
        {
            if (rectTransform == null)
                rectTransform = (RectTransform)transform;
            _canvas = GetComponentInParent<Canvas>();
        }

        public void Setup(CardHandUI hand, CardData data, float restingScale, float hoverScale, float hoverLift, float animDuration, float dragHoverShrink)
        {
            _hand = hand;
            Data = data;
            _restingScale = restingScale;
            _hoverScale = hoverScale;
            _hoverLift = hoverLift;
            _animDuration = animDuration;
            _dragHoverShrink = dragHoverShrink;

            CardDefinition definition = data.Definition;
            if (artImage != null && definition != null)
                artImage.sprite = definition.art;
            if (nameText != null)
                nameText.text = definition != null ? definition.displayName : string.Empty;
            if (typeText != null)
            {
                // Abilities only — no card type prefix (matches ArmyUnitCardUI's own SkillsText
                // slot). Abbreviated per GameConfig.abilityAbbreviations (see
                // GameConfig.FormatAbilities) — falls back to the raw tag name for anything not
                // listed there.
                string formatted = definition != null ? _hand?.GameConfig?.FormatAbilities(definition.grantedAbilities) : null;
                typeText.text = formatted ?? string.Empty;
            }

            RefreshStatsRow(definition);

            ResourceCost cost = definition?.resourceCost;
            SetBadge(apBadgeRoot, apBadgeText, definition != null ? definition.apCost : 0);
            SetBadge(humanBadgeRoot, humanBadgeText, cost != null ? cost.human : 0);
            SetBadge(energyBadgeRoot, energyBadgeText, cost != null ? cost.energy : 0);
            SetBadge(materialsBadgeRoot, materialsBadgeText, cost != null ? cost.materials : 0);
            SetBadge(techBadgeRoot, techBadgeText, cost != null ? cost.tech : 0);

            rectTransform.localScale = Vector3.one * _restingScale;
        }

        // See the field block's own comment for the fixed per-slot stat mapping. Facility and
        // Tactic cards have no unit/hero/building identity of their own to show stats for, so
        // the whole row is hidden for them rather than showing 5 zeroes.
        private void RefreshStatsRow(CardDefinition definition)
        {
            if (statsRow == null)
                return;

            bool show = definition != null && definition.cardType != CardType.Facility && definition.cardType != CardType.Tactic;
            statsRow.SetActive(show);
            if (!show)
                return;

            int slot1, slot2, hp, slot4, slot5;
            switch (definition.cardType)
            {
                case CardType.Hero:
                    slot1 = definition.commandRating;
                    slot2 = definition.fate;
                    hp = definition.hitPoints;
                    slot4 = definition.moveMax;
                    slot5 = definition.initiative;
                    break;
                case CardType.Base:
                    slot1 = 1; // Level — always 1 for a not-yet-built card, see the field block's own comment
                    slot2 = definition.defenseRating;
                    hp = definition.hitPoints;
                    slot4 = definition.resistanceRating;
                    slot5 = definition.fate;
                    break;
                default: // Unit
                    slot1 = definition.attack;
                    slot2 = definition.defenseRating;
                    hp = definition.hitPoints;
                    slot4 = definition.moveMax;
                    slot5 = definition.range;
                    break;
            }

            if (attackStatText != null) attackStatText.text = slot1.ToString();
            if (defenseStatText != null) defenseStatText.text = slot2.ToString();
            if (hpStatText != null) hpStatText.text = $"{hp}/{hp}";
            if (moveStatText != null) moveStatText.text = slot4.ToString();
            if (rangeStatText != null) rangeStatText.text = slot5.ToString();
        }

        // Hides the badge entirely for a 0 cost rather than showing "0" — most cards don't
        // touch most of the four resources, and a row of zeroes would just be noise.
        private static void SetBadge(GameObject root, TMP_Text text, int amount)
        {
            if (root == null)
                return;
            bool visible = amount > 0;
            root.SetActive(visible);
            if (visible && text != null)
                text.text = amount.ToString();
        }

        // Called by CardHandUI whenever the hand relayouts (card added/removed/reordered) —
        // this card's slot position within the panel, not counting the hover lift, which is
        // applied on top of it locally in Retarget. Skips restarting the animation entirely
        // if the slot didn't actually move — during a drag, PreviewDrag relayouts the WHOLE
        // hand on every index change, and most cards in it keep the exact same slot, so this
        // is what stops every one of them from restarting a (visually pointless) coroutine on
        // every single reorder tick.
        public void SetHome(Vector2 anchoredPosition, bool animated)
        {
            bool changed = (anchoredPosition - _homePosition).sqrMagnitude > 0.01f;
            _homePosition = anchoredPosition;
            if (!IsDragging && changed)
                Retarget(animated);
        }

        public void OnPointerEnter(PointerEventData eventData)
        {
            if (IsDragging)
                return;
            _isHovered = true;
            rectTransform.SetAsLastSibling();
            Retarget(animated: true);
        }

        public void OnPointerExit(PointerEventData eventData)
        {
            if (IsDragging)
                return;
            _isHovered = false;
            Retarget(animated: true);
            _hand.RestoreSiblingOrder();
        }

        // Right/left click routing for equipment attach mode (see CardHandUI, and the project
        // owner's own spec: right-click an Equipment card to start attaching, left-click a
        // Unit/Hero card to be its host, right-click again anywhere to cancel). A plain click
        // that isn't part of a drag; deploying a card is still the drag gesture, untouched.
        public void OnPointerClick(PointerEventData eventData)
        {
            if (IsDragging || _hand == null || Data?.Definition == null)
                return;

            if (eventData.button == PointerEventData.InputButton.Right)
            {
                if (_hand.IsAttachMode)
                    _hand.CancelAttachMode();
                else if (Data.Definition.cardType == CardType.Equipment)
                    _hand.BeginAttachMode(Data);
                return;
            }

            if (eventData.button == PointerEventData.InputButton.Left && _hand.IsAttachMode)
                _hand.TryAttachToHandCard(Data);
        }

        // Hover and hand-scrolling always work regardless of whose turn it is (see
        // CardHandUI.CanDragCards) — only actually picking a card up is gated, so it's checked
        // here, once, for the whole drag gesture, rather than letting the card start following
        // the mouse and only refusing later. _dragAllowed is what OnDrag/OnEndDrag check, since
        // Unity keeps calling them for the rest of the gesture regardless of what happens here.
        // How far above everything else a card renders while being dragged — high enough to
        // clear the Army Viewer modal (see DragSortingOrder below), which otherwise draws over
        // it: the modal is just plain hierarchy under Canvas_UI, ordered after the hand in
        // sibling order, and SetAsLastSibling() only reorders within the hand's own parent, not
        // relative to Canvas_UI's other top-level children.
        private const int DragSortingOrder = 1000;

        public void OnBeginDrag(PointerEventData eventData)
        {
            _dragAllowed = _hand != null && _hand.CanDragCards();
            if (!_dragAllowed)
                return;

            IsDragging = true;
            _isHovered = false;
            _dragHoverValid = false;
            rectTransform.SetAsLastSibling();
            // The prefab's own nested Canvas (normally just inherits the parent's batch) breaks
            // out into an independent, explicitly-ordered one for the duration of the drag —
            // see DragSortingOrder.
            if (_canvas != null)
            {
                _canvas.overrideSorting = true;
                _canvas.sortingOrder = DragSortingOrder;
            }
            // Eases from whatever scale it was at (hover or resting) to full carried size,
            // instead of snapping there instantly — the hard cut used to read as a jarring
            // shrink right as the pick-up happened.
            AnimateScaleTo(DragScale, _animDuration);
        }

        public void OnDrag(PointerEventData eventData)
        {
            if (!_dragAllowed)
                return;

            UIDragUtility.ApplyScreenDelta(rectTransform, eventData, _canvas);
            // Live reorder preview — the rest of the hand shifts to make room right now,
            // instead of only snapping into place once the card is dropped. CardHandUI itself
            // decides whether this position still counts as "over the hand" (see
            // PreviewDrag's drag-band check) — e.g. once the card is lifted well clear of the
            // hand row (on its way to being played), the rest of the hand should stop reacting.
            // eventData.position (screen space) is also what lets CardHandUI figure out which
            // hex (if any) is under the card right now, for the drop-preview shrink below.
            _hand.PreviewDrag(this, rectTransform.anchoredPosition, eventData.position);
        }

        // Called by CardHandUI while dragging, once per hex actually changed under the cursor
        // (not every frame) — shrinks the card while it's poised over a hex this card could
        // really be dropped on, back to full size anywhere else (including within the hand
        // itself, or over an invalid hex). No-ops outside of a drag or if nothing changed.
        public void SetDragHoverValid(bool valid)
        {
            if (_dragHoverValid == valid)
                return;
            _dragHoverValid = valid;
            if (!IsDragging)
                return;
            AnimateScaleTo(valid ? DragScale * (1f - _dragHoverShrink) : DragScale, _animDuration);
        }

        public void OnEndDrag(PointerEventData eventData)
        {
            if (!_dragAllowed)
                return;

            IsDragging = false;
            if (_canvas != null)
                _canvas.overrideSorting = false;
            // The hand's own order already matches what PreviewDrag showed while dragging —
            // this just lets the card itself snap into that already-previewed slot (or, if it
            // was dragged clear of the hand, attempts to play it onto the map hex under
            // eventData.position — see CardHandUI.FinishDrop/TryPlayCard).
            _hand.FinishDrop(this, eventData.position);
        }

        // Called once by CardHandUI right after a drag ends. SetHome keeps updating
        // _homePosition even while dragging (just without visually moving the card, since
        // Retarget is suppressed by IsDragging) — so by the time the drag ends, the home
        // position CardHandUI's Relayout hands back is usually already equal to what's
        // cached here, and SetHome's "did it actually change" guard skips Retarget entirely.
        // This forces the snap unconditionally instead of relying on that guard.
        public void SnapToHome()
        {
            Retarget(animated: false);
        }

        private void Retarget(bool animated)
        {
            Vector2 targetPos = _homePosition + (_isHovered ? new Vector2(0f, _hoverLift) : Vector2.zero);
            float targetScale = _isHovered ? _hoverScale : _restingScale;

            if (_animRoutine != null)
                StopCoroutine(_animRoutine);
            _animRoutine = StartCoroutine(AnimateTo(targetPos, targetScale, animated ? _animDuration : 0f));
        }

        // Scale-only counterpart to Retarget/AnimateTo — used while dragging, when position is
        // being driven every frame by OnDrag's own delta accumulation instead of by this
        // component. Animating position here too (even toward its own current value, via the
        // position+scale AnimateTo below) would fight that: this coroutine's per-frame Lerp
        // would overwrite whatever OnDrag just set that same frame. Scale is independent of
        // position, so it can safely share the same _animRoutine slot as Retarget without any
        // of that risk.
        private void AnimateScaleTo(float targetScale, float duration)
        {
            if (_animRoutine != null)
                StopCoroutine(_animRoutine);
            _animRoutine = StartCoroutine(ScaleRoutine(targetScale, duration));
        }

        private IEnumerator ScaleRoutine(float targetScale, float duration)
        {
            float startScale = rectTransform.localScale.x;
            if (duration <= 0f)
            {
                rectTransform.localScale = Vector3.one * targetScale;
                yield break;
            }

            float t = 0f;
            while (t < duration)
            {
                t += Time.deltaTime;
                float f = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(t / duration));
                rectTransform.localScale = Vector3.one * Mathf.Lerp(startScale, targetScale, f);
                yield return null;
            }
            rectTransform.localScale = Vector3.one * targetScale;
        }

        private IEnumerator AnimateTo(Vector2 targetPos, float targetScale, float duration)
        {
            Vector2 startPos = rectTransform.anchoredPosition;
            float startScale = rectTransform.localScale.x;
            if (duration <= 0f)
            {
                rectTransform.anchoredPosition = targetPos;
                rectTransform.localScale = Vector3.one * targetScale;
                yield break;
            }

            float t = 0f;
            while (t < duration)
            {
                t += Time.deltaTime;
                float f = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(t / duration));
                rectTransform.anchoredPosition = Vector2.Lerp(startPos, targetPos, f);
                rectTransform.localScale = Vector3.one * Mathf.Lerp(startScale, targetScale, f);
                yield return null;
            }
            rectTransform.anchoredPosition = targetPos;
            rectTransform.localScale = Vector3.one * targetScale;
        }
    }
}
