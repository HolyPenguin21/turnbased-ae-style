using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Game.Combat;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Units;
using UnityEngine;

namespace Game.Ai.V2
{
    internal static class AttackPreparationPolicy
    {
        // The own Base a mobilization preparation assembles on: of every held own Base
        // (SelfSnapshot.BaseHexes, starting Citadel included) the one nearest to the target; ties
        // go to the starting Citadel, then Q, R. Null when no own Base is held. Recomputed every
        // pass, so a lost Base simply moves the staging point. With a `host` whose route facts
        // Analysis measured (a structural field army, ArmySnapshot.ReachableOwnBaseHexes) only a
        // Base it can really reach qualifies — an unreachable staging point would otherwise hold
        // the preparation in WAIT forever.
        internal static HexCoord? PreparationStagingBase(WorldSnapshot snap, HexCoord targetHex,
            ArmySnapshot host = null)
        {
            IReadOnlyList<HexCoord> bases = snap?.Self?.BaseHexes;
            if (host != null && host.IsStructuralRaidActor && bases != null)
                bases = bases.Where(h => h.Equals(host.Hex)
                    || (host.ReachableOwnBaseHexes != null && host.ReachableOwnBaseHexes.Contains(h)))
                    .ToList();
            if (bases == null || bases.Count == 0)
                return null;
            HexCoord citadel = snap.Self.Citadel;
            return bases
                .OrderBy(h => HexGridMath.Distance(h, targetHex))
                .ThenByDescending(h => h.Equals(citadel))
                .ThenBy(h => h.Q).ThenBy(h => h.R)
                .First();
        }
        internal static (ArmyData garrison, UnitData hero, float power) FindCommanderUpgrade(
            WorldSnapshot snap, ArmySnapshot host, out float currentPower, out int currentCommand, out bool assessable)
        {
            assessable = false;
            currentPower = 0f;
            currentCommand = 0;
            PlayerSetupData player = snap.Observer;
            ArmyData liveHost = player == null ? null : AiV2Util.ResolveArmy(player, host.ArmyId);
            if (liveHost == null || snap.Self.StrikePool == null || snap.Self.StrikePool.Count == 0)
                return default;

            assessable = true;
            UnitData current = liveHost.Commander;
            StrikeRoster.ComposeUnder(snap.Self.StrikePool,
                current == null ? (StrikeRosterCandidate?)null : StrikeRoster.CommanderCandidate(current),
                liveHost.Capacity, out currentPower);
            if (AttackForceReadiness.ForceReady(currentPower, snap.Self.AttackPeak))
                return default;
            currentCommand = current?.CommandRating ?? 0;
            (ArmyData garrison, UnitData hero, float power) best = (null, null, currentPower);
            foreach (ArmyData g in ArmyRegistry.AllForOwner(player))
            {
                if (g == null || !g.IsGarrison || g.Hex.Equals(liveHost.Hex))
                    continue;
                foreach (UnitData u in g.Members)
                {
                    if (u == null || !u.IsHero || u.IsPrisoner || u.CommandRating <= currentCommand
                        || AiArmyRoles.IsGarrisonHero(u) || AiArmyRoles.IsFacilityOperator(player, g.Hex, u)
                        || !AiArmyRoles.CanSpareGarrisonMember(player, g, u))
                        continue;
                    StrikeRoster.ComposeUnder(snap.Self.StrikePool, StrikeRoster.CommanderCandidate(u),
                        u.CommandRating, out float power);
                    if (power > best.power + AiConfigV2.allocatorSliceEpsilon
                        || (best.hero != null && System.Math.Abs(power - best.power) <= AiConfigV2.allocatorSliceEpsilon
                            && HexGridMath.Distance(g.Hex, liveHost.Hex) < HexGridMath.Distance(best.garrison.Hex, liveHost.Hex)))
                        best = (g, u, power);
                }
            }
            return best;
        }

        // Another OBSERVED hostile structure (never a location-only guess) the host passes the
        // attack coverage / win-chance gate against right now, in the evaluator's own priority
        // order. Snapshot-pure: remembered opposition and structural defence only.
        internal static bool ClearedAlternativeTarget(WorldSnapshot snap, AttackTargetRef target, int hostId,
            out string label)
        {
            label = null;
            foreach (AttackObjective objective in AttackObjectiveEvaluator.Enumerate(snap))
            {
                if (objective == null)
                    continue;
                // The evaluator's order is what the fresh pick will follow: only a target that
                // ranks AHEAD of the current one can take over (otherwise the same one returns).
                if (objective.Target.Hex.Equals(target.Hex))
                    return false;
                if (AttackObjectiveEvaluator.IsLocationOnly(snap, objective.Target)
                    || objective.Opposition == null || objective.Opposition.Count == 0)
                    continue;
                GroundCombatAssemblyPlan plan = GroundCombatAssemblyPlanner.PlanForArmyAtThreshold(
                    snap, objective.Opposition, hostId, GroundCombatAdmissionPolicy.AttackCoverageGate,
                    objective.HexDefense);
                if (!plan.Feasible || !plan.CoversAllDefenders)
                    continue;
                label = $"{objective.Target} (win {plan.ProjectedWinChance:0.00})";
                return true;
            }
            return false;
        }
    }
}
