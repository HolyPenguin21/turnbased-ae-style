using System.Collections.Generic;
using System.Linq;
using Game.Ai;
using Game.Aviation;
using Game.Economy;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Units;
using UnityEngine;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  AI-RECON-02 — AIR OBSERVATION CAPACITY  (the single shared air-recon capacity authority)
    // ===========================================================================================
    //  ONE place decides how much air OBSERVATION capacity actually exists this turn, using the
    //  exact primitives ReconAirExecutor launches against so the capacity model and the executor
    //  cannot drift:
    //    · MaxAirReconActorsPerTurn — the per-turn air-recon actor slot cap (was
    //      ReconAirExecutor.MaxAirActorsPerTurn, a private const the snapshot could not see);
    //    · SelectReconLaunchSubset + AiConfig.aviationLaunchMinReadyAircraft — a storage sortie is
    //      one minimum aircraft subset, never the whole hangar;
    //    · AiAviationSupport.CanAffordLaunch semantics — AP + reservation-net Energy;
    //    · a ready standalone wing is on an owned airfield with NO AiTask and MP left.
    //
    //  Evaluate() runs ONE greedy budget pass: the post-reservation AP/Energy budget, minus the
    //  first-activation Energy airborne recon/strike wings still owe, is spent slot by slot across
    //  (ready standalone wings, then storage launch subsets). Each accepted slot consumes its own
    //  AP + Energy from the local budget, so the same AP/Energy is never counted for two aircraft,
    //  and the number of slots is bounded by MaxAirReconActorsPerTurn minus the slots already
    //  consumed by in-flight air work.
    // ===========================================================================================
    internal readonly struct ReconAirObservationCapacity
    {
        // Own air armies already flying a durable ReconAssignment — active observation lanes the
        // executor will spend a slot CONTINUING this turn.
        public readonly int AirborneReconWings;
        // Additional recon sorties that could actually be launched THIS turn — slot- AND
        // shared-AP/Energy-budget-bounded.
        public readonly int SpareSorties;

        public ReconAirObservationCapacity(int airborneReconWings, int spareSorties)
        {
            AirborneReconWings = airborneReconWings;
            SpareSorties = spareSorties;
        }
    }

    internal static class ReconAirCapacityPolicy
    {
        // Was ReconAirExecutor.MaxAirActorsPerTurn — hoisted so the capacity snapshot honours the
        // same per-turn ceiling the executor enforces (it stops after this many air actors,
        // continued + newly launched combined).
        public const int MaxAirReconActorsPerTurn = 2;

        // Mirror of ReconAirExecutor's own storage-subset rule — kept here so both read ONE rule:
        // the cheapest-to-activate minimum aircraft subset for a single recon sortie, deterministic
        // tie-break on the canonical storage roster order.
        internal static List<UnitData> SelectReconLaunchSubset(IReadOnlyList<UnitData> stored)
        {
            int want = Mathf.Max(1, AiConfig.aviationLaunchMinReadyAircraft);
            return (stored ?? System.Array.Empty<UnitData>())
                .Select((u, i) => (u, i))
                .Where(t => t.u != null)
                .OrderBy(t => t.u.LaunchEnergyCost)
                .ThenBy(t => t.u.ActivationApCost)
                .ThenBy(t => t.i)
                .Take(want)
                .Select(t => t.u)
                .ToList();
        }

        public static ReconAirObservationCapacity Evaluate(PlayerSetupData player, PlayerRoot root)
        {
            if (player == null || root == null)
                return default;

            List<ArmyData> ownAir = ArmyRegistry.AllForOwner(player)
                .Where(a => a != null && AviationRules.IsValidAirArmy(a))
                .ToList();

            bool InFlightAir(ArmyData a) =>
                ReconAssignmentRegistry.TryGet(player, a.Id, out _)
                || (AiTaskRegistry.TaskFor(player, a) is AiTask t
                    && (t.Kind == AiTaskKind.AirRecon || t.Kind == AiTaskKind.AirStrike));

            int airborneReconWings = ownAir.Count(a =>
                !AviationRules.IsOwnedAirfieldAt(a.Hex, player)
                && ReconAssignmentRegistry.TryGet(player, a.Id, out _));

            // ReconAirExecutor's per-turn actor counter (actorsUsed) is only incremented for RECON
            // actors it drives: in-flight wings with a ReconAssignment, then ready standalone recon
            // launches, then storage recon launches. An AirStrike sortie (or an orphan AirRecon
            // task with no assignment) is NEVER counted against MaxAirReconActorsPerTurn — it just
            // makes that aircraft unavailable and owes its own activation Energy. So only airborne
            // recon wings consume a slot here (review round 4, P1).
            int spareSlots = Mathf.Max(0, MaxAirReconActorsPerTurn - airborneReconWings);
            if (spareSlots == 0)
                return new ReconAirObservationCapacity(airborneReconWings, 0);

            // Local shared budget: post-reservation AP/Energy minus the first-activation Energy
            // EVERY in-flight air wing still owes this turn — AirStrike included (parity with
            // ReconAirEnergyPolicy's committed term; an already-activated wing owes nothing).
            int apBudget = Mathf.Max(0, root.ActionPoints);
            int energyBudget = Mathf.Max(0, AiResourceReservation.Available(root, player, ResourceType.Energy));
            foreach (ArmyData a in ownAir)
                if (!a.HasActivatedThisTurn && InFlightAir(a))
                    energyBudget = Mathf.Max(0, energyBudget - Mathf.Max(0, a.ActivationEnergyCost));

            // Ordered EXACTLY as ReconAirExecutor launches (review round 4, P1): ready standalone
            // wings first, in the executor's own sort, THEN one storage launch subset per owned
            // airfield in OwnedAirfieldHexes order. NOT a global cheapest-first sort — that could
            // "afford" a combination the executor would never reach (a pricey ready wing it meets
            // first eats the budget before a cheap hangar sortie).
            var orderedCosts = new List<(int ap, int energy)>();

            foreach (ArmyData a in ownAir
                .Where(a => AviationRules.IsOwnedAirfieldAt(a.Hex, player)
                    && AiTaskRegistry.TaskFor(player, a) == null
                    && a.CurrentMovement > 0)
                .OrderBy(a => a.HasActivatedThisTurn ? 0 : Mathf.Max(0, a.ActivationEnergyCost))
                .ThenBy(a => a.HasActivatedThisTurn ? 0 : Mathf.Max(0, a.ActivationApCost))
                .ThenBy(a => a.Id))
            {
                orderedCosts.Add((a.HasActivatedThisTurn ? 0 : Mathf.Max(0, a.ActivationApCost),
                    a.HasActivatedThisTurn ? 0 : Mathf.Max(0, a.ActivationEnergyCost)));
            }

            foreach (HexCoord hex in AiAviationSupport.OwnedAirfieldHexes(player))
            {
                ArmyData airfield = AviationRules.FindAirfieldAt(hex, player);
                if (airfield == null
                    || airfield.Members.Count < Mathf.Max(1, AiConfig.aviationLaunchMinReadyAircraft))
                    continue;
                List<UnitData> subset = SelectReconLaunchSubset(airfield.Members);
                if (subset.Count == 0)
                    continue;
                orderedCosts.Add((subset.Sum(u => Mathf.Max(0, u.ActivationApCost)),
                    subset.Sum(u => Mathf.Max(0, u.LaunchEnergyCost))));
            }

            int spare = 0;
            foreach ((int ap, int energy) in orderedCosts)
            {
                if (spare >= spareSlots)
                    break;
                if (ap > apBudget || energy > energyBudget)
                    continue;   // executor moves on to the next candidate in order (does not reorder)
                apBudget -= ap;
                energyBudget -= energy;
                spare++;
            }

            return new ReconAirObservationCapacity(airborneReconWings, spare);
        }
    }
}
