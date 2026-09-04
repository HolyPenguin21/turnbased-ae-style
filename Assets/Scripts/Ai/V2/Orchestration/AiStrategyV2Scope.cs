using System.Collections.Generic;
using System.Linq;
using Game.Players;

namespace Game.Ai.V2
{
    // Central execution scope for focused Strategy V2 development/testing.
    // ReconOnly is intentionally enforced at orchestration boundaries rather than by scattered
    // feature flags: radar allocation, durable intents, capability demand, mission admission and
    // surplus preparation all consult the same switch.
    public enum AiStrategyV2Mode
    {
        Full,
        ReconOnly,
    }

    public static class AiStrategyV2Scope
    {
        // AI-MGR-02 — switched to Full: the end-of-turn tempo arbiter must be exercised against the
        // real competing set (AGG / DEF / ECO / DEV spend + reaction reservation), not the narrow
        // ReconOnly slice. Change this one value to isolate a slice again; do not add local
        // "disable aggression" booleans elsewhere.
        public static AiStrategyV2Mode Mode = AiStrategyV2Mode.Full;

        public static bool IsReconOnly => Mode == AiStrategyV2Mode.ReconOnly;

        public static RadarAssessment ApplyRadarScope(RadarAssessment assessment)
        {
            if (!IsReconOnly || assessment == null || assessment.Desires == null)
                return assessment;

            DesireVector desires = assessment.Desires;
            foreach (DesireAxis axis in DesireAxes.All)
                desires.Raw[axis] = axis == DesireAxis.Recon ? 1f : 0f;

            // Deliberately normalize a non-empty vector. This prevents Radar.Normalize's generic
            // all-zero fallback from turning ReconOnly back into the even five-axis distribution.
            assessment.Radar = Radar.Normalize(desires);
            AiDebugLog.Write("[AI][V2][Scope] mode=ReconOnly radar=RCN:1 AGG:0 DEF:0 ECO:0 DEV:0");
            return assessment;
        }

        public static List<MissionIntent> ApplyIntentScope(PlayerSetupData player,
            IReadOnlyList<MissionIntent> activeIntents)
        {
            if (!IsReconOnly)
                return activeIntents?.Where(i => i != null).ToList() ?? new List<MissionIntent>();

            MissionIntentState state = MissionIntentRegistry.GetOrCreate(player);
            foreach (MissionIntent stale in state.All.Where(i => i != null && i.Kind != MissionKind.Scout).ToList())
            {
                state.Remove(stale.IntentKey);
                AiDebugLog.Write($"[AI][V2][Scope] retire {stale.IntentKey} reason=ReconOnly");
            }

            return (activeIntents ?? new List<MissionIntent>())
                .Where(i => i != null && i.Kind == MissionKind.Scout)
                .ToList();
        }

        public static List<AxisDemand> ApplyDemandScope(IEnumerable<AxisDemand> demands)
        {
            List<AxisDemand> all = demands?.Where(d => d != null).ToList() ?? new List<AxisDemand>();
            if (!IsReconOnly)
                return all;

            int suppressed = all.Count(d => d.RequestingAxis != DesireAxis.Recon);
            if (suppressed > 0)
                AiDebugLog.Write($"[AI][V2][Scope] suppressedDemands={suppressed} reason=ReconOnly");
            return all.Where(d => d.RequestingAxis == DesireAxis.Recon).ToList();
        }

        public static List<MissionProposal> ApplyMissionScope(IEnumerable<MissionProposal> missions)
        {
            List<MissionProposal> all = missions?.Where(m => m != null).ToList() ?? new List<MissionProposal>();
            if (!IsReconOnly)
                return all;

            int suppressed = all.Count(m => m.Kind != MissionKind.Scout);
            if (suppressed > 0)
                AiDebugLog.Write($"[AI][V2][Scope] suppressedMissions={suppressed} reason=ReconOnly");
            return all.Where(m => m.Kind == MissionKind.Scout).ToList();
        }

        // Spec §5/§13 — ReconOnly isolates which operational MISSIONS execute (Recon only). It is
        // NOT a hand-management scope: StrategicManager Phase B (UseSurplus) must keep running so
        // every legally playable card is still deployed or drawn regardless of its card type. Card
        // type alone is never a reason a legal card is left in hand in ReconOnly.
        public static bool AllowSurplusPreparation => true;
    }
}
