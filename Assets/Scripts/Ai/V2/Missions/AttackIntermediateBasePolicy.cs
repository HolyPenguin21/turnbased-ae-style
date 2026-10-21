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
            return SelectOnRoute(snap, attack, actor, attack.Target.Hex, objectives,
                GroundCombatAdmissionPolicy.AttackIntermediateBaseWinChanceGate,
                // The roster Missions and provisioning will field for this Assault.
                preserveDepartureRoster: attack.AssaultStarted, accept: null);
        }

        // 2026-10-21 (playtest Vex T17) — a preparation host on its walk to the staging Base
        // passes an enemy Base it takes decisively: Rustborn (21.4) walked (2,1)->(1,1)->(1,0)
        // beside Mordak's new Base (2,0) defended by 9.7 and never touched it (the one live
        // operation held every other objective). The same optional local fight as a marching
        // Assault's, toward the staging Base instead of the main target, at the fresh decisive
        // gate (the host is still below the fist bar) and only where the march start itself is
        // allowed (AttackMarchPolicy.CanStart: home not in danger, no retreat-worthy contact).
        // The leg the planner builds from it (AppendAttack) is an intermediate Assault of the
        // preparation; Continuity keeps the operation a preparation (ApplyAttackStepFacts).
        internal static AttackObjective SelectForPreparationWalk(WorldSnapshot snap, AttackIntent attack,
            IReadOnlyList<AttackObjective> objectives)
        {
            // A support or commander walking to the host pins it (GroundCombatLegs.PinsActors):
            // the detour waits until no other leg of the operation holds the host.
            if (attack == null || !attack.Preparation || attack.Phase != AttackMissionPhase.Gather
                || !attack.PrimaryArmyId.HasValue || attack.LastOpportunisticStrikeTurn == snap?.TurnNumber
                || attack.GatherSupportArmyIds.Count > 0 || attack.CommanderArmyId.HasValue)
                return null;
            ArmySnapshot host = snap?.Self?.Armies?.FirstOrDefault(a => a != null
                && a.ArmyId == attack.PrimaryArmyId.Value);
            if (host == null || !host.IsStructuralRaidActor || host.CurrentMovement <= 0
                || ActiveDefenceObjectiveEvaluator.IsPinnedStrongholdDefender(snap, host))
                return null;
            HexCoord? staging = AttackPreparationPolicy.KeptStagingBase(snap, attack, host);
            if (!staging.HasValue || host.Hex.Equals(staging.Value))
                return null;
            return SelectOnRoute(snap, attack, host, staging.Value, objectives,
                PreparationWalkWinChanceGate, preserveDepartureRoster: true,
                accept: plan => AttackMarchPolicy.CanStart(snap, attack.Target, plan, snap.Intents, out _));
        }

        // The preparation detour's gate: the fresh decisive confidence (raidMinViableWinChance),
        // the bar attack-by-opportunity uses for a force below the fist floor.
        internal static float PreparationWalkWinChanceGate => GroundCombatAdmissionPolicy.FreshStartWinChanceGate;

        private static AttackObjective SelectOnRoute(WorldSnapshot snap, AttackIntent attack,
            ArmySnapshot actor, HexCoord destination, IReadOnlyList<AttackObjective> objectives,
            float gate, bool preserveDepartureRoster, Func<GroundCombatAssemblyPlan, bool> accept)
        {
            AttackObjective best = null;
            int bestExtra = int.MaxValue, bestContact = int.MaxValue;
            HexPath direct = Route(snap, actor, actor.Hex, destination);
            // 2026-10-21 — a Base another live Attack operation (the second one, AttackSecondaryPolicy)
            // is taking is served: never two operations on one site.
            var otherTargets = new HashSet<AttackTargetRef>((snap.Intents?.All ?? Array.Empty<MissionIntent>())
                .Where(i => i != null && i.Status == IntentStatus.Active && i.Attack != null
                    && !ReferenceEquals(i.Attack, attack) && i.Attack.Phase != AttackMissionPhase.RecoveryReturn)
                .Select(i => i.Attack.Target));
            foreach (AttackObjective objective in objectives ?? Array.Empty<AttackObjective>())
            {
                if (!EligibleKnowledge(snap, attack.Target, objective) || otherTargets.Contains(objective.Target))
                    continue;
                HexPath contact = Route(snap, actor, actor.Hex, objective.Hex);
                HexPath onward = Route(snap, actor, objective.Hex, destination);
                if (!RouteAllows(direct?.TotalCost ?? int.MaxValue,
                        contact?.TotalCost ?? int.MaxValue, onward?.TotalCost ?? int.MaxValue,
                        actor.CurrentMovement, actor.MaxMovement))
                    continue;
                float bonus = AttackObjectiveEvaluator.KnownSiteDefenceBonus(snap, snap.Map, objective.Hex);
                GroundCombatAssemblyPlan plan = GroundCombatAssemblyPlanner.PlanForArmyAtThreshold(
                    snap, objective.Opposition, actor.ArmyId, gate, bonus,
                    GroundCombatAdmissionPolicy.AttackIntermediateBaseRequiresCoverage,
                    preserveDepartureRoster: preserveDepartureRoster);
                if (!plan.Feasible || accept != null && !accept(plan))
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

        // The optional Base's route is the ONE Combat route (SafeStepPathing): remembered armies and
        // event guards block transit, the chosen endpoint is exempt, terrain above MaxMovement is
        // impassable. No private blocker list of its own (it used to differ from the executor's
        // route). A snapshot without a map (synthetic fixture) falls back to a straight line.
        internal static HexPath Route(WorldSnapshot snap, ArmySnapshot actor, HexCoord from, HexCoord to)
        {
            if (snap.Map is null)
                return AiV2Util.StraightLine(from, to);
            return SafeStepPathing.FindSafePath(snap.Map, actor.Owner, from, to, actor.MaxMovement,
                SafeRouteProfile.Combat);
        }

        private static int CompareIdentity(AttackTargetRef a, AttackTargetRef b)
        {
            int c = a.Hex.Q.CompareTo(b.Hex.Q);
            if (c == 0) c = a.Hex.R.CompareTo(b.Hex.R);
            return c == 0 ? a.ExpectedOwnerId.CompareTo(b.ExpectedOwnerId) : c;
        }
    }
}
