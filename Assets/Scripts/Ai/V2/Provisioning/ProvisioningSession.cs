using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Game.Aviation;
using Game.Cards;
using Game.Economy;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Units;
using UnityEngine;

using Game.Combat;

namespace Game.Ai.V2
{
    public sealed class ProvisioningSession
    {
        public readonly WorldSnapshot Snapshot;
        public float ApClaimed { get; private set; }
        // The cumulative real Energy every AirLaunch mission provisioned so far THIS pass has
        // claimed. Mirrors ApClaimed's role for AP: without this, two separate
        // AirLaunch missions provisioned sequentially within the same pass each check affordability
        // against the SAME unmutated root Energy stock independently, so both can pass even though
        // launching both together would exceed it (ProvisioningManager.ProvisionAir checks against
        // this before accepting a launch).
        public float EnergyClaimed { get; private set; }
        public readonly HashSet<int> ClaimedArmyIds = new HashSet<int>();
        // Durable ownership is distinct from same-pass claims. Provisioning must preserve both:
        // the batch solvers filter with this set, and Raid live revalidation uses it for hosts and
        // assembly donors so a retry cannot steal an Economy/Recon/Raid incumbent.
        public readonly HashSet<int> DurableClaimedArmyIds = new HashSet<int>();

        private readonly Dictionary<StableMissionKey, ProvisionedMission> _successful =
            new Dictionary<StableMissionKey, ProvisionedMission>();
        private readonly Dictionary<StableMissionKey, ScoutExecutionCandidate> _assignment =
            new Dictionary<StableMissionKey, ScoutExecutionCandidate>();
        // Round 3 (Problem 2) — the rejection reason ReconAssignmentPlanner.AssignFunded already
        // computed for every Scout mission that got no actor this pass. ClassifyNoAssignment below
        // is now a pure translation of this into a ProvisionFailure — it never re-derives it.
        private readonly Dictionary<StableMissionKey, ScoutAssignmentFailureReason> _assignmentRejections =
            new Dictionary<StableMissionKey, ScoutAssignmentFailureReason>();
        private readonly Dictionary<StableMissionKey, int> _groundCombatAssignment =
            new Dictionary<StableMissionKey, int>();
        // The SAME ownership constraints used for the single ground-combat batch assignment must
        // survive into the per-leg provisioners (including their same-hex donors). Refreshed on
        // every repack. ATK §44 — this store is the ONE ground-combat actor assignment for every
        // lane that fights on the ground (Raid, ActiveDefence and, from ATK, Attack); it was named
        // after Raid only because Raid happened to be the first lane built on it.
        private ActorCommitments _groundCombatDurableCommitments;
        private HashSet<int> _groundCombatPinnedByOtherLegs = new HashSet<int>();

        public ProvisioningSession(WorldSnapshot snapshot) { Snapshot = snapshot; }
        public IReadOnlyDictionary<StableMissionKey, ProvisionedMission> Successful => _successful;
        public bool AlreadyProvisioned(StableMissionKey k) => _successful.ContainsKey(k);

        public void RegisterSuccess(StableMissionKey k, ProvisionedMission m)
        {
            _successful[k] = m;
            ApClaimed += m.ClaimedAp;
            EnergyClaimed += m.ClaimedEnergy;
            ClaimedArmyIds.Add(m.MoverArmyId);
            // A deferred garrison-extraction mission's MoverArmyId is a synthetic negative id; the
            // garrison and the chosen container (if one already exists — Shell/Host tiers) are the
            // REAL armies this mission has committed to and must not be handed to a second mission
            // later in the same batch pass.
            if (m.EconomyExtractionGarrisonArmyId >= 0)
                ClaimedArmyIds.Add(m.EconomyExtractionGarrisonArmyId);
            if (m.EconomyExtractionPlan.Container != null)
                ClaimedArmyIds.Add(m.EconomyExtractionPlan.Container.Id);
        }

        internal void SetAssignment(ReconAssignmentResult result)
        {
            _assignment.Clear();
            _assignmentRejections.Clear();
            if (result == null) return;
            foreach (KeyValuePair<StableMissionKey, ScoutExecutionCandidate> kv in result.Assigned)
                _assignment[kv.Key] = kv.Value;
            foreach (KeyValuePair<StableMissionKey, ScoutAssignmentFailureReason> kv in result.Rejected)
                _assignmentRejections[kv.Key] = kv.Value;
        }

        internal bool TryGetAssignedExecution(StableMissionKey k, out ScoutExecutionCandidate exec) =>
            _assignment.TryGetValue(k, out exec);

        internal bool TryGetAssignmentRejection(StableMissionKey k, out ScoutAssignmentFailureReason reason) =>
            _assignmentRejections.TryGetValue(k, out reason);

        internal IReadOnlyDictionary<StableMissionKey, ScoutAssignmentFailureReason>
            AssignmentRejections => _assignmentRejections;

        internal void SetGroundCombatConstraints(ActorCommitments durableCommitments,
            ISet<int> pinnedByOtherLegs)
        {
            _groundCombatDurableCommitments = durableCommitments;
            _groundCombatPinnedByOtherLegs = pinnedByOtherLegs == null
                ? new HashSet<int>() : new HashSet<int>(pinnedByOtherLegs);
        }

        // Single ground-combat ownership read, shared by batch assignment AND final binding.
        // A durable mission may use its own incumbent, but never another mission's army;
        // exclusions also apply to donors, not merely the primary host.
        internal HashSet<int> ExcludedForGroundCombat(MissionProposal proposal)
        {
            var excluded = new HashSet<int>(ClaimedArmyIds);
            foreach (int pinnedId in _groundCombatPinnedByOtherLegs)
            {
                // The pinned set is computed across ALL funded non-Assault Raid legs, including
                // this very Reinforcement/Return leg. Its own actor must remain permitted;
                // never erase a real same-pass claim or an assignment belonging to another Raid.
                bool thisLegsActor = proposal?.Target is RaidMissionTarget raid
                    && ((raid.Phase == RaidMissionPhase.Reinforcement
                            && raid.SupportArmyId == pinnedId)
                        || (raid.Phase == RaidMissionPhase.Return
                            && raid.PrimaryArmyId == pinnedId)
                        || (raid.Phase == RaidMissionPhase.RecoveryReturn
                            && raid.PrimaryArmyId == pinnedId)
                        || (raid.Phase == RaidMissionPhase.SupportReturn
                            && raid.SupportArmyId == pinnedId));
                // ATK §44 — same rule for an Attack leg: the pinned set is computed across ALL
                // funded non-Assault legs including this one, so its own actor must stay permitted.
                bool thisAttackLegsActor = proposal?.Target is AttackMissionTarget attackLeg
                    && ((attackLeg.Phase == AttackMissionPhase.Reinforcement
                            && attackLeg.SupportArmyId == pinnedId)
                        || (attackLeg.Phase == AttackMissionPhase.RecoveryReturn
                            && attackLeg.PrimaryArmyId == pinnedId)
                        || (attackLeg.Phase == AttackMissionPhase.SupportReturn
                            && attackLeg.SupportArmyId == pinnedId));
                thisLegsActor |= thisAttackLegsActor;
                if (!thisLegsActor)
                    excluded.Add(pinnedId);
            }
            if (_groundCombatDurableCommitments != null)
                foreach (int id in _groundCombatDurableCommitments.ClaimedArmyIds)
                {
                    bool ownIncumbent = proposal != null && proposal.FromDurableIntent
                        && proposal.PreferredMoverArmyId == id;
                    bool exactOffensiveBorrow = proposal?.Target is ActiveDefenceMissionTarget defence
                        && defence.SuspendedOffensiveIntentKey.HasValue
                        && proposal.PreferredMoverArmyId == id;
                    if (!ownIncumbent && !exactOffensiveBorrow)
                        excluded.Add(id);
                }
            // Batch-assigned Raid hosts/support are also unavailable as donors, even before
            // their mission executes and RegisterSuccess adds them to ClaimedArmyIds.
            StableMissionKey? ownKey = proposal == null
                ? (StableMissionKey?)null : StableMissionKey.For(proposal);
            foreach (KeyValuePair<StableMissionKey, int> assignment in _groundCombatAssignment)
                if (!ownKey.HasValue || !assignment.Key.Equals(ownKey.Value))
                    excluded.Add(assignment.Value);
            return excluded;
        }

        internal void SetGroundCombatAssignment(Dictionary<StableMissionKey, int> a)
        {
            _groundCombatAssignment.Clear();
            foreach (KeyValuePair<StableMissionKey, int> kv in a)
                _groundCombatAssignment[kv.Key] = kv.Value;
        }

        internal void SetDurableClaims(IEnumerable<int> ids)
        {
            DurableClaimedArmyIds.Clear();
            if (ids == null) return;
            foreach (int id in ids) DurableClaimedArmyIds.Add(id);
        }

        internal bool TryGetAssignedGroundCombatActor(StableMissionKey k, out int armyId) =>
            _groundCombatAssignment.TryGetValue(k, out armyId);
    }
}
