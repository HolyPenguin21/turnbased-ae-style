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
    // ===================================================================================
    //  END-OF-TURN TEMPO ARBITER  (AI-MGR-02)
    // ===================================================================================
    //  The SINGLE late-turn spend entry. Every end-of-turn decision — PlayCard (materialization
    //  OR non-combat, scored ONLY by StrategicCardEvaluator), DrawCard, an existing strategic
    //  spend (maintenance / decisive structure pressure), HoldResources, EndTurn — is a
    //  candidate in ONE comparable utility space. Each iteration rebuilds live world / hand /
    //  resources / reservations, rebuilds every candidate, executes exactly ONE, inspects the
    //  REAL result, and rebuilds again. It stops when max(Hold, EndTurn) >= the best actionable
    //  spend, or the hard action bound is hit. There is no fixed lane order and no bypass:
    //  a failed / no-op candidate is parked for the current state version and cannot be
    //  re-chosen until a real state mutation invalidates the whole candidate set (spec §2/§3).
    //
    //  ARCH-02 §8 — this class owns only the arbiter LOOP. Candidate construction + structural
    //  admission is TempoCandidateProvider; per-action execution is TempoActionExecutor; the
    //  persistent-resource retention policy is HoldEvaluator; spendability is StrategicSpendability.
    //  Body is unchanged from the former StrategicManager.UseSurplus.
    //
    //  §5 single-count: a PlayCard candidate's utility is the StrategicCardEvaluator NetScore
    //  VERBATIM — the arbiter never re-adds hand pressure / resource pressure / hold. Those
    //  factors are recomputed here ONLY for the arbiter-owned candidates (Draw / Hold / spend).
    public static class StrategicPhaseB
    {
        public static System.Collections.IEnumerator UseSurplus(WorldSnapshot snap, PlayerSetupData player,
            PlayerRoot root, AiHandData hand, AiTurnContext ctx, ActorCommitments commitments,
            MaterializationReservation carriedReservation, StrategicPhaseResult result,
            IReadOnlyList<ReconObjective> reconObjectives = null)
        {
            result.Reservation = carriedReservation ?? new MaterializationReservation();
            if (player == null || root == null || hand == null || ctx == null)
                yield break;

            // AI-MGR-02 §P0.4 — ONE turn-scoped budget for every tempo action. Every hard cap
            // (total actions / surplus card plays / draws / generation attempts) is enforced
            // against this, so re-entering the arbiter (main Phase B, reaction round, reaction
            // follow-up, Housekeeping tempo re-run) cannot buy more than the per-turn limit. Keep
            // MGR-01's internal generation counter in sync with the shared budget.
            StrategicTempoBudget budget = StrategicTempoBudget.For(player, ctx.TurnNumber);
            result.Reservation.GenerationAttemptsUsed =
                Mathf.Max(result.Reservation.GenerationAttemptsUsed, budget.GenerationAttemptsUsed);

            // --- §7 (round 5) reaction reservation: a BOUNDED AP BUDGET + the persistent H/E/M/T
            //     ENVELOPE that a REAL feasibility probe proved is needed to keep at least one
            //     feasible reaction possible. The budget stays generic (the replan picks its own
            //     action) but is only created when the probe passes and the AP >= min feasible AP.
            StrategicReactionOpportunity reactionOpp =
                StrategicReactionPass.BuildReactionOpportunity(player, root, ctx, snap);
            if (reactionOpp.IsActionable)
            {
                StrategicResourceReservationLedger.Upsert(player, ctx.TurnNumber,
                    new StrategicResourceReservation
                    {
                        Owner = reactionOpp.OwnerKey,
                        Reason = StrategicReservationReason.StrategicReactionPass,
                        Resource = StrategicReservedResource.ActionPoints,
                        Amount = reactionOpp.ReservedApBudget,
                        ExpirationStage = StrategicReservationExpiry.EndOfReaction,
                    });
                if (reactionOpp.Envelope != null)
                    foreach (ResourceType rt in ResourceBundle.All)
                    {
                        int n = reactionOpp.Envelope.Get(rt);
                        if (n <= 0) continue;
                        StrategicResourceReservationLedger.Upsert(player, ctx.TurnNumber,
                            new StrategicResourceReservation
                            {
                                Owner = reactionOpp.OwnerKey,
                                Reason = StrategicReservationReason.StrategicReactionPass,
                                Resource = StrategicResourceReservationLedger.Map(rt),
                                Amount = n,
                                ExpirationStage = StrategicReservationExpiry.EndOfReaction,
                            });
                    }
                AiDebugLog.Write($"[AI][V2]   strat.B — reaction feasible ({reactionOpp.Kind}); reserve BOUNDED "
                    + $"{F(reactionOpp.ReservedApBudget)} AP"
                    + (reactionOpp.Envelope != null ? $" + envelope [{ResCostStr(reactionOpp.Envelope)}]" : "")
                    + $" (owner={reactionOpp.OwnerKey} exp=EndOfReaction; {reactionOpp.Rationale}), spendable AP now "
                    + $"{F(StrategicResourceReservationLedger.SpendableAp(player, ctx.TurnNumber, root.ActionPoints))}.");
            }
            else
            {
                // spec §7 — an existing reaction budget reservation is released the moment no
                // feasible same-turn reaction remains (same-turn re-arbitration is Housekeeping's re-run).
                StrategicResourceReservationLedger.ReleaseByReason(player, ctx.TurnNumber,
                    StrategicReservationReason.StrategicReactionPass);
                if (StrategicInterruptRegistry.HasPendingDiscovery(player, ctx.TurnNumber))
                    AiDebugLog.Write($"[AI][V2]   strat.B — pending invalidation but no FEASIBLE reaction "
                        + $"({reactionOpp.FailReason}); NOT reserving — tempo uses the full pool (spec §7)");
            }

            AiDebugLog.Write($"[AI][V2]   strat.B — {player.Nickname} hand {AiCardLog.Hand(hand)}");

            AiDebugLog.Write($"[AI][V2]   strat.B/tempo — budget on entry: total {budget.TotalTempoActionsUsed}/"
                + $"{AiConfigV2.maxEndOfTurnTempoActionsPerTurn}, cards {budget.SurplusCardActionsUsed}/"
                + $"{AiConfigV2.maxSurplusActionsPerTurn}, draws {budget.DrawActionsUsed}/"
                + $"{AiConfigV2.maxTerminalDrawsPerTurn}, gen {budget.GenerationAttemptsUsed}/{AiConfigV2.maxGenerationActionsPerTurn}");

            // §P1.8 — parking is keyed by (ActionKey, StateVersion). A parked candidate stays
            // parked only while StateVersion is unchanged; any real mutation bumps the version and
            // every park goes stale (== the whole candidate set is rebuilt, spec §2/§3).
            var parkedAt = new Dictionary<string, int>(System.StringComparer.Ordinal);
            int stateVersion = 0;
            int iter = 0;
            string stopReason = null;
            while (!budget.TotalCapHit && iter <= AiConfigV2.maxEndOfTurnTempoActionsPerTurn + 1)
            {
                snap = WorldAnalysis.RefreshOperationalState(snap, player, root, hand, ctx);
                float spendableAp = StrategicResourceReservationLedger.SpendableAp(
                    player, ctx.TurnNumber, root.ActionPoints);

                var cands = TempoCandidateProvider.BuildTempoCandidates(snap, player, root, hand, ctx, commitments, result,
                    reconObjectives, spendableAp, budget, verbose: iter == 0);

                float endU = cands.First(c => c.Kind == TempoKind.EndTurn).Utility;   // 0
                float holdPolicyFull = HoldEvaluator.HoldResourcesUtility(root, snap, null); // whole pool — diagnostic only

                LogTempoIterationHeader(ctx, root, snap, player, hand, budget, iter, spendableAp);

                // §P0 (round 4) — ONE comparable space, but HoldResources is NOT a global stop gate.
                //   · PlayCard (mat / non-combat): utility = StrategicCardEvaluator NetScore VERBATIM
                //     (the evaluator already owns HoldValue / ScarcityValue / ResourcePressureBenefit).
                //   · AP-only actions (Draw, AP-only Pressure): utility verbatim — keeping H/E/M/T is
                //     COMPATIBLE with spending AP, so the persistent-hold policy never blocks them.
                //   · Non-card spend (capacity upgrade): effective = utility − holdOfConsumed, i.e.
                //     the retention value of ONLY the persistent resources IT consumes.
                // A candidate is eligible when effective > max(EndTurn, tempoMinSpendUtility).
                TempoCandidate best = null;
                float bestEff = float.NegativeInfinity;
                foreach (TempoCandidate c in cands
                    .Where(c => TempoCandidateProvider.IsSpend(c.Kind))
                    .OrderByDescending(c => c.Utility)
                    .ThenBy(c => c.ActionKey, System.StringComparer.Ordinal))
                {
                    string block = TempoBlockReason(c, spendableAp, budget, parkedAt, stateVersion, player, root, ctx);
                    float holdOfConsumed = c.Kind == TempoKind.MaintenanceSpend && c.ResCost != null
                        ? HoldEvaluator.HoldResourcesUtility(root, snap, c.ResCost) : 0f;
                    float eff = c.Utility - holdOfConsumed;
                    AiDebugLog.Write($"[AI][V2]     cand {c.Kind} rawUtil {F(c.Utility)} holdOfConsumed {F(holdOfConsumed)}"
                        + $" eff {F(eff)} apCost {F(c.ApCost)} resCost [{ResCostStr(c.ResCost)}] key={c.ActionKey}"
                        + (block != null ? $" BLOCKED: {block}" : "")
                        + (c.DrawDiag != null ? $" {{{c.DrawDiag}}}" : "")
                        + $" — {c.Label}");
                    if (block == null && eff > bestEff)
                    {
                        best = c;
                        bestEff = eff;
                    }
                }

                AiDebugLog.Write($"[AI][V2]     policy Hold(full pool) {F(holdPolicyFull)} (diag only)  |  EndTurn {F(endU)}");

                float spendBar = Mathf.Max(AiConfigV2.tempoMinSpendUtility, endU);
                if (best == null)
                {
                    stopReason = "no eligible spend candidate " + BudgetSummary(budget);
                    break;
                }
                if (bestEff <= spendBar)
                {
                    stopReason = $"best spend {best.Kind} eff {F(bestEff)} <= max(minSpend {F(AiConfigV2.tempoMinSpendUtility)}, "
                        + $"endTurn {F(endU)}) = {F(spendBar)}";
                    break;
                }

                int ap0 = root.ActionPoints;
                int h0 = root.GetResource(Game.Economy.ResourceType.Human), e0 = root.GetResource(Game.Economy.ResourceType.Energy);
                int m0 = root.GetResource(Game.Economy.ResourceType.Materials), t0 = root.GetResource(Game.Economy.ResourceType.Tech);
                var exec = new TempoExecutionResult();
                switch (best.Kind)
                {
                    case TempoKind.PlayMat:
                        snap = TempoActionExecutor.ExecuteMatSurplus(best.Mat, snap, player, root, hand, ctx, commitments, result, ref exec);
                        break;
                    case TempoKind.PlayNonCombat:
                        snap = TempoActionExecutor.ExecuteNonCombatSurplus(best.Nc, snap, player, root, hand, ctx, result, ref exec);
                        break;
                    case TempoKind.Draw:
                        if (CardDrawExecutor.TryCycle(root, hand, ctx))
                        {
                            exec.Succeeded = exec.StateChanged = exec.Progressed = exec.Drawn = true;
                            result.CardsDrawn++;
                        }
                        else exec.FailReason = "TryCycle refused";
                        break;
                    case TempoKind.MaintenanceSpend:
                        // Execute EXACTLY the chosen candidate (no re-selection by the policy).
                        exec.Succeeded = best.Spend.Execute(player, root, ctx,
                            out bool msChanged, out bool msProgressed);
                        exec.StateChanged = msChanged;
                        exec.Progressed = msProgressed;
                        if (!exec.Succeeded) exec.FailReason = "capacity upgrade refused";
                        break;
                    case TempoKind.PressureSpend:
                    {
                        bool pc = false;
                        yield return StrategicPressureAdvance.Execute(player, root, ctx, best.Pressure, v => pc = v);
                        exec.Succeeded = exec.StateChanged = exec.Progressed = pc;
                        if (!pc) exec.FailReason = "no advance step taken";
                        break;
                    }
                }
                exec.ApSpent = Mathf.Max(0, ap0 - root.ActionPoints);
                exec.HumanSpent = Mathf.Max(0, h0 - root.GetResource(Game.Economy.ResourceType.Human));
                exec.EnergySpent = Mathf.Max(0, e0 - root.GetResource(Game.Economy.ResourceType.Energy));
                exec.MaterialsSpent = Mathf.Max(0, m0 - root.GetResource(Game.Economy.ResourceType.Materials));
                exec.TechSpent = Mathf.Max(0, t0 - root.GetResource(Game.Economy.ResourceType.Tech));

                iter++;
                // Debit the turn budget only for an action that actually EXECUTED (progressed or
                // mutated real state). A candidate that turned out to be a no-op is parked below,
                // not counted against the per-turn action limit. Generation attempts debit
                // GenerationAttemptsUsed directly from the execute paths.
                if (exec.Progressed || exec.StateChanged)
                    budget.RecordAction(
                        card: exec.Progressed && best.CountsAsSurplusCardPlay,
                        draw: exec.Progressed && best.CountsAsTerminalDraw,
                        generationAttempt: false);

                AiDebugLog.Write($"[AI][V2]   tempo[{iter - 1}] — WINNER {best.Kind} util {F(best.Utility)} eff {F(bestEff)} — {best.Label}"
                    + $"  => ok {(exec.Succeeded ? 1 : 0)} progressed {(exec.Progressed ? 1 : 0)} stateChanged {(exec.StateChanged ? 1 : 0)}"
                    + $" spent ap {exec.ApSpent} H/E/M/T {exec.HumanSpent}/{exec.EnergySpent}/{exec.MaterialsSpent}/{exec.TechSpent}"
                    + $" card {(exec.CardPlayed ? 1 : 0)} drawn {(exec.Drawn ? 1 : 0)} gen {(exec.Generated ? 1 : 0)} attach {(exec.Attached ? 1 : 0)}"
                    + (exec.FailReason != null ? $" fail={exec.FailReason}" : ""));

                if (exec.StateChanged || exec.Progressed)
                {
                    stateVersion++;
                    result.StateChanged |= exec.StateChanged;
                }
                if (!exec.Progressed)
                {
                    parkedAt[best.ActionKey] = stateVersion;
                    AiDebugLog.Write($"[AI][V2]   tempo — {best.Kind} did not complete; parked {best.ActionKey}@v{stateVersion}");
                }
                if (exec.Interrupt)
                {
                    stopReason = "Phase B delivered an operational residual — re-admit missions before more spending";
                    break;
                }
            }
            if (stopReason == null)
                stopReason = budget.TotalCapHit
                    ? $"turn tempo action budget {AiConfigV2.maxEndOfTurnTempoActionsPerTurn} reached"
                    : "local iteration guard";

            // §13 — the mandatory final line: it must be impossible to read "AP left, reservation
            // none, reason unknown" off the log.
            AiDebugLog.Write($"[AI][V2] strat.B/tempo — END: iters {iter}, turn budget total {budget.TotalTempoActionsUsed}/"
                + $"{AiConfigV2.maxEndOfTurnTempoActionsPerTurn} cards {budget.SurplusCardActionsUsed}/{AiConfigV2.maxSurplusActionsPerTurn}"
                + $" draws {budget.DrawActionsUsed}/{AiConfigV2.maxTerminalDrawsPerTurn} gen {budget.GenerationAttemptsUsed}/{AiConfigV2.maxGenerationActionsPerTurn}; "
                + $"cardsPlayed {result.CardsPlayed}, drawn {result.CardsDrawn}; ap {root.ActionPoints} "
                + $"(spendable {F(StrategicResourceReservationLedger.SpendableAp(player, ctx.TurnNumber, root.ActionPoints))}), "
                + $"H/E/M/T {root.GetResource(Game.Economy.ResourceType.Human)}/{root.GetResource(Game.Economy.ResourceType.Energy)}/"
                + $"{root.GetResource(Game.Economy.ResourceType.Materials)}/{root.GetResource(Game.Economy.ResourceType.Tech)}; "
                + $"reservations [{StrategicResourceReservationLedger.DebugLine(player, ctx.TurnNumber)}]; stop={stopReason}");
        }

        // ---- tempo diagnostics / helpers ----------------------------------------------------
        private static string BudgetSummary(StrategicTempoBudget b) =>
            $"(budget total {b.TotalTempoActionsUsed}/{AiConfigV2.maxEndOfTurnTempoActionsPerTurn}, "
            + $"cards {b.SurplusCardActionsUsed}/{AiConfigV2.maxSurplusActionsPerTurn}, "
            + $"draws {b.DrawActionsUsed}/{AiConfigV2.maxTerminalDrawsPerTurn}, "
            + $"gen {b.GenerationAttemptsUsed}/{AiConfigV2.maxGenerationActionsPerTurn})";

        private static string ResCostStr(ResourceCost c)
        {
            if (c == null) return "-";
            if (c.human == 0 && c.energy == 0 && c.materials == 0 && c.tech == 0) return "0";
            return $"H{c.human} E{c.energy} M{c.materials} T{c.tech}";
        }

        // Per-iteration mandatory diagnostic: AP, per-resource total/reserved/spendable/runway-target/
        // expected-income/strategic-overstock (NOT a physical overflow — the game has no storage cap),
        // hand, deck, and the shared turn budget.
        private static void LogTempoIterationHeader(AiTurnContext ctx, PlayerRoot root, WorldSnapshot snap,
            PlayerSetupData player, AiHandData hand, StrategicTempoBudget budget, int iter, float spendableAp)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append($"[AI][V2]   tempo[{iter}] T{ctx.TurnNumber} — ap {root.ActionPoints} spendable {F(spendableAp)}; ");
            float comfortable = Mathf.Max(1f, AiConfigV2.tempoHoldResourceComfortableStock);
            foreach (ResourceType rt in ResourceBundle.All)
            {
                int stock = root.GetResource(rt);
                float reserved = StrategicResourceReservationLedger.Active(
                    player, ctx.TurnNumber, StrategicResourceReservationLedger.Map(rt));
                float spendable = Mathf.Max(0f, stock - reserved);
                float incomeTarget = snap?.Economy?.IncomeTarget.Get(rt) ?? 0f;
                float nextIncome = snap?.Self != null ? snap.Self.PerTurnIncome.Get(rt) : 0f;
                float runwayTarget = Mathf.Max(comfortable, incomeTarget * AiConfigV2.tempoHoldOverstockRunwayHorizon);
                float overstock = Mathf.Max(0f, (stock + nextIncome) - runwayTarget);
                sb.Append($"{rt.ToString()[0]} {stock}(rsv {F(reserved)} sp {F(spendable)} runway {F(runwayTarget)} inc {F(nextIncome)} overstock {F(overstock)}) ");
            }
            sb.Append($"| hand {hand.Hand.Count}/{ctx.HandCapacity} deck {hand.RemainingDeckCount} ");
            sb.Append(BudgetSummary(budget));
            AiDebugLog.Write(sb.ToString());
        }

        // null => the candidate may be chosen; otherwise a short reason it is currently blocked.
        // Every cap is checked against the turn budget (spec §P0.4), not a per-call local.
        private static string TempoBlockReason(TempoCandidate c, float spendableAp, StrategicTempoBudget budget,
            Dictionary<string, int> parkedAt, int stateVersion, PlayerSetupData player, PlayerRoot root, AiTurnContext ctx)
        {
            if (parkedAt.TryGetValue(c.ActionKey, out int v) && v == stateVersion)
                return $"parked@v{v}";
            if (c.CountsAsSurplusCardPlay && budget.CardCapHit) return "surplus card-play budget";
            if (c.CountsAsTerminalDraw && budget.DrawCapHit) return "draw budget";
            if (c.ConsumesGeneration && budget.GenerationCapHit) return "generation budget";
            if (c.ApCost > spendableAp + AiConfigV2.allocatorSliceEpsilon)
                return $"spendable AP ({F(c.ApCost)} > {F(spendableAp)})";
            if (!StrategicSpendability.FitsSpendableResources(player, root, ctx, c.ResCost))
                return "spendable resources";
            return null;
        }

        private static string F(float v) => v.ToString("0.##", CultureInfo.InvariantCulture);
    }
}
