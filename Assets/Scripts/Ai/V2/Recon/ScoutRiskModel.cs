using System.Collections.Generic;
using Game.HexGrid;
using UnityEngine;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  SCOUT RISK MODEL  (Strategy V2 build-order step 6b — shared "how exposed is a scout here")
    // ===========================================================================================
    //  One implementation of the detection-risk number, so a Surveil vantage (SurveilVantageSelector)
    //  is scored the exact same way the frontier scan scores an Explore hex. Extracted from
    //  ReconMissionPlanner.CurrentDetectorRisk verbatim — HONEST memory only
    //  (WorldSnapshot.Known.EnemySightings), a "detector" is a known non-neutral force within
    //  AiConfigV2.frontierEnemyExposureRadius that could actually roll a stealth challenge on the
    //  hex (KnownEnemySighting.CanDetectStealthAt). Count-based, normalised by
    //  AiConfigV2.scoutDetectionRiskNorm. Never reads TrueWorld.
    // ===========================================================================================
    public static class ScoutRiskModel
    {
        public static float DetectorRisk(WorldSnapshot snap, HexCoord hex) =>
            DetectorRisk(snap?.Known?.EnemySightings, hex);

        // The same number from any honest sighting list — the frozen snapshot above, or live
        // AiMapMemory at execution time (ReconGroundExecutor's optional-stealth leg risk).
        public static float DetectorRisk(IEnumerable<AiMapMemory.KnownEnemySighting> sightings, HexCoord hex) =>
            Mathf.Clamp01(CountDetectors(sightings, hex) / Mathf.Max(0.0001f, AiConfigV2.scoutDetectionRiskNorm));

        // Known non-neutral forces within frontierEnemyExposureRadius that could roll a stealth
        // challenge on `hex` (KnownEnemySighting.CanDetectStealthAt) — garrisons included.
        public static int CountDetectors(IEnumerable<AiMapMemory.KnownEnemySighting> sightings, HexCoord hex)
        {
            if (sightings == null)
                return 0;
            int r = AiConfigV2.frontierEnemyExposureRadius;
            int detectors = 0;
            foreach (AiMapMemory.KnownEnemySighting s in sightings)
                if (HexGridMath.Distance(s.Hex, hex) <= r && s.CanDetectStealthAt(hex))
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
    }
}
