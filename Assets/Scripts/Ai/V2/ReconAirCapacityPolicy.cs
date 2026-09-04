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
    //    · AviationSupport.CanAffordLaunch semantics — AP + reservation-net Energy;
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
        // Executor-operational in-flight recon wings (Controller != null && CurrentMovement > 0),
        // in the executor's own order, each carrying the first-activation AP/Energy it still owes.
        public readonly List<AirObservationSlot> AirborneWings = new List<AirObservationSlot>();
        // Every ready-standalone-wing then hangar-launch-subset candidate, in the exact order the
        // executor would try them. NOT budget-filtered and NOT capped — ReconAirReservationPrepass
        // runs the ONE authoritative greedy (cumulative AP/Energy + AIR-01 route + energy policy)
        // so a route-invalid earlier candidate cannot hide a valid later aircraft.
        public readonly List<AirObservationSlot> SpareCandidatesInOrder = new List<AirObservationSlot>();
        public int ApBudgetBase;                              // root.ActionPoints
        public int EnergyBudgetBase;                          // root Energy − ReconAirEnergyPolicy hard reserve
        public int AirborneReconWings => AirborneWings.Count;
        // Loose upper bound for the WorldAnalysis fallback only (real capacity is what the prepass pins).
        public int SpareSorties;
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

            // Executor-operational in-flight recon wings, in id order (the executor's `active` sort).
            // A wing without a Controller / with no movement left is NOT guaranteed capacity — it is
            // stuck, and the executor will not drive it this turn.
            foreach (ArmyData a in ownAir
                .Where(a => !AviationRules.IsOwnedAirfieldAt(a.Hex, player)
                    && a.Controller != null && a.CurrentMovement > 0
                    && ReconAssignmentRegistry.TryGet(player, a.Id, out _))
                .OrderBy(a => a.Id))
            {
                detail.AirborneWings.Add(new AirObservationSlot(a.Id, default,
                    a.HasActivatedThisTurn ? 0 : Mathf.Max(0, a.ActivationApCost),
                    a.HasActivatedThisTurn ? 0 : Mathf.Max(0, a.ActivationEnergyCost)));
            }

            // Budget bases the prepass runs its ONE greedy against. Energy base nets out the
            // ReconAirEnergyPolicy hard reserve (committed air + playable high-value hand card +
            // near-term draw). Read the stockpile directly (like ReconAirEnergyPolicy) — NOT
            // AiResourceReservation.Available, whose V2 hook is the recon-air reservation itself.
            detail.ApBudgetBase = Mathf.Max(0, root.ActionPoints);
            ReconAirEnergyDecision reserveProbe = ReconAirEnergyPolicy.Evaluate(player, root, null, 0, 999f, -1);
            int energyReserve = reserveProbe.Committed + reserveProbe.ProtectedHand + reserveProbe.ProtectedDeck;
            detail.EnergyBudgetBase = Mathf.Max(0,
                Mathf.Max(0, root.GetResource(ResourceType.Energy)) - energyReserve);

            // Spare candidates in the EXACT order ReconAirExecutor tries them: ready standalone
            // wings first (executor sort), then one hangar launch subset per owned airfield in
            // OwnedAirfieldHexes order. Not budget-filtered / not capped — the prepass owns that.
            foreach (ArmyData a in ownAir
                .Where(a => AviationRules.IsOwnedAirfieldAt(a.Hex, player)
                    && AiTaskRegistry.TaskFor(player, a) == null
                    && a.CurrentMovement > 0)
                .OrderBy(a => a.HasActivatedThisTurn ? 0 : Mathf.Max(0, a.ActivationEnergyCost))
                .ThenBy(a => a.HasActivatedThisTurn ? 0 : Mathf.Max(0, a.ActivationApCost))
                .ThenBy(a => a.Id))
            {
                detail.SpareCandidatesInOrder.Add(new AirObservationSlot(a.Id, default,
                    a.HasActivatedThisTurn ? 0 : Mathf.Max(0, a.ActivationApCost),
                    a.HasActivatedThisTurn ? 0 : Mathf.Max(0, a.ActivationEnergyCost)));
            }

            foreach (HexCoord hex in AviationSupport.OwnedAirfieldHexes(player))
            {
                ArmyData airfield = AviationRules.FindAirfieldAt(hex, player);
                if (airfield == null
                    || airfield.Members.Count < Mathf.Max(1, AiConfig.aviationLaunchMinReadyAircraft))
                    continue;
                List<UnitData> subset = SelectReconLaunchSubset(airfield.Members);
                if (subset.Count == 0)
                    continue;
                detail.SpareCandidatesInOrder.Add(new AirObservationSlot(null, hex,
                    subset.Sum(u => Mathf.Max(0, u.ActivationApCost)),
                    subset.Sum(u => Mathf.Max(0, u.LaunchEnergyCost))));
            }

            // Loose fallback count (WorldAnalysis only): simple cumulative-budget greedy, no route.
            // EnergyBudgetBase already nets the airborne wings' owed Energy via the policy's
            // `committed` term — do NOT subtract it again. AP is not pre-committed, so it is.
            int spareSlots = Mathf.Max(0, MaxAirReconActorsPerTurn - detail.AirborneWings.Count);
            int apLeft = detail.ApBudgetBase - detail.AirborneWings.Sum(w => w.Ap);
            int energyLeft = detail.EnergyBudgetBase;
            foreach (AirObservationSlot slot in detail.SpareCandidatesInOrder)
            {
                if (detail.SpareSorties >= spareSlots) break;
                if (slot.Ap > apLeft || slot.Energy > energyLeft) continue;
                apLeft -= slot.Ap; energyLeft -= slot.Energy;
                detail.SpareSorties++;
            }

            return detail;
        }
    }
}
