using Game.Units;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Game.UI
{
    // One entry in BattleScreenUI's turn-order strip — that unit's own detail art plus its owner's
    // faction logo to its left, highlighted while it's the one currently up (see
    // BattleScreenUI.RefreshTurnOrder). Mirrors the manual's own "icons on the side of the
    // Battle Viewer Interface display which side is currently taking an action and the order" —
    // simplified to art+logo rather than the original's full roll-status iconography.
    public class BattleTurnOrderIconUI : MonoBehaviour
    {
        private const float InactiveBorderAlpha = 50f;
        private const float InactiveTitleAlpha = 150f;

        [SerializeField] private Image factionLogo;
        [SerializeField] private Image artImage;
        [SerializeField] private Image highlightBorder;
        [SerializeField] private Image titleBackground;
        [SerializeField] private TMP_Text titleText;

        public void Setup(UnitData unit, Sprite ownerFactionLogo, Color ownerColor, bool isCurrent)
        {
            if (factionLogo != null)
            {
                factionLogo.sprite = ownerFactionLogo;
                factionLogo.gameObject.SetActive(ownerFactionLogo != null);
            }
            if (artImage != null)
            {
                Sprite unitDetailArt = unit != null ? unit.DetailArt : null;
                artImage.sprite = unitDetailArt;
                artImage.gameObject.SetActive(unitDetailArt != null);
            }
            if (titleText != null)
            {
                titleText.text = unit != null ? unit.Name : string.Empty;
                titleText.color = WithAlpha(titleText.color, isCurrent ? 255f : InactiveTitleAlpha);
            }
            if (titleBackground != null)
                titleBackground.color = WithAlpha(titleBackground.color, isCurrent ? 255f : InactiveTitleAlpha);
            if (highlightBorder != null)
            {
                // Always shown in the owner's color; dimmed while it isn't this unit's turn.
                Color borderColor = ownerColor;
                if (!isCurrent)
                    borderColor.a = InactiveBorderAlpha / 255f;
                highlightBorder.color = borderColor;
                highlightBorder.gameObject.SetActive(true);
            }
        }

        private static Color WithAlpha(Color color, float alpha255)
        {
            color.a = alpha255 / 255f;
            return color;
        }
    }
}
