using System.Linq;
using Game.Core;
using Game.Map;
using Game.Players;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  SANCTIONED LIVE CHEATS - the true-world facts a live tier is allowed to read at the moment
    //  it acts, outside the observer's knowledge. The snapshot counterpart is
    //  WorldSnapshot.TrueWorld; this is its live door for the checks that run between snapshots.
    //
    //  Each member is a deliberate, owner-approved exception to the fog of war. "Does this
    //  decision cheat?" is answered by whether it calls into this class (or reads
    //  snap.TrueWorld). Add a member only by an explicit decision, never as a convenience.
    // ===========================================================================================
    internal static class TrueWorldReads
    {
        // 2026-10-15 (owner decision: kept as is, named explicitly) - does any other ordinary
        // (non-neutral) player field this army id now? Read from the other players' live rosters,
        // whether the observer could see it or not: a neutral captured in the fog stops being a
        // Raid target at once (ObjectiveLiveStatus.RaidSatisfied, step 2).
        internal static bool ArmyFieldedByAnotherPlayer(PlayerSetupData observer, int armyId)
        {
            foreach (PlayerSetupData other in GameSession.Players ?? Enumerable.Empty<PlayerSetupData>())
            {
                if (other == null || other.Equals(observer))
                    continue;
                if (ArmyRegistry.AllForOwner(other)
                    .Any(a => a != null && a.Id == armyId && a.Members.Count > 0))
                    return true;
            }
            return false;
        }
    }
}
