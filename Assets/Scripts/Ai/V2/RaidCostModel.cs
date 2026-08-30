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
    //  (envelope check) size a Raid off THIS, so the allocator can never fund a Raid the
    //  provisioner then cannot pay for (pipeline design risk 3).
    //
    //  WHAT A GROUND RAID ACTUALLY COSTS THIS CYCLE (spec §16 / §33):
    //    * AP     — only to ACTIVATE the raid mover (ArmyData.ActivationApCost). Travel is
    //               MOVEMENT, never AP; the engagement itself costs no AP. Strategic-preparation
    //               costs (a hero card, unit cards, equipment) are Phase A's and are ALREADY spent
    //               from the real pool before this is read — MissionRequirements is execution cost
    //               only, never a re-count of Phase A (spec §16 / §20, AC #20).
    //    * Energy / Human / Materials / Tech — 0 for a ground raid. The dimensions exist in the
    //               shared contract; a raid simply does not draw from them (spec §17, AC #16).
    //    * Structural — RequiresArmy, RequiresHero (per Raid policy), CombatPower Min/Desired from
    //               the frozen target projection. These describe WHAT the mission needs;
    //               ProvisioningManager picks the concrete actor.
    // ===========================================================================================
    public static class RaidCostModel
    {
        public static MissionRequirements Build(WorldSnapshot snap, RaidMissionTarget target)
        {
            // AP envelope: the activation of the raid mover. Sized off the CHEAPEST eligible ready
            // ground combat army when one exists, else a notional activation (Provisioning resolves
            // the real one — assembly may also add nothing, an already-activated army costs 0).
            float minAp = AiConfigV2.raidNotionalActivationAp;
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
                    minAp = ready.Min(a => a.HasActivatedThisTurn ? 0 : a.ActivationApCost);
                    int nearest = ready.Min(a => HexGridMath.Distance(a.Hex, target.LastKnownHex));
                    int budget = ready.Max(a => Mathf.Max(1, a.MaxMovement));
                    eta = Mathf.Max(1, CeilDiv(nearest, budget));
                }
            }

            float combatMin = Mathf.Max(0f, target.TargetPower);
            float combatDesired = combatMin * AiConfigV2.raidCombatPowerMargin;

            return new MissionRequirements
            {
                MoverKnown = moverKnown,
                ApMinimum = minAp,
                ApDesired = minAp,
                ApMaximum = Mathf.Max(minAp, AiConfigV2.raidActivationApMax),
                // Energy / Human / Materials / Tech: a ground raid draws nothing — left at 0.
                RequiresArmy = true,
                RequiresHero = target.DefenderCount > 0 && !target.CanCoverAllDefenders
                    ? true
                    : target.DefenderCount > 0,   // a defended raid is hero-led (parity with V1 NeedsHero)
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
