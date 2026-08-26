using Game.Cards;
using Game.Units;

namespace Game.Aviation
{
    // The suffix parser is intentionally tolerant: content authors can use AA for radius one
    // while the card catalogue later grows AA2/AA3 without a code or editor overhaul.
    public static class AntiAirRules
    {
        public static bool TryGetRadius(UnitData unit, out int radius)
        {
            radius = 0;
            if (unit == null)
                return false;
            foreach (string ability in unit.Abilities)
            {
                if (ability == UnitAbilities.AntiAir)
                {
                    radius = unit.AntiAirRadius;
                    return true;
                }
                if (!string.IsNullOrEmpty(ability) && ability.StartsWith(UnitAbilities.AntiAir)
                    && int.TryParse(ability.Substring(UnitAbilities.AntiAir.Length), out int parsed) && parsed > 0)
                {
                    radius = parsed;
                    return true;
                }
            }
            return false;
        }
    }
}
