using System.Collections.Generic;
using System.Linq;
using Game.Players;

namespace Game.Ai.V2
{
    // Central execution scope for focused Strategy V2 development/testing.
    // A focus scope is intentionally enforced at orchestration boundaries rather than by scattered
    // feature flags: radar allocation, durable intents, capability demand, mission admission and
    // surplus preparation all consult the same switch.
    //
    //   Full             — all four desire axes (Recon / Aggression / Economy / Development).
    //                      Active Defence is not a separate axis — it is folded into Aggression.
    //   ReconOnly        — Recon only. Radar is pinned to RCN:1.
    //   ReconDevelopment — Recon + Development. Aggression / Economy demand is dropped.
    //                      StrategicManager Phase A/B, Development generation, attach and draw all
    //                      stay.
    //   ReconAggressionEconomyDevelopment
    //                    — Recon + Economy + Aggression + Development, i.e. the same axes as Full.
    //                      AllowStrategicPressure stays OFF: turning neutral Raid on must never
    //                      re-enable the old Citadel-pressure advance.
    public enum AiStrategyV2Mode
    {
        Full,
        ReconOnly,
        ReconDevelopment,
        ReconEconomyDevelopment,
        ReconAggressionEconomyDevelopment,
    }

    public static class AiStrategyV2Scope
    {
        // Focused production bring-up: Recon -> Economy -> Aggression demand -> Development/
        // Production support. Phase B and Housekeeping are unaffected, and AllowStrategicPressure
        // stays OFF for every focus scope.
        public static AiStrategyV2Mode Mode = AiStrategyV2Mode.ReconAggressionEconomyDevelopment;

        public static bool IsReconOnly => Mode == AiStrategyV2Mode.ReconOnly;

        // Any non-Full mode is a focus scope: it restricts which desire axes may reach Phase A /
        // mission planning and suppresses the legacy strategic reaction path.
        public static bool IsFocusScoped => Mode != AiStrategyV2Mode.Full;

        // The bounded typed loop is now the production execution path for every scope, including
        // Full. Scope remains useful for isolated diagnostics, but no longer selects the legacy
        // batch orchestrator or disables production axes by default.
        public static bool UsesTypedLoop => true;

        private static readonly DesireAxis[] AllAxes =
        {
            DesireAxis.Recon, DesireAxis.Aggression,
            DesireAxis.Economy, DesireAxis.Development,
        };

        private static readonly DesireAxis[] ReconOnlyAxes = { DesireAxis.Recon };

        private static readonly DesireAxis[] ReconDevelopmentAxes =
        {
            DesireAxis.Recon, DesireAxis.Development,
        };

        private static readonly DesireAxis[] ReconEconomyDevelopmentAxes =
        {
            DesireAxis.Recon, DesireAxis.Economy, DesireAxis.Development,
        };

        private static readonly DesireAxis[] ReconAggressionEconomyDevelopmentAxes =
        {
            DesireAxis.Recon, DesireAxis.Aggression, DesireAxis.Economy, DesireAxis.Development,
        };

        // The desire axes the current mode keeps live. Full keeps all five.
        public static IReadOnlyList<DesireAxis> AxesInScope
        {
            get
            {
                switch (Mode)
                {
                    case AiStrategyV2Mode.ReconOnly: return ReconOnlyAxes;
                    case AiStrategyV2Mode.ReconDevelopment: return ReconDevelopmentAxes;
                    case AiStrategyV2Mode.ReconEconomyDevelopment: return ReconEconomyDevelopmentAxes;
                    case AiStrategyV2Mode.ReconAggressionEconomyDevelopment:
                        return ReconAggressionEconomyDevelopmentAxes;
                    default: return AllAxes;
                }
            }
        }

        // AGG-RAID §2 — the SINGLE mission-kind -> desire-axis mapping table. Intent scope and
        // mission scope both consult it, so a new mission kind can never be admitted by one and
        // silently dropped by the other. Active Defence is folded into Aggression, not a separate
        // axis.
        internal static DesireAxis AxisOf(MissionKind kind)
        {
            switch (kind)
            {
                case MissionKind.Scout: return DesireAxis.Recon;
                case MissionKind.Raid: return DesireAxis.Aggression;
                case MissionKind.Economy: return DesireAxis.Economy;
                default: return DesireAxis.Development;
            }
        }

        internal static bool MissionKindInScope(MissionKind kind) => AxisInScope(AxisOf(kind));

        public static bool AxisInScope(DesireAxis axis) => !IsFocusScoped || AxesInScope.Contains(axis);

        public static RadarAssessment ApplyRadarScope(RadarAssessment assessment)
        {
            if (!IsFocusScoped || assessment == null || assessment.Desires == null)
                return assessment;

            DesireVector desires = assessment.Desires;
            foreach (DesireAxis axis in DesireAxes.All)
                if (!AxisInScope(axis))
                    desires.Raw[axis] = 0f;

            // ReconOnly pins Recon to a full unit so the radar is unambiguously RCN:1. Every focus
            // scope still guarantees a non-empty vector: this prevents Radar.Normalize's generic
            // all-zero fallback from restoring the even five-axis distribution. Recon is in scope
            // for every focus mode.
            if (IsReconOnly || DesireAxes.All.All(a => desires.Raw[a] <= 0f))
                desires.Raw[DesireAxis.Recon] = 1f;

            assessment.Radar = Radar.Normalize(desires);
            AiDebugLog.Write($"[AI][V2][Scope] mode={Mode} radar={assessment.Radar.DebugLine()}");
            return assessment;
        }

        public static List<MissionIntent> ApplyIntentScope(PlayerSetupData player,
            IReadOnlyList<MissionIntent> activeIntents)
        {
            if (!IsFocusScoped)
                return activeIntents?.Where(i => i != null).ToList() ?? new List<MissionIntent>();

            MissionIntentState state = MissionIntentRegistry.GetOrCreate(player);
            foreach (MissionIntent stale in state.All
                .Where(i => i != null && !MissionKindInScope(i.Kind)).ToList())
            {
                state.Remove(stale.IntentKey);
                AiDebugLog.Write($"[AI][V2][Scope] retire {stale.IntentKey} reason={Mode}");
            }

            return (activeIntents ?? new List<MissionIntent>())
                .Where(i => i != null && MissionKindInScope(i.Kind))
                .ToList();
        }

        public static List<AxisDemand> ApplyDemandScope(IEnumerable<AxisDemand> demands)
        {
            List<AxisDemand> all = demands?.Where(d => d != null).ToList() ?? new List<AxisDemand>();
            if (!IsFocusScoped)
                return all;

            int suppressed = all.Count(d => !AxisInScope(d.RequestingAxis));
            if (suppressed > 0)
                AiDebugLog.Write($"[AI][V2][Scope] suppressedDemands={suppressed} reason={Mode}");
            return all.Where(d => AxisInScope(d.RequestingAxis)).ToList();
        }

        public static List<MissionProposal> ApplyMissionScope(IEnumerable<MissionProposal> missions)
        {
            List<MissionProposal> all = missions?.Where(m => m != null).ToList() ?? new List<MissionProposal>();
            if (!IsFocusScoped)
                return all;

            int suppressed = all.Count(m => !MissionKindInScope(m.Kind));
            if (suppressed > 0)
                AiDebugLog.Write($"[AI][V2][Scope] suppressedMissions={suppressed} reason={Mode}");
            return all.Where(m => MissionKindInScope(m.Kind)).ToList();
        }

        // Spec §5/§13 — a focus scope isolates which operational missions execute. It is
        // NOT a hand-management scope: StrategicManager Phase B (UseSurplus) must keep running so
        // every legally playable card is still deployed or drawn regardless of its card type. Card
        // type alone is never a reason a legal card is left in hand.
        public static bool AllowSurplusPreparation => true;

        // Phase B (tempo/UseSurplus) is deliberately NOT scoped by AxisInScope above — it is a
        // hand-management pass, not an operational-mission one (see AllowSurplusPreparation). That
        // is exactly why StrategicPressureAdvance's PressureSpend candidate (an army marching on
        // the enemy Citadel — genuine Aggression, not a card play) could slip through Phase B in a
        // Recon/Economy focus scope even with Aggression desire at zero: Phase B never asked. This
        // is the one Phase B decision that IS an operational-mission choice, so
        // it consults scope directly rather than being carried along by the hand-management
        // exemption. TempoCandidateProvider is the only consumer; StrategicPressureAdvance and
        // Execution take the resulting candidate/plan as given and do not re-interpret scope.
        public static bool AllowStrategicPressure => !IsFocusScoped;

        // AGG-RAID P1#2 — the mission axes with a live, in-turn-re-admittable durable OPERATION
        // (Recon/Scout, Aggression/Raid — Active Defence included, folded into Aggression).
        // Economy and Development are demand-side axes with their own dedicated re-admission gating
        // (AiStrategyV2Pipeline.TakeTypedTriggers' dirtyStrategicAxes loop) and are deliberately NOT
        // part of this list. Add a new operational mission axis here ONCE and every consumer below
        // (and every follow-up Consume call site) picks it up automatically.
        private static readonly DesireAxis[] OperationalMissionAxes =
        {
            DesireAxis.Recon, DesireAxis.Aggression,
        };

        // AGG-RAID P1#2 — the ONE canonical operational invalidation mask, built from every
        // CURRENTLY-ENABLED operational mission axis. Before this, two follow-up re-check call
        // sites in AiStrategyV2Pipeline (the main mid-turn loop and the end-of-turn management
        // round) hardcoded Recon-only, so an Aggression invalidation published mid-loop (e.g. a
        // reinforcement materialization or a combat-power change) was invisible to those specific
        // re-checks — a delay, or an accidental dependency on an unrelated Recon event firing too.
        internal static StrategicInvalidationReason OperationalInvalidationMask
        {
            get
            {
                StrategicInvalidationReason mask = StrategicInvalidationReason.None;
                foreach (DesireAxis axis in OperationalMissionAxes)
                    if (AxisInScope(axis))
                        mask |= DesireAxes.InvalidationMaskFor(axis);
                return mask;
            }
        }
    }
}
