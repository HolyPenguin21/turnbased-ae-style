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
    }

    // The complete air-recon decision for a pass: actors to continue, ready aircraft to send, and
    // concrete launches. No gameplay state is touched building this.
    internal sealed class AirReconPlan
    {
        public readonly List<int> ContinueActorIds = new List<int>(); // airborne, has a ReconPatrolState
        public readonly List<int> ReadyActorIds = new List<int>();    // on own airfield, no task
        public readonly List<AirLaunchPlan> Launches = new List<AirLaunchPlan>();
        public string Summary;

        public bool IsEmpty =>
            ContinueActorIds.Count == 0 && ReadyActorIds.Count == 0 && Launches.Count == 0;
    }

    // ARCH-02 §35 / DoD "Execution не планирует" — the air-recon PLANNER. It does all of what the
    // former ReconAirExecutor.RunFallback did before it ever launched anything: aircraft discovery,
    // actor selection/ordering, ReconMode selection, per-airfield launch-subset selection, the
    // ReconAirStepPlanner.PickFromStorage minimum-useful-step gate and the ReconAirEnergyPolicy
    // check. It produces an AirReconPlan; ReconAirExecutor only flies it.
    internal static class AirReconPlanner
    {
        internal static AirReconPlan Plan(PlayerSetupData player, PlayerRoot root, AiTurnContext ctx,
            WorldSnapshot snapshot)
        {
            var plan = new AirReconPlan();
            if (player == null || root == null || ctx?.Map == null || snapshot?.Self == null)
            {
                plan.Summary = "not reached (missing player/root/map/snapshot)";
                return plan;
            }

            int cap = ReconAirCapacityPolicy.MaxAirReconActorsPerTurn;
            var skips = new List<string>();
            var claimed = new HashSet<int>();
            int Planned() => plan.ContinueActorIds.Count + plan.ReadyActorIds.Count + plan.Launches.Count;

            // 1. airborne aircraft that already own a ReconPatrolState — continue them.
            foreach (ArmyData air in ArmyRegistry.AllForOwner(player)
                         .Where(a => a != null && AviationRules.IsValidAirArmy(a)
                             && a.Controller != null && a.CurrentMovement > 0
                             && !AviationRules.IsOwnedAirfieldAt(a.Hex, player)
                             && ReconPatrolStateRegistry.TryGet(player, a.Id, out _))
                         .OrderBy(a => a.Id))
            {
                if (Planned() >= cap) { skips.Add("actorLimitReached"); break; }
                plan.ContinueActorIds.Add(air.Id);
                claimed.Add(air.Id);
            }

            // 2. ready aircraft sitting on their own airfield with no sortie task.
            if (Planned() < cap)
                foreach (ArmyData air in ArmyRegistry.AllForOwner(player)
                             .Where(a => a != null && !claimed.Contains(a.Id)
                                 && AviationRules.IsValidAirArmy(a) && a.Controller != null && a.CurrentMovement > 0
                                 && AviationRules.IsOwnedAirfieldAt(a.Hex, player)
                                 && AirSortieRegistry.ForArmy(player, a) == null)
                             .OrderBy(a => a.HasActivatedThisTurn ? 0 : 1)
                             .ThenBy(a => a.HasActivatedThisTurn ? 0 : a.ActivationEnergyCost)
                             .ThenBy(a => a.HasActivatedThisTurn ? 0 : a.ActivationApCost)
                             .ThenBy(a => a.Id))
                {
                    if (Planned() >= cap) { skips.Add("actorLimitReached"); break; }
                    plan.ReadyActorIds.Add(air.Id);
                    claimed.Add(air.Id);
                }

            // 3. stored aircraft — one concrete launch per airfield, gated exactly as before.
            ReconMode mode = AirReconModePolicy.RequestedMode(player, snapshot);
            var airfields = AiAirSortiePlanner.OwnedAirfieldHexes(player).ToList();
            if (airfields.Count == 0)
                skips.Add("noOwnedAirfield");
            foreach (HexCoord airfieldHex in airfields)
            {
                if (Planned() >= cap) { skips.Add("actorLimitReached"); break; }
                ArmyData stored = AviationRules.FindAirfieldAt(airfieldHex, player);
                if (stored == null || stored.Members.Count < AiConfig.aviationLaunchMinReadyAircraft)
                {
                    skips.Add(stored == null ? "airfieldEmpty" : "belowMinReadyAircraft");
                    continue;
                }

                List<UnitData> launchSubset = ReconAirCapacityPolicy.SelectReconLaunchSubset(stored.Members);
                if (!AiAirSortiePlanner.CanAffordLaunch(root, player, launchSubset))
                {
                    skips.Add("launchApEnergyUnavailable");
                    continue;
                }

                var launchCandidate = new AirLaunchCandidate(airfieldHex, null, launchSubset);
                ReconAirStepPlanner.StepChoice? first = ReconAirStepPlanner.PickFromStorage(
                    player, ctx, launchCandidate, snapshot, mode, ctx.TurnNumber);
                if (!first.HasValue || first.Value.Score < ReconAirStepPlanner.MinimumUsefulScore)
                {
                    skips.Add("noUsefulRefreshStep");
                    continue;
                }

                int launchEnergy = launchSubset.Sum(u => u != null ? u.LaunchEnergyCost : 0);
                ReconAirEnergyDecision energy = ReconAirEnergyPolicy.Evaluate(player, root, ctx.Map,
                    launchEnergy, first.Value.Score, excludeArmyId: -1);
                if (!energy.Allowed)
                {
                    skips.Add("energyReserveRejectedLaunch");
                    continue;
                }

                plan.Launches.Add(new AirLaunchPlan
                {
                    AirfieldHex = airfieldHex,
                    Subset = launchSubset,
                    Mode = mode,
                    FirstStepHex = first.Value.Hex,
                    LandingHex = first.Value.LandingHex,
                    Score = first.Value.Score,
                    Reason = first.Value.Reason,
                    LaunchEnergy = launchEnergy,
                });
            }

            plan.Summary = $"continue={plan.ContinueActorIds.Count} ready={plan.ReadyActorIds.Count} "
                + $"launches={plan.Launches.Count} cap={cap} "
                + $"skips=[{(skips.Count > 0 ? string.Join(",", skips) : "none")}]";
            return plan;
        }
    }
}
