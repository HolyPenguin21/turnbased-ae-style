using System.Collections.Generic;
using System.Linq;
using Game.Cards;
using Game.Map;
using Game.Players;
using Game.Units;

namespace Game.Aviation
{
    // Transactional aviation actions. UI and the later AI both call this layer, so AP, Energy,
    // capacity, and slot order cannot drift between a modal button and autonomous behavior.
    public static class AviationActions
    {
        public static ArmyData EnsureAirfield(HexSelectionController hexSelection, PlayerSetupData owner, Game.HexGrid.HexCoord hex)
        {
            if (!AviationRules.IsOwnedAirfieldAt(hex, owner))
                return null;
            ArmyData existing = AviationRules.FindAirfieldAt(hex, owner);
            if (existing != null)
                return existing;

            var airfield = new ArmyData { Name = "Airfield", Owner = owner, Hex = hex, IsAirfield = true };
            ArmyRegistry.Register(airfield);
            // The building is the airfield's map presence; creating another marker would leak
            // its contents through FOW and make a base hex look like it has an extra army.
            hexSelection?.RestackArmiesOn(hex, null);
            return airfield;
        }

        public static bool TryDeployFromCard(CardDefinition definition, PlayerSetupData owner,
            PlayerRoot root, HexSelectionController hexSelection, Game.HexGrid.HexCoord hex, out string failReason)
        {
            failReason = null;
            if (definition == null || !definition.isAviation)
            {
                failReason = "This is not an aviation card.";
                return false;
            }
            ArmyData airfield = EnsureAirfield(hexSelection, owner, hex);
            if (airfield == null)
            {
                failReason = "Aircraft can only be deployed to your Barracks airfield.";
                return false;
            }
            return ArmyActions.DeployUnitFromCard(definition, owner, airfield, root, hexSelection, out failReason);
        }

        public static bool TryLaunch(ArmyData airfield, IList<UnitData> aircraft, FactionCardCatalog catalog,
            HexSelectionController hexSelection, out ArmyData launchedArmy, out string failReason)
        {
            launchedArmy = null;
            failReason = null;
            if (!AviationRules.IsAirfield(airfield) || aircraft == null || aircraft.Count == 0)
            {
                failReason = "Select aircraft from an airfield to launch.";
                return false;
            }
            if (aircraft.Any(unit => unit == null || !airfield.Members.Contains(unit) || !unit.IsAviation))
            {
                failReason = "Every launched card must be an aircraft stored in this airfield.";
                return false;
            }

            // Forming a stack is not a take-off. The common movement activation path charges
            // AP + Energy exactly once, and is also what shows both costs on the route arrow.
            var army = new ArmyData
            {
                Name = catalog != null ? catalog.GetRandomArmyName(ArmyRegistry.AllForOwner(airfield.Owner).Select(existing => existing.Name)) : "Air Wing",
                Owner = airfield.Owner,
                Hex = airfield.Hex,
                IsAirArmy = true,
            };
            foreach (UnitData aircraftCard in aircraft.ToList())
            {
                airfield.Members.Remove(aircraftCard);
                army.AddMemberSorted(aircraftCard);
            }
            ArmyRegistry.Register(army);
            hexSelection?.CreateArmyMarker(army);
            hexSelection?.RestackArmiesOn(airfield.Hex, null);
            launchedArmy = army;
            return true;
        }

        // Kept as the shared landing entry point for future UI/AI callers. Landing is a refuel
        // condition only: cards stay in their formed air army instead of being transferred.
        public static int LandInSlotOrder(ArmyData airArmy, HexSelectionController hexSelection)
        {
            if (!AviationRules.IsValidAirArmy(airArmy))
                return 0;
            ArmyData airfield = EnsureAirfield(hexSelection, airArmy.Owner, airArmy.Hex);
            if (airfield == null)
                return 0;

            foreach (UnitData aircraft in airArmy.Members)
                AviationRules.ResetAfterLanding(aircraft);
            hexSelection?.RestackArmiesOn(airArmy.Hex, null);
            return airArmy.Members.Count;
        }
    }
}
