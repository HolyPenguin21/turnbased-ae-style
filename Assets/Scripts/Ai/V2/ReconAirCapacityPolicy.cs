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

    // AI-RECON-01 — the same greedy result as ReconAirObservationCapacity, but with the concrete
    // slots so ReconAirReservationPrepass can pin specific actors and protect their exact AP/Energy.
    internal readonly struct AirObservationSlot
    {
        public readonly int? ActorId;       // a ready standalone wing's army id; null for a hangar launch subset
        public readonly HexCoord AirfieldHex; // the launching airfield when ActorId is null; default otherwise
        public readonly int Ap;
        public readonly int Energy;

        public AirObservationSlot(int? actorId, HexCoord airfieldHex, int ap, int energy)
        {
            ActorId = actorId;
            AirfieldHex = airfieldHex;
            Ap = ap;
            Energy = energy;
        }
    }

    internal sealed class ReconAirObservationDetail
    {
        public readonly List<int> AirborneWingIds = new List<int>();
        public int AirborneUnactivatedEnergy;                 // first-activation Energy airborne wings still owe this turn
        public readonly List<AirObservationSlot> AcceptedSpareSlots = new List<AirObservationSlot>();
        public int AirborneReconWings => AirborneWingIds.Count;
        public int SpareSorties => AcceptedSpareSlots.Count;
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
            ReconAirObservationDetail d = EvaluateDetailed(player, root);
            return new ReconAirObservationCapacity(d.AirborneReconWings, d.SpareSorties);
        }

        public static ReconAirObservationDetail EvaluateDetailed(PlayerSetupData player, PlayerRoot root)
        {
            var detail = new ReconAirObservationDetail();
            if (player == null || root == null)
                return detail;

            List<ArmyData> ownAir = ArmyRegistry.AllForOwner(player)
                .Where(a => a != null && AviationRules.IsValidAirArmy(a))
                .ToList();

            bool InFlightAir(ArmyData a) =>
                ReconAssignmentRegistry.TryGet(player, a.Id, out _)
                || (AiTaskRegistry.TaskFor(player, a) is AiTask t
                    && (t.Kind == AiTaskKind.AirRecon || t.Kind == AiTaskKind.AirStrike));

            foreach (ArmyData a in ownAir)
                if (!AviationRules.IsOwnedAirfieldAt(a.Hex, player)
                    && ReconAssignmentRegistry.TryGet(player, a.Id, out _))
                    detail.AirborneWingIds.Add(a.Id);
            int airborneReconWings = detail.AirborneWingIds.Count;

            // First-activation Energy EVERY in-flight air wing still owes this turn — AirStrike
            // included (parity with ReconAirEnergyPolicy's committed term; an already-activated wing
            // owes nothing). ReconAirReservationPrepass protects this too.
            foreach (ArmyData a in ownAir)
                if (!a.HasActivatedThisTurn && InFlightAir(a))
                    detail.AirborneUnactivatedEnergy += Mathf.Max(0, a.ActivationEnergyCost);

            // ReconAirExecutor's per-turn actor counter (actorsUsed) is only incremented for RECON
            // actors it drives: in-flight wings with a ReconAssignment, then ready standalone recon
            // launches, then storage recon launches. An AirStrike sortie (or an orphan AirRecon
            // task with no assignment) is NEVER counted against MaxAirReconActorsPerTurn — it just
            // makes that aircraft unavailable and owes its own activation Energy. So only airborne
            // recon wings consume a slot here (review round 4, P1).
            int spareSlots = Mathf.Max(0, MaxAirReconActorsPerTurn - airborneReconWings);
            if (spareSlots == 0)
                return detail;

            // Local shared budget: post-reservation AP/Energy minus the airborne first-activation
            // Energy above.
            int apBudget = Mathf.Max(0, root.ActionPoints);
            int energyBudget = Mathf.Max(0,
                AiResourceReservation.Available(root, player, ResourceType.Energy) - detail.AirborneUnactivatedEnergy);

            // Ordered EXACTLY as ReconAirExecutor launches (review round 4, P1): ready standalone
            // wings first, in the executor's own sort, THEN one storage launch subset per owned
            // airfield in OwnedAirfieldHexes order. NOT a global cheapest-first sort — that could
            // "afford" a combination the executor would never reach (a pricey ready wing it meets
            // first eats the budget before a cheap hangar sortie).
            var ordered = new List<AirObservationSlot>();

            foreach (ArmyData a in ownAir
                .Where(a => AviationRules.IsOwnedAirfieldAt(a.Hex, player)
                    && AiTaskRegistry.TaskFor(player, a) == null
                    && a.CurrentMovement > 0)
                .OrderBy(a => a.HasActivatedThisTurn ? 0 : Mathf.Max(0, a.ActivationEnergyCost))
                .ThenBy(a => a.HasActivatedThisTurn ? 0 : Mathf.Max(0, a.ActivationApCost))
                .ThenBy(a => a.Id))
            {
                ordered.Add(new AirObservationSlot(a.Id, default,
                    a.HasActivatedThisTurn ? 0 : Mathf.Max(0, a.ActivationApCost),
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
                ordered.Add(new AirObservationSlot(null, hex,
                    subset.Sum(u => Mathf.Max(0, u.ActivationApCost)),
                    subset.Sum(u => Mathf.Max(0, u.LaunchEnergyCost))));
            }

            foreach (AirObservationSlot slot in ordered)
            {
                if (detail.AcceptedSpareSlots.Count >= spareSlots)
                    break;
                if (slot.Ap > apBudget || slot.Energy > energyBudget)
                    continue;   // executor moves on to the next candidate in order (does not reorder)
                apBudget -= slot.Ap;
                energyBudget -= slot.Energy;
                detail.AcceptedSpareSlots.Add(slot);
            }

            return detail;
        }
    }
}
