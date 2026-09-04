namespace Game.UI
{
    // Shared TMP rich-text suffix for a detailed-card stat line's parenthesized modifier — e.g.
    // "Defense 9(+3)" for a hex/building bonus, "Move 3/6(-3)" for an aviation emergency penalty.
    // Only the "(+N)"/"(-N)" portion gets colored; the stat name/value before it keeps whatever
    // color the label already has. Centralized so ArmyViewerModalUI and BattleScreenUI.Grid's
    // otherwise-duplicated detailed cards can't drift apart on markup or color (DEBUG-UI-02/03).
    public static class StatSuffixFormatter
    {
        private const string BonusColorHex = "#3ED97C";
        private const string PenaltyColorHex = "#FF5C5C";

        // bonus may be positive or negative (e.g. a hex/building Defense modifier); 0 renders nothing.
        public static string WithBonusSuffix(string baseText, int bonus)
        {
            return bonus == 0 ? baseText : $"{baseText}<color={BonusColorHex}>({bonus:+0;-0})</color>";
        }

        // penalty is a positive magnitude (amount lost to an aviation emergency); always renders
        // as "(-N)". 0 (or less) renders nothing, e.g. once the aircraft has landed/refueled.
        public static string WithPenaltySuffix(string baseText, int penalty)
        {
            return penalty <= 0 ? baseText : $"{baseText}<color={PenaltyColorHex}>(-{penalty})</color>";
        }
    }
}
