using System.Collections.Generic;
using System.Linq;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using UnityEngine;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  MISSION REVALIDATOR  (Strategy V2 — live mission revalidation between provisioned missions)
    // ===========================================================================================
    //  Provisioning is a batch; this is the live gate before every mission. Generic Refresh is
    //  explicitly distinct from Explore: a previously VISITED hex can still be a valid stale-info
    //  objective, and only observing it again completes that Refresh.
    // ===========================================================================================
    internal enum MissionValidity
    {
        Valid,
        StaleGoalMet,
        StaleTargetInvalidated,
        StaleMoverLost,
        StaleUnaffordable,
    }

    internal static class MissionRevalidator
    {
        public static bool IsStale(MissionValidity v) => v != MissionValidity.Valid;

        public static MissionValidity Validate(PlayerSetupData player, PlayerRoot root, AiTurnContext ctx,
            ProvisionedMission pm)
        {
            if (pm == null)
                return MissionValidity.StaleMoverLost;

            ArmyData mover = ArmyRegistry.AllForOwner(player).FirstOrDefault(a => a.Id == pm.MoverArmyId);
            if (mover == null || mover.Owner != player)
                return MissionValidity.StaleMoverLost;

            if (pm.Kind == MissionKind.Scout && !AiArmyRoles.IsSoloRecce(mover))
                return MissionValidity.StaleMoverLost;

            if (root != null && !mover.HasActivatedThisTurn && mover.ActivationApCost > 0
                && root.ActionPoints < mover.ActivationApCost)
                return MissionValidity.StaleUnaffordable;

            if (pm.Kind == MissionKind.Raid)
            {
                if (RaidObjectiveEvaluator.IsObjectiveSatisfiedLive(player, pm.RaidTargetArmyId))
                    return MissionValidity.StaleGoalMet;
                return MissionValidity.Valid;
            }

            if (ReconScoutKinds.IsSurveil(pm.ScoutKind))
            {
                if (ScoutObjectiveEvaluator.IsSurveilSatisfiedLive(player, pm.FocusHex, pm.TrackedArmyId,
                        pm.BaselineObservedTurn))
                    return MissionValidity.StaleGoalMet;
                if (ctx != null && ScoutExecutionSafety.VantageBlockedNow(player, pm.ExecutionHex, ctx.TurnNumber))
                    return MissionValidity.StaleTargetInvalidated;
                return MissionValidity.Valid;
            }

            if (ReconScoutKinds.IsRefresh(pm.ScoutKind))
            {
                if (ScoutObjectiveEvaluator.IsRefreshSatisfiedLive(player, pm.FocusHex))
                    return MissionValidity.StaleGoalMet;
                if (AiMapMemory.KnownEnemySightingAt(player, pm.ExecutionHex).HasValue)
                    return MissionValidity.StaleTargetInvalidated;
                return MissionValidity.Valid;
            }

            // Never silently reinterpret a future/invalid Scout kind as Explore. The enum has three
            // explicit semantics and every lifecycle stage must reject values it does not understand.
            if (!ReconScoutKinds.IsExplore(pm.ScoutKind))
            {
                AiDebugLog.Write($"[AI][V2][Recon] revalidate reject — unknown Scout kind {(int)pm.ScoutKind}");
                return MissionValidity.StaleTargetInvalidated;
            }

            // Explore only. Physical visitation is completion here; generic Refresh intentionally
            // does NOT share this shortcut.
            if (VisionSystem.IsVisited(player, pm.ExecutionHex))
                return MissionValidity.StaleGoalMet;
            if (AiMapMemory.KnownEnemySightingAt(player, pm.ExecutionHex).HasValue)
                return MissionValidity.StaleTargetInvalidated;
            return MissionValidity.Valid;
        }

        public static HashSet<HexCoord> CollectExploreProposalFoci(IEnumerable<MissionProposal> missions)
        {
            var foci = new HashSet<HexCoord>();
            if (missions == null)
                return foci;
            foreach (MissionProposal m in missions)
                if (m?.Kind == MissionKind.Scout && m.Target is ScoutMissionTarget smt
                    && ReconScoutKinds.IsExplore(smt.Kind))
                    foci.Add(smt.FocusHex);
            return foci;
        }

        public static bool TryPickReplacementExploreFocus(WorldSnapshot snapshot, PlayerSetupData player,
            ProvisionedMission pm, HexCoord from, ISet<HexCoord> takenFoci, out HexCoord focus)
        {
            focus = default;
            if (snapshot?.MapKnowledge?.Frontier == null || pm == null || pm.IsReplacement
                || pm.Kind != MissionKind.Scout || !ReconScoutKinds.IsExplore(pm.ScoutKind))
                return false;

            FrontierHexSnapshot? best = null;
            foreach (FrontierHexSnapshot f in snapshot.MapKnowledge.Frontier)
            {
                if (VisionSystem.IsVisited(player, f.Hex) || f.Hex.Equals(pm.ExecutionHex))
                    continue;
                if (takenFoci != null && takenFoci.Contains(f.Hex))
                    continue;
                if (AiMapMemory.KnownEnemySightingAt(player, f.Hex).HasValue)
                    continue;
                if (best == null || Better(f, best.Value, from))
                    best = f;
            }
            if (best == null)
                return false;
            focus = best.Value.Hex;
            return true;
        }

        public static ProvisionedMission BuildExploreReplacement(ProvisionedMission stale, HexCoord newFocus,
            PlayerSetupData player = null)
        {
            var target = new ScoutMissionTarget
            {
                FocusHex = newFocus,
                Kind = ScoutTargetKind.Explore,
                Stealth = StealthRequirement.None,
                DetectionRisk = 0f,
            };
            var proposal = new MissionProposal
            {
                Kind = MissionKind.Scout,
                Target = target,
                BaseValue = stale?.Mission?.BaseValue ?? 0f,
                Explain = "live replacement for a stale Explore focus",
                PreferredMoverArmyId = stale?.MoverArmyId,
                AttemptId = AiV2Trace.CurrentScope(player)?.NextMissionAttemptId(),
                ReplacementOfAttemptId = stale?.Mission?.AttemptId,
            };
            return new ProvisionedMission
            {
                Mission = proposal,
                Key = new StableMissionKey(MissionKind.Scout, (int)ScoutTargetKind.Explore, 0,
                    newFocus.Q, newFocus.R),
                Kind = MissionKind.Scout,
                ScoutKind = ScoutTargetKind.Explore,
                MoverArmyId = stale?.MoverArmyId ?? 0,
                FocusHex = newFocus,
                ExecutionHex = newFocus,
                TrackedArmyId = null,
                BaselineObservedTurn = 0,
                ClaimedPhysical = stale?.ClaimedPhysical ?? default,
                ClaimedAp = stale?.ClaimedAp ?? 0f,
                StealthApReserved = false,
                IsReplacement = true,
            };
        }

        private static bool Better(FrontierHexSnapshot a, FrontierHexSnapshot b, HexCoord from)
        {
            int da = HexGridMath.Distance(from, a.Hex);
            int db = HexGridMath.Distance(from, b.Hex);
            if (da != db) return da < db;
            if (a.FreshNeighbors != b.FreshNeighbors) return a.FreshNeighbors > b.FreshNeighbors;
            if (a.Hex.Q != b.Hex.Q) return a.Hex.Q < b.Hex.Q;
            return a.Hex.R < b.Hex.R;
        }

        public static bool WasAttempt(ExecutionResult r) => r != null && !r.Replaced;

        public static bool WasGenuineExecution(ExecutionResult r) =>
            r != null && !r.Replaced && r.ReachedGoal && (r.StepsMoved > 0 || r.ApSpent > Mathf.Epsilon);

        public static bool WasStaleOrSkipped(ExecutionResult r) =>
            r != null && r.StepsMoved == 0 && r.ApSpent <= Mathf.Epsilon;

        public static bool WasReplacement(ExecutionResult r) => r != null && r.IsReplacement;
    }
}
