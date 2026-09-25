namespace Game.Ai.V2
{
    // ===========================================================================================
    //  MISSION ADMISSION POLICY  (Strategy V2 build-order step 7.1)
    // ===========================================================================================
    //  The SINGLE owner of execution-side pairwise admission rules. It never binds an actor;
    //  ProvisioningManager remains authoritative. Physical actor metadata is proposal-side only
    //  and is used here to reject portfolios that are already provably impossible.
    // ===========================================================================================
    public enum ExecutionLane
    {
        None,
        Recon,
        Aggression,
        Economy,
        Development,
    }

    internal static class MissionAdmissionPolicy
    {
        public static ExecutionLane LaneFor(MissionProposal mission)
        {
            if (mission == null) return ExecutionLane.None;
            switch (mission.Kind)
            {
                case MissionKind.Scout: return ExecutionLane.Recon;
                // ATK §22 — Attack is a third Aggression-lane mission, not a lane of its own.
                case MissionKind.Raid:
                case MissionKind.ActiveDefence:
                case MissionKind.Attack: return ExecutionLane.Aggression;
                case MissionKind.Economy: return ExecutionLane.Economy;
                case MissionKind.Development: return ExecutionLane.Development;
                default: return ExecutionLane.None;
            }
        }

        public static int Capacity(ExecutionLane lane)
        {
            switch (lane)
            {
                case ExecutionLane.Recon:
                    // Ground-vs-air concurrency is finalized by ReconAssignmentPlanner where the
                    // executor kind is known. Actor-priced ground proposals are nevertheless allowed
                    // to participate in generic pairwise compatibility below before funding.
                    return int.MaxValue;
                case ExecutionLane.Aggression:
                    return int.MaxValue;
                case ExecutionLane.Economy:
                case ExecutionLane.Development:
                    return int.MaxValue;
                default:
                    return int.MaxValue;
            }
        }

        // A proposal-side actor is a PLANNING WITNESS: the actor whose real current-turn envelope
        // was used before funding. It is not a binding operation here; Assignment/Provisioning can
        // still rematch if live facts change. Durable intents naturally publish their continuity
        // mover through the same field. Economy also carries the builder in its typed target, so a
        // legacy/test proposal that omitted PreferredMover still exposes its concrete builder.
        private static int? PlannedActor(MissionProposal mission)
        {
            if (mission == null)
                return null;
            if (mission.PreferredMoverArmyId.HasValue)
                return mission.PreferredMoverArmyId;
            if (mission.Kind == MissionKind.Economy
                && mission.Target is EconomyMissionTarget economy
                && economy.BuilderArmyId.HasValue)
                return economy.BuilderArmyId;
            if (mission.Kind == MissionKind.Development
                && mission.Target is DevelopmentMissionTarget development)
                return development.SourceArmyId;
            return null;
        }

        // Pairwise execution conflicts. This is the ONE generic portfolio-compatibility owner:
        // objective identity stays kind-specific, while a concrete actor-priced plan is exclusive
        // across Recon/Economy/Raid lanes before money is committed. The rule does not assign an
        // actor; it only refuses two proposals that both advertise the same already-priced actor.
        public static bool Conflicts(MissionProposal a, MissionProposal b)
        {
            if (a == null || b == null) return false;

            if (a.Kind == MissionKind.Raid && b.Kind == MissionKind.Raid
                && a.Target is RaidMissionTarget ra && b.Target is RaidMissionTarget rb
                && ra.Target.HasValue && rb.Target.HasValue && ra.Target.Equals(rb.Target))
                return true;

            if (a.Kind == MissionKind.Attack && b.Kind == MissionKind.Attack
                && a.Target is AttackMissionTarget aaTarget && b.Target is AttackMissionTarget abTarget
                && aaTarget.Target.HasValue && abTarget.Target.HasValue
                && aaTarget.Target.Equals(abTarget.Target))
                // Audit F7 — parallel Gather legs of one operation walk DIFFERENT supports to the
                // same host: complementary work, funded together, never one-per-pass.
                return !(aaTarget.Phase == AttackMissionPhase.Gather
                    && abTarget.Phase == AttackMissionPhase.Gather
                    && aaTarget.SupportArmyId != abTarget.SupportArmyId);

            if (UsesGroundCombatAssignmentRegistry(a) && UsesGroundCombatAssignmentRegistry(b))
            {
                if (!GroundCombatAdmissionRegistry.PairHasDistinctAssignment(a, b))
                    return true;
                return false;
            }

            if (a.Kind == MissionKind.Economy && b.Kind == MissionKind.Economy
                && a.Target is EconomyMissionTarget ea && b.Target is EconomyMissionTarget eb
                && ea.TargetHex.Equals(eb.TargetHex))
                return true;

            if (a.Kind == MissionKind.Development && b.Kind == MissionKind.Development
                && a.Target is DevelopmentMissionTarget da && b.Target is DevelopmentMissionTarget db
                && ((da.Mode == db.Mode && da.FacilityHex.Equals(db.FacilityHex))
                    || ReferenceEquals(da.Hero, db.Hero)))
                return true;

            if (a.Target is ScoutMissionTarget ta && b.Target is ScoutMissionTarget tb
                && ta.FocusHex.Equals(tb.FocusHex))
                return true;

            int? aa = PlannedActor(a);
            int? ba = PlannedActor(b);
            return aa.HasValue && ba.HasValue && aa.Value == ba.Value;
        }

        private static bool UsesGroundCombatAssignmentRegistry(MissionProposal mission)
        {
            if (mission?.Target is RaidMissionTarget raid)
                return raid.Phase == RaidMissionPhase.Assault
                    || raid.Phase == RaidMissionPhase.Reinforcement;
            if (mission?.Target is AttackMissionTarget attack)
                return attack.Phase == AttackMissionPhase.Assault
                    || attack.Phase == AttackMissionPhase.Reinforcement;
            return false;
        }

        public static float AdmissionRank(MissionProposal m)
        {
            if (m == null) return 0f;
            return AdmissionRank(m.LocalAdmissionScore,
                m.FromDurableIntent, m.DurableFundingTier);
        }

        public static float AdmissionRank(float localScore, bool fromDurableIntent, CommitmentTier tier)
        {
            if (fromDurableIntent && tier == CommitmentTier.None)
                return localScore * (1f + AiConfigV2.commitmentRetargetMargin);
            return localScore;
        }
    }
}
