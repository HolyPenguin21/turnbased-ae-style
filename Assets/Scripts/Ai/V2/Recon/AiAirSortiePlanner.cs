using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Game.Ai;
using Game.Aviation;
using Game.Combat;
using Game.Cards;
using Game.Economy;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Units;
using UnityEngine;

namespace Game.Ai.V2
{
    // Strategy V2's aviation planner + executor: known-AA-aware route choice, reservation-aware
    // launch affordability, landing-slot accounting (over AirSortieRegistry), target/landing
    // selection, and the LaunchRoutine / ContinueSortie coroutines that drive an air sortie
    // through AiTurnController.MoveArmyRoutine. The physical half it builds on — airfield
    // capacity/range, plan data (Sortie / MultiTurnSortie), pure route-feasibility simulation —
    // lives in Game.Aviation (AviationRules, AviationSortiePlan / AviationRange). Was the
    // namespace-neutral Game.Aviation.AviationSupport; moved here (ARCH-01) because AA memory,
    // map knowledge, AiConfig radii and resource reservations make it AI planning, not domain.
    public static class AiAirSortiePlanner
    {
        public readonly struct RebaseRoute
        {
            public readonly HexCoord LandingHex;
            public readonly HexCoord CurrentTurnDestination;
            public readonly int TotalCost;
            public readonly int RequiredTurns;

            public RebaseRoute(HexCoord landingHex, HexCoord currentTurnDestination,
                int totalCost, int requiredTurns)
            {
                LandingHex = landingHex;
                CurrentTurnDestination = currentTurnDestination;
                TotalCost = totalCost;
                RequiredTurns = requiredTurns;
            }
        }

        // Every one of this player's own owned, airfield-CAPABLE hexes (citadel + every later
        // Base) — selected from the building registry by the shared AirfieldCapacity rule, not
        // indirectly through Barracks/garrison presence. "Any owned airfield with free capacity"
        // is always filtered from this set, never hard-coded to one building name.
        public static IEnumerable<HexCoord> OwnedAirfieldHexes(PlayerSetupData player) =>
            BuildingRegistry.AllBuildings()
                .Where(building => AviationRules.IsAirfieldBuilding(building, player))
                .OrderByDescending(building => building.IsStartingCitadel)
                .ThenBy(building => building.Hex.Q)
                .ThenBy(building => building.Hex.R)
                .Select(building => building.Hex)
                .Distinct();

        // Coarse route-risk read — every known-AA-tagged enemy sighting
        // (AiMapMemory.KnownEnemySighting.HasAntiAir, see that field's own comment) within
        // raidThreatRadius of ANY hex the given leg crosses. Deliberately approximate (no per-unit
        // AA radius is kept in memory, only the bool flag) — good enough to rank routes relative to
        // each other, never meant as an exact prediction of what will actually react (that stays
        // AntiAirRules' own live, honest-fog job at execution time). The one shared route-risk
        // number for every sortie kind.
        public static int KnownAaExposure(PlayerSetupData actor, HexPath leg) =>
            leg == null ? 0 : KnownAaExposureOver(actor, leg.Hexes);

        // Exposure already unavoidable given where the army is standing RIGHT NOW — e.g. AA that
        // was only revealed once the strike itself landed on this hex. Used exclusively by the
        // emergency-return searches below (TryReplan/TryReplanMultiTurnReturn) as a baseline: a
        // route home necessarily starts inside whatever already covers the current hex, so that
        // part of its exposure was never an avoidable choice and must not disqualify the route the
        // way a genuinely NEW zone should — otherwise an aircraft that discovers AA only on arrival
        // would have every route home rejected.
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
        // capacity-checks the STORED container (new card deployment, see
        // AviationRules.FreeAirfieldCapacity/ArmyActions.DeployUnitFromCard) — a landed,
        // already-launched air army is a separate ArmyData the move layer never caps. This is
        // deliberately MORE conservative than the engine: it also counts every other already-landed
        // air army's aircraft against the same capacity, so the AI never voluntarily stacks more
        // aircraft onto one airfield hex than its stated capacity. It also subtracts every OTHER
        // active sortie's claim on this landing hex via ReservedLandingSlots below — an in-flight
        // sortie is as real a claim on its slot as an aircraft already sitting there.
        //
        // `excluding` — the mover's own air army, so a sortie re-checking its ALREADY-chosen
        // landing hex mid-flight does not count itself against its own capacity (as a landed army
        // or, via its own AirSortie, as a reservation).
        public static int FreeLandingCapacity(HexCoord hex, PlayerSetupData owner, ArmyData excluding = null)
        {
            int capacity = AviationRules.AirfieldCapacityAt(hex, owner);
            if (capacity <= 0)
                return 0;
            int used = AviationRules.FindAirfieldAt(hex, owner)?.Members.Count ?? 0;
            foreach (ArmyData army in ArmyRegistry.AllAt(hex))
                if (army != excluding && army.Owner == owner && AviationRules.IsAirArmy(army))
                    used += army.Members.Count;
            AirSortie excludingTask = excluding != null ? AirSortieRegistry.ForArmy(owner, excluding) : null;
            used += ReservedLandingSlots(hex, owner, excludingTask);
            return Mathf.Max(0, capacity - used);
        }

        // How many of `hex`'s free slots are already spoken for by OTHER active Strike/Recon/Rebase
        // sorties committed to land there but not physically there yet (still outbound, or inbound
        // but not yet arrived — see AirSortie.LandingHex). Landed aircraft are NOT counted again
        // here — FreeLandingCapacity's own ArmyRegistry loop already counts anything physically
        // sitting on `hex`. `excludingTask` lets a sortie re-checking its OWN already-chosen
        // landing hex exclude its own prior claim, the same role FreeLandingCapacity's `excluding`
        // plays against the ArmyRegistry loop. Without this, two independently-launched groups
        // could both claim the same single free slot, since neither outbound flight is visible to
        // the other's capacity check until it lands.
        private static int ReservedLandingSlots(HexCoord hex, PlayerSetupData owner, AirSortie excludingTask)
        {
            int reserved = 0;
            foreach (AirSortie task in AirSortieRegistry.For(owner))
            {
                if (task == excludingTask)
                    continue;
                if (task.Army == null || !task.LandingHex.Equals(hex) || task.Army.Hex.Equals(hex))
                    continue;
                reserved += task.Army.Members.Count;
            }
            return reserved;
        }



        // AI-AIR-02 — can this airborne group END the current turn on the hex it is standing on
        // RIGHT NOW, taking NO further movement this turn, and still be guaranteed a legal recovery
        // to an owned airfield afterwards? This is the exact proof the recon executor's Hold /
        // don't-reserve-return decision needs.
        //
        // It deliberately does NOT use TryReplan / TryReplanMultiTurnReturn: both of those spend
        // THIS turn's remaining CurrentMovement toward home on their first simulated turn. A Hold
        // freezes the wing where it is, so that movement never happens — trusting their proof let
        // the wing choose Hold from a position it could only recover from by ALSO flying the MP it
        // then never flew (planning ↔ execution mismatch on the safe-return invariant).
        //
        //   CanEndTurnHereAndRecover =
        //       SafeUnlandedEndsRemaining >= 1          (this turn's airborne EndTurn is legal)
        //       AND from the CURRENT hex, a route to a capacity-OK, no-new-known-AA owned airfield
        //           lands within (SafeUnlandedEndsRemaining - 1) more unlanded turn-ends, each
        //           future turn simulated with the group's refreshed EffectiveMoveMax.
        //
        // A plane (SafeUnlandedEndsRemaining == 0) always fails the first clause, so its existing
        // single-turn boomerang model is completely untouched. Pure query — never mutates unit
        // state, re-derives everything from the live shared aviation rules.
        public static bool CanEndTurnHereAndRecover(ArmyData airArmy, HexMap map, PlayerSetupData owner)
        {
            if (!AviationRules.IsValidAirArmy(airArmy) || map == null || owner == null)
                return false;
            int safeEnds = AviationRange.SafeUnlandedEndsRemaining(airArmy);
            if (safeEnds < 1)
                return false; // ending this turn aloft is already illegal / would take fuel damage

            // This turn's hold consumes one unlanded end; the return itself gets what is left.
            int marginForReturn = safeEnds - 1;
            int freshMovement = airArmy.Members.Min(AviationRules.EffectiveMoveMax);
            if (freshMovement <= 0)
                return false;
            int baselineExposure = KnownAaExposureAt(owner, airArmy.Hex);

            foreach (HexCoord landing in OwnedAirfieldHexes(owner))
            {
                if (FreeLandingCapacity(landing, owner, airArmy) < airArmy.Members.Count)
                    continue;
                HexPath path = HexPathfinder.FindPath(map, airArmy.Hex, landing, flatCost: true);
                if (path == null)
                    continue;
                if (KnownAaExposure(owner, path) - baselineExposure > 0)
                    continue; // recovery route would cross NEW known AA — not a safe deliberate hold

                // Simulate the return starting NEXT turn from the current hex: firstTurnMovement is
                // the refreshed EffectiveMoveMax (not this turn's spent CurrentMovement), and only
                // marginForReturn further unlanded ends are allowed.
                if (AviationRange.TrySimulateHexSequence(path.Hexes, 0, freshMovement, airArmy.Members, marginForReturn,
                    owner, out _, out _, out _, out _, out _))
                    return true;
            }
            return false;
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

        // Exact owned-airfield-to-owned-airfield relocation. Unlike an ordinary sortie, the
        // requested destination is not re-ranked to a different landing field: the strategic
        // caller has already proved that this exact Base serves current objectives better. The
        // route still uses the canonical capacity, known-AA and fuel simulation gates.
        public static RebaseRoute? TryPlanRebaseFromStorage(HexCoord sourceHex,
            IReadOnlyList<UnitData> aircraft, HexCoord destinationHex, HexMap map,
            PlayerSetupData owner)
        {
            if (aircraft == null || aircraft.Count == 0 || sourceHex.Equals(destinationHex))
                return null;
            int movement = aircraft.Min(AviationRules.EffectiveMoveMax);
            return PlanExactRebase(sourceHex, null, aircraft, movement, aircraft.Count,
                destinationHex, map, owner, allowExistingExposure: false);
        }

        private static RebaseRoute? TryContinueExactRebase(ArmyData airArmy,
            HexCoord destinationHex, HexMap map, PlayerSetupData owner)
        {
            if (!AviationRules.IsValidAirArmy(airArmy) || airArmy.Owner != owner)
                return null;
            return PlanExactRebase(airArmy.Hex, airArmy, airArmy.Members,
                airArmy.CurrentMovement, airArmy.Members.Count, destinationHex, map, owner,
                allowExistingExposure: true);
        }

        private static RebaseRoute? PlanExactRebase(HexCoord startHex, ArmyData excluding,
            IReadOnlyList<UnitData> aircraft, int firstTurnMovement, int requiredSlots,
            HexCoord destinationHex, HexMap map, PlayerSetupData owner, bool allowExistingExposure)
        {
            if (map == null || owner == null || aircraft == null || aircraft.Count == 0
                || !AviationRules.IsOwnedAirfieldAt(destinationHex, owner)
                || FreeLandingCapacity(destinationHex, owner, excluding) < requiredSlots)
                return null;
            HexPath path = HexPathfinder.FindPath(map, startHex, destinationHex, flatCost: true);
            if (path == null)
                return null;
            int exposure = KnownAaExposure(owner, path);
            if (allowExistingExposure)
                exposure = Mathf.Max(0, exposure - KnownAaExposureAt(owner, startHex));
            if (!IsVoluntaryRebaseRouteSafe(exposure))
                return null;

            int cost = excluding != null
                ? AviationRules.PathMoveCost(excluding, path)
                : path.Hexes.Count - 1;
            if (cost <= firstTurnMovement)
                return new RebaseRoute(destinationHex, destinationHex, cost, 1);

            int safeRemaining = AviationRange.SafeUnlandedEndsRemaining(aircraft);
            if (safeRemaining <= 0
                || !AviationRange.TrySimulateHexSequence(path.Hexes, path.Hexes.Count - 1,
                    firstTurnMovement, aircraft, safeRemaining, owner, out int turns, out _,
                    out HexCoord firstDestination, out _, out _))
                return null;
            return new RebaseRoute(destinationHex, firstDestination, cost, turns);
        }

        internal static bool IsVoluntaryRebaseRouteSafe(int knownAaExposure) =>
            knownAaExposure <= 0;

        // requiredSlots: how many aircraft need a free landing slot together — the WHOLE group
        // lands as one stack, so a landing hex with fewer free slots than that is rejected
        // outright, not just "at least one".
        //
        // vacatingAtStart: for a still-STORED group (TryPlanSortieFromStorage), these exact
        // aircraft are counted in FindAirfieldAt(startHex)'s Members.Count right now even though
        // launching frees that many slots — so a fully-packed airfield can still plan a round-trip
        // sortie back to itself. Zero for an already-airborne army (TryPlanSortie/TryReplan) — it
        // was never part of any airfield's stored container.
        //
        // Landing choice: known AA is ONE hard filter, and every reachable owned airfield is
        // weighed by safety-then-forwardness-then-cost before the caller computes the target's own
        // score, so each candidate TARGET gets back its truly best target+landing pairing (not a
        // cheapest-path landing locked in early). A landing whose route (either leg) carries ANY
        // known AA exposure is dropped outright whenever the AA-free set is non-empty — never
        // merely ranked down — matching TryPlanSortiePreferForwardLanding below. Returns null when
        // no AA-free candidate reaches within the mover's movement budget this turn — callers must
        // not offer that target as a launch option; there is deliberately no "fly the unsafe route
        // anyway" fallback for a launch that has not happened yet.
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
            int safeRemaining = AviationRange.SafeUnlandedEndsRemaining(aircraft);
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

                if (!AviationRange.TrySimulateHexSequence(AviationRange.CombineRoute(outbound, ret), outbound.Hexes.Count - 1, firstTurnMovement,
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

        // Can this army, PARKED at currentHex (a repeat strike never moves the army, see
        // AviationCombatPresenter.ResolveAirStrikeAtCurrentHex), still reach a safe owned airfield
        // NEXT turn, once its movement refreshes? Uses each aircraft's fresh EffectiveMoveMax — the
        // movement restored next turn — never the army's current, already-spent CurrentMovement.
        // The repeat strike itself never costs MP (a strike never charges movement, see
        // AviationCombatPresenter.RunAirStrike), so no cost is deducted beyond the return path
        // itself. Same capacity/AA-hard-filter/forward-then-cost ranking as every other landing
        // search in this class.
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

        // Estimate-time overload for raid-support scoring — the launch candidate has not flown yet,
        // so there is no ArmyData to validate/exclude from landing capacity, only the raw aircraft
        // list a launch would use. Same rule otherwise; real eligibility is re-verified live once
        // the army is actually sitting on the hex (CanStrikeNextTurnAndLand(ArmyData, ...) above).
        //
        // launchAirfieldHex: these aircraft are still physically in THAT airfield's stored
        // container — the launch this estimate scores is what vacates their slots. Same
        // vacatingAtStart idea TryPlanSortieFromStorage/PlanMultiTurnSortieCore apply for the
        // outbound leg, here for the second-strike LANDING leg. Applies ONLY to that one airfield
        // hex.
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

        // The multi-turn analogue of TryReplan — an emergency (or merely "no same-turn route exists
        // any more") return-to-base search for an army with a genuine safe-unlanded-ends margin
        // left. Returns null the instant that margin is zero — a fuel-exhausted group has no
        // multi-turn safety net; TryReplan's single-turn search (or holding position) is the only
        // honest option left for it.
        //
        // AA handling matches TryReplan below: only exposure a candidate route adds BEYOND
        // KnownAaExposureAt(current hex) can disqualify or rank it down; exposure the army already
        // stands in is never held against any route, since every route starts there.
        public static MultiTurnSortie? TryReplanMultiTurnReturn(ArmyData airArmy, HexMap map, PlayerSetupData owner)
        {
            if (!AviationRules.IsValidAirArmy(airArmy) || map == null)
                return null;
            int safeRemaining = AviationRange.SafeUnlandedEndsRemaining(airArmy.Members);
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

                if (!AviationRange.TrySimulateHexSequence(path.Hexes, 0, airArmy.CurrentMovement, airArmy.Members, safeRemaining, owner,
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



        // The "plan became invalid" fallback (target disappeared, landing base
        // captured/destroyed/full, path became impossible, or the army lost effective MP) — prefers
        // a newly reachable OWNED airfield over giving up. Tries the army's own CURRENT hex as the
        // "action hex" ("can I still fly straight home from here") first since that is always the
        // cheapest sortie, then searches every owned airfield directly. Null means nothing is
        // reachable THIS turn — callers must stop proposing voluntary aviation movement rather than
        // strand the aircraft on a doomed order.
        //
        // TryReplan is ONLY called from ContinueSortie's two "heading home" branches — never from
        // the voluntary launch/outbound path, which keeps its own absolute AA-free hard filter in
        // PlanSortieCore/TryPlanSortiePreferForwardLanding. Here, exposure already unavoidable from
        // the army's CURRENT hex (KnownAaExposureAt) is not held against any candidate: a sighting
        // revealed on arrival covers every path home, and treating it as a hard filter would ground
        // the aircraft forever. Only exposure a route adds BEYOND that baseline ranks it down
        // (fewest-extra-exposure first), then shorter path cost, then forward usefulness — never an
        // outright rejection, so a reachable airfield (capacity/movement permitting) always wins
        // over holding position. Null means no owned airfield is reachable at all this turn
        // (capacity/movement), never "reachable but through AA".
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

        // Same reachability math TryPlanSortie/PlanSortieCore apply for an already-airborne army
        // (full round trip current hex -> actionHex -> a landing hex, all within
        // airArmy.CurrentMovement) — used ONLY by ContinueSortie's outbound-leg re-evaluation.
        // Every owned airfield is re-considered fresh on every step, so a safer/more-forward base
        // can win at any point during the outbound leg, not only once the original choice breaks.
        //
        // Priority: (1) known-AA route exposure is a hard filter, not a ranking tier — a landing
        // whose route (either leg) carries ANY exposure is dropped whenever an AA-free candidate
        // also completes the round trip; (2) among the AA-free survivors, more useful as a forward
        // base (NearestKnownEnemyDistance, shared with TryReplan's tie-break so "more forward"
        // means the same thing everywhere) outranks (3) lower total round-trip cost. Returns null
        // whenever no AA-free owned airfield offers a real round trip — the caller's TryReplan
        // fallback (abandon the target, fly straight home) covers that.
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
        // is known). The one shared "how forward is this base" yardstick for
        // TryReplan/TryPlanSortiePreferForwardLanding's tie-break and forward-landing scoring, so
        // the reads can never quietly disagree.
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

        // Shared continuation logic for every sortie kind — advance the outbound leg, flip to the
        // return leg once the objective hex is reached, advance the return leg, complete on landing
        // — so route/capacity logic exists exactly once.
        //
        // Re-validates the plan fresh every call (must recheck before it launches OR moves): the
        // outbound leg re-searches every owned airfield via TryPlanSortiePreferForwardLanding on
        // every step, the same "always re-derived" treatment the return leg gets via TryReplan.
        // Both legs hard-filter to known-AA-free candidates first; the outbound leg then prefers
        // forward usefulness over cost (it is choosing where to base next), while the return leg
        // prefers lower cost (it is just going home). Whenever NEITHER leg can find a complete safe
        // round trip, the "turn for home NOW, abandon further progress toward the target" fallback
        // below applies. Returns null (propose nothing this step) whenever nothing reachable exists
        // at all — callers must never strand the aircraft on a doomed order.
        public static AiDecision ContinueSortie(PlayerSetupData player, PlayerRoot root, AiTurnContext ctx, AirSortie task,
            string logLabel, string outboundReason, float continuationScore)
        {
            if (task.Army?.Controller == null || !ArmyRegistry.AllForOwner(player).Contains(task.Army) || !AviationRules.IsAirArmy(task.Army))
            {
                // Releases whatever launch-Energy reservation (see AiAiAirSortiePlanner.LaunchRoutine)
                // this task may still be holding — safe even if the army already moved and the
                // reservation was already released there (Release is a no-op on an unknown task).
                AirSortieRegistry.Remove(player, task);
                return null;
            }
            if (task.Army.Members.Count == 0)
            {
                // AA destroyed the whole sortie — ordinary empty-army cleanup applies (see
                // AiTurnController.RunEmptyArmyCleanup), nothing left here to fly.
                AirSortieRegistry.Remove(player, task);
                return null;
            }

            // Anti-loop memory — AirRecon must not fly endlessly into one stale hex: once a recon
            // sortie is underway toward a hex, stamp it so another sortie is not sent to the same
            // hex for AiConfig.airReconTargetCooldownTurns turns after this one ends (unless live
            // enemy intel turns up on it). Re-stamped every outbound step — including the step that
            // reaches it — so the cooldown counts from the sortie's last real progress. Recon only:
            // Strike has its own targeting and no such loop to guard against.
            if (task.Kind == AirSortieKind.Recon && task.Outbound)
                AiMapMemory.RecordAirReconTarget(player, task.TargetHex, ctx.TurnNumber);

            // Outbound leg finished the moment the army reaches the objective — the strike itself
            // (if the target was still there) or the recon reveal already happened as a side effect
            // of the MoveArmy step that landed the army on this hex (AviationCombatPresenter.
            // ResolveStep). Turn for home.
            if (task.Outbound && task.Army.Hex.Equals(task.TargetHex))
            {
                task.Outbound = false;
                task.TargetHex = task.LandingHex;
            }

            // Return leg finished — the sortie is over.
            if (!task.Outbound && task.Army.Hex.Equals(task.TargetHex))
            {
                AirSortieRegistry.Remove(player, task);
                return null;
            }

            // Definite assignment: the Rebase branch below assigns `destination` in both
            // exactRebaseReady cases, but the compiler cannot correlate the two tests (CS0165).
            // Seeded with the task's current target; every reachable path overwrites it before use.
            HexCoord destination = task.TargetHex;
            if (task.Outbound)
            {
                Sortie? sortie = TryPlanSortiePreferForwardLanding(task.Army, task.TargetHex, ctx.Map, player);
                if (sortie.HasValue)
                {
                    task.LandingHex = sortie.Value.LandingHex;
                    task.IsMultiTurn = false;
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
                        task.IsMultiTurn = true;
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
                        task.Outbound = false;
                        HexCoord home = fallback ?? multiFallback.Value.LandingHex;
                        task.IsMultiTurn = fallback == null;
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
                bool exactRebaseReady = false;
                if (task.Kind == AirSortieKind.Rebase)
                {
                    RebaseRoute? exact = TryContinueExactRebase(
                        task.Army, task.LandingHex, ctx.Map, player);
                    if (exact.HasValue)
                    {
                        task.TargetHex = task.LandingHex;
                        task.IsMultiTurn = exact.Value.RequiredTurns > 1;
                        destination = task.LandingHex;
                        exactRebaseReady = true;
                    }

                    else
                    {
                        // The selected Base ceased to be a safe/capacious destination after
                        // launch. Recovery remains mandatory: fall through to the ordinary live
                        // emergency landing search instead of pressing a stale rebase order.
                        AiDebugLog.Write($"[AI] {player.Nickname}: \"{task.Army.Name}\" — AviationRebase "
                            + "destination invalidated; replanning nearest safe owned airfield.");
                    }
                }
                if (!exactRebaseReady)
                {
                    HexCoord? confirmedLanding = TryReplan(task.Army, ctx.Map, player);
                    if (confirmedLanding != null)
                    {
                        task.LandingHex = confirmedLanding.Value;
                        task.TargetHex = confirmedLanding.Value;
                        task.IsMultiTurn = false;
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
                        task.IsMultiTurn = true;
                        destination = multiReturn.Value.LandingHex;
                        LogMultiTurnContinuation(player, task, logLabel, multiReturn.Value, arrivingHome: true);
                    }
                }
            }

            if (!AiTurnController.CanIssueMoveNow(root, task.Army, ctx.Map, destination))
                return null;
            HexCoord? nextStep = AiTurnController.FindAffordableStep(ctx.Map, task.Army, destination);
            if (nextStep == null)
                return null;

            string reason = task.Outbound ? outboundReason : "returns to land";
            return AiDecision.Move(task.Army, nextStep.Value, reason, continuationScore);
        }

        // Multi-turn diagnostic line (spec point 12/16) — reports the group's own live
        // SafeUnlandedEndsRemaining against what this specific plan still needs, so a playtester can
        // see exactly when the next turn's landing stops being optional. `arrivingHome` only changes
        // the wording (still pressing toward the objective vs. already turned for home).
        private static void LogMultiTurnContinuation(PlayerSetupData player, AirSortie task, string logLabel,
            MultiTurnSortie multi, bool arrivingHome)
        {
            int safeNow = AviationRange.SafeUnlandedEndsRemaining(task.Army.Members);
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
        // computation over the specific UnitData subset a candidate wants to launch. Energy is read
        // from the raw stockpile, the same physical gate as AiTurnController.CanIssueMoveNow (see
        // its comment); owner-reserved Energy is protected at the Provisioning gate.
        public static bool CanAffordLaunch(PlayerRoot root, IReadOnlyList<UnitData> aircraft)
        {
            if (root == null || aircraft == null || aircraft.Count == 0)
                return false;
            int apCost = aircraft.Sum(u => u.ActivationApCost);
            int energyCost = aircraft.Sum(u => u.LaunchEnergyCost);
            return root.CanSpendActionPoints(apCost)
                && root.GetResource(ResourceType.Energy) >= energyCost;
        }

        // Shared execution for LaunchAirStrike/LaunchAirRecon (see AiTurnController.
        // PerformDecision's own dispatch switch) — the one Kind pair genuine MoveArmy can't express
        // (converting a still-stored aircraft group into a real flying ArmyData, via
        // AviationActions.TryLaunch — the exact same shared API a human's own launch button calls).
        // Forming the stack itself is free (see AviationActions.TryLaunch's own comment — "forming a
        // stack is not a take-off"); the real AP/Energy launch cost is still charged the ordinary
        // way, by this new army's own first MoveArmy activation, whenever that step actually comes
        // up — below, synchronously, via ContinueSortie. Registers the fresh AirSortie itself once
        // the army actually exists; there is no sortie, and nothing to claim, until the launch
        // actually succeeds.
        public static IEnumerator LaunchRoutine(PlayerSetupData player, AiDecision decision,
            AiTurnContext ctx, AirSortieKind taskKind, AiMoveExecutionTrace executionTrace = null)
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

            var task = new AirSortie
            {
                Kind = taskKind, Army = airArmy,
                TargetHex = taskKind == AirSortieKind.Rebase ? decision.AirLandingHex : decision.AirActionHex,
                LandingHex = decision.AirLandingHex,
                Outbound = taskKind != AirSortieKind.Rebase,
            };
            AirSortieRegistry.Add(player, task);

            PlayerRoot root = PlayerRootRegistry.FindFor(player);
            AiDebugLog.Write($"[AI] {player.Nickname}: \"{airArmy.Name}\" assigned {taskKind} — target "
                + $"({decision.AirActionHex.Q},{decision.AirActionHex.R}), landing ({decision.AirLandingHex.Q},{decision.AirLandingHex.R}).");

            // Launch and the sortie's first real step are one indivisible sequence: otherwise the
            // army could sit unactivated while another action spends the AP/Energy its first step
            // needs, or a route/landing slot stops being safe, stranding it airborne. Driving
            // ContinueSortie synchronously right here — the same route/AP/Energy/landing-slot
            // recheck every later step goes through (CanIssueMoveNow) — closes that gap: by the
            // time this coroutine yields, the army has either taken its first real step or never
            // left storage at all.
            string logLabel = taskKind == AirSortieKind.Strike ? "AirStrike"
                : taskKind == AirSortieKind.Rebase ? "AviationRebase" : "AirRecon";
            string outboundReason = taskKind == AirSortieKind.Strike
                ? "presses on toward the strike target"
                : taskKind == AirSortieKind.Rebase
                    ? "relocates to the selected forward airfield"
                    : "flies on toward the recon target";
            AiDecision firstMove = ContinueSortie(player, root, ctx, task, logLabel, outboundReason,
                AiConfig.airStrikeContinuationScore);
            if (firstMove == null)
            {
                // Can't even take the first step this turn — never leave the group formed and
                // airborne with nothing able to move it: undo the launch and return every aircraft
                // to the airfield's own stored container, right where they started this step.
                AiDebugLog.Write($"[AI] {player.Nickname}: \"{airArmy.Name}\" — {taskKind} has no viable first step "
                    + "this turn, cancels the launch and returns aircraft to storage.");
                AirSortieRegistry.Remove(player, task);
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
            yield return AiTurnController.MoveArmyRoutine(player, firstMove, ctx, executionTrace);
        }
    }
}
