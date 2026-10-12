using UnityEngine;

namespace Game.UI
{
    // Shared TMP rich-text suffix for a detailed-card stat line's parenthesized modifier — e.g.
    // "Defense 9(+3)" for a hex/building bonus, "Move 3/6(-3)" for an aviation emergency penalty.
    // Only the "(+N)"/"(-N)" portion gets colored; the stat name/value before it keeps whatever
    // color the label already has. Centralized so ArmyViewerModalUI and BattleScreenUI.Grid's
    // otherwise-duplicated detailed cards can't drift apart on markup or color (DEBUG-UI-02/03).
    // Colors themselves live on GameConfig (statBonusColor/statPenaltyColor) so they're tunable
    // from the Inspector instead of a code constant; these fallbacks only cover a null GameConfig.
    public static class StatSuffixFormatter
    {
        public static readonly Color DefaultBonusColor = new Color(0.243f, 0.851f, 0.486f);
        public static readonly Color DefaultPenaltyColor = new Color(1f, 0.361f, 0.361f);

        // bonus may be positive or negative (e.g. a hex/building Defense modifier); 0 renders nothing.
        public static string WithBonusSuffix(string baseText, int bonus, Color color)
        {
            return bonus == 0 ? baseText : $"{baseText}<color=#{ColorUtility.ToHtmlStringRGB(color)}>({bonus:+0;-0})</color>";
        }

        // penalty is a positive magnitude (amount lost to an aviation emergency); always renders
        // as "(-N)". 0 (or less) renders nothing, e.g. once the aircraft has landed/refueled.
        public static string WithPenaltySuffix(string baseText, int penalty, Color color)
        {
            return penalty <= 0 ? baseText : $"{baseText}<color=#{ColorUtility.ToHtmlStringRGB(color)}>(-{penalty})</color>";
        }
    }
}
