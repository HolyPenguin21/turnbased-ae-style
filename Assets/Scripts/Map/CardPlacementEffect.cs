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

        public static void Play(HexMap map, HexCoord hex, Sprite art, PlayerSetupData owner)
        {
            if (map == null || art == null || !CanShow(owner, hex))
                return;

            Bounds bounds = art.bounds;
            float diagonal = bounds.size.magnitude;
            if (diagonal <= 0f || map.OuterRadius <= 0f)
                return;
            var material = CardDissolveEffect.CreateMaterial(art, Color.white);
            if (material == null) return;

            float scale = map.OuterRadius * CardDiagonalInHexRadii / diagonal;
            var mapTransform = map.transform;
            var camera = Camera.main;
            Vector3 cardTop = camera != null
                ? mapTransform.InverseTransformDirection(camera.transform.up)
                : Vector3.forward;
            cardTop.y = 0f;
            if (cardTop.sqrMagnitude < 0.0001f)
                cardTop = Vector3.forward;
            var rotation = Quaternion.LookRotation(Vector3.down, cardTop);
            Vector3 center = mapTransform.InverseTransformPoint(map.HexToWorld(hex)) + Vector3.up * SurfaceOffset;

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

        private static bool CanShow(PlayerSetupData owner, HexCoord hex)
            => LocalSeat.Is(owner) && VisionSystem.IsVisibleToCurrentViewer(hex);
    }
}
