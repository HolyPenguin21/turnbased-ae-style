using System.Linq;

namespace Game.Ai.V2
{
    // Existing domain result semantics, owned by the same continuity policy.
    internal static partial class MissionContinuityLayer
    {
        internal static bool IsDevelopmentStepObjectiveSatisfiedLive(Game.Players.PlayerSetupData player,
            ProvisionedMission pm) => Game.Cards.ResearchProductionSystem.ActorStillQualifies(player,
                pm.DevelopmentTarget.Hero, pm.DevelopmentTarget.FacilityHex, pm.DevelopmentTarget.Mode)
                && Game.Cards.ResearchProductionSystem.IsEligible(player,
                    pm.DevelopmentTarget.FacilityHex, pm.DevelopmentTarget.Mode, out _);

        internal static void ClassifyDevelopmentStep(ExecutionResult e, MissionTurnOutcome o)
        {
            switch (e.StopReason)
            {
                case ExecutionStopReason.StepCompleted:
                case ExecutionStopReason.OutOfMovement:
                    o.Outcome = ExecutionOutcome.ProductiveStop;
                    break;
                case ExecutionStopReason.NoSafeStep:
                case ExecutionStopReason.MoveRejected:
                case ExecutionStopReason.BattleStarted:
                case ExecutionStopReason.HexEventStarted:
                    o.Outcome = ExecutionOutcome.Blocked;
                    break;
                default:
                    o.Outcome = ExecutionOutcome.Failed;
                    break;
            }
            return;
        }
        private static bool TryCreateDevelopmentStep(MissionIntentState state, AiAllocatorState allocState,
            MissionIntent intent, MissionTurnOutcome o, int turn)
        {
            if (!(o.HasDevelopmentPayload && o.MadeProgress)) return false;
            CreateDevelopmentIntent(state, o, turn);
            return true;
        }


        private static void CreateDevelopmentIntent(MissionIntentState state,
            MissionTurnOutcome o, int turn)
        {
            DevelopmentMissionTarget target = o.DevelopmentTarget;
            if (target.Hero == null || !o.MoverArmyId.HasValue
                || state.All.Any(i => i?.Development?.Hero == target.Hero
                    || i?.Development != null && i.Development.Mode == target.Mode
                        && i.Development.FacilityHex.Equals(target.FacilityHex)))
                return;
            var objective = new DevelopmentIntent
            {
                Hero = target.Hero, HeroKey = target.HeroKey,
                FacilityHex = target.FacilityHex, Mode = target.Mode,
                IntrinsicValue = o.Proposal?.BaseValue ?? target.IntrinsicValue,
            };
            MissionIntent intent = NewIntent(o, turn, MissionKind.Development,
                CommitmentTier.Soft, objective);
            state.Put(intent);
            AiDebugLog.Write($"[AI][V2][Development] continuity create {intent.IntentKey} "
                + $"hero={target.HeroKey} actor=#{o.MoverArmyId}");
        }
    }
}
