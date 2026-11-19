using UnityEngine;

namespace Game.Terrain
{
    public interface IMapGenerationRandom
    {
        int Range(int minimum, int maximum);
        float Range(float minimum, float maximum);
        float Value { get; }
    }

    // Scene composition uses the original Unity stream and range semantics.
    internal sealed class UnityMapGenerationRandom : IMapGenerationRandom
    {
        internal static readonly UnityMapGenerationRandom Shared = new UnityMapGenerationRandom();
        private UnityMapGenerationRandom() { }
        public int Range(int minimum, int maximum) => Random.Range(minimum, maximum);
        public float Range(float minimum, float maximum) => Random.Range(minimum, maximum);
        public float Value => Random.value;
    }
}
