using System.Linq;
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
        public static void ResolveEndOfTurn(PlayerSetupData owner, HexSelectionController hexSelection)
        {
            if (owner == null)
                return;
            foreach (ArmyData airArmy in ArmyRegistry.AllForOwner(owner).Where(AviationRules.IsAirArmy).ToList())
            {
                AviationActions.LandInSlotOrder(airArmy, hexSelection);
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
                        continue;
                    }
                    airArmy.Members.Remove(aircraft);
                }
                if (airArmy.Members.Count == 0)
                    hexSelection?.DeleteArmyIfEmptied(airArmy);
            }
        }
    }
}