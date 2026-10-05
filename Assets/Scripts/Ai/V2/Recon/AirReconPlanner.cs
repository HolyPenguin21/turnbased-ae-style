using System;
using System.Collections.Generic;
using System.Linq;
using Game.Aviation;
using Game.Map;
using Game.Players;

namespace Game.Ai.V2
{
    internal sealed class AirReconSkippedMission
    {
        public ProvisionedMission Mission;
        public ExecutionStopReason Reason;
    }

    // Execution input for funded air Recon. Pending storage preparations retain the exact
    // source/aircraft binding; the executor materializes them before issuing their first step.
    internal sealed class AirReconPlan
    {
        public readonly HashSet<int> ReservedActorIds = new HashSet<int>();
        public readonly HashSet<int> MaterializedActorIds = new HashSet<int>();
        public readonly List<ProvisionedMission> StoredMissions = new List<ProvisionedMission>();
        public readonly List<int> ReadyActorIds = new List<int>();
        public readonly List<AirReconSkippedMission> SkippedMissions = new List<AirReconSkippedMission>();
        public readonly Dictionary<int, ProvisionedMission> ReadyMissionByActorId =
            new Dictionary<int, ProvisionedMission>();
        public string Summary;
    }

    internal static class AirReconPlanner
    {
        internal static AirReconPlan Plan(PlayerSetupData player, PlayerRoot root, AiTurnContext ctx,
            WorldSnapshot snapshot, IReadOnlyList<ProvisionedMission> airProvisioned,
            IEnumerable<int> reservedActorIds = null)
        {
            var plan = new AirReconPlan();
            plan.ReservedActorIds.UnionWith(reservedActorIds ?? Array.Empty<int>());
            if (player == null || root == null || ctx?.Map == null || snapshot?.Self == null)
            {
                plan.Summary = "not reached (missing player/root/map/snapshot)";
                return plan;
            }

            var skips = new List<string>();
            foreach (ProvisionedMission pm in airProvisioned ?? Array.Empty<ProvisionedMission>())
            {
                if (pm == null || pm.Kind != MissionKind.Scout)
                    continue;

                if (pm.ExecutorKind == ScoutExecutorKind.AirStored)
                {
                    plan.StoredMissions.Add(pm);
                    continue;
                }
                if (pm.ExecutorKind != ScoutExecutorKind.AirExisting)
                {
                    skips.Add("nonExistingAirActor");
                    plan.SkippedMissions.Add(new AirReconSkippedMission
                    {
                        Mission = pm,
                        Reason = ExecutionStopReason.TargetInvalidated,
                    });
                    continue;
                }

                ArmyData wing = AiV2Util.ResolveArmy(player, pm.MoverArmyId);
                if (wing == null || !AviationRules.IsValidAirArmy(wing) || wing.Controller == null
                    || wing.CurrentMovement <= 0)
                {
                    skips.Add($"readyGone#{pm.MoverArmyId}");
                    plan.SkippedMissions.Add(new AirReconSkippedMission
                    {
                        Mission = pm,
                        Reason = ExecutionStopReason.MoverLost,
                    });
                    continue;
                }

                plan.ReadyActorIds.Add(wing.Id);
                plan.ReadyMissionByActorId[wing.Id] = pm;
            }

            plan.Summary = $"ready={plan.ReadyActorIds.Count} "
                + $"skips=[{(skips.Count > 0 ? string.Join(",", skips) : "none")}]";
            return plan;
        }
    }
}
