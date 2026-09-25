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
    //    * not MapKnowledge.IsBlockedForScout for THIS mover — an active scout-danger cooldown for
    //      every scout; for a mover that will not arrive hidden
    //      (ScoutMoverSelector.ArrivesHidden) also any hex whose arrival sets
    //      something off (a known army to fight, a known undefended foreign structure to take
    //      over — a Surveil mission must never do an Aggression action). A fully hidden scout
    //      may share such a hex (stealth design); DetectionRisk ranks it.
    //  An own army on the hex does NOT block it. See ScoutExecutionSafety for the same rule as the
    //  live-memory check provisioning / execution apply.
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
        // Which Scout jobs execute FROM a vantage rather than ON their focus hex: every Surveil
        // (never step onto a stale enemy's last-known hex), and a Refresh whose focus a visible
        // scout may not arrive on (a known army / undefended foreign structure — the Attack
        // observation need on a known hostile site, Recon audit B2). Observation completes by
        // seeing the focus, so standing next to it within vision is the whole job.
        public static bool UsesVantage(WorldSnapshot snap, ScoutMissionTarget target) =>
            target.Kind == ScoutTargetKind.Surveil
            || (ReconScoutKinds.IsRefresh(target.Kind)
                && snap?.MapKnowledge?.IsBlockedForScout(target.FocusHex, stealthCapable: false) == true);

        public static List<SurveilVantageCandidate> Rank(WorldSnapshot snap, ArmySnapshot mover, ScoutMissionTarget target)
        {
            var result = new List<SurveilVantageCandidate>();
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

        private static int CeilDiv(int a, int b) => AiV2Util.CeilDiv(a, b);
    }
}
