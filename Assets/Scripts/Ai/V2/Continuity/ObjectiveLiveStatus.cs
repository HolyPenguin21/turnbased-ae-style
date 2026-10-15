using System.Linq;
using Game.Core;
using Game.HexGrid;
using Game.Map;
using Game.Players;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  OBJECTIVE STATUS AGAINST THE LIVE WORLD (2026-10-15: moved here from the Strategy evaluators).
    //
    //  Planning judges objectives on the WorldSnapshot. These are the live counterparts, read
    //  between and after executed steps — by Continuity (ResolveActive / Reconcile and
    //  MissionRevalidator), Provisioning and the executors — where post-mutation state is the
    //  truth. The knowledge rules stay those of the owning evaluators (AiMapMemory for every
    //  foreign fact, AttackObjectiveEvaluator.StatusFromMemory for a site); only the data source is
    //  live: our own objects and our own vision through OwnWorld.
    // ===========================================================================================
    public static class ObjectiveLiveStatus
    {
        // ---- Scout ----------------------------------------------------------------------------

        // Explore is done only by physical ground visitation.
        public static bool ExploreSatisfied(PlayerSetupData player, HexCoord focus) =>
            OwnWorld.Of(player).IsVisited(focus);

        // Generic Refresh is an OBSERVATION objective. The focus was selected from stale frozen
        // IntelAge, so observing it again now is sufficient regardless of whether a ground unit
        // physically visits it. This preserves Observed != Visited and is future-compatible with
        // AirRecon refreshing information without claiming ground visitation.
        public static bool RefreshSatisfied(PlayerSetupData player, HexCoord focus) =>
            OwnWorld.Of(player).IsVisible(focus);

        // THE live "is this Scout job's objective met" dispatch, read by the post-execution ledger,
        // the ground and air executors and MissionRevalidator. AirSweep is never met by observation
        // (each sortie ends by its own refuel endurance).
        public static bool ScoutSatisfied(PlayerSetupData player, ScoutTargetKind kind, HexCoord focus)
        {
            switch (kind)
            {
                case ScoutTargetKind.CaptureStructure:
                    if (!OwnWorld.Of(player).IsVisible(focus)) return false;
                    BuildingData building = OwnWorld.Of(player).VisibleBuildingAt(focus);
                    return building == null || building.Owner == player;
                case ScoutTargetKind.Explore: return ExploreSatisfied(player, focus);
                case ScoutTargetKind.Refresh: return RefreshSatisfied(player, focus);
                default: return false;
            }
        }

        // ---- Raid -----------------------------------------------------------------------------

        // Objective completion must be POSITIVE, not inferred from one registry's absence.
        //  NeutralArmy:
        //   1) If the target id is now ours, it was captured -> satisfied.
        //   2) If any ordinary non-us player now fields it, it is no longer neutral and therefore
        //      no longer a legal Raid target -> satisfied for this campaign objective.
        //   3) If no ordinary roster resolves it but honest neutral memory still tracks it, this is
        //      the neutral/fog case -> not satisfied.
        //   4) Only absence from both live ownership and honest memory counts as confirmed gone.
        //  Step 2 reads OTHER players' live rosters: a live-tier fact (this class), never planning.
        //  EventGuard:
        //   Satisfied exactly when OUR memory confirms the event completed — by us (known at once)
        //   or by another player, learned only on a re-observation of the hex. Disappearance from
        //   visibility, or a global Consumed flag we have not observed, has no bearing.
        public static bool RaidSatisfied(PlayerSetupData player, RaidTargetRef target)
        {
            if (player == null || !target.HasValue)
                return false;

            if (target.Kind == RaidTargetKind.EventGuard)
                return AiMapMemory.KnownEventStateAt(player, target.Hex) == AiMapMemory.KnownEventState.Completed;

            int targetArmyId = target.ArmyId;
            if (OwnWorld.Of(player).Armies
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

        // ---- ActiveDefence --------------------------------------------------------------------

        // The ONE honest completion check for an Intercept objective, and the live counterpart of
        // Enumerate above. MissionRevalidator, MissionOutcomeLedger, Enumerate and
        // ActiveDefenceProvisioner must all apply the same knowledge rule: strategic knowledge of
        // an enemy army is AiMapMemory's (ARCH-02 canonical seams), and "absent from the world" is
        // never objective completion — a global ArmyRegistry sweep would let an intercept retire
        // because the army moved/died somewhere we cannot see, while the other stages still
        // consider it a live target, recreating and instantly "completing" the mission every pass.
        // Mirrors RaidSatisfied:
        //  1) the tracked id is fielded by US now — positive confirmation of the outcome of an
        //     action we ourselves completed (the one ArmyRegistry read the seam allows);
        //  2) honest memory still tracks it as a hostile, non-neutral army — NOT satisfied. It is
        //     exactly as real as our last observation says, fog included;
        //  3) honest memory now tracks it as a NEUTRAL army — this hostile-threat objective is
        //     over (Raid, not ActiveDefence, owns neutrals);
        //  4) honest memory no longer tracks it at all — either the hex was genuinely re-observed
        //     empty (AiMapMemory's own "corrected (gone on re-observation)" write) or a
        //     player-owned sighting aged out under the existing enemySightingMemoryTurns
        //     uncertainty policy. Both are "gone as far as we may honestly know"; neither invents
        //     a new lost-contact state, and Enumerate cannot re-admit an objective for an id that
        //     is no longer in Known.EnemySightings, so this can never oscillate.
        public static bool ActiveDefenceSatisfied(PlayerSetupData player, int enemyArmyId)
        {
            if (player == null)
                return false;
            if (OwnWorld.Of(player).Armies
                .Any(a => a != null && a.Id == enemyArmyId && a.Members.Count > 0))
                return true;
            foreach (AiMapMemory.KnownEnemySighting s in AiMapMemory.AllKnownEnemySightings(player))
                if (s.ArmyId == enemyArmyId)
                    return false;
            return true;
        }

        // ---- Attack ---------------------------------------------------------------------------

        // The SAME §25 question against LIVE state instead of a snapshot, for the one caller that
        // has no snapshot: MissionRevalidator's per-mission gate, which runs between provisioned
        // missions. The rules are identical and deliberately kept in this one owner rather than
        // reimplemented there — only the two data sources differ: our own current Base topology
        // (live truth about ourselves, exactly what Self.BaseHexes projects) and this observer's
        // own remembered buildings (never a live read of a FOREIGN owner, §19/§65).
        public static AttackObjectiveEvaluator.AttackTargetStatus AttackTarget(PlayerSetupData player,
            AttackTargetRef target)
        {
            if (!target.HasValue || player == null)
                return AttackObjectiveEvaluator.AttackTargetStatus.Invalidated;
            BuildingData live = OwnWorld.Of(player).BuildingAt(target.Hex);
            if (live != null && (live.IsBase || live.IsStartingCitadel))
                return AttackObjectiveEvaluator.AttackTargetStatus.Captured;

            return AttackObjectiveEvaluator.StatusFromMemory(target, AiMapMemory.KnownBuildingAt(player, target.Hex),
                OwnWorld.Of(player).HasEverSeen(target.Hex));
        }
    }
}
