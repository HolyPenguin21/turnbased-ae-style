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
    // not a partial class.
    internal static partial class MissionContinuityLayer
    {
        internal static HexCoord? SelectEconomyRecoveryTarget(WorldSnapshot snap,
            PlayerSetupData player, ArmySnapshot actor, bool avoidCurrentHex = false)
        {
            if (snap?.Self?.BaseHexes == null || actor == null || player == null)
                return null;
            // ATK §20/§53 — one own-Base identity owner for EVERY consumer, Economy recovery
            // included. Self.BaseHexes is current truth; a remembered KnownBuilding.Owner could
            // send a retreating builder to a base we had already lost, or hide one we had just
            // built or captured.
            IEnumerable<HexCoord> candidates = snap.Self.BaseHexes;
            if (avoidCurrentHex && candidates.Any(h => !h.Equals(actor.Hex)))
                candidates = candidates.Where(h => !h.Equals(actor.Hex));
            HexCoord citadel = snap.Self.Citadel;
            List<HexCoord> ordered = candidates
                .OrderBy(h => HexGridMath.Distance(actor.Hex, h))
                .ThenBy(h => DemandLayer.EconomyRecoveryThreatExposure(snap, h))
                .ThenByDescending(h => h.Equals(citadel) ? 1 : 0)
                .ThenBy(h => h.Q).ThenBy(h => h.R).ToList();
            if (ordered.Count > 0)
                return ordered[0];
            return player.CitadelHexQ.HasValue && player.CitadelHexR.HasValue
                ? new HexCoord(player.CitadelHexQ.Value, player.CitadelHexR.Value)
                : (HexCoord?)null;
        }

        internal static bool RequiresEconomyBuilderRecovery(EconomyTaskKind completedKind,
            MissionIntent lender, bool underImmediateThreat, bool alreadyProtected,
            bool hasRecoveryTarget)
        {
            if (!hasRecoveryTarget) return false;
            if (alreadyProtected && !underImmediateThreat) return false;
            if (lender?.Kind == MissionKind.Scout && lender.Scout != null
                && lender.Scout.Kind != ScoutTargetKind.Surveil && !underImmediateThreat)
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
            StrategicResourceReservationLedger.ReleaseByOwner(player, turn, oldOwner);
            if (oldTargetOwner != oldOwner)
                StrategicResourceReservationLedger.ReleaseByOwner(player, turn, oldTargetOwner);
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
            bool threatened = DemandLayer.EconomyBuilderUnderImmediateThreat(snap, actor.Hex);
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
        // AdvanceIntent / ShouldReap bound how long the retry may go on. Siege, EconomyLoan and
        // ActiveDefencePreemption are owned by their own resume edges, never by this one.
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
            IReadOnlyList<AggressionObjective> aggressionObjectives = null,
            AiTurnContext ctx = null)
        {
            var active = new List<MissionIntent>();
            Func<HexCoord, HexCoord, int, int> safeRouteCost = ctx?.Map == null ? null
                : (Func<HexCoord, HexCoord, int, int>)((from, to, maxMovement) =>
                    SafeStepPathing.FindSafePathCost(ctx.Map, player, from, to, maxMovement));
            MissionIntentState state = MissionIntentRegistry.GetOrCreate(player);
            if (state.Count == 0)
            {
                GroundCombatAirSupport.ReleaseOrphanStrikes(player, state.All);
                return active;
            }

            bool underSiege = snap?.Threat?.UnderSiege == true;
            var dead = new List<MissionIntentKey>();
            var rekeys = new List<(MissionIntentKey Old, MissionIntent Intent)>();
            // There is deliberately NO global "primary" build selection here. ResolveActive
            // validates each build intent on its OWN facts (objective completed / target still
            // legal / actor still alive / route still usable). Progress on one site is not evidence
            // that another site's obligation became illegal; real ownership conflicts (same actor,
            // same objective identity, same physical card) are resolved where ownership is actually
            // granted — BeginEconomyDelivery — not by retiring bystanders here.
            var liveLoanSources = new HashSet<MissionIntentKey>(state.All
                .Where(i => i?.Kind == MissionKind.Economy && i.Economy?.Loaned == true)
                .Select(i => i.Economy.LoanSource));
            foreach (MissionIntent orphanedDonor in state.All.Where(i => i != null
                && i.Status == IntentStatus.Suspended && i.Suspended == SuspendReason.EconomyLoan
                && !liveLoanSources.Contains(i.IntentKey)))
            {
                AiDebugLog.Write($"[AI][V2][Economy][Loan] orphan repair donor={orphanedDonor.IntentKey}");
                ResumeEconomyLender(orphanedDonor);
            }

            // Strike force — a Raid / ActiveDefence whose primary an Attack gather bought ends here.
            // The gather priced the abandoned operation into its own score
            // (GroundCombatDonorPolicy.BorrowableDonorApPrices) and won the allocation; the army now
            // walks to the host and, after the handoff, home. Runs before the orphan repair below,
            // so a Raid an ActiveDefence had borrowed is resumed (and, if its army is the one given
            // away, retired by this same rule on the next pass).
            var givenToGather = new HashSet<int>(state.All
                .Where(i => i?.Kind == MissionKind.Attack && i.Status == IntentStatus.Active
                    && i.Attack?.Phase == AttackMissionPhase.Gather)
                .SelectMany(i => i.Attack.GatherSupportArmyIds));
            var donatedOperations = new HashSet<MissionIntentKey>();
            foreach (MissionIntent lender in state.All.Where(i => i != null
                && (i.Kind == MissionKind.Raid || i.Kind == MissionKind.ActiveDefence)
                && i.PreferredMoverArmyId.HasValue
                && givenToGather.Contains(i.PreferredMoverArmyId.Value)))
            {
                donatedOperations.Add(lender.IntentKey);
                dead.Add(lender.IntentKey);
                AiDebugLog.Write($"[AI][V2][Attack][Gather] continuity — {lender.IntentKey} retired: "
                    + $"its army #{lender.PreferredMoverArmyId} was given to an Attack gather "
                    + $"(abandoned value {lender.LastIntrinsicValue:0.00})");
            }

            // The same orphan repair for the Raid an ActiveDefence borrowed.
            // Every ordinary ActiveDefence exit resumes its lender explicitly, but if the defending
            // intent is gone without one of those exits having run (retired on another path, or its
            // record dropped), the Raid would stay ActiveDefencePreemption-suspended forever while
            // its army is free. Repair reuses the SAME suspend/resume state machine; it never
            // resumes a Raid that a LIVE defence still borrows, so a raid can never be resumed
            // twice into an active defence.
            var liveBorrowedRaids = new HashSet<MissionIntentKey>(state.All
                .Where(i => i?.Kind == MissionKind.ActiveDefence
                    && i.ActiveDefence?.SuspendedOffensiveIntentKey.HasValue == true)
                .Select(i => i.ActiveDefence.SuspendedOffensiveIntentKey.Value));
            // ATK §49/§73 — the same repair covers every OFFENSIVE intent a defence may borrow
            // from, not Raid alone, so a preempted Attack can never be stranded suspended while its
            // army is free.
            foreach (MissionIntent orphaned in state.All.Where(i => i != null
                && IsOffensiveGroundCombatIntent(i) && i.Status == IntentStatus.Suspended
                && i.Suspended == SuspendReason.ActiveDefencePreemption
                && !liveBorrowedRaids.Contains(i.IntentKey)))
            {
                orphaned.Status = IntentStatus.Active;
                orphaned.Suspended = SuspendReason.None;
                AiDebugLog.Write($"[AI][V2][ActiveDefence][Continuity] decision=RESUME "
                    + $"offensive={orphaned.IntentKey} reason=orphan_repair");
            }

            // Spec §1 — foci currently owned by ground scout intents, so a re-focus never lands two
            // durable intents on the same waypoint. Mutated as intents are re-pointed below.
            var scoutFoci = new HashSet<HexCoord>();
            foreach (MissionIntent i in state.All)
                if (i.Scout != null && i.Scout.Kind != ScoutTargetKind.Surveil
                    && !ReconScoutKinds.IsAirSweep(i.Scout.Kind))
                    scoutFoci.Add(i.Scout.FocusHex);

            // Neutral targets already owned by a durable Raid, so a re-orientation
            // never lands two Raid operations on the same neutral target (either kind).
            HashSet<int> raidClaims = ActorCommitments.FromIntents(state.All, snap,
                reconObjectives).ClaimedArmyIdSet;
            HashSet<int> attackGatherUnavailable = null;

            foreach (MissionIntent intent in state.All.ToList())
            {
                if (donatedOperations.Contains(intent.IntentKey))
                    continue;
                if (intent.Kind == MissionKind.Development)
                {
                    DevelopmentIntent d = intent.Development;
                    ArmyData actual = d?.Hero == null ? null : ArmyRegistry.AllForOwner(player)
                        .FirstOrDefault(a => a != null && !a.IsPrison
                            && a.Members.Contains(d.Hero));
                    BuildingData building = d == null ? null : BuildingRegistry.FindAt(d.FacilityHex);
                    bool valid = d != null && actual != null && d.Hero.Owner == player
                        && !d.Hero.IsPrisoner && d.Hero.IsHero
                        && d.Hero.HasAbility(ResearchProductionSystem.RoleAbility(d.Mode))
                        && string.Equals(d.HeroKey, GenerationSource.StableHeroKey(d.Hero),
                            System.StringComparison.Ordinal)
                        && building != null && building.Owner == player
                        && building.HasFacilityWithAbility(ResearchProductionSystem.FacilityAbility(d.Mode))
                        && (intent.PreferredMoverArmyId == actual.Id
                            || !intent.PreferredMoverArmyId.HasValue);
                    bool arrived = valid && ResearchProductionSystem.ActorStillQualifies(
                        player, d.Hero, d.FacilityHex, d.Mode)
                        && ResearchProductionSystem.IsEligible(player, d.FacilityHex, d.Mode, out _);
                    if (!valid || arrived || ShouldReap(intent, snap?.TurnNumber ?? 0))
                    {
                        dead.Add(intent.IntentKey);
                        AiDebugLog.Write($"[AI][V2][Development] retire {intent.IntentKey} "
                            + $"valid={valid} arrived={arrived} stall={intent.StallTurns}");
                        continue;
                    }
                    ResumeTransientSuspension(intent);
                    active.Add(intent);
                    continue;
                }
                if (intent.Kind == MissionKind.Economy)
                {
                    EconomyIntent ei = intent.Economy;
                    bool collectorMission = ei?.Kind == EconomyTaskKind.MobileCollection
                        || ei?.Kind == EconomyTaskKind.ReturnCollector;
                    ArmySnapshot actor = snap?.Self?.Armies?.FirstOrDefault(a => a != null
                        && a.ArmyId == intent.PreferredMoverArmyId && !a.IsPrison && !a.IsAir
                        && (collectorMission || a.HasHero));
                    if (ei?.Kind == EconomyTaskKind.MobileCollection)
                    {
                        bool capable = actor != null && ei.ResourceType.HasValue
                            && actor.CollectionCapacity.Get(ei.ResourceType.Value) > 0f;
                        bool arrived = capable && actor.Hex.Equals(ei.TargetHex);
                        if (arrived && ei.ArrivalTurn < 0)
                            ei.ArrivalTurn = snap.TurnNumber;
                        if (arrived && snap.TurnNumber > ei.ArrivalTurn)
                            ei.LastConfirmedIncomeTick = snap.TurnNumber;
                        // Economy audit B12 — the SAME usefulness test Analysis admits a collector
                        // with (UsefulMarginalIncomeGain), judged without the income this collector
                        // itself now produces; a second "income below target" rule sent a still
                        // useful collector home and Analysis sent it straight back.
                        bool useful = capable && ei.ResourceType.HasValue
                            && CollectorStillUseful(snap, ei.ResourceType.Value, ei.ExpectedMarginalYield)
                            && WorldAnalysis.KnownExtractionYields(snap).Any(x =>
                                x.Hex.Equals(ei.TargetHex) && x.Type == ei.ResourceType.Value
                                && x.Yield > 0);
                        bool safe = !WorldAnalysis.KnownHostileAtHex(snap, ei.TargetHex);
                        if (!capable)
                        {
                            RetireEconomyIntent(state, intent, null, snap?.TurnNumber ?? 0);
                            AiDebugLog.Write($"[AI][V2][Economy][Mobile] retire {intent.IntentKey} "
                                + "reason=collector_lost_or_capability_lost");
                            continue;
                        }
                        if (arrived && ei.LastConfirmedIncomeTick >= 0 && (!useful || !safe))
                        {
                            HexCoord? home = ei.SafeReturnHex;
                            if (!home.HasValue || !IsProtectedEconomyHex(snap, player, home.Value))
                                home = SelectEconomyRecoveryTarget(snap, player, actor);
                            if (!home.HasValue)
                            {
                                RetireEconomyIntent(state, intent, null, snap?.TurnNumber ?? 0);
                                continue;
                            }
                            MissionIntentKey oldKey = intent.IntentKey;
                            ei.Kind = EconomyTaskKind.ReturnCollector;
                            ei.TargetHex = home.Value;
                            intent.IntentKey = MissionIntentKey.For(intent);
                            if (!oldKey.Equals(intent.IntentKey))
                                rekeys.Add((oldKey, intent));
                            active.Add(intent);
                            AiDebugLog.Write($"[AI][V2][Economy][Mobile] return collector "
                                + $"#{actor.ArmyId} -> ({home.Value.Q},{home.Value.R})");
                            continue;
                        }
                        // Holding the site IS the collector's activity (the planner proposes no
                        // step for it): record it as progress so it neither stalls nor ages out.
                        if (arrived)
                        {
                            intent.LastProgressTurn = snap.TurnNumber;
                            intent.LastProtectedTurn = snap.TurnNumber;
                            intent.StallTurns = 0;
                        }
                        ResumeTransientSuspension(intent);
                        active.Add(intent);
                        continue;
                    }
                    if (ei?.Kind == EconomyTaskKind.ReturnCollector)
                    {
                        bool completed = actor != null && actor.Hex.Equals(ei.TargetHex);
                        bool targetValid = IsProtectedEconomyHex(snap, player, ei.TargetHex);
                        if (completed || actor == null || !targetValid)
                        {
                            RetireEconomyIntent(state, intent, null, snap?.TurnNumber ?? 0);
                            AiDebugLog.Write($"[AI][V2][Economy][Mobile] retire return "
                                + $"{intent.IntentKey} arrived={(completed ? 1 : 0)}");
                            continue;
                        }
                        // Economy audit B3 — a transient capability suspension is re-tested every
                        // pass (the planner only proposes Active intents); AdvanceIntent ages it.
                        ResumeTransientSuspension(intent);
                        active.Add(intent);
                        continue;
                    }
                    if (ei?.Kind == EconomyTaskKind.ReturnBuilder)
                    {
                        bool completed = actor != null && actor.Hex.Equals(ei.TargetHex);
                        bool targetValid = IsProtectedEconomyHex(snap, player, ei.TargetHex);
                        if (!targetValid && actor != null)
                        {
                            HexCoord? retarget = SelectEconomyRecoveryTarget(snap, player, actor);
                            if (retarget.HasValue)
                            {
                                MissionIntentKey oldKey = intent.IntentKey;
                                ei.TargetHex = retarget.Value;
                                completed = actor.Hex.Equals(ei.TargetHex);
                                intent.IntentKey = completed ? oldKey : MissionIntentKey.For(intent);
                                if (!completed && !oldKey.Equals(intent.IntentKey))
                                    rekeys.Add((oldKey, intent));
                                targetValid = true;
                            }
                        }
                        if (completed || actor == null || !targetValid)
                        {
                            RetireEconomyIntent(state, intent, null, snap?.TurnNumber ?? 0);
                            AiDebugLog.Write($"[AI][V2][Economy][Recovery] retire {intent.IntentKey} "
                                + $"arrived={(completed ? 1 : 0)} actor={(actor != null ? 1 : 0)} "
                                + $"target={(targetValid ? 1 : 0)}");
                            continue;
                        }
                        ResumeTransientSuspension(intent);
                        active.Add(intent);
                        continue;
                    }

                    bool completedBuild = ei == null || MissionOutcomeLedger.EconomyObjectiveSatisfied(player,
                        new EconomyMissionTarget { Kind = ei.Kind, TargetHex = ei.TargetHex,
                            ResourceType = ei.ResourceType, BuilderArmyId = ei.BuilderArmyId });
                    bool targetValidBuild = ei != null && (ei.Kind == EconomyTaskKind.FoundBase
                        ? snap?.Self?.Hand?.Contains(ei.BuildCard) == true
                            && snap?.Economy?.BaseOpportunities?.Any(
                                site => site.Hex.Equals(ei.TargetHex)) == true
                        : ei.ResourceType.HasValue && snap?.Economy?.IsExtractionActionable(
                            ei.TargetHex, ei.ResourceType.Value) == true);
                    if (completedBuild || actor == null || !targetValidBuild)
                    {
                        RetireEconomyIntent(state, intent, null, snap?.TurnNumber ?? 0);
                        AiDebugLog.Write($"[AI][V2][Economy] retire {intent.IntentKey} "
                            + $"completed={(completedBuild ? 1 : 0)} actor={(actor != null ? 1 : 0)} "
                            + $"target={(targetValidBuild ? 1 : 0)}");
                        continue;
                    }
                    IReadOnlyList<EconomyBuilderRouteSnapshot> routes = ei.Kind == EconomyTaskKind.FoundBase
                        ? snap.Economy.BaseOpportunities.FirstOrDefault(x => x.Hex.Equals(ei.TargetHex)).BuilderRoutes
                        : snap.Economy.ExtractionOpportunities.FirstOrDefault(x => x.Hex.Equals(ei.TargetHex)
                            && ei.ResourceType.HasValue && x.ResourceType == ei.ResourceType.Value).BuilderRoutes;
                    bool recoveredBuilder = false;
                    foreach (EconomyBuilderRouteSnapshot route in routes
                        ?? System.Array.Empty<EconomyBuilderRouteSnapshot>())
                    {
                        if (route.ArmyId != actor.ArmyId) continue;
                        var suitability = DemandLayer.AssessEconomyArmy(snap, ei.TargetHex, route,
                            actor, ei.BuildApCost, includeReturn: ei.Kind == EconomyTaskKind.BuildExtraction);
                        if (suitability.Suitability != DemandLayer.EconomyArmySuitability.Ineligible
                            || suitability.IneligibleReason == "escort_activated_this_turn")
                            break;
                        // A composition failure does not heal when movement resets. Release the
                        // outbound envelope and let the existing recovery lifecycle own the actor.
                        StrategicResourceReservationLedger.ReleaseByOwner(player, snap.TurnNumber,
                            EconomyMissionPlanner.OwnerKey(intent.LastAttemptKey));
                        AiDebugLog.Write($"[AI][V2][Economy] recover {intent.IntentKey} actor=#{actor.ArmyId} "
                            + $"reason={suitability.IneligibleReason}");
                        BeginEconomyBuilderRecovery(player, snap, new AxisDemand
                        {
                            RequestingAxis = DesireAxis.Economy,
                            Capability = CapabilityKind.EconomicInfrastructure,
                            TargetHex = ei.TargetHex, EconomySiteValue = ei.BuildValue,
                        }, actor.ArmyId, snap.TurnNumber);
                        // Publish a replacement recovery in this same pass so downstream actor
                        // commitments cannot briefly expose the returning builder as unassigned.
                        if (state.TryGet(intent.IntentKey, out MissionIntent recovery)
                            && recovery.Economy?.Kind == EconomyTaskKind.ReturnBuilder)
                            active.Add(recovery);
                        // Recovery may instead have released the actor at a protected hex.
                        recoveredBuilder = true;
                        break;
                    }
                    if (recoveredBuilder) continue;
                    ResumeTransientSuspension(intent);
                    if (intent.Status == IntentStatus.Active) active.Add(intent);
                    continue;
                }
                if (intent.Kind == MissionKind.ActiveDefence)
                {
                    DefenceResolution resolution = ResolveActiveDefenceIntent(player, snap, state, intent);
                    if (resolution == DefenceResolution.Retire)
                        dead.Add(intent.IntentKey);
                    else if (resolution == DefenceResolution.Keep
                        || intent.Status == IntentStatus.Active)
                        active.Add(intent);
                    continue;
                }
                if (intent.Kind == MissionKind.Attack)
                {
                    // ATK §24/§25 — the Attack lane's own lifecycle answers live in
                    // MissionContinuityLayer.Attack.cs (a mechanical partial of this same owner).
                    // Audit F7 — a Gather re-plan may not recruit another intent's actor, nor an
                    // army still walking home on a Return fallback (its leg would pin it).
                    attackGatherUnavailable = attackGatherUnavailable
                        ?? AttackGatherUnavailable(state, raidClaims);
                    if (!ResolveAttackIntent(player, snap, intent, intent.Attack,
                            attackGatherUnavailable, out bool captured))
                    {
                        dead.Add(intent.IntentKey);
                        if (captured)
                            // §8/§59 — success releases the claim in place. Nothing here touches the
                            // army's roster or its garrison: Housekeeping owns local stabilisation.
                            AiDebugLog.Write($"[AI][V2][Attack] {intent.IntentKey} claim released on "
                                + "captured base; Housekeeping owns garrison stabilisation");
                        continue;
                    }
                    // A transient capability / pool suspension is re-tested every pass, exactly as
                    // Raid and ActiveDefence do. Without this an Attack suspended once was never
                    // resumed, never aged by ReconcileAfterTurn and never reaped.
                    ResumeTransientSuspension(intent);
                    if (intent.Status == IntentStatus.Active) active.Add(intent);
                    continue;
                }
                if (intent.Kind == MissionKind.Raid)
                {
                    if (!ResolveRaidIntent(player, snap, intent, raidClaims, safeRouteCost))
                    {
                        dead.Add(intent.IntentKey);
                        continue;
                    }
                    if (intent.Status == IntentStatus.Active)
                        active.Add(intent);
                    continue;
                }

                ScoutIntent s = intent.Scout;
                if (s == null) { dead.Add(intent.IntentKey); continue; }

                // A donor parked on an Economy loan (SuspendReason.EconomyLoan) keeps its pre-loan
                // identity untouched. ProvisioningManager is the sole owner of granting the loan;
                // RepayEconomyLoan is the only place that resumes it, and it does so by re-finding
                // this exact IntentKey via the borrowing Economy intent's LoanSource. Refocusing (or
                // retiring, if no runnable waypoint remains) a stale objective here would rekey or
                // delete that identity mid-loan; the orphan-repair pass above then finds no live
                // Economy intent pointing at the surviving key and wrongly reactivates the donor
                // while its actor is still out on loan — one actor claimed by two active intents.
                // Leave it parked; its waypoint is stale by definition anyway once it resumes.
                if (intent.Status == IntentStatus.Suspended
                    && intent.Suspended == SuspendReason.EconomyLoan)
                    continue;

                // A ground Recon role whose bound actor no longer exists (killed / merged away) is
                // not a lane any more. Unbind it so AdvanceIntent ages it like any idle intent
                // instead of parking it forever under the CapabilityUnavailable exemption, and so
                // TrimSurplusReconLanes never counts it as a physical lane (2026-09-25 audit F4:
                // Vex Intent(Refresh 1,-2) outlived scout #8 from T8 and displaced live scout #7 at
                // T13). AirSweep is exempt: its wing legitimately leaves the army list while stored.
                // Recon audit B11 — an actor that still exists but can no longer serve the role at all
                // (no longer a solo Recce, a prison, empty) is the same case: the structural test is
                // ActorCommitments.HasCapableActor, whose answer also decides the actor claim.
                // Stealth is deliberately not part of it (a scout that cannot hide THIS turn keeps
                // its role; the claim itself applies the objective's stealth requirement).
                if (intent.PreferredMoverArmyId.HasValue && !ReconScoutKinds.IsAirSweep(s.Kind)
                    && snap?.Self?.Armies != null
                    && !ActorCommitments.HasCapableActor(intent, snap, StealthRequirement.None))
                {
                    AiDebugLog.Write($"[AI][V2] continuity — {intent.IntentKey} actor "
                        + $"#{intent.PreferredMoverArmyId.Value} no longer exists or can no longer "
                        + "scout; role unbound");
                    intent.PreferredMoverArmyId = null;
                }

                if (!ScoutObjectiveEvaluator.IsIntentStillValid(snap, s))
                {
                    // Spec §1/§7/§50-52 — the focus hex is a live waypoint, not the durable
                    // identity. Re-point it at the nearest still-runnable Explore frontier / stale
                    // Refresh hex not already owned by another scout intent, re-key the ledger row
                    // in place, and keep the intent (with its CreatedTurn / PreferredMoverArmyId /
                    // accumulated progress). Only genuine exhaustion retires it.
                    MissionIntentKey oldKey = intent.IntentKey;
                    if (TryRefocusScoutIntent(snap, s, scoutFoci))
                    {
                        intent.IntentKey = MissionIntentKey.For(intent);
                        intent.LastProgressTurn = snap?.TurnNumber ?? intent.LastProgressTurn;
                        intent.StallTurns = 0;
                        if (!intent.IntentKey.Equals(oldKey))
                            rekeys.Add((oldKey, intent));
                        AiDebugLog.Write($"[AI][V2] continuity — {oldKey} waypoint done; re-focused to "
                            + $"{intent.IntentKey} — durable identity kept");
                        if (intent.Status == IntentStatus.Active)
                            active.Add(intent);
                        continue;
                    }
                    dead.Add(oldKey);
                    AiDebugLog.Write($"[AI][V2] continuity — {oldKey} retired at turn start (no runnable re-focus)");
                    continue;
                }

                ResumeTransientSuspension(intent);

                if (intent.Funding == CommitmentTier.Soft && underSiege)
                {
                    intent.Status = IntentStatus.Suspended;
                    intent.Suspended = SuspendReason.Siege;
                    continue;
                }
                if (intent.Status == IntentStatus.Suspended && intent.Suspended == SuspendReason.Siege && !underSiege)
                {
                    intent.Status = IntentStatus.Active;
                    intent.Suspended = SuspendReason.None;
                }

                if (intent.Status == IntentStatus.Active)
                    active.Add(intent);
            }

            foreach (MissionIntentKey k in dead)
                state.Remove(k);

            // Apply the in-place re-keys after the enumeration so the live dictionary is never
            // mutated mid-iteration. The intent object (and all its accumulated state) is kept;
            // only its dictionary slot moves to the new focus-hex key.
            foreach ((MissionIntentKey oldKey, MissionIntent it) in rekeys)
            {
                state.Remove(oldKey);
                state.Put(it);
            }

            active.Sort((x, y) =>
            {
                int c = y.Funding.CompareTo(x.Funding); if (c != 0) return c;
                c = x.CreatedTurn.CompareTo(y.CreatedTurn); if (c != 0) return c;
                return x.IntentKey.CompareTo(y.IntentKey);
            });


            // §P1 — if desired concurrency has fallen below the number of active durable Scout
            // lanes (map mostly explored, fewer reachable regions), retire the surplus lanes
            // instead of carrying them forever. A "not create more" cap alone leaves earlier
            // lanes alive; this actively sheds them.
            if (reconObjectives != null)
                TrimSurplusReconLanes(player, active, state, snap, reconObjectives);

            // Spec §1/§10 invariant — one physical Recon actor owns at most one active durable
            // role. Prevention lives in ReconAssignmentPlanner, but persisted saves/log replays may
            // already contain a collision. Repair it here at the continuity boundary: keep the role
            // most recently reconciled/progressed by the physical actor and unbind the rest. The
            // objectives remain alive and may acquire another actor; no mission is silently deleted.
            foreach (IGrouping<int, MissionIntent> g in active
                .Where(i => i.Kind == MissionKind.Scout && i.Scout != null && i.PreferredMoverArmyId.HasValue)
                .GroupBy(i => i.PreferredMoverArmyId.Value))
            {
                List<MissionIntent> claims = g
                    .OrderByDescending(i => i.LastReconciledTurn)
                    .ThenByDescending(i => i.LastProgressTurn)
                    .ThenByDescending(i => i.Funding)
                    .ThenBy(i => i.CreatedTurn)
                    .ThenBy(i => i.IntentKey)
                    .ToList();
                if (claims.Count <= 1) continue;

                MissionIntent owner = claims[0];
                foreach (MissionIntent duplicate in claims.Skip(1))
                {
                    duplicate.PreferredMoverArmyId = null;
                    AiDebugLog.Write($"[AI][V2] continuity — repaired duplicate Recon actor #{g.Key}: "
                        + $"kept {owner.IntentKey}, unbound {duplicate.IntentKey}");
                }
            }
            // An airborne strike sortie whose operation let go of it (retired, released, failed)
            // must still land.
            GroundCombatAirSupport.ReleaseOrphanStrikes(player, state.All);
            return active;
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
                && !ReconScoutKinds.IsAirSweep(i.Scout.Kind)
                && (!i.PreferredMoverArmyId.HasValue || !airActorIds.Contains(i.PreferredMoverArmyId.Value)))
                .ToList();
            if (scoutLanes.Count == 0)
                return;

            var runnable = reconObjectives
                .Where(o => o != null && o.BaseValue > 0f)
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
                if (dropped >= surplus || !state.TryConsumeReconLaneTrim(snap.TurnNumber))
                    break;
                state.Remove(v.IntentKey);
                active.Remove(v);
                if (v.PreferredMoverArmyId.HasValue)
                {
                    // A contraction decision is turn-wide. Do not let the same actor immediately
                    // acquire a fresh Recon mission later in this turn's bounded replans.
                    state.MarkReconActorTrimmed(snap.TurnNumber,
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
            if (snap?.MapKnowledge == null || s == null || s.Kind == ScoutTargetKind.Surveil
                || ReconScoutKinds.IsAirSweep(s.Kind))
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

        // §11 — is the fixed return base still ours AND still structurally
        // reachable? "Structurally" is the key word: this reads the GENUINE, any-number-of-turns
        // route-existence fact WorldAnalysis froze onto the mover's ArmySnapshot
        // (SafeStepPathing.FindSafePath — the same oracle Provisioning uses live), never "reachable
        // THIS turn" — a merely-temporarily-blocked step must NOT trigger a retarget, only a base
        // with NO safe route at all. Shared by the primary's Return leg and the support's
        // SupportReturn leg — `moverArmyId` is whichever of the two is walking home.
        internal static bool ReturnBaseStillValid(WorldSnapshot snap, PlayerSetupData player,
            int? moverArmyId, HexCoord? hex)
        {
            if (!hex.HasValue || snap?.Self?.BaseHexes == null)
                return false;
            // ATK §20/§48 — "is this hex MY base right now" is answered by Self.BaseHexes, the
            // current-truth topology WorldAnalysis derives from live owned buildings, never by a
            // remembered KnownBuilding.Owner. The old read could keep walking an army home to a
            // base this player had already lost (memory still says we own it) and could refuse a
            // freshly built or freshly captured base until it happened to be re-observed.
            if (!snap.Self.BaseHexes.Contains(hex.Value))
                return false;

            ArmySnapshot mover = moverArmyId.HasValue
                ? snap.Self?.Armies?.FirstOrDefault(a => a != null && a.ArmyId == moverArmyId.Value)
                : null;
            if (mover != null && mover.IsStructuralRaidActor
                && mover.ReachableOwnBaseHexes.Count > 0
                && !mover.ReachableOwnBaseHexes.Contains(hex.Value))
                return false;
            return true;
        }

        // ---------------------------------------------------------------------------------------
        //  §11 — "the most active base", as a PURE lexicographic rule over existing snapshot data
        //  (KnownBuilding / Self.Armies / ThreatModel). Deliberately a small function, not a new
        //  manager. Order:
        //    1. max total collected amounts at that base this turn
        //    2. has working Research/Production/Barracks infrastructure
        //    3. max own field+garrison power standing there
        //    4. min current threat severity
        //    5. min ETA for the returning army
        //    6. starting Citadel first, then coordinates (stable tie-break only)
        // ---------------------------------------------------------------------------------------
        // The ONE walk-home destination rule for every lifecycle leg (Raid Return / SupportReturn,
        // Attack SupportReturn / RecoveryReturn / GatherReturn, ActiveDefence Return): keep the
        // fixed base while it is still ours and structurally reachable, otherwise re-pick through
        // SelectReturnBase. `reselected` tells the caller the leg moved (reset its stall clock);
        // null means no own base is left at all.
        internal static HexCoord? KeepOrReselectHome(WorldSnapshot snap, PlayerSetupData player,
            int? moverArmyId, HexCoord? current, out bool reselected)
        {
            reselected = false;
            if (current.HasValue && ReturnBaseStillValid(snap, player, moverArmyId, current))
                return current;
            reselected = true;
            return SelectReturnBase(snap, player, moverArmyId);
        }

        internal static HexCoord? SelectReturnBase(WorldSnapshot snap, PlayerSetupData player, int? moverArmyId)
        {
            if (snap?.Self?.BaseHexes == null || player == null)
                return null;
            // ATK §20/§48 — identity comes from Self.BaseHexes (current truth); Known.Buildings
            // stays the METADATA source the ranking below reads (stored resources, facilities).
            // Splitting the two is what lets a just-built or just-captured base be chosen on the
            // very turn it becomes ours, without waiting for a fresh structural observation.
            List<HexCoord> bases = snap.Self.BaseHexes.ToList();
            if (bases.Count == 0)
                return null;

            ArmySnapshot mover = moverArmyId.HasValue
                ? snap.Self?.Armies?.FirstOrDefault(a => a != null && a.ArmyId == moverArmyId.Value)
                : null;
            int moveBudget = System.Math.Max(1, mover?.MaxMovement ?? AiConfigV2.etaFallbackMoveBudget);

            // A structurally reachable base always outranks an unreachable one, ahead of every
            // other tie-break. Falls back to distance-only ordering among
            // bases with the SAME reachability, and — if genuinely none are reachable right now —
            // still returns the best-by-distance candidate rather than stranding the operation on a
            // signal that may only be a transient blockade.
            bool Reachable(HexCoord h) =>
                mover == null || !mover.IsStructuralRaidActor
                || mover.ReachableOwnBaseHexes.Contains(h);

            HexCoord citadel = snap.Self.Citadel;
            return bases
                .OrderByDescending(h => Reachable(h) ? 1 : 0)
                .ThenByDescending(h => BaseCollectedAmount(snap, h))
                .ThenByDescending(h => BaseHasDevelopmentInfrastructure(snap, h) ? 1 : 0)
                .ThenByDescending(h => BaseOwnPowerAt(snap, h))
                .ThenBy(h => BaseThreatSeverityAt(snap, h))
                .ThenBy(h => mover == null ? 0
                    : AiV2Util.CeilDiv(HexGridMath.Distance(mover.Hex, h), moveBudget))
                .ThenByDescending(h => h.Equals(citadel) ? 1 : 0)
                .ThenBy(h => h.Q).ThenBy(h => h.R)
                .Select(h => (HexCoord?)h)
                .FirstOrDefault();
        }

        private static float BaseCollectedAmount(WorldSnapshot snap, HexCoord hex)
        {
            float total = 0f;
            if (snap.Known?.Buildings == null)
                return total;
            foreach (Game.Ai.AiMapMemory.KnownBuilding b in snap.Known.Buildings)
            {
                if (!b.Hex.Equals(hex) || b.CollectedAmounts == null) continue;
                foreach (int amount in b.CollectedAmounts)
                    total += System.Math.Max(0, amount);
            }
            return total;
        }

        // Metadata read only — the caller has ALREADY established that `hex` is one of our own
        // bases (Self.BaseHexes). Matching on the hex alone is deliberate: a remembered
        // KnownBuilding.Owner can lag reality on a base we just built or captured, and gating this
        // on it would silently drop real facilities out of the ranking.
        private static bool BaseHasDevelopmentInfrastructure(WorldSnapshot snap, HexCoord hex) =>
            snap.Known?.Buildings != null && snap.Known.Buildings.Any(b => b.Hex.Equals(hex)
                && (b.HasFacilityWithAbility(UnitAbilities.Research)
                    || b.HasFacilityWithAbility(UnitAbilities.Production)
                    || b.HasFacilityWithAbility(UnitAbilities.Barracks)));

        private static float BaseOwnPowerAt(WorldSnapshot snap, HexCoord hex)
        {
            float power = 0f;
            foreach (ArmySnapshot a in snap.Self?.Armies ?? System.Array.Empty<ArmySnapshot>())
                if (a != null && !a.IsPrison && !a.IsAir && a.Hex.Equals(hex))
                    power += a.EffectiveArmyPower;
            return power;
        }

        private static float BaseThreatSeverityAt(WorldSnapshot snap, HexCoord hex)
        {
            float worst = 0f;
            foreach (AssetThreatSnapshot t in snap.Threat?.Threats ?? System.Array.Empty<AssetThreatSnapshot>())
                if (t?.Asset != null && t.Asset.Hex.Equals(hex) && t.Severity > worst)
                    worst = t.Severity;
            return worst;
        }

        public static List<Commitment> BindFunding(IReadOnlyList<MissionIntent> activeIntents,
            IReadOnlyList<MissionProposal> proposals)
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
                    AiDebugLog.WriteDeduped(intent.IntentKey.ToString(),
                        $"[AI][V2] continuity — WARN {intent.IntentKey} ({intent.Funding}) "
                        + "not materialised this turn; no funding bound");
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
            MissionTurnOutcome outcome)
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
        // invisible to Continuity: the intent produces no MissionTurnOutcome this turn, and
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
            IReadOnlyList<MissionTurnOutcome> outcomes)
        {
            MissionIntentState state = MissionIntentRegistry.GetOrCreate(player);
            AiAllocatorState allocState = AiAllocatorStateRegistry.GetOrCreate(player);
            var seen = new HashSet<MissionIntentKey>();

            foreach (MissionTurnOutcome o in outcomes ?? new List<MissionTurnOutcome>())
            {
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
                        || intent.Suspended == SuspendReason.EconomyLoan
                        || intent.Suspended == SuspendReason.ActiveDefencePreemption)
                    && intent.Kind != MissionKind.Development)
                    continue;

                if (intent.LastReconciledTurn == turn)
                    continue;

                intent.LastReconciledTurn = turn;
                intent.TurnsActive++;
                // A committed Economy build Phase A is still protecting this very turn (reserving
                // its resources, holding its card) has real, legitimate activity even though it
                // produced no MissionTurnOutcome — it simply has nothing executable yet while H/E/
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
                        + $"stall {intent.StallTurns}/{AiConfigV2.commitmentStallTurns}, "
                        + $"age {intent.TurnsActive}/{AiConfigV2.commitmentMaxTurns})");
                }
            }
        }

        // A materialized outcome carries its EconomyTarget directly, but a fresh mission that failed
        // provisioning before ever producing a ProvisionedMission only has it on the proposal.
        private static bool TryGetEconomyTarget(MissionTurnOutcome o, out EconomyMissionTarget target)
        {
            if (o.HasEconomyPayload)
            {
                target = o.EconomyTarget;
                return true;
            }
            if (o.Proposal?.Target is EconomyMissionTarget proposed)
            {
                target = proposed;
                return true;
            }
            target = default;
            return false;
        }

        private static void ReconcileOutcome(MissionIntentState state,
            AiAllocatorState allocState, MissionTurnOutcome o, int turn)
        {
            state.TryGet(o.IntentKey, out MissionIntent intent);

            string aid = AiV2Trace.FormatCorrelation(o.Proposal);
            bool returnBuilderOutcome = o.MissionKind == MissionKind.Economy
                && (o.EconomyTarget.Kind == EconomyTaskKind.ReturnBuilder
                    || intent?.Economy?.Kind == EconomyTaskKind.ReturnBuilder);
            AiDebugLog.Write($"[AI][V2] [{aid}] outcome {o.Outcome}"
                + (o.ObjectiveSatisfied ? " satisfied" : "")
                + (o.StructuralFailure ? " structural" : "")
                + $" {o.IntentKey}");

            // Strike force — a side leg (a gather donor walking home, the support wing's sortie) is
            // no step of the operation: whatever its outcome, the Attack intent's lifecycle
            // (progress, stall, suspension, retirement) is untouched. Arrival, landing or loss is
            // read from the next snapshot (ResolveGatherReturns / ResolveAttackAirSupport); a
            // failed leg releases just that donor or wing (an airborne wing then lands through
            // GroundCombatAirSupport.ReleaseOrphanStrikes).
            // The leg is read from the payload or, when Provisioning failed before one existed,
            // from the proposal (GroundCombatLegs.AttackLegOf): it shares the operation's
            // IntentKey, so the generic branches below would otherwise end the whole operation.
            AttackMissionTarget? attackLeg = GroundCombatLegs.AttackLegOf(o);
            if (attackLeg.HasValue && GroundCombatLegs.IsAttackSideLeg(attackLeg.Value.Phase))
            {
                AttackMissionTarget leg = attackLeg.Value;
                bool failed = o.StructuralFailure || o.Outcome == ExecutionOutcome.Failed
                    || o.ProvisionFailureKindValue == ProvisionFailureKind.TargetInvalidated;
                if (failed && intent?.Attack != null
                    && leg.Phase == AttackMissionPhase.GatherReturn
                    && leg.SupportArmyId.HasValue)
                {
                    intent.Attack.GatherReturns.RemoveAll(r => r.ArmyId == leg.SupportArmyId.Value);
                    AiDebugLog.Write($"[AI][V2][Attack][Gather] continuity — [{aid}] {o.IntentKey} donor "
                        + $"#{leg.SupportArmyId.Value} walk home failed ({Describe(o)}); released");
                }
                if (failed && intent?.Attack != null
                    && leg.Phase == AttackMissionPhase.AirSupport
                    && intent.Attack.AirSupportArmyId == leg.AirSupportArmyId)
                {
                    ReleaseAttackAirSupport(intent.Attack, turn);
                    AiDebugLog.Write($"[AI][V2][Attack][AirSupport] continuity — [{aid}] {o.IntentKey} wing "
                        + $"#{leg.AirSupportArmyId} sortie failed ({Describe(o)}); released");
                }
                return;
            }

            // A support whose roster no longer improves the primary (Provisioning AssemblyInfeasible
            // on a convoy / gather leg) invalidates only that assignment, never the durable
            // operation or its target: the support is released and the operation stays in its
            // reinforcement / gather phase. One edge for Raid and Attack (GroundCombatLegs.IsSupportLeg).
            if (o.StructuralFailure && intent != null
                && o.ProvisionFailureKindValue == ProvisionFailureKind.AssemblyInfeasible
                && GroundCombatLegs.IsSupportLeg(o) && ReleaseInvalidSupport(intent, o))
            {
                intent.Status = IntentStatus.Active;
                intent.Suspended = SuspendReason.None;
                intent.LastReconciledTurn = turn;
                AiDebugLog.Write($"[AI][V2] continuity — [{aid}] {o.IntentKey} support assembly "
                    + "invalid; support released, operation kept");
                return;
            }

            if (o.Outcome == ExecutionOutcome.Completed && o.ObjectiveSatisfied)
            {
                if (o.MissionKind == MissionKind.ActiveDefence && intent?.ActiveDefence != null)
                {
                    if (TryResumePreemptedOffensive(state, intent.ActiveDefence, "defence_completed"))
                    {
                        state.Remove(intent.IntentKey);
                        return;
                    }
                    intent.ActiveDefence.ObjectiveCompleted = true;
                    intent.Status = IntentStatus.Active;
                    intent.Suspended = SuspendReason.None;
                    AiDebugLog.Write($"[AI][V2][ActiveDefence][Continuity] decision=REANALYZE_STABILIZATION actor={intent.PreferredMoverArmyId}");
                    return;
                }
                if (o.MissionKind == MissionKind.ActiveDefence && intent == null
                    && o.HasActiveDefencePayload && o.MoverArmyId.HasValue
                    && !o.ActiveDefenceTarget.SuspendedOffensiveIntentKey.HasValue)
                {
                    CreateActiveDefenceIntent(state, o, turn);
                    if (state.TryGet(MissionIntentKey.ForActiveDefence(
                            o.ActiveDefenceTarget.EnemyArmyId), out MissionIntent created)
                        && created?.ActiveDefence != null)
                        created.ActiveDefence.ObjectiveCompleted = true;
                    return;
                }
                // Raid completion ends only the CURRENT neutral target, not the durable campaign.
                // Keep (or create, when the first attack completed immediately) the operation so
                // the next ResolveActive pass can re-orient the same primary onto another neutral
                // or enter Return. Removing it here strands the victorious army and makes the
                // Assault -> next target / Return phase machine unreachable.
                bool completedRaidAssault = o.MissionKind == MissionKind.Raid
                    && (o.HasRaidPayload
                        ? o.RaidPhase == RaidMissionPhase.Assault
                        : o.Proposal?.Target is RaidMissionTarget raidTarget
                            && raidTarget.Phase == RaidMissionPhase.Assault);
                if (completedRaidAssault)
                {
                    if (intent != null)
                    {
                        AdvanceIntent(intent, o, turn, state, allocState);
                        AiDebugLog.Write($"[AI][V2][Raid] continuity — [{aid}] {o.IntentKey} current "
                            + "target completed; durable campaign kept for re-orient/return");
                        return;
                    }
                    if (o.HasRaidPayload && o.OperationStarted)
                    {
                        CreateRaidIntent(state, o, turn);
                        AiDebugLog.Write($"[AI][V2][Raid] continuity — [{aid}] {o.IntentKey} first "
                            + "target completed during opening step; campaign created for return/refocus");
                        return;
                    }
                }

                // ATK §7/§8 — an Attack that reached its objective is DONE. One intent is one
                // Base/Citadel, so there is deliberately no re-orient here: the army stays where it
                // is, the claim is released, and the next global replan decides what the new
                // topology is worth. An intent that still exists is advanced so ResolveActive
                // observes the capture through the ordinary path and logs the release once.
                if (o.MissionKind == MissionKind.Attack)
                {
                    if (intent != null)
                    {
                        AdvanceIntent(intent, o, turn, state, allocState);
                        AiDebugLog.Write($"[AI][V2][Attack] continuity — [{aid}] {o.IntentKey} "
                            + (o.AttackTarget.Phase == AttackMissionPhase.Assault
                                ? "objective reached; operation ends at the captured site"
                                : $"{o.AttackTarget.Phase} leg reached its goal"));
                        return;
                    }
                    if (o.HasAttackPayload && o.OperationStarted)
                    {
                        CreateAttackIntent(state, o, turn);
                        AiDebugLog.Write($"[AI][V2][Attack] continuity — [{aid}] {o.IntentKey} "
                            + "captured on its opening step; intent recorded for a clean release");
                        return;
                    }
                }

                // Review P1 #1/#2 (+ follow-up) — an Explore/Refresh focus hex met by something
                // OTHER than this actor's own execution reaching goal (another scout opened it
                // mid-turn, or provisioning found it already live-satisfied) is a satisfied
                // WAYPOINT, not a finished role. KEEP — or, for a fresh mission that really
                // began executing this turn, CREATE — the durable ground-scout intent so
                // ActorCommitments retains the scout and ResolveActive re-focuses it next turn
                // (its hex now fails IsIntentStillValid). Mirrors the own-execution
                // ExecutionResult.DurableRoleContinues ProductiveStop path. Surveil and genuine
                // own-execution completions still retire.
                if (o.ObjectiveSatisfiedExternally)
                {
                    bool existingScoutRole = intent != null
                        && intent.Scout != null && intent.Scout.Kind != ScoutTargetKind.Surveil;
                    // Fresh role: the mission was provisioned AND executed at least one step
                    // this turn (so ReconPatrolState already exists). A provisioning-only
                    // TargetSatisfied for a never-executed fresh mission has HasScoutPayload ==
                    // false / MadeProgress == false and is correctly NOT made durable.
                    bool freshScoutRole = intent == null && o.HasScoutPayload && o.MadeProgress
                        && o.ScoutKind != ScoutTargetKind.Surveil;

                    if (existingScoutRole)
                    {
                        // Count the AP / steps the scout actually spent before the waypoint
                        // was taken (accumulated-state preservation), same as any other
                        // productive turn — AdvanceIntent owns that accounting.
                        o.MadeProgress = true;
                        AdvanceIntent(intent, o, turn, state, allocState);
                        AiDebugLog.Write($"[AI][V2] continuity — [{aid}] {o.IntentKey} waypoint satisfied "
                            + "externally; durable scout role kept for next-turn re-focus");
                        return;
                    }
                    if (freshScoutRole)
                    {
                        if (TryAbsorbIntoExistingActorRole(state, o, turn, allocState))
                            return;
                        CreateIntent(state, o, turn);
                        AiDebugLog.Write($"[AI][V2] continuity — [{aid}] {o.IntentKey} fresh scout began "
                            + "this turn; waypoint satisfied externally, durable intent created for re-focus");
                        return;
                    }
                }
                if (intent != null)
                    AiDebugLog.Write($"[AI][V2] continuity — [{aid}] {o.IntentKey} COMPLETED, retired");
                RetireOutcomeIntent(state, intent, o, turn);
                return;
            }

            if (o.StructuralFailure)
            {
                if (returnBuilderOutcome && intent != null)
                {
                    KeepReturnBuilder(state, intent, o, turn);
                    return;
                }
                RetireOutcomeIntent(state, intent, o, turn);
                string reason = o.ProvisionFailureKindValue?.ToString() ?? "StructuralFailure";
                StartPersistentCooldown(allocState, o.AttemptKey, o.MissionKind, turn, reason);
                AiDebugLog.Write($"[AI][V2] continuity — [{aid}] {o.IntentKey} structural failure ({reason}), retired + cooldown");
                return;
            }

            if (o.Outcome == ExecutionOutcome.Failed)
            {
                if (returnBuilderOutcome && intent != null)
                {
                    KeepReturnBuilder(state, intent, o, turn);
                    return;
                }
                if (intent != null)
                    AiDebugLog.Write($"[AI][V2] continuity — [{aid}] {o.IntentKey} failed ({Describe(o)}), retired");
                RetireOutcomeIntent(state, intent, o, turn);
                return;
            }

            if (o.MissionKind == MissionKind.Economy && !o.MadeProgress)
            {
                if (returnBuilderOutcome && intent != null)
                {
                    KeepReturnBuilder(state, intent, o, turn);
                    return;
                }
                // A no-progress Economy outcome ends its outbound commitment only on a PROVEN
                // route failure: Provisioning's NoExecutableStep (no live safe route for the
                // committed mover) or an executed step that found no safe / legal move. Every
                // other no-progress outcome is transient — NoMoverExists / MoverContended
                // suspend the intent (AdvanceIntent, CapabilityUnavailable, bounded by the
                // per-project delivery-failure streaks), anything else (an AP / resource
                // envelope that did not fit this pass, a stale plan or activation) ages it
                // through StallTurns/ShouldReap like any idle intent (Economy audit B1/B2).
                // ReturnBuilder (the return-trip leg) keeps its own preservation rule above.
                bool capabilityFailure = o.ProvisionFailureKindValue == ProvisionFailureKind.NoMoverExists
                    || o.ProvisionFailureKindValue == ProvisionFailureKind.MoverContended;

                // P1 fix: a FoundBase project that never got far enough to become a durable intent
                // (no actor at all — provisioning failed on the very first attempt) must still count
                // toward the delivery-failure streak, or that project can retry forever without ever
                // reaching AdvanceIntent's own call (below), which only fires once intent != null.
                // This is the ONE registration point for the no-intent case; AdvanceIntent's call
                // only fires for an existing intent, so the two never double-count the same outcome.
                if (capabilityFailure && intent == null
                    && TryGetEconomyTarget(o, out EconomyMissionTarget freshTarget)
                    && freshTarget.Kind == EconomyTaskKind.FoundBase)
                    state.RecordBaseExpansionDeliveryFailure(turn, freshTarget.BuildCard, freshTarget.TargetHex);

                if (intent != null && !IsEconomyRouteFailure(o))
                {
                    AdvanceIntent(intent, o, turn, state, allocState);
                    return;
                }
                RetireEconomyIntent(state, intent, o, turn);
                return;
            }

            if (intent != null)
            {
                AdvanceIntent(intent, o, turn, state, allocState);
            }
            else if (o.MadeProgress && o.HasScoutPayload)
            {
                if (!TryAbsorbIntoExistingActorRole(state, o, turn, allocState))
                    CreateIntent(state, o, turn);
            }
            else if (o.HasRaidPayload && o.OperationStarted)
            {
                CreateRaidIntent(state, o, turn);
            }
            else if (o.HasAttackPayload && o.OperationStarted)
            {
                CreateAttackIntent(state, o, turn);
            }
            else if (o.HasActiveDefencePayload && o.MadeProgress)
            {
                CreateActiveDefenceIntent(state, o, turn);
            }
            else if (o.HasEconomyPayload && o.MadeProgress)
            {
                CreateEconomyIntent(state, o, turn);
            }
            else if (o.HasDevelopmentPayload && o.MadeProgress)
            {
                CreateDevelopmentIntent(state, o, turn);
            }

        }

        // Releases the support an AssemblyInfeasible convoy / gather leg named. False when the leg
        // is not one whose support can be released this way (the generic failure path applies).
        private static bool ReleaseInvalidSupport(MissionIntent intent, MissionTurnOutcome o)
        {
            if (intent.Raid != null
                && GroundCombatLegs.RaidLegOf(o) == RaidMissionPhase.Reinforcement)
            {
                intent.Raid.SupportArmyId = null;
                intent.Raid.ReinforcementRequestedTurn = -1;
                intent.Raid.Phase = RaidMissionPhase.Reinforcement;
                return true;
            }
            AttackMissionTarget? leg = GroundCombatLegs.AttackLegOf(o);
            if (intent.Attack == null || !leg.HasValue)
                return false;
            if (leg.Value.Phase == AttackMissionPhase.Gather && leg.Value.SupportArmyId.HasValue)
            {
                intent.Attack.GatherSupportArmyIds.Remove(leg.Value.SupportArmyId.Value);
                return true;
            }
            if (leg.Value.Phase == AttackMissionPhase.Reinforcement)
            {
                intent.Attack.SupportArmyId = null;
                intent.Attack.ReinforcementRequestedTurn = -1;
                return true;
            }
            return false;
        }

        private static void AdvanceIntent(MissionIntent intent, MissionTurnOutcome o, int turn,
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
                ReleaseOtherReconActorClaims(state, intent, o.MoverArmyId.Value);
                // For a Raid the executor of a given turn may be the
                // SUPPORT army (Reinforcement transit / handoff), not the primary. Generic code
                // must never let that support id overwrite PrimaryArmyId and silently orphan the
                // real raiding force. Only a mover that IS (or is taking over as) the primary may
                // rewrite it: a support-executed step leaves the primary untouched.
                RaidIntent raid = intent.Raid;
                // Actor role is taken from the immutable provisioned outcome. Execution may already
                // have called CompleteRaidReinforcement, clearing SupportArmyId and switching the
                // live intent to Assault; inspecting that mutated phase here used to misclassify the
                // convoy as the new primary. A completed handoff deliberately releases the convoy;
                // only a still-travelling selected support becomes a durable claim.
                bool supportExecutedThisTurn = raid != null && o.HasRaidPayload
                    && (o.RaidPhase == RaidMissionPhase.Reinforcement || o.RaidPhase == RaidMissionPhase.SupportReturn)
                    && o.RaidPrimaryArmyId == raid.PrimaryArmyId
                    && o.RaidSupportArmyId.HasValue
                    && o.RaidSupportArmyId.Value == o.MoverArmyId.Value;
                bool airSupportExecutedThisTurn = raid != null && o.HasRaidPayload
                    && o.RaidPhase == RaidMissionPhase.AirSupport
                    && o.RaidAirSupportArmyId.HasValue
                    && o.RaidAirSupportArmyId.Value == o.MoverArmyId.Value;
                if (airSupportExecutedThisTurn)
                {
                    raid.Phase = RaidMissionPhase.AirSupport;
                    raid.AirSupportArmyId = o.RaidAirSupportArmyId;
                    raid.AirSupportLandingHex = o.RaidAirSupportLandingHex;
                    raid.AirSupportStrikeSucceeded |= o.RaidAirSupportStrikeSucceeded;
                }
                else if (supportExecutedThisTurn)
                {
                    if (o.RaidPhase == RaidMissionPhase.Reinforcement
                        && !o.ReinforcementHandoffAttempted && !raid.SupportArmyId.HasValue)
                        raid.SupportArmyId = o.MoverArmyId.Value;
                }
                // Economy actor ownership is durable. A replacement may only happen after
                // ResolveActive retires a structurally invalid intent; an ordinary retry cannot
                // atomically rewrite the mover behind continuity's back.
                // An Attack Reinforcement / SupportReturn / Gather step is executed by a SUPPORT
                // army: like Raid's support legs above it must never overwrite the primary.
                else if (!(intent.Attack != null && o.HasAttackPayload
                        && GroundCombatLegs.IsAttackSupportLeg(o.AttackTarget.Phase))
                    && ((intent.Kind != MissionKind.Economy
                            && intent.Kind != MissionKind.Development)
                        || !intent.PreferredMoverArmyId.HasValue
                        || intent.PreferredMoverArmyId.Value == o.MoverArmyId.Value))
                    intent.PreferredMoverArmyId = o.MoverArmyId;
            }

            if (o.HasScoutPayload && intent.Scout != null)
                ApplyScoutPayload(intent.Scout, o);

            if (o.HasAttackPayload && intent.Attack != null)
            {
                AttackIntent ai = intent.Attack;
                ai.OperationStarted |= o.OperationStarted;
                if (o.AttackTarget.Phase == AttackMissionPhase.Gather)
                {
                    // Audit F7 — an attempted handoff (full, partial or rejected) ends that
                    // support's gather leg. Strike force step 5: whatever container is left walks
                    // home (GatherReturn; ResolveAttackIntent picks the base). It never becomes the
                    // Reinforcement support.
                    if (o.ReinforcementHandoffAttempted && o.MoverArmyId.HasValue
                        && ai.GatherSupportArmyIds.Remove(o.MoverArmyId.Value)
                        && !ai.GatherReturns.Any(r => r.ArmyId == o.MoverArmyId.Value))
                        ai.GatherReturns.Add(new AttackGatherReturn { ArmyId = o.MoverArmyId.Value });
                }
                else
                {
                    if (o.AttackTarget.SupportArmyId.HasValue)
                        ai.SupportArmyId = o.AttackTarget.SupportArmyId;
                    if (o.AttackTarget.RecoveryBaseHex.HasValue)
                        ai.RecoveryBaseHex = o.AttackTarget.RecoveryBaseHex;
                    // §46/§23 — a full/full swap displaced a primary body into the support
                    // container, so the whole support army must walk itself home. Same shared
                    // handoff semantics and the same SupportReturn leg the Raid lane uses.
                    // The destination itself is chosen by ResolveAttackIntent, which has the
                    // snapshot and the player: AdvanceIntent only records the immutable fact.
                    if (o.ReinforcementHandoffAttempted && ai.SupportArmyId.HasValue)
                        ai.Phase = AttackMissionPhase.SupportReturn;
                }
                // §17 — the operation's turn-local side-strike marker. Continuity is the only
                // writer; Execution merely reported that the diversion was really spent.
                if (o.AttackOpportunisticStrike)
                    ai.LastOpportunisticStrikeTurn = turn;
            }
            if (o.HasRaidPayload && intent.Raid != null)
            {
                intent.Raid.LastKnownHex = o.RaidLastKnownHex;
                if (o.OperationStarted)
                {
                    intent.Raid.OperationStarted = true;
                    if (intent.Funding != CommitmentTier.Hard)
                    {
                        intent.Funding = CommitmentTier.Hard;
                        AiDebugLog.Write($"[AI][V2] continuity — {intent.IntentKey} promoted to Hard commitment (operation started)");
                    }
                }
            }

            if (o.HasEconomyPayload && intent.Economy != null)
            {
                intent.Economy.TargetHex = o.EconomyTarget.TargetHex;
                intent.Economy.BuilderArmyId = intent.PreferredMoverArmyId;
                // The mission has already used the continuity-pinned builder's world score.
                // Persist the last accepted intrinsic value so a later turn without a refreshed
                // demand cannot silently revive the unrelated site-only BuildValue.
                if (o.MadeProgress && o.Proposal?.Target is EconomyMissionTarget scored
                    && scored.Kind == intent.Economy.Kind
                    && scored.TargetHex.Equals(intent.Economy.TargetHex)
                    && scored.BuilderArmyId == intent.PreferredMoverArmyId)
                    intent.Economy.IntrinsicValue = o.Proposal.BaseValue;
                if (o.EconomyBuildCompleted) intent.Funding = CommitmentTier.Hard;
            }

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
                && (!capabilityUnavailable || intent.Kind == MissionKind.Development
                    || IsMoverlessScoutRole(intent) || IsCollectorEconomyIntent(intent)))
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

                // Base expansion is deliberately exempt from StallTurns/ShouldReap aging (see the
                // comment above transientCapability in ReconcileOutcome) so an in-progress delivery
                // survives a transient blip. That exemption previously had no upper bound: the same
                // stuck project — NoMoverExists / MoverContended, turn after turn — never triggered
                // the existing MissionIntentState delivery-failure cooldown because nothing called
                // it. Wire it here, the one place this intent is suspended for that reason. A gap
                // turn without a capability failure (real progress or a different suspend reason)
                // breaks RecordBaseExpansionDeliveryFailure's consecutive-turn streak on its own —
                // no separate reset is needed.
                if (!o.MadeProgress && intent.Kind == MissionKind.Economy
                    && intent.Economy?.Kind == EconomyTaskKind.FoundBase
                    && state.RecordBaseExpansionDeliveryFailure(
                        turn, intent.Economy.BuildCard, intent.Economy.TargetHex))
                {
                    RetireEconomyIntent(state, intent, o, turn);
                    StartPersistentCooldown(allocState, intent.LastAttemptKey, intent.Kind, turn,
                        "BaseExpansionDeliverySuppressed");
                    AiDebugLog.Write($"[AI][V2] continuity — [{AiV2Trace.FormatCorrelation(o.Proposal)}] "
                        + $"{intent.IntentKey} Base delivery repeatedly failed "
                        + $"({o.ProvisionFailureKindValue}); suppressed for cooldown, retired");
                    return;
                }

                // BuildExtraction needs the same upper bound: it is exempt from StallTurns/ShouldReap
                // for the identical reason (capabilityUnavailable, above), so its consecutive
                // NoMoverExists/MoverContended turns are counted here — otherwise an intent whose
                // pinned mover can no longer advance (e.g. its safe route stays blocked every turn)
                // would stay suspended forever with its actor and card claim held.
                // Extraction has no single staged slot like Base (several sites can be active at
                // once), so the counter is keyed per (resource, site) in MissionIntentState — same
                // owner, same StartPersistentCooldown exit already used by every other retirement
                // path (reap/structural-failure/Base) — no new registry or manager.
                if (!o.MadeProgress && intent.Kind == MissionKind.Economy
                    && intent.Economy?.Kind == EconomyTaskKind.BuildExtraction
                    && state.RecordExtractionDeliveryFailure(
                        turn, intent.Economy.ResourceType, intent.Economy.TargetHex))
                {
                    RetireEconomyIntent(state, intent, o, turn);
                    StartPersistentCooldown(allocState, intent.LastAttemptKey, intent.Kind, turn,
                        "ExtractionDeliverySuppressed");
                    AiDebugLog.Write($"[AI][V2] continuity — [{AiV2Trace.FormatCorrelation(o.Proposal)}] "
                        + $"{intent.IntentKey} Extraction delivery repeatedly failed "
                        + $"({o.ProvisionFailureKindValue}); suppressed for cooldown, retired");
                    return;
                }
            }

            // A Raid keeps its absolute age cap (raidIntentMaxTurns) through capability
            // suspensions: ResolveActive resumes it every pass, so without the cap a Raid waiting
            // on a support no stage can bind was held — primary claimed — forever.
            if ((!capabilityUnavailable || intent.Kind == MissionKind.Development
                    || intent.Kind == MissionKind.Raid
                    || IsMoverlessScoutRole(intent) || IsCollectorEconomyIntent(intent))
                && ShouldReap(intent, turn))
            {
                RetireOutcomeIntent(state, intent, o, turn);
                StartPersistentCooldown(allocState, intent.LastAttemptKey, intent.Kind, turn, "IntentReapedStall");
                AiDebugLog.Write($"[AI][V2] continuity — [{AiV2Trace.FormatCorrelation(o.Proposal)}] {intent.IntentKey} reaped (stall "
                    + $"{intent.StallTurns}/{AiConfigV2.commitmentStallTurns}, age "
                    + $"{intent.TurnsActive}/{AiConfigV2.commitmentMaxTurns})");
            }
            else
            {
                AiDebugLog.Write($"[AI][V2] continuity — [{AiV2Trace.FormatCorrelation(o.Proposal)}] {intent.IntentKey} advanced "
                    + $"({o.Outcome}, progress {(o.MadeProgress ? 1 : 0)}, t{intent.TurnsActive} stall{intent.StallTurns}"
                    + (capabilityUnavailable ? $", suspended CapabilityUnavailable:{o.ProvisionFailureKindValue}" : "") + ")");
            }
        }

        private static void ReleaseOtherReconActorClaims(MissionIntentState state,
            MissionIntent owner, int moverArmyId)
        {
            if (state == null || owner == null || owner.Kind != MissionKind.Scout)
                return;

            foreach (MissionIntent other in state.All)
            {
                if (other == null || object.ReferenceEquals(other, owner)
                    || other.Kind != MissionKind.Scout || other.Scout == null
                    || other.PreferredMoverArmyId != moverArmyId)
                    continue;
                other.PreferredMoverArmyId = null;
                AiDebugLog.Write($"[AI][V2] continuity — actor #{moverArmyId} moved to "
                    + $"{owner.IntentKey}; unbound prior role {other.IntentKey}");
            }
        }

        // Spec §1/§10 — the physical scout that produced this fresh scout outcome already owns a
        // durable Recon role (Explore / Refresh / Surveil) under a different key: a new
        // opportunistic mission ran on a mover continuity already tracks. Re-point that existing
        // role at the new objective and re-key its registry slot, preserving CreatedTurn /
        // TurnsActive / CumulativeApSpent / StepsMovedTotal / PreferredMoverArmyId, instead of
        // creating a second durable intent for the same physical actor. Ownership is actor-
        // exclusive across all three Recon sub-kinds. Returns true when it absorbed the outcome.
        private static bool TryAbsorbIntoExistingActorRole(MissionIntentState state,
            MissionTurnOutcome o, int turn, AiAllocatorState allocState)
        {
            if (!o.HasScoutPayload || o.MoverArmyId == null)
                return false;

            MissionIntent owner = null;
            foreach (MissionIntent it in state.All)
            {
                if (it.Kind != MissionKind.Scout || it.Scout == null)
                    continue;
                if (it.PreferredMoverArmyId == o.MoverArmyId && !it.IntentKey.Equals(o.IntentKey))
                {
                    owner = it;
                    break;
                }
            }
            if (owner == null)
                return false;

            MissionIntentKey oldKey = owner.IntentKey;
            ApplyScoutPayload(owner.Scout, o);
            // A durable Surveil role keeps the Soft funding that marks it as a bound surveillance
            // commitment; switching to Explore/Refresh drops back to an unfunded frontier role.
            owner.Funding = o.ScoutKind == ScoutTargetKind.Surveil
                ? (owner.Funding == CommitmentTier.Hard ? CommitmentTier.Hard : CommitmentTier.Soft)
                : (owner.Funding == CommitmentTier.Hard ? CommitmentTier.Hard : CommitmentTier.None);
            owner.IntentKey = MissionIntentKey.For(owner);
            state.Remove(oldKey);
            state.Put(owner);

            o.MadeProgress = true;
            AdvanceIntent(owner, o, turn, state, allocState);
            AiDebugLog.Write($"[AI][V2] continuity — [{AiV2Trace.FormatCorrelation(o.Proposal)}] actor #{o.MoverArmyId} already "
                + $"owns {oldKey}; absorbed fresh {o.IntentKey} into that durable role (no duplicate intent)");
            return true;
        }

        // THE one writer of a Scout outcome's provisioned payload into a durable ScoutIntent — used
        // when a role is created, advanced and when an actor's existing role absorbs a fresh
        // mission, so the three can never drift apart again (Recon audit B4: the absorb copy lost
        // the Surveil baseline). Contact identity and baseline belong to Surveil only.
        private static void ApplyScoutPayload(ScoutIntent s, MissionTurnOutcome o)
        {
            s.FocusHex = o.FocusHex;
            s.Kind = o.ScoutKind;
            s.RequiresStealth = o.ScoutRequiresStealth;
            bool surveil = o.ScoutKind == ScoutTargetKind.Surveil;
            s.TrackedArmyId = surveil ? o.TrackedArmyId : null;
            s.BaselineObservedTurn = surveil ? o.BaselineObservedTurn : 0;
        }

        // Shared skeleton for the three Create*Intent methods below — was three independent,
        // hand-written copies of the same 12-field MissionIntent construction (see
        // Docs/ai-duplicate-methods-analysis.md, group M). Collapsing them here means a future
        // field added to MissionIntent only has to be wired up once, instead of risking a silently
        // half-initialized intent from a copy nobody remembered to update.
        private static MissionIntent NewIntent(MissionTurnOutcome o, int turn, MissionKind kind,
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
                PreferredMoverArmyId = o.MoverArmyId,
            };
        }

        private static void CreateIntent(MissionIntentState state, MissionTurnOutcome o, int turn)
        {
            var si = new ScoutIntent();
            ApplyScoutPayload(si, o);
            CommitmentTier funding = o.ScoutKind == ScoutTargetKind.Surveil ? CommitmentTier.Soft : CommitmentTier.None;
            MissionIntent intent = NewIntent(o, turn, MissionKind.Scout, funding, si);
            state.Put(intent);
            AiDebugLog.Write($"[AI][V2] continuity — [{AiV2Trace.FormatCorrelation(o.Proposal)}] {intent.IntentKey} created ({intent.Funding}, "
                + $"mover #{o.MoverArmyId}, {o.StepsMoved} step(s))");
        }

        // ATK §49 — the ONE predicate for "this intent is an offensive ground-combat operation an
        // ActiveDefence may preempt and later resume". Raid today; Attack becomes eligible by
        // adding its payload check here, so the five preempt/resume sites above never grow a
        // per-lane branch and no second suspended-key field is needed.
        internal static bool IsOffensiveGroundCombatIntent(MissionIntent i) =>
            i != null
            && ((i.Kind == MissionKind.Raid && i.Raid != null)
                || (i.Kind == MissionKind.Attack && i.Attack != null));

        // ATK §49/§73 — the ONE answer to "this offensive operation is marching on its objective
        // right now, with actor X, toward hex Y". ActiveDefence's preemption arithmetic (how far
        // off its route would borrowing this army drag it) needs exactly those two facts and must
        // not care which lane owns them: a Raid's own target hex and an Attack's Base/Citadel hex
        // are the same kind of fact. A leg that is reinforcing, returning or recovering is NOT
        // borrowable here — its actor is already mid-handoff or walking home.
        internal static bool TryOffensiveAssaultOperation(MissionIntent i, out int primaryArmyId,
            out HexCoord operationHex)
        {
            primaryArmyId = 0;
            operationHex = default;
            if (i == null || i.Status != IntentStatus.Active)
                return false;
            RaidIntent raid = i.Raid;
            if (raid != null)
            {
                if (raid.Phase != RaidMissionPhase.Assault || !raid.PrimaryArmyId.HasValue)
                    return false;
                primaryArmyId = raid.PrimaryArmyId.Value;
                operationHex = raid.LastKnownHex;
                return true;
            }
            AttackIntent attack = i.Attack;
            if (attack != null)
            {
                if (attack.Phase != AttackMissionPhase.Assault || !attack.PrimaryArmyId.HasValue
                    || !attack.Target.HasValue)
                    return false;
                primaryArmyId = attack.PrimaryArmyId.Value;
                operationHex = attack.Target.Hex;
                return true;
            }
            return false;
        }

        // The zero-value walk-home legs that leave their actor to fresh global allocation: a
        // completed Raid target's Return and (audit F6) an ActiveDefence Return. When that actor
        // is bound to a new ground-combat operation the fallback leg is retired, never kept as a
        // second owner of the same army.
        private static HashSet<int> AttackGatherUnavailable(MissionIntentState state,
            ISet<int> claims)
        {
            var unavailable = claims == null ? new HashSet<int>() : new HashSet<int>(claims);
            foreach (MissionIntent i in state.All)
            {
                if (i?.Raid != null && i.Raid.CompletedTargetAwaitingFreshDecision
                    && i.Raid.PrimaryArmyId.HasValue)
                    unavailable.Add(i.Raid.PrimaryArmyId.Value);
                if (i?.ActiveDefence != null && i.ActiveDefence.Phase == ActiveDefencePhase.Return
                    && i.ActiveDefence.PrimaryArmyId.HasValue)
                    unavailable.Add(i.ActiveDefence.PrimaryArmyId.Value);
            }
            return unavailable;
        }

        private static void RetireReturnFallbacksForActor(MissionIntentState state,
            int? actorId, string reason)
        {
            if (state == null || !actorId.HasValue)
                return;
            foreach (MissionIntent fallback in state.All.Where(i =>
                (i?.Raid != null && i.Raid.CompletedTargetAwaitingFreshDecision
                    && i.Raid.PrimaryArmyId == actorId)
                || (i?.ActiveDefence != null && i.ActiveDefence.Phase == ActiveDefencePhase.Return
                    && i.ActiveDefence.PrimaryArmyId == actorId)).ToList())
            {
                state.Remove(fallback.IntentKey);
                AiDebugLog.Write($"[AI][V2][{fallback.Kind}] {fallback.IntentKey} return fallback retired — "
                    + $"actor #{actorId.Value} reassigned by global allocation ({reason})");
            }
        }

        private static void CreateEconomyIntent(MissionIntentState state, MissionTurnOutcome o, int turn)
        {
            EconomyMissionTarget t = o.EconomyTarget;
            var ei = new EconomyIntent
            {
                Kind = t.Kind, TargetHex = t.TargetHex, ResourceType = t.ResourceType,
                BuilderArmyId = t.Kind == EconomyTaskKind.MobileCollection
                    || t.Kind == EconomyTaskKind.ReturnCollector ? t.BuilderArmyId : o.MoverArmyId,
                CollectorArmyId = t.Kind == EconomyTaskKind.MobileCollection
                    || t.Kind == EconomyTaskKind.ReturnCollector ? o.MoverArmyId : t.CollectorArmyId,
                CollectorSourceArmyId = t.CollectorSourceArmyId,
                ExpectedMarginalYield = t.ExpectedMarginalYield,
                SafeReturnHex = t.SafeReturnHex,
                ArrivalTurn = t.Kind == EconomyTaskKind.MobileCollection
                    && o.FinalHex.Equals(t.TargetHex) ? turn : -1,
                BuildCard = t.BuildCard, BuildResourceCost = t.BuildResourceCost,
                BuildApCost = t.BuildApCost, BuildValue = t.BuildValue,
                IntrinsicValue = o.Proposal?.BaseValue,
                MinimumFollowupAp = t.MinimumFollowupAp,
                Loaned = o.EconomyLoanSource.HasValue,
                LoanSource = o.EconomyLoanSource ?? default,
            };
            CommitmentTier funding = o.EconomyBuildCompleted ? CommitmentTier.Hard : CommitmentTier.Soft;
            MissionIntent intent = NewIntent(o, turn, MissionKind.Economy, funding, ei);
            state.Put(intent);
            AiDebugLog.Write($"[AI][V2][Economy] continuity create {intent.IntentKey} mover=#{o.MoverArmyId}");
        }

        private static void CreateDevelopmentIntent(MissionIntentState state,
            MissionTurnOutcome o, int turn)
        {
            DevelopmentMissionTarget target = o.DevelopmentTarget;
            if (target.Hero == null || !o.MoverArmyId.HasValue
                || state.All.Any(i => i?.Development?.Hero == target.Hero
                    || i?.Development != null && i.Development.Mode == target.Mode
                        && i.Development.FacilityHex.Equals(target.FacilityHex)))
                return;
            var objective = new DevelopmentIntent
            {
                Hero = target.Hero, HeroKey = target.HeroKey,
                FacilityHex = target.FacilityHex, Mode = target.Mode,
                IntrinsicValue = o.Proposal?.BaseValue ?? target.IntrinsicValue,
            };
            MissionIntent intent = NewIntent(o, turn, MissionKind.Development,
                CommitmentTier.Soft, objective);
            state.Put(intent);
            AiDebugLog.Write($"[AI][V2][Development] continuity create {intent.IntentKey} "
                + $"hero={target.HeroKey} actor=#{o.MoverArmyId}");
        }

        private static void RepayEconomyLoan(MissionIntentState state, MissionIntent economy,
            MissionTurnOutcome outcome)
        {
            MissionIntentKey? source = outcome?.EconomyLoanSource;
            if (!source.HasValue && economy?.Economy?.Loaned == true)
                source = economy.Economy.LoanSource;
            if (source.HasValue && state.TryGet(source.Value, out MissionIntent lender))
                ResumeEconomyLender(lender);
        }

        // The ONE retirement of an Economy intent: a borrowed Recon/Raid owner gets its actor back,
        // this owner's turn-scoped H/E/M/T/AP holds are released (Phase B may spend them for the
        // rest of the turn), then the intent goes. `outcome` may carry the loan of a mission that
        // never became durable. Economy audit B8.
        private static void RetireEconomyIntent(MissionIntentState state, MissionIntent intent,
            MissionTurnOutcome outcome, int turn, bool returnLoan = true)
        {
            if (returnLoan)
                RepayEconomyLoan(state, intent, outcome);
            if (intent == null)
                return;
            StrategicResourceReservationLedger.ReleaseByOwner(state.Owner, turn,
                EconomyMissionPlanner.OwnerKey(intent.LastAttemptKey));
            state.Remove(intent.IntentKey);
        }

        // Retirement of whatever intent an outcome names: an Economy intent through its own owner
        // above, any other kind is simply removed (a loan can only point at an Economy borrower).
        private static void RetireOutcomeIntent(MissionIntentState state, MissionIntent intent,
            MissionTurnOutcome outcome, int turn)
        {
            if (intent == null || intent.Kind == MissionKind.Economy)
            {
                RetireEconomyIntent(state, intent, outcome, turn);
                return;
            }
            state.Remove(intent.IntentKey);
        }

        // A proven route failure of an outbound Economy step (see ReconcileOutcome).
        private static bool IsEconomyRouteFailure(MissionTurnOutcome o) =>
            o.ProvisionFailureKindValue == ProvisionFailureKind.NoExecutableStep
            || (!o.ProvisionFailureKindValue.HasValue
                && (o.StopReason == ExecutionStopReason.NoSafeStep
                    || o.StopReason == ExecutionStopReason.MoveRejected));

        internal static bool IsCollectorEconomyIntent(MissionIntent i) =>
            i != null && i.Kind == MissionKind.Economy
            && (i.Economy?.Kind == EconomyTaskKind.MobileCollection
                || i.Economy?.Kind == EconomyTaskKind.ReturnCollector);

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

        // ReturnBuilder is preserved through every transient failure (a lost shelter is re-targeted
        // by ResolveActive, a blocked way home may clear) — but only while it still gets home:
        // a builder that has not advanced for commitmentMaxTurns is released, its lender resumed,
        // instead of holding the hero (and the lender) forever. Economy audit S1.
        private static void KeepReturnBuilder(MissionIntentState state, MissionIntent intent,
            MissionTurnOutcome o, int turn)
        {
            if (turn - intent.LastProgressTurn >= AiConfigV2.commitmentMaxTurns)
            {
                AiDebugLog.Write($"[AI][V2][Economy][Recovery] release {intent.IntentKey} — no progress "
                    + $"home since t{intent.LastProgressTurn}");
                RetireEconomyIntent(state, intent, o, turn);
                return;
            }
            intent.Status = IntentStatus.Active;
            intent.Suspended = SuspendReason.None;
            intent.LastReconciledTurn = turn;
            intent.StallTurns = 0;
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

        private static string Describe(MissionTurnOutcome o) =>
            o.Proposal != null && o.Proposal.Target is ScoutMissionTarget t
                ? ReconScoutKinds.Name(t.Kind)
                : o.Proposal != null && o.Proposal.Target is EconomyMissionTarget e
                    ? $"{e.Kind}@{e.TargetHex.Q},{e.TargetHex.R}"
                    : "?";
    }
}
