using System;
using System.Collections.Generic;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  One pending-invalidation snapshot fanned out to every consumer family before anything is
    //  consumed (Pipeline.TakeTypedTriggers): the operational families and each strategic axis
    //  (Economy, Development, Aggression) get their share of a compound fact, so family order
    //  cannot erase a sibling's trigger. Pure: the caller consumes the returned reasons.
    // ===========================================================================================
    internal readonly struct TypedTriggerSplit
    {
        internal readonly StrategicInvalidationReason Operational;
        internal readonly StrategicInvalidationReason Strategic;
        internal readonly HashSet<DesireAxis> DirtyAxes;

        internal TypedTriggerSplit(StrategicInvalidationReason operational,
            StrategicInvalidationReason strategic, HashSet<DesireAxis> dirtyAxes)
        { Operational = operational; Strategic = strategic; DirtyAxes = dirtyAxes; }

        internal StrategicInvalidationReason Consumed => Operational | Strategic;
    }

    internal static class TypedTriggerFanOut
    {
        private static readonly DesireAxis[] StrategicAxes =
            { DesireAxis.Economy, DesireAxis.Development, DesireAxis.Aggression };

        // The OPERATIONAL mask covers every mission axis, not only Recon: destroying a neutral
        // publishes a Contact invalidation Aggression must consume, so the bounded loop gets a
        // same-turn chance to refresh objectives, pick the next target or start a Return.
        // Aggression's shortages re-enter on its own mask like Economy/Development (its
        // fingerprint drops re-entries whose inputs did not change).
        // economyBuilderReady is asked only when Economy's sole reason is Actor movement: it is
        // actionable then only if continuity's committed builder reached its build hex.
        internal static TypedTriggerSplit Split(StrategicInvalidationReason pending,
            Func<bool> economyBuilderReady)
        {
            StrategicInvalidationReason operational =
                pending & AiStrategyV2Scope.OperationalInvalidationMask;
            StrategicInvalidationReason strategic = StrategicInvalidationReason.None;
            var dirty = new HashSet<DesireAxis>();
            foreach (DesireAxis axis in StrategicAxes)
            {
                StrategicInvalidationReason axisReasons = pending & DesireAxes.InvalidationMaskFor(axis);
                if (axis == DesireAxis.Economy
                    && axisReasons == StrategicInvalidationReason.Actor
                    && !(economyBuilderReady?.Invoke() ?? false))
                    continue;
                if (axisReasons == StrategicInvalidationReason.None)
                    continue;
                dirty.Add(axis);
                strategic |= axisReasons;
            }
            return new TypedTriggerSplit(operational, strategic, dirty);
        }
    }
}
