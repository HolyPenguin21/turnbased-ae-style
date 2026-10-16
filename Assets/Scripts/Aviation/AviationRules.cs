using System.Collections.Generic;
using System.Linq;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Units;
using UnityEngine;

namespace Game.Aviation
{
    // Pure aviation classification and capacity rules. UI, movement, and AI all
    // call these helpers instead of independently interpreting ArmyData's two aviation flags.
    public static class AviationRules
    {
        public static bool IsAviation(UnitData unit) => unit != null && unit.IsAviation;
        // Composition is authoritative for mobile aviation: this also makes visual state recover
        // correctly after a save/load or a drag-created army whose flag was not yet refreshed.
        public static bool IsAirArmy(ArmyData army) => army != null && !army.IsAirfield && !army.IsGarrison
            && !army.IsPrison && army.Members.Count > 0 && army.Members.All(unit => unit.IsAviation);
        public static bool IsAirfield(ArmyData army) => army != null && army.IsAirfield;

        public static IEnumerable<ArmyData> AirfieldsAt(HexCoord hex, PlayerSetupData owner) =>
            ArmyRegistry.AllAt(hex).Where(a => a.IsAirfield && a.Owner == owner)
                .OrderBy(a => a.CarrierUnit == null ? 0 : 1).ThenBy(a => a.Id);

        // One owner-scoped map capability for human placement, flight planning and landing.
        // Fixed airfields keep their established order (starting citadel first, then coordinates):
        // planners break exact ties by this order. Mobile carrier hexes follow.
        public static IEnumerable<HexCoord> OwnedAirfieldHexes(PlayerSetupData owner) =>
            BuildingRegistry.AllBuildings().Where(b => IsAirfieldBuilding(b, owner))
                .OrderByDescending(b => b.IsStartingCitadel).ThenBy(b => b.Hex.Q).ThenBy(b => b.Hex.R)
                .Select(b => b.Hex)
                .Concat(ArmyRegistry.AllForOwner(owner)
                    .Where(a => !a.IsCarrierContainer && !a.IsPrison && a.Members.Any(AviationActions.IsCarrier))
                    .Select(a => a.Hex).OrderBy(h => h.Q).ThenBy(h => h.R))
                .Distinct();

        public static bool IsAirfieldBuilding(BuildingData building, PlayerSetupData owner)
        {
            return building != null && building.Owner == owner && building.AirfieldCapacity > 0;
        }

        public static ArmyData FindAirfieldAt(HexCoord hex, PlayerSetupData owner)
        {
            var fields = AirfieldsAt(hex, owner).ToList();
            return fields.FirstOrDefault(a => FreeContainerCapacity(a) > 0) ?? fields.FirstOrDefault();
        }

        public static bool IsOwnedAirfieldAt(HexCoord hex, PlayerSetupData owner)
        {
            return AirfieldCapacityAt(hex, owner) > 0;
        }

        public static int AirfieldCapacityAt(HexCoord hex, PlayerSetupData owner)
        {
            BuildingData building = BuildingRegistry.FindAt(hex);
            int fixedCapacity = IsAirfieldBuilding(building, owner) ? building.AirfieldCapacity : 0;
            return fixedCapacity + ArmyRegistry.AllAt(hex)
                .Where(a => a.Owner == owner && !a.IsCarrierContainer && !a.IsPrison)
                .SelectMany(a => a.Members).Where(AviationActions.IsCarrier).Sum(u => u.AirfieldCapacity);
        }

        public static int ContainerCapacity(ArmyData container)
        {
            if (container == null) return 0;
            if (container.IsCarrierContainer)
                return AviationActions.IsCarrier(container.CarrierUnit) ? container.CarrierUnit.AirfieldCapacity : 0;
            BuildingData b = BuildingRegistry.FindAt(container.Hex);
            return container.IsAirfield && IsAirfieldBuilding(b, container.Owner) ? b.AirfieldCapacity : 0;
        }

        // The hangar and ready wing share the carrier's physical deck capacity.
        public static int FreeContainerCapacity(ArmyData target, ArmyData excluding = null,
            bool landing = false)
        {
            if (target == null) return 0;
            int used = target.Members.Count;
            if (target.IsCarrierContainer)
            {
                ArmyData paired = target.IsAirfield ? target.CarrierUnit.CarrierFlightDeck
                    : target.CarrierUnit.CarrierAirfield;
                if (paired != null && paired != excluding) used += paired.Members.Count;
            }
            int local = Mathf.Max(0, ContainerCapacity(target) - used);
            // Repacking already stored aircraft on this hex consumes no additional hex slot.
            if (excluding != null && excluding.Hex.Equals(target.Hex)
                && (excluding.IsAirfield || excluding.IsCarrierFlightDeck)) return local;
            int global = landing ? FreeStorageSlots(target.Hex, target.Owner, excluding)
                : FreeAirfieldCapacity(target.Hex, target.Owner, excluding);
            return Mathf.Min(local, global);
        }

        // THE free-slot rule of an airfield, shared by card deployment, transfers, formation,
        // landing planning, rebase, the real landing and UI:
        //   capacity − aircraft stored in the container − aircraft of the owner's own air armies
        //   standing on the hex (they land into it at the end of this turn).
        // `excluding` — an air army whose own aircraft must not be counted against itself (a wing
        // re-checking the field it is landing at, or a stack transferring into the container).
        // In-flight reservations are counted by this same rule, including domain/UI callers.
        // The assignment owner publishes its reservation read here. Domain actions never depend
        // on an AI planner, and there remains only one authoritative sortie registry.
        internal static System.Func<HexCoord, PlayerSetupData, ArmyData, int> ReservedLandingSlots;

        public static int FreeAirfieldCapacity(HexCoord hex, PlayerSetupData owner, ArmyData excluding = null,
            int additionalClaims = 0)
        {
            if (AirfieldCapacityAt(hex, owner) <= 0) return 0;
            int free = RawStorageSlots(hex, owner, excluding) - additionalClaims;
            foreach (ArmyData army in ArmyRegistry.AllAt(hex))
                if (army != excluding && army.Owner == owner && IsAirArmy(army) && !army.IsCarrierFlightDeck)
                    free -= army.Members.Count;
            return Mathf.Max(0, free);
        }

        // capacity − stored aircraft − in-flight reservations: the slots the real landing converts a
        // standing wing's claim into (AviationActions.LandInSlotOrder). Every wing on the hex is
        // landed in turn against this, so two wings can never be landed into one slot.
        public static int FreeStorageSlots(HexCoord hex, PlayerSetupData owner, ArmyData excluding = null)
        {
            return Mathf.Max(0, RawStorageSlots(hex, owner, excluding));
        }

        private static int RawStorageSlots(HexCoord hex, PlayerSetupData owner, ArmyData excluding) =>
            AirfieldCapacityAt(hex, owner) - ArmyRegistry.AllAt(hex)
                .Where(a => a.Owner == owner && a != excluding && (a.IsAirfield || a.IsCarrierFlightDeck))
                .Sum(a => a.Members.Count)
                - (ReservedLandingSlots?.Invoke(hex, owner, excluding) ?? 0);

        // Keeps an army's stored air/ground flag and its marker in step with its roster after a
        // transfer or landing: a stack of aircraft is an air army, an emptied former wing is an
        // ordinary empty shell again (free for ground or air reuse). Containers never change.
        public static void SyncAirArmyShell(ArmyData army, HexSelectionController hexSelection)
        {
            if (army == null || army.IsAirfield || army.IsGarrison || army.IsPrison)
                return;
            if (army.IsCarrierFlightDeck)
            {
                army.IsAirArmy = true;
                return;
            }
            bool air = IsAirArmy(army);
            if (air)
                army.AirWingLanded = false;
            if (army.IsAirArmy == air)
                return;
            army.IsAirArmy = air;
            hexSelection?.RefreshArmyAirLook(army);
        }

        public static bool CanContain(ArmyData target, UnitData unit)
        {
            if (target == null || unit == null || target.IsPrison)
                return false;
            if (unit.IsAviation && target.IsGarrison)
                return false;
            if (target.IsAirfield || target.IsCarrierFlightDeck || IsAirArmy(target))
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

        // Single source of truth for the per-occurrence emergency HP cost: AviationTurnLifecycle.
        // ResolveEndOfTurn applies this to inflict the damage, EmergencyHpPenalty below reads it
        // for UI display — neither hardcodes the 0.5f itself.
        public static int EmergencyHpLoss(UnitData unit)
        {
            return Mathf.CeilToInt(unit.HitPointsMax * 0.5f);
        }

        // The fixed per-occurrence HP cost AviationTurnLifecycle.ResolveEndOfTurn already applies
        // once this unit is in emergency state — same formula, not a UI-side copy of the 0.5f, so
        // this stays correct if that rule's fraction ever changes.
        public static int EmergencyHpPenalty(UnitData unit)
        {
            if (unit == null || !unit.HasEmergencyFlightPenalty)
                return 0;
            return EmergencyHpLoss(unit);
        }

        // Turns of endurance left before this aircraft enters emergency state — full again right
        // after ResetAfterLanding zeroes ConsecutiveUnlandedEnds. Ground units (TurnsWithoutRefuel
        // == 0, never set) have no meaningful fuel and should not be shown one.
        public static int RemainingFuel(UnitData unit)
        {
            return AviationRange.SafeUnlandedEndsRemaining(unit);
        }

        public static void ResetAfterLanding(UnitData aircraft)
        {
            if (!IsAviation(aircraft))
                return;
            aircraft.ConsecutiveUnlandedEnds = 0;
            aircraft.HasEmergencyFlightPenalty = false;
            // A completed landing ends the sortie: the next take-off is a new, paid launch.
            aircraft.SortieLaunchPaid = false;
        }
    }
}
