using System.Collections.Generic;
using System.Linq;
using Game.Combat;
using Game.HexGrid;

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

        internal AttackMarchAssessment(AttackMarchVerdict verdict, float win, bool decisive,
            HexCoord? home, string reason)
        {
            Verdict = verdict;
            WinChance = win;
            Decisive = decisive;
            HomeHex = home;
            Reason = reason;
        }
    }

    // ===========================================================================================
    //  2026-10-16 (user decision) — the strategic verdict for an Attack army on its way to the main
    //  target, re-read every turn on the facts now known. Snapshot-pure; Continuity applies it.
    //    1. Own stronghold in danger: an honest hostile army threatens an own Citadel / Base that
    //       cannot hold without this army, this army arrives before the enemy's action and its
    //       return makes it hold -> DefendHome. The one exception: a decisive blow at the enemy's
    //       Citadel that lands before the enemy reaches ours (taking the Citadel ends that
    //       player) -> Continue. A threat whose army stands on the target and is struck decisively
    //       this turn is removed by the march itself, so it calls nobody home (2026-10-18).
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
                && !AttackObjectiveEvaluator.HasAssaultObservation(snap, target, out reason))
                return false;
            AttackMarchAssessment home = Assess(snap, target, actor, projected);
            if (home.Verdict == AttackMarchVerdict.DefendHome)
            { reason = "home_needs_the_army:" + home.Reason; return false; }
            AttackLocalAction contact = AttackTacticalOpportunity.Decide(snap, actor, target,
                snap.TurnNumber, false, null, intents);
            if (contact.Kind == AttackLocalActionKind.Retreat)
            { reason = "hostile_contact_on_path"; return false; }
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

        // Only an already-owned, hidden, activation-paid carrier that can finish for zero AP may delay the
        // terminal step. No speculative wait for production or for a distant scout.
        internal static bool WaitForDisruption(WorldSnapshot snap, ArmySnapshot actor, HexCoord target,
            out string reason, GroundCombatAssemblyPlan plan = null)
        {
            reason = null;
            if (snap?.Observer == null || actor == null
                || AiMapMemory.KnownBuildingDefenseBonusFor(snap.Observer, snap.Map, target,
                    (snap.Known?.Buildings ?? System.Array.Empty<AiMapMemory.KnownBuilding>())
                        .Where(b => b.Hex.Equals(target)).Select(b => b.Owner).FirstOrDefault()) <= 0f)
                return false;
            ArmySnapshot departing = actor;
            if (plan != null)
            {
                var projected = GroundCombatAssemblyPlanner.ProjectedDepartureArmies(snap, plan);
                if (projected.TryGetValue(actor.ArmyId, out ArmySnapshot assembled)) departing = assembled;
            }
            if (DisruptionRules.HasCarrier(departing.MembersWithHeroes)
                || DisruptionRules.HasCarrier(departing.Members)
                || DisruptionRules.HasCarrier(departing.Commander.SupportHeroes)) return false;
            foreach (MissionIntent intent in snap.AllIntents)
            {
                if (intent?.Status != IntentStatus.Active || intent.Scout?.Kind != ScoutTargetKind.DisruptStructure
                    || !intent.Scout.FocusHex.Equals(target) || !intent.PreferredMoverArmyId.HasValue) continue;
                ArmySnapshot carrier = snap.Self?.Armies?.FirstOrDefault(a => a != null
                    && a.ArmyId == intent.PreferredMoverArmyId.Value);
                if (carrier == null || carrier.Hex.Equals(target) || !carrier.IsHidden
                    || (!carrier.HasActivatedThisTurn && carrier.ActivationApCost > 0)
                    || !ReconAssignmentPlanner.CanDeliverDisruptionThisTurn(snap, carrier, target)) continue;
                reason = $"awaiting_disruption_delivery:#{carrier.ArmyId}";
                return true;
            }
            return false;
        }

        internal static AttackMarchAssessment Assess(WorldSnapshot snap, AttackTargetRef target,
            ArmySnapshot primary, IReadOnlyDictionary<int, ArmySnapshot> projectedArmies = null)
        {
            if (snap?.Self == null || primary == null)
                return new AttackMarchAssessment(AttackMarchVerdict.Continue, 0f, false, null, "no_primary");
            IReadOnlyList<WorthIt.DefendingArmy> opposition =
                KnownOpposition.OnHex(snap, target.Hex);
            float hexBonus = AiMapMemory.KnownHexDefenseBonus(snap?.Observer, null, target.Hex);
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

            HexCoord? home = StrongholdNeedingArmy(snap, primary, out int enemyEta, struckNow, projectedArmies);
            if (home.HasValue)
            {
                int strikeEta = ActiveDefenceObjectiveEvaluator.GroundArrivalEta(snap, primary.Hex,
                    target.Hex, primary.CurrentMovement, primary.MaxMovement, fastestByTurns: true);
                bool mateFirst = decisive && target.Kind == AttackTargetKind.Citadel
                    && (strikeEta == 0 || strikeEta < enemyEta);
                if (!mateFirst)
                    return new AttackMarchAssessment(AttackMarchVerdict.DefendHome, win, decisive, home,
                        $"own stronghold ({home.Value.Q},{home.Value.R}) falls without this army (enemy eta {enemyEta})");
            }
            if (!observed)
                return new AttackMarchAssessment(AttackMarchVerdict.Continue, 0f, false, null,
                    "defenders_unknown_or_stale; approach_only");
            if (!known)
                return new AttackMarchAssessment(AttackMarchVerdict.Continue, win, false, null, "observed_empty");
            if (win >= AbortFloor)
                return new AttackMarchAssessment(AttackMarchVerdict.Continue, win, decisive, null,
                    decisive ? "decisive" : "contested");
            return new AttackMarchAssessment(AttackMarchVerdict.Withdraw, win, false, null,
                $"win {win:0.00} < abort floor {AbortFloor:0.00}");
        }

        // The own Citadel / Base that an honest hostile army will take unless `army` returns: the
        // threat is imminent (activeDefenceWithdrawMaxEnemyEta), it does not hold without the army
        // (activeDefenceHoldWinChance), the army is already there or arrives before the enemy, and with the
        // army it holds. The Citadel first, then the nearest
        // enemy. Null when no stronghold needs this army. `struckNow` — the hex this army strikes
        // decisively this turn: an enemy standing there is destroyed before it can move, so its
        // threat is no reason to keep the army (ActiveDefence leaves such an enemy to Attack:
        // enemy_on_known_foreign_structure).
        internal static HexCoord? StrongholdNeedingArmy(WorldSnapshot snap, ArmySnapshot army,
            out int enemyEta, HexCoord? struckNow = null,
            IReadOnlyDictionary<int, ArmySnapshot> projectedArmies = null)
        {
            enemyEta = int.MaxValue;
            if (snap?.Self?.BaseHexes == null || army == null)
                return null;
            HexCoord? best = null;
            bool bestCitadel = false;
            var without = new HashSet<int> { army.ArmyId };
            foreach (AssetThreatSnapshot t in snap.Threat?.Threats ?? Enumerable.Empty<AssetThreatSnapshot>())
            {
                if (!ActiveDefenceObjectiveEvaluator.IsHonestPositionedHostile(t) || !t.CanDamage
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
                if (ActiveDefenceObjectiveEvaluator.HoldChanceAtAsset(snap, objective, null, null, enemy,
                        without, projectedArmies) >= AiConfigV2.activeDefenceHoldWinChance)
                    continue;
                // Several individually spareable donors can together remove a base's defence.
                // A projected departure that breaks an existing hold is forbidden even when the
                // assembled host is too far away to return and repair its own proposed damage.
                bool departureBreaksHold = projectedArmies?.Count > 0
                    && ActiveDefenceObjectiveEvaluator.HoldChanceAtAsset(snap, objective, null, null,
                        enemy) >= AiConfigV2.activeDefenceHoldWinChance;
                if (!departureBreaksHold)
                {
                    if (!army.Hex.Equals(t.Asset.Hex)
                        && !ActiveDefenceObjectiveEvaluator.CanArriveBeforeThreat(snap, army,
                            t.Asset.Hex, eta, out _, fastestByTurns: true)) continue;
                    if (ActiveDefenceObjectiveEvaluator.HoldChanceAtAsset(snap, objective, t.Asset.Hex,
                            new[] { army }, enemy, projectedArmies: projectedArmies)
                            < AiConfigV2.activeDefenceHoldWinChance) continue;
                }
                bool citadel = t.Asset.Kind == AssetKind.Citadel;
                if (best == null || (citadel && !bestCitadel) || (citadel == bestCitadel && eta < enemyEta))
                {
                    best = t.Asset.Hex;
                    bestCitadel = citadel;
                    enemyEta = eta;
                }
            }
            return best;
        }
    }
}
