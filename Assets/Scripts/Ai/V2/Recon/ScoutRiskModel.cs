using System.Collections.Generic;
using System.Linq;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using UnityEngine;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  SCOUT RISK MODEL  (Strategy V2 build-order step 6b — shared "how exposed is a scout here")
    // ===========================================================================================
    //  STEP SAFETY — the ONE Recon rule for "may a scout step onto / stand on this hex". Every
    //  per-step Recon choice (ReconGroundStepPlanner, ReconReactionPolicy) and every live vantage
    //  check (ProvisioningManager, MissionRevalidator) asks StepBlocked, so a Recon choice can never
    //  be one the execution gate (AiTurnController.MoveArmyRoutine) then rejects — the class of bug
    //  that strands a scout re-picking the same refused hex every turn.
    //  A hex is BLOCKED for a scout when its terrain cannot be entered, or when arriving there
    //  would set something off — AiMapMemory.KnownGroundArrival, the single AI arrival rule the
    //  execution gate itself uses: a known army fights a visible scout, a known undefended
    //  foreign structure is taken over by it. A FULLY hidden scout sets neither off and may share
    //  the hex (stealth design). A scout never seeks either.
    //  Hex Events are not blocks: a Transit scout always Skips them and keeps stealth/movement.
    //  `moverFullyHidden` is the mover's own stealth state as it will move (a planned stealth
    //  entry counts: callers pass requiresStealth for a mission that enters stealth first). For a
    //  vantage the scout must reach and stand on, pass ScoutMoverSelector.ArrivesHiddenLive, never
    //  the mission's bare stealth wish: the gate judges the scout's real hidden state.
    //
    //  DETECTOR RISK — a hex's detection risk is scored the exact same way the frontier scan
    //  scores an Explore hex. HONEST memory only (WorldSnapshot.Known.EnemySightings or live
    //  AiMapMemory): a "detector" is a known non-neutral force that could actually roll a stealth
    //  challenge on the hex (KnownEnemySighting.CanDetectStealthAt: same hex, or adjacent with its
    //  own Recce). Count-based, normalised by AiConfigV2.scoutDetectionRiskNorm. Never reads TrueWorld.
    //
    //  STEALTH THREAT — the ONE rule for "does hiding protect a solo scout here", shared by the
    //  Recon executor's optional stealth and the shared mover's automatic stealth for every other
    //  move of a solo Recce: a force that can come and engage it (IsExposed) is a full threat,
    //  otherwise the detectors that could pierce its stealth.
    // ===========================================================================================
    public static class ScoutRiskModel
    {
        public static bool StepBlocked(PlayerSetupData player, HexCoord hex, bool moverFullyHidden)
            => AiMapMemory.GroundTerrainBlocked(hex)
               || AiMapMemory.KnownGroundArrival(player, hex, moverFullyHidden).HasOutcome;

        public static bool StepBlocked(PlayerSetupData player, ArmyData mover, HexCoord hex)
            => StepBlocked(player, hex, StealthSystem.IsArmyFullyHidden(mover));

        public static float DetectorRisk(WorldSnapshot snap, HexCoord hex) =>
            DetectorRisk(snap?.Known?.EnemySightings, hex);

        // AllKnownEnemySightings excludes neutral/ownerless encounters, exactly as the old
        // near-memory consumers did.
        public static float DetectorRiskLive(PlayerSetupData player, HexCoord hex) =>
            DetectorRisk(AiMapMemory.AllKnownEnemySightings(player), hex);

        // The same number from any honest sighting list — the frozen snapshot above, or live
        // AiMapMemory at execution time (ReconGroundExecutor's optional-stealth leg risk).
        public static float DetectorRisk(IEnumerable<AiMapMemory.KnownEnemySighting> sightings, HexCoord hex) =>
            Mathf.Clamp01(CountDetectors(sightings, hex) / Mathf.Max(0.0001f, AiConfigV2.scoutDetectionRiskNorm));

        // Known non-neutral forces that could roll a stealth challenge on `hex`
        // (KnownEnemySighting.CanDetectStealthAt owns the range) — garrisons included.
        public static int CountDetectors(IEnumerable<AiMapMemory.KnownEnemySighting> sightings, HexCoord hex)
        {
            if (sightings == null)
                return 0;
            int detectors = 0;
            foreach (AiMapMemory.KnownEnemySighting s in sightings)
                if (s.CanDetectStealthAt(hex))
                    detectors++;
            return detectors;
        }

        // Exposure = a known non-neutral force within frontierEnemyExposureRadius that can come and
        // engage the scout. A building-bound garrison cannot (audit F1); its Recce still counts as a
        // detector above. The ONE rule for the frontier annotation and the Explore/Refresh scan.
        public static bool IsExposed(IEnumerable<AiMapMemory.KnownEnemySighting> sightings, HexCoord hex)
        {
            if (sightings == null)
                return false;
            int r = AiConfigV2.frontierEnemyExposureRadius;
            foreach (AiMapMemory.KnownEnemySighting s in sightings)
                if (!s.IsGarrison && HexGridMath.Distance(s.Hex, hex) <= r)
                    return true;
            return false;
        }

        public static float StealthThreat(IEnumerable<AiMapMemory.KnownEnemySighting> sightings, HexCoord hex) =>
            IsExposed(sightings, hex) ? 1f : DetectorRisk(sightings, hex);

        // Automatic stealth of the shared mover (AiTurnController.MoveArmyRoutine) for a solo Recce
        // that no Recon executor moves: hide before a step out of or into a threatened hex. The
        // Recon executor prices the same threat itself
        // (ScoutOptionalStealthPolicy) and opts out of this one.
        public static bool MoveWarrantsStealth(PlayerSetupData player, ArmyData army, HexCoord nextStep)
        {
            if (army == null)
                return false;
            var sightings = AiMapMemory.AllKnownEnemySightings(player).ToList();
            return StealthThreat(sightings, army.Hex) > 0f || StealthThreat(sightings, nextStep) > 0f;
        }
    }
}
