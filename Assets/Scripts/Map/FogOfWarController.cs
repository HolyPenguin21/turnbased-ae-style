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

        private static readonly int ColorId = Shader.PropertyToID("_Color");
        private static readonly int VisibilityMaskId = Shader.PropertyToID("_VisibilityMask");
        private static readonly int OuterRadiusId = Shader.PropertyToID("_OuterRadius");
        private static readonly int MaskMinQRId = Shader.PropertyToID("_MaskMinQR");
        private static readonly int MaskSizeId = Shader.PropertyToID("_MaskSize");
        private static readonly int EdgeSoftnessId = Shader.PropertyToID("_EdgeSoftness");
        private static readonly int EdgeSharpnessId = Shader.PropertyToID("_EdgeSharpness");
        private static readonly int NoiseScaleId = Shader.PropertyToID("_NoiseScale");
        private static readonly int NoiseSpeedId = Shader.PropertyToID("_NoiseSpeed");
        private static readonly int NoiseTexId = Shader.PropertyToID("_NoiseTex");
        private static readonly int NoiseTexScaleId = Shader.PropertyToID("_NoiseTexScale");
        private static readonly int NoiseTexStrengthId = Shader.PropertyToID("_NoiseTexStrength");

        private HexMap _map;
        private HexMap Map => _map != null ? _map : (_map = GetComponent<HexMap>());

        private MeshRenderer _overlayRenderer;
        private Material _overlayMaterial;
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

                Shader shader = Shader.Find("Custom/FogOfWar");
                if (shader == null)
                    Debug.LogWarning("FogOfWarController: shader 'Custom/FogOfWar' not found.");
                _overlayMaterial = new Material(shader);
                _overlayRenderer.sharedMaterial = _overlayMaterial;
                _propertyBlock = new MaterialPropertyBlock();
            }

            _overlayRenderer.GetComponent<MeshFilter>().mesh = BuildQuadMesh(map);

            FogOfWarStyle style = gameConfig.fogOfWarStyle;
            _overlayRenderer.sortingOrder = style != null ? style.sortingOrder : 4;
        }

        // World-space bounding rectangle over every hex centre, padded by a full hex radius on
        // each side so the overlay's own edge never clips a border hex's own geometry — same
        // vertices-in-local-space convention as HexShaderHighlight/HexClusterHighlight (this
        // object sits at the map's own origin, see BuildOverlayQuad, so local space already IS
        // world space here).
        private static Mesh BuildQuadMesh(HexMap map)
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

            float pad = map.OuterRadius * 1.5f;
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
        // exists there to darken).
        private void BuildMask(HexMap map)
        {
            int maxQ = int.MinValue, maxR = int.MinValue;
            _minQ = int.MaxValue;
            _minR = int.MaxValue;
            foreach (HexCoord coord in map.AllCoords)
            {
                _minQ = Mathf.Min(_minQ, coord.Q);
                maxQ = Mathf.Max(maxQ, coord.Q);
                _minR = Mathf.Min(_minR, coord.R);
                maxR = Mathf.Max(maxR, coord.R);
            }

            _maskWidth = Mathf.Max(1, maxQ - _minQ + 1);
            _maskHeight = Mathf.Max(1, maxR - _minR + 1);
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

            FogOfWarStyle style = gameConfig.fogOfWarStyle;
            _overlayRenderer.GetPropertyBlock(_propertyBlock);
            _propertyBlock.SetColor(ColorId, style != null ? style.color : new Color(0.03f, 0.04f, 0.07f, 0.75f));
            _propertyBlock.SetTexture(VisibilityMaskId, _mask);
            _propertyBlock.SetFloat(OuterRadiusId, map.OuterRadius);
            _propertyBlock.SetVector(MaskMinQRId, new Vector4(_minQ, _minR, 0f, 0f));
            _propertyBlock.SetVector(MaskSizeId, new Vector4(_maskWidth, _maskHeight, 0f, 0f));
            if (style != null)
            {
                _propertyBlock.SetFloat(EdgeSoftnessId, style.edgeSoftness);
                _propertyBlock.SetFloat(EdgeSharpnessId, style.edgeSharpness);
                _propertyBlock.SetFloat(NoiseScaleId, style.edgeNoiseScale);
                _propertyBlock.SetFloat(NoiseSpeedId, style.edgeNoiseSpeed);
                if (style.detailTexture != null)
                    _propertyBlock.SetTexture(NoiseTexId, style.detailTexture);
                _propertyBlock.SetFloat(NoiseTexScaleId, style.detailTextureScale);
                _propertyBlock.SetFloat(NoiseTexStrengthId, style.detailTextureStrength);
            }
            _overlayRenderer.SetPropertyBlock(_propertyBlock);
        }
    }
}
