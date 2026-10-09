using System;
using System.Collections;
using System.Collections.Generic;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  The typed-trigger fan-out that follows a settled work step: take the pending facts, fan them
    //  out, consume exactly that snapshot, re-enter the strategic axes — once per "pair".
    //
    //  WHY THERE ARE TWO PAIR COUNTS (baseline behaviour, kept on purpose):
    //    StandardPairs = 2 (mission step, recovery, Phase B management round). Re-entering the
    //      strategic axes can itself publish a COMPOUND fact (for example materializing a Raid
    //      reinforcement changes Actor + Capability). Consume removes only the reasons of the
    //      snapshot it was given, so that fact stays pending after the first pair; the second pair
    //      takes it, fans it out to EVERY consumer family (Economy / Development / Aggression and
    //      the operational families) and re-enters for it within the same step.
    //    RebasePairs = 1 (a resumed multi-turn rebase). The first pair is the same; the follow-up
    //      pair never ran for a rebase. A fact the first re-entry publishes is therefore NOT lost —
    //      it stays pending in StrategicInterruptRegistry and the next take (the next work step's
    //      first pair, or the management round) fans it out to the same consumers — but it is
    //      processed one work step LATER. In between, the next work step can already decide and
    //      spend AP before that follow-up admission runs, so making the rebase 2 pairs would change
    //      the order of decisions inside a turn. That is a behaviour change, outside the
    //      simplification, and the rebase path has never been observed in a native log.
    //  A re-entry only publishes new facts when it actually admits axes; while another aviation
    //  obligation is pending the first re-entry defers instead (no new facts either way).
    // ===========================================================================================
    internal static class StepTriggerSequence
    {
        internal const int StandardPairs = 2;
        internal const int RebasePairs = 1;

        // take: fan the pending facts out and consume that snapshot (nothing else).
        // reenter: the re-admission for the strategic share of that snapshot.
        // reentryChanged: whether the last re-entry changed state.
        internal static IEnumerator Run(int pairs, Func<TypedTriggerSplit> take,
            Func<StrategicInvalidationReason, HashSet<DesireAxis>, IEnumerator> reenter,
            Func<bool> reentryChanged, Action<StepTriggerOutcome> done)
        {
            StrategicInvalidationReason operationalReasons = StrategicInvalidationReason.None;
            StrategicInvalidationReason strategicReasons = StrategicInvalidationReason.None;
            bool strategicChanged = false;
            for (int pair = 0; pair < pairs; pair++)
            {
                TypedTriggerSplit split = take();
                yield return reenter(split.Strategic, split.DirtyAxes);
                strategicChanged |= reentryChanged();
                operationalReasons |= split.Operational;
                strategicReasons |= split.Strategic;
            }
            done(new StepTriggerOutcome(operationalReasons, strategicReasons, strategicChanged));
        }
    }
}
