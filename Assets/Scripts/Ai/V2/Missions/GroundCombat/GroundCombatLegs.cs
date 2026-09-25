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
            || phase == AttackMissionPhase.GatherReturn
            || phase == AttackMissionPhase.AirSupport;

        // An Attack leg run BESIDE the operation, never as its step: a gather donor walking home or
        // the support wing's sortie. Its outcome never touches the operation's lifecycle, and it
        // never competes with the operation's other legs for admission.
        // The leg an outcome reports, read from the provisioned payload or — when Provisioning
        // failed before a payload existed — from the proposal itself.
        internal static RaidMissionPhase? RaidLegOf(MissionTurnOutcome o) =>
            o == null || o.MissionKind != MissionKind.Raid ? (RaidMissionPhase?)null
            : o.HasRaidPayload ? o.RaidPhase
            : o.Proposal?.Target is RaidMissionTarget rt ? rt.Phase : (RaidMissionPhase?)null;

        internal static AttackMissionTarget? AttackLegOf(MissionTurnOutcome o) =>
            o == null || o.MissionKind != MissionKind.Attack ? (AttackMissionTarget?)null
            : o.HasAttackPayload ? o.AttackTarget
            : o.Proposal?.Target is AttackMissionTarget at ? at : (AttackMissionTarget?)null;

        // THE "support-local" rule, one answer for every ground-combat lane: a leg moved by a
        // SUPPORT actor of the operation (Raid convoy / support walk home / wing; every Attack
        // support leg) that loses its mover, its primary or its target never ends the operation.
        // The ledger reports it Blocked and ResolveActive's next pass cleans up only that support
        // (or, for a lost primary, retires the operation itself).
        internal static bool IsSupportLeg(MissionTurnOutcome o)
        {
            RaidMissionPhase? raid = RaidLegOf(o);
            if (raid.HasValue)
                return raid.Value == RaidMissionPhase.AirSupport
                    || raid.Value == RaidMissionPhase.Reinforcement
                    || raid.Value == RaidMissionPhase.SupportReturn;
            AttackMissionTarget? attack = AttackLegOf(o);
            return attack.HasValue && IsAttackSupportLeg(attack.Value.Phase);
        }

        internal static bool IsAttackSideLeg(AttackMissionPhase phase) =>
            phase == AttackMissionPhase.GatherReturn || phase == AttackMissionPhase.AirSupport;

        // The support wing a durable operation holds (Raid in its AirSupport phase, Attack while a
        // wing is bound). ActorCommitments claims it; an airborne strike sortie no operation holds
        // is sent home by GroundCombatAirSupport.ReleaseOrphanStrikes.
        internal static int? HeldAirSupportArmyId(MissionIntent intent)
        {
            RaidIntent raid = intent?.Raid;
            if (raid != null && raid.Phase == RaidMissionPhase.AirSupport && raid.AirSupportArmyId.HasValue)
                return raid.AirSupportArmyId;
            return intent?.Attack?.AirSupportArmyId;
        }

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
