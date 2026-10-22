using System.Collections.Generic;
using Game.Map;
using Game.Players;
using Game.Units;

namespace Game.Ai.V2
{
    // The garrison sortie of ActiveDefence's intercept and Attack's second operation alike: the
    // lane's own live re-proof of the pinned plan, then the one detachment mutation. The lane binds
    // the new actor to its own target and publishes its own flags.
    internal static class GarrisonSortieStep
    {
        internal delegate bool Resolver(out ArmyData donor, out ArmyData shell,
            out List<UnitData> units, out string why);

        // The materialized actor, or null when refused — world unchanged and the result marked for
        // a re-plan. On success the step is completed with the actor recorded as this mission's mover.
        internal static ArmyData Materialize(PlayerSetupData player, AiTurnContext ctx,
            GarrisonSortiePlan plan, ProvisionedMission pm, ExecutionResult result, string lane,
            Resolver resolve, out ArmyData donor, out int bodies)
        {
            bodies = 0;
            result.StartHex = result.FinalHex = plan.StrongholdHex;
            if (!resolve(out donor, out ArmyData shell, out List<UnitData> units, out string why))
            {
                result.StopForReplan(ExecutionStopReason.TargetInvalidated);
                AiDebugLog.Write($"[AI][V2][{lane}][Sortie] rejected: {why}; world unchanged");
                return null;
            }
            ArmyData actor = MaterializeDetachment(player, ctx, donor, shell, units, out why);
            if (actor == null)
            {
                result.StopForReplan(ExecutionStopReason.TargetInvalidated);
                AiDebugLog.Write($"[AI][V2][{lane}][Sortie] materialization refused: {why}");
                return null;
            }
            bodies = units.Count;
            pm.MoverArmyId = actor.Id;
            result.ActualActorArmyId = actor.Id;
            result.ActorMaterialized = true;
            result.StopReason = ExecutionStopReason.StepCompleted;
            return actor;
        }

        // The ONE mutation of a resolved garrison sortie: the pinned bodies leave the garrison into
        // the pinned empty shell, or into one canonical new container on that hex. Null (world
        // unchanged) when refused.
        internal static ArmyData MaterializeDetachment(PlayerSetupData player, AiTurnContext ctx,
            ArmyData donor, ArmyData shell, List<UnitData> units, out string why)
        {
            if (shell == null)
                return ArmyActions.CreateArmyWithMembers(player, donor.Hex,
                    ctx.StartingDeckCatalog.GetCatalog(player.Faction), donor, units, ctx.HexSelection, out why);
            return ArmyActions.TransferMembersAtomic(units, donor, shell, ctx.HexSelection, out why) ? shell : null;
        }
    }
}
