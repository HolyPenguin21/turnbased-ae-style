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
        // Audit F7 — CROSS-HEX GATHER. Plan() knows an already-sufficient army or a SAME-HEX
        // package only. When the strength exists but is spread over free field armies on different
        // hexes, this answers which army HOSTS the formation and which others walk to it and hand
        // their bodies over, so the assembled force clears `winChanceGate` at the lowest total AP:
        //
        //   cost(host) = Σ support.ActivationAp × max(1, turns(support -> host))
        //              + Σ handoff charge (GroundCombatReinforcement.ProjectedHandoffApCost)
        //              + ActivationAp(assembled roster) × turns(host -> target)
        //
        // Every walking turn re-activates the walker, so the cheapest host is naturally one already
        // on the way to the target and close to its supports. Supports are added greedily by win
        // gain per AP through the SAME fill/swap projection the handoff executes
        // (TryProjectReinforcement over GroundCombatReinforcement.SparableSupportBodies). There is
        // no count or distance cap: the win gate is the bar to be feasible, and past it supports
        // keep joining while each still adds attackGatherMinWinGain (strike force step 5), so a
        // spread-out late-game army attacks at its assembled peak. Candidates are the snapshot's free ready field armies
        // (GroundCombatActorEligibility) minus `excludeArmyIds`; rosters are read live, exactly as
        // TryAssembleForHost does. `pinnedHostArmyId` re-plans a started gather around its host.
        // `donorValues` — armies of other operations the gather may buy as SUPPORTS (never as
        // host), each with the TaskScore value its operation loses (GroundCombatDonorPolicy).
        // `requireMovementNow: false` — the capability question (Demand): could these armies be
        // gathered at all, counting those whose MP is spent this turn (GroundCombatActorEligibility
        // .EligibleArmies). Such a plan is never executed.
        // `allowPartial` (T01 preparation only, with a pinned host): the host may be weak or
        // hero-only (not yet a structural actor), and supports that genuinely raise its power are
        // returned even when the whole pool still misses the threshold (ReachesThreshold=false).
        internal static GroundCombatGatherPlan PlanGather(WorldSnapshot snap,
            IReadOnlyList<WorthIt.DefendingArmy> opposition, float defenderHexDefenseBonus,
            HexCoord targetHex, ISet<int> excludeArmyIds, float winChanceGate,
            int? pinnedHostArmyId = null, IReadOnlyDictionary<int, float> donorValues = null,
            bool requireMovementNow = true, float minimumArmyPower = 0f, bool allowPartial = false)
        {
            if (snap?.Self?.Armies == null)
                return GroundCombatGatherPlan.Infeasible("no own-force snapshot");
            opposition = opposition ?? System.Array.Empty<WorthIt.DefendingArmy>();

            List<ArmySnapshot> free = GroundCombatActorEligibility.EligibleArmies(snap, excludeArmyIds,
                requireMovementNow);
            List<ArmySnapshot> bought = donorValues == null || donorValues.Count == 0
                ? new List<ArmySnapshot>()
                : GroundCombatActorEligibility.EligibleReadyArmies(snap, null)
                    .Where(a => donorValues.ContainsKey(a.ArmyId)
                        && !free.Any(f => f.ArmyId == a.ArmyId))
                    .ToList();
            List<ArmySnapshot> hosts = pinnedHostArmyId.HasValue
                ? snap.Self.Armies.Where(a => a != null && a.ArmyId == pinnedHostArmyId.Value
                    && (a.IsStructuralRaidActor || allowPartial)).ToList()
                : free;

            GroundCombatGatherPlan best = null;
            // T10 — every host's own refusal is kept (not only the last one), and an empty host
            // list names why the field armies are not free (same predicates as EligibleArmies).
            string why = hosts.Count == 0
                ? (pinnedHostArmyId.HasValue
                    ? $"pinned host #{pinnedHostArmyId.Value} is not a usable field army"
                    : "no free field army can host a gather ("
                        + GroundCombatActorEligibility.ExclusionSummary(snap, excludeArmyIds,
                            requireMovementNow) + ")")
                : null;
            var refusals = new List<string>();
            foreach (ArmySnapshot hostSnap in hosts)
            {
                GroundCombatGatherPlan p = PlanGatherForHost(snap, opposition, defenderHexDefenseBonus,
                    targetHex, hostSnap, free.Concat(bought).Where(s => s.ArmyId != hostSnap.ArmyId).ToList(),
                    winChanceGate, donorValues, minimumArmyPower,
                    allowPartial && pinnedHostArmyId.HasValue);
                if (!p.Feasible)
                {
                    refusals.Add(p.Reason);
                    continue;
                }
                if (best == null || (minimumArmyPower > 0f
                        ? p.ProjectedPower > best.ProjectedPower
                        : p.SelectionCost < best.SelectionCost)
                    || (p.SelectionCost == best.SelectionCost && (p.TotalEta < best.TotalEta
                        || (p.TotalEta == best.TotalEta
                            && p.ProjectedWinChance > best.ProjectedWinChance + 0.001f))))
                    best = p;
            }
            if (best == null && refusals.Count > 0)
                why = refusals.Count == 1 ? refusals[0]
                    : $"{refusals.Count} hosts refused: " + string.Join(" | ", refusals.Take(3))
                        + (refusals.Count > 3 ? $" | +{refusals.Count - 3} more" : "");
            return best ?? GroundCombatGatherPlan.Infeasible(why);
        }

        private static GroundCombatGatherPlan PlanGatherForHost(WorldSnapshot snap,
            IReadOnlyList<WorthIt.DefendingArmy> opposition, float defenderHexDefenseBonus,
            HexCoord targetHex, ArmySnapshot hostSnap, List<ArmySnapshot> supportSnaps,
            float winChanceGate, IReadOnlyDictionary<int, float> donorValues,
            float minimumArmyPower, bool allowPartial = false)
        {
            ArmyData host = LiveArmy(hostSnap);
            if (host == null || host.Members.Count == 0)
                return GroundCombatGatherPlan.Infeasible($"gather host #{hostSnap?.ArmyId} is no longer live");

            // The host's side of the handoff, exactly as GroundCombatReinforcement.ImprovesOdds
            // reads the primary.
            // `bodyUnits` stays index-parallel to `bodies`, so every projected fill / swap names
            // the live units that move and the handoff is priced on them.
            var roster = new List<UnitData>(host.Members);
            List<UnitData> bodyUnits = host.Members
                .Where(u => AiArmyRoles.IsGroundBattleBody(u))
                .ToList();
            List<WorthIt.DefenderProfile> bodies = bodyUnits.Select(WorthIt.FromLiveUnit).ToList();

            var pool = new List<GatherSupport>();
            foreach (ArmySnapshot s in supportSnaps)
            {
                ArmyData live = LiveArmy(s);
                List<UnitData> sparable = GroundCombatReinforcement.SparableSupportBodies(live,
                    allowCompleteTransfer: true);
                if (live == null || sparable.Count == 0)
                    continue;
                int turns = AiV2Util.CeilDiv(HexGridMath.Distance(s.Hex, host.Hex),
                    System.Math.Max(1, s.MaxMovement));
                pool.Add(new GatherSupport
                {
                    ArmyId = s.ArmyId,
                    Snapshot = s,
                    Live = live,
                    Units = sparable,
                    Bodies = sparable.Select(WorthIt.FromLiveUnit).ToList(),
                    Turns = turns,
                    Ap = s.ActivationApCost * System.Math.Max(1, turns),
                    // A bought donor also costs the operation it abandons — in its own units.
                    DisplacedValue = donorValues != null
                        && donorValues.TryGetValue(s.ArmyId, out float lost) ? lost : 0f,
                });
            }
            if (pool.Count == 0)
                return GroundCombatGatherPlan.Infeasible(
                    $"gather host #{host.Id}: no other free field army has a body to spare "
                    + $"(candidates={supportSnaps.Count})");

            // The PEAK formation's commander (its bodies plus the pool — HeroRoleEvaluator): the
            // host's own best hero (the assault transaction promotes it before the march), or a
            // support's hero handed over because it leads this fight better
            // (GroundCombatReinforcement.CommandHandover — the handoff applies the same rule; the
            // cheapest such support is kept in the plan for its hero). Its Command sets capacity.
            // ATK-F04 — the handoffs run under the host's CURRENT commander (a handoff promotes only
            // a hero it brings), so the projection's slots are that commander's; the handover is
            // the one the live plan would find (no pooled prospects), legal as an exchange, and
            // taken only when it raises the host (GroundCombatReinforcement.PlanAttackHandoff's
            // rule) — otherwise that support's bodies stay in the ordinary pool below.
            UnitData lead = host.Commander;
            GatherSupport heroDonor = null;
            float hostPowerBefore = AiPower.EffectiveArmyPower(host.Members);
            foreach (GatherSupport s in (minimumArmyPower > 0f
                ? pool.OrderByDescending(x => x.Live.Members.Where(u => u.IsHero)
                        .Select(u => u.CommandRating).DefaultIfEmpty(0).Max())
                    .ThenBy(x => x.SelectionCost).ThenBy(x => x.ArmyId)
                : pool.OrderBy(x => x.SelectionCost).ThenBy(x => x.ArmyId)))
            {
                CommandHandoverPlan handover = GroundCombatReinforcement.CommandHandover(host, s.Live,
                    opposition, defenderHexDefenseBonus, null);
                if (handover == null
                    || !ArmyActions.CanExchangeMembers(handover.Incoming, s.Live, host, handover.Hero,
                        handover.Displaced, out _, requireChargeNow: false))
                    continue;
                List<UnitData> handed = host.Members.Except(handover.Displaced)
                    .Concat(handover.Incoming).ToList();
                bool raises = AiPower.EffectiveArmyPower(handed) > hostPowerBefore
                    || (!WorthIt.CanDamageAll(host.Members.Select(WorthIt.FromLiveUnit).ToList(),
                            opposition, defenderHexDefenseBonus)
                        && WorthIt.CanDamageAll(handed.Select(WorthIt.FromLiveUnit).ToList(),
                            opposition, defenderHexDefenseBonus));
                if (!raises)
                    continue;
                lead = handover.Hero;
                heroDonor = s;
                // The whole exchange the handoff will make (PlanHandoff takes this same plan): the
                // hero and every body it brings join, every host body it exchanges leaves — the
                // formation and the handoff's AP price read the same lists.
                foreach (UnitData gone in handover.Displaced)
                {
                    int at = bodyUnits.IndexOf(gone);
                    roster.Remove(gone);
                    s.Displaced.Add(gone);
                    if (at >= 0)
                    {
                        bodies.RemoveAt(at);
                        bodyUnits.RemoveAt(at);
                    }
                }
                foreach (UnitData joined in handover.Incoming)
                {
                    roster.Add(joined);
                    s.Incoming.Add(joined);
                    if (AiArmyRoles.IsGroundBattleBody(joined))
                    {
                        bodyUnits.Add(joined);
                        bodies.Add(WorthIt.FromLiveUnit(joined));
                    }
                }
                break;
            }
            List<UnitData> ledRoster = roster.Where(u => u != lead).ToList();
            if (lead != null)
                ledRoster.Insert(0, lead);
            int capacity = ArmyData.ComputeCapacity(ledRoster, host.IsGarrison);
            int memberCount = roster.Count;
            WorthIt.SideCommander commander = WorthIt.SideCommander.Of(lead);

            // One Monte-Carlo bound before the greedy loop: the host's slots filled with the
            // strongest bodies the whole pool holds. If even that misses the gate, skip this host.
            List<WorthIt.DefenderProfile> bound = bodies
                .Concat(pool.Where(x => x != heroDonor).SelectMany(x => x.Bodies))
                .OrderByDescending(ProfileCombatValue)
                .Take(bodies.Count + System.Math.Max(0, capacity - memberCount))
                .ToList();
            if (minimumArmyPower <= 0f
                && !GroundCombatFeasibility.Clears(bound, commander, opposition, winChanceGate,
                    defenderHexDefenseBonus, out _, out _))
                return GroundCombatGatherPlan.Infeasible(
                    $"gather host #{host.Id}: even the strongest pooled roster misses the "
                    + $"{winChanceGate:0.00} gate");

            bool coverageOk = GroundCombatFeasibility.Clears(bodies, commander, opposition, winChanceGate,
                defenderHexDefenseBonus, out float win, out bool cover);
            float power = AiPower.EffectiveArmyPower(roster);
            bool clears = coverageOk && (minimumArmyPower <= 0f || power > minimumArmyPower);
            var chosen = new List<GatherSupport>();
            // Strike force step 5 — gather to the PEAK, not to the bare gate: below the gate any
            // improving support is taken (best win gain per AP first); past it a support is worth
            // its walk only while it moves the fight by more than Monte-Carlo noise
            // (attackGatherMinWinGain).
            while (true)
            {
                GatherSupport pick = null;
                List<WorthIt.DefenderProfile> pickRoster = null;
                List<int> pickIncoming = null;
                int pickDisplaced = -1;
                float pickWin = 0f, pickRate = 0f;
                foreach (GatherSupport s in pool)
                {
                    if (chosen.Contains(s) || s == heroDonor
                        || !TryProjectReinforcement(bodies, s.Bodies, capacity, memberCount,
                            commander, opposition, out List<WorthIt.DefenderProfile> projected, out _,
                            out float projectedWin, out List<int> incomingIdx, out int displacedIdx,
                            defenderHexDefenseBonus, requireWinGain: minimumArmyPower <= 0f))
                        continue;
                    var candidate = new List<UnitData>(roster);
                    if (displacedIdx >= 0) candidate.Remove(bodyUnits[displacedIdx]);
                    foreach (int i in incomingIdx) candidate.Add(s.Units[i]);
                    float nextPower = AiPower.EffectiveArmyPower(candidate);
                    if (minimumArmyPower > 0f && nextPower <= power)
                        continue;
                    if (minimumArmyPower <= 0f && clears
                        && projectedWin - win < AiConfigV2.attackGatherMinWinGain)
                        continue;
                    float rate = (minimumArmyPower > 0f ? nextPower - power : projectedWin - win)
                        / System.Math.Max(1f, s.SelectionCost);
                    if (pick == null || rate > pickRate)
                    {
                        pick = s;
                        pickRoster = projected;
                        pickIncoming = incomingIdx;
                        pickDisplaced = displacedIdx;
                        pickWin = projectedWin;
                        pickRate = rate;
                    }
                }
                if (pick == null)
                {
                    if (clears || (allowPartial && chosen.Count > 0))
                        break;
                    // Name the gate that actually failed: the strict power threshold is not a
                    // win-chance inequality (T10).
                    // T10 — name the gate that actually failed. The win-chance path fails on
                    // coverage (a known defender no roster unit can damage) or on the win bar;
                    // "win 1.00 < 0.60" must never stand for a coverage failure.
                    return GroundCombatGatherPlan.Infeasible(minimumArmyPower > 0f
                        ? $"gather host #{host.Id}: no remaining support raises the formation "
                            + $"(projectedPower {power:0.#} must be > requiredPower {minimumArmyPower:0.#}"
                            + $" strict; coverage {(cover ? "ok" : "missing")})"
                        : !cover
                            ? $"gather host #{host.Id}: no remaining support closes coverage "
                                + $"(a known defender no pooled body can damage; win {win:0.00})"
                            : $"gather host #{host.Id}: no remaining support improves the formation "
                                + $"(win {win:0.00} < gate {winChanceGate:0.00})");
                }

                // The live units the projection moved: a fill appends them, a swap exchanges one
                // for the host's weakest body, which leaves with the support.
                if (pickDisplaced >= 0)
                {
                    UnitData gone = bodyUnits[pickDisplaced];
                    bodyUnits.RemoveAt(pickDisplaced);
                    roster.Remove(gone);
                    pick.Displaced.Add(gone);
                }
                foreach (int i in pickIncoming)
                {
                    bodyUnits.Add(pick.Units[i]);
                    roster.Add(pick.Units[i]);
                    pick.Incoming.Add(pick.Units[i]);
                }
                memberCount += pickIncoming.Count - (pickDisplaced >= 0 ? 1 : 0);
                bodies = pickRoster;
                win = pickWin;
                chosen.Add(pick);
                power = AiPower.EffectiveArmyPower(roster);
                coverageOk = GroundCombatFeasibility.Clears(bodies, commander, opposition,
                    winChanceGate, defenderHexDefenseBonus, out win, out cover);
                clears = coverageOk && (minimumArmyPower <= 0f || power > minimumArmyPower);
            }

            if (heroDonor != null && !chosen.Contains(heroDonor))
                chosen.Add(heroDonor);

            int assembledMove = ArmyData.ComputeMaxMovement(roster);
            int assaultEta = AiV2Util.CeilDiv(HexGridMath.Distance(host.Hex, targetHex),
                System.Math.Max(AiConfigV2.etaFallbackMoveBudget, assembledMove));
            var plan = new GroundCombatGatherPlan
            {
                Feasible = true,
                HostArmyId = host.Id,
                HostHex = host.Hex,
                ProjectedWinChance = win,
                CoversAllDefenders = cover,
                ProjectedPower = power,
                ReachesThreshold = clears,
                GatherTurns = chosen.Count == 0 ? 0 : chosen.Max(s => s.Turns),
                AssaultEta = assaultEta,
                // Walks + each support's handoff (the same charge its rendezvous leg provisions)
                // + the assembled roster's march to the target.
                GatherAp = chosen.Sum(s => s.Ap + GroundCombatReinforcement.ProjectedHandoffApCost(
                        s.Incoming, s.Displaced, host, s.Live, supportWalks: s.Turns > 0)),
                TotalAp = chosen.Sum(s => s.Ap + GroundCombatReinforcement.ProjectedHandoffApCost(
                        s.Incoming, s.Displaced, host, s.Live, supportWalks: s.Turns > 0))
                    + ArmyData.ComputeActivationApCost(roster) * System.Math.Max(1, assaultEta),
                DisplacedValue = chosen.Sum(s => s.DisplacedValue),
            };
            plan.CurrentTurnAp = System.Math.Min(plan.TotalAp, chosen
                .Where(s => !s.Snapshot.HasActivatedThisTurn
                    && (s.Snapshot.Hex.Equals(host.Hex) || s.Snapshot.CurrentMovement > 0))
                .Sum(s => s.Snapshot.ActivationApCost));
            foreach (GatherSupport s in chosen.OrderByDescending(s => s.Turns).ThenBy(s => s.ArmyId))
                plan.SupportArmyIds.Add(s.ArmyId);
            return plan;
        }

        private sealed class GatherSupport
        {
            public int ArmyId;
            public ArmySnapshot Snapshot;
            public ArmyData Live;
            // What its handoff moves: support -> host, and host -> support.
            public readonly List<UnitData> Incoming = new List<UnitData>();
            public readonly List<UnitData> Displaced = new List<UnitData>();
            public List<UnitData> Units;
            public List<WorthIt.DefenderProfile> Bodies;
            public int Turns;
            public int Ap;
            public float DisplacedValue;
            public float SelectionCost =>
                Ap + ActionPrice.FromTaskScore(DisplacedValue);
        }
    }
}
