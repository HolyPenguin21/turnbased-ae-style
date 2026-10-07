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
    // Persistent per-project delivery evidence and bounded suppression. No resource storage.
    internal sealed class EconomyLifecycleState
    {
        internal static bool ObjectiveSatisfied(PlayerSetupData player, EconomyMissionTarget t)
        {
            if (t.Kind == EconomyTaskKind.MobileCollection)
                return false;
            if (t.Kind == EconomyTaskKind.ReturnCollector)
                return t.CollectorArmyId.HasValue && ArmyRegistry.AllForOwner(player).Any(a => a != null
                    && a.Id == t.CollectorArmyId.Value && a.Owner == player
                    && a.Hex.Equals(t.TargetHex));
            if (t.Kind == EconomyTaskKind.ReturnBuilder)
                return t.BuilderArmyId.HasValue && ArmyRegistry.AllForOwner(player).Any(a => a != null
                    && a.Id == t.BuilderArmyId.Value && a.Owner == player
                    && a.Hex.Equals(t.TargetHex));
            BuildingData b = BuildingRegistry.AllBuildings().FirstOrDefault(x => x != null
                && x.Owner == player && x.Hex.Equals(t.TargetHex));
            if (t.Kind == EconomyTaskKind.FoundBase)
                return b != null && b.IsBase;
            return b != null && t.ResourceType.HasValue
                && b.HasFacilityWithAbility(UnitAbilities.CollectAbilityFor(t.ResourceType.Value));
        }



        // Bounded delivery-failure streaks. A structurally valid site may still be operationally
        // impossible for every builder: count only CONSECUTIVE-turn delivery-gate failures of the
        // exact project and, once the ordinary commitment stall window is exhausted, briefly
        // suppress that project so Demand compares other sites instead of repeating it. One
        // counter per project — Base by (card, site), Extraction by (resource, site) — because
        // several Economy builds can be active (and stuck) at once; a single shared slot let two
        // stuck projects reset each other's streak forever (Economy audit B4).
        private sealed class DeliveryFailureStreaks<TKey>
        {
            private readonly Dictionary<TKey, (int Turn, int Count)> _failures =
                new Dictionary<TKey, (int, int)>();
            private readonly Dictionary<TKey, int> _suppressedUntilTurn = new Dictionary<TKey, int>();
            // The last turn the project made real delivery progress. A turn with progress is never
            // a failed turn, whatever order that turn's outcomes arrive in (a first attempt blocked,
            // a later one delivered), and it ends the running streak.
            private readonly Dictionary<TKey, int> _progressTurn = new Dictionary<TKey, int>();

            public void RecordProgress(int turn, TKey key)
            {
                _progressTurn[key] = turn;
                _failures.Remove(key);
            }

            public bool IsSuppressed(int turn, TKey key) =>
                _suppressedUntilTurn.TryGetValue(key, out int until) && turn < until;

            public bool Record(int turn, TKey key)
            {
                if (IsSuppressed(turn, key))
                    return true;
                if (_progressTurn.TryGetValue(key, out int progressTurn) && progressTurn == turn)
                    return false;
                bool hasRecord = _failures.TryGetValue(key, out (int Turn, int Count) rec);
                bool consecutiveTurn = hasRecord && (rec.Turn == turn || rec.Turn == turn - 1);
                int count = consecutiveTurn ? rec.Count : 0;
                if (!hasRecord || rec.Turn != turn)
                    count++;
                _failures[key] = (turn, count);

                if (count < System.Math.Max(1, AiConfigV2.commitmentStallTurns))
                    return false;

                _suppressedUntilTurn[key] = turn
                    + System.Math.Max(1, AiConfigV2.allocatorRejectCooldownTurns) + 1;
                _failures.Remove(key);
                return true;
            }
        }

        private readonly DeliveryFailureStreaks<(CardData, HexCoord)> _baseDeliveryFailures =
            new DeliveryFailureStreaks<(CardData, HexCoord)>();
        private readonly DeliveryFailureStreaks<(ResourceType?, HexCoord)> _extractionDeliveryFailures =
            new DeliveryFailureStreaks<(ResourceType?, HexCoord)>();

        internal bool IsBaseExpansionDeliverySuppressed(int turn, CardData card, HexCoord? target) =>
            card != null && target.HasValue
            && _baseDeliveryFailures.IsSuppressed(turn, (card, target.Value));

        internal bool RecordBaseExpansionDeliveryFailure(int turn, CardData card, HexCoord? target) =>
            card != null && target.HasValue
            && _baseDeliveryFailures.Record(turn, (card, target.Value));

        // Real delivery progress of one build project this turn — ends its failure streak. The ONE
        // writer is MissionContinuityLayer.ReconcileOutcome.
        internal void RecordBaseExpansionDeliveryProgress(int turn, CardData card, HexCoord? target)
        {
            if (card != null && target.HasValue)
                _baseDeliveryFailures.RecordProgress(turn, (card, target.Value));
        }

        internal void RecordExtractionDeliveryProgress(int turn, ResourceType? resourceType, HexCoord target) =>
            _extractionDeliveryFailures.RecordProgress(turn, (resourceType, target));

        internal bool IsExtractionDeliverySuppressed(int turn, ResourceType? resourceType, HexCoord target) =>
            _extractionDeliveryFailures.IsSuppressed(turn, (resourceType, target));

        internal bool RecordExtractionDeliveryFailure(int turn, ResourceType? resourceType, HexCoord target) =>
            _extractionDeliveryFailures.Record(turn, (resourceType, target));

    }
}
