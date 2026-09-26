using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;

using Game.Combat;

namespace Game.Ai.V2
{
    // Proposal-side physical feasibility for EVERY ground-combat lane — Raid, ActiveDefence and
    // (ATK §45) Attack. Like ScoutAdmissionRegistry, this NEVER binds an actor; ProvisioningManager
    // remains authoritative. It only records which ready ground armies can independently clear the
    // SAME WorthIt estimator for each target, so obvious same-turn actor collisions can be rejected
    // as portfolio admission rather than discovered as a fake structural target failure after
    // funding.
    //
    // ATK §45 — the per-lane Record* entry points below are thin: each one translates its own
    // mission target into a GroundCombatAssemblyRequest and hands it to the ONE enumeration
    // (EnumerateEligible) and the ONE durable-incumbent rule (ApplyDurableIncumbentPin). Mission
    // kind decides target semantics; it never gets its own estimator, its own eligibility rule or
    // its own admission system.
    internal static class GroundCombatAdmissionRegistry
    {
        private sealed class Entry
        {
            public readonly HashSet<int> EligibleArmyIds;
            public Entry(IEnumerable<int> ids) => EligibleArmyIds = new HashSet<int>(ids);
        }

        private static readonly ConditionalWeakTable<MissionProposal, Entry> ByProposal =
            new ConditionalWeakTable<MissionProposal, Entry>();

        public static void Record(MissionProposal proposal, WorldSnapshot snap,
            ISet<int> unavailableArmyIds = null)
        {
            if (proposal == null || snap == null || !(proposal.Target is RaidMissionTarget target))
                return;
            // Only an ASSAULT leg is a fresh actor-admission decision. Reinforcement
            // and Return already carry a Continuity-pinned actor.
            if (target.Phase != RaidMissionPhase.Assault)
                return;

            IReadOnlyList<WorthIt.DefendingArmy> opposition = AiV2Util.KnownOpposition(snap, target.Target);
            float hexBonus = AiV2Util.KnownRaidDefenceBonus(snap, target.Target);
            List<int> ids = EnumerateEligible(snap, opposition, unavailableArmyIds,
                GroundCombatAdmissionPolicy.FreshStartWinChanceGate, hexBonus);
            ApplyDurableIncumbentPin(proposal, snap, opposition, hexBonus, ids,
                GroundCombatAdmissionPolicy.ContinuationWinChanceFloor,
                "RaidAdmission", target.Target.DiagnosticLabel);

            ByProposal.Remove(proposal);
            ByProposal.Add(proposal, new Entry(ids));
        }

        // ATK §45 — Attack's Assault leg, through exactly the same enumeration and the same
        // durable-incumbent rule as Raid's. The only lane-specific inputs are which defenders are
        // being fought and what defence bonus the site gives them (§30).
        public static void RecordAttack(MissionProposal proposal, WorldSnapshot snap,
            IReadOnlyList<WorthIt.DefendingArmy> opposition, float defenderHexDefenseBonus,
            ISet<int> unavailableArmyIds)
        {
            if (proposal == null || snap == null
                || !(proposal.Target is AttackMissionTarget target)
                || target.Phase != AttackMissionPhase.Assault)
                return;

            opposition = opposition ?? Array.Empty<WorthIt.DefendingArmy>();
            List<int> ids = EnumerateEligible(snap, opposition, unavailableArmyIds,
                GroundCombatAdmissionPolicy.AttackWinChanceFloor, defenderHexDefenseBonus);
            ApplyDurableIncumbentPin(proposal, snap, opposition, defenderHexDefenseBonus, ids,
                GroundCombatAdmissionPolicy.AttackWinChanceFloor,
                "AttackAdmission", target.Target.DiagnosticLabel);

            ByProposal.Remove(proposal);
            ByProposal.Add(proposal, new Entry(ids));
        }

        // The ONE enumeration of "which ready ground armies could independently take this fight"
        // is GroundCombatAssemblyPlanner.EligibleActorIds (single pass, same eligibility and
        // WorthIt rules as Plan). This wrapper only fixes the lane-agnostic request shape.
        private static List<int> EnumerateEligible(WorldSnapshot snap,
            IReadOnlyList<WorthIt.DefendingArmy> opposition, ISet<int> unavailableArmyIds,
            float winChanceGate, float defenderHexDefenseBonus, int? pinnedArmyId = null) =>
            GroundCombatAssemblyPlanner.EligibleActorIds(snap, new GroundCombatAssemblyRequest
            {
                Opposition = opposition ?? Array.Empty<WorthIt.DefendingArmy>(),
                WinChanceGate = winChanceGate,
                ExcludedArmyIds = unavailableArmyIds,
                DefenderHexDefenseBonus = defenderHexDefenseBonus,
                PreferredPrimaryArmyId = pinnedArmyId,
                PinToPreferred = pinnedArmyId.HasValue,
            });

        // A started Hard operation is not a fresh admission decision. Its PreferredMover already
        // passed the strict gate when the operation began and continuity/ActorCommitments owns that
        // physical actor across turns. Re-test that exact incumbent through the bounded continuation
        // gate so a small Monte-Carlo drop (the observed ~0.78 -> ~0.41 case) does not produce the
        // impossible state "Hard/CLAIM actor #X" + "readyActors=[none]".
        //
        // If the incumbent passes, PIN the operation to it. PrepareGroundCombatAssignments
        // deliberately sorts actors by activation/power and otherwise has no knowledge of
        // PreferredMover; leaving fresh actors in the set could silently switch a Hard operation to
        // another army and orphan the physical force continuity just protected. If the incumbent
        // fails the continuation gate, the strict fresh set remains a legitimate fallback.
        private static void ApplyDurableIncumbentPin(MissionProposal proposal, WorldSnapshot snap,
            IReadOnlyList<WorthIt.DefendingArmy> opposition, float defenderHexDefenseBonus,
            List<int> ids, float continuationGate, string logTag, string targetLabel)
        {
            if (!proposal.FromDurableIntent
                || proposal.DurableFundingTier != CommitmentTier.Hard
                || !proposal.PreferredMoverArmyId.HasValue)
                return;
            int incumbentId = proposal.PreferredMoverArmyId.Value;
            GroundCombatAssemblyPlan incumbent = GroundCombatAssemblyPlanner.PlanForArmyAtThreshold(
                snap, opposition, incumbentId, continuationGate, defenderHexDefenseBonus);
            if (!incumbent.Feasible)
                return;
            ids.Clear();
            ids.Add(incumbentId);
            AiDebugLog.Write($"[AI][V2][{logTag}] decision=CONTINUE target={targetLabel} "
                + $"actor={incumbentId} win={incumbent.ProjectedWinChance:0.00} "
                + "reason=durable_hard_incumbent_passed_continuation_gate");
        }

        // Mirror of Record() for an UNPINNED Reinforcement leg (no SupportArmyId
        // yet): the eligible set is every existing free army whose merge with the primary's roster
        // improves the primary's WorthIt win chance, so PrepareGroundCombatAssignments can run the
        // same actor-contention batch solve it already runs for Assault instead of leaving the leg
        // permanently unassignable until a materialization happens to hand it an actor.
        //
        // Attack's unpinned Reinforcement leg asks the identical question, so it goes
        // through this same entry point: the dispatch below only decides WHICH primary is being
        // reinforced and against WHICH defender package (and, for a structure assault, what defence
        // that site gives them, §30). There is deliberately no RecordAttackReinforcement twin.
        // `unavailableArmyIds` — armies claimed by other operations. Provisioning excludes them
        // (ProvisioningSession.ExcludedForGroundCombat), so the eligible set must too: otherwise the
        // leg is proposed/funded for a support no assignment can ever bind (NoMoverExists every
        // pass) and PairHasDistinctAssignment over-estimates what can coexist.
        public static void RecordReinforcement(MissionProposal proposal, WorldSnapshot snap,
            ISet<int> unavailableArmyIds = null)
        {
            if (proposal == null || snap == null)
                return;

            int primaryArmyId;
            IReadOnlyList<WorthIt.DefendingArmy> opposition;
            float hexBonus = 0f;
            bool allowCommandHandover = false;
            if (proposal.Target is RaidMissionTarget raid)
            {
                if (raid.Phase != RaidMissionPhase.Reinforcement || raid.SupportArmyId.HasValue
                    || !raid.PrimaryArmyId.HasValue)
                    return;
                primaryArmyId = raid.PrimaryArmyId.Value;
                opposition = AiV2Util.KnownOpposition(snap, raid.Target);
                hexBonus = AiV2Util.KnownRaidDefenceBonus(snap, raid.Target);
            }
            else if (proposal.Target is AttackMissionTarget attack)
            {
                if (attack.Phase != AttackMissionPhase.Reinforcement || attack.SupportArmyId.HasValue
                    || !attack.PrimaryArmyId.HasValue)
                    return;
                primaryArmyId = attack.PrimaryArmyId.Value;
                opposition = AttackObjectiveEvaluator.KnownSiteOpposition(snap, attack.Target.Hex);
                hexBonus = attack.DefenderHexDefenseBonus;
                allowCommandHandover = true;
            }
            else
            {
                return;
            }

            List<int> ids = GroundCombatAssemblyPlanner.ReinforcementSupportCandidates(
                snap, primaryArmyId, opposition, unavailableArmyIds, hexBonus, allowCommandHandover);

            ByProposal.Remove(proposal);
            ByProposal.Add(proposal, new Entry(ids));
        }

        public static void RecordActiveDefence(MissionProposal proposal, WorldSnapshot snap,
            IReadOnlyList<WorthIt.DefendingArmy> opposition, ISet<int> unavailableArmyIds)
        {
            if (proposal == null || snap == null
                || !(proposal.Target is ActiveDefenceMissionTarget target)
                || target.Phase != ActiveDefencePhase.Intercept)
                return;
            List<int> ids = EnumerateEligible(snap, opposition, unavailableArmyIds,
                GroundCombatAdmissionPolicy.PinnedOrFreshGate(proposal.FromDurableIntent),
                0f,
                proposal.FromDurableIntent ? proposal.PreferredMoverArmyId : null);
            ByProposal.Remove(proposal);
            ByProposal.Add(proposal, new Entry(ids));
        }

        public static bool TryGet(MissionProposal proposal, out HashSet<int> ids)
        {
            if (proposal != null && ByProposal.TryGetValue(proposal, out Entry entry))
            {
                ids = entry.EligibleArmyIds;
                return true;
            }
            ids = null;
            return false;
        }

#if UNITY_INCLUDE_TESTS
        internal static void RecordEligibleForTest(MissionProposal proposal, IEnumerable<int> ids)
        {
            ByProposal.Remove(proposal);
            ByProposal.Add(proposal, new Entry(ids ?? Enumerable.Empty<int>()));
        }
#endif

        // Exact for a two-mission comparison: at least one distinct actor assignment must exist.
        // The allocator's bounded provision/re-pack remains the final N-way guard; importantly,
        // RaidProvisioner separately classifies a solver failure caused only by earlier actor
        // claims as MoverContended (transient), never AssemblyInfeasible/cooldown.
        public static bool PairHasDistinctAssignment(MissionProposal a, MissionProposal b)
        {
            if (!TryGet(a, out HashSet<int> aa) || !TryGet(b, out HashSet<int> bb))
                return false;
            return SetsHaveDistinctAssignment(aa, bb);
        }

        internal static bool SetsHaveDistinctAssignment(IEnumerable<int> a, IEnumerable<int> b)
        {
            if (a == null || b == null)
                return true;
            int[] aa = a.Distinct().ToArray();
            int[] bb = b.Distinct().ToArray();
            foreach (int x in aa)
                foreach (int y in bb)
                    if (x != y)
                        return true;
            return false;
        }

        public static string EligibleIds(MissionProposal proposal)
        {
            if (!TryGet(proposal, out HashSet<int> ids))
                return "?";
            return ids.Count == 0 ? "none" : string.Join(",", ids.OrderBy(x => x));
        }

    }
}
