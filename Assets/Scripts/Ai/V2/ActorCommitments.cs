using System.Collections.Generic;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  ACTOR COMMITMENTS  (Strategy V2 — Strategic Manager)
    // ===========================================================================================
    //  A normalized "which of my own armies are already committed to an active operation" view.
    //  Built once from MissionContinuityLayer's resolved intents (and rebuilt from the reconciled
    //  registry before Phase B). Downstream code — DemandLayer, CapabilityInventory,
    //  ReusableArmySelector, StrategicManager — only ever asks IsArmyClaimed(id); it never learns
    //  HOW continuity stores mover ownership. This is what lets "existing Scout" be told apart
    //  from "available Scout", and it extends unchanged to Raid / Defence / Assembly when those
    //  gain persistent missions.
    //
    //  An intent's PreferredMoverArmyId is only claimed while the actor is STILL VALID for that
    //  intent — a live solo Recce that still meets the intent's real stealth requirement. A live
    //  army that has been folded into a combat force, or lost its stealth unit, is NOT claimed:
    //  its objective is genuinely uncovered AND the army is free to be reassigned to a job it can
    //  still do (a plain Explore).
    //
    //  TODO (when Raid / Defence land): replace the Recon-shaped (snap, ReconObjective[]) inputs
    //  with a per-intent CapabilityRequirement + IsClaimStillValid(snapshot, intent) contract.
    // ===========================================================================================
    public sealed class ActorCommitments
    {
        private readonly HashSet<int> _claimedArmyIds = new HashSet<int>();

        public IReadOnlyCollection<int> ClaimedArmyIds => _claimedArmyIds;

        // Live copy for the shared eligibility primitive (ScoutMoverSelector.Eligible takes an ISet).
        public HashSet<int> ClaimedArmyIdSet => new HashSet<int>(_claimedArmyIds);

        public bool IsArmyClaimed(int armyId) => armyId != 0 && _claimedArmyIds.Contains(armyId);

        public void Claim(int armyId)
        {
            if (armyId != 0)
                _claimedArmyIds.Add(armyId);
        }

        public static ActorCommitments FromIntents(IEnumerable<MissionIntent> intents,
            WorldSnapshot snap, IReadOnlyList<ReconObjective> reconObjectives)
        {
            var c = new ActorCommitments();
            if (intents == null || snap?.Self?.Armies == null)
                return c;

            // The current stealth requirement of each intent's objective, from the ONE Recon
            // objective enumeration (an exposed Explore is Stealth.Required too — never infer the
            // requirement from ScoutTargetKind).
            var reqByKey = new Dictionary<MissionIntentKey, StealthRequirement>();
            if (reconObjectives != null)
                foreach (ReconObjective o in reconObjectives)
                    reqByKey[o.IntentKey] = o.Stealth;

            foreach (MissionIntent i in intents)
            {
                if (i?.PreferredMoverArmyId == null)
                    continue;

                StealthRequirement req;
                if (reqByKey.TryGetValue(i.IntentKey, out StealthRequirement r))
                {
                    req = r;
                }
                else
                {
                    // A still-valid incumbent Explore whose hex has fallen out of the frozen
                    // frontier (wave band moved) has NO entry here — MissionLayer re-materialises
                    // it via ReconObjectiveEvaluator.ExploreAt, which recomputes exposure and can
                    // return Stealth.Required. Materialise that ONE incumbent objective the same
                    // way (not a re-scan for new opportunities) so the requirement matches what
                    // the planner will actually demand.
                    ReconObjective o = null;
                    if (i.Scout != null)
                        o = i.Scout.Kind == ScoutTargetKind.Explore
                            ? ReconObjectiveEvaluator.ExploreAt(snap, i.Scout.FocusHex)
                            : ReconObjectiveEvaluator.SurveilOf(snap,
                                ScoutObjectiveEvaluator.SurveilContact(snap, i.Scout.TrackedArmyId));
                    req = o?.Stealth ?? StealthRequirement.None;
                }

                if (HasCapableActor(i, snap, req))
                    c.Claim(i.PreferredMoverArmyId.Value);
            }
            return c;
        }

        // Is the intent's committed mover a live own army STRUCTURALLY able to run it — a solo
        // Recce, and (when the objective requires stealth) hidden or able to enter stealth? Read
        // from the snapshot's own-army list; no live game-system call.
        public static bool HasCapableActor(MissionIntent intent, WorldSnapshot snap, StealthRequirement requirement)
        {
            if (intent?.PreferredMoverArmyId == null || snap?.Self?.Armies == null)
                return false;
            int id = intent.PreferredMoverArmyId.Value;

            ArmySnapshot a = null;
            foreach (ArmySnapshot s in snap.Self.Armies)
                if (s != null && s.ArmyId == id) { a = s; break; }
            if (a == null || !a.IsSoloRecce || a.IsPrison || a.IsAir || a.MemberCount <= 0)
                return false;

            if (requirement == StealthRequirement.Required
                && !(a.IsHidden || a.CanEnterStealth || a.StealthLevel > 0))
                return false;
            return true;
        }
    }
}
