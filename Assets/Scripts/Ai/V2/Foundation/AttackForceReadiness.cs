using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Game.Combat;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using UnityEngine;

namespace Game.Ai.V2
{
    internal static class AttackForceReadiness
    {
        // Strike force step 4 — Attack readiness from the SelfSnapshot force measures. Not a gate:
        // how much of the available force already stands in one fist (assembly: Fist / P_field) and
        // how much of the reachable ceiling is already on the map (deployment: P_field against
        // P_deck plus the equipment still in hand/deck). Aviation is parallel support and stays out.
        // 2026-09-30 (user decision) — once the mobilization gate is open the force IS ready to
        // mobilize: the slot is full, so every preparation step (MoveHost first) keeps Attack's
        // priority against fresh work instead of being starved by it.
        internal static float Readiness(SelfSnapshot self)
        {
            if (self == null)
                return 0f;
            if (MobilizationOpen(self))
                return 1f;
            float assembly = self.FistPower / Mathf.Max(1f, self.FieldPotential);
            float deployment = self.FieldPotential
                / Mathf.Max(1f, self.TotalMilitaryPotential + self.Reserve.Equipment);
            return Curves.Ramp(assembly, AiConfigV2.attackAssemblyReadyLo, AiConfigV2.attackAssemblyReadyHi)
                * Curves.Ramp(deployment, AiConfigV2.attackDeploymentReadyLo, AiConfigV2.attackDeploymentReadyHi);
        }

        // Admission uses the current ground-only deck/map ceiling. Strict inequality is
        // intentional: an army at exactly four fifths still prepares.
        internal static bool ForceReady(float attackArmyPower, float currentDeckPeakPower) =>
            currentDeckPeakPower > 0f && attackArmyPower > RequiredPower(currentDeckPeakPower);

        // Mobilization opens a new Attack preparation (never a march). Two independent starts
        // (2026-09-30, user decision); the march itself keeps ForceReady's strict > 80%:
        //  (A) deck share — at least three quarters of the additive live + hand + remaining-deck
        //      ground force is already on the map (PlayerForceAnalysis scale). Inclusive; written
        //      as 4·deployed >= 3·available so exactly three quarters (135 of 180) is not lost to
        //      the binary rounding of 0.75f.
        //  (B) field strike force — the bodies already on the field can form the strike army:
        //      SelfSnapshot.FieldStrikePotential (no active scouts, aviation, heroes' own power or
        //      mandatory garrison defence; Raid / ActiveDefence armies count — they come back)
        //      clears the same ForceReady bar on the current deck peak.
        internal static bool MobilizationOpen(float deployedPower, float availablePower) =>
            availablePower > 0f && 4f * deployedPower >= 3f * availablePower;

        internal static bool FieldStrikeForceReady(float fieldStrikePotential, float currentDeckPeakPower) =>
            ForceReady(fieldStrikePotential, currentDeckPeakPower);

        internal static bool MobilizationOpen(SelfSnapshot self) =>
            self != null && (MobilizationRawOpen(self) || self.MobilizationHeld);

        // The gate as measured this pass, without the hysteresis hold.
        internal static bool MobilizationRawOpen(SelfSnapshot self) =>
            self != null && (MobilizationOpen(self.DeployedPower, self.AvailablePower)
                || FieldStrikeForceReady(self.FieldStrikePotential, self.AttackPeak));

        internal static float RequiredPower(float currentDeckPeakPower) =>
            0.80f * currentDeckPeakPower;

    }
}
