using System.Linq;
using Game.Cards;
using Game.Combat;
using Game.Map;

namespace Game.Ai
{
    // The auto-resolve decision for a NON-human mover (AI or neutral) that just triggered a clean
    // Hex Event: explore it, or skip it? This is an AI-layer policy — it asks "should this mover
    // take the fight" — and it delegates the physical half ("would this mover win it") to
    // Game.Combat.WorthIt. Was Game.Ai.AiEventPlanner before ARCH-01; the combat math it leans on
    // is unchanged.
    //
    // Reads the guard's CardDefinition stats (defenseRating, attack, hitPoints, initiative,
    // grantedAbilities) rather than a live ArmyData — the guard is not spawned until Explore is
    // chosen — repeating each card by its copy count so the full-roster Monte Carlo plays a
    // "3 Grunts" guard out as three separate combatants. No hex-defense bonus is added (this call
    // site has no HexCoord/HexMap context handy), matching the original.
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

            // Same verdict the former WorthIt.IsWorthIt(attacker, def, atk, defenders, hexBonus)
            // gave: with a real per-unit roster, win chance over 50% AND able to scratch every
            // defender; with no non-hero body to fight, the aggregate expected exchange margin.
            if (guardDefenders.Count == 0)
                return WorthIt.ExpectedExchangeMargin(mover, guardDefense, guardAttack) > 0f;
            return WorthIt.WinChance(mover, guardDefenders) > 0.5f && WorthIt.CanDamageAll(mover, guardDefenders);
        }
    }
}
