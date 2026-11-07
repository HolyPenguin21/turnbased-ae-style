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
            ArmyData incomingAttacker = null) =>
            DisruptingArmy(hex, defenderOwner, incomingAttacker) != null;

        // The army whose carrier cancels the defending owner's building Defense on `hex` (the
        // incoming attacker first), or null. Authoritative read: battle rules and diagnostics only.
        public static ArmyData DisruptingArmy(HexCoord hex, PlayerSetupData defenderOwner,
            ArmyData incomingAttacker = null)
        {
            if (defenderOwner == null) return null;
            if (incomingAttacker != null && incomingAttacker.Owner != defenderOwner
                && HasCarrier(incomingAttacker)) return incomingAttacker;
            return ArmyRegistry.AllAt(hex).FirstOrDefault(a => a.Owner != defenderOwner && HasCarrier(a));
        }

        // An air army defends without the hex's building bonus (and without terrain, see
        // BattleRulesSnapshot).
        public static int BuildingDefenseAtEncounterStart(HexCoord hex, ArmyData defender,
            ArmyData incomingAttacker)
            => defender == null || Game.Aviation.AviationRules.IsAirArmy(defender) ? 0
                : BuildingDefenseFor(hex, defender.Owner, incomingAttacker);

        // The defending owner's own Base building Defense on `hex`, 0 when disrupted.
        public static int BuildingDefenseFor(HexCoord hex, PlayerSetupData defenderOwner,
            ArmyData incomingAttacker = null)
        {
            int defense = OwnBaseDefense(hex, defenderOwner);
            return defense > 0 && IsDisrupted(hex, defenderOwner, incomingAttacker) ? 0 : defense;
        }

        // Knowledge-safe variant: only the caller's own incoming attacker can disrupt; other
        // (possibly hidden) armies on the hex are never inspected.
        public static int KnownBuildingDefenseFor(HexCoord hex, PlayerSetupData defenderOwner,
            ArmyData incomingAttacker = null)
        {
            int defense = OwnBaseDefense(hex, defenderOwner);
            return defense > 0 && incomingAttacker != null && defenderOwner != null
                && incomingAttacker.Owner != defenderOwner && HasCarrier(incomingAttacker) ? 0 : defense;
        }

        // A Base's Defense counts only for an army of the building's own owner.
        public static int OwnBaseDefense(HexCoord hex, PlayerSetupData defenderOwner)
        {
            BuildingData building = BuildingRegistry.FindAt(hex);
            return defenderOwner == null || building == null || !building.IsBase
                || building.Owner != defenderOwner ? 0 : building.Defense;
        }
    }
}
