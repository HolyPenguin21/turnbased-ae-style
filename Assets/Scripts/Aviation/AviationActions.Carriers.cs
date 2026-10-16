using System;
using System.Linq;
using Game.Cards;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Units;
using UnityEngine;

namespace Game.Aviation
{
    // Carrier lifetime is part of the existing aviation domain. The live Unit owns its hangar
    // and launch deck; the registry remains the only map index. No separate fleet registry.
    public static partial class AviationActions
    {
        public static event Action<PlayerSetupData, CardData> AircraftReturnedToHand;
        // Map-side deletion (selection/marker) for a wing emptied without a selection reference.
        public static event Action<ArmyData> ReturnedWingDeletionRequested;

        public static bool IsCarrier(UnitData unit) => unit != null && !unit.IsHero && !unit.IsAviation
            && !unit.IsPrisoner && unit.HitPointsCurrent > 0 && unit.AirfieldCapacity > 0;

        public static void EnsureCarrierContainers(ArmyData host)
        {
            if (host == null || host.IsCarrierContainer || host.IsAirfield || host.IsPrison
                || host.Owner == null || !ArmyRegistry.IsRegistered(host)) return;
            foreach (UnitData unit in host.Members.Where(IsCarrier).ToList())
            {
                string name = unit.Name;
                if (unit.CarrierAirfield == null || !ArmyRegistry.IsRegistered(unit.CarrierAirfield))
                {
                    unit.CarrierAirfield = new ArmyData
                    {
                        Name = name + " - Airfield", Owner = host.Owner, Hex = host.Hex,
                        CarrierUnit = unit, IsAirfield = true,
                    };
                    ArmyRegistry.Register(unit.CarrierAirfield);
                }
                if (unit.CarrierFlightDeck == null || !ArmyRegistry.IsRegistered(unit.CarrierFlightDeck))
                {
                    unit.CarrierFlightDeck = new ArmyData
                    {
                        Name = name + " - Air Wing", Owner = host.Owner, Hex = host.Hex,
                        CarrierUnit = unit, IsAirArmy = true, LaunchCarrier = unit,
                    };
                    ArmyRegistry.Register(unit.CarrierFlightDeck);
                }
            }
        }

        // Called after a completed roster transaction, never between its remove and add halves.
        public static void RefreshCarrierContainers(ArmyData host)
        {
            EnsureCarrierContainers(host);
            if (host == null || host.IsCarrierContainer) return;
            foreach (UnitData unit in host.Members.Where(IsCarrier).ToList())
                foreach (ArmyData container in new[] { unit.CarrierAirfield, unit.CarrierFlightDeck })
                    if (container != null && !container.Hex.Equals(host.Hex))
                        ArmyRegistry.MoveArmy(container, host.Hex);
        }

        // Charging the first real flight action is the take-off boundary. A ready wing may
        // still travel on the deck until then; an airborne wing never follows the carrier.
        public static void DetachCarrierWing(ArmyData wing)
        {
            if (wing?.IsCarrierFlightDeck != true || wing.Members.Count == 0) return;
            UnitData carrier = wing.CarrierUnit;
            wing.CarrierUnit = null;
            wing.Name = $"{carrier.Name} - Sortie {wing.Id}";
            carrier.CarrierFlightDeck = null;
            EnsureCarrierContainers(ArmyRegistry.FindArmyContaining(carrier));
            VisionSystem.NotifyContentChanged(wing.Hex);
        }

        public static void RemoveCarrierContainers(UnitData carrier, HexSelectionController selection = null)
        {
            if (carrier == null) return;
            ArmyData hangar = carrier.CarrierAirfield;
            ArmyData deck = carrier.CarrierFlightDeck;
            carrier.CarrierAirfield = null;
            carrier.CarrierFlightDeck = null;
            foreach (ArmyData container in new[] { hangar, deck })
            {
                if (container == null) continue;
                // Keep its aviation identity until the existing return-to-hand action completes.
                ReturnAircraftToDeck(container, selection);
                container.CarrierUnit = null;
                DeleteReturnedWing(container, selection);
            }
        }

        // A fully landed carrier sortie must disappear even on a Barracks hex. Empty ground
        // shells retain their existing lifetime; only the carrier's own deck is persistent.
        public static void DeleteReturnedWing(ArmyData wing, HexSelectionController selection)
        {
            if (wing == null || wing.Members.Count > 0 || wing.IsCarrierContainer) return;
            if (selection != null) selection.DeleteArmyIfEmptied(wing, force: true);
            else if (ReturnedWingDeletionRequested != null) ReturnedWingDeletionRequested(wing);
            else
            {
                ArmyController controller = wing.Controller;
                wing.Controller = null;
                if (controller != null)
                {
                    controller.CancelMovement();
                    UnityEngine.Object.Destroy(controller.gameObject);
                }
                ArmyRegistry.Unregister(wing);
            }
        }

        // A carrier hex is an airfield only while the carrier stands on it, but landing happens at
        // end of turn. Before the last carrier leaves a hex without a fixed airfield, own wings
        // that finished their flight there land aboard now: the same end-of-turn landing brought
        // forward, so an arrived sortie is never stranded by a later ground move.
        internal static void LandWingsBeforeCarrierDeparts(ArmyData host)
        {
            if (host == null || host.Owner == null || host.IsCarrierContainer || host.IsPrison
                || !host.Members.Any(IsCarrier))
                return;
            HexCoord hex = host.Hex;
            if (AviationRules.IsAirfieldBuilding(BuildingRegistry.FindAt(hex), host.Owner)
                || ArmyRegistry.AllAt(hex).Any(a => a != host && a.Owner == host.Owner
                    && !a.IsCarrierContainer && !a.IsPrison && a.Members.Any(IsCarrier)))
                return;
            foreach (ArmyData wing in ArmyRegistry.AllAt(hex).Where(a => a.Owner == host.Owner
                && !a.IsCarrierContainer && AviationRules.IsValidAirArmy(a)
                && (a.Controller == null || !a.Controller.IsMoving)).ToList())
                LandInSlotOrder(wing, null);
        }

        public static ArmyData FindStoredAircraftSource(HexCoord hex, PlayerSetupData owner,
            System.Collections.Generic.IEnumerable<UnitData> aircraft) => aircraft == null ? null
            : AviationRules.AirfieldsAt(hex, owner).FirstOrDefault(a => aircraft.All(a.Members.Contains));

        // Reuse the launch deck for the common formation path used by both UI transfers and AI.
        internal static ArmyData CarrierLaunchDeck(ArmyData airfield, HexSelectionController selection)
        {
            UnitData carrier = airfield?.CarrierUnit;
            if (!IsCarrier(carrier)) return null;
            EnsureCarrierContainers(ArmyRegistry.FindArmyContaining(carrier));
            ArmyData deck = carrier.CarrierFlightDeck;
            if (deck != null && deck.Controller == null && selection != null)
                selection.CreateArmyMarker(deck);
            return deck;
        }
    }
}
