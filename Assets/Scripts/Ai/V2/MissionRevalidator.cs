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
    //  Provisioning is a BATCH: every funded mission is sized against ONE beginning-of-execution
    //  snapshot, then TaskExecutor runs them one after another. After mission N-1 runs, the world
    //  has moved — a hex is captured, a target destroyed, an army relocated, AP drained, a focus
    //  already visited by an earlier scout. Mission N still carries the stale snapshot's plan.
    //
    //  This is the SYSTEMIC gate: one pure, live-world check applied to EVERY provisioned mission
    //  immediately before it is activated, of any kind. A stale mission spends no AP, plays no
    //  card, is never counted a successful execution, and never emits success telemetry.
    //
    //  REPLACEMENT is deliberately narrow and bounded: a stale Explore whose mover is still a
    //  ready solo scout is RE-POINTED, ONCE, at the nearest still-unvisited frontier hex that the
    //  turn's own WorldSnapshot already enumerated. No re-run of WorldAnalysis / Strategy / Demand
    //  / Allocation / Provisioning — only validity / target / eligibility are recomputed, against
    //  data that already exists. No replacement loop: one attempt per mission, then it completes
    //  stale.
    // ===========================================================================================
    internal enum MissionValidity
    {
        Valid,
        StaleGoalMet,          // the objective is already satisfied (focus visited / target gone / surveil met)
        StaleTargetInvalidated,// the world changed under the mission (enemy now on the focus, vantage blocked)
        StaleMoverLost,        // the assigned mover is gone / no longer this player's / no longer the right shape
        StaleUnaffordable,     // an earlier mission drained the shared AP pool below this mission's activation cost
    }

    internal static class MissionRevalidator
    {
        public static bool IsStale(MissionValidity v) => v != MissionValidity.Valid;

        // Pure live-world read. Never mutates game state, the snapshot, or the mission.
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

            // Shared AP pool: earlier missions in the batch spend real AP. A mover that still has to
            // activate but can no longer pay for it is stale for this turn.
            if (root != null && !mover.HasActivatedThisTurn && mover.ActivationApCost > 0
                && root.ActionPoints < mover.ActivationApCost)
                return MissionValidity.StaleUnaffordable;

            if (pm.Kind == MissionKind.Raid)
            {
                if (RaidObjectiveEvaluator.IsObjectiveSatisfiedLive(player, pm.RaidTargetArmyId))
                    return MissionValidity.StaleGoalMet;
                return MissionValidity.Valid;
            }

            if (pm.ScoutKind == ScoutTargetKind.Surveil)
            {
                if (ScoutObjectiveEvaluator.IsSurveilSatisfiedLive(player, pm.FocusHex, pm.TrackedArmyId,
                        pm.BaselineObservedTurn))
                    return MissionValidity.StaleGoalMet;
                if (ctx != null && ScoutExecutionSafety.VantageBlockedNow(player, pm.ExecutionHex, ctx.TurnNumber))
                    return MissionValidity.StaleTargetInvalidated;
                return MissionValidity.Valid;
            }

            // Explore.
            if (VisionSystem.IsVisited(player, pm.ExecutionHex))
                return MissionValidity.StaleGoalMet;
            if (AiMapMemory.KnownEnemySightingAt(player, pm.ExecutionHex).HasValue)
                return MissionValidity.StaleTargetInvalidated;
            return MissionValidity.Valid;
        }

        // Bounded, deterministic replacement for a stale Explore. Returns a still-unvisited frontier
        // hex from the turn's own snapshot the mover could be re-pointed at, or null. Never re-plans.
        // A mission that is ITSELF a replacement never gets replaced again (one hop, bounded).
        public static bool TryPickReplacementExploreFocus(WorldSnapshot snapshot, PlayerSetupData player,
            ProvisionedMission pm, out HexCoord focus)
        {
            focus = default;
            if (snapshot?.MapKnowledge?.Frontier == null || pm == null || pm.IsReplacement
                || pm.Kind != MissionKind.Scout || pm.ScoutKind != ScoutTargetKind.Explore)
                return false;

            HexCoord from = pm.ExecutionHex;
            FrontierHexSnapshot? best = null;
            foreach (FrontierHexSnapshot f in snapshot.MapKnowledge.Frontier)
            {
                if (VisionSystem.IsVisited(player, f.Hex) || f.Hex.Equals(pm.ExecutionHex))
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

        // Synthesise a NEW mission for `newFocus` off the stale one's mover. The result has its OWN
        // fresh StableMissionKey and its OWN ScoutMissionTarget — it never carries the superseded
        // mission's identity (spec §5). It reuses only the physical mover + AP claim (already
        // reserved for that mover this turn). Deterministic: `newFocus` came from a totally-ordered
        // frontier pick.
        public static ProvisionedMission BuildExploreReplacement(ProvisionedMission stale, HexCoord newFocus)
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
                StealthApReserved = false,   // a different route — never inherit a stealth reserve
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

        // --- ExecutionResult classification for turn-activity telemetry. The `executed` list is
        //     the SINGLE source of truth; every counter is DERIVED from it once in the caller,
        //     never incremented inside TaskExecutor (spec §11 — no double counting).
        //  Attempt          : a mission the executor actually ran (not a superseded stale one).
        //  Genuine success  : the goal was reached AND real work was done (moved, or spent AP).
        //  Stale / skipped  : nothing happened at all — 0 steps and 0 AP — whether flagged a goal
        //                     (already met before start), invalidated, or superseded by a
        //                     replacement.
        //  Replacement      : the synthesised replacement mission (its own fresh key).
        public static bool WasAttempt(ExecutionResult r) => r != null && !r.Replaced;

        public static bool WasGenuineExecution(ExecutionResult r) =>
            r != null && !r.Replaced && r.ReachedGoal && (r.StepsMoved > 0 || r.ApSpent > Mathf.Epsilon);

        public static bool WasStaleOrSkipped(ExecutionResult r) =>
            r != null && r.StepsMoved == 0 && r.ApSpent <= Mathf.Epsilon;

        public static bool WasReplacement(ExecutionResult r) => r != null && r.IsReplacement;
    }
}
