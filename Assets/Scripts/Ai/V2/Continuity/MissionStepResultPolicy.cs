using System;
using System.Collections.Generic;
using System.Linq;
using Game.Players;

namespace Game.Ai.V2
{
    // The existing domain interpretation at the result boundary, moved without rule changes.
    // Completed means the reported leg/obligation completed; Continuity owns durable transitions.
    internal static class MissionStepResultPolicy
    {
        internal static void RefreshObjectiveStatesLive(IEnumerable<MissionStepFacts> rows, PlayerSetupData player)
        {
            foreach (MissionStepFacts r in rows)
            {
                if (r.Proposal == null || r.Provisioned == null)
                    continue;
                if (r.Execution != null && r.Execution.ReachedGoal)
                    continue;
                ProvisionedMission pm = r.Provisioned;
                bool satisfied = MissionContinuityLayer.IsStepObjectiveSatisfiedLive(player, pm);

                if (satisfied)
                {
                    r.LiveSatisfiedOverride = true;
                    AiDebugLog.Write($"[AI][V2] ledger — [{r.Proposal.AttemptId}] {MissionIntentKey.For(r.Proposal)} objective met by "
                        + "another action this turn (post-execution live pass)");
                }
            }
        }

        internal static MissionTurnOutcome Normalize(StableMissionKey attemptKey, MissionStepFacts r)
        {
                var o = new MissionTurnOutcome
                {
                    AttemptKey = attemptKey,
                    IntentKey = MissionIntentKey.For(r.Proposal),
                    Proposal = r.Proposal,
                    WasCommitment = r.WasCommitment,
                };
                o.MissionKind = r.Proposal.Kind;

                if (r.Provisioned != null)
                {
                    // Recon binds an existing army; extraction in other lanes may report the
                    // materialized actor through ActualActorArmyId after execution.
                    o.MoverArmyId = r.Provisioned.MoverArmyId;
                    MissionContinuityLayer.CaptureProvisionFacts(r.Provisioned, o);
                }

                if (r.Execution != null)
                {
                    ExecutionResult e = r.Execution;
                    o.WorldDeltas = e.WorldDeltas;
                    o.StateVersionAfter = e.StateVersionAfter;
                    o.StepsMoved = e.StepsMoved;
                    o.ApSpent = e.ApSpent;
                    o.FinalHex = e.FinalHex;
                    // A materialized actor supersedes the provisional mover identity.
                    if (e.ActualActorArmyId.HasValue)
                        o.MoverArmyId = e.ActualActorArmyId;
                    // Extraction and direct-army preparation are distinct productive mutations:
                    // the former creates the actor, the latter changes its roster and/or donor intent.
                    // An Economy actor standing productively on its target (a build ready for
                    // Phase A's follow-up, a collector holding its site for the income tick) is
                    // doing its job without a mutation of its own — progress, not a stall.
                    o.MadeProgress = e.StepsMoved > 0 || e.EnteredStealth
                        || e.InfrastructureChanged || e.CombatChanged
                        || e.OperationStarted
                        || e.ActorMaterialized || e.EconomyPrepared
                        || e.EconomyDeliveryReady || e.EconomyHolding;
                    o.StopReason = e.StopReason;
                    MissionContinuityLayer.CaptureExecutionFacts(e, o);
                    MissionStepResultPolicy.Classify(e, o);
                    if (o.Disposition == MissionStepDisposition.Waiting && e.NeedsReplan)
                        o.Disposition = MissionStepDisposition.Replan;
                }
                else if (r.PendingFailure.HasValue)
                {
                    o.ProvisionFailureKindValue = r.PendingFailure.Value.Kind;
                    MissionStepResultPolicy.ClassifyProvisionFailure(r.PendingFailure.Value, o);
                }
                else
                {
                    o.AllocationDeferReason = r.Deferred;
                    o.Outcome = ExecutionOutcome.Blocked;
                }

                if (r.LiveSatisfiedOverride)
                {
                    o.Outcome = ExecutionOutcome.Completed;
                    o.ObjectiveSatisfied = true;
                    o.ObjectiveSatisfiedExternally = true;
                    o.StructuralFailure = false;
                }

                return o;
        }

        internal static void Classify(ExecutionResult e, MissionTurnOutcome o)
        {
            if (e.ReachedGoal)
            {
                // Spec §1 (review P1 #1) — a satisfied WAYPOINT for an actor whose durable
                // Explore/Refresh role is still runnable is a ProductiveStop, not a Completed
                // objective: the MissionIntent is kept and re-focused next turn rather than retired.
                if (e.DurableRoleContinues)
                {
                    o.Outcome = ExecutionOutcome.ProductiveStop;
                    o.MadeProgress = true;
                    return;
                }
                o.Outcome = ExecutionOutcome.Completed;
                o.ObjectiveSatisfied = true;
                return;

            }
            MissionContinuityLayer.ClassifyDomainExecution(e, o);
        }

        internal static void ClassifyDefaultExecution(ExecutionResult e, MissionTurnOutcome o)
        {
            switch (e.StopReason)
            {
                case ExecutionStopReason.OutOfMovement:
                case ExecutionStopReason.EnemyDiscovered:
                case ExecutionStopReason.NeutralDiscovered:
                case ExecutionStopReason.StepCompleted:
                    o.Outcome = ExecutionOutcome.ProductiveStop;
                    break;
                case ExecutionStopReason.HexEventStarted:
                case ExecutionStopReason.BattleStarted:
                    // Spec §2 — an ordinary hex event or battle interruption is NOT a structural
                    // Recon failure. A scout that moved / entered stealth / made a discovery before
                    // the interruption made productive progress and keeps its durable role. Only a
                    // scout that was ALREADY combat-locked before it could take a single step
                    // (BlockedBeforeMovement, no progress) is a recoverable Blocked.
                    o.Outcome = (e.BlockedBeforeMovement && !o.MadeProgress)
                        ? ExecutionOutcome.Blocked
                        : ExecutionOutcome.ProductiveStop;
                    if (o.Outcome == ExecutionOutcome.ProductiveStop)
                        o.MadeProgress = true;
                    break;
                case ExecutionStopReason.NoSafeStep:
                case ExecutionStopReason.MoveRejected:
                case ExecutionStopReason.RequiredStealthUnavailable:
                    o.Outcome = ExecutionOutcome.Blocked;
                    break;
                default:
                    o.Outcome = ExecutionOutcome.Failed;
                    break;
            }
        }

        internal static void ClassifyProvisionFailure(ProvisionFailure f, MissionTurnOutcome o)
        {
            switch (f.Kind)
            {
                case ProvisionFailureKind.NoMoverExists:
                    o.Outcome = ExecutionOutcome.Blocked;
                    break;
                case ProvisionFailureKind.NoObservationVantage:
                case ProvisionFailureKind.AssemblyInfeasible:
                    o.Outcome = ExecutionOutcome.Failed;
                    o.StructuralFailure = true;
                    break;
                case ProvisionFailureKind.TargetSatisfied:
                    // SupportReturn is a sub-leg of one durable Raid campaign. ProvisionReturn can
                    // discover that the support is already standing at its fixed home before an
                    // executor is ever created. Reporting that as Completed+ObjectiveSatisfied
                    // would make generic continuity retire the WHOLE Raid. Keep the campaign alive;
                    // ResolveActive owns the canonical CompleteRaidSupportReturn transition and will
                    // consume this already-home fact on the next reconciliation/reaction pass.
                    if (MissionContinuityLayer.TryClassifySatisfiedDomainLeg(o))
                        break;
                    // Review P1 #2 — provisioning short-circuited because the focus hex was
                    // already visited/refreshed by an earlier action this turn. No mover was
                    // assigned; the durable actor lives on the existing MissionIntent, so mark
                    // this as an external satisfaction and let ReconcileAfterTurn keep the
                    // Explore/Refresh intent for re-focus instead of retiring it.
                    o.Outcome = ExecutionOutcome.Completed;
                    o.ObjectiveSatisfied = true;
                    o.ObjectiveSatisfiedExternally = true;
                    break;
                case ProvisionFailureKind.TargetInvalidated:
                    // The same support-local rule the execution-side Classify applies
                    // (GroundCombatLegs.IsSupportLeg): a vanished support mover, a stale wing target
                    // or a lost primary behind a support leg never retires the operation from here —
                    // ResolveActive's next pass releases that support, moves the operation to its
                    // next phase, or retires it if the primary itself is gone. Assault / Return
                    // (the primary's own legs) keep their failure semantics.
                    // A Scout target is re-validated by Continuity itself (Recon audit B5, same
                    // rule as the execution-side Classify).
                    MissionContinuityLayer.ClassifyInvalidatedDomainTarget(o);
                    break;
                default:
                    o.Outcome = ExecutionOutcome.Blocked;
                    break;
            }
        }
    }
}
