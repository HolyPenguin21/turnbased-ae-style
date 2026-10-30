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

        public ResourceCost() { }

        public ResourceCost(int human = 0, int energy = 0, int materials = 0, int tech = 0)
        {
            this.human = human;
            this.energy = energy;
            this.materials = materials;
            this.tech = tech;
        }

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

        public bool CanAfford(PlayerRoot root) => new PaymentQuote(0, this).CanDebit(root, out _);

        public bool TryPayFrom(PlayerRoot root, out PaymentReceipt receipt)
            => new PaymentQuote(0, this).TryDebit(root, out receipt, out _);

        // Compatibility adapter. Application commands use TryDebit/TryPayFrom and act on failure.
        public void PayFrom(PlayerRoot root) => TryPayFrom(root, out _);
    }
}
