using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;

namespace Game.Ai.V2
{
    // Phase A's infrastructure lane. DEF/ECO/DEV EconomicInfrastructure / DevelopmentInfra demands
    // are fulfilled by BuildingPlayExecutor through the authoritative gameplay API
    // (InfrastructureFulfillment), NOT the Unit/Hero materialization chain. The requesting axis
    // labels value/telemetry; AP comes from the shared pool. Three batches, one executor (Fulfill):
    //  · pre-pass — Economy builds and other builder-walk infrastructure, once, before any card;
    //  · arbitrated — Development and global-source Facilities, plain card plays that bid against
    //    the best materialization chain each round (BestArbitrated);
    //  · residual — zero-Radar uncommitted infrastructure, after card arbitration.
    internal static class PhaseAInfrastructureLane
    {
        internal readonly struct Bid
        {
            public readonly DemandState State;
            public readonly float Weighted;

            public Bid(DemandState state, float weighted)
            {
                State = state;
                Weighted = weighted;
            }
        }

        internal static void Classify(PhaseAPass pass)
        {
            // Zero-weight uncommitted infrastructure is a residual: it runs only after card
            // arbitration, on the AP and resources the cards left. Active committed Economy builds
            // keep their original early admission and resource/card protection.
            pass.ResidualInfrastructure = pass.States
                .Where(state => InfrastructureFulfillment.Handles(state.Demand.Capability)
                    && !StrategicPhaseA.IsCommittedEconomyBuild(pass.ActiveIntents, state.Demand)
                    && RadarValueScale.For(pass.Radar, state.Demand.RequestingAxis) <= 0f)
                .ToList();
            foreach (DemandState deferred in pass.ResidualInfrastructure)
                deferred.Blocked = true;
            if (pass.ResidualInfrastructure.Count > 0)
                AiDebugLog.Write($"[AI][V2]   strat.A infra - deferred {pass.ResidualInfrastructure.Count} zero-Radar requests until after card arbitration");
            // A Development facility / operator step is an ordinary card action with a positive
            // Radar weight: it is NOT a late sink. Each round of card arbitration ranks its intrinsic
            // card score x its own Radar scale against the best materialization chain, so the
            // better of the two acts first and the other is re-judged on the refreshed world.
            // An Economy global-source Facility is the same kind of plain card play (no builder,
            // no hold): playtest 2026-10-18 Thane spent its only Energy on Intel Center (net 2.7)
            // ahead of a threatened Citadel's garrison chain, so it competes there too.
            pass.ArbitratedInfrastructure = pass.States
                .Where(state => StrategicPhaseA.CompetesInCardArbitration(state.Demand)
                    && !pass.ResidualInfrastructure.Contains(state))
                .ToHashSet();
            if (pass.ArbitratedInfrastructure.Count > 0)
                AiDebugLog.Write($"[AI][V2]   strat.A infra - {pass.ArbitratedInfrastructure.Count} Development/global-source request(s) compete in card arbitration");
        }

        // Economy builds are handled here once, then blocked, so card arbitration does not emit
        // a spurious "no feasible chain" for a capability it can't match.
        internal static void FulfillPrePass(PhaseAPass pass) =>
            Fulfill(pass, pass.States.Where(state =>
                InfrastructureFulfillment.Handles(state.Demand.Capability)
                && !pass.ResidualInfrastructure.Contains(state)
                && !pass.ArbitratedInfrastructure.Contains(state)));

        // Recheck real AP and protected resources before zero-weight residual infrastructure.
        internal static void FulfillResidual(PhaseAPass pass) => Fulfill(pass, pass.ResidualInfrastructure);

        // The best admissible arbitrated step right now, weighted by its Radar scale. A step that
        // cannot act in this round (no legal build, AP pool, reserved or live resources) is closed
        // (Blocked) exactly as the single-shot build lane closed it; looking reserves and spends
        // nothing.
        internal static Bid? BestArbitrated(PhaseAPass pass)
        {
            Bid? best = null;
            float bestWeighted = float.NegativeInfinity;
            foreach (DemandState candidate in pass.ArbitratedInfrastructure
                .Where(s => !s.Blocked && s.Remaining > 0f)
                .OrderBy(s => s.Ordinal).ToList())
            {
                InfrastructureFulfillment.InfraProposal proposal = InfrastructureFulfillment.Propose(
                    pass.Snap, pass.Player, pass.Root, pass.Hand, pass.Ctx, candidate.Demand, pass.ApBudget,
                    pass.Reservation, pass.ActiveIntents, pass.Commitments);
                if (!proposal.Admissible || proposal.Score <= AiConfigV2.allocatorSliceEpsilon)
                {
                    candidate.Blocked = true;
                    AiDebugLog.WriteDedupedWithId(candidate.Demand.TraceId,
                        $"[AI][V2]   strat.A infra - {candidate.Demand}: not built ({proposal.Reason})");
                    continue;
                }
                float weighted = MaterializationPortfolioSolver.InfrastructureArbitrationScore(
                    proposal.Score, candidate.Demand.RequestingAxis, pass.Radar);
                if (weighted > bestWeighted)
                {
                    bestWeighted = weighted;
                    best = new Bid(candidate, weighted);
                }
            }
            return best;
        }

        // The SAME infrastructure executor handles every batch.
        internal static void Fulfill(PhaseAPass pass, IEnumerable<DemandState> pending)
        {
            pass.CloseSourceDemandsAlreadyInPlay();
            foreach (DemandState istate in pending
                .OrderByDescending(s => StrategicPhaseA.IsCommittedEconomyBuild(pass.ActiveIntents, s.Demand))
                .ThenByDescending(s => s.Demand.Value * RadarValueScale.For(pass.Radar, s.Demand.RequestingAxis)))
            {
                if (istate.Remaining <= 0f)
                    continue;
                istate.Blocked = true;
                pass.Result.InfrastructureAttempts++;
                FulfillOne(pass, istate);
            }
        }

        private static void FulfillOne(PhaseAPass pass, DemandState istate)
        {
            StrategicPhaseResult result = pass.Result;
            AxisDemand demand = istate.Demand;
            // Budget admission happens INSIDE TryFulfill, BEFORE any gameplay mutation: it
            // checks the shared AP pool and live affordability, and
            // only then runs the authoritative build. A shortfall => nothing spent, not built.
            // §2.4 — independent controlled-state snapshot around the op (building count,
            // filled facility slots, army movement, resources), NOT derived from the op's own
            // result. A failed build that changed any of these is a rollback leak.
            V2InfraWorldStamp infraBefore = AiV2Trace.InfraStamp(pass.Player, pass.Root);
            InfraFulfillResult infra;
            using (new Game.Core.ProfileScope("AI/PhaseA.InfraFulfill"))
                infra = InfrastructureFulfillment.TryFulfill(
                    pass.Snap, pass.Player, pass.Root, pass.Hand, pass.Ctx, demand, pass.ApBudget,
                    pass.Reservation, pass.ActiveIntents, pass.Commitments);
            V2InfraWorldStamp infraAfter = AiV2Trace.InfraStamp(pass.Player, pass.Root);
            if (infra.StateChanged)
                result.StateChanged = true;
            // A failed BUILD must roll back. A paid Challenge (win or loss) is an
            // intentional partial action and must NOT be diagnosed as a build leak.
            // A paid stand-alone Base level is its own successful operation (separate invariant);
            // every other non-built outcome must still have rolled back cleanly.
            if (infra.CapacityUnlocked)
                AiV2Trace.CheckCapacityUnlock(demand.TraceId, infraBefore, infraAfter);
            else if (!infra.GenerationAttempted)
                AiV2Trace.CheckInfrastructureRollback(demand.TraceId, infra.Built,
                    infra.StateChanged, infraBefore, infraAfter);
            // A compensated failure can still publish newer knowledge/version stamps.
            // Refresh reads without inventing a resource debit or a completed project.
            if (!infra.Built && !infra.GenerationAttempted && infra.StateVersionAfter >= 0)
            {
                pass.RefreshKnowledge();
                result.StateChanged = true;
            }
            if (infra.GenerationAttempted)
            {
                // A successful Challenge can exhaust AP before placement. Keep
                // the exact physical Hero and its destination in durable State.
                if (infra.Generated && infra.GeneratedOperatorCard != null
                    && demand.TargetHex.HasValue
                    && demand.DevelopmentOperatorMode.HasValue)
                    pass.IntentState?
                        .Development.RememberGeneratedDevelopmentOperator(
                            infra.GeneratedOperatorCard, demand.TargetHex.Value,
                            demand.DevelopmentOperatorMode.Value, pass.Turn);
                result.RecordGenerationAttempt(pass.Player, pass.Turn, infra.Generation, null, infra.Generated);
            }
            // A partial state-changing action (a paid Base capacity upgrade whose Facility
            // placement then failed) is debited like a Challenge: its AP is really spent.
            if (!(infra.Built || infra.CapacityUnlocked || infra.GenerationAttempted
                || (infra.StateChanged && infra.ApSpent > 0f)))
            {
                AiDebugLog.WriteDedupedWithId(demand.TraceId,
                    $"[AI][V2]   strat.A infra - {demand}: not built ({infra.Detail})");
                return;
            }
            // §2.3 — the physical AP drop must match what the transaction reports.
            AiV2Trace.CheckPhaseAAp(demand.TraceId, demand.RequestingAxis,
                infraBefore.Resources.Ap - infraAfter.Resources.Ap, infra.ApSpent);
            if (infra.Built)
            {
                istate.Remaining = Mathf.Max(0f, istate.Remaining - 1f);
                result.CardsPlayed += 1 + infra.AdditionalCardsConsumed;
                result.InfrastructureBuilt++;
                if (demand.Capability == CapabilityKind.DevelopmentInfrastructure)
                    DevelopmentOutcomeTelemetry.RecordFacilityBuilt(pass.Player, pass.Turn);
                result.CapabilityDeliveries++;
            }
            // The unlock closes ITS demand only: the later placement is a new request, judged on the
            // refreshed world (typed Infrastructure|Capability re-admission), never a counted
            // card play or facility build.
            if (infra.CapacityUnlocked)
            {
                istate.Remaining = Mathf.Max(0f, istate.Remaining - 1f);
                result.CapacityUnlocks++;
            }
            AiDebugLog.Write($"[AI][V2]   strat.A infra - {demand}: "
                + $"{(infra.Built ? "built" : infra.CapacityUnlocked ? "capacity unlocked" : infra.GenerationAttempted ? "operator Challenge" : "partial (not built)")} {infra.Detail} "
                + $"(ap {F(infra.ApSpent)} -> {DesireAxes.Abbrev(demand.RequestingAxis)})");
            if (infra.StateChanged)
            {
                pass.RefreshKnowledge();
                // A minted card is protected immediately. A consumed card or a
                // newly invalid factory loses protection in this same pass.
                InfrastructureFulfillment.RestoreGeneratedOperatorClaims(pass.Player,
                    pass.IntentState, pass.Hand, pass.Turn, pass.Reservation, pass.Ctx);
            }
            if (infra.Built && demand.RequestingAxis == DesireAxis.Economy
                && infra.BuilderArmyId.HasValue)
                EconomyContinuity.BeginEconomyBuilderRecovery(
                    pass.Player, pass.Snap, demand, infra.BuilderArmyId.Value, pass.Turn);
        }

        private static string F(float v) => v.ToString("0.##", CultureInfo.InvariantCulture);
    }
}
