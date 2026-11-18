using System.Collections.Generic;
using System.Linq;
using Game.Combat;
using Game.HexGrid;
using Game.Map;

namespace Game.Ai.V2
{
    internal enum AttackMarchVerdict { Continue, Withdraw, DefendHome }

    internal readonly struct AttackMarchAssessment
    {
        internal readonly AttackMarchVerdict Verdict;
        internal readonly float WinChance;
        internal readonly bool Decisive;
        internal readonly HexCoord? HomeHex;
        internal readonly string Reason;
        // DefendHome only: the threat that calls the army home (StrongholdNeedingArmy's pin).
        internal readonly AssetThreatSnapshot Pin;

        internal AttackMarchAssessment(AttackMarchVerdict verdict, float win, bool decisive,
            HexCoord? home, string reason, AssetThreatSnapshot pin = null)
        {
            Verdict = verdict;
            WinChance = win;
            Decisive = decisive;
            HomeHex = home;
            Reason = reason;
            Pin = pin;
        }
    }

    // ===========================================================================================
    //  2026-10-16 (user decision) — the strategic verdict for an Attack army on its way to the main
    //  target, re-read every turn on the facts now known. Snapshot-pure; Continuity applies it.
    //    1. Own stronghold in danger: an honest hostile army threatens an own Citadel / Base that
    //       cannot hold without this army, this army arrives before the enemy's action and its
    //       return makes it hold (own Citadel: its return is required even when it does not make
    //       it hold, 2026-11-18) -> DefendHome. The one exception: a decisive blow at the enemy's
    //       Citadel (taking the Citadel ends that player) -> Continue; when the stronghold that
    //       falls is our own Citadel the blow must also land before the enemy reaches it
    //       (2026-11-13: a Base may be traded for the enemy Citadel). A threat whose army stands
    //       on the target and is struck decisively this turn is removed by the march itself,
    //       so it calls nobody home (2026-10-18).
    //    2. Unknown defenders (location only) give no estimate -> Continue.
    //    3. Win chance at or above the abort floor (attackLocalMinWinChance) -> Continue: air
    //       support, reinforcement on the route and equipment keep improving the fight meanwhile.
    //    4. Below the floor -> Withdraw (Continuity first tries an existing support that meets the
    //       army ahead; the start gate attackStartMinWinChance keeps it from restarting on the
    //       same facts).
    // ===========================================================================================
    internal static class AttackMarchPolicy
    {
        internal static float AbortFloor => GroundCombatAdmissionPolicy.AttackLocalWinChanceGate;

        // The same departure decision for nomination, admission and the capability view.
        internal static bool CanStart(WorldSnapshot snap, AttackTargetRef target,
            GroundCombatAssemblyPlan plan, IMissionIntentView intents, out string reason,
            IReadOnlyDictionary<int, ArmySnapshot> departureOverride = null)
        {
            reason = null;
            if (plan?.Feasible != true) { reason = "plan_infeasible"; return false; }
            var projected = departureOverride ?? GroundCombatAssemblyPlanner.ProjectedDepartureArmies(snap, plan);
            ArmySnapshot actor = projected.TryGetValue(plan.BaseArmyId, out ArmySnapshot changed)
                ? changed : snap.Self.Armies.FirstOrDefault(a => a != null && a.ArmyId == plan.BaseArmyId);
            if (actor == null) { reason = "actor_unavailable"; return false; }
            if (AiV2Util.TravelCost(snap, actor, target.Hex, maxMovement: actor.MaxMovement,
                    terrainOnly: true) == int.MaxValue)
            { reason = "main_unreachable_terrain"; return false; }
            if (HexGridMath.Distance(actor.Hex, target.Hex) <= 1
                && !AttackObjectiveEvaluator.AssaultEntryReady(snap, target, actor, out reason))
                return false;
            AttackMarchAssessment home = Assess(snap, target, actor, projected);
            if (home.Verdict == AttackMarchVerdict.DefendHome)
            { reason = "home_needs_the_army:" + home.Reason; return false; }
            AttackLocalAction contact = AttackTacticalOpportunity.Decide(snap, actor, target,
                snap.TurnNumber, false, null, intents, departure: true);
            // The callers that decide on this refusal log it (Assembly REJECT_CANDIDATE, Gather
            // REJECT, Continuity "retired before departure"): the contact's facts travel in it.
            if (contact.Kind == AttackLocalActionKind.Retreat)
            {
                reason = $"hostile_contact_on_path:actor=#{actor.ArmyId} "
                    + $"enemy=#{contact.EnemyArmyId} at=({contact.Hex.Q},{contact.Hex.R}) "
                    + $"fightWin={contact.WinChance:0.00} < floor {AbortFloor:0.00}";
                return false;
            }
            if (AttackRetreatWitness.Blocks(intents, snap, target, actor, out string why, out _))
            { reason = "waiting_for_changed_odds:" + why; return false; }
            return true;
        }

        // Preparation and continuity may gather before target observation is complete. They still
        // owe every home the same final-departure defence proof as the fresh assault.
        internal static string GatherDepartureReason(WorldSnapshot snap, AttackTargetRef target,
            GroundCombatGatherPlan plan)
        {
            if (plan?.Feasible != true) return "plan_infeasible";
            ArmySnapshot actor = plan.DepartureArmies.TryGetValue(plan.HostArmyId, out var projected)
                ? projected : snap.Self.Armies.FirstOrDefault(a => a?.ArmyId == plan.HostArmyId);
            AttackMarchAssessment home = Assess(snap, target, actor, plan.DepartureArmies);
            return home.Verdict == AttackMarchVerdict.DefendHome ? "home_needs_the_army:" + home.Reason : null;
        }

        // 2026-11-13 (owner, P1) — a structure-defended target is approached to distance 1 only
        // when the step onto it can follow: the entry gate already holds (observation aside —
        // approach_only on stale data stays legal), or the counted carrier on its DisruptStructure
        // leg arrives no later than this fist (AttackSupportProjection.CountedCarrier). Otherwise
        // the fist waits out of the target's reach instead of beside it (Korrin T29-T31: Cinderfall
        // stood on (0,-5) three turns, 5 AP spent on the march).
        internal static bool MayApproachAssault(WorldSnapshot snap, ArmySnapshot fist, AttackTargetRef target,
            out string reason)
        {
            reason = null;
            if (AttackObjectiveEvaluator.HasAssaultDisruption(snap, target, fist, out reason))
                return true;
            int eta = AttackSupportProjection.ArmyEta(snap, fist, target.Hex);
            if (eta != int.MaxValue && AttackSupportProjection.CountedCarrier(snap, target.Hex, eta).HasValue)
            {
                reason = null;
                return true;
            }
            reason = "awaiting_disruption_carrier_before_approach";
            return false;
        }

        internal static AttackMarchAssessment Assess(WorldSnapshot snap, AttackTargetRef target,
            ArmySnapshot primary, IReadOnlyDictionary<int, ArmySnapshot> projectedArmies = null)
        {
            if (snap?.Self == null || primary == null)
                return new AttackMarchAssessment(AttackMarchVerdict.Continue, 0f, false, null, "no_primary");
            // 2026-11-02 — priced against what the march meets: the committed support counted
            // (AttackSupportProjection). A bound wing in flight no longer reads as a lost fight.
            AttackSupportForecast forecast = AttackSupportProjection.For(snap, target.Hex, primary);
            IReadOnlyList<WorthIt.DefendingArmy> opposition = forecast.Opposition;
            float hexBonus = forecast.HexBonus;
            GroundCombatFeasibility.Clears(primary.Members ?? System.Array.Empty<WorthIt.DefenderProfile>(),
                primary.Commander, opposition, 0f, hexBonus, out float win, out _, requireCoverage: false);
            bool known = opposition != null && opposition.Any(a => a.Units.Any(u => u.IsGroundCombatant));
            bool observed = AttackObjectiveEvaluator.HasAssaultObservation(snap, target, out _);
            bool decisive = observed && AttackForceReadiness.IsOpportunity(win, opposition);
            // 2026-10-18 — a decisive blow that lands this very turn (before the enemy moves again)
            // removes every threat whose army stands on the target: such a threat calls nobody home.
            HexCoord? struckNow = decisive
                && ActiveDefenceObjectiveEvaluator.CanReachNow(snap, primary, target.Hex, out _)
                ? target.Hex : (HexCoord?)null;

            HexCoord? home = StrongholdNeedingArmy(snap, primary, out int enemyEta, out bool homeIsCitadel,
                out AssetThreatSnapshot pinning, struckNow, projectedArmies);
            if (home.HasValue)
            {
                // 2026-11-13 (owner, match Sable vs Korrin) — trading an own Base for the enemy's
                // Citadel is the AI's option, not a forbidden move: taking the Citadel ends that
                // player, losing a Base does not. The race (the blow lands before the enemy acts)
                // is required only when the stronghold that falls is our own Citadel.
                int strikeEta = ActiveDefenceObjectiveEvaluator.GroundArrivalEta(snap, primary.Hex,
                    target.Hex, primary.CurrentMovement, primary.MaxMovement, fastestByTurns: true);
                bool mateFirst = decisive && target.Kind == AttackTargetKind.Citadel
                    && (!homeIsCitadel || strikeEta == 0 || strikeEta < enemyEta);
                if (!mateFirst)
                    return new AttackMarchAssessment(AttackMarchVerdict.DefendHome, win, decisive, home,
                        $"own stronghold ({home.Value.Q},{home.Value.R}) falls without this army (enemy eta {enemyEta}; {DescribePin(snap, pinning)})",
                        pinning);
            }
            if (!observed)
                return new AttackMarchAssessment(AttackMarchVerdict.Continue, 0f, false, null,
                    "defenders_unknown_or_stale; approach_only");
            if (!known)
                return new AttackMarchAssessment(AttackMarchVerdict.Continue, win, false, null, "observed_empty");
            if (win >= AbortFloor)
                return new AttackMarchAssessment(AttackMarchVerdict.Continue, win, decisive, null,
                    decisive ? "decisive" : "contested");
            // 2026-11-02 — a capture out of reach may still be a favorable trade on this hex: the
            // same read readiness started the march on (AttackExchangePolicy), re-asked every turn.
            AttackExchange exchange = AttackExchangePolicy.Assess(
                primary.Members ?? System.Array.Empty<WorthIt.DefenderProfile>(), primary.Commander,
                opposition, hexBonus);
            if (exchange.Favorable)
                return new AttackMarchAssessment(AttackMarchVerdict.Continue, win, false, null,
                    $"{exchange.Describe()} ({forecast.Describe()})");
            return new AttackMarchAssessment(AttackMarchVerdict.Withdraw, win, false, null,
                $"win {win:0.00} < abort floor {AbortFloor:0.00}; {exchange.Describe()} ({forecast.Describe()})");
        }

        // 2026-11-02 — the terminal step waits for the bound wings whose strikes the march was
        // priced on (AttackSupportProjection counts a first strike up to one turn after arrival),
        // while one of those strikes is still to come and the fist does not clear the fight
        // without them. The wait ends when the wings strike or Continuity releases them (never
        // took off / series over), so it is never a timer.
        internal static bool WaitForAirStrike(WorldSnapshot snap, AttackSupportForecast forecast,
            ArmySnapshot actor, out string reason, GroundCombatAssemblyPlan plan = null)
        {
            reason = null;
            if (snap?.Observer == null || actor == null || forecast.Air == null)
                return false;
            List<CountedAirStrike> pending = forecast.Air.Where(s => s.Bound && s.FirstStrikeEta <= 1)
                .Where(s =>
                {
                    ArmyData wing = OwnWorld.Of(snap.Observer).Army(s.ArmyId);
                    return wing == null || Game.Aviation.AviationRules.IsAirfield(wing)
                        || !wing.Members.Any(u => u != null && u.HasAirAttackedThisTurn);
                }).ToList();
            if (pending.Count == 0)
                return false;
            ArmySnapshot departing = actor;
            if (plan != null)
            {
                var projected = GroundCombatAssemblyPlanner.ProjectedDepartureArmies(snap, plan);
                if (projected.TryGetValue(actor.ArmyId, out ArmySnapshot assembled)) departing = assembled;
            }
            if (GroundCombatFeasibility.Clears(departing.Members ?? System.Array.Empty<WorthIt.DefenderProfile>(),
                    departing.Commander, forecast.BaseOpposition, GroundCombatAdmissionPolicy.AttackLocalWinChanceGate,
                    forecast.BaseHexBonus, out _, out _))
                return false;
            reason = "awaiting_air_strike:" + string.Join(",", pending.Select(s => $"#{s.ArmyId}@eta{s.FirstStrikeEta}"));
            return true;
        }

        // The own Citadel / Base that an honest hostile army will take unless `army` returns: the
        // threat is imminent (activeDefenceWithdrawMaxEnemyEta), it does not hold without the army
        // (activeDefenceHoldWinChance), the army is already there or arrives before the enemy, and with the
        // army it holds (an own Citadel: the army is needed even when it holds without
        // reaching that bar — its loss ends the player). The Citadel first, then the nearest
        // enemy. Null when no stronghold needs this army. `struckNow` — the hex this army strikes
        // decisively this turn: an enemy standing there is destroyed before it can move, so its
        // threat is no reason to keep the army (ActiveDefence leaves such an enemy to Attack:
        // enemy_on_known_foreign_structure).
        internal static HexCoord? StrongholdNeedingArmy(WorldSnapshot snap, ArmySnapshot army,
            out int enemyEta, HexCoord? struckNow = null,
            IReadOnlyDictionary<int, ArmySnapshot> projectedArmies = null) =>
            StrongholdNeedingArmy(snap, army, out enemyEta, out _, struckNow, projectedArmies);

        // `isCitadel` — the returned stronghold is an own Citadel (the Citadel is preferred).
        internal static HexCoord? StrongholdNeedingArmy(WorldSnapshot snap, ArmySnapshot army,
            out int enemyEta, out bool isCitadel, HexCoord? struckNow = null,
            IReadOnlyDictionary<int, ArmySnapshot> projectedArmies = null) =>
            StrongholdNeedingArmy(snap, army, out enemyEta, out isCitadel, out _, struckNow, projectedArmies);

        // `pinning` — the threat that keeps the army home (its contact: id, knowledge, last
        // position). Read by the march reason and by Attack's observation need for that contact
        // (AttackObjectiveEvaluator.ObservationNeeds: a pin by a remembered, unobserved army is
        // a Recon job, 2026-11-15).
        internal static HexCoord? StrongholdNeedingArmy(WorldSnapshot snap, ArmySnapshot army,
            out int enemyEta, out bool isCitadel, out AssetThreatSnapshot pinning,
            HexCoord? struckNow = null, IReadOnlyDictionary<int, ArmySnapshot> projectedArmies = null)
        {
            enemyEta = int.MaxValue;
            isCitadel = false;
            pinning = null;
            if (snap?.Self?.BaseHexes == null || army == null)
                return null;
            HexCoord? best = null;
            bool bestCitadel = false;
            var without = new HashSet<int> { army.ArmyId };
            HashSet<int> notFree = null; // armies that cannot answer the threat instead (lazy)
            foreach (AssetThreatSnapshot t in snap.Threat?.Threats ?? Enumerable.Empty<AssetThreatSnapshot>())
            {
                // 2026-11-15 (owner) — only a force that can take the stronghold pins (IsPinCandidate).
                if (!ActiveDefenceObjectiveEvaluator.IsPinCandidate(t)
                    || !t.EnemyEta.HasValue || !snap.Self.BaseHexes.Contains(t.Asset.Hex)
                    || (t.Asset.Kind != AssetKind.Citadel && t.Asset.Kind != AssetKind.Base))
                    continue;
                int eta = t.EnemyEta.Value;
                // The same imminence ActiveDefence answers (threat_not_imminent beyond it): a
                // distant contact does not call a march home.
                if (eta > AiConfigV2.activeDefenceWithdrawMaxEnemyEta)
                    continue;
                if (struckNow.HasValue && t.Contact.Position.HasValue
                    && t.Contact.Position.Value.Equals(struckNow.Value))
                    continue;
                IReadOnlyList<WorthIt.DefendingArmy> enemy =
                    KnownOpposition.OfEnemyArmy(snap, t.Contact.Army.ArmyId);
                if (enemy == null)
                    continue;
                var objective = new ActiveDefenceObjective { Target = new ActiveDefenceMissionTarget
                    { ProtectedAssetHex = t.Asset.Hex, ProtectedAssetKind = t.Asset.Kind } };
                // 2026-11-15 (owner) — a remembered force pins only as strongly as the memory is
                // sure it is there (ActiveDefenceObjectiveEvaluator.FallsWithout / PinConfidence,
                // the garrison floor's decay): one not seen for 11 turns no longer holds the fist
                // like one seen yesterday.
                float confidence = ActiveDefenceObjectiveEvaluator.PinConfidence(snap, t.Contact);
                if (!ActiveDefenceObjectiveEvaluator.FallsWithout(ActiveDefenceObjectiveEvaluator.HoldChanceAtAsset(
                        snap, objective, null, null, enemy, without, projectedArmies), confidence))
                    continue;
                // 2026-11-15 (owner) — a force another free army of ours destroys before it reaches
                // the stronghold is ActiveDefence's intercept, not a reason to keep this army home.
                if (notFree == null)
                {
                    notFree = new HashSet<int>(ActorCommitments.FromIntents((snap.AllIntents
                            ?? Enumerable.Empty<MissionIntent>()).Where(i => i != null
                            && i.Status == IntentStatus.Active && i.Kind != MissionKind.Scout), snap, null)
                        .ClaimedArmyIds);
                    notFree.Add(army.ArmyId);
                    if (projectedArmies != null)
                        notFree.UnionWith(projectedArmies.Keys);
                }
                if (ActiveDefenceObjectiveEvaluator.CanDefeatBeforeThreat(snap, t, notFree, out _))
                    continue;
                // Several individually spareable donors can together remove a base's defence.
                // A projected departure that breaks an existing hold is forbidden even when the
                // assembled host is too far away to return and repair its own proposed damage.
                bool departureBreaksHold = projectedArmies?.Count > 0
                    && !ActiveDefenceObjectiveEvaluator.FallsWithout(ActiveDefenceObjectiveEvaluator.HoldChanceAtAsset(
                        snap, objective, null, null, enemy), confidence);
                bool citadel = t.Asset.Kind == AssetKind.Citadel;
                if (!departureBreaksHold)
                {
                    if (!army.Hex.Equals(t.Asset.Hex)
                        && !ActiveDefenceObjectiveEvaluator.CanArriveBeforeThreat(snap, army,
                            t.Asset.Hex, eta, out _, fastestByTurns: true)) continue;
                    // 2026-11-18 (owner, match Tessek vs Halden T19-T20) — losing the own Citadel
                    // ends the player, so an army that can be back in time is needed there even
                    // when it cannot lift the hold to activeDefenceHoldWinChance: leaving it is
                    // worth something only through a decisive blow that lands first (Assess,
                    // mateFirst). Rust Bite left a Citadel held 0.04 for a Citadel two turns away
                    // while the enemy stood one turn out. A Base keeps the "with it, it holds" rule.
                    if (!citadel && ActiveDefenceObjectiveEvaluator.HoldChanceAtAsset(snap, objective,
                            t.Asset.Hex, new[] { army }, enemy, projectedArmies: projectedArmies)
                            < AiConfigV2.activeDefenceHoldWinChance) continue;
                }
                if (best == null || (citadel && !bestCitadel) || (citadel == bestCitadel && eta < enemyEta))
                {
                    best = t.Asset.Hex;
                    bestCitadel = citadel;
                    enemyEta = eta;
                    pinning = t;
                }
            }
            isCitadel = bestCitadel;
            return best;
        }

        // 2026-11-15 (owner) — does enemy `enemyArmyId` keep this Attack fist home? Either the march
        // verdict's pin (StrongholdNeedingArmy) names it, or the fist is the last defender held
        // ONLY by it (IsPinnedStrongholdDefender without that enemy releases it). Read by
        // ActiveDefence: such a force is intercepted even while the stronghold holds with the fist.
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<WorldSnapshot,
            Dictionary<(int, int), bool>> s_blockedBy = new();

        internal static bool IsBlockedBy(WorldSnapshot snap, ArmySnapshot fist, int enemyArmyId)
        {
            if (snap == null || fist == null)
                return false;
            Dictionary<(int, int), bool> memo = s_blockedBy.GetOrCreateValue(snap);
            if (memo.TryGetValue((fist.ArmyId, enemyArmyId), out bool known))
                return known;
            bool blocked = (StrongholdNeedingArmy(snap, fist, out _, out _, out AssetThreatSnapshot pin) != null
                    && pin?.Contact?.Army?.ArmyId == enemyArmyId)
                || (ActiveDefenceObjectiveEvaluator.IsPinnedStrongholdDefender(snap, fist)
                    && !ActiveDefenceObjectiveEvaluator.IsPinnedStrongholdDefender(snap, fist,
                        ignoreEnemyArmyId: enemyArmyId));
            memo[(fist.ArmyId, enemyArmyId)] = blocked;
            return blocked;
        }

        // The pin's contact for logs: "#40 last_known age 2 at (1,0)".
        internal static string DescribePin(WorldSnapshot snap, AssetThreatSnapshot pinning)
        {
            EnemyContactSnapshot c = pinning?.Contact;
            if (c?.Army == null)
                return "contact unknown";
            string at = c.Position.HasValue ? $" at ({c.Position.Value.Q},{c.Position.Value.R})" : "";
            return c.Knowledge == ContactKnowledge.Exact
                ? $"#{c.Army.ArmyId} observed{at}"
                : $"#{c.Army.ArmyId} last_known age {c.AgeTurns(snap?.TurnNumber ?? 0)}{at}";
        }

        // 2026-11-15 (match Brynn vs Grimm) — the hex Recon must look at so a remembered army
        // stops pinning `army` on its worst-case position: the contact's last known hex, while it
        // has not been looked at since that sighting (seen empty afterwards, revisiting the same
        // hex adds nothing; the unobserved-army model then rules). Null when no pin, the pin is
        // an observed contact, or the hex is already fresher than the sighting.
        internal static HexCoord? PinObservationHex(WorldSnapshot snap, ArmySnapshot army)
        {
            if (StrongholdNeedingArmy(snap, army, out _, out _, out AssetThreatSnapshot pin) == null)
                return null;
            EnemyContactSnapshot c = pin?.Contact;
            if (c == null || c.Knowledge == ContactKnowledge.Exact || !c.Position.HasValue)
                return null;
            if (ReconIntelSnapshotRegistry.TryGetIntelAge(snap, c.Position.Value, out int hexAge)
                && hexAge < c.AgeTurns(snap.TurnNumber))
                return null;
            return c.Position.Value;
        }
    }
}
