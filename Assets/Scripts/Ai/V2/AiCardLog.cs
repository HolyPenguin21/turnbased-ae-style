using System.Collections.Generic;
using System.Linq;
using Game.Cards;

namespace Game.Ai.V2
{
    // Human-readable card identities for AiDebug.log. StableKey remains the machine-oriented
    // deterministic identity; these helpers add the content-author-facing names needed to tell
    // which actual cards an AI held, generated, equipped and deployed.
    internal static class AiCardLog
    {
        public static string Name(CardDefinition def) =>
            def == null || string.IsNullOrWhiteSpace(def.displayName) ? "<unnamed>" : def.displayName;

        public static string Name(CardData card) => Name(card?.Definition);

        public static string Hand(AiHandData hand)
        {
            if (hand == null)
                return "<no-hand>";
            IReadOnlyList<CardData> cards = hand.Hand;
            if (cards == null || cards.Count == 0)
                return "[]";
            return "[" + string.Join(", ", cards.Select((c, i) => $"{i}:\"{Name(c)}\"")) + "]";
        }

        public static string Plan(MaterializationPlan plan)
        {
            if (plan == null)
                return "cards[<no-plan>]";

            CardDefinition baseDef = plan.BaseCardInHand?.Definition ?? plan.GeneratedBaseDef;
            CardDefinition equipDef = plan.EquipmentInHand?.Definition ?? plan.GeneratedEquipmentDef;
            string source = plan.GeneratedBaseDef != null ? "generated-base"
                : plan.GeneratedEquipmentDef != null ? "generated-equipment"
                : "hand";

            string text = $"cards[base=\"{Name(baseDef)}\" source={source}";
            if (equipDef != null)
                text += $" equip=\"{Name(equipDef)}\"";
            if (plan.Generation?.CardDef != null)
                text += $" gen=\"{Name(plan.Generation.CardDef)}\"";
            return text + "]";
        }
    }
}
