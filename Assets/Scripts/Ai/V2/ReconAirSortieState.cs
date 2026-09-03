using System.Collections.Generic;
using Game.HexGrid;
using Game.Players;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  PER-SORTIE AIR RECON STATE  (spec §33 / §34 / §38 / §48)
    // ===========================================================================================
    //  A ground ReconAssignment is a durable, cross-turn actor identity. An air sortie is not: it
    //  is one launch -> boomerang -> landing arc, and its trail / phase / chosen-landing reset the
    //  moment the wing lands (or the sortie is lost). This state therefore lives beside
    //  ReconAssignmentRegistry, keyed by army id, and is retired on exactly the same events.
    //
    //  Phases (spec §34 / AI-AIR-02):
    //    Outbound  press toward useful information; boomerang shaping applies here
    //    Turning   the one pivot step — logged, then immediately Return
    //    Hold      aloft on purpose, ending THIS turn here — re-decide next turn (AI-AIR-02: a
    //              helicopter with a real TurnsWithoutRefuel margin turns its two-turn endurance
    //              into a tactical window instead of boomeranging home turn 1)
    //    Return    safe landing is the priority; only free en-route refresh, never an unsafe detour
    //    Landing   the wing is on an owned airfield; the sortie is over
    // ===========================================================================================
    internal enum ReconAirPhase { Outbound, Turning, Hold, Return, Landing }

    // AI-AIR-02 — what this sortie is actually for, so telemetry (and a later strike-first
    // planner) can tell a pure recon flight from one that has already revealed itself with a
    // strike. Never gates gameplay rules — those stay AviationRules'/AviationActions' job.
    internal enum ReconAirMissionMode { Recon, Strike, ReconStrike }

    internal sealed class ReconAirSortieState
    {
        // Stable per-sortie identity (one launch -> landing arc). Used so AirReconCoverageRegistry
        // can tell "a hex MY current sortie just swept" from "a hex another aircraft swept" — an
        // army id would be reused across a wing's successive sorties.
        public int SortieId;

        public ReconAirPhase Phase = ReconAirPhase.Outbound;
        public HexCoord LaunchHex;
        public HexCoord ChosenLandingHex;
        public bool HasChosenLanding;

        // Coarse sector this sortie is currently refreshing, measured from its launch hex. Other
        // sorties penalise stepping into a sector already claimed here (spec §49 — no two aircraft
        // grinding the same stale corridor / forming the same boomerang).
        public ReconSector ClaimedSector;
        public bool HasClaim;

        // Best single Outbound step score seen this sortie — the marginal-gain Turning trigger
        // compares each later step against a fraction of this (spec §34: "marginal information gain
        // снизился").
        public float BestOutboundStepScore;

        // ===================================================================================
        //  AI-AIR-02 PERSISTENT SORTIE PLAN — the durable bits of the spec's AirSortiePlan that
        //  are not already covered by an existing aviation rule. Endurance itself
        //  (TurnsWithoutRefuel / ConsecutiveUnlandedEnds / HasAirAttackedThisTurn) and landing
        //  feasibility are NEVER duplicated here — they are read live from AviationRules /
        //  AiAviationSupport every decision.
        // ===================================================================================
        public int LaunchTurn = -1;                                 // turn the wing left the airfield
        public int AirborneTurnIndex;                               // 0 on the launch turn, +1 each further AI turn aloft
        public int LastProcessedTurn = -1;                          // guards AirborneTurnIndex against a double bump within one turn
        public ReconAirMissionMode MissionMode = ReconAirMissionMode.Recon;
        public bool MustRecoverThisTurn;                            // real endurance deadline reached — Return is a hard priority this turn
        public string LastDecisionReason;                           // one-line "why" for the last airborne decision (telemetry)

        // Advance the airborne-turn counter exactly once per AI turn this sortie is processed.
        // Returns true on the first call of a NEW turn so the caller can re-open a Hold.
        public bool BeginTurn(int turn)
        {
            if (turn == LastProcessedTurn)
                return false;
            if (LastProcessedTurn >= 0)
                AirborneTurnIndex++;
            LastProcessedTurn = turn;
            return true;
        }

        // Ordered hexes this sortie has occupied, launch hex first. Used only as a soft boomerang
        // nudge (spec §48) — safety always wins, a straight retrace is allowed when it is the only
        // safe way home.
        public readonly List<HexCoord> Trail = new List<HexCoord>();

        public void RecordStep(HexCoord hex)
        {
            if (Trail.Count == 0 || !Trail[Trail.Count - 1].Equals(hex))
                Trail.Add(hex);
        }

        // How many OLD trail hexes sit within one hex of `hex` — a candidate step that hugs the
        // way out is penalised by this count (spec §48 outbound-trail overlap). `currentPos` (the
        // wing's hex right now) is excluded: every legal next step is adjacent to it by
        // definition, so counting it taxed a ready wing's very first step off its airfield
        // (Trail == [airfield], currentPos == airfield) while a storage launch — scored with no
        // sortie state — paid nothing. Real anti-retrace shaping (proximity to EARLIER trail
        // hexes) is unaffected.
        public int TrailAdjacency(HexCoord hex, HexCoord currentPos)
        {
            int n = 0;
            foreach (HexCoord t in Trail)
                if (!t.Equals(currentPos) && HexGridMath.Distance(t, hex) <= 1)
                    n++;
            return n;
        }
    }

    internal static class ReconAirSortieRegistry
    {
        private static readonly Dictionary<PlayerSetupData, Dictionary<int, ReconAirSortieState>> ByPlayer =
            new Dictionary<PlayerSetupData, Dictionary<int, ReconAirSortieState>>();
        private static int _nextSortieId = 1;

        public static void ClearAll()
        {
            ByPlayer.Clear();
            _nextSortieId = 1;
        }

        public static ReconAirSortieState GetOrCreate(PlayerSetupData player, int armyId, HexCoord launchHex)
        {
            if (player == null)
                return new ReconAirSortieState { LaunchHex = launchHex, SortieId = _nextSortieId++ };
            if (!ByPlayer.TryGetValue(player, out Dictionary<int, ReconAirSortieState> byArmy))
                ByPlayer[player] = byArmy = new Dictionary<int, ReconAirSortieState>();
            if (!byArmy.TryGetValue(armyId, out ReconAirSortieState state))
            {
                state = new ReconAirSortieState { LaunchHex = launchHex, SortieId = _nextSortieId++ };
                state.RecordStep(launchHex);
                byArmy[armyId] = state;
            }
            return state;
        }

        public static bool TryGet(PlayerSetupData player, int armyId, out ReconAirSortieState state)
        {
            state = null;
            return player != null
                && ByPlayer.TryGetValue(player, out Dictionary<int, ReconAirSortieState> byArmy)
                && byArmy.TryGetValue(armyId, out state);
        }

        // How many OTHER active air sorties are currently claiming `sector` (spec §49 coverage
        // deconfliction). Never a hard reservation — a step into a claimed sector is only penalised.
        public static int OtherSectorClaims(PlayerSetupData player, int armyId, ReconSector sector)
        {
            if (player == null || !ByPlayer.TryGetValue(player, out Dictionary<int, ReconAirSortieState> byArmy))
                return 0;
            int n = 0;
            foreach (KeyValuePair<int, ReconAirSortieState> kv in byArmy)
                if (kv.Key != armyId && kv.Value.HasClaim && kv.Value.ClaimedSector == sector)
                    n++;
            return n;
        }

        public static void Retire(PlayerSetupData player, int armyId)
        {
            if (player != null && ByPlayer.TryGetValue(player, out Dictionary<int, ReconAirSortieState> byArmy))
                byArmy.Remove(armyId);
        }
    }

    // ===========================================================================================
    //  RECENT AIR-RECON COVERAGE  (AI-AIR-01 §5 — "repeats a recently completed air observation")
    // ===========================================================================================
    //  Every completed air step stamps its whole observed vision footprint here, tagged with the
    //  SORTIE that saw it. AirReconRouteScorer's redundancy term / hard reject then asks
    //  "was this corridor hex recently covered by a DIFFERENT sortie" — so a wing does not block
    //  its own advance on the footprint it just laid down (an r1-Recce aircraft footprints all six
    //  neighbours of its next candidate step), while two different aircraft — including a second
    //  wing the same turn — still can't grind the same ground. Turn-scoped by the same
    //  airReconTargetCooldownTurns window V1's AirReconTargets uses; never marks a hex observed.
    //
    //  R3 review fix — coverage is stored PER SORTIE per hex (sortieId -> lastTurn), not
    //  last-writer-wins. A single (turn, sortieId) cell meant sortie B footprinting a hex sortie A
    //  had just swept ERASED A's evidence; B then excluding its own id hid the fact the hex was
    //  recently covered at all. Keeping every recent distinct sortie source makes "a DIFFERENT
    //  sortie still counts" actually hold. Sources older than the cooldown are pruned lazily.
    // ===========================================================================================
    internal static class AirReconCoverageRegistry
    {
        // player -> hex -> (sortieId -> last turn that sortie air-observed the hex)
        private static readonly Dictionary<PlayerSetupData, Dictionary<HexCoord, Dictionary<int, int>>> ByPlayer =
            new Dictionary<PlayerSetupData, Dictionary<HexCoord, Dictionary<int, int>>>();

        public static void ClearAll() => ByPlayer.Clear();

        public static void Record(PlayerSetupData player, HexCoord hex, int turn, int sortieId)
        {
            if (player == null)
                return;
            if (!ByPlayer.TryGetValue(player, out Dictionary<HexCoord, Dictionary<int, int>> byHex))
                ByPlayer[player] = byHex = new Dictionary<HexCoord, Dictionary<int, int>>();
            if (!byHex.TryGetValue(hex, out Dictionary<int, int> bySortie))
                byHex[hex] = bySortie = new Dictionary<int, int>();
            bySortie[sortieId] = turn;
            if (bySortie.Count > 1)
            {
                List<int> stale = null;
                foreach (KeyValuePair<int, int> kv in bySortie)
                    if (turn - kv.Value >= AiConfig.airReconTargetCooldownTurns)
                        (stale ??= new List<int>()).Add(kv.Key);
                if (stale != null)
                    foreach (int s in stale)
                        bySortie.Remove(s);
            }
        }

        // True when `hex` was air-observed within `cooldownTurns` by ANY sortie OTHER than
        // `excludeSortieId` (pass -1 to exclude nothing — e.g. a not-yet-launched storage candidate).
        public static bool RecentlyCoveredByOther(PlayerSetupData player, HexCoord hex, int currentTurn,
            int cooldownTurns, int excludeSortieId)
        {
            if (player == null
                || !ByPlayer.TryGetValue(player, out Dictionary<HexCoord, Dictionary<int, int>> byHex)
                || !byHex.TryGetValue(hex, out Dictionary<int, int> bySortie))
                return false;
            foreach (KeyValuePair<int, int> kv in bySortie)
                if (kv.Key != excludeSortieId && currentTurn - kv.Value < cooldownTurns)
                    return true;
            return false;
        }
    }
}
