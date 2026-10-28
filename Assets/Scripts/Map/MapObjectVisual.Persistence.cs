using UnityEngine;

namespace Game.Map
{
    public partial class MapObjectVisual
    {
        internal SpriteRenderer PersistenceHitRenderer => hitRendererOverride != null ? hitRendererOverride : innerCircle;
        internal float PersistenceClickRadius => clickRadiusFactor;
        internal SpriteRenderer PersistenceInnerCircle => innerCircle;
        internal SpriteRenderer PersistenceObjectImage => objectImage;
        internal SpriteRenderer PersistenceFactionAccent => factionAccent;
        internal SpriteRenderer[] PersistenceAuxiliaryRenderers => auxiliaryRenderers;
        internal bool PersistenceControlsVisibility(SpriteRenderer renderer)
        {
            if (renderer == innerCircle || renderer == objectImage || renderer == factionAccent) return true;
            if (auxiliaryRenderers != null) foreach (var auxiliary in auxiliaryRenderers) if (renderer == auxiliary) return true;
            return false;
        }

        // A saved tree contains only rendering state. Never recreate ArmyController, gameplay
        // scripts or flag animators: the exact observed flag frame is already on its renderer.
        internal void ConfigurePersistedAppearance(SpriteRenderer circle, SpriteRenderer image,
            SpriteRenderer accent, SpriteRenderer[] auxiliary, SpriteRenderer hit, float radius)
        {
            // Retain authored renderer roles: an imported army observation is refreshed with
            // CopyAppearanceFrom when it is actually seen again, just like an ordinary clone.
            innerCircle = circle;
            hitRendererOverride = hit;
            objectImage = image;
            factionAccent = accent;
            auxiliaryRenderers = auxiliary;
            clickRadiusFactor = radius;
            FreezeRememberedAppearance();
        }
    }
}
