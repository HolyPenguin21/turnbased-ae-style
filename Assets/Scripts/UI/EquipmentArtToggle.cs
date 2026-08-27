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
    //   - a text element (infoText) replaced with the equipment's name, added abilities, then
    //     stat changes (see EquipmentCardText.HoverInfo)
    // Each of the three overridden elements has its previous value captured on the first change
    // and restored when neither hover nor press holds, so statsToHideOnHover / infoText can
    // point straight at the card's own existing stat row / ability-text element — no dedicated
    // overlay objects needed. The owning card also calls Revert() from its own OnPointerExit.
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
        // A text element whose content is swapped for the equipment description while shown
        // (typically the card's own ability/name text — its text is restored afterwards).
        [SerializeField] private TMP_Text infoText;

        private CardDefinition _equipment;
        private GameConfig _config;
        private bool _hovering;
        private bool _pressed;

        // Captured-on-first-change / restored-when-done state for each overridden element.
        private bool _showing;
        private Sprite _savedArt;
        private string _savedInfoText;
        private bool _savedInfoActive;
        private bool _savedStatsActive;

        public void Configure(CardDefinition equipment, GameConfig config)
        {
            RestoreNow();                 // undo anything still applied from a previous binding
            _equipment = equipment;
            _config = config;
            _hovering = false;
            _pressed = false;

            GameObject target = buttonVisual != null ? buttonVisual : gameObject;
            // Shown whenever equipment is attached — the info text works with no art; only the
            // portrait swap needs equipment.art (and no-ops without it).
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
            bool show = _equipment != null && (_hovering || _pressed);
            if (show)
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
            if (infoText != null)
            {
                _savedInfoText = infoText.text;
                _savedInfoActive = infoText.gameObject.activeSelf;
                infoText.text = EquipmentCardText.HoverInfo(_equipment, _config);
                infoText.gameObject.SetActive(true);
            }
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
            if (infoText != null)
            {
                infoText.text = _savedInfoText;
                infoText.gameObject.SetActive(_savedInfoActive);
            }
        }
    }
}
