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
            float defenderHexDefenseBonus)
        {
            if (host?.Owner == null || snap?.Self?.Armies == null || host.IsGarrison || host.IsPrison
                || host.IsAirfield || host.IsAirArmy)
                return GroundCombatAssemblyPlan.Infeasible("preparation host is not an own ground field container");
            return AssembleSameHex(snap, host.Owner, host,
                opposition ?? System.Array.Empty<WorthIt.DefendingArmy>(), excludeArmyIds,
                GroundCombatAdmissionPolicy.AttackCoverageGate, defenderHexDefenseBonus, 0f,
                preparation: true);
        }

        private static GroundCombatAssemblyPlan AssembleSameHex(WorldSnapshot snap,
            PlayerSetupData owner, ArmyData host, IReadOnlyList<WorthIt.DefendingArmy> opposition,
            ISet<int> excludeArmyIds, float minWinChance, float defenderHexDefenseBonus,
            float minimumArmyPower, bool preparation)
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
            if (!projectedUnits.Any(u => u != null && u.IsHero))
            {
                (ArmyData heroDonor, UnitData hero) = GroundCombatDonorPolicy.PickAttachableHero(owner, host,
                    excludeArmyIds, opposition, defenderHexDefenseBonus, preparation);
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
                        if (!preparation && (minimumArmyPower <= 0f
                                || AiPower.EffectiveArmyPower(projectedUnits) > minimumArmyPower)
                            && GroundCombatFeasibility.Clears(projectedProfiles, WorthIt.SideCommander.Of(projectedUnits), opposition, minWinChance,
                                defenderHexDefenseBonus, out float hWin, out bool hCover))
                            return FinishAssembly(host, selected, hWin, hCover, minWinChance, projectedUnits);
                    }
                }
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
                ArmyData donor = ArmyRegistry.AllForOwner(owner)
                    .FirstOrDefault(a => a != null && a.Id == donorSnap.ArmyId);
                if (donor == null || donor.Members.Count <= 1 || !donor.Hex.Equals(host.Hex)
                    || donor.IsPrison || donor.IsAirfield || donor.IsAirArmy || AiArmyRoles.IsSoloRecce(donor))
                    continue;

                List<UnitData> picks = donor.Members
                    .Where(u => AiArmyRoles.IsGroundBattleBody(u)
                        && donor.Members.Count > 1
                        && donor.CanLeaveWithoutOvercrowding(u)
                        && (!host.HasActivatedThisTurn || u.ActivationApCost <= 0))
                    .OrderByDescending(GroundCombatDonorPolicy.UnitCombatValue)
                    .ThenBy(u => u.Name)
                    .ToList();
                var selectedFromDonor = new List<UnitData>();
                foreach (UnitData pick in picks)
                {
                    if (donor.Members.Count - selectedFromDonor.Count <= 1)
                        break;
                    selectedFromDonor.Add(pick);
                    if (donor.IsGarrison
                        && !AiArmyRoles.CanSpareGarrisonMembers(owner, donor, selectedFromDonor))
                    {
                        selectedFromDonor.RemoveAt(selectedFromDonor.Count - 1);
                        continue;
                    }
                    var withPick = new List<UnitData>(projectedUnits) { pick };
                    if (!ArmyData.RosterFits(withPick, host.IsGarrison))
                    {
                        selectedFromDonor.RemoveAt(selectedFromDonor.Count - 1);
                        break;
                    }
                    if (preparation && AiPower.EffectiveArmyPower(withPick)
                            <= AiPower.EffectiveArmyPower(projectedUnits))
                    {
                        selectedFromDonor.RemoveAt(selectedFromDonor.Count - 1);
                        continue;
                    }

                    projectedUnits.Add(pick);
                    projectedProfiles.Add(WorthIt.FromLiveUnit(pick));
                    selected.Add(new GroundCombatAssemblyTransfer { DonorArmyId = donor.Id, Unit = pick });
                    if (!preparation && (minimumArmyPower <= 0f
                            || AiPower.EffectiveArmyPower(projectedUnits) > minimumArmyPower)
                        && GroundCombatFeasibility.Clears(projectedProfiles, WorthIt.SideCommander.Of(projectedUnits), opposition, minWinChance,
                            defenderHexDefenseBonus, out float win, out bool cover))
                        return FinishAssembly(host, selected, win, cover, minWinChance, projectedUnits);
                }
            }

            if (preparation)
            {
                bool capacityRaised = ArmyData.ComputeCapacity(projectedUnits, host.IsGarrison)
                    > ArmyData.ComputeCapacity(host.Members, host.IsGarrison);
                if (selected.Count == 0
                    || (AiPower.EffectiveArmyPower(projectedUnits) <= hostPower && !capacityRaised))
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
            if (selected.Count > 0 && (minimumArmyPower <= 0f
                    || AiPower.EffectiveArmyPower(projectedUnits) > minimumArmyPower)
                && GroundCombatFeasibility.Clears(projectedProfiles, WorthIt.SideCommander.Of(projectedUnits), opposition,
                    minWinChance, defenderHexDefenseBonus, out float wOnly, out bool cOnly))
                return FinishAssembly(host, selected, wOnly, cOnly, minWinChance, projectedUnits);

            return GroundCombatAssemblyPlan.Infeasible($"raid actor #{host.Id} cannot reach the win bar from safe same-hex donors");
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
