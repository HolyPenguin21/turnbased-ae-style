using System.Collections.Generic;
using Game.HexGrid;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  SURVEIL VANTAGE SELECTOR  (Strategy V2 build-order step 6b)
    // ===========================================================================================
    //  Answers ONE question: given a concrete mover, from which on-map hex should it stand to
    //  safely observe a Surveil mission's FocusHex? It never picks a new strategic objective and
    //  never routes — live executability (a safe first step toward the chosen hex) is
    //  ProvisioningManager's check. FocusHex is NEVER a valid answer: Surveil deliberately does
    //  not step onto a stale enemy's last-known hex, and there is no FocusHex fallback.
    //
    //  VALID VANTAGE for THIS mover
    //    * on the map (MapKnowledge.AllHexes) and != FocusHex
    //    * within the mover's REAL vision reach: Distance(hex, FocusHex) <= EffectiveVisionRadius
    //    * not ScoutHardBlocked (a known neutral on it / an active scout-danger cooldown)
    //    * no CURRENTLY-observed (this turn) non-neutral force standing on it
    //  A stale (LastKnown) enemy position is NOT a hard block — it only raises DetectionRisk.
    //  An own army on the hex does NOT block it.
    //
    //  RANK (deterministic — safety before speed)
    //    DetectionRisk ASC -> StandOff DESC -> ETA ASC -> mover->vantage Distance ASC -> (Q,R)
    //  Surveil is a deliberate approach toward a stale enemy position, so one extra turn is worth
    //  it when it clearly lowers known risk.
    // ===========================================================================================
    public readonly struct SurveilVantageCandidate
    {
        public readonly HexCoord ExecutionHex;
        public readonly float DetectionRisk;
        public readonly int StandOff;      // Distance(ExecutionHex, FocusHex) — bigger is safer
        public readonly int Distance;      // mover.Hex -> ExecutionHex
        public readonly int EtaTurns;

        public SurveilVantageCandidate(HexCoord executionHex, float detectionRisk, int standOff, int distance, int etaTurns)
        {
            ExecutionHex = executionHex;
            DetectionRisk = detectionRisk;
            StandOff = standOff;
            Distance = distance;
            EtaTurns = etaTurns;
        }
    }

    public static class SurveilVantageSelector
    {
        public static List<SurveilVantageCandidate> Rank(WorldSnapshot snap, ArmySnapshot mover, ScoutMissionTarget target)
        {
            var result = new List<SurveilVantageCandidate>();
            if (snap?.MapKnowledge?.AllHexes == null || mover == null)
                return result;

            HexCoord focus = target.FocusHex;
            int visionR = mover.EffectiveVisionRadius;
            ISet<HexCoord> hardBlocked = snap.MapKnowledge.ScoutHardBlockedHexes;
            int budget = mover.MaxMovement > 0 ? mover.MaxMovement : 1;

            foreach (HexCoord h in snap.MapKnowledge.AllHexes)
            {
                if (h.Equals(focus))
                    continue;
                int standOff = HexGridMath.Distance(h, focus);
                if (standOff > visionR)
                    continue;
                if (hardBlocked != null && hardBlocked.Contains(h))
                    continue;
                if (CurrentHostileOn(snap, h))
                    continue;

                int dist = HexGridMath.Distance(mover.Hex, h);
                int eta = mover.CurrentMovement >= dist ? 1 : 1 + CeilDiv(dist - mover.CurrentMovement, budget);
                result.Add(new SurveilVantageCandidate(h, ScoutRiskModel.DetectorRisk(snap, h), standOff, dist, eta));
            }

            result.Sort((x, y) =>
            {
                int c = x.DetectionRisk.CompareTo(y.DetectionRisk); if (c != 0) return c;
                c = y.StandOff.CompareTo(x.StandOff); if (c != 0) return c;          // DESC
                c = x.EtaTurns.CompareTo(y.EtaTurns); if (c != 0) return c;
                c = x.Distance.CompareTo(y.Distance); if (c != 0) return c;
                c = x.ExecutionHex.Q.CompareTo(y.ExecutionHex.Q); if (c != 0) return c;
                return x.ExecutionHex.R.CompareTo(y.ExecutionHex.R);
            });
            return result;
        }

        // A currently-observed (SeenTurn >= this turn) non-neutral force standing ON `hex`. A
        // stale sighting is deliberately NOT caught here — it feeds DetectionRisk instead.
        // snap.Known.EnemySightings is already non-neutral only.
        private static bool CurrentHostileOn(WorldSnapshot snap, HexCoord hex)
        {
            var sightings = snap.Known?.EnemySightings;
            if (sightings == null)
                return false;
            foreach (AiMapMemory.KnownEnemySighting s in sightings)
                if (s.Hex.Equals(hex) && s.SeenTurn >= snap.TurnNumber)
                    return true;
            return false;
        }

        private static int CeilDiv(int a, int b) => b <= 0 ? a : (a + b - 1) / b;
    }
}
