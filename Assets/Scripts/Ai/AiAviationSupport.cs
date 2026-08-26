using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Game.Aviation;
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

        // How many MORE aircraft `hex` can actually receive right now. The engine itself only
        // capacity-checks the STORED container (new card deployment, see AviationRules.
        // FreeAirfieldCapacity/ArmyActions.DeployUnitFromCard) — a landed, already-launched air
        // army is a separate ArmyData the move layer never caps. This is deliberately MORE
        // conservative than the engine strictly requires: it also counts every other already-
        // landed air army's own aircraft against the same capacity, so the AI never voluntarily
        // stacks more aircraft onto one airfield hex than its stated capacity, even though nothing
        // stops it from doing so. `excluding` — the mover's own air army, so a sortie re-checking
        // its ALREADY-chosen landing hex mid-flight doesn't count itself against its own capacity.
        public static int FreeLandingCapacity(HexCoord hex, PlayerSetupData owner, ArmyData excluding = null)
        {
            int capacity = AviationRules.AirfieldCapacityAt(hex, owner);
            if (capacity <= 0)
                return 0;
            int used = AviationRules.FindAirfieldAt(hex, owner)?.Members.Count ?? 0;
            foreach (ArmyData army in ArmyRegistry.AllAt(hex))
                if (army != excluding && army.Owner == owner && AviationRules.IsAirArmy(army))
                    used += army.Members.Count;
            return Mathf.Max(0, capacity - used);
        }

        // start airfield -> action hex -> any owned airfield with free capacity — the shared
        // safety invariant every AirStrike/AirRecon continuation re-derives fresh (never cached on
        // the task) before proposing a launch or a further step. `preferredLanding` lets a caller
        // ask "is the CURRENTLY chosen landing hex still good" without re-searching every airfield
        // — TryContinueAirStrikeTask/TryContinueAirReconTask pass the task's own LandingHex first
        // and only fall back to a full search (via TryReplan) once that specific one stops working.
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

        public static Sortie? TryPlanSortie(ArmyData airArmy, HexCoord actionHex, HexMap map, PlayerSetupData owner,
            HexCoord? preferredLanding = null)
        {
            if (!AviationRules.IsValidAirArmy(airArmy) || airArmy.Owner != owner)
                return null;
            return PlanSortieCore(airArmy.Hex, airArmy, army => army.CurrentMovement, path => AviationRules.PathMoveCost(airArmy, path),
                airArmy.Members.Count, 0, actionHex, map, owner, preferredLanding);
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
                aircraft.Count, aircraft.Count, actionHex, map, owner, null);
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
        private static Sortie? PlanSortieCore(HexCoord startHex, ArmyData excludingFromCapacity,
            System.Func<ArmyData, int> movementBudget, System.Func<HexPath, int> pathCost,
            int requiredSlots, int vacatingAtStart, HexCoord actionHex, HexMap map, PlayerSetupData owner,
            HexCoord? preferredLanding)
        {
            if (map == null || owner == null)
                return null;

            HexPath outbound = HexPathfinder.FindPath(map, startHex, actionHex, flatCost: true);
            if (outbound == null)
                return null;
            int outboundCost = pathCost(outbound);
            int movement = movementBudget(excludingFromCapacity);

            IEnumerable<HexCoord> candidates = preferredLanding.HasValue
                ? new[] { preferredLanding.Value }
                : OwnedAirfieldHexes(owner);

            Sortie? best = null;
            foreach (HexCoord landing in candidates)
            {
                if (!AviationRules.IsOwnedAirfieldAt(landing, owner))
                    continue;
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
                if (best == null || totalCost < best.Value.TotalCost)
                    best = new Sortie(actionHex, landing, outbound, ret, totalCost);
            }
            return best;
        }

        // The "plan became invalid" fallback (target disappeared, landing base captured/destroyed/
        // full, path became impossible, or the army lost effective MP) — prefers a newly reachable
        // OWNED airfield over giving up outright. Tries the army's own CURRENT hex as the "action
        // hex" (i.e. "can I still just fly straight home from here") first since that's always the
        // cheapest possible sortie, then falls back to searching every owned airfield directly.
        // Null means nothing is reachable THIS turn — callers must stop proposing voluntary
        // aviation movement rather than strand the aircraft on a doomed order (per spec).
        public static HexCoord? TryReplan(ArmyData airArmy, HexMap map, PlayerSetupData owner)
        {
            if (!AviationRules.IsValidAirArmy(airArmy) || map == null)
                return null;

            HexCoord? best = null;
            int bestCost = int.MaxValue;
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
                if (cost < bestCost)
                {
                    bestCost = cost;
                    best = landing;
                }
            }
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
        // moves) — the outbound leg keeps preferring the task's own already-chosen LandingHex for
        // stability, only widening to a full search (TryReplan) once that specific one stops
        // working; the return leg re-derives a fresh best landing every step via TryReplan directly
        // (a strictly single-leg check — always monotonically at least as cheap as the step before,
        // so this can never thrash between two airfields). Returns null (propose nothing this step)
        // whenever nothing reachable exists — callers must never strand the aircraft on a doomed
        // order, per spec.
        public static AiDecision ContinueSortie(PlayerSetupData player, PlayerRoot root, AiTurnContext ctx, AiTask task,
            string logLabel, string outboundReason, float continuationScore, AiTaskCategory category)
        {
            if (task.Army?.Controller == null || !ArmyRegistry.AllForOwner(player).Contains(task.Army) || !AviationRules.IsAirArmy(task.Army))
            {
                AiTaskRegistry.Remove(player, task);
                return null;
            }
            if (task.Army.Members.Count == 0)
            {
                // AA destroyed the whole sortie — ordinary empty-army cleanup applies (see
                // AiTurnController.RunEmptyArmyCleanup), nothing left here to fly.
                AiTaskRegistry.Remove(player, task);
                return null;
            }

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
                AiTaskRegistry.Remove(player, task);
                return null;
            }

            HexCoord destination;
            if (task.AirOutbound)
            {
                Sortie? sortie = TryPlanSortie(task.Army, task.TargetHex, ctx.Map, player, task.LandingHex);
                if (!sortie.HasValue)
                {
                    HexCoord? fallback = TryReplan(task.Army, ctx.Map, player);
                    if (fallback == null)
                    {
                        AiDebugLog.Write($"[AI] {player.Nickname}: \"{task.Army.Name}\" — {logLabel} has no reachable "
                            + "owned airfield this turn, holding position.");
                        return null;
                    }
                    AiDebugLog.Write($"[AI] {player.Nickname}: \"{task.Army.Name}\" — {logLabel} target/route no longer "
                        + $"viable, turns for home to ({fallback.Value.Q},{fallback.Value.R}).");
                    task.AirOutbound = false;
                    task.LandingHex = fallback.Value;
                    task.TargetHex = fallback.Value;
                    destination = fallback.Value;
                }
                else
                {
                    task.LandingHex = sortie.Value.LandingHex;
                    destination = task.TargetHex;
                }
            }
            else
            {
                HexCoord? confirmedLanding = TryReplan(task.Army, ctx.Map, player);
                if (confirmedLanding == null)
                {
                    AiDebugLog.Write($"[AI] {player.Nickname}: \"{task.Army.Name}\" — {logLabel} has no reachable owned "
                        + "airfield this turn, holding position.");
                    return null;
                }
                task.LandingHex = confirmedLanding.Value;
                task.TargetHex = confirmedLanding.Value;
                destination = confirmedLanding.Value;
            }

            if (!AiTurnController.CanIssueMoveNow(root, player, task.Army, ctx.Map, destination))
                return null;
            HexCoord? nextStep = AiTurnController.FindAffordableStep(ctx.Map, task.Army, destination);
            if (nextStep == null)
                return null;

            string reason = task.AirOutbound ? outboundReason : "returns to land";
            return AiDecision.Move(task.Army, nextStep.Value, reason, task, continuationScore, category);
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
        // stack is not a take-off"); the real AP/Energy launch cost is charged the ordinary way, by
        // this new army's own first MoveArmy activation, the very next step — nothing special-cased
        // here for that. Registers the fresh AiTask itself once the army actually exists — Commit
        // never does this for a Launch* decision (decision.Task is deliberately left null by the
        // factories; there is no task, and nothing to claim, until the launch actually succeeds).
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
            AiDebugLog.Write($"[AI] {player.Nickname}: \"{airArmy.Name}\" assigned {taskKind} — target "
                + $"({decision.AirActionHex.Q},{decision.AirActionHex.R}), landing ({decision.AirLandingHex.Q},{decision.AirLandingHex.R}).");
            yield return AiTurnController.WaitStep(ctx);
        }
    }
}
