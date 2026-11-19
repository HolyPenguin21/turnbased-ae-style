using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Game.Combat;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using UnityEngine;

namespace Game.Ai.V2
{
    // Response — THE answer to "how does this player respond to this threat" (Intercept / Defer /
    // Regroup / Shortage), the hold estimate at the asset and the defence pool.
    public static partial class ActiveDefenceObjectiveEvaluator
    {
        // One snapshot decision shared by Planner, Demand and regroup Continuity. Claims may
        // only be opened for this exact defence's own regroup legs, never for a withdrawal or
        // a different operation. A present garrison is assessed BEFORE proposing any movement.
        internal static ActiveDefenceResponse AssessResponse(WorldSnapshot snap,
            ActiveDefenceObjective objective, ISet<int> committed, ICollection<int> withdrawing,
            int? pinnedActor, IEnumerable<MissionIntent> activeIntents = null,
            ISet<int> fundedRegroupArmyIds = null)
        {
            if (objective != null && objective.Target.InterceptPurpose
                == ActiveDefenceInterceptPurpose.ImmediateOpportunity)
                return AssessImmediate(snap, objective, committed, withdrawing, pinnedActor);
            if (objective == null || !IsDefendableAsset(snap, objective.Target.ProtectedAssetKind,
                    objective.Target.ProtectedAssetHex) || snap?.Self?.Armies == null)
                return null;
            IReadOnlyList<WorthIt.DefendingArmy> opposition = KnownOpposition.OfEnemyArmy(snap, objective.Target.EnemyArmyId);
            if (opposition == null || OnKnownForeignStructure(snap, objective.Target.LastKnownHex))
                return null;
            HexCoord asset = objective.Target.ProtectedAssetHex;
            var unavailable = committed == null ? new HashSet<int>() : new HashSet<int>(committed);
            var ownRegroup = new HashSet<int>((activeIntents ?? Enumerable.Empty<MissionIntent>())
                .Where(i => i != null && (i.Status == IntentStatus.Active
                    || (i.Status == IntentStatus.Suspended
                        && (i.Suspended == SuspendReason.PoolExhausted
                            || i.Suspended == SuspendReason.CapabilityUnavailable)))
                    && i.ActiveDefence?.Phase == ActiveDefencePhase.Return
                    && i.ActiveDefence.ReturnPurpose == ActiveDefenceReturnPurpose.RegroupForAsset
                    && i.ActiveDefence.EnemyArmyId == objective.Target.EnemyArmyId
                    && i.ActiveDefence.ProtectedAssetHex.Equals(asset)
                    && i.ActiveDefence.ReturnHex.HasValue && i.ActiveDefence.ReturnHex.Value.Equals(asset) && i.ActiveDefence.PrimaryArmyId.HasValue)
                .Select(i => i.ActiveDefence.PrimaryArmyId.Value));
            // Read other owners through the canonical commitment factory, including Economy,
            // Raid, Attack supports and other ActiveDefence operations.
            var otherOwners = ActorCommitments.FromIntents(
                (activeIntents ?? Enumerable.Empty<MissionIntent>()).Where(i =>
                    i?.ActiveDefence?.Phase != ActiveDefencePhase.Return
                    || i.ActiveDefence.ReturnPurpose != ActiveDefenceReturnPurpose.RegroupForAsset
                    || i.ActiveDefence.EnemyArmyId != objective.Target.EnemyArmyId
                    || !i.ActiveDefence.ProtectedAssetHex.Equals(asset)), snap, null);
            if (fundedRegroupArmyIds != null) ownRegroup.UnionWith(fundedRegroupArmyIds);
            ownRegroup.ExceptWith(otherOwners.ClaimedArmyIds);
            unavailable.ExceptWith(ownRegroup);
            var response = new ActiveDefenceResponse { Opposition = opposition };
            response.HoldWinChance = HoldChanceAtAsset(snap, objective, null, null, opposition, unavailable);
            if (response.HoldWinChance >= AiConfigV2.activeDefenceHoldWinChance)
            {
                // 2026-11-15 (owner, match Brynn vs Grimm T32-T35) — the stronghold holds only
                // because an Attack fist sits on it: a force that keeps that fist home is destroyed
                // when a direct intercept beats it before it arrives (Grim March vs Cinderfall,
                // win 1.00, was deferred "asset_holds"; three turns later Cinderfall recalled the
                // assault). Same plan, gate and timing as any direct response.
                string blocked = BlockedAttackFist(snap, objective, pinnedActor);
                if (blocked != null)
                {
                    float held = response.HoldWinChance;
                    if (TryDirectResponse(snap, objective, opposition, committed, withdrawing, pinnedActor, response)
                        && response.Kind == ActiveDefenceResponseKind.Intercept)
                    {
                        response.Reason = "intercept_attack_blocker:" + blocked;
                        response.UnblocksAttack = true;
                        return TraceResponse(snap, objective, response, committed);
                    }
                    response.Plan = null;
                    response.ExcludedArmyIds = null;
                    response.HoldWinChance = held;
                }
                response.Kind = ActiveDefenceResponseKind.Defer;
                response.Reason = blocked != null ? "asset_holds; attack_blocker_not_interceptable:" + blocked
                    : "asset_holds";
                return TraceResponse(snap, objective, response, committed);
            }
            if (TryDirectResponse(snap, objective, opposition, committed, withdrawing, pinnedActor, response))
                return TraceResponse(snap, objective, response, committed);
            // Unknown ETA is uncertainty, never an immediate retreat order. Strategic reserve
            // still sees the honest contact through ForceNeedModel.
            if (!objective.Target.EnemyEta.HasValue
                || objective.Target.EnemyEta.Value > AiConfigV2.activeDefenceWithdrawMaxEnemyEta)
            {
                response.Kind = ActiveDefenceResponseKind.Defer;
                response.Reason = !objective.Target.EnemyEta.HasValue ? "enemy_eta_unknown" : "threat_not_imminent";
                return TraceResponse(snap, objective, response, committed);
            }
            List<ArmySnapshot> usable = BuildDefencePool(snap, unavailable, null);
            List<ArmySnapshot> timely = usable.Where(a => a.Hex.Equals(asset)
                || ((fundedRegroupArmyIds == null || fundedRegroupArmyIds.Contains(a.ArmyId))
                    && !IsPinnedStrongholdDefender(snap, a, unavailable)
                    && CanArriveBeforeThreat(snap, a, asset, objective.Target.EnemyEta, out _))).ToList();
            response.RequiredPower = GroundCombatFeasibility.RequiredPower(WorthIt.UnitsOf(opposition), 0f);
            response.AvailablePower = GroundCombatFeasibility.AggregatePower(timely)
                + snap.Self.Armies.Where(a => a != null && a.IsGarrison && a.Hex.Equals(asset))
                    .Sum(a => Mathf.Max(0f, a.EffectiveArmyPower));
            response.StrongestPower = timely.Count == 0 ? 0f : timely.Max(a => Mathf.Max(0f, a.EffectiveArmyPower));
            var selected = new List<ArmySnapshot>();
            foreach (ArmySnapshot a in timely.Where(a => !a.Hex.Equals(asset))
                .OrderBy(a => ArrivalEta(snap, a, asset))
                .ThenBy(a => a.HasActivatedThisTurn ? 0 : a.ActivationApCost)
                .ThenByDescending(a => a.EffectiveArmyPower).ThenBy(a => a.ArmyId))
            {
                selected.Add(a);
                float chance = HoldChanceAtAsset(snap, objective, asset, selected, opposition, unavailable);
                // Zero sampled wins for one body do not prove it contributes nothing: several
                // individually losing defenders can wear down the same attacking roster.
                response.HoldWinChance = chance;
                if (chance < AiConfigV2.activeDefenceHoldWinChance) continue;
                // Remove any redundant earlier pick under the same simulator, without a global
                // subset search. Stable order and deterministic estimates keep identical plans stable.
                for (int i = selected.Count - 1; i >= 0; i--)
                {
                    var without = selected.Where((_, index) => index != i).ToList();
                    float smaller = HoldChanceAtAsset(snap, objective, asset, without, opposition, unavailable);
                    if (smaller < AiConfigV2.activeDefenceHoldWinChance) continue;
                    selected.RemoveAt(i);
                    response.HoldWinChance = smaller;
                }
                response.Kind = ActiveDefenceResponseKind.Regroup;
                response.RegroupHex = asset;
                response.Reason = "regroup_sufficient";
                response.ReinforcementArmyIds.UnionWith(selected.Select(m => m.ArmyId));
                response.Movers.AddRange(selected.Where(m => withdrawing == null || !withdrawing.Contains(m.ArmyId)));
                return TraceResponse(snap, objective, response, committed);
            }
            response.Kind = ActiveDefenceResponseKind.Shortage;
            response.Reason = "insufficient_defence_power";
            // 2026-11-18 (owner, match Tessek vs Halden) — the own Citadel's loss ends the player:
            // a shortage there still gathers every army that arrives before the threat on the
            // Citadel (best effort, the Shortage stays and keeps its production demand), instead
            // of sending the threatened ones to another Base that outlives the game by nothing.
            if (objective.Target.ProtectedAssetKind == AssetKind.Citadel && selected.Count > 0)
            {
                response.RegroupHex = asset;
                response.ReinforcementArmyIds.UnionWith(selected.Select(m => m.ArmyId));
                response.Movers.AddRange(selected.Where(m => withdrawing == null || !withdrawing.Contains(m.ArmyId)));
            }
            // A shortage at one base is not authority to evacuate every free field army.
            response.Movers.AddRange(usable.Where(a => (withdrawing == null || !withdrawing.Contains(a.ArmyId))
                && !response.ReinforcementArmyIds.Contains(a.ArmyId)
                && !snap.Self.BaseHexes.Contains(a.Hex) && NeedsSafeWithdrawal(snap, a)));
            return TraceResponse(snap, objective, response, committed);
        }

        private static ActiveDefenceResponse TraceResponse(WorldSnapshot snap, ActiveDefenceObjective objective,
            ActiveDefenceResponse response, ISet<int> committed)
        {
            var t = objective.Target;
            string free = string.Join(",", snap.Self.Armies.Where(a => GroundCombatActorEligibility.IsStructuralActor(a)
                && (committed == null || !committed.Contains(a.ArmyId))).OrderBy(a => a.ArmyId)
                .Select(a => $"{a.ArmyId}:mp{a.CurrentMovement}/{a.MaxMovement}:ap{(a.HasActivatedThisTurn ? 0 : a.ActivationApCost)}"));
            string selected = string.Join(",", response.ReinforcementArmyIds.OrderBy(id => id).Select(id =>
            {
                ArmySnapshot actor = snap.Self.Armies.First(a => a.ArmyId == id);
                return $"{id}:eta{ArrivalEta(snap, actor, t.ProtectedAssetHex)}";
            }));
            string committedList = string.Join(",", (committed ?? new HashSet<int>()).OrderBy(id => id));
            // Several readers ask the same question in one pass under different exclusion sets
            // (planner, ImminentResponseArmyIds, Demand, Provisioning). Keyed by that set, each
            // context logs its answer once instead of the two alternating lines re-printing.
            AiDebugLog.WriteDeduped($"defence-response:{snap.Observer?.Nickname}:{snap.TurnNumber}:{t.EnemyArmyId}:{committedList}",
                $"[AI][V2][ActiveDefence][Response] turn={snap.TurnNumber} player={snap.Observer?.Nickname} "
                + $"enemy={t.EnemyArmyId} asset={t.ProtectedAssetKind}@{t.ProtectedAssetHex} "
                + $"enemyEta={t.EnemyEta?.ToString() ?? "unknown"} hold={response.HoldWinChance:0.00} "
                + $"free=[{free}] committed=[{committedList}] "
                + $"selected=[{selected}] destination={response.RegroupHex} decision={response.Kind} reason={response.Reason}");
            return response;
        }

        // The chance the defence holds the threatened Base / Citadel: the enemy fist ATTACKS, the
        // armies standing at the asset (garrison included) and — for the regroup hex — the usable
        // armies that can gather there DEFEND with the hex's terrain + structure defence. -1 for any
        // other asset (a facility is not held by standing on it) or when no defender exists.
        internal static float HoldChanceAtAsset(WorldSnapshot snap, ActiveDefenceObjective objective,
            HexCoord? regroupHex, IReadOnlyList<ArmySnapshot> gatherable,
            IReadOnlyList<WorthIt.DefendingArmy> opposition, ISet<int> committed = null,
            IReadOnlyDictionary<int, ArmySnapshot> projectedArmies = null)
        {
            if (snap?.Self?.Armies == null || objective == null || opposition == null
                || opposition.Count == 0 || snap.Observer == null)
                return -1f;
            AssetKind kind = objective.Target.ProtectedAssetKind;
            if (!IsDefendableAsset(snap, kind, objective.Target.ProtectedAssetHex))
                return -1f;
            HexCoord hex = objective.Target.ProtectedAssetHex;
            // Analysis freezes the complete terrain + structure bonus. Reuse that fact for both
            // existing and arriving defenders, without a second live world read or a zero clamp.
            StrategicAssetSnapshot asset = snap.Threat?.Assets?.FirstOrDefault(a => a.Hex.Equals(hex)
                && a.Kind == kind);
            float bonus = asset?.HexDefenseBonus ?? (snap.Known?.Buildings
                ?? System.Array.Empty<AiMapMemory.KnownBuilding>())
                .Where(b => b.Hex.Equals(hex) && (b.IsBase || b.IsStartingCitadel) && b.Owner == snap.Observer)
                .Select(b => b.Defense).FirstOrDefault();
            float buildingBonus = asset?.BuildingDefenseBonus ?? (snap.Known?.Buildings
                ?? System.Array.Empty<AiMapMemory.KnownBuilding>())
                .Where(b => b.Hex.Equals(hex) && (b.IsBase || b.IsStartingCitadel) && b.Owner == snap.Observer)
                .Select(b => UnityEngine.Mathf.Max(0f, b.Defense)).FirstOrDefault();
            var seen = new HashSet<int>();
            var defenders = new List<WorthIt.DefendingArmy>();
            void Add(ArmySnapshot a)
            {
                if (a != null && projectedArmies != null
                    && projectedArmies.TryGetValue(a.ArmyId, out ArmySnapshot projected)) a = projected;
                if (a == null || a.IsAir || a.IsPrison || a.Members == null || a.Members.Count == 0
                    || !seen.Add(a.ArmyId))
                    return;
                defenders.Add(new WorthIt.DefendingArmy(a.Members, a.Commander, bonus, a.ArmyId,
                    buildingBonus, environment: KnownOpposition.WeatherAt(snap, hex)));
            }
            foreach (ArmySnapshot original in snap.Self.Armies)
            {
                ArmySnapshot resident = original;
                if (resident != null && projectedArmies != null
                    && projectedArmies.TryGetValue(resident.ArmyId, out ArmySnapshot projected)) resident = projected;
                // The one resident rule, shared with the garrison floor (AiArmyRoles.ResidentFieldPower).
                if (resident != null && resident.Hex.Equals(hex)
                    && AiArmyRoles.CountsAsHexResident(resident.IsGarrison, resident.IsAir, resident.IsPrison,
                        resident.Members != null && resident.Members.Count > 0,
                        committed != null && committed.Contains(resident.ArmyId)))
                    Add(resident);
            }
            if (regroupHex.HasValue && regroupHex.Value.Equals(hex) && gatherable != null)
                foreach (ArmySnapshot a in gatherable)
                    Add(a);
            if (defenders.Count == 0)
                return 0f;
            var fist = opposition[0];
            float attackerWins = WorthIt.EstimateHexControl(fist.Units, fist.Commander,
                defenders, bonus).WinChance;
            return Mathf.Clamp01(1f - attackerWins);
        }

        private static bool TryDirectResponse(WorldSnapshot snap, ActiveDefenceObjective objective,
            IReadOnlyList<WorthIt.DefendingArmy> opposition, ISet<int> committed,
            ICollection<int> withdrawing, int? pinnedActor, ActiveDefenceResponse response)
        {
            var planExcluded = committed == null ? new HashSet<int>() : new HashSet<int>(committed);
            if (withdrawing != null) planExcluded.UnionWith(withdrawing);
            if (pinnedActor.HasValue) planExcluded.Remove(pinnedActor.Value);
            // 2026-11-15 - a defender held home only by THIS enemy may go and destroy it first
            // (the intercept-before-threat timing below still applies).
            foreach (ArmySnapshot a in snap.Self.Armies.Where(GroundCombatActorEligibility.IsStructuralActor))
                if (IsPinnedStrongholdDefender(snap, a, committed, ignoreEnemyArmyId: objective.Target.EnemyArmyId))
                    planExcluded.Add(a.ArmyId);
            // Timing is a complete-plan constraint. Filtering the original host here misses slow
            // recruits and can also discard a host whose legal departure roster becomes faster.
            GroundCombatAssemblyPlan plan = GroundCombatAssemblyPlanner.Plan(snap,
                new GroundCombatAssemblyRequest
                {
                    Opposition = opposition,
                    WinChanceGate = GroundCombatAdmissionPolicy.PinnedOrFreshGate(pinnedActor.HasValue),
                    PreferredPrimaryArmyId = pinnedActor,
                    PinToPreferred = pinnedActor.HasValue,
                    ExcludedArmyIds = planExcluded,
                    PinIgnoredEnemyArmyId = objective.Target.EnemyArmyId,
                    AcceptPlan = candidate => CanInterceptPlanBeforeThreat(snap, objective.Target, candidate),
                });
            if (plan.Feasible)
            {
                response.Kind = ActiveDefenceResponseKind.Intercept;
                response.Plan = plan;
                response.ExcludedArmyIds = planExcluded;
                response.Reason = "direct_response";
                return true;
            }
            if (pinnedActor.HasValue && !planExcluded.Contains(pinnedActor.Value)
                && CanInterceptPlanBeforeThreat(snap, objective.Target,
                    GroundCombatAssemblyPlanner.PlanForArmyAtThreshold(snap, opposition,
                        pinnedActor.Value, GroundCombatAdmissionPolicy.ContinuationWinChanceFloor)))
            {
                // Continuity already re-tested the incumbent's capability this pass; failing the
                // ready plan here only means it cannot move right now.
                response.Kind = ActiveDefenceResponseKind.Defer;
                response.Reason = "incumbent_waits";
                return true;
            }

            // Capable but temporarily unavailable: one physical army clears the gate on its own
            // once its MP returns before the deadline. Other operations remain excluded. Buying or retreating
            // against that would be phantom.
            float freshGate = GroundCombatAdmissionPolicy.FreshStartWinChanceGate;
            ArmySnapshot capable = snap.Self.Armies.FirstOrDefault(a => GroundCombatActorEligibility.IsStructuralActor(a)
                && (committed == null || !committed.Contains(a.ArmyId))
                && !IsPinnedStrongholdDefender(snap, a, committed, ignoreEnemyArmyId: objective.Target.EnemyArmyId)
                && (withdrawing == null || !withdrawing.Contains(a.ArmyId))
                && CanInterceptPlanBeforeThreat(snap, objective.Target,
                    GroundCombatAssemblyPlanner.PlanForArmyAtThreshold(snap, opposition,
                        a.ArmyId, freshGate)));
            if (capable != null)
            {
                response.Kind = ActiveDefenceResponseKind.Defer;
                response.Reason = $"capable_actor_unavailable actor=#{capable.ArmyId}";
                return true;
            }

            return false;
        }

        private static List<ArmySnapshot> BuildDefencePool(WorldSnapshot snap,
            ISet<int> committed, ICollection<int> withdrawing)
        {
            var powerExcluded = committed == null ? new HashSet<int>() : new HashSet<int>(committed);
            // Only exact own regroup legs are removed from committed by AssessResponse.
            return GroundCombatActorEligibility.EligibleArmies(snap,
                powerExcluded, requireMovementNow: false);
        }
    }
}
