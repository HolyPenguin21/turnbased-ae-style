using UnityEngine;

namespace Game.Economy
{
    // How much of each resource a single hex produces per turn — a hex isn't tied to one
    // resource type, it can yield several at once in different amounts (e.g. Ruins: 2 Human,
    // 1 Materials, 0 Energy, 0 Tech). Named fields rather than a Dictionary so it shows up
    // cleanly in the Inspector.
    [System.Serializable]
    public class ResourceYields
    {
        [Min(0)] public int human;
        [Min(0)] public int energy;
        [Min(0)] public int materials;
        [Min(0)] public int tech;

        public int Get(ResourceType type)
        {
            switch (type)
            {
                case ResourceType.Human: return human;
                case ResourceType.Energy: return energy;
                case ResourceType.Materials: return materials;
                case ResourceType.Tech: return tech;
                default: return 0;
            }
        }

        public bool HasAnyYield => human > 0 || energy > 0 || materials > 0 || tech > 0;

        public ResourceYields Add(ResourceYields other)
        {
            if (other == null)
                return this;

            return new ResourceYields
            {
                human = human + other.human,
                energy = energy + other.energy,
                materials = materials + other.materials,
                tech = tech + other.tech,
            };
        }
    }
}
