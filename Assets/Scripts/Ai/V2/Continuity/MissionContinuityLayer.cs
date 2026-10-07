using System;
using System.Collections.Generic;
using System.Linq;
using Game.Cards;
using Game.Economy;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Aviation;

namespace Game.Ai.V2
{
    // MissionContinuityLayer (Strategy V2 build-order step 7).
    // File-split (mechanical, no behaviour change) from MissionIntent.cs — see
    // Docs/ai-v2-file-split-refactor-tasks.md Task 3. Independent standalone types,
    // domain policies share this partial class and one durable intent store.
    internal static partial class MissionContinuityLayer
    {
        internal static HexCoord? SelectEconomyRecoveryTarget(WorldSnapshot snap,
            PlayerSetupData player, ArmySnapshot actor, bool avoidCurrentHex = false)
        {
            if (actor == null || player == null) return null;
            return SelectEconomyHome(snap, actor.Hex, actor.EconomyHomeRouteCosts, avoidCurrentHex);
        }

        // Economy chooses the closest physically reachable shelter. Combat's "most active base"
        // ranking intentionally has a different purpose and keeps its separate owner.
        internal static HexCoord? SelectEconomyHome(WorldSnapshot snap, HexCoord from,
            IReadOnlyDictionary<HexCoord, int> routeCosts, bool avoidCurrentHex = false)
        {
            if (snap?.Self?.BaseHexes == null || routeCosts == null) return null;
            IEnumerable<HexCoord> candidates = snap.Self.BaseHexes.Where(h =>
                routeCosts.TryGetValue(h, out int cost) && cost < int.MaxValue);
            if (avoidCurrentHex && candidates.Any(h => !h.Equals(from)))
                candidates = candidates.Where(h => !h.Equals(from));
            return candidates.OrderBy(h => routeCosts[h])
                .ThenBy(h => AiReturnBasePolicy.BaseThreatSeverityAt(snap, h))
                .ThenByDescending(h => h.Equals(snap.Self.Citadel))
                .ThenBy(h => h.Q).ThenBy(h => h.R)
                .Select(h => (HexCoord?)h).FirstOrDefault();
        }

        // Exact obligation identity, distinct from the coarser site lease. The optional card is
        // an unpinned durable choice; resource type always distinguishes extraction obligations.
        internal static bool MatchesEconomyBuild(MissionIntent intent, EconomyTaskKind kind,
            HexCoord hex, ResourceType? resource, CardData card) => HoldsEconomyBuildSite(intent, hex)
                && intent.Economy.Kind == kind
                && (kind == EconomyTaskKind.FoundBase || intent.Economy.ResourceType == resource)
                && (intent.Economy.BuildCard == null || intent.Economy.BuildCard == card);

        internal static bool HasEconomyBuildCommitment(IReadOnlyList<MissionIntent> intents,
            AxisDemand demand) => demand?.TargetHex != null && intents != null
                && intents.Any(i => MatchesEconomyBuild(i, DemandLayer.EconomyBuildKind(demand),
                    demand.TargetHex.Value, demand.EconomyResourceType, demand.EconomyBuildCard));

        internal static bool RequiresEconomyBuilderRecovery(EconomyTaskKind completedKind,
            MissionIntent lender, bool underImmediateThreat, bool alreadyProtected,
            bool hasRecoveryTarget)
        {
            if (!hasRecoveryTarget) return false;
            if (alreadyProtected && !underImmediateThreat) return false;
            if (lender?.Kind == MissionKind.Scout && lender.Scout != null
                && !underImmediateThreat)
                return false;
            return true;
        }

        // Continuity is the sole owner of durable build-site leases. Physical placement may
        // allow a completed extraction site to become a Base later; two unfinished owners of
        // the SAME site are nevertheless incompatible. Recovery/collection is not construction.
        internal static bool HoldsEconomyBuildSite(MissionIntent intent, HexCoord hex) =>
            IsLiveEconomyBuild(intent) && intent.Economy.TargetHex.Equals(hex);

        // A build obligation (BuildExtraction / FoundBase) that still holds its site lease: Active,
        // or Suspended on a transient reason ResolveActive re-tests. The one predicate behind the
        // lease grant AND every Demand / Phase A "is this build committed" read — a transiently
        // suspended build is not a free site to originate a second builder for (audit B11).
        internal static bool IsLiveEconomyBuild(MissionIntent intent) =>
            intent != null && intent.Kind == MissionKind.Economy
            && (intent.Status == IntentStatus.Active || intent.Status == IntentStatus.Suspended)
            && intent.Economy != null
            && (intent.Economy.Kind == EconomyTaskKind.FoundBase
                || intent.Economy.Kind == EconomyTaskKind.BuildExtraction);

        // Same actor, same objective and same physical card use existing takeover ownership
        // policy. An independent actor/card/objective cannot acquire an already-leased site.
        internal static bool CanGrantEconomyBuildSite(PlayerSetupData player, HexCoord hex,
            MissionIntentKey candidateKey, int builderArmyId, CardData card)
        {
            if (player == null) return false;
            return !MissionIntentRegistry.GetOrCreate(player).All.Any(intent =>
                HoldsEconomyBuildSite(intent, hex)
                && intent.PreferredMoverArmyId != builderArmyId
                && !intent.IntentKey.Equals(candidateKey)
                && (card == null || intent.Economy.BuildCard != card));
        }

        // Materialization has delivered the Hero for one concrete Economy prerequisite (Capability.
        // Hero), OR Provisioning has just bound an ALREADY-EXISTING mobile builder to a multi-turn
        // delivery (Capability.EconomicInfrastructure/EconomicExpansionBase — see the direct-army
        // path in ProvisioningManager.ProvisionEconomy). Either way, the transaction-local capability
        // lease ends at this handoff; Continuity immediately becomes the sole owner of the actor and
        // exact build objective — StrategicPhaseA's protectedActiveEconomyBuild then protects the
        // full H/E/M/T vector every following turn regardless of remaining travel distance, so
        // InfrastructureFulfillment's own one-turn horizon only ever has to cover the turn BEFORE
        // this intent exists.
        internal static MissionIntent BeginEconomyDelivery(PlayerSetupData player,
            AxisDemand demand, int builderArmyId, int turn)
        {
            if (player == null || demand?.TargetHex == null
                || demand.RequestingAxis != DesireAxis.Economy
                || (demand.Capability != CapabilityKind.Hero
                    && demand.Capability != CapabilityKind.EconomicInfrastructure
                    && demand.Capability != CapabilityKind.EconomicExpansionBase))
                return null;

            EconomyTaskKind kind = DemandLayer.EconomyBuildKind(demand);
            var objective = new EconomyIntent
            {
                Kind = kind,
                TargetHex = demand.TargetHex.Value,
                ResourceType = demand.EconomyResourceType,
                BuilderArmyId = builderArmyId,
                BuildCard = demand.EconomyBuildCard,
                BuildResourceCost = demand.EconomyBuildResourceCost,
                BuildApCost = demand.EconomyBuildApCost,
                IntrinsicValue = demand.Value,
                BuildValue = demand.EconomySiteValue,
                MinimumFollowupAp = demand.MinimumFollowupAp,
            };
            var intent = new MissionIntent
            {
                Kind = MissionKind.Economy,
                Funding = CommitmentTier.Soft,
                Status = IntentStatus.Active,
                Suspended = SuspendReason.None,
                Objective = objective,
                CreatedTurn = turn,
                TurnsActive = 1,
                LastReconciledTurn = turn,
                LastProgressTurn = turn,
                PreferredMoverArmyId = builderArmyId,
            };
            intent.IntentKey = MissionIntentKey.For(intent);
            intent.LastAttemptKey = StableMissionKey.ForEconomy(kind, intent.IntentKey.ObjectiveId,
                objective.TargetHex);

            MissionIntentState state = MissionIntentRegistry.GetOrCreate(player);

            // Reentry: this exact (target/resource/kind, actor) delivery is already the active
            // intent — return the SAME object so its CreatedTurn/TurnsActive/StepsMovedTotal/
            // Funding/LoanSource history survives, instead of deleting and rebuilding it fresh
            // every time Provisioning re-confirms the same ongoing multi-turn walk.
            if (state.TryGet(intent.IntentKey, out MissionIntent existing)
                && existing.Status == IntentStatus.Active
                && existing.PreferredMoverArmyId == builderArmyId)
            {
                AiDebugLog.Write($"[ECO][Continuity] intent={existing.IntentKey} "
                    + $"actor=#{builderArmyId} decision=REUSE createdTurn={existing.CreatedTurn} "
                    + $"progress={existing.StepsMovedTotal} funding={existing.Funding}");
                return existing;
            }

            // Reject independent spatial contenders BEFORE retiring any existing intent or
            // touching its per-owner reserves. Demand-level dedup is not a durable ownership gate.
            if (!CanGrantEconomyBuildSite(player, objective.TargetHex, intent.IntentKey,
                    builderArmyId, objective.BuildCard))
            {
                AiDebugLog.Write($"[AI][V2][Economy] ownership refused {intent.IntentKey} "
                    + $"actor=#{builderArmyId} reason=site_owned_by_independent_contender");
                return null;
            }

            // This is the ONE place
            // Economy ownership is granted, so it is the one place that resolves ownership
            // CONFLICTS. Once the reentry case above has returned, a pre-existing Economy intent is
            // superseded only when it actually collides with the new grant:
            //   · same actor (PreferredMoverArmyId) — one army cannot hold two assignments, and a
            //     fresh delivery supersedes that same actor's own ReturnBuilder recovery walk (a
            //     ReturnBuilder belonging to a DIFFERENT actor is untouched);
            //   · same objective identity (IntentKey) — a takeover of this exact objective;
            //   · same physical build card — one card cannot fund two sites at once.
            // Any OTHER active Economy intent — a different target run by a different actor with a
            // different card — is an independent delivery and survives this handoff untouched.
            bool ConflictsWithGrant(MissionIntent i)
            {
                if (i == null || i.Kind != MissionKind.Economy) return false;
                if (i.PreferredMoverArmyId == builderArmyId) return true;
                if (i.Economy?.Kind == EconomyTaskKind.ReturnBuilder) return false;
                if (i.IntentKey.Equals(intent.IntentKey)) return true;
                return objective.BuildCard != null && i.Economy?.BuildCard == objective.BuildCard;
            }
            // A takeover/redirect conflict retires the DISPLACED intent through the ordinary
            // retirement, releasing its own reservation, so a forced handoff cannot leave this
            // turn's H/E/M/T hold reserved for an owner key nothing will ever complete or release.
            // Only a superseded ReturnBuilder returns its loan here: its actor goes back to the
            // lender's side of the ledger; a superseded build's actor stays on Economy work.
            foreach (MissionIntent stale in state.All.Where(ConflictsWithGrant).ToList())
                RetireEconomyIntent(state, stale, null, turn,
                    returnLoan: stale.Economy?.Kind == EconomyTaskKind.ReturnBuilder);
            state.Put(intent);
            AiDebugLog.Write($"[AI][V2][Economy] materialization handoff {intent.IntentKey} "
                + $"actor=#{builderArmyId} funding=Soft");
            return intent;
        }

        // The existing Continuity owner performs the only Base commitment switch. Same
        // card/actor are mandatory: no double-booking, no accidental donor/loan release.
        // Release the old reservation owner before rekeying; Phase A then reserves the new
        // target using the SAME intent object already referenced by the active-intent list.
        internal static bool TryRetargetCommittedBase(PlayerSetupData player,
            MissionIntent incumbent, AxisDemand challenger, int turn)
        {
            if (player == null || !DemandLayer.CanReplaceCommittedBase(incumbent, challenger)
                || (incumbent.LastProgressTurn == turn && incumbent.StepsMovedTotal > 0))
                return false; // an actor that already advanced this turn cannot be rerouted mid-step
            MissionIntentState state = MissionIntentRegistry.GetOrCreate(player);
            MissionIntentKey oldKey = incumbent.IntentKey;
            if (!state.TryGet(oldKey, out MissionIntent owned)
                || !object.ReferenceEquals(owned, incumbent))
                return false;
            HexCoord target = challenger.TargetHex.Value;
            var newKey = MissionIntentKey.ForEconomy(EconomyTaskKind.FoundBase, 0, target);
            if (state.TryGet(newKey, out MissionIntent occupied)
                && !object.ReferenceEquals(occupied, incumbent))
                return false;

            // Retarget has no displacement transaction for a third-party site lease.
            // Validate BEFORE releasing the original owner or rewriting its objective.
            if (state.All.Any(i => !object.ReferenceEquals(i, incumbent)
                    && HoldsEconomyBuildSite(i, target)))
                return false;

            string oldOwner = EconomyMissionPlanner.OwnerKey(incumbent.LastAttemptKey);
            string oldTargetOwner = InfrastructureFulfillment.EconomyReservationOwner(new AxisDemand
            {
                Capability = CapabilityKind.EconomicExpansionBase,
                TargetHex = incumbent.Economy.TargetHex,
            });
            MissionLeaseBook.ReleaseByOwner(player, turn, oldOwner);
            if (oldTargetOwner != oldOwner)
                MissionLeaseBook.ReleaseByOwner(player, turn, oldTargetOwner);
            state.Remove(oldKey);
            EconomyIntent objective = incumbent.Economy;
            objective.TargetHex = target;
            objective.BuildCard = challenger.EconomyBuildCard;
            objective.BuildResourceCost = challenger.EconomyBuildResourceCost;
            objective.BuildApCost = challenger.EconomyBuildApCost;
            objective.MinimumFollowupAp = challenger.MinimumFollowupAp;
            objective.IntrinsicValue = challenger.Value;
            objective.BuildValue = challenger.EconomySiteValue;
            objective.BuilderArmyId = incumbent.PreferredMoverArmyId;
            incumbent.IntentKey = newKey;
            incumbent.LastAttemptKey = StableMissionKey.ForEconomy(EconomyTaskKind.FoundBase, 0, target);
            incumbent.CreatedTurn = turn;
            incumbent.TurnsActive = 1;
            incumbent.LastProgressTurn = turn;
            incumbent.StallTurns = 0;
            incumbent.StepsMovedTotal = 0;
            incumbent.CumulativeApSpent = 0f;
            state.Put(incumbent);
            return true;
        }

        internal static void BeginEconomyBuilderRecovery(PlayerSetupData player,
            WorldSnapshot snap, AxisDemand completedDemand, int builderArmyId, int turn)
        {
            if (player == null || snap == null || completedDemand?.TargetHex == null)
                return;
            MissionIntentState state = MissionIntentRegistry.GetOrCreate(player);
            // With several builds legally active at once, "the first Economy
            // intent holding this actor" must resolve to the CONCRETE mission that just completed.
            // Prefer the intent whose own target matches the completed demand; only then fall back
            // to the actor match, and make that fallback deterministic instead of dictionary-order.
            List<MissionIntent> actorOwned = state.All.Where(i => i != null
                && i.Kind == MissionKind.Economy && i.PreferredMoverArmyId == builderArmyId).ToList();
            MissionIntent economy = actorOwned
                .OrderByDescending(i => i.Economy != null && completedDemand.TargetHex.HasValue
                    && i.Economy.TargetHex.Equals(completedDemand.TargetHex.Value) ? 1 : 0)
                .ThenBy(i => i.IntentKey)
                .FirstOrDefault();
            MissionIntent lender = null;
            if (economy?.Economy?.Loaned == true)
                state.TryGet(economy.Economy.LoanSource, out lender);
            if (lender == null)
                lender = state.All.FirstOrDefault(i => i != null && i.Kind != MissionKind.Economy
                    && i.PreferredMoverArmyId == builderArmyId
                    && DemandLayer.EconomyDonorStructurallyEligible(i));

            ArmySnapshot actor = snap.Self?.Armies?.FirstOrDefault(a => a != null
                && a.ArmyId == builderArmyId && a.HasHero && !a.IsPrison && !a.IsAir);
            if (actor == null)
            {
                ResumeEconomyLender(lender);
                RetireEconomyIntent(state, economy, null, turn, returnLoan: false);
                return;
            }

            EconomyTaskKind completedKind = DemandLayer.EconomyBuildKind(completedDemand);
            // The same threat witness that sizes a builder's escort: a known enemy army close
            // enough to the finished site sends the builder home.
            bool threatened = WorldAnalysis.KnownThreatsAffectingEconomyRoute(
                snap, new[] { actor.Hex }).Count > 0;
            bool alreadyProtected = (completedKind == EconomyTaskKind.FoundBase && !threatened)
                || IsProtectedEconomyHex(snap, player, actor.Hex);
            HexCoord? target = SelectEconomyRecoveryTarget(
                snap, player, actor, avoidCurrentHex: threatened);
            if (!RequiresEconomyBuilderRecovery(completedKind, lender, threatened,
                    alreadyProtected, target.HasValue))
            {
                ResumeEconomyLender(lender);
                RetireEconomyIntent(state, economy, null, turn, returnLoan: false);
                AiDebugLog.Write($"[AI][V2][Economy][Recovery] actor=#{builderArmyId} "
                    + "released at safe build hex / resumed scout");
                return;
            }

            MissionIntentKey oldKey = economy?.IntentKey ?? default;
            MissionIntent recovery = economy ?? new MissionIntent
            {
                Kind = MissionKind.Economy, CreatedTurn = turn, TurnsActive = 1,
                LastReconciledTurn = turn, PreferredMoverArmyId = builderArmyId,
            };
            recovery.Kind = MissionKind.Economy;
            recovery.Funding = CommitmentTier.Hard;
            recovery.Status = IntentStatus.Active;
            recovery.Suspended = SuspendReason.None;
            recovery.PreferredMoverArmyId = builderArmyId;
            recovery.LastProgressTurn = turn;
            recovery.StallTurns = 0;
            recovery.Objective = new EconomyIntent
            {
                Kind = EconomyTaskKind.ReturnBuilder, TargetHex = target.Value,
                BuilderArmyId = builderArmyId,
                BuildValue = completedDemand.EconomySiteValue > 0f
                    ? completedDemand.EconomySiteValue : completedDemand.Value,
                Loaned = lender != null, LoanSource = lender?.IntentKey ?? default,
            };
            recovery.IntentKey = MissionIntentKey.For(recovery);
            recovery.LastAttemptKey = StableMissionKey.ForEconomy(EconomyTaskKind.ReturnBuilder,
                recovery.IntentKey.ObjectiveId, target.Value);
            if (economy != null && !oldKey.Equals(recovery.IntentKey))
                state.Remove(oldKey);
            if (lender != null)
            {
                lender.Status = IntentStatus.Suspended;
                lender.Suspended = SuspendReason.EconomyLoan;
            }
            state.Put(recovery);
            AiDebugLog.Write($"[AI][V2][Economy][Recovery] actor=#{builderArmyId} -> "
                + $"({target.Value.Q},{target.Value.R})"
                + (lender != null ? $" lender={lender.IntentKey}" : ""));
        }

        // ATK §20/§53 — same single own-Base identity owner as SelectEconomyRecoveryTarget and
        // SelectReturnBase above.
        // Existing home ownership and current safe-route availability are different facts.
        // Keep an owned home through transient blockers; Provisioning retries its movement.
        // When ownership is lost, SelectEconomyHome requires a reachable replacement witness.

        private static bool IsProtectedEconomyHex(WorldSnapshot snap,
            PlayerSetupData player, HexCoord hex) =>
            snap?.Self?.BaseHexes != null && snap.Self.BaseHexes.Contains(hex);

        // The ONE EconomyLoan -> Active transition (repayment, supersede, orphan repair).
        private static void ResumeEconomyLender(MissionIntent lender)
        {
            if (lender != null && lender.Status == IntentStatus.Suspended
                && lender.Suspended == SuspendReason.EconomyLoan)
            {
                lender.Status = IntentStatus.Active;
                lender.Suspended = SuspendReason.None;
                AiDebugLog.Write($"[AI][V2][Economy][Loan] resume actor=#{lender.PreferredMoverArmyId} "
                    + $"to={lender.IntentKey}");
            }
        }

        // A transient suspension (the pool or a capability was unavailable on an earlier pass) is
        // re-tested on every ResolveActive pass: the planner only proposes Active intents, and
        // AdvanceIntent / ShouldReap bound how long the retry may go on. Siege and EconomyLoan
        // are owned by their own resume edges, never by this one.
        private static void ResumeTransientSuspension(MissionIntent intent)
        {
            if (intent.Status == IntentStatus.Suspended
                && (intent.Suspended == SuspendReason.PoolExhausted
                    || intent.Suspended == SuspendReason.CapabilityUnavailable))
            {
                intent.Status = IntentStatus.Active;
                intent.Suspended = SuspendReason.None;
            }
        }

        // `aggressionObjectives` is the freshly sorted, neutral-only objective list,
        // handed in exactly the way `reconObjectives` already is. It is what lets a durable Raid
        // role be RE-ORIENTED onto the next neutral once its current target is confirmed gone,
        // mirroring the Scout re-focus pattern instead of retiring and re-creating the operation.
        public static List<MissionIntent> ResolveActive(PlayerSetupData player, WorldSnapshot snap,
            IReadOnlyList<ReconObjective> reconObjectives = null,
            IReadOnlyList<RaidObjective> aggressionObjectives = null,
            AiTurnContext ctx = null)
        {
            using var __profile = new Game.Core.ProfileScope("AI/Continuity.ResolveActive");
            MissionIntentState state = MissionIntentRegistry.GetOrCreate(player);
            var pass = new ActiveResolution(state, reconObjectives, ctx?.Map == null ? null
                : (Func<HexCoord, HexCoord, int, int>)((from, to, maxMovement) =>
                    SafeStepPathing.FindSafePathCost(ctx.Map, player, from, to, maxMovement)));
            if (state.Count == 0)
            {
                FinalizeAirSupportResolution(player, snap, pass);
                return pass.Active;
            }
            foreach (var prepare in ResolutionPreparers) prepare(player, snap, pass);
            pass.ActorClaims = ActorCommitments.FromIntents(state.All, snap, reconObjectives).ClaimedArmyIdSet;
            foreach (MissionIntent intent in state.All.ToList())
            {
                if (pass.DonatedOperations.Contains(intent.IntentKey)) continue;
                if (ActiveResolvers.TryGetValue(intent.Kind, out var resolve)) resolve(player, snap, intent, pass);
                else ResolveScoutOperation(player, snap, intent, pass);
            }
            RetireRecruitedReturnFallbacks(player, snap, pass);
            foreach (MissionIntentKey key in pass.Dead) state.Remove(key);
            // In-place rekeys preserve the same durable intent and accumulated progress.
            foreach ((MissionIntentKey oldKey, MissionIntent intent) in pass.Rekeys)
            {
                state.Remove(oldKey);
                state.Put(intent);
            }
            pass.Active.Sort((x, y) =>
            {
                int c = y.Funding.CompareTo(x.Funding); if (c != 0) return c;
                c = x.CreatedTurn.CompareTo(y.CreatedTurn); if (c != 0) return c;
                return x.IntentKey.CompareTo(y.IntentKey);
            });
            foreach (var finalize in ResolutionFinalizers) finalize(player, snap, pass);
            return pass.Active;
        }

        // A mover that already advanced this turn owns its lane through the productive typed loop.
        // Reconciliation may run repeatedly after each step; trimming it here would manufacture
        // surplus churn and immediately recreate effectively the same intent.
        internal static bool IsProductiveReconLaneThisTurn(MissionIntent intent, int turn) =>
            intent != null && intent.Kind == MissionKind.Scout && intent.Scout != null
            && intent.PreferredMoverArmyId.HasValue && intent.LastProgressTurn == turn;

        // The CapabilityUnavailable stall exemption protects a Recon lane whose OWN actor is only
        // momentarily busy / out of MP. A Scout intent with no bound actor has no such actor to wait
        // for: every failed turn is genuine idleness and must age toward ShouldReap (audit F4).
        internal static bool IsMoverlessScoutRole(MissionIntent intent) =>
            intent != null && intent.Kind == MissionKind.Scout && intent.Scout != null
            && !ReconScoutKinds.IsAirSweep(intent.Scout.Kind)
            && !intent.PreferredMoverArmyId.HasValue;

        // §P1 — GRADUAL contraction of durable Scout lanes toward desired concurrency: at most
        // maxReconLaneTrimPerTurn shed per turn, only Soft/None-funded lanes, and the target floor
        // already accounts for any Hard-funded lanes that are being kept regardless.
        private static void TrimSurplusReconLanes(PlayerSetupData player, List<MissionIntent> active,
            MissionIntentState state, WorldSnapshot snap, IReadOnlyList<ReconObjective> reconObjectives)
        {
            var airActorIds = new HashSet<int>((snap?.Self?.Armies ?? System.Array.Empty<ArmySnapshot>())
                .Where(a => a != null && a.IsAir).Select(a => a.ArmyId));
            // DesiredTotal/HardCap govern physical ground scout lanes. Air intents use the
            // independent aviation capacity policy and must survive this contraction pass.
            // AirSweep is an aviation operation even when its wing has landed back into storage.
            var scoutLanes = active.Where(i => i.Kind == MissionKind.Scout && i.Scout != null
                && !ReconScoutKinds.IsCapture(i.Scout.Kind)
                && !ReconScoutKinds.IsAirSweep(i.Scout.Kind)
                && (!i.PreferredMoverArmyId.HasValue || !airActorIds.Contains(i.PreferredMoverArmyId.Value)))
                .ToList();
            if (scoutLanes.Count == 0)
                return;

            var runnable = reconObjectives
                .Where(o => o != null && o.Kind != ReconObjectiveKind.CaptureStructure && o.BaseValue > 0f)
                .OrderByDescending(o => o.BaseValue)
                .ThenBy(o => o.IntentKey)
                .ToList();
            int desired = ReconConcurrencyPolicy.DesiredTotal(snap, runnable);

            var shedable = scoutLanes
                .Where(i => i.Funding < CommitmentTier.Hard
                    && !IsProductiveReconLaneThisTurn(i, snap.TurnNumber))
                // A lane without a bound actor occupies no physical scout, so it is shed first;
                // then the lane that has gone longest without progress (audit F4). Only after
                // that the original Funding / newest-first order.
                .OrderByDescending(i => i.PreferredMoverArmyId.HasValue ? 0 : 1)
                .ThenBy(i => i.LastProgressTurn)
                .ThenBy(i => (int)i.Funding)
                .ThenByDescending(i => i.CreatedTurn)
                .ThenByDescending(i => i.StallTurns)
                .ThenByDescending(i => i.IntentKey)
                .ToList();
            int hardKept = scoutLanes.Count - shedable.Count;
            // How many shedable lanes exceed the room left under `desired` after the Hard lanes.
            int surplus = shedable.Count - System.Math.Max(0, desired - hardKept);
            if (surplus <= 0)
                return;

            int dropped = 0;
            foreach (MissionIntent v in shedable)
            {
                if (dropped >= surplus || !ReconTurnStateStore.For(player, snap.TurnNumber).TryConsumeReconLaneTrim(snap.TurnNumber))
                    break;
                state.Remove(v.IntentKey);
                active.Remove(v);
                if (v.PreferredMoverArmyId.HasValue)
                {
                    // A contraction decision is turn-wide. Do not let the same actor immediately
                    // acquire a fresh Recon mission later in this turn's bounded replans.
                    ReconTurnStateStore.For(player, snap.TurnNumber).MarkReconActorTrimmed(snap.TurnNumber,
                        v.PreferredMoverArmyId.Value);
                    ReconPatrolStateRegistry.Retire(player,
                        v.PreferredMoverArmyId.Value, "recon lane surplus trim");
                }
                dropped++;
                AiDebugLog.Write($"[AI][V2] continuity — {v.IntentKey} retired: recon lane surplus "
                    + $"(active {scoutLanes.Count}, hard {hardKept}, desired {desired}, "
                    + $"shed {dropped}/{surplus} this pass; per-turn quota enforced)");
            }
        }

        // Spec §1 — re-point a stale ground scout intent's live waypoint at the nearest still-
        // runnable hex of its own kind, avoiding hexes already owned by another scout intent.
        // Mutates s.FocusHex and the shared ownedFoci set. Returns false only when nothing runnable
        // remains, in which case the caller retires the intent.
        private static bool TryRefocusScoutIntent(WorldSnapshot snap, ScoutIntent s, HashSet<HexCoord> ownedFoci)
        {
            // AirSweep has no waypoint to re-point: its anchor is re-derived every turn, and an
            // invalid sweep (no enemy anchor at all) simply retires.
            if (snap?.MapKnowledge == null || s == null || ReconScoutKinds.IsAirSweep(s.Kind))
                return false;

            HexCoord old = s.FocusHex;
            HexCoord? pick = null;
            int bestDist = int.MaxValue;
            // Recon S3 — prefer a waypoint at least scoutTargetMinSeparation from every other
            // scout's focus (the spacing Assignment keeps between lanes); only when none exists
            // fall back to the nearest runnable hex.
            HexCoord? spacedPick = null;
            int spacedDist = int.MaxValue;
            bool Spaced(HexCoord h)
            {
                foreach (HexCoord other in ownedFoci)
                    if (!other.Equals(old)
                        && HexGridMath.Distance(other, h) < AiConfigV2.scoutTargetMinSeparation)
                        return false;
                return true;
            }
            void Consider(HexCoord h)
            {
                int d = HexGridMath.Distance(old, h);
                if (d < bestDist) { bestDist = d; pick = h; }
                if (d < spacedDist && Spaced(h)) { spacedDist = d; spacedPick = h; }
            }

            if (ReconScoutKinds.IsCapture(s.Kind)) return false;
            if (ReconScoutKinds.IsRefresh(s.Kind))
            {
                foreach (KeyValuePair<HexCoord, int> kv in ReconIntelSnapshotRegistry.LastObservedFor(snap))
                {
                    if (kv.Key.Equals(old) || ownedFoci.Contains(kv.Key))
                        continue;
                    int age = System.Math.Max(0, snap.TurnNumber - kv.Value);
                    if (!ReconIntelSnapshotRegistry.IsStaleAge(age))
                        continue;
                    if (!ScoutObjectiveEvaluator.IsRefreshFocusRunnable(snap, kv.Key))
                        continue;
                    Consider(kv.Key);
                }
            }
            else
            {
                if (snap.MapKnowledge.Frontier == null)
                    return false;
                foreach (FrontierHexSnapshot f in snap.MapKnowledge.Frontier)
                {
                    if (f.Hex.Equals(old) || ownedFoci.Contains(f.Hex))
                        continue;
                    if (!ScoutObjectiveEvaluator.IsExploreFocusRunnable(snap, f.Hex))
                        continue;
                    Consider(f.Hex);
                }
            }

            if (spacedPick.HasValue)
                pick = spacedPick;
            if (pick == null)
                return false;
            ownedFoci.Remove(old);
            ownedFoci.Add(pick.Value);
            s.FocusHex = pick.Value;
            return true;
        }

        // =====================================================================================
        //  The Raid phase machine and its actor/base helpers.
        // =====================================================================================

        // Is the durable primary still the kind of army ground-combat provisioning (Raid, Attack)
        // would accept? Uses the SAME structural snapshot predicate ActorCommitments applies in
        // combat phases.
        internal static bool GroundCombatPrimaryAlive(WorldSnapshot snap, int armyId)
        {
            ArmySnapshot a = snap?.Self?.Armies?.FirstOrDefault(x => x != null && x.ArmyId == armyId);
            return a != null && a.IsStructuralRaidActor;
        }

        public static List<Commitment> BindFunding(IReadOnlyList<MissionIntent> activeIntents,
            IReadOnlyList<MissionProposal> proposals, WorldSnapshot snapshot = null,
            IReadOnlyDictionary<MissionIntentKey, string> deferredThisPass = null)
        {
            var commitments = new List<Commitment>();
            if (activeIntents == null || proposals == null)
                return commitments;

            var byKey = new Dictionary<MissionIntentKey, MissionProposal>();
            foreach (MissionProposal p in proposals)
                if (p != null)
                    byKey[MissionIntentKey.For(p)] = p;

            foreach (MissionIntent intent in activeIntents)
            {
                if (intent.Funding == CommitmentTier.None)
                    continue;
                if (!byKey.TryGetValue(intent.IntentKey, out MissionProposal p))
                {
                    // Missions owns the reason an otherwise-live durable intent deliberately has
                    // no executable proposal in THIS settled pass. Continuity must never re-run
                    // lane-specific eligibility here: that would duplicate planner logic and let
                    // diagnostics drift from the decision that actually withheld the proposal.
                    string deferred = null;
                    if (deferredThisPass != null)
                        deferredThisPass.TryGetValue(intent.IntentKey, out deferred);
                    if (!string.IsNullOrWhiteSpace(deferred))
                    {
                        AiDebugLog.WriteDeduped(intent.IntentKey.ToString(),
                            $"[AI][V2] continuity — DEFER {intent.IntentKey} ({intent.Funding}) "
                            + $"no step this pass; reason={deferred}");
                        continue;
                    }
                    AiDebugLog.WriteDeduped(intent.IntentKey.ToString(),
                        $"[AI][V2] continuity — WARN {intent.IntentKey} ({intent.Funding}) "
                        + "not materialised this turn; no planner deferral or funding bound");
                    continue;
                }
                commitments.Add(new Commitment
                {
                    IntentKey = intent.IntentKey,
                    Mission = p,
                    Tier = intent.Funding,
                    ContinuationValue = p.BaseValue,
                    SwitchingCost = 0f,
                });
            }
            return commitments;
        }

        // Mid-turn variant: apply exactly one settled outcome without aging, stalling or
        // reaping unrelated intents. ReconcileAfterTurn remains the sole end-of-turn sweep owner.
        public static void ReconcileStep(PlayerSetupData player, int turn,
            MissionStepResult outcome)
        {
            if (player == null || outcome == null)
                return;
            ReconcileOutcome(MissionIntentRegistry.GetOrCreate(player),
                AiAllocatorStateRegistry.GetOrCreate(player), outcome, turn);
        }

        // StrategicPhaseA's ProtectActiveEconomyBuild reserves this intent's physical resources
        // and claims its card every cycle it is still committed — a real per-turn touch of the
        // intent's lifecycle, just one that has nothing to execute yet (builder already at/near
        // target, simply waiting for H/E/M/T to accumulate). Without this call that touch is
        // invisible to Continuity: the intent produces no MissionStepResult this turn, and
        // ReconcileAfterTurn's "unseen" branch grows StallTurns for it via the SAME raw idle
        // counter used for an abandoned project — reaping a still-legal, still-funded build that
        // is doing exactly the right thing (holding, not thrashing) after 2 quiet turns.
        //
        // Deliberately stamps LastProtectedTurn, NOT LastReconciledTurn: the unseen branch's own
        // `LastReconciledTurn == turn` guard skips its ENTIRE per-turn block, ShouldReap included,
        // so reusing that field here would also switch off the intent's absolute-age reap cap
        // (commitmentMaxTurns) for as long as it stays protected — trading an over-eager reap for
        // no reap at all. LastProtectedTurn only ever suppresses that one turn's StallTurns++;
        // TurnsActive and ShouldReap still run every turn, so a build that never becomes
        // affordable is still bounded by its ordinary age cap, just not punished for waiting.
        public static void MarkProtectedThisTurn(PlayerSetupData player, MissionIntentKey key, int turn)
        {
            if (player == null)
                return;
            MissionIntentState state = MissionIntentRegistry.GetOrCreate(player);
            if (state.TryGet(key, out MissionIntent intent))
                intent.LastProtectedTurn = turn;
        }

        public static void ReconcileAfterTurn(PlayerSetupData player, int turn,
            IReadOnlyList<MissionStepResult> outcomes)
        {
            MissionIntentState state = MissionIntentRegistry.GetOrCreate(player);
            AiAllocatorState allocState = AiAllocatorStateRegistry.GetOrCreate(player);
            var seen = new HashSet<MissionIntentKey>();

            foreach (MissionStepResult result in outcomes ?? Array.Empty<MissionStepResult>())
            {
                MissionStepResult o = result;
                seen.Add(o.IntentKey);
                ReconcileOutcome(state, allocState, o, turn);
            }

            foreach (MissionIntent intent in state.All.ToList())
            {
                if (seen.Contains(intent.IntentKey))
                    continue;
                if (intent.Status == IntentStatus.Suspended
                    && (intent.Suspended == SuspendReason.Siege
                        || intent.Suspended == SuspendReason.CapabilityUnavailable
                        || intent.Suspended == SuspendReason.EconomyLoan)
                    && intent.Kind != MissionKind.Development)
                    continue;

                if (intent.LastReconciledTurn == turn)
                    continue;

                intent.LastReconciledTurn = turn;
                intent.TurnsActive++;
                // A committed Economy build Phase A is still protecting this very turn (reserving
                // its resources, holding its card) has real, legitimate activity even though it
                // produced no MissionStepResult — it simply has nothing executable yet while H/E/
                // M/T accumulate. That is not the same "nothing happened" as an abandoned/invalid
                // intent, so it must not spend the same StallTurns budget. See MarkProtectedThisTurn.
                bool protectedWaitingOnResources = intent.LastProtectedTurn == turn;
                if (intent.Suspended != SuspendReason.PoolExhausted && !protectedWaitingOnResources)
                    intent.StallTurns++;

                if (ShouldReap(intent, turn))
                {
                    RetireOutcomeIntent(state, intent, null, turn);
                    StartPersistentCooldown(allocState, intent.LastAttemptKey, intent.Kind, turn, "IntentReapedIdle");
                    AiDebugLog.Write($"[AI][V2] continuity — {intent.IntentKey} reaped (idle: "
                        + $"{ReapProgress(intent)})");
                }
            }
        }

        private static void ReconcileOutcome(MissionIntentState state,
            AiAllocatorState allocState, MissionStepResult o, int turn)
        {
            state.TryGet(o.IntentKey, out MissionIntent intent);
            string aid = AiV2Trace.FormatCorrelation(o.Proposal);
            AiDebugLog.Write($"[AI][V2] [{aid}] outcome {o.Disposition}"
                + (o.ObjectiveSatisfied ? " satisfied" : "")
                + (o.Disposition == MissionStepDisposition.PermanentFailure ? " structural" : "")
                + $" {o.IntentKey}");
            RecordDomainStepProgress(state, o, turn);
            if (TryDomainTransition(SideLegTransitions, state, allocState, intent, o, turn)) return;

            if (o.Disposition == MissionStepDisposition.Completed && o.ObjectiveSatisfied)
            {
                if (TryDomainTransition(CompletionTransitions, state, allocState, intent, o, turn)) return;
                if (intent != null)
                    AiDebugLog.Write($"[AI][V2] continuity — [{aid}] {o.IntentKey} COMPLETED, retired");
                RetireOutcomeIntent(state, intent, o, turn);
                return;
            }
            if (o.Disposition == MissionStepDisposition.PermanentFailure)
            {
                if (TryDomainTransition(RecoveryTransitions, state, allocState, intent, o, turn)) return;
                RetireOutcomeIntent(state, intent, o, turn);
                string reason = o.ProvisionFailureKindValue?.ToString() ?? "StructuralFailure";
                StartPersistentCooldown(allocState, o.AttemptKey, o.MissionKind, turn, reason);
                AiDebugLog.Write($"[AI][V2] continuity — [{aid}] {o.IntentKey} structural failure ({reason}), retired + cooldown");
                return;
            }
            if (o.Disposition == MissionStepDisposition.Invalidated)
            {
                if (TryDomainTransition(RecoveryTransitions, state, allocState, intent, o, turn)) return;
                if (intent != null)
                    AiDebugLog.Write($"[AI][V2] continuity — [{aid}] {o.IntentKey} failed ({Describe(o)}), retired");
                RetireOutcomeIntent(state, intent, o, turn);
                return;
            }
            if (TryDomainTransition(NoProgressTransitions, state, allocState, intent, o, turn)) return;
            if (intent != null) AdvanceIntent(intent, o, turn, state, allocState);
            else TryDomainTransition(CreationTransitions, state, allocState, null, o, turn);
        }

        private static void AdvanceIntent(MissionIntent intent, MissionStepResult o, int turn,
            MissionIntentState state, AiAllocatorState allocState)
        {
            bool firstReconcileThisTurn = intent.LastReconciledTurn != turn;
            if (firstReconcileThisTurn)
            {
                intent.LastReconciledTurn = turn;
                intent.TurnsActive++;
            }
            intent.LastAttemptKey = o.AttemptKey;
            if (o.Proposal != null && o.Proposal.BaseValue > 0f)
                intent.LastIntrinsicValue = o.Proposal.BaseValue;
            intent.CumulativeApSpent += o.ApSpent;
            intent.StepsMovedTotal += o.StepsMoved;
            if (o.MoverArmyId.HasValue)
            {
                foreach (var observe in BeforeMoverObservers) observe(state, allocState, intent, o, turn);
                if (!TryDomainTransition(MoverTransitions, state, allocState, intent, o, turn))
                    intent.PreferredMoverArmyId = o.MoverArmyId;
            }

            foreach (var observe in IntentFactObservers) observe(state, allocState, intent, o, turn);

            bool poolExhausted = o.AllocationDeferReason == DeferReason.CommitmentPoolExhausted;
            bool capabilityUnavailable =
                o.ProvisionFailureKindValue == ProvisionFailureKind.NoMoverExists
                || o.ProvisionFailureKindValue == ProvisionFailureKind.MoverContended;

            if (o.MadeProgress)
            {
                intent.LastProgressTurn = turn;
                intent.StallTurns = 0;
            }
            else if (firstReconcileThisTurn && !poolExhausted
                && (!capabilityUnavailable || DomainAgesCapabilityFailure(intent, forReaping: false)))
            {
                intent.StallTurns++;
            }

            if (poolExhausted)
            {
                intent.Status = IntentStatus.Suspended;
                intent.Suspended = SuspendReason.PoolExhausted;
            }
            else if (capabilityUnavailable)
            {
                intent.Status = IntentStatus.Suspended;
                intent.Suspended = SuspendReason.CapabilityUnavailable;

                if (TryDomainTransition(CapabilityFailureTransitions, state, allocState, intent, o, turn)) return;
            }

            // A Raid keeps its absolute age cap (raidIntentMaxTurns) through capability
            // suspensions: ResolveActive resumes it every pass, so without the cap a Raid waiting
            // on a support no stage can bind was held — primary claimed — forever.
            if ((!capabilityUnavailable || DomainAgesCapabilityFailure(intent, forReaping: true))
                && ShouldReap(intent, turn))
            {
                RetireOutcomeIntent(state, intent, o, turn);
                StartPersistentCooldown(allocState, intent.LastAttemptKey, intent.Kind, turn, "IntentReapedStall");
                AiDebugLog.Write($"[AI][V2] continuity — [{AiV2Trace.FormatCorrelation(o.Proposal)}] {intent.IntentKey} reaped ("
                    + $"{ReapProgress(intent)})");
            }
            else
            {
                AiDebugLog.Write($"[AI][V2] continuity — [{AiV2Trace.FormatCorrelation(o.Proposal)}] {intent.IntentKey} advanced "
                    + $"({o.Disposition}, progress {(o.MadeProgress ? 1 : 0)}, t{intent.TurnsActive} stall{intent.StallTurns}"
                    + (capabilityUnavailable ? $", suspended CapabilityUnavailable:{o.ProvisionFailureKindValue}" : "") + ")");
            }
        }

        // Shared skeleton for the three Create*Intent methods below — was three independent,
        // hand-written copies of the same 12-field MissionIntent construction (see
        // Docs/ai-duplicate-methods-analysis.md, group M). Collapsing them here means a future
        // field added to MissionIntent only has to be wired up once, instead of risking a silently
        // half-initialized intent from a copy nobody remembered to update.
        private static MissionIntent NewIntent(MissionStepResult o, int turn, MissionKind kind,
            CommitmentTier funding, object objective)
        {
            return new MissionIntent
            {
                IntentKey = o.IntentKey,
                LastAttemptKey = o.AttemptKey,
                Kind = kind,
                Funding = funding,
                Status = IntentStatus.Active,
                Suspended = SuspendReason.None,
                Objective = objective,
                CreatedTurn = turn,
                TurnsActive = 1,
                LastReconciledTurn = turn,
                LastProgressTurn = turn,
                StallTurns = 0,
                CumulativeApSpent = o.ApSpent,
                StepsMovedTotal = o.StepsMoved,
                // For a ground-combat payload this setter IS the payload's PrimaryArmyId: keep a
                // primary the payload already names (an Attack Gather is born from a SUPPORT's
                // step, so the mover is not the host), and fall back to the mover only when the
                // payload has none (Raid).
                PreferredMoverArmyId = (objective as IGroundCombatOperation)?.PrimaryArmyId
                    ?? o.MoverArmyId,
            };
        }



        // Retirement of whatever intent an outcome names: an Economy intent through its own owner
        // above, any other kind is simply removed (a loan can only point at an Economy borrower).
        private static void RetireOutcomeIntent(MissionIntentState state, MissionIntent intent,
            MissionStepResult outcome, int turn)
        {
            foreach (var prepare in RetirementPreparers) prepare(state, null, intent, outcome, turn);
            state.Remove(intent?.IntentKey ?? outcome.IntentKey, turn);
        }

        // Is an already-collecting actor still worth its site? Analysis' admission test for a
        // NEW collector (UsefulMarginalIncomeGain), judged against own income without this
        // collector's own contribution.
        internal static bool CollectorStillUseful(WorldSnapshot snap, ResourceType type,
            float contribution)
        {
            foreach (EconomyResourceStanding standing in snap?.Economy?.PerType
                         ?? System.Array.Empty<EconomyResourceStanding>())
                if (standing.Type == type)
                    return standing.UsefulRetainedIncomeGain(contribution)
                        > AiConfigV2.allocatorSliceEpsilon;
            return false;
        }

        // The limits ShouldReap applies to this intent, for the reap log line. A Raid is judged by
        // the raid* caps and Scout/Attack have no absolute age cap, so printing the shared
        // commitment* limits for every kind (e.g. "age 10/6" for a Raid) misreported the cause.
        private static string ReapProgress(MissionIntent i)
        {
            if (i.Kind == MissionKind.Raid)
                return $"stall {i.StallTurns}/{AiConfigV2.raidIntentStallTurns}, "
                    + $"age {i.TurnsActive}/{AiConfigV2.raidIntentMaxTurns}";
            if (i.Kind == MissionKind.Scout || i.Kind == MissionKind.Attack)
                return $"stall {i.StallTurns}/{AiConfigV2.commitmentStallTurns}, age {i.TurnsActive} (no age cap)";
            return $"stall {i.StallTurns}/{AiConfigV2.commitmentStallTurns}, "
                + $"age {i.TurnsActive}/{AiConfigV2.commitmentMaxTurns}";
        }

        private static bool ShouldReap(MissionIntent i, int turn)
        {
            if (i.Kind == MissionKind.Raid)
                return i.StallTurns >= AiConfigV2.raidIntentStallTurns
                    || i.TurnsActive >= AiConfigV2.raidIntentMaxTurns;
            // Explore/Refresh are durable roles whose waypoint is re-focused by ResolveActive.
            // Productive movement resets StallTurns; absolute age must not turn that success into
            // IntentReapedStall. Objective exhaustion/invalidity is handled separately above.
            // Attack owns its full lifecycle (target validity, live primary, Gather/Reinforcement/
            // RecoveryReturn) and a gather plus a long march legitimately outlives the generic age
            // cap; only a real stall ends it here.
            if (i.Kind == MissionKind.Scout || i.Kind == MissionKind.Attack)
                return i.StallTurns >= AiConfigV2.commitmentStallTurns;
            // Economy audit B10 — an Economy obligation that advances every turn (a long walk to
            // a site, a collector holding its site, a builder walking home) is not aged out by
            // absolute age; the cap bounds time WITHOUT progress instead, which still ends a build
            // that waits (protected, unaffordable) longer than commitmentMaxTurns.
            if (i.Kind == MissionKind.Economy)
                return i.StallTurns >= AiConfigV2.commitmentStallTurns
                    || turn - i.LastProgressTurn >= AiConfigV2.commitmentMaxTurns;
            return i.StallTurns >= AiConfigV2.commitmentStallTurns
                || i.TurnsActive >= AiConfigV2.commitmentMaxTurns;
        }

        private static void StartPersistentCooldown(AiAllocatorState state, StableMissionKey key,
            MissionKind kind, int turn, string reason)
        {
            int duration = kind == MissionKind.Raid
                ? AiConfigV2.raidRejectCooldownTurns
                : AiConfigV2.allocatorRejectCooldownTurns;
            int until = turn + duration;
            state.StartCooldown(key, turn, until, reason);
            AiDebugLog.Write($"[AI][V2] cooldown — {key} reason={reason} start=t{turn} until=t{until} duration={duration}");
        }

        private static string Describe(MissionStepResult o) =>
            o.Proposal != null && o.Proposal.Target is ScoutMissionTarget t
                ? ReconScoutKinds.Name(t.Kind)
                : o.Proposal != null && o.Proposal.Target is EconomyMissionTarget e
                    ? $"{e.Kind}@{e.TargetHex.Q},{e.TargetHex.R}"
                    : "?";
    }
}


