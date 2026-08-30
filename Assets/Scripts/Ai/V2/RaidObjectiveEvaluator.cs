using System.Linq;
using Game.Core;
using Game.Map;
using Game.Players;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  RAID OBJECTIVE EVALUATOR  (Strategy V2 build-order step 9 — the single Raid completion/validity home)
    // ===========================================================================================
    //  The Aggression counterpart of ScoutObjectiveEvaluator (spec §38). It answers ONLY the
    //  runtime questions:
    //    · IsIntentStillValid   — is a durable RaidIntent still a coherent thing to pursue against
    //                             THIS snapshot? (snapshot read only — MissionContinuityLayer /
    //                             MissionLayer re-materialisation)
    //    · IsObjectiveSatisfied — has the target been destroyed / captured / turned non-hostile?
    //                             (live read — the ledger's post-execution pass)
    //
    //  It is NOT strategic scoring: AggressionObjectiveEvaluator answers "is this known target
    //  worth considering and how valuable" (BaseValue); this answers "does the concrete target
    //  operation still exist and is it done".
    //
    //  KNOWLEDGE RULES (spec §39). Loss of current visibility is NEVER proof of destruction. The
    //  live "satisfied" read scans the army REGISTRY (not fog memory), so a target that merely
    //  walked out of vision still exists and the raid keeps going; only an army that is genuinely
    //  gone from every opponent's roster — or now ours — retires the intent.
    // ===========================================================================================
    public static class RaidObjectiveEvaluator
    {
        // ---- SNAPSHOT (continuity / mission-layer re-materialisation) -----------------------

        // Is the tracked target still a known hostile army we could still raid? Reads ONLY the
        // snapshot's honest sightings — never a live game system.
        public static bool IsIntentStillValid(WorldSnapshot snap, RaidIntent intent)
        {
            if (snap?.Known == null || intent == null || intent.TargetArmyId == 0)
                return false;

            AiMapMemory.KnownEnemySighting? s = FindSighting(snap, intent.TargetArmyId);
            if (s == null)
                // No current honest sighting. Keep the intent alive as long as it has actually
                // started (a Hard raid in transit must not evaporate the turn the target slips
                // into fog) — MissionContinuityLayer's stall / age caps still reap a raid that
                // never re-acquires. An unstarted intent with no sighting is dropped.
                return intent.OperationStarted;

            // Still there — but no longer a legal raid target if it became ours.
            return s.Value.Owner == null || s.Value.Owner.IsNeutral || !s.Value.Owner.Equals(SelfOwner(snap));
        }

        // The tracked target's freshest honest sighting, or null.
        public static AiMapMemory.KnownEnemySighting? FindSighting(WorldSnapshot snap, int trackedArmyId)
        {
            if (snap?.Known == null || trackedArmyId == 0)
                return null;
            var all = (snap.Known.EnemySightings ?? System.Linq.Enumerable.Empty<AiMapMemory.KnownEnemySighting>())
                .Concat(snap.Known.NeutralSightings ?? System.Linq.Enumerable.Empty<AiMapMemory.KnownEnemySighting>());
            foreach (AiMapMemory.KnownEnemySighting s in all)
                if (s.ArmyId == trackedArmyId)
                    return s;
            return null;
        }

        // ---- LIVE (post-execution ledger pass) --------------------------------------------

        // The raid objective is satisfied the moment the target army no longer exists as a hostile
        // force — destroyed in a fight, or captured (now ours). Registry read, NOT fog memory
        // (spec §39): an army that just left our vision is still in some opponent's roster and does
        // NOT count as satisfied.
        public static bool IsObjectiveSatisfiedLive(PlayerSetupData player, int targetArmyId)
        {
            if (player == null || targetArmyId == 0)
                return false;
            foreach (PlayerSetupData other in GameSession.Players ?? System.Linq.Enumerable.Empty<PlayerSetupData>())
            {
                if (other == null || other.Equals(player))
                    continue;
                foreach (ArmyData a in ArmyRegistry.AllForOwner(other))
                    if (a != null && a.Id == targetArmyId && a.Members.Count > 0)
                        return false;   // still fielded by a non-us player -> not satisfied
            }
            return true;   // gone from every opponent roster -> destroyed or captured
        }

        private static PlayerSetupData SelfOwner(WorldSnapshot snap)
        {
            ArmySnapshot a = snap?.Self?.Armies?.FirstOrDefault(x => x != null && x.Owner != null);
            return a?.Owner;
        }
    }
}
