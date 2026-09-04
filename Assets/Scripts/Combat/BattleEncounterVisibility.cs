using System.Collections.Generic;
using Game.HexGrid;
using Game.Map;
using Game.Players;

namespace Game.Combat
{
    // STEALTH-COMBAT-01 §7: the stealth-aware roster BattleContactPopupUI's own side lists must
    // use instead of a raw ArmyRegistry.AllAt(hex) filtered only by owner/membership. Mirrors the
    // same "who does this observer actually see on this hex" rule HexSelectionController's own
    // map-marker layout already applies (see its VisibleForLayout) — just scoped to "one owner's
    // armies on one hex" instead of "every owner's representative marker".
    public static class BattleEncounterVisibility
    {
        // `participants` are armies already revealed by this encounter's own
        // BattleEncounterCoordinator.PrepareCommittedEncounter — always shown regardless of
        // owner, since they're the whole reason this popup is open. Every other army of `owner`
        // on `hex` (a second, uninvolved army sharing the hex) still goes through the ordinary
        // stealth-aware filter: the observer's own armies always show, a fully-hidden enemy army
        // never does, and a mixed enemy army shows only as much as StealthSystem.IsHiddenFrom
        // already permits elsewhere.
        public static List<ArmyData> VisibleEncounterArmiesAt(HexCoord hex, PlayerSetupData owner,
            PlayerSetupData observer, IReadOnlyCollection<ArmyData> participants = null)
        {
            var result = new List<ArmyData>();
            if (owner == null)
                return result;
            foreach (ArmyData army in ArmyRegistry.AllAt(hex))
            {
                if (army.Owner != owner || army.Members.Count == 0)
                    continue;
                bool isParticipant = participants != null && ContainsReference(participants, army);
                if (isParticipant || owner == observer || !StealthSystem.ArmyFullyHiddenFrom(army, observer))
                    result.Add(army);
            }
            return result;
        }

        private static bool ContainsReference(IReadOnlyCollection<ArmyData> armies, ArmyData army)
        {
            foreach (ArmyData candidate in armies)
                if (candidate == army)
                    return true;
            return false;
        }
    }
}
