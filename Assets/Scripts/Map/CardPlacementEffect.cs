using Game.HexGrid;
using Game.Players;
using Game.UI;
using UnityEngine;

namespace Game.Map
{
    internal static class CardPlacementEffect
    {
        private const float CardDiagonalInHexRadii = 1.6f;
        private const float SurfaceOffset = 0.015f;

        // screenWidth: the card art's on-screen width at the drop, in pixels. The map copy is sized
        // to the same on-screen width at the hex, so it does not jump in size; <= 0 falls back
        // to a fixed size relative to the hex.
        public static void Play(HexMap map, HexCoord hex, Sprite art, PlayerSetupData owner, float screenWidth = 0f)
        {
            if (map == null || art == null || !CanShow(owner, hex))
                return;

            Bounds bounds = art.bounds;
            float diagonal = bounds.size.magnitude;
            if (diagonal <= 0f || map.OuterRadius <= 0f)
                return;
            var material = CardDissolveEffect.CreateMaterial(art, Color.white);
            if (material == null) return;

            var mapTransform = map.transform;
            var camera = Camera.main;
            Vector3 hexWorld = map.HexToWorld(hex);
            float scale = ScreenMatchedScale(camera, mapTransform, hexWorld, screenWidth, bounds.size.x)
                ?? map.OuterRadius * CardDiagonalInHexRadii / diagonal;
            Vector3 cardTop = camera != null
                ? mapTransform.InverseTransformDirection(camera.transform.up)
                : Vector3.forward;
            cardTop.y = 0f;
            if (cardTop.sqrMagnitude < 0.0001f)
                cardTop = Vector3.forward;
            var rotation = Quaternion.LookRotation(Vector3.down, cardTop);
            Vector3 center = mapTransform.InverseTransformPoint(hexWorld) + Vector3.up * SurfaceOffset;

            var go = new GameObject("Card Placement Dissolve");
            go.layer = map.gameObject.layer;
            go.transform.SetParent(mapTransform, false);
            go.transform.localRotation = rotation;
            go.transform.localScale = Vector3.one * scale;
            go.transform.localPosition = center - rotation * (bounds.center * scale);

            var sprite = go.AddComponent<SpriteRenderer>();
            sprite.sprite = art;
            sprite.sortingOrder = MapSortingOrder.CardPlacement;
            sprite.sharedMaterial = material;
            CardDissolveEffect.Play(go, material, () => CanShow(owner, hex));
        }

        // Local scale at which the sprite (local width spriteWidth) covers screenWidth pixels across
        // the screen's horizontal through the hex centre, measured on the map surface plane. The
        // card lies with its top towards the camera's up, so its width runs along screen X.
        private static float? ScreenMatchedScale(Camera camera, Transform mapTransform, Vector3 hexWorld,
            float screenWidth, float spriteWidth)
        {
            if (camera == null || screenWidth <= 0f || spriteWidth <= 0f) return null;
            Vector3 centre = camera.WorldToScreenPoint(hexWorld);
            if (centre.z <= 0f) return null;
            var plane = new Plane(mapTransform.up, hexWorld);
            Vector3? left = ProjectToPlane(camera, plane, new Vector2(centre.x - screenWidth * 0.5f, centre.y));
            Vector3? right = ProjectToPlane(camera, plane, new Vector2(centre.x + screenWidth * 0.5f, centre.y));
            if (!left.HasValue || !right.HasValue) return null;
            Vector3 localSpan = mapTransform.InverseTransformVector(right.Value - left.Value);
            float width = localSpan.magnitude;
            return width > 0f ? width / spriteWidth : (float?)null;
        }

        private static Vector3? ProjectToPlane(Camera camera, Plane plane, Vector2 screenPoint)
        {
            Ray ray = camera.ScreenPointToRay(screenPoint);
            return plane.Raycast(ray, out float distance) ? ray.GetPoint(distance) : (Vector3?)null;
        }

        private static bool CanShow(PlayerSetupData owner, HexCoord hex)
            => LocalSeat.Is(owner) && VisionSystem.IsVisibleToCurrentViewer(hex);
    }
}
