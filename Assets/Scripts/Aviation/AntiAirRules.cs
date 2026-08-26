using System.Collections.Generic;
using Game.Cards;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Units;

namespace Game.Aviation
{
    // One AA unit's reaction opportunity against one specific air army — found either by
    // starting from the air army (CollectEntryReactions: "who can shoot AT me") or by starting
    // from a moving ground AA unit (CollectGroundOpportunities: "what can I shoot") — same
    // underlying (aaUnit, airArmy) pairing either way, just discovered from opposite ends.
    public readonly struct AaReaction
    {
        public readonly UnitData AaUnit;
        public readonly ArmyData AaArmy;
        public readonly ArmyData AirArmy;
        public readonly int Distance;

        public AaReaction(UnitData aaUnit, ArmyData aaArmy, ArmyData airArmy, int distance)
        {
            AaUnit = aaUnit;
            AaArmy = aaArmy;
            AirArmy = airArmy;
            Distance = distance;
        }
    }

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

        // Every enemy AA unit anywhere on the map currently within its own radius of `hex` and
        // still free (see AntiAirState) to react against `airArmy` specifically — called once an
        // air army finishes entering a hex. Deliberately does NOT consult human FOW: a hidden AA
        // unit still correctly reacts before an air strike even though the air army's own owner
        // can't see it (per the design's own "hidden AA fires before an air strike" rule) — this
        // is a map-truth query, not a display one.
        public static List<AaReaction> CollectEntryReactions(ArmyData airArmy, HexCoord enteredHex)
        {
            var reactions = new List<AaReaction>();
            if (airArmy == null)
                return reactions;

            foreach (HexCoord aaHex in ArmyRegistry.AllOccupiedHexes())
            {
                int distance = HexGridMath.Distance(aaHex, enteredHex);
                foreach (ArmyData army in ArmyRegistry.AllAt(aaHex))
                {
                    if (army.Owner == null || army.Owner == airArmy.Owner
                        || AviationRules.IsAirArmy(army) || AviationRules.IsAirfield(army))
                        continue;
                    foreach (UnitData member in army.Members)
                    {
                        if (!TryGetRadius(member, out int radius) || distance > radius)
                            continue;
                        if (!AntiAirState.CanReact(member, airArmy.Id))
                            continue;
                        reactions.Add(new AaReaction(member, army, airArmy, distance));
                    }
                }
            }
            SortReactions(reactions);
            return reactions;
        }

        // The mirror query: called once a GROUND army carrying at least one AA-tagged member
        // finishes entering a hex — every enemy air army anywhere within that member's own
        // radius, so a passing air army can be shot at from the ground even though it never
        // itself entered the AA unit's hex (see HexSelectionController.Movement.cs's own
        // ground-mover call site).
        public static List<AaReaction> CollectGroundOpportunities(ArmyData groundArmy, HexCoord hex)
        {
            var reactions = new List<AaReaction>();
            if (groundArmy == null)
                return reactions;

            foreach (UnitData member in groundArmy.Members)
            {
                if (!TryGetRadius(member, out int radius))
                    continue;
                foreach (HexCoord airHex in ArmyRegistry.AllOccupiedHexes())
                {
                    int distance = HexGridMath.Distance(hex, airHex);
                    if (distance > radius)
                        continue;
                    foreach (ArmyData airArmy in ArmyRegistry.AllAt(airHex))
                    {
                        if (airArmy.Owner == groundArmy.Owner || !AviationRules.IsAirArmy(airArmy))
                            continue;
                        if (!AntiAirState.CanReact(member, airArmy.Id))
                            continue;
                        reactions.Add(new AaReaction(member, groundArmy, airArmy, distance));
                    }
                }
            }
            SortReactions(reactions);
            return reactions;
        }

        // Stable order per the design: hex distance, then army Id, then member slot index.
        private static void SortReactions(List<AaReaction> reactions)
        {
            reactions.Sort((a, b) =>
            {
                if (a.Distance != b.Distance)
                    return a.Distance.CompareTo(b.Distance);
                if (a.AaArmy.Id != b.AaArmy.Id)
                    return a.AaArmy.Id.CompareTo(b.AaArmy.Id);
                return a.AaArmy.Members.IndexOf(a.AaUnit).CompareTo(b.AaArmy.Members.IndexOf(b.AaUnit));
            });
        }
    }
}
