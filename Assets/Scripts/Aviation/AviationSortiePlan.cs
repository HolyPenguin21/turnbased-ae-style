using System.Collections.Generic;
using System.Linq;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Units;
using UnityEngine;

namespace Game.Aviation
{
    // Physical sortie-plan data + pure range/route feasibility. A Sortie / MultiTurnSortie is a
    // "start -> action hex -> owned airfield" plan; AviationRange answers how long a group can
    // stay airborne and whether a hex sequence is flyable within that fuel budget. No AA
    // knowledge, no map memory, no reservations - that route/target SELECTION is AI planning
    // (Game.Ai.V2.AiAirSortiePlanner). Extracted from Game.Aviation.AviationSupport (ARCH-01).

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

    public static class AviationRange
    {
        public static int SafeUnlandedEndsRemaining(UnitData aircraft) => aircraft == null
            ? 0 : Mathf.Max(0, aircraft.TurnsWithoutRefuel - aircraft.ConsecutiveUnlandedEnds);

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
                min = Mathf.Min(min, SafeUnlandedEndsRemaining(unit));
            return min == int.MaxValue ? 0 : min;
        }

        public static int SafeUnlandedEndsRemaining(ArmyData airArmy) => SafeUnlandedEndsRemaining(airArmy?.Members);

        // Coarse first-turn outbound budget for valuation before a concrete route is selected.
        // Zero endurance must preserve enough of the same turn to fly home; positive endurance may
        // use the full first-turn movement and recover on a later turn. Real step admission never
        // trusts this estimate — TrySimulateHexSequence / sortie planners prove the whole route.
        public static int FirstTurnOutboundBudget(IReadOnlyList<UnitData> aircraft)
        {
            if (aircraft == null || aircraft.Count == 0)
                return 0;
            int movement = aircraft.Min(AviationRules.EffectiveMoveCurrent);
            return SafeUnlandedEndsRemaining(aircraft) > 0 ? movement : movement / 2;
        }

        // ---- Route calendar over whole transitions ---------------------------------------------
        // The one aviation flight calendar (spec 2026-11-11 §5): every transition of the route is
        // priced by MovementRules (Air profile: 1 MP, +1 leaving a storm hex) and paid WHOLE out of
        // this turn's budget; a transition that does not fit ends the own turn on the last reached
        // hex and is tried again with the next turn's full budget. Never `index += movement`,
        // never a hex count or a geometric distance standing in for MP.

        // The route's transition costs (Air profile, the map's current weather); false when the
        // route is not flyable.
        public static bool TryStepCosts(HexMap map, IReadOnlyList<HexCoord> hexes, List<int> costs) =>
            MovementRules.TryGetStepCosts(map, hexes, MovementProfile.Air, map?.Weather, costs);

        public static List<int> StepCostsOrNull(HexMap map, HexPath path)
        {
            var costs = new List<int>();
            return path != null && TryStepCosts(map, path.Hexes, costs) ? costs : null;
        }

        // Own turns needed to fly `costs` (1 = arrives this turn), spending whole transitions out
        // of firstTurnMovement now and maxMovement each later turn. int.MaxValue when some
        // transition exceeds even a full turn's budget. An empty route takes 1 (already there).
        public static int TurnsToFly(IReadOnlyList<int> costs, int firstTurnMovement, int maxMovement)
        {
            if (costs == null) return int.MaxValue;
            int turns = 1;
            int mp = Mathf.Max(0, firstTurnMovement);
            foreach (int cost in costs)
            {
                if (cost > mp)
                {
                    if (cost > maxMovement) return int.MaxValue;
                    turns++;
                    mp = maxMovement;
                }
                mp -= cost;
            }
            return turns;
        }

        // How many of its own turns a group can strike one target in a single sortie: fly the
        // outbound transitions (this turn's `firstTurnMovement`, then `maxMovement` per later
        // turn), strike on arrival, hold over the target for further strike turns, then fly the
        // return transitions home. Every turn-end away from an airfield consumes one of
        // `safeEnds`; the plan never exceeds them (no fuel damage). Approach and return turns
        // therefore eat into the strike turns — it is NOT simply safeEnds + 1. Intermediate
        // airfields are ignored (conservative). 0 when not even one strike-and-return fits.
        public static int StrikeTurns(int firstTurnMovement, int maxMovement, int safeEnds,
            IReadOnlyList<int> outboundCosts, IReadOnlyList<int> returnCosts)
        {
            if (outboundCosts == null || returnCosts == null)
                return 0;
            int best = 0;
            for (int strikes = 1; strikes <= Mathf.Max(0, safeEnds) + 1; strikes++)
            {
                if (!StrikeCalendarFits(firstTurnMovement, maxMovement, safeEnds, outboundCosts,
                        returnCosts, strikes))
                    break;
                best = strikes;
            }
            return best;
        }

        private static bool StrikeCalendarFits(int firstTurnMovement, int maxMovement, int safeEnds,
            IReadOnlyList<int> outboundCosts, IReadOnlyList<int> returnCosts, int strikes)
        {
            int mp = Mathf.Max(0, firstTurnMovement);
            int ends = 0;
            bool Fly(IReadOnlyList<int> costs)
            {
                foreach (int cost in costs)
                {
                    if (cost > mp)
                    {
                        if (++ends > safeEnds || cost > maxMovement)
                            return false;
                        mp = maxMovement;
                    }
                    mp -= cost;
                }
                return true;
            }
            if (!Fly(outboundCosts))
                return false;
            for (int held = 1; held < strikes; held++)
            {
                if (++ends > safeEnds)
                    return false;
                mp = maxMovement;
            }
            return Fly(returnCosts);
        }

        public static IReadOnlyList<HexCoord> CombineRoute(HexPath outbound, HexPath returnPath)
        {
            var hexes = new List<HexCoord>(outbound.Hexes);
            hexes.AddRange(returnPath.Hexes.Skip(1));
            return hexes;
        }

        // Turn-by-turn feasibility core shared by every multi-turn plan (a two-leg start->action->
        // landing route, an expendable strike, a rebase, a landing-only return with actionIndex 0).
        // Walks the route's transitions (MovementRules on `map`'s current weather), paying each
        // one whole from firstTurnMovement this turn and from the group's own fresh
        // EffectiveMoveMax (min across members) every later turn. A turn that cannot pay its next
        // transition ends on the last reached hex — including the start hex when this turn has
        // no budget left, a real end of turn that consumes fuel like any other.
        //
        // Ending a turn on an owned airfield is a real landing (AviationTurnLifecycle): it closes
        // the sortie, and going on needs a new paid launch. A single route proof therefore never
        // refuels at an intermediate airfield and flies on for free: a plan whose turn first ends
        // on an owned airfield before its last hex is REJECTED (the caller may plan that landing
        // as its own leg). Every other turn-end away from the final hex increments the unlanded
        // count, and the route is rejected the instant it would exceed safeRemaining — the live
        // "ConsecutiveUnlandedEnds > TurnsWithoutRefuel" damage rule, simulated as a pure query
        // that never touches unit state. `turnEnds` (optional) receives the route index where each
        // own turn ends, the last one being the final hex.
        public static bool TrySimulateHexSequence(HexMap map, IReadOnlyList<HexCoord> hexes, int actionIndex,
            int firstTurnMovement, IReadOnlyList<UnitData> aircraft, int safeRemaining, PlayerSetupData owner,
            out int requiredTurns, out int requiredUnlandedEnds, out HexCoord turn1Destination,
            out bool reachesActionThisTurn, out bool landsThisTurn, List<int> turnEnds = null)
        {
            requiredTurns = 0;
            requiredUnlandedEnds = 0;
            turn1Destination = hexes != null && hexes.Count > 0 ? hexes[0] : default;
            reachesActionThisTurn = actionIndex <= 0;
            landsThisTurn = false;
            turnEnds?.Clear();
            var costs = new List<int>();
            if (hexes == null || hexes.Count == 0 || (hexes.Count > 1 && !TryStepCosts(map, hexes, costs)))
                return false;
            return TrySimulateTransitions(hexes, costs, actionIndex, firstTurnMovement, aircraft, safeRemaining,
                owner, out requiredTurns, out requiredUnlandedEnds, out turn1Destination,
                out reachesActionThisTurn, out landsThisTurn, turnEnds);
        }

        // The calendar core over transition costs already priced by MovementRules (costs[i] is
        // hexes[i] -> hexes[i + 1]). TrySimulateHexSequence prices the route on the map first.
        public static bool TrySimulateTransitions(IReadOnlyList<HexCoord> hexes, IReadOnlyList<int> costs,
            int actionIndex, int firstTurnMovement, IReadOnlyList<UnitData> aircraft, int safeRemaining,
            PlayerSetupData owner, out int requiredTurns, out int requiredUnlandedEnds, out HexCoord turn1Destination,
            out bool reachesActionThisTurn, out bool landsThisTurn, List<int> turnEnds = null)
        {
            requiredTurns = 0;
            requiredUnlandedEnds = 0;
            turn1Destination = hexes != null && hexes.Count > 0 ? hexes[0] : default;
            reachesActionThisTurn = actionIndex <= 0;
            landsThisTurn = false;
            turnEnds?.Clear();
            if (hexes == null || hexes.Count == 0 || aircraft == null || aircraft.Count == 0
                || costs == null || costs.Count != hexes.Count - 1)
                return false;

            int lastIndex = hexes.Count - 1;
            if (lastIndex == 0)
            {
                landsThisTurn = true;
                turnEnds?.Add(0);
                return true;
            }

            int idx = 0;
            int used = 0;
            int mp = Mathf.Max(0, firstTurnMovement);
            int maxEffective = aircraft.Min(AviationRules.EffectiveMoveMax);
            bool first = true;
            while (idx < lastIndex)
            {
                while (idx < lastIndex && costs[idx] <= mp)
                {
                    mp -= costs[idx];
                    idx++;
                }
                requiredTurns++;
                turnEnds?.Add(idx);
                HexCoord stop = hexes[idx];
                if (first)
                {
                    turn1Destination = stop;
                    reachesActionThisTurn = idx >= actionIndex;
                    first = false;
                }
                if (idx == lastIndex)
                    break;
                if (AviationRules.IsOwnedAirfieldAt(stop, owner))
                    return false; // an intermediate landing would close this sortie
                used++;
                if (used > safeRemaining)
                    return false;
                requiredUnlandedEnds = Mathf.Max(requiredUnlandedEnds, used);
                // A transition the next full budget cannot pay either never becomes possible.
                if (maxEffective <= 0 || costs[idx] > maxEffective)
                    return false;
                mp = maxEffective;
            }
            landsThisTurn = requiredTurns == 1;
            return true;
        }
    }
}
