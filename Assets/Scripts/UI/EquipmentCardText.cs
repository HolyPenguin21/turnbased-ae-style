using System.Collections.Generic;
using System.Linq;
using Game.Cards;
using Game.Core;

namespace Game.UI
{
    // Shared text for showing what a CardType.Equipment card does — used both on the equipment
    // card's own face in hand (CardFace) and in the hover panel over a unit card's equipment
    // button (see EquipmentArtToggle). One place so both read the EquipmentGrant identically.
    public static class EquipmentCardText
    {
        // "Fits: Infantry, Vehicle" — empty string when hostTypeTags is empty (fits anything).
        public static string HostTags(EquipmentGrant grant)
        {
            if (grant?.hostTypeTags == null || grant.hostTypeTags.Count == 0)
                return string.Empty;
            return "Fits: " + string.Join(", ", grant.hostTypeTags);
        }

        // Abilities the grant ADDS — abbreviated via GameConfig, raw PrettyName fallback when
        // config is null (e.g. the battle grid, which has no catalog handy). Empty when none.
        public static string AddedAbilities(EquipmentGrant grant, GameConfig config)
        {
            List<string> tags = grant?.addAbilities;
            if (tags == null || tags.Count == 0)
                return string.Empty;
            string joined = config != null
                ? config.FormatAbilities(tags)
                : string.Join(" ", tags.Select(UnitAbilities.PrettyName));
            return string.IsNullOrEmpty(joined) ? string.Empty : "Skill: " + joined;
        }

        // "Range → 1, Defense +2, HP +3" — override shows an arrow, additive a signed
        // number. Empty when the grant changes no stats.
        public static string StatChanges(EquipmentGrant grant)
        {
            if (grant?.statChanges == null || grant.statChanges.Count == 0)
                return string.Empty;
            var parts = new List<string>();
            foreach (EquipmentStatChange change in grant.statChanges)
            {
                if (change == null)
                    continue;
                string name = StatName(change.stat);
                parts.Add(change.isOverride
                    ? $"{name} → {change.amount}"
                    : $"{name} {(change.amount >= 0 ? "+" : "")}{change.amount}");
            }
            return parts.Count == 0 ? string.Empty : string.Join(", ", parts);
        }

        // Hover panel over a unit card's equipment button: name, then added abilities, then
        // stat changes (the order the project owner asked for).
        public static string HoverInfo(CardDefinition equip, GameConfig config)
        {
            if (equip == null)
                return string.Empty;
            return Join(equip.displayName, AddedAbilities(equip.equipment, config), StatChanges(equip.equipment));
        }

        // The equipment card's own face in hand: who it fits, then added abilities, then stat
        // changes.
        public static string CardFace(CardDefinition equip, GameConfig config)
        {
            if (equip == null)
                return string.Empty;
            return Join(HostTags(equip.equipment), AddedAbilities(equip.equipment, config), StatChanges(equip.equipment));
        }

        private static string Join(params string[] lines) =>
            string.Join("\n", lines.Where(s => !string.IsNullOrEmpty(s)));

        private static string StatName(EquipmentStat stat)
        {
            switch (stat)
            {
                case EquipmentStat.HitPoints: return "HP";
                case EquipmentStat.MoveMax: return "Move";
                case EquipmentStat.ActivationApCost: return "Activation AP";
                case EquipmentStat.CommandRating: return "Command";
                default: return stat.ToString();
            }
        }
    }
}
