using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Game.Cards;
using Game.Economy;
using Game.HexGrid;
using Game.Map;
using Game.Players;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  A Phase B (tempo) round of the AI turn, and the settlement of the bank before the first
    //  one. Management / Development is another bounded task family, not the owner of the
    //  operational loop: a round settles until Phase B exhausts its candidates or publishes a
    //  capability-changing residual; typed Analysis deltas then re-admit only the affected family.
    //  Each round consumes a coherent strategic snapshot, refreshed first (the frame).
    // ===========================================================================================
    internal sealed class TempoRound : ITempoWork
    {
        private readonly DecisionFrame _frame;
        private readonly AiTurnSession _session;
        private readonly StrategicReadmissionRunner _runner;
        private readonly PhaseResults _phases;
        private readonly PlayerSetupData _player;
        private readonly PlayerRoot _root;
        private readonly AiHandData _hand;
        private readonly AiTurnContext _ctx;

        internal TempoRound(DecisionFrame frame, AiTurnSession session, StrategicReadmissionRunner runner,
            PhaseResults phases, PlayerSetupData player, PlayerRoot root, AiHandData hand, AiTurnContext ctx)
        {
            _frame = frame;
            _session = session;
            _runner = runner;
            _phases = phases;
            _player = player;
            _root = root;
            _hand = hand;
            _ctx = ctx;
        }

        // The first Phase B: the ordinary passes are closed (also on bounded / no-progress exits):
        // the bank settles what it holds before Phase B spends (StrategicTurnLifecycle).
        public void SettleBeforeFirstRound() =>
            StrategicManager.BeforeFirstTempo(_player, _root, _hand, _ctx);

        public IEnumerator Round(int managementRound, TempoRoundSink sink)
        {
            _frame.PrepareTempoOwnership();

            WorldAnalysis.StepObservationStamp beforeManagement =
                WorldAnalysis.CaptureStepObservation(_root, _hand, _frame.Snapshot);
            // A prior Phase B action may have spent AP or removed a build card: the bank
            // revalidates its holds before the next pass.
            StrategicManager.BeforeTempoSpend(_player, _root, _hand, _ctx);
            var phaseBRound = new StrategicPhaseResult();
            yield return StrategicManager.UseSurplus(_frame.Snapshot, _player, _root, _hand, _ctx,
                _frame.PostCommitments, _phases.Carried,
                phaseBRound, _frame.Recon);
            ReservationInvariants.CheckBoundary(_player, _root, _ctx,
                $"phaseB round {managementRound + 1}");
            _frame.ObserveSettled(beforeManagement, null);
            _phases.PhaseB.Accumulate(phaseBRound);
            yield return _ctx.WaitAtObserverActionBoundary();

            // Phase B reentry can itself publish a compound invalidation: the second pair
            // preserves its full typed fan-out before it is acknowledged.
            var triggers = new StepTriggerSink();
            yield return StepTriggerSequence.Run(StepTriggerSequence.StandardPairs, _session, _frame, _runner, triggers);
            StepTriggerOutcome stepTriggers = triggers.Outcome;
            StrategicInvalidationReason operationalReasons = stepTriggers.Operational;
            StrategicInvalidationReason strategicReasons = stepTriggers.Strategic;
            bool operationalDirty = operationalReasons != StrategicInvalidationReason.None;
            bool strategicDirty = strategicReasons != StrategicInvalidationReason.None;
            bool strategicChanged = stepTriggers.StrategicChanged;

            AiDebugLog.Write($"[AI][V2][Loop] management round={managementRound + 1} "
                + $"strategicTriggers={strategicReasons} "
                + $"operationalTriggers={operationalReasons} "
                + $"operationalReadmit={(operationalDirty ? 1 : 0)}");
            // Phase B can change the hand or world without publishing a typed operational
            // trigger: TurnLoop then reuses the canonical bounded admission on the settled
            // state before admitting any zero-Radar residual.
            sink.Outcome = new TempoRoundOutcome(operationalDirty, strategicDirty, strategicChanged,
                phaseBRound.StateChanged);
        }


    }
}
