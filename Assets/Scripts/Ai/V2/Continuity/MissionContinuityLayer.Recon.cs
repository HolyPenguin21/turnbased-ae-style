using System.Linq;

namespace Game.Ai.V2
{
    // Existing domain result semantics, owned by the same continuity policy.
    internal static partial class MissionContinuityLayer
    {
        internal static bool IsScoutStepObjectiveSatisfiedLive(Game.Players.PlayerSetupData player,
            ProvisionedMission pm) => ScoutObjectiveEvaluator.IsSatisfiedLive(player, pm.ScoutKind, pm.FocusHex);

        internal static void ClassifyScoutStep(ExecutionResult e, MissionTurnOutcome o)
        {
            if (e.StopReason == ExecutionStopReason.TargetInvalidated)
            {
                o.Outcome = ExecutionOutcome.Blocked;
                return;
            }
            MissionStepResultPolicy.ClassifyDefaultExecution(e, o);
        }
        private static bool TryContinueScoutWaypoint(MissionIntentState state, AiAllocatorState allocState,
            MissionIntent intent, MissionTurnOutcome o, int turn)
        {
            string aid = AiV2Trace.FormatCorrelation(o.Proposal);
            // Review P1 #1/#2 (+ follow-up) — an Explore/Refresh focus hex met by something
            // OTHER than this actor's own execution reaching goal (another scout opened it
            // mid-turn, or provisioning found it already live-satisfied) is a satisfied
            // WAYPOINT, not a finished role. KEEP — or, for a fresh mission that really
            // began executing this turn, CREATE — the durable ground-scout intent so
            // ActorCommitments retains the scout and ResolveActive re-focuses it next turn
            // (its hex now fails IsIntentStillValid). Own-execution completion uses
            // ExecutionResult.DurableRoleContinues.
            if (o.ObjectiveSatisfiedExternally)
            {
                bool existingScoutRole = intent != null
                    && intent.Scout != null;
                // Fresh role: the mission was provisioned AND executed at least one step
                // this turn (so ReconPatrolState already exists). A provisioning-only
                // TargetSatisfied for a never-executed fresh mission has HasScoutPayload ==
                // false / MadeProgress == false and is correctly NOT made durable.
                bool freshScoutRole = intent == null && o.HasScoutPayload && o.MadeProgress;

                if (existingScoutRole)
                {
                    // Count the AP / steps the scout actually spent before the waypoint
                    // was taken (accumulated-state preservation), same as any other
                    // productive turn — AdvanceIntent owns that accounting.
                    o.MadeProgress = true;
                    AdvanceIntent(intent, o, turn, state, allocState);
                    AiDebugLog.Write($"[AI][V2] continuity — [{aid}] {o.IntentKey} waypoint satisfied "
                        + "externally; durable scout role kept for next-turn re-focus");
                    return true;
                }
                if (freshScoutRole)
                {
                    if (TryAbsorbIntoExistingActorRole(state, o, turn, allocState))
                        return true;
                    CreateIntent(state, o, turn);
                    AiDebugLog.Write($"[AI][V2] continuity — [{aid}] {o.IntentKey} fresh scout began "
                        + "this turn; waypoint satisfied externally, durable intent created for re-focus");
                    return true;
                }
            }
            return false;
        }

        private static bool TryCreateScoutStep(MissionIntentState state, AiAllocatorState allocState,
            MissionIntent intent, MissionTurnOutcome o, int turn)
        {
            if (!(o.MadeProgress && o.HasScoutPayload)) return false;
            if (!TryAbsorbIntoExistingActorRole(state, o, turn, allocState))
                CreateIntent(state, o, turn);
            return true;
        }


        // Spec §1/§10 — the physical scout that produced this fresh scout outcome already owns a
        // durable Recon role (Explore / Refresh) under a different key: a new
        // opportunistic mission ran on a mover continuity already tracks. Re-point that existing
        // role at the new objective and re-key its registry slot, preserving CreatedTurn /
        // TurnsActive / CumulativeApSpent / StepsMovedTotal / PreferredMoverArmyId, instead of
        // creating a second durable intent for the same physical actor. Ownership is actor-
        // exclusive across all three Recon sub-kinds. Returns true when it absorbed the outcome.
        private static bool TryAbsorbIntoExistingActorRole(MissionIntentState state,
            MissionTurnOutcome o, int turn, AiAllocatorState allocState)
        {
            if (!o.HasScoutPayload || o.MoverArmyId == null)
                return false;

            MissionIntent owner = null;
            foreach (MissionIntent it in state.All)
            {
                if (it.Kind != MissionKind.Scout || it.Scout == null)
                    continue;
                if (it.PreferredMoverArmyId == o.MoverArmyId && !it.IntentKey.Equals(o.IntentKey))
                {
                    owner = it;
                    break;
                }
            }
            if (owner == null)
                return false;

            MissionIntentKey oldKey = owner.IntentKey;
            ApplyScoutPayload(owner.Scout, o);
            owner.Funding = owner.Funding == CommitmentTier.Hard ? CommitmentTier.Hard : CommitmentTier.None;
            owner.IntentKey = MissionIntentKey.For(owner);
            state.Remove(oldKey);
            state.Put(owner);

            o.MadeProgress = true;
            AdvanceIntent(owner, o, turn, state, allocState);
            AiDebugLog.Write($"[AI][V2] continuity — [{AiV2Trace.FormatCorrelation(o.Proposal)}] actor #{o.MoverArmyId} already "
                + $"owns {oldKey}; absorbed fresh {o.IntentKey} into that durable role (no duplicate intent)");
            return true;
        }

        // THE one writer of a Scout outcome's provisioned payload into a durable ScoutIntent — used
        // when a role is created, advanced and when an actor's existing role absorbs a fresh
        // mission, so the three can never drift apart again.
        private static void ApplyScoutPayload(ScoutIntent s, MissionTurnOutcome o)
        {
            s.FocusHex = o.FocusHex;
            s.Kind = o.ScoutKind;
            s.RequiresStealth = o.ScoutRequiresStealth;
        }

        private static void CreateIntent(MissionIntentState state, MissionTurnOutcome o, int turn)
        {
            var si = new ScoutIntent();
            ApplyScoutPayload(si, o);
            CommitmentTier funding = CommitmentTier.None;
            MissionIntent intent = NewIntent(o, turn, MissionKind.Scout, funding, si);
            state.Put(intent);
            AiDebugLog.Write($"[AI][V2] continuity — [{AiV2Trace.FormatCorrelation(o.Proposal)}] {intent.IntentKey} created ({intent.Funding}, "
                + $"mover #{o.MoverArmyId}, {o.StepsMoved} step(s))");
        }

        private static void ReleaseOtherReconActorClaims(MissionIntentState state,
            MissionIntent owner, int moverArmyId)
        {
            if (state == null || owner == null || owner.Kind != MissionKind.Scout)
                return;

            foreach (MissionIntent other in state.All)
            {
                if (other == null || object.ReferenceEquals(other, owner)
                    || other.Kind != MissionKind.Scout || other.Scout == null
                    || other.PreferredMoverArmyId != moverArmyId)
                    continue;
                other.PreferredMoverArmyId = null;
                AiDebugLog.Write($"[AI][V2] continuity — actor #{moverArmyId} moved to "
                    + $"{owner.IntentKey}; unbound prior role {other.IntentKey}");
            }
        }
        private static void ObserveReconMover(MissionIntentState state, AiAllocatorState allocator,
            MissionIntent intent, MissionTurnOutcome o, int turn) =>
            ReleaseOtherReconActorClaims(state, intent, o.MoverArmyId.Value);

        private static void ApplyScoutStepFacts(MissionIntentState state, AiAllocatorState allocator,
            MissionIntent intent, MissionTurnOutcome o, int turn)
        {
            if (o.HasScoutPayload && intent.Scout != null)
                ApplyScoutPayload(intent.Scout, o);

        }

    }
}
