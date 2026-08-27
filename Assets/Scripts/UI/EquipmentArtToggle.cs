using Game.Cards;
using Game.Core;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Game.UI
{
    // The small button on a unit/hero card (CardUI, ArmyUnitCardUI, BattleGridCellUI) that
    // discloses the CardType.Equipment card attached to that host:
    //   - hover  -> hide the host's own stat row, show a text panel with the equipment's name,
    //               added abilities, then stat changes (see EquipmentCardText.HoverInfo).
    //   - press-and-hold -> swap the card portrait to the equipment's art.
    // Everything reverts on release / when the pointer leaves the button (the owning card also
    // calls Revert() from its own OnPointerExit, per the spec).
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
        // The host card's own stat row/badges — hidden while hovering this button.
        [SerializeField] private GameObject statsToHideOnHover;
        // Where the equipment's name/abilities/stats are written on hover. Normally starts
        // inactive/empty; shown only during hover.
        [SerializeField] private TMP_Text infoText;

        private Sprite _unitArt;
        private CardDefinition _equipment;
        private GameConfig _config;
        private bool _statsWereActive = true;

        public void Configure(Sprite unitArt, CardDefinition equipment, GameConfig config)
        {
            _unitArt = unitArt;
            _equipment = equipment;
            _config = config;
            _statsWereActive = statsToHideOnHover == null || statsToHideOnHover.activeSelf;
            Revert();
            GameObject target = buttonVisual != null ? buttonVisual : gameObject;
            // Shown whenever equipment is attached — the hover text works with no art; only the
            // press-and-hold portrait swap needs equipment.art (and no-ops without it).
            target.SetActive(_equipment != null);
        }

        public void OnPointerEnter(PointerEventData eventData)
        {
            if (_equipment == null)
                return;
            _statsWereActive = statsToHideOnHover == null || statsToHideOnHover.activeSelf;
            if (statsToHideOnHover != null)
                statsToHideOnHover.SetActive(false);
            if (infoText != null)
            {
                infoText.text = EquipmentCardText.HoverInfo(_equipment, _config);
                infoText.gameObject.SetActive(true);
            }
        }

        public void OnPointerExit(PointerEventData eventData) => Revert();

        public void OnPointerDown(PointerEventData eventData)
        {
            if (_equipment?.art != null && cardArtImage != null)
                cardArtImage.sprite = _equipment.art;
        }

        public void OnPointerUp(PointerEventData eventData) => Revert();

        // Restore the portrait, the host's stat row, and hide the info panel. Also called by the
        // owning card from its own OnPointerExit.
        public void Revert()
        {
            if (cardArtImage != null && _unitArt != null)
                cardArtImage.sprite = _unitArt;
            if (statsToHideOnHover != null)
                statsToHideOnHover.SetActive(_statsWereActive);
            if (infoText != null)
                infoText.gameObject.SetActive(false);
        }
    }
}
