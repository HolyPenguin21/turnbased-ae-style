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
    //  Phases (spec §34):
    //    Outbound  press toward useful information; boomerang shaping applies here
    //    Turning   the one pivot step — logged, then immediately Return
    //    Return    safe landing is the priority; only free en-route refresh, never an unsafe detour
    //    Landing   the wing is on an owned airfield; the sortie is over
    // ===========================================================================================
    internal enum ReconAirPhase { Outbound, Turning, Return, Landing }

    internal sealed class ReconAirSortieState
    {
        public ReconAirPhase Phase = ReconAirPhase.Outbound;
        public HexCoord LaunchHex;
        public HexCoord ChosenLandingHex;
        public bool HasChosenLanding;

        // Best single Outbound step score seen this sortie — the marginal-gain Turning trigger
        // compares each later step against a fraction of this (spec §34: "marginal information gain
        // снизился").
        public float BestOutboundStepScore;

        // Ordered hexes this sortie has occupied, launch hex first. Used only as a soft boomerang
        // nudge (spec §48) — safety always wins, a straight retrace is allowed when it is the only
        // safe way home.
        public readonly List<HexCoord> Trail = new List<HexCoord>();

        public void RecordStep(HexCoord hex)
        {
            if (Trail.Count == 0 || !Trail[Trail.Count - 1].Equals(hex))
                Trail.Add(hex);
        }

        // How many trail hexes sit within one hex of `hex` — a candidate step that hugs the way
        // out is penalised by this count (spec §48 outbound-trail overlap).
        public int TrailAdjacency(HexCoord hex)
        {
            int n = 0;
            foreach (HexCoord t in Trail)
                if (HexGridMath.Distance(t, hex) <= 1)
                    n++;
            return n;
        }
    }

    internal static class ReconAirSortieRegistry
    {
        private static readonly Dictionary<PlayerSetupData, Dictionary<int, ReconAirSortieState>> ByPlayer =
            new Dictionary<PlayerSetupData, Dictionary<int, ReconAirSortieState>>();

        public static void ClearAll() => ByPlayer.Clear();

        public static ReconAirSortieState GetOrCreate(PlayerSetupData player, int armyId, HexCoord launchHex)
        {
            if (player == null)
                return new ReconAirSortieState { LaunchHex = launchHex };
            if (!ByPlayer.TryGetValue(player, out Dictionary<int, ReconAirSortieState> byArmy))
                ByPlayer[player] = byArmy = new Dictionary<int, ReconAirSortieState>();
            if (!byArmy.TryGetValue(armyId, out ReconAirSortieState state))
            {
                state = new ReconAirSortieState { LaunchHex = launchHex };
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

        public static void Retire(PlayerSetupData player, int armyId)
        {
            if (player != null && ByPlayer.TryGetValue(player, out Dictionary<int, ReconAirSortieState> byArmy))
                byArmy.Remove(armyId);
        }
    }
}
