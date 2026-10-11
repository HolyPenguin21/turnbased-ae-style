using Game.Cards;
using Game.Core;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Game.UI
{
    // The small button on a unit/hero card (CardUI, ArmyUnitCardUI, BattleGridCellUI) that
    // discloses the CardType.Equipment card attached to that host. While the pointer is over it
    // OR the left button is held down on it, the card shows the equipment instead of the unit:
    //   - portrait swapped to the equipment's art
    //   - the host's own stat row hidden (statsToHideOnHover)
    //   - the name element replaced with the equipment's name (nameOverrideText)
    //   - the info element replaced with the equipment's added abilities + stat changes
    //     (infoText — see EquipmentCardText.EffectSummary)
    // Each overridden element's previous value is captured on the first change and restored
    // when neither hover nor press holds, so nameOverrideText / infoText / statsToHideOnHover
    // can point straight at the card's own existing elements — no dedicated overlay objects.
    // BattleGridCell has no ability-text element, so there only nameOverrideText is wired and
    // the name alone changes. The owning card also calls Revert() from its own OnPointerExit.
    //
    // The owning card drives this via Configure(equipmentCard, config) every time it (re)binds
    // a unit — that also shows/hides this button (hidden when nothing's attached).
    public class EquipmentArtToggle : MonoBehaviour, IPointerDownHandler, IPointerUpHandler,
        IPointerEnterHandler, IPointerExitHandler
    {
        [SerializeField] private Image cardArtImage;
        // The GameObject shown only while equipment is attached — leave empty to toggle this
        // component's own GameObject. Resolved every Configure (not cached in Awake) so it
        // works even if it starts inactive in the prefab.
        [SerializeField] private GameObject buttonVisual;
        // Legacy fallback for cards that have not wired individual stat texts yet.
        [SerializeField] private GameObject statsToHideOnHover;
        // Optional five-slot Equipment view. When wired (Card_Hand), attached Equipment keeps
        // the row visible and temporarily replaces host totals with the gear's own modifiers.
        [SerializeField] private TMP_Text attackStatText;
        [SerializeField] private TMP_Text defenseStatText;
        [SerializeField] private TMP_Text hpStatText;
        [SerializeField] private TMP_Text moveStatText;
        [SerializeField] private TMP_Text rangeStatText;
        // The card's name text — shows the equipment's name while shown, restored afterwards.
        [SerializeField] private TMP_Text nameOverrideText;
        // The card's ability/description text — shows the equipment's effect summary while
        // shown, restored afterwards. Leave empty where the card has no such element (the
        // battle grid cell): the name alone then carries the disclosure.
        [SerializeField] private TMP_Text infoText;

        // The existing component/GUID renders either slot; no parallel hover implementation.
        private EquipmentArtToggle _peer;
        public void SetPeer(EquipmentArtToggle peer) => _peer = peer;
        private void OnDisable() => Revert();

        private CardDefinition _equipment;
        private GameConfig _config;
        private CardType? _hostCardType;
        private bool _hovering;
        private bool _pressed;

        private bool _showing;
        private Sprite _savedArt;
        private bool _savedStatsActive;
        private readonly TextSwap _nameSwap = new TextSwap();
        private readonly TextSwap _infoSwap = new TextSwap();
        private readonly TextSwap _attackSwap = new TextSwap();
        private readonly TextSwap _defenseSwap = new TextSwap();
        private readonly TextSwap _hpSwap = new TextSwap();
        private readonly TextSwap _moveSwap = new TextSwap();
        private readonly TextSwap _rangeSwap = new TextSwap();

        public void Configure(CardDefinition equipment, GameConfig config, CardType? hostCardType = null)
        {
            RestoreNow();                 // undo anything still applied from a previous binding
            _equipment = equipment;
            _config = config;
            _hostCardType = hostCardType;
            _hovering = false;
            _pressed = false;

            GameObject target = buttonVisual != null ? buttonVisual : gameObject;
            // Shown whenever equipment is attached — the text overrides work with no art; only
            // the portrait swap needs equipment.art (and no-ops without it).
            target.SetActive(_equipment != null);
        }

        public void OnPointerEnter(PointerEventData eventData) { _hovering = true; Apply(); }
        public void OnPointerExit(PointerEventData eventData) { _hovering = false; _pressed = false; Apply(); }
        public void OnPointerDown(PointerEventData eventData) { _pressed = true; Apply(); }
        public void OnPointerUp(PointerEventData eventData) { _pressed = false; Apply(); }

        // Called by the owning card from its own OnPointerExit — force everything back.
        public void Revert()
        {
            _hovering = false;
            _pressed = false;
            Apply();
        }

        private void Apply()
        {
            if (_equipment != null && (_hovering || _pressed))
                ShowNow();
            else
                RestoreNow();
        }

        private void ShowNow()
        {
            if (_showing)
                return;
            _peer?.Revert();
            _showing = true;

            if (cardArtImage != null && _equipment.art != null)
            {
                _savedArt = cardArtImage.sprite;
                cardArtImage.sprite = _equipment.art;
            }
            bool hasEquipmentStatView = attackStatText != null || defenseStatText != null
                || hpStatText != null || moveStatText != null || rangeStatText != null;
            if (statsToHideOnHover != null)
            {
                _savedStatsActive = statsToHideOnHover.activeSelf;
                statsToHideOnHover.SetActive(hasEquipmentStatView || _savedStatsActive);
                if (!hasEquipmentStatView)
                    statsToHideOnHover.SetActive(false);
            }

            EquipmentGrant grant = _equipment.equipment;
            _attackSwap.Show(attackStatText, EquipmentCardText.StatBadgeValueForSlot(grant, 0, _hostCardType));
            _defenseSwap.Show(defenseStatText, EquipmentCardText.StatBadgeValueForSlot(grant, 1, _hostCardType));
            _hpSwap.Show(hpStatText, EquipmentCardText.StatBadgeValueForSlot(grant, 2, _hostCardType));
            _moveSwap.Show(moveStatText, EquipmentCardText.StatBadgeValueForSlot(grant, 3, _hostCardType));
            _rangeSwap.Show(rangeStatText, EquipmentCardText.StatBadgeValueForSlot(grant, 4, _hostCardType));
            _nameSwap.Show(nameOverrideText, _equipment.displayName);
            // Only the hand prefab has the new five-slot Equipment view. Keep legacy effect text
            // in Army/Battle contexts until those modals are migrated to the same template.
            _infoSwap.Show(infoText, hasEquipmentStatView
                ? EquipmentCardText.AttachedCardFace(_equipment, _config)
                : EquipmentCardText.EffectSummary(_equipment, _config));
        }

        private void RestoreNow()
        {
            if (!_showing)
                return;
            _showing = false;

            if (cardArtImage != null && _equipment?.art != null)
                cardArtImage.sprite = _savedArt;
            _attackSwap.Restore();
            _defenseSwap.Restore();
            _hpSwap.Restore();
            _moveSwap.Restore();
            _rangeSwap.Restore();
            if (statsToHideOnHover != null)
                statsToHideOnHover.SetActive(_savedStatsActive);
            _nameSwap.Restore();
            _infoSwap.Restore();
        }

        // Captures a TMP element's text + active state on Show and puts them back on Restore —
        // so a swap target can be the card's own existing name/description text.
        private sealed class TextSwap
        {
            private TMP_Text _target;
            private string _savedText;
            private bool _savedActive;
            private bool _active;

            public void Show(TMP_Text target, string value)
            {
                if (target == null || _active)
                    return;
                _target = target;
                _savedText = target.text;
                _savedActive = target.gameObject.activeSelf;
                _active = true;
                target.text = value;
                target.gameObject.SetActive(true);
            }

            public void Restore()
            {
                if (!_active || _target == null)
                    return;
                _active = false;
                _target.text = _savedText;
                _target.gameObject.SetActive(_savedActive);
            }
        }
    }
}
