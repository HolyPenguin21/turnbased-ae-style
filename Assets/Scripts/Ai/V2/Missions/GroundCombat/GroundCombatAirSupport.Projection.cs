using System;
using System.Collections.Generic;
using System.Linq;
using Game.Aviation;
using Game.Combat;
using Game.Economy;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Units;
using UnityEngine;

namespace Game.Ai.V2
{
    internal readonly struct CombatAirSupportRequest
    {
        internal readonly MissionIntentKey Key;
        internal readonly HexCoord Target;
        internal readonly AirStrikePolicy Policy;
        internal readonly TaskScore Score;
        internal CombatAirSupportRequest(MissionIntentKey key, HexCoord target,
            AirStrikePolicy policy, TaskScore score)
        { Key = key; Target = target; Policy = policy; Score = score; }
    }

    internal readonly struct CombatAirService
    {
        internal readonly HexCoord Landing;
        internal readonly int FirstStrikeEta, StrikeTurns, RouteCost;
        internal readonly bool RosterKnown;
        internal readonly float Ap, Energy;
        internal readonly TaskScore Score;
        internal readonly AviationCombatEstimator.AirStrikeEstimate Estimate;
        internal CombatAirService(HexCoord landing, int eta, int strikes, int routeCost, bool known,
            float ap, float energy, TaskScore score, AviationCombatEstimator.AirStrikeEstimate estimate)
        { Landing = landing; FirstStrikeEta = eta; StrikeTurns = strikes; RouteCost = routeCost; RosterKnown = known;
          Ap = ap; Energy = energy; Score = score; Estimate = estimate; }
    }

    internal static partial class GroundCombatAirSupport
    {
        // Same route proof as provisioning, with optional read-only claims for a joint coverage
        // projection. Storage and live wings differ only in their initial movement/launch state.
        internal static CombatAirService? ProjectService(WorldSnapshot snap, PlayerSetupData player,
            HexMap map, IReadOnlyList<UnitData> aircraft, HexCoord start,
            CombatAirSupportRequest request, ArmyData wing = null,
            Func<HexCoord, int> additionalClaims = null)
        {
            if (snap == null || map == null || player == null || aircraft == null || aircraft.Count == 0
                || aircraft.Any(u => !AviationRules.IsAviation(u))) return null;
            if (wing != null && wing.Controller == null) return null;
            if (TargetKnownEmpty(snap, request.Target, request.Policy)) return null;
            Sortie? same = wing != null
                ? AiAirSortiePlanner.TryPlanSortie(wing, request.Target, map, player, additionalClaims)
                : AiAirSortiePlanner.TryPlanSortieFromStorage(start, aircraft, request.Target, map, player);
            MultiTurnSortie? multi = same.HasValue ? null : wing != null
                ? AiAirSortiePlanner.TryPlanMultiTurnSortie(wing, request.Target, map, player, additionalClaims)
                : AiAirSortiePlanner.TryPlanMultiTurnSortieFromStorage(start, aircraft, request.Target, map, player);
            if (!same.HasValue && !multi.HasValue) return null;
            int outbound = (same?.OutboundPath ?? multi.Value.PathToAction).Hexes.Count - 1;
            int home = (same?.ReturnPath ?? multi.Value.PathFromActionToLanding).Hexes.Count - 1;
            int movement = wing?.CurrentMovement ?? aircraft.Min(AviationRules.EffectiveMoveCurrent);
            int maxMovement = aircraft.Min(AviationRules.EffectiveMoveMax);
            if (maxMovement <= 0 || (movement <= 0 && outbound > 0)) return null;
            int eta = 1 + Mathf.CeilToInt(Mathf.Max(0, outbound - movement) / (float)maxMovement);
            int strikes = AviationRange.StrikeTurns(movement, maxMovement,
                AviationRange.SafeUnlandedEndsRemaining(aircraft), outbound, home);
            if (strikes <= 0 || aircraft.All(u => u.Attack <= 0)) return null;
            bool[] spent = aircraft.Select(u => eta == 1 && u.HasAirAttackedThisTurn).ToArray();
            if (strikes == 1 && spent.All(s => s)) return null;
            var targets = KnownAirTargets(snap, request.Target, request.Policy);
            bool known = targets.Sum(t => t.Roster.Units.Count) > 0;
            var estimate = known ? AviationCombatEstimator.EstimateAirStrikeAgainstArmies(aircraft,
                targets.Select(t => t.Roster).ToList(), request.Policy, strikes, spent) : default;
            if (known && (estimate.ExpectedDamage <= AiConfigV2.allocatorSliceEpsilon
                || estimate.ExpectedDefendersAfter.Count < request.Policy.MinimumSurvivors)) return null;
            float ap = wing?.PendingActivationApCost ?? aircraft.Sum(u => Mathf.Max(0, u.ActivationApCost));
            float energy = wing?.PendingActivationEnergyCost ?? aircraft.Sum(u => Mathf.Max(0, u.LaunchEnergyCost));
            float hp = targets.Sum(t => t.Roster.Units.Sum(u => u.HitPoints));
            float effect = known && hp > 0 ? Mathf.Clamp01(estimate.ExpectedDamage / hp) : 0.5f;
            TaskScore score = ServiceScore(request.Score, effect, ap, energy, eta);
            return new CombatAirService(same?.LandingHex ?? multi.Value.LandingHex,
                eta, strikes, outbound + home, known, ap, energy, score, estimate);
        }

        internal static TaskScore ServiceScore(TaskScore intrinsic, float effect,
            float ap, float energy, int eta) => TaskScoreEvaluator.WithResponse(intrinsic, effect,
                ap + energy * AiConfigV2.actionPriceResourceAp, 0f, eta);

        // Augmenting assignments retain task identity. A complete proposed assignment must pass
        // the joint validator before it counts as coverage; no registries or bank are mutated.
        internal static Dictionary<int, int> MatchCoverage(int requestCount, int wingCount,
            Func<int, int, bool> eligible, Func<IReadOnlyDictionary<int, int>, bool> valid)
        {
            var assigned = new Dictionary<int, int>(); // wing -> request
            bool Assign(int request, HashSet<int> visited)
            {
                for (int wing = 0; wing < wingCount; wing++)
                {
                    if (!eligible(request, wing) || !visited.Add(wing)) continue;
                    var before = new Dictionary<int, int>(assigned);
                    bool occupied = assigned.TryGetValue(wing, out int previous);
                    assigned.Remove(wing);
                    if (!occupied || Assign(previous, visited))
                    {
                        assigned[wing] = request;
                        if (valid(assigned)) return true;
                    }
                    assigned.Clear();
                    foreach (var pair in before) assigned[pair.Key] = pair.Value;
                }
                return false;
            }
            // A task rejected before another wing's valid assignment may become feasible after
            // that assignment frees its departure field. Retry only while coverage grows.
            bool progress;
            do
            {
                progress = false;
                for (int request = 0; request < requestCount; request++)
                    if (!assigned.ContainsValue(request) && Assign(request, new HashSet<int>())) progress = true;
            } while (progress && assigned.Count < Math.Min(requestCount, wingCount));
            return assigned;
        }

        internal static List<CombatAirSupportRequest> UncoveredRequests(WorldSnapshot snap,
            PlayerSetupData player, PlayerRoot root, AiTurnContext ctx,
            IReadOnlyList<CombatAirSupportRequest> requests, ActorCommitments commitments)
        {
            if (requests == null) return new List<CombatAirSupportRequest>();
            if (ctx?.Map == null || player == null || root == null) return requests.ToList();
            var wings = ArmyRegistry.AllForOwner(player)
                .Where(w => AviationRules.IsValidAirArmy(w) && w.CurrentMovement > 0
                    && AirSortieRegistry.ForArmy(player, w) == null
                    && (commitments == null || !commitments.IsArmyClaimed(w.Id)))
                .OrderBy(w => w.PendingActivationApCost + w.PendingActivationEnergyCost * AiConfigV2.actionPriceResourceAp)
                .ThenBy(w => w.Id).ToList();
            var admissible = new bool[requests.Count, wings.Count];
            for (int r = 0; r < requests.Count; r++)
                for (int w = 0; w < wings.Count; w++)
                {
                    int PotentialDepartures(HexCoord hex) => -wings.Where(other => other != wings[w]
                        && other.Hex.Equals(hex)).Sum(other => other.Members.Count);
                    // Optimistic graph only; the joint validator below must prove an executable
                    // order without borrowing the departure of a wing not proved yet.
                    admissible[r, w] = ProjectService(snap, player, ctx.Map, wings[w].Members,
                        wings[w].Hex, requests[r], wings[w], PotentialDepartures).HasValue;
                }
            var order = Enumerable.Range(0, requests.Count)
                .OrderBy(r => Enumerable.Range(0, wings.Count).Count(w => admissible[r, w]))
                .ThenByDescending(r => requests[r].Score.Value).ThenBy(r => r).ToList();
            float bankAp = StrategicSpendability.SpendableAp(player, root, ctx);
            float bankEnergy = StrategicSpendability.SpendableAmount(player, root, ctx, ResourceType.Energy);
            bool Validate(IReadOnlyDictionary<int, int> assignments)
            {
                if (assignments.Keys.Sum(w => wings[w].PendingActivationApCost) > bankAp + AiConfigV2.allocatorSliceEpsilon
                    || assignments.Keys.Sum(w => wings[w].PendingActivationEnergyCost) > bankEnergy + AiConfigV2.allocatorSliceEpsilon)
                    return false;
                var claims = new Dictionary<HexCoord, int>();
                void Add(HexCoord hex, int count) => claims[hex] =
                    (claims.TryGetValue(hex, out int prior) ? prior : 0) + count;
                var remaining = assignments.OrderBy(p => p.Key).ToList();
                while (remaining.Count > 0)
                {
                    bool advanced = false;
                    for (int i = 0; i < remaining.Count; i++)
                    {
                        var pair = remaining[i];
                        ArmyData wing = wings[pair.Key];
                        int Extra(HexCoord hex) => claims.TryGetValue(hex, out int count) ? count : 0;
                        var service = ProjectService(snap, player, ctx.Map, wing.Members, wing.Hex,
                            requests[order[pair.Value]], wing, Extra);
                        if (!service.HasValue) continue;
                        // Only this proved wing vacates its standing slot. A following wing
                        // must also respect its landing reservation; own exclusion remains once.
                        Add(wing.Hex, -wing.Members.Count);
                        Add(service.Value.Landing, wing.Members.Count);
                        remaining.RemoveAt(i);
                        advanced = true;
                        break;
                    }
                    if (!advanced) return false;
                }
                return true;
            }
            var matched = MatchCoverage(requests.Count, wings.Count,
                (r, w) => admissible[order[r], w], Validate);
            var covered = new HashSet<int>(matched.Values.Select(r => order[r]));
            return requests.Where((r, i) => !covered.Contains(i)).ToList();
        }
    }
}

