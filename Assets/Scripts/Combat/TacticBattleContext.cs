using System.Linq;
using Game.Ai;
using Game.Ai.V2;
using Game.Cards;
using Game.HexGrid;
using Game.Map;

namespace Game.Combat
{
    public static class TacticBattleContext
    {
        // Called once for a committed encounter. No enemy mission/hand lookup: the opposing
        // combat roster is revealed to the participants by encounter preparation already.
        public static bool IsCritical(HexCoord hex, ArmyData first, ArmyData second)
        {
            if (first?.Owner == null || second?.Owner == null
                || first.Owner.IsNeutral || second.Owner.IsNeutral) return false;
            var building = BuildingRegistry.FindAt(hex);
            if (building != null && building.IsStartingCitadel
                && (building.Owner == first.Owner || building.Owner == second.Owner)) return true;
            return IsStrikeForce(first) && IsStrikeForce(second);
        }

        // An AA Facility shooting at `second`: the Facility side has no army, so only the
        // starting-citadel rule can make the challenge critical.
        public static bool IsCritical(HexCoord hex, Game.Players.PlayerSetupData facilityOwner, ArmyData second)
        {
            if (facilityOwner == null || second?.Owner == null
                || facilityOwner.IsNeutral || second.Owner.IsNeutral) return false;
            var building = BuildingRegistry.FindAt(hex);
            return building != null && building.IsStartingCitadel
                && (building.Owner == facilityOwner || building.Owner == second.Owner);
        }

        private static bool IsStrikeForce(ArmyData army) =>
            army != null && !army.IsGarrison && !army.IsPrison
            && army.Members.Any(AiArmyRoles.IsGroundCombatBody)
            && army.Members.Any(u => u != null && u.IsHero)
            && ActiveDefenceObjectiveEvaluator.IsSignificantHostilePower(AiPower.EffectiveArmyPower(army.Members));
    }
}
