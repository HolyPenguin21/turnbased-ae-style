using System.Collections.Generic;
using System.Linq;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Units;

using Game.Combat;

namespace Game.Ai.V2
{
    public static partial class GroundCombatAssemblyPlanner
    {
        // AI-01 — the roster this plan would ACTUALLY produce: the live host's own members plus
        // every body the plan promises to transfer in. This is the one place that answers
        // "what will the assembled force look like", so cost/movement/AP pricing in Missions and
        // the pre-mutation re-check in Provisioning cannot drift apart into two projections.
        public static List<UnitData> ProjectedRoster(ArmyData host, GroundCombatAssemblyPlan plan)
        {
            var roster = new List<UnitData>();
            if (host != null)
                roster.AddRange(host.Members.Where(u => plan == null || !plan.ReturnHeroes.Contains(u)));
            if (plan != null && plan.NeedsAssembly)
                foreach (GroundCombatAssemblyTransfer t in plan.Transfers)
                    if (t?.Unit != null && !roster.Contains(t.Unit))
                        roster.Add(t.Unit);
            if (plan?.Commander != null && roster.Remove(plan.Commander))
                roster.Insert(0, plan.Commander);
            return roster;
        }

        // One complete departure roster for Raid/ActiveDefence: command, optional hero returns,
        // recruits, gate and cost all describe the SAME package before it reaches Allocation.
        // Attack preparation retains its frozen-roster policy (it never calls this method).
        private static GroundCombatAssemblyPlan PlanDepartureRoster(WorldSnapshot snap, ArmyData host,
            IReadOnlyList<UnitData> members, IReadOnlyList<GroundCombatAssemblyTransfer> transfers,
            IReadOnlyList<WorthIt.DefendingArmy> opposition, float gate, float hexBonus,
            float minimumPower, bool? requireCoverage)
        {
            ArmyData home = snap?.Self?.BaseHexes != null && snap.Self.BaseHexes.Contains(host.Hex)
                ? ArmyRegistry.AllAt(host.Hex).FirstOrDefault(a => a != null && a.IsGarrison
                    && a.Owner == host.Owner && !a.HasActivatedThisTurn) : null;
            var heroes = members.Where(u => u != null && u.IsHero && !u.IsPrisoner).ToList();
            var leaders = heroes.Count > 0 ? heroes : new List<UnitData> { null };
            GroundCombatAssemblyPlan best = null;

            GroundCombatAssemblyPlan Candidate(UnitData lead, List<UnitData> returning)
            {
                var roster = members.Where(u => !returning.Contains(u)).ToList();
                if (lead != null && roster.Remove(lead)) roster.Insert(0, lead);
                if (!ArmyData.RosterFits(roster, false)) return null;
                if (roster.Count == 1 && roster[0].IsHero) return null;
                // A Transport carrier can matter without adding damage or command. A cheaper
                // package must not lose the complete roster's present or recurring movement.
                if (returning.Count > 0 && (ArmyData.ComputeCurrentMovement(roster)
                        < ArmyData.ComputeCurrentMovement(members)
                    || ArmyData.ComputeMaxMovement(roster) < ArmyData.ComputeMaxMovement(members))) return null;
                // Operators are protected by the same domain duty rule as hero donors. Their
                // on-hex relocation is allowed; leaving a served facility is not.
                if (roster.Any(u => u != null && u.IsHero
                    && AiArmyRoles.IsFacilityOperator(host.Owner, host.Hex, u))) return null;
                if (returning.Count > 0 && !CanApplyHeroReturn(host, home, lead, returning)) return null;
                float power = AiPower.EffectiveArmyPower(roster);
                if (minimumPower > 0f && power <= minimumPower) return null;
                if (!GroundCombatFeasibility.Clears(roster.Select(WorthIt.FromLiveUnit).ToList(),
                    WorthIt.SideCommander.Of(roster), opposition, gate, hexBonus,
                    out float win, out bool cover, requireCoverage)) return null;
                var plan = new GroundCombatAssemblyPlan
                {
                    Feasible = true, BaseArmyId = host.Id, NeedsAssembly = transfers.Count > 0,
                    Commander = lead, ProjectedWinChance = win, CoversAllDefenders = cover,
                    ProjectedPower = power, WinChanceGate = gate, RequireCoverage = requireCoverage,
                    HeroReturnArmyId = returning.Count > 0 ? home?.Id : null,
                    UsesGarrisonHero = lead != null && AiArmyRoles.IsGarrisonHero(lead),
                };
                plan.ReturnHeroes.AddRange(returning);
                plan.Transfers.AddRange(transfers);
                plan.MergeArmyIds.AddRange(transfers.Select(t => t.DonorArmyId).Distinct());
                return plan;
            }

            foreach (UnitData lead in leaders)
            {
                // A chosen incoming hero is already the unique commander selected by donor policy.
                // Only host members can be sent home; no incoming unit is transferred twice.
                var returning = home == null ? new List<UnitData>() : heroes.Where(h => h != lead
                    && host.Members.Contains(h)
                    && (AiArmyRoles.IsFacilityOperator(host.Owner, host.Hex, h)
                        || AiArmyRoles.IsGarrisonHero(h)
                            && !StrategicEffectRegistry.HasArmySupportEffect(h.Abilities, h.MoveMax))).ToList();
                GroundCombatAssemblyPlan candidate = Candidate(lead, returning);
                // Excess heroes can themselves prevent a lower-capacity commander from fitting.
                // Try that legal final package too; its returns occur before the recruits arrive.
                if (candidate == null && home != null)
                {
                    var reduced = heroes.Where(h => h != lead && host.Members.Contains(h)
                        && !StrategicEffectRegistry.HasArmySupportEffect(h.Abilities, h.MoveMax)).ToList();
                    reduced.AddRange(returning.Where(h => !reduced.Contains(h)));
                    candidate = Candidate(lead, reduced);
                    if (candidate != null) returning = reduced;
                }
                if (candidate == null) continue;
                if (home != null)
                    foreach (UnitData secondary in heroes.Where(h => h != lead
                        && host.Members.Contains(h) && !returning.Contains(h))
                        .OrderByDescending(h => h.ActivationApCost).ThenBy(h => h.RuntimeId))
                    {
                        var trial = new List<UnitData>(returning) { secondary };
                        GroundCombatAssemblyPlan reduced = Candidate(lead, trial);
                        if (reduced == null) continue;
                        // Heal/Drill and any future estimator-supported hero ability are evaluated
                        // through the whole roster. A carrier needed to clear the gate stays.
                        returning = trial;
                        candidate = reduced;
                    }
                if (best == null || BetterDeparture(host, candidate, best)) best = candidate;
            }
            return best ?? GroundCombatAssemblyPlan.Infeasible(
                $"raid actor #{host.Id} has no legal departure roster clearing the combat gate");
        }

        private static bool BetterDeparture(ArmyData host, GroundCombatAssemblyPlan a,
            GroundCombatAssemblyPlan b)
        {
            // A Support-tag commander remains the existing fallback, never a cheap replacement
            // for an ordinary commander that already clears the fight.
            int support = a.UsesGarrisonHero.CompareTo(b.UsesGarrisonHero);
            if (support != 0) return support < 0;
            List<UnitData> ar = ProjectedRoster(host, a), br = ProjectedRoster(host, b);
            int price = ArmyData.ComputeActivationApCost(ar).CompareTo(ArmyData.ComputeActivationApCost(br));
            if (price != 0) return price < 0;
            // Zero-AP secondary heroes still consume slots, even after today's activation is paid.
            int size = ar.Count.CompareTo(br.Count);
            if (size != 0) return size < 0;
            HeroRoleEvaluator.CommandCandidate Command(GroundCombatAssemblyPlan plan, List<UnitData> roster) =>
                HeroRoleEvaluator.Candidate(plan.Commander,
                    new HeroRoleEvaluator.CommandProjection(plan.ProjectedWinChance,
                        System.Math.Max(0, ArmyData.ComputeCapacity(roster, false)
                            - roster.Count(u => u != null && u.IsHero)),
                        roster.Where(u => u != null && u.IsGroundCombatant).Select(WorthIt.FromLiveUnit).ToList()),
                    plan.Commander?.RuntimeId ?? 0, preferRapid: true);
            return HeroRoleEvaluator.CompareCandidates(Command(a, ar), Command(b, br)) < 0;
        }

        // The exact zero-cost mutation the assault transaction can apply. A weaker chosen
        // commander is promoted before returning heroes only if the untouched host fits it;
        // otherwise returns happen under the old commander and promotion follows recruitment.
        internal static bool CanApplyHeroReturn(ArmyData host, ArmyData home, UnitData lead,
            IReadOnlyList<UnitData> returning)
        {
            if (host == null || home == null || !home.IsGarrison || home.Owner != host.Owner
                || !home.Hex.Equals(host.Hex) || home.HasActivatedThisTurn
                || returning == null || returning.Count == 0
                || returning.Any(h => h == null || !h.IsHero || h == lead || !host.Members.Contains(h))
                || ArmyActions.TransferMembersApCost(returning, home) != 0) return false;
            var preview = ArmyData.CreateVisualSnapshot();
            preview.Owner = host.Owner;
            preview.Hex = host.Hex;
            preview.Members.AddRange(host.Members);
            if (CanPromoteBeforeHeroReturn(host, lead))
            {
                preview.Members.Remove(lead);
                preview.Members.Insert(0, lead);
            }
            return ArmyActions.CanExchangeMembers(returning, preview, home, null, null,
                out _, requireChargeNow: false);
        }

        internal static bool CanPromoteBeforeHeroReturn(ArmyData host, UnitData lead) =>
            lead != null && host.Members.Contains(lead)
                && ArmyData.RosterFits(new[] { lead }.Concat(host.Members.Where(u => u != lead)).ToList(), false);

        private static bool HasDepartureRoom(WorldSnapshot snap, ArmyData host,
            IReadOnlyList<UnitData> members)
        {
            if (snap?.Self?.BaseHexes == null || !snap.Self.BaseHexes.Contains(host.Hex)) return false;
            ArmyData home = ArmyRegistry.AllAt(host.Hex).FirstOrDefault(a => a != null && a.IsGarrison
                && a.Owner == host.Owner && !a.HasActivatedThisTurn);
            if (home == null) return false;
            foreach (UnitData lead in members.Where(u => u != null && u.IsHero))
            {
                var returning = host.Members.Where(u => u != null && u.IsHero && u != lead).ToList();
                var roster = members.Where(u => !returning.Contains(u)).ToList();
                if (roster.Remove(lead)) roster.Insert(0, lead);
                if (ArmyData.RosterFits(roster, false)
                    && CanApplyHeroReturn(host, home, lead, returning)) return true;
            }
            return false;
        }

        // The FULL activation AP of the roster this plan would produce, priced by the canonical
        // game rule (ArmyData.ComputeActivationApCost). Deliberately NOT zeroed for an
        // already-activated host: whether this turn's charge is waived stays RaidCostModel's
        // current-turn-vs-recurring decision, which already owns that split. Null when the plan is
        // infeasible or its host no longer resolves live — callers then fall back to their
        // existing snapshot-based figure rather than inventing a second cost model.
        public static int? ProjectedActivationApCost(WorldSnapshot snap, GroundCombatAssemblyPlan plan)
        {
            List<UnitData> roster = ProjectedRosterOrNull(snap, plan);
            return roster == null ? (int?)null : ArmyData.ComputeActivationApCost(roster);
        }

        // The travel speed of the roster this plan would produce. A recruit slower than
        // the host lowers the WHOLE formation's shared movement (ArmyData.ComputeMaxMovement), so
        // an ETA derived from the untouched host is optimistic exactly when the plan needs
        // assembly. Same owner, same projection, same null-means-fall-back-to-your-existing-figure
        // contract as ProjectedActivationApCost above — never a second cost/ETA model.
        public static int? ProjectedMaxMovement(WorldSnapshot snap, GroundCombatAssemblyPlan plan)
        {
            List<UnitData> roster = ProjectedRosterOrNull(snap, plan);
            return roster == null ? (int?)null : ArmyData.ComputeMaxMovement(roster);
        }

        // The movement points the assembled roster has LEFT this turn (the shared current movement,
        // ArmyData.ComputeCurrentMovement): a slower or already spent recruit lowers the whole
        // formation. Same owner and null-means-fall-back contract as the projections above.
        public static int? ProjectedCurrentMovement(WorldSnapshot snap, GroundCombatAssemblyPlan plan)
        {
            List<UnitData> roster = ProjectedRosterOrNull(snap, plan);
            return roster == null ? (int?)null : ArmyData.ComputeCurrentMovement(roster);
        }

        // The live roster a feasible plan would produce, or null when the plan is infeasible or
        // its host no longer resolves live. One resolution path for every projection above.
        private static List<UnitData> ProjectedRosterOrNull(WorldSnapshot snap,
            GroundCombatAssemblyPlan plan)
        {
            if (snap?.Self?.Armies == null || plan == null || !plan.Feasible)
                return null;
            ArmySnapshot hostSnap = snap.Self.Armies.FirstOrDefault(
                a => a != null && a.ArmyId == plan.BaseArmyId);
            PlayerSetupData owner = hostSnap?.Owner;
            if (owner == null)
                return null;
            ArmyData host = ArmyRegistry.AllForOwner(owner)
                .FirstOrDefault(a => a != null && a.Id == plan.BaseArmyId);
            if (host == null || host.Members.Count == 0)
                return null;
            return ProjectedRoster(host, plan);
        }

        private static ArmyData LiveArmy(ArmySnapshot s)
        {
            PlayerSetupData owner = s?.Owner;
            return owner == null ? null : ArmyRegistry.AllForOwner(owner)
                .FirstOrDefault(a => a != null && a.Id == s.ArmyId);
        }

        private static List<WorthIt.DefenderProfile> NonAviationProfiles(ArmySnapshot army)
        {
            var result = new List<WorthIt.DefenderProfile>();
            IReadOnlyList<WorthIt.DefenderProfile> members =
                army?.Members ?? System.Array.Empty<WorthIt.DefenderProfile>();
            IReadOnlyList<bool> aviation =
                army?.NonHeroIsAviation ?? System.Array.Empty<bool>();
            for (int i = 0; i < members.Count; i++)
                if (i >= aviation.Count || !aviation[i])
                    result.Add(members[i]);
            return result;
        }

        private static float ProfileCombatValue(WorthIt.DefenderProfile p) => AiPower.SkilledCombatValue(p);
    }
}
