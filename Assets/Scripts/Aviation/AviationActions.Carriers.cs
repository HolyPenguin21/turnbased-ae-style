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

        public static bool IsCarrier(UnitData unit) => unit != null && !unit.IsHero && !unit.IsAviation
            && !unit.IsPrisoner && unit.HitPointsCurrent > 0 && unit.AirfieldCapacity > 0;

        public static void EnsureCarrierContainers(ArmyData host)
        {
            if (host == null || host.IsCarrierContainer || host.IsAirfield || host.IsPrison
                || host.Owner == null || !ArmyRegistry.IsRegistered(host)) return;
            foreach (UnitData unit in host.Members.Where(IsCarrier).ToList())
            {
                string name = $"{unit.Name} {unit.RuntimeId}";
                if (unit.CarrierAirfield == null || !ArmyRegistry.IsRegistered(unit.CarrierAirfield))
                {
                    unit.CarrierAirfield = new ArmyData
                    {
                        Name = name + " — Airfield", Owner = host.Owner, Hex = host.Hex,
                        CarrierUnit = unit, IsAirfield = true,
                    };
                    ArmyRegistry.Register(unit.CarrierAirfield);
                }
                if (unit.CarrierFlightDeck == null || !ArmyRegistry.IsRegistered(unit.CarrierFlightDeck))
                {
                    unit.CarrierFlightDeck = new ArmyData
                    {
                        Name = name + " — Air Wing", Owner = host.Owner, Hex = host.Hex,
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
            wing.Name = $"{carrier.Name} {carrier.RuntimeId} — Sortie {wing.Id}";
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
