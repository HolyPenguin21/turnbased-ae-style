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

            PlayerRoot root = PlayerRootRegistry.FindFor(airfield.Owner);
            // Launch mirrors a normal army's first movement activation: every aircraft pays its
            // own activation AP, plus its aviation-specific Energy cost.  Card play AP was
            // already paid when it entered the airfield and must never be charged twice.
            int ap = aircraft.Sum(unit => unit.ActivationApCost);
            int energy = aircraft.Sum(unit => unit.LaunchEnergyCost);
            if (root == null || !root.CanSpendActionPoints(ap)
                || root.GetResource(Game.Economy.ResourceType.Energy) < energy)
            {
                failReason = $"Not enough AP or Energy to launch ({ap} AP, {energy} Energy).";
                return false;
            }

            root.SpendActionPoints(ap);
            if (energy > 0)
                root.AddResource(Game.Economy.ResourceType.Energy, -energy);
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

        // Only called at end turn. Passing through a friendly base earlier in a route must not
        // land aircraft, otherwise a long ordered flight would stop before its player intended.
        public static int LandInSlotOrder(ArmyData airArmy, HexSelectionController hexSelection)
        {
            if (!AviationRules.IsValidAirArmy(airArmy))
                return 0;
            ArmyData airfield = EnsureAirfield(hexSelection, airArmy.Owner, airArmy.Hex);
            if (airfield == null)
                return 0;

            int landed = 0;
            while (airArmy.Members.Count > 0
                && airfield.Members.Count < AviationRules.AirfieldCapacityAt(airArmy.Hex, airArmy.Owner))
            {
                UnitData aircraft = airArmy.Members[0];
                airArmy.Members.RemoveAt(0);
                AviationRules.ResetAfterLanding(aircraft);
                airfield.AddMemberSorted(aircraft);
                landed++;
            }
            hexSelection?.RestackArmiesOn(airArmy.Hex, null);
            return landed;
        }
    }
}
