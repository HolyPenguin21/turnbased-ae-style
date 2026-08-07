using Game.Economy;
using Game.Map;
using UnityEngine;

namespace Game.Cards
{
    // What a card costs to play, in the same four stockpiled resources PlayerRoot tracks (see
    // ResourceYields for the equivalent shape on the production side — this is the spending
    // side, kept as its own type since "yield per turn" and "cost to play" are different
    // concepts even though they're shaped the same). Named fields rather than a Dictionary so
    // it shows up cleanly in the Inspector.
    [System.Serializable]
    public class ResourceCost
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

        private static readonly ResourceType[] AllTypes =
        {
            ResourceType.Human, ResourceType.Energy, ResourceType.Materials, ResourceType.Tech,
        };

        public bool CanAfford(PlayerRoot root)
        {
            if (root == null)
                return false;
            foreach (ResourceType type in AllTypes)
                if (root.GetResource(type) < Get(type))
                    return false;
            return true;
        }

        public void PayFrom(PlayerRoot root)
        {
            if (!CanAfford(root))
                return;
            foreach (ResourceType type in AllTypes)
            {
                int amount = Get(type);
                if (amount > 0)
                    root.AddResource(type, -amount);
            }
        }
    }
}
