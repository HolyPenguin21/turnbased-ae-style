using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Game.UI
{
    // A small press-and-hold button that lives on a unit/hero card (CardUI, ArmyUnitCardUI,
    // BattleGridCellUI) and, while held, swaps the card's portrait to show the art of whatever
    // CardType.Equipment card is attached to that host — the disclosure the project owner asked
    // for instead of a permanent second thumbnail. Reverts on release, and the owning card also
    // reverts it when the pointer leaves the card entirely (per the spec: "когда убираем
    // указатель с карты изображение юнита возвращается").
    //
    // The owning card component drives this: it calls Configure(unitArt, equipmentArt) every
    // time it (re)binds a unit — that also shows/hides this button (hidden when nothing's
    // attached) — and Revert() from its own OnPointerExit.
    public class EquipmentArtToggle : MonoBehaviour, IPointerDownHandler, IPointerUpHandler
    {
        [SerializeField] private Image cardArtImage;
        // The GameObject to show only while equipment is attached — defaults to this one.
        [SerializeField] private GameObject buttonVisual;

        private Sprite _unitArt;
        private Sprite _equipmentArt;

        private void Awake()
        {
            if (buttonVisual == null)
                buttonVisual = gameObject;
        }

        public void Configure(Sprite unitArt, Sprite equipmentArt)
        {
            _unitArt = unitArt;
            _equipmentArt = equipmentArt;
            Revert();
            if (buttonVisual != null)
                buttonVisual.SetActive(_equipmentArt != null);
        }

        public void OnPointerDown(PointerEventData eventData)
        {
            if (_equipmentArt != null && cardArtImage != null)
                cardArtImage.sprite = _equipmentArt;
        }

        public void OnPointerUp(PointerEventData eventData) => Revert();

        public void Revert()
        {
            if (cardArtImage != null && _unitArt != null)
                cardArtImage.sprite = _unitArt;
        }
    }
}
