using System;
using System.Collections.Generic;
using System.Linq;
using Game.Map;
using Game.Players;

namespace Game.Ai.V2
{
    // One player/turn lifecycle boundary. Domain rules and persistent stores stay outside.
    // Existing ledgers remain storage adapters during this migration; there is no mirrored state.
    internal sealed class AiTurnSession : IDisposable
    {
        private static readonly Dictionary<PlayerSetupData, AiTurnSession> Active =
            new Dictionary<PlayerSetupData, AiTurnSession>();

        internal PlayerSetupData Player { get; }
        internal PlayerRoot Root { get; }
        internal AiHandData Hand { get; }
        internal AiTurnContext Context { get; }
        internal int TurnNumber { get; }
        internal int DecisionRevision => WorldDeltaLifecycle.Current;
        internal MissionIntentState PersistentState { get; }
        internal MissionLeaseBook Leases { get; }
        private readonly ReconTurnState _recon;
        private readonly List<MissionLeaseBook> _passClaims = new List<MissionLeaseBook>();
        private bool _ended;
        private bool _audited;
        // The most recent lifecycle line, kept so tests can assert on it without a log sink.
        internal string LastLifecycleLine { get; private set; }
        private readonly int _revisionAtBegin;
        private readonly int _commitsAtBegin;
        private readonly int _stagedAtBegin;
        private readonly List<int> _progressReceipts = new List<int>();
        private List<MissionIntent> _lastActorIntents = new List<MissionIntent>();

        internal ReconTurnState Recon
        {
            get { EnsureActive(); return _recon; }
        }

        private AiTurnSession(PlayerSetupData player, PlayerRoot root, AiHandData hand,
            AiTurnContext context, int turn)
        {
            Player = player;
            Root = root;
            Hand = hand;
            Context = context;
            TurnNumber = turn;
            Leases = new MissionLeaseBook(player, turn);
            _revisionAtBegin = WorldDeltaLifecycle.Current;
            _commitsAtBegin = WorldDeltaLifecycle.CommitEvents;
            _stagedAtBegin = WorldDeltaLifecycle.StagedChildMutations;
            PersistentState = MissionIntentRegistry.GetOrCreate(player);
            _recon = ReconTurnStateStore.Begin(player, turn);
            V2TurnActivityTelemetry.Begin(player, turn);
            CapabilityPoolExhaustionRegistry.BeginTurn(player, turn);
            MissionLeaseBook.BeginTurn(player, turn);
            StrategicInterruptRegistry.CaptureTurnContext(player, turn, hand);
        }

        internal static AiTurnSession Begin(PlayerSetupData player, PlayerRoot root,
            AiHandData hand, AiTurnContext context) =>
            Begin(player, root, hand, context, context?.TurnNumber ?? -1);

        // Explicit turn overload permits lifecycle tests without constructing a Unity map.
        internal static AiTurnSession Begin(PlayerSetupData player, PlayerRoot root,
            AiHandData hand, AiTurnContext context, int turn)
        {
            if (player == null) throw new ArgumentNullException(nameof(player));
            if (Active.TryGetValue(player, out AiTurnSession previous))
            {
                if (previous.TurnNumber == turn)
                    throw new InvalidOperationException("An AI turn session is already active for this player.");
                // Native coroutine cancellation is not guaranteed to Dispose nested enumerators.
                // The next turn closes an abandoned prior scope before publishing a new one.
                previous.Dispose();
            }
            var session = new AiTurnSession(player, root, hand, context, turn);
            Active.Add(player, session);
            return session;
        }

        internal static AiTurnSession Peek(PlayerSetupData player, int turn) =>
            player != null && Active.TryGetValue(player, out AiTurnSession session)
                && session.TurnNumber == turn && !session._ended ? session : null;

        internal ActorCommitments RefreshActors(IEnumerable<MissionIntent> intents,
            WorldSnapshot snapshot, IReadOnlyList<ReconObjective> objectives)
        {
            EnsureActive();
            RequireFrame(snapshot);
            _lastActorIntents = intents == null ? new List<MissionIntent>() : new List<MissionIntent>(intents);
            return MissionActorPolicy.Build(intents, snapshot, objectives, Leases);
        }

        internal static AiTurnSession PeekActive(PlayerSetupData player) =>
            player != null && Active.TryGetValue(player, out var session) && !session._ended ? session : null;

        internal ISet<int> CreateProvisioningClaims()
        {
            EnsureActive();
            var pass = new MissionLeaseBook(Player, TurnNumber);
            _passClaims.Add(pass);
            return pass.PassActorSet();
        }

        internal StrategicInvalidation PendingInvalidations
        { get { EnsureActive(); return StrategicInterruptRegistry.Peek(Player, TurnNumber); } }
        internal void ConsumeInvalidations(StrategicInvalidationReason reasons)
        { EnsureActive(); StrategicInterruptRegistry.Consume(Player, TurnNumber, reasons); }

        // Preserve the original normal-turn expiry/diagnostic ordering before the summary.
        // Dispose also runs this boundary on coroutine disposal or an exception.
        internal void CompleteReservations()
        {
            EnsureActive();
            MissionLeaseBook.ExpireStage(Player, TurnNumber,
                StrategicReservationExpiry.EndOfTurn);
            MissionLeaseBook.AssertClearAtTurnEnd(Player, TurnNumber);
        }

        internal void Settle(MissionStepResult result, WorldSnapshot snapshot = null,
            IReadOnlyList<ReconObjective> objectives = null)
        {
            EnsureActive();
            RequireFrame(snapshot);
            if (result == null) { MissionContinuityLayer.ReconcileStep(Player, TurnNumber, null); return; }
            IntentTrace before = TraceIntent(result.IntentKey);
            MissionContinuityLayer.ReconcileStep(Player, TurnNumber, result);
            if (result.MadeProgress && result.StateVersionAfter >= 0)
                _progressReceipts.Add(result.StateVersionAfter);
            if (snapshot != null)
            {
                // A domain may release only a support leg while retaining its durable operation.
                // Re-project that operation through the authoritative role policy, preserving every
                // other operation and anonymous same-pass claim. No second eligibility rule lives here.
                var projection = new MissionLeaseBook();
                PersistentState.TryGet(result.IntentKey, out var intent);
                MissionActorPolicy.Build(intent == null ? Array.Empty<MissionIntent>() : new[] { intent },
                    snapshot, objectives, projection);
                Leases.ReplaceOperationActors(result.IntentKey, projection);
                projection.Close();
            }
            LogTransition(result, before);
        }

        // The durable intent as the lifecycle line sees it: identity kept so a rekey is visible.
        private readonly struct IntentTrace
        {
            internal readonly MissionIntent Intent;
            internal readonly string State;
            internal readonly int Stall;
            internal readonly int ActorCount;
            internal readonly int ResourceRows;
            internal IntentTrace(MissionIntent intent, string state, int stall, int actors, int resources)
            { Intent = intent; State = state; Stall = stall; ActorCount = actors; ResourceRows = resources; }
        }

        private IntentTrace TraceIntent(MissionIntentKey key)
        {
            PersistentState.TryGet(key, out MissionIntent intent);
            return new IntentTrace(intent,
                intent == null ? "none" : intent.Status + (intent.Status == IntentStatus.Suspended
                    ? "/" + intent.Suspended : ""),
                intent?.StallTurns ?? 0, Leases.ActorsFor(key).Count, Leases.ResourcesFor(key).Count);
        }

        // The single lifecycle-transition line, one per settled step, showing the whole chain:
        //   exec   = what Execution/Provisioning/Allocation reported,
        //   norm   = how the result boundary (ledger) normalized it,
        //   domain = what Continuity decided for the durable intent,
        //   claims = the lease effect. Domain continuity keeps its own detail lines.
        private void LogTransition(MissionStepResult result, IntentTrace before)
        {
            MissionIntentKey key = result.IntentKey;
            var dirty = StrategicInvalidationReason.None;
            foreach (WorldDelta delta in result.WorldDeltas) dirty |= delta.DirtyFacts;
            string source = result.StopReason.HasValue ? "execution"
                : result.ProvisionFailureKindValue.HasValue ? "provisioning"
                : result.AllocationDeferReason.HasValue ? "deferral" : "none";
            // The step may have created the durable intent, kept it, rekeyed it or retired it.
            bool alive = before.Intent != null
                ? PersistentState.All.Contains(before.Intent)
                : PersistentState.TryGet(key, out _);
            IntentTrace after = alive
                ? TraceIntent(before.Intent != null ? before.Intent.IntentKey : key) : default;
            string fate = before.Intent == null ? (alive ? "created" : "none")
                : !alive ? "retired"
                : before.Intent.IntentKey.Equals(key) ? "kept" : "rekeyed:" + before.Intent.IntentKey;
            string newState = alive ? after.State : (before.Intent == null ? "none" : "retired");
            string payloads = (result.GetPayload<ReconStepPayload>() != null ? "recon," : "")
                + (result.GetPayload<RaidStepPayload>() != null ? "raid," : "")
                + (result.GetPayload<AttackStepPayload>() != null ? "attack," : "")
                + (result.GetPayload<ActiveDefenceStepPayload>() != null ? "defence," : "")
                + (result.GetPayload<EconomyStepPayload>() != null ? "economy," : "")
                + (result.GetPayload<DevelopmentStepPayload>() != null ? "development," : "")
                + (result.GetPayload<GroundCombatStepPayload>() != null ? "ground," : "");
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            string line = ($"[AI][V2][Lifecycle] operation={key} kind={key.Kind}"
                + $" | exec stop={result.StopReason?.ToString() ?? "-"}"
                + $" prov={result.ProvisionFailureKindValue?.ToString() ?? "-"}"
                + $" defer={result.AllocationDeferReason?.ToString() ?? "-"}"
                + $" progress={(result.MadeProgress ? 1 : 0)} moved={result.StepsMoved}"
                + $" ap={result.ApSpent.ToString("0.##", inv)} rev={result.StateVersionAfter}"
                + $" sat={(result.ObjectiveSatisfied ? 1 : 0)}/{(result.ObjectiveSatisfiedExternally ? 1 : 0)}"
                + $" mover={(result.MoverArmyId.HasValue ? result.MoverArmyId.Value.ToString() : "-")}"
                + $" payload=[{payloads.TrimEnd(',')}]"
                + $" | norm src={source} result={result.Disposition}"
                + $" | domain intent={fate} old={before.State} new={newState}"
                + $" stall={before.Stall}>{(alive ? after.Stall : 0)}"
                + $" | claims actors={before.ActorCount}>{Leases.ActorsFor(key).Count}"
                + $" [{string.Join(",", Leases.ActorsFor(key))}]"
                + $" resources={before.ResourceRows}>{Leases.ResourcesFor(key).Count}"
                + $" dirty={dirty}");
            LastLifecycleLine = line;
            // A repeated identical transition for the same operation (a failed attempt retried by
            // later cycles) is written once.
            AiDebugLog.WriteDeduped("lifecycle:" + key, line);
        }

        // End-of-turn invariants. Violations are written as [AI][V2][Invariant] ERROR lines and
        // returned (tests); the summary line is always written. Diagnostic only: nothing here
        // mutates lifecycle state, so it cannot change AI behaviour.
        internal IReadOnlyList<string> AuditTurnEnd(WorldSnapshot snapshot,
            IReadOnlyList<ReconObjective> objectives)
        {
            EnsureActive();
            var violations = new List<string>();
            if (_audited) return violations;
            _audited = true;
            int checkedOperations = 0;
            try { CollectViolations(snapshot, objectives, violations, ref checkedOperations); }
            catch (Exception e) { violations.Add("audit failed: " + e.GetType().Name + ": " + e.Message); }

            int bumps = WorldDeltaLifecycle.Current - _revisionAtBegin;
            int commits = WorldDeltaLifecycle.CommitEvents - _commitsAtBegin;
            int staged = WorldDeltaLifecycle.StagedChildMutations - _stagedAtBegin;
            AiDebugLog.Write($"[AI][V2][Invariant] turn={TurnNumber} revision bumps={bumps} commits={commits} "
                + $"stagedChildren={staged} progressReceipts={_progressReceipts.Count} "
                + $"operationsChecked={checkedOperations} violations={violations.Count}");
            foreach (string v in violations)
                AiDebugLog.Write("[AI][V2][Invariant] ERROR " + v);
            return violations;
        }

        private void CollectViolations(WorldSnapshot snapshot, IReadOnlyList<ReconObjective> objectives,
            List<string> violations, ref int checkedOperations)
        {
            int bumps = WorldDeltaLifecycle.Current - _revisionAtBegin;
            int commits = WorldDeltaLifecycle.CommitEvents - _commitsAtBegin;
            // 1. Every operation that owns actor claims still has a durable intent (else a retired
            //    operation leaked its claim), and every tracked operation's claims equal the
            //    detached derivation of the same intent.
            foreach (MissionIntentKey op in Leases.Operations())
                if (!PersistentState.TryGet(op, out _))
                    violations.Add($"claim owner {op} has no durable intent; actors=[{string.Join(",", Leases.ActorsFor(op))}]");
            if (snapshot?.Self?.Armies != null)
                foreach (MissionIntent intent in _lastActorIntents)
                {
                    if (intent == null || !PersistentState.TryGet(intent.IntentKey, out MissionIntent live)
                        || !ReferenceEquals(live, intent))
                        continue;
                    checkedOperations++;
                    var derived = new HashSet<int>(MissionActorPolicy.Build(new[] { intent }, snapshot,
                        objectives).ClaimedArmyIdSet);
                    var table = new HashSet<int>(Leases.ActorsFor(intent.IntentKey));
                    if (!derived.SetEquals(table))
                        violations.Add($"claim table != derived view for {intent.IntentKey}: "
                            + $"table=[{string.Join(",", table.OrderBy(x => x))}] "
                            + $"derived=[{string.Join(",", derived.OrderBy(x => x))}]");
                }

            // 2. Revision: advances equal commit events, no transaction is left open, and every
            //    step that claimed progress carries a receipt from this turn that is not in the future.
            if (bumps != commits)
                violations.Add($"revision advanced {bumps} time(s) but {commits} mutation(s) committed");
            if (WorldDeltaLifecycle.TransactionOpen)
                violations.Add("a world mutation transaction is still open at turn end");
            foreach (int receipt in _progressReceipts)
            {
                if (receipt <= _revisionAtBegin)
                    violations.Add($"a step reported progress with receipt {receipt}, which does not advance the "
                        + $"turn-start revision {_revisionAtBegin} (missed stamp)");
                else if (receipt > WorldDeltaLifecycle.Current)
                    violations.Add($"a step carries receipt {receipt} newer than the current revision "
                        + $"{WorldDeltaLifecycle.Current}");
            }
        }

        internal void SettleAfterTurn(IReadOnlyList<MissionStepResult> results)
        {
            EnsureActive();
            MissionContinuityLayer.ReconcileAfterTurn(Player, TurnNumber, results);
        }

        // Entry points for passes that run inside the turn (Reaction, Housekeeping), so they do not
        // reach the turn-scoped owners directly. A detached caller (no session, e.g. a fixture)
        // falls through to the same owner unchanged.
        internal static void SettleResultsAfterTurn(PlayerSetupData player, int turn,
            IReadOnlyList<MissionStepResult> results)
        {
            AiTurnSession session = Peek(player, turn);
            if (session != null) session.SettleAfterTurn(results);
            else MissionContinuityLayer.ReconcileAfterTurn(player, turn, results);
        }

        internal static void ClearPendingInvalidations(PlayerSetupData player, int turn) =>
            StrategicInterruptRegistry.Clear(player, turn);

        internal static void ReleaseCapabilityLeases(PlayerSetupData player, int turn) =>
            StrategicCapabilityLeaseRegistry.Clear(player, turn);

        internal int Apply(WorldDelta delta)
        {
            EnsureActive();
            return WorldDeltaLifecycle.Apply(Player, TurnNumber, delta);
        }

        internal void EnsureActive()
        {
            if (_ended) throw new ObjectDisposedException(nameof(AiTurnSession));
        }

        private void RequireFrame(WorldSnapshot snapshot)
        {
            if (snapshot != null && (snapshot.TurnNumber != TurnNumber
                || snapshot.Observer != null && !ReferenceEquals(snapshot.Observer, Player)))
                throw new InvalidOperationException("Decision frame belongs to another player or turn.");
        }

        internal static void ClearAll()
        {
            foreach (AiTurnSession session in new List<AiTurnSession>(Active.Values))
                session.Dispose();
        }

        public void Dispose()
        {
            if (_ended) return;
            CompleteReservations();
            StrategicInterruptRegistry.Clear(Player, TurnNumber);
            CapabilityPoolExhaustionRegistry.EndTurn(Player, TurnNumber);
            StrategicCapabilityLeaseRegistry.Clear(Player, TurnNumber);
            StrategicTempoBudget.EndTurn(Player, TurnNumber);
            AviationObligationStallRegistry.EndTurn(Player, TurnNumber);
            OperationContinuationWindow.EndTurn(Player, TurnNumber);
            ReconTurnStateStore.End(Player);
            foreach (var pass in _passClaims) pass.Close();
            _passClaims.Clear();
            Leases.Close();
            _ended = true;
            Active.Remove(Player);
        }
    }
}
