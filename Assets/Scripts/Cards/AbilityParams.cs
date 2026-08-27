using System.Text.RegularExpressions;
using Game.Map;
using Game.Units;

namespace Game.Cards
{
    // The one place ability tags whose NUMBERS matter (Recce vision/detection strength,
    // Stealth hide dice) get parsed — replaces the old bool UnitAbilities.Recce +
    // UnitAbilityCatalog.recceRadius/recceStrength scheme (project owner's own call, see
    // the stealth design). Every consumer (VisionSystem, StealthSystem, combat, movement,
    // AI, UI) reads these instead of re-parsing the strings itself, so the tag grammar
    // lives in exactly one file.
    //
    // Tag grammar:
    //   r<radius>s<spot>  — army vision +<radius> hexes, detection strength <spot>
    //                       (r1s0 / r1s4 / r1s5 / r1s6 are the ones in UnitAbilities.All;
    //                        the parser accepts any r<N>s<M> so a new data-only tag needs
    //                        no code change).
    //   Stealth<level>    — unit may enter stealth with <level> base hide dice (Stealth4
    //                       in UnitAbilities.All).
    public static class AbilityParams
    {
        private static readonly Regex ReccePattern = new Regex(@"^r(\d+)s(\d+)$", RegexOptions.Compiled);
        private static readonly Regex StealthPattern = new Regex(@"^Stealth(\d+)$", RegexOptions.Compiled);

        // radius = extra hex steps of vision this tag grants; spotStrength = the spot-die
        // pool it brings to a stealth-detection challenge (0 for r1s0 — reveals a hex and
        // ordinary units but never detects stealth).
        public static bool TryGetRecce(string ability, out int radius, out int spotStrength)
        {
            radius = 0;
            spotStrength = 0;
            if (string.IsNullOrEmpty(ability))
                return false;
            Match m = ReccePattern.Match(ability);
            if (!m.Success)
                return false;
            radius = int.Parse(m.Groups[1].Value);
            spotStrength = int.Parse(m.Groups[2].Value);
            return true;
        }

        public static bool TryGetStealthLevel(string ability, out int level)
        {
            level = 0;
            if (string.IsNullOrEmpty(ability))
                return false;
            Match m = StealthPattern.Match(ability);
            if (!m.Success)
                return false;
            level = int.Parse(m.Groups[1].Value);
            return true;
        }

        // Best (max, never summed — see the stealth design's "several observers don't stack
        // dice" rule and the same reasoning for several Recce members) vision radius bonus
        // any member of `army` carries. 0 for an army with no Recce-tagged member — the
        // drop-in replacement for the old ArmyData.HasRecce flag is GetBestRecceRadius > 0
        // (see ArmyHasAnyRecce).
        public static int GetBestRecceRadius(ArmyData army)
        {
            int best = 0;
            if (army == null)
                return best;
            foreach (UnitData member in army.Members)
                best = System.Math.Max(best, GetBestRecceRadius(member));
            return best;
        }

        public static int GetBestRecceSpotStrength(ArmyData army)
        {
            int best = 0;
            if (army == null)
                return best;
            foreach (UnitData member in army.Members)
                best = System.Math.Max(best, GetBestRecceSpotStrength(member));
            return best;
        }

        // Overloads over a raw ability-tag collection — for vision sources that aren't a
        // UnitData/ArmyData: a BuildingData / FacilityData / a not-yet-spawned card.
        public static int GetBestRecceRadius(System.Collections.Generic.IEnumerable<string> abilities)
        {
            int best = 0;
            if (abilities == null)
                return best;
            foreach (string ability in abilities)
                if (TryGetRecce(ability, out int radius, out _))
                    best = System.Math.Max(best, radius);
            return best;
        }

        public static int GetBestRecceSpotStrength(System.Collections.Generic.IEnumerable<string> abilities)
        {
            int best = 0;
            if (abilities == null)
                return best;
            foreach (string ability in abilities)
                if (TryGetRecce(ability, out _, out int spot))
                    best = System.Math.Max(best, spot);
            return best;
        }

        public static int GetBestRecceRadius(UnitData unit)
        {
            int best = 0;
            if (unit == null)
                return best;
            foreach (string ability in unit.Abilities)
                if (TryGetRecce(ability, out int radius, out _))
                    best = System.Math.Max(best, radius);
            return best;
        }

        public static int GetBestRecceSpotStrength(UnitData unit)
        {
            int best = 0;
            if (unit == null)
                return best;
            foreach (string ability in unit.Abilities)
                if (TryGetRecce(ability, out _, out int spot))
                    best = System.Math.Max(best, spot);
            return best;
        }

        // 0 = this unit has no Stealth tag and can never be hidden.
        public static int GetStealthLevel(UnitData unit)
        {
            int best = 0;
            if (unit == null)
                return best;
            foreach (string ability in unit.Abilities)
                if (TryGetStealthLevel(ability, out int level))
                    best = System.Math.Max(best, level);
            return best;
        }

        // The literal replacement for the removed ArmyData.HasRecce — "does this army get
        // any vision-radius bonus from a Recce-tagged member".
        public static bool ArmyHasAnyRecce(ArmyData army) => GetBestRecceRadius(army) > 0;

        // "Does this unit carry a Recce tag at all" — for the AI role checks / card-role
        // classification that used to test HasAbility(UnitAbilities.Recce) directly.
        public static bool UnitHasAnyRecce(UnitData unit)
        {
            if (unit == null)
                return false;
            foreach (string ability in unit.Abilities)
                if (TryGetRecce(ability, out _, out _))
                    return true;
            return false;
        }

        // Same test against a raw ability-tag collection (CardDefinition.grantedAbilities) —
        // for AiManagementPlanner.IsRecceCard, which classifies a card before it's ever
        // spawned into a UnitData.
        public static bool AbilitiesHaveAnyRecce(System.Collections.Generic.IEnumerable<string> abilities)
        {
            if (abilities == null)
                return false;
            foreach (string ability in abilities)
                if (TryGetRecce(ability, out _, out _))
                    return true;
            return false;
        }

        // Same, for a StealthN tag — lets AiScoutPlanner.FindMatchingRecceCard prefer a Recce
        // card whose unit can also slip into stealth (safe-first scouting) before the card is
        // ever spawned into a UnitData, mirroring AbilitiesHaveAnyRecce above.
        public static bool AbilitiesHaveAnyStealth(System.Collections.Generic.IEnumerable<string> abilities)
        {
            if (abilities == null)
                return false;
            foreach (string ability in abilities)
                if (TryGetStealthLevel(ability, out _))
                    return true;
            return false;
        }
    }
}
