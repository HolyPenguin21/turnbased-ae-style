using System.Linq;
using Game.Cards;
using Game.Map;

namespace Game.Ai
{
    // A rough, standalone "is this Hex Event worth exploring" estimate for an AI-owned mover that
    // just triggered a clean (or now-cleared) event hex (see HexSelectionController.Events.cs's
    // ShowEventChoice, called from both BeginCleanHexEvent and TriggerHexEventIfClear) —
    // deliberately NOT BattleAi (the in-combat tactical AI). This is a map-level, pre-contact
    // "would I win this fight" guess — the comparison itself now lives on the shared WorthIt (see
    // its own class comment for why AiAggressionPlanner's own former IsEnemyWeaker duplicate got
    // folded in there too). Reads off ResolvedGuardMembers' own CardDefinition stats rather than a
    // live ArmyData — the guard itself is never actually spawned until Explore is chosen (see
    // HexEventRegistry.Entry's own comment), so there's nothing else here to read yet, and no hex
    // bonus is added here either (WorthIt.HexDefenseBonus is available but this call site has no
    // HexCoord/HexMap context handy — left out, not this pass's scope to change). Both the
    // guard's card-stat Defense AND Attack now feed WorthIt.Score (see that class's own comment)
    // — a guard tough enough to grind through but that would also chew up `mover` doing it no
    // longer reads the same as an easy clear. WorthIt.CanDamageAll's coverage check also runs
    // here per guard card COPY (2026-08-22 — was per card type, "identical cards make identical
    // DefenderProfiles"; now repeated by count instead, same fix as AiMapMemory's own event-guard
    // remembering — WorthIt.IsWorthIt's full-roster Monte Carlo needs one real combatant/HP-pool
    // per physical copy to actually play a "3 Grunts" guard out as three separate units, not just
    // check coverage against one), since a card's own CardDefinition already carries everything
    // that needs (defenseRating, attack, hitPoints, initiative, grantedAbilities) with no live
    // UnitData required.
    public static class AiEventPlanner
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
