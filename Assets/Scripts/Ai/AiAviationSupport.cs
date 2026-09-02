using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Game.Aviation;
using Game.Cards;
using Game.Economy;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Units;
using UnityEngine;

namespace Game.Ai
{
    // Shared, read-only aviation planning primitives — the one place AirStrikeTask/AirReconTask
    // (and AiManagementPlanner's own aviation card-placement branch) compute route/capacity logic,
    // so the two tasks can never drift apart on what "a complete safe sortie" means. Same role
    // WorthIt.cs already plays for combat estimation: pure functions over the exact same shared
    // rules a human player's own moves go through (AviationRules/AviationActions/HexPathfinder) —
    // never a parallel resolver, never mutates anything.
    public static class AiAviationSupport
    {
        // Every one of this player's own owned, airfield-CAPABLE hexes (citadel + every later
        // Base) — NOT simply every garrison hex (AiTurnController.OwnGarrisonHexes), since a Base's
        // own airfieldCapacity is data-driven per card and can be zero (see AviationRules.
        // IsAirfieldBuilding). "Any owned airfield with free capacity" per the spec is always
        // filtered from this set, never hard-coded to the citadel or the launch airfield.
        public static IEnumerable<HexCoord> OwnedAirfieldHexes(PlayerSetupData player) =>
            AiTurnController.OwnGarrisonHexes(player).Where(hex => AviationRules.IsOwnedAirfieldAt(hex, player));

        // Coarse route-risk read — every known-AA-tagged enemy sighting (AiMapMemory.
        // KnownEnemySighting.HasAntiAir, see that field's own comment) within raidThreatRadius of
        // ANY hex the given leg crosses. Deliberately approximate (no per-unit AA radius is kept in
        // memory, only the bool flag) — good enough to rank routes relative to each other, never
        // meant as an exact prediction of what will actually react (that stays AntiAirRules' own
        // live, honest-fog job at execution time). Moved here from AirStrikeTask (2026-08-26,
        // project owner's own AirRecon-AA spec point 2) so AirReconTask's own route ranking reads
        // the exact same route-risk number instead of a second, separately-drifting copy — same
        // "one shared place" role this class already plays for TryPlanSortie/FreeLandingCapacity.
        public static int KnownAaExposure(PlayerSetupData actor, HexPath leg) =>
            leg == null ? 0 : KnownAaExposureOver(actor, leg.Hexes);

        // Exposure already unavoidable given where the army is standing RIGHT NOW — e.g. AA that
        // was only revealed once the strike itself landed on this hex. Used exclusively by the
        // emergency-return searches below (TryReplan/TryReplanMultiTurnReturn) as a baseline: a
        // route home necessarily starts inside whatever's already covering the current hex, so
        // that part of its exposure was never an avoidable choice and must not disqualify the
        // route the way a genuinely NEW zone should (2026-08-26 fix, project owner's own report —
        // an aircraft that discovers AA only on arrival at its target had every route home
        // hard-rejected forever after, since KnownAaExposure(path) sees the same already-standing-
        // in-it sighting on literally every candidate path).
        public static int KnownAaExposureAt(PlayerSetupData actor, HexCoord hex) =>
            KnownAaExposureOver(actor, new[] { hex });

        private static int KnownAaExposureOver(PlayerSetupData actor, IEnumerable<HexCoord> hexes)
        {
            int exposure = 0;
            foreach (AiMapMemory.KnownEnemySighting sighting in AiMapMemory.AllKnownEnemySightings(actor))
            {
                if (!sighting.HasAntiAir)
                    continue;
                if (hexes.Any(hex => HexGridMath.Distance(hex, sighting.Hex) <= AiConfig.raidThreatRadius))
                    exposure++;
            }
            return exposure;
        }

        // How many MORE aircraft `hex` can actually receive right now. The engine itself only
        // capacity-checks the STORED container (new card deployment, see AviationRules.
        // FreeAirfieldCapacity/ArmyActions.DeployUnitFromCard) — a landed, already-launched air
        // army is a separate ArmyData the move layer never caps. This is deliberately MORE
        // conservative than the engine strictly requires: it also counts every other already-
        // landed air army's own aircraft against the same capacity, so the AI never voluntarily
        // stacks more aircraft onto one airfield hex than its stated capacity, even though nothing
        // stops it from doing so. Also subtracts every OTHER active sortie's own claim on this
        // landing hex via ReservedLandingSlots below (2026-08-26 fix) — an in-flight sortie is
        // just as real a claim on the slot it's headed for as an aircraft already sitting there.
        // `excluding` — the mover's own air army, so a sortie re-checking its ALREADY-chosen
        // landing hex mid-flight doesn't count itself against its own capacity (both as a landed
        // army and, via its own AiTask, as a reservation).
        public static int FreeLandingCapacity(HexCoord hex, PlayerSetupData owner, ArmyData excluding = null)
        {
            int capacity = AviationRules.AirfieldCapacityAt(hex, owner);
            if (capacity <= 0)
                return 0;
            int used = AviationRules.FindAirfieldAt(hex, owner)?.Members.Count ?? 0;
            foreach (ArmyData army in ArmyRegistry.AllAt(hex))
                if (army != excluding && army.Owner == owner && AviationRules.IsAirArmy(army))
                    used += army.Members.Count;
            AiTask excludingTask = excluding != null ? AiTaskRegistry.TaskFor(owner, excluding) : null;
            used += ReservedLandingSlots(hex, owner, excludingTask);
            return Mathf.Max(0, capacity - used);
        }

        // How many of `hex`'s own free slots are already spoken for by OTHER active AirStrike/
        // AirRecon sorties committed to land there but not physically there yet (still outbound,
        // or inbound but not yet arrived — see AiTask.LandingHex's own comment). Landed aircraft
        // are deliberately NOT counted again here — FreeLandingCapacity's own ArmyRegistry loop
        // above already counts anything physically sitting on `hex` right now, landed or not;
        // double-counting a task whose army has already arrived would undercount capacity for
        // nothing. `excludingTask` lets a sortie re-checking its OWN already-chosen landing hex
        // exclude its own prior claim, the same role FreeLandingCapacity's own `excluding`
        // ArmyData param already plays against the ArmyRegistry loop (2026-08-26 fix, project
        // owner's own report: two independently-launched 2-aircraft groups could otherwise both
        // claim the same single free slot on a 2-capacity base, since neither's outbound flight
        // was ever visible to the other's own capacity check until it actually landed).
        private static int ReservedLandingSlots(HexCoord hex, PlayerSetupData owner, AiTask excludingTask)
        {
            int reserved = 0;
            foreach (AiTask task in AiTaskRegistry.TasksFor(owner))
            {
                if (task == excludingTask || (task.Kind != AiTaskKind.AirStrike && task.Kind != AiTaskKind.AirRecon))
                    continue;
                if (task.Army == null || !task.LandingHex.Equals(hex) || task.Army.Hex.Equals(hex))
                    continue;
                reserved += task.Army.Members.Count;
            }
            return reserved;
        }

        // How many MORE times this group can safely end a turn away from an owned airfield right
        // now, before AviationTurnLifecycle.ResolveEndOfTurn's own fuel-damage rule
        // (ConsecutiveUnlandedEnds > TurnsWithoutRefuel) would fire. A plane (TurnsWithoutRefuel==0,
        // never landed away) always reads 0 here — the existing single-turn Sortie/PlanSortieCore
        // model already covers it exactly as before (2026-08-26 multi-turn aviation spec, point 6:
        // "самолёты должны сохранить текущее поведение"). A mixed group is bound by its single most
        // fuel-limited member (spec point 2/14: "группа ограничена самым ограниченным участником")
        // — never per-member, since this codebase never splits an air army mid-flight.
        public static int SafeUnlandedEndsRemaining(IReadOnlyList<UnitData> aircraft)
        {
            if (aircraft == null || aircraft.Count == 0)
                return 0;
            int min = int.MaxValue;
            foreach (UnitData unit in aircraft)
                min = Mathf.Min(min, Mathf.Max(0, unit.TurnsWithoutRefuel - unit.ConsecutiveUnlandedEnds));
            return min == int.MaxValue ? 0 : min;
        }

        public static int SafeUnlandedEndsRemaining(ArmyData airArmy) => SafeUnlandedEndsRemaining(airArmy?.Members);

        // AI-AIR-02 — may this already-airborne group legally END the current turn still aloft AND
        // still be guaranteed its mandatory recovery afterwards? This is the test that turns a
        // helicopter's two-turn endurance into a real tactical window: when it is true, the recon
        // executor must NOT reserve movement to boomerang home this turn.
        //
        //   CanSafelyRemainAirborne =
        //       endurance allows this turn's EndTurn                (SafeUnlandedEndsRemaining >= 1)
        //       AND a realistic recovery plan exists right now      (same-turn OR multi-turn return)
        //       AND next turn still allows the mandatory return      (see below)
        //
        // A plane (SafeUnlandedEndsRemaining == 0) always fails the first clause, so its existing
        // single-turn boomerang model is completely untouched. Pure query — never mutates unit
        // state, re-derives everything from the live shared aviation rules.
        public static bool CanSafelyEndTurnAirborne(ArmyData airArmy, HexMap map, PlayerSetupData owner)
        {
            if (!AviationRules.IsValidAirArmy(airArmy) || map == null || owner == null)
                return false;
            int safeEnds = SafeUnlandedEndsRemaining(airArmy);
            if (safeEnds < 1)
                return false; // ending this turn aloft is already illegal / would take fuel damage

            HexCoord? sameTurn = TryReplan(airArmy, map, owner);
            if (sameTurn.HasValue)
                // A route that already fits this turn's partly-spent movement is trivially safe
                // next turn: full movement refreshes, the wing lands in one turn, zero further
                // unlanded ends. Ending this turn aloft only spends the safe end we just verified.
                return true;

            MultiTurnSortie? multi = TryReplanMultiTurnReturn(airArmy, map, owner);
            if (!multi.HasValue)
                return false; // no recovery plan of any kind — must deal with it now
            // Multi-turn-only return: enough endurance margin must remain AFTER this turn's end is
            // spent to cover every unlanded end that return still needs.
            return safeEnds - 1 >= multi.Value.RequiredUnlandedEnds;
        }

        // start airfield -> action hex -> any owned airfield with free capacity — the shared
        // safety invariant every AirStrike/AirRecon continuation re-derives fresh (never cached on
        // the task) before proposing a launch or a further step.
        public readonly struct Sortie
        {
            public readonly HexCoord ActionHex;
            public readonly HexCoord LandingHex;
            public readonly HexPath OutboundPath;
            public readonly HexPath ReturnPath;
            public readonly int TotalCost;

            public Sortie(HexCoord actionHex, HexCoord landingHex, HexPath outboundPath, HexPath returnPath, int totalCost)
            {
                ActionHex = actionHex;
                LandingHex = landingHex;
                OutboundPath = outboundPath;
                ReturnPath = returnPath;
                TotalCost = totalCost;
            }
        }

        public static Sortie? TryPlanSortie(ArmyData airArmy, HexCoord actionHex, HexMap map, PlayerSetupData owner)
        {
            if (!AviationRules.IsValidAirArmy(airArmy) || airArmy.Owner != owner)
                return null;
            return PlanSortieCore(airArmy.Hex, airArmy, army => army.CurrentMovement, path => AviationRules.PathMoveCost(airArmy, path),
                airArmy.Members.Count, 0, actionHex, map, owner);
        }

        // Same "start -> action hex -> owned airfield with capacity" plan, computed for aircraft
        // that haven't launched yet (still sitting in an airfield's own stored container — see
        // AirStrikeTask.FindLaunchCandidates) — used to decide whether launching at all is even
        // worth it, and to pick the target/landing pair a LaunchAirStrike/LaunchAirRecon candidate
        // carries. No ArmyData exists yet to read CurrentMovement/PathMoveCost off, so this uses
        // each aircraft's own fresh EffectiveMoveMax (nothing's been spent yet this turn — a stored
        // aircraft never moves before it launches) and a flat 1-MP-per-hex cost (see
        // AviationRules.PathMoveCost's own comment — every air army pays exactly that, regardless
        // of terrain).
        public static Sortie? TryPlanSortieFromStorage(HexCoord airfieldHex, IReadOnlyList<UnitData> aircraft,
            HexCoord actionHex, HexMap map, PlayerSetupData owner)
        {
            if (aircraft == null || aircraft.Count == 0)
                return null;
            int movement = aircraft.Min(AviationRules.EffectiveMoveMax);
            return PlanSortieCore(airfieldHex, null, _ => movement, path => path.Hexes.Count - 1,
                aircraft.Count, aircraft.Count, actionHex, map, owner);
        }

        // requiredSlots: how many aircraft need a free landing slot together — the WHOLE group
        // lands as one stack, so a landing hex with fewer free slots than that must be rejected
        // outright, not just "at least one" (2026-08-26 fix, project owner's own report — two
        // aircraft could otherwise both plan to land on a base with only 1 free slot). vacatingAtStart:
        // for a still-STORED group (TryPlanSortieFromStorage), these exact aircraft are themselves
        // counted in FindAirfieldAt(startHex)'s own Members.Count right now, even though they're
        // about to launch and free that many slots up — so a fully-packed airfield can still plan a
        // round-trip sortie back to itself (the other 2026-08-26 edge case: without this, the sole
        // airfield being full would make the AI think it could never fly a sortie that returns
        // there, even though take-off itself vacates the slots this same sortie needs to land).
        // Zero for an already-airborne army (TryPlanSortie/TryReplan) — it was never part of any
        // airfield's stored container, so no double-count to undo.
        // Landing choice rewritten 2026-08-26 (project owner's own follow-up spec, item 1+2 —
        // "ПВО единым жёстким фильтром" / "не выбирать единственный самый дешёвый landing до
        // расчёта score цели"). Two things used to be wrong together here: (a) a route crossing
        // known AA was still a valid candidate, only ever losing a tie-break it could still win
        // against an equally-cheap safe one, and (b) EVERY reachable owned airfield is now actually
        // weighed against each other by safety-then-forwardness-then-cost, not just by raw total
        // path cost — so AirStrikeTask.FindTarget/AirReconTask.FindReconHex, which both call this
        // (via TryPlanSortie/TryPlanSortieFromStorage) once per candidate TARGET, get back the one
        // truly best target+landing pairing for that target instead of a cheapest-path landing that
        // happened to lock in before the target's own score (which leans on this Sortie's forward-
        // landing bonus, see AirReconTask.FindReconHex) was ever computed. A landing whose route
        // (either leg) carries ANY known AA exposure is dropped outright whenever the AA-free set is
        // non-empty — never merely ranked down — matching TryReplan/TryPlanSortiePreferForwardLanding
        // below. Returns null when no AA-free candidate reaches within the mover's own movement
        // budget this turn — callers (LaunchAirStrike/LaunchAirRecon candidate search) must simply
        // not offer that target as a launch option, per spec's own "cancel the voluntary task/don't
        // launch" — there is deliberately no "fly the unsafe route anyway" fallback for a launch
        // that hasn't happened yet.
        private static Sortie? PlanSortieCore(HexCoord startHex, ArmyData excludingFromCapacity,
            System.Func<ArmyData, int> movementBudget, System.Func<HexPath, int> pathCost,
            int requiredSlots, int vacatingAtStart, HexCoord actionHex, HexMap map, PlayerSetupData owner)
        {
            if (map == null || owner == null)
                return null;

            HexPath outbound = HexPathfinder.FindPath(map, startHex, actionHex, flatCost: true);
            if (outbound == null)
                return null;
            int outboundCost = pathCost(outbound);
            int outboundExposure = KnownAaExposure(owner, outbound);
            int movement = movementBudget(excludingFromCapacity);

            Sortie? best = null;
            int bestForward = int.MaxValue;
            int bestCost = int.MaxValue;
            foreach (HexCoord landing in OwnedAirfieldHexes(owner))
            {
                int freeSlots = FreeLandingCapacity(landing, owner, excludingFromCapacity);
                if (landing.Equals(startHex))
                    freeSlots += vacatingAtStart;
                if (freeSlots < requiredSlots)
                    continue;
                HexPath ret = HexPathfinder.FindPath(map, actionHex, landing, flatCost: true);
                if (ret == null)
                    continue;
                int totalCost = outboundCost + pathCost(ret);
                if (totalCost > movement)
                    continue;
                if (outboundExposure + KnownAaExposure(owner, ret) > 0)
                    continue; // known AA on this route — never a candidate while a safe one might exist

                int forward = NearestKnownEnemyDistance(owner, landing);
                bool better = best == null || forward < bestForward
                    || (forward == bestForward && totalCost < bestCost);
                if (better)
                {
                    best = new Sortie(actionHex, landing, outbound, ret, totalCost);
                    bestForward = forward;
                    bestCost = totalCost;
                }
            }
            return best;
        }

        // Multi-turn aviation spec (2026-08-26, project owner's own follow-up to the helicopter/
        // TurnsWithoutRefuel cards already in play): a Sortie above only ever proves a round trip
        // that fits inside ONE turn's own movement budget — the correct, safe model for a plane
        // (TurnsWithoutRefuel==0, must always land the very turn it flies), but needlessly grounds
        // any card with a positive TurnsWithoutRefuel margin whenever the target is merely a little
        // farther than one turn's reach. This struct/pair of planners covers exactly that second
        // case: a route that may span several real game turns, proven safe end-to-end (start ->
        // action -> landing) BEFORE it's ever offered as a candidate, the same "prove the whole trip
        // up front" contract Sortie already keeps, just extended across turn boundaries via
        // TrySimulateHexSequence below. RequiredUnlandedEnds is the worst single stretch of
        // consecutive turn-ends this route asks the group to spend away from ANY owned airfield —
        // must never exceed SafeUnlandedEndsRemaining, checked live at every step (never trusted
        // stale once committed — see ContinueSortie).
        public readonly struct MultiTurnSortie
        {
            public readonly HexCoord ActionHex;
            public readonly HexCoord LandingHex;
            public readonly HexPath PathToAction;
            public readonly HexPath PathFromActionToLanding;
            public readonly int TotalRouteCost;
            public readonly int RequiredTurns;
            public readonly int RequiredUnlandedEnds;
            public readonly HexCoord CurrentTurnDestination;
            public readonly bool ReachesActionThisTurn;
            public readonly bool LandsThisTurn;

            public MultiTurnSortie(HexCoord actionHex, HexCoord landingHex, HexPath pathToAction,
                HexPath pathFromActionToLanding, int totalRouteCost, int requiredTurns, int requiredUnlandedEnds,
                HexCoord currentTurnDestination, bool reachesActionThisTurn, bool landsThisTurn)
            {
                ActionHex = actionHex;
                LandingHex = landingHex;
                PathToAction = pathToAction;
                PathFromActionToLanding = pathFromActionToLanding;
                TotalRouteCost = totalRouteCost;
                RequiredTurns = requiredTurns;
                RequiredUnlandedEnds = requiredUnlandedEnds;
                CurrentTurnDestination = currentTurnDestination;
                ReachesActionThisTurn = reachesActionThisTurn;
                LandsThisTurn = landsThisTurn;
            }
        }

        public static MultiTurnSortie? TryPlanMultiTurnSortie(ArmyData airArmy, HexCoord actionHex, HexMap map, PlayerSetupData owner)
        {
            if (!AviationRules.IsValidAirArmy(airArmy) || airArmy.Owner != owner)
                return null;
            return PlanMultiTurnSortieCore(airArmy.Hex, airArmy, airArmy.Members, airArmy.CurrentMovement,
                path => AviationRules.PathMoveCost(airArmy, path), airArmy.Members.Count, 0, actionHex, map, owner);
        }

        public static MultiTurnSortie? TryPlanMultiTurnSortieFromStorage(HexCoord airfieldHex, IReadOnlyList<UnitData> aircraft,
            HexCoord actionHex, HexMap map, PlayerSetupData owner)
        {
            if (aircraft == null || aircraft.Count == 0)
                return null;
            int movement = aircraft.Min(AviationRules.EffectiveMoveMax);
            return PlanMultiTurnSortieCore(airfieldHex, null, aircraft, movement, path => path.Hexes.Count - 1,
                aircraft.Count, aircraft.Count, actionHex, map, owner);
        }

        // Same landing search as PlanSortieCore (capacity/AA hard filter/forward-then-cost ranking,
        // all through the exact same FreeLandingCapacity/KnownAaExposure/NearestKnownEnemyDistance
        // helpers) except the round-trip feasibility test is TrySimulateHexSequence's own turn-by-
        // turn simulation instead of a flat "outboundCost + returnCost <= movement" check. Ranks by
        // fewest real turns first (a helicopter that can reach and return in 2 turns always beats
        // one needing 3, regardless of forwardness/cost), THEN forwardness, THEN cost — same
        // tie-break shape PlanSortieCore already uses, just with RequiredTurns as the new outermost
        // tier. Returns null outright whenever this group has no safe unlanded-end margin at all
        // (SafeUnlandedEndsRemaining <= 0) — that's exactly a plane, and planes stay on
        // PlanSortieCore's existing single-turn model untouched (spec point 6).
        private static MultiTurnSortie? PlanMultiTurnSortieCore(HexCoord startHex, ArmyData excludingFromCapacity,
            IReadOnlyList<UnitData> aircraft, int firstTurnMovement, System.Func<HexPath, int> pathCost,
            int requiredSlots, int vacatingAtStart, HexCoord actionHex, HexMap map, PlayerSetupData owner)
        {
            if (map == null || owner == null)
                return null;
            int safeRemaining = SafeUnlandedEndsRemaining(aircraft);
            if (safeRemaining <= 0)
                return null;

            HexPath outbound = HexPathfinder.FindPath(map, startHex, actionHex, flatCost: true);
            if (outbound == null)
                return null;
            if (KnownAaExposure(owner, outbound) > 0)
                return null; // known AA anywhere on the outbound leg — hard filter, whole-route (spec point 7)

            MultiTurnSortie? best = null;
            int bestTurns = int.MaxValue;
            int bestForward = int.MaxValue;
            int bestCost = int.MaxValue;
            foreach (HexCoord landing in OwnedAirfieldHexes(owner))
            {
                int freeSlots = FreeLandingCapacity(landing, owner, excludingFromCapacity);
                if (landing.Equals(startHex))
                    freeSlots += vacatingAtStart;
                if (freeSlots < requiredSlots)
                    continue;
                HexPath ret = HexPathfinder.FindPath(map, actionHex, landing, flatCost: true);
                if (ret == null)
                    continue;
                if (KnownAaExposure(owner, ret) > 0)
                    continue; // known AA on the return leg — same hard filter as the outbound leg

                if (!TrySimulateHexSequence(CombineRoute(outbound, ret), outbound.Hexes.Count - 1, firstTurnMovement,
                    aircraft, safeRemaining, owner, out int requiredTurns, out int requiredUnlandedEnds,
                    out HexCoord turn1Destination, out bool reachesActionThisTurn, out bool landsThisTurn))
                    continue;

                int totalCost = pathCost(outbound) + pathCost(ret);
                int forward = NearestKnownEnemyDistance(owner, landing);
                bool better = best == null || requiredTurns < bestTurns
                    || (requiredTurns == bestTurns && forward < bestForward)
                    || (requiredTurns == bestTurns && forward == bestForward && totalCost < bestCost);
                if (better)
                {
                    best = new MultiTurnSortie(actionHex, landing, outbound, ret, totalCost, requiredTurns,
                        requiredUnlandedEnds, turn1Destination, reachesActionThisTurn, landsThisTurn);
                    bestTurns = requiredTurns;
                    bestForward = forward;
                    bestCost = totalCost;
                }
            }
            return best;
        }

        // Repeat-strike spec (2026-08-26 follow-up) — can this army, PARKED at currentHex (no MP
        // spent getting there — a repeat strike never moves the army, see AviationCombatPresenter.
        // ResolveAirStrikeAtCurrentHex), still reach a safe owned airfield NEXT turn, once its own
        // movement refreshes? Deliberately uses each aircraft's fresh EffectiveMoveMax (spec point 3:
        // "движение, которое будет восстановлено на следующем ходу"), never the army's current,
        // already-spent CurrentMovement — this is a forward-looking check for a turn that hasn't
        // started yet. The repeat strike itself never costs its own MP (mirrors the live rule: a
        // strike has never charged movement of its own, see AviationCombatPresenter.RunAirStrike —
        // "repeat" reuses the exact same free mechanic), so no cost is deducted here beyond the
        // return path itself. Same capacity/AA-hard-filter/forward-then-cost ranking every other
        // landing search in this class already applies — one shared rule, never a second copy.
        public static bool CanStrikeNextTurnAndLand(ArmyData airArmy, HexCoord currentHex, HexMap map, PlayerSetupData owner,
            out HexCoord landingHex)
        {
            if (!AviationRules.IsValidAirArmy(airArmy))
            {
                landingHex = default;
                return false;
            }
            return CanStrikeNextTurnAndLandCore(airArmy.Members, airArmy, currentHex, default, 0, map, owner, out landingHex);
        }

        // Estimate-time overload (AiAggressionPlanner.EvaluateRaidSupport's raid-support scoring) —
        // the launch candidate hasn't flown yet, so there's no ArmyData to validate/exclude from
        // landing capacity, only the raw aircraft list a launch would use. Same rule otherwise; a
        // candidate can only ever be "estimated eligible" here, real eligibility is still
        // re-verified live once the army is actually sitting on the hex (TryEnterLoiterAtTarget/
        // CanStrikeNextTurnAndLand(ArmyData, ...) above).
        //
        // launchAirfieldHex (2026-08-26 fix, project owner's own report): these aircraft are still
        // physically sitting in THAT airfield's own stored container right now — the very launch
        // this estimate is scoring is what will vacate their slots there. Same vacatingAtStart idea
        // TryPlanSortieFromStorage/PlanMultiTurnSortieCore already apply for the outbound leg — here
        // it's the second-strike LANDING leg that can otherwise wrongly see the home field as full
        // of aircraft that, by the time a second strike would land, will already have left. Applies
        // ONLY to that one specific airfield hex, never to any other owned airfield the search
        // considers — those are unaffected by this launch either way.
        public static bool CanStrikeNextTurnAndLand(IReadOnlyList<UnitData> aircraft, HexCoord currentHex,
            HexCoord launchAirfieldHex, HexMap map, PlayerSetupData owner, out HexCoord landingHex)
        {
            if (aircraft == null || aircraft.Count == 0)
            {
                landingHex = default;
                return false;
            }
            return CanStrikeNextTurnAndLandCore(aircraft, null, currentHex, launchAirfieldHex, aircraft.Count, map, owner, out landingHex);
        }

        private static bool CanStrikeNextTurnAndLandCore(IReadOnlyList<UnitData> aircraft, ArmyData excludingFromCapacity,
            HexCoord currentHex, HexCoord vacatingHex, int vacatingAtStart, HexMap map, PlayerSetupData owner, out HexCoord landingHex)
        {
            landingHex = default;
            if (map == null || owner == null)
                return false;
            int nextTurnMovement = aircraft.Min(AviationRules.EffectiveMoveMax);

            HexCoord? best = null;
            int bestForward = int.MaxValue;
            int bestCost = int.MaxValue;
            foreach (HexCoord landing in OwnedAirfieldHexes(owner))
            {
                int freeSlots = FreeLandingCapacity(landing, owner, excludingFromCapacity);
                if (vacatingAtStart > 0 && landing.Equals(vacatingHex))
                    freeSlots += vacatingAtStart;
                if (freeSlots < aircraft.Count)
                    continue;
                HexPath path = HexPathfinder.FindPath(map, currentHex, landing, flatCost: true);
                if (path == null)
                    continue;
                int cost = path.Hexes.Count - 1; // flat 1 MP/hex, same rule AviationRules.PathMoveCost applies
                if (cost > nextTurnMovement)
                    continue;
                if (KnownAaExposure(owner, path) > 0)
                    continue;

                int forward = NearestKnownEnemyDistance(owner, landing);
                bool better = best == null || forward < bestForward || (forward == bestForward && cost < bestCost);
                if (better)
                {
                    best = landing;
                    bestForward = forward;
                    bestCost = cost;
                }
            }
            if (best == null)
                return false;
            landingHex = best.Value;
            return true;
        }

        // The multi-turn analogue of TryReplan — an emergency (or merely "no same-turn route
        // exists any more") return-to-base search for an army with a genuine safe-unlanded-ends
        // margin left. Returns null the instant that margin is already zero — a fuel-exhausted
        // group has no multi-turn safety net at all, TryReplan's own single-turn search (or holding
        // position) is the only honest option left for it, exactly as before this feature existed.
        //
        // AA handling softened to match TryReplan below (2026-08-26 P0 fix) — see that method's own
        // comment for the full rationale. Only exposure a candidate route adds BEYOND
        // KnownAaExposureAt(current hex) can disqualify or rank it down; exposure the army is
        // already standing in is never held against any route, since every route starts there.
        public static MultiTurnSortie? TryReplanMultiTurnReturn(ArmyData airArmy, HexMap map, PlayerSetupData owner)
        {
            if (!AviationRules.IsValidAirArmy(airArmy) || map == null)
                return null;
            int safeRemaining = SafeUnlandedEndsRemaining(airArmy.Members);
            if (safeRemaining <= 0)
                return null;
            int baselineExposure = KnownAaExposureAt(owner, airArmy.Hex);

            MultiTurnSortie? best = null;
            int bestTurns = int.MaxValue;
            int bestExposure = int.MaxValue;
            int bestCost = int.MaxValue;
            int bestForward = int.MaxValue;
            foreach (HexCoord landing in OwnedAirfieldHexes(owner))
            {
                if (FreeLandingCapacity(landing, owner, airArmy) < airArmy.Members.Count)
                    continue;
                HexPath path = HexPathfinder.FindPath(map, airArmy.Hex, landing, flatCost: true);
                if (path == null)
                    continue;
                int extraExposure = Mathf.Max(0, KnownAaExposure(owner, path) - baselineExposure);

                if (!TrySimulateHexSequence(path.Hexes, 0, airArmy.CurrentMovement, airArmy.Members, safeRemaining, owner,
                    out int requiredTurns, out int requiredUnlandedEnds, out HexCoord turn1Destination, out _, out bool landsThisTurn))
                    continue;

                int cost = AviationRules.PathMoveCost(airArmy, path);
                int forward = NearestKnownEnemyDistance(owner, landing);
                bool better = best == null || requiredTurns < bestTurns
                    || (requiredTurns == bestTurns && extraExposure < bestExposure)
                    || (requiredTurns == bestTurns && extraExposure == bestExposure && cost < bestCost)
                    || (requiredTurns == bestTurns && extraExposure == bestExposure && cost == bestCost && forward < bestForward);
                if (better)
                {
                    best = new MultiTurnSortie(airArmy.Hex, landing, null, path, cost, requiredTurns,
                        requiredUnlandedEnds, turn1Destination, false, landsThisTurn);
                    bestTurns = requiredTurns;
                    bestExposure = extraExposure;
                    bestCost = cost;
                    bestForward = forward;
                }
            }
            return best;
        }

        private static IReadOnlyList<HexCoord> CombineRoute(HexPath outbound, HexPath returnPath)
        {
            var hexes = new List<HexCoord>(outbound.Hexes);
            hexes.AddRange(returnPath.Hexes.Skip(1));
            return hexes;
        }

        // Turn-by-turn feasibility core shared by PlanMultiTurnSortieCore (a two-leg start->action->
        // landing route) and TryReplanMultiTurnReturn (a single landing-only leg, actionIndex 0 —
        // "the action already happened, everything from here on is the return"). Walks the route
        // hex-by-hex, spending firstTurnMovement this turn and each aircraft's own fresh
        // EffectiveMoveMax (min across the group) every turn after — mirrors ArmyData.CurrentMovement/
        // MaxMovement's own "min across members" rule (spec point 2/14), never a per-member split.
        // A turn-end hex that IS an owned airfield fully resets the simulated fuel counter (matching
        // AviationRules.ResetAfterLanding — landing anywhere owned refuels, not just the original
        // launch field); any other turn-end increments it, and the route is rejected the instant that
        // running count would exceed safeRemaining — exactly the live game's own
        // "ConsecutiveUnlandedEnds > TurnsWithoutRefuel" damage rule (AviationTurnLifecycle.
        // ResolveEndOfTurn), simulated here as a pure query, never touching real unit state (spec
        // point 15 — "не менять fuel-damage механику").
        private static bool TrySimulateHexSequence(IReadOnlyList<HexCoord> hexes, int actionIndex, int firstTurnMovement,
            IReadOnlyList<UnitData> aircraft, int safeRemaining, PlayerSetupData owner,
            out int requiredTurns, out int requiredUnlandedEnds, out HexCoord turn1Destination,
            out bool reachesActionThisTurn, out bool landsThisTurn)
        {
            requiredTurns = 0;
            requiredUnlandedEnds = 0;
            turn1Destination = hexes.Count > 0 ? hexes[0] : default;
            reachesActionThisTurn = actionIndex <= 0;
            landsThisTurn = false;

            int lastIndex = hexes.Count - 1;
            if (lastIndex <= 0)
            {
                landsThisTurn = true;
                return true;
            }

            int idx = 0;
            int used = 0;
            int turnMovement = firstTurnMovement;
            int maxEffective = aircraft.Min(AviationRules.EffectiveMoveMax);
            bool first = true;

            while (idx < lastIndex)
            {
                if (turnMovement <= 0)
                    return false; // no movement at all this turn and the route isn't finished yet
                idx = Mathf.Min(lastIndex, idx + turnMovement);
                requiredTurns++;
                HexCoord stop = hexes[idx];
                if (first)
                {
                    turn1Destination = stop;
                    reachesActionThisTurn = idx >= actionIndex;
                    first = false;
                }
                if (idx == lastIndex || AviationRules.IsOwnedAirfieldAt(stop, owner))
                {
                    used = 0;
                }
                else
                {
                    used++;
                    if (used > safeRemaining)
                        return false;
                    requiredUnlandedEnds = Mathf.Max(requiredUnlandedEnds, used);
                }
                turnMovement = maxEffective;
            }
            landsThisTurn = requiredTurns == 1;
            return true;
        }

        // The "plan became invalid" fallback (target disappeared, landing base captured/destroyed/
        // full, path became impossible, or the army lost effective MP) — prefers a newly reachable
        // OWNED airfield over giving up outright. Tries the army's own CURRENT hex as the "action
        // hex" (i.e. "can I still just fly straight home from here") first since that's always the
        // cheapest possible sortie, then falls back to searching every owned airfield directly.
        // Null means nothing is reachable THIS turn — callers must stop proposing voluntary
        // aviation movement rather than strand the aircraft on a doomed order (per spec).
        //
        // Ranking rewritten 2026-08-26 (project owner's own spec item 2 — "заново выбирать лучший
        // достижимый аэродром... приоритет: достижимость и безопасность посадки, меньшая дистанция
        // до текущей позиции, полезность как передовой база"), then AA handling hardened the same
        // day in a follow-up spec (item 1 — "ПВО единым жёстким фильтром... не трактовать ПВО как
        // простой штраф") — and then softened again the same day once that hard filter turned out
        // to have its own P0 bug (project owner's own report): a strike that discovers AA only on
        // ARRIVAL at its target had every route home rejected forever after, since the just-
        // revealed sighting sits within raidThreatRadius of the army's own current hex and so
        // covers literally every candidate path, safe ones included — ContinueSortie logged
        // "no reachable owned airfield" every single step and the aircraft never moved again.
        // TryReplan is ONLY ever called from ContinueSortie's own two "heading home" branches (see
        // that method) — never from the voluntary launch/outbound path, which keeps its own
        // separate absolute AA-free hard filter in PlanSortieCore/TryPlanSortiePreferForwardLanding
        // untouched. So here, exposure already unavoidable from the army's CURRENT hex
        // (KnownAaExposureAt) is no longer held against any candidate — every route necessarily
        // starts inside it, so it was never an avoidable choice. Only exposure a route adds BEYOND
        // that baseline still ranks it down (fewest-extra-exposure first), then shorter path cost,
        // then forward usefulness — never an outright rejection, so a genuinely reachable airfield
        // (capacity/movement permitting) always wins over holding position. Null still means no
        // owned airfield is reachable at all this turn (capacity/movement), never "reachable but
        // through AA."
        public static HexCoord? TryReplan(ArmyData airArmy, HexMap map, PlayerSetupData owner)
        {
            if (!AviationRules.IsValidAirArmy(airArmy) || map == null)
                return null;
            int baselineExposure = KnownAaExposureAt(owner, airArmy.Hex);

            HexCoord? best = null;
            int bestExposure = int.MaxValue;
            int bestCost = int.MaxValue;
            int bestForward = int.MaxValue;
            foreach (HexCoord landing in OwnedAirfieldHexes(owner))
            {
                if (FreeLandingCapacity(landing, owner, airArmy) < airArmy.Members.Count)
                    continue;
                HexPath path = HexPathfinder.FindPath(map, airArmy.Hex, landing, flatCost: true);
                if (path == null)
                    continue;
                int cost = AviationRules.PathMoveCost(airArmy, path);
                if (cost > airArmy.CurrentMovement)
                    continue;
                int extraExposure = Mathf.Max(0, KnownAaExposure(owner, path) - baselineExposure);

                int forward = NearestKnownEnemyDistance(owner, landing);
                bool better = best == null || extraExposure < bestExposure
                    || (extraExposure == bestExposure && cost < bestCost)
                    || (extraExposure == bestExposure && cost == bestCost && forward < bestForward);
                if (better)
                {
                    best = landing;
                    bestExposure = extraExposure;
                    bestCost = cost;
                    bestForward = forward;
                }
            }
            return best;
        }

        // Same reachability math TryPlanSortie/PlanSortieCore already apply for an already-airborne
        // army (full round trip current hex -> actionHex -> a landing hex, all within
        // airArmy.CurrentMovement) — used ONLY by ContinueSortie's own outbound-leg re-evaluation
        // (2026-08-26, project owner's own spec item 2), which used to pin the search to the task's
        // own already-chosen LandingHex and only widen to a full search once that one specific hex
        // stopped working. Now every owned airfield is re-considered fresh on every step, so a
        // safer/more-forward base can win at any point during the outbound leg, not just once the
        // original choice breaks outright.
        //
        // Priority order corrected 2026-08-26 (project owner's own follow-up spec, item 1+3 — the
        // old order was "ПВО → близость к текущей позиции → передовость", which meant a nearby
        // REARWARD base almost always beat a genuinely useful forward one on the middle tier before
        // forwardness ever got a say). Now: (1) known-AA route exposure is a hard filter, not a
        // ranking tier at all — a landing whose route (either leg) carries ANY exposure is dropped
        // outright whenever an AA-free candidate also completes the round trip, never merely ranked
        // behind one (per spec's own "не трактовать ПВО как простой штраф"); (2) among the AA-free
        // survivors, more useful as a forward base (NearestKnownEnemyDistance, shared with
        // TryReplan's own tie-break so "more forward" always means the same thing everywhere) now
        // outranks (3) lower total round-trip cost — the old straight-line "distance from current
        // position" tie-break is gone entirely, folded into this same final cost tier via the
        // route's own real totalCost, which already measures it more precisely. Returns null
        // whenever no AA-free owned airfield offers a real round trip at all — the caller's own
        // existing TryReplan fallback (abandon the target, fly straight home) already covers that
        // per spec's "если не может закончить... идти к ближайшему безопасному аэродрому" — no
        // separate handling needed here.
        public static Sortie? TryPlanSortiePreferForwardLanding(ArmyData airArmy, HexCoord actionHex, HexMap map, PlayerSetupData owner)
        {
            if (!AviationRules.IsValidAirArmy(airArmy) || airArmy.Owner != owner || map == null)
                return null;

            HexPath outbound = HexPathfinder.FindPath(map, airArmy.Hex, actionHex, flatCost: true);
            if (outbound == null)
                return null;
            int outboundCost = AviationRules.PathMoveCost(airArmy, outbound);
            int outboundExposure = KnownAaExposure(owner, outbound);
            int movement = airArmy.CurrentMovement;

            Sortie? best = null;
            int bestForward = int.MaxValue;
            int bestCost = int.MaxValue;
            foreach (HexCoord landing in OwnedAirfieldHexes(owner))
            {
                if (FreeLandingCapacity(landing, owner, airArmy) < airArmy.Members.Count)
                    continue;
                HexPath ret = HexPathfinder.FindPath(map, actionHex, landing, flatCost: true);
                if (ret == null)
                    continue;
                int totalCost = outboundCost + AviationRules.PathMoveCost(airArmy, ret);
                if (totalCost > movement)
                    continue; // not a real, complete round trip from here — never a candidate

                if (outboundExposure + KnownAaExposure(owner, ret) > 0)
                    continue; // known AA on this route — never a candidate while a safe one might exist

                int forward = NearestKnownEnemyDistance(owner, landing);
                bool better = best == null || forward < bestForward
                    || (forward == bestForward && totalCost < bestCost);
                if (better)
                {
                    best = new Sortie(actionHex, landing, outbound, ret, totalCost);
                    bestForward = forward;
                    bestCost = totalCost;
                }
            }
            return best;
        }

        // How close `hex` is to the nearest known enemy reference — the enemy citadel if known,
        // else the nearest known enemy army sighting, whichever is closer (int.MaxValue if neither
        // is known at all). Shared "how forward is this base" yardstick for TryReplan/
        // TryPlanSortiePreferForwardLanding's own tie-break (2026-08-26, spec item 2) AND
        // AirReconTask.FindReconHex's own forward-landing scoring bonus (spec item 3) — one place so
        // the two "which base counts as more forward" reads can never quietly disagree.
        public static int NearestKnownEnemyDistance(PlayerSetupData owner, HexCoord hex)
        {
            int best = int.MaxValue;
            foreach (AiMapMemory.KnownBuilding building in AiMapMemory.AllKnownBuildings(owner))
                if (building.IsStartingCitadel && building.Owner != null && building.Owner != owner && !building.Owner.IsNeutral)
                    best = Mathf.Min(best, HexGridMath.Distance(hex, building.Hex));
            foreach (AiMapMemory.KnownEnemySighting sighting in AiMapMemory.AllKnownEnemySightings(owner))
                best = Mathf.Min(best, HexGridMath.Distance(hex, sighting.Hex));
            return best;
        }

        // Shared continuation logic for BOTH AirStrike and AirRecon — identical shape for either
        // Kind (advance the outbound leg, flip to the return leg once the objective hex is reached,
        // advance the return leg, complete on landing), so this exists exactly once instead of once
        // per category, per the spec's own "small shared AI aviation helper if it prevents
        // duplicate route/capacity logic" ask. AiAggressionPlanner.TryContinueAirStrikeTask/
        // AiScoutPlanner.TryContinueAirReconTask are both thin wrappers around this.
        //
        // Re-validates the plan fresh every call (per spec: must recheck before it launches OR
        // moves) — 2026-08-26 rewrite (project owner's own spec item 2): the outbound leg no longer
        // preferentially sticks to the task's own already-chosen LandingHex, it re-searches every
        // owned airfield fresh via TryPlanSortiePreferForwardLanding on every single step, the same
        // "never held onto, always re-derived" treatment the return leg's own TryReplan call already
        // got. Both legs hard-filter to known-AA-free candidates first (2026-08-26 follow-up spec,
        // item 1); the outbound leg then prefers forward usefulness over cost (TryPlanSortie
        // PreferForwardLanding's own comment, spec item 3 — it's actively choosing where to base
        // next), while the return leg still prefers lower cost over forward usefulness (TryReplan's
        // own comment — it's just going home). Whenever NEITHER leg can find a complete safe round
        // trip any more, the existing "turn for home NOW, abandon further progress toward the
        // target" fallback below already does exactly what the spec asks ("если не может закончить
        // текущий или следующий ход на безопасном
        // аэродроме... идти к ближайшему безопасному аэродрому") — nothing about that fallback
        // shape needed to change, only which landing hex wins when a plan DOES still exist. Returns
        // null (propose nothing this step) whenever nothing reachable exists at all — callers must
        // never strand the aircraft on a doomed order, per spec.
        public static AiDecision ContinueSortie(PlayerSetupData player, PlayerRoot root, AiTurnContext ctx, AiTask task,
            string logLabel, string outboundReason, float continuationScore, AiTaskCategory category)
        {
            if (task.Army?.Controller == null || !ArmyRegistry.AllForOwner(player).Contains(task.Army) || !AviationRules.IsAirArmy(task.Army))
            {
                // Releases whatever launch-Energy reservation (see AiAviationSupport.LaunchRoutine)
                // this task may still be holding — safe even if the army already moved and the
                // reservation was already released there (Release is a no-op on an unknown task).
                AiResourceReservation.Release(task);
                AiTaskRegistry.Remove(player, task);
                return null;
            }
            if (task.Army.Members.Count == 0)
            {
                // AA destroyed the whole sortie — ordinary empty-army cleanup applies (see
                // AiTurnController.RunEmptyArmyCleanup), nothing left here to fly.
                AiResourceReservation.Release(task);
                AiTaskRegistry.Remove(player, task);
                return null;
            }

            // Anti-loop memory (project owner's own spec — "AirRecon не должен бесконечно летать в
            // один stale-гекс"): once a recon sortie is underway toward a hex, stamp it so
            // AirReconTask.FindReconHex won't send another sortie to the same hex for
            // AiConfig.airReconTargetCooldownTurns turns after this one ends (unless live enemy
            // intel turns up on it). Re-stamped every outbound step — including the step that
            // reaches it — so the cooldown counts from the sortie's last real progress. Recon only:
            // AirStrike has its own targeting and no such loop to guard against.
            if (category == AiTaskCategory.Reconnaissance && task.AirOutbound)
                AiMapMemory.RecordAirReconTarget(player, task.TargetHex, ctx.TurnNumber);

            // Outbound leg finished the moment the army reaches the objective — the strike itself
            // (if the target was still there) or the recon reveal already happened as a side effect
            // of the MoveArmy step that landed the army on this hex (AviationCombatPresenter.
            // ResolveStep). Turn for home.
            if (task.AirOutbound && task.Army.Hex.Equals(task.TargetHex))
            {
                task.AirOutbound = false;
                task.TargetHex = task.LandingHex;
            }

            // Return leg finished — the sortie is over.
            if (!task.AirOutbound && task.Army.Hex.Equals(task.TargetHex))
            {
                AiResourceReservation.Release(task);
                AiTaskRegistry.Remove(player, task);
                return null;
            }

            HexCoord destination;
            if (task.AirOutbound)
            {
                Sortie? sortie = TryPlanSortiePreferForwardLanding(task.Army, task.TargetHex, ctx.Map, player);
                if (sortie.HasValue)
                {
                    task.LandingHex = sortie.Value.LandingHex;
                    task.IsMultiTurnSortie = false;
                    destination = task.TargetHex;
                }
                else
                {
                    // No same-turn round trip exists any more — before giving up on the target
                    // outright, check whether this group still has enough safe-unlanded-ends margin
                    // (SafeUnlandedEndsRemaining) to reach it and return over several turns instead
                    // (multi-turn aviation spec, point 10: AiAggressionPlanner/AiScoutPlanner decide
                    // to START a multi-turn sortie, but CONTINUING one re-derives the exact same
                    // full-round-trip proof every step here, never trusting a plan made turns ago).
                    MultiTurnSortie? multi = TryPlanMultiTurnSortie(task.Army, task.TargetHex, ctx.Map, player);
                    if (multi.HasValue)
                    {
                        task.LandingHex = multi.Value.LandingHex;
                        task.IsMultiTurnSortie = true;
                        destination = task.TargetHex;
                        LogMultiTurnContinuation(player, task, logLabel, multi.Value, arrivingHome: false);
                    }
                    else
                    {
                        // Neither a same-turn nor a safe multi-turn round trip exists any more —
                        // abandon forward progress immediately and turn for home (spec point 10:
                        // "если уже нельзя гарантировать возвращение после атаки — немедленно
                        // отказаться от атаки и перейти к Returning").
                        HexCoord? fallback = TryReplan(task.Army, ctx.Map, player);
                        MultiTurnSortie? multiFallback = fallback == null ? TryReplanMultiTurnReturn(task.Army, ctx.Map, player) : null;
                        if (fallback == null && multiFallback == null)
                        {
                            AiDebugLog.Write($"[AI] {player.Nickname}: \"{task.Army.Name}\" — {logLabel} — no safe "
                                + "airfield reachable before fuel deadline; holds position.");
                            return null;
                        }
                        task.AirOutbound = false;
                        HexCoord home = fallback ?? multiFallback.Value.LandingHex;
                        task.IsMultiTurnSortie = fallback == null;
                        task.LandingHex = home;
                        task.TargetHex = home;
                        destination = home;
                        AiDebugLog.Write($"[AI] {player.Nickname}: \"{task.Army.Name}\" — {logLabel} aborted — target "
                            + "plus safe return no longer fits remaining fuel; returning to nearest safe airfield "
                            + $"({home.Q},{home.R})" + (multiFallback.HasValue ? $" over {multiFallback.Value.RequiredTurns} turn(s)." : "."));
                    }
                }
            }
            else
            {
                HexCoord? confirmedLanding = TryReplan(task.Army, ctx.Map, player);
                if (confirmedLanding != null)
                {
                    task.LandingHex = confirmedLanding.Value;
                    task.TargetHex = confirmedLanding.Value;
                    task.IsMultiTurnSortie = false;
                    destination = confirmedLanding.Value;
                }
                else
                {
                    MultiTurnSortie? multiReturn = TryReplanMultiTurnReturn(task.Army, ctx.Map, player);
                    if (!multiReturn.HasValue)
                    {
                        AiDebugLog.Write($"[AI] {player.Nickname}: \"{task.Army.Name}\" — {logLabel} has no reachable owned "
                            + "airfield this turn, holding position.");
                        return null;
                    }
                    task.LandingHex = multiReturn.Value.LandingHex;
                    task.TargetHex = multiReturn.Value.LandingHex;
                    task.IsMultiTurnSortie = true;
                    destination = multiReturn.Value.LandingHex;
                    LogMultiTurnContinuation(player, task, logLabel, multiReturn.Value, arrivingHome: true);
                }
            }

            if (!AiTurnController.CanIssueMoveNow(root, player, task.Army, ctx.Map, destination, task))
                return null;
            HexCoord? nextStep = AiTurnController.FindAffordableStep(ctx.Map, task.Army, destination);
            if (nextStep == null)
                return null;

            string reason = task.AirOutbound ? outboundReason : "returns to land";
            return AiDecision.Move(task.Army, nextStep.Value, reason, task, continuationScore, category);
        }

        // Multi-turn diagnostic line (spec point 12/16) — reports the group's own live
        // SafeUnlandedEndsRemaining against what this specific plan still needs, so a playtester can
        // see exactly when the next turn's landing stops being optional. `arrivingHome` only changes
        // the wording (still pressing toward the objective vs. already turned for home).
        private static void LogMultiTurnContinuation(PlayerSetupData player, AiTask task, string logLabel,
            MultiTurnSortie multi, bool arrivingHome)
        {
            int safeNow = SafeUnlandedEndsRemaining(task.Army.Members);
            int remainingAfter = Mathf.Max(0, safeNow - multi.RequiredUnlandedEnds);
            string deadline = remainingAfter <= 0
                ? "next turn must land"
                : $"{remainingAfter} safe unlanded end(s) remain";
            string progress = arrivingHome
                ? $"returns over {multi.RequiredTurns} more turn(s)"
                : (multi.ReachesActionThisTurn ? "action reached this turn" : $"{multi.RequiredTurns}-turn route continues");
            AiDebugLog.Write($"[AI] {player.Nickname}: \"{task.Army.Name}\" — {logLabel} {progress}, "
                + $"{multi.RequiredUnlandedEnds} safe unlanded end(s) required — {deadline}; landing "
                + $"({multi.LandingHex.Q},{multi.LandingHex.R}).");
        }

        // Launch-affordability pre-check for a still-STORED group of aircraft (no ArmyData exists
        // yet to read ActivationApCost/ActivationEnergyCost off — see ArmyData's own comment on
        // where those numbers come from for an already-formed air army). Mirrors that same
        // computation over the specific UnitData subset a candidate wants to launch, and reads
        // Energy through AiResourceReservation.Available (never root.GetResource directly) for the
        // exact reason every other AI spend check in this codebase already does — must not
        // double-count what an active task has already claimed.
        public static bool CanAffordLaunch(PlayerRoot root, PlayerSetupData player, IReadOnlyList<UnitData> aircraft)
        {
            if (root == null || aircraft == null || aircraft.Count == 0)
                return false;
            int apCost = aircraft.Sum(u => u.ActivationApCost);
            int energyCost = aircraft.Sum(u => u.LaunchEnergyCost);
            return root.CanSpendActionPoints(apCost)
                && AiResourceReservation.Available(root, player, ResourceType.Energy) >= energyCost;
        }

        // Shared execution for LaunchAirStrike/LaunchAirRecon (see AiTurnController.
        // PerformDecision's own dispatch switch) — the one Kind pair genuine MoveArmy can't express
        // (converting a still-stored aircraft group into a real flying ArmyData, via
        // AviationActions.TryLaunch — the exact same shared API a human's own launch button calls).
        // Forming the stack itself is free (see AviationActions.TryLaunch's own comment — "forming a
        // stack is not a take-off"); the real AP/Energy launch cost is still charged the ordinary
        // way, by this new army's own first MoveArmy activation, whenever that step actually comes
        // up (possibly several Decide() steps or even turns later — see AiTurnController.RunTurn's
        // own one-decision-per-step loop). The Energy portion of that future cost IS reserved here,
        // though (2026-08-26 P1 fix, project owner's own report) via the AiResourceReservation.TopUp
        // call below — otherwise a higher-scoring candidate on some later step could spend it out
        // from under this army before its own first move ever gets a turn, and the safe-return
        // invariant every AirStrike/AirRecon sortie is supposed to guarantee would already be
        // broken by the time CanIssueMoveNow finally catches the shortfall. Registers the fresh
        // AiTask itself once the army actually exists — Commit never does this for a Launch*
        // decision (decision.Task is deliberately left null by the factories; there is no task, and
        // nothing to claim, until the launch actually succeeds).
        public static IEnumerator LaunchRoutine(PlayerSetupData player, AiDecision decision, AiTurnContext ctx, AiTaskKind taskKind)
        {
            ArmyData airArmy = decision.ExistingArmy;
            if (airArmy == null)
            {
                ArmyData airfield = AviationRules.FindAirfieldAt(decision.TargetHex, player);
                if (airfield == null || decision.AircraftToLaunch == null || decision.AircraftToLaunch.Count == 0)
                {
                    AiDebugLog.Write($"[AI] {player.Nickname}: {taskKind} launch failed — no airfield/aircraft at "
                        + $"({decision.TargetHex.Q},{decision.TargetHex.R}).");
                    yield break;
                }
                bool launched = AviationActions.TryLaunch(airfield, decision.AircraftToLaunch.ToList(),
                    ctx.StartingDeckCatalog?.GetCatalog(player.Faction), ctx.HexSelection, out airArmy, out string failReason);
                if (!launched || airArmy == null)
                {
                    AiDebugLog.Write($"[AI] {player.Nickname}: {taskKind} launch failed — {failReason}");
                    yield break;
                }
                AiDebugLog.Write($"[AI] {player.Nickname}: launches \"{airArmy.Name}\" ({decision.AircraftToLaunch.Count} aircraft) "
                    + $"from ({decision.TargetHex.Q},{decision.TargetHex.R}) — {decision.Reason}.");
            }

            var task = new AiTask
            {
                Kind = taskKind, Army = airArmy, TargetHex = decision.AirActionHex, LandingHex = decision.AirLandingHex, AirOutbound = true,
            };
            AiTaskRegistry.Add(player, task);

            // Reserve this army's own first-move ActivationEnergyCost the instant the task exists
            // (2026-08-26 P1 fix, project owner's own report) — CanAffordLaunch above only checked
            // it was available a moment ago, at candidate time; without a real reservation here, a
            // DIFFERENT higher-scoring task could spend that same Energy before this army's own
            // first MoveArmy step ever gets a turn to claim it (Decide only ever commits ONE
            // decision per step — see AiTurnController.RunTurn's own per-step loop — so a freshly
            // formed air army can easily sit un-activated for one or more further steps, even whole
            // turns, before ContinueSortie's own CanIssueMoveNow check ever runs again). Released
            // the moment that first move actually executes for real — see
            // AiTurnController.MoveArmyRoutine's own Release call — never held past that, unlike
            // BuildFacility/BuildBase's own multi-turn trickle reservation.
            PlayerRoot root = PlayerRootRegistry.FindFor(player);
            AiResourceReservation.TopUp(root, player, task, new ResourceCost { energy = airArmy.ActivationEnergyCost });
            AiDebugLog.Write($"[AI] {player.Nickname}: \"{airArmy.Name}\" assigned {taskKind} — target "
                + $"({decision.AirActionHex.Q},{decision.AirActionHex.R}), landing ({decision.AirLandingHex.Q},{decision.AirLandingHex.R}).");

            // Launch and the sortie's first real step are now one indivisible sequence (2026-08-26
            // P1 fix, project owner's own report) — RunTurn's own one-decision-per-step loop used
            // to leave it here: task registered, Energy reserved, but the army itself still just
            // sitting on the map unactivated until ContinueSortie happened to win arbitration on
            // some LATER Decide() step. A different, higher-scoring candidate could spend AP in
            // between, or a route/landing-slot could stop being safe, and the aircraft would be
            // stranded already airborne with no step left to move it. Driving ContinueSortie
            // synchronously right here — the exact same recheck of route/AP/Energy/landing-slot
            // every later step already goes through, this task's own reservation excluded via
            // CanIssueMoveNow's own reservationOwner param — closes that gap: by the time this
            // coroutine yields back to RunTurn's own step loop, the army has either taken its
            // first real step for real, or never left storage at all.
            string logLabel = taskKind == AiTaskKind.AirStrike ? "AirStrike" : "AirRecon";
            string outboundReason = taskKind == AiTaskKind.AirStrike
                ? "presses on toward the strike target" : "flies on toward the recon target";
            AiTaskCategory category = taskKind == AiTaskKind.AirStrike ? AiTaskCategory.Aggression : AiTaskCategory.Reconnaissance;
            AiDecision firstMove = ContinueSortie(player, root, ctx, task, logLabel, outboundReason,
                AiConfig.airStrikeContinuationScore, category);
            if (firstMove == null)
            {
                // Can't even take the first step this turn — never leave the group formed and
                // airborne with nothing able to move it: undo the launch and return every aircraft
                // to the airfield's own stored container, right where they started this step.
                AiDebugLog.Write($"[AI] {player.Nickname}: \"{airArmy.Name}\" — {taskKind} has no viable first step "
                    + "this turn, cancels the launch and returns aircraft to storage.");
                AiResourceReservation.Release(task);
                AiTaskRegistry.Remove(player, task);
                ArmyData homeAirfield = AviationActions.EnsureAirfield(ctx.HexSelection, player, airArmy.Hex);
                foreach (UnitData aircraft in airArmy.Members.ToList())
                {
                    airArmy.Members.Remove(aircraft);
                    homeAirfield?.AddMemberSorted(aircraft);
                }
                ctx.HexSelection?.DeleteArmyIfEmptied(airArmy);
                ctx.HexSelection?.RestackArmiesOn(airArmy.Hex, null);
                yield return AiTurnController.WaitStep(ctx);
                yield break;
            }
            yield return AiTurnController.MoveArmyRoutine(player, firstMove, ctx);
        }
    }
}
