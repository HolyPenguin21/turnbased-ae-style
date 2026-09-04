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

        // Same per-hex flattening as MovementCost above, applied to a whole HexPathfinder route
        // at once — an air army spends exactly 1 MP per hex regardless of terrain, so its route's
        // real cost is just the hex count, not HexPath.TotalCost (which is always terrain-weighted,
        // see HexPathfinder.FindPath's own comment on why it never bakes in a mover-specific rule).
        // Shared by the move-preview arrow and anything else that needs to show/check a route's
        // actual MP cost for `army` instead of assuming a ground army's terrain-weighted one.
        public static int PathMoveCost(ArmyData army, Game.HexGrid.HexPath path)
        {
            if (path == null)
                return 0;
            return IsAirArmy(army) ? path.Hexes.Count - 1 : path.TotalCost;
        }

        // Live delta between normal and penalized movement, for UI display only — derived from
        // the same EffectiveMoveCurrent this unit's actual move budget already uses, so a rule
        // change (e.g. 50% to some other fraction) can't drift out of sync with what the UI shows.
        public static int EmergencyMovePenalty(UnitData unit)
        {
            if (unit == null || !unit.HasEmergencyFlightPenalty)
                return 0;
            return unit.MoveCurrent - EffectiveMoveCurrent(unit);
        }

        // The fixed per-occurrence HP cost AviationTurnLifecycle.ResolveEndOfTurn already applies
        // once this unit is in emergency state — same formula, not a UI-side copy of the 0.5f, so
        // this stays correct if that rule's fraction ever changes.
        public static int EmergencyHpPenalty(UnitData unit)
        {
            if (unit == null || !unit.HasEmergencyFlightPenalty)
                return 0;
            return Mathf.CeilToInt(unit.HitPointsMax * 0.5f);
        }

        // Turns of endurance left before this aircraft enters emergency state — full again right
        // after ResetAfterLanding zeroes ConsecutiveUnlandedEnds. Ground units (TurnsWithoutRefuel
        // == 0, never set) have no meaningful fuel and should not be shown one.
        public static int RemainingFuel(UnitData unit)
        {
            if (unit == null)
                return 0;
            return Mathf.Max(0, unit.TurnsWithoutRefuel - unit.ConsecutiveUnlandedEnds);
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
