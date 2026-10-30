using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Game.Cards;
using Game.Economy;
using Game.Map;
using Game.Players;
using Game.Units;

namespace Game.Aviation
{
    // Transactional aviation actions. UI and AI both call this layer, so AP, Energy,
    // capacity, and slot order cannot drift between a modal button and autonomous behavior.
    public static partial class AviationActions
    {
        public static event System.Action<ArmyData, ArmyData, UnitData> AircraftLanded;
        // All landing/storage paths publish once after their roster/refuel transaction commits.
        internal static void NotifyLandingCompleted(ArmyData wing, ArmyData receiver, UnitData aircraft)
        {
            if (wing != null && receiver != null && aircraft != null && aircraft.IsAviation
                && receiver.Owner == wing.Owner && receiver.Members.Contains(aircraft)
                && !aircraft.SortieLaunchPaid)
                VisionSystem.PublishAfterMutation(AircraftLanded, wing, receiver, aircraft);
        }
        public static ArmyData EnsureAirfield(HexSelectionController hexSelection, PlayerSetupData owner, Game.HexGrid.HexCoord hex)
        {
            if (!AviationRules.IsOwnedAirfieldAt(hex, owner))
                return null;
            if (AviationRules.IsAirfieldBuilding(BuildingRegistry.FindAt(hex), owner)
                && !AviationRules.AirfieldsAt(hex, owner).Any(a => a.CarrierUnit == null))
                ArmyRegistry.Register(new ArmyData { Name = "Airfield", Owner = owner, Hex = hex, IsAirfield = true });
            // The building is the airfield's map presence; creating another marker would leak
            // its contents through FOW and make a base hex look like it has an extra army.
            hexSelection?.RestackArmiesOn(hex, null);
            return AviationRules.FindAirfieldAt(hex, owner);
        }

        // sourceCard (optional): forwarded to ArmyActions.DeployUnitFromCard so a Research/
        // Production-created aviation card pays activationApCost and skips its already-paid
        // ResourceCost. AI callers pass none.
        public static bool TryDeployFromCard(CardDefinition definition, PlayerSetupData owner,
            PlayerRoot root, HexSelectionController hexSelection, Game.HexGrid.HexCoord hex, out string failReason,
            CardDefinition attachedEquipment = null, CardData sourceCard = null, ArmyData airfieldTarget = null)
        {
            failReason = null;
            if (definition == null || !definition.isAviation)
            {
                failReason = "Select an aircraft card.";
                return false;
            }
            ArmyData airfield = airfieldTarget ?? EnsureAirfield(hexSelection, owner, hex);
            if (airfield == null || !airfield.IsAirfield || airfield.Owner != owner
                || !airfield.Hex.Equals(hex) || !ArmyRegistry.IsRegistered(airfield))
            {
                failReason = "Deploy aircraft to your airfield first.";
                return false;
            }
            return ArmyActions.DeployUnitFromCard(definition, owner, airfield, root, hexSelection, out failReason, attachedEquipment, sourceCard);
        }

        public static bool TryLaunch(ArmyData airfield, IList<UnitData> aircraft, FactionCardCatalog catalog,
            HexSelectionController hexSelection, out ArmyData launchedArmy, out string failReason)
        {
            using var mutation = VisionSystem.BeginMutation();
            launchedArmy = null;
            failReason = null;
            if (!ArmyRegistry.IsRegistered(airfield) || !AviationRules.IsAirfield(airfield) || aircraft == null || aircraft.Count == 0)
            {
                failReason = "Select aircraft from an airfield to launch.";
                return false;
            }
            if (aircraft.Distinct().Count() != aircraft.Count
                || aircraft.Any(unit => unit == null || !airfield.Members.Contains(unit) || !unit.IsAviation))
            {
                failReason = "Select only aircraft stored at this airfield.";
                return false;
            }

            // Forming a stack is not a take-off. The common movement activation path charges
            // AP + Energy exactly once, and is also what shows both costs on the route arrow.
            // A launch is exactly the selected aircraft: an occupied ready deck already belongs to
            // an earlier formation, so this one becomes a separate wing of the same carrier.
            ArmyData deck = CarrierLaunchDeck(airfield, hexSelection);
            var army = deck != null && deck.Members.Count == 0 ? deck : new ArmyData
            {
                Name = catalog != null ? catalog.GetRandomArmyName(ArmyRegistry.AllForOwner(airfield.Owner).Select(existing => existing.Name)) : "Air Wing",
                Owner = airfield.Owner,
                Hex = airfield.Hex,
                IsAirArmy = true,
                LaunchCarrier = airfield.CarrierUnit,
            };
            foreach (UnitData aircraftCard in aircraft.ToList())
            {
                airfield.Members.Remove(aircraftCard);
                army.AddMemberSorted(aircraftCard);
            }
            if (!ArmyRegistry.IsRegistered(army)) ArmyRegistry.Register(army);
            if (army.Controller == null) hexSelection?.CreateArmyMarker(army);
            else hexSelection?.RefreshArmyAirLook(army); // a carrier deck marker was made empty (ground look)
            hexSelection?.RestackArmiesOn(airfield.Hex, null);
            airfield.RefreshTransport(); army.RefreshTransport();
            VisionSystem.RecomputeFor(army.Owner);
            VisionSystem.NotifyContentChanged(army.Hex);
            launchedArmy = army;
            return true;
        }

        // Match-owned aircraft return. The hand model grants the ordinary card (with its
        // attachments); optional presentation only observes the completed result.
        public static void ReturnAircraftToDeck(ArmyData army, HexSelectionController hexSelection)
        {
            if (army == null || (!AviationRules.IsAirfield(army) && !AviationRules.IsAirArmy(army)
                && !army.IsCarrierContainer))
                return;
            using var mutation = VisionSystem.BeginMutation();
            var aircraftToReturn = army.Members.ToList(); army.Members.Clear();
            foreach (UnitData aircraft in aircraftToReturn)
            {
                StealthSystem.OnUnitRemoved(aircraft);
                if (aircraft.OriginatingCard == null || army.Owner == null) continue;
                var card = new CardData(aircraft.OriginatingCard) { Equipment = aircraft.Equipment, Mutator = aircraft.Mutator };
                if ((army.Owner.Hand ??= new PlayerHandData(null, 0)).TryAddCard(card))
                    VisionSystem.PublishAfterMutation(AircraftReturnedToHand, army.Owner, card);
            }
            army.RefreshTransport(); ArmyActions.DeleteArmyIfEmptied(army);
            VisionSystem.RecomputeFor(army.Owner); VisionSystem.NotifyContentChanged(army.Hex);
        }
        internal static void ReturnStaleAviationAt(Game.HexGrid.HexCoord hex, BuildingData building)
        {
            using var mutation = VisionSystem.BeginMutation();
            foreach (ArmyData army in ArmyRegistry.At(hex).Where(a => !a.IsCarrierContainer
                && (AviationRules.IsAirfield(a) || AviationRules.IsAirArmy(a))
                && (a.IsAirfield ? !AviationRules.IsAirfieldBuilding(building, a.Owner)
                    : !AviationRules.IsOwnedAirfieldAt(hex, a.Owner))).ToArray())
            { ReturnAircraftToDeck(army, null); if (army.IsAirfield) DeleteReturnedWing(army, null); }
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
        public static bool CanStrikeAtCurrentHex(ArmyData airArmy) =>
            CanStrikeAtCurrentHex(airArmy, AirStrikePolicy.Standard);

        public static bool CanStrikeAtCurrentHex(ArmyData airArmy, AirStrikePolicy policy)
        {
            if (!policy.AllowsStrike || !AviationRules.IsValidAirArmy(airArmy))
                return false;
            if (!airArmy.Members.Any(unit => !unit.HasAirAttackedThisTurn))
                return false;
            return policy.MinimumSurvivors == 0 && AviationCombatPresenter.CanExplosionStrikeAt(airArmy, airArmy.Hex)
                || AviationCombatPresenter.FindAirStrikeTargetsAt(airArmy.Hex, airArmy.Owner,
                policy.ExactTargetArmyId).Sum(target => target.Members.Count) > policy.MinimumSurvivors;
        }

        public static IEnumerator ResolveStationaryStrike(AviationCombatPresenter presenter, ArmyData airArmy,
            AviationCombatPresenter.AirStrikeResult result = null) =>
            ResolveStationaryStrike(presenter, airArmy, AirStrikePolicy.Standard, result);

        // A stationary strike spends no MP. During a paid sortie it is free; it pays only the
        // launch share still owed by members that have not paid yet (the same
        // ArmyActions.CanAffordActivation/TryPayActivation every move order uses).
        public static bool CanActivateForStationaryStrike(ArmyData airArmy)
        {
            if (!AviationRules.IsValidAirArmy(airArmy))
                return false;
            return ArmyActions.CanAffordActivation(airArmy, PlayerRootRegistry.FindFor(airArmy.Owner));
        }

        public static IEnumerator ResolveStationaryStrike(AviationCombatPresenter presenter, ArmyData airArmy,
            AirStrikePolicy policy, AviationCombatPresenter.AirStrikeResult result = null)
        {
            if (presenter == null || !CanActivateForStationaryStrike(airArmy)
                || !CanStrikeAtCurrentHex(airArmy, policy))
                yield break;
            if (!ArmyActions.TryPayActivation(airArmy, PlayerRootRegistry.FindFor(airArmy.Owner)))
                yield break;
            yield return presenter.ResolveAirStrikeAtCurrentHex(airArmy, airArmy.Hex, policy, result);
        }

        // THE completed landing (end of turn on an owned airfield — AviationTurnLifecycle, or any
        // caller that lands a wing explicitly): the wing's aircraft go back into the airfield's
        // container in slot order, as many as its free storage (AviationRules.FreeStorageSlots)
        // allows. Each landed aircraft is
        // refuelled and its sortie closed (ResetAfterLanding — no repair). Carrier arrivals are
        // deleted once empty; a carrier's own ready deck persists. Ordinary fixed-airfield wings
        // retain their existing empty-shell lifetime. Returns the number
        // of aircraft landed; any that found no slot stay airborne in the wing.
        public static int LandInSlotOrder(ArmyData airArmy, HexSelectionController hexSelection)
        {
            using var mutation = VisionSystem.BeginMutation();
            if (!AviationRules.IsValidAirArmy(airArmy))
                return 0;
            if (EnsureAirfield(hexSelection, airArmy.Owner, airArmy.Hex) == null) return 0;
            int landed = 0;
            bool carrierLanding = false;
            foreach (ArmyData airfield in AviationRules.AirfieldsAt(airArmy.Hex, airArmy.Owner)
                .OrderBy(a => a.CarrierUnit != null && a.CarrierUnit == airArmy.LaunchCarrier ? 0 : 1).ToList())
            {
                int free = AviationRules.FreeContainerCapacity(airfield, airArmy, landing: true);
                var landing = airArmy.Members.Take(free).ToList();
                foreach (UnitData aircraft in landing)
                {
                    airArmy.Members.Remove(aircraft);
                    AviationRules.ResetAfterLanding(aircraft);
                    airfield.AddMemberSorted(aircraft);
                }
                if (landing.Count == 0) continue;
                landed += landing.Count;
                carrierLanding |= airfield.IsCarrierContainer;
                NotifyLandingCompleted(airArmy, airfield, landing[0]);
            }
            if (landed == 0) return 0;
            if (airArmy.Members.Count == 0) airArmy.AirWingLanded = true;
            AviationRules.SyncAirArmyShell(airArmy, hexSelection);
            if (!airArmy.IsCarrierContainer && airArmy.Members.Count == 0
                && (carrierLanding || airArmy.WasCarrierWing)) DeleteReturnedWing(airArmy, hexSelection);
            hexSelection?.RestackArmiesOn(airArmy.Hex, null);
            VisionSystem.NotifyContentChanged(airArmy.Hex);
            return landed;
        }
    }
}
