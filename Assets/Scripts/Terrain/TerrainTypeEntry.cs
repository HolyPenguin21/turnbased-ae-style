using System.Collections.Generic;
using Game.Economy;
using UnityEngine;

namespace Game.Terrain
{
    [System.Serializable]
    public class TerrainTypeEntry
    {
        public string terrainName;
        public Texture2D texture;

        // Optional extra visual variants of the same terrain type — if any are set, each hex
        // of this type independently rolls a random pick among texture + these alternatives
        // when the map is generated (still one draw call per variant actually in use, not
        // per hex). Leave empty to always use texture alone, same as before.
        public Texture2D[] alternativeTextures;

        // Relative chance (0-100) of this type winning the baseline random fill against every
        // other type in the pool — not a strict percentage that must sum to 100 across types,
        // just weighted odds within a friendlier 0-100 range. 0 means it never appears via the
        // baseline fill (e.g. a type only ever placed by its own dedicated rule, like
        // Mountains' range-forming — see MapGenerationSettings.mountainsTerrainName).
        [Range(0f, 100f)]
        public float baselineWeight = 1f;

        // How much of each resource this hex produces per turn. Every terrain type can carry a
        // yield now (no "Resource" role gate) — most just default to zero. Not tied to a single
        // resource type: e.g. Ruins might give 2 Human + 1 Materials at once.
        public ResourceYields resourceYields = new ResourceYields();

        // How many move points a unit spends entering this hex (e.g. Desert = 1, SandDunes =
        // 2, Mountains = expensive but never blocked outright). Every on-map hex is passable in
        // principle, but an army stops short the moment it can't fully afford the next hex's
        // cost (see ArmyController.MoveRoutine) — this cost is the only friction a terrain type
        // can impose.
        [Min(1)]
        public int moveCost = 1;

        // texture + alternativeTextures, nulls skipped — every hex-visual-variant a generator
        // needs to pick between.
        public List<Texture2D> GetAllTextures()
        {
            var result = new List<Texture2D>();
            if (texture != null)
                result.Add(texture);
            if (alternativeTextures != null)
                foreach (Texture2D t in alternativeTextures)
                    if (t != null)
                        result.Add(t);
            return result;
        }
    }
}
