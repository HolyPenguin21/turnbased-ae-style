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
    //  The cold (zero-Radar) residual. A zero Radar is not a prohibition: only AFTER the existing
    //  operational and tempo passes exhaust their actionable budgets may new cold-axis preparation
    //  use what is physically left. No new budget, scorer or executor: the same Phase A owner is
    //  called with freshly regenerated cold demands.
    // ===========================================================================================
    internal sealed class ColdResidual : IColdWork
    {
        private readonly DecisionFrame _frame;
        private readonly PhaseResults _phases;
        private readonly PhaseAApBudget _apBudget;
        private readonly Radar _radar;
        private readonly HashSet<DesireAxis> _demandAxes;
        private readonly PlayerSetupData _player;
        private readonly PlayerRoot _root;
        private readonly AiHandData _hand;
        private readonly AiTurnContext _ctx;
        private HashSet<DesireAxis> _coldAxes;

        internal ColdResidual(DecisionFrame frame, PhaseResults phases, PhaseAApBudget apBudget, Radar radar,
            HashSet<DesireAxis> demandAxes, PlayerSetupData player, PlayerRoot root, AiHandData hand,
            AiTurnContext ctx)
        {
            _frame = frame;
            _phases = phases;
            _apBudget = apBudget;
            _radar = radar;
            _demandAxes = demandAxes;
            _player = player;
            _root = root;
            _hand = hand;
            _ctx = ctx;
        }

        public int AxisCount()
        {
            _coldAxes = new HashSet<DesireAxis>(_demandAxes.Where(a =>
                RadarValueScale.For(_radar, a) <= 0f));
            return _coldAxes.Count;
        }



        public IEnumerator Run(ColdSink sink)
        {
            yield return _frame.PrepareColdResidual();
            List<AxisDemand> coldDemands = _frame.GenerateDemands(_demandAxes)
                .Where(d => d != null && _coldAxes.Contains(d.RequestingAxis)).ToList();
            if (coldDemands.Count > 0)
            {
                // Phase A owns one carried Reservation object. Its per-call residual
                // rewrite must not erase still-unfulfilled positive-axis telemetry.
                List<AxisDemand> warmResidual = _phases.Carried
                    .UnresolvedDemands.Where(d => d != null
                        && RadarValueScale.For(_radar, d.RequestingAxis) > 0f).ToList();
                WorldAnalysis.StepObservationStamp beforeCold =
                    WorldAnalysis.CaptureStepObservation(_root, _hand, _frame.Snapshot);
                StrategicPhaseResult coldPass = StrategicManager.FulfillDemands(
                    _frame.Snapshot, _player, _root, _hand, _ctx, _apBudget, coldDemands,
                    _frame.Commitments, _frame.Intents, _frame.Recon,
                    _phases.Carried,
                    economyAxisAuthoritative: _coldAxes.Contains(DesireAxis.Economy),
                    radar: _radar);
                _phases.PhaseA.Accumulate(coldPass);
                _phases.PhaseA.Reservation.UnresolvedDemands.AddRange(warmResidual);
                AiDebugLog.Write($"[AI][V2][Loop] cold Radar residual — demands={coldDemands.Count} "
                    + $"spent={coldPass.CardsPlayed} changed={(coldPass.StateChanged ? 1 : 0)}");
                if (coldPass.StateChanged)
                {
                    yield return _frame.AcceptChangedCold(beforeCold, _demandAxes);
                    yield return _ctx.WaitAtObserverActionBoundary();
                    // TurnLoop opens the typed admission pass on this settled state.
                    sink.Changed = true;
                }
                else
                {
                    yield return _ctx.WaitAtObserverActionBoundary();
                }
            }
        }


    }
}
