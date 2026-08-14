using System;
using System.Linq;
using Game.Combat;
using Game.HexGrid;
using Game.Map;
using Game.Players;

namespace Game.Ai
{
    // Разведка's own two target-pickers (AI architecture doc, section 02 · 2.1) — pure
    // read-only scoring, same stateless-static style as AiGoalScorer/BattleAi, never mutates
    // anything. Neither checks actual move-point reachability (same coarse-radius approach
    // AiGoalScorer's own ScanRadius already takes for its goals) — an unreachable pick just
    // fails to move (see AiTurnController), and a different one gets tried next turn once more
    // of the map nearby has opened up.
    public static class AiScoutPlanner
    {
        public readonly struct ScoutTarget
        {
            public readonly HexCoord Hex;
            public readonly float Score;
            public readonly string Reason;

            public ScoutTarget(HexCoord hex, float score, string reason)
            {
                Hex = hex;
                Score = score;
                Reason = reason;
            }
        }

        // Every tunable number this class used to hold as private consts (ring band, proximity/
        // fresh-neighbor/citadel-distance weights, attack-opportunity bonus, solo-hero home
        // radius, resource-scout max-distance fraction) now lives on AiConfig, read via
        // AiConfig.Current at each use site below — see that class for what each field means and
        // why (comments preserved there).

        // Best unvisited, not-known-occupied-by-something-too-strong hex on the map for
        // Разведка · Задача 1 — "посетить как можно больше непосещенных хексов начиная от
        // цитадели" per the project owner's own spec. Null if nothing qualifies. Weighs
        // proximity to `army`'s current position, how much further unvisited ground the pick
        // borders, and how far the pick is from the actor's own citadel (so the mapped area
        // grows as a ring around home), same shape the old single-purpose planner already used.
        //
        // mayAttack: AiArmyRoles.IsMakeshiftScoutCapable armies (hero + 2-3 units, no Recce) are
        // allowed to treat a KNOWN-weaker enemy-held hex as a valid — even preferred — target
        // (see AttackOpportunityBonus); every other eligible composition (solo hero/unit, with
        // or without Recce) must keep avoiding contact entirely, same as before.
        //
        // requireVisible/homeRadius: AiArmyRoles.IsSoloHeroAwaitingEscort's own fragile lone-hero
        // carve-out — restricts candidates to already-visible ground within a fixed radius of the
        // citadel instead of the normal wavefront band, and refuses to propose ANY target at all
        // once AiMapMemory's own honest sightings put an enemy/neutral army within that radius —
        // this hero has no escort to fight its way home through one.
        public static ScoutTarget? FindVisitTargetHex(PlayerSetupData actor, ArmyData army, HexMap map,
            bool mayAttack, bool requireVisible = false, int? homeRadius = null)
        {
            if (actor == null || army == null || map == null)
                return null;

            HexCoord? citadelHex = actor.CitadelHexQ.HasValue && actor.CitadelHexR.HasValue
                ? new HexCoord(actor.CitadelHexQ.Value, actor.CitadelHexR.Value)
                : (HexCoord?)null;

            if (homeRadius.HasValue)
            {
                if (!citadelHex.HasValue || AiMapMemory.HasKnownEnemyWithin(actor, citadelHex.Value, homeRadius.Value))
                    return null;
            }

            int? wavefrontDistance = !homeRadius.HasValue && citadelHex.HasValue
                ? NearestUnvisitedDistance(actor, map, citadelHex.Value)
                : null;

            ScoutTarget? best = null;
            foreach (HexCoord candidate in map.AllCoords)
            {
                if (candidate.Equals(army.Hex))
                    continue;
                if (VisionSystem.IsVisited(actor, candidate))
                    continue;

                if (homeRadius.HasValue)
                {
                    if (HexGridMath.Distance(citadelHex.Value, candidate) > homeRadius.Value)
                        continue;
                }
                else if (citadelHex.HasValue && wavefrontDistance.HasValue
                    && HexGridMath.Distance(citadelHex.Value, candidate) > wavefrontDistance.Value + AiConfig.Current.visitRingBand)
                {
                    continue;
                }

                bool visible = VisionSystem.IsVisible(actor, candidate);
                if (requireVisible && !visible)
                    continue;

                // Only a CURRENTLY visible hex is something we can honestly judge as attackable —
                // a fog-hidden enemy is discovered mid-move (HandleVisionStep), never targeted.
                ArmyData enemy = visible ? BattleInitiator.FindEnemyAt(candidate, actor) : null;
                bool isAttack = false;
                if (enemy != null)
                {
                    if (!mayAttack || !IsEnemyWeaker(army, enemy))
                        continue; // can't fight, or known too strong — avoid like any other scout
                    isAttack = true;
                }

                int distanceFromScout = HexGridMath.Distance(army.Hex, candidate);
                float score = -distanceFromScout * AiConfig.Current.scoutProximityWeight;

                int freshNeighbors = 0;
                foreach (HexCoord neighbor in HexGridMath.Neighbors(candidate))
                    if (map.TryGetTerrainAt(neighbor, out _) && !VisionSystem.IsVisited(actor, neighbor))
                        freshNeighbors++;
                score += freshNeighbors * AiConfig.Current.freshNeighborWeight;

                if (isAttack)
                    score += AiConfig.Current.attackOpportunityBonus;

                if (citadelHex.HasValue)
                    score -= HexGridMath.Distance(citadelHex.Value, candidate) * AiConfig.Current.citadelDistancePenalty;

                if (best != null && !(score > best.Value.Score))
                    continue;

                string reason = isAttack
                    ? $"известная армия послабее в {distanceFromScout} хексах — атакует по пути"
                    : $"{distanceFromScout} хексов, открывает {freshNeighbors} соседних непосещённых";
                best = new ScoutTarget(candidate, score, reason);
            }
            return best;
        }

        // Разведка · Задача 2 — "разведывать (можно не наступать) территорию и найти (увидеть)
        // хекс с определенным типом ресурсов" per the project owner's own spec: long trips aimed
        // at opening fog broadly (see the class's own RingBand comment for why this deliberately
        // skips the citadel-anchored wave Задача 1 uses — a plain ring, ResourceScoutMaxDistanceFraction's
        // own flat outer cap is a different, much looser leash, not the same mechanism), never
        // fighting — Recce-only compositions (AiArmyRoles.IsScoutCapable), so any visible enemy
        // hex is always avoided, no mayAttack branch at all. Completion (a hex of `wantedType`
        // actually known now) is the caller's own check, not this method's — AiMapMemory.
        // KnownResourceHexesOfType. Never reads HexResourceBonusRegistry — the project owner's own
        // stance: the AI has no business knowing a hex carries a resource, of ANY type, before it
        // has actually SEEN that hex, so this scores purely on ordinary exploration value (same
        // proximity/fresh-neighbor terms FindVisitTargetHex itself uses), no thumb on the scale for
        // hexes that just happen to secretly hold a bonus.
        public static ScoutTarget? FindResourceScoutTargetHex(PlayerSetupData actor, ArmyData army, HexMap map)
        {
            if (actor == null || army == null || map == null)
                return null;

            HexCoord? citadelHex = actor.CitadelHexQ.HasValue && actor.CitadelHexR.HasValue
                ? new HexCoord(actor.CitadelHexQ.Value, actor.CitadelHexR.Value)
                : (HexCoord?)null;
            int maxDistance = (int)(Math.Max(map.Width, map.Height) * AiConfig.Current.resourceScoutMaxDistanceFraction);

            ScoutTarget? best = null;
            foreach (HexCoord candidate in map.AllCoords)
            {
                if (candidate.Equals(army.Hex))
                    continue;
                if (VisionSystem.IsVisited(actor, candidate))
                    continue;
                if (citadelHex.HasValue && HexGridMath.Distance(citadelHex.Value, candidate) > maxDistance)
                    continue;

                bool visible = VisionSystem.IsVisible(actor, candidate);

                // Never a destination if it's currently known to hold an enemy/neutral army —
                // this composition can never fight (see the method's own comment).
                if (visible && BattleInitiator.FindEnemyAt(candidate, actor) != null)
                    continue;

                int distanceFromScout = HexGridMath.Distance(army.Hex, candidate);
                float score = -distanceFromScout * AiConfig.Current.scoutProximityWeight;

                int freshNeighbors = 0;
                foreach (HexCoord neighbor in HexGridMath.Neighbors(candidate))
                    if (map.TryGetTerrainAt(neighbor, out _) && !VisionSystem.IsVisited(actor, neighbor))
                        freshNeighbors++;
                score += freshNeighbors * AiConfig.Current.freshNeighborWeight;

                if (best != null && !(score > best.Value.Score))
                    continue;

                best = new ScoutTarget(candidate, score,
                    $"{distanceFromScout} хексов, открывает {freshNeighbors} соседних непосещённых");
            }
            return best;
        }

        // A flat attack/defense comparison, placeholder maturity — replace once real Combat
        // Worth-It scoring (AI architecture doc section 07) exists. Only ever called against a
        // CURRENTLY VISIBLE enemy army (see FindVisitTargetHex), so this reads live
        // Attack/Defense, not memory.
        private static bool IsEnemyWeaker(ArmyData mover, ArmyData enemy)
        {
            float ownAttack = mover.Members.Where(m => !m.IsHero).Sum(m => m.Attack);
            float enemyDefense = enemy.Members.Where(m => !m.IsHero).Sum(m => m.Defense);
            return ownAttack > enemyDefense;
        }

        // Shortest citadel distance among every still-unvisited on-map hex — the current
        // wavefront's own leading edge (see RingBand's own comment). Null once the whole map is
        // visited, same "nothing left to scout" outcome the caller already handles.
        private static int? NearestUnvisitedDistance(PlayerSetupData actor, HexMap map, HexCoord citadelHex)
        {
            int? nearest = null;
            foreach (HexCoord hex in map.AllCoords)
            {
                if (VisionSystem.IsVisited(actor, hex))
                    continue;
                int distance = HexGridMath.Distance(citadelHex, hex);
                if (!nearest.HasValue || distance < nearest.Value)
                    nearest = distance;
            }
            return nearest;
        }
    }
}
