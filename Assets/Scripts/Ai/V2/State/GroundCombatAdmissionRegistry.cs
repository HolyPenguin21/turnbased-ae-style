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
            // AGG-RAID §8 — only an ASSAULT leg is a fresh actor-admission decision. Reinforcement
            // and Return already carry a Continuity-pinned actor.
            if (target.Phase != RaidMissionPhase.Assault)
                return;

            IReadOnlyList<WorthIt.DefenderProfile> defenders = AiV2Util.KnownDefenders(snap, target.Target);
            List<int> ids = EnumerateEligible(snap, defenders, unavailableArmyIds,
                GroundCombatAdmissionPolicy.FreshStartWinChanceGate, 0f);
            ApplyDurableIncumbentPin(proposal, snap, defenders, 0f, ids,
                "RaidAdmission", target.Target.DiagnosticLabel);

            ByProposal.Remove(proposal);
            ByProposal.Add(proposal, new Entry(ids));
        }

        // ATK §45 — Attack's Assault leg, through exactly the same enumeration and the same
        // durable-incumbent rule as Raid's. The only lane-specific inputs are which defenders are
        // being fought and what defence bonus the site gives them (§30).
        public static void RecordAttack(MissionProposal proposal, WorldSnapshot snap,
            IReadOnlyList<WorthIt.DefenderProfile> defenders, float defenderHexDefenseBonus,
            ISet<int> unavailableArmyIds)
        {
            if (proposal == null || snap == null
                || !(proposal.Target is AttackMissionTarget target)
                || target.Phase != AttackMissionPhase.Assault)
                return;

            defenders = defenders ?? Array.Empty<WorthIt.DefenderProfile>();
            List<int> ids = EnumerateEligible(snap, defenders, unavailableArmyIds,
                GroundCombatAdmissionPolicy.FreshStartWinChanceGate, defenderHexDefenseBonus);
            ApplyDurableIncumbentPin(proposal, snap, defenders, defenderHexDefenseBonus, ids,
                "AttackAdmission", target.Target.DiagnosticLabel);

            ByProposal.Remove(proposal);
            ByProposal.Add(proposal, new Entry(ids));
        }

        // The ONE enumeration of "which ready ground armies could independently take this fight".
        // GroundCombatAssemblyPlanner.Plan returns the strongest currently-eligible actor under the
        // given gate; re-running while excluding each hit walks the whole eligible set without
        // duplicating its eligibility or WorthIt rules anywhere else.
        private static List<int> EnumerateEligible(WorldSnapshot snap,
            IReadOnlyList<WorthIt.DefenderProfile> defenders, ISet<int> unavailableArmyIds,
            float winChanceGate, float defenderHexDefenseBonus)
        {
            var excluded = unavailableArmyIds == null
                ? new HashSet<int>() : new HashSet<int>(unavailableArmyIds);
            var ids = new List<int>();
            while (true)
            {
                GroundCombatAssemblyPlan plan = GroundCombatAssemblyPlanner.Plan(snap,
                    new GroundCombatAssemblyRequest
                    {
                        Defenders = defenders ?? Array.Empty<WorthIt.DefenderProfile>(),
                        WinChanceGate = winChanceGate,
                        ExcludedArmyIds = excluded,
                        DefenderHexDefenseBonus = defenderHexDefenseBonus,
                    });
                if (!plan.Feasible || !excluded.Add(plan.BaseArmyId))
                    break;
                ids.Add(plan.BaseArmyId);
            }
            return ids;
        }

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
            IReadOnlyList<WorthIt.DefenderProfile> defenders, float defenderHexDefenseBonus,
            List<int> ids, string logTag, string targetLabel)
        {
            if (!proposal.FromDurableIntent
                || proposal.DurableFundingTier != CommitmentTier.Hard
                || !proposal.PreferredMoverArmyId.HasValue)
                return;
            int incumbentId = proposal.PreferredMoverArmyId.Value;
            GroundCombatAssemblyPlan incumbent = GroundCombatAssemblyPlanner.PlanForArmyAtThreshold(
                snap, defenders, incumbentId,
                GroundCombatAdmissionPolicy.ContinuationWinChanceFloor, defenderHexDefenseBonus);
            if (!incumbent.Feasible)
                return;
            ids.Clear();
            ids.Add(incumbentId);
            AiDebugLog.Write($"[AI][V2][{logTag}] decision=CONTINUE target={targetLabel} "
                + $"actor={incumbentId} win={incumbent.ProjectedWinChance:0.00} "
                + "reason=durable_hard_incumbent_passed_continuation_gate");
        }

        // AGG-RAID P0#1 — mirror of Record() for an UNPINNED Reinforcement leg (no SupportArmyId
        // yet): the eligible set is every existing free army whose merge with the primary's roster
        // improves the primary's WorthIt win chance, so PrepareGroundCombatAssignments can run the
        // same actor-contention batch solve it already runs for Assault instead of leaving the leg
        // permanently unassignable until a materialization happens to hand it an actor.
        //
        // ATK §45/§46 — Attack's unpinned Reinforcement leg asks the identical question, so it goes
        // through this same entry point: the dispatch below only decides WHICH primary is being
        // reinforced and against WHICH defender package (and, for a structure assault, what defence
        // that site gives them, §30). There is deliberately no RecordAttackReinforcement twin.
        public static void RecordReinforcement(MissionProposal proposal, WorldSnapshot snap)
        {
            if (proposal == null || snap == null)
                return;

            int primaryArmyId;
            IReadOnlyList<WorthIt.DefenderProfile> defenders;
            float hexBonus = 0f;
            if (proposal.Target is RaidMissionTarget raid)
            {
                if (raid.Phase != RaidMissionPhase.Reinforcement || raid.SupportArmyId.HasValue
                    || !raid.PrimaryArmyId.HasValue)
                    return;
                primaryArmyId = raid.PrimaryArmyId.Value;
                defenders = AiV2Util.KnownDefenders(snap, raid.Target);
            }
            else if (proposal.Target is AttackMissionTarget attack)
            {
                if (attack.Phase != AttackMissionPhase.Reinforcement || attack.SupportArmyId.HasValue
                    || !attack.PrimaryArmyId.HasValue)
                    return;
                primaryArmyId = attack.PrimaryArmyId.Value;
                defenders = AttackObjectiveEvaluator.KnownSiteDefenders(snap, attack.Target.Hex);
                hexBonus = attack.DefenderHexDefenseBonus;
            }
            else
            {
                return;
            }

            List<int> ids = GroundCombatAssemblyPlanner.ReinforcementSupportCandidates(
                snap, primaryArmyId, defenders, null, hexBonus);

            ByProposal.Remove(proposal);
            ByProposal.Add(proposal, new Entry(ids));
        }

        public static void RecordActiveDefence(MissionProposal proposal, WorldSnapshot snap,
            IReadOnlyList<WorthIt.DefenderProfile> defenders, ISet<int> unavailableArmyIds)
        {
            if (proposal == null || snap == null
                || !(proposal.Target is ActiveDefenceMissionTarget target)
                || target.Phase != ActiveDefencePhase.Intercept)
                return;
            var excluded = unavailableArmyIds == null
                ? new HashSet<int>() : new HashSet<int>(unavailableArmyIds);
            var ids = new List<int>();
            while (true)
            {
                GroundCombatAssemblyPlan plan = GroundCombatAssemblyPlanner.Plan(snap,
                    new GroundCombatAssemblyRequest
                    {
                        Defenders = defenders ?? Array.Empty<WorthIt.DefenderProfile>(),
                        WinChanceGate = proposal.FromDurableIntent
                            ? GroundCombatAdmissionPolicy.ContinuationWinChanceFloor
                            : GroundCombatAdmissionPolicy.FreshStartWinChanceGate,
                        PreferredPrimaryArmyId = proposal.FromDurableIntent
                            ? proposal.PreferredMoverArmyId : null,
                        PinToPreferred = proposal.FromDurableIntent
                            && proposal.PreferredMoverArmyId.HasValue,
                        ExcludedArmyIds = excluded,
                    });
                if (!plan.Feasible || !excluded.Add(plan.BaseArmyId)) break;
                ids.Add(plan.BaseArmyId);
            }
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

        // Exact for a two-mission comparison: at least one distinct actor assignment must exist.
        // The allocator's bounded provision/re-pack remains the final N-way guard; importantly,
        // RaidProvisioner separately classifies a solver failure caused only by earlier actor
        // claims as MoverContended (transient), never AssemblyInfeasible/cooldown.
        public static bool PairHasDistinctAssignment(MissionProposal a, MissionProposal b)
        {
            if (!TryGet(a, out HashSet<int> aa) || !TryGet(b, out HashSet<int> bb))
                return true; // legacy/bare harness: keep the final Provisioning guard authoritative
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
