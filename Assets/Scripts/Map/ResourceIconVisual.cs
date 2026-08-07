using System.Collections.Generic;
using Game.Economy;
using TMPro;
using UnityEngine;

namespace Game.Map
{
    // A small coloured circle + number showing how much of one resource a hex yields. Unlike
    // MapObjectVisual, the circle colour here is fixed per resource type (not per player) —
    // matches the original game's resource icon colours: Human beige, Energy yellow, Materials
    // green, Tech blue.
    public class ResourceIconVisual : MonoBehaviour
    {
        private static readonly Dictionary<ResourceType, Color> ResourceColors = new Dictionary<ResourceType, Color>
        {
            { ResourceType.Human, new Color(0.80f, 0.70f, 0.55f) },
            { ResourceType.Energy, new Color(0.85f, 0.75f, 0.15f) },
            { ResourceType.Materials, new Color(0.20f, 0.60f, 0.20f) },
            { ResourceType.Tech, new Color(0.15f, 0.35f, 0.80f) },
        };

        [SerializeField] private SpriteRenderer circle;
        [SerializeField] private TMP_Text amountText;

        // Lets other UI (e.g. the initiative dice buy panel) match these same per-resource
        // colours without duplicating the map's colour choices.
        public static Color GetColor(ResourceType type) => ResourceColors[type];

        public void SetResource(ResourceType type, int amount)
        {
            if (circle != null && ResourceColors.TryGetValue(type, out Color color))
                circle.color = color;
            if (amountText != null)
                amountText.text = amount.ToString();
        }
    }
}
