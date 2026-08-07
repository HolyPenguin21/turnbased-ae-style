using System.Collections.Generic;
using Game.Core;
using Game.Economy;
using Game.HexGrid;
using Game.Terrain;
using UnityEngine;

namespace Game.Map
{
    // Owns every ResourceIconVisual currently shown on the map, keyed by hex, so a specific
    // hex's icons can be cleared and respawned when its effective yield changes (e.g. a
    // citadel gets built there) instead of only ever being drawn once. Lives on the same
    // GameObject as HexMap, so it survives for the whole game (unlike HexMapGenerator, which
    // self-destructs after generating).
    [RequireComponent(typeof(HexMap))]
    public class MapResourceDisplay : MonoBehaviour
    {
        [SerializeField] private GameConfig gameConfig;

        private static readonly ResourceType[] AllResourceTypes =
        {
            ResourceType.Human, ResourceType.Energy, ResourceType.Materials, ResourceType.Tech,
        };

        private readonly Dictionary<HexCoord, List<ResourceIconVisual>> _iconsByHex = new Dictionary<HexCoord, List<ResourceIconVisual>>();

        // Fetched on demand rather than cached in Awake — this component isn't [ExecuteAlways],
        // so RefreshAll can be called (from HexMapGenerator, which is) before Awake has ever
        // run, e.g. while tuning the map in the editor outside Play mode.
        private HexMap Map => GetComponent<HexMap>();

        // Called once right after the map finishes generating — shows every hex's own terrain
        // yield, before any citadel exists yet.
        public void RefreshAll()
        {
            HexMap map = Map;
            if (gameConfig == null || map == null)
                return;

            for (int row = 0; row < map.Height; row++)
                for (int col = 0; col < map.Width; col++)
                    RefreshHex(HexCoord.FromOffset(col, row));
        }

        // Called whenever a hex's effective yield may have changed (right now: a citadel bonus
        // just got stamped onto it, see HexResourceBonusRegistry) — clears whatever was shown
        // there and redraws from scratch, so the hex ends up showing the combined terrain +
        // bonus total, not just the terrain's own.
        public void RefreshHex(HexCoord coord)
        {
            HexMap map = Map;
            if (gameConfig == null || map == null)
                return;

            ClearHex(coord);

            if (!map.TryGetTerrainAt(coord, out TerrainTypeEntry entry))
                return;

            ResourceYields yields = HexResourceCalculator.GetEffectiveYield(entry, HexResourceBonusRegistry.GetBonus(coord));
            if (yields.HasAnyYield)
                SpawnIconsAt(coord, yields);
        }

        private void ClearHex(HexCoord coord)
        {
            if (!_iconsByHex.TryGetValue(coord, out List<ResourceIconVisual> icons))
                return;

            foreach (ResourceIconVisual icon in icons)
                if (icon != null)
                    DestroyIcon(icon.gameObject);
            _iconsByHex.Remove(coord);
        }

        // RefreshAll can run outside Play mode too (HexMapGenerator is [ExecuteAlways]) —
        // Destroy() isn't allowed there, has to be DestroyImmediate() instead.
        private static void DestroyIcon(GameObject icon)
        {
            if (Application.isPlaying) Destroy(icon);
            else DestroyImmediate(icon);
        }

        // Laid out as a single horizontal row above the hex — only the resources it actually
        // yields (1 to 4 of them), centred as a group, not fixed per-type corner slots.
        private void SpawnIconsAt(HexCoord coord, ResourceYields yields)
        {
            if (gameConfig.resourceIconPrefab == null)
                return;

            var present = new List<(ResourceType type, int amount)>();
            foreach (ResourceType type in AllResourceTypes)
            {
                int amount = yields.Get(type);
                if (amount > 0)
                    present.Add((type, amount));
            }
            if (present.Count == 0)
                return;

            HexMap map = Map;
            Vector3 center = map.HexToWorld(coord);
            float radius = map.OuterRadius;
            float startX = -(present.Count - 1) * gameConfig.resourceIconSpacing * 0.5f;

            var icons = new List<ResourceIconVisual>(present.Count);
            for (int i = 0; i < present.Count; i++)
            {
                (ResourceType type, int amount) = present[i];
                float x = startX + i * gameConfig.resourceIconSpacing;
                ResourceIconVisual icon = Instantiate(gameConfig.resourceIconPrefab, map.transform);
                icon.transform.position = center + new Vector3(x * radius, 0f, gameConfig.resourceRowOffset * radius);
                icon.SetResource(type, amount);
                icons.Add(icon);
            }
            _iconsByHex[coord] = icons;
        }
    }
}
