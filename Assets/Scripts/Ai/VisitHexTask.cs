using Game.Combat;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Terrain;
using UnityEngine;

namespace Game.Ai
{
    // Разведка · Задача 1 (AI architecture doc, section 02 · 2.1) — composition eligibility,
    // target scoring, AND the threat reaction all live here now instead of AiScoutPlanner, so a
    // change to Задача 1's own rules never needs touching the orchestrator, just this class. Now
    // the only Разведка task — see AiScoutPlanner's own class comment for why ScoutResourceHexTask
    // (Задача 2) was removed. Агрессия (RaidWeakerArmyTask) does NOT have a TryFlee of this shape
    // — its own threat reaction was redesigned into a one-way fight-or-retreat-to-regroup call,
    // see that class's own "Поведение" comment. What stays in AiScoutPlanner is only Recce-army
    // assembly (TryStartReconAssemblyCandidates).
    //
    // Цель — посетить (наступить) как можно больше непосещённых хексов, начиная от цитадели:
    // расходится волной, не бросается на самый дальний хекс.
    //
    // Композиция — ровно одна: 1 герой (Recce) · 1 юнит (Recce) (IsEligibleComposition delegates
    // to AiArmyRoles.IsScoutCapable — hero-vs-unit doesn't matter, only "exactly one Recce
    // member" does). The lone-hero-without-Recce composition that used to also run Задача 1 is
    // gone from here — see AiArmyRoles.IsSoloHeroAwaitingEscort's own comment (that role now just
    // walks home to wait for an escort); the hero+2-3-units composition moved to its own Агрессия
    // category (RaidWeakerArmyTask), which itself later stopped using a fixed composition at all.
    //
    // Поведение — никогда не атакует: любой видимый хекс с известной армией (враг ИЛИ нейтрал) is
    // excluded from candidates outright, not merely penalized (see FindTarget). Известная (по
    // памяти, не обязательно видимая сейчас) вражеская армия рядом с текущим хексом — отступление
    // в гарнизон на один ход, не более двух ходов подряд (see TryFlee). Задача не завершается на
    // одном хексе — каждая continuation-проверка переоценивается заново и предлагает следующий
    // непосещённый хекс, пока рядом есть что посетить; "нечего посетить рядом" (FindTarget вернул
    // null) — единственное условие завершения, снимает задачу AiTurnController.TryContinueVisitTask.
    public static class VisitHexTask
    {
        public static bool IsEligibleComposition(ArmyData army) => AiArmyRoles.IsScoutCapable(army);

        // Известная НЕ-нейтральная армия (AiMapMemory — честная память, не живое зрение) в
        // радиусе scoutFleeRadius от текущего хекса `army` → отступление в гарнизон на один ход
        // вместо обычной цели FindTarget. Нет отдельной проверки "своя армия достаточно сильна,
        // можно не бежать": единственная композиция, годная для Задачи 1 (IsScoutCapable — ровно
        // один Recce-член), по построению никогда не пересекается с
        // AiArmyRoles.IsMakeshiftScoutCapable (та требует ОТСУТСТВИЯ Recce), так что эта
        // композиция никогда не "достаточно сильна" в боевом смысле — она просто всегда бежит.
        // `task` == null для ещё не начатой задачи — всегда свободна бежать. FledLastTurn не даёт
        // бежать два хода подряд (см. AiTask.FledLastTurn) — "бежал → возобновил → бежал" вместо
        // бесконечного отступления, пока угроза не исчезнет из памяти.
        public static AiScoutPlanner.ScoutTarget? TryFlee(PlayerSetupData player, ArmyData army, AiTask task)
        {
            if (army == null)
                return null;

            if (task != null && task.FledLastTurn)
            {
                task.FledLastTurn = false;
                return null;
            }

            bool anyThreat = false;
            foreach (AiMapMemory.KnownEnemySighting sighting in
                     AiMapMemory.KnownEnemySightingsNear(player, new[] { army.Hex }, AiConfig.Current.scoutFleeRadius))
            {
                if (sighting.Owner != null && sighting.Owner.IsNeutral)
                    continue; // neutrals never trigger flight — see this class's own comment

                anyThreat = true;
                break;
            }
            if (!anyThreat)
            {
                if (task != null)
                    task.FledLastTurn = false;
                return null;
            }

            if (task != null)
                task.FledLastTurn = true;
            HexCoord garrisonHex = AiTurnController.GarrisonHexFor(player);
            return new AiScoutPlanner.ScoutTarget(garrisonHex, AiConfig.Current.scoutFleeBonus,
                "рядом известная вражеская армия — уходит в гарнизон на один ход");
        }

        // Best unvisited, not-known-occupied hex on the map. Null if nothing qualifies. The scan
        // itself stays coarse — proximity/coverage only, no real pathfinding — but the winning
        // pick IS checked for actual first-step affordability below, with a cheap neighbor-only
        // fallback if it fails (see the affordability comment further down for why).
        //
        // Условия "+" к скору — ближе к текущей позиции армии (scoutProximityWeight) и чем больше
        // непосещённых соседей открывает (freshNeighborWeight).
        // Условия "-" к скору — чем дальше от цитадели (citadelDistancePenalty, чтобы охват рос
        // кольцом вокруг дома, а не убегал в произвольную сторону). Хекс с известной вражеской/
        // нейтральной армией не штрафуется — он исключается из кандидатов целиком (см. класса
        // "Поведение" выше).
        public static AiScoutPlanner.ScoutTarget? FindTarget(PlayerSetupData actor, ArmyData army, HexMap map)
        {
            if (actor == null || army == null || map == null)
                return null;

            HexCoord? citadelHex = actor.CitadelHexQ.HasValue && actor.CitadelHexR.HasValue
                ? new HexCoord(actor.CitadelHexQ.Value, actor.CitadelHexR.Value)
                : (HexCoord?)null;

            int? wavefrontDistance = citadelHex.HasValue
                ? NearestUnvisitedDistance(actor, map, citadelHex.Value)
                : null;

            AiScoutPlanner.ScoutTarget? best = null;
            foreach (HexCoord candidate in map.AllCoords)
            {
                AiScoutPlanner.ScoutTarget? scored = ScoreCandidate(actor, army, map, candidate, citadelHex, wavefrontDistance);
                if (!scored.HasValue)
                    continue;
                if (best != null && !(scored.Value.Score > best.Value.Score))
                    continue;
                best = scored;
            }
            if (best == null)
                return null;

            // The scan above never checks real move cost, so a nearby-yet-expensive pick (rough
            // terrain right next door) can outright reject the whole move order — see
            // HexSelectionController.Movement.IssueMoveOrder's own first-step check — even while
            // the army still has enough points left to reach a cheaper neighbor instead, wasting
            // its remaining movement for the rest of the turn (AiTurnController marks a rejected
            // mover "stuck" and won't retry it until next turn). Re-checked here, once, for just
            // the winning pick — not during the scan above — so the common (affordable) case pays
            // no extra pathfinding cost.
            if (IsFirstStepAffordable(map, army, best.Value.Hex))
                return best;

            AiScoutPlanner.ScoutTarget? affordableNeighbor = null;
            foreach (HexCoord neighbor in HexGridMath.Neighbors(army.Hex))
            {
                if (!map.TryGetTerrainAt(neighbor, out TerrainTypeEntry entry)
                    || army.CurrentMovement < Mathf.Max(1, entry.moveCost))
                {
                    continue;
                }
                AiScoutPlanner.ScoutTarget? scored = ScoreCandidate(actor, army, map, neighbor, citadelHex, wavefrontDistance);
                if (!scored.HasValue)
                    continue;
                if (affordableNeighbor != null && !(scored.Value.Score > affordableNeighbor.Value.Score))
                    continue;
                affordableNeighbor = scored;
            }
            // Nothing affordable even among direct neighbors — the army genuinely can't use its
            // remaining movement this turn, so fall back to the original (unaffordable) pick;
            // AiTurnController's own "stuck" handling takes it from there, same as before this
            // affordability check existed.
            return affordableNeighbor ?? best;
        }

        // Shared scoring/filtering for a single candidate hex — used both for the full-map scan
        // above and the neighbor-only affordability fallback, so the two never score a hex
        // differently.
        private static AiScoutPlanner.ScoutTarget? ScoreCandidate(PlayerSetupData actor, ArmyData army, HexMap map,
            HexCoord candidate, HexCoord? citadelHex, int? wavefrontDistance)
        {
            if (candidate.Equals(army.Hex))
                return null;
            if (VisionSystem.IsVisited(actor, candidate))
                return null;

            if (citadelHex.HasValue && wavefrontDistance.HasValue
                && HexGridMath.Distance(citadelHex.Value, candidate) > wavefrontDistance.Value + AiConfig.Current.visitRingBand)
            {
                return null;
            }

            bool visible = VisionSystem.IsVisible(actor, candidate);

            // A visible hex known to hold an enemy/neutral army is never a destination — this
            // composition never fights (see this class's own "Поведение" comment).
            if (visible && BattleInitiator.FindEnemyAt(candidate, actor) != null)
                return null;

            int distanceFromScout = HexGridMath.Distance(army.Hex, candidate);
            float score = -distanceFromScout * AiConfig.Current.scoutProximityWeight;

            int freshNeighbors = 0;
            foreach (HexCoord neighbor in HexGridMath.Neighbors(candidate))
                if (map.TryGetTerrainAt(neighbor, out _) && !VisionSystem.IsVisited(actor, neighbor))
                    freshNeighbors++;
            score += freshNeighbors * AiConfig.Current.freshNeighborWeight;

            if (citadelHex.HasValue)
                score -= HexGridMath.Distance(citadelHex.Value, candidate) * AiConfig.Current.citadelDistancePenalty;

            return new AiScoutPlanner.ScoutTarget(candidate, score,
                $"{distanceFromScout} хексов, открывает {freshNeighbors} соседних непосещённых");
        }

        // Mirrors HexSelectionController.Movement.IssueMoveOrder's own first-step check (same
        // "army only ever stops short of a hex it can't fully afford" rule) — a hex more than one
        // step away can still be a valid target even if the FULL path is unaffordable this turn
        // (the army just moves as far as it can and re-targets next call); only an unaffordable
        // FIRST step rejects the whole order outright, which is what this guards against.
        private static bool IsFirstStepAffordable(HexMap map, ArmyData army, HexCoord destination)
        {
            if (destination.Equals(army.Hex))
                return true;
            HexPath path = HexPathfinder.FindPath(map, army.Hex, destination);
            if (path == null || path.Hexes.Count < 2)
                return false;
            map.TryGetTerrainAt(path.Hexes[1], out TerrainTypeEntry firstStepEntry);
            int firstStepCost = firstStepEntry != null ? Mathf.Max(1, firstStepEntry.moveCost) : 1;
            return army.CurrentMovement >= firstStepCost;
        }

        // Shortest citadel distance among every still-unvisited on-map hex — the current
        // wavefront's own leading edge (see visitRingBand's own comment). Null once the whole map
        // is visited, same "nothing left to scout" outcome the caller already handles.
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
