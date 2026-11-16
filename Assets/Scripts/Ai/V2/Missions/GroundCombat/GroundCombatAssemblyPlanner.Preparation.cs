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
        // T01 — the preparation host's same-hex step: the SAME donor legality as the assault's
        // same-hex package (one heroless-host hero pick through HeroRoleEvaluator, garrison floors
        // and operators through AiArmyRoles.CanSpareGarrisonMembers, capacity from the projected
        // commander, 0-AP joins only), but nothing has to clear yet: every legal body that raises
        // the host's EffectiveArmyPower joins; a body that does not raise it is never "progress".
        // `host` may be empty (a claimed shell) or an ArmyData.CreateVisualSnapshot preview of the
        // container ArmyActions.CreateArmyWithMember is about to create on that hex (Id -1).
        internal static GroundCombatAssemblyPlan PlanPreparationAssembly(WorldSnapshot snap,
            ArmyData host, ISet<int> excludeArmyIds, IReadOnlyList<WorthIt.DefendingArmy> opposition,
            float defenderHexDefenseBonus,
            System.Func<IReadOnlyList<UnitData>, bool> rosterAdmissible = null)
        {
            if (host?.Owner == null || snap?.Self?.Armies == null || host.IsGarrison || host.IsPrison
                || host.IsAirfield || host.IsAirArmy)
                return GroundCombatAssemblyPlan.Infeasible("preparation host is not an own ground field container");
            return AssembleSameHex(snap, host.Owner, host,
                opposition ?? System.Array.Empty<WorthIt.DefendingArmy>(), excludeArmyIds,
                GroundCombatAdmissionPolicy.AttackWinChanceGate, defenderHexDefenseBonus, 0f,
                preparation: true, rosterAdmissible: rosterAdmissible);
        }

        private static GroundCombatAssemblyPlan AssembleSameHex(WorldSnapshot snap,
            PlayerSetupData owner, ArmyData host, IReadOnlyList<WorthIt.DefendingArmy> opposition,
            ISet<int> excludeArmyIds, float minWinChance, float defenderHexDefenseBonus,
            float minimumArmyPower, bool preparation, bool? requireCoverage = null,
            bool preserveDepartureRoster = false, bool requireReadyThisTurn = true,
            System.Func<IReadOnlyList<UnitData>, bool> rosterAdmissible = null)
        {
            float hostPower = AiPower.EffectiveArmyPower(host.Members);
            var projectedUnits = new List<UnitData>(host.Members);
            var projectedProfiles = projectedUnits.Select(WorthIt.FromLiveUnit).ToList();
            var selected = new List<GroundCombatAssemblyTransfer>();

            // §12 — a heroless host may take ONE eligible same-hex hero from a safe donor
            // (typically the garrison), the best commander for THIS fight (HeroRoleEvaluator).
            // A lone-hero container is intentionally left to Housekeeping first: Provisioning's
            // canonical raid transaction never empties donor containers, so the planner must not
            // promise a transfer the executor will reject.
            // 2026-09-30 (user decision) — in a preparation the hero is taken for its CAPACITY: a
            // heroless host is capped at the bare container's slots and can never grow to the
            // Attack threshold, so a hero that raises the capacity is progress even though a hero
            // adds no power of its own. A lone-hero army may hand its hero over (preparation only).
            bool garrisonHeroTaken = false;
            bool CarrierHelps(IEnumerable<UnitData> roster) =>
                GroundCombatDonorPolicy.DisruptionHelps(roster, opposition);
            bool disruptionAdded = false;
            if (!projectedUnits.Any(u => u != null && u.IsHero))
            {
                (ArmyData heroDonor, UnitData hero) = GroundCombatDonorPolicy.PickAttachableHero(owner, host,
                    excludeArmyIds, opposition, defenderHexDefenseBonus, preparation, requireReadyThisTurn,
                    rosterAdmissible);
                if (hero != null)
                {
                    var withHero = new List<UnitData>(projectedUnits) { hero };
                    int capacityWith = ArmyData.ComputeRosterCapacity(withHero, host.IsGarrison);
                    if (capacityWith >= withHero.Count
                        && (!preparation
                            || capacityWith > ArmyData.ComputeRosterCapacity(projectedUnits, host.IsGarrison)
                            || AiPower.EffectiveArmyPower(withHero) > AiPower.EffectiveArmyPower(projectedUnits)))
                    {
                        garrisonHeroTaken = AiArmyRoles.IsGarrisonHero(hero);
                        projectedUnits.Add(hero);
                        projectedProfiles.Add(WorthIt.FromLiveUnit(hero));
                        selected.Add(new GroundCombatAssemblyTransfer { DonorArmyId = heroDonor.Id, Unit = hero });
                        if (!preparation)
                        {
                            GroundCombatAssemblyPlan ready = CompleteAssaultAssembly(snap, host,
                                projectedUnits, selected, opposition, minWinChance,
                                defenderHexDefenseBonus, minimumArmyPower, requireCoverage, preserveDepartureRoster,
                                requireReadyThisTurn);
                            if (ready.Feasible) return ready;
                        }
                    }
                }
            }

            // 2026-11-16 (owner decision, match Ysolde T29-T33) — a preparation host with a frozen
            // target roster keeps the slots its pending roster cards need: the same contract
            // Housekeeping reads (AttackPreparationMutationContract: TargetRosterKeys,
            // ReservedDeploymentKeys = held cards that deploy here). A body the roster does not name
            // takes a free slot only while those slots stay free; otherwise Assemble filled them
            // and Housekeeping's 0.8 release emptied them again, every turn.
            IReadOnlyList<string> rosterKeys = null, rosterHandKeys = null;
            if (preparation)
            {
                ArmyMutationContract rosterContract = AttackContinuity.AttackPreparationMutationContract(snap.TurnNumber);
                rosterKeys = rosterContract.TargetRosterKeys(host);
                if (rosterKeys != null && rosterKeys.Count > 0)
                    rosterHandKeys = rosterContract.ReservedDeploymentKeys(host);
                else
                    rosterKeys = null;
            }

            IEnumerable<ArmySnapshot> donorSnaps = snap.Self.Armies
                .Where(d => d != null && d.ArmyId != host.Id && d.Owner == owner
                    && d.Hex.Equals(host.Hex) && !d.IsPrison && !d.IsAir && !d.IsSoloRecce
                    && d.MemberCount > 1
                    && (excludeArmyIds == null || !excludeArmyIds.Contains(d.ArmyId)))
                .OrderByDescending(d => d.EffectiveArmyPower)
                .ThenBy(d => d.ArmyId);

            foreach (ArmySnapshot donorSnap in donorSnaps)
            {
                ArmyData donor = OwnWorld.Of(owner).Armies
                    .FirstOrDefault(a => a != null && a.Id == donorSnap.ArmyId);
                if (donor == null || donor.Members.Count <= 1 || !donor.Hex.Equals(host.Hex)
                    || donor.IsPrison || donor.IsAirfield || donor.IsAirArmy || AiArmyRoles.IsSoloRecce(donor))
                    continue;

                List<UnitData> picks = donor.Members
                    .Where(u => AiArmyRoles.IsGroundBattleBody(u)
                        && donor.Members.Count > 1
                        && donor.CanLeaveWithoutOvercrowding(u)
                        && (!requireReadyThisTurn || !host.HasActivatedThisTurn || u.ActivationApCost <= 0))
                    // A first passive carrier removes a known site defence even when its own
                    // power is low. Reserve its slot before the ordinary damage-ranked bodies.
                    .OrderByDescending(u => CarrierHelps(projectedUnits) && Game.Combat.DisruptionRules.IsCarrier(u))
                    .ThenByDescending(GroundCombatDonorPolicy.UnitCombatValue)
                    .ThenBy(u => u.Name)
                    .ToList();
                // The hero pick above is part of this same transaction. Every later body must
                // leave a legal roster after ALL transfers, including the departing commander.
                var selectedFromDonor = selected.Where(t => t.DonorArmyId == donor.Id)
                    .Select(t => t.Unit).ToList();
                // The donor stays unchanged while its picks are tried: its floor is read once.
                System.Func<IEnumerable<UnitData>, bool> canSpare = donor.IsGarrison
                    ? AiArmyRoles.GarrisonSpareCheck(owner, donor) : null;
                foreach (UnitData pick in picks)
                {
                    if (selectedFromDonor.Contains(pick))
                        continue;
                    if (donor.Members.Count - selectedFromDonor.Count <= 1)
                        break;
                    selectedFromDonor.Add(pick);
                    if (!ArmyData.RosterFits(donor.Members.Except(selectedFromDonor).ToList(), donor.IsGarrison)
                        || (canSpare != null && !canSpare(selectedFromDonor)))
                    {
                        selectedFromDonor.RemoveAt(selectedFromDonor.Count - 1);
                        continue;
                    }
                    var withPick = new List<UnitData>(projectedUnits) { pick };
                    if (rosterAdmissible != null && !rosterAdmissible(withPick))
                    {
                        selectedFromDonor.RemoveAt(selectedFromDonor.Count - 1);
                        continue; // A stronger slow pick must not hide a usable fast alternative.
                    }
                    if (!ArmyData.RosterFits(withPick, host.IsGarrison)
                        && (preparation || preserveDepartureRoster
                            || !HasDepartureRoom(snap, host, withPick, requireReadyThisTurn)))
                    {
                        selectedFromDonor.RemoveAt(selectedFromDonor.Count - 1);
                        break;
                    }
                    // The roster is a plan, never a gate (owner, 2026-11-16): a body that makes the
                    // host ready to march (AttackForceReadiness — the 80% floor or the opportunity
                    // win against THIS target) is always taken, whatever card its slot waited for.
                    if (rosterKeys != null && !FillsRosterPosition(rosterKeys, projectedUnits, pick)
                        && ArmyData.ComputeRosterCapacity(withPick, host.IsGarrison) - withPick.Count
                            < PendingRosterCardSlots(rosterKeys, rosterHandKeys, withPick)
                        && !MakesHostReady(snap, withPick, opposition, minWinChance, defenderHexDefenseBonus))
                    {
                        selectedFromDonor.RemoveAt(selectedFromDonor.Count - 1);
                        continue; // its slot waits for a roster card; a roster body may still fit
                    }
                    bool addsDisruption = CarrierHelps(projectedUnits)
                        && Game.Combat.DisruptionRules.IsCarrier(pick);
                    if (preparation && !addsDisruption && AiPower.EffectiveArmyPower(withPick)
                            <= AiPower.EffectiveArmyPower(projectedUnits))
                    {
                        selectedFromDonor.RemoveAt(selectedFromDonor.Count - 1);
                        continue;
                    }

                    disruptionAdded |= addsDisruption;
                    projectedUnits.Add(pick);
                    projectedProfiles.Add(WorthIt.FromLiveUnit(pick));
                    selected.Add(new GroundCombatAssemblyTransfer { DonorArmyId = donor.Id, Unit = pick });
                    if (!preparation)
                    {
                        GroundCombatAssemblyPlan ready = CompleteAssaultAssembly(snap, host,
                            projectedUnits, selected, opposition, minWinChance,
                            defenderHexDefenseBonus, minimumArmyPower, requireCoverage, preserveDepartureRoster,
                            requireReadyThisTurn);
                        if (ready.Feasible) return ready;
                    }
                }
            }

            if (preparation)
            {
                bool capacityRaised = ArmyData.ComputeCapacity(projectedUnits, host.IsGarrison)
                    > ArmyData.ComputeCapacity(host.Members, host.IsGarrison);
                if (selected.Count == 0
                    || (AiPower.EffectiveArmyPower(projectedUnits) <= hostPower && !capacityRaised && !disruptionAdded))
                    return GroundCombatAssemblyPlan.Infeasible(
                        $"preparation host #{host.Id}: no legal same-hex body raises its power "
                        + "and no hero raises its capacity");
                GroundCombatFeasibility.Clears(projectedProfiles, WorthIt.SideCommander.Of(projectedUnits),
                    opposition, minWinChance, defenderHexDefenseBonus, out float pWin, out bool pCover);
                GroundCombatAssemblyPlan prep = FinishAssembly(host, selected, pWin, pCover, minWinChance, projectedUnits);
                prep.UsesGarrisonHero = garrisonHeroTaken;
                return prep;
            }

            // The hero alone (no bodies available/needed) may already clear.
            if (selected.Count > 0)
            {
                GroundCombatAssemblyPlan ready = CompleteAssaultAssembly(snap, host,
                    projectedUnits, selected, opposition, minWinChance,
                    defenderHexDefenseBonus, minimumArmyPower, requireCoverage, preserveDepartureRoster,
                    requireReadyThisTurn);
                if (ready.Feasible) return ready;
            }

            return GroundCombatAssemblyPlan.Infeasible($"raid actor #{host.Id} cannot reach the win bar from safe same-hex donors");
        }

        // Would `roster` march: the Attack readiness rule on the combat gate it is planned against
        // (AttackForceReadiness.ForceReady with the win chance, as the preparation's own readiness).
        private static bool MakesHostReady(WorldSnapshot snap, IReadOnlyList<UnitData> roster,
            IReadOnlyList<WorthIt.DefendingArmy> opposition, float minWinChance, float hexBonus)
        {
            if (snap?.Self == null || opposition == null)
                return false;
            List<WorthIt.DefenderProfile> profiles = roster.Where(AiArmyRoles.IsGroundBattleBody)
                .Select(WorthIt.FromLiveUnit).ToList();
            if (!GroundCombatFeasibility.Clears(profiles, WorthIt.SideCommander.Of(roster), opposition,
                    minWinChance, hexBonus, out float win, out _))
                return false;
            return AttackForceReadiness.ForceReady(AiPower.EffectiveArmyPower(roster.ToList()),
                snap.Self.AttackPeak, win, opposition);
        }

        // The roster positions (multiset by key) `members` do not hold yet — heroes count too, as in
        // ReorgViability.PreparationRosterGap.
        private static Dictionary<string, int> MissingRosterKeys(IReadOnlyList<string> rosterKeys,
            IEnumerable<UnitData> members)
        {
            var need = new Dictionary<string, int>();
            foreach (string k in rosterKeys)
                if (k != null) { need.TryGetValue(k, out int n); need[k] = n + 1; }
            foreach (UnitData u in members)
            {
                string key = StrikeRoster.UnitKey(u);
                if (key != null && need.TryGetValue(key, out int n) && n > 0)
                    need[key] = n - 1;
            }
            return need;
        }

        internal static bool FillsRosterPosition(IReadOnlyList<string> rosterKeys, IEnumerable<UnitData> members,
            UnitData pick)
        {
            string key = StrikeRoster.UnitKey(pick);
            return key != null && MissingRosterKeys(rosterKeys, members).TryGetValue(key, out int n) && n > 0;
        }

        // Missing positions a held card that deploys here will fill (the pending sources
        // Housekeeping keeps slots for).
        internal static int PendingRosterCardSlots(IReadOnlyList<string> rosterKeys,
            IReadOnlyList<string> handKeys, IEnumerable<UnitData> members)
        {
            if (handKeys == null || handKeys.Count == 0)
                return 0;
            Dictionary<string, int> need = MissingRosterKeys(rosterKeys, members);
            int pending = 0;
            foreach (string k in handKeys)
                if (k != null && need.TryGetValue(k, out int n) && n > 0)
                {
                    need[k] = n - 1;
                    pending++;
                }
            return pending;
        }

        private static GroundCombatAssemblyPlan CompleteAssaultAssembly(WorldSnapshot snap,
            ArmyData host, IReadOnlyList<UnitData> members,
            IReadOnlyList<GroundCombatAssemblyTransfer> transfers,
            IReadOnlyList<WorthIt.DefendingArmy> opposition, float gate, float hexBonus,
            float minimumPower, bool? requireCoverage, bool preserveDepartureRoster,
            bool requireReadyThisTurn = true)
        {
            if (!preserveDepartureRoster)
                return PlanDepartureRoster(snap, host, members, transfers, opposition,
                    gate, hexBonus, minimumPower, requireCoverage, requireReadyThisTurn);
            if (GroundCombatFeasibility.Clears(members.Select(WorthIt.FromLiveUnit).ToList(),
                    WorthIt.SideCommander.Of(members), opposition, gate, hexBonus,
                    out float win, out bool cover, requireCoverage)
                && AttackForceReadiness.ClearsFloor(AiPower.EffectiveArmyPower(members),
                    minimumPower, win, opposition))
            {
                GroundCombatAssemblyPlan plan = FinishAssembly(host, transfers, win, cover, gate, members);
                plan.RequireCoverage = requireCoverage;
                return plan;
            }
            return GroundCombatAssemblyPlan.Infeasible("assembled roster does not clear the combat gate");
        }

        private static GroundCombatAssemblyPlan FinishAssembly(ArmyData host,
            IReadOnlyList<GroundCombatAssemblyTransfer> selected, float win, bool cover, float gate,
            IReadOnlyList<UnitData> roster)
        {
            var plan = new GroundCombatAssemblyPlan
            {
                Feasible = true,
                BaseArmyId = host.Id,
                NeedsAssembly = true,
                ProjectedWinChance = win,
                CoversAllDefenders = cover,
                ProjectedPower = AiPower.EffectiveArmyPower(roster),
                WinChanceGate = gate,
            };
            foreach (GroundCombatAssemblyTransfer t in selected)
            {
                plan.Transfers.Add(t);
                if (!plan.MergeArmyIds.Contains(t.DonorArmyId))
                    plan.MergeArmyIds.Add(t.DonorArmyId);
            }
            return plan;
        }
    }
}

