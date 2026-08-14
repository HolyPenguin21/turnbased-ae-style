using Game.Styles;
using TMPro;
using UnityEngine;

namespace Game.Map
{
    // The permanent "you've been here" marker for one hex — a plain "q:r" world-space text,
    // built entirely at runtime (no prefab; same reasoning as HexShaderHighlight/MapCloudOverlay
    // building their own mesh/material in Awake instead of needing one hand-authored in the
    // Editor). Separate from the fog dimming overlay on purpose — per the project owner's own
    // call, visiting a hex is remembered forever (this), while its actual content still re-hides
    // the moment current vision leaves (Custom/FogOfWar.shader) — the two never interact.
    public class HexCoordLabel : MonoBehaviour
    {
        private TextMeshPro _text;

        private void Awake()
        {
            _text = gameObject.AddComponent<TextMeshPro>();
            _text.alignment = TextAlignmentOptions.Center;
            _text.textWrappingMode = TextWrappingModes.NoWrap;
            _text.raycastTarget = false;
            // Lies flat on the map plane (Y=0), readable from this project's fixed elevated
            // camera angle — same rotation convention as ResourceIcon.prefab's own "Circle"
            // child (Euler 90,0,0), which every other flat map decal in this project matches.
            transform.rotation = Quaternion.Euler(90f, 0f, 0f);
            gameObject.SetActive(false); // hidden until ApplyStyle+SetVisited(true) says otherwise
        }

        // Copies the tunables rather than keeping the style reference, same convention as
        // HexShaderHighlight.ApplyStyle — style is often the shared GameConfig asset's own
        // instance, which must never be written back to at runtime.
        public void ApplyStyle(FogOfWarStyle style)
        {
            if (style == null || _text == null)
                return;
            if (style.coordLabelFont != null)
                _text.font = style.coordLabelFont;
            _text.fontSize = style.coordLabelFontSize;
            _text.color = style.coordLabelColor;
            GetComponent<Renderer>().sortingOrder = style.coordLabelSortingOrder;
        }

        public void SetCoord(int q, int r)
        {
            if (_text != null)
                _text.text = $"{q}:{r}";
        }

        public void SetVisited(bool visited)
        {
            if (gameObject.activeSelf != visited)
                gameObject.SetActive(visited);
        }
    }
}
