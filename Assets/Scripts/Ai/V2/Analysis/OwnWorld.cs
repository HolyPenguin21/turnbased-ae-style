using System.Collections.Generic;
using System.Linq;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Units;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  THE ONE DOOR from the planning layers (Strategy, Missions, Evaluation) to live objects
    //  (2026-10-15, snapshot single-source refactoring).
    //
    //  Planning reasons on the WorldSnapshot. Where a planner must hold a concrete live object —
    //  the ArmyData whose units a plan will transfer, the BuildingData whose facility slot it will
    //  fill — it asks the snapshot's observer-scoped view (`WorldSnapshot.Own`, or `Of(player)`
    //  where only the player is in hand). Every query answers for the observer's OWN objects, plus
    //  what the observer's own vision shows right now; a foreign live object is never reachable
    //  from planning (foreign facts come from AiMapMemory / WorldSnapshot.Known). The registries
    //  are read here, in Analysis, and in the live tiers (Continuity, Provisioning, Execution,
    //  Housekeeping, Reaction) — never directly by Strategy, Missions or Evaluation.
    //
    //  Reads are live (the same registries the planners read before), so this is a boundary, not
    //  a cache: a planner that runs after a mutation in the same cycle still sees the mutation.
    // ===========================================================================================
    public sealed class OwnWorld
    {
        public readonly PlayerSetupData Player;

        private OwnWorld(PlayerSetupData player) => Player = player;

        public static OwnWorld Of(PlayerSetupData player) => new OwnWorld(player);

        // ---- armies -----------------------------------------------------------------------------

        // Exactly ArmyRegistry.AllForOwner(Player) (a null observer matches ownerless armies, as
        // that call always did; callers guard their player).
        public IEnumerable<ArmyData> Armies => ArmyRegistry.AllForOwner(Player);

        public ArmyData Army(int armyId) => Armies.FirstOrDefault(a => a != null && a.Id == armyId);

        public IEnumerable<ArmyData> ArmiesAt(HexCoord hex) =>
            ArmyRegistry.AllAt(hex).Where(a => a != null && a.Owner == Player);

        // The own (non-prison) garrison on `hex`.
        public ArmyData GarrisonAt(HexCoord hex) =>
            ArmiesAt(hex).FirstOrDefault(a => a.IsGarrison && !a.IsPrison);

        // The own army `unit` stands in (null: none, or a foreign one).
        public ArmyData ArmyContaining(UnitData unit)
        {
            if (unit == null) return null;
            ArmyData army = ArmyRegistry.FindArmyContaining(unit);
            return army != null && army.Owner == Player ? army : null;
        }

        // ---- buildings --------------------------------------------------------------------------

        public IEnumerable<BuildingData> Buildings =>
            BuildingRegistry.AllBuildings().Where(b => b != null && b.Owner == Player);

        // The own building on `hex` (null: none, or a foreign one).
        public BuildingData BuildingAt(HexCoord hex)
        {
            BuildingData b = BuildingRegistry.FindAt(hex);
            return b != null && b.Owner == Player ? b : null;
        }

        // Whatever building stands on a hex the observer sees right now (own or foreign).
        public BuildingData VisibleBuildingAt(HexCoord hex) =>
            IsVisible(hex) ? BuildingRegistry.FindAt(hex) : null;

        // ---- the observer's own vision ----------------------------------------------------------

        public bool IsVisible(HexCoord hex) => VisionSystem.IsVisible(Player, hex);
        public bool IsVisited(HexCoord hex) => VisionSystem.IsVisited(Player, hex);
        public bool HasEverSeen(HexCoord hex) => VisionSystem.HasEverSeen(Player, hex);
    }
}
