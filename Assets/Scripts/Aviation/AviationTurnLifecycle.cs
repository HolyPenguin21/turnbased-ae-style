using System.Linq;
using System.Collections.Generic;
using Game.Map;
using Game.Players;
using Game.Units;
using UnityEngine;

namespace Game.Aviation
{
    // Owns the one end-of-turn aviation sweep.  It runs regardless of whether the active player
    // was human or AI, so fuel cannot be bypassed by a different turn controller path.
    public static class AviationTurnLifecycle
    {
        public static List<string> ResolveEndOfTurn(PlayerSetupData owner, HexSelectionController hexSelection)
        {
            var messages = new List<string>();
            if (owner == null)
                return messages;
            foreach (ArmyData airArmy in ArmyRegistry.AllForOwner(owner).Where(AviationRules.IsAirArmy).ToList())
            {
                // Landing is intentionally a state condition, not a transfer into a container:
                // the formed air army persists on a friendly barracks hex for its next sortie.
                if (AviationRules.IsOwnedAirfieldAt(airArmy.Hex, owner))
                {
                    AviationActions.EnsureAirfield(hexSelection, owner, airArmy.Hex);
                    foreach (UnitData aircraft in airArmy.Members)
                        AviationRules.ResetAfterLanding(aircraft);
                    continue;
                }
                int destroyed = 0;
                foreach (UnitData aircraft in airArmy.Members.ToList())
                {
                    aircraft.ConsecutiveUnlandedEnds++;
                    if (aircraft.ConsecutiveUnlandedEnds <= aircraft.TurnsWithoutRefuel)
                        continue;
                    if (!aircraft.HasEmergencyFlightPenalty)
                    {
                        // Losing half of CURRENT HP leaves floor(current/2), making repeated
                        // emergency landings visibly severe without silently restoring damage.
                        aircraft.HitPointsCurrent = Mathf.FloorToInt(aircraft.HitPointsCurrent * 0.5f);
                        aircraft.HasEmergencyFlightPenalty = true;
                        messages.Add($"{airArmy.Name} at {airArmy.Hex}: {aircraft.Name} lost 50% HP because it did not finish the turn at an airfield.");
                        continue;
                    }
                    airArmy.Members.Remove(aircraft);
                    destroyed++;
                }
                if (airArmy.Members.Count == 0)
                {
                    hexSelection?.DeleteArmyIfEmptied(airArmy);
                    messages.Add($"{airArmy.Name} at {airArmy.Hex}: all aircraft were destroyed because they did not finish the turn at an airfield.");
                }
                else if (destroyed > 0)
                {
                    messages.Add($"{airArmy.Name} at {airArmy.Hex}: {destroyed} aircraft were destroyed because they did not finish the turn at an airfield.");
                }
            }
            return messages;
        }
    }
}
