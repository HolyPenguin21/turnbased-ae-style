using System;
using System.Collections.Generic;
using System.Linq;
using Game.Cards;
using Game.HexGrid;
using Game.Map;
using Game.Players;

namespace Game.Ai.V2
{
    // The file-format layer supplies a typed detached objective payload. The tag is mandatory;
    // no Unity JSON polymorphism, reflection, live MissionIntent reference, or re-planning fallback.
    // A codec must cover all six model types and preserve every field (including phase clocks,
    // pinned actor IDs, gather returns, retreat evidence, CardData/UnitData IDs and nullable values).
    // Capture must not return the mutable live objective; Restore must return a fresh rebound one.
    internal interface IMissionObjectiveStateCodec<TState>
    {
        TState CaptureScout(ScoutIntent objective);
        TState CaptureRaid(RaidIntent objective);
        TState CaptureEconomy(EconomyIntent objective);
        TState CaptureDevelopment(DevelopmentIntent objective);
        TState CaptureActiveDefence(ActiveDefenceIntent objective);
        TState CaptureAttack(AttackIntent objective);
        IMissionObjective Restore(MissionKind kind, TState state);
    }

    public sealed partial class MissionIntentState
    {
        internal sealed class IntentState<TObjective>
        {
        // Exact detached save reconstruction; no planning, registration or normalization.
        internal IntentState(bool __matchSave, global::Game.Ai.V2.MissionIntentKey __IntentKey, global::Game.Ai.V2.StableMissionKey __LastAttemptKey, global::Game.Ai.V2.MissionKind __Kind, global::Game.Ai.V2.CommitmentTier __Funding, global::Game.Ai.V2.IntentStatus __Status, global::Game.Ai.V2.SuspendReason __Suspended, int __CreatedTurn, int __TurnsActive, int __LastReconciledTurn, int __LastProgressTurn, int __LastProtectedTurn, int __StallTurns, float __LastIntrinsicValue, float __CumulativeApSpent, int __StepsMovedTotal, int? __PreferredMoverArmyId, TObjective __Objective)
        {
            IntentKey = __IntentKey;
            LastAttemptKey = __LastAttemptKey;
            Kind = __Kind;
            Funding = __Funding;
            Status = __Status;
            Suspended = __Suspended;
            CreatedTurn = __CreatedTurn;
            TurnsActive = __TurnsActive;
            LastReconciledTurn = __LastReconciledTurn;
            LastProgressTurn = __LastProgressTurn;
            LastProtectedTurn = __LastProtectedTurn;
            StallTurns = __StallTurns;
            LastIntrinsicValue = __LastIntrinsicValue;
            CumulativeApSpent = __CumulativeApSpent;
            StepsMovedTotal = __StepsMovedTotal;
            PreferredMoverArmyId = __PreferredMoverArmyId;
            Objective = __Objective;
        }

            internal readonly MissionIntentKey IntentKey;
            internal readonly StableMissionKey LastAttemptKey;
            internal readonly MissionKind Kind;
            internal readonly CommitmentTier Funding;
            internal readonly IntentStatus Status;
            internal readonly SuspendReason Suspended;
            internal readonly int CreatedTurn;
            internal readonly int TurnsActive;
            internal readonly int LastReconciledTurn;
            internal readonly int LastProgressTurn;
            internal readonly int LastProtectedTurn;
            internal readonly int StallTurns;
            internal readonly float LastIntrinsicValue;
            internal readonly float CumulativeApSpent;
            internal readonly int StepsMovedTotal;
            internal readonly int? PreferredMoverArmyId;
            internal readonly TObjective Objective;
            internal IntentState(MissionIntent source, IMissionObjectiveStateCodec<TObjective> codec)
            {
                IntentKey = source.IntentKey;
                LastAttemptKey = source.LastAttemptKey;
                Kind = source.Kind;
                Funding = source.Funding;
                Status = source.Status;
                Suspended = source.Suspended;
                CreatedTurn = source.CreatedTurn;
                TurnsActive = source.TurnsActive;
                LastReconciledTurn = source.LastReconciledTurn;
                LastProgressTurn = source.LastProgressTurn;
                LastProtectedTurn = source.LastProtectedTurn;
                StallTurns = source.StallTurns;
                LastIntrinsicValue = source.LastIntrinsicValue;
                CumulativeApSpent = source.CumulativeApSpent;
                StepsMovedTotal = source.StepsMovedTotal;
                PreferredMoverArmyId = source.PreferredMoverArmyId;
                if (source.Objective == null || source.Objective.Kind != source.Kind)
                    throw new ArgumentException("Missing or mismatched durable mission objective");
                switch (source.Objective)
                {
                    case ScoutIntent x: Objective = codec.CaptureScout(x); break;
                    case RaidIntent x: Objective = codec.CaptureRaid(x); break;
                    case EconomyIntent x: Objective = codec.CaptureEconomy(x); break;
                    case DevelopmentIntent x: Objective = codec.CaptureDevelopment(x); break;
                    case ActiveDefenceIntent x: Objective = codec.CaptureActiveDefence(x); break;
                    case AttackIntent x: Objective = codec.CaptureAttack(x); break;
                    default: throw new NotSupportedException("Unsupported durable mission objective");
                }
            }
            internal MissionIntent Restore(IMissionObjectiveStateCodec<TObjective> codec)
            {
                IMissionObjective objective = codec.Restore(Kind, Objective);
                if (objective == null || objective.Kind != Kind)
                    throw new ArgumentException("Mismatched restored mission objective tag");
                var intent = new MissionIntent
                {
                    Objective = objective,
                    IntentKey = IntentKey,
                    LastAttemptKey = LastAttemptKey,
                    Kind = Kind,
                    Funding = Funding,
                    Status = Status,
                    Suspended = Suspended,
                    CreatedTurn = CreatedTurn,
                    TurnsActive = TurnsActive,
                    LastReconciledTurn = LastReconciledTurn,
                    LastProgressTurn = LastProgressTurn,
                    LastProtectedTurn = LastProtectedTurn,
                    StallTurns = StallTurns,
                    LastIntrinsicValue = LastIntrinsicValue,
                    CumulativeApSpent = CumulativeApSpent,
                    StepsMovedTotal = StepsMovedTotal,
                    PreferredMoverArmyId = PreferredMoverArmyId
                };
                if (!objective.IntentKey(intent.PreferredMoverArmyId).Equals(IntentKey))
                    throw new ArgumentException("Restored mission objective does not match its saved key");
                return intent;
            }
        }

        internal readonly struct RetreatTargetState
        {
        // Exact detached save reconstruction; no planning, registration or normalization.
        internal RetreatTargetState(bool __matchSave, bool __HasValue, global::Game.HexGrid.HexCoord __Hex, string __ExpectedOwnerId, global::Game.Ai.V2.AttackTargetKind __Kind)
        {
            HasValue = __HasValue;
            Hex = __Hex;
            ExpectedOwnerId = __ExpectedOwnerId;
            Kind = __Kind;
        }

            internal readonly bool HasValue;
            internal readonly HexCoord Hex;
            internal readonly string ExpectedOwnerId;
            internal readonly AttackTargetKind Kind;
            internal RetreatTargetState(AttackTargetRef target, Func<PlayerSetupData, string> playerId)
            {
                HasValue = target.HasValue;
                Hex = target.Hex;
                Kind = target.Kind;
                ExpectedOwnerId = target.HasValue ? AiPersistenceCollections.CapturePlayerId(target.ExpectedOwner, playerId) : null;
            }
            internal AttackTargetRef Restore(Func<string, PlayerSetupData> player) => !HasValue
                ? AttackTargetRef.None : AttackTargetRef.For(Hex,
                    AiPersistenceCollections.ResolvePlayer(ExpectedOwnerId, player), Kind);
        }

        internal readonly struct RetreatWitnessState
        {
        // Exact detached save reconstruction; no planning, registration or normalization.
        internal RetreatWitnessState(bool __matchSave, global::Game.Ai.V2.MissionIntentState.RetreatTargetState __Target, int __OwnArmyId, int __EnemyArmyId, int __EnemyFingerprint, int __OwnFingerprint, int __Turn, global::Game.HexGrid.HexCoord __EnemyHex, string __Reason)
        {
            Target = __Target;
            OwnArmyId = __OwnArmyId;
            EnemyArmyId = __EnemyArmyId;
            EnemyFingerprint = __EnemyFingerprint;
            OwnFingerprint = __OwnFingerprint;
            Turn = __Turn;
            EnemyHex = __EnemyHex;
            Reason = __Reason;
        }

            internal readonly RetreatTargetState Target;
            internal readonly int OwnArmyId, EnemyArmyId, EnemyFingerprint, OwnFingerprint, Turn;
            internal readonly HexCoord EnemyHex;
            internal readonly string Reason;
            internal RetreatWitnessState(AttackRetreatWitness x, Func<PlayerSetupData, string> playerId)
            {
                Target = new RetreatTargetState(x.Target, playerId); OwnArmyId = x.OwnArmyId; EnemyArmyId = x.EnemyArmyId;
                EnemyHex = x.EnemyHex; EnemyFingerprint = x.EnemyFingerprint;
                OwnFingerprint = x.OwnFingerprint; Turn = x.Turn; Reason = x.Reason;
            }
            internal AttackRetreatWitness Restore(Func<string, PlayerSetupData> player) => new AttackRetreatWitness
            {
                Target = Target.Restore(player), OwnArmyId = OwnArmyId, EnemyArmyId = EnemyArmyId,
                EnemyHex = EnemyHex, EnemyFingerprint = EnemyFingerprint,
                OwnFingerprint = OwnFingerprint, Turn = Turn, Reason = Reason,
            };
        }

        internal sealed class PersistentState<TObjective>
        {
        // Exact detached save reconstruction; no planning, registration or normalization.
        internal PersistentState(bool __matchSave, global::System.Collections.Generic.IReadOnlyList<global::Game.Ai.V2.MissionIntentState.IntentState<TObjective>> __Intents, global::System.Collections.Generic.IReadOnlyList<global::Game.Ai.V2.MissionIntentState.RetreatWitnessState> __RetreatWitnesses, global::Game.Ai.V2.EconomyLifecycleState.PersistentState __Economy, global::System.Collections.Generic.IReadOnlyDictionary<int, (global::Game.HexGrid.HexCoord Site, global::Game.Cards.ResearchProductionMode Mode, int Turn)> __Development)
        {
            Intents = __Intents;
            RetreatWitnesses = __RetreatWitnesses;
            Economy = __Economy;
            Development = __Development;
        }

            internal readonly IReadOnlyList<IntentState<TObjective>> Intents;
            internal readonly IReadOnlyList<RetreatWitnessState> RetreatWitnesses;
            internal readonly EconomyLifecycleState.PersistentState Economy;
            internal readonly IReadOnlyDictionary<int, (HexCoord Site, ResearchProductionMode Mode, int Turn)> Development;
            internal PersistentState(MissionIntentState source, IMissionObjectiveStateCodec<TObjective> codec,
                Func<PlayerSetupData, string> playerId, Action<CardData> includeReferencedCard)
            {
                Intents = AiPersistenceCollections.FreezeList(source._intents.Select(x =>
                {
                    if (!x.Key.Equals(x.Value.IntentKey)) throw new InvalidOperationException("Unsettled mission rekey");
                    return new IntentState<TObjective>(x.Value, codec);
                }));
                RetreatWitnesses = AiPersistenceCollections.FreezeList(source._retreatWitnesses.Values.Select(x => new RetreatWitnessState(x, playerId)));
                Economy = source.Economy.CapturePersistentState(includeReferencedCard);
                Development = source.Development.CapturePersistentState(includeReferencedCard);
            }
        }

        internal PersistentState<TObjective> CapturePersistentState<TObjective>(IMissionObjectiveStateCodec<TObjective> codec,
            Func<PlayerSetupData, string> playerId, Action<CardData> includeReferencedCard = null)
        {
            if (codec == null) throw new ArgumentNullException(nameof(codec));
            if (playerId == null) throw new ArgumentNullException(nameof(playerId));
            return new PersistentState<TObjective>(this, codec, playerId, includeReferencedCard);
        }

        // Produces a detached owner-bound store before it can become registry authority.
        internal static MissionIntentState RestorePersistentState<TObjective>(PlayerSetupData owner,
            PersistentState<TObjective> state, IMissionObjectiveStateCodec<TObjective> codec, Func<int, CardData> card,
            Func<string, PlayerSetupData> player, bool reserveIdentities = true)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            if (codec == null) throw new ArgumentNullException(nameof(codec));
            var restored = new MissionIntentState(owner);
            foreach (var row in state.Intents)
            {
                MissionIntent intent = row.Restore(codec);
                restored._intents.Add(intent.IntentKey, intent);
            }
            foreach (var row in state.RetreatWitnesses)
            {
                var witness = row.Restore(player);
                restored._retreatWitnesses.Add(witness.Target, witness);
            }
            restored.Economy.RestorePersistentState(state.Economy, card);
            restored.Development.RestorePersistentState(state.Development, card);
            int highestArmyId = restored._intents.Values.Select(x => x.PreferredMoverArmyId ?? -1)
                .Concat(restored._retreatWitnesses.Values.SelectMany(x => new[] { x.OwnArmyId, x.EnemyArmyId }))
                .DefaultIfEmpty(-1).Max();
            if (reserveIdentities && highestArmyId >= 0) ArmyData.ReserveIdentity(highestArmyId);
            return restored;
        }
    }

    public static partial class MissionIntentRegistry
    {
        internal static IReadOnlyDictionary<string, MissionIntentState.PersistentState<TObjective>> CapturePersistentState<TObjective>(
            Func<PlayerSetupData, string> playerId, IMissionObjectiveStateCodec<TObjective> codec, Action<CardData> includeReferencedCard = null) =>
            AiPersistenceCollections.Capture(ByPlayer, playerId, x => x.CapturePersistentState(codec, playerId, includeReferencedCard));

        internal static void RestorePersistentState<TObjective>(
            IReadOnlyDictionary<string, MissionIntentState.PersistentState<TObjective>> state,
            Func<string, PlayerSetupData> player, IMissionObjectiveStateCodec<TObjective> codec, Func<int, CardData> card)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            if (player == null) throw new ArgumentNullException(nameof(player));
            var restored = new Dictionary<PlayerSetupData, MissionIntentState>();
            foreach (var row in state)
            {
                PlayerSetupData owner = AiPersistenceCollections.ResolvePlayer(row.Key, player);
                restored.Add(owner, MissionIntentState.RestorePersistentState(owner, row.Value, codec, card, player));
            }
            Clear();
            foreach (var row in restored) ByPlayer.Add(row.Key, row.Value);
        }
    }
}
