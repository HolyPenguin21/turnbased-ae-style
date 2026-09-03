using System.Collections.Generic;
using Game.Core;
using Game.Players;
using UnityEngine;

namespace Game.Map
{
    // Where a hex's occupants sit relative to its centre, resolved fresh from what's actually
    // on the hex right now rather than baked into one fixed per-slot layout. Every offset is in
    // hex-radius units (x = left/right, y = world Z), same convention GameConfig's
    // buildingIconOffset/armyIconOffset/armySlotRight/Left/Top already use — multiply by
    // HexMap.OuterRadius and add to HexToWorld(hex) to get a world position.
    //
    // IMPORTANT: `armyOwners` must already be filtered to what the CURRENT MAP VIEWER can see
    // (see HexSelectionController.VisibleForLayout) — a fully-hidden enemy army (stealth) or one
    // on a hex the viewer has no vision of must NOT reach here, or the viewer's own army gets
    // pushed off-centre into a two-owners slot and silently discloses "someone is here" (project
    // owner's own report, кейс 4). Same for `hasBuilding`: pass false when the viewer hasn't
    // personally confirmed the building, so a lone army on that hex still sits centred rather
    // than announcing the building via its corner offset.
    //
    // Rules, in priority order (armyOwners carries ONE entry per DISTINCT owner — several armies
    // of the same owner already collapsed to a single marker upstream, see
    // HexSelectionController.DistinctOwners):
    //  1. Exactly one occupant total (a lone building, or a lone army) -> centred.
    //  2. A building plus exactly one army -> building bottom-left (buildingIconOffset), army
    //     bottom-right (armyIconOffset). This is the ONLY case the building keeps an off-centre
    //     corner — with 2+ armies it moves to centre instead (project owner's spec, кейс 4.1).
    //  3. Two or three armies of DIFFERENT owners (with or without a building): fixed slots —
    //     1st owner -> armySlotRight, 2nd -> armySlotLeft, 3rd -> armySlotTop. A building present
    //     sits at hex centre.
    //  4. Anything past that (4+ distinct owners on one hex) isn't designed yet — project owner:
    //     "4е разных игрока на хексе пока не рассматриваем" — so everyone stacks at centre
    //     (building keeps its own corner if present) rather than guessing a layout.
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

            // 2 or 3 armies of different owners — fixed right/left/top slots, building (if any)
            // re-centres rather than keeping a corner.
            if (armyCount >= 2 && armyCount <= 3)
            {
                armyOffsets[0] = config.armySlotRight;
                armyOffsets[1] = config.armySlotLeft;
                if (armyCount == 3)
                    armyOffsets[2] = config.armySlotTop;
                return new Result(Vector2.zero, armyOffsets);
            }

            // Fallback for not-yet-designed combinations (4+ distinct owners) — stack at centre.
            for (int i = 0; i < armyCount; i++)
                armyOffsets[i] = Vector2.zero;
            return new Result(hasBuilding ? config.buildingIconOffset : Vector2.zero, armyOffsets);
        }
    }
}
