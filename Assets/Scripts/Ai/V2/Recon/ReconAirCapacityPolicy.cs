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
    //  exact primitives ReconAirExecutor uses so the capacity model and executor cannot drift:
    //    · MaxAirReconActorsPerTurn — the per-turn air-recon actor slot cap;
    //    · only already-formed AirExisting wings are candidates;
    //    · activation AP/Energy come from that concrete wing;
    //    · a ready standalone wing is on an owned airfield with no active sortie and MP left.
    //
    //  EvaluateDetailed() enumerates the concrete air slots (airborne wings, then ready standalone
    //  wings only) plus a loose WorldAnalysis-only fallback count from a
    //  raw-stockpile greedy. It is STRUCTURAL throughout: no hand/deck/income reserve, no
    //  "is spending it worthwhile" judgement. That strategic decision has exactly one owner —
    //  ProvisioningManager.AirSortieReservationAdmission -> AviationSortieReservationEvaluator.
    // ===========================================================================================
    internal readonly struct ReconAirObservationCapacity
    {
        // Own air armies already flying a durable ReconPatrolState — active observation lanes the
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

    // Concrete already-formed aviation actor. Recon has no storage-launch slot shape.
    internal readonly struct AirObservationSlot
    {
        public readonly int ActorId;
        public readonly int Ap;
        public readonly int Energy;

        public AirObservationSlot(int actorId, int ap, int energy)
        {
            ActorId = actorId;
            Ap = ap;
            Energy = energy;
        }
    }

    internal sealed class ReconAirObservationDetail
    {
        // Executor-operational in-flight recon wings (Controller != null && CurrentMovement > 0),
        // in the executor's own order, each carrying the first-activation AP/Energy it still owes.
        public readonly List<AirObservationSlot> AirborneWings = new List<AirObservationSlot>();
        // Every ready-standalone-wing candidates, in the exact order the
        // executor would try them. NOT budget-filtered and NOT capped — ReconAirReservationPrepass
        // runs the ONE authoritative greedy (cumulative AP/Energy + AIR-01 route + energy policy)
        // so a route-invalid earlier candidate cannot hide a valid later aircraft.
        public readonly List<AirObservationSlot> SpareCandidatesInOrder = new List<AirObservationSlot>();
        public int ApBudgetBase;                              // root.ActionPoints (structural — no strategic reserve)
        public int EnergyBudgetBase;                          // root Energy stockpile (structural — no strategic reserve)
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

        // The ONE rule for "can aviation service this Recon objective at all": generic Observation
        // only — never Explore/GroundTraversal (a physical visit), never a stealth-Required or
        // positive-DetectionRisk job (air cannot go hidden). Capacity (MeasureAirCapacity),
        // Assignment and aviation Deployment/Rebase valuation (NonCombatCardPlayer.
        // BestAirfieldServiceTaskScore) all read this, so a card is never valued for a job the
        // Recon owner will never hand to an aircraft.
        // Aviation is SUPPORT: it never visits a hex, so it serves only the aviation-only AirSweep
        // observation pass. Generic Refresh / Surveil stay with ground scouts — an aircraft is no
        // longer spent (or valued) on re-checking one hex next to home.
        internal static bool IsAirServiceable(ReconObjective o)
            => o != null
               && o.Kind == ReconObjectiveKind.AirSweep
               && !o.NeedsStealth;

        // The farthest point of a sweep from `from` toward `anchor`: walk the straight hex line
        // (each step to the neighbour closest to the anchor, deterministic tie-break) for `reach`
        // steps, stopping at the anchor. Air movement is flat-cost, so this IS the outbound leg.
        internal static HexCoord SweepEndpoint(HexCoord from, HexCoord anchor, int reach)
        {
            HexCoord cur = from;
            for (int i = 0; i < reach && !cur.Equals(anchor); i++)
            {
                HexCoord best = cur;
                int bestD = int.MaxValue;
                foreach (HexCoord n in HexGridMath.Neighbors(cur))
                {
                    int d = HexGridMath.Distance(n, anchor);
                    if (d < bestD || (d == bestD && (n.Q < best.Q || (n.Q == best.Q && n.R < best.R))))
                    {
                        bestD = d;
                        best = n;
                    }
                }
                cur = best;
            }
            return cur;
        }

        // THE two air-recon actor states (D13 — one owner for capacity, Provisioning and flight
        // recovery), explicitly mutually exclusive by the airfield test:
        //   ReadyStandaloneWing — a formed wing on its own airfield, flying no sortie, MP left.
        //   AirborneReconWing   — off the airfield, controllable, MP left, a durable Recon patrol.
        internal static bool IsReadyStandaloneWing(PlayerSetupData player, ArmyData a) =>
            a != null && AviationRules.IsValidAirArmy(a)
            && AviationRules.IsOwnedAirfieldAt(a.Hex, player)
            && AirSortieRegistry.ForArmy(player, a) == null
            && a.CurrentMovement > 0;

        internal static bool IsAirborneReconWing(PlayerSetupData player, ArmyData a) =>
            a != null && AviationRules.IsValidAirArmy(a)
            && !AviationRules.IsOwnedAirfieldAt(a.Hex, player)
            && a.Controller != null && a.CurrentMovement > 0
            && ReconPatrolStateRegistry.TryGet(player, a.Id, out _);

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
                .Where(a => IsAirborneReconWing(player, a))
                .OrderBy(a => a.Id))
            {
                detail.AirborneWings.Add(new AirObservationSlot(a.Id,
                    a.HasActivatedThisTurn ? 0 : Mathf.Max(0, a.ActivationApCost),
                    a.HasActivatedThisTurn ? 0 : Mathf.Max(0, a.ActivationEnergyCost)));
            }

            // Budget bases for the loose WorldAnalysis fallback greedy below. STRUCTURAL only —
            // raw physical stockpile, NO strategic hand/deck/income reserve. Whether spending Energy
            // on a sortie is worthwhile this turn is decided exclusively at Provisioning time
            // (ProvisioningManager.AirSortieReservationAdmission -> AviationSortieReservationEvaluator);
            // capacity measurement must not pre-judge it or the two authorities drift.
            detail.ApBudgetBase = Mathf.Max(0, root.ActionPoints);
            detail.EnergyBudgetBase = Mathf.Max(0, root.GetResource(ResourceType.Energy));

            // Ready already-formed standalone wings in executor order. Not budget-filtered or
            // capped here; Assignment/Provisioning own those decisions.
            foreach (ArmyData a in ownAir
                .Where(a => IsReadyStandaloneWing(player, a))
                .OrderBy(a => a.HasActivatedThisTurn ? 0 : Mathf.Max(0, a.ActivationEnergyCost))
                .ThenBy(a => a.HasActivatedThisTurn ? 0 : Mathf.Max(0, a.ActivationApCost))
                .ThenBy(a => a.Id))
            {
                detail.SpareCandidatesInOrder.Add(new AirObservationSlot(a.Id,
                    a.HasActivatedThisTurn ? 0 : Mathf.Max(0, a.ActivationApCost),
                    a.HasActivatedThisTurn ? 0 : Mathf.Max(0, a.ActivationEnergyCost)));
            }

            // Recon never materializes aircraft from storage. AirSweep may only use an already
            // formed ready wing or continue an airborne one. Aircraft creation/formation belongs
            // to the separate score-driven aviation systems, never to a mission request.

            // Loose fallback count (WorldAnalysis only): simple cumulative-budget greedy, no route,
            // no strategic reserve — just "how many more sorties do the raw stockpile + slot cap
            // physically allow". Subtract the airborne wings' still-owed first-activation AP/Energy
            // so the same resources are not counted twice.
            int spareSlots = Mathf.Max(0, MaxAirReconActorsPerTurn - detail.AirborneWings.Count);
            int apLeft = detail.ApBudgetBase - detail.AirborneWings.Sum(w => w.Ap);
            int energyLeft = detail.EnergyBudgetBase - detail.AirborneWings.Sum(w => w.Energy);
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
