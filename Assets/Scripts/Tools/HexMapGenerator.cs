using System.Collections.Generic;
using Game.Core;
using Game.HexGrid;
using Game.Terrain;
using UnityEngine;

namespace Game.Map
{
    // Builds the entire hex map as a single combined mesh on this GameObject's HexMap
    // component — one submesh per terrain type (not per tile), so a 12x9 map costs at most 8
    // draw calls total instead of 108 separate GameObjects.
    //
    // Every hex starts as a weighted-random pick from one shared pool covering every terrain
    // type (see TerrainTypeEntry.baselineWeight) — any type can carry resources or a high move
    // cost, there's no separate "resource"/"impassable" placement path any more. The one
    // exception is whichever type Settings.mountainsTerrainName points to: instead of scattering
    // individually, it's placed afterwards as a few connected chains (PlaceMountainRanges),
    // overwriting some of the baseline picks.
    //
    // This is purely a generation TOOL, not where the map's data lives (see HexMap). At
    // runtime, once it has generated the map and written the result into HexMap, it removes
    // itself — the scene is left with only the data, not the tool that produced it. In the
    // editor it keeps behaving normally (OnEnable + the "Generate Map" context menu still
    // regenerate), so the map can still be tuned while working on it.
    //
    // All the tunable numbers (grid size, terrain list, placement rules) live on GameConfig's
    // MapGenerationSettings now, not as fields here — this component just reads them.
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
            Generate();
        }

        [ContextMenu("Generate Map")]
        public void Generate()
        {
            if (gameConfig == null)
            {
                Debug.LogWarning("HexMapGenerator: no GameConfig assigned, nothing to generate.");
                return;
            }

            if (Settings.terrainTypes.Count == 0)
            {
                Debug.LogWarning("HexMapGenerator: no terrain types assigned, nothing to generate.");
                return;
            }

            List<HexCoord> allCoords = BuildCoordList();
            Dictionary<HexCoord, int> assignment = AssignTerrainTypes(allCoords);

            List<TextureVariantSlot> variantSlots = BuildVariantSlots(out List<int>[] slotIndicesByType);

            var vertices = new List<Vector3>();
            var normals = new List<Vector3>();
            var uvs = new List<Vector2>();
            var colors = new List<Color>();
            var hexData = new Dictionary<HexCoord, TerrainTypeEntry>();

            // One triangle list per texture variant (submesh) — every hex using the same
            // variant lands in the same submesh, so they share a single draw call. A terrain
            // type with only one texture gets exactly one submesh, same as before; one with
            // several gets several, and each hex of that type independently rolls which one
            // below.
            var trianglesByVariant = new List<int>[variantSlots.Count];
            for (int i = 0; i < trianglesByVariant.Length; i++)
                trianglesByVariant[i] = new List<int>();

            var bounds = new Bounds(Vector3.zero, Vector3.zero);
            bool boundsInitialized = false;

            foreach (HexCoord coord in allCoords)
            {
                Vector3 center = HexGridMath.AxialToWorld(coord.Q, coord.R, Settings.outerRadius);
                int typeIndex = assignment[coord];
                int variantSlot = PickVariantSlot(slotIndicesByType, typeIndex);
                HexTileMeshGenerator.AppendFlatHexFace(vertices, normals, uvs, colors, trianglesByVariant[variantSlot], center, Settings.outerRadius, Settings.blend, Settings.alpha);
                hexData[coord] = Settings.terrainTypes[typeIndex];

                if (!boundsInitialized) { bounds = new Bounds(center, Vector3.zero); boundsInitialized = true; }
                else bounds.Encapsulate(center);
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

            EnsureMaterialInstances(variantSlots);
            MeshRenderer mapRenderer = GetComponent<MeshRenderer>();
            mapRenderer.sharedMaterials = _materialInstances.ToArray();
            mapRenderer.sortingOrder = gameConfig.mapSortingOrder;

            GenerateGround(bounds);

            GetComponent<HexMap>().SetData(Settings.width, Settings.height, Settings.outerRadius, hexData);

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
            }

            // Editor-time regeneration (tuning textures/blend/etc.) must keep working, so only
            // remove the tool once the game is actually running.
            if (Application.isPlaying)
                Destroy(this);
        }

        // --- Terrain placement -----------------------------------------------------------

        private List<HexCoord> BuildCoordList()
        {
            var result = new List<HexCoord>(Settings.width * Settings.height);
            for (int row = 0; row < Settings.height; row++)
                for (int col = 0; col < Settings.width; col++)
                    result.Add(HexCoord.FromOffset(col, row));
            return result;
        }

        private Dictionary<HexCoord, int> AssignTerrainTypes(List<HexCoord> allCoords)
        {
            List<TerrainTypeEntry> terrainTypes = Settings.terrainTypes;

            int mountainIndex = IndexOfTerrainNamed(Settings.mountainsTerrainName);

            // One shared weighted pool covering every type except the dedicated mountain-range
            // type — a type that should be rare just gets a low baselineWeight, there's no
            // separate placement path to opt into any more.
            var poolIndices = new List<int>();
            for (int i = 0; i < terrainTypes.Count; i++)
                if (i != mountainIndex)
                    poolIndices.Add(i);
            if (poolIndices.Count == 0)
                poolIndices.Add(0); // safety net so an all-mountain list can't crash generation

            var poolWeights = new float[poolIndices.Count];
            float poolWeightTotal = 0f;
            for (int i = 0; i < poolIndices.Count; i++)
            {
                poolWeights[i] = Mathf.Max(0.0001f, terrainTypes[poolIndices[i]].baselineWeight);
                poolWeightTotal += poolWeights[i];
            }

            var assignment = new Dictionary<HexCoord, int>(allCoords.Count);
            foreach (HexCoord coord in allCoords)
                assignment[coord] = PickWeightedIndex(poolIndices, poolWeights, poolWeightTotal);

            // Mountain ranges go last, overwriting whatever the baseline fill picked for the
            // hexes they walk through — they need runs of untouched hexes to form a chain.
            if (mountainIndex >= 0)
            {
                var claimed = new HashSet<HexCoord>();
                var coordSet = new HashSet<HexCoord>(allCoords);
                PlaceMountainRanges(assignment, claimed, coordSet, mountainIndex);
            }

            return assignment;
        }

        private static int PickWeightedIndex(List<int> indices, float[] weights, float totalWeight)
        {
            float roll = Random.value * totalWeight;
            float cumulative = 0f;
            for (int i = 0; i < indices.Count; i++)
            {
                cumulative += weights[i];
                if (roll <= cumulative)
                    return indices[i];
            }
            return indices[indices.Count - 1]; // float rounding safety net
        }

        // Random-walk a chain of hexes, mostly continuing in the same direction with an
        // occasional gentle turn, so ranges read as elongated chains rather than blobs.
        private void PlaceMountainRanges(Dictionary<HexCoord, int> assignment, HashSet<HexCoord> claimed, HashSet<HexCoord> coordSet, int mountainIndex)
        {
            for (int i = 0; i < Settings.mountainRangeCount; i++)
            {
                HexCoord? start = PickRandomUnclaimed(coordSet, claimed);
                if (!start.HasValue)
                    break; // no room left on the map

                HexCoord current = start.Value;
                int dirIndex = Random.Range(0, HexGridMath.NeighborDirectionsByEdge.Length);
                int length = Mathf.Max(1, Settings.mountainRangeLength + Random.Range(-1, 2));

                for (int step = 0; step < length; step++)
                {
                    if (!coordSet.Contains(current) || claimed.Contains(current))
                        break; // ran off the map, or into another claimed feature — stop here

                    assignment[current] = mountainIndex;
                    claimed.Add(current);

                    if (Random.value < 0.3f)
                        dirIndex = (dirIndex + (Random.value < 0.5f ? 1 : 5)) % 6; // +1 or -1, wrapped

                    (int dq, int dr) = HexGridMath.NeighborDirectionsByEdge[dirIndex];
                    current = new HexCoord(current.Q + dq, current.R + dr);
                }
            }
        }

        private int IndexOfTerrainNamed(string name)
        {
            List<TerrainTypeEntry> terrainTypes = Settings.terrainTypes;
            for (int i = 0; i < terrainTypes.Count; i++)
                if (string.Equals(terrainTypes[i].terrainName, name, System.StringComparison.OrdinalIgnoreCase))
                    return i;
            return -1;
        }

        // Used for mountain-range starting points — every unclaimed hex is fair game.
        private static HexCoord? PickRandomUnclaimed(HashSet<HexCoord> coordSet, HashSet<HexCoord> claimed)
        {
            // Scan from a random starting point rather than reshuffling a list each call.
            var coords = new List<HexCoord>(coordSet);
            int startIndex = Random.Range(0, coords.Count);
            for (int i = 0; i < coords.Count; i++)
            {
                HexCoord candidate = coords[(startIndex + i) % coords.Count];
                if (!claimed.Contains(candidate))
                    return candidate;
            }
            return null;
        }

        // --- Texture variants (one submesh per texture actually in use, not per terrain type) --

        // Which terrain type a given submesh renders, and which of that type's textures it
        // uses — a type with N textures occupies N consecutive-ish slots, one per texture.
        private readonly struct TextureVariantSlot
        {
            public readonly int TypeIndex;
            public readonly Texture2D Texture;
            public TextureVariantSlot(int typeIndex, Texture2D texture) { TypeIndex = typeIndex; Texture = texture; }
        }

        private List<TextureVariantSlot> BuildVariantSlots(out List<int>[] slotIndicesByType)
        {
            List<TerrainTypeEntry> terrainTypes = Settings.terrainTypes;
            var slots = new List<TextureVariantSlot>();
            slotIndicesByType = new List<int>[terrainTypes.Count];

            for (int typeIndex = 0; typeIndex < terrainTypes.Count; typeIndex++)
            {
                slotIndicesByType[typeIndex] = new List<int>();
                List<Texture2D> textures = terrainTypes[typeIndex].GetAllTextures();
                if (textures.Count == 0)
                    textures.Add(null); // still needs exactly one submesh even with nothing assigned

                foreach (Texture2D tex in textures)
                {
                    slotIndicesByType[typeIndex].Add(slots.Count);
                    slots.Add(new TextureVariantSlot(typeIndex, tex));
                }
            }

            return slots;
        }

        // Independent random roll per hex among that terrain type's texture variants.
        private static int PickVariantSlot(List<int>[] slotIndicesByType, int typeIndex)
        {
            List<int> slots = slotIndicesByType[typeIndex];
            return slots[Random.Range(0, slots.Count)];
        }

        // --- Mesh/material/ground plumbing (unchanged by the placement rules above) ------

        private void EnsureMaterialInstances(List<TextureVariantSlot> variantSlots)
        {
            List<TerrainTypeEntry> terrainTypes = Settings.terrainTypes;

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
        private void GenerateGround(Bounds tileBounds)
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

            float margin = Settings.outerRadius * 2f;
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
            _groundMaterialInstance.color = Settings.groundColor;
            groundObject.GetComponent<MeshRenderer>().sharedMaterial = _groundMaterialInstance;

            // Thin invisible-height box so raycasts (hex picking) can hit the map without
            // needing a collider on the combined hex mesh itself. Centre matches the ground
            // quad's own vertices (baked directly into the mesh, not via transform.position).
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
