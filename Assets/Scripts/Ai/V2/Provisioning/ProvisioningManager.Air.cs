using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Game.Aviation;
using Game.Cards;
using Game.Economy;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Units;
using UnityEngine;

using Game.Combat;

namespace Game.Ai.V2
{
    // Air Scout lane provisioning + the strategic air-sortie admission gate. A mechanical
    // partial of ProvisioningManager; Provision (ProvisioningManager.cs) is its only caller.

    internal static partial class ProvisioningManager
    {
        // Claim the air actor/subset Assignment already picked, THROUGH THE SAME generic
        // funding/provisioning accounting Ground uses: the real AP/Energy Assignment resolved for
        // this exact actor/subset (ScoutExecutionCandidate.RequiredAp/RequiredEnergy — see
        // ReconAssignmentPlanner.AppendAirCandidates) is checked against the envelope Funding
        // granted (funded.Tentative.Ap / funded.PhysicalDraw.Energy) and, if it fits, claimed as
        // ClaimedAp/ClaimedEnergy. If it does not fit, this returns the ordinary EnvelopeTooSmall
        // failure and the repack/reprice loop (ResourceAllocator.RegisterProvisionFailure) handles
        // it exactly like ground — no separate air ledger. The terminal air execution stage still
        // re-checks LIVE HARD gates (AiAirSortiePlanner.CanAffordLaunch / CanIssueMoveNow / AA /
        // safe return) against the post-ground-movement world before spending anything, but never
        // re-runs strategic hand/deck/income economics: that decision is made once, here, by
        // AirSortieReservationAdmission.
        private static ProvisioningResult ProvisionAir(PlayerSetupData player, PlayerRoot root, AiTurnContext ctx,
            ProvisioningSession session, FundedEntry funded, ScoutExecutionCandidate exec,
            ScoutMissionTarget target, StableMissionKey key)
        {
            // Aviation serves only AirSweep (ReconAssignmentPlanner.AppendAirCandidates), whose focus
            // is its moving ANCHOR, not an observation goal: seeing the enemy concentration never
            // "satisfies" the pass (the sortie ends by its refuel endurance), so there is no
            // objective check here.
            MissionProposal m = funded.Mission;
            HexCoord focus = target.FocusHex;
            HexCoord executionHex = exec.ExecutionHex;

            if (exec.ExecutorKind != ScoutExecutorKind.AirExisting || exec.Army == null)
                return ProvisioningResult.Fail(ProvisionFailure.NoMoverExists(
                    "air missions may use only an already-formed aviation army"));

            ArmyData wing = ResolveArmy(player, exec.Army.ArmyId);
            if (wing == null || wing.Owner != player || !AviationRules.IsValidAirArmy(wing)
                || wing.CurrentMovement <= 0)
                return ProvisioningResult.Fail(ProvisionFailure.MoverContended(
                    $"assigned air actor #{exec.Army.ArmyId} is no longer a usable air wing"));

            AirSortie liveSortie = AirSortieRegistry.ForArmy(player, wing);
            bool ready = ReconAirCapacityPolicy.IsReadyStandaloneWing(player, wing);
            bool continuing = ReconAirCapacityPolicy.IsAirborneReconWing(player, wing)
                && liveSortie != null
                && liveSortie.Kind == AirSortieKind.Recon;

            if (continuing)
            {
                ReconAirSortieState projected = ReconAirReservationPrepass.ProjectScoringSortie(player, ctx, wing);
                if (projected == null)
                    return ProvisioningResult.Fail(ProvisionFailure.MoverContended(
                        $"continuing air actor #{wing.Id} has no valid live Recon sortie state"));
                if (projected.Phase == ReconAirPhase.Return || projected.Phase == ReconAirPhase.Hold)
                    return ProvisioningResult.Fail(ProvisionFailure.NoExecutableStep(
                        $"continuing air actor #{wing.Id} is Return/Hold-bound this turn (recovery, not fresh Recon progress)"));
            }
            else if (!ready)
            {
                return ProvisioningResult.Fail(ProvisionFailure.MoverContended(
                    $"assigned air actor #{wing.Id} is neither a ready standalone wing nor a valid continuing Recon sortie"));
            }

            int moverArmyId = wing.Id;
            HexCoord airfieldHex = default;
            List<UnitData> launchSubset = null;

            // The real, actor-specific cost Assignment already resolved for THIS
            // exact candidate (see AppendAirCandidates: a live Pick/PickFromStorage against the
            // bound mission target, not a generic "some useful step exists" probe). Compare against
            // the envelope Funding granted; claim for real only if it fits.
            float eps = AiConfigV2.allocatorSliceEpsilon;
            float realAp = exec.RequiredAp;
            float realEnergy = exec.RequiredEnergy;
            float apEnvelope = funded.Tentative.Ap;
            float energyEnvelope = funded.PhysicalDraw.Energy;
            if (realAp > apEnvelope + eps || realEnergy > energyEnvelope + eps)
                // Round 7 (Problem 3) — report BOTH the real AP and the real Energy this exact air
                // actor needs, not AP alone: an air launch's Energy shortfall must raise an Energy
                // floor too, so ResourceAllocator's next Pack() can actually fund it, instead of
                // repricing only the AP dimension and looping on the same Energy-starved envelope.
                return ProvisioningResult.Fail(ProvisionFailure.EnvelopeTooSmall(
                    new ProvisionRequirement(realAp, new ResourceVector(0f, 0f, realEnergy, 0f, 0f)),
                    $"air actor #{moverArmyId} needs {N(realAp)} AP / {N(realEnergy)} Energy, "
                    + $"envelope is {N(apEnvelope)} AP / {N(energyEnvelope)} Energy"));

            float turnApLeft = root.ActionPoints - session.ApClaimed;
            if (realAp > turnApLeft + eps)
                return ProvisioningResult.Fail(ProvisionFailure.MoverContended(
                    $"turn AP exhausted: air actor #{moverArmyId} needs {N(realAp)}, {N(turnApLeft)} left after earlier claims"));

            float liveEnergyLeft = AirSpendableEnergyLeft(player, root, ctx, session);
            if (realEnergy > liveEnergyLeft + eps)
                return ProvisioningResult.Fail(ProvisionFailure.MoverContended(
                    $"spendable Energy exhausted: air actor #{moverArmyId} needs {N(realEnergy)}, "
                    + $"{N(liveEnergyLeft)} left after reservations and earlier claims this pass"));

            // AI-MGR — the SINGLE strategic sortie-reservation admission. Everything above
            // (CanAffordLaunch, the funded-envelope comparison, the cumulative live AP/Energy checks)
            // is HARD feasibility: "enough resources exist right now". This is the one strategic
            // question — "is THIS exact sortie worth paying for this turn versus keeping the Energy
            // for hand/deck card pressure?" — and it is answered HERE and nowhere else. The canonical
            // staged decision (Resource Outlook -> Hand/Deck Energy Pressure -> Recon Value ->
            // Reserve) is AviationSortieReservationEvaluator, fed the exact per-actor AP/Energy and
            // the mission-specific route score Assignment already resolved (ScoutExecutionCandidate),
            // never a fresh air-route re-probe. Recomputed every turn: an idle aircraft never yields
            // a standing reservation. Tactical layers below MUST NOT re-run this economics — they are
            // limited to live hard/safety gates (CanAffordLaunch / CanIssueMoveNow / AA / safe return).
            ProvisionFailure? sortieDeclined = AirSortieReservationAdmission(
                player, root, ctx, session, exec, moverArmyId, realAp, realEnergy);
            if (sortieDeclined.HasValue)
                return ProvisioningResult.Fail(sortieDeclined.Value);

            return ProvisioningResult.Ok(new ProvisionedMission
            {
                Mission = m,
                Key = key,
                Kind = MissionKind.Scout,
                ScoutKind = target.Kind,
                MoverArmyId = moverArmyId,
                FocusHex = focus,
                ExecutionHex = executionHex,
                ClaimedAp = realAp,
                ClaimedEnergy = realEnergy,
                ClaimedPhysical = new ResourceVector(0f, 0f, realEnergy, 0f, 0f),
                StealthApReserved = false,
                RequiresStealth = false,
                ExecutorKind = exec.ExecutorKind,
            });
        }

        // The ONE strategic admission for an air-recon sortie whose HARD feasibility already passed
        // inside ProvisionAir. Returns null to proceed with the reservation, or a SortieNotWorthwhile
        // failure (RetryNextTurn, no cooldown) when the canonical evaluator declines this turn.
        //
        // No generic air-route re-probe happens here: Assignment (ReconAssignmentPlanner.
        // AppendAirCandidates) already picked this exact actor AND proved a mission-specific
        // route — the resulting AIR-01 route score and the exact per-actor AP/Energy ride in on the
        // ScoutExecutionCandidate. This method feeds those figures, plus what earlier missions this
        // pass have already claimed (session.ApClaimed/EnergyClaimed), straight into the canonical
        // AviationSortieReservationEvaluator (Resource Outlook -> Hand/Deck Energy Pressure -> Recon
        // Value -> Reserve), which reads live hand/deck/income Energy pressure itself. No new
        // evaluator, no per-turn reservation registry — recomputed every turn.
        // The Energy an air claim (Recon sortie, Raid AirSupport) may still take this pass. Air is
        // non-Economy spending, so it must fit StrategicSpendability — owner-aware ledger holds
        // (EconomyDeferredBuild/Completion, reaction envelope) and unpaid mandatory-recovery
        // activation (the ONE model of what airborne wings owe) are off limits — minus
        // session.EnergyClaimed: Provisioning never mutates world
        // resources, so sequential air claims in one pass would otherwise each see the same stock.
        // Only air missions set ClaimedEnergy, so this never double-counts an Economy ledger row.
        internal static float AirSpendableEnergyLeft(PlayerSetupData player, PlayerRoot root,
            AiTurnContext ctx, ProvisioningSession session) =>
            StrategicSpendability.SpendableAmount(player, root, ctx, ResourceType.Energy)
            - (session?.EnergyClaimed ?? 0f);

        private static ProvisionFailure? AirSortieReservationAdmission(
            PlayerSetupData player, PlayerRoot root, AiTurnContext ctx, ProvisioningSession session,
            ScoutExecutionCandidate exec, int moverArmyId, float realAp, float realEnergy)
        {
            // Bare harness / no world to reason about — hard gates already passed, leave behaviour unchanged.
            if (ctx?.Map == null || session?.Snapshot == null)
                return null;

            string label = $"actor=#{moverArmyId}";

            AviationReservationDecision decision = AviationSortieReservationEvaluator.EvaluateRecon(
                player, root, ctx.Map,
                Mathf.CeilToInt(Mathf.Max(0f, realAp)),
                Mathf.CeilToInt(Mathf.Max(0f, realEnergy)),
                exec.RouteScore,
                exec.NextTurnEnergy,
                exec.NextTurnAp,
                AirSpendableEnergyLeft(player, root, ctx, session),
                Mathf.CeilToInt(Mathf.Max(0f, session.ApClaimed)));
            AiDebugLog.Write(decision.ToLog(label));

            return decision.ShouldReserve
                ? (ProvisionFailure?)null
                : ProvisionFailure.SortieNotWorthwhile(
                    $"air actor #{moverArmyId}: sortie not worth reserving this turn ({decision.Reason})");
        }
    }
}
