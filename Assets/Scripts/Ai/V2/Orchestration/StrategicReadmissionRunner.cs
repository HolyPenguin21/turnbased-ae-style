using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Game.Map;
using Game.Players;

namespace Game.Ai.V2
{
    // The re-admission as the trigger sequence sees it. The interface exists so the sequence can be
    // exercised on a real session with a scripted re-admission; the runner is the only implementation.
    internal interface IStrategicReadmission
    {
        IEnumerator Run(ReadmissionCause cause, StrategicInvalidationReason reasons,
            HashSet<DesireAxis> dirtyAxes, ReadmissionOutcome outcome);
    }

    // The result of one re-admission request: did Phase A change the world.
    internal sealed class ReadmissionOutcome
    {
        internal bool StateChanged;
    }

    // ===========================================================================================
    //  Typed strategic re-admission of the AI turn (the protocol that was RunTurn's inline
    //  ReenterStrategicAxes): StrategicReadmission decides which axes run (gate, deferral,
    //  fingerprints), the frame refreshes the operational decision and regenerates the dirty demand
    //  families, the existing Phase-A owner spends, the observation delta is published and the
    //  input fingerprints are committed. The cause says why the pass is requested: Trigger (typed
    //  facts), DeferredFlush (admit waiting axes once nothing is pending, loop top), TerminalForce
    //  (admit them even while an obligation is pending: the loop is over), CapacityUnlock.
    //  Uses the shared AP ledger and the carried reservation; it is not a second manager.
    // ===========================================================================================
    internal sealed class StrategicReadmissionRunner : IStrategicReadmission
    {
        private readonly DecisionFrame _frame;
        private readonly StrategicReadmission _readmission;
        private readonly PhaseResults _phases;
        private readonly PhaseAApBudget _apBudget;
        private readonly Radar _radar;
        private readonly PlayerSetupData _player;
        private readonly PlayerRoot _root;
        private readonly AiHandData _hand;
        private readonly AiTurnContext _ctx;

        internal StrategicReadmissionRunner(DecisionFrame frame, StrategicReadmission readmission,
            PhaseResults phases, PhaseAApBudget apBudget, Radar radar,
            PlayerSetupData player, PlayerRoot root, AiHandData hand, AiTurnContext ctx)
        {
            _frame = frame;
            _readmission = readmission;
            _phases = phases;
            _apBudget = apBudget;
            _radar = radar;
            _player = player;
            _root = root;
            _hand = hand;
            _ctx = ctx;
        }

        // The admission key of an axis on the CURRENT frame (the domain owner computes it).
        internal string Key(DesireAxis axis) =>
            StrategicAdmissionFingerprints.For(axis, _frame.Snapshot, _frame.Intents, _root, _hand, _player, _ctx);

        // The baselines of the axes that have an admission key.
        internal void SeedKeys(IEnumerable<DesireAxis> axes)
        {
            foreach (DesireAxis axis in axes.Where(a =>
                         a == DesireAxis.Economy || a == DesireAxis.Development
                         || a == DesireAxis.Aggression))
                _readmission.Seed(axis, Key(axis));
        }

        IEnumerator IStrategicReadmission.Run(ReadmissionCause cause, StrategicInvalidationReason reasons,
            HashSet<DesireAxis> dirtyAxes, ReadmissionOutcome outcome) =>
            Run(cause, reasons, dirtyAxes, outcome);

        internal IEnumerator Run(ReadmissionCause cause, StrategicInvalidationReason reasons,
            HashSet<DesireAxis> dirtyAxes) =>
            Run(cause, reasons, dirtyAxes, new ReadmissionOutcome());

        // T03 / one axis's demand family by stable consumer identity plus its amount: the re-admission
        // log's old -> new line.
        internal static string DemandIdentityDigest(IEnumerable<AxisDemand> demands, DesireAxis axis) =>
            string.Join(";", (demands ?? Enumerable.Empty<AxisDemand>())
                .Where(d => d != null && d.RequestingAxis == axis)
                .Select(d => $"{d.ConsumerIntentKey?.ToString() ?? "-"}:{d.Capability}"
                    + $":{d.AttackFistArmyId?.ToString() ?? "-"}"
                    + (d.AttackCoverageGap ? ":cov" : "")
                    + $":{(d.TargetHex.HasValue ? $"{d.TargetHex.Value.Q},{d.TargetHex.Value.R}" : "-")}"
                    + $"={d.DesiredAmount.ToString("0.#", CultureInfo.InvariantCulture)}")
                .OrderBy(x => x, System.StringComparer.Ordinal));

        internal IEnumerator Run(ReadmissionCause cause, StrategicInvalidationReason reasons,
            HashSet<DesireAxis> dirtyAxes, ReadmissionOutcome outcome)
        {
            outcome.StateChanged = false;
            DeferredAdmissionGate gate = _readmission.Decide(cause, reasons, dirtyAxes,
                () => AviationObligations.Pending(_player, _ctx), Key,
                (axis, fingerprint) =>
                    // Diagnostics only: admission still compares the full fingerprint.
                    // All axes can have large keys; print a digest and suppress repeats.
                    AiDebugLog.WriteDeduped($"admission-unchanged|{axis}",
                        $"[AI][V2][Loop] strategic re-admission skipped "
                        + $"axis={axis} reason=settled_state_unchanged fingerprint="
                        + $"#{(uint)fingerprint.GetHashCode():x8}/{fingerprint.Length}"),
                out HashSet<DesireAxis> admittedAxes);
            if (gate == DeferredAdmissionGate.Skip)
                yield break;
            if (gate == DeferredAdmissionGate.Defer)
            {
                AiDebugLog.Write($"[AI][V2][Loop] strategic re-admission deferred — aviation "
                    + $"obligations pending; axes={string.Join(",", _readmission.Deferred.Axes)}");
                yield break;
            }
            if (gate == DeferredAdmissionGate.AdmitDespitePending)
                AiDebugLog.Write("[AI][V2][Loop] aviation obligations still pending after the "
                    + "loop — admitting the deferred axes anyway");
            dirtyAxes = admittedAxes;
            if (dirtyAxes.Count == 0)
                yield break;

            yield return _frame.RefreshOperationalDecision();
            // T03 — the baseline is the input Generate actually evaluates: taken after
            // continuity resolved (a completed target, a handed-off donor), before any
            // follow-up delivery. Post-delivery state is judged by the delta it publishes,
            // never pre-declared as already considered.
            Dictionary<DesireAxis, string> admittedFingerprints = dirtyAxes
                .ToDictionary(axis => axis, Key);
            Dictionary<DesireAxis, string> demandsBefore = dirtyAxes.ToDictionary(axis => axis,
                axis => DemandIdentityDigest(_frame.Demands, axis));
            List<AxisDemand> regenerated = _frame.GenerateDemands(dirtyAxes);
            List<AxisDemand> dirtyDemands = regenerated;
            _frame.ReplaceDemandFamilies(dirtyAxes, regenerated);
            // Economy deferred-hold reconciliation now lives entirely inside
            // StrategicPhaseA (economyAxisAuthoritative) — a single canonical writer
            // instead of this call duplicating the same existence check right before it.
            // dirtyAxes.Contains(Economy) is the exact "was Economy actually re-evaluated
            // this round" signal Phase A needs to tell "Economy resolved" apart from
            // "Economy wasn't part of this dirty-axis subset".
            WorldAnalysis.StepObservationStamp beforeCapabilities =
                WorldAnalysis.CaptureStepObservation(_root, _hand, _frame.Snapshot);
            StrategicPhaseResult followup = StrategicManager.FulfillDemands(
                _frame.Snapshot, _player, _root, _hand, _ctx, _apBudget, dirtyDemands,
                _frame.Commitments, _frame.Intents, _frame.Recon,
                _phases.Carried,
                economyAxisAuthoritative: dirtyAxes.Contains(DesireAxis.Economy), radar: _radar,
                deferFreshZeroRadar: true);
            _phases.PhaseA.Accumulate(followup);
            ReservationInvariants.CheckBoundary(_player, _root, _ctx,
                $"phaseA reentry axes={string.Join(",", dirtyAxes)}");
            if (followup.StateChanged)
            {
                yield return _frame.AcceptChangedReentry();
            }
            WorldAnalysis.StepObservationStamp afterCapabilities =
                WorldAnalysis.CaptureStepObservation(_root, _hand, _frame.Snapshot);
            WorldAnalysis.PublishStepObservationDelta(_player, _ctx.TurnNumber,
                beforeCapabilities, afterCapabilities, null);
            _readmission.Commit(dirtyAxes, admittedFingerprints);
            AiDebugLog.Write($"[AI][V2][Loop] strategic re-admission "
                + $"axes={string.Join(",", dirtyAxes)} triggers={reasons} "
                + $"changed={(followup.StateChanged ? 1 : 0)}");
            foreach (DesireAxis axis in dirtyAxes)
            {
                string after = DemandIdentityDigest(regenerated, axis);
                AiDebugLog.Write($"[AI][V2][Loop] strategic re-admission demands axis={axis} "
                    + (after == demandsBefore[axis]
                        ? $"unchanged count={regenerated.Count(d => d?.RequestingAxis == axis)}"
                        : $"old=[{demandsBefore[axis]}] new=[{after}]"));
            }
            outcome.StateChanged = followup.StateChanged;
        }


    }
}
