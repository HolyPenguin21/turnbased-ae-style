using System.Linq;
using Game.HexGrid;
using UnityEngine;

namespace Game.Ai.V2
{
    // Shared physical cost estimator for Raid. If assembly already selected a primary mover, every
    // cost/distance fact is derived from that exact actor; otherwise one deterministic fallback actor
    // is selected and used for all dimensions (never AP from one army, distance from another).
    public static class RaidCostModel
    {
        public static MissionRequirements Build(WorldSnapshot snap, RaidMissionTarget target,
            int? selectedMoverArmyId = null)
        {
            float activationAp = AiConfigV2.raidNotionalActivationAp;
            bool moverKnown = false;
            int eta = Mathf.Max(1, target.EstimatedEta);
            HexCoord destination = target.Phase == RaidMissionPhase.Assault
                ? target.LastKnownHex : target.DestinationHex;
            // No actor means there is no actor-specific route yet. Keep the physical unit honest:
            // use the shared nearest-owned-home hex distance as a notional fallback, never ETA turns.
            int distance = TaskScoreEvaluator.NearestOwnedHomeDistance(snap, destination, 0);

            if (snap?.Self?.Armies != null)
            {
                var ready = snap.Self.Armies
                    .Where(a => a != null && !a.IsPrison && !a.IsAir && !a.IsGarrison && !a.IsSoloRecce
                                && a.MemberCount > 0 && a.CurrentMovement > 0)
                    .ToList();
                ArmySnapshot mover = selectedMoverArmyId.HasValue
                    ? ready.FirstOrDefault(a => a.ArmyId == selectedMoverArmyId.Value)
                    : null;
                if (mover == null)
                    mover = ready
                        .OrderBy(a => HexGridMath.Distance(a.Hex, destination))
                        .ThenBy(a => a.HasActivatedThisTurn ? 0 : a.ActivationApCost)
                        .ThenBy(a => a.ArmyId)
                        .FirstOrDefault();
                if (mover != null)
                {
                    moverKnown = true;
                    activationAp = mover.HasActivatedThisTurn ? 0 : mover.ActivationApCost;
                    distance = HexGridMath.Distance(mover.Hex, destination);
                    eta = Mathf.Max(1, CeilDiv(distance, Mathf.Max(1, mover.MaxMovement)));
                }
            }

            float admissionAp = activationAp <= 0f ? 0f : Mathf.Min(activationAp, 1f);
            float combatMin = Mathf.Max(0f, target.TargetPower);
            float combatDesired = combatMin * AiConfigV2.raidCombatPowerMargin;

            return new MissionRequirements
            {
                MoverKnown = moverKnown,
                ApMinimum = admissionAp,
                ApDesired = activationAp,
                ApMaximum = Mathf.Max(activationAp, AiConfigV2.raidActivationApMax),
                RequiresArmy = true,
                RequiresHero = target.DefenderCount > 0 && !target.CanCoverAllDefenders
                    ? true
                    : target.DefenderCount > 0,
                CombatPowerMinimum = combatMin,
                CombatPowerDesired = combatDesired,
                RequiredCombatTraits = TraitPreference.None,
                EtaTurns = eta,
                EstimatedDistance = distance,
            };
        }

        private static int CeilDiv(int a, int b) => AiV2Util.CeilDiv(a, b);
    }
}
