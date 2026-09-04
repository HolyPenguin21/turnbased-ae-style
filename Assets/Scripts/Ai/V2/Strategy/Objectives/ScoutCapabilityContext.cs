using Game.HexGrid;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  SCOUT CAPABILITY CONTEXT  (Strategy V2 — Strategic Manager, Capability Quality)
    // ===========================================================================================
    //  The SMALL, typed, mission-shaped context a Recon AxisDemand may carry so StrategicManager
    //  can judge the QUALITY of a Scout materialization in the setting the demand was raised for —
    //  not just "does this card scout / does it carry the preferred trait".
    //
    //  It carries FACTS about the work, never a card choice: expected work kind, detection
    //  pressure, how dark the reachable map still is, and how much fresh information the focus
    //  frontier hex would open. DemandLayer fills it from the SAME frozen ReconObjective the rest
    //  of the Recon pipeline reads; it must never reference a concrete CardData / card name / a
    //  pre-computed card score.
    // ===========================================================================================
    public sealed class ScoutCapabilityContext
    {
        // The reachable-but-unwalked fraction of the whole map (MapKnowledgeSnapshot
        // .ExplorableUnknownFrac). Scales the marginal value of extra mobility and extra vision:
        // ~1 on a wide-open board, ~0 once there is almost nothing left to find on foot.
        public float ExplorableUnknownFrac;

        // Fresh (on-map, unvisited) neighbours the focus frontier hex itself would open. A high
        // number means a wider Recce radius genuinely reveals more here; a low number means radius
        // 2 barely beats radius 1 at this spot.
        public int FocusFreshNeighbors;

        // Honest pressure that THIS scout may be detected around the focus hex, [0..1]
        // (ReconObjective.DetectionRisk). This informs stealth protection, not Recce spot strength.
        public float DetectionRisk;

        // The work is specifically about re-acquiring / observing a potentially hidden target.
        // Recce spot strength detects OTHER hidden units; it does NOT protect our scout from enemy
        // detectors, so an exposed Explore does not become spot-relevant merely because its own
        // DetectionRisk is high. Today Surveil is the only such objective.
        public bool DetectionRelevant;

        public HexCoord? FocusHex;

        public static ScoutCapabilityContext FromReconObjective(ReconObjective o, WorldSnapshot snap)
        {
            if (o == null)
                return null;
            float darkFrac = snap?.MapKnowledge != null ? snap.MapKnowledge.ExplorableUnknownFrac : 0.5f;
            return new ScoutCapabilityContext
            {
                ExplorableUnknownFrac = UnityEngine.Mathf.Clamp01(darkFrac),
                FocusFreshNeighbors = o.FreshNeighbors,
                DetectionRisk = UnityEngine.Mathf.Clamp01(o.DetectionRisk),
                DetectionRelevant = o.Kind == ReconObjectiveKind.Surveil,
                FocusHex = o.FocusHex,
            };
        }
    }
}
