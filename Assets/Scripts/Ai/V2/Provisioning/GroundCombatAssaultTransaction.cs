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
        // The opposition this fight is against (every defending army with its commander).
        public IReadOnlyList<WorthIt.DefendingArmy> Opposition =
            System.Array.Empty<WorthIt.DefendingArmy>();
        // §30 — the defence the DEFENDERS enjoy where this fight will happen. 0 for open ground;
        // an assault on a known Base/Citadel passes what honest memory observed.
        public float DefenderHexDefenseBonus;
        // Lane name for log lines and failure text only — never a behavioural switch.
        public string LaneLabel = "raid";
        public float Eps = AiConfigV2.allocatorSliceEpsilon;
        // A lane-specific last proof on the roster that will really march (host + planned recruits,
        // after shedding), run BEFORE any paid mutation. Returns a refusal reason, or null to go on.
        public System.Func<ArmyData, IReadOnlyList<UnitData>, string> PreMutationVeto;
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
        // The host's commander was promoted for this fight: a world mutation of its own.
        public bool CommanderReordered;

        public static GroundCombatAssaultOutcome Failed(ProvisioningResult failure) =>
            new GroundCombatAssaultOutcome { Success = false, Failure = failure };
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
            MissionProposal proposal, IReadOnlyList<WorthIt.DefendingArmy> opposition,
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
            // A durable incumbent resolves its actor by id, outside the nomination filter: the
            // stronghold-hold rule is re-checked here so no assault leg takes the last defender
            // off a threatened own Citadel/Base.
            ArmySnapshot assigned = session.Snapshot?.Self?.Armies?.FirstOrDefault(a => a != null
                && a.ArmyId == actorId);
            if (ActiveDefenceObjectiveEvaluator.IsPinnedStrongholdDefender(session.Snapshot, assigned))
            {
                failure = ProvisionFailure.MoverContended(
                    $"{lane} {key} assigned actor #{actorId} is the last defender of a threatened stronghold");
                return null;
            }

            // GroundCombatAdmissionPolicy.AssaultGate picks the gate (Attack's floor; the strict
            // gate for fresh actors and the bounded continuation floor for the same Hard
            // incumbent). Unlike PlanForArmyAtThreshold, this request can also assemble a legal same-hex
            // roster, but may never re-select a different primary.
            GroundCombatAssemblyPlan plan = GroundCombatAssemblyPlanner.Plan(session.Snapshot,
                new GroundCombatAssemblyRequest
                {
                    Opposition = opposition,
                    PreferredPrimaryArmyId = actorId,
                    PinToPreferred = true,
                    ExcludedArmyIds = excluded,
                    WinChanceGate = GroundCombatAdmissionPolicy.AssaultGate(proposal, actorId),
                    RequireCoverage = GroundCombatAdmissionPolicy.AssaultCoverage(proposal),
                    AllowSameHexAssembly = !(proposal.Target is AttackMissionTarget local && local.IsIntermediateAssault),
                    PreserveDepartureRoster = proposal.Target is AttackMissionTarget committed && committed.ForceCommitted,
                    MinimumArmyPower = proposal.Target is AttackMissionTarget attackTarget
                        && !attackTarget.ForceCommitted
                        ? AttackForceReadiness.RequiredPower(session.Snapshot.Self.AttackPeak) : 0f,
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
            IReadOnlyList<WorthIt.DefendingArmy> opposition = r.Opposition
                ?? System.Array.Empty<WorthIt.DefendingArmy>();
            string lane = r.LaneLabel;
            float eps = r.Eps;

            // Assault actor ownership is decided once by PrepareGroundCombatAssignments. Do not
            // re-run a FREE army search here: re-plan ONLY the assigned host, through the same
            // ExcludedForGroundCombat ownership view the batch solver used, so a mission can never
            // steal a durable Economy/Recon/Raid actor after the batch solver correctly rejected it.
            GroundCombatAssemblyPlan plan = PlanAssignedAssault(session, m,
                opposition, out ProvisionFailure assignmentFailure, r.DefenderHexDefenseBonus, lane);
            if (plan == null)
                return GroundCombatAssaultOutcome.Failed(ProvisioningResult.Fail(assignmentFailure));

            ArmyData host = AiV2Util.ResolveArmy(player, plan.BaseArmyId);
            if (host == null || host.Members.Count == 0 || host.CurrentMovement <= 0
                || host.IsPrison || host.IsAirfield || AviationRules.IsAirArmy(host)
                || AiArmyRoles.IsSoloRecce(host)
                || AiArmyRoles.IsSoloHeroAwaitingEscort(host) && !plan.NeedsAssembly)
                return GroundCombatAssaultOutcome.Failed(ProvisioningResult.Fail(
                    ProvisionFailure.MoverContended(
                        $"{lane} host #{plan.BaseArmyId} is no longer a usable ground combat army")));
            if (host.Owner != player || session.ClaimedArmyIds.Contains(host.Id))
                return GroundCombatAssaultOutcome.Failed(ProvisioningResult.Fail(
                    ProvisionFailure.MoverContended(
                        $"{lane} host #{plan.BaseArmyId} was claimed by an earlier mission this cycle")));

            var transfers = new List<GroundCombatAssemblyTransfer>();
            var claimedDonors = new HashSet<int>();
            // Consume the planner's departure roster. Provisioning may revalidate it, never
            // invent a different hero-shedding policy after its budget was already allocated.
            var shed = new List<UnitData>(plan.ReturnHeroes);
            ArmyData shedGarrison = plan.HeroReturnArmyId.HasValue
                ? AiV2Util.ResolveArmy(player, plan.HeroReturnArmyId.Value) : null;
            if (shed.Count > 0 && !GroundCombatAssemblyPlanner.CanApplyHeroReturn(
                host, shedGarrison, plan.Commander, shed))
                return GroundCombatAssaultOutcome.Failed(ProvisioningResult.Fail(
                    ProvisionFailure.MoverContended($"{lane} host #{host.Id} hero-return plan became stale")));
            var projectedUnits = host.Members.Where(u => !shed.Contains(u)).ToList();
            if (plan.Commander != null && projectedUnits.Remove(plan.Commander))
                projectedUnits.Insert(0, plan.Commander);
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
                    if (!ArmyData.RosterFits(withU, host.IsGarrison))
                        return GroundCombatAssaultOutcome.Failed(ProvisioningResult.Fail(
                            ProvisionFailure.AssemblyInfeasible(
                                $"{lane} host #{host.Id} no longer has capacity for planned assembly")));
                    projectedUnits.Add(t.Unit);
                    transfers.Add(t);
                    claimedDonors.Add(donor.Id);
                }
                foreach (IGrouping<int, GroundCombatAssemblyTransfer> group in transfers.GroupBy(t => t.DonorArmyId))
                {
                    ArmyData donor = AiV2Util.ResolveArmy(player, group.Key);
                    List<UnitData> units = group.Select(t => t.Unit).ToList();
                    if (donor == null || donor.Members.Count - units.Count < 1
                        || donor.IsGarrison && !AiArmyRoles.CanSpareGarrisonMembers(player, donor, units))
                        return GroundCombatAssaultOutcome.Failed(ProvisioningResult.Fail(
                            ProvisionFailure.AssemblyInfeasible(
                                $"{lane} donor #{group.Key} cannot spare the complete planned batch")));
                }
            }
            List<WorthIt.DefenderProfile> projectedProfiles =
                projectedUnits.Select(WorthIt.FromLiveUnit).ToList();
            if (!GroundCombatFeasibility.Clears(projectedProfiles,
                WorthIt.SideCommander.Of(projectedUnits), opposition, plan.WinChanceGate,
                r.DefenderHexDefenseBonus, out float departureWin, out bool departureCover,
                plan.RequireCoverage))
                return GroundCombatAssaultOutcome.Failed(ProvisioningResult.Fail(
                    ProvisionFailure.AssemblyInfeasible($"{lane} departure roster no longer clears the combat gate")));
            plan.ProjectedWinChance = departureWin;
            plan.CoversAllDefenders = departureCover;
            if (m.Target is AttackMissionTarget attackTarget && !attackTarget.ForceCommitted
                && !AttackForceReadiness.ForceReady(AiPower.EffectiveArmyPower(projectedUnits),
                    session.Snapshot.Self.AttackPeak))
                return GroundCombatAssaultOutcome.Failed(ProvisioningResult.Fail(
                    ProvisionFailure.AssemblyInfeasible("fresh attack force fell below the current deck peak")));

            // AI-01 — every check below is priced against `projectedUnits`, the roster that will
            // actually march, and every one of them runs BEFORE the first ArmyActions.TransferMember
            // call. Reachability too: a recruit slower than the host lowers the whole army's shared
            // movement (ArmyData.ComputeCurrentMovement), so the first step must be re-asked with
            // the projected movement rather than the host's own.
            // An Attack march on its MAIN target is judged on terrain alone here: a contact on the
            // path (army, guard, building) is the fight policy's decision at execution, so it must
            // not make the funded step "unexecutable". Everything else keeps the Combat profile.
            SafeRouteProfile firstStepProfile = lane == "attack"
                && !(m.Target is AttackMissionTarget routeTarget && routeTarget.IsIntermediateAssault)
                    ? SafeRouteProfile.Attack : SafeRouteProfile.Combat;
            if (SafeStepPathing.FindNextSafeStepForRoster(ctx.Map, host, targetHex, projectedUnits,
                    firstStepProfile) == null)
                return GroundCombatAssaultOutcome.Failed(ProvisioningResult.Fail(
                    ProvisionFailure.NoExecutableStep(
                        $"no safe first step from ({host.Hex.Q},{host.Hex.R}) toward {lane} target "
                        + $"({targetHex.Q},{targetHex.R})"
                        + (plan.NeedsAssembly ? " for the projected assembled roster" : ""))));

            string veto = r.PreMutationVeto?.Invoke(host, projectedUnits);
            if (veto != null)
                return GroundCombatAssaultOutcome.Failed(ProvisioningResult.Fail(
                    ProvisionFailure.NoExecutableStep($"{lane} host #{host.Id}: {veto}")));

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

            GroundCombatAssemblyBaseline baseline = GroundCombatAssemblyTransaction.Capture(player, host, transfers);
            bool reordered = plan.Commander != null && plan.Commander != host.Commander
                && GroundCombatAssemblyPlanner.CanPromoteBeforeHeroReturn(host, plan.Commander)
                && host.TryReorderCommander(plan.Commander, out _);
            var shedDone = new List<UnitData>();
            if (shed.Count > 0)
            {
                if (!ArmyActions.TransferMembersAtomic(shed, host, shedGarrison,
                    ctx.HexSelection, out string shedWhy))
                {
                    GroundCombatAssemblyTransaction.Rollback(player, host,
                        new List<GroundCombatAssemblyTransfer>(), ctx, lane, baseline);
                    return GroundCombatAssaultOutcome.Failed(ProvisioningResult.Fail(
                        ProvisionFailure.MoverContended($"{lane} host #{host.Id} hero return rejected: {shedWhy}")));
                }
                shedDone.AddRange(shed);
                AiDebugLog.Write($"[AI][V2]   {lane} provision [{m.AttemptId}] {key} — secondary hero(es) "
                    + $"[{string.Join(",", shed.Select(u => u.Name))}] returned to garrison #{shedGarrison.Id}");
            }

            var applied = new List<GroundCombatAssemblyTransfer>();
            foreach (GroundCombatAssemblyTransfer t in transfers)
            {
                ArmyData donor = AiV2Util.ResolveArmy(player, t.DonorArmyId);
                string why = donor == null ? "donor missing" : null;
                if (donor == null || !ArmyActions.TransferMember(t.Unit, donor, host, ctx.HexSelection, out why))
                {
                    bool rollbackOk = GroundCombatAssemblyTransaction.Rollback(player, host, applied, ctx, lane,
                        baseline, shedGarrison, shedDone);
                    int transfersStillApplied = GroundCombatAssemblyTransaction.RemainingApplied(host, applied,
                        baseline, shedDone);
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

            // Apply exactly the commander priced in the departure plan. Attack's unchanged
            // preparation path may still leave command selection to its existing evaluator.
            UnitData lead = plan.Commander ?? HeroRoleEvaluator.BestCommanderFor(host.Members, host.IsGarrison,
                opposition, r.DefenderHexDefenseBonus, preferRapid: lane != "attack");
            if (lead != null && lead != host.Commander)
                reordered |= host.TryReorderCommander(lead, out _);

            // AI-01 — the assembled force must cost exactly what was projected and funded. Any
            // divergence is a failure, never a partial success: roll the transaction back and let
            // the existing repack/reprice loop re-decide with honest numbers.
            int actualAp = host.ProjectedActivationApCost(host.Members);
            bool sameRoster = host.Members.Count == projectedUnits.Count
                && projectedUnits.All(host.Members.Contains)
                && (plan.Commander == null || host.Commander == plan.Commander);
            if (!sameRoster || actualAp != activationAp || actualAp > envelope + eps)
            {
                bool reconcileRollbackOk = GroundCombatAssemblyTransaction.Rollback(player, host, applied, ctx, lane,
                        baseline, shedGarrison, shedDone);
                int stillApplied = GroundCombatAssemblyTransaction.RemainingApplied(host, applied,
                    baseline, shedDone);
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

            // Strike force step 5 — the host marches under its best legal commander for THIS fight
            // (HeroRoleEvaluator, the same choice the gather projection made). Zero AP, no roster
            // change, so the funded activation above is untouched.
            if (reordered)
                AiDebugLog.Write($"[AI][V2]   {lane} provision [{m.AttemptId}] {key} — host #{host.Id} "
                    + $"commander -> {lead.Name} for this fight");

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
                AppliedTransfers = applied.Count + shedDone.Count,
                CommanderReordered = reordered,
            };
        }

        private static string N(float v) => v.ToString("0.##", CultureInfo.InvariantCulture);
    }
}



