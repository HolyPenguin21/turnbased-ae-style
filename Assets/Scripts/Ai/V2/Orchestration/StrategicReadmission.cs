using System;
using System.Collections.Generic;

namespace Game.Ai.V2
{
    // Why a strategic re-admission is requested. One protocol, explicit causes instead of
    // flush / force flags. (The cold zero-Radar residual is NOT a cause: it selects its axes by
    // Radar and bypasses the fingerprint gate; it stays its own branch until level 4.)
    public enum ReadmissionCause
    {
        Trigger,         // typed facts named dirty axes after a settled step / Phase B round
        DeferredFlush,   // loop top: admit axes that waited for aviation once nothing is pending
        TerminalForce,   // loop over: admit waiting axes even while an obligation is pending
        CapacityUnlock,  // a stand-alone Base level opened a slot after the baselines were taken
    }

    // ===========================================================================================
    //  The arithmetic of a strategic re-admission: which axes are admitted now. It owns exactly the
    //  two pieces of per-turn state this needs — the key last admitted per axis and the axes that
    //  waited for aviation — and compares keys; it never computes a key (the domain owners do,
    //  via StrategicAdmissionFingerprints) and never runs a pass (the pipeline does).
    // ===========================================================================================
    internal sealed class StrategicReadmission
    {
        private readonly Dictionary<DesireAxis, string> _admitted = new Dictionary<DesireAxis, string>();

        internal DeferredStrategicAdmission Deferred { get; } = new DeferredStrategicAdmission();

        internal static bool Needed(IReadOnlyDictionary<DesireAxis, string> lastHandled,
            DesireAxis axis, string fingerprint) => lastHandled == null
            || !lastHandled.TryGetValue(axis, out string previous)
            || previous != fingerprint;

        // The baseline taken when the turn's first demand frame was judged.
        internal void Seed(DesireAxis axis, string fingerprint) => _admitted[axis] = fingerprint;

        // The key an axis was last admitted with (null when none).
        internal string LastAdmitted(DesireAxis axis) =>
            _admitted.TryGetValue(axis, out string key) ? key : null;

        // Decide a request. Returns the gate; on Admit, `axes` holds the axes to run (deferred ones
        // merged in, unchanged keys removed — possibly empty). `onUnchanged` receives each dropped
        // axis with its key for diagnostics. `obligationsPending` is read only when the request
        // could be admitted at all.
        internal DeferredAdmissionGate Decide(ReadmissionCause cause, StrategicInvalidationReason reasons,
            HashSet<DesireAxis> dirtyAxes, Func<bool> obligationsPending,
            Func<DesireAxis, string> fingerprintOf, Action<DesireAxis, string> onUnchanged,
            out HashSet<DesireAxis> axes)
        {
            axes = null;
            bool flush = cause == ReadmissionCause.DeferredFlush;
            bool force = cause == ReadmissionCause.TerminalForce;
            bool triggered = reasons != StrategicInvalidationReason.None
                && dirtyAxes != null && dirtyAxes.Count > 0;
            if (cause == ReadmissionCause.CapacityUnlock)
                _admitted.Remove(DesireAxis.Development);
            bool wanted = triggered || ((flush || force) && Deferred.HasAxes);
            DeferredAdmissionGate gate = DeferredStrategicAdmission.Gate(triggered, flush, force,
                Deferred.HasAxes, obligationsPending: wanted && obligationsPending());
            if (gate == DeferredAdmissionGate.Defer)
            {
                Deferred.Defer(dirtyAxes);
                return gate;
            }
            if (gate == DeferredAdmissionGate.Skip)
                return gate;

            axes = Deferred.HasAxes ? Deferred.TakeWith(triggered ? dirtyAxes : null) : dirtyAxes;
            axes.RemoveWhere(axis =>
            {
                string fingerprint = fingerprintOf(axis);
                bool unchanged = !Needed(_admitted, axis, fingerprint);
                if (unchanged)
                    onUnchanged?.Invoke(axis, fingerprint);
                return unchanged;
            });
            return gate;
        }

        // The keys the admitted axes were judged with (taken after the frame was refreshed).
        internal void Commit(IEnumerable<DesireAxis> axes, IReadOnlyDictionary<DesireAxis, string> fingerprints)
        {
            foreach (DesireAxis axis in axes)
                _admitted[axis] = fingerprints[axis];
        }
    }
}
