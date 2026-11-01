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
        // Float noise of AiPower.EffectiveArmyPower over the same units summed in another order.
        private const float GatherPowerEpsilon = 0.01f;

        internal static GroundCombatGatherPlan PlanGather(WorldSnapshot snap,
            IReadOnlyList<WorthIt.DefendingArmy> opposition, float defenderHexDefenseBonus,
            HexCoord targetHex, ISet<int> excludeArmyIds, float winChanceGate,
            int? pinnedHostArmyId = null, IReadOnlyDictionary<int, float> donorValues = null,
            bool requireMovementNow = true, float minimumArmyPower = 0f, bool allowPartial = false,
            System.Func<GroundCombatGatherPlan, string> departureVeto = null,
            IReadOnlyList<StrikeRosterSlot> targetRoster = null)
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
                    allowPartial && pinnedHostArmyId.HasValue, targetRoster);
                string departureReason = p.Feasible ? departureVeto?.Invoke(p) : null;
                if (departureReason != null)
                { p.Feasible = false; p.Reason = departureReason; }
                if (!p.Feasible)
                {
                    refusals.Add(p.Reason);
                    continue;
                }
                // 2026-11-01 — the power bar prefers the stronger formation, but two hosts that
                // assemble the same strength (equal up to float summation order) are one formation
                // at two places: the cheaper walk wins, never the first candidate (the weakest host
                // in EligibleArmies order), so the fist never backtracks to a rear shell.
                bool samePower = best != null
                    && System.Math.Abs(p.ProjectedPower - best.ProjectedPower) <= GatherPowerEpsilon;
                if (best == null || (minimumArmyPower > 0f
                        ? (p.ProjectedPower > best.ProjectedPower && !samePower)
                            || (samePower && p.SelectionCost < best.SelectionCost)
                        : p.SelectionCost < best.SelectionCost)
                    || ((minimumArmyPower <= 0f || samePower)
                        && p.SelectionCost == best.SelectionCost && (p.TotalEta < best.TotalEta
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
            float minimumArmyPower, bool allowPartial = false,
            IReadOnlyList<StrikeRosterSlot> targetRoster = null)
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
                List<UnitData> sparable = GroundCombatReinforcement.SparableSupportBodies(live);
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
            foreach (GatherSupport s in (minimumArmyPower > 0f
                ? pool.OrderByDescending(x => x.Live.Members.Where(u => u.IsHero)
                        .Select(u => u.CommandRating).DefaultIfEmpty(0).Max())
                    .ThenBy(x => x.SelectionCost).ThenBy(x => x.Turns).ThenBy(x => x.ArmyId)
                : pool.OrderBy(x => x.SelectionCost).ThenBy(x => x.Turns).ThenBy(x => x.ArmyId)))
            {
                // CommandHandover returns only an exchange the armies can make on arrival.
                HandoffPlan handover = GroundCombatReinforcement.CommandHandover(host, s.Live,
                    opposition, defenderHexDefenseBonus, null,
                    rosterAdmissible: GroundCombatReinforcement.PreserveTaskRoute(snap.Map, host,
                        targetHex, Game.Ai.SafeRouteProfile.Attack));
                if (handover == null)
                    continue;
                List<UnitData> handed = host.Members.Except(handover.Displaced)
                    .Concat(handover.Incoming).ToList();
                if (!GroundCombatReinforcement.RaisesPrimary(host.Members, handed, opposition,
                        defenderHexDefenseBonus))
                    continue;
                lead = handover.Promote;
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
            // The led roster also carries any secondary healing hero into the body-only forecast.
            WorthIt.SideCommander commander = lead != null ? WorthIt.SideCommander.Of(ledRoster) : default;

            // One Monte-Carlo bound before the greedy loop: the host's slots filled with the
            // strongest bodies the whole pool holds. If even that misses the gate, skip this host.
            List<WorthIt.DefenderProfile> pooled = bodies
                .Concat(pool.Where(x => x != heroDonor).SelectMany(x => x.Bodies))
                .OrderByDescending(ProfileCombatValue)
                .ToList();
            List<WorthIt.DefenderProfile> bound = pooled
                .Take(bodies.Count + System.Math.Max(0, capacity - memberCount))
                .ToList();
            // Combat value never shows a carrier's removed structure defence, so the strongest
            // roster is not the best one against a defended site: the same slots with the best
            // pooled carrier in place of the weakest body are the other bound candidate.
            List<WorthIt.DefenderProfile> carrierBound = null;
            if (bound.Count > 0 && GroundCombatDonorPolicy.DisruptionHelps(bound, opposition))
            {
                int carrierAt = pooled.FindIndex(bound.Count,
                    p => Game.Combat.DisruptionRules.HasCarrier(new[] { p }));
                if (carrierAt >= 0)
                {
                    carrierBound = new List<WorthIt.DefenderProfile>(bound);
                    carrierBound[carrierBound.Count - 1] = pooled[carrierAt];
                }
            }
            if (minimumArmyPower <= 0f
                && !GroundCombatFeasibility.Clears(bound, commander, opposition, winChanceGate,
                    defenderHexDefenseBonus, out _, out _)
                && (carrierBound == null || !GroundCombatFeasibility.Clears(carrierBound, commander,
                    opposition, winChanceGate, defenderHexDefenseBonus, out _, out _)))
                return GroundCombatGatherPlan.Infeasible(
                    $"gather host #{host.Id}: even the strongest pooled roster misses the "
                    + $"{winChanceGate:0.00} gate");

            bool coverageOk = GroundCombatFeasibility.Clears(bodies, commander, opposition, winChanceGate,
                defenderHexDefenseBonus, out float win, out bool cover);
            float power = AiPower.EffectiveArmyPower(roster);
            bool clears = coverageOk
                && AttackForceReadiness.ClearsFloor(power, minimumArmyPower, win, opposition);
            int uncovered = WorthIt.UncoveredDefenderCount(bodies, opposition, defenderHexDefenseBonus, attackerCommander: commander);
            // The power bar is 80% of the peak (AttackForceReadiness.RequiredPower(AttackPeak)), and
            // the peak is a concrete roster (SelfSnapshot.StrikeRoster): what the formation still
            // lacks of it, an equivalent body counting as its position. A live preparation reads
            // its frozen roster (`targetRoster`, AttackIntent.TargetRoster), the one its card
            // witness waits for (AggressionDemandEvaluator.PreparationHostCardSource).
            IReadOnlyList<StrikeRosterSlot> rosterSource = targetRoster != null && targetRoster.Count > 0
                ? targetRoster : snap.Self.StrikeRoster;
            IReadOnlyList<StrikeRosterSlot> peakRoster = minimumArmyPower > 0f
                && rosterSource != null && rosterSource.Count > 0 ? rosterSource : null;
            float RosterShortfall(IEnumerable<UnitData> formation) => peakRoster == null ? 0f
                : StrikeRoster.Missing(peakRoster, formation, equivalents: true).Sum(m => m.Power);
            float shortfall = RosterShortfall(roster);
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
                    if (chosen.Contains(s) || s == heroDonor) continue;
                    ArmyData gathered = ArmyData.CreateVisualSnapshot();
                    gathered.Owner = host.Owner;
                    gathered.Hex = host.Hex;
                    if (lead != null) gathered.Members.Add(lead);
                    gathered.Members.AddRange(roster.Where(u => u != lead));
                    var taskRoute = GroundCombatReinforcement.PreserveTaskRoute(snap.Map, host,
                        targetHex, Game.Ai.SafeRouteProfile.Attack);
                    // Ask the same live-unit handoff solver as execution. It can select a fast
                    // escort instead of discarding a donor whose strongest tank blocks the route.
                    HandoffPlan handoff = GroundCombatReinforcement.PlanHandoff(gathered, s.Live,
                        null, 0f, out _, requireChargeNow: false, rosterAdmissible: taskRoute,
                        siteOpposition: opposition);
                    if (handoff == null) continue;
                    var candidate = GroundCombatReinforcement.FinalRoster(gathered, handoff);
                    List<WorthIt.DefenderProfile> projected = candidate
                        .Where(AiArmyRoles.IsGroundBattleBody).Select(WorthIt.FromLiveUnit).ToList();
                    float projectedWin = WorthIt.EstimateSequential(projected, commander, opposition,
                        defenderHexDefenseBonus).WinChance;
                    if (minimumArmyPower <= 0f && projectedWin <= win + .001f) continue;
                    List<int> incomingIdx = handoff.Incoming.Select(u => s.Units.IndexOf(u)).ToList();
                    int displacedIdx = handoff.Displaced.Count == 0 ? -1 : bodyUnits.IndexOf(handoff.Displaced[0]);
                    if (incomingIdx.Any(i => i < 0)) continue;
                    float nextPower = AiPower.EffectiveArmyPower(candidate);
                    if (minimumArmyPower > 0f && nextPower <= power)
                        continue;
                    // 2026-10-17 — past the power bar only coverage holds the formation: a support
                    // that damages no still-uncovered defender is no progress, and taking it reset
                    // the stall clock forever (Ysolde T15-T20: 7/7 host, fist 61 > 60, power-only
                    // supports re-planned each turn while one defender stayed uncovered).
                    if (minimumArmyPower > 0f && uncovered > 0
                        && AttackForceReadiness.ClearsFloor(power, minimumArmyPower, win, opposition)
                        && WorthIt.UncoveredDefenderCount(projected, opposition,
                            defenderHexDefenseBonus, attackerCommander: commander) >= uncovered)
                        continue;
                    // 2026-11-01 — past the power bar (and in a T01 preparation, which waits only
                    // for a missing position — StrikeRoster) the formation gathers toward the
                    // peak's roster, not toward any power gain: a support whose handoff fills no
                    // missing position is a walk for nothing (Draven T16: the 6/6 fist walked three
                    // turns to swap a Light Infantry in for +0.2). A support closing coverage
                    // still joins.
                    if (peakRoster != null && (clears || allowPartial)
                        && RosterShortfall(candidate) >= shortfall - GatherPowerEpsilon
                        && (uncovered == 0 || WorthIt.UncoveredDefenderCount(projected, opposition,
                            defenderHexDefenseBonus, attackerCommander: commander) >= uncovered))
                        continue;
                    if (minimumArmyPower <= 0f && clears
                        && projectedWin - win < AiConfigV2.attackGatherMinWinGain)
                        continue;
                    float rate = (minimumArmyPower > 0f ? nextPower - power : projectedWin - win)
                        / System.Math.Max(1f, s.SelectionCost);
                    // Equal combat gain per price and equal price: prefer the support that
                    // arrives sooner. ID is only the final deterministic tie-break.
                    if (pick == null || rate > pickRate
                        || (rate == pickRate && s.SelectionCost == pick.SelectionCost
                            && (s.Turns < pick.Turns
                                || (s.Turns == pick.Turns && s.ArmyId < pick.ArmyId))))
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
                            + $" strict; coverage {(cover ? "ok" : "missing")}"
                            + (peakRoster == null ? "" : $"; peak roster short {shortfall:0.#}") + ")"
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
                shortfall = RosterShortfall(roster);
                uncovered = WorthIt.UncoveredDefenderCount(bodies, opposition, defenderHexDefenseBonus, attackerCommander: commander);
                coverageOk = GroundCombatFeasibility.Clears(bodies, commander, opposition,
                    winChanceGate, defenderHexDefenseBonus, out win, out cover);
                clears = coverageOk
                    && AttackForceReadiness.ClearsFloor(power, minimumArmyPower, win, opposition);
            }

            if (heroDonor != null && !chosen.Contains(heroDonor))
                chosen.Add(heroDonor);

            var routeAdmissible = GroundCombatReinforcement.PreserveTaskRoute(snap.Map, host,
                targetHex, Game.Ai.SafeRouteProfile.Attack);
            if (routeAdmissible != null && !routeAdmissible(roster))
                return GroundCombatGatherPlan.Infeasible($"gather host #{host.Id}: final roster loses the task route");
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
            var finalRosters = new Dictionary<int, List<UnitData>> { [host.Id] = ledRoster.Where(roster.Contains).Concat(roster.Except(ledRoster)).ToList() };
            foreach (GatherSupport support in chosen)
                finalRosters[support.ArmyId] = support.Live.Members.Except(support.Incoming)
                    .Concat(support.Displaced).ToList();
            foreach (var entry in finalRosters)
            {
                ArmyData projected = ArmyData.CreateVisualSnapshot();
                projected.Owner = host.Owner;
                projected.Hex = host.Hex; // every convoy is at the rendezvous after its handoff
                projected.Members.AddRange(entry.Value);
                ArmySnapshot final = WorldAnalysis.ToArmySnapshot(projected, host.Owner, true, 0, snap.Intents, null);
                final.ArmyId = entry.Key;
                plan.DepartureArmies[entry.Key] = final;
            }
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
