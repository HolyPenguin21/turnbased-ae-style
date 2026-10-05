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
            return string.Join(", ", grant.hostTypeTags.Select(t => t.ToString()).Distinct());
        }

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
                parts.Add($"{name} {change.amount}");
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

        // The equipment card's own face while it is still free in hand: compatibility + skills.
        // Numeric stat changes live in the five stat badges and are deliberately not duplicated here.
        public static string CardFace(CardDefinition equip, GameConfig config)
        {
            if (equip == null)
                return string.Empty;
            return Join(AttachmentTargets(equip), AddedAbilities(equip.equipment, config),
                UnbadgedStatChanges(equip.equipment));
        }

        private static string AttachmentTargets(CardDefinition equip)
        {
            return Compatibility(equip.equipment, equip.attachmentSlot == AttachmentSlot.Mutator);
        }

        // Unit badges cover Attack/Defense/HP/Move/Range. Initiative and activation AP
        // still need readable text, including while an attachment is being previewed.
        private static string UnbadgedStatChanges(EquipmentGrant grant)
            => FormatStatChanges(grant?.statChanges?.Where(c => c != null
                && (c.stat == EquipmentStat.ActivationApCost
                    || (c.stat == EquipmentStat.Initiative
                        && ResolveEquipmentHostType(grant) == CardType.Unit))));

        // The result detail has no stat badges, so include the complete existing effect summary.
        public static string Description(CardDefinition equip, GameConfig config)
            => equip == null ? string.Empty : Join(AttachmentTargets(equip), EffectSummary(equip, config));

        // The equipment's description once it is already attached to a host. Compatibility is no
        // longer useful at that point; numeric stat changes still belong to the stat badges.
        public static string AttachedCardFace(CardDefinition equip, GameConfig config)
        {
            if (equip == null)
                return string.Empty;
            return Join(AddedAbilities(equip.equipment, config), UnbadgedStatChanges(equip.equipment));
        }

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
                // There is no EquipmentStat for building Level, so slot 1 is intentionally empty.
                if (slot == 0)
                    return "-";
                stat = slot == 1 ? EquipmentStat.Defense
                    : slot == 2 ? EquipmentStat.HitPoints
                    : slot == 3 ? EquipmentStat.Resistance
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
