using System.Linq;
using Game.Cards;
using Game.Map;

namespace Game.Ai
{
    // A rough, standalone "is this Hex Event worth exploring" estimate for an AI-owned mover that
    // just triggered a clean (or now-cleared) event hex (see HexSelectionController.Events.cs's
    // ShowEventChoice, called from both BeginCleanHexEvent and TriggerHexEventIfClear) —
    // deliberately NOT BattleAi (the in-combat tactical AI) and NOT AiScoutPlanner.IsEnemyWeaker
    // (private, scoped to that file's own scout-task gating). This is a map-level, pre-contact
    // "would I win this fight" guess, same flat Attack/Defense comparison style — rough on
    // purpose, per the user's own call ("good enough for tests"). Reads off
    // ResolvedGuardMembers' own CardDefinition stats rather than a live ArmyData — the guard
    // itself is never actually spawned until Explore is chosen (see HexEventRegistry.Entry's own
    // comment), so there's nothing else here to read yet.
    public static class AiEventPlanner
    {
        public static bool ShouldExplore(ArmyData mover, HexEventRegistry.Entry entry)
        {
            if (entry?.ResolvedGuardMembers == null || entry.ResolvedGuardMembers.Count == 0)
                return true; // no guard — nothing to risk, always worth it

            float ownAttack = mover.Members.Where(m => !m.IsHero).Sum(m => m.Attack);
            float guardDefense = entry.ResolvedGuardMembers
                .Where(g => g.card != null && g.card.cardType != CardType.Hero)
                .Sum(g => g.card.defenseRating * g.count);
            return ownAttack > guardDefense;
        }
    }
}
