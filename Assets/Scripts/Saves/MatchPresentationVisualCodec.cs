using System;
using System.Collections.Generic;
using Game.Core;
using Game.Map;
using Game.Players;
using UnityEngine;

namespace Game.Saves
{
    internal static partial class MatchPresentationCodec
    {
        private static MatchFrozenVisualData CaptureVisual(MapObjectVisual visual)
        {
            if (visual == null || !visual.IsFrozenRememberedAppearance) throw new ArgumentException("Expected frozen appearance clone.");
            var data = new MatchFrozenVisualData { IsFrozen = true, ClickRadius = visual.PersistenceClickRadius };
            var nodes = new Dictionary<Transform, int>();
            var rendererIndices = new Dictionary<SpriteRenderer, int>();
            CaptureNode(visual.transform, -1, data.Nodes, nodes);
            foreach (var renderer in visual.GetComponentsInChildren<SpriteRenderer>(true))
            {
                rendererIndices.Add(renderer, data.Renderers.Count);
                if (renderer == visual.PersistenceHitRenderer) data.HitRenderer = data.Renderers.Count;
                data.Renderers.Add(new MatchSpriteRendererData { Node = nodes[renderer.transform],
                    SpriteKey = MatchSaveAssets.SpriteKey(renderer.sprite), MaterialKey = MatchSaveAssets.MaterialKey(renderer.sharedMaterial),
                    Tint = renderer.color, SortingLayer = renderer.sortingLayerID, SortingOrder = renderer.sortingOrder,
                    Enabled = renderer.enabled, ControlsVisibility = visual.PersistenceControlsVisibility(renderer), FlipX = renderer.flipX, FlipY = renderer.flipY,
                    DrawMode = (int)renderer.drawMode, TileMode = (int)renderer.tileMode,
                    MaskInteraction = (int)renderer.maskInteraction, SortPoint = (int)renderer.spriteSortPoint,
                    Size = renderer.size, AdaptiveModeThreshold = renderer.adaptiveModeThreshold });
            }
            int Index(SpriteRenderer renderer) => renderer == null ? -1 : rendererIndices[renderer];
            data.InnerCircle = Index(visual.PersistenceInnerCircle);
            data.ObjectImage = Index(visual.PersistenceObjectImage);
            data.FactionAccent = Index(visual.PersistenceFactionAccent);
            if (visual.PersistenceAuxiliaryRenderers != null)
                foreach (var renderer in visual.PersistenceAuxiliaryRenderers) data.AuxiliaryRenderers.Add(Index(renderer));
            return data;
        }
        private static void CaptureNode(Transform node, int parent, List<MatchVisualNodeData> values, Dictionary<Transform, int> indices)
        {
            int index = values.Count; indices.Add(node, index);
            values.Add(new MatchVisualNodeData { Parent = parent, Name = node.name, Active = node.gameObject.activeSelf, Layer = node.gameObject.layer,
                Position = parent < 0 ? node.position : node.localPosition, Rotation = parent < 0 ? node.rotation : node.localRotation,
                Scale = node.localScale });
            for (int i = 0; i < node.childCount; i++) CaptureNode(node.GetChild(i), index, values, indices);
        }
        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        private static bool Finite(Vector3 value) => Finite(value.x) && Finite(value.y) && Finite(value.z);
        private static void ValidateVisual(MatchFrozenVisualData data)
        {
            if (!data.IsFrozen || data.Nodes == null || data.Nodes.Count == 0 || data.Nodes.Count > 4096
                || data.Renderers == null || data.Renderers.Count > 4096 || !Finite(data.ClickRadius)
                || data.ClickRadius < 0.2f || data.ClickRadius > 1f || data.HitRenderer < -1 || data.HitRenderer >= data.Renderers.Count
                || data.AuxiliaryRenderers == null || data.AuxiliaryRenderers.Count > 4096)
                throw new ArgumentException("Invalid frozen appearance tree.");
            bool ValidRole(int index) => index >= -1 && index < data.Renderers.Count;
            if (!ValidRole(data.InnerCircle) || !ValidRole(data.ObjectImage) || !ValidRole(data.FactionAccent))
                throw new ArgumentException("Invalid frozen renderer role.");
            var controlled = new HashSet<int> { data.InnerCircle, data.ObjectImage, data.FactionAccent };
            foreach (int index in data.AuxiliaryRenderers)
            {
                if (!ValidRole(index)) throw new ArgumentException("Invalid frozen auxiliary renderer role.");
                controlled.Add(index);
            }
            for (int i = 0; i < data.Renderers.Count; i++)
                if (data.Renderers[i] == null || data.Renderers[i].ControlsVisibility != controlled.Contains(i))
                    throw new ArgumentException("Frozen renderer visibility differs from its roles.");
            for (int i = 0; i < data.Nodes.Count; i++)
            {
                var node = data.Nodes[i];
                if (node == null || (i == 0 ? node.Parent != -1 : node.Parent < 0 || node.Parent >= i)
                    || node.Layer < 0 || node.Layer > 31 || !Finite(node.Position) || !Finite(node.Scale) || !Finite(node.Rotation.x) || !Finite(node.Rotation.y)
                    || !Finite(node.Rotation.z) || !Finite(node.Rotation.w)) throw new ArgumentException("Invalid frozen appearance pose.");
                float norm = node.Rotation.x * node.Rotation.x + node.Rotation.y * node.Rotation.y
                    + node.Rotation.z * node.Rotation.z + node.Rotation.w * node.Rotation.w;
                if (Math.Abs(norm - 1f) > 0.01f) throw new ArgumentException("Invalid frozen appearance rotation.");
            }
            foreach (var renderer in data.Renderers)
            {
                if (renderer == null || renderer.Node < 0 || renderer.Node >= data.Nodes.Count
                    || !Finite(renderer.Tint.r) || !Finite(renderer.Tint.g) || !Finite(renderer.Tint.b) || !Finite(renderer.Tint.a)
                    || !Finite(renderer.Size.x) || !Finite(renderer.Size.y) || renderer.Size.x < 0 || renderer.Size.y < 0
                    || !Finite(renderer.AdaptiveModeThreshold) || renderer.AdaptiveModeThreshold < 0 || renderer.AdaptiveModeThreshold > 1
                    || !Enum.IsDefined(typeof(SpriteDrawMode), renderer.DrawMode) || !Enum.IsDefined(typeof(SpriteTileMode), renderer.TileMode)
                    || !Enum.IsDefined(typeof(SpriteMaskInteraction), renderer.MaskInteraction) || !Enum.IsDefined(typeof(SpriteSortPoint), renderer.SortPoint)
                    || !SortingLayer.IsValid(renderer.SortingLayer)) throw new ArgumentException("Invalid frozen sprite renderer.");
                MatchSaveAssets.ResolveSprite(renderer.SpriteKey); MatchSaveAssets.ResolveMaterial(renderer.MaterialKey);
            }
        }
        internal static void RestorePresentation(MatchPresentationData data, HexSelectionController selection,
            GameConfig config, Func<string, PlayerSetupData> player)
        {
            var values = new Dictionary<string, MatchFrozenVisualData>(StringComparer.Ordinal);
            var references = new RememberedMapVisualState(); int next = 0;
            foreach (var value in data.Visuals)
            {
                string key = (++next).ToString(System.Globalization.CultureInfo.InvariantCulture);
                values.Add(key, value);
                var reference = new MapVisualSnapshotReference { TypeKey = "frozen-tree-v1", SnapshotKey = key };
                if (value.IsArmy) references.Armies.Add(new RememberedArmyVisualState { ViewerId = value.ViewerId,
                    SourceArmyId = value.SourceArmyId, Hex = new Game.HexGrid.HexCoord(value.Q, value.R), Appearance = reference });
                else references.Buildings.Add(new RememberedBuildingVisualState { ViewerId = value.ViewerId,
                    Hex = new Game.HexGrid.HexCoord(value.Q, value.R), Appearance = reference });
            }
            var created = new List<MapObjectVisual>();
            try
            {
                selection.RestoreRememberedVisuals(references, player, reference =>
                {
                    if (reference.TypeKey != "frozen-tree-v1" || !values.TryGetValue(reference.SnapshotKey, out var value))
                        throw new ArgumentException("Invalid frozen appearance reference.");
                    var visual = RestoreVisual(value); created.Add(visual); return visual;
                });
            }
            finally
            {
                foreach (var visual in created)
                    if (visual != null) DestroyTemporary(visual.gameObject);
            }
        }
        private static MapObjectVisual RestoreVisual(MatchFrozenVisualData data)
        {
            ValidateVisual(data);
            GameObject root = null;
            try
            {
                var nodes = new List<Transform>();
                foreach (var value in data.Nodes)
                {
                    var node = new GameObject(value.Name ?? "Remembered appearance");
                    if (root == null) root = node;
                    // Keep the entire detached tree inactive while its renderers are populated.
                    node.SetActive(false); node.layer = value.Layer;
                    if (value.Parent >= 0) node.transform.SetParent(nodes[value.Parent], false);
                    node.transform.localPosition = value.Position; node.transform.localRotation = value.Rotation;
                    node.transform.localScale = value.Scale; nodes.Add(node.transform);
                }
                var renderers = new List<SpriteRenderer>();
                foreach (var value in data.Renderers)
                {
                    var renderer = nodes[value.Node].gameObject.AddComponent<SpriteRenderer>();
                    renderer.sprite = MatchSaveAssets.ResolveSprite(value.SpriteKey);
                    renderer.sharedMaterial = MatchSaveAssets.ResolveMaterial(value.MaterialKey);
                    renderer.color = value.Tint; renderer.sortingLayerID = value.SortingLayer; renderer.sortingOrder = value.SortingOrder;
                    renderer.flipX = value.FlipX; renderer.flipY = value.FlipY;
                    renderer.drawMode = (SpriteDrawMode)value.DrawMode; renderer.tileMode = (SpriteTileMode)value.TileMode;
                    renderer.maskInteraction = (SpriteMaskInteraction)value.MaskInteraction; renderer.spriteSortPoint = (SpriteSortPoint)value.SortPoint;
                    renderer.size = value.Size; renderer.adaptiveModeThreshold = value.AdaptiveModeThreshold;
                    renderer.enabled = false; renderers.Add(renderer);
                }
                var result = root.AddComponent<MapObjectVisual>();
                SpriteRenderer Role(int index) => index < 0 ? null : renderers[index];
                var auxiliary = new List<SpriteRenderer>();
                foreach (int index in data.AuxiliaryRenderers) auxiliary.Add(Role(index));
                result.ConfigurePersistedAppearance(Role(data.InnerCircle), Role(data.ObjectImage), Role(data.FactionAccent),
                    auxiliary.ToArray(), Role(data.HitRenderer), data.ClickRadius);
                for (int i = 0; i < renderers.Count; i++)
                    if (!data.Renderers[i].ControlsVisibility) renderers[i].enabled = data.Renderers[i].Enabled;
                for (int i = nodes.Count - 1; i >= 0; i--) nodes[i].gameObject.SetActive(data.Nodes[i].Active);
                return result;
            }
            catch { if (root != null) DestroyTemporary(root); throw; }
        }
        private static void DestroyTemporary(GameObject value)
        {
            if (Application.isPlaying) UnityEngine.Object.Destroy(value);
            else UnityEngine.Object.DestroyImmediate(value);
        }
    }
}
