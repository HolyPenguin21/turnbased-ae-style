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
        // "Infantry, Vehicle" — the unit type tags this equipment fits. Empty string when
        // hostTypeTags is empty (fits anything).
        public static string HostTags(EquipmentGrant grant)
        {
            if (grant?.hostTypeTags == null || grant.hostTypeTags.Count == 0)
                return string.Empty;
            return string.Join(", ", grant.hostTypeTags);
        }

        // Abilities the grant ADDS — abbreviated via GameConfig, raw PrettyName fallback when
        // config is null (e.g. the battle grid, which has no catalog handy). Empty when none.
        public static string AddedAbilities(EquipmentGrant grant, GameConfig config)
        {
            List<string> tags = grant?.addAbilities;
            if (tags == null || tags.Count == 0)
                return string.Empty;
            return config != null
                ? config.FormatAbilities(tags)
                : string.Join(" ", tags.Select(UnitAbilities.PrettyName));
        }

        // "Range = 1, Defense +2, HP +3" — override shows "= value", additive a signed number.
        // Empty when the grant changes no stats.
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
                    ? $"{name} = {change.amount}"
                    : $"{name} {(change.amount >= 0 ? "+" : "")}{change.amount}");
            }
            return parts.Count == 0 ? string.Empty : string.Join(", ", parts);
        }

        // What an attached equipment DOES — added abilities then stat changes, no name (the
        // card's own name element is overridden separately, see EquipmentArtToggle). Empty when
        // the grant neither adds an ability nor changes a stat.
        public static string EffectSummary(CardDefinition equip, GameConfig config)
        {
            if (equip == null)
                return string.Empty;
            return Join(AddedAbilities(equip.equipment, config), StatChanges(equip.equipment));
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
