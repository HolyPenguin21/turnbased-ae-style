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
    //    · IsObjectiveSatisfied — has the target been destroyed / captured / turned non-hostile /
    //                             consumed? (live read — the ledger's post-execution pass)
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
    //
    //  EVENT GUARDS behave differently by nature, not by a weaker rule: they have no ArmyId sighting
    //  to lose track of, so "no sighting" never applies to them. They are active exactly while
    //  HexEventRegistry.HasActiveEvent(hex) says so, and satisfied exactly when that entry is
    //  Consumed (by us or by anyone else) — never inferred from map-memory absence.
    // ===========================================================================================
    public static class RaidObjectiveEvaluator
    {
        // ---- SNAPSHOT (continuity / mission-layer re-materialisation) -----------------------

        // Is the tracked target still a coherent thing to raid? Reads ONLY the snapshot's honest
        // sightings for a NeutralArmy target, or the live (but universally visible) event registry
        // for an EventGuard target — never a live army-ownership system.
        public static bool IsIntentStillValid(WorldSnapshot snap, RaidIntent intent)
        {
            if (snap?.Known == null || intent == null || !intent.Target.HasValue)
                return false;

            if (intent.Target.Kind == RaidTargetKind.EventGuard)
                return HexEventRegistry.HasActiveEvent(intent.Target.Hex);

            AiMapMemory.KnownEnemySighting? s = FindSighting(snap, intent.Target.ArmyId);
            if (s == null)
                // No current honest sighting. Keep the intent alive as long as it has actually
                // started (a Hard raid in transit must not evaporate the turn the target slips
                // into fog) — MissionContinuityLayer's stall / age caps still reap a raid that
                // never re-acquires. An unstarted intent with no sighting is dropped.
                return intent.OperationStarted;

            // AGG-RAID P0#2 — Raid targets NEUTRALS ONLY (Active Defence, not yet built, owns enemy
            // armies). A target that turned into ANY non-neutral player's army — ours included —
            // ends this objective; the old check only ever asked "not ours", so a neutral that
            // flipped to a THIRD player's ownership mid-Raid was silently accepted as still valid.
            return IsNeutralRaidTarget(s.Value.Owner);
        }

        // AGG-RAID P0#2 — the ONE canonical "is this still a legal Raid target" ownership check.
        // A null owner is an unclaimed neutral encounter army. Everything downstream (Provisioning,
        // Execution) must call THIS, not re-derive its own neutrality rule.
        public static bool IsNeutralRaidTarget(PlayerSetupData owner) => owner == null || owner.IsNeutral;

        // Snapshot-pure completion edge for the campaign phase machine. Loss of sight is UNKNOWN;
        // a present sighting with a non-neutral owner is positive proof that this target no longer
        // belongs to Raid and must trigger next-neutral/Return handling. An event guard can never
        // change owner (it has no owner concept until spawned, and is torn down on defeat) — always
        // false for that kind.
        public static bool IsKnownTargetNoLongerNeutral(WorldSnapshot snap, RaidTargetRef target)
        {
            if (!target.HasValue || target.Kind == RaidTargetKind.EventGuard)
                return false;
            AiMapMemory.KnownEnemySighting? sighting = FindSighting(snap, target.ArmyId);
            return sighting.HasValue && !IsNeutralRaidTarget(sighting.Value.Owner);
        }

        // The tracked target's freshest honest sighting, or null. Physical-army lookup only —
        // callers must branch on RaidTargetKind BEFORE calling this for an EventGuard target.
        public static AiMapMemory.KnownEnemySighting? FindSighting(WorldSnapshot snap, int trackedArmyId)
        {
            if (snap?.Known == null)
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
        //  NeutralArmy:
        //   1) If the target id is now ours, it was captured -> satisfied.
        //   2) If any ordinary non-us player now fields it, it is no longer neutral and therefore
        //      no longer a legal Raid target -> satisfied for this campaign objective.
        //   3) If no ordinary roster resolves it but honest neutral memory still tracks it, this is
        //      the neutral/fog case -> not satisfied.
        //   4) Only absence from both live ownership and honest memory counts as confirmed gone.
        //  EventGuard:
        //   Satisfied exactly when HexEventRegistry marks that hex's entry Consumed — whether by us
        //   (victory) or by another player who explored it first. Disappearance from visibility has
        //   no bearing; the registry entry is the single source of truth.
        public static bool IsObjectiveSatisfiedLive(PlayerSetupData player, RaidTargetRef target)
        {
            if (player == null || !target.HasValue)
                return false;

            if (target.Kind == RaidTargetKind.EventGuard)
            {
                HexEventRegistry.Entry entry = HexEventRegistry.FindAt(target.Hex);
                return entry != null && entry.Consumed;
            }

            int targetArmyId = target.ArmyId;
            if (ArmyRegistry.AllForOwner(player)
                .Any(a => a != null && a.Id == targetArmyId && a.Members.Count > 0))
                return true;

            foreach (PlayerSetupData other in GameSession.Players ?? System.Linq.Enumerable.Empty<PlayerSetupData>())
            {
                if (other == null || other.Equals(player))
                    continue;
                if (ArmyRegistry.AllForOwner(other)
                    .Any(a => a != null && a.Id == targetArmyId && a.Members.Count > 0))
                    return true;
            }

            bool rememberedEnemy = AiMapMemory.AllKnownEnemySightings(player)
                .Any(s => s.ArmyId == targetArmyId);
            bool rememberedNeutral = AiMapMemory.AllKnownNeutralSightings(player)
                .Any(s => s.ArmyId == targetArmyId);
            return !rememberedEnemy && !rememberedNeutral;
        }
    }
}
