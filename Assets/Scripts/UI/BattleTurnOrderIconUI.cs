using Game.Units;
using UnityEngine;
using UnityEngine.UI;

namespace Game.UI
{
    // One entry in BattleScreenUI's turn-order strip — that unit's own art plus its owner's
    // faction logo to its left, highlighted while it's the one currently up (see
    // BattleScreenUI.RefreshTurnOrder). Mirrors the manual's own "icons on the side of the
    // Battle Viewer Interface display which side is currently taking an action and the order" —
    // simplified to art+logo rather than the original's full roll-status iconography.
    public class BattleTurnOrderIconUI : MonoBehaviour
    {
        [SerializeField] private Image factionLogo;
        [SerializeField] private Image artImage;
        [SerializeField] private Image highlightBorder;

        public void Setup(UnitData unit, Sprite ownerFactionLogo, Color ownerColor, bool isCurrent)
        {
            if (factionLogo != null)
            {
                factionLogo.sprite = ownerFactionLogo;
                factionLogo.gameObject.SetActive(ownerFactionLogo != null);
            }
            if (artImage != null)
                artImage.sprite = unit != null ? unit.Art : null;
            if (highlightBorder != null)
            {
                highlightBorder.color = ownerColor;
                highlightBorder.gameObject.SetActive(isCurrent);
            }
        }
    }
}
