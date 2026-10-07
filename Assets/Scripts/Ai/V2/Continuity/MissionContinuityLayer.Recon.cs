using System.Collections.Generic;
using System.Linq;

namespace Game.Ai.V2
{
    // Existing domain result semantics, owned by the same continuity policy.
    internal static partial class MissionContinuityLayer
    {
        internal static bool IsScoutStepObjectiveSatisfiedLive(Game.Players.PlayerSetupData player,
            ProvisionedMission pm) => ScoutObjectiveEvaluator.IsSatisfiedLive(player, pm.ScoutKind, pm.FocusHex);

        internal static void ClassifyScoutStep(ExecutionResult e, MissionStepResult o)
        {
            if (e.StopReason == ExecutionStopReason.TargetInvalidated)
            {
                o.Disposition = MissionStepDisposition.Waiting;
                return;
            }
            MissionStepResultPolicy.ClassifyDefaultExecution(e, o);
        }
        private static bool TryContinueScoutWaypoint(MissionIntentState state, AiAllocatorState allocState,
            MissionIntent intent, MissionStepResult o, int turn)
        {
            string aid = AiV2Trace.FormatCorrelation(o.Proposal);
            // Review P1 #1/#2 (+ follow-up) — an Explore/Refresh focus hex met by something
            // OTHER than this actor's own execution reaching goal (another scout opened it
            // mid-turn, or provisioning found it already live-satisfied) is a satisfied
            // WAYPOINT, not a finished role. KEEP — or, for a fresh mission that really
            // began executing this turn, CREATE — the durable ground-scout intent so
            // ActorCommitments retains the scout and ResolveActive re-focuses it next turn
            // (its hex now fails IsIntentStillValid). Own-execution completion uses
            // ExecutionResult.DurableRoleContinues.
            if (o.ObjectiveSatisfiedExternally)
            {
                bool existingScoutRole = intent != null
                    && intent.Scout != null;
                // Fresh role: the mission was provisioned AND executed at least one step
                // this turn (so ReconPatrolState already exists). A provisioning-only
                // TargetSatisfied for a never-executed fresh mission has HasScoutPayload ==
                // false / MadeProgress == false and is correctly NOT made durable.
                bool freshScoutRole = intent == null && o.ReconFacts().HasScoutPayload && o.MadeProgress;

                if (existingScoutRole)
                {
                    // Count the AP / steps the scout actually spent before the waypoint
                    // was taken (accumulated-state preservation), same as any other
                    // productive turn — AdvanceIntent owns that accounting.
                    o.MadeProgress = true;
                    AdvanceIntent(intent, o, turn, state, allocState);
                    AiDebugLog.Write($"[AI][V2] continuity — [{aid}] {o.IntentKey} waypoint satisfied "
                        + "externally; durable scout role kept for next-turn re-focus");
                    return true;
                }
                if (freshScoutRole)
                {
                    if (TryAbsorbIntoExistingActorRole(state, o, turn, allocState))
                        return true;
                    CreateIntent(state, o, turn);
                    AiDebugLog.Write($"[AI][V2] continuity — [{aid}] {o.IntentKey} fresh scout began "
                        + "this turn; waypoint satisfied externally, durable intent created for re-focus");
                    return true;
                }
            }
            return false;
        }

        private static bool TryCreateScoutStep(MissionIntentState state, AiAllocatorState allocState,
            MissionIntent intent, MissionStepResult o, int turn)
        {
            if (!(o.MadeProgress && o.ReconFacts().HasScoutPayload)) return false;
            if (!TryAbsorbIntoExistingActorRole(state, o, turn, allocState))
                CreateIntent(state, o, turn);
            return true;
        }


        // Spec §1/§10 — the physical scout that produced this fresh scout outcome already owns a
        // durable Recon role (Explore / Refresh) under a different key: a new
        // opportunistic mission ran on a mover continuity already tracks. Re-point that existing
        // role at the new objective and re-key its registry slot, preserving CreatedTurn /
        // TurnsActive / CumulativeApSpent / StepsMovedTotal / PreferredMoverArmyId, instead of
        // creating a second durable intent for the same physical actor. Ownership is actor-
        // exclusive across all three Recon sub-kinds. Returns true when it absorbed the outcome.
        private static bool TryAbsorbIntoExistingActorRole(MissionIntentState state,
            MissionStepResult o, int turn, AiAllocatorState allocState)
        {
            if (!o.ReconFacts().HasScoutPayload || o.MoverArmyId == null)
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
            owner.Funding = owner.Funding == CommitmentTier.Hard ? CommitmentTier.Hard : CommitmentTier.None;
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
        // mission, so the three can never drift apart again.
        private static void ApplyScoutPayload(ScoutIntent s, MissionStepResult o)
        {
            s.FocusHex = o.ReconFacts().FocusHex;
            s.Kind = o.ReconFacts().ScoutKind;
            s.RequiresStealth = o.ReconFacts().ScoutRequiresStealth;
        }

        private static void CreateIntent(MissionIntentState state, MissionStepResult o, int turn)
        {
            var si = new ScoutIntent();
            ApplyScoutPayload(si, o);
            CommitmentTier funding = CommitmentTier.None;
            MissionIntent intent = NewIntent(o, turn, MissionKind.Scout, funding, si);
            state.Put(intent);
            AiDebugLog.Write($"[AI][V2] continuity — [{AiV2Trace.FormatCorrelation(o.Proposal)}] {intent.IntentKey} created ({intent.Funding}, "
                + $"mover #{o.MoverArmyId}, {o.StepsMoved} step(s))");
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
        private static void ObserveReconMover(MissionIntentState state, AiAllocatorState allocator,
            MissionIntent intent, MissionStepResult o, int turn) =>
            ReleaseOtherReconActorClaims(state, intent, o.MoverArmyId.Value);

        private static void ApplyScoutStepFacts(MissionIntentState state, AiAllocatorState allocator,
            MissionIntent intent, MissionStepResult o, int turn)
        {
            if (o.ReconFacts().HasScoutPayload && intent.Scout != null)
                ApplyScoutPayload(intent.Scout, o);

        }

        private static void CollectScoutFoci(Game.Players.PlayerSetupData player, WorldSnapshot snap, ActiveResolution pass)
        {
            var state = pass.State;
            var scoutFoci = pass.ScoutFoci;
            // Spec §1 — foci currently owned by ground scout intents, so a re-focus never lands two
            // durable intents on the same waypoint. Mutated as intents are re-pointed below.
            foreach (MissionIntent i in state.All)
                if (i.Scout != null && !ReconScoutKinds.IsAirSweep(i.Scout.Kind))
                    scoutFoci.Add(i.Scout.FocusHex);
        }

        private static void ResolveScoutOperation(Game.Players.PlayerSetupData player, WorldSnapshot snap, MissionIntent intent, ActiveResolution pass)
        {
            var active = pass.Active;
            var dead = pass.Dead;
            var rekeys = pass.Rekeys;
            var scoutFoci = pass.ScoutFoci;
            bool underSiege = snap?.Threat?.UnderSiege == true;
            ScoutIntent s = intent.Scout;
            if (s == null) { dead.Add(intent.IntentKey); return; }

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
                return;

            // A ground Recon role whose bound actor no longer exists (killed / merged away) is
            // not a lane any more. Unbind it so AdvanceIntent ages it like any idle intent
            // instead of parking it forever under the CapabilityUnavailable exemption, and so
            // TrimSurplusReconLanes never counts it as a physical lane (2026-09-25 audit F4:
            // Vex Intent(Refresh 1,-2) outlived scout #8 from T8 and displaced live scout #7 at
            // T13). AirSweep is exempt: its wing legitimately leaves the army list while stored.
            // Recon audit B11 — an actor that still exists but can no longer serve the role at all
            // (no longer a solo Recce, a prison, empty) is the same case: the structural test is
            // MissionActorPolicy.HasCapableActor, whose answer also decides the actor claim.
            // Stealth is deliberately not part of it (a scout that cannot hide THIS turn keeps
            // its role; the claim itself applies the objective's stealth requirement).
            if (intent.PreferredMoverArmyId.HasValue && !ReconScoutKinds.IsAirSweep(s.Kind)
                && snap?.Self?.Armies != null
                && !MissionActorPolicy.HasCapableActor(intent, snap, StealthRequirement.None))
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
                    return;
                }
                dead.Add(oldKey);
                AiDebugLog.Write($"[AI][V2] continuity — {oldKey} retired at turn start (no runnable re-focus)");
                return;
            }

            ResumeTransientSuspension(intent);

            if (intent.Funding == CommitmentTier.Soft && underSiege)
            {
                intent.Status = IntentStatus.Suspended;
                intent.Suspended = SuspendReason.Siege;
                return;
            }
            if (intent.Status == IntentStatus.Suspended && intent.Suspended == SuspendReason.Siege && !underSiege)
            {
                intent.Status = IntentStatus.Active;
                intent.Suspended = SuspendReason.None;
            }

            if (intent.Status == IntentStatus.Active)
                active.Add(intent);
        }

        private static void FinalizeReconResolution(Game.Players.PlayerSetupData player, WorldSnapshot snap, ActiveResolution pass)
        {
            var state = pass.State;
            var active = pass.Active;
            var reconObjectives = pass.ReconObjectives;
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
        }

        private static void CaptureScoutProvisionFacts(ProvisionedMission pm, MissionStepResult o)
        {
            o.ReconFactsForWrite().HasScoutPayload = true;
            o.ReconFactsForWrite().ScoutKind = pm.ScoutKind;
            o.ReconFactsForWrite().ScoutRequiresStealth = pm.RequiresStealth;
            o.ReconFactsForWrite().FocusHex = pm.FocusHex;
        }

        private static void CaptureScoutExecutionFacts(ExecutionResult e, MissionStepResult o)
        {
            o.PayloadForWrite<ReconStepPayload>().DurableRoleContinues = e.DurableRoleContinues;
        }

    }
}
