using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Game.Aviation;
using Game.Combat;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Units;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  ATK §28/§46 — THE transactional ground-combat ASSAULT ASSEMBLY, shared by every lane that
    //  marches a ground force onto a defended hex.
    //
    //  Moved here verbatim from RaidProvisioner, which was its only caller. Nothing in it was ever
    //  Raid-specific: it resolves the host the ONE batch assignment picked, re-validates and applies
    //  the planned same-hex transfers atomically, prices and paths the roster that will ACTUALLY
    //  march (never the untouched host), and reconciles the real activation cost against the funded
    //  one — rolling the whole transaction back and reporting the mutation honestly if any of that
    //  diverges. A lane decides only WHICH fight this is and what the site's defence is worth; it
    //  never gets its own assembly, its own estimator or its own transaction.
    // ===========================================================================================
    internal sealed class GroundCombatAssaultRequest
    {
        public PlayerSetupData Player;
        public PlayerRoot Root;
        public AiTurnContext Ctx;
        public ProvisioningSession Session;
        public FundedEntry Funded;
        public StableMissionKey Key;
        public HexCoord TargetHex;
        public IReadOnlyList<WorthIt.DefenderProfile> Defenders =
            System.Array.Empty<WorthIt.DefenderProfile>();
        // §30 — the defence the DEFENDERS enjoy where this fight will happen. 0 for open ground;
        // an assault on a known Base/Citadel passes what honest memory observed.
        public float DefenderHexDefenseBonus;
        // Lane name for log lines and failure text only — never a behavioural switch.
        public string LaneLabel = "raid";
        public float Eps = AiConfigV2.allocatorSliceEpsilon;
    }

    internal sealed class GroundCombatAssaultOutcome
    {
        public bool Success;
        public ProvisioningResult Failure;
        public ArmyData Host;
        public GroundCombatAssemblyPlan Plan;
        // The reconciled cost of the force that actually exists now — asserted equal to the
        // projected/funded figure, so allocator, revalidator and executor all debit this one
        // number exactly once.
        public int ActualAp;
        public int AppliedTransfers;

        public static GroundCombatAssaultOutcome Failed(ProvisioningResult failure) =>
            new GroundCombatAssaultOutcome { Success = false, Failure = failure };
    }


    // ===========================================================================================
    //  ATK §28/§46/§47 — the two NON-assault ground-combat legs, shared by every lane.
    //
    //  Walking a mover home and bringing a support convoy to a primary are physical procedures with
    //  no lane semantics at all: resolve the actor, refuse a contended one, check it can actually
    //  take a first step, and check the activation AP fits both the mission envelope and the turn.
    //  Each lane still builds its OWN ProvisionedMission payload from the validated result, because
    //  that payload is where its semantics live.
    // ===========================================================================================
    internal sealed class GroundCombatLegCheck
    {
        public bool Ok;
        public ProvisioningResult Failure;
        public ArmyData Mover;
        public int ActivationAp;

        public static GroundCombatLegCheck Failed(ProvisioningResult failure) =>
            new GroundCombatLegCheck { Ok = false, Failure = failure };
    }

    internal static class GroundCombatLegChecks
    {
        private static string N(float v) => v.ToString("0.##", CultureInfo.InvariantCulture);

        // §47 — the mover walks to an ALREADY CHOSEN destination. This never re-picks the base:
        // Continuity owns that decision (MissionContinuityLayer.SelectReturnBase).
        internal static GroundCombatLegCheck ValidateWalkHome(PlayerSetupData player, PlayerRoot root,
            AiTurnContext ctx, ProvisioningSession session, FundedEntry funded, StableMissionKey key,
            float eps, int? moverArmyId, HexCoord home, string lane, string roleLabel)
        {
            if (!moverArmyId.HasValue)
                return GroundCombatLegCheck.Failed(ProvisioningResult.Fail(
                    ProvisionFailure.TargetInvalidated($"{lane} {roleLabel} return has no mover assigned")));
            ArmyData mover = AiV2Util.ResolveArmy(player, moverArmyId.Value);
            if (mover == null || mover.Owner != player || mover.Members.Count == 0
                || mover.IsPrison || mover.IsAirfield || AviationRules.IsAirArmy(mover))
                return GroundCombatLegCheck.Failed(ProvisioningResult.Fail(
                    ProvisionFailure.TargetInvalidated(
                        $"{lane} {roleLabel} return mover #{moverArmyId.Value} is no longer a usable field army")));
            if (session.ClaimedArmyIds.Contains(mover.Id))
                return GroundCombatLegCheck.Failed(ProvisioningResult.Fail(
                    ProvisionFailure.MoverContended(
                        $"{lane} {roleLabel} return mover #{mover.Id} was claimed by an earlier mission this cycle")));

            if (mover.Hex.Equals(home))
                return GroundCombatLegCheck.Failed(ProvisioningResult.Fail(
                    ProvisionFailure.TargetSatisfied(
                        $"{lane} {roleLabel} return mover #{mover.Id} is already home at ({home.Q},{home.R})")));
            if (mover.CurrentMovement <= 0)
                return GroundCombatLegCheck.Failed(ProvisioningResult.Fail(
                    ProvisionFailure.NoExecutableStep(
                        $"{lane} {roleLabel} return mover #{mover.Id} has no movement left")));
            if (SafeStepPathing.FindNextSafeStep(ctx.Map, mover, home) == null)
            {
                // Defensive re-check only; the frozen Analysis reachability fact
                // (ReturnBaseStillValid) already retargets a genuinely unreachable base at turn-start
                // reconciliation. This classifies the rare same-turn edge case distinctly from an
                // ordinary "blocked only this turn" retry.
                ArmySnapshot moverSnap = session.Snapshot?.Self?.Armies?
                    .FirstOrDefault(a => a != null && a.ArmyId == mover.Id);
                bool genuinelyUnreachable = moverSnap != null && moverSnap.IsStructuralRaidActor
                    && !moverSnap.ReachableOwnBaseHexes.Contains(home);
                return GroundCombatLegCheck.Failed(ProvisioningResult.Fail(genuinelyUnreachable
                    ? ProvisionFailure.DestinationUnreachable(
                        $"return base ({home.Q},{home.R}) has no safe route at all from "
                        + $"({mover.Hex.Q},{mover.Hex.R})")
                    : ProvisionFailure.NoExecutableStep(
                        $"no safe first step from ({mover.Hex.Q},{mover.Hex.R}) toward return base ({home.Q},{home.R})")));
            }

            int activationAp = mover.HasActivatedThisTurn ? 0 : mover.ActivationApCost;
            if (activationAp > funded.Tentative.Ap + eps)
                return GroundCombatLegCheck.Failed(ProvisioningResult.Fail(
                    ProvisionFailure.EnvelopeTooSmall(activationAp,
                        $"{lane} return needs {N(activationAp)} AP, envelope is {N(funded.Tentative.Ap)}")));
            if (activationAp > root.ActionPoints - session.ApClaimed + eps)
                return GroundCombatLegCheck.Failed(ProvisioningResult.Fail(
                    ProvisionFailure.MoverContended(
                        $"turn AP exhausted: {lane} return needs {N(activationAp)}")));

            return new GroundCombatLegCheck { Ok = true, Mover = mover, ActivationAp = activationAp };
        }

        // §46 — the support convoy leg. `atRendezvous` tells the caller whether this step is the
        // atomic same-hex roster handoff or another transit step toward the primary.
        internal static GroundCombatLegCheck ValidateReinforcement(PlayerSetupData player,
            PlayerRoot root, AiTurnContext ctx, ProvisioningSession session, FundedEntry funded,
            StableMissionKey key, float eps, ArmyData primary, int supportArmyId,
            IReadOnlyList<WorthIt.DefenderProfile> defenders, float defenderHexDefenseBonus,
            string lane, out bool atRendezvous)
        {
            atRendezvous = false;
            ArmyData support = AiV2Util.ResolveArmy(player, supportArmyId);
            if (support == null || support.Owner != player || support.Id == primary.Id
                || support.Members.Count == 0 || support.IsPrison || support.IsGarrison
                || support.IsAirfield || AviationRules.IsAirArmy(support))
                return GroundCombatLegCheck.Failed(ProvisioningResult.Fail(
                    ProvisionFailure.NoMoverExists(
                        $"{lane} reinforcement support #{supportArmyId} is not a separate mobile ground army")));
            if (session.ExcludedForGroundCombat(funded.Mission).Contains(support.Id))
                return GroundCombatLegCheck.Failed(ProvisioningResult.Fail(
                    ProvisionFailure.MoverContended(
                        $"{lane} reinforcement support #{support.Id} is claimed by another mission or leg")));

            HexCoord rendezvous = primary.Hex;
            atRendezvous = support.Hex.Equals(rendezvous);

            // Does the projected delivered roster actually improve the primary's odds? The SAME
            // WorthIt projection provisioning/execution will use, never a separate estimator.
            if (!GroundCombatReinforcement.ImprovesOdds(primary, support, defenders,
                    defenderHexDefenseBonus, out string why))
                return GroundCombatLegCheck.Failed(ProvisioningResult.Fail(
                    ProvisionFailure.AssemblyInfeasible(
                        $"{lane} reinforcement #{support.Id} -> #{primary.Id} would not improve the primary's odds: {why}")));

            if (!atRendezvous)
            {
                if (support.CurrentMovement <= 0)
                    return GroundCombatLegCheck.Failed(ProvisioningResult.Fail(
                        ProvisionFailure.NoExecutableStep(
                            $"{lane} reinforcement support #{support.Id} has no movement left")));
                if (SafeStepPathing.FindNextSafeStep(ctx.Map, support, rendezvous) == null)
                    return GroundCombatLegCheck.Failed(ProvisioningResult.Fail(
                        ProvisionFailure.NoExecutableStep(
                            $"no safe first step from ({support.Hex.Q},{support.Hex.R}) toward rendezvous "
                            + $"({rendezvous.Q},{rendezvous.R})")));
            }

            int activationAp = support.HasActivatedThisTurn ? 0 : support.ActivationApCost;
            if (activationAp > funded.Tentative.Ap + eps)
                return GroundCombatLegCheck.Failed(ProvisioningResult.Fail(
                    ProvisionFailure.EnvelopeTooSmall(activationAp,
                        $"{lane} reinforcement needs {N(activationAp)} AP, envelope is {N(funded.Tentative.Ap)}")));
            if (activationAp > root.ActionPoints - session.ApClaimed + eps)
                return GroundCombatLegCheck.Failed(ProvisioningResult.Fail(
                    ProvisionFailure.MoverContended(
                        $"turn AP exhausted: {lane} reinforcement needs {N(activationAp)}")));

            return new GroundCombatLegCheck { Ok = true, Mover = support, ActivationAp = activationAp };
        }
    }

    // ATK §28/§46 — reinforcement admission belongs to the GroundCombat kernel, not to a lane.
    // Both halves were private to the Raid provisioner; nothing in either is Raid-specific.
    internal static class GroundCombatReinforcement
    {
        // Would merging the support's transferable bodies into the primary raise its WorthIt win
        // chance against the current defenders, on the hex where the fight will happen? A convoy
        // that cannot help is never provisioned.
        internal static bool ImprovesOdds(ArmyData primary, ArmyData support,
            IReadOnlyList<WorthIt.DefenderProfile> defenders, float defenderHexDefenseBonus,
            out string why)
        {
            List<UnitData> sparable = SparableSupportBodies(support);
            List<WorthIt.DefenderProfile> primaryBodies = primary.Members
                .Where(u => u != null && !u.IsHero && !u.IsAviation)
                .Select(WorthIt.FromLiveUnit)
                .ToList();
            List<WorthIt.DefenderProfile> supportBodies = sparable
                .Select(WorthIt.FromLiveUnit)
                .ToList();
            int capacity = ArmyData.ComputeCapacity(primary.Members, primary.IsGarrison);
            return GroundCombatAssemblyPlanner.TryProjectReinforcement(
                primaryBodies, supportBodies, capacity, primary.Members.Count,
                defenders, out _, out why, defenderHexDefenseBonus);
        }

        // A support container is never emptied and never gives up its own hero.
        internal static List<UnitData> SparableSupportBodies(ArmyData support)
        {
            var list = new List<UnitData>();
            if (support == null)
                return list;
            foreach (UnitData u in support.Members
                .Where(x => x != null && !x.IsHero && !x.IsAviation)
                .OrderByDescending(GroundCombatDonorPolicy.UnitCombatValue)
                .ThenBy(x => x.Name))
            {
                if (support.Members.Count - list.Count <= 1)
                    break;  // minimum-body invariant: leave at least one member behind
                if (!support.CanLeaveWithoutOvercrowding(u))
                    continue;
                list.Add(u);
            }
            return list;
        }
    }

    internal static class GroundCombatAssaultTransactionRunner
    {
        // ATK §28/§45 — the ONE "re-plan exactly the actor the batch assignment picked" rule. It
        // lived in the Raid lane only because Raid was the first lane built on the shared batch
        // solve; nothing in it is Raid-specific.
        // A single binding path for assault, used by both the real Provision method and
        // regression tests. The batch solver owns actor identity; the combat assembly kernel
        // owns feasibility of THAT actor and donors, never a replacement actor search.
        internal static GroundCombatAssemblyPlan PlanAssignedAssault(ProvisioningSession session,
            MissionProposal proposal, IReadOnlyList<WorthIt.DefenderProfile> defenders,
            out ProvisionFailure failure, float defenderHexDefenseBonus = 0f, string lane = "raid")
        {
            failure = default;
            StableMissionKey key = StableMissionKey.For(proposal);
            if (!session.TryGetAssignedGroundCombatActor(key, out int actorId))
            {
                failure = ProvisionFailure.MoverContended(
                    $"{lane} {key} has no actor in the shared ground-combat assignment");
                return null;
            }

            HashSet<int> excluded = session.ExcludedForGroundCombat(proposal);
            if (excluded.Contains(actorId))
            {
                failure = ProvisionFailure.MoverContended(
                    $"{lane} {key} assigned actor #{actorId} is claimed by another mission");
                return null;
            }

            // Keep the strict gate for fresh actors and the bounded continuation floor for
            // the same Hard incumbent. Unlike PlanForArmy, this request can also assemble
            // a legal same-hex roster, but may never re-select a different primary.
            GroundCombatAssemblyPlan plan = GroundCombatAssemblyPlanner.Plan(session.Snapshot,
                new GroundCombatAssemblyRequest
                {
                    Defenders = defenders,
                    PreferredPrimaryArmyId = actorId,
                    PinToPreferred = true,
                    ExcludedArmyIds = excluded,
                    // New operations keep the strict fresh gate; only a pinned Hard
                    // incumbent may use the existing bounded continuation floor.
                    WinChanceGate = proposal.FromDurableIntent
                        && proposal.DurableFundingTier == CommitmentTier.Hard
                        && proposal.PreferredMoverArmyId == actorId
                        ? GroundCombatAdmissionPolicy.ContinuationWinChanceFloor
                        : GroundCombatAdmissionPolicy.FreshStartWinChanceGate,
                    DefenderHexDefenseBonus = defenderHexDefenseBonus,
                });
            if (!plan.Feasible)
            {
                // The actor was admitted by the strict proposal-side registry. Rejection now
                // is transient (e.g. a donor became unavailable), not target infeasibility.
                failure = ProvisionFailure.MoverContended(
                    $"{lane} {key} assigned actor #{actorId} / eligible donors unavailable: {plan.Reason}");
                return null;
            }
            return plan;
        }

        internal static GroundCombatAssaultOutcome Run(GroundCombatAssaultRequest r)
        {
            PlayerSetupData player = r.Player;
            AiTurnContext ctx = r.Ctx;
            ProvisioningSession session = r.Session;
            FundedEntry funded = r.Funded;
            MissionProposal m = funded.Mission;
            StableMissionKey key = r.Key;
            HexCoord targetHex = r.TargetHex;
            IReadOnlyList<WorthIt.DefenderProfile> defenders = r.Defenders
                ?? System.Array.Empty<WorthIt.DefenderProfile>();
            string lane = r.LaneLabel;
            float eps = r.Eps;

            // Assault actor ownership is decided once by PrepareGroundCombatAssignments. Do not
            // re-run a FREE army search here: re-plan ONLY the assigned host, through the same
            // ExcludedForGroundCombat ownership view the batch solver used, so a mission can never
            // steal a durable Economy/Recon/Raid actor after the batch solver correctly rejected it.
            GroundCombatAssemblyPlan plan = PlanAssignedAssault(session, m,
                defenders, out ProvisionFailure assignmentFailure, r.DefenderHexDefenseBonus, lane);
            if (plan == null)
                return GroundCombatAssaultOutcome.Failed(ProvisioningResult.Fail(assignmentFailure));

            ArmyData host = AiV2Util.ResolveArmy(player, plan.BaseArmyId);
            if (host == null || host.Members.Count == 0 || host.CurrentMovement <= 0
                || host.IsPrison || host.IsAirfield || AviationRules.IsAirArmy(host)
                || AiArmyRoles.IsSoloRecce(host) || AiArmyRoles.IsSoloHeroAwaitingEscort(host))
                return GroundCombatAssaultOutcome.Failed(ProvisioningResult.Fail(
                    ProvisionFailure.MoverContended(
                        $"{lane} host #{plan.BaseArmyId} is no longer a usable ground combat army")));
            if (host.Owner != player || session.ClaimedArmyIds.Contains(host.Id))
                return GroundCombatAssaultOutcome.Failed(ProvisioningResult.Fail(
                    ProvisionFailure.MoverContended(
                        $"{lane} host #{plan.BaseArmyId} was claimed by an earlier mission this cycle")));

            var transfers = new List<GroundCombatAssemblyTransfer>();
            var claimedDonors = new HashSet<int>();
            var projectedUnits = new List<UnitData>(host.Members);
            if (plan.NeedsAssembly)
            {
                int heroTransfers = 0;
                foreach (GroundCombatAssemblyTransfer t in plan.Transfers)
                {
                    if (t?.Unit == null)
                        return GroundCombatAssaultOutcome.Failed(ProvisioningResult.Fail(
                            ProvisionFailure.AssemblyInfeasible($"{lane} assembly contains a null unit")));
                    ArmyData donor = AiV2Util.ResolveArmy(player, t.DonorArmyId);
                    if (donor == null || donor.Members.Count <= 1 || !donor.Hex.Equals(host.Hex))
                        return GroundCombatAssaultOutcome.Failed(ProvisioningResult.Fail(
                            ProvisionFailure.AssemblyInfeasible(
                                $"{lane} donor #{t.DonorArmyId} is gone, moved, or would be emptied")));
                    if (session.ClaimedArmyIds.Contains(donor.Id))
                        return GroundCombatAssaultOutcome.Failed(ProvisioningResult.Fail(
                            ProvisionFailure.MoverContended(
                                $"{lane} donor #{donor.Id} was claimed by an earlier mission this cycle")));
                    bool unitIsHero = t.Unit.IsHero;
                    if (unitIsHero && (++heroTransfers > 1 || projectedUnits.Any(u => u != null && u.IsHero)))
                        return GroundCombatAssaultOutcome.Failed(ProvisioningResult.Fail(
                            ProvisionFailure.AssemblyInfeasible(
                                $"{lane} host #{host.Id} may take at most one hero and only when heroless")));
                    if (donor.IsPrison || donor.IsAirfield || AviationRules.IsAirArmy(donor)
                        || AiArmyRoles.IsSoloRecce(donor) || !donor.Members.Contains(t.Unit)
                        || t.Unit.IsAviation)
                        return GroundCombatAssaultOutcome.Failed(ProvisioningResult.Fail(
                            ProvisionFailure.AssemblyInfeasible(
                                $"{lane} donor #{donor.Id} / unit {t.Unit.Name} is no longer legal")));
                    if (!donor.CanLeaveWithoutOvercrowding(t.Unit)
                        || (donor.IsGarrison && !AiArmyRoles.CanSpareGarrisonMember(player, donor, t.Unit)))
                        return GroundCombatAssaultOutcome.Failed(ProvisioningResult.Fail(
                            ProvisionFailure.AssemblyInfeasible(
                                $"{lane} donor #{donor.Id} can no longer spare {t.Unit.Name}")));
                    if (host.HasActivatedThisTurn && t.Unit.ActivationApCost > 0)
                        return GroundCombatAssaultOutcome.Failed(ProvisioningResult.Fail(
                            ProvisionFailure.AssemblyInfeasible(
                                $"adding {t.Unit.Name} to activated {lane} host would spend unbudgeted AP")));

                    var withU = new List<UnitData>(projectedUnits) { t.Unit };
                    if (ArmyData.ComputeCapacity(withU, host.IsGarrison) < withU.Count)
                        return GroundCombatAssaultOutcome.Failed(ProvisioningResult.Fail(
                            ProvisionFailure.AssemblyInfeasible(
                                $"{lane} host #{host.Id} no longer has capacity for planned assembly")));
                    projectedUnits.Add(t.Unit);
                    transfers.Add(t);
                    claimedDonors.Add(donor.Id);
                }

                List<WorthIt.DefenderProfile> projectedProfiles =
                    projectedUnits.Select(WorthIt.FromLiveUnit).ToList();
                foreach (IGrouping<int, GroundCombatAssemblyTransfer> group in transfers.GroupBy(t => t.DonorArmyId))
                {
                    ArmyData donor = AiV2Util.ResolveArmy(player, group.Key);
                    List<UnitData> units = group.Select(t => t.Unit).ToList();
                    if (donor == null || donor.Members.Count - units.Count < 1
                        || donor.IsGarrison
                            && !AiArmyRoles.CanSpareGarrisonMembers(player, donor, units))
                        return GroundCombatAssaultOutcome.Failed(ProvisioningResult.Fail(
                            ProvisionFailure.AssemblyInfeasible(
                                $"{lane} donor #{group.Key} cannot spare the complete planned batch")));
                }
                // §29 — the site's own defence is part of THIS fight, so the pre-mutation re-check
                // must ask the estimator the same question the plan was admitted on.
                if (!GroundCombatFeasibility.Clears(projectedProfiles, defenders,
                        AiConfigV2.raidMinViableWinChance, r.DefenderHexDefenseBonus,
                        out float projectedWin, out _))
                    return GroundCombatAssaultOutcome.Failed(ProvisioningResult.Fail(
                        ProvisionFailure.AssemblyInfeasible(
                            "planned same-hex roster no longer clears the shared WorthIt estimator")));
                plan.ProjectedWinChance = projectedWin;
            }

            // AI-01 — every check below is priced against `projectedUnits`, the roster that will
            // actually march, and every one of them runs BEFORE the first ArmyActions.TransferMember
            // call. Reachability too: a recruit slower than the host lowers the whole army's shared
            // movement (ArmyData.ComputeCurrentMovement), so the first step must be re-asked with
            // the projected movement rather than the host's own.
            if (SafeStepPathing.FindNextSafeStepForRoster(ctx.Map, host, targetHex, projectedUnits) == null)
                return GroundCombatAssaultOutcome.Failed(ProvisioningResult.Fail(
                    ProvisionFailure.NoExecutableStep(
                        $"no safe first step from ({host.Hex.Q},{host.Hex.R}) toward {lane} target "
                        + $"({targetHex.Q},{targetHex.R})"
                        + (plan.NeedsAssembly ? " for the projected assembled roster" : ""))));

            int activationAp = host.ProjectedActivationApCost(projectedUnits);
            float envelope = funded.Tentative.Ap;
            if (activationAp > envelope + eps)
                return GroundCombatAssaultOutcome.Failed(ProvisioningResult.Fail(
                    ProvisionFailure.EnvelopeTooSmall(activationAp,
                        $"{lane} host #{host.Id} needs {N(activationAp)} AP for its projected "
                        + $"{projectedUnits.Count}-body roster, envelope is {N(envelope)}")));
            float turnApLeft = r.Root.ActionPoints - session.ApClaimed;
            if (activationAp > turnApLeft + eps)
                return GroundCombatAssaultOutcome.Failed(ProvisioningResult.Fail(
                    ProvisionFailure.MoverContended(
                        $"turn AP exhausted: {lane} needs {N(activationAp)}, {N(turnApLeft)} left")));

            var applied = new List<GroundCombatAssemblyTransfer>();
            foreach (GroundCombatAssemblyTransfer t in transfers)
            {
                ArmyData donor = AiV2Util.ResolveArmy(player, t.DonorArmyId);
                string why = donor == null ? "donor missing" : null;
                if (donor == null || !ArmyActions.TransferMember(t.Unit, donor, host, ctx.HexSelection, out why))
                {
                    bool rollbackOk = GroundCombatAssemblyTransaction.Rollback(player, host, applied, ctx, lane);
                    int transfersStillApplied = GroundCombatAssemblyTransaction.RemainingApplied(host, applied);
                    bool rollbackChangedWorld = transfersStillApplied > 0;
                    AiDebugLog.Write($"[AI][V2]   {lane} provision [{m.AttemptId}] {key} — assembly transaction failed on "
                        + $"{t.Unit.Name} from #{t.DonorArmyId}: {why}; rollback={(rollbackOk ? "OK" : "FAILED")}; "
                        + $"remainingTransfers={transfersStillApplied}");
                    return GroundCombatAssaultOutcome.Failed(ProvisioningResult.Fail(
                        ProvisionFailure.AssemblyInfeasible(rollbackOk
                            ? $"atomic {lane} assembly rejected: {why}"
                            : $"{lane} assembly failed and rollback was incomplete: {why}"),
                        rollbackChangedWorld, transfersStillApplied));
                }
                applied.Add(t);
            }

            // AI-01 — the assembled force must cost exactly what was projected and funded. Any
            // divergence is a failure, never a partial success: roll the transaction back and let
            // the existing repack/reprice loop re-decide with honest numbers.
            int actualAp = host.ProjectedActivationApCost(host.Members);
            if (actualAp != activationAp || actualAp > envelope + eps)
            {
                bool reconcileRollbackOk = GroundCombatAssemblyTransaction.Rollback(player, host, applied, ctx, lane);
                int stillApplied = GroundCombatAssemblyTransaction.RemainingApplied(host, applied);
                AiDebugLog.Write($"[AI][V2]   {lane} provision [{m.AttemptId}] {key} — assembled host #{host.Id} "
                    + $"costs {N(actualAp)} AP but {N(activationAp)} was projected/funded "
                    + $"(envelope {N(envelope)}); rollback={(reconcileRollbackOk ? "OK" : "FAILED")}; "
                    + $"remainingTransfers={stillApplied}");
                return GroundCombatAssaultOutcome.Failed(ProvisioningResult.Fail(
                    ProvisionFailure.EnvelopeTooSmall(actualAp,
                        $"{lane} host #{host.Id} reconciled activation {N(actualAp)} AP diverges from the "
                        + $"projected {N(activationAp)} AP"),
                    stillApplied > 0, stillApplied));
            }

            foreach (int d in claimedDonors)
                session.ClaimedArmyIds.Add(d);

            AiDebugLog.Write($"[AI][V2]   {lane} provision [{m.AttemptId}] {key} — OK host #{host.Id} "
                + $"{(plan.NeedsAssembly ? $"(+{transfers.Count} body from {claimedDonors.Count} donor) " : "")}"
                + $"win~{plan.ProjectedWinChance.ToString("0.00", CultureInfo.InvariantCulture)} "
                + $"ap {N(actualAp)} (projected {N(activationAp)}) -> ({targetHex.Q},{targetHex.R})");

            return new GroundCombatAssaultOutcome
            {
                Success = true,
                Host = host,
                Plan = plan,
                ActualAp = actualAp,
                AppliedTransfers = applied.Count,
            };
        }

        private static string N(float v) => v.ToString("0.##", CultureInfo.InvariantCulture);
    }
}
