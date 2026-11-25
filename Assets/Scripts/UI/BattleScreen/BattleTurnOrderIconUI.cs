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
        private const float InactiveContentAlpha = 150f;

        [SerializeField] private Image factionLogo;
        [SerializeField] private Image artImage;
        [SerializeField] private Image highlightBorder;
        [SerializeField] private Image titleBackground;
        [SerializeField] private TMP_Text titleText;
        internal UnitData Unit { get; private set; }
        private (Color OwnerColor, bool Current)? _style;

        public void Setup(UnitData unit, Sprite ownerFactionLogo, Color ownerColor, bool isCurrent)
        {
            Unit = unit;
            if (factionLogo != null && factionLogo.sprite != ownerFactionLogo)
            {
                factionLogo.sprite = ownerFactionLogo;
            }
            if (factionLogo != null && factionLogo.gameObject.activeSelf != (ownerFactionLogo != null))
                factionLogo.gameObject.SetActive(ownerFactionLogo != null);
            Sprite art = unit?.DetailArt;
            if (artImage != null && artImage.sprite != art) artImage.sprite = art;
            if (artImage != null && artImage.gameObject.activeSelf != (art != null)) artImage.gameObject.SetActive(art != null);
            string title = unit?.Name ?? string.Empty;
            if (titleText != null && titleText.text != title) titleText.text = title;

            var style = (ownerColor, isCurrent);
            if (_style == style) return;
            _style = style;
            float contentAlpha = isCurrent ? 255f : InactiveContentAlpha;
            if (factionLogo != null) factionLogo.color = WithAlpha(factionLogo.color, contentAlpha);
            if (artImage != null) artImage.color = WithAlpha(artImage.color, contentAlpha);
            if (titleText != null) titleText.color = WithAlpha(titleText.color, contentAlpha);
            if (titleBackground != null) titleBackground.color = WithAlpha(titleBackground.color, contentAlpha);
            if (highlightBorder != null)
            {
                highlightBorder.color = isCurrent ? ownerColor : WithAlpha(ownerColor, InactiveBorderAlpha);
                if (!highlightBorder.gameObject.activeSelf) highlightBorder.gameObject.SetActive(true);
            }
        }

        private static Color WithAlpha(Color color, float alpha255)
        {
            color.a = alpha255 / 255f;
            return color;
        }
    }
}
