using System.Collections.Generic;
using Game.Core;
using Game.HexGrid;
using Game.Players;
using Game.Styles;
using Game.Turns;
using UnityEngine;

namespace Game.Map
{
    // Ties VisionSystem's per-player state to what actually shows on screen: the dimming
    // overlay over every hex the current viewer (VisionSystem.CurrentViewer) doesn't presently
    // have vision of (Custom/FogOfWar.shader), and the permanent per-hex "q:r" coordinate label
    // that appears once that viewer has ever visited it (see HexCoordLabel — a separate,
    // unrelated marker, per the project owner's own call). Lives on the same GameObject as
    // HexMap (same convention as MapResourceDisplay) so RefreshAll can be called from
    // HexMapGenerator right after the map's real hex data exists — this component isn't
    // [ExecuteAlways], so it can't discover that on its own the way HexMapGenerator can.
    //
    // Doesn't gate armies/buildings/resource icons itself — those hide/show their OWN markers
    // (see HexSelectionController.RestackArmiesOn, MapResourceDisplay.RefreshVisibility) off the
    // exact same VisionSystem.VisibilityChanged/GameTurnController.TurnStateChanged signals this
    // reacts to, each independently, same "every component owns its own subscription" pattern
    // used throughout this project.
    [RequireComponent(typeof(HexMap))]
    public class FogOfWarController : MonoBehaviour
    {
        [SerializeField] private GameConfig gameConfig;
        [SerializeField] private GameTurnController turnController;

        // Assets/Materials/FogOfWar.mat — an authored asset instead of a `new Material(shader)`
        // built at runtime. Tint/edge/noise values live ONLY on this asset (its own Inspector),
        // so they can be tuned live in Play Mode with immediate visual feedback (asset edits
        // survive stopping Play Mode, unlike scene/GameObject state) instead of round-tripping
        // through shader-default edits + a fresh Play session for every attempt.
        [SerializeField] private Material overlayMaterial;

        private static readonly int VisibilityMaskId = Shader.PropertyToID("_VisibilityMask");
        private static readonly int OuterRadiusId = Shader.PropertyToID("_OuterRadius");
        private static readonly int MaskMinQRId = Shader.PropertyToID("_MaskMinQR");
        private static readonly int MaskSizeId = Shader.PropertyToID("_MaskSize");

        private HexMap _map;
        private HexMap Map => _map != null ? _map : (_map = GetComponent<HexMap>());

        private MeshRenderer _overlayRenderer;
        private MaterialPropertyBlock _propertyBlock;
        private Texture2D _mask;
        private int _minQ, _minR, _maskWidth, _maskHeight;
        private byte[] _maskPixels;

        private readonly Dictionary<HexCoord, HexCoordLabel> _labels = new Dictionary<HexCoord, HexCoordLabel>();

        private void OnEnable()
        {
            VisionSystem.VisibilityChanged += OnVisibilityChanged;
            if (turnController != null)
                turnController.TurnStateChanged += RefreshVisibility;
        }

        private void OnDisable()
        {
            VisionSystem.VisibilityChanged -= OnVisibilityChanged;
            if (turnController != null)
                turnController.TurnStateChanged -= RefreshVisibility;
        }

        private void OnVisibilityChanged(PlayerSetupData player)
        {
            if (player == VisionSystem.CurrentViewer)
                RefreshVisibility();
        }

        // Called once, right after HexMapGenerator populates HexMap's real data (see its own
        // GetComponent<FogOfWarController>()?.RefreshAll() hook) — builds the overlay quad sized
        // to the map's actual bounds, the visibility mask texture, and one label per hex. Safe to
        // call again later (e.g. a future "regenerate map" feature) — rebuilds everything fresh.
        public void RefreshAll()
        {
            HexMap map = Map;
            if (gameConfig == null || map == null)
                return;

            BuildOverlayQuad(map);
            BuildMask(map);
            BuildLabels(map);
            RefreshVisibility();
        }

        private void BuildOverlayQuad(HexMap map)
        {
            if (_overlayRenderer == null)
            {
                var overlayObject = new GameObject("FogOverlayQuad");
                overlayObject.transform.SetParent(transform, worldPositionStays: false);
                overlayObject.transform.localPosition = Vector3.zero;
                overlayObject.transform.localRotation = Quaternion.identity;
                overlayObject.transform.localScale = Vector3.one;

                overlayObject.AddComponent<MeshFilter>();
                _overlayRenderer = overlayObject.AddComponent<MeshRenderer>();
                _overlayRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                _overlayRenderer.receiveShadows = false;

                if (overlayMaterial == null)
                    Debug.LogWarning("FogOfWarController: 'Overlay Material' is not assigned (expected Assets/Materials/FogOfWar.mat).");

                // The SHARED asset, not a runtime clone — so tweaking it in the Inspector while
                // in Play Mode affects this renderer immediately, and the tuned values persist
                // on the asset after stopping Play Mode.
                _overlayRenderer.sharedMaterial = overlayMaterial;
                _propertyBlock = new MaterialPropertyBlock();
            }

            _overlayRenderer.GetComponent<MeshFilter>().mesh = BuildQuadMesh(map, gameConfig.mapGeneration.ComputeBorderDepthWorld());

            FogOfWarStyle style = gameConfig.fogOfWarStyle;
            _overlayRenderer.sortingOrder = style != null ? style.sortingOrder : 4;
        }

        // World-space bounding rectangle over every hex centre, padded out to the decorative
        // border's own depth (see HexMapGenerator/MapGenerationSettings.ComputeBorderDepthWorld)
        // so the overlay's dark edge reaches at least as far as the border hexes it sits above —
        // otherwise the quad stopped at the old (border-less) map edge while the border extended
        // well past it, leaving a visible seam where the fog just stopped darkening mid-border.
        // The shader itself has no mesh UVs (it derives axial coords from world position via
        // _MaskMinQR/_MaskSize), so this extra geometry doesn't need — and doesn't get — any
        // matching growth of the visibility mask: positions past the real mask just Clamp to its
        // outermost (always-fogged) texel, which is exactly the "always dark" look the border
        // wants. Same vertices-in-local-space convention as HexShaderHighlight/
        // HexClusterHighlight (this object sits at the map's own origin, see BuildOverlayQuad, so
        // local space already IS world space here).
        private static Mesh BuildQuadMesh(HexMap map, float borderDepthWorld)
        {
            float minX = float.MaxValue, maxX = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue;
            foreach (HexCoord coord in map.AllCoords)
            {
                Vector3 world = map.HexToWorld(coord);
                minX = Mathf.Min(minX, world.x);
                maxX = Mathf.Max(maxX, world.x);
                minZ = Mathf.Min(minZ, world.z);
                maxZ = Mathf.Max(maxZ, world.z);
            }

            float pad = map.OuterRadius * 1.5f + Mathf.Max(0f, borderDepthWorld);
            minX -= pad; maxX += pad; minZ -= pad; maxZ += pad;

            var mesh = new Mesh { name = "FogOverlayQuad" };
            mesh.SetVertices(new[]
            {
                new Vector3(minX, 0f, minZ),
                new Vector3(maxX, 0f, minZ),
                new Vector3(minX, 0f, maxZ),
                new Vector3(maxX, 0f, maxZ),
            });
            mesh.SetTriangles(new[] { 0, 2, 1, 2, 3, 1 }, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        // One texel per axial (q, r) inside the map's own bounding box — sized generously (a
        // rectangle over an inherently diamond/hex-shaped coordinate range), the slack texels
        // outside the real grid are simply never sampled by anything that draws (no terrain
        // exists there to darken) — EXCEPT the shader's own neighbour look-up at the true map
        // edge (Custom/FogOfWar.shader's sampleHexFog), which can step one axial unit past this
        // bounding box. With no padding, that UV lands outside [0,1] and the mask's Clamp wrap
        // silently reuses an unrelated texel from the opposite edge/corner of the texture as
        // that "neighbour"'s fog value, producing a patchy blend right on the map's outer rim.
        // Padding the mask by one always-fogged texel ring absorbs every such look-up (each
        // neighbour direction is at most 1 step in q and 1 in r) with an explicit, correct
        // value instead of a wrapped/clamped guess.
        private void BuildMask(HexMap map)
        {
            int maxQ = int.MinValue, maxR = int.MinValue;
            int minQ = int.MaxValue, minR = int.MaxValue;
            foreach (HexCoord coord in map.AllCoords)
            {
                minQ = Mathf.Min(minQ, coord.Q);
                maxQ = Mathf.Max(maxQ, coord.Q);
                minR = Mathf.Min(minR, coord.R);
                maxR = Mathf.Max(maxR, coord.R);
            }

            _minQ = minQ - 1;
            _minR = minR - 1;
            _maskWidth = Mathf.Max(1, maxQ - minQ + 1) + 2;
            _maskHeight = Mathf.Max(1, maxR - minR + 1) + 2;
            // Defaults to all-zero (= fully fogged, see RefreshVisibility's byte convention),
            // which is exactly what both the padding ring and any phantom bounding-box texel
            // (a rectangle over a diamond-shaped coordinate range never covers every texel with
            // a real hex) should read as.
            _maskPixels = new byte[_maskWidth * _maskHeight];

            // Bilinear (not Point) is what makes Custom/FogOfWar.shader's own continuous-UV
            // sampling actually blend smoothly between neighbouring hexes' mask values instead
            // of jumping at each hex boundary — see the shader's own comment on why a
            // hard-edged mask would defeat that entirely.
            _mask = new Texture2D(_maskWidth, _maskHeight, TextureFormat.R8, mipChain: false)
            {
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
            };
        }

        private void BuildLabels(HexMap map)
        {
            FogOfWarStyle style = gameConfig.fogOfWarStyle;
            Vector2 offset = style != null ? style.coordLabelOffset : Vector2.zero;

            foreach (HexCoord coord in map.AllCoords)
            {
                if (_labels.ContainsKey(coord))
                    continue;

                var labelObject = new GameObject($"HexCoordLabel_{coord.Q}_{coord.R}");
                labelObject.transform.SetParent(map.transform, worldPositionStays: false);
                HexCoordLabel label = labelObject.AddComponent<HexCoordLabel>();
                label.ApplyStyle(style);

                // Same (col, row) convention HexInfoPanelUI already shows ("Hex (col, row)") —
                // not raw axial (q, r) — so this label reads as the same coordinate a player
                // would already recognise from clicking the hex.
                (int col, int row) = coord.ToOffset();
                label.SetCoord(col, row);
                labelObject.transform.position = map.HexToWorld(coord)
                    + new Vector3(offset.x, 0f, offset.y) * map.OuterRadius;

                _labels[coord] = label;
            }
        }

        // Rewrites the mask texture and every label's visited state from VisionSystem's current
        // snapshot for CurrentViewer — called whenever that snapshot could have changed (see
        // OnVisibilityChanged/OnEnable's TurnStateChanged subscription).
        private void RefreshVisibility()
        {
            HexMap map = Map;
            if (map == null || _mask == null)
                return;

            PlayerSetupData viewer = VisionSystem.CurrentViewer;

            bool maskChanged = false;
            foreach (HexCoord coord in map.AllCoords)
            {
                bool visible = VisionSystem.IsVisibleToCurrentViewer(coord);
                int x = coord.Q - _minQ;
                int y = coord.R - _minR;
                int pixelIndex = y * _maskWidth + x;
                byte value = visible ? (byte)255 : (byte)0;
                if (_maskPixels[pixelIndex] != value)
                {
                    _maskPixels[pixelIndex] = value;
                    maskChanged = true;
                }

                if (_labels.TryGetValue(coord, out HexCoordLabel label))
                    label.SetVisited(VisionSystem.IsVisitedByCurrentViewer(coord));
            }

            if (maskChanged)
            {
                _mask.SetPixelData(_maskPixels, 0);
                _mask.Apply(updateMipmaps: false);
            }

            if (_overlayRenderer == null)
                return;

            // Only the runtime visibility data/geometry goes through the property block. Tint,
            // edge erosion/sharpness and patina are intentionally left untouched here — those
            // live entirely on the overlayMaterial asset itself (see its own field comment).
            _overlayRenderer.GetPropertyBlock(_propertyBlock);
            _propertyBlock.SetTexture(VisibilityMaskId, _mask);
            _propertyBlock.SetFloat(OuterRadiusId, map.OuterRadius);
            _propertyBlock.SetVector(MaskMinQRId, new Vector4(_minQ, _minR, 0f, 0f));
            _propertyBlock.SetVector(MaskSizeId, new Vector4(_maskWidth, _maskHeight, 0f, 0f));
            _overlayRenderer.SetPropertyBlock(_propertyBlock);
        }
    }
}
