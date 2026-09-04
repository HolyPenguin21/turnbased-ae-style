using System;
using System.Collections.Generic;
using System.Linq;
using Game.Aviation;
using Game.HexGrid;
using Game.Map;
using Game.Players;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  REUSABLE ARMY SELECTOR  (Strategy V2 — Strategic Manager)
    // ===========================================================================================
    //  V2-native reusable empty-army model. An empty ArmyData is a PAID, REUSABLE asset
    //  (ArmyActions.CreateArmy costs AP) — never cleanup garbage, never deleted just for being
    //  empty. This does NOT use V1 GarrisonReorgTask.DisposableEmptyArmies and does not carry the
    //  V1 "disposable army" concept: an army's role is read from its CURRENT composition + mission
    //  ownership (ActorCommitments), and a shell is only ever used where game rules already allow
    //  it — at the hex it currently sits on. A shell that previously served another role is fully
    //  reusable; ArmyId does not bind purpose.
    //
    //  PLACEMENT RULE: the shell is part of a COMPLETE placement candidate (card + hex + shell).
    //  The caller never picks a hex and then reuses an unrelated shell elsewhere — a shell at
    //  base A cannot be "used at base B".
    // ===========================================================================================
    public static class ReusableArmySelector
    {
        public static bool IsReusableShell(ArmyData army, PlayerSetupData player, ActorCommitments commitments)
        {
            if (army == null || army.Owner != player || army.Controller == null)
                return false;
            if (army.Members.Count != 0 || army.IsGarrison || army.IsPrison)
                return false;
            // Airfield containers / air armies are semantically distinct from a ground shell — the
            // model exposes these predicates, so this is not a hardcoded flag.
            if (AviationRules.IsAirfield(army) || AviationRules.IsAirArmy(army))
                return false;
            return commitments == null || !commitments.IsArmyClaimed(army.Id);
        }

        public static IReadOnlyList<ArmyData> ReusableShells(PlayerSetupData player, ActorCommitments commitments) =>
            player == null
                ? (IReadOnlyList<ArmyData>)Array.Empty<ArmyData>()
                : ArmyRegistry.AllForOwner(player)
                    .Where(a => IsReusableShell(a, player, commitments))
                    .OrderBy(a => a.Id)
                    .ToList();

        // Priority-1 pick: a reusable shell ALREADY sitting on `hex`. null if none.
        public static ArmyData FindReusableAt(PlayerSetupData player, HexCoord hex, ActorCommitments commitments) =>
            ReusableShells(player, commitments).FirstOrDefault(a => a.Hex.Equals(hex));
    }
}
