using System;
using System.Collections.Generic;
using System.Linq;
using Game.Combat;
using Game.HexGrid;

namespace Game.Ai.V2
{
    // A local objective of the existing Assault, selected BEFORE funding. It never compares
    // base defenders with main-target defenders: the latter may be completely unknown.
    // No demands, donors, wing binding or separate operation are created for this optional fight.
    internal static class AttackIntermediateBasePolicy
    {
        internal static AttackObjective Select(WorldSnapshot snap, AttackIntent attack,
            IReadOnlyList<AttackObjective> objectives)
        {
            if (attack == null || attack.Phase != AttackMissionPhase.Assault
                || !attack.PrimaryArmyId.HasValue || attack.LastOpportunisticStrikeTurn == snap?.TurnNumber)
                return null;
            ArmySnapshot actor = snap?.Self?.Armies?.FirstOrDefault(a => a != null
                && a.ArmyId == attack.PrimaryArmyId.Value);
            if (actor == null || !actor.IsStructuralRaidActor || actor.CurrentMovement <= 0)
                return null;

            AttackObjective best = null;
            int bestExtra = int.MaxValue, bestContact = int.MaxValue;
            HexPath direct = Route(snap, actor, actor.Hex, attack.Target.Hex);
            foreach (AttackObjective objective in objectives ?? Array.Empty<AttackObjective>())
            {
                if (!EligibleKnowledge(snap, attack.Target, objective))
                    continue;
                HexPath contact = Route(snap, actor, actor.Hex, objective.Hex);
                HexPath onward = Route(snap, actor, objective.Hex, attack.Target.Hex);
                if (!RouteAllows(direct?.TotalCost ?? int.MaxValue,
                        contact?.TotalCost ?? int.MaxValue, onward?.TotalCost ?? int.MaxValue,
                        actor.CurrentMovement, actor.MaxMovement))
                    continue;
                float bonus = AttackObjectiveEvaluator.KnownSiteDefenceBonus(snap, snap.Map, objective.Hex);
                GroundCombatAssemblyPlan plan = GroundCombatAssemblyPlanner.PlanForArmyAtThreshold(
                    snap, objective.Opposition, actor.ArmyId,
                    GroundCombatAdmissionPolicy.FreshStartWinChanceGate, bonus);
                if (!plan.Feasible)
                    continue;
                // Keep an actually started detour stable, but re-prove knowledge, routes and
                // combat on every snapshot. It is freely abandoned if any check stops passing.
                if (attack.IntermediateTarget == objective.Target)
                    return objective;
                int extra = direct == null ? 0 : contact.TotalCost + onward.TotalCost - direct.TotalCost;
                if (best == null || extra < bestExtra || extra == bestExtra
                    && (contact.TotalCost < bestContact || contact.TotalCost == bestContact
                        && CompareIdentity(objective.Target, best.Target) < 0))
                {
                    best = objective; bestExtra = extra; bestContact = contact.TotalCost;
                }
            }
            return best;
        }

        internal static bool EligibleKnowledge(WorldSnapshot snap, AttackTargetRef main, AttackObjective candidate)
        {
            if (snap?.Known == null || candidate == null || candidate.LocationOnly
                || candidate.Target.Kind != AttackTargetKind.Base || candidate.Target == main
                || AttackObjectiveEvaluator.EvaluateTarget(snap, candidate.Target)
                    != AttackObjectiveEvaluator.AttackTargetStatus.Continue)
                return false;
            // A stale empty site is UNKNOWN, not a free capture. Only current observation of
            // both the building and all remembered defending armies may start an optional fight.
            bool observed = (snap.Known.Buildings ?? Array.Empty<AiMapMemory.KnownBuilding>())
                .Any(b => b.Hex.Equals(candidate.Hex) && b.Owner == candidate.Target.ExpectedOwner
                    && b.SeenTurn == snap.TurnNumber);
            return observed && !(snap.Known.EnemySightings ?? Array.Empty<AiMapMemory.KnownEnemySighting>())
                .Any(s => s.Hex.Equals(candidate.Hex) && (s.SeenTurn != snap.TurnNumber
                    || s.MemberCount > 0 && (s.Defenders == null || s.Defenders.Count == 0)));
        }

        // At most one movement turn of extra travel, contact reachable THIS turn, and genuine
        // progress toward the main location. A base sealing the only route can also be cleared.
        internal static bool RouteAllows(int direct, int contact, int onward, int remaining, int maxMove) =>
            contact > 0 && contact != int.MaxValue && onward != int.MaxValue
            && contact <= remaining && maxMove > 0
            && (direct == int.MaxValue || onward < direct
                && (long)contact + onward - direct <= maxMove);

        // Frozen, honest Combat route: known armies and event guards block transit, the chosen
        // endpoint is exempt. No cache or live opponent registry is introduced by this policy.
        internal static HexPath Route(WorldSnapshot snap, ArmySnapshot actor, HexCoord from, HexCoord to)
        {
            if (snap.Map is null)
                return AiV2Util.StraightLine(from, to);
            var blocked = new HashSet<HexCoord>((snap.Known?.EnemySightings
                ?? Array.Empty<AiMapMemory.KnownEnemySighting>()).Select(s => s.Hex));
            foreach (var s in snap.Known?.NeutralSightings ?? Array.Empty<AiMapMemory.KnownEnemySighting>())
                blocked.Add(s.Hex);
            foreach (var h in snap.Known?.EventGuardHexes ?? Array.Empty<HexCoord>()) blocked.Add(h);
            bool Block(HexCoord h) => !h.Equals(to) && !h.Equals(from) && blocked.Contains(h)
                || snap.Map.TryGetTerrainAt(h, out var terrain) && terrain.moveCost > actor.MaxMovement;
            return HexPathfinder.FindPath(snap.Map, from, to, blockHex: Block);
        }

        private static int CompareIdentity(AttackTargetRef a, AttackTargetRef b)
        {
            int c = a.Hex.Q.CompareTo(b.Hex.Q);
            if (c == 0) c = a.Hex.R.CompareTo(b.Hex.R);
            return c == 0 ? a.ExpectedOwnerId.CompareTo(b.ExpectedOwnerId) : c;
        }
    }
}
