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

        public static IReadOnlyList<HexCoord> CombineRoute(HexPath outbound, HexPath returnPath)
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
        public static bool TrySimulateHexSequence(IReadOnlyList<HexCoord> hexes, int actionIndex, int firstTurnMovement,
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
    }
}
