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
    //   Full             — all five desire axes (Recon / Aggression / Defence / Economy / Development).
    //   ReconOnly        — Recon only. Radar is pinned to RCN:1.
    //   ReconDevelopment — Recon + Development. Aggression / Defence / Economy demand is dropped, so
    //                      BaselineForceReadiness (RequestingAxis = Defence -> FieldCombatPower) can
    //                      no longer materialize an ordinary combat body while an isolated
    //                      Recon+Development run is active. StrategicManager Phase A/B, Development
    //                      generation, attach and draw all stay.
    public enum AiStrategyV2Mode
    {
        Full,
        ReconOnly,
        ReconDevelopment,
        ReconEconomyDevelopment,
    }

    public static class AiStrategyV2Scope
    {
        // Focused production bring-up: Recon -> Economy -> Development/Production support.
        // Aggression/Defence are disabled only here so radar, demand, proposals, continuity and
        // typed admission all observe the same boundary. Phase B and Housekeeping are unaffected.
        public static AiStrategyV2Mode Mode = AiStrategyV2Mode.ReconEconomyDevelopment;

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
            DesireAxis.Recon, DesireAxis.Aggression, DesireAxis.Defence,
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
                    default: return AllAxes;
                }
            }
        }

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

            bool economy = Mode == AiStrategyV2Mode.ReconEconomyDevelopment;
            MissionIntentState state = MissionIntentRegistry.GetOrCreate(player);
            foreach (MissionIntent stale in state.All.Where(i => i != null
                && i.Kind != MissionKind.Scout && !(economy && i.Kind == MissionKind.Economy)).ToList())
            {
                state.Remove(stale.IntentKey);
                AiDebugLog.Write($"[AI][V2][Scope] retire {stale.IntentKey} reason={Mode}");
            }

            return (activeIntents ?? new List<MissionIntent>())
                .Where(i => i != null && (i.Kind == MissionKind.Scout
                    || (economy && i.Kind == MissionKind.Economy)))
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

            bool economy = Mode == AiStrategyV2Mode.ReconEconomyDevelopment;
            int suppressed = all.Count(m => m.Kind != MissionKind.Scout
                && !(economy && m.Kind == MissionKind.Economy));
            if (suppressed > 0)
                AiDebugLog.Write($"[AI][V2][Scope] suppressedMissions={suppressed} reason={Mode}");
            return all.Where(m => m.Kind == MissionKind.Scout
                || (economy && m.Kind == MissionKind.Economy)).ToList();
        }

        // Spec §5/§13 — a focus scope isolates which operational missions execute. It is
        // NOT a hand-management scope: StrategicManager Phase B (UseSurplus) must keep running so
        // every legally playable card is still deployed or drawn regardless of its card type. Card
        // type alone is never a reason a legal card is left in hand.
        public static bool AllowSurplusPreparation => true;
    }
}
