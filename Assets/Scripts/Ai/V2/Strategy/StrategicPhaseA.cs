using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Game.Cards;
using Game.Economy;
using Game.Map;
using Game.Players;
using UnityEngine;

namespace Game.Ai.V2
{
    // ARCH-02 §8 — Strategic Phase A: the demand-driven card-play pass that runs BEFORE mission
    // planning. It orchestrates only; the algorithms it drives live in their own owners —
    // MaterializationCandidateBuilder (candidate chains), MaterializationPortfolioSolver (jointly
    // feasible set), MaterializationExecutor (play), CapabilityDeliveryEvaluator (delivered amount
    // + lease), InfrastructureFulfillment (build lane). Spends from the live AP pool
    // (PhaseAApBudget). One call is one PhaseAPass, run as stages (PhaseA/): Economy holds
    // (PhaseAEconomyHolds), infrastructure batches (PhaseAInfrastructureLane), card rounds
    // (PhaseACandidateRound, executed here), residual demands (PhaseAPass.PublishResiduals).
    public static class StrategicPhaseA
    {
        // Re-entrant within a turn: the Main pass, every strategic re-admission, a reaction round
        // and the cold residual call it. What carries between entries is `carriedReservation`
        // (generator claims, build-card claims, residual demands), `apBudget` (follow-up AP
        // promises), the reservation ledger, the turn's tempo budget and `intentState`.
        //
        // economyAxisAuthoritative — true when `demands` reflects Economy's COMPLETE current view
        // (the Main pass, a reaction round's fresh DemandLayer.Generate, or an orchestration
        // reconciliation that actually re-evaluated Economy this round). False when `demands` is a
        // dirty-axis SUBSET that deliberately excludes Economy because Economy itself was not
        // re-evaluated this call (StrategicReadmissionRunner.Run) — there, an absent
        // Economy demand means "not looked at", not "resolved", and must never be read as license
        // to drop the deferred-build hold. Defaults to true: the Main pass and a reaction round
        // rely on it.
        // deferFreshZeroRadar — early admissions leave fresh zero-Radar work to the late residual
        // entry (see ShouldDeferFreshZeroRadarDemand).
        // `intentState` - the owner's durable intent store: Phase A transactions record their
        // generated operators and delivery failures in it.
        public static StrategicPhaseResult FulfillDemands(WorldSnapshot snap, PlayerSetupData player,
            PlayerRoot root, AiHandData hand, AiTurnContext ctx, PhaseAApBudget apBudget,
            IReadOnlyList<AxisDemand> demands, ActorCommitments commitments, MissionIntentState intentState,
            IReadOnlyList<MissionIntent> activeIntents = null,
            IReadOnlyList<ReconObjective> reconObjectives = null,
            MaterializationReservation carriedReservation = null,
            bool economyAxisAuthoritative = true, Radar radar = null,
            bool deferFreshZeroRadar = false)
        {
            using var __profile = new Game.Core.ProfileScope("AI/Strategic.FulfillDemands");
            // One turn-scoped identity set must survive main, reaction and housekeeping entries:
            // a failed generator/card pair is not a fresh candidate merely because Phase A re-entered.
            var result = new StrategicPhaseResult
            {
                Reservation = carriedReservation ?? new MaterializationReservation()
            };
            if (player == null || root == null || hand == null || apBudget == null || ctx == null)
                return result;
            var pass = new PhaseAPass(snap, player, root, hand, ctx, apBudget, demands, commitments,
                intentState, activeIntents, reconObjectives, economyAxisAuthoritative, radar,
                deferFreshZeroRadar, result);
            RefreshActorFollowups(pass);
            // A new turn constructs a fresh MaterializationReservation. Restore the exact
            // pending Hero even when the current dirty-demand subset omits Development.
            InfrastructureFulfillment.RestoreGeneratedOperatorClaims(player, intentState, hand,
                ctx.TurnNumber, result.Reservation, ctx);

            PhaseAEconomyHolds.ProtectActiveBuilds(pass);
            PhaseAEconomyHolds.ClaimDemandBuildCards(pass);
            PhaseACandidateRound.MeasurePassWorkload(pass);
            // One shared per-turn generation budget: a reaction-round Phase A must
            // not reset the Challenge count a main-pass generation already spent.
            result.Reservation.GenerationAttemptsUsed = StrategicTempoBudget.GenerationUsed(player, ctx.TurnNumber);
            AiDebugLog.WriteDeduped("hand", $"[AI][V2]   strat.A - {player.Nickname} hand {AiCardLog.Hand(hand)}");

            pass.Partition();
            if (pass.NothingToAdmit)
            {
                PhaseAEconomyHolds.ClearPreIntentHoldIfResolved(pass);
                pass.PublishColdResiduals();
                return result;
            }
            PhaseAEconomyHolds.HoldPreIntentBuild(pass);
            PhaseAInfrastructureLane.Classify(pass);
            PhaseAInfrastructureLane.FulfillPrePass(pass);
            // CardUpgrade is intentionally not pre-executed here. It enters the same candidate
            // builder + jointly-feasible Phase-A portfolio below as every materialization demand.
            RunCardArbitration(pass);
            PhaseAInfrastructureLane.FulfillResidual(pass);
            pass.PublishResiduals();

            if (result.CardsPlayed > 0)
                AiDebugLog.Write($"[AI][V2] strat.A - {result.CardsPlayed} chain(s), budget now " + apBudget.DebugLine());
            if (result.Reservation.UnresolvedDemands.Count > 0)
                AiDebugLog.Write($"[AI][V2] strat.A - residual demands "
                    + string.Join(" | ", result.Reservation.UnresolvedDemands.Select(d => d.ToString())));
            return result;
        }

        // Local refits promise one real next activation. Reentry after movement, a phase
        // change or base loss must not keep an obsolete actor promise alongside the live bank.
        private static void RefreshActorFollowups(PhaseAPass pass)
        {
            foreach (int id in pass.ApBudget.ActorFollowupIds.ToArray())
            {
                var primary = OwnWorld.Of(pass.Player).Army(id);
                var attack = primary == null ? null
                    : AttackBaseRefitPolicy.Resolve(
                        pass.IntentState?.All, pass.Player, id, primary.Hex, pass.Turn);
                pass.ApBudget.ReserveActorFollowup(id, primary == null || attack == null ? 0f
                    : AttackBaseRefitPolicy.FollowupAp(pass.Player, primary, attack, primary.Members, pass.Ctx, pass.Root));
            }
        }

        // Card arbitration: one action per round, at most maxDemandFulfillmentActionsPerTurn per
        // call. Each round compares the best jointly feasible chain with the best arbitrated
        // infrastructure step; the winner acts and the next round re-judges both on the new world.
        private static void RunCardArbitration(PhaseAPass pass)
        {
            while (pass.ChainAttempts < AiConfigV2.maxDemandFulfillmentActionsPerTurn)
            {
                pass.CloseSourceDemandsAlreadyInPlay();
                List<DemandState> active = pass.ActiveMaterializationStates();
                // Infrastructure competing in the same arbitration is judged on the live world each
                // round; with no materialization demand left it simply has no card rival.
                if (active.Count == 0)
                {
                    PhaseAInfrastructureLane.Bid? onlyInfra = PhaseAInfrastructureLane.BestArbitrated(pass);
                    if (onlyInfra.HasValue)
                    {
                        pass.ChainAttempts++;
                        PhaseAInfrastructureLane.Fulfill(pass, new[] { onlyInfra.Value.State });
                        continue;
                    }
                    if (pass.TryPromotePersistenceDeferred())
                        continue;
                    break;
                }

                PhaseACandidateRound round = PhaseACandidateRound.Build(pass, active);

                // ONE comparison of weighted scores: the best jointly feasible materialization
                // chain (DecisionScore x Radar scale) against the best admissible Development
                // infrastructure step (intrinsic card score x the same Radar scale). No lane has a
                // hard priority; the winner acts and the next round re-judges both on the new world.
                PhaseAInfrastructureLane.Bid? infraBid = PhaseAInfrastructureLane.BestArbitrated(pass);
                if (infraBid.HasValue)
                {
                    float bestCard = round.BestCardScore(pass.Radar);
                    if (infraBid.Value.Weighted > bestCard)
                    {
                        AiDebugLog.Write($"[AI][V2]   strat.A arbitration - {infraBid.Value.State.Demand} "
                            + $"wins {F(infraBid.Value.Weighted)} over best chain {(round.Feasible.Count == 0 ? "none" : F(bestCard))}");
                        pass.ChainAttempts++;
                        PhaseAInfrastructureLane.Fulfill(pass, new[] { infraBid.Value.State });
                        continue;
                    }
                }

                if (round.Feasible.Count == 0)
                {
                    ReportNoFeasibleChain(pass, round);
                    // No currently-active demand has any feasible chain this round — the AI is about
                    // to give up on real strategic work for this pass. Before it does, give any
                    // persistence-deferred demand its no-alternative-work chance (spec Rule 2).
                    if (pass.TryPromotePersistenceDeferred())
                        continue;
                    break;
                }

                ExecuteSelected(pass, round, round.Select(pass.Radar));
            }
        }

        private static void ReportNoFeasibleChain(PhaseAPass pass, PhaseACandidateRound round)
        {
            foreach (DemandState state in round.Active)
            {
                AxisDemand d = state.Demand;
                float reserved = pass.ApBudget.ReservedFollowup();
                if (round.Options.TryGetValue(state, out var topOpts) && topOpts.Count > 0)
                {
                    DemandCandidate b = topOpts[0];
                    AiDebugLog.Write($"[AI][V2]   strat.A hold - {d}: best chain {b.Plan.StableKey} "
                        + $"play {F(b.PlayScore)} hold {F(b.HoldValue)} decision {F(b.DecisionScore)} "
                        + $"idle={F(b.Plan.UseBreakdown?.IdleBonus ?? 0f)} age={b.Plan.UseBreakdown?.IdleAgeTurns ?? 0} "
                        + "not worth playing over holding the card / lost to contention; keep in hand");
                    continue;
                }
                string diag;
                using (new Game.Core.ProfileScope("AI/PhaseA.ExplainNoChain"))
                    diag = MaterializationDiagnostics.ExplainNoChain(
                        pass.Snap, pass.Player, pass.Root, pass.Hand, pass.Ctx, d, pass.ApBudget,
                        pass.Commitments, reserved);
                // The preparation host's witness reads this verdict (one truth) — only a
                // structural one: no chain shape at all, or shapes that pass the play
                // preflight yet cannot deliver into this host. An AP/resource shortfall of
                // this round is timing and marks nothing.
                // The same structural verdict releases the demand's claim on hand cards.
                {
                    MaterializationDeliveryAvailability availability;
                    using (new Game.Core.ProfileScope("AI/PhaseA.DeliveryAvailability"))
                        availability = MaterializationCandidateBuilder.OperationalDeliveryAvailabilityForDemand(
                            pass.Snap, pass.Player, pass.Root, pass.Hand, pass.Ctx, d, pass.Commitments,
                            pass.Reservation, pass.ActiveIntents);
                    bool structural = availability.StructurallyUndeliverable;
                    d.StructurallyUndeliverable = structural;
                    if (structural && d.AttackFistIsPreparationHost && d.AttackFistArmyId.HasValue)
                        PreparationDeliveryMemory.MarkNoChain(pass.Player, d.AttackFistArmyId.Value,
                            pass.Turn, pass.Hand.Hand.Select(c => StrikeRoster.CardKey(c?.Definition)));
                }
                AiDebugLog.WriteDedupedWithId(d.TraceId, $"[AI][V2]   strat.A - {d}: no feasible useful chain "
                    + $"({DesireAxes.Abbrev(d.RequestingAxis)} entitlement {F(pass.ApBudget.Balance())}, "
                    + $"discrete {F(pass.ApBudget.DiscreteAdmissionBudget())}, "
                    + $"followup reserved {F(reserved)}); {diag}");

                // §17 — an unfulfilled Aggression/Recon capability demand plus an empty
                // resource stock is a starvation signal for that resource (own state only).
                if (!d.AttackLocalRefit
                    && (d.RequestingAxis == DesireAxis.Aggression || d.RequestingAxis == DesireAxis.Recon))
                    foreach (ResourceType rt in ResourceBundle.All)
                        if (pass.Root.GetResource(rt) <= 0f)
                            ResourceStarvationRegistry.RecordBlock(pass.Player, rt);
            }
        }

        private static void ExecuteSelected(PhaseAPass pass, PhaseACandidateRound round, PhaseACandidate selected)
        {
            var armyIdsBefore = new HashSet<int>(pass.Snap.Self?.Armies?
                .Where(a => a != null).Select(a => a.ArmyId) ?? Enumerable.Empty<int>());
            int chainApBefore = pass.Root.ActionPoints;
            if (selected.Plan.Kind == MaterializationChainKind.GenerateAttachUpgrade)
                ExecuteDevelopmentUpgrade(pass, selected, chainApBefore);
            else
                ExecuteMaterialization(pass, round, selected, armyIdsBefore, chainApBefore);
        }

        private static void ExecuteDevelopmentUpgrade(PhaseAPass pass, PhaseACandidate selected, int chainApBefore)
        {
            StrategicPhaseResult result = pass.Result;
            AxisDemand chosenDemand = selected.State.Demand;
            MaterializationPlan plan = selected.Plan;
            DevUpgradeResult up = DevelopmentUpgradeFulfillment.TryFulfill(
                pass.Snap, pass.Player, pass.Root, pass.Hand, pass.Ctx, chosenDemand, plan, pass.ApBudget);
            int upgradeApAfter = pass.Root.ActionPoints;
            pass.ChainAttempts++;
            result.MaterializationAttempts++;

            if (up.ApSpent > 0f)
                AiV2Trace.CheckPhaseAAp(chosenDemand.TraceId, chosenDemand.RequestingAxis,
                    chainApBefore - upgradeApAfter, up.ApSpent);

            if (up.Executed)
                result.RecordGenerationAttempt(pass.Player, pass.Turn, plan.Generation,
                    new MaterializationResult
                    {
                        StateChanged = up.StateChanged,
                        GenerationAttempted = true,
                        Generated = up.ChallengeWon,
                        ApSpent = up.ApSpent,
                        AttemptedGenerationUseKey = plan.Generation?.UseKey,
                    }, up.ChallengeWon);
            // Executed or not, the upgrade demand is done for this pass: creation is complete and
            // the capability is delivered by the hand-card lane.
            selected.State.Blocked = true;

            if (up.StateChanged)
            {
                result.StateChanged = true;
                pass.RefreshKnowledge();
            }
            DevelopmentOutcomeTelemetry.RecordUpgrade(pass.Player, pass.Turn, up.Executed, up.ChallengeWon);
            AiDebugLog.Write($"[AI][V2][Dev] {(up.Executed ? "EXEC" : "SKIP")} - "
                + $"{plan.DevelopmentUpgrade?.Mode} '{plan.DevelopmentUpgrade?.Card?.displayName}' "
                + $"-> {plan.DevelopmentUpgrade?.RecipientLabel} "
                + $"p={F(plan.DevelopmentUpgrade?.SuccessChance ?? 0f)} "
                + $"gain={F(plan.DevelopmentUpgrade?.ExpectedGain ?? 0f)} "
                + $"policy={plan.DevelopmentUpgrade?.Explain} "
                + $":: {up.Detail} (ap {F(up.ApSpent)} -> DEV)");
        }

        private static void ExecuteMaterialization(PhaseAPass pass, PhaseACandidateRound round,
            PhaseACandidate selected, HashSet<int> armyIdsBefore, int chainApBefore)
        {
            StrategicPhaseResult result = pass.Result;
            AxisDemand chosenDemand = selected.State.Demand;
            MaterializationPlan plan = selected.Plan;
            WorldSnapshot snapBeforeDelivery = pass.Snap;
            MaterializationResult play;
            using (new Game.Core.ProfileScope("AI/PhaseA.Execute"))
                play = MaterializationExecutor.Execute(
                    pass.Snap, pass.Player, pass.Root, pass.Hand, pass.Ctx, plan, pass.Commitments,
                    chosenDemand.SpendAuthority);
            int chainApAfter = pass.Root.ActionPoints;
            pass.ChainAttempts++;

            // Production telemetry (spec §12) — attempts/successes per chain stage. Derived
            // from the plan shape + MaterializationResult; no scoring change.
            result.RecordMaterialization(pass.Player, pass.Turn, plan, play);

            // §2.3 — the physical AP drop must match the chain's reported ApSpent.
            AiV2Trace.CheckPhaseAAp(chosenDemand.TraceId, chosenDemand.RequestingAxis,
                chainApBefore - chainApAfter, play.ApSpent);

            if (!play.Deployed)
            {
                if (play.CardDeployed) result.CardsPlayed++;
                AiDebugLog.Write($"[AI][V2]   strat.A - {chosenDemand}: {plan.Kind} {AiCardLog.Plan(plan)} "
                    + $"chain did not deploy ({play.FailReason}); gen={(play.Generated ? 1 : 0)} "
                    + $"att={(play.Attached ? 1 : 0)}");
                // ARCH-02 §35 — a stale-placement failure means "replan me", not "block me":
                // refresh so the next TopForDemand enumerates against the current world, and
                // leave the demand active. The loop is still bounded by ChainAttempts.
                if (play.StateChanged || play.PlacementStale)
                    pass.RefreshKnowledge();
                if (!play.StateChanged && !play.PlacementStale && plan.Generation == null)
                    selected.State.Blocked = true;
                return;
            }

            pass.RefreshKnowledge();
            CapabilityInventory afterInv = CapabilityInventory.Build(pass.Snap, pass.Player, pass.Commitments);
            bool operationallyDelivered = CapabilityDeliveryEvaluator.FinalizeOperationalDelivery(pass.Player,
                pass.Ctx, pass.Snap, plan, chosenDemand, round.Inventory, afterInv, armyIdsBefore,
                out float delivered, beforeSnap: snapBeforeDelivery);

            if (operationallyDelivered
                && (MaterializationDeliveryPolicy.IsEconomyHeroDemand(chosenDemand)
                    || chosenDemand.EconomyEscortArmyId.HasValue)
                && chosenDemand.EconomyPreferredBuilderArmyId.HasValue)
                pass.Commitments?.Claim(chosenDemand.EconomyPreferredBuilderArmyId.Value);

            if (operationallyDelivered)
            {
                if (chosenDemand.AttackLocalRefit)
                {
                    var primary = OwnWorld.Of(pass.Player).Army(plan.AttackRefitPrimaryId.Value);
                    var attack = AttackBaseRefitPolicy.Resolve(
                        pass.IntentState?.All, pass.Player, primary.Id, primary.Hex, pass.Turn);
                    pass.ApBudget.ReserveActorFollowup(primary.Id,
                        AttackBaseRefitPolicy.FollowupAp(pass.Player, primary, attack, primary.Members, pass.Ctx, pass.Root));
                }
                else
                    pass.ApBudget.ReserveFollowup(selected.FollowupAp);
                selected.State.Remaining = Mathf.Max(0f, selected.State.Remaining - delivered);
                result.CapabilityDeliveries++;
            }
            else
            {
                selected.State.Blocked = true;
                AiDebugLog.Write($"[AI][V2]   strat.A - {chosenDemand}: deployment changed state but delivered "
                    + $"0 operational {chosenDemand.Capability}; reserve/potential only, residual unchanged");
            }
            result.CardsPlayed++;

            AiDebugLog.Write($"[AI][V2]   strat.A - {chosenDemand}: {plan.Kind} {AiCardLog.Plan(plan)} "
                + $"@{plan.Deploy.Hex.Q},{plan.Deploy.Hex.R} "
                + $"(ap {F(play.ApSpent)} -> {DesireAxes.Abbrev(chosenDemand.RequestingAxis)}, {plan.Deploy.Kind}, "
                + $"delivered {F(delivered)}, followup {(operationallyDelivered ? F(selected.FollowupAp) : "0")}ap reserved"
                + $", {plan.StableKey})");
        }

        // Strategy owns the admission policy, not Orchestration or the executor.
        // Zero is a residual PRIORITY for new work, never a cancellation of an active intent.
        internal static bool ShouldDeferFreshZeroRadarDemand(Radar radar, AxisDemand demand,
            IReadOnlyList<MissionIntent> activeIntents)
        {
            if (demand == null || RadarValueScale.For(radar ?? Radar.Even(),
                    demand.RequestingAxis) > 0f)
                return false;
            if (demand.ConsumerIntentKey.HasValue && activeIntents != null
                && activeIntents.Any(i => i != null && i.Status == IntentStatus.Active
                    && i.IntentKey.Equals(demand.ConsumerIntentKey.Value)))
                return false;
            return demand.RequestingAxis != DesireAxis.Economy
                || !IsCommittedEconomyBuild(activeIntents, demand);
        }

        internal static bool IsCommittedEconomyBuild(
            IReadOnlyList<MissionIntent> activeIntents, AxisDemand demand)
        {
            return EconomyContinuity.HasEconomyBuildCommitment(activeIntents, demand);
        }

        // Owner decision 2026-10-18 (revises C3 2026-09-24): a pending Hero/escort prerequisite
        // keeps its build's pre-intent H/E/M/T hold only while the current hand/world can still
        // deliver it — a shortfall of AP/resources (timing) keeps it, a structural block drops it
        // for this pass. Holding while nothing can deliver is self-defeating: the frozen resources
        // stop exactly the plays that free a hand slot and draw the missing Hero/escort
        // (playtest 2026-10-18, Halden T8-T9: 3 Energy held, hand 10/10, nothing played or drawn).
        // Re-evaluated every pass, so the hold returns the pass a deliverable card appears.
        internal static bool PendingPrerequisiteHoldIsLive(AxisDemand demand,
            MaterializationDeliveryAvailability availability) =>
            !HoldsForPendingPrerequisite(demand) || !availability.StructurallyUndeliverable;

        // Infrastructure that is a plain card play (no builder walk, no Economy hold) and so ranks
        // against the best materialization chain instead of running ahead of it.
        internal static bool CompetesInCardArbitration(AxisDemand demand) =>
            demand != null && InfrastructureFulfillment.Handles(demand.Capability)
            && (demand.RequestingAxis == DesireAxis.Development
                || demand.Capability == CapabilityKind.GlobalResourceFacility);

        // The same rule that makes ShouldReserveDeferredEconomyResources hold for a demand with
        // no ready builder/route: an accepted build's Hero/escort prerequisite.
        internal static bool HoldsForPendingPrerequisite(AxisDemand demand) =>
            demand != null && !demand.IsEconomyNewHeroAlternative
            && InfrastructureFulfillment.EconomyHeroPrerequisiteOwner(demand) != null;

        // A Base project's pending prerequisite whose confirmed delivery block counts toward the
        // per-(card, site) delivery-failure streak, so a site that keeps failing is reconsidered
        // (Demand picks another). Both prerequisite shapes count — a builder Hero or a pinned
        // builder escort (playtest 2026-10-18: only the Hero shape counted).
        internal static bool IsBoundedBasePrerequisiteDemand(AxisDemand demand) =>
            HoldsForPendingPrerequisite(demand)
            && demand.EconomyBuildCard?.Definition?.cardType == CardType.Base;

        private static string F(float v) => v.ToString("0.##", CultureInfo.InvariantCulture);
    }
}
