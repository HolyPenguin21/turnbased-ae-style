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
                // Ending the turn on an owned airfield is THE completed landing: the aircraft go
                // back into the airfield container (refuelled, sortie closed, no repair) and the
                // wing stays as an empty shell (AviationActions.LandInSlotOrder). Merely passing
                // through an airfield mid-turn lands nothing. Aircraft that find no free slot stay
                // airborne and take the ordinary unlanded end below.
                if (AviationRules.IsOwnedAirfieldAt(airArmy.Hex, owner))
                {
                    int landed = AviationActions.LandInSlotOrder(airArmy, hexSelection);
                    if (airArmy.Members.Count == 0)
                        continue;
                    messages.Add($"{airArmy.Name} at {FormatGameCoord(airArmy.Hex)}: the airfield is full — {airArmy.Members.Count} aircraft could not land"
                        + (landed > 0 ? $" ({landed} landed)." : "."));
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
                    // Mutates HP/roster directly (never through ArmyRegistry), so any player
                    // watching this hex needs an explicit nudge — see the matching comment on
                    // BattleScreenUI.Combat.cs's OnBattleOutcomeAcknowledged for the same gap in
                    // ground combat and why a stale AiMapMemory.KnowledgeVersion otherwise serves
                    // pre-damage sightings for this hex.
                    VisionSystem.NotifyContentChanged(airArmy.Hex);
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
            // Axial (q, r), the same coordinate the map labels and the logs show.
            return $"({hex.Q}, {hex.R})";
        }
    }
}
