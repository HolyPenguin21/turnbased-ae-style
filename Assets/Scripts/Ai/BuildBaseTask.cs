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
    // (2026-08-21). Trigger/composition gating, the AiTaskKind itself, continuation/cancel while a
    // raid army is en route, and execution (playing the Base card once the army arrives) all live
    // on AiAggressionPlanner instead (its own "Задача 2" section) — same split every other task
    // class in this codebase already follows (see RaidWeakerArmyTask's own class comment for why
    // target/composition/threat-reaction live on the task class, orchestration lives on the
    // planner).
    //
    // Цель — see this class's own FindTargetHex: a hex roughly on the bisector between the
    // player's own citadel and every known enemy citadel (the project owner's own spec, 2026-08-21
    // — "цитадель врага уже известна, направление читерим, содержимое хекса нет"), refined by
    // picking whichever of that aim point's own neighbors (or the aim point itself) scores best —
    // internal-only ranking (ScoreCandidateHex), never leaked into the cross-category AiDecision.
    // Score, same principle BuildFacilityTask.RankHex/RaidWeakerArmyTask.ProximityScore already
    // establish for their own internal hex picks.
    public static class BuildBaseTask
    {
        // Best known target hex for a new base right now, or null if nothing legal is found (no
        // known enemy at all yet, or every candidate near the aim point is disqualified). Origin is
        // always the player's own STARTING citadel (player.CitadelHexQ/R), not AiTurnController.
        // GarrisonHexFor's multi-base-aware cousin — the citadel is the one stable "home" reference
        // guaranteed to exist even before any base is ever founded, and this is a strategic,
        // whole-map direction pick, not a per-army travel decision (contrast RaidWeakerArmyTask's
        // own ProximityScore, which deliberately DOES read from the acting army's own hex).
        public static HexCoord? FindTargetHex(PlayerSetupData player, ArmyData buildingArmy, HexMap map)
        {
            if (player == null || map == null || !player.CitadelHexQ.HasValue || !player.CitadelHexR.HasValue)
                return null;

            var ownHex = new HexCoord(player.CitadelHexQ.Value, player.CitadelHexR.Value);
            Vector3 ownWorld = map.HexToWorld(ownHex);

            var enemyWorlds = new List<Vector3>();
            foreach (PlayerSetupData other in GameSession.Players ?? Enumerable.Empty<PlayerSetupData>())
            {
                if (other == null || other == player || other.IsNeutral || !other.CitadelHexQ.HasValue || !other.CitadelHexR.HasValue)
                    continue;
                enemyWorlds.Add(map.HexToWorld(new HexCoord(other.CitadelHexQ.Value, other.CitadelHexR.Value)));
            }
            if (enemyWorlds.Count == 0)
                return null; // nothing to aim at yet

            // Bisector direction — average of the UNIT vectors toward each known enemy citadel
            // (project owner's own spec: "берём направление к первому противнику, берём
            // направление ко второму, между двумя векторами ищем середину" — for exactly two
            // enemies this average IS that middle direction; generalizes the same way to three or
            // more). Unit vectors, not raw displacement — with only ONE known enemy, a raw average
            // would just BE that enemy's own citadel hex.
            Vector3 bisector = Vector3.zero;
            foreach (Vector3 enemyWorld in enemyWorlds)
                bisector += (enemyWorld - ownWorld).normalized;
            if (bisector.sqrMagnitude < 0.0001f)
                return null; // known enemies cancel out (opposite directions) — no single sensible forward direction this step
            bisector.Normalize();

            // Fixed forward distance from the OWN citadel (project owner's own correction,
            // 2026-08-21 — "не по середине между цитаделями, а ближе к своей цитадели 5-6
            // хексов"), not a fraction of the distance to the enemies. World-units-per-hex-step is
            // only approximate (sqrt(3)*outerRadius — exact for a straight run of neighbor hops,
            // see HexGridMath.AxialToWorld's own edge-vector math; a "good enough aim point, refine
            // via neighbors" figure, same as every other rough distance already in this method).
            float worldPerHex = Mathf.Sqrt(3f) * map.OuterRadius;
            Vector3 aimWorld = ownWorld + bisector * (AiConfig.buildBaseForwardDistanceHexes * worldPerHex);
            HexCoord aimHex = map.WorldToHex(aimWorld);

            // "Дальше по скору смотрим на соседей" — the aim hex itself is only a rough projection
            // from continuous world space; the actual placement is whichever of ITS OWN neighbors
            // (or itself) scores best among the legal ones.
            HexCoord? best = null;
            float bestScore = float.NegativeInfinity;
            foreach (HexCoord candidate in HexGridMath.Neighbors(aimHex).Append(aimHex))
            {
                if (!IsLegalHex(player, candidate, buildingArmy, map))
                    continue;
                float score = ScoreCandidateHex(candidate, map);
                if (score > bestScore)
                {
                    bestScore = score;
                    best = candidate;
                }
            }
            return best;
        }

        // Легальность — never within buildBaseMinDistanceFromExistingBase of one of this player's
        // own already-founded bases (a player can found several; this just keeps them spread out),
        // never with a THREATENING known non-neutral army within buildBaseCancelRadius (same radius
        // the eventual cancel condition re-checks continuously once a task is under way — no point
        // aiming somewhere that would cancel the moment the task actually started), and never a hex
        // this class's own real game rule (CardHandUI.IsValidBaseDropTarget) would reject outright
        // — bare ground, or an existing hero-built resource site the new base could merge into
        // (own copy of that exact check, see CanMergeIntoResourceSite below); anything else
        // (a citadel, another base, a full resource site) is a wasted target: FindTargetHex would
        // pick it and the eventual TryBuildBase-equivalent execution step would simply fail.
        private static bool IsLegalHex(PlayerSetupData player, HexCoord candidate, ArmyData buildingArmy, HexMap map)
        {
            foreach (BuildingData existing in BuildingRegistry.AllBuildings())
            {
                if (existing.Owner != player || !existing.HasAbility(UnitAbilities.Base))
                    continue;
                if (HexGridMath.Distance(candidate, existing.Hex) <= AiConfig.buildBaseMinDistanceFromExistingBase)
                    return false;
            }

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
        // this class's own class comment). "+" for the hex's own terrain/Base defense bonus (same
        // WorthIt.HexDefenseBonus every real fight already reads) — the project owner's own spec:
        // a base is worth more on ground that also helps it survive. "+" again if the hex already
        // hosts a hero-built resource site the new base could merge into (same CanMergeIntoResourceSite
        // rule CardHandUI.IsValidBaseDropTarget already enforces for a human's own drag-drop — a
        // fresh Base card carries over whatever extraction Facilities are already built there
        // instead of wasting them) — this is the "дорогой хекс с ресурсами" case from the project
        // owner's own spec; CardDefinition.resourceYield itself is explicitly NOT wired to
        // gameplay yet (see its own comment), so this merge-preservation is the only real payoff
        // today, not a flat yield bonus.
        private static float ScoreCandidateHex(HexCoord candidate, HexMap map)
        {
            float score = WorthIt.HexDefenseBonus(candidate, map) * AiConfig.buildBaseDefenseBonusWeight;
            BuildingData existing = BuildingRegistry.FindAt(candidate);
            if (existing != null && CanMergeIntoResourceSite(existing))
                score += AiConfig.buildBaseResourceSiteMergeBonus;
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
