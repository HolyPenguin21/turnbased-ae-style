using System;
using System.Collections.Generic;
using System.Linq;
using Game.HexGrid;
using Game.Players;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  MISSION CONTINUITY  (Strategy V2 build-order step 7)
    // ===========================================================================================
    //  Closes the multi-turn loop. The pipeline recomputes everything from a fresh snapshot every
    //  turn; without this layer a half-walked recon chain re-plans from scratch on a 0.05 Radar
    //  wobble. Step 7 separates TWO things V1 kept fused (and kept corrupting):
    //
    //    INTENT      — "I still want to finish THIS objective / keep tracking THIS army." Durable.
    //                  Outlives any single MissionProposal. Every recon mission that starts and
    //                  does not finish leaves one; next turn MissionLayer re-materialises it from
    //                  the CURRENT snapshot (one place still owns proposal creation) and retarget
    //                  hysteresis keeps the scout pointed the same way through Radar noise.
    //    COMMITMENT  — a FUNDING POLICY on an Intent: "do not drop this from the budget over a
    //                  small Radar move." Soft for a far Surveil that has actually started moving;
    //                  Hard (raid) lands in step 9. A Commitment reaches the allocator as
    //                  already-funded, drawn first, allowed to drive an axis slice negative — but
    //                  NEVER to conjure AP that does not exist (Σ commitments <= real pool).
    //
    //  FOUR INVARIANTS (hold these into step 9):
    //    1. Intent != Proposal        — the proposal is this turn's attempt; the intent persists.
    //    2. Intent != Commitment      — Explore keeps an intent with NO funding protection.
    //    3. Progress != AP spent      — an intent earns continuation by MOVING (a step, or a
    //                                   stealth entry), never by having cost something. Sunk AP is
    //                                   telemetry, never continuation value.
    //    4. Post-execution observation != strategic policy — ReconcileAfterTurn reads NO live world
    //                                   state. It takes FACTS (MissionTurnOutcome, built by the
    //                                   ledger from ScoutObjectiveEvaluator + ExecutionResult) and
    //                                   only runs a state transition.
    //
    //  PRE-EMPTION is deferred (project owner's call: "commitment honoured to completion; add
    //  de-funding later"). ContinuationValue / SwitchingCost are recorded but the allocator does
    //  not yet weigh them — it just funds commitments first. When pre-emption lands it must use
    //  ProtectedValue = ContinuationValue + SwitchingCost and NEVER + sunk cost.
    // ===========================================================================================

    // How hard the funding protection is. None -> hysteresis only (Explore, short Surveil). Soft ->
    // a far Surveil that has started walking: funded first, sticky, still bounded by the real pool.
    // Hard -> a raid the AI has paid to assemble (step 9).
    public enum CommitmentTier { None, Soft, Hard }

    public enum IntentStatus { Active, Suspended }

    // Why an Active intent is not being pursued THIS turn. Siege -> an existential threat outranks
    // it (absolute MilitaryThreat / UnderSiege, exactly what those out-of-simplex scalars exist
    // for); resume when the siege lifts. PoolExhausted -> more commitments than AP this turn; the
    // intent survives and gets another shot next turn.
    public enum SuspendReason { None, Siege, PoolExhausted }

    // Strategic identity — deliberately COARSER than StableMissionKey. It survives a change of
    // tactical attempt: a Surveil intent is keyed by the tracked army alone, so a fresher
    // last-known position (a new attempt hex) is the SAME intent, and a NoObservationVantage
    // cooldown on Surveil(#42) never lands on Surveil(#77). Explore is keyed by its focus hex
    // (the hex IS the objective). Everything else: kind only, until step 9 gives raids an
    // ObjectiveId (target building / army id).
    public readonly struct MissionIntentKey : IEquatable<MissionIntentKey>
    {
        public readonly MissionKind Kind;
        public readonly int SubKind;      // (int)ScoutTargetKind for Scout
        public readonly int ObjectiveId;  // Surveil: tracked ArmyId. Explore: 0. (raid target id -> step 9)
        public readonly int Q, R;         // Explore focus hex; 0,0 otherwise

        public MissionIntentKey(MissionKind kind, int subKind, int objectiveId, int q, int r)
        {
            Kind = kind; SubKind = subKind; ObjectiveId = objectiveId; Q = q; R = r;
        }

        public static MissionIntentKey For(MissionProposal m)
        {
            if (m != null && m.Kind == MissionKind.Scout && m.Target is ScoutMissionTarget t)
                return t.Kind == ScoutTargetKind.Surveil
                    ? new MissionIntentKey(MissionKind.Scout, (int)ScoutTargetKind.Surveil,
                        t.Contact?.Army?.ArmyId ?? 0, 0, 0)
                    : new MissionIntentKey(MissionKind.Scout, (int)ScoutTargetKind.Explore, 0,
                        t.FocusHex.Q, t.FocusHex.R);
            return new MissionIntentKey(m?.Kind ?? MissionKind.Scout, 0, 0, 0, 0);
        }

        public static MissionIntentKey For(MissionIntent intent)
        {
            ScoutIntent s = intent?.Scout;
            if (s == null)
                return new MissionIntentKey(intent?.Kind ?? MissionKind.Scout, 0, 0, 0, 0);
            return s.Kind == ScoutTargetKind.Surveil
                ? new MissionIntentKey(MissionKind.Scout, (int)ScoutTargetKind.Surveil, s.TrackedArmyId ?? 0, 0, 0)
                : new MissionIntentKey(MissionKind.Scout, (int)ScoutTargetKind.Explore, 0, s.FocusHex.Q, s.FocusHex.R);
        }

        public bool Equals(MissionIntentKey o) =>
            Kind == o.Kind && SubKind == o.SubKind && ObjectiveId == o.ObjectiveId && Q == o.Q && R == o.R;
        public override bool Equals(object obj) => obj is MissionIntentKey o && Equals(o);
        public override int GetHashCode() => ((int)Kind, SubKind, ObjectiveId, Q, R).GetHashCode();
        public override string ToString() =>
            Kind == MissionKind.Scout
                ? (SubKind == (int)ScoutTargetKind.Surveil
                    ? $"Intent(Surveil #{ObjectiveId})"
                    : $"Intent(Explore {Q},{R})")
                : $"Intent({Kind})";
    }

    // The durable objective payload for a Scout intent. NO ExecutionHex — the vantage a Surveil
    // observes from is a per-turn tactical solution recomputed in provisioning, never persisted.
    public sealed class ScoutIntent
    {
        public ScoutTargetKind Kind;
        public HexCoord FocusHex;          // Explore: the frontier hex. Surveil: last-known enemy hex (refreshed each turn).
        public int? TrackedArmyId;         // Surveil only
        public int BaselineObservedTurn;   // Surveil only — a sighting past this turn == objective met. Fixed per intent instance.
    }

    public sealed class MissionIntent
    {
        public MissionIntentKey IntentKey;
        public StableMissionKey LastAttemptKey;   // last turn's concrete proposal key — correlates the ledger / cooldown
        public MissionKind Kind;

        public CommitmentTier Funding;            // None for Explore / short Surveil; Soft for a far Surveil; Hard -> step 9
        public IntentStatus Status;
        public SuspendReason Suspended;

        public object Objective;                  // boxed ScoutIntent

        public int CreatedTurn;
        public int TurnsActive;
        public int LastProgressTurn;
        public int StallTurns;

        // TELEMETRY ONLY. Never folded into ContinuationValue / any funding decision — that is the
        // sunk-cost fallacy the Intent/Commitment split exists to keep out of the architecture.
        public float CumulativeApSpent;
        public int StepsMovedTotal;

        // The mover that carried this intent last turn. Provisioning PREFERS it (a solver tie-break)
        // so a multi-turn operation is not silently restarted by a different unit each turn — but
        // it is not RESERVED: a dead / blocked preferred mover is freely replaced.
        public int? PreferredMoverArmyId;

        public ScoutIntent Scout => Objective as ScoutIntent;
    }

    // Per-player durable intent store. Same lifetime/registry shape as AiRadarStateRegistry /
    // AiAllocatorStateRegistry — created lazily, cleared in CitadelSetupController on new game.
    public sealed class MissionIntentState
    {
        private readonly Dictionary<MissionIntentKey, MissionIntent> _intents =
            new Dictionary<MissionIntentKey, MissionIntent>();

        public IReadOnlyCollection<MissionIntent> All => _intents.Values;
        public int Count => _intents.Count;

        public bool TryGet(MissionIntentKey k, out MissionIntent i) => _intents.TryGetValue(k, out i);
        public void Put(MissionIntent i) => _intents[i.IntentKey] = i;
        public void Remove(MissionIntentKey k) => _intents.Remove(k);
    }

    public static class MissionIntentRegistry
    {
        private static readonly Dictionary<PlayerSetupData, MissionIntentState> ByPlayer =
            new Dictionary<PlayerSetupData, MissionIntentState>();

        public static MissionIntentState GetOrCreate(PlayerSetupData player)
        {
            if (player == null)
                return new MissionIntentState();
            if (!ByPlayer.TryGetValue(player, out MissionIntentState s))
                ByPlayer[player] = s = new MissionIntentState();
            return s;
        }

        public static void Clear() => ByPlayer.Clear();
    }

    // ===========================================================================================
    //  OUTCOME LEDGER — the single, ordered record of what happened to each mission this turn.
    // ===========================================================================================
    //  The pipeline runs a bounded pack -> provision -> re-pack loop, so ONE mission key can see an
    //  intermediate RepriceThisTurn failure on pass 1 and a success on pass 2, then execute. A
    //  naive union of {failures, provisioned, executed} would let ReconcileAfterTurn read the stale
    //  failure as the outcome. The ledger records events in order; RecordProvisionSuccess clears a
    //  pending failure so Finalize() classifies the row by its FINAL state only.
    //  Finalize() emits pure FACTS — ReconcileAfterTurn never touches the world.
    public enum ExecutionOutcome { Completed, ProductiveStop, Blocked, Failed }

    public sealed class MissionTurnOutcome
    {
        public StableMissionKey AttemptKey;
        public MissionIntentKey IntentKey;
        public MissionProposal Proposal;
        public bool WasCommitment;

        public ExecutionOutcome Outcome;
        public bool ObjectiveSatisfied;
        public bool StructuralFailure;   // -> retire the intent AND cooldown the key
        public bool MadeProgress;        // StepsMoved > 0 || EnteredStealth — the ONLY "earned continuation" test

        public int StepsMoved;
        public float ApSpent;
        public int? MoverArmyId;

        // Surveil identity refresh for the next turn (from the provisioned plan, if any).
        public ScoutTargetKind ScoutKind;
        public HexCoord FocusHex;
        public int? TrackedArmyId;
        public int BaselineObservedTurn;
        public bool HasScoutPayload;
    }

    public sealed class MissionOutcomeLedger
    {
        private sealed class Row
        {
            public MissionProposal Proposal;
            public bool WasCommitment;
            public ProvisionedMission Provisioned;
            public ProvisionFailure? PendingFailure;   // superseded by a later success
            public ExecutionResult Execution;
        }

        private readonly Dictionary<StableMissionKey, Row> _rows = new Dictionary<StableMissionKey, Row>();

        private Row RowFor(MissionProposal m)
        {
            StableMissionKey k = StableMissionKey.For(m);
            if (!_rows.TryGetValue(k, out Row r))
                _rows[k] = r = new Row();
            if (r.Proposal == null) r.Proposal = m;
            return r;
        }

        public void RegisterProposals(IEnumerable<MissionProposal> missions)
        {
            if (missions == null) return;
            foreach (MissionProposal m in missions)
                if (m != null) RowFor(m);
        }

        public void RegisterCommitments(IEnumerable<Commitment> commitments)
        {
            if (commitments == null) return;
            foreach (Commitment c in commitments)
                if (c?.Mission != null) RowFor(c.Mission).WasCommitment = true;
        }

        public void RecordProvisionSuccess(MissionProposal m, ProvisionedMission pm)
        {
            Row r = RowFor(m);
            r.Provisioned = pm;
            r.PendingFailure = null;   // an earlier reprice/contend failure is no longer the last word
        }

        public void RecordProvisionFailure(MissionProposal m, ProvisionFailure f)
        {
            Row r = RowFor(m);
            if (r.Provisioned == null)   // a success already locked this mission — keep it
                r.PendingFailure = f;
        }

        public void RecordExecution(ExecutionResult result)
        {
            if (result == null) return;
            if (!_rows.TryGetValue(result.Key, out Row r))
            {
                // No proposal registered for this key — cannot derive an IntentKey. Should not
                // happen (the pipeline registers every funded mission first); drop rather than guess.
                AiDebugLog.Write($"[AI][V2] ledger — WARN execution for unregistered mission {result.Key}, ignored");
                return;
            }
            r.Execution = result;
        }

        public List<MissionTurnOutcome> Finalize()
        {
            var list = new List<MissionTurnOutcome>();
            foreach (KeyValuePair<StableMissionKey, Row> kv in _rows)
            {
                Row r = kv.Value;
                if (r.Proposal == null)
                    continue;

                var o = new MissionTurnOutcome
                {
                    AttemptKey = kv.Key,
                    IntentKey = MissionIntentKey.For(r.Proposal),
                    Proposal = r.Proposal,
                    WasCommitment = r.WasCommitment,
                };

                if (r.Provisioned != null)
                {
                    o.MoverArmyId = r.Provisioned.MoverArmyId;
                    o.HasScoutPayload = true;
                    o.ScoutKind = r.Provisioned.ScoutKind;
                    o.FocusHex = r.Provisioned.FocusHex;
                    o.TrackedArmyId = r.Provisioned.TrackedArmyId;
                    o.BaselineObservedTurn = r.Provisioned.BaselineObservedTurn;
                }

                if (r.Execution != null)
                {
                    ExecutionResult e = r.Execution;
                    o.StepsMoved = e.StepsMoved;
                    o.ApSpent = e.ApSpent;
                    o.MadeProgress = e.StepsMoved > 0 || e.EnteredStealth;
                    Classify(e, o);
                }
                else if (r.PendingFailure.HasValue)
                {
                    ClassifyProvisionFailure(r.PendingFailure.Value, o);
                }
                else
                {
                    // Funded but never got a real attempt (deferred on a re-pack, or a commitment
                    // the pool could not cover). The intent survives; its stall counter ticks.
                    o.Outcome = ExecutionOutcome.Blocked;
                }

                list.Add(o);
            }
            return list;
        }

        private static void Classify(ExecutionResult e, MissionTurnOutcome o)
        {
            if (e.ReachedGoal)
            {
                o.Outcome = ExecutionOutcome.Completed;
                o.ObjectiveSatisfied = true;
                return;
            }
            switch (e.StopReason)
            {
                case ExecutionStopReason.OutOfMovement:
                case ExecutionStopReason.EnemyDiscovered:
                case ExecutionStopReason.NeutralDiscovered:
                    o.Outcome = ExecutionOutcome.ProductiveStop;
                    break;
                case ExecutionStopReason.NoSafeStep:
                case ExecutionStopReason.MoveRejected:
                    o.Outcome = ExecutionOutcome.Blocked;
                    break;
                default: // MoverLost / BattleStarted / HexEventStarted / TargetInvalidated / ObservationUnavailable / RequiredStealthUnavailable
                    o.Outcome = ExecutionOutcome.Failed;
                    break;
            }
        }

        private static void ClassifyProvisionFailure(ProvisionFailure f, MissionTurnOutcome o)
        {
            switch (f.Kind)
            {
                case ProvisionFailureKind.NoMoverExists:
                case ProvisionFailureKind.NoObservationVantage:
                case ProvisionFailureKind.AssemblyInfeasible:
                    o.Outcome = ExecutionOutcome.Failed;
                    o.StructuralFailure = true;
                    break;
                case ProvisionFailureKind.TargetSatisfied:
                    o.Outcome = ExecutionOutcome.Completed;
                    o.ObjectiveSatisfied = true;
                    break;
                case ProvisionFailureKind.TargetInvalidated:
                    o.Outcome = ExecutionOutcome.Failed;   // world changed under the mission — hand back to fresh planning
                    break;
                default: // MoverContended / EnvelopeTooSmall / NoExecutableStep — retry next turn
                    o.Outcome = ExecutionOutcome.Blocked;
                    break;
            }
        }
    }

    // ===========================================================================================
    //  THE LAYER — three calls, bookending the turn.
    // ===========================================================================================
    internal static class MissionContinuityLayer
    {
        // START OF TURN. Refresh each durable intent against the fresh snapshot: purge the
        // structurally dead, suspend funding under siege, clear a spent PoolExhausted suspension.
        // Returns the intents MissionLayer must re-materialise (Active only).
        public static List<MissionIntent> ResolveActive(PlayerSetupData player, WorldSnapshot snap)
        {
            var active = new List<MissionIntent>();
            MissionIntentState state = MissionIntentRegistry.GetOrCreate(player);
            if (state.Count == 0)
                return active;

            bool underSiege = snap?.Threat?.UnderSiege == true;
            var dead = new List<MissionIntentKey>();

            foreach (MissionIntent intent in state.All)
            {
                ScoutIntent s = intent.Scout;
                if (s == null) { dead.Add(intent.IntentKey); continue; }

                if (!ScoutObjectiveEvaluator.IsIntentStillValid(snap, s))
                {
                    dead.Add(intent.IntentKey);
                    AiDebugLog.Write($"[AI][V2] continuity — {intent.IntentKey} retired at turn start (objective no longer valid)");
                    continue;
                }

                // A spent pool-exhaustion suspension gets another chance every turn.
                if (intent.Status == IntentStatus.Suspended && intent.Suspended == SuspendReason.PoolExhausted)
                {
                    intent.Status = IntentStatus.Active;
                    intent.Suspended = SuspendReason.None;
                }

                // Siege outranks any Soft/Hard funding: keep the intent, drop the funding, wait it out.
                if (intent.Funding != CommitmentTier.None && underSiege)
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

            if (state.Count > 0)
                AiDebugLog.Write($"[AI][V2] continuity — {state.Count} intent(s): "
                    + string.Join(" ", state.All.Select(i =>
                        $"{i.IntentKey}[{i.Funding}/{i.Status}{(i.Suspended != SuspendReason.None ? ":" + i.Suspended : "")} "
                        + $"t{i.TurnsActive} stall{i.StallTurns}{(i.PreferredMoverArmyId.HasValue ? " mv#" + i.PreferredMoverArmyId : "")}]")));
            return active;
        }

        // AFTER MISSION LAYER. Bind a funding policy to each Soft/Hard intent by matching it to its
        // freshly re-materialised proposal. The matched proposal is still in `proposals` — the
        // allocator excludes commitment keys from the fresh-mission pass so it is not double-funded.
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
                    // MissionLayer is contracted to materialise every Funding != None intent. If it
                    // could not, there is nothing to protect this turn — the intent still lives and
                    // ReconcileAfterTurn will tick its stall counter.
                    AiDebugLog.Write($"[AI][V2] continuity — WARN {intent.IntentKey} ({intent.Funding}) "
                        + "not materialised this turn; no funding bound");
                    continue;
                }
                commitments.Add(new Commitment
                {
                    IntentKey = intent.IntentKey,
                    Mission = p,
                    Tier = intent.Funding,
                    ContinuationValue = p.BaseValue,   // forward-looking merit of FINISHING — never sunk cost
                    SwitchingCost = 0f,                 // real disband/reposition loss -> pre-emption step
                });
            }
            return commitments;
        }

        // END OF TURN. Pure state transition over FACTS. No world reads.
        public static void ReconcileAfterTurn(PlayerSetupData player, int turn,
            IReadOnlyList<MissionTurnOutcome> outcomes)
        {
            MissionIntentState state = MissionIntentRegistry.GetOrCreate(player);
            AiAllocatorState allocState = AiAllocatorStateRegistry.GetOrCreate(player);

            var seen = new HashSet<MissionIntentKey>();

            foreach (MissionTurnOutcome o in outcomes ?? new List<MissionTurnOutcome>())
            {
                seen.Add(o.IntentKey);
                state.TryGet(o.IntentKey, out MissionIntent intent);

                if (o.Outcome == ExecutionOutcome.Completed && o.ObjectiveSatisfied)
                {
                    if (intent != null)
                    {
                        state.Remove(o.IntentKey);
                        AiDebugLog.Write($"[AI][V2] continuity — {o.IntentKey} COMPLETED, retired");
                    }
                    continue;
                }

                if (o.StructuralFailure)
                {
                    if (intent != null) state.Remove(o.IntentKey);
                    allocState.StartCooldown(o.AttemptKey, turn + AiConfigV2.allocatorRejectCooldownTurns);
                    AiDebugLog.Write($"[AI][V2] continuity — {o.IntentKey} structural failure, retired + cooldown");
                    continue;
                }

                if (o.Outcome == ExecutionOutcome.Failed)
                {
                    if (intent != null)
                    {
                        state.Remove(o.IntentKey);
                        AiDebugLog.Write($"[AI][V2] continuity — {o.IntentKey} failed ({Describe(o)}), retired");
                    }
                    continue;
                }

                // ProductiveStop / Blocked — an operation that should carry into next turn.
                if (intent != null)
                {
                    AdvanceIntent(intent, o, turn, state, allocState);
                }
                else if (o.MadeProgress && o.HasScoutPayload)
                {
                    CreateIntent(state, o, turn);
                }
            }

            // Registry intents with no outcome this turn: not materialised / not funded (a loose
            // Explore incumbent that lost its slot, or a commitment the pool could not cover).
            foreach (MissionIntent intent in state.All.ToList())
            {
                if (seen.Contains(intent.IntentKey))
                    continue;
                if (intent.Status == IntentStatus.Suspended && intent.Suspended == SuspendReason.Siege)
                    continue; // wait out the siege, no ageing

                intent.TurnsActive++;
                if (intent.Suspended != SuspendReason.PoolExhausted)
                    intent.StallTurns++;

                if (ShouldReap(intent))
                {
                    state.Remove(intent.IntentKey);
                    allocState.StartCooldown(intent.LastAttemptKey, turn + AiConfigV2.allocatorRejectCooldownTurns);
                    AiDebugLog.Write($"[AI][V2] continuity — {intent.IntentKey} reaped (idle: "
                        + $"stall {intent.StallTurns}/{AiConfigV2.commitmentStallTurns}, "
                        + $"age {intent.TurnsActive}/{AiConfigV2.commitmentMaxTurns})");
                }
            }
        }

        private static void AdvanceIntent(MissionIntent intent, MissionTurnOutcome o, int turn,
            MissionIntentState state, AiAllocatorState allocState)
        {
            intent.TurnsActive++;
            intent.LastAttemptKey = o.AttemptKey;
            intent.CumulativeApSpent += o.ApSpent;   // telemetry
            intent.StepsMovedTotal += o.StepsMoved;  // telemetry
            if (o.MoverArmyId.HasValue)
                intent.PreferredMoverArmyId = o.MoverArmyId;

            if (o.HasScoutPayload && intent.Scout != null)
            {
                // Refresh the tactical facts that legitimately move turn to turn (a Surveil's
                // last-known focus hex). Identity (the IntentKey) does not change.
                intent.Scout.FocusHex = o.FocusHex;
                if (o.TrackedArmyId.HasValue)
                    intent.Scout.TrackedArmyId = o.TrackedArmyId;
            }

            if (o.MadeProgress)
            {
                intent.LastProgressTurn = turn;
                intent.StallTurns = 0;
            }
            else
            {
                intent.StallTurns++;
            }

            if (o.Outcome == ExecutionOutcome.Blocked && o.WasCommitment && !o.MadeProgress)
            {
                // A commitment the allocator could not cover this turn (pool exhausted) — keep the
                // intent, mark it so ResolveActive gives it a fresh shot, don't tick stall as hard.
                intent.Status = IntentStatus.Suspended;
                intent.Suspended = SuspendReason.PoolExhausted;
            }

            if (ShouldReap(intent))
            {
                state.Remove(intent.IntentKey);
                allocState.StartCooldown(intent.LastAttemptKey, turn + AiConfigV2.allocatorRejectCooldownTurns);
                AiDebugLog.Write($"[AI][V2] continuity — {intent.IntentKey} reaped (stall "
                    + $"{intent.StallTurns}/{AiConfigV2.commitmentStallTurns}, age "
                    + $"{intent.TurnsActive}/{AiConfigV2.commitmentMaxTurns})");
            }
            else
            {
                AiDebugLog.Write($"[AI][V2] continuity — {intent.IntentKey} advanced "
                    + $"({o.Outcome}, progress {(o.MadeProgress ? 1 : 0)}, t{intent.TurnsActive} stall{intent.StallTurns})");
            }
        }

        private static void CreateIntent(MissionIntentState state, MissionTurnOutcome o, int turn)
        {
            // Earned by movement: a Scout that actually started an operation and did not finish it.
            // Explore keeps an intent with NO funding (hysteresis only). A Surveil that has begun
            // walking is multi-turn by definition (a one-turn Surveil completes and never reaches
            // here) — it earns a Soft commitment.
            var si = new ScoutIntent
            {
                Kind = o.ScoutKind,
                FocusHex = o.FocusHex,
                TrackedArmyId = o.TrackedArmyId,
                BaselineObservedTurn = o.BaselineObservedTurn,
            };
            var intent = new MissionIntent
            {
                IntentKey = o.IntentKey,
                LastAttemptKey = o.AttemptKey,
                Kind = MissionKind.Scout,
                Funding = o.ScoutKind == ScoutTargetKind.Surveil ? CommitmentTier.Soft : CommitmentTier.None,
                Status = IntentStatus.Active,
                Suspended = SuspendReason.None,
                Objective = si,
                CreatedTurn = turn,
                TurnsActive = 1,
                LastProgressTurn = turn,
                StallTurns = 0,
                CumulativeApSpent = o.ApSpent,
                StepsMovedTotal = o.StepsMoved,
                PreferredMoverArmyId = o.MoverArmyId,
            };
            state.Put(intent);
            AiDebugLog.Write($"[AI][V2] continuity — {intent.IntentKey} created ({intent.Funding}, "
                + $"mover #{o.MoverArmyId}, {o.StepsMoved} step(s))");
        }

        private static bool ShouldReap(MissionIntent i) =>
            i.StallTurns >= AiConfigV2.commitmentStallTurns
            || i.TurnsActive >= AiConfigV2.commitmentMaxTurns;

        private static string Describe(MissionTurnOutcome o) =>
            o.Proposal != null && o.Proposal.Target is ScoutMissionTarget t ? $"{t.Kind}" : "?";
    }
}
