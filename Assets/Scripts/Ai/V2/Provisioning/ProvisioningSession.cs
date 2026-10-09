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
    public sealed class ProvisioningSession : IDisposable
    {
        public readonly WorldSnapshot Snapshot;
        public float ApClaimed { get; private set; }
        // Cumulative current-turn aviation Energy claimed in this planning pass.
        public float EnergyClaimed { get; private set; }
        // Tentative pass claims use the same claim storage abstraction and are closed by the turn
        // owner. Durable exclusions remain the read-only commitment projection below.
        public readonly ISet<int> ClaimedArmyIds;

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
        // Read-only funded lifecycle proposals for joint feasibility, not another claim store.
        private readonly List<MissionProposal> _groundCombatPinnedLegs = new List<MissionProposal>();
        internal IReadOnlyList<MissionProposal> PinnedGroundCombatLegs => _groundCombatPinnedLegs;

        public ProvisioningSession(WorldSnapshot snapshot) : this(snapshot,
            AiTurnSession.Peek(snapshot?.Observer, snapshot?.TurnNumber ?? -1)) { }
        internal ProvisioningSession(WorldSnapshot snapshot, AiTurnSession turn)
        {
            if (turn != null && snapshot != null && (snapshot.TurnNumber != turn.TurnNumber
                || snapshot.Observer != null && !ReferenceEquals(snapshot.Observer, turn.Player)))
                throw new InvalidOperationException("Provisioning belongs to another player or turn.");
            Snapshot = snapshot;
            ClaimedArmyIds = turn?.CreateProvisioningClaims() ?? new MissionLeaseBook().PassActorSet();
        }
        public void Dispose() => (ClaimedArmyIds as IDisposable)?.Dispose();
        private void EnsureActive() { _ = ClaimedArmyIds.Count; }
        public IReadOnlyDictionary<StableMissionKey, ProvisionedMission> Successful => _successful;
        public bool AlreadyProvisioned(StableMissionKey k) => _successful.ContainsKey(k);

        public void RegisterSuccess(StableMissionKey k, ProvisionedMission m)
        {
            EnsureActive();
            // Success pins this mission for the pass. A retry/repack may acknowledge it again,
            // but must not reserve its AP/Energy a second time while the keyed result stays single.
            if (_successful.ContainsKey(k))
                return;
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
            EnsureActive();
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
            ISet<int> pinnedByOtherLegs, IEnumerable<MissionProposal> pinnedLegs = null)
        {
            EnsureActive();
            _groundCombatDurableCommitments = durableCommitments;
            _groundCombatPinnedLegs.Clear();
            if (pinnedLegs != null)
                _groundCombatPinnedLegs.AddRange(pinnedLegs.Where(p => GroundCombatLegs.PinsActors(p, out _, out _)));
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
                // The pinned set is computed across ALL funded lifecycle legs, including this
                // very leg. Its own actor must remain permitted; never erase a real same-pass
                // claim or an assignment belonging to another operation.
                if (!GroundCombatLegs.IsOwnLegActor(proposal, pinnedId))
                    excluded.Add(pinnedId);
            }
            if (_groundCombatDurableCommitments != null)
                foreach (int id in _groundCombatDurableCommitments.ClaimedArmyIds)
                {
                    bool ownIncumbent = proposal != null && proposal.FromDurableIntent
                        && proposal.PreferredMoverArmyId == id;
                    if (!ownIncumbent)
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
            EnsureActive();
            _groundCombatAssignment.Clear();
            foreach (KeyValuePair<StableMissionKey, int> kv in a)
                _groundCombatAssignment[kv.Key] = kv.Value;
        }

        internal bool TryGetAssignedGroundCombatActor(StableMissionKey k, out int armyId) =>
            _groundCombatAssignment.TryGetValue(k, out armyId);
    }
}
