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
                AircraftLanded?.Invoke(wing, receiver, aircraft);
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
                failReason = "This is not an aviation card.";
                return false;
            }
            ArmyData airfield = airfieldTarget ?? EnsureAirfield(hexSelection, owner, hex);
            if (airfield == null || !airfield.IsAirfield || airfield.Owner != owner
                || !airfield.Hex.Equals(hex) || !ArmyRegistry.IsRegistered(airfield))
            {
                failReason = "Aircraft can only be deployed to your airfield.";
                return false;
            }
            return ArmyActions.DeployUnitFromCard(definition, owner, airfield, root, hexSelection, out failReason, attachedEquipment, sourceCard);
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
            if (aircraft.Distinct().Count() != aircraft.Count
                || aircraft.Any(unit => unit == null || !airfield.Members.Contains(unit) || !unit.IsAviation))
            {
                failReason = "Every launched card must be an aircraft stored in this airfield.";
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
            if (army == null || (!AviationRules.IsAirfield(army) && !AviationRules.IsAirArmy(army)
                && !army.IsCarrierContainer))
                return;
            foreach (UnitData aircraft in army.Members.ToList())
            {
                army.Members.Remove(aircraft);
                Game.Map.StealthSystem.OnUnitRemoved(aircraft);
                if (aircraft.OriginatingCard != null)
                {
                    // Hand back a CardData, not just the definition, so any Equipment attached
                    // to this aircraft rides home with its card — replaying it then respawns the
                    // plane fully kitted. Its cost isn't re-charged: this is the same card
                    // instance's gear, restored, not a fresh attach (see CardHandUI.AddCardToHand).
                    //
                    // INTENTIONAL: this is a fresh, ordinary CardData — ResearchProductionCreated
                    // is left at its default false even if the aircraft was originally minted by a
                    // Research/Production Challenge. Once a produced card has been played, the
                    // "first activation" concessions (activationApCost instead of apCost, no
                    // ResourceCost) are spent for good; a unit the game hands back to hand is an
                    // ordinary card and pays full apCost + ResourceCost on its next play. Do NOT
                    // thread the production flag through UnitData / OriginatingCard to "preserve"
                    // it here — that is not a bug (see CardData.ResearchProductionCreated).
                    var returnedCard = new CardData(aircraft.OriginatingCard) { Equipment = aircraft.Equipment, Mutator = aircraft.Mutator };
                    if (hexSelection != null) hexSelection.GrantCard(army.Owner, returnedCard);
                    else AircraftReturnedToHand?.Invoke(army.Owner, returnedCard);
                }
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
        public static bool CanStrikeAtCurrentHex(ArmyData airArmy) =>
            CanStrikeAtCurrentHex(airArmy, AirStrikePolicy.Standard);

        public static bool CanStrikeAtCurrentHex(ArmyData airArmy, AirStrikePolicy policy)
        {
            if (!policy.AllowsStrike || !AviationRules.IsValidAirArmy(airArmy))
                return false;
            if (!airArmy.Members.Any(unit => !unit.HasAirAttackedThisTurn))
                return false;
            return AviationCombatPresenter.FindAirStrikeTargetsAt(airArmy.Hex, airArmy.Owner,
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
