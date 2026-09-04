using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Game.Ai;
using Game.Cards;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using UnityEngine;

namespace Game.Ai.V2
{
    // Bounded same-turn replanning. Round 0 consumes the ordinary strategic invalidation. A single
    // round 1 is permitted only when round 0 itself materializes new operational capability or a
    // terminal draw exposes an actionable hand. New contact discovery never recursively chains.
    public sealed class StrategicReactionResult
    {
        public bool Ran;
        public bool StateChanged;
        public int DiscoveredTargets;
        public int Demands;
        public int Missions;
        public int Provisioned;
        public int Executed;          // real attempts (superseded stale missions excluded)
        public int CardsPlayed;
        public int CardsDrawn;
        public int Rounds;
    }

    // AI-MGR-02 §7 (round 5) — a BOUNDED REACTION BUDGET backed by a REAL feasibility probe. The
    // bounded reaction round re-runs the whole Demand→Mission→Provision→Execute pipeline and picks
    // its own action, so the budget stays GENERIC (not bound to one exact actor/card). But it is
    // only created when the SAME gates the real pipeline uses prove at least one feasible reaction
    // exists, the reserved AP is >= that reaction's minimum feasible AP, and the persistent
    // H/E/M/T envelope needed to keep it feasible is reserved alongside the AP.
    internal readonly struct StrategicReactionOpportunity
    {
        public readonly bool IsActionable;
        public readonly string OwnerKey;         // reservation owner tag ("reaction-budget:<kind>")
        public readonly string Kind;             // "RespondToDiscovery" | "HandFollowup"
        public readonly float ReservedApBudget;  // BOUNDED replan budget (<= reactionReserveApCap), >= MinFeasibleAp
        public readonly ResourceCost Envelope;   // persistent H/E/M/T that must stay unspent (may be null)
        public readonly string Rationale;
        public readonly string FailReason;

        public StrategicReactionOpportunity(bool actionable, string ownerKey, string kind,
            float reservedApBudget, ResourceCost envelope, string rationale, string failReason)
        {
            IsActionable = actionable;
            OwnerKey = ownerKey;
            Kind = kind;
            ReservedApBudget = reservedApBudget;
            Envelope = envelope;
            Rationale = rationale;
            FailReason = failReason;
        }

        public static StrategicReactionOpportunity None(string failReason) =>
            new StrategicReactionOpportunity(false, null, null, 0f, null, null, failReason);
    }

    // Round 6/round 7 (P0.1/P0.2) — ONE feasible reaction the real pipeline would actually admit
    // at the current state. BuildReactionOpportunity collects EVERY witness from EVERY enabled
    // source (discovery-direct responder, discovery-materialization, hand follow-up) — never a
    // fixed `targetDriven ? A : B` branch — and reserves a bounded budget for the single cheapest
    // one. Execution is NOT bound to the witness (the reservation stays a generic replan budget) —
    // it only proves the budget protects something real.
    internal readonly struct ReactionWitness
    {
        public readonly string Kind;             // "RespondToDiscovery" | "MaterializeForDiscovery" | "HandFollowup"
        public readonly string ActionKey;        // stable deterministic tie-break key
        // round 10 (P0.1) — the ONE full AP the protected reaction actually needs, downstream/move
        // envelope INCLUDED (direct: activation + responder-move; materialization: prep + downstream;
        // hand: play AP + follow-up floor). Arbitration ranks and gates on this exact number and
        // NEVER reserves a budget below it (a clamped-below reservation does not protect the action).
        public readonly float RequiredAp;
        public readonly ResourceCost Envelope;   // persistent cost of that action (may be null)
        public readonly int WitnessActorId;      // responder army id (-1 for a hand / materialization play)
        public readonly int WitnessTargetId;     // discovered target army id (-1 for a pure hand play)
        public readonly string Detail;

        public ReactionWitness(string kind, string actionKey, float requiredAp,
            ResourceCost envelope, string detail, int witnessActorId = -1, int witnessTargetId = -1)
        {
            Kind = kind;
            ActionKey = actionKey;
            RequiredAp = requiredAp;
            Envelope = envelope;
            WitnessActorId = witnessActorId;
            WitnessTargetId = witnessTargetId;
            Detail = detail;
        }

        // Reservation owner tag — concrete per witness kind, NOT the shared Reason. P1: the
        // envelope-spendable check and the §6 re-probe exclude by THIS exact owner.
        public string OwnerKey => "reaction-budget:" + Kind;

        public float EnvelopeCost => Envelope == null ? 0f
            : Envelope.human + Envelope.energy + Envelope.materials + Envelope.tech;
    }

    internal static class StrategicReactionPass
    {
        // AI-MGR-02 §7 — CAN the pass run at all in this scope / with a resolvable hand.
        internal static bool CanStrategicReactionPassRun(PlayerSetupData player, AiTurnContext ctx)
        {
            if (player == null || ctx == null || ctx.Map == null)
                return false;
            // ExecuteIfPending consumes-and-suppresses the whole pass in ReconOnly scope.
            return !AiStrategyV2Scope.IsReconOnly;
        }

        // AI-MGR-02 §7 (round 5) — reserve a bounded reaction budget ONLY when a real feasibility
        // probe proves at least one genuinely feasible reaction exists, and only for an AP budget
        // >= its minimum feasible AP, plus its persistent-resource envelope. `snap` is the world
        // the probe runs against (the same one the caller will arbitrate / replan with).
        internal static StrategicReactionOpportunity BuildReactionOpportunity(PlayerSetupData player,
            PlayerRoot root, AiTurnContext ctx, WorldSnapshot snap)
        {
            if (!CanStrategicReactionPassRun(player, ctx))
                return StrategicReactionOpportunity.None("cannotRun(scope)");
            if (!StrategicInterruptRegistry.HasPending(player, ctx.TurnNumber))
                return StrategicReactionOpportunity.None("noPendingInvalidation");
            if (snap == null)
                return StrategicReactionOpportunity.None("noProbeSnapshot");

            AiHandData hand = AiHandRegistry.Peek(player);
            if (hand == null)
                StrategicInterruptRegistry.TryGetHand(player, ctx.TurnNumber, out hand);
            if (hand == null)
                return StrategicReactionOpportunity.None("noResolvableHand");

            bool targetDriven = StrategicInterruptRegistry.TargetIds(player, ctx.TurnNumber).Count > 0;
            bool followupDriven = StrategicInterruptRegistry.HasPendingFollowup(player, ctx.TurnNumber);
            if (!targetDriven && !followupDriven)
                return StrategicReactionOpportunity.None("noActionableContent");

            float apAvailable = root != null ? Mathf.Max(0f, root.ActionPoints) : 0f;
            int cap = AiConfigV2.reactionReserveApCap;
            float ceiling = Mathf.Min(apAvailable, (float)cap);

            // round 6 architectural-debt fix — the canonical normalized commitment source, not a
            // hand-rolled PreferredMoverArmyId scrape.
            ActorCommitments commitments = ActorCommitments.FromIntents(
                MissionIntentRegistry.GetOrCreate(player).All, snap, ReconObjectiveEvaluator.Enumerate(snap));

            // P0.1 — StrategicInterruptRegistry stores reasons as FLAGS, so a Discovery and a
            // HandOpportunity can both be pending at once. Probe every ENABLED source and let the
            // cheapest genuine witness win — no fixed Discovery>Hand (or Hand>Discovery) priority.
            // P0.2 — a discovered target whose aggression gate is not (yet) passed is still a real
            // reaction when the DemandLayer shortage it raises has a legal Phase-A materialization;
            // that is exactly what Phase A exists for (Raid accepted -> GatePassed=false because
            // power is short now -> DemandLayer asks for power -> Phase A plays a Unit -> Refresh
            // -> Raid executable). ProbeMaterializationForDiscovery composes the real primitives
            // (AggressionObjectiveEvaluator + RaidOperationalReadiness + the surplus materialization
            // enumerator), not a second planner.
            var witnesses = new List<ReactionWitness>();
            if (targetDriven)
            {
                // ONE canonical aggression evaluation, shared by both discovery probes (and the same
                // primitive DemandLayer.AggressionDemands uses). Stateless: CombatOpportunityAnalyzer
                // .Analyze does not mutate the radar; AggressionDemandEvaluator.Build only reads.
                var activeIntents = MissionIntentRegistry.GetOrCreate(player).All
                    .Where(i => i != null && i.Status == IntentStatus.Active).ToList();
                AggressionDemandEvaluation aggEval = AggressionDemandEvaluator.Build(snap,
                    AggressionObjectiveEvaluator.Enumerate(snap, CombatOpportunityAnalyzer.Analyze(snap)),
                    activeIntents, commitments, player);
                witnesses.AddRange(ProbeTargetDriven(player, ctx, aggEval));
                witnesses.AddRange(ProbeMaterializationForDiscovery(
                    player, root, ctx, snap, hand, commitments, aggEval));
            }
            if (followupDriven)
                witnesses.AddRange(ProbeHandFollowup(player, root, ctx, snap, hand, commitments));

            // round 10 (P0.1) — rank and gate on the witness's FULL RequiredAp (downstream/move
            // envelope already folded in). A witness whose RequiredAp exceeds the ceiling is dropped
            // outright — never clamped down and then treated as "protected". P1 — the envelope-
            // spendable check excludes THIS witness's own prospective reservation OWNER (not merely
            // its shared Reason), so the §6 re-probe of an already-placed budget does not fail
            // against itself and two distinct reaction owners cannot shadow each other.
            var feasible = witnesses
                .Where(w => w.RequiredAp <= ceiling + 0.001f)
                .Where(w => w.Envelope == null
                    || StrategicManager.FitsSpendableResources(player, root, ctx, w.Envelope, w.OwnerKey))
                .OrderBy(w => w.RequiredAp)
                .ThenBy(w => w.EnvelopeCost)
                .ThenBy(w => w.ActionKey, System.StringComparer.Ordinal)
                .ToList();
            if (feasible.Count == 0)
                return StrategicReactionOpportunity.None(witnesses.Count == 0
                    ? "noFeasibleReaction(no witness from any enabled source)"
                    : $"noFeasibleReaction({witnesses.Count} witness(es), none whose full RequiredAp fits "
                        + $"the ceiling {ceiling:0.#} + spendable envelope)");

            ReactionWitness win = feasible[0];
            float budget = win.RequiredAp; // reserve EXACTLY what the protected reaction needs (<= ceiling)
            string rationale = $"{win.Detail}; reserve {budget:0.#} AP (full RequiredAp)";
            if (feasible.Count > 1)
                rationale += $"; chosen over {feasible.Count - 1} other feasible witness(es) by (RequiredAp, envelope cost, key)";
            return new StrategicReactionOpportunity(true, win.OwnerKey, win.Kind,
                budget, win.Envelope, rationale, null);
        }

        // round 9 (P0.1) — a DIRECT-responder reaction witness is built ONLY from a discovered target
        // whose canonical RaidOperationalReadiness is ReadyExecutable right now (no GatePassed
        // filter — GatePassed is a frozen strategic projection, not the live admission gate). The AP
        // envelope is the ready RaidAssemblyPlan's own actor (ReadyPlan.BaseArmyId), NOT the cheapest
        // arbitrary pathable army — the cheapest pathable army may not be the one that clears
        // RaidAssemblyPlanner, which under-reserved the budget.
        private static List<ReactionWitness> ProbeTargetDriven(PlayerSetupData player, AiTurnContext ctx,
            AggressionDemandEvaluation eval)
        {
            var witnesses = new List<ReactionWitness>();
            HashSet<int> targetIds = StrategicInterruptRegistry.TargetIds(player, ctx.TurnNumber);

            foreach ((AggressionObjective obj, RaidAssemblyPlan plan) in eval.ReadyExecutable)
            {
                if (obj == null || plan == null || !targetIds.Contains(obj.TargetArmyId))
                    continue;
                ArmyData actor = ArmyRegistry.AllForOwner(player)
                    .FirstOrDefault(a => a != null && a.Id == plan.BaseArmyId);
                float activation = actor == null || actor.HasActivatedThisTurn ? 0f : actor.ActivationApCost;
                // P0.1 — RequiredAp is the FULL cost of the protected reaction: the ready actor's
                // activation PLUS the downstream move envelope. Arbitration reserves this exact
                // number or drops the witness — never a clamped-below "budget".
                float requiredAp = activation + Mathf.Max(0f, AiConfigV2.reactionResponderMoveApEstimate);
                witnesses.Add(new ReactionWitness("RespondToDiscovery",
                    $"discovery:direct:{plan.BaseArmyId}->{obj.TargetArmyId}", requiredAp, null,
                    $"targetDriven witness: canonical ready raid actor #{plan.BaseArmyId} -> discovered "
                    + $"target #{obj.TargetArmyId} (win {plan.ProjectedWinChance:0.00}, cover "
                    + $"{(plan.CoversAllDefenders ? 1 : 0)}); activation {activation:0.#} + move "
                    + $"{Mathf.Max(0f, AiConfigV2.reactionResponderMoveApEstimate):0.#} = {requiredAp:0.#} AP",
                    plan.BaseArmyId, obj.TargetArmyId));
            }
            if (witnesses.Count == 0)
                AiDebugLog.Write("[AI][V2] reaction probe(targetDriven) — no discovered target is "
                    + "ReadyExecutable per the canonical RaidOperationalReadiness");
            return witnesses;
        }

        // P1 (round 10) — the DFS DEPTH bound has ONE source of truth: AiConfigV2
        // .maxDemandFulfillmentActionsPerTurn, the exact per-call action limit the real reaction
        // Phase A (StrategicManager.FulfillDemands) runs under. `reactionMatPoolCap` is a pure
        // DoS safety valve on candidate WIDTH — at realistic hand sizes it never truncates; if it
        // ever bit, the result is a false-negative (no reservation), never a phantom.
        private const int reactionMatPoolCap = 24;

        // A discovered target that has a canonical Hero / FieldCombatPower shortage (from the shared
        // AggressionDemandEvaluator — SAME primitive DemandLayer.AggressionDemands uses, no mirrored
        // admission rules) is a real reaction ONLY if ProjectMaterializationClosure proves a BOUNDED
        // combination of legal materialization actions FULLY closes that shortage (round 8/9 P0),
        // never counting one physical card twice (round 9 P0.2) and measuring contribution as the
        // projected RaidAvailableFieldPower delta, not raw Σ BasePower (round 9 P0.3).
        private static List<ReactionWitness> ProbeMaterializationForDiscovery(PlayerSetupData player,
            PlayerRoot root, AiTurnContext ctx, WorldSnapshot snap, AiHandData hand,
            ActorCommitments commitments, AggressionDemandEvaluation eval)
        {
            var witnesses = new List<ReactionWitness>();
            HashSet<int> targetIds = StrategicInterruptRegistry.TargetIds(player, ctx.TurnNumber);

            if (eval.Outcome != AggressionDemandOutcome.Demand || eval.ChosenObjective == null
                || eval.Readiness == null)
            {
                AiDebugLog.Write($"[AI][V2] reaction probe(materializeForDiscovery) — canonical aggression demand "
                    + $"outcome={eval.Outcome} ({eval.Reason}); no materialization reaction");
                return witnesses;
            }
            if (!targetIds.Contains(eval.ChosenObjective.TargetArmyId))
            {
                AiDebugLog.Write($"[AI][V2] reaction probe(materializeForDiscovery) — canonical demand targets raid "
                    + $"#{eval.ChosenObjective.TargetArmyId}, not among this discovery "
                    + $"[{string.Join(",", targetIds.OrderBy(x => x))}]");
                return witnesses;
            }

            MaterializationClosure closure = ProjectMaterializationClosure(
                player, root, ctx, snap, hand, commitments, eval.ChosenObjective, eval.Readiness);
            if (closure == null)
            {
                AiDebugLog.Write("[AI][V2] reaction probe(materializeForDiscovery) — no bounded legal "
                    + "materialization combination closes the canonical shortage within the reaction budget");
                return witnesses;
            }

            witnesses.Add(new ReactionWitness("MaterializeForDiscovery",
                $"discovery:materialize:{eval.ChosenObjective.TargetArmyId}:{closure.Key}",
                closure.TotalAp, closure.Envelope, closure.Detail,
                -1, eval.ChosenObjective.TargetArmyId));
            return witnesses;
        }

        // P0 — the whole shortage must close, not just improve. A bounded DFS over legal
        // materialization candidates that operationally deliver the needed capability; accepts the
        // subset with the smallest total prep AP that drives Σ projected FieldCombatPower ≥
        // NumericPowerDeficit (and/or lands ≥ 1 hero) while staying inside every real bound.
        private sealed class MaterializationClosure
        {
            public float TotalAp;          // Σ prep AP + bounded downstream AP reserve
            public ResourceCost Envelope;  // Σ prep H/E/M/T (may be null)
            public string Key;             // deterministic subset key
            public string Detail;
        }

        private static MaterializationClosure ProjectMaterializationClosure(PlayerSetupData player,
            PlayerRoot root, AiTurnContext ctx, WorldSnapshot snap, AiHandData hand,
            ActorCommitments commitments, AggressionObjective objective, RaidOperationalReadiness readiness)
        {
            bool needPower = readiness.NeedsPower;
            bool needHero = readiness.NeedsHero;
            if (!needPower && !needHero)
                return null;

            float apAvail = root != null ? Mathf.Max(0f, root.ActionPoints) : 0f;
            float apCeiling = Mathf.Min(apAvail, (float)AiConfigV2.reactionReserveApCap);
            float downstreamAp = Mathf.Max(0f, AiConfigV2.reactionResponderMoveApEstimate);
            float prepCeiling = apCeiling - downstreamAp;
            if (prepCeiling < -0.001f)
                return null;

            // P1 — reaction preparation IS Phase A; the DFS depth bound is EXACTLY the Phase-A
            // per-call action limit the real reaction FulfillDemands runs under (single source of
            // truth), NOT the end-of-turn tempo caps (which FulfillDemands does not debit).
            // Generation stays bounded by the turn-scoped generation budget, which Phase A DOES
            // debit.
            StrategicTempoBudget budget = StrategicTempoBudget.For(player, ctx.TurnNumber);
            int maxActions = AiConfigV2.maxDemandFulfillmentActionsPerTurn;
            if (maxActions <= 0)
                return null;
            int genRemaining = AiConfigV2.maxGenerationActionsPerTurn - budget.GenerationAttemptsUsed;
            int handSlotBudget = hand != null ? Mathf.Max(0, hand.Capacity - hand.Hand.Count) : 0;

            CapabilityInventory inv = CapabilityInventory.Build(snap, player, commitments);
            var reservation = new MaterializationReservation
            {
                GenerationAttemptsUsed = budget.GenerationAttemptsUsed,
            };
            var heroDemand = new AxisDemand
            {
                RequestingAxis = DesireAxis.Aggression, Capability = CapabilityKind.Hero,
                DesiredAmount = 1, RequiredTraits = TraitPreference.None, MinimumFollowupAp = 0f,
                TargetHex = objective.LastKnownHex, Value = objective.BaseValue,
            };
            var powerDemand = new AxisDemand
            {
                RequestingAxis = DesireAxis.Aggression, Capability = CapabilityKind.FieldCombatPower,
                DesiredAmount = Mathf.Max(1f, readiness.RequestedPower), RequiredTraits = TraitPreference.None,
                MinimumFollowupAp = 0f, TargetHex = objective.LastKnownHex, Value = objective.BaseValue,
            };

            HashSet<int> claimed = commitments?.ClaimedArmyIdSet ?? new HashSet<int>();

            // P0.2 (round 10) — the shortage (NumericPowerDeficit) is measured in the CANONICAL
            // own-force metric: ArmySnapshot.EffectiveArmyPower == AiPower.EffectiveArmyPower over
            // AiPower.PowerUnit (Attack/Defense/HP/Init/Resistance/Fate/Range/IsHero + full ability
            // multiplier + composition). The contribution MUST be a delta of that SAME metric —
            // NOT WorthIt.DefenderProfile, which is a lossy enemy/fog line (no Resistance/Fate,
            // Range forced to 1, IsHero=false, reduced abilities).
            //
            // P0.3 (round 10) — per target army also carry the projected physical CAPACITY (canonical
            // ArmyData.Capacity baseline + hero occupancy). Two individually-legal cards into the
            // SAME army with one free slot must not BOTH be accepted — execution would preflight-fail
            // the second. Conservative: one hero per army; a hero raises capacity by +1 (an under-
            // estimate — a real hero's CommandRating is ≥3). ProjectedRoster power is still counted
            // only for a raid-ELIGIBLE army (structural raid actor, unclaimed — mirrors CapabilityInventory).
            var armyState = new Dictionary<string,
                (List<AiPower.PowerUnit> seed, bool eligible, int freeNonHero, bool hasHero)>();
            (string key, bool eligible) ResolveArmy(MaterializationPlan p)
            {
                switch (p.Deploy.Kind)
                {
                    case DeploymentKind.ExistingArmy:
                    {
                        int id = p.Deploy.Army?.Id ?? -1;
                        string k = "existing:" + id;
                        if (!armyState.ContainsKey(k))
                        {
                            ArmySnapshot a = snap?.Self?.Armies?.FirstOrDefault(x => x != null && x.ArmyId == id);
                            ArmyData live = ArmyRegistry.AllForOwner(player)
                                .FirstOrDefault(x => x != null && x.Id == id);
                            bool elig = a != null && RaidAssemblyPlanner.IsStructuralRaidActor(a)
                                && !claimed.Contains(id);
                            armyState[k] = (
                                live?.Members != null
                                    ? live.Members.Select(m => AiPower.ToPowerUnit(m)).ToList()
                                    : new List<AiPower.PowerUnit>(),
                                elig,
                                live != null ? Mathf.Max(0, live.Capacity - live.Members.Count) : 0,
                                live != null && live.Members.Any(m => m != null && m.IsHero));
                        }
                        return (k, armyState[k].eligible);
                    }
                    case DeploymentKind.ReusableShell:
                    {
                        string k = "shell:" + (p.Deploy.Army?.Id ?? -1);
                        if (!armyState.ContainsKey(k))
                            armyState[k] = (new List<AiPower.PowerUnit>(), true, 2, false);
                        return (k, true);
                    }
                    default: // NewArmy — fresh solo non-hero unit (hero-only solo is excluded by
                             // CanDeliverDemandOperationally), structurally a raid actor.
                    {
                        string k = "new:" + p.StableKey;
                        if (!armyState.ContainsKey(k))
                            armyState[k] = (new List<AiPower.PowerUnit>(), true, 2, false);
                        return (k, true);
                    }
                }
            }
            AiPower.PowerUnit ProjectedUnit(MaterializationPlan p)
            {
                CardDefinition def = p.BaseCardInHand?.Definition ?? p.GeneratedBaseDef;
                // ProjectMaterialization folds in equipment already-attached AND attached-by-this-plan;
                // with no equipment it returns the plain base line — one path for every chain kind.
                AiPower.ProjectedStrategicLine line = AiPower.ProjectMaterialization(p);
                return new AiPower.PowerUnit(Mathf.Max(0f, line.BasePower), def?.unitTypeTags, line.Range,
                    def != null && def.cardType == CardType.Hero);
            }

            // Legal candidate pool — the SAME enumeration the hand-follow-up probe uses.
            var pool = new List<(MaterializationPlan plan, float ap, bool deliversHero, bool isHeroPlan,
                string armyKey, bool armyEligible, AiPower.PowerUnit unit)>();
            foreach (MaterializationPlan p in MaterializationCandidateBuilder.EnumerateSurplusPlans(
                snap, player, root, hand, ctx, inv, commitments, reservation))
            {
                if (p == null) continue;
                bool dHero = MaterializationCandidateBuilder.CanDeliverDemandOperationally(p, heroDemand);
                bool dPower = MaterializationCandidateBuilder.CanDeliverDemandOperationally(p, powerDemand);
                if (!dHero && !dPower) continue;
                (string armyKey, bool elig) = ResolveArmy(p);
                CardDefinition bd = p.BaseCardInHand?.Definition ?? p.GeneratedBaseDef;
                bool isHeroPlan = bd != null && bd.cardType == CardType.Hero;
                pool.Add((p, Mathf.Max(0f, p.ApCost), dHero, isHeroPlan, armyKey, elig, ProjectedUnit(p)));
            }
            if (pool.Count == 0)
                return null;
            pool = pool
                .OrderBy(c => c.ap).ThenBy(c => ResCostSum(c.plan.ResCost))
                .ThenBy(c => c.plan.StableKey, System.StringComparer.Ordinal)
                .Take(reactionMatPoolCap).ToList();

            float needPowerAmt = needPower ? Mathf.Max(0f, readiness.NumericPowerDeficit) : 0f;
            const string owner = "reaction-budget:MaterializeForDiscovery";
            var consumed = new MaterializationConsumptionState();

            float bestPrepAp = float.MaxValue, bestEnvSum = float.MaxValue;
            ResourceCost bestEnv = null;
            string bestKey = null;
            var chosen = new List<(MaterializationPlan plan, float ap, bool deliversHero, bool isHeroPlan,
                string armyKey, bool armyEligible, AiPower.PowerUnit unit)>();

            // Σ over touched eligible armies of (EffectiveArmyPower(seed + added units) − seed),
            // recomputed from scratch at each node — cheap (pool ≤ 24, depth ≤ 3) and free of
            // incremental-bookkeeping bugs. SAME metric as NumericPowerDeficit.
            float ProjectedFieldPowerDelta()
            {
                float total = 0f;
                foreach (var g in chosen.GroupBy(c => c.armyKey))
                {
                    if (!g.First().armyEligible) continue;
                    List<AiPower.PowerUnit> seed = armyState[g.Key].seed ?? new List<AiPower.PowerUnit>();
                    float before = AiPower.EffectiveArmyPower(seed);
                    var after = new List<AiPower.PowerUnit>(seed);
                    foreach (var c in g) after.Add(c.unit);
                    total += Mathf.Max(0f, AiPower.EffectiveArmyPower(after) - before);
                }
                return total;
            }

            // P0.3 — would `chosen` + `extra` still be physically placeable? One hero per recipient
            // army; non-hero slots bounded by the projected free capacity; the combined generate-
            // chain hand-slot peak within the free hand.
            bool RecipientCapacityOk(
                (MaterializationPlan plan, float ap, bool deliversHero, bool isHeroPlan,
                 string armyKey, bool armyEligible, AiPower.PowerUnit unit) extra)
            {
                int handPeak = extra.plan.HandSlotsNeededAtPeak;
                foreach (var c in chosen) handPeak += c.plan.HandSlotsNeededAtPeak;
                if (handPeak > handSlotBudget)
                    return false;
                foreach (var g in chosen.Append(extra).GroupBy(c => c.armyKey))
                {
                    var st = armyState[g.Key]; // ResolveArmy always populates this for every pool key
                    int heroesAdded = 0, nonHeroAdded = 0;
                    foreach (var c in g) { if (c.isHeroPlan) heroesAdded++; else nonHeroAdded++; }
                    if ((st.hasHero ? 1 : 0) + heroesAdded > 1)
                        return false;
                    int nonHeroCap = st.freeNonHero + (heroesAdded > 0 ? 1 : 0);
                    if (nonHeroAdded > nonHeroCap)
                        return false;
                }
                return true;
            }

            void Consider()
            {
                bool powerOk = !needPower || ProjectedFieldPowerDelta() + 0.001f >= needPowerAmt;
                bool heroOk = !needHero || chosen.Any(c => c.deliversHero);
                if (!powerOk || !heroOk)
                    return;
                if (consumed.ApUsed > prepCeiling + 0.001f)
                    return;
                var env = new ResourceCost
                {
                    human = consumed.HumanUsed, energy = consumed.EnergyUsed,
                    materials = consumed.MaterialsUsed, tech = consumed.TechUsed,
                };
                if (!StrategicManager.FitsSpendableResources(player, root, ctx, env, owner))
                    return;
                float envSum = ResCostSum(env);
                if (consumed.ApUsed < bestPrepAp - 0.001f
                    || (consumed.ApUsed <= bestPrepAp + 0.001f && envSum < bestEnvSum - 0.001f))
                {
                    bestPrepAp = consumed.ApUsed;
                    bestEnvSum = envSum;
                    bestEnv = envSum > 0f ? env : null;
                    bestKey = string.Join("+",
                        chosen.Select(c => c.plan.StableKey).OrderBy(k => k, System.StringComparer.Ordinal));
                }
            }

            void Dfs(int start)
            {
                Consider();
                if (chosen.Count >= maxActions)
                    return;
                for (int i = start; i < pool.Count; i++)
                {
                    var c = pool[i];
                    if (!consumed.CardsDisjoint(c.plan)) continue;
                    if (c.plan.Generation != null && consumed.GenerationAttempts + 1 > genRemaining) continue;
                    if (consumed.ApUsed + c.ap > prepCeiling + 0.001f) continue;
                    if (!RecipientCapacityOk(c)) continue;
                    MaterializationConsumptionState.Token token = consumed.Push(c.plan);
                    chosen.Add(c);
                    Dfs(i + 1);
                    chosen.RemoveAt(chosen.Count - 1);
                    consumed.Pop(token);
                }
            }
            Dfs(0);

            if (bestKey == null)
                return null;

            float total = bestPrepAp + downstreamAp;
            string envStr = bestEnv == null ? "-"
                : $"H{bestEnv.human} E{bestEnv.energy} M{bestEnv.materials} T{bestEnv.tech}";
            string shortage = needHero && needPower ? $"Hero+power≥{needPowerAmt:0.#}"
                : needHero ? "Hero" : $"power≥{needPowerAmt:0.#}";
            return new MaterializationClosure
            {
                TotalAp = total,
                Envelope = bestEnv,
                Key = bestKey,
                Detail = $"materializeForDiscovery witness: raid #{objective.TargetArmyId} shortage {shortage} "
                    + $"closed by [{bestKey}] — prep {bestPrepAp:0.#} AP + downstream {downstreamAp:0.#} AP "
                    + $"+ envelope [{envStr}] (projected RaidAvailableFieldPower Δ ≥ deficit)",
            };
        }

        // §3/§P1 (round 6) — a hand follow-up is feasible only if SOME legal play (from the full
        // preflighted enumeration, not just the best-scored one) fits the reaction AP ceiling AND
        // its persistent envelope is spendable. FitsSpendableResources excludes the HandFollowup
        // reservation owner so a re-probe after the envelope is placed doesn't fail against itself.
        private static List<ReactionWitness> ProbeHandFollowup(PlayerSetupData player, PlayerRoot root,
            AiTurnContext ctx, WorldSnapshot snap, AiHandData hand, ActorCommitments commitments)
        {
            var witnesses = new List<ReactionWitness>();
            var reservation = new MaterializationReservation
            {
                GenerationAttemptsUsed = StrategicTempoBudget.GenerationUsed(player, ctx.TurnNumber),
            };
            CapabilityInventory inv = CapabilityInventory.Build(snap, player, commitments);

            var options = new List<(float ap, ResourceCost env)>();
            foreach (NonCombatCardPlayer.NonCombatPlay p in NonCombatCardPlayer.EnumeratePlays(
                snap, player, root, hand, ctx, null, reservation))
                options.Add((p.Card != null ? p.Card.EffectivePlayApCost : 0f,
                    p.Generation != null ? p.Generation.GenerationResourceCost
                        : (p.Card != null ? p.Card.EffectivePlayResourceCost : null)));
            foreach (MaterializationPlan mp in MaterializationCandidateBuilder.EnumerateSurplusPlans(
                snap, player, root, hand, ctx, inv, commitments, reservation))
                options.Add((mp.ApCost, mp.ResCost));

            float ap = root != null ? Mathf.Max(0f, root.ActionPoints) : 0f;
            float ceiling = Mathf.Min(ap, (float)AiConfigV2.reactionReserveApCap);
            const string owner = "reaction-budget:HandFollowup";
            var feasible = options
                .Where(o => o.ap <= ceiling + 0.001f
                    && StrategicManager.FitsSpendableResources(player, root, ctx, o.env, owner))
                .OrderBy(o => o.ap)
                .ThenBy(o => ResCostSum(o.env))
                .ToList();
            if (feasible.Count == 0)
            {
                AiDebugLog.Write("[AI][V2] reaction probe(handFollowup) — no legal hand play fits the reaction "
                    + $"AP ceiling ({ceiling:0.#}) + spendable-resource check");
                return witnesses;
            }
            var best = feasible[0];
            string env = best.env == null ? "-"
                : $"H{best.env.human} E{best.env.energy} M{best.env.materials} T{best.env.tech}";
            // P0.1 — RequiredAp is the FULL cost: the play AP, floored by the reaction follow-up
            // estimate (the replan's own downstream allowance).
            float requiredAp = Mathf.Max(best.ap, Mathf.Max(0f, AiConfigV2.reactionFollowupApEstimate));
            witnesses.Add(new ReactionWitness("HandFollowup",
                $"handFollowup:{best.ap:0.#}:{env}", requiredAp, best.env,
                $"handFollowup witness: play {best.ap:0.#} AP (RequiredAp {requiredAp:0.#}) + envelope "
                + $"[{env}] ({feasible.Count} feasible plays)"));
            return witnesses;
        }

        private static float ResCostSum(ResourceCost c) => c == null ? 0f
            : c.human + c.energy + c.materials + c.tech;

        // §6 — before the bounded reaction round runs, a genuinely feasible reaction must STILL
        // exist (the same probe, not a weaker one). If not, its budget + envelope reservation is
        // released so the resources re-enter arbitration this turn (Housekeeping's tempo re-run).
        internal static bool ReactionStillActionable(PlayerSetupData player, PlayerRoot root,
            AiTurnContext ctx, WorldSnapshot snap)
            => BuildReactionOpportunity(player, root, ctx, snap).IsActionable;

        public static IEnumerator ExecuteIfPending(WorldSnapshot priorSnapshot, PlayerSetupData player,
            PlayerRoot root, AiTurnContext ctx, StrategicReactionResult result)
        {
            // ReconOnly isolates the current deep-rework from the legacy strategic reaction loop.
            // The live Recon executor will own ordinary step->refresh->reaction; until that lands,
            // do not let a contact discovery reopen Aggression/Defence/Economy/Development through
            // this second orchestration path. Consume the turn-scoped invalidation so it cannot
            // leak into the next turn.
            if (AiStrategyV2Scope.IsReconOnly)
            {
                if (player != null && ctx != null && StrategicInterruptRegistry.HasPending(player, ctx.TurnNumber))
                {
                    StrategicInterruptRegistry.Clear(player, ctx.TurnNumber);
                    AiDebugLog.Write("[AI][V2][Scope] strategic reaction pass suppressed reason=ReconOnly");
                }
                // AI-MGR-02 §4 — a scope-suppressed pass deliberately leaves any AP reservation in
                // place: HousekeepingManager releases it and re-runs end-of-turn tempo spending with
                // the freed AP the same turn (so it is not stranded).
                yield break;
            }

            yield return ExecuteRound(priorSnapshot, player, root, ctx,
                result ?? new StrategicReactionResult(), 0);

            // AI-MGR-02 §4 — the pass has had its bounded round(s); any AP Phase B reserved for it
            // is now free (its own inner Phase B call already spent whatever it wanted).
            if (player != null && ctx != null)
                StrategicResourceReservationLedger.ExpireStage(player, ctx.TurnNumber,
                    StrategicReservationExpiry.EndOfReaction);
        }

        private static IEnumerator ExecuteRound(WorldSnapshot priorSnapshot, PlayerSetupData player,
            PlayerRoot root, AiTurnContext ctx, StrategicReactionResult result, int round)
        {
            if (player == null || root == null || ctx == null || ctx.Map == null)
                yield break;
            if (!StrategicInterruptRegistry.HasPending(player, ctx.TurnNumber))
                yield break;

            HashSet<int> targetIds = StrategicInterruptRegistry.TargetIds(player, ctx.TurnNumber);
            AiHandData hand = AiHandRegistry.Peek(player);
            if (hand == null)
                StrategicInterruptRegistry.TryGetHand(player, ctx.TurnNumber, out hand);

            // §6 — immediately before the bounded round, re-run the SAME feasibility probe. If no
            // genuinely feasible reaction remains, release the budget + envelope reservation now so
            // the resources re-enter arbitration THIS turn (HousekeepingManager's tempo re-run)
            // instead of being pinned to a dead budget.
            if (round == 0 && hand != null
                && StrategicResourceReservationLedger.HasAny(player, ctx.TurnNumber)
                && !ReactionStillActionable(player, root, ctx,
                    WorldAnalysis.Scan(player, root, hand, ctx)))
            {
                StrategicResourceReservationLedger.ReleaseByReason(player, ctx.TurnNumber,
                    StrategicReservationReason.StrategicReactionPass);
                AiDebugLog.Write("[AI][V2] reaction — the feasibility probe no longer finds a feasible "
                    + "reaction; released the reaction-budget + envelope reservation before executing");
            }

            StrategicInterruptRegistry.Clear(player, ctx.TurnNumber);
            result.Ran = true;
            result.Rounds++;
            result.DiscoveredTargets += targetIds.Count;

            // Correlation scope for this bounded round: round 0 -> T{turn}-P{c}-R1, round 1 -> …-R2.
            V2TraceScope rtrace = AiV2Trace.BeginReaction(player, ctx.TurnNumber, round);
            V2ResourceStamp rStart = AiV2Trace.Stamp(root);

            // A bounded reaction round is a fresh capability-exhaustion scope: Phase A below may
            // materialise new capability, so nothing the main pass marked exhausted carries in.
            CapabilityPoolExhaustionRegistry.BeginRound(player, ctx.TurnNumber, round + 1);

            if (hand == null)
            {
                AiDebugLog.Write("[AI][V2] reaction — pending invalidation consumed, but no AI hand is available; defer to next turn");
                // The round opened a scope; close it with a [STATE] line on this early exit too.
                AiV2Trace.LogState(rtrace.Id, rStart, AiV2Trace.Stamp(root));
                yield break;
            }

            int apAtStart = root.ActionPoints;
            AiDebugLog.Write($"[AI][V2] reaction — BEGIN round {round + 1}/2 bounded strategic replan "
                + $"targets=[{string.Join(",", targetIds.OrderBy(x => x))}] ap={apAtStart}");

            WorldSnapshot snapshot = WorldAnalysis.Scan(player, root, hand, ctx);
            AiRadarState radarState = AiRadarStateRegistry.GetOrCreate(player);
            RadarAssessment assessment = StrategyLayer.Evaluate(snapshot, radarState);
            Radar radar = assessment.Radar;
            List<ReconObjective> reconObjectives = ReconObjectiveEvaluator.Enumerate(snapshot);
            List<AggressionObjective> aggressionObjectives =
                AggressionObjectiveEvaluator.Enumerate(snapshot, assessment.Breakdown.OpportunityReport);

            AiDebugLog.Write($"[AI][V2] reaction — radar {radar.DebugLine()} "
                + $"aggObjectives={aggressionObjectives.Count} reconObjectives={reconObjectives.Count}");
            foreach (AggressionObjective ao in aggressionObjectives)
                AiDebugLog.Write($"[AI][V2]   reaction aggObjective — {ao.ObjectiveId} "
                    + $"@{ao.LastKnownHex.Q},{ao.LastKnownHex.R} base "
                    + $"{ao.BaseValue.ToString("0.0", CultureInfo.InvariantCulture)} "
                    + $"readyWin {ao.ReadyWinChance.ToString("0.00", CultureInfo.InvariantCulture)} "
                    + $"asmWin {ao.AssemblableWinChance.ToString("0.00", CultureInfo.InvariantCulture)} "
                    + $"gate {(ao.GatePassed ? 1 : 0)}");

            List<MissionIntent> activeIntents = MissionContinuityLayer.ResolveActive(player, snapshot);
            ActorCommitments actorCommitments = ActorCommitments.FromIntents(activeIntents, snapshot, reconObjectives);
            // AI-RECON-01 — the reaction round runs its OWN air reservation prepass. The main pass's
            // reservation was already consumed by its terminal air fallback (aircraft moved / AP
            // spent), so reusing its stale ReservedLaunchSorties would let DemandLayer suppress a
            // ground scout for capacity that no longer exists. Reset + re-evaluate against the
            // now-current AP / Energy / movement.
            ReconAirReservationPrepass.Run(snapshot, player, root, ctx, activeIntents, actorCommitments, reconObjectives);
            List<AxisDemand> demands = DemandLayer.Generate(snapshot, assessment.Breakdown,
                reconObjectives, aggressionObjectives, activeIntents, actorCommitments, player);
            result.Demands += demands.Count;

            ReconAirReservationState airReservation =
                ReconAirReservationRegistry.ForTurn(player, snapshot.TurnNumber);
            AxisBudgetLedger apLedger = AxisBudgetLedger.Create(
                UnityEngine.Mathf.Max(0f, (snapshot.Self?.ActionPoints ?? 0) - airReservation.ProtectedAp), radar);
            StrategicPhaseResult phaseA = StrategicManager.FulfillDemands(snapshot, player, root, hand,
                ctx, apLedger, demands, actorCommitments);
            result.CardsPlayed += phaseA.CardsPlayed;
            result.StateChanged |= phaseA.StateChanged;
            if (phaseA.StateChanged)
                snapshot = WorldAnalysis.RefreshOperationalState(snapshot, player, root, hand, ctx);

            List<MissionProposal> missions = MissionLayer.Propose(snapshot, assessment.Breakdown,
                activeIntents, reconObjectives);
            missions.AddRange(AggressionMissionLayer.Propose(snapshot, assessment.Breakdown,
                activeIntents, aggressionObjectives));
            // AI-RECON-01 — the reaction round runs the SAME DemandLayer -> MissionLayer -> Allocator
            // -> Provisioning path as the main pass, so it needs the same actor-before-budget
            // reservation, or every reaction-round Scout would defer ReconActorUnreserved.
            var reconActorCtx = new ReconActorReservationContext();
            ReconActorReservationPlanner.Plan(reconActorCtx, snapshot, ctx, player, missions, actorCommitments,
                activeIntents, reconObjectives);
            foreach (MissionProposal m in missions)
                if (m != null && string.IsNullOrEmpty(m.AttemptId))
                    m.AttemptId = rtrace?.NextMissionAttemptId() ?? "?";
            AiV2Trace.CorrelateDemandsToMissions(demands, missions);
            foreach (MissionProposal m in missions)
                AiDebugLog.Write($"[AI][V2]   reaction mission — [{m.AttemptId}] causeDemand={m.CauseDemandTrace} "
                    + $"{m.Kind} base {m.BaseValue.ToString("0.0", CultureInfo.InvariantCulture)} | {m.Explain}");
            result.Missions += missions.Count;

            List<Commitment> commitments = MissionContinuityLayer.BindFunding(activeIntents, missions);
            var outcomeLedger = new MissionOutcomeLedger();
            outcomeLedger.RegisterProposals(missions);
            outcomeLedger.RegisterCommitments(commitments);

            AllocationSession session = ResourceAllocator.BeginTurn(snapshot, radar, missions,
                commitments, player, apLedger, airReservation.ProtectedEnergy, airReservation.ProtectedAp);
            var provSession = new ProvisioningSession(snapshot);
            TentativeAllocation allocation = session.Pack();
            var provisioned = new List<ProvisionedMission>();

            int reallocPass = 0;
            while (true)
            {
                bool anyFailure = false;
                bool anySuccess = false;
                bool allFailuresArePoolWide = true;
                ProvisioningManager.PreparePass(player, root, ctx, provSession, allocation,
                    reconActorCtx.ReservedActorIds);
                foreach (FundedEntry fe in allocation.Funded)
                {
                    if (fe?.Mission == null) continue;
                    StableMissionKey key = StableMissionKey.For(fe.Mission);
                    if (provSession.AlreadyProvisioned(key)) continue;
                    // A capability pool proven pool-wide unable stays exhausted across the reaction
                    // round boundary; it is only re-tried if revalidation now finds an actor (spec §7).
                    if (!CapabilityPoolExhaustionRegistry.RevalidateAndClearIfRecovered(player,
                            CapabilityPoolExhaustionRegistry.PoolFor(fe.Mission), snapshot))
                        continue;

                    ProvisioningResult provision = ProvisioningManager.Provision(
                        player, root, hand, ctx, provSession, fe);
                    if (provision.Success)
                    {
                        anySuccess = true;
                        provSession.RegisterSuccess(key, provision.Provisioned);
                        session.RegisterProvisionSuccess(fe, provision.Provisioned.ClaimedAp);
                        outcomeLedger.RecordProvisionSuccess(fe.Mission, provision.Provisioned);
                        provisioned.Add(provision.Provisioned);
                        AiV2Trace.CheckProvisionEnvelope(fe.Mission.AttemptId,
                            provision.Provisioned.ClaimedAp, fe.Tentative.Ap);
                        AiDebugLog.Write($"[AI][V2]   reaction provision [{fe.Mission.AttemptId}] {key} — OK mover "
                            + $"#{provision.Provisioned.MoverArmyId} ap "
                            + $"{provision.Provisioned.ClaimedAp.ToString("0.#", CultureInfo.InvariantCulture)}");
                    }
                    else
                    {
                        anyFailure = true;
                        bool poolWide = CapabilityPoolExhaustionRegistry.ProvenPoolWideUnable(
                            snapshot, player, fe.Mission, provision.Failure);
                        if (poolWide)
                            CapabilityPoolExhaustionRegistry.MarkExhausted(player,
                                CapabilityPoolExhaustionRegistry.PoolFor(fe.Mission),
                                $"reaction {provision.Failure.Kind}: no eligible actor in snapshot");
                        allFailuresArePoolWide &= poolWide;
                        session.RegisterProvisionFailure(fe, provision.Failure);
                        outcomeLedger.RecordProvisionFailure(fe.Mission, provision.Failure);
                        if (fe.Mission.Kind == MissionKind.Scout
                            && provision.Failure.Kind != ProvisionFailureKind.EnvelopeTooSmall)
                            ReconActorReservationPlanner.RecordProvisionFailure(reconActorCtx, fe.Mission,
                                provision.Failure.Kind);
                        AiDebugLog.Write($"[AI][V2]   reaction provision [{fe.Mission.AttemptId}] {key} — FAIL "
                            + $"{provision.Failure.Kind} [{provision.Failure.Disposition}] "
                            + provision.Failure.Detail);
                    }
                }

                if (anyFailure && allFailuresArePoolWide)
                {
                    AiDebugLog.Write("[AI][V2] reaction — every funded mission's capability pool is exhausted this cycle; stop key-by-key reallocation");
                    break;
                }
                bool reconRematched = ReconActorReservationPlanner.Rematch(reconActorCtx, missions, provSession,
                    allocation, portfolioChanged: anyFailure || anySuccess);
                if (!reconRematched && (!session.HasNewFailures || session.Converged))
                    break;
                if (++reallocPass >= AiConfigV2.maxReallocIterations)
                    break;
                allocation = session.Pack();
            }

            result.Provisioned += provisioned.Count;
            var executed = new List<ExecutionResult>();
            // Same lifecycle as the main pipeline: outcomeLedger.RegisterProposals(missions) rowed
            // every Explore proposal (incl. deferred), so the stale-Explore replacement picker must
            // be told the whole focus set, not just what reached the queue. Shared helper keeps the
            // two passes from drifting.
            HashSet<HexCoord> exploreProposalFoci = MissionRevalidator.CollectExploreProposalFoci(missions);
            yield return TaskExecutor.Execute(player, root, ctx, provisioned, executed, snapshot, exploreProposalFoci,
                () => ReconAirReservationPrepass.ReleaseProtection(player));
            ReconAirReservationPrepass.ReleaseProtection(player);
            result.Executed += executed.Count(MissionRevalidator.WasAttempt);
            foreach (ExecutionResult er in executed)
            {
                if (er.IsReplacement && er.Source?.Mission != null)
                {
                    outcomeLedger.RegisterProposals(new[] { er.Source.Mission });
                    outcomeLedger.RecordProvisionSuccess(er.Source.Mission, er.Source);
                }
                outcomeLedger.RecordExecution(er);
            }
            outcomeLedger.RecordDeferrals(allocation.Deferred);
            outcomeLedger.RefreshObjectiveStatesLive(player);
            MissionContinuityLayer.ReconcileAfterTurn(player, snapshot.TurnNumber, outcomeLedger.Finalize());

            if (StrategicInterruptRegistry.HasPendingContactDiscovery(player, ctx.TurnNumber))
            {
                HashSet<int> deferred = StrategicInterruptRegistry.TargetIds(player, ctx.TurnNumber);
                AiDebugLog.Write($"[AI][V2] reaction — contact recursion suppressed; additional discovery "
                    + $"[{string.Join(",", deferred.OrderBy(x => x))}] deferred to next strategic scan");
                StrategicInterruptRegistry.ClearDiscovery(player, ctx.TurnNumber);
            }

            snapshot = WorldAnalysis.RefreshOperationalState(snapshot, player, root, hand, ctx);
            ActorCommitments postCommitments = ActorCommitments.FromIntents(
                MissionIntentRegistry.GetOrCreate(player).All, snapshot, ReconObjectiveEvaluator.Enumerate(snapshot));
            // AI-MGR-02 §7/§P0 — the reaction round is NOW executing its own spend. The AP that
            // Phase B reserved as a placeholder for "the reaction will need AP" must be released
            // BEFORE this inner tempo arbitration, or the reaction cannot use the very AP it held
            // back (and it would look stranded until end of turn).
            StrategicResourceReservationLedger.ReleaseByReason(player, ctx.TurnNumber,
                StrategicReservationReason.StrategicReactionPass);
            var phaseB = new StrategicPhaseResult();
            yield return StrategicManager.UseSurplus(snapshot, player, root, hand, ctx,
                postCommitments, phaseA.Reservation, phaseB);
            result.CardsPlayed += phaseB.CardsPlayed;
            result.CardsDrawn += phaseB.CardsDrawn;
            result.StateChanged |= phaseB.StateChanged || executed.Count > 0;

            // Reaction-phase activity bucket (additive across the up-to-2 bounded rounds). Every
            // execution counter is DERIVED from `executed` exactly once — never incremented inside
            // TaskExecutor (spec §11). The Main bucket is owned by Pipeline.RunTurn; Total = Main +
            // Reaction, no double count.
            V2PhaseActivity ract = V2TurnActivityTelemetry.Phase(player, ctx.TurnNumber, V2Phase.Reaction);
            ract.DemandsRaised += demands.Count;
            ract.MissionsConsidered += missions.Count;
            ract.MissionsFunded += allocation.Funded.Count;
            ract.Provisioned += provisioned.Count;
            ract.ExecutionAttempts += executed.Count(MissionRevalidator.WasAttempt);
            ract.ExecutionsSucceeded += executed.Count(MissionRevalidator.WasGenuineExecution);
            ract.ExecutionsStaleOrSkipped += executed.Count(MissionRevalidator.WasStaleOrSkipped);
            ract.ReplacementMissions += executed.Count(MissionRevalidator.WasReplacement);
            ract.CardsPlayed += phaseA.CardsPlayed + phaseB.CardsPlayed;
            ract.CardsDrawn += phaseB.CardsDrawn;
            ract.CapabilityDeliveries += phaseA.CapabilityDeliveries + phaseB.CapabilityDeliveries;
            ract.InfrastructureAttempts += phaseA.InfrastructureAttempts + phaseB.InfrastructureAttempts;
            ract.InfrastructureBuilt += phaseA.InfrastructureBuilt + phaseB.InfrastructureBuilt;
            ract.MaterializationAttempts += phaseA.MaterializationAttempts + phaseB.MaterializationAttempts;
            ract.MaterializationsSucceeded += phaseA.MaterializationsSucceeded + phaseB.MaterializationsSucceeded;
            ract.GeneratedCardAttempts += phaseA.GeneratedCardAttempts + phaseB.GeneratedCardAttempts;
            ract.GeneratedCardsSucceeded += phaseA.GeneratedCardsSucceeded + phaseB.GeneratedCardsSucceeded;
            ract.EquipmentAssignmentAttempts += phaseA.EquipmentAssignmentAttempts + phaseB.EquipmentAssignmentAttempts;
            ract.EquipmentAssignmentsSucceeded += phaseA.EquipmentAssignmentsSucceeded + phaseB.EquipmentAssignmentsSucceeded;

            AiDebugLog.Write($"[AI][V2] reaction — END round {round + 1}/2 ap {apAtStart}->{root.ActionPoints}, "
                + $"demands {demands.Count}, missions {missions.Count}, provisioned {provisioned.Count}, "
                + $"executed {executed.Count}, cardsPlayed {phaseA.CardsPlayed + phaseB.CardsPlayed}, "
                + $"draws {phaseB.CardsDrawn}");
            // End-of-round physical resource control totals (spec §2.7).
            AiV2Trace.LogState(rtrace.Id, rStart, AiV2Trace.Stamp(root));

            if (StrategicInterruptRegistry.HasPendingFollowup(player, ctx.TurnNumber))
            {
                if (round == 0)
                {
                    AiDebugLog.Write("[AI][V2] reaction — operational hand/capability changed inside round 1; run one bounded follow-up round");
                    yield return ExecuteRound(snapshot, player, root, ctx, result, 1);
                }
                else
                {
                    AiDebugLog.Write("[AI][V2] reaction — follow-up bound reached; remaining hand/capability invalidation deferred to next strategic scan");
                    StrategicInterruptRegistry.Clear(player, ctx.TurnNumber);
                }
            }
        }
    }
}