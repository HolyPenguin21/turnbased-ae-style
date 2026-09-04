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
    //  live "satisfied" read first resolves positive live ownership and then falls back to honest
    //  map memory. Neutral encounter armies are not guaranteed to live in GameSession.Players, so
    //  absence from ordinary player rosters is UNKNOWN while a hostile/neutral sighting is still
    //  remembered. This keeps a target that merely left vision — or a neutral encounter army —
    //  alive until capture/destruction is authoritative.
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

        // Objective completion must be POSITIVE, not inferred from one registry's absence.
        //  1) If the target id is now ours, it was captured / turned non-hostile -> satisfied.
        //  2) If any ordinary non-us player still fields it -> not satisfied.
        //  3) If no ordinary roster resolves it but honest memory still tracks it, this is the
        //     neutral/fog case -> not satisfied.
        //  4) Only absence from both live ownership and honest memory counts as confirmed gone.
        public static bool IsObjectiveSatisfiedLive(PlayerSetupData player, int targetArmyId)
        {
            if (player == null || targetArmyId == 0)
                return false;

            if (ArmyRegistry.AllForOwner(player)
                .Any(a => a != null && a.Id == targetArmyId && a.Members.Count > 0))
                return true;

            foreach (PlayerSetupData other in GameSession.Players ?? System.Linq.Enumerable.Empty<PlayerSetupData>())
            {
                if (other == null || other.Equals(player))
                    continue;
                if (ArmyRegistry.AllForOwner(other)
                    .Any(a => a != null && a.Id == targetArmyId && a.Members.Count > 0))
                    return false;
            }

            bool rememberedEnemy = AiMapMemory.AllKnownEnemySightings(player)
                .Any(s => s.ArmyId == targetArmyId);
            bool rememberedNeutral = AiMapMemory.AllKnownNeutralSightings(player)
                .Any(s => s.ArmyId == targetArmyId);
            return !rememberedEnemy && !rememberedNeutral;
        }

        private static PlayerSetupData SelfOwner(WorldSnapshot snap)
        {
            ArmySnapshot a = snap?.Self?.Armies?.FirstOrDefault(x => x != null && x.Owner != null);
            return a?.Owner;
        }
    }
}
