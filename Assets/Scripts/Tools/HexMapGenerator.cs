using System.Collections.Generic;
using System.Linq;
using Game.Core;
using Game.HexGrid;
using Game.Terrain;
using UnityEngine;
using static Game.Terrain.HexMapLayoutPlanner;

namespace Game.Map
{
    // Scene adapter: selects the starting inputs and builds a mesh from either a new layout
    // or the layout carried by a save. Terrain planning and validation run without the scene.
    [ExecuteAlways]
    [RequireComponent(typeof(HexMap))]
    public class HexMapGenerator : MonoBehaviour
    {
        [SerializeField] private GameConfig gameConfig;

        private MapGenerationSettings Settings => gameConfig.mapGeneration;

        private readonly List<Material> _materialInstances = new List<Material>();
        private Material _groundMaterialInstance;
        private static Shader _hexBlendShader;

        private void OnEnable()
        {
            if (!Application.isPlaying) { Generate(); return; }
            GameSession.InitializeWorld(gameObject.scene.handle.GetRawData());
            try
            {
                if (GameSession.PendingRestore != null) BuildFromLayout(GameSession.PendingRestore.Map);
                else Generate();
            }
            catch (System.Exception ex)
            {
                GameSession.SetupError = "Could not initialize the match map: " + ex.Message;
                Debug.LogError(GameSession.SetupError);
            }
        }

        [ContextMenu("Generate Map")]
        public void Generate()
        {
            if (gameConfig == null)
            {
                Debug.LogWarning("HexMapGenerator: no GameConfig assigned, nothing to generate.");
                return;
            }

            int radius = Application.isPlaying ? GameSession.ResolveMapRadius(Settings.radius) : Settings.radius;
            Biome biome = Application.isPlaying ? GameSession.ResolveBiome() : Biome.Arid;

            if (Settings.ResolveBiome(biome).terrainTypes.Count == 0)
            {
                Debug.LogWarning("HexMapGenerator: no terrain types assigned, nothing to generate.");
                return;
            }

            HexMapLayout layout = Create(Settings, radius, biome, UnityMapGenerationRandom.Shared, message => Debug.LogWarning(message));
            BuildFromLayout(layout);
        }

        private static Color ToColor(MapLayoutColor color) => new Color(color.R, color.G, color.B, color.A);

        // Same pipeline as a newly generated map, with every placement/variant/phase already
        // decided. Validation resolves content and rejects bad layouts before replacing a mesh.
        public void BuildFromLayout(HexMapLayout source)
        {
            HexMapLayout layout = source?.Copy();
            ResolvedLayout plan = Resolve(layout, gameConfig == null ? null : Settings);
            List<HexCoord> allCoords = layout.Cells.Select(c => new HexCoord(c.Q, c.R)).ToList();
            List<TextureVariantSlot> variantSlots = plan.Slots;
            List<PlacedComplex> complexes = plan.Complexes;
            var vertices = new List<Vector3>();
            var normals = new List<Vector3>();
            var uvs = new List<Vector2>();
            var colors = new List<Color>();
            var hexData = new Dictionary<HexCoord, TerrainTypeEntry>();

            // One triangle list per texture variant (submesh) — every hex using the same
            // variant lands in the same submesh, so they share a single draw call. A terrain
            // type with only one texture gets exactly one submesh, same as before; one with
            // several gets several; the descriptor already records each hex's choice.
            var trianglesByVariant = new List<int>[variantSlots.Count];
            for (int i = 0; i < trianglesByVariant.Length; i++)
                trianglesByVariant[i] = new List<int>();

            var bounds = new Bounds(Vector3.zero, Vector3.zero);
            bool boundsInitialized = false;

            foreach (HexCoord coord in allCoords)
            {
                Vector3 center = HexGridMath.AxialToWorld(coord.Q, coord.R, layout.OuterRadius);
                int variantSlot = plan.CellSlots[coord];
                int typeIndex = variantSlots[variantSlot].TypeIndex;
                int rotationSteps = plan.Rotations.TryGetValue(coord, out int rotation) ? rotation : 0;
                HexTileMeshGenerator.AppendFlatHexFace(vertices, normals, uvs, colors, trianglesByVariant[variantSlot], center, layout.OuterRadius, layout.Blend, layout.Alpha,
                    rotationSteps: rotationSteps);
                hexData[coord] = plan.Biome.terrainTypes[typeIndex];

                if (!boundsInitialized) { bounds = new Bounds(center, Vector3.zero); boundsInitialized = true; }
                else bounds.Encapsulate(center);
            }

            // Decorative border: same mesh/submesh pipeline as the playfield above, just
            // darkened and never written into hexData, so HexMap has no record of these coords
            // (non-interactive) and the camera's own centring — always the origin now, see
            // RtsCameraController — is unaffected. groundBounds (not bounds) drives the ground
            // plane, so it extends far enough that the border never runs out onto bare background.
            Bounds groundBounds = bounds;
            List<HexCoord> borderCoords = layout.BorderCells.Select(c => new HexCoord(c.Q, c.R)).ToList();
            foreach (HexCoord coord in borderCoords)
            {
                Vector3 center = HexGridMath.AxialToWorld(coord.Q, coord.R, layout.OuterRadius);
                int variantSlot = plan.CellSlots[coord];
                HexTileMeshGenerator.AppendFlatHexFace(vertices, normals, uvs, colors, trianglesByVariant[variantSlot], center, layout.OuterRadius, layout.Blend, layout.Alpha, ToColor(layout.BorderTint));

                groundBounds.Encapsulate(center);
            }

            var mesh = new Mesh { name = "HexMap" };
            if (vertices.Count > 65535)
                mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            mesh.SetVertices(vertices);
            mesh.SetNormals(normals);
            mesh.SetUVs(0, uvs);
            mesh.SetColors(colors);
            mesh.subMeshCount = variantSlots.Count;
            for (int i = 0; i < variantSlots.Count; i++)
                mesh.SetTriangles(trianglesByVariant[i], i);
            mesh.RecalculateBounds();
            mesh.RecalculateTangents();

            var meshFilter = GetComponent<MeshFilter>();
            if (meshFilter.sharedMesh != null)
                DestroyMeshImmediate(meshFilter.sharedMesh);
            meshFilter.sharedMesh = mesh;

            EnsureMaterialInstances(variantSlots, plan.Biome);
            MeshRenderer mapRenderer = GetComponent<MeshRenderer>();
            mapRenderer.sharedMaterials = _materialInstances.ToArray();
            mapRenderer.sortingOrder = MapSortingOrder.Map;
            var animationGroups = new List<MapTerrainAnimator.Group>();
            foreach (PlacedComplex complex in complexes)
            {
                int count = complex.Cells.Length;
                var materials = new Material[count];
                var frames = new Texture2D[count][];
                for (int i = 0; i < count; i++)
                {
                    materials[i] = _materialInstances[complex.FirstSlot + i];
                    frames[i] = complex.Template.parts[i].frames;
                }
                animationGroups.Add(new MapTerrainAnimator.Group
                {
                    Materials = materials, Frames = frames,
                    FramesPerSecond = complex.FramesPerSecond,
                    Phase = complex.Phase,
                });
            }
            MapTerrainAnimator animator = GetComponent<MapTerrainAnimator>();
            if (animator == null) animator = gameObject.AddComponent<MapTerrainAnimator>();
            animator.Configure(animationGroups, _materialInstances.ToArray());

            GenerateGround(groundBounds, layout.OuterRadius, ToColor(layout.GroundColor));

            GetComponent<HexMap>().SetData(layout.FieldRadius, layout.OuterRadius, hexData);
            GetComponent<HexMap>().SetLayout(layout);
            GetComponent<HexMap>().PublishTerrainLayout();

            // Shows every hex's own resource yield right away, before any citadel exists —
            // MapResourceDisplay (not this generator, which self-destructs) owns updating it
            // afterwards when a citadel gets built on a hex. Play-mode only: this method runs
            // in the editor too (ExecuteAlways), and Instantiate() there creates real, saved
            // scene objects instead of temporary ones — spawning icons on every edit-time
            // regenerate would leave permanent duplicate clones behind in the scene.
            if (Application.isPlaying)
            {
                MapResourceDisplay resourceDisplay = GetComponent<MapResourceDisplay>();
                if (resourceDisplay != null)
                    resourceDisplay.RefreshAll();

                // Same reasoning as resourceDisplay above — builds the fog overlay quad, the
                // visibility mask, and every hex's coordinate label, all of which need the real
                // hex data SetData just populated.
                FogOfWarController fogOfWar = GetComponent<FogOfWarController>();
                if (fogOfWar != null)
                    fogOfWar.RefreshAll();

                // Public weather layer; follows HexMap.Weather on its own from here on.
                SandstormMapLayer sandstormLayer = GetComponent<SandstormMapLayer>();
                if (sandstormLayer == null) sandstormLayer = gameObject.AddComponent<SandstormMapLayer>();
                sandstormLayer.Configure(gameConfig.sandstormVisualStyle);
            }

            // Editor-time regeneration (tuning textures/blend/etc.) must keep working, so only
            // remove the tool once the game is actually running.
            if (Application.isPlaying)
                Destroy(this);
        }

        public static void ValidateLayout(HexMapLayout layout, GameConfig config)
            => Validate(layout, config == null ? null : config.mapGeneration);

        // --- Mesh/material/ground plumbing (unchanged by the placement rules above) ------

        private void EnsureMaterialInstances(List<TextureVariantSlot> variantSlots, BiomeTerrainSet biome)
        {
            List<TerrainTypeEntry> terrainTypes = biome.terrainTypes;

            if (_hexBlendShader == null)
                _hexBlendShader = Shader.Find("Custom/HexBlend");

            while (_materialInstances.Count < variantSlots.Count)
                _materialInstances.Add(new Material(_hexBlendShader));
            if (_materialInstances.Count > variantSlots.Count)
                _materialInstances.RemoveRange(variantSlots.Count, _materialInstances.Count - variantSlots.Count);

            for (int i = 0; i < variantSlots.Count; i++)
            {
                TerrainTypeEntry type = terrainTypes[variantSlots[i].TypeIndex];
                _materialInstances[i].name = string.IsNullOrEmpty(type.terrainName) ? $"Terrain_{variantSlots[i].TypeIndex}_{i}" : $"{type.terrainName}_{i}";
                if (variantSlots[i].Texture != null)
                    variantSlots[i].Texture.wrapMode = TextureWrapMode.Clamp;
                _materialInstances[i].mainTexture = variantSlots[i].Texture;
            }
        }

        // A single flat quad behind the whole map, in a solid colour matching the terrain
        // palette — this is what every hex's edge fades into.
        private void GenerateGround(Bounds tileBounds, float outerRadius, Color groundColor)
        {
            var groundTransform = transform.Find("Ground");
            GameObject groundObject;
            if (groundTransform == null)
            {
                groundObject = new GameObject("Ground");
                groundObject.transform.SetParent(transform, worldPositionStays: false);
                groundObject.AddComponent<MeshFilter>();
                groundObject.AddComponent<MeshRenderer>();
                groundObject.AddComponent<BoxCollider>();
            }
            else
            {
                groundObject = groundTransform.gameObject;
            }

            float margin = outerRadius * 2f;
            float sizeX = tileBounds.size.x + margin * 2f;
            float sizeZ = tileBounds.size.z + margin * 2f;
            Vector3 center = tileBounds.center;
            center.y = -0.01f; // the one exception to "everything sits flat at Y=0" — sits just under the hex layer to avoid z-fighting

            var vertices = new[]
            {
                center + new Vector3(-sizeX / 2f, 0f, -sizeZ / 2f),
                center + new Vector3(-sizeX / 2f, 0f, sizeZ / 2f),
                center + new Vector3(sizeX / 2f, 0f, sizeZ / 2f),
                center + new Vector3(sizeX / 2f, 0f, -sizeZ / 2f),
            };
            var triangles = new[] { 0, 1, 2, 0, 2, 3 };
            var normals = new[] { Vector3.up, Vector3.up, Vector3.up, Vector3.up };

            var groundMesh = new Mesh { name = "Ground" };
            groundMesh.SetVertices(vertices);
            groundMesh.SetNormals(normals);
            groundMesh.SetTriangles(triangles, 0);
            groundMesh.RecalculateBounds();

            var groundFilter = groundObject.GetComponent<MeshFilter>();
            if (groundFilter.sharedMesh != null)
                DestroyMeshImmediate(groundFilter.sharedMesh);
            groundFilter.sharedMesh = groundMesh;

            if (_groundMaterialInstance == null)
                _groundMaterialInstance = new Material(Shader.Find("Universal Render Pipeline/Unlit"));
            _groundMaterialInstance.color = groundColor;
            groundObject.GetComponent<MeshRenderer>().sharedMaterial = _groundMaterialInstance;

            // No longer needed for hex picking (see HexSelectionController.RaycastHex/
            // CitadelSetupController.Update, both a math Y=0 plane intersection now instead of
            // a Physics.Raycast against this) — kept anyway as harmless baked geometry in case
            // something else ever wants a physical hit-test against the ground plane. Centre
            // matches the ground quad's own vertices (baked directly into the mesh, not via
            // transform.position).
            var boxCollider = groundObject.GetComponent<BoxCollider>();
            if (boxCollider == null)
                boxCollider = groundObject.AddComponent<BoxCollider>();
            boxCollider.center = center;
            boxCollider.size = new Vector3(sizeX, 0.05f, sizeZ);
        }

        private static void DestroyMeshImmediate(Mesh mesh)
        {
            if (Application.isPlaying) Destroy(mesh);
            else DestroyImmediate(mesh);
        }
    }
}

