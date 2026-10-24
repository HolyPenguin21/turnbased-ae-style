using Game.Aviation;
using Game.Cards;
using Game.Core;
using Game.Units;
using UnityEngine;

namespace Game.UI
{
    // Shared detail text only. Each window supplies its own terrain/building defense bonus.
    internal static class UnitDetailFormatter
    {
        // Fallbacks for a null GameConfig; the tunable colors live on GameConfig
        // (statBonusColor/statPenaltyColor).
        private static readonly Color DefaultBonusColor = new Color(0.243f, 0.851f, 0.486f);
        private static readonly Color DefaultPenaltyColor = new Color(1f, 0.361f, 0.361f);

        internal static string Format(UnitData unit, GameConfig gameConfig, int defenseBonus)
            => unit == null ? string.Empty : Format(unit, defenseBonus,
                gameConfig != null ? gameConfig.statBonusColor : DefaultBonusColor,
                gameConfig != null ? gameConfig.statPenaltyColor : DefaultPenaltyColor,
                gameConfig != null ? gameConfig.FormatAbilitiesDetailed(unit.Abilities) : null);

        internal static string Format(UnitData unit, int defenseBonus,
            Color bonusColor, Color penaltyColor, string abilities)
        {
            if (unit == null) return string.Empty;

            string text = $"{unit.Name}\n";
            if (unit.TypeTags.Count > 0)
                text += $"{string.Join(", ", unit.TypeTags)}\n";
            return text + FormatBody(unit, defenseBonus, bonusColor, penaltyColor, abilities);
        }

        // Stats, then abilities: everything below the name / type-tag header lines. The collection
        // window shows name and tags in its own fields and reuses only this part.
        internal static string FormatBody(UnitData unit, GameConfig gameConfig)
            => unit == null ? string.Empty : FormatBody(unit, 0,
                gameConfig != null ? gameConfig.statBonusColor : DefaultBonusColor,
                gameConfig != null ? gameConfig.statPenaltyColor : DefaultPenaltyColor,
                gameConfig != null ? gameConfig.FormatAbilitiesDetailed(unit.Abilities) : null);

        private static string FormatBody(UnitData unit, int defenseBonus,
            Color bonusColor, Color penaltyColor, string abilities)
        {
            string defenseLine = WithBonusSuffix($"Defense {unit.Defense + defenseBonus}", defenseBonus, bonusColor);
            string hpLine = WithPenaltySuffix(
                $"HP {unit.HitPointsCurrent}/{unit.HitPointsMax}", AviationRules.EmergencyHpPenalty(unit), penaltyColor);
            string moveLine = WithPenaltySuffix(
                $"Move {AviationRules.EffectiveMoveCurrent(unit)}/{unit.MoveMax}", AviationRules.EmergencyMovePenalty(unit), penaltyColor);
            string text = string.Empty;
            // Attack / Defense / Range are omitted for a hero card — a hero fights through
            // Command Rating / Fate / Initiative, not a per-unit combat stat block, so those
            // three numbers are meaningless noise on a hero (per the user's own request).
            if (!unit.IsHero)
                text +=
                    $"Attack {unit.Attack}\n" +
                    $"{defenseLine}\n" +
                    $"Range {unit.Range}\n";
            text +=
                $"{hpLine}\n" +
                $"{moveLine}\n" +
                $"Initiative {unit.Initiative}";
            if (unit.IsAviation)
                text += $"\nFuel {AviationRules.RemainingFuel(unit)}/{unit.TurnsWithoutRefuel}";
            if (unit.AirfieldCapacity > 0)
                text += $"\nAirfield Capacity {unit.AirfieldCapacity}";
            if (unit.IsHero)
                text += $"\nCommand Rating: {unit.CommandRating}\nFate: {unit.Fate}";
            // Full name + description per ability here (detail panel), as opposed to the
            // abbreviated one-line form shown on the card itself (see
            // GameConfig.FormatAbilitiesDetailed vs FormatAbilities).
            if (!string.IsNullOrEmpty(abilities))
                text += $"\n{abilities}";
            // The attached Equipment card's own name — its effect is already folded into
            // the abilities/stats above by EquipmentSystem.Apply, this just names the source.
            if (unit.Equipment != null)
                text += $"\nEquipment: {unit.Equipment.displayName}";
            if (unit.Mutator != null)
                text += $"\nMutator: {unit.Mutator.displayName}";
            if (unit.TransportCarrier != null)
                text += $"\nTransport: {unit.TransportCarrier.Name} (base Move {unit.PermanentMoveMax})";
            return text;
        }

        // TMP rich-text suffix for a stat line's parenthesized modifier — e.g. "Defense 9(+3)" for a
        // hex/building bonus, "Move 3/6(-3)" for an aviation emergency penalty. Only the "(+N)"/"(-N)"
        // portion gets colored. bonus may be positive or negative; 0 renders nothing.
        private static string WithBonusSuffix(string baseText, int bonus, Color color)
        {
            return bonus == 0 ? baseText : $"{baseText}<color=#{ColorUtility.ToHtmlStringRGB(color)}>({bonus:+0;-0})</color>";
        }

        // penalty is a positive magnitude (amount lost to an aviation emergency); always renders
        // as "(-N)". 0 (or less) renders nothing, e.g. once the aircraft has landed/refueled.
        private static string WithPenaltySuffix(string baseText, int penalty, Color color)
        {
            return penalty <= 0 ? baseText : $"{baseText}<color=#{ColorUtility.ToHtmlStringRGB(color)}>(-{penalty})</color>";
        }
    }
}
