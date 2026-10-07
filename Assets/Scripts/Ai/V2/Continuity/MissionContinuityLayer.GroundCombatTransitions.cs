using System.Linq;

namespace Game.Ai.V2
{
    internal static partial class MissionContinuityLayer
    {
        private static bool TryHandleInvalidGroundSupport(MissionIntentState state, AiAllocatorState allocState,
            MissionIntent intent, MissionStepResult o, int turn)
        {
            string aid = AiV2Trace.FormatCorrelation(o.Proposal);
            // A support whose roster no longer improves the primary (Provisioning AssemblyInfeasible
            // on a convoy / gather leg) invalidates only that assignment, never the durable
            // operation or its target: the support is released and the operation stays in its
            // reinforcement / gather phase. One edge for Raid and Attack (GroundCombatLegs.IsSupportLeg).
            if (o.Disposition == MissionStepDisposition.PermanentFailure && intent != null
                && o.ProvisionFailureKindValue == ProvisionFailureKind.AssemblyInfeasible
                && GroundCombatLegs.IsSupportLeg(o) && ReleaseInvalidSupport(intent, o))
            {
                intent.Status = IntentStatus.Active;
                intent.Suspended = SuspendReason.None;
                intent.LastReconciledTurn = turn;
                AiDebugLog.Write($"[AI][V2] continuity — [{aid}] {o.IntentKey} support assembly "
                    + "invalid; support released, operation kept");
                return true;
            }

            return false;
        }

        // Releases the support an AssemblyInfeasible convoy / gather leg named. False when the leg
        // is not one whose support can be released this way (the generic failure path applies).
        private static bool ReleaseInvalidSupport(MissionIntent intent, MissionStepResult o)
        {
            if (intent.Raid != null
                && GroundCombatLegs.RaidLegOf(o) == RaidMissionPhase.Reinforcement)
            {
                intent.Raid.SupportArmyId = null;
                intent.Raid.ReinforcementRequestedTurn = -1;
                intent.Raid.Phase = RaidMissionPhase.Reinforcement;
                return true;
            }
            AttackMissionTarget? leg = GroundCombatLegs.AttackLegOf(o);
            if (intent.Attack == null || !leg.HasValue)
                return false;
            if (leg.Value.Phase == AttackMissionPhase.Gather && leg.Value.SupportArmyId.HasValue)
            {
                intent.Attack.GatherSupportArmyIds.Remove(leg.Value.SupportArmyId.Value);
                return true;
            }
            if (leg.Value.Phase == AttackMissionPhase.Reinforcement)
            {
                intent.Attack.SupportArmyId = null;
                intent.Attack.RendezvousHex = null;
                intent.Attack.ReinforcementRequestedTurn = -1;
                return true;
            }
            return false;
        }

        // The zero-value walk-home leg that leaves its actor to fresh global allocation: a
        // completed Raid target's Return. When that actor is bound to a new ground-combat
        // operation the fallback leg is retired, never kept as a second owner of the same army.
        // (An ActiveDefence Return is a real, claimed withdrawal — not a fallback.)
        private static void RetireReturnFallbacksForActor(MissionIntentState state,
            int? actorId, string reason)
        {
            if (state == null || !actorId.HasValue)
                return;
            foreach (MissionIntent fallback in state.All.Where(i =>
                i?.Raid != null && i.Raid.CompletedTargetAwaitingFreshDecision
                    && i.Raid.PrimaryArmyId == actorId).ToList())
            {
                state.Remove(fallback.IntentKey);
                AiDebugLog.Write($"[AI][V2][{fallback.Kind}] {fallback.IntentKey} return fallback retired — "
                    + $"actor #{actorId.Value} reassigned by global allocation ({reason})");
            }
        }
        private static void FinalizeAirSupportResolution(Game.Players.PlayerSetupData player, WorldSnapshot snap, ActiveResolution pass)
        {
            GroundCombatAirSupport.ReleaseOrphanStrikes(player, pass.State.All);
        }

        private static void CaptureGroundHandoffFacts(ExecutionResult e, MissionStepResult o)
        {
            if (o.MissionKind == MissionKind.Raid || o.MissionKind == MissionKind.Attack)
                o.GroundFactsForWrite().ReinforcementHandoffAttempted = e.ReinforcementHandoffAttempted;
        }

    }
}
