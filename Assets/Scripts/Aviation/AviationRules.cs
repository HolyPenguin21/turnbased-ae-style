using System.Collections.Generic;
using System.Linq;
using Game.Cards;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Units;
using UnityEngine;

namespace Game.Aviation
{
    // Pure aviation classification and capacity rules.  UI, movement, and the future AI all
    // call these helpers instead of independently interpreting ArmyData's two aviation flags.
    public static class AviationRules
    {
        public static bool IsAviation(UnitData unit) => unit != null && unit.IsAviation;
        // Composition is authoritative for mobile aviation: this also makes visual state recover
        // correctly after a save/load or a drag-created army whose flag was not yet refreshed.
        public static bool IsAirArmy(ArmyData army) => army != null && !army.IsAirfield && !army.IsGarrison
            && !army.IsPrison && army.Members.Count > 0 && army.Members.All(unit => unit.IsAviation);
        public static bool IsAirfield(ArmyData army) => army != null && army.IsAirfield;

        public static bool IsAirfieldBuilding(BuildingData building, PlayerSetupData owner)
        {
            return building != null && building.Owner == owner && building.AirfieldCapacity > 0
                && building.HasAbility(UnitAbilities.Barracks);
        }

        public static ArmyData FindAirfieldAt(HexCoord hex, PlayerSetupData owner)
        {
            return ArmyRegistry.AllAt(hex).FirstOrDefault(army => army.IsAirfield && army.Owner == owner);
        }

        public static bool IsOwnedAirfieldAt(HexCoord hex, PlayerSetupData owner)
        {
            return IsAirfieldBuilding(BuildingRegistry.FindAt(hex), owner);
        }

        public static int AirfieldCapacityAt(HexCoord hex, PlayerSetupData owner)
        {
            BuildingData building = BuildingRegistry.FindAt(hex);
            return IsAirfieldBuilding(building, owner) ? building.AirfieldCapacity : 0;
        }

        public static int FreeAirfieldCapacity(HexCoord hex, PlayerSetupData owner)
        {
            ArmyData airfield = FindAirfieldAt(hex, owner);
            return Mathf.Max(0, AirfieldCapacityAt(hex, owner) - (airfield?.Members.Count ?? 0));
        }

        public static bool CanContain(ArmyData target, UnitData unit)
        {
            if (target == null || unit == null || target.IsPrison)
                return false;
            if (unit.IsAviation && target.IsGarrison)
                return false;
            if (target.IsAirfield || IsAirArmy(target))
                return unit.IsAviation;
            if (unit.IsAviation)
                return target.Members.Count == 0;
            return !target.Members.Any(member => member.IsAviation);
        }

        public static bool IsValidAirArmy(ArmyData army)
        {
            return IsAirArmy(army);
        }

        public static int EffectiveMoveCurrent(UnitData unit)
        {
            if (unit == null)
                return 0;
            return unit.HasEmergencyFlightPenalty ? Mathf.FloorToInt(unit.MoveCurrent * 0.5f) : unit.MoveCurrent;
        }

        public static int EffectiveMoveMax(UnitData unit)
        {
            if (unit == null)
                return 0;
            return unit.HasEmergencyFlightPenalty ? Mathf.FloorToInt(unit.MoveMax * 0.5f) : unit.MoveMax;
        }

        public static int MovementCost(ArmyData army, int terrainCost)
        {
            return IsAirArmy(army) ? 1 : Mathf.Max(1, terrainCost);
        }

        public static void ResetAfterLanding(UnitData aircraft)
        {
            if (!IsAviation(aircraft))
                return;
            aircraft.ConsecutiveUnlandedEnds = 0;
            aircraft.HasEmergencyFlightPenalty = false;
        }
    }
}
