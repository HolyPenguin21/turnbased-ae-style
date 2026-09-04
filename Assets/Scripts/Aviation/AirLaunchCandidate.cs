using System.Collections.Generic;
using Game.HexGrid;
using Game.Map;
using Game.Units;

namespace Game.Aviation
{
    // One launchable air group — either aircraft still sitting in an airfield's own stored
    // container (ExistingArmy null), or an existing, currently-untasked air army already parked
    // over an owned airfield (ExistingArmy set — no launch step needed, only a fresh sortie
    // assignment). Never a mobile air army mid-flight — that one is already task-owned.
    //
    // Extracted verbatim (ARCH-01, 2026-09-04) from the former Game.Ai.AirStrikeTask.LaunchCandidate
    // so the V2 recon-air executors have a launch-group descriptor that does not live inside a
    // deleted V1 task class.
    public readonly struct AirLaunchCandidate
    {
        public readonly HexCoord AirfieldHex;
        public readonly ArmyData ExistingArmy;
        public readonly IReadOnlyList<UnitData> Aircraft;

        public AirLaunchCandidate(HexCoord airfieldHex, ArmyData existingArmy, IReadOnlyList<UnitData> aircraft)
        {
            AirfieldHex = airfieldHex;
            ExistingArmy = existingArmy;
            Aircraft = aircraft;
        }
    }
}
