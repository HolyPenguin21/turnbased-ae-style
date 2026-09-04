using System.Linq;
using Game.Ai;
using Game.Cards;
using Game.Map;

namespace Game.Combat
{
    // A rough, standalone "is this Hex Event worth exploring" estimate for a mover that just
    // triggered a clean (or now-cleared) event hex — a map-level, pre-contact "would I win this
    // fight" guess against the event's guard roster. Extracted verbatim (ARCH-01, 2026-09-04)
    // from the former Game.Ai.AiEventPlanner; physical winnability only, no strategic weighting.
    //
    // Reads the guard's CardDefinition stats (defenseRating, attack, hitPoints, initiative,
    // grantedAbilities) rather than a live ArmyData — the guard is not spawned until Explore is
    // chosen — repeating each card by its copy count so WorthIt's full-roster Monte Carlo plays
    // a "3 Grunts" guard out as three separate combatants. No hex-defense bonus is added (this
    // call site has no HexCoord/HexMap context handy), matching the original.
    public static class HexEventGuardEstimate
    {
        public static bool ShouldExplore(ArmyData mover, HexEventRegistry.Entry entry)
        {
            if (entry?.ResolvedGuardMembers == null || entry.ResolvedGuardMembers.Count == 0)
                return true; // no guard — nothing to risk, always worth it

            var guardMembers = entry.ResolvedGuardMembers.Where(g => g.card != null && g.card.cardType != CardType.Hero).ToList();
            float guardDefense = guardMembers.Sum(g => g.card.defenseRating * g.count);
            float guardAttack = guardMembers.Sum(g => g.card.attack * g.count);
            var guardDefenders = guardMembers.SelectMany(g => Enumerable.Repeat(new WorthIt.DefenderProfile(g.card.defenseRating,
                g.card.grantedAbilities != null && g.card.grantedAbilities.Contains(UnitAbilities.CeramicArmor), g.card.unitTypeTags,
                g.card.attack, g.card.hitPoints, g.card.initiative), g.count)).ToList();
            return WorthIt.IsWorthIt(mover, guardDefense, guardAttack, guardDefenders);
        }
    }
}
