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
        // RECON-AIR-05/06 — the ProvisionedMission Assignment bound this launch to. Threaded down so
        // the executor can (a) anchor the tactical planner's live replanning at the bound target
        // (RECON-AIR-05) and (b) produce a per-mission ExecutionResult once the real ArmyId exists
        // (RECON-AIR-06), instead of only the pass-wide aggregate.
        public ProvisionedMission Mission;
    }

    internal sealed class AirReconSkippedMission
    {
        public ProvisionedMission Mission;
        public ExecutionStopReason Reason;
    }

    // The complete air-recon decision for a pass: ready aircraft to send, and concrete launches. No
    // gameplay state is touched building this.
    internal sealed class AirReconPlan
    {
        public readonly List<int> ReadyActorIds = new List<int>();    // funded this pass — airfield-idle OR airborne
        public readonly List<AirLaunchPlan> Launches = new List<AirLaunchPlan>();
        // A provisioned air mission must always reach the outcome ledger, even when live plan
        // assembly rejects it before an actor can execute.
        public readonly List<AirReconSkippedMission> SkippedMissions = new List<AirReconSkippedMission>();
        // RECON-AIR-05/06 — the ProvisionedMission a ReadyActorIds entry is bound to (AirExisting).
        public readonly Dictionary<int, ProvisionedMission> ReadyMissionByActorId = new Dictionary<int, ProvisionedMission>();
        public string Summary;

    }

    // ===========================================================================================
    //  ROUND 4/7 — AirReconPlanner is EXECUTION-INPUT ASSEMBLY ONLY. It no longer discovers or
    //  SELECTS which actor/airfield/subset flies (that decision now belongs entirely to
    //  ReconAssignmentPlanner/ProvisioningManager, the SAME single owner Ground already has — see
    //  ReconAssignmentPlanner.AppendAirCandidates / ProvisioningManager.ProvisionAir). This class
    //  turns each air-executed ProvisionedMission (AirExisting / AirLaunch, already bound to a
    //  concrete actor/airfield+subset by Assignment/Provisioning) into the local ReadyActorIds /
    //  AirLaunchPlan shape ReconAirExecutor already consumes — calling ReconAirStepPlanner.
    //  PickFromStorage/Pick here is legitimate LIVE execution-input assembly (re-deriving the
    //  concrete first step fresh against current world state, exactly the same "replan the live
    //  step at execution time" latitude ReconGroundExecutor/ReconAirStepDirector already have —
    //  never a second SELECTION pass).
    //
    //  ROUND 7 (Problem 1) — the "already-airborne wing with a live ReconPatrolState -> keep flying
    //  it" bypass (formerly ContinueActorIds, discovered directly off ArmyRegistry/
    //  ReconPatrolStateRegistry with NO funding check) is REMOVED. An airborne wing is no longer
    //  auto-entitled to continue strategic Recon progress next turn merely because durable state
    //  exists for it: it must win a FRESH ProvisionedMission through the ordinary funded pipeline
    //  (ReconMissionPlanner materialises its ScoutIntent as an incumbent MissionProposal with
    //  PreferredMoverArmyId = this wing, exactly like a continuing ground scout; ReconAssignmentPlanner
    //  gives the incumbent a continuity preference, not a hard entitlement) to appear here as an
    //  ordinary `airProvisioned` entry (ExecutorKind.AirExisting) — it is then indistinguishable from
    //  a freshly-assigned idle wing and takes the SAME ReadyActorIds path below. A wing that does NOT
    //  win fresh funding this pass makes no forward/observation progress this turn — EXCEPT
    //  Mandatory Flight Recovery, a lifecycle/safety obligation independent of Recon funding, which
    //  ReconAirExecutor discovers and flies unconditionally (see Execute's recovery pass).
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
            ReconMode mode = AirReconModePolicy.RequestedMode(player, snapshot);

            // Every air-executed ProvisionedMission this pass — Assignment already picked WHICH
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
                        plan.SkippedMissions.Add(new AirReconSkippedMission
                        {
                            Mission = pm,
                            Reason = ExecutionStopReason.MoverLost,
                        });
                        continue;
                    }
                    plan.ReadyActorIds.Add(wing.Id);
                    plan.ReadyMissionByActorId[wing.Id] = pm;
                    continue;
                }

                if (pm.ExecutorKind != ScoutExecutorKind.AirLaunch)
                    continue;

                ArmyData stored = AviationRules.FindAirfieldAt(pm.AirfieldHex, player);
                if (stored == null || pm.LaunchSubset == null || pm.LaunchSubset.Count == 0)
                {
                    skips.Add("launchSourceGone");
                    plan.SkippedMissions.Add(new AirReconSkippedMission
                    {
                        Mission = pm,
                        Reason = ExecutionStopReason.MoverLost,
                    });
                    continue;
                }
                if (!AiAirSortiePlanner.CanAffordLaunch(root, player, pm.LaunchSubset))
                {
                    skips.Add("launchNoLongerAffordable");
                    plan.SkippedMissions.Add(new AirReconSkippedMission
                    {
                        Mission = pm,
                        Reason = ExecutionStopReason.NoSafeStep,
                    });
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
                    plan.SkippedMissions.Add(new AirReconSkippedMission
                    {
                        Mission = pm,
                        Reason = ExecutionStopReason.NoSafeStep,
                    });
                    continue;
                }

                // No strategic Energy re-evaluation here — Provisioning's AirSortieReservationAdmission
                // already decided this exact sortie is worth reserving. This layer only re-derives the
                // live first step (above) and re-checks hard affordability (CanAffordLaunch, above).
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
                });
            }

            plan.Summary = $"ready={plan.ReadyActorIds.Count} "
                + $"launches={plan.Launches.Count} "
                + $"skips=[{(skips.Count > 0 ? string.Join(",", skips) : "none")}]";
            return plan;
        }
    }
}
