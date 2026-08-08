using System;
using System.Collections.Generic;
using Game.HexGrid;
using Game.Players;
using Game.Styles;
using UnityEngine;

namespace Game.Map
{
    // Every building currently on the map, keyed by hex — mirrors ArmyRegistry's role for
    // armies. Buildings never change which hex they're on once placed, but their on-hex OFFSET
    // still can (e.g. re-centring when the last army sharing their hex leaves), so
    // HexSelectionController needs a way to find a hex's building (BuildingData.Visual) to
    // reposition it — and card-play spawning needs a way to check what a hex's building can do
    // (BuildingData.Abilities) before deploying a unit there.
    public static class BuildingRegistry
    {
        private static readonly Dictionary<HexCoord, BuildingData> ByHex = new Dictionary<HexCoord, BuildingData>();

        // Fired by Unregister — GameTurnController listens for this to check
        // BuildingData.IsStartingCitadel (the win condition). No combat system exists yet to
        // ever actually call Unregister, same "wired for correctness, currently unreachable"
        // status as BaseViewerModalUI's own Repair button.
        public static event Action<BuildingData> BuildingDestroyed;

        public static void Clear()
        {
            ByHex.Clear();
        }

        public static void Register(HexCoord hex, BuildingData building)
        {
            ByHex[hex] = building;
        }

        public static BuildingData FindAt(HexCoord hex)
        {
            return ByHex.TryGetValue(hex, out BuildingData building) ? building : null;
        }

        // Every building on the map regardless of hex or owner — used by GameTurnController's
        // per-turn resource collection, which needs to visit every player's buildings, not just
        // one hex at a time.
        public static IEnumerable<BuildingData> AllBuildings() => ByHex.Values;

        public static void Unregister(HexCoord hex)
        {
            if (!ByHex.TryGetValue(hex, out BuildingData building))
                return;
            ByHex.Remove(hex);
            BuildingDestroyed?.Invoke(building);
        }

        // Shared by every place a building changes hands through combat, per the user's own
        // Siege spec — a defending army wiped out completely (see BattleScreenUI.Combat.cs's
        // HandleBuildingOnArmyDefeat) or an enemy simply walking onto a hex nobody defended at
        // all (see HexSelectionController.Movement.cs's own undefended-building check). A
        // Base-tagged building (a citadel or player-built Base) is CAPTURED intact — ownership
        // only, recoloured to match. Anything else — a bare hero-built extraction facility,
        // which never had a garrison of its own to begin with — has no structure worth
        // capturing, so it's destroyed outright instead, icon and all.
        public static void CaptureOrDestroy(BuildingData building, PlayerSetupData newOwner)
        {
            if (building == null)
                return;
            if (building.HasAbility(BuildingAbilities.Base))
            {
                building.Owner = newOwner;
                if (building.Visual != null)
                    building.Visual.SetColor(newOwner != null ? PlayerColorPalette.Colors[newOwner.ColorIndex] : Color.white);
            }
            else
            {
                Unregister(building.Hex);
                if (building.Visual != null)
                    UnityEngine.Object.Destroy(building.Visual.gameObject);
            }
        }
    }
}
