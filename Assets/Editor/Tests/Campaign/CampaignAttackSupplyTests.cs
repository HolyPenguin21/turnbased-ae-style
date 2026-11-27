#if UNITY_INCLUDE_TESTS
using System.Linq;
using Game.Campaign;
using Game.Economy;
using Game.Players;
using Game.Terrain;
using NUnit.Framework;

// Starting supply: the battlefield and the defender's neighbours supply the defender, the attacker's
// neighbours supply the attacker, a third faction's neighbours supply nobody.
public sealed class CampaignAttackSupplyTests
{
    private static ResourceYields Bonus(ResourceSpecialization s) => s switch
    {
        ResourceSpecialization.Human => new ResourceYields { human = 2 },
        ResourceSpecialization.Energy => new ResourceYields { energy = 2 },
        ResourceSpecialization.Materials => new ResourceYields { materials = 2 },
        _ => new ResourceYields { human = 1, energy = 1, materials = 1 },
    };

    private static string Text(ResourceYields y) => $"{y.human}/{y.energy}/{y.materials}/{y.tech}";

    [Test]
    public void Calculate_SplitsNeighboursByOwner()
    {
        int checkedBattles = 0;
        foreach (int seed in new[] { 1, 37, 2026 })
        {
            var state = new CampaignMapGenerator().Generate(seed, Faction.Ashen, new CampaignGenerationSettings { RegionCount = 24 });
            var view = new CampaignReadModel(state);
            foreach (var battlefield in view.Regions)
                foreach (Faction attacker in battlefield.NeighborIds.Select(id => view.FindRegion(id).OwnerFaction)
                    .Where(f => f != battlefield.OwnerFaction).Distinct())
                {
                    var supply = CampaignAttackSupply.Calculate(view, battlefield, attacker);
                    var neighbours = battlefield.NeighborIds.Select(view.FindRegion).ToList();
                    var attackers = neighbours.Where(r => r.OwnerFaction == attacker).ToList();
                    var defenders = new[] { battlefield }.Concat(neighbours.Where(r => r.OwnerFaction == battlefield.OwnerFaction)).ToList();

                    Assert.That(supply.Attacker.Sources.Select(s => s.Region.RegionId), Is.EquivalentTo(attackers.Select(r => r.RegionId)));
                    Assert.That(supply.Defender.Sources.Select(s => s.Region.RegionId), Is.EquivalentTo(defenders.Select(r => r.RegionId)));
                    Assert.That(Text(supply.Attacker.Total), Is.EqualTo(Text(attackers.Aggregate(new ResourceYields(), (t, r) => t.Add(Bonus(r.Specialization))))));
                    Assert.That(Text(supply.Defender.Total), Is.EqualTo(Text(defenders.Aggregate(new ResourceYields(), (t, r) => t.Add(Bonus(r.Specialization))))));
                    Assert.That(supply.Attacker.Total.tech + supply.Defender.Total.tech, Is.Zero);
                    checkedBattles++;
                }
        }
        Assert.That(checkedBattles, Is.GreaterThan(0));
    }

    [Test]
    public void TempoEdge_ShiftsAutoResolveChanceSlightly()
    {
        var resolver = new CampaignBattleResolver();
        var deck = new CampaignDeck("Empty", new Game.Cards.CardDefinition[0]);
        double even = resolver.Compare(deck, deck).WinChance;
        double ahead = resolver.Compare(deck, deck, 6).WinChance;
        double behind = resolver.Compare(deck, deck, -6).WinChance;
        Assert.That(even, Is.EqualTo(.5).Within(1e-9));
        Assert.That(ahead, Is.GreaterThan(even).And.LessThan(.6));
        Assert.That(ahead - even, Is.EqualTo(even - behind).Within(1e-9));
    }
}
#endif
