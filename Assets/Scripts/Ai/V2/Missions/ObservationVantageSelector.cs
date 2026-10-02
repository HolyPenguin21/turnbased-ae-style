using System.Collections.Generic;
using Game.HexGrid;

namespace Game.Ai.V2
{
    // Choose a safe observation position for Refresh when arriving on its focus would
    // initiate combat or capture. The focus itself is never an execution vantage.
    public readonly struct ObservationVantageCandidate
    {
        public readonly HexCoord ExecutionHex;
        public readonly float DetectionRisk;
        public readonly int StandOff;      // Distance(ExecutionHex, FocusHex) — bigger is safer
        public readonly int Distance;      // mover.Hex -> ExecutionHex
        public readonly int EtaTurns;

        public ObservationVantageCandidate(HexCoord executionHex, float detectionRisk, int standOff, int distance, int etaTurns)
        {
            ExecutionHex = executionHex;
            DetectionRisk = detectionRisk;
            StandOff = standOff;
            Distance = distance;
            EtaTurns = etaTurns;
        }
    }

    public static class ObservationVantageSelector
    {
        public static bool UsesVantage(WorldSnapshot snap, ScoutMissionTarget target) =>
            ReconScoutKinds.IsRefresh(target.Kind)
            && snap?.MapKnowledge?.IsBlockedForScout(target.FocusHex, stealthCapable: false) == true;

        public static List<ObservationVantageCandidate> Rank(WorldSnapshot snap, ArmySnapshot mover, ScoutMissionTarget target)
        {
            var result = new List<ObservationVantageCandidate>();
            if (snap?.MapKnowledge?.AllHexes == null || mover == null)
                return result;

            HexCoord focus = target.FocusHex;
            int visionR = mover.EffectiveVisionRadius;
            // Spec §19 — a mover that will stand fully hidden there ignores occupancy when
            // choosing a vantage; mere stealth capability is not enough (provisioning and the
            // execution gate check the hidden state the mover will actually arrive in).
            bool arrivesHidden = ScoutMoverSelector.ArrivesHidden(mover, target);
            int budget = mover.MaxMovement > 0 ? mover.MaxMovement : 1;

            foreach (HexCoord h in snap.MapKnowledge.AllHexes)
            {
                if (h.Equals(focus))
                    continue;
                int standOff = HexGridMath.Distance(h, focus);
                if (standOff > visionR)
                    continue;
                // The one arrival rule (snapshot form): danger zones for every scout; a known army
                // or undefended foreign structure only for a scout that cannot go hidden.
                if (snap.MapKnowledge.IsBlockedForScout(h, arrivesHidden))
                    continue;

                int dist = AiV2Util.TravelCost(snap, mover, h, arrivesHidden);
                if (dist == int.MaxValue) continue;
                int eta = ScoutCostModel.TravelTurns(mover.CurrentMovement, dist, budget);
                result.Add(new ObservationVantageCandidate(h, ScoutRiskModel.DetectorRisk(snap, h), standOff, dist, eta));
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

    }
}
