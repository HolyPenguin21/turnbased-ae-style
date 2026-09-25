using System.Collections.Generic;
using Game.HexGrid;
using UnityEngine;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  S4 — THE ONE ANSWER TO "WHICH ARMIES DOES THIS GROUND-COMBAT LEG OCCUPY".
    //
    //  Raid, Attack and ActiveDefence share one leg vocabulary: a fresh actor-contention leg
    //  (Assault / Intercept, plus an UNPINNED Reinforcement that still needs a support army) and
    //  lifecycle legs whose actors Continuity already pinned (convoys, gathers, walks home). The
    //  batch assignment (ProvisioningManager), the exclusion view (ProvisioningSession), durable
    //  occupancy (ActorCommitments), the admission fingerprints and AdvanceIntent all read these
    //  rules from here instead of each re-listing the phases of each lane.
    // ===========================================================================================
    internal static class GroundCombatLegs
    {
        // Does this leg carry Continuity-pinned actors that every OTHER ground-combat proposal in
        // the same batch solve must treat as taken? `primary` / `support` are those actors.
        // Raid: every non-Assault leg (its AirSupport aircraft is deliberately not a ground pin).
        // Attack: every non-Assault leg. ActiveDefence: the Return leg's primary.
        internal static bool PinsActors(MissionProposal mission, out int? primary, out int? support)
        {
            primary = null;
            support = null;
            if (mission?.Target is ActiveDefenceMissionTarget ad)
            {
                if (ad.Phase != ActiveDefencePhase.Return || !ad.PrimaryArmyId.HasValue)
                    return false;
                primary = ad.PrimaryArmyId;
                return true;
            }
            if (mission?.Kind == MissionKind.Attack && mission.Target is AttackMissionTarget at)
            {
                if (at.Phase == AttackMissionPhase.Assault)
                    return false;
                primary = at.PrimaryArmyId;
                support = at.SupportArmyId;
                return true;
            }
            if (mission?.Kind == MissionKind.Raid && mission.Target is RaidMissionTarget rt)
            {
                if (rt.Phase == RaidMissionPhase.Assault)
                    return false;
                primary = rt.PrimaryArmyId;
                support = rt.SupportArmyId;
                return true;
            }
            return false;
        }

        // Is this leg a fresh actor-contention decision for the ONE batch assignment solve?
        // Assault / Intercept always; a Reinforcement leg only while no support army is bound yet.
        internal static bool JoinsAssignmentSolve(MissionProposal mission)
        {
            if (mission?.Target is RaidMissionTarget rt)
                return rt.Phase == RaidMissionPhase.Assault
                    || (rt.Phase == RaidMissionPhase.Reinforcement && !rt.SupportArmyId.HasValue);
            if (mission?.Target is ActiveDefenceMissionTarget ad)
                return ad.Phase != ActiveDefencePhase.Return;
            if (mission?.Target is AttackMissionTarget at)
                return at.Phase == AttackMissionPhase.Assault
                    || (at.Phase == AttackMissionPhase.Reinforcement && !at.SupportArmyId.HasValue);
            return false;
        }

        // The pinned set is computed across ALL funded lifecycle legs, including the leg being
        // provisioned; the actor THIS leg moves must stay permitted to it.
        internal static bool IsOwnLegActor(MissionProposal mission, int armyId)
        {
            if (mission?.Target is RaidMissionTarget raid)
                return ((raid.Phase == RaidMissionPhase.Reinforcement
                            || raid.Phase == RaidMissionPhase.SupportReturn)
                        && raid.SupportArmyId == armyId)
                    || ((raid.Phase == RaidMissionPhase.Return
                            || raid.Phase == RaidMissionPhase.RecoveryReturn)
                        && raid.PrimaryArmyId == armyId);
            if (mission?.Target is AttackMissionTarget attack)
                return ((attack.Phase == AttackMissionPhase.Reinforcement
                            || attack.Phase == AttackMissionPhase.Gather
                            || attack.Phase == AttackMissionPhase.SupportReturn
                            || attack.Phase == AttackMissionPhase.GatherReturn)
                        && attack.SupportArmyId == armyId)
                    || (attack.Phase == AttackMissionPhase.RecoveryReturn
                        && attack.PrimaryArmyId == armyId);
            return false;
        }

        // An Attack leg executed by a SUPPORT army, never by the primary: its mover must not be
        // written back as the operation's primary.
        internal static bool IsAttackSupportLeg(AttackMissionPhase phase) =>
            phase == AttackMissionPhase.Reinforcement
            || phase == AttackMissionPhase.SupportReturn
            || phase == AttackMissionPhase.Gather
            || phase == AttackMissionPhase.GatherReturn;

        // The requirements of a lifecycle leg whose mover Continuity already pinned (a walk home,
        // a convoy, a gather): one activation this turn unless already paid, and the mover's own
        // travel time to `destination`. The leg's execution priority is its durable commitment.
        internal static MissionRequirements PinnedLegRequirements(ArmySnapshot mover,
            HexCoord destination, out int eta)
        {
            int distance = HexGridMath.Distance(mover.Hex, destination);
            eta = AiV2Util.CeilDiv(distance, Mathf.Max(1, mover.MaxMovement));
            float ap = mover.HasActivatedThisTurn ? 0f : mover.ActivationApCost;
            return new MissionRequirements
            {
                MoverKnown = true,
                RequiresArmy = true,
                ApMinimum = ap, ApDesired = ap, ApMaximum = ap,
                EtaTurns = eta,
                EstimatedDistance = distance,
            };
        }

        // Ground support armies a durable operation holds beyond its primary: the Raid/Attack
        // convoy while it travels or walks home after a swap, and every support an Attack Gather
        // still expects. ActorCommitments claims them; admission fingerprints key on them.
        internal static IEnumerable<int> HeldGroundSupportArmyIds(MissionIntent intent)
        {
            RaidIntent raid = intent?.Raid;
            if (raid != null && raid.SupportArmyId.HasValue
                && (raid.Phase == RaidMissionPhase.Reinforcement
                    || raid.Phase == RaidMissionPhase.SupportReturn))
                yield return raid.SupportArmyId.Value;
            AttackIntent attack = intent?.Attack;
            if (attack == null)
                yield break;
            if (attack.SupportArmyId.HasValue
                && (attack.Phase == AttackMissionPhase.Reinforcement
                    || attack.Phase == AttackMissionPhase.SupportReturn))
                yield return attack.SupportArmyId.Value;
            if (attack.Phase == AttackMissionPhase.Gather)
                foreach (int id in attack.GatherSupportArmyIds)
                    yield return id;
            foreach (AttackGatherReturn r in attack.GatherReturns)
                yield return r.ArmyId;
        }
    }
}
