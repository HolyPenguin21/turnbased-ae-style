using System.Collections.Generic;
using Game.Core;
using Game.Players;
using UnityEngine;

namespace Game.Map
{
    // Where a hex's occupants sit relative to its centre, resolved fresh from what's actually
    // on the hex right now rather than baked into one fixed per-slot layout. Every offset is in
    // hex-radius units (x = left/right, y = world Z), same convention GameConfig's
    // buildingIconOffset/armyIconOffset/twoArmySideOffset already use — multiply by
    // HexMap.OuterRadius and add to HexToWorld(hex) to get a world position.
    //
    // Rules, in priority order:
    //  1. Exactly one occupant total (a lone building, or a lone army) -> centred.
    //  2. A building plus exactly one army -> the fixed building/army corner offsets.
    //  3. No building, exactly two armies of DIFFERENT owners -> side by side, mirrored around
    //     the centre (twoArmySideOffset).
    //  4. Anything past that (a building sharing a hex with 2+ armies, 3+ armies of mixed
    //     owners, several armies of the SAME owner) isn't designed yet — same-owner stacking is
    //     a known follow-up — so everyone just stacks at centre (building keeps its own corner
    //     if present) rather than guessing a layout.
    public static class HexObjectLayout
    {
        public readonly struct Result
        {
            public readonly Vector2 BuildingOffset;
            public readonly Vector2[] ArmyOffsets; // same order as the armyOwners list passed in

            public Result(Vector2 buildingOffset, Vector2[] armyOffsets)
            {
                BuildingOffset = buildingOffset;
                ArmyOffsets = armyOffsets;
            }
        }

        public static Result Resolve(GameConfig config, bool hasBuilding, IReadOnlyList<PlayerSetupData> armyOwners)
        {
            int armyCount = armyOwners?.Count ?? 0;
            var armyOffsets = new Vector2[armyCount];

            int totalObjects = (hasBuilding ? 1 : 0) + armyCount;
            if (totalObjects <= 1)
                return new Result(Vector2.zero, armyOffsets); // the lone occupant (if any) sits centred

            if (hasBuilding && armyCount == 1)
            {
                armyOffsets[0] = config.armyIconOffset;
                return new Result(config.buildingIconOffset, armyOffsets);
            }

            if (!hasBuilding && armyCount == 2 && armyOwners[0] != armyOwners[1])
            {
                armyOffsets[0] = new Vector2(-config.twoArmySideOffset.x, config.twoArmySideOffset.y);
                armyOffsets[1] = new Vector2(config.twoArmySideOffset.x, config.twoArmySideOffset.y);
                return new Result(Vector2.zero, armyOffsets);
            }

            // Fallback for not-yet-designed combinations — stack everyone at centre.
            for (int i = 0; i < armyCount; i++)
                armyOffsets[i] = Vector2.zero;
            return new Result(hasBuilding ? config.buildingIconOffset : Vector2.zero, armyOffsets);
        }
    }
}
