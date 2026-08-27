using Game.Cards;
using Game.Core;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Game.UI
{
    // The small button on a unit/hero card (CardUI, ArmyUnitCardUI, BattleGridCellUI) that
    // discloses the CardType.Equipment card attached to that host. While the pointer is over
    // it OR the left button is held down on it, the card shows the equipment instead of the
    // unit: portrait swapped to the equipment's art, the host's stat row hidden, and a text
    // panel with the equipment's name, added abilities, then stat changes (see
    // EquipmentCardText.HoverInfo). Everything reverts when neither condition holds — and the
    // owning card also calls Revert() from its own OnPointerExit, per the spec.
    //
    // The owning card drives this: Configure(unitArt, equipmentCard, config) every time it
    // (re)binds a unit — that also shows/hides this button (hidden when nothing's attached).
    public class EquipmentArtToggle : MonoBehaviour, IPointerDownHandler, IPointerUpHandler,
        IPointerEnterHandler, IPointerExitHandler
    {
        [SerializeField] private Image cardArtImage;
        // The GameObject shown only while equipment is attached — leave empty to toggle this
        // component's own GameObject. Resolved every Configure (not cached in Awake) so it
        // works even if it starts inactive in the prefab.
        [SerializeField] private GameObject buttonVisual;
        // The host card's own stat row/badges — hidden while the equipment is being shown.
        [SerializeField] private GameObject statsToHideOnHover;
        // Where the equipment's name/abilities/stats are written. Normally starts inactive/
        // empty; shown only while the equipment is being shown.
        [SerializeField] private TMP_Text infoText;

        private Sprite _unitArt;
        private CardDefinition _equipment;
        private GameConfig _config;
        private bool _statsWereActive = true;
        private bool _hovering;
        private bool _pressed;

        public void Configure(Sprite unitArt, CardDefinition equipment, GameConfig config)
        {
            _unitArt = unitArt;
            _equipment = equipment;
            _config = config;
            _hovering = false;
            _pressed = false;
            _statsWereActive = statsToHideOnHover == null || statsToHideOnHover.activeSelf;
            ApplyState();
            GameObject target = buttonVisual != null ? buttonVisual : gameObject;
            // Shown whenever equipment is attached — the info text works with no art; only the
            // portrait swap needs equipment.art (and no-ops without it).
            target.SetActive(_equipment != null);
        }

        public void OnPointerEnter(PointerEventData eventData)
        {
            _hovering = true;
            ApplyState();
        }

        public void OnPointerExit(PointerEventData eventData)
        {
            _hovering = false;
            _pressed = false;
            ApplyState();
        }

        public void OnPointerDown(PointerEventData eventData)
        {
            _pressed = true;
            ApplyState();
        }

        public void OnPointerUp(PointerEventData eventData)
        {
            _pressed = false;
            ApplyState();
        }

        // Called by the owning card from its own OnPointerExit — force everything back.
        public void Revert()
        {
            _hovering = false;
            _pressed = false;
            ApplyState();
        }

        private void ApplyState()
        {
            bool show = _equipment != null && (_hovering || _pressed);

            if (cardArtImage != null)
            {
                Sprite target = show && _equipment.art != null ? _equipment.art : _unitArt;
                if (target != null)
                    cardArtImage.sprite = target;
            }
            if (statsToHideOnHover != null)
                statsToHideOnHover.SetActive(show ? false : _statsWereActive);
            if (infoText != null)
            {
                if (show)
                    infoText.text = EquipmentCardText.HoverInfo(_equipment, _config);
                infoText.gameObject.SetActive(show);
            }
        }
    }
}
