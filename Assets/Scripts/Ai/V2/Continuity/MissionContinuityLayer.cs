using System;
using System.Collections.Generic;
using System.Linq;
using Game.Cards;
using Game.Economy;
using Game.HexGrid;
using Game.Map;
using Game.Players;

namespace Game.Ai.V2
{
    // MissionContinuityLayer (Strategy V2 build-order step 7).
    // File-split (mechanical, no behaviour change) from MissionIntent.cs — see
    // Docs/ai-v2-file-split-refactor-tasks.md Task 3. Independent standalone types,
    // not a partial class.
    internal static class MissionContinuityLayer
    {
        internal static HexCoord? SelectEconomyRecoveryTarget(WorldSnapshot snap,
            PlayerSetupData player, ArmySnapshot actor, bool avoidCurrentHex = false)
        {
            if (snap?.Known?.Buildings == null || actor == null || player == null)
                return null;
            IEnumerable<Game.Ai.AiMapMemory.KnownBuilding> candidates =
                snap.Known.Buildings.Where(b => b.Owner == player
                    && (b.IsBase || b.IsStartingCitadel));
            if (avoidCurrentHex && candidates.Any(b => !b.Hex.Equals(actor.Hex)))
                candidates = candidates.Where(b => !b.Hex.Equals(actor.Hex));
            List<Game.Ai.AiMapMemory.KnownBuilding> ordered = candidates
                .OrderBy(b => HexGridMath.Distance(actor.Hex, b.Hex))
                .ThenBy(b => DemandLayer.EconomyRecoveryThreatExposure(snap, b.Hex))
                .ThenByDescending(b => b.IsStartingCitadel)
                .ThenBy(b => b.Hex.Q).ThenBy(b => b.Hex.R).ToList();
            if (ordered.Count > 0)
                return ordered[0].Hex;
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

            EconomyTaskKind kind = demand.EconomyBuildCard?.Definition?.cardType == CardType.Base
                ? EconomyTaskKind.FoundBase : EconomyTaskKind.BuildExtraction;
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
                ProjectedActivationApCost = demand.EconomyProjectedActivationApCost,
                ProjectedMaxMovement = demand.EconomyProjectedMaxMovement,
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
            intent.LastAttemptKey = new StableMissionKey(MissionKind.Economy,
                (int)kind,
                kind == EconomyTaskKind.ReturnBuilder ? builderArmyId
                    : demand.EconomyResourceType.HasValue
                        ? (int)demand.EconomyResourceType.Value + 1 : 0,
                objective.TargetHex.Q, objective.TargetHex.R);

            MissionIntentState state = MissionIntentRegistry.GetOrCreate(player);
            foreach (MissionIntent stale in state.All.Where(i => i != null
                         && i.Kind == MissionKind.Economy
                         && (i.Economy?.Kind == EconomyTaskKind.ReturnBuilder
                             // The same actor cannot hold both a recovery walk and this fresh
                             // delivery handoff — the new assignment supersedes its own recovery.
                             // A ReturnBuilder belonging to a DIFFERENT actor is untouched.
                             ? i.PreferredMoverArmyId == builderArmyId
                             : (!i.IntentKey.Equals(intent.IntentKey)
                                 || i.PreferredMoverArmyId != builderArmyId))).ToList())
            {
                if (stale.Economy?.Kind == EconomyTaskKind.ReturnBuilder && stale.Economy.Loaned
                    && state.TryGet(stale.Economy.LoanSource, out MissionIntent staleLender))
                    ResumeEconomyLender(staleLender);
                state.Remove(stale.IntentKey);
            }
            state.Put(intent);
            AiDebugLog.Write($"[AI][V2][Economy] materialization handoff {intent.IntentKey} "
                + $"actor=#{builderArmyId} funding=Soft");
            return intent;
        }

        internal static void BeginEconomyBuilderRecovery(PlayerSetupData player,
            WorldSnapshot snap, AxisDemand completedDemand, int builderArmyId, int turn)
        {
            if (player == null || snap == null || completedDemand?.TargetHex == null)
                return;
            MissionIntentState state = MissionIntentRegistry.GetOrCreate(player);
            MissionIntent economy = state.All.FirstOrDefault(i => i != null
                && i.Kind == MissionKind.Economy && i.PreferredMoverArmyId == builderArmyId);
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
                if (economy != null) state.Remove(economy.IntentKey);
                return;
            }

            EconomyTaskKind completedKind =
                completedDemand.Capability == CapabilityKind.EconomicExpansionBase
                    ? EconomyTaskKind.FoundBase : EconomyTaskKind.BuildExtraction;
            bool threatened = DemandLayer.EconomyBuilderUnderImmediateThreat(snap, actor.Hex);
            bool alreadyProtected = (completedKind == EconomyTaskKind.FoundBase && !threatened)
                || IsProtectedEconomyHex(snap, player, actor.Hex);
            HexCoord? target = SelectEconomyRecoveryTarget(
                snap, player, actor, avoidCurrentHex: threatened);
            if (!RequiresEconomyBuilderRecovery(completedKind, lender, threatened,
                    alreadyProtected, target.HasValue))
            {
                ResumeEconomyLender(lender);
                if (economy != null) state.Remove(economy.IntentKey);
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
            recovery.LastAttemptKey = new StableMissionKey(MissionKind.Economy,
                (int)EconomyTaskKind.ReturnBuilder, builderArmyId,
                target.Value.Q, target.Value.R);
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

        private static bool IsProtectedEconomyHex(WorldSnapshot snap,
            PlayerSetupData player, HexCoord hex) =>
            snap?.Known?.Buildings != null && snap.Known.Buildings.Any(b => b.Owner == player
                && b.Hex.Equals(hex) && (b.IsBase || b.IsStartingCitadel));

        private static void ResumeEconomyLender(MissionIntent lender)
        {
            if (lender != null && lender.Status == IntentStatus.Suspended
                && lender.Suspended == SuspendReason.EconomyLoan)
            {
                lender.Status = IntentStatus.Active;
                lender.Suspended = SuspendReason.None;
            }
        }

        // AGG-RAID §5 — `aggressionObjectives` is the freshly sorted, neutral-only objective list,
        // handed in exactly the way `reconObjectives` already is. It is what lets a durable Raid
        // role be RE-ORIENTED onto the next neutral once its current target is confirmed gone,
        // mirroring the Scout re-focus pattern instead of retiring and re-creating the operation.
        public static List<MissionIntent> ResolveActive(PlayerSetupData player, WorldSnapshot snap,
            IReadOnlyList<ReconObjective> reconObjectives = null,
            IReadOnlyList<AggressionObjective> aggressionObjectives = null)
        {
            var active = new List<MissionIntent>();
            MissionIntentState state = MissionIntentRegistry.GetOrCreate(player);
            if (state.Count == 0)
                return active;

            bool underSiege = snap?.Threat?.UnderSiege == true;
            var dead = new List<MissionIntentKey>();
            var rekeys = new List<(MissionIntentKey Old, MissionIntent Intent)>();
            MissionIntent primaryEconomyBuild = state.All
                .Where(i => i?.Kind == MissionKind.Economy && i.Economy != null
                    && i.Economy.Kind != EconomyTaskKind.ReturnBuilder)
                .OrderByDescending(i => i.StepsMovedTotal)
                .ThenByDescending(i => i.CumulativeApSpent)
                .ThenBy(i => i.CreatedTurn)
                .ThenBy(i => i.IntentKey)
                .FirstOrDefault();
            var liveLoanSources = new HashSet<MissionIntentKey>(state.All
                .Where(i => i?.Kind == MissionKind.Economy && i.Economy?.Loaned == true)
                .Select(i => i.Economy.LoanSource));
            foreach (MissionIntent orphanedDonor in state.All.Where(i => i != null
                && i.Status == IntentStatus.Suspended && i.Suspended == SuspendReason.EconomyLoan
                && !liveLoanSources.Contains(i.IntentKey)))
            {
                orphanedDonor.Status = IntentStatus.Active;
                orphanedDonor.Suspended = SuspendReason.None;
                AiDebugLog.Write($"[AI][V2][Economy][Loan] orphan repair donor={orphanedDonor.IntentKey}");
            }

            // Spec §1 — foci currently owned by ground scout intents, so a re-focus never lands two
            // durable intents on the same waypoint. Mutated as intents are re-pointed below.
            var scoutFoci = new HashSet<HexCoord>();
            foreach (MissionIntent i in state.All)
                if (i.Scout != null && i.Scout.Kind != ScoutTargetKind.Surveil)
                    scoutFoci.Add(i.Scout.FocusHex);

            // AGG-RAID §5 — neutral targets already owned by a durable Raid, so a re-orientation
            // never lands two Raid operations on the same neutral target (either kind).
            var activeRaidTargets = new HashSet<RaidTargetRef>();
            foreach (MissionIntent i in state.All)
                if (i?.Raid != null && i.Raid.Target.HasValue)
                    activeRaidTargets.Add(i.Raid.Target);

            foreach (MissionIntent intent in state.All.ToList())
            {
                if (intent.Kind == MissionKind.Economy)
                {
                    EconomyIntent ei = intent.Economy;
                    if (ei?.Kind != EconomyTaskKind.ReturnBuilder
                        && !object.ReferenceEquals(intent, primaryEconomyBuild))
                    {
                        MissionIntent duplicateLender = null;
                        if (ei?.Loaned == true)
                            state.TryGet(ei.LoanSource, out duplicateLender);
                        ResumeEconomyLender(duplicateLender);
                        StrategicResourceReservationLedger.ReleaseByOwner(player,
                            snap?.TurnNumber ?? 0,
                            EconomyMissionPlanner.OwnerKey(intent.LastAttemptKey));
                        dead.Add(intent.IntentKey);
                        AiDebugLog.Write($"[AI][V2][Economy] retire alternative {intent.IntentKey} "
                            + $"committed={primaryEconomyBuild?.IntentKey}");
                        continue;
                    }
                    ArmySnapshot actor = snap?.Self?.Armies?.FirstOrDefault(a => a != null
                        && a.ArmyId == intent.PreferredMoverArmyId && a.HasHero && !a.IsPrison && !a.IsAir);
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
                            MissionIntent lender = null;
                            if (ei?.Loaned == true) state.TryGet(ei.LoanSource, out lender);
                            ResumeEconomyLender(lender);
                            dead.Add(intent.IntentKey);
                            AiDebugLog.Write($"[AI][V2][Economy][Recovery] retire {intent.IntentKey} "
                                + $"arrived={(completed ? 1 : 0)} actor={(actor != null ? 1 : 0)} "
                                + $"target={(targetValid ? 1 : 0)}");
                            continue;
                        }
                        if (intent.Status == IntentStatus.Suspended)
                        {
                            intent.Status = IntentStatus.Active;
                            intent.Suspended = SuspendReason.None;
                        }
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
                        MissionIntent lender = null;
                        if (ei?.Loaned == true) state.TryGet(ei.LoanSource, out lender);
                        ResumeEconomyLender(lender);
                        StrategicResourceReservationLedger.ReleaseByOwner(player,
                            snap?.TurnNumber ?? 0, EconomyMissionPlanner.OwnerKey(intent.LastAttemptKey));
                        dead.Add(intent.IntentKey);
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
                    if (intent.Status == IntentStatus.Suspended
                        && (intent.Suspended == SuspendReason.PoolExhausted
                            || intent.Suspended == SuspendReason.CapabilityUnavailable))
                    {
                        intent.Status = IntentStatus.Active;
                        intent.Suspended = SuspendReason.None;
                    }
                    if (intent.Status == IntentStatus.Active) active.Add(intent);
                    continue;
                }
                if (intent.Kind == MissionKind.Raid)
                {
                    RaidIntent ri = intent.Raid;
                    if (ri == null)
                    {
                        dead.Add(intent.IntentKey);
                        AiDebugLog.Write($"[AI][V2] continuity — {intent.IntentKey} retired at turn start (no raid objective)");
                        continue;
                    }

                    // §5 actor ownership — a LOST SUPPORT actor releases only the support claim;
                    // the primary Raid survives untouched. Only revert to Assault if the primary can
                    // actually clear the target alone (same PrimaryClearsTarget gate AdvanceRaidPhase
                    // uses below) — otherwise this reset would set Phase=Assault BEFORE
                    // AdvanceRaidPhase runs later in this same pass, and its own analogous re-check
                    // (`ri.Phase == RaidMissionPhase.Reinforcement && ...`) would then silently no-op
                    // because Phase is already Assault, letting a still-too-weak primary be proposed
                    // for an ordinary Assault this step. A support in SupportReturn is walking home
                    // after a successful swap, not carrying reinforcement — its loss there is handled
                    // separately, below, without reverting the phase.
                    if (ri.SupportArmyId.HasValue && ri.Phase == RaidMissionPhase.Reinforcement
                        && !RaidSupportActorAlive(snap, ri.SupportArmyId.Value))
                    {
                        int lostSupportId = ri.SupportArmyId.Value;
                        ri.SupportArmyId = null;
                        ri.ReinforcementRequestedTurn = -1;
                        bool nowClears = PrimaryClearsTarget(snap, player, ri.PrimaryArmyId, ri.Target);
                        if (nowClears)
                            ri.Phase = RaidMissionPhase.Assault;
                        AiDebugLog.Write($"[AI][V2][Raid] {intent.IntentKey} support #{lostSupportId} lost; "
                            + $"released support claim, primary raid kept, phase={ri.Phase} "
                            + $"(primaryNowClears={(nowClears ? 1 : 0)})");
                    }
                    // §SupportReturn — a lost returning support must close the return leg as well as
                    // release the actor claim. Reuse the same lifecycle transition as physical
                    // arrival so Phase cannot remain SupportReturn with no support actor.
                    else if (ri.SupportArmyId.HasValue && ri.PrimaryArmyId.HasValue
                        && ri.Phase == RaidMissionPhase.SupportReturn
                        && !RaidSupportActorAlive(snap, ri.SupportArmyId.Value))
                    {
                        int lostSupportId = ri.SupportArmyId.Value;
                        CompleteRaidSupportReturn(player, snap, ri.PrimaryArmyId.Value,
                            $"support #{lostSupportId} lost en route home");
                        intent.LastProgressTurn = snap?.TurnNumber ?? intent.LastProgressTurn;
                        intent.StallTurns = 0;
                    }

                    // §5 actor ownership — a LOST PRIMARY ends the operation. Any support is
                    // released with it (ActorCommitments stops claiming the moment the intent dies).
                    // Return is phase-sensitive: once the objective is homeward movement, the same
                    // live/non-empty ground-container gate used by ProvisionReturn is sufficient.
                    // Combat phases, including SupportReturn (where primary still holds the target),
                    // retain the strict structural Raid gate.
                    bool primaryActorAlive = ri.PrimaryArmyId.HasValue
                        && (ri.Phase == RaidMissionPhase.Return
                            ? RaidSupportActorAlive(snap, ri.PrimaryArmyId.Value)
                            : RaidPrimaryActorAlive(snap, ri.PrimaryArmyId.Value));
                    if (ri.OperationStarted && ri.PrimaryArmyId.HasValue && !primaryActorAlive)
                    {
                        dead.Add(intent.IntentKey);
                        AiDebugLog.Write($"[AI][V2][Raid] {intent.IntentKey} retired — primary "
                            + $"#{ri.PrimaryArmyId.Value} is no longer usable for phase {ri.Phase} "
                            + $"(support #{(ri.SupportArmyId.HasValue ? ri.SupportArmyId.Value.ToString() : "none")} released)");
                        continue;
                    }

                    // A SupportReturn already satisfied in the fresh snapshot is a continuity fact,
                    // not a provisioning failure. Resolve it here through the same canonical
                    // transition Execution uses on physical arrival. Otherwise ProvisionReturn's
                    // generic TargetSatisfied bypasses Raid phase payload and ReconcileOutcome can
                    // retire the whole durable campaign instead of only releasing the support.
                    if (ri.Phase == RaidMissionPhase.SupportReturn
                        && ri.PrimaryArmyId.HasValue && ri.SupportArmyId.HasValue
                        && ri.SupportReturnHex.HasValue)
                    {
                        ArmySnapshot returningSupport = snap?.Self?.Armies?.FirstOrDefault(a => a != null
                            && a.ArmyId == ri.SupportArmyId.Value);
                        if (returningSupport != null
                            && returningSupport.Hex.Equals(ri.SupportReturnHex.Value))
                        {
                            int returnedSupportId = returningSupport.ArmyId;
                            CompleteRaidSupportReturn(player, snap, ri.PrimaryArmyId.Value,
                                $"support #{returnedSupportId} already home during reconciliation");
                            intent.LastProgressTurn = snap?.TurnNumber ?? intent.LastProgressTurn;
                            intent.StallTurns = 0;
                        }
                    }

                    // §5/§SupportReturn phase machine. Return and SupportReturn never consult target
                    // validity (their objective is a base, not the Raid target); every other phase
                    // keeps the existing fog!=death rule.
                    bool isReturnLeg = ri.Phase == RaidMissionPhase.Return || ri.Phase == RaidMissionPhase.SupportReturn;
                    if (!isReturnLeg
                        && !AdvanceRaidPhase(player, snap, intent, ri, aggressionObjectives,
                            activeRaidTargets, rekeys))
                    {
                        dead.Add(intent.IntentKey);
                        continue;
                    }

                    if (!isReturnLeg
                        && !RaidObjectiveEvaluator.IsIntentStillValid(snap, ri))
                    {
                        dead.Add(intent.IntentKey);
                        AiDebugLog.Write($"[AI][V2] continuity — {intent.IntentKey} retired at turn start (raid target no longer valid)");
                        continue;
                    }
                    if (ri.Phase == RaidMissionPhase.Return
                        && !ReturnBaseStillValid(snap, player, ri.PrimaryArmyId, ri.ReturnHex))
                    {
                        // §11 — losing the chosen base is a controlled RETARGET, never a stall.
                        HexCoord? replacement = SelectReturnBase(snap, player, ri.PrimaryArmyId);
                        if (replacement == null)
                        {
                            dead.Add(intent.IntentKey);
                            AiDebugLog.Write($"[AI][V2][Raid] {intent.IntentKey} retired — return base lost "
                                + "and no replacement base exists");
                            continue;
                        }
                        AiDebugLog.Write($"[AI][V2][Raid] {intent.IntentKey} return base retargeted to "
                            + $"({replacement.Value.Q},{replacement.Value.R})");
                        ri.ReturnHex = replacement;
                        intent.StallTurns = 0;
                    }
                    // §SupportReturn — same controlled-retarget rule for the support's own home.
                    // Losing the support (already handled above) always releases the claim before
                    // this point can even run against a stale actor.
                    if (ri.Phase == RaidMissionPhase.SupportReturn && ri.SupportArmyId.HasValue
                        && !ReturnBaseStillValid(snap, player, ri.SupportArmyId, ri.SupportReturnHex))
                    {
                        HexCoord? replacement = SelectReturnBase(snap, player, ri.SupportArmyId);
                        if (replacement == null)
                        {
                            // §SupportReturn — no own base to send it to must never wedge the Raid:
                            // release the support and let the primary carry on being re-evaluated.
                            AiDebugLog.Write($"[AI][V2][Raid] {intent.IntentKey} support #{ri.SupportArmyId.Value} "
                                + "has no reachable home base — released, primary continues "
                                + "reason=no_replacement_base_for_support_return");
                            ri.SupportArmyId = null;
                            ri.SupportReturnHex = null;
                            bool nowClears = PrimaryClearsTarget(snap, player, ri.PrimaryArmyId, ri.Target);
                            ri.Phase = nowClears ? RaidMissionPhase.Assault : RaidMissionPhase.Reinforcement;
                        }
                        else
                        {
                            AiDebugLog.Write($"[AI][V2][Raid] {intent.IntentKey} support return base "
                                + $"retargeted to ({replacement.Value.Q},{replacement.Value.R})");
                            ri.SupportReturnHex = replacement;
                            intent.StallTurns = 0;
                        }
                    }
                    if (intent.Status == IntentStatus.Suspended
                        && (intent.Suspended == SuspendReason.PoolExhausted
                            || intent.Suspended == SuspendReason.CapabilityUnavailable))
                    {
                        intent.Status = IntentStatus.Active;
                        intent.Suspended = SuspendReason.None;
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

                if (intent.Status == IntentStatus.Suspended
                    && (intent.Suspended == SuspendReason.PoolExhausted
                        || intent.Suspended == SuspendReason.CapabilityUnavailable))
                {
                    intent.Status = IntentStatus.Active;
                    intent.Suspended = SuspendReason.None;
                }

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

            if (state.Count > 0)
                AiDebugLog.WriteVerbose($"[AI][V2] continuity — {state.Count} intent(s): "
                    + string.Join(" ", state.All.Select(i =>
                        $"{i.IntentKey}[{i.Funding}/{i.Status}{(i.Suspended != SuspendReason.None ? ":" + i.Suspended : "")} "
                        + $"t{i.TurnsActive} stall{i.StallTurns}{(i.PreferredMoverArmyId.HasValue ? " mv#" + i.PreferredMoverArmyId : "")}]")));

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
            return active;
        }

        // A mover that already advanced this turn owns its lane through the productive typed loop.
        // Reconciliation may run repeatedly after each step; trimming it here would manufacture
        // surplus churn and immediately recreate effectively the same intent.
        internal static bool IsProductiveReconLaneThisTurn(MissionIntent intent, int turn) =>
            intent != null && intent.Kind == MissionKind.Scout && intent.Scout != null
            && intent.PreferredMoverArmyId.HasValue && intent.LastProgressTurn == turn;

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
            var scoutLanes = active.Where(i => i.Kind == MissionKind.Scout && i.Scout != null
                && (!i.PreferredMoverArmyId.HasValue || !airActorIds.Contains(i.PreferredMoverArmyId.Value)))
                .ToList();
            if (scoutLanes.Count <= 1)
                return;

            var runnable = reconObjectives
                .Where(o => o != null && o.BaseValue > 0f)
                .OrderByDescending(o => o.BaseValue)
                .ThenBy(o => o.IntentKey)
                .ToList();
            int desired = System.Math.Max(1, ReconConcurrencyPolicy.DesiredTotal(snap, runnable));

            var shedable = scoutLanes
                .Where(i => i.Funding < CommitmentTier.Hard
                    && !IsProductiveReconLaneThisTurn(i, snap.TurnNumber))
                .OrderBy(i => (int)i.Funding)
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
            if (snap?.MapKnowledge == null || s == null || s.Kind == ScoutTargetKind.Surveil)
                return false;

            HexCoord old = s.FocusHex;
            HexCoord? pick = null;
            int bestDist = int.MaxValue;

            if (ReconScoutKinds.IsRefresh(s.Kind))
            {
                foreach (KeyValuePair<HexCoord, int> kv in ReconIntelSnapshotRegistry.LastObservedFor(snap))
                {
                    if (kv.Key.Equals(old) || ownedFoci.Contains(kv.Key))
                        continue;
                    int age = System.Math.Max(0, snap.TurnNumber - kv.Value);
                    if (age < AiConfigV2.scoutSurveilStaleTurnsLo)
                        continue;
                    if (!ScoutObjectiveEvaluator.IsRefreshFocusRunnable(snap, kv.Key))
                        continue;
                    int d = HexGridMath.Distance(old, kv.Key);
                    if (d < bestDist) { bestDist = d; pick = kv.Key; }
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
                    int d = HexGridMath.Distance(old, f.Hex);
                    if (d < bestDist) { bestDist = d; pick = f.Hex; }
                }
            }

            if (pick == null)
                return false;
            ownedFoci.Remove(old);
            ownedFoci.Add(pick.Value);
            s.FocusHex = pick.Value;
            return true;
        }

        // =====================================================================================
        //  AGG-RAID §5 / §11 — the Raid phase machine and its actor/base helpers.
        // =====================================================================================

        // Is the durable primary still the kind of army Raid provisioning would accept? Uses the
        // SAME structural snapshot predicate ActorCommitments applies in combat phases.
        internal static bool RaidPrimaryActorAlive(WorldSnapshot snap, int armyId)
        {
            ArmySnapshot a = snap?.Self?.Armies?.FirstOrDefault(x => x != null && x.ArmyId == armyId);
            return a != null && a.IsStructuralRaidActor;
        }

        // A support/return actor only has to be a live, mobile, non-air ground container — during
        // these transit legs it is carrying bodies or itself home, not qualifying for fresh combat.
        internal static bool RaidSupportActorAlive(WorldSnapshot snap, int armyId)
        {
            ArmySnapshot a = snap?.Self?.Armies?.FirstOrDefault(x => x != null && x.ArmyId == armyId);
            return a != null && !a.IsPrison && !a.IsAir && a.MemberCount > 0;
        }

        // The §5 transition table, run once per reconciliation pass against FRESH objectives:
        //   target completed -> next neutral exists -> primary clears it   => Assault
        //                                           -> primary too weak    => Reinforcement
        //                    -> no neutral targets left                    => Return
        // Returns false only when the operation cannot continue in any phase (caller retires it).
        private static bool AdvanceRaidPhase(PlayerSetupData player, WorldSnapshot snap,
            MissionIntent intent, RaidIntent ri,
            IReadOnlyList<AggressionObjective> aggressionObjectives,
            HashSet<RaidTargetRef> activeRaidTargets,
            List<(MissionIntentKey Old, MissionIntent Intent)> rekeys)
        {
            // Loss of VISIBILITY is never proof of destruction — IsObjectiveSatisfiedLive is the
            // positive live read (ours / another player's roster / honest map memory / event-guard
            // Consumed state, per target kind).
            bool targetGone = ri.Target.HasValue
                && (RaidObjectiveEvaluator.IsObjectiveSatisfiedLive(player, ri.Target)
                    || RaidObjectiveEvaluator.IsKnownTargetNoLongerNeutral(snap, ri.Target));
            if (!targetGone)
            {
                // A Reinforcement whose primary has since become strong enough again returns to
                // Assault on its own; Provisioning re-checks this after every roster transfer too.
                if (ri.Phase == RaidMissionPhase.Reinforcement && !ri.SupportArmyId.HasValue
                    && PrimaryClearsTarget(snap, player, ri.PrimaryArmyId, ri.Target))
                {
                    ri.Phase = RaidMissionPhase.Assault;
                    AiDebugLog.Write($"[AI][V2][Raid] {intent.IntentKey} phase Reinforcement -> Assault "
                        + "reason=primary_clears_current_target_again");
                }
                // AGG-RAID P1#1 — the symmetric direction. This used to be a side effect of
                // AggressionDemandEvaluator.Build (now a pure snapshot read); Continuity is the sole
                // owner of durable Phase, so a bound primary that no longer clears its CURRENT
                // (possibly already re-oriented) target is moved to Reinforcement here, before
                // Demand/Missions run this same pass.
                else if (ri.Phase == RaidMissionPhase.Assault && ri.PrimaryArmyId.HasValue
                    && !PrimaryClearsTarget(snap, player, ri.PrimaryArmyId, ri.Target))
                {
                    ri.Phase = RaidMissionPhase.Reinforcement;
                    AiDebugLog.Write($"[AI][V2][Raid] {intent.IntentKey} phase Assault -> Reinforcement "
                        + "reason=primary_no_longer_clears_current_target");
                }
                return true;
            }

            AggressionObjective next = (aggressionObjectives
                    ?? (IReadOnlyList<AggressionObjective>)System.Array.Empty<AggressionObjective>())
                .Where(o => o != null && o.TargetIsNeutral && o.Target.HasValue
                    && !o.Target.Equals(ri.Target)
                    && !activeRaidTargets.Contains(o.Target))
                .OrderByDescending(o => o.BaseValue)
                .ThenBy(o => o.Target.DiagnosticLabel)
                .FirstOrDefault();

            if (next == null)
            {
                HexCoord? home = SelectReturnBase(snap, player, ri.PrimaryArmyId);
                if (home == null)
                {
                    AiDebugLog.Write($"[AI][V2][Raid] {intent.IntentKey} target completed, no further "
                        + "neutral target and no return base — retiring");
                    return false;
                }
                ri.Phase = RaidMissionPhase.Return;
                ri.ReturnHex = home;
                ri.SupportArmyId = null;
                ri.ReinforcementRequestedTurn = -1;
                intent.StallTurns = 0;
                intent.LastProgressTurn = snap?.TurnNumber ?? intent.LastProgressTurn;
                AiDebugLog.Write($"[AI][V2][Raid] {intent.IntentKey} target completed, no neutral targets "
                    + $"left -> Return to ({home.Value.Q},{home.Value.R}) with primary #{ri.PrimaryArmyId}");
                return true;
            }

            // Re-orient the SAME durable operation onto the next neutral (identity is the target
            // itself, so the registry slot is re-keyed in place — accumulated AP/steps kept).
            MissionIntentKey oldKey = intent.IntentKey;
            activeRaidTargets.Remove(ri.Target);
            ri.Target = next.Target;
            ri.LastKnownHex = next.LastKnownHex;
            ri.TargetIsNeutral = true;
            activeRaidTargets.Add(ri.Target);
            intent.IntentKey = MissionIntentKey.For(intent);
            if (!intent.IntentKey.Equals(oldKey))
                rekeys.Add((oldKey, intent));
            intent.StallTurns = 0;
            intent.LastProgressTurn = snap?.TurnNumber ?? intent.LastProgressTurn;

            // §5 — the new target is a FRESH decision: the primary is re-checked against the strict
            // start gate, never against the stale gate that admitted the previous target.
            bool strongEnough = PrimaryClearsTarget(snap, player, ri.PrimaryArmyId, ri.Target);
            ri.Phase = strongEnough ? RaidMissionPhase.Assault : RaidMissionPhase.Reinforcement;
            if (strongEnough)
            {
                ri.SupportArmyId = null;
                ri.ReinforcementRequestedTurn = -1;
            }
            AiDebugLog.Write($"[AI][V2][Raid] {oldKey} target completed -> re-oriented to {intent.IntentKey} "
                + $"phase={ri.Phase} primary=#{ri.PrimaryArmyId} worthIt={(strongEnough ? 1 : 0)}");
            return true;
        }

        // AGG-RAID §9/§10 — Execution has finished the atomic rendezvous handoff. Continuity (the
        // sole owner of intent state) releases the support claim and returns the operation to
        // Assault ONLY when the post-transfer roster actually re-cleared the shared estimator.
        internal static void CompleteRaidReinforcement(PlayerSetupData player, int primaryArmyId,
            bool rosterVerified, string detail)
        {
            if (player == null)
                return;
            MissionIntentState state = MissionIntentRegistry.GetOrCreate(player);
            MissionIntent intent = state.All.FirstOrDefault(i => i?.Raid != null
                && i.Raid.PrimaryArmyId == primaryArmyId);
            if (intent == null)
                return;
            RaidIntent ri = intent.Raid;
            ri.SupportArmyId = null;
            ri.ReinforcementRequestedTurn = -1;
            ri.Phase = rosterVerified ? RaidMissionPhase.Assault : RaidMissionPhase.Reinforcement;
            AiDebugLog.Write($"[AI][V2][Raid] {intent.IntentKey} reinforcement complete — "
                + $"phase={ri.Phase} verified={(rosterVerified ? 1 : 0)} {detail}");
        }

        // AGG-RAID §SupportReturn — a full/full swap displaced a primary member into support;
        // Execution has finished the swap and now hands the support army a Return leg home while
        // the primary stays put on the target. Called once, from the same execution step that ran
        // ArmyActions.SwapMembers.
        internal static void BeginRaidSupportReturn(PlayerSetupData player, WorldSnapshot snap,
            int primaryArmyId, int supportArmyId, string detail)
        {
            if (player == null)
                return;
            MissionIntentState state = MissionIntentRegistry.GetOrCreate(player);
            MissionIntent intent = state.All.FirstOrDefault(i => i?.Raid != null
                && i.Raid.PrimaryArmyId == primaryArmyId);
            if (intent == null)
                return;
            RaidIntent ri = intent.Raid;
            HexCoord? home = SelectReturnBase(snap, player, supportArmyId);
            if (home == null)
            {
                // No base to send it home to — never block the Raid on this. Release the support
                // right away and let the usual reinforcement-loss path re-evaluate the primary.
                ri.SupportArmyId = null;
                ri.ReinforcementRequestedTurn = -1;
                bool nowClears = PrimaryClearsTarget(snap, player, ri.PrimaryArmyId, ri.Target);
                ri.Phase = nowClears ? RaidMissionPhase.Assault : RaidMissionPhase.Reinforcement;
                AiDebugLog.Write($"[AI][V2][Raid] {intent.IntentKey} support #{supportArmyId} swapped "
                    + "out but no reachable home base exists — released immediately "
                    + $"phase={ri.Phase} {detail}");
                return;
            }
            ri.SupportArmyId = supportArmyId;
            ri.SupportReturnHex = home;
            ri.Phase = RaidMissionPhase.SupportReturn;
            AiDebugLog.Write($"[AI][V2][Raid] {intent.IntentKey} full/full swap complete — support "
                + $"#{supportArmyId} -> SupportReturn home ({home.Value.Q},{home.Value.R}); "
                + $"primary #{primaryArmyId} holds target {ri.Target.DiagnosticLabel} {detail}");
        }

        // AGG-RAID §SupportReturn — the return leg ended (arrival or actor loss). Release its claim,
        // clear the leg, and re-evaluate the primary against the current target exactly like any
        // other reinforcement-completion edge.
        internal static void CompleteRaidSupportReturn(PlayerSetupData player, WorldSnapshot snap,
            int primaryArmyId, string detail)
        {
            if (player == null)
                return;
            MissionIntentState state = MissionIntentRegistry.GetOrCreate(player);
            MissionIntent intent = state.All.FirstOrDefault(i => i?.Raid != null
                && i.Raid.PrimaryArmyId == primaryArmyId);
            if (intent == null)
                return;
            RaidIntent ri = intent.Raid;
            ri.SupportArmyId = null;
            ri.SupportReturnHex = null;
            bool stillHasTarget = ri.Target.HasValue
                && !RaidObjectiveEvaluator.IsObjectiveSatisfiedLive(player, ri.Target)
                && !RaidObjectiveEvaluator.IsKnownTargetNoLongerNeutral(snap, ri.Target);
            if (!stillHasTarget)
            {
                // The target finished while support was walking home — let the ordinary phase
                // machine pick the next target (or Return) on the next ResolveActive pass; parking
                // in Assault here is a safe default since AdvanceRaidPhase re-derives everything.
                ri.Phase = RaidMissionPhase.Assault;
                AiDebugLog.Write($"[AI][V2][Raid] {intent.IntentKey} support return ended; target already "
                    + $"resolved while away — will re-orient next pass {detail}");
                return;
            }
            bool nowClears = PrimaryClearsTarget(snap, player, ri.PrimaryArmyId, ri.Target);
            ri.Phase = nowClears ? RaidMissionPhase.Assault : RaidMissionPhase.Reinforcement;
            AiDebugLog.Write($"[AI][V2][Raid] {intent.IntentKey} support return ended; released — "
                + $"phase={ri.Phase} primaryClears={(nowClears ? 1 : 0)} {detail}");
        }

        // §5/§6 — the one shared "can the primary take THIS target right now" question. Fresh
        // target => fresh start gate.
        internal static bool PrimaryClearsTarget(WorldSnapshot snap, PlayerSetupData player,
            int? primaryArmyId, RaidTargetRef target)
        {
            if (snap == null || !primaryArmyId.HasValue || !target.HasValue)
                return false;
            GroundCombatAssemblyPlan plan = GroundCombatAssemblyPlanner.PlanForArmyAt(
                snap, AiV2Util.KnownDefenders(snap, target), primaryArmyId.Value,
                AiConfigV2.raidMinViableWinChance);
            return plan.Feasible;
        }

        // §11 / AGG-RAID P1#3 — is the fixed return base still ours AND still structurally
        // reachable? "Structurally" is the key word: this reads the GENUINE, any-number-of-turns
        // route-existence fact WorldAnalysis froze onto the mover's ArmySnapshot
        // (SafeStepPathing.FindSafePath — the same oracle Provisioning uses live), never "reachable
        // THIS turn" — a merely-temporarily-blocked step must NOT trigger a retarget, only a base
        // with NO safe route at all. Shared by the primary's Return leg and the support's
        // SupportReturn leg — `moverArmyId` is whichever of the two is walking home.
        internal static bool ReturnBaseStillValid(WorldSnapshot snap, PlayerSetupData player,
            int? moverArmyId, HexCoord? hex)
        {
            if (!hex.HasValue || snap?.Known?.Buildings == null)
                return false;
            bool ownedBase = snap.Known.Buildings.Any(b => b.Owner == player && b.Hex.Equals(hex.Value)
                && (b.IsBase || b.IsStartingCitadel));
            if (!ownedBase)
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
        internal static HexCoord? SelectReturnBase(WorldSnapshot snap, PlayerSetupData player, int? moverArmyId)
        {
            if (snap?.Known?.Buildings == null || player == null)
                return null;
            List<Game.Ai.AiMapMemory.KnownBuilding> bases = snap.Known.Buildings
                .Where(b => b.Owner == player && (b.IsBase || b.IsStartingCitadel))
                .ToList();
            if (bases.Count == 0)
                return null;

            ArmySnapshot mover = moverArmyId.HasValue
                ? snap.Self?.Armies?.FirstOrDefault(a => a != null && a.ArmyId == moverArmyId.Value)
                : null;
            int moveBudget = System.Math.Max(1, mover?.MaxMovement ?? AiConfigV2.etaFallbackMoveBudget);

            // AGG-RAID P1#3 — a structurally reachable base always outranks an unreachable one,
            // ahead of every other tie-break. Falls back to the old distance-only ordering among
            // bases with the SAME reachability, and — if genuinely none are reachable right now —
            // still returns the best-by-distance candidate rather than stranding the operation on a
            // signal that may only be a transient blockade.
            bool Reachable(Game.Ai.AiMapMemory.KnownBuilding b) =>
                mover == null || !mover.IsStructuralRaidActor
                || mover.ReachableOwnBaseHexes.Contains(b.Hex);

            return bases
                .OrderByDescending(b => Reachable(b) ? 1 : 0)
                .ThenByDescending(b => BaseCollectedAmount(snap, b.Hex))
                .ThenByDescending(b => BaseHasDevelopmentInfrastructure(snap, player, b.Hex) ? 1 : 0)
                .ThenByDescending(b => BaseOwnPowerAt(snap, b.Hex))
                .ThenBy(b => BaseThreatSeverityAt(snap, b.Hex))
                .ThenBy(b => mover == null ? 0
                    : AiV2Util.CeilDiv(HexGridMath.Distance(mover.Hex, b.Hex), moveBudget))
                .ThenByDescending(b => b.IsStartingCitadel ? 1 : 0)
                .ThenBy(b => b.Hex.Q).ThenBy(b => b.Hex.R)
                .Select(b => (HexCoord?)b.Hex)
                .FirstOrDefault();
        }

        private static float BaseCollectedAmount(WorldSnapshot snap, HexCoord hex)
        {
            float total = 0f;
            foreach (Game.Ai.AiMapMemory.KnownBuilding b in snap.Known.Buildings)
            {
                if (!b.Hex.Equals(hex) || b.CollectedAmounts == null) continue;
                foreach (int amount in b.CollectedAmounts)
                    total += System.Math.Max(0, amount);
            }
            return total;
        }

        private static bool BaseHasDevelopmentInfrastructure(WorldSnapshot snap, PlayerSetupData player, HexCoord hex) =>
            snap.Known.Buildings.Any(b => b.Owner == player && b.Hex.Equals(hex)
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
                        || intent.Suspended == SuspendReason.EconomyLoan))
                    continue;

                if (intent.LastReconciledTurn == turn)
                    continue;

                intent.LastReconciledTurn = turn;
                intent.TurnsActive++;
                if (intent.Suspended != SuspendReason.PoolExhausted)
                    intent.StallTurns++;

                if (ShouldReap(intent))
                {
                    state.Remove(intent.IntentKey);
                    StartPersistentCooldown(allocState, intent.LastAttemptKey, intent.Kind, turn, "IntentReapedIdle");
                    AiDebugLog.Write($"[AI][V2] continuity — {intent.IntentKey} reaped (idle: "
                        + $"stall {intent.StallTurns}/{AiConfigV2.commitmentStallTurns}, "
                        + $"age {intent.TurnsActive}/{AiConfigV2.commitmentMaxTurns})");
                }
            }
            state.ReconcileBaseExpansionWait(turn, outcomes);
        }

        // Same fallback MissionIntentState's own Base-expansion outcome matching uses: a
        // materialized outcome carries its EconomyTarget directly, but a fresh mission that failed
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
            bool raidReinforcementOutcome = o.MissionKind == MissionKind.Raid
                && (o.HasRaidPayload
                    ? o.RaidPhase == RaidMissionPhase.Reinforcement
                    : o.Proposal?.Target is RaidMissionTarget reinforcementTarget
                        && reinforcementTarget.Phase == RaidMissionPhase.Reinforcement);
            AiDebugLog.Write($"[AI][V2] [{aid}] outcome {o.Outcome}"
                + (o.ObjectiveSatisfied ? " satisfied" : "")
                + (o.StructuralFailure ? " structural" : "")
                + $" {o.IntentKey}");

            if (o.Outcome == ExecutionOutcome.Completed && o.ObjectiveSatisfied)
            {
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
                    if (o.HasRaidPayload && o.RaidOperationStarted)
                    {
                        CreateRaidIntent(state, o, turn);
                        AiDebugLog.Write($"[AI][V2][Raid] continuity — [{aid}] {o.IntentKey} first "
                            + "target completed during opening step; campaign created for return/refocus");
                        return;
                    }
                }

                RepayEconomyLoan(state, intent, o);
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
                {
                    state.Remove(o.IntentKey);
                    AiDebugLog.Write($"[AI][V2] continuity — [{aid}] {o.IntentKey} COMPLETED, retired");
                }
                return;
            }

            if (o.StructuralFailure)
            {
                // A failed support roster invalidates only that reinforcement assignment, not the
                // durable Raid campaign or its neutral target. Provisioning classifies a missing
                // primary/target separately; AssemblyInfeasible here is therefore support-local.
                if (raidReinforcementOutcome && intent?.Raid != null
                    && o.ProvisionFailureKindValue == ProvisionFailureKind.AssemblyInfeasible)
                {
                    intent.Raid.SupportArmyId = null;
                    intent.Raid.ReinforcementRequestedTurn = -1;
                    intent.Raid.Phase = RaidMissionPhase.Reinforcement;
                    intent.Status = IntentStatus.Active;
                    intent.Suspended = SuspendReason.None;
                    intent.LastReconciledTurn = turn;
                    AiDebugLog.Write($"[AI][V2][Raid] continuity — [{aid}] {o.IntentKey} "
                        + "support assembly invalid; support released, campaign kept in Reinforcement");
                    return;
                }

                if (returnBuilderOutcome && intent != null)
                {
                    intent.Status = IntentStatus.Active;
                    intent.Suspended = SuspendReason.None;
                    intent.LastReconciledTurn = turn;
                    return;
                }
                RepayEconomyLoan(state, intent, o);
                if (intent != null) state.Remove(o.IntentKey);
                string reason = o.ProvisionFailureKindValue?.ToString() ?? "StructuralFailure";
                StartPersistentCooldown(allocState, o.AttemptKey, o.MissionKind, turn, reason);
                AiDebugLog.Write($"[AI][V2] continuity — [{aid}] {o.IntentKey} structural failure ({reason}), retired + cooldown");
                return;
            }

            if (o.Outcome == ExecutionOutcome.Failed)
            {
                if (returnBuilderOutcome && intent != null)
                {
                    intent.Status = IntentStatus.Active;
                    intent.Suspended = SuspendReason.None;
                    intent.LastReconciledTurn = turn;
                    return;
                }
                RepayEconomyLoan(state, intent, o);
                if (intent != null)
                {
                    state.Remove(o.IntentKey);
                    AiDebugLog.Write($"[AI][V2] continuity — [{aid}] {o.IntentKey} failed ({Describe(o)}), retired");
                }
                return;
            }

            if (o.MissionKind == MissionKind.Economy && !o.MadeProgress)
            {
                if (returnBuilderOutcome && intent != null)
                {
                    intent.LastReconciledTurn = turn;
                    intent.StallTurns = 0;
                    return;
                }
                // Outbound Economy commitments (BuildExtraction/FoundBase) preserve their assigned
                // builder only on genuinely transient capability failures (NoMoverExists /
                // MoverContended) — AdvanceIntent suspends+preserves the intent for exactly this
                // case (SuspendReason.CapabilityUnavailable), and these two kinds do NOT age out
                // through StallTurns/ShouldReap on purpose (retiring here would let a fresh
                // materialization hand a second builder the same target next admission pass while
                // the first was still mid-route). Provisioning reports a missing committed actor as
                // TargetInvalidated, which is handled by the Failed branch above (Outcome.Failed,
                // not routed through transientCapability at all). A missing live safe route is
                // reported as NoExecutableStep and, for an outbound Economy outcome with no
                // progress, is not transientCapability either, so it reaches the cleanup below.
                // Neither NoMoverExists nor MoverContended is claimed to be always-transient in some
                // absolute sense — this fix only stops a proven route failure from being mistaken
                // for one; a mover that stays stuck for some other eligibility reason with a route
                // that does exist is unaffected. ReturnBuilder (the return-trip leg) has its own,
                // deliberately unconditional preservation rule above (returnBuilderOutcome) and is
                // not affected by any of this.
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

                bool transientCapability = intent != null && capabilityFailure;
                if (transientCapability)
                {
                    AdvanceIntent(intent, o, turn, state, allocState);
                    return;
                }
                RepayEconomyLoan(state, intent, o);
                if (intent != null) state.Remove(intent.IntentKey);
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
            else if (o.HasRaidPayload && o.RaidOperationStarted)
            {
                CreateRaidIntent(state, o, turn);
            }
            else if (o.HasEconomyPayload && o.MadeProgress)
            {
                CreateEconomyIntent(state, o, turn);
            }

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
            intent.CumulativeApSpent += o.ApSpent;
            intent.StepsMovedTotal += o.StepsMoved;
            if (o.MoverArmyId.HasValue)
            {
                ReleaseOtherReconActorClaims(state, intent, o.MoverArmyId.Value);
                // AGG-RAID §5 (critical) — for a Raid the executor of a given turn may be the
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
                if (supportExecutedThisTurn)
                {
                    if (o.RaidPhase == RaidMissionPhase.Reinforcement
                        && !o.RaidReinforcementHandoffAttempted && !raid.SupportArmyId.HasValue)
                        raid.SupportArmyId = o.MoverArmyId.Value;
                    AiDebugLog.WriteVerbose($"[AI][V2][Raid] {intent.IntentKey} step executed by support "
                        + $"#{o.MoverArmyId.Value}; primary #{raid.PrimaryArmyId} kept; "
                        + $"handoffAttempted={(o.RaidReinforcementHandoffAttempted ? 1 : 0)}");
                }
                // Economy actor ownership is durable. A replacement may only happen after
                // ResolveActive retires a structurally invalid intent; an ordinary retry cannot
                // atomically rewrite the mover behind continuity's back.
                else if (intent.Kind != MissionKind.Economy
                    || !intent.PreferredMoverArmyId.HasValue
                    || intent.PreferredMoverArmyId.Value == o.MoverArmyId.Value)
                    intent.PreferredMoverArmyId = o.MoverArmyId;
            }

            if (o.HasScoutPayload && intent.Scout != null)
            {
                intent.Scout.FocusHex = o.FocusHex;
                intent.Scout.Kind = o.ScoutKind;
                intent.Scout.RequiresStealth = o.ScoutRequiresStealth;
                if (o.TrackedArmyId.HasValue)
                    intent.Scout.TrackedArmyId = o.TrackedArmyId;
            }

            if (o.HasRaidPayload && intent.Raid != null)
            {
                intent.Raid.LastKnownHex = o.RaidLastKnownHex;
                if (o.RaidOperationStarted)
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
            else if (firstReconcileThisTurn && !poolExhausted && !capabilityUnavailable)
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
                    state.Remove(intent.IntentKey);
                    StartPersistentCooldown(allocState, intent.LastAttemptKey, intent.Kind, turn,
                        "BaseExpansionDeliverySuppressed");
                    AiDebugLog.Write($"[AI][V2] continuity — [{AiV2Trace.FormatCorrelation(o.Proposal)}] "
                        + $"{intent.IntentKey} Base delivery repeatedly failed "
                        + $"({o.ProvisionFailureKindValue}); suppressed for cooldown, retired");
                    return;
                }
            }

            if (!capabilityUnavailable && ShouldReap(intent))
            {
                state.Remove(intent.IntentKey);
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
            owner.Scout.FocusHex = o.FocusHex;
            owner.Scout.Kind = o.ScoutKind;
            owner.Scout.RequiresStealth = o.ScoutRequiresStealth;
            owner.Scout.TrackedArmyId = o.ScoutKind == ScoutTargetKind.Surveil ? o.TrackedArmyId : null;
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
            var si = new ScoutIntent
            {
                Kind = o.ScoutKind,
                RequiresStealth = o.ScoutRequiresStealth,
                FocusHex = o.FocusHex,
                TrackedArmyId = o.TrackedArmyId,
                BaselineObservedTurn = o.BaselineObservedTurn,
            };
            CommitmentTier funding = o.ScoutKind == ScoutTargetKind.Surveil ? CommitmentTier.Soft : CommitmentTier.None;
            MissionIntent intent = NewIntent(o, turn, MissionKind.Scout, funding, si);
            state.Put(intent);
            AiDebugLog.Write($"[AI][V2] continuity — [{AiV2Trace.FormatCorrelation(o.Proposal)}] {intent.IntentKey} created ({intent.Funding}, "
                + $"mover #{o.MoverArmyId}, {o.StepsMoved} step(s))");
        }

        private static void CreateRaidIntent(MissionIntentState state, MissionTurnOutcome o, int turn)
        {
            var ri = new RaidIntent
            {
                Target = o.RaidTarget,
                LastKnownHex = o.RaidLastKnownHex,
                TargetIsNeutral = o.RaidTargetIsNeutral,
                OperationStarted = true,
            };
            MissionIntent intent = NewIntent(o, turn, MissionKind.Raid, CommitmentTier.Hard, ri);
            state.Put(intent);
            AiDebugLog.Write($"[AI][V2] continuity — [{AiV2Trace.FormatCorrelation(o.Proposal)}] {intent.IntentKey} created (Hard raid, mover #{o.MoverArmyId})");
        }

        private static void CreateEconomyIntent(MissionIntentState state, MissionTurnOutcome o, int turn)
        {
            EconomyMissionTarget t = o.EconomyTarget;
            var ei = new EconomyIntent
            {
                Kind = t.Kind, TargetHex = t.TargetHex, ResourceType = t.ResourceType,
                BuilderArmyId = o.MoverArmyId,
                BuildCard = t.BuildCard, BuildResourceCost = t.BuildResourceCost,
                BuildApCost = t.BuildApCost, BuildValue = t.BuildValue,
                IntrinsicValue = o.Proposal?.BaseValue,
                MinimumFollowupAp = t.MinimumFollowupAp,
                ProjectedActivationApCost = t.ProjectedActivationApCost,
                ProjectedMaxMovement = t.ProjectedMaxMovement,
                Loaned = o.EconomyLoanSource.HasValue,
                LoanSource = o.EconomyLoanSource ?? default,
            };
            CommitmentTier funding = o.EconomyBuildCompleted ? CommitmentTier.Hard : CommitmentTier.Soft;
            MissionIntent intent = NewIntent(o, turn, MissionKind.Economy, funding, ei);
            state.Put(intent);
            AiDebugLog.Write($"[AI][V2][Economy] continuity create {intent.IntentKey} mover=#{o.MoverArmyId}");
        }

        private static void RepayEconomyLoan(MissionIntentState state, MissionIntent economy,
            MissionTurnOutcome outcome)
        {
            MissionIntentKey? source = outcome.EconomyLoanSource;
            if (!source.HasValue && economy?.Economy?.Loaned == true)
                source = economy.Economy.LoanSource;
            if (!source.HasValue || !state.TryGet(source.Value, out MissionIntent lender))
                return;
            if (lender.Status == IntentStatus.Suspended && lender.Suspended == SuspendReason.EconomyLoan)
            {
                lender.Status = IntentStatus.Active;
                lender.Suspended = SuspendReason.None;
                AiDebugLog.Write($"[AI][V2][Economy][Loan] repay actor=#{lender.PreferredMoverArmyId} to={lender.IntentKey}");
            }
        }

        private static bool ShouldReap(MissionIntent i)
        {
            if (i.Kind == MissionKind.Raid)
                return i.StallTurns >= AiConfigV2.raidIntentStallTurns
                    || i.TurnsActive >= AiConfigV2.raidIntentMaxTurns;
            // Explore/Refresh are durable roles whose waypoint is re-focused by ResolveActive.
            // Productive movement resets StallTurns; absolute age must not turn that success into
            // IntentReapedStall. Objective exhaustion/invalidity is handled separately above.
            if (i.Kind == MissionKind.Scout)
                return i.StallTurns >= AiConfigV2.commitmentStallTurns;
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
