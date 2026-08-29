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
        public static float DetectorRisk(WorldSnapshot snap, HexCoord hex)
        {
            var sightings = snap?.Known?.EnemySightings;
            if (sightings == null)
                return 0f;
            int r = AiConfigV2.frontierEnemyExposureRadius;
            int detectors = 0;
            foreach (AiMapMemory.KnownEnemySighting s in sightings)
                if (HexGridMath.Distance(s.Hex, hex) <= r && s.CanDetectStealthAt(hex))
                    detectors++;
            return Mathf.Clamp01(detectors / Mathf.Max(0.0001f, AiConfigV2.scoutDetectionRiskNorm));
        }
    }
}
