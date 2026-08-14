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
        }

        public static void Unregister(ArmyData army)
        {
            if (army == null)
                return;
            if (ByHex.TryGetValue(army.Hex, out List<ArmyData> list))
                list.Remove(army);
            VisionSystem.RecomputeFor(army.Owner);
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

        // Re-keys `army` from wherever it's currently filed to `newHex` — the only way
        // ArmyData.Hex ever changes after creation. Needed now that whole armies move (see
        // HexSelectionController.TryIssueMoveOrder); a no-op if it's already there.
        // Note: Unregister/Register both already recompute the owner's vision on their own (see
        // above) — the per-step recompute during the move itself (see ArmyController.MoveRoutine's
        // shouldStopEarly callback) already reflects wherever the army actually is mid-animation,
        // so this re-keying doesn't need its own extra recompute on top of that.
        public static void MoveArmy(ArmyData army, HexCoord newHex)
        {
            if (army == null || army.Hex.Equals(newHex))
                return;
            Unregister(army);
            army.Hex = newHex;
            Register(army);
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
