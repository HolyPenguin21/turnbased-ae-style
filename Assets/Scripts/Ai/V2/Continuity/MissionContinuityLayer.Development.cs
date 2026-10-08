using Game.Cards;
using Game.Map;
using System.Linq;

namespace Game.Ai.V2
{
    // Existing domain result semantics, owned by the same continuity policy.
    internal static partial class MissionContinuityLayer
    {
        internal static bool IsDevelopmentStepObjectiveSatisfiedLive(Game.Players.PlayerSetupData player,
            ProvisionedMission pm) => DevelopmentOpportunityEvaluator.OperatorPreparedAt(player,
                pm.DevelopmentTarget.Hero, pm.DevelopmentTarget.FacilityHex, pm.DevelopmentTarget.Mode)
                && Game.Combat.BattleInitiator.FindEnemyAt(pm.DevelopmentTarget.FacilityHex, player) == null;

        internal static void ClassifyDevelopmentStep(ExecutionResult e, MissionStepResult o)
        {
            switch (e.StopReason)
            {
                case ExecutionStopReason.StepCompleted:
                case ExecutionStopReason.OutOfMovement:
                    o.Disposition = MissionStepDisposition.Progress;
                    break;
                case ExecutionStopReason.NoSafeStep:
                case ExecutionStopReason.MoveRejected:
                case ExecutionStopReason.BattleStarted:
                case ExecutionStopReason.HexEventStarted:
                    o.Disposition = MissionStepDisposition.Waiting;
                    break;
                default:
                    o.Fail();
                    break;
            }
            return;
        }
        private static bool TryCreateDevelopmentStep(MissionIntentState state, AiAllocatorState allocState,
            MissionIntent intent, MissionStepResult o, int turn)
        {
            if (!(o.DevelopmentFacts().HasDevelopmentPayload && o.MadeProgress)) return false;
            CreateDevelopmentIntent(state, o, turn);
            return true;
        }


        private static void CreateDevelopmentIntent(MissionIntentState state,
            MissionStepResult o, int turn)
        {
            DevelopmentMissionTarget target = o.DevelopmentFacts().DevelopmentTarget;
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
        private static void ResolveDevelopmentOperation(Game.Players.PlayerSetupData player, WorldSnapshot snap, MissionIntent intent, ActiveResolution pass)
        {
            var state = pass.State;
            var active = pass.Active;
            var dead = pass.Dead;
            var rekeys = pass.Rekeys;

            DevelopmentIntent d = intent.Development;
            ArmyData actual = d?.Hero == null ? null : ArmyRegistry.AllForOwner(player)
                .FirstOrDefault(a => a != null && !a.IsPrison
                    && a.Members.Contains(d.Hero));
            bool valid = d != null && actual != null && d.Hero.Owner == player
                && !d.Hero.IsPrisoner && d.Hero.IsHero
                && d.Hero.HasAbility(ResearchProductionSystem.RoleAbility(d.Mode))
                && string.Equals(d.HeroKey, GenerationSource.StableHeroKey(d.Hero),
                    System.StringComparison.Ordinal)
                && DevelopmentOpportunityEvaluator.IsPreparationSite(player, d.FacilityHex)
                && (intent.PreferredMoverArmyId == actual.Id
                    || !intent.PreferredMoverArmyId.HasValue);
            bool arrived = valid && DevelopmentOpportunityEvaluator.OperatorPreparedAt(player, d.Hero, d.FacilityHex, d.Mode)
                && Game.Combat.BattleInitiator.FindEnemyAt(d.FacilityHex, player) == null;
            if (!valid || arrived || ShouldReap(intent, snap?.TurnNumber ?? 0))
            {
                dead.Add(intent.IntentKey);
                AiDebugLog.Write($"[AI][V2][Development] retire {intent.IntentKey} "
                    + $"valid={valid} arrived={arrived} stall={intent.StallTurns}");
                return;
            }
            ResumeTransientSuspension(intent);
            active.Add(intent);
            return;
        }

        private static void CaptureDevelopmentProvisionFacts(ProvisionedMission pm, MissionStepResult o)
        {
            o.DevelopmentFactsForWrite().HasDevelopmentPayload = true;
            o.DevelopmentFactsForWrite().DevelopmentTarget = pm.DevelopmentTarget;
        }

        private static void CaptureDevelopmentExecutionFacts(ExecutionResult e, MissionStepResult o)
        {
            o.PayloadForWrite<DevelopmentStepPayload>().DeliveryReady = e.DevelopmentDeliveryReady;
        }

    }
}

