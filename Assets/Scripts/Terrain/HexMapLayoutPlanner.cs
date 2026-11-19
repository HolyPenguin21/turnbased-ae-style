using System.Collections.Generic;
using System.Linq;
using Game.HexGrid;
using Game.Map;
using UnityEngine;

namespace Game.Terrain
{
    // Plans an entire terrain layout before the renderer touches the scene. Replay resolves
    // the saved identities and phases using the same variant table, without using an RNG.
    public sealed class HexMapLayoutPlanner
    {
        private readonly MapGenerationSettings Settings;
        private readonly int _activeRadius;
        private readonly BiomeTerrainSet _activeBiome;
        private readonly IMapGenerationRandom random;
        private readonly System.Action<string> warning;

        private HexMapLayoutPlanner(MapGenerationSettings settings, int radius, Biome biome, IMapGenerationRandom random,
            System.Action<string> warning)
        {
            Settings = settings;
            _activeRadius = radius;
            _activeBiome = settings.ResolveBiome(biome);
            this.random = random ?? throw new System.ArgumentNullException(nameof(random));
            this.warning = warning;
        }

        public static HexMapLayout Create(MapGenerationSettings settings, int radius, Biome biome,
            IMapGenerationRandom random, System.Action<string> warning = null)
            => new HexMapLayoutPlanner(settings, radius, biome, random, warning).CreateLayout(biome);

        public static void Validate(HexMapLayout layout, MapGenerationSettings settings) => Resolve(layout, settings);

        private HexMapLayout CreateLayout(Biome requested)
        {
            List<HexCoord> allCoords = BuildCoordList();
            Dictionary<HexCoord, int> assignment = AssignTerrainTypes(allCoords);
            List<TextureVariantSlot> slots = BuildVariantSlots(out List<int>[] slotIndicesByType);
            List<PlacedComplex> complexes = PlaceComplexes(allCoords, assignment);
            var complexTextures = new Dictionary<HexCoord, Texture2D>();
            foreach (PlacedComplex complex in complexes)
                for (int part = 0; part < complex.Cells.Length; part++)
                    complexTextures.Add(complex.Cells[part], complex.Template.parts[part].frames[0]);
            var chosen = new Dictionary<HexCoord, Texture2D>();
            var cells = new List<MapLayoutCell>();
            foreach (HexCoord coord in allCoords)
            {
                int typeIndex = assignment[coord];
                TerrainTypeEntry terrain = _activeBiome.terrainTypes[typeIndex];
                bool complexPart = complexTextures.TryGetValue(coord, out Texture2D texture);
                if (!complexPart)
                    texture = slots[PickVariantSlot(slotIndicesByType, typeIndex, slots,
                        CollectNeighborTextures(coord, typeIndex, assignment, chosen))].Texture;
                chosen[coord] = texture;
                cells.Add(Cell(coord, terrain.ContentKey, complexPart ? null : terrain.GetTextureKey(texture)));
            }
            List<HexCoord> border = BuildBorderCoords(out Dictionary<HexCoord, int> borderAssignment);
            var borderChosen = new Dictionary<HexCoord, Texture2D>();
            var borderCells = new List<MapLayoutCell>();
            foreach (HexCoord coord in border)
            {
                int typeIndex = borderAssignment[coord];
                TerrainTypeEntry terrain = _activeBiome.terrainTypes[typeIndex];
                Texture2D texture = slots[PickVariantSlot(slotIndicesByType, typeIndex, slots,
                    CollectNeighborTextures(coord, typeIndex, borderAssignment, borderChosen))].Texture;
                borderChosen[coord] = texture;
                borderCells.Add(Cell(coord, terrain.ContentKey, terrain.GetTextureKey(texture)));
            }
            var totals = complexes.GroupBy(c => c.Template).ToDictionary(g => g.Key, g => g.Count());
            var seen = new Dictionary<TerrainComplexTemplate, int>();
            var offsets = new Dictionary<TerrainComplexTemplate, int>();
            var complexLayouts = new List<MapLayoutComplex>();
            foreach (PlacedComplex complex in complexes)
            {
                int instance = seen.TryGetValue(complex.Template, out int n) ? n : 0;
                seen[complex.Template] = instance + 1;
                int frameCount = complex.Template.parts[0].frames.Length;
                if (!offsets.TryGetValue(complex.Template, out int offset))
                    offsets[complex.Template] = offset = random.Range(0, frameCount);
                complexLayouts.Add(new MapLayoutComplex
                {
                    TemplateKey = complex.Template.ContentKey, RotationSteps = complex.RotationSteps,
                    Phase = (offset + instance * frameCount / totals[complex.Template]) % frameCount,
                    FramesPerSecond = complex.Template.framesPerSecond,
                    Parts = complex.Cells.Select(c => Cell(c, _activeBiome.terrainTypes[complex.TypeIndex].ContentKey, null)).ToArray(),
                });
            }
            Biome actual = requested == Biome.Desert && Settings.desertOverride?.terrainTypes?.Count > 0
                ? Biome.Desert : Biome.Arid;
            return new HexMapLayout
            {
                FieldRadius = _activeRadius, OuterRadius = Settings.outerRadius, Biome = actual,
                Blend = Settings.blend, Alpha = Settings.alpha,
                GroundColor = FromColor(_activeBiome.groundColor), BorderTint = FromColor(_activeBiome.borderTint),
                Cells = cells.ToArray(), BorderCells = borderCells.ToArray(), Complexes = complexLayouts.ToArray(),
            };
        }

        private static MapLayoutCell Cell(HexCoord coord, string terrainKey, string textureKey) =>
            new MapLayoutCell { Q = coord.Q, R = coord.R, TerrainKey = terrainKey, TextureKey = textureKey };
        private static MapLayoutColor FromColor(Color color) => new MapLayoutColor(color.r, color.g, color.b, color.a);

        internal sealed class ResolvedLayout
        {
            public BiomeTerrainSet Biome;
            public List<TextureVariantSlot> Slots;
            public readonly Dictionary<HexCoord, int> CellSlots = new Dictionary<HexCoord, int>();
            public readonly Dictionary<HexCoord, int> Rotations = new Dictionary<HexCoord, int>();
            public readonly List<PlacedComplex> Complexes = new List<PlacedComplex>();
        }

        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        private static bool ValidColor(MapLayoutColor color) =>
            Finite(color.R) && Finite(color.G) && Finite(color.B) && Finite(color.A);
        private static long DistanceFromOrigin(MapLayoutCell cell) => System.Math.Max(
            System.Math.Max(System.Math.Abs((long)cell.Q), System.Math.Abs((long)cell.R)),
            System.Math.Abs((long)cell.Q + cell.R));
        private static System.ArgumentException InvalidLayout(string detail) =>
            new System.ArgumentException("Cannot restore map layout: " + detail);

        private static void ValidateLayoutGeometry(HexMapLayout layout)
        {
            float minX = float.MaxValue, minZ = float.MaxValue;
            float maxX = float.MinValue, maxZ = float.MinValue;
            foreach (var cell in layout.Cells.Concat(layout.BorderCells))
            {
                var center = HexGridMath.AxialToWorld(cell.Q, cell.R, layout.OuterRadius);
                float left = center.x - layout.OuterRadius, right = center.x + layout.OuterRadius;
                float bottom = center.z - layout.OuterRadius, top = center.z + layout.OuterRadius;
                if (!Finite(left) || !Finite(right) || !Finite(bottom) || !Finite(top))
                    throw InvalidLayout("tile geometry overflows world coordinates.");
                minX = System.Math.Min(minX, left); maxX = System.Math.Max(maxX, right);
                minZ = System.Math.Min(minZ, bottom); maxZ = System.Math.Max(maxZ, top);
            }
            // Bounds, ground padding and fog are derived after scene replacement. Finite
            // source fields alone do not guarantee their single-precision arithmetic is finite.
            float padding = layout.OuterRadius * 4f;
            float width = maxX - minX + padding, depth = maxZ - minZ + padding;
            float x = (minX + maxX) * 0.5f, z = (minZ + maxZ) * 0.5f;
            if (!Finite(width) || !Finite(depth) || !Finite(x) || !Finite(z)
                || !Finite(x - width * 0.5f) || !Finite(x + width * 0.5f)
                || !Finite(z - depth * 0.5f) || !Finite(z + depth * 0.5f))
                throw InvalidLayout("map bounds, ground or fog geometry overflows world coordinates.");
        }

        internal static ResolvedLayout Resolve(HexMapLayout layout, MapGenerationSettings settings)
        {
            if (layout == null || settings == null)
                throw InvalidLayout("missing layout or map configuration.");
            if (layout.Version != 1 || layout.FieldRadius < 0 || !Finite(layout.OuterRadius) || layout.OuterRadius <= 0
                || !Finite(layout.Blend) || layout.Blend < 0 || layout.Blend > 1
                || !Finite(layout.Alpha) || layout.Alpha < 0 || layout.Alpha > 1
                || !ValidColor(layout.GroundColor) || !ValidColor(layout.BorderTint)
                || !System.Enum.IsDefined(typeof(Biome), layout.Biome)
                || layout.Cells == null || layout.BorderCells == null || layout.Complexes == null)
                throw InvalidLayout("unsupported version, dimensions or visual settings.");
            long expected = 1L + 3L * layout.FieldRadius * (layout.FieldRadius + 1L);
            if (layout.Cells.LongLength != expected) throw InvalidLayout("incomplete playable footprint.");
            // A saved Desert palette must remain Desert; fallback is only a new-game choice.
            if (layout.Biome == Biome.Desert && (settings.desertOverride?.terrainTypes?.Count ?? 0) <= 0)
                throw InvalidLayout("saved biome is unavailable.");
            BiomeTerrainSet biome = settings.ResolveBiome(layout.Biome);
            if (biome.terrainTypes == null || biome.terrainTypes.Count == 0 || biome.terrainTypes.Any(t => t == null))
                throw InvalidLayout("terrain catalog is empty or contains null entries.");
            var terrainIndices = new Dictionary<string, int>(System.StringComparer.Ordinal);
            for (int i = 0; i < biome.terrainTypes.Count; i++)
            {
                string key = biome.terrainTypes[i].ContentKey;
                if (terrainIndices.ContainsKey(key)) throw InvalidLayout("duplicate terrain key '" + key + "'.");
                terrainIndices.Add(key, i);
            }
            var plan = new ResolvedLayout { Biome = biome, Slots = BuildVariantSlots(biome, out _) };
            var textureSlots = new Dictionary<(int type, string key), int>();
            for (int i = 0; i < plan.Slots.Count; i++)
            {
                TextureVariantSlot slot = plan.Slots[i];
                var key = (slot.TypeIndex, biome.terrainTypes[slot.TypeIndex].GetTextureKey(slot.Texture));
                if (textureSlots.TryGetValue(key, out int previous))
                {
                    if (plan.Slots[previous].Texture != slot.Texture) throw InvalidLayout("duplicate texture key '" + key.Item2 + "'.");
                }
                else textureSlots.Add(key, i);
            }
            var cells = new Dictionary<HexCoord, MapLayoutCell>();
            foreach (MapLayoutCell cell in layout.Cells.Concat(layout.BorderCells))
            {
                if (cell == null || string.IsNullOrEmpty(cell.TerrainKey) || !terrainIndices.ContainsKey(cell.TerrainKey))
                    throw InvalidLayout("unknown terrain key.");
                var coord = new HexCoord(cell.Q, cell.R);
                if (cells.ContainsKey(coord)) throw InvalidLayout("duplicate coordinate " + coord + ".");
                cells.Add(coord, cell);
            }
            var playable = new HashSet<HexCoord>();
            foreach (MapLayoutCell cell in layout.Cells)
            {
                if (DistanceFromOrigin(cell) > layout.FieldRadius) throw InvalidLayout("playable coordinate outside field.");
                playable.Add(new HexCoord(cell.Q, cell.R));
            }
            foreach (MapLayoutCell cell in layout.BorderCells)
                if (DistanceFromOrigin(cell) <= layout.FieldRadius) throw InvalidLayout("border coordinate inside field.");
            ValidateLayoutGeometry(layout);
            var templates = new Dictionary<string, TerrainComplexTemplate>(System.StringComparer.Ordinal);
            foreach (TerrainComplexTemplate template in biome.complexes)
            {
                if (template == null) continue;
                string key = template.ContentKey;
                if (templates.ContainsKey(key)) throw InvalidLayout("duplicate complex key '" + key + "'.");
                templates.Add(key, template);
            }
            foreach (MapLayoutComplex complex in layout.Complexes)
            {
                if (complex == null || string.IsNullOrEmpty(complex.TemplateKey)
                    || !templates.TryGetValue(complex.TemplateKey, out TerrainComplexTemplate template)
                    || !template.IsValid() || complex.Parts == null || complex.Parts.Length != template.parts.Length
                    || complex.RotationSteps < 0 || complex.RotationSteps > 5
                    || complex.Phase < 0
                    || !Finite(complex.FramesPerSecond) || complex.FramesPerSecond <= 0)
                    throw InvalidLayout("unknown or incompatible complex.");
                var placed = new PlacedComplex
                {
                    // Frame count is art (project owner, 2026-11-04): a saved phase wraps into
                    // the current animation instead of rejecting the save.
                    Template = template, RotationSteps = complex.RotationSteps, Phase = complex.Phase % template.parts[0].frames.Length,
                    FramesPerSecond = complex.FramesPerSecond, Cells = new HexCoord[complex.Parts.Length], FirstSlot = plan.Slots.Count,
                };
                HexCoord? origin = null;
                string terrainKey = null;
                for (int i = 0; i < complex.Parts.Length; i++)
                {
                    MapLayoutCell part = complex.Parts[i];
                    if (part == null) throw InvalidLayout("null complex part.");
                    var coord = new HexCoord(part.Q, part.R);
                    if (!playable.Contains(coord) || !cells.TryGetValue(coord, out MapLayoutCell cell)
                        || cell.TerrainKey != part.TerrainKey || !string.IsNullOrEmpty(cell.TextureKey)
                        || plan.CellSlots.ContainsKey(coord)) throw InvalidLayout("overlapping or mismatched complex part.");
                    terrainKey = terrainKey ?? cell.TerrainKey;
                    if (terrainKey != cell.TerrainKey) throw InvalidLayout("mixed terrain in complex.");
                    HexCoord offset = HexGridMath.RotateOffset60(new HexCoord(template.parts[i].offset.x,
                        template.parts[i].offset.y), complex.RotationSteps);
                    var candidateOrigin = new HexCoord(coord.Q - offset.Q, coord.R - offset.R);
                    if (origin.HasValue && !origin.Value.Equals(candidateOrigin)) throw InvalidLayout("incompatible complex footprint.");
                    origin = candidateOrigin;
                    placed.Cells[i] = coord;
                    placed.TypeIndex = terrainIndices[cell.TerrainKey];
                    plan.CellSlots.Add(coord, plan.Slots.Count);
                    plan.Rotations.Add(coord, complex.RotationSteps);
                    plan.Slots.Add(new TextureVariantSlot(placed.TypeIndex, template.parts[i].frames[0]));
                }
                plan.Complexes.Add(placed);
            }
            foreach (KeyValuePair<HexCoord, MapLayoutCell> entry in cells)
            {
                if (plan.CellSlots.ContainsKey(entry.Key)) continue;
                int typeIndex = terrainIndices[entry.Value.TerrainKey];
                if (string.IsNullOrEmpty(entry.Value.TextureKey)) throw InvalidLayout("missing texture key.");
                // A texture variant is art (project owner, 2026-11-04): one that a patch renamed
                // or removed falls back to the terrain's first variant; the terrain itself stays.
                if (!textureSlots.TryGetValue((typeIndex, entry.Value.TextureKey), out int slot))
                {
                    slot = -1;
                    foreach (var known in textureSlots)
                        if (known.Key.type == typeIndex && (slot < 0 || known.Value < slot)) slot = known.Value;
                    if (slot < 0) throw InvalidLayout("terrain has no texture variant.");
                }
                plan.CellSlots.Add(entry.Key, slot);
            }
            return plan;
        }

        // --- Terrain placement -----------------------------------------------------------

        // A hexagon of hexes around the field's (0,0) centre, not a rectangle — HexGridMath
        // already has exactly this shape (used elsewhere for vision/supply ranges).
        private List<HexCoord> BuildCoordList()
        {
            return HexGridMath.HexesInRange(new HexCoord(0, 0), _activeRadius).ToList();
        }

        private Dictionary<HexCoord, int> AssignTerrainTypes(List<HexCoord> allCoords)
        {
            (List<int> poolIndices, float[] poolWeights, float poolWeightTotal) = BuildBaselinePool(out int mountainIndex);

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

        // Same weighted pool AssignTerrainTypes uses for the playfield (every type except the
        // dedicated mountain-range type — a type that should be rare just gets a low
        // baselineWeight, there's no separate placement path to opt into any more), pulled out
        // so the decorative border can roll from the exact same distribution without its own
        // mountain-range chains.
        private (List<int> indices, float[] weights, float total) BuildBaselinePool(out int mountainIndex)
        {
            List<TerrainTypeEntry> terrainTypes = _activeBiome.terrainTypes;
            mountainIndex = IndexOfTerrainNamed(_activeBiome.mountainsTerrainName);

            var poolIndices = new List<int>();
            for (int i = 0; i < terrainTypes.Count; i++)
                if (i != mountainIndex && terrainTypes[i].baselineWeight > 0f
                    && !_activeBiome.complexes.Any(t => t != null
                        && string.Equals(t.terrainName, terrainTypes[i].terrainName, System.StringComparison.OrdinalIgnoreCase)))
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

            return (poolIndices, poolWeights, poolWeightTotal);
        }

        // Decorative, non-interactive hexes past the field's own outer ring: same terrain pool
        // as the playfield (no mountain ranges — those are a gameplay feature), thinning out
        // raggedly with distance via per-hex Perlin noise so the cutoff isn't a clean ring.
        // Never added to allCoords/hexData, so HexMap has no record of them and they can't be
        // selected, pathed to, or seen by anything gameplay-side.
        private List<HexCoord> BuildBorderCoords(out Dictionary<HexCoord, int> borderAssignment)
        {
            borderAssignment = new Dictionary<HexCoord, int>();

            float borderDepthWorld = Settings.ComputeBorderDepthWorld(_activeRadius);

            int marginRings = Mathf.CeilToInt(borderDepthWorld / (Settings.outerRadius * Mathf.Sqrt(3f)));
            if (marginRings <= 0)
                return new List<HexCoord>();

            (List<int> poolIndices, float[] poolWeights, float poolWeightTotal) = BuildBaselinePool(out _);

            // Randomised per-generation so the ragged cutoff pattern differs between maps
            // instead of always fraying at the same spots relative to the grid.
            var noiseOffset = new Vector2(random.Range(0f, 1000f), random.Range(0f, 1000f));

            var origin = new HexCoord(0, 0);
            var coords = new List<HexCoord>();
            foreach (HexCoord coord in HexGridMath.HexesInRange(origin, _activeRadius + marginRings))
            {
                int distance = HexGridMath.Distance(origin, coord);
                if (distance <= _activeRadius)
                    continue; // inside the playfield itself

                if (!IncludeBorderHex(distance, marginRings, coord, noiseOffset))
                    continue;

                coords.Add(coord);
                borderAssignment[coord] = PickWeightedIndex(poolIndices, poolWeights, poolWeightTotal);
            }

            return coords;
        }

        // 0 at the field's own outer ring, 1 at the far edge of the border depth. Perturbed by
        // per-hex Perlin noise before the raggedness cutoff, so hexes drop out increasingly
        // often (rather than at a fixed radius) the further out they sit.
        private bool IncludeBorderHex(int distance, int marginRings, HexCoord coord, Vector2 noiseOffset)
        {
            float depthNorm = (distance - _activeRadius) / (float)marginRings;

            float noise = Mathf.PerlinNoise((coord.Q + noiseOffset.x) * Settings.borderNoiseScale, (coord.R + noiseOffset.y) * Settings.borderNoiseScale);
            float raggedDepth = depthNorm + (noise - 0.5f) * Settings.borderRaggedness;
            return raggedDepth <= 1f;
        }

        private int PickWeightedIndex(List<int> indices, float[] weights, float totalWeight)
        {
            float roll = random.Value * totalWeight;
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
            for (int i = 0; i < _activeBiome.mountainRangeCount; i++)
            {
                HexCoord? start = PickRandomUnclaimed(coordSet, claimed);
                if (!start.HasValue)
                    break; // no room left on the map

                HexCoord current = start.Value;
                int dirIndex = random.Range(0, HexGridMath.NeighborDirectionsByEdge.Length);
                int length = Mathf.Max(1, _activeBiome.mountainRangeLength + random.Range(-1, 2));

                for (int step = 0; step < length; step++)
                {
                    if (!coordSet.Contains(current) || claimed.Contains(current))
                        break; // ran off the map, or into another claimed feature — stop here

                    assignment[current] = mountainIndex;
                    claimed.Add(current);

                    if (random.Value < 0.3f)
                        dirIndex = (dirIndex + (random.Value < 0.5f ? 1 : 5)) % 6; // +1 or -1, wrapped

                    (int dq, int dr) = HexGridMath.NeighborDirectionsByEdge[dirIndex];
                    current = new HexCoord(current.Q + dq, current.R + dr);
                }
            }
        }

        private int IndexOfTerrainNamed(string name)
        {
            List<TerrainTypeEntry> terrainTypes = _activeBiome.terrainTypes;
            for (int i = 0; i < terrainTypes.Count; i++)
                if (string.Equals(terrainTypes[i].terrainName, name, System.StringComparison.OrdinalIgnoreCase))
                    return i;
            return -1;
        }

        // Used for mountain-range starting points — every unclaimed hex is fair game.
        private HexCoord? PickRandomUnclaimed(HashSet<HexCoord> coordSet, HashSet<HexCoord> claimed)
        {
            // Scan from a random starting point rather than reshuffling a list each call.
            var coords = new List<HexCoord>(coordSet);
            int startIndex = random.Range(0, coords.Count);
            for (int i = 0; i < coords.Count; i++)
            {
                HexCoord candidate = coords[(startIndex + i) % coords.Count];
                if (!claimed.Contains(candidate))
                    return candidate;
            }
            return null;
        }

        internal sealed class PlacedComplex
        {
            public TerrainComplexTemplate Template;
            public int RotationSteps;
            public HexCoord[] Cells;
            public int TypeIndex;
            public int FirstSlot;
            public int Phase;
            public float FramesPerSecond;
        }

        private List<PlacedComplex> PlaceComplexes(List<HexCoord> allCoords, Dictionary<HexCoord, int> assignment)
        {
            var result = new List<PlacedComplex>();
            var claimed = new HashSet<HexCoord>();
            var valid = new List<(TerrainComplexTemplate template, int typeIndex)>();
            foreach (TerrainComplexTemplate template in _activeBiome.complexes)
            {
                // Exclude disabled templates before allocation so they cannot consume a share
                // of the map's total or trigger placement attempts.
                if (template != null && !template.useInGeneration) continue;
                int typeIndex = template == null ? -1 : IndexOfTerrainNamed(template.terrainName);
                if (typeIndex < 0 || !template.IsValid())
                {
                    warning?.Invoke($"HexMapGenerator: skipped invalid complex '{template?.name ?? "null"}'.");
                    continue;
                }
                valid.Add((template, typeIndex));
            }
            // Alternatives (e.g. canyon OR giant machine wreck) are decided once per map, after
            // disabled templates are gone, so a disabled alternative never takes the draw.
            List<int> keep = TerrainComplexPlacement.ChooseFromExclusiveGroups(
                valid.Select(v => v.template).ToList(), n => random.Range(0, n));
            valid = keep.Select(i => valid[i]).ToList();
            // Settings.complexCount is the map's total; each template's count is its share of it.
            int[] instances = TerrainComplexPlacement.AllocateInstances(
                valid.Select(v => v.template.count).ToList(), Settings.complexCount, n => random.Range(0, n));
            for (int t = 0; t < valid.Count; t++)
            {
                (TerrainComplexTemplate template, int typeIndex) = valid[t];
                for (int instance = 0; instance < instances[t]; instance++)
                {
                    bool placed = false;
                    // One uniformly chosen orientation per instance, kept across retries.
                    int rotationSteps = TerrainComplexPlacement.ChooseRotationSteps(template, n => random.Range(0, n));
                    // Origins come from the band the footprint must fit in (footprint offsets reach
                    // at most 2 rings from the origin), so a narrow "near the middle" band is not
                    // left to the luck of 64 uniform rolls over the whole map.
                    TerrainComplexPlacement.CenterDistanceRange(template,
                        _activeBiome.terrainTypes[typeIndex].blocksGroundMovement, _activeRadius,
                        Settings.impassableEdgeMarginRings, out int minCenter, out int maxCenter);
                    List<HexCoord> origins = allCoords.FindAll(h =>
                    {
                        int d = HexGridMath.Distance(new HexCoord(0, 0), h);
                        return d >= minCenter - 2 && d <= maxCenter + 2;
                    });
                    for (int attempt = 0; attempt < template.placementAttempts && origins.Count > 0; attempt++)
                    {
                        HexCoord origin = origins[random.Range(0, origins.Count)];
                        bool Protected(HexCoord h) => BuildingRegistry.FindAt(h) != null
                            || ArmyRegistry.AllAt(h).Any() || HexEventRegistry.FindAt(h) != null
                            || HexResourceBonusRegistry.GetBonus(h) != null;
                        if (!TerrainComplexPlacement.TryValidate(template, origin, assignment,
                            _activeBiome.terrainTypes, typeIndex, claimed, Protected, out HexCoord[] cells,
                            minCenter, maxCenter, rotationSteps)) continue;
                        // All rejection paths above leave the original assignment unchanged.
                        foreach (HexCoord h in cells) { assignment[h] = typeIndex; claimed.Add(h); }
                        result.Add(new PlacedComplex
                        { Template = template, Cells = cells, TypeIndex = typeIndex, RotationSteps = rotationSteps });
                        placed = true;
                        break;
                    }
                    if (!placed)
                        warning?.Invoke($"HexMapGenerator: skipped whole complex '{template.name}' instance {instance + 1} "
                            + $"after {template.placementAttempts} attempts (footprint, protected terrain/content or ground connectivity). "
                            + $"radius={_activeRadius}.");
                }
            }
            return result;
        }

        // --- Texture variants (one submesh per texture actually in use, not per terrain type) --

        // Which terrain type a given submesh renders, and which of that type's textures it
        // uses — a type with N textures occupies N consecutive-ish slots, one per texture.
        internal readonly struct TextureVariantSlot
        {
            public readonly int TypeIndex;
            public readonly Texture2D Texture;
            public TextureVariantSlot(int typeIndex, Texture2D texture) { TypeIndex = typeIndex; Texture = texture; }
        }

        private List<TextureVariantSlot> BuildVariantSlots(out List<int>[] slotIndicesByType) =>
            BuildVariantSlots(_activeBiome, out slotIndicesByType);

        private static List<TextureVariantSlot> BuildVariantSlots(BiomeTerrainSet biome, out List<int>[] slotIndicesByType)
        {
            List<TerrainTypeEntry> terrainTypes = biome.terrainTypes;
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

        // Every same-type hex already placed next to this one, before this hex's own variant is
        // rolled (HexesInRange's own enumeration order means only SOME neighbours are already in
        // chosenTexture by the time a given hex is reached, never all 6 — this only ever softens
        // repeats, it doesn't guarantee none, per the project owner's own "nice to have, not
        // critical" call).
        private static HashSet<Texture2D> CollectNeighborTextures(HexCoord coord, int typeIndex, Dictionary<HexCoord, int> assignment, Dictionary<HexCoord, Texture2D> chosenTexture)
        {
            HashSet<Texture2D> result = null;
            foreach ((int dq, int dr) in HexGridMath.NeighborDirectionsByEdge)
            {
                var neighbor = new HexCoord(coord.Q + dq, coord.R + dr);
                if (!chosenTexture.TryGetValue(neighbor, out Texture2D neighborTexture))
                    continue;
                if (!assignment.TryGetValue(neighbor, out int neighborType) || neighborType != typeIndex)
                    continue;

                result ??= new HashSet<Texture2D>();
                result.Add(neighborTexture);
            }
            return result;
        }

        // Random roll per hex among that terrain type's texture variants, steered away from
        // whichever variants its already-placed same-type neighbours picked (CollectNeighborTextures)
        // so adjacent hexes read as visually distinct where the type has more than one texture to
        // offer. Falls back to the full pool whenever every variant is already a neighbour's pick
        // (small variant counts, e.g. only 2 textures both already used) so a hex is never left
        // without a texture.
        private int PickVariantSlot(List<int>[] slotIndicesByType, int typeIndex, List<TextureVariantSlot> variantSlots, HashSet<Texture2D> excludeTextures)
        {
            List<int> slots = slotIndicesByType[typeIndex];
            if (excludeTextures == null || excludeTextures.Count == 0 || slots.Count <= 1)
                return slots[random.Range(0, slots.Count)];

            List<int> candidates = null;
            foreach (int slotIndex in slots)
            {
                if (excludeTextures.Contains(variantSlots[slotIndex].Texture))
                    continue;
                candidates ??= new List<int>();
                candidates.Add(slotIndex);
            }

            if (candidates == null || candidates.Count == 0)
                return slots[random.Range(0, slots.Count)];
            return candidates[random.Range(0, candidates.Count)];
        }

    }
}
