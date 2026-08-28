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
        // The card's own stat row/badges — hidden while the equipment is being shown.
        [SerializeField] private GameObject statsToHideOnHover;
        // The card's name text — shows the equipment's name while shown, restored afterwards.
        [SerializeField] private TMP_Text nameOverrideText;
        // The card's ability/description text — shows the equipment's effect summary while
        // shown, restored afterwards. Leave empty where the card has no such element (the
        // battle grid cell): the name alone then carries the disclosure.
        [SerializeField] private TMP_Text infoText;

        private CardDefinition _equipment;
        private GameConfig _config;
        private bool _hovering;
        private bool _pressed;

        private bool _showing;
        private Sprite _savedArt;
        private bool _savedStatsActive;
        private readonly TextSwap _nameSwap = new TextSwap();
        private readonly TextSwap _infoSwap = new TextSwap();

        public void Configure(CardDefinition equipment, GameConfig config)
        {
            RestoreNow();                 // undo anything still applied from a previous binding
            _equipment = equipment;
            _config = config;
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
            _showing = true;

            if (cardArtImage != null && _equipment.art != null)
            {
                _savedArt = cardArtImage.sprite;
                cardArtImage.sprite = _equipment.art;
            }
            if (statsToHideOnHover != null)
            {
                _savedStatsActive = statsToHideOnHover.activeSelf;
                statsToHideOnHover.SetActive(false);
            }
            _nameSwap.Show(nameOverrideText, _equipment.displayName);
            _infoSwap.Show(infoText, EquipmentCardText.EffectSummary(_equipment, _config));
        }

        private void RestoreNow()
        {
            if (!_showing)
                return;
            _showing = false;

            if (cardArtImage != null && _savedArt != null)
                cardArtImage.sprite = _savedArt;
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
