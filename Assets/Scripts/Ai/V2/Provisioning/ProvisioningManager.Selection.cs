using System;
using System.Collections.Generic;
using System.Linq;
using Game.Players;
using Game.Map;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  SELECTION: which funded mission becomes executable this iteration. The protocol was a
    //  `while` inside the orchestrator; Provisioning owns provisioning, so the retry/reprice
    //  protocol lives here and the orchestrator calls ProvisionNext once per admission iteration.
    //
    //  The protocol, unchanged:
    //    · the Scout assignment is solved for the whole funded set; every impossible job is
    //      reported at once, the allocator is told (RegisterProvisionFailure) and re-packs ONCE per
    //      batch, so released AP can admit Economy / Development immediately;
    //    · otherwise the first funded mission that may be attempted is provisioned; a failure is
    //      registered and, unless the allocator converged, the allocator re-packs and the next
    //      mission is tried;
    //    · TWO independent bounded budgets, not one shared counter: a Scout batch that keeps
    //      failing (assignmentReallocPass) must not consume every re-pack this cycle had, starving
    //      the single-mission re-pack (repriceReallocPass) that a mandatory Economy
    //      EnvelopeTooSmall / RepriceThisTurn needs to ever see a corrected envelope this turn.
    //  The boundaries are the baseline's: `< maxReallocIterations` before a batch re-pack and
    //  `++counter >= maxReallocIterations` after a RepriceThisTurn failure.
    //
    //  The initial Pack and the choice of mandatory aviation stay with the caller (they come
    //  before the first call, otherwise the registration order of funded missions would change);
    //  the ProvisioningSession lives for the whole admission iteration and is NOT closed here -
    //  the selected mission executes, is observed and re-admitted while its claims are held.
    // ===========================================================================================
    internal static partial class ProvisioningManager
    {
        // The three world-dependent steps of the protocol. Production binds them to this class;
        // they exist as a seam so the sequences of failures (budgets, convergence, retry) can be
        // driven by tests without a world. No knowledge is added: the call order is fixed below.
        internal readonly struct SelectionSteps
        {
            internal readonly Action<TentativeAllocation> Prepare;
            internal readonly Func<TentativeAllocation, IReadOnlyList<(FundedEntry Funded, ProvisionFailure Failure)>>
                ScoutFailures;
            internal readonly Func<FundedEntry, ProvisioningResult> Provision;

            internal SelectionSteps(Action<TentativeAllocation> prepare,
                Func<TentativeAllocation, IReadOnlyList<(FundedEntry Funded, ProvisionFailure Failure)>> scoutFailures,
                Func<FundedEntry, ProvisioningResult> provision)
            {
                Prepare = prepare;
                ScoutFailures = scoutFailures;
                Provision = provision;
            }
        }

        internal static ProvisioningSelectionOutcome ProvisionNext(PlayerSetupData player,
            PlayerRoot root, AiHandData hand, AiTurnContext ctx, WorldSnapshot snapshot,
            ActorCommitments commitments, AllocationSession allocationSession,
            ProvisioningSession provisioningSession, PassParking parking, TentativeAllocation initial)
        {
            var steps = new SelectionSteps(
                allocation => PreparePass(player, root, ctx, provisioningSession, allocation, commitments),
                allocation => ScoutAssignmentFailures(provisioningSession, allocation),
                funded => Provision(player, root, hand, ctx, provisioningSession, funded));
            return ProvisionNext(player, snapshot, allocationSession, provisioningSession, parking,
                initial, steps);
        }

        internal static ProvisioningSelectionOutcome ProvisionNext(PlayerSetupData player,
            WorldSnapshot snapshot, AllocationSession allocationSession,
            ProvisioningSession provisioningSession, PassParking parking, TentativeAllocation initial,
            SelectionSteps steps)
        {
            var outcome = new ProvisioningSelectionOutcome();
            TentativeAllocation allocation = initial;
            int assignmentReallocPass = 0;
            int repriceReallocPass = 0;

            void Pack()
            {
                allocation = allocationSession.Pack();
                foreach (FundedEntry fe in allocation.Funded)
                    if (fe?.Mission != null)
                        outcome.FundedKeysAcrossPacks.Add(StableMissionKey.For(fe.Mission));
            }

            while (true)
            {
                steps.Prepare(allocation);
                IReadOnlyList<(FundedEntry Funded, ProvisionFailure Failure)> scoutFailures =
                    steps.ScoutFailures(allocation);
                if (scoutFailures.Count > 0)
                {
                    foreach ((FundedEntry failedFunding, ProvisionFailure failure) in scoutFailures)
                    {
                        StableMissionKey failedKey = StableMissionKey.For(failedFunding.Mission);
                        outcome.AttemptedKeys.Add(failedKey);
                        CapabilityPoolExhaustionRegistry.DeferNoExecutableStep(
                            player, failedFunding.Mission, failure);
                        allocationSession.RegisterProvisionFailure(failedFunding, failure);
                        outcome.Events.Add(new ProvisionEvent(ProvisionEventKind.ScoutBatchFailure,
                            failedFunding.Mission, failure, null));
                        // Park any RetryNextTurn failure here, while it is seen (before the next
                        // re-pack can drop this mission out of Funded and erase it from the
                        // session's AssignmentRejections).
                        if (failure.Disposition == ProvisionDisposition.RetryNextTurn)
                            parking.Park(player, failedFunding.Mission);
                        AiDebugLog.Write($"[AI][V2][Loop] assignment-batch "
                            + $"[{AiV2Trace.FormatCorrelation(failedFunding.Mission)}] {failedKey} — FAIL "
                            + $"{failure.Kind} [{failure.Disposition}] {failure.Detail}");
                    }

                    List<FundedEntry> openScouts = allocation.Funded.Where(fe =>
                        fe?.Mission?.Kind == MissionKind.Scout
                        && !provisioningSession.AlreadyProvisioned(
                            StableMissionKey.For(fe.Mission))).ToList();
                    Dictionary<StableMissionKey, ProvisionFailure> scoutFailureByKey =
                        scoutFailures.ToDictionary(
                            f => StableMissionKey.For(f.Funded.Mission), f => f.Failure);
                    CapabilityPoolExhaustionRegistry.SettleScoutBatch(snapshot, player,
                        openScouts.Select(fe => fe.Mission), scoutFailureByKey,
                        provisioningSession.Successful.Values.Select(m => m?.Mission));

                    // One batch means one re-pack. The allocator now sees every impossible Scout
                    // at once, so released AP can admit Economy/Development immediately.
                    if (allocationSession.HasNewFailures && !allocationSession.Converged
                        && assignmentReallocPass < AiConfigV2.maxReallocIterations)
                    {
                        assignmentReallocPass++;
                        Pack();
                        continue;
                    }
                }

                FundedEntry selectedFunding = allocation.Funded.FirstOrDefault(fe =>
                    fe?.Mission != null
                    && CapabilityPoolExhaustionRegistry.CanAttempt(player, fe.Mission, snapshot));
                if (selectedFunding == null)
                    break;

                StableMissionKey selectedKey = StableMissionKey.For(selectedFunding.Mission);
                outcome.AttemptedKeys.Add(selectedKey);
                ProvisioningResult result = steps.Provision(selectedFunding);
                if (result.Success)
                {
                    ProvisionedMission selected = result.Provisioned;
                    outcome.Selected = selected;
                    outcome.SelectedKey = selectedKey;
                    outcome.SelectedIsCommitment = selectedFunding.IsCommitment;
                    provisioningSession.RegisterSuccess(selectedKey, selected);
                    allocationSession.RegisterProvisionSuccess(selectedFunding,
                        selected.ClaimedAp, selected.ClaimedPhysical);
                    outcome.Events.Add(new ProvisionEvent(ProvisionEventKind.Success,
                        selectedFunding.Mission, default, selected));
                    AiV2Trace.CheckProvisionEnvelope(selectedFunding.Mission.AttemptId,
                        selected.ClaimedAp, selectedFunding.Tentative.Ap);
                    break;
                }

                CapabilityPoolExhaustionRegistry.RecordProvisionFailure(snapshot, player,
                    selectedFunding.Mission, result.Failure);
                allocationSession.RegisterProvisionFailure(selectedFunding, result.Failure);
                outcome.Events.Add(new ProvisionEvent(ProvisionEventKind.Failure,
                    selectedFunding.Mission, result.Failure, null));
                if (result.Failure.Disposition == ProvisionDisposition.RetryNextTurn)
                    parking.Park(player, selectedFunding.Mission);
                AiDebugLog.Write($"[AI][V2][Loop] provision [{AiV2Trace.FormatCorrelation(selectedFunding.Mission)}] "
                    + $"{selectedKey} — FAIL {result.Failure.Kind} "
                    + $"[{result.Failure.Disposition}] {result.Failure.Detail}");

                // A non-repricing failure rejects this key; a re-pack can consider other missions.
                // Count only a retry of the SAME key with a repriced envelope.
                if (!allocationSession.HasNewFailures || allocationSession.Converged
                    || (result.Failure.Disposition == ProvisionDisposition.RepriceThisTurn
                        && ++repriceReallocPass >= AiConfigV2.maxReallocIterations))
                {
                    // Nothing else can be provisioned from this pack. The last attempted key stays
                    // in AttemptedKeys; Selected stays null.
                    outcome.SelectedKey = selectedKey;
                    break;
                }
                Pack();
            }

            outcome.FinalAllocation = allocation;
            return outcome;
        }
    }
}
