using System.Collections.Generic;
using System.Linq;
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
    // to AiArmyRoles.IsSoloRecce — hero-vs-unit doesn't matter, only "the army is a single Recce
    // member, alone" does; a Recce member riding along inside a bigger combat army does NOT
    // qualify — that army is just too AP-expensive to spend on scouting, see IsSoloRecce's own
    // comment). The lone-hero-without-Recce composition that used to also run Задача 1 is
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
        public static bool IsEligibleComposition(ArmyData army) => AiArmyRoles.IsSoloRecce(army);

        // Известная НЕ-нейтральная армия (AiMapMemory — честная память, не живое зрение) в
        // радиусе scoutFleeRadius от текущего хекса `army` → отступление в гарнизон на один ход
        // вместо обычной цели FindTarget. Нет отдельной проверки "своя армия достаточно сильна,
        // можно не бежать": единственная композиция, годная для Задачи 1 (IsSoloRecce — ровно
        // один член, и он Recce), по построению никогда не пересекается с
        // AiArmyRoles.IsMakeshiftScoutCapable (та требует ОТСУТСТВИЯ Recce), так что эта
        // композиция никогда не "достаточно сильна" в боевом смысле — она просто всегда бежит.
        // `task` == null для ещё не начатой задачи — всегда свободна бежать. `currentTurn` gates
        // AiTask.FledOnTurn (see its own comment) — while this task already fled THIS turn, every
        // further call keeps heading for the garrison (covers both more flee steps still needed
        // AND "already home, hold until next turn") instead of falling through to routine
        // FindTarget, whether or not the original threat is still in range THIS call.
        public static AiScoutPlanner.ScoutTarget? TryFlee(PlayerSetupData player, ArmyData army, AiTask task, int currentTurn)
        {
            if (army == null)
                return null;

            if (task != null && task.FledOnTurn == currentTurn)
            {
                // Keep heading for whatever base this flee already committed to earlier this same
                // turn — task.TargetHex was stamped with that exact hex the moment this flee
                // started (both TryContinueVisitTask and TryStartVisitCandidates set
                // task.TargetHex = target.Value.Hex right after calling TryFlee, so it's already
                // sitting there by the time a later call this turn reaches this branch). NOT
                // recomputed fresh here (2026-08-24 fix, multi-base awareness, project owner's own
                // report) — re-deriving the nearest base from army.Hex on every call could ping-
                // pong a scout between two now-equidistant bases mid-route as its own position
                // moves toward one of them.
                return new AiScoutPlanner.ScoutTarget(task.TargetHex, AiConfig.scoutFleeBonus,
                    "already retreating this turn — continues to the garrison");
            }

            // Multi-base-aware since 2026-08-24 (project owner's own report) — the nearest of this
            // player's own garrisoned hexes to the scout's CURRENT position, not always the
            // starting citadel (see AiTurnController.NearestOwnGarrisonHex's own comment); a scout
            // fleeing near a later-founded base has no reason to trek all the way back to the
            // citadel instead.
            HexCoord homeHex = AiTurnController.NearestOwnGarrisonHex(player, army.Hex);

            HexCoord? threatHex = null;
            foreach (AiMapMemory.KnownEnemySighting sighting in
                     AiMapMemory.KnownEnemySightingsNear(player, new[] { army.Hex }, AiConfig.scoutFleeRadius))
            {
                if (sighting.Owner != null && sighting.Owner.IsNeutral)
                    continue; // neutrals never trigger flight — see this class's own comment

                // Стелс · Задача 1 — a scout still FULLY HIDDEN from this sighting's owner (and
                // not personally detected by them) is under no threat from that army, so it keeps
                // scouting past it. StealthSystem.ArmyFullyHiddenFrom already folds in
                // IsDetectedBy via IsHiddenFrom, so this one call covers both "in stealth" and
                // "not spotted by this player". Keep scanning the rest: one nearby army whose
                // owner CAN see the scout — an ordinary visible scout, or one already detected —
                // is still enough to make it retreat.
                if (sighting.Owner != null && Game.Map.StealthSystem.ArmyFullyHiddenFrom(army, sighting.Owner))
                {
                    AiDebugLog.Write($"[AI] {player.Nickname}: scout \"{army.Name}\" ignores nearby enemy at "
                        + $"({sighting.Hex.Q}, {sighting.Hex.R}) — remains hidden from {sighting.Owner.Nickname}.");
                    continue;
                }

                threatHex = sighting.Hex;
                AiDebugLog.Write($"[AI] {player.Nickname}: scout \"{army.Name}\" retreats — detected/visible to "
                    + $"{(sighting.Owner != null ? sighting.Owner.Nickname : "an enemy")} at ({sighting.Hex.Q}, {sighting.Hex.R}).");
                break;
            }
            if (!threatHex.HasValue)
                return null;

            if (task != null)
                task.FledOnTurn = currentTurn;
            // Outlives the triggering sighting itself (see AiMapMemory.ScoutDangerZones' own
            // comment) — without this, the sighting that JUST caused this retreat goes stale in
            // enemySightingMemoryTurns(2) turns and this exact spot opens back up to FindTarget
            // again while the enemy army is most likely still sitting right there.
            AiMapMemory.MarkScoutDanger(player, threatHex.Value, AiConfig.scoutDangerRadius,
                currentTurn + AiConfig.scoutDangerCooldownTurns);
            return new AiScoutPlanner.ScoutTarget(homeHex, AiConfig.scoutFleeBonus,
                "a known enemy army is nearby — retreats to the nearest base for one turn");
        }

        // Best unvisited, not-known-occupied hex on the map. Null if nothing qualifies. The scan
        // itself stays coarse — proximity/coverage only, no real pathfinding — but the winning
        // pick IS checked for actual first-step affordability below, with a cheap neighbor-only
        // fallback if it fails (see the affordability comment further down for why).
        //
        // Условия "+" к скору — ближе к текущей позиции армии (scoutProximityWeight) и чем больше
        // непосещённых соседей открывает (freshNeighborWeight).
        // Условия "-" к скору — чем дальше от цитадели (visitTargetCitadelWeight, чтобы охват рос
        // кольцом вокруг дома, а не убегал в произвольную сторону). Хекс с известной вражеской/
        // нейтральной армией не штрафуется — он исключается из кандидатов целиком (см. класса
        // "Поведение" выше). Этот скор — чисто внутренний выбор ЦЕЛИ (какой хекс предпочесть среди
        // кандидатов), в кросс-категорийный AiDecision.Score не попадает — см. AiScoutPlanner's own
        // TryContinueVisitTask/TryStartVisitCandidates.
        public static AiScoutPlanner.ScoutTarget? FindTarget(PlayerSetupData actor, ArmyData army, HexMap map,
            IReadOnlyCollection<HexCoord> excludedTargets = null)
        {
            if (actor == null || army == null || map == null)
                return null;

            HexCoord? citadelHex = actor.CitadelHexQ.HasValue && actor.CitadelHexR.HasValue
                ? new HexCoord(actor.CitadelHexQ.Value, actor.CitadelHexR.Value)
                : (HexCoord?)null;

            int? wavefrontDistance = citadelHex.HasValue
                ? NearestUnvisitedDistance(actor, map, citadelHex.Value)
                : null;

            // Frontier (opens ≥1 fresh neighbor) always wins over cleanup (opens none) — a cleanup
            // candidate is only ever the actual pick once no frontier candidate exists anywhere on
            // the map this step (see AiConfig.visitCleanupScore's own comment on why this matters:
            // without the split, a zero-value hole in the coverage could outscore — or simply get
            // picked ahead of — genuine unexplored frontier just because it happened to be closer).
            //
            // Frontier itself is ALSO split local-vs-distant now (2026-08-24 fix, see AiConfig.
            // visitFrontierLocalRadius's own comment for the root cause) — a frontier candidate
            // within that radius of THIS scout always wins over one farther away, even if the
            // farther one scores higher on scoutProximityWeight/freshNeighborWeight alone; only once
            // no local frontier exists anywhere does the unrestricted best-scoring distant frontier
            // become the fallback. Cleanup is unaffected — still only ever considered once neither
            // frontier bucket has anything (see below).
            AiScoutPlanner.ScoutTarget? bestLocalFrontier = null;
            AiScoutPlanner.ScoutTarget? bestDistantFrontier = null;
            AiScoutPlanner.ScoutTarget? bestCleanup = null;
            foreach (HexCoord candidate in map.AllCoords)
            {
                AiScoutPlanner.ScoutTarget? scored = ScoreCandidate(actor, army, map, candidate, citadelHex, wavefrontDistance, excludedTargets);
                if (!scored.HasValue)
                    continue;
                if (scored.Value.IsCleanup)
                {
                    if (bestCleanup == null || scored.Value.Score > bestCleanup.Value.Score)
                        bestCleanup = scored;
                    continue;
                }
                if (HexGridMath.Distance(army.Hex, candidate) <= AiConfig.visitFrontierLocalRadius)
                {
                    if (bestLocalFrontier == null || scored.Value.Score > bestLocalFrontier.Value.Score)
                        bestLocalFrontier = scored;
                }
                else
                {
                    if (bestDistantFrontier == null || scored.Value.Score > bestDistantFrontier.Value.Score)
                        bestDistantFrontier = scored;
                }
            }
            bool isDistantFallback = bestLocalFrontier == null && bestDistantFrontier != null;
            AiScoutPlanner.ScoutTarget? best = bestLocalFrontier ?? bestDistantFrontier ?? bestCleanup;
            if (best == null)
                return null;
            if (isDistantFallback)
            {
                best = new AiScoutPlanner.ScoutTarget(best.Value.Hex, best.Value.Score,
                    best.Value.Reason + " — distant frontier fallback, nothing unexplored closer", best.Value.IsCleanup);
            }

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

            AiScoutPlanner.ScoutTarget? affordableFrontier = null;
            AiScoutPlanner.ScoutTarget? affordableCleanup = null;
            foreach (HexCoord neighbor in HexGridMath.Neighbors(army.Hex))
            {
                if (!map.TryGetTerrainAt(neighbor, out TerrainTypeEntry entry)
                    || army.CurrentMovement < Mathf.Max(1, entry.moveCost))
                {
                    continue;
                }
                AiScoutPlanner.ScoutTarget? scored = ScoreCandidate(actor, army, map, neighbor, citadelHex, wavefrontDistance, excludedTargets);
                if (!scored.HasValue)
                    continue;
                if (scored.Value.IsCleanup)
                {
                    if (affordableCleanup == null || scored.Value.Score > affordableCleanup.Value.Score)
                        affordableCleanup = scored;
                }
                else
                {
                    if (affordableFrontier == null || scored.Value.Score > affordableFrontier.Value.Score)
                        affordableFrontier = scored;
                }
            }
            // Nothing affordable even among direct neighbors — the army genuinely can't use its
            // remaining movement this turn, so fall back to the original (unaffordable) pick;
            // AiTurnController's own "stuck" handling takes it from there, same as before this
            // affordability check existed. Same frontier-over-cleanup preference as the full scan
            // above.
            return affordableFrontier ?? affordableCleanup ?? best;
        }

        // Shared scoring/filtering for a single candidate hex — used both for the full-map scan
        // above and the neighbor-only affordability fallback, so the two never score a hex
        // differently.
        private static AiScoutPlanner.ScoutTarget? ScoreCandidate(PlayerSetupData actor, ArmyData army, HexMap map,
            HexCoord candidate, HexCoord? citadelHex, int? wavefrontDistance,
            IReadOnlyCollection<HexCoord> excludedTargets = null)
        {
            if (candidate.Equals(army.Hex))
                return null;
            if (VisionSystem.IsVisited(actor, candidate))
                return null;
            // Deconfliction (2026-08-24, project owner's own log audit): a hex another VisitHex
            // task already committed to as ITS destination this turn is off the table for this
            // one — without this, two scouts independently re-run this same scan every step and
            // land on the identical best-scoring wavefront hex, so the second one to arrive finds
            // nothing left to discover there (see AiScoutPlanner.TryContinueVisitTask/
            // TryStartVisitCandidates' own callers for how this set gets built — never includes
            // THIS task's own current target, only every other active VisitHex task's).
            if (excludedTargets != null && excludedTargets.Contains(candidate))
                return null;

            if (citadelHex.HasValue && wavefrontDistance.HasValue
                && HexGridMath.Distance(citadelHex.Value, candidate) > wavefrontDistance.Value + AiConfig.visitRingBand)
            {
                return null;
            }

            bool visible = VisionSystem.IsVisible(actor, candidate);

            // A visible hex known to hold an enemy/neutral army is never a destination — this
            // composition never fights (see this class's own "Поведение" comment).
            if (visible && BattleInitiator.FindEnemyAt(candidate, actor) != null)
                return null;

            // Same non-neutral filter as TryFlee, checked preemptively here instead of only
            // reactively once the scout has already arrived: a candidate within scoutFleeRadius
            // of a known (memory, not necessarily currently visible) non-neutral sighting would
            // just have TryFlee retreat it again next turn, burning AP getting there for nothing
            // — see the user's own report of a scout walking up to a hex next to an enemy
            // citadel it already knew about, only to immediately retreat.
            //
            // Стелс · Задача 3 — that hard exclusion holds only for an ORDINARY (visible) scout.
            // A scout CURRENTLY IN STEALTH is instead softly penalised per nearby sighting
            // (scoutStealthRiskPenalty): it can still slip in close when every safer frontier hex
            // scores clearly worse, but an equal one elsewhere wins. Detection status is
            // deliberately NOT consulted here — unlike TryFlee (which reacts after the fact), the
            // scout's owner has no honest way to know whether a planned move will get it spotted;
            // "is the scout hidden at all" is the only gate. Risk is read only from honest
            // AiMapMemory, never live stealth-side vision.
            bool scoutHidden = army.Members.Any(m => m.IsHidden);
            float stealthRiskPenalty = 0f;
            foreach (AiMapMemory.KnownEnemySighting sighting in
                     AiMapMemory.KnownEnemySightingsNear(actor, new[] { candidate }, AiConfig.scoutFleeRadius))
            {
                if (sighting.Owner != null && sighting.Owner.IsNeutral)
                    continue;
                if (!scoutHidden)
                    return null;
                stealthRiskPenalty += AiConfig.scoutStealthRiskPenalty;
            }

            // A cooled-down scout-danger zone (see AiMapMemory.ScoutDangerZones' own comment)
            // keeps excluding this candidate even once the sighting that first flagged it as
            // dangerous has gone stale — 2026-08-24 fix, project owner's own report: without this,
            // a scout that fled home cycled straight back out to the exact same still-there enemy
            // every few turns the moment enemySightingMemoryTurns(2) let the sighting itself expire.
            if (AiMapMemory.IsScoutDangerous(actor, candidate))
                return null;

            int distanceFromScout = HexGridMath.Distance(army.Hex, candidate);
            float score = -distanceFromScout * AiConfig.scoutProximityWeight;

            int freshNeighbors = 0;
            foreach (HexCoord neighbor in HexGridMath.Neighbors(candidate))
                if (map.TryGetTerrainAt(neighbor, out _) && !VisionSystem.IsVisited(actor, neighbor))
                    freshNeighbors++;
            score += freshNeighbors * AiConfig.freshNeighborWeight;

            // A candidate that opens nothing new is only ever worth visiting as a nearby cleanup —
            // see FindTarget's own frontier/cleanup split and AiConfig.visitCleanupMaxDistance's own
            // comment. Excluded outright once too far from the SCOUT (not the citadel) for that to
            // be worthwhile — a distant single-hex gap just waits for the wavefront to reach it
            // naturally instead of pulling a scout across the map for zero real gain.
            bool isCleanup = freshNeighbors == 0;
            if (isCleanup && distanceFromScout > AiConfig.visitCleanupMaxDistance)
                return null;

            if (citadelHex.HasValue)
                score -= HexGridMath.Distance(citadelHex.Value, candidate) * AiConfig.visitTargetCitadelWeight;

            // Стелс · Задача 3 — soft detection-risk penalty for a hidden scout (see the
            // scoutHidden loop above); zero for a visible one (it was excluded outright there).
            score -= stealthRiskPenalty;

            string reason = $"{distanceFromScout} hexes away, opens {freshNeighbors} adjacent unvisited hex(es)";
            if (stealthRiskPenalty > 0f)
                reason += $" — near a known enemy army, -{stealthRiskPenalty:0} stealth-risk";
            return new AiScoutPlanner.ScoutTarget(candidate, score, reason, isCleanup);
        }

        // This composition never fights (see this class's own "Поведение" comment), so it must
        // never be routed blind onto a hex this player's own memory already knows holds an army
        // (enemy or neutral) — the ordinary move order (HexSelectionController.Movement.
        // IssueMoveOrder) only avoids a CURRENTLY VISIBLE enemy, which says nothing about a
        // sighting recorded earlier and no longer in sight, several hexes along the route to a
        // distant wavefront target. 2026-08-21 fix (project owner's own report): a scout/scout-hero
        // walking the FULL multi-hex path straight to a far wavefront target used to cross a
        // remembered-but-not-currently-visible hex unchecked, occasionally landing right on top of
        // a neutral army neither the target-selection scoring nor the ordinary vision-only
        // move-order pipeline had any way to see coming.
        //
        // 2026-08-22 correction (project owner's own report — the first pass over-corrected): the
        // very first version of this blocked EVERY not-yet-visited hex outright, `targetHex` itself
        // the one exemption — modeled on AiEconomyPlanner.FindNextVisitedStep's own solo-hero rule.
        // That's wrong for this task specifically: VisitHexTask's whole purpose is walking INTO
        // fog nobody's ever seen (that's how new ground gets discovered at all), and its own target
        // is routinely several hexes past the visited frontier (see freshNeighborWeight — a
        // richer, farther hex often outscores an adjacent one). Blocking every hex on the way there
        // made a fresh scout's very first move (and most later ones) fail outright — no known
        // threat anywhere, just ordinary unseen ground the path had to cross — which is exactly the
        // stuck-at-the-garrison-forever bug this was traced back to. The actual risk this method
        // exists to guard against is a REMEMBERED army sitting on the path, not fog itself, so only
        // that is blocked now (AiMapMemory.KnownEnemySightingAt — honest, fog-of-war-respecting,
        // covers enemy AND neutral alike, same source RaidWeakerArmyTask/TryFlee already trust for
        // "is anything known to be here").
        //
        // `targetHex` stays exempted regardless (even if memory flags it) — the caller
        // (VisitHexTask.FindTarget) already refuses to ever pick a destination with a known
        // sighting on or near it; this is purely about hexes along the WAY there. Null if no such
        // route exists yet — the caller treats that as "nothing to do this step", same as any
        // other unaffordable/blocked move candidate, not a reason to give up on `targetHex` itself.
        public static HexCoord? FindNextSafeStep(HexMap map, ArmyData army, HexCoord targetHex)
        {
            System.Func<HexCoord, bool> blockHex = hex => !hex.Equals(targetHex)
                && (AiMapMemory.KnownEnemySightingAt(army.Owner, hex).HasValue || AiMapMemory.IsScoutDangerous(army.Owner, hex));
            // Routed through the shared AiTurnController.FindAffordableStep (2026-08-23 fix) —
            // this method's own path (blocked around known sightings) can differ from
            // IsFirstStepAffordable's own unblocked one below, so THIS is the path that actually
            // needs its first step checked against army.CurrentMovement; checking only the other
            // one let a real move order through that the movement system then rejected outright.
            return AiTurnController.FindAffordableStep(map, army, targetHex, blockHex);
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
            return AiTurnController.FindAffordableStep(map, army, destination) != null;
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
