using System;
using System.Collections.Generic;
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
        private static readonly List<ArmyData> Empty = new List<ArmyData>();

        public static void Clear()
        {
            ByHex.Clear();
        }

        public static void Register(ArmyData army)
        {
            if (army == null)
                return;
            if (!ByHex.TryGetValue(army.Hex, out List<ArmyData> list))
            {
                list = new List<ArmyData>();
                ByHex[army.Hex] = list;
            }
            list.Add(army);
            VisionSystem.RecomputeFor(army.Owner);
            // Anyone else already watching this hex needs to know it just gained content too —
            // see VisionSystem.NotifyContentChanged's own comment.
            VisionSystem.NotifyContentChanged(army.Hex);
        }

        public static void Unregister(ArmyData army)
        {
            if (army == null)
                return;
            if (ByHex.TryGetValue(army.Hex, out List<ArmyData> list))
                list.Remove(army);
            VisionSystem.RecomputeFor(army.Owner);
            VisionSystem.NotifyContentChanged(army.Hex);
        }

        // Never null — callers can foreach this directly without a null check.
        public static List<ArmyData> AllAt(HexCoord hex)
        {
            return ByHex.TryGetValue(hex, out List<ArmyData> list) ? list : Empty;
        }

        // Every hex currently holding at least one army — for a scan that needs to consider the
        // whole map at once (see GameTurnController's own end-of-turn contested-hex sweep)
        // rather than one hex at a time.
        public static IEnumerable<HexCoord> AllOccupiedHexes() => ByHex.Keys;

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
            if (army == null || army.Hex.Equals(newHex))
                return;

            HexCoord oldHex = army.Hex;
            if (ByHex.TryGetValue(oldHex, out List<ArmyData> oldList))
                oldList.Remove(army);
            army.Hex = newHex;
            if (!ByHex.TryGetValue(newHex, out List<ArmyData> newList))
            {
                newList = new List<ArmyData>();
                ByHex[newHex] = newList;
            }
            newList.Add(army);

            VisionSystem.RecomputeFor(army.Owner);
            VisionSystem.NotifyContentChanged(oldHex);
            VisionSystem.NotifyContentChanged(newHex);
            ArmyRelocated?.Invoke(army, oldHex, newHex);
        }

        // Every army belonging to one player, across every hex — used to reset
        // HasActivatedThisTurn (and replenish every member's move points) for all of them at
        // the start of that player's turn (see GameTurnController.ReplenishMoveForOwner).
        public static IEnumerable<ArmyData> AllForOwner(Players.PlayerSetupData owner)
        {
            foreach (List<ArmyData> list in ByHex.Values)
                foreach (ArmyData army in list)
                    if (army.Owner == owner)
                        yield return army;
        }
    }
}
