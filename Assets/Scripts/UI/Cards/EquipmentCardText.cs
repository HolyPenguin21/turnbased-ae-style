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

        // One compact compatibility line: authored tags followed by allowed host kinds.
        // The tags retain EquipmentSystem's ANY-match semantics; this is presentation only.
        public static string AttachTargets(EquipmentGrant grant)
        {
            if (grant == null)
                return string.Empty;
            return Compatibility(grant, false);
        }

        private static string Compatibility(EquipmentGrant grant, bool mutator)
        {
            var parts = new List<string>();
            // Bio is mandatory for Mutator even when the authored tag list is unrestricted.
            if (mutator) parts.Add(UnitTypeTag.Bio.ToString());
            if (grant?.hostTypeTags != null)
                parts.AddRange(grant.hostTypeTags.Select(t => t.ToString()));
            if (grant?.hostKinds != null)
                parts.AddRange(grant.hostKinds.Select(k => k.ToString()));
            return string.Join(", ", parts.Distinct());
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

        // The same plain-value convention as the stat badges; negative values keep their sign.
        public static string StatChanges(EquipmentGrant grant)
            => FormatStatChanges(grant?.statChanges);

        private static string FormatStatChanges(IEnumerable<EquipmentStatChange> changes)
        {
            if (changes == null)
                return string.Empty;
            var parts = new List<string>();
            foreach (EquipmentStatChange change in changes)
            {
                if (change == null)
                    continue;
                string name = StatName(change.stat);
                // An override sets the value ("Attack: 4"); a delta is signed ("Attack: +1", "Attack: -1").
                string value = change.isOverride || change.amount < 0 ? change.amount.ToString() : "+" + change.amount;
                parts.Add($"{name}: {value}");
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
            return Join(ItemAbilities(equip, config), StatChanges(equip.equipment));
        }

        // The equipment/mutator card's own face (free in hand or catalog preview): who it fits
        // (the same compatibility line as Description), then the added abilities' abbreviations
        // (see GameConfig.FormatAbilities). Numeric changes are on the stat badges; the full
        // stat list stays in the detail panel (see Description).
        public static string CardFace(CardDefinition equip, GameConfig config)
            => equip == null ? string.Empty : Join(AttachmentTargets(equip), ItemAbilities(equip, config));

        private static string ItemAbilities(CardDefinition equip, GameConfig config)
            => Join(AddedAbilities(equip.equipment, config),
                EquipmentSystem.IsSingleUse(equip)
                    && equip.equipment?.addAbilities?.Contains(UnitAbilities.SingleUse) != true
                        ? UnitAbilities.PrettyName(UnitAbilities.SingleUse) : string.Empty);

        private static string AttachmentTargets(CardDefinition equip)
        {
            return Compatibility(equip.equipment, equip.attachmentSlot == AttachmentSlot.Mutator);
        }

        // The result detail has no stat badges, so include the complete existing effect summary.
        // The first line is the production difficulty (CardDefinition.fate, see its comment).
        public static string Description(CardDefinition equip, GameConfig config)
            => equip == null ? string.Empty
                : Join($"Challenge: {equip.fate}", AttachmentTargets(equip), EffectSummary(equip, config));

        // The equipment's face once it is already attached to a host (shown over the host card
        // while its equipment button is held) — the same face as the free card (CardFace).
        public static string AttachedCardFace(CardDefinition equip, GameConfig config)
            => CardFace(equip, config);

        // Resolves the five physical badge slots through the card type the Equipment targets.
        // A free Equipment card derives this from hostKinds; an attached preview passes its actual
        // host type, so Hero gear uses Command/Fate/HP/Move/Initiative instead of Unit semantics.
        public static string StatBadgeValueForSlot(EquipmentGrant grant, int slot, CardType? hostType = null)
        {
            CardType resolved = hostType ?? ResolveEquipmentHostType(grant);
            EquipmentStat stat;
            if (resolved == CardType.Hero)
            {
                stat = slot == 0 ? EquipmentStat.CommandRating
                    : slot == 1 ? EquipmentStat.Fate
                    : slot == 2 ? EquipmentStat.HitPoints
                    : slot == 3 ? EquipmentStat.MoveMax
                    : EquipmentStat.Initiative;
            }
            else if (resolved == CardType.Facility || resolved == CardType.Base)
            {
                // There is no EquipmentStat for building Level (slot 0), and slot 3 (the old Resistance
                // position) no longer carries a stat, so both are intentionally empty.
                if (slot == 0 || slot == 3)
                    return "-";
                stat = slot == 1 ? EquipmentStat.Defense
                    : slot == 2 ? EquipmentStat.HitPoints
                    : EquipmentStat.Fate;
            }
            else
            {
                stat = slot == 0 ? EquipmentStat.Attack
                    : slot == 1 ? EquipmentStat.Defense
                    : slot == 2 ? EquipmentStat.HitPoints
                    : slot == 3 ? EquipmentStat.MoveMax
                    : EquipmentStat.Range;
            }
            return StatBadgeValue(grant, stat);
        }

        private static CardType ResolveEquipmentHostType(EquipmentGrant grant)
        {
            bool unit = grant?.hostKinds != null && grant.hostKinds.Contains(EquipmentHostKind.Unit);
            bool hero = grant?.hostKinds != null && grant.hostKinds.Contains(EquipmentHostKind.Hero);
            bool facility = grant?.hostKinds != null && grant.hostKinds.Contains(EquipmentHostKind.Facility);
            if (hero && !unit && !facility) return CardType.Hero;
            if (facility && !unit && !hero) return CardType.Facility;
            // Existing gear is Unit-targeted; mixed host-kind gear keeps the Unit vocabulary on
            // its free card until an actual host is known, then attached preview uses that host.
            return CardType.Unit;
        }

        // Compact value used by an Equipment card's stat badges. It describes the gear itself,
        // never a host+gear total. Positive additive and override values are shown as plain
        // numbers; negative values keep their minus sign, and untouched slots show "-".
        // Overrides still win because EquipmentSystem applies them after deltas.
        public static string StatBadgeValue(EquipmentGrant grant, EquipmentStat stat)
        {
            if (grant?.statChanges == null)
                return "-";

            int additive = 0;
            bool hasAdditive = false;
            bool hasOverride = false;
            int overrideValue = 0;
            foreach (EquipmentStatChange change in grant.statChanges)
            {
                if (change == null || change.stat != stat)
                    continue;
                if (change.isOverride)
                {
                    hasOverride = true;
                    overrideValue = change.amount;
                }
                else
                {
                    hasAdditive = true;
                    additive += change.amount;
                }
            }

            if (hasOverride)
                return overrideValue.ToString();
            if (!hasAdditive || additive == 0)
                return "-";
            return additive.ToString();
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
