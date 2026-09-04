using System.Linq;
using Game.HexGrid;
using UnityEngine;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  RAID COST MODEL  (Strategy V2 build-order step 9 — the shared Raid estimator)
    // ===========================================================================================
    //  "ONE ESTIMATOR, MANY STAGES" for the Raid mission type, the Aggression counterpart of
    //  ScoutCostModel. Both AggressionMissionPlanner (MissionRequirements) and ProvisioningManager
    //  (envelope check) size a Raid off THIS, so the allocator can never execute a Raid whose final
    //  funded envelope cannot pay the real mover activation.
    //
    //  AP FUNDING CONTRACT:
    //    * ApMinimum is the axis-admission stake. A fresh Raid must receive real Aggression budget
    //      before it enters the portfolio, but a fractional radar slice is not allowed to become a
    //      hard wall around the entire activation cost.
    //    * ApDesired is the authoritative current-cycle activation cost. Once admitted, the
    //      allocator's existing fungible remainder pass tops the mission toward this amount.
    //    * Provisioning still revalidates the exact mover and refuses an envelope below the real
    //      claim. Therefore this softens ONLY the axis partition; it never creates/spends AP that
    //      the shared pool does not actually contain.
    //
    //  WHAT A GROUND RAID ACTUALLY COSTS THIS CYCLE:
    //    * AP — activation of the raid mover. Travel is MOVEMENT, engagement costs no AP.
    //    * H/E/M/T — 0 for a ground raid; Phase-A preparation was already paid.
    // ===========================================================================================
    public static class RaidCostModel
    {
        public static MissionRequirements Build(WorldSnapshot snap, RaidMissionTarget target)
        {
            float activationAp = AiConfigV2.raidNotionalActivationAp;
            bool moverKnown = false;
            int eta = Mathf.Max(1, target.EstimatedEta);
            if (snap?.Self?.Armies != null)
            {
                var ready = snap.Self.Armies
                    .Where(a => a != null && !a.IsPrison && !a.IsAir && !a.IsGarrison && !a.IsSoloRecce
                                && a.MemberCount > 0 && a.CurrentMovement > 0)
                    .ToList();
                if (ready.Count > 0)
                {
                    moverKnown = true;
                    activationAp = ready.Min(a => a.HasActivatedThisTurn ? 0 : a.ActivationApCost);
                    int nearest = ready.Min(a => HexGridMath.Distance(a.Hex, target.LastKnownHex));
                    int budget = ready.Max(a => Mathf.Max(1, a.MaxMovement));
                    eta = Mathf.Max(1, CeilDiv(nearest, budget));
                }
            }

            // Axis partition is a preference, not a second physical AP pool. One AP of genuine AGG
            // entitlement admits the mission; the existing global remainder must then pay the rest
            // before Provisioning can activate the actor. Already-activated armies need no stake.
            float admissionAp = activationAp <= 0f ? 0f : Mathf.Min(activationAp, 1f);

            float combatMin = Mathf.Max(0f, target.TargetPower);
            float combatDesired = combatMin * AiConfigV2.raidCombatPowerMargin;

            return new MissionRequirements
            {
                MoverKnown = moverKnown,
                ApMinimum = admissionAp,
                ApDesired = activationAp,
                ApMaximum = Mathf.Max(activationAp, AiConfigV2.raidActivationApMax),
                // Energy / Human / Materials / Tech: a ground raid draws nothing — left at 0.
                RequiresArmy = true,
                RequiresHero = target.DefenderCount > 0 && !target.CanCoverAllDefenders
                    ? true
                    : target.DefenderCount > 0,
                CombatPowerMinimum = combatMin,
                CombatPowerDesired = combatDesired,
                RequiredCombatTraits = TraitPreference.None,
                EtaTurns = eta,
                EstimatedDistance = target.EstimatedEta,
            };
        }

        private static int CeilDiv(int a, int b) => b <= 0 ? a : (a + b - 1) / b;
    }
}
