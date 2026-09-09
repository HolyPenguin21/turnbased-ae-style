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
                // No EnsureAirfield here — in the current "landing doesn't transfer cards" model
                // nothing populates a speculatively-created container, so it would only leave a
                // permanent empty IsAirfield army on the base hex that DeleteArmyIfEmptied keeps
                // forever (Barracks-hex exemption) and that the hex-side army button row then
                // renders as an extra, unmovable pick. The real container is created on demand by
                // AviationActions.TryDeployFromCard / the AI's own launch-abort path, both of
                // which add members immediately.
                if (AviationRules.IsOwnedAirfieldAt(airArmy.Hex, owner))
                {
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
                    // Every overdue end inflicts the same fixed damage: half of this card's
                    // maximum HP. Whether it survives is therefore determined solely by its
                    // current HP; repairing it naturally lets it survive another missed landing.
                    aircraft.HitPointsCurrent -= AviationRules.EmergencyHpLoss(aircraft);
                    aircraft.HasEmergencyFlightPenalty = true;
                    if (aircraft.HitPointsCurrent > 0)
                    {
                        messages.Add($"{airArmy.Name} at {FormatGameCoord(airArmy.Hex)}: {aircraft.Name} lost 50% max HP because it did not finish the turn at an airfield.");
                        continue;
                    }
                    aircraft.HitPointsCurrent = 0;
                    airArmy.Members.Remove(aircraft);
                    destroyed++;
                }
                if (airArmy.Members.Count == 0)
                {
                    hexSelection?.DeleteArmyIfEmptied(airArmy);
                    messages.Add($"{airArmy.Name} at {FormatGameCoord(airArmy.Hex)}: all aircraft were destroyed because they did not finish the turn at an airfield.");
                }
                else if (destroyed > 0)
                {
                    messages.Add($"{airArmy.Name} at {FormatGameCoord(airArmy.Hex)}: {destroyed} aircraft were destroyed because they did not finish the turn at an airfield.");
                }
            }
            return messages;
        }

        private static string FormatGameCoord(Game.HexGrid.HexCoord hex)
        {
            (int col, int row) = hex.ToOffset();
            return $"({col}, {row})";
        }
    }
}
