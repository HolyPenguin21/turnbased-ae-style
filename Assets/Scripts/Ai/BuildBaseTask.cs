using System.Collections.Generic;
using System.Linq;
using Game.Cards;
using Game.Combat;
using Game.Core;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using UnityEngine;

namespace Game.Ai
{
    // Агрессия · Задача 2 (Постройка дополнительной базы) — target-hex selection lives here
    // (2026-08-21, rewritten 2026-08-24). Trigger/composition gating, the AiTaskKind itself,
    // continuation/cancel while a raid army is en route, and execution (playing the Base card once
    // the army arrives) all live on AiAggressionPlanner instead (its own "Задача 2" section) —
    // same split every other task class in this codebase already follows (see RaidWeakerArmyTask's
    // own class comment for why target/composition/threat-reaction live on the task class,
    // orchestration lives on the planner).
    //
    // Цель — see this class's own FindTargetHex: sweep every legal hex on the map (HexMap.
    // AllCoords), score each one, and take the best — internal-only ranking (ScoreCandidateHex),
    // never leaked into the cross-category AiDecision.Score, same principle BuildFacilityTask.
    // RankHex/RaidWeakerArmyTask.ProximityScore already establish for their own internal hex picks.
    // 2026-08-24 rewrite (project owner's own report/spec, replacing the 2026-08-21 "aim a fixed
    // world-space distance along the bisector toward known enemies, then only look at that one
    // hex's own neighbors" version): that version's own aim point was only an approximation (exact
    // along one grid direction, off by as much as 2 hexes in others — see the project owner's own
    // log read: buildBaseForwardDistanceHexes=4 produced a real 6-hex placement), and its own
    // seven-hex search patch could entirely miss a genuinely better hex sitting just outside it.
    // Now: legality is a real HexGridMath.Distance range from whichever of the player's own bases
    // is nearest to each candidate (own IsLegalHex), the direction toward known enemies is a SOFT
    // score bonus rather than the sole search anchor (own ScoreCandidateHex), and a known-but-
    // unbuilt resource hex is worth something on its own, not just a built site's own merge bonus.
    public static class BuildBaseTask
    {
        // Best known target hex for a new base right now, or null if nothing legal is found (no
        // known enemy at all yet, or nothing on the whole map clears IsLegalHex). Distance/legality
        // are measured from whichever of this player's own Base-tagged buildings (starting citadel
        // included) is nearest to each individual candidate — see IsLegalHex's own comment — so a
        // third base naturally chains off whichever of the first two is actually closest, rather
        // than every one measuring from the original citadel forever.
        public static HexCoord? FindTargetHex(PlayerSetupData player, ArmyData buildingArmy, HexMap map)
        {
            if (player == null || map == null || !player.CitadelHexQ.HasValue || !player.CitadelHexR.HasValue)
                return null;

            var enemyHexes = new List<HexCoord>();
            foreach (PlayerSetupData other in GameSession.Players ?? Enumerable.Empty<PlayerSetupData>())
            {
                if (other == null || other == player || other.IsNeutral || !other.CitadelHexQ.HasValue || !other.CitadelHexR.HasValue)
                    continue;
                enemyHexes.Add(new HexCoord(other.CitadelHexQ.Value, other.CitadelHexR.Value));
            }
            if (enemyHexes.Count == 0)
                return null; // nothing to aim at yet

            // Per-anchor bisector cache — several candidates near the same own base share the same
            // "nearest own base" anchor, so its own forward direction only needs computing once per
            // anchor rather than once per candidate.
            var forwardByAnchor = new Dictionary<HexCoord, Vector3>();

            HexCoord? best = null;
            float bestScore = float.NegativeInfinity;
            foreach (HexCoord candidate in map.AllCoords)
            {
                if (!IsLegalHex(player, candidate, buildingArmy, map, out HexCoord nearestOwnBase, out int distance))
                    continue;

                if (!forwardByAnchor.TryGetValue(nearestOwnBase, out Vector3 forward))
                {
                    forward = ForwardDirectionFrom(nearestOwnBase, enemyHexes, map);
                    forwardByAnchor[nearestOwnBase] = forward;
                }

                float score = ScoreCandidateHex(player, candidate, map, nearestOwnBase, distance, forward);
                if (score > bestScore)
                {
                    bestScore = score;
                    best = candidate;
                }
            }
            return best;
        }

        // Bisector direction from `anchor` toward every known enemy citadel — average of the UNIT
        // vectors (project owner's own original spec: "между векторами ищем середину" — for
        // exactly two enemies this average IS that middle direction, generalizes the same way to
        // three or more). Vector3.zero (no bonus/penalty either way) if the enemies cancel out
        // (opposite directions) or none are known — ScoreCandidateHex treats that as "no directional
        // signal" rather than disqualifying anything, unlike the old aim-point version.
        private static Vector3 ForwardDirectionFrom(HexCoord anchor, List<HexCoord> enemyHexes, HexMap map)
        {
            Vector3 anchorWorld = map.HexToWorld(anchor);
            Vector3 sum = Vector3.zero;
            foreach (HexCoord enemyHex in enemyHexes)
                sum += (map.HexToWorld(enemyHex) - anchorWorld).normalized;
            return sum.sqrMagnitude > 0.0001f ? sum.normalized : Vector3.zero;
        }

        // Легальность — real HexGridMath.Distance (not a world-space approximation) from whichever
        // of this player's own already-founded Base-tagged buildings (starting citadel included) is
        // NEAREST to `candidate` must fall inside [buildBaseMinDistanceFromExistingBase,
        // buildBaseMaxDistanceFromExistingBase] — too close crowds that base, too far isn't "close
        // enough to be part of the same push" any more. Also: never with a THREATENING known
        // non-neutral army within buildBaseCancelRadius (same radius the eventual cancel condition
        // re-checks continuously once a task is under way — no point targeting somewhere that would
        // cancel the moment the task actually started), and never a hex this class's own real game
        // rule (CardHandUI.IsValidBaseDropTarget) would reject outright — bare ground, or an
        // existing hero-built resource site the new base could merge into (own copy of that exact
        // check, see CanMergeIntoResourceSite below); anything else (a citadel, another base, a full
        // resource site) is a wasted target: FindTargetHex would pick it and the eventual
        // TryBuildBase-equivalent execution step would simply fail. `nearestOwnBase`/`distance` are
        // handed back so FindTargetHex/ScoreCandidateHex never have to re-scan every owned building
        // a second time per candidate.
        private static bool IsLegalHex(PlayerSetupData player, HexCoord candidate, ArmyData buildingArmy, HexMap map,
            out HexCoord nearestOwnBase, out int distanceFromNearestOwnBase)
        {
            nearestOwnBase = default;
            distanceFromNearestOwnBase = int.MaxValue;
            foreach (BuildingData existing in BuildingRegistry.AllBuildings())
            {
                if (existing.Owner != player || !existing.IsBase)
                    continue;
                int distance = HexGridMath.Distance(candidate, existing.Hex);
                if (distance < distanceFromNearestOwnBase)
                {
                    distanceFromNearestOwnBase = distance;
                    nearestOwnBase = existing.Hex;
                }
            }
            if (distanceFromNearestOwnBase < AiConfig.buildBaseMinDistanceFromExistingBase
                || distanceFromNearestOwnBase > AiConfig.buildBaseMaxDistanceFromExistingBase)
                return false;

            BuildingData onCandidate = BuildingRegistry.FindAt(candidate);
            if (onCandidate != null && !CanMergeIntoResourceSite(onCandidate))
                return false;

            return !HasThreateningEnemyNear(player, candidate, buildingArmy, AiConfig.buildBaseCancelRadius);
        }

        // Still honest about WHERE the player has scouted — only ever looks at hexes AiMapMemory
        // already has a sighting for near `hex` (never reveals a hex the player hasn't seen an
        // enemy on before, i.e. never learns where an army went). The one narrow cheat (project
        // owner's own explicit call, 2026-08-22: "он не должен знать где вражеские армии, он
        // просто знает осталась ли она на том хексе где он её видел в прошлый раз или нет") is
        // re-verifying, for each such REMEMBERED hex specifically, whether an enemy army is still
        // physically standing there right now — fixes the earlier version's own "phantom threat"
        // bug (2026-08-21 simulation report): a sighting recorded once and never refreshed
        // (AiMapMemory only corrects a hex once it's actually re-observed) used to keep
        // blocking/cancelling long after the real army had actually moved on.
        //
        // No longer a bare presence check (2026-08-22, project owner's own follow-up call, alongside
        // buildBaseCancelRadius's own 4 → 2 shrink) — a remembered-and-still-there sighting only
        // actually disqualifies/cancels the hex if `buildingArmy`'s OWN win chance against it
        // (WorthIt.WinChance, buildingArmy as attacker) drops BELOW buildBaseMinWinChance(0.3) —
        // simplified 2026-08-22 (project owner's own follow-up call) from the earlier "sighting's
        // own win chance clears 0.75" framing to this more direct "our own win chance floor" one.
        // Aggregate Attack/Defense sums only (no hex-bonus term, no per-unit roster read) — same
        // coarse level of rigor RequiredBuildBaseStrength/CheatEstimateRaiderThreat already use for
        // a pre-combat strategic gate, not an actual fight about to happen.
        internal static bool HasThreateningEnemyNear(PlayerSetupData player, HexCoord hex, ArmyData buildingArmy, int radius)
        {
            if (buildingArmy == null)
                return false;
            float ourAttack = WorthIt.AttackSum(buildingArmy);
            float ourDefense = WorthIt.DefenseSum(buildingArmy);
            foreach (AiMapMemory.KnownEnemySighting sighting in
                     AiMapMemory.KnownEnemySightingsNear(player, new[] { hex }, radius))
            {
                if (sighting.Owner == null || sighting.Owner.IsNeutral)
                    continue;
                if (!ArmyRegistry.AllAt(sighting.Hex).Any(a => a.Owner != null && !a.Owner.IsNeutral && BattleInitiator.IsEngageable(a)))
                    continue;
                if (WorthIt.WinChance(ourAttack, ourDefense, sighting.AttackSum, sighting.DefenseSum) < AiConfig.buildBaseMinWinChance)
                    return true;
            }
            return false;
        }

        // Internal-only ranking among legal candidates — never leaked into AiDecision.Score (see
        // this class's own class comment). Four terms:
        //   "+" the hex's own terrain/Base defense bonus (same WorthIt.HexDefenseBonus every real
        //   fight already reads) — the project owner's own spec: a base is worth more on ground
        //   that also helps it survive.
        //   "+" if the hex already hosts a hero-built resource site the new base could merge into
        //   (same CanMergeIntoResourceSite rule CardHandUI.IsValidBaseDropTarget already enforces
        //   for a human's own drag-drop — a fresh Base card carries over whatever extraction
        //   Facilities are already built there instead of wasting them), ELSE "+" a smaller flat
        //   bonus if the hex is a KNOWN (AiMapMemory, honest fog-of-war) resource hex with nothing
        //   built on it yet (2026-08-24 addition, project owner's own spec point 3 — a resource hex
        //   used to be worth exactly the same as bare ground unless it already had a mergeable
        //   site).
        //   "-" a penalty scaled by how far `distanceFromNearestOwnBase` strays from
        //   buildBasePreferredDistance in EITHER direction — every candidate in [min, max] is
        //   legal, but this keeps the ranking centered on the sweet spot rather than indifferent
        //   across the whole legal range.
        //   "+/-" a soft dot-product alignment with `forwardDirection` (2026-08-24 rewrite,
        //   project owner's own spec point 2) — nudges toward the known-enemy side without ever
        //   disqualifying a strong lateral hex the way the old aim-point search structurally did.
        private static float ScoreCandidateHex(PlayerSetupData player, HexCoord candidate, HexMap map,
            HexCoord nearestOwnBase, int distanceFromNearestOwnBase, Vector3 forwardDirection)
        {
            float score = WorthIt.HexDefenseBonus(candidate, map) * AiConfig.buildBaseDefenseBonusWeight;

            BuildingData existing = BuildingRegistry.FindAt(candidate);
            if (existing != null && CanMergeIntoResourceSite(existing))
                score += AiConfig.buildBaseResourceSiteMergeBonus;
            else if (existing == null && AiMapMemory.IsResourceHexKnown(player, candidate))
                score += AiConfig.buildBaseResourceTypeWeight;

            score -= Mathf.Abs(distanceFromNearestOwnBase - AiConfig.buildBasePreferredDistance) * AiConfig.buildBaseDistanceWeight;

            if (forwardDirection.sqrMagnitude > 0.0001f)
            {
                Vector3 toCandidate = map.HexToWorld(candidate) - map.HexToWorld(nearestOwnBase);
                if (toCandidate.sqrMagnitude > 0.0001f)
                    score += Vector3.Dot(toCandidate.normalized, forwardDirection) * AiConfig.buildBaseForwardAlignmentWeight;
            }

            return score;
        }

        // Same rule CardHandUI.CanMergeIntoResourceSite already enforces for a human's own drag-drop
        // (own copy — that one's private to CardHandUI and this class has no UI dependency to share
        // it through): a hero-built resource site (HasTieredUnlock == false, the one kind of
        // building that's ever false there) whose occupied Facility slots already fit inside a
        // fresh Base's own default capacity. Internal, not private — AiAggressionPlanner's own
        // TryContinueBuildBaseTask reuses this exact rule to re-validate the target hex is still
        // buildable right before actually proposing the execution step.
        internal static bool CanMergeIntoResourceSite(BuildingData existing)
        {
            if (existing.HasTieredUnlock)
                return false;
            int occupied = existing.FacilitySlots.Count(f => f != null);
            return occupied <= BuildingData.DefaultTotalFacilitySlots;
        }
    }
}
