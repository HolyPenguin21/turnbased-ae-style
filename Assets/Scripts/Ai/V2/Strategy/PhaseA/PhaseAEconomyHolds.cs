using System.Collections.Generic;
using System.Linq;
using Game.Economy;

namespace Game.Ai.V2
{
    // Phase A's Economy holds, set BEFORE any card is arbitrated: the resources and build cards of
    // active Economy builds (one slot per EconomyTaskKind), the FoundBase retarget, and the single
    // pre-intent hold of the best not-yet-committed build. The holds themselves are owned by
    // EconomyReservationLifecycle / the reservation ledger; this stage only decides which to renew.
    internal static class PhaseAEconomyHolds
    {
        // A target-specific Economy build may deliberately emit no repeated demand once
        // Continuity owns its builder. That active intent outranks every fresh build for its
        // own deferred-resource hold and must be protected before any early return.
        //
        // Protection is one independent slot PER EconomyTaskKind: a BuildExtraction's
        // BuildValue is structurally an order of magnitude higher than a FoundBase's
        // (extraction apCost~1 vs a Base's ~28), so the two kinds are not comparable on raw
        // BuildValue and must not compete for one slot. Within a kind, every ACTIVE Economy
        // intent is a real, independent obligation (Continuity supports several concurrent
        // BuildExtraction/FoundBase deliveries, each keyed by its own target hex — see
        // MissionIntentKey.For and EconomyContinuity.BeginEconomyDelivery), and
        // StrategicResourceReservationLedger is per-owner, so each keeps its own reserved
        // resources and idle clock.
        internal static void ProtectActiveBuilds(PhaseAPass pass)
        {
            // Build-card claims follow the same rule as the residuals and the hold: an entry that
            // re-evaluated Economy rebuilds them from the live builds and its own demands (owner
            // decision 2026-10-24), so a site Economy dropped no longer locks its card; any other
            // entry keeps what an earlier entry claimed.
            if (pass.EconomyAxisAuthoritative)
                pass.Reservation.ClaimedEconomyBuildCards.Clear();
            List<MissionIntent> protectedActiveExtractions = ProtectedActiveBuilds(pass, EconomyTaskKind.BuildExtraction);
            List<MissionIntent> protectedActiveBases = ProtectedActiveBuilds(pass, EconomyTaskKind.FoundBase);
            ReleaseUnprofitableBuilds(pass, protectedActiveBases, protectedActiveExtractions);
            // The FoundBase retarget/hysteresis switch below still evaluates against exactly one
            // (the highest-ranked) active Base commitment — deliberately unchanged scope: retargeting
            // several concurrent Base builds against each other's hysteresis margin is a separate,
            // unconfirmed need. Any OTHER concurrently active Base still gets its own resources
            // protected further below, just without retarget eligibility this turn.
            MissionIntent protectedActiveBase = protectedActiveBases.FirstOrDefault();

            foreach (MissionIntent extraction in protectedActiveExtractions)
                ProtectActiveEconomyBuild(pass, extraction);

            if (protectedActiveBase != null)
                ProtectOrRetargetBase(pass, protectedActiveBase);
            // Any OTHER concurrently active Base commitment beyond the one retarget-eligible slot
            // above still owns real reserved resources and must not be left unprotected.
            foreach (MissionIntent otherBase in protectedActiveBases.Skip(1))
                ProtectActiveEconomyBuild(pass, otherBase);
            pass.AnyProtectedActiveEconomyBuild =
                protectedActiveExtractions.Count > 0 || protectedActiveBases.Count > 0;
        }

        // Only an Active build is protected — owner decision 2026-10-24. Every Phase A entry runs
        // on a freshly resolved intent set, and ResolveActive resumes a TRANSIENT suspension
        // (PoolExhausted / CapabilityUnavailable) before it — that is how audit B11 keeps such a
        // build's hold. A build still Suspended here is a lasting one (EconomyLoan, Siege): it gives
        // up its resource hold and card claim while suspended. ResolveActive does not hand those
        // to Phase A either; this filter states the rule instead of relying on it.
        private static List<MissionIntent> ProtectedActiveBuilds(PhaseAPass pass, EconomyTaskKind kind) =>
            (pass.ActiveIntents?
                .Where(i => EconomyContinuity.IsLiveEconomyBuild(i) && i.Status == IntentStatus.Active
                    && i.Economy.Kind == kind)
                .OrderByDescending(i => i.Funding)
                .ThenByDescending(i => i.Economy.BuildValue)
                .ThenBy(i => i.CreatedTurn)
                .ThenBy(i => i.IntentKey)
                ?? Enumerable.Empty<MissionIntent>()).ToList();

        // Revalue live builds before renewing holds or paying the next action. An absent
        // demand in a partial/blocked view is not evidence of economic failure.
        private static void ReleaseUnprofitableBuilds(PhaseAPass pass, List<MissionIntent> protectedActiveBases,
            List<MissionIntent> protectedActiveExtractions)
        {
            if (pass.EconomyAxisAuthoritative)
            {
                foreach (MissionIntent build in protectedActiveBases.Concat(protectedActiveExtractions).ToList())
                {
                    if (build.Economy.Kind == EconomyTaskKind.FoundBase
                        && (build.Economy.BuildCard == null || !pass.Hand.Hand.Contains(build.Economy.BuildCard)))
                        continue;
                    var committed = new[] { build };
                    List<AxisDemand> sameSite = pass.Demands.Where(d => d != null
                        && (d.Capability == CapabilityKind.EconomicExpansionBase
                            || d.Capability == CapabilityKind.EconomicInfrastructure)
                        && EconomyContinuity.HasEconomyBuildCommitment(committed, d)).ToList();
                    if (sameSite.Count == 0
                        || !EconomyContinuity.TryReleaseUnprofitableBuild(pass.IntentState, pass.Player,
                            build, sameSite.OrderByDescending(d => d.Value).First(), pass.Turn))
                        continue;
                    protectedActiveBases.Remove(build);
                    protectedActiveExtractions.Remove(build);
                    pass.Demands = pass.Demands.Where(d => !sameSite.Contains(d)).ToList();
                    // Intent/lease changes invalidate ownership and demand views even when no
                    // card was played and no army moved in this pass.
                    pass.Result.StateChanged = true;
                }
            }
            if (pass.Result.StateChanged)
            {
                pass.ActiveIntents = pass.IntentState.All.Where(i => i != null
                    && i.Status == IntentStatus.Active).ToList();
                pass.Commitments = ActorCommitments.FromIntents(pass.ActiveIntents, pass.Snap, pass.ReconObjectives);
            }
        }

        // Exception, scoped to FoundBase only (unchanged behaviour from before the split,
        // just evaluated against Base's own slot instead of a shared one): per the project
        // owner's own call, a Base site must keep reacting to newly-known hexes even after
        // commitment, not just while still staged (DemandLayer.Economy.AddBaseCandidates
        // already does this for an uncommitted candidate — see
        // economyBaseSwitchHysteresisThreshold). Only safe to release while the founding
        // card is still physically in hand (nothing irreversible spent yet) and only for a
        // fresh candidate that clears the SAME hysteresis margin — one threshold, one rule,
        // now applied at both stages instead of just the first.
        private static void ProtectOrRetargetBase(PhaseAPass pass, MissionIntent protectedActiveBase)
        {
            AxisDemand supersedingBaseSite = null;
            if (protectedActiveBase.Economy.BuildCard != null
                && pass.Hand.Hand.Contains(protectedActiveBase.Economy.BuildCard)
                && pass.EconomyAxisAuthoritative)
            {
                supersedingBaseSite = pass.Demands
                    .Where(d => DemandLayer.CanReplaceCommittedBase(protectedActiveBase, d))
                    .OrderByDescending(d => d.Value)
                    .FirstOrDefault();
            }
            if (supersedingBaseSite != null
                && EconomyContinuity.TryRetargetCommittedBase(pass.Player,
                    protectedActiveBase, supersedingBaseSite, pass.Turn))
            {
                // Continuity has atomically rekeyed the same intent/actor and released
                // the old reservation owner; protect the NEW exact build before any cards.
                AiDebugLog.Write($"[AI][V2]   strat.A economy hold - switched Base "
                    + $"to ({supersedingBaseSite.TargetHex.Value.Q},"
                    + $"{supersedingBaseSite.TargetHex.Value.R}) net={supersedingBaseSite.Value:0.##}");
                ProtectActiveEconomyBuild(pass, protectedActiveBase);
            }
            else
            {
                if (supersedingBaseSite != null)
                    pass.Demands = pass.Demands.Where(d => !object.ReferenceEquals(d, supersedingBaseSite))
                        .ToList(); // failed transition cannot leak a rival executable demand
                ProtectActiveEconomyBuild(pass, protectedActiveBase);
            }
        }

        private static void ProtectActiveEconomyBuild(PhaseAPass pass, MissionIntent active)
        {
            EconomyReservationLifecycle.ReserveDeferredEconomyResourcesForActiveIntent(
                pass.Player, pass.Turn, active);
            if (active.Economy.BuildCard != null)
                pass.Reservation.ClaimedEconomyBuildCards.Add(active.Economy.BuildCard);
            // This hold IS the intent's real activity this cycle even when nothing is
            // executable yet (waiting on H/E/M/T to accumulate) — tell Continuity so it does
            // not silently age through ReconcileAfterTurn's idle-stall path (IntentReapedIdle)
            // for legitimately waiting on its own reserved resources. See MarkProtectedThisTurn.
            MissionContinuityLayer.MarkProtectedThisTurn(pass.Player, active.IntentKey, pass.Turn);
            AiDebugLog.WriteDeduped(active.IntentKey.ToString(),
                $"[AI][V2]   strat.A economy hold - protected active "
                + $"{active.Economy.Kind} "
                + $"@({active.Economy.TargetHex.Q},{active.Economy.TargetHex.R}) before card arbitration");
        }

        internal static void ClaimDemandBuildCards(PhaseAPass pass)
        {
            foreach (AxisDemand economyDemand in pass.Demands.Where(d => d != null
                         && d.RequestingAxis == DesireAxis.Economy
                         && d.EconomyBuildCard != null))
                pass.Reservation.ClaimedEconomyBuildCards.Add(economyDemand.EconomyBuildCard);
        }

        // No obligation survived selection — but only clear when this call actually had
        // Economy's authoritative view. A dirty-axis subset that never included Economy
        // (EconomyAxisAuthoritative == false) says nothing about whether Economy's build
        // target still exists; the hold must be left exactly as it was.
        internal static void ClearPreIntentHoldIfResolved(PhaseAPass pass)
        {
            if (pass.EconomyAxisAuthoritative && !pass.AnyProtectedActiveEconomyBuild)
                EconomyReservationLifecycle.ClearDeferredEconomyResources(pass.Player, pass.Turn);
        }

        // Establish the existing single Economy hold BEFORE infrastructure can spend it.
        internal static void HoldPreIntentBuild(PhaseAPass pass)
        {
            var economyBuildObligations = pass.AnyProtectedActiveEconomyBuild
                ? new List<AxisDemand>()
                : pass.AllStates.Where(s => !pass.ColdStates.Contains(s)
                        && (!s.Demand.IsPersistenceDeferred
                            || InfrastructureFulfillment.EconomyHeroPrerequisiteOwner(s.Demand) != null))
                    .Select(s => s.Demand)
                    .Where(d => EconomyReservationLifecycle.ShouldReserveDeferredEconomyResources(pass.Snap, d))
                    .Where(d => PendingPrerequisiteHoldIsLive(pass, d))
                    .ToList();
            AxisDemand protectedEconomyBuild = economyBuildObligations
                .OrderByDescending(d => StrategicPhaseA.IsCommittedEconomyBuild(pass.ActiveIntents, d) ? 1 : 0)
                .ThenByDescending(d => d.Value)
                .ThenByDescending(d => DemandLayer.EconomyBuildKind(d) == EconomyTaskKind.FoundBase ? 1 : 0)
                .ThenByDescending(d => d.EconomySiteValue)
                .ThenBy(d => d.TargetHex?.Q ?? int.MaxValue)
                .ThenBy(d => d.TargetHex?.R ?? int.MaxValue)
                .FirstOrDefault();
            if (protectedEconomyBuild == null)
            {
                ClearPreIntentHoldIfResolved(pass);
                return;
            }
            // The single pre-intent Economy hold. No durable Economy build exists here
            // (economyBuildObligations is empty whenever one does), so every deferred row is an
            // earlier pass's demand hold; a re-admission pass that now protects a different
            // site replaces it instead of stacking a second hold for the same build card.
            EconomyReservationLifecycle.RetainDeferredEconomyOwner(pass.Player, pass.Turn,
                InfrastructureFulfillment.EconomyHeroPrerequisiteOwner(protectedEconomyBuild)
                    ?? InfrastructureFulfillment.EconomyReservationOwner(protectedEconomyBuild));
            if (InfrastructureFulfillment.EconomyHeroPrerequisiteOwner(protectedEconomyBuild) != null)
                EconomyReservationLifecycle.ReserveDeferredEconomyResourcesForPendingHero(
                    pass.Player, pass.Turn, protectedEconomyBuild);
            else
                EconomyReservationLifecycle.ReserveDeferredEconomyResources(
                    pass.Snap, pass.Player, pass.Turn, protectedEconomyBuild);
            AiDebugLog.Write($"[AI][V2]   strat.A economy hold - protected "
                + $"{protectedEconomyBuild.Capability} @({protectedEconomyBuild.TargetHex?.Q},"
                + $"{protectedEconomyBuild.TargetHex?.R}) before card arbitration");
        }

        // See StrategicPhaseA.PendingPrerequisiteHoldIsLive(AxisDemand, availability): the
        // availability read and its log line for one candidate obligation.
        private static bool PendingPrerequisiteHoldIsLive(PhaseAPass pass, AxisDemand demand)
        {
            if (!StrategicPhaseA.HoldsForPendingPrerequisite(demand))
                return true;
            MaterializationDeliveryAvailability availability =
                MaterializationCandidateBuilder.OperationalDeliveryAvailabilityForDemand(
                    pass.Snap, pass.Player, pass.Root, pass.Hand, pass.Ctx, demand, pass.Commitments,
                    pass.Reservation, pass.ActiveIntents);
            bool live = StrategicPhaseA.PendingPrerequisiteHoldIsLive(demand, availability);
            if (!live)
                AiDebugLog.Write($"[AI][V2]   strat.A economy hold - skip pending "
                    + $"{demand.Capability} @({demand.TargetHex?.Q},{demand.TargetHex?.R}): "
                    + $"prerequisite structurally undeliverable raw={availability.RawCandidates} "
                    + $"preflight={availability.PreflightCandidates} "
                    + $"opDeliver={availability.OperationalCandidates}");
            return live;
        }
    }
}
