using System;
using System.Collections.Generic;
using System.Linq;
using Game.Aviation;
using Game.HexGrid;

namespace Game.Map
{
    // Every army currently on the map, keyed by hex — mirrors BuildingRegistry. Unlike that,
    // a hex can hold SEVERAL armies (including more than one belonging to the same player, per
    // the original game's manual), so this is a hex -> list, not hex -> one. Each non-empty
    // ArmyData has its own marker (see ArmyData.Controller/HexSelectionController.
    // CreateArmyMarker) — a unit has no map presence of its own at all — but only one marker
    // per (hex, owner) is ever actually shown at a time (see RestackArmiesOn).
    public static class ArmyRegistry
    {
        private static readonly Dictionary<HexCoord, List<ArmyData>> ByHex = new Dictionary<HexCoord, List<ArmyData>>();
        private static readonly IReadOnlyList<ArmyData> Empty = Array.AsReadOnly(Array.Empty<ArmyData>());

        public static void Clear()
        {
            foreach (List<ArmyData> armies in ByHex.Values)
                foreach (ArmyData army in armies)
                {
                    army.ClearTransport();
                    foreach (var unit in army.Members)
                    {
                        unit.CarrierAirfield = null;
                        unit.CarrierFlightDeck = null;
                    }
                }
            ByHex.Clear();
            ArmyData.ResetIdentitySequence();
        }

        public static void Register(ArmyData army)
        {
            using var mutation = VisionSystem.BeginMutation();
            if (army == null)
                return;
            if (IsRegistered(army)) return;
            if (army.Id < 0 || AllArmies().Any(a => a.Id == army.Id)
                || army.Members.Where(u => u != null).Select(u => u.RuntimeId).Distinct().Count() != army.Members.Count
                || army.Members.Any(u => u == null || u.RuntimeId <= 0 || AllArmies().Any(a => a.Members.Any(m => m.RuntimeId == u.RuntimeId))))
                throw new InvalidOperationException("A live unit can belong to only one registered army.");
            if (!ByHex.TryGetValue(army.Hex, out List<ArmyData> list))
            {
                list = new List<ArmyData>();
                ByHex[army.Hex] = list;
            }
            if (list.Contains(army)) return;
            list.Add(army);
            if (Game.Setup.MatchRestoreScope.IsRestoring) return;
            army.RefreshTransport();
            AviationActions.EnsureCarrierContainers(army);
            VisionSystem.RecomputeFor(army.Owner);
            // Anyone else already watching this hex needs to know it just gained content too —
            // see VisionSystem.NotifyContentChanged's own comment.
            VisionSystem.NotifyContentChanged(army.Hex);
        }

        public static bool IsRegistered(ArmyData army) =>
            army != null && ByHex.TryGetValue(army.Hex, out List<ArmyData> list) && list.Contains(army);

        // Remove the indexed origin before recording a moving army's actual terminal hex.
        // Every notification sees the final, absent state; a repeated removal is a no-op.
        public static void Unregister(ArmyData army, HexCoord? finalHex = null)
        {
            using var mutation = VisionSystem.BeginMutation();
            if (!IsRegistered(army))
                return;
            HexCoord origin = army.Hex;
            ByHex[origin].Remove(army);
            if (Game.Setup.MatchRestoreScope.IsRestoring)
            {
                if (finalHex.HasValue) army.SetHex(finalHex.Value);
                return;
            }
            foreach (var unit in army.Members.ToList())
                AviationActions.RemoveCarrierContainers(unit);
            army.ClearTransport();
            if (finalHex.HasValue) army.SetHex(finalHex.Value);
            VisionSystem.RecomputeFor(army.Owner);
            VisionSystem.NotifyContentChanged(origin);
            if (!army.Hex.Equals(origin))
                VisionSystem.NotifyContentChanged(army.Hex);
            VisionSystem.PublishAfterMutation(ArmyRemoved, army);
        }

        // Protected, detached query with canonical entity references.
        public static IReadOnlyList<ArmyData> At(HexCoord hex) =>
            ByHex.TryGetValue(hex, out List<ArmyData> list) ? list.ToList().AsReadOnly() : Empty;

        // Compatibility adapter for List helpers; never exposes registry storage.
        public static List<ArmyData> AllAt(HexCoord hex) =>
            ByHex.TryGetValue(hex, out List<ArmyData> list) ? new List<ArmyData>(list) : new List<ArmyData>();

        // Every hex currently holding at least one army — for a scan that needs to consider the
        // whole map at once (see GameTurnController's own end-of-turn contested-hex sweep)
        // rather than one hex at a time.
        public static IEnumerable<HexCoord> AllOccupiedHexes() => ByHex.Where(kv => kv.Value.Count > 0).Select(kv => kv.Key).ToArray();

        // The one army every citadel hex starts with (see CitadelSetupController) — this is
        // where deployed Unit/Hero cards land (see CardHandUI.TryPlayCard) before the player
        // manually sorts them into other armies.
        public static ArmyData FindGarrisonAt(HexCoord hex, Players.PlayerSetupData owner)
        {
            foreach (ArmyData army in AllAt(hex))
                if (army.IsGarrison && army.Owner == owner)
                    return army;
            return null;
        }

        // A unit's army membership isn't tracked on UnitData itself, so a move order (or
        // anything else that needs to know "which army is this unit currently in, if any")
        // has to search for it — used to drop a unit from its army the moment it moves off that
        // army's hex (see HexSelectionController.TryIssueMoveOrder), which is what actually
        // frees up the capacity slot it was holding.
        public static ArmyData FindArmyContaining(Units.UnitData unit)
        {
            foreach (List<ArmyData> list in ByHex.Values)
                foreach (ArmyData army in list)
                    if (army.Members.Contains(unit))
                        return army;
            return null;
        }

        // MAP-VIS-01: fired once a relocation's whole transaction (index re-key + recompute +
        // content notifications, below) has fully completed — never mid-transaction. A future
        // subscriber that wants "this army just moved" instead of two separate content-changed
        // notifications for oldHex/newHex can use this; nothing in this project subscribes yet.
        public static event Action<ArmyData, HexCoord, HexCoord> ArmyRelocated;
        public static event Action<ArmyData> ArmyRemoved;

        // Re-keys `army` from wherever it's currently filed to `newHex` — the only way
        // ArmyData.Hex ever changes after creation. Needed now that whole armies move (see
        // HexSelectionController.TryIssueMoveOrder); a no-op if it's already there.
        //
        // MAP-VIS-01 (2026-09-04, project owner's own root-cause report): deliberately NOT
        // implemented as Unregister(army) + army.Hex = newHex + Register(army) any more — those
        // two public methods each fire VisionSystem.RecomputeFor/NotifyContentChanged
        // independently, which used to mean any subscriber (chiefly HexSelectionController's own
        // visual-memory/layout wiring) could observe the army registered NOWHERE at all for the
        // brief window between the Unregister and the Register — a single logical relocation
        // (e.g. a retreat) firing two disjoint "this hex's content changed" events instead of one
        // atomic "this army moved" transaction. That's what let the live building visual and its
        // remembered "Last Seen" clone at oldHex/newHex get refreshed at genuinely different
        // moments, with different HexObjectLayout results each time — a duplicate citadel visual
        // (one centred, one at an edge offset) instead of the one-or-the-other invariant
        // HexSelectionController.Visuals.cs's ReconcileHexVisualState now enforces.
        //
        // Below: re-key the index directly (no events), THEN — once the registry is in its final,
        // fully-consistent state — recompute vision and notify content-changed for both hexes
        // exactly once each. No subscriber can ever observe the intermediate "registered nowhere"
        // state any more.
        public static void MoveArmy(ArmyData army, HexCoord newHex)
        {
            using var mutation = VisionSystem.BeginMutation();
            if (!IsRegistered(army) || army.Hex.Equals(newHex))
                return;

            if (!Game.Setup.MatchRestoreScope.IsRestoring)
                AviationActions.LandWingsBeforeCarrierDeparts(army);
            HexCoord oldHex = army.Hex;
            RelocateInIndex(army, newHex);
            // The whole indexed group is in its final position before observers are notified.
            foreach (var unit in army.Members)
                foreach (ArmyData attached in new[] { unit.CarrierAirfield, unit.CarrierFlightDeck })
                    if (attached != null && IsRegistered(attached)) RelocateInIndex(attached, newHex);

            VisionSystem.RecomputeFor(army.Owner);
            VisionSystem.NotifyContentChanged(oldHex);
            VisionSystem.NotifyContentChanged(newHex);
            if (!Game.Setup.MatchRestoreScope.IsRestoring)
                VisionSystem.PublishAfterMutation(ArmyRelocated, army, oldHex, newHex);
        }

        private static void RelocateInIndex(ArmyData army, HexCoord hex)
        {
            if (ByHex.TryGetValue(army.Hex, out List<ArmyData> oldList)) oldList.Remove(army);
            army.SetHex(hex);
            if (!ByHex.TryGetValue(hex, out List<ArmyData> list)) ByHex[hex] = list = new List<ArmyData>();
            list.Add(army);
        }

        // Every army belonging to one player, across every hex — used to reset
        // HasActivatedThisTurn (and replenish every member's move points) for all of them at
        // the start of that player's turn (see GameTurnController.ReplenishMoveForOwner).
        public static IEnumerable<ArmyData> AllForOwner(Players.PlayerSetupData owner) =>
            ByHex.Values.SelectMany(list => list).Where(army => army.Owner == owner).ToArray();
        internal static IEnumerable<ArmyData> AllArmies() => ByHex.Values.SelectMany(list => list).ToArray();
    }
}
