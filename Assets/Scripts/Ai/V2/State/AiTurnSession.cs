using System;
using System.Collections.Generic;
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
        internal int DecisionRevision => V2StateVersion.Current;
        internal MissionIntentState PersistentState { get; }
        internal MissionLeaseBook Leases { get; }
        private readonly ReconTurnState _recon;
        private readonly List<MissionLeaseBook> _passClaims = new List<MissionLeaseBook>();
        private bool _ended;

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
            string before = result == null ? null : IntentState(result.IntentKey);
            MissionContinuityLayer.ReconcileStep(Player, TurnNumber, result);
            if (result != null) LogTransition(result, before);
            if (result == null || snapshot == null) return;
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

        private string IntentState(MissionIntentKey key) =>
            PersistentState.TryGet(key, out MissionIntent intent) && intent != null
                ? intent.Status.ToString() : "none";

        // The single lifecycle-transition line; domain continuity keeps its own detail lines.
        private void LogTransition(MissionStepResult result, string before)
        {
            MissionIntentKey key = result.IntentKey;
            var dirty = StrategicInvalidationReason.None;
            foreach (WorldDelta delta in result.WorldDeltas) dirty |= delta.DirtyFacts;
            AiDebugLog.Write($"[AI][V2][Lifecycle] operation={key} kind={key.Kind} old={before} "
                + $"result={result.Disposition} new={IntentState(key)} "
                + $"actors=[{string.Join(",", Leases.ActorsFor(key))}] "
                + $"resources={Leases.ResourcesFor(key).Count} dirty={dirty}");
        }

        internal void SettleAfterTurn(IReadOnlyList<MissionStepResult> results)
        {
            EnsureActive();
            MissionContinuityLayer.ReconcileAfterTurn(Player, TurnNumber, results);
        }

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
