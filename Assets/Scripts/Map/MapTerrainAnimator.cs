using System;
using System.Collections.Generic;
using UnityEngine;

namespace Game.Map
{
    // Updates textures on existing combined-mesh materials only. No gameplay writes,
    // fog refresh, mesh rebuild or per-cell GameObjects. One clock per complex.
    public sealed class MapTerrainAnimator : MonoBehaviour
    {
        public sealed class Group
        {
            public Material[] Materials;
            public Texture2D[][] Frames;
            public float FramesPerSecond;
            public int Phase;
            internal int LastFrame = -1;
        }

        private readonly List<Group> _groups = new List<Group>();
        private double _epoch;
        private Material[] _ownedMaterials = Array.Empty<Material>();

        public void Configure(IEnumerable<Group> groups, Material[] ownedMaterials = null)
        {
            var retained = new HashSet<Material>(ownedMaterials ?? Array.Empty<Material>());
            foreach (Material material in _ownedMaterials)
                if (material != null && !retained.Contains(material)) Release(material);
            _ownedMaterials = ownedMaterials ?? Array.Empty<Material>();
            _groups.Clear();
            _epoch = Time.timeAsDouble;
            foreach (Group group in groups)
                if (group?.Frames != null && group.Frames.Length > 0
                    && group.Frames[0].Length > 1)
                { group.LastFrame = -1; _groups.Add(group); }
            enabled = _groups.Count > 0;
            ApplyAtTime(0);
        }

        public static int FrameAtTime(double elapsed, float framesPerSecond, int phase, int frameCount) =>
            frameCount <= 0 ? 0 : (int)((Math.Floor(Math.Max(0, elapsed) * Math.Max(0.01f, framesPerSecond))
                + phase) % frameCount);

        public void ApplyAtTime(double elapsed)
        {
            foreach (Group group in _groups)
            {
                int frame = FrameAtTime(elapsed, group.FramesPerSecond, group.Phase, group.Frames[0].Length);
                if (frame == group.LastFrame) continue;
                for (int i = 0; i < group.Materials.Length; i++)
                    if (group.Materials[i] != null) group.Materials[i].mainTexture = group.Frames[i][frame];
                group.LastFrame = frame;
            }
        }

        private void Update() => ApplyAtTime(Time.timeAsDouble - _epoch);
        private static void Release(Material material)
        {
            if (Application.isPlaying) Destroy(material);
            else DestroyImmediate(material);
        }

        private void OnDestroy()
        {
            foreach (Material material in _ownedMaterials) if (material != null) Release(material);
            _groups.Clear();
        }
    }
}
