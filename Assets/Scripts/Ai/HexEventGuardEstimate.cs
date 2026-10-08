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
            var guardDefenders = guardMembers.SelectMany(g => Enumerable.Repeat(new WorthIt.DefenderProfile(g.card.defenseRating,
                g.card.grantedAbilities != null && g.card.grantedAbilities.Contains(UnitAbilities.CeramicArmor), g.card.unitTypeTags,
                g.card.attack, g.card.hitPoints, g.card.initiative, g.card.grantedAbilities,
                range: g.card.range), g.count)).ToList();

            // Win chance over 50% AND able to scratch every defender. A guard with no fighting body
            // is a trivial win for the roster estimator (nothing to fight).
            return WorthIt.WinChance(mover, guardDefenders, 0f, GuardCommander(entry)) > 0.5f
                && WorthIt.CanDamageAll(mover, guardDefenders);
        }

        // The guard's commander: its first hero card (the same "first hero leads" rule as
        // ArmyData.Commander), so guard memory and this estimate read one answer.
        public static WorthIt.SideCommander GuardCommander(HexEventRegistry.Entry entry)
        {
            var hero = entry?.ResolvedGuardMembers?
                .FirstOrDefault(g => g.card != null && g.card.cardType == CardType.Hero).card;
            return WorthIt.SideCommander.Of(hero);
        }

        // The event's guard tier: the index of the authored variant (light 0 / medium 1 / heavy 2,
        // EventDefinition.variants) whose guard this hex carries; -1 when unknown. The reward is
        // authored per tier, so the tier is what an observer of the guard learns about the reward
        // without ever reading the hidden payout (HexEventRegistry.Entry.SelectedRewards).
        public static int RewardTier(HexEventRegistry.Entry entry)
        {
            var variants = entry?.Definition?.variants;
            if (variants == null || string.IsNullOrEmpty(entry.GuardArmyName))
                return -1;
            for (int i = 0; i < variants.Count; i++)
                if (variants[i] != null && variants[i].guardArmyName == entry.GuardArmyName)
                    return i;
            return -1;
        }
    }
}
