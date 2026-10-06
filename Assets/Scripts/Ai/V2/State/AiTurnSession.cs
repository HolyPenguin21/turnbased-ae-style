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
        private readonly ReconTurnState _recon;
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
            PersistentState = MissionIntentRegistry.GetOrCreate(player);
            _recon = ReconTurnStateStore.Begin(player, turn);
            V2TurnActivityTelemetry.Begin(player, turn);
            CapabilityPoolExhaustionRegistry.BeginTurn(player, turn);
            StrategicResourceReservationLedger.BeginTurn(player, turn);
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

        // Preserve the original normal-turn expiry/diagnostic ordering before the summary.
        // Dispose also runs this boundary on coroutine disposal or an exception.
        internal void CompleteReservations()
        {
            EnsureActive();
            StrategicResourceReservationLedger.ExpireStage(Player, TurnNumber,
                StrategicReservationExpiry.EndOfTurn);
            StrategicResourceReservationLedger.AssertClearAtTurnEnd(Player, TurnNumber);
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
            _ended = true;
            Active.Remove(Player);
        }
    }
}
