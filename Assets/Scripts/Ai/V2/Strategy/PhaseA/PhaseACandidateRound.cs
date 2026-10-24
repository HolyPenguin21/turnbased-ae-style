using System.Collections.Generic;
using System.Linq;
using Game.Cards;
using Game.Map;
using Game.Players;
using UnityEngine;

namespace Game.Ai.V2
{
    // One round of Phase A card arbitration up to the pick: the owner-witnessed AP workload, the
    // TOP-K chains per active demand, the jointly feasible assignment and its live-fit filter.
    // Nothing is played here; the round is rebuilt after every executed action.
    internal sealed class PhaseACandidateRound
    {
        public readonly List<DemandState> Active;
        public readonly CapabilityInventory Inventory;
        public readonly Dictionary<DemandState, List<DemandCandidate>> Options =
            new Dictionary<DemandState, List<DemandCandidate>>();
        public List<PhaseACandidate> Feasible { get; private set; }

        private PhaseACandidateRound(List<DemandState> active, CapabilityInventory inventory)
        {
            Active = active;
            Inventory = inventory;
        }

        // AI-MGR — the NON-card half of the owner-witnessed AP workload the recurring-AP effect
        // (ApBonus) is priced against: AP that COMMITTED work will consume this turn — a durable
        // mission's claimed mover that has not activated yet, plus WITNESSED recon-air sorties
        // (ReconAssignmentPlanner.MeasureAirCapacity, the canonical air-capacity owner). Idle
        // unattached armies are deliberately NOT counted — a formation is not proof of a useful
        // operation. The card half is added per round from the real candidate set (Build).
        //
        // The witnessed measurement only matters when a PlayerGlobal recurring-resource effect
        // (ApBonus today) could actually be SCORED this turn: a carrier reachable through hand OR
        // deck (a Challenge could still mint it). Descriptor-driven via the registry — NOT a
        // hardcoded UnitAbilities.ApBonus scan — so a generated / future recurring mechanic is
        // covered with no edit here. Read once per call from the hand and deck at its start.
        internal static void MeasurePassWorkload(PhaseAPass pass)
        {
            pass.CommittedNonCardAp = CommittedNonCardApDemand(
                pass.Snap, pass.Player, pass.Root, pass.Ctx, pass.ActiveIntents, pass.ReconObjectives);
            pass.WitnessedUsefulApDemand = null;
            pass.ReachableGlobalRecurringCarrier = StrategicEffectRegistry.AnyGlobalRecurringCarrier(
                (pass.Snap?.Self?.Hand ?? System.Array.Empty<CardData>()).Select(c => c?.Definition)
                    .Concat(pass.Snap?.Self?.Deck ?? System.Array.Empty<CardDefinition>()));
        }

        internal static PhaseACandidateRound Build(PhaseAPass pass, List<DemandState> active)
        {
            var round = new PhaseACandidateRound(active,
                CapabilityInventory.Build(pass.Snap, pass.Player, pass.Commitments));
            List<(MaterializationPlan plan, float followupAp)> fillerUniverse = round.MeasureWitnessedWorkload(pass);
            round.CollectOptions(pass, fillerUniverse);
            round.Assign(pass);
            return round;
        }

        // AI-MGR — MEASUREMENT PASS (two-pass within the round). Assemble the owner-witnessed
        // AP workload the recurring-AP effect is priced against from the SAME feasible
        // candidate set the scoring pass below uses, BEFORE any AP-dependent scoring — so a
        // good ApBonus carrier is never dropped by fallback-scored Top-K pruning before it is
        // priced against the real workload. Feasibility-based ONLY, never DecisionScore /
        // Worthwhile (that would be circular: AP utility -> card score -> workload -> AP
        // utility). Recomputed every round because an executed chain changes hand / AP /
        // generation / physical state.
        //
        // Only worth doing when something this round actually READS it: a reachable
        // PlayerGlobal recurring-resource carrier (GlobalRecurringValue) or a Hero demand
        // (Command 6-vs-7 filler count). Otherwise leave WitnessedUsefulApDemand null -> the
        // evaluator uses the discounted structural fallback, exactly as before.
        private List<(MaterializationPlan plan, float followupAp)> MeasureWitnessedWorkload(PhaseAPass pass)
        {
            bool needsWitnessedWorkload = pass.ReachableGlobalRecurringCarrier
                || Active.Any(s => s.Demand.Capability == CapabilityKind.Hero);
            if (!needsWitnessedWorkload)
            {
                pass.WitnessedUsefulApDemand = null;
                return null;
            }
            var measOptions = new Dictionary<DemandState, List<(MaterializationPlan plan, float followupAp)>>();
            foreach (DemandState state in Active)
            {
                List<(MaterializationPlan plan, float followupAp)> feas;
                using (new Game.Core.ProfileScope("AI/PhaseA.AllFeasiblePlans"))
                    feas = MaterializationCandidateBuilder.AllFeasiblePlansForDemand(pass.Snap, pass.Player,
                        pass.Root, pass.Hand, pass.Ctx, state.Demand, pass.ApBudget, pass.Commitments,
                        pass.ApBudget.ReservedFollowup(), pass.Reservation, pass.ActiveIntents);
                if (feas.Count > 0)
                    measOptions[state] = feas;
            }
            float legalCardApWorkload;
            using (new Game.Core.ProfileScope("AI/PhaseA.LegalApWorkload"))
                legalCardApWorkload = MaterializationPortfolioSolver.EstimateLegalApWorkload(
                    measOptions, pass.Root, pass.Player, pass.Ctx, pass.Hand,
                    pass.Reservation.GenerationAttemptsLeft);
            pass.WitnessedUsefulApDemand = pass.CommittedNonCardAp + legalCardApWorkload;

            // Cross-demand filler universe for the hero Command 6-vs-7 valuation: every
            // feasible body across ALL active demands this round (one per physical consumption
            // signature), so a Hero demand's candidate can see the Unit plans a
            // FieldCombatPower demand would put in the same recipient.
            return measOptions.Values.SelectMany(v => v).ToList();
        }

        // TOP-K worthwhile chains per active demand (each carries its
        // own opportunity-adjusted DecisionScore), then a bounded max-total injective
        // assignment: exactly one collision-free chain per demand (or none), so no hand
        // card / generation source is ever double-counted as available capacity, and the
        // globally best total is chosen (not a greedy per-demand pick).
        private void CollectOptions(PhaseAPass pass,
            List<(MaterializationPlan plan, float followupAp)> fillerUniverse)
        {
            foreach (DemandState state in Active)
            {
                bool competingHeroDemand = state.Demand.Capability == CapabilityKind.ScoutCapability
                    && Active.Any(other => !ReferenceEquals(other, state)
                        && other.Remaining > AiConfigV2.allocatorSliceEpsilon
                        && other.Demand.Capability == CapabilityKind.Hero);
                List<DemandCandidate> top;
                using (new Game.Core.ProfileScope("AI/PhaseA.TopForDemand"))
                    top = MaterializationCandidateBuilder.TopForDemand(pass.Snap, pass.Player, pass.Root,
                        pass.Hand, pass.Ctx, state.Demand, pass.ApBudget, pass.Commitments,
                        pass.ApBudget.ReservedFollowup(), pass.Reservation, Inventory, competingHeroDemand,
                        AiConfigV2.phaseATopK, witnessedUsefulApDemand: pass.WitnessedUsefulApDemand,
                        fillerUniverse: fillerUniverse, activeIntents: pass.ActiveIntents);
                if (top.Count > 0)
                {
                    Options[state] = top;
                }
                // A new-hero alternative (IsEconomyNewHeroAlternative) is never a delivery
                // block: its chains are refused by price against the ready hero, and the
                // project itself stays with that hero — it must not be suppressed here.
                else if (StrategicPhaseA.IsBoundedBasePrerequisiteDemand(state.Demand))
                {
                    RecordBasePrerequisiteDeliveryBlock(pass, state);
                }
            }
        }

        // A confirmed delivery block of a Base project's prerequisite counts toward its
        // per-(card, site) failure streak; once the streak suppresses the site, the demand closes
        // and only THIS project's card claim and pending hold are released.
        private static void RecordBasePrerequisiteDeliveryBlock(PhaseAPass pass, DemandState state)
        {
            MaterializationDeliveryAvailability availability =
                MaterializationCandidateBuilder.OperationalDeliveryAvailabilityForDemand(
                    pass.Snap, pass.Player, pass.Root, pass.Hand, pass.Ctx, state.Demand, pass.Commitments,
                    pass.Reservation, pass.ActiveIntents);
            if (!availability.ConfirmedBlocked)
                return;
            bool suppressed = pass.IntentState != null && pass.IntentState
                .Economy.RecordBaseExpansionDeliveryFailure(pass.Turn,
                    state.Demand.EconomyBuildCard, state.Demand.TargetHex);
            AiDebugLog.Write($"[AI][V2]   strat.A economy delivery-block - "
                + $"target=({state.Demand.TargetHex.Value.Q},"
                + $"{state.Demand.TargetHex.Value.R}) "
                + $"raw={availability.RawCandidates} "
                + $"preflight={availability.PreflightCandidates} "
                + $"opDeliver={availability.OperationalCandidates} "
                + $"decision={(suppressed ? "reconsider-project" : "keep-continuity")}");
            if (!suppressed)
                return;
            state.Remaining = 0f;
            pass.Reservation.ClaimedEconomyBuildCards.Remove(state.Demand.EconomyBuildCard);
            // Only THIS project's pending hold — other active builds were
            // protected earlier in this pass and stay protected.
            string suppressedOwner = state.Demand.EconomyHeroBuildOwner;
            if (suppressedOwner != null)
                EconomyReservationLifecycle.ClearDeferredEconomyResources(
                    pass.Player, pass.Turn, suppressedOwner);
        }

        private void Assign(PhaseAPass pass)
        {
            Dictionary<DemandState, DemandCandidate> assigned;
            using (new Game.Core.ProfileScope("AI/PhaseA.BestAssignment"))
                assigned = Options.Count > 0
                    ? MaterializationPortfolioSolver.BestInjectiveAssignment(Options, pass.Root, pass.Player,
                        pass.Ctx, pass.Hand, pass.Reservation.GenerationAttemptsLeft, pass.Radar)
                    : new Dictionary<DemandState, DemandCandidate>();

            Feasible = assigned.Select(kv => new PhaseACandidate(kv.Key, kv.Value)).ToList();
            // One chain runs per round, so the pick must be legal against the live recipient
            // now; a chain that only fits after a portfolio hero lands waits for the next round.
            int portfolioSize = Feasible.Count;
            Feasible = Feasible.Where(c => ProjectedPhysicalState.FitsLiveNow(c.Plan)).ToList();
            if (portfolioSize > 0 && Feasible.Count < portfolioSize)
                AiDebugLog.Write($"[AI][V2]   strat.A order - {portfolioSize - Feasible.Count} of "
                    + $"{portfolioSize} portfolio chains wait for a capacity-raising chain to land first");
        }

        public float BestCardScore(Radar radar) => Feasible.Count == 0 ? float.NegativeInfinity
            : Feasible.Max(c => MaterializationPortfolioSolver.ArbitrationScore(c, radar));

        // The evaluator's opportunity-adjusted DecisionScore is the FINAL arbiter. The
        // `Feasible` set is already a JOINTLY feasible collision-free assignment
        // (BestInjectiveAssignment models the shared generation attempt + AP + H/E/M/T
        // pools), so there is no hidden hardcoded capability-priority layer deciding that a
        // Hero chain "protects" resources from a higher-DecisionScore Field chain. Only
        // deterministic tie-breakers follow the score.
        public PhaseACandidate Select(Radar radar) => Feasible
            .OrderByDescending(c => MaterializationPortfolioSolver.ArbitrationScore(c, radar))
            .ThenByDescending(c => c.State.Demand.Value)
            .ThenBy(c => (int)c.State.Demand.RequestingAxis)
            .ThenBy(c => c.Plan.StableKey, System.StringComparer.Ordinal)
            .ThenBy(c => c.State.Ordinal)
            .First();

        // AI-MGR — the NON-card half of the owner-witnessed AP workload (see MeasurePassWorkload).
        // Only DEMONSTRATED obligations count: a durable mission's claimed mover that has not
        // activated this turn (activation AP, floored at 1), plus the WITNESSED recon-air sortie count
        // from the canonical air-capacity owner (ReconAssignmentPlanner.MeasureAirCapacity). Idle
        // unattached armies and "actionable formation" heuristics are deliberately excluded — a
        // formation is not proof of a useful AP operation. Development is 0 here: DemandLayer emits
        // its axis demand precisely when there is NO operator base, so it is not a runnable AP action.
        private static float CommittedNonCardApDemand(WorldSnapshot snap, PlayerSetupData player,
            PlayerRoot root, AiTurnContext ctx, IReadOnlyList<MissionIntent> activeIntents,
            IReadOnlyList<ReconObjective> reconObjectives)
        {
            float ap = 0f;
            IReadOnlyList<ArmySnapshot> armies = snap?.Self?.Armies;
            if (activeIntents != null && armies != null)
            {
                var countedMovers = new HashSet<int>();
                foreach (MissionIntent intent in activeIntents)
                {
                    if (intent?.PreferredMoverArmyId == null) continue;
                    int moverId = intent.PreferredMoverArmyId.Value;
                    if (!countedMovers.Add(moverId)) continue;
                    ArmySnapshot army = null;
                    foreach (ArmySnapshot a in armies)
                        if (a != null && a.ArmyId == moverId) { army = a; break; }
                    if (army == null || army.HasActivatedThisTurn) continue;
                    // Recon audit B7 — an air mover (AirSweep wing) is priced once, through the air
                    // witness below; counting it here as well double-reserved its activation.
                    if (army.IsAir) continue;
                    ap += Mathf.Max(1f, army.ActivationApCost);
                }
            }

            (int airborne, int spare) = ReconAssignmentPlanner.MeasureAirCapacity(
                ctx, player, root, snap, reconObjectives);
            ap += (Mathf.Max(0, airborne) + Mathf.Max(0, spare)) * AiConfigV2.apAirSortieApProxy;
            return ap;
        }
    }
}
