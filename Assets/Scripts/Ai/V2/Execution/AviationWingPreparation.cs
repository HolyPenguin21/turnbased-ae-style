using System.Collections.Generic;
using System.Linq;
using Game.Aviation;
using Game.Cards;
using Game.Map;
using Game.Players;
using Game.Units;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  THE ONE WAY THE AI FORMS AN AIR WING FROM STORED AIRCRAFT — shared by every aviation
    //  consumer (AirSweep formation, combat support formation for Attack / Raid / ActiveDefence,
    //  rebase launches). Forming is not a take-off and costs no AP/Energy; the sortie launch is
    //  paid later by the wing's first flight action (ArmyActions.TryPayActivation).
    //
    //  Shell priority (all on the airfield's own hex — never a remote army):
    //    1. a free empty army (ReusableArmySelector — unclaimed, not a preparation host);
    //    2. a free ground army that can legally unload its WHOLE roster into the hex garrison
    //       (final garrison roster validated by ArmyActions' own transfer rules); the unload and
    //       aircraft boarding are validated together before either roster changes; a paid
    //       garrison join is declined and formation falls back to a new wing;
    //    3. a new wing (AviationActions.TryLaunch, the existing free formation rule).
    //  Ids, names, units, equipment, HP, remaining movement and spent strikes are never touched:
    //  only container membership changes.
    // ===========================================================================================
    internal static class AviationWingPreparation
    {
        internal static bool TryForm(PlayerSetupData player, AiTurnContext ctx, ArmyData airfield,
            IReadOnlyList<UnitData> aircraft, ActorCommitments commitments, out ArmyData wing,
            out string how)
        {
            wing = null;
            how = null;
            if (player == null || !AviationRules.IsAirfield(airfield) || airfield.Owner != player
                || aircraft == null || aircraft.Count == 0
                || aircraft.Any(u => u == null || !u.IsAviation || !airfield.Members.Contains(u)))
            {
                how = "aircraft are not stored in this airfield";
                return false;
            }
            HexSelectionController hexSelection = ctx?.HexSelection;

            // Without the ownership view no existing army may be repurposed: a shell or a ground
            // army could belong to an operation this caller cannot see.
            if (commitments != null)
            {
                ArmyData shell = ReusableArmySelector.FindReusableAt(player, airfield.Hex, commitments);
                if (shell != null && !commitments.IsPreparationHost(shell.Id)
                    && ArmyActions.TransferMembersAtomic(aircraft, airfield, shell, hexSelection, out _))
                {
                    wing = shell;
                    how = "reused empty shell";
                    return true;
                }

                foreach (ArmyData ground in UnloadableGroundArmiesAt(player, airfield, commitments))
                {
                    if (TryUnloadAndBoard(player, airfield, ground, aircraft, hexSelection))
                    {
                        wing = ground;
                        how = "unloaded ground army into the garrison";
                        return true;
                    }
                }
            }

            FactionCardCatalog catalog = ctx?.StartingDeckCatalog?.GetCatalog(player.Faction);
            if (AviationActions.TryLaunch(airfield, aircraft.ToList(), catalog, hexSelection,
                    out wing, out string why))
            {
                how = "new wing";
                return true;
            }
            how = why;
            return false;
        }

        // Free ground armies on the airfield hex whose whole roster the garrison can take.
        internal static IEnumerable<ArmyData> UnloadableGroundArmiesAt(PlayerSetupData player,
            ArmyData airfield, ActorCommitments commitments)
        {
            ArmyData garrison = GarrisonAt(player, airfield);
            if (garrison == null || commitments == null)
                yield break;
            foreach (ArmyData army in ArmyRegistry.AllAt(airfield.Hex).OrderBy(a => a.Id).ToList())
            {
                if (army == null || army.Owner != player || army.IsGarrison || army.IsPrison
                    || army.IsAirfield || AviationRules.IsAirArmy(army) || army.Members.Count == 0
                    || army.Controller == null
                    || commitments.IsArmyClaimed(army.Id) || commitments.IsPreparationHost(army.Id)
                    || army.Members.Any(u => u == null || u.IsAviation || u.IsPrisoner))
                    continue;
                if (!ArmyActions.CanTransferMembers(army.Members.ToList(), army, garrison, out _))
                    continue;
                yield return army;
            }
        }

        private static ArmyData GarrisonAt(PlayerSetupData player, ArmyData airfield) =>
            ArmyRegistry.AllAt(airfield.Hex).FirstOrDefault(a => a != null && a.Owner == player
                && a.IsGarrison);

        private static bool TryUnloadAndBoard(PlayerSetupData player, ArmyData airfield,
            ArmyData ground, IReadOnlyList<UnitData> aircraft, HexSelectionController hexSelection)
        {
            ArmyData garrison = GarrisonAt(player, airfield);
            if (garrison == null)
                return false;
            return ArmyActions.TryUnloadAndBoardAircraft(ground, garrison, airfield, aircraft,
                hexSelection, out _);
        }
    }
}
