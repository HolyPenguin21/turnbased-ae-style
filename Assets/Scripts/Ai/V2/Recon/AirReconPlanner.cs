using System;
using System.Collections.Generic;
using System.Linq;
using Game.Aviation;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Units;

namespace Game.Ai.V2
{
    // One concrete air-recon launch: which aircraft leave which airfield, in which mode, toward
    // which first hex, with a proven-useful first step and an energy-policy pass already done.
    internal sealed class AirLaunchPlan
    {
        public HexCoord AirfieldHex;
        public List<UnitData> Subset;
        public ReconMode Mode;
        public HexCoord FirstStepHex;
        public HexCoord LandingHex;
        public float Score;
        public string Reason;
        public int LaunchEnergy;
        // RECON-AIR-05/06 — the ProvisionedMission Assignment bound this launch to. Threaded down so
        // the executor can (a) anchor the tactical planner's live replanning at the bound target
        // (RECON-AIR-05) and (b) produce a per-mission ExecutionResult once the real ArmyId exists
        // (RECON-AIR-06), instead of only the pass-wide aggregate.
        public ProvisionedMission Mission;
    }

    // The complete air-recon decision for a pass: actors to continue, ready aircraft to send, and
    // concrete launches. No gameplay state is touched building this.
    internal sealed class AirReconPlan
    {
        public readonly List<int> ContinueActorIds = new List<int>(); // airborne, has a ReconPatrolState
        public readonly List<int> ReadyActorIds = new List<int>();    // on own airfield, no task
        public readonly List<AirLaunchPlan> Launches = new List<AirLaunchPlan>();
        // RECON-AIR-05/06 — the ProvisionedMission a ReadyActorIds entry is bound to (AirExisting).
        // ContinueActorIds carries none — a continuing sortie has no fresh ProvisionedMission this
        // turn (Continuity, not fresh Assignment; see the class header) and its bound target already
        // lives on the durable ReconPatrolState.StrategicAnchor instead.
        public readonly Dictionary<int, ProvisionedMission> ReadyMissionByActorId = new Dictionary<int, ProvisionedMission>();
        public string Summary;

        public bool IsEmpty =>
            ContinueActorIds.Count == 0 && ReadyActorIds.Count == 0 && Launches.Count == 0;
    }

    // ===========================================================================================
    //  ROUND 4 — AirReconPlanner is EXECUTION-INPUT ASSEMBLY ONLY. It no longer discovers or SELECTS
    //  which actor/airfield/subset flies (that decision now belongs entirely to
    //  ReconAssignmentPlanner/ProvisioningManager, the SAME single owner Ground already has — see
    //  ReconAssignmentPlanner.AppendAirCandidates / ProvisioningManager.ProvisionAir). This class:
    //
    //    1. Carries forward already-airborne wings with a live ReconPatrolState (ContinueActorIds) —
    //       untouched by this pass's Assignment; an in-flight sortie is Mission Continuity's concern,
    //       exactly like a ground scout mid-Explore is not re-Assigned every turn either.
    //    2. Turns each air-executed ProvisionedMission (AirExisting / AirLaunch, already bound to a
    //       concrete actor/airfield+subset by Assignment/Provisioning) into the local
    //       ReadyActorIds / AirLaunchPlan shape ReconAirExecutor already consumes — calling
    //       ReconAirStepPlanner.PickFromStorage/Pick here is legitimate LIVE execution-input
    //       assembly (re-deriving the concrete first step fresh against current world state,
    //       exactly the same "replan the live step at execution time" latitude
    //       ReconGroundExecutor/ReconAirStepDirector already have — never a second SELECTION pass).
    // ===========================================================================================
    internal static class AirReconPlanner
    {
        internal static AirReconPlan Plan(PlayerSetupData player, PlayerRoot root, AiTurnContext ctx,
            WorldSnapshot snapshot, IReadOnlyList<ProvisionedMission> airProvisioned)
        {
            var plan = new AirReconPlan();
            if (player == null || root == null || ctx?.Map == null || snapshot?.Self == null)
            {
                plan.Summary = "not reached (missing player/root/map/snapshot)";
                return plan;
            }

            var skips = new List<string>();

            // 1. airborne aircraft that already own a ReconPatrolState — continue them. Untouched by
            //    this pass's Assignment (Mission Continuity, not fresh selection).
            foreach (ArmyData air in ArmyRegistry.AllForOwner(player)
                         .Where(a => a != null && AviationRules.IsValidAirArmy(a)
                             && a.Controller != null && a.CurrentMovement > 0
                             && !AviationRules.IsOwnedAirfieldAt(a.Hex, player)
                             && ReconPatrolStateRegistry.TryGet(player, a.Id, out _))
                         .OrderBy(a => a.Id))
                plan.ContinueActorIds.Add(air.Id);

            ReconMode mode = AirReconModePolicy.RequestedMode(player, snapshot);

            // 2/3. Every air-executed ProvisionedMission this pass — Assignment already picked WHICH
            //      actor (AirExisting) or WHICH airfield+subset (AirLaunch); this is purely turning
            //      that binding into the executor's input shape.
            foreach (ProvisionedMission pm in airProvisioned ?? Array.Empty<ProvisionedMission>())
            {
                if (pm == null || pm.Kind != MissionKind.Scout)
                    continue;

                if (pm.ExecutorKind == ScoutExecutorKind.AirExisting)
                {
                    ArmyData wing = ArmyRegistry.AllForOwner(player)
                        .FirstOrDefault(a => a != null && a.Id == pm.MoverArmyId);
                    if (wing == null || !AviationRules.IsValidAirArmy(wing) || wing.Controller == null
                        || wing.CurrentMovement <= 0)
                    {
                        skips.Add($"readyGone#{pm.MoverArmyId}");
                        continue;
                    }
                    plan.ReadyActorIds.Add(wing.Id);
                    plan.ReadyMissionByActorId[wing.Id] = pm;
                    continue;
                }

                if (pm.ExecutorKind != ScoutExecutorKind.AirLaunch)
                    continue;

                ArmyData stored = AviationRules.FindAirfieldAt(pm.AirfieldHex, player);
                if (stored == null || pm.LaunchSubset == null || pm.LaunchSubset.Count == 0
                    || !AiAirSortiePlanner.CanAffordLaunch(root, player, pm.LaunchSubset))
                {
                    skips.Add("launchNoLongerAffordable");
                    continue;
                }

                var launchCandidate = new AirLaunchCandidate(pm.AirfieldHex, null, pm.LaunchSubset);
                // RECON-AIR-05 — anchor at the bound Refresh target Assignment already committed
                // this launch to, so the live replan happens AROUND that objective, not a fresh one.
                ReconAirStepPlanner.StepChoice? first = ReconAirStepPlanner.PickFromStorage(
                    player, ctx, launchCandidate, snapshot, mode, ctx.TurnNumber,
                    scoringCtx: null, missionFocusHex: pm.FocusHex);
                if (!first.HasValue || first.Value.Score < ReconAirStepPlanner.MinimumUsefulScore)
                {
                    skips.Add("noUsefulRefreshStep");
                    continue;
                }

                int launchEnergy = pm.LaunchSubset.Sum(u => u != null ? u.LaunchEnergyCost : 0);
                ReconAirEnergyDecision energy = ReconAirEnergyPolicy.Evaluate(player, root, ctx.Map,
                    launchEnergy, first.Value.Score, excludeArmyId: -1);
                if (!energy.Allowed)
                {
                    skips.Add("energyReserveRejectedLaunch");
                    continue;
                }

                plan.Launches.Add(new AirLaunchPlan
                {
                    AirfieldHex = pm.AirfieldHex,
                    Subset = pm.LaunchSubset,
                    Mode = mode,
                    FirstStepHex = first.Value.Hex,
                    LandingHex = first.Value.LandingHex,
                    Score = first.Value.Score,
                    Reason = first.Value.Reason,
                    Mission = pm,
                    LaunchEnergy = launchEnergy,
                });
            }

            plan.Summary = $"continue={plan.ContinueActorIds.Count} ready={plan.ReadyActorIds.Count} "
                + $"launches={plan.Launches.Count} "
                + $"skips=[{(skips.Count > 0 ? string.Join(",", skips) : "none")}]";
            return plan;
        }
    }
}
