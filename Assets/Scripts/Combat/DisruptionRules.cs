using System.Collections.Generic;
using System.Linq;
using Game.Cards;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Units;

namespace Game.Combat
{
    // Presence is authoritative gameplay, independent of targeting/visibility. Reading this
    // rule never reveals a carrier. Knowledge callers must provide only their own known army.
    public static class DisruptionRules
    {
        public static bool IsCarrier(UnitData unit) => unit != null && unit.HitPointsCurrent > 0
            && !unit.IsPrisoner && !unit.IsAviation && unit.HasAbility(UnitAbilities.Disruption);

        public static bool HasCarrier(ArmyData army) => army != null && !army.IsPrison
            && !army.IsAirArmy && !army.IsAirfield && army.Members.Any(IsCarrier);

        public static bool HasCarrier(IEnumerable<WorthIt.DefenderProfile> units) => units != null
            && units.Any(u => u.HitPoints > 0 && !u.IsSummoned && u.CanDisruptBuilding
                && u.Abilities != null && u.Abilities.Contains(UnitAbilities.Disruption));

        public static bool IsDisrupted(HexCoord hex, PlayerSetupData defenderOwner,
            ArmyData incomingAttacker = null)
        {
            if (defenderOwner == null) return false;
            if (incomingAttacker != null && incomingAttacker.Owner != defenderOwner
                && HasCarrier(incomingAttacker)) return true;
            return ArmyRegistry.AllAt(hex).Any(a => a.Owner != defenderOwner && HasCarrier(a));
        }

        public static int BuildingDefenseAtEncounterStart(HexCoord hex, ArmyData defender,
            ArmyData incomingAttacker)
        {
            BuildingData building = BuildingRegistry.FindAt(hex);
            if (defender?.Owner == null || building == null || !building.IsBase
                || building.Owner != defender.Owner) return 0;
            return building.Defense > 0 && IsDisrupted(hex, defender.Owner, incomingAttacker)
                ? 0 : building.Defense;
        }
    }
}
