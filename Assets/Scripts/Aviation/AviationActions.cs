using System.Collections;
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
            PlayerRoot root, HexSelectionController hexSelection, Game.HexGrid.HexCoord hex, out string failReason,
            CardDefinition attachedEquipment = null)
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
            return ArmyActions.DeployUnitFromCard(definition, owner, airfield, root, hexSelection, out failReason, attachedEquipment);
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

        // Shared by the aviation service's own BuildingRegistry subscription (see
        // HexSelectionController.ReturnStaleAviationAt) — capturing or destroying the building
        // that hosts an airfield leaves its STORED (never-launched) aircraft, and any already-
        // launched air army still parked right there on the same hex, with no owner able to fly
        // them, so each one's card goes back to its original owner's hand (same destination a
        // hex-event reward card lands in — see HexSelectionController.GrantCard) instead of just
        // vanishing — per the project owner's own spec (2026-08-26): "все воздушные юниты (из
        // ангара и армий) снова кладутся в руку игроку, чтобы он их снова разыгрывал". Works on
        // either an IsAirfield container or a real IsAirArmy formation — both just end up as a
        // list of aircraft UnitData to convert back into cards.
        public static void ReturnAircraftToDeck(ArmyData army, HexSelectionController hexSelection)
        {
            if (!AviationRules.IsAirfield(army) && !AviationRules.IsAirArmy(army))
                return;
            foreach (UnitData aircraft in army.Members.ToList())
            {
                army.Members.Remove(aircraft);
                Game.Map.StealthSystem.OnUnitRemoved(aircraft);
                if (aircraft.OriginatingCard != null)
                    hexSelection?.GrantCard(army.Owner, aircraft.OriginatingCard);
            }
            hexSelection?.DeleteArmyIfEmptied(army);
        }

        // Stationary air strike — an air army already sitting on a hex attacking whatever enemy
        // content shares it again, without moving, once HasAirAttackedThisTurn resets for a new
        // turn. Added 2026-08-26 (repeat-strike consistency follow-up) as the one shared entry
        // point for this capability: AiAggressionPlanner's own repeat-strike scoring (TryEnter/
        // TryContinueLoiterAtTarget) decides WHEN to use it, but the capability itself — an
        // already-parked helicopter attacking again — was never AI-specific, only its sole caller
        // was. A future human control (e.g. a button while a helicopter sits on a live target)
        // calls the same two methods, so AP/targeting/HasAirAttackedThisTurn rules can never drift
        // between the two callers, same reason every other method in this class already lives here
        // rather than inside AiAggressionPlanner or a UI script.
        public static bool CanStrikeAtCurrentHex(ArmyData airArmy)
        {
            if (!AviationRules.IsValidAirArmy(airArmy))
                return false;
            if (!airArmy.Members.Any(unit => !unit.HasAirAttackedThisTurn))
                return false;
            return AviationCombatPresenter.FindAirStrikeTargetsAt(airArmy.Hex, airArmy.Owner).Count > 0;
        }

        public static IEnumerator ResolveStationaryStrike(AviationCombatPresenter presenter, ArmyData airArmy,
            AviationCombatPresenter.AirStrikeResult result = null)
        {
            if (presenter == null || airArmy == null)
                yield break;
            yield return presenter.ResolveAirStrikeAtCurrentHex(airArmy, airArmy.Hex, result);
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
