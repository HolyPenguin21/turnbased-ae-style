using System;
using System.Collections.Generic;
using System.Linq;
using Game.Economy;
using Game.Players;
using Game.Terrain;

namespace Game.Campaign
{
    internal sealed class CampaignAttackSupply
    {
        internal static readonly IReadOnlyList<ResourceType> Types =
            Array.AsReadOnly(new[] { ResourceType.Human, ResourceType.Energy, ResourceType.Materials });

        internal sealed class Side
        {
            private readonly List<(CampaignRegionView Region, ResourceYields Bonus)> sources =
                new List<(CampaignRegionView, ResourceYields)>();

            internal IReadOnlyList<(CampaignRegionView Region, ResourceYields Bonus)> Sources => sources;
            internal ResourceYields Total { get; private set; } = new ResourceYields();
            internal int Points => Total.human + Total.energy + Total.materials;

            internal void Add(CampaignRegionView region)
            {
                ResourceYields bonus = region.Specialization switch
                {
                    ResourceSpecialization.Human => new ResourceYields { human = 2 },
                    ResourceSpecialization.Energy => new ResourceYields { energy = 2 },
                    ResourceSpecialization.Materials => new ResourceYields { materials = 2 },
                    ResourceSpecialization.Balanced => new ResourceYields { human = 1, energy = 1, materials = 1 },
                    _ => throw new ArgumentOutOfRangeException(nameof(region.Specialization))
                };
                sources.Add((region, bonus));
                Total = Total.Add(bonus);
            }
        }

        internal Side Attacker { get; } = new Side();
        internal Side Defender { get; } = new Side();
        // Attacker's head start over the defender, in resource points.
        internal int TempoEdge => Attacker.Points - Defender.Points;

        internal static string Describe(ResourceYields resources) => resources.HasAnyYield
            ? string.Join(" · ", Types.Where(type => resources.Get(type) > 0).Select(type => $"+{resources.Get(type)} {type}"))
            : "None";

        internal static CampaignAttackSupply Calculate(CampaignReadModel state, CampaignRegionView battlefield, Faction attacker)
        {
            var supply = new CampaignAttackSupply();
            supply.Defender.Add(battlefield);
            foreach (int id in battlefield.NeighborIds)
            {
                var neighbor = state.FindRegion(id);
                if (neighbor.OwnerFaction == attacker)
                    supply.Attacker.Add(neighbor);
                else if (neighbor.OwnerFaction == battlefield.OwnerFaction)
                    supply.Defender.Add(neighbor);
            }
            return supply;
        }
    }
}
