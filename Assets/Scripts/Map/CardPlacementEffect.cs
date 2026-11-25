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

        // hasScreenPose: the card art's on-screen pose at the drop is known — screenCenter and
        // screenBottomEdge (bottom-left -> bottom-right, pixels). The map copy is laid on the map
        // surface under that centre, with the same on-screen width and tilt, so it does not jump.
        // Without it the copy falls back to a fixed size centred on the hex, top towards the camera.
        public static void Play(HexMap map, HexCoord hex, Sprite art, PlayerSetupData owner,
            bool hasScreenPose = false, Vector2 screenCenter = default, Vector2 screenBottomEdge = default)
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
            if (!hasScreenPose || !TryScreenPose(camera, mapTransform, hexWorld, screenCenter, screenBottomEdge,
                    bounds.size.x, out Vector3 center, out Vector3 cardRight, out float scale))
            {
                center = mapTransform.InverseTransformPoint(hexWorld);
                cardRight = DefaultCardRight(camera, mapTransform);
                scale = map.OuterRadius * CardDiagonalInHexRadii / diagonal;
            }
            // Card lies face up (forward = down into the map); its top follows from its right edge.
            var rotation = Quaternion.LookRotation(Vector3.down, Vector3.Cross(Vector3.down, cardRight));
            center += Vector3.up * SurfaceOffset;

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

        // Projects the art's screen centre and bottom edge (laid through that centre) onto the map
        // surface plane through the hex: map-local centre, map-local right direction, and the local
        // scale at which the sprite (local width spriteWidth) spans that edge.
        private static bool TryScreenPose(Camera camera, Transform mapTransform, Vector3 hexWorld,
            Vector2 screenCenter, Vector2 screenBottomEdge, float spriteWidth,
            out Vector3 center, out Vector3 cardRight, out float scale)
        {
            center = cardRight = Vector3.zero;
            scale = 0f;
            if (camera == null || spriteWidth <= 0f || screenBottomEdge.sqrMagnitude <= 0f) return false;
            var plane = new Plane(mapTransform.up, hexWorld);
            Vector3? mid = ProjectToPlane(camera, plane, screenCenter);
            Vector3? left = ProjectToPlane(camera, plane, screenCenter - screenBottomEdge * 0.5f);
            Vector3? right = ProjectToPlane(camera, plane, screenCenter + screenBottomEdge * 0.5f);
            if (!mid.HasValue || !left.HasValue || !right.HasValue) return false;
            Vector3 span = mapTransform.InverseTransformVector(right.Value - left.Value);
            span.y = 0f;
            float width = span.magnitude;
            if (width <= 0f) return false;
            center = mapTransform.InverseTransformPoint(mid.Value);
            cardRight = span / width;
            scale = width / spriteWidth;
            return true;
        }

        private static Vector3 DefaultCardRight(Camera camera, Transform mapTransform)
        {
            Vector3 right = camera != null
                ? mapTransform.InverseTransformDirection(camera.transform.right)
                : Vector3.right;
            right.y = 0f;
            return right.sqrMagnitude < 0.0001f ? Vector3.right : right.normalized;
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
